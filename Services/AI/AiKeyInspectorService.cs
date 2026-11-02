using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIArchitectStudio.Services.AI;

/// <summary>Результат проверки API-ключа: валидность и список доступных моделей.</summary>
public sealed record AiKeyInspection(AiProviderKind Provider, bool IsValid, IReadOnlyList<AiModelOption> Models, string Message, string? ErrorCode = null, string? Details = null);

/// <summary>
/// Умный анализатор API-ключей: определяет провайдера по префиксу ключа и реальным HTTP-запросом
/// к API провайдера проверяет ключ и загружает актуальный список моделей в AiModelRegistry.
/// </summary>
public sealed partial class AiKeyInspectorService(SettingsService settings, AiModelRegistry registry)
{
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>Проверенный список Claude, если /v1/models недоступен для ключа (после тестового пинга).</summary>
    private static readonly string[] AnthropicFallbackModels =
        ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-sonnet-4-20250514"];

    private static readonly string[] OpenAiExcluded =
        ["whisper", "tts", "dall-e", "embedding", "audio", "realtime", "transcribe", "search", "image", "moderation", "-pro", "codex", "deep-research", "instruct"];

    private static readonly string[] GeminiExcluded =
        ["embedding", "aqa", "tts", "image", "imagen", "veo", "native-audio", "live", "gemma", "learnlm", "robotics", "computer-use"];

    private static readonly string[] OpenRouterFamilies =
        ["openai/", "anthropic/", "google/", "deepseek/", "qwen/", "meta-llama/", "mistralai/", "x-ai/", "moonshotai/", "z-ai/"];

    /// <summary>Определяет провайдера по префиксу: AIza → Gemini, sk-ant- → Claude, sk-or- → OpenRouter, sk- → OpenAI.</summary>
    public static AiProviderKind? DetectProvider(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim();
        if (key.StartsWith("AIza", StringComparison.Ordinal)) return AiProviderKind.Gemini;
        if (key.StartsWith("sk-ant-", StringComparison.Ordinal)) return AiProviderKind.Anthropic;
        if (key.StartsWith("sk-or-", StringComparison.Ordinal)) return AiProviderKind.OpenRouter;
        if (key.StartsWith("sk-", StringComparison.Ordinal)) return AiProviderKind.OpenAI;
        return null;
    }

