#if DEBUG
using System.Globalization;
using System.Reflection;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Models.Layouts;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.Services.Layouts;
using AIArchitectStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест Волны 7: строгая терминология (ни одной разговорной формулировки в словарях и в замечаниях проверки посадки),
/// 4-этапный анализ эталонного ГПЗУ с топосъёмкой (разделы 1–12, сноски, отрицания «0 единиц» / «не подлежат установлению»,
/// реестровые номера ЗОУИТ ↔ таблицы Приложения № 1, сверка цитат и чисел, нормативы сетей) и с моделью-заглушкой (блоки А–Г,
/// рассуждение «high», вымышленная цитата и ложное ограничение по отрицанию отсеяны), изоляция интерфейса трёх разделов (один
/// правый док, профильные палитры), 50-этажный корпус в разделе 3 (пакеты по 5 этажей, мульти-план, ядро с лифтами, лестницами
/// и тамбуром, подвал с ИТП / ВРУ / насосной и магистралями, проверка мокрых и газовых зон 5 / 6 этажа и согласование одним шагом).
/// </summary>
public static partial class CadSelfTestService
{
    private static readonly CultureInfo W7Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static async Task<string> RunWave7OnlyAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        int failures = await CheckWave7Async(directory, report);
        report.Insert(0, failures == 0 ? "ИТОГ: УСПЕХ\n" : $"ИТОГ: ОШИБОК {failures}\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "wave7.txt"), report.ToString(), new UTF8Encoding(true));
        return report.ToString();
    }

    /// <summary>Разговорные формулировки, запрещённые в интерфейсе (Волна 7, блок 1).</summary>
    public static readonly string[] W7BannedPhrases =
    [
        "«НО»", "\"НО\"", "ЖИВОЙ УЧАСТОК", "Живой участок", "живой участок", "Живая площадь", "ИИ перепутал", "ошибка анализа", "ЧТО ПЕРЕПУТАНО", "Что перепутано",
        "Дом прохода", "Среда 1", "Среда 2", "Среда 3", "Пятно размещения", "пятно размещения", "Авто-исправление", "Состав КБ", "Задать / исправить",
        "Повторить поиск с уточнением", "со сверенной цитатой", "Применить подтверждённые"
    ];

