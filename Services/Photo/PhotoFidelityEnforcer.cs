using System.Globalization;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services.Photo;

/// <summary>Признаки формы BIM-модели для сверки со снимком.</summary>
public sealed record ModelTraits(
    int ArcElements, bool HasRoundedContour, int Tiers, bool HasDome, bool HasVault, bool HasGable, int NonOrthogonalWalls, double MinPlanAngle,
    int RoundColumns, int ColoredElements, int Elements, int Floors, double Width, double Depth, double Height, int InteriorColumnsInHall,
    int AlternatingArcs = 0, bool HasRingSlab = false, bool HasMansard = false, int InclinedPylons = 0, bool HasPodium = false)
{
    public string Describe() => string.Create(CultureInfo.GetCultureInfo("ru-RU"),
        $"дуг {ArcElements} (чередований {AlternatingArcs}), скругление {(HasRoundedContour ? "да" : "нет")}, ярусов {Tiers}, купол {(HasDome ? "да" : "нет")}, двор {(HasRingSlab ? "да" : "нет")}, " +
        $"свод {(HasVault ? "да" : "нет")}, косых стен {NonOrthogonalWalls} (мин. угол {MinPlanAngle:0} °), мансарда {(HasMansard ? "да" : "нет")}, стилобат {(HasPodium ? "да" : "нет")}, " +
        $"круглых колонн {RoundColumns}, наклонных пилонов {InclinedPylons}, в цвете {ColoredElements} из {Elements}, этажей {Floors}");
}

/// <summary>Итог сверки ответа модели со снимком: оценки до и после, пересборка, список наложенных исправлений.</summary>
public sealed record FidelityReport(double ScoreBefore, double ScoreAfter, bool Rebuilt, IReadOnlyList<string> Corrections, ModelTraits Before, ModelTraits After);

