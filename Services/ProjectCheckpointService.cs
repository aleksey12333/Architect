using AIArchitectStudio.Models;
using AIArchitectStudio.Services.Verification;

namespace AIArchitectStudio.Services;

/// <summary>
/// Контрольная точка (именованная версия варианта проекта): копии элементов, построек, осей и закреплённых размеров на момент
/// создания и показатели варианта — стоимость каркаса (бетон + арматура + закладные по ведомости), K_исп max, объём бетона,
/// расход арматуры и число элементов. Копии не связаны с документом: правки сцены точку не меняют.
/// </summary>
public sealed class ProjectCheckpoint
{
    public string Id { get; init; } = "cp_" + Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>Шагов Undo в журнале в момент создания (место точки на шкале истории).</summary>
    public int HistoryDepth { get; init; }

    public double FrameCostRub { get; init; }
    public double MaxUtilization { get; init; }
    public string GoverningElement { get; init; } = string.Empty;
    public double ConcreteM3 { get; init; }
    public double RebarKg { get; init; }
    public int ElementCount { get; init; }
    public int StoreyCount { get; init; }

    internal IReadOnlyList<ArchitecturalElement> Elements { get; init; } = [];
    internal IReadOnlyList<BuildingGroup> Groups { get; init; } = [];
    internal IReadOnlyList<StructuralGridAxis> Axes { get; init; } = [];
    internal IReadOnlyList<PinnedDimension> Dimensions { get; init; } = [];

    /// <summary>Каркас обеспечен (K_исп ≤ 1,0).</summary>
    public bool IsSafe => MaxUtilization <= 1.0;
}

/// <summary>Сравнение варианта с текущей сценой (или с другой точкой): Δ стоимости каркаса и Δ K_исп.</summary>
public sealed record CheckpointComparison(ProjectCheckpoint Checkpoint, double CostDeltaRub, double UtilizationDelta, double ConcreteDeltaM3, int ElementDelta);

/// <summary>
/// Контрольные точки и версии сцены: «Создать контрольную точку» снимает копию варианта и считает его показатели (КЭ-расчёт,
/// ведомость, смета каркаса по ценам проекта); «Перейти» загружает вариант в сцену одним шагом журнала транзакций
/// («Вариант: …»), его можно отменить Undo. Сравнение вариантов — по стоимости каркаса в рублях и K_исп max.
/// </summary>
public static class ProjectCheckpointService
{
    /// <summary>Снимок текущей сцены как контрольная точка с показателями варианта.</summary>
    public static ProjectCheckpoint Capture(SceneStateService scene, string name, StructuralLoadSettings loads, GateCostRates? rates = null)
    {
        rates ??= GateCostRates.Default;
        var elements = scene.Elements.Select(e => e.Clone()).ToList();
        var groups = scene.BuildingGroups.Select(g => g.Clone()).ToList();
        var (cost, k, governing, concrete, rebar) = Metrics(elements, groups, loads, rates);
        return new ProjectCheckpoint
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Вариант {DateTime.Now:HH:mm}" : name.Trim(),
            HistoryDepth = scene.Transactions.UndoCount,
            FrameCostRub = cost,
            MaxUtilization = k,
            GoverningElement = governing,
            ConcreteM3 = concrete,
            RebarKg = rebar,
            ElementCount = elements.Count(e => !ElementTypes.IsVoidType(e.Category)),
            StoreyCount = elements.Count == 0 ? 0 : elements.Max(e => e.LevelIndex + Math.Max(1, e.RepeatLevels)),
            Elements = elements,
            Groups = groups,
            Axes = scene.GridAxes.Select(a => a.Clone()).ToList(),
            Dimensions = scene.PinnedDimensions.Select(d => d.Clone()).ToList()
        };
    }

    /// <summary>Показатели варианта: стоимость каркаса, K_исп max и определяющий элемент, бетон (м³), арматура (кг).</summary>
    public static (double Cost, double K, string Governing, double Concrete, double Rebar) Metrics(IReadOnlyList<ArchitecturalElement> elements,
        IReadOnlyList<BuildingGroup> groups, StructuralLoadSettings loads, GateCostRates rates)
    {
        if (elements.Count == 0) return (0, 0, string.Empty, 0, 0);
        var analysis = StructuralAnalysisService.Analyze(elements.ToList(), groups.ToList(), loads);
        var schedule = QuantityScheduleService.Calculate(elements.ToList(), analysis.UtilizationById, BimAnalyticsService.PunchingReinforcedIds(analysis), loads.Rebar);
        var estimate = QuantityScheduleService.EstimateFrameCost(schedule, rates.ConcretePerM3, rates.RebarPerTon);
        var top = analysis.Elements.OrderByDescending(p => p.Value.Utilization).FirstOrDefault();
        string governing = top.Key is null ? string.Empty : elements.FirstOrDefault(e => e.Id == top.Key)?.DisplayTitle ?? top.Key;
        return (estimate.Total, analysis.MaxUtilization, governing, estimate.ConcreteM3, estimate.RebarKg);
    }

    /// <summary>
    /// «Перейти к контрольной точке»: вариант загружается в сцену новыми копиями (точка остаётся неизменной) одним шагом журнала
    /// «Вариант: имя» — Undo возвращает сцену до перехода.
    /// </summary>
    public static void Restore(SceneStateService scene, ProjectCheckpoint checkpoint)
    {
        using (scene.BeginUndoTransaction($"Вариант: {checkpoint.Name}"))
            scene.LoadScene(checkpoint.Elements.Select(e => e.Clone()).ToList(), checkpoint.Groups.Select(g => g.Clone()).ToList(), recordUndo: true,
                axes: checkpoint.Axes.Select(a => a.Clone()).ToList(), dimensions: checkpoint.Dimensions.Select(d => d.Clone()).ToList());
    }

    /// <summary>Сравнение точки с базой (текущей сценой или другой точкой): плюс — дороже / нагруженнее.</summary>
    public static CheckpointComparison Compare(ProjectCheckpoint checkpoint, double baseCost, double baseK, double baseConcrete, int baseElements) =>
        new(checkpoint, checkpoint.FrameCostRub - baseCost, checkpoint.MaxUtilization - baseK, checkpoint.ConcreteM3 - baseConcrete, checkpoint.ElementCount - baseElements);
}
