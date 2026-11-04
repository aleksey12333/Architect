using System.Collections.ObjectModel;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Economics;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Verification;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Волна 3 в ViewModel: основная локальная модель программы (<see cref="LocalModelDirectorService"/> — рейтинг установленных
/// моделей, автовыбор и ручной выбор, замер скорости), режим дока «Инженерный агент» (<see cref="BimEngineeringAgentService"/> с
/// цепочкой рассуждений и одним шагом журнала), веб-импорт прайса по ссылке и из прошлых смет с обязательным окном проверки
/// (<see cref="SmartPriceExtractorService"/>; подтверждённые расценки — база цен сессии, settings.json не меняется).
/// </summary>
public sealed partial class MainViewModel
{
    // ───────────────────────────── Основная локальная модель ─────────────────────────────

    public LocalModelDirectorService ModelDirector { get; } = new();

    private void InitializeWave3()
    {
        ModelRegistry.ModelsChanged += (_, _) => RunOnUi(RefreshModelDirector);
        ModelDirector.Changed += (_, _) => RunOnUi(RefreshPrimaryModelTexts);
        if (Settings.Current.PrimaryLocalModelId is { Length: > 0 } manual) ModelDirector.SetManual(manual);
        RefreshModelDirector();
        Loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalizationService.Language)) RefreshPrimaryModelTexts();
        };
    }

    /// <summary>Пересчёт рейтинга: модели сервера локального ядра + встроенные GGUF (файл на месте).</summary>
    private void RefreshModelDirector()
    {
        string? dir = null;
        try
        {
            dir = Settings.ResolveLocalModelsDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Папка моделей недоступна — встроенные модели считаются недоступными.
        }
        var gguf = ModelRegistry.LocalModels.Select(m => (m, dir is not null && File.Exists(Path.Combine(dir, m.ModelId))));
        var ollama = _modelDirectorOverride ?? ModelRegistry.OllamaModels;
        // Скорость замеряется только по действию пользователя («Автовыбор», «Замерить скорость»): замер загружает веса в память.
        ModelDirector.Refresh(ollama, gguf, OllamaProcessManager.TotalMemoryGb);
    }

    /// <summary>Список моделей директора для съёмки и автотеста (null — модели сервера локального ядра).</summary>
    private IReadOnlyList<AiModelOption>? _modelDirectorOverride;

    internal void OverrideDirectorModels(IReadOnlyList<AiModelOption>? models, IReadOnlyDictionary<string, ModelSpeedSample>? speed = null)
    {
        _modelDirectorOverride = models;
        RefreshModelDirector();
        if (speed is not null) foreach (var (id, s) in speed) ModelDirector.RecordSpeed(id, s);
    }

    private static bool IsSelfTestRun => Environment.GetEnvironmentVariable("AIA_DEBUG_SELFTEST") is { Length: > 0 };

    [ObservableProperty] public partial string PrimaryModelTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrimaryModelRating { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrimaryModelDetail { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrimaryModelSpeed { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrimaryModelColorHex { get; set; } = "#8E8E93";
    [ObservableProperty] public partial bool HasPrimaryModel { get; set; }
    [ObservableProperty] public partial bool IsMeasuringSpeed { get; set; }

    /// <summary>Короткая подпись чипа вне режима агента (рядом с чипами вложений): рейтинг основной модели.</summary>
    [ObservableProperty] public partial string PrimaryModelRatingShort { get; set; } = "—";

    /// <summary>Подпись чипа в доке: «Локальное ядро · qwen2.5-coder:14b · 86».</summary>
    [ObservableProperty] public partial string PrimaryModelChip { get; set; } = string.Empty;

    private void RefreshPrimaryModelTexts()
    {
        var c = Loc.Culture;
        var primary = ModelDirector.Primary;
        HasPrimaryModel = primary is not null;
        if (primary is null)
        {
            PrimaryModelTitle = Loc.Get("W3CoreNone");
            PrimaryModelRating = string.Empty;
            PrimaryModelDetail = Loc.Get("W3CoreNoneHint");
            PrimaryModelSpeed = string.Empty;
            PrimaryModelColorHex = "#8E8E93";
            PrimaryModelChip = Loc.Get("W3CoreTitle") + " · " + Loc.Get("W3CoreNone");
            PrimaryModelRatingShort = "—";
            return;
        }
        PrimaryModelTitle = primary.Option.ModelId.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? primary.Option.DisplayName : primary.Option.ModelId;
        PrimaryModelRating = primary.EngineeringScore.ToString("0", c) + " / 100";
        PrimaryModelDetail = $"{primary.Family} · {primary.ParameterBillions.ToString("0.#", c)}B · {primary.Specialization} · " +
                             (ModelDirector.IsManual ? Loc.Get("W3CoreManual") : Loc.Get("W3CoreAutoState"));
        PrimaryModelSpeed = primary.Speed is { } s
            ? Loc.Format("W3CoreSpeedValue", s.TokensPerSecond.ToString("0", c), s.LatencyMs.ToString("N0", c))
            : Loc.Get("W3CoreSpeedUnknown");
        PrimaryModelRatingShort = primary.EngineeringScore.ToString("0", c);
        PrimaryModelColorHex = primary.EngineeringScore >= 75 ? "#30D158" : primary.EngineeringScore >= 55 ? "#0A84FF" : "#FF9F0A";
        PrimaryModelChip = $"{Loc.Get("W3CoreTitle")} · {(PrimaryModelTitle.Length > 22 ? PrimaryModelTitle[..22] + "…" : PrimaryModelTitle)} · {primary.EngineeringScore.ToString("0", c)}" +
                           (primary.Speed is { } sp ? $" · {sp.TokensPerSecond.ToString("0", c)} ток/с" : string.Empty);
    }

    /// <summary>«Автовыбор лучшей модели»: снять ручной выбор (настройки — только если выбор был сохранён).</summary>
    [RelayCommand]
    private void AutoSelectPrimaryModel()
    {
        var best = ModelDirector.AutoSelect();
        if (Settings.Current.PrimaryLocalModelId is not null) SaveSettingsSafe(s => s.PrimaryLocalModelId = null);
        if (best is null) return;
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("W3CoreAutoDone", best.Option.ModelId, best.EngineeringScore.ToString("0", Loc.Culture))));
        if (best.Option.IsOllama && best.Speed is null) _ = MeasurePrimarySpeedAsync(best.Option);
    }

    /// <summary>Ручной выбор основной модели (сохраняется в настройках).</summary>
    public void SetPrimaryModel(string id)
    {
        ModelDirector.SetManual(id);
        if (Settings.Current.PrimaryLocalModelId != id) SaveSettingsSafe(s => s.PrimaryLocalModelId = id);
    }

    [RelayCommand]
    private async Task MeasurePrimarySpeed()
    {
        if (ModelDirector.Primary is { } primary) await MeasurePrimarySpeedAsync(primary.Option);
    }

    private async Task MeasurePrimarySpeedAsync(AiModelOption model)
    {
        if (IsMeasuringSpeed || model.IsLocal) return;
        IsMeasuringSpeed = true;
        try
        {
            await ModelDirector.MeasureAsync(_ai.ProviderFor(model), model);
        }
        finally
        {
            IsMeasuringSpeed = false;
        }
    }

    // ───────────────────────────── Режим «Инженерный агент» ─────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingleAgentMode), nameof(ChatPlaceholder))]
    public partial bool IsAgentMode { get; set; }

    /// <summary>Режим Совета КБ до входа в режим агента (вернуть без записи настроек — <see cref="RestoreCouncilAfterAgent"/>).</summary>
    private bool _councilBeforeAgent;

    partial void OnIsAgentModeChanged(bool value)
    {
        if (value)
        {
            _councilBeforeAgent = IsCouncilMode;
            if (IsCouncilMode) IsCouncilMode = false;
            IsAgentPanelOpen = true;
        }
    }

    /// <summary>Вернуть режим Совета КБ, бывший до режима агента (состояние сессии, settings.json не пишется).</summary>
    internal void RestoreCouncilAfterAgent()
    {
        if (IsAgentMode) IsAgentMode = false;
        _syncingCouncil = true;
        IsCouncilMode = _councilBeforeAgent;
        _syncingCouncil = false;
    }

    /// <summary>Состояние цен сессии (автотест: вернуть после проверки применения).</summary>
    internal (MaterialPriceCatalog? Active, string? SessionActive, List<MaterialPriceCatalog> Session) SnapshotPricingState() =>
        (ActivePriceCatalog, _sessionActiveCatalog, _sessionCatalogs.ToList());

    internal void RestorePricingState((MaterialPriceCatalog? Active, string? SessionActive, List<MaterialPriceCatalog> Session) state)
    {
        _sessionCatalogs.Clear();
        _sessionCatalogs.AddRange(state.Session);
        _sessionActiveCatalog = state.SessionActive;
        PricingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Панель агента в боковом доке: цепочка рассуждений, вызовы инструментов, отчёт последнего задания.</summary>
    [ObservableProperty]
    public partial bool IsAgentPanelOpen { get; set; }

    partial void OnIsAgentPanelOpenChanged(bool value)
    {
        if (value) IsCouncilDockCollapsed = false;
        RefreshCouncilDock();
        OnPropertyChanged(nameof(IsAgentPanelVisible));
        OnPropertyChanged(nameof(IsCouncilTimelineVisible));
    }

    /// <summary>Панель агента на месте ленты Коллоквиума (лист согласования — приоритетнее).</summary>
    public bool IsAgentPanelVisible => IsAgentPanelOpen && Review is null;

    /// <summary>Задания агента сессии (новые — последними).</summary>
    public ObservableCollection<AgentRunResult> AgentRuns { get; } = [];

    public AgentRunResult? LastAgentRun => AgentRuns.Count > 0 ? AgentRuns[^1] : null;

    /// <summary>Задание агента завершено — главное окно перестраивает панель агента.</summary>
    public event EventHandler<AgentRunResult>? AgentRunCompleted;

    /// <summary>
    /// Окно подтверждения расценок (задаёт главное окно): запрос источников → пакет, проверенный пользователем; null — отмена.
    /// </summary>
    public Func<PriceImportRequest, Task<PriceExtractionBatch?>>? PriceVerifier { get; set; }

    [RelayCommand]
    private void CloseAgentPanel() => IsAgentPanelOpen = false;

    /// <summary>Задание агента из панели команд (режим «Инженерный агент»).</summary>
    public async Task<AgentRunResult?> RunAgentAsync(string task, IReadOnlyList<AgentToolCall>? plan = null)
    {
        if (IsBusy) return null;
        IsBusy = true;
        BusyText = Loc.Get("W3AgentBusy");
        IsAgentPanelOpen = true;
        try
        {
            var primary = ModelDirector.Primary;
            var ctx = new AgentContext
            {
                Scene = Scene,
                Loads = Settings.Current.StructuralLoads,
                Rates = new GateCostRates(Settings.Current.ConcretePricePerM3, Settings.Current.RebarPricePerTon),
                Selection = Scene.SelectedElements.ToList(),
                PriceHost = AgentPriceHostAsync,
                ModelPlanner = primary is { Option.IsLocal: false } p && !IsSelfTestRun ? (system, user, ct) => PlanWithModelAsync(p.Option, system, user, ct) : null,
                PlannerName = primary is null ? string.Empty : $"{Loc.Get("W3CoreModel").ToLower(Loc.Culture)} {primary.Option.ModelId}"
            };
            var result = await BimEngineeringAgentService.RunAsync(task, ctx, plan);
            AgentRuns.Add(result);
            OnPropertyChanged(nameof(LastAgentRun));
            var message = ChatMessageModel.Assistant(string.Join("\n", result.Report), Loc.Get("W3AgentTitle"));
            message.Trace = result.Trace;
            message.ToolCalls = result.ToolCalls;
            message.Badge = result.Trace.Summary;
            message.BadgeColorHex = result.Trace.IsVerified ? "#30D158" : "#FF9F0A";
            ChatMessages.Add(message);
            var affected = result.ToolCalls.SelectMany(c => c.AffectedIds).Distinct().Select(Scene.FindById).OfType<ArchitecturalElement>().ToList();
            if (affected.Count > 0) Scene.SetSelection(affected, affected[0]);
            SyncInspector();
            AgentRunCompleted?.Invoke(this, result);
            return result;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>План основной локальной моделью (сервер локального ядра — потоковый JSON).</summary>
    private async Task<string> PlanWithModelAsync(AiModelOption model, string system, string user, CancellationToken ct)
    {
        var response = await _ai.ProviderFor(model).GenerateAsync(new AiRequest { Model = model, SystemPrompt = system, UserPrompt = user, Temperature = 0.1, MaxOutputTokens = 1200 }, ct);
        return response.Text;
    }

    /// <summary>Отмена действий агента: его шаг журнала — последний, один Undo.</summary>
    [RelayCommand]
    private void UndoAgent()
    {
        if (LastAgentRun is not { Changed: true } || !Scene.CanUndo) return;
        Scene.Undo();
        SyncInspector();
        ChatMessages.Add(ChatMessageModel.System(Loc.Get("W3AgentUndone")));
    }

    /// <summary>Инструмент pull_supplier_prices: источники вызова → окно проверки → применение только по «Да, всё верно».</summary>
    private async Task<string?> AgentPriceHostAsync(AgentToolCall call, CancellationToken ct)
    {
        var request = new PriceImportRequest(
            call.Argument("url").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            call.Argument("path").Split('|', StringSplitOptions.RemoveEmptyEntries),
            call.Argument("text"),
            call.Number("index_percent") ?? 0);
        var batch = PriceVerifier is { } verifier ? await verifier(request) : null;
        if (batch is null || !batch.Confirmed) return null;
        return Loc.Format("W3PriceApplied", batch.Checked.Count(), ActivePriceCatalog?.Name ?? string.Empty, LastPriceApply.Before.ToString("N0", Loc.Culture),
            LastPriceApply.After.ToString("N0", Loc.Culture));
    }

    // ───────────────────────────── Веб-импорт прайса ─────────────────────────────

    /// <summary>«Подтянуть цены по ссылке / из сметы…» в окне ведомости — главное окно открывает окно источников и проверки.</summary>
    public event EventHandler? PricePullRequested;

    [RelayCommand]
    private void PullPrices() => PricePullRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Итог последнего применения: стоимость каркаса до и после.</summary>
    public (double Before, double After) LastPriceApply { get; private set; }

    /// <summary>
    /// Извлечение расценок из всех источников запроса: страницы магазинов (загрузка и разбор), прошлые проекты .aia, сметы и прайсы
    /// .csv / .json / .txt, вставленный текст. Ошибки источников — предупреждениями пакета (остальные источники разбираются).
    /// </summary>
    public async Task<PriceExtractionBatch> ExtractPricesAsync(PriceImportRequest request, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        var items = new List<ExtractedPriceItem>();
        var sources = new List<string>();
        var warnings = new List<string>();
        foreach (var raw in request.Urls)
        {
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url))
            {
                warnings.Add(Loc.Format("W3PriceError", raw, "ссылка не распознана"));
                continue;
            }
            try
            {
                var found = await SmartPriceExtractorService.ExtractFromUrlAsync(url, ct, handler);
                items.AddRange(found);
                sources.Add($"{url.Host}: позиций {found.Count}");
                if (found.Count == 0) warnings.Add(Loc.Format("W3PriceError", url.Host, "на странице не найдено расценок материалов проекта (страница может строиться скриптом — вставьте её текст)"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
            {
                warnings.Add(Loc.Format("W3PriceError", url.Host, ex.Message));
            }
        }
        foreach (var path in request.Paths)
        {
            try
            {
                string name = Path.GetFileName(path);
                var found = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".aia" => SmartPriceExtractorService.ExtractFromProject(await File.ReadAllTextAsync(path, ct), name),
                    ".txt" => SmartPriceExtractorService.ExtractFromText(await File.ReadAllTextAsync(path, ct), name),
                    _ => SmartPriceExtractorService.ExtractFromCatalog(MaterialPriceCatalog.Load(path), name)
                };
                items.AddRange(found);
                sources.Add($"{name}: позиций {found.Count}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                warnings.Add(Loc.Format("W3PriceError", Path.GetFileName(path), ex.Message));
            }
        }
        if (!string.IsNullOrWhiteSpace(request.Text))
        {
            var found = SmartPriceExtractorService.ExtractFromText(request.Text);
            items.AddRange(found);
            sources.Add($"вставленный текст: позиций {found.Count}");
        }
        var batch = SmartPriceExtractorService.BuildBatch(items, CurrentUnitPrice, request.IndexPercent, sources);
        batch.Warnings.InsertRange(0, warnings);
        return batch;
    }

    /// <summary>Текущая цена позиции сметы проекта по коду материала (смета → база цен → библиотека материалов); null — позиции нет.</summary>
    public double? CurrentUnitPrice(string code)
    {
        string key = PriceCodes.Normalize(code);
        var estimate = BuildPricedEstimate(BuildQuantitySchedule());
        if (estimate.Lines.FirstOrDefault(l => PriceCodes.Normalize(l.Code) == key) is { } line) return line.UnitPriceRub;
        if (ActivePriceCatalog?.Find(key) is { } item) return item.UnitPriceRub;
        return MaterialLibrary.Find(Settings.Current.StructuralLoads.Materials, key)?.UnitPriceRub;
    }

    /// <summary>Итог стоимости каркаса с базой цен (null — текущая база): для сводки «Было → Станет» окна проверки.</summary>
    public double FrameTotalWith(MaterialPriceCatalog? catalog)
    {
        var schedule = BuildQuantitySchedule();
        return CostEstimateService.Build(schedule, Settings.Current.StructuralLoads.Concrete, ProjectRates, catalog ?? ActivePriceCatalog, EstimateOverrides,
            Settings.Current.Engineering.IncludeSteelAndGlazing).FrameTotal;
    }

    /// <summary>Стоимость каркаса, если применить отмеченные позиции пакета (пакет не помечается подтверждённым).</summary>
    public double FrameTotalAfter(PriceExtractionBatch batch) => FrameTotalWith(SmartPriceExtractorService.BuildCatalog(batch, ActivePriceCatalog));

    /// <summary>
    /// «Да, всё верно — применить в смету»: отмеченные позиции — новая база цен сессии (копия текущей + подтверждённые цены с
    /// поставщиком и основанием), выбирается активной; settings.json не меняется, смета сохраняется в проект при записи .aia.
    /// </summary>
    public MaterialPriceCatalog ApplyPriceBatch(PriceExtractionBatch batch)
    {
        double before = FrameTotalWith(null);
        var catalog = SmartPriceExtractorService.ConfirmAndApply(batch, ActivePriceCatalog);
        _sessionCatalogs.RemoveAll(c => c.Name == catalog.Name);
        _sessionCatalogs.Add(catalog);
        _sessionActiveCatalog = catalog.Name;
        if (Scene.Elements.Count > 0) Scene.IsDirty = true;
        PricingChanged?.Invoke(this, EventArgs.Empty);
        double after = FrameTotalWith(null);
        LastPriceApply = (before, after);
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("W3PriceApplied", batch.Checked.Count(), catalog.Name, before.ToString("N0", Loc.Culture), after.ToString("N0", Loc.Culture))));
        return catalog;
    }
}
