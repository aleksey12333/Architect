namespace AIArchitectStudio.Services.AI;

public enum AiKeyState
{
    NotSet,
    Unchecked,
    Checking,
    Valid,
    Invalid,
    Error
}

/// <summary>Состояние API-ключа провайдера после проверки AiKeyInspectorService.</summary>
public sealed record AiKeyStatus(AiProviderKind Provider, AiKeyState State, string Message, int ModelCount = 0, DateTime? CheckedAt = null)
{
    public static AiKeyStatus NotSet(AiProviderKind provider) => new(provider, AiKeyState.NotSet, string.Empty);
}

/// <summary>
/// Живой реестр моделей ИИ-дока: локальные Qwen (всегда), облачные модели проверенных ключей и веб-аккаунты.
/// Потокобезопасен: проверка ключей идёт в фоне, событие ModelsChanged подписчики маршалят в UI-поток сами.
/// </summary>
public sealed class AiModelRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<AiProviderKind, List<AiModelOption>> _cloud = [];
    private readonly Dictionary<AiProviderKind, AiKeyStatus> _status = [];
    private readonly SettingsService _settings;
    private List<AiModelOption> _localExtras = [];
    private List<AiModelOption> _ollama = [];
    private List<AiModelOption> _unified = [];

    public event EventHandler? ModelsChanged;

    /// <summary>
    /// Модели единого облачного шлюза сеанса (волна 4.1: Sonnet 5.5 и GPT 5.6 Luna первыми). Только в памяти — в settings.json
    /// не кэшируются (ключ шлюза живёт в переменных окружения сеанса).
    /// </summary>
    public void SetUnifiedModels(IReadOnlyList<AiModelOption> models)
    {
        lock (_sync) _unified = models.ToList();
        ModelsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<AiModelOption> UnifiedModels
    {
        get
        {
            lock (_sync) return _unified.ToList();
        }
    }

    public AiModelRegistry(SettingsService settings)
    {
        _settings = settings;
        foreach (var provider in AiModelCatalog.CloudProviders)
        {
            _status[provider] = AiKeyStatus.NotSet(provider);
            if (settings.Current.CloudModelCache.TryGetValue(AiModelCatalog.IdPrefix(provider), out var cached) && cached.Count > 0)
            {
                _cloud[provider] = cached.Select(c => AiModelCatalog.Cloud(provider, c.Id, c.Vision)).ToList();
                _status[provider] = new AiKeyStatus(provider, AiKeyState.Unchecked, string.Empty, cached.Count);
            }
        }
        ScanLocalModels();
    }

    /// <summary>Все пункты меню: модели сервера Ollama → встроенные LLamaSharp → облачные (по провайдерам) → веб-аккаунты.</summary>
    public IReadOnlyList<AiModelOption> All
    {
        get
        {
            lock (_sync)
            {
                var list = new List<AiModelOption>(_ollama) { AiModelCatalog.LocalQwen3B, AiModelCatalog.LocalQwen7B };
                list.AddRange(_localExtras);
                list.AddRange(_unified);
                foreach (var provider in AiModelCatalog.CloudProviders)
                    if (_cloud.TryGetValue(provider, out var models)) list.AddRange(models);
                list.AddRange(AiModelCatalog.WebOptions);
                return list;
            }
        }
    }

    public IReadOnlyList<AiModelOption> LocalModels
    {
        get
        {
            lock (_sync) return [AiModelCatalog.LocalQwen3B, AiModelCatalog.LocalQwen7B, .. _localExtras];
        }
    }

    /// <summary>Модели локального сервера Ollama (последний успешный опрос /api/tags).</summary>
    public IReadOnlyList<AiModelOption> OllamaModels
    {
        get
        {
            lock (_sync) return _ollama.ToList();
        }
    }

    /// <summary>Публикует установленные модели Ollama (меню дока и списки ролей Совета).</summary>
    public void SetOllamaModels(IReadOnlyList<AiModelOption> models)
    {
        lock (_sync)
        {
            if (_ollama.Count == models.Count && _ollama.Zip(models).All(p => p.First.Id == p.Second.Id && p.First.Capabilities.SequenceEqual(p.Second.Capabilities))) return;
            _ollama = models.ToList();
        }
        ModelsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<AiModelOption> CloudModels(AiProviderKind provider)
    {
        lock (_sync) return _cloud.TryGetValue(provider, out var list) ? list.ToList() : [];
    }

    public bool HasAnyCloudModels
    {
        get
        {
            lock (_sync) return _cloud.Values.Any(l => l.Count > 0);
        }
    }

    public AiKeyStatus GetStatus(AiProviderKind provider)
    {
        lock (_sync) return _status.TryGetValue(provider, out var s) ? s : AiKeyStatus.NotSet(provider);
    }

    /// <summary>Находит модель по сохранённому ключу; неизвестные облачные ключи восстанавливаются по префиксу.</summary>
    public AiModelOption Resolve(string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            var match = All.FirstOrDefault(o => o.Id == id);
            if (match is not null) return match;
            if (AiModelCatalog.FromId(id) is { } restored) return restored;
        }
        return AiModelCatalog.LocalQwen3B;
    }

    public void SetStatus(AiKeyStatus status)
    {
        lock (_sync) _status[status.Provider] = status;
        ModelsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Публикует проверенный список моделей провайдера и кэширует его в settings.json (без ключей).</summary>
    public void SetCloudModels(AiProviderKind provider, IReadOnlyList<AiModelOption> models)
    {
        lock (_sync) _cloud[provider] = models.ToList();
        try
        {
            _settings.Update(s => s.CloudModelCache[AiModelCatalog.IdPrefix(provider)] =
                models.Select(m => new CachedModel { Id = m.ModelId, Vision = m.SupportsVision }).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Кэш списка моделей — удобство: сбой записи не мешает работе с моделями в текущем сеансе.
        }
        ModelsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Убирает облачные модели провайдера (ключ удалён или отклонён).</summary>
    public void ClearCloudModels(AiProviderKind provider)
    {
        bool removed;
        lock (_sync) removed = _cloud.Remove(provider);
        if (!removed) return;
        try
        {
            _settings.Update(s => s.CloudModelCache.Remove(AiModelCatalog.IdPrefix(provider)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // См. SetCloudModels.
        }
        ModelsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Подхватывает дополнительные .gguf файлы из папки LocalModels (только имена файлов, без чтения содержимого).</summary>
    public void ScanLocalModels()
    {
        var extras = new List<AiModelOption>();
        try
        {
            var dir = _settings.ResolveLocalModelsDirectory();
            if (dir is not null)
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*.gguf").Order())
                {
                    var file = Path.GetFileName(path);
                    if (file.Equals(AiModelCatalog.Qwen3BFile, StringComparison.OrdinalIgnoreCase) ||
                        file.Equals(AiModelCatalog.Qwen7BFile, StringComparison.OrdinalIgnoreCase))
                        continue;
                    extras.Add(AiModelCatalog.LocalFile(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Папка недоступна — остаются только две постоянные модели Qwen.
        }
        lock (_sync) _localExtras = extras;
    }
}
