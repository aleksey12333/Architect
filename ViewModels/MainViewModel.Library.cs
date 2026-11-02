using System.Collections.ObjectModel;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Verification;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>Строка журнала «История изменений»: номер шага, имя, время, затронутые элементы, текущее положение и отменённые шаги.</summary>
public sealed record HistoryEntry(int Index, string Name, DateTime Time, int AffectedElements, int Changes, bool IsCurrent, bool IsUndone, bool IsInitial);

/// <summary>
/// Блок девелопера 3–4: библиотеки типовых элементов ГОСТ/СП (назначение типа, вставка, пользовательские библиотеки
/// .aialib.json), ручное моделирование без генератора («Добавить элемент», дублирование со смещением, разделение пополам,
/// примыкание к уровню), интерактивный журнал транзакций с прыжком к любому шагу и контрольные точки вариантов проекта со
/// сравнением стоимости каркаса и K_исп, справочный центр и интерактивный тур.
/// </summary>
public sealed partial class MainViewModel
{
    private StandardLibraryService? _libraries;

    /// <summary>Библиотеки ГОСТ/СП и пользовательские (создаются при первом обращении; файлы — в папке Libraries, не в settings.json).</summary>
    public StandardLibraryService Libraries => _libraries ??= new StandardLibraryService();

    /// <summary>Только Debug-съёмка и автотест: библиотеки без записи на диск.</summary>
    public void UseSessionLibraries(StandardLibraryService service) => _libraries = service;

    /// <summary>«Библиотека конструкций ГОСТ и СП» (окно открывает MainWindow): аргумент — режим «назначить выделенным» (true) или обзор.</summary>
    public event EventHandler<bool>? LibraryRequested;

    public event EventHandler? HistoryRequested;

    /// <summary>Справочный центр: аргумент — вкладка (quickstart|methods|keys|imports) или null.</summary>
    public event EventHandler<string?>? HelpRequested;

    public event EventHandler? TourRequested;

    [RelayCommand]
    private void OpenLibrary() => LibraryRequested?.Invoke(this, Scene.SelectedElement is not null);

    [RelayCommand]
    private void OpenHistory() => HistoryRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenHelp(string? tab) => HelpRequested?.Invoke(this, tab);

    public void RequestTour() => TourRequested?.Invoke(this, EventArgs.Empty);

    // ───────────────────────────── Видимость инструментов инспектора ─────────────────────────────

    public bool IsManualEditVisible => Scene.SelectedElement is not null;

    public bool CanSplitSelected => !Scene.IsMultiSelection && Scene.SelectedElement is { } e && ManualModelingService.CanSplit(e);

    public bool CanSnapSelected => Scene.SelectedElements.Any(ManualModelingService.CanSnapToLevel);

    /// <summary>Подпись типоразмера выделенного: «К 400×400 · ГОСТ 25628 / Серия 1.020» или «тип не назначен».</summary>
    public string SelectedStandardText =>
        Scene.SelectedElement is { } e && StandardTypeMark.Read(e) is { Length: > 0 } mark
            ? mark + (e.GetUserParameter(StandardTypeMark.StandardGuid) is { IsEmpty: false } v ? " · " + v.AsString() : string.Empty) + (StructuralRole.IsNonBearingPartition(e) ? " · " + Loc.Get("LibNonBearing") : string.Empty)
            : Loc.Get("LibNoType");

    private void RefreshManualTools()
    {
        OnPropertyChanged(nameof(IsManualEditVisible));
        OnPropertyChanged(nameof(CanSplitSelected));
        OnPropertyChanged(nameof(CanSnapSelected));
        OnPropertyChanged(nameof(SelectedStandardText));
    }

    // ───────────────────────────── Библиотека ГОСТ/СП ─────────────────────────────

