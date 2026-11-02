using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace AIArchitectStudio.Services.Pdf;

/// <summary>
/// Системный TrueType-шрифт для встраивания в PDF (CIDFontType2 + Identity-H): таблицы cmap (формат 4/12), hmtx, head,
/// hhea, OS/2 и name. В PDF встраивается подмножество использованных глифов (<see cref="TrueTypeSubsetter"/>, FontFile2) —
/// кириллица отображается в любом просмотрщике, а файл листа меньше в 8–10 раз, чем с полным шрифтом.
/// </summary>
public sealed class TrueTypeFont
{
    public required byte[] Data { get; init; }
    public required string PostScriptName { get; init; }
    public required string FilePath { get; init; }
    public int UnitsPerEm { get; private init; }
    public int Ascender { get; private init; }
    public int Descender { get; private init; }
    public int CapHeight { get; private init; }
    public (int XMin, int YMin, int XMax, int YMax) BBox { get; private init; }
    private Dictionary<int, ushort> CMap { get; init; } = [];
    private ushort[] Advances { get; init; } = [];

    /// <summary>Доля высоты прописной буквы от кегля (для задания высоты шрифта по ГОСТ 2.304 в мм).</summary>
    public double CapRatio => CapHeight > 0 ? (double)CapHeight / UnitsPerEm : 0.716;

