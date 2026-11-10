using System.Globalization;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Models.Layouts;

namespace AIArchitectStudio.Services.Layouts;

/// <summary>Исходные данные планировки: контур корпуса, этажность, подвал, пакеты, нарезка квартир, газ и вводы сетей участка.</summary>
public sealed record LayoutRequest
{
    public string GroupId { get; init; } = string.Empty;
    public string Name { get; init; } = "Жилой корпус";
    public int Levels { get; init; } = 9;
    public double LevelHeight { get; init; } = 3.0;
    /// <summary>Длина корпуса вдоль X и ширина поперёк (м).</summary>
    public double Length { get; init; } = 42;
    public double Width { get; init; } = 18;
    public double OriginX { get; init; }
    public double OriginZ { get; init; }
    public bool Basement { get; init; } = true;
    public double BasementDepth { get; init; } = 3.6;
    public int PackageSize { get; init; } = 5;
    public ApartmentMix Mix { get; init; } = ApartmentMix.Default;
    /// <summary>Форма лестниц ядра (null — по высоте здания).</summary>
    public CoreStairForm? StairForm { get; init; }
    /// <summary>У участка есть газопровод (газ в кухнях допустим только до 10 этажей включительно).</summary>
    public bool GasAvailable { get; init; }
    /// <summary>Точки вводов сетей участка в системе плана (м); пусто — по умолчанию на фасадах.</summary>
    public IReadOnlyList<(GpzuZoneKind Kind, double X, double Z)> SiteEntries { get; init; } = [];
    /// <summary>Оси по колоннам здания в системе плана (пусто — сетка 6 м).</summary>
    public IReadOnlyList<double> AxisX { get; init; } = [];
    public IReadOnlyList<double> AxisZ { get; init; } = [];
}

