using System.Globalization;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Конструктивная роль элемента в паспорте свойств.</summary>
public enum ConstructiveRole
{
    Bearing,
    Enclosing,
    Partition
}

/// <summary>
/// Паспорт свойств элемента по образцу Renga Professional: идентификация (марка, позиция, уровень, смещение низа, роль),
/// расчётные величины (объёмы нетто/брутто, площадь проёмов, опалубки, подошвы, периметр, масса бетона/стали, масса арматуры) и
/// инженерные характеристики (предел огнестойкости, сопротивление теплопередаче R0 по слоям пирога).
/// </summary>
public sealed record RengaProperties(
    string Mark, int Position, string LevelName, double BaseOffsetMm, ConstructiveRole Role,
    double NetVolume, double GrossVolume, double OpeningArea, double FormworkArea, double BaseArea, double Perimeter,
    double MassTonnes, double RebarKg, string FireResistance, double? ThermalResistance, string ThermalLayers)
{
    public string RoleTitle => Role switch
    {
        ConstructiveRole.Enclosing => "Ограждающая",
        ConstructiveRole.Partition => "Перегородка",
        _ => "Несущая"
    };
}

/// <summary>
/// Система объектных и расчётных свойств уровня Renga Professional. Хранимые свойства — общие параметры с постоянными Guid
/// («Марка конструкции», «Позиция по спецификации»: журнал Undo, .aia, IFC AIA_UserParameters, группировка ведомости);
/// расчётные — на лету по текущей геометрии и привязкам:
/// V_брутто — тело по габариту (контур × высота, повторы этажей), V_нетто — за вычетом врезанных окон, дверей, проёмов и отверстий
/// плит (каждый — в пределах тела хоста), S_проём — площадь проёмов стены (отверстий плиты), S_опал — поверхность опалубки
/// (колонна — боковые грани, балка — дно и боковые грани, плита — дно и торцы, стена — две грани и откосы проёмов, фундамент —
/// торцы), S_осн — площадь подошвы в плане, L — периметр плана, m — масса по плотности материала или слоёв пирога, арматура —
/// по нормам расхода и K_исп (<see cref="ReinforcementCalculator"/>). Огнестойкость — по минимальному размеру сечения и толщине
/// (СП 468.1325800.2019, упрощённо), R0 = 1/αв + Σ δ/λ + 1/αн (СП 50.13330.2012, αв = 8,7, αн = 23 Вт/(м²·°С)).
/// </summary>
public static class RengaPropertyService
{
    public static readonly Guid MarkGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e91");
    public static readonly Guid PositionGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e92");
    public const string Group = "Идентификация (Renga)";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static class Definitions
    {
        public static readonly ParameterDefinition Mark = StructuralRole.Register(new ParameterDefinition
        {
            Guid = MarkGuid, Name = "Марка конструкции", SpecType = ParameterSpecType.Text, StorageType = ParameterStorageType.String, Group = Group
        });

        public static readonly ParameterDefinition Position = StructuralRole.Register(new ParameterDefinition
        {
            Guid = PositionGuid, Name = "Позиция по спецификации", SpecType = ParameterSpecType.Integer, StorageType = ParameterStorageType.Int, Group = Group
        });
    }

    public static ParameterDefinition MarkDefinition => Definitions.Mark;

    /// <summary>Префикс марки по категории: КЖ-К (колонна), КЖ-Б (балка), ПМ (плита), СТ (стена), ФП (фундамент), ОК, Д…</summary>
    public static string MarkPrefix(ArchitecturalElement e) => e.Category switch
    {
        BimCategory.Column => IsSteel(e) ? "КМ-К" : "КЖ-К",
        BimCategory.Beam => IsSteel(e) ? "КМ-Б" : "КЖ-Б",
        BimCategory.Slab or BimCategory.CantileverSlab => "ПМ-",
        BimCategory.Wall => StructuralRole.IsLoadBearing(e) ? "СТ-" : "ПГ-",
        BimCategory.Foundation => "ФП-",
        BimCategory.Roof => "ПК-",
        BimCategory.Window => "ОК-",
        BimCategory.Door => "Д-",
        BimCategory.Stairs => "ЛМ-",
        BimCategory.CurtainWall => "ВТ-",
        BimCategory.Core => "ЯЖ-",
        _ => "ЭЛ-"
    };

