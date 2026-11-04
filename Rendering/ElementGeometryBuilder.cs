using System.Numerics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;

namespace AIArchitectStudio.Rendering;

/// <summary>Контекст построения: фильтр этажей и центр постройки (ось закрутки диагрида).</summary>
public readonly record struct GeometryContext(int LevelFilter, double GroupOriginX, double GroupOriginZ);

/// <summary>
/// Процедурный генератор 3D-геометрии всех 18 типов BIM-элементов. Геометрия строится в локальной системе
/// элемента (центр = Position, локальная X — вдоль стены), параметрические элементы (RepeatLevels)
/// разворачиваются поэтажно с закруткой TwistAnglePerFloor и сужением TaperScalePerFloor. Результат — независимая геометрия
/// (<see cref="BimMesh"/>, System.Numerics) без типов графического движка; во вьюпорт её переносит HelixMeshAdapter.
/// </summary>
public static partial class ElementGeometryBuilder
{
    private const float Rad = MathF.PI / 180f;

    /// <summary>Набор аккумуляторов по материалам с общей поэтажной матрицей.</summary>
    private sealed class PartSet
    {
        private readonly Dictionary<MaterialKind, MeshAccumulator> _acc = [];
        private Matrix4x4 _transform = Matrix4x4.Identity;

        public Matrix4x4 Transform
        {
            get => _transform;
            set
            {
                _transform = value;
                foreach (var a in _acc.Values) a.Transform = value;
            }
        }

        public MeshAccumulator this[MaterialKind kind]
        {
            get
            {
                if (!_acc.TryGetValue(kind, out var a))
                {
                    a = new MeshAccumulator { Transform = _transform, CollectEdges = kind == MaterialKind.Void || !MaterialCatalog.IsTransparent(kind) };
                    _acc[kind] = a;
                }
                return a;
            }
        }

        public ElementGeometry Finish(Matrix4x4 world)
        {
            var parts = new List<BimMeshPart>();
            var edgePoints = new List<Vector3>();
            var allPoints = new List<Vector3>();
            foreach (var (kind, acc) in _acc)
            {
                if (acc.IsEmpty) continue;
                parts.Add(new BimMeshPart(acc.ToMesh(), kind));
                edgePoints.AddRange(acc.EdgePoints);
                allPoints.AddRange(acc.Positions);
            }

            var edges = edgePoints.Count >= 2 ? BimLineSet.FromSegments(edgePoints) : null;

            return new ElementGeometry
            {
                Parts = parts,
                Edges = edges,
                Transform = world,
                LocalBounds = BimBounds.FromPoints(allPoints)
            };
        }
    }

    public static ElementGeometry Build(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> hosted, GeometryContext ctx)
    {
        var set = new PartSet();
        float w = (float)Math.Max(0.01, e.Width), h = (float)Math.Max(0.01, e.Height), d = (float)Math.Max(0.01, e.Depth);
        var mat = MaterialCatalog.ForElement(e);

        bool tilt = e.Category == BimCategory.Diagrid && e.RepeatLevels <= 1;
        var world = WorldMatrix(e, includeTilt: tilt || e.Category is BimCategory.Asset or BimCategory.Beam or BimCategory.Column);

        if (e.Category == BimCategory.Diagrid && e.RepeatLevels > 1)
        {
            BuildDiagridShell(set, e, ctx);
            return set.Finish(world);
        }

        int repeat = Math.Max(1, e.RepeatLevels);
        bool ceilingLike = ElementTypes.IsCeilingType(e.Category) || e.Category == BimCategory.CantileverSlab;
        float taper = e.TaperScalePerFloor <= 0 ? 1f : (float)e.TaperScalePerFloor;
        float scale = 1f;

        for (int i = 0; i < repeat; i++, scale *= taper)
        {
            int level = e.LevelIndex + i;
            if (ctx.LevelFilter >= 0)
            {
                if (level > ctx.LevelFilter) break;
                if (ceilingLike && level == ctx.LevelFilter) continue;
            }

            set.Transform = i == 0 && repeat == 1
                ? Matrix4x4.Identity
                : BimMath.Multiply(BimMath.Multiply(BimMath.Scaling(scale, 1, scale), BimMath.RotationY((float)(e.TwistAnglePerFloor * i) * Rad)), BimMath.Translation(0, (float)(e.LevelHeight * i), 0));

            var floorHosted = repeat == 1 ? hosted : hosted.Where(o => o.LevelIndex == level || ElementTypes.IsHostedType(o.Category)).ToList();
            BuildSingle(set, e, floorHosted, w, h, d, mat, repeat == 1 ? null : (scale, (float)(e.TwistAnglePerFloor * i)));
        }

        return set.Finish(world);
    }

