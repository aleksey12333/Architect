using System.Globalization;
using AIArchitectStudio.Models.Gpzu;

namespace AIArchitectStudio.Models.Layouts;

/// <summary>Назначение помещения поэтажной планировки (квартиры, МОП, ядро, технические помещения подвала).</summary>
public enum LayoutRoomKind
{
    Living,
    Bedroom,
    Kitchen,
    KitchenGas,
    Bathroom,
    Toilet,
    Hall,
    Corridor,
    LiftShaft,
    Stair,
    Vestibule,
    LiftLobby,
    Itp,
    WaterMeter,
    Electrical,
    Ventilation,
    Storage
}

/// <summary>Форма лестницы ядра: прямая, Г-, П-образная, трёхмаршевая, криволинейная, ножничная (две незадымляемые в одной клетке).</summary>
public enum CoreStairForm { Straight, LShaped, UShaped, ThreeFlight, Curved, Scissor }

/// <summary>Тип квартиры по числу жилых комнат (евро — кухня-гостиная + спальни).</summary>
public enum ApartmentType { Studio, OneRoom, TwoRoom, ThreeRoom, FourRoom, Euro2, Euro3 }

/// <summary>Инженерный стояк: В1 (ХВС), Т3 (ГВС), К1 (бытовая канализация), Т1 (отопление), ЭС (электроснабжение), Г (газ).</summary>
public enum RiserKind { ColdWater, HotWater, Sewer, Heating, Power, Gas }

/// <summary>Прямоугольное помещение в системе плана здания (м; X — вдоль корпуса, Z — поперёк, север — −Z).</summary>
public sealed record LayoutRoom(string Id, LayoutRoomKind Kind, string Name, double X0, double Z0, double X1, double Z1, string? ApartmentId = null)
{
    public double Width => X1 - X0;
    public double Depth => Z1 - Z0;
    public double Area => Math.Max(0, Width) * Math.Max(0, Depth);
    public double CenterX => (X0 + X1) / 2;
    public double CenterZ => (Z0 + Z1) / 2;

    /// <summary>Площадь пересечения в плане (м²).</summary>
    public double OverlapArea(LayoutRoom o) =>
        Math.Max(0, Math.Min(X1, o.X1) - Math.Max(X0, o.X0)) * Math.Max(0, Math.Min(Z1, o.Z1) - Math.Max(Z0, o.Z0));

    public bool Contains(double x, double z, double tol = 1e-6) => x >= X0 - tol && x <= X1 + tol && z >= Z0 - tol && z <= Z1 + tol;
}

/// <summary>
/// Квартира пакета: слот по фасаду (X0…X1 — вдоль корпуса, сторона «север»/«юг» от коридора), тип, сторона мокрого блока
/// (санузел + кухня у одной межквартирной стены: false — у X0, true — у X1), площади ТЭП.
/// </summary>
public sealed record Apartment(string Id, string Mark, ApartmentType Type, bool North, double X0, double Z0, double X1, double Z1, bool WetAtHighX,
    double TotalArea, double LivingArea, int Rooms);

/// <summary>Стояк в плане (м) — сквозной для всех этажей пакета.</summary>
public sealed record RiserStack(string Id, RiserKind Kind, double X, double Z, string? ApartmentId = null);

/// <summary>Узел ввода наружной сети в подвал (или на 1-й этаж для газа): вид сети, точка на контуре, помещение приёма.</summary>
public sealed record UtilityInlet(string Id, GpzuZoneKind Kind, string Title, double X, double Z, string TargetRoomId, bool IntoBasement);

/// <summary>Магистральный трубопровод / кабель подвала: путь (м) от узла ввода до каждого стояка.</summary>
public sealed record MainLine(string Id, RiserKind Kind, IReadOnlyList<(double X, double Z)> Path, string? RiserId = null);

/// <summary>Ядро: лестнично-лифтовой узел (габарит в плане), лифты, лестницы, форма, категория лестницы, тамбур / лифтовой холл.</summary>
public sealed record CoreLayout(double X0, double Z0, double X1, double Z1, int Lifts, int FireLifts, int Stairs, CoreStairForm StairForm, string StairCategory);

