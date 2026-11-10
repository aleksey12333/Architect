using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace AIArchitectStudio.ViewModels;

public enum ViewportMode
{
    Perspective3D,
    Plan2D,
    Section2D
}

/// <summary>Главная ViewModel студии: TitleBar, сцена, чат ИИ, дерево BIM, Transform Inspector и смета.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private bool _syncingInspector;
    private bool _rebuildingLevels;
    private readonly AIOrchestrator _ai;
    private readonly DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _aiCts;
    private int _noticeVersion;

    public SettingsService Settings { get; }
    public ThemeService Theme { get; }
    public LocalizationService Loc { get; }
    public SceneStateService Scene { get; }
    public AiModelRegistry ModelRegistry { get; }
    public WebAccountBridgeProvider WebBridge { get; }
    public BimAnalyticsService Analytics { get; }
    public ProjectFileService ProjectFiles { get; }
    public CadExportService CadExport { get; }

    public IReadOnlyList<string> ThemeNames => ThemeService.ThemeNames;
    public IReadOnlyList<AiModelOption> AiModels => ModelRegistry.All;
    public IReadOnlyList<string> ElementTypeNames => ElementTypes.All;

    public ObservableCollection<LevelOption> Levels { get; } = [];
    public ObservableCollection<ChatMessageModel> ChatMessages { get; } = [];
    public ObservableCollection<BimTreeNode> TreeNodes { get; } = [];
    public ObservableCollection<BimSpecificationLine> SpecLines { get; } = [];

    public MainViewModel(SettingsService settings, ThemeService theme, LocalizationService loc, SceneStateService scene,
        AiModelRegistry models, AIOrchestrator ai, WebAccountBridgeProvider webBridge,
        BimAnalyticsService analytics, ProjectFileService projectFiles, CadExportService cadExport, StructuralAnalysisService structure,
        AgentModelGateway councilGateway, OllamaProcessManager ollama, AppStartupOptions startup)
    {
        Structure = structure;
        Settings = settings;
        Theme = theme;
        Loc = loc;
        Scene = scene;
        ModelRegistry = models;
        WebBridge = webBridge;
        Analytics = analytics;
        ProjectFiles = projectFiles;
        CadExport = cadExport;
        _ai = ai;
        // Живое приложение: строгая логика проёмов в ответах генератора (сдвиг от колонн, отказ проёму без места в стене).
        _ai.EnforceBuildingLogic = true;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        var s = settings.Current;
        Loc.Language = s.Language;
        ChatMessageModel.CurrentLanguage = Loc.Language;
        SelectedThemeName = s.ThemeName;
        SelectedAiModel = ModelRegistry.Resolve(s.AiModel).Id;
        IsAppendToSite = s.AppendToSiteByDefault;
        ExperienceProfile = s.ExperienceProfile;
        Modules = s.EffectiveModules;

        ModelRegistry.ModelsChanged += (_, _) => RunOnUi(() =>
        {
            OnPropertyChanged(nameof(AiModels));
            OnPropertyChanged(nameof(SelectedAiModelShort));
        });
        WebBridge.ResponseCaptured += (_, capture) => RunOnUi(() => ApplyWebCapture(capture));
        WebBridge.PendingChanged += (_, _) => RunOnUi(() => OnPropertyChanged(nameof(IsWebAwaiting)));

        Scene.SceneChanged += (_, _) => RefreshSceneViews();
        Scene.ElementsTransformed += OnElementsTransformed;
        Scene.GridAxesChanged += OnSceneGridAxesChanged;
        Analytics.SpecificationChanged += (_, spec) => RunOnUi(() => ApplySpecification(spec));
        Scene.PropertyChanged += OnScenePropertyChanged;
        Loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalizationService.Language)) OnLanguageChanged();
        };

        ChatMessages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HistoryLabel));
        ChatMessages.Add(ChatMessageModel.System(Loc.Get("Welcome")));
        if (startup.StartupPresetId is { } presetId) LoadStartupScene(presetId);
        RefreshSceneViews();
        InitializeEngineering();
        InitializeModeling();
        InitializePresentation();
        InitializeUserParameters();
        InitializeCouncil(ai, councilGateway);
        InitializeOllama(ollama);
        InitializeAttachments();
        InitializeWave1();
        InitializeWave2();
        InitializeWave3();
        InitializeWave6();
    }

    /// <summary>Стартовая сцена (шаблон стартового окна или Пресет 1 без оболочки): без записи в Undo и без пометки «изменено».</summary>
    public void LoadStartupScene(string presetId)
    {
        try
        {
            var demo = PresetScenesService.Create(presetId, Loc.Language);
            Scene.LoadScene(demo.Elements, demo.Groups, recordUndo: false, clearHistory: true, axes: demo.Axes ?? []);
            Scene.ProjectName = demo.Title;
            Scene.IsDirty = false;
        }
        catch (Exception ex)
        {
            ShowError(Loc.Get("ErrorTitle"), ex.Message, "AIA-PRESET-STARTUP", ex.ToString());
        }
    }

    /// <summary>«Новый проект» стартового окна: чистая сцена без истории.</summary>
    public void StartEmptyProject()
    {
        Scene.LoadScene([], [], recordUndo: false, clearHistory: true, axes: []);
        Scene.ProjectName = Loc.Get("NewProjectName");
        Scene.IsDirty = false;
    }

    // ───────────────────────────── Режим вида и TitleBar ─────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPerspectiveMode), nameof(IsPlanMode), nameof(IsSectionMode), nameof(ViewModeTitle))]
    public partial ViewportMode ViewMode { get; set; } = ViewportMode.Perspective3D;

    public bool IsPerspectiveMode
    {
        get => ViewMode == ViewportMode.Perspective3D && !IsStructuralMode;
        set
        {
            if (!value) return;
            IsStructuralMode = false;
            ViewMode = ViewportMode.Perspective3D;
        }
    }

    public bool IsPlanMode
    {
        get => ViewMode == ViewportMode.Plan2D;
        set
        {
            if (!value) return;
            IsStructuralMode = false;
            ViewMode = ViewportMode.Plan2D;
        }
    }

    public bool IsSectionMode
    {
        get => ViewMode == ViewportMode.Section2D;
        set
        {
            if (!value) return;
            IsStructuralMode = false;
            ViewMode = ViewportMode.Section2D;
        }
    }

    public string ViewModeTitle => ViewMode switch
    {
        ViewportMode.Plan2D => Loc.Get("ViewPlanTitle"),
        ViewportMode.Section2D => Loc.Get("ViewSectionTitle"),
        _ => Loc.Get("View3DTitle")
    };

    /// <summary>Запрос вьюпорту: вписать сцену в кадр текущей камеры.</summary>
    public event EventHandler? ZoomExtentsRequested;

    [RelayCommand]
    private void ZoomToFit() => ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);

    // ───────────────────────────── Парящие острова UI ─────────────────────────────

    /// <summary>Вкладка правого острова: 0 — «Инспектор», 1 — «Оси», 2 — «Смета», 3 — «Анализ» (нагрузки и коллизии).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInspectorTab), nameof(IsAxesTab), nameof(IsEstimateTab), nameof(IsAnalysisTab))]
    public partial int RightTab { get; set; }

    partial void OnRightTabChanged(int value)
    {
        if (value == 1) RefreshAxesEditor();
    }

    public bool IsInspectorTab
    {
        get => RightTab == 0;
        set { if (value) RightTab = 0; }
    }

    public bool IsAxesTab
    {
        get => RightTab == 1;
        set { if (value) RightTab = 1; }
    }

    public bool IsEstimateTab
    {
        get => RightTab == 2;
        set { if (value) RightTab = 2; }
    }

    [ObservableProperty]
    public partial bool IsInspectorCollapsed { get; set; }

    [RelayCommand]
    private void ToggleInspector() => IsInspectorCollapsed = !IsInspectorCollapsed;

    /// <summary>Всплывающее окно истории чата и лога C#-солвера над ИИ-доком.</summary>
    [ObservableProperty]
    public partial bool IsHistoryOpen { get; set; }

    [RelayCommand]
    private void ToggleHistory() => IsHistoryOpen = !IsHistoryOpen;

    public string HistoryLabel => $"{Loc.Get("History")} · {ChatMessages.Count}";

    public AiModelOption SelectedModel => ModelRegistry.Resolve(SelectedAiModel);

    /// <summary>Короткое имя модели в капсуле ИИ-дока.</summary>
    public string SelectedAiModelShort => ModelLabel(SelectedModel, shortForm: true);

    public string SelectedAiModelGlyph => AiModelCatalog.ProviderGlyph(SelectedModel.IsWeb ? AiProviderKind.WebChatGpt : SelectedModel.Provider);

    /// <summary>Подпись модели в меню: «Qwen 2.5 Coder 3B · Локально», «Gemini · gemini-2.5-flash», «Веб-аккаунт · ChatGPT (chatgpt.com)».</summary>
    public string ModelLabel(AiModelOption model, bool shortForm = false)
    {
        if (model.IsLocal) return shortForm ? model.ShortName : $"{model.DisplayName} · {Loc.Get("BuiltInSuffix")}";
        if (model.IsOllama)
            return shortForm ? model.ShortName
                : $"{model.ModelId} · {(model.ParameterBillions > 0 ? model.ParameterBillions.ToString("0.#", Loc.Culture) + "B · " : string.Empty)}" +
                  $"{(model.SizeGb > 0 ? model.SizeGb.ToString("0.0", Loc.Culture) + " " + Loc.Get("Gb") + " · " : string.Empty)}{Loc.Get("LocalSuffix")}" +
                  (model.IsReasoning ? " · " + Loc.Get("ReasoningSuffix") : string.Empty);
        if (model.IsWeb) return shortForm ? $"{Loc.Get("WebShort")} · {AiModelCatalog.ProviderTitle(model.Provider)}" : $"{Loc.Get("WebAccount")} · {model.DisplayName}";
        return shortForm ? model.ShortName : model.DisplayName;
    }

    /// <summary>Показ строительных осей А-1 (используется вьюпортом).</summary>
    [ObservableProperty]
    public partial bool ShowGridAxes { get; set; } = true;

    /// <summary>Каркасный режим отображения (используется вьюпортом).</summary>
    [ObservableProperty]
    public partial bool IsWireframe { get; set; }

    [ObservableProperty]
    public partial string SelectedThemeName { get; set; } = ThemeService.CupertinoDark;

    partial void OnSelectedThemeNameChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Theme.Apply(value);
        // Ручной выбор темы отключает следование системной теме Windows.
        if (Settings.Current.ThemeName != value)
            SaveSettingsSafe(s =>
            {
                s.ThemeName = value;
                s.FollowSystemTheme = false;
            });
    }

    [ObservableProperty]
    public partial LevelOption? SelectedLevel { get; set; }

    partial void OnSelectedLevelChanged(LevelOption? value)
    {
        if (_rebuildingLevels || value is null) return;
        Scene.ActiveLevelFilter = value.Index;
    }

    public string LanguageLabel => Loc.Language;

    [RelayCommand]
    private void ToggleLanguage()
    {
        Loc.Toggle();
        SaveSettingsSafe(s => s.Language = Loc.Language);
    }

    private void OnLanguageChanged()
    {
        // Подписи «Вы / Система / Ошибка» в истории чата переключаются вместе с языком интерфейса.
        ChatMessageModel.CurrentLanguage = Loc.Language;
        foreach (var m in ChatMessages) m.Language = Loc.Language;
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(ViewModeTitle));
        OnPropertyChanged(nameof(SelectedHint));
        OnPropertyChanged(nameof(HistoryLabel));
        OnPropertyChanged(nameof(SelectedAiModelShort));
        OnPropertyChanged(nameof(ChatPlaceholder));
        OnPropertyChanged(nameof(StressScaleText));
        OnPropertyChanged(nameof(SolarInfoText));
        OnPropertyChanged(nameof(SolarShadowText));
        RefreshSolarLanguage();
        RefreshMultiSelection();
        RefreshCouncilSlots();
        RefreshOllamaTexts();
        RefreshVersions();
        OnPropertyChanged(nameof(ReviewFooterText));
        OnPropertyChanged(nameof(CouncilTimelineLabel));
        OnPropertyChanged(nameof(ConsultationTitle));
        OnPropertyChanged(nameof(ConsultationMeta));
        RefreshSceneViews();
        OnAnalysisChanged();
        RunClashDetection();
    }

    // ───────────────────────────── ИИ-Проектировщик ─────────────────────────────

    /// <summary>Ключ выбранной модели (local:qwen3b, gemini:…, openai:…, anthropic:…, openrouter:…, web:…).</summary>
    [ObservableProperty]
    public partial string SelectedAiModel { get; set; } = AiModelCatalog.DefaultModelId;

    partial void OnSelectedAiModelChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedAiModelShort));
        OnPropertyChanged(nameof(SelectedAiModelGlyph));
        OnPropertyChanged(nameof(SelectedModelAcceptsImages));
        OnPropertyChanged(nameof(AttachmentsHint));
        if (string.IsNullOrEmpty(value)) return;
        if (Settings.Current.AiModel != value) SaveSettingsSafe(s => s.AiModel = value);

        var model = SelectedModel;
        _ = UnloadLocalSafeAsync(model);
        // Сайт веб-аккаунта запоминается, но панель (и процесс браузера WebView2) открывается только явным действием пользователя:
        // восстановление модели из настроек или проекта не разворачивает онлайн-панель поверх чистого 3D-вида.
        if (model.IsWeb) WebBridgeSite = model.Provider;
    }

    /// <summary>Выбор модели в меню капсулы (явное действие): веб-аккаунт сразу открывает свою панель.</summary>
    public void SelectAiModelByUser(string id)
    {
        SelectedAiModel = id;
        if (SelectedModel.IsWeb) IsWebBridgeOpen = true;
    }

    private async Task UnloadLocalSafeAsync(AiModelOption model)
    {
        try
        {
            await _ai.OnModelSelectedAsync(model);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("local-unload", ex);
        }
    }

    // ───────────────────────────── Веб-аккаунт (WebView2) ─────────────────────────────

    /// <summary>Плавающая стеклянная панель WebView2 с веб-чатом.</summary>
    [ObservableProperty]
    public partial bool IsWebBridgeOpen { get; set; }

    /// <summary>Какой сайт открыт в панели: ChatGPT, Claude или Gemini.</summary>
    [ObservableProperty]
    public partial AiProviderKind WebBridgeSite { get; set; } = AiProviderKind.WebChatGpt;

    public bool IsWebAwaiting => WebBridge.Pending is not null;

    [RelayCommand]
    private void CloseWebBridge() => IsWebBridgeOpen = false;

    [RelayCommand]
    private void ToggleWebBridge() => IsWebBridgeOpen = !IsWebBridgeOpen;

    /// <summary>Применяет JSON, перехваченный из веб-чата (DOM-поллинг, буфер обмена или кнопка).</summary>
    private void ApplyWebCapture(WebBridgeCapture capture)
    {
        var model = AiModelCatalog.WebOptions.First(o => o.Provider == capture.Site.Kind);
        var context = capture.Context ?? _ai.CreateContext(Loc.Get("WebManualTask"), model, IsAppendToSite);
        string source = $"{Loc.Get("WebAccount")} · {capture.Site.Title} ({capture.Source})";
        try
        {
            OnAiSucceeded(_ai.ApplyResponseText(capture.Text, context, source), external: true);
        }
        catch (AiProviderException ex)
        {
            ShowError(Loc.Get("ErrorAi"), ex.Message, ex.Code, ex.Details);
        }
        catch (Exception ex)
        {
            ShowError(Loc.Get("ErrorAi"), ex.Message, $"AIA-WEB-{ex.HResult:X8}", ex.ToString());
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewSceneMode))]
    public partial bool IsAppendToSite { get; set; }

    public bool IsNewSceneMode
    {
        get => !IsAppendToSite;
        set { if (value) IsAppendToSite = false; }
    }

    public bool IsAppendMode
    {
        get => IsAppendToSite;
        set { if (value) IsAppendToSite = true; }
    }

    partial void OnIsAppendToSiteChanged(bool value) => OnPropertyChanged(nameof(IsAppendMode));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChatCommand))]
    public partial string ChatInput { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChatCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    /// <summary>Строка состояния ИИ во время запроса («Qwen 3B думает…»).</summary>
    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    /// <summary>Готовность долгой задачи 0…100 % (волна 4.1: живой диалог по снимку); −1 — без полосы прогресса.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBusyPercent))]
    public partial double BusyPercent { get; set; } = -1;

    public bool HasBusyPercent => BusyPercent >= 0;

    /// <summary>Подсказка над полем ввода: запрос изменит выбранный элемент.</summary>
    public string SelectedHint => Scene.SelectedElement is { } el ? Loc.Format("ChatSelectedHint", el.DisplayTitle) : string.Empty;

    /// <summary>Плейсхолдер капсулы ИИ: при выделении подсказывает, что запрос изменит выбранный объект.</summary>
    public string ChatPlaceholder => Scene.SelectedElement is null ? Loc.Get(IsAgentMode ? "W3AgentPlaceholder" : IsCouncilMode ? "CouncilPlaceholder" : "ChatPlaceholder") : SelectedHint;

    private bool CanSendChat() => !string.IsNullOrWhiteSpace(ChatInput) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSendChat))]
    private async Task SendChatAsync()
    {
        var text = ChatInput.Trim();
        if (text.Length == 0) return;
        ChatInput = string.Empty;

        // Волна 4: ссылка на фото из интернета в панели команд → студия «Реконструкция по фото и плану».
        if (await TryOpenPhotoLinkAsync(text)) return;

        // Волна 6: «Найди действующие ограничения / охранные зоны …» — поиск «НО» в сети и базе нормативов с окном подтверждения.
        if (await TryHandleConstraintQueryAsync(text)) return;

        // Режим «Инженерный агент»: задача уходит агенту (инструменты программы, без снимков вида).
        if (IsAgentMode)
        {
            ChatMessages.Add(ChatMessageModel.User(text));
            await RunAgentAsync(text);
            return;
        }

        var target = Scene.SelectedElement;
        // Визуальный контекст: свежий авто-снимок вида и прикреплённые планы/эскизы — без ручной вставки.
        var attachments = await PrepareAttachmentsAsync();
        var message = ChatMessageModel.User(target is null ? text : $"[{target.DisplayTitle}] {text}");
        message.Thumbnails = attachments.Select(a => a.Bytes).ToList();
        ChatMessages.Add(message);
        if (attachments.Count == 0)
        {
            await RunAiAsync(text, VisionMode.None, null, null);
            return;
        }
        var images = attachments.Select(a => a.ToAiImage()).ToList();
        string description = RequestAttachmentService.DescribeForPrompt(attachments);
        ClearSentAttachments();
        await RunAiAsync(text, VisionMode.None, images, attachments[0].FileName, BlueprintKind.Auto, description, null);
    }

    [RelayCommand]
    private void CancelAi()
    {
        _aiCts?.Cancel();
        WebBridge.Cancel();
    }

    /// <summary>Режим «Из картинки»: 2D-план БТИ, эскиз от руки или фото здания → 3D (адаптивный промпт по типу материала).</summary>
    public async Task RunVisionAsync(VisionMode mode, string modeTitle, string imagePath, BlueprintKind kind = BlueprintKind.Auto)
    {
        try
        {
            var (bytes, mime) = await ImagePreparer.PrepareAsync(imagePath);
            var task = ChatInput.Trim();
            ChatInput = string.Empty;
            ChatMessages.Add(ChatMessageModel.User($"[Vision · {modeTitle}] {Path.GetFileName(imagePath)}" + (task.Length > 0 ? $"\n{task}" : string.Empty)));
            await RunAiAsync(task, mode, [new AiImage(bytes, mime)], Path.GetFileName(imagePath), kind, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            ShowError(Loc.Get("ErrorVision"), Loc.Format("ErrorImageRead", ex.Message), $"AIA-VISION-IMG-{ex.HResult:X8}", ex.ToString());
        }
    }

    /// <summary>
    /// Универсальный конвейер чертежей: выбранные листы PDF рендерятся в 2000 px, промпт собирается под тип материала
    /// (AdaptivePromptBuilder), ответ ИИ с блоком blueprint разворачивает BlueprintCompilerService.
    /// </summary>
    public async Task RunDrawingSetAsync(Windows.Data.Pdf.PdfDocument pdf, string path, IReadOnlyList<int> pages, BlueprintKind kind, string comment,
        Services.Pdf.ExtractedBlueprintAnnotations? vectorFacts = null, BlueprintDefinition? verified = null)
    {
        if (pages.Count == 0) return;
        IsBusy = true;
        BusyText = Loc.Format("PdfRendering", pages.Count);
        List<AiImage> images;
        byte[]? sheet;
        try
        {
            images = await PdfBlueprintExtractor.RenderForAiAsync(pdf, pages);
            sheet = SelectedModel.IsWeb ? await PdfBlueprintExtractor.ComposeSheetAsync(images) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            ShowError(Loc.Get("ErrorVision"), Loc.Format("ErrorImageRead", ex.Message), $"AIA-PDF-{ex.HResult:X8}", ex.ToString());
            return;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }

        var labels = pages.Take(PdfBlueprintExtractor.MaxPagesForAi).Select(p => Loc.Format("PdfPageShort", p + 1)).ToList();
        string fileName = Path.GetFileName(path);
        ChatMessages.Add(ChatMessageModel.User($"[PDF · {AdaptivePromptBuilder.KindTitle(kind)}] {fileName} · {string.Join(", ", labels)}" +
                                               (comment.Length > 0 ? $"\n{comment}" : string.Empty)));
        // Точные числа векторного слоя PDF и проверенная таблица — в промпт любого провайдера (API, локальный, веб-мост).
        string sheets = AdaptivePromptBuilder.DescribeSheets(labels, (int)pdf.PageCount, kind, fileName, vectorFacts, verified);
        await RunAiAsync(comment, VisionMode.DrawingSet, images, fileName, kind, sheets, sheet);
    }

    /// <summary>
    /// «Прямая компиляция 3D по параметрам (без ИИ)»: проверенная таблица → JSON blueprint → BlueprintCompilerService →
    /// C#-солвер → сцена одним шагом Undo, тем же путём, что ответ нейросети, но без единого запроса к ней.
    /// </summary>
    public void CompileBlueprintDirect(BlueprintDefinition definition, string sourceName)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string json = definition.ToSceneJson(
            $"Каркас построен прямой компиляцией по проверенным параметрам «{sourceName}»: сетка {definition.WidthM:0.###} × {definition.DepthM:0.###} м, этажей {definition.StoreyCount}.");
        ChatMessages.Add(ChatMessageModel.User(Loc.Format("DirectCompileUser", sourceName,
            definition.WidthM.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            definition.DepthM.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), definition.StoreyCount)));
        var context = _ai.CreateContext(Loc.Get("PdfBuildDirect"), SelectedModel, IsAppendToSite, VisionMode.DrawingSet);
        try
        {
            var result = _ai.ApplyResponseText(json, context, Loc.Get("PdfBuildDirect"));
            sw.Stop();
            result.Report.Actions.Insert(0, Loc.Format("DirectCompileTime", sw.Elapsed.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
            OnAiSucceeded(result);
        }
        catch (AiProviderException ex)
        {
            ShowError(Loc.Get("ErrorAi"), ex.Message, ex.Code, ex.Details);
        }
    }

    /// <summary>Общий конвейер: модель → JSON → C#-солвер → сцена (шаг Undo) → отчёт в чате и ErrorInfoBar.</summary>
    private Task RunAiAsync(string task, VisionMode vision, IReadOnlyList<AiImage>? images, string? imageName) =>
        RunAiAsync(task, vision, images, imageName, BlueprintKind.Auto, null, null);

    private async Task RunAiAsync(string task, VisionMode vision, IReadOnlyList<AiImage>? images, string? imageName,
        BlueprintKind kind, string? sheets, byte[]? clipboardImage)
    {
        var model = SelectedModel;
        var context = _ai.CreateContext(task, model, IsAppendToSite, vision, images, imageName, kind, sheets, clipboardImage);

        // Режим «Совет КБ»: Архитектор → Конструктор → Нормоконтроль → Координатор (каждая роль — своей моделью).
        if (IsCouncilMode)
        {
            await RunCouncilDesignAsync(context);
            return;
        }

        if (model.IsWeb)
        {
            WebBridgeSite = model.Provider;
            IsWebBridgeOpen = true;
            WebBridge.Prepare(context, _ai.BuildWebPrompt(context));
            ChatMessages.Add(ChatMessageModel.System(Loc.Format(context.Image is null ? "WebPromptSent" : "WebPromptSentVision", AiModelCatalog.ProviderTitle(model.Provider))));
            return;
        }

        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        IsBusy = true;
        BusyText = Loc.Format(model.IsOnDevice ? "BusyLocal" : "BusyCloud", ModelLabel(model, shortForm: true));
        try
        {
            OnAiSucceeded(await _ai.RunAsync(context, _aiCts.Token), IsExternalGenerator(model));
        }
        catch (OperationCanceledException)
        {
            ChatMessages.Add(ChatMessageModel.System(Loc.Get("AiCancelled")));
        }
        catch (AiProviderException ex)
        {
            ShowError(Loc.Get("ErrorAi"), ex.Message, ex.Code, ex.Details);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("ai", ex);
            ShowError(Loc.Get("ErrorAi"), ex.Message, $"AIA-AI-{ex.HResult:X8}", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>Ответ ИИ построен: сообщение в чат, отчёт солвера, кадрирование новой постройки.</summary>
    /// <param name="external">Ответ внешнего генератора (облако, веб-аккаунт): сцена уходит на проверку достоверности.</param>
    private void OnAiSucceeded(AiRunResult result, bool external = false)
    {
        var message = ChatMessageModel.Assistant(result.Message, result.Source, result.Report);
        ChatMessages.Add(message);
        ShowSolverReport(result.Report, result.RawJson);
        if (result.Mode != SolverMode.Modify) ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
        if (external && IsVerifyExternal) _ = RunVerificationGateAsync(result.Source, message);
    }

    /// <summary>Отчёт C#-солвера в ErrorInfoBar: успех, предупреждения или ошибки аудита; полный журнал и JSON — во Flyout.</summary>
    public void ShowSolverReport(SolverReport report, string? rawJson = null)
    {
        ErrorSeverity = report.Errors.Count > 0 || report.PhysicsErrors > 0
            ? InfoBarSeverity.Error
            : report.Warnings.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        ErrorTitle = Loc.Get("SolverReport");
        ErrorMessage = report.Summary + (report.Warnings.Count + report.Errors.Count > 0
            ? " · " + (report.Errors.FirstOrDefault() ?? report.Warnings[0])
            : string.Empty);
        ErrorCode = report.ErrorCode ?? (report.HasIssues ? "AIA-SOLVER-WARN" : "AIA-SOLVER-OK");
        ErrorDetails = report.DetailsText + (string.IsNullOrEmpty(rawJson)
            ? string.Empty
            : "\n\nJSON ответа BIM-Координатора:\n" + (rawJson.Length > 12000 ? rawJson[..12000] + "…" : rawJson));
        ErrorActionLabel = Loc.Get("SolverLog");
        UpdateErrorReport();
        IsErrorOpen = true;
        if (ErrorSeverity == InfoBarSeverity.Success) AutoCloseNotice();
    }

    /// <summary>Успешный отчёт сам скрывается через 10 секунд, если его не сменило другое сообщение.</summary>
    private async void AutoCloseNotice()
    {
        int version = ++_noticeVersion;
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (version == _noticeVersion && ErrorSeverity == InfoBarSeverity.Success) IsErrorOpen = false;
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    [RelayCommand]
    private void LoadPreset(string presetId)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var demo = PresetScenesService.Create(presetId, Loc.Language);
            string message;
            if (IsAppendToSite && Scene.Elements.Count > 0)
            {
                double offset = Scene.AppendBuildings(demo.Groups, demo.Elements);
                message = Loc.Format("PresetAppended", demo.Title, offset);
            }
            else
            {
                Scene.LoadScene(demo.Elements, demo.Groups, recordUndo: Scene.Elements.Count > 0, axes: demo.Axes, dimensions: []);
                Scene.ProjectName = demo.Title;
                message = Loc.Format("PresetLoaded", demo.Title, demo.Elements.Count);
            }
            sw.Stop();

            var report = new SolverReport
            {
                Success = true,
                ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
                InputElements = demo.Elements.Count,
                GeneratedElements = demo.Elements.Count,
                HostedOpenings = demo.Elements.Count(e => e.HostElementId is not null),
                Actions =
                [
                    $"Построек: {demo.Groups.Count} ({string.Join(", ", demo.Groups.Select(g => g.Id))})",
                    $"Параметрических элементов (RepeatLevels > 1): {demo.Elements.Count(e => e.RepeatLevels > 1)}",
                    IsAppendToSite ? "Режим AppendToSite: существующие постройки сохранены" : "Режим: новая сцена"
                ]
            };
            ChatMessages.Add(ChatMessageModel.Assistant(message, "Demo Preset", report));
        }
        catch (Exception ex)
        {
            ShowError(Loc.Get("ErrorTitle"), ex.Message, "AIA-PRESET-001", ex.ToString());
        }
    }

    [RelayCommand]
    private void ClearChat()
    {
        ChatMessages.Clear();
        ChatMessages.Add(ChatMessageModel.System(Loc.Get("Welcome")));
    }

    // ───────────────────────────── ErrorInfoBar ─────────────────────────────

    [ObservableProperty]
    public partial bool IsErrorOpen { get; set; }

    [ObservableProperty]
    public partial string ErrorTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorDetails { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity ErrorSeverity { get; set; } = InfoBarSeverity.Error;

    /// <summary>Подпись кнопки InfoBar: «Код ошибки» или «Лог солвера».</summary>
    [ObservableProperty]
    public partial string ErrorActionLabel { get; set; } = string.Empty;

    /// <summary>Полный лог для Flyout кнопки «Код ошибки».</summary>
    [ObservableProperty]
    public partial string ErrorReportText { get; set; } = string.Empty;

    public void ShowError(string title, string message, string code, string? details = null)
    {
        _noticeVersion++;
        ErrorSeverity = InfoBarSeverity.Error;
        ErrorTitle = title;
        ErrorMessage = message;
        ErrorCode = code;
        ErrorDetails = details ?? message;
        ErrorActionLabel = Loc.Get("ErrorCode");
        UpdateErrorReport();
        IsErrorOpen = true;
        ChatMessages.Add(new ChatMessageModel { Role = ChatRole.Error, Text = $"{code}: {message}" });
    }

    public void ShowNotice(string title, string message, string code)
    {
        _noticeVersion++;
        ErrorSeverity = InfoBarSeverity.Informational;
        ErrorTitle = title;
        ErrorMessage = message;
        ErrorCode = code;
        ErrorDetails = message;
        ErrorActionLabel = Loc.Get("ErrorCode");
        UpdateErrorReport();
        IsErrorOpen = true;
    }

    private void UpdateErrorReport() =>
        ErrorReportText =
            $"Код: {ErrorCode}\nВремя: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nМодуль проектирования: {ModelLabel(SelectedModel)}\nТема: {SelectedThemeName}\n" +
            $"Элементов в сцене: {Scene.Elements.Count}\n\n{ErrorTitle}: {ErrorMessage}\n\n{ErrorDetails}";

    // ───────────────────────────── Undo / Redo / правка ─────────────────────────────

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Scene.Undo();

    private bool CanUndo() => Scene.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Scene.Redo();

    private bool CanRedo() => Scene.CanRedo;

    [RelayCommand]
    private void ClearScene() => Scene.ClearScene();

    /// <summary>
    /// Delete: окно/дверь превращаются в сквозной проём (стена не замуровывается), проём или отверстие удаляются
    /// (стена/плита снова сплошная), остальные элементы удаляются вместе с привязанными проёмами.
    /// </summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        if (Scene.SelectedElement is not { } el) return;
        if (Scene.IsMultiSelection)
        {
            // Мультивыделение удаляется целиком (с проёмами) одним шагом Undo.
            Scene.RemoveElements(Scene.SelectedElements.ToList());
            return;
        }
        bool filling = el.Type is ElementTypes.Window or ElementTypes.Door;
        var keep = Scene.DeleteElement(el);
        if (keep is not null)
        {
            Scene.SelectedElement = null;
            Scene.SelectedElement = keep;
            ShowNotice(Loc.Get("OpeningKeptTitle"), Loc.Get(filling ? "OpeningKept" : "OpeningSealed"), "AIA-OPENING-KEPT");
        }
    }

    /// <summary>Кнопка «Замуровать проём» на пилюле: удаляет выбранный проём или отверстие плиты.</summary>
    [RelayCommand]
    private void SealOpening()
    {
        if (Scene.SelectedElement is { } el && ElementTypes.IsVoidType(el.Type)) Scene.RemoveElement(el);
    }

    /// <summary>Новый сквозной проём в выбранной стене (дальше — Gizmo и поля W/H/D).</summary>
    [RelayCommand]
    private void AddWallOpening()
    {
        if (Scene.SelectedElement is { } wall && ElementTypes.IsHostType(wall.Type))
            Scene.SelectedElement = Scene.AddWallOpening(wall);
    }

    /// <summary>Новое отверстие в выбранной плите, плоском покрытии или фундаменте.</summary>
    [RelayCommand]
    private void AddSlabOpening()
    {
        if (Scene.SelectedElement is { } slab && slab.CanHostSlabOpenings)
            Scene.SelectedElement = Scene.AddSlabOpening(slab);
    }

    [RelayCommand]
    private void DuplicateSelected()
    {
        if (Scene.SelectedElement is not { } el) return;
        if (Scene.IsMultiSelection)
        {
            // Копия всей группы со смещением 1 × 1 м (одним шагом Undo), выделение переходит на копии.
            var copies = Scene.CreateLinearArray(Scene.SelectedElements.ToList(), 2, 1.0, 1.0);
            Scene.SetSelection(copies.Where(c => c.HostElementId is null || copies.All(o => o.Id != c.HostElementId)));
            return;
        }
        Scene.SelectedElement = Scene.DuplicateElement(el);
    }

    [RelayCommand]
    private void RotateSelected90()
    {
        if (Scene.SelectedElement is not { } el) return;
        if (!Scene.IsMultiSelection)
        {
            Scene.RotateBy(el, 90);
            return;
        }
        // Группа поворачивается как жёсткий блок вокруг общего центра габарита (один шаг Undo).
        Scene.RotateGroup(Scene.SelectedElements.ToList(), 90);
    }

    [RelayCommand]
    private void Deselect() => Scene.SelectedElement = null;

    /// <summary>Сдвиг выбранного элемента на 0.1 м: параметр "x+", "x-", "y+", "y-", "z+", "z-".</summary>
    [RelayCommand]
    private void Nudge(string direction)
    {
        if (Scene.SelectedElement is not { } el || direction is not { Length: 2 }) return;
        double step = direction[1] == '+' ? 0.1 : -0.1;
        if (Scene.IsMultiSelection)
        {
            // Группа сдвигается синхронно одним шагом Undo.
            var group = Scene.SelectedElements.ToList();
            var starts = group.ToDictionary(g => g.Id, g => new[] { g.X, g.Y, g.Z });
            Scene.PushUndo();
            Scene.MoveGroup(group, starts, direction[0] == 'x' ? step : 0, direction[0] == 'y' ? step : 0, direction[0] == 'z' ? step : 0);
            return;
        }
        var before = OpeningState(el);
        switch (direction[0])
        {
            case 'x': Scene.Translate(el, step, 0, 0); break;
            case 'y': Scene.Translate(el, 0, step, 0); break;
            case 'z': Scene.Translate(el, 0, 0, step); break;
        }
        if (OpeningState(el) != before) EnforceOpeningAfterInspector(el);
    }

    public void SelectElement(ArchitecturalElement? element) => Scene.SelectedElement = element;

    // ───────────────────────────── Transform Inspector ─────────────────────────────

    public bool HasSelection => Scene.SelectedElement is not null;

    public string SelectedTitle => Scene.IsMultiSelection ? SelectionHeaderText : Scene.SelectedElement?.DisplayTitle ?? string.Empty;
    public string SelectedType => Scene.IsMultiSelection ? SelectionTypesText : Scene.SelectedElement?.Type ?? string.Empty;
    public string SelectedGroup => Scene.SelectedElement?.BuildingGroupId ?? string.Empty;
    public string SelectedGlyph => Scene.SelectedElement?.Glyph ?? "";

    /// <summary>Строка под координатами: тип · постройка · привязка.</summary>
    public string SelectedDetails => Scene.SelectedElement is null
        ? string.Empty
        : $"{SelectedType} · {SelectedGroup} · {SelectedHost}";

    public string SelectedHost
    {
        get
        {
            if (Scene.SelectedElement is not { } el) return string.Empty;
            if (el.HostElementId is { } hostId) return Scene.FindById(hostId)?.DisplayTitle ?? hostId;
            int hosted = Scene.GetHostedElements(el.Id).Count();
            return hosted > 0 ? Loc.Format("Hosted", hosted) : "—";
        }
    }

    [ObservableProperty] public partial string InspectorName { get; set; } = string.Empty;
    [ObservableProperty] public partial string InspectorMaterial { get; set; } = string.Empty;
    [ObservableProperty] public partial double PosX { get; set; }
    [ObservableProperty] public partial double PosY { get; set; }
    [ObservableProperty] public partial double PosZ { get; set; }
    [ObservableProperty] public partial double SizeW { get; set; }
    [ObservableProperty] public partial double SizeH { get; set; }
    [ObservableProperty] public partial double SizeD { get; set; }
    [ObservableProperty] public partial double RotY { get; set; }
    [ObservableProperty] public partial double LevelValue { get; set; }

    partial void OnPosXChanged(double value) => ApplyInspector();
    partial void OnPosYChanged(double value) => ApplyInspector();
    partial void OnPosZChanged(double value) => ApplyInspector();
    partial void OnSizeWChanged(double value) => ApplyInspector();
    partial void OnSizeHChanged(double value) => ApplyInspector();
    partial void OnSizeDChanged(double value) => ApplyInspector();
    partial void OnRotYChanged(double value) => ApplyInspector();
    partial void OnLevelValueChanged(double value) => ApplyInspector();

    partial void OnInspectorNameChanged(string value)
    {
        if (_syncingInspector || Scene.SelectedElement is not { } el || el.Name == value || Scene.IsMultiSelection) return;
        Scene.PushUndo();
        el.Name = value;
        RefreshSelectionInfo();
        RebuildTree();
    }

    /// <summary>Материал элемента (или всех выделенных — одним шагом Undo) с перестройкой геометрии и расчёта.</summary>
    partial void OnInspectorMaterialChanged(string value)
    {
        if (_syncingInspector || Scene.SelectedElement is null || string.IsNullOrWhiteSpace(value)) return;
        var targets = Scene.SelectedElements.Where(e => e.Material != value && !ElementTypes.IsVoidType(e.Type)).ToList();
        if (targets.Count == 0) return;
        Scene.UpdateElements(targets, e => e.Material = value.Trim());
    }

    private void ApplyInspector()
    {
        if (_syncingInspector || Scene.SelectedElement is not { } el) return;
        if (!double.IsFinite(PosX) || !double.IsFinite(PosY) || !double.IsFinite(PosZ) ||
            !double.IsFinite(SizeW) || !double.IsFinite(SizeH) || !double.IsFinite(SizeD) ||
            !double.IsFinite(RotY) || !double.IsFinite(LevelValue))
        {
            // Пустой NumberBox (NaN) — возвращаем реальные значения элемента.
            SyncInspector();
            return;
        }
        if (Scene.IsMultiSelection)
        {
            ApplyBatchInspector(el);
            return;
        }
        var before = OpeningState(el);
        Scene.SetTransform(el, PosX, PosY, PosZ, SizeW, SizeH, SizeD, RotY, (int)Math.Round(LevelValue));
        // Толщина, заданная вручную, переходит в несущий слой пирога (остальные слои сохраняются).
        AbsorbInspectorThickness(el);
        // Окно/дверь, сдвинутые с клавиатуры в колонну, поперечную стену или за пределы стены, — та же логика проёмов, что у Gizmo.
        if (OpeningState(el) != before) EnforceOpeningAfterInspector(el);
    }

    private void SyncInspector()
    {
        _syncingInspector = true;
        try
        {
            var el = Scene.SelectedElement;
            InspectorName = el?.Name ?? string.Empty;
            EnsureMaterialOption(el?.Material);
            InspectorMaterial = el?.Material ?? string.Empty;
            PosX = Math.Round(el?.X ?? 0, 3);
            PosY = Math.Round(el?.Y ?? 0, 3);
            PosZ = Math.Round(el?.Z ?? 0, 3);
            SizeW = Math.Round(el?.Width ?? 0, 3);
            SizeH = Math.Round(el?.Height ?? 0, 3);
            SizeD = Math.Round(el?.Depth ?? 0, 3);
            RotY = Math.Round(el?.RotationY ?? 0, 2);
            LevelValue = el?.LevelIndex ?? 0;
            if (el is not null && ElementTypes.IsProfiledType(el.Type))
            {
                var (b, h) = el.Type == ElementTypes.Beam ? (el.Depth, el.Height) : (el.Width, el.Depth);
                var (wall, web) = el.ProfileThicknesses(b, h);
                ProfileWallValue = Math.Round(el.ProfileWallThickness > 0 ? el.ProfileWallThickness : wall, 4);
                ProfileWebValue = Math.Round(el.ProfileWebThickness > 0 ? el.ProfileWebThickness : web, 4);
            }
        }
        finally
        {
            _syncingInspector = false;
        }
    }

    // ───────────────────────────── Сечение и форма кровли выбранного элемента ─────────────────────────────

    /// <summary>Выбрана колонна или балка — доступен выбор профиля сечения.</summary>
    public bool IsProfiledSelected => Scene.SelectedElement is { } el && ElementTypes.IsProfiledType(el.Type) && el.Type != ElementTypes.Diagrid;

    public bool IsRoofSelected => Scene.SelectedElement?.Type == ElementTypes.Roof;

    /// <summary>Выбрана стена (можно добавить сквозной проём).</summary>
    public bool IsWallHostSelected => Scene.SelectedElement is { } el && ElementTypes.IsHostType(el.Type);

    /// <summary>Выбрана плита, плоское покрытие или фундамент (можно добавить отверстие).</summary>
    public bool IsSlabHostSelected => Scene.SelectedElement?.CanHostSlabOpenings == true;

    /// <summary>Выбран проём-пустота (можно замуровать).</summary>
    public bool IsOpeningSelected => Scene.SelectedElement is { } el && ElementTypes.IsVoidType(el.Type);

    private string SelectedProfile => Scene.SelectedElement?.EffectiveProfile ?? ProfileTypes.Rectangle;

    public bool IsProfileRect
    {
        get => SelectedProfile is ProfileTypes.Rectangle or ProfileTypes.Polygon;
        set { if (value) SetProfile(ProfileTypes.Rectangle); }
    }

    public bool IsProfileCircle
    {
        get => SelectedProfile == ProfileTypes.Circle;
        set { if (value) SetProfile(ProfileTypes.Circle); }
    }

    public bool IsProfilePipe
    {
        get => SelectedProfile == ProfileTypes.Pipe;
        set { if (value) SetProfile(ProfileTypes.Pipe); }
    }

    public bool IsProfileIBeam
    {
        get => SelectedProfile == ProfileTypes.IBeam;
        set { if (value) SetProfile(ProfileTypes.IBeam); }
    }

    /// <summary>Поля толщин стенки/полки видны у трубы и двутавра.</summary>
    public bool IsProfileThicknessVisible => IsProfiledSelected && SelectedProfile is ProfileTypes.Pipe or ProfileTypes.IBeam;

    public bool IsWebThicknessVisible => IsProfiledSelected && SelectedProfile == ProfileTypes.IBeam;

    [ObservableProperty] public partial double ProfileWallValue { get; set; }
    [ObservableProperty] public partial double ProfileWebValue { get; set; }

    partial void OnProfileWallValueChanged(double value)
    {
        if (_syncingInspector || Scene.SelectedElement is not { } el || !double.IsFinite(value) || Math.Abs(el.ProfileWallThickness - value) < 1e-9) return;
        Scene.UpdateElement(el, e => e.ProfileWallThickness = Math.Max(0, value));
    }

    partial void OnProfileWebValueChanged(double value)
    {
        if (_syncingInspector || Scene.SelectedElement is not { } el || !double.IsFinite(value) || Math.Abs(el.ProfileWebThickness - value) < 1e-9) return;
        Scene.UpdateElement(el, e => e.ProfileWebThickness = Math.Max(0, value));
    }

    /// <summary>
    /// Смена профиля в 1 клик. Круг/труба получают равные стороны сечения (диаметр = W у колонны, = H у балки);
    /// прямоугольник у колонны снимает полигональный контур, остальные габариты не меняются.
    /// </summary>
    private void SetProfile(string profile)
    {
        if (_syncingInspector || Scene.SelectedElement is null || !IsProfiledSelected) return;
        // Пакетная правка: все выделенные колонны и балки получают профиль одним атомарным шагом Undo.
        var targets = Scene.SelectedElements.Where(e => ElementTypes.IsProfiledType(e.Type) && e.Type != ElementTypes.Diagrid && e.EffectiveProfile != profile).ToList();
        if (targets.Count == 0) return;
        Scene.UpdateElements(targets, e => SceneStateService.ApplyProfile(e, profile));
        SyncInspector();
        RefreshProfileInfo();
    }

    private string SelectedRoofShape => RoofShapes.Normalize(Scene.SelectedElement?.RoofShape);

    public bool IsRoofFlat
    {
        get => SelectedRoofShape == RoofShapes.Flat;
        set { if (value) SetRoofShape(RoofShapes.Flat); }
    }

    public bool IsRoofGable
    {
        get => SelectedRoofShape == RoofShapes.Gable;
        set { if (value) SetRoofShape(RoofShapes.Gable); }
    }

    public bool IsRoofHip
    {
        get => SelectedRoofShape == RoofShapes.Hip;
        set { if (value) SetRoofShape(RoofShapes.Hip); }
    }

    public bool IsRoofShed
    {
        get => SelectedRoofShape == RoofShapes.Shed;
        set { if (value) SetRoofShape(RoofShapes.Shed); }
    }

    private void SetRoofShape(string shape)
    {
        if (_syncingInspector || Scene.SelectedElement is not { Type: ElementTypes.Roof } el || SelectedRoofShape == shape) return;
        Scene.UpdateElement(el, e => e.RoofShape = shape);
        RefreshProfileInfo();
        RefreshSelectionInfo();
    }

    private void RefreshProfileInfo()
    {
        foreach (var name in new[]
                 {
                     nameof(IsProfiledSelected), nameof(IsRoofSelected), nameof(IsWallHostSelected), nameof(IsSlabHostSelected), nameof(IsOpeningSelected),
                     nameof(IsProfileRect), nameof(IsProfileCircle), nameof(IsProfilePipe), nameof(IsProfileIBeam),
                     nameof(IsProfileThicknessVisible), nameof(IsWebThicknessVisible),
                     nameof(IsRoofFlat), nameof(IsRoofGable), nameof(IsRoofHip), nameof(IsRoofShed)
                 })
            OnPropertyChanged(name);
    }

    // ───────────────────────────── Координационные оси (вкладка «Оси») ─────────────────────────────

    /// <summary>Постройки участка для выбора в редакторе осей.</summary>
    public ObservableCollection<string> AxesGroups { get; } = [];

    /// <summary>Оси выбранной постройки: явные (редактируемые) или предпросмотр автоматических.</summary>
    public ObservableCollection<StructuralGridAxis> EditableAxes { get; } = [];

    [ObservableProperty]
    public partial string? SelectedAxesGroup { get; set; }

    partial void OnSelectedAxesGroupChanged(string? value)
    {
        // Во время пересборки списка ComboBox сбрасывает выбор через TwoWay-привязку — это не выбор пользователя.
        if (!_refreshingAxes && value is not null) RefreshAxesEditor();
    }

    /// <summary>У постройки заданы явные оси (с чертежа или вручную).</summary>
    public bool AreAxesExplicit => SelectedAxesGroup is { } g && Scene.AxesOf(g).Any();

    public bool AreAxesAuto => !AreAxesExplicit;

    public string AxesStatusText => AreAxesExplicit ? Loc.Get("AxesExplicit") : Loc.Get("AxesAuto");

    private bool _refreshingAxes;

    private void RefreshAxesEditor()
    {
        if (_refreshingAxes) return;
        _refreshingAxes = true;
        try
        {
            var groups = Scene.BuildingGroups.Select(g => g.Id).ToList();
            string? target = SelectedAxesGroup is { } current && groups.Contains(current)
                ? current
                : Scene.SelectedElement?.BuildingGroupId is { } sg && groups.Contains(sg) ? sg : groups.FirstOrDefault();
            // Инкрементально, без Clear(): иначе ComboBox сбрасывает выбор посреди изменения коллекции.
            foreach (var g in AxesGroups.Where(g => !groups.Contains(g)).ToList()) AxesGroups.Remove(g);
            foreach (var g in groups.Where(g => !AxesGroups.Contains(g))) AxesGroups.Add(g);
            SelectedAxesGroup = target;

            foreach (var a in EditableAxes)
            {
                a.PropertyChanging -= OnAxisEditing;
                a.PropertyChanged -= OnAxisEdited;
            }
            EditableAxes.Clear();
            if (SelectedAxesGroup is { } gid)
            {
                var explicitAxes = Scene.AxesOf(gid).ToList();
                var list = explicitAxes.Count > 0
                    ? explicitAxes
                    : Rendering.StructuralAxesBuilder.AutoAxes(Scene.Elements.Where(e => e.BuildingGroupId == gid).ToList(), gid);
                foreach (var a in list.OrderBy(a => a.IsNumeric ? 0 : 1).ThenBy(a => a.IsNumeric ? a.Coordinate : -a.Coordinate))
                {
                    if (explicitAxes.Count > 0)
                    {
                        a.PropertyChanging += OnAxisEditing;
                        a.PropertyChanged += OnAxisEdited;
                    }
                    EditableAxes.Add(a);
                }
            }
        }
        finally
        {
            _refreshingAxes = false;
        }
        OnPropertyChanged(nameof(AreAxesExplicit));
        OnPropertyChanged(nameof(AreAxesAuto));
        OnPropertyChanged(nameof(AxesStatusText));
    }

    private bool _editingAxis;

    /// <summary>
    /// Правка подписи или координаты оси на месте (фиксация поля — по Enter или потере фокуса): шаг Undo записывается до
    /// изменения, снимок хранит прежнее значение — правку можно отменить, как и любую другую операцию со сценой.
    /// </summary>
    private void OnAxisEditing(object? sender, PropertyChangingEventArgs e)
    {
        if (_refreshingAxes || _editingAxis) return;
        Scene.PushUndo();
    }

    private void OnAxisEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (_refreshingAxes || sender is not StructuralGridAxis axis) return;
        _editingAxis = true;
        try
        {
            // Пустое поле координаты — 0 (в том же шаге Undo).
            if (e.PropertyName == nameof(StructuralGridAxis.Coordinate) && !double.IsFinite(axis.Coordinate)) axis.Coordinate = 0;
            Scene.NotifyGridAxesChanged();
        }
        finally
        {
            _editingAxis = false;
        }
    }

    private void OnSceneGridAxesChanged(object? sender, EventArgs e)
    {
        // Правка поля оси на месте не пересобирает список (иначе теряется фокус ввода); Undo/Redo и новые оси — пересобирают.
        if (!_editingAxis && IsAxesTab) RunOnUi(RefreshAxesEditor);
    }

    /// <summary>Перевод автоматических осей постройки в явные — дальше их можно править, добавлять и удалять.</summary>
    [RelayCommand]
    private void EditAutoAxes()
    {
        if (SelectedAxesGroup is not { } gid) return;
        var auto = Rendering.StructuralAxesBuilder.AutoAxes(Scene.Elements.Where(e => e.BuildingGroupId == gid).ToList(), gid);
        Scene.SetGroupAxes(gid, auto);
        RefreshAxesEditor();
    }

    /// <summary>Сброс явных осей: сетка снова считается автоматически по несущим конструкциям.</summary>
    [RelayCommand]
    private void ResetAxes()
    {
        if (SelectedAxesGroup is not { } gid || !AreAxesExplicit) return;
        Scene.SetGroupAxes(gid, []);
        RefreshAxesEditor();
    }

    /// <summary>Новая ось: «x» — цифровая (следующий номер, шаг 6 м), «z» — буквенная.</summary>
    [RelayCommand]
    private void AddAxis(string direction)
    {
        if (SelectedAxesGroup is not { } gid) return;
        // Перевод автоматических осей в явные и новая ось — одно действие пользователя, один шаг Undo.
        using (Scene.BeginUndoTransaction())
        {
            if (!AreAxesExplicit) EditAutoAxes();
            var axes = Scene.AxesOf(gid).ToList();
            bool numeric = direction != StructuralGridAxis.AlongX;
            var same = axes.Where(a => a.IsNumeric == numeric).ToList();
            double step = same.Count >= 2
                ? Math.Abs(same.OrderBy(a => a.Coordinate).Select(a => a.Coordinate).Zip(same.OrderBy(a => a.Coordinate).Skip(1).Select(a => a.Coordinate), (a, b) => b - a).Last())
                : 6.0;
            double coordinate = numeric
                ? (same.Count > 0 ? same.Max(a => a.Coordinate) + step : 0)
                : (same.Count > 0 ? same.Min(a => a.Coordinate) - step : 0);
            string label = numeric ? (same.Count + 1).ToString() : Rendering.StructuralAxesBuilder.Letter(same.Count);
            axes.Add(new StructuralGridAxis(label, numeric ? StructuralGridAxis.AlongZ : StructuralGridAxis.AlongX, Math.Round(coordinate, 3), gid));
            Scene.SetGroupAxes(gid, axes);
        }
        RefreshAxesEditor();
    }

    public void RemoveAxis(StructuralGridAxis axis)
    {
        if (SelectedAxesGroup is not { } gid || !AreAxesExplicit) return;
        Scene.SetGroupAxes(gid, Scene.AxesOf(gid).Where(a => !ReferenceEquals(a, axis)).ToList());
        RefreshAxesEditor();
    }

    private void RefreshSelectionInfo()
    {
        RefreshProfileInfo();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedType));
        OnPropertyChanged(nameof(SelectedGroup));
        OnPropertyChanged(nameof(SelectedGlyph));
        OnPropertyChanged(nameof(SelectedHost));
        OnPropertyChanged(nameof(SelectedHint));
        OnPropertyChanged(nameof(ChatPlaceholder));
        OnPropertyChanged(nameof(SelectedDetails));
        OnPropertyChanged(nameof(StatusText));
        RefreshLayers();
        RefreshUserParameters();
        AddUserParameterCommand.NotifyCanExecuteChanged();
        RefreshForces();
        RefreshParametricTools();
    }

    private void OnScenePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SceneStateService.SelectedElement):
                SyncInspector();
                RefreshSelectionInfo();
                RefreshMultiSelection();
                break;
            case nameof(SceneStateService.CanUndo):
                UndoCommand.NotifyCanExecuteChanged();
                break;
            case nameof(SceneStateService.CanRedo):
                RedoCommand.NotifyCanExecuteChanged();
                break;
            case nameof(SceneStateService.ActiveLevelFilter):
                SyncSelectedLevel();
                break;
            case nameof(SceneStateService.ProjectName):
            case nameof(SceneStateService.IsDirty):
                OnPropertyChanged(nameof(WindowTitle));
                break;
        }
    }

    private void OnElementsTransformed(object? sender, IReadOnlyList<ArchitecturalElement> changed)
    {
        // Смету пересчитывает BimAnalyticsService (подписан на ElementsTransformed).
        if (Scene.SelectedElement is { } sel && changed.Contains(sel))
        {
            SyncInspector();
            RefreshProfileInfo();
        }
    }

    // ───────────────────────────── Сцена, дерево, смета, статус ─────────────────────────────

    public bool IsSceneEmpty => Scene.Elements.Count == 0;

    public string SceneSummary => Loc.Format("SceneSummary", Scene.Elements.Count, Scene.BuildingGroups.Count, Scene.MaxLevelIndex + (IsSceneEmpty ? 0 : 1));

    /// <summary>Компактная сводка в шапке дерева объектов.</summary>
    public string SceneSummaryShort => $"{Scene.Elements.Count} · {Scene.BuildingGroups.Count}";

    public string ElementCountText => Scene.Elements.Count.ToString(Loc.Culture);

    public string StatusText => Scene.SelectedElement is { } el
        ? $"{SceneSummary}   |   {Loc.Format("Selected", el.DisplayTitle)}"
        : SceneSummary;

    public string WindowTitle => $"{Scene.ProjectName}{(Scene.IsDirty ? " •" : string.Empty)} — Architect Studio Pro";

    [ObservableProperty] public partial string TotalConcreteText { get; set; } = "0";
    [ObservableProperty] public partial string TotalGlazingText { get; set; } = "0";
    [ObservableProperty] public partial string TotalPoolText { get; set; } = "0";
    [ObservableProperty] public partial string TotalCostText { get; set; } = "0 ₽";

    private void RefreshSceneViews()
    {
        RebuildLevels();
        RebuildTree();
        RefreshSpecification();
        RefreshSelectionInfo();
        if (IsAxesTab) RefreshAxesEditor();
        OnPropertyChanged(nameof(IsSceneEmpty));
        OnPropertyChanged(nameof(SceneSummary));
        OnPropertyChanged(nameof(SceneSummaryShort));
        OnPropertyChanged(nameof(ElementCountText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void RebuildLevels()
    {
        _rebuildingLevels = true;
        try
        {
            int max = IsSceneEmpty ? 0 : Scene.MaxLevelIndex;
            Levels.Clear();
            Levels.Add(new LevelOption(SceneStateService.AllLevels, Loc.Get("AllLevels")));
            for (int i = 0; i <= max; i++)
                Levels.Add(new LevelOption(i, Loc.Format("LevelN", i + 1)));
            SelectedLevel = Levels.FirstOrDefault(l => l.Index == Scene.ActiveLevelFilter) ?? Levels[0];
        }
        finally
        {
            _rebuildingLevels = false;
        }
    }

    private void SyncSelectedLevel()
    {
        var match = Levels.FirstOrDefault(l => l.Index == Scene.ActiveLevelFilter);
        if (match is not null && !Equals(match, SelectedLevel))
        {
            _rebuildingLevels = true;
            SelectedLevel = match;
            _rebuildingLevels = false;
        }
    }

    private void RebuildTree()
    {
        TreeNodes.Clear();
        foreach (var group in Scene.BuildingGroups)
        {
            var items = Scene.Elements.Where(e => e.BuildingGroupId == group.Id).ToList();
            if (items.Count == 0) continue;

            var groupNode = new BimTreeNode
            {
                Title = group.Name,
                Subtitle = $"{items.Count}",
                Glyph = "",
                IsExpanded = true
            };

            foreach (var level in items.Select(e => e.LevelIndex).Distinct().Order())
            {
                var levelItems = items.Where(e => e.LevelIndex == level).ToList();
                var levelNode = new BimTreeNode
                {
                    Title = Loc.Format("LevelN", level + 1),
                    Subtitle = $"{levelItems.Count}",
                    Glyph = "",
                    IsExpanded = Scene.BuildingGroups.Count == 1
                };

                // Корневые элементы этажа: всё, кроме проёмов, чей хост существует.
                foreach (var el in levelItems.Where(e => e.HostElementId is null || Scene.FindById(e.HostElementId) is null))
                    levelNode.Children.Add(CreateElementNode(el));

                groupNode.Children.Add(levelNode);
            }
            TreeNodes.Add(groupNode);
        }
    }

    private BimTreeNode CreateElementNode(ArchitecturalElement el)
    {
        var node = new BimTreeNode
        {
            Title = el.DisplayTitle,
            Subtitle = el.RepeatLevels > 1 ? $"{el.Type} ×{el.RepeatLevels}" : el.Type,
            Glyph = el.Glyph,
            Element = el
        };
        foreach (var child in Scene.GetHostedElements(el.Id))
            node.Children.Add(CreateElementNode(child));
        return node;
    }

    public void RefreshSpecification() => ApplySpecification(Analytics.Current);

    private void ApplySpecification(BimSpecification spec)
    {
        SpecLines.Clear();
        foreach (var line in spec.Lines) SpecLines.Add(line);

        var c = Loc.Culture;
        TotalConcreteText = spec.TotalConcrete.ToString("N1", c);
        TotalGlazingText = spec.TotalGlazing.ToString("N1", c);
        TotalPoolText = spec.TotalPool.ToString("N1", c);
        TotalCostText = spec.TotalCost.ToString("N0", c) + " ₽";
        ApplySteelTotals(spec);
    }

    // ───────────────────────────── Файлы проекта и внешние модули ─────────────────────────────

    /// <summary>Параметры проекта для записи .aia (сохранение и автосохранение Волны 6).</summary>
    public SceneProjectParameters CaptureProjectParameters()
    {
        var s = Settings.Current;
        var spec = Analytics.Current;
        return new SceneProjectParameters
        {
            AiModel = SelectedAiModel,
            AppendToSite = IsAppendToSite,
            ShowGridAxes = ShowGridAxes,
            Wireframe = IsWireframe,
            ThemeName = SelectedThemeName,
            ConcretePricePerM3 = s.ConcretePricePerM3,
            GlazingPricePerM2 = s.GlazingPricePerM2,
            TotalConcreteM3 = Math.Round(spec.TotalConcrete, 3),
            TotalGlazingM2 = Math.Round(spec.TotalGlazing, 3),
            TotalCostRub = Math.Round(spec.TotalCost),
            RebarPricePerTon = s.RebarPricePerTon,
            TotalSteelKg = Math.Round(spec.TotalSteelKg, 1),
            StructuralLoads = s.StructuralLoads.Clone(),
            SolarRegion = SolarLocation.Id,
            CostEstimate = BuildCostEstimate()
        };
    }

    public async Task SaveProjectAsync(string path)
    {
        try
        {
            var s = Settings.Current;
            var spec = Analytics.Current;
            var parameters = new SceneProjectParameters
            {
                AiModel = SelectedAiModel,
                AppendToSite = IsAppendToSite,
                ShowGridAxes = ShowGridAxes,
                Wireframe = IsWireframe,
                ThemeName = SelectedThemeName,
                ConcretePricePerM3 = s.ConcretePricePerM3,
                GlazingPricePerM2 = s.GlazingPricePerM2,
                TotalConcreteM3 = Math.Round(spec.TotalConcrete, 3),
                TotalGlazingM2 = Math.Round(spec.TotalGlazing, 3),
                TotalCostRub = Math.Round(spec.TotalCost),
                RebarPricePerTon = s.RebarPricePerTon,
                TotalSteelKg = Math.Round(spec.TotalSteelKg, 1),
                StructuralLoads = s.StructuralLoads.Clone(),
                SolarRegion = SolarLocation.Id,
                CostEstimate = BuildCostEstimate()
            };
            // Сообщение о сохранении добавляется до записи, чтобы оно попало в историю файла.
            ChatMessages.Add(ChatMessageModel.System(Loc.Format("ProjectSaved", path)));
            await ProjectFiles.SaveAsync(path, new ProjectSnapshot(ViewMode.ToString(), Scene.ActiveLevelFilter, parameters, ChatMessages.ToList(), EnvironmentForFile));
            ProjectEstimate = parameters.CostEstimate;
            RememberRecentProject(path);
            OnProjectSavedWave6(path);
        }
        catch (Exception ex)
        {
            ShowError(Loc.Get("ErrorSave"), ex.Message, $"AIA-IO-SAVE-{ex.HResult:X8}", ex.ToString());
        }
    }

    public async Task OpenProjectAsync(string path)
    {
        try
        {
            var file = await ProjectFiles.OpenAsync(path);
            ApplyOpenedProject(file);
            Scene.IsDirty = false;
            RememberRecentProject(path);
        }
        catch (Exception ex)
        {
            ShowError(Loc.Get("ErrorOpen"), ex.Message, $"AIA-IO-OPEN-{ex.HResult:X8}", ex.ToString());
        }
    }

    /// <summary>Параметры сессии, история чата, смета и Среда из открытого файла (открытие проекта и восстановление сессии).</summary>
    private void ApplyOpenedProject(SceneProjectFile file)
    {
        {
            if (Enum.TryParse<ViewportMode>(file.ViewMode, out var mode)) ViewMode = mode;

            // Параметры сессии из файла (тема и модель — только если они доступны на этом компьютере).
            var p = file.Parameters;
            if (file.FormatVersion >= 2)
            {
                IsAppendToSite = p.AppendToSite;
                ShowGridAxes = p.ShowGridAxes;
                IsWireframe = p.Wireframe;
                if (ThemeNames.Contains(p.ThemeName)) SelectedThemeName = p.ThemeName;
                if (!string.IsNullOrEmpty(p.AiModel) && ModelRegistry.All.Any(m => m.Id == p.AiModel)) SelectedAiModel = p.AiModel;
            }
            if (file.FormatVersion >= 4) ApplyProjectLoads(p);
            if (!string.IsNullOrWhiteSpace(p.SolarRegion)) SelectRegion(p.SolarRegion);

            ChatMessages.Clear();
            foreach (var m in file.ChatHistory) ChatMessages.Add(m);
            ChatMessages.Add(ChatMessageModel.System(Loc.Format("ProjectLoaded", Scene.ProjectName, file.Elements.Count)));
            RestoreCostEstimate(p.CostEstimate);
            if (file.Site?.Environment is { } env) ActiveEnvironment = WorkspaceEnvironmentService.Parse(env, ActiveEnvironment);
        }
    }

    /// <summary>Экспорт в IFC / DXF / OBJ: снимок сцены (клоны) пишется CadExportService в фоне, отчёт — в чат и InfoBar.</summary>
    public async Task ExportAsync(CadExportFormat format, string path)
    {
        string tag = format.ToString().ToUpperInvariant();
        if (Scene.Elements.Count == 0)
        {
            ShowError(Loc.Get("ErrorExport"), Loc.Get("ExportEmpty"), $"AIA-EXPORT-{tag}-EMPTY");
            return;
        }

        var elements = Scene.Elements.Select(e => e.Clone()).ToList();
        var groups = Scene.BuildingGroups.Select(g => g.Clone()).ToList();
        IsBusy = true;
        BusyText = Loc.Format("ExportBusy", tag);
        try
        {
            var axes = Scene.GridAxes.Select(a => a.Clone()).ToList();
            var dimensions = Scene.PinnedDimensions.Select(d => d.Clone()).ToList();
            var result = await CadExport.ExportAsync(format, path, elements, groups, Scene.ProjectName, axes, dimensions, baseQuantities: true);
            var report = new SolverReport
            {
                Success = true,
                ElapsedMilliseconds = result.ElapsedMilliseconds,
                InputElements = result.ElementCount,
                GeneratedElements = result.EntityCount,
                HostedOpenings = elements.Count(e => e.HostElementId is not null),
                Actions = [.. result.Details, .. result.Files.Select(f => $"{Path.GetFileName(f)} — {new FileInfo(f).Length / 1024.0:N0} КБ")]
            };
            string files = string.Join(", ", result.Files.Select(Path.GetFileName));
            ChatMessages.Add(ChatMessageModel.Assistant(Loc.Format("ExportDone", tag, result.ElementCount, files), "CadExportService", report));

            _noticeVersion++;
            ErrorSeverity = InfoBarSeverity.Success;
            ErrorTitle = Loc.Format("ExportDoneTitle", tag);
            ErrorMessage = $"{path} · {result.Bytes / 1024.0:N0} KB · {result.ElapsedMilliseconds:N0} ms";
            ErrorCode = $"AIA-EXPORT-{tag}-OK";
            ErrorDetails = string.Join("\n", result.Details);
            ErrorActionLabel = Loc.Get("SolverLog");
            UpdateErrorReport();
            IsErrorOpen = true;
            AutoCloseNotice();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            App.WriteCrashLog("export", ex);
            ShowError(Loc.Get("ErrorExport"), ex.Message, $"AIA-EXPORT-{tag}-{ex.HResult:X8}", ex.ToString());
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>Применяет изменения из диалога настроек.</summary>
    public void ApplySettingsFromDialog()
    {
        var s = Settings.Current;
        if (SelectedThemeName != s.ThemeName) SelectedThemeName = s.ThemeName;
        if (Loc.Language != s.Language) Loc.Language = s.Language;
        if (ExperienceProfile != s.ExperienceProfile) ExperienceProfile = s.ExperienceProfile;
        if (Modules != s.EffectiveModules) Modules = s.EffectiveModules;
        ModelRegistry.ScanLocalModels();
        var model = ModelRegistry.Resolve(s.AiModel);
        if (SelectedAiModel != model.Id) SelectedAiModel = model.Id;
        OnPropertyChanged(nameof(AiModels));
        OnPropertyChanged(nameof(SelectedAiModelShort));
        RefreshSpecification();
    }

#if DEBUG
    /// <summary>Только Debug (AIA_DEBUG_AIJSON): прогон готового JSON-ответа через солвер без обращения к нейросети.</summary>
    public void DebugApplyJson(string json, bool append)
    {
        try
        {
            var context = _ai.CreateContext("debug", SelectedModel, append);
            OnAiSucceeded(_ai.ApplyResponseText(json, context, "Debug JSON"));
        }
        catch (AiProviderException ex)
        {
            ShowError(Loc.Get("ErrorAi"), ex.Message, ex.Code, ex.Details);
        }
    }
#endif

    /// <summary>Проект — в список «Недавние» стартового окна (временные файлы автотестов не запоминаются).</summary>
    private void RememberRecentProject(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) return;
        if (Settings.Current.RecentProjects.FirstOrDefault() is { } first && string.Equals(first, full, StringComparison.OrdinalIgnoreCase)) return;
        SaveSettingsSafe(s => s.RememberRecent(full));
    }

    // ───────────────────────────── Квалификация (онбординг) и модули ─────────────────────────────

    /// <summary>Квалификация: «Студент / Практикант», «Архитектор» или «Инженер-конструктор (ГИП)» — скрывает лишние панели.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEngineerUi), nameof(IsArchitectUi))]
    public partial UserExperienceProfile ExperienceProfile { get; set; } = UserExperienceProfile.StructuralEngineer;

    /// <summary>Полный профессиональный интерфейс: K_исп, продавливание, карта напряжений, нагрузки площадки.</summary>
    public bool IsEngineerUi => ExperienceProfile == UserExperienceProfile.StructuralEngineer;

    /// <summary>Инструменты архитектора: оси, каркас, секущий бокс, OSnap, инсоляция.</summary>
    public bool IsArchitectUi => ExperienceProfile != UserExperienceProfile.Beginner;

    partial void OnExperienceProfileChanged(UserExperienceProfile value)
    {
        XamlHelpers.CurrentProfile = value;
        // Скрытые профилем режимы выключаются, вкладки — возвращаются к «Инспектору».
        if (!IsEngineerUi)
        {
            IsStressMapEnabled = false;
            if (IsAnalysisTab) RightTab = 0;
        }
        if (!IsArchitectUi)
        {
            if (IsAxesTab) RightTab = 0;
            IsSectionBoxEnabled = false;
        }
        // Координационные оси: у «Эскизов и идей» скрыты вместе с кнопкой, с профиля «Архитектура» — снова видны.
        ShowGridAxes = IsArchitectUi;
        if (Settings.Current.ExperienceProfile != value) SaveSettingsSafe(s => s.ExperienceProfile = value);
        OnPropertyChanged(nameof(IsSolarUi));
    }

    /// <summary>Выбор квалификации пользователем (онбординг, AIA_DEBUG_PROFILE): профиль и его набор модулей по умолчанию.</summary>
    public void SelectProfile(UserExperienceProfile profile, WorkspaceModules? modules = null)
    {
        ExperienceProfile = profile;
        Modules = modules ?? WorkspaceModules.ForProfile(profile);
    }

    /// <summary>Включённые модули («Настройки → Модули и Плагины»), независимые от квалификации.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsolationModule), nameof(IsAnalysisModule), nameof(IsRebarModule), nameof(IsClashModule),
        nameof(IsQuantityModule), nameof(IsAnalysisTabUi), nameof(IsSolarUi))]
    public partial WorkspaceModules Modules { get; set; } = new();

    public bool IsInsolationModule => Modules.Insolation;

    public bool IsAnalysisModule => Modules.StructuralAnalysis;

    public bool IsRebarModule => Modules.Reinforcement3D;

    public bool IsClashModule => Modules.ClashInspector;

    public bool IsQuantityModule => Modules.QuantitySchedule;

    /// <summary>Вкладка «Анализ»: КЭ-расчёт и/или инспектор коллизий.</summary>
    public bool IsAnalysisTabUi => Modules.StructuralAnalysis || Modules.ClashInspector;

    /// <summary>Кнопка «Инсоляция и Тени»: солнце и тени — с квалификации «Архитектор», проверка СанПиН — модулем.</summary>
    public bool IsSolarUi => IsArchitectUi || Modules.Insolation;

    partial void OnModulesChanged(WorkspaceModules value)
    {
        // Выключенный модуль гасит свои режимы; свойства VM остаются доступными автотесту.
        if (!value.StructuralAnalysis) IsStressMapEnabled = false;
        if (!IsAnalysisTabUi && IsAnalysisTab) RightTab = 0;
        if (!value.Reinforcement3D) IsRebarView = false;
        if (!IsSolarUi) IsSolarEnabled = false;
        if (Settings.Current.EffectiveModules != value) SaveSettingsSafe(s => s.Modules = value);
    }

    private void SaveSettingsSafe(Action<AppSettings> mutate)
    {
        try
        {
            Settings.Update(mutate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(Loc.Get("ErrorSettings"), ex.Message, $"AIA-SETTINGS-{ex.HResult:X8}", ex.ToString());
        }
    }
}
