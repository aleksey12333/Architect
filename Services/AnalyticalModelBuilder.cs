using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Analytical;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>
/// Построитель расчётной схемы (<see cref="IAnalyticalModel"/>) по элементам сцены. Параметрические элементы разворачиваются
/// поэтажно; каждый несущий элемент становится стержнем (колонна, ядро, этаж диагрида, балка, ферма) или пластиной (плита,
/// покрытие, фундамент, купол, стена, витраж) с материалом, сечением, собственным весом и узлами (общими в точках сопряжения).
/// Затем находятся связи: перекрытия, открытые сверху (снег), опоры плит и балок, пролёты балок, точки передачи нагрузки вниз
/// и их получатели, распределение равномерной нагрузки плит по грузовым площадям с пролётом и вылетом консоли. Эта часть не
/// зависит от сил, приходящих сверху, и считается параллельно — по одному элементу на поток в том же порядке операций, что и
/// последовательно, поэтому схема (и расчёт по ней) от числа ядер не зависит до бита.
/// </summary>
public sealed class AnalyticalModelBuilder
{
    private const double Tol = AnalyticalModel.Tolerance;

    /// <summary>Ширина плавной границы грузовых площадей соседних опор, м (не зависит от шага сетки).</summary>
    private const double TributaryBlend = 0.75;

    private const double G = 9.81 / 1000.0; // кг → кН
    private const int DiagridMembers = 16;
    private const int FoundationSamples = 240, LoadOnlySamples = 48;

    private readonly IReadOnlyList<ArchitecturalElement> _elements;
    private readonly IReadOnlyList<BuildingGroup> _groups;
    private readonly StructuralLoadSettings s;
    private readonly int _slabSamples;
    private readonly bool _parallel;
    private readonly AnalyticalModel _model;
    private readonly (double Rb, double Rbt, double Eb, double Creep) _concrete;
    private readonly (double Ry, double E, double Density) _steel;
    private readonly double _concreteDensity;

    private AnalyticalModelBuilder(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, StructuralLoadSettings settings,
        int slabSamples, bool parallel)
    {
        _elements = elements;
        _groups = groups;
        s = settings;
        _slabSamples = slabSamples;
        _parallel = parallel;
        _model = new AnalyticalModel(settings);
        // Марки по таблицам СП или по сертификатам проекта (без переопределений — те же значения таблиц до бита).
        _concrete = settings.ConcreteProps();
        _steel = settings.SteelProps();
        _concreteDensity = settings.ConcreteDensity();
    }

    /// <summary>Расчётная схема текущей сцены документа.</summary>
    public static AnalyticalModel Build(SceneStateService scene, StructuralLoadSettings settings) =>
        Build(scene.Elements.ToList(), scene.BuildingGroups.ToList(), settings);

    /// <summary>Расчётная схема произвольного набора элементов.</summary>
    /// <param name="slabSamples">Число квадратурных точек на плиту (40…4000).</param>
    /// <param name="parallel">Связи и грузовые площади в пуле потоков (false — в вызывающем потоке; схема одинакова до бита).</param>
    public static AnalyticalModel Build(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, StructuralLoadSettings settings,
        int slabSamples = 220, bool parallel = true)
    {
        var sanitized = settings.Clone();
        sanitized.Sanitize();
        var builder = new AnalyticalModelBuilder(elements, groups, sanitized, Math.Clamp(slabSamples, 40, 4000), parallel);
        builder.Run();
        return builder._model;
    }

    private void Run()
    {
        var profile = _model.Profile;
        var sw = Stopwatch.StartNew();
        BuildElements();
        BuildNodes();
        profile.ModelMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        foreach (var m in _model.Elements.Where(m => m.Role is AnalyticalRole.Slab or AnalyticalRole.Foundation)) m.Exposed = IsExposed(m);
        profile.ExposedMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        PlanAreas();
        profile.PlansMs = sw.Elapsed.TotalMilliseconds;
    }

    // ───────────── Элементы схемы ─────────────