/// <summary>
/// Гарантия совпадения с картинкой: языковые модели часто возвращают упрощённую прямоугольную коробку. Сверка признаков ответа
/// (дуги и скругления, ярусы-уступы, купол, свод, косые стены и острый угол плана, колоннада, цвета) с морфологией оптического
/// паспорта снимка; оценка 0…1 по обязательным признакам морфологии. Ниже порога 0,75 — модель пересобирается Визуальным ядром
/// по паспорту с числами ответа модели (этажность, габариты, если они правдоподобны), выше — дополняются только цвета фасада.
/// </summary>
public static class PhotoFidelityEnforcer
{
    public const double AcceptScore = 0.75;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static ModelTraits Traits(IReadOnlyList<ArchitecturalElement> elements)
    {
        int arcs = elements.Count(e => e.Category is BimCategory.Wall or BimCategory.CurtainWall or BimCategory.Beam && CurvedDesign.IsArc(e));
        bool rounded = elements.Any(e => e.Category is BimCategory.Slab or BimCategory.Roof or BimCategory.Foundation && (CurvedDesign.CornerRadius(e) > 0.3 || HasCurvedPolygon(e)));

        // Ярусы — различные отметки верхних поверхностей (покрытий), шаг ≥ 2,5 м.
        var roofTops = elements.Where(e => e.Category == BimCategory.Roof && e.IsFlatRoof).Select(e => e.TopY).OrderBy(v => v).ToList();
        int tiers = roofTops.Aggregate(new List<double>(), (acc, v) => { if (acc.Count == 0 || v - acc[^1] >= 2.5) acc.Add(v); return acc; }).Count;
        bool dome = elements.Any(e => e.Category == BimCategory.Shell || e.Category == BimCategory.Roof && RoofShapes.Normalize(e.RoofShape) == RoofShapes.Dome);
        bool vault = elements.Any(e => e.Category == BimCategory.Roof && RoofShapes.Normalize(e.RoofShape) == RoofShapes.BarrelVault);
        bool gable = elements.Any(e => e.Category == BimCategory.Roof && RoofShapes.Normalize(e.RoofShape) is RoofShapes.Gable or RoofShapes.GableHollow or RoofShapes.Hip or RoofShapes.HipHollow);
        var walls = elements.Where(e => e.Category is BimCategory.Wall or BimCategory.CurtainWall && !CurvedDesign.IsArc(e) && e.Width > 3).ToList();
        int nonOrtho = walls.Count(w => Math.Abs(Math.IEEERemainder(w.RotationY, 90)) > 5);
        double minAngle = 180;
        foreach (var e in elements.Where(e => e.Category is BimCategory.Slab or BimCategory.Roof && e.Polygon2D.Count >= 3 && e.Polygon2D.Count <= 40))
            minAngle = Math.Min(minAngle, MinInteriorAngle(e.Polygon2D.Select(p => (p.X, p.Z)).ToList()));
        int round = elements.Count(e => e.Category == BimCategory.Column && e.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe);
        int colored = elements.Count(e => CurvedDesign.FacadeColor(e) is not null);
        int floors = elements.Where(e => e.Category is BimCategory.Slab or BimCategory.Roof).Select(e => e.LevelIndex).DefaultIfEmpty(0).Max() + 1;
        var outline = elements.Where(e => e.Category is not (BimCategory.Window or BimCategory.Door or BimCategory.WallOpening or BimCategory.SlabOpening))
            .SelectMany(e => ElementGeometryOutline(e)).ToList();
        double w = outline.Count > 0 ? outline.Max(p => p.X) - outline.Min(p => p.X) : 0, d = outline.Count > 0 ? outline.Max(p => p.Z) - outline.Min(p => p.Z) : 0;
        double h = elements.Count > 0 ? elements.Max(e => e.TopY) : 0;
        // Колонны внутри зала под сводом (большепролётное сооружение не должно их иметь).
        int inHall = 0;
        if (elements.FirstOrDefault(e => e.Category == BimCategory.Roof && RoofShapes.Normalize(e.RoofShape) == RoofShapes.BarrelVault) is { } v)
        {
            var (lx, lz) = (v.Width / 2 - 2.5, v.Depth / 2 - 2.5);
            inHall = elements.Count(e => e.Category == BimCategory.Column && Math.Abs(ArchitecturalElement.RotateY(e.X - v.X, e.Z - v.Z, -v.RotationY).X) < lx &&
                                         Math.Abs(ArchitecturalElement.RotateY(e.X - v.X, e.Z - v.Z, -v.RotationY).Z) < lz);
        }
        // Волна 4.1: чередование знака стрелы дуг фасада вдоль X на самом «дуговом» этаже (волна / S-фасад).
        int alternating = 0;
        var arcWalls = elements.Where(e => e.Category is BimCategory.Wall or BimCategory.CurtainWall && CurvedDesign.IsArc(e) && e.Width > 3).ToList();
        if (arcWalls.Count > 0)
        {
            int level = arcWalls.GroupBy(e => e.LevelIndex).MaxBy(g => g.Count())!.Key;
            // Знак «к зрителю» (−Z): стрела по локальной +Z, поворот стены — в мировую нормаль; дуги тыла не считаются.
            var front = arcWalls.Where(e => e.LevelIndex == level).Select(e =>
            {
                var (nx, nz) = ArchitecturalElement.RotateY(0, CurvedDesign.ArcBulge(e), e.RotationY);
                return (e.X, e.Z, Toward: -nz);
            }).OrderBy(p => p.X).ToList();
            double zMid = front.Count > 0 ? front.Average(p => p.Z) : 0;
            var signs = front.Where(p => p.Z <= zMid + 1e-6 && Math.Abs(p.Toward) > 0.05).Select(p => Math.Sign(p.Toward)).ToList();
            for (int i = 0; i < signs.Count; i++) if (i == 0 || signs[i] != signs[i - 1]) alternating++;
        }
        bool ringSlab = elements.Any(o => o.Category == BimCategory.SlabOpening && elements.FirstOrDefault(s => s.Id == o.HostElementId) is { } host &&
                                          o.Width * o.Depth > 0.12 * host.Width * host.Depth);
        bool mansard = elements.Any(e => e.Category == BimCategory.Roof && RoofShapes.Normalize(e.RoofShape) == RoofShapes.Mansard) ||
                       elements.Any(e => e.Name.Contains("мансард", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("mansard", StringComparison.OrdinalIgnoreCase));
        int pylons = elements.Count(e => e.Category is BimCategory.Column or BimCategory.Beam && e.Rotation.Length > 2 && (Math.Abs(e.Rotation[2]) > 3 || Math.Abs(e.Rotation[0]) > 3));
        // Стилобат: нижние перекрытия шире верхних на ≥ 15 %.
        var slabArea = elements.Where(e => e.Category == BimCategory.Slab || e.Category == BimCategory.Roof && e.IsFlatRoof)
            .GroupBy(e => e.LevelIndex).ToDictionary(g => g.Key, g => g.Sum(e => e.Width * e.Depth));
        bool podium = slabArea.Count >= 3 && slabArea.OrderBy(p => p.Key).Take(2).Max(p => p.Value) >= 1.15 * slabArea.OrderByDescending(p => p.Key).Take(2).Min(p => p.Value);
        return new ModelTraits(arcs, rounded, tiers, dome, vault, gable, nonOrtho, minAngle, round, colored, elements.Count, floors, w, d, h, inHall,
            alternating, ringSlab, mansard, pylons, podium);
    }

    /// <summary>
    /// Оценка совпадения с паспортом снимка (волна 4.1): обязательные признаки морфологии плюс то, что Визуальное ядро и модель со
    /// зрением увидели на снимке: волна из N дуг, стилобат, двор-атриум, мансарда, число ярусов.
    /// </summary>
    public static double Score(ModelTraits t, PhotoArchitectureDescriptor d, PhotoMorphology morphology)
    {
        double basic = Score(t, morphology);
        var extra = new List<bool>();
        switch (morphology)
        {
            case PhotoMorphology.Curved:
                if (d.FacadeArcs >= 2) extra.Add(t.AlternatingArcs >= Math.Min(3, d.FacadeArcs));
                if (d.PodiumFloors > 0) extra.Add(t.HasPodium);
                break;
            case PhotoMorphology.Rotunda when d.HasCourtyard:
                extra.Add(t.HasRingSlab);
                break;
            case PhotoMorphology.Triangular when d.Roof == PhotoRoofKind.Mansard:
                extra.Add(t.HasMansard);
                break;
            case PhotoMorphology.Cascading when d.Tiers.Count >= 3:
                extra.Add(t.Tiers >= Math.Min(d.Tiers.Count, 6));
                break;
            case PhotoMorphology.ArchSpan when d.InclinedPylons > 0:
                extra.Add(t.InclinedPylons >= Math.Min(4, d.InclinedPylons));
                break;
        }
        if (extra.Count == 0) return basic;
        // Базовая оценка (обязательные признаки + цвет) и дополнительные признаки — поровну.
        return (basic + (double)extra.Count(c => c) / extra.Count) / 2;
    }

    private static IEnumerable<(double X, double Z)> ElementGeometryOutline(ArchitecturalElement e) => AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(e);

    private static bool HasCurvedPolygon(ArchitecturalElement e) => e.Polygon2D.Count >= 12;

    private static double MinInteriorAngle(List<(double X, double Z)> poly)
    {
        double min = 180;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a = poly[(i - 1 + n) % n]; var b = poly[i]; var c = poly[(i + 1) % n];
            double ux = a.X - b.X, uz = a.Z - b.Z, vx = c.X - b.X, vz = c.Z - b.Z;
            double lu = Math.Sqrt(ux * ux + uz * uz), lv = Math.Sqrt(vx * vx + vz * vz);
            if (lu < 0.3 || lv < 0.3) continue;
            double ang = Math.Acos(Math.Clamp((ux * vx + uz * vz) / (lu * lv), -1, 1)) * 180 / Math.PI;
            if (ang < 179) min = Math.Min(min, ang);
        }
        return min;
    }

