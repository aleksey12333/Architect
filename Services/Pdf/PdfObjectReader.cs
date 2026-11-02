using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace AIArchitectStudio.Services.Pdf;

/// <summary>Имя PDF (/Name).</summary>
internal sealed record PdfName(string Value)
{
    public override string ToString() => "/" + Value;
}

/// <summary>Косвенная ссылка «n g R».</summary>
internal readonly record struct PdfRef(int Num, int Gen);

/// <summary>Строка PDF (литеральная или шестнадцатеричная) — сырые байты, кодировку задаёт шрифт.</summary>
internal sealed class PdfString(byte[] bytes)
{
    public byte[] Bytes { get; } = bytes;
    public string Latin1 => Encoding.Latin1.GetString(Bytes);
}

/// <summary>Ключевое слово: оператор потока содержимого или служебное слово синтаксиса (R, obj, stream…).</summary>
internal sealed record PdfKeyword(string Name);

internal sealed class PdfDict : Dictionary<string, object?>
{
    public PdfDict() : base(StringComparer.Ordinal) { }

    public object? Raw(string key) => TryGetValue(key, out var v) ? v : null;
}

internal sealed class PdfStream(PdfDict dict, byte[] raw)
{
    public PdfDict Dict { get; } = dict;
    public byte[] Raw { get; } = raw;
}

/// <summary>
/// Лексер синтаксиса PDF (объекты файла и потоки содержимого): числа, имена с #-экранированием, литеральные строки
/// со вложенными скобками и восьмеричными кодами, шестнадцатеричные строки, массивы, словари, ключевые слова, комментарии.
/// </summary>
internal sealed class PdfLexer(byte[] data, int start = 0, int end = -1)
{
    private static readonly object ArrayStart = new(), ArrayEnd = new(), DictStart = new(), DictEnd = new(), Junk = new();
    public static readonly object EndOfData = new();

    private readonly byte[] _d = data;
    private readonly int _end = end < 0 ? data.Length : Math.Min(end, data.Length);
    public int Pos = start;

    public int End => _end;

    public static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public void SkipWhite()
    {
        while (Pos < _end)
        {
            byte b = _d[Pos];
            if (IsWhite(b)) { Pos++; continue; }
            if (b == '%')
            {
                while (Pos < _end && _d[Pos] != '\n' && _d[Pos] != '\r') Pos++;
                continue;
            }
            break;
        }
    }

    /// <summary>Следующий объект: массивы и словари собираются целиком; refs — распознавать «n g R».</summary>
    public object? ReadObject(bool refs, int depth = 0)
    {
        var token = ReadToken();
        if (depth > 64) return token;
        if (token == ArrayStart)
        {
            var list = new List<object?>();
            while (true)
            {
                int save = Pos;
                var item = ReadObject(refs, depth + 1);
                if (item == ArrayEnd || item == EndOfData) break;
                if (item == DictEnd || item == Junk) { if (Pos == save) Pos++; continue; }
                list.Add(item);
            }
            return list;
        }
        if (token == DictStart)
        {
            var dict = new PdfDict();
            while (true)
            {
                var key = ReadObject(refs, depth + 1);
                if (key == DictEnd || key == EndOfData) break;
                if (key is not PdfName name) continue;
                var value = ReadObject(refs, depth + 1);
                if (value == DictEnd) { dict[name.Value] = null; break; }
                if (value == EndOfData) break;
                dict[name.Value] = value;
            }
            return dict;
        }
        if (refs && token is double num && num >= 0 && num == Math.Floor(num))
        {
            int save = Pos;
            if (ReadToken() is double gen && gen >= 0 && gen == Math.Floor(gen) && ReadToken() is PdfKeyword { Name: "R" })
                return new PdfRef((int)num, (int)gen);
            Pos = save;
        }
        return token;
    }

