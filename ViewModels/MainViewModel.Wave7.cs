using System.Globalization;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Models.Layouts;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.Services.Layouts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>Вид главной области раздела «3. Планировки и ИОС»: мульти-план пакетов этажей или подвал и вертикальная проверка зон.</summary>
public enum LayoutViewKind { MultiPlan, Basement }

/// <summary>
/// Волна 7 в модели представления: три раздела (1. Генплан и ГПЗУ · 2. Архитектура и КР · 3. Планировки и ИОС) с одним правым доком,
/// видимость инструментов по разделу, глубокий 4-этапный анализ документов с индикатором этапов, пакетная планировка этажей,
/// мульти-план, проверка и согласование стояков и мокрых / газовых зон (каждая правка планировки — один шаг журнала).
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly CultureInfo W7Ru = CultureInfo.GetCultureInfo("ru-RU");

    public event EventHandler? LayoutChanged;

    private void InitializeWave7()
    {
        Scene.BuildingLayoutChanged += (_, _) => RunOnUi(() =>
        {
            RefreshStackReport();
            OnPropertyChanged(nameof(HasBuildingLayout));
            OnPropertyChanged(nameof(IsMultiPlanVisible));
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        });
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ExperienceProfile))
            {
                OnPropertyChanged(nameof(IsArchitectBuildingUi));
                OnPropertyChanged(nameof(IsStructuralModeAvailable));
            }
            if (e.PropertyName is nameof(Modules))
            {
                OnPropertyChanged(nameof(IsAnalysisBuildingUi));
                OnPropertyChanged(nameof(IsRebarBuildingUi));
                OnPropertyChanged(nameof(IsQuantityBuildingUi));
                OnPropertyChanged(nameof(IsStructuralModeAvailable));
            }
            if (e.PropertyName is nameof(IsWalkthrough)) OnPropertyChanged(nameof(IsMultiPlanVisible));
            if (e.PropertyName is nameof(IsAgentMode) or nameof(IsCouncilMode) or nameof(IsSingleAgentMode))
            {
                OnPropertyChanged(nameof(AssistantModeTitle));
                OnPropertyChanged(nameof(AssistantModeGlyph));
            }
        };
        Loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalizationService.Language)) OnPropertyChanged(nameof(AssistantModeTitle));
        };
    }

    // ═════════════════════════════ Режим ассистента (одна кнопка вместо чипов) ═════════════════════════════

    public string AssistantModeTitle => IsAgentMode ? Loc.Get("W3AgentModeShort") : IsCouncilMode ? Loc.Get("CouncilMulti") : Loc.Get("W7ModeDesigner");

    public string AssistantModeGlyph => IsAgentMode ? "\uE90F" : IsCouncilMode ? "\uE716" : "\uE8BD";

    // ═════════════════════════════ Разделы: видимость инструментов ═════════════════════════════

    public bool IsSiteOrBuilding => ActiveEnvironment != DeveloperEnvironment.Interiors;
    public bool IsBuildingOrLayouts => ActiveEnvironment != DeveloperEnvironment.Site;

    /// <summary>Инспектор — только в разделе «2. Архитектура и КР» и не в режиме «Конструкции» (тогда док занимает панель КЖ/КМ).</summary>
    public bool IsInspectorDockVisible => ActiveEnvironment == DeveloperEnvironment.Building && !IsStructuralMode;

    public bool IsArchitectBuildingUi => IsArchitectUi && ActiveEnvironment == DeveloperEnvironment.Building;
    public bool IsAnalysisBuildingUi => IsAnalysisModule && ActiveEnvironment == DeveloperEnvironment.Building;
    public bool IsRebarBuildingUi => IsRebarModule && ActiveEnvironment == DeveloperEnvironment.Building;
    public bool IsQuantityBuildingUi => IsQuantityModule && ActiveEnvironment == DeveloperEnvironment.Building;

    /// <summary>Вкладка правого дока раздела 1: 0 — Паспорт ЗУ, 1 — Регламент и ЗОУИТ, 2 — Сети ИГДИ.</summary>
    [ObservableProperty]
    public partial int SiteDockTab { get; set; }

    /// <summary>Вкладка правого дока раздела 3: 0 — Пакеты этажей, 1 — Квартиры и МОП, 2 — Инженерные стояки.</summary>
    [ObservableProperty]
    public partial int LayoutDockTab { get; set; }

    partial void OnSiteDockTabChanged(int value) => EnvironmentChanged?.Invoke(this, EventArgs.Empty);
    partial void OnLayoutDockTabChanged(int value) => EnvironmentChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Редактирование вершин участка (пины точек во вьюпорте) — переключатель палитры раздела 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSiteEditing))]
    public partial bool IsSiteVertexEditEnabled { get; set; } = true;

    [RelayCommand]
    private void SelectSection(string code)
    {
        ActiveEnvironment = WorkspaceEnvironmentService.Parse(code, ActiveEnvironment);
    }

    // ═════════════════════════════ Раздел 3: планировки ═════════════════════════════

    public BuildingLayoutPlan? BuildingLayout => Scene.BuildingLayout;
    public bool HasBuildingLayout => Scene.BuildingLayout is not null;
    public StackReport? StackReport { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiPlanVisible))]
    public partial bool IsMultiPlanOpen { get; set; }

    /// <summary>Мульти-план в главной области: только в разделе 3 при планировке.</summary>
    public bool IsMultiPlanVisible => IsMultiPlanOpen && ActiveEnvironment == DeveloperEnvironment.Interiors && Scene.BuildingLayout is not null && !IsWalkthrough;

    [ObservableProperty]
    public partial int MultiPlanCount { get; set; } = 4;

    [ObservableProperty]
    public partial LayoutViewKind LayoutView { get; set; }

    partial void OnMultiPlanCountChanged(int value) => LayoutChanged?.Invoke(this, EventArgs.Empty);
    partial void OnLayoutViewChanged(LayoutViewKind value) => LayoutChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Нарезка квартир застройщика (Квартиры и МОП).</summary>
    [ObservableProperty]
    public partial ApartmentMix LayoutMix { get; set; } = ApartmentMix.Default;

    [ObservableProperty]
    public partial CoreStairForm? LayoutStairForm { get; set; }

    public StackReport? RefreshStackReport()
    {
        StackReport = Scene.BuildingLayout is { } plan ? MepStackValidatorService.Validate(plan) : null;
        return StackReport;
    }

    /// <summary>
    /// Пакетная планировка здания: исходные данные из 3D-модели (контур, этажность, оси, подвал) и участка (вводы сетей, газ),
    /// генерация ядра, квартир, подвала и стояков — в документ одним шагом журнала. groupId null — самое высокое здание.
    /// </summary>
    public BuildingLayoutPlan? GenerateBuildingLayout(string? groupId = null)
    {
        var request = FloorTierLayoutService.RequestFor(Scene.Elements.ToList(), Scene.BuildingGroups.ToList(), groupId ?? Scene.BuildingLayout?.BuildingGroupId,
            Scene.SitePlan, Math.Clamp(FloorPackageSize, 1, 50), LayoutMix, LayoutStairForm);
        if (request is null)
        {
            ShowNotice(Loc.Get("W7LayoutsTitle"), Loc.Get("W7NoBuilding"), "AIA-W7-LAYOUT");
            return null;
        }
        var plan = DeveloperLayoutGenerator.Generate(request);
        FloorTierLayoutService.Apply(Scene, plan, Loc.Format("W7UndoGenerate", plan.BuildingName, plan.Levels));
        RefreshStackReport();
        var tep = plan.Tep();
        ChatMessages.Add(ChatMessageModel.Assistant(Loc.Format("W7LayoutDone", plan.BuildingName, plan.Levels, plan.Packages.Count, tep.Apartments,
            tep.ApartmentArea.ToString("N0", W7Ru), plan.Core.Lifts, plan.Core.Stairs) + (StackReport is { Passed: false } s ? "\n" + Loc.Format("W7StackIssues", s.Critical) : string.Empty),
            Loc.Get("W5Engine")));
        return plan;
    }

    /// <summary>Жилой корпус 50 эт. (42 × 18 м, подвал) — на участок или свободное место, затем пакетная планировка; два шага журнала.</summary>
    public BuildingLayoutPlan? PlaceHighRiseWithLayout()
    {
        string? group = AddStructureToSite(SiteStructureKind.HighRise, 50);
        return group is null ? null : GenerateBuildingLayout(group);
    }

    /// <summary>Правка пакета (тип А / Б, зеркальная планировка) — для всех этажей пакета одним шагом журнала.</summary>
    public bool EditLayoutPackage(string packageId, string action)
    {
        if (Scene.BuildingLayout is not { } plan || plan.Package(packageId) is not { } p) return false;
        var next = action switch
        {
            "mirror" => FloorTierLayoutService.Mirror(plan, packageId),
            "А" or "Б" => FloorTierLayoutService.SetPackageCode(plan, packageId, action),
            _ => plan
        };
        if (ReferenceEquals(next, plan)) return false;
        FloorTierLayoutService.Apply(Scene, next, Loc.Format(action == "mirror" ? "W7UndoMirror" : "W7UndoCode", p.RangeText, action));
        return true;
    }

    /// <summary>«Согласовать стояки и мокрые зоны»: выравнивание по вертикали одним шагом журнала; 0 — исправлять нечего.</summary>
    public int AlignStacks()
    {
        if (Scene.BuildingLayout is not { } plan) return 0;
        var before = MepStackValidatorService.Validate(plan);
        var (next, changes) = MepStackValidatorService.Align(plan);
        if (changes == 0)
        {
            ShowNotice(Loc.Get("W7StacksTitle"), Loc.Get("W7StacksNothing"), "AIA-W7-STACKS");
            return 0;
        }
        FloorTierLayoutService.Apply(Scene, next, Loc.Format("W7UndoAlign", changes));
        var after = RefreshStackReport();
        ChatMessages.Add(ChatMessageModel.Assistant(Loc.Format("W7StacksDone", before.Critical, after?.Critical ?? 0, changes), Loc.Get("W5Engine")));
        return changes;
    }

    [RelayCommand]
    private void ToggleMultiPlan()
    {
        if (Scene.BuildingLayout is null && GenerateBuildingLayout() is null) return;
        LayoutView = LayoutViewKind.MultiPlan;
        IsMultiPlanOpen = !IsMultiPlanOpen || LayoutView != LayoutViewKind.MultiPlan;
    }

    [RelayCommand]
    private void ShowBasementView()
    {
        if (Scene.BuildingLayout is null && GenerateBuildingLayout() is null) return;
        LayoutView = LayoutViewKind.Basement;
        IsMultiPlanOpen = true;
        LayoutDockTab = 2;
    }

    // ═════════════════════════════ Глубокий анализ документов ═════════════════════════════

    public ExhaustiveAnalysisReport? LastDocumentAnalysis { get; private set; }

    [ObservableProperty]
    public partial string AnalysisStageText { get; set; } = string.Empty;

    private IProgress<AnalysisProgress> AnalysisProgressSink() => new Progress<AnalysisProgress>(p =>
    {
        AnalysisStageText = Loc.Format("W7StageProgress", p.Stage, p.Title, p.Detail);
        BusyText = AnalysisStageText;
        BusyPercent = Math.Round(p.Fraction * 100);
    });

    /// <summary>
    /// Глубокий анализ файла (ГПЗУ, ТУ, пояснительная записка): декомпозиция → блоки А–Г (ядро + модель, рассуждение «high») →
    /// перекрёстная сверка → нормативы. Пункты — в окно «Градостроительные ограничения и ЗОУИТ» (ничего не применяется само).
    /// </summary>
    public async Task<ExhaustiveAnalysisReport?> AnalyzeDocumentAsync(string path, string? modelKey = null, bool openDialog = true, CancellationToken ct = default)
    {
        IsBusy = true;
        BusyPercent = 0;
        try
        {
            var model = ResolveConstraintModel(modelKey ?? DefaultConstraintModelKey, "high");
            var report = await ExhaustiveDocumentAnalyzerService.AnalyzeFileAsync(path, GpzuTopo, model, AnalysisProgressSink(), renderDrawings: !IsSelfTestRun, ct);
            ConstraintSource = DocumentTextOf(report.Structure);
            Publish(report, model, openDialog);
            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or System.Xml.XmlException)
        {
            ShowError(Loc.Get("W6ConstraintsTitle"), ex.Message, $"AIA-W7-READ-{ex.HResult:X8}", ex.ToString());
            return null;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            BusyPercent = -1;
        }
    }

    /// <summary>Тот же анализ для текста (вставка, нестандартный документ).</summary>
    public async Task<ExhaustiveAnalysisReport> AnalyzeDocumentTextAsync(DocumentText doc, string? modelKey, bool openDialog = true, CancellationToken ct = default)
    {
        ConstraintSource = doc;
        var model = ResolveConstraintModel(modelKey ?? DefaultConstraintModelKey, "high");
        var report = await ExhaustiveDocumentAnalyzerService.AnalyzeTextAsync(doc, model, GpzuTopo, AnalysisProgressSink(), ct);
        Publish(report, model, openDialog);
        return report;
    }

    private void Publish(ExhaustiveAnalysisReport report, ConstraintModel? model, bool openDialog)
    {
        LastDocumentAnalysis = report;
        LastConstraintExtraction = report.ToExtraction();
        ChatMessages.Add(ChatMessageModel.Assistant(Loc.Format("W7AnalysisDone", report.Structure.FileName, report.Structure.PageCount, report.Facts.Count, report.Items.Count,
            report.Elapsed.TotalSeconds.ToString("0.0", W7Ru)) + "\n" + string.Join("\n", report.Log), model?.Title ?? Loc.Get("W5Engine")));
        if (openDialog) ConstraintDialogRequested?.Invoke(this, ConstraintDialogMode.Document);
    }

    private static DocumentText DocumentTextOf(DocumentStructure s)
    {
        var text = new StringBuilder();
        foreach (var (page, lines) in s.Pages) text.Append("=== СТРАНИЦА ").Append(page.ToString(W7Ru)).Append(" ===\n").Append(string.Join("\n", lines)).Append('\n');
        var doc = DeepDocumentConstraintService.FromText(text.ToString(), s.FileName);
        return doc;
    }
}