    /// <summary>Первый доступный шрифт из списка (системные и пользовательские папки шрифтов).</summary>
    public static TrueTypeFont LoadSystem(params string[] fileNames)
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts")
        };
        foreach (var name in fileNames)
            foreach (var dir in dirs)
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                try
                {
                    return Parse(File.ReadAllBytes(path), path);
                }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
                {
                    // Шрифт с CFF-контурами или повреждённый — пробуем следующий.
                }
            }
        throw new FileNotFoundException("Не найден системный TrueType-шрифт с кириллицей (ISOCPEUR, Arial, Segoe UI).");
    }

    public static TrueTypeFont Parse(byte[] data, string path)
    {
        var tables = new Dictionary<string, (int Offset, int Length)>();
        int numTables = U16(data, 4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string tag = Encoding.ASCII.GetString(data, rec, 4);
            tables[tag] = ((int)U32(data, rec + 8), (int)U32(data, rec + 12));
        }
        if (!tables.ContainsKey("glyf") || tables.ContainsKey("CFF ")) throw new InvalidDataException("Шрифт без TrueType-контуров.");

        var head = tables["head"].Offset;
        var hhea = tables["hhea"].Offset;
        int unitsPerEm = U16(data, head + 18);
        var bbox = (S16(data, head + 36), S16(data, head + 38), S16(data, head + 40), S16(data, head + 42));
        int ascender = S16(data, hhea + 4), descender = S16(data, hhea + 6);
        int numberOfHMetrics = U16(data, hhea + 34);
        int numGlyphs = U16(data, tables["maxp"].Offset + 4);

        var advances = new ushort[numGlyphs];
        int hmtx = tables["hmtx"].Offset;
        ushort last = 0;
        for (int g = 0; g < numGlyphs; g++)
        {
            if (g < numberOfHMetrics) last = (ushort)U16(data, hmtx + g * 4);
            advances[g] = last;
        }

        int capHeight = (int)(unitsPerEm * 0.716);
        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 90 && U16(data, os2.Offset) >= 2)
            capHeight = S16(data, os2.Offset + 88);

        return new TrueTypeFont
        {
            Data = data,
            FilePath = path,
            PostScriptName = ReadName(data, tables) ?? Path.GetFileNameWithoutExtension(path).Replace(' ', '-'),
            UnitsPerEm = unitsPerEm,
            Ascender = ascender,
            Descender = descender,
            CapHeight = capHeight,
            BBox = bbox,
            CMap = ReadCMap(data, tables["cmap"].Offset),
            Advances = advances
        };
    }

    public ushort Glyph(int codepoint) => CMap.TryGetValue(codepoint, out var g) ? g : (ushort)0;

    public int Advance(ushort glyph) => glyph < Advances.Length ? Advances[glyph] : 0;

    /// <summary>Обратная таблица cmap «глиф → кодовая точка» (текст PDF-шрифтов без ToUnicode).</summary>
    public Dictionary<ushort, int> GlyphToCodepoint()
    {
        var map = new Dictionary<ushort, int>();
        foreach (var (cp, glyph) in CMap.OrderBy(kv => kv.Key)) map.TryAdd(glyph, cp);
        return map;
    }

    /// <summary>Ширина строки при кегле size (в тех же единицах, что и size).</summary>
    public double Width(string text, double size)
    {
        double sum = 0;
        foreach (var rune in text.EnumerateRunes()) sum += Advance(Glyph(rune.Value));
        return sum * size / UnitsPerEm;
    }

    private static string? ReadName(byte[] d, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("name", out var name)) return null;
        int count = U16(d, name.Offset + 2), storage = name.Offset + U16(d, name.Offset + 4);
        for (int i = 0; i < count; i++)
        {
            int rec = name.Offset + 6 + i * 12;
            int platform = U16(d, rec), nameId = U16(d, rec + 6), length = U16(d, rec + 8), offset = U16(d, rec + 10);
            if (nameId != 6) continue;
            var raw = d.AsSpan(storage + offset, length);
            string value = platform == 3 ? Encoding.BigEndianUnicode.GetString(raw) : Encoding.ASCII.GetString(raw);
            var clean = new string(value.Where(c => c is > ' ' and < '\x7F' and not ('(' or ')' or '/' or '[' or ']' or '<' or '>' or '{' or '}' or '%')).ToArray());
            if (clean.Length > 0) return clean;
        }
        return null;
    }

    private static Dictionary<int, ushort> ReadCMap(byte[] d, int cmap)
    {
        var map = new Dictionary<int, ushort>();
        int count = U16(d, cmap + 2);
        int best = -1, bestRank = -1;
        for (int i = 0; i < count; i++)
        {
            int rec = cmap + 4 + i * 8;
            int platform = U16(d, rec), encoding = U16(d, rec + 2);
            int offset = cmap + (int)U32(d, rec + 4);
            int format = U16(d, offset);
            int rank = (platform, encoding, format) switch
            {
                (3, 10, 12) => 3,
                (0, _, 12) => 3,
                (3, 1, 4) => 2,
                (0, _, 4) => 1,
                _ => -1
            };
            if (rank > bestRank) { bestRank = rank; best = offset; }
        }
        if (best < 0) throw new InvalidDataException("В шрифте нет Unicode-таблицы cmap.");

        if (U16(d, best) == 12)
        {
            long groups = U32(d, best + 12);
            for (long g = 0; g < groups; g++)
            {
                int rec = best + 16 + (int)g * 12;
                long start = U32(d, rec), end = U32(d, rec + 4), glyph = U32(d, rec + 8);
                for (long c = start; c <= end && c <= 0x10FFFF; c++) map[(int)c] = (ushort)(glyph + c - start);
            }
            return map;
        }

        int segX2 = U16(d, best + 6), segCount = segX2 / 2;
        int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
        for (int s = 0; s < segCount; s++)
        {
            int end = U16(d, ends + s * 2), start = U16(d, starts + s * 2);
            int delta = S16(d, deltas + s * 2), rangeOffset = U16(d, ranges + s * 2);
            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (rangeOffset == 0) glyph = (c + delta) & 0xFFFF;
                else
                {
                    int at = ranges + s * 2 + rangeOffset + (c - start) * 2;
                    glyph = U16(d, at);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                if (glyph != 0) map[c] = (ushort)glyph;
            }
        }
        return map;
    }

    private static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static short S16(byte[] d, int o) => (short)((d[o] << 8) | d[o + 1]);
    private static long U32(byte[] d, int o) => ((long)d[o] << 24) | ((long)d[o + 1] << 16) | ((long)d[o + 2] << 8) | d[o + 3];
}