    /// <summary>Одиночный токен без сборки массивов и словарей.</summary>
    public object? ReadToken()
    {
        SkipWhite();
        if (Pos >= _end) return EndOfData;
        byte c = _d[Pos];
        switch (c)
        {
            case (byte)'/':
                return ReadName();
            case (byte)'(':
                return ReadLiteral();
            case (byte)'<':
                if (Pos + 1 < _end && _d[Pos + 1] == '<') { Pos += 2; return DictStart; }
                return ReadHex();
            case (byte)'>':
                if (Pos + 1 < _end && _d[Pos + 1] == '>') { Pos += 2; return DictEnd; }
                Pos++;
                return Junk;
            case (byte)'[':
                Pos++;
                return ArrayStart;
            case (byte)']':
                Pos++;
                return ArrayEnd;
            case (byte)'{' or (byte)'}' or (byte)')':
                Pos++;
                return Junk;
        }
        if (c is >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'-' or (byte)'.')
            return ReadNumber();

        int start = Pos;
        while (Pos < _end && !IsWhite(_d[Pos]) && !IsDelimiter(_d[Pos])) Pos++;
        if (Pos == start) { Pos++; return Junk; }
        string word = Encoding.Latin1.GetString(_d, start, Pos - start);
        return word switch
        {
            "true" => true,
            "false" => false,
            "null" => null,
            _ => new PdfKeyword(word)
        };
    }

    private object ReadNumber()
    {
        int start = Pos;
        Pos++;
        while (Pos < _end && (_d[Pos] is >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'-' or (byte)'+')) Pos++;
        var s = Encoding.Latin1.GetString(_d, start, Pos - start);
        // Неверные числа вида «--5» или «1.2.3» трактуются по первой корректной части.
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        var m = Regex.Match(s, @"[+-]?(\d+\.?\d*|\.\d+)");
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0.0;
    }

    private PdfName ReadName()
    {
        Pos++;
        var bytes = new List<byte>();
        while (Pos < _end && !IsWhite(_d[Pos]) && !IsDelimiter(_d[Pos]))
        {
            byte b = _d[Pos++];
            if (b == '#' && Pos + 1 < _end && Hex(_d[Pos]) >= 0 && Hex(_d[Pos + 1]) >= 0)
            {
                b = (byte)(Hex(_d[Pos]) * 16 + Hex(_d[Pos + 1]));
                Pos += 2;
            }
            bytes.Add(b);
        }
        return new PdfName(Encoding.Latin1.GetString(bytes.ToArray()));
    }

    private PdfString ReadLiteral()
    {
        Pos++;
        var bytes = new List<byte>();
        int depth = 1;
        while (Pos < _end)
        {
            byte b = _d[Pos++];
            if (b == '\\')
            {
                if (Pos >= _end) break;
                byte e = _d[Pos++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'\r':
                        if (Pos < _end && _d[Pos] == '\n') Pos++;
                        break;
                    case (byte)'\n':
                        break;
                    case >= (byte)'0' and <= (byte)'7':
                    {
                        int v = e - '0';
                        for (int k = 0; k < 2 && Pos < _end && _d[Pos] is >= (byte)'0' and <= (byte)'7'; k++) v = v * 8 + (_d[Pos++] - '0');
                        bytes.Add((byte)v);
                        break;
                    }
                    default: bytes.Add(e); break;
                }
                continue;
            }
            if (b == '(') depth++;
            else if (b == ')' && --depth == 0) break;
            bytes.Add(b);
        }
        return new PdfString(bytes.ToArray());
    }

    private PdfString ReadHex()
    {
        Pos++;
        var bytes = new List<byte>();
        int hi = -1;
        while (Pos < _end)
        {
            byte b = _d[Pos++];
            if (b == '>') break;
            int h = Hex(b);
            if (h < 0) continue;
            if (hi < 0) hi = h;
            else { bytes.Add((byte)(hi * 16 + h)); hi = -1; }
        }
        if (hi >= 0) bytes.Add((byte)(hi * 16));
        return new PdfString(bytes.ToArray());
    }

    public static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1
    };

    /// <summary>Пропуск данных встроенного изображения (BI … ID данные EI).</summary>
    public void SkipInlineImageData()
    {
        if (Pos < _end && IsWhite(_d[Pos])) Pos++;
        while (Pos + 1 < _end)
        {
            if (_d[Pos] == 'E' && _d[Pos + 1] == 'I' && (Pos == 0 || IsWhite(_d[Pos - 1])) && (Pos + 2 >= _end || IsWhite(_d[Pos + 2]) || IsDelimiter(_d[Pos + 2])))
            {
                Pos += 2;
                return;
            }
            Pos++;
        }
        Pos = _end;
    }
}