/// <summary>
/// Пакет этажей: диапазон этажей с одной планировкой (или подвал), помещения, квартиры и стояки. Изменение планировки пакета
/// относится сразу ко всем его этажам (один шаг журнала <c>BuildingLayoutPlan</c>).
/// </summary>
public sealed record FloorPackagePlan(string Id, string Title, int FromLevel, int ToLevel, double Elevation, bool IsBasement,
    IReadOnlyList<LayoutRoom> Rooms, IReadOnlyList<Apartment> Apartments, IReadOnlyList<RiserStack> Risers, string LayoutCode)
{
    public int Floors => IsBasement ? 1 : ToLevel - FromLevel + 1;

    public string RangeText => IsBasement ? "Подвал" : FromLevel == ToLevel ? $"Этаж {FromLevel}" : $"Этажи {FromLevel}–{ToLevel}";

    public LayoutRoom? Room(string id) => Rooms.FirstOrDefault(r => r.Id == id);
}

/// <summary>Параметры нарезки квартир застройщика (доли типов и ширины модулей вдоль фасада, м).</summary>
public sealed record ApartmentMix
{
    public double Studio { get; init; } = 0.20;
    public double OneRoom { get; init; } = 0.30;
    public double TwoRoom { get; init; } = 0.30;
    public double ThreeRoom { get; init; } = 0.15;
    public double Euro { get; init; } = 0.05;
    /// <summary>Глубина корпуса (поперёк, м) и ширина коридора МОП (м).</summary>
    public double CorridorWidth { get; init; } = 1.8;
    public static ApartmentMix Default { get; } = new();
}

/// <summary>Технико-экономические показатели пакета / здания.</summary>
public sealed record LayoutTep(int Apartments, double ApartmentArea, double LivingArea, double CommonArea, double FloorArea)
{
    /// <summary>Планировочный коэффициент K1 = S квартир / S этажа.</summary>
    public double K1 => FloorArea > 0 ? ApartmentArea / FloorArea : 0;
}

/// <summary>
/// Волна 7 — поэтажная планировка здания (Раздел «3. Планировки и ИОС»): контур и сетка осей, ядро, пакеты этажей (подвал,
/// 1–5, 6–10 … 46–50), квартиры с мокрыми зонами, стояки, узлы ввода и магистрали подвала. Неизменяемая: правка — новый план
/// одним шагом журнала (<see cref="Services.SceneStateService.ApplyBuildingLayout"/>), Undo/Redo возвращают прежний объект.
/// </summary>
public sealed class BuildingLayoutPlan
{
    public string BuildingGroupId { get; init; } = string.Empty;
    public string BuildingName { get; init; } = string.Empty;
    public int Levels { get; init; }
    public double LevelHeight { get; init; } = 3.0;
    public double BasementDepth { get; init; } = 3.6;

    /// <summary>Контур корпуса в системе плана (м) и его привязка к сцене: центр плана — (OriginX, OriginZ).</summary>
    public double X0 { get; init; }
    public double Z0 { get; init; }
    public double X1 { get; init; }
    public double Z1 { get; init; }
    public double OriginX { get; init; }
    public double OriginZ { get; init; }

    /// <summary>Координационные оси: цифровые — по X, буквенные — по Z.</summary>
    public IReadOnlyList<(string Label, double X)> AxesX { get; init; } = [];
    public IReadOnlyList<(string Label, double Z)> AxesZ { get; init; } = [];

    public CoreLayout Core { get; init; } = new(0, 0, 0, 0, 0, 0, 0, CoreStairForm.UShaped, "Н1");
    public IReadOnlyList<FloorPackagePlan> Packages { get; init; } = [];
    public IReadOnlyList<UtilityInlet> Inlets { get; init; } = [];
    public IReadOnlyList<MainLine> Mains { get; init; } = [];
    public ApartmentMix Mix { get; init; } = ApartmentMix.Default;