/// <summary>
/// Итог записи PDF: размер файла, страниц, сжатый поток шрифта (FontFile2), размер подмножества и исходного файла шрифта,
/// число встроенных глифов и имя шрифта в PDF (с тегом подмножества).
/// </summary>
public sealed record PdfSaveInfo(long Bytes, int Pages, int FontStreamBytes, int FontBytes, int FullFontBytes, int Glyphs, string BaseFont)
{
    /// <summary>Во сколько раз шрифт-подмножество меньше исходного файла шрифта.</summary>
    public double FontReduction => FontBytes > 0 ? (double)FullFontBytes / FontBytes : 1;
}

public enum PdfTextAlign
{
    Left,
    Center,
    Right
}

/// <summary>
/// Страница векторного PDF в миллиметрах (начало — левый нижний угол листа): линии заданной толщины, штриховые и
/// штрихпунктирные типы линий, заливки, штриховка с отсечением по контуру и текст с внедрённым шрифтом.
/// </summary>
public sealed class PdfVectorPage
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const double PtPerMm = 72.0 / 25.4;

    private readonly StringBuilder _ops = new();
    private readonly PdfVectorDocument _doc;

    public double WidthMm { get; }
    public double HeightMm { get; }

    internal PdfVectorPage(PdfVectorDocument doc, double widthMm, double heightMm)
    {
        _doc = doc;
        WidthMm = widthMm;
        HeightMm = heightMm;
        // Все дальнейшие координаты — в мм.
        _ops.Append(F(PtPerMm)).Append(" 0 0 ").Append(F(PtPerMm)).Append(" 0 0 cm\n1 J 1 j\n");
    }

    internal string Content => _ops.ToString();

    public static string F(double v)
    {
        if (!double.IsFinite(v)) v = 0;
        var s = Math.Round(v, 4).ToString("0.####", Inv);
        return s == "-0" ? "0" : s;
    }

    public void Save() => _ops.Append("q\n");

    public void Restore() => _ops.Append("Q\n");

    /// <summary>Толщина линии (мм) по ГОСТ 2.303: основная 0.5–0.7, тонкая 0.18.</summary>
    public void LineWidth(double mm) => _ops.Append(F(mm)).Append(" w\n");

    /// <summary>Штрихи и пробелы, мм; без аргументов — сплошная.</summary>
    public void Dash(params double[] pattern) =>
        _ops.Append('[').Append(string.Join(' ', pattern.Select(F))).Append("] 0 d\n");

    public void StrokeGray(double g) => _ops.Append(F(g)).Append(" G\n");

    public void FillGray(double g) => _ops.Append(F(g)).Append(" g\n");

    public void Line(double x1, double y1, double x2, double y2) =>
        _ops.Append(F(x1)).Append(' ').Append(F(y1)).Append(" m ").Append(F(x2)).Append(' ').Append(F(y2)).Append(" l S\n");

    public void Path(IReadOnlyList<(double X, double Y)> pts, bool close)
    {
        if (pts.Count < 2) return;
        _ops.Append(F(pts[0].X)).Append(' ').Append(F(pts[0].Y)).Append(" m");
        for (int i = 1; i < pts.Count; i++) _ops.Append(' ').Append(F(pts[i].X)).Append(' ').Append(F(pts[i].Y)).Append(" l");
        if (close) _ops.Append(" h");
        _ops.Append('\n');
    }

    public void Polyline(IReadOnlyList<(double X, double Y)> pts, bool closed)
    {
        Path(pts, closed);
        _ops.Append("S\n");
    }

    /// <summary>Многоугольник: fill — заливка текущим серым, stroke — контур.</summary>
    public void Polygon(IReadOnlyList<(double X, double Y)> pts, bool fill, bool stroke)
    {
        if (pts.Count < 3) return;
        Path(pts, true);
        _ops.Append(fill && stroke ? "B\n" : fill ? "f\n" : "S\n");
    }

    public void Rect(double x, double y, double w, double h, bool fill = false, bool stroke = true)
    {
        _ops.Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(F(w)).Append(' ').Append(F(h)).Append(" re ");
        _ops.Append(fill && stroke ? "B\n" : fill ? "f\n" : "S\n");
    }

    /// <summary>Окружность кривыми Безье (4 дуги).</summary>
    public void Circle(double cx, double cy, double r, bool fill = false, bool stroke = true)
    {
        CirclePath(cx, cy, r);
        _ops.Append(fill && stroke ? "B\n" : fill ? "f\n" : "S\n");
    }

    private void CirclePath(double cx, double cy, double r)
    {
        const double k = 0.5522847498;
        double c = r * k;
        _ops.Append(F(cx + r)).Append(' ').Append(F(cy)).Append(" m\n");
        Curve(cx + r, cy + c, cx + c, cy + r, cx, cy + r);
        Curve(cx - c, cy + r, cx - r, cy + c, cx - r, cy);
        Curve(cx - r, cy - c, cx - c, cy - r, cx, cy - r);
        Curve(cx + c, cy - r, cx + r, cy - c, cx + r, cy);
        _ops.Append("h\n");
    }

    private void Curve(double x1, double y1, double x2, double y2, double x3, double y3) =>
        _ops.Append(F(x1)).Append(' ').Append(F(y1)).Append(' ').Append(F(x2)).Append(' ').Append(F(y2)).Append(' ')
            .Append(F(x3)).Append(' ').Append(F(y3)).Append(" c\n");

    /// <summary>Дуга окружности ломаной (для дверных полотен).</summary>
    public void Arc(double cx, double cy, double r, double startDeg, double endDeg)
    {
        int steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(endDeg - startDeg) / 6));
        var pts = Enumerable.Range(0, steps + 1)
            .Select(i => (startDeg + (endDeg - startDeg) * i / steps) * Math.PI / 180)
            .Select(a => (cx + r * Math.Cos(a), cy + r * Math.Sin(a))).ToList();
        Polyline(pts, false);
    }

    /// <summary>
    /// Штриховка области (набор контуров, правило ненулевой обмотки) параллельными линиями под углом angle с шагом step (мм).
    /// </summary>
    public void Hatch(IReadOnlyList<IReadOnlyList<(double X, double Y)>> regions, double step, double angleDeg, double lineWidth)
    {
        var all = regions.Where(r => r.Count >= 3).ToList();
        if (all.Count == 0) return;
        double x0 = all.Min(r => r.Min(p => p.X)), x1 = all.Max(r => r.Max(p => p.X));
        double y0 = all.Min(r => r.Min(p => p.Y)), y1 = all.Max(r => r.Max(p => p.Y));
        Save();
        foreach (var r in all) Path(r, true);
        _ops.Append("W n\n");
        LineWidth(lineWidth);
        Dash();
        double a = angleDeg * Math.PI / 180, dx = Math.Cos(a), dy = Math.Sin(a);
        // Нормаль к штрихам; проходим всю диагональ габарита.
        double nx = -dy, ny = dx;
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, half = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / 2 + step;
        for (double t = -half; t <= half; t += step)
        {
            double px = cx + nx * t, py = cy + ny * t;
            _ops.Append(F(px - dx * half)).Append(' ').Append(F(py - dy * half)).Append(" m ")
                .Append(F(px + dx * half)).Append(' ').Append(F(py + dy * half)).Append(" l\n");
        }
        _ops.Append("S\n");
        Restore();
    }

    /// <summary>Текст: height — высота прописных букв (размер шрифта по ГОСТ 2.304), angle — поворот против часовой.</summary>
    public void Text(string text, double x, double y, double height, PdfTextAlign align = PdfTextAlign.Left, double angleDeg = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        var font = _doc.Font;
        double size = height / font.CapRatio;
        double width = font.Width(text, size);
        double shift = align switch { PdfTextAlign.Center => width / 2, PdfTextAlign.Right => width, _ => 0 };
        double a = angleDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double tx = x - c * shift, ty = y - s * shift;
        var hex = new StringBuilder(text.Length * 4);
        foreach (var rune in text.EnumerateRunes())
        {
            ushort glyph = font.Glyph(rune.Value);
            _doc.Use(glyph, rune.Value);
            hex.Append(glyph.ToString("X4", Inv));
        }
        _ops.Append("BT /F1 ").Append(F(size)).Append(" Tf ")
            .Append(F(c)).Append(' ').Append(F(s)).Append(' ').Append(F(-s)).Append(' ').Append(F(c)).Append(' ')
            .Append(F(tx)).Append(' ').Append(F(ty)).Append(" Tm <").Append(hex).Append("> Tj ET\n");
    }

    /// <summary>Ширина текста (мм) при высоте прописных height.</summary>
    public double TextWidth(string text, double height) => _doc.Font.Width(text, height / _doc.Font.CapRatio);
}

