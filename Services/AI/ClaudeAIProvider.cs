using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AIArchitectStudio.Services.AI;

/// <summary>Anthropic Messages API (/v1/messages) с блоком image base64 для Vision.</summary>
public sealed class ClaudeAIProvider(SettingsService settings) : IAiProvider
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>Модели, у которых включается серверный откат при отказе (refusal) классификатора безопасности.</summary>
    private static readonly string[] FallbackModels = ["claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5", "claude-fable-5-1"];

    public async Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var key = settings.GetApiKey(AiProviderKind.Anthropic);
        if (string.IsNullOrWhiteSpace(key))
            throw new AiProviderException("AIA-KEY-MISSING", "Не задан API-ключ Anthropic (Claude). Откройте «Настройки» и вставьте ключ sk-ant-…");

        string model = request.Model.ModelId;
        bool useFallbacks = FallbackModels.Contains(model);
        var sw = Stopwatch.StartNew();
        JsonDocument doc;
        try
        {
            doc = await SendAsync(request, key, useFallbacks, cancellationToken).ConfigureAwait(false);
        }
        catch (AiProviderException ex) when (useFallbacks && ex.Code == "AIA-HTTP-400" && ex.Details.Contains("fallback", StringComparison.OrdinalIgnoreCase))
        {
            // Аккаунт без доступа к серверным откатам — повторяем обычный запрос.
            doc = await SendAsync(request, key, false, cancellationToken).ConfigureAwait(false);
        }
        sw.Stop();

        using (doc)
        {
            var root = doc.RootElement;
            string? stop = root.TryGetProperty("stop_reason", out var s) ? s.GetString() : null;
            if (stop == "refusal")
            {
                string category = root.TryGetProperty("stop_details", out var d) && d.ValueKind == JsonValueKind.Object &&
                                  d.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "не указана";
                throw new AiProviderException("AIA-REFUSAL", $"Claude отклонил запрос (категория: {category}). Переформулируйте задачу.", root.GetRawText());
            }

            var text = new StringBuilder();
            if (root.TryGetProperty("content", out var content))
                foreach (var block in content.EnumerateArray())
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var tx))
                        text.Append(tx.GetString());

            if (text.Length == 0)
                throw new AiProviderException("AIA-EMPTY", $"Claude вернул пустой ответ ({stop ?? "нет данных"}).", root.GetRawText());

            long? input = null, output = null;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var iv)) input = iv;
                if (usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov)) output = ov;
            }
            string served = root.TryGetProperty("model", out var m) ? m.GetString() ?? model : model;
            return new AiResponse(text.ToString(), $"Claude · {served}", sw.Elapsed, "Anthropic API", input, output);
        }
    }

    private static async Task<JsonDocument> SendAsync(AiRequest request, string key, bool useFallbacks, CancellationToken ct)
    {
        var content = new List<object>();
        foreach (var image in request.Images)
        {
            if (image.Label is { Length: > 0 } label) content.Add(new { type = "text", text = label });
            content.Add(new { type = "image", source = new { type = "base64", media_type = image.MimeType, data = image.Base64 } });
        }
        content.Add(new { type = "text", text = request.UserPrompt });

        var body = new Dictionary<string, object>
        {
            ["model"] = request.Model.ModelId,
            ["max_tokens"] = 16000,
            ["system"] = request.SystemPrompt,
            ["messages"] = new[] { new { role = "user", content } }
        };
        // Параметры выборки поддерживают только модели поколения Claude 3; новые модели отклоняют temperature.
        if (request.Model.ModelId.StartsWith("claude-3", StringComparison.Ordinal)) body["temperature"] = request.Temperature;
        if (useFallbacks) body["fallbacks"] = "default";

        using var http = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = AiHttp.Json(body) };
        http.Headers.Add("x-api-key", key);
        http.Headers.Add("anthropic-version", "2023-06-01");
        if (useFallbacks) http.Headers.Add("anthropic-beta", FallbackBeta);
        return await AiHttp.SendAsync(http, "Anthropic", AiHttp.GenerationTimeout, ct).ConfigureAwait(false);
    }
}
