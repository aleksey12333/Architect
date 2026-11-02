namespace AIArchitectStudio.Models.Analytical;

public enum AnalyticalMaterialKind
{
    Concrete,
    Steel,
    Timber,
    Masonry,
    Glass
}

/// <summary>
/// Материал расчётной схемы: E, Rc (сжатие), Rbt (растяжение бетона), Rbend (изгиб дерева) — кПа; плотность — кг/м³.
/// Модуль сдвига G — по коэффициенту Пуассона материала (дерево — G = E / 20, СП 64.13330).
/// </summary>
public sealed record AnalyticalMaterial(AnalyticalMaterialKind Kind, double E, double Rc, double Rbt, double Rbend, double Density)
{
    /// <summary>Коэффициент Пуассона: бетон 0.2, сталь 0.3, кладка 0.25, стекло 0.22.</summary>
    public double Poisson => Kind switch
    {
        AnalyticalMaterialKind.Steel => 0.3,
        AnalyticalMaterialKind.Masonry => 0.25,
        AnalyticalMaterialKind.Glass => 0.22,
        _ => 0.2
    };

    /// <summary>Модуль сдвига, кПа.</summary>
    public double G => Kind == AnalyticalMaterialKind.Timber ? E / 20 : E / (2 * (1 + Poisson));
}

/// <summary>Сечение стержня: площадь, моменты инерции относительно сильной и слабой осей, высоты сечения по этим осям, площадь среза (м², м⁴, м).</summary>
public readonly record struct AnalyticalSection(double A, double IStrong, double IWeak, double HStrong, double HWeak, double AShear);