    private static async Task<int> CheckWave7Async(string directory, StringBuilder report)
    {
        int failures = 0;

        // ── 1. Терминология: все словари локализации (RU и EN) без разговорных формулировок.
        var dictionaries = typeof(LocalizationService).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Dictionary<string, (string Ru, string En)>))
            .Select(f => (f.Name, Dict: (Dictionary<string, (string Ru, string En)>)f.GetValue(null)!)).ToList();
        var hits = dictionaries.SelectMany(d => d.Dict.Where(kv => W7BannedPhrases.Any(b => kv.Value.Ru.Contains(b, StringComparison.Ordinal) || kv.Value.En.Contains(b, StringComparison.Ordinal)))
            .Select(kv => $"{d.Name}.{kv.Key}")).ToList();
        var w7 = dictionaries.FirstOrDefault(d => d.Name == "Wave7Strings").Dict;
        bool w7Filled = w7 is { Count: >= 100 } && w7.All(kv => kv.Value.Ru.Length > 0 && kv.Value.En.Length > 0 && !kv.Value.Ru.Any(char.IsSurrogate));
        // Кнопки — коротко: подписи палитр и вкладок не длиннее 4 слов.
        var shortKeys = w7?.Where(kv => kv.Key.StartsWith("W7Tool", StringComparison.Ordinal) || kv.Key.StartsWith("W7Tab", StringComparison.Ordinal)).ToList() ?? [];
        bool concise = shortKeys.Count >= 15 && shortKeys.All(kv => kv.Value.Ru.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter)) <= 5);
        var loc = App.Services.GetRequiredService<LocalizationService>();
        bool terms = loc.Get("W6EnvSite") == "1. Генплан и ГПЗУ" && loc.Get("W6EnvBuilding") == "2. Архитектура и КР" && loc.Get("W6EnvInteriors") == "3. Планировки и ИОС" &&
                     loc.Get("W6ConstraintsTitle") == "Градостроительные ограничения и ЗОУИТ" && loc.Get("W6Reject") == "Исключить условие" && loc.Get("W6ConfirmVerified") == "Принять верифицированные условия" &&
                     loc.Get("W6ApplyConfirmed") == "Применить регламент к участку" && loc.Get("W6RetryCaption") == "Уточнение критериев повторного анализа" && loc.Get("W6Retry") == "Повторить анализ" &&
                     loc.Get("W6Manual") == "Изменить параметры" && loc.Get("W6AllowedArea") == "Допустимая зона застройки" && loc.Get("W6SiteLive") == "Земельный участок (ГПЗУ)";
        Check(report, ref failures, hits.Count == 0 && w7Filled && concise && terms,
            $"терминология: словарей {dictionaries.Count}, ключей {dictionaries.Sum(d => d.Dict.Count)} — разговорных формулировок {hits.Count}{(hits.Count > 0 ? " (" + string.Join(", ", hits.Take(8)) + ")" : string.Empty)}; " +
            $"разделы «1. Генплан и ГПЗУ · 2. Архитектура и КР · 3. Планировки и ИОС», «Градостроительные ограничения и ЗОУИТ», «Исключить условие», «Принять верифицированные условия», " +
            $"«Применить регламент к участку», «Повторить анализ», «Допустимая зона застройки» [{terms}]; Волна 7 — {w7?.Count} ключей RU/EN [{w7Filled}], подписи кнопок ≤ 5 слов [{concise}]");

        string kit = Path.Combine(AppContext.BaseDirectory, "DemoKit", "GPZU");
        string? pdfPath = Directory.Exists(kit) ? Directory.GetFiles(kit, "*.pdf").FirstOrDefault() : null;
        string? dwgPath = Directory.Exists(kit) ? Directory.GetFiles(kit, "*.dwg").FirstOrDefault() : null;
        if (pdfPath is null || dwgPath is null)
        {
            Check(report, ref failures, false, $"DemoKit/GPZU рядом с программой ({kit})");
            return failures;
        }

        // ── 2. Глубокий анализ эталонного ГПЗУ (ядро) с топосъёмкой.
        var gpzu = await Task.Run(() => GpzuParser.Load(pdfPath));
        var topo = await Task.Run(() => TopoSurveyReader.Read(dwgPath, gpzu.Boundary));
        var stages = new List<AnalysisProgress>();
        var progress = new W7Progress<AnalysisProgress>(stages.Add);
        var a = await ExhaustiveDocumentAnalyzerService.AnalyzeFileAsync(pdfPath, topo, null, progress, renderDrawings: true);
        var s = a.Structure;
        var tops = s.TopSections.OrderBy(n => n).ToList();
        await File.WriteAllTextAsync(Path.Combine(directory, "wave7_gpzu_analysis.txt"),
            string.Join("\n", a.Log) + "\n\n== Разделы ==\n" + string.Join("\n", s.Sections.Select(x => $"{x.Number} [{x.Block}] стр. {x.Page}–{x.EndPage} {(x.ByHeading ? "" : "(ссылка)")} {x.Title}")) +
            "\n\n== Сноски ==\n" + string.Join("\n", s.Footnotes.Select(f => $"стр. {f.Page} {f.Marker} к {f.Section}: {f.Text}")) +
            "\n\n== Сведения ==\n" + string.Join("\n", a.Facts.Select(f => $"[{f.Block}] {f.Key} · {f.Title} = {f.Value} ({f.Status}) стр. {f.Page} {f.Clause}")) +
            "\n\n== Ограничения ==\n" + string.Join("\n", a.Items.Select(i => $"{i.Category} · {i.Title} · {i.ValueText(W7Ru)} · стр. {i.Page} {i.Clause} · {i.Origin} · сверено {i.QuoteVerified} · «{i.Quote}»")) +
            "\n\n== Сверка ==\n" + string.Join("\n", a.Coverage.Select(c => $"{c.Kind} {c.Title}: {c.Passed} — {c.Detail}")) +
            "\n\n== Отрицания ==\n" + string.Join("\n", a.Negations), new UTF8Encoding(true));
        bool sections = s.IsGpzu && a.SectionsFound == 12 && Enumerable.Range(1, 12).All(tops.Contains) && s.Sections.Any(x => x.Number == "2.3") && s.Sections.Any(x => x.Number == "5.1");
        bool footnotes = s.Footnotes.Count >= 3 && a.FootnotesCovered == a.FootnotesTotal && s.Footnotes.Any(f => f.Page == 8 && f.Text.Contains("45.4", StringComparison.Ordinal)) &&
                         s.Footnotes.Any(f => f.Page == 14 && f.Text.StartsWith("Правообладатель", StringComparison.Ordinal));
        bool okn = a.Facts.Any(f => f.Key == "okn-count" && f.Status == FactStatus.Absent) && a.Items.Where(i => i.Category == ConstraintCategory.CulturalHeritage).All(i => i.Page != 3 && i.Impact == ConstraintImpact.Informational);
        bool negations = a.Facts.Count(f => f.Status == FactStatus.NotEstablished) >= 4 && a.Facts.Any(f => f.Key.StartsWith("setback", StringComparison.Ordinal) && f.Status == FactStatus.NotEstablished) &&
                         a.Facts.Any(f => f.Key.StartsWith("height", StringComparison.Ordinal) && f.Status == FactStatus.NotEstablished) && a.Items.All(i => i.Category != ConstraintCategory.Setback) && a.Negations.Count >= 4;
        bool limits = a.Items.Any(i => i.MaxLivingAreaM2 == 20810 && i.QuoteVerified && i.Page == 8) && a.Items.Any(i => i.MinParkingSpots == 130 && i.QuoteVerified) &&
                      a.Facts.Any(f => f.Key == "living" && f.Value.Replace(' ', ' ').Contains("≤ 20 810 м²", StringComparison.Ordinal)) && a.Facts.Any(f => f.Key == "parking" && f.Value.StartsWith("≥ 130", StringComparison.Ordinal)) && !a.Facts.Any(f => f.Key.StartsWith("parking", StringComparison.Ordinal) && f.Status == FactStatus.NotEstablished);
        bool heights = new[] { 365.00, 383.50, 423.58 }.All(h => a.Items.Any(i => i.MaxHeightAbsM is { } v && Math.Abs(v - h) < 0.005 && i.QuoteVerified));
        var registry = s.RegistryNumbers.Select(r => r.Number).Distinct().ToList();
        bool zones = registry.Count(r => r.StartsWith("66:00-6.", StringComparison.Ordinal)) == 7 && a.RegistryMatched == a.RegistryTotal &&
                     a.Coverage.Where(c => c.Kind == "registry" && c.Title.StartsWith("66:00-6.", StringComparison.Ordinal)).All(c => c.Detail.Contains("Приложении", StringComparison.Ordinal));
        bool quotes = a.QuotesTotal > 0 && a.QuotesVerified == a.QuotesTotal;
        bool dwg = a.Facts.Count(f => f.Key.StartsWith("dwg-net-", StringComparison.Ordinal)) >= 4 && a.Facts.Any(f => f.Key.StartsWith("dwg-oks-", StringComparison.Ordinal));
        bool normative = a.Items.Count(i => i.Origin == ConstraintOrigin.Normative && i.Source.StartsWith("Норматив: ", StringComparison.Ordinal)) >= 4 &&
                         a.Items.Any(i => i.Origin == ConstraintOrigin.Normative && i.NetworkKind == GpzuZoneKind.GasPipeline) && a.Items.All(i => i.Status == ConstraintStatus.Pending);
        bool pipeline = a.Stages.Count == 4 && Enumerable.Range(1, 4).All(n => stages.Any(p => p.Stage == n)) && stages[^1].Fraction >= 1 && s.Drawings.Count >= 2 && s.Drawings.Any(d => d.Image is { Length: > 10_000 }) &&
                        s.Tables.Count(t => t.Kind == "координаты") == 15 && s.Tables.Any(t => t.Kind == "экспликация");
        Check(report, ref failures, sections && footnotes && okn && negations && limits && heights && zones && quotes && dwg && normative && pipeline,
            string.Create(W7Ru, $"4-этапный анализ ГПЗУ ({s.PageCount} стр., {s.CharCount:N0} знаков, {a.Elapsed.TotalSeconds:0.0} с): разделы {string.Join(",", tops)} — {a.SectionsFound}/12, подразделы 2.3 и 5.1 [{sections}]; ") +
            string.Create(W7Ru, $"сносок {s.Footnotes.Count}, в ведомости {a.FootnotesCovered} [{footnotes}]; «Количество объектов культурного наследия 0 единиц» — сведение «отсутствует», ограничения ОКН нет [{okn}]; ") +
            string.Create(W7Ru, $"«не подлежат установлению» — сведений {a.Facts.Count(f => f.Status == FactStatus.NotEstablished)}, учтено отрицаний {a.Negations.Count}, ложного отступа нет [{negations}]; жильё ≤ 20 810 м² и машино-места ≥ 130 из сноски 2.3 [{limits}]; ") +
            string.Create(W7Ru, $"Hабс 365,00 / 383,50 / 423,58 м БС с цитатами [{heights}]; ЗОУИТ 66:00-6.* — {registry.Count(r => r.StartsWith("66:00-6.", StringComparison.Ordinal))}, сопоставлено с таблицами Приложения № 1 {a.RegistryMatched}/{a.RegistryTotal} [{zones}]; ") +
            string.Create(W7Ru, $"цитат и чисел сверено {a.QuotesVerified}/{a.QuotesTotal} [{quotes}]; топосъёмка: сетей {a.Facts.Count(f => f.Key.StartsWith("dwg-net-", StringComparison.Ordinal))}, ОКС по кадастру [{dwg}]; ") +
            string.Create(W7Ru, $"нормативов без коридора в документе — {a.Items.Count(i => i.Origin == ConstraintOrigin.Normative)} с пометкой «Норматив: …» [{normative}]; этапы 1–4 с индикатором, графических страниц {s.Drawings.Count} (снимок {s.Drawings.Max(d => d.Image?.Length ?? 0) / 1024:N0} КБ), таблиц координат {s.Tables.Count(t => t.Kind == "координаты")} [{pipeline}]"));

        // ── 3. Модель-заглушка по блокам А–Г: рассуждение «high», вымышленная цитата и ложное ограничение по отрицанию отсеяны.
        var prompts = new List<string>();
        var stub = new ConstraintModel("Облачный сервис · заглушка", (system, user, ct) =>
        {
            prompts.Add(system + "\n" + user);
            string answer = system.Contains("Блок А", StringComparison.Ordinal)
                ? """{"facts":[{"key":"subzone-model","title":"Подзона","value":"45.4","status":"established","quote":"Земельный участок находится в пределах подзоны № 45.4.","page":8,"clause":"сноска *"}],"constraints":[{"category":"setback","title":"Отступ 10 м от красной линии","quote":"Минимальный отступ от красной линии 10 м","page":8,"minSetbackM":10,"impact":"blocking"},{"category":"living_area","title":"Жилая площадь не более 20 810 м²","quote":"общая площадь жилых помещений – не более 20 810 кв. м","page":8,"maxLivingAreaM2":20810,"impact":"blocking"}]}"""
                : system.Contains("Блок Б", StringComparison.Ordinal)
                    ? """{"facts":[],"constraints":[{"category":"cultural_heritage","title":"Объекты культурного наследия","quote":"Количество объектов культурного наследия 0 единиц(ы).","page":3,"impact":"approval"}]}"""
                    : """{"facts":[],"constraints":[]}""";
            return Task.FromResult(answer);
        });
        var m = await ExhaustiveDocumentAnalyzerService.AnalyzeFileAsync(pdfPath, topo, stub, null, renderDrawings: false);
        var fake = m.Items.FirstOrDefault(i => i.MinSetbackM == 10);
        bool modelOk = m.ModelCalls >= 4 && prompts.Count(p => p.Contains("Блок А", StringComparison.Ordinal)) >= 1 && prompts.Any(p => p.Contains("Блок В", StringComparison.Ordinal)) &&
                       prompts.Any(p => p.Contains("Блок Г", StringComparison.Ordinal)) && prompts.All(p => p.Contains("Правило отрицаний", StringComparison.Ordinal)) &&
                       fake is { QuoteVerified: false, Confidence: <= 0.35 } && m.Items.Where(i => i.Category == ConstraintCategory.CulturalHeritage).All(i => i.Page != 3 && i.Impact == ConstraintImpact.Informational) &&
                       m.Items.Any(i => i.MaxLivingAreaM2 == 20810 && i.QuoteVerified && i.ExtractedBy.Contains("заглушка", StringComparison.Ordinal)) &&
                       m.Facts.Any(f => f.Key == "subzone-model" && f.Verified);
        Check(report, ref failures, modelOk,
            $"пораздельный аудит моделью: запросов {m.ModelCalls} (блоки А–Г, правило отрицаний в каждом), вымышленная цитата «отступ 10 м от красной линии» — не подтверждена, достоверность {(fake?.Confidence ?? 0) * 100:0} %; " +
            $"ложное ограничение ОКН по «0 единиц» исключено; жильё 20 810 м² — подтверждено ядром и моделью; сведение модели о подзоне 45.4 сверено с текстом [{modelOk}]");

        // ── 4. Изоляция интерфейса трёх разделов и 50-этажный корпус в разделе 3.
        failures += await CheckWave7SectionsAsync(report);
        failures += CheckWave7Layouts(report);
        return failures;
    }

    /// <summary>Синхронный приёмник прогресса (Progress&lt;T&gt; публикует через контекст синхронизации — в тесте нужен порядок).</summary>
    private sealed class W7Progress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private static async Task<int> CheckWave7SectionsAsync(StringBuilder report)
    {
        int failures = 0;
        var vm = App.Services.GetRequiredService<MainViewModel>();
        if (App.MainWindowInstance is not MainWindow window)
        {
            Check(report, ref failures, false, "главное окно для проверки изоляции разделов");
            return failures;
        }
        var envBefore = vm.ActiveEnvironment;
        bool structuralBefore = vm.IsStructuralMode;
        vm.StartEmptyProject();
        await vm.LoadDemoSiteAsync();
        string? gid = vm.AddStructureToSite(SiteStructureKind.ResidentialSection, 9);
        int Docks((bool Inspector, bool SectionDock, bool Structural, bool SitePalette, bool BuildingPalette, bool LayoutPalette, bool StressToggle, bool StructuralSegment, bool Chips, bool Assistant, bool PlanHost) c) =>
            (c.Inspector ? 1 : 0) + (c.SectionDock ? 1 : 0) + (c.Structural ? 1 : 0);

        vm.ActiveEnvironment = DeveloperEnvironment.Site;
        await Task.Delay(250);
        window.DebugRefreshSectionDock();
        var c1 = window.DebugChrome();
        var tabs1 = window.DebugDockTabs();
        string text1 = window.DebugDockText();
        bool s1 = Docks(c1) == 1 && c1.SectionDock && !c1.Inspector && c1.SitePalette && !c1.BuildingPalette && !c1.LayoutPalette && !c1.StressToggle && !c1.StructuralSegment && !c1.Chips && c1.Assistant &&
                  tabs1.SequenceEqual(["Паспорт ЗУ", "Регламент и ЗОУИТ", "Сети ИГДИ"]) && !text1.Contains("лестниц", StringComparison.OrdinalIgnoreCase) && !text1.Contains("КЖ", StringComparison.Ordinal) &&
                  text1.Contains(vm.Scene.SitePlan!.Document.Cadastral, StringComparison.Ordinal) && !vm.IsStructuralModeAvailable;

        vm.ActiveEnvironment = DeveloperEnvironment.Building;
        await Task.Delay(250);
        var c2 = window.DebugChrome();
        bool s2 = Docks(c2) == 1 && c2.Inspector && c2.BuildingPalette && !c2.SitePalette && !c2.LayoutPalette && c2.StressToggle && !c2.StructuralSegment;
        vm.IsStructuralMode = true;
        await Task.Delay(250);
        var c2s = window.DebugChrome();
        bool s2s = Docks(c2s) == 1 && c2s.Structural && !c2s.Inspector;
        vm.IsStructuralMode = false;

        vm.ActiveEnvironment = DeveloperEnvironment.Interiors;
        await Task.Delay(250);
        window.DebugRefreshSectionDock();
        var c3 = window.DebugChrome();
        var tabs3 = window.DebugDockTabs();
        var plan = vm.GenerateBuildingLayout(gid);
        vm.IsMultiPlanOpen = true;
        await Task.Delay(400);
        var c3m = window.DebugChrome();
        int tiers = window.PlanView?.LastTiers.Count ?? 0;
        bool s3 = Docks(c3) == 1 && c3.SectionDock && c3.LayoutPalette && !c3.SitePalette && !c3.BuildingPalette && !c3.StructuralSegment && !c3.StressToggle &&
                  tabs3.SequenceEqual(["Пакеты этажей", "Квартиры и МОП", "Инженерные стояки"]) && plan is not null && c3m.PlanHost && tiers >= 2;
        vm.IsMultiPlanOpen = false;
        Check(report, ref failures, s1 && s2 && s2s && s3,
            $"изоляция разделов: «1. Генплан и ГПЗУ» — один правый док (вкладки {string.Join(" · ", tabs1)}), палитра участка, без инспектора, «Конструкций», карты напряжений и конструктора лестниц, чипов ассистента нет — одна кнопка режима [{s1}]; " +
            $"«2. Архитектура и КР» — только инспектор и палитра моделирования [{s2}], режим «Конструкции» занимает тот же док [{s2s}]; «3. Планировки и ИОС» — док {string.Join(" · ", tabs3)}, палитра планировок, мульти-план колонок {tiers} [{s3}]");
        vm.StartEmptyProject();
        vm.ActiveEnvironment = envBefore;
        vm.IsStructuralMode = structuralBefore && vm.IsStructuralModeAvailable;
        return failures;
    }

    private static int CheckWave7Layouts(StringBuilder report)
    {
        int failures = 0;
        // ── 50-этажный корпус: пакеты по 5 этажей, мульти-план, ядро, подвал.
        const string g = "w7-tower";
        var elements = EditableSiteTerrainService.CreateStructure(SiteStructureKind.HighRise, g, 50);
        var request = FloorTierLayoutService.RequestFor(elements, [new BuildingGroup { Id = g, Name = "Жилой корпус 50 эт." }], g, null)!;
        var plan = DeveloperLayoutGenerator.Generate(request);
        var floors = plan.Floors.ToList();
        bool packages = request.Levels == 50 && request.Basement && plan.Basement is not null && floors.Count == 10 && floors.All(p => p.Floors == 5) &&
                        floors.Select(p => $"{p.FromLevel}–{p.ToLevel}").SequenceEqual(Enumerable.Range(0, 10).Select(i => $"{i * 5 + 1}–{i * 5 + 5}"));
        var tiers = FloorTierLayoutService.Tiers(plan, 4);
        bool multi = tiers.Select(t => t.Title).SequenceEqual(["Подвал", "Этажи 1–5", "Этажи 6–10", "Этажи 11–50"]) && tiers[3].PackagesMerged == 8 &&
                     FloorTierLayoutService.Tiers(plan, 2).Count == 2 && FloorTierLayoutService.Tiers(plan, 3).Count == 3 && plan.AxesX.Count == 8 && plan.AxesZ.Count == 4;
        // Ядро: шахты сквозные от приямка подвала до 50-го этажа (одни и те же координаты), лестницы, тамбур-шлюз и лифтовой холл на каждом этаже.
        var shafts = plan.Packages.Select(p => p.Rooms.Where(r => r.Kind == LayoutRoomKind.LiftShaft).Select(r => (r.X0, r.Z0, r.X1, r.Z1)).ToList()).ToList();
        bool core = plan.Core.Lifts == 4 && plan.Core.FireLifts == 1 && plan.Core.Stairs == 2 && plan.Core.StairCategory.Contains("Н2", StringComparison.Ordinal) &&
                    shafts.All(x => x.Count == 4 && x.SequenceEqual(shafts[0])) && plan.Basement!.Rooms.Count(r => r.Kind == LayoutRoomKind.LiftShaft && r.Name.Contains("приямок", StringComparison.Ordinal)) == 4 &&
                    floors.All(p => p.Rooms.Any(r => r.Kind == LayoutRoomKind.Vestibule) && p.Rooms.Any(r => r.Kind == LayoutRoomKind.LiftLobby) && p.Rooms.Count(r => r.Kind == LayoutRoomKind.Stair) == 2);
        // Квартиры: прихожая, санузел, кухня и комнаты в каждой; ТЭП.
        var tep = plan.Tep();
        bool apartments = floors.All(p => p.Apartments.Count >= 8 && p.Apartments.All(ap => p.Rooms.Count(r => r.ApartmentId == ap.Id && r.Kind == LayoutRoomKind.Hall) == 1 &&
                              p.Rooms.Any(r => r.ApartmentId == ap.Id && r.Kind == LayoutRoomKind.Bathroom) && p.Rooms.Any(r => r.ApartmentId == ap.Id && LayoutRooms.IsKitchen(r.Kind)) &&
                              p.Rooms.Any(r => r.ApartmentId == ap.Id && LayoutRooms.IsDwelling(r.Kind)))) &&
                          tep.Apartments == floors.Sum(p => p.Apartments.Count * 5) && tep.ApartmentArea > 20_000 && tep.LivingArea > 0.4 * tep.ApartmentArea && tep.K1 is > 0.6 and < 0.95 &&
                          floors.SelectMany(p => p.Apartments).Select(ap => ap.Type).Distinct().Count() >= 3 && !plan.GasSupply;
        // Подвал: ИТП, водомерный узел и насосная, ВРУ, венткамера; узлы ввода; магистрали к каждому стояку 1-го пакета.
        var bas = plan.Basement!;
        var vru = bas.Rooms.First(r => r.Kind == LayoutRoomKind.Electrical);
        bool basement = bas.Rooms.Any(r => r.Kind == LayoutRoomKind.Itp) && bas.Rooms.Any(r => r.Kind == LayoutRoomKind.WaterMeter) && bas.Rooms.Any(r => r.Kind == LayoutRoomKind.Ventilation) &&
                        floors[0].Rooms.All(u => !LayoutRooms.IsWet(u.Kind) || u.OverlapArea(vru) < 1e-6) &&
                        new[] { GpzuZoneKind.WaterSupply, GpzuZoneKind.HeatNetwork, GpzuZoneKind.PowerCable, GpzuZoneKind.Sewer }.All(k => plan.Inlets.Any(i => i.Kind == k && i.IntoBasement)) &&
                        floors[0].Risers.All(r => plan.Mains.Any(mn => mn.Kind == r.Kind && Math.Abs(mn.Path[^1].X - r.X) < 1e-6 && Math.Abs(mn.Path[^1].Z - r.Z) < 1e-6)) &&
                        plan.Mains.Any(mn => mn.Kind == RiserKind.ColdWater) && plan.Mains.Any(mn => mn.Kind == RiserKind.Sewer) && plan.Mains.Any(mn => mn.Kind == RiserKind.HotWater);
        var clean = MepStackValidatorService.Validate(plan);
        Check(report, ref failures, packages && multi && core && apartments && basement && clean.Passed,
            string.Create(W7Ru, $"корпус 50 эт. ({request.Length:0.0} × {request.Width:0.0} м, подвал −3,600): пакетов {floors.Count} по 5 этажей + подвал [{packages}]; мульти-план — {string.Join(" | ", tiers.Select(t => t.Title))}, оси 8 × 4 [{multi}]; ") +
            string.Create(W7Ru, $"ядро: лифтов {plan.Core.Lifts} (пожарный {plan.Core.FireLifts}) — шахты сквозные от приямка до 50-го этажа, лестниц {plan.Core.Stairs} ({plan.Core.StairCategory}), лифтовой холл и тамбур-шлюз на каждом этаже [{core}]; ") +
            string.Create(W7Ru, $"квартир {tep.Apartments:N0}, S кв. {tep.ApartmentArea:N0} м², S жил. {tep.LivingArea:N0} м², K1 {tep.K1:0.00}, газа нет (> 10 эт.) [{apartments}]; ") +
            string.Create(W7Ru, $"подвал: ИТП, водомерный узел и насосная, ВРУ (не под мокрыми зонами), венткамера, вводы В1/Т1/кабель/К1, магистралей {plan.Mains.Count} к {floors[0].Risers.Count} стоякам [{basement}]; проверка зон без нарушений [{clean.Passed}]"));

        // ── Мокрая зона 6-го этажа над жилой комнатой 5-го: зеркальная планировка пакета 6–10 → нарушения → согласование одним шагом.
        var scene = new SceneStateService();
        scene.LoadScene(elements, [new BuildingGroup { Id = g, Name = "Жилой корпус 50 эт." }], recordUndo: false, clearHistory: true, axes: []);
        FloorTierLayoutService.Apply(scene, plan, "Планировки");
        var mirrored = FloorTierLayoutService.Mirror(plan, floors[1].Id);
        FloorTierLayoutService.Apply(scene, mirrored, "Планировки: этажи 6–10 — зеркальная");
        var bad = MepStackValidatorService.Validate(scene.BuildingLayout!);
        var wet = bad.Findings.Where(f => f.Severity == StackSeverity.Critical && f.UpperPackageId == floors[1].Id && f.LowerPackageId == floors[0].Id &&
                                          f.Code is "MEP-KITCHEN" or "MEP-WET-LIVING" or "MEP-WET-CORRIDOR").ToList();
        bool flagged = wet.Count > 0 && wet.Any(f => f.Title.Contains("6-го этажа", StringComparison.Ordinal) && f.Title.Contains("5-го этажа", StringComparison.Ordinal) && f.Norm.Contains("9.22", StringComparison.Ordinal)) &&
                       bad.Findings.Any(f => f.Code == "MEP-RISER-BREAK" && f.Title.Contains("6-й этаж → 5-й этаж", StringComparison.Ordinal));
        int depth = scene.UndoDepth;
        var (aligned, changes) = MepStackValidatorService.Align(scene.BuildingLayout!);
        FloorTierLayoutService.Apply(scene, aligned, $"Согласование стояков и мокрых зон ({changes})");
        var good = MepStackValidatorService.Validate(scene.BuildingLayout!);
        bool oneStep = scene.UndoDepth == depth + 1 && ReferenceEquals(scene.BuildingLayout, aligned);
        scene.Undo();
        bool undone = ReferenceEquals(scene.BuildingLayout, mirrored);
        scene.Redo();
        bool redone = ReferenceEquals(scene.BuildingLayout, aligned);
        bool sameAsSource = FloorTierLayoutService.Signature(aligned.Package(floors[1].Id)!) == FloorTierLayoutService.Signature(plan.Package(floors[1].Id)!);
        Check(report, ref failures, flagged && good.Passed && changes > 0 && oneStep && undone && redone && sameAsSource,
            string.Create(W7Ru, $"вертикальная совместимость: зеркальная планировка пакета 6–10 — нарушений {bad.Critical} ({string.Join("; ", wet.Take(2).Select(f => f.Title))}; разрывов стояков {bad.RiserBreaks}) [{flagged}]; ") +
            string.Create(W7Ru, $"«Согласовать стояки и мокрые зоны» — исправлений {changes}, нарушений {bad.Critical} → {good.Critical}, ровно 1 шаг журнала [{oneStep}], Undo → зеркальная [{undone}], Redo [{redone}], мокрые блоки над мокрыми блоками пакета 1–5 [{sameAsSource}]"));

        // ── Газ: дом 9 эт. с газом — кухни с плитами, ввод на 1-й этаж; газовая кухня 6-го этажа над жилой комнатой 5-го; газ в подвале и выше 10 эт.
        const string g9 = "w7-gas";
        var nine = EditableSiteTerrainService.CreateStructure(SiteStructureKind.HighRise, g9, 9);
        var req9 = FloorTierLayoutService.RequestFor(nine, [new BuildingGroup { Id = g9, Name = "Дом 9 эт." }], g9, null)! with { GasAvailable = true };
        var gas = DeveloperLayoutGenerator.Generate(req9);
        var gasOk = MepStackValidatorService.Validate(gas);
        var gasFloors = gas.Floors.ToList();
        var gasBad = MepStackValidatorService.Validate(FloorTierLayoutService.Mirror(gas, gasFloors[1].Id));
        var withBasementGas = gas.With(inlets: gas.Inlets.Select(i => i.Kind == GpzuZoneKind.GasPipeline ? i with { IntoBasement = true } : i).ToList());
        var gasBasement = MepStackValidatorService.Validate(withBasementGas);
        var (gasFixed, _) = MepStackValidatorService.Align(withBasementGas);
        var highGas = plan.With(packages: plan.Packages.Select(p => p with { Rooms = p.Rooms.Select(r => r.Kind == LayoutRoomKind.Kitchen ? r with { Kind = LayoutRoomKind.KitchenGas } : r).ToList() }).ToList());
        var highBad = MepStackValidatorService.Validate(highGas);
        var (highFixed, _) = MepStackValidatorService.Align(highGas);
        bool gasChecks = gas.GasSupply && gasFloors.All(p => p.Rooms.Any(r => r.Kind == LayoutRoomKind.KitchenGas)) && gas.Inlets.Any(i => i.Kind == GpzuZoneKind.GasPipeline && !i.IntoBasement) && gasOk.Passed &&
                         gasBad.Findings.Any(f => f.Code == "MEP-GAS-LIVING" && f.Title.Contains("6-го этажа", StringComparison.Ordinal)) &&
                         gasBasement.Findings.Any(f => f.Code == "MEP-GAS-BASEMENT") && MepStackValidatorService.Validate(gasFixed).Passed &&
                         highBad.Findings.Any(f => f.Code == "MEP-GAS-HIGHRISE") && !highFixed.GasSupply && highFixed.Packages.All(p => p.Rooms.All(r => r.Kind != LayoutRoomKind.KitchenGas)) &&
                         MepStackValidatorService.Validate(highFixed).Passed;
        Check(report, ref failures, gasChecks,
            $"газовые зоны: дом 9 эт. — кухни с газовыми плитами, ввод газопровода на 1-й этаж в обход подвала, без нарушений [{gas.GasSupply && gasOk.Passed}]; зеркальный пакет — газовая кухня 6-го этажа над жилой комнатой 5-го выявлена; " +
            $"ввод газа в подвал — нарушение, согласование переносит на 1-й этаж; газовые кухни в доме 50 эт. — нарушение «Газ в квартирах здания 50 эт.», согласование переводит кухни на электроплиты [{gasChecks}]");
        return failures;
    }
}
#endif
