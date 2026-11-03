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
    }

    private static double? Read(ArchitecturalElement e, Guid guid, double min, double max)
    {
        if (e.UserParameters is null) return null;
        var v = e.GetUserParameter(guid);
        return v.IsEmpty || !double.IsFinite(v.AsDouble()) ? null : Math.Clamp(v.AsDouble(), min, max);
    }
}