    /// <summary>Мировая матрица элемента: поворот (X/Z — наклон, Y — в плане) и перенос в Position.</summary>
    public static Matrix4x4 WorldMatrix(ArchitecturalElement e, bool includeTilt = false)
    {
        float rx = includeTilt && e.Rotation.Length > 0 ? (float)e.Rotation[0] * Rad : 0f;
        float rz = includeTilt && e.Rotation.Length > 2 ? (float)e.Rotation[2] * Rad : 0f;
        return BimMath.Multiply(BimMath.Multiply(BimMath.Multiply(BimMath.RotationX(rx), BimMath.RotationZ(rz)), BimMath.RotationY((float)e.RotationY * Rad)),
            BimMath.Translation((float)e.X, (float)e.Y, (float)e.Z));
    }

    /// <param name="floorTransform">Масштаб и закрутка этажа параметрического элемента (для пересчёта отверстий плит в его систему).</param>
    private static void BuildSingle(PartSet set, ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> hosted, float w, float h, float d, MaterialKind mat,
        (float Scale, float TwistDegrees)? floorTransform = null)
    {
        var poly = PolygonOf(e);
        // Волна 4: дуги, косые торцы, арочный верх витража, купол/конус/складки — элементы без этих форм идут прежним путём.
        if (TryBuildCurved(set, e, hosted, w, h, d, mat, poly, floorTransform)) return;
        switch (e.Type)
        {
            case ElementTypes.Wall when e.Layers.Count > 0:
                // Пирог стены: слои от грани +Z к грани −Z, каждый — своим материалом и со своими вырезами проёмов.
                foreach (var (layer, z0, z1) in LayerBands(e.Layers, -d / 2, d / 2, fromTop: true))
                    BuildWall(set[MaterialCatalog.LayerKind(layer, mat)], e, hosted, w, h, z1 - z0, (z0 + z1) / 2);
                break;
            case ElementTypes.Wall:
                BuildWall(set[mat], e, hosted, w, h, d);
                break;
            case ElementTypes.CurtainWall:
                if (poly is not null) BuildPolygonCurtainWall(set, poly, h);
                else BuildCurtainWall(set, w, h, d);
                break;
            case ElementTypes.Column:
                BuildColumn(set[mat], e, poly, w, h, d);
                break;
            case ElementTypes.Core:
                BuildCore(set[mat], w, h, d);
                break;
            case ElementTypes.Beam:
                if (poly is not null) BuildRingBeam(set[mat], poly, h);
                else BuildBeam(set[mat], e, w, h, d);
                break;
            case ElementTypes.WallOpening:
                BuildWallOpeningVoid(set[MaterialKind.Void], w, h, d);
                break;
            case ElementTypes.SlabOpening:
                BuildSlabOpeningVoid(set[MaterialKind.Void], poly, w, h, d);
                break;
            case ElementTypes.Truss:
                BuildTruss(set[MaterialKind.Steel], w, h, d);
                break;
            case ElementTypes.Diagrid:
                set[mat].AddCylinder(new Vector3(0, -h / 2, 0), new Vector3(0, h / 2, 0), Math.Max(w, d) / 2, 10);
                break;
            case ElementTypes.Shell:
                set[mat].AddDome(-h / 2, w / 2, h, d / 2);
                break;
            case ElementTypes.Roof when RoofShapes.IsHollow(RoofShapes.Normalize(e.RoofShape)):
                // Полая кровля: скаты заданной толщины со свесами, фронтоны, подшивка (или плоская с парапетом).
                BuildHollowRoof(set, e, poly, SlabHoles(e, hosted, floorTransform), w, h, d, mat);
                break;
            case ElementTypes.Roof when e.IsFlatRoof && e.Layers.Count > 0:
            {
                // Пирог плоской кровли: ж/б, пароизоляция, утеплитель, разуклонка, гидроизоляция — снизу вверх.
                var roofHoles = SlabHoles(e, hosted, floorTransform);
                foreach (var (layer, y0, y1) in LayerBands(e.Layers, -h / 2, h / 2, fromTop: false))
                    AddSlabBand(set[MaterialCatalog.LayerKind(layer, mat)], poly, roofHoles, w, d, y0, y1);
                break;
            }
            case ElementTypes.Roof:
                BuildRoof(set[mat], e, poly, SlabHoles(e, hosted, floorTransform), w, h, d);
                break;
            case ElementTypes.Stairs:
                BuildStairElement(set, e, w, h, d);
                break;
            case ElementTypes.Window:
                BuildWindow(set, w, h, d);
                break;
            case ElementTypes.Door:
                BuildDoor(set, w, h, d);
                break;
            case ElementTypes.Pool:
                BuildPool(set, w, h, d);
                break;
            default:
                // foundation, slab, cantilever_slab, terrace, asset: призма строго по контуру Polygon2D (или W × D)
                // со сквозными отверстиями slab_opening этого элемента; с пирогом — каждый слой отдельной призмой снизу вверх.
                var holes = ElementTypes.IsSlabHostType(e.Category) ? SlabHoles(e, hosted, floorTransform) : [];
                if (e.Layers.Count > 0 && MaterialLayers.IsLayerHost(e))
                {
                    foreach (var (layer, y0, y1) in LayerBands(e.Layers, -h / 2, h / 2, fromTop: false))
                        AddSlabBand(set[MaterialCatalog.LayerKind(layer, mat)], poly, holes, w, d, y0, y1);
                    break;
                }
                AddSlabBand(set[mat], poly, holes, w, d, -h / 2, h / 2);
                break;
        }
    }

