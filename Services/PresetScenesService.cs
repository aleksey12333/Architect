using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>Готовая демо-сцена: постройки участка + элементы (+ явные координационные оси, если пресет их задаёт).</summary>
public sealed record DemoScene(string Title, IReadOnlyList<BuildingGroup> Groups, IReadOnlyList<ArchitecturalElement> Elements,
    IReadOnlyList<StructuralGridAxis>? Axes = null);

/// <summary>Описание пресета для чипа быстрого старта: короткая подпись, полное название (RU / EN) и глиф Segoe Fluent Icons.</summary>
public sealed record PresetInfo(string Id, string ChipKey, string TitleRu, string TitleEn, string Glyph)
{
    public string Title(string language) => language == LocalizationService.English ? TitleEn : TitleRu;
}

/// <summary>
/// Детальные демонстрационные сцены — шаблоны стартового окна (коттедж, каркасный корпус, спиральная башня, купольный
/// павильон). Все координаты — Y-Up, центр элемента высотой H на отметке BaseY равен BaseY + H / 2. Окна и двери привязаны
/// к стенам (HostElementId), вырезы в стенах строит геометрия по этим привязкам.
/// </summary>
public static class PresetScenesService
{
    public const string Cottage = "cottage";
    public const string Frame = "frame";
    public const string Tower = "tower";
    public const string Pavilion = "pavilion";

    public static readonly IReadOnlyList<PresetInfo> All =
    [
        new(Cottage, "PresetCottage", "Участок: Коттедж + Баня с бассейном", "Site: Cottage + Sauna with pool", ""),
        new(Frame, "PresetFrame", "Каркасный корпус: монолитный каркас, 4 этажа", "Frame building: cast-in-place frame, 4 storeys", ""),
        new(Tower, "PresetTower", "Уникальная 30-этажная спиральная башня", "Unique 30-storey twisted tower", ""),
        new(Pavilion, "PresetPavilion", "Купольный павильон", "Dome pavilion", "")
    ];

    public static PresetInfo Info(string presetId) =>
        All.FirstOrDefault(p => p.Id == presetId) ?? throw new ArgumentOutOfRangeException(nameof(presetId), presetId, "Неизвестный пресет");

    public static DemoScene Create(string presetId, string language = LocalizationService.Russian)
    {
        if (presetId == Frame)
        {
            var (frameGroups, frameElements, axes) = CreateFrameBuilding();
            return new DemoScene(Info(presetId).Title(language), frameGroups, frameElements, axes);
        }
        var (groups, elements) = presetId switch
        {
            Cottage => CreateCottageSite(),
            Tower => CreateTwistedTower(),
            Pavilion => CreateDomePavilion(),
            _ => throw new ArgumentOutOfRangeException(nameof(presetId), presetId, "Неизвестный пресет")
        };
        return new DemoScene(Info(presetId).Title(language), groups, elements);
    }

    // ───────────────────────── Пресет «Каркасный корпус»: монолитный каркас 24 × 18 м, 4 этажа ─────────────────────────

