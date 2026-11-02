using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models;

/// <summary>
/// Класс бетона по прочности на сжатие (СП 63.13330). Новые классы дописываются в конец: settings.json хранит класс числом,
/// а порядок B20–B40 должен остаться прежним.
/// </summary>
public enum ConcreteClass
{
    B20,
    B25,
    B30,
    B40,
    B15,
    B35,
    B45,
    B50,
    B55,
    B60
}

/// <summary>Класс рабочей арматуры (ГОСТ 34028-2016 / ГОСТ 5781-82).</summary>
public enum RebarClass
{
    A240,
    A400,
    A500C
}

/// <summary>Расчётные характеристики бетона и арматуры (СП 63.13330.2018, табл. 6.8, 6.11, 6.12, 6.14).</summary>
public static class StructuralNorms
{
    /// <summary>Rb, Rbt, Eb — МПа; φb,cr — коэффициент ползучести при влажности 40–75 %.</summary>
    public static (double Rb, double Rbt, double Eb, double CreepPhi) Concrete(ConcreteClass c) => c switch
    {
        ConcreteClass.B15 => (8.5, 0.75, 24000, 3.4),
        ConcreteClass.B20 => (11.5, 0.90, 27500, 2.8),
        ConcreteClass.B30 => (17.0, 1.15, 32500, 2.3),
        ConcreteClass.B35 => (19.5, 1.30, 34500, 2.1),
        ConcreteClass.B40 => (22.0, 1.40, 36000, 1.9),
        ConcreteClass.B45 => (25.0, 1.50, 37000, 1.8),
        ConcreteClass.B50 => (27.5, 1.60, 38000, 1.6),
        ConcreteClass.B55 => (30.0, 1.70, 39000, 1.5),
        ConcreteClass.B60 => (33.0, 1.80, 39500, 1.4),
        _ => (14.5, 1.05, 30000, 2.5)
    };

    /// <summary>Классы бетона по возрастанию прочности (списки интерфейса и библиотеки материалов).</summary>
    public static readonly IReadOnlyList<ConcreteClass> ConcreteClasses =
    [
        ConcreteClass.B15, ConcreteClass.B20, ConcreteClass.B25, ConcreteClass.B30, ConcreteClass.B35,
        ConcreteClass.B40, ConcreteClass.B45, ConcreteClass.B50, ConcreteClass.B55, ConcreteClass.B60
    ];

    /// <summary>Сталь С345 (ГОСТ 27772, СП 16.13330, табл. В.5, t 2–20 мм): Ry, МПа.</summary>
    public const double SteelC345Ry = 340;

    /// <summary>Rs, Rsc, Rsw — МПа.</summary>
    public static (double Rs, double Rsc, double Rsw) Rebar(RebarClass c) => c switch
    {
        RebarClass.A240 => (210, 210, 170),
        RebarClass.A500C => (435, 400, 300),
        _ => (350, 350, 280)
    };

    public static string Title(ConcreteClass c) => c.ToString();

    public static string Title(RebarClass c) => c switch
    {
        RebarClass.A240 => "А240",
        RebarClass.A500C => "А500С",
        _ => "А400"
    };

    /// <summary>Модуль упругости арматуры и проката, МПа.</summary>
    public const double Es = 200000;

    /// <summary>Сталь С245 (ГОСТ 27772): Ry, МПа; E — 206 000 МПа.</summary>
    public const double SteelRy = 240, SteelE = 206000;

    /// <summary>Коэффициенты надёжности по нагрузке (СП 20.13330): ж/б и сталь, изоляция и стяжки, полезная, ветер.</summary>
    public const double GammaDead = 1.1, GammaFinish = 1.3, GammaLive = 1.2, GammaWind = 1.4;
}

/// <summary>
/// Параметры площадки строительства для экспресс-расчёта: снег (СП 20.13330, расчётное значение веса снегового покрова
/// по районам I–VIII), ветер (нормативное давление w0, районы Iа–VII), сейсмичность (СП 14.13330), классы бетона и арматуры.
/// </summary>
public sealed class StructuralLoadSettings
{
    /// <summary>Расчётный вес снегового покрова S0, кг/м² (80…600).</summary>
    public double SnowKgM2 { get; set; } = 180;

