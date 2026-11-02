using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.Verification;

/// <summary>Строка «Было → Стало» правки: параметр, значения до и после (текст для листа согласования).</summary>
public sealed record BimParameterDelta(string Parameter, string Before, string After);

/// <summary>
/// Предложенное локальным аудитором исправление сцены внешнего генератора: дефект, действие, параметры «до/после», прогноз
/// K_исп и влияние на смету каркаса. <see cref="IsAccepted"/> — отметка пользователя в листе согласования (по умолчанию — да).
/// </summary>
public sealed class ProposedBimFix
{
    public string FixId { get; init; } = string.Empty;
    public string ElementId { get; init; } = string.Empty;
    public string ElementName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;

    /// <summary>Роль аудитора: «Конструктор КЖ (СП 63)», «Геометрический аудитор», «Инженер по нагрузкам»…</summary>
    public string AuthorRole { get; init; } = string.Empty;

    public string RoleColorHex { get; init; } = "#0A84FF";
    public string RoleGlyph { get; init; } = "";
    public string DefectDescription { get; init; } = string.Empty;
    public string ProposedActionDescription { get; init; } = string.Empty;

    /// <summary>Пункт норм, на котором основана правка.</summary>
    public string NormReference { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;
    public VerificationSeverity Severity { get; init; }

    /// <summary>Параметры элемента до правки (имя → значение).</summary>
    public IReadOnlyDictionary<string, string> BeforeSnapshot { get; init; } = new Dictionary<string, string>();

    /// <summary>Параметры после правки (моделирование на копии сцены).</summary>
    public IReadOnlyDictionary<string, string> AfterSnapshot { get; init; } = new Dictionary<string, string>();

    /// <summary>Изменившиеся параметры «Было → Стало» (порядок снимка).</summary>
    public IReadOnlyList<BimParameterDelta> Changes { get; init; } = [];

    public double KBefore { get; init; }
    public double KAfter { get; init; }
    public double DeltaKisp => KAfter - KBefore;

    /// <summary>Изменение стоимости каркаса (бетон, арматура и закладные по ведомости), ₽: плюс — дороже.</summary>
    public double DeltaCostRub { get; init; }

    public int ElementsAdded { get; init; }
    public IReadOnlyList<FixAction> Actions { get; init; } = [];
    public IReadOnlyList<string> AffectedElementIds { get; init; } = [];
    public double[]? Location { get; init; }
    public double Extent { get; init; } = 4;

    public bool IsAccepted { get; set; } = true;

    /// <summary>Проход доводки: 1 — замечания сцены генератора, 2+ — элементы под новой нагрузкой после правок предыдущих проходов.</summary>
    public int Pass { get; init; } = 1;

    public bool IsCascade => Pass > 1;

    public override string ToString() => $"{FixId} {ElementName}: {DefectDescription} → {ProposedActionDescription}";
}

/// <summary>Цены сметы каркаса для оценки влияния правок, ₽.</summary>
public sealed record GateCostRates(double ConcretePerM3, double RebarPerTon)
{
    public static readonly GateCostRates Default = new(8500, 75000);
}

/// <summary>Итог проверки достоверности сцены внешнего генератора: сводка и лист правок.</summary>
public sealed class VerificationGateReport
{
    public string Source { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public VerificationReport GeneratorReport { get; init; } = new();
    public List<ProposedBimFix> Fixes { get; } = [];

    /// <summary>Замечания без автоматического исправления (остаются на решение пользователя).</summary>
    public List<VerificationFinding> Unresolved { get; } = [];

    public int VerifiersRun => GeneratorReport.Timings.Count;

    /// <summary>Проверщиков без замечаний.</summary>
    public int VerifiersPassed => GeneratorReport.Timings.Count(t => t.Findings == 0);

    public double KBefore { get; init; }
    public double KAfterAll { get; set; }
    public int ClashesBefore { get; init; }
    public int ClashesAfterAll { get; set; }
    public int CriticalAfterAll { get; set; }
    public double CostBeforeRub { get; init; }
    public double CostAfterAllRub { get; set; }

    /// <summary>Глубина истории Undo сразу после выпуска сцены генератора («Отменить всё» откатывает до неё и ещё один шаг).</summary>
    public int UndoDepth { get; init; }

    /// <summary>Сцена генератора выпущена шагом Undo (иначе «Отменить всё» недоступно).</summary>
    public bool CanRevertGeneration { get; init; }

    public double ElapsedMs { get; set; }

    /// <summary>Двухпроходная доводка: после правок первого прохода колонны, стены и балки проверяются под новой нагрузкой.</summary>
    public bool CascadeEnabled { get; init; } = true;

    /// <summary>Проходов доводки (1 — без каскада).</summary>
    public int Passes { get; set; } = 1;

    /// <summary>K_исп max после каждого прохода (моделирование на копии сцены): [после прохода 1, после прохода 2, …].</summary>
    public List<double> PassUtilization { get; } = [];

    public int CascadeFixCount => Fixes.Count(f => f.IsCascade);

    public bool IsClean => Fixes.Count == 0 && Unresolved.Count(f => f.Severity != VerificationSeverity.Info) == 0;

    /// <summary>Индикатор достоверности: «Проверено по СП 63 · 0 коллизий» и т.п.</summary>
    public string BadgeText(bool afterFixes) => afterFixes
        ? $"Проверено по СП 63 · K_исп {K(KAfterAll)} · коллизий {ClashesAfterAll}"
        : $"Проверено по СП 63 · K_исп {K(KBefore)} · коллизий {ClashesBefore}";
}

/// <summary>Итог применения отмеченных правок.</summary>
public sealed record GateApplyResult(int Accepted, int Applied, IReadOnlyList<string> Log, IReadOnlyList<string> Failed, double KAfter, int ClashesAfter, double CostAfterRub)
{
    /// <summary>Дополнительных действий стабилизации на живой сцене (элементы, созданные правками, и остаточные перегрузки).</summary>
    public int StabilizationActions { get; init; }
}

/// <summary>
/// Шлюз достоверности: после ответа внешнего генератора (облачный или веб-проектировщик) сцена проходит конвейер
/// C#-проверщиков (<see cref="BimVerificationPipeline"/>: прочность, продавливание, прогибы, устойчивость, пересечения,
/// висящие элементы, СП 63/СП 20, проёмы и лестницы), а локальный аудитор (<see cref="DesignFixEngine"/>) готовит пакет
/// исправлений. Каждое исправление моделируется отдельно на копии сцены: «Было → Стало», ΔK_исп и Δсметы каркаса. Пользователь
/// принимает отмеченные правки одним шагом Undo (<see cref="ApplyAccepted"/>), оставляет сцену генератора или откатывает генерацию.
/// </summary>
public static class VerificationGateService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static VerificationGateReport Inspect(SceneStateService scene, StructuralLoadSettings loads, string source, GateCostRates? rates = null,
        BimVerificationPipeline? pipeline = null, SolarLocation? location = null, int? undoDepth = null, bool cascade = true)
    {
        // Проверка может идти на копии живой сцены (в фоне): глубину истории «Отменить всё» передаёт вызывающий код.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        rates ??= GateCostRates.Default;
        pipeline ??= BimVerificationPipeline.CreateDefault();
        var report = pipeline.Run(VerificationContext.FromScene(scene, loads, location));
        var baseAnalysis = report.Analysis;
        double baseCost = FrameCost(scene, baseAnalysis, loads, rates);
        var gate = new VerificationGateReport
        {
            Source = source, GeneratorReport = report, KBefore = baseAnalysis.MaxUtilization, ClashesBefore = report.ClashCount, CostBeforeRub = baseCost,
            UndoDepth = undoDepth ?? scene.Transactions.UndoCount, CanRevertGeneration = (undoDepth ?? scene.Transactions.UndoCount) > 0,
            CascadeEnabled = cascade
        };

        var signatures = new HashSet<string>(StringComparer.Ordinal);
        int n = 0;
        foreach (var finding in report.Findings)
        {
            if (finding.Severity == VerificationSeverity.Info) continue;
            var option = finding.HasFixes ? CouncilPlanner.Pick(finding, FixAuthor.Automatic) : null;
            if (option is null)
            {
                gate.Unresolved.Add(finding);
                continue;
            }
            // Разные проверщики могут предложить одно и то же действие (KE и SPAN — утолщение той же плиты): одна правка.
            string signature = string.Join(";", option.Actions.Select(a => $"{a.Kind}:{a.TargetId}:{a.Value.ToString("R", Inv)}"));
            if (!signatures.Add(signature)) continue;
            // Правка, целиком покрытая более ранней (то же действие с тем же элементом и не меньшим значением), — не отдельная карточка.
            if (gate.Fixes.Count > 0 && option.Actions.All(a => gate.Fixes.Any(f => f.Actions.Any(b => b.Kind == a.Kind && b.TargetId == a.TargetId && b.Value >= a.Value && b.Value2 >= a.Value2))))
                continue;
            gate.Fixes.Add(Describe(scene, scene, finding, option, ++n, baseAnalysis, baseCost, loads, rates));
        }
        if (cascade && gate.Fixes.Count > 0) BuildCascadeFixes(scene, gate, loads, rates, pipeline);

        if (gate.Fixes.Count > 0)
        {
            var all = Simulate(scene, gate.Fixes, loads, rates, pipeline);
            gate.KAfterAll = all.K;
            gate.ClashesAfterAll = all.Clashes;
            gate.CriticalAfterAll = all.Critical;
            gate.CostAfterAllRub = all.Cost;
        }
        else
        {
            gate.KAfterAll = gate.KBefore;
            gate.ClashesAfterAll = gate.ClashesBefore;
            gate.CriticalAfterAll = report.CriticalCount;
            gate.CostAfterAllRub = baseCost;
        }
        sw.Stop();
        gate.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return gate;
    }

