using AIArchitectStudio.Models;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AIArchitectStudio.Services;

/// <summary>Снимок состояния студии для записи в .aia.</summary>
public sealed record ProjectSnapshot(
    string ViewMode,
    int ActiveLevelFilter,
    SceneProjectParameters Parameters,
    IReadOnlyList<ChatMessageModel> ChatHistory,
    string? Environment = null);

/// <summary>
/// Проектные файлы .aia: выбор пути через FileOpenPicker / FileSavePicker (привязка к окну через
/// <c>WindowNative.GetWindowHandle</c>), сериализация сцены, истории чата ИИ и параметров, загрузка в <see cref="SceneStateService"/>.
/// </summary>
public sealed class ProjectFileService
{
    private readonly SceneStateService _scene;

    public ProjectFileService(SceneStateService scene) => _scene = scene;

    // ───────────────────────────── Диалоги ─────────────────────────────

    public async Task<string?> PickOpenPathAsync(Window owner)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List
        };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        picker.FileTypeFilter.Add(SceneProjectFile.Extension);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSavePathAsync(Window owner)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = SafeFileName(_scene.ProjectName)
        };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        picker.FileTypeChoices.Add("Architect Studio Pro", [SceneProjectFile.Extension]);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    // ───────────────────────────── Сохранение / открытие ─────────────────────────────

    /// <summary>Сохраняет текущую сцену с историей чата и параметрами; помечает проект сохранённым.</summary>
    public async Task<SceneProjectFile> SaveAsync(string path, ProjectSnapshot snapshot)
    {
        var file = new SceneProjectFile
        {
            ProjectName = Path.GetFileNameWithoutExtension(path),
            AppVersion = typeof(ProjectFileService).Assembly.GetName().Version?.ToString(3) ?? "1.0",
            ViewMode = snapshot.ViewMode,
            ActiveLevelFilter = snapshot.ActiveLevelFilter,
            Parameters = snapshot.Parameters,
            BuildingGroups = _scene.BuildingGroups.Select(g => g.Clone()).ToList(),
            Elements = _scene.Elements.Select(e => e.Clone()).ToList(),
            GridAxes = _scene.GridAxes.Select(a => a.Clone()).ToList(),
            PinnedDimensions = _scene.PinnedDimensions.Select(d => d.Clone()).ToList(),
            ChatHistory = snapshot.ChatHistory.ToList(),
            Site = SiteWorkspaceRecord.From(_scene.SitePlan, snapshot.Environment)
        };
        await file.SaveAsync(path);

        _scene.ProjectName = file.ProjectName;
        _scene.ProjectFilePath = path;
        _scene.IsDirty = false;
        return file;
    }

    /// <summary>Открывает .aia и заменяет сцену (история Undo очищается). Остальные параметры применяет вызывающая сторона.</summary>
    public async Task<SceneProjectFile> OpenAsync(string path)
    {
        var file = await SceneProjectFile.LoadAsync(path);
        _scene.LoadScene(file.Elements, file.BuildingGroups, recordUndo: false, clearHistory: true, axes: file.GridAxes, dimensions: file.PinnedDimensions);
        _scene.ProjectName = string.IsNullOrWhiteSpace(file.ProjectName) ? Path.GetFileNameWithoutExtension(path) : file.ProjectName;
        _scene.ProjectFilePath = path;
        _scene.ActiveLevelFilter = file.ActiveLevelFilter <= _scene.MaxLevelIndex ? file.ActiveLevelFilter : SceneStateService.AllLevels;
        // Волна 6: участок ГПЗУ проекта — без шага журнала (открытие файла начинает историю заново).
        if (file.Site?.ToPlan() is { } site) _scene.RestoreSitePlan(site);
        _scene.IsDirty = false;
        return file;
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Проект" : cleaned;
    }
}