/// <summary>
/// Волна 7 — генератор поэтажных планировок девелопера: ядро в центре корпуса (лифтовые шахты от приямка подвала до верхнего этажа,
/// число лифтов по этажности, лестничные клетки заданной формы, лифтовой холл и тамбур-шлюз с противопожарными преградами),
/// коридор МОП вдоль корпуса, квартиры по обе стороны (студии, 1–4 комн., евро) с прихожей, санузлом, кухней и комнатами; мокрые
/// блоки соседних квартир — у общей межквартирной стены (общий стояк В1/Т3/К1). Подвал: ИТП, водомерный узел и насосная,
/// электрощитовая (ВРУ — не под мокрыми зонами 1-го этажа, ПУЭ п. 7.1.29), венткамера, техподполье, узлы ввода наружных сетей
/// и магистрали до каждого стояка. Генератор детерминирован: одинаковый запрос — одинаковый план.
/// </summary>
public static class DeveloperLayoutGenerator
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Глубина полосы прихожей и санузла у коридора (м), ширина санузла и кухни.</summary>
    public const double WetStrip = 2.6;
    public const double BathWidth = 2.2;
    public const double ToiletWidth = 1.2;
    public const double KitchenWidth = 3.0;
    public const double ShaftSize = 2.4;

    /// <summary>Лифты по этажности: до 9 эт. — 1, до 19 — 2, до 25 — 3, выше — 4 (из них при высоте > 28 м один — для пожарных подразделений).</summary>
    public static (int Lifts, int Fire) LiftsFor(int levels, double levelHeight) =>
        (levels <= 9 ? 1 : levels <= 19 ? 2 : levels <= 25 ? 3 : 4, levels * levelHeight > 28 ? 1 : 0);

    /// <summary>Лестницы по высоте: до 28 м — одна Н1, до 75 м — одна Н2 с тамбур-шлюзом, выше — две (Н2 + Н3) или ножничная.</summary>
    public static (int Count, string Category) StairsFor(int levels, double levelHeight)
    {
        double h = levels * levelHeight;
        return h <= 28 ? (1, "Н1") : h <= 75 ? (1, "Н2") : (2, "Н2 + Н3");
    }

    public static BuildingLayoutPlan Generate(LayoutRequest r)
    {
        double hx = r.Length / 2, hz = r.Width / 2, c = Math.Clamp(r.Mix.CorridorWidth, 1.5, 3.0), hc = c / 2;
        var (lifts, fire) = LiftsFor(r.Levels, r.LevelHeight);
        var (stairs, category) = StairsFor(r.Levels, r.LevelHeight);
        var form = r.StairForm ?? (stairs > 1 ? CoreStairForm.UShaped : CoreStairForm.UShaped);
        if (form == CoreStairForm.Scissor) stairs = Math.Max(stairs, 2);
        double stairCell = form switch { CoreStairForm.Straight => 2.6, CoreStairForm.Scissor => 3.6, CoreStairForm.ThreeFlight => 4.2, CoreStairForm.Curved => 4.2, _ => 3.0 };
        int stairCells = form == CoreStairForm.Scissor ? 1 : stairs;
        double coreW = Math.Max(lifts * ShaftSize + 0.6, stairCells * stairCell + 2.4);
        coreW = Math.Min(Math.Ceiling(coreW / 0.3) * 0.3, r.Length - 8);
        var core = new CoreLayout(-coreW / 2, -hz, coreW / 2, hz, lifts, fire, stairs, form, category);
        bool gas = r.GasAvailable && r.Levels <= 10;

        var axesX = r.AxisX.Count >= 2 ? r.AxisX.OrderBy(v => v).ToList() : Grid(-hx, hx, 6.0);
        var axesZ = r.AxisZ.Count >= 2 ? r.AxisZ.OrderBy(v => v).ToList() : [-hz, -hc, hc, hz];
        var plan = new BuildingLayoutPlan
        {
            BuildingGroupId = r.GroupId,
            BuildingName = r.Name,
            Levels = r.Levels,
            LevelHeight = r.LevelHeight,
            BasementDepth = r.BasementDepth,
            X0 = -hx, Z0 = -hz, X1 = hx, Z1 = hz,
            OriginX = r.OriginX, OriginZ = r.OriginZ,
            AxesX = axesX.Select((x, i) => ((i + 1).ToString(Ru), x)).ToList(),
            AxesZ = axesZ.Select((z, i) => (AxisLetter(i), z)).ToList(),
            Core = core,
            Mix = r.Mix,
            GasSupply = gas
        };

        // Слоты квартир — одинаковые для всех пакетов (мокрые блоки соосны по вертикали).
        var slots = Slots(r.Length, coreW, r.Width, c, r.Mix);
        var packages = new List<FloorPackagePlan>();
        int index = 0;
        foreach (var (from, to) in FloorTierLayoutService.SplitPackages(r.Levels, r.PackageSize))
        {
            string code = index == 0 ? "А" : "Б";
            string id = $"p{from:00}-{to:00}";
            packages.Add(BuildFloorPackage(plan, id, from, to, code, slots, gas));
            index++;
        }
        if (r.Basement && packages.Count > 0)
            packages.Insert(0, BuildBasement(plan, packages[0]));
        plan = plan.With(packages: packages);
        var inlets = BuildInlets(plan, r.SiteEntries, gas);
        return plan.With(inlets: inlets, mains: BuildMains(plan.With(inlets: inlets)));
    }

    private static List<double> Grid(double from, double to, double step)
    {
        int n = Math.Max(1, (int)Math.Round((to - from) / step));
        double s = (to - from) / n;
        return Enumerable.Range(0, n + 1).Select(i => Math.Round(from + i * s, 3)).ToList();
    }

    private static readonly string Letters = "АБВГДЕЖИКЛМНПРСТУФ";

    public static string AxisLetter(int i) => i < Letters.Length ? Letters[i].ToString() : "Я" + (i - Letters.Length + 1).ToString(Ru);

    /// <summary>Слот квартиры: X0…X1, сторона (север / юг), тип, мокрый блок у X1 (true) или у X0.</summary>
    public sealed record Slot(string Key, bool North, double X0, double X1, double Z0, double Z1, ApartmentType Type, bool WetHigh);

    /// <summary>
    /// Нарезка полос по обе стороны ядра на квартиры: типы чередуются по долям смеси (детерминированный взвешенный круговой
    /// порядок), ширины модулей вдоль фасада — студия 3,6 м, 1-комн. 4,8, евро-2 5,4, 2-комн. 6,6, евро-3 7,2, 3-комн. 8,4, 4-комн. 10,2;
    /// остаток полосы отдаётся последней квартире. Мокрые блоки соседних квартир — у общей стены.
    /// </summary>
    public static List<Slot> Slots(double length, double coreW, double width, double corridor, ApartmentMix mix)
    {
        var order = TypeOrder(mix);
        var list = new List<Slot>();
        double hx = length / 2, hz = width / 2, hc = corridor / 2;
        int k = 0;
        foreach (bool north in new[] { true, false })
        {
            double z0 = north ? -hz : hc, z1 = north ? -hc : hz;
            foreach (var (a, b) in new[] { (-hx, -coreW / 2), (coreW / 2, hx) })
            {
                var widths = new List<(ApartmentType Type, double W)>();
                double x = a;
                while (b - x > 0.01)
                {
                    var t = order[k++ % order.Count];
                    double w = ModuleWidth(t);
                    if (b - x - w < 3.6)
                    {
                        // Остаток — к последней квартире (или новая студия, если остаток широкий).
                        if (widths.Count > 0 && b - x < 3.6) widths[^1] = (Grow(widths[^1].Type, widths[^1].W + b - x), widths[^1].W + b - x);
                        else widths.Add((Grow(t, b - x), b - x));
                        break;
                    }
                    widths.Add((t, w));
                    x += w;
                }
                double cursor = a;
                for (int i = 0; i < widths.Count; i++)
                {
                    var (type, w) = widths[i];
                    string key = $"{(north ? "С" : "Ю")}{list.Count(s => s.North == north) + 1}";
                    list.Add(new Slot(key, north, Math.Round(cursor, 3), Math.Round(cursor + w, 3), z0, z1, type, i % 2 == 0));
                    cursor += w;
                }
            }
        }
        return list;
    }

    private static ApartmentType Grow(ApartmentType t, double w) => w switch
    {
        >= 9.6 => ApartmentType.FourRoom,
        >= 7.8 => ApartmentType.ThreeRoom,
        >= 6.3 => ApartmentType.TwoRoom,
        >= 4.2 => t is ApartmentType.Studio ? ApartmentType.OneRoom : t is ApartmentType.Euro2 or ApartmentType.OneRoom ? t : ApartmentType.OneRoom,
        _ => ApartmentType.Studio
    };

    public static double ModuleWidth(ApartmentType t) => t switch
    {
        ApartmentType.Studio => 3.6,
        ApartmentType.OneRoom => 4.8,
        ApartmentType.Euro2 => 5.4,
        ApartmentType.TwoRoom => 6.6,
        ApartmentType.Euro3 => 7.2,
        ApartmentType.ThreeRoom => 8.4,
        _ => 10.2
    };

    private static List<ApartmentType> TypeOrder(ApartmentMix mix)
    {
        var weights = new (ApartmentType T, double W)[]
        {
            (ApartmentType.Studio, mix.Studio), (ApartmentType.OneRoom, mix.OneRoom), (ApartmentType.TwoRoom, mix.TwoRoom),
            (ApartmentType.ThreeRoom, mix.ThreeRoom), (ApartmentType.Euro2, mix.Euro)
        };
        double total = weights.Sum(w => Math.Max(0, w.W));
        if (total <= 0) return [ApartmentType.OneRoom];
        var acc = weights.Select(w => 0.0).ToArray();
        var order = new List<ApartmentType>();
        for (int i = 0; i < 20; i++)
        {
            for (int j = 0; j < weights.Length; j++) acc[j] += Math.Max(0, weights[j].W) / total;
            int best = Array.IndexOf(acc, acc.Max());
            order.Add(weights[best].T);
            acc[best] -= 1;
        }
        return order;
    }

    private static FloorPackagePlan BuildFloorPackage(BuildingLayoutPlan plan, string id, int from, int to, string code, IReadOnlyList<Slot> slots, bool gas)
    {
        var rooms = new List<LayoutRoom>();
        var apartments = new List<Apartment>();
        var risers = new List<RiserStack>();
        AddCoreRooms(plan, id, rooms, basement: false);
        double hc = plan.Mix.CorridorWidth / 2;
        // Коридор МОП — по обе стороны ядра (в ядре — лифтовой холл).
        rooms.Add(new LayoutRoom($"{id}:corW", LayoutRoomKind.Corridor, "Коридор МОП", plan.X0, -hc, plan.Core.X0, hc));
        rooms.Add(new LayoutRoom($"{id}:corE", LayoutRoomKind.Corridor, "Коридор МОП", plan.Core.X1, -hc, plan.X1, hc));
        int n = 0;
        foreach (var s in slots)
        {
            var type = code == "Б" ? Euro(s.Type) : s.Type;
            var apt = new Apartment($"{id}:{s.Key}", $"{s.Key}", type, s.North, s.X0, s.Z0, s.X1, s.Z1, s.WetHigh, 0, 0, RoomsOf(type));
            var (r, st, done) = BuildApartment(apt, gas, plan.Mix.CorridorWidth);
            rooms.AddRange(r);
            risers.AddRange(st);
            apartments.Add(done);
            n++;
        }
        // Стояки отопления Т1 и электроснабжения ЭС — в МОП у ядра.
        risers.Add(new RiserStack($"{id}:T1-W", RiserKind.Heating, plan.Core.X0 - 0.4, 0));
        risers.Add(new RiserStack($"{id}:T1-E", RiserKind.Heating, plan.Core.X1 + 0.4, 0));
        risers.Add(new RiserStack($"{id}:ES", RiserKind.Power, plan.Core.X0 + 0.4, hc - 0.3));
        string title = from == to ? $"Этаж {from}" : $"Этажи {from}–{to}";
        return new FloorPackagePlan(id, title, from, to, (from - 1) * plan.LevelHeight, false, rooms, apartments, risers, code);
    }

    private static ApartmentType Euro(ApartmentType t) => t switch
    {
        ApartmentType.OneRoom => ApartmentType.Euro2,
        ApartmentType.TwoRoom => ApartmentType.Euro3,
        _ => t
    };

    private static int RoomsOf(ApartmentType t) => t switch
    {
        ApartmentType.Studio => 1,
        ApartmentType.OneRoom or ApartmentType.Euro2 => 1,
        ApartmentType.TwoRoom or ApartmentType.Euro3 => 2,
        ApartmentType.ThreeRoom => 3,
        _ => 4
    };

    /// <summary>
    /// Помещения квартиры: у коридора — прихожая и санузел (+ уборная у 2-комн. и больше) со стороны мокрого блока, у фасада — кухня
    /// со стороны мокрого блока и жилые комнаты. Стояки В1/Т3/К1 — во внутреннем углу санузла у коридора, газовый — в кухне.
    /// </summary>
    public static (List<LayoutRoom> Rooms, List<RiserStack> Risers, Apartment Apartment) BuildApartment(Apartment apt, bool gas, double corridor)
    {
        var rooms = new List<LayoutRoom>();
        double a0 = apt.X0, a1 = apt.X1, w = a1 - a0;
        // t — от коридора к фасаду; z(t) — в системе плана.
        double zc = apt.North ? apt.Z1 : apt.Z0, zf = apt.North ? apt.Z0 : apt.Z1, dir = apt.North ? -1 : 1;
        (double Z0, double Z1) Band(double t0, double t1)
        {
            double p = zc + dir * t0, q = zc + dir * t1;
            return (Math.Min(p, q), Math.Max(p, q));
        }
        double depth = Math.Abs(zf - zc);
        double strip = Math.Min(WetStrip, depth * 0.4);
        int target = RoomsOf(apt.Type);
        bool big = target >= 2 && w >= 6.0;
        double wetW = Math.Min(w - 1.4, BathWidth + (big ? ToiletWidth : 0));
        double kitW = apt.Type == ApartmentType.Studio ? Math.Min(2.0, w * 0.45) : Math.Min(KitchenWidth, w * 0.45);
        bool high = apt.WetAtHighX;
        var (sz0, sz1) = Band(0, strip);
        var (fz0, fz1) = Band(strip, depth);
        string p = apt.Id + ":";
        // Санузел (и уборная) у мокрой стороны, прихожая — остальная полоса у коридора.
        double wx0 = high ? a1 - wetW : a0, wx1 = high ? a1 : a0 + wetW;
        if (big)
        {
            double bx0 = high ? wx1 - BathWidth : wx0, bx1 = bx0 + BathWidth;
            double tx0 = high ? wx0 : bx1, tx1 = tx0 + (wx1 - wx0 - BathWidth);
            rooms.Add(new LayoutRoom(p + "bath", LayoutRoomKind.Bathroom, "Ванная", bx0, sz0, bx1, sz1, apt.Id));
            rooms.Add(new LayoutRoom(p + "wc", LayoutRoomKind.Toilet, "Уборная", tx0, sz0, tx1, sz1, apt.Id));
        }
        else rooms.Add(new LayoutRoom(p + "bath", LayoutRoomKind.Bathroom, "Санузел", wx0, sz0, wx1, sz1, apt.Id));
        rooms.Add(new LayoutRoom(p + "hall", LayoutRoomKind.Hall, "Прихожая", high ? a0 : wx1, sz0, high ? wx0 : a1, sz1, apt.Id));
        // Кухня у мокрой стороны, комнаты — к другой стене.
        double kx0 = high ? a1 - kitW : a0, kx1 = high ? a1 : a0 + kitW;
        var kitchenKind = gas ? LayoutRoomKind.KitchenGas : LayoutRoomKind.Kitchen;
        string kitchenName = apt.Type is ApartmentType.Euro2 or ApartmentType.Euro3 ? "Кухня-гостиная (зона кухни)" : apt.Type == ApartmentType.Studio ? "Кухонная ниша" : "Кухня";
        rooms.Add(new LayoutRoom(p + "kit", kitchenKind, kitchenName, kx0, fz0, kx1, fz1, apt.Id));
        double rx0 = high ? a0 : kx1, rx1 = high ? kx0 : a1;
        int count = Math.Max(1, Math.Min(target, (int)Math.Floor((rx1 - rx0) / 2.7)));
        double rw = (rx1 - rx0) / count;
        double living = 0;
        for (int i = 0; i < count; i++)
        {
            int k = high ? i : count - 1 - i;
            double x0 = rx0 + k * rw, x1 = x0 + rw;
            var kind = i == 0 ? LayoutRoomKind.Living : LayoutRoomKind.Bedroom;
            string name = i == 0 ? apt.Type is ApartmentType.Euro2 or ApartmentType.Euro3 ? "Кухня-гостиная (зона гостиной)" : "Общая комната" : "Спальня";
            rooms.Add(new LayoutRoom(p + "r" + (i + 1).ToString(Ru), kind, name, Math.Round(x0, 3), fz0, Math.Round(x1, 3), fz1, apt.Id));
            living += (x1 - x0) * (fz1 - fz0);
        }
        var risers = new List<RiserStack>();
        double rx = high ? a1 - 0.3 : a0 + 0.3, rz = zc + dir * 0.35;
        risers.Add(new RiserStack(p + "B1", RiserKind.ColdWater, Math.Round(rx, 3), Math.Round(rz, 3), apt.Id));
        risers.Add(new RiserStack(p + "T3", RiserKind.HotWater, Math.Round(rx + (high ? -0.25 : 0.25), 3), Math.Round(rz, 3), apt.Id));
        risers.Add(new RiserStack(p + "K1", RiserKind.Sewer, Math.Round(rx, 3), Math.Round(rz + dir * 0.3, 3), apt.Id));
        if (gas) risers.Add(new RiserStack(p + "G", RiserKind.Gas, Math.Round(high ? a1 - 0.25 : a0 + 0.25, 3), Math.Round(zc + dir * (strip + 0.3), 3), apt.Id));
        double total = rooms.Sum(r => r.Area);
        return (rooms, risers, apt with { TotalArea = Math.Round(total, 2), LivingArea = Math.Round(living, 2), Rooms = count });
    }

    /// <summary>Ядро: лифтовые шахты у коридора (север), лестничные клетки у юга, лифтовой холл в коридоре ядра и тамбур-шлюз перед лестницей.</summary>
    private static void AddCoreRooms(BuildingLayoutPlan plan, string id, List<LayoutRoom> rooms, bool basement)
    {
        var c = plan.Core;
        double hc = plan.Mix.CorridorWidth / 2;
        double shaftsW = c.Lifts * ShaftSize;
        double sx = (c.X0 + c.X1) / 2 - shaftsW / 2;
        for (int i = 0; i < c.Lifts; i++)
        {
            bool fire = i == 0 && c.FireLifts > 0;
            rooms.Add(new LayoutRoom($"{id}:lift{i + 1}", LayoutRoomKind.LiftShaft,
                (fire ? "Лифт для пожарных подразделений" : "Лифтовая шахта") + (basement ? " (приямок)" : string.Empty),
                Math.Round(sx + i * ShaftSize, 3), Math.Round(-hc - ShaftSize, 3), Math.Round(sx + (i + 1) * ShaftSize, 3), -hc));
        }
        // Остаток северной части ядра — венткамера дымоудаления / шахты систем.
        rooms.Add(new LayoutRoom($"{id}:coreN", basement ? LayoutRoomKind.Ventilation : LayoutRoomKind.Vestibule,
            basement ? "Венткамера подпора воздуха" : "Тамбур-шлюз лифтов (подпор воздуха)", c.X0, c.Z0, c.X1, Math.Round(-hc - ShaftSize, 3)));
        rooms.Add(new LayoutRoom($"{id}:lobby", LayoutRoomKind.LiftLobby, "Лифтовой холл (противопожарные двери EI 30)", c.X0, -hc, c.X1, hc));
        // Лестницы: одна клетка у западной стены ядра, вторая (или ножничная) — у восточной; между ними — тамбур-шлюз.
        double cell = c.StairForm switch { CoreStairForm.Straight => 2.6, CoreStairForm.Scissor => 3.6, CoreStairForm.ThreeFlight or CoreStairForm.Curved => 4.2, _ => 3.0 };
        int cells = c.StairForm == CoreStairForm.Scissor ? 1 : c.Stairs;
        string stairName = $"Лестничная клетка {c.StairCategory.Split(' ')[0]} ({LayoutRooms.StairTitle(c.StairForm)})";
        rooms.Add(new LayoutRoom($"{id}:st1", LayoutRoomKind.Stair, cells == 1 && c.Stairs == 2 ? $"Ножничная лестница Н2 + Н3" : stairName, c.X0, hc, Math.Round(c.X0 + cell, 3), c.Z1));
        double vx1 = c.X1;
        if (cells >= 2)
        {
            rooms.Add(new LayoutRoom($"{id}:st2", LayoutRoomKind.Stair, $"Лестничная клетка Н3 ({LayoutRooms.StairTitle(c.StairForm)})", Math.Round(c.X1 - cell, 3), hc, c.X1, c.Z1));
            vx1 = Math.Round(c.X1 - cell, 3);
        }
        rooms.Add(new LayoutRoom($"{id}:vest", basement ? LayoutRoomKind.Storage : LayoutRoomKind.Vestibule,
            basement ? "Тамбур-шлюз подвала" : "Тамбур-шлюз (противопожарные преграды 2-го типа)", Math.Round(c.X0 + cell, 3), hc, vx1, c.Z1));
    }

    /// <summary>
    /// Подвал (−1 этаж): ядро (приямки лифтов, лестницы), коридор, ИТП и водомерный узел с насосной (север-запад), электрощитовая
    /// ВРУ — под жилой комнатой 1-го этажа (не под мокрыми зонами, ПУЭ п. 7.1.29), венткамера, техподполье.
    /// </summary>
    public static FloorPackagePlan BuildBasement(BuildingLayoutPlan plan, FloorPackagePlan first)
    {
        const string id = "b-1";
        var rooms = new List<LayoutRoom>();
        AddCoreRooms(plan, id, rooms, basement: true);
        double hc = plan.Mix.CorridorWidth / 2;
        rooms.Add(new LayoutRoom($"{id}:corW", LayoutRoomKind.Corridor, "Коридор подвала (магистрали)", plan.X0, -hc, plan.Core.X0, hc));
        rooms.Add(new LayoutRoom($"{id}:corE", LayoutRoomKind.Corridor, "Коридор подвала (магистрали)", plan.Core.X1, -hc, plan.X1, hc));
        double westN = plan.Core.X0 - plan.X0;
        double itpW = Math.Min(9.0, westN * 0.55);
        rooms.Add(new LayoutRoom($"{id}:itp", LayoutRoomKind.Itp, "ИТП", plan.X0, plan.Z0, Math.Round(plan.X0 + itpW, 3), -hc));
        rooms.Add(new LayoutRoom($"{id}:water", LayoutRoomKind.WaterMeter, "Водомерный узел и насосная", Math.Round(plan.X0 + itpW, 3), plan.Z0, plan.Core.X0, -hc));
        var el = ElectricalRect(plan, first);
        rooms.Add(new LayoutRoom($"{id}:vru", LayoutRoomKind.Electrical, "Электрощитовая (ВРУ)", el.X0, el.Z0, el.X1, el.Z1));
        // Остальное — техподполье (полосы без ИТП, насосной и ВРУ), разбитое по участкам вокруг электрощитовой.
        void Fill(double x0, double z0, double x1, double z1, string key)
        {
            if (x1 - x0 > 0.5 && z1 - z0 > 0.5) rooms.Add(new LayoutRoom($"{id}:{key}", LayoutRoomKind.Storage, "Техподполье", Math.Round(x0, 3), z0, Math.Round(x1, 3), z1));
        }
        foreach (var (bx0, bx1, bz0, bz1, key) in new[]
                 {
                     (plan.Core.X1, plan.X1, plan.Z0, -hc, "neN"),
                     (plan.X0, plan.Core.X0, hc, plan.Z1, "swS"),
                     (plan.Core.X1, plan.X1, hc, plan.Z1, "seS")
                 })
        {
            bool inside = el.X0 >= bx0 - 1e-6 && el.X1 <= bx1 + 1e-6 && el.Z0 >= bz0 - 1e-6 && el.Z1 <= bz1 + 1e-6;
            if (!inside) { Fill(bx0, bz0, bx1, bz1, key); continue; }
            Fill(bx0, bz0, el.X0, bz1, key + "a");
            Fill(el.X1, bz0, bx1, bz1, key + "b");
            Fill(el.X0, bz0, el.X1, el.Z0, key + "c");
            Fill(el.X0, el.Z1, el.X1, bz1, key + "d");
        }
        // В подвале стояки не начинаются: магистрали подходят к низу стояков 1-го этажа.
        return new FloorPackagePlan(id, $"Подвал ({(-plan.BasementDepth).ToString("+0.000;-0.000", Ru)})", 0, 0, -plan.BasementDepth, true, rooms, [], [], "П");
    }

    /// <summary>Электрощитовая под жилой комнатой 1-го этажа (северо-восток), шириной до 4,2 м — без мокрых зон над ней.</summary>
    public static (double X0, double Z0, double X1, double Z1) ElectricalRect(BuildingLayoutPlan plan, FloorPackagePlan first)
    {
        double hc = plan.Mix.CorridorWidth / 2;
        var candidates = first.Rooms.Where(r => LayoutRooms.IsDwelling(r.Kind) && r.Width >= 2.7)
            .OrderBy(r => r.CenterZ < 0 && r.CenterX > plan.Core.X1 ? 0 : r.CenterZ < 0 ? 1 : 2).ThenBy(r => -r.CenterX).ToList();
        foreach (var room in candidates)
        {
            double w = Math.Min(4.2, room.Width - 0.1);
            double x0 = room.CenterX - w / 2;
            var rect = new LayoutRoom("probe", LayoutRoomKind.Electrical, "", Math.Round(x0, 3), room.Z0, Math.Round(x0 + w, 3), room.Z1);
            if (first.Rooms.All(u => !LayoutRooms.IsWet(u.Kind) || rect.OverlapArea(u) < 1e-6))
                return (rect.X0, rect.Z0, rect.X1, rect.Z1);
        }
        return (plan.Core.X1 + 0.5, plan.Z0, Math.Min(plan.X1, plan.Core.X1 + 4.5), -hc);
    }

    /// <summary>
    /// Узлы ввода: водопровод — в водомерный узел, теплосеть — в ИТП, кабель и связь — в электрощитовую, выпуск канализации — из
    /// коридора подвала; газ — на 1-й этаж (в подвал не вводится). Точка — проекция ввода участка на контур, иначе на северном фасаде.
    /// </summary>
    public static List<UtilityInlet> BuildInlets(BuildingLayoutPlan plan, IReadOnlyList<(GpzuZoneKind Kind, double X, double Z)> site, bool gas)
    {
        var list = new List<UtilityInlet>();
        var basement = plan.Basement;
        LayoutRoom? Room(LayoutRoomKind k) => basement?.Rooms.FirstOrDefault(r => r.Kind == k);
        (double X, double Z) Snap(GpzuZoneKind kind, LayoutRoom? target)
        {
            if (site.FirstOrDefault(s => s.Kind == kind) is { } e && (e.X != 0 || e.Z != 0))
            {
                // Проекция на ближайшую сторону контура в пределах помещения приёма (если оно у фасада).
                double x = Math.Clamp(e.X, plan.X0, plan.X1), z = Math.Clamp(e.Z, plan.Z0, plan.Z1);
                if (target is not null)
                {
                    x = Math.Clamp(x, target.X0 + 0.3, target.X1 - 0.3);
                    z = Math.Abs(target.Z0 - plan.Z0) < 0.01 ? plan.Z0 : Math.Abs(target.Z1 - plan.Z1) < 0.01 ? plan.Z1 : z;
                }
                return (Math.Round(x, 3), Math.Round(z, 3));
            }
            if (target is null) return (plan.X0, 0);
            bool northFacade = Math.Abs(target.Z0 - plan.Z0) < 0.01;
            return (Math.Round(target.CenterX, 3), northFacade ? plan.Z0 : plan.Z1);
        }
        void Add(GpzuZoneKind kind, string title, LayoutRoom? target)
        {
            if (target is null) return;
            var (x, z) = Snap(kind, target);
            list.Add(new UtilityInlet("in-" + kind, kind, title, x, z, target.Id, true));
        }
        Add(GpzuZoneKind.WaterSupply, "Ввод водопровода В1", Room(LayoutRoomKind.WaterMeter));
        Add(GpzuZoneKind.HeatNetwork, "Ввод теплосети Т1/Т2", Room(LayoutRoomKind.Itp));
        Add(GpzuZoneKind.PowerCable, "Ввод кабеля 0,4 кВ", Room(LayoutRoomKind.Electrical));
        Add(GpzuZoneKind.Communication, "Ввод сетей связи", Room(LayoutRoomKind.Electrical));
        if (basement?.Rooms.FirstOrDefault(r => r.Kind == LayoutRoomKind.Corridor) is { } corridor)
            list.Add(new UtilityInlet("in-" + GpzuZoneKind.Sewer, GpzuZoneKind.Sewer, "Выпуск канализации К1", Math.Round(plan.X0 + 1.2, 3), plan.Z0, corridor.Id, true));
        if (gas && plan.Floors.FirstOrDefault()?.Rooms.FirstOrDefault(r => r.Kind == LayoutRoomKind.KitchenGas) is { } kitchen)
            list.Add(new UtilityInlet("in-" + GpzuZoneKind.GasPipeline, GpzuZoneKind.GasPipeline, "Ввод газопровода (1-й этаж, в обход подвала)",
                kitchen.CenterX, kitchen.Z0 <= plan.Z0 + 0.01 ? plan.Z0 : plan.Z1, kitchen.Id, false));
        return list;
    }

    /// <summary>
    /// Магистрали подвала: от помещения-источника (водомерный узел — В1, ИТП — Т3 и Т1, ВРУ — ЭС, выпуск К1) по коридору подвала к
    /// низу каждого стояка нижнего пакета этажей. Газовые стояки магистралей в подвале не имеют.
    /// </summary>
    public static List<MainLine> BuildMains(BuildingLayoutPlan plan)
    {
        var list = new List<MainLine>();
        var basement = plan.Basement;
        var first = plan.Floors.FirstOrDefault();
        if (basement is null || first is null) return list;
        LayoutRoom? Room(LayoutRoomKind k) => basement.Rooms.FirstOrDefault(r => r.Kind == k);
        var sewerOut = plan.Inlets.FirstOrDefault(i => i.Kind == GpzuZoneKind.Sewer);
        foreach (var r in first.Risers.Where(r => r.Kind != RiserKind.Gas).OrderBy(r => r.Kind).ThenBy(r => r.X).ThenBy(r => r.Z))
        {
            (double X, double Z)? source = r.Kind switch
            {
                RiserKind.ColdWater => Room(LayoutRoomKind.WaterMeter) is { } w ? (w.CenterX, w.CenterZ) : null,
                RiserKind.HotWater or RiserKind.Heating => Room(LayoutRoomKind.Itp) is { } i ? (i.CenterX, i.CenterZ) : null,
                RiserKind.Power => Room(LayoutRoomKind.Electrical) is { } e ? (e.CenterX, e.CenterZ) : null,
                RiserKind.Sewer => sewerOut is { } s ? (s.X, s.Z) : null,
                _ => null
            };
            if (source is not { } src) continue;
            // По коридору подвала (ось z = смещение по виду сети, чтобы трубы не сливались на схеме).
            double lane = r.Kind switch { RiserKind.ColdWater => -0.45, RiserKind.HotWater => -0.15, RiserKind.Heating => 0.15, RiserKind.Sewer => 0.45, _ => 0.6 };
            var path = new List<(double X, double Z)> { (Math.Round(src.X, 3), Math.Round(src.Z, 3)), (Math.Round(src.X, 3), lane), (r.X, lane), (r.X, r.Z) };
            list.Add(new MainLine($"m-{r.Id}", r.Kind, path, r.Id));
        }
        return list;
    }
}
