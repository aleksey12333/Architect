namespace AIArchitectStudio.Services.AI;

/// <summary>
/// Кросс-модельная маршрутизация ролей Совета КБ (Multi-LLM). Явная привязка роли к модели побеждает; «Автоподбор»
/// ранжирует доступные модели по профилю роли (Архитектор — сильное зрение Gemini, Конструктор — генерация сложного JSON
/// Claude / o-серия, Нормоконтроль — ChatGPT или локальный Qwen, Координатор — Claude Sonnet) со штрафом за повтор
/// провайдера, чтобы бюро состояло из разных моделей. Если доступен один провайдер (только локальный Qwen, один ключ или
/// один веб-аккаунт), все роли выполняются им последовательно со своими промптами и температурами.
/// </summary>
public static class AgentModelRouter
{
    /// <summary>Штраф за повторное использование провайдера в автоподборе (разнообразие бюро).</summary>
    private const double ReusePenalty = 9;

    public static IReadOnlyList<AgentAssignment> Resolve(CouncilSettings settings, IReadOnlyList<AiModelOption> available, AiModelOption dockModel,
        double memoryGb = 0)
    {
        // Веб-аккаунт участвует в автоподборе, только если пользователь сам работает через него (он требует открытой панели).
        var pool = available.Where(m => !m.IsWeb || m.Id == dockModel.Id).GroupBy(m => m.Id).Select(g => g.First()).ToList();
        // Есть сервер Ollama — встроенные веса LLamaSharp в автоподбор не идут: несколько моделей в процессе приложения
        // дают Out-Of-Memory, а Ollama сам загружает и вытесняет модели ролей.
        if (pool.Any(m => m.IsOllama)) pool.RemoveAll(m => m.IsLocal);
        if (pool.Count == 0) pool.Add(dockModel);
        bool single = pool.Select(ProviderKey).Distinct().Count() == 1;

        var result = new List<AgentAssignment>();
        var used = new Dictionary<string, int>();
        foreach (var info in AgentRoles.All)
        {
            string binding = settings.BindingFor(info.Role);
            AiModelOption? model = null;
            bool fallback = false;
            string reason;
            if (binding != CouncilSettings.AutoModelId)
            {
                model = available.FirstOrDefault(m => m.Id == binding) ?? (dockModel.Id == binding ? dockModel : null);
                if (model is null)
                {
                    fallback = true;
                    reason = "назначенная модель недоступна (нет ключа или файла) — автоподбор";
                }
                else reason = "назначено пользователем";
            }
            else reason = single ? "единственный провайдер — смена ролевых промптов и температур" : "автоподбор по профилю роли";

            if (model is null)
            {
                model = pool.OrderByDescending(m => Score(info.Role, m, memoryGb) - (single ? 0 : ReusePenalty * used.GetValueOrDefault(ProviderKey(m))))
                    .ThenBy(m => m.Id, StringComparer.Ordinal).First();
            }
            used[ProviderKey(model)] = used.GetValueOrDefault(ProviderKey(model)) + 1;
            result.Add(new AgentAssignment(info.Role, model, info.Temperature, binding == CouncilSettings.AutoModelId || fallback, fallback, reason));
        }
        return result;
    }

    /// <summary>Все роли выполняет один провайдер (последовательный режим одной модели).</summary>
    public static bool IsSingleProvider(IReadOnlyList<AgentAssignment> assignments) =>
        assignments.Select(a => ProviderKey(a.Model)).Distinct().Count() == 1;

    /// <summary>
    /// Ключ «провайдера» для разнообразия бюро. Модели Ollama разных семейств считаются разными участниками
    /// (deepseek-r1, qwen2.5-coder, gemma4 — разные нейросети, хоть и на одном сервере).
    /// </summary>
    public static string ProviderKey(AiModelOption m) =>
        m.IsLocal ? "local" : m.IsOllama ? "ollama:" + OllamaFamily(m.ModelId) : m.IsWeb ? "web:" + m.Provider : m.Provider.ToString();