    /// <summary>Прогноз по набору правок на копии сцены: K_исп max, коллизии, критичные замечания, стоимость каркаса.</summary>
    public static (double K, int Clashes, int Critical, double Cost) Simulate(SceneStateService scene, IEnumerable<ProposedBimFix> fixes, StructuralLoadSettings loads,
        GateCostRates? rates = null, BimVerificationPipeline? pipeline = null)
    {
        rates ??= GateCostRates.Default;
        var sandbox = DesignFixEngine.Sandbox(scene);
        DesignFixEngine.Apply(sandbox, fixes.SelectMany(f => f.Actions).ToList());
        var analysis = DesignFixEngine.Analyze(sandbox, loads);
        int clashes = ClashDetectionService.Detect(sandbox.Elements.ToList()).Count;
        int critical = pipeline is null ? 0 : pipeline.Run(VerificationContext.FromScene(sandbox, loads)).CriticalCount;
        return (analysis.MaxUtilization, clashes, critical, FrameCost(sandbox, analysis, loads, rates));
    }

    /// <summary>
    /// «Принять выбранные правки»: действия отмеченных исправлений применяются к живой сцене одной транзакцией журнала
    /// (<see cref="SceneStateService.BeginUndoTransaction"/>) — ровно один шаг Undo. Без отмеченных правок сцена не меняется.
    /// </summary>
    public static GateApplyResult ApplyAccepted(SceneStateService scene, VerificationGateReport gate, StructuralLoadSettings loads, GateCostRates? rates = null)
    {
        rates ??= GateCostRates.Default;
        var accepted = gate.Fixes.Where(f => f.IsAccepted).ToList();
        if (accepted.Count == 0) return new GateApplyResult(0, 0, [], [], gate.KBefore, gate.ClashesBefore, gate.CostBeforeRub);
        FixOutcome outcome;
        int stabilization = 0;
        using (var transaction = scene.BeginUndoTransaction("Правки локального аудитора"))
        {
            outcome = DesignFixEngine.Apply(scene, accepted.SelectMany(f => f.Actions).ToList());
            // «Принять все правки» двухпроходного листа: остаточные перегрузки (в т.ч. у элементов, созданных правками, —
            // их Id известны только после применения) доводятся в том же шаге, чтобы K_исп всей сцены стал ≤ 1,0.
            if (gate.CascadeEnabled && accepted.Count == gate.Fixes.Count)
            {
                var residual = Stabilize(scene, loads, BimVerificationPipeline.CreateDefault());
                stabilization = residual.Applied;
                outcome.Applied += residual.Applied;
                outcome.Log.AddRange(residual.Log);
            }
            if (outcome.Applied == 0) transaction.Discard();
        }
        var analysis = DesignFixEngine.Analyze(scene, loads);
        int clashes = ClashDetectionService.Detect(scene.Elements.ToList()).Count;
        return new GateApplyResult(accepted.Count, outcome.Applied, outcome.Log, outcome.Failed, analysis.MaxUtilization, clashes, FrameCost(scene, analysis, loads, rates))
        {
            StabilizationActions = stabilization
        };
    }