    public static string? StoredMark(ArchitecturalElement e) =>
        e.UserParameters is not null && e.GetUserParameter(MarkGuid) is { IsEmpty: false } v ? v.AsString() : null;

    public static int? StoredPosition(ArchitecturalElement e) =>
        e.UserParameters is not null && e.GetUserParameter(PositionGuid) is { IsEmpty: false } v ? v.AsInt() : null;

    /// <summary>
    /// Марки и позиции по типам (как в Renga: одинаковый тип — одна марка): элементы сортируются по категории и подписи
    /// типоразмера, марка = префикс + номер типоразмера в категории, позиция — сквозной номер типоразмера в спецификации.
    /// </summary>
    public static Dictionary<string, (string Mark, int Position)> PlanMarks(IReadOnlyList<ArchitecturalElement> elements)
    {
        var result = new Dictionary<string, (string, int)>();
        int position = 0;
        foreach (var category in elements.Where(e => !ElementTypes.IsVoidType(e.Category)).GroupBy(MarkPrefix).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int n = 0;
            foreach (var type in category.GroupBy(TypeKey).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                n++;
                position++;
                foreach (var e in type) result[e.Id] = ($"{category.Key}{n}", position);
            }
        }
        return result;
    }

    /// <summary>Подпись типоразмера: тип, материал и сечение (мм) — одинаковые элементы получают одну марку.</summary>
    private static string TypeKey(ArchitecturalElement e)
    {
        static string Mm(double m) => Math.Round(m * 1000).ToString("0000000", CultureInfo.InvariantCulture);
        string size = e.Category switch
        {
            BimCategory.Column => $"{Mm(e.Width)}x{Mm(e.Depth)}{e.EffectiveProfile}",
            BimCategory.Beam => $"{Mm(e.Depth)}x{Mm(e.Height)}{e.EffectiveProfile}",
            BimCategory.Wall or BimCategory.CurtainWall => Mm(e.Depth),
            BimCategory.Window or BimCategory.Door => $"{Mm(e.Width)}x{Mm(e.Height)}",
            _ => Mm(e.Height)
        };
        return $"{size}|{e.Material}|{e.Layers.Count}";
    }

    /// <summary>Запись марки и позиции элементу (общие параметры; вызывающий открывает шаг журнала).</summary>
    public static void WriteMark(ArchitecturalElement e, string mark, int position)
    {
        e.SetUserParameter(Definitions.Mark, ParameterValue.FromString(mark));
        e.SetUserParameter(Definitions.Position, ParameterValue.FromInt(position));
    }

    // ───────────────────────────── Расчёт свойств ─────────────────────────────