    private void BuildElements()
    {
        var openings = _elements.Where(e => e.Category == BimCategory.SlabOpening).ToList();
        var wallOpenings = _elements.Where(e => ElementTypes.IsHostedType(e.Category) && e.HostElementId is not null)
            .GroupBy(e => e.HostElementId!).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var e in _elements)
        {
            if (ElementTypes.IsVoidType(e.Category) || e.Category is BimCategory.Window or BimCategory.Door) continue;
            if (e.Position.Concat(e.Size).Any(v => !double.IsFinite(v))) continue;
            foreach (var (inst, floor) in Expand(e))
            {
                var m = Create(e, inst, floor, wallOpenings.GetValueOrDefault(e.Id));
                if (m is null) continue;
                if (m.Role is AnalyticalRole.Slab or AnalyticalRole.Foundation && e.CanHostSlabOpenings)
                    foreach (var hole in openings)
                    {
                        bool mine = hole.HostElementId == e.Id && (floor == 0 || hole.LevelIndex == inst.LevelIndex);
                        bool loose = hole.HostElementId is null && hole.BuildingGroupId == e.BuildingGroupId && hole.LevelIndex == inst.LevelIndex &&
                                     Geometry2D.Contains(m.Outline, hole.X, hole.Z);
                        if (!mine && !loose) continue;
                        var holeOutline = ElementGeometryBuilder.WorldOutline(hole);
                        m.Holes.Add(holeOutline);
                        m.HoleArr.Add(holeOutline.ToArray());
                    }
                m.NetArea = Math.Max(0.01, Geometry2D.Area(m.Outline) - m.Holes.Sum(h => Geometry2D.Area(h)));
                if (m is AnalyticalPanel { Role: AnalyticalRole.Slab or AnalyticalRole.Foundation } panel)
                    (panel.AreaSelfD, panel.AreaSelfN) = AreaSelf(panel);
                _model.Add(m);
            }
        }
    }

    /// <summary>Параметрический элемент разворачивается поэтажно (масштаб и закрутка этажа), включая диагрид.</summary>
    private static IEnumerable<(ArchitecturalElement Instance, int Floor)> Expand(ArchitecturalElement e)
    {
        int repeat = Math.Max(1, e.RepeatLevels);
        if (repeat == 1)
        {
            yield return (e, 0);
            yield break;
        }
        double taper = e.TaperScalePerFloor <= 0 ? 1 : e.TaperScalePerFloor, scale = 1;
        for (int i = 0; i < repeat; i++, scale *= taper)
        {
            var c = e.Clone();
            c.Id = i == 0 ? e.Id : $"{e.Id}:{i}";
            c.RepeatLevels = 1;
            c.LevelIndex = e.LevelIndex + i;
            c.SetPosition(e.X, e.Y + e.LevelHeight * i, e.Z);
            if (e.Category != BimCategory.Diagrid) c.SetSize(e.Width * scale, e.Height, e.Depth * scale);
            c.SetRotationY(e.RotationY + e.TwistAnglePerFloor * i);
            c.Polygon2D = e.Polygon2D.Select(p => new Point2D(p.X * scale, p.Z * scale)).ToList();
            c.TaperScalePerFloor = scale;
            yield return (c, i);
        }
    }

    /// <summary>Стержни — колонны, ядра, диагрид, балки и фермы; всё остальное — пластины.</summary>
    private static bool IsStick(string type) =>
        type is ElementTypes.Column or ElementTypes.Core or ElementTypes.Diagrid or ElementTypes.Beam or ElementTypes.Truss;

    private AnalyticalElement? Create(ArchitecturalElement source, ArchitecturalElement e, int floor, List<ArchitecturalElement>? wallOpenings)
    {
        var mat = MatOf(e);
        AnalyticalMember? bar = null;
        AnalyticalPanel? panel = null;
        AnalyticalElement m = IsStick(e.Type)
            ? bar = new AnalyticalMember { Source = source, Instance = e, Material = mat }
            : panel = new AnalyticalPanel { Source = source, Instance = e, Material = mat };
        m.Floor = floor;
        m.Base = e.BaseY;
        m.Top = e.TopY;
        m.Category = e.Category;
        m.GroupId = source.BuildingGroupId;
        m.RotationY = e.RotationY;
        m.HasPolygon = e.Polygon2D.Count >= 3;
        m.Outline = ElementGeometryBuilder.WorldOutline(e);
        m.Center = (e.X, e.Z);
        double w = Math.Max(0.01, e.Width), h = Math.Max(0.01, e.Height), d = Math.Max(0.01, e.Depth);
        var (axisA, axisB) = AxisOf(e);
        if (panel is not null)
        {
            panel.Width = e.Width;
            panel.ModelHeight = e.Height;
            panel.Depth = e.Depth;
        }

        switch (e.Type)
        {
            case ElementTypes.Column:
                m.Role = AnalyticalRole.Vertical;
                m.Bearing = true;
                if (e.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe && e.Polygon2D.Count < 3)
                {
                    m.Shape = AnalyticalShape.Circle;
                    m.Radius = Math.Max(w, d) / 2;
                }
                else m.Shape = AnalyticalShape.Polygon;
                bar!.Section = SectionOf(e, w, d);
                bar.SectionB = e.Width;
                bar.SectionH = e.Depth;
                bar.J = TorsionOf(e, w, d, bar.Section);
                m.SelfD = bar.Section.A * h * mat.Density * G * StructuralNorms.GammaDead;
                m.LateralInertia = SectionOf(e, e.Width, e.Depth) is var lateral ? (lateral.IStrong + lateral.IWeak) / 2 : 0;
                break;
            case ElementTypes.Wall when mat.Kind == AnalyticalMaterialKind.Glass:
            case ElementTypes.CurtainWall:
                m.Role = AnalyticalRole.LoadOnly;
                m.Shape = e.Polygon2D.Count >= 3 ? AnalyticalShape.Polyline : AnalyticalShape.Segment;
                m.AxisStart = axisA; m.AxisEnd = axisB;
                m.Axis = e.Polygon2D.Count >= 3 ? m.Outline : [axisA, axisB];
                m.AxisClosed = e.Polygon2D.Count >= 3;
                panel!.IsVertical = true;
                panel.Thickness = e.Depth;
                // Светопрозрачный фасад: 0.5 кПа площади (стекло, импосты, ригели).
                m.SelfD = 0.5 * Geometry2D.Perimeter(m.Axis, m.AxisClosed) * h * StructuralNorms.GammaDead;
                break;
            case ElementTypes.Wall:
            {
                m.Role = AnalyticalRole.Vertical;
                // Явно ненесущая перегородка («Несущая конструкция» = нет): передаёт вниз только собственный вес и не служит
                // опорой плитам, балкам и стойкам выше. Без признака (эталонные сцены) — несущая, как прежде.
                m.Bearing = e.UserParameters is null || StructuralRole.IsLoadBearing(e);
                m.Shape = AnalyticalShape.Segment;
                m.AxisStart = axisA; m.AxisEnd = axisB;
                m.HalfT = d / 2;
                m.ThickStruct = MaterialLayers.StructuralThickness(e);
                panel!.IsVertical = true;
                panel.Thickness = m.ThickStruct > 0 ? m.ThickStruct : e.Depth;
                double openArea = (wallOpenings ?? []).Sum(o => Math.Min(o.Width, w) * Math.Min(o.Height, h));
                double area = Math.Max(0, w * h - openArea);
                m.SelfD = e.Layers.Count > 0
                    ? area * e.Layers.Sum(l => l.WeightKPa * (l.Role == MaterialLayerRole.Structural ? StructuralNorms.GammaDead : StructuralNorms.GammaFinish))
                    : area * d * mat.Density * G * StructuralNorms.GammaDead;
                m.LateralInertia = m.ThickStruct * Math.Pow(e.Width, 3) / 12;
                break;
            }
            case ElementTypes.Core:
            {
                m.Role = AnalyticalRole.Vertical;
                m.Bearing = true;
                m.Shape = AnalyticalShape.Box;
                double t = CoreThickness(e);
                m.WallT = t;
                double inertia = BoxInertia(e), side = Math.Min(e.Width, e.Depth);
                bar!.Section = new AnalyticalSection(e.Width * e.Depth - Math.Max(0, e.Width - 2 * t) * Math.Max(0, e.Depth - 2 * t),
                    inertia, inertia, Math.Max(e.Width, e.Depth), side, 2 * t * side);
                bar.SectionB = e.Width;
                bar.SectionH = e.Depth;
                // Кручение замкнутой тонкостенной коробки (формула Бредта): J = 4·Am²·t / периметр средней линии.
                double am = Math.Max(0, e.Width - t) * Math.Max(0, e.Depth - t), loop = 2 * (Math.Max(0, e.Width - t) + Math.Max(0, e.Depth - t));
                bar.J = loop > 0 ? 4 * am * am * t / loop : 0;
                m.SelfD = (w * d - Math.Max(0, w - 2 * t) * Math.Max(0, d - 2 * t)) * h * mat.Density * G * StructuralNorms.GammaDead;
                m.LateralInertia = inertia;
                break;
            }
            case ElementTypes.Diagrid:
            {
                m.Role = AnalyticalRole.Vertical;
                m.Bearing = true;
                m.Shape = AnalyticalShape.Ring;
                var group = _groups.FirstOrDefault(g => g.Id == source.BuildingGroupId);
                var center = (group?.OriginX ?? 0, group?.OriginZ ?? 0);
                double r0 = Math.Max(1, Math.Sqrt((source.X - center.Item1) * (source.X - center.Item1) + (source.Z - center.Item2) * (source.Z - center.Item2)));
                m.Center = center;
                m.Radius = r0 * e.TaperScalePerFloor;
                m.Outline = Enumerable.Range(0, 16).Select(i => 2 * Math.PI * i / 16)
                    .Select(a => (m.Center.X + m.Radius * Math.Cos(a), m.Center.Z + m.Radius * Math.Sin(a))).ToList();
                if (Math.Max(1, source.RepeatLevels) > 1)
                {
                    m.Base = e.BaseY;
                    m.Top = e.BaseY + Math.Max(0.5, source.LevelHeight);
                }
                bar!.BarCount = DiagridMembers;
                bar.Section = PipeOf(e);
                bar.SectionB = e.Width;
                bar.SectionH = e.Depth;
                bar.J = 2 * bar.Section.IStrong;
                var (memberLength, _) = bar.InclinedBar();
                m.SelfD = DiagridMembers * bar.Section.A * memberLength * mat.Density * G * StructuralNorms.GammaDead;
                m.LateralInertia = DiagridMembers * bar.Section.A * m.Radius * m.Radius / 2;
                break;
            }
            case ElementTypes.Beam:
            case ElementTypes.Truss:
            {
                m.Role = AnalyticalRole.Line;
                m.Bearing = true;
                if (e.Polygon2D.Count >= 3 && e.Category == BimCategory.Beam)
                {
                    m.Shape = AnalyticalShape.Polyline;
                    m.Axis = m.Outline;
                    m.AxisClosed = true;
                    m.HalfT = 0.3;
                }
                else
                {
                    m.Shape = AnalyticalShape.Segment;
                    m.AxisStart = axisA; m.AxisEnd = axisB;
                    m.Axis = [axisA, axisB];
                    m.HalfT = d / 2;
                }
                bar!.SectionB = e.Depth;
                bar.SectionH = e.Height;
                bool truss = e.Category == BimCategory.Truss;
                // Ферма и кольцевая балка проверяются по своим формулам — сечение прокатного профиля у них не строится.
                if (!truss && m.Shape != AnalyticalShape.Polyline)
                {
                    bar.Section = SectionOf(e, d, h);
                    bar.BendingInertia = bar.Section.IStrong;
                    bar.J = TorsionOf(e, d, h, bar.Section);
                }
                else if (truss)
                    bar.BendingInertia = 2 * bar.ChordArea * Math.Pow(Math.Max(0.1, e.Height - Math.Min(0.25, e.Height * 0.15)) / 2, 2);
                else bar.BendingInertia = 0.6 * Math.Pow(e.Height, 3) / 12;
                double length = Geometry2D.Perimeter(m.Axis, m.AxisClosed);
                bar.AxisLength = length;
                double area = truss ? bar.ChordArea * 2.6 : m.Shape == AnalyticalShape.Polyline ? 0.6 * h : bar.Section.A;
                m.SelfD = area * length * mat.Density * G * StructuralNorms.GammaDead;
                break;
            }
            case ElementTypes.Slab:
            case ElementTypes.CantileverSlab:
            case ElementTypes.Roof:
            case ElementTypes.Terrace:
            {
                bool flat = e.Category != BimCategory.Roof || e.IsFlatRoof;
                if (e.Category == BimCategory.Terrace && mat.Kind != AnalyticalMaterialKind.Concrete)
                {
                    m.Role = AnalyticalRole.LoadOnly;
                    m.Shape = AnalyticalShape.Polygon;
                    m.SelfD = e.PlanArea() * (0.6 + s.LiveLoadKPa) * StructuralNorms.GammaDead;
                    break;
                }
                m.Role = AnalyticalRole.Slab;
                m.Shape = AnalyticalShape.Polygon;
                m.ThickStruct = flat ? MaterialLayers.StructuralThickness(e) : e.Layers.Where(l => l.Role == MaterialLayerRole.Structural).Sum(l => l.ThicknessM);
                m.Checked = flat || m.ThickStruct > 0.02;
                panel!.Thickness = m.ThickStruct > 0 ? m.ThickStruct : e.Height;
                break;
            }
            case ElementTypes.Foundation:
                m.Role = AnalyticalRole.Foundation;
                m.Shape = AnalyticalShape.Polygon;
                m.ThickStruct = MaterialLayers.StructuralThickness(e);
                panel!.Thickness = m.ThickStruct > 0 ? m.ThickStruct : e.Height;
                break;
            case ElementTypes.Shell:
                m.Role = AnalyticalRole.Shell;
                m.Shape = AnalyticalShape.Polygon;
                m.ThickStruct = 0.15;
                panel!.Thickness = m.ThickStruct;
                panel.SupportRing = ShellRing(e);
                m.SelfD = ShellArea(e) * m.ThickStruct * mat.Density * G * StructuralNorms.GammaDead;
                break;
            case ElementTypes.Stairs:
                m.Role = AnalyticalRole.LoadOnly;
                m.Shape = AnalyticalShape.Polygon;
                m.SelfD = (w * d * h / 2 * 2500 * G + w * d * 0.5) * StructuralNorms.GammaDead + w * d * 3.0 * StructuralNorms.GammaLive;
                break;
            case ElementTypes.Pool:
                m.Role = AnalyticalRole.LoadOnly;
                m.Shape = AnalyticalShape.Polygon;
                m.SelfD = (2 * (w + d) * h * 0.2 + w * d * 0.2) * 2500 * G * StructuralNorms.GammaDead + Math.Max(0, w - 0.4) * Math.Max(0, d - 0.4) * Math.Max(0, h - 0.4) * 10;
                break;
            default:
                return null;
        }
        m.SelfN = m.SelfD / StructuralNorms.GammaDead;
        var (minX, maxX, minZ, maxZ) = Geometry2D.Bounds(m.Outline);
        double pad = m.Shape is AnalyticalShape.Segment or AnalyticalShape.Polyline ? m.HalfT : 0;
        m.MinX = minX - pad; m.MaxX = maxX + pad; m.MinZ = minZ - pad; m.MaxZ = maxZ + pad;
        m.Poly = m.Outline.ToArray();
        m.SetDistanceBounds();
        return m;
    }

    /// <summary>Ось стены или балки в плане: от −W/2 до +W/2 вдоль локальной X.</summary>
    private static ((double X, double Z) A, (double X, double Z) B) AxisOf(ArchitecturalElement e)
    {
        var (ax, az) = ArchitecturalElement.RotateY(-e.Width / 2, 0, e.RotationY);
        var (bx, bz) = ArchitecturalElement.RotateY(e.Width / 2, 0, e.RotationY);
        return ((e.X + ax, e.Z + az), (e.X + bx, e.Z + bz));
    }

    private static double CoreThickness(ArchitecturalElement e) => Math.Clamp(Math.Min(e.Width, e.Depth) * 0.06, 0.25, 0.5);

    private static double BoxInertia(ArchitecturalElement e)
    {
        double t = CoreThickness(e), w = e.Width, d = e.Depth;
        double wi = Math.Max(0, w - 2 * t), di = Math.Max(0, d - 2 * t);
        return ((w * Math.Pow(d, 3) - wi * Math.Pow(di, 3)) / 12 + (d * Math.Pow(w, 3) - di * Math.Pow(wi, 3)) / 12) / 2;
    }

    private static double ShellArea(ArchitecturalElement e)
    {
        const double p = 1.6075;
        double a = e.Width / 2, b = e.Depth / 2, c = e.Height;
        return 4 * Math.PI * Math.Pow((Math.Pow(a * b, p) + Math.Pow(a * c, p) + Math.Pow(b * c, p)) / 3, 1 / p) / 2;
    }

    /// <summary>Контур опирания купола: 32 точки эллипса W × D с поворотом элемента.</summary>
    private static List<(double X, double Z)> ShellRing(ArchitecturalElement e) =>
        Enumerable.Range(0, 32).Select(i => 2 * Math.PI * i / 32).Select(a =>
        {
            var (x, z) = ArchitecturalElement.RotateY(e.Width / 2 * Math.Cos(a), e.Depth / 2 * Math.Sin(a), e.RotationY);
            return (e.X + x, e.Z + z);
        }).ToList();

    /// <summary>
    /// Материал элемента: по несущему слою пирога, иначе по строке Material; характеристики сертификата элемента (R, E, ρ из
    /// «Свойств пользователя») заменяют табличные. Без пользовательских параметров — одна проверка на null.
    /// </summary>
    private AnalyticalMaterial MatOf(ArchitecturalElement e)
    {
        var mat = BaseMaterialOf(e);
        if (e.UserParameters is null) return mat;
        var (strength, modulus, density) = MaterialCertificate.Read(e);
        if (double.IsNaN(strength) && double.IsNaN(modulus) && double.IsNaN(density)) return mat;
        double rc = double.IsNaN(strength) ? mat.Rc : strength * 1000;
        return mat with
        {
            Rc = rc,
            Rbend = mat.Kind is AnalyticalMaterialKind.Steel && !double.IsNaN(strength) ? rc : mat.Rbend,
            E = double.IsNaN(modulus) ? mat.E : modulus * 1000,
            Density = double.IsNaN(density) ? mat.Density : density
        };
    }

    private AnalyticalMaterial BaseMaterialOf(ArchitecturalElement e)
    {
        string m = (e.Material ?? string.Empty).ToLowerInvariant();
        if (e.Layers.FirstOrDefault(l => l.Role == MaterialLayerRole.Structural) is { } layer) m = layer.Name.ToLowerInvariant();
        var concrete = new AnalyticalMaterial(AnalyticalMaterialKind.Concrete, _concrete.Eb * 1000, _concrete.Rb * 1000, _concrete.Rbt * 1000, 0, _concreteDensity);
        if (e.Category is BimCategory.Diagrid or BimCategory.Truss || m.Contains("steel") || m.Contains("metal") || m.Contains("стал"))
            return new AnalyticalMaterial(AnalyticalMaterialKind.Steel, _steel.E * 1000, _steel.Ry * 1000, 0, _steel.Ry * 1000, _steel.Density);
        if (m.Contains("wood") || m.Contains("timber") || m.Contains("дерев") || m.Contains("брус"))
            // Сосна 2 сорта (СП 64.13330): сжатие и изгиб 13 МПа, E = 10 000 МПа; стены из бруса — смятие поперёк волокон 3 МПа.
            return new AnalyticalMaterial(AnalyticalMaterialKind.Timber, 10e6, e.Category == BimCategory.Wall ? 3e3 : 13e3, 0, 13e3, 500);
        if (m.Contains("glass") || m.Contains("стекл")) return new AnalyticalMaterial(AnalyticalMaterialKind.Glass, 70e6, 0, 0, 0, 2500);
        if (m.Contains("brick") || m.Contains("кирпич")) return new AnalyticalMaterial(AnalyticalMaterialKind.Masonry, 2.7e6, 1.5e3, 0, 0, 1800);
        if (m.Contains("block") || m.Contains("газо") || m.Contains("aerated") || m.Contains("блок"))
            return new AnalyticalMaterial(AnalyticalMaterialKind.Masonry, 1.6e6, 1.9e3, 0, 0, e.Layers.Count > 0 ? 700 : 900);
        return concrete;
    }

    // ───────────── Сечения ─────────────

    /// <summary>Геометрия сечения b × h по профилю: площадь, моменты инерции, высоты для моментов сопротивления.</summary>
    private static AnalyticalSection SectionOf(ArchitecturalElement e, double b, double h)
    {
        var (t, tw) = e.ProfileThicknesses(b, h);
        switch (e.EffectiveProfile)
        {
            case ProfileTypes.Circle:
            {
                double a = b / 2, c = h / 2;
                double ih = Math.PI * a * c * c * c / 4, ib = Math.PI * c * a * a * a / 4;
                return new AnalyticalSection(Math.PI * a * c, Math.Max(ih, ib), Math.Min(ih, ib), Math.Max(b, h), Math.Min(b, h), Math.PI * a * c * 0.9);
            }
            case ProfileTypes.Pipe:
            {
                double r = Math.Min(b, h) / 2, ri = Math.Max(0, r - t);
                double area = Math.PI * (r * r - ri * ri), i = Math.PI * (Math.Pow(r, 4) - Math.Pow(ri, 4)) / 4;
                return new AnalyticalSection(area, i, i, 2 * r, 2 * r, area / 2);
            }
            case ProfileTypes.IBeam:
            {
                double area = 2 * b * t + Math.Max(0, h - 2 * t) * tw;
                double strong = (b * h * h * h - (b - tw) * Math.Pow(Math.Max(0, h - 2 * t), 3)) / 12;
                double weak = 2 * t * b * b * b / 12 + Math.Max(0, h - 2 * t) * tw * tw * tw / 12;
                return new AnalyticalSection(area, strong, weak, h, b, h * tw);
            }
            case ProfileTypes.Polygon when e.Polygon2D.Count >= 3:
            {
                double area = e.PlanArea(), i = area * area / (4 * Math.PI), side = Math.Sqrt(area);
                return new AnalyticalSection(area, i, i, side, side, area);
            }
            default:
            {
                double strong = b * h * h * h / 12, weak = h * b * b * b / 12;
                return strong >= weak
                    ? new AnalyticalSection(b * h, strong, weak, h, b, b * h)
                    : new AnalyticalSection(b * h, weak, strong, b, h, b * h);
            }
        }
    }

    /// <summary>
    /// Момент инерции при кручении: эллипс πa³c³/(a² + c²), труба π(r⁴ − r_i⁴)/2, двутавр Σb·t³/3, многоугольник — как круг той же
    /// площади, прямоугольник — β·a·b³ (β = 1/3·(1 − 0.63·b/a + 0.052·(b/a)⁵), a ≥ b).
    /// </summary>
    private static double TorsionOf(ArchitecturalElement e, double b, double h, AnalyticalSection section)
    {
        var (t, tw) = e.ProfileThicknesses(b, h);
        switch (e.EffectiveProfile)
        {
            case ProfileTypes.Circle:
            {
                double a = b / 2, c = h / 2;
                return Math.PI * a * a * a * c * c * c / (a * a + c * c);
            }
            case ProfileTypes.Pipe:
            {
                double r = Math.Min(b, h) / 2, ri = Math.Max(0, r - t);
                return Math.PI * (Math.Pow(r, 4) - Math.Pow(ri, 4)) / 2;
            }
            case ProfileTypes.IBeam:
                return (2 * b * t * t * t + Math.Max(0, h - 2 * t) * tw * tw * tw) / 3;
            case ProfileTypes.Polygon when e.Polygon2D.Count >= 3:
                return section.A * section.A / (2 * Math.PI);
            default:
            {
                double longSide = Math.Max(b, h), shortSide = Math.Min(b, h), ratio = shortSide / longSide;
                return (1 - 0.63 * ratio + 0.052 * Math.Pow(ratio, 5)) / 3 * longSide * shortSide * shortSide * shortSide;
            }
        }
    }

    private static AnalyticalSection PipeOf(ArchitecturalElement e)
    {
        double dia = Math.Max(0.05, Math.Max(e.Width, e.Depth));
        double t = e.ProfileWallThickness > 0 ? e.ProfileWallThickness : Math.Max(0.006, dia * 0.08);
        double r = dia / 2, ri = Math.Max(0, r - t);
        double area = Math.PI * (r * r - ri * ri), i = Math.PI * (Math.Pow(r, 4) - Math.Pow(ri, 4)) / 4;
        return new AnalyticalSection(area, i, i, dia, dia, area / 2);
    }

    // ───────────── Узлы ─────────────

    /// <summary>
    /// Узлы схемы: колонна, ядро, диагрид — центр сечения на отметках низа и верха; балка — торцы оси (кольцевая — вершины
    /// контура) на отметке верха; стена и витраж — четыре угла срединной плоскости; плита — вершины контура на отметке верха;
    /// фундамент, купол, лестница и бассейн — на отметке низа. Узлы на грунте и под фундаментной плитой — жёсткие опоры.
    /// </summary>
    private void BuildNodes()
    {
        var model = _model;
        foreach (var m in model.Elements)
        {
            switch (m)
            {
                case AnalyticalMember { Role: AnalyticalRole.Line } beam when beam.Shape == AnalyticalShape.Polyline:
                    beam.Path = beam.Axis.Select(p => model.NodeAt(p.X, beam.Top, p.Z)).ToList();
                    beam.StartNode = beam.Path.Count > 0 ? beam.Path[0] : model.NodeAt(beam.Center.X, beam.Top, beam.Center.Z);
                    beam.EndNode = beam.AxisClosed ? beam.StartNode : beam.Path[^1];
                    break;
                case AnalyticalMember { Role: AnalyticalRole.Line } beam:
                    beam.StartNode = model.NodeAt(beam.AxisStart.X, beam.Top, beam.AxisStart.Z);
                    beam.EndNode = model.NodeAt(beam.AxisEnd.X, beam.Top, beam.AxisEnd.Z);
                    break;
                case AnalyticalMember stand:
                    stand.StartNode = model.NodeAt(stand.Center.X, stand.Base, stand.Center.Z);
                    stand.EndNode = model.NodeAt(stand.Center.X, stand.Top, stand.Center.Z);
                    break;
                case AnalyticalPanel { IsVertical: true } wall:
                {
                    var axis = wall.Shape == AnalyticalShape.Segment ? new List<(double X, double Z)> { wall.AxisStart, wall.AxisEnd } : wall.Axis;
                    var contour = axis.Select(p => model.NodeAt(p.X, wall.Base, p.Z)).ToList();
                    for (int i = axis.Count - 1; i >= 0; i--) contour.Add(model.NodeAt(axis[i].X, wall.Top, axis[i].Z));
                    wall.Contour = contour;
                    break;
                }
                case AnalyticalPanel panel:
                {
                    double y = panel.Role == AnalyticalRole.Slab ? panel.Top : panel.Base;
                    var loop = panel.SupportRing ?? panel.Outline;
                    var contour = loop.Select(p => model.NodeAt(p.X, y, p.Z)).ToList();
                    if (panel.Role == AnalyticalRole.Foundation)
                        foreach (var node in contour) node.Restraints = NodeDof.All;
                    panel.Contour = contour;
                    break;
                }
            }
        }
        foreach (var node in model.Nodes)
            if (node.Y <= AnalyticalModel.GroundLevel) node.Restraints = NodeDof.All;
    }

    // ───────────── Нагрузки площадных элементов ─────────────

    /// <summary>Перекрытие открыто сверху (кровля, эксплуатируемая кровля стилобата) — снеговая нагрузка вместо полезной.</summary>
    private bool IsExposed(AnalyticalElement m)
    {
        if (m.Category == BimCategory.Roof) return true;
        if (m.Role == AnalyticalRole.Foundation) return false;
        double area = Math.Max(1e-6, Geometry2D.Area(m.Outline));
        double covered = 0;
        foreach (var o in _model.Elements)
        {
            if (o == m || o.Role is not (AnalyticalRole.Slab or AnalyticalRole.Shell) || o.Base < m.Top + 0.3 || !o.Overlaps(m, 0)) continue;
            covered += Geometry2D.IntersectionArea(m.Outline, o.Outline);
            if (covered > 0.3 * area) return false;
        }
        return true;
    }

    /// <summary>Собственный вес 1 м² плиты: по слоям пирога (несущий γf = 1.1, прочие 1.3) или по толщине и плотности.</summary>
    private static (double D, double N) AreaSelf(AnalyticalPanel m)
    {
        var e = m.Instance;
        if (e.Layers.Count > 0)
        {
            double n = e.Layers.Sum(l => l.WeightKPa);
            double d = e.Layers.Sum(l => l.WeightKPa * (l.Role == MaterialLayerRole.Structural ? StructuralNorms.GammaDead : StructuralNorms.GammaFinish));
            return (d, n);
        }
        if (e.Category == BimCategory.Roof && !e.IsFlatRoof && RoofShapes.IsHollow(RoofShapes.Normalize(e.RoofShape)) &&
            (e.Material ?? string.Empty).Contains("concrete", StringComparison.OrdinalIgnoreCase))
        {
            // Полая ж/б кровля: вес ската толщиной t на единицу площади плана (с учётом уклона).
            double run0 = RoofShapes.IsShedLike(RoofShapes.Normalize(e.RoofShape)) ? Math.Min(e.Width, e.Depth) : Math.Min(e.Width, e.Depth) / 2;
            double slope = Math.Sqrt(1 + Math.Pow(e.Height / Math.Max(0.1, run0), 2));
            double sn = RoofDesign.SlabThickness(e) * slope * m.Material.Density * G;
            return (sn * StructuralNorms.GammaDead, sn);
        }
        if (e.Category == BimCategory.Roof && !e.IsFlatRoof)
        {
            // Скатная кровля без пирога: кровельное покрытие со стропильной системой (0.5 кПа; черепица — 0.9 кПа).
            double cover = (e.Material ?? string.Empty).Contains("tile", StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.5;
            return (cover * StructuralNorms.GammaFinish, cover);
        }
        double wn = Math.Max(0.01, e.Height) * m.Material.Density * G;
        return (wn * StructuralNorms.GammaDead, wn);
    }

    /// <summary>Коэффициент μ перехода к снеговой нагрузке на покрытие: 1 при уклоне до 30°, 0 — от 60°.</summary>
    private static double SnowMu(ArchitecturalElement e)
    {
        if (e.Category != BimCategory.Roof || e.IsFlatRoof) return 1;
        double run = RoofShapes.IsShedLike(RoofShapes.Normalize(e.RoofShape)) ? Math.Min(e.Width, e.Depth) : Math.Min(e.Width, e.Depth) / 2;
        double angle = Math.Atan2(e.Height, Math.Max(0.1, run)) * 180 / Math.PI;
        return angle <= 30 ? 1 : angle >= 60 ? 0 : (60 - angle) / 30;
    }

    // ───────────── Связи и грузовые площади (параллельно) ─────────────

    /// <summary>
    /// Геометрическая часть расчёта, не зависящая от нагрузок сверху: у плит — квадратурная сетка, опоры, доли грузовых площадей,
    /// пролёт и вылет консоли (<see cref="PanelTributary"/>), у балок — опоры, точки оси и пролёты, у фундаментов — квадратурные
    /// точки, у стоек, ненесущих элементов и оболочек — точки передачи нагрузки вниз и их получатели. Каждый элемент
    /// обрабатывается одним потоком (пишет только в себя), поэтому схема не зависит от числа ядер и совпадает до бита.
    /// </summary>
    private void PlanAreas()
    {
        var work = _model.Elements
            .OrderByDescending(m => m.Role == AnalyticalRole.Slab ? SampleBudget(m) : 0)
            .ToArray();
        if (work.Length == 0) return;
        void Plan(AnalyticalElement m)
        {
            switch (m.Role)
            {
                case AnalyticalRole.Slab: PlanSlab((AnalyticalPanel)m); break;
                case AnalyticalRole.Foundation: m.Samples = SampleArea(m, FoundationSamples); break;
                case AnalyticalRole.Line: PlanLine((AnalyticalMember)m); break;
                default: PlanTransfer(m); break;
            }
        }
        // Мелкие сцены — без накладных расходов пула; крупные — по одному элементу на поток, самые тяжёлые плиты первыми.
        if (!_parallel || work.Length < 8)
            foreach (var m in work) Plan(m);
        else
            try
            {
                Parallel.ForEach(Partitioner.Create(0, work.Length, 1), range =>
                {
                    for (int i = range.Item1; i < range.Item2; i++) Plan(work[i]);
                });
            }
            catch (AggregateException ex)
            {
                // Как и последовательный расчёт — наружу первое исключение (без обёртки), фильтры вызывающих не меняются.
                ExceptionDispatchInfo.Throw(ex.Flatten().InnerExceptions[0]);
            }
        var phases = _model.Profile.Phases;
        foreach (var m in work)
            if (m is AnalyticalPanel { Tributary: { } plan })
            {
                phases["плиты: сетка"] = phases.GetValueOrDefault("плиты: сетка") + plan.GridMs;
                phases["плиты: опоры"] = phases.GetValueOrDefault("плиты: опоры") + plan.SupportsMs;
                phases["плиты: площади"] = phases.GetValueOrDefault("плиты: площади") + plan.AreasMs;
            }
    }

    /// <summary>Точки передачи нагрузки вниз и их получатели — для стоек, ненесущих элементов и оболочек.</summary>
    private void PlanTransfer(AnalyticalElement m)
    {
        if (m.Role == AnalyticalRole.LoadOnly && m.Shape is not (AnalyticalShape.Segment or AnalyticalShape.Polyline)) m.Samples = SampleArea(m, LoadOnlySamples);
        var points = TransferPointsOf(m);
        if (points is null) return;
        m.TransferPoints = points;
        m.Receivers = _model.ReceiversOf(m, points.Select(p => (p.X, p.Z)));
    }

    /// <summary>
    /// Статические точки передачи нагрузки вниз с весами: колонна — центр, стена — ось с шагом 0.5 м, ядро — контур коробки
    /// с шагом 0.75 м, диагрид — 16 узлов кольца; витраж — ось с шагом 1 м (вес — длина участка), площадка — квадратурные
    /// точки (вес — площадь); купол — 32 точки контура опирания.
    /// </summary>
    private static List<(double X, double Z, double W)>? TransferPointsOf(AnalyticalElement m) => m.Role switch
    {
        AnalyticalRole.Vertical => m.Shape switch
        {
            AnalyticalShape.Segment => Geometry2D.Resample([m.AxisStart, m.AxisEnd], 0.5, false).Select(p => (p.X, p.Z, p.Length)).ToList(),
            AnalyticalShape.Box => Geometry2D.Resample(m.Outline, 0.75, true).Select(p => (p.X, p.Z, p.Length)).ToList(),
            AnalyticalShape.Ring => m.Outline.Select(p => (p.X, p.Z, 1.0)).ToList(),
            _ => [(m.Center.X, m.Center.Z, 1.0)]
        },
        AnalyticalRole.LoadOnly => m.Shape is AnalyticalShape.Segment or AnalyticalShape.Polyline
            ? Geometry2D.Resample(m.Axis, 1.0, m.AxisClosed).Select(p => (p.X, p.Z, p.Length)).ToList()
            : (m.Samples ?? SampleArea(m, LoadOnlySamples)).Select(p => (p.X, p.Z, p.A)).ToList(),
        AnalyticalRole.Shell => ((AnalyticalPanel)m).SupportRing!.Select(p => (p.X, p.Z, 1.0)).ToList(),
        _ => null
    };

    /// <summary>Число точек сетки плиты: поэтажные экземпляры параметрической плиты однотипны — достаточно половинной плотности.</summary>
    private int SampleBudget(AnalyticalElement m) => m.Source.RepeatLevels > 4 ? Math.Max(90, _slabSamples / 2) : _slabSamples;

    /// <summary>
    /// Квадратурная сетка плиты: ячейки целиком внутри — точка в центре с полной площадью; ячейки на границе контура или
    /// отверстия делятся на 3 × 3 подъячейки — вес равен доле площади внутри, точка — центр тяжести этой доли.
    /// </summary>
    private static List<(double X, double Z, double A)> SampleArea(AnalyticalElement m, int maxSamples = 220)
    {
        double area = Geometry2D.Area(m.Outline);
        double step = Math.Max(0.2, Math.Sqrt(Math.Max(0.04, area) / maxSamples));
        var (minX, maxX, minZ, maxZ) = Geometry2D.Bounds(m.Outline);
        int nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / step)), nz = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / step));
        // Углы ячеек — плоским массивом (строка i, столбец j): без многомерного индексатора в горячем цикле.
        int row = nz + 1;
        var corner = new bool[(nx + 1) * row];
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
                corner[i * row + j] = AnalyticalGeometry.InsideNet(m, minX + i * step, minZ + j * step);

        var pts = new List<(double X, double Z, double A)>(nx * nz);
        double cellArea = step * step;
        bool holes = m.HoleArr.Count > 0;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                double x0 = minX + i * step, z0 = minZ + j * step;
                int c = i * row + j;
                int corners = (corner[c] ? 1 : 0) + (corner[c + row] ? 1 : 0) + (corner[c + 1] ? 1 : 0) + (corner[c + row + 1] ? 1 : 0);
                // Центр проверяется только там, где внутри ячейки может оказаться отверстие или выступ контура.
                if (corners == 4 && (!holes || AnalyticalGeometry.InsideNet(m, x0 + step / 2, z0 + step / 2)))
                {
                    pts.Add((x0 + step / 2, z0 + step / 2, cellArea));
                    continue;
                }
                if (corners == 0 && !AnalyticalGeometry.InsideNet(m, x0 + step / 2, z0 + step / 2)) continue;
                double sx = 0, sz = 0;
                int hits = 0;
                for (int u = 0; u < 3; u++)
                    for (int v = 0; v < 3; v++)
                    {
                        double x = x0 + (u + 0.5) * step / 3, z = z0 + (v + 0.5) * step / 3;
                        if (!AnalyticalGeometry.InsideNet(m, x, z)) continue;
                        sx += x;
                        sz += z;
                        hits++;
                    }
                if (hits > 0) pts.Add((sx / hits, sz / hits, cellArea * hits / 9));
            }
        return pts.Count == 0 ? [(m.Center.X, m.Center.Z, m.NetArea)] : pts;
    }

    /// <summary>Опоры плиты: вертикальные несущие, проходящие через отметку низа плиты, и балки под плитой (или в её толще).</summary>
    private List<AnalyticalElement> SlabSupports(AnalyticalElement slab)
    {
        var list = new List<AnalyticalElement>();
        foreach (var o in _model.Elements)
        {
            if (o == slab || !o.Bearing || !o.Overlaps(slab, 0.3)) continue;
            if (o.Role == AnalyticalRole.Vertical && o.Base < slab.Base + Tol - 0.02 && o.Top > slab.Base - Tol && !(o.Base >= slab.Top - 0.02))
            {
                if (SupportTouches(o, slab)) list.Add(o);
            }
            else if (o.Role == AnalyticalRole.Line && o.Base < slab.Base + 0.02 && o.Top >= slab.Base - Tol && o.Top <= slab.Top + 0.05)
            {
                if (SupportTouches(o, slab)) list.Add(o);
            }
        }
        return list;
    }

    /// <summary>Опора касается контура плиты: её сечение (или ось) ближе 0.3 м к контуру либо внутри него.</summary>
    private static bool SupportTouches(AnalyticalElement support, AnalyticalElement slab)
    {
        foreach (var (x, z) in support.Anchors().Append(support.Center))
            if (Geometry2D.Contains(slab.Outline, x, z) || Geometry2D.DistanceToBoundary(slab.Outline, x, z) < 0.3) return true;
        return false;
    }

    /// <summary>Ближайшая опора с отсечкой по габариту: опора, нижняя оценка которой не меньше текущего минимума, пропускается.</summary>
    private static int NearestPruned(AnalyticalElement[] supports, double x, double z, double[] dist, double[] lower, out double dMin)
    {
        int nearest = 0;
        dMin = double.MaxValue;
        for (int i = 0; i < supports.Length; i++)
        {
            double lb = supports[i].BoundDistance(x, z);
            lower[i] = lb;
            if (lb >= dMin)
            {
                dist[i] = double.NaN;
                continue;
            }
            double d = supports[i].DistanceTo(x, z);
            dist[i] = d;
            if (d < dMin) { dMin = d; nearest = i; }
        }
        return nearest;
    }

    /// <summary>
    /// Веса опор точки по расстояниям (результат — в dist[]): плавная граница зон Вороного шириной <see cref="TributaryBlend"/>.
    /// Опоры, отсечённые по габариту, заведомо дальше границы зоны — их вес 0 без точного расчёта; ближние досчитываются.
    /// true — точка делится между несколькими опорами.
    /// </summary>
    private static bool BlendWeights(AnalyticalElement[] supports, double x, double z, double dMin, double[] dist, double[] lower)
    {
        int count = 0;
        for (int i = 0; i < supports.Length; i++)
        {
            double d = dist[i];
            if (double.IsNaN(d))
            {
                if (lower[i] - dMin >= TributaryBlend)
                {
                    dist[i] = 0;
                    continue;
                }
                d = supports[i].DistanceTo(x, z);
            }
            dist[i] = d - dMin < TributaryBlend ? 1 - (d - dMin) / TributaryBlend : 0;
            if (dist[i] > 0) count++;
        }
        return count > 1;
    }

    /// <summary>
    /// Нагрузка на 1 м² плиты (собственный вес + полезная или снег с μ по уклону) и её распределение по грузовым площадям опор,
    /// пролёт и вылет консоли по максимуму непрерывного поля «расстояние до опоры».
    /// </summary>
    private void PlanSlab(AnalyticalPanel m)
    {
        var (selfD, selfN) = (m.AreaSelfD, m.AreaSelfN);
        double mu = SnowMu(m.Instance);
        double liveD = m.Exposed ? s.SnowKPa * mu : s.LiveLoadKPa * StructuralNorms.GammaLive;
        double liveN = m.Exposed ? 0.7 * s.SnowKPa * mu : s.LiveLoadKPa;
        double qd = selfD + liveD, qn = selfN + liveN;
        m.AreaLoadD = qd;
        m.AreaLoadN = qn;
        var clock = Stopwatch.StartNew();
        // Поэтажные экземпляры параметрической плиты однотипны — достаточно половинной плотности (максимум пролёта уточняется локально).
        int sampleCount = SampleBudget(m);
        var samples = SampleArea(m, sampleCount);
        double gridMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var supportList = SlabSupports(m);
        var plan = new PanelTributary { Samples = samples, Supports = supportList, GridMs = gridMs, SupportsMs = clock.Elapsed.TotalMilliseconds };
        m.Tributary = plan;
        clock.Restart();
        if (supportList.Count == 0)
        {
            // Плита без опор передаёт нагрузку точками сетки вниз — получатели точек находятся заранее.
            plan.Receivers = _model.ReceiversOf(m, samples.Select(p => (p.X, p.Z)));
            return;
        }

        var supports = supportList.ToArray();
        int count = supports.Length;
        var hull = Geometry2D.ConvexHull(supportList.SelectMany(sp => sp.Anchors())).ToArray();
        var disc = AnalyticalGeometry.InscribedDisc(hull);
        var reactD = new double[count];
        var reactN = new double[count];
        var lineLists = new List<TributaryLoad>?[count];
        var isLine = supports.Select(sp => sp.Role == AnalyticalRole.Line).ToArray();

        // Пролёт и вылет консоли в точке: внутри оболочки опор — расстояние до ближайшей опоры (непрерывное поле),
        // снаружи — вылет консоли.
        bool ProbeAt(double d, double x, double z, out double value)
        {
            value = d;
            if (hull.Length < 3) return false;
            if (AnalyticalGeometry.InDisc(disc, x, z) || AnalyticalGeometry.InsideConvex(hull, x, z)) return true;
            double outside = AnalyticalGeometry.DistBoundary(hull, x, z);
            if (outside < 0.05) return true;
            value = outside;
            return false;
        }

        double step = Math.Max(0.2, Math.Sqrt(Math.Max(0.04, Geometry2D.Area(m.Outline)) / sampleCount));
        var dist = new double[count];
        var lower = new double[count];
        var sub = new (double X, double Z)[4];
        // Наибольшее удаление от опор: [0] — вне коробок ядер, [1] — внутри шахты ядра (плита, опёртая по контуру стен).
        double span = 0, cantilever = 0, spanFactor = 1;
        var reach = new double[2];
        var reachAt = new (double X, double Z)[2];
        (double X, double Z) cantAt = default;
        var boxes = supportList.Where(sp => sp.Shape == AnalyticalShape.Box).Select(sp => sp.Poly).ToArray();
        bool InBox(double x, double z)
        {
            foreach (var b in boxes)
                if (AnalyticalGeometry.Pip(b, x, z)) return true;
            return false;
        }

        // Грузовая площадь точки по весам dist[] — на опоры с ненулевым весом (у балок — ещё и линейная нагрузка вдоль оси).
        void Share(double x, double z, double area)
        {
            double wSum = 0;
            for (int i = 0; i < count; i++) wSum += dist[i];
            for (int i = 0; i < count; i++)
            {
                if (dist[i] <= 0) continue;
                double d = qd * area * dist[i] / wSum, n = qn * area * dist[i] / wSum;
                reactD[i] += d;
                reactN[i] += n;
                if (isLine[i]) (lineLists[i] ??= []).Add(new TributaryLoad(x, z, d, n));
            }
        }

        foreach (var (x, z, a) in samples)
        {
            NearestPruned(supports, x, z, dist, lower, out double dMin);
            if (ProbeAt(dMin, x, z, out double value))
            {
                int k = InBox(x, z) ? 1 : 0;
                if (value > reach[k]) { reach[k] = value; reachAt[k] = (x, z); }
            }
            else if (value > cantilever) { cantilever = value; cantAt = (x, z); }
            // Грузовая площадь точки делится между опорами, почти равноудалёнными от неё (плавная граница зон Вороного
            // постоянной ширины) — доля нагрузки на балку или колонну не «прыгает» рядами точек при смене шага.
            if (!BlendWeights(supports, x, z, dMin, dist, lower) || a < 0.02)
            {
                Share(x, z, a);
                continue;
            }
            // Точка у границы зон нескольких опор — адаптивное уточнение 2 × 2: доли опор считаются по подточкам.
            double half = Math.Min(step, Math.Sqrt(a)) / 4;
            int inside = 0;
            for (int k = 0; k < 4; k++)
            {
                double sx = x + ((k & 1) == 0 ? -half : half), sz = z + ((k & 2) == 0 ? -half : half);
                if (AnalyticalGeometry.InsideNet(m, sx, sz)) sub[inside++] = (sx, sz);
            }
            if (inside == 0) { Share(x, z, a); continue; }
            for (int k = 0; k < inside; k++)
            {
                double sx = sub[k].X, sz = sub[k].Z;
                NearestPruned(supports, sx, sz, dist, lower, out double sMin);
                BlendWeights(supports, sx, sz, sMin, dist, lower);
                Share(sx, sz, a / inside);
            }
        }

        // Максимумы непрерывных полей — расстояния до ближайшей опоры внутри оболочки опор и вылета консоли снаружи —
        // уточняются мелкой сеткой вокруг лучших точек, поэтому не зависят от шага дискретизации.
        for (int pass = 0; pass < 2; pass++)
        {
            double h = step / (pass == 0 ? 2.5 : 7.0);
            for (int target = 0; target < 3; target++)
            {
                var (cx, cz) = target < 2 ? reachAt[target] : cantAt;
                if (target < 2 ? reach[target] <= 0 : cantilever <= 0) continue;
                for (int u = -2; u <= 2; u++)
                    for (int v = -2; v <= 2; v++)
                    {
                        double x = cx + u * h, z = cz + v * h;
                        if (!AnalyticalGeometry.InsideNet(m, x, z)) continue;
                        NearestPruned(supports, x, z, dist, lower, out double d);
                        bool interior = ProbeAt(d, x, z, out double value);
                        if (target < 2 && interior && (InBox(x, z) ? 1 : 0) == target && value > reach[target])
                        {
                            reach[target] = value;
                            reachAt[target] = (x, z);
                        }
                        if (target == 2 && !interior && value > cantilever) { cantilever = value; cantAt = (x, z); }
                    }
            }
        }
        // Самая удалённая от опор точка: равноудалена от трёх и более колонн — центр ячейки сетки колонн, пролёт по оси
        // ячейки L = √2·d; иначе (между балками, стенами, двумя колоннами по краю) — L = 2·d.
        // Плита, окружённая линейными опорами со всех сторон (комната, шахта ядра), работает в двух направлениях:
        // квадратная плита, опёртая по контуру, передаёт в каждое направление половину нагрузки (метод Маркуса).
        // Определяющий пролёт — с наибольшим k·L².
        for (int k = 0; k < 2; k++)
        {
            if (reach[k] <= 0) continue;
            double tolerance = 0.05 + 0.03 * reach[k];
            var around = supportList.Where(sp => sp.DistanceTo(reachAt[k].X, reachAt[k].Z) <= reach[k] + tolerance).ToList();
            bool columnCell = around.Count >= 3 && around.All(sp => sp.IsPointLike);
            double length = (columnCell ? Math.Sqrt(2) : 2) * reach[k];
            double factor = !columnCell && (k == 1 || Enclosed(reachAt[k], around)) ? 0.5 : 1;
            if (factor * length * length > spanFactor * span * span)
            {
                span = length;
                spanFactor = factor;
            }
        }
        plan.ReactD = reactD;
        plan.ReactN = reactN;
        plan.LineLoads = lineLists;
        plan.Span = span;
        plan.Cantilever = cantilever;
        plan.SpanFactor = spanFactor;
        plan.AreasMs = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Точка плиты окружена линейными опорами (стены, балки, ядро, кольцо диагрида): лежит внутри коробки ядра или
    /// направления на ближайшие линейные опоры охватывают её со всех сторон (наибольший угловой разрыв меньше 171°).
    /// Колонны край плиты между собой не поддерживают и в опирание по контуру не входят.
    /// </summary>
    private static bool Enclosed((double X, double Z) p, List<AnalyticalElement> around)
    {
        if (around.Any(sp => sp.Shape == AnalyticalShape.Box && AnalyticalGeometry.Pip(sp.Poly, p.X, p.Z))) return true;
        var angles = around.Where(sp => !sp.IsPointLike).Select(sp => sp.NearestPoint(p.X, p.Z))
            .Where(q => (q.X - p.X) * (q.X - p.X) + (q.Z - p.Z) * (q.Z - p.Z) > 1e-8)
            .Select(q => Math.Atan2(q.Z - p.Z, q.X - p.X)).Order().ToList();
        if (angles.Count < 3) return false;
        double maxGap = angles[0] + 2 * Math.PI - angles[^1];
        for (int i = 1; i < angles.Count; i++) maxGap = Math.Max(maxGap, angles[i] - angles[i - 1]);
        return maxGap < 0.95 * Math.PI;
    }

    // ───────────── Балки: опоры, дискретизация оси, пролёты ─────────────

    /// <summary>
    /// Ось балки делится на 16…240 участков (шаг ~0.1 м); опоры — вертикальные элементы, пересекающие её низ у оси, и более
    /// глубокие ригели — ставятся на ось (середина участка, ближайшего к сечению опоры), между соседними опорами — пролёты
    /// (у кольцевой балки — ещё замыкающий). Балка без опор передаёт нагрузку вниз точками оси с шагом 0.5 м.
    /// </summary>
    private void PlanLine(AnalyticalMember m)
    {
        double length = Math.Max(0.05, m.AxisLength);
        int count = Math.Clamp((int)(length / 0.1), 16, 240);
        var axis = Geometry2D.Resample(m.Axis, length / count, m.AxisClosed);
        var stations = axis.Select(p => new MemberStation(m.ArcPosition(p.X, p.Z), p.X, p.Z, p.Length)).ToList();
        m.Stations = stations;
        m.Supports = LineSupports(m);
        if (m.Supports.Count == 0)
        {
            m.TransferPoints = Geometry2D.Resample(m.Axis, 0.5, m.AxisClosed).Select(p => (p.X, p.Z, p.Length)).ToList();
            m.Receivers = _model.ReceiversOf(m, m.TransferPoints.Select(p => (p.X, p.Z)));
            return;
        }
        var sup = m.Supports.Select(sp => (Support: sp, S: SupportPosition(sp, stations))).OrderBy(x => x.S).ToList();
        var spans = new List<MemberSpan>();
        for (int i = 0; i + 1 < sup.Count; i++)
            if (sup[i + 1].S - sup[i].S > 0.05) spans.Add(new MemberSpan(sup[i].S, sup[i + 1].S, sup[i].Support, sup[i + 1].Support));
        if (m.AxisClosed && sup.Count >= 2) spans.Add(new MemberSpan(sup[^1].S, sup[0].S + length, sup[^1].Support, sup[0].Support));
        m.SupportStations = sup;
        m.Spans = spans;
    }

    /// <summary>Положение опоры на оси балки: середина участка оси, ближайшего к сечению опоры.</summary>
    private static double SupportPosition(AnalyticalElement support, List<MemberStation> axis)
    {
        double best = double.MaxValue;
        var distances = axis.Select(p => support.DistanceTo(p.X, p.Z)).ToList();
        foreach (var d in distances) best = Math.Min(best, d);
        var near = axis.Where((_, i) => distances[i] <= best + 0.02).Select(p => p.S).ToList();
        return near.Count > 0 ? near.Average() : axis[0].S;
    }

    /// <summary>Опоры балки: вертикальные элементы, пересекающие её низ у оси, и более глубокие балки-ригели.</summary>
    private List<AnalyticalElement> LineSupports(AnalyticalElement beam)
    {
        var list = new List<AnalyticalElement>();
        foreach (var o in _model.Elements)
        {
            if (o == beam || !o.Bearing || !o.Overlaps(beam, 0.3)) continue;
            bool vertical = o.Role == AnalyticalRole.Vertical && o.Base < beam.Base + 0.02 && o.Top > beam.Base - Tol;
            bool girder = o.Role == AnalyticalRole.Line && o.Base < beam.Base - 0.02 && o.Top >= beam.Base - 0.05;
            if (!vertical && !girder) continue;
            double reach = beam.HalfT + 0.15;
            bool near = o.Anchors().Append(o.Center).Any(p => DistanceToAxis(beam, p.X, p.Z) <= reach) ||
                        beam.Axis.Any(p => o.DistanceTo(p.X, p.Z) <= reach);
            if (near) list.Add(o);
        }
        return list;
    }

    private static double DistanceToAxis(AnalyticalElement m, double x, double z) => Geometry2D.DistanceToBoundary(m.Axis, x, z, m.AxisClosed);
}
