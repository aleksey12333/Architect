using System.Numerics;
using System.Runtime.InteropServices;

namespace AIArchitectStudio.Models.Geometry;

/// <summary>
/// Независимая треугольная сетка: вершины, нормали, текстурные координаты (могут отсутствовать) и индексы треугольников.
/// Списки передаются сетке во владение без копирования (построитель после этого их не меняет); наружу — только чтение
/// и непрерывные блоки памяти для быстрой передачи в графический движок.
/// </summary>
public sealed class BimMesh
{
    private readonly List<Vector3> _positions;
    private readonly List<Vector3>? _normals;
    private readonly List<Vector2>? _textureCoordinates;
    private readonly List<int> _indices;

    public BimMesh(List<Vector3> positions, List<Vector3>? normals, List<int> triangleIndices, List<Vector2>? textureCoordinates = null)
    {
        _positions = positions;
        _normals = normals;
        _indices = triangleIndices;
        _textureCoordinates = textureCoordinates;
    }

    public IReadOnlyList<Vector3> Positions => _positions;

    /// <summary>Нормали вершин (null — не заданы).</summary>
    public IReadOnlyList<Vector3>? Normals => _normals;

    /// <summary>Текстурные координаты (null — не заданы; процедурная геометрия BIM их не строит).</summary>
    public IReadOnlyList<Vector2>? TextureCoordinates => _textureCoordinates;

    public IReadOnlyList<int> TriangleIndices => _indices;

    public int VertexCount => _positions.Count;

    public int TriangleCount => _indices.Count / 3;

    public ReadOnlySpan<Vector3> PositionSpan => CollectionsMarshal.AsSpan(_positions);

    public ReadOnlySpan<Vector3> NormalSpan => CollectionsMarshal.AsSpan(_normals);

    public ReadOnlySpan<Vector2> TextureCoordinateSpan => CollectionsMarshal.AsSpan(_textureCoordinates);

    public ReadOnlySpan<int> IndexSpan => CollectionsMarshal.AsSpan(_indices);

    public BimBounds Bounds => BimBounds.FromPoints(_positions);
}

/// <summary>Отрезки линий (рёбра, размерные линии): пары вершин и индексы концов.</summary>
public sealed class BimLineSet
{
    private readonly List<Vector3> _positions;
    private readonly List<int> _indices;

    public BimLineSet() : this([], []) { }

    public BimLineSet(int capacity) : this(new List<Vector3>(capacity * 2), new List<int>(capacity * 2)) { }

    private BimLineSet(List<Vector3> positions, List<int> indices)
    {
        _positions = positions;
        _indices = indices;
    }

    public IReadOnlyList<Vector3> Positions => _positions;

    public IReadOnlyList<int> Indices => _indices;

    public int LineCount => _indices.Count / 2;

    public bool IsEmpty => _indices.Count == 0;

    public ReadOnlySpan<Vector3> PositionSpan => CollectionsMarshal.AsSpan(_positions);

    public ReadOnlySpan<int> IndexSpan => CollectionsMarshal.AsSpan(_indices);

    /// <summary>Отрезок a–b: две новые вершины и их индексы подряд.</summary>
    public void AddLine(Vector3 a, Vector3 b)
    {
        _positions.Add(a);
        _indices.Add(_positions.Count - 1);
        _positions.Add(b);
        _indices.Add(_positions.Count - 1);
    }

    /// <summary>Отрезки по парам точек списка (0–1, 2–3, …).</summary>
    public static BimLineSet FromSegments(IReadOnlyList<Vector3> pairs)
    {
        var lines = new BimLineSet(pairs.Count / 2);
        for (int i = 0; i + 1 < pairs.Count; i += 2) lines.AddLine(pairs[i], pairs[i + 1]);
        return lines;
    }

    /// <summary>Концы i-го отрезка.</summary>
    public (Vector3 A, Vector3 B) Line(int i) => (_positions[_indices[2 * i]], _positions[_indices[2 * i + 1]]);
}
