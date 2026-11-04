using AIArchitectStudio.Models.Analytical;

namespace AIArchitectStudio.Models;

/// <summary>Закрепление концов стойки (расчётная схема гибкости, СП 63.13330 п. 8.1.17, СП 16.13330 п. 10.3.1).</summary>
public enum EndFixity
{
    /// <summary>Не задано: ж/б — l0 = 0,7·H (защемление в перекрытиях), сталь и дерево — l0 = H.</summary>
    Default,

    /// <summary>Жёсткое защемление обоих концов, μ = 0,7.</summary>
    Fixed,

    /// <summary>Шарнир — защемление, μ = 0,85.</summary>
    PinnedFixed,

    /// <summary>Шарнирное опирание обоих концов, μ = 1,0.</summary>
    Pinned,

    /// <summary>Консоль (защемление внизу, свободный верх), μ = 2,0.</summary>
    Cantilever
}

/// <summary>
/// Конструктивные параметры режима «Конструкции (КЖ / КМ)» — общие параметры с постоянными Guid (журнал Undo, .aia, IFC
/// AIA_UserParameters): закрепление концов стойки (μ в гибкость), диаметр рабочей арматуры, шаг хомутов, защитный слой бетона и
/// поперечное армирование зоны продавливания плиты (F_sw,ult по СП 63.13330 п. 8.1.48). Без значения элемент считается как раньше —
/// эталонные сцены совпадают до бита.
/// </summary>
public static class StructuralDesign
{
    public static readonly Guid FixityGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e81");
    public static readonly Guid BarDiameterGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e82");
    public static readonly Guid StirrupSpacingGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e83");
    public static readonly Guid CoverGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e84");
    public static readonly Guid PunchingRebarGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6e85");

    public const string Group = "Конструирование (КЖ / КМ)";

    /// <summary>Диаметр поперечных стержней каркасов продавливания Кр, м (А240).</summary>
    public const double PunchingBarDiameter = 0.008;

    /// <summary>Расчётное сопротивление поперечной арматуры А240, кПа (СП 63.13330, табл. 6.15).</summary>
    public const double PunchingRsw = 170e3;

    public static readonly IReadOnlyList<int> BarDiameters = [12, 14, 16, 18, 20, 22, 25, 28, 32];

    private static class Definitions
    {
        public static readonly ParameterDefinition Fixity = StructuralRole.Register(new ParameterDefinition
        {
            Guid = FixityGuid, Name = "Закрепление концов", SpecType = ParameterSpecType.Text, StorageType = ParameterStorageType.String, Group = Group
        });