    public static RengaProperties Compute(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> scene, double utilization = 0, bool punching = false)
    {
        var plan = PlanMarks(scene);
        var (autoMark, autoPosition) = plan.GetValueOrDefault(e.Id, ($"{MarkPrefix(e)}1", 0));
        string mark = StoredMark(e) ?? autoMark;
        int position = StoredPosition(e) ?? autoPosition;
        var level = e.Level;
        string levelName = level?.Name is { Length: > 0 } name ? name : $"Этаж {e.LevelIndex + 1}";
        double baseOffset = level is not null ? (e.BaseY - level.Elevation) * 1000 : 0;

        int repeat = Math.Max(1, e.RepeatLevels);
        double planArea = e.PlanArea() * e.RepeatAreaFactor();
        double perimeter = PlanPerimeter(e);
        double gross, opening, net, formwork, baseArea;
        var hosted = scene.Where(o => o.HostElementId == e.Id && !ReferenceEquals(o, e)).ToList();
        switch (e.Category)
        {
            case BimCategory.Wall:
            case BimCategory.CurtainWall:
            {
                // Дуговая стена (волна 4): длина оси — длина дуги, а не хорда.
                double axisLength = e.Width * CurvedDesign.LengthFactor(e);
                gross = axisLength * e.Height * e.Depth * repeat;
                var cuts = hosted.Where(o => ElementTypes.IsHostedType(o.Category)).ToList();
                opening = cuts.Sum(o => ClippedOpening(e, o).Area);
                net = Math.Max(0, gross - opening * e.Depth);
                double reveals = cuts.Sum(o => ClippedOpening(e, o).Perimeter) * e.Depth;
                formwork = Math.Max(0, 2 * (axisLength * e.Height * repeat - opening)) + reveals;
                baseArea = axisLength * e.Depth;
                break;
            }
            case BimCategory.Column:
            case BimCategory.Core:
                gross = planArea * e.Height;
                opening = 0;
                net = gross;
                formwork = perimeter * e.Height * repeat;
                baseArea = e.PlanArea();
                break;
            case BimCategory.Beam:
                gross = e.Width * e.Height * e.Depth * repeat;
                opening = 0;
                net = gross;
                formwork = (2 * e.Height + e.Depth) * e.Width * repeat;
                baseArea = e.Width * e.Depth;
                break;
            case BimCategory.Window:
            case BimCategory.Door:
                gross = e.Width * e.Height * e.Depth;
                opening = e.Width * e.Height;
                net = gross;
                formwork = 0;
                baseArea = e.Width * e.Depth;
                break;
            default:
            {
                // Плиты, покрытия, фундаменты, террасы: отверстия slab_opening в пределах контура хоста.
                var outline = ElementGeometryOutline(e);
                double holes = hosted.Where(o => o.Category == BimCategory.SlabOpening)
                    .Sum(o => Geometry2D.IntersectionArea(outline, AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(o))) * repeat;
                gross = planArea * e.Height;
                opening = holes;
                net = Math.Max(0, planArea - holes) * e.Height;
                double holeEdges = hosted.Where(o => o.Category == BimCategory.SlabOpening).Sum(o => Geometry2D.Perimeter(AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(o)));
                formwork = e.Category == BimCategory.Foundation
                    ? perimeter * e.Height * repeat
                    : Math.Max(0, planArea - holes) + (perimeter + holeEdges) * e.Height * repeat;
                baseArea = Math.Max(0, e.PlanArea() - holes / repeat);
                break;
            }
        }

        double mass = Mass(e, net);
        double rebar = 0;
        if (ReinforcementCalculator.IsReinforced(e))
        {
            double concrete = BimAnalyticsService.ConcreteVolume(e, scene, opening * (e.Category is BimCategory.Wall ? 0 : 1));
            rebar = ReinforcementCalculator.Compute(e, ReinforcementCalculator.ReinforcedVolume(e, concrete), utilization, punching).Total;
        }
        var (r0, layers) = ThermalResistance(e);
        return new RengaProperties(mark, position, levelName, baseOffset, RoleOf(e), net, gross, opening, formwork, baseArea, perimeter, mass, rebar,
            FireResistance(e), r0, layers);
    }

    private static List<(double X, double Z)> ElementGeometryOutline(ArchitecturalElement e) => AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(e);

    private static double PlanPerimeter(ArchitecturalElement e) => e.Category switch
    {
        BimCategory.Column when e.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe && e.Polygon2D.Count < 3 =>
            Math.PI * (3 * (e.Width + e.Depth) / 2 - Math.Sqrt((3 * e.Width / 2 + e.Depth / 2) * (e.Width / 2 + 3 * e.Depth / 2))),
        _ => Geometry2D.Perimeter(AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(e))
    };

