using System.Text.Json;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Transactions;

namespace AIArchitectStudio.Services;

/// <summary>Сведения о сохранённой сессии (файл current_session.meta.json рядом с автосохранением).</summary>
public sealed class AutoSaveSessionInfo
{
    public DateTime SavedAt { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string? OriginalPath { get; set; }
    public string? Environment { get; set; }
    public int Elements { get; set; }
    public bool HasSite { get; set; }
    public int UndoSteps { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long Bytes { get; set; }
}

/// <summary>
/// Волна 6 — автосохранение прогресса. После каждого значимого шага журнала (DocumentChanged, смена участка) — с задержкой 15 с
/// (серия правок — одна запись), и по таймеру каждые 2 минуты, если с прошлой записи что-то менялось, полный снимок проекта
/// (сцена, постройки, оси, размеры, участок ГПЗУ с подтверждёнными ограничениями, активная Среда, история чата) пишется в
/// %LocalAppData%\AIArchitectStudio\Autosave\current_session.aia.autosave (атомарно, формат .aia) + current_session.meta.json.
/// Путь проекта и признак «изменён» у документа не меняются — это не «Сохранить». При следующем запуске стартовое окно
/// предлагает восстановить сессию в 1 клик (<see cref="TryReadSession"/>).
/// </summary>
public sealed class AutoSaveService : IDisposable
{
    public const string FileName = "current_session.aia.autosave";
    public const string MetaName = "current_session.meta.json";
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions MetaJson = new() { WriteIndented = true };

    private readonly SceneStateService _scene;
    private readonly Func<(ProjectSnapshot Snapshot, string? Environment)> _capture;
    private readonly Action<Action> _runOnUi;
    private readonly Timer _debounce;
    private readonly Timer _period;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _pending;
    private bool _enabled;
    private bool _disposed;

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, FileName);
    public string MetaPath => Path.Combine(Directory, MetaName);

    /// <summary>Время последней успешной записи (null — не было).</summary>
    public DateTime? LastSaved { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Запись завершена (индикатор «Прогресс сохранён ЧЧ:ММ:СС»), в потоке интерфейса.</summary>
    public event EventHandler<AutoSaveSessionInfo>? Saved;

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIArchitectStudio", "Autosave");

    public AutoSaveService(SceneStateService scene, Func<(ProjectSnapshot Snapshot, string? Environment)> capture, Action<Action> runOnUi, string? directory = null)
    {
        _scene = scene;
        _capture = capture;
        _runOnUi = runOnUi;
        Directory = directory ?? DefaultDirectory;
        _debounce = new Timer(_ => Fire("после правки"), null, Timeout.Infinite, Timeout.Infinite);
        _period = new Timer(_ => { if (_pending) Fire("по таймеру"); }, null, Timeout.Infinite, Timeout.Infinite);
        _scene.DocumentChanged += (_, e) => { if (e.Reason != DocumentChangeReason.Filter) MarkChanged(); };
        _scene.SitePlanChanged += (_, _) => MarkChanged();
    }

    /// <summary>Включает фоновые записи (таймеры). Автотест и сценарии съёмки пишут только явным вызовом.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value) _period.Change(Period, Period);
            else
            {
                _period.Change(Timeout.Infinite, Timeout.Infinite);
                _debounce.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    /// <summary>Есть изменения с прошлой записи.</summary>
    public bool HasPendingChanges => _pending;

    /// <summary>Значимое изменение: перезапуск отсчёта 15 с.</summary>
    public void MarkChanged()
    {
        _pending = true;
        if (_enabled && !_disposed) _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    private void Fire(string reason)
    {
        if (_disposed || !_enabled) return;
        // Снимок сцены — в потоке интерфейса (элементы принадлежат ему), запись — в пуле.
        _runOnUi(() => _ = SaveNowAsync(reason));
    }

    /// <summary>Запись снимка прямо сейчас (вызывать из потока интерфейса). Пустой проект без участка не пишется.</summary>
    public async Task<AutoSaveSessionInfo?> SaveNowAsync(string reason = "вручную")
    {
        if (_scene.Elements.Count == 0 && _scene.SitePlan is null) return null;
        var (snapshot, environment) = _capture();
        var file = new SceneProjectFile
        {
            ProjectName = _scene.ProjectName,
            AppVersion = typeof(AutoSaveService).Assembly.GetName().Version?.ToString(3) ?? "1.0",
            ViewMode = snapshot.ViewMode,
            ActiveLevelFilter = snapshot.ActiveLevelFilter,
            Parameters = snapshot.Parameters,
            BuildingGroups = _scene.BuildingGroups.Select(g => g.Clone()).ToList(),
            Elements = _scene.Elements.Select(e => e.Clone()).ToList(),
            GridAxes = _scene.GridAxes.Select(a => a.Clone()).ToList(),
            PinnedDimensions = _scene.PinnedDimensions.Select(d => d.Clone()).ToList(),
            ChatHistory = snapshot.ChatHistory.ToList(),
            Site = SiteWorkspaceRecord.From(_scene.SitePlan, environment)
        };
        var info = new AutoSaveSessionInfo
        {
            SavedAt = DateTime.Now,
            ProjectName = _scene.ProjectName,
            OriginalPath = _scene.ProjectFilePath,
            Environment = environment,
            Elements = file.Elements.Count,
            HasSite = _scene.SitePlan is not null,
            UndoSteps = _scene.UndoDepth,
            Reason = reason
        };
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await file.SaveAsync(FilePath).ConfigureAwait(true);
            info.Bytes = new FileInfo(FilePath).Length;
            string tmp = MetaPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(info, MetaJson)).ConfigureAwait(true);
            File.Move(tmp, MetaPath, overwrite: true);
            LastSaved = info.SavedAt;
            LastError = null;
            _pending = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            return null;
        }
        finally
        {
            _gate.Release();
        }
        Saved?.Invoke(this, info);
        return info;
    }

    /// <summary>Сведения о сохранённой сессии в папке (null — нет файла или он повреждён).</summary>
    public static AutoSaveSessionInfo? TryReadSession(string? directory = null)
    {
        directory ??= DefaultDirectory;
        string file = Path.Combine(directory, FileName), meta = Path.Combine(directory, MetaName);
        if (!File.Exists(file)) return null;
        try
        {
            var info = File.Exists(meta) ? JsonSerializer.Deserialize<AutoSaveSessionInfo>(File.ReadAllText(meta)) : null;
            info ??= new AutoSaveSessionInfo { SavedAt = File.GetLastWriteTime(file), ProjectName = "Проект" };
            info.Bytes = new FileInfo(file).Length;
            return info;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Удаление сохранённой сессии (после «Начать заново» в стартовом окне).</summary>
    public static void Discard(string? directory = null)
    {
        directory ??= DefaultDirectory;
        foreach (var name in new[] { FileName, MetaName })
        {
            try { File.Delete(Path.Combine(directory, name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Dispose();
        _period.Dispose();
    }
}