/// <summary>
/// Читатель файла PDF без внешних библиотек: объекты находятся сканированием «n g obj» (устойчиво к повреждённым и
/// инкрементальным xref), объектные потоки /ObjStm распаковываются, фильтры FlateDecode / LZW / ASCIIHex / ASCII85 /
/// RunLength с PNG/TIFF-предикторами. Дерево страниц — с наследованием /Resources, /MediaBox и /Rotate.
/// </summary>
internal sealed class PdfFile
{
    private static readonly Regex ObjHeader = new(@"(?<![0-9])(\d{1,7})\s+(\d{1,5})\s+obj\b", RegexOptions.Compiled);

    private readonly byte[] _data;
    private readonly Dictionary<int, int> _offsets = [];
    private readonly Dictionary<int, (int Stream, int Index)> _compressed = [];
    private readonly Dictionary<int, object?> _cache = [];
    private readonly Dictionary<int, (byte[] Data, int First, List<int> Offsets)> _objStreams = [];
    private readonly HashSet<int> _loading = [];

    public bool Encrypted { get; private set; }
    public PdfDict? Trailer { get; private set; }

    public PdfFile(byte[] data)
    {
        _data = data;
        Scan();
    }

    private void Scan()
    {
        string text = Encoding.Latin1.GetString(_data);
        foreach (Match m in ObjHeader.Matches(text))
            _offsets[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = m.Index + m.Length;

        // Классические трейлеры.
        int at = 0;
        while ((at = text.IndexOf("trailer", at, StringComparison.Ordinal)) >= 0)
        {
            var lexer = new PdfLexer(_data, at + 7);
            if (lexer.ReadObject(refs: true) is PdfDict dict)
            {
                if (dict.ContainsKey("Encrypt")) Encrypted = true;
                if (dict.ContainsKey("Root")) Trailer = dict;
            }
            at += 7;
        }

        // Потоки перекрёстных ссылок (/XRef) и объектные потоки (/ObjStm).
        foreach (var (num, offset) in _offsets.ToList())
        {
            int probe = Math.Min(text.Length - offset, 600);
            if (probe <= 0) continue;
            string head = text.Substring(offset, probe);
            bool xref = head.Contains("/XRef", StringComparison.Ordinal);
            bool objStm = head.Contains("/ObjStm", StringComparison.Ordinal);
            if (!xref && !objStm) continue;
            if (Get(num) is not PdfStream s) continue;
            if (xref)
            {
                if (s.Dict.ContainsKey("Encrypt")) Encrypted = true;
                if (s.Dict.ContainsKey("Root") && Trailer is null) Trailer = s.Dict;
            }
            if (objStm && Name(s.Dict.Raw("Type")) == "ObjStm") IndexObjectStream(num, s);
        }
        if (Trailer is null || !Trailer.ContainsKey("Root"))
            foreach (Match m in Regex.Matches(text, @"/Root\s+(\d+)\s+(\d+)\s+R"))
            {
                Trailer = new PdfDict { ["Root"] = new PdfRef(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0) };
            }
    }

    private void IndexObjectStream(int num, PdfStream s)
    {
        try
        {
            var data = Decode(s);
            int n = (int)(Number(Resolve(s.Dict.Raw("N"))) ?? 0);
            int first = (int)(Number(Resolve(s.Dict.Raw("First"))) ?? 0);
            var lexer = new PdfLexer(data, 0, Math.Min(first, data.Length));
            var offsets = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (lexer.ReadToken() is not double objNum || lexer.ReadToken() is not double off) break;
                offsets.Add((int)off);
                if (!_offsets.ContainsKey((int)objNum)) _compressed[(int)objNum] = (num, offsets.Count - 1);
            }
            _objStreams[num] = (data, first, offsets);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // Повреждённый объектный поток — объекты из него недоступны.
        }
    }

    public IEnumerable<int> ObjectNumbers => _offsets.Keys.Concat(_compressed.Keys).Distinct().Order();