    // ───────────────────────────── Двухпроходная доводка ─────────────────────────────

    /// <summary>Наибольшее число проходов доводки (первый — замечания генератора, остальные — каскад под новой нагрузкой).</summary>
    public const int MaxPasses = 4;

    /// <summary>
    /// Каскад правок: правки предыдущих проходов моделируются на копии сцены (утолщённые плиты тяжелее — растут усилия в колоннах
    /// и стенах ниже), сцена проверяется заново, и для элементов, перегруженных под новой нагрузкой, добавляются правки следующего
    /// прохода — пока K_исп max модели ≤ 1,0 или до <see cref="MaxPasses"/>. Правки ссылаются только на элементы живой сцены;
    /// «Было» — значения живой сцены, «Стало» и K — по моделированию после всех предыдущих проходов. Возвращает число новых правок.
    /// </summary>
    public static int BuildCascadeFixes(SceneStateService scene, VerificationGateReport gate, StructuralLoadSettings loads, GateCostRates? rates = null,
        BimVerificationPipeline? pipeline = null)
    {
        rates ??= GateCostRates.Default;
        pipeline ??= BimVerificationPipeline.CreateDefault();
        var signatures = gate.Fixes.Select(f => Signature(f.Actions)).ToHashSet(StringComparer.Ordinal);
        int n = gate.Fixes.Count, added = 0;
        gate.PassUtilization.Clear();
        for (int pass = 2; pass <= MaxPasses; pass++)
        {
            var work = DesignFixEngine.Sandbox(scene);
            DesignFixEngine.Apply(work, gate.Fixes.SelectMany(f => f.Actions).ToList());
            var analysis = DesignFixEngine.Analyze(work, loads);
            gate.PassUtilization.Add(analysis.MaxUtilization);
            if (analysis.MaxUtilization <= 1.0) break;
            double workCost = FrameCost(work, analysis, loads, rates);
            var report = pipeline.Run(VerificationContext.FromScene(work, loads));
            int before = added;
            foreach (var finding in report.Findings)
            {
                if (finding.Severity != VerificationSeverity.Critical || !IsLoadFinding(finding.Code)) continue;
                var option = finding.HasFixes ? CouncilPlanner.Pick(finding, FixAuthor.Automatic) : null;
                option ??= WallOption(work, finding, loads);
                // Правка элемента, созданного предыдущим проходом, не переносится на живую сцену (у него ещё нет Id) — его доводит Stabilize.
                if (option is null || option.Actions.Any(a => a.TargetId.Length > 0 && scene.FindById(a.TargetId) is null)) continue;
                if (!signatures.Add(Signature(option.Actions))) continue;
                var fix = Describe(scene, work, finding, option, ++n, analysis, workCost, loads, rates, pass);
                gate.Fixes.Add(fix);
                added++;
            }
            gate.Passes = pass;
            if (added == before) break;
        }
        return added;
    }

