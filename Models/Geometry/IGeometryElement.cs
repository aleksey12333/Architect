using System.Numerics;

namespace AIArchitectStudio.Models.Geometry;

/// <summary>Часть геометрии элемента с одним материалом.</summary>
public sealed record BimMeshPart(BimMesh Mesh, MaterialKind Material);

/// <summary>
/// Геометрия BIM-элемента без привязки к графическому движку: части-сетки по материалам и рёбра в локальной системе элемента,
/// мировая матрица и локальный габарит. Строится ядром (ElementGeometryBuilder), потребляется экспортом, листами ГОСТ,
/// Section Box, рулеткой и вьюпортом (через адаптер сеток).
/// </summary>
public interface IGeometryElement
{
    IReadOnlyList<BimMeshPart> Parts { get; }

    /// <summary>Рёбра (CAD-контуры); null — элемент без рёбер.</summary>
    BimLineSet? Edges { get; }

    /// <summary>Локальная система → мир (строка-вектор, перенос в M41…M43).</summary>
    Matrix4x4 Transform { get; }

    BimBounds LocalBounds { get; }

    bool IsEmpty { get; }
}

/// <summary>Готовая геометрия BIM-элемента в его локальной системе + мировая матрица.</summary>
public sealed class ElementGeometry : IGeometryElement
{
    public required IReadOnlyList<BimMeshPart> Parts { get; init; }
    public BimLineSet? Edges { get; init; }
    public Matrix4x4 Transform { get; init; } = Matrix4x4.Identity;
    public BimBounds LocalBounds { get; init; }
    public bool IsEmpty => Parts.Count == 0;
}

public static class GeometryElementExtensions
{
    /// <summary>8 углов локального габарита в мировых координатах (нумерация <see cref="BimBounds.GetCorners"/>).</summary>
    public static Vector3[] WorldCorners(this IGeometryElement g)
    {
        var corners = g.LocalBounds.GetCorners();
        var m = g.Transform;
        for (int i = 0; i < corners.Length; i++) corners[i] = BimMath.TransformCoordinate(corners[i], m);
        return corners;
    }

    /// <summary>Мировой габарит (по углам локального габарита).</summary>
    public static BimBounds WorldBounds(this IGeometryElement g) => BimBounds.FromPoints(g.WorldCorners());
}