    /// <summary>Призма плиты между отметками y0…y1 (локально): по контуру или W × D, с отверстиями.</summary>
    private static void AddSlabBand(MeshAccumulator acc, List<Vector2>? poly, List<IReadOnlyList<Vector2>> holes, float w, float d, float y0, float y1)
    {
        if (holes.Count > 0) acc.AddPrismWithHoles(poly ?? Rect(w, d), holes, y0, y1);
        else if (poly is not null) acc.AddPrism(poly, y0, y1);
        else acc.AddBox(new Vector3(0, (y0 + y1) / 2, 0), w, y1 - y0, d);
    }

    /// <summary>
    /// Полосы слоёв пирога в пределах [from, to]: толщины масштабируются на габарит элемента; fromTop — первый слой у верхней
    /// границы (стена: грань +Z), иначе — у нижней (плита: низ).
    /// </summary>
    private static IEnumerable<(MaterialLayer Layer, float Start, float End)> LayerBands(IReadOnlyList<MaterialLayer> layers, float from, float to, bool fromTop)
    {
        double total = MaterialLayers.TotalMm(layers);
        if (total <= 0) yield break;
        float span = to - from, cursor = fromTop ? to : from;
        foreach (var layer in layers)
        {
            float t = (float)(Math.Max(0, layer.ThicknessMm) / total) * span;
            if (t <= 1e-5f) continue;
            if (fromTop)
            {
                yield return (layer, cursor - t, cursor);
                cursor -= t;
            }
            else
            {
                yield return (layer, cursor, cursor + t);
                cursor += t;
            }
        }
    }

    private static List<Vector2>? PolygonOf(ArchitecturalElement e) =>
        e.Polygon2D is { Count: >= 3 } p ? p.Select(pt => new Vector2((float)pt.X, (float)pt.Z)).ToList() : null;

    private static List<Vector2> Rect(float w, float d) =>
        [new(-w / 2, -d / 2), new(w / 2, -d / 2), new(w / 2, d / 2), new(-w / 2, d / 2)];

    /// <summary>Контур элемента в плане в мировых координатах (X, Z): Polygon2D или прямоугольник W × D с учётом поворота.</summary>
    public static List<(double X, double Z)> WorldOutline(ArchitecturalElement e)
    {
        // Волна 4: дуговая стена/витраж — полоса по дуге, стена с косыми торцами — четырёхугольник реза.
        if (e.UserParameters is not null && e.Polygon2D.Count < 3 && e.Category is BimCategory.Wall or BimCategory.CurtainWall && ShapedOutline(e) is { } shaped)
            return shaped;
        IEnumerable<(double X, double Z)> local = e.Polygon2D.Count >= 3
            ? e.Polygon2D.Select(p => (p.X, p.Z))
            : [(-e.Width / 2, -e.Depth / 2), (e.Width / 2, -e.Depth / 2), (e.Width / 2, e.Depth / 2), (-e.Width / 2, e.Depth / 2)];
        return local.Select(p =>
        {
            var (rx, rz) = ArchitecturalElement.RotateY(p.X, p.Z, e.RotationY);
            return (e.X + rx, e.Z + rz);
        }).ToList();
    }

    /// <summary>
    /// Контуры отверстий slab_opening в локальной системе плиты-хоста (для этажа параметрической плиты — с обратной
    /// закруткой и масштабом этажа).
    /// </summary>
    private static List<IReadOnlyList<Vector2>> SlabHoles(ArchitecturalElement host, IReadOnlyList<ArchitecturalElement> hosted, (float Scale, float TwistDegrees)? floor)
    {
        var holes = new List<IReadOnlyList<Vector2>>();
        if (!host.CanHostSlabOpenings) return holes;
        foreach (var o in hosted)
        {
            if (o.Category != BimCategory.SlabOpening) continue;
            var ring = new List<Vector2>();
            foreach (var (wx, wz) in WorldOutline(o))
            {
                var (lx, lz) = ArchitecturalElement.RotateY(wx - host.X, wz - host.Z, -host.RotationY);
                if (floor is { } f)
                {
                    (lx, lz) = ArchitecturalElement.RotateY(lx, lz, -f.TwistDegrees);
                    lx /= f.Scale;
                    lz /= f.Scale;
                }
                ring.Add(new Vector2((float)lx, (float)lz));
            }
            if (ring.Count >= 3) holes.Add(ring);
        }
        return holes;
    }

    // ───────────────────────────── Колонны и балки: профили сечений ─────────────────────────────

