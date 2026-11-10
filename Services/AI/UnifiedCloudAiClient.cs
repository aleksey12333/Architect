using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIArchitectStudio.Services.AI;

/// <summary>
/// Адрес и ключ единого облачного шлюза (OpenAI-совместимый /v1/chat/completions с моделями разных семейств по одному ключу).
/// Ключ берётся только из переменных окружения сеанса (<see cref="KeyVariable"/>, <see cref="UrlVariable"/>) или передаётся
/// вызывающим кодом: в settings.json, файлы проекта и журналы он не попадает (<see cref="UnifiedCloudAiClient.Redact"/>).
/// </summary>
public sealed record UnifiedGatewayConfig(string BaseUrl, string ApiKey)
{
    public const string KeyVariable = "AIA_LIVE_API_KEY";
    public const string UrlVariable = "AIA_LIVE_BASE_URL";

    /// <summary>Переменная настройщика шлюза «Лачуга» (процесс или HKCU\Environment).</summary>
    public const string LachugaKeyVariable = "LACHUGA_API_KEY";

    /// <summary>Адрес шлюза «Лачуга» по умолчанию (OpenAI-совместимый маршрут — /v1, нативный Anthropic — /v1/messages).</summary>
    public const string LachugaBaseUrl = "https://lachuga.shop/api/llm";

    /// <summary>
    /// Модели claude-* идут нативным маршрутом Anthropic (POST {BaseUrl}/v1/messages, x-api-key + anthropic-version): на шлюзе
    /// «Лачуга» OpenAI-маршрут отвечал 502 на любой запрос Sonnet 5.5 со снимком, нативный принимает изображения.
    /// </summary>
    public bool AnthropicNative { get; init; }

    /// <summary>Откуда взят ключ (для журнала, без значения): переменная процесса, реестр пользователя, настройки Claude Code.</summary>
    public string Source { get; init; } = "вызывающий код";

    /// <summary>Конфигурация из переменных окружения процесса; без ключа или адреса — null (офлайн, облачный маршрут скрыт).</summary>
    public static UnifiedGatewayConfig? FromEnvironment()
    {
        string? key = Environment.GetEnvironmentVariable(KeyVariable)?.Trim();
        string? url = Environment.GetEnvironmentVariable(UrlVariable)?.Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        return new UnifiedGatewayConfig(url.TrimEnd('/'), key) { AnthropicNative = IsLachuga(url), Source = KeyVariable };
    }

