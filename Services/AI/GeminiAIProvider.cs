using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AIArchitectStudio.Services.AI;

/// <summary>Google Gemini REST API (generateContent) с поддержкой Vision через inlineData base64.</summary>
public sealed class GeminiAIProvider(SettingsService settings) : IAiProvider
{
    public async Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var key = settings.GetApiKey(AiProviderKind.Gemini);
        if (string.IsNullOrWhiteSpace(key))
            throw new AiProviderException("AIA-KEY-MISSING", "Не задан API-ключ Google Gemini. Откройте «Настройки» и вставьте ключ AIza…");

        var parts = new List<object> { new { text = request.UserPrompt } };
        foreach (var image in request.Images)
        {
            if (image.Label is { Length: > 0 } label) parts.Add(new { text = label });
            parts.Add(new { inlineData = new { mimeType = image.MimeType, data = image.Base64 } });
        }

        string model = request.Model.ModelId;
        bool legacy = model.Contains("1.5", StringComparison.Ordinal) || model.Contains("2.0", StringComparison.Ordinal);
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = request.SystemPrompt } } },
            contents = new[] { new { role = "user", parts } },
            generationConfig = new
            {
                temperature = request.Temperature,
                maxOutputTokens = legacy ? 8192 : 32768,
                responseMimeType = "application/json"
            }
        };

        using var http = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = AiHttp.Json(body)
        };
        http.Headers.Add("x-goog-api-key", key);

        var sw = Stopwatch.StartNew();
        using var doc = await AiHttp.SendAsync(http, "Gemini", AiHttp.GenerationTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        var root = doc.RootElement;
        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var block))
            throw new AiProviderException("AIA-REFUSAL", $"Gemini заблокировал запрос фильтром безопасности ({block.GetString()}). Переформулируйте задачу.", root.GetRawText());

        var text = new StringBuilder();
        string? finish = null;
        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var first = candidates[0];
            finish = first.TryGetProperty("finishReason", out var f) ? f.GetString() : null;
            if (first.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var outParts))
            {
                foreach (var part in outParts.EnumerateArray())
                {
                    if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                    if (part.TryGetProperty("text", out var t)) text.Append(t.GetString());
                }
            }
        }

        if (text.Length == 0)
        {
            string reason = finish switch
            {
                "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" => "ответ заблокирован фильтром безопасности",
                "MAX_TOKENS" => "модель исчерпала лимит токенов до начала ответа",
                "RECITATION" => "ответ остановлен фильтром цитирования",
                _ => "пустой ответ модели"
            };
            throw new AiProviderException("AIA-EMPTY", $"Gemini: {reason}.", root.GetRawText());
        }

        long? input = null, output = null;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            if (usage.TryGetProperty("promptTokenCount", out var p) && p.TryGetInt64(out var pv)) input = pv;
            if (usage.TryGetProperty("candidatesTokenCount", out var c) && c.TryGetInt64(out var cv)) output = cv;
        }
        return new AiResponse(text.ToString(), request.Model.DisplayName, sw.Elapsed, "Google AI Studio", input, output);
    }
}