    /// <summary>
    /// Колонна строго по своему сечению: прямоугольник W × D (квадрат без искажений), круг/эллипс, труба, двутавр
    /// или контур Polygon2D, выдавленные по высоте H.
    /// </summary>
    private static void BuildColumn(MeshAccumulator acc, ArchitecturalElement e, List<Vector2>? poly, float w, float h, float d)
    {
        switch (e.EffectiveProfile)
        {
            case ProfileTypes.Polygon when poly is not null:
                acc.AddPrism(poly, -h / 2, h / 2);
                break;
            case ProfileTypes.Circle:
                acc.AddTube(w / 2, d / 2, -h / 2, h / 2);
                break;
            case ProfileTypes.Pipe:
                acc.AddTube(w / 2, d / 2, -h / 2, h / 2, (float)e.ProfileThicknesses(w, d).Wall);
                break;
            case ProfileTypes.IBeam:
                acc.AddPrism(IShape(e, w, d), -h / 2, h / 2);
                break;
            default:
                acc.AddBox(Vector3.Zero, w, h, d);
                break;
        }
    }

    /// <summary>
    /// Балка вдоль локальной X (длина W) с сечением D (ширина) × H (высота): прямоугольник, круг, труба или двутавр
    /// (полки горизонтальны). Сечение строится в системе «ось профиля = Y» и поворачивается на ось балки.
    /// </summary>
    private static void BuildBeam(MeshAccumulator acc, ArchitecturalElement e, float w, float h, float d)
    {
        string profile = e.EffectiveProfile;
        if (profile is ProfileTypes.Rectangle or ProfileTypes.Polygon)
        {
            acc.AddBox(Vector3.Zero, w, h, d);
            return;
        }

        var saved = acc.Transform;
        // RotationZ(−90°): ось Y профиля → ось X балки, локальная X профиля → −Y (вертикаль сечения).
        acc.Transform = BimMath.Multiply(BimMath.RotationZ(-MathF.PI / 2), saved);
        switch (profile)
        {
            case ProfileTypes.Circle:
                acc.AddTube(h / 2, d / 2, -w / 2, w / 2);
                break;
            case ProfileTypes.Pipe:
                acc.AddTube(h / 2, d / 2, -w / 2, w / 2, (float)e.ProfileThicknesses(d, h).Wall);
                break;
            case ProfileTypes.IBeam:
                // В системе профиля: X — высота сечения (H), Z — ширина полки (D); полки перпендикулярны высоте.
                var shape = IShape(e, d, h).Select(p => new Vector2(p.Y, p.X)).ToList();
                acc.AddPrism(shape, -w / 2, w / 2);
                break;
        }
        acc.Transform = saved;
    }

    /// <summary>Контур двутавра: ширина полки b (по X), высота сечения hh (по Z), полки — по краям ±hh/2.</summary>
    private static List<Vector2> IShape(ArchitecturalElement e, float b, float hh)
    {
        var (wallT, webT) = e.ProfileThicknesses(b, hh);
        float tf = (float)wallT, tw = (float)webT;
        float x = b / 2, z = hh / 2;
        return
        [
            new(-x, -z), new(x, -z), new(x, -z + tf), new(tw / 2, -z + tf), new(tw / 2, z - tf), new(x, z - tf),
            new(x, z), new(-x, z), new(-x, z - tf), new(-tw / 2, z - tf), new(-tw / 2, -z + tf), new(-x, -z + tf)
        ];
    }

    // ───────────────────────────── Проёмы-пустоты ─────────────────────────────

    /// <summary>Сквозной проём стены: полупрозрачный объём выреза с контуром и диагоналями (условное обозначение проёма).</summary>
    private static void BuildWallOpeningVoid(MeshAccumulator acc, float w, float h, float d)
    {
        acc.AddBox(Vector3.Zero, w, h, d);
        acc.AddEdge(new Vector3(-w / 2, -h / 2, d / 2), new Vector3(w / 2, h / 2, d / 2));
        acc.AddEdge(new Vector3(w / 2, -h / 2, d / 2), new Vector3(-w / 2, h / 2, d / 2));
    }

    /// <summary>Отверстие плиты: объём выреза по контуру (прямоугольник или Polygon2D) и крест на верхней грани.</summary>
    private static void BuildSlabOpeningVoid(MeshAccumulator acc, List<Vector2>? poly, float w, float h, float d)
    {
        if (poly is not null) acc.AddPrism(poly, -h / 2, h / 2);
        else acc.AddBox(Vector3.Zero, w, h, d);
        var outline = poly ?? Rect(w, d);
        float minX = outline.Min(p => p.X), maxX = outline.Max(p => p.X), minZ = outline.Min(p => p.Y), maxZ = outline.Max(p => p.Y);
        acc.AddEdge(new Vector3(minX, h / 2, minZ), new Vector3(maxX, h / 2, maxZ));
        acc.AddEdge(new Vector3(maxX, h / 2, minZ), new Vector3(minX, h / 2, maxZ));
    }

    // ───────────────────────────── Стена с проёмами ─────────────────────────────