    /// <summary>Нормативное ветровое давление w0, кг/м² (23…85).</summary>
    public double WindKgM2 { get; set; } = 23;

    /// <summary>Сейсмичность площадки, баллы MSK-64: 6, 7, 8, 9.</summary>
    public int Seismicity { get; set; } = 6;

    public ConcreteClass Concrete { get; set; } = ConcreteClass.B25;
    public RebarClass Rebar { get; set; } = RebarClass.A400;

    /// <summary>Нормативная полезная нагрузка на перекрытия, кПа.</summary>
    public double LiveLoadKPa { get; set; } = 2.0;

    /// <summary>Расчётное сопротивление грунта основания R, кПа.</summary>
    public double SoilBearingKPa { get; set; } = 300;

    /// <summary>Климатические районы РФ по снегу: подпись и S0, кг/м².</summary>
    public static readonly IReadOnlyList<(string Title, double Value)> SnowRegions =
    [
        ("I", 80), ("II", 120), ("III", 180), ("IV", 240), ("V", 320), ("VI", 400), ("VII", 480), ("VIII", 560)
    ];

    /// <summary>Ветровые районы РФ: подпись и w0, кг/м² (СП 20.13330, табл. 11.1).</summary>
    public static readonly IReadOnlyList<(string Title, double Value)> WindRegions =
    [
        ("Iа", 17), ("I", 23), ("II", 30), ("III", 38), ("IV", 48), ("V", 60), ("VI", 73), ("VII", 85)
    ];

    /// <summary>
    /// Класс сооружения по последствиям отказа (ГОСТ 27751-2014, табл. 2): 1 — КС-1, 2 — КС-2, 3 — КС-3; 0 — не задан (как КС-2).
    /// Значение по умолчанию в файл не пишется: .aia и settings.json без него совпадают с прежними до байта.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ConsequenceClass { get; set; }

    /// <summary>Коэффициент надёжности по ответственности γn (ГОСТ 27751-2014, п. 10.1): КС-1 — 0.8, КС-2 — 1.0, КС-3 — 1.1.</summary>
    [JsonIgnore]
    public double GammaN => ConsequenceClass switch
    {
        1 => 0.8,
        3 => 1.1,
        _ => 1.0
    };

    /// <summary>
    /// Характеристики материалов проекта, отличающиеся от нормативных (сертификаты поставщиков, ТУ): только изменённые марки.
    /// null — все марки по таблицам СП (блок в файлы не пишется).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MaterialGrade>? Materials { get; set; }

    /// <summary>Rb, Rbt, Eb, φb,cr бетона расчёта: класс проекта с учётом переопределения характеристик.</summary>
    public (double Rb, double Rbt, double Eb, double CreepPhi) ConcreteProps() =>
        MaterialLibrary.Find(Materials, StructuralNorms.Title(Concrete)) is { } g
            ? (g.Strength, g.Tensile, g.ElasticModulus, StructuralNorms.Concrete(Concrete).CreepPhi)
            : StructuralNorms.Concrete(Concrete);

    /// <summary>Rs, Rsc, Rsw рабочей арматуры расчёта с учётом переопределения характеристик.</summary>
    public (double Rs, double Rsc, double Rsw) RebarProps() =>
        MaterialLibrary.Find(Materials, StructuralNorms.Title(Rebar)) is { } g
            ? (g.Strength, g.Tensile, StructuralNorms.Rebar(Rebar).Rsw)
            : StructuralNorms.Rebar(Rebar);

    /// <summary>Ry и E стали С245 (колонны, балки, фермы и диагрид из стали) с учётом переопределения, МПа; плотность, кг/м³.</summary>
    public (double Ry, double E, double Density) SteelProps() =>
        MaterialLibrary.Find(Materials, MaterialLibrary.SteelC245) is { } g
            ? (g.Strength, g.ElasticModulus, g.Density)
            : (StructuralNorms.SteelRy, StructuralNorms.SteelE, 7850);

