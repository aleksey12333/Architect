using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIArchitectStudio.ViewModels;

/// <summary>Модули «Ведомость объёмов и сталей» (со сметой монолитного каркаса) и «Визуализация 3D-армирования (КЖ)».</summary>
public sealed partial class MainViewModel
{
    /// <summary>Схема армирования поверх модели (кнопка «Армирование» верхнего бара).</summary>
    [ObservableProperty]
    public partial bool IsRebarView { get; set; }

    /// <summary>Подпись пилюли армирования: стержни и хомуты выбранного элемента или число элементов.</summary>
    [ObservableProperty]
    public partial string RebarInfoText { get; set; } = string.Empty;

    /// <summary>Смета монолитного каркаса, сохранённая в открытом проекте (.aia) или при последнем сохранении.</summary>
    [ObservableProperty]
    public partial ProjectCostEstimate? ProjectEstimate { get; set; }

    partial void OnIsRebarViewChanged(bool value)
    {
        if (value && !IsRebarModule) IsRebarView = false;
    }

    /// <summary>Ведомость объёмов по текущей сцене с K_исп и узлами продавливания последнего экспресс-расчёта.</summary>
    public QuantitySchedule BuildQuantitySchedule()
    {
        var analysis = Structure.Current;
        return QuantityScheduleService.Calculate(Scene.Elements.ToList(), analysis.UtilizationById,
            BimAnalyticsService.PunchingReinforcedIds(analysis), Settings.Current.StructuralLoads.Rebar);
    }

    /// <summary>Ведомость по значениям свойства пользователя (окно «Ведомость объёмов», «Группировать по»): те же K_исп и продавливание.</summary>
    public ParameterQuantityBreakdown BuildParameterBreakdown(ParameterDefinition definition)
    {
        var analysis = Structure.Current;
        return QuantityScheduleService.GroupByParameter(Scene.Elements.ToList(), definition, analysis.UtilizationById,
            BimAnalyticsService.PunchingReinforcedIds(analysis), Settings.Current.StructuralLoads.Rebar);
    }

    /// <summary>Стоимость монолитного каркаса по ведомости и заданным ценам (по умолчанию — расценки проекта).</summary>
    public FrameCostEstimate EstimateFrameCost(QuantitySchedule schedule, double? concretePrice = null, double? rebarPrice = null) =>
        QuantityScheduleService.EstimateFrameCost(schedule, concretePrice ?? Settings.Current.ConcretePricePerM3, rebarPrice ?? Settings.Current.RebarPricePerTon);

    /// <summary>
    /// Цены сметы из окна ведомости: становятся расценками проекта (смета «Итого» пересчитывается), проект помечается
    /// изменённым — при сохранении .aia цены и итог каркаса пишутся в <see cref="SceneProjectParameters.CostEstimate"/>.
    /// </summary>
    public bool SetEstimatePrices(double concretePricePerM3, double rebarPricePerTon)
    {
        if (!double.IsFinite(concretePricePerM3) || !double.IsFinite(rebarPricePerTon) || concretePricePerM3 < 0 || rebarPricePerTon < 0) return false;
        var s = Settings.Current;
        if (Math.Abs(s.ConcretePricePerM3 - concretePricePerM3) < 1e-6 && Math.Abs(s.RebarPricePerTon - rebarPricePerTon) < 1e-6) return false;
        SaveSettingsSafe(x =>
        {
            x.ConcretePricePerM3 = concretePricePerM3;
            x.RebarPricePerTon = rebarPricePerTon;
        });
        if (Scene.Elements.Count > 0) Scene.IsDirty = true;
        return true;
    }

    /// <summary>Мини-смета для файла проекта: текущие цены, объём бетона, масса арматуры и итог каркаса.</summary>
    public ProjectCostEstimate BuildCostEstimate()
    {
        var schedule = BuildQuantitySchedule();
        var cost = EstimateFrameCost(schedule);
        // Доказательные строки (поставщик, документ-основание, ручные правки) — только если они есть: иначе блок прежнего формата.
        bool evidence = HasEvidenceLines;
        var priced = evidence ? BuildPricedEstimate(schedule) : null;
        var catalog = ActivePriceCatalog;
        return new ProjectCostEstimate
        {
            ConcretePricePerM3 = cost.ConcretePricePerM3,
            RebarPricePerTon = cost.RebarPricePerTon,
            ConcreteM3 = Math.Round(cost.ConcreteM3, 3),
            RebarKg = Math.Round(cost.RebarKg, 1),
            FrameCostRub = Math.Round(priced?.FrameTotal ?? cost.Total),
            SavedAt = DateTime.Now,
            CatalogName = catalog?.Name,
            CatalogDate = catalog?.UpdatedAt,
            Lines = priced is null ? null : Services.CostEstimateService.ToFileLines(priced)
        };
    }

    /// <summary>Открытый проект со сметой: её цены снова становятся расценками (не сбрасываются к настройкам по умолчанию).</summary>
    private void RestoreCostEstimate(ProjectCostEstimate? estimate)
    {
        ProjectEstimate = estimate?.Clone();
        RestoreEvidence(estimate);
        if (estimate is null) return;
        SaveSettingsSafe(s =>
        {
            s.ConcretePricePerM3 = estimate.ConcretePricePerM3;
            s.RebarPricePerTon = estimate.RebarPricePerTon;
        });
        var c = Loc.Culture;
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("CostRestored", estimate.ConcretePricePerM3.ToString("N0", c), estimate.RebarPricePerTon.ToString("N0", c))));
    }

    /// <summary>Запись о сохранённой смете в чат после «Сохранить смету в проект».</summary>
    public void ReportCostEstimateSaved()
    {
        if (ProjectEstimate is not { } e) return;
        var c = Loc.Culture;
        string message = Loc.Format("CostChat", e.FrameCostRub.ToString("N0", c), e.ConcreteM3.ToString("N2", c), e.ConcretePricePerM3.ToString("N0", c),
            (e.RebarKg / 1000).ToString("N2", c), e.RebarPricePerTon.ToString("N0", c));
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("CostSaved"), message, "AIA-COST-SAVED");
    }
}
