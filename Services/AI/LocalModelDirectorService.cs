using System.Diagnostics;
using System.Globalization;

namespace AIArchitectStudio.Services.AI;

/// <summary>Замер скорости локальной модели: токенов в секунду генерации и задержка ответа.</summary>
public sealed record ModelSpeedSample(double TokensPerSecond, double LatencyMs, DateTime MeasuredAt);

/// <summary>
/// Оценка локальной модели для инженерных задач программы: индекс <see cref="EngineeringScore"/> (0…100) и его составляющие —
/// семейство и специализация (логика, код, математика), глубина по числу параметров, посадка в память ПК, скорость и доступность.
/// </summary>
public sealed class LocalModelCandidate
{
    public required AiModelOption Option { get; init; }
    public string Family { get; init; } = string.Empty;
    public string Specialization { get; init; } = string.Empty;
    public double ParameterBillions { get; init; }
    public double SizeGb { get; init; }
    public bool FitsMemory { get; init; }
    public bool Available { get; init; }
    public double FamilyScore { get; init; }
    public double DepthScore { get; init; }
    public double MemoryScore { get; init; }
    public double SpeedScore { get; init; }
    public double EngineeringScore { get; init; }
    public ModelSpeedSample? Speed { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>«92 / 100 · 38 ток/с» — рейтинг и скорость (если замерена).</summary>
    public string RatingText(CultureInfo culture) =>
        $"{EngineeringScore.ToString("0", culture)} / 100" + (Speed is { } s ? $" · {s.TokensPerSecond.ToString("0", culture)} ток/с" : string.Empty);
}

/// <summary>
/// Директор локальных моделей: находит все модели на компьютере (сервер локального ядра — /api/tags, папка GGUF LocalModels),
/// ранжирует их по инженерному индексу и назначает лучшую «Основной локальной моделью программы» для внутренних задач
/// (Инженерный агент, цепочка рассуждений, аудит, разбор смет). Модель не исключается из проектирования и Совета КБ: директор
/// только выбирает «внутренний мозг», привязки ролей Совета и выбор модели дока не меняются.
/// </summary>
public sealed class LocalModelDirectorService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ModelSpeedSample> _speed = [];
    private List<LocalModelCandidate> _ranked = [];
    private string? _manualId;

    /// <summary>Ранжирование или выбор изменились (подписчики маршалят в UI-поток сами).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<LocalModelCandidate> Candidates
    {
        get { lock (_sync) return _ranked.ToList(); }
    }

    /// <summary>Модель выбрана вручную (иначе — автовыбор лучшей).</summary>
    public bool IsManual
    {
        get { lock (_sync) return _manualId is not null && _ranked.Any(c => c.Option.Id == _manualId && c.Available); }
    }

    /// <summary>Основная локальная модель программы (ручной выбор, иначе лучшая доступная); null — локальных моделей нет.</summary>
    public LocalModelCandidate? Primary
    {
        get
        {
            lock (_sync)
            {
                if (_manualId is not null && _ranked.FirstOrDefault(c => c.Option.Id == _manualId && c.Available) is { } manual) return manual;
                return _ranked.FirstOrDefault(c => c.Available);
            }
        }
    }

    public string? ManualId
    {
        get { lock (_sync) return _manualId; }
    }