    /// <summary>Плотность бетона расчёта, кг/м³ (2500 — тяжёлый бетон с арматурой).</summary>
    public double ConcreteDensity() => MaterialLibrary.Find(Materials, StructuralNorms.Title(Concrete)) is { } g ? g.Density : 2500;

    [JsonIgnore] public double SnowKPa => SnowKgM2 * 9.81 / 1000.0;
    [JsonIgnore] public double WindKPa => WindKgM2 * 9.81 / 1000.0;

    /// <summary>
    /// Сейсмическое ускорение A в долях g (СП 14.13330, п. 5.5: 7 баллов — 0.1, 8 — 0.2, 9 — 0.4).
    /// При 6 баллах сейсмический расчёт не требуется (СП 14.13330, п. 4.1) — A = 0.
    /// </summary>
    [JsonIgnore]
    public double SeismicA => Seismicity switch
    {
        >= 9 => 0.4,
        8 => 0.2,
        7 => 0.1,
        _ => 0
    };

    public StructuralLoadSettings Clone()
    {
        var copy = (StructuralLoadSettings)MemberwiseClone();
        copy.Materials = Materials?.Select(m => m.Clone()).ToList();
        return copy;
    }

    public void Sanitize()
    {
        SnowKgM2 = Clamp(SnowKgM2, 80, 600, 180);
        WindKgM2 = Clamp(WindKgM2, 17, 85, 23);
        if (ConsequenceClass is < 0 or > 3) ConsequenceClass = 0;
        Materials = MaterialLibrary.Normalize(Materials);
        Seismicity = Math.Clamp(Seismicity, 6, 9);
        LiveLoadKPa = Clamp(LiveLoadKPa, 0, 50, 2);
        SoilBearingKPa = Clamp(SoilBearingKPa, 50, 2000, 300);
        if (!Enum.IsDefined(Concrete)) Concrete = ConcreteClass.B25;
        if (!Enum.IsDefined(Rebar)) Rebar = RebarClass.A400;
    }

    private static double Clamp(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}

/// <summary>Состояние узла «колонна — плита» по продавливанию (СП 63.13330, п. 8.1.46–8.1.52).</summary>
public enum PunchingStatus
{
    None,
    Ok,

    /// <summary>F_b,ult &lt; F ≤ 2·F_b,ult — нужна поперечная арматура (каркасы Кр) или капитель.</summary>
    NeedsReinforcement,

    /// <summary>F &gt; 2·F_b,ult — критическое превышение.</summary>
    Critical
}

/// <summary>Расчётный узел продавливания плиты колонной (или ядром) по контуру на расстоянии h0/2 от граней.</summary>
public sealed class PunchingNode
{
    public string ColumnId { get; set; } = string.Empty;
    public string SlabId { get; set; } = string.Empty;

    /// <summary>Продавливающая сила F, кН.</summary>
    public double Force { get; set; }

    /// <summary>F_b,ult = Rbt·u·h0, кН.</summary>
    public double Capacity { get; set; }

    /// <summary>Периметр расчётного контура u, м (с учётом края плиты и отверстий).</summary>
    public double Perimeter { get; set; }

    public double H0 { get; set; }
    public double Ratio => Capacity > 0 ? Force / Capacity : 0;
    public PunchingStatus Status { get; set; }

    /// <summary>Точка узла (центр колонны на отметке плиты), м.</summary>
    public double[] Location { get; set; } = [0, 0, 0];

    /// <summary>Расчётный контур в плане (мировые X, Z) на отметке узла.</summary>
    public List<(double X, double Z)> Contour { get; set; } = [];

    public string Recommendation => Status switch
    {
        PunchingStatus.Critical => "Критическое превышение на продавливание: увеличьте толщину плиты, класс бетона или сечение колонны, устройте капитель",
        PunchingStatus.NeedsReinforcement => "Требуется поперечное армирование зоны продавливания (каркасы Кр) или устройство капители",
        _ => "Прочность на продавливание обеспечена"
    };
}

/// <summary>Результат экспресс-расчёта элемента (наихудший этаж для параметрических элементов).</summary>
public sealed class ElementStructuralResult
{
    public string ElementId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    /// <summary>Продольная сжимающая сила N, кН.</summary>
    public double AxialForce { get; set; }