    /// <summary>
    /// Стабилизация живой сцены (внутри открытой транзакции вызывающего): пока K_исп max &gt; 1,0, перегруженные элементы получают
    /// автоматические варианты проверщиков (колонны, балки, плиты, фундамент; стены — утолщение) — до трёх проходов.
    /// </summary>
    public static FixOutcome Stabilize(SceneStateService scene, StructuralLoadSettings loads, BimVerificationPipeline pipeline, int maxPasses = 3)
    {
        var total = new FixOutcome();
        for (int pass = 0; pass < maxPasses; pass++)
        {
            if (DesignFixEngine.Analyze(scene, loads).MaxUtilization <= 1.0) break;
            var report = pipeline.Run(VerificationContext.FromScene(scene, loads));
            var actions = new List<FixAction>();
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finding in report.Findings)
            {
                if (finding.Severity != VerificationSeverity.Critical || !IsLoadFinding(finding.Code)) continue;
                var option = (finding.HasFixes ? CouncilPlanner.Pick(finding, FixAuthor.Automatic) : null) ?? WallOption(scene, finding, loads);
                if (option is null || !signatures.Add(Signature(option.Actions))) continue;
                actions.AddRange(option.Actions);
            }
            if (actions.Count == 0) break;
            var outcome = DesignFixEngine.Apply(scene, actions);
            total.Applied += outcome.Applied;
            total.Log.AddRange(outcome.Log);
            total.CreatedIds.AddRange(outcome.CreatedIds);
            if (outcome.Applied == 0) break;
        }
        return total;
    }