    /// <summary>Оценка совпадения формы 0…1 по обязательным признакам морфологии снимка (+ цвета).</summary>
    public static double Score(ModelTraits t, PhotoMorphology morphology)
    {
        var checks = new List<bool>();
        switch (morphology)
        {
            case PhotoMorphology.Curved:
                checks.Add(t.ArcElements >= 1);
                checks.Add(t.HasRoundedContour || t.ArcElements >= 2);
                break;
            case PhotoMorphology.Rounded:
                checks.Add(t.HasRoundedContour);
                break;
            case PhotoMorphology.Cascading:
                checks.Add(t.Tiers >= 3);
                break;
            case PhotoMorphology.Triangular:
                checks.Add(t.MinPlanAngle < 75);
                checks.Add(t.NonOrthogonalWalls >= 1);
                break;
            case PhotoMorphology.Rotunda:
                checks.Add(t.HasDome);
                checks.Add(t.ArcElements >= 4 || t.HasRoundedContour);
                checks.Add(t.RoundColumns >= 4);
                break;
            case PhotoMorphology.ArchSpan:
                checks.Add(t.HasVault);
                checks.Add(t.InteriorColumnsInHall == 0);
                break;
            case PhotoMorphology.Pitched:
                checks.Add(t.HasGable);
                break;
            default:
                checks.Add(t.Elements > 0);
                break;
        }
        checks.Add(t.ColoredElements >= t.Elements * 0.5);
        return checks.Count == 0 ? 1 : (double)checks.Count(c => c) / checks.Count;
    }

