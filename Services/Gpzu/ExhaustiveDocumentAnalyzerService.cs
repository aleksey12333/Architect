using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services.Pdf;

namespace AIArchitectStudio.Services.Gpzu;

/// <summary>Смысловой блок анализа: А — идентификация и ПЗЗ (разд. 1–2), Б — ОКС, ОКН, снос (3–4), В — ЗОУИТ, ПАТ, сети, ТУ (5, 9),
/// Г — координаты, Приложение № 1, сервитуты, красные линии, облик (6–8, 10–12) и сверка с топосъёмкой.</summary>
public enum AnalysisBlock { A, B, C, D }

/// <summary>Раздел (подраздел) документа: номер «2.3», заголовок, страницы, полный текст и блок анализа.</summary>
public sealed record DocumentSection(string Number, string Title, int Page, int EndPage, int Level, string Text, AnalysisBlock Block, bool ByHeading);

/// <summary>Подстрочное примечание со звёздочкой (*, **): маркер, страница, полный текст с продолжением, раздел.</summary>
public sealed record DocumentFootnote(string Marker, int Page, string Text, string Section);

/// <summary>Таблица документа: координатная (Приложение № 1), регламент, ТУ, экспликация.</summary>
public sealed record DocumentTableInfo(string Title, int FirstPage, int LastPage, int Rows, string Kind);

/// <summary>Графическая страница (ситуационный план, чертёж ГПЗУ, схема сетей): отрезков, подписей, снимок высокого разрешения.</summary>
public sealed record DrawingPageInfo(int Page, string Title, int Segments, int TextRuns, byte[]? Image);

/// <summary>Сведение документа по разделу: установлено / не подлежит установлению / отсутствует / справочно — с цитатой и местом.</summary>
public enum FactStatus { Established, NotEstablished, Absent, Informational }

public sealed record DocumentFact(AnalysisBlock Block, string Key, string Title, string Value, FactStatus Status, string Quote, int Page, string Clause,
    string ExtractedBy, bool Verified);

/// <summary>Замечание сверки полноты: раздел, сноска, реестровый номер, цитата или число, которые нужно было найти.</summary>
public sealed record CoverageCheck(string Kind, string Title, int Page, bool Passed, string Detail);

/// <summary>Состояние этапа конвейера для индикатора: номер 1…4, название, подробность, доля всего анализа.</summary>
public sealed record AnalysisProgress(int Stage, string Title, string Detail, double Fraction);

/// <summary>Итог этапа: название, длительность, сводка.</summary>
public sealed record AnalysisStageResult(int Number, string Title, string Summary, TimeSpan Elapsed);

/// <summary>Структура документа после этапа 1: страницы, разделы, сноски, таблицы, графические страницы, реестровые номера ЗОУИТ.</summary>
public sealed class DocumentStructure
{
    public string FileName { get; init; } = string.Empty;
    public List<(int Page, List<string> Lines)> Pages { get; } = [];
    public List<DocumentSection> Sections { get; } = [];
    public List<DocumentFootnote> Footnotes { get; } = [];
    public List<DocumentTableInfo> Tables { get; } = [];
    public List<DrawingPageInfo> Drawings { get; } = [];
    public List<(string Number, int Page)> RegistryNumbers { get; } = [];
    public List<string> Warnings { get; } = [];
    public GpzuDocument? Gpzu { get; set; }
    public TopoSurvey? Topo { get; set; }
    public bool IsGpzu { get; set; }
    public int PageCount => Pages.Count;
    public int CharCount => Pages.Sum(p => p.Lines.Sum(l => l.Length + 1));

    public string PageText(int page) => Pages.FirstOrDefault(p => p.Page == page).Lines is { } l ? string.Join("\n", l) : string.Empty;

    public IEnumerable<int> TopSections => Sections.Where(s => s.Level == 1).Select(s => int.TryParse(s.Number, out int n) ? n : 0).Where(n => n > 0).Distinct();
}

/// <summary>Итог глубокого анализа документа.</summary>
public sealed class ExhaustiveAnalysisReport
{
    public required DocumentStructure Structure { get; init; }
    public List<DocumentFact> Facts { get; } = [];
    public List<ProjectConstraintItem> Items { get; } = [];
    public List<CoverageCheck> Coverage { get; } = [];
    public List<string> Negations { get; } = [];
    public List<AnalysisStageResult> Stages { get; } = [];
    public List<string> Log { get; } = [];
    public int ModelCalls { get; set; }
    public string? ModelError { get; set; }
    public string? ModelTitle { get; set; }
    public TimeSpan Elapsed { get; set; }

    public int SectionsExpected => Structure.IsGpzu ? 12 : Structure.TopSections.DefaultIfEmpty(0).Max();
    public int SectionsFound => Structure.TopSections.Count();
    public int FootnotesTotal => Structure.Footnotes.Count;
    public int FootnotesCovered => Coverage.Count(c => c.Kind == "footnote" && c.Passed);
    public int RegistryTotal => Coverage.Count(c => c.Kind == "registry");
    public int RegistryMatched => Coverage.Count(c => c.Kind == "registry" && c.Passed);
    public int QuotesTotal => Coverage.Count(c => c.Kind == "quote");
    public int QuotesVerified => Coverage.Count(c => c.Kind == "quote" && c.Passed);
    public bool Complete => Coverage.All(c => c.Passed);

    /// <summary>Совместимость с окном подтверждения: пункты ограничений + журнал.</summary>
    public ConstraintExtraction ToExtraction()
    {
        var e = new ConstraintExtraction { Chars = Structure.CharCount, Pages = Structure.PageCount, ModelCalls = ModelCalls, ModelError = ModelError };
        e.Items.AddRange(Items);
        e.Log.AddRange(Log);
        e.CoreCount = Items.Count(i => i.ExtractedBy.StartsWith(DeepDocumentConstraintService.CoreName, StringComparison.Ordinal));
        e.ModelCount = Items.Count - e.CoreCount;
        return e;
    }
}