    /// <summary>
    /// Административный корпус: сетка колонн 6 × 6 м (оси 1–5 по X, А–Г по Z), колонны 400 × 400, ригели 300 × 500 по всем
    /// осям, плиты 220 мм, этаж 3.6 м, витражи по периметру, лестница в пролёте 4–5 / В–Г с проёмами в перекрытиях,
    /// плоская кровля и фундаментная плита 600 мм. Явные оси передаются в сцену вместе с элементами.
    /// </summary>
    private static (List<BuildingGroup>, List<ArchitecturalElement>, List<StructuralGridAxis>) CreateFrameBuilding()
    {
        const string g = "Каркасный корпус";
        const int floors = 4;
        const double floorH = 3.6, slabT = 0.22, fH = 0.6, col = 0.4, beamB = 0.3, beamH = 0.5, edge = 0.4;
        double[] xs = [-12, -6, 0, 6, 12];
        double[] zs = [9, 3, -3, -9];
        double colH = floorH - slabT;
        double planW = xs[^1] - xs[0] + 2 * edge, planD = zs[0] - zs[^1] + 2 * edge;
        var list = new List<ArchitecturalElement>();

        list.Add(Box(g, ElementTypes.Foundation, "Фундаментная плита", 0, 0, -fH / 2, 0, planW + 1.2, fH, planD + 1.2, "concrete"));
        for (int level = 0; level < floors; level++)
        {
            double b = level * floorH;
            double beamTop = b + colH;
            for (int i = 0; i < xs.Length; i++)
                for (int j = 0; j < zs.Length; j++)
                    list.Add(Box(g, ElementTypes.Column, $"Колонна {i + 1}/{StructuralAxesBuilder.Letter(j)} Э{level + 1}", level,
                        xs[i], b + colH / 2, zs[j], col, colH, col, "concrete"));

            // Ригели вдоль буквенных осей (по X) и вдоль цифровых осей (по Z) под плитой.
            for (int j = 0; j < zs.Length; j++)
                for (int i = 0; i + 1 < xs.Length; i++)
                    list.Add(Box(g, ElementTypes.Beam, $"Ригель {StructuralAxesBuilder.Letter(j)}/{i + 1}-{i + 2} Э{level + 1}", level,
                        (xs[i] + xs[i + 1]) / 2, beamTop - beamH / 2, zs[j], xs[i + 1] - xs[i], beamH, beamB, "concrete"));
            for (int i = 0; i < xs.Length; i++)
                for (int j = 0; j + 1 < zs.Length; j++)
                {
                    var beam = Box(g, ElementTypes.Beam, $"Ригель {i + 1}/{StructuralAxesBuilder.Letter(j)}-{StructuralAxesBuilder.Letter(j + 1)} Э{level + 1}", level,
                        xs[i], beamTop - beamH / 2, (zs[j] + zs[j + 1]) / 2, zs[j] - zs[j + 1], beamH, beamB, "concrete");
                    beam.SetRotationY(90);
                    list.Add(beam);
                }

            // Витражи по краю перекрытия; на первом этаже — главный вход по оси А.
            double cwY = b + colH / 2;
            var front = Box(g, ElementTypes.CurtainWall, $"Витраж по оси А Э{level + 1}", level, 0, cwY, zs[0] + edge - 0.06, planW, colH, 0.12, "glass");
            list.Add(front);
            list.Add(Box(g, ElementTypes.CurtainWall, $"Витраж по оси Г Э{level + 1}", level, 0, cwY, zs[^1] - edge + 0.06, planW, colH, 0.12, "glass"));
            var left = Box(g, ElementTypes.CurtainWall, $"Витраж по оси 1 Э{level + 1}", level, xs[0] - edge + 0.06, cwY, 0, planD - 0.24, colH, 0.12, "glass");
            left.SetRotationY(90);
            list.Add(left);
            var right = Box(g, ElementTypes.CurtainWall, $"Витраж по оси 5 Э{level + 1}", level, xs[^1] + edge - 0.06, cwY, 0, planD - 0.24, colH, 0.12, "glass");
            right.SetRotationY(90);
            list.Add(right);
            if (level == 0) Opening(list, front, ElementTypes.Door, "Главный вход", 0, 2.4, 2.4, 0);

            if (level < floors - 1)
            {
                var slab = Box(g, ElementTypes.Slab, $"Плита перекрытия Э{level + 1}", level, 0, beamTop + slabT / 2, 0, planW, slabT, planD, "concrete");
                list.Add(slab);
                // Лестница в пролёте 4–5 / В–Г (между ригелями), над маршем — проём в плите.
                list.Add(Box(g, ElementTypes.Stairs, $"Лестница Э{level + 1}", level, 9, b + floorH / 2, -6, 1.2, floorH, 3.6, "concrete"));
                list.Add(SlabOpening(slab, $"Лестничный проём Э{level + 1}", 9, -6, 1.4, 3.8));
            }
            else
            {
                var roof = Box(g, ElementTypes.Roof, "Плоская кровля", level, 0, beamTop + slabT / 2, 0, planW, slabT, planD, "concrete");
                roof.RoofShape = RoofShapes.Flat;
                list.Add(roof);
            }
        }

        var axes = new List<StructuralGridAxis>();
        for (int i = 0; i < xs.Length; i++) axes.Add(new StructuralGridAxis((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StructuralGridAxis.AlongZ, xs[i], g));
        for (int j = 0; j < zs.Length; j++) axes.Add(new StructuralGridAxis(StructuralAxesBuilder.Letter(j), StructuralGridAxis.AlongX, zs[j], g));
        return ([new BuildingGroup(g, "office", "#5AC8FA")], list, axes);
    }

    // ───────────────────────── Пресет 1: Коттедж + Баня с бассейном ─────────────────────────

    private static (List<BuildingGroup>, List<ArchitecturalElement>) CreateCottageSite()
    {
        const string house = "Главный дом";
        const string sauna = "Баня с бассейном";
        var list = new List<ArchitecturalElement>();

        // Главный дом 12 × 9 м, 2 этажа.
        const double fH = 0.5, wallH = 3.0, wallT = 0.3, slabT = 0.22;
        list.Add(Box(house, ElementTypes.Foundation, "Фундаментная плита", 0, 0, fH / 2, 0, 12.6, fH, 9.6, "concrete"));
        double baseY = fH;
        for (int level = 0; level < 2; level++)
        {
            var front = Wall(list, house, level, $"Стена фасадная Э{level + 1}", -6, 4.5, 6, 4.5, baseY, wallH, wallT);
            var back = Wall(list, house, level, $"Стена задняя Э{level + 1}", 6, -4.5, -6, -4.5, baseY, wallH, wallT);
            var left = Wall(list, house, level, $"Стена левая Э{level + 1}", -6, -4.5, -6, 4.5, baseY, wallH, wallT);
            var right = Wall(list, house, level, $"Стена правая Э{level + 1}", 6, 4.5, 6, -4.5, baseY, wallH, wallT);

            if (level == 0)
            {
                // Гостиная: панорамные окна в пол на террасу по обе стороны от входной двери.
                Opening(list, front, ElementTypes.Window, "Панорамное окно гостиной", -3.5, 2.6, 2.3, 0.2);
                Opening(list, front, ElementTypes.Window, "Панорамное окно столовой", 3.5, 2.6, 2.3, 0.2);
                Opening(list, front, ElementTypes.Door, "Входная дверь", 0, 1.1, 2.2, 0);
            }
            else
            {
                Opening(list, front, ElementTypes.Window, "Окно спальни", -3.5, 1.6, 1.5, 0.9);
                Opening(list, front, ElementTypes.Window, "Окно спальни", 3.5, 1.6, 1.5, 0.9);
                Opening(list, front, ElementTypes.Window, "Панорамное окно холла", 0, 2.4, 1.9, 0.6);
            }
            Opening(list, back, ElementTypes.Window, "Окно", -2.5, 1.5, 1.5, 0.9);
            Opening(list, back, ElementTypes.Window, "Окно", 2.5, 1.5, 1.5, 0.9);
            Opening(list, left, ElementTypes.Window, "Окно", 0, 1.2, 1.5, 0.9);
            Opening(list, right, ElementTypes.Window, level == 0 ? "Панорамное окно кухни" : "Окно",
                0, level == 0 ? 3.0 : 1.2, level == 0 ? 2.1 : 1.5, level == 0 ? 0.4 : 0.9);

            baseY += wallH;
            var slab = Box(house, ElementTypes.Slab, $"Перекрытие Э{level + 1}", level, 0, baseY + slabT / 2, 0, 12.3, slabT, 9.3, "concrete");
            list.Add(slab);
            // Сквозное отверстие лестничного проёма над маршем первого этажа.
            if (level == 0) list.Add(SlabOpening(slab, "Лестничный проём", 4.2, -2.8, 1.3, 3.8));
            baseY += slabT;
        }
        list.Add(Box(house, ElementTypes.Stairs, "Лестница", 0, 4.2, fH + wallH / 2, -2.8, 1.1, wallH, 3.6, "concrete"));
        var roof = Box(house, ElementTypes.Roof, "Двускатная кровля", 1, 0, baseY + 1.3, 0, 13.2, 2.6, 10.2, "metal_roof");
        roof.RoofShape = RoofShapes.Gable;
        list.Add(roof);
        list.Add(Box(house, ElementTypes.Terrace, "Терраса", 0, 0, 0.2, 7.5, 8, 0.4, 5, "wood"));

        // Баня 6 × 5 м с бассейном и террасой (справа от дома, отступ 4 м).
        const double ox = 6.3 + 4 + 3;
        list.Add(Box(sauna, ElementTypes.Foundation, "Фундамент бани", 0, ox, 0.2, -2, 6.4, 0.4, 5.4, "concrete"));
        var sf = Wall(list, sauna, 0, "Стена бани фасадная", ox - 3, 0.5, ox + 3, 0.5, 0.4, 2.7, 0.25, "timber");
        Wall(list, sauna, 0, "Стена бани задняя", ox + 3, -4.5, ox - 3, -4.5, 0.4, 2.7, 0.25, "timber");
        var sl = Wall(list, sauna, 0, "Стена бани левая", ox - 3, -4.5, ox - 3, 0.5, 0.4, 2.7, 0.25, "timber");
        Wall(list, sauna, 0, "Стена бани правая", ox + 3, 0.5, ox + 3, -4.5, 0.4, 2.7, 0.25, "timber");
        Opening(list, sf, ElementTypes.Door, "Дверь бани", 1.5, 0.9, 2.1, 0);
        Opening(list, sf, ElementTypes.Window, "Панорамное окно комнаты отдыха (вид на бассейн)", -1.2, 1.8, 1.6, 0.5);
        Opening(list, sl, ElementTypes.Window, "Окно парной", 0, 0.8, 0.8, 1.2);
        var saunaRoof = Box(sauna, ElementTypes.Roof, "Кровля бани", 0, ox, 3.1 + 0.9, -2, 7, 1.8, 6.2, "metal_roof");
        saunaRoof.RoofShape = RoofShapes.Gable;
        list.Add(saunaRoof);
        // Терраса 9 × 8 м с П-образным вырезом под утопленную чашу бассейна 4 × 7 м (контур в локальных координатах террасы).
        var deck = Box(sauna, ElementTypes.Terrace, "Терраса у бассейна", 0, ox, 0.15, 4.5, 9, 0.3, 8, "wood");
        deck.Polygon2D =
        [
            new(-4.5, -4), new(4.5, -4), new(4.5, 4), new(3.0, 4),
            new(3.0, -3.0), new(-1.0, -3.0), new(-1.0, 4), new(-4.5, 4)
        ];
        list.Add(deck);
        // Чаша утоплена в грунт: верх борта на отметке 0, дно на −1.5 м.
        list.Add(Box(sauna, ElementTypes.Pool, "Бассейн", 0, ox + 1, -0.75, 5, 4, 1.5, 7, "water"));

        var groups = new List<BuildingGroup>
        {
            new(house, "residential", "#3B82F6"),
            new(sauna, "bathhouse", "#10B981") { OriginX = ox }
        };
        return (groups, list);
    }

    // ───────────────────────── Пресет 2: 30-этажная спиральная башня ─────────────────────────

    private static (List<BuildingGroup>, List<ArchitecturalElement>) CreateTwistedTower()
    {
        const string tower = "Спиральная башня";
        const string podium = "Стилобат";
        const int floors = 30;
        const double levelH = 3.6, podiumH = 4.5, slabT = 0.3;
        var list = new List<ArchitecturalElement>();

        // Стилобат 2 этажа на фундаментной плите 2.4 м (низ −1.200, верх +1.200) — под 30-этажное ядро.
        list.Add(Box(podium, ElementTypes.Foundation, "Фундаментная плита", 0, 0, 0, 0, 60, 2.4, 44, "concrete"));
        // Плита покрытия стилобата — по сетке колонн 50 × 30 м с вылетом 1 м, витраж — по её краю.
        list.Add(Box(podium, ElementTypes.CurtainWall, "Витраж стилобата", 0, 0, 1.2 + podiumH, 16, 52, podiumH * 2, 0.15, "glass"));
        var podiumRoof = Box(podium, ElementTypes.Slab, "Кровля стилобата", 1, 0, 1.2 + podiumH * 2 + slabT / 2, 0, 52, slabT, 32, "concrete");
        list.Add(podiumRoof);
        // Шахта сквозного ядра в плите стилобата (внутренний контур ядра 8 × 8 м).
        list.Add(SlabOpening(podiumRoof, "Шахта ядра", 0, 0, 8, 8));
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 4; j++)
                list.Add(Box(podium, ElementTypes.Column, $"Колонна {(char)('А' + j)}-{i + 1}", 0,
                    -25 + i * 10, 1.2 + podiumH, -15 + j * 10, 0.8, podiumH * 2, 0.8, "concrete"));

        // Колонны стилобата под 16 узлами кольца диагрида (R = 12 м): нагрузка оболочки башни идёт напрямую в фундамент.
        for (int k = 0; k < 16; k++)
        {
            double a = 2 * Math.PI * k / 16;
            var ringColumn = Box(podium, ElementTypes.Column, $"Колонна кольца {k + 1}", 0,
                Math.Round(12 * Math.Cos(a), 4), 1.2 + podiumH, Math.Round(12 * Math.Sin(a), 4), 0.9, podiumH * 2, 0.9, "concrete");
            ringColumn.ProfileType = ProfileTypes.Circle;
            list.Add(ringColumn);
        }

        double towerBase = 1.2 + podiumH * 2 + slabT;
        // Сквозное ядро жёсткости: от фундаментной плиты через стилобат на всю высоту башни.
        double coreTop = towerBase + floors * levelH;
        list.Add(Box(tower, ElementTypes.Core, "Ядро жёсткости", 0, 0, (1.2 + coreTop) / 2, 0, 9, coreTop - 1.2, 9, "concrete"));

        // Параметрические элементы: солвер и геометрия разворачивают RepeatLevels = 30 с закруткой 3° и сужением 0.8 % на этаж.
        var slab = Box(tower, ElementTypes.CantileverSlab, "Типовое перекрытие (закрутка)", 0, 0, towerBase + levelH - slabT / 2, 0, 28, slabT, 28, "concrete");
        slab.Polygon2D = RegularPolygon(8, 14);
        Parametric(slab, floors, levelH, 3.0, 0.992);
        list.Add(slab);

        var facade = Box(tower, ElementTypes.CurtainWall, "Типовой витраж этажа", 0, 0, towerBase + levelH / 2, 0, 26, levelH - slabT, 26, "glass");
        facade.Polygon2D = RegularPolygon(8, 13);
        Parametric(facade, floors, levelH, 3.0, 0.992);
        list.Add(facade);

        var diagrid = Box(tower, ElementTypes.Diagrid, "Диагрид-оболочка фасада", 0, 12, towerBase + levelH / 2, 0, 0.5, levelH, 0.5, "steel");
        diagrid.Rotation = [12, 0, 0];
        Parametric(diagrid, floors, levelH, 3.0, 0.992);
        list.Add(diagrid);

        var groups = new List<BuildingGroup>
        {
            new(podium, "podium", "#64748B"),
            new(tower, "tower", "#38BDF8")
        };
        return (groups, list);
    }

