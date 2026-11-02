using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AIArchitectStudio.Services.AI;

/// <summary>Состояние локального ИИ-движка Ollama для строки статуса.</summary>
public enum OllamaEngineState
{
    Unknown,
    Connecting,
    Starting,
    Ready,
    NotInstalled,
    Failed
}

/// <summary>
/// Zero-Config менеджер локального ИИ-сервера Ollama. При старте приложения пингует <c>/api/tags</c>; если сервер не
/// отвечает — находит <c>ollama.exe</c> (PATH, <c>%LOCALAPPDATA%\Programs\Ollama</c>, <c>%ProgramFiles%\Ollama</c>), скрыто
/// запускает <c>ollama serve</c> (без окна, без оболочки; OLLAMA_MODELS — из настроек или переменной пользователя), ждёт
/// готовности и публикует установленные модели в <see cref="AiModelRegistry"/> для ИИ-дока и ролей Совета КБ. Сервер,
/// запущенный приложением, останавливается при выходе (вместе с раннерами моделей); запущенный пользователем — не трогается.
/// Процессы сервера входят в объект задания Windows с завершением при закрытии: даже при аварийном завершении приложения
/// или снятии его из диспетчера задач система сама завершает их — процессов-сирот не остаётся.
/// </summary>
public sealed class OllamaProcessManager : IDisposable
{
    public const string DefaultEndpoint = "http://localhost:11434";

    /// <summary>Сколько ждать готовности после запуска <c>ollama serve</c>.</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Сколько ждать завершения дерева процессов сервера при выходе.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly SettingsService _settings;
    private readonly AiModelRegistry _registry;
    private readonly object _sync = new();
    private readonly Queue<string> _serverLog = new();

    /// <summary>Отмена фонового запуска при выходе: сервер не должен стартовать после закрытия окна.</summary>
    private readonly CancellationTokenSource _lifetime = new();
    private Task<bool>? _ensureTask;
    private volatile Process? _process;
    private volatile bool _disposed;
    private string? _endpointOverride;

    public OllamaProcessManager(SettingsService settings, AiModelRegistry registry)
    {
        _settings = settings;
        _registry = registry;
    }

    public event EventHandler? StateChanged;

    public OllamaEngineState State { get; private set; } = OllamaEngineState.Unknown;

    /// <summary>Пояснение к состоянию (версия, число моделей, причина сбоя) — русский текст для подсказки.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>Версия сервера из <c>/api/version</c>.</summary>
    public string? Version { get; private set; }

    /// <summary>Сервер запущен этим приложением (будет остановлен при выходе).</summary>
    public bool StartedByApp => IsAlive(_process);

