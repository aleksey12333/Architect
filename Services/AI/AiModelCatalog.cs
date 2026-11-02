namespace AIArchitectStudio.Services.AI;

/// <summary>Источник ответа ИИ: локальная модель, облачный API или обычный веб-аккаунт через WebView2.</summary>
public enum AiProviderKind
{
    Local,
    Gemini,
    OpenAI,
    Anthropic,
    OpenRouter,
    WebChatGpt,
    WebClaude,
    WebGemini,

    /// <summary>Локальный ИИ-сервер Ollama (http://localhost:11434): модели пользователя, загрузка и выгрузка весов — на стороне сервера.</summary>
    Ollama
}

/// <summary>Пункт меню выбора модели в Spotlight ИИ-доке.</summary>
/// <param name="Id">Уникальный ключ, сохраняемый в settings.json (например, <c>gemini:gemini-2.5-flash</c>).</param>
/// <param name="ModelId">Имя модели у провайдера (или имя .gguf файла для локальных моделей).</param>
public sealed record AiModelOption(string Id, AiProviderKind Provider, string ModelId, string DisplayName, string ShortName, bool SupportsVision)
{
    /// <summary>Встроенная модель LLamaSharp (.gguf из LocalModels, контекст 2048 — компактные промпты).</summary>
    public bool IsLocal => Provider == AiProviderKind.Local;

    /// <summary>Модель локального сервера Ollama (полные промпты, большой контекст, потоковая генерация).</summary>
    public bool IsOllama => Provider == AiProviderKind.Ollama;

    /// <summary>Работает на компьютере пользователя (LLamaSharp или Ollama) — без сети и ключей.</summary>
    public bool IsOnDevice => IsLocal || IsOllama;

    public bool IsWeb => Provider is AiProviderKind.WebChatGpt or AiProviderKind.WebClaude or AiProviderKind.WebGemini;
    public bool IsCloud => !IsOnDevice && !IsWeb;

    /// <summary>Число параметров модели, млрд (Ollama: details.parameter_size; 0 — неизвестно).</summary>
    public double ParameterBillions { get; init; }

    /// <summary>Размер весов на диске, ГБ (Ollama: size из /api/tags; 0 — неизвестно) — оценка требуемой памяти.</summary>
    public double SizeGb { get; init; }

    /// <summary>Возможности модели по данным сервера: completion, vision, tools, thinking…</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Рассуждающая модель (отдаёт «внутренние рассуждения» &lt;think&gt; или поле thinking).</summary>
    public bool IsReasoning => Capabilities.Contains("thinking") || ReasoningText.IsReasoningFamily(ModelId);
}

/// <summary>Каталог моделей: постоянные локальные и веб-пункты + правила именования облачных моделей.</summary>
public static class AiModelCatalog
{
    /// <summary>Файлы локальных моделей в C:\AIArchitectStudio\LocalModels\ (LLamaSharp Vulkan → CPU).</summary>
    public const string Qwen7BFile = "qwen2.5-coder-7b-instruct-q4_k_m.gguf";
    public const string Qwen3BFile = "qwen2.5-coder-3b-instruct-q4_k_m.gguf";

    /// <summary>Каталог моделей по умолчанию, если папка не найдена поиском от каталога приложения.</summary>
    public const string DefaultLocalModelsDirectory = @"C:\AIArchitectStudio\LocalModels";

    public const string LocalQwen3BId = "local:qwen3b";
    public const string LocalQwen7BId = "local:qwen7b";
    public const string WebChatGptId = "web:chatgpt";
    public const string WebClaudeId = "web:claude";
    public const string WebGeminiId = "web:gemini";

    public const string DefaultModelId = LocalQwen3BId;

    public static readonly AiModelOption LocalQwen3B =
        new(LocalQwen3BId, AiProviderKind.Local, Qwen3BFile, "Qwen 2.5 Coder 3B", "Qwen 3B", false);

    public static readonly AiModelOption LocalQwen7B =
        new(LocalQwen7BId, AiProviderKind.Local, Qwen7BFile, "Qwen 2.5 Coder 7B", "Qwen 7B", false);

    public static readonly AiModelOption WebChatGpt =
        new(WebChatGptId, AiProviderKind.WebChatGpt, "chatgpt.com", "ChatGPT (chatgpt.com)", "Веб · ChatGPT", true);

    public static readonly AiModelOption WebClaude =
        new(WebClaudeId, AiProviderKind.WebClaude, "claude.ai", "Claude (claude.ai)", "Веб · Claude", true);

    public static readonly AiModelOption WebGemini =
        new(WebGeminiId, AiProviderKind.WebGemini, "gemini.google.com", "Gemini (gemini.google.com)", "Веб · Gemini", true);

    public static readonly IReadOnlyList<AiModelOption> WebOptions = [WebChatGpt, WebClaude, WebGemini];

    /// <summary>Облачные провайдеры с API-ключами в порядке показа в меню.</summary>
    public static readonly IReadOnlyList<AiProviderKind> CloudProviders =
        [AiProviderKind.Gemini, AiProviderKind.OpenAI, AiProviderKind.Anthropic, AiProviderKind.OpenRouter];

    /// <summary>Префикс провайдера в меню («Gemini · …», «ChatGPT · …», «Claude · …»).</summary>
    public static string ProviderTitle(AiProviderKind provider) => provider switch
    {
        AiProviderKind.Gemini => "Gemini",
        AiProviderKind.OpenAI => "ChatGPT",
        AiProviderKind.Anthropic => "Claude",
        AiProviderKind.OpenRouter => "OpenRouter",
        AiProviderKind.WebChatGpt => "ChatGPT",
        AiProviderKind.WebClaude => "Claude",
        AiProviderKind.WebGemini => "Gemini",
        AiProviderKind.Ollama => "Локальный интеллект",
        _ => "Qwen"
    };

