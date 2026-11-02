using System.Collections.ObjectModel;
using System.ComponentModel;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Визуальный контекст запроса и навигация по версиям: «Авто-снимок вида» (чистый кадр вьюпорта при каждой отправке),
/// «Прикрепить план / эскиз», миниатюры вложений у поля ввода (удалить, обновить кадр), «Назад / Вперёд по версиям»
/// (шаги журнала транзакций) и реконструкция здания по поэтажному плану (<see cref="PlanToBimPipelineService"/>).
/// </summary>
public sealed partial class MainViewModel
{
    private bool _syncingAttachments;

    public ObservableCollection<RequestAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Чистый кадр вьюпорта BGRA8 (длинная сторона — аргумент), задаёт MainWindow; null — вьюпорт не готов.</summary>
    public Func<int, Task<(byte[] Bgra, int Width, int Height)?>>? ViewportSnapshotProvider { get; set; }

    /// <summary>«Прикрепить план / эскиз»: окно выбирает файл и вызывает <see cref="AddAttachmentFileAsync"/>.</summary>
    public event EventHandler? AttachFileRequested;

    [ObservableProperty]
    public partial bool IsAutoSnapshot { get; set; } = true;

    partial void OnIsAutoSnapshotChanged(bool value)
    {
        if (_syncingAttachments) return;
        if (Settings.Current.AutoViewSnapshot != value) SaveSettingsSafe(s => s.AutoViewSnapshot = value);
        if (!value)
            foreach (var a in Attachments.Where(a => a.IsAuto).ToList()) Attachments.Remove(a);
        else _ = RefreshSnapshotAsync();
    }

