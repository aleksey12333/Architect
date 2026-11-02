using System.Numerics;

namespace AIArchitectStudio.Models.Geometry;

/// <summary>
/// Векторная и матричная арифметика геометрического ядра на типах System.Numerics. Каждая формула записана скалярно, с тем же
/// порядком операций, что и в прежней математике SharpDX: результат не зависит от SIMD-реализации .NET (Dot через dpps,
/// Normalize делением, CreateRotation через MathF) и совпадает до бита с эталоном ядра. Покомпонентные операторы
/// (+, −, умножение и деление на скаляр) — точные операции IEEE 754 и используются напрямую.
/// </summary>
public static class BimMath
{
    /// <summary>Порог «нулевой» длины при нормализации (как MathUtil.ZeroTolerance).</summary>
    public const float ZeroTolerance = 1e-6f;

    // ───────────────────────────── Vector3 ─────────────────────────────

    /// <summary>
    /// −v сменой знака каждой компоненты (у нулевых — −0): знак нуля нормали входит в биты геометрии, поэтому результат
    /// не должен зависеть от того, как унарный минус System.Numerics реализован в конкретной версии .NET (Zero − v даёт +0).
    /// </summary>
    public static Vector3 Negate(Vector3 v) => new(-v.X, -v.Y, -v.Z);

    public static readonly Vector3 NegativeUnitX = Negate(Vector3.UnitX);
    public static readonly Vector3 NegativeUnitY = Negate(Vector3.UnitY);
    public static readonly Vector3 NegativeUnitZ = Negate(Vector3.UnitZ);

