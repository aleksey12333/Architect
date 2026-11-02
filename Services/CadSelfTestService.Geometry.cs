#if DEBUG
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.Rendering;
using Dx = SharpDX;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест геометрического слоя (фаза 6): арифметика ядра BimMath ≡ прежней математике SharpDX до бита, ядро (Models, Rendering,
/// Services) не ссылается на HelixToolkit/SharpDX ни сигнатурами, ни кодом методов, адаптер BimMesh → MeshGeometry3D переносит
/// сетки без потерь, рулетка строит привязки и размерные линии на независимой геометрии, построение и перенос во вьюпорт — в нормативе.
/// Сам автотест сравнивает с SharpDX и потому ссылается на него (исключён из сканирования вместе с адаптером и материалами рендера).
/// </summary>
public static partial class CadSelfTestService
{
    private static int CheckGeometryLayer(StringBuilder report)
    {
        int failures = 0;
        CheckGeometryMath(report, ref failures);
        CheckCoreIndependence(report, ref failures);

        var scenes = BaselineSources().ToList();
        var geometries = new List<(string Scene, ArchitecturalElement Element, ElementGeometry Geometry)>();
        foreach (var s in scenes)
            foreach (var e in s.Elements)
                geometries.Add((s.Name, e, BuildBaselineGeometry(e, s.Elements, s.Groups)));
        CheckHelixAdapter(report, ref failures, geometries);
        CheckMeasureGeometry(report, ref failures, geometries);
        CheckGeometrySpeed(report, ref failures, scenes);
        return failures;
    }

    private static ElementGeometry BuildBaselineGeometry(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> all, IReadOnlyList<BuildingGroup> groups)
    {
        var group = groups.FirstOrDefault(g => g.Id == e.BuildingGroupId);
        return ElementGeometryBuilder.Build(e, SceneStateService.OpeningsFor(e, all), new GeometryContext(SceneStateService.AllLevels, group?.OriginX ?? 0, group?.OriginZ ?? 0));
    }

    // ───────────── 1. BimMath ≡ SharpDX ─────────────

    /// <summary>
    /// Случайные входы (в том числе ±0, 1e-7 и порядки 1e-3…1e3): каждая операция BimMath даёт те же биты, что SharpDX.Mathematics.
    /// Для сравнения считается и расхождение штатных методов System.Numerics — оно и есть причина явных формул.
    /// </summary>
    private static void CheckGeometryMath(StringBuilder report, ref int failures)
    {
        var rnd = new Random(20261030);
        float R() => rnd.Next(10) switch
        {
            0 => 0f,
            1 => -0f,
            2 => (float)(rnd.NextDouble() * 1e-7),
            _ => (float)((rnd.NextDouble() - 0.5) * Math.Pow(10, rnd.Next(-3, 4)))
        };
        Vector3 V() => new(R(), R(), R());
        Matrix4x4 M()
        {
            var m = BimMath.Multiply(BimMath.Multiply(BimMath.Multiply(BimMath.RotationX(R()), BimMath.RotationZ(R())), BimMath.RotationY(R())), BimMath.Translation(R(), R(), R()));
            return rnd.Next(3) == 0 ? BimMath.Multiply(BimMath.Scaling(R(), 1, R()), m) : m;
        }
        static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        static bool Same3(Vector3 a, Dx.Vector3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
        static bool SameM(Matrix4x4 a, Dx.Matrix b)
        {
            var x = BimMath.ToArray(a);
            var y = b.ToArray();
            for (int i = 0; i < 16; i++) if (!Same(x[i], y[i])) return false;
            return true;
        }

        var bim = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var system = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Bim(string op, bool ok) { if (!ok) bim[op] = bim.GetValueOrDefault(op) + 1; }
        void Sys(string op, bool ok) { if (!ok) system[op] = system.GetValueOrDefault(op) + 1; }
        const int samples = 200_000;
        for (int i = 0; i < samples; i++)
        {
            var a = V();
            var b = V();
            float s = R();
            if (s == 0) s = 0.5f;
            Dx.Vector3 da = a.ToDx(), db = b.ToDx();
            Bim("Dot", Same(BimMath.Dot(a, b), Dx.Vector3.Dot(da, db)));
            Bim("Cross", Same3(BimMath.Cross(a, b), Dx.Vector3.Cross(da, db)));
            Bim("Length", Same(BimMath.Length(a), da.Length()) && Same(BimMath.LengthSquared(a), da.LengthSquared()));
            Bim("Normalize", Same3(BimMath.Normalize(a), Dx.Vector3.Normalize(da)));
            Bim("Negate", Same3(BimMath.Negate(a), -da));
            Bim("Min/Max", Same3(BimMath.Min(a, b), Dx.Vector3.Min(da, db)) && Same3(BimMath.Max(a, b), Dx.Vector3.Max(da, db)));
            Bim("+ − × ÷", Same3(a + b, da + db) && Same3(a - b, da - db) && Same3(a * s, da * s) && Same3(a / s, da / s));
            Vector2 a2 = new(a.X, a.Y), b2 = new(b.X, b.Y);
            Dx.Vector2 da2 = new(a.X, a.Y), db2 = new(b.X, b.Y);
            var lerp = BimMath.Lerp(a2, b2, s);
            var dLerp = Dx.Vector2.Lerp(da2, db2, s);
            var norm2 = BimMath.Normalize(a2);
            var dNorm2 = da2;
            dNorm2.Normalize();
            Bim("Vector2", Same(lerp.X, dLerp.X) && Same(lerp.Y, dLerp.Y) && Same(norm2.X, dNorm2.X) && Same(norm2.Y, dNorm2.Y) && Same(BimMath.Length(a2), da2.Length()));
            Sys("Vector3.Normalize", Same3(Vector3.Normalize(a), Dx.Vector3.Normalize(da)));
            Sys("Vector3.Dot", Same(Vector3.Dot(a, b), Dx.Vector3.Dot(da, db)));
            if (i % 4 != 0) continue;

            float angle = R();
            Bim("Rotation", SameM(BimMath.RotationX(angle), Dx.Matrix.RotationX(angle)) && SameM(BimMath.RotationY(angle), Dx.Matrix.RotationY(angle)) &&
                            SameM(BimMath.RotationZ(angle), Dx.Matrix.RotationZ(angle)));
            Sys("Matrix4x4.CreateRotationY", SameM(Matrix4x4.CreateRotationY(angle), Dx.Matrix.RotationY(angle)));
            var m = M();
            var n = M();
            Bim("Multiply", SameM(BimMath.Multiply(m, n), m.ToDx() * n.ToDx()));
            Bim("TransformCoordinate", Same3(BimMath.TransformCoordinate(a, m), Dx.Vector3.TransformCoordinate(da, m.ToDx())));
            Bim("TransformNormal", Same3(BimMath.TransformNormal(a, m), Dx.Vector3.TransformNormal(da, m.ToDx())));
            var points = new[] { a, b, V(), V() };
            var bounds = BimBounds.FromPoints(points);
            var dBounds = Dx.BoundingBox.FromPoints(points.Select(p => p.ToDx()).ToArray());
            Bim("BimBounds", Same3(bounds.Minimum, dBounds.Minimum) && Same3(bounds.Maximum, dBounds.Maximum) && Same3(bounds.Center, dBounds.Center) &&
                             bounds.GetCorners().Zip(dBounds.GetCorners()).All(p => Same3(p.First, p.Second)));
        }
        string Summary(SortedDictionary<string, int> d) => d.Count == 0 ? "расхождений нет" : string.Join(", ", d.Select(p => $"{p.Key} {p.Value}"));
        Check(report, ref failures, bim.Count == 0,
            $"BimMath ≡ SharpDX.Mathematics до бита: {samples:N0} случайных входов (±0, 1e-7, 1e-3…1e3) — Dot, Cross, Length, Normalize, Negate, Min/Max, " +
            $"+ − × ÷, Vector2 (Lerp, Normalize, Length), Rotation X/Y/Z, Multiply, TransformCoordinate/Normal, габарит и углы: {Summary(bim)}; " +
            $"штатные System.Numerics для сравнения: {Summary(system)} — поэтому формулы ядра явные");
    }

    /// <summary>Равенство векторов SharpDX по битам (их == сравнивает приближённо, NearEqual).</summary>
    private static bool SameVector(Dx.Vector3 a, Dx.Vector3 b) =>
        BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X) && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) &&
        BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    // ───────────── 2. Ядро без HelixToolkit / SharpDX ─────────────

    private static readonly Dictionary<short, OpCode> IlOpCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    /// <summary>Типы слоя отображения, оставшиеся рядом с ядром: материалы рендера (PhongMaterial, Color4) и адаптер сеток.</summary>
    private static bool IsRenderBridge(Type outer) =>
        outer.Namespace == "AIArchitectStudio.Services.Rendering" || outer == typeof(ArchitecturalMaterials) || outer == typeof(StressPalette) ||
        outer == typeof(CadSelfTestService);

    private static bool IsUiAssembly(Assembly a) => a.GetName().Name is { } n && (n.StartsWith("HelixToolkit", StringComparison.Ordinal) || n.StartsWith("SharpDX", StringComparison.Ordinal));

    private static bool RefersUi(Type? t)
    {
        if (t is null) return false;
        if (t.HasElementType) return RefersUi(t.GetElementType());
        if (t.IsGenericParameter) return false;
        if (IsUiAssembly(t.Assembly)) return true;
        return t.IsGenericType && !t.IsGenericTypeDefinition && t.GetGenericArguments().Any(RefersUi);
    }

    private static bool RefersUi(MemberInfo member) => member switch
    {
        Type t => RefersUi(t),
        FieldInfo f => RefersUi(f.DeclaringType) || RefersUi(f.FieldType),
        MethodInfo m => RefersUi(m.DeclaringType) || RefersUi(m.ReturnType) || m.GetParameters().Any(p => RefersUi(p.ParameterType)) ||
                        m.IsGenericMethod && m.GetGenericArguments().Any(RefersUi),
        MethodBase c => RefersUi(c.DeclaringType) || c.GetParameters().Any(p => RefersUi(p.ParameterType)),
        _ => false
    };

    /// <summary>
    /// Все типы ядра (пространства имён Models, Rendering, Services, включая замыкания и вложенные типы) — базовые типы, интерфейсы,
    /// поля, сигнатуры, локальные переменные и каждый токен IL методов (вызовы, поля, типы, ldtoken) — без ссылок на сборки HelixToolkit и SharpDX.
    /// </summary>
    private static void CheckCoreIndependence(StringBuilder report, ref int failures)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var hits = new SortedSet<string>(StringComparer.Ordinal);
        int types = 0, methods = 0, tokens = 0, unresolved = 0;
        foreach (var type in typeof(CadSelfTestService).Assembly.GetTypes())
        {
            var outer = type;
            while (outer.DeclaringType is { } d) outer = d;
            string ns = outer.Namespace ?? string.Empty;
            if (!(ns.StartsWith("AIArchitectStudio.Models", StringComparison.Ordinal) || ns.StartsWith("AIArchitectStudio.Rendering", StringComparison.Ordinal) ||
                  ns.StartsWith("AIArchitectStudio.Services", StringComparison.Ordinal)) || IsRenderBridge(outer))
                continue;
            types++;
            void Hit(string what) => hits.Add($"{type.Name}: {what}");
            if (RefersUi(type.BaseType)) Hit("базовый тип " + type.BaseType);
            foreach (var i in type.GetInterfaces().Where(RefersUi)) Hit("интерфейс " + i);
            foreach (var f in type.GetFields(all).Where(f => RefersUi(f.FieldType))) Hit("поле " + f.Name);
            var targs = type.IsGenericType ? type.GetGenericArguments() : null;
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                methods++;
                if (RefersUi(method)) Hit("сигнатура " + method.Name);
                if (method.GetMethodBody() is not { } body) continue;
                foreach (var local in body.LocalVariables.Where(l => RefersUi(l.LocalType))) Hit($"{method.Name}: локальная {local.LocalType}");
                var margs = method.IsGenericMethod ? method.GetGenericArguments() : null;
                var il = body.GetILAsByteArray() ?? [];
                for (int p = 0; p < il.Length;)
                {
                    OpCode op;
                    if (il[p] == 0xFE) { op = IlOpCodes[unchecked((short)(0xFE00 | il[p + 1]))]; p += 2; }
                    else op = IlOpCodes[il[p++]];
                    switch (op.OperandType)
                    {
                        case OperandType.InlineNone: break;
                        case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar: p += 1; break;
                        case OperandType.InlineVar: p += 2; break;
                        case OperandType.InlineI8 or OperandType.InlineR: p += 8; break;
                        case OperandType.InlineSwitch: p += 4 + 4 * BitConverter.ToInt32(il, p); break;
                        case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType:
                        {
                            int token = BitConverter.ToInt32(il, p);
                            p += 4;
                            tokens++;
                            try
                            {
                                if (type.Module.ResolveMember(token, targs, margs) is { } member && RefersUi(member)) Hit($"{method.Name}: {op.Name} {member.DeclaringType?.Name}.{member.Name}");
                            }
                                            catch (Exception) { unresolved++; }
                            break;
                        }
                        default: p += 4; break;
                    }
                }
            }
        }
        Check(report, ref failures, hits.Count == 0 && types > 100 && tokens > 10_000,
            $"ядро независимо от UI-библиотек: {types} типов Models/Rendering/Services, {methods:N0} методов, {tokens:N0} токенов IL (не разрешено {unresolved}) — " +
            $"ссылок на HelixToolkit/SharpDX {hits.Count} (вне сканирования — мост отображения: Services.Rendering.HelixMeshAdapter, " +
            $"ArchitecturalMaterials/StressPalette, сам автотест)" + (hits.Count > 0 ? ": " + string.Join("; ", hits.Take(8)) : string.Empty));
    }

    // ───────────── 3. Адаптер BimMesh → MeshGeometry3D ─────────────

    private static void CheckHelixAdapter(StringBuilder report, ref int failures, List<(string Scene, ArchitecturalElement Element, ElementGeometry Geometry)> geometries)
    {
        static bool SameBits<TFrom, TTo>(ReadOnlySpan<TFrom> a, TTo[] b, int count) where TFrom : unmanaged where TTo : unmanaged =>
            count == a.Length && System.Runtime.InteropServices.MemoryMarshal.AsBytes(a).SequenceEqual(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan(0, count)));
        var broken = new List<string>();
        int meshes = 0, lineSets = 0;
        long vertices = 0;
        foreach (var (scene, e, g) in geometries)
        {
            foreach (var part in g.Parts)
            {
                var mesh = part.Mesh;
                var helix = mesh.ToMeshGeometry();
                meshes++;
                vertices += mesh.VertexCount;
                bool ok = SameBits(mesh.PositionSpan, helix.Positions.GetInternalArray(), helix.Positions.Count) &&
                          mesh.Normals is not null && SameBits(mesh.NormalSpan, helix.Normals.GetInternalArray(), helix.Normals.Count) &&
                          SameBits(mesh.IndexSpan, helix.TriangleIndices.GetInternalArray(), helix.TriangleIndices.Count) &&
                          helix.TextureCoordinates is null == mesh.TextureCoordinates is null;
                if (!ok) broken.Add($"{scene}/{e.Name}: сетка {part.Material}");
            }
            if (g.Edges is { } edges)
            {
                var helix = edges.ToLineGeometry();
                lineSets++;
                if (!SameBits(edges.PositionSpan, helix.Positions.GetInternalArray(), helix.Positions.Count) ||
                    !SameBits(edges.IndexSpan, helix.Indices.GetInternalArray(), helix.Indices.Count))
                    broken.Add($"{scene}/{e.Name}: рёбра");
            }
            var m = g.Transform.ToDx();
            var b = g.LocalBounds.ToDx();
            if (!BimMath.ToArray(g.Transform).Select(BitConverter.SingleToInt32Bits).SequenceEqual(m.ToArray().Select(BitConverter.SingleToInt32Bits)) ||
                !SameVector(b.Minimum, g.LocalBounds.Minimum.ToDx()) || !SameVector(b.Maximum, g.LocalBounds.Maximum.ToDx()))
                broken.Add($"{scene}/{e.Name}: матрица или габарит");
        }
        Check(report, ref failures, broken.Count == 0 && meshes > 0,
            $"HelixMeshAdapter: {meshes:N0} сеток ({vertices:N0} вершин) и {lineSets:N0} наборов рёбер 7 сцен эталона → MeshGeometry3D / LineGeometry3D " +
            "блочным копированием без потерь (вершины, нормали, индексы, отсутствие UV, матрица, габарит — до бита)" +
            (broken.Count > 0 ? ": " + string.Join("; ", broken.Take(5)) : string.Empty));
    }

    // ───────────── 4. Рулетка на независимой геометрии ─────────────

    /// <summary>
    /// Привязка рулетки: углы и середины рёбер габарита из IGeometryElement ≡ прежнему пути вьюпорта (BoundingBox.GetCorners +
    /// TransformCoordinate SharpDX) до бита на всех элементах эталона; выбор угла/середины по экранному расстоянию; размерная линия
    /// с засечками в BimLineSet ≡ прежнему построению на SharpDX.
    /// </summary>
    private static void CheckMeasureGeometry(StringBuilder report, ref int failures, List<(string Scene, ArchitecturalElement Element, ElementGeometry Geometry)> geometries)
    {
        var broken = new List<string>();
        int candidates = 0;
        foreach (var (scene, e, g) in geometries)
        {
            if (g.IsEmpty) continue;
            var core = MeasurementService.BoxSnapCandidates(g);
            var corners = g.LocalBounds.ToDx().GetCorners().Select(c => Dx.Vector3.TransformCoordinate(c, g.Transform.ToDx())).ToArray();
            var legacy = corners.Select(c => (c, false)).Concat(BimBounds.Edges.Select(p => ((corners[p.A] + corners[p.B]) / 2, true))).ToList();
            candidates += core.Count;
            if (core.Count != legacy.Count || core.Zip(legacy).Any(p => !SameVector(p.First.Point.ToDx(), p.Second.Item1) || p.First.IsMidpoint != p.Second.Item2))
                broken.Add($"{scene}/{e.Name}: привязки габарита");
        }

        // Экранная проекция для выбора: вид сверху, 100 px/м. Курсор у угла (2 px) и у середины ребра (1 px) колонны 1 × 1 м.
        var column = new ElementGeometry { Parts = [], Transform = BimMath.Translation(10, 0, 5), LocalBounds = new BimBounds(new Vector3(-0.5f, -1.5f, -0.5f), new Vector3(0.5f, 1.5f, 0.5f)) };
        var list = MeasurementService.BoxSnapCandidates(column);
        double? Screen(Vector3 p, Vector2 cursor) => Math.Sqrt(Math.Pow((p.X - cursor.X) * 100, 2) + Math.Pow((p.Z - cursor.Y) * 100, 2));
        var nearCorner = MeasurementService.PickSnap(list, p => Screen(p, new Vector2(10.52f, 5.5f)), 12);
        var nearMid = MeasurementService.PickSnap(list, p => Screen(p, new Vector2(10.0f, 5.51f)), 12);
        var none = MeasurementService.PickSnap(list, p => Screen(p, new Vector2(10.25f, 5.25f)), 12);
        bool pick = nearCorner is { Candidate.IsMidpoint: false } c && Math.Abs(c.Candidate.Point.X - 10.5f) < 1e-5 && Math.Abs(c.Distance - 2) < 1e-3 &&
                    nearMid is { Candidate.IsMidpoint: true } mid && Math.Abs(mid.Candidate.Point.X - 10f) < 1e-5 && none is null;

        // Размерная линия: прежний путь вьюпорта (SharpDX) против MeasurementService.AddDimensionStrokes (BimLineSet).
        var rnd = new Random(7);
        int strokeMismatch = 0;
        for (int i = 0; i < 2000; i++)
        {
            Vector3 a = new((float)(rnd.NextDouble() * 40 - 20), (float)(rnd.NextDouble() * 20), (float)(rnd.NextDouble() * 40 - 20));
            Vector3 b = a + new Vector3((float)(rnd.NextDouble() * 12 - 6), rnd.Next(3) == 0 ? 0 : (float)(rnd.NextDouble() * 6), (float)(rnd.NextDouble() * 12 - 6));
            var look = BimMath.Normalize(new Vector3((float)(rnd.NextDouble() - 0.5), -1, (float)(rnd.NextDouble() - 0.5)));
            double wpp = rnd.NextDouble() * 0.05;
            var lines = new BimLineSet();
            var label = MeasurementService.AddDimensionStrokes(lines, a, b, look, wpp);

            Dx.Vector3 da = a.ToDx(), db = b.ToDx();
            var dmid = (da + db) / 2;
            var (tick, overshoot) = MeasurementService.StrokeSizes((db - da).Length(), wpp);
            var dir = Dx.Vector3.Normalize(db - da);
            var perp = MeasurementService.Perpendicular((dir.X, dir.Y, dir.Z), (look.X, look.Y, look.Z));
            var legacy = new HelixToolkit.SharpDX.Core.LineBuilder();
            foreach (var s in MeasurementService.Strokes((da.X, da.Y, da.Z), (db.X, db.Y, db.Z), perp, tick, overshoot))
                legacy.AddLine(new Dx.Vector3((float)s.A.X, (float)s.A.Y, (float)s.A.Z), new Dx.Vector3((float)s.B.X, (float)s.B.Y, (float)s.B.Z));
            var legacyLabel = dmid + new Dx.Vector3((float)perp.X, (float)perp.Y, (float)perp.Z) * (float)(tick * 0.9);
            var lg = legacy.ToLineGeometry3D();
            var helix = lines.ToLineGeometry();
            if (!SameVector(label.ToDx(), legacyLabel) || lg.Positions.Count != helix.Positions.Count || lg.Indices.Count != helix.Indices.Count ||
                !lg.Positions.Zip(helix.Positions).All(p => SameVector(p.First, p.Second)) || !lg.Indices.SequenceEqual(helix.Indices))
                strokeMismatch++;
        }
        Check(report, ref failures, broken.Count == 0 && pick && strokeMismatch == 0,
            $"рулетка на независимой геометрии: {candidates:N0} привязок (8 углов + 12 середин рёбер габарита IGeometryElement) {geometries.Count} элементов эталона ≡ прежнему " +
            $"пути вьюпорта до бита; выбор — угол {(nearCorner is { } nc ? $"{nc.Distance:0.0} px" : "нет")}, середина ребра {(nearMid is { } nm ? $"{nm.Distance:0.0} px" : "нет")}, " +
            $"вне радиуса — {(none is null ? "нет привязки" : "ошибочно найдена")}; размерные линии BimLineSet ≡ LineBuilder SharpDX на 2 000 размерах (расхождений {strokeMismatch})" +
            (broken.Count > 0 ? ": " + string.Join("; ", broken.Take(5)) : string.Empty));
    }

    // ───────────── 5. Скорость ─────────────

    /// <summary>
    /// Медиана 7 прогонов (после прогрева): построение BimMesh всех элементов 7 сцен эталона; перенос во вьюпорт адаптером против прежнего
    /// прямого создания тех же объектов Helix из списков SharpDX (Vector3Collection(IEnumerable), LineBuilder) — как это делал старый
    /// ElementGeometryBuilder. Норматив: адаптер не медленнее прежнего пути (≤ +20 % + 1 мс на шум таймера).
    /// </summary>
    private static void CheckGeometrySpeed(StringBuilder report, ref int failures, List<BaselineSource> scenes)
    {
        var items = scenes.SelectMany(s => s.Elements.Select(e => (e, s))).ToList();
        var openings = items.Select(p => SceneStateService.OpeningsFor(p.e, p.s.Elements)).ToList();
        var contexts = items.Select(p => p.s.Groups.FirstOrDefault(g => g.Id == p.e.BuildingGroupId) is { } g
            ? new GeometryContext(SceneStateService.AllLevels, g.OriginX, g.OriginZ) : new GeometryContext(SceneStateService.AllLevels, 0, 0)).ToList();
        ElementGeometry[] BuildAll()
        {
            var built = new ElementGeometry[items.Count];
            for (int i = 0; i < items.Count; i++) built[i] = ElementGeometryBuilder.Build(items[i].e, openings[i], contexts[i]);
            return built;
        }
        var geometries = BuildAll();
        var meshes = geometries.SelectMany(g => g.Parts.Select(p => p.Mesh)).ToList();
        var edges = geometries.Where(g => g.Edges is not null).Select(g => g.Edges!).ToList();
        // Прежний путь: списки SharpDX готовы заранее (их строил MeshAccumulator), время — только создание объектов Helix.
        var legacyMeshes = meshes.Select(m => (P: m.Positions.Select(v => v.ToDx()).ToList(), N: m.Normals!.Select(v => v.ToDx()).ToList(), I: m.TriangleIndices.ToList())).ToList();
        var legacyEdges = edges.Select(l => l.Positions.Select(v => v.ToDx()).ToList()).ToList();
        long vertices = meshes.Sum(m => (long)m.VertexCount);

        double Time(Action action)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var sw = Stopwatch.StartNew();
            action();
            return sw.Elapsed.TotalMilliseconds;
        }
        void Adapter()
        {
            foreach (var m in meshes) m.ToMeshGeometry();
            foreach (var l in edges) l.ToLineGeometry();
        }
        void Legacy()
        {
            foreach (var (p, n, i) in legacyMeshes)
                _ = new HelixToolkit.SharpDX.Core.MeshGeometry3D
                {
                    Positions = new HelixToolkit.SharpDX.Core.Vector3Collection(p),
                    Normals = new HelixToolkit.SharpDX.Core.Vector3Collection(n),
                    TriangleIndices = new HelixToolkit.SharpDX.Core.IntCollection(i)
                };
            foreach (var pairs in legacyEdges)
            {
                var lb = new HelixToolkit.SharpDX.Core.LineBuilder();
                for (int i = 0; i + 1 < pairs.Count; i += 2) lb.AddLine(pairs[i], pairs[i + 1]);
                lb.ToLineGeometry3D();
            }
        }
        var build = new List<double>();
        var adapter = new List<double>();
        var legacy = new List<double>();
        for (int run = 0; run < 8; run++)
        {
            double b = Time(() => BuildAll());
            // Порядок чередуется: прогрев кэшей и сборщика мусора не достаётся одному из путей.
            double a, l;
            if (run % 2 == 0) { a = Time(Adapter); l = Time(Legacy); }
            else { l = Time(Legacy); a = Time(Adapter); }
            if (run == 0) continue; // прогрев JIT
            build.Add(b);
            adapter.Add(a);
            legacy.Add(l);
        }
        double Median(List<double> x) => x.OrderBy(t => t).ElementAt(x.Count / 2);
        double mb = Median(build), ma = Median(adapter), ml = Median(legacy);
        Check(report, ref failures, ma <= ml * 1.2 + 1,
            $"скорость: построение BimMesh {items.Count} элементов 7 сцен эталона — {mb:0.0} мс (без объектов Helix); перенос {meshes.Count:N0} сеток " +
            $"({vertices:N0} вершин) и {edges.Count:N0} наборов рёбер во вьюпорт — адаптер {ma:0.0} мс против прежнего прямого создания {ml:0.0} мс " +
            $"(норматив ≤ +20 %; время почти целиком — сеттеры MeshGeometry3D, копирование — один блок памяти на массив); медиана 7 прогонов");
    }}
#endif