    /// <summary>Замечания по несущей способности (KE-*) и продавливанию узла (SEIS-NODE) — их правит каскад.</summary>
    private static bool IsLoadFinding(string code) => code.StartsWith("KE", StringComparison.Ordinal) || code == "SEIS-NODE";

    private static string Signature(IEnumerable<FixAction> actions) =>
        string.Join(";", actions.Select(a => $"{a.Kind}:{a.TargetId}:{a.Value.ToString("R", Inv)}"));

    /// <summary>
    /// Перегруженная стена (у проверщика нет вариантов для стен): утолщение шагом 50 мм до K_исп ≤ 0,95 (не толще 600 мм),
    /// подбор моделированием на копии сцены. Ненесущая перегородка не утолщается — её нагрузка только собственный вес.
    /// </summary>
    private static ProposedFixOption? WallOption(SceneStateService work, VerificationFinding finding, StructuralLoadSettings loads)
    {
        if (finding.Code != "KE-WALL" || finding.AffectedElementIds.FirstOrDefault() is not { } id || work.FindById(id) is not { Category: BimCategory.Wall } wall) return null;
        double t0 = wall.Depth;
        for (double t = Math.Ceiling((t0 + 0.05) / 0.05 - 1e-9) * 0.05; t <= 0.6 + 1e-9; t += 0.05)
        {
            var actions = new List<FixAction> { new(FixActionKind.ThickenWall, wall.Id) { Value = Math.Round(t, 3) } };
            var sandbox = DesignFixEngine.Sandbox(work);
            DesignFixEngine.Apply(sandbox, actions);
            double k = DesignFixEngine.Analyze(sandbox, loads).For(wall.Id) is { } r ? MemberK(r) : 0;
            if (k > DesignFixEngine.TargetUtilization) continue;
            return new ProposedFixOption
            {
                Author = FixAuthor.Automatic,
                Title = $"Увеличить толщину стены с {Mm(t0)} до {Mm(t)} мм",
                Description = $"Ось стены сохраняется: K_исп {K(finding.Utilization ?? 0)} → {K(k)}",
                Actions = actions,
                PredictedUtilization = k,
                Priority = 1,
                PreservesArchitecture = true
            };
        }
        return null;
    }

