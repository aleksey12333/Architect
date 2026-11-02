using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIArchitectStudio.Services.AI;

/// <summary>Изображение для Vision: байты, MIME-тип и подпись (например, «Лист 3 из 12»), которая передаётся модели перед картинкой.</summary>
public sealed record AiImage(byte[] Bytes, string MimeType, string? Label = null)
{
    public string Base64 => Convert.ToBase64String(Bytes);
}

/// <summary>Запрос к любому провайдеру ИИ: системное ТЗ + задача пользователя + изображения (Vision, листы PDF).</summary>
public sealed class AiRequest
{
    public required AiModelOption Model { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    public IReadOnlyList<AiImage> Images { get; init; } = [];
    public double Temperature { get; init; } = 0.1;
    public int MaxOutputTokens { get; init; } = 16000;

    /// <summary>Потоковый прогресс (провайдеры с потоковой генерацией — Ollama): рассуждения и ответ по мере генерации.</summary>
    public IProgress<AiStreamProgress>? Progress { get; init; }

    public bool HasImage => Images.Count > 0;
}

/// <summary>
/// Текст ответа модели и служебные сведения для отчёта. <see cref="Text"/> — только ответ (без блоков &lt;think&gt;),
/// <see cref="Reasoning"/> — «внутренние рассуждения» рассуждающих моделей (deepseek-r1 и т.п.), если они были.
/// </summary>
public sealed record AiResponse(string Text, string ModelName, TimeSpan Elapsed, string? Backend = null, long? InputTokens = null, long? OutputTokens = null,
    string? Reasoning = null)
{
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);
}

public interface IAiProvider
{
    Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken);
}

/// <summary>Понятная русскоязычная ошибка ИИ с кодом для кнопки «Код ошибки» и полным логом.</summary>
public sealed class AiProviderException(string code, string message, string? details = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public string Details { get; } = details ?? message;
}

/// <summary>Общий HTTP-слой облачных провайдеров: один HttpClient, таймауты и перевод HTTP-ошибок на русский.</summary>
internal static class AiHttp
{
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("AIArchitectStudio", "3.0") } }
    };

    public static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(25);

    public static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>Отправляет запрос и возвращает JSON-ответ; любые сбои превращаются в AiProviderException с кодом.</summary>
    public static async Task<JsonDocument> SendAsync(HttpRequestMessage request, string provider, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        HttpResponseMessage? response = null;
        string body;
        try
        {
            response = await Client.SendAsync(request, cts.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw new AiProviderException("AIA-TIMEOUT",
                $"{provider} не ответил за {timeout.TotalSeconds:0} с. Проверьте интернет или выберите более быструю модель.",
                $"{request.Method} {Redact(request.RequestUri)}\n{ex}", ex);
        }
        catch (OperationCanceledException)
        {
            // Отмена пользователем — без сообщения об ошибке, но с освобождением соединения.
            response?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // IOException — обрыв соединения при чтении тела ответа (прокси, Wi-Fi, VPN); ответ освобождается сразу.
            response?.Dispose();
            throw new AiProviderException("AIA-NET",
                $"Нет соединения с {provider}: {ex.Message}",
                $"{request.Method} {Redact(request.RequestUri)}\n{ex}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw FromStatus((int)response.StatusCode, body, provider, $"{request.Method} {Redact(request.RequestUri)}");

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new AiProviderException("AIA-HTTP-BADJSON", $"{provider} вернул некорректный ответ сервера.", Truncate(body, 4000), ex);
            }
        }
    }

    public static AiProviderException FromStatus(int status, string body, string provider, string requestLine)
    {
        string apiMessage = ExtractErrorMessage(body);
        string details = $"{requestLine}\nHTTP {status}\n{Truncate(body, 4000)}";
        bool invalidKey = status is 401 ||
                          body.Contains("API_KEY_INVALID", StringComparison.Ordinal) ||
                          body.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) ||
                          body.Contains("invalid x-api-key", StringComparison.OrdinalIgnoreCase) ||
                          body.Contains("Incorrect API key", StringComparison.OrdinalIgnoreCase);

        string message = invalidKey
            ? $"Ключ {provider} недействителен или отозван. Проверьте его в окне «Настройки»."
            : status switch
            {
                400 => $"{provider} отклонил запрос: {apiMessage}",
                402 => $"Недостаточно средств на балансе {provider}: {apiMessage}",
                403 => $"Доступ к {provider} запрещён (нет прав у ключа или регион не поддерживается): {apiMessage}",
                404 => $"Модель или адрес API не найдены у {provider}: {apiMessage}",
                408 => $"{provider} прервал запрос по таймауту. Повторите попытку.",
                413 => $"Запрос слишком большой для {provider} (уменьшите изображение).",
                429 => $"Превышен лимит запросов или исчерпана квота {provider}. Подождите и повторите: {apiMessage}",
                529 => $"{provider} перегружен. Повторите запрос через минуту.",
                >= 500 => $"Сервер {provider} временно недоступен (HTTP {status}).",
                _ => $"{provider}: HTTP {status}. {apiMessage}"
            };
        return new AiProviderException(invalidKey ? "AIA-KEY-INVALID" : $"AIA-HTTP-{status}", message.TrimEnd(' ', ':'), details);
    }

    /// <summary>Достаёт текст ошибки из error.message / error / message любого из API.</summary>
    public static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.ValueKind != JsonValueKind.Object) return Truncate(body, 300);
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? string.Empty;
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString() ?? string.Empty;
            }
            if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String) return msg.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // Ответ не JSON (HTML-страница прокси и т.п.).
        }
        return Truncate(body, 300);
    }

    /// <summary>Убирает ключ из query-строки для логов (Gemini ?key=...).</summary>
    public static string Redact(Uri? uri)
    {
        if (uri is null) return string.Empty;
        var text = uri.GetLeftPart(UriPartial.Path);
        return uri.Query.Length > 0 ? text + "?…" : text;
    }

    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    public static string MaskKey(string key) =>
        key.Length <= 10 ? new string('•', key.Length) : key[..6] + "…" + key[^4..];
}