    /// <summary>Изгибающий момент M, кН·м (для плит — на 1 м ширины).</summary>
    public double Moment { get; set; }

    /// <summary>Поперечная сила Q, кН (для плит — на 1 м ширины).</summary>
    public double Shear { get; set; }

    /// <summary>Расчётный пролёт (или вылет консоли), м.</summary>
    public double Span { get; set; }

    public bool IsCantilever { get; set; }

    /// <summary>Прогиб f и предельный прогиб f_ult, мм.</summary>
    public double DeflectionMm { get; set; }
    public double DeflectionLimitMm { get; set; }

    /// <summary>Гибкость λ = l0 / i и коэффициент продольного изгиба φ.</summary>
    public double Slenderness { get; set; }
    public double Phi { get; set; } = 1;

    /// <summary>Погонная (кН/м) или распределённая (кПа) расчётная нагрузка.</summary>
    public double DesignLoad { get; set; }

    public double KStrength { get; set; }
    public double KStability { get; set; }
    public double KShear { get; set; }
    public double KDeflection { get; set; }
    public double KPunching { get; set; }

    /// <summary>Давление под подошвой / R грунта (фундаменты).</summary>
    public double KSoil { get; set; }

    public PunchingStatus Punching { get; set; }
    public bool PunchingShearCritical => Punching == PunchingStatus.Critical;

    /// <summary>Коэффициент использования K_исп = max(Demand / Capacity) по всем проверкам.</summary>
    public double Utilization => Math.Max(Math.Max(Math.Max(KStrength, KStability), Math.Max(KShear, KDeflection)), Math.Max(KPunching, KSoil));

    public double DeflectionRatio => DeflectionLimitMm > 0 ? DeflectionMm / DeflectionLimitMm : 0;

    /// <summary>Определяющая проверка: «прочность», «устойчивость», «срез», «прогиб», «продавливание», «грунт».</summary>
    public string Governing
    {
        get
        {
            var checks = new (double K, string Name)[]
            {
                (KStrength, "прочность"), (KStability, "устойчивость"), (KShear, "поперечная сила"),
                (KDeflection, "прогиб"), (KPunching, "продавливание"), (KSoil, "давление на грунт")
            };
            return checks.MaxBy(c => c.K).Name;
        }
    }

    /// <summary>Номер этажа параметрического элемента с наихудшим результатом (0 — базовый).</summary>
    public int Floor { get; set; }

    public List<string> Notes { get; set; } = [];
}

/// <summary>Результат экспресс-расчёта сцены.</summary>
public sealed class StructuralAnalysisResult
{
    public static readonly StructuralAnalysisResult Empty = new();

    public Dictionary<string, ElementStructuralResult> Elements { get; } = [];
    public List<PunchingNode> PunchingNodes { get; } = [];
    public double ElapsedMilliseconds { get; set; }

    /// <summary>Время по фазам расчёта (диагностика производительности).</summary>
    public string Profile { get; set; } = string.Empty;
    public StructuralLoadSettings Settings { get; set; } = new();

    /// <summary>Сдвигающая сила в основании по постройкам, кН: ветер и сейсмика.</summary>
    public Dictionary<string, (double Wind, double Seismic)> BaseShear { get; } = [];

    public double MaxUtilization => Elements.Count == 0 ? 0 : Elements.Values.Max(r => r.Utilization);
    public double MaxDeflectionMm => Elements.Count == 0 ? 0 : Elements.Values.Max(r => r.DeflectionMm);
    public int OverloadedCount => Elements.Values.Count(r => r.Utilization > 0.95);
    public int PunchingCritical => PunchingNodes.Count(n => n.Status == PunchingStatus.Critical);
    public int PunchingReinforce => PunchingNodes.Count(n => n.Status == PunchingStatus.NeedsReinforcement);

    public ElementStructuralResult? For(string? id) => id is not null && Elements.TryGetValue(id, out var r) ? r : null;

    /// <summary>K_исп по Id элемента (для расчёта армирования).</summary>
    public IReadOnlyDictionary<string, double> UtilizationById => Elements.ToDictionary(p => p.Key, p => p.Value.Utilization);
}