    /// <summary>Назначить тип выделенным элементам (один шаг журнала); несовместимые по категории — пропускаются с пометкой.</summary>
    public StandardApplyResult ApplyLibraryEntry(StandardLibraryEntry entry)
    {
        var targets = Scene.SelectedElements.Count > 0 ? Scene.SelectedElements.ToList() : Scene.SelectedElement is { } s ? [s] : [];
        var result = StandardLibraryService.Apply(Scene, entry, targets);
        if (result.Applied.Count == 0)
        {
            ShowNotice(Loc.Get("LibTitle"), Loc.Format("LibIncompatible", entry.Caption), "AIA-LIB-TYPE");
            return result;
        }
        string skipped = result.Skipped.Count > 0 ? Loc.Format("LibSkipped", result.Skipped.Count) : string.Empty;
        string message = Loc.Format("LibApplied", entry.Caption, result.Applied.Count, entry.SizeText, skipped);
        ChatMessages.Add(ChatMessageModel.System(message));
        ShowSuccess(Loc.Get("LibTitle"), message, "AIA-LIB-APPLY");
        AfterManualEdit();
        return result;
    }

    /// <summary>Вставить новый элемент выбранного типа на активном этаже.</summary>
    public ManualEditResult InsertLibraryEntry(StandardLibraryEntry entry) => AddManualElement(KindOf(entry), entry);

    private static ManualElementKind KindOf(StandardLibraryEntry entry) => entry.Category switch
    {
        BimCategory.Column => ManualElementKind.Column,
        BimCategory.Beam => ManualElementKind.Beam,
        BimCategory.Wall => ManualElementKind.Wall,
        BimCategory.Foundation => ManualElementKind.Foundation,
        _ => ManualElementKind.Slab
    };

    /// <summary>«Сохранить тип в библиотеку»: тип выделенного элемента — в пользовательскую библиотеку проекта.</summary>
    public StandardLibraryEntry? SaveSelectedTypeToLibrary(string? mark = null)
    {
        if (Scene.SelectedElement is not { } e || !(e.Category is BimCategory.Column or BimCategory.Beam or BimCategory.Wall or BimCategory.Slab or BimCategory.Foundation))
        {
            ShowNotice(Loc.Get("LibTitle"), Loc.Get("LibSaveNeedsSelection"), "AIA-LIB-SAVE");
            return null;
        }
        var entry = Libraries.AddToUserLibrary(StandardLibraryService.CaptureEntry(e, string.IsNullOrWhiteSpace(mark) ? null : mark.Trim()));
        ShowSuccess(Loc.Get("LibTitle"), Loc.Format("LibSaved", entry.Mark, entry.SizeText), "AIA-LIB-SAVE");
        return entry;
    }

    // ───────────────────────────── Ручное моделирование ─────────────────────────────

    [RelayCommand]
    private void AddElementOfKind(string? kind)
    {
        if (Enum.TryParse<ManualElementKind>(kind, true, out var k)) AddManualElement(k);
    }

    /// <summary>«Добавить элемент»: новый элемент на активном этаже (окно/дверь — в выделенную стену); выделяется, один шаг журнала.</summary>
    public ManualEditResult AddManualElement(ManualElementKind kind, StandardLibraryEntry? entry = null)
    {
        var result = ManualModelingService.AddElement(Scene, kind, entry);
        Report(result, "AIA-MANUAL-ADD");
        if (result.Success && result.Created.Count > 0) Scene.SelectedElement = result.Created[0];
        return result;
    }

    [RelayCommand]
    private void DuplicateWithOffset(string? mode)
    {
        if (Scene.SelectedElement is not { } primary) return;
        var list = Scene.SelectedElements.Count > 0 ? Scene.SelectedElements.ToList() : [primary];
        var (dx, dz) = ManualModelingService.DefaultDuplicateOffset(primary);
        var result = mode switch
        {
            "up" => ManualModelingService.Duplicate(Scene, list, 0, 0, floorUp: true),
            "z" => ManualModelingService.Duplicate(Scene, list, 0, Math.Round(Math.Max(primary.Depth, primary.Width) + 1, 3)),
            _ => ManualModelingService.Duplicate(Scene, list, dx, dz)
        };
        Report(result, "AIA-MANUAL-DUP");
        if (result.Success && result.Created.Count > 0)
        {
            var roots = result.Created.Where(c => c.HostElementId is null || result.Created.All(o => o.Id != c.HostElementId)).ToList();
            Scene.SetSelection(roots, roots[0]);
        }
    }