    /// <summary>Проём стены в пределах её тела: ширина вдоль оси и высота, обрезанные по габариту стены (м², м).</summary>
    public static (double Area, double Perimeter) ClippedOpening(ArchitecturalElement wall, ArchitecturalElement o)
    {
        var (lx, _) = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY);
        double x0 = Math.Max(-wall.Width / 2, lx - o.Width / 2), x1 = Math.Min(wall.Width / 2, lx + o.Width / 2);
        double y0 = Math.Max(wall.BaseY, o.BaseY), y1 = Math.Min(wall.TopY, o.TopY);
        double w = Math.Max(0, x1 - x0), h = Math.Max(0, y1 - y0);
        return (w * h, w > 0 && h > 0 ? 2 * (w + h) : 0);
    }

    public static ConstructiveRole RoleOf(ArchitecturalElement e) => e.Category switch
    {
        BimCategory.Wall => StructuralRole.IsLoadBearing(e) ? ConstructiveRole.Bearing : ConstructiveRole.Partition,
        BimCategory.CurtainWall or BimCategory.Window or BimCategory.Door or BimCategory.WallOpening => ConstructiveRole.Enclosing,
        BimCategory.Roof when !(e.Material ?? string.Empty).Contains("concrete", StringComparison.OrdinalIgnoreCase) => ConstructiveRole.Enclosing,
        _ => ConstructiveRole.Bearing
    };

    private static bool IsSteel(ArchitecturalElement e) =>
        (e.Material ?? string.Empty).Contains("steel", StringComparison.OrdinalIgnoreCase) || e.EffectiveProfile is ProfileTypes.IBeam or ProfileTypes.Pipe;

    /// <summary>Плотность и теплопроводность материала элемента (кг/м³, Вт/(м·°С)).</summary>
    public static (double Density, double Lambda) MaterialProps(string? material)
    {
        string m = (material ?? string.Empty).ToLowerInvariant();
        if (m.Contains("aerated") || m.Contains("gas")) return (500, 0.14);
        if (m.Contains("concrete")) return (2500, 1.92);
        if (m.Contains("brick")) return (1800, 0.70);
        if (m.Contains("wood") || m.Contains("timber")) return (500, 0.18);
        if (m.Contains("steel") || m.Contains("metal")) return (7850, 58);
        if (m.Contains("glass")) return (2500, 0.76);
        if (m.Contains("stone")) return (2200, 1.5);
        if (m.Contains("plaster") || m.Contains("gypsum")) return (1100, 0.35);
        return (2000, 1.0);
    }

    /// <summary>Теплопроводность слоя пирога по названию, назначению и плотности (СП 50.13330, прил. Т, условия Б).</summary>
    public static double LayerLambda(MaterialLayer layer)
    {
        string n = layer.Name.ToLowerInvariant();
        if (n.Contains("минват") || n.Contains("минерал") || n.Contains("rockwool")) return 0.042;
        if (n.Contains("пеноплекс") || n.Contains("xps") || n.Contains("экструд")) return 0.032;
        if (n.Contains("пенопол") || n.Contains("eps")) return 0.039;
        if (n.Contains("газобетон") || n.Contains("пенобетон")) return layer.DensityKgM3 <= 450 ? 0.12 : layer.DensityKgM3 <= 550 ? 0.14 : 0.18;
        if (n.Contains("гкл") || n.Contains("гипсокарт")) return 0.21;
        if (n.Contains("кирпич") || n.Contains("облицов")) return layer.DensityKgM3 >= 1600 ? 0.70 : 0.56;
        if (n.Contains("штукат")) return 0.76;
        if (n.Contains("бетон") || n.Contains("ж/б") || n.Contains("железобет")) return 1.92;
        return layer.Role switch
        {
            MaterialLayerRole.Insulation => layer.DensityKgM3 < 60 ? 0.04 : 0.045,
            MaterialLayerRole.Waterproofing => 0.17,
            MaterialLayerRole.Screed => 0.93,
            MaterialLayerRole.Finish => layer.DensityKgM3 >= 1500 ? 0.76 : 0.35,
            _ => layer.DensityKgM3 >= 2200 ? 1.92 : layer.DensityKgM3 >= 1600 ? 0.70 : layer.DensityKgM3 >= 900 ? 0.40 : layer.DensityKgM3 >= 500 ? 0.18 : 0.12
        };
    }

    /// <summary>R0 = 1/αв + Σ δ/λ + 1/αн по слоям пирога (или однослойной конструкции), м²·°С/Вт; null — не ограждение.</summary>
    public static (double? R0, string Layers) ThermalResistance(ArchitecturalElement e)
    {
        if (e.Category is not (BimCategory.Wall or BimCategory.Roof or BimCategory.Slab or BimCategory.CurtainWall or BimCategory.Foundation)) return (null, string.Empty);
        const double alphaIn = 8.7, alphaOut = 23;
        double sum = 0;
        var parts = new List<string>();
        if (e.Category == BimCategory.CurtainWall) return (0.55, "стеклопакет двухкамерный (R ≈ 0,55)");
        if (e.Layers.Count > 0)
            foreach (var layer in e.Layers)
            {
                double lambda = LayerLambda(layer);
                double r = layer.ThicknessM / lambda;
                sum += r;
                parts.Add($"{layer.Name} {layer.ThicknessMm.ToString("0", Ru)} мм / {lambda.ToString("0.###", Ru)}");
            }
        else
        {
            double t = e.Category == BimCategory.Wall ? e.Depth : e.Height;
            var (_, lambda) = MaterialProps(e.Material);
            sum = t / lambda;
            parts.Add($"{e.Material} {(t * 1000).ToString("0", Ru)} мм / {lambda.ToString("0.###", Ru)}");
        }
        return (1 / alphaIn + sum + 1 / alphaOut, string.Join(" + ", parts));
    }

    /// <summary>Масса элемента, т: слои пирога — по своим плотностям, сталь двутавра/трубы — по площади профиля, иначе — по материалу.</summary>
    private static double Mass(ArchitecturalElement e, double netVolume)
    {
        if (e.Layers.Count > 0)
        {
            double total = MaterialLayers.TotalMm(e.Layers) / 1000;
            if (total <= 0) return 0;
            return e.Layers.Sum(l => netVolume * (l.ThicknessM / total) * l.DensityKgM3) / 1000;
        }
        if (e.Category is BimCategory.Column or BimCategory.Beam && e.EffectiveProfile is ProfileTypes.IBeam or ProfileTypes.Pipe)
        {
            if (QuantityScheduleService.RolledSection(e) is { } section) return section.Area * section.Length * 7.85;
        }
        return netVolume * MaterialProps(e.Material).Density / 1000;
    }

    /// <summary>Предел огнестойкости по минимальному размеру сечения / толщине (ж/б — с защитным слоем ≥ 20…45 мм), упрощённо.</summary>
    public static string FireResistance(ArchitecturalElement e)
    {
        string m = (e.Material ?? string.Empty).ToLowerInvariant();
        bool concrete = m.Contains("concrete") || e.Layers.Any(l => l.IsReinforcedConcrete);
        bool bearing = RoleOf(e) == ConstructiveRole.Bearing;
        string prefix = bearing ? "REI" : "EI";
        static int Step(double mm, params (double Min, int Rating)[] table)
        {
            foreach (var (min, rating) in table)
                if (mm >= min) return rating;
            return 30;
        }
        double t = MaterialLayers.StructuralThickness(e) * 1000;
        switch (e.Category)
        {
            case BimCategory.Column:
            case BimCategory.Beam:
            {
                double b = (e.Category == BimCategory.Column ? Math.Min(e.Width, e.Depth) : e.Depth) * 1000;
                if (IsSteel(e) || m.Contains("steel")) return "R 15";
                if (m.Contains("wood") || m.Contains("timber")) return b >= 150 ? "R 30" : "R 15";
                return "R " + Step(b, (400, 150), (300, 120), (250, 90), (200, 60));
            }
            case BimCategory.Slab:
            case BimCategory.CantileverSlab:
            case BimCategory.Roof:
            case BimCategory.Foundation:
                if (!concrete) return m.Contains("wood") ? "REI 30" : "REI 15";
                return "REI " + Step(t, (150, 150), (120, 120), (100, 90), (80, 60));
            case BimCategory.Wall:
            case BimCategory.Core:
                if (concrete) return prefix + " " + Step(t, (170, 150), (140, 120), (120, 90), (100, 60));
                if (m.Contains("brick") || m.Contains("aerated") || m.Contains("gas") || e.Layers.Count > 0)
                    return prefix + " " + Step(t, (250, 150), (120, 120), (100, 90), (80, 60));
                if (m.Contains("wood")) return prefix + " 30";
                return prefix + " 45";
            case BimCategory.CurtainWall:
            case BimCategory.Window:
                return "E 15";
            case BimCategory.Door:
                return "EI 30";
            default:
                return "—";
        }
    }

    public static string Format(double value, string format = "0.###") => value.ToString(format, Ru);
}
