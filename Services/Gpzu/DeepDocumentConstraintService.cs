using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services.Pdf;

namespace AIArchitectStudio.Services.Gpzu;

/// <summary>Предложение (фрагмент) текста документа с местом: страница (с 1; 0 — нет страниц) и признак сноски «*».</summary>
public sealed record DocumentSentence(int Page, string Text, bool IsFootnote);

/// <summary>Полный текст документа по страницам: имя, предложения, наличие текстового слоя, предупреждения.</summary>
public sealed class DocumentText
{
    public string FileName { get; init; } = string.Empty;
    public List<(int Page, string Text)> Pages { get; } = [];
    public List<DocumentSentence> Sentences { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool HasText => Pages.Any(p => p.Text.Trim().Length > 0);
    public int CharCount => Pages.Sum(p => p.Text.Length);
    public string FullText => string.Join("\n", Pages.Select(p => p.Text));
}

/// <summary>Вызов модели «система + запрос → ответ» (облачный сервис по ключу шлюза или основная локальная модель).</summary>
public sealed record ConstraintModel(string Title, Func<string, string, CancellationToken, Task<string>> Ask);

/// <summary>Найденный в сети источник: заголовок, адрес, фрагмент.</summary>
public sealed record WebSearchHit(string Title, string Url, string Snippet);

/// <summary>Итог чтения документа или поиска: пункты для окна подтверждения, отчёт о проходах, сбой модели (если был).</summary>
public sealed class ConstraintExtraction
{
    public List<ProjectConstraintItem> Items { get; } = [];
    public List<string> Log { get; } = [];
    public int CoreCount { get; set; }
    public int ModelCount { get; set; }
    public int ModelCalls { get; set; }
    public string? ModelError { get; set; }
    public int Chars { get; set; }
    public int Pages { get; set; }
    public List<WebSearchHit> WebHits { get; } = [];
}

/// <summary>
/// Волна 6 — глубокое чтение всех «НО» документа. Помимо координат, документ (PDF, DOCX, TXT, XML, JSON) читается целиком от первой
/// до последней страницы: детерминированные правила ядра (высоты, отступы, вынос сетей и ТУ, СТУ, согласования с Росавиацией и
/// оператором аэродрома, СЗЗ, пожарные проезды, лимиты жилья, машино-места, культурное наследие, ОПО, охранные зоны, сноски «*»)
/// и проход выбранной моделью (облачный сервис по ключу шлюза или основная локальная модель) по тексту частями с маркерами страниц.
/// Цитата каждого пункта модели сверяется с текстом документа (дословно); дубли объединяются. Веб-поиск: поисковый HTTP-экстрактор +
/// база нормативов ядра (СП 42.13330, ПП РФ № 160, № 878, № 578, СП 62, СП 124, СП 4.13130, СанПиН 2.2.1/2.1.1.1200-03) + синтез моделью.
/// Ничего не применяется само: пункты уходят в окно подтверждения; отклонённый пункт можно перепроверить с уточнением или задать вручную.
/// </summary>
public static partial class DeepDocumentConstraintService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string CoreName = "Ядро расчётов";

    /// <summary>Размер части текста для одного вызова модели (символов).</summary>
    public const int ChunkChars = 14_000;

    // ═════════════════════════════ Текст документа ═════════════════════════════