    /// <summary>
    /// Автоподхват ключа (Волна 5): переменные процесса <see cref="LachugaKeyVariable"/> и <see cref="KeyVariable"/>, затем
    /// HKCU\Environment\LACHUGA_API_KEY, затем ~/.claude/settings.json (env.ANTHROPIC_AUTH_TOKEN / ANTHROPIC_API_KEY) — последний
    /// источник берётся только если там же ANTHROPIC_BASE_URL указывает на тот же шлюз (иначе это ключ другого сервиса, и
    /// отправлять его на шлюз нельзя). Адрес — <see cref="UrlVariable"/>, иначе <see cref="LachugaBaseUrl"/>. Ключ нигде не
    /// сохраняется: в settings.json, PasswordVault, журналы и отчёты не попадает. Формат ключа — строго «sk-…».
    /// </summary>
    public static UnifiedGatewayConfig? Discover(string? claudeSettingsPath = null, bool useRegistry = true)
    {
        string? url = Environment.GetEnvironmentVariable(UrlVariable)?.Trim();
        if (url is { Length: > 0 } && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http"))) url = null;
        string baseUrl = (url is { Length: > 0 } ? url : LachugaBaseUrl).TrimEnd('/');
        foreach (var (name, source) in new[] { (LachugaKeyVariable, "переменная процесса " + LachugaKeyVariable), (KeyVariable, "переменная процесса " + KeyVariable) })
            if (ValidKey(Environment.GetEnvironmentVariable(name)) is { } k)
                return new UnifiedGatewayConfig(baseUrl, k) { AnthropicNative = IsLachuga(baseUrl), Source = source };
        if (useRegistry && OperatingSystem.IsWindows())
            try
            {
                using var env = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
                if (ValidKey(env?.GetValue(LachugaKeyVariable) as string) is { } k)
                    return new UnifiedGatewayConfig(baseUrl, k) { AnthropicNative = IsLachuga(baseUrl), Source = @"реестр HKCU\Environment\" + LachugaKeyVariable };
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        string path = claudeSettingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("env", out var envBlock) || envBlock.ValueKind != JsonValueKind.Object) return null;
            string? claudeBase = envBlock.TryGetProperty("ANTHROPIC_BASE_URL", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            if (!SameHost(claudeBase, baseUrl)) return null;
            foreach (var name in new[] { "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY" })
                if (envBlock.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && ValidKey(v.GetString()) is { } k)
                    return new UnifiedGatewayConfig(baseUrl, k) { AnthropicNative = IsLachuga(baseUrl), Source = "настройки Claude Code (env." + name + ")" };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Ключ шлюза: «sk-» строго с маленькой буквы, допустимые символы как у настройщика шлюза, 8…4096 символов.</summary>
    public static string? ValidKey(string? value)
    {
        string? key = value?.Trim();
        return key is { Length: >= 8 and <= 4096 } && key.StartsWith("sk-", StringComparison.Ordinal) &&
               key.Skip(3).All(c => char.IsAsciiLetterOrDigit(c) || "._~+/=:%-".Contains(c)) ? key : null;
    }

    public static bool IsLachuga(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals("lachuga.shop", StringComparison.OrdinalIgnoreCase);

    private static bool SameHost(string? a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub) && ua.Host.Equals(ub.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Строка для журнала: адрес без ключа, ключ маской.</summary>
    public override string ToString() => $"{BaseUrl} · ключ {AiHttp.MaskKey(ApiKey)}";
}

/// <summary>Роль модели в пуле шлюза: рабочая (основные живые испытания), флагман (резерв, беречь), нестабильная на шлюзе.</summary>
public enum UnifiedTier { Workhorse, Flagship, Unstable }

/// <summary>Семейство модели пула: подпись, шаблоны идентификатора у шлюза, роль, порядок в меню.</summary>
public sealed record UnifiedModelSpec(string Alias, string Title, string[] Patterns, UnifiedTier Tier, int Priority);

/// <summary>Модель, найденная на шлюзе: семейство + точный идентификатор провайдера + пункт меню программы.</summary>
public sealed record UnifiedModel(UnifiedModelSpec Spec, string Id, AiModelOption Option);

/// <summary>Сообщение диалога: роль (user / assistant), текст и изображения (только у реплик пользователя).</summary>
public sealed record UnifiedMessage(string Role, string Text, IReadOnlyList<AiImage>? Images = null)
{
    public static UnifiedMessage User(string text, IReadOnlyList<AiImage>? images = null) => new("user", text, images);
    public static UnifiedMessage Assistant(string text) => new("assistant", text);
    public bool HasImages => Images is { Count: > 0 };
}

/// <summary>Запрос диалога: модель (идентификатор шлюза или семейство), системное ТЗ, реплики, лимиты, подпись для журнала.</summary>
public sealed class UnifiedChatRequest
{
    public required string Model { get; init; }
    public required string System { get; init; }
    public required IReadOnlyList<UnifiedMessage> Messages { get; init; }
    public int MaxTokens { get; init; } = 16000;
    public double Temperature { get; init; } = 0.1;
    public string Purpose { get; init; } = "запрос";

    /// <summary>Разрешить переход на другую рабочую модель при сбое запрошенной.</summary>
    public bool AllowFallback { get; init; } = true;

    /// <summary>
    /// Потоковый ответ (SSE): первые байты приходят через секунды — шлюз не обрывает долгую генерацию по таймауту (~110 с без
    /// потока у рассуждающих моделей со снимком). Ответ шлюза обычным JSON тоже разбирается.
    /// </summary>
    public bool Stream { get; init; } = true;

    /// <summary>Глубина рассуждений GPT 5.x/6.x ("low" — быстрее; null — по умолчанию модели).</summary>
    public string? ReasoningEffort { get; init; } = "low";

    /// <summary>Прогресс потока: число символов ответа и рассуждений, полученных к этому моменту (для строки состояния).</summary>
    public IProgress<int>? StreamProgress { get; init; }

    /// <summary>
    /// Текст, добавляемый к последней реплике, если шлюз не принял изображения и запрос повторяется без них (например: «снимок
    /// недоступен — строй по матрице компьютерного зрения»).
    /// </summary>
    public string? NoImageNotice { get; init; }
}

/// <summary>Попытка вызова (для журнала): модель, были ли изображения, HTTP, время, токены, причина отказа.</summary>
public sealed record UnifiedAttempt(string Model, bool Images, int Status, double Ms, string? Error);

/// <summary>Ответ шлюза с полным следом: какая модель ответила, дошёл ли снимок, токены, время, откаты.</summary>
public sealed record UnifiedChatResult(string Text, string? Reasoning, string ModelRequested, string ModelUsed, bool ImagesDelivered, TimeSpan Elapsed,
    long InputTokens, long OutputTokens, bool OutputEstimated, IReadOnlyList<UnifiedAttempt> Attempts)
{
    public bool FellBack => !string.Equals(ModelRequested, ModelUsed, StringComparison.Ordinal);
    public bool ImagesDropped => Attempts.Any(a => a.Images) && !ImagesDelivered;
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>Запись журнала вызовов (без ключа): время, назначение, модели, снимок, статус, мс, токены, примечание.</summary>
public sealed record UnifiedCallRecord(DateTime At, string Purpose, string Requested, string Used, bool ImagesSent, bool ImagesDelivered, int Status, double Ms,
    long InputTokens, long OutputTokens, bool OutputEstimated, string Note);

/// <summary>
/// Единый живой клиент облачного шлюза. Авторазведка: <see cref="DiscoverAsync"/> опрашивает /v1/models (адрес с /v1 и без него)
/// и сопоставляет точные идентификаторы с семействами пула (<see cref="Pool"/>): рабочие Sonnet 5.5 и GPT 5.6 Luna — первыми,
/// флагманы Opus 5.5 и GPT 6.1 Terra — резерв, Gemini 3.1 Pro / 3.8 Flash — нестабильны на шлюзе. Запрос
/// (<see cref="ChatAsync"/>) — многоходовой диалог OpenAI chat/completions; при сбое: запрос со снимком повторяется той же моделью
/// без снимка (шлюз мог отклонить изображение — тогда модель получает матрицу компьютерного зрения, а модель помечается
/// «снимок не принимается» до конца сеанса), затем — бесшовный переход на рабочую модель (Luna → Sonnet). Ключ 401 — без откатов.
/// Каждый вызов пишется в <see cref="Calls"/> (без ключа), токены копятся в <see cref="TotalInputTokens"/>/<see cref="TotalOutputTokens"/>.
/// </summary>
public sealed class UnifiedCloudAiClient : IAiProvider, IDisposable
{
    public static readonly UnifiedModelSpec Sonnet55 = new("sonnet-5.5", "Sonnet 5.5", ["claude-sonnet-5-5", "claude-sonnet-5.5", "sonnet-5-5", "sonnet-5.5"], UnifiedTier.Workhorse, 0);
    public static readonly UnifiedModelSpec Luna56 = new("gpt-5.6-luna", "GPT 5.6 Luna", ["gpt-5.6-luna", "gpt-5-6-luna"], UnifiedTier.Workhorse, 1);
    public static readonly UnifiedModelSpec Opus55 = new("opus-5.5", "Opus 5.5", ["claude-opus-5-5", "claude-opus-5.5", "opus-5-5"], UnifiedTier.Flagship, 2);
    public static readonly UnifiedModelSpec Terra61 = new("gpt-6.1-terra", "GPT 6.1 Terra", ["gpt-6.1-terra", "gpt-6-1-terra"], UnifiedTier.Flagship, 3);
    public static readonly UnifiedModelSpec Gemini31Pro = new("gemini-3.1-pro", "Gemini 3.1 Pro", ["gemini-3.1-pro", "gemini-3-1-pro"], UnifiedTier.Unstable, 4);
    public static readonly UnifiedModelSpec Gemini38Flash = new("gemini-3.8-flash", "Gemini 3.8 Flash", ["gemini-3.8-flash", "gemini-3-8-flash"], UnifiedTier.Unstable, 5);

    /// <summary>Пул моделей ключа в порядке приоритета меню.</summary>
    public static readonly IReadOnlyList<UnifiedModelSpec> Pool = [Sonnet55, Luna56, Opus55, Terra61, Gemini31Pro, Gemini38Flash];

    /// <summary>Порядок отката при сбое: рабочие модели (сначала Luna — у Sonnet на шлюзе бывает отказ по изображениям).</summary>
    public static readonly IReadOnlyList<UnifiedModelSpec> FallbackChain = [Luna56, Sonnet55];

    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly object _sync = new();
    private readonly List<UnifiedCallRecord> _calls = [];
    private readonly HashSet<string> _visionBlocked = new(StringComparer.Ordinal);
    private List<UnifiedModel> _models = [];
    private string? _apiRoot;

    public UnifiedCloudAiClient(UnifiedGatewayConfig config, HttpMessageHandler? handler = null)
    {
        Config = config;
        if (handler is null) _http = AiHttp.Client;
        else
        {
            _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsClient = true;
        }
    }

    public UnifiedGatewayConfig Config { get; }

    /// <summary>Модели шлюза, сопоставленные с пулом (после <see cref="DiscoverAsync"/>).</summary>
    public IReadOnlyList<UnifiedModel> Models
    {
        get { lock (_sync) return _models.ToList(); }
    }

    /// <summary>Все идентификаторы, которые вернул /v1/models.</summary>
    public IReadOnlyList<string> GatewayIds { get; private set; } = [];

    public bool IsDiscovered => _apiRoot is not null;

    public IReadOnlyList<UnifiedCallRecord> Calls
    {
        get { lock (_sync) return _calls.ToList(); }
    }

    public long TotalInputTokens { get { lock (_sync) return _calls.Sum(c => c.InputTokens); } }
    public long TotalOutputTokens { get { lock (_sync) return _calls.Sum(c => c.OutputTokens); } }

    /// <summary>Модели, у которых шлюз в этом сеансе не принял изображение (запросы идут с матрицей без снимка).</summary>
    public IReadOnlyCollection<string> VisionBlocked
    {
        get { lock (_sync) return _visionBlocked.ToList(); }
    }

    public event EventHandler<UnifiedCallRecord>? CallCompleted;

    // ───────────────────────────── Разведка ─────────────────────────────

    /// <summary>Опрос /models (адрес как задан и с суффиксом /v1), сопоставление идентификаторов с пулом.</summary>
    public async Task<IReadOnlyList<UnifiedModel>> DiscoverAsync(CancellationToken ct = default)
    {
        string root = Config.BaseUrl.TrimEnd('/');
        var candidates = root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? new[] { root } : new[] { root + "/v1", root };
        AiProviderException? last = null;
        foreach (var api in candidates)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, api + "/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
                using var doc = await SendJsonAsync(request, AiHttp.InspectionTimeout, ct).ConfigureAwait(false);
                var ids = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var m in data.EnumerateArray())
                        if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) ids.Add(id.GetString()!);
                if (ids.Count == 0) continue;
                GatewayIds = ids;
                var found = Match(ids);
                lock (_sync)
                {
                    _models = found;
                    _apiRoot = api;
                }
                return found;
            }
            catch (AiProviderException ex) when (ex.Code is not "AIA-KEY-INVALID")
            {
                last = ex;
            }
        }
        throw last ?? new AiProviderException("AIA-UNIFIED-DISCOVERY", "Облачный сервис не вернул список моделей.");
    }

    /// <summary>Сопоставление идентификаторов шлюза с семействами пула (точное совпадение — приоритетнее вхождения).</summary>
    public static List<UnifiedModel> Match(IReadOnlyList<string> ids)
    {
        var result = new List<UnifiedModel>();
        foreach (var spec in Pool)
        {
            string? pick = null;
            foreach (var pattern in spec.Patterns)
            {
                pick = ids.FirstOrDefault(i => Normalize(i) == Normalize(pattern));
                if (pick is not null) break;
            }
            pick ??= ids.Where(i => spec.Patterns.Any(p => Normalize(i).Contains(Normalize(p), StringComparison.Ordinal))).OrderBy(i => i.Length).FirstOrDefault();
            if (pick is null) continue;
            result.Add(new UnifiedModel(spec, pick, OptionFor(spec, pick)));
        }
        return result;
    }

    private static string Normalize(string id) => id.Trim().ToLowerInvariant().Replace('.', '-').Replace('_', '-').Replace(' ', '-');

    /// <summary>Пункт меню модели шлюза: «Облачный сервис · Sonnet 5.5», зрение — да (при отказе шлюза — матрица без снимка).</summary>
    public static AiModelOption OptionFor(UnifiedModelSpec spec, string id) =>
        new("unified:" + id, AiProviderKind.UnifiedGateway, id, AiModelCatalog.ProviderTitle(AiProviderKind.UnifiedGateway) + " · " + spec.Title, spec.Title, true);

    /// <summary>Семейство пула по идентификатору или подписи (null — не из пула).</summary>
    public static UnifiedModelSpec? SpecOf(string modelOrAlias)
    {
        string n = Normalize(modelOrAlias);
        return Pool.FirstOrDefault(s => Normalize(s.Alias) == n || s.Patterns.Any(p => Normalize(p) == n || n.Contains(Normalize(p), StringComparison.Ordinal)));
    }

    /// <summary>Точный идентификатор шлюза для семейства или подписи; не найден — как передан.</summary>
    public string Resolve(string modelOrAlias)
    {
        var spec = SpecOf(modelOrAlias);
        lock (_sync)
            return spec is not null && _models.FirstOrDefault(m => m.Spec == spec) is { } found ? found.Id : modelOrAlias;
    }

    // ───────────────────────────── Запросы ─────────────────────────────

    public async Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var result = await ChatAsync(new UnifiedChatRequest
        {
            Model = request.Model.ModelId,
            System = request.SystemPrompt,
            Messages = [UnifiedMessage.User(request.UserPrompt, request.Images)],
            MaxTokens = Math.Max(4000, request.MaxOutputTokens),
            Temperature = request.Temperature,
            Purpose = "проектирование",
            NoImageNotice = "Снимок облачному сервису не передан (отказ шлюза): строй модель строго по матрице компьютерного зрения из задания."
        }, cancellationToken).ConfigureAwait(false);
        return new AiResponse(result.Text, AiModelCatalog.ProviderTitle(AiProviderKind.UnifiedGateway) + " · " + (SpecOf(result.ModelUsed)?.Title ?? result.ModelUsed),
            result.Elapsed, Backend(result), result.InputTokens, result.OutputTokens, result.Reasoning);
    }

    private static string Backend(UnifiedChatResult r) =>
        "облачный сервис" + (r.FellBack ? $" (замена {SpecOf(r.ModelRequested)?.Title ?? r.ModelRequested} → {SpecOf(r.ModelUsed)?.Title ?? r.ModelUsed})" : string.Empty) +
        (r.ImagesDropped ? " · снимок не принят, матрица без снимка" : string.Empty);

    /// <summary>
    /// Диалог с откатами: запрошенная модель (со снимком, если модель не помечена «снимок не принимается»), при сбое — та же модель
    /// без изображений, затем рабочие модели <see cref="FallbackChain"/>. Возвращает текст и след попыток.
    /// </summary>
    public async Task<UnifiedChatResult> ChatAsync(UnifiedChatRequest request, CancellationToken ct = default)
    {
        if (!IsDiscovered)
            try { await DiscoverAsync(ct).ConfigureAwait(false); }
            catch (AiProviderException) { /* разведка не удалась — запрос по идентификатору как есть */ }
        string requested = Resolve(request.Model);
        bool hasImages = request.Messages.Any(m => m.HasImages);
        var plan = new List<(string Model, bool Images)>();
        void Plan(string model, bool images)
        {
            if (!plan.Contains((model, images))) plan.Add((model, images));
        }
        Plan(requested, hasImages && !IsVisionBlocked(requested));
        if (hasImages) Plan(requested, false);
        if (request.AllowFallback)
            foreach (var spec in FallbackChain)
            {
                string id = Resolve(spec.Alias);
                if (id == requested) continue;
                if (hasImages && !IsVisionBlocked(id)) Plan(id, true);
                Plan(id, false);
            }

        var attempts = new List<UnifiedAttempt>();
        var total = Stopwatch.StartNew();
        AiProviderException? lastError = null;
        // Временные сбои шлюза (504/408/429, таймаут, обрыв) — повтор той же попытки один раз; снимок при этом не снимается.
        var expanded = new List<(string Model, bool Images, bool Retry)>();
        foreach (var (m, img) in plan) expanded.Add((m, img, false));
        for (int idx = 0; idx < expanded.Count; idx++)
        {
            var (model, images, isRetry) = expanded[idx];
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                var (text, reasoning, input, output, estimated) = await SendChatAsync(model, request, images, ct).ConfigureAwait(false);
                sw.Stop();
                attempts.Add(new UnifiedAttempt(model, images, 200, sw.Elapsed.TotalMilliseconds, null));
                // Снимок отклонён (не временный сбой), а без него та же модель ответила — до конца сеанса снимок этой модели не отправляется.
                if (hasImages && !images && attempts.Any(a => a.Model == model && a.Images && a.Error is not null && !IsTransient(a.Status)))
                    lock (_sync) _visionBlocked.Add(model);
                var result = new UnifiedChatResult(text, reasoning, requested, model, images, total.Elapsed, input, output, estimated, attempts);
                Record(request, result, 200, Note(result));
                return result;
            }
            catch (AiProviderException ex) when (ex.Code is not "AIA-KEY-INVALID")
            {
                sw.Stop();
                int status = StatusOf(ex);
                attempts.Add(new UnifiedAttempt(model, images, status, sw.Elapsed.TotalMilliseconds, ex.Message));
                Record(request, new UnifiedChatResult(string.Empty, null, requested, model, false, sw.Elapsed, 0, 0, false, [attempts[^1]]), status,
                    "сбой: " + Redact(ex.Message) + (IsTransient(status) && !isRetry ? " — временный сбой, повтор той же попытки" : string.Empty));
                lastError = ex;
                if (IsTransient(status) && !isRetry)
                {
                    expanded.Insert(idx + 1, (model, images, true));
                    if (status == 429) await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                }
            }
        }
        throw new AiProviderException(lastError?.Code ?? "AIA-UNIFIED-FAILED",
            "Облачный сервис не ответил ни одной моделью пула: " + (lastError?.Message ?? "нет данных"),
            string.Join("\n", attempts.Select(a => $"{a.Model} {(a.Images ? "со снимком" : "без снимка")}: HTTP {a.Status}, {a.Ms:0} мс, {a.Error}")));
    }

    /// <summary>Временный сбой шлюза: таймаут (0 — нет ответа), 408, 429, 504.</summary>
    public static bool IsTransient(int status) => status is 0 or 408 or 429 or 504;

    public bool IsVisionBlocked(string model)
    {
        lock (_sync) return _visionBlocked.Contains(model);
    }

    private static string Note(UnifiedChatResult r) =>
        (r.FellBack ? $"замена модели {r.ModelRequested} → {r.ModelUsed}; " : string.Empty) +
        (r.ImagesDropped ? "шлюз не принял снимок — повтор с матрицей без снимка; " : string.Empty) +
        (r.OutputEstimated ? "выходные токены оценены по длине ответа (шлюз вернул 0–1)" : string.Empty);

    private static int StatusOf(AiProviderException ex) =>
        ex.Code.StartsWith("AIA-HTTP-", StringComparison.Ordinal) && int.TryParse(ex.Code[9..], out var s) ? s : 0;

    private void Record(UnifiedChatRequest request, UnifiedChatResult result, int status, string note)
    {
        var rec = new UnifiedCallRecord(DateTime.Now, request.Purpose, result.ModelRequested, result.ModelUsed, result.Attempts.Any(a => a.Images), result.ImagesDelivered, status,
            result.Elapsed.TotalMilliseconds, result.InputTokens, result.OutputTokens, result.OutputEstimated, note.TrimEnd(' ', ';'));
        lock (_sync) _calls.Add(rec);
        CallCompleted?.Invoke(this, rec);
    }

    /// <summary>
    /// Принимает ли модель параметр temperature. Живая проверка шлюза 10.11.2026: claude-sonnet-5-5 отвечает HTTP 502 на любой
    /// запрос с temperature (на обоих маршрутах, со снимком и без) и 200 — без неё; прежний вывод «Sonnet не принимает снимки»
    /// был следствием этого. Модели Claude получают температуру по умолчанию.
    /// </summary>
    public static bool AcceptsTemperature(string model) => !model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Модель идёт нативным маршрутом Anthropic /v1/messages (claude-* при <see cref="UnifiedGatewayConfig.AnthropicNative"/>).</summary>
    public bool UsesAnthropicRoute(string model) => Config.AnthropicNative && model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);