    [RelayCommand]
    private void SplitSelected()
    {
        if (Scene.SelectedElement is not { } e) return;
        var result = ManualModelingService.Split(Scene, e);
        Report(result, "AIA-MANUAL-SPLIT");
        if (result.Success) Scene.SetSelection([e, .. result.Created], result.Created[0]);
    }

    [RelayCommand]
    private void SnapSelectedToLevel()
    {
        var list = Scene.SelectedElements.Count > 0 ? Scene.SelectedElements.ToList() : Scene.SelectedElement is { } s ? [s] : [];
        Report(ManualModelingService.SnapToLevel(Scene, list), "AIA-MANUAL-LEVEL");
    }

    private void Report(ManualEditResult result, string code)
    {
        if (!result.Success)
        {
            ShowNotice(Loc.Get("ManualTitle"), result.Message, code);
            return;
        }
        ChatMessages.Add(ChatMessageModel.System(result.Message));
        ShowSuccess(result.StepName.Length > 0 ? result.StepName : Loc.Get("ManualTitle"), result.Message, code);
        AfterManualEdit();
    }

    private void AfterManualEdit()
    {
        SyncInspector();
        RefreshLayers();
        RefreshSelectionInfo();
        RefreshManualTools();
    }

    // ───────────────────────────── История изменений и версии ─────────────────────────────

    /// <summary>
    /// Шкала истории: «Исходное состояние» (0), совершённые шаги (1…N, последний — текущий) и отменённые шаги Redo (N+1…),
    /// в хронологическом порядке.
    /// </summary>
    public List<HistoryEntry> HistoryEntries()
    {
        var journal = Scene.Transactions;
        journal.PeekUndo();
        int undo = journal.UndoCount;
        var list = new List<HistoryEntry> { new(0, Loc.Get("VersionInitial"), journal.UndoStack.Count > 0 ? journal.UndoStack[0].CreatedAt : DateTime.Now, 0, 0, undo == 0, false, true) };
        for (int i = 0; i < journal.UndoStack.Count; i++)
        {
            var t = journal.UndoStack[i];
            list.Add(new HistoryEntry(i + 1, t.Name, t.CreatedAt, t.AffectedElementCount, t.Changes.Count, i + 1 == undo, false, false));
        }
        // Стек Redo: верх стека — ближайший следующий шаг.
        for (int i = journal.RedoStack.Count - 1, n = undo + 1; i >= 0; i--, n++)
        {
            var t = journal.RedoStack[i];
            list.Add(new HistoryEntry(n, t.Name, t.CreatedAt, t.AffectedElementCount, t.Changes.Count, false, true, false));
        }
        return list;
    }

    /// <summary>Прыжок к шагу истории (0 — исходное состояние): пакет Undo/Redo с одним обновлением вьюпорта.</summary>
    public int JumpToHistory(int index)
    {
        int applied = Scene.JumpToHistory(index);
        if (applied != 0)
        {
            AfterManualEdit();
            RefreshVersions();
        }
        return applied;
    }

    /// <summary>Контрольные точки (именованные варианты проекта) сессии.</summary>
    public ObservableCollection<ProjectCheckpoint> Checkpoints { get; } = [];

    private GateCostRates CostRates => new(Settings.Current.ConcretePricePerM3, Settings.Current.RebarPricePerTon);