/// <summary>
/// Минимальный, но полноценный генератор векторного PDF 1.7: страницы с потоками FlateDecode, шрифт Type0 / CIDFontType2
/// (Identity-H, CIDToGIDMap /Identity, массив ширин /W используемых глифов) с внедрённым TrueType (FontFile2)
/// и CMap ToUnicode — кириллица отображается, выделяется и ищется в браузерах и Adobe Acrobat.
/// </summary>
public sealed class PdfVectorDocument
{
    private readonly List<PdfVectorPage> _pages = [];
    private readonly SortedDictionary<ushort, int> _used = [];

    public TrueTypeFont Font { get; }
    public string Title { get; init; } = "Drawing";
    public string Author { get; init; } = "Architect Studio Pro";

    /// <summary>
    /// Font Subsetting: в FontFile2 пишутся только использованные глифы (имя шрифта — с тегом подмножества «ABCDEF+»);
    /// false — встраивается полный файл шрифта.
    /// </summary>
    public bool SubsetFont { get; init; } = true;

    public int PageCount => _pages.Count;

    public PdfVectorDocument(TrueTypeFont font) => Font = font;

    public PdfVectorPage AddPage(double widthMm, double heightMm)
    {
        var page = new PdfVectorPage(this, widthMm, heightMm);
        _pages.Add(page);
        return page;
    }