    /// <summary>«Отменить всё»: откат правок (если приняты) и шага генерации — сцена возвращается к состоянию до запроса.</summary>
    public static bool RevertGeneration(SceneStateService scene, VerificationGateReport gate)
    {
        if (!gate.CanRevertGeneration) return false;
        bool any = false;
        while (scene.Transactions.UndoCount >= gate.UndoDepth && scene.CanUndo)
        {
            if (!scene.Undo()) break;
            any = true;
        }
        return any;
    }

    // ───────────────────────────── Описание правки ─────────────────────────────

    /// <param name="origin">Живая сцена: «Было» правки — её значения.</param>
    /// <param name="scene">Сцена моделирования: живая (проход 1) или копия после правок предыдущих проходов (каскад).</param>
    private static ProposedBimFix Describe(SceneStateService origin, SceneStateService scene, VerificationFinding finding, ProposedFixOption option, int number,
        StructuralAnalysisResult baseAnalysis, double baseCost, StructuralLoadSettings loads, GateCostRates rates, int pass = 1)
    {
        string targetId = option.Actions.Select(a => a.TargetId).FirstOrDefault(id => scene.FindById(id) is not null) ?? finding.AffectedElementIds.FirstOrDefault() ?? string.Empty;
        var element = scene.FindById(targetId) ?? finding.AffectedElementIds.Select(scene.FindById).OfType<ArchitecturalElement>().FirstOrDefault();
        var originElement = element is null ? null : origin.FindById(element.Id) ?? element;
        var sandbox = DesignFixEngine.Sandbox(scene);
        var outcome = DesignFixEngine.Apply(sandbox, option.Actions);
        var analysis = DesignFixEngine.Analyze(sandbox, loads);
        var ids = finding.AffectedElementIds.Concat(element is null ? [] : [element.Id]).Distinct().ToList();
        // Сравниваются те же проверки, что у проверщика: KE — прочность/устойчивость/срез/прогиб/грунт (продавливание узла —
        // отдельное замечание SEIS-NODE), узел — отношение продавливания, остальные — полный K_исп элементов.
        bool member = finding.Code.StartsWith("KE", StringComparison.Ordinal);
        double kBefore = finding.Utilization ?? DesignFixEngine.MaxK(baseAnalysis, ids);
        var afterIds = ids.Concat(outcome.CreatedIds).ToList();
        double kAfter = finding.Code == "SEIS-NODE" ? PunchingAfter(finding, analysis)
            : member ? afterIds.Select(analysis.For).OfType<ElementStructuralResult>().Select(MemberK).DefaultIfEmpty(0).Max()
            : DesignFixEngine.MaxK(analysis, afterIds);
        double cost = FrameCost(sandbox, analysis, loads, rates);

        var before = originElement is null ? new Dictionary<string, string>() : SnapshotOf(originElement);
        var afterElement = element is null ? null : sandbox.FindById(element.Id);
        var after = afterElement is null ? new Dictionary<string, string> { ["Элемент"] = "удалён" } : SnapshotOf(afterElement);
        var changes = new List<BimParameterDelta>();
        foreach (var (key, value) in before)
            if (after.TryGetValue(key, out var v) && v != value) changes.Add(new BimParameterDelta(key, value, v));
        if (afterElement is null && element is not null) changes.Add(new BimParameterDelta("Элемент", element.DisplayTitle, "удалён"));
        if (outcome.CreatedIds.Count > 0)
        {
            var created = outcome.CreatedIds.Select(sandbox.FindById).OfType<ArchitecturalElement>().ToList();
            string what = string.Join(", ", created.GroupBy(e => CategoryTitle(e.Category)).Select(g => $"{g.Key} × {g.Count()}"));
            changes.Add(new BimParameterDelta("Новые элементы", "—", what));
        }
        changes.Add(new BimParameterDelta("K_исп", K(kBefore), K(kAfter)));

        var (role, color, glyph, norm) = Role(finding);
        string action = option.Title + (kAfter > 0 ? $"; K_исп {(kAfter < kBefore ? "снижен" : "составит")} до {K(kAfter)}" : string.Empty);
        return new ProposedBimFix
        {
            FixId = $"fix-{number:D2}",
            ElementId = element?.Id ?? targetId,
            ElementName = element?.DisplayTitle ?? finding.Title,
            Category = element is null ? finding.Category : CategoryTitle(element.Category),
            AuthorRole = role,
            RoleColorHex = color,
            RoleGlyph = glyph,
            NormReference = norm,
            Code = finding.Code,
            Severity = finding.Severity,
            DefectDescription = pass > 1 ? $"Проход {pass} · под новой нагрузкой после правок: {Defect(finding, element, kBefore)}" : Defect(finding, element, kBefore),
            Pass = pass,
            ProposedActionDescription = action,
            BeforeSnapshot = before,
            AfterSnapshot = after,
            Changes = changes,
            KBefore = kBefore,
            KAfter = kAfter,
            DeltaCostRub = cost - baseCost,
            ElementsAdded = sandbox.Elements.Count - scene.Elements.Count,
            Actions = option.Actions,
            AffectedElementIds = ids,
            Location = finding.Location ?? (element is null ? null : [element.X, element.Y, element.Z]),
            Extent = finding.Extent
        };
    }

