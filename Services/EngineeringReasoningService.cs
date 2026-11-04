using System.Diagnostics;
using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.Verification;

namespace AIArchitectStudio.Services;

/// <summary>
/// Инженерные показатели состояния сцены для цепочки рассуждений: K_исп max (и элемент), σ_max (МПа) несущих элементов, наибольший
/// прогиб f (мм), стоимость каркаса (₽), коллизии, висящие (без опоры) элементы и перегруженные (K &gt; 1).
/// </summary>
public sealed record ReasoningSnapshot(double KMax, string KMaxElement, double SigmaMaxMPa, double DeflectionMaxMm, double CostRub,
    int Clashes, int Unsupported, int Overloaded, int Elements, IReadOnlyDictionary<string, double> Utilization)
{
    /// <summary>K_исп max среди элементов (Id); 0 — элементов нет в результате.</summary>
    public double KOf(IEnumerable<string> ids) => ids.Select(id => Utilization.GetValueOrDefault(id)).DefaultIfEmpty(0).Max();

    public bool Passes(double kLimit = 1.0) => KMax <= kLimit && Unsupported == 0;
}

/// <summary>
/// Гипотеза для проверки в песочнице: заголовок, применимый норматив, действие над копией сцены (null — успешно, иначе причина
/// отказа: например, логика проёмов не нашла места) и атомарные действия для итогового решения.
/// </summary>
public sealed class EngineeringHypothesis
{
    public string Title { get; init; } = string.Empty;
    public string Norm { get; init; } = string.Empty;
    public Func<SceneStateService, string?> Apply { get; init; } = _ => null;
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>Гипотеза только читает модель (аудит, ведомость): проверка K ≤ 1 не отсеивает её, а фиксирует состояние.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Произвольная полезная нагрузка вызывающего (например, выбранный типоразмер ГОСТ).</summary>
    public object? Tag { get; init; }
}

/// <summary>Запрос цепочки рассуждений: задача, сцена, нагрузки, затронутые элементы, ограничения и варианты решения.</summary>
public sealed class ReasoningRequest
{
    public string Task { get; init; } = string.Empty;
    public required SceneStateService Scene { get; init; }
    public required StructuralLoadSettings Loads { get; init; }
    public GateCostRates Rates { get; init; } = GateCostRates.Default;

    /// <summary>Распознанные намерения и исходные данные задачи (строки фазы 1).</summary>
    public IReadOnlyList<string> Intents { get; init; } = [];

    public IReadOnlyList<ArchitecturalElement> Affected { get; init; } = [];

    /// <summary>Применимые пункты норм (фаза 2).</summary>
    public IReadOnlyList<string> Norms { get; init; } = [];

    /// <summary>Варианты в порядке предпочтения: выбирается первый, прошедший самопроверку (или самый дешёвый — <see cref="PreferCheapest"/>).</summary>
    public IReadOnlyList<EngineeringHypothesis> Hypotheses { get; init; } = [];

    public bool PreferCheapest { get; init; }

    /// <summary>Предельный K_исп решения (по умолчанию 1,0). Если исходная сцена уже перегружена, решение не должно её ухудшать.</summary>
    public double KLimit { get; init; } = 1.0;

    /// <summary>Элементы, ради которых меняется модель (подбор сечения): их K_исп после решения — не больше <see cref="KLimit"/>.</summary>
    public IReadOnlyList<string> FocusIds { get; init; } = [];
}

