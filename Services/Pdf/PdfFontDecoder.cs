using System.Globalization;
using System.Text;

namespace AIArchitectStudio.Services.Pdf;

/// <summary>Глиф показанной строки: Unicode-текст, ширина в 1/1000 кегля и признак однобайтового пробела (Tw).</summary>
internal readonly record struct PdfGlyph(string Text, double Width, bool IsSpace);

/// <summary>
/// Декодер шрифта PDF: составные шрифты Type0 (Identity-H/V, встроенные CMap с codespacerange/cidrange, массив /W),
/// простые Type1/TrueType/Type3 (WinAnsi, MacRoman, Standard, /Differences с именами глифов Adobe Glyph List, включая
/// кириллицу afii10017…), CMap /ToUnicode (bfchar/bfrange) и обратная таблица cmap встроенного TrueType, если ToUnicode нет.
/// Строки старых CAD-программ в CP1251 без ToUnicode распознаются по сериям байтов 0xC0–0xFF.
/// </summary>
internal sealed class PdfFontDecoder
{
    private static readonly Encoding Cp1252, Cp1251, MacRoman;

    static PdfFontDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252 = Encoding.GetEncoding(1252);
        Cp1251 = Encoding.GetEncoding(1251);
        MacRoman = Encoding.GetEncoding(10000);
    }

    private readonly List<(int Len, uint Lo, uint Hi)> _codespace = [];
    private readonly Dictionary<uint, string> _toUnicode = [];
    private readonly Dictionary<uint, uint> _codeToCid = [];
    private readonly string?[] _differences = new string?[256];
    private readonly Dictionary<uint, double> _cidWidths = [];
    private Encoding _base = Cp1252;
    private bool _forceCyrillic;
    private double[]? _widths;
    private int _firstChar;
    private double _missingWidth;
    private double _defaultCidWidth = 1000;
    private double _widthScale = 1;
    private ushort[]? _cidToGid;
    private Dictionary<ushort, int>? _gidToUnicode;
    private bool _ucs2;

    public bool IsComposite { get; private set; }

    /// <summary>Шрифт сам по себе ничего не говорит о тексте (нет ToUnicode, кодировки и встроенного cmap).</summary>
    public bool IsOpaque { get; private set; }

    public static PdfFontDecoder Create(PdfFile file, PdfDict font)
    {
        var d = new PdfFontDecoder();
        string subtype = file.NameOf(font, "Subtype") ?? "Type1";
        string baseFont = file.NameOf(font, "BaseFont") ?? string.Empty;
        d._forceCyrillic = baseFont.Contains("Cyr", StringComparison.OrdinalIgnoreCase) || baseFont.Contains("1251", StringComparison.Ordinal);

        if (file.Resolve(font.Raw("ToUnicode")) is PdfStream tu) d.ReadCMap(file.Decode(tu), toUnicode: true);

        if (subtype == "Type0")
        {
            d.IsComposite = true;
            var encoding = file.Resolve(font.Raw("Encoding"));
            if (encoding is PdfStream cmap) d.ReadCMap(file.Decode(cmap), toUnicode: false);
            else if (PdfFile.Name(encoding) is { } encName && encName.Contains("UCS2", StringComparison.OrdinalIgnoreCase)) d._ucs2 = true;

            var descendant = file.Array(font.Raw("DescendantFonts")) is { Count: > 0 } df ? file.Dict(df[0]) : null;
            if (descendant is not null)
            {
                d._defaultCidWidth = file.Num(descendant, "DW") ?? 1000;
                d.ReadCidWidths(file, file.Array(descendant.Raw("W")));
                if (file.Resolve(descendant.Raw("CIDToGIDMap")) is PdfStream map)
                {
                    var bytes = file.Decode(map);
                    d._cidToGid = new ushort[bytes.Length / 2];
                    for (int i = 0; i < d._cidToGid.Length; i++) d._cidToGid[i] = (ushort)((bytes[2 * i] << 8) | bytes[2 * i + 1]);
                }
                if (d._toUnicode.Count == 0 && !d._ucs2) d._gidToUnicode = EmbeddedCMap(file, file.Dict(descendant.Raw("FontDescriptor")));
            }
            d.IsOpaque = d._toUnicode.Count == 0 && !d._ucs2 && d._gidToUnicode is null;
            return d;
        }

        // Простой шрифт.
        if (subtype == "Type3" && file.Numbers(font.Raw("FontMatrix")) is { Length: 6 } fm) d._widthScale = fm[0] * 1000;
        d._firstChar = (int)(file.Num(font, "FirstChar") ?? 0);
        d._widths = file.Numbers(font.Raw("Widths"));
        var descriptor = file.Dict(font.Raw("FontDescriptor"));
        d._missingWidth = file.Num(descriptor, "MissingWidth") ?? 0;

        var enc = file.Resolve(font.Raw("Encoding"));
        string? baseEncoding = PdfFile.Name(enc);
        if (enc is PdfDict encDict)
        {
            baseEncoding = file.NameOf(encDict, "BaseEncoding");
            if (file.Array(encDict.Raw("Differences")) is { } diff)
            {
                int code = 0;
                foreach (var item in diff)
                {
                    var v = file.Resolve(item);
                    if (PdfFile.Number(v) is { } n) code = (int)n;
                    else if (v is PdfName name && code is >= 0 and < 256)
                    {
                        d._differences[code] = GlyphNameToUnicode(name.Value);
                        code++;
                    }
                }
            }
        }
        if (baseEncoding == "MacRomanEncoding") d._base = MacRoman;
        return d;
    }

    private static Dictionary<ushort, int>? EmbeddedCMap(PdfFile file, PdfDict? descriptor)
    {
        if (descriptor is null || file.Resolve(descriptor.Raw("FontFile2")) is not PdfStream ff) return null;
        try
        {
            var map = TrueTypeFont.Parse(file.Decode(ff), "embedded").GlyphToCodepoint();
            return map.Count > 0 ? map : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException)
        {
            return null;
        }
    }

    private void ReadCidWidths(PdfFile file, List<object?>? w)
    {
        if (w is null) return;
        for (int i = 0; i < w.Count;)
        {
            if (PdfFile.Number(file.Resolve(w[i])) is not { } first) { i++; continue; }
            if (i + 1 < w.Count && file.Resolve(w[i + 1]) is List<object?> list)
            {
                for (int k = 0; k < list.Count && k < 65536; k++)
                    _cidWidths[(uint)(first + k)] = PdfFile.Number(file.Resolve(list[k])) ?? _defaultCidWidth;
                i += 2;
            }
            else if (i + 2 < w.Count && PdfFile.Number(file.Resolve(w[i + 1])) is { } last && PdfFile.Number(file.Resolve(w[i + 2])) is { } width)
            {
                for (double c = first; c <= last && c - first < 65536; c++) _cidWidths[(uint)c] = width;
                i += 3;
            }
            else i++;
        }
    }

    /// <summary>CMap: ToUnicode (bfchar/bfrange) или кодировка составного шрифта (codespacerange, cidchar/cidrange).</summary>
    private void ReadCMap(byte[] data, bool toUnicode)
    {
        var lexer = new PdfLexer(data);
        var stack = new List<object?>();
        int guard = 0;
        while (guard++ < 2_000_000)
        {
            var t = lexer.ReadObject(refs: false);
            if (t == PdfLexer.EndOfData) break;
            if (t is not PdfKeyword k)
            {
                stack.Add(t);
                continue;
            }
            switch (k.Name)
            {
                case "endcodespacerange":
                    for (int i = 0; i + 1 < stack.Count; i += 2)
                        if (stack[i] is PdfString lo && stack[i + 1] is PdfString hi)
                            _codespace.Add((lo.Bytes.Length, Code(lo.Bytes), Code(hi.Bytes)));
                    break;
                case "endbfchar" when toUnicode:
                    for (int i = 0; i + 1 < stack.Count; i += 2)
                        if (stack[i] is PdfString src)
                        {
                            string? dst = stack[i + 1] switch
                            {
                                PdfString s => Utf16(s.Bytes),
                                PdfName n => GlyphNameToUnicode(n.Value),
                                _ => null
                            };
                            if (dst is not null) _toUnicode[Code(src.Bytes)] = dst;
                        }
                    break;
                case "endbfrange" when toUnicode:
                    for (int i = 0; i + 2 < stack.Count; i += 3)
                    {
                        if (stack[i] is not PdfString lo || stack[i + 1] is not PdfString hi) continue;
                        uint a = Code(lo.Bytes), b = Code(hi.Bytes);
                        if (b < a || b - a > 65535) continue;
                        if (stack[i + 2] is PdfString dst)
                        {
                            var units = dst.Bytes;
                            for (uint add = 0; add <= b - a; add++)
                            {
                                uint c = a + add;
                                var bytes = (byte[])units.Clone();
                                // Приращение последнего UTF-16 кода диапазона.
                                if (bytes.Length >= 2)
                                {
                                    int last = (bytes[^2] << 8 | bytes[^1]) + (int)add;
                                    bytes[^2] = (byte)(last >> 8);
                                    bytes[^1] = (byte)last;
                                }
                                else if (bytes.Length == 1) bytes[0] = (byte)(bytes[0] + add);
                                _toUnicode[c] = Utf16(bytes);
                            }
                        }
                        else if (stack[i + 2] is List<object?> list)
                        {
                            for (int n = 0; n <= b - a && n < list.Count; n++)
                                if (list[n] is PdfString s) _toUnicode[a + (uint)n] = Utf16(s.Bytes);
                        }
                    }
                    break;
                case "endcidchar" when !toUnicode:
                    for (int i = 0; i + 1 < stack.Count; i += 2)
                        if (stack[i] is PdfString src && PdfFile.Number(stack[i + 1]) is { } cid)
                            _codeToCid[Code(src.Bytes)] = (uint)cid;
                    break;
                case "endcidrange" when !toUnicode:
                    for (int i = 0; i + 2 < stack.Count; i += 3)
                        if (stack[i] is PdfString lo && stack[i + 1] is PdfString hi && PdfFile.Number(stack[i + 2]) is { } cid)
                        {
                            uint a = Code(lo.Bytes), b = Code(hi.Bytes);
                            if (b < a || b - a > 65535) continue;
                            for (uint n = 0; n <= b - a; n++) _codeToCid[a + n] = (uint)cid + n;
                        }
                    break;
            }
            stack.Clear();
        }
    }

    private static uint Code(byte[] bytes)
    {
        uint v = 0;
        foreach (byte b in bytes.Take(4)) v = (v << 8) | b;
        return v;
    }

    private static string Utf16(byte[] bytes)
    {
        if (bytes.Length == 1) return ((char)bytes[0]).ToString();
        return Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length & ~1);
    }

    // ───────────────────────────── Декодирование строки ─────────────────────────────

    public List<PdfGlyph> Decode(byte[] bytes)
    {
        var result = new List<PdfGlyph>(bytes.Length);
        if (IsComposite)
        {
            for (int i = 0; i < bytes.Length;)
            {
                int len = CodeLength(bytes, i);
                uint code = 0;
                for (int k = 0; k < len && i + k < bytes.Length; k++) code = (code << 8) | bytes[i + k];
                uint cid = _codeToCid.TryGetValue(code, out var mapped) ? mapped : code;
                string text = _toUnicode.TryGetValue(code, out var u) ? u
                    : _ucs2 ? ((char)code).ToString()
                    : _gidToUnicode is not null && _gidToUnicode.TryGetValue(Gid(cid), out var cp) ? char.ConvertFromUtf32(cp)
                    : "�";
                double width = _cidWidths.TryGetValue(cid, out var w) ? w : _defaultCidWidth;
                result.Add(new PdfGlyph(text, width, len == 1 && code == 32));
                i += len;
            }
            return result;
        }

        // Простой шрифт: кириллица CP1251 без ToUnicode — серия из 2+ байтов 0xC0–0xFF или одиночная буква (марка оси).
        bool cyrillic = _forceCyrillic;
        if (!cyrillic && _toUnicode.Count == 0)
        {
            for (int i = 0; i + 1 < bytes.Length && !cyrillic; i++)
                cyrillic = IsCyrByte(bytes[i]) && IsCyrByte(bytes[i + 1]);
            if (!cyrillic && bytes.Length <= 2 && bytes.Any(b => b >= 0xC0)) cyrillic = true;
            if (cyrillic) _forceCyrillic = true;
        }
        foreach (byte b in bytes)
        {
            string text;
            if (_toUnicode.TryGetValue(b, out var u)) text = u;
            else if (_differences[b] is { } diff) text = diff;
            else if (b >= 0x80 && cyrillic) text = Cp1251.GetString([b]);
            else text = _base.GetString([b]);
            double width = _widths is not null && b - _firstChar >= 0 && b - _firstChar < _widths.Length ? _widths[b - _firstChar]
                : _missingWidth > 0 ? _missingWidth
                : b == 32 ? 250 : 500;
            result.Add(new PdfGlyph(text, width * _widthScale, b == 32));
        }
        return result;
    }

    private static bool IsCyrByte(byte b) => b >= 0xC0 || b is 0xA8 or 0xB8;

    private ushort Gid(uint cid) => _cidToGid is not null ? cid < _cidToGid.Length ? _cidToGid[cid] : (ushort)0 : (ushort)cid;

    private int CodeLength(byte[] bytes, int at)
    {
        if (_codespace.Count == 0) return 2;
        for (int len = 1; len <= 4; len++)
        {
            if (at + len > bytes.Length) break;
            uint code = 0;
            for (int k = 0; k < len; k++) code = (code << 8) | bytes[at + k];
            foreach (var (l, lo, hi) in _codespace)
                if (l == len && code >= lo && code <= hi) return len;
        }
        return _codespace[0].Len;
    }

    // ───────────────────────────── Имена глифов ─────────────────────────────

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["space"] = " ", ["exclam"] = "!", ["quotedbl"] = "\"", ["numbersign"] = "#", ["dollar"] = "$", ["percent"] = "%",
        ["ampersand"] = "&", ["quotesingle"] = "'", ["quoteright"] = "’", ["quoteleft"] = "‘", ["parenleft"] = "(",
        ["parenright"] = ")", ["asterisk"] = "*", ["plus"] = "+", ["comma"] = ",", ["hyphen"] = "-", ["period"] = ".",
        ["slash"] = "/", ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5",
        ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["colon"] = ":", ["semicolon"] = ";", ["less"] = "<",
        ["equal"] = "=", ["greater"] = ">", ["question"] = "?", ["at"] = "@", ["bracketleft"] = "[", ["backslash"] = "\\",
        ["bracketright"] = "]", ["asciicircum"] = "^", ["underscore"] = "_", ["grave"] = "`", ["braceleft"] = "{", ["bar"] = "|",
        ["braceright"] = "}", ["asciitilde"] = "~", ["minus"] = "−", ["plusminus"] = "±", ["multiply"] = "×",
        ["degree"] = "°", ["Oslash"] = "Ø", ["oslash"] = "ø", ["endash"] = "–", ["emdash"] = "—",
        ["bullet"] = "•", ["periodcentered"] = "·", ["quotedblleft"] = "“", ["quotedblright"] = "”",
        ["guillemotleft"] = "«", ["guillemotright"] = "»", ["afii61352"] = "№", ["numero"] = "№",
        ["section"] = "§", ["mu"] = "µ", ["twosuperior"] = "²", ["threesuperior"] = "³", ["Euro"] = "€",
        ["nbspace"] = " ", ["uni00A0"] = " ", ["diameter"] = "⌀", ["afii10023"] = "Ё", ["afii10071"] = "ё"
    };

    public static string? GlyphNameToUnicode(string name)
    {
        if (Names.TryGetValue(name, out var known)) return known;
        int dot = name.IndexOf('.');
        if (dot > 0) return GlyphNameToUnicode(name[..dot]);
        if (name.Contains('_')) return string.Concat(name.Split('_').Select(p => GlyphNameToUnicode(p) ?? string.Empty));
        if (name.Length == 1 && char.IsAsciiLetter(name[0])) return name;
        if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7 && (name.Length - 3) % 4 == 0)
        {
            var sb = new StringBuilder();
            for (int i = 3; i + 4 <= name.Length; i += 4)
                if (int.TryParse(name.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u)) sb.Append((char)u);
                else return null;
            return sb.ToString();
        }
        if (name.Length is >= 5 and <= 7 && name[0] == 'u' && int.TryParse(name.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp) && cp <= 0x10FFFF)
            return char.ConvertFromUtf32(cp);
        if (name.StartsWith("afii", StringComparison.Ordinal) && int.TryParse(name.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int afii))
        {
            // Кириллица Adobe Glyph List: afii10017…10049 — А…Я (Ё отдельно), afii10065…10097 — а…я (ё отдельно).
            if (afii is >= 10017 and <= 10022) return ((char)(0x0410 + afii - 10017)).ToString();
            if (afii is >= 10024 and <= 10049) return ((char)(0x0416 + afii - 10024)).ToString();
            if (afii is >= 10065 and <= 10070) return ((char)(0x0430 + afii - 10065)).ToString();
            if (afii is >= 10072 and <= 10097) return ((char)(0x0436 + afii - 10072)).ToString();
        }
        return null;
    }
}