    public object? Resolve(object? o, int depth = 0)
    {
        while (o is PdfRef r && depth++ < 32) o = Get(r.Num);
        return o;
    }

    public object? Get(int num)
    {
        if (_cache.TryGetValue(num, out var cached)) return cached;
        if (!_loading.Add(num)) return null;
        object? result = null;
        try
        {
            if (_offsets.TryGetValue(num, out int offset)) result = ParseAt(offset);
            else if (_compressed.TryGetValue(num, out var c) && _objStreams.TryGetValue(c.Stream, out var os) && c.Index < os.Offsets.Count)
                result = new PdfLexer(os.Data, os.First + os.Offsets[c.Index]).ReadObject(refs: true);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            result = null;
        }
        finally
        {
            _loading.Remove(num);
        }
        _cache[num] = result;
        return result;
    }

    private object? ParseAt(int offset)
    {
        var lexer = new PdfLexer(_data, offset);
        var obj = lexer.ReadObject(refs: true);
        if (obj is not PdfDict dict) return obj;
        int save = lexer.Pos;
        if (lexer.ReadToken() is not PdfKeyword { Name: "stream" })
        {
            lexer.Pos = save;
            return dict;
        }
        int p = lexer.Pos;
        if (p < _data.Length && _data[p] == '\r') p++;
        if (p < _data.Length && _data[p] == '\n') p++;
        int length = -1;
        if (Number(Resolve(dict.Raw("Length"))) is { } len && len >= 0 && p + len <= _data.Length)
        {
            int after = (int)(p + len);
            int q = after;
            while (q < _data.Length && PdfLexer.IsWhite(_data[q])) q++;
            if (StartsWith(q, "endstream")) length = (int)len;
        }
        if (length < 0)
        {
            int endAt = IndexOf("endstream", p);
            if (endAt < 0) endAt = _data.Length;
            int e = endAt;
            if (e > p && _data[e - 1] == '\n') e--;
            if (e > p && _data[e - 1] == '\r') e--;
            length = Math.Max(0, e - p);
        }
        return new PdfStream(dict, _data.AsSpan(p, length).ToArray());
    }

    private bool StartsWith(int at, string s)
    {
        if (at + s.Length > _data.Length) return false;
        for (int i = 0; i < s.Length; i++)
            if (_data[at + i] != s[i]) return false;
        return true;
    }

    private int IndexOf(string s, int from)
    {
        var pattern = Encoding.ASCII.GetBytes(s);
        int idx = _data.AsSpan(from).IndexOf(pattern);
        return idx < 0 ? -1 : from + idx;
    }

    // ───────────────────────────── Доступ к значениям ─────────────────────────────

    public static string? Name(object? o) => o is PdfName n ? n.Value : null;

    public static double? Number(object? o) => o switch
    {
        double d => d,
        int i => i,
        _ => null
    };

    public PdfDict? Dict(object? o) => Resolve(o) switch
    {
        PdfDict d => d,
        PdfStream s => s.Dict,
        _ => null
    };

    public List<object?>? Array(object? o) => Resolve(o) as List<object?>;

    public double? Num(PdfDict? d, string key) => d is null ? null : Number(Resolve(d.Raw(key)));

    public string? NameOf(PdfDict? d, string key) => d is null ? null : Name(Resolve(d.Raw(key)));

    public double[]? Numbers(object? o) => Array(o)?.Select(x => Number(Resolve(x)) ?? 0).ToArray();

    // ───────────────────────────── Страницы ─────────────────────────────

    public sealed record PageInfo(PdfDict Dict, PdfDict? Resources, double[] MediaBox, int Rotate);