    private static double MemberK(ElementStructuralResult r) =>
        Math.Max(Math.Max(Math.Max(r.KStrength, r.KStability), Math.Max(r.KShear, r.KDeflection)), r.KSoil);

    /// <summary>K узла продавливания после правки (замечание SEIS-NODE: колонна + плита).</summary>
    private static double PunchingAfter(VerificationFinding f, StructuralAnalysisResult analysis)
    {
        if (f.Code != "SEIS-NODE" || f.AffectedElementIds.Count < 2) return 0;
        string column = f.AffectedElementIds[0], slab = f.AffectedElementIds[1];
        return analysis.PunchingNodes.Where(n => n.ColumnId == column && n.SlabId == slab).Select(n => n.Ratio).DefaultIfEmpty(0).Max();
    }

    private static string Defect(VerificationFinding f, ArchitecturalElement? e, double k)
    {
        if (e is null) return f.Title;
        string section = e.Category switch
        {
            BimCategory.Column => $"сечение {Mm(e.Width)} × {Mm(e.Depth)} мм",
            BimCategory.Beam => $"сечение {Mm(e.Depth)} × {Mm(e.Height)} мм",
            BimCategory.Slab or BimCategory.Roof or BimCategory.Foundation or BimCategory.CantileverSlab => $"толщина {Mm(e.Height)} мм",
            BimCategory.Wall => $"толщина {Mm(e.Depth)} мм",
            _ => string.Empty
        };
        if (f.Code.StartsWith("KE", StringComparison.Ordinal) || f.Code == "SEIS-NODE")
        {
            string what = f.Code == "SEIS-NODE" ? "продавливание" : "недостаточная несущая способность";
            return $"{e.DisplayTitle}: K_исп = {K(k)} > 1,0 ({what}{(section.Length > 0 ? ", " + section : string.Empty)})";
        }
        return f.Title.Length > 0 ? f.Title : e.DisplayTitle;
    }

