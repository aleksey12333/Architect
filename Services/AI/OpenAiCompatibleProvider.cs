using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIArchitectStudio.Services.AI;

/// <summary>
/// Единый провайдер OpenAI-совместимого API /chat/completions: OpenAI (ChatGPT) и OpenRouter.
/// Изображения передаются блоком image_url с data-URL base64.
/// </summary>
public sealed class OpenAiCompatibleProvider(SettingsService settings) : IAiProvider
{
    public async Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var provider = request.Model.Provider;
        bool openRouter = provider == AiProviderKind.OpenRouter;
        string title = openRouter ? "OpenRouter" : "OpenAI";

        var key = settings.GetApiKey(provider);
        if (string.IsNullOrWhiteSpace(key))
            throw new AiProviderException("AIA-KEY-MISSING", $"Не задан API-ключ {title}. Откройте «Настройки» и вставьте ключ {(openRouter ? "sk-or-…" : "sk-…")}");

        object userContent = request.UserPrompt;
        if (request.HasImage)
        {
            var parts = new List<object> { new { type = "text", text = request.UserPrompt } };
            foreach (var image in request.Images)
            {
                if (image.Label is { Length: > 0 } label) parts.Add(new { type = "text", text = label });
                parts.Add(new { type = "image_url", image_url = new { url = $"data:{image.MimeType};base64,{image.Base64}" } });
            }
            userContent = parts;
        }

        string model = request.Model.ModelId;
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = userContent }
            }
        };

        if (openRouter)
        {
            body["max_tokens"] = 8192;
            body["temperature"] = request.Temperature;
        }
        else
        {
            // Модели рассуждений (o-серия, gpt-5) не принимают temperature и считают лимит через max_completion_tokens.
            bool reasoning = model.StartsWith('o') || model.StartsWith("gpt-5", StringComparison.Ordinal);
            body["max_completion_tokens"] = 16000;
            if (!reasoning) body["temperature"] = request.Temperature;
            body["response_format"] = new { type = "json_object" };
        }

        var url = openRouter ? "https://openrouter.ai/api/v1/chat/completions" : "https://api.openai.com/v1/chat/completions";
        using var http = new HttpRequestMessage(HttpMethod.Post, url) { Content = AiHttp.Json(body) };
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (openRouter) http.Headers.Add("X-Title", "AI Architect Studio");

        var sw = Stopwatch.StartNew();
        using var doc = await AiHttp.SendAsync(http, title, AiHttp.GenerationTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        var root = doc.RootElement;
        if (root.TryGetProperty("error", out _))
            throw AiHttp.FromStatus(root.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) ? c : 500,
                root.GetRawText(), title, $"POST {url}");

        var text = new StringBuilder();
        string? finish = null, refusal = null;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            finish = first.TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
            if (first.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String) text.Append(content.GetString());
                    else if (content.ValueKind == JsonValueKind.Array)
                        foreach (var part in content.EnumerateArray())
                            if (part.TryGetProperty("text", out var t)) text.Append(t.GetString());
                }
                if (message.TryGetProperty("refusal", out var r) && r.ValueKind == JsonValueKind.String) refusal = r.GetString();
            }
        }

        if (!string.IsNullOrEmpty(refusal))
            throw new AiProviderException("AIA-REFUSAL", $"{title}: модель отказалась выполнять запрос — {refusal}", root.GetRawText());
        if (text.Length == 0)
            throw new AiProviderException("AIA-EMPTY",
                finish == "content_filter" ? $"{title}: ответ заблокирован фильтром контента." : $"{title}: пустой ответ модели ({finish ?? "нет данных"}).",
                root.GetRawText());

        long? input = null, output = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt64(out var pv)) input = pv;
            if (usage.TryGetProperty("completion_tokens", out var cc) && cc.TryGetInt64(out var cv)) output = cv;
        }
        return new AiResponse(text.ToString(), request.Model.DisplayName, sw.Elapsed, title, input, output);
    }
}
