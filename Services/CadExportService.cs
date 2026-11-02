using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AIArchitectStudio.Services;

public enum CadExportFormat
{
    Ifc,
    Dxf,
    Obj
}

/// <summary>Итог экспорта: записанные файлы и статистика для отчёта в чате.</summary>
public sealed record CadExportResult(
    CadExportFormat Format,
    string Path,
    IReadOnlyList<string> Files,
    int ElementCount,
    int EntityCount,
    long Bytes,
    double ElapsedMilliseconds,
    IReadOnlyList<string> Details);

/// <summary>
/// Профессиональный экспорт BIM-сцены:
/// <list type="bullet">
/// <item><b>IFC2X3</b> (ISO-10303-21) для Renga / Revit / ArchiCAD: IfcProject → IfcSite → IfcBuilding (по BuildingGroupId)
/// → IfcBuildingStorey (по LevelIndex) → IfcWallStandardCase / IfcSlab / IfcColumn / IfcWindow / IfcDoor и др. с твердотельной
/// геометрией IfcExtrudedAreaSolid; проёмы — IfcOpeningElement + IfcRelVoidsElement / IfcRelFillsElement.</item>
/// <item><b>DXF R12</b> (AutoCAD / nanoCAD): слои WALLS, COLUMNS, SLABS, WINDOWS, DOORS, GRID_AXES и др.; 3DFACE — объёмы,
/// LINE — плановые контуры и координационные оси.</item>
/// <item><b>OBJ + MTL</b> (Blender / 3ds Max): вершины, нормали и грани в мировых координатах с материалами.</item>
/// </list>
/// Сцена — Y-Up; IFC и DXF — Z-Up: (x, y, z) → (X = x, Y = −z, Z = y).
/// </summary>
public sealed class CadExportService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>IFC: набор свойств пользователя (общих параметров инспектора) — отдельно от служебного AIA_Parameters.</summary>
    public const string UserPsetName = "AIA_UserParameters";

    /// <summary>DXF: зарегистрированное приложение (таблица APPID) расширенных данных XData со свойствами пользователя.</summary>
    public const string UserXDataApp = "AIA_USERPARAMS";

    /// <summary>Определение общего параметра для экспорта; значение без известного определения пишется под запасным именем.</summary>
    private static ParameterDefinition UserDefinition(Guid guid, ParameterValue value) =>
        ParameterRegistry.Shared.Find(guid) ?? ParameterDefinition.Fallback(guid, value);

    public static string Extension(CadExportFormat format) => format switch
    {
        CadExportFormat.Ifc => ".ifc",
        CadExportFormat.Dxf => ".dxf",
        _ => ".obj"
    };

    public static string Title(CadExportFormat format) => format switch
    {
        CadExportFormat.Ifc => "IFC2X3 (Renga / Revit / ArchiCAD)",
        CadExportFormat.Dxf => "AutoCAD DXF",
        _ => "Wavefront OBJ + MTL"
    };

    public static bool TryParse(string? tag, out CadExportFormat format)
    {
        switch (tag?.ToLowerInvariant())
        {
            case "ifc": format = CadExportFormat.Ifc; return true;
            case "dxf": format = CadExportFormat.Dxf; return true;
            case "obj": format = CadExportFormat.Obj; return true;
            default: format = CadExportFormat.Obj; return false;
        }
    }

    /// <summary>FileSavePicker, привязанный к окну через WindowNative.GetWindowHandle.</summary>
    public async Task<string?> PickTargetAsync(Window owner, CadExportFormat format, string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = ProjectFileService.SafeFileName(suggestedName)
        };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        picker.FileTypeChoices.Add(Title(format), [Extension(format)]);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <summary>Экспорт снимка сцены в фоне. Передавайте клоны элементов: UI может менять сцену во время записи.</summary>
    public Task<CadExportResult> ExportAsync(CadExportFormat format, string path,
        IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, string projectName,
        IReadOnlyList<StructuralGridAxis>? axes = null, IReadOnlyList<PinnedDimension>? dimensions = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var scene = new ExportScene(elements, groups, string.IsNullOrWhiteSpace(projectName) ? "Architect Studio Pro" : projectName, axes ?? [])
            {
                Dimensions = dimensions ?? []
            };
            var (files, entities, details) = format switch
            {
                CadExportFormat.Ifc => new IfcWriter(scene, cancellationToken).Write(path),
                CadExportFormat.Dxf => new DxfWriter(scene, cancellationToken).Write(path),
                _ => new ObjWriter(scene, cancellationToken).Write(path)
            };
            sw.Stop();
            long bytes = files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            return new CadExportResult(format, path, files, elements.Count, entities, bytes, sw.Elapsed.TotalMilliseconds, details);
        }, cancellationToken);

    // ═════════════════════════════ Общая модель экспорта ═════════════════════════════

    /// <summary>Часть геометрии элемента в мировых координатах сцены (Y-Up).</summary>
    private sealed record WorldMesh(MaterialKind Material, Vector3[] Positions, Vector3[] Normals, int[] Indices);

    private sealed class ExportScene
    {
        public IReadOnlyList<ArchitecturalElement> Elements { get; }
        public IReadOnlyList<BuildingGroup> Groups { get; }
        public IReadOnlyList<StructuralGridAxis> Axes { get; }
        public string ProjectName { get; }

        /// <summary>Пользовательские размеры рулетки (DXF — слой DIMENSIONS).</summary>
        public IReadOnlyList<PinnedDimension> Dimensions { get; init; } = [];
        private readonly Dictionary<string, BuildingGroup> _groups;
        private readonly Dictionary<string, List<ArchitecturalElement>> _openings = [];

        public ExportScene(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, string projectName, IReadOnlyList<StructuralGridAxis> axes)
        {
            Elements = elements;
            ProjectName = projectName;
            Axes = axes;
            var groupList = groups.ToList();
            foreach (var gid in elements.Select(e => e.BuildingGroupId).Distinct())
                if (groupList.All(g => g.Id != gid)) groupList.Add(new BuildingGroup(gid));
            Groups = groupList;
            _groups = groupList.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
        }

        /// <summary>Проёмы, вырезаемые в элементе: окна/двери/wall_opening стены или slab_opening плиты.</summary>
        public IReadOnlyList<ArchitecturalElement> HostedOf(ArchitecturalElement e)
        {
            if (!_openings.TryGetValue(e.Id, out var list))
            {
                list = SceneStateService.OpeningsFor(e, Elements);
                _openings[e.Id] = list;
            }
            return list;
        }

        public BuildingGroup? Group(string id) => _groups.GetValueOrDefault(id);

        /// <summary>
        /// Тот же процедурный генератор, что и во вьюпорте (все этажи, без фильтра): булевы вырезы проёмов в стенах,
        /// поэтажная закрутка и сужение параметрических элементов. Вершины переводятся в мировые координаты.
        /// </summary>
        public IEnumerable<WorldMesh> WorldMeshes(ArchitecturalElement e)
        {
            var group = Group(e.BuildingGroupId);
            var geometry = ElementGeometryBuilder.Build(e, HostedOf(e), new GeometryContext(SceneStateService.AllLevels, group?.OriginX ?? 0, group?.OriginZ ?? 0));
            var m = geometry.Transform;
            foreach (var part in geometry.Parts)
            {
                var mesh = part.Mesh;
                if (mesh.TriangleIndices.Count < 3) continue;
                var positions = ToWorld(mesh.PositionSpan, m);
                var normals = mesh.Normals is { } n && n.Count == positions.Length
                    ? NormalsToWorld(mesh.NormalSpan, m)
                    : Enumerable.Repeat(Vector3.UnitY, positions.Length).ToArray();
                yield return new WorldMesh(part.Material, positions, normals, mesh.IndexSpan.ToArray());
            }
        }
    }

    private static Vector3[] ToWorld(ReadOnlySpan<Vector3> local, in Matrix4x4 m)
    {
        var world = new Vector3[local.Length];
        for (int i = 0; i < local.Length; i++) world[i] = BimMath.TransformCoordinate(local[i], m);
        return world;
    }

    private static Vector3[] NormalsToWorld(ReadOnlySpan<Vector3> local, in Matrix4x4 m)
    {
        var world = new Vector3[local.Length];
        for (int i = 0; i < local.Length; i++) world[i] = SafeNormalize(BimMath.TransformNormal(local[i], m));
        return world;
    }

    private static Vector3 SafeNormalize(Vector3 v) => BimMath.LengthSquared(v) > 1e-12f ? BimMath.Normalize(v) : Vector3.UnitY;

    /// <summary>Грани сетки: пары треугольников AddQuad (a,b,c)+(a,c,d) объединяются обратно в четырёхугольники.</summary>
    private static IEnumerable<int[]> Faces(int[] idx)
    {
        int k = 0;
        while (k + 2 < idx.Length)
        {
            if (k + 5 < idx.Length && idx[k + 3] == idx[k] && idx[k + 4] == idx[k + 2])
            {
                yield return [idx[k], idx[k + 1], idx[k + 2], idx[k + 5]];
                k += 6;
            }
            else
            {
                yield return [idx[k], idx[k + 1], idx[k + 2]];
                k += 3;
            }
        }
    }

    /// <summary>
    /// Разворачивает параметрический элемент (RepeatLevels) в поэтажные экземпляры с закруткой и сужением —
    /// так же, как это делает геометрия вьюпорта. Диагрид-оболочка остаётся единым элементом.
    /// </summary>
    private static IEnumerable<(ArchitecturalElement Instance, int Floor)> ExpandRepeats(ArchitecturalElement e)
    {
        int repeat = Math.Max(1, e.RepeatLevels);
        if (repeat == 1 || e.Type == ElementTypes.Diagrid)
        {
            yield return (e, 0);
            yield break;
        }

        double taper = e.TaperScalePerFloor <= 0 ? 1 : e.TaperScalePerFloor, scale = 1;
        for (int i = 0; i < repeat; i++, scale *= taper)
        {
            var c = e.Clone();
            c.RepeatLevels = 1;
            c.TwistAnglePerFloor = 0;
            c.TaperScalePerFloor = 1;
            c.LevelIndex = e.LevelIndex + i;
            c.SetPosition(e.X, e.Y + e.LevelHeight * i, e.Z);
            c.SetSize(e.Width * scale, e.Height, e.Depth * scale);
            c.SetRotationY(e.RotationY + e.TwistAnglePerFloor * i);
            c.Polygon2D = e.Polygon2D.Select(p => new Point2D(p.X * scale, p.Z * scale)).ToList();
            yield return (c, i);
        }
    }

    /// <summary>
    /// Контур элемента в плане (локальные X, Z): Polygon2D, сечение колонны по её профилю (круг/труба — окружность,
    /// двутавр — I-контур, прямоугольник — строго W × D), эллипс купола или прямоугольник габарита.
    /// </summary>
    private static List<(double X, double Z)> LocalOutline(ArchitecturalElement e)
    {
        if (e.Polygon2D.Count >= 3) return e.Polygon2D.Select(p => (p.X, p.Z)).ToList();

        double w = e.Width, d = e.Depth;
        string profile = e.EffectiveProfile;
        bool round = e.Type == ElementTypes.Shell || (e.Type == ElementTypes.Column && profile is ProfileTypes.Circle or ProfileTypes.Pipe);
        if (round)
        {
            int n = e.Type == ElementTypes.Shell ? 48 : 24;
            return Enumerable.Range(0, n)
                .Select(i => 2 * Math.PI * i / n)
                .Select(a => (Math.Cos(a) * w / 2, Math.Sin(a) * d / 2))
                .ToList();
        }
        if (e.Type == ElementTypes.Column && profile == ProfileTypes.IBeam)
        {
            var (tf, tw) = e.ProfileThicknesses(w, d);
            double x = w / 2, z = d / 2;
            return [(-x, -z), (x, -z), (x, -z + tf), (tw / 2, -z + tf), (tw / 2, z - tf), (x, z - tf),
                    (x, z), (-x, z), (-x, z - tf), (-tw / 2, z - tf), (-tw / 2, -z + tf), (-x, -z + tf)];
        }
        return [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)];
    }

    /// <summary>Сцена Y-Up → CAD Z-Up.</summary>
    private static (double X, double Y, double Z) ToCad(double x, double y, double z) => (x, -z, y);

    private static (double X, double Y, double Z) ToCad(Vector3 p) => (p.X, -p.Z, p.Y);

    private static bool HasTilt(ArchitecturalElement e) =>
        (e.Rotation.Length > 0 && Math.Abs(e.Rotation[0]) > 1e-6) || (e.Rotation.Length > 2 && Math.Abs(e.Rotation[2]) > 1e-6);

    private static string MaterialName(MaterialKind kind) => kind.ToString();

    // ═════════════════════════════ Wavefront OBJ + MTL ═════════════════════════════

    private sealed class ObjWriter(ExportScene scene, CancellationToken ct)
    {
        public (List<string> Files, int Entities, List<string> Details) Write(string path)
        {
            string mtlPath = Path.ChangeExtension(path, ".mtl");
            var used = new HashSet<MaterialKind>();
            int vertexBase = 1, faces = 0, vertices = 0, objects = 0;

            var tmp = path + ".tmp";
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                w.NewLine = "\n";
                w.WriteLine("# Architect Studio Pro — Wavefront OBJ");
                w.WriteLine($"# Project: {scene.ProjectName}");
                w.WriteLine($"# Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine("# Units: meters, up axis: +Y (Blender: Forward -Z, Up Y)");
                w.WriteLine($"mtllib {Path.GetFileName(mtlPath)}");

                foreach (var e in scene.Elements)
                {
                    ct.ThrowIfCancellationRequested();
                    // Проёмы — это вырезы в стенах и плитах, а не тела.
                    if (ElementTypes.IsVoidType(e.Type)) continue;
                    var meshes = scene.WorldMeshes(e).ToList();
                    if (meshes.Count == 0) continue;

                    objects++;
                    w.WriteLine();
                    w.WriteLine($"o {ObjName(e.BuildingGroupId)}/{ObjName(e.DisplayTitle)}_{e.Id}");
                    w.WriteLine($"g {ObjName(e.BuildingGroupId)} {e.Type}");
                    foreach (var mesh in meshes)
                    {
                        used.Add(mesh.Material);
                        w.WriteLine($"usemtl {MaterialName(mesh.Material)}");
                        foreach (var p in mesh.Positions)
                            w.WriteLine($"v {F(p.X)} {F(p.Y)} {F(p.Z)}");
                        foreach (var n in mesh.Normals)
                            w.WriteLine($"vn {F(n.X)} {F(n.Y)} {F(n.Z)}");

                        var sb = new StringBuilder();
                        foreach (var face in Faces(mesh.Indices))
                        {
                            sb.Clear().Append('f');
                            foreach (int i in face)
                            {
                                int vi = vertexBase + i;
                                sb.Append(' ').Append(vi).Append("//").Append(vi);
                            }
                            w.WriteLine(sb.ToString());
                            faces++;
                        }
                        vertexBase += mesh.Positions.Length;
                        vertices += mesh.Positions.Length;
                    }
                }
            }
            File.Move(tmp, path, overwrite: true);
            WriteMtl(mtlPath, used);

            return ([path, mtlPath], faces, [
                $"OBJ: объектов {objects}, вершин {vertices:N0}, граней {faces:N0}",
                $"MTL: материалов {used.Count} ({string.Join(", ", used.Select(MaterialName))})",
                "Система координат: Y-Up, метры, мировые координаты каждого элемента"
            ]);
        }

        private void WriteMtl(string mtlPath, IEnumerable<MaterialKind> kinds)
        {
            var sb = new StringBuilder();
            sb.Append("# Architect Studio Pro — materials\n");
            foreach (var kind in kinds.OrderBy(k => k))
            {
                var c = MaterialCatalog.BaseColor(kind);
                bool glossy = kind is MaterialKind.Glass or MaterialKind.Water or MaterialKind.Steel;
                sb.Append('\n').Append("newmtl ").Append(MaterialName(kind)).Append('\n');
                sb.Append($"Ka {F(c.Red * 0.6f)} {F(c.Green * 0.6f)} {F(c.Blue * 0.6f)}\n");
                sb.Append($"Kd {F(c.Red)} {F(c.Green)} {F(c.Blue)}\n");
                sb.Append(glossy ? "Ks 0.55 0.55 0.55\nNs 60\n" : "Ks 0.06 0.06 0.06\nNs 8\n");
                sb.Append($"d {F(c.Alpha)}\nTr {F(1 - c.Alpha)}\n");
                sb.Append(c.Alpha < 1 ? "illum 4\n" : "illum 2\n");
            }
            File.WriteAllText(mtlPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static string ObjName(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsWhiteSpace(c) || c is '/' or '#' ? '_' : c);
            return sb.Length == 0 ? "element" : sb.ToString();
        }

        private static string F(double v) => (Math.Abs(v) < 5e-7 ? 0 : v).ToString("0.######", Inv);
    }

    // ═════════════════════════════ AutoCAD DXF (R12, AC1009) ═════════════════════════════

    private sealed class DxfWriter(ExportScene scene, CancellationToken ct)
    {
        /// <summary>Слои чертежа: имя → цвет ACI и тип линии.</summary>
        private static readonly (string Name, int Color, string LineType)[] Layers =
        [
            ("WALLS", 7, "CONTINUOUS"),
            ("CURTAIN_WALLS", 4, "CONTINUOUS"),
            ("COLUMNS", 1, "CONTINUOUS"),
            ("SLABS", 8, "CONTINUOUS"),
            ("FOUNDATION", 9, "CONTINUOUS"),
            ("WINDOWS", 5, "CONTINUOUS"),
            ("DOORS", 32, "CONTINUOUS"),
            ("FRAME", 6, "CONTINUOUS"),
            ("ROOFS", 30, "CONTINUOUS"),
            ("STAIRS", 3, "CONTINUOUS"),
            ("POOL", 150, "CONTINUOUS"),
            ("TERRACES", 42, "CONTINUOUS"),
            ("ASSETS", 2, "CONTINUOUS"),
            ("OPENINGS", 30, "DASHED"),
            ("GRID_AXES", 1, "DASHDOT"),
            ("DIMENSIONS", 3, "CONTINUOUS")
        ];

        /// <summary>Геометрические блоки размеров (*D1, *D2…): в R12 DIMENSION ссылается на анонимный блок своего изображения.</summary>
        private readonly StringBuilder _blocks = new();

        private readonly StringBuilder _entities = new();
        private double _minX = double.MaxValue, _minY = double.MaxValue, _minZ = double.MaxValue;
        private double _maxX = double.MinValue, _maxY = double.MinValue, _maxZ = double.MinValue;
        private readonly Dictionary<string, int> _counts = [];

        /// <summary>
        /// XData свойств пользователя текущего элемента (null — нет): полная запись — на первом примитиве элемента (первый 3DFACE
        /// объёма или первая LINE контура проёма), ссылка «1000 Id элемента» — на остальных LINE планового контура; грани объёма
        /// после первой — без XData (у двутавра их сотни: полная запись на каждой увеличила бы файл в разы).
        /// </summary>
        private string? _xdata, _xdataRef;
        private bool _xdataFirst;
        private int _xdataElements, _xdataValues;

        public static string LayerFor(string type) => type switch
        {
            ElementTypes.Wall or ElementTypes.Core => "WALLS",
            ElementTypes.CurtainWall => "CURTAIN_WALLS",
            ElementTypes.Column => "COLUMNS",
            ElementTypes.Slab or ElementTypes.CantileverSlab => "SLABS",
            ElementTypes.Foundation => "FOUNDATION",
            ElementTypes.Window => "WINDOWS",
            ElementTypes.Door => "DOORS",
            ElementTypes.Beam or ElementTypes.Truss or ElementTypes.Diagrid => "FRAME",
            ElementTypes.Roof or ElementTypes.Shell => "ROOFS",
            ElementTypes.Stairs => "STAIRS",
            ElementTypes.Pool => "POOL",
            ElementTypes.Terrace => "TERRACES",
            ElementTypes.WallOpening or ElementTypes.SlabOpening => "OPENINGS",
            _ => "ASSETS"
        };

        public (List<string> Files, int Entities, List<string> Details) Write(string path)
        {
            int faces = 0, lines = 0, openings = 0;

            foreach (var e in scene.Elements)
            {
                ct.ThrowIfCancellationRequested();
                string layer = LayerFor(e.Type);
                _xdata = UserXData(e);
                _xdataFirst = _xdata is not null;
                _xdataRef = _xdata is null ? null : $"1001\n{UserXDataApp}\n1000\n{Escape(e.Id)}\n";

                // Проём — только контур выреза (сам вырез уже сделан в геометрии стены/плиты): для стены — прямоугольник
                // в плоскости стены, для плиты — контур в плане на отметке низа плиты.
                if (ElementTypes.IsVoidType(e.Type))
                {
                    var loop = e.Type == ElementTypes.WallOpening
                        ? new[] { (-e.Width / 2, -e.Height / 2), (e.Width / 2, -e.Height / 2), (e.Width / 2, e.Height / 2), (-e.Width / 2, e.Height / 2) }
                            .Select(p =>
                            {
                                var (rx, rz) = ArchitecturalElement.RotateY(p.Item1, 0, e.RotationY);
                                return ToCad(e.X + rx, e.Y + p.Item2, e.Z + rz);
                            }).ToList()
                        : LocalOutline(e).Select(p =>
                            {
                                var (rx, rz) = ArchitecturalElement.RotateY(p.X, p.Z, e.RotationY);
                                return ToCad(e.X + rx, e.BaseY, e.Z + rz);
                            }).ToList();
                    for (int i = 0; i < loop.Count; i++) Line(layer, loop[i], loop[(i + 1) % loop.Count]);
                    Line(layer, loop[0], loop[2]);
                    Line(layer, loop[1], loop[3 % loop.Count]);
                    openings++;
                    continue;
                }

                // 3D-объёмы: 3DFACE (треугольник — четвёртая вершина совпадает с третьей).
                foreach (var mesh in scene.WorldMeshes(e))
                {
                    foreach (var face in Faces(mesh.Indices))
                    {
                        var a = ToCad(mesh.Positions[face[0]]);
                        var b = ToCad(mesh.Positions[face[1]]);
                        var c = ToCad(mesh.Positions[face[2]]);
                        var d = face.Length == 4 ? ToCad(mesh.Positions[face[3]]) : c;
                        Face3D(layer, a, b, c, d);
                        faces++;
                    }
                }

                // 2D-контуры в плане на отметке низа элемента: LINE (для параметрических — по каждому этажу).
                if (e.Type == ElementTypes.Diagrid) continue;
                foreach (var (inst, _) in ExpandRepeats(e))
                {
                    var outline = LocalOutline(inst);
                    var world = outline.Select(p =>
                    {
                        var (rx, rz) = ArchitecturalElement.RotateY(p.X, p.Z, inst.RotationY);
                        return ToCad(inst.X + rx, inst.BaseY, inst.Z + rz);
                    }).ToList();
                    for (int i = 0; i < world.Count; i++)
                    {
                        Line(layer, world[i], world[(i + 1) % world.Count]);
                        lines++;
                    }
                }
            }
            _xdata = _xdataRef = null;
            _xdataFirst = false;

            int axisCount = WriteGridAxes();
            int dimensionCount = WriteDimensions();

            var sb = new StringBuilder(_entities.Length + _blocks.Length + 4096);
            // Комментарий-подпись: импорт узнаёт собственный файл и не центрирует координаты (точный Round-Trip).
            Pair(sb, 999, CadDxfImporterService.ExportSignature);
            WriteHeader(sb);
            WriteTables(sb, _xdataElements > 0);
            Pair(sb, 0, "SECTION"); Pair(sb, 2, "BLOCKS");
            sb.Append(_blocks);
            Pair(sb, 0, "ENDSEC");
            Pair(sb, 0, "SECTION"); Pair(sb, 2, "ENTITIES");
            sb.Append(_entities);
            Pair(sb, 0, "ENDSEC");
            Pair(sb, 0, "EOF");

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.ASCII);
            File.Move(tmp, path, overwrite: true);

            var perLayer = _counts.OrderBy(p => Array.FindIndex(Layers, l => l.Name == p.Key)).Select(p => $"{p.Key}: {p.Value}");
            return ([path], faces + lines + axisCount + dimensionCount, [
                $"DXF R12 (AC1009): 3DFACE {faces:N0}, LINE контуров {lines:N0}, контуров проёмов {openings}, объектов осей {axisCount}" +
                (dimensionCount > 0 ? $", пользовательских размеров {dimensionCount} (DIMENSIONS, ALIGNED)" : string.Empty),
                $"Слои: {string.Join(", ", perLayer)}",
                .. (_xdataElements > 0 ? new[] { $"Свойства пользователя: XData «{UserXDataApp}» у {_xdataElements} элементов (значений {_xdataValues}): запись на первом примитиве, ссылка по Id — на LINE контура" } : []),
                "Система координат: Z-Up, метры (X — восток, Y — север плана)"
            ]);
        }

        /// <summary>
        /// Свойства пользователя элемента — расширенные данные XData приложения AIA_USERPARAMS (DXF R12, AutoCAD сохраняет их в DWG,
        /// читает XDLIST, (entget e '("AIA_USERPARAMS")) в AutoLISP и .NET API; геометрия и слои примитивов не меняются): 1000 — Id элемента, затем на каждое свойство список 1002 { 1000 имя, 1000 Guid,
        /// 1000 смысл (Length, Text…), значение 1040 (вещественное, СИ: м, м², кг…, угол — градусы) / 1070 (целое ±32767, да/нет — 1/0) /
        /// 1000 (текст, до 250 знаков) } 1002. Без значений — null: файл элемента не меняется.
        /// </summary>
        private string? UserXData(ArchitecturalElement e)
        {
            if (e.UserParameters is not { } values) return null;
            var sb = new StringBuilder(256);
            Pair(sb, 1001, UserXDataApp);
            Pair(sb, 1000, Escape(e.Id));
            int count = 0;
            foreach (var (guid, raw) in values.OrderBy(p => UserDefinition(p.Key, p.Value).Name, StringComparer.Ordinal).ThenBy(p => p.Key))
            {
                var definition = UserDefinition(guid, raw);
                var value = raw.ConvertTo(definition.StorageType);
                if (value.IsEmpty || (value.StorageType == ParameterStorageType.Double && !double.IsFinite(value.AsDouble()))) continue;
                Pair(sb, 1002, "{");
                Pair(sb, 1000, Escape(Clip(definition.Name)));
                Pair(sb, 1000, guid.ToString("D"));
                Pair(sb, 1000, definition.SpecType.ToString());
                switch (value.StorageType)
                {
                    case ParameterStorageType.Double:
                        Pair(sb, 1040, value.AsDouble().ToString("R", Inv));
                        break;
                    case ParameterStorageType.Int when value.AsInt() is >= short.MinValue and <= short.MaxValue:
                    case ParameterStorageType.Bool:
                        Pair(sb, 1070, value.AsInt().ToString(Inv));
                        break;
                    case ParameterStorageType.Int:
                        Pair(sb, 1040, value.AsInt().ToString(Inv));
                        break;
                    default:
                        Pair(sb, 1000, Escape(Clip(value.AsString() ?? string.Empty)));
                        break;
                }
                Pair(sb, 1002, "}");
                count++;
            }
            if (count == 0) return null;
            _xdataElements++;
            _xdataValues += count;
            return sb.ToString();
        }

        /// <summary>Строка XData (1000) в AutoCAD — не длиннее 255 знаков.</summary>
        private static string Clip(string s) => s.Length <= 250 ? s : s[..250];

        /// <summary>
        /// Строительные оси 1-2-3 / А-Б-В каждой постройки: штрихпунктирные линии на отметке каждого этажа, марки в кружках
        /// и размеры между осями (мм) — на первом этаже, отметки уровней — текстом у начала сетки.
        /// </summary>
        private int WriteGridAxes()
        {
            int count = 0;
            const double bubble = 0.6;
            foreach (var grid in StructuralAxesBuilder.BuildGrids(scene.Elements, scene.Axes))
            {
                for (int li = 0; li < grid.Levels.Count; li++)
                {
                    float y = (float)grid.Levels[li].Elevation;
                    var axes = grid.AxesAt(y);
                    foreach (var axis in axes)
                    {
                        Line("GRID_AXES", ToCad(axis.Start), ToCad(axis.End));
                        count++;
                        if (li > 0) continue;
                        foreach (var labelPoint in new[] { axis.LabelStart, axis.LabelEnd })
                        {
                            var p = ToCad(labelPoint);
                            Circle("GRID_AXES", p, bubble);
                            Text("GRID_AXES", p, 0.6, axis.Label);
                            count += 2;
                        }
                    }
                    if (li == 0)
                        foreach (var dim in grid.DimensionsAt(y))
                        {
                            Text("GRID_AXES", ToCad(dim.Position), 0.35, dim.Text);
                            count++;
                        }
                    var anchor = axes[0].LabelStart + new Vector3(-2.2f, 0, 0.9f);
                    Text("GRID_AXES", ToCad(anchor), 0.35, StructuralAxesBuilder.ElevationText(grid.Levels[li].Elevation));
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Пользовательские размеры рулетки — ALIGNED DIMENSION (70 = 1) на слое DIMENSIONS: определяющие точки 13/14 — концы
        /// размера (WCS, Z-Up), текст — значение в мм; изображение (размерная линия с выступом, засечки 45°, текст) — в
        /// анонимном блоке *Dn, как того требует DXF R12.
        /// </summary>
        private int WriteDimensions()
        {
            int n = 0;
            foreach (var d in scene.Dimensions.Where(d => d.IsValid))
            {
                ct.ThrowIfCancellationRequested();
                var a = ToCad(d.Start[0], d.Start[1], d.Start[2]);
                var b = ToCad(d.End[0], d.End[1], d.End[2]);
                var dir = MeasurementService.Normalize(MeasurementService.Sub(b, a));
                // Засечки и текст — в плане (вид сверху −Z), у вертикального размера — в плоскости XZ.
                var perp = MeasurementService.Perpendicular(dir, (0, 0, -1));
                double length = d.Length;
                var (tick, overshoot) = MeasurementService.StrokeSizes(length);
                string text = MeasurementService.LabelOf(d);
                double height = Math.Clamp(length * 0.04, 0.12, 0.5);
                var mid = MeasurementService.Scale(MeasurementService.Add(a, b), 0.5);
                var textAt = MeasurementService.Add(mid, MeasurementService.Scale(perp, height * 0.9));
                double angle = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
                if (Math.Abs(dir.Z) > 0.999) angle = 0;
                if (angle > 90.0001 || angle < -89.9999) angle += angle > 0 ? -180 : 180;
                string block = $"*D{++n}";

                Pair(_blocks, 0, "BLOCK"); Pair(_blocks, 8, "DIMENSIONS"); Pair(_blocks, 2, block); Pair(_blocks, 70, "1");
                Point(_blocks, 10, (0, 0, 0)); Pair(_blocks, 3, block);
                foreach (var s in MeasurementService.Strokes(a, b, perp, tick, overshoot))
                {
                    Pair(_blocks, 0, "LINE"); Pair(_blocks, 8, "DIMENSIONS");
                    Point(_blocks, 10, s.A); Point(_blocks, 11, s.B);
                }
                Pair(_blocks, 0, "TEXT"); Pair(_blocks, 8, "DIMENSIONS");
                Point(_blocks, 10, textAt); Pair(_blocks, 40, F(height)); Pair(_blocks, 1, Escape(text));
                Pair(_blocks, 50, F(angle)); Pair(_blocks, 72, "1"); Pair(_blocks, 73, "1"); Point(_blocks, 11, textAt);
                Pair(_blocks, 0, "ENDBLK"); Pair(_blocks, 8, "DIMENSIONS");

                Pair(_entities, 0, "DIMENSION"); Pair(_entities, 8, "DIMENSIONS"); Pair(_entities, 2, block);
                Point(_entities, 10, b); Point(_entities, 11, textAt);
                Pair(_entities, 70, "1"); Pair(_entities, 1, Escape(text));
                Point(_entities, 13, a); Point(_entities, 14, b);
                Track(a); Track(b);
                Count("DIMENSIONS");
            }
            return n;
        }

        // ───────────── Секции ─────────────

        private void WriteHeader(StringBuilder sb)
        {
            if (_minX == double.MaxValue) { _minX = _minY = _minZ = 0; _maxX = _maxY = _maxZ = 0; }
            Pair(sb, 0, "SECTION"); Pair(sb, 2, "HEADER");
            Pair(sb, 9, "$ACADVER"); Pair(sb, 1, "AC1009");
            Pair(sb, 9, "$DWGCODEPAGE"); Pair(sb, 3, "ANSI_1251");
            Pair(sb, 9, "$INSBASE"); Point(sb, 10, (0, 0, 0));
            Pair(sb, 9, "$EXTMIN"); Point(sb, 10, (_minX, _minY, _minZ));
            Pair(sb, 9, "$EXTMAX"); Point(sb, 10, (_maxX, _maxY, _maxZ));
            Pair(sb, 9, "$LUNITS"); Pair(sb, 70, "2");
            Pair(sb, 9, "$LUPREC"); Pair(sb, 70, "3");
            Pair(sb, 9, "$LTSCALE"); Pair(sb, 40, "1.0");
            Pair(sb, 0, "ENDSEC");
        }

        private static void WriteTables(StringBuilder sb, bool userXData)
        {
            Pair(sb, 0, "SECTION"); Pair(sb, 2, "TABLES");

            Pair(sb, 0, "TABLE"); Pair(sb, 2, "LTYPE"); Pair(sb, 70, "3");
            Pair(sb, 0, "LTYPE"); Pair(sb, 2, "CONTINUOUS"); Pair(sb, 70, "0");
            Pair(sb, 3, "Solid line"); Pair(sb, 72, "65"); Pair(sb, 73, "0"); Pair(sb, 40, "0.0");
            // Штриховая линия контуров проёмов: штрих 0.4, пробел 0.2.
            Pair(sb, 0, "LTYPE"); Pair(sb, 2, "DASHED"); Pair(sb, 70, "0");
            Pair(sb, 3, "Dashed __ __ __"); Pair(sb, 72, "65"); Pair(sb, 73, "2"); Pair(sb, 40, "0.6");
            Pair(sb, 49, "0.4"); Pair(sb, 49, "-0.2");
            // Штрихпунктир осей (в метрах): штрих 1.2, пробел 0.3, точка, пробел 0.3.
            Pair(sb, 0, "LTYPE"); Pair(sb, 2, "DASHDOT"); Pair(sb, 70, "0");
            Pair(sb, 3, "Dash dot __ . __ . __"); Pair(sb, 72, "65"); Pair(sb, 73, "4"); Pair(sb, 40, "1.8");
            Pair(sb, 49, "1.2"); Pair(sb, 49, "-0.3"); Pair(sb, 49, "0.0"); Pair(sb, 49, "-0.3");
            Pair(sb, 0, "ENDTAB");

            Pair(sb, 0, "TABLE"); Pair(sb, 2, "LAYER"); Pair(sb, 70, (Layers.Length + 1).ToString(Inv));
            Pair(sb, 0, "LAYER"); Pair(sb, 2, "0"); Pair(sb, 70, "0"); Pair(sb, 62, "7"); Pair(sb, 6, "CONTINUOUS");
            foreach (var (name, color, ltype) in Layers)
            {
                Pair(sb, 0, "LAYER"); Pair(sb, 2, name); Pair(sb, 70, "0");
                Pair(sb, 62, color.ToString(Inv)); Pair(sb, 6, ltype);
            }
            Pair(sb, 0, "ENDTAB");

            Pair(sb, 0, "TABLE"); Pair(sb, 2, "STYLE"); Pair(sb, 70, "1");
            Pair(sb, 0, "STYLE"); Pair(sb, 2, "STANDARD"); Pair(sb, 70, "0");
            Pair(sb, 40, "0.0"); Pair(sb, 41, "1.0"); Pair(sb, 50, "0.0"); Pair(sb, 71, "0");
            Pair(sb, 42, "0.5"); Pair(sb, 3, "txt"); Pair(sb, 4, "");
            Pair(sb, 0, "ENDTAB");

            // Приложения расширенных данных: AutoCAD отклоняет XData незарегистрированного приложения (только при свойствах пользователя).
            if (userXData)
            {
                Pair(sb, 0, "TABLE"); Pair(sb, 2, "APPID"); Pair(sb, 70, "2");
                Pair(sb, 0, "APPID"); Pair(sb, 2, "ACAD"); Pair(sb, 70, "0");
                Pair(sb, 0, "APPID"); Pair(sb, 2, UserXDataApp); Pair(sb, 70, "0");
                Pair(sb, 0, "ENDTAB");
            }

            Pair(sb, 0, "ENDSEC");
        }

        // ───────────── Примитивы ─────────────

        private void Face3D(string layer, (double, double, double) a, (double, double, double) b, (double, double, double) c, (double, double, double) d)
        {
            Pair(_entities, 0, "3DFACE"); Pair(_entities, 8, layer);
            Point(_entities, 10, a); Point(_entities, 11, b); Point(_entities, 12, c); Point(_entities, 13, d);
            if (_xdataFirst)
            {
                _entities.Append(_xdata);
                _xdataFirst = false;
            }
            Track(a); Track(b); Track(c); Track(d);
            Count(layer);
        }

        private void Line(string layer, (double, double, double) a, (double, double, double) b)
        {
            Pair(_entities, 0, "LINE"); Pair(_entities, 8, layer);
            Point(_entities, 10, a); Point(_entities, 11, b);
            if (_xdataFirst)
            {
                _entities.Append(_xdata);
                _xdataFirst = false;
            }
            else if (_xdataRef is not null) _entities.Append(_xdataRef);
            Track(a); Track(b);
            Count(layer);
        }

        private void Circle(string layer, (double, double, double) center, double radius)
        {
            Pair(_entities, 0, "CIRCLE"); Pair(_entities, 8, layer);
            Point(_entities, 10, center); Pair(_entities, 40, F(radius));
            Count(layer);
        }

        /// <summary>Текст с выравниванием по центру (72 = 1, 73 = 2); кириллица — escape-последовательностями \U+XXXX.</summary>
        private void Text(string layer, (double, double, double) at, double height, string text)
        {
            Pair(_entities, 0, "TEXT"); Pair(_entities, 8, layer);
            Point(_entities, 10, at); Pair(_entities, 40, F(height)); Pair(_entities, 1, Escape(text));
            Pair(_entities, 72, "1"); Pair(_entities, 73, "2"); Point(_entities, 11, at);
            Count(layer);
        }

        private void Count(string layer) => _counts[layer] = _counts.GetValueOrDefault(layer) + 1;

        private void Track((double X, double Y, double Z) p)
        {
            _minX = Math.Min(_minX, p.X); _minY = Math.Min(_minY, p.Y); _minZ = Math.Min(_minZ, p.Z);
            _maxX = Math.Max(_maxX, p.X); _maxY = Math.Max(_maxY, p.Y); _maxZ = Math.Max(_maxZ, p.Z);
        }

        private static void Pair(StringBuilder sb, int code, string value) =>
            sb.Append(code.ToString(Inv).PadLeft(3)).Append('\n').Append(value).Append('\n');

        private static void Point(StringBuilder sb, int code, (double X, double Y, double Z) p)
        {
            Pair(sb, code, F(p.X));
            Pair(sb, code + 10, F(p.Y));
            Pair(sb, code + 20, F(p.Z));
        }

        private static string F(double v) => (Math.Abs(v) < 5e-7 ? 0 : v).ToString("0.0#####", Inv);

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c is >= ' ' and <= '~') sb.Append(c);
                else if (c > '~') sb.Append("\\U+").Append(((int)c).ToString("X4", Inv));
            }
            return sb.ToString();
        }
    }

    // ═════════════════════════════ IFC2X3 (ISO-10303-21) ═════════════════════════════

    private sealed class IfcWriter(ExportScene scene, CancellationToken ct)
    {
        private const string Base64Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

        private readonly StringBuilder _data = new();
        private int _next = 1;

        private int _ownerHistory, _bodyContext, _axisContext, _footprintContext, _origin, _dirZ, _dirX, _origin2D, _placement2D;
        private readonly Dictionary<MaterialKind, int> _styles = [];
        private readonly Dictionary<string, List<int>> _byMaterial = [];
        private readonly Dictionary<(string, double), int> _layerSetUsages = [];
        private readonly Dictionary<string, int> _materials = [];
        private int _openings, _wallOpenings, _slabOpenings, _walls, _slabs, _columns, _beams, _windows, _doors, _other, _grids, _gridAxes, _layerSets, _layers;
        private readonly Dictionary<string, int> _profiles = [];

        /// <summary>Смыслы пользовательских параметров сцены: единицы массы, силы и плотности объявляются, только если они нужны.</summary>
        private readonly HashSet<ParameterSpecType> _userSpecs = [];
        private int _userPsets, _userProps;

        public (List<string> Files, int Entities, List<string> Details) Write(string path)
        {
            string name = scene.ProjectName;
            foreach (var e in scene.Elements)
                foreach (var (guid, value) in e.UserParameters ?? Enumerable.Empty<KeyValuePair<Guid, ParameterValue>>())
                    _userSpecs.Add(UserDefinition(guid, value).SpecType);
            WriteContext(name, out int project, out int units);

            // Участок
            int sitePlacement = Add($"IFCLOCALPLACEMENT($,{Placement3D(0, 0, 0)})");
            int site = Add($"IFCSITE('{Guid("site")}',#{_ownerHistory},{S("Участок")},$,$,#{sitePlacement},$,$,.ELEMENT.,$,$,0.,$,$)");
            Rel("IFCRELAGGREGATES", "project-site", $"#{project},(#{site})");

            var buildings = new List<int>();
            int storeyCount = 0;
            foreach (var group in scene.Groups)
            {
                var items = scene.Elements.Where(e => e.BuildingGroupId == group.Id).ToList();
                if (items.Count == 0) continue;
                ct.ThrowIfCancellationRequested();

                int bPlacement = Add($"IFCLOCALPLACEMENT(#{sitePlacement},{Placement3D(0, 0, 0)})");
                int building = Add($"IFCBUILDING('{Guid("bld:" + group.Id)}',#{_ownerHistory},{S(group.Name)},{S(group.Purpose)},$,#{bPlacement},$,$,.ELEMENT.,$,$,$)");
                buildings.Add(building);

                // Экземпляры по этажам (параметрические элементы разворачиваются).
                var instances = items.SelectMany(e => ExpandRepeats(e).Select(x => (Source: e, x.Instance, x.Floor))).ToList();
                var levels = instances.Select(i => i.Instance.LevelIndex).Distinct().Order().ToList();
                var storeys = new List<int>();
                var grid = StructuralAxesBuilder.BuildGrids(items, scene.Axes.Where(a => a.BuildingGroupId == group.Id).ToList()).FirstOrDefault();
                foreach (int level in levels)
                {
                    var onLevel = instances.Where(i => i.Instance.LevelIndex == level).ToList();
                    double elevation = StoreyElevation(onLevel.Select(i => i.Instance), level);
                    int sPlacement = Add($"IFCLOCALPLACEMENT(#{bPlacement},{Placement3D(0, 0, 0)})");
                    int storey = Add($"IFCBUILDINGSTOREY('{Guid($"st:{group.Id}:{level}")}',#{_ownerHistory},{S($"Этаж {level + 1}")},$,$,#{sPlacement},$,$,.ELEMENT.,{R(elevation)})");
                    storeys.Add(storey);
                    storeyCount++;

                    var contained = new List<int>();
                    foreach (var (source, inst, floor) in onLevel)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Пустоты (wall_opening / slab_opening) — только IfcOpeningElement у своего хоста, не самостоятельные изделия.
                        if (ElementTypes.IsVoidType(inst.Type)) continue;
                        // Окна и двери обрабатываются вместе со стеной-хостом.
                        if (ElementTypes.IsHostedType(inst.Type) && inst.HostElementId is not null &&
                            scene.Elements.Any(h => h.Id == inst.HostElementId && ElementTypes.IsHostType(h.Type) && Math.Max(1, h.RepeatLevels) == 1 && !HasTilt(h)))
                            continue;
                        WriteElement(source, inst, floor, sPlacement, contained);
                    }
                    // Координационные оси этажа: IfcGrid (UAxes — цифровые, VAxes — буквенные) на отметке этажа.
                    if (grid is not null) WriteGrid(grid, group.Id, level, elevation, sPlacement, contained);
                    if (contained.Count > 0)
                        Rel("IFCRELCONTAINEDINSPATIALSTRUCTURE", $"cont:{group.Id}:{level}", $"{List(contained)},#{storey}");
                }
                if (storeys.Count > 0) Rel("IFCRELAGGREGATES", "bld-st:" + group.Id, $"#{building},{List(storeys)}");
            }
            if (buildings.Count > 0) Rel("IFCRELAGGREGATES", "site-bld", $"#{site},{List(buildings)}");

            foreach (var (material, elements) in _byMaterial)
            {
                if (elements.Count == 0) continue;
                Rel("IFCRELASSOCIATESMATERIAL", "mat:" + material, $"{List(elements)},#{Material(material)}");
            }

            var file = new StringBuilder(_data.Length + 1024);
            file.Append("ISO-10303-21;\n");
            file.Append("HEADER;\n");
            file.Append("FILE_DESCRIPTION(('ViewDefinition [CoordinationView_V2.0]'),'2;1');\n");
            file.Append($"FILE_NAME({S(Path.GetFileName(path))},'{DateTime.Now:yyyy-MM-ddTHH:mm:ss}',('Architect Studio Pro'),('Architect Studio Pro'),'Architect Studio Pro CadExportService','Architect Studio Pro 1.0','');\n");
            file.Append("FILE_SCHEMA(('IFC2X3'));\n");
            file.Append("ENDSEC;\n");
            file.Append("DATA;\n");
            file.Append(_data);
            file.Append("ENDSEC;\n");
            file.Append("END-ISO-10303-21;\n");

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, file.ToString(), Encoding.ASCII);
            File.Move(tmp, path, overwrite: true);

            return ([path], _next - 1, [
                $"IFC2X3: IfcProject → IfcSite → IfcBuilding × {buildings.Count} → IfcBuildingStorey × {storeyCount}",
                $"Стены {_walls}, перекрытия {_slabs}, колонны {_columns}, балки IfcBeam {_beams}, окна {_windows}, двери {_doors}, прочие {_other}",
                $"Проёмы IfcOpeningElement + IfcRelVoidsElement: всего {_openings} (окна/двери {_openings - _wallOpenings - _slabOpenings}, " +
                $"сквозные проёмы стен {_wallOpenings}, отверстия плит {_slabOpenings})",
                $"Профили сечений: {(_profiles.Count == 0 ? "—" : string.Join(", ", _profiles.Select(p => $"{p.Key} × {p.Value}")))}",
                $"Координационные оси: IfcGrid × {_grids} (осей IfcGridAxis {_gridAxes})",
                $"Многослойные конструкции: IfcMaterialLayerSet × {_layerSets} (слоёв IfcMaterialLayer {_layers})",
                .. (_userPsets > 0 ? new[] { $"Свойства пользователя: IfcPropertySet «{UserPsetName}» × {_userPsets} (значений {_userProps})" } : []),
                $"STEP-сущностей: {_next - 1:N0}; геометрия IfcExtrudedAreaSolid, сложные формы — IfcShellBasedSurfaceModel"
            ]);
        }

        // ───────────── Контекст, единицы, владелец ─────────────

        private void WriteContext(string name, out int project, out int units)
        {
            int person = Add("IFCPERSON($,$,'Architect',$,$,$,$,$)");
            int org = Add($"IFCORGANIZATION($,{S("Architect Studio Pro")},$,$,$)");
            int personOrg = Add($"IFCPERSONANDORGANIZATION(#{person},#{org},$)");
            int app = Add($"IFCAPPLICATION(#{org},'1.0',{S("Architect Studio Pro")},'AIArchitectStudio')");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _ownerHistory = Add($"IFCOWNERHISTORY(#{personOrg},#{app},$,.ADDED.,$,$,$,{now})");

            int length = Add("IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)");
            int area = Add("IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.)");
            int volume = Add("IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.)");
            int angle = Add("IFCSIUNIT(*,.PLANEANGLEUNIT.,$,.RADIAN.)");
            var assigned = new List<int> { length, area, volume, angle };
            // Единицы свойств пользователя (кг, Н, кг/м³) — только если такие значения есть: файл без них не меняется.
            int mass = 0;
            if (_userSpecs.Contains(ParameterSpecType.Mass) || _userSpecs.Contains(ParameterSpecType.Density))
            {
                mass = Add("IFCSIUNIT(*,.MASSUNIT.,.KILO.,.GRAM.)");
                if (_userSpecs.Contains(ParameterSpecType.Mass)) assigned.Add(mass);
            }
            if (_userSpecs.Contains(ParameterSpecType.Force)) assigned.Add(Add("IFCSIUNIT(*,.FORCEUNIT.,$,.NEWTON.)"));
            if (_userSpecs.Contains(ParameterSpecType.Density))
            {
                int kg = Add($"IFCDERIVEDUNITELEMENT(#{mass},1)");
                int m3 = Add($"IFCDERIVEDUNITELEMENT(#{length},-3)");
                assigned.Add(Add($"IFCDERIVEDUNIT((#{kg},#{m3}),.MASSDENSITYUNIT.,$)"));
            }
            units = Add($"IFCUNITASSIGNMENT({List(assigned)})");

            _origin = Add("IFCCARTESIANPOINT((0.,0.,0.))");
            _dirZ = Add("IFCDIRECTION((0.,0.,1.))");
            _dirX = Add("IFCDIRECTION((1.,0.,0.))");
            _origin2D = Add("IFCCARTESIANPOINT((0.,0.))");
            _placement2D = Add($"IFCAXIS2PLACEMENT2D(#{_origin2D},$)");
            int wcs = Add($"IFCAXIS2PLACEMENT3D(#{_origin},#{_dirZ},#{_dirX})");
            int context = Add($"IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,#{wcs},$)");
            _bodyContext = Add($"IFCGEOMETRICREPRESENTATIONSUBCONTEXT('Body','Model',*,*,*,*,#{context},$,.MODEL_VIEW.,$)");
            _axisContext = Add($"IFCGEOMETRICREPRESENTATIONSUBCONTEXT('Axis','Model',*,*,*,*,#{context},$,.GRAPH_VIEW.,$)");
            _footprintContext = Add($"IFCGEOMETRICREPRESENTATIONSUBCONTEXT('FootPrint','Model',*,*,*,*,#{context},$,.PLAN_VIEW.,$)");

            project = Add($"IFCPROJECT('{Guid("project")}',#{_ownerHistory},{S(name)},{S("Architect Studio Pro BIM")},$,$,$,(#{context}),#{units})");
        }

        // ───────────── Элементы ─────────────

        private void WriteElement(ArchitecturalElement source, ArchitecturalElement e, int floor, int storeyPlacement, List<int> contained)
        {
            string key = floor == 0 ? e.Id : $"{e.Id}:{floor}";
            string title = floor == 0 ? e.DisplayTitle : $"{e.DisplayTitle} · этаж {e.LevelIndex + 1}";
            var kind = MaterialCatalog.ForElement(e);
            // Плоская кровля — экструзия по контуру (с отверстиями), скатная — сетка поверхности.
            bool brep = HasTilt(e) || e.Type is ElementTypes.Truss or ElementTypes.Shell
                or ElementTypes.Stairs or ElementTypes.Diagrid or ElementTypes.Pool or ElementTypes.Asset
                || (e.Type == ElementTypes.Roof && !e.IsFlatRoof);
            // Балка — экструзия сечения вдоль её оси (реальный профиль), а не вертикальная коробка.
            bool axialBeam = !brep && e.Type == ElementTypes.Beam && e.Polygon2D.Count < 3;

            int placement;
            int shape;
            if (brep)
            {
                placement = Add($"IFCLOCALPLACEMENT(#{storeyPlacement},{Placement3D(0, 0, 0)})");
                shape = SurfaceModelShape(source, e, floor);
                if (shape == 0) return;
            }
            else if (axialBeam)
            {
                placement = BeamPlacement(storeyPlacement, e);
                shape = BeamShape(e, kind);
            }
            else
            {
                placement = ElementPlacement(storeyPlacement, e);
                shape = ExtrusionShape(e, kind);
            }

            string guid = Guid("el:" + key);
            string head = $"'{guid}',#{_ownerHistory},{S(title)},{S(e.Type)},{S(e.Material)},#{placement},#{shape},{S(e.Id)}";
            int product;
            switch (e.Type)
            {
                case ElementTypes.Wall when e.Polygon2D.Count < 3 && !brep:
                    product = Add($"IFCWALLSTANDARDCASE({head})");
                    _walls++;
                    AssociateLayerSet(product, e);
                    break;
                case ElementTypes.Wall:
                case ElementTypes.Core:
                    product = Add($"IFCWALL({head})");
                    _walls++;
                    break;
                case ElementTypes.CurtainWall:
                    product = Add($"IFCCURTAINWALL({head})");
                    _walls++;
                    break;
                case ElementTypes.Column:
                    product = Add($"IFCCOLUMN({head})");
                    _columns++;
                    break;
                case ElementTypes.Beam:
                    product = Add($"IFCBEAM({head})");
                    _beams++;
                    break;
                case ElementTypes.Truss:
                case ElementTypes.Diagrid:
                    product = Add($"IFCMEMBER({head})");
                    _other++;
                    break;
                case ElementTypes.Slab:
                case ElementTypes.CantileverSlab:
                case ElementTypes.Terrace:
                    product = Add($"IFCSLAB({head},.FLOOR.)");
                    _slabs++;
                    break;
                case ElementTypes.Foundation:
                    product = Add($"IFCSLAB({head},.BASESLAB.)");
                    _slabs++;
                    break;
                case ElementTypes.Roof:
                case ElementTypes.Shell:
                    product = Add($"IFCSLAB({head},.ROOF.)");
                    _slabs++;
                    break;
                case ElementTypes.Stairs:
                    product = Add($"IFCSTAIR({head},.STRAIGHT_RUN_STAIR.)");
                    _other++;
                    break;
                case ElementTypes.Window:
                    product = Add($"IFCWINDOW({head},{R(e.Height)},{R(e.Width)})");
                    _windows++;
                    break;
                case ElementTypes.Door:
                    product = Add($"IFCDOOR({head},{R(e.Height)},{R(e.Width)})");
                    _doors++;
                    break;
                default:
                    product = Add($"IFCBUILDINGELEMENTPROXY({head},$)");
                    _other++;
                    break;
            }

            contained.Add(product);
            bool standardWall = e.Type == ElementTypes.Wall && e.Polygon2D.Count < 3 && !brep;
            // Многослойный пирог плиты, кровли, фундамента (и стены сложной формы) — IfcMaterialLayerSetUsage по оси AXIS3/AXIS2.
            bool layered = e.Layers.Count > 0 && MaterialLayers.IsLayerHost(e);
            if (layered && !standardWall) AssociateLayerSet(product, e);
            TrackMaterial(e.Material, product, standardWall || layered);
            WriteProperties(product, source, e, floor);

            // Хост-экструзия (не параметрическая и без наклона): вырезы и заполнения её окон и дверей, сквозные проёмы.
            if (ElementTypes.IsHostType(e.Type) && !brep && Math.Max(1, source.RepeatLevels) == 1)
                WriteOpenings(e, product, placement, contained);
            // Плита, плоское покрытие или фундамент: сквозные отверстия slab_opening.
            if (e.CanHostSlabOpenings && !brep && Math.Max(1, source.RepeatLevels) == 1)
                WriteSlabOpenings(e, product, placement);
        }

        /// <summary>
        /// Отверстия плиты: IfcOpeningElement (экструзия контура отверстия на всю толщину плиты с запасом, в системе
        /// размещения плиты) + IfcRelVoidsElement — в Renga и Revit это настоящие сквозные отверстия.
        /// </summary>
        private void WriteSlabOpenings(ArchitecturalElement slab, int slabProduct, int slabPlacement)
        {
            foreach (var o in scene.HostedOf(slab).Where(h => h.Type == ElementTypes.SlabOpening))
            {
                var (lx, lz) = ArchitecturalElement.RotateY(o.X - slab.X, o.Z - slab.Z, -slab.RotationY);
                double a = (o.RotationY - slab.RotationY) * Math.PI / 180;
                const double clearance = 0.05;
                int refDir = Add($"IFCDIRECTION(({R(Math.Cos(a))},{R(Math.Sin(a))},0.))");
                int axis = Add($"IFCAXIS2PLACEMENT3D(#{Point3(lx, -lz, -clearance)},#{_dirZ},#{refDir})");
                int opPlacement = Add($"IFCLOCALPLACEMENT(#{slabPlacement},#{axis})");
                int profile = o.Polygon2D.Count >= 3
                    ? Add($"IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,{S("Отверстие")},#{Polyline(o.Polygon2D.Select(p => (p.X, -p.Z)).ToList())})")
                    : Add($"IFCRECTANGLEPROFILEDEF(.AREA.,{S("Отверстие")},#{_placement2D},{R(Math.Max(0.01, o.Width))},{R(Math.Max(0.01, o.Depth))})");
                int solid = Add($"IFCEXTRUDEDAREASOLID(#{profile},#{Add($"IFCAXIS2PLACEMENT3D(#{_origin},$,$)")},#{_dirZ},{R(slab.Height + 2 * clearance)})");
                int rep = Add($"IFCSHAPEREPRESENTATION(#{_bodyContext},'Body','SweptSolid',(#{solid}))");
                int shape = Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{rep}))");
                int opening = Add($"IFCOPENINGELEMENT('{Guid("op:" + o.Id)}',#{_ownerHistory},{S(o.DisplayTitle)},{S("slab_opening")},'Opening',#{opPlacement},#{shape},{S(o.Id)})");
                Rel("IFCRELVOIDSELEMENT", "void:" + o.Id, $"#{slabProduct},#{opening}");
                WriteProperties(opening, o, o, 0);
                _openings++;
                _slabOpenings++;
            }
        }

        /// <summary>
        /// Координационные оси постройки на этаже: IfcGrid в отметке этажа с UAxes (цифровые оси 1, 2, 3…) и VAxes
        /// (буквенные А, Б, В…), кривые IfcGridAxis — 2D-полилинии; представление FootPrint / GeometricCurveSet.
        /// </summary>
        private void WriteGrid(BuildingGrid grid, string groupId, int level, double elevation, int storeyPlacement, List<int> contained)
        {
            var nums = grid.NumericAxes.ToList();
            var letters = grid.LetterAxes.ToList();
            // IFC2X3: UAxes и VAxes не могут быть пустыми.
            if (nums.Count == 0 || letters.Count == 0) return;

            int Curve(PlanAxis axis)
            {
                var (p0, p1) = axis.IsNumeric
                    ? ((axis.Coordinate, -axis.From), (axis.Coordinate, -axis.To))
                    : ((axis.From, -axis.Coordinate), (axis.To, -axis.Coordinate));
                int a = Add($"IFCCARTESIANPOINT(({R(p0.Item1)},{R(p0.Item2)}))");
                int b = Add($"IFCCARTESIANPOINT(({R(p1.Item1)},{R(p1.Item2)}))");
                return Add($"IFCPOLYLINE((#{a},#{b}))");
            }

            var curves = new List<int>();
            List<int> Axes(List<PlanAxis> list) => list.Select(axis =>
            {
                int curve = Curve(axis);
                curves.Add(curve);
                _gridAxes++;
                return Add($"IFCGRIDAXIS({S(axis.Label)},#{curve},.T.)");
            }).ToList();

            var u = Axes(nums);
            var v = Axes(letters);
            int set = Add($"IFCGEOMETRICCURVESET({List(curves)})");
            int rep = Add($"IFCSHAPEREPRESENTATION(#{_footprintContext},'FootPrint','GeometricCurveSet',(#{set}))");
            int shape = Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{rep}))");
            int placement = Add($"IFCLOCALPLACEMENT(#{storeyPlacement},{Placement3D(0, 0, elevation)})");
            int gridId = Add($"IFCGRID('{Guid($"grid:{groupId}:{level}")}',#{_ownerHistory},{S($"Оси · Этаж {level + 1}")},{S(groupId)},$,#{placement},#{shape},{List(u)},{List(v)},$)");
            contained.Add(gridId);
            _grids++;
        }

        /// <summary>
        /// Окна, двери и сквозные проёмы стены: IfcOpeningElement (вырез, привязанный к размещению стены) + IfcRelVoidsElement;
        /// у окон и дверей — заполнение IfcWindow / IfcDoor + IfcRelFillsElement. Проём сохраняет положение относительно стены.
        /// </summary>
        private void WriteOpenings(ArchitecturalElement wall, int wallProduct, int wallPlacement, List<int> contained)
        {
            foreach (var o in scene.HostedOf(wall).Where(h => ElementTypes.IsHostedType(h.Type)))
            {
                var (lx, lz) = ArchitecturalElement.RotateY(o.X - wall.X, o.Z - wall.Z, -wall.RotationY);
                double z = o.BaseY - wall.BaseY;
                double depth = Math.Max(wall.Depth, 0.1) + 0.2;

                int opPlacement = Add($"IFCLOCALPLACEMENT(#{wallPlacement},{Placement3D(lx, -lz, z)})");
                int opProfile = Add($"IFCRECTANGLEPROFILEDEF(.AREA.,$,#{_placement2D},{R(o.Width)},{R(depth)})");
                int opSolid = Add($"IFCEXTRUDEDAREASOLID(#{opProfile},#{Add($"IFCAXIS2PLACEMENT3D(#{_origin},$,$)")},#{_dirZ},{R(o.Height)})");
                int opRep = Add($"IFCSHAPEREPRESENTATION(#{_bodyContext},'Body','SweptSolid',(#{opSolid}))");
                int opShape = Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{opRep}))");
                bool voidOnly = o.Type == ElementTypes.WallOpening;
                int opening = Add($"IFCOPENINGELEMENT('{Guid("op:" + o.Id)}',#{_ownerHistory},{S(voidOnly ? o.DisplayTitle : "Проём · " + o.DisplayTitle)},{(voidOnly ? S("wall_opening") : "$")},'Opening',#{opPlacement},#{opShape},{(voidOnly ? S(o.Id) : "$")})");
                Rel("IFCRELVOIDSELEMENT", "void:" + o.Id, $"#{wallProduct},#{opening}");
                _openings++;
                if (voidOnly)
                {
                    // Сквозной проём без заполнения: только вырез в стене.
                    WriteProperties(opening, o, o, 0);
                    _wallOpenings++;
                    continue;
                }

                // Заполнение: рама толщиной до 0.12 м по оси стены.
                var kind = MaterialCatalog.ForElement(o);
                double frame = Math.Clamp(wall.Depth * 0.5, 0.06, 0.12);
                int fillPlacement = Add($"IFCLOCALPLACEMENT(#{opPlacement},{Placement3D(0, 0, 0)})");
                int fillProfile = Add($"IFCRECTANGLEPROFILEDEF(.AREA.,$,#{_placement2D},{R(o.Width)},{R(frame)})");
                int fillSolid = Add($"IFCEXTRUDEDAREASOLID(#{fillProfile},#{Add($"IFCAXIS2PLACEMENT3D(#{_origin},$,$)")},#{_dirZ},{R(o.Height)})");
                Style(fillSolid, kind);
                int fillRep = Add($"IFCSHAPEREPRESENTATION(#{_bodyContext},'Body','SweptSolid',(#{fillSolid}))");
                int fillShape = Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{fillRep}))");
                string head = $"'{Guid("el:" + o.Id)}',#{_ownerHistory},{S(o.DisplayTitle)},{S(o.Type)},{S(o.Material)},#{fillPlacement},#{fillShape},{S(o.Id)}";
                int fill;
                if (o.Type == ElementTypes.Door)
                {
                    fill = Add($"IFCDOOR({head},{R(o.Height)},{R(o.Width)})");
                    _doors++;
                }
                else
                {
                    fill = Add($"IFCWINDOW({head},{R(o.Height)},{R(o.Width)})");
                    _windows++;
                }
                Rel("IFCRELFILLSELEMENT", "fill:" + o.Id, $"#{opening},#{fill}");
                contained.Add(fill);
                TrackMaterial(o.Material, fill, false);
                WriteProperties(fill, o, o, 0);
            }
        }

        /// <summary>Размещение экструзии: центр низа элемента, локальная X — вдоль стены (поворот в плане).</summary>
        private int ElementPlacement(int storeyPlacement, ArchitecturalElement e)
        {
            var (x, y, z) = ToCad(e.X, e.BaseY, e.Z);
            double a = e.RotationY * Math.PI / 180;
            int point = Point3(x, y, z);
            int refDir = Add($"IFCDIRECTION(({R(Math.Cos(a))},{R(Math.Sin(a))},0.))");
            int axis = Add($"IFCAXIS2PLACEMENT3D(#{point},#{_dirZ},#{refDir})");
            return Add($"IFCLOCALPLACEMENT(#{storeyPlacement},#{axis})");
        }

        /// <summary>Тело элемента — IfcExtrudedAreaSolid: прямоугольник, круг, произвольный контур или полое кольцо.</summary>
        private int ExtrusionShape(ArchitecturalElement e, MaterialKind kind)
        {
            double w = Math.Max(0.01, e.Width), h = Math.Max(0.01, e.Height), d = Math.Max(0.01, e.Depth);
            int profile;
            bool polygon = e.Polygon2D.Count >= 3;

            if (e.Type == ElementTypes.Core && !polygon)
            {
                double t = Math.Clamp(Math.Min(w, d) * 0.06, 0.25, 0.5);
                profile = Add($"IFCRECTANGLEHOLLOWPROFILEDEF(.AREA.,{S("Ядро жёсткости")},#{_placement2D},{R(w)},{R(d)},{R(t)},$,$)");
            }
            else if (polygon && e.Type is ElementTypes.CurtainWall or ElementTypes.Beam or ElementTypes.Core)
            {
                // Витраж и кольцевая балка по контуру — полое кольцо (контур с внутренним вырезом).
                double band = e.Type == ElementTypes.Beam ? 0.6 : 0.12;
                double outerShift = e.Type == ElementTypes.Beam ? band / 2 : 0;
                profile = Add($"IFCARBITRARYPROFILEDEFWITHVOIDS(.AREA.,$,#{Polyline(ScaledPolygon(e, outerShift))},(#{Polyline(ScaledPolygon(e, outerShift - band))}))");
            }
            else if (polygon)
            {
                profile = Add($"IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,$,#{Polyline(ScaledPolygon(e, 0))})");
            }
            else if (e.Type == ElementTypes.Column)
            {
                // Колонна строго по своему сечению: прямоугольник W × D (квадрат без искажений), круг, труба, двутавр.
                profile = ProfileDef(e, w, d);
            }
            else
            {
                double profileDepth = e.Type is ElementTypes.Window or ElementTypes.Door ? Math.Min(d, 0.12) : d;
                profile = Add($"IFCRECTANGLEPROFILEDEF(.AREA.,$,#{_placement2D},{R(w)},{R(profileDepth)})");
            }

            int position = Add($"IFCAXIS2PLACEMENT3D(#{_origin},$,$)");
            int solid = Add($"IFCEXTRUDEDAREASOLID(#{profile},#{position},#{_dirZ},{R(h)})");
            Style(solid, kind);
            int body = Add($"IFCSHAPEREPRESENTATION(#{_bodyContext},'Body','SweptSolid',(#{solid}))");

            if (e.Type == ElementTypes.Wall && !polygon)
            {
                // Ось стены (Axis / Curve2D) — нужна IfcWallStandardCase для Revit и Renga.
                int p0 = Add($"IFCCARTESIANPOINT(({R(-w / 2)},0.))");
                int p1 = Add($"IFCCARTESIANPOINT(({R(w / 2)},0.))");
                int axisLine = Add($"IFCPOLYLINE((#{p0},#{p1}))");
                int axisRep = Add($"IFCSHAPEREPRESENTATION(#{_axisContext},'Axis','Curve2D',(#{axisLine}))");
                return Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{axisRep},#{body}))");
            }
            return Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{body}))");
        }

        /// <summary>
        /// Профиль сечения стержня в системе размещения (X — xDim, Y — yDim): IfcRectangleProfileDef, IfcCircleProfileDef
        /// (IfcEllipseProfileDef при неравных сторонах), IfcCircleHollowProfileDef, IfcIShapeProfileDef.
        /// </summary>
        private int ProfileDef(ArchitecturalElement e, double xDim, double yDim)
        {
            string profile = e.EffectiveProfile;
            string name = profile switch
            {
                ProfileTypes.Circle => $"Круг {xDim * 1000:0}" + (Math.Abs(xDim - yDim) > 1e-3 ? $"x{yDim * 1000:0}" : string.Empty),
                ProfileTypes.Pipe => $"Труба {xDim * 1000:0}x{e.ProfileThicknesses(xDim, yDim).Wall * 1000:0.#}",
                ProfileTypes.IBeam => $"Двутавр {yDim * 1000:0}x{xDim * 1000:0}",
                _ => $"{xDim * 1000:0}x{yDim * 1000:0}"
            };
            _profiles[profile] = _profiles.GetValueOrDefault(profile) + 1;
            switch (profile)
            {
                case ProfileTypes.Circle when Math.Abs(xDim - yDim) < 1e-3:
                    return Add($"IFCCIRCLEPROFILEDEF(.AREA.,{S(name)},#{_placement2D},{R(xDim / 2)})");
                case ProfileTypes.Circle:
                    return Add($"IFCELLIPSEPROFILEDEF(.AREA.,{S(name)},#{_placement2D},{R(xDim / 2)},{R(yDim / 2)})");
                case ProfileTypes.Pipe:
                {
                    double radius = Math.Min(xDim, yDim) / 2;
                    double t = Math.Min(e.ProfileThicknesses(xDim, yDim).Wall, radius * 0.95);
                    return Add($"IFCCIRCLEHOLLOWPROFILEDEF(.AREA.,{S(name)},#{_placement2D},{R(radius)},{R(t)})");
                }
                case ProfileTypes.IBeam:
                {
                    var (tf, tw) = e.ProfileThicknesses(xDim, yDim);
                    return Add($"IFCISHAPEPROFILEDEF(.AREA.,{S(name)},#{_placement2D},{R(xDim)},{R(yDim)},{R(tw)},{R(tf)},$)");
                }
                default:
                    return Add($"IFCRECTANGLEPROFILEDEF(.AREA.,{S(name)},#{_placement2D},{R(xDim)},{R(yDim)})");
            }
        }

        /// <summary>
        /// Размещение балки: начало — торец балки на оси сечения, ось Z — вдоль балки, X — горизонтально поперёк, Y — вверх.
        /// </summary>
        private int BeamPlacement(int storeyPlacement, ArchitecturalElement e)
        {
            double a = e.RotationY * Math.PI / 180;
            double c = Math.Cos(a), s = Math.Sin(a);
            var (x, y, z) = ToCad(e.X, e.Y, e.Z);
            int point = Point3(x - c * e.Width / 2, y - s * e.Width / 2, z);
            int axis = Add($"IFCDIRECTION(({R(c)},{R(s)},0.))");
            int refDir = Add($"IFCDIRECTION(({R(-s)},{R(c)},0.))");
            int placement = Add($"IFCAXIS2PLACEMENT3D(#{point},#{axis},#{refDir})");
            return Add($"IFCLOCALPLACEMENT(#{storeyPlacement},#{placement})");
        }

        /// <summary>Тело балки: реальное сечение D (ширина) × H (высота), выдавленное на длину W вдоль оси балки; плюс ось 'Axis'.</summary>
        private int BeamShape(ArchitecturalElement e, MaterialKind kind)
        {
            double length = Math.Max(0.01, e.Width), h = Math.Max(0.01, e.Height), d = Math.Max(0.01, e.Depth);
            int profile = ProfileDef(e, d, h);
            int solid = Add($"IFCEXTRUDEDAREASOLID(#{profile},#{Add($"IFCAXIS2PLACEMENT3D(#{_origin},$,$)")},#{_dirZ},{R(length)})");
            Style(solid, kind);
            int body = Add($"IFCSHAPEREPRESENTATION(#{_bodyContext},'Body','SweptSolid',(#{solid}))");
            int p0 = Add("IFCCARTESIANPOINT((0.,0.,0.))");
            int p1 = Add($"IFCCARTESIANPOINT((0.,0.,{R(length)}))");
            int axisLine = Add($"IFCPOLYLINE((#{p0},#{p1}))");
            int axisRep = Add($"IFCSHAPEREPRESENTATION(#{_axisContext},'Axis','Curve3D',(#{axisLine}))");
            return Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{axisRep},#{body}))");
        }

        /// <summary>Сложная форма (купол, ферма, кровля, лестница, чаша бассейна, наклонные стержни) — сетка в мировых координатах.</summary>
        private int SurfaceModelShape(ArchitecturalElement source, ArchitecturalElement e, int floor)
        {
            var items = new List<int>();
            var meshSource = floor == 0 && source.Type == ElementTypes.Diagrid ? source : e;
            foreach (var mesh in scene.WorldMeshes(meshSource))
            {
                var points = new Dictionary<(long, long, long), int>();
                var faces = new List<int>();
                foreach (var face in Faces(mesh.Indices))
                {
                    var ids = new List<int>(face.Length);
                    foreach (int i in face)
                    {
                        var (x, y, z) = ToCad(mesh.Positions[i]);
                        var k = ((long)Math.Round(x * 1e5), (long)Math.Round(y * 1e5), (long)Math.Round(z * 1e5));
                        if (!points.TryGetValue(k, out int pid))
                        {
                            pid = Point3(x, y, z);
                            points[k] = pid;
                        }
                        if (!ids.Contains(pid)) ids.Add(pid);
                    }
                    if (ids.Count < 3) continue;
                    int loop = Add($"IFCPOLYLOOP({List(ids)})");
                    int bound = Add($"IFCFACEOUTERBOUND(#{loop},.T.)");
                    faces.Add(Add($"IFCFACE((#{bound}))"));
                }
                if (faces.Count == 0) continue;
                int shell = Add($"IFCOPENSHELL({List(faces)})");
                int model = Add($"IFCSHELLBASEDSURFACEMODEL((#{shell}))");
                Style(model, mesh.Material);
                items.Add(model);
            }
            if (items.Count == 0) return 0;
            int rep = Add($"IFCSHAPEREPRESENTATION(#{_bodyContext},'Body','SurfaceModel',{List(items)})");
            return Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{rep}))");
        }

        /// <summary>Контур Polygon2D в локальной системе CAD (x, −z), смещённый от центра на offset метров (для колец).</summary>
        private static List<(double X, double Y)> ScaledPolygon(ArchitecturalElement e, double offset)
        {
            double cx = e.Polygon2D.Average(p => p.X), cz = e.Polygon2D.Average(p => p.Z);
            double radius = e.Polygon2D.Max(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Z - cz) * (p.Z - cz)));
            double k = radius > 1e-6 ? Math.Max(0.05, (radius + offset) / radius) : 1;
            return e.Polygon2D.Select(p => (cx + (p.X - cx) * k, -(cz + (p.Z - cz) * k))).ToList();
        }

        private int Polyline(List<(double X, double Y)> points)
        {
            var ids = points.Select(p => Add($"IFCCARTESIANPOINT(({R(p.X)},{R(p.Y)}))")).ToList();
            ids.Add(ids[0]);
            return Add($"IFCPOLYLINE({List(ids)})");
        }

        // ───────────── Материалы, стили, свойства ─────────────

        private void Style(int item, MaterialKind kind)
        {
            if (!_styles.TryGetValue(kind, out int assignment))
            {
                var c = MaterialCatalog.BaseColor(kind);
                int colour = Add($"IFCCOLOURRGB({S(MaterialName(kind))},{R(c.Red)},{R(c.Green)},{R(c.Blue)})");
                int rendering = Add($"IFCSURFACESTYLERENDERING(#{colour},{R(1 - c.Alpha)},$,$,$,$,$,$,.FLAT.)");
                int surface = Add($"IFCSURFACESTYLE({S(MaterialName(kind))},.BOTH.,(#{rendering}))");
                assignment = Add($"IFCPRESENTATIONSTYLEASSIGNMENT((#{surface}))");
                _styles[kind] = assignment;
            }
            Add($"IFCSTYLEDITEM(#{item},(#{assignment}),$)");
        }

        private int Material(string name)
        {
            if (!_materials.TryGetValue(name, out int id))
            {
                id = Add($"IFCMATERIAL({S(name)})");
                _materials[name] = id;
            }
            return id;
        }

        private void TrackMaterial(string? material, int product, bool hasLayerSet)
        {
            if (hasLayerSet) return;
            string name = string.IsNullOrWhiteSpace(material) ? "concrete" : material;
            if (!_byMaterial.TryGetValue(name, out var list)) _byMaterial[name] = list = [];
            list.Add(product);
        }

        /// <summary>
        /// Материалы конструкции для Renga и Revit. Пирог (Layers): IfcMaterial → IfcMaterialLayer (толщина, м) → IfcMaterialLayerSet →
        /// IfcMaterialLayerSetUsage → IfcRelAssociatesMaterial; у стены — по оси AXIS2 от грани +Z (смещение −t/2), у плит, кровли и
        /// фундамента — по AXIS3 снизу вверх. Без пирога стена получает однослойную конструкцию, симметричную оси.
        /// </summary>
        private void AssociateLayerSet(int product, ArchitecturalElement e)
        {
            bool wall = MaterialLayers.IsVerticalStack(e) || e.Type is ElementTypes.Core or ElementTypes.CurtainWall;
            if (e.Layers.Count > 0)
            {
                var layerIds = e.Layers.Select(l => Add($"IFCMATERIALLAYER(#{Material(string.IsNullOrWhiteSpace(l.Name) ? "Слой" : l.Name)},{R(Math.Max(0.0001, l.ThicknessM))},$)")).ToList();
                double total = e.Layers.Sum(l => Math.Max(0.0001, l.ThicknessM));
                string setName = $"{e.DisplayTitle}: " + string.Join(" + ", e.Layers.Select(l => $"{l.Name} {l.ThicknessMm.ToString("0.##", Inv)}"));
                int set = Add($"IFCMATERIALLAYERSET({List(layerIds)},{S(setName)})");
                int layered = wall
                    ? Add($"IFCMATERIALLAYERSETUSAGE(#{set},.AXIS2.,.POSITIVE.,{R(-total / 2)})")
                    : Add($"IFCMATERIALLAYERSETUSAGE(#{set},.AXIS3.,.POSITIVE.,0.)");
                Rel("IFCRELASSOCIATESMATERIAL", $"ls:{e.Id}:{product}", $"(#{product}),#{layered}");
                _layerSets++;
                _layers += e.Layers.Count;
                return;
            }

            string material = string.IsNullOrWhiteSpace(e.Material) ? "brick" : e.Material;
            double t = Math.Round(Math.Max(0.01, e.Depth), 4);
            if (!_layerSetUsages.TryGetValue((material, t), out int usage))
            {
                int layer = Add($"IFCMATERIALLAYER(#{Material(material)},{R(t)},$)");
                int set = Add($"IFCMATERIALLAYERSET((#{layer}),{S($"{material} {t * 1000:0} мм")})");
                usage = Add($"IFCMATERIALLAYERSETUSAGE(#{set},.AXIS2.,.POSITIVE.,{R(-t / 2)})");
                _layerSetUsages[(material, t)] = usage;
            }
            Rel("IFCRELASSOCIATESMATERIAL", "ls:" + e.Id, $"(#{product}),#{usage}");
        }

        private void WriteProperties(int product, ArchitecturalElement source, ArchitecturalElement e, int floor)
        {
            var props = new List<int>
            {
                Add($"IFCPROPERTYSINGLEVALUE('ElementId',$,IFCIDENTIFIER({S(source.Id)}),$)"),
                Add($"IFCPROPERTYSINGLEVALUE('ElementType',$,IFCLABEL({S(e.Type)}),$)"),
                Add($"IFCPROPERTYSINGLEVALUE('BuildingGroup',$,IFCLABEL({S(e.BuildingGroupId)}),$)"),
                Add($"IFCPROPERTYSINGLEVALUE('Material',$,IFCLABEL({S(e.Material)}),$)"),
                Add($"IFCPROPERTYSINGLEVALUE('LevelIndex',$,IFCINTEGER({e.LevelIndex}),$)"),
                Add($"IFCPROPERTYSINGLEVALUE('Width',$,IFCPOSITIVELENGTHMEASURE({R(Math.Max(0.001, e.Width))}),$)"),
                Add($"IFCPROPERTYSINGLEVALUE('Height',$,IFCPOSITIVELENGTHMEASURE({R(Math.Max(0.001, e.Height))}),$)"),
                Add($"IFCPROPERTYSINGLEVALUE('Depth',$,IFCPOSITIVELENGTHMEASURE({R(Math.Max(0.001, e.Depth))}),$)"),
                Add($"IFCPROPERTYSINGLEVALUE('RotationY',$,IFCREAL({R(e.RotationY)}),$)")
            };
            if (e.Type == ElementTypes.Roof)
                props.Add(Add($"IFCPROPERTYSINGLEVALUE('RoofShape',$,IFCLABEL({S(RoofShapes.Normalize(e.RoofShape))}),$)"));
            if (ElementTypes.IsProfiledType(e.Type))
                props.Add(Add($"IFCPROPERTYSINGLEVALUE('ProfileType',$,IFCLABEL({S(e.EffectiveProfile)}),$)"));
            if (source.RepeatLevels > 1)
            {
                props.Add(Add($"IFCPROPERTYSINGLEVALUE('RepeatFloor',$,IFCINTEGER({floor + 1}),$)"));
                props.Add(Add($"IFCPROPERTYSINGLEVALUE('TwistAnglePerFloor',$,IFCREAL({R(source.TwistAnglePerFloor)}),$)"));
                props.Add(Add($"IFCPROPERTYSINGLEVALUE('TaperScalePerFloor',$,IFCREAL({R(source.TaperScalePerFloor)}),$)"));
            }
            if (e.HostElementId is not null)
                props.Add(Add($"IFCPROPERTYSINGLEVALUE('HostElementId',$,IFCIDENTIFIER({S(e.HostElementId)}),$)"));
            if (e.Layers.Count > 0)
            {
                // Назначение, плотность и цвет слоёв (в IFC2X3 у IfcMaterialLayer их нет) — для точного Round-Trip пирога.
                props.Add(Add($"IFCPROPERTYSINGLEVALUE('LayerRoles',$,IFCLABEL({S(string.Join(";", e.Layers.Select(l => l.Role.ToString())))}),$)"));
                props.Add(Add($"IFCPROPERTYSINGLEVALUE('LayerDensities',$,IFCLABEL({S(string.Join(";", e.Layers.Select(l => l.DensityKgM3.ToString("0.###", Inv))))}),$)"));
                props.Add(Add($"IFCPROPERTYSINGLEVALUE('LayerColors',$,IFCLABEL({S(string.Join(";", e.Layers.Select(l => l.ColorHex)))}),$)"));
            }

            string key = floor == 0 ? e.Id : $"{e.Id}:{floor}";
            int pset = Add($"IFCPROPERTYSET('{Guid("pset:" + key)}',#{_ownerHistory},'AIA_Parameters',$,{List(props)})");
            Rel("IFCRELDEFINESBYPROPERTIES", "pdef:" + key, $"(#{product}),#{pset}");
            WriteUserProperties(product, source, key);
        }

        /// <summary>
        /// Свойства пользователя (общие параметры инспектора) — отдельный набор AIA_UserParameters: имя свойства — имя определения,
        /// описание — его Guid (точный Round-Trip), значение — тип IFC по смыслу: текст — IfcLabel (длиннее 255 — IfcText), число —
        /// IfcReal, целое — IfcInteger, да/нет — IfcBoolean, ссылка на элемент — IfcIdentifier, длина/площадь/объём/угол/масса/
        /// плотность/сила — IfcLengthMeasure… в единицах файла (м, м², м³, рад, кг, кг/м³, Н). Без значений набор не пишется.
        /// </summary>
        private void WriteUserProperties(int product, ArchitecturalElement source, string key)
        {
            if (source.UserParameters is not { } values) return;
            var props = new List<int>(values.Count);
            foreach (var (guid, value) in values.OrderBy(p => UserDefinition(p.Key, p.Value).Name, StringComparer.Ordinal).ThenBy(p => p.Key))
            {
                var definition = UserDefinition(guid, value);
                if (UserValue(definition, value) is not { } typed) continue;
                props.Add(Add($"IFCPROPERTYSINGLEVALUE({S(definition.Name)},{S(guid.ToString("D"))},{typed},$)"));
            }
            if (props.Count == 0) return;
            int pset = Add($"IFCPROPERTYSET('{Guid("upset:" + key)}',#{_ownerHistory},{S(UserPsetName)},$,{List(props)})");
            Rel("IFCRELDEFINESBYPROPERTIES", "updef:" + key, $"(#{product}),#{pset}");
            _userPsets++;
            _userProps += props.Count;
        }

        private static string? UserValue(ParameterDefinition definition, ParameterValue raw)
        {
            var value = raw.ConvertTo(definition.StorageType);
            switch (value.StorageType)
            {
                case ParameterStorageType.Bool:
                    return value.AsBool() ? "IFCBOOLEAN(.T.)" : "IFCBOOLEAN(.F.)";
                case ParameterStorageType.Int:
                    return $"IFCINTEGER({value.AsInt().ToString(Inv)})";
                case ParameterStorageType.ElementId:
                    return $"IFCIDENTIFIER({S(value.AsString())})";
                case ParameterStorageType.String:
                    string text = value.AsString() ?? string.Empty;
                    return text.Length <= 255 ? $"IFCLABEL({S(text)})" : $"IFCTEXT({S(text)})";
                case ParameterStorageType.Double:
                    double d = value.AsDouble();
                    if (!double.IsFinite(d)) return null;
                    return definition.SpecType switch
                    {
                        ParameterSpecType.Length => $"IFCLENGTHMEASURE({Exact(d)})",
                        ParameterSpecType.Area => $"IFCAREAMEASURE({Exact(d)})",
                        ParameterSpecType.Volume => $"IFCVOLUMEMEASURE({Exact(d)})",
                        ParameterSpecType.Angle => $"IFCPLANEANGLEMEASURE({Exact(d * Math.PI / 180)})",
                        ParameterSpecType.Mass => $"IFCMASSMEASURE({Exact(d)})",
                        ParameterSpecType.Density => $"IFCMASSDENSITYMEASURE({Exact(d)})",
                        ParameterSpecType.Force => $"IFCFORCEMEASURE({Exact(d)})",
                        _ => $"IFCREAL({Exact(d)})"
                    };
                default:
                    return null;
            }
        }

        /// <summary>Вещественное STEP без округления (биты double): «R» с обязательной десятичной точкой.</summary>
        private static string Exact(double v)
        {
            string s = v.ToString("R", Inv);
            if (s.Contains('.')) return s;
            int exp = s.IndexOfAny(['E', 'e']);
            return exp < 0 ? s + "." : s.Insert(exp, ".");
        }

        /// <summary>Отметка этажа: низ стен/колонн этажа (без фундамента и бассейна), иначе 3 м × индекс.</summary>
        private static double StoreyElevation(IEnumerable<ArchitecturalElement> items, int level)
        {
            var bearing = items.Where(e => e.Type is ElementTypes.Wall or ElementTypes.Column or ElementTypes.Core or ElementTypes.CurtainWall)
                .Select(e => e.BaseY).ToList();
            if (bearing.Count > 0) return Math.Round(bearing.Min(), 3);
            var other = items.Where(e => e.Type is not (ElementTypes.Foundation or ElementTypes.Pool)).Select(e => e.BaseY).ToList();
            return other.Count > 0 ? Math.Round(other.Min(), 3) : level * 3.0;
        }

        // ───────────── STEP-запись ─────────────

        private int Add(string entity)
        {
            int id = _next++;
            _data.Append('#').Append(id).Append('=').Append(entity).Append(";\n");
            return id;
        }

        private void Rel(string type, string key, string tail) =>
            Add($"{type}('{Guid("rel:" + key)}',#{_ownerHistory},$,$,{tail})");

        private int Point3(double x, double y, double z) => Add($"IFCCARTESIANPOINT(({R(x)},{R(y)},{R(z)}))");

        private string Placement3D(double x, double y, double z)
        {
            int point = Math.Abs(x) + Math.Abs(y) + Math.Abs(z) < 1e-9 ? _origin : Point3(x, y, z);
            return $"#{Add($"IFCAXIS2PLACEMENT3D(#{point},$,$)")}";
        }

        private static string List(IEnumerable<int> ids) => "(" + string.Join(",", ids.Select(i => "#" + i.ToString(Inv))) + ")";

        /// <summary>Вещественное число STEP: всегда с десятичной точкой.</summary>
        private static string R(double v)
        {
            if (!double.IsFinite(v) || Math.Abs(v) < 5e-9) return "0.";
            return Math.Round(v, 6).ToString("0.0#####", Inv);
        }

        /// <summary>Строка STEP: апострофы удваиваются, не-ASCII (кириллица) кодируется \X2\XXXX\X0\.</summary>
        private static string S(string? s)
        {
            if (s is null) return "$";
            var sb = new StringBuilder("'");
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c > '~')
                {
                    sb.Append("\\X2\\");
                    while (i < s.Length && s[i] > '~') sb.Append(((int)s[i++]).ToString("X4", Inv));
                    sb.Append("\\X0\\");
                    continue;
                }
                if (c == '\'') sb.Append("''");
                else if (c == '\\') sb.Append("\\\\");
                else sb.Append(c < ' ' ? ' ' : c);
                i++;
            }
            return sb.Append('\'').ToString();
        }

        /// <summary>Детерминированный IfcGloballyUniqueId (22 символа base64 IFC) от проекта и ключа объекта.</summary>
        private string Guid(string key)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(scene.ProjectName + "|" + key));
            var n = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            var chars = new char[22];
            for (int i = 21; i >= 0; i--)
            {
                chars[i] = Base64Chars[(int)(n % 64)];
                n /= 64;
            }
            return new string(chars);
        }
    }
}