    internal void Use(ushort glyph, int codepoint) => _used.TryAdd(glyph, codepoint);

    public PdfSaveInfo Save(string path)
    {
        var objects = new List<byte[]>();
        int Reserve()
        {
            objects.Add([]);
            return objects.Count;
        }
        void Set(int id, string body) => objects[id - 1] = Encoding.ASCII.GetBytes(body);
        void SetStream(int id, string dict, byte[] data)
        {
            var head = Encoding.ASCII.GetBytes($"{dict.TrimEnd('>').TrimEnd()} /Length {data.Length} >>\nstream\n");
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            objects[id - 1] = [.. head, .. data, .. tail];
        }

        int catalog = Reserve();
        int pages = Reserve();
        int font = Reserve();
        int cidFont = Reserve();
        int descriptor = Reserve();
        int fontFile = Reserve();
        int toUnicode = Reserve();
        int info = Reserve();
        var pageIds = new List<int>();
        foreach (var page in _pages)
        {
            int pageId = Reserve();
            int contentId = Reserve();
            pageIds.Add(pageId);
            double w = page.WidthMm * 72 / 25.4, h = page.HeightMm * 72 / 25.4;
            Set(pageId, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {PdfVectorPage.F(w)} {PdfVectorPage.F(h)}] " +
                        $"/Resources << /Font << /F1 {font} 0 R >> /ProcSet [/PDF /Text] >> /Contents {contentId} 0 R >>");
            SetStream(contentId, "<< /Filter /FlateDecode >>", Deflate(Encoding.ASCII.GetBytes(page.Content)));
        }

        Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        Set(pages, $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(p => $"{p} 0 R"))}] /Count {pageIds.Count} >>");

        // Подмножество шрифта: только использованные глифы (с компонентами составных), имя — «ABCDEF+ISOCPEUR» (ISO 32000, 9.6.4).
        byte[] fontData = Font.Data;
        int glyphCount = _used.Count;
        string baseFont = Font.PostScriptName;
        if (SubsetFont)
        {
            var subset = TrueTypeSubsetter.Subset(Font.Data, _used, Font.PostScriptName);
            fontData = subset.Data;
            glyphCount = subset.GlyphCount;
            baseFont = subset.Tag + "+" + Font.PostScriptName;
        }
        Set(font,$"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H /DescendantFonts [{cidFont} 0 R] /ToUnicode {toUnicode} 0 R >>");
        var widths = new StringBuilder();
        foreach (var glyph in _used.Keys)
            widths.Append(glyph).Append(" [").Append((int)Math.Round(Font.Advance(glyph) * 1000.0 / Font.UnitsPerEm)).Append("] ");
        Set(cidFont, $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} " +
                     $"/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R " +
                     $"/DW 1000 /W [{widths}] /CIDToGIDMap /Identity >>");
        double k = 1000.0 / Font.UnitsPerEm;
        var bb = Font.BBox;
        Set(descriptor, $"<< /Type /FontDescriptor /FontName /{baseFont} /Flags 32 " +
                        $"/FontBBox [{(int)(bb.XMin * k)} {(int)(bb.YMin * k)} {(int)(bb.XMax * k)} {(int)(bb.YMax * k)}] /ItalicAngle 0 " +
                        $"/Ascent {(int)(Font.Ascender * k)} /Descent {(int)(Font.Descender * k)} /CapHeight {(int)(Font.CapHeight * k)} /StemV 80 /FontFile2 {fontFile} 0 R >>");
        var fontStream = Deflate(fontData);
        SetStream(fontFile, $"<< /Filter /FlateDecode /Length1 {fontData.Length} >>", fontStream);
        SetStream(toUnicode, "<< /Filter /FlateDecode >>", Deflate(Encoding.ASCII.GetBytes(BuildToUnicode())));
        Set(info, $"<< /Title {PdfString(Title)} /Author {PdfString(Author)} /Creator {PdfString("Architect Studio Pro · GostDrawingPdfExporter")} " +
                  $"/Producer {PdfString("Architect Studio Pro PdfVectorDocument")} /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>");

        using var ms = new MemoryStream();
        void Write(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        Write("%PDF-1.7\n");
        ms.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = ms.Position;
            Write($"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            Write("\nendobj\n");
        }
        long xref = ms.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, path, overwrite: true);
        return new PdfSaveInfo(ms.Length, _pages.Count, fontStream.Length, fontData.Length, Font.Data.Length, glyphCount, baseFont);
    }

    private string BuildToUnicode()
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = _used.ToList();
        for (int i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (glyph, cp) in chunk)
            {
                string utf16 = string.Concat(char.ConvertFromUtf32(cp).Select(ch => ((int)ch).ToString("X4", CultureInfo.InvariantCulture)));
                sb.Append('<').Append(glyph.ToString("X4", CultureInfo.InvariantCulture)).Append("> <").Append(utf16).Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    /// <summary>Строка документа в UTF-16BE с BOM (поля Info).</summary>
    private static string PdfString(string s)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (char c in s) sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        return sb.Append('>').ToString();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }
}