    public List<PageInfo> Pages()
    {
        var result = new List<PageInfo>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var root = Dict(Trailer?.Raw("Root"));
        root ??= ObjectNumbers.Select(Get).Select(Dict).FirstOrDefault(d => NameOf(d, "Type") == "Catalog");
        if (root is not null && Dict(root.Raw("Pages")) is { } tree) Walk(tree, null, null, null, 0);

        if (result.Count == 0)
        {
            // Повреждённое дерево страниц — все объекты /Type /Page в порядке номеров.
            foreach (int num in ObjectNumbers)
                if (Dict(Get(num)) is { } d && NameOf(d, "Type") == "Page")
                    result.Add(new PageInfo(d, Dict(d.Raw("Resources")), Numbers(d.Raw("MediaBox")) ?? [0, 0, 595, 842], (int)(Num(d, "Rotate") ?? 0)));
        }
        return result;

        void Walk(PdfDict node, PdfDict? resources, double[]? box, int? rotate, int depth)
        {
            if (depth > 64 || !visited.Add(node)) return;
            resources = Dict(node.Raw("Resources")) ?? resources;
            box = Numbers(node.Raw("MediaBox")) is { Length: 4 } b ? b : box;
            rotate = Num(node, "Rotate") is { } r ? (int)r : rotate;
            if (Array(node.Raw("Kids")) is { } kids && NameOf(node, "Type") != "Page")
            {
                foreach (var kid in kids)
                    if (Dict(kid) is { } k) Walk(k, resources, box, rotate, depth + 1);
                return;
            }
            result.Add(new PageInfo(node, resources, box ?? [0, 0, 595, 842], ((rotate ?? 0) % 360 + 360) % 360));
        }
    }

    // ───────────────────────────── Фильтры ─────────────────────────────

    public byte[] Decode(PdfStream s)
    {
        var data = s.Raw;
        var filters = Resolve(s.Dict.Raw("Filter")) switch
        {
            PdfName n => [n.Value],
            List<object?> list => list.Select(x => Name(Resolve(x)) ?? string.Empty).ToList(),
            _ => new List<string>()
        };
        var parms = Resolve(s.Dict.Raw("DecodeParms") ?? s.Dict.Raw("DP"));
        for (int i = 0; i < filters.Count; i++)
        {
            var p = parms is List<object?> pl ? Dict(i < pl.Count ? pl[i] : null) : Dict(parms);
            data = filters[i] switch
            {
                "FlateDecode" or "Fl" => Predict(Inflate(data), p),
                "LZWDecode" or "LZW" => Predict(Lzw(data, (int)(Num(p, "EarlyChange") ?? 1)), p),
                "ASCIIHexDecode" or "AHx" => AsciiHex(data),
                "ASCII85Decode" or "A85" => Ascii85(data),
                "RunLengthDecode" or "RL" => RunLength(data),
                _ => data
            };
        }
        return data;
    }

    /// <summary>zlib с заголовком; при его отсутствии — «сырой» deflate; при обрыве — то, что успело распаковаться.</summary>
    public static byte[] Inflate(byte[] data)
    {
        if (data.Length == 0) return data;
        var output = new MemoryStream();
        if (TryInflate(new ZLibStream(new MemoryStream(data), CompressionMode.Decompress), output)) return output.ToArray();
        var partial = output.ToArray();
        output = new MemoryStream();
        int skip = data.Length > 2 && (data[0] & 0x0F) == 8 ? 2 : 0;
        TryInflate(new DeflateStream(new MemoryStream(data, skip, data.Length - skip), CompressionMode.Decompress), output);
        return output.Length >= partial.Length ? output.ToArray() : partial;
    }