    /// <summary>Фоновая проверка всех сохранённых ключей при запуске приложения.</summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task>();
        foreach (var provider in AiModelCatalog.CloudProviders)
        {
            var key = settings.GetApiKey(provider);
            if (string.IsNullOrWhiteSpace(key))
            {
                registry.ClearCloudModels(provider);
                registry.SetStatus(AiKeyStatus.NotSet(provider));
                continue;
            }
            tasks.Add(InspectAsync(provider, key, cancellationToken));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Проверяет ключ реальным запросом к API и публикует модели в реестр.</summary>
    public async Task<AiKeyInspection> InspectAsync(AiProviderKind provider, string key, CancellationToken cancellationToken = default)
    {
        key = key.Trim();
        registry.SetStatus(new AiKeyStatus(provider, AiKeyState.Checking, "Проверка…"));
        AiKeyInspection result;
        try
        {
            var models = provider switch
            {
                AiProviderKind.Gemini => await ListGeminiAsync(key, cancellationToken).ConfigureAwait(false),
                AiProviderKind.OpenAI => await ListOpenAiAsync(key, cancellationToken).ConfigureAwait(false),
                AiProviderKind.Anthropic => await ListAnthropicAsync(key, cancellationToken).ConfigureAwait(false),
                AiProviderKind.OpenRouter => await ListOpenRouterAsync(key, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(provider))
            };
            if (models.Count == 0)
            {
                result = new AiKeyInspection(provider, true, models, "Ключ действителен, но подходящих текстовых моделей не найдено");
                registry.ClearCloudModels(provider);
            }
            else
            {
                result = new AiKeyInspection(provider, true, models, $"Проверен · моделей: {models.Count}");
                registry.SetCloudModels(provider, models);
            }
            registry.SetStatus(new AiKeyStatus(provider, AiKeyState.Valid, result.Message, models.Count, DateTime.Now));
        }
        catch (AiProviderException ex)
        {
            bool invalid = ex.Code is "AIA-KEY-INVALID" or "AIA-HTTP-401" or "AIA-HTTP-403";
            result = new AiKeyInspection(provider, false, [], ex.Message, ex.Code, ex.Details);
            if (invalid) registry.ClearCloudModels(provider);
            registry.SetStatus(new AiKeyStatus(provider, invalid ? AiKeyState.Invalid : AiKeyState.Error, $"{ex.Code}: {ex.Message}", 0, DateTime.Now));
        }
        catch (OperationCanceledException)
        {
            result = new AiKeyInspection(provider, false, [], "Проверка отменена");
            registry.SetStatus(new AiKeyStatus(provider, AiKeyState.Unchecked, result.Message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or System.Text.Json.JsonException)
        {
            // Ответ API в неожиданном формате (изменился контракт, прокси вернул чужой JSON) — статус «Ошибка», а не вечная «Проверка…».
            result = new AiKeyInspection(provider, false, [], "Ответ API в неожиданном формате: " + ex.Message, "AIA-KEY-FORMAT", ex.ToString());
            registry.SetStatus(new AiKeyStatus(provider, AiKeyState.Error, "AIA-KEY-FORMAT: " + ex.Message, 0, DateTime.Now));
        }
        return result;
    }

    // ───────────────────────────── Google Gemini ─────────────────────────────

    private static async Task<List<AiModelOption>> ListGeminiAsync(string key, CancellationToken ct)
    {
        var names = new List<string>();
        string? pageToken = null;
        for (int page = 0; page < 5; page++)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key)}&pageSize=1000" +
                      (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var doc = await AiHttp.SendAsync(request, "Gemini", AiHttp.InspectionTimeout, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var m in models.EnumerateArray())
                {
                    var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    if (name.StartsWith("models/", StringComparison.Ordinal)) name = name[7..];
                    bool generates = m.TryGetProperty("supportedGenerationMethods", out var methods) &&
                                     methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
                    if (!generates || !name.StartsWith("gemini", StringComparison.Ordinal)) continue;
                    if (GeminiExcluded.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                    names.Add(name);
                }
            }
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(pageToken)) break;
        }

        return names.Distinct()
            .OrderByDescending(GeminiVersion)
            .ThenBy(n => n.Contains("preview", StringComparison.Ordinal) || n.Contains("exp", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(n => n.Contains("pro", StringComparison.Ordinal) ? 0 : n.Contains("flash-lite", StringComparison.Ordinal) ? 2 : 1)
            .ThenBy(n => n, StringComparer.Ordinal)
            .Select(n => AiModelCatalog.Cloud(AiProviderKind.Gemini, n))
            .ToList();
    }

    private static double GeminiVersion(string name)
    {
        var m = GeminiVersionRegex().Match(name);
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    [GeneratedRegex(@"gemini-(\d+(?:\.\d+)?)")]
    private static partial Regex GeminiVersionRegex();

    // ───────────────────────────── OpenAI / ChatGPT ─────────────────────────────

    private static async Task<List<AiModelOption>> ListOpenAiAsync(string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var doc = await AiHttp.SendAsync(request, "OpenAI", AiHttp.InspectionTimeout, ct).ConfigureAwait(false);

        var models = new List<(string Id, long Created)>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var i) ? i.GetString() ?? string.Empty : string.Empty;
                long created = m.TryGetProperty("created", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
                if (!IsOpenAiChatModel(id)) continue;
                models.Add((id, created));
            }
        }
        return models
            .OrderByDescending(m => m.Created)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => AiModelCatalog.Cloud(AiProviderKind.OpenAI, m.Id, OpenAiSupportsVision(m.Id)))
            .ToList();
    }