    // ───────────────────────── Пресет 3: Купольный павильон ─────────────────────────

    private static (List<BuildingGroup>, List<ArchitecturalElement>) CreateDomePavilion()
    {
        const string pavilion = "Выставочный павильон";
        const double radius = 18, colH = 8;
        var list = new List<ArchitecturalElement>();

        var foundation = Box(pavilion, ElementTypes.Foundation, "Круглая фундаментная плита", 0, 0, 0.4, 0, radius * 2 + 2, 0.8, radius * 2 + 2, "concrete");
        foundation.Polygon2D = RegularPolygon(32, radius + 1);
        list.Add(foundation);

        // Радиальная схема: 16 колонн по окружности.
        const int columns = 16;
        for (int i = 0; i < columns; i++)
        {
            double a = 2 * Math.PI * i / columns;
            var column = Box(pavilion, ElementTypes.Column, $"Колонна {i + 1}", 0,
                Math.Cos(a) * radius, 0.8 + colH / 2, Math.Sin(a) * radius, 0.7, colH, 0.7, "concrete");
            column.ProfileType = ProfileTypes.Circle;
            list.Add(column);
        }

        var ring = Box(pavilion, ElementTypes.Beam, "Опорное кольцо", 0, 0, 0.8 + colH + 0.4, 0, radius * 2 + 1, 0.8, radius * 2 + 1, "concrete");
        ring.Polygon2D = RegularPolygon(32, radius + 0.5);
        list.Add(ring);

        for (int i = 0; i < 4; i++)
        {
            // Большепролётные фермы: пролёт чуть короче диаметра, чтобы опорные узлы оставались под оболочкой купола.
            var truss = Box(pavilion, ElementTypes.Truss, $"Ферма {i + 1}", 0, 0, 0.8 + colH + 1.6, 0, radius * 2 - 1.4, 1.6, 0.4, "steel");
            truss.SetRotationY(i * 45);
            list.Add(truss);
        }

        list.Add(Box(pavilion, ElementTypes.Shell, "Купольная оболочка", 0, 0, 0.8 + colH + 0.8 + 5, 0, radius * 2, 10, radius * 2, "concrete_shell"));

        for (int i = 0; i < columns; i += 2)
        {
            double a1 = 2 * Math.PI * i / columns, a2 = 2 * Math.PI * (i + 1) / columns;
            var cw = WallElement(pavilion, 0, $"Витраж {i / 2 + 1}",
                Math.Cos(a1) * radius, Math.Sin(a1) * radius, Math.Cos(a2) * radius, Math.Sin(a2) * radius,
                0.8, colH, 0.12, "glass");
            cw.Type = ElementTypes.CurtainWall;
            list.Add(cw);
            if (i == 4) Opening(list, cw, ElementTypes.Door, "Главный вход", 0, 2.4, 2.8, 0);
        }

        var groups = new List<BuildingGroup> { new(pavilion, "pavilion", "#F59E0B") };
        return (groups, list);
    }

