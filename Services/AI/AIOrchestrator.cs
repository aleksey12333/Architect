using AIArchitectStudio.Models;
using AIArchitectStudio.Services.Photo;

namespace AIArchitectStudio.Services.AI;

/// <summary>Всё, что нужно для одного запроса к ИИ: задача, модель, режим сцены, выделение и изображение.</summary>
public sealed class AiRequestContext
{
    public required string Task { get; init; }
    public required AiModelOption Model { get; init; }
    public SolverMode Mode { get; init; }
    public string? SelectedElementId { get; init; }
    public SceneContext Scene { get; init; } = SceneContext.Empty;
    public VisionMode Vision { get; init; }

    /// <summary>Изображения для модели (одно фото/план или выбранные листы PDF в высоком разрешении).</summary>
    public IReadOnlyList<AiImage> Images { get; init; } = [];

    /// <summary>Картинка для буфера обмена веб-чата: единственное изображение или сводный лист из выбранных страниц.</summary>
    public byte[]? Image { get; init; }

    public string? ImageName { get; init; }

    /// <summary>Тип материала (КЖ/КМ, АР/БТИ, эскиз/фото или автоопределение) для адаптивного промпта.</summary>
    public BlueprintKind Kind { get; init; }

    /// <summary>Описание выбранных листов PDF для пользовательской части промпта.</summary>
    public string? SheetsDescription { get; init; }

    /// <summary>
    /// Оптический паспорт снимка (волна 4): ответ любой модели сверяется с ним и доводится (<see cref="PhotoFidelityEnforcer"/>),
    /// затем каркас проходит двухпроходную проверку. null — обычный запрос.
    /// </summary>
    public PhotoArchitectureDescriptor? Photo { get; init; }

    /// <summary>Ручная доводка подъёма по фото (профиль, этажность, кривизна, остекление, покрытие).</summary>
    public PhotoLiftOptions? PhotoOptions { get; init; }

    /// <summary>Нагрузки для проверки каркаса поднятого по фото здания (null — по умолчанию).</summary>
    public StructuralLoadSettings? PhotoLoads { get; init; }

    /// <summary>Тот же контекст с оптическим паспортом снимка.</summary>
    public AiRequestContext WithPhoto(PhotoArchitectureDescriptor photo, PhotoLiftOptions? options, StructuralLoadSettings? loads) => new()
    {
        Task = Task, Model = Model, Mode = Mode, SelectedElementId = SelectedElementId, Scene = Scene, Vision = Vision, Images = Images, Image = Image,
        ImageName = ImageName, Kind = Kind, SheetsDescription = SheetsDescription, Photo = photo, PhotoOptions = options, PhotoLoads = loads
    };

    /// <summary>Тот же контекст для другой модели (роли Совета КБ выполняются разными моделями).</summary>
    public AiRequestContext WithModel(AiModelOption model) => new()
    {
        Photo = Photo,
        PhotoOptions = PhotoOptions,
        PhotoLoads = PhotoLoads,
        Task = Task,
        Model = model,
        Mode = Mode,
        SelectedElementId = SelectedElementId,
        Scene = Scene,
        Vision = Vision,
        Images = Images,
        Image = Image,
        ImageName = ImageName,
        Kind = Kind,
        SheetsDescription = SheetsDescription
    };
}

/// <summary>Итог запроса: комментарий ИИ, отчёт солвера и сырой JSON для журнала.</summary>
public sealed record AiRunResult(string Message, SolverReport Report, string Source, string RawJson, SolverMode Mode);

/// <summary>Ответ ИИ, разобранный и развёрнутый C#-солвером, но ещё не применённый к сцене.</summary>
public sealed record SolvedResponse(AiSceneDocument Document, SolverResult Result, SolverMode Mode)
{
    public AiRunResult ToRunResult(string source)
    {
        var report = Result.Report;
        if (!report.Actions.Any(a => a.StartsWith("Источник:", StringComparison.Ordinal)))
            report.Actions.Insert(0, $"Источник: {source} · режим: {AIOrchestrator.ModeTitle(Mode)}");
        string message = string.IsNullOrWhiteSpace(Document.Message)
            ? Mode == SolverMode.Modify ? "Изменения применены к выбранному элементу." : $"Построено элементов: {report.GeneratedElements}."
            : Document.Message.Trim();
        return new AiRunResult(message, report, source, Document.RawJson, Mode);
    }
}