    private static bool TryInflate(Stream z, MemoryStream output)
    {
        var buffer = new byte[65536];
        try
        {
            using (z)
            {
                int n;
                while ((n = z.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
            }
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private byte[] Predict(byte[] data, PdfDict? p)
    {
        int predictor = (int)(Num(p, "Predictor") ?? 1);
        if (predictor < 2) return data;
        int colors = (int)(Num(p, "Colors") ?? 1), bpc = (int)(Num(p, "BitsPerComponent") ?? 8), columns = (int)(Num(p, "Columns") ?? 1);
        int bpp = Math.Max(1, colors * bpc / 8);
        int rowLen = (colors * bpc * columns + 7) / 8;
        if (rowLen <= 0) return data;
        if (predictor == 2)
        {
            var copy = (byte[])data.Clone();
            for (int r = 0; r + rowLen <= copy.Length; r += rowLen)
                for (int i = bpp; i < rowLen; i++) copy[r + i] = (byte)(copy[r + i] + copy[r + i - bpp]);
            return copy;
        }
        var result = new List<byte>(data.Length);
        var prev = new byte[rowLen];
        for (int at = 0; at + 1 + rowLen <= data.Length; at += rowLen + 1)
        {
            int type = data[at];
            var row = new byte[rowLen];
            for (int i = 0; i < rowLen; i++)
            {
                int raw = data[at + 1 + i];
                int left = i >= bpp ? row[i - bpp] : 0, up = prev[i], upLeft = i >= bpp ? prev[i - bpp] : 0;
                row[i] = type switch
                {
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + (left + up) / 2),
                    4 => (byte)(raw + Paeth(left, up, upLeft)),
                    _ => (byte)raw
                };
            }
            result.AddRange(row);
            prev = row;
        }
        return result.ToArray();

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    private static byte[] AsciiHex(byte[] data)
    {
        var result = new List<byte>(data.Length / 2);
        int hi = -1;
        foreach (byte b in data)
        {
            if (b == '>') break;
            int h = PdfLexer.Hex(b);
            if (h < 0) continue;
            if (hi < 0) hi = h;
            else { result.Add((byte)(hi * 16 + h)); hi = -1; }
        }
        if (hi >= 0) result.Add((byte)(hi * 16));
        return result.ToArray();
    }

    private static byte[] Ascii85(byte[] data)
    {
        var result = new List<byte>(data.Length);
        var group = new int[5];
        int n = 0;
        int i = 0;
        if (data.Length > 1 && data[0] == '<' && data[1] == '~') i = 2;
        for (; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == '~') break;
            if (PdfLexer.IsWhite(b)) continue;
            if (b == 'z' && n == 0) { result.AddRange([0, 0, 0, 0]); continue; }
            if (b is < (byte)'!' or > (byte)'u') continue;
            group[n++] = b - '!';
            if (n == 5)
            {
                long v = 0;
                for (int k = 0; k < 5; k++) v = v * 85 + group[k];
                result.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
                n = 0;
            }
        }
        if (n > 1)
        {
            for (int k = n; k < 5; k++) group[k] = 84;
            long v = 0;
            for (int k = 0; k < 5; k++) v = v * 85 + group[k];
            var bytes = new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            result.AddRange(bytes.Take(n - 1));
        }
        return result.ToArray();
    }

    private static byte[] RunLength(byte[] data)
    {
        var result = new List<byte>(data.Length * 2);
        int i = 0;
        while (i < data.Length)
        {
            int len = data[i++];
            if (len == 128) break;
            if (len < 128)
            {
                int count = Math.Min(len + 1, data.Length - i);
                result.AddRange(data.AsSpan(i, count).ToArray());
                i += count;
            }
            else if (i < data.Length)
            {
                result.AddRange(Enumerable.Repeat(data[i++], 257 - len));
            }
        }
        return result.ToArray();
    }

    private static byte[] Lzw(byte[] data, int earlyChange)
    {
        var result = new List<byte>(data.Length * 3);
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (int k = 0; k < 256; k++) table.Add([(byte)k]);
            table.Add([]);
            table.Add([]);
        }
        Reset();
        int codeLen = 9;
        long bitBuf = 0;
        int bits = 0;
        byte[]? prev = null;
        foreach (byte b in data)
        {
            bitBuf = (bitBuf << 8) | b;
            bits += 8;
            while (bits >= codeLen)
            {
                int code = (int)((bitBuf >> (bits - codeLen)) & ((1 << codeLen) - 1));
                bits -= codeLen;
                if (code == 256) { Reset(); codeLen = 9; prev = null; continue; }
                if (code == 257) return result.ToArray();
                byte[] entry;
                if (code < table.Count) entry = table[code];
                else if (prev is not null) entry = [.. prev, prev[0]];
                else return result.ToArray();
                result.AddRange(entry);
                if (prev is not null && table.Count < 4096) table.Add([.. prev, entry[0]]);
                prev = entry;
                int limit = table.Count + earlyChange;
                codeLen = limit >= 2048 ? 12 : limit >= 1024 ? 11 : limit >= 512 ? 10 : 9;
            }
        }
        return result.ToArray();
    }
}
