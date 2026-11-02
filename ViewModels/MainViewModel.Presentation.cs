using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>Итог рендера высокого разрешения: файл, размер кадра, фон, объём и время.</summary>
public sealed record HighResRenderResult(string Path, int Width, int Height, bool Transparent, long Bytes, double ElapsedMilliseconds);

/// <summary>
/// Архитектурная подача: интерактивная 3D-рулетка с закреплением размеров (клавиша M), режим «Белый макет (White Clay)»
/// и экспорт рендера 4K PNG (фон неба или прозрачный).
/// </summary>
public sealed partial class MainViewModel
{
    private void InitializePresentation()
    {
        Scene.DimensionsChanged += (_, _) => OnPropertyChanged(nameof(PinnedDimensionsText));
        Loc.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PinnedDimensionsText));
            if (IsMeasureMode) MeasureHintText = Loc.Get("MeasureHintStart");
        };
    }

    // ───────────────────────────── 3D-рулетка ─────────────────────────────

    /// <summary>Счётчик закреплённых размеров на пилюле рулетки: «Размеров: 3».</summary>
    public string PinnedDimensionsText => (Loc.IsRussian ? "Размеров: " : "Dimensions: ") + Scene.PinnedDimensions.Count;

    /// <summary>Режим рулетки: клики во вьюпорте ставят точки P1/P2 с привязкой OSnap вместо выбора элементов.</summary>
    [ObservableProperty]
    public partial bool IsMeasureMode { get; set; }

    /// <summary>Подсказка-пилюля режима рулетки (вьюпорт обновляет по шагу обмера).</summary>
    [ObservableProperty]
    public partial string MeasureHintText { get; set; } = string.Empty;

    partial void OnIsMeasureModeChanged(bool value)
    {
        if (value)
        {
            if (IsWalkthrough) IsWalkthrough = false;
            Scene.SelectedElement = null;
            MeasureHintText = Loc.Get("MeasureHintStart");
        }
        else MeasureHintText = string.Empty;
    }

    [RelayCommand]
    private void ToggleMeasure() => IsMeasureMode = !IsMeasureMode;

    /// <summary>«Закрепить размер на чертеже»: размер P1–P2 попадает в PinnedDimensions (один шаг Undo, .aia, DXF, ГОСТ).</summary>
    public PinnedDimension? PinDimension((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        var dimension = Scene.AddPinnedDimension(a, b);
        if (dimension is null) return null;
        string value = PushPullService.Millimeters(dimension.Length, unit: Loc.Get("Mm"));
        string message = Loc.Format("MeasurePinned", value, Scene.PinnedDimensions.Count);
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("MeasurePinnedTitle"), message, "AIA-MEASURE-PIN");
        return dimension;
    }

    /// <summary>Удаление закреплённого размера (капсула выбранного размера в режиме рулетки).</summary>
    public void RemoveDimension(string id)
    {
        var dimension = Scene.FindDimension(id);
        if (dimension is null || !Scene.RemovePinnedDimension(id)) return;
        string message = Loc.Format("MeasureRemoved", PushPullService.Millimeters(dimension.Length, unit: Loc.Get("Mm")), Scene.PinnedDimensions.Count);
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("MeasurePinnedTitle"), message, "AIA-MEASURE-REMOVE");
    }

    // ───────────────────────────── Белый макет (White Clay) ─────────────────────────────

    /// <summary>
    /// «Макет (White Clay)»: все элементы — единый матовый гипс #F4F4F6, рёбра — графит #2C2C2E поверх заливки,
    /// свет и падающие тени — по Солнцу текущего региона и времени.
    /// </summary>
    [ObservableProperty]
    public partial bool IsClayMode { get; set; }

    /// <summary>Солнечный свет и тени во вьюпорте: панель инсоляции или режим макета.</summary>
    public bool IsSunLighting => IsSolarEnabled || IsClayMode;

    partial void OnIsClayModeChanged(bool value)
    {
        if (value && IsWireframe) IsWireframe = false;
        SolarChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleClay() => IsClayMode = !IsClayMode;

    // ───────────────────────────── Рендер 4K PNG ─────────────────────────────

    public const int RenderWidth = 3840, RenderHeight = 2160;

    /// <summary>Отчёт о рендере 4K: сообщение в чат и уведомление.</summary>
    public void ReportHighResRender(HighResRenderResult result)
    {
        string background = Loc.Get(result.Transparent ? "Render4KBgAlpha" : "Render4KBgSky");
        string message = Loc.Format("Render4KDone", result.Width, result.Height, background, result.Path, result.Bytes / 1024.0, result.ElapsedMilliseconds);
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("Render4KTitle"), message, "AIA-RENDER-4K");
    }

    public void ReportHighResRenderError(Exception ex)
    {
        App.WriteCrashLog("render-4k", ex);
        ShowError(Loc.Get("Render4KTitle"), ex.Message, $"AIA-RENDER-4K-{ex.HResult:X8}", ex.ToString());
    }
}
