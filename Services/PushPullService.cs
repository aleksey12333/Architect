using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Грань элемента в его локальной системе (с учётом поворота RotationY): ±X — длина/ширина W, ±Y — высота H, ±Z — толщина D.</summary>
public enum PushPullFace
{
    PosX,
    NegX,
    PosY,
    NegY,
    PosZ,
    NegZ
}

/// <summary>Новые центр и габариты элемента после вытягивания грани.</summary>
public readonly record struct PushPullResult(PushPullFace Face, double OldLength, double Length, double CenterX, double CenterY, double CenterZ, double Width, double Height, double Depth)
{
    /// <summary>Фактическое приращение размера (с учётом минимального габарита 0.1 м).</summary>
    public double Delta => Length - OldLength;
}

/// <summary>
/// Тактильное моделирование в стиле Shapr3D: торцевые ручки Push/Pull. Противоположная грань остаётся неподвижной в мировых
/// координатах — новый размер L' = max(0.1, L + ΔL), центр смещается вдоль единичного вектора грани на ΔL' / 2.
/// </summary>
public static class PushPullService
{
    /// <summary>Минимальный габарит после вытягивания, м.</summary>
    public const double MinSize = 0.1;

    public static readonly IReadOnlyList<PushPullFace> AllFaces =
        [PushPullFace.PosX, PushPullFace.NegX, PushPullFace.PosY, PushPullFace.NegY, PushPullFace.PosZ, PushPullFace.NegZ];

    /// <summary>Типы элементов с торцевыми ручками.</summary>
    public static bool SupportsHandles(ArchitecturalElement e) => e.Type is ElementTypes.Wall or ElementTypes.Beam or ElementTypes.Column
        or ElementTypes.Slab or ElementTypes.Foundation or ElementTypes.Window or ElementTypes.Door or ElementTypes.WallOpening
        or ElementTypes.SlabOpening or ElementTypes.Pool or ElementTypes.Terrace;

    /// <summary>
    /// Грани, которые можно тянуть: у проёмов стены — ширина и высота (толщина следует за стеной), у отверстия плиты — только план;
    /// у элементов с контуром Polygon2D — только высота (контур не масштабируется без явных данных проекта).
    /// </summary>
    public static IReadOnlyList<PushPullFace> FacesFor(ArchitecturalElement e)
    {
        if (!SupportsHandles(e)) return [];
        if (e.Polygon2D.Count >= 3) return e.Type == ElementTypes.SlabOpening ? [] : [PushPullFace.PosY, PushPullFace.NegY];
        return e.Type switch
        {
            ElementTypes.Window or ElementTypes.Door or ElementTypes.WallOpening => [PushPullFace.PosX, PushPullFace.NegX, PushPullFace.PosY, PushPullFace.NegY],
            ElementTypes.SlabOpening => [PushPullFace.PosX, PushPullFace.NegX, PushPullFace.PosZ, PushPullFace.NegZ],
            _ => AllFaces
        };
    }

    /// <summary>Индекс габарита: 0 — W (локальная X), 1 — H (Y), 2 — D (локальная Z).</summary>
    public static int AxisIndex(PushPullFace face) => face switch
    {
        PushPullFace.PosX or PushPullFace.NegX => 0,
        PushPullFace.PosY or PushPullFace.NegY => 1,
        _ => 2
    };

    public static double Sign(PushPullFace face) => face is PushPullFace.PosX or PushPullFace.PosY or PushPullFace.PosZ ? 1 : -1;

    public static PushPullFace Opposite(PushPullFace face) => face switch
    {
        PushPullFace.PosX => PushPullFace.NegX,
        PushPullFace.NegX => PushPullFace.PosX,
        PushPullFace.PosY => PushPullFace.NegY,
        PushPullFace.NegY => PushPullFace.PosY,
        PushPullFace.PosZ => PushPullFace.NegZ,
        _ => PushPullFace.PosZ
    };

    /// <summary>Мировой единичный вектор внешней нормали грани (локальные оси повёрнуты на RotationY).</summary>
    public static (double X, double Y, double Z) Direction(ArchitecturalElement e, PushPullFace face)
    {
        double s = Sign(face);
        switch (AxisIndex(face))
        {
            case 1:
                return (0, s, 0);
            case 0:
            {
                var (x, z) = ArchitecturalElement.RotateY(s, 0, e.RotationY);
                return (x, 0, z);
            }
            default:
            {
                var (x, z) = ArchitecturalElement.RotateY(0, s, e.RotationY);
                return (x, 0, z);
            }
        }
    }

    /// <summary>Текущий размер элемента вдоль оси грани.</summary>
    public static double Length(ArchitecturalElement e, PushPullFace face) => AxisIndex(face) switch
    {
        0 => e.Width,
        1 => e.Height,
        _ => e.Depth
    };

    /// <summary>Центр грани в мировых координатах.</summary>
    public static (double X, double Y, double Z) FaceCenter(ArchitecturalElement e, PushPullFace face)
    {
        var d = Direction(e, face);
        double half = Length(e, face) / 2;
        return (e.X + d.X * half, e.Y + d.Y * half, e.Z + d.Z * half);
    }

    /// <summary>
    /// Новые центр и габариты при размере newLength вдоль оси грани: противоположная грань неподвижна,
    /// центр смещается вдоль нормали тянущейся грани на половину фактического приращения.
    /// </summary>
    public static PushPullResult Compute(ArchitecturalElement e, PushPullFace face, double newLength)
    {
        double old = Length(e, face);
        double length = double.IsFinite(newLength) ? Math.Max(MinSize, newLength) : old;
        double delta = length - old;
        var d = Direction(e, face);
        double w = e.Width, h = e.Height, depth = e.Depth;
        switch (AxisIndex(face))
        {
            case 0: w = length; break;
            case 1: h = length; break;
            default: depth = length; break;
        }
        return new PushPullResult(face, old, length, e.X + d.X * delta / 2, e.Y + d.Y * delta / 2, e.Z + d.Z * delta / 2, w, h, depth);
    }

    /// <summary>Подпись размера в HUD: «Длина», «Высота», «Толщина», «Ширина», «Глубина».</summary>
    public static string DimensionKey(ArchitecturalElement e, PushPullFace face) => AxisIndex(face) switch
    {
        1 => "DimHeight",
        0 => e.Type is ElementTypes.Wall or ElementTypes.Beam ? "DimLength" : "DimWidth",
        _ => e.Type is ElementTypes.Wall or ElementTypes.Slab or ElementTypes.Foundation or ElementTypes.Terrace ? "DimThickness" : "DimDepth"
    };

    /// <summary>Грань, активная по умолчанию для ввода с клавиатуры: длина стены/балки, высота колонны, иначе ширина.</summary>
    public static PushPullFace DefaultFace(ArchitecturalElement e)
    {
        var faces = FacesFor(e);
        var preferred = e.Type == ElementTypes.Column ? PushPullFace.PosY : PushPullFace.PosX;
        return faces.Contains(preferred) ? preferred : faces.Count > 0 ? faces[0] : PushPullFace.PosX;
    }

    /// <summary>Миллиметры с разделителем тысяч: «6 000 мм».</summary>
    public static string Millimeters(double meters, bool signed = false, string unit = "мм")
    {
        double mm = Math.Round(meters * 1000);
        string text = Math.Abs(mm).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ');
        string sign = mm < 0 ? "−" : signed ? "+" : string.Empty;
        return $"{sign}{text} {unit}";
    }
}
