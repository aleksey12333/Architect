using System.Globalization;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Gpzu;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>Режим окна подтверждения ограничений: «НО» документа, найденные в сети или уже действующие на участке.</summary>
public enum ConstraintDialogMode { Document, Web, Review }

/// <summary>Решение при выходе из программы.</summary>
public enum ExitDecision { Save, SaveCopy, Discard, Cancel }

/// <summary>
/// Волна 6 в модели представления: три Среды девелопера над одним проектом, живой участок (правка поворотных точек и вершин зон,
/// отметки рельефа, несколько строений на участке, пересчёт показателей на лету — каждая правка один шаг журнала), глубокое чтение
/// «НО» документа и поиск ограничений в сети с окном подтверждения, автосохранение сессии и защита выхода, каркас Среды 3.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly CultureInfo W6Ru = CultureInfo.GetCultureInfo("ru-RU");
    private bool _siteMetricsQueued;
    private readonly List<ProjectConstraintItem> _deferredConstraints = [];

    /// <summary>Автосохранение сессии (%LocalAppData%\AIArchitectStudio\Autosave).</summary>
    public AutoSaveService? AutoSave { get; private set; }

    /// <summary>Запущено автоматизацией Debug (AIA_DEBUG_*): автосохранение пишет только явным вызовом.</summary>
    public static bool IsDebugAutomation => Environment.GetEnvironmentVariables().Keys.OfType<string>()
        .Any(k => k.StartsWith("AIA_DEBUG_", StringComparison.OrdinalIgnoreCase) && !k.Equals("AIA_DEBUG_AUTOSAVE", StringComparison.OrdinalIgnoreCase))
        && Environment.GetEnvironmentVariable("AIA_DEBUG_AUTOSAVE") != "1";

    public event EventHandler? EnvironmentChanged;
    public event EventHandler? SiteMetricsChanged;
    public event EventHandler<ConstraintDialogMode>? ConstraintDialogRequested;

    private void InitializeWave6()
    {
        if (WorkspaceEnvironmentService.Parse(Settings.Current.DeveloperEnvironment, DeveloperEnvironment.Building) is var env && env != ActiveEnvironment)
            ActiveEnvironment = env;
        Scene.SitePlanChanged += (_, _) => RunOnUi(() =>
        {
            OnPropertyChanged(nameof(HasSitePlan));
            OnPropertyChanged(nameof(IsSiteEditing));
            if (SelectedSiteVertex is { } v && (Scene.SitePlan is null || EditableSiteTerrainService.PointOf(Scene.SitePlan, v) is null)) SelectedSiteVertex = null;
            QueueSiteMetrics();
        });
        Scene.DocumentChanged += (_, _) => { if (Scene.SitePlan is not null) QueueSiteMetrics(); };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsStructuralMode) or nameof(IsWalkthrough) or nameof(IsSectionPanelVisible) or nameof(IsEnvironmentPanelOpen))
                OnPropertyChanged(nameof(IsEnvironmentPanelVisible));
        };
        AutoSave = new AutoSaveService(Scene, () => (new ProjectSnapshot(ViewMode.ToString(), Scene.ActiveLevelFilter, CaptureProjectParameters(), ChatMessages.ToList(), EnvironmentForFile),
            EnvironmentForFile), RunOnUi);
        AutoSave.Saved += (_, info) => RunOnUi(() => AutoSaveStatusText = Loc.Format("W6AutoSaved", info.SavedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        AutoSave.Enabled = !IsDebugAutomation;
    }

    // ═════════════════════════════ Среды девелопера ═════════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnvSite), nameof(IsEnvBuilding), nameof(IsEnvInteriors), nameof(IsSiteEditing), nameof(EnvironmentTitle), nameof(EnvironmentGlyph))]
    public partial DeveloperEnvironment ActiveEnvironment { get; set; } = DeveloperEnvironment.Building;

    partial void OnActiveEnvironmentChanged(DeveloperEnvironment value)
    {
        // Среда меняет фокус инструментов, а не проект: документ, журнал и участок — прежние.
        var d = WorkspaceEnvironmentService.Of(value);
        if (!d.StructuralTools && IsStructuralMode) IsStructuralMode = false;
        if (value == DeveloperEnvironment.Site && IsSectionBoxEnabled) IsSectionBoxEnabled = false;
        if (!d.SiteEditing) SelectedSiteVertex = null;
        IsEnvironmentPanelOpen = true;
        // Автотест и сценарии съёмки (AIA_DEBUG_*) настройки пользователя не меняют.
        string? stored = value == DeveloperEnvironment.Building ? null : WorkspaceEnvironmentService.Code(value);
        if (!IsSelfTestRun && !IsDebugAutomation && Settings.Current.DeveloperEnvironment != stored)
            SaveSettingsSafe(s => s.DeveloperEnvironment = stored);
        AutoSave?.MarkChanged();
        if (value == DeveloperEnvironment.Interiors) RefreshInteriors();
        EnvironmentChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsEnvSite { get => ActiveEnvironment == DeveloperEnvironment.Site; set { if (value) ActiveEnvironment = DeveloperEnvironment.Site; } }
    public bool IsEnvBuilding { get => ActiveEnvironment == DeveloperEnvironment.Building; set { if (value) ActiveEnvironment = DeveloperEnvironment.Building; } }
    public bool IsEnvInteriors { get => ActiveEnvironment == DeveloperEnvironment.Interiors; set { if (value) ActiveEnvironment = DeveloperEnvironment.Interiors; } }

    public string EnvironmentTitle => Loc.Get(WorkspaceEnvironmentService.Of(ActiveEnvironment).TitleKey);
    public string EnvironmentGlyph => WorkspaceEnvironmentService.Of(ActiveEnvironment).Glyph;

    /// <summary>Среда для файла: пишется только при участке или Среде, отличной от «Объём и Конструктив» (эталон и старые проекты без блока).</summary>
    public string? EnvironmentForFile => ActiveEnvironment != DeveloperEnvironment.Building || Scene.SitePlan is not null ? WorkspaceEnvironmentService.Code(ActiveEnvironment) : null;

    [ObservableProperty]
    public partial bool IsEnvironmentPanelOpen { get; set; } = true;

    /// <summary>Панель Среды (справа, слева от инспектора): не вместе с панелью «Конструкции», секущим боксом и прогулкой.</summary>
    public bool IsEnvironmentPanelVisible => IsEnvironmentPanelOpen && !IsStructuralMode && !IsWalkthrough && !IsSectionPanelVisible;

    [RelayCommand]
    private void ToggleEnvironmentPanel() => IsEnvironmentPanelOpen = !IsEnvironmentPanelOpen;

    [RelayCommand]
    private void SelectEnvironment(string code) => ActiveEnvironment = WorkspaceEnvironmentService.Parse(code, ActiveEnvironment);

    // ═════════════════════════════ Живой участок ═════════════════════════════

    public bool HasSitePlan => Scene.SitePlan is not null;

    /// <summary>Ручки поворотных точек и вершин зон во вьюпорте — в Среде 1 при участке.</summary>
    public bool IsSiteEditing => ActiveEnvironment == DeveloperEnvironment.Site && Scene.SitePlan is not null && !IsWalkthrough;

    [ObservableProperty]
    public partial SiteVertexRef? SelectedSiteVertex { get; set; }

    partial void OnSelectedSiteVertexChanged(SiteVertexRef? value) => SiteMetricsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Показатели участка (пересчёт на лету — раз в кадр UI после изменения участка или зданий).</summary>
    public SiteMetrics? SiteMetrics { get; private set; }

    private void QueueSiteMetrics()
    {
        if (_siteMetricsQueued) return;
        _siteMetricsQueued = true;
        RunOnUiDeferred(() =>
        {
            _siteMetricsQueued = false;
            RefreshSiteMetrics();
        });
    }

    public SiteMetrics? RefreshSiteMetrics()
    {
        SiteMetrics = Scene.SitePlan is { } plan ? EditableSiteTerrainService.Analyze(plan, Scene.Elements.ToList()) : null;
        if (SiteMetrics is not null) LastGpzuCompliance = SiteMetrics.Compliance;
        SiteMetricsChanged?.Invoke(this, EventArgs.Empty);
        return SiteMetrics;
    }

    /// <summary>Правка участка — ровно один шаг журнала; недопустимый контур (самопересечение, &lt; 3 вершин) отклоняется с уведомлением.</summary>
    private bool ApplySiteEdit(GpzuSitePlan? next, string name)
    {
        if (next is null)
        {
            ShowNotice(Loc.Get("W6SiteTitle"), Loc.Get("W6SiteInvalid"), "AIA-W6-SITE");
            return false;
        }
        Scene.ApplySitePlan(next, name);
        RefreshSiteMetrics();
        return true;
    }

    private string VertexName(SiteVertexRef r) => Scene.SitePlan is { } plan && r.Layer == SiteVertexLayer.Boundary && r.Index < plan.PointNumbers.Count
        ? Loc.Format("W6PointN", plan.PointNumbers[r.Index])
        : Loc.Format("W6ZoneVertexN", r.Index + 1);

    public bool MoveSiteVertex(SiteVertexRef r, double x, double z)
    {
        if (Scene.SitePlan is not { } plan || EditableSiteTerrainService.PointOf(plan, r) is not { } p) return false;
        if (Math.Abs(p.X - x) < 1e-4 && Math.Abs(p.Z - z) < 1e-4) return false;
        double d = Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
        bool ok = ApplySiteEdit(EditableSiteTerrainService.MoveVertex(plan, r, x, z), Loc.Format("W6UndoMove", VertexName(r), d.ToString("0.00", Loc.Culture) + " м"));
        if (ok) SelectedSiteVertex = r;
        return ok;
    }

    public bool MoveSiteVertexGeo(SiteVertexRef r, double geoX, double geoY)
    {
        if (Scene.SitePlan is not { } plan) return false;
        bool ok = ApplySiteEdit(EditableSiteTerrainService.MoveVertexGeo(plan, r, geoX, geoY), Loc.Format("W6UndoMove", VertexName(r), "МСК"));
        if (ok) SelectedSiteVertex = r;
        return ok;
    }

    public bool InsertSiteVertex(SiteVertexRef edge)
    {
        if (Scene.SitePlan is not { } plan) return false;
        bool ok = ApplySiteEdit(EditableSiteTerrainService.InsertVertex(plan, edge), Loc.Get("W6UndoInsert"));
        if (ok) SelectedSiteVertex = edge with { Index = edge.Index + 1 };
        return ok;
    }

    public bool RemoveSiteVertex(SiteVertexRef r)
    {
        if (Scene.SitePlan is not { } plan) return false;
        string name = VertexName(r);
        bool ok = ApplySiteEdit(EditableSiteTerrainService.RemoveVertex(plan, r), Loc.Format("W6UndoRemove", name));
        if (ok) SelectedSiteVertex = null;
        return ok;
    }

    public bool SetSiteVertexElevation(int index, double elevationBs)
    {
        if (Scene.SitePlan is not { } plan) return false;
        string name = VertexName(SiteVertexRef.Boundary(index));
        return ApplySiteEdit(EditableSiteTerrainService.SetVertexElevation(plan, index, elevationBs),
            Loc.Format("W6UndoElevation", name, elevationBs.ToString("0.00", Loc.Culture)));
    }

    /// <summary>
    /// Строение на участок (Среда 2): корпус ЖК, башня, ТП или паркинг — новой постройкой рядом с уже стоящими, в границах участка
    /// с запасом до коридоров сетей; один шаг журнала. Без участка — на свободное место генплана.
    /// </summary>
    public string? AddStructureToSite(SiteStructureKind kind, int floors = 0)
    {
        string title = EditableSiteTerrainService.StructureTitle(kind);
        int n = Scene.BuildingGroups.Count(g => g.Name.StartsWith(title, StringComparison.Ordinal)) + 1;
        string name = n > 1 ? $"{title} {n}" : title;
        string groupId = "site-" + Guid.NewGuid().ToString("N")[..8];
        var elements = EditableSiteTerrainService.CreateStructure(kind, groupId, floors);
        double dx = 0, dz = 0;
        if (Scene.SitePlan is { } plan)
        {
            if (EditableSiteTerrainService.FindFreePlace(plan, Scene.Elements.ToList(), elements) is not { } place)
            {
                ShowNotice(Loc.Get("W6SiteTitle"), Loc.Format("W6NoPlace", name), "AIA-W6-PLACE");
                return null;
            }
            (dx, dz) = (place.Dx, place.Dz);
        }
        else if (Scene.Elements.Count > 0)
        {
            var (minX, maxX, _, _) = SceneStateService.Bounds(Scene.Elements.ToList());
            dx = maxX + 10 + elements.Max(e => Math.Abs(e.X) + e.Width / 2);
        }
        foreach (var e in elements) e.Position = [e.X + dx, e.Y, e.Z + dz];
        using (Scene.BeginUndoTransaction(Loc.Format("W6UndoStructure", name)))
        {
            Scene.BuildingGroups.Add(new BuildingGroup { Id = groupId, Name = name, OriginX = dx, OriginZ = dz });
            Scene.AddElements(elements, recordUndo: false);
        }
        RefreshSiteMetrics();
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("W6StructureAdded", name, elements.Count)));
        return groupId;
    }

    /// <summary>Демо-участок DemoKit/GPZU (ГПЗУ PDF + топосъёмка DWG) — в сцену одним шагом журнала.</summary>
    public async Task<bool> LoadDemoSiteAsync()
    {
        string kit = Path.Combine(AppContext.BaseDirectory, "DemoKit", "GPZU");
        string? pdf = Directory.Exists(kit) ? Directory.GetFiles(kit, "*.pdf").FirstOrDefault() : null;
        string? dwg = Directory.Exists(kit) ? Directory.GetFiles(kit, "*.dwg").FirstOrDefault() : null;
        if (pdf is null) return false;
        await LoadGpzuAsync(pdf, deepRead: false);
        if (dwg is not null) await LoadTopoAsync(dwg);
        if (BuildGpzuPlan() is not { } plan) return false;
        BuildSiteInScene(plan);
        return true;
    }

    // ═════════════════════════════ Ограничения «НО» ═════════════════════════════

    /// <summary>Текст последнего прочитанного документа (для «Повторить поиск с уточнением»).</summary>
    public DocumentText? ConstraintSource { get; private set; }

    /// <summary>Последнее чтение / поиск: пункты для окна подтверждения.</summary>
    public ConstraintExtraction? LastConstraintExtraction { get; private set; }

    public string LastConstraintQuery { get; private set; } = string.Empty;

    /// <summary>Контуры ИИ для чтения «НО»: ядро без модели, облачный сервис по ключу шлюза, основная локальная модель.</summary>
    public IReadOnlyList<(string Key, string Title)> ConstraintModelChoices
    {
        get
        {
            var list = new List<(string, string)>();
            if (UnifiedCloudAiClient.Shared is not null)
            {
                list.Add(("sonnet", "Sonnet 5.5"));
                list.Add(("luna", "GPT 5.6 Luna"));
                list.Add(("opus", "Opus 5.5"));
            }
            if (ModelDirector.Primary is { } p) list.Add(("local", Loc.Get("W3CoreModel") + " · " + p.Option.ModelId));
            list.Add(("core", Loc.Get("W6CoreOnly")));
            return list;
        }
    }

    public string DefaultConstraintModelKey => ConstraintModelChoices[0].Key;

    /// <summary>Модель для чтения «НО» по ключу выбора (null — только ядро).</summary>
    public ConstraintModel? ResolveConstraintModel(string key)
    {
        switch (key)
        {
            case "sonnet" or "luna" or "opus" when UnifiedCloudAiClient.Shared is { } client:
            {
                var spec = key == "luna" ? UnifiedCloudAiClient.Luna56 : key == "opus" ? UnifiedCloudAiClient.Opus55 : UnifiedCloudAiClient.Sonnet55;
                return new ConstraintModel(Loc.Get("W41Cloud") + " · " + spec.Title, async (system, user, ct) =>
                {
                    var r = await client.ChatAsync(new UnifiedChatRequest
                    {
                        Model = spec.Alias,
                        System = system,
                        Messages = [UnifiedMessage.User(user)],
                        MaxTokens = 4000,
                        Temperature = 0.1,
                        Purpose = "ограничения документа",
                        ReasoningEffort = "low"
                    }, ct).ConfigureAwait(false);
                    return r.Text;
                });
            }
            case "local" when ModelDirector.Primary is { } p:
                return new ConstraintModel(Loc.Get("W3CoreModel") + " · " + p.Option.ModelId, (system, user, ct) => PlanWithModelAsync(p.Option, system, user, ct));
            default:
                return null;
        }
    }

    /// <summary>
    /// Глубокое чтение документа (PDF, DOCX, TXT, XML, JSON): все страницы, сноски «*», правила ядра и проход моделью; пункты —
    /// в окно подтверждения (ничего не применяется само).
    /// </summary>
    public async Task<ConstraintExtraction?> ExtractConstraintsAsync(string path, string? modelKey = null, bool openDialog = true, CancellationToken ct = default)
    {
        IsBusy = true;
        BusyText = Loc.Get("W6ReadingDocument");
        try
        {
            var doc = await Task.Run(() => DeepDocumentConstraintService.Read(path), ct);
            return await ExtractConstraintsAsync(doc, modelKey, openDialog, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or System.Xml.XmlException)
        {
            ShowError(Loc.Get("W6ConstraintsTitle"), ex.Message, $"AIA-W6-READ-{ex.HResult:X8}", ex.ToString());
            return null;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    public async Task<ConstraintExtraction> ExtractConstraintsAsync(DocumentText doc, string? modelKey, bool openDialog = true, CancellationToken ct = default)
    {
        ConstraintSource = doc;
        var model = ResolveConstraintModel(modelKey ?? DefaultConstraintModelKey);
        var result = await DeepDocumentConstraintService.ExtractAsync(doc, model, ct);
        LastConstraintExtraction = result;
        ChatMessages.Add(ChatMessageModel.Assistant(Loc.Format("W6ReadDone", doc.FileName, doc.Pages.Count, result.Items.Count) + "\n" + string.Join("\n", result.Log),
            model?.Title ?? Loc.Get("W5Engine")));
        if (openDialog) ConstraintDialogRequested?.Invoke(this, ConstraintDialogMode.Document);
        return result;
    }

    /// <summary>Поиск действующих ограничений по запросу (сеть + база нормативов ядра + синтез моделью) — в окно подтверждения.</summary>
    public async Task<ConstraintExtraction> SearchConstraintsAsync(string query, string? modelKey = null, bool openDialog = true, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        IsBusy = true;
        BusyText = Loc.Get("W6Searching");
        try
        {
            LastConstraintQuery = query;
            var model = ResolveConstraintModel(modelKey ?? DefaultConstraintModelKey);
            var result = await DeepDocumentConstraintService.SearchAsync(query, model, handler, ct);
            LastConstraintExtraction = result;
            ChatMessages.Add(ChatMessageModel.Assistant(Loc.Format("W6SearchDone", result.Items.Count, result.WebHits.Count) + "\n" + string.Join("\n", result.Log),
                model?.Title ?? Loc.Get("W5Engine")));
            if (openDialog) ConstraintDialogRequested?.Invoke(this, ConstraintDialogMode.Web);
            return result;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>«Повторить поиск с уточнением»: пункт перепроверяется с комментарием пользователя.</summary>
    public Task<List<ProjectConstraintItem>> RefineConstraintAsync(ProjectConstraintItem item, string comment, string? modelKey = null, CancellationToken ct = default) =>
        DeepDocumentConstraintService.RefineAsync(item, comment, item.Origin == ConstraintOrigin.Document ? ConstraintSource : null,
            ResolveConstraintModel(modelKey ?? DefaultConstraintModelKey), ct);

    /// <summary>Действующие ограничения проекта (участок или отложенные до появления участка).</summary>
    public IReadOnlyList<ProjectConstraintItem> ActiveConstraints => Scene.SitePlan?.Constraints ?? _deferredConstraints;

    /// <summary>
    /// Подтверждённые и вручную заданные пункты вступают в силу: участок получает новый набор одним шагом журнала (пункты с тем же
    /// Id заменяются), проверка посадки учитывает их сразу. Без участка — набор ждёт его появления.
    /// </summary>
    public int ApplyConfirmedConstraints(IEnumerable<ProjectConstraintItem> items)
    {
        var active = items.Where(i => i.IsActive).ToList();
        if (active.Count == 0) return 0;
        if (Scene.SitePlan is not { } plan)
        {
            _deferredConstraints.RemoveAll(d => active.Any(a => a.Id == d.Id));
            _deferredConstraints.AddRange(active);
            ChatMessages.Add(ChatMessageModel.System(Loc.Format("W6ConstraintsDeferred", active.Count)));
            return active.Count;
        }
        var merged = plan.Constraints.Where(c => active.All(a => a.Id != c.Id)).Concat(active).ToList();
        Scene.ApplySitePlan(EditableSiteTerrainService.WithConstraints(plan, merged), Loc.Format("W6UndoConstraints", active.Count));
        var metrics = RefreshSiteMetrics();
        ChatMessages.Add(ChatMessageModel.Assistant(Loc.Format("W6ConstraintsApplied", active.Count, merged.Count) +
            (metrics is null ? string.Empty : "\n" + Summary(metrics.Compliance)), Loc.Get("W5Engine")));
        return active.Count;
    }

    /// <summary>Снятие действующего пункта (одним шагом журнала).</summary>
    public bool RemoveConstraint(string id)
    {
        if (Scene.SitePlan is not { } plan || plan.Constraints.All(c => c.Id != id)) return _deferredConstraints.RemoveAll(c => c.Id == id) > 0;
        Scene.ApplySitePlan(plan.With(constraints: plan.Constraints.Where(c => c.Id != id).ToList()), Loc.Get("W6UndoConstraintRemove"));
        RefreshSiteMetrics();
        return true;
    }

    /// <summary>Отложенные пункты — на новый участок (вызывается при переносе участка в сцену).</summary>
    private GpzuSitePlan WithDeferredConstraints(GpzuSitePlan plan)
    {
        if (_deferredConstraints.Count == 0) return plan;
        var merged = plan.Constraints.Concat(_deferredConstraints.Where(d => plan.Constraints.All(c => c.Id != d.Id))).ToList();
        _deferredConstraints.Clear();
        return plan.With(constraints: merged);
    }

    [GeneratedRegex(@"(найд|найти|поиск|ищи|какие|покажи|подбери)[^.?!]{0,60}(ограничени|охранн\w*\s+зон|санитарн|отступ|сзз|зоны\s+с\s+особыми)", RegexOptions.IgnoreCase)]
    private static partial Regex ConstraintQuery();

    private async Task<bool> TryHandleConstraintQueryAsync(string text)
    {
        if (!ConstraintQuery().IsMatch(text)) return false;
        ChatMessages.Add(ChatMessageModel.User(text));
        await SearchConstraintsAsync(text);
        return true;
    }

    [RelayCommand]
    private void OpenConstraintReview() => ConstraintDialogRequested?.Invoke(this, ConstraintDialogMode.Review);

    /// <summary>Асинхронное действие в потоке интерфейса (из фоновой задачи).</summary>
    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess) return action();
        var tcs = new TaskCompletionSource();
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    // ═════════════════════════════ Среда 3 ═════════════════════════════

    [ObservableProperty]
    public partial int FloorPackageSize { get; set; } = 5;

    partial void OnFloorPackageSizeChanged(int value) => RefreshInteriors();

    public InteriorsSummary? Interiors { get; private set; }

    public event EventHandler? InteriorsChanged;

    public InteriorsSummary RefreshInteriors()
    {
        Interiors = WorkspaceEnvironmentService.AnalyzeInteriors(Scene.Elements.ToList(), Scene.SitePlan, Math.Clamp(FloorPackageSize, 1, 50));
        InteriorsChanged?.Invoke(this, EventArgs.Empty);
        return Interiors;
    }

    // ═════════════════════════════ Автосохранение и выход ═════════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoSaveStatus))]
    public partial string AutoSaveStatusText { get; set; } = string.Empty;

    public bool HasAutoSaveStatus => AutoSaveStatusText.Length > 0;

    private void OnProjectSavedWave6(string path) =>
        AutoSaveStatusText = Loc.Format("W6ProjectSaved", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

    /// <summary>Нужен ли вопрос при выходе: в проекте есть здание, участок или несохранённые правки.</summary>
    public bool ShouldConfirmExit => Scene.IsDirty || Scene.Elements.Count > 0 || Scene.SitePlan is not null;

    /// <summary>Восстановление последней рабочей сессии из автосохранения (в 1 клик со стартового окна).</summary>
    public async Task<bool> RestoreAutoSaveSessionAsync(string? directory = null)
    {
        directory ??= AutoSaveService.DefaultDirectory;
        if (AutoSaveService.TryReadSession(directory) is not { } info) return false;
        try
        {
            var file = await ProjectFiles.OpenAsync(Path.Combine(directory, AutoSaveService.FileName));
            ApplyOpenedProject(file);
            Scene.ProjectName = info.ProjectName.Length > 0 ? info.ProjectName : Scene.ProjectName;
            Scene.ProjectFilePath = info.OriginalPath is { Length: > 0 } p && File.Exists(p) ? p : null;
            if (WorkspaceEnvironmentService.Parse(info.Environment, ActiveEnvironment) is var env) ActiveEnvironment = env;
            Scene.IsDirty = true;
            ChatMessages.Add(ChatMessageModel.System(Loc.Format("W6SessionRestored", info.SavedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), info.Elements)));
            AutoSaveStatusText = Loc.Format("W6AutoSaved", info.SavedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            RefreshSiteMetrics();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ShowError(Loc.Get("W6RestoreTitle"), ex.Message, $"AIA-W6-RESTORE-{ex.HResult:X8}", ex.ToString());
            return false;
        }
    }
}