    /// <summary>Стена режется на участки вокруг привязанных окон и дверей (HostElementId).</summary>
    /// <param name="zc">Смещение слоя пирога от оси стены по локальной Z.</param>
    private static void BuildWall(MeshAccumulator acc, ArchitecturalElement wall, IReadOnlyList<ArchitecturalElement> hosted, float w, float h, float d, float zc = 0)
    {
        var openings = new List<(float X0, float X1, float Y0, float Y1)>();
        foreach (var o in hosted)
        {
            if (!ElementTypes.IsHostedType(o.Category)) continue;
            var (lx, _) = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY);
            float x0 = Math.Max(-w / 2, (float)(lx - o.Width / 2));
            float x1 = Math.Min(w / 2, (float)(lx + o.Width / 2));
            float y0 = Math.Max(-h / 2, (float)(o.BaseY - wall.Y));
            float y1 = Math.Min(h / 2, (float)(o.TopY - wall.Y));
            if (x1 - x0 > 0.05f && y1 - y0 > 0.05f) openings.Add((x0, x1, y0, y1));
        }
        openings.Sort((a, b) => a.X0.CompareTo(b.X0));

        float cursor = -w / 2;
        foreach (var (ox0, x1, y0, y1) in openings)
        {
            float x0 = Math.Max(ox0, cursor);
            if (x1 <= x0) continue;
            if (x0 - cursor > 0.001f) acc.AddBox(new Vector3((cursor + x0) / 2, 0, zc), x0 - cursor, h, d);
            float ow = x1 - x0, cx = (x0 + x1) / 2;
            if (y0 - (-h / 2) > 0.001f) acc.AddBox(new Vector3(cx, (-h / 2 + y0) / 2, zc), ow, y0 + h / 2, d);
            if (h / 2 - y1 > 0.001f) acc.AddBox(new Vector3(cx, (y1 + h / 2) / 2, zc), ow, h / 2 - y1, d);
            cursor = x1;
        }
        if (w / 2 - cursor > 0.001f) acc.AddBox(new Vector3((cursor + w / 2) / 2, 0, zc), w / 2 - cursor, h, d);
    }

    // ───────────────────────────── Витражи ─────────────────────────────

    private static void BuildCurtainWall(PartSet set, float w, float h, float d)
    {
        var glass = set[MaterialKind.Glass];
        var frame = set[MaterialKind.Frame];
        float gt = Math.Min(d, 0.03f);
        glass.AddBox(Vector3.Zero, w, h, gt, edges: false);

        float fd = Math.Max(d, 0.08f), fw = 0.06f;
        int cols = Math.Max(1, (int)Math.Ceiling(w / 1.6f));
        for (int i = 0; i <= cols; i++)
        {
            float x = -w / 2 + w * i / cols;
            frame.AddBox(new Vector3(x, 0, 0), fw, h, fd);
        }
        int rows = Math.Max(1, (int)Math.Ceiling(h / 3.6f));
        for (int j = 0; j <= rows; j++)
        {
            float y = -h / 2 + h * j / rows;
            frame.AddBox(new Vector3(0, y, 0), w, fw, fd);
        }
    }

    private static void BuildPolygonCurtainWall(PartSet set, List<Vector2> poly, float h)
    {
        var glass = set[MaterialKind.Glass];
        var frame = set[MaterialKind.Frame];
        var ccw = MeshAccumulator.EnsureCounterClockwise(poly);
        float y0 = -h / 2, y1 = h / 2;

        for (int i = 0; i < ccw.Count; i++)
        {
            var p = ccw[i];
            var q = ccw[(i + 1) % ccw.Count];
            var dir = q - p;
            var outward = new Vector3(dir.Y, 0, -dir.X);
            glass.AddQuad(new Vector3(p.X, y0, p.Y), new Vector3(q.X, y0, q.Y), new Vector3(q.X, y1, q.Y), new Vector3(p.X, y1, p.Y), outward);

            int panels = Math.Max(1, (int)Math.Ceiling(BimMath.Length(dir) / 3.2f));
            for (int k = 0; k < panels; k++)
            {
                var m = p + dir * (k / (float)panels);
                frame.AddCylinder(new Vector3(m.X, y0, m.Y), new Vector3(m.X, y1, m.Y), 0.05f, 4, caps: false);
            }
            frame.AddCylinder(new Vector3(p.X, y1, p.Y), new Vector3(q.X, y1, q.Y), 0.06f, 4, caps: false);
            frame.AddEdge(new Vector3(p.X, y1, p.Y), new Vector3(q.X, y1, q.Y));
            frame.AddEdge(new Vector3(p.X, y0, p.Y), new Vector3(q.X, y0, q.Y));
        }
    }

    // ───────────────────────────── Несущие конструкции ─────────────────────────────

    /// <summary>Ядро жёсткости: полая коробка из четырёх монолитных стен.</summary>
    private static void BuildCore(MeshAccumulator acc, float w, float h, float d)
    {
        float t = Math.Clamp(Math.Min(w, d) * 0.06f, 0.25f, 0.5f);
        acc.AddBox(new Vector3(0, 0, d / 2 - t / 2), w, h, t);
        acc.AddBox(new Vector3(0, 0, -d / 2 + t / 2), w, h, t);
        acc.AddBox(new Vector3(w / 2 - t / 2, 0, 0), t, h, d - 2 * t);
        acc.AddBox(new Vector3(-w / 2 + t / 2, 0, 0), t, h, d - 2 * t);
    }

    /// <summary>Кольцевая балка по контуру Polygon2D (сегментами шириной 0.6 м).</summary>
    private static void BuildRingBeam(MeshAccumulator acc, List<Vector2> poly, float h)
    {
        const float bw = 0.6f;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            var dir = q - p;
            if (BimMath.LengthSquared(dir) < 1e-6f) continue;
            dir = BimMath.Normalize(dir);
            var n = new Vector2(-dir.Y, dir.X) * (bw / 2);
            acc.AddPrism([p - n, q - n, q + n, p + n], -h / 2, h / 2, edges: true);
        }
    }

    /// <summary>Плоская стальная ферма: пояса + стойки + раскосы (схема Пратта) вдоль локальной X.</summary>
    private static void BuildTruss(MeshAccumulator acc, float w, float h, float d)
    {
        float chord = Math.Min(0.25f, h * 0.15f);
        float r = Math.Max(0.03f, Math.Min(d, chord) * 0.3f);
        acc.AddBox(new Vector3(0, h / 2 - chord / 2, 0), w, chord, d);
        acc.AddBox(new Vector3(0, -h / 2 + chord / 2, 0), w, chord, d);

        int panels = Math.Max(2, (int)Math.Round(w / Math.Max(h, 0.5f)));
        float yb = -h / 2 + chord, yt = h / 2 - chord;
        for (int i = 0; i <= panels; i++)
        {
            float x = -w / 2 + w * i / panels;
            acc.AddCylinder(new Vector3(x, yb, 0), new Vector3(x, yt, 0), r, 8);
            if (i == panels) continue;
            float xn = -w / 2 + w * (i + 1) / panels;
            bool leftHalf = (i + 0.5f) < panels / 2f;
            if (leftHalf) acc.AddCylinder(new Vector3(x, yt, 0), new Vector3(xn, yb, 0), r, 8);
            else acc.AddCylinder(new Vector3(x, yb, 0), new Vector3(xn, yt, 0), r, 8);
        }
    }

    /// <summary>
    /// Параметрический диагрид: сетчатая оболочка из наклонных стержней вокруг оси постройки
    /// (радиус = расстояние от элемента до центра группы), с закруткой и сужением по этажам.
    /// </summary>
    private static void BuildDiagridShell(PartSet set, ArchitecturalElement e, GeometryContext ctx)
    {
        var acc = set[MaterialKind.Steel];
        set.Transform = Matrix4x4.Identity;

        // Центр оболочки в локальных координатах элемента.
        var (cxl, czl) = ArchitecturalElement.RotateY(ctx.GroupOriginX - e.X, ctx.GroupOriginZ - e.Z, -e.RotationY);
        var center = new Vector2((float)cxl, (float)czl);
        float radius = Math.Max(1f, BimMath.Length(center));
        float angle0 = MathF.Atan2(-center.Y, -center.X);

        const int members = 16;
        float step = 2 * MathF.PI / members;
        float r = (float)Math.Max(0.08, Math.Max(e.Width, e.Depth) / 2);
        float levelH = (float)Math.Max(0.5, e.LevelHeight);
        float taper = e.TaperScalePerFloor <= 0 ? 1f : (float)e.TaperScalePerFloor;
        float twist = (float)e.TwistAnglePerFloor * Rad;
        float yStart = -(float)e.Height / 2;

        Vector3 Point(int floor, float a)
        {
            float rr = radius * MathF.Pow(taper, floor);
            float ang = angle0 + a + twist * floor;
            return new Vector3(center.X + rr * MathF.Cos(ang), yStart + floor * levelH, center.Y + rr * MathF.Sin(ang));
        }

        int repeat = Math.Max(1, e.RepeatLevels);
        for (int i = 0; i < repeat; i++)
        {
            if (ctx.LevelFilter >= 0 && e.LevelIndex + i > ctx.LevelFilter) break;
            for (int k = 0; k < members; k++)
            {
                bool forward = (k + i) % 2 == 0;
                float a0 = k * step, a1 = (k + 1) * step;
                if (forward) acc.AddCylinder(Point(i, a0), Point(i + 1, a1), r, 6, caps: false);
                else acc.AddCylinder(Point(i, a1), Point(i + 1, a0), r, 6, caps: false);
            }
            if (i % 2 == 0)
            {
                for (int k = 0; k < members; k++)
                    acc.AddCylinder(Point(i, k * step), Point(i, (k + 1) * step), r * 0.7f, 6, caps: false);
            }
        }
    }

    // ───────────────────────────── Кровля, лестница ─────────────────────────────

    /// <summary>
    /// Кровля строго по форме из проекта (RoofShape) и контуру Polygon2D: плоская — плита покрытия толщиной H с отверстиями
    /// slab_opening; двускатная / вальмовая / односкатная — высота H над карнизом. Контурная скатная кровля строится
    /// по точному контуру здания (конёк вдоль длинной стороны габарита).
    /// </summary>
    private static void BuildRoof(MeshAccumulator acc, ArchitecturalElement e, List<Vector2>? poly, List<IReadOnlyList<Vector2>> holes, float w, float h, float d)
    {
        string shape = RoofShapes.Normalize(e.RoofShape);
        float y0 = -h / 2;
        if (shape == RoofShapes.Flat)
        {
            if (holes.Count > 0) acc.AddPrismWithHoles(poly ?? Rect(w, d), holes, -h / 2, h / 2);
            else if (poly is not null) acc.AddPrism(poly, -h / 2, h / 2);
            else acc.AddBox(Vector3.Zero, w, h, d);
            return;
        }
        if (poly is null && shape == RoofShapes.Gable)
        {
            BuildGableRoof(acc, w, h, d);
            return;
        }
        if (poly is null && shape == RoofShapes.Hip)
        {
            BuildHipRoof(acc, w, h, d);
            return;
        }

        var outline = poly ?? Rect(w, d);
        float minX = outline.Min(p => p.X), maxX = outline.Max(p => p.X), minZ = outline.Min(p => p.Y), maxZ = outline.Max(p => p.Y);
        if (shape == RoofShapes.Shed)
        {
            float eave = Math.Min(0.15f, h * 0.1f), span = Math.Max(1e-3f, maxZ - minZ);
            acc.AddRegionSolid(outline, [], y0, p => y0 + eave + (h - eave) * (p.Y - minZ) / span, [], false);
            return;
        }

        // Двускатная (и вальмовая) по произвольному контуру: конёк по середине габарита вдоль длинной стороны.
        if (maxX - minX >= maxZ - minZ)
        {
            float cz = (minZ + maxZ) / 2, half = Math.Max(1e-3f, (maxZ - minZ) / 2);
            acc.AddRegionSolid(outline, [], y0, p => y0 + h * Math.Max(0, 1 - Math.Abs(p.Y - cz) / half), [cz], false);
        }
        else
        {
            float cx = (minX + maxX) / 2, half = Math.Max(1e-3f, (maxX - minX) / 2);
            acc.AddRegionSolid(outline, [], y0, p => y0 + h * Math.Max(0, 1 - Math.Abs(p.X - cx) / half), [cx], true);
        }
    }

    /// <summary>Вальмовая кровля прямоугольного плана: конёк длиной |W − D| вдоль длинной стороны, четыре ската.</summary>
    private static void BuildHipRoof(MeshAccumulator acc, float w, float h, float d)
    {
        float y0 = -h / 2, y1 = h / 2, half = Math.Min(w, d) / 2;
        var a = new Vector3(-w / 2, y0, -d / 2); var b = new Vector3(w / 2, y0, -d / 2);
        var c = new Vector3(w / 2, y0, d / 2); var dd = new Vector3(-w / 2, y0, d / 2);
        if (w >= d)
        {
            var r0 = new Vector3(-w / 2 + half, y1, 0); var r1 = new Vector3(w / 2 - half, y1, 0);
            acc.AddQuad(dd, c, r1, r0, new Vector3(0, 1, 1));
            acc.AddQuad(b, a, r0, r1, new Vector3(0, 1, -1));
            acc.AddTriangle(a, dd, r0, new Vector3(-1, 1, 0));
            acc.AddTriangle(c, b, r1, new Vector3(1, 1, 0));
            acc.AddEdge(r0, r1); acc.AddEdge(a, r0); acc.AddEdge(dd, r0); acc.AddEdge(b, r1); acc.AddEdge(c, r1);
        }
        else
        {
            var r0 = new Vector3(0, y1, -d / 2 + half); var r1 = new Vector3(0, y1, d / 2 - half);
            acc.AddQuad(b, c, r1, r0, new Vector3(1, 1, 0));
            acc.AddQuad(dd, a, r0, r1, new Vector3(-1, 1, 0));
            acc.AddTriangle(a, b, r0, new Vector3(0, 1, -1));
            acc.AddTriangle(c, dd, r1, new Vector3(0, 1, 1));
            acc.AddEdge(r0, r1); acc.AddEdge(a, r0); acc.AddEdge(b, r0); acc.AddEdge(c, r1); acc.AddEdge(dd, r1);
        }
        acc.AddQuad(a, b, c, dd, BimMath.NegativeUnitY);
        acc.AddEdgeLoop([a, b, c, dd]);
    }

    /// <summary>Двускатная кровля: конёк вдоль длинной стороны.</summary>
    private static void BuildGableRoof(MeshAccumulator acc, float w, float h, float d)
    {
        float y0 = -h / 2, y1 = h / 2;
        if (w >= d)
        {
            var a = new Vector3(-w / 2, y0, -d / 2); var b = new Vector3(w / 2, y0, -d / 2);
            var c = new Vector3(w / 2, y0, d / 2); var dd = new Vector3(-w / 2, y0, d / 2);
            var r0 = new Vector3(-w / 2, y1, 0); var r1 = new Vector3(w / 2, y1, 0);
            acc.AddQuad(dd, c, r1, r0, new Vector3(0, d / 2, h));
            acc.AddQuad(b, a, r0, r1, new Vector3(0, d / 2, -h));
            acc.AddTriangle(a, dd, r0, BimMath.NegativeUnitX);
            acc.AddTriangle(c, b, r1, Vector3.UnitX);
            acc.AddQuad(a, b, c, dd, BimMath.NegativeUnitY);
            acc.AddEdgeLoop([a, b, c, dd]);
            acc.AddEdge(r0, r1); acc.AddEdge(a, r0); acc.AddEdge(dd, r0); acc.AddEdge(b, r1); acc.AddEdge(c, r1);
        }
        else
        {
            var a = new Vector3(-w / 2, y0, -d / 2); var b = new Vector3(w / 2, y0, -d / 2);
            var c = new Vector3(w / 2, y0, d / 2); var dd = new Vector3(-w / 2, y0, d / 2);
            var r0 = new Vector3(0, y1, -d / 2); var r1 = new Vector3(0, y1, d / 2);
            acc.AddQuad(b, c, r1, r0, new Vector3(w / 2, h, 0));
            acc.AddQuad(dd, a, r0, r1, new Vector3(-w / 2, h, 0));
            acc.AddTriangle(a, b, r0, BimMath.NegativeUnitZ);
            acc.AddTriangle(c, dd, r1, Vector3.UnitZ);
            acc.AddQuad(a, b, c, dd, BimMath.NegativeUnitY);
            acc.AddEdgeLoop([a, b, c, dd]);
            acc.AddEdge(r0, r1); acc.AddEdge(a, r0); acc.AddEdge(b, r0); acc.AddEdge(c, r1); acc.AddEdge(dd, r1);
        }
    }

    /// <summary>Монолитный лестничный марш: ступени вдоль локальной Z (подъём ~0.17 м или заданное число подступенков).</summary>
    private static void BuildStairs(MeshAccumulator acc, float w, float h, float d, int stepCount)
    {
        int steps = stepCount > 0 ? Math.Clamp(stepCount, 1, 60) : Math.Clamp((int)Math.Round(h / 0.17f), 3, 40);
        float rise = h / steps, run = d / steps;
        for (int i = 0; i < steps; i++)
        {
            float top = -h / 2 + (i + 1) * rise;
            float z0 = -d / 2 + i * run;
            acc.AddBox(new Vector3(0, (-h / 2 + top) / 2, z0 + run / 2), w, top + h / 2, run);
        }
    }

    // ───────────────────────────── Проёмы ─────────────────────────────

    private static void BuildWindow(PartSet set, float w, float h, float d)
    {
        var frame = set[MaterialKind.Frame];
        float fw = 0.07f, fd = Math.Min(d, 0.14f);
        frame.AddBox(new Vector3(0, h / 2 - fw / 2, 0), w, fw, fd);
        frame.AddBox(new Vector3(0, -h / 2 + fw / 2, 0), w, fw, fd);
        frame.AddBox(new Vector3(-w / 2 + fw / 2, 0, 0), fw, h - 2 * fw, fd);
        frame.AddBox(new Vector3(w / 2 - fw / 2, 0, 0), fw, h - 2 * fw, fd);
        if (w > 1.2f) frame.AddBox(Vector3.Zero, fw * 0.8f, h - 2 * fw, fd * 0.8f);
        set[MaterialKind.Glass].AddBox(Vector3.Zero, w - 2 * fw, h - 2 * fw, 0.02f, edges: false);
    }

    private static void BuildDoor(PartSet set, float w, float h, float d)
    {
        var frame = set[MaterialKind.Frame];
        float fw = 0.07f, fd = Math.Min(d, 0.16f);
        frame.AddBox(new Vector3(0, h / 2 - fw / 2, 0), w, fw, fd);
        frame.AddBox(new Vector3(-w / 2 + fw / 2, -fw / 2, 0), fw, h - fw, fd);
        frame.AddBox(new Vector3(w / 2 - fw / 2, -fw / 2, 0), fw, h - fw, fd);
        set[MaterialKind.DoorWood].AddBox(new Vector3(0, -fw / 2, 0), w - 2 * fw, h - fw, 0.05f);
    }

    // ───────────────────────────── Бассейн ─────────────────────────────

    /// <summary>Чаша бассейна (дно + борта) и полупрозрачный объём воды.</summary>
    private static void BuildPool(PartSet set, float w, float h, float d)
    {
        var bowl = set[MaterialKind.PoolTile];
        float t = Math.Min(0.25f, Math.Min(w, d) / 6);
        bowl.AddBox(new Vector3(0, -h / 2 + t / 2, 0), w, t, d);
        bowl.AddBox(new Vector3(w / 2 - t / 2, 0, 0), t, h, d);
        bowl.AddBox(new Vector3(-w / 2 + t / 2, 0, 0), t, h, d);
        bowl.AddBox(new Vector3(0, 0, d / 2 - t / 2), w - 2 * t, h, t);
        bowl.AddBox(new Vector3(0, 0, -d / 2 + t / 2), w - 2 * t, h, t);

        float waterTop = h / 2 - 0.12f, waterBottom = -h / 2 + t;
        if (waterTop > waterBottom)
            set[MaterialKind.Water].AddBox(new Vector3(0, (waterTop + waterBottom) / 2, 0), w - 2 * t, waterTop - waterBottom, d - 2 * t, edges: false);
    }
}