    private async Task<(string Text, string? Reasoning, long Input, long Output, bool Estimated)> SendChatAsync(string model, UnifiedChatRequest request, bool images,
        CancellationToken ct)
    {
        if (UsesAnthropicRoute(model)) return await SendAnthropicAsync(model, request, images, ct).ConfigureAwait(false);
        var messages = new List<object> { new { role = "system", content = request.System } };
        bool droppedImages = !images && request.Messages.Any(m => m.HasImages);
        for (int i = 0; i < request.Messages.Count; i++)
        {
            var m = request.Messages[i];
            string body0 = m.Text;
            if (droppedImages && i == request.Messages.Count - 1 && m.Role == "user" && request.NoImageNotice is { Length: > 0 } notice) body0 = notice + "\n" + body0;
            if (images && m.HasImages)
            {
                var parts = new List<object> { new { type = "text", text = body0 } };
                foreach (var image in m.Images!)
                {
                    if (image.Label is { Length: > 0 } label) parts.Add(new { type = "text", text = label });
                    parts.Add(new { type = "image_url", image_url = new { url = $"data:{image.MimeType};base64,{image.Base64}" } });
                }
                messages.Add(new { role = m.Role, content = parts });
            }
            else messages.Add(new { role = m.Role, content = body0 });
        }
        var body = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages, ["max_tokens"] = request.MaxTokens };
        // Рассуждающие модели GPT 5.x/6.x принимают только температуру по умолчанию; глубина рассуждений — reasoning_effort.
        bool gptReasoning = model.StartsWith("gpt-5", StringComparison.Ordinal) || model.StartsWith("gpt-6", StringComparison.Ordinal) || model.StartsWith('o');
        if (!gptReasoning && AcceptsTemperature(model)) body["temperature"] = request.Temperature;
        else if (request.ReasoningEffort is { Length: > 0 } effort) body["reasoning_effort"] = effort;
        if (request.Stream)
        {
            body["stream"] = true;
            body["stream_options"] = new { include_usage = true };
        }
        string root = _apiRoot ?? (Config.BaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? Config.BaseUrl : Config.BaseUrl + "/v1");
        using var http = new HttpRequestMessage(HttpMethod.Post, root + "/chat/completions") { Content = AiHttp.Json(body) };
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
        var (text, reasoningRaw, input, output) = await SendChatBodyAsync(http, request.StreamProgress, ct).ConfigureAwait(false);
        string? reasoning = reasoningRaw;
        var separated = ReasoningText.Separate(text);
        string answer = separated.Answer.Length > 0 ? separated.Answer : text;
        if (separated.Reasoning is { Length: > 0 } inline) reasoning = string.IsNullOrEmpty(reasoning) ? inline : reasoning + "\n" + inline;
        if (string.IsNullOrWhiteSpace(answer)) throw new AiProviderException("AIA-EMPTY", "Облачный сервис вернул пустой ответ модели.");
        // Шлюз у части семейств возвращает completion_tokens = 0…1 при длинном ответе — оценка по длине (≈ 3,5 символа на токен).
        bool estimated = false;
        long byLength = (long)Math.Ceiling((answer.Length + (reasoning?.Length ?? 0)) / 3.5);
        if (output <= 1 && byLength > 4) { output = byLength; estimated = true; }
        return (answer, reasoning, input, output, estimated);
    }

    /// <summary>
    /// Нативный маршрут Anthropic: POST {BaseUrl}/v1/messages, заголовки x-api-key, Authorization: Bearer и anthropic-version
    /// 2023-06-01; системное ТЗ — поле system, изображения — блоки {type:"image", source:{base64}}; поток SSE
    /// (content_block_delta: text_delta / thinking_delta, message_start / message_delta — токены) или обычный JSON.
    /// </summary>
    private async Task<(string Text, string? Reasoning, long Input, long Output, bool Estimated)> SendAnthropicAsync(string model, UnifiedChatRequest request, bool images,
        CancellationToken ct)
    {
        var messages = new List<object>();
        bool droppedImages = !images && request.Messages.Any(m => m.HasImages);
        for (int i = 0; i < request.Messages.Count; i++)
        {
            var m = request.Messages[i];
            string text0 = m.Text;
            if (droppedImages && i == request.Messages.Count - 1 && m.Role == "user" && request.NoImageNotice is { Length: > 0 } notice) text0 = notice + "\n" + text0;
            var parts = new List<object>();
            if (images && m.HasImages)
                foreach (var image in m.Images!)
                {
                    if (image.Label is { Length: > 0 } label) parts.Add(new { type = "text", text = label });
                    parts.Add(new { type = "image", source = new { type = "base64", media_type = image.MimeType, data = image.Base64 } });
                }
            parts.Add(new { type = "text", text = text0 });
            messages.Add(new { role = m.Role == "assistant" ? "assistant" : "user", content = parts });
        }
        var body = new Dictionary<string, object> { ["model"] = model, ["system"] = request.System, ["messages"] = messages, ["max_tokens"] = request.MaxTokens };
        if (AcceptsTemperature(model)) body["temperature"] = request.Temperature;
        if (request.Stream) body["stream"] = true;
        string root = Config.BaseUrl.TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3];
        using var http = new HttpRequestMessage(HttpMethod.Post, root + "/v1/messages") { Content = AiHttp.Json(body) };
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
        http.Headers.TryAddWithoutValidation("x-api-key", Config.ApiKey);
        http.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(AttemptTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AiProviderException("AIA-TIMEOUT", $"Облачный сервис не ответил за {AttemptTimeout.TotalSeconds:0} с.", Redact(ex.Message), ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new AiProviderException("AIA-NET", "Нет соединения с облачным сервисом: " + Redact(ex.Message), null, ex);
        }
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        long input = 0, output = 0;
        using (response)
        {
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    string err = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    throw AiHttp.FromStatus((int)response.StatusCode, Redact(err), "Облачный сервис", $"{http.Method} {AiHttp.Redact(http.RequestUri)}");
                }
                using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var raw = new StringBuilder();
                bool sse = false;
                int reported = 0;
                string? line;
                while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) is not null)
                {
                    if (!sse && raw.Length == 0 && (line.StartsWith("event:", StringComparison.Ordinal) || line.StartsWith("data:", StringComparison.Ordinal))) sse = true;
                    if (!sse) { raw.AppendLine(line); continue; }
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string data = line[5..].Trim();
                    if (data.Length == 0 || data == "[DONE]") continue;
                    using var chunk = JsonDocument.Parse(data);
                    ReadAnthropic(chunk.RootElement, text, reasoning, ref input, ref output);
                    int received = text.Length + reasoning.Length;
                    if (request.StreamProgress is not null && received - reported >= 200) { reported = received; request.StreamProgress.Report(received); }
                }
                if (!sse)
                {
                    using var doc = JsonDocument.Parse(raw.ToString());
                    ReadAnthropic(doc.RootElement, text, reasoning, ref input, ref output);
                }
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new AiProviderException("AIA-TIMEOUT", $"Облачный сервис не завершил ответ за {AttemptTimeout.TotalSeconds:0} с.", Redact(ex.Message), ex);
            }
            catch (IOException ex)
            {
                throw new AiProviderException("AIA-NET", "Обрыв потока облачного сервиса: " + Redact(ex.Message), null, ex);
            }
            catch (JsonException ex)
            {
                throw new AiProviderException("AIA-HTTP-BADJSON", "Облачный сервис вернул некорректный ответ.", Redact(ex.Message), ex);
            }
        }
        var separated = ReasoningText.Separate(text.ToString());
        string answer = separated.Answer.Length > 0 ? separated.Answer : text.ToString();
        string? thinking = reasoning.Length > 0 ? reasoning.ToString() : null;
        if (separated.Reasoning is { Length: > 0 } inline) thinking = string.IsNullOrEmpty(thinking) ? inline : thinking + "\n" + inline;
        if (string.IsNullOrWhiteSpace(answer)) throw new AiProviderException("AIA-EMPTY", "Облачный сервис вернул пустой ответ модели.");
        bool estimated = false;
        long byLength = (long)Math.Ceiling((answer.Length + (thinking?.Length ?? 0)) / 3.5);
        if (output <= 1 && byLength > 4) { output = byLength; estimated = true; }
        return (answer, thinking, input, output, estimated);
    }

    /// <summary>Событие потока Anthropic или ответ целиком: текст, рассуждения (thinking) и токены; {"type":"error"} — сбой шлюза.</summary>
    private void ReadAnthropic(JsonElement root, StringBuilder text, StringBuilder reasoning, ref long input, ref long output)
    {
        string type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
        if (type == "error" || (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object))
            throw new AiProviderException("AIA-HTTP-502", "Облачный сервис вернул ошибку: " + Redact(AiHttp.ExtractErrorMessage(root.GetRawText())));
        static void Usage(JsonElement u, ref long input, ref long output)
        {
            if (u.ValueKind != JsonValueKind.Object) return;
            if (u.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var iv) && iv > 0) input = iv;
            if (u.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov) && ov > 0) output = ov;
        }
        switch (type)
        {
            case "message_start" when root.TryGetProperty("message", out var msg):
                if (msg.TryGetProperty("usage", out var mu)) Usage(mu, ref input, ref output);
                break;
            case "message_delta":
                if (root.TryGetProperty("usage", out var du)) Usage(du, ref input, ref output);
                break;
            case "content_block_delta" when root.TryGetProperty("delta", out var d):
                if (d.TryGetProperty("text", out var dt) && dt.ValueKind == JsonValueKind.String) text.Append(dt.GetString());
                if (d.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String) reasoning.Append(th.GetString());
                break;
            case "message":
                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                    {
                        string bt = block.TryGetProperty("type", out var b) ? b.GetString() ?? string.Empty : string.Empty;
                        if (bt == "text" && block.TryGetProperty("text", out var bx)) text.Append(bx.GetString());
                        else if (bt == "thinking" && block.TryGetProperty("thinking", out var bk)) reasoning.Append(bk.GetString());
                    }
                if (root.TryGetProperty("usage", out var u2)) Usage(u2, ref input, ref output);
                break;
        }
    }

    /// <summary>
    /// Отправка запроса диалога: ответ потоком SSE ("data: {…}" — delta.content / delta.reasoning_content, usage в последнем
    /// фрагменте) или обычным JSON; ошибки HTTP и {"error": …} внутри потока — в <see cref="AiProviderException"/>.
    /// </summary>
    private async Task<(string Text, string? Reasoning, long Input, long Output)> SendChatBodyAsync(HttpRequestMessage request, IProgress<int>? streamProgress, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(AttemptTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AiProviderException("AIA-TIMEOUT", $"Облачный сервис не ответил за {AttemptTimeout.TotalSeconds:0} с.", Redact(ex.Message), ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new AiProviderException("AIA-NET", "Нет соединения с облачным сервисом: " + Redact(ex.Message), null, ex);
        }
        using (response)
        {
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    string err = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    throw AiHttp.FromStatus((int)response.StatusCode, Redact(err), "Облачный сервис", $"{request.Method} {AiHttp.Redact(request.RequestUri)}");
                }
                using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = new StringBuilder();
                var reasoning = new StringBuilder();
                var raw = new StringBuilder();
                long input = 0, output = 0;
                int reported = 0;
                bool sse = false;
                string? line;
                while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) is not null)
                {
                    if (!sse && raw.Length == 0 && line.StartsWith("data:", StringComparison.Ordinal)) sse = true;
                    if (!sse) { raw.AppendLine(line); continue; }
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string data = line[5..].Trim();
                    if (data == "[DONE]") break;
                    using var chunk = JsonDocument.Parse(data);
                    ReadPayload(chunk.RootElement, text, reasoning, ref input, ref output, delta: true);
                    int received = text.Length + reasoning.Length;
                    if (streamProgress is not null && received - reported >= 200) { reported = received; streamProgress.Report(received); }
                }
                if (!sse)
                {
                    using var doc = JsonDocument.Parse(raw.ToString());
                    ReadPayload(doc.RootElement, text, reasoning, ref input, ref output, delta: false);
                }
                return (text.ToString(), reasoning.Length > 0 ? reasoning.ToString() : null, input, output);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new AiProviderException("AIA-TIMEOUT", $"Облачный сервис не завершил ответ за {AttemptTimeout.TotalSeconds:0} с.", Redact(ex.Message), ex);
            }
            catch (IOException ex)
            {
                throw new AiProviderException("AIA-NET", "Обрыв потока облачного сервиса: " + Redact(ex.Message), null, ex);
            }
            catch (JsonException ex)
            {
                throw new AiProviderException("AIA-HTTP-BADJSON", "Облачный сервис вернул некорректный ответ.", Redact(ex.Message), ex);
            }
        }
    }

    /// <summary>Текст, рассуждения и токены из ответа целиком (message) или фрагмента потока (delta); {"error": …} — сбой шлюза.</summary>
    private void ReadPayload(JsonElement root, StringBuilder text, StringBuilder reasoning, ref long input, ref long output, bool delta)
    {
        if (root.TryGetProperty("error", out var err) && err.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            throw new AiProviderException("AIA-HTTP-502", "Облачный сервис вернул ошибку: " + Redact(AiHttp.ExtractErrorMessage(root.GetRawText())));
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty(delta ? "delta" : "message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            if (message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String) text.Append(content.GetString());
                else if (content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("text", out var t)) text.Append(t.GetString());
            }
            if (message.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String) reasoning.Append(rc.GetString());
        }
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt64(out var pv)) input = pv;
            if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt64(out var cv)) output = cv;
        }
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        HttpResponseMessage response;
        string body;
        try
        {
            response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AiProviderException("AIA-TIMEOUT", $"Облачный сервис не ответил за {timeout.TotalSeconds:0} с.", Redact(ex.Message), ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new AiProviderException("AIA-NET", "Нет соединения с облачным сервисом: " + Redact(ex.Message), null, ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw AiHttp.FromStatus((int)response.StatusCode, Redact(body), "Облачный сервис", $"{request.Method} {AiHttp.Redact(request.RequestUri)}");
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new AiProviderException("AIA-HTTP-BADJSON", "Облачный сервис вернул некорректный ответ.", AiHttp.Truncate(Redact(body), 2000), ex);
            }
        }
    }

    /// <summary>Убирает из текста ключ шлюза (на случай, если сервер повторил его в сообщении об ошибке).</summary>
    public string Redact(string text) => Config.ApiKey.Length >= 8 ? text.Replace(Config.ApiKey, AiHttp.MaskKey(Config.ApiKey), StringComparison.Ordinal) : text;

    /// <summary>Журнал вызовов одной строкой на вызов (для отчёта и ленты; без ключа).</summary>
    public static string Describe(UnifiedCallRecord r) => string.Create(CultureInfo.GetCultureInfo("ru-RU"),
        $"{r.At:HH:mm:ss} · {r.Purpose} · {SpecOf(r.Used)?.Title ?? r.Used}{(r.Used != r.Requested ? $" (вместо {SpecOf(r.Requested)?.Title ?? r.Requested})" : string.Empty)} · " +
        $"{(r.ImagesDelivered ? "снимок + паспорт" : r.ImagesSent ? "снимок отклонён → матрица" : "матрица")} · HTTP {r.Status} · {r.Ms / 1000:0.0} с · " +
        $"токенов {r.InputTokens:N0} → {r.OutputTokens:N0}{(r.OutputEstimated ? " (оценка)" : string.Empty)}{(r.Note.Length > 0 ? " · " + r.Note : string.Empty)}");

    // ───────────────────────────── Общий экземпляр ─────────────────────────────

    /// <summary>Клиент текущего сеанса (создаётся при старте, если заданы переменные окружения шлюза), иначе null.</summary>
    public static UnifiedCloudAiClient? Shared { get; set; }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