        public static readonly ParameterDefinition BarDiameter = StructuralRole.Register(new ParameterDefinition
        {
            Guid = BarDiameterGuid, Name = "Диаметр рабочей арматуры", SpecType = ParameterSpecType.Length, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition StirrupSpacing = StructuralRole.Register(new ParameterDefinition
        {
            Guid = StirrupSpacingGuid, Name = "Шаг хомутов", SpecType = ParameterSpecType.Length, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition Cover = StructuralRole.Register(new ParameterDefinition
        {
            Guid = CoverGuid, Name = "Защитный слой бетона", SpecType = ParameterSpecType.Length, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition PunchingRebar = StructuralRole.Register(new ParameterDefinition
        {
            Guid = PunchingRebarGuid, Name = "Поперечное армирование зоны продавливания", SpecType = ParameterSpecType.YesNo, StorageType = ParameterStorageType.Bool, Group = Group
        });
    }

    public static string Code(EndFixity f) => f switch
    {
        EndFixity.Fixed => "fixed",
        EndFixity.PinnedFixed => "pinned_fixed",
        EndFixity.Pinned => "pinned",
        EndFixity.Cantilever => "cantilever",
        _ => string.Empty
    };

    /// <summary>μ схемы закрепления; 0 — не задано (прежнее правило по материалу).</summary>
    public static double LengthFactor(EndFixity f) => f switch
    {
        EndFixity.Fixed => 0.7,
        EndFixity.PinnedFixed => 0.85,
        EndFixity.Pinned => 1.0,
        EndFixity.Cantilever => 2.0,
        _ => 0
    };

    public static EndFixity Fixity(ArchitecturalElement e)
    {
        if (e.UserParameters is null) return EndFixity.Default;
        var v = e.GetUserParameter(FixityGuid);
        if (v.IsEmpty) return EndFixity.Default;
        return v.AsString() switch
        {
            "fixed" => EndFixity.Fixed,
            "pinned_fixed" => EndFixity.PinnedFixed,
            "pinned" => EndFixity.Pinned,
            "cantilever" => EndFixity.Cantilever,
            _ => EndFixity.Default
        };
    }

    /// <summary>Диаметр рабочей арматуры, м (Ø12…32); null — по расчёту армирования.</summary>
    public static double? BarDiameter(ArchitecturalElement e) => Read(e, BarDiameterGuid, 0.012, 0.032);

    /// <summary>Шаг хомутов, м (100…300 мм); null — по правилу 15d.</summary>
    public static double? StirrupSpacing(ArchitecturalElement e) => Read(e, StirrupSpacingGuid, 0.1, 0.3);

    /// <summary>Защитный слой бетона, м (20…50 мм); null — 40 мм.</summary>
    public static double? Cover(ArchitecturalElement e) => Read(e, CoverGuid, 0.02, 0.05);

    public static bool HasPunchingRebar(ArchitecturalElement e) =>
        e.UserParameters is not null && e.GetUserParameter(PunchingRebarGuid) is { IsEmpty: false } v && v.AsBool();

    /// <summary>
    /// Погонное усилие поперечной арматуры продавливания q_sw = R_sw·A_sw / s_w, кН/м контура (СП 63, п. 8.1.48): стержни Кр Ø8 А240
    /// с шагом s_w вдоль контура и поперёк него (шаг хомутов элемента, иначе 100 мм).
    /// </summary>
    public static double PunchingQsw(ArchitecturalElement slab)
    {
        double sw = StirrupSpacing(slab) ?? 0.1;
        double area = Math.PI * PunchingBarDiameter * PunchingBarDiameter / 4;
        return PunchingRsw * area / (sw * sw);
    }

    public static void WriteFixity(ArchitecturalElement e, EndFixity f)
    {
        if (f == EndFixity.Default) e.RemoveUserParameter(FixityGuid);
        else e.SetUserParameter(Definitions.Fixity, ParameterValue.FromString(Code(f)));
    }

    public static void WriteRebar(ArchitecturalElement e, double barDiameter, double stirrupSpacing, double cover)
    {
        e.SetUserParameter(Definitions.BarDiameter, ParameterValue.FromDouble(Math.Clamp(barDiameter, 0.012, 0.032)));
        e.SetUserParameter(Definitions.StirrupSpacing, ParameterValue.FromDouble(Math.Clamp(stirrupSpacing, 0.1, 0.3)));
        e.SetUserParameter(Definitions.Cover, ParameterValue.FromDouble(Math.Clamp(cover, 0.02, 0.05)));
    }

    public static void WritePunchingRebar(ArchitecturalElement e, bool on)
    {
        if (on) e.SetUserParameter(Definitions.PunchingRebar, ParameterValue.FromBool(true));
        else e.RemoveUserParameter(PunchingRebarGuid);
    }

    /// <summary>Перенос параметров в элемент расчётной схемы (вызывается построителем только у элементов с параметрами).</summary>
    public static void ApplyTo(AnalyticalElement m, ArchitecturalElement source)
    {
        if (source.UserParameters is null) return;
        if (m.Role == AnalyticalRole.Vertical) m.LengthFactor = LengthFactor(Fixity(source));
        if (m.Role is AnalyticalRole.Slab or AnalyticalRole.Foundation && HasPunchingRebar(source)) m.PunchingQsw = PunchingQsw(source);
        if (Layout(source) is { } layout)
        {
            m.RebarCover = layout.AxisDistance;
            m.RebarMu = layout.Mu;
            m.StirrupQsw = layout.StirrupQsw;
        }
    }

    // ───────────── Явное армирование: раскладка стержней (общая для расчёта и 3D-каркаса) ─────────────

    /// <summary>Диаметр хомутов и поперечных стержней каркасов, м (А240 Ø8).</summary>
    public const double StirrupBarDiameter = 0.008;

    /// <summary>Защитный слой бетона до хомута (или до стержня сетки) по умолчанию, м.</summary>
    public const double DefaultCover = 0.04;

    /// <summary>Наибольший шаг рабочих стержней по контуру сечения и стержней сетки, м.</summary>
    public const double MaxBarPitch = 0.2;

    /// <summary>Шаг поперечных стержней сетки плиты при явном армировании: шаг хомутов, иначе 200 мм.</summary>
    public static double MeshPitch(ArchitecturalElement e) => StirrupSpacing(e) ?? MaxBarPitch;

    /// <summary>Стержней в ряду длиной span (между осями крайних стержней) при шаге ≤ 200 мм.</summary>
    public static int RowBarCount(double span) => Math.Max(2, (int)Math.Ceiling(Math.Max(0, span) / MaxBarPitch) + 1);

    /// <summary>Стержней по окружности радиуса r (шаг ≈ 150 мм, не меньше 6).</summary>
    public static int RoundBarCount(double r) => Math.Max(6, (int)Math.Round(2 * Math.PI * r / 0.15));

    /// <summary>Рабочих стержней по контуру прямоугольного сечения: два ряда по u и промежуточные по v.</summary>
    public static int PerimeterBarCount(double spanU, double spanV) => 2 * RowBarCount(spanU) + 2 * (RowBarCount(spanV) - 2);

    /// <summary>
    /// Раскладка явного армирования элемента (задан диаметр рабочей арматуры): расстояние до оси стержня a, μ по фактической
    /// площади стержней As = n·π·d²/4 и q_sw хомутов балки. null — параметров нет, расчёт и каркас прежние.
    /// </summary>
    public static RebarLayout? Layout(ArchitecturalElement e)
    {
        if (BarDiameter(e) is not double d || !AIArchitectStudio.Services.ReinforcementCalculator.IsReinforced(e)) return null;
        double c = Cover(e) ?? DefaultCover, ds = StirrupBarDiameter, bar = Math.PI * d * d / 4;
        switch (e.Category)
        {
            case BimCategory.Column:
            {
                double a = c + ds + d / 2;
                bool round = e.EffectiveProfile == ProfileTypes.Circle;
                double w = Math.Max(0.1, e.Width), dp = Math.Max(0.1, e.Depth);
                int n = round ? RoundBarCount(Math.Max(0.01, Math.Min(w, dp) / 2 - a)) : PerimeterBarCount(Math.Max(0.02, w - 2 * a), Math.Max(0.02, dp - 2 * a));
                double area = round ? Math.PI * w * dp / 4 : w * dp;
                return new RebarLayout(d, c, a, n, n * bar / area, 0);
            }
            case BimCategory.Beam:
            {
                // Сечение балки D × H: растянутый нижний ряд по ширине b = D.
                double a = c + ds + d / 2, b = Math.Max(0.1, e.Depth), h = Math.Max(0.1, e.Height);
                int n = RowBarCount(Math.Max(0.02, b - 2 * a));
                double qsw = StirrupSpacing(e) is double s ? PunchingRsw * 2 * Math.PI * ds * ds / 4 / s : 0;
                return new RebarLayout(d, c, a, n, n * bar / (b * Math.Max(0.02, h - a)), qsw);
            }
            case BimCategory.Slab or BimCategory.CantileverSlab or BimCategory.Roof or BimCategory.Terrace or BimCategory.Foundation:
            {
                double a = c + d / 2, h = Math.Max(0.05, MaterialLayers.StructuralThickness(e));
                double perMeter = 1 / MeshPitch(e);
                return new RebarLayout(d, c, a, (int)Math.Round(perMeter), perMeter * bar / Math.Max(0.02, h - a), 0);
            }
            default:
                return null;
        }
    }

    private static double? Read(ArchitecturalElement e, Guid guid, double min, double max)
    {
        if (e.UserParameters is null) return null;
        var v = e.GetUserParameter(guid);
        return v.IsEmpty || !double.IsFinite(v.AsDouble()) ? null : Math.Clamp(v.AsDouble(), min, max);
    }
}

/// <summary>
/// Раскладка явного армирования: диаметр рабочих стержней d и защитный слой c (м), расстояние от грани до оси стержня a (м),
/// число рабочих стержней (колонна — по контуру, балка — нижний ряд, плита — на 1 м), μ по фактической площади и q_sw хомутов, кН/м.
/// </summary>
public readonly record struct RebarLayout(double Diameter, double Cover, double AxisDistance, int Bars, double Mu, double StirrupQsw);
