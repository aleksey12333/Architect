using System.Globalization;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services.Photo;

/// <summary>Ручная доводка подъёма по фото (ползунки студии): профиль, этажность, кривизна, доля остекления, покрытие.</summary>
public sealed record PhotoLiftOptions
{
    public PhotoMorphology Profile { get; init; } = PhotoMorphology.Auto;
    public int? Floors { get; init; }

    /// <summary>Кривизна фасада 0…1 (доля от наибольшей стрелы 0,3 хорды); null — по снимку.</summary>
    public double? Curvature { get; init; }

    /// <summary>Доля остекления 0,1…0,95; null — по снимку.</summary>
    public double? Glazing { get; init; }

    public PhotoRoofKind? Roof { get; init; }
    public string GroupName { get; init; } = "По фото";
}

/// <summary>Собранная по снимку BIM-модель (без изменения сцены): элементы, постройки, морфология, сводка и журнал сборки.</summary>
public sealed class PhotoBimModel
{
    public List<ArchitecturalElement> Elements { get; } = [];
    public List<BuildingGroup> Groups { get; } = [];
    public List<StructuralGridAxis> Axes { get; } = [];
    public PhotoMorphology Morphology { get; set; }
    public int Floors { get; set; }
    public double HeightM { get; set; }
    public double FootprintW { get; set; }
    public double FootprintD { get; set; }
    public List<string> Log { get; } = [];

    public PhotoBimModel CloneModel()
    {
        var c = new PhotoBimModel { Morphology = Morphology, Floors = Floors, HeightM = HeightM, FootprintW = FootprintW, FootprintD = FootprintD };
        c.Elements.AddRange(Elements.Select(e => e.Clone(keepId: true)));
        c.Groups.AddRange(Groups.Select(g => g.Clone()));
        c.Axes.AddRange(Axes.Select(a => a.Clone()));
        c.Log.AddRange(Log);
        return c;
    }
}