    /// <summary>Газоснабжение квартир запроектировано (кухни с газовыми плитами).</summary>
    public bool GasSupply { get; init; }

    public FloorPackagePlan? Basement => Packages.FirstOrDefault(p => p.IsBasement);
    public IEnumerable<FloorPackagePlan> Floors => Packages.Where(p => !p.IsBasement).OrderBy(p => p.FromLevel);
    public FloorPackagePlan? Package(string id) => Packages.FirstOrDefault(p => p.Id == id);
    public FloorPackagePlan? PackageOfLevel(int level) => Packages.FirstOrDefault(p => !p.IsBasement && level >= p.FromLevel && level <= p.ToLevel);

    public double Length => X1 - X0;
    public double Width => Z1 - Z0;
    public double FootprintArea => Length * Width;
    public double Height => Levels * LevelHeight;

    public BuildingLayoutPlan With(IReadOnlyList<FloorPackagePlan>? packages = null, IReadOnlyList<MainLine>? mains = null, IReadOnlyList<UtilityInlet>? inlets = null,
        CoreLayout? core = null, bool? gas = null, ApartmentMix? mix = null) => new()
    {
        BuildingGroupId = BuildingGroupId,
        BuildingName = BuildingName,
        Levels = Levels,
        LevelHeight = LevelHeight,
        BasementDepth = BasementDepth,
        X0 = X0, Z0 = Z0, X1 = X1, Z1 = Z1,
        OriginX = OriginX, OriginZ = OriginZ,
        AxesX = AxesX,
        AxesZ = AxesZ,
        Core = core ?? Core,
        Packages = packages ?? Packages,
        Inlets = inlets ?? Inlets,
        Mains = mains ?? Mains,
        Mix = mix ?? Mix,
        GasSupply = gas ?? GasSupply
    };

    /// <summary>ТЭП пакета на один этаж.</summary>
    public static LayoutTep TepOf(FloorPackagePlan p)
    {
        double apartments = p.Apartments.Sum(a => a.TotalArea);
        double living = p.Apartments.Sum(a => a.LivingArea);
        double floor = p.Rooms.Sum(r => r.Area);
        double common = p.Rooms.Where(r => r.ApartmentId is null).Sum(r => r.Area);
        return new LayoutTep(p.Apartments.Count, apartments, living, common, floor);
    }

    /// <summary>ТЭП здания: суммы по этажам всех пакетов (подвал — только МОП и технические помещения).</summary>
    public LayoutTep Tep()
    {
        int n = 0;
        double a = 0, l = 0, c = 0, f = 0;
        foreach (var p in Packages)
        {
            var t = TepOf(p);
            n += t.Apartments * p.Floors;
            a += t.ApartmentArea * p.Floors;
            l += t.LivingArea * p.Floors;
            c += t.CommonArea * p.Floors;
            f += t.FloorArea * p.Floors;
        }
        return new LayoutTep(n, a, l, c, f);
    }

    public static string Format(double v, string fmt = "N1") => v.ToString(fmt, CultureInfo.GetCultureInfo("ru-RU"));
}

/// <summary>Справочник помещений: наименования, мокрые и газовые зоны, цвета плана.</summary>
public static class LayoutRooms
{
    public static bool IsWet(LayoutRoomKind k) => k is LayoutRoomKind.Bathroom or LayoutRoomKind.Toilet or LayoutRoomKind.Kitchen or LayoutRoomKind.KitchenGas;

    /// <summary>Санузел или ванная (уборная / душевая) — не размещаются над жилыми комнатами и кухнями.</summary>
    public static bool IsSanitary(LayoutRoomKind k) => k is LayoutRoomKind.Bathroom or LayoutRoomKind.Toilet;

    public static bool IsKitchen(LayoutRoomKind k) => k is LayoutRoomKind.Kitchen or LayoutRoomKind.KitchenGas;

    public static bool IsGas(LayoutRoomKind k) => k == LayoutRoomKind.KitchenGas;

    public static bool IsDwelling(LayoutRoomKind k) => k is LayoutRoomKind.Living or LayoutRoomKind.Bedroom;