    // ───────────────────────── Строительные примитивы ─────────────────────────

    private static ArchitecturalElement Box(string group, string type, string name, int level,
        double x, double y, double z, double w, double h, double d, string material) => new()
    {
        Name = name,
        Type = type,
        BuildingGroupId = group,
        LevelIndex = level,
        Position = [x, y, z],
        Size = [w, h, d],
        Rotation = [0, 0, 0],
        Material = material
    };

    /// <summary>Стена между точками (x1, z1)–(x2, z2) на отметке baseY: локальная ось X направлена вдоль стены.</summary>
    private static ArchitecturalElement WallElement(string group, int level, string name,
        double x1, double z1, double x2, double z2, double baseY, double height, double thickness, string material = "brick")
    {
        double dx = x2 - x1, dz = z2 - z1;
        double length = Math.Sqrt(dx * dx + dz * dz);
        double rot = Math.Atan2(-dz, dx) * 180 / Math.PI;
        var wall = Box(group, ElementTypes.Wall, name, level, (x1 + x2) / 2, baseY + height / 2, (z1 + z2) / 2, length, height, thickness, material);
        wall.SetRotationY(rot);
        return wall;
    }

    private static ArchitecturalElement Wall(List<ArchitecturalElement> list, string group, int level, string name,
        double x1, double z1, double x2, double z2, double baseY, double height, double thickness, string material = "brick")
    {
        var wall = WallElement(group, level, name, x1, z1, x2, z2, baseY, height, thickness, material);
        list.Add(wall);
        return wall;
    }