    public static bool IsOpenAiChatModel(string id)
    {
        bool family = id.StartsWith("gpt-4o", StringComparison.Ordinal) || id.StartsWith("gpt-4.1", StringComparison.Ordinal) ||
                      id.StartsWith("gpt-5", StringComparison.Ordinal) || id.StartsWith("chatgpt-4o", StringComparison.Ordinal) ||
                      Regex.IsMatch(id, @"^o[134](-|$)");
        return family && !OpenAiExcluded.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    public static bool OpenAiSupportsVision(string id) =>
        !(id.StartsWith("o1-mini", StringComparison.Ordinal) || id.StartsWith("o3-mini", StringComparison.Ordinal));

    // ───────────────────────────── Anthropic / Claude ─────────────────────────────

    private static async Task<List<AiModelOption>> ListAnthropicAsync(string key, CancellationToken ct)
    {
        try
        {
            var ids = new List<string>();
            string? afterId = null;
            for (int page = 0; page < 5; page++)
            {
                var url = "https://api.anthropic.com/v1/models?limit=100" + (afterId is null ? string.Empty : "&after_id=" + Uri.EscapeDataString(afterId));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", AnthropicVersion);
                using var doc = await AiHttp.SendAsync(request, "Anthropic", AiHttp.InspectionTimeout, ct).ConfigureAwait(false);

                if (doc.RootElement.TryGetProperty("data", out var data))
                    foreach (var m in data.EnumerateArray())
                        if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s) ids.Add(s);

                bool hasMore = doc.RootElement.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
                afterId = doc.RootElement.TryGetProperty("last_id", out var last) ? last.GetString() : null;
                if (!hasMore || afterId is null) break;
            }
            return ids.Distinct().Select(id => AiModelCatalog.Cloud(AiProviderKind.Anthropic, id)).ToList();
        }
        catch (AiProviderException ex) when (ex.Code is not ("AIA-KEY-INVALID" or "AIA-HTTP-401" or "AIA-NET" or "AIA-TIMEOUT"))
        {
            // Эндпоинт /v1/models недоступен для ключа: лёгкий тестовый пинг и проверенный список моделей.
            await PingAnthropicAsync(key, ct).ConfigureAwait(false);
            return AnthropicFallbackModels.Select(id => AiModelCatalog.Cloud(AiProviderKind.Anthropic, id)).ToList();
        }
    }

    private static async Task PingAnthropicAsync(string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = AiHttp.Json(new
            {
                model = "claude-haiku-5-5",
                max_tokens = 1,
                messages = new[] { new { role = "user", content = "ping" } }
            })
        };
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        using var _ = await AiHttp.SendAsync(request, "Anthropic", AiHttp.InspectionTimeout, ct).ConfigureAwait(false);
    }

    // ───────────────────────────── OpenRouter ─────────────────────────────

    private static async Task<List<AiModelOption>> ListOpenRouterAsync(string key, CancellationToken ct)
    {
        // Каталог OpenRouter публичный, поэтому ключ проверяется отдельным запросом сведений о ключе.
        using (var check = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key"))
        {
            check.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var _ = await AiHttp.SendAsync(check, "OpenRouter", AiHttp.InspectionTimeout, ct).ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var doc = await AiHttp.SendAsync(request, "OpenRouter", AiHttp.InspectionTimeout, ct).ConfigureAwait(false);

        var list = new List<(string Id, long Created, bool Vision, int Family)>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var i) ? i.GetString() ?? string.Empty : string.Empty;
                int family = Array.FindIndex(OpenRouterFamilies, f => id.StartsWith(f, StringComparison.Ordinal));
                if (family < 0) continue;

                bool textOut = true, vision = false;
                if (m.TryGetProperty("architecture", out var arch))
                {
                    if (arch.TryGetProperty("output_modalities", out var outMods) && outMods.ValueKind == JsonValueKind.Array)
                        textOut = outMods.EnumerateArray().Any(x => x.GetString() == "text") && !outMods.EnumerateArray().Any(x => x.GetString() == "image");
                    if (arch.TryGetProperty("input_modalities", out var inMods) && inMods.ValueKind == JsonValueKind.Array)
                        vision = inMods.EnumerateArray().Any(x => x.GetString() == "image");
                }
                if (!textOut) continue;
                long created = m.TryGetProperty("created", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
                list.Add((id, created, vision, family));
            }
        }

        return list
            .GroupBy(x => x.Family)
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderByDescending(x => x.Created).Take(12))
            .Select(x => AiModelCatalog.Cloud(AiProviderKind.OpenRouter, x.Id, x.Vision))
            .ToList();
    }
}