    /// <summary>
    /// Сверка и доводка: элементы ответа модели → либо те же элементы в цветах снимка, либо пересборка Визуальным ядром по паспорту
    /// с этажностью и габаритами модели. Возвращает итоговую модель и отчёт.
    /// </summary>
    public static (PhotoBimModel Model, FidelityReport Report) Enforce(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups,
        PhotoArchitectureDescriptor descriptor, PhotoLiftOptions? options = null)
    {
        options ??= new PhotoLiftOptions();
        var morphology = PhotoBimSynthesizer.Resolve(descriptor, options);
        var before = Traits(elements);
        double score = Score(before, descriptor, morphology);
        var corrections = new List<string>();
        string group = groups.FirstOrDefault()?.Id ?? elements.FirstOrDefault()?.BuildingGroupId ?? options.GroupName;
        bool shapeOk = Score(before with { ColoredElements = before.Elements }, descriptor, morphology) >= AcceptScore && elements.Count > 0;
        PhotoBimModel model;
        if (shapeOk)
        {
            model = new PhotoBimModel { Morphology = morphology, Floors = before.Floors, HeightM = before.Height, FootprintW = before.Width, FootprintD = before.Depth };
            model.Elements.AddRange(elements.Select(e => e.Clone(keepId: true)));
            model.Groups.AddRange(groups.Select(g => g.Clone()));
            int painted = Paint(model.Elements, descriptor);
            if (painted > 0) corrections.Add($"цвета фасада со снимка наложены на {painted} элем. (стены {descriptor.WallHex}, стекло {descriptor.GlassHex}, кровля {descriptor.RoofHex})");
        }
        else
        {
            // Пересборка по паспорту: числа модели — если правдоподобны (этажность ±60 %, габарит 6…300 м).
            var tuned = descriptor.Clone();
            if (before.Floors >= 1 && descriptor.Floors > 0 && Math.Abs(before.Floors - descriptor.Floors) <= descriptor.Floors * 0.6 && morphology != PhotoMorphology.ArchSpan)
                tuned.Floors = before.Floors;
            if (before.Width is >= 6 and <= 300 && before.Depth is >= 6 and <= 300)
            {
                tuned.WidthM = Math.Max(before.Width, before.Depth) * 0.85 + tuned.WidthM * 0.15;
                tuned.DepthM = Math.Min(before.Width, before.Depth) * 0.85 + tuned.DepthM * 0.15;
                if (morphology == PhotoMorphology.ArchSpan) (tuned.WidthM, tuned.DepthM) = (Math.Min(before.Width, before.Depth) / 0.8, Math.Max(before.Width, before.Depth));
            }
            model = PhotoBimSynthesizer.Build(tuned, options with { GroupName = group, Floors = options.Floors ?? tuned.Floors });
            corrections.Add(string.Create(Ru, $"ответ модели не совпал с формой снимка (оценка {score:0.00}: {before.Describe()}) — пересобрано Визуальным ядром по паспорту"));
            corrections.AddRange(Restored(before, Traits(model.Elements), morphology, descriptor));
        }
        var after = Traits(model.Elements);
        return (model, new FidelityReport(score, Score(after, descriptor, morphology), !shapeOk, corrections, before, after));
    }

