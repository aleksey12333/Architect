using System.Text.Json;
using System.Text.Json.Serialization;
using AIArchitectStudio.Services.AI;

namespace AIArchitectStudio.Services;

/// <summary>Облачная модель из кэша последней проверки ключа (сам ключ в settings.json не хранится).</summary>
public sealed class CachedModel
{
    public string Id { get; set; } = string.Empty;
    public bool Vision { get; set; } = true;
}

/// <summary>Пользовательские настройки приложения.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Только для миграции: ключ из старых версий, где он хранился открытым текстом.
    /// При загрузке переносится в PasswordVault и удаляется из settings.json.
    /// </summary>
    [JsonPropertyName("GeminiApiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyGeminiApiKey { get; set; }

    public string ThemeName { get; set; } = ThemeService.CupertinoDark;
    public string Language { get; set; } = LocalizationService.Russian;

    /// <summary>Ключ выбранной модели (<c>local:qwen3b</c>, <c>gemini:gemini-2.5-flash</c>, <c>web:chatgpt</c>…).</summary>
    public string AiModel { get; set; } = AiModelCatalog.DefaultModelId;

    /// <summary>Списки моделей, полученные при последней проверке ключей: префикс провайдера → модели.</summary>
    public Dictionary<string, List<CachedModel>> CloudModelCache { get; set; } = [];

    /// <summary>Цена бетона, ₽ за м³.</summary>
    public double ConcretePricePerM3 { get; set; } = DefaultConcretePrice;

    /// <summary>Цена остекления, ₽ за м².</summary>
    public double GlazingPricePerM2 { get; set; } = 18000;

    /// <summary>Цена арматурной стали и закладного проката, ₽ за тонну.</summary>
    public double RebarPricePerTon { get; set; } = DefaultRebarPrice;

    /// <summary>Расценки по умолчанию для сметы монолитного каркаса: бетон, ₽/м³, и арматура, ₽/т.</summary>
    public const double DefaultConcretePrice = 8500;
    public const double DefaultRebarPrice = 75000;

    /// <summary>Параметры площадки для экспресс-расчёта: снег, ветер, сейсмика, классы бетона и арматуры.</summary>
    public Models.StructuralLoadSettings StructuralLoads { get; set; } = new();

    /// <summary>Папка с локальными .gguf моделями (пусто — автопоиск LocalModels).</summary>
    public string LocalModelsDirectory { get; set; } = string.Empty;

    public bool AppendToSiteByDefault { get; set; }

    /// <summary>«Авто-снимок вида»: кадр 3D-вьюпорта прикладывается к каждому запросу проектировщику, принимающему изображения.</summary>
    public bool AutoViewSnapshot { get; set; } = true;

    /// <summary>Проверка достоверности ответа внешнего генератора (лист согласования правок локального аудитора).</summary>
    public bool VerifyExternalDesigns { get; set; } = true;

    /// <summary>Регион строительства (<see cref="SolarLocations"/>): широта, долгота и часовой пояс Солнца и инсоляции.</summary>
    public string SolarRegionId { get; set; } = SolarLocations.MoscowId;

    /// <summary>Мультиагентное конструкторское бюро: режим дока, согласование правок, модели ролей.</summary>
    public CouncilSettings Council { get; set; } = new();

    /// <summary>Адрес локального ИИ-сервера Ollama.</summary>
    public string OllamaEndpoint { get; set; } = OllamaProcessManager.DefaultEndpoint;

    /// <summary>
    /// Основная локальная модель программы, выбранная вручную (null — автовыбор лучшей по инженерному рейтингу; по умолчанию не пишется).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryLocalModelId { get; set; }

    /// <summary>Волна 6: Среда девелопера, выбранная на начальном экране или в верхнем баре (null — «Объём и Конструктив»; не пишется по умолчанию).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeveloperEnvironment { get; set; }

    /// <summary>Запускать <c>ollama serve</c> скрыто при старте, если сервер не отвечает (Zero-Config).</summary>
    public bool OllamaAutoStart { get; set; } = true;

    /// <summary>Папка моделей Ollama для запущенного приложением сервера (пусто — переменная OLLAMA_MODELS пользователя).</summary>
    public string OllamaModelsDirectory { get; set; } = string.Empty;

    /// <summary>Профиль работы (онбординг): какие панели главного окна видны. По умолчанию — полный интерфейс.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<Models.UserExperienceProfile>))]
    public Models.UserExperienceProfile ExperienceProfile { get; set; } = Models.UserExperienceProfile.StructuralEngineer;

    /// <summary>Онбординг «Укажите ваш опыт в проектировании» пройден.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>Интерактивный тур по интерфейсу пройден или пропущен (по умолчанию не пишется в settings.json).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InterfaceTourCompleted { get; set; }

    /// <summary>Модули главного окна («Модули и Плагины»); null — набор по умолчанию для квалификации (настройки старых версий).</summary>
    public Models.WorkspaceModules? Modules { get; set; }

    [JsonIgnore]
    public Models.WorkspaceModules EffectiveModules => Modules ?? Models.WorkspaceModules.ForProfile(ExperienceProfile);

    /// <summary>Показывать заставку и стартовое окно с шаблонами при запуске.</summary>
    public bool ShowWelcomeScreen { get; set; } = true;

    /// <summary>Тема следует системной (светлая / тёмная по ключу AppsUseLightTheme); ручной выбор темы снимает флаг.</summary>
    public bool FollowSystemTheme { get; set; }

    /// <summary>Недавние проекты .aia (новые — первыми) для стартового окна.</summary>
    public List<string> RecentProjects { get; set; } = [];

    public const int RecentProjectsLimit = 6;

    /// <summary>Поведение расчётной среды («Нормативы и Расчёт»): живой пересчёт, ссылки на СП, состав итога, μmin.</summary>
    public EngineeringOptions Engineering { get; set; } = new();

    /// <summary>Загруженные прайс-листы поставщиков (окно «Ведомость объёмов» → «Загрузить прайс»).</summary>
    public List<Models.Economics.MaterialPriceCatalog> PriceCatalogs { get; set; } = [];

    /// <summary>Название выбранной базы цен; пусто — расценки проекта.</summary>
    public string ActivePriceCatalog { get; set; } = string.Empty;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.CloudModelCache = CloudModelCache.ToDictionary(p => p.Key, p => p.Value.ToList());
        copy.StructuralLoads = StructuralLoads.Clone();
        copy.Council = Council.Clone();
        copy.RecentProjects = RecentProjects.ToList();
        copy.Engineering = Engineering.Clone();
        copy.PriceCatalogs = PriceCatalogs.Select(c => c.Clone()).ToList();
        return copy;
    }

    /// <summary>Поднимает путь проекта в начало списка недавних (без дублей, не больше <see cref="RecentProjectsLimit"/>).</summary>
    public void RememberRecent(string path)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > RecentProjectsLimit) RecentProjects.RemoveRange(RecentProjectsLimit, RecentProjects.Count - RecentProjectsLimit);
    }
}

