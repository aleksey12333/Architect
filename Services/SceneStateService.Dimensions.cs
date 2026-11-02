using System.Collections.ObjectModel;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Закреплённые размеры интерактивной 3D-рулетки: хранение, Undo (в снимке сцены) и уведомление вьюпорта/экспорта.</summary>
public sealed partial class SceneStateService
{
    /// <summary>Пользовательские размеры рулетки (клавиша M): видны во вьюпорте, выгружаются в DXF и на листы ГОСТ.</summary>
    public ObservableCollection<PinnedDimension> PinnedDimensions { get; } = [];

    /// <summary>Изменился набор закреплённых размеров (закрепление, удаление, Undo/Redo, загрузка проекта).</summary>
    public event EventHandler? DimensionsChanged;

    /// <summary>Закрепляет размер между точками a и b (м) одним шагом Undo; null — точки совпадают или не конечны.</summary>
    public PinnedDimension? AddPinnedDimension((double X, double Y, double Z) a, (double X, double Y, double Z) b, string label = "", bool recordUndo = true)
    {
        var dimension = new PinnedDimension(a, b, label);
        if (!dimension.IsValid || dimension.Length < MeasurementService.MinLength) return null;
        if (recordUndo) PushUndo("Закрепление размера");
        PinnedDimensions.Add(dimension);
        IsDirty = true;
        DimensionsChanged?.Invoke(this, EventArgs.Empty);
        return dimension;
    }

    /// <summary>Удаляет закреплённый размер (один шаг Undo).</summary>
    public bool RemovePinnedDimension(string id, bool recordUndo = true)
    {
        if (PinnedDimensions.FirstOrDefault(d => d.Id == id) is not { } dimension) return false;
        if (recordUndo) PushUndo("Удаление размера");
        PinnedDimensions.Remove(dimension);
        IsDirty = true;
        DimensionsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public PinnedDimension? FindDimension(string? id) =>
        string.IsNullOrEmpty(id) ? null : PinnedDimensions.FirstOrDefault(d => d.Id == id);

    private void ReplaceDimensions(IEnumerable<PinnedDimension> dimensions)
    {
        var list = dimensions.Where(d => d.IsValid).ToList();
        if (list.Count == 0 && PinnedDimensions.Count == 0) return;
        PinnedDimensions.Clear();
        foreach (var d in list) PinnedDimensions.Add(d);
        DimensionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
