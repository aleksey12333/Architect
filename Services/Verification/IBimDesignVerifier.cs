using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;

namespace AIArchitectStudio.Services.Verification;

/// <summary>
/// Плагин инженерной верификации: получает неизменяемый снимок сцены (клоны элементов) и возвращает структурированные
/// замечания с вариантами исправления. Плагины без состояния; любой класс сборки с конструктором без параметров,
/// реализующий интерфейс, подключается конвейером автоматически (<see cref="BimVerificationPipeline.CreateDefault"/>),
/// внешние — через <see cref="BimVerificationPipeline.Register"/>.
/// </summary>
public interface IBimDesignVerifier
{
    /// <summary>Короткий код плагина (префикс кодов замечаний), например «SPAN».</summary>
    string Id { get; }

    /// <summary>Название для отчёта и ленты Совета.</summary>
    string Title { get; }

    /// <summary>Порядок запуска: меньше — раньше (замечания коллизий и проёмов идут после расчётных).</summary>
    int Order { get; }

    IEnumerable<VerificationFinding> Verify(VerificationContext context);
}

/// <summary>
/// Снимок сцены для проверщиков: клоны элементов, постройки, оси, параметры площадки и регион. Экспресс-расчёт
/// и поиск коллизий выполняются один раз на прогон конвейера (ленивые значения разделяются всеми плагинами).
/// </summary>
public sealed class VerificationContext
{
    private readonly Lazy<StructuralAnalysisResult> _analysis;
    private readonly Lazy<List<ClashItem>> _clashes;
    private readonly Dictionary<string, ArchitecturalElement> _byId;

    public VerificationContext(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups,
        IReadOnlyList<StructuralGridAxis> axes, StructuralLoadSettings loads, SolarLocation? location = null, bool includeInsolation = false)
    {
        Elements = elements;
        Groups = groups;
        Axes = axes;
        Loads = loads;
        Location = location;
        IncludeInsolation = includeInsolation;
        _byId = new Dictionary<string, ArchitecturalElement>(StringComparer.Ordinal);
        foreach (var e in elements) _byId.TryAdd(e.Id, e);
        _analysis = new Lazy<StructuralAnalysisResult>(() => StructuralAnalysisService.Analyze(Elements, Groups, Loads));
        _clashes = new Lazy<List<ClashItem>>(() => ClashDetectionService.Detect(Elements));
    }

    /// <summary>Снимок живой сцены: клоны элементов, построек и осей (проверщики не меняют сцену).</summary>
    public static VerificationContext FromScene(SceneStateService scene, StructuralLoadSettings loads, SolarLocation? location = null, bool includeInsolation = false) =>
        new(scene.Elements.Select(e => e.Clone()).ToList(), scene.BuildingGroups.Select(g => g.Clone()).ToList(),
            scene.GridAxes.Select(a => a.Clone()).ToList(), loads.Clone(), location, includeInsolation);

    public IReadOnlyList<ArchitecturalElement> Elements { get; }
    public IReadOnlyList<BuildingGroup> Groups { get; }
    public IReadOnlyList<StructuralGridAxis> Axes { get; }
    public StructuralLoadSettings Loads { get; }
    public SolarLocation? Location { get; }

    /// <summary>Проверять инсоляцию окон (секунды на большой сцене — включается аудитом).</summary>
    public bool IncludeInsolation { get; }

    public StructuralAnalysisResult Analysis => _analysis.Value;
    public List<ClashItem> Clashes => _clashes.Value;

    public ArchitecturalElement? Find(string? id) => id is not null && _byId.TryGetValue(id, out var e) ? e : null;

    public string Name(string? id) => Find(id)?.DisplayTitle ?? id ?? "—";
}

/// <summary>Удобные конструкторы замечаний и вариантов для плагинов.</summary>
public static class VerificationText
{
    public static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

    public static string Mm(double meters) => (meters * 1000).ToString("0", Ru);

    public static string M(double meters) => meters.ToString("0.00", Ru);

    public static string K(double k) => k.ToString("0.00", Ru);

    public static ProposedFixOption Keep() => new()
    {
        Id = "keep",
        Author = FixAuthor.Keep,
        Title = "Оставить как есть",
        Description = "Замечание принимается под ответственность ГИПа, модель не меняется",
        Priority = 1000,
        PreservesArchitecture = true
    };

    /// <summary>Заголовок автора варианта для карточки согласования.</summary>
    public static string AuthorTitle(FixAuthor author) => author switch
    {
        FixAuthor.Structural => "Конструктор",
        FixAuthor.Architect => "Архитектор",
        FixAuthor.Automatic => "КБ",
        _ => "ГИП"
    };

    public static AiAgentRole RoleOf(FixAuthor author) => author switch
    {
        FixAuthor.Structural => AiAgentRole.StructuralEngineer,
        FixAuthor.Architect => AiAgentRole.ChiefArchitect,
        _ => AiAgentRole.MasterSynthesizer
    };
}