/// <summary>
/// Визуальное ядро, этап синтеза: оптический паспорт снимка → BIM-модель с физически корректным несущим каркасом внутри оболочки
/// в цветах фотографии. Для каждой морфологии план задаётся контуром из прямых и дуг (<see cref="ContourPath"/>): перекрытия — точно
/// по контуру (дуги дискретизируются, углы скругляются), фасад — стены и витражи по участкам контура (дуга — «Стрела дуги», углы
/// под произвольным углом — биссектрисная стыковка), колонны — по контуру с отступом внутрь (радиальная расстановка у дуг) и сеткой
/// внутри, ядро жёсткости у высоких зданий, фундаментная плита по контуру + 1 м. Сечения колонн подбираются по грузовой площади
/// и числу этажей выше (N ≈ 13 кПа на этаж), окна и двери ставятся с проверкой простенков (<see cref="BuildingLogicGuard"/>).
/// </summary>
public static class PhotoBimSynthesizer
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public const double SlabThickness = 0.22;
    public const double WallThickness = 0.3;

    public static PhotoMorphology Resolve(PhotoArchitectureDescriptor d, PhotoLiftOptions o) =>
        o.Profile == PhotoMorphology.Auto ? d.Morphology : o.Profile;

    public static PhotoBimModel Build(PhotoArchitectureDescriptor d, PhotoLiftOptions? options = null)
    {
        options ??= new PhotoLiftOptions();
        var morphology = Resolve(d, options);
        var b = new Builder(d, options, morphology);
        switch (morphology)
        {
            case PhotoMorphology.Curved: b.CurvedTower(); break;
            case PhotoMorphology.Cascading: b.Cascade(); break;
            case PhotoMorphology.Triangular: b.Flatiron(); break;
            case PhotoMorphology.Rotunda: b.Rotunda(); break;
            case PhotoMorphology.ArchSpan: b.ArchHall(); break;
            case PhotoMorphology.Pitched: b.PitchedHouse(); break;
            case PhotoMorphology.Rounded: b.RoundedTower(); break;
            default: b.BoxTower(); break;
        }
        return b.Finish();
    }

    // ═════════════════════════════ Сборщик ═════════════════════════════

    private sealed class Builder(PhotoArchitectureDescriptor d, PhotoLiftOptions o, PhotoMorphology morphology)
    {
        private readonly PhotoBimModel _m = new() { Morphology = morphology };
        private readonly string _group = o.GroupName;
        private double _maxTop;
        private double _footW, _footD;

        /// <summary>Центр сквозного ядра, общего для нескольких вызовов Storeys (стилобат + башня); колонны в зоне ядра не ставятся.</summary>
        private (double X, double Z)? CoreCenter;

        /// <summary>Колонны, заменённые стенами (поперечные стены секций каскада).</summary>
        private Func<double, double, bool>? SkipColumn;

        private int Floors(int fallback, int min, int max) => Math.Clamp(o.Floors ?? (d.Floors > 0 ? d.Floors : fallback), min, max);
        private double Glazing => Math.Clamp(o.Glazing ?? d.GlazingRatio, 0.1, 0.95);
        private double FloorH => Math.Clamp(d.FloorHeight > 0 ? d.FloorHeight : 3.3, 2.8, 6);

        // ───────────────────────────── Примитивы ─────────────────────────────

        private ArchitecturalElement Add(ArchitecturalElement e, string? hex)
        {
            e.BuildingGroupId = _group;
            if (hex is not null) CurvedDesign.SetFacadeColor(e, hex);
            _m.Elements.Add(e);
            _maxTop = Math.Max(_maxTop, e.TopY);
            return e;
        }

        private ArchitecturalElement Plate(string type, string name, IReadOnlyList<(double X, double Z)> world, double thickness, double topY, int level, string? hex,
            string material = "concrete", string? roofShape = null)
        {
            var (minX, maxX, minZ, maxZ) = Geometry2D.Bounds(world);
            double cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
            var e = new ArchitecturalElement { Type = type, Name = name, Material = material, LevelIndex = level };
            if (roofShape is not null) e.RoofShape = roofShape;
            e.SetSize(Math.Max(0.1, maxX - minX), thickness, Math.Max(0.1, maxZ - minZ));
            e.SetPosition(cx, topY - thickness / 2, cz);
            e.Polygon2D = world.Select(p => new Point2D(p.X - cx, p.Z - cz)).ToList();
            return Add(e, hex);
        }

        private ArchitecturalElement Column(double x, double z, double baseY, double h, double size, int level, string name, string? hex = null, bool round = false)
        {
            var e = new ArchitecturalElement { Type = ElementTypes.Column, Name = name, Material = "concrete", LevelIndex = level };
            if (round) e.ProfileType = ProfileTypes.Circle;
            e.SetSize(size, h, size);
            e.SetPosition(x, baseY + h / 2, z);
            return Add(e, hex ?? d.AccentHex);
        }

        private ArchitecturalElement Box(string type, string name, double x, double baseY, double z, double w, double h, double dd, double rot, int level, string? hex,
            string material = "concrete")
        {
            var e = new ArchitecturalElement { Type = type, Name = name, Material = material, LevelIndex = level };
            e.SetSize(w, h, dd);
            e.SetPosition(x, baseY + h / 2, z);
            e.SetRotationY(rot);
            return Add(e, hex);
        }

        /// <summary>Сечение колонны по грузовой площади и числу этажей выше: A = N / (0,75·Rb), Rb = 14,5 МПа, шаг 0,1 м, 0,4…1,2 м.</summary>
        private static double ColumnSize(double tributary, int floorsAbove)
        {
            double n = tributary * floorsAbove * 13.0; // кН
            double area = n / (0.75 * 14500);
            return Math.Clamp(Math.Ceiling(Math.Sqrt(area) * 10) / 10, 0.4, 1.2);
        }

        /// <summary>Фундаментная плита по контуру с выносом 1 м, верх на отметке 0.</summary>
        private void Foundation(IReadOnlyList<(double X, double Z)> contour, double thickness)
        {
            var path = ContourPath.Polygon(Offset(contour, 1.0));
            Plate(ElementTypes.Foundation, "Фундаментная плита", ContourPath.Sample(path), thickness, 0, 0, null);
        }

        /// <summary>Колонны по контуру с отступом внутрь (дуга — радиально) с шагом ≤ step и сеткой внутри (отступ ≥ clearance от контура).</summary>
        private static List<(double X, double Z)> ColumnLayout(IReadOnlyList<PathSegment> path, double inset, double step, double clearance, double gridStep)
        {
            var pts = new List<(double X, double Z)>();
            foreach (var s in path)
            {
                double len = s.IsArc ? new ArcGeometry(s.Chord, s.Bulge).Length : s.Chord;
                if (len < 1.5) continue;
                int n = Math.Max(1, (int)Math.Ceiling(len / step));
                for (int i = 0; i <= n; i++)
                {
                    double t = (double)i / n;
                    (double X, double Z) local;
                    if (s.IsArc)
                    {
                        var arc = new ArcGeometry(s.Chord, s.Bulge);
                        double phi = -arc.HalfAngle + 2 * arc.HalfAngle * t;
                        local = arc.Point(phi, -arc.Sign * inset);
                    }
                    else local = ((t - 0.5) * s.Chord, -inset);
                    var p = ContourPath.ToWorld(s, local);
                    // У острых углов отступ от одной стороны выводит точку за другую — такие колонны не ставятся.
                    var ring = ContourPath.Sample(path);
                    if (!Geometry2D.Contains(ring, p.X, p.Z) || Geometry2D.DistanceToBoundary(ring, p.X, p.Z) < inset * 0.7) continue;
                    if (pts.All(q => Dist(q, p) > step * 0.45)) pts.Add(p);
                }
            }
            var poly = ContourPath.Sample(path);
            var (minX, maxX, minZ, maxZ) = Geometry2D.Bounds(poly);
            int nx = Math.Max(1, (int)Math.Round((maxX - minX) / gridStep)), nz = Math.Max(1, (int)Math.Round((maxZ - minZ) / gridStep));
            double sx = (maxX - minX) / nx, sz = (maxZ - minZ) / nz;
            for (int i = 1; i < nx; i++)
                for (int j = 1; j < nz; j++)
                {
                    var p = (minX + sx * i, minZ + sz * j);
                    if (!Geometry2D.Contains(poly, p.Item1, p.Item2) || Geometry2D.DistanceToBoundary(poly, p.Item1, p.Item2) < clearance) continue;
                    if (pts.All(q => Dist(q, p) > gridStep * 0.55)) pts.Add(p);
                }
            return pts;
        }

        /// <summary>Фасад по участку контура: стена (с окнами по ритму) или витраж; дуга — с «Стрелой дуги».</summary>
        private ArchitecturalElement Facade(PathSegment s, bool glass, double baseY, double h, int level, string name, string? wallHex, double windowBay = 3.0,
            double windowW = 1.5, double windowH = 1.8, double sill = 0.9, double thickness = WallThickness)
        {
            var e = ContourPath.Element(s, glass ? ElementTypes.CurtainWall : ElementTypes.Wall, name, baseY, h, glass ? 0.12 : thickness, _group, level,
                glass ? "glass" : "concrete");
            Add(e, glass ? d.GlassHex : wallHex);
            if (glass || windowBay <= 0) return e;
            double len = e.Width;
            // Доля остекления из студии: ширина окна в шаге ритма.
            if (o.Glazing is { } gz) windowW = Math.Clamp(windowBay * (0.2 + gz * 0.75), 0.6, windowBay - 0.35);
            int n = (int)Math.Floor((len - 0.8) / windowBay);
            for (int i = 0; i < n; i++)
            {
                double lx = -len / 2 + (len - n * windowBay) / 2 + windowBay * (i + 0.5);
                var (wx, wz) = ArchitecturalElement.RotateY(lx, BuildingLogicGuard.ArcZ(e, lx), e.RotationY);
                var win = new ArchitecturalElement { Type = ElementTypes.Window, Name = $"{name} · окно {i + 1}", Material = "glass", LevelIndex = level, HostElementId = e.Id };
                win.SetSize(windowW, Math.Min(windowH, h - sill - 0.25), thickness + ParametricSolverService.OpeningClearance);
                win.SetPosition(e.X + wx, baseY + sill + win.Height / 2, e.Z + wz);
                win.SetRotationY(e.RotationY);
                Add(win, d.GlassHex);
            }
            return e;
        }

        private void Door(ArchitecturalElement wall, double lx, string name)
        {
            var (wx, wz) = ArchitecturalElement.RotateY(lx, BuildingLogicGuard.ArcZ(wall, lx), wall.RotationY);
            var door = new ArchitecturalElement { Type = ElementTypes.Door, Name = name, Material = "wood", LevelIndex = wall.LevelIndex, HostElementId = wall.Id };
            door.SetSize(1.8, 2.4, wall.Depth + ParametricSolverService.OpeningClearance);
            door.SetPosition(wall.X + wx, wall.BaseY + 1.2, wall.Z + wz);
            door.SetRotationY(wall.RotationY);
            Add(door, d.FrameHex);
        }

        /// <summary>Этажи каркаса по контуру: колонны (сечение по нагрузке), фасад по участкам, перекрытие по контуру; последний — покрытие.</summary>
        private void Storeys(IReadOnlyList<PathSegment> path, int firstLevel, int floors, double baseY, double floorH, List<(double X, double Z)> columns, double tributary,
            bool glass, string label, string? wallHex, string? slabHex, int floorsAboveExtra = 0, bool coreFromFloors = true, bool roofParapet = false, string? roofHex = null)
        {
            var poly = ContourPath.Sample(path);
            double y = baseY;
            bool core = coreFromFloors && floors + firstLevel >= 9 && Geometry2D.Area(poly) > 400;
            var coreAt = CoreCenter ?? Geometry2D.Centroid(poly);
            var (minX, maxX, minZ, maxZ) = Geometry2D.Bounds(poly);
            var centroid = Geometry2D.Centroid(poly);
            for (int i = 0; i < floors; i++)
            {
                int level = firstLevel + i;
                bool last = i == floors - 1;
                double h = floorH - SlabThickness;
                int above = floors - i + floorsAboveExtra;
                double size = ColumnSize(tributary, above);
                int k = 0;
                foreach (var (x, z) in columns)
                {
                    if ((core || CoreCenter is not null) && Math.Abs(x - coreAt.X) < 4.5 && Math.Abs(z - coreAt.Z) < 4.5) continue;
                    if (SkipColumn?.Invoke(x, z) == true) continue;
                    Column(x, z, y, h, size, level, $"{label} · К{++k} · эт. {level + 1}");
                }
                // Сквозное ядро жёсткости (как в пресете башни): одно тело от низа первого этажа до верха последнего.
                if (core && i == 0) Box(ElementTypes.Core, $"{label} · ядро жёсткости", coreAt.X, y, coreAt.Z, 7.2, floors * floorH, 7.2, 0, level, d.AccentHex);
                int f = 0;
                foreach (var s in path)
                {
                    if (s.Chord < 0.4) continue;
                    var wall = Facade(s, glass, y, h, level, $"{label} · фасад {++f} · эт. {level + 1}", wallHex);
                    if (!glass && level == 0 && f == 1 && wall.Width > 6) Door(wall, 0, $"{label} · вход");
                }
                double top = y + floorH;
                if (last)
                {
                    bool parapet = o.Roof switch { PhotoRoofKind.Parapet => true, PhotoRoofKind.Flat => false, _ => roofParapet };
                    Plate(ElementTypes.Roof, $"{label} · покрытие", poly, SlabThickness + 0.08, top, level, roofHex ?? slabHex, roofShape: parapet ? RoofShapes.FlatParapet : RoofShapes.Flat);
                    if (parapet && !roofParapet) Models.RoofDesign.Write(_m.Elements[^1], Models.RoofDesign.DefaultSlabThickness, 0, 1.1);
                    if (o.Roof == PhotoRoofKind.Dome)
                    {
                        // Купол над покрытием по вписанному в контур кругу.
                        var c = Geometry2D.Centroid(poly);
                        double r = Math.Max(3, poly.Min(p => Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Z - c.Z) * (p.Z - c.Z))) - 0.5);
                        var dome = new ArchitecturalElement { Type = ElementTypes.Shell, Name = $"{label} · купол", Material = "concrete", LevelIndex = level + 1 };
                        dome.SetSize(2 * r, r * 0.7, 2 * r);
                        dome.SetPosition(c.X, top + 0.3 + r * 0.35, c.Z);
                        Add(dome, d.RoofHex);
                    }
                }
                else Plate(ElementTypes.Slab, $"{label} · перекрытие {level + 1}", poly, SlabThickness, top, level, slabHex);
                if (last && roofParapet) Models.RoofDesign.Write(_m.Elements[^1], Models.RoofDesign.DefaultSlabThickness, 0, 1.1);
                y = top;
            }
            _footW = Math.Max(_footW, maxX - minX);
            _footD = Math.Max(_footD, maxZ - minZ);
        }

        // ───────────────────────────── 1. Изогнутая башня со стилобатом ─────────────────────────────

        public void CurvedTower()
        {
            int floors = Floors(20, 4, 50);
            int podium = Math.Clamp(d.PodiumFloors > 0 ? d.PodiumFloors : 2, 1, 4);
            if (floors <= podium + 2) podium = 0;
            double L = Math.Clamp(d.WidthM, 24, 60), B = Math.Clamp(Math.Min(d.DepthM, L * 0.7), 16, 30);
            double bulge = Math.Clamp(o.Curvature is { } c ? c * 0.3 : Math.Max(0.15, d.ArcBulgeRatio), 0, 0.3) * L;
            double r = Math.Clamp(d.CornerRadiusRatio * B, 2.5, 6);
            var tower = CurvedPlan(L, B, bulge, r);
            _m.Log.Add(string.Create(Ru, $"План башни: хорда {L:0.#} м, глубина {B:0.#} м, стрела дуги фасада {bulge:0.##} м, скругление {r:0.#} м"));
            var towerCols = ColumnLayout(tower, 1.1, 6.6, 3.2, 7.2);
            if (floors >= 9) CoreCenter = Geometry2D.Centroid(ContourPath.Sample(tower));

            List<PathSegment>? podiumPath = null;
            if (podium > 0)
            {
                // Стилобат охватывает башню с запасом ≥ 4 м за вершиной дуги фасада (витраж и колонны дуги стоят на его покрытии).
                double podiumD = B + 2 * Math.Max(6, bulge + 4);
                podiumPath = ContourPath.RoundedRect(L + 14, podiumD, 5);
                var podiumCols = towerCols.ToList();
                foreach (var p in ColumnLayout(podiumPath, 1.1, 7.2, 2.4, 7.2))
                    if (!Geometry2D.Contains(ContourPath.Sample(tower), p.X, p.Z) && podiumCols.All(q => Dist(q, p) > 3.5)) podiumCols.Add(p);
                Foundation(ContourPath.Sample(podiumPath), 0.9);
                Storeys(podiumPath, 0, podium, 0, 4.2, podiumCols, 6.6 * 7.2, glass: false, "Стилобат", d.AccentHex, d.AccentHex, floorsAboveExtra: floors - podium, coreFromFloors: false);
                _m.Log.Add($"Стилобат: {podium} эт., контур {L + 14:0} × {podiumD:0} м со скруглением 5 м");
            }
            else Foundation(ContourPath.Sample(tower), 0.9);
            double towerBase = podium * 4.2;
            Storeys(tower, podium, floors - podium, towerBase, FloorH, towerCols, 6.6 * 7.2, glass: true, "Башня", d.WallHex, d.AccentHex, coreFromFloors: false, roofHex: d.RoofHex);
            // Сквозное ядро жёсткости от фундамента до покрытия башни.
            if (CoreCenter is { } cc)
                Box(ElementTypes.Core, "Башня · ядро жёсткости", cc.X, 0, cc.Z, 7.2, towerBase + (floors - podium) * FloorH, 7.2, 0, 0, d.AccentHex);
        }

        private void RemovePodiumColumnsInCore((double X, double Z) c) =>
            _m.Elements.RemoveAll(e => e.Category == BimCategory.Column && e.Name.StartsWith("Стилобат", StringComparison.Ordinal) && Math.Abs(e.X - c.X) < 4.5 && Math.Abs(e.Z - c.Z) < 4.5);

        /// <summary>План с дуговым фасадом на стороне −Z (к зрителю) и скруглёнными задними углами.</summary>
        private static List<PathSegment> CurvedPlan(double L, double B, double bulge, double r)
        {
            double x = L / 2, z = B / 2;
            // Обход против часовой: −Z (фронт, дуга наружу) → +X → +Z (тыл) → −X.
            var path = new List<PathSegment> { new(-x, -z, x, -z, bulge), new(x, -z, x, z - r, 0) };
            var corner1 = Corner((x, z - r), (x, z), (x - r, z), r);
            path.Add(corner1);
            path.Add(new PathSegment(x - r, z, -x + r, z, 0));
            path.Add(Corner((-x + r, z), (-x, z), (-x, z - r), r));
            path.Add(new PathSegment(-x, z - r, -x, -z, 0));
            return path;
        }

        /// <summary>Дуга скругления прямого угла: от a через вершину v к b, радиус r (стрела r(1 − cos 45°)).</summary>
        private static PathSegment Corner((double X, double Z) a, (double X, double Z) v, (double X, double Z) b, double r) =>
            new(a.X, a.Z, b.X, b.Z, r * (1 - Math.Cos(Math.PI / 4)));

        // ───────────────────────────── 2. Каскадный комплекс ─────────────────────────────

        public void Cascade()
        {
            int top = Floors(14, 6, 30);
            int bays = Math.Clamp(d.Tiers.Count >= 3 ? d.Tiers.Count : 5, 3, 7);
            double bayW = Math.Clamp(d.WidthM / bays, 9.6, 14.4);
            double D = Math.Clamp(d.DepthM, 14, 22);
            // Этажность секций по ярусам снимка (слева направо), иначе — ступенчато вниз.
            var floorsBy = new int[bays];
            var tierLevels = d.Tiers.Select(t => t.TopFraction).ToList();
            double maxLevel = tierLevels.Count > 0 ? tierLevels.Max() : 1;
            for (int i = 0; i < bays; i++)
            {
                // Правая система координат: при взгляде с фронта (−Z) левый край снимка — это +X, ярусы снимка идут справа налево по X.
                double frac = tierLevels.Count == bays ? tierLevels[bays - 1 - i] / maxLevel : 1 - (double)i / bays * 0.62;
                floorsBy[i] = Math.Clamp((int)Math.Round(top * frac), 3, top);
            }
            // Гарантия ≥ 3 различных ярусов (уступы минимум по 2 этажа).
            if (floorsBy.Distinct().Count() < 3)
                for (int i = 0; i < bays; i++) floorsBy[i] = Math.Max(3, top - 2 * (int)Math.Round((double)i * 3 / Math.Max(1, bays - 1)));
            _m.Log.Add($"Каскад: {bays} секций по {bayW:0.#} м, этажность " + string.Join(" / ", floorsBy));
            double L = bayW * bays, x0 = -L / 2;
            Foundation([(x0, -D / 2), (x0 + L, -D / 2), (x0 + L, D / 2), (x0, D / 2)], 0.8);
            double fh = Math.Clamp(FloorH, 2.9, 3.4);
            int maxFloors = floorsBy.Max();
            for (int level = 0; level < maxFloors; level++)
            {
                var active = Enumerable.Range(0, bays).Where(i => floorsBy[i] > level).ToList();
                if (active.Count == 0) break;
                double baseY = level * fh, h = fh - SlabThickness;
                double ax = x0 + active.Min() * bayW, bx = x0 + (active.Max() + 1) * bayW;
                // Колонны по осям секций (шаг bayW/2) в средней линии и у фасадов с отступом.
                int abovePeak = active.Max(i => floorsBy[i]) - level;
                double size = ColumnSize(bayW / 2 * D / 2, abovePeak);
                int k = 0;
                // Колонны — по середине секций; границы секций — несущие поперечные стены на всю высоту (стена уступа всегда стоит на стене).
                for (int i = active.Min(); i <= active.Max(); i++)
                    foreach (double z in new[] { -D / 2 + 1.0, 0, D / 2 - 1.0 })
                        Column(x0 + (i + 0.5) * bayW, z, baseY, h, size, level, $"Каскад · К{++k} · эт. {level + 1}", d.AccentHex);
                for (int i = active.Min() + 1; i <= active.Max(); i++)
                    Box(ElementTypes.Wall, $"Каскад · поперечная стена секций {i} · эт. {level + 1}", x0 + i * bayW, baseY, 0, D - 0.3, h, 0.25, -90, level, d.AccentHex);
                var rect = Rectangle(ax, bx, -D / 2, D / 2);
                int f = 0;
                foreach (var s in rect)
                {
                    var wall = Facade(s, false, baseY, h, level, $"Каскад · фасад {++f} · эт. {level + 1}", level % 3 == 2 ? d.AccentHex : d.WallHex, 3.0, 1.6, 1.7, 0.9);
                    if (level == 0 && f == 1) Door(wall, 0, "Каскад · вход");
                }
                double topY = baseY + fh;
                // Перекрытие над секциями, где выше есть этаж; секции без верхних этажей — эксплуатируемая терраса с парапетом.
                var continuing = active.Where(i => floorsBy[i] > level + 1).ToList();
                if (continuing.Count > 0)
                    Plate(ElementTypes.Slab, $"Каскад · перекрытие {level + 1}", Rect(x0 + continuing.Min() * bayW, x0 + (continuing.Max() + 1) * bayW, -D / 2, D / 2), SlabThickness, topY, level, d.AccentHex);
                foreach (int i in active.Where(i => floorsBy[i] == level + 1))
                {
                    var roof = Plate(ElementTypes.Roof, $"Каскад · терраса секции {i + 1}", Rect(x0 + i * bayW, x0 + (i + 1) * bayW, -D / 2, D / 2), SlabThickness + 0.08, topY, level,
                        d.RoofHex, roofShape: RoofShapes.FlatParapet);
                    Models.RoofDesign.Write(roof, Models.RoofDesign.DefaultSlabThickness, 0, 1.1);
                    // Озеленение террасы.
                    if (i != Array.IndexOf(floorsBy, maxFloors))
                        Box(ElementTypes.Asset, $"Каскад · озеленение террасы {i + 1}", x0 + (i + 0.5) * bayW, topY + 0.08, 0, bayW * 0.55, 0.6, D * 0.35, 0, level, "#6E8F4E", "planter");
                }
            }
            _footW = L;
            _footD = D;
        }

        private static List<PathSegment> Rectangle(double ax, double bx, double az, double bz) =>
            ContourPath.Polygon([(ax, az), (bx, az), (bx, bz), (ax, bz)]);

        private static List<(double X, double Z)> Rect(double ax, double bx, double az, double bz) => [(ax, az), (bx, az), (bx, bz), (ax, bz)];

        // ───────────────────────────── 3. Ротонда с куполом и колоннадой ─────────────────────────────

        public void Rotunda()
        {
            int storeys = Math.Clamp(o.Floors ?? 2, 1, 4);
            double R = Math.Clamp(d.DomeWidthRatio * d.WidthM / 2, 8, 13);
            double fh = 5.0, drumH = storeys * fh;
            var drum = ContourPath.Circle(R, 8, 0, 0, Math.PI / 8);
            var drumPoly = ContourPath.Sample(drum);
            _m.Log.Add(string.Create(Ru, $"Ротонда: барабан R = {R:0.#} м, {storeys} эт. по {fh:0.#} м, купол, портик и два крыла под углом"));
            // Фундамент: барабан + портик + крылья.
            double wingL = Math.Clamp(R * 1.7, 14, 30), wingD = Math.Clamp(R * 1.1, 10, 16);
            double wingAngle = 22;
            var wingCenters = new List<(double X, double Z, double Rot)>();
            foreach (int side in new[] { -1, 1 })
            {
                double ang = wingAngle * Math.PI / 180;
                double cx = side * (R + wingL / 2 - 0.8) * Math.Cos(ang), cz = (R + wingL / 2 - 0.8) * Math.Sin(ang) * 1.0;
                wingCenters.Add((cx, cz, side * -wingAngle));
            }
            var all = new List<(double X, double Z)>(drumPoly);
            foreach (var (cx, cz, rot) in wingCenters) all.AddRange(Corners(cx, cz, wingL + 1, wingD + 1, rot));
            all.AddRange(Rect(-R * 0.7, R * 0.7, -R - 7.5, -R + 1));
            Foundation(Geometry2D.ConvexHull(all), 0.8);

            // Барабан: 8 дуговых стен на этаж, кольцо колонн внутри, круглое перекрытие.
            var ring = Enumerable.Range(0, 8).Select(i => 2 * Math.PI * i / 8).Select(a => (R * 0.52 * Math.Cos(a), R * 0.52 * Math.Sin(a))).ToList();
            ring.Add((0.0, 0.0));
            for (int i = 0; i < storeys; i++)
            {
                double y = i * fh, h = fh - SlabThickness;
                int k = 0;
                foreach (var (x, z) in ring) Column(x, z, y, h, ColumnSize(R * R * 0.4, storeys - i + 1), i, $"Ротонда · К{++k} · эт. {i + 1}", d.AccentHex, round: true);
                int f = 0;
                foreach (var s in drum) Facade(s, false, y, h, i, $"Ротонда · барабан {++f} · эт. {i + 1}", d.WallHex, 4.0, 1.6, 3.0, 0.9, 0.6);
                Plate(ElementTypes.Slab, $"Ротонда · перекрытие {i + 1}", drumPoly, SlabThickness + 0.03, y + fh, i, d.AccentHex);
            }
            // Купол — оболочка по кольцу барабана.
            double domeH = Math.Clamp(d.DomeHeightRatio * d.HeightM, R * 0.55, R * 0.95);
            var dome = new ArchitecturalElement { Type = ElementTypes.Shell, Name = "Ротонда · купол", Material = "concrete", LevelIndex = storeys };
            dome.SetSize(2 * R + 0.6, domeH, 2 * R + 0.6);
            dome.SetPosition(0, drumH + domeH / 2, 0);
            Add(dome, d.RoofHex);

            // Портик: колоннада на стилобате, антаблемент и фронтон.
            int cols = Math.Clamp(d.ColonnadeColumns > 0 ? d.ColonnadeColumns : 6, 4, 10);
            double porticoW = Math.Max(R * 1.2, cols * 2.4), zc = -R - 4.5, colH = fh * Math.Max(1, storeys) - 1.6;
            // Стилобат портика — цоколь на фундаментной плите (объём благоустройства, нагрузку передаёт на плиту).
            var stylobate = Plate(ElementTypes.Asset, "Портик · стилобат", Rect(-porticoW / 2 - 1, porticoW / 2 + 1, zc - 2.6, -R + 0.8), 0.6, 0.6, 0, d.AccentHex, "stone");
            double colBase = stylobate.TopY;
            for (int i = 0; i < cols; i++)
            {
                double x = -porticoW / 2 + porticoW * (i + 0.5) / cols;
                Column(x, zc - 1.2, colBase, colH, 1.0, 0, $"Портик · колонна {i + 1}", d.AccentHex, round: true);
            }
            Box(ElementTypes.Beam, "Портик · антаблемент", 0, colBase + colH, zc - 1.2, porticoW + 1.2, 1.2, 1.2, 0, 0, d.AccentHex);
            var pediment = Plate(ElementTypes.Roof, "Портик · фронтон", Rect(-porticoW / 2 - 0.6, porticoW / 2 + 0.6, zc - 2.0, -R + 0.6), 2.6, colBase + colH + 1.2 + 2.6, 0,
                d.RoofHex, roofShape: RoofShapes.Gable);
            pediment.Polygon2D = [];
            pediment.SetSize(porticoW + 1.2, 2.6, Math.Abs(-R + 0.6 - (zc - 2.0)));

            // Крылья под углом: несущие стены по периметру с окнами, средний ряд колонн, перекрытия, плоская кровля с парапетом.
            int wi = 0;
            foreach (var (cx, cz, rot) in wingCenters)
            {
                wi++;
                var corners = Corners(cx, cz, wingL, wingD, rot);
                var path = ContourPath.Polygon(corners);
                var cols2 = new List<(double X, double Z)>();
                for (int k = 1; k < 4; k++)
                {
                    var (lx, lz) = ArchitecturalElement.RotateY(-wingL / 2 + wingL * k / 4, 0, rot);
                    cols2.Add((cx + lx, cz + lz));
                }
                Storeys(path, 0, storeys, 0, fh, cols2, wingL / 4 * wingD / 2, false, $"Крыло {wi}", d.WallHex, d.AccentHex, coreFromFloors: false, roofParapet: true, roofHex: d.AccentHex);
            }
            _footW = 2 * (R + wingL);
            _footD = 2 * R + 8;
        }

        private static List<(double X, double Z)> Corners(double cx, double cz, double w, double dd, double rot) =>
            new[] { (-w / 2, -dd / 2), (w / 2, -dd / 2), (w / 2, dd / 2), (-w / 2, dd / 2) }.Select(p =>
            {
                var (x, z) = ArchitecturalElement.RotateY(p.Item1, p.Item2, rot);
                return (cx + x, cz + z);
            }).ToList();

        // ───────────────────────────── 4. Треугольный корпус на стрелке ─────────────────────────────

        public void Flatiron()
        {
            int floors = Floors(16, 4, 40);
            double theta = Math.Clamp(d.CornerAngleDeg > 0 ? d.CornerAngleDeg : 50, 30, 75);
            double Lt = Math.Clamp(d.WidthM * 1.05, 36, 66);
            double Bw = 2 * Lt * Math.Tan(theta / 2 * Math.PI / 180);
            double r = Math.Clamp(d.CornerRadiusRatio * Bw, 1.5, 4.0);
            // Стрелка на −Z (к зрителю), тыльная сторона на +Z; обход против часовой: стрелка → правый тыльный угол → левый.
            var prow = (X: 0.0, Z: -Lt * 2 / 3);
            var right = (X: Bw / 2, Z: Lt / 3);
            var left = (X: -Bw / 2, Z: Lt / 3);
            var path = TrianglePath(prow, right, left, r);
            _m.Log.Add(string.Create(Ru, $"Треугольный план: угол на стрелке {theta:0} °, длина {Lt:0.#} м, тыльный фасад {Bw:0.#} м, скругление стрелки R = {r:0.#} м"));
            var poly = ContourPath.Sample(path);
            Foundation(poly, 0.8);
            var columns = ColumnLayout(path, 1.6, 6.4, 3.0, 6.6);
            double fh = Math.Clamp(FloorH, 3.0, 3.8);
            bool core = floors >= 9;
            var cc = (X: 0.0, Z: Lt / 3 - 6.5);
            for (int i = 0; i < floors; i++)
            {
                double y = i * fh, h = fh - SlabThickness;
                int k = 0;
                double size = ColumnSize(6.4 * 6.6, floors - i);
                foreach (var (x, z) in columns)
                {
                    if (core && Math.Abs(x - cc.X) < 4.4 && Math.Abs(z - cc.Z) < 4.4) continue;
                    Column(x, z, y, h, size, i, $"Утюг · К{++k} · эт. {i + 1}");
                }
                if (core && i == 0) Box(ElementTypes.Core, "Утюг · ядро жёсткости", cc.X, 0, cc.Z, 7.2, floors * fh, 7.2, 0, 0, d.AccentHex);
                // Фасады: две длинные стены, тыльная и дуга стрелки; углы тыла — биссектрисная стыковка.
                var walls = new List<ArchitecturalElement>();
                int f = 0;
                foreach (var s in path)
                {
                    string hex = i < 2 ? d.AccentHex : d.WallHex;
                    var w = Facade(s, false, y, h, i, $"Утюг · фасад {++f} · эт. {i + 1}", hex, s.IsArc ? 1.6 : 2.2, 1.2, 1.9, 0.85, 0.45);
                    walls.Add(w);
                    if (i == 0 && s.IsArc) { }
                }
                var straight = walls.Where(w => !CurvedDesign.IsArc(w)).ToList();
                for (int a = 0; a < straight.Count; a++)
                    for (int bI = a + 1; bI < straight.Count; bI++)
                        ParametricSolverService.MiterJoin(straight[a], straight[bI]);
                if (i == 0) Door(straight.OrderByDescending(w => w.Width).First(), -6, "Утюг · вход");
                double top = y + fh;
                if (i == floors - 1)
                {
                    // Карниз: покрытие с выносом 0,8 м по контуру.
                    var cornice = Offset(poly, 0.8);
                    Plate(ElementTypes.Roof, "Утюг · покрытие и карниз", cornice, 0.45, top, i, d.FrameHex, roofShape: RoofShapes.Flat);
                }
                else Plate(ElementTypes.Slab, $"Утюг · перекрытие {i + 1}", poly, SlabThickness, top, i, d.AccentHex);
            }
            _footW = Bw;
            _footD = Lt;
        }

        /// <summary>Треугольник со скруглённой стрелкой: дуга, касательная к обеим сторонам, и прямые к тыльным углам.</summary>
        private static List<PathSegment> TrianglePath((double X, double Z) prow, (double X, double Z) right, (double X, double Z) left, double r)
        {
            var fillet = ContourPath.Fillet([prow, right, left], r);
            // Скругляется только стрелка: тыльные углы — острые (стыкуются биссектрисно).
            var result = new List<PathSegment>();
            foreach (var s in fillet)
            {
                if (!s.IsArc) { result.Add(s); continue; }
                double mx = (s.X1 + s.X2) / 2, mz = (s.Z1 + s.Z2) / 2;
                if (Math.Sqrt((mx - prow.X) * (mx - prow.X) + (mz - prow.Z) * (mz - prow.Z)) < r * 3) result.Add(s);
                else
                {
                    // Тыльный угол: дуга заменяется продолжением сторон до вершины.
                    var v = Math.Abs(mx - right.X) < Math.Abs(mx - left.X) ? right : left;
                    result.Add(new PathSegment(s.X1, s.Z1, v.X, v.Z, 0));
                    result.Add(new PathSegment(v.X, v.Z, s.X2, s.Z2, 0));
                }
            }
            // Слияние коллинеарных соседних прямых.
            var merged = new List<PathSegment>();
            foreach (var s in result)
            {
                if (merged.Count > 0 && !s.IsArc && !merged[^1].IsArc && Collinear(merged[^1], s))
                    merged[^1] = new PathSegment(merged[^1].X1, merged[^1].Z1, s.X2, s.Z2, 0);
                else merged.Add(s);
            }
            if (merged.Count > 1 && !merged[0].IsArc && !merged[^1].IsArc && Collinear(merged[^1], merged[0]))
            {
                merged[0] = new PathSegment(merged[^1].X1, merged[^1].Z1, merged[0].X2, merged[0].Z2, 0);
                merged.RemoveAt(merged.Count - 1);
            }
            return merged;
        }

        private static bool Collinear(PathSegment a, PathSegment b)
        {
            double ax = a.X2 - a.X1, az = a.Z2 - a.Z1, bx = b.X2 - b.X1, bz = b.Z2 - b.Z1;
            double cross = ax * bz - az * bx;
            return Math.Abs(cross) < 1e-6 * Math.Max(1, Math.Sqrt(ax * ax + az * az) * Math.Sqrt(bx * bx + bz * bz)) && ax * bx + az * bz > 0;
        }

        // ───────────────────────────── 5. Большепролётный арочный зал ─────────────────────────────

        public void ArchHall()
        {
            double span = Math.Clamp(d.WidthM * 0.8, 24, 42);
            double length = Math.Clamp(d.DepthM, 48, 96);
            length = Math.Round(length / 6) * 6;
            double wallH = 6.0;
            double rise = Math.Clamp(d.DomeHeightRatio * d.HeightM * 1.1, span * 0.22, span * 0.42);
            _m.Log.Add(string.Create(Ru, $"Зал: пролёт {span:0.#} м без промежуточных опор, длина {length:0} м, свод со стрелой {rise:0.#} м, фермы через 6 м, наклонные пилоны"));
            var plan = Rect(-span / 2, span / 2, -length / 2, length / 2);
            Foundation(plan, 0.8);
            // Продольные несущие стены зала с окнами и входами.
            var sides = new[] { new PathSegment(span / 2, -length / 2, span / 2, length / 2, 0), new PathSegment(-span / 2, length / 2, -span / 2, -length / 2, 0) };
            int si = 0;
            foreach (var s in sides)
            {
                var wall = Facade(s, false, 0, wallH, 0, $"Зал · продольная стена {++si}", d.AccentHex, 6.0, 2.4, 2.2, 2.6, 0.45);
                Door(wall, 3, $"Зал · вход {si}");
            }

            // Наклонные пилоны-контрфорсы снаружи продольных стен.
            int pi = 0;
            for (double z = -length / 2 + 3; z < length / 2 - 2.9; z += 6)
                foreach (int side in new[] { -1, 1 })
                {
                    var p = Column(side * (span / 2 + 1.4), z, 0, wallH + 0.6, 0.7, 0, $"Зал · наклонный пилон {++pi}", d.FrameHex);
                    p.Rotation = [0, 0, side * 14];
                    p.SetSize(0.7, wallH + 0.6, 0.9);
                }
            // Свод: полая сводчатая кровля над стенами.
            // Монолитная сводчатая оболочка 180 мм: распор воспринимают продольные стены и наклонные пилоны-контрфорсы.
            var vault = new ArchitecturalElement { Type = ElementTypes.Roof, Name = "Зал · свод покрытия", Material = "concrete", LevelIndex = 0, RoofShape = RoofShapes.BarrelVault };
            vault.SetSize(span + 0.9, rise, length + 0.6);
            vault.SetPosition(0, wallH + rise / 2, 0);
            Models.RoofDesign.Write(vault, 0.18, 0.4);
            Add(vault, d.RoofHex);
            // Панорамные витражные торцы с арочным верхом.
            foreach (int side in new[] { -1, 1 })
            {
                var glass = new ArchitecturalElement { Type = ElementTypes.CurtainWall, Name = side < 0 ? "Зал · панорамный витраж торца" : "Зал · витраж тыльного торца", Material = "glass", LevelIndex = 0 };
                glass.SetSize(span - 0.2, wallH + rise - 0.25, 0.15);
                glass.SetPosition(0, (wallH + rise - 0.25) / 2, side * length / 2);
                CurvedDesign.SetArchRise(glass, rise - 0.25);
                Add(glass, d.GlassHex);
            }
            _footW = span;
            _footD = length;
        }

        // ───────────────────────────── 6–8. Скатный дом, скруглённый и прямоугольный корпуса ─────────────────────────────

        public void PitchedHouse()
        {
            int floors = Floors(2, 1, 4);
            double W = Math.Clamp(d.WidthM, 8, 18), D = Math.Clamp(d.DepthM, 7, 12);
            var path = Rectangle(-W / 2, W / 2, -D / 2, D / 2);
            Foundation(Rect(-W / 2, W / 2, -D / 2, D / 2), 0.5);
            var cols = new List<(double X, double Z)> { (0, 0) };
            double fh = 3.0;
            for (int i = 0; i < floors; i++)
            {
                double y = i * fh, h = fh - SlabThickness;
                Column(0, 0, y, h, 0.4, i, $"Дом · колонна эт. {i + 1}");
                int f = 0;
                foreach (var s in path)
                {
                    var wall = Facade(s, false, y, h, i, $"Дом · стена {++f} · эт. {i + 1}", d.WallHex, 3.0, 1.4, 1.5, 0.9);
                    if (i == 0 && f == 1) Door(wall, -W / 4, "Дом · вход");
                }
                Plate(ElementTypes.Slab, $"Дом · перекрытие {i + 1}", Rect(-W / 2, W / 2, -D / 2, D / 2), SlabThickness, y + fh, i, d.AccentHex);
            }
            double rise = Math.Clamp(d.DomeHeightRatio * d.HeightM, 2, 5);
            var roof = new ArchitecturalElement { Type = ElementTypes.Roof, Name = "Дом · скатная кровля", Material = "tile", LevelIndex = floors, RoofShape = RoofShapes.GableHollow };
            roof.SetSize(W, rise, D);
            roof.SetPosition(0, floors * fh + rise / 2, 0);
            Models.RoofDesign.Write(roof, 0.25, 0.5);
            Add(roof, d.RoofHex);
            _ = cols;
            _footW = W;
            _footD = D;
        }

        public void RoundedTower() => RectTower(Math.Clamp(d.CornerRadiusRatio > 0 ? d.CornerRadiusRatio : 0.2, 0.08, 0.45));

        public void BoxTower() => RectTower(0);

        private void RectTower(double radiusRatio)
        {
            int floors = Floors(9, 1, 50);
            double W = Math.Clamp(d.WidthM, 12, 60), D = Math.Clamp(d.DepthM, 10, 36);
            double r = radiusRatio * Math.Min(W, D);
            var path = r > 0.3 ? ContourPath.RoundedRect(W, D, r) : Rectangle(-W / 2, W / 2, -D / 2, D / 2);
            bool glass = Glazing > 0.55;
            Foundation(ContourPath.Sample(path), floors > 9 ? 0.9 : 0.6);
            var cols = ColumnLayout(path, glass ? 1.1 : 1.0, 6.6, 3.0, 7.2);
            Storeys(path, 0, floors, 0, FloorH, cols, 6.6 * 7.2, glass, r > 0.3 ? "Скруглённый корпус" : "Корпус", d.WallHex, d.AccentHex, roofHex: d.RoofHex);
            _m.Log.Add(string.Create(Ru, $"Корпус {W:0.#} × {D:0.#} м") + (r > 0.3 ? string.Create(Ru, $", скругление углов {r:0.#} м") : string.Empty));
        }

        /// <summary>
        /// Явные координационные оси по колоннам первого этажа: линии X и Z, на которых стоят ≥ 3 колонны (допуск 0,3 м), не больше
        /// 14 по направлению (без них автоматические оси по радиальным колоннам дали бы десятки марок).
        /// </summary>
        private void BuildAxes()
        {
            var cols = _m.Elements.Where(e => e.Category == BimCategory.Column && e.LevelIndex == 0).ToList();
            List<double> Lines(Func<ArchitecturalElement, double> key)
            {
                var values = cols.Select(key).OrderBy(v => v).ToList();
                var lines = new List<(double Sum, int N)>();
                foreach (double v in values)
                    if (lines.Count > 0 && Math.Abs(v - lines[^1].Sum / lines[^1].N) < 0.3) lines[^1] = (lines[^1].Sum + v, lines[^1].N + 1);
                    else lines.Add((v, 1));
                return lines.Where(l => l.N >= 3).Select(l => Math.Round(l.Sum / l.N, 3)).Take(14).ToList();
            }
            const string letters = "АБВГДЕЖИКЛМНПРСТУФ";
            var xs = Lines(e => e.X);
            var zs = Lines(e => e.Z);
            if (xs.Count < 2 || zs.Count < 2)
            {
                var all = _m.Elements.Where(e => e.Category is BimCategory.Slab or BimCategory.Foundation).SelectMany(AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline).ToList();
                if (all.Count == 0) return;
                var (minX, maxX, minZ, maxZ) = Geometry2D.Bounds(all);
                if (xs.Count < 2) xs = [Math.Round(minX + 1, 3), Math.Round((minX + maxX) / 2, 3), Math.Round(maxX - 1, 3)];
                if (zs.Count < 2) zs = [Math.Round(minZ + 1, 3), Math.Round(maxZ - 1, 3)];
            }
            for (int i = 0; i < xs.Count; i++) _m.Axes.Add(new StructuralGridAxis((i + 1).ToString(Ru), StructuralGridAxis.AlongZ, xs[i], _group));
            for (int i = 0; i < zs.Count; i++) _m.Axes.Add(new StructuralGridAxis(letters[Math.Min(i, letters.Length - 1)].ToString(), StructuralGridAxis.AlongX, zs[i], _group));
        }

        // ───────────────────────────── Итог ─────────────────────────────

        public PhotoBimModel Finish()
        {
            var group = new BuildingGroup(_group, morphology == PhotoMorphology.ArchSpan ? "public" : "residential", d.WallHex) { Name = _group };
            _m.Groups.Add(group);
            // Окна и двери — честные проёмы: сдвиг из зон колонн и поперечных стен, без места — удаляются.
            var openings = _m.Elements.Where(e => e.Category is BimCategory.Window or BimCategory.Door).ToList();
            var byLevel = _m.Elements.GroupBy(e => e.LevelIndex).ToDictionary(g => g.Key, g => g.ToList());
            int moved = 0, removed = 0;
            foreach (var op in openings)
            {
                var res = BuildingLogicGuard.Enforce(byLevel[op.LevelIndex], op);
                if (!res.Success) { _m.Elements.Remove(op); byLevel[op.LevelIndex].Remove(op); removed++; }
                else if (Math.Abs(res.Shift) > 1e-4) moved++;
            }
            if (moved + removed > 0) _m.Log.Add($"Логика проёмов: сдвинуто {moved}, без места удалено {removed}");
            BuildAxes();
            _m.Floors = _m.Elements.Where(e => e.Category is BimCategory.Slab or BimCategory.Roof or BimCategory.Shell).Select(e => e.LevelIndex).DefaultIfEmpty(0).Max() + 1;
            if (morphology == PhotoMorphology.ArchSpan) _m.Floors = 1;
            _m.HeightM = _maxTop;
            _m.FootprintW = _footW;
            _m.FootprintD = _footD;
            return _m;
        }
    }

    // ───────────────────────────── Геометрия ─────────────────────────────

    /// <summary>Смещение многоугольника наружу на δ (миттер по биссектрисе, ограничение ×3 у острых углов).</summary>
    public static List<(double X, double Z)> Offset(IReadOnlyList<(double X, double Z)> poly, double delta)
    {
        var p = ContourPath.SignedArea(poly) >= 0 ? poly.ToList() : poly.Reverse().ToList();
        int n = p.Count;
        var result = new List<(double X, double Z)>(n);
        for (int i = 0; i < n; i++)
        {
            var a = p[(i - 1 + n) % n];
            var b = p[i];
            var c = p[(i + 1) % n];
            var n1 = OutNormal(a, b);
            var n2 = OutNormal(b, c);
            double bx = n1.X + n2.X, bz = n1.Z + n2.Z;
            double len = Math.Sqrt(bx * bx + bz * bz);
            if (len < 1e-9) { result.Add((b.X + n1.X * delta, b.Z + n1.Z * delta)); continue; }
            bx /= len; bz /= len;
            double cos = bx * n1.X + bz * n1.Z;
            double k = Math.Min(3, 1 / Math.Max(1e-3, cos));
            result.Add((b.X + bx * delta * k, b.Z + bz * delta * k));
        }
        return result;
    }

    private static (double X, double Z) OutNormal((double X, double Z) a, (double X, double Z) b)
    {
        double dx = b.X - a.X, dz = b.Z - a.Z, l = Math.Max(1e-9, Math.Sqrt(dx * dx + dz * dz));
        return (dz / l, -dx / l);
    }

    private static double Dist((double X, double Z) a, (double X, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