    private static (string Role, string Color, string Glyph, string Norm) Role(VerificationFinding f)
    {
        string code = f.Code;
        if (code == "SEIS-NODE") return ("Конструктор КЖ (СП 63)", "#FF9F0A", "", "СП 63.13330.2018, п. 8.1.46 (продавливание)");
        if (code.StartsWith("KE-COL", StringComparison.Ordinal)) return ("Конструктор КЖ (СП 63)", "#FF9F0A", "", "СП 63.13330.2018, п. 8.1.14 (сжатые элементы)");
        if (code.StartsWith("KE", StringComparison.Ordinal)) return ("Конструктор КЖ (СП 63)", "#FF9F0A", "", "СП 63.13330.2018, п. 8.1.4; СП 20.13330.2016, прил. Д");
        if (code.StartsWith("SPAN", StringComparison.Ordinal)) return ("Инженер по прогибам (СП 20)", "#64D2FF", "", "СП 20.13330.2016, табл. Д.1; СП 63.13330.2018, п. 8.2");
        if (code.StartsWith("VLP", StringComparison.Ordinal)) return ("Инженер по нагрузкам", "#30D158", "", "СП 20.13330.2016, п. 4.2 (путь нагрузки)");
        if (code.StartsWith("SEIS", StringComparison.Ordinal)) return ("Инженер по устойчивости (СП 14)", "#BF5AF2", "", "СП 14.13330.2018, п. 6.2");
        if (code.StartsWith("ACCESS", StringComparison.Ordinal)) return ("Нормоконтроль (СП 1.13130)", "#FF453A", "", "СП 1.13130.2020, п. 4.3; СП 54.13330.2022");
        if (code.StartsWith("SUN", StringComparison.Ordinal)) return ("Инсоляция (СанПиН)", "#FFD60A", "", "СанПиН 1.2.3685-21, табл. 5.58");
        return ("Геометрический аудитор", "#5AC8FA", "", "Согласованность модели: коллизии и привязки");
    }

    /// <summary>Параметры элемента для листа «Было → Стало» (сечение/толщина, положение, материал, профиль).</summary>
    public static Dictionary<string, string> SnapshotOf(ArchitecturalElement e)
    {
        var s = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (e.Category)
        {
            case BimCategory.Column:
                s["Сечение, мм"] = $"{Mm(e.Width)} × {Mm(e.Depth)}";
                s["Высота, мм"] = Mm(e.Height);
                break;
            case BimCategory.Beam:
                s["Сечение, мм"] = $"{Mm(e.Depth)} × {Mm(e.Height)}";
                s["Длина, мм"] = Mm(e.Width);
                break;
            case BimCategory.Slab or BimCategory.Roof or BimCategory.Foundation or BimCategory.CantileverSlab or BimCategory.Terrace:
                s["Толщина, мм"] = Mm(e.Height);
                s["Низ, отм."] = e.BaseY.ToString("+0.000;-0.000;0.000", Ru);
                break;
            case BimCategory.Wall:
                s["Толщина, мм"] = Mm(e.Depth);
                s["Длина, мм"] = Mm(e.Width);
                break;
            default:
                s["Габарит W × H, мм"] = $"{Mm(e.Width)} × {Mm(e.Height)}";
                break;
        }
        s["Положение X / Z, мм"] = $"{Mm(e.X)} / {Mm(e.Z)}";
        s["Материал"] = e.Material;
        if (ElementTypes.IsProfiledType(e.Category)) s["Профиль"] = e.EffectiveProfile;
        return s;
    }

    private static string CategoryTitle(BimCategory c) => c switch
    {
        BimCategory.Column => "Колонна",
        BimCategory.Beam => "Балка",
        BimCategory.Slab => "Плита",
        BimCategory.Roof => "Покрытие",
        BimCategory.Foundation => "Фундамент",
        BimCategory.Wall => "Стена",
        BimCategory.Window => "Окно",
        BimCategory.Door => "Дверь",
        BimCategory.WallOpening => "Проём",
        BimCategory.SlabOpening => "Отверстие",
        BimCategory.Stairs => "Лестница",
        _ => c.ToString()
    };

    /// <summary>Стоимость каркаса по ведомости: бетон + арматура (рабочая, А240, закладные) по ценам сметы.</summary>
    public static double FrameCost(SceneStateService scene, StructuralAnalysisResult analysis, StructuralLoadSettings loads, GateCostRates rates)
    {
        var schedule = QuantityScheduleService.Calculate(scene.Elements.ToList(), analysis.UtilizationById, BimAnalyticsService.PunchingReinforcedIds(analysis), loads.Rebar);
        return QuantityScheduleService.EstimateFrameCost(schedule, rates.ConcretePerM3, rates.RebarPerTon).Total;
    }
}
