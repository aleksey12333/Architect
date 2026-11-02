#if DEBUG
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест интеллектуального ядра на локальном ИИ: отделение рассуждений &lt;think&gt; deepseek-r1 от JSON (заглушки ответов
/// Ollama, потоковый NDJSON через подменённый HTTP-обработчик), разбор /api/tags и маршрутизация ролей по моделям Ollama с
/// учётом памяти, конвейер BimVerificationPipeline на сцене с намеренными ошибками (висящая колонна, проём в колонне, балка
/// сквозь окно, наложенные колонны) с проверкой ProposedFixOptions, и цикл самоисправления «ИИ ↔ C#-движок».
/// </summary>
public static partial class CadSelfTestService
{
    /// <summary>Модели Ollama пользователя (как в /api/tags): размер, ГБ весов, возможности.</summary>
    public static List<AiModelOption> OllamaTestModels() =>
    [
        AiModelCatalog.Ollama("deepseek-r1:14b", 14.8, ["tools", "thinking", "completion"]) with { SizeGb = 8.4 },
        AiModelCatalog.Ollama("gemma2:9b", 9.2, ["completion"]) with { SizeGb = 5.1 },
        AiModelCatalog.Ollama("gemma4:12b", 11.9, ["completion", "vision", "audio", "tools", "thinking"]) with { SizeGb = 7.5 },
        AiModelCatalog.Ollama("gpt-oss:20b", 20.9, ["completion", "tools", "thinking"]) with { SizeGb = 12.8 },
        AiModelCatalog.Ollama("hf.co/bartowski/Mistral-Nemo-Instruct-2407-GGUF:Q4_K_M", 12.2, ["completion", "tools"]) with { SizeGb = 7.0 },
        AiModelCatalog.Ollama("qwen2.5-coder:14b", 14.8, ["completion", "tools", "insert"]) with { SizeGb = 8.4 },
        AiModelCatalog.Ollama("qwen3.8:27b", 27.3, ["completion", "vision", "tools", "thinking"]) with { SizeGb = 16.5 }
    ];

    // ═════════════════════════════ 1. Рассуждения <think> и JSON ═════════════════════════════

    private static int CheckReasoningSeparation(StringBuilder report)
    {
        int failures = 0;
        const string scene = """{"mode":"new","message":"Тест","elements":[{"id":"c1","type":"column","position":[0,0],"size":[0.4,3,0.4]}]}""";

        // deepseek-r1 через Ollama без параметра think: рассуждение с фигурными скобками и JSON-подобным мусором внутри <think>.
        string deepseek = "<think>\nПроверю нагрузку: q = {g + p} = 8.5 кПа; кандидат {\"mode\":\"draft\",\"elements\":[{\"id\":\"x\"\nНет, так нельзя — нужен полный JSON.\n</think>\n\n```json\n" + scene + "\n```";
        var (answer, reasoning) = ReasoningText.Separate(deepseek);
        bool extracted = AiJsonExtractor.TryExtract(deepseek, out var doc) && doc.Elements.Count == 1 && doc.Elements[0].Id == "c1";
        Check(report, ref failures, extracted && !answer.Contains("<think>") && reasoning.StartsWith("Проверю нагрузку", StringComparison.Ordinal) && reasoning.Contains("полный JSON"),
            $"deepseek-r1 (<think>…</think> + ```json): рассуждения {reasoning.Length} симв. отделены, JSON разобран ({doc?.Elements.Count ?? 0} эл.), скобки внутри рассуждений не мешают");

        // Шаблон deepseek-r1 с открывающим тегом в промпте: ответ начинается сразу с рассуждения и «</think>».
        string prefilled = "Сначала оси, затем колонны {по сетке}.\n</think>\n" + scene;
        var (answer2, reasoning2) = ReasoningText.Separate(prefilled);
        Check(report, ref failures, answer2 == scene && reasoning2.StartsWith("Сначала оси", StringComparison.Ordinal) && AiJsonExtractor.TryExtract(prefilled, out _),
            "шаблон без открывающего тега («рассуждение…</think>JSON»): рассуждение и ответ разделены");

        // Несколько блоков, незакрытый блок (ответ обрезан по лимиту токенов), регистр тегов.
        var (answer3, reasoning3) = ReasoningText.Separate("<THINK>первый</THINK>{\"message\":\"a\"}<think>второй");
        var (answer4, reasoning4) = ReasoningText.Separate("<think>только рассуждение, ответа нет {");
        Check(report, ref failures, answer3 == "{\"message\":\"a\"}" && reasoning3.Contains("первый") && reasoning3.Contains("второй") && answer4.Length == 0 && reasoning4.Length > 0,
            "несколько блоков и незакрытый <think>: ответ без рассуждений, обрезанный хвост — в рассуждения");

        // Реплика агента: JSON внутри рассуждения не должен приниматься за ответ.
        bool reply = AgentPrompts.TryReadObject("<think>{\"message\":\"черновик\"}</think>{\"message\":\"итог\",\"variant\":\"v2\"}", out var root) &&
                     AgentPrompts.ReadString(root, "message") == "итог" && AgentPrompts.IsReplyJson("<think>x</think>{\"message\":\"ok\"}");
        Check(report, ref failures, reply, "реплика агента: JSON из <think> не принимается за ответ, берётся итоговый объект");

        // Потоковая реплика: незавершённое поле message.
        Check(report, ref failures, AgentDebateEntry.PartialMessage("{\"message\": \"Плита пере") == "Плита пере" && AgentDebateEntry.PartialMessage("{\"mess") is null,
            "живая лента: фрагмент поля message из незавершённого потока JSON");
        Check(report, ref failures, ReasoningText.IsReasoningFamily("deepseek-r1:14b") && ReasoningText.IsReasoningFamily("gpt-oss:20b") && !ReasoningText.IsReasoningFamily("qwen2.5-coder:14b"),
            "семейства рассуждающих моделей: deepseek-r1, gpt-oss — да; qwen2.5-coder — нет");
        return failures;
    }

    // ═════════════════════════════ 2. Провайдер Ollama (заглушка HTTP) ═════════════════════════════

    /// <summary>Подменённый HTTP-обработчик: запоминает тела запросов и отдаёт сценарный ответ.</summary>
    private sealed class StubOllamaHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 0) Bodies.Add(JsonNode.Parse(body)!.AsObject());
            return respond(request.RequestUri?.AbsolutePath ?? string.Empty);
        }
    }

    private static HttpResponseMessage Ndjson(params object[] lines) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join("\n", lines.Select(l => JsonSerializer.Serialize(l))) + "\n", Encoding.UTF8, "application/x-ndjson")
    };

    private static object Chunk(string content, string? thinking = null) => thinking is null
        ? new { model = "m", message = new { role = "assistant", content }, done = false }
        : new { model = "m", message = new { role = "assistant", content, thinking }, done = false };

    private static object Done() => new { model = "m", message = new { role = "assistant", content = "" }, done = true, done_reason = "stop", prompt_eval_count = 812, eval_count = 230 };

    private sealed class CollectProgress : IProgress<AiStreamProgress>
    {
        public List<AiStreamProgress> Items { get; } = [];
        public void Report(AiStreamProgress value) => Items.Add(value);
    }

    private static async Task<int> CheckOllamaProviderAsync(StringBuilder report)
    {
        int failures = 0;
        var endpoint = new Uri("http://localhost:11434/");
        var models = OllamaTestModels();
        var deepseek = models.First(m => m.ModelId == "deepseek-r1:14b");
        var coder = models.First(m => m.ModelId == "qwen2.5-coder:14b");
        AiRequest Request(AiModelOption m, IProgress<AiStreamProgress>? p = null) => new()
        {
            Model = m, SystemPrompt = "Ответь JSON-объектом {\"message\"}.", UserPrompt = "Оцени плиту 200 мм на пролёте 10.5 м.", Temperature = 0.0, MaxOutputTokens = 1500, Progress = p
        };

        // A. deepseek-r1, рассуждения блоком <think> внутри content, теги разрезаны между фрагментами потока.
        var handlerA = new StubOllamaHandler(_ => Ndjson(Chunk("<thi"), Chunk("nk>\nСчитаю: L/30 = 350 мм {грубо}"), Chunk(", значит 200 мм мало.</th"),
            Chunk("ink>\n\n{\"message\": \"Пл"), Chunk("ита перегружена: K = 1.4\"}"), Done()));
        var progressA = new CollectProgress();
        var a = await new OllamaAIProvider(endpoint, handlerA).GenerateAsync(Request(deepseek, progressA), CancellationToken.None);
        var bodyA = handlerA.Bodies[0];
        bool thinkSent = bodyA["think"]?.GetValue<bool>() == true && bodyA["format"] is null && bodyA["stream"]?.GetValue<bool>() == true &&
                         bodyA["options"]!["num_predict"]!.GetValue<int>() == 1500 + OllamaAIProvider.ReasoningBudget;
        Check(report, ref failures, a.Text == "{\"message\": \"Плита перегружена: K = 1.4\"}" && a.Reasoning?.Contains("200 мм мало") == true && thinkSent &&
                                    a.InputTokens == 812 && a.OutputTokens == 230 && progressA.Items.Count >= 1 && progressA.Items.All(p => !p.Answer.Contains("<think>")) &&
                                    progressA.Items[^1].Answer == a.Text && progressA.Items[^1].Reasoning == a.Reasoning,
            $"Ollama · deepseek-r1 (поток NDJSON, теги <think> разрезаны между фрагментами): ответ «{a.Text}», рассуждения {a.Reasoning?.Length ?? 0} симв., " +
            $"think = true, num_predict {bodyA["options"]!["num_predict"]} (ответ + бюджет рассуждений), обновлений ленты {progressA.Items.Count}, токенов {a.InputTokens}/{a.OutputTokens}");

        // B. Новый протокол: рассуждения в поле message.thinking.
        var handlerB = new StubOllamaHandler(_ => Ndjson(Chunk("", "Нагрузка 8.5 кПа, "), Chunk("", "прогиб выше предельного."), Chunk("{\"message\":\"Нужны балки\"}"), Done()));
        var b = await new OllamaAIProvider(endpoint, handlerB).GenerateAsync(Request(deepseek), CancellationToken.None);
        Check(report, ref failures, b.Text == "{\"message\":\"Нужны балки\"}" && b.Reasoning == "Нагрузка 8.5 кПа, прогиб выше предельного.",
            "Ollama · поле message.thinking: рассуждения отделены от ответа");

        // C. Модель без рассуждений: format = json, без think; num_ctx/num_predict по размеру промпта.
        var handlerC = new StubOllamaHandler(_ => Ndjson(Chunk("{\"message\":\"ok\"}"), Done()));
        var c = await new OllamaAIProvider(endpoint, handlerC).GenerateAsync(Request(coder), CancellationToken.None);
        var bodyC = handlerC.Bodies[0];
        var options = bodyC["options"]!.AsObject();
        Check(report, ref failures, c.Text == "{\"message\":\"ok\"}" && c.Reasoning is null && bodyC["format"]?.GetValue<string>() == "json" && bodyC["think"] is null &&
                                    options["num_predict"]!.GetValue<int>() == 1500 && options["num_ctx"]!.GetValue<int>() >= 8192 && bodyC["model"]!.GetValue<string>() == "qwen2.5-coder:14b",
            $"Ollama · qwen2.5-coder: format = json, без think, num_ctx {options["num_ctx"]}, num_predict {options["num_predict"]}");

        // D. Ошибки сервера: нехватка памяти в потоке и отсутствующая модель.
        string? memoryCode = null, notFoundCode = null;
        try
        {
            await new OllamaAIProvider(endpoint, new StubOllamaHandler(_ => Ndjson(new { error = "model requires more system memory (17.7 GiB) than is available (11.2 GiB)" })))
                .GenerateAsync(Request(coder), CancellationToken.None);
        }
        catch (AiProviderException ex)
        {
            memoryCode = ex.Code;
        }
        try
        {
            await new OllamaAIProvider(endpoint, new StubOllamaHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
                { Content = new StringContent("{\"error\":\"model 'qwen9:70b' not found\"}") })).GenerateAsync(Request(coder), CancellationToken.None);
        }
        catch (AiProviderException ex)
        {
            notFoundCode = ex.Code;
        }
        Check(report, ref failures, memoryCode == "AIA-OLLAMA-MEMORY" && notFoundCode == "AIA-OLLAMA-404",
            $"ошибки Ollama переводятся в понятные коды: нехватка памяти → {memoryCode}, нет модели → {notFoundCode}");

        // E. /api/tags: модели, размеры, возможности; эмбеддинги отбрасываются.
        const string tags = """
{"models":[{"name":"deepseek-r1:14b","size":8988112209,"details":{"parameter_size":"14.8B"},"capabilities":["tools","thinking","completion"]},
{"name":"gemma4:12b","size":8021618941,"details":{"parameter_size":"11.9B"},"capabilities":["completion","vision","audio","tools","thinking"]},
{"name":"nomic-embed-text:latest","size":274302450,"details":{"parameter_size":"137M"},"capabilities":["embedding"]},
{"name":"qwen2.5-coder:14b","size":8988124298,"details":{"parameter_size":"14.8B"},"capabilities":["completion","tools","insert"]}]}
""";
        using var tagsDoc = JsonDocument.Parse(tags);
        var parsed = OllamaProcessManager.ParseTags(tagsDoc.RootElement);
        var gemma = parsed.FirstOrDefault(m => m.ModelId == "gemma4:12b");
        Check(report, ref failures, parsed.Count == 3 && gemma is { SupportsVision: true, IsReasoning: true, IsOllama: true } && Math.Abs(gemma.ParameterBillions - 11.9) < 1e-9 &&
                                    parsed.First(m => m.ModelId == "deepseek-r1:14b").IsReasoning && !parsed.First(m => m.ModelId == "qwen2.5-coder:14b").SupportsVision &&
                                    AiModelCatalog.FromId("ollama:deepseek-r1:14b") is { IsOllama: true, ModelId: "deepseek-r1:14b" },
            $"/api/tags: моделей {parsed.Count} (эмбеддинги отброшены), gemma4 — зрение и рассуждения, {gemma?.ParameterBillions:0.0}B; ключ ollama:… восстанавливается");

        // F. Маршрутизация ролей по моделям Ollama с учётом памяти компьютера (16 ГБ): 27B-модель в автоподбор не идёт.
        var all = new List<AiModelOption>(models) { AiModelCatalog.LocalQwen7B };
        var routes = AgentModelRouter.Resolve(new CouncilSettings(), all, models[0], memoryGb: 15.5);
        string Show(IEnumerable<AgentAssignment> list) => string.Join("; ", list.Select(r => $"{AgentRoles.Info(r.Role).ShortRu} → {r.Model.ModelId}"));
        report.AppendLine("  Автоподбор по Ollama (ОЗУ 15.5 ГБ): " + Show(routes));
        var arch = routes.First(r => r.Role == AiAgentRole.ChiefArchitect).Model;
        var norms = routes.First(r => r.Role == AiAgentRole.NormsAndSafetyVerifier).Model;
        var structural = routes.First(r => r.Role == AiAgentRole.StructuralEngineer).Model;
        Check(report, ref failures, routes.All(r => r.Model.IsOllama) && arch.SupportsVision && norms.ModelId.StartsWith("deepseek-r1", StringComparison.Ordinal) &&
                                    structural.ModelId.Contains("coder", StringComparison.Ordinal) && routes.All(r => r.Model.ModelId != "qwen3.8:27b") &&
                                    routes.Select(r => AgentModelRouter.ProviderKey(r.Model)).Distinct().Count() >= 3 && !AgentModelRouter.IsSingleProvider(routes),
            "кросс-модельная маршрутизация Ollama: Архитектор — модель со зрением, Конструктор — coder, Нормоконтроль — deepseek-r1; " +
            "встроенный LLamaSharp и не помещающаяся в память 27B не назначаются; не меньше трёх разных моделей");
        var bound = new CouncilSettings { Bindings = { ["architect"] = "ollama:gemma4:12b", ["structural"] = "ollama:qwen3.8:27b", ["norms"] = "ollama:deepseek-r1:14b", ["synthesizer"] = "ollama:gpt-oss:20b" } };
        var explicitRoutes = AgentModelRouter.Resolve(bound, all, models[0], memoryGb: 15.5);
        Check(report, ref failures, explicitRoutes.Select(r => r.Model.ModelId).SequenceEqual(["gemma4:12b", "qwen3.8:27b", "deepseek-r1:14b", "gpt-oss:20b"]) && explicitRoutes.All(r => !r.IsFallback),
            "явные назначения из выпадающих списков (gemma4 / qwen3.8 / deepseek-r1 / gpt-oss) побеждают автоподбор: " + Show(explicitRoutes));

        report.AppendLine($"  ollama.exe: {OllamaProcessManager.FindExecutable() ?? "не найден"}; ОЗУ {OllamaProcessManager.TotalMemoryGb:0.0} ГБ");
        var manager = App.Services.GetRequiredService<OllamaProcessManager>();
        report.AppendLine($"  Движок приложения ({manager.Endpoint}): {manager.State} — {manager.Detail}" +
                          (manager.Models.Count > 0 ? $"; модели: {string.Join(", ", manager.Models.Select(m => m.ModelId))}" : string.Empty));
        return failures;
    }

    /// <summary>
    /// Только при AIA_DEBUG_OLLAMALIVE=модель: настоящий запрос к серверу Ollama (автозапуск при необходимости) — короткая
    /// реплика Аудитора с проверкой отделения рассуждений. Без переменной раздел пропускается (автотест не зависит от сети).
    /// </summary>
    private static async Task<int> CheckOllamaLiveAsync(StringBuilder report)
    {
        string? modelId = Environment.GetEnvironmentVariable("AIA_DEBUG_OLLAMALIVE");
        if (string.IsNullOrWhiteSpace(modelId))
        {
            report.AppendLine("  (живой запрос пропущен: AIA_DEBUG_OLLAMALIVE не задана)");
            return 0;
        }
        int failures = 0;
        var manager = App.Services.GetRequiredService<OllamaProcessManager>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ready = await manager.EnsureRunningAsync();
        report.AppendLine($"  Движок: {manager.State} за {sw.Elapsed.TotalSeconds:0.0} с — {manager.Detail}");
        Check(report, ref failures, ready && manager.Models.Count > 0, $"Zero-Config: сервер Ollama готов, моделей {manager.Models.Count}");
        if (!ready) return failures;
        var model = manager.Models.FirstOrDefault(m => m.ModelId == modelId) ?? AiModelCatalog.Ollama(modelId);
        var provider = App.Services.GetRequiredService<OllamaAIProvider>();
        var progress = new CollectProgress();
        sw.Restart();
        try
        {
            var response = await provider.GenerateAsync(new AiRequest
            {
                Model = model,
                SystemPrompt = "Ты инженер нормоконтроля. Ответь ОДНИМ JSON-объектом {\"message\": \"1-2 предложения по-русски\"}. Без эмодзи.",
                UserPrompt = "Плита 200 мм на пролёте 10.5 м, K_исп = 1.4. Дай рецепт усиления с числами.",
                Temperature = 0,
                MaxOutputTokens = 2500,
                Progress = progress
            }, CancellationToken.None);
            report.AppendLine($"  Ответ ({sw.Elapsed.TotalSeconds:0.0} с, токенов {response.InputTokens}/{response.OutputTokens}): {AiHttp.Truncate(response.Text, 400)}");
            report.AppendLine($"  Рассуждения ({response.Reasoning?.Length ?? 0} симв.): {AiHttp.Truncate(response.Reasoning ?? "—", 400)}");
            bool json = AgentPrompts.TryReadObject(response.Text, out var root) && AgentPrompts.ReadString(root, "message") is { Length: > 0 };
            Check(report, ref failures, json && !response.Text.Contains("<think>", StringComparison.OrdinalIgnoreCase) && (!model.IsReasoning || response.HasReasoning),
                $"живой ответ {model.ModelId}: JSON реплики разобран, рассуждения {(response.HasReasoning ? "отделены" : "отсутствуют")}, обновлений потока {progress.Items.Count}");
        }
        catch (AiProviderException ex)
        {
            Check(report, ref failures, false, $"живой запрос {model.ModelId}: {ex.Code} {ex.Message}");
        }
        return failures;
    }

    // ═════════════════════════════ 3. Конвейер на сцене с намеренными ошибками ═════════════════════════════

    /// <summary>
    /// Одноэтажный каркас 6 × 6 м + второй ярус: колонна второго этажа висит в пустоте (вне плиты), окно стены задевает
    /// колонну, балка проходит сквозь второе окно, две колонны наложены друг на друга.
    /// </summary>
    public static (List<ArchitecturalElement> Elements, List<BuildingGroup> Groups) CreateDefectScene()
    {
        const string g = "Дефекты-тест";
        var list = new List<ArchitecturalElement>();
        int n = 0;
        ArchitecturalElement Add(string type, string name, double x, double baseY, double z, double w, double h, double d, int level = 0, string material = "concrete")
        {
            var e = new ArchitecturalElement { Id = $"el_df{++n:D3}", Type = type, Name = name, BuildingGroupId = g, LevelIndex = level, Material = material };
            e.SetSize(w, h, d);
            e.SetPosition(x, baseY + h / 2, z);
            list.Add(e);
            return e;
        }
        Add(ElementTypes.Foundation, "Фундаментная плита", 3, -0.5, 3, 7.0, 0.5, 7.0);
        foreach (var (x, z) in new[] { (0.0, 0.0), (6.0, 0.0), (0.0, 6.0), (6.0, 6.0) })
            Add(ElementTypes.Column, $"Колонна {x:0}/{z:0}", x, 0, z, 0.4, 3.0, 0.4);
        Add(ElementTypes.Column, "Колонна-дубль 6/6", 6.25, 0, 6.2, 0.4, 3.0, 0.4);
        Add(ElementTypes.Slab, "Плита перекрытия", 3, 3.0, 3, 6.6, 0.2, 6.6);
        Add(ElementTypes.Column, "Колонна висящая", 9.5, 3.2, 3, 0.4, 3.0, 0.4, 1);
        var wall = Add(ElementTypes.Wall, "Стена по оси А", 3, 0, 0, 6.0, 3.0, 0.3, 0, "brick");
        var windowOnColumn = Add(ElementTypes.Window, "Окно ОК-1 на колонне", 5.6, 0.9, 0, 1.4, 1.5, 0.36, 0, "glass");
        windowOnColumn.HostElementId = wall.Id;
        var windowUnderBeam = Add(ElementTypes.Window, "Окно ОК-2 под балкой", 2.0, 0.9, 0, 1.2, 1.9, 0.36, 0, "glass");
        windowUnderBeam.HostElementId = wall.Id;
        var beam = Add(ElementTypes.Beam, "Балка Б-1 сквозь окно", 3, 2.4, 0, 6.0, 0.6, 0.3);
        _ = beam;
        return (list, [new BuildingGroup(g, "office", "#FF453A")]);
    }

    private static int CheckDefectScenePipeline(StringBuilder report)
    {
        int failures = 0;
        var pipeline = BimVerificationPipeline.CreateDefault();
        var (elements, groups) = CreateDefectScene();
        var ctx = new VerificationContext(elements, groups, [], new StructuralLoadSettings());
        var result = pipeline.Run(ctx);
        report.AppendLine($"  Сцена с намеренными ошибками: {result.Summary}");
        foreach (var f in result.Findings.Where(f => f.Severity != VerificationSeverity.Info))
            report.AppendLine($"    [{f.Code}] {f.SeverityTitle}: {f.Title} → {string.Join(" | ", f.ProposedFixOptions.Select(o => $"{VerificationText.AuthorTitle(o.Author)}: {o.Title}"))}");

        string Id(string name) => elements.First(e => e.Name == name).Id;
        VerificationFinding? Find(string code, string name) => result.Findings.FirstOrDefault(f => f.Code == code && f.AffectedElementIds.Contains(Id(name)));
        var hanging = Find("VLP-VOID", "Колонна висящая");
        var onColumn = Find("CLASH-OPENING-COLUMN", "Окно ОК-1 на колонне");
        var throughWindow = Find("XSECT-BEAM-OPENING", "Окно ОК-2 под балкой");
        var overlap = result.Findings.FirstOrDefault(f => f.Code == "XSECT-COLUMNS");
        Check(report, ref failures, pipeline.Verifiers.Any(v => v.Id == "XSECT"), "плагин MemberIntersectionVerifier (XSECT) подключён конвейером автоматически");
        Check(report, ref failures, hanging is { IsCritical: true } && hanging.ProposedFixOptions.Any(o => o.Author == FixAuthor.Structural && o.Actions.Any(a => a.Kind == FixActionKind.AddSupportColumnBelow)),
            $"висящая колонна → {hanging?.Code ?? "не найдена"}: «{hanging?.ProposedFixOptions.FirstOrDefault(o => !o.IsKeep)?.Title}»");
        Check(report, ref failures, onColumn is not null && onColumn.ProposedFixOptions.Count(o => !o.IsKeep) >= 2 &&
                                    onColumn.ProposedFixOptions.Any(o => o.Actions.Any(a => a.Kind == FixActionKind.MoveWallOpening)) &&
                                    onColumn.ProposedFixOptions.Any(o => o.Actions.Any(a => a.Kind == FixActionKind.NarrowWallOpening)),
            $"проём в колонне → {onColumn?.Code ?? "не найден"}: сдвиг (Архитектор) и сужение (Конструктор)");
        Check(report, ref failures, throughWindow is { IsCritical: true } && throughWindow.ProposedFixOptions.Any(o => o.Author == FixAuthor.Architect && o.Actions.Any(a => a.Kind == FixActionKind.ResizeOpening)),
            $"балка сквозь окно → {throughWindow?.Code ?? "не найдена"}: {string.Join(" | ", throughWindow?.ProposedFixOptions.Where(o => !o.IsKeep).Select(o => o.Title) ?? [])}");
        Check(report, ref failures, overlap is not null && overlap.HasFixes, $"наложенные колонны → {overlap?.Code ?? "не найдены"}");
        Check(report, ref failures, result.Findings.All(f => f.ProposedFixOptions.Any(o => o.IsKeep)) && result.Findings.Where(f => f.IsCritical).All(f => f.HasFixes),
            "каждое замечание несёт ProposedFixOptions (у критичных — исправление, у всех — «Оставить как есть»)");

        // Каждый вариант исправления снимает своё замечание (моделирование на песочнице).
        var fixedAll = true;
        var log = new List<string>();
        foreach (var f in new[] { hanging, onColumn, throughWindow, overlap }.OfType<VerificationFinding>())
            foreach (var option in f.ProposedFixOptions.Where(o => !o.IsKeep && o.Actions.Count > 0))
            {
                var sandbox = DesignFixEngine.Sandbox(ctx);
                DesignFixEngine.Apply(sandbox, option.Actions);
                var after = pipeline.Run(VerificationContext.FromScene(sandbox, new StructuralLoadSettings()));
                bool gone = !after.Findings.Any(x => x.Code == f.Code && x.AffectedElementIds.Intersect(f.AffectedElementIds.Take(1)).Any());
                fixedAll &= gone;
                log.Add($"{f.Code} ← {VerificationText.AuthorTitle(option.Author)}: {(gone ? "снято" : "ОСТАЛОСЬ")}");
            }
        Check(report, ref failures, fixedAll && log.Count >= 5, "варианты исправления снимают замечания: " + string.Join("; ", log));

        // Список дефектов для ИИ-Аудитора — на чистом русском, с кодами, элементами и координатами.
        string defects = AgentPrompts.DefectList(result, 16, 2, elements);
        bool russian = defects.Contains("[VLP-VOID]") && defects.Contains("«Колонна висящая»") && defects.Contains("центр X 9.5") && defects.Contains("почему:") &&
                       defects.Contains("вариант (Конструктор)") && !defects.Any(ch => char.IsSurrogate(ch));
        Check(report, ref failures, russian, $"список дефектов для Аудитора: {defects.Split('\n').Length} строк, коды, элементы с координатами, причины и варианты");
        return failures;
    }

    // ═════════════════════════════ 4. Цикл самоисправления «ИИ ↔ C#» ═════════════════════════════

    private static async Task<int> CheckSelfCorrectionLoopAsync(StringBuilder report)
    {
        int failures = 0;
        var models = OllamaTestModels();
        var council = new CouncilSettings
        {
            ConsultUser = true,
            MaxReworkIterations = 3,
            Bindings = { ["architect"] = "ollama:gemma4:12b", ["structural"] = "ollama:qwen2.5-coder:14b", ["norms"] = "ollama:deepseek-r1:14b", ["synthesizer"] = "ollama:gpt-oss:20b" }
        };
        var scene = new SceneStateService();
        var gateway = new CouncilScenarioGateway { FlawedFirstDraft = true };
        var swarm = new AgentSwarmOrchestrator(scene, App.Services.GetRequiredService<AIOrchestrator>(), gateway, () => council, () => new StructuralLoadSettings(), () => models);
        var timeline = new List<AgentDebateEntry>();
        var context = new AiRequestContext { Task = "Двухэтажный офис 16.5 × 12 м с пролётом 10.5 м и лестницей", Model = models[0], Mode = SolverMode.NewScene };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = await swarm.RunDesignAsync(context, consult: true, timeline.Add, CancellationToken.None);
        sw.Stop();
        foreach (var e in timeline)
            report.AppendLine($"    {e.RoleTitle} [{e.ModelLabel}] {e.ElapsedText}: {AiHttp.Truncate(e.Text, 260)}" + (e.HasReasoning ? $"\n      рассуждения: {AiHttp.Truncate(e.Reasoning, 160)}" : string.Empty));
        foreach (var i in run.Iterations)
            report.AppendLine($"    раунд {i.Number}: критичных {i.Critical}, коллизий {i.Clashes}, K_исп {i.MaxUtilization:0.00}, элементов {i.Elements}, коды {string.Join(",", i.Codes)}");

        var rework = gateway.Calls.FirstOrDefault(c => c.IsRework);
        var first = run.Iterations.FirstOrDefault();
        var last = run.Iterations.LastOrDefault();
        Check(report, ref failures, run.Iterations.Count >= 2 && first is { HasDefects: true } && last is { Critical: 0, Clashes: 0 } && last.MaxUtilization <= 1.0 &&
                                    run.ReleasedIteration == last.Number,
            $"самоисправление: раундов {run.Iterations.Count}, критичных {first?.Critical} → {last?.Critical}, K_исп {first?.MaxUtilization:0.00} → {last?.MaxUtilization:0.00}; выпущен раунд {run.ReleasedIteration} ({sw.Elapsed.TotalMilliseconds:0} мс)");
        Check(report, ref failures, rework is not null && rework.Role == AiAgentRole.StructuralEngineer && rework.ModelId == "ollama:qwen2.5-coder:14b" &&
                                    rework.UserPrompt.Contains("ДЕФЕКТЫ C#-ДВИЖКА", StringComparison.Ordinal) && rework.UserPrompt.Contains("KE-SLAB", StringComparison.Ordinal) &&
                                    rework.UserPrompt.Contains("РЕЦЕПТ ИНЖЕНЕРА-АУДИТОРА", StringComparison.Ordinal) && rework.UserPrompt.Contains("x = 11.25", StringComparison.Ordinal),
            "доработка: Конструктор (qwen2.5-coder) получил точный список дефектов C#-движка с кодами и рецепт Аудитора deepseek-r1 (recipes)");
        var normsEntry = timeline.FirstOrDefault(e => e.Role == AiAgentRole.NormsAndSafetyVerifier && e.HasReasoning);
        Check(report, ref failures, normsEntry is not null && normsEntry.Reasoning.Contains("L/30", StringComparison.Ordinal) && !normsEntry.Text.Contains("<think>", StringComparison.Ordinal) &&
                                    timeline.Where(e => e.Role == AiAgentRole.StructuralEngineer).All(e => !e.HasReasoning),
            "Коллоквиум: рассуждения deepseek-r1 (Аудитор) отделены в «Внутренние рассуждения агента», у модели без рассуждений блока нет");
        Check(report, ref failures, scene.Elements.Count > 20 && scene.Elements.Count(e => e.Type == ElementTypes.Column) >= 24 && scene.Elements.Any(e => e.Type == ElementTypes.SlabOpening),
            $"в сцену выпущена доработанная модель: элементов {scene.Elements.Count}, колонн {scene.Elements.Count(e => e.Type == ElementTypes.Column)}, проём над маршем есть");

        // Лимит: Конструктор не исправляет дефекты — цикл останавливается на MaxReworkIterations, выпускается лучший черновик.
        var stubborn = new CouncilScenarioGateway();
        var limited = new CouncilSettings { ConsultUser = true, MaxReworkIterations = 2 };
        var scene2 = new SceneStateService();
        var swarm2 = new AgentSwarmOrchestrator(scene2, App.Services.GetRequiredService<AIOrchestrator>(), stubborn, () => limited, () => new StructuralLoadSettings(), () => models);
        var run2 = await swarm2.RunDesignAsync(context, consult: true, _ => { }, CancellationToken.None);
        Check(report, ref failures, run2.Iterations.Count == 3 && stubborn.Calls.Count(c => c.IsRework) == 2 && run2.Consultation is { Variants.Count: >= 1 },
            $"лимит итераций: дефекты не устранены моделью — {run2.Iterations.Count} проверки, {stubborn.Calls.Count(c => c.IsRework)} доработки, остаток — в карточку «Запрос ГИПа к Вам» ({run2.Consultation?.Variants.Count ?? 0} вар.)");
        return failures;
    }
}
#endif
