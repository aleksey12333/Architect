using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AIArchitectStudio.Models.Geometry;
using HelixToolkit.SharpDX.Core;
using Num = System.Numerics;

namespace AIArchitectStudio.Services.Rendering;

/// <summary>
/// Мост независимой геометрии ядра (System.Numerics, <see cref="BimMesh"/>, <see cref="BimLineSet"/>) в типы HelixToolkit / SharpDX.
/// Единственное место ядра → вьюпорт, где появляются MeshGeometry3D и LineGeometry3D. Раскладка System.Numerics.Vector3/Vector2/
/// Matrix4x4 и SharpDX.Vector3/Vector2/Matrix совпадает (float подряд, матрица построчно), поэтому массивы копируются одним
/// блоком памяти (MemoryMarshal.Cast) прямо во внутренний буфер коллекций Helix — без поэлементного перебора и без промежуточных копий.
/// </summary>
public static class HelixMeshAdapter
{
    static HelixMeshAdapter()
    {
        if (Unsafe.SizeOf<Num.Vector3>() != Unsafe.SizeOf<SharpDX.Vector3>() || Unsafe.SizeOf<Num.Vector2>() != Unsafe.SizeOf<SharpDX.Vector2>() ||
            Unsafe.SizeOf<Num.Matrix4x4>() != Unsafe.SizeOf<SharpDX.Matrix>())
            throw new PlatformNotSupportedException("Раскладка векторов System.Numerics и SharpDX различается.");
    }

    /// <summary>Сетка Helix: вершины, нормали и индексы (и текстурные координаты, если заданы) — блочным копированием.</summary>
    public static MeshGeometry3D ToMeshGeometry(this BimMesh mesh)
    {
        var geometry = new MeshGeometry3D { Positions = Vectors(mesh.PositionSpan) };
        if (mesh.Normals is not null) geometry.Normals = Vectors(mesh.NormalSpan);
        if (mesh.TextureCoordinates is not null) geometry.TextureCoordinates = Vectors2(mesh.TextureCoordinateSpan);
        geometry.TriangleIndices = Ints(mesh.IndexSpan);
        return geometry;
    }

    /// <summary>Линии Helix (рёбра, размеры, армирование).</summary>
    public static LineGeometry3D ToLineGeometry(this BimLineSet lines) => new()
    {
        Positions = Vectors(lines.PositionSpan),
        Indices = Ints(lines.IndexSpan)
    };

    /// <summary>Отрезки по парам точек (0–1, 2–3, …) в локальной системе.</summary>
    public static LineGeometry3D ToLineGeometry(IReadOnlyList<Num.Vector3> pairs) => BimLineSet.FromSegments(pairs).ToLineGeometry();

    public static SharpDX.Vector3 ToDx(this Num.Vector3 v) => new(v.X, v.Y, v.Z);

    public static Num.Vector3 ToBim(this SharpDX.Vector3 v) => new(v.X, v.Y, v.Z);

    public static SharpDX.Matrix ToDx(this Num.Matrix4x4 m) => Unsafe.As<Num.Matrix4x4, SharpDX.Matrix>(ref m);

    public static SharpDX.BoundingBox ToDx(this BimBounds b) => new(b.Minimum.ToDx(), b.Maximum.ToDx());

    private static Vector3Collection Vectors(ReadOnlySpan<Num.Vector3> source)
    {
        var target = new Vector3Collection(source.Length);
        target.Resize(source.Length, false);
        MemoryMarshal.Cast<Num.Vector3, SharpDX.Vector3>(source).CopyTo(target.GetInternalArray());
        return target;
    }

    private static Vector2Collection Vectors2(ReadOnlySpan<Num.Vector2> source)
    {
        var target = new Vector2Collection(source.Length);
        target.Resize(source.Length, false);
        MemoryMarshal.Cast<Num.Vector2, SharpDX.Vector2>(source).CopyTo(target.GetInternalArray());
        return target;
    }

    private static IntCollection Ints(ReadOnlySpan<int> source)
    {
        var target = new IntCollection(source.Length);
        target.Resize(source.Length, false);
        source.CopyTo(target.GetInternalArray());
        return target;
    }
}