    /// <summary>Семейство модели Ollama: «deepseek-r1:14b» → «deepseek-r1», «hf.co/x/Mistral-Nemo:Q4» → «mistral-nemo».</summary>
    public static string OllamaFamily(string modelId)
    {
        string name = modelId.ToLowerInvariant();
        if (name.Contains('/')) name = name[(name.LastIndexOf('/') + 1)..];
        int colon = name.IndexOf(':');
        if (colon > 0) name = name[..colon];
        foreach (var suffix in new[] { "-instruct-2407-gguf", "-instruct-gguf", "-gguf", "-instruct" })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        return name;
    }

    /// <summary>
    /// Пригодность модели Ollama для роли: размер (логарифм числа параметров), зрение для Архитектора, coder-модели для
    /// JSON каркаса, рассуждающие модели (deepseek-r1, gpt-oss) для Нормоконтроля и Координатора. Модель, которая не
    /// помещается в оперативную память (размер весов &gt; 75 % ОЗУ), в автоподборе почти не выбирается.
    /// </summary>
    private static double OllamaScore(AiAgentRole role, AiModelOption m, double memoryGb)
    {
        string id = m.ModelId.ToLowerInvariant();
        double billions = m.ParameterBillions > 0 ? m.ParameterBillions : 7;
        double score = 50 + 6 * Math.Log(1 + billions);
        bool coder = id.Contains("coder") || id.Contains("code");
        bool reasoning = m.IsReasoning;
        bool r1 = id.Contains("deepseek-r1") || id.Contains("qwq");
        score += role switch
        {
            AiAgentRole.ChiefArchitect => (m.SupportsVision ? 12 : 0) + (reasoning ? 2 : 0) - (coder ? 4 : 0),
            AiAgentRole.StructuralEngineer => (coder ? 10 : 0) + (reasoning ? 3 : 0) + (id.Contains("qwen") ? 4 : 0) - (r1 ? 6 : 0),
            AiAgentRole.NormsAndSafetyVerifier => (reasoning ? 14 : 0) + (r1 ? 5 : 0),
            _ => (reasoning ? 8 : 0)
        };
        if (memoryGb > 0 && m.SizeGb > memoryGb * 0.75) score -= 40;
        return score;
    }

    /// <summary>Пригодность модели для роли (0…100).</summary>
    public static double Score(AiAgentRole role, AiModelOption m, double memoryGb = 0)
    {
        if (m.IsOllama) return OllamaScore(role, m, memoryGb);
        string id = m.ModelId.ToLowerInvariant();
        bool pro = id.Contains("pro") || id.Contains("opus") || id.Contains("o3") || id.Contains("gpt-5") || id.Contains("4.1");
        bool small = id.Contains("haiku") || id.Contains("mini") || id.Contains("flash-lite") || id.Contains("nano") || id.Contains("3b");
        double tier = pro ? 8 : small ? -8 : 0;
        if (m.IsLocal) return (id.Contains("7b") ? 40 : 28) + (role == AiAgentRole.NormsAndSafetyVerifier ? 18 : 0);
        double baseScore = (role, m.Provider) switch
        {
            (AiAgentRole.ChiefArchitect, AiProviderKind.Gemini or AiProviderKind.WebGemini) => 92,
            (AiAgentRole.ChiefArchitect, AiProviderKind.Anthropic or AiProviderKind.WebClaude) => 84,
            (AiAgentRole.ChiefArchitect, AiProviderKind.OpenAI or AiProviderKind.WebChatGpt) => 80,
            (AiAgentRole.StructuralEngineer, AiProviderKind.Anthropic or AiProviderKind.WebClaude) => 94,
            (AiAgentRole.StructuralEngineer, AiProviderKind.OpenAI or AiProviderKind.WebChatGpt) => 86,
            (AiAgentRole.StructuralEngineer, AiProviderKind.Gemini or AiProviderKind.WebGemini) => 82,
            (AiAgentRole.NormsAndSafetyVerifier, AiProviderKind.OpenAI or AiProviderKind.WebChatGpt) => 92,
            (AiAgentRole.NormsAndSafetyVerifier, AiProviderKind.Anthropic or AiProviderKind.WebClaude) => 84,
            (AiAgentRole.NormsAndSafetyVerifier, AiProviderKind.Gemini or AiProviderKind.WebGemini) => 80,
            (AiAgentRole.MasterSynthesizer, AiProviderKind.Anthropic or AiProviderKind.WebClaude) => 94,
            (AiAgentRole.MasterSynthesizer, AiProviderKind.Gemini or AiProviderKind.WebGemini) => 88,
            (AiAgentRole.MasterSynthesizer, AiProviderKind.OpenAI or AiProviderKind.WebChatGpt) => 86,
            (_, AiProviderKind.OpenRouter) => 66,
            _ => 60
        };
        // Нормоконтролю не нужна «тяжёлая» модель — быстрые варианты не штрафуются.
        if (role == AiAgentRole.NormsAndSafetyVerifier && small) tier = 2;
        if (role == AiAgentRole.ChiefArchitect && !m.SupportsVision) tier -= 20;
        if (m.IsWeb) tier -= 4;
        return baseScore + tier;
    }
}

