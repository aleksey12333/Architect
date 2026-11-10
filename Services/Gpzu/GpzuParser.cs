using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services.Pdf;

namespace AIArchitectStudio.Services.Gpzu;

/// <summary>Строка текстового слоя страницы (надписи с одной базовой линией, склеенные слева направо).</summary>
public sealed record GpzuTextLine(int Page, double Y, double X, string Text);

/// <summary>Числовая ячейка таблицы координат с положением на листе (пункты PDF).</summary>
public readonly record struct GpzuToken(int Page, double X, double Y, double Height, string Text);

/// <summary>
/// Разбор градостроительного плана земельного участка в любом из форматов: PDF с текстовым слоем (форма ГПЗУ по приказу
/// Минстроя № 400/пр — реквизиты, регламент, раздел 3.1, раздел 5 и таблицы координат Приложения № 1), XML (выписки ЕГРН /
/// КПТ: ordinate x/y, num_geopoint), JSON (собственный формат GpzuDocument или массив точек) и TXT/CSV («№ X Y»).
/// Таблицы координат читаются по положению ячеек: строка = пара координат на одной базовой линии, номер точки — ближайший
/// по вертикали номер в колонке номеров (склеенные ячейки «4/5», «13/14» не сдвигают нумерацию), таблица продолжается на
/// следующей странице, если номера идут подряд; новая таблица — номер «1» или замыкание контура.
/// </summary>
public static partial class GpzuParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Палитра контуров ЗОУИТ и подзон (по очереди), штрих смещается от зоны к зоне.</summary>
    public static readonly string[] ZonePalette = ["#EC4899", "#EAB308", "#3B82F6", "#14B8A6", "#F97316", "#8B5CF6", "#22C55E", "#E11D48", "#0EA5E9", "#84CC16", "#D946EF", "#F43F5E", "#06B6D4", "#A3A3A3"];

    public static GpzuDocument Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var doc = ext switch
        {
            ".pdf" => FromPdf(path),
            ".xml" => FromXml(File.ReadAllText(path)),
            ".json" => FromJson(File.ReadAllText(path)),
            _ => FromPlainText(File.ReadAllText(path, DetectEncoding(path)))
        };
        doc.SourceFile = path;
        return doc;
    }

    private static Encoding DetectEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { new UTF8Encoding(false, true).GetString(bytes); return Encoding.UTF8; }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251);
        }
    }

    // ═════════════════════════════ PDF ═════════════════════════════

    public static GpzuDocument FromPdf(string path)
    {
        var pdf = PdfNativeContentExtractor.Read(path);
        var lines = new List<GpzuTextLine>();
        var tokens = new List<GpzuToken>();
        foreach (var page in pdf.Pages)
        {
            lines.AddRange(LinesOf(page));
            tokens.AddRange(TokensOf(page));
        }
        var doc = FromLines(lines, tokens);
        doc.PageCount = pdf.PageCount;
        int scans = pdf.Pages.Count(p => !p.HasTextLayer);
        if (scans > 0) doc.Warnings.Add($"Страниц без текстового слоя (скан): {scans} — их содержимое не разбирается.");
        doc.Warnings.AddRange(pdf.Warnings.Take(5));
        return doc;
    }

    /// <summary>Строки страницы: надписи с близкой базовой линией (допуск 45 % кегля) — слева направо, сверху вниз.</summary>
    public static IEnumerable<GpzuTextLine> LinesOf(PdfPageVectorContent page)
    {
        var runs = page.Runs.Where(r => r.IsHorizontal && r.Text.Trim().Length > 0).OrderByDescending(r => r.Y).ThenBy(r => r.X).ToList();
        var result = new List<GpzuTextLine>();
        var current = new List<PdfTextRun>();
        double y = double.NaN, tol = 0;
        void Flush()
        {
            if (current.Count == 0) return;
            var sb = new StringBuilder();
            PdfTextRun? prev = null;
            foreach (var r in current.OrderBy(r => r.X))
            {
                if (prev is not null && r.X - prev.EndX > prev.FontSize * 0.18) sb.Append(' ');
                sb.Append(r.Text);
                prev = r;
            }
            result.Add(new GpzuTextLine(page.Index, y, current.Min(r => r.X), sb.ToString()));
            current.Clear();
        }
        foreach (var r in runs)
        {
            if (current.Count > 0 && Math.Abs(r.Y - y) > tol) Flush();
            if (current.Count == 0) { y = r.Y; tol = Math.Max(1.2, r.FontSize * 0.45); }
            current.Add(r);
        }
        Flush();
        return result;
    }

    /// <summary>Отдельные слова надписей с оценкой положения (надпись «1 387509.92 1532936.30» даёт три ячейки).</summary>
    public static IEnumerable<GpzuToken> TokensOf(PdfPageVectorContent page)
    {
        foreach (var r in page.Runs.Where(r => r.IsHorizontal))
        {
            string t = r.Text;
            int len = Math.Max(1, t.Length);
            foreach (Match m in Word().Matches(t))
            {
                double x = r.X + (r.EndX - r.X) * (m.Index + m.Length / 2.0) / len;
                yield return new GpzuToken(page.Index, x, r.Y, Math.Max(4, r.FontSize), m.Value);
            }
        }
    }

    /// <summary>Разбор по строкам текстового слоя и ячейкам (общий для PDF и простого текста).</summary>
    public static GpzuDocument FromLines(IReadOnlyList<GpzuTextLine> lines, IReadOnlyList<GpzuToken>? tokens = null)
    {
        var doc = new GpzuDocument();
        string all = string.Join("\n", lines.Select(l => l.Text));
        string flat = Regex.Replace(all, @"[ \t ]+", " ");
        ReadRequisites(doc, flat);
        ReadRegulation(doc, flat);
        ReadObjects(doc, flat);
        var tables = tokens is { Count: > 0 } ? ReadTablesByPosition(tokens, lines) : ReadTablesByText(lines);
        BuildFromTables(doc, tables, flat);
        return doc;
    }

    // ───────────── Реквизиты ─────────────

    private static void ReadRequisites(GpzuDocument doc, string text)
    {
        string compact = Regex.Replace(text, @"(?<=[А-ЯA-Z0-9-]) (?=[А-ЯA-Z0-9-])", string.Empty);
        if (GpzuNumber().Match(compact) is { Success: true } n) doc.Number = n.Value;
        if (Regex.Match(text, @"Кадастровый номер[^\n]*?(\d{2}:\d{2}:\d{6,7}:\d+)") is { Success: true } c1) doc.Cadastral = c1.Groups[1].Value;
        else if (Regex.Match(text, @"\d{2}:\d{2}:\d{6,7}:\d+") is { Success: true } c2) doc.Cadastral = c2.Value;
        if (Regex.Match(text, @"Площадь (?:земельного )?участка:?\s*\n?\s*([\d\s ,.]+?)\s*(?:м²|м2|кв\.?\s*м)", RegexOptions.IgnoreCase) is { Success: true } a &&
            ParseNumber(a.Groups[1].Value) is { } area) doc.DeclaredArea = area;
        if (Regex.Match(text, @"на основании заявления:\s*\n?(.+?)\s*№\s*([\d/]+)\s*от\s*(\d{2}\.\d{2}\.\d{4})") is { Success: true } ap)
        {
            doc.Applicant = ap.Groups[1].Value.Trim().TrimEnd(',');
            doc.Application = $"№ {ap.Groups[2].Value} от {ap.Groups[3].Value}";
        }
        if (Regex.Match(text, @"Дата выдачи\s*(\d{2}\.\d{2}\.\d{4})") is { Success: true } d) doc.IssueDate = d.Groups[1].Value;
        else if (Regex.Match(text, @"Дата\s*(\d{2}\.\d{2}\.\d{4})") is { Success: true } d2) doc.IssueDate = d2.Groups[1].Value;
        // Адрес: строка с «обл.»/«край» и «ул» после «Местонахождение», иначе любая строка «…область, г …, ул …».
        if (Regex.Match(text, @"([А-ЯЁ][а-яё]+ская область,\s*г\.?\s*[А-ЯЁ][а-яё-]+(?:,\s*(?:ул|пр|пер|б-р)\.?\s*[^\n,]+)?)") is { Success: true } adr)
            doc.Address = adr.Groups[1].Value.Trim();
        if (Regex.Match(text, @"квартал[^\n]+") is { Success: true } q) doc.Address += " (" + q.Value.Trim() + ")";
        if (Regex.IsMatch(text, @"МСК-\d+")) doc.CoordinateSystem = Regex.Match(text, @"МСК-\d+").Value;
    }

    private static void ReadRegulation(GpzuDocument doc, string text)
    {
        string zone = string.Empty, zoneName = string.Empty, sub = string.Empty;
        if (Regex.Match(text, @"Территориальная зона:?\s*([А-ЯA-Z]{1,3}-\d+(?:\.\d+)?)(?:,\s*подзона\s*([\d.]+))?") is { Success: true } z)
        {
            zone = z.Groups[1].Value;
            sub = z.Groups[2].Value;
        }
        if (Regex.Match(text, @"зоны?\s*[–-]\s*([А-ЯA-Z]{1,3}-\d+)\s+(Зона[^.\n]+)") is { Success: true } zn)
        {
            if (zone.Length == 0) zone = zn.Groups[1].Value;
            zoneName = zn.Groups[2].Value.Trim();
        }
        if (sub.Length == 0 && Regex.Match(text, @"Подзоне?\s*№\s*([\d.]+)") is { Success: true } s) sub = s.Groups[1].Value;
        double? living = Regex.Match(text, @"общая площадь жилых помещений\s*[–-]\s*не более\s*([\d\s ]+)\s*кв") is { Success: true } lv ? ParseNumber(lv.Groups[1].Value) : null;
        int? parking = Regex.Match(text, @"машино-мест\s*[–-]\s*не менее\s*(\d+)") is { Success: true } pk ? int.Parse(pk.Groups[1].Value, Inv) : null;
        double? minArea = Regex.Match(text, @"минимальная площадь земельного участка\s*[–-]\s*([\d\s ]+)\s*кв") is { Success: true } ma ? ParseNumber(ma.Groups[1].Value) : null;
        var uses = new List<string>();
        if (Regex.Match(text, @"Основные виды разрешенного использования:\s*\n((?:\d+\.\s*[^\n]+\n)+)") is { Success: true } u)
            uses.AddRange(Regex.Matches(u.Groups[1].Value, @"\d+\.\s*([^\n]+)").Select(m => m.Groups[1].Value.Trim()));
        string act = Regex.Match(text, @"(Постановление\s+Администрации[^\n]+\n?[^\n]*)") is { Success: true } ac ? Regex.Replace(ac.Value, @"\s+", " ").Trim() : string.Empty;
        doc.Regulation = new GpzuRegulation
        {
            ZoneCode = zone, ZoneName = zoneName, Subzone = sub, MaxLivingArea = living, MinParkingPlaces = parking, MinParcelArea = minArea,
            PermittedUses = uses, Act = act
        };
    }

    private static void ReadObjects(GpzuDocument doc, string text)
    {
        int s = text.IndexOf("3.1. Объекты капитального строительства", StringComparison.Ordinal);
        int e = text.IndexOf("3.2.", s < 0 ? 0 : s, StringComparison.Ordinal);
        if (s < 0) return;
        string block = text[s..(e > s ? e : text.Length)];
        foreach (Match m in Regex.Matches(block, @"№\s*(\d+)\s*,\s*([^,\n]+?)\s*,(?:.|\n)*?(?:кадастровый номер|номер)\s*\n?\s*([\d:]+|-)", RegexOptions.None))
            doc.Objects.Add(new GpzuCapitalObject
            {
                Number = int.Parse(m.Groups[1].Value, Inv),
                Name = m.Groups[2].Value.Trim(),
                Cadastral = m.Groups[3].Value == "-" ? string.Empty : m.Groups[3].Value
            });
    }

    // ───────────── Таблицы координат ─────────────

    internal sealed class RawTable
    {
        public string Title = string.Empty;
        public int FirstPage, LastPage;
        public bool Merged, Closed, TitleClosed;
        public double FirstY;
        public double IdColumnX = double.NaN;
        public List<GeoPoint> Points { get; } = [];
    }

    private static List<RawTable> ReadTablesByPosition(IReadOnlyList<GpzuToken> tokens, IReadOnlyList<GpzuTextLine> lines)
    {
        var tables = new List<RawTable>();
        RawTable? current = null;
        foreach (var pageTokens in tokens.GroupBy(t => t.Page).OrderBy(g => g.Key))
        {
            var coords = pageTokens.Where(t => Coordinate().IsMatch(t.Text)).ToList();
            if (coords.Count < 2) continue;
            // Строки: пары координат на одной базовой линии (X — левее, Y — правее); базовые линии кластеризуются с допуском 40 % кегля.
            var rows = new List<(double Y, double H, GpzuToken Xt, GpzuToken Yt)>();
            var sorted = coords.OrderByDescending(t => t.Y).ToList();
            for (int i = 0; i < sorted.Count;)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[i].Y - sorted[j + 1].Y <= sorted[i].Height * 0.4) j++;
                var cells = sorted.Skip(i).Take(j - i + 1).OrderBy(t => t.X).ToList();
                for (int k = 0; k + 1 < cells.Count; k += 2) rows.Add((cells[k].Y, cells[k].Height, cells[k], cells[k + 1]));
                i = j + 1;
            }
            if (rows.Count == 0) continue;
            double xColumn = rows.Select(r => r.Xt.X).OrderBy(x => x).ElementAt(rows.Count / 2);
            double h = rows.Select(r => r.H).OrderBy(x => x).ElementAt(rows.Count / 2);
            var ids = pageTokens.Where(t => IdToken().IsMatch(t.Text) && t.X < xColumn - h * 1.2 && t.X > xColumn - 260).ToList();
            var used = new HashSet<GpzuToken>();
            double idColumn = ids.Count > 0 ? ids.Average(t => t.X) : xColumn - 60;
            double step = rows.Count > 1 ? rows.Zip(rows.Skip(1), (a, b) => a.Y - b.Y).Where(d => d > 0).DefaultIfEmpty(h * 1.4).OrderBy(d => d).ElementAt(0) : h * 1.4;
            double prevY = double.NaN;
            foreach (var row in rows)
            {
                // Текст колонки наименования на этой строке: начало новой таблицы (у таблиц ЗОУИТ координаты могут совпадать
                // с границей, поэтому «1» с координатами первой точки — замыкание, только если наименования нет и нет разрыва).
                string leftText = LeftText(lines, row.Xt.Page, idColumn, row.Y, h);
                bool gap = !double.IsNaN(prevY) && prevY - row.Y > step * 1.6;
                prevY = row.Y;
                // Номер на той же базовой линии, иначе ближайший неиспользованный по вертикали (склеенная ячейка).
                var same = ids.Where(t => !used.Contains(t) && Math.Abs(t.Y - row.Y) <= h * 0.45).OrderByDescending(t => t.X).FirstOrDefault();
                bool merged = false;
                if (same.Text is null)
                {
                    same = ids.Where(t => !used.Contains(t) && Math.Abs(t.Y - row.Y) <= h * 1.6).OrderBy(t => Math.Abs(t.Y - row.Y)).FirstOrDefault();
                    merged = true;
                }
                string? id = same.Text;
                if (id is not null) used.Add(same);
                double x = ParseCoordinate(row.Xt.Text), y = ParseCoordinate(row.Yt.Text);
                bool closure = current is { Points.Count: > 2 } && SamePoint(current.Points[0], x, y) && (id is null || id == current.Points[0].Number) &&
                               leftText.Length == 0 && !gap;
                // Новая таблица: замкнутая таблица, номер «1» (не замыкание), или наименование новой зоны в первой колонке.
                if (current is not null && !closure && (current.Closed || id == "1" || (leftText.Length > 0 && current.Points.Count > 2 && current.TitleClosed)))
                {
                    tables.Add(current);
                    current = null;
                }
                if (current is null)
                    current = new RawTable { FirstPage = row.Xt.Page, LastPage = row.Xt.Page, FirstY = row.Y, IdColumnX = idColumn };
                // Наименование — подряд идущие строки первой колонки с начала таблицы на её первой странице.
                if (!current.TitleClosed && row.Xt.Page == current.FirstPage)
                {
                    if (leftText.Length > 0) current.Title = (current.Title + " " + leftText).Trim();
                    else if (current.Points.Count > 0) current.TitleClosed = true;
                }
                current.LastPage = row.Xt.Page;
                current.Merged |= merged && id is not null;
                if (closure)
                {
                    current!.Closed = true;
                    continue;
                }
                id ??= (current.Points.Count + 1).ToString(Inv);
                current.Points.Add(new GeoPoint(id, x, y));
            }
        }
        if (current is not null) tables.Add(current);
        // Заголовок таблицы на её первой странице: левее колонки номеров, от первой строки вниз до конца таблицы на странице.
        return tables.Where(t => t.Points.Count >= 3).ToList();
    }

    /// <summary>Текст колонки наименования на базовой линии строки (левее колонки номеров, без номера и координат).</summary>
    private static string LeftText(IReadOnlyList<GpzuTextLine> lines, int page, double idColumnX, double rowY, double h)
    {
        var line = lines.FirstOrDefault(l => l.Page == page && Math.Abs(l.Y - rowY) <= h * 0.45 && l.X < idColumnX - h * 0.8);
        if (line is null) return string.Empty;
        string cleaned = Regex.Replace(line.Text, @"\s*н?\d{1,4}(?:\s+\d{5,7}[.,]\d+)+.*$", string.Empty).Trim();
        return IdToken().IsMatch(cleaned) || Coordinate().IsMatch(cleaned) ? string.Empty : cleaned;
    }

    /// <summary>Таблицы по тексту (TXT/CSV): строки «№ X Y» или «X Y»; новая таблица — заголовок-строка без чисел или номер 1.</summary>
    private static List<RawTable> ReadTablesByText(IReadOnlyList<GpzuTextLine> lines)
    {
        var tables = new List<RawTable>();
        RawTable? current = null;
        string pendingTitle = string.Empty;
        foreach (var line in lines)
        {
            var m = RowPattern().Match(line.Text);
            if (!m.Success)
            {
                if (line.Text.Trim().Length > 3 && !Regex.IsMatch(line.Text, @"^\s*[XYХ№\s]+$")) pendingTitle = line.Text.Trim();
                continue;
            }
            string? id = m.Groups["id"].Success ? m.Groups["id"].Value : null;
            double x = ParseCoordinate(m.Groups["x"].Value), y = ParseCoordinate(m.Groups["y"].Value);
            if (current is not null && (current.Closed || (id == "1" && !(current.Points.Count > 2 && SamePoint(current.Points[0], x, y)))))
            {
                tables.Add(current);
                current = null;
            }
            current ??= new RawTable { Title = pendingTitle, FirstPage = line.Page, LastPage = line.Page };
            current.LastPage = line.Page;
            if (current.Points.Count > 2 && SamePoint(current.Points[0], x, y)) { current.Closed = true; continue; }
            current.Points.Add(new GeoPoint(id ?? (current.Points.Count + 1).ToString(Inv), x, y));
        }
        if (current is not null) tables.Add(current);
        return tables.Where(t => t.Points.Count >= 3).ToList();
    }

    private static bool SamePoint(GeoPoint p, double x, double y) => Math.Abs(p.X - x) < 0.005 && Math.Abs(p.Y - y) < 0.005;

    // ───────────── Зоны: таблицы Приложения № 1 + раздел 5 + экспликация ─────────────

    private static void BuildFromTables(GpzuDocument doc, List<RawTable> tables, string text)
    {
        // Граница — первая таблица с замыканием, иначе первая таблица.
        var boundary = tables.FirstOrDefault(t => t.Closed) ?? tables.FirstOrDefault();
        if (boundary is not null)
        {
            doc.Boundary = boundary.Points;
            doc.Tables.Add(new GpzuCoordinateTable("Граница земельного участка", boundary.FirstPage + 1, boundary.LastPage + 1, boundary.Points.Count, true, boundary.Merged));
        }
        else doc.Warnings.Add("Таблица координат границы земельного участка не найдена.");

        var paragraphs = ZoneParagraphs(text);
        var explication = Explication(text);
        int color = 0;
        // 1) Зарегистрированные ЗОУИТ (раздел 5.1: «Охранная зона транспорта 66:00-6.NNNN»).
        foreach (var p in paragraphs.Where(p => p.Registry.Length > 0))
        {
            var key = ZoneKey(p.Header + " " + p.Restriction, registered: true);
            var table = tables.Where(t => t != boundary && t.Title.Contains("ЕГРН", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(t => ZoneKey(t.Title, registered: true) == key);
            var (min, max) = Heights(p.Header + " " + p.Restriction);
            doc.Zones.Add(new GpzuZone
            {
                Id = "zouit-" + p.Registry,
                Code = p.Registry,
                Name = table is null ? p.Header : $"{p.Registry} · {table.Title.Replace("(ЕГРН)", string.Empty).Trim()}",
                Kind = GpzuZoneKind.TransportZone,
                ExplicationNo = explication.TryGetValue(p.Registry, out var no) ? no : 0,
                Restriction = p.Restriction,
                HeightLimitMin = min, HeightLimitMax = max,
                Polygon = table?.Points ?? [],
                CoversWholeParcel = table is not null && Geodesy.SameRing(table.Points, doc.Boundary),
                Source = table is null ? "ГПЗУ, раздел 5.1 (координаты — в ЕГРН)" : $"ГПЗУ, раздел 5.1 + Приложение № 1, стр. {table.FirstPage + 1}",
                ColorHex = ZonePalette[color++ % ZonePalette.Length]
            });
            if (table is not null) table.Title += " ⇒ " + p.Registry;
        }
        // 2) Остальные таблицы Приложения № 1 — подзоны и секторы ПАТ (сведения ИСОГД), в т. ч. ЕГРН без сопоставления.
        foreach (var t in tables.Where(t => t != boundary))
        {
            bool mapped = t.Title.Contains("⇒", StringComparison.Ordinal);
            doc.Tables.Add(new GpzuCoordinateTable(t.Title, t.FirstPage + 1, t.LastPage + 1, t.Points.Count, Geodesy.SameRing(t.Points, doc.Boundary), t.Merged));
            if (mapped) continue;
            var key = ZoneKey(t.Title, registered: false);
            var para = paragraphs.FirstOrDefault(p => p.Registry.Length == 0 && ZoneKey(p.Header, registered: false) == key);
            var (min, max) = Heights(t.Title);
            if (min is null && para is not null) (min, max) = Heights(para.Header + " " + para.Restriction);
            string name = Regex.Replace(t.Title, @"\(Приказ.*$", string.Empty).Trim();
            if (name.Length == 0) name = $"Зона по таблице, стр. {t.FirstPage + 1}";
            doc.Zones.Add(new GpzuZone
            {
                Id = $"zone-{doc.Zones.Count + 1}",
                Code = key,
                Name = name,
                Kind = GpzuZoneKind.AirfieldSubzone,
                ExplicationNo = explication.TryGetValue(key, out var no) ? no : 0,
                Restriction = para?.Restriction ?? string.Empty,
                HeightLimitMin = min, HeightLimitMax = max,
                Polygon = t.Points,
                CoversWholeParcel = Geodesy.SameRing(t.Points, doc.Boundary),
                Source = $"ГПЗУ, Приложение № 1, стр. {t.FirstPage + 1}{(t.LastPage != t.FirstPage ? "–" + (t.LastPage + 1) : string.Empty)}",
                ColorHex = ZonePalette[color++ % ZonePalette.Length]
            });
        }
        // 3) Место допустимого размещения — по экспликации (без координат ППТ — совпадает с участком: отступы не установлены).
        if (Regex.IsMatch(text, @"Место допустимого размещения", RegexOptions.IgnoreCase) && doc.Boundary.Count > 2)
            doc.Zones.Insert(0, new GpzuZone
            {
                Id = "allowed",
                Code = doc.Regulation.ZoneCode,
                Name = $"Место допустимого размещения ОКС ({doc.Regulation.ZoneCode})",
                Kind = GpzuZoneKind.AllowedArea,
                ExplicationNo = 1,
                Restriction = Regex.IsMatch(text, @"отступы[^\n]*не подлежат установлению", RegexOptions.IgnoreCase)
                    ? "Минимальные отступы от границ участка не подлежат установлению — пятно совпадает с границей участка."
                    : "По чертежу градостроительного плана.",
                Polygon = doc.Boundary,
                CoversWholeParcel = true,
                Source = "ГПЗУ, чертёж (экспликация п. 1), раздел 2.3",
                ColorHex = "#10B981"
            });
    }

    private sealed record ZoneParagraph(string Registry, string Header, string Restriction);

    /// <summary>Абзацы раздела 5.1: «- Охранная зона транспорта 66:00-6.1908. Ограничения: …;» и «– Приаэродромная территория: …».</summary>
    private static List<ZoneParagraph> ZoneParagraphs(string text)
    {
        var result = new List<ZoneParagraph>();
        int s = text.IndexOf("5.1.", StringComparison.Ordinal);
        int e = text.IndexOf("5.2.", Math.Max(0, s), StringComparison.Ordinal);
        if (s < 0) return result;
        string block = text[s..(e > s ? e : text.Length)];
        block = Regex.Replace(block, @"Страница\s*\d+\s*из\s*\d+\s*\n?ГПЗУ № [^\n]*\n?[^\n]*\n", " ");
        var starts = Regex.Matches(block, @"(?m)^[-–]\s*(?=Охранная зона|Приаэродромная|Зона|Санитарн|Водоохран)");
        for (int i = 0; i < starts.Count; i++)
        {
            int from = starts[i].Index + starts[i].Length;
            int to = i + 1 < starts.Count ? starts[i + 1].Index : block.Length;
            string body = Regex.Replace(block[from..to], @"\s+", " ").Trim();
            int r = body.IndexOf("Ограничения:", StringComparison.Ordinal);
            string header = (r > 0 ? body[..r] : body).Trim().TrimEnd('.', ',');
            string restriction = r > 0 ? body[(r + 12)..].Trim() : string.Empty;
            int stop = restriction.IndexOf("В соответствии с данными информационной системы", StringComparison.Ordinal);
            if (stop > 0) restriction = restriction[..stop].Trim();
            stop = restriction.IndexOf("В целях актуализации", StringComparison.Ordinal);
            if (stop > 0) restriction = restriction[..stop].Trim();
            string registry = Regex.Match(header, @"\d{2}:\d{2}-\d\.\d+").Value;
            result.Add(new ZoneParagraph(registry, header, restriction.TrimEnd(';', ' ')));
        }
        return result;
    }

    /// <summary>Экспликация чертежа: «14 Охранная зона транспорта №66:00-6.1909» → номер; подзоны — по ключу.</summary>
    private static Dictionary<string, int> Explication(string text)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        int s = text.IndexOf("ЭКСПЛИКАЦИЯ", StringComparison.OrdinalIgnoreCase);
        if (s < 0) return map;
        int e = text.IndexOf("УСЛОВНЫЕ ОБОЗНАЧЕНИЯ", s, StringComparison.OrdinalIgnoreCase);
        string block = text[s..(e > s ? e : Math.Min(text.Length, s + 4000))];
        // Пункт начинается строкой «N Текст с заглавной», где N — следующий номер; «3 подзона …» — продолжение пункта 2.
        var items = new List<(int No, StringBuilder Body)>();
        foreach (var raw in block.Split('\n'))
        {
            int expected = items.Count == 0 ? 1 : items[^1].No + 1;
            // На строку экспликации могут попасть номера-выноски чертежа левее («21 4 Приаэродромная…») — ищем ожидаемый номер.
            var m = Regex.Matches(raw, @"(?:^|\s)(\d{1,2})\s+(\p{Lu}.*)$").FirstOrDefault(x => int.Parse(x.Groups[1].Value, Inv) == expected);
            if (m is not null) items.Add((expected, new StringBuilder(m.Groups[2].Value)));
            else if (items.Count > 0) items[^1].Body.Append(' ').Append(raw.Trim());
        }
        foreach (var (no, sb) in items)
        {
            string body = Regex.Replace(sb.ToString(), @"\s+", " ");
            if (Regex.Match(body, @"\d{2}:\d{2}-\d\.\d+") is { Success: true } reg) map.TryAdd(reg.Value, no);
            else if (body.Contains("Приаэродромная", StringComparison.Ordinal)) map.TryAdd(ZoneKey(body, registered: false), no);
        }
        return map;
    }

    /// <summary>
    /// Ключ сопоставления зоны: аэродром (K — Кольцово, A — Арамиль), номер подзоны (римские и словами), номер сектора.
    /// У зарегистрированных ЗОУИТ подзона определяется ещё и по смыслу ограничений (помехи — 4, высота — 3, ОПО — 5, птицы — 6).
    /// </summary>
    public static string ZoneKey(string text, bool registered)
    {
        string t = Regex.Replace(text, @"\s+", " ");
        string airfield = t.Contains("Арамиль", StringComparison.OrdinalIgnoreCase) ? "A" : "K";
        int sub = 0;
        // У абзацев раздела 5.1 подзона определяется по смыслу ограничений (в тексте встречаются ссылки на «первую подзону»).
        if (registered)
        {
            if (t.Contains("помехи", StringComparison.OrdinalIgnoreCase)) sub = 4;
            else if (t.Contains("шестой", StringComparison.OrdinalIgnoreCase) || t.Contains("птиц", StringComparison.OrdinalIgnoreCase)) sub = 6;
            else if (t.Contains("опасных производственных", StringComparison.OrdinalIgnoreCase)) sub = 5;
            else if (t.Contains("третьей подзоне", StringComparison.OrdinalIgnoreCase) || t.Contains("высота которых превышает", StringComparison.OrdinalIgnoreCase)) sub = 3;
        }
        var m = Regex.Match(t, @"\b(VI|IV|V|III|II|I|\d)\s*[Пп]одзон|[Пп]одзона\s*(\d)|\b(Перв|Втор|Трет|Четв[её]рт|Пят|Шест)\w*\s+подзон", RegexOptions.IgnoreCase);
        if (sub == 0 && m.Success)
        {
            string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            sub = v.ToUpperInvariant() switch
            {
                "I" or "1" or "ПЕРВ" => 1, "II" or "2" or "ВТОР" => 2, "III" or "3" or "ТРЕТ" => 3, "IV" or "4" or "ЧЕТВЁРТ" or "ЧЕТВЕРТ" => 4,
                "V" or "5" or "ПЯТ" => 5, "VI" or "6" or "ШЕСТ" => 6, _ => 0
            };
        }
        var sec = Regex.Match(t, @"(\d+)\s*Сектор|Сектор\s*(\d+)", RegexOptions.IgnoreCase);
        // Зарегистрированные ЗОУИТ — подзоны целиком (в тексте ограничений «выделяются 9 секторов» — не номер сектора).
        string sector = !registered && sec.Success ? (sec.Groups[1].Success ? sec.Groups[1].Value : sec.Groups[2].Value) : "-";
        return $"ПАТ-{airfield}-{sub}-{sector}";
    }

    /// <summary>Ограничение абсолютной высоты в тексте: «Hабс = 383,50 м», «от 365,00 м до 367,40 м», «423,58 - Ограничение абсолютной высоты».</summary>
    public static (double? Min, double? Max) Heights(string text)
    {
        var range = Regex.Match(text, @"от\s*(\d{2,4}[,.]\d{1,2})\s*м?\s*до\s*(\d{2,4}[,.]\d{1,2})\s*м");
        if (range.Success) return (ParseCoordinate(range.Groups[1].Value), ParseCoordinate(range.Groups[2].Value));
        var one = Regex.Match(text, @"[HН]\s*абс\s*=\s*(\d{2,4}[,.]\d{1,2})");
        if (one.Success) { double v = ParseCoordinate(one.Groups[1].Value); return (v, v); }
        var lim = Regex.Match(text, @"(\d{2,4}[,.]\d{1,2})\s*[-–]\s*Ограничение абсолютной высоты");
        if (lim.Success) { double v = ParseCoordinate(lim.Groups[1].Value); return (v, v); }
        var bs = Regex.Match(text, @"высот[^\n]{0,80}?(\d{3}[,.]\d{2})\s*м", RegexOptions.IgnoreCase);
        if (bs.Success && text.Contains("Балтийской", StringComparison.OrdinalIgnoreCase)) { double v = ParseCoordinate(bs.Groups[1].Value); return (v, v); }
        return (null, null);
    }

    // ═════════════════════════════ XML / JSON / TXT ═════════════════════════════

    /// <summary>XML (выписка ЕГРН, КПТ, произвольная схема): точки — элементы с атрибутами/дочерними x и y (ordinate, Point, …).</summary>
    public static GpzuDocument FromXml(string xml)
    {
        var x = XDocument.Parse(xml);
        var doc = new GpzuDocument();
        var points = new List<GeoPoint>();
        foreach (var el in x.Descendants())
        {
            string? xs = Attr(el, "x") ?? Child(el, "x"), ys = Attr(el, "y") ?? Child(el, "y");
            if (xs is null || ys is null || !double.TryParse(xs.Replace(',', '.'), NumberStyles.Float, Inv, out var px) ||
                !double.TryParse(ys.Replace(',', '.'), NumberStyles.Float, Inv, out var py)) continue;
            string num = Attr(el, "num_geopoint") ?? Attr(el, "number") ?? Child(el, "num_geopoint") ?? Child(el, "ord_nmb") ?? (points.Count + 1).ToString(Inv);
            if (points.Count > 2 && SamePoint(points[0], px, py)) break;
            points.Add(new GeoPoint(num, px, py));
        }
        doc.Boundary = points;
        string text = string.Join("\n", x.Descendants().Where(e => !e.HasElements).Select(e => e.Value));
        if (Regex.Match(xml, @"\d{2}:\d{2}:\d{6,7}:\d+") is { Success: true } c) doc.Cadastral = c.Value;
        foreach (var name in new[] { "area", "Area" })
            if (x.Descendants().FirstOrDefault(e => e.Name.LocalName == name) is { } ae && ParseNumber(ae.Elements().FirstOrDefault()?.Value ?? ae.Value) is { } a) { doc.DeclaredArea = a; break; }
        ReadRegulation(doc, text);
        doc.Tables.Add(new GpzuCoordinateTable("Граница (XML)", 1, 1, points.Count, true, false));
        return doc;

        static string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        static string? Child(XElement e, string name) =>
            e.Elements().FirstOrDefault(c => c.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase) && !c.HasElements)?.Value;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    /// <summary>JSON: собственный формат GpzuDocument (Serialize) или массив точек [{number?, x, y}].</summary>
    public static GpzuDocument FromJson(string json)
    {
        using var probe = JsonDocument.Parse(json);
        if (probe.RootElement.ValueKind == JsonValueKind.Object && probe.RootElement.TryGetProperty("boundary", out _) || probe.RootElement.TryGetProperty("Boundary", out _))
            return JsonSerializer.Deserialize<GpzuDocument>(json, Json) ?? new GpzuDocument();
        var doc = new GpzuDocument();
        var arr = probe.RootElement.ValueKind == JsonValueKind.Array ? probe.RootElement
            : probe.RootElement.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Array);
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var p in arr.EnumerateArray())
            {
                double px = Num(p, "x"), py = Num(p, "y");
                string n = p.TryGetProperty("number", out var nn) ? nn.ToString() : (doc.Boundary.Count + 1).ToString(Inv);
                doc.Boundary.Add(new GeoPoint(n, px, py));
            }
        return doc;

        static double Num(JsonElement e, string name) =>
            e.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value: var v } && v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : e.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value: var s } && s.ValueKind == JsonValueKind.String ? ParseCoordinate(s.GetString()!) : 0;
    }

    public static string Serialize(GpzuDocument doc) => JsonSerializer.Serialize(doc, Json);

    /// <summary>Простой текст / CSV / копия текстового слоя PDF: строки «№ X Y» (разделители — пробел, «;», «,» и табуляция).</summary>
    public static GpzuDocument FromPlainText(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n').Select((t, i) => new GpzuTextLine(0, -i, 0, t)).ToList();
        return FromLines(lines);
    }

    // ═════════════════════════════ Числа ═════════════════════════════

    public static double ParseCoordinate(string s) => double.Parse(s.Replace(',', '.').Replace(" ", string.Empty), NumberStyles.Float, Inv);

    public static double? ParseNumber(string s)
    {
        string t = Regex.Replace(s, @"[\s ]", string.Empty).Replace(',', '.').TrimEnd('.');
        return double.TryParse(t, NumberStyles.Float, Inv, out var v) ? v : null;
    }

    [GeneratedRegex(@"[^\s;]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"^\d{5,7}[.,]\d{1,3}$")]
    private static partial Regex Coordinate();

    [GeneratedRegex(@"^н?\d{1,4}$")]
    private static partial Regex IdToken();

    [GeneratedRegex(@"^\s*(?:(?<id>н?\d{1,4})[\s;,\t]+)?(?<x>\d{5,7}[.,]\d{1,3})[\s;\t]+(?<y>\d{5,7}[.,]\d{1,3})\s*[;,]?\s*$")]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"РФ-\d{2}-\d-\d{2}-\d-\d{2}-\d{4}-\d{3,5}-\d")]
    private static partial Regex GpzuNumber();
}
