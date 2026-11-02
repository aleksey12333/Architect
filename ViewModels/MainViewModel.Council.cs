using System.Collections.ObjectModel;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Verification;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace AIArchitectStudio.ViewModels;

/// <summary>Пункт списка моделей роли: «Автоподбор» или конкретная модель (локальная, облачная, веб-аккаунт).</summary>
public sealed record CouncilModelChoice(string Id, string Title, string Glyph);

/// <summary>Роль в панели «Состав Конструкторского Бюро»: назначенная модель и итог маршрутизации.</summary>
public sealed partial class CouncilRoleSlot : ObservableObject
{
    private readonly Action<CouncilRoleSlot> _changed;
    internal bool Syncing;

    public CouncilRoleSlot(AgentRoleInfo info, bool russian, Action<CouncilRoleSlot> changed)
    {
        Info = info;
        _changed = changed;
        Title = info.Title(russian);
        Description = info.Description(russian);
    }

    public AgentRoleInfo Info { get; }
    public AiAgentRole Role => Info.Role;
    public string Glyph => Info.Glyph;
    public string ColorHex => Info.ColorHex;

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string Description { get; set; }
    [ObservableProperty] public partial string ResolvedText { get; set; } = string.Empty;

    public ObservableCollection<CouncilModelChoice> Choices { get; } = [];

    [ObservableProperty]
    public partial CouncilModelChoice? SelectedChoice { get; set; }

    partial void OnSelectedChoiceChanged(CouncilModelChoice? value)
    {
        if (!Syncing && value is not null) _changed(this);
    }
}

/// <summary>Спорный узел в карточке согласования.</summary>
public sealed record ConsultationIssueRow(VerificationFinding Finding, string Title, string Explanation, string Category, bool Critical)
{
    public string Glyph => Critical ? "" : "";
}

/// <summary>Кнопка варианта решения в карточке согласования.</summary>
public sealed record ConsultationVariantRow(CouncilVariant Variant, string Badge, string Author, string Title, string Description, string Metrics, string ColorHex, bool Recommended)
{
    public string Id => Variant.Id;
}

/// <summary>
/// Совет КБ в ViewModel: переключатель «1 Агент | Совет КБ», состав бюро (модели ролей, автоподбор, согласование или
/// автопилот), лента «Ход мысли Совета», «Аудит текущей сцены» и карточка «Согласование решения с главным инженером».
/// </summary>
public sealed partial class MainViewModel
{
    // Временный состав бюро Debug-съёмки (AIA_DEBUG_COUNCILDEMO); в Release всегда null — явная инициализация без CS0649.
    private CouncilSettings? _councilOverride = null;
    private Func<IReadOnlyList<AiModelOption>>? _councilModelsOverride = null;
    private bool _syncingCouncil;
    private readonly Dictionary<string, CouncilModelChoice> _choiceCache = [];
    private CancellationTokenSource? _commentCts;

    public AgentSwarmOrchestrator Council { get; private set; } = null!;

    public ObservableCollection<AgentDebateEntry> CouncilTimeline { get; } = [];
    public ObservableCollection<CouncilRoleSlot> CouncilSlots { get; } = [];
    public ObservableCollection<ConsultationIssueRow> ConsultationIssues { get; } = [];
    public ObservableCollection<ConsultationVariantRow> ConsultationVariants { get; } = [];

    /// <summary>Элементы спорных узлов для подсветки во вьюпорте (Id, критично).</summary>
    public IReadOnlyList<(string Id, bool Critical)> CouncilHighlights { get; private set; } = [];

    /// <summary>Текущие настройки Совета (в Debug-съёмке — временные, без записи в settings.json).</summary>
    private CouncilSettings CouncilSettingsCurrent => _councilOverride ?? Settings.Current.Council;