/// <summary>Тумблеры поведения расчётной и сметной среды (раздел настроек «Нормативы и Расчёт»).</summary>
public sealed class EngineeringOptions
{
    /// <summary>Пересчёт КЭ-схемы на каждое изменение при перетаскивании (выключено — после паузы в правках).</summary>
    public bool LiveRecalculation { get; set; } = true;

    /// <summary>Ссылки на пункты СП во всплывающих подсказках карточки усилий инспектора.</summary>
    public bool NormTooltips { get; set; } = true;

    /// <summary>Стальной прокат и остекление входят в итоговую стоимость каркаса (по умолчанию — только бетон и арматура).</summary>
    public bool IncludeSteelAndGlazing { get; set; }

    /// <summary>Строгий контроль минимального армирования: расход рабочей арматуры не ниже μmin (СП 63, п. 10.3.6) и μ расчёта.</summary>
    public bool StrictMinimumReinforcement { get; set; }

    public EngineeringOptions Clone() => (EngineeringOptions)MemberwiseClone();
}

/// <summary>Сохранение и загрузка настроек в %LOCALAPPDATA%\AIArchitectStudio\settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly object _sync = new();
    private readonly SecretStore _secrets;

    public string SettingsDirectory { get; }
    public string SettingsFilePath { get; }
    public AppSettings Current { get; private set; } = new();

    /// <summary>Первый запуск: файла настроек ещё не было (онбординг и тема по системе).</summary>
    public bool IsFirstRun { get; private set; }

    public event EventHandler<AppSettings>? SettingsChanged;

    public SettingsService(SecretStore secrets)
    {
        _secrets = secrets;
        SettingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIArchitectStudio");
        SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
        IsFirstRun = !File.Exists(SettingsFilePath);
        Load();
        MigrateLegacyApiKey();
    }

    /// <summary>API-ключ облачного провайдера из защищённого хранилища Windows (PasswordVault).</summary>
    public string GetApiKey(AiProviderKind provider)
    {
        try
        {
            return _secrets.Read(SecretStore.KeyNameFor(provider));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return string.Empty;
        }
    }

    /// <summary>Сохраняет API-ключ провайдера в PasswordVault (пустое значение удаляет ключ).</summary>
    public void SetApiKey(AiProviderKind provider, string? value) => _secrets.Write(SecretStore.KeyNameFor(provider), value);

    /// <summary>Переносит открытый ключ из старого settings.json в PasswordVault и стирает его из файла.</summary>
    private void MigrateLegacyApiKey()
    {
        if (string.IsNullOrWhiteSpace(Current.LegacyGeminiApiKey))
        {
            Current.LegacyGeminiApiKey = null;
            return;
        }
        try
        {
            SetApiKey(AiProviderKind.Gemini, Current.LegacyGeminiApiKey);
            Current.LegacyGeminiApiKey = null;
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Хранилище недоступно — ключ остаётся только в памяти до следующего успешного сохранения.
        }
    }

    public void Load()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Повреждённый файл настроек не должен блокировать запуск — откатываемся к значениям по умолчанию.
                Current = new AppSettings();
            }
            Sanitize(Current);
        }
        ApplyRuntimeOptions();
    }

    /// <summary>Глобальные флаги расчёта из настроек — до события SettingsChanged (смета и ведомость пересчитываются уже с ними).</summary>
    private void ApplyRuntimeOptions() => ReinforcementCalculator.StrictMinimum = Current.Engineering.StrictMinimumReinforcement;

    public void Save()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(SettingsDirectory);
            var tmp = SettingsFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, SettingsFilePath, overwrite: true);
        }
        ApplyRuntimeOptions();
        SettingsChanged?.Invoke(this, Current);
    }

    /// <summary>
    /// Изменяет настройки и сразу сохраняет их (безопасно из фоновых потоков проверки ключей). Копия при записи: правка
    /// применяется к клону и публикуется заменой ссылки — UI-поток, перечисляющий словари настроек (кэш моделей, привязки
    /// ролей Совета), никогда не видит их в момент изменения из пула.
    /// </summary>
    public void Update(Action<AppSettings> mutate)
    {
        lock (_sync)
        {
            var next = Current.Clone();
            mutate(next);
            Sanitize(next);
            Current = next;
            Save();
        }
    }

    /// <summary>Находит папку LocalModels: из настроек либо поиском вверх от каталога приложения (без чтения файлов моделей).</summary>
    public string? ResolveLocalModelsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(Current.LocalModelsDirectory) && Directory.Exists(Current.LocalModelsDirectory))
            return Current.LocalModelsDirectory;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "LocalModels");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Directory.Exists(AiModelCatalog.DefaultLocalModelsDirectory) ? AiModelCatalog.DefaultLocalModelsDirectory : null;
    }

    private static void Sanitize(AppSettings s)
    {
        if (!ThemeService.ThemeNames.Contains(s.ThemeName)) s.ThemeName = ThemeService.CupertinoDark;
        if (s.Language is not (LocalizationService.Russian or LocalizationService.English)) s.Language = LocalizationService.Russian;
        s.AiModel = AiModelCatalog.MigrateLegacyId(s.AiModel);
        s.CloudModelCache ??= [];
        if (s.ConcretePricePerM3 < 0 || double.IsNaN(s.ConcretePricePerM3)) s.ConcretePricePerM3 = AppSettings.DefaultConcretePrice;
        if (s.GlazingPricePerM2 < 0 || double.IsNaN(s.GlazingPricePerM2)) s.GlazingPricePerM2 = 18000;
        if (s.RebarPricePerTon < 0 || double.IsNaN(s.RebarPricePerTon)) s.RebarPricePerTon = AppSettings.DefaultRebarPrice;
        s.StructuralLoads ??= new Models.StructuralLoadSettings();
        s.StructuralLoads.Sanitize();
        s.LocalModelsDirectory ??= string.Empty;
        s.Council ??= new CouncilSettings();
        s.Council.Bindings ??= [];
        if (string.IsNullOrWhiteSpace(s.OllamaEndpoint) || !Uri.TryCreate(s.OllamaEndpoint, UriKind.Absolute, out _))
            s.OllamaEndpoint = OllamaProcessManager.DefaultEndpoint;
        s.OllamaModelsDirectory ??= string.Empty;
        if (!Enum.IsDefined(s.ExperienceProfile)) s.ExperienceProfile = Models.UserExperienceProfile.StructuralEngineer;
        s.RecentProjects = (s.RecentProjects ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Take(AppSettings.RecentProjectsLimit).ToList();
        s.Engineering ??= new EngineeringOptions();
        s.PriceCatalogs = (s.PriceCatalogs ?? []).Where(c => c is not null).Select(c => c.Normalized()).Where(c => c.Items.Count > 0).ToList();
        s.ActivePriceCatalog ??= string.Empty;
        if (s.ActivePriceCatalog.Length > 0 && !s.PriceCatalogs.Any(c => c.Name == s.ActivePriceCatalog)) s.ActivePriceCatalog = string.Empty;
    }
}
