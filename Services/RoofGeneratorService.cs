using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Параметры полой кровли: форма, уклон (°), толщина пирога ската и свес (м), высота парапета (м), фронтонное окно.</summary>
public sealed record RoofDesignRequest(string Shape, double AngleDeg = 30, double SlabThickness = RoofDesign.DefaultSlabThickness,
    double Overhang = RoofDesign.DefaultOverhang, double ParapetHeight = RoofDesign.DefaultParapetHeight, bool GableWindow = true);

/// <summary>
/// Генератор полых кровель: двускатная, вальмовая, односкатная с карнизом, плоская эксплуатируемая с парапетом, мансардная
/// ломаная и сводчатая оболочка. Высота H (от карниза до верха конька) — по уклону и короткой стороне плана: H = tg α · пролёт/2 +
/// t·√(1 + tg²α) (у односкатной — весь пролёт, у мансардной α — уклон верхнего ската при нижнем 70°, у свода подъём ≤ полупролёта).
/// Кровля меняет форму на месте (низ сохраняется), над плитой создаётся новая кровля по её контуру. Параметры конструкции —
/// общие параметры <see cref="RoofDesign"/>. Чистая функция; применение к сцене — одним шагом журнала.
/// </summary>
public static class RoofGeneratorService
{
    public static string ShapeTitle(string shape) => shape switch
    {
        RoofShapes.GableHollow => "Двускатная полая",
        RoofShapes.HipHollow => "Вальмовая полая",
        RoofShapes.ShedHollow => "Односкатная с карнизом",
        RoofShapes.FlatParapet => "Плоская эксплуатируемая с парапетом",
        RoofShapes.Mansard => "Мансардная ломаная",
        RoofShapes.BarrelVault => "Сводчатая оболочка",
        _ => shape
    };

    /// <summary>Высота H элемента по уклону для формы (пролёт — короткая сторона плана).</summary>
    public static double HeightFor(string shape, double span, double angleDeg, double t, double flatThickness)
    {
        double k = Math.Tan(Math.Clamp(angleDeg, 5, 60) * Math.PI / 180), s = span / 2, v = t * Math.Sqrt(1 + k * k);
        switch (shape)
        {
            case RoofShapes.ShedHollow:
                return Math.Round(k * span + v, 3);
            case RoofShapes.FlatParapet:
                return flatThickness;
            case RoofShapes.Mansard:
            {
                double a = Math.Clamp(0.22 * s, 0.4, Math.Max(0.4, 0.45 * s));
                return Math.Round(a * Math.Tan(70 * Math.PI / 180) + (s - a) * k + v, 3);
            }
            case RoofShapes.BarrelVault:
                return Math.Round(Math.Clamp(s * k, 0.3, s) + t, 3);
            default:
                return Math.Round(k * s + v, 3);
        }
    }

    public static PitchedRoofResult? Apply(IReadOnlyList<ArchitecturalElement> scene, ArchitecturalElement target, RoofDesignRequest request)
    {
        if (target.Type is not (ElementTypes.Roof or ElementTypes.Slab or ElementTypes.CantileverSlab)) return null;
        string shape = RoofShapes.IsHollow(request.Shape) ? request.Shape : RoofShapes.GableHollow;
        double angle = double.IsFinite(request.AngleDeg) ? Math.Clamp(request.AngleDeg, 5, 60) : 30;
        double t = double.IsFinite(request.SlabThickness) ? Math.Clamp(request.SlabThickness, 0.08, 0.6) : RoofDesign.DefaultSlabThickness;
        double overhang = double.IsFinite(request.Overhang) ? Math.Clamp(request.Overhang, 0, 1.5) : RoofDesign.DefaultOverhang;
        double span = ParametricToolsService.Span(target);
        if (span < 0.5) return null;
        bool flat = shape == RoofShapes.FlatParapet;

        if (target.Type == ElementTypes.Roof)
        {
            double baseY = target.BaseY;
            double flatThickness = target.IsFlatRoof ? target.Height : t;
            List<ArchitecturalElement> removed = flat ? [] : scene.Where(h => h.Type == ElementTypes.SlabOpening && h.HostElementId == target.Id).ToList();
            double height = HeightFor(shape, span, angle, t, flatThickness);
            target.RoofShape = shape;
            if (!flat) target.Layers = [];
            target.SetSize(target.Width, height, target.Depth);
            target.PlaceOnBase(baseY);
            RoofDesign.Write(target, t, overhang, flat ? request.ParapetHeight : null, flat ? null : request.GableWindow);
            return new PitchedRoofResult(target, false, shape, angle, height, span, removed);
        }

        double h = HeightFor(shape, span, angle, t, t);
        var roof = new ArchitecturalElement
        {
            Type = ElementTypes.Roof,
            Name = ShapeTitle(shape) + " кровля",
            BuildingGroupId = target.BuildingGroupId,
            LevelIndex = target.LevelIndex,
            Material = flat || shape == RoofShapes.BarrelVault ? "concrete" : "metal_roof",
            RoofShape = shape,
            Position = (double[])target.Position.Clone(),
            Size = [target.Width, h, target.Depth],
            Rotation = (double[])target.Rotation.Clone(),
            Polygon2D = target.Polygon2D.Select(p => p.Clone()).ToList()
        };
        roof.PlaceOnBase(target.TopY);
        RoofDesign.Write(roof, t, overhang, flat ? request.ParapetHeight : null, flat ? null : request.GableWindow);
        return new PitchedRoofResult(roof, true, shape, angle, h, span, []);
    }
}