    /// <summary>Процесс жив; уже освобождённый (Dispose при выходе) считается завершённым.</summary>
    private static bool IsAlive(Process? process)
    {
        if (process is null) return false;
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public string? ExecutablePath { get; private set; }

    public IReadOnlyList<AiModelOption> Models { get; private set; } = [];

    public bool IsReady => State == OllamaEngineState.Ready;

    /// <summary>
    /// Адрес сервера (настройки или временная подмена Debug-съёмки). «localhost» заменяется на 127.0.0.1: Ollama слушает
    /// только IPv4, а на Windows попытка сначала подключиться к ::1 и получить отказ занимает около 2 с на каждый запрос.
    /// </summary>
    public Uri Endpoint => NormalizeEndpoint(_endpointOverride ?? _settings.Current.OllamaEndpoint);

    /// <summary>
    /// Адрес из настроек: без схемы — http://, некорректный или не http(s) — адрес по умолчанию (ошибка ввода в настройках
    /// не должна ронять запуск движка и запросы к нему исключением разбора URI).
    /// </summary>
    public static Uri NormalizeEndpoint(string? text)
    {
        string raw = (text ?? string.Empty).Trim().TrimEnd('/');
        if (raw.Length > 0 && !raw.Contains("://", StringComparison.Ordinal)) raw = "http://" + raw;
        if (!Uri.TryCreate(raw + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            uri = new Uri(DefaultEndpoint + "/");
        var builder = new UriBuilder(uri);
        if (string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase)) builder.Host = "127.0.0.1";
        return builder.Uri;
    }

    /// <summary>Только для Debug-автотеста: другой адрес сервера без записи в settings.json.</summary>
    public void OverrideEndpoint(string endpoint) => _endpointOverride = endpoint;

    /// <summary>Оперативная память компьютера, ГБ (оценка того, какие модели поместятся без подкачки).</summary>
    public static double TotalMemoryGb { get; } = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024;

    // ───────────────────────────── Запуск и готовность ─────────────────────────────

    /// <summary>
    /// Гарантирует работающий сервер: пинг → поиск ollama.exe → скрытый <c>ollama serve</c> → ожидание готовности →
    /// список моделей. Повторные вызовы во время запуска ждут того же результата; отмена запроса одного вызывающего
    /// прекращает только его ожидание, а не общий запуск (его отменяет лишь выход приложения).
    /// </summary>
    public Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        Task<bool> task;
        lock (_sync)
        {
            if (_disposed) return Task.FromResult(false);
            if (State == OllamaEngineState.Ready) return Task.FromResult(true);
            if (_ensureTask is not { IsCompleted: false })
            {
                var lifetime = _lifetime.Token;
                // Отмена обрабатывается внутри (false, без исключения) — задача не должна «отменяться» до старта.
                _ensureTask = Task.Run(() => EnsureCoreAsync(lifetime), CancellationToken.None);
            }
            task = _ensureTask;
        }
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    /// <summary>Запуск без исключений наружу: сбой — состояние Failed с причиной, выход приложения — false.</summary>
    private async Task<bool> EnsureCoreAsync(CancellationToken ct)
    {
        try
        {
            return await EnsureServerAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UriFormatException or System.ComponentModel.Win32Exception)
        {
            SetState(OllamaEngineState.Failed, "Сбой запуска локального интеллекта: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> EnsureServerAsync(CancellationToken ct)
    {
        SetState(OllamaEngineState.Connecting, "Подключение к " + Endpoint.GetLeftPart(UriPartial.Authority));
        if (await PingAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            return await OnReadyAsync(ct).ConfigureAwait(false);

        if (!_settings.Current.OllamaAutoStart)
        {
            SetState(OllamaEngineState.Failed, "Сервер Ollama не отвечает, автозапуск выключен в настройках");
            return false;
        }

        ExecutablePath = FindExecutable();
        if (ExecutablePath is null)
        {
            SetState(OllamaEngineState.NotInstalled, "ollama.exe не найден (PATH, %LOCALAPPDATA%\\Programs\\Ollama). Установите Ollama с ollama.com");
            return false;
        }

        SetState(OllamaEngineState.Starting, "Запуск ollama serve: " + ExecutablePath);
        ct.ThrowIfCancellationRequested();
        try
        {
            StartServer(ExecutablePath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            SetState(OllamaEngineState.Failed, "Не удалось запустить ollama serve: " + ex.Message);
            return false;
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StartupTimeout)
        {
            ct.ThrowIfCancellationRequested();
            if (_process is { } started && !IsAlive(started))
            {
                // Порт занят: другой экземпляр поднялся параллельно — подключаемся к нему; чужая программа на порту
                // (сервер не отвечает как Ollama) — понятная ошибка с журналом сервера вместо зависания.
                if (await PingAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false)) return await OnReadyAsync(ct).ConfigureAwait(false);
                SetState(OllamaEngineState.Failed, $"ollama serve завершился{ExitCodeText(started)}: {ServerLogTail()}");
                bool owned;
                lock (_sync)
                {
                    owned = ReferenceEquals(_process, started);
                    if (owned) _process = null;
                }
                if (owned) started.Dispose();
                return false;
            }
            if (await PingAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false)) return await OnReadyAsync(ct).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        SetState(OllamaEngineState.Failed, $"Сервер не ответил за {StartupTimeout.TotalSeconds:0} с: {ServerLogTail()}");
        // «Немой» сервер останавливается: следующий запрос запустит новый, а не второй экземпляр рядом с потерянным первым.
        Process? silent;
        lock (_sync)
        {
            silent = _process;
            _process = null;
        }
        if (silent is not null) StopTree(silent);
        return false;
    }

    private async Task<bool> OnReadyAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync("api/version", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            Version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Version = null;
        }
        await RefreshModelsAsync(ct).ConfigureAwait(false);
        return State == OllamaEngineState.Ready;
    }

    /// <summary>Перечитывает установленные модели (<c>/api/tags</c>) и публикует их в реестре.</summary>
    public async Task RefreshModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync("api/tags", TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            var models = ParseTags(doc.RootElement);
            Models = models;
            _registry.SetOllamaModels(models);
            string who = StartedByApp ? "запущен приложением" : "уже работал";
            SetState(OllamaEngineState.Ready, $"Ollama {Version ?? string.Empty} · {who} · моделей {models.Count}".Replace("  ", " "));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            SetState(OllamaEngineState.Failed, "Сервер Ollama не вернул список моделей: " + ex.Message);
        }
    }

    /// <summary>Разбор ответа <c>/api/tags</c>: имя, parameter_size («14.8B»), размер файла, возможности (без моделей эмбеддингов).</summary>
    public static List<AiModelOption> ParseTags(JsonElement root)
    {
        var list = new List<AiModelOption>();
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in models.EnumerateArray())
        {
            string? name = m.TryGetProperty("name", out var n) ? n.GetString() : m.TryGetProperty("model", out var mm) ? mm.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var caps = new List<string>();
            if (m.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
                caps.AddRange(c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            if (caps.Contains("embedding") && !caps.Contains("completion")) continue;
            double billions = 0;
            if (m.TryGetProperty("details", out var d) && d.TryGetProperty("parameter_size", out var ps) && ps.GetString() is { Length: > 0 } size)
                billions = ParseParameterSize(size);
            double gb = m.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long bytes) ? bytes / 1024d / 1024 / 1024 : 0;
            list.Add(AiModelCatalog.Ollama(name, billions, caps) with { SizeGb = gb });
        }
        return list.OrderBy(o => o.ModelId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>«14.8B» → 14.8, «567M» → 0.567.</summary>
    public static double ParseParameterSize(string text)
    {
        text = text.Trim().ToUpperInvariant();
        double scale = text.EndsWith('B') ? 1 : text.EndsWith('M') ? 0.001 : text.EndsWith('T') ? 1000 : 1;
        string number = text.TrimEnd('B', 'M', 'T', 'K');
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * scale : 0;
    }

    /// <summary>Сервер отвечает на <c>/api/tags</c>.</summary>
    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var response = await Http.GetAsync(new Uri(Endpoint, "api/tags"), cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Сервер был готов, но перестал отвечать (закрыт пользователем) — следующий запрос перезапустит его.</summary>
    public void MarkUnavailable(string reason)
    {
        lock (_sync)
        {
            if (State != OllamaEngineState.Ready) return;
        }
        SetState(OllamaEngineState.Failed, reason);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var response = await Http.GetAsync(new Uri(Endpoint, path), cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    // ───────────────────────────── Процесс ollama serve ─────────────────────────────

    /// <summary>ollama.exe: PATH, затем стандартные папки установки (пользовательская и для всех пользователей).</summary>
    public static string? FindExecutable()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "ollama.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Некорректный элемент PATH — пропускаем.
            }
        }
        string[] known =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe")
        ];
        return known.FirstOrDefault(File.Exists);
    }

    private void StartServer(string exe)
    {
        var info = new ProcessStartInfo(exe, "serve")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
        };
        // Папка моделей: явная из настроек, иначе — переменная пользователя/системы (процесс мог стартовать до её установки).
        string models = _settings.Current.OllamaModelsDirectory;
        if (string.IsNullOrWhiteSpace(models))
            models = Environment.GetEnvironmentVariable("OLLAMA_MODELS")
                     ?? Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User)
                     ?? Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.Machine) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(models)) info.Environment["OLLAMA_MODELS"] = models;
        var endpoint = Endpoint;
        if (!string.Equals(endpoint.GetLeftPart(UriPartial.Authority), new Uri(DefaultEndpoint).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            info.Environment["OLLAMA_HOST"] = $"{endpoint.Host}:{endpoint.Port}";

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Log(e.Data);
        process.ErrorDataReceived += (_, e) => Log(e.Data);
        process.Exited += (_, _) =>
        {
            if (!_disposed && State == OllamaEngineState.Ready) SetState(OllamaEngineState.Failed, "ollama serve завершился: " + ServerLogTail());
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Процесс не запущен");
        }
        // Сервер и запускаемые им раннеры моделей — в задании «завершить вместе с приложением» (защита от сирот при сбое).
        if (!ChildProcessJob.TryAssign(process)) Log("Объект задания Windows недоступен: сервер будет остановлен только при штатном выходе");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        bool disposed;
        lock (_sync)
        {
            disposed = _disposed;
            if (!disposed) _process = process;
        }
        // Окно закрыли, пока сервер запускался, — он не должен пережить приложение.
        if (disposed) StopTree(process);
    }

    private static string ExitCodeText(Process process)
    {
        try
        {
            return $" с кодом {process.ExitCode}";
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private void Log(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_serverLog)
        {
            _serverLog.Enqueue(line.Length > 300 ? line[..300] : line);
            while (_serverLog.Count > 40) _serverLog.Dequeue();
        }
    }

    /// <summary>Последние строки журнала запущенного сервера (для отчёта об ошибке).</summary>
    public string ServerLogTail(int lines = 4)
    {
        lock (_serverLog) return _serverLog.Count == 0 ? "журнал сервера пуст" : string.Join(" | ", _serverLog.TakeLast(lines));
    }

    private void SetState(OllamaEngineState state, string detail)
    {
        lock (_sync)
        {
            State = state;
            Detail = detail;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Останавливает сервер, если его запустило приложение (освобождает память моделей): всё дерево процессов
    /// (ollama serve и раннеры моделей) с ожиданием завершения; незавершённый фоновый запуск отменяется.
    /// </summary>
    public void Dispose()
    {
        Process? process;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            process = _process;
            _process = null;
        }
        _lifetime.Cancel();
        if (process is not null) StopTree(process);
    }

    private static void StopTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Процесс уже завершён.
        }
        finally
        {
            process.Dispose();
        }
    }
}

/// <summary>
/// Объект задания Windows (Job Object) с <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: дочерние процессы приложения, включённые
/// в задание, и все их потомки завершаются системой, когда закрывается последний дескриптор задания — то есть при любом
/// завершении приложения, в том числе аварийном или через диспетчер задач, когда код выхода не выполняется.
/// </summary>
internal static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<IntPtr> Job = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Включает процесс в задание; false — задание недоступно (тогда остаётся штатная остановка при выходе).</summary>
    public static bool TryAssign(Process process)
    {
        try
        {
            var job = Job.Value;
            return job != IntPtr.Zero && AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Дескриптор задания живёт до конца процесса приложения и намеренно не закрывается: его закрывает система.</summary>
    private static IntPtr Create()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JobObjectExtendedLimitInformationData>())) return job;
        CloseHandle(job);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformationData info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