    /// <summary>Полное имя провайдера для окна настроек.</summary>
    public static string ProviderLongTitle(AiProviderKind provider) => provider switch
    {
        AiProviderKind.Gemini => "Google Gemini",
        AiProviderKind.OpenAI => "OpenAI / ChatGPT",
        AiProviderKind.Anthropic => "Anthropic / Claude",
        AiProviderKind.OpenRouter => "OpenRouter",
        _ => ProviderTitle(provider)
    };

    /// <summary>Глиф Segoe Fluent Icons для провайдера.</summary>
    public static string ProviderGlyph(AiProviderKind provider) => provider switch
    {
        AiProviderKind.Local => "\uE7F8",
        AiProviderKind.Gemini => "\uE753",
        AiProviderKind.OpenAI => "\uE8BD",
        AiProviderKind.Anthropic => "\uE8F2",
        AiProviderKind.OpenRouter => "\uE968",
        AiProviderKind.Ollama => "\uE950",
        _ => "\uE774"
    };

    /// <summary>Префикс ключа в settings.json для облачного провайдера.</summary>
    public static string IdPrefix(AiProviderKind provider) => provider switch
    {
        AiProviderKind.Gemini => "gemini",
        AiProviderKind.OpenAI => "openai",
        AiProviderKind.Anthropic => "anthropic",
        AiProviderKind.OpenRouter => "openrouter",
        AiProviderKind.Local => "local",
        AiProviderKind.Ollama => "ollama",
        _ => "web"
    };

    /// <summary>
    /// Модель сервера Ollama: «Ollama · deepseek-r1:14b». Зрение, рассуждения и размер — по данным /api/tags
    /// (без них — по имени семейства).
    /// </summary>
    public static AiModelOption Ollama(string name, double parameterBillions = 0, IReadOnlyList<string>? capabilities = null)
    {
        capabilities ??= [];
        string tail = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        string shortName = tail.Length > 24 ? tail[..24] : tail;
        bool vision = capabilities.Contains("vision") || capabilities.Count == 0 && OllamaVisionFamily(name);
        return new AiModelOption("ollama:" + name, AiProviderKind.Ollama, name, "Локальный интеллект · " + name, shortName, vision)
        {
            ParameterBillions = parameterBillions > 0 ? parameterBillions : GuessBillions(name),
            Capabilities = capabilities
        };
    }

    private static bool OllamaVisionFamily(string name)
    {
        string n = name.ToLowerInvariant();
        return n.Contains("llava") || n.Contains("vision") || n.Contains("gemma3") || n.Contains("gemma4") || n.Contains("qwen2.5vl") || n.Contains("-vl") || n.Contains("minicpm-v");
    }

    /// <summary>Размер по тегу «:14b» / «-7b» (если сервер не сообщил parameter_size).</summary>
    public static double GuessBillions(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name.ToLowerInvariant(), @"(\d+(?:\.\d+)?)b\b");
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 0;
    }

    /// <summary>Облачная модель: «Gemini · gemini-2.5-flash».</summary>
    public static AiModelOption Cloud(AiProviderKind provider, string modelId, bool supportsVision = true)
    {
        string title = ProviderTitle(provider);
        return new AiModelOption($"{IdPrefix(provider)}:{modelId}", provider, modelId, $"{title} · {modelId}", ShortCloudName(provider, modelId), supportsVision);
    }

    /// <summary>Дополнительная локальная .gguf модель, найденная в папке LocalModels.</summary>
    public static AiModelOption LocalFile(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return new AiModelOption("local:file:" + fileName, AiProviderKind.Local, fileName, name, name.Length > 18 ? name[..18] : name, false);
    }

    private static string ShortCloudName(AiProviderKind provider, string modelId)
    {
        var name = modelId.Contains('/') ? modelId[(modelId.LastIndexOf('/') + 1)..] : modelId;
        if (name.StartsWith("models/", StringComparison.Ordinal)) name = name[7..];
        return name.Length > 22 ? name[..22] : name;
    }

    /// <summary>Перевод старых названий моделей (Этап 2) в новые ключи.</summary>
    public static string MigrateLegacyId(string? value) => value switch
    {
        null or "" => DefaultModelId,
        "Gemini 2.5 Flash" => "gemini:gemini-2.5-flash",
        "Gemini 2.5 Pro" => "gemini:gemini-2.5-pro",
        "Local · Qwen2.5-Coder 7B (GGUF)" => LocalQwen7BId,
        "Local · Qwen2.5-Coder 3B (GGUF)" => LocalQwen3BId,
        _ => value.Contains(':') ? value : DefaultModelId
    };

    /// <summary>Восстанавливает пункт меню по сохранённому ключу (если облачный список ещё не загружен).</summary>
    public static AiModelOption? FromId(string id)
    {
        if (id == LocalQwen3BId) return LocalQwen3B;
        if (id == LocalQwen7BId) return LocalQwen7B;
        if (WebOptions.FirstOrDefault(o => o.Id == id) is { } web) return web;
        if (id.StartsWith("local:file:", StringComparison.Ordinal)) return LocalFile(id["local:file:".Length..]);
        if (id.StartsWith("ollama:", StringComparison.Ordinal) && id.Length > 7) return Ollama(id[7..]);
        int colon = id.IndexOf(':');
        if (colon <= 0) return null;
        var prefix = id[..colon];
        var model = id[(colon + 1)..];
        foreach (var provider in CloudProviders)
            if (IdPrefix(provider) == prefix) return Cloud(provider, model);
        return null;
    }
}