    private static IEnumerable<string> Restored(ModelTraits b, ModelTraits a, PhotoMorphology m, PhotoArchitectureDescriptor d)
    {
        if (a.ArcElements > b.ArcElements) yield return string.Create(Ru, $"наложена кривизна фасада: дуговых элементов {a.ArcElements} (стрела/хорда {Math.Max(0.12, d.ArcBulgeRatio):0.00})");
        if (a.HasRoundedContour && !b.HasRoundedContour) yield return "скруглены углы перекрытий (CornerRadius)";
        if (a.Tiers > b.Tiers) yield return $"наложены ярусы-уступы: {a.Tiers}";
        if (a.HasDome && !b.HasDome) yield return "восстановлен купол над барабаном";
        if (a.RoundColumns > b.RoundColumns) yield return $"восстановлена колоннада: {a.RoundColumns} круглых колонн";
        if (a.HasVault && !b.HasVault) yield return "восстановлено сводчатое покрытие без промежуточных опор";
        if (a.NonOrthogonalWalls > b.NonOrthogonalWalls) yield return string.Create(Ru, $"восстановлен непрямоугольный план: острый угол {a.MinPlanAngle:0} °, косых стен {a.NonOrthogonalWalls}");
        if (a.ColoredElements > b.ColoredElements) yield return $"цвета снимка: стены {d.WallHex}, стекло {d.GlassHex}, кровля {d.RoofHex}";
        if (a.AlternatingArcs > b.AlternatingArcs && a.AlternatingArcs >= 2) yield return $"фасад-волна: {a.AlternatingArcs} чередующихся дуг";
        if (a.HasRingSlab && !b.HasRingSlab) yield return "кольцевое перекрытие с двором-атриумом";
        if (a.HasMansard && !b.HasMansard) yield return "мансардные этажи с отступом";
        if (a.HasPodium && !b.HasPodium) yield return "стилобат шире башни";
        _ = m;
    }

    /// <summary>Окраска элементов без цвета по ролям: стены — стена/акцент, витражи и окна — стекло, кровли и купола — кровля, плиты — акцент.</summary>
    public static int Paint(IList<ArchitecturalElement> elements, PhotoArchitectureDescriptor d)
    {
        int painted = 0;
        foreach (var e in elements)
        {
            if (CurvedDesign.FacadeColor(e) is not null) continue;
            string? hex = e.Category switch
            {
                BimCategory.Wall => d.WallHex,
                BimCategory.CurtainWall or BimCategory.Window => d.GlassHex,
                BimCategory.Roof or BimCategory.Shell => d.RoofHex,
                BimCategory.Slab or BimCategory.Column or BimCategory.Beam or BimCategory.Core => d.AccentHex,
                BimCategory.Door => d.FrameHex,
                _ => null
            };
            if (hex is null) continue;
            CurvedDesign.SetFacadeColor(e, hex);
            painted++;
        }
        return painted;
    }
}