    /// <summary>Окно/дверь в стене: offset — смещение вдоль стены от её центра, sill — высота подоконника.</summary>
    private static void Opening(List<ArchitecturalElement> list, ArchitecturalElement host, string type, string name,
        double offset, double width, double height, double sill)
    {
        var (ox, oz) = ArchitecturalElement.RotateY(offset, 0, host.RotationY);
        var opening = Box(host.BuildingGroupId, type, name, host.LevelIndex,
            host.X + ox, host.BaseY + sill + height / 2, host.Z + oz,
            width, height, host.Depth + 0.06, type == ElementTypes.Door ? "wood" : "glass");
        opening.SetRotationY(host.RotationY);
        opening.HostElementId = host.Id;
        list.Add(opening);
    }

    /// <summary>Отверстие в плите: центр (x, z) в мировых координатах, размеры w × d в плане, сквозь всю толщину плиты.</summary>
    private static ArchitecturalElement SlabOpening(ArchitecturalElement slab, string name, double x, double z, double w, double d)
    {
        var hole = Box(slab.BuildingGroupId, ElementTypes.SlabOpening, name, slab.LevelIndex, x, slab.Y, z, w, slab.Height + 0.06, d, "void");
        hole.HostElementId = slab.Id;
        return hole;
    }

    private static void Parametric(ArchitecturalElement e, int repeat, double levelHeight, double twist, double taper)
    {
        e.RepeatLevels = repeat;
        e.LevelHeight = levelHeight;
        e.TwistAnglePerFloor = twist;
        e.TaperScalePerFloor = taper;
    }

    private static List<Point2D> RegularPolygon(int sides, double radius)
    {
        var points = new List<Point2D>(sides);
        for (int i = 0; i < sides; i++)
        {
            double a = 2 * Math.PI * i / sides + Math.PI / sides;
            points.Add(new Point2D(Math.Round(Math.Cos(a) * radius, 4), Math.Round(Math.Sin(a) * radius, 4)));
        }
        return points;
    }
}