    public static DocumentText Read(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var doc = new DocumentText { FileName = Path.GetFileName(path) };
        switch (ext)
        {
            case ".pdf":
            {
                var pdf = PdfNativeContentExtractor.Read(path);
                foreach (var page in pdf.Pages)
                {
                    var lines = GpzuParser.LinesOf(page).Select(l => l.Text);
                    doc.Pages.Add((page.Index + 1, string.Join("\n", lines)));
                }
                int scans = pdf.Pages.Count(p => !p.HasTextLayer);
                if (scans > 0) doc.Warnings.Add($"Страниц без текстового слоя (скан): {scans} — их текст недоступен ядру; модель со зрением читает их по снимку страницы.");
                break;
            }
            case ".docx":
                ReadDocx(path, doc);
                break;
            case ".xml":
            {
                var x = XDocument.Load(path);
                doc.Pages.Add((1, string.Join("\n", x.DescendantNodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0))));
                break;
            }
            case ".json":
            {
                using var j = JsonDocument.Parse(File.ReadAllText(path));
                var sb = new StringBuilder();
                CollectJsonStrings(j.RootElement, sb);
                doc.Pages.Add((1, sb.ToString()));
                break;
            }
            case ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp":
                doc.Warnings.Add("Изображение (скан) без текстового слоя — текст читает только модель со зрением.");
                break;
            default:
            {
                string text = ReadPlain(path);
                // Разрыв страницы (\f) — новая страница.
                var parts = text.Split('\f');
                for (int i = 0; i < parts.Length; i++) doc.Pages.Add((i + 1, parts[i]));
                break;
            }
        }
        Segment(doc);
        return doc;
    }

    /// <summary>Текст из строки (вставка, автотест): страницы — по символу \f или строкам «=== СТРАНИЦА N ===».</summary>
    public static DocumentText FromText(string text, string name = "текст")
    {
        var doc = new DocumentText { FileName = name };
        var parts = Regex.Split(text.Replace("\r", string.Empty), @"\f|^={2,}\s*СТРАНИЦА\s*\d+\s*={2,}\s*$", RegexOptions.Multiline);
        int page = 0;
        foreach (var p in parts)
        {
            if (p.Trim().Length == 0 && parts.Length > 1) continue;
            doc.Pages.Add((++page, p));
        }
        Segment(doc);
        return doc;
    }

    private static string ReadPlain(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static void ReadDocx(string path, DocumentText doc)
    {
        using var zip = ZipFile.OpenRead(path);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        string Part(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry is null) return string.Empty;
            using var s = entry.Open();
            var x = XDocument.Load(s);
            // Абзац — строка; разрыв страницы (w:br type=page или lastRenderedPageBreak) — символ \f.
            var sb = new StringBuilder();
            foreach (var p in x.Descendants(w + "p"))
            {
                foreach (var node in p.Descendants())
                {
                    if (node.Name == w + "t") sb.Append(node.Value);
                    else if (node.Name == w + "tab") sb.Append(' ');
                    else if (node.Name == w + "br" && (string?)node.Attribute(w + "type") == "page" || node.Name == w + "lastRenderedPageBreak") sb.Append('\f');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
        var body = Part("word/document.xml").Split('\f');
        for (int i = 0; i < body.Length; i++) doc.Pages.Add((i + 1, body[i]));
        // Сноски и концевые сноски — там чаще всего и прячутся «но».
        string notes = Part("word/footnotes.xml") + Part("word/endnotes.xml");
        if (notes.Trim().Length > 0) doc.Pages.Add((body.Length + 1, string.Join("\n", notes.Split('\n').Where(l => l.Trim().Length > 0).Select(l => "* " + l.Trim()))));
    }

    private static void CollectJsonStrings(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                if (e.GetString() is { Length: > 3 } s) sb.AppendLine(s);
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) CollectJsonStrings(p.Value, sb);
                break;
            case JsonValueKind.Array:
                foreach (var v in e.EnumerateArray()) CollectJsonStrings(v, sb);
                break;
        }
    }

    /// <summary>Разбивка страниц на предложения: строки, начинающиеся со «*», — отдельные сноски; переносы строк склеиваются.</summary>
    private static void Segment(DocumentText doc)
    {
        foreach (var (page, text) in doc.Pages)
        {
            var buffer = new StringBuilder();
            void Flush(bool footnote)
            {
                string t = Regex.Replace(buffer.ToString(), @"\s+", " ").Trim();
                buffer.Clear();
                if (t.Length < 6) return;
                foreach (var s in SentenceSplit().Split(t))
                    if (s.Trim().Length >= 6) doc.Sentences.Add(new DocumentSentence(page, s.Trim(), footnote));
            }
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { Flush(false); continue; }
                if (FootnoteStart().IsMatch(line))
                {
                    Flush(false);
                    buffer.Append(line);
                    Flush(true);
                    continue;
                }
                buffer.Append(line).Append(' ');
            }
            Flush(false);
        }
    }

    // ═════════════════════════════ Правила ядра ═════════════════════════════

    /// <summary>Детерминированный проход ядра по всем предложениям документа (страницы 1…N, сноски «*»).</summary>
    public static List<ProjectConstraintItem> ExtractCore(DocumentText doc, ConstraintOrigin origin = ConstraintOrigin.Document, string? source = null)
    {
        var items = new List<ProjectConstraintItem>();
        source ??= doc.FileName;
        foreach (var s in doc.Sentences)
        {
            var found = RulesFor(s.Text).ToList();
            if (found.Count == 0 && s.IsFootnote)
                found.Add(new ProjectConstraintItem
                {
                    Category = ConstraintCategory.SpecialConditions,
                    Title = "Особое условие (сноска)",
                    Impact = ConstraintImpact.RequiresApproval,
                    ImpactText = "Сноска документа содержит условие без числового параметра — проверьте его в проекте."
                });
            foreach (var f in found)
                items.Add(f with
                {
                    Quote = QuoteWindow(s.Text, f),
                    Page = s.Page,
                    Clause = ClauseOf(s.Text, s.IsFootnote),
                    Source = source,
                    Origin = origin,
                    ExtractedBy = CoreName,
                    QuoteVerified = true,
                    IsFootnote = s.IsFootnote,
                    Confidence = 0.8
                });
        }
        return Dedupe(items);
    }

    /// <summary>Правила по одному предложению: каждая сработавшая категория — пункт с числами для ядра.</summary>
    public static IEnumerable<ProjectConstraintItem> RulesFor(string text)
    {
        string t = text.Replace(' ', ' ');
        string low = t.ToLowerInvariant();
        var net = NetworkOf(low);

        // Абсолютная отметка (Hабс, м БС).
        var abs = AbsHeight().Match(t);
        if (abs.Success && Num(abs.Groups["v"].Value) is { } habs && habs is > 50 and < 3000)
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.HeightLimit,
                Title = string.Create(Ru, $"Ограничение абсолютной высоты Hабс {habs:0.00} м БС"),
                MaxHeightAbsM = habs,
                Impact = ConstraintImpact.Blocking,
                ImpactText = "Верх здания (с надстройками, антеннами, кранами) не выше указанной отметки."
            };
        else if (RelHeight().Match(t) is { Success: true } rel && Num(rel.Groups["v"].Value) is { } h && h is > 2 and < 600)
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.HeightLimit,
                Title = string.Create(Ru, $"Предельная высота {h:0.0#} м"),
                MaxHeightM = h,
                Impact = ConstraintImpact.Blocking,
                ImpactText = "Высота здания над уровнем земли не должна превышать предельную."
            };

        if (Setback().Match(t) is { Success: true } sb && Num(sb.Groups["v"].Value) is { } setback && setback is > 0 and < 100)
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.Setback,
                Title = string.Create(Ru, $"Минимальный отступ от границ участка {setback:0.0#} м"),
                MinSetbackM = setback,
                Impact = ConstraintImpact.Blocking,
                ImpactText = "Здание размещается не ближе отступа к границам земельного участка."
            };

        bool relocation = Relocation().IsMatch(low) && (net is not null || NetworkWord().IsMatch(low));
        if (relocation)
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.NetworkRelocation,
                Title = "Вынос (переустройство) сетей" + (net is { } k ? ": " + SiteBoundaryBuilder.StyleOf(k).Title.ToLowerInvariant() : string.Empty),
                RequiresNetworkRelocation = true,
                NetworkKind = net,
                BufferRadiusM = ProtectionDistance(t),
                Impact = ConstraintImpact.RequiresApproval,
                ImpactText = "Размещение в охранной зоне — только после выноса сети по техническим условиям балансодержателя."
            };
        else if (Tu().IsMatch(low) && (net is not null || NetworkWord().IsMatch(low)))
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.NetworkRelocation,
                Title = "Технические условия балансодержателя сети",
                RequiresNetworkRelocation = true,
                NetworkKind = net,
                Impact = ConstraintImpact.RequiresApproval,
                ImpactText = "Нужны ТУ на подключение / переустройство сети до начала проектирования."
            };

        if (Stu().IsMatch(t))
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.SpecialConditions,
                Title = "Требуются специальные технические условия (СТУ)",
                Impact = ConstraintImpact.RequiresApproval,
                ImpactText = "Отступление от норм возможно только по СТУ, согласованным Минстроем / МЧС."
            };

        if (Aviation().IsMatch(low) && Agreement().IsMatch(low))
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.Approval,
                Title = "Согласование с Росавиацией / оператором аэродрома",
                Impact = ConstraintImpact.RequiresApproval,
                ImpactText = "Размещение объекта в приаэродромной территории — по согласованию (Воздушный кодекс, ст. 47)."
            };

        if (SanitaryZone().Match(t) is { Success: true } sz)
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.SanitaryZone,
                Title = "Санитарно-защитная зона" + (Num(sz.Groups["v"].Value) is { } r ? string.Create(Ru, $" {r:0.#} м") : string.Empty),
                BufferRadiusM = Num(sz.Groups["v"].Value),
                Impact = ConstraintImpact.Blocking,
                ImpactText = "В СЗЗ не размещается жилая застройка (СанПиН 2.2.1/2.1.1.1200-03)."
            };

        if (FireAccess().Match(t) is { Success: true } fire)
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.FireAccess,
                Title = "Пожарный проезд" + (Num(fire.Groups["v"].Value) is { } fw ? string.Create(Ru, $": {fw:0.0#} м") : string.Empty),
                Impact = ConstraintImpact.Blocking,
                ImpactText = "Проезд пожарной техники к зданию (СП 4.13130.2013, раздел 8) — учесть в посадке и благоустройстве."
            };

        if (Living().Match(t) is { Success: true } lv && Num(lv.Groups["v"].Value) is { } living && living > 50)
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.LivingAreaLimit,
                Title = string.Create(Ru, $"Предельная общая площадь жилых помещений {living:N0} м²"),
                MaxLivingAreaM2 = living,
                Impact = ConstraintImpact.Blocking,
                ImpactText = "Общая площадь квартир не больше лимита подзоны."
            };

        var parking = Parking().Match(t);
        if (!parking.Success) parking = ParkingAlt().Match(t);
        if (parking.Success && int.TryParse(parking.Groups["v"].Value, NumberStyles.Integer, Inv, out int spots) && spots is > 0 and < 100_000)
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.Parking,
                Title = $"Машино-места: не менее {spots}",
                MinParkingSpots = spots,
                Impact = ConstraintImpact.Blocking,
                ImpactText = "Подземная автостоянка или открытые места в границах участка."
            };

        // «Количество объектов культурного наследия 0 единиц», «отсутствуют», «не выявлены» — не ограничение.
        if (Heritage().IsMatch(low) && !Absent().IsMatch(low))
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.CulturalHeritage,
                Title = "Объекты культурного наследия / зоны их охраны",
                Impact = ConstraintImpact.RequiresApproval,
                ImpactText = "Нужно заключение органа охраны ОКН (ФЗ № 73-ФЗ)."
            };

        if (Hazard().IsMatch(low) && Forbidden().IsMatch(low))
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.HazardousFacility,
                Title = "Запрет размещения опасных производственных объектов",
                Impact = ConstraintImpact.Informational,
                ImpactText = "На участке нельзя размещать ОПО (котельные на газе выше порога, АЗС и т. п.) без согласования."
            };

        if (!relocation && ProtectionZone().Match(t) is { Success: true } pz && Num(pz.Groups["v"].Value) is { } buffer && buffer is > 0 and < 500)
            yield return new ProjectConstraintItem
            {
                Category = ConstraintCategory.ProtectionZone,
                Title = "Охранная зона" + (net is { } nk ? " " + SiteBoundaryBuilder.StyleOf(nk).Title.ToLowerInvariant() : string.Empty) + string.Create(Ru, $" ± {buffer:0.0#} м"),
                BufferRadiusM = buffer,
                NetworkKind = net,
                Impact = ConstraintImpact.Blocking,
                ImpactText = "Здания не размещаются в охранной зоне без выноса сети."
            };
    }

    private static double? ProtectionDistance(string t) =>
        ProtectionZone().Match(t) is { Success: true } m ? Num(m.Groups["v"].Value) : null;

    /// <summary>Вид сети по словам предложения.</summary>
    public static GpzuZoneKind? NetworkOf(string low)
    {
        if (Regex.IsMatch(low, @"газопровод|газоснабжен|газораспредел")) return GpzuZoneKind.GasPipeline;
        if (Regex.IsMatch(low, @"тепло(сет|трасс|вых сет|снабжен)|тепловой сет|тепловых сет")) return GpzuZoneKind.HeatNetwork;
        if (Regex.IsMatch(low, @"\bлэп\b|\bвл\b|\bкл\b|кабел|электросет|\d+\s*кв\b|электропередач")) return GpzuZoneKind.PowerCable;
        if (Regex.IsMatch(low, @"водопровод|водоснабжен|водовод")) return GpzuZoneKind.WaterSupply;
        if (Regex.IsMatch(low, @"канализац|водоотвед|коллектор")) return GpzuZoneKind.Sewer;
        if (Regex.IsMatch(low, @"лини[ия]\w* связи|связи\b|телефон|оптоволок")) return GpzuZoneKind.Communication;
        return null;
    }

    private static string ClauseOf(string text, bool footnote)
    {
        if (footnote && FootnoteStart().Match(text) is { Success: true } f) return "сноска " + f.Value.Trim();
        var m = ClauseNumber().Match(text);
        return m.Success ? "п. " + m.Groups[1].Value : string.Empty;
    }

    /// <summary>Число с запятой или пробелами разрядов: «20 810», «102,0», «365.00».</summary>
    public static double? Num(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string c = Regex.Replace(s, @"[\s  ]", string.Empty).Replace(',', '.');
        return double.TryParse(c, NumberStyles.Float, Inv, out double v) && double.IsFinite(v) ? v : null;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    /// <summary>
    /// Цитата пункта: короткое предложение — целиком; длинное (строка таблицы, склеенный абзац) — окно ±150 символов вокруг
    /// числа пункта (в записи «383,50» или «383.50»), чтобы пользователь видел именно то место, откуда взято значение.
    /// </summary>
    private static string QuoteWindow(string text, ProjectConstraintItem item)
    {
        if (text.Length <= 320) return text;
        double? v = item.MaxHeightAbsM ?? item.MaxHeightM ?? item.MinSetbackM ?? item.BufferRadiusM ?? item.MaxLivingAreaM2 ?? item.MinParkingSpots;
        int at = -1;
        if (v is { } value)
            foreach (var form in new[] { value.ToString("0.00", Ru), value.ToString("0.00", Inv), value.ToString("0.#", Ru), value.ToString("N0", Ru), value.ToString("0", Inv) })
                if ((at = text.IndexOf(form, StringComparison.Ordinal)) >= 0) break;
        if (at < 0) return Trim(text, 320);
        int from = Math.Max(0, at - 150), to = Math.Min(text.Length, at + 150);
        // Окно — по границам слов.
        while (from > 0 && !char.IsWhiteSpace(text[from - 1])) from--;
        while (to < text.Length && !char.IsWhiteSpace(text[to])) to++;
        return (from > 0 ? "…" : string.Empty) + text[from..to].Trim() + (to < text.Length ? "…" : string.Empty);
    }

    /// <summary>Дубли (одинаковые категория, сеть и числа) — один пункт: первая цитата, остальные места — в пункт.</summary>
    public static List<ProjectConstraintItem> Dedupe(IEnumerable<ProjectConstraintItem> items)
    {
        var result = new List<ProjectConstraintItem>();
        foreach (var item in items)
        {
            int i = result.FindIndex(r => r.MergeKey == item.MergeKey);
            if (i < 0) { result.Add(item); continue; }
            var r = result[i];
            // Подтверждённая дословно цитата и заголовок модели (обычно точнее) побеждают; «Ядро + модель» — если согласны оба.
            string by = r.ExtractedBy == item.ExtractedBy || item.ExtractedBy.Length == 0 ? r.ExtractedBy
                : r.ExtractedBy.Contains(item.ExtractedBy, StringComparison.Ordinal) ? r.ExtractedBy : r.ExtractedBy + " + " + item.ExtractedBy;
            result[i] = r with
            {
                ExtractedBy = by,
                Title = item.Origin == r.Origin && item.ExtractedBy != CoreName && item.Title.Length > 0 ? item.Title : r.Title,
                Quote = r.QuoteVerified || !item.QuoteVerified ? r.Quote : item.Quote,
                QuoteVerified = r.QuoteVerified || item.QuoteVerified,
                Confidence = Math.Min(0.98, Math.Max(r.Confidence, item.Confidence) + 0.1),
                Clause = r.Clause.Length > 0 ? r.Clause : item.Clause,
                Page = r.Page > 0 ? r.Page : item.Page,
                // Один пункт из разных источников (норматив + страница в сети) — все источники видны пользователю.
                Source = item.Source.Length == 0 || r.Source.Contains(item.Source, StringComparison.Ordinal) || r.Source.Count(c => c == ';') >= 2
                    ? r.Source : r.Source + "; " + item.Source
            };
        }
        return result;
    }

    // ═════════════════════════════ Проход моделью ═════════════════════════════

    public const string SystemPrompt =
        "Ты — эксперт по градостроительной документации РФ (ГПЗУ, ПЗЗ, ТУ, СП, ПП РФ). Прочитай ВЕСЬ присланный текст документа, включая " +
        "сноски со звёздочками, примечания, условия «при условии», «за исключением», «по согласованию», и выпиши ВСЕ ограничения, особые " +
        "условия и «НО», влияющие на размещение и параметры здания. Не выдумывай: каждая цитата — дословно из текста. Верни ТОЛЬКО JSON: " +
        "{\"constraints\":[{\"category\":\"height|network_relocation|sanitary_zone|fire_access|living_area|parking|cultural_heritage|hazardous|setback|protection_zone|special_conditions|approval|other\"," +
        "\"title\":\"кратко по-русски\",\"quote\":\"дословная цитата\",\"page\":номер страницы или 0,\"clause\":\"пункт/сноска\"," +
        "\"maxHeightM\":число|null,\"maxHeightAbsM\":число|null,\"minSetbackM\":число|null,\"requiresNetworkRelocation\":true|false," +
        "\"minParkingSpots\":число|null,\"bufferRadiusM\":число|null,\"maxLivingAreaM2\":число|null," +
        "\"network\":\"gas|heat|power|water|sewer|communication\"|null,\"impact\":\"blocking|approval|info\",\"impactText\":\"как влияет на проект\"}]}. " +
        "Числа — в метрах, м² и штуках, без единиц.";

    /// <summary>Части текста для модели: страницы с маркерами «=== СТРАНИЦА N ===», не длиннее ChunkChars.</summary>
    public static List<string> Chunks(DocumentText doc, int max = ChunkChars)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var (page, text) in doc.Pages)
        {
            string block = $"=== СТРАНИЦА {page} ===\n{text.Trim()}\n";
            if (sb.Length > 0 && sb.Length + block.Length > max)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            while (block.Length > max)
            {
                chunks.Add(block[..max]);
                block = $"=== СТРАНИЦА {page} (продолжение) ===\n" + block[max..];
            }
            sb.Append(block);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    /// <summary>
    /// Полное чтение документа: ядро по всем предложениям + модель по всем частям текста (если задана). Модель без ответа или с
    /// неразборчивым ответом не роняет чтение — остаются пункты ядра и запись о сбое.
    /// </summary>
    public static async Task<ConstraintExtraction> ExtractAsync(DocumentText doc, ConstraintModel? model, CancellationToken ct = default)
    {
        var result = new ConstraintExtraction { Chars = doc.CharCount, Pages = doc.Pages.Count };
        var core = ExtractCore(doc);
        result.CoreCount = core.Count;
        result.Log.Add(string.Create(Ru, $"Ядро: {doc.Pages.Count} стр., {doc.Sentences.Count} предложений, {doc.Sentences.Count(s => s.IsFootnote)} сносок — пунктов {core.Count}."));
        var all = new List<ProjectConstraintItem>(core);
        if (model is not null && doc.HasText)
        {
            int n = 0;
            foreach (var chunk in Chunks(doc))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string answer = await model.Ask(SystemPrompt, "Документ «" + doc.FileName + "». Текст:\n" + chunk, ct).ConfigureAwait(false);
                    result.ModelCalls++;
                    var parsed = ParseModelItems(answer, doc, model.Title, ConstraintOrigin.Document, doc.FileName);
                    n += parsed.Count;
                    all.AddRange(parsed);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or AI.AiProviderException or TaskCanceledException)
                {
                    result.ModelError = ex.Message;
                    result.Log.Add($"{model.Title}: нет ответа ({ex.Message}) — остаются пункты ядра.");
                    break;
                }
            }
            result.ModelCount = n;
            result.Log.Add($"{model.Title}: частей текста {result.ModelCalls}, пунктов {n} (цитаты сверены с документом).");
        }
        foreach (var w in doc.Warnings) result.Log.Add(w);
        result.Items.AddRange(Dedupe(all).OrderBy(i => i.Page).ThenBy(i => i.Category));
        return result;
    }

    /// <summary>Разбор ответа модели: JSON {"constraints":[…]} или массив; цитата сверяется с текстом документа.</summary>
    public static List<ProjectConstraintItem> ParseModelItems(string answer, DocumentText? doc, string modelTitle, ConstraintOrigin origin, string source)
    {
        var list = new List<ProjectConstraintItem>();
        if (ExtractJson(answer) is not { } json) return list;
        using var j = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var arr = j.RootElement.ValueKind == JsonValueKind.Array ? j.RootElement
            : j.RootElement.TryGetProperty("constraints", out var c) ? c
            : j.RootElement.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Array);
        if (arr.ValueKind != JsonValueKind.Array) return list;
        string normDoc = doc is null ? string.Empty : Normalize(doc.FullText);
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string quote = Str(e, "quote");
            bool verified = doc is not null && quote.Length >= 8 && (normDoc.Contains(Normalize(quote), StringComparison.Ordinal)
                || Normalize(quote).Length > 40 && normDoc.Contains(Normalize(quote)[..40], StringComparison.Ordinal));
            var item = new ProjectConstraintItem
            {
                Category = CategoryOf(Str(e, "category")),
                Title = Str(e, "title"),
                Quote = Trim(quote, 400),
                Page = (int)(Dbl(e, "page") ?? 0),
                Clause = Str(e, "clause"),
                Source = Str(e, "source") is { Length: > 0 } s ? s : source,
                Origin = origin,
                ExtractedBy = modelTitle,
                MaxHeightM = Dbl(e, "maxHeightM"),
                MaxHeightAbsM = Dbl(e, "maxHeightAbsM"),
                MinSetbackM = Dbl(e, "minSetbackM"),
                RequiresNetworkRelocation = e.TryGetProperty("requiresNetworkRelocation", out var rr) && rr.ValueKind == JsonValueKind.True,
                MinParkingSpots = Dbl(e, "minParkingSpots") is { } p ? (int)p : null,
                BufferRadiusM = Dbl(e, "bufferRadiusM"),
                MaxLivingAreaM2 = Dbl(e, "maxLivingAreaM2"),
                NetworkKind = NetworkCode(Str(e, "network")),
                Impact = Str(e, "impact").ToLowerInvariant() switch { "blocking" => ConstraintImpact.Blocking, "approval" => ConstraintImpact.RequiresApproval, _ => ConstraintImpact.Informational },
                ImpactText = Str(e, "impactText"),
                QuoteVerified = verified || origin == ConstraintOrigin.Web,
                Confidence = verified ? 0.85 : origin == ConstraintOrigin.Web ? 0.6 : 0.4,
                IsFootnote = quote.TrimStart().StartsWith('*') || Str(e, "clause").Contains("сноск", StringComparison.OrdinalIgnoreCase)
            };
            if (item.Title.Length == 0 && item.Quote.Length == 0) continue;
            if (item.Title.Length == 0) item = item with { Title = Trim(item.Quote, 80) };
            list.Add(item);
        }
        return list;
    }

    public static string? ExtractJson(string text)
    {
        string t = Regex.Replace(text ?? string.Empty, @"<(think|thinking|reasoning)>[\s\S]*?</\1>", string.Empty, RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"```(?:json)?", string.Empty, RegexOptions.IgnoreCase);
        int o = t.IndexOf('{'), a = t.IndexOf('[');
        int start = o < 0 ? a : a < 0 ? o : Math.Min(o, a);
        if (start < 0) return null;
        char close = t[start] == '{' ? '}' : ']';
        int end = t.LastIndexOf(close);
        if (end <= start) return null;
        string candidate = t[start..(end + 1)];
        try
        {
            using var _ = JsonDocument.Parse(candidate, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return candidate;
        }
        catch (JsonException) { return null; }
    }

    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant().Replace('ё', 'е').Replace(' ', ' '), @"[\s«»""'„“”]+", " ").Trim();

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? string.Empty, JsonValueKind.Number => v.GetRawText(), _ => string.Empty } : string.Empty;

    private static double? Dbl(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d)) return d;
        if (v.ValueKind == JsonValueKind.String) return Num(Regex.Replace(v.GetString() ?? string.Empty, @"[^\d.,\s-]", string.Empty));
        return null;
    }

    public static ConstraintCategory CategoryOf(string code) => code.ToLowerInvariant() switch
    {
        var c when c.Contains("height") || c.Contains("высот") => ConstraintCategory.HeightLimit,
        var c when c.Contains("reloc") || c.Contains("вынос") || c.Contains("network") => ConstraintCategory.NetworkRelocation,
        var c when c.Contains("sanit") || c.Contains("сзз") || c.Contains("санитар") => ConstraintCategory.SanitaryZone,
        var c when c.Contains("fire") || c.Contains("пожар") => ConstraintCategory.FireAccess,
        var c when c.Contains("living") || c.Contains("жил") => ConstraintCategory.LivingAreaLimit,
        var c when c.Contains("park") || c.Contains("машино") => ConstraintCategory.Parking,
        var c when c.Contains("herit") || c.Contains("наслед") => ConstraintCategory.CulturalHeritage,
        var c when c.Contains("hazard") || c.Contains("опо") => ConstraintCategory.HazardousFacility,
        var c when c.Contains("setback") || c.Contains("отступ") => ConstraintCategory.Setback,
        var c when c.Contains("protect") || c.Contains("охран") => ConstraintCategory.ProtectionZone,
        var c when c.Contains("special") || c.Contains("сту") => ConstraintCategory.SpecialConditions,
        var c when c.Contains("approv") || c.Contains("соглас") => ConstraintCategory.Approval,
        _ => ConstraintCategory.Other
    };

    public static GpzuZoneKind? NetworkCode(string code) => code.ToLowerInvariant() switch
    {
        "gas" or "газ" => GpzuZoneKind.GasPipeline,
        "heat" or "тепло" => GpzuZoneKind.HeatNetwork,
        "power" or "электро" or "лэп" => GpzuZoneKind.PowerCable,
        "water" or "вода" => GpzuZoneKind.WaterSupply,
        "sewer" or "канализация" => GpzuZoneKind.Sewer,
        "communication" or "связь" => GpzuZoneKind.Communication,
        _ => null
    };

    public static string CategoryTitle(ConstraintCategory c) => c switch
    {
        ConstraintCategory.HeightLimit => "Высотный лимит",
        ConstraintCategory.NetworkRelocation => "Вынос сетей по ТУ",
        ConstraintCategory.SanitaryZone => "Санитарно-защитная зона",
        ConstraintCategory.FireAccess => "Пожарный проезд",
        ConstraintCategory.LivingAreaLimit => "Ограничение жилой площади",
        ConstraintCategory.Parking => "Парковки",
        ConstraintCategory.CulturalHeritage => "Культурное наследие",
        ConstraintCategory.HazardousFacility => "Запрет ОПО",
        ConstraintCategory.Setback => "Отступы от границ",
        ConstraintCategory.ProtectionZone => "Охранная зона",
        ConstraintCategory.SpecialConditions => "Особые условия / СТУ",
        ConstraintCategory.Approval => "Согласование",
        _ => "Прочее"
    };

    // ═════════════════════════════ Веб-поиск и база нормативов ═════════════════════════════

    /// <summary>Пункт базы нормативов ядра: ключевые слова (все должны встретиться хотя бы одним вариантом) и ограничение.</summary>
    public sealed record NormativeEntry(string[][] Keywords, ProjectConstraintItem Item);

    private static ProjectConstraintItem N(ConstraintCategory cat, string title, string source, string clause, string quote, GpzuZoneKind? net = null,
        double? buffer = null, double? setback = null, ConstraintImpact impact = ConstraintImpact.Blocking, string impactText = "") => new()
    {
        Category = cat, Title = title, Source = source, Clause = clause, Quote = quote, NetworkKind = net, BufferRadiusM = buffer, MinSetbackM = setback,
        Origin = ConstraintOrigin.Normative, ExtractedBy = CoreName + " · база нормативов", Impact = impact, ImpactText = impactText, QuoteVerified = true, Confidence = 0.9
    };

    /// <summary>База нормативов ядра (без сети): охранные зоны и расстояния до фундаментов по видам сетей, ЛЭП, СЗЗ, пожарные проезды.</summary>
    public static readonly IReadOnlyList<NormativeEntry> NormativeBase =
    [
        new([["газ"], ["низк"]], N(ConstraintCategory.ProtectionZone, "Газопровод низкого давления: до фундаментов зданий 2 м", "СП 42.13330.2016", "табл. 12.6",
            "Подземный газопровод давлением до 0,005 МПа — расстояние по горизонтали до фундаментов зданий 2 м.", GpzuZoneKind.GasPipeline, 2.0)),
        new([["газ"], ["средн"]], N(ConstraintCategory.ProtectionZone, "Газопровод среднего давления: до фундаментов зданий 4 м", "СП 42.13330.2016", "табл. 12.6",
            "Газопровод давлением св. 0,005 до 0,3 МПа — расстояние по горизонтали до фундаментов зданий и сооружений 4 м.", GpzuZoneKind.GasPipeline, 4.0)),
        new([["газ"], ["средн", "охранн", "низк", "высок"]], N(ConstraintCategory.ProtectionZone, "Охранная зона газораспределительной сети ± 2 м", "ПП РФ № 878 от 20.11.2000", "п. 7 «а»",
            "Вдоль трасс наружных газопроводов — территория, ограниченная условными линиями на расстоянии 2 м с каждой стороны газопровода.", GpzuZoneKind.GasPipeline, 2.0,
            impact: ConstraintImpact.RequiresApproval, impactText: "Строительство в охранной зоне — по согласованию с газораспределительной организацией.")),
        new([["газ"], ["высок"]], N(ConstraintCategory.ProtectionZone, "Газопровод высокого давления: до фундаментов 7–10 м", "СП 42.13330.2016", "табл. 12.6",
            "Св. 0,3 до 0,6 МПа — 7 м; св. 0,6 до 1,2 МПа — 10 м до фундаментов зданий.", GpzuZoneKind.GasPipeline, 7.0)),
        new([["лэп", "вл ", "электропередач", "кв"], ["110"]], N(ConstraintCategory.ProtectionZone, "Охранная зона ВЛ 110 кВ ± 20 м", "ПП РФ № 160 от 24.02.2009", "приложение, п. «а»",
            "Охранная зона ВЛ напряжением 110 кВ — 20 м от крайних проводов по обе стороны линии.", GpzuZoneKind.PowerCable, 20.0,
            impactText: "Жилые здания в охранной зоне ВЛ не размещаются; размещение — после переустройства (кабелирования) линии.")),
        new([["лэп", "вл ", "электропередач", "кв"], ["35"]], N(ConstraintCategory.ProtectionZone, "Охранная зона ВЛ 35 кВ ± 15 м", "ПП РФ № 160 от 24.02.2009", "приложение",
            "Охранная зона ВЛ напряжением 35 кВ — 15 м от крайних проводов.", GpzuZoneKind.PowerCable, 15.0)),
        new([["лэп", "вл ", "электропередач", "кв"], ["220", "150"]], N(ConstraintCategory.ProtectionZone, "Охранная зона ВЛ 150–220 кВ ± 25 м", "ПП РФ № 160 от 24.02.2009", "приложение",
            "Охранная зона ВЛ напряжением 150, 220 кВ — 25 м от крайних проводов.", GpzuZoneKind.PowerCable, 25.0)),
        new([["лэп", "вл ", "электропередач"], ["10 кв", "6 кв", "20 кв", "1-20", "до 20"]], N(ConstraintCategory.ProtectionZone, "Охранная зона ВЛ 1–20 кВ ± 10 м", "ПП РФ № 160 от 24.02.2009", "приложение",
            "Охранная зона ВЛ напряжением 1–20 кВ — 10 м (5 м — для СИП в границах населённых пунктов).", GpzuZoneKind.PowerCable, 10.0)),
        new([["кабел", "кл "]], N(ConstraintCategory.ProtectionZone, "Охранная зона кабельной линии ± 1 м", "ПП РФ № 160 от 24.02.2009", "приложение, п. «г»",
            "Подземные кабельные линии — 1 м от крайних кабелей с каждой стороны.", GpzuZoneKind.PowerCable, 1.0)),
        new([["тепл"]], N(ConstraintCategory.ProtectionZone, "Охранная зона тепловой сети ± 3 м", "Приказ Минархстроя РФ № 197 от 17.08.1992", "Типовые правила, п. 4",
            "Охранная зона — не менее 3 м от края строительной конструкции тепловой сети.", GpzuZoneKind.HeatNetwork, 3.0)),
        new([["водопровод", "водоснабжен"]], N(ConstraintCategory.ProtectionZone, "Водопровод: до фундаментов зданий 5 м", "СП 42.13330.2016", "табл. 12.6",
            "Водопровод и напорная канализация — 5 м до фундаментов зданий и сооружений.", GpzuZoneKind.WaterSupply, 5.0)),
        new([["канализац"]], N(ConstraintCategory.ProtectionZone, "Самотечная канализация: до фундаментов 3 м", "СП 42.13330.2016", "табл. 12.6",
            "Самотечная канализация (бытовая и дождевая) — 3 м до фундаментов зданий.", GpzuZoneKind.Sewer, 3.0)),
        new([["связ"]], N(ConstraintCategory.ProtectionZone, "Охранная зона линии связи ± 2 м", "ПП РФ № 578 от 09.06.1995", "п. 4 «а»",
            "Охранная зона подземных кабельных линий связи — 2 м с каждой стороны.", GpzuZoneKind.Communication, 2.0)),
        new([["санитарн", "сзз"]], N(ConstraintCategory.SanitaryZone, "Санитарно-защитные зоны I–V класса: 1000 / 500 / 300 / 100 / 50 м", "СанПиН 2.2.1/2.1.1.1200-03", "п. 7.1",
            "Ориентировочные размеры СЗЗ: I класс — 1000 м, II — 500 м, III — 300 м, IV — 100 м, V — 50 м.", impact: ConstraintImpact.Blocking,
            impactText: "В СЗЗ не допускается жилая застройка.")),
        new([["пожарн"]], N(ConstraintCategory.FireAccess, "Пожарный проезд: 5–8 м от стены (здания до 28 м), 8–10 м (выше 28 м)", "СП 4.13130.2013", "п. 8.6, 8.8",
            "Ширина проезда 3,5 м (до 13 м), 4,2 м (13–46 м), 6 м (выше 46 м); расстояние от края проезда до стены 5–8 м и 8–10 м.", setback: null,
            impactText: "Проезд закладывается в посадку и благоустройство вдоль продольной стороны здания."))
    ];

    /// <summary>Пункты базы нормативов, подходящие к запросу (все группы ключевых слов должны совпасть).</summary>
    public static List<ProjectConstraintItem> MatchNormatives(string query)
    {
        string low = " " + query.ToLowerInvariant().Replace('ё', 'е') + " ";
        return NormativeBase.Where(n => n.Keywords.All(group => group.Any(k => low.Contains(k, StringComparison.Ordinal))))
            .Select(n => n.Item with { Id = Guid.NewGuid().ToString("N")[..10] }).ToList();
    }

    /// <summary>
    /// Поиск ограничений «в интернете» по запросу пользователя: база нормативов ядра + поисковый HTTP-экстрактор (выдача
    /// html.duckduckgo.com: заголовок, адрес, фрагмент) + правила ядра по фрагментам + синтез моделью (если задана). Сеть
    /// недоступна — остаётся база нормативов. handler — для автотеста (заглушка сети).
    /// </summary>
    public static async Task<ConstraintExtraction> SearchAsync(string query, ConstraintModel? model, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        var result = new ConstraintExtraction();
        var normative = MatchNormatives(query);
        result.Log.Add($"База нормативов ядра: {normative.Count} пунктов по запросу.");
        var all = new List<ProjectConstraintItem>(normative);
        try
        {
            var hits = await WebSearchAsync(query, handler, ct).ConfigureAwait(false);
            result.WebHits.AddRange(hits);
            result.Log.Add($"Поиск в сети: {hits.Count} источников.");
            foreach (var hit in hits)
            {
                var doc = FromText(hit.Title + ". " + hit.Snippet, hit.Url);
                all.AddRange(ExtractCore(doc, ConstraintOrigin.Web, hit.Url).Select(i => i with { Confidence = 0.55, ExtractedBy = CoreName + " · поиск" }));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            result.Log.Add("Поиск в сети недоступен: " + ex.Message + " — остаются нормативы ядра.");
        }
        if (model is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Запрос пользователя: " + query);
            sb.AppendLine("Нормативы ядра:");
            foreach (var n in normative) sb.AppendLine($"- {n.Title} ({n.Source}, {n.Clause}): {n.Quote}");
            sb.AppendLine("Найденные источники:");
            foreach (var h in result.WebHits.Take(8)) sb.AppendLine($"- {h.Title} | {h.Url} | {h.Snippet}");
            sb.AppendLine("Сформируй список действующих ограничений по запросу. Для каждого укажи \"source\" (норматив или адрес) и \"clause\". " +
                          "Цитата — из нормативов или фрагментов выше.");
            try
            {
                string answer = await model.Ask(SystemPrompt, sb.ToString(), ct).ConfigureAwait(false);
                result.ModelCalls++;
                var parsed = ParseModelItems(answer, null, model.Title, ConstraintOrigin.Web, "веб-поиск");
                result.ModelCount = parsed.Count;
                all.AddRange(parsed);
                result.Log.Add($"{model.Title}: синтез — {parsed.Count} пунктов.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or AI.AiProviderException or TaskCanceledException)
            {
                result.ModelError = ex.Message;
                result.Log.Add($"{model.Title}: нет ответа ({ex.Message}).");
            }
        }
        result.Items.AddRange(Dedupe(all));
        return result;
    }

    /// <summary>Поисковая выдача (HTML-версия DuckDuckGo): до 8 результатов, ссылки-переадресации раскрываются.</summary>
    public static async Task<List<WebSearchHit>> WebSearchAsync(string query, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ArchitectStudioPro/1.0");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9");
        string url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
        string html = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        return ParseSearchHtml(html);
    }

    public static List<WebSearchHit> ParseSearchHtml(string html)
    {
        var hits = new List<WebSearchHit>();
        var links = ResultLink().Matches(html);
        var snippets = ResultSnippet().Matches(html);
        for (int i = 0; i < links.Count && hits.Count < 8; i++)
        {
            string href = WebUtility.HtmlDecode(links[i].Groups["href"].Value);
            if (href.Contains("uddg=", StringComparison.Ordinal))
                href = Uri.UnescapeDataString(Regex.Match(href, @"uddg=([^&]+)").Groups[1].Value);
            if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
            string title = StripTags(links[i].Groups["t"].Value);
            string snippet = i < snippets.Count ? StripTags(snippets[i].Groups["t"].Value) : string.Empty;
            if (title.Length > 0) hits.Add(new WebSearchHit(title, href, snippet));
        }
        return hits;
    }

    private static string StripTags(string s) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", string.Empty)), @"\s+", " ").Trim();

    // ═════════════════════════════ Уточнение и ручная правка ═════════════════════════════

    /// <summary>
    /// «Повторить поиск с уточнением»: пункт, отклонённый пользователем, перепроверяется с его комментарием. Модель получает пункт,
    /// комментарий и фрагмент документа вокруг цитаты (или самые близкие по словам комментария предложения); без модели — ядро:
    /// предложения документа со словами комментария прогоняются правилами, число из комментария («не 5, а 3 м») заменяет значение.
    /// </summary>
    public static async Task<List<ProjectConstraintItem>> RefineAsync(ProjectConstraintItem rejected, string comment, DocumentText? doc, ConstraintModel? model,
        CancellationToken ct = default)
    {
        var context = doc is null ? [] : RelevantSentences(doc, rejected.Quote + " " + comment, 12);
        if (model is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Пользователь ОТКЛОНИЛ извлечённый пункт как ошибочный. Перечитай фрагменты и верни исправленный пункт(ы) в том же JSON.");
            sb.AppendLine($"Отклонённый пункт: категория {CategoryTitle(rejected.Category)}; «{rejected.Title}»; значение {rejected.ValueText(Ru)}; цитата «{rejected.Quote}»; стр. {rejected.Page}.");
            sb.AppendLine("Комментарий пользователя (что перепутано): " + comment);
            if (context.Count > 0)
            {
                sb.AppendLine("Фрагменты документа:");
                foreach (var s in context) sb.AppendLine($"[стр. {s.Page}] {s.Text}");
            }
            try
            {
                string answer = await model.Ask(SystemPrompt, sb.ToString(), ct).ConfigureAwait(false);
                var items = ParseModelItems(answer, doc, model.Title, rejected.Origin, rejected.Source);
                if (items.Count > 0) return items.Select(i => i with { UserComment = comment, Status = ConstraintStatus.Pending }).ToList();
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or AI.AiProviderException or TaskCanceledException)
            {
                // Модель недоступна — уточнение ядром ниже.
            }
        }
        var result = new List<ProjectConstraintItem>();
        foreach (var s in context)
            foreach (var r in RulesFor(s.Text))
                result.Add(r with
                {
                    Quote = Trim(s.Text, 400), Page = s.Page, Clause = ClauseOf(s.Text, s.IsFootnote), Source = rejected.Source, Origin = rejected.Origin,
                    ExtractedBy = CoreName + " (уточнение)", QuoteVerified = true, IsFootnote = s.IsFootnote, Confidence = 0.75, UserComment = comment
                });
        result = Dedupe(result).Where(r => r.MergeKey != rejected.MergeKey).ToList();
        // Число из комментария — «правильное» значение того же параметра.
        if (LastNumber(comment) is { } value)
        {
            var fixedItem = ApplyValue(rejected, value) with
            {
                Id = Guid.NewGuid().ToString("N")[..10], Status = ConstraintStatus.Pending, UserComment = comment, ExtractedBy = CoreName + " (уточнение)", Confidence = 0.7
            };
            result.RemoveAll(r => r.Category == fixedItem.Category && r.MergeKey != fixedItem.MergeKey && SameParameter(r, rejected));
            result.Insert(0, fixedItem);
        }
        return result;
    }

    /// <summary>«Задать / исправить вручную»: пользователь вводит правильное значение параметра пункта.</summary>
    public static ProjectConstraintItem Manual(ProjectConstraintItem item, double? value, string? title = null, bool? relocation = null) =>
        (value is { } v ? ApplyValue(item, v) : item) with
        {
            Title = title is { Length: > 0 } t ? t : item.Title,
            RequiresNetworkRelocation = relocation ?? item.RequiresNetworkRelocation,
            Status = ConstraintStatus.Manual,
            Origin = ConstraintOrigin.Manual,
            ExtractedBy = "Пользователь",
            Confidence = 1.0
        };

    /// <summary>Главный числовой параметр пункта (для поля ручного ввода): имя и значение.</summary>
    public static (string Name, double? Value) PrimaryValue(ProjectConstraintItem i) => i switch
    {
        { MaxHeightAbsM: { } v } => ("Hабс, м БС", v),
        { MaxHeightM: { } v } => ("H, м", v),
        { MinSetbackM: { } v } => ("Отступ, м", v),
        { BufferRadiusM: { } v } => ("Зона, м", v),
        { MinParkingSpots: { } v } => ("Машино-мест", v),
        { MaxLivingAreaM2: { } v } => ("Жильё, м²", v),
        { Category: ConstraintCategory.HeightLimit } => ("H, м", null),
        { Category: ConstraintCategory.Setback or ConstraintCategory.FireAccess } => ("Отступ, м", null),
        { Category: ConstraintCategory.Parking } => ("Машино-мест", null),
        { Category: ConstraintCategory.LivingAreaLimit } => ("Жильё, м²", null),
        _ => ("Зона, м", null)
    };

    private static ProjectConstraintItem ApplyValue(ProjectConstraintItem i, double v) => i switch
    {
        { MaxHeightAbsM: not null } => i with { MaxHeightAbsM = v },
        { MaxHeightM: not null } => i with { MaxHeightM = v },
        { MinSetbackM: not null } => i with { MinSetbackM = v },
        { BufferRadiusM: not null } => i with { BufferRadiusM = v },
        { MinParkingSpots: not null } => i with { MinParkingSpots = (int)Math.Round(v) },
        { MaxLivingAreaM2: not null } => i with { MaxLivingAreaM2 = v },
        { Category: ConstraintCategory.HeightLimit } => v > 150 ? i with { MaxHeightAbsM = v } : i with { MaxHeightM = v },
        { Category: ConstraintCategory.Setback or ConstraintCategory.FireAccess } => i with { MinSetbackM = v },
        { Category: ConstraintCategory.Parking } => i with { MinParkingSpots = (int)Math.Round(v) },
        { Category: ConstraintCategory.LivingAreaLimit } => i with { MaxLivingAreaM2 = v },
        _ => i with { BufferRadiusM = v }
    };

    private static bool SameParameter(ProjectConstraintItem a, ProjectConstraintItem b) =>
        a.MaxHeightM.HasValue == b.MaxHeightM.HasValue && a.MaxHeightAbsM.HasValue == b.MaxHeightAbsM.HasValue && a.MinSetbackM.HasValue == b.MinSetbackM.HasValue &&
        a.BufferRadiusM.HasValue == b.BufferRadiusM.HasValue;

    private static double? LastNumber(string s)
    {
        var all = Regex.Matches(s ?? string.Empty, @"\d+(?:[.,]\d+)?");
        return all.Count == 0 ? null : Num(all[^1].Value);
    }

    /// <summary>Предложения документа, ближайшие к тексту по общим словам (≥ 4 букв).</summary>
    public static List<DocumentSentence> RelevantSentences(DocumentText doc, string probe, int take)
    {
        var words = Regex.Matches(probe.ToLowerInvariant(), @"[а-яёa-z]{4,}").Select(m => m.Value[..Math.Min(6, m.Value.Length)]).Distinct().ToList();
        if (words.Count == 0) return [];
        return doc.Sentences.Select(s => (s, score: words.Count(w => s.Text.ToLowerInvariant().Contains(w, StringComparison.Ordinal))))
            .Where(t => t.score > 0).OrderByDescending(t => t.score).ThenBy(t => t.s.Page).Take(take).Select(t => t.s).ToList();
    }

    // ═════════════════════════════ Регулярные выражения ═════════════════════════════

    [GeneratedRegex(@"(?<=[.;!?])\s+(?=[А-ЯЁA-Z*«(\d])")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"^\s*\*{1,3}\s?")]
    private static partial Regex FootnoteStart();

    [GeneratedRegex(@"(?:[НH]\s*абс|абсолютн\w*\s+(?:высот\w*|отметк\w*))[^\d]{0,40}?(?<v>\d{2,4}(?:[.,]\d{1,2})?)\s*м", RegexOptions.IgnoreCase)]
    private static partial Regex AbsHeight();

    [GeneratedRegex(@"(?:предельн\w*|максимальн\w*)\s+(?:\w+\s+){0,3}?высот\w*[^\d]{0,60}?(?<v>\d{1,3}(?:[.,]\d{1,2})?)\s*м(?![²2]|\s*БС)|высот\w*\s+(?:\w+\s+){0,4}?не\s+(?:более|выше|должна\s+превышать)\s+(?<v>\d{1,3}(?:[.,]\d{1,2})?)\s*м(?![²2]|\s*БС)", RegexOptions.IgnoreCase)]
    private static partial Regex RelHeight();

    [GeneratedRegex(@"отступ\w*[^.;]{0,90}?(?:не\s+менее|≥|составляет|—|-)?\s*(?<v>\d{1,2}(?:[.,]\d{1,2})?)\s*м(?![²2])", RegexOptions.IgnoreCase)]
    private static partial Regex Setback();

    [GeneratedRegex(@"вынос\w*|переустройств\w*|перекладк\w*|перенос\w*\s+(?:сет|газ|кабел|тепл|водо)")]
    private static partial Regex Relocation();

    [GeneratedRegex(@"сет[ие]й?\b|сетей|инженерн\w+\s+коммуникац|трасс")]
    private static partial Regex NetworkWord();

    [GeneratedRegex(@"технически\w*\s+услови")]
    private static partial Regex Tu();

    [GeneratedRegex(@"\bСТУ\b|специальн\w*\s+техническ\w*\s+услови", RegexOptions.IgnoreCase)]
    private static partial Regex Stu();

    [GeneratedRegex(@"росавиаци|федеральн\w+\s+агентств\w+\s+воздушн|оператор\w*\s+аэродром|приаэродромн|аэродром")]
    private static partial Regex Aviation();

    [GeneratedRegex(@"согласован|по\s+согласованию|согласовать|согласовани")]
    private static partial Regex Agreement();

    [GeneratedRegex(@"санитарно-защитн\w*\s+зон\w*(?:[^.;]{0,80}?(?<v>\d{1,4})\s*м(?![²2]))?", RegexOptions.IgnoreCase)]
    private static partial Regex SanitaryZone();

    [GeneratedRegex(@"(?:пожарн\w*\s+проезд\w*|проезд\w*\s+(?:для\s+)?пожарн\w*)(?:[^.;]{0,80}?(?<v>\d{1,2}(?:[.,]\d)?)\s*м(?![²2]))?", RegexOptions.IgnoreCase)]
    private static partial Regex FireAccess();

    [GeneratedRegex(@"жил\w*\s+помещени\w*[^.;]{0,100}?(?:не\s+более|не\s+должна\s+превышать|≤|до)\s*(?<v>\d[\d\s ]{1,9}(?:[.,]\d+)?)\s*(?:кв\.?\s*м|м2|м²)", RegexOptions.IgnoreCase)]
    private static partial Regex Living();

    [GeneratedRegex(@"(?:не\s+менее|≥|минимальн\w*\s+количеств\w*[^.;\d]{0,20})\s*(?<v>\d{1,5})\s*машино-мест", RegexOptions.IgnoreCase)]
    private static partial Regex Parking();

    [GeneratedRegex(@"машино-мест\w*[^.;\d]{0,60}?(?:не\s+менее|≥)\s*(?<v>\d{1,5})", RegexOptions.IgnoreCase)]
    private static partial Regex ParkingAlt();

    [GeneratedRegex(@"объект\w*\s+культурн\w*\s+наследи|зон\w*\s+охраны\s+объект")]
    private static partial Regex Heritage();

    [GeneratedRegex(@"\b0\s+единиц|отсутству|не\s+выявлен|не\s+имеется|не\s+расположен|нет\s+объект")]
    private static partial Regex Absent();

    [GeneratedRegex(@"опасн\w*\s+производственн\w*\s+объект|\bопо\b")]
    private static partial Regex Hazard();

    [GeneratedRegex(@"запрещ|не\s+допуска|не\s+разрешается")]
    private static partial Regex Forbidden();

    [GeneratedRegex(@"охранн\w*\s+зон\w*[^.;]{0,100}?(?<v>\d{1,3}(?:[.,]\d{1,2})?)\s*м(?![²2])", RegexOptions.IgnoreCase)]
    private static partial Regex ProtectionZone();

    [GeneratedRegex(@"(?:^|\s)(?:п\.|пункт)\s*(\d+(?:\.\d+){0,3})", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseNumber();

    [GeneratedRegex(@"<a[^>]+class=""result__a""[^>]+href=""(?<href>[^""]+)""[^>]*>(?<t>[\s\S]*?)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex ResultLink();

    [GeneratedRegex(@"class=""result__snippet""[^>]*>(?<t>[\s\S]*?)</(?:a|div|td)>", RegexOptions.IgnoreCase)]
    private static partial Regex ResultSnippet();
}