    public static float Dot(Vector3 a, Vector3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static Vector3 Cross(Vector3 a, Vector3 b) =>
        new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    public static float LengthSquared(Vector3 v) => (v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z);

    public static float Length(Vector3 v) => (float)Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));

    /// <summary>Единичный вектор умножением на 1/|v|; вектор короче <see cref="ZeroTolerance"/> возвращается без изменений.</summary>
    public static Vector3 Normalize(Vector3 v)
    {
        float length = Length(v);
        if (Math.Abs(length) < ZeroTolerance) return v;
        float inv = 1.0f / length;
        return new Vector3(v.X * inv, v.Y * inv, v.Z * inv);
    }

    public static Vector3 Min(Vector3 a, Vector3 b) => new(a.X < b.X ? a.X : b.X, a.Y < b.Y ? a.Y : b.Y, a.Z < b.Z ? a.Z : b.Z);

    public static Vector3 Max(Vector3 a, Vector3 b) => new(a.X > b.X ? a.X : b.X, a.Y > b.Y ? a.Y : b.Y, a.Z > b.Z ? a.Z : b.Z);

    /// <summary>Точка × матрица (строка-вектор, перенос в M41…M43) с делением на W.</summary>
    public static Vector3 TransformCoordinate(Vector3 p, in Matrix4x4 m)
    {
        float x = (p.X * m.M11) + (p.Y * m.M21) + (p.Z * m.M31) + m.M41;
        float y = (p.X * m.M12) + (p.Y * m.M22) + (p.Z * m.M32) + m.M42;
        float z = (p.X * m.M13) + (p.Y * m.M23) + (p.Z * m.M33) + m.M43;
        float w = 1f / ((p.X * m.M14) + (p.Y * m.M24) + (p.Z * m.M34) + m.M44);
        return new Vector3(x * w, y * w, z * w);
    }

    /// <summary>Направление × матрица без переноса (нормали — после этого нормализуются).</summary>
    public static Vector3 TransformNormal(Vector3 n, in Matrix4x4 m) => new(
        (n.X * m.M11) + (n.Y * m.M21) + (n.Z * m.M31),
        (n.X * m.M12) + (n.Y * m.M22) + (n.Z * m.M32),
        (n.X * m.M13) + (n.Y * m.M23) + (n.Z * m.M33));

    // ───────────────────────────── Vector2 ─────────────────────────────

    public static float LengthSquared(Vector2 v) => (v.X * v.X) + (v.Y * v.Y);

    public static float Length(Vector2 v) => (float)Math.Sqrt((v.X * v.X) + (v.Y * v.Y));

    public static Vector2 Normalize(Vector2 v)
    {
        float length = Length(v);
        if (Math.Abs(length) < ZeroTolerance) return v;
        float inv = 1.0f / length;
        return new Vector2(v.X * inv, v.Y * inv);
    }

    /// <summary>Линейная интерполяция (1 − t)·a + t·b (точно попадает в концы при t = 0 и t = 1).</summary>
    public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t));

    public static float Lerp(float from, float to, float t) => (1 - t) * from + t * to;

    // ───────────────────────────── Matrix4x4 ─────────────────────────────

    /// <summary>Произведение a · b (сначала a, затем b — для строк-векторов).</summary>
    public static Matrix4x4 Multiply(in Matrix4x4 a, in Matrix4x4 b) => new(
        (a.M11 * b.M11) + (a.M12 * b.M21) + (a.M13 * b.M31) + (a.M14 * b.M41),
        (a.M11 * b.M12) + (a.M12 * b.M22) + (a.M13 * b.M32) + (a.M14 * b.M42),
        (a.M11 * b.M13) + (a.M12 * b.M23) + (a.M13 * b.M33) + (a.M14 * b.M43),
        (a.M11 * b.M14) + (a.M12 * b.M24) + (a.M13 * b.M34) + (a.M14 * b.M44),
        (a.M21 * b.M11) + (a.M22 * b.M21) + (a.M23 * b.M31) + (a.M24 * b.M41),
        (a.M21 * b.M12) + (a.M22 * b.M22) + (a.M23 * b.M32) + (a.M24 * b.M42),
        (a.M21 * b.M13) + (a.M22 * b.M23) + (a.M23 * b.M33) + (a.M24 * b.M43),
        (a.M21 * b.M14) + (a.M22 * b.M24) + (a.M23 * b.M34) + (a.M24 * b.M44),
        (a.M31 * b.M11) + (a.M32 * b.M21) + (a.M33 * b.M31) + (a.M34 * b.M41),
        (a.M31 * b.M12) + (a.M32 * b.M22) + (a.M33 * b.M32) + (a.M34 * b.M42),
        (a.M31 * b.M13) + (a.M32 * b.M23) + (a.M33 * b.M33) + (a.M34 * b.M43),
        (a.M31 * b.M14) + (a.M32 * b.M24) + (a.M33 * b.M34) + (a.M34 * b.M44),
        (a.M41 * b.M11) + (a.M42 * b.M21) + (a.M43 * b.M31) + (a.M44 * b.M41),
        (a.M41 * b.M12) + (a.M42 * b.M22) + (a.M43 * b.M32) + (a.M44 * b.M42),
        (a.M41 * b.M13) + (a.M42 * b.M23) + (a.M43 * b.M33) + (a.M44 * b.M43),
        (a.M41 * b.M14) + (a.M42 * b.M24) + (a.M43 * b.M34) + (a.M44 * b.M44));

    /// <summary>Поворот вокруг X (радианы; синус и косинус — в double, как в прежнем ядре).</summary>
    public static Matrix4x4 RotationX(float angle)
    {
        float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
        var m = Matrix4x4.Identity;
        m.M22 = cos;
        m.M23 = sin;
        m.M32 = -sin;
        m.M33 = cos;
        return m;
    }

    public static Matrix4x4 RotationY(float angle)
    {
        float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
        var m = Matrix4x4.Identity;
        m.M11 = cos;
        m.M13 = -sin;
        m.M31 = sin;
        m.M33 = cos;
        return m;
    }

    public static Matrix4x4 RotationZ(float angle)
    {
        float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
        var m = Matrix4x4.Identity;
        m.M11 = cos;
        m.M12 = sin;
        m.M21 = -sin;
        m.M22 = cos;
        return m;
    }

    public static Matrix4x4 Scaling(float x, float y, float z)
    {
        var m = Matrix4x4.Identity;
        m.M11 = x;
        m.M22 = y;
        m.M33 = z;
        return m;
    }

    public static Matrix4x4 Translation(float x, float y, float z)
    {
        var m = Matrix4x4.Identity;
        m.M41 = x;
        m.M42 = y;
        m.M43 = z;
        return m;
    }

    /// <summary>16 чисел матрицы построчно (M11, M12, …, M44) — для отпечатков и сериализации.</summary>
    public static float[] ToArray(in Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
}