    /// <summary>Показатели текущей сцены для сравнения с вариантами: стоимость каркаса, K_исп max, бетон, число элементов.</summary>
    public (double Cost, double K, double Concrete, int Elements) CurrentVariantMetrics()
    {
        var elements = Scene.Elements.ToList();
        var (cost, k, _, concrete, _) = ProjectCheckpointService.Metrics(elements, Scene.BuildingGroups.ToList(), Settings.Current.StructuralLoads, CostRates);
        return (cost, k, concrete, elements.Count(e => !ElementTypes.IsVoidType(e.Category)));
    }

    /// <summary>«Создать контрольную точку»: именованный слепок варианта с показателями.</summary>
    public ProjectCheckpoint CreateCheckpoint(string? name)
    {
        string title = string.IsNullOrWhiteSpace(name) ? Loc.Format("CheckpointDefaultName", Checkpoints.Count + 1) : name.Trim();
        var cp = ProjectCheckpointService.Capture(Scene, title, Settings.Current.StructuralLoads, CostRates);
        Checkpoints.Add(cp);
        ShowSuccess(Loc.Get("HistoryTitle"), Loc.Format("CheckpointCreated", cp.Name, Rub(cp.FrameCostRub), VerificationText.K(cp.MaxUtilization)), "AIA-CHECKPOINT");
        return cp;
    }

    /// <summary>«Перейти к варианту»: сцена варианта — одним шагом журнала (Undo возвращает).</summary>
    public void RestoreCheckpoint(ProjectCheckpoint cp)
    {
        ProjectCheckpointService.Restore(Scene, cp);
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("CheckpointRestored", cp.Name, Rub(cp.FrameCostRub), VerificationText.K(cp.MaxUtilization))));
        AfterManualEdit();
        RefreshVersions();
        ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveCheckpoint(ProjectCheckpoint cp) => Checkpoints.Remove(cp);

    private string Rub(double value) => value.ToString("N0", Loc.Culture) + " ₽";

#if DEBUG
    /// <summary>
    /// Только Debug-съёмка (AIA_DEBUG_TIMELINE=demo): вариант 1 текущей сцены, ручные операции (колонна из библиотеки, тип 500 × 500
    /// колоннам этажа, дублирование, разделение стены или балки, примыкание к уровню), вариант 2 и один отменённый шаг.
    /// </summary>
    public void DebugHistoryDemo()
    {
        CreateCheckpoint(Loc.IsRussian ? "Вариант 1 · сечения 400 × 400" : "Option 1 · 400 × 400 sections");
        var columns = Scene.Elements.Where(e => e.Category == BimCategory.Column && e.LevelIndex == 0).Take(12).ToList();
        if (columns.Count > 0)
        {
            Scene.SetSelection(columns, columns[0]);
            ApplyLibraryEntry(StandardLibraryService.BuiltIn.Find("col-500x500")!);
        }
        Scene.SelectedElement = null;
        AddManualElement(ManualElementKind.Column);
        if (Scene.SelectedElement is { } added) DuplicateWithOffset("axis");
        if (Scene.Elements.FirstOrDefault(ManualModelingService.CanSplit) is { } linear)
        {
            Scene.SelectedElement = linear;
            SplitSelected();
        }
        if (Scene.Elements.FirstOrDefault(e => e.Category == BimCategory.Beam) is { } beam)
        {
            Scene.SelectedElement = beam;
            ApplyLibraryEntry(StandardLibraryService.BuiltIn.Find("beam-400x600")!);
        }
        var slabs = Scene.Elements.Where(e => e.Category == BimCategory.Slab).ToList();
        if (slabs.Count > 0)
        {
            Scene.SetSelection(slabs, slabs[0]);
            ApplyLibraryEntry(StandardLibraryService.BuiltIn.Find("slab-180")!);
        }
        CreateCheckpoint(Loc.IsRussian ? "Вариант 2 · колонны 500 × 500, плиты 180" : "Option 2 · 500 × 500 columns, 180 slabs");
        AddManualElement(ManualElementKind.Slab);
        Scene.Undo();
        Scene.SelectedElement = null;
        RefreshVersions();
    }
#endif
}