    private void InitializeCouncil(AIOrchestrator ai, AgentModelGateway gateway)
    {
        Council = new AgentSwarmOrchestrator(Scene, ai, gateway, () => CouncilSettingsCurrent, () => Settings.Current.StructuralLoads,
            AvailableCouncilModels, () => SolarLocations.Resolve(Settings.Current.SolarRegionId));
        gateway.WebSessionRequested += (_, kind) => RunOnUi(() =>
        {
            WebBridgeSite = kind;
            IsWebBridgeOpen = true;
        });
        _syncingCouncil = true;
        IsCouncilMode = Settings.Current.Council.Enabled;
        IsCouncilConsult = Settings.Current.Council.ConsultUser;
        IsCouncilAiComments = Settings.Current.Council.AiCommentaryInAudit;
        _syncingCouncil = false;
        foreach (var info in AgentRoles.All) CouncilSlots.Add(new CouncilRoleSlot(info, Loc.IsRussian, OnCouncilSlotChanged));
        CouncilTimeline.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CouncilTimelineLabel));
            OnPropertyChanged(nameof(HasCouncilTimeline));
        };
        ModelRegistry.ModelsChanged += (_, _) => RunOnUi(RefreshCouncilSlots);
        RefreshCouncilSlots();
    }

    /// <summary>Модели, доступные Совету: облачные (проверенные ключи), локальные .gguf (файл на месте), веб-аккаунты.</summary>
    private IReadOnlyList<AiModelOption> AvailableCouncilModels()
    {
        if (_councilModelsOverride is { } demo) return demo();
        string? dir = null;
        try
        {
            dir = Settings.ResolveLocalModelsDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Папка моделей недоступна — локальные роли не назначаются.
        }
        return ModelRegistry.All.Where(m => !m.IsLocal || dir is not null && File.Exists(Path.Combine(dir, m.ModelId))).ToList();
    }

    // ───────────────────────────── Режим дока ─────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingleAgentMode), nameof(ChatPlaceholder))]
    public partial bool IsCouncilMode { get; set; }

    public bool IsSingleAgentMode
    {
        get => !IsCouncilMode;
        set { if (value) IsCouncilMode = false; }
    }

    partial void OnIsCouncilModeChanged(bool value)
    {
        if (!_syncingCouncil && _councilOverride is null) SaveSettingsSafe(s => s.Council.Enabled = value);
        if (value) RefreshCouncilSlots();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCouncilAutopilot))]
    public partial bool IsCouncilConsult { get; set; } = true;

    public bool IsCouncilAutopilot
    {
        get => !IsCouncilConsult;
        set { if (value) IsCouncilConsult = false; }
    }

    partial void OnIsCouncilConsultChanged(bool value)
    {
        if (_syncingCouncil) return;
        if (_councilOverride is not null) _councilOverride.ConsultUser = value;
        else SaveSettingsSafe(s => s.Council.ConsultUser = value);
    }

    [ObservableProperty]
    public partial bool IsCouncilAiComments { get; set; } = true;

    partial void OnIsCouncilAiCommentsChanged(bool value)
    {
        if (_syncingCouncil) return;
        if (_councilOverride is not null) _councilOverride.AiCommentaryInAudit = value;
        else SaveSettingsSafe(s => s.Council.AiCommentaryInAudit = value);
    }

    [ObservableProperty]
    public partial bool IsCouncilTimelineOpen { get; set; }

    [RelayCommand]
    private void ToggleCouncilTimeline() => IsCouncilTimelineOpen = !IsCouncilTimelineOpen;

    [RelayCommand]
    private void ClearCouncilTimeline() => CouncilTimeline.Clear();

    public bool HasCouncilTimeline => CouncilTimeline.Count > 0;

    public string CouncilTimelineLabel => CouncilTimeline.Count == 0 ? Loc.Get("CouncilTimelineShort") : $"{Loc.Get("CouncilTimelineShort")} · {CouncilTimeline.Count}";

    /// <summary>Итог маршрутизации для панели состава: мульти-ИИ или один провайдер последовательно.</summary>
    [ObservableProperty] public partial string CouncilRouteText { get; set; } = string.Empty;

    // ───────────────────────────── Состав КБ ─────────────────────────────

    /// <summary>Пересобирает списки моделей ролей (инкрементально — без Clear(), чтобы TwoWay-привязка ComboBox не сбрасывала выбор).</summary>
    public void RefreshCouncilSlots()
    {
        var available = AvailableCouncilModels();
        var desired = new List<CouncilModelChoice> { Choice(CouncilSettings.AutoModelId, Loc.Get("CouncilAutoModel"), "") };
        desired.AddRange(available.Select(m => Choice(m.Id, AgentSwarmOrchestrator.ModelLabel(m), m.IsWeb ? "" : AiModelCatalog.ProviderGlyph(m.Provider))));
        var settings = CouncilSettingsCurrent;
        foreach (var slot in CouncilSlots)
        {
            slot.Syncing = true;
            try
            {
                string bound = settings.BindingFor(slot.Role);
                var list = desired.ToList();
                // Назначенная, но сейчас недоступная модель остаётся в списке (помечена) — выбор пользователя не теряется.
                if (bound != CouncilSettings.AutoModelId && list.All(c => c.Id != bound))
                    list.Add(Choice(bound, $"{AiModelCatalog.FromId(bound)?.DisplayName ?? bound} · {Loc.Get("CouncilUnavailable")}", ""));
                foreach (var c in slot.Choices.Where(c => !list.Contains(c)).ToList()) slot.Choices.Remove(c);
                for (int i = 0; i < list.Count; i++)
                {
                    if (i < slot.Choices.Count && ReferenceEquals(slot.Choices[i], list[i])) continue;
                    if (slot.Choices.Contains(list[i])) slot.Choices.Remove(list[i]);
                    slot.Choices.Insert(Math.Min(i, slot.Choices.Count), list[i]);
                }
                slot.SelectedChoice = slot.Choices.FirstOrDefault(c => c.Id == bound) ?? slot.Choices.FirstOrDefault();
                slot.Title = slot.Info.Title(Loc.IsRussian);
                slot.Description = slot.Info.Description(Loc.IsRussian);
            }
            finally
            {
                slot.Syncing = false;
            }
        }
        RefreshCouncilRoutes();
    }

    private CouncilModelChoice Choice(string id, string title, string glyph)
    {
        if (_choiceCache.TryGetValue(id, out var c) && c.Title == title && c.Glyph == glyph) return c;
        return _choiceCache[id] = new CouncilModelChoice(id, title, glyph);
    }

    private void OnCouncilSlotChanged(CouncilRoleSlot slot)
    {
        if (slot.SelectedChoice is not { } choice) return;
        string key = slot.Info.Key;
        if (_councilOverride is not null) _councilOverride.Bindings[key] = choice.Id;
        else SaveSettingsSafe(s => s.Council.Bindings[key] = choice.Id);
        RefreshCouncilRoutes();
    }

    private void RefreshCouncilRoutes()
    {
        var routes = Council.ResolveAssignments(SelectedModel);
        foreach (var slot in CouncilSlots)
            if (routes.FirstOrDefault(r => r.Role == slot.Role) is { } a)
            {
                string t = "t = " + a.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                slot.ResolvedText = a.IsFallback ? $"{Loc.Get("CouncilFallback")} → {AgentSwarmOrchestrator.ModelLabel(a.Model)} · {t}"
                    : a.IsAuto ? $"{Loc.Get("CouncilAutoShort")} → {AgentSwarmOrchestrator.ModelLabel(a.Model)} · {t}"
                    : $"{Loc.Get("CouncilBound")} · {Loc.Get("CouncilTemperature")} {t}";
            }
        int models = routes.Select(r => r.Model.Id).Distinct().Count();
        int providers = routes.Select(r => AgentModelRouter.ProviderKey(r.Model)).Distinct().Count();
        CouncilRouteText = AgentModelRouter.IsSingleProvider(routes)
            ? Loc.Get("CouncilRouteSingle")
            : Loc.Format("CouncilRouteMulti", providers, models);
    }

    /// <summary>«Автораспределение лучших моделей»: все роли — автоподбор по профилю.</summary>
    [RelayCommand]
    private void AutoAssignCouncil()
    {
        if (_councilOverride is not null) _councilOverride.Bindings.Clear();
        else SaveSettingsSafe(s => s.Council.Bindings.Clear());
        RefreshCouncilSlots();
    }

    // ───────────────────────────── Лента и подсветка ─────────────────────────────

    private void PostCouncilEntry(AgentDebateEntry entry) => RunOnUi(() =>
    {
        CouncilTimeline.Add(entry);
        while (CouncilTimeline.Count > 120) CouncilTimeline.RemoveAt(0);
    });

    private void SetCouncilHighlights(IReadOnlyList<(string Id, bool Critical)> ids)
    {
        CouncilHighlights = ids;
        ClashHighlightChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── Проектирование Советом ─────────────────────────────

    private async Task RunCouncilDesignAsync(AiRequestContext context)
    {
        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        IsBusy = true;
        BusyText = Loc.Get("CouncilBusyDesign");
        IsCouncilTimelineOpen = true;
        CloseConsultation();
        try
        {
            var run = await Council.RunDesignAsync(context, IsCouncilConsult, PostCouncilEntry, _aiCts.Token);
            if (run.Draft is { } draft)
            {
                ChatMessages.Add(ChatMessageModel.Assistant(draft.Message, draft.Source, draft.Report));
                ShowSolverReport(draft.Report, draft.RawJson);
                if (draft.Mode != SolverMode.Modify) ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
            }
            if (run.AutoApplied is { } applied) OnCouncilApplied(applied, autopilot: true);
            if (run.Consultation is { } consultation) ShowConsultation(consultation);
        }
        catch (OperationCanceledException)
        {
            ChatMessages.Add(ChatMessageModel.System(Loc.Get("AiCancelled")));
        }
        catch (AiProviderException ex)
        {
            ShowError(Loc.Get("ErrorAi"), ex.Message, ex.Code, ex.Details);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            App.WriteCrashLog("council", ex);
            ShowError(Loc.Get("ErrorAi"), ex.Message, $"AIA-COUNCIL-{ex.HResult:X8}", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    // ───────────────────────────── Аудит текущей сцены ─────────────────────────────

    [RelayCommand]
    private async Task RunCouncilAuditAsync()
    {
        if (IsBusy) return;
        if (Scene.Elements.Count == 0)
        {
            ShowNotice(Loc.Get("CouncilTitle"), Loc.Get("CouncilEmptyScene"), "AIA-COUNCIL-EMPTY");
            return;
        }
        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        _commentCts?.Cancel();
        IsBusy = true;
        BusyText = Loc.Get("CouncilBusyAudit");
        IsCouncilTimelineOpen = true;
        CloseConsultation();
        CouncilRunResult? run = null;
        try
        {
            run = await Council.RunAuditAsync(SelectedModel, IsCouncilConsult, PostCouncilEntry, _aiCts.Token);
            ChatMessages.Add(ChatMessageModel.System(run.Message));
            if (run.AutoApplied is { } applied) OnCouncilApplied(applied, autopilot: true);
            if (run.Consultation is { } consultation) ShowConsultation(consultation);
            else if (run.AutoApplied is null) ShowNotice(Loc.Get("CouncilTitle"), run.Message, "AIA-COUNCIL-OK");
        }
        catch (OperationCanceledException)
        {
            ChatMessages.Add(ChatMessageModel.System(Loc.Get("AiCancelled")));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or AiProviderException)
        {
            App.WriteCrashLog("council-audit", ex);
            ShowError(Loc.Get("ErrorAi"), ex.Message, $"AIA-COUNCIL-AUDIT-{ex.HResult:X8}", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
        // ИИ-комментарии Нормоконтроля и Координатора — после карточки, не блокируя работу.
        if (run is not null && IsCouncilAiComments) await CommentCouncilAsync(run);
    }

    private async Task CommentCouncilAsync(CouncilRunResult run)
    {
        _commentCts?.Dispose();
        _commentCts = new CancellationTokenSource();
        try
        {
            await Council.CommentAsync(run, PostCouncilEntry, _commentCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Пользователь начал новый аудит или отменил запрос.
        }
        catch (Exception ex) when (ex is InvalidOperationException or AiProviderException)
        {
            App.WriteCrashLog("council-comment", ex);
        }
    }

    // ───────────────────────────── Карточка согласования ─────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConsultation), nameof(ConsultationTitle), nameof(ConsultationSummary), nameof(ConsultationMeta))]
    public partial CouncilConsultation? Consultation { get; set; }

    public bool HasConsultation => Consultation is not null;
    public string ConsultationTitle => Consultation is null ? string.Empty : Loc.Get("CouncilConsultTitle");
    public string ConsultationSummary => Consultation?.Summary ?? string.Empty;

    public string ConsultationMeta => Consultation is null
        ? string.Empty
        : Loc.Format("CouncilConsultMeta", Consultation.Issues.Count, Consultation.Variants.Count, VerificationText.K(Consultation.Report.MaxUtilization), Consultation.Report.ClashCount);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendConsultationReplyCommand))]
    public partial string ConsultationReply { get; set; } = string.Empty;

    private void ShowConsultation(CouncilConsultation c)
    {
        ConsultationIssues.Clear();
        foreach (var f in c.Issues)
            ConsultationIssues.Add(new ConsultationIssueRow(f, f.Title, f.DetailedExplanation, f.Category, f.IsCritical));
        ConsultationVariants.Clear();
        var auto = c.Variants.FirstOrDefault(v => v.Author == FixAuthor.Automatic);
        foreach (var v in c.Variants)
        {
            string author = v.Author switch
            {
                FixAuthor.Structural => Loc.Get("CouncilAuthorStructural"),
                FixAuthor.Architect => Loc.Get("CouncilAuthorArchitect"),
                _ => Loc.Get("CouncilAuthorAuto")
            };
            string color = v.Author switch
            {
                FixAuthor.Structural => AgentRoles.Info(AiAgentRole.StructuralEngineer).ColorHex,
                FixAuthor.Architect => AgentRoles.Info(AiAgentRole.ChiefArchitect).ColorHex,
                _ => AgentRoles.Info(AiAgentRole.MasterSynthesizer).ColorHex
            };
            string title = v.Author == FixAuthor.Automatic ? Loc.Get("CouncilVariantAuto") : CouncilPlanner.LeadOption(v) + (v.Choices.Count > 1 ? Loc.Format("CouncilMoreFixes", v.Choices.Count - 1) : string.Empty);
            ConsultationVariants.Add(new ConsultationVariantRow(v, v.Number.ToString(Loc.Culture), Loc.Format("CouncilVariantN", v.Number, author), title, v.Description, v.MetricsText, color,
                ReferenceEquals(v, auto) && v.CriticalAfter == 0));
        }
        Consultation = c;
        ConsultationReply = string.Empty;
        SetCouncilHighlights(c.Issues.SelectMany(i => i.AffectedElementIds.Take(2).Select(id => (id, i.IsCritical))).Distinct().ToList());
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("CouncilConsultChat", c.Issues.Count, c.Variants.Count)));
    }

    private void CloseConsultation()
    {
        if (Consultation is null && CouncilHighlights.Count == 0) return;
        Consultation = null;
        ConsultationIssues.Clear();
        ConsultationVariants.Clear();
        SetCouncilHighlights([]);
    }

    /// <summary>Клик по спорному узлу: выделение элемента и плавный фокус камеры.</summary>
    public void FocusCouncilIssue(ConsultationIssueRow row)
    {
        var f = row.Finding;
        if (f.AffectedElementIds.Select(Scene.FindById).OfType<ArchitecturalElement>().FirstOrDefault() is { } e) Scene.SelectedElement = e;
        if (f.Location is { Length: 3 } p) ClashFocusRequested?.Invoke(this, new ClashFocus(p[0], p[1], p[2], f.Extent));
    }

    public async Task ChooseVariantAsync(ConsultationVariantRow row) => await ApplyCouncilDecisionAsync(new CouncilDecision(row.Id, CouncilModifiers.None));

    [RelayCommand]
    private async Task KeepAsIsAsync() => await ApplyCouncilDecisionAsync(new CouncilDecision(CouncilDecision.Keep, CouncilModifiers.None));

    private bool CanSendConsultationReply() => !string.IsNullOrWhiteSpace(ConsultationReply) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSendConsultationReply))]
    private async Task SendConsultationReplyAsync()
    {
        if (Consultation is not { } c) return;
        string text = ConsultationReply.Trim();
        ChatMessages.Add(ChatMessageModel.User($"[{Loc.Get("CouncilConsultShort")}] {text}"));
        IsBusy = true;
        BusyText = Loc.Get("CouncilBusyReply");
        CouncilDecision? decision;
        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        try
        {
            decision = await Council.InterpretReplyAsync(c, text, SelectedModel, _aiCts.Token);
        }
        catch (OperationCanceledException)
        {
            decision = null;
        }
        catch (AiProviderException ex)
        {
            // Модель Координатора недоступна (сеть, ключ, Ollama) — ошибка в InfoBar, карточка остаётся для выбора вручную.
            ShowError(Loc.Get("ErrorAi"), ex.Message, ex.Code, ex.Details);
            return;
        }
        catch (InvalidOperationException ex)
        {
            App.WriteCrashLog("council-reply", ex);
            ShowError(Loc.Get("ErrorAi"), ex.Message, $"AIA-COUNCIL-REPLY-{ex.HResult:X8}", ex.ToString());
            return;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
        if (decision is null)
        {
            ShowNotice(Loc.Get("CouncilConsultShort"), Loc.Get("CouncilNotRecognized"), "AIA-COUNCIL-REPLY");
            return;
        }
        ConsultationReply = string.Empty;
        await ApplyCouncilDecisionAsync(decision);
    }

    private async Task ApplyCouncilDecisionAsync(CouncilDecision decision)
    {
        if (Consultation is not { } c || IsBusy) return;
        var syn = Council.ResolveAssignments(SelectedModel).First(a => a.Role == AiAgentRole.MasterSynthesizer);
        var entry = new AgentDebateEntry(AiAgentRole.MasterSynthesizer, AgentSwarmOrchestrator.ModelLabel(syn.Model) + " · " + Loc.Get("CouncilDecisionLabel"), Loc.IsRussian);
        PostCouncilEntry(entry);
        IsBusy = true;
        BusyText = Loc.Get("CouncilBusyApply");
        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await Council.ApplyDecisionAsync(c, decision, _aiCts.Token);
            CloseConsultation();
            entry.Found = c.Issues.Count;
            entry.Fixed = result.Applied ? c.Variants.FirstOrDefault(v => v.Id == decision.VariantId)?.Choices.Count ?? 0 : 0;
            entry.Complete(result.Applied ? AgentEntryStatus.Done : AgentEntryStatus.Decision,
                (decision.Rationale is { Length: > 0 } why ? why + " " : string.Empty) + result.Summary, sw.Elapsed);
            OnCouncilApplied(result, autopilot: false);
        }
        catch (OperationCanceledException)
        {
            entry.Complete(AgentEntryStatus.Failed, Loc.Get("AiCancelled"), sw.Elapsed);
        }
        catch (InvalidOperationException ex)
        {
            entry.Complete(AgentEntryStatus.Failed, ex.Message, sw.Elapsed);
            ShowError(Loc.Get("CouncilConsultShort"), ex.Message, "AIA-COUNCIL-APPLY", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private void OnCouncilApplied(CouncilApplyResult result, bool autopilot)
    {
        var report = new SolverReport
        {
            Success = true,
            ElapsedMilliseconds = result.ElapsedMilliseconds,
            InputElements = result.ActionsApplied,
            GeneratedElements = result.ElementsAdded,
            Actions = [.. result.Log]
        };
        ChatMessages.Add(ChatMessageModel.Assistant((autopilot ? Loc.Get("CouncilAutopilotPrefix") : Loc.Get("CouncilDecisionPrefix")) + result.Summary, Loc.Get("CouncilTitle"), report));
        RunClashDetection();
        if (!result.Applied) return;
        _noticeVersion++;
        ErrorSeverity = result.KAfter <= 1.0 && result.ClashesAfter == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ErrorTitle = Loc.Get("CouncilApplied");
        ErrorMessage = Loc.Format("CouncilAppliedMessage", VerificationText.K(result.KBefore), VerificationText.K(result.KAfter), result.ClashesBefore, result.ClashesAfter);
        ErrorCode = "AIA-COUNCIL-OK";
        ErrorDetails = string.Join("\n", result.Log);
        ErrorActionLabel = Loc.Get("SolverLog");
        UpdateErrorReport();
        IsErrorOpen = true;
        if (ErrorSeverity == InfoBarSeverity.Success) AutoCloseNotice();
    }

#if DEBUG
    /// <summary>Только Debug-съёмка: временный состав бюро (без записи в settings.json) и сценарный шлюз без сети.</summary>
    public void DebugUseCouncilDemo(IReadOnlyDictionary<string, string> bindings, bool injectModels, bool ollama = false)
    {
        _councilOverride = Settings.Current.Council.Clone();
        _councilOverride.Bindings.Clear();
        foreach (var (role, id) in bindings) _councilOverride.Bindings[role] = id;
        if (injectModels)
        {
            // Модели для съёмки живут только в памяти Совета: фоновая перепроверка ключей реестр не затрагивает.
            List<AiModelOption> demo = ollama
                ? [.. CadSelfTestService.OllamaTestModels(), AiModelCatalog.Cloud(AiProviderKind.Anthropic, "claude-opus-5-5"), AiModelCatalog.Cloud(AiProviderKind.Gemini, "gemini-2.5-pro")]
                :
                [
                    AiModelCatalog.LocalQwen3B, AiModelCatalog.LocalQwen7B,
                    AiModelCatalog.Cloud(AiProviderKind.Gemini, "gemini-2.5-pro"), AiModelCatalog.Cloud(AiProviderKind.Gemini, "gemini-2.5-flash"),
                    AiModelCatalog.Cloud(AiProviderKind.OpenAI, "gpt-4o"), AiModelCatalog.Cloud(AiProviderKind.OpenAI, "o3"),
                    AiModelCatalog.Cloud(AiProviderKind.Anthropic, "claude-opus-5-5"), AiModelCatalog.Cloud(AiProviderKind.Anthropic, "claude-sonnet-5-5"),
                    .. AiModelCatalog.WebOptions
                ];
            _councilModelsOverride = () => demo;
        }
        var ai = App.Services.GetService(typeof(AIOrchestrator)) as AIOrchestrator;
        IAgentModelGateway gateway = ollama ? new CouncilScenarioGateway { FlawedFirstDraft = true, StreamDelayMs = 35 } : new DemoCouncilGateway();
        Council = new AgentSwarmOrchestrator(Scene, ai!, gateway, () => CouncilSettingsCurrent, () => Settings.Current.StructuralLoads,
            AvailableCouncilModels, () => SolarLocations.Resolve(Settings.Current.SolarRegionId));
        RefreshCouncilSlots();
    }

    /// <summary>Сценарные реплики ролей для Debug-съёмки ленты (без обращения к сети и локальным весам).</summary>
    private sealed class DemoCouncilGateway : IAgentModelGateway
    {
        public async Task<AiResponse> InvokeAsync(AgentInvocation invocation, CancellationToken cancellationToken)
        {
            await Task.Delay(invocation.Assignment.Role == AiAgentRole.NormsAndSafetyVerifier ? 900 : 650, cancellationToken);
            string text = invocation.Assignment.Role switch
            {
                AiAgentRole.NormsAndSafetyVerifier => "{\"message\": \"Главный риск — плита 200 мм на пролёте 10.5 м: прогиб в 2 раза выше предельного и K_исп 3.1. Колонну на консоли нужно опереть до фундамента, над маршем вырезать проём 1.4 × 3.2 м, окно ОК-1 увести с колонны 2/В.\"}",
                _ => "{\"message\": \"Вариант КБ закрывает все узлы при наименьшем вмешательстве в план; если важен свободный план — вариант Конструктора с балочной клеткой.\", \"variant\": \"v3\"}"
            };
            return new AiResponse(text, invocation.Assignment.Model.DisplayName, TimeSpan.FromMilliseconds(800));
        }
    }
#endif
}