    private void InitializeAttachments()
    {
        _syncingAttachments = true;
        IsAutoSnapshot = Settings.Current.AutoViewSnapshot;
        IsVerifyExternal = Settings.Current.VerifyExternalDesigns;
        _syncingAttachments = false;
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachmentsHint));
        };
        Scene.PropertyChanged += OnVersionScenePropertyChanged;
        RefreshVersions();
    }

    public string AttachmentsHint => !SelectedModelAcceptsImages && Attachments.Count > 0 ? Loc.Get("AttachNoVision") : string.Empty;

    /// <summary>Модель принимает изображения: облачная с зрением, Ollama с vision или веб-аккаунт.</summary>
    public bool SelectedModelAcceptsImages => SelectedModel.IsWeb || SelectedModel.SupportsVision;

    [RelayCommand]
    private void AttachFile() => AttachFileRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Прикрепить план или эскиз из файла (PNG/JPG; не больше <see cref="RequestAttachmentService.MaxAttachments"/>).</summary>
    public async Task AddAttachmentFileAsync(string path, AttachmentKind kind = AttachmentKind.PlanImage)
    {
        try
        {
            var a = await RequestAttachmentService.FromFileAsync(path, kind);
            while (Attachments.Count(x => !x.IsAuto) >= RequestAttachmentService.MaxAttachments - 1) Attachments.Remove(Attachments.First(x => !x.IsAuto));
            Attachments.Add(a);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            ShowError(Loc.Get("ErrorVision"), Loc.Format("ErrorImageRead", ex.Message), $"AIA-ATTACH-{ex.HResult:X8}", ex.ToString());
        }
    }

    public void AddAttachment(RequestAttachment attachment)
    {
        if (attachment.IsAuto)
            foreach (var old in Attachments.Where(x => x.IsAuto).ToList()) Attachments.Remove(old);
        Attachments.Insert(attachment.IsAuto ? 0 : Attachments.Count, attachment);
    }

    [RelayCommand]
    private void RemoveAttachment(RequestAttachment? attachment)
    {
        if (attachment is null) return;
        Attachments.Remove(attachment);
        // Удалённый авто-снимок — выключение авто-режима до следующего включения.
        if (attachment.IsAuto && IsAutoSnapshot) IsAutoSnapshot = false;
    }

    /// <summary>Обновить кадр авто-снимка (кнопка на миниатюре и перед каждой отправкой).</summary>
    [RelayCommand]
    private async Task RefreshSnapshotAsync()
    {
        if (await CaptureViewSnapshotAsync() is { } snapshot) AddAttachment(snapshot);
    }

    public async Task<RequestAttachment?> CaptureViewSnapshotAsync(AttachmentKind kind = AttachmentKind.ViewportSnapshot)
    {
        if (ViewportSnapshotProvider is null || Scene.Elements.Count == 0) return null;
        try
        {
            if (await ViewportSnapshotProvider(RequestAttachmentService.SnapshotLongSide) is not { } frame) return null;
            string title = Loc.Format(kind == AttachmentKind.NodeSnapshot ? "AttachNodeTitle" : "AttachViewTitle", ViewModeTitle);
            return await RequestAttachmentService.FromPixelsAsync(frame.Bgra, frame.Width, frame.Height, kind, title, isAuto: kind == AttachmentKind.ViewportSnapshot);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            App.WriteCrashLog("view-snapshot", ex);
            return null;
        }
    }

    /// <summary>
    /// Вложения для отправки: свежий авто-снимок (если включён) и прикреплённые файлы. Модели без зрения изображения не
    /// получают — в чат уходит пометка.
    /// </summary>
    private async Task<IReadOnlyList<RequestAttachment>> PrepareAttachmentsAsync()
    {
        if (IsAutoSnapshot && SelectedModelAcceptsImages && await CaptureViewSnapshotAsync() is { } fresh) AddAttachment(fresh);
        if (Attachments.Count == 0) return [];
        if (!SelectedModelAcceptsImages)
        {
            ChatMessages.Add(ChatMessageModel.System(Loc.Format("AttachSkipped", Attachments.Count, ModelLabel(SelectedModel, shortForm: true))));
            return [];
        }
        return Attachments.ToList();
    }

    /// <summary>После отправки прикреплённые файлы снимаются, авто-снимок остаётся превью (обновится при следующей отправке).</summary>
    private void ClearSentAttachments()
    {
        foreach (var a in Attachments.Where(a => !a.IsAuto).ToList()) Attachments.Remove(a);
    }

    // ───────────────────────────── Версии сцены (журнал транзакций) ─────────────────────────────

    [ObservableProperty] public partial string VersionLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string VersionDetail { get; set; } = string.Empty;

    private void OnVersionScenePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SceneStateService.CanUndo) or nameof(SceneStateService.CanRedo)) RefreshVersions();
    }

    private void RefreshVersions()
    {
        var journal = Scene.Transactions;
        int current = journal.UndoCount, total = journal.UndoCount + journal.RedoCount;
        VersionLabel = Loc.Format("VersionLabel", current + 1, total + 1);
        VersionDetail = current == 0 ? Loc.Get("VersionInitial") : journal.UndoStack[^1].Name;
        VersionBackCommand.NotifyCanExecuteChanged();
        VersionForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void VersionBack() => Scene.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void VersionForward() => Scene.Redo();

    // ───────────────────────────── Реконструкция по поэтажному плану ─────────────────────────────

    /// <summary>«Реконструкция по плану (инженерный конвейер)»: окно выбирает файл и показывает проверку распознанного плана.</summary>
    public event EventHandler? PlanReconstructionRequested;

    [RelayCommand]
    private void ReconstructFromPlan() => PlanReconstructionRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Сборка здания по плану (без изменения сцены) с текущими нагрузками проекта.</summary>
    public PlanToBimResult BuildFromPlan(RecognizedPlan plan, PlanToBimOptions options) =>
        PlanToBimPipelineService.Run(plan, options, Settings.Current.StructuralLoads);

    /// <summary>Вставка собранного здания: новая сцена или «+ К участку» — одним шагом Undo, план — во вложения запроса.</summary>
    public void InsertPlanResult(PlanToBimResult result)
    {
        if (result.Elements.Count == 0) return;
        if (IsAppendToSite && Scene.Elements.Count > 0)
            Scene.AppendBuildings(result.Groups, result.Elements.Select(e => e.Clone()).ToList(), null, result.Axes);
        else
        {
            Scene.LoadScene(result.Elements.Select(e => e.Clone()).ToList(), result.Groups.Select(g => g.Clone()).ToList(), recordUndo: Scene.Elements.Count > 0,
                axes: result.Axes.Select(a => a.Clone()).ToList(), dimensions: []);
            Scene.ProjectName = Path.GetFileNameWithoutExtension(result.Plan.Name);
        }
        var report = new SolverReport
        {
            Success = true, ElapsedMilliseconds = result.ElapsedMs, InputElements = result.Plan.Walls.Count, GeneratedElements = result.Elements.Count,
            Actions = [.. result.Log]
        };
        if (result.PhysicsErrors > 0) report.Errors.Add(Loc.Format("PlanPhysicsErrors", result.PhysicsErrors));
        var message = ChatMessageModel.Assistant(Loc.Format("PlanInserted", result.Plan.Name, PlanToBimPipelineService.Summary(result)), Loc.Get("PlanEngine"), report);
        message.Badge = result.IsStructurallySound ? Loc.Format("ReviewBadgeOk", Services.Verification.VerificationText.K(result.MaxUtilization), 0) : Loc.Get("PlanBadgeIssues");
        message.BadgeColorHex = result.IsStructurallySound ? "#30D158" : "#FF9F0A";
        if (result.Plan.Image is { Length: > 0 } image) message.Thumbnails = [image];
        ChatMessages.Add(message);
        VerificationBadgeText = message.Badge;
        VerificationBadgeColorHex = message.BadgeColorHex;
        if (result.Plan.Image is { Length: > 0 } plan)
            AddAttachment(new RequestAttachment
            {
                Id = "att_plan", Kind = AttachmentKind.PlanImage, Bytes = plan, MimeType = result.Plan.ImageMime ?? "image/jpeg", Title = result.Plan.Name,
                Width = result.Plan.PixelWidth, Height = result.Plan.PixelHeight
            });
        ShowSolverReport(report);
        ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
    }

#if DEBUG
    /// <summary>
    /// Только Debug-съёмка (AIA_DEBUG_ATTACH): три шага журнала (ответ генератора, правки аудитора, ручная правка) и шаг назад,
    /// поэтажный план и авто-снимок во вложениях, проверка достоверности текущей сцены — индикатор в доке и в сообщении.
    /// </summary>
    public async Task DebugAttachmentsAsync(byte[] planPng)
    {
        if (Scene.Elements.FirstOrDefault(e => e.Category is BimCategory.Slab or BimCategory.Roof) is { } slab)
        {
            foreach (string step in new[] { "Ответ внешнего генератора · Gemini", "Правки локального аудитора", "Ручная правка" })
                using (Scene.BeginUndoTransaction(step))
                    Services.Verification.DesignFixEngine.Apply(Scene, [new Services.Verification.FixAction(Services.Verification.FixActionKind.ThickenSlab, slab.Id) { Value = slab.Height + 0.01 }]);
            Scene.Undo();
        }
        var (w, h) = await RequestAttachmentService.SizeAsync(planPng);
        AddAttachment(new RequestAttachment { Id = "att_plan", Kind = AttachmentKind.PlanImage, Bytes = planPng, MimeType = "image/png", Title = "План 1 этажа.png", Width = w, Height = h });
        await RefreshSnapshotAsync();
        var sandbox = Services.Verification.DesignFixEngine.Sandbox(Scene);
        var loads = Settings.Current.StructuralLoads.Clone();
        var gate = await Task.Run(() => Services.Verification.VerificationGateService.Inspect(sandbox, loads, "Gemini · gemini-2.5-pro"));
        var user = ChatMessageModel.User(Loc.IsRussian ? "Добавь террасу над гаражом и проверь опирание балок" : "Add a terrace above the garage and check the beam supports");
        user.Thumbnails = Attachments.Select(a => a.Bytes).ToList();
        ChatMessages.Add(user);
        var answer = ChatMessageModel.Assistant(Loc.IsRussian ? "Терраса 6 × 4 м добавлена на отметке +3,000, балки опёрты на колонны осей 2 и 3." : "A 6 × 4 m terrace is added at +3.000, beams bear on the columns of axes 2 and 3.",
            "Gemini · gemini-2.5-pro");
        ChatMessages.Add(answer);
        _reviewMessage = answer;
        SetBadge(gate, afterFixes: false);
        IsHistoryOpen = true;
        ChatInput = Loc.IsRussian ? "Проверь узел опирания балки на колонну 2/Б" : "Check the beam bearing on column 2/B";
    }
#endif
}
