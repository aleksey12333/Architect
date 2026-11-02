using System.Numerics;

namespace AIArchitectStudio.Models.Geometry;

/// <summary>Габарит, выровненный по осям (м): минимум и максимум.</summary>
public readonly record struct BimBounds(Vector3 Minimum, Vector3 Maximum)
{
    public Vector3 Size => Maximum - Minimum;

    public Vector3 Center => (Minimum + Maximum) / 2;

    /// <summary>Габарит точек; пустой набор — нулевой габарит (default).</summary>
    public static BimBounds FromPoints(IReadOnlyList<Vector3> points)
    {
        if (points.Count == 0) return default;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < points.Count; i++)
        {
            min = BimMath.Min(min, points[i]);
            max = BimMath.Max(max, points[i]);
        }
        return new BimBounds(min, max);
    }

    /// <summary>
    /// 8 углов: грань max Z (0 — (min X, max Y), 1 — (max X, max Y), 2 — (max X, min Y), 3 — (min X, min Y)), затем те же
    /// четыре угла на грани min Z (4–7).
    /// </summary>
    public Vector3[] GetCorners() =>
    [
        new(Minimum.X, Maximum.Y, Maximum.Z), new(Maximum.X, Maximum.Y, Maximum.Z),
        new(Maximum.X, Minimum.Y, Maximum.Z), new(Minimum.X, Minimum.Y, Maximum.Z),
        new(Minimum.X, Maximum.Y, Minimum.Z), new(Maximum.X, Maximum.Y, Minimum.Z),
        new(Maximum.X, Minimum.Y, Minimum.Z), new(Minimum.X, Minimum.Y, Minimum.Z)
    ];

    /// <summary>12 рёбер параллелепипеда в нумерации <see cref="GetCorners"/>: грань max Z, грань min Z, боковые рёбра.</summary>
    public static readonly (int A, int B)[] Edges =
        [(0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7)];

    public BimBounds Inflate(float amount) => new(Minimum - new Vector3(amount), Maximum + new Vector3(amount));
}