/// <summary>Вызов модели агентом: назначение, промпты, изображения, критерий приёма ответа веб-моста.</summary>
public sealed record AgentInvocation(AgentAssignment Assignment, string SystemPrompt, string UserPrompt, IReadOnlyList<AiImage> Images,
    AiRequestContext Context, int MaxOutputTokens = 16000, Func<string, bool>? Accept = null, IProgress<AiStreamProgress>? Progress = null);

/// <summary>Шлюз моделей агентов (реальный — провайдеры и веб-мост; в автотесте — сценарный).</summary>
public interface IAgentModelGateway
{
    Task<AiResponse> InvokeAsync(AgentInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// Реальный шлюз: облачные API (Gemini, OpenAI/OpenRouter, Claude), сервер Ollama и встроенный Qwen через
/// AIOrchestrator.ProviderFor, веб-аккаунты — через WebAccountBridgeProvider.AskAsync (панель WebView2 вставляет промпт и
/// ловит ответ). Модели без зрения получают задачу без изображений; рассуждения &lt;think&gt; всегда отделяются от ответа.
/// </summary>
public sealed class AgentModelGateway(AIOrchestrator ai, WebAccountBridgeProvider web) : IAgentModelGateway
{
    /// <summary>Агенту нужен веб-аккаунт — ViewModel открывает панель WebView2 нужного сайта.</summary>
    public event EventHandler<AiProviderKind>? WebSessionRequested;

    public async Task<AiResponse> InvokeAsync(AgentInvocation invocation, CancellationToken cancellationToken)
    {
        var model = invocation.Assignment.Model;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (model.IsWeb)
        {
            WebSessionRequested?.Invoke(this, model.Provider);
            string prompt = SystemPrompts.BuildWebPrompt(invocation.SystemPrompt, invocation.UserPrompt);
            var ctx = invocation.Context.WithModel(model);
            var text = await web.AskAsync(ctx, prompt, invocation.Accept, cancellationToken);
            var (answer, reasoning) = ReasoningText.Separate(text);
            return new AiResponse(answer.Length > 0 ? answer : text, model.DisplayName, sw.Elapsed, Reasoning: reasoning.Length > 0 ? reasoning : null);
        }
        var request = new AiRequest
        {
            Model = model,
            SystemPrompt = invocation.SystemPrompt,
            UserPrompt = invocation.UserPrompt,
            Images = model.SupportsVision ? invocation.Images : [],
            Temperature = invocation.Assignment.Temperature,
            MaxOutputTokens = invocation.MaxOutputTokens,
            Progress = invocation.Progress
        };
        AiResponse response;
        try
        {
            response = await ai.ProviderFor(model).GenerateAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or System.Text.Json.JsonException
                                       or HttpRequestException or IOException)
        {
            // Неожиданная форма ответа API или обрыв сети — сбой роли (агент помечается, Совет продолжает по C#-решению),
            // а не необработанное исключение всего прогона.
            throw new AiProviderException("AIA-AGENT-RESPONSE", $"{model.DisplayName}: {ex.Message}", ex.ToString(), ex);
        }
        // Облачные и встроенные модели тоже могут прислать <think>-блоки (DeepSeek R1 через OpenRouter и т.п.).
        if (response.HasReasoning || !ReasoningText.HasReasoningTags(response.Text)) return response;
        var (clean, thought) = ReasoningText.Separate(response.Text);
        return response with { Text = clean, Reasoning = thought };
    }
}
