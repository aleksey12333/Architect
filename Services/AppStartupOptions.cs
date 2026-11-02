namespace AIArchitectStudio.Services;

/// <summary>
/// Параметры запуска главного окна. Без стартовой оболочки (Debug-сценарии AIA_DEBUG_*) ViewModel, как раньше, сразу
/// загружает Пресет 1 (коттедж); после заставки и стартового окна сцену выбирает пользователь (<see cref="StartupPresetId"/> = null).
/// </summary>
public sealed class AppStartupOptions
{
    public string? StartupPresetId { get; set; } = PresetScenesService.Cottage;
}