/// <summary>
/// Волна 7 — глубокий 4-этапный анализ градостроительной документации (ГПЗУ, ТУ, пояснительные записки, топосъёмка). Тщательность
/// важнее скорости: документ изучается от первой до последней строки, ничего не сжимается в один короткий запрос.
/// <list type="number">
/// <item>Декомпозиция: текст каждой страницы по строкам, иерархия разделов 1–12 и Приложений, таблицы координат и регламента,
/// все сноски «*», «**» с продолжением, экспликация, графические страницы (ситуационный план, чертёж) — снимок 2000 px для модели со зрением.</item>
/// <item>Пораздельный семантический аудит: блоки А (разд. 1–2: кадастр, зона и подзона, ВРИ, предельные параметры и сноски к 2.3),
/// Б (разд. 3–4: ОКС с кадастровыми номерами, ОКН, снос), В (разд. 5, 9: каждая ЗОУИТ 66:00-6.*, секторы ПАТ с Hабс, вынос сетей
/// по ПП № 160, СТУ, согласования, ТУ), Г (разд. 6–8, 10–12, Приложение № 1: координатные таблицы, сервитуты, красные линии, облик) —
/// правила ядра и, при подключённой модели, отдельный запрос по каждому блоку (рассуждение «high»).</item>
/// <item>Перекрёстная верификация: все разделы найдены, каждая сноска и каждый реестровый номер попали в ведомость, каждая цитата и
/// каждое число есть на указанной странице; отрицания («0 единиц», «не подлежат установлению», «информация отсутствует») — сведения,
/// а не ограничения.</item>
/// <item>Недостающие нормативы: сеть или ссылка на акт без численного коридора (ПП № 160, № 878, СП 42.13330) — нормативное значение
/// из базы ядра с явной пометкой источника; все пункты ждут утверждения ГИПом.</item>
/// </list>
/// </summary>
public static partial class ExhaustiveDocumentAnalyzerService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string StageTitle(int stage) => stage switch
    {
        1 => "Декомпозиция структуры документа",
        2 => "Пораздельный семантический аудит",
        3 => "Перекрёстная верификация",
        _ => "Нормативы и утверждение"
    };

    public static string BlockTitle(AnalysisBlock b) => b switch
    {
        AnalysisBlock.A => "А. Идентификация и ПЗЗ (разделы 1–2)",
        AnalysisBlock.B => "Б. ОКС, ОКН и снос (разделы 3–4)",
        AnalysisBlock.C => "В. ЗОУИТ, ПАТ и сети (разделы 5, 9)",
        _ => "Г. Координаты и сверка (разделы 6–8, 10–12, Приложение № 1)"
    };

    // ═════════════════════════════ Вход ═════════════════════════════

    /// <summary>Полный анализ файла (PDF, DOCX, TXT, XML, JSON) с необязательной топосъёмкой и моделью.</summary>
    public static async Task<ExhaustiveAnalysisReport> AnalyzeFileAsync(string path, TopoSurvey? topo, ConstraintModel? model, IProgress<AnalysisProgress>? progress = null,
        bool renderDrawings = true, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        progress?.Report(new AnalysisProgress(1, StageTitle(1), "Чтение всех страниц документа…", 0.02));
        var structure = await Task.Run(() => Decompose(path, topo), ct).ConfigureAwait(false);
        if (renderDrawings && Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) && structure.Drawings.Count > 0)
        {
            progress?.Report(new AnalysisProgress(1, StageTitle(1), $"Рендер графических страниц: {structure.Drawings.Count}…", 0.12));
            await RenderDrawingsAsync(path, structure).ConfigureAwait(false);
        }
        return await AnalyzeAsync(structure, model, progress, sw, ct).ConfigureAwait(false);
    }

    /// <summary>Анализ текста (вставка, нестандартный документ, автотест): страницы — по маркерам «=== СТРАНИЦА N ===».</summary>
    public static Task<ExhaustiveAnalysisReport> AnalyzeTextAsync(DocumentText doc, ConstraintModel? model, TopoSurvey? topo = null, IProgress<AnalysisProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var structure = new DocumentStructure { FileName = doc.FileName, Topo = topo };
        foreach (var (page, text) in doc.Pages) structure.Pages.Add((page, text.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList()));
        BuildStructure(structure);
        return AnalyzeAsync(structure, model, progress, sw, ct);
    }

    private static async Task<ExhaustiveAnalysisReport> AnalyzeAsync(DocumentStructure structure, ConstraintModel? model, IProgress<AnalysisProgress>? progress, Stopwatch sw,
        CancellationToken ct)
    {
        var report = new ExhaustiveAnalysisReport { Structure = structure, ModelTitle = model?.Title };
        var stage = Stopwatch.StartNew();
        report.Stages.Add(new AnalysisStageResult(1, StageTitle(1),
            string.Create(Ru, $"страниц {structure.PageCount}, разделов {structure.TopSections.Count()} ({structure.Sections.Count} с подразделами), сносок {structure.Footnotes.Count}, ") +
            string.Create(Ru, $"таблиц {structure.Tables.Count}, графических страниц {structure.Drawings.Count}, реестровых номеров ЗОУИТ {structure.RegistryNumbers.Select(r => r.Number).Distinct().Count()}"),
            sw.Elapsed));
        report.Log.Add("Этап 1. " + report.Stages[^1].Summary);
        foreach (var w in structure.Warnings) report.Log.Add(w);

        // ── Этап 2: блоки А–Г — ядро по каждому разделу, затем модель по каждому блоку.
        stage.Restart();
        var blocks = new[] { AnalysisBlock.A, AnalysisBlock.B, AnalysisBlock.C, AnalysisBlock.D };
        for (int b = 0; b < blocks.Length; b++)
        {
            ct.ThrowIfCancellationRequested();
            var block = blocks[b];
            progress?.Report(new AnalysisProgress(2, StageTitle(2), BlockTitle(block) + " — правила ядра", 0.15 + 0.6 * b / blocks.Length));
            CoreBlock(structure, block, report);
            if (model is not null && BlockText(structure, block) is { Length: > 40 } text)
            {
                progress?.Report(new AnalysisProgress(2, StageTitle(2), BlockTitle(block) + " — " + model.Title, 0.15 + 0.6 * (b + 0.5) / blocks.Length));
                await ModelBlockAsync(structure, block, text, model, report, ct).ConfigureAwait(false);
            }
        }
        report.Stages.Add(new AnalysisStageResult(2, StageTitle(2),
            string.Create(Ru, $"сведений {report.Facts.Count}, ограничений {report.Items.Count}") + (model is null ? " · правила ядра" : string.Create(Ru, $" · {model.Title}: запросов {report.ModelCalls}") +
            (report.ModelError is { } err ? " (сбой: " + err + ")" : string.Empty)), stage.Elapsed));
        report.Log.Add("Этап 2. " + report.Stages[^1].Summary);

        // ── Этап 3: сверка полноты и цитат, отрицания.
        stage.Restart();
        progress?.Report(new AnalysisProgress(3, StageTitle(3), "Разделы, сноски, реестровые номера, цитаты и числа…", 0.8));
        Verify(structure, report);
        report.Stages.Add(new AnalysisStageResult(3, StageTitle(3),
            string.Create(Ru, $"разделов {report.SectionsFound} из {report.SectionsExpected}, сносок {report.FootnotesCovered} из {report.FootnotesTotal}, ЗОУИТ {report.RegistryMatched} из {report.RegistryTotal}, ") +
            string.Create(Ru, $"цитат сверено {report.QuotesVerified} из {report.QuotesTotal}, отрицаний учтено {report.Negations.Count}"), stage.Elapsed));
        report.Log.Add("Этап 3. " + report.Stages[^1].Summary);

        // ── Этап 4: недостающие нормативы.
        stage.Restart();
        progress?.Report(new AnalysisProgress(4, StageTitle(4), "Нормативные коридоры сетей без численных значений в документе…", 0.92));
        int added = AddNormatives(structure, report);
        report.Stages.Add(new AnalysisStageResult(4, StageTitle(4), string.Create(Ru, $"добавлено нормативных значений {added}; пунктов в ведомости {report.Items.Count} — ждут утверждения ГИПом"), stage.Elapsed));
        report.Log.Add("Этап 4. " + report.Stages[^1].Summary);
        progress?.Report(new AnalysisProgress(4, StageTitle(4), "Готово", 1));
        report.Elapsed = sw.Elapsed;
        return report;
    }

    // ═════════════════════════════ Этап 1: декомпозиция ═════════════════════════════

    public static DocumentStructure Decompose(string path, TopoSurvey? topo = null)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var s = new DocumentStructure { FileName = Path.GetFileName(path), Topo = topo };
        if (ext == ".pdf")
        {
            var pdf = PdfNativeContentExtractor.Read(path);
            foreach (var page in pdf.Pages)
            {
                var lines = GpzuParser.LinesOf(page).Select(l => l.Text.Trim()).Where(l => l.Length > 0).ToList();
                s.Pages.Add((page.Index + 1, lines));
                int segments = page.Segments.Count, runs = page.Runs.Count;
                bool drawing = segments > 600 && runs < segments / 3 || lines.Any(l => Regex.IsMatch(l, @"СИТУАЦИОННЫЙ ПЛАН|УСЛОВНЫЕ ОБОЗНАЧЕНИЯ|1\.\s*ЧЕРТЕЖ", RegexOptions.IgnoreCase)) || !page.HasTextLayer;
                if (drawing)
                    s.Drawings.Add(new DrawingPageInfo(page.Index + 1, DrawingTitle(lines, page.HasTextLayer), segments, runs, null));
            }
            int scans = pdf.Pages.Count(p => !p.HasTextLayer);
            if (scans > 0) s.Warnings.Add(string.Create(Ru, $"Страниц без текстового слоя: {scans} — их снимок передаётся модели со зрением."));
            try { s.Gpzu = GpzuParser.FromPdf(path); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException) { s.Warnings.Add("Координатные таблицы не разобраны: " + ex.Message); }
        }
        else
        {
            var doc = DeepDocumentConstraintService.Read(path);
            foreach (var (page, text) in doc.Pages) s.Pages.Add((page, text.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList()));
            s.Warnings.AddRange(doc.Warnings);
            if (ext is ".xml" or ".json" or ".txt" or ".csv")
                try { s.Gpzu = GpzuParser.Load(path); } catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or JsonException or System.Xml.XmlException) { }
        }
        BuildStructure(s);
        return s;
    }

    private static string DrawingTitle(List<string> lines, bool text)
    {
        if (!text) return "Графическая страница без текстового слоя";
        if (lines.Any(l => l.Contains("СИТУАЦИОННЫЙ ПЛАН", StringComparison.OrdinalIgnoreCase))) return "Ситуационный план";
        if (lines.Any(l => Regex.IsMatch(l, @"ЧЕРТЕЖ|Чертеж градостроительного плана", RegexOptions.IgnoreCase))) return "Чертёж ГПЗУ с экспликацией";
        if (lines.Any(l => Regex.IsMatch(l, @"сет(и|ей) инженерно|схема сетей", RegexOptions.IgnoreCase))) return "Схема сетей";
        return "Графическая страница";
    }

    [GeneratedRegex(@"^(?<n>\d{1,2})(?:\.(?<m>\d{1,2}))?\.?\s*(?<t>[А-ЯЁA-Z«(].*)$")]
    private static partial Regex HeadingRx();

    [GeneratedRegex(@"^(Информаци|Реквизит|Номер|Чертеж|Требовани|Сведени|Описани|Ограничени|Предельн|Объект|Иные)", RegexOptions.IgnoreCase)]
    private static partial Regex TopTitleRx();

    [GeneratedRegex(@"^(?<m>\*{1,3})\s*[-–—]?\s*(?<t>\S.*)$")]
    private static partial Regex FootnoteRx();

    [GeneratedRegex(@"\(раздел\s+(?<n>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SectionRefRx();

    [GeneratedRegex(@"\b\d{2}:\d{2}-6\.\d+\b")]
    private static partial Regex RegistryRx();

    /// <summary>
    /// Иерархия разделов: «N. Заголовок» (N — следующий по порядку, заголовок — «Информация…», «Реквизиты…», «Чертеж…» и т. п.),
    /// «N.M. Подраздел» внутри текущего раздела; разделы, представленные только ссылкой «(раздел N …)» в Приложении, — тоже
    /// найдены (страница ссылки). Сноски — строки «*», «**» с продолжением до следующей сноски, заголовка или пункта «- …».
    /// </summary>
    public static void BuildStructure(DocumentStructure s)
    {
        string all = string.Join("\n", s.Pages.SelectMany(p => p.Lines));
        s.IsGpzu = Regex.IsMatch(all, @"градостроительн\w+\s+план\w*\s+земельного\s+участка", RegexOptions.IgnoreCase);
        var heads = new List<(string Number, string Title, int Page, int Line, int Level, bool ByHeading)>();
        int top = 0, sub = 0, flat = 0;
        foreach (var (page, lines) in s.Pages)
        {
            for (int i = 0; i < lines.Count; i++, flat++)
            {
                string line = lines[i];
                if (Regex.IsMatch(line, @"(^|\s)1\.\s*ЧЕРТЕЖ", RegexOptions.IgnoreCase) && top == 0)
                {
                    heads.Add(("1", "Чертеж градостроительного плана земельного участка", page, flat, 1, true));
                    top = 1; sub = 0;
                    continue;
                }
                var m = HeadingRx().Match(line);
                if (!m.Success) continue;
                int n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                string title = m.Groups["t"].Value.Trim();
                if (n > 12) continue;
                if (m.Groups["m"].Success)
                {
                    int k = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
                    // ГПЗУ — строго по порядку формы; прочие документы — подраздел может открыть следующий раздел без заголовка.
                    if (n == top && k == sub + 1 || n == top + 1 && k == 1 || !s.IsGpzu && n > top)
                    {
                        if (n != top)
                        {
                            heads.Add((n.ToString(Ru), title, page, flat, 1, false));
                            top = n;
                        }
                        heads.Add(($"{n}.{k}", title, page, flat, 2, true));
                        sub = k;
                    }
                }
                else if (n == top + 1 && TopTitleRx().IsMatch(title) && title.Length >= 14)
                {
                    heads.Add((n.ToString(Ru), title, page, flat, 1, true));
                    top = n;
                    sub = 0;
                }
            }
        }
        // Текст раздела — от его заголовка до следующего заголовка того же или верхнего уровня.
        var flatLines = s.Pages.SelectMany(p => p.Lines.Select(l => (p.Page, Line: l))).ToList();
        for (int h = 0; h < heads.Count; h++)
        {
            var (number, title, page, line, level, byHeading) = heads[h];
            int end = flatLines.Count;
            for (int k = h + 1; k < heads.Count; k++)
                if (heads[k].Level <= level) { end = heads[k].Line; break; }
            // Подраздел заканчивается на следующем заголовке любого уровня.
            if (level == 2 && h + 1 < heads.Count) end = Math.Min(end, heads[h + 1].Line);
            var slice = flatLines.Skip(line).Take(Math.Max(1, end - line)).ToList();
            string text = string.Join("\n", slice.Select(x => x.Line));
            int endPage = slice.Count > 0 ? slice[^1].Page : page;
            int topNumber = int.Parse(number.Split('.')[0], CultureInfo.InvariantCulture);
            s.Sections.Add(new DocumentSection(number, title, page, endPage, level, text, BlockOf(topNumber), byHeading));
        }
        // Разделы, найденные по ссылке «(раздел N …)» в Приложении № 1 (6, 7, 11, 12 часто только там); текст приложения к разделу,
        // найденному заголовком («см. Приложение № 1»), дописывается к разделу — там бывает «Информация отсутствует».
        foreach (var (page, lines) in s.Pages)
            for (int idx = 0; idx < lines.Count; idx++)
                foreach (Match r in SectionRefRx().Matches(lines[idx]))
                {
                    string n = r.Groups["n"].Value;
                    int topNumber = int.Parse(n, CultureInfo.InvariantCulture);
                    if (topNumber is < 1 or > 12) continue;
                    string text = string.Join("\n", lines.Skip(Math.Max(0, idx - 1)).Take(5));
                    int at = s.Sections.FindIndex(x => x.Level == 1 && x.Number == n);
                    if (at >= 0)
                    {
                        var sec = s.Sections[at];
                        if (!sec.Text.Contains(text, StringComparison.Ordinal)) s.Sections[at] = sec with { Text = sec.Text + "\n[Приложение № 1, стр. " + page.ToString(Ru) + "]\n" + text };
                        continue;
                    }
                    s.Sections.Add(new DocumentSection(n, GpzuSectionTitle(topNumber), page, page, 1, text, BlockOf(topNumber), false));
                }
        s.Sections.Sort((a, b) => CompareNumbers(a.Number, b.Number));

        // Сноски: маркер «*» в начале строки; продолжение — до пустой строки, следующей сноски, заголовка или пункта «- …»;
        // раздел сноски — последний заголовок перед её строкой.
        var pageOffset = new Dictionary<int, int>();
        int offset = 0;
        foreach (var (page, lines) in s.Pages) { pageOffset[page] = offset; offset += lines.Count; }
        foreach (var (page, lines) in s.Pages)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var f = FootnoteRx().Match(lines[i]);
                if (!f.Success) continue;
                var sb = new StringBuilder(f.Groups["t"].Value.Trim());
                int j = i + 1;
                for (; j < lines.Count; j++)
                {
                    string next = lines[j];
                    if (FootnoteRx().IsMatch(next) || next.StartsWith('-') || next.StartsWith('–') || HeadingRx().Match(next) is { Success: true } hm && TopTitleRx().IsMatch(hm.Groups["t"].Value) ||
                        Regex.IsMatch(next, @"^(Страница\s*\d+|ГПЗУ\s*№)")) break;
                    sb.Append(' ').Append(next);
                    if (sb.Length > 2400) break;
                }
                int flatAt = pageOffset[page] + i;
                string section = heads.LastOrDefault(h => h.ByHeading && h.Line < flatAt).Number ?? string.Empty;
                s.Footnotes.Add(new DocumentFootnote(f.Groups["m"].Value, page, Regex.Replace(sb.ToString(), @"\s+", " ").Trim(), section));
                i = j - 1;
            }
        }

        // Реестровые номера ЗОУИТ (66:00-6.1908 и т. п.) — со страницами.
        foreach (var (page, lines) in s.Pages)
            foreach (var l in lines)
                foreach (Match r in RegistryRx().Matches(l))
                    if (!s.RegistryNumbers.Contains((r.Value, page))) s.RegistryNumbers.Add((r.Value, page));

        // Таблицы: координатные (из разбора ГПЗУ), регламент (строка «1 2 3 4 5 6 7 8»), ТУ, экспликация чертежа.
        if (s.Gpzu is { } g)
            foreach (var t in g.Tables)
                s.Tables.Add(new DocumentTableInfo(t.Title, t.FirstPage, t.LastPage, t.PointCount, "координаты"));
        foreach (var (page, lines) in s.Pages)
        {
            if (lines.Any(l => Regex.IsMatch(l, @"^1 2 3 4 5( 6 7 8)?$")))
            {
                string kind = lines.Any(l => l.Contains("технические условия", StringComparison.OrdinalIgnoreCase)) ? "технические условия"
                    : lines.Any(l => l.Contains("Предельн", StringComparison.Ordinal)) ? "предельные параметры" : "таблица раздела";
                s.Tables.Add(new DocumentTableInfo(kind, page, page, lines.Count(l => Regex.IsMatch(l, @"^\d+\s+\S")), kind));
            }
            if (lines.Count(l => Regex.IsMatch(l, @"^\d{1,2}\s+(Место|Приаэродромная|Объект|Охранная|Зона)")) >= 3)
                s.Tables.Add(new DocumentTableInfo("экспликация чертежа", page, page, lines.Count(l => Regex.IsMatch(l, @"^\d{1,2}\s+(Место|Приаэродромная|Объект|Охранная|Зона)")), "экспликация"));
        }
    }

    /// <summary>Наименования разделов формы ГПЗУ (для разделов, представленных только в Приложении № 1).</summary>
    public static string GpzuSectionTitle(int n) => n switch
    {
        1 => "Чертёж градостроительного плана земельного участка",
        2 => "Информация о градостроительном регламенте",
        3 => "Информация об ОКС и объектах культурного наследия",
        4 => "Расчётные показатели обеспеченности инфраструктурой",
        5 => "Информация об ограничениях использования земельного участка",
        6 => "Информация о границах зон с особыми условиями использования территорий",
        7 => "Информация о границах публичных сервитутов",
        8 => "Элемент планировочной структуры",
        9 => "Информация о технических условиях подключения",
        10 => "Реквизиты актов о благоустройстве территории",
        11 => "Информация о красных линиях",
        _ => "Требования к архитектурно-градостроительному облику ОКС"
    };

    private static int CompareNumbers(string a, string b)
    {
        var pa = a.Split('.').Select(x => int.TryParse(x, out int v) ? v : 0).ToArray();
        var pb = b.Split('.').Select(x => int.TryParse(x, out int v) ? v : 0).ToArray();
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : -1, y = i < pb.Length ? pb[i] : -1;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public static AnalysisBlock BlockOf(int section) => section switch
    {
        1 or 2 => AnalysisBlock.A,
        3 or 4 => AnalysisBlock.B,
        5 or 9 => AnalysisBlock.C,
        _ => AnalysisBlock.D
    };

    /// <summary>Текст блока: все его разделы, сноски на их страницах; для блока Г — ещё Приложение и таблицы координат.</summary>
    public static string BlockText(DocumentStructure s, AnalysisBlock block)
    {
        var sb = new StringBuilder();
        foreach (var sec in s.Sections.Where(x => x.Block == block && x.Level == 1).OrderBy(x => x.Page))
            sb.Append("=== РАЗДЕЛ ").Append(sec.Number).Append(" (стр. ").Append(sec.Page.ToString(Ru)).Append(") ===\n").Append(sec.Text).Append('\n');
        if (sb.Length == 0 && block == AnalysisBlock.A)
            foreach (var (page, lines) in s.Pages.Take(3)) sb.Append($"=== СТРАНИЦА {page} ===\n").Append(string.Join("\n", lines)).Append('\n');
        foreach (var f in s.Footnotes.Where(f => BlockOf(SectionTop(f.Section)) == block))
            sb.Append("=== СНОСКА ").Append(f.Marker).Append(" (стр. ").Append(f.Page.ToString(Ru)).Append(") ===\n").Append(f.Text).Append('\n');
        return sb.ToString();
    }

    private static int SectionTop(string number) => int.TryParse(number.Split('.')[0], out int n) ? n : 1;

    /// <summary>Снимки графических страниц (2000 px) — для модели со зрением и визуальной сверки.</summary>
    private static async Task RenderDrawingsAsync(string path, DocumentStructure s)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            for (int i = 0; i < s.Drawings.Count && i < 4; i++)
            {
                var d = s.Drawings[i];
                if (d.Page - 1 >= pdf.PageCount) continue;
                var images = await AI.PdfBlueprintExtractor.RenderForAiAsync(pdf, [d.Page - 1]);
                if (images.Count > 0) s.Drawings[i] = d with { Image = images[0].Bytes };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            s.Warnings.Add("Графические страницы не отрисованы: " + ex.Message);
        }
    }

    // ═════════════════════════════ Этап 2: ядро по блокам ═════════════════════════════

    [GeneratedRegex(@"не\s+подлеж\w*\s+установлени|не\s+установлен|информация\s+отсутствует|отсутству\w+|\b0\s+единиц|не\s+имеется|не\s+выявлен|не\s+утвержден|не\s+распространя", RegexOptions.IgnoreCase)]
    private static partial Regex NegationRx();

    public static bool IsNegated(string text) => NegationRx().IsMatch(text);

    private static void Fact(ExhaustiveAnalysisReport r, AnalysisBlock block, string key, string title, string value, FactStatus status, string quote, int page, string clause)
    {
        if (r.Facts.Any(f => f.Key == key && f.Value == value)) return;
        r.Facts.Add(new DocumentFact(block, key, title, value, status, Short(quote, 360), page, clause, DeepDocumentConstraintService.CoreName, true));
    }

    private static (string Line, int Page)? FindLine(DocumentStructure s, Func<string, bool> match, int fromPage = 0)
    {
        foreach (var (page, lines) in s.Pages.Where(p => p.Page >= fromPage))
            foreach (var l in lines)
                if (match(l)) return (l, page);
        return null;
    }

    /// <summary>Предложения раздела (склейка строк до точки с запятой / точки): основа отрицаний и числовых правил.</summary>
    private static IEnumerable<(string Text, int Page)> Sentences(DocumentStructure s, DocumentSection sec)
    {
        var buf = new StringBuilder();
        int page = sec.Page;
        foreach (var (p, lines) in s.Pages.Where(x => x.Page >= sec.Page && x.Page <= sec.EndPage))
            foreach (var raw in lines)
            {
                if (!sec.Text.Contains(raw, StringComparison.Ordinal)) continue;
                if (buf.Length == 0) page = p;
                buf.Append(raw).Append(' ');
                if (Regex.IsMatch(raw, @"[.;:]$") || raw.Length < 60)
                {
                    string t = Regex.Replace(buf.ToString(), @"\s+", " ").Trim();
                    buf.Clear();
                    if (t.Length >= 6) yield return (t, page);
                }
            }
        if (buf.Length > 0) yield return (Regex.Replace(buf.ToString(), @"\s+", " ").Trim(), page);
    }

    private static void CoreBlock(DocumentStructure s, AnalysisBlock block, ExhaustiveAnalysisReport r)
    {
        var g = s.Gpzu;
        switch (block)
        {
            case AnalysisBlock.A:
            {
                if (g is not null && g.Cadastral.Length > 0 && FindLine(s, l => l.Contains(g.Cadastral, StringComparison.Ordinal)) is { } cad)
                    Fact(r, block, "cadastral", "Кадастровый номер земельного участка", g.Cadastral, FactStatus.Established, cad.Line, cad.Page, "раздел «Кадастровый номер»");
                if (g is not null && g.DeclaredArea > 0 && FindLine(s, l => Regex.IsMatch(l, @"^\d[\d\s]*м²|площадь", RegexOptions.IgnoreCase) && l.Contains(((int)g.DeclaredArea).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) is { } area)
                    Fact(r, block, "area", "Площадь земельного участка", string.Create(Ru, $"{g.DeclaredArea:N0} м² (по Гауссу {g.GaussArea:N2} м²)"), FactStatus.Established, area.Line, area.Page, "титульный лист");
                if (g is not null && g.Regulation.ZoneCode.Length > 0 &&
                    (FindLine(s, l => l.Contains("Территориальная зона", StringComparison.Ordinal) && l.Contains(g.Regulation.ZoneCode, StringComparison.Ordinal)) ??
                     FindLine(s, l => l.Contains(g.Regulation.ZoneCode, StringComparison.Ordinal))) is { } zone)
                    Fact(r, block, "zone", "Территориальная зона", $"{g.Regulation.ZoneCode} {g.Regulation.ZoneName}".Trim(), FactStatus.Established, zone.Line, zone.Page, SectionAt(s, zone.Page));
                if (g is not null && g.Regulation.PermittedUses.Count > 0)
                    Fact(r, block, "vri-main", "Основные виды разрешённого использования", string.Join("; ", g.Regulation.PermittedUses), FactStatus.Established,
                        string.Join("; ", g.Regulation.PermittedUses.Take(4)), s.Sections.FirstOrDefault(x => x.Number == "2.2")?.Page ?? 0, "п. 2.2");
                // Условно разрешённые и вспомогательные ВРИ — списки после соответствующих подзаголовков раздела 2.2.
                if (s.Sections.FirstOrDefault(x => x.Number == "2.2") is { } vri)
                {
                    var lines = vri.Text.Split('\n');
                    var lists = new List<List<string>>();
                    List<string>? current = null;
                    foreach (var l in lines.Skip(1))
                    {
                        var m = Regex.Match(l, @"^(\d{1,2})\.\s+(.+)$");
                        if (!m.Success) continue;
                        if (m.Groups[1].Value == "1") lists.Add(current = []);
                        current?.Add(m.Groups[2].Value.Trim());
                    }
                    if (lists.Count >= 2)
                        Fact(r, block, "vri-cond", "Условно разрешённые виды использования", string.Join("; ", lists[1]), FactStatus.Established, string.Join("; ", lists[1].Take(4)), vri.Page, "п. 2.2");
                    if (lists.Count >= 3)
                        Fact(r, block, "vri-aux", "Вспомогательные виды использования", string.Join("; ", lists[2]),
                            lists[2].All(IsNegated) ? FactStatus.NotEstablished : FactStatus.Established, string.Join("; ", lists[2]), vri.Page, "п. 2.2");
                }
                // Предельные параметры 2.3 (и сноски под таблицей): каждое предложение — сведение с отрицанием или значением.
                foreach (var sec in s.Sections.Where(x => x.Number is "2.3" or "2.4" or "2.5" || x.Level == 1 && x.Number == "2" && !s.Sections.Any(y => y.Number == "2.3")))
                    foreach (var (text, page) in Sentences(s, sec))
                        ParameterFact(s, r, block, text, page, "п. " + sec.Number);
                foreach (var f in s.Footnotes.Where(f => BlockOf(SectionTop(f.Section)) == AnalysisBlock.A))
                {
                    foreach (var part in Regex.Split(f.Text, @"(?<=;)\s+|(?<=[.:])\s+(?=[А-ЯЁ])"))
                        ParameterFact(s, r, block, part, f.Page, "сноска " + f.Marker);
                    if (Regex.Match(f.Text, @"подзоны\s*№\s*(?<z>[\d.]+)") is { Success: true } sz)
                        Fact(r, block, "subzone", "Подзона территориальной зоны", sz.Groups["z"].Value.TrimEnd('.'), FactStatus.Established, f.Text, f.Page, "сноска " + f.Marker);
                }
                AddCoreItems(s, r, s.Sections.Where(x => x.Block == block && x.Level == 2 || x.Block == block && !s.Sections.Any(y => y.Level == 2 && y.Number.StartsWith(x.Number + ".", StringComparison.Ordinal))),
                    s.Footnotes.Where(f => BlockOf(SectionTop(f.Section)) == block));
                break;
            }
            case AnalysisBlock.B:
            {
                foreach (var (page, lines) in s.Pages)
                    foreach (var l in lines)
                    {
                        if (Regex.Match(l, @"Количество\s+объектов\s+капитального\s+строительства\s+(?<n>\d+)") is { Success: true } oks)
                            Fact(r, block, "oks-count", "Объекты капитального строительства на участке", oks.Groups["n"].Value + " ед.", oks.Groups["n"].Value == "0" ? FactStatus.Absent : FactStatus.Established, l, page, "разд. 3");
                        if (Regex.Match(l, @"Количество\s+объектов\s+культурного\s+наследия\s+(?<n>\d+)") is { Success: true } okn)
                        {
                            bool none = okn.Groups["n"].Value == "0";
                            Fact(r, block, "okn-count", "Объекты культурного наследия", none ? "отсутствуют (0 ед.)" : okn.Groups["n"].Value + " ед.", none ? FactStatus.Absent : FactStatus.Established, l, page, "разд. 3");
                            if (none) r.Negations.Add(string.Create(Ru, $"стр. {page}: «{Short(l, 120)}» — ОКН нет, ограничение не создаётся"));
                        }
                    }
                if (s.Sections.FirstOrDefault(x => x.Number == "3.2") is { } s32 && IsNegated(s32.Text))
                    Fact(r, block, "okn-list", "Объекты в реестре ОКН (п. 3.2)", "информация отсутствует", FactStatus.Absent, FirstNegated(s32.Text), s32.Page, "п. 3.2");
                if (g is not null)
                    foreach (var o in g.Objects)
                    {
                        string status = s.Pages.SelectMany(p => p.Lines).Any(l => l.Contains(o.Cadastral.Length > 0 ? o.Cadastral : "\u0001", StringComparison.Ordinal) && Regex.IsMatch(l, "снос", RegexOptions.IgnoreCase))
                            ? "подлежит сносу" : "статус сохранения / сноса в ГПЗУ не указан — по выписке ЕГРН";
                        var at = o.Cadastral.Length > 0 ? FindLine(s, l => l.Contains(o.Cadastral, StringComparison.Ordinal)) : null;
                        Fact(r, block, "oks-" + o.Number, string.Create(Ru, $"ОКС № {o.Number}: {o.Name}"), (o.Cadastral.Length > 0 ? o.Cadastral + " · " : string.Empty) + status,
                            FactStatus.Informational, at?.Line ?? o.Name, at?.Page ?? s.Sections.FirstOrDefault(x => x.Number == "3.1")?.Page ?? 0, "п. 3.1");
                    }
                if (s.Sections.FirstOrDefault(x => x.Number == "4") is { } s4)
                    Fact(r, block, "infra", "Расчётные показатели обеспеченности инфраструктурой", IsNegated(s4.Text) ? "не установлены" : "приведены в разделе 4",
                        IsNegated(s4.Text) ? FactStatus.NotEstablished : FactStatus.Informational, Short(s4.Text.Replace('\n', ' '), 200), s4.Page, "разд. 4");
                break;
            }
            case AnalysisBlock.C:
            {
                // Каждая зона раздела 5.1 и подзона ПАТ — сведение и, если есть числа, ограничение для ядра.
                if (g is not null)
                    foreach (var z in g.Zones.Where(z => z.Kind is not GpzuZoneKind.AllowedArea))
                    {
                        string key = "zone-" + (z.Code.Length > 0 ? z.Code : z.Name);
                        bool registered = z.Code.StartsWith("66:", StringComparison.Ordinal);
                        var at = FindLine(s, l => registered ? l.Contains(z.Code, StringComparison.Ordinal) : l.Contains(Short(z.Name, 30).TrimEnd('…'), StringComparison.Ordinal));
                        int page = at?.Page ?? (Regex.Match(z.Source, @"стр\.\s*(\d+)") is { Success: true } sp ? int.Parse(sp.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
                        string name = registered && z.Name.StartsWith(z.Code, StringComparison.Ordinal) ? z.Name : (registered ? z.Code + " · " : string.Empty) + z.Name;
                        string value = name + (z.HeightLimitMin is { } hmin ? string.Create(Ru, $" · Hабс {hmin:0.00}{(z.HeightLimitMax is { } hmax && hmax > hmin + 0.005 ? $"…{hmax:0.00}" : string.Empty)} м БС") : string.Empty);
                        Fact(r, block, key, z.ExplicationNo > 0 ? $"ЗОУИТ (экспликация п. {z.ExplicationNo})" : "ЗОУИТ", value, FactStatus.Established,
                            z.Restriction.Length > 0 ? z.Restriction : at?.Line ?? z.Name, page, z.Source);
                    }
                AddCoreItems(s, r, s.Sections.Where(x => x.Block == block), s.Footnotes.Where(f => BlockOf(SectionTop(f.Section)) == block));
                // Подзоны ПАТ с отметками из таблиц Приложения — ограничения Hабс (сверка цитаты — по строке таблицы).
                if (g is not null)
                    foreach (var z in g.Zones.Where(z => z.HeightLimitMin.HasValue))
                    {
                        double h = z.HeightLimitMin!.Value;
                        if (r.Items.Any(i => i.MaxHeightAbsM is { } v && Math.Abs(v - h) < 0.005)) continue;
                        var at = FindLine(s, l => l.Contains(h.ToString("0.00", Ru), StringComparison.Ordinal) || l.Contains(h.ToString("0.00", CultureInfo.InvariantCulture), StringComparison.Ordinal));
                        r.Items.Add(new ProjectConstraintItem
                        {
                            Category = ConstraintCategory.HeightLimit,
                            Title = string.Create(Ru, $"Ограничение абсолютной высоты Hабс {h:0.00} м БС — {Short(z.Name, 60)}"),
                            MaxHeightAbsM = h,
                            Quote = at?.Line ?? z.Name,
                            Page = at?.Page ?? 0,
                            Clause = z.Source,
                            Source = s.FileName,
                            ExtractedBy = DeepDocumentConstraintService.CoreName,
                            QuoteVerified = at is not null,
                            Impact = ConstraintImpact.Blocking,
                            ImpactText = "Верх здания (с надстройками, антеннами, кранами) не выше отметки подзоны.",
                            Confidence = 0.85
                        });
                    }
                // ТУ подключения (раздел 9): «технические условия отсутствуют» — сведение (ТУ нужно получить), не отрицание запрета.
                if (s.Sections.FirstOrDefault(x => x.Number == "9") is { } s9)
                {
                    int missing = Regex.Matches(s9.Text, @"технические\s+условия\s*\n?\s*отсутствуют", RegexOptions.IgnoreCase).Count + Regex.Matches(s9.Text, @"отсутствуют\*").Count;
                    Fact(r, block, "tu", "Технические условия подключения (раздел 9)", missing > 0 ? string.Create(Ru, $"не выданы по {Math.Max(1, missing / 2 + missing % 2)} сетям — запросить у балансодержателей") : "приведены в разделе 9",
                        missing > 0 ? FactStatus.Absent : FactStatus.Established, FirstNegated(s9.Text), s9.Page, "разд. 9");
                }
                break;
            }
            default:
            {
                if (g is not null && g.Boundary.Count > 2)
                    Fact(r, block, "boundary", "Характерные точки границы", string.Create(Ru, $"{g.Boundary.Count} точек ({g.CoordinateSystem}), площадь по Гауссу {g.GaussArea:N2} м²"),
                        FactStatus.Established, string.Join("; ", g.Boundary.Take(3).Select(p => string.Create(Ru, $"{p.Number}: X {p.X:0.00} Y {p.Y:0.00}"))), g.Tables.FirstOrDefault()?.FirstPage ?? 0, "Приложение № 1");
                foreach (var sec in s.Sections.Where(x => x.Block == block && x.Level == 1))
                {
                    var negated = IsNegated(sec.Text);
                    Fact(r, block, "sec-" + sec.Number, $"Раздел {sec.Number}: {Short(sec.Title, 70)}", negated ? "информация отсутствует / не установлено" : "сведения приведены",
                        negated ? FactStatus.Absent : FactStatus.Informational, negated ? FirstNegated(sec.Text) : Short(sec.Text.Replace('\n', ' '), 160), sec.Page, "разд. " + sec.Number);
                }
                AddCoreItems(s, r, s.Sections.Where(x => x.Block == block), []);
                // Сверка с топосъёмкой DWG: ОКС по кадастровым номерам, ЗОУИТ кадастрового слоя, сети.
                if (s.Topo is { } topo)
                {
                    if (g is not null)
                        foreach (var o in g.Objects.Where(o => o.Cadastral.Length > 0))
                        {
                            bool inDwg = topo.CapitalContours.Any(c => c.Label.Contains(o.Cadastral, StringComparison.Ordinal));
                            Fact(r, block, "dwg-oks-" + o.Number, string.Create(Ru, $"ОКС № {o.Number} в топосъёмке"), inDwg ? "контур найден по кадастровому номеру" : "в топосъёмке не отображён",
                                inDwg ? FactStatus.Established : FactStatus.Informational, o.Cadastral, 0, Path.GetFileName(topo.SourceFile));
                        }
                    foreach (var kind in topo.Networks.Select(n => n.Kind).Distinct())
                        Fact(r, block, "dwg-net-" + kind, "Сеть в топосъёмке: " + SiteBoundaryBuilder.StyleOf(kind).Title,
                            string.Create(Ru, $"{topo.Networks.Count(n => n.Kind == kind)} трасс, {topo.Networks.Where(n => n.Kind == kind).Sum(n => n.Length):N0} м"),
                            FactStatus.Established, string.Join(", ", topo.Networks.Where(n => n.Kind == kind).Select(n => n.Layer).Distinct().Take(3)), 0, Path.GetFileName(topo.SourceFile));
                    foreach (var cz in topo.CadastralZones.Where(z => z.Label.Length > 0))
                        Fact(r, block, "dwg-zone-" + cz.Label, "ЗОУИТ кадастрового слоя топосъёмки", cz.Label, FactStatus.Informational, cz.Layer, 0, Path.GetFileName(topo.SourceFile));
                }
                break;
            }
        }
    }

    /// <summary>Сведение по предложению предельных параметров: «не подлежат установлению» → не установлено, иначе число и единица.</summary>
    private static void ParameterFact(DocumentStructure s, ExhaustiveAnalysisReport r, AnalysisBlock block, string text, int page, string clause)
    {
        string low = text.ToLowerInvariant();
        (string Key, string Title)? p = low switch
        {
            _ when low.Contains("минимальн") && low.Contains("отступ") => ("setback", "Минимальные отступы от границ участка"),
            _ when low.Contains("количество этажей") || low.Contains("предельная высота") => ("height", "Предельное количество этажей / высота"),
            _ when low.Contains("процент застройки подземного") => ("underground", "Процент застройки подземного пространства"),
            _ when low.Contains("процент застройки") => ("coverage", "Максимальный процент застройки"),
            _ when low.Contains("коэффициент строительного") => ("ksi", "Коэффициент строительного использования"),
            _ when low.Contains("площадь жилых помещений") => ("living", "Общая площадь жилых помещений"),
            _ when low.Contains("машино-мест") => ("parking", "Количество машино-мест"),
            _ when low.Contains("минимальная площадь земельного") => ("minarea", "Минимальная площадь земельного участка"),
            _ when low.Contains("мест в дошкольных") => ("kindergarten", "Места в дошкольных учреждениях"),
            _ => null
        };
        if (p is not { } prm) return;
        // Предложение с несколькими параметрами («общая площадь … не более 20 810 кв. м, количество машино-мест – не менее 130») —
        // по частям, каждая со своим параметром и знаком.
        var parts = Regex.Split(text, @"[,;]\s+(?=(общая|количество|минимальн|предельн|максимальн|коэффициент|процент)\b)", RegexOptions.IgnoreCase)
            .Where(x => !Regex.IsMatch(x, @"^(общая|количество|минимальн|предельн|максимальн|коэффициент|процент)$", RegexOptions.IgnoreCase)).ToList();
        if (parts.Count > 1)
        {
            foreach (var part in parts) ParameterFact(s, r, block, part, page, clause);
            return;
        }
        // Отдельные назначения (многоэтажная / среднеэтажная / иные) — разные ключи по заголовку подпункта, если он есть в тексте.
        string scope = low.Contains("среднеэтажн") ? " (среднеэтажная)" : low.Contains("иных объектов") ? " (иные ОКС)" : string.Empty;
        // «Не подлежат установлению, за исключением …» — общая оговорка: исключённые параметры заданы далее отдельным предложением.
        if (low.Contains("за исключением") && IsNegated(text) && !Regex.IsMatch(low, @"не\s+более|не\s+менее")) return;
        if (IsNegated(text) && !Regex.IsMatch(low, @"не\s+более|не\s+менее"))
        {
            Fact(r, block, prm.Key + scope + page, prm.Title + scope, "не подлежит установлению", FactStatus.NotEstablished, text, page, clause);
            r.Negations.Add(string.Create(Ru, $"стр. {page}: «{Short(text, 110)}» — ограничение не создаётся"));
            return;
        }
        var num = Regex.Match(text, @"(не\s+(?<op>более|менее)|[-–—])\s*(?<v>\d[\d\s ]*(?:[.,]\d+)?)\s*(?<u>кв\.?\s*м|м²|м\b|%|машино|мест)?", RegexOptions.IgnoreCase);
        if (num.Success && DeepDocumentConstraintService.Num(num.Groups["v"].Value) is { } v)
        {
            string unit = num.Groups["u"].Value.StartsWith("кв", StringComparison.OrdinalIgnoreCase) || num.Groups["u"].Value == "м²" ? "м²" : num.Groups["u"].Value is "%" ? "%" : prm.Key == "parking" ? "м/м" : num.Groups["u"].Value;
            string op = num.Groups["op"].Value.ToLowerInvariant() switch { "менее" => "≥ ", "более" => "≤ ", _ => string.Empty };
            Fact(r, block, prm.Key + scope, prm.Title + scope, string.Create(Ru, $"{op}{v:N0} {unit}").Trim(), FactStatus.Established, text, page, clause);
        }
    }

    /// <summary>Раздел страницы: последний заголовок раздела до неё (титульный лист — до раздела 1).</summary>
    private static string SectionAt(DocumentStructure s, int page) =>
        s.Sections.LastOrDefault(x => x.Level == 1 && x.ByHeading && x.Page <= page) is { } sec ? "разд. " + sec.Number : "титульный лист";

    private static string FirstNegated(string text) =>
        text.Split('\n').FirstOrDefault(IsNegated) is { } l ? Short(l, 200) : Short(text.Replace('\n', ' '), 200);

    /// <summary>Правила ядра (DeepDocumentConstraintService.RulesFor) по предложениям разделов и сноскам — с отсевом отрицаний.</summary>
    private static void AddCoreItems(DocumentStructure s, ExhaustiveAnalysisReport r, IEnumerable<DocumentSection> sections, IEnumerable<DocumentFootnote> footnotes)
    {
        var found = new List<ProjectConstraintItem>();
        foreach (var sec in sections.DistinctBy(x => x.Number))
            foreach (var (text, page) in Sentences(s, sec))
                foreach (var item in DeepDocumentConstraintService.RulesFor(text))
                {
                    if (IsNegated(text) && item.Category is not ConstraintCategory.Approval and not ConstraintCategory.NetworkRelocation)
                    {
                        r.Negations.Add(string.Create(Ru, $"стр. {page}: «{Short(text, 110)}» — {item.Title}: отрицание, ограничение не создаётся"));
                        continue;
                    }
                    found.Add(Refine(item, text) with
                    {
                        Quote = Short(text, 400), Page = page, Clause = "п. " + sec.Number, Source = s.FileName, ExtractedBy = DeepDocumentConstraintService.CoreName,
                        QuoteVerified = true, Confidence = 0.8
                    });
                }
        foreach (var f in footnotes)
        {
            var rules = DeepDocumentConstraintService.RulesFor(f.Text).Where(i => !(IsNegated(f.Text) && i.Category is not ConstraintCategory.Approval and not ConstraintCategory.NetworkRelocation && !i.HasNumbers)).ToList();
            foreach (var item in rules)
                found.Add(item with { Quote = Short(f.Text, 400), Page = f.Page, Clause = "сноска " + f.Marker + (f.Section.Length > 0 ? " к п. " + f.Section : string.Empty), Source = s.FileName,
                    ExtractedBy = DeepDocumentConstraintService.CoreName, QuoteVerified = true, IsFootnote = true, Confidence = 0.8 });
        }
        foreach (var item in found)
            if (r.Items.All(i => i.MergeKey != item.MergeKey)) r.Items.Add(item);
    }

    /// <summary>
    /// Смысл условия по контексту: порядок действий «в случае обнаружения» объекта культурного наследия (ст. 36 73-ФЗ) — справочное
    /// требование к производству работ, а не ограничение посадки.
    /// </summary>
    private static ProjectConstraintItem Refine(ProjectConstraintItem item, string text) =>
        item.Category == ConstraintCategory.CulturalHeritage && Regex.IsMatch(text, @"в\s+случае\s+обнаружени", RegexOptions.IgnoreCase)
            ? item with
            {
                Title = "Порядок работ при обнаружении объекта культурного наследия (ст. 36 № 73-ФЗ)",
                Impact = ConstraintImpact.Informational,
                ImpactText = "ОКН на участке нет; при обнаружении признаков ОКН работы приостанавливаются с уведомлением органа охраны в течение 3 дней."
            }
            : item;

    // ═════════════════════════════ Этап 2: модель по блокам ═════════════════════════════

    public static string BlockPrompt(AnalysisBlock b) =>
        "Ты — эксперт по градостроительной документации РФ (Градостроительный кодекс ст. 57.3, ПЗЗ, ЗОУИТ, Воздушный кодекс ст. 47, ПП РФ № 160, " +
        "СП 42.13330). Прочитай ВЕСЬ присланный фрагмент документа — каждый абзац, строку таблицы и сноску со звёздочкой. " + b switch
        {
            AnalysisBlock.A => "Блок А (разделы 1–2): кадастровый номер, площадь, территориальная зона и подзона, все основные, условно разрешённые и " +
                               "вспомогательные ВРИ, предельные параметры (отступы, этажность, высота, процент застройки, общая площадь жилых помещений, " +
                               "машино-места, площадь участка) и ОБЯЗАТЕЛЬНО все сноски под таблицей 2.3. ",
            AnalysisBlock.B => "Блок Б (разделы 3–4): перечень ОКС на участке с кадастровыми номерами и статусом сохранения или сноса, ОКН, показатели инфраструктуры. ",
            AnalysisBlock.C => "Блок В (разделы 5, 9): каждая ЗОУИТ с реестровым номером 66:00-6.*, каждый сектор приаэродромной территории с абсолютной отметкой Hабс, " +
                               "условия переустройства (выноса) сетей, СТУ, согласования, технические условия подключения. ",
            _ => "Блок Г (разделы 6–8, 10–12 и Приложение № 1): координатные таблицы зон, сервитуты, красные линии, требования к облику. "
        } +
        "Правило отрицаний: «не подлежат установлению», «0 единиц», «информация отсутствует», «не установлены» — это сведение со status " +
        "\"not_established\" или \"absent\", а НЕ ограничение. Не выдумывай: каждая цитата — дословно из текста, с номером страницы. Верни ТОЛЬКО JSON: " +
        "{\"facts\":[{\"key\":\"латиницей\",\"title\":\"кратко\",\"value\":\"значение\",\"status\":\"established|not_established|absent|info\",\"quote\":\"дословно\",\"page\":N,\"clause\":\"пункт\"}]," +
        "\"constraints\":[{\"category\":\"height|network_relocation|sanitary_zone|fire_access|living_area|parking|cultural_heritage|hazardous|setback|protection_zone|special_conditions|approval|other\"," +
        "\"title\":\"кратко\",\"quote\":\"дословно\",\"page\":N,\"clause\":\"пункт/сноска\",\"maxHeightM\":число|null,\"maxHeightAbsM\":число|null,\"minSetbackM\":число|null," +
        "\"requiresNetworkRelocation\":true|false,\"minParkingSpots\":число|null,\"bufferRadiusM\":число|null,\"maxLivingAreaM2\":число|null," +
        "\"network\":\"gas|heat|power|water|sewer|communication\"|null,\"impact\":\"blocking|approval|info\",\"impactText\":\"влияние на проект\"}]}.";

    private static async Task ModelBlockAsync(DocumentStructure s, AnalysisBlock block, string text, ConstraintModel model, ExhaustiveAnalysisReport r, CancellationToken ct)
    {
        foreach (var chunk in Split(text, DeepDocumentConstraintService.ChunkChars))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string answer = await model.Ask(BlockPrompt(block), $"Документ «{s.FileName}», {BlockTitle(block)}. Фрагмент:\n{chunk}", ct).ConfigureAwait(false);
                r.ModelCalls++;
                var doc = new DocumentText { FileName = s.FileName };
                foreach (var (page, lines) in s.Pages) doc.Pages.Add((page, string.Join("\n", lines)));
                foreach (var item in DeepDocumentConstraintService.ParseModelItems(answer, doc, model.Title, ConstraintOrigin.Document, s.FileName))
                {
                    int i = r.Items.FindIndex(x => x.MergeKey == item.MergeKey);
                    if (i < 0) r.Items.Add(item);
                    else r.Items[i] = r.Items[i] with { ExtractedBy = r.Items[i].ExtractedBy.Contains(model.Title, StringComparison.Ordinal) ? r.Items[i].ExtractedBy : r.Items[i].ExtractedBy + " + " + model.Title, Confidence = Math.Min(0.98, r.Items[i].Confidence + 0.1) };
                }
                ParseModelFacts(answer, block, model.Title, r);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or AI.AiProviderException or TaskCanceledException)
            {
                r.ModelError = ex.Message;
                r.Log.Add($"{model.Title}: блок {block} без ответа ({ex.Message}) — остаются сведения ядра.");
                return;
            }
        }
    }

    private static IEnumerable<string> Split(string text, int max)
    {
        for (int i = 0; i < text.Length; i += max) yield return text.Substring(i, Math.Min(max, text.Length - i));
    }

    private static void ParseModelFacts(string answer, AnalysisBlock block, string title, ExhaustiveAnalysisReport r)
    {
        if (DeepDocumentConstraintService.ExtractJson(answer) is not { } json) return;
        using var j = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (j.RootElement.ValueKind != JsonValueKind.Object || !j.RootElement.TryGetProperty("facts", out var facts) || facts.ValueKind != JsonValueKind.Array) return;
        foreach (var f in facts.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) continue;
            string S(string k) => f.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : f.TryGetProperty(k, out var n) && n.ValueKind == JsonValueKind.Number ? n.ToString() : string.Empty;
            string key = S("key"), value = S("value");
            if (key.Length == 0 || r.Facts.Any(x => x.Key == key && x.Value == value)) continue;
            var status = S("status").ToLowerInvariant() switch { "not_established" => FactStatus.NotEstablished, "absent" => FactStatus.Absent, "info" => FactStatus.Informational, _ => FactStatus.Established };
            int page = int.TryParse(S("page"), out int p) ? p : 0;
            r.Facts.Add(new DocumentFact(block, key, S("title"), value, status, Short(S("quote"), 360), page, S("clause"), title, false));
        }
    }

    // ═════════════════════════════ Этап 3: перекрёстная верификация ═════════════════════════════

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace('ё', 'е').Replace(' ', ' '), @"[\s«»""'„“”]+", " ").Trim();

    private static bool OnPage(DocumentStructure s, int page, string quote)
    {
        if (quote.Length < 6) return false;
        string q = Norm(quote.Trim('…', '.', ' '));
        string text = Norm(page > 0 ? s.PageText(page) : string.Join("\n", s.Pages.SelectMany(p => p.Lines)));
        if (text.Contains(q, StringComparison.Ordinal)) return true;
        // Цитата из склеенных строк страницы: каждая её часть по 40 символов — на странице.
        return q.Length > 40 && Enumerable.Range(0, (q.Length - 1) / 40 + 1).Select(i => q.Substring(i * 40, Math.Min(40, q.Length - i * 40)).Trim()).Where(x => x.Length >= 12)
            .All(part => text.Contains(part, StringComparison.Ordinal));
    }

    private static int PageOf(DocumentStructure s, string quote) =>
        s.Pages.FirstOrDefault(p => OnPage(s, p.Page, quote)).Page;

    private static bool NumberOnPage(DocumentStructure s, int page, double v)
    {
        string text = page > 0 ? s.PageText(page) : string.Join("\n", s.Pages.SelectMany(p => p.Lines));
        string compact = Regex.Replace(text, @"(?<=\d)[\s ](?=\d{3}\b)", string.Empty);
        return new[] { v.ToString("0.00", Ru), v.ToString("0.00", CultureInfo.InvariantCulture), v.ToString("0.#", Ru), v.ToString("0.#", CultureInfo.InvariantCulture), v.ToString("0", CultureInfo.InvariantCulture) }
            .Any(form => compact.Contains(form, StringComparison.Ordinal) || text.Contains(form, StringComparison.Ordinal));
    }

    public static void Verify(DocumentStructure s, ExhaustiveAnalysisReport r)
    {
        // Разделы 1–12 (для ГПЗУ) — найдены заголовком или ссылкой Приложения.
        var found = s.TopSections.ToHashSet();
        int expected = s.IsGpzu ? 12 : found.DefaultIfEmpty(0).Max();
        for (int n = 1; n <= expected; n++)
        {
            var sec = s.Sections.FirstOrDefault(x => x.Level == 1 && x.Number == n.ToString(Ru));
            r.Coverage.Add(new CoverageCheck("section", $"Раздел {n}", sec?.Page ?? 0, sec is not null,
                sec is null ? "раздел не найден в тексте документа" : (sec.ByHeading ? "заголовок" : "ссылка Приложения") + string.Create(Ru, $", стр. {sec.Page}")));
        }

        // Цитаты и числа: каждая цитата — на своей странице (если страница неверна — уточняется), каждое число — в тексте страницы.
        for (int i = 0; i < r.Items.Count; i++)
        {
            var item = r.Items[i];
            if (item.Origin != ConstraintOrigin.Document) continue;
            bool onPage = OnPage(s, item.Page, item.Quote);
            int page = item.Page;
            if (!onPage && PageOf(s, item.Quote) is > 0 and var real) { page = real; onPage = true; }
            double? v = item.MaxHeightAbsM ?? item.MaxHeightM ?? item.MinSetbackM ?? item.BufferRadiusM ?? item.MaxLivingAreaM2 ?? item.MinParkingSpots;
            bool numberOk = v is not { } value || NumberOnPage(s, page, value);
            bool negated = item.Category is not ConstraintCategory.Approval and not ConstraintCategory.NetworkRelocation and not ConstraintCategory.SpecialConditions && IsNegated(item.Quote) &&
                           !Regex.IsMatch(item.Quote, @"не\s+более|не\s+менее", RegexOptions.IgnoreCase) && !item.HasNumbers;
            r.Coverage.Add(new CoverageCheck("quote", Short(item.Title, 80), page, onPage && numberOk,
                !onPage ? "цитата не найдена в тексте документа" : !numberOk ? "число не найдено на странице" : page != item.Page ? string.Create(Ru, $"страница уточнена: {item.Page} → {page}") : "цитата и число на странице"));
            if (negated)
            {
                r.Negations.Add($"«{Short(item.Quote, 110)}» — {item.Title}: отрицание, пункт исключён");
                r.Items.RemoveAt(i--);
                continue;
            }
            r.Items[i] = item with
            {
                Page = page,
                QuoteVerified = onPage && numberOk,
                Confidence = onPage && numberOk ? Math.Max(item.Confidence, 0.8) : Math.Min(item.Confidence, 0.35),
                ImpactText = onPage && numberOk ? item.ImpactText : "Не подтверждено текстом документа: " + (onPage ? "число отсутствует на странице. " : "цитата не найдена. ") + item.ImpactText
            };
        }
        foreach (var f in r.Facts.Where(f => !f.Verified).ToList())
        {
            bool ok = OnPage(s, f.Page, f.Quote) || PageOf(s, f.Quote) > 0;
            int idx = r.Facts.IndexOf(f);
            r.Facts[idx] = f with { Verified = ok, Page = ok && !OnPage(s, f.Page, f.Quote) ? PageOf(s, f.Quote) : f.Page };
            r.Coverage.Add(new CoverageCheck("quote", Short(f.Title, 80), f.Page, ok, ok ? "цитата на странице" : "цитата модели не найдена в документе"));
        }

        // Каждая сноска — в ведомости (ограничение или сведение); иначе — сведение «Примечание *» с полным текстом.
        foreach (var f in s.Footnotes)
        {
            string head = Norm(Short(f.Text, 60).TrimEnd('…'));
            bool covered = r.Items.Any(i => i.Page == f.Page && (i.IsFootnote || i.Clause.Contains("сноск", StringComparison.Ordinal)) && Norm(i.Quote).Contains(head[..Math.Min(head.Length, 40)], StringComparison.Ordinal))
                           || r.Facts.Any(x => x.Page == f.Page && x.Clause.StartsWith("сноска", StringComparison.Ordinal) && Norm(f.Text).Contains(Norm(Short(x.Quote, 60).TrimEnd('…'))[..Math.Min(30, Norm(Short(x.Quote, 60).TrimEnd('…')).Length)], StringComparison.Ordinal));
            if (!covered)
            {
                r.Facts.Add(new DocumentFact(BlockOf(SectionTop(f.Section)), "footnote-" + f.Page + "-" + r.Facts.Count, $"Примечание {f.Marker}" + (f.Section.Length > 0 ? " к п. " + f.Section : string.Empty),
                    Short(f.Text, 160), IsNegated(f.Text) ? FactStatus.NotEstablished : FactStatus.Informational, Short(f.Text, 360), f.Page, "сноска " + f.Marker, DeepDocumentConstraintService.CoreName, true));
            }
            r.Coverage.Add(new CoverageCheck("footnote", $"Сноска {f.Marker} (стр. {f.Page})", f.Page, true, covered ? "в ограничениях / сведениях" : "добавлена как примечание"));
        }

        // Каждый реестровый номер ЗОУИТ — в зонах документа и таблице координат Приложения № 1.
        foreach (var number in s.RegistryNumbers.Select(x => x.Number).Distinct())
        {
            int page = s.RegistryNumbers.First(x => x.Number == number).Page;
            var zone = s.Gpzu?.Zones.FirstOrDefault(z => z.Code == number);
            bool table = s.Tables.Any(t => t.Title.Contains(number, StringComparison.Ordinal)) || zone?.Polygon.Count > 2;
            bool inFacts = r.Facts.Any(f => f.Value.Contains(number, StringComparison.Ordinal));
            if (!inFacts)
                r.Facts.Add(new DocumentFact(BlockOf(5), "zone-" + number, "ЗОУИТ (по тексту документа)", number, FactStatus.Established,
                    s.Pages.First(p => p.Page == page).Lines.First(l => l.Contains(number, StringComparison.Ordinal)), page, "разд. 5", DeepDocumentConstraintService.CoreName, true));
            r.Coverage.Add(new CoverageCheck("registry", number, page, zone is not null || inFacts || s.Gpzu is null,
                (zone is not null ? "зона раздела 5" : "сведение по тексту") + (table ? " · координаты в Приложении № 1" : s.Gpzu is null ? string.Empty : " · без таблицы координат")));
        }
    }

    // ═════════════════════════════ Этап 4: нормативы ═════════════════════════════

    /// <summary>
    /// Сеть (в тексте или в топосъёмке) или ссылка на акт без численного коридора — нормативное значение из базы ядра с явным
    /// источником «Норматив: …» (ПП № 160 / № 878 / № 578, СП 42.13330 табл. 12.6, приказ № 197); пункты не подтверждены.
    /// </summary>
    public static int AddNormatives(DocumentStructure s, ExhaustiveAnalysisReport r)
    {
        string all = string.Join("\n", s.Pages.SelectMany(p => p.Lines)).ToLowerInvariant();
        var kinds = new HashSet<GpzuZoneKind>();
        foreach (var (page, lines) in s.Pages)
            foreach (var l in lines)
                if (DeepDocumentConstraintService.NetworkOf(l.ToLowerInvariant()) is { } k) kinds.Add(k);
        if (s.Topo is { } topo) foreach (var n in topo.Networks) kinds.Add(n.Kind);
        var queries = new List<string>();
        foreach (var k in kinds)
        {
            if (r.Items.Any(i => i.NetworkKind == k && i.BufferRadiusM.HasValue)) continue;
            queries.Add(k switch
            {
                GpzuZoneKind.GasPipeline => "газопровод среднего давления",
                GpzuZoneKind.HeatNetwork => "теплосеть тепловые сети",
                GpzuZoneKind.PowerCable => "кабельная линия электропередачи кл",
                GpzuZoneKind.WaterSupply => "водопровод",
                GpzuZoneKind.Sewer => "канализация",
                GpzuZoneKind.Communication => "линии связи",
                _ => string.Empty
            });
        }
        if (Regex.IsMatch(all, @"пп\s*рф\s*№\s*160|постановлени\w+\s+правительства[^.]{0,60}№\s*160")) queries.Add("лэп 110 кв");
        int added = 0;
        foreach (var q in queries.Where(q => q.Length > 0))
            foreach (var n in DeepDocumentConstraintService.MatchNormatives(q))
            {
                if (r.Items.Any(i => i.MergeKey == n.MergeKey)) continue;
                r.Items.Add(n with
                {
                    Origin = ConstraintOrigin.Normative,
                    Source = "Норматив: " + n.Source,
                    ExtractedBy = DeepDocumentConstraintService.CoreName + " · база нормативов",
                    ImpactText = "В документе нет численного коридора — значение по нормативу; требует утверждения ГИПом. " + n.ImpactText,
                    Status = ConstraintStatus.Pending
                });
                added++;
            }
        return added;
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
}
