using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIArchitectStudio.Services.AI;

/// <summary>
/// Провайдер локального ИИ-сервера Ollama (<c>POST /api/chat</c>, потоковый NDJSON). Веса загружает и выгружает сам
/// сервер (вытеснение при нехватке памяти — его планировщик), поэтому роли Совета КБ могут работать разными моделями без
/// Out-Of-Memory в процессе приложения. Рассуждающие модели (deepseek-r1, gpt-oss, Qwen 3 и т.п.) отдают «внутренние
/// рассуждения» в поле <c>message.thinking</c> или блоками &lt;think&gt; внутри ответа — и то и другое отделяется от
/// ответа (<see cref="ReasoningText"/>): JSON-разбор получает только ответ, лента «Коллоквиум ИИ» — рассуждения.
/// Модели без рассуждений получают <c>format: "json"</c> (только валидный JSON).
/// </summary>
public sealed class OllamaAIProvider
    : IAiProvider
{
    /// <summary>Пауза без единого байта ответа: первая строка (загрузка весов с диска) и последующие.</summary>
    public static readonly TimeSpan FirstTokenTimeout = TimeSpan.FromMinutes(6);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Токены на «внутренние рассуждения» сверх лимита ответа (deepseek-r1 тратит 1–5 К на короткую реплику).</summary>
    public const int ReasoningBudget = 6144;

    /// <summary>Интервал обновления ленты при потоковой генерации.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(120);

    private readonly OllamaProcessManager? _manager;
    private readonly Func<Uri> _endpoint;
    private readonly HttpClient _http;

    public OllamaAIProvider(OllamaProcessManager manager)
    {
        _manager = manager;
        _endpoint = () => manager.Endpoint;
        _http = AiHttp.Client;
    }

    /// <summary>Для автотеста: подменённый HTTP-обработчик (сценарный NDJSON без сервера).</summary>
    public OllamaAIProvider(Uri endpoint, HttpMessageHandler handler)
    {
        _endpoint = () => endpoint;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var model = request.Model;
        if (request.HasImage && !model.SupportsVision)
            throw new AiProviderException("AIA-OLLAMA-VISION",
                $"Модель {model.ModelId} не принимает изображения. Выберите модель Ollama со зрением (gemma4, qwen3.8, llava) или облачную модель.");
        if (_manager is not null && !await _manager.EnsureRunningAsync(cancellationToken).ConfigureAwait(false))
            throw new AiProviderException("AIA-OLLAMA-OFFLINE",
                $"Локальный интеллект недоступен: {_manager.Detail}", _manager.ServerLogTail(20));

        bool think = model.IsReasoning;
        try
        {
            return await SendAsync(request, think, cancellationToken).ConfigureAwait(false);
        }
        catch (AiProviderException ex) when (think && ex.Code == "AIA-OLLAMA-400" && ex.Details.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            // Сервер не поддерживает параметр think для этой модели — повтор без него (теги <think> отделятся из текста).
            return await SendAsync(request, false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Выгружает модель из памяти сервера (<c>keep_alive: 0</c>).</summary>
    public async Task UnloadAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["model"] = modelId, ["keep_alive"] = 0 };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var _ = await _http.PostAsync(new Uri(_endpoint(), "api/generate"), content, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Выгрузка — оптимизация памяти; сервер всё равно вытеснит модель сам.
        }
    }

    /// <summary>Тело запроса /api/chat (вынесено для автотеста).</summary>
    public static JsonObject BuildBody(AiRequest request, bool think)
    {
        var model = request.Model;
        var user = new JsonObject { ["role"] = "user", ["content"] = BuildUserText(request) };
        if (request.HasImage && model.SupportsVision)
            user["images"] = new JsonArray(request.Images.Select(i => (JsonNode)JsonValue.Create(i.Base64)!).ToArray());

        // Контекст = промпт + ответ (у рассуждающих моделей рассуждения расходуют тот же лимит). Локальная память ограничена
        // (KV-кэш 14B-модели ≈ 0.2 МБ на токен), поэтому ответ ограничен 8 К токенов, у рассуждающих моделей к нему
        // добавляется бюджет рассуждений 6 К (всего не больше 16 К), контекст — 32 К.
        int promptTokens = (request.SystemPrompt.Length + request.UserPrompt.Length) / 3 + request.Images.Count * 800;
        int answer = Math.Min(request.MaxOutputTokens, 8192);
        int predict = Math.Clamp(think ? Math.Min(answer + ReasoningBudget, 16384) : answer, 256, 32768);
        int context = Math.Clamp(RoundUp(promptTokens + predict + 256, 2048), 8192, 32768);
        var body = new JsonObject
        {
            ["model"] = model.ModelId,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt }, user),
            ["stream"] = true,
            ["keep_alive"] = "10m",
            ["options"] = new JsonObject
            {
                ["temperature"] = Math.Clamp(request.Temperature, 0, 1.5),
                ["num_predict"] = predict,
                ["num_ctx"] = context
            }
        };
        if (think)
            body["think"] = model.ModelId.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) ? JsonValue.Create("medium") : JsonValue.Create(true);
        else
            body["format"] = "json";
        return body;
    }

    private static string BuildUserText(AiRequest request)
    {
        if (request.Images.Count == 0 || request.Images.All(i => string.IsNullOrWhiteSpace(i.Label))) return request.UserPrompt;
        var labels = string.Join("; ", request.Images.Select((img, i) => $"изображение {i + 1}: {img.Label ?? "без подписи"}"));
        return request.UserPrompt + "\n\nПриложенные изображения по порядку — " + labels + ".";
    }

    private static int RoundUp(int value, int step) => (value + step - 1) / step * step;

    private async Task<AiResponse> SendAsync(AiRequest request, bool think, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var body = BuildBody(request, think);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint(), "api/chat"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(FirstTokenTimeout);
        string modelName = request.Model.ModelId;

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException("AIA-OLLAMA-TIMEOUT",
                $"Ollama не начал отвечать за {FirstTokenTimeout.TotalMinutes:0} мин (модель {modelName} загружается слишком долго).", ex.ToString(), ex);
        }
        catch (HttpRequestException ex)
        {
            _manager?.MarkUnavailable("Нет соединения с сервером Ollama: " + ex.Message);
            throw new AiProviderException("AIA-OLLAMA-NET", $"Нет соединения с локальным сервером Ollama ({_endpoint()}): {ex.Message}", ex.ToString(), ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string error;
                try
                {
                    // Тело ошибки — с тем же ограничением ожидания, что и поток ответа (сервер может зависнуть и здесь).
                    idle.CancelAfter(IdleTimeout);
                    error = await response.Content.ReadAsStringAsync(idle.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    error = ex.Message;
                }
                throw FromStatus(response.StatusCode, error, modelName);
            }

            var content = new StringBuilder();
            var thinking = new StringBuilder();
            long? promptCount = null, evalCount = null;
            string? doneReason = null;
            var lastProgress = Stopwatch.StartNew();
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync(idle.Token).ConfigureAwait(false) is { } line)
                {
                    idle.CancelAfter(IdleTimeout);
                    if (line.Length == 0) continue;
                    using var doc = ParseLine(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var err))
                        throw FromError(err.GetString() ?? line, modelName);
                    if (root.TryGetProperty("message", out var msg))
                    {
                        if (msg.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } part) content.Append(part);
                        if (msg.TryGetProperty("thinking", out var t) && t.GetString() is { Length: > 0 } th) thinking.Append(th);
                    }
                    else if (root.TryGetProperty("response", out var r) && r.GetString() is { Length: > 0 } resp)
                        content.Append(resp);
                    bool done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
                    if (done)
                    {
                        if (root.TryGetProperty("prompt_eval_count", out var pc) && pc.TryGetInt64(out long p)) promptCount = p;
                        if (root.TryGetProperty("eval_count", out var ec) && ec.TryGetInt64(out long e)) evalCount = e;
                        if (root.TryGetProperty("done_reason", out var dr)) doneReason = dr.GetString();
                    }
                    if (request.Progress is { } progress && (done || lastProgress.Elapsed >= ProgressInterval))
                    {
                        lastProgress.Restart();
                        progress.Report(Split(content, thinking));
                    }
                    if (done) break;
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiProviderException("AIA-OLLAMA-TIMEOUT",
                    $"Ollama перестал отвечать во время генерации ({IdleTimeout.TotalMinutes:0} мин без данных, модель {modelName}).", ex.ToString(), ex);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                _manager?.MarkUnavailable("Соединение с сервером Ollama прервано: " + ex.Message);
                throw new AiProviderException("AIA-OLLAMA-NET", $"Соединение с Ollama прервано во время генерации: {ex.Message}", ex.ToString(), ex);
            }
            sw.Stop();

            var result = Split(content, thinking);
            if (result.Answer.Length == 0)
                throw new AiProviderException("AIA-OLLAMA-EMPTY",
                    doneReason == "length"
                        ? $"Модель {modelName} исчерпала лимит токенов на рассуждения и не успела выдать ответ. Повторите запрос или выберите модель без режима рассуждений."
                        : $"Модель {modelName} вернула пустой ответ.",
                    "Рассуждения модели:\n" + AiHttp.Truncate(result.Reasoning, 4000));
            return new AiResponse(result.Answer, $"Ollama · {modelName}", sw.Elapsed, "Ollama", promptCount, evalCount,
                result.Reasoning.Length > 0 ? result.Reasoning : null);
        }
    }

    /// <summary>Рассуждения из поля thinking + блоки &lt;think&gt; внутри ответа; ответ — без них.</summary>
    public static AiStreamProgress Split(StringBuilder content, StringBuilder thinking)
    {
        var (answer, inline) = ReasoningText.Separate(content.ToString());
        string reasoning = thinking.Length == 0 ? inline : inline.Length == 0 ? thinking.ToString().Trim() : thinking.ToString().Trim() + "\n\n" + inline;
        return new AiStreamProgress(reasoning, answer);
    }

    private static JsonDocument ParseLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new AiProviderException("AIA-OLLAMA-BADJSON", "Сервер Ollama вернул некорректную строку потока.", AiHttp.Truncate(line, 2000), ex);
        }
    }

    private static AiProviderException FromStatus(HttpStatusCode status, string body, string model)
    {
        string error = AiHttp.ExtractErrorMessage(body);
        if (status == HttpStatusCode.NotFound || error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return new AiProviderException("AIA-OLLAMA-404",
                $"Модель {model} не найдена на сервере Ollama. Скачайте её командой «ollama pull {model}» или выберите другую.", body);
        if (error.Contains("memory", StringComparison.OrdinalIgnoreCase))
            return FromError(error, model);
        return new AiProviderException($"AIA-OLLAMA-{(int)status}", $"Ollama отклонил запрос ({(int)status}): {error}", body);
    }

    private static AiProviderException FromError(string error, string model) =>
        error.Contains("memory", StringComparison.OrdinalIgnoreCase)
            ? new AiProviderException("AIA-OLLAMA-MEMORY",
                $"Не хватает памяти для модели {model}: {error}. Назначьте роли модель меньшего размера или закройте другие приложения.", error)
            : new AiProviderException("AIA-OLLAMA-ERROR", $"Ошибка Ollama ({model}): {error}", error);
}