/// <summary>
/// Движок структурированных инженерных рассуждений. Перед проектной задачей или изменением конструкции строит
/// <see cref="ReasoningTrace"/> из пяти обязательных фаз: декомпозиция и исходные данные → гипотезы и нормативы → численная
/// проверка каждой гипотезы в песочнице (копия сцены, <see cref="StructuralAnalysisService"/>, <see cref="FeaLogicalAuditService"/>,
/// стоимость каркаса по ведомости) → самопроверка (коллизии, логика проёмов <see cref="BuildingLogicGuard"/>, висящие узлы,
/// перегрузки) с автоматическим переходом к следующей гипотезе → итоговое решение и атомарные действия. Живая сцена не меняется.
/// </summary>
public static class EngineeringReasoningService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Показатели сцены расчётным ядром (без изменения сцены).</summary>
    public static ReasoningSnapshot Measure(SceneStateService scene, StructuralLoadSettings loads, GateCostRates? rates = null)
    {
        var elements = scene.Elements.ToList();
        var groups = scene.BuildingGroups.ToList();
        var model = AnalyticalModelBuilder.Build(elements, groups, loads);
        var result = StructuralAnalysisService.Analyze(model);
        var stresses = FeaLogicalAuditService.Stresses(model, result);
        var worst = result.Elements.OrderByDescending(p => p.Value.Utilization).FirstOrDefault();
        string worstTitle = worst.Key is null ? "—" : scene.FindById(worst.Key)?.DisplayTitle ?? worst.Key;
        double sigma = stresses.Where(s => !s.Reference).Select(s => s.SigmaMax).DefaultIfEmpty(0).Max();
        double cost = VerificationGateService.FrameCost(scene, result, loads, rates ?? GateCostRates.Default);
        int clashes = ClashDetectionService.Detect(elements).Count;
        int unsupported = CountUnsupported(elements);
        return new ReasoningSnapshot(result.MaxUtilization, worstTitle, sigma, result.MaxDeflectionMm, cost, clashes, unsupported,
            result.Elements.Values.Count(r => r.Utilization > 1.0), elements.Count, result.UtilizationById);
    }

    /// <summary>Висящие элементы по аудитору солвера (на клонах: флаги живой сцены не меняются).</summary>
    public static int CountUnsupported(IReadOnlyList<ArchitecturalElement> elements)
    {
        var clones = elements.Select(e => e.Clone()).ToList();
        foreach (var c in clones) c.HasPhysicsError = false;
        var report = new SolverReport();
        StructuralAuditor.Audit(clones, report);
        return clones.Count(c => c.HasPhysicsError);
    }

    /// <summary>
    /// Цепочка рассуждений по запросу. <paramref name="chosen"/> — выбранная гипотеза (null — ни одна не применима, решение не принято).
    /// </summary>
    public static ReasoningTrace Reason(ReasoningRequest request, out EngineeringHypothesis? chosen)
    {
        var sw = Stopwatch.StartNew();
        var trace = new ReasoningTrace { Task = request.Task };
        var scene = request.Scene;
        var loads = request.Loads;

        // ── 1. Декомпозиция и исходные данные ──
        var decomposition = trace.Step(ReasoningPhase.Decomposition);
        decomposition.Lines.Add("Задача: " + (request.Task.Length > 0 ? request.Task : "проверка текущей модели"));
        foreach (var line in request.Intents) decomposition.Lines.Add(line);
        decomposition.Lines.Add(DescribeScene(scene));
        decomposition.Lines.Add($"Нагрузки: снег S0 = {loads.SnowKgM2.ToString("0", Ru)} кг/м², ветер w0 = {loads.WindKgM2.ToString("0", Ru)} кг/м², " +
                                $"сейсмичность {loads.Seismicity} баллов, γn = {loads.GammaN.ToString("0.0#", Ru)}, бетон {StructuralNorms.Title(loads.Concrete)}, арматура {StructuralNorms.Title(loads.Rebar)}");
        if (request.Affected.Count > 0)
            decomposition.Lines.Add($"Затронуто элементов: {request.Affected.Count} — " + string.Join(", ", request.Affected.Take(6).Select(e => "«" + e.DisplayTitle + "»")) +
                                    (request.Affected.Count > 6 ? $" и ещё {request.Affected.Count - 6}" : string.Empty));
        decomposition.Lines.Add($"Ограничения: K_исп ≤ {request.KLimit.ToString("0.0#", Ru)}, без новых коллизий и висящих узлов, проёмы — по логике простенков (BuildingLogicGuard), каждое действие — в одном шаге журнала");

        // ── 2. Гипотезы и нормативы ──
        var hypotheses = trace.Step(ReasoningPhase.Hypotheses);
        foreach (var norm in request.Norms.Distinct()) hypotheses.Lines.Add("Норматив: " + norm);
        if (request.Hypotheses.Count == 0) hypotheses.Lines.Add("Вариантов изменения нет — оценивается текущее состояние модели");
        for (int i = 0; i < request.Hypotheses.Count; i++)
        {
            var h = request.Hypotheses[i];
            hypotheses.Lines.Add($"Гипотеза {i + 1}: {h.Title}" + (h.Norm.Length > 0 ? $" ({h.Norm})" : string.Empty));
        }

        // ── 3. Численная проверка в песочнице ──
        var sandbox = trace.Step(ReasoningPhase.Sandbox);
        sandbox.IsComputed = true;
        var before = Measure(scene, loads, request.Rates);
        sandbox.Lines.Add($"Исходное состояние: K_исп max = {F(before.KMax)} («{before.KMaxElement}»), σ_max = {F(before.SigmaMaxMPa)} МПа, f_max = {F(before.DeflectionMaxMm, "0.0")} мм, " +
                          $"стоимость каркаса {Rub(before.CostRub)}, коллизий {before.Clashes}, без опоры {before.Unsupported}");
        double limit = Math.Max(request.KLimit, before.KMax > request.KLimit ? before.KMax : 0);
        var evaluated = new List<(EngineeringHypothesis H, ReasoningHypothesis R, ReasoningSnapshot? After, string? Error)>();
        foreach (var h in request.Hypotheses)
        {
            var copy = DesignFixEngine.Sandbox(scene);
            string? error;
            try
            {
                error = h.Apply(copy);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
            {
                error = ex.Message;
            }
            var record = new ReasoningHypothesis { Title = h.Title, Norm = h.Norm, KBefore = before.KMax };
            if (error is not null)
            {
                record.RejectReason = error;
                evaluated.Add((h, record, null, error));
                sandbox.Lines.Add($"«{h.Title}»: не применима — {error}");
                trace.Hypotheses.Add(record);
                continue;
            }
            var after = Measure(copy, loads, request.Rates);
            record.KAfter = after.KMax;
            record.SigmaMaxMPa = after.SigmaMaxMPa;
            record.DeflectionMm = after.DeflectionMaxMm;
            record.CostRub = after.CostRub;
            record.Clashes = after.Clashes;
            string focusText = request.FocusIds.Count > 0 ? $" (целевых {F(before.KOf(request.FocusIds))} → {F(after.KOf(request.FocusIds))})" : string.Empty;
            // Исходные значения — в первой строке фазы; здесь — «стало» и разница стоимости.
            sandbox.Lines.Add($"«{h.Title}»: K_исп {F(after.KMax)}{focusText}, σ_max {F(after.SigmaMaxMPa)} МПа, f {F(after.DeflectionMaxMm, "0.0")} мм, " +
                              $"стоимость {Rub(after.CostRub)} ({Delta(after.CostRub - before.CostRub)})");
            evaluated.Add((h, record, after, null));
            trace.Hypotheses.Add(record);
        }

        // ── 4. Самопроверка и отсев ──
        var check = trace.Step(ReasoningPhase.SelfCheck);
        foreach (var (h, r, after, error) in evaluated)
        {
            if (after is null) continue;
            var reasons = new List<string>();
            if (!h.ReadOnly && after.KMax > limit + 1e-9) reasons.Add($"K_исп = {F(after.KMax)} > {F(limit)}");
            if (!h.ReadOnly && request.FocusIds.Count > 0 && after.KOf(request.FocusIds) is var kFocus && kFocus > request.KLimit + 1e-9)
                reasons.Add($"K_исп целевых элементов = {F(kFocus)} > {F(request.KLimit)}");
            if (after.Clashes > before.Clashes) reasons.Add($"новые коллизии: {after.Clashes - before.Clashes}");
            if (after.Unsupported > before.Unsupported) reasons.Add($"висящие узлы: +{after.Unsupported - before.Unsupported}");
            if (after.Elements == 0 && before.Elements > 0) reasons.Add("модель опустела");
            r.Passed = reasons.Count == 0;
            r.RejectReason = string.Join("; ", reasons);
            check.Lines.Add(r.Passed
                ? $"«{h.Title}»: коллизий {after.Clashes} (было {before.Clashes}), без опоры {after.Unsupported}, перегружено {after.Overloaded} — пройдено"
                : $"«{h.Title}»: отклонена — {r.RejectReason}");
        }
        var passing = evaluated.Where(x => x.R.Passed).ToList();
        var pick = request.PreferCheapest ? passing.OrderBy(x => x.R.CostRub).FirstOrDefault() : passing.FirstOrDefault();
        int pickIndex = pick.H is null ? -1 : evaluated.FindIndex(x => ReferenceEquals(x.H, pick.H));
        // Отсеянные до выбранной гипотезы — автоматические корректировки шага.
        trace.Corrections = pickIndex < 0 ? evaluated.Count(x => !x.R.Passed) : evaluated.Take(pickIndex).Count(x => !x.R.Passed);
        if (trace.Corrections > 0 && pickIndex >= 0)
        {
            check.Status = ReasoningStepStatus.Corrected;
            check.Lines.Add($"Корректировка шага: отсеяно гипотез {trace.Corrections}, принят следующий проходящий вариант «{pick.H!.Title}»");
        }
        if (request.Hypotheses.Count == 0)
        {
            check.Lines.Add($"Текущая модель: коллизий {before.Clashes}, без опоры {before.Unsupported}, перегружено {before.Overloaded}" +
                            (before.Passes(request.KLimit) ? " — замечаний нет" : " — есть замечания"));
            if (!before.Passes(request.KLimit)) check.Status = ReasoningStepStatus.Corrected;
        }
        else if (pickIndex < 0)
        {
            check.Status = ReasoningStepStatus.Failed;
            check.Lines.Add("Ни один вариант не прошёл самопроверку — изменение не выполняется");
        }

        // ── 5. Итоговое решение ──
        var decision = trace.Step(ReasoningPhase.Decision);
        chosen = pick.H;
        var final = pick.After ?? before;
        sandbox.Metrics.Add(new ReasoningMetric("K_исп max", before.KMax, final.KMax, string.Empty));
        sandbox.Metrics.Add(new ReasoningMetric("σ_max", before.SigmaMaxMPa, final.SigmaMaxMPa, "МПа"));
        sandbox.Metrics.Add(new ReasoningMetric("f_max", before.DeflectionMaxMm, final.DeflectionMaxMm, "мм", "0.0"));
        sandbox.Metrics.Add(new ReasoningMetric("Стоимость каркаса", before.CostRub, final.CostRub, "₽", "N0"));
        if (chosen is not null)
        {
            pick.R.Chosen = true;
            decision.Lines.Add($"Принято: «{chosen.Title}» — K_исп {F(before.KMax)} → {F(final.KMax)}, стоимость {Delta(final.CostRub - before.CostRub)}");
            trace.Actions.AddRange(chosen.Actions.Count > 0 ? chosen.Actions : [chosen.Title]);
            decision.Metrics.Add(new ReasoningMetric("K_исп max", before.KMax, final.KMax, string.Empty));
            trace.IsVerified = true;
        }
        else if (request.Hypotheses.Count == 0)
        {
            decision.Lines.Add(before.Passes(request.KLimit)
                ? $"Изменений не требуется: K_исп max = {F(before.KMax)} ≤ {F(request.KLimit)}"
                : $"Требуется доводка каркаса: K_исп max = {F(before.KMax)} у «{before.KMaxElement}», перегружено элементов {before.Overloaded}");
            trace.IsVerified = true;
        }
        else
        {
            decision.Status = ReasoningStepStatus.Failed;
            decision.Lines.Add("Решение не принято: проверенные варианты нарушают ограничения (см. фазу 4)");
        }
        sandbox.Status = evaluated.Any(x => x.Error is not null) ? ReasoningStepStatus.Corrected : ReasoningStepStatus.Passed;
        trace.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return trace;
    }

    /// <summary>Сводка модели для фазы 1: постройки, этажи, оси, элементы по категориям.</summary>
    public static string DescribeScene(SceneStateService scene)
    {
        var counts = scene.Elements.GroupBy(e => CategoryTitle(e.Category)).OrderByDescending(g => g.Count()).Take(6)
            .Select(g => $"{g.Key} {g.Count()}");
        int levels = scene.Elements.Count == 0 ? 0 : scene.MaxLevelIndex + 1;
        int axes = scene.GridAxes.Count;
        return $"Модель: построек {Math.Max(1, scene.BuildingGroups.Count)}, этажей {levels}, явных осей {axes}, элементов {scene.Elements.Count} ({string.Join(", ", counts)})";
    }

    /// <summary>Подпись категории для сводок (именительный падеж, мн. ч.).</summary>
    public static string CategoryTitle(BimCategory c) => c switch
    {
        BimCategory.Column => "колонн",
        BimCategory.Beam => "балок",
        BimCategory.Wall => "стен",
        BimCategory.Slab => "плит",
        BimCategory.Foundation => "фундаментов",
        BimCategory.Window => "окон",
        BimCategory.Door => "дверей",
        BimCategory.Roof => "кровель",
        BimCategory.Stairs => "маршей",
        BimCategory.Core => "ядер",
        BimCategory.CurtainWall => "витражей",
        _ => "прочих"
    };

    private static string F(double v, string format = "0.00") => v.ToString(format, Ru);

    private static string Rub(double v) => v.ToString("N0", Ru) + " ₽";

    private static string Delta(double v) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("N0", Ru) + " ₽";
}