    /// <summary>Пересчитать рейтинг по списку моделей (модели Ollama + встроенные GGUF с признаком наличия файла).</summary>
    public void Refresh(IEnumerable<AiModelOption> ollamaModels, IEnumerable<(AiModelOption Option, bool FileExists)> ggufModels, double memoryGb)
    {
        Dictionary<string, ModelSpeedSample> speed;
        lock (_sync) speed = new Dictionary<string, ModelSpeedSample>(_speed);
        var ranked = Rank(ollamaModels.Select(m => (m, true)).Concat(ggufModels), memoryGb, speed);
        lock (_sync)
        {
            _ranked = ranked;
            _memoryGb = memoryGb;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ручной выбор основной модели (null — снова автовыбор лучшей).</summary>
    public void SetManual(string? id)
    {
        lock (_sync) _manualId = string.IsNullOrEmpty(id) ? null : id;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>«Автовыбор лучшей модели»: снять ручной выбор и вернуть лучшую по индексу.</summary>
    public LocalModelCandidate? AutoSelect()
    {
        SetManual(null);
        return Primary;
    }

    public void RecordSpeed(string id, ModelSpeedSample sample)
    {
        lock (_sync)
        {
            _speed[id] = sample;
            // Скорость входит в индекс: пересчёт составляющей без повторного опроса сервера.
            _ranked = _ranked.Select(c => c.Option.Id == id ? Score(c.Option, c.Available, _memoryGb, sample) : c)
                .OrderByDescending(c => c.Available).ThenByDescending(c => c.EngineeringScore).ThenBy(c => c.Option.Id, StringComparer.Ordinal).ToList();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private double _memoryGb;

    /// <summary>
    /// Ранжирование (чистая функция): доступные — первыми, затем по индексу (убывание), при равенстве — по ключу (порядок
    /// устойчив). <paramref name="memoryGb"/> ≤ 0 — память неизвестна (без штрафа).
    /// </summary>
    public static List<LocalModelCandidate> Rank(IEnumerable<(AiModelOption Option, bool Available)> models, double memoryGb,
        IReadOnlyDictionary<string, ModelSpeedSample>? speed = null)
    {
        return models
            .GroupBy(m => m.Option.Id).Select(g => g.First())
            .Select(m => Score(m.Option, m.Available, memoryGb, speed?.GetValueOrDefault(m.Option.Id)))
            .OrderByDescending(c => c.Available).ThenByDescending(c => c.EngineeringScore).ThenBy(c => c.Option.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Инженерный индекс модели (0…100):
    /// семейство (qwen3 — 30, qwen2.5-coder / deepseek-r1 — 28, gpt-oss — 27, qwen2.5 — 25, llama3 — 23, mistral/mixtral — 22,
    /// gemma — 21, phi — 18, прочие — 14) + специализация (код +6, рассуждения/математика +6, instruct +2, только зрение −4);
    /// глубина 12·log2(1 + B/1,5) до 38 (1,5B — 12, 3B — 19, 7B — 30, 8B — 32, 14B — 38); память: веса > 75 % ОЗУ — −45 (не помещается),
    /// > 45 % — −8 (вытесняет ОС); сервер локального ядра +6 (большой контекст, потоковая генерация), встроенный GGUF — 0;
    /// скорость (замер): до +10 (≥ 40 ток/с), < 5 ток/с — −6.
    /// </summary>
    public static LocalModelCandidate Score(AiModelOption m, bool available, double memoryGb, ModelSpeedSample? speed = null)
    {
        string id = m.ModelId.ToLowerInvariant();
        var reasons = new List<string>();
        (string family, double familyScore) = id switch
        {
            _ when id.Contains("qwen3") => ("Qwen 3", 30.0),
            _ when id.Contains("qwen2.5-coder") || id.Contains("qwen2.5coder") => ("Qwen 2.5 Coder", 28.0),
            _ when id.Contains("deepseek-r1") => ("DeepSeek-R1", 28.0),
            _ when id.Contains("gpt-oss") => ("gpt-oss", 27.0),
            _ when id.Contains("qwq") => ("QwQ", 27.0),
            _ when id.Contains("qwen2.5") || id.Contains("qwen2") => ("Qwen 2.5", 25.0),
            _ when id.Contains("deepseek") => ("DeepSeek", 24.0),
            _ when id.Contains("llama3") || id.Contains("llama-3") => ("Llama 3", 23.0),
            _ when id.Contains("mistral") || id.Contains("mixtral") || id.Contains("ministral") => ("Mistral", 22.0),
            _ when id.Contains("gemma") => ("Gemma", 21.0),
            _ when id.Contains("phi") => ("Phi", 18.0),
            _ when id.Contains("llama") => ("Llama", 19.0),
            _ => ("Прочие", 14.0)
        };
        reasons.Add($"семейство {family}: {familyScore:0}");

        var spec = new List<string>();
        double specScore = 0;
        if (id.Contains("coder") || id.Contains("code"))
        {
            specScore += 6;
            spec.Add("код");
        }
        if (m.IsReasoning || id.Contains("math") || id.Contains("r1") || id.Contains("qwq"))
        {
            specScore += 6;
            spec.Add("логика и математика");
        }
        if (id.Contains("instruct") || id.Contains("-it") || id.Contains(":it"))
        {
            specScore += 2;
            spec.Add("инструкции");
        }
        if (id.Contains("llava") || id.Contains("vision") || id.Contains("-vl"))
        {
            specScore -= 4;
            spec.Add("зрение");
        }
        if (specScore != 0) reasons.Add($"специализация ({string.Join(", ", spec)}): {specScore:+0;-0}");

        double billions = m.ParameterBillions > 0 ? m.ParameterBillions : AiModelCatalog.GuessBillions(m.ModelId) is var g && g > 0 ? g : m.IsLocal ? (id.Contains("7b") ? 7 : 3) : 7;
        double depth = Math.Min(38, 12 * Math.Log2(1 + billions / 1.5));
        reasons.Add($"глубина {billions.ToString("0.#", CultureInfo.InvariantCulture)}B: {depth:0.0}");

        double size = m.SizeGb > 0 ? m.SizeGb : billions * 0.62;
        double memory = 0;
        bool fits = true;
        if (memoryGb > 0)
        {
            if (size > memoryGb * 0.75)
            {
                memory = -45;
                fits = false;
                reasons.Add($"веса {size:0.#} ГБ не помещаются в {memoryGb:0} ГБ ОЗУ: −45");
            }
            else if (size > memoryGb * 0.45)
            {
                memory = -8;
                reasons.Add($"веса {size:0.#} ГБ — больше 45 % ОЗУ: −8");
            }
        }
        double server = m.IsOllama ? 6 : 0;
        if (server > 0) reasons.Add("сервер локального ядра: +6");

        double speedScore = 0;
        if (speed is { TokensPerSecond: > 0 } s)
        {
            speedScore = s.TokensPerSecond >= 5 ? Math.Min(10, s.TokensPerSecond / 4) : -6;
            reasons.Add($"скорость {s.TokensPerSecond:0} ток/с: {speedScore:+0.0;-0.0}");
        }
        if (!available) reasons.Add("файл модели не найден — недоступна");

        double total = Math.Clamp(familyScore + specScore + depth + memory + server + speedScore, 0, 100);
        return new LocalModelCandidate
        {
            Option = m,
            Family = family,
            Specialization = spec.Count == 0 ? "общего назначения" : string.Join(", ", spec),
            ParameterBillions = billions,
            SizeGb = size,
            FitsMemory = fits,
            Available = available,
            FamilyScore = familyScore + specScore,
            DepthScore = depth,
            MemoryScore = memory + server,
            SpeedScore = speedScore,
            EngineeringScore = Math.Round(total, 1),
            Speed = speed,
            Reasons = reasons
        };
    }

    /// <summary>
    /// Короткий замер скорости основной модели (ответ из нескольких десятков токенов): токенов в секунду по счётчику сервера
    /// (иначе ≈ символы / 4) и задержка. Сбой замера не влияет на выбор.
    /// </summary>
    public async Task<ModelSpeedSample?> MeasureAsync(IAiProvider provider, AiModelOption model, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            var response = await provider.GenerateAsync(new AiRequest
            {
                Model = model,
                SystemPrompt = "Ответь коротким JSON-объектом.",
                UserPrompt = "Верни {\"status\":\"ok\",\"steps\":[1,2,3,4,5]} без пояснений.",
                Temperature = 0,
                MaxOutputTokens = 64
            }, cts.Token).ConfigureAwait(false);
            double seconds = Math.Max(0.05, response.Elapsed.TotalSeconds);
            double tokens = response.OutputTokens is > 0 ? response.OutputTokens.Value : Math.Max(1, response.Text.Length / 4.0);
            var sample = new ModelSpeedSample(tokens / seconds, sw.Elapsed.TotalMilliseconds, DateTime.Now);
            RecordSpeed(model.Id, sample);
            return sample;
        }
        catch (Exception ex) when (ex is AiProviderException or OperationCanceledException or HttpRequestException or IOException)
        {
            return null;
        }
    }
}