    public static bool IsCore(LayoutRoomKind k) => k is LayoutRoomKind.LiftShaft or LayoutRoomKind.Stair or LayoutRoomKind.Vestibule or LayoutRoomKind.LiftLobby;

    public static string Title(LayoutRoomKind k) => k switch
    {
        LayoutRoomKind.Living => "Жилая комната",
        LayoutRoomKind.Bedroom => "Спальня",
        LayoutRoomKind.Kitchen => "Кухня",
        LayoutRoomKind.KitchenGas => "Кухня (газ)",
        LayoutRoomKind.Bathroom => "Санузел",
        LayoutRoomKind.Toilet => "Уборная",
        LayoutRoomKind.Hall => "Прихожая",
        LayoutRoomKind.Corridor => "Коридор МОП",
        LayoutRoomKind.LiftShaft => "Лифтовая шахта",
        LayoutRoomKind.Stair => "Лестничная клетка",
        LayoutRoomKind.Vestibule => "Тамбур-шлюз",
        LayoutRoomKind.LiftLobby => "Лифтовой холл",
        LayoutRoomKind.Itp => "ИТП",
        LayoutRoomKind.WaterMeter => "Водомерный узел и насосная",
        LayoutRoomKind.Electrical => "Электрощитовая (ВРУ)",
        LayoutRoomKind.Ventilation => "Венткамера",
        _ => "Техподполье"
    };

    public static string ColorHex(LayoutRoomKind k) => k switch
    {
        LayoutRoomKind.Living or LayoutRoomKind.Bedroom => "#F4C78B",
        LayoutRoomKind.Kitchen => "#7DD3C0",
        LayoutRoomKind.KitchenGas => "#F59E0B",
        LayoutRoomKind.Bathroom or LayoutRoomKind.Toilet => "#38BDF8",
        LayoutRoomKind.Hall => "#E7D9C6",
        LayoutRoomKind.Corridor => "#CBD5E1",
        LayoutRoomKind.LiftShaft => "#64748B",
        LayoutRoomKind.Stair => "#94A3B8",
        LayoutRoomKind.Vestibule or LayoutRoomKind.LiftLobby => "#A5B4FC",
        LayoutRoomKind.Itp => "#EF4444",
        LayoutRoomKind.WaterMeter => "#06B6D4",
        LayoutRoomKind.Electrical => "#F59E0B",
        LayoutRoomKind.Ventilation => "#A855F7",
        _ => "#D6D3D1"
    };

    public static string RiserTitle(RiserKind k) => k switch
    {
        RiserKind.ColdWater => "В1 (ХВС)",
        RiserKind.HotWater => "Т3 (ГВС)",
        RiserKind.Sewer => "К1",
        RiserKind.Heating => "Т1 (отопление)",
        RiserKind.Power => "ЭС (ВРУ)",
        _ => "Г (газ)"
    };

    public static string RiserColor(RiserKind k) => k switch
    {
        RiserKind.ColdWater => "#0EA5E9",
        RiserKind.HotWater => "#EF4444",
        RiserKind.Sewer => "#78716C",
        RiserKind.Heating => "#F97316",
        RiserKind.Power => "#EAB308",
        _ => "#D946EF"
    };

    public static string ApartmentTitle(ApartmentType t) => t switch
    {
        ApartmentType.Studio => "Студия",
        ApartmentType.OneRoom => "1-комн.",
        ApartmentType.TwoRoom => "2-комн.",
        ApartmentType.ThreeRoom => "3-комн.",
        ApartmentType.FourRoom => "4-комн.",
        ApartmentType.Euro2 => "Евро-2",
        _ => "Евро-3"
    };

    public static string StairTitle(CoreStairForm f) => f switch
    {
        CoreStairForm.Straight => "прямая",
        CoreStairForm.LShaped => "Г-образная",
        CoreStairForm.UShaped => "П-образная",
        CoreStairForm.ThreeFlight => "трёхмаршевая",
        CoreStairForm.Curved => "криволинейная",
        _ => "ножничная"
    };
}
