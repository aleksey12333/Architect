namespace AIArchitectStudio.Models;

/// <summary>
/// Квалификация пользователя (онбординг стартового окна «Укажите ваш опыт в проектировании»): определяет базовый интерфейс
/// и набор модулей по умолчанию (<see cref="WorkspaceModules.ForProfile"/>).
/// <see cref="Beginner"/> — «Студент / Практикант»: эскизы, 3D-моделирование, макет; <see cref="Architect"/> — «Архитектор»: плюс оси,
/// разрезы, привязки, инсоляция, листы ГОСТ; <see cref="StructuralEngineer"/> — «Инженер-конструктор (ГИП)»: весь интерфейс.
/// </summary>
public enum UserExperienceProfile
{
    Beginner,
    Architect,
    StructuralEngineer
}

/// <summary>Подписи и глифы профилей для онбординга, стартового окна и настроек.</summary>
public static class UserExperienceProfiles
{
    public static IReadOnlyList<UserExperienceProfile> All { get; } =
        [UserExperienceProfile.Beginner, UserExperienceProfile.Architect, UserExperienceProfile.StructuralEngineer];

    /// <summary>Ключ локализации названия: «Студент / Практикант» / «Архитектор» / «Инженер-конструктор (ГИП)»; + "Short" — для сегмента настроек.</summary>
    public static string TitleKey(UserExperienceProfile profile) => profile switch
    {
        UserExperienceProfile.Beginner => "ProfileBeginner",
        UserExperienceProfile.Architect => "ProfileArchitect",
        _ => "ProfileEngineer"
    };

    public static string Glyph(UserExperienceProfile profile) => profile switch
    {
        UserExperienceProfile.Beginner => "",
        UserExperienceProfile.Architect => "",
        _ => ""
    };

    public static string ColorHex(UserExperienceProfile profile) => profile switch
    {
        UserExperienceProfile.Beginner => "#30D158",
        UserExperienceProfile.Architect => "#0A84FF",
        _ => "#FF9F0A"
    };

    public static UserExperienceProfile Parse(string? value, UserExperienceProfile fallback) => value?.Trim().ToLowerInvariant() switch
    {
        "beginner" or "sketch" => UserExperienceProfile.Beginner,
        "architect" => UserExperienceProfile.Architect,
        "engineer" or "structuralengineer" or "structural" => UserExperienceProfile.StructuralEngineer,
        _ => fallback
    };
}

/// <summary>
/// Модули главного окна («Настройки → Модули и Плагины»), независимые от квалификации: онбординг и смена квалификации
/// включают набор <see cref="ForProfile"/>, дальше пользователь включает и выключает любой модуль сам.
/// </summary>
public sealed record WorkspaceModules
{
    /// <summary>Расчёт инсоляции окон по СанПиН 1.2.3685-21 (блок панели «Инсоляция и Тени»).</summary>
    public bool Insolation { get; init; } = true;

    /// <summary>КЭ-анализ: вкладка «Анализ» (нагрузки, результаты), карта напряжений, усилия в инспекторе.</summary>
    public bool StructuralAnalysis { get; init; } = true;

    /// <summary>Визуализация 3D-армирования (КЖ): каркасы и сетки ж/б элементов поверх модели.</summary>
    public bool Reinforcement3D { get; init; } = true;

    /// <summary>Инспектор коллизий каркаса (раздел вкладки «Анализ»).</summary>
    public bool ClashInspector { get; init; } = true;

    /// <summary>Ведомость объёмов бетона, арматуры, остекления и проката.</summary>
    public bool QuantitySchedule { get; init; } = true;

    public static WorkspaceModules ForProfile(UserExperienceProfile profile) => profile switch
    {
        UserExperienceProfile.Beginner => new() { Insolation = false, StructuralAnalysis = false, Reinforcement3D = false, ClashInspector = false, QuantitySchedule = false },
        UserExperienceProfile.Architect => new() { Insolation = true, StructuralAnalysis = false, Reinforcement3D = false, ClashInspector = false, QuantitySchedule = true },
        _ => new()
    };
}

/// <summary>Выбор пользователя в стартовом окне: пустой проект, шаблон или файл.</summary>
public sealed record StartupChoice(StartupChoiceKind Kind, string? PresetId = null, string? FilePath = null)
{
    public static StartupChoice Empty { get; } = new(StartupChoiceKind.Empty);

    public static StartupChoice Preset(string presetId) => new(StartupChoiceKind.Preset, PresetId: presetId);

    public static StartupChoice Open(string path) => new(StartupChoiceKind.OpenFile, FilePath: path);

    /// <summary>Волна 6: восстановить последнюю рабочую сессию из автосохранения.</summary>
    public static StartupChoice Restore { get; } = new(StartupChoiceKind.RestoreSession);
}

public enum StartupChoiceKind
{
    Empty,
    Preset,
    OpenFile,
    RestoreSession
}