/// <summary>
/// Дирижёр ИИ: собирает промпт (SystemPrompts + контекст выделения), вызывает нужного провайдера
/// (локальный Qwen / Gemini / ChatGPT / Claude / OpenRouter), извлекает JSON, прогоняет его через
/// ParametricSolverService и применяет к сцене одним шагом Undo.
/// </summary>
public sealed class AIOrchestrator(
    SceneStateService scene,
    ParametricSolverService solver,
    LocalAIProvider local,
    GeminiAIProvider gemini,
    OpenAiCompatibleProvider openAi,
    ClaudeAIProvider claude,
    OllamaAIProvider ollama)
{
    /// <summary>Снимок контекста: режим определяется выделением и переключателем «Новое | + К участку».</summary>
    public AiRequestContext CreateContext(string task, AiModelOption model, bool appendToSite,
        VisionMode vision = VisionMode.None, IReadOnlyList<AiImage>? images = null, string? imageName = null,
        BlueprintKind kind = BlueprintKind.Auto, string? sheetsDescription = null, byte[]? clipboardImage = null)
    {
        images ??= [];
        var selected = scene.SelectedElement;
        var mode = selected is not null && vision == VisionMode.None
            ? SolverMode.Modify
            : appendToSite && scene.Elements.Count > 0 ? SolverMode.AppendToSite : SolverMode.NewScene;

        SceneContext context;
        if (mode == SolverMode.Modify && selected is not null)
            context = new SceneContext(selected, scene.FindById(selected.HostElementId), scene.GetHostedElements(selected.Id).ToList(), []);
        else
            context = new SceneContext(null, null, [], scene.BuildingGroups.Select(g => g.Name).ToList());

        return new AiRequestContext
        {
            Task = string.IsNullOrWhiteSpace(task) ? DefaultVisionTask(vision) : task,
            Model = model,
            Mode = mode,
            SelectedElementId = selected?.Id,
            Scene = context,
            Vision = vision,
            Images = images,
            Image = clipboardImage ?? (images.Count > 0 ? images[0].Bytes : null),
            ImageName = imageName,
            Kind = kind,
            SheetsDescription = sheetsDescription
        };
    }

    public (string System, string User) BuildPrompts(AiRequestContext ctx)
    {
        var system = SystemPrompts.BuildSystem(ctx.Model, ctx.Vision, ctx.Kind, Math.Max(1, ctx.Images.Count));
        var task = string.IsNullOrWhiteSpace(ctx.SheetsDescription) ? ctx.Task : ctx.SheetsDescription + "\n" + ctx.Task;
        var user = SystemPrompts.BuildUser(task, ctx.Mode, ctx.Scene, ctx.Model.IsLocal);
        return (system, user);
    }

    /// <summary>Полный промпт для веб-аккаунта (системное ТЗ + задача в одном сообщении).</summary>
    public string BuildWebPrompt(AiRequestContext ctx)
    {
        var (system, user) = BuildPrompts(ctx);
        return SystemPrompts.BuildWebPrompt(system, user);
    }

    public async Task<AiRunResult> RunAsync(AiRequestContext ctx, CancellationToken cancellationToken)
    {
        if (ctx.Model.IsWeb)
            throw new InvalidOperationException("Веб-аккаунты обрабатываются WebAccountBridgeProvider.");
        if (ctx.Images.Count > 0 && !ctx.Model.SupportsVision)
            throw new AiProviderException("AIA-VISION-UNSUPPORTED",
                $"Модель {ctx.Model.DisplayName} не принимает изображения. Выберите Gemini, ChatGPT (gpt-4o / gpt-4.1 / gpt-5), Claude или веб-аккаунт.");

        var (system, user) = BuildPrompts(ctx);
        var request = new AiRequest
        {
            Model = ctx.Model,
            SystemPrompt = system,
            UserPrompt = user,
            Images = ctx.Images,
            Temperature = 0.1
        };

        var response = await ProviderFor(ctx.Model).GenerateAsync(request, cancellationToken);
        var result = ApplyResponseText(response.Text, ctx, response.ModelName);
        result.Report.Actions.Insert(0,
            $"Ответ BIM-Координатора: {response.Elapsed.TotalSeconds:0.0} с" +
            (response.Backend is { } backend ? $" · {backend}" : string.Empty) +
            (response.InputTokens is { } i ? $" · токенов вход {i}" : string.Empty) +
            (response.OutputTokens is { } o ? $", выход {o}" : string.Empty));
        return result;
    }

    /// <summary>
    /// Извлекает JSON из ответа любой нейросети (API, локальной или веб-чата), прогоняет через C#-солвер
    /// и применяет к сцене. Перед изменением сцены сохраняется шаг Undo. Вызывать из UI-потока.
    /// </summary>
    public AiRunResult ApplyResponseText(string text, AiRequestContext ctx, string source)
    {
        var solved = Solve(text, ctx);
        Commit(solved, scene);
        return solved.ToRunResult(source);
    }

    /// <summary>Провайдер для модели (Ollama, встроенная LLamaSharp, Gemini, OpenAI/OpenRouter, Claude). Веб-аккаунты — через WebAccountBridgeProvider.</summary>
    public IAiProvider ProviderFor(AiModelOption model) => model.Provider switch
    {
        AiProviderKind.Ollama => ollama,
        AiProviderKind.Local => local,
        AiProviderKind.Gemini => gemini,
        AiProviderKind.OpenAI or AiProviderKind.OpenRouter => openAi,
        AiProviderKind.Anthropic => claude,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model.Provider, "Веб-аккаунты обрабатываются WebAccountBridgeProvider.")
    };

    /// <summary>
    /// Разбор ответа ИИ и прогон через C#-солвер без изменения сцены (Совет КБ проверяет черновик на песочнице до выпуска).
    /// </summary>
    public SolvedResponse Solve(string text, AiRequestContext ctx) => Solve(text, ctx, scene);

    /// <summary>Строгая логика проёмов в солвере (<see cref="SolverInput.EnforceBuildingLogic"/>); включает главное окно приложения.</summary>
    public bool EnforceBuildingLogic { get; set; }

    public SolvedResponse Solve(string text, AiRequestContext ctx, SceneStateService target)
    {
        if (!AiJsonExtractor.TryExtract(text, out var document))
            throw new AiProviderException("AIA-JSON-001",
                "В ответе BIM-Координатора не найдено описание модели. Повторите команду или уточните задачу.",
                "Ответ модели:\n" + AiHttp.Truncate(text, 6000));

        var mode = ctx.Mode;
        string? selectedId = ctx.SelectedElementId;
        if (mode == SolverMode.Modify && target.FindById(selectedId) is null && document.Elements.All(e => e.Id is null || target.FindById(e.Id) is null))
            mode = target.Elements.Count > 0 ? SolverMode.AppendToSite : SolverMode.NewScene;
        if (mode == SolverMode.AppendToSite && target.Elements.Count == 0) mode = SolverMode.NewScene;

        var result = solver.Solve(new SolverInput
        {
            Document = document,
            Mode = mode,
            ExistingElements = target.Elements.ToList(),
            ExistingGroups = target.BuildingGroups.ToList(),
            SelectedElementId = selectedId,
            EnforceBuildingLogic = EnforceBuildingLogic
        });

        var report = result.Report;
        // Волна 4: подъём по фото — форма, ярусы, кривизна и цвета снимка гарантируются, каркас доводится до K_исп ≤ 1,0.
        if (ctx.Photo is { } photo && mode != SolverMode.Modify)
            PhotoToBimService.EnforceSolverResult(result, photo, ctx.PhotoOptions, ctx.PhotoLoads ?? new StructuralLoadSettings());
        if (document.WasRepaired)
            report.Warnings.Insert(0, "Ответ BIM-Координатора был обрезан по лимиту длины — построены все целые элементы");
        if (result.Elements.Count == 0)
            throw new AiProviderException("AIA-SOLVER-EMPTY", "После обработки ядром расчётов в 3D-модели не осталось элементов.", report.DetailsText);
        return new SolvedResponse(document, result, mode);
    }

    /// <summary>Применяет решённый ответ к сцене (живой или песочнице Совета) одним шагом Undo.</summary>
    public static void Commit(SolvedResponse solved, SceneStateService target)
    {
        var result = solved.Result;
        switch (solved.Mode)
        {
            case SolverMode.Modify:
                using (target.BeginUndoTransaction())
                {
                    target.LoadScene(result.Elements, result.Groups, recordUndo: true);
                    foreach (var byGroup in result.GridAxes.GroupBy(a => a.BuildingGroupId))
                        target.SetGroupAxes(byGroup.Key, byGroup.ToList(), recordUndo: false);
                }
                target.SelectById(result.SelectId);
                break;
            case SolverMode.AppendToSite:
                target.AppendBuildings(result.Groups, result.Elements, (result.OffsetX, result.OffsetZ), result.GridAxes);
                break;
            default:
                // Новая сцена: размеры рулетки старой сцены теряют смысл (Undo возвращает их вместе со сценой).
                target.LoadScene(result.Elements, result.Groups, recordUndo: target.Elements.Count > 0, axes: result.GridAxes, dimensions: []);
                break;
        }
    }

    /// <summary>При смене модели выгружает веса локальной модели из памяти (Dispose + GC.Collect).</summary>
    public Task OnModelSelectedAsync(AiModelOption model) =>
        model.IsLocal && local.LoadedModelFile == model.ModelId ? Task.CompletedTask : local.UnloadAsync();

    public static string ModeTitle(SolverMode mode) => mode switch
    {
        SolverMode.Modify => "правка выбранного",
        SolverMode.AppendToSite => "+ к участку",
        _ => "новая сцена"
    };

    private static string DefaultVisionTask(VisionMode vision) => vision switch
    {
        VisionMode.FloorPlan2DTo3D => "Построй 3D-модель здания по этому 2D-плану этажа.",
        VisionMode.HandSketchTo3D => "Построй 3D-модель по этому эскизу от руки.",
        VisionMode.RealPhotoTo3D => "Реконструируй 3D-модель здания по этой фотографии.",
        VisionMode.DrawingSet => "Построй точную 3D BIM-модель здания по выбранным листам чертежа: оси, уровни, сечения, плиты, отверстия и кровля — строго как на листах.",
        _ => "Построй здание."
    };
}
