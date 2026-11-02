using System.Globalization;
using System.Text;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Итог импорта IFC: элементы, постройки, координационные оси и отчёт.</summary>
public sealed record IfcImportResult(
    List<ArchitecturalElement> Elements,
    List<BuildingGroup> Groups,
    List<StructuralGridAxis> Axes,
    string ProjectName,
    string Schema,
    List<string> Report);

/// <summary>
/// Импорт IFC2X3 / IFC4 (Renga, Revit, ArchiCAD и собственный экспорт — полный Round-Trip): разбор STEP-графа ISO-10303-21
/// (ссылки, вложенные списки, типизированные значения, строки с \X2\…\X0\), единицы длины (IfcSIUnit .MILLI. → 0.001),
/// цепочки IfcLocalPlacement → IfcAxis2Placement3D, перевод из Z-Up IFC в Y-Up сцены: (X, Y, Z)ifc → (x = X, y = Z, z = −Y).
/// Восстанавливаются этажи, оси IfcGrid, профили сечений, классы элементов, проёмы IfcOpeningElement с привязкой к хосту
/// и заполнения IfcRelFillsElement; незаполненный проём становится сквозным wall_opening / slab_opening.
/// </summary>
public sealed class CadIfcImporterService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public Task<IfcImportResult> ImportAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Import(File.ReadAllText(path, Encoding.Latin1), Path.GetFileName(path)), ct);

    // ═════════════════════════════ STEP (ISO-10303-21) ═════════════════════════════

    public readonly record struct StepRef(int Id);

    public readonly record struct StepEnum(string Value);

    public sealed record StepTyped(string Name, List<object?> Args);

    public sealed record StepEntity(int Id, string Type, List<object?> Args);

    /// <summary>Разбор секции DATA: «#id=ТИП(аргументы);» с учётом строк, вложенных списков и типизированных значений.</summary>
    public static (Dictionary<int, StepEntity> Entities, string Schema) ParseStep(string text)
    {
        var entities = new Dictionary<int, StepEntity>();
        string schema = "IFC2X3";
        int schemaAt = text.IndexOf("FILE_SCHEMA", StringComparison.OrdinalIgnoreCase);
        if (schemaAt >= 0)
        {
            int q1 = text.IndexOf('\'', schemaAt), q2 = q1 >= 0 ? text.IndexOf('\'', q1 + 1) : -1;
            if (q1 >= 0 && q2 > q1) schema = text[(q1 + 1)..q2].ToUpperInvariant();
        }

        int data = text.IndexOf("DATA;", StringComparison.OrdinalIgnoreCase);
        if (data < 0) throw new InvalidDataException("Файл не является IFC (ISO-10303-21): нет секции DATA.");
        int pos = data + 5;
        int n = text.Length;
        while (pos < n)
        {
            // Начало записи «#id» (после последней записи секции идёт ENDSEC;).
            int hash = text.IndexOf('#', pos);
            if (hash < 0) break;
            int next = pos;
            while (next < hash && char.IsWhiteSpace(text[next])) next++;
            if (next < hash && string.Compare(text, next, "ENDSEC", 0, 6, StringComparison.OrdinalIgnoreCase) == 0) break;
            int i = hash + 1;
            int id = 0;
            while (i < n && char.IsDigit(text[i])) id = id * 10 + (text[i++] - '0');
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n || text[i] != '=')
            {
                pos = i;
                continue;
            }
            i++;
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i < n && text[i] == '(')
            {
                // Сложный экземпляр (несколько сущностей) — пропускаем до конца записи.
                pos = SkipRecord(text, i);
                continue;
            }
            int nameStart = i;
            while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            string type = text[nameStart..i].ToUpperInvariant();
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n || text[i] != '(')
            {
                pos = SkipRecord(text, i);
                continue;
            }
            var args = ParseList(text, ref i);
            entities[id] = new StepEntity(id, type, args);
            pos = SkipRecord(text, i);
        }
        return (entities, schema);
    }

    private static int SkipRecord(string text, int i)
    {
        bool inString = false;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'') inString = !inString;
            else if (c == ';' && !inString) return i + 1;
        }
        return text.Length;
    }

    /// <summary>Список «( … )» начиная с открывающей скобки; i — после закрывающей.</summary>
    private static List<object?> ParseList(string s, ref int i)
    {
        var list = new List<object?>();
        i++; // '('
        while (i < s.Length)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) break;
            char c = s[i];
            if (c == ')') { i++; break; }
            if (c == ',') { i++; continue; }
            list.Add(ParseValue(s, ref i));
        }
        return list;
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    private static object? ParseValue(string s, ref int i)
    {
        char c = s[i];
        switch (c)
        {
            case '$':
            case '*':
                i++;
                return null;
            case '#':
            {
                i++;
                int id = 0;
                while (i < s.Length && char.IsDigit(s[i])) id = id * 10 + (s[i++] - '0');
                return new StepRef(id);
            }
            case '\'':
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length)
                {
                    if (s[i] == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                return DecodeString(sb.ToString());
            }
            case '.':
            {
                int end = s.IndexOf('.', i + 1);
                var value = s[(i + 1)..end];
                i = end + 1;
                return new StepEnum(value.ToUpperInvariant());
            }
            case '(':
                return ParseList(s, ref i);
            case '"':
            {
                int end = s.IndexOf('"', i + 1);
                var value = s[(i + 1)..end];
                i = end + 1;
                return value;
            }
        }
        if (char.IsDigit(c) || c is '-' or '+')
        {
            int start = i;
            i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or 'E' or 'e' or '-' or '+')) i++;
            var token = s[start..i];
            return double.TryParse(token, NumberStyles.Float, Inv, out var d) ? d : 0.0;
        }
        if (char.IsLetter(c))
        {
            int start = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
            string name = s[start..i].ToUpperInvariant();
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '(') return new StepTyped(name, ParseList(s, ref i));
            return new StepEnum(name);
        }
        i++;
        return null;
    }

    /// <summary>Строка STEP: \X2\XXXX…\X0\ (UTF-16), \X4\…\X0\, \X\HH (ISO 8859-1), \S\c, \\.</summary>
    public static string DecodeString(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            if (Starts(s, i, "\\X2\\"))
            {
                int end = s.IndexOf("\\X0\\", i + 4, StringComparison.Ordinal);
                if (end < 0) end = s.Length;
                for (int k = i + 4; k + 4 <= end; k += 4)
                    if (int.TryParse(s.AsSpan(k, 4), NumberStyles.HexNumber, Inv, out int code)) sb.Append((char)code);
                i = Math.Min(s.Length, end + 4) - 1;
                continue;
            }
            if (Starts(s, i, "\\X4\\"))
            {
                int end = s.IndexOf("\\X0\\", i + 4, StringComparison.Ordinal);
                if (end < 0) end = s.Length;
                for (int k = i + 4; k + 8 <= end; k += 8)
                    if (int.TryParse(s.AsSpan(k, 8), NumberStyles.HexNumber, Inv, out int code)) sb.Append(char.ConvertFromUtf32(code));
                i = Math.Min(s.Length, end + 4) - 1;
                continue;
            }
            if (Starts(s, i, "\\X\\") && i + 5 <= s.Length && int.TryParse(s.AsSpan(i + 3, 2), NumberStyles.HexNumber, Inv, out int b))
            {
                sb.Append((char)b);
                i += 4;
                continue;
            }
            if (Starts(s, i, "\\S\\") && i + 3 < s.Length)
            {
                sb.Append((char)(s[i + 3] + 128));
                i += 3;
                continue;
            }
            if (Starts(s, i, "\\\\"))
            {
                sb.Append('\\');
                i++;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static bool Starts(string s, int i, string token) => string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

    // ═════════════════════════════ Геометрия: векторы и системы координат ═════════════════════════════

    public readonly record struct Vec3(double X, double Y, double Z)
    {
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
        public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public Vec3 Cross(Vec3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
        public double Length => Math.Sqrt(Dot(this));
        public Vec3 Normalized => Length > 1e-12 ? this * (1 / Length) : new Vec3(0, 0, 1);
    }

    /// <summary>Ортонормированная система координат IFC: начало и оси X, Y, Z (мировые, Z-Up, метры).</summary>
    public readonly record struct Frame(Vec3 Origin, Vec3 X, Vec3 Y, Vec3 Z)
    {
        public static Frame Identity => new(default, new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));

        public Vec3 Point(Vec3 p) => Origin + X * p.X + Y * p.Y + Z * p.Z;

        public Vec3 Direction(Vec3 d) => X * d.X + Y * d.Y + Z * d.Z;

        /// <summary>Вложенная система: local задана в координатах этой.</summary>
        public Frame Then(Frame local) => new(Point(local.Origin), Direction(local.X), Direction(local.Y), Direction(local.Z));

        public static Frame FromAxes(Vec3 origin, Vec3? axisZ, Vec3? refX)
        {
            var z = (axisZ ?? new Vec3(0, 0, 1)).Normalized;
            var x0 = refX ?? new Vec3(1, 0, 0);
            var x = x0 - z * x0.Dot(z);
            if (x.Length < 1e-9) x = Math.Abs(z.X) < 0.9 ? new Vec3(1, 0, 0) - z * z.X : new Vec3(0, 1, 0) - z * z.Y;
            x = x.Normalized;
            return new Frame(origin, x, z.Cross(x).Normalized, z);
        }
    }

    // ═════════════════════════════ Импорт ═════════════════════════════

    private enum ProfileKind
    {
        Rectangle,
        RectangleHollow,
        Circle,
        Ellipse,
        CircleHollow,
        IShape,
        Polygon,
        Unknown
    }

    private sealed class Profile
    {
        public ProfileKind Kind;
        public double XDim, YDim, Radius, Wall, Web, Flange;
        public List<(double U, double V)> Points = [];

        /// <summary>Размещение профиля в плоскости сечения (начало и ось X).</summary>
        public (double X, double Y) Origin;
        public (double X, double Y) Axis = (1, 0);

        public double Width => Kind switch
        {
            ProfileKind.Circle or ProfileKind.CircleHollow => 2 * Radius,
            ProfileKind.Polygon => Points.Count > 0 ? Points.Max(p => p.U) - Points.Min(p => p.U) : XDim,
            _ => XDim
        };

        public double Height => Kind switch
        {
            ProfileKind.Circle or ProfileKind.CircleHollow => 2 * Radius,
            ProfileKind.Polygon => Points.Count > 0 ? Points.Max(p => p.V) - Points.Min(p => p.V) : YDim,
            _ => YDim
        };

        /// <summary>Контур сечения в координатах профиля (для габаритов проёмов).</summary>
        public IEnumerable<(double U, double V)> Outline()
        {
            double hx = Width / 2, hy = Height / 2;
            IEnumerable<(double U, double V)> local = Kind == ProfileKind.Polygon && Points.Count >= 3
                ? Points
                : Kind is ProfileKind.Circle or ProfileKind.CircleHollow or ProfileKind.Ellipse
                    ? Enumerable.Range(0, 24).Select(k => 2 * Math.PI * k / 24).Select(a => (hx * Math.Cos(a), hy * Math.Sin(a)))
                    : [(-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy)];
            foreach (var (u, v) in local)
                yield return (Origin.X + u * Axis.X - v * Axis.Y, Origin.Y + u * Axis.Y + v * Axis.X);
        }
    }

    /// <summary>Твердотельная часть представления в мировых координатах.</summary>
    private sealed class Solid
    {
        public Profile? Profile;

        /// <summary>Система профиля: начало — точка профиля (0, 0), X/Y — оси профиля, Z — нормаль плоскости сечения.</summary>
        public Frame Frame;

        /// <summary>Единичное направление выдавливания (мировое) и длина.</summary>
        public Vec3 Direction;
        public double Depth;

        /// <summary>Вершины сетки (Brep / поверхности / треугольники) и нормали граней.</summary>
        public List<Vec3>? Mesh;
        public List<Vec3>? FaceNormals;

        public bool IsExtrusion => Profile is not null;

        public IEnumerable<Vec3> Corners()
        {
            if (Mesh is not null)
            {
                foreach (var p in Mesh) yield return p;
                yield break;
            }
            foreach (var (u, v) in Profile!.Outline())
            {
                var p = Frame.Origin + Frame.X * u + Frame.Y * v;
                yield return p;
                yield return p + Direction * Depth;
            }
        }
    }

    private sealed class ImportContext
    {
        public required Dictionary<int, StepEntity> E;
        public double Scale = 1;
        public readonly Dictionary<int, Frame> Placements = [];
        public readonly List<string> Warnings = [];

        public StepEntity? Get(object? v) => v is StepRef r && E.TryGetValue(r.Id, out var e) ? e : null;
    }

    /// <summary>Классы IFC, превращаемые в элементы сцены.</summary>
    private static readonly Dictionary<string, string> ClassMap = new()
    {
        ["IFCWALL"] = ElementTypes.Wall,
        ["IFCWALLSTANDARDCASE"] = ElementTypes.Wall,
        ["IFCWALLELEMENTEDCASE"] = ElementTypes.Wall,
        ["IFCCURTAINWALL"] = ElementTypes.CurtainWall,
        ["IFCCOLUMN"] = ElementTypes.Column,
        ["IFCCOLUMNSTANDARDCASE"] = ElementTypes.Column,
        ["IFCBEAM"] = ElementTypes.Beam,
        ["IFCBEAMSTANDARDCASE"] = ElementTypes.Beam,
        ["IFCMEMBER"] = ElementTypes.Beam,
        ["IFCMEMBERSTANDARDCASE"] = ElementTypes.Beam,
        ["IFCSLAB"] = ElementTypes.Slab,
        ["IFCSLABSTANDARDCASE"] = ElementTypes.Slab,
        ["IFCFOOTING"] = ElementTypes.Foundation,
        ["IFCROOF"] = ElementTypes.Roof,
        ["IFCWINDOW"] = ElementTypes.Window,
        ["IFCWINDOWSTANDARDCASE"] = ElementTypes.Window,
        ["IFCDOOR"] = ElementTypes.Door,
        ["IFCDOORSTANDARDCASE"] = ElementTypes.Door,
        ["IFCSTAIR"] = ElementTypes.Stairs,
        ["IFCSTAIRFLIGHT"] = ElementTypes.Stairs,
        ["IFCBUILDINGELEMENTPROXY"] = ElementTypes.Asset
    };

    public static IfcImportResult Import(string text, string fileName)
    {
        var (entities, schema) = ParseStep(text);
        var ctx = new ImportContext { E = entities };
        ctx.Scale = LengthScale(ctx);

        string projectName = entities.Values.FirstOrDefault(e => e.Type == "IFCPROJECT") is { } proj ? Str(proj, 2) ?? Path.GetFileNameWithoutExtension(fileName) : Path.GetFileNameWithoutExtension(fileName);

        // ── Пространственная структура: элемент → этаж → постройка.
        var containedIn = new Dictionary<int, int>();
        var aggregatedTo = new Dictionary<int, int>();
        var voids = new List<(int Host, int Opening)>();
        var fills = new Dictionary<int, int>(); // заполнение → проём
        var psets = new Dictionary<int, Dictionary<string, object?>>();
        var userProps = new Dictionary<int, List<object?>>();
        var materials = new Dictionary<int, string>();
        var layerSets = new Dictionary<int, List<(string Name, double Thickness)>>();
        foreach (var e in entities.Values)
        {
            switch (e.Type)
            {
                case "IFCRELCONTAINEDINSPATIALSTRUCTURE":
                    if (e.Args.Count > 5 && e.Args[5] is StepRef structure && e.Args[4] is List<object?> related)
                        foreach (var r in related.OfType<StepRef>()) containedIn[r.Id] = structure.Id;
                    break;
                case "IFCRELAGGREGATES":
                case "IFCRELNESTS":
                    if (e.Args.Count > 5 && e.Args[4] is StepRef parent)
                        foreach (var r in (e.Args[5] as List<object?> ?? []).OfType<StepRef>()) aggregatedTo[r.Id] = parent.Id;
                    break;
                case "IFCRELVOIDSELEMENT":
                    if (e.Args.Count > 5 && e.Args[4] is StepRef host && e.Args[5] is StepRef opening) voids.Add((host.Id, opening.Id));
                    break;
                case "IFCRELFILLSELEMENT":
                    if (e.Args.Count > 5 && e.Args[4] is StepRef op && e.Args[5] is StepRef fill) fills[fill.Id] = op.Id;
                    break;
                case "IFCRELDEFINESBYPROPERTIES":
                    if (e.Args.Count > 5 && ctx.Get(e.Args[5]) is { Type: "IFCPROPERTYSET" } pset)
                    {
                        // Свойства пользователя — отдельно: их имена не должны подменять служебные (Width, ElementType…).
                        if (Str(pset, 2) == CadExportService.UserPsetName)
                        {
                            foreach (var r in (e.Args[4] as List<object?> ?? []).OfType<StepRef>())
                                userProps[r.Id] = [.. userProps.GetValueOrDefault(r.Id) ?? [], .. pset.Args.ElementAtOrDefault(4) as List<object?> ?? []];
                            break;
                        }
                        var props = ReadProperties(ctx, pset);
                        foreach (var r in (e.Args[4] as List<object?> ?? []).OfType<StepRef>())
                        {
                            if (!psets.TryGetValue(r.Id, out var dict)) psets[r.Id] = dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            // AIA_Parameters (собственный экспорт) имеют приоритет над прочими наборами.
                            bool own = Str(pset, 2) == "AIA_Parameters";
                            foreach (var (k, v) in props)
                                if (own || !dict.ContainsKey(k)) dict[k] = v;
                        }
                    }
                    break;
                case "IFCRELASSOCIATESMATERIAL":
                    if (e.Args.Count > 5 && MaterialName(ctx, ctx.Get(e.Args[5])) is { } mat)
                        foreach (var r in (e.Args[4] as List<object?> ?? []).OfType<StepRef>()) materials.TryAdd(r.Id, mat);
                    if (e.Args.Count > 5 && ReadLayerSet(ctx, ctx.Get(e.Args[5])) is { Count: > 0 } layerSet)
                        foreach (var r in (e.Args[4] as List<object?> ?? []).OfType<StepRef>()) layerSets.TryAdd(r.Id, layerSet);
                    break;
            }
        }

        var hostOfOpening = new Dictionary<int, int>();
        foreach (var (host, opening) in voids) hostOfOpening.TryAdd(opening, host);

        // ── Этажи и постройки.
        var storeys = entities.Values.Where(e => e.Type == "IFCBUILDINGSTOREY").ToList();
        var buildings = entities.Values.Where(e => e.Type == "IFCBUILDING").ToList();
        string defaultGroup = buildings.Count > 0 ? Str(buildings[0], 2) ?? "IFC" : $"IFC · {Path.GetFileNameWithoutExtension(fileName)}";
        var groups = new List<BuildingGroup>();
        var buildingGroup = new Dictionary<int, string>();
        foreach (var b in buildings)
        {
            string name = Str(b, 2) is { Length: > 0 } n ? n : $"Здание {groups.Count + 1}";
            while (groups.Any(g => g.Id == name)) name += " (2)";
            buildingGroup[b.Id] = name;
            groups.Add(new BuildingGroup(name, (Str(b, 4) ?? Str(b, 3)) is { Length: > 0 } purpose ? purpose : "residential"));
        }
        if (groups.Count == 0) groups.Add(new BuildingGroup(defaultGroup));

        string GroupOfSpatial(int id)
        {
            for (int guard = 0; guard < 16 && id != 0; guard++)
            {
                if (buildingGroup.TryGetValue(id, out var g)) return g;
                if (!aggregatedTo.TryGetValue(id, out id)) break;
            }
            return groups[0].Id;
        }

        var storeyElevation = storeys.ToDictionary(s => s.Id, s => Num(s, 9) is { } elev ? elev * ctx.Scale : Placement(ctx, s.Args.ElementAtOrDefault(5)).Origin.Z);
        var storeyLevel = new Dictionary<int, int>();
        foreach (var byBuilding in storeys.GroupBy(s => GroupOfSpatial(s.Id)))
        {
            int index = 0;
            foreach (var s in byBuilding.OrderBy(s => storeyElevation[s.Id])) storeyLevel[s.Id] = index++;
        }

        // Контейнер элемента: этаж напрямую или через родителя (например, IfcRoof → IfcSlab).
        (int Level, string Group) SpatialOf(int id)
        {
            for (int guard = 0; guard < 16; guard++)
            {
                if (containedIn.TryGetValue(id, out var s))
                    return (storeyLevel.GetValueOrDefault(s), GroupOfSpatial(s));
                if (fills.TryGetValue(id, out var op) && hostOfOpening.TryGetValue(op, out var voidHost)) { id = voidHost; continue; }
                if (!aggregatedTo.TryGetValue(id, out id)) break;
            }
            return (0, groups[0].Id);
        }

        // ── Элементы.
        var result = new List<ArchitecturalElement>();
        var byStep = new Dictionary<int, ArchitecturalElement>();
        var usedIds = new HashSet<string>();
        int skipped = 0, layeredElements = 0, userValues = 0;
        var openingIds = voids.Select(v => v.Opening).ToHashSet();
        var roofChildren = aggregatedTo.Where(p => entities.TryGetValue(p.Value, out var parent) && parent.Type == "IFCROOF").Select(p => p.Key).ToHashSet();

        foreach (var e in entities.Values.Where(e => ClassMap.ContainsKey(e.Type)).OrderBy(e => e.Type is "IFCWINDOW" or "IFCDOOR" or "IFCWINDOWSTANDARDCASE" or "IFCDOORSTANDARDCASE" ? 1 : 0))
        {
            var props = psets.GetValueOrDefault(e.Id);
            string type = ClassMap[e.Type];
            if (props?.GetValueOrDefault("ElementType") is string own && ElementTypes.All.Contains(own)) type = own;
            else if (e.Type.StartsWith("IFCSLAB", StringComparison.Ordinal))
                type = Enum(e, 8) switch { "ROOF" => ElementTypes.Roof, "BASESLAB" => ElementTypes.Foundation, _ => roofChildren.Contains(e.Id) ? ElementTypes.Roof : ElementTypes.Slab };
            else if (e.Type == "IFCFOOTING") type = ElementTypes.Foundation;

            var solids = Representation(ctx, e);
            if (solids.Count == 0)
            {
                // IfcRoof / IfcStair без собственного тела — геометрия у вложенных элементов.
                if (e.Type is not ("IFCROOF" or "IFCSTAIR" or "IFCCURTAINWALL")) skipped++;
                continue;
            }

            var (level, group) = SpatialOf(e.Id);
            var element = new ArchitecturalElement
            {
                Type = type,
                Name = Str(e, 2) ?? type,
                BuildingGroupId = props?.GetValueOrDefault("BuildingGroup") is string g && groups.Any(x => x.Id == g) ? g : group,
                LevelIndex = props?.GetValueOrDefault("LevelIndex") is double lv ? (int)lv : level,
                Material = props?.GetValueOrDefault("Material") as string ?? NormalizeMaterial(materials.GetValueOrDefault(e.Id), type)
            };
            string? tag = Str(e, 7);
            string id = props?.GetValueOrDefault("ElementId") as string ?? tag ?? string.Empty;
            element.Id = id.StartsWith("el_", StringComparison.Ordinal) && usedIds.Add(id) ? id : NewUniqueId(usedIds);

            if (!BuildGeometry(element, solids, props, ctx))
            {
                skipped++;
                continue;
            }
            // Пирог конструкции: многослойный IfcMaterialLayerSet (однослойная конструкция = однородный элемент) или собственный экспорт.
            if (MaterialLayers.IsLayerHost(element) && layerSets.TryGetValue(e.Id, out var layerSet) &&
                (layerSet.Count >= 2 || props?.GetValueOrDefault("LayerRoles") is string))
            {
                element.Layers = BuildLayers(layerSet, props, ctx.Scale);
                layeredElements++;
            }
            userValues += ApplyUserParameters(ctx, element, userProps.GetValueOrDefault(e.Id));
            byStep[e.Id] = element;
            result.Add(element);
        }

        // ── Окна и двери: хост — стена проёма, который они заполняют (IfcRelFillsElement → IfcRelVoidsElement).
        foreach (var (stepId, el) in byStep.Where(p => p.Value.Type is ElementTypes.Window or ElementTypes.Door).ToList())
        {
            ArchitecturalElement? host = null;
            if (fills.TryGetValue(stepId, out var opening) && hostOfOpening.TryGetValue(opening, out var linkHost))
                host = byStep.GetValueOrDefault(linkHost);
            if (host is null && psets.GetValueOrDefault(stepId)?.GetValueOrDefault("HostElementId") is string hid)
                host = result.FirstOrDefault(r => r.Id == hid);
            if (host is null || !ElementTypes.IsHostType(host.Type)) continue;
            FitToHost(el, Representation(ctx, entities[stepId]), host, psets.GetValueOrDefault(stepId));
        }

        // ── Проёмы без заполнения: сквозной проём стены или отверстие плиты.
        int wallOpenings = 0, slabOpenings = 0;
        var filledOpenings = fills.Values.ToHashSet();
        foreach (var (hostStep, openingStep) in voids)
        {
            if (filledOpenings.Contains(openingStep) || !byStep.TryGetValue(hostStep, out var host) || !entities.TryGetValue(openingStep, out var opEntity)) continue;
            var solids = Representation(ctx, opEntity);
            if (solids.Count == 0) continue;
            var props = psets.GetValueOrDefault(openingStep);
            bool slabHost = host.CanHostSlabOpenings && !ElementTypes.IsHostType(host.Type);
            var opening = new ArchitecturalElement
            {
                Type = slabHost ? ElementTypes.SlabOpening : ElementTypes.WallOpening,
                Name = Str(opEntity, 2) is { Length: > 0 } nm ? nm : slabHost ? "Отверстие в плите" : "Проём",
                BuildingGroupId = host.BuildingGroupId,
                LevelIndex = host.LevelIndex,
                Material = "void",
                HostElementId = host.Id
            };
            string id = props?.GetValueOrDefault("ElementId") as string ?? Str(opEntity, 7) ?? string.Empty;
            opening.Id = id.StartsWith("el_", StringComparison.Ordinal) && usedIds.Add(id) ? id : NewUniqueId(usedIds);
            if (slabHost)
            {
                FitSlabOpening(opening, solids, host);
                slabOpenings++;
            }
            else
            {
                FitToHost(opening, solids, host, props);
                wallOpenings++;
            }
            userValues += ApplyUserParameters(ctx, opening, userProps.GetValueOrDefault(openingStep));
            result.Add(opening);
        }

        // ── Координационные оси IfcGrid (повторы по этажам объединяются).
        var axes = new List<StructuralGridAxis>();
        int grids = 0;
        foreach (var grid in entities.Values.Where(e => e.Type == "IFCGRID"))
        {
            grids++;
            var frame = Placement(ctx, grid.Args.ElementAtOrDefault(5));
            string gid = (Str(grid, 4) ?? Str(grid, 3)) is { } og && groups.Any(g => g.Id == og) ? og : SpatialOf(grid.Id).Group;
            foreach (int index in new[] { 7, 8 })
                foreach (var axisRef in grid.Args.ElementAtOrDefault(index) as List<object?> ?? [])
                {
                    if (ctx.Get(axisRef) is not { Type: "IFCGRIDAXIS" } axis) continue;
                    var pts = CurvePoints(ctx, ctx.Get(axis.Args.ElementAtOrDefault(1)));
                    if (pts.Count < 2) continue;
                    var a = frame.Point(new Vec3(pts[0].U, pts[0].V, 0));
                    var b = frame.Point(new Vec3(pts[^1].U, pts[^1].V, 0));
                    double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-6) continue;
                    string label = Str(axis, 0) ?? (axes.Count + 1).ToString(Inv);
                    StructuralGridAxis candidate;
                    if (Math.Abs(dx) <= 1e-3 * len) candidate = new StructuralGridAxis(label, StructuralGridAxis.AlongZ, Round((a.X + b.X) / 2), gid);
                    else if (Math.Abs(dy) <= 1e-3 * len) candidate = new StructuralGridAxis(label, StructuralGridAxis.AlongX, Round(-(a.Y + b.Y) / 2), gid);
                    else continue; // косые оси — вне ортогональной сетки А-1
                    if (!axes.Any(x => x.BuildingGroupId == gid && x.Label == candidate.Label && x.Direction == candidate.Direction && Math.Abs(x.Coordinate - candidate.Coordinate) < 1e-4))
                        axes.Add(candidate);
                }
        }

        foreach (var g in result.Select(r => r.BuildingGroupId).Distinct())
            if (groups.All(x => x.Id != g)) groups.Add(new BuildingGroup(g));
        groups.RemoveAll(g => result.All(r => r.BuildingGroupId != g.Id) && axes.All(a => a.BuildingGroupId != g.Id));

        var report = new List<string>
        {
            $"IFC «{fileName}»: схема {schema}, единицы длины ×{ctx.Scale.ToString("0.####", Inv)} ({(Math.Abs(ctx.Scale - 0.001) < 1e-9 ? "мм" : Math.Abs(ctx.Scale - 1) < 1e-9 ? "м" : "пересчёт в м")}), STEP-записей {entities.Count:N0}",
            $"Построек {groups.Count}, этажей {storeys.Count}, элементов {result.Count} (стен {result.Count(r => r.Type is ElementTypes.Wall or ElementTypes.Core)}, " +
            $"колонн {result.Count(r => r.Type == ElementTypes.Column)}, балок {result.Count(r => r.Type == ElementTypes.Beam)}, плит {result.Count(r => r.Type is ElementTypes.Slab or ElementTypes.Foundation or ElementTypes.CantileverSlab)}, " +
            $"окон {result.Count(r => r.Type == ElementTypes.Window)}, дверей {result.Count(r => r.Type == ElementTypes.Door)})",
            $"Проёмы: IfcRelVoidsElement {voids.Count}, заполнений IfcRelFillsElement {fills.Count}, сквозных проёмов стен {wallOpenings}, отверстий плит {slabOpenings}",
            $"Профили: {string.Join(", ", result.Where(r => ElementTypes.IsProfiledType(r.Type)).GroupBy(r => r.EffectiveProfile).Select(g => $"{g.Key} × {g.Count()}"))}",
            $"Координационные оси: IfcGrid × {grids} → осей {axes.Count}",
            $"Многослойные конструкции IfcMaterialLayerSet: {layeredElements} (слоёв {result.Sum(r => r.Layers.Count)})"
        };
        if (userValues > 0)
            report.Add($"Свойства пользователя ({CadExportService.UserPsetName}): значений {userValues} у {result.Count(r => r.UserParameters is not null)} элементов");
        if (skipped > 0) report.Add($"Пропущено элементов без распознаваемой геометрии: {skipped}");
        report.AddRange(ctx.Warnings.Distinct().Take(8));
        return new IfcImportResult(result, groups, axes, projectName, schema, report);
    }

    private static string NewUniqueId(HashSet<string> used)
    {
        string id;
        do id = ArchitecturalElement.NewId(); while (!used.Add(id));
        return id;
    }

    private static double Round(double v)
    {
        double r = Math.Round(v, 5);
        return r == 0 ? 0 : r;
    }

    // ───────────── Чтение атрибутов ─────────────

    private static string? Str(StepEntity e, int index) => e.Args.ElementAtOrDefault(index) as string;

    private static double? Num(StepEntity e, int index) => e.Args.ElementAtOrDefault(index) switch
    {
        double d => d,
        StepTyped { Args: [double d] } => d,
        _ => null
    };

    private static string? Enum(StepEntity e, int index) => e.Args.ElementAtOrDefault(index) is StepEnum en ? en.Value : null;

    private static double D(object? v) => v switch
    {
        double d => d,
        StepTyped { Args: [double d] } => d,
        _ => 0
    };

    private static Dictionary<string, object?> ReadProperties(ImportContext ctx, StepEntity pset)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in pset.Args.ElementAtOrDefault(4) as List<object?> ?? [])
        {
            if (ctx.Get(r) is not { Type: "IFCPROPERTYSINGLEVALUE" } prop || Str(prop, 0) is not { } name) continue;
            dict[name] = prop.Args.ElementAtOrDefault(2) switch
            {
                StepTyped { Name: "IFCPOSITIVELENGTHMEASURE" or "IFCLENGTHMEASURE", Args: [double d] } => d * ctx.Scale,
                StepTyped { Args: [double d] } => d,
                StepTyped { Args: [string s] } => s,
                StepTyped { Args: [StepEnum en] } => en.Value,
                _ => null
            };
        }
        return dict;
    }

    /// <summary>
    /// Свойства набора AIA_UserParameters → общие параметры элемента: Guid из описания свойства (собственный экспорт — то же
    /// определение, что в проекте), иначе определение по имени и смыслу; смысл — по типу IFC (IfcLengthMeasure — длина в метрах
    /// с пересчётом единиц файла, IfcPlaneAngleMeasure — радианы в градусы, IfcBoolean — да/нет…). Возвращает число значений.
    /// </summary>
    private static int ApplyUserParameters(ImportContext ctx, ArchitecturalElement element, List<object?>? properties)
    {
        if (properties is null) return 0;
        int count = 0;
        var registry = ParameterRegistry.Shared;
        foreach (var r in properties)
        {
            if (ctx.Get(r) is not { Type: "IFCPROPERTYSINGLEVALUE" } prop || Str(prop, 0) is not { Length: > 0 } name ||
                prop.Args.ElementAtOrDefault(2) is not StepTyped typed) continue;
            double s = ctx.Scale;
            (ParameterSpecType Spec, ParameterValue Value)? parsed = typed switch
            {
                { Name: "IFCBOOLEAN" or "IFCLOGICAL", Args: [StepEnum en] } => (ParameterSpecType.YesNo, ParameterValue.FromBool(en.Value is "T" or "TRUE")),
                { Name: "IFCINTEGER" or "IFCCOUNTMEASURE", Args: [double d] } => (ParameterSpecType.Integer, ParameterValue.FromDouble(d)),
                { Name: "IFCLENGTHMEASURE" or "IFCPOSITIVELENGTHMEASURE", Args: [double d] } => (ParameterSpecType.Length, ParameterValue.FromDouble(d * s)),
                { Name: "IFCAREAMEASURE", Args: [double d] } => (ParameterSpecType.Area, ParameterValue.FromDouble(d * s * s)),
                { Name: "IFCVOLUMEMEASURE", Args: [double d] } => (ParameterSpecType.Volume, ParameterValue.FromDouble(d * s * s * s)),
                { Name: "IFCPLANEANGLEMEASURE", Args: [double d] } => (ParameterSpecType.Angle, ParameterValue.FromDouble(d * 180 / Math.PI)),
                { Name: "IFCMASSMEASURE", Args: [double d] } => (ParameterSpecType.Mass, ParameterValue.FromDouble(d)),
                { Name: "IFCMASSDENSITYMEASURE", Args: [double d] } => (ParameterSpecType.Density, ParameterValue.FromDouble(d)),
                { Name: "IFCFORCEMEASURE", Args: [double d] } => (ParameterSpecType.Force, ParameterValue.FromDouble(d)),
                { Name: "IFCIDENTIFIER", Args: [string id] } => (ParameterSpecType.ElementReference, ParameterValue.FromElementId(id)),
                { Args: [string text] } => (ParameterSpecType.Text, ParameterValue.FromString(text)),
                { Args: [double d] } => (ParameterSpecType.Number, ParameterValue.FromDouble(d)),
                _ => null
            };
            if (parsed is not { } p) continue;
            var definition = System.Guid.TryParse(Str(prop, 1), out var guid) && guid != System.Guid.Empty
                ? registry.Register(new ParameterDefinition { Guid = guid, Name = name.Trim(), SpecType = p.Spec, StorageType = ParameterSpecs.StorageFor(p.Spec) })
                : null;
            definition ??= registry.Define(name, p.Spec);
            if (element.SetUserParameter(definition, p.Value)) count++;
        }
        return count;
    }

    private static string? MaterialName(ImportContext ctx, StepEntity? m)
    {
        for (int guard = 0; m is not null && guard < 6; guard++)
        {
            switch (m.Type)
            {
                case "IFCMATERIAL": return Str(m, 0);
                case "IFCMATERIALLAYERSETUSAGE": m = ctx.Get(m.Args.ElementAtOrDefault(0)); break;
                case "IFCMATERIALLAYERSET": m = ctx.Get((m.Args.ElementAtOrDefault(0) as List<object?>)?.FirstOrDefault()); break;
                case "IFCMATERIALLAYER": m = ctx.Get(m.Args.ElementAtOrDefault(0)); break;
                case "IFCMATERIALLIST": m = ctx.Get((m.Args.ElementAtOrDefault(0) as List<object?>)?.FirstOrDefault()); break;
                case "IFCMATERIALCONSTITUENTSET": m = ctx.Get((m.Args.ElementAtOrDefault(2) as List<object?>)?.FirstOrDefault()); break;
                case "IFCMATERIALCONSTITUENT": m = ctx.Get(m.Args.ElementAtOrDefault(2)); break;
                case "IFCMATERIALPROFILESETUSAGE": m = ctx.Get(m.Args.ElementAtOrDefault(0)); break;
                case "IFCMATERIALPROFILESET": m = ctx.Get((m.Args.ElementAtOrDefault(2) as List<object?>)?.FirstOrDefault()); break;
                case "IFCMATERIALPROFILE": m = ctx.Get(m.Args.ElementAtOrDefault(2)); break;
                default: return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Слои IfcMaterialLayerSet (через IfcMaterialLayerSetUsage или напрямую): название (IFC4 — имя слоя, иначе материал) и
    /// толщина в единицах файла. При направлении .NEGATIVE. порядок обращается — в сцене первый слой всегда у грани +Z / низа.
    /// </summary>
    private static List<(string Name, double Thickness)>? ReadLayerSet(ImportContext ctx, StepEntity? m)
    {
        bool reverse = false;
        if (m?.Type == "IFCMATERIALLAYERSETUSAGE")
        {
            reverse = Enum(m, 2) == "NEGATIVE";
            m = ctx.Get(m.Args.ElementAtOrDefault(0));
        }
        if (m?.Type != "IFCMATERIALLAYERSET") return null;
        var layers = new List<(string Name, double Thickness)>();
        foreach (var r in m.Args.ElementAtOrDefault(0) as List<object?> ?? [])
        {
            if (ctx.Get(r) is not { Type: "IFCMATERIALLAYER" } layer) continue;
            string name = Str(layer, 3) is { Length: > 0 } own ? own : ctx.Get(layer.Args.ElementAtOrDefault(0)) is { Type: "IFCMATERIAL" } material ? Str(material, 0) ?? "Слой" : "Слой";
            double thickness = Num(layer, 1) ?? 0;
            if (thickness > 0) layers.Add((name, thickness));
        }
        if (reverse) layers.Reverse();
        return layers;
    }

    /// <summary>Слои первого IfcMaterialLayerSetUsage (или набора) файла — для проверки разбора пирогов чужих программ.</summary>
    public static List<(string Name, double Thickness)> FirstLayerSet(string text)
    {
        var (entities, _) = ParseStep(text);
        var ctx = new ImportContext { E = entities };
        var set = entities.Values.FirstOrDefault(e => e.Type == "IFCMATERIALLAYERSETUSAGE") ?? entities.Values.FirstOrDefault(e => e.Type == "IFCMATERIALLAYERSET");
        return ReadLayerSet(ctx, set) ?? [];
    }

    /// <summary>Пирог элемента: толщины в мм (×единицы файла), назначение/плотность/цвет — из AIA_Parameters или по названию.</summary>
    private static List<MaterialLayer> BuildLayers(List<(string Name, double Thickness)> set, Dictionary<string, object?>? props, double scale)
    {
        string[] Split(string key) => props?.GetValueOrDefault(key) is string s ? s.Split(';') : [];
        var roles = Split("LayerRoles");
        var densities = Split("LayerDensities");
        var colors = Split("LayerColors");
        var result = new List<MaterialLayer>();
        for (int i = 0; i < set.Count; i++)
        {
            var (name, thickness) = set[i];
            var role = i < roles.Length ? MaterialLayers.ParseRole(roles[i]) : MaterialLayers.InferRole(name);
            double density = i < densities.Length && double.TryParse(densities[i], NumberStyles.Float, Inv, out var dens) ? dens : MaterialLayers.DefaultDensity(role, name);
            string color = i < colors.Length && colors[i].StartsWith('#') ? colors[i] : MaterialLayers.DefaultColor(role, name);
            result.Add(new MaterialLayer(name, Math.Round(thickness * scale * 1000, 4), density, role, color));
        }
        return result;
    }

    /// <summary>Название материала Renga/Revit → материал сцены (бетон, кирпич, стекло, сталь, дерево).</summary>
    private static string NormalizeMaterial(string? raw, string type)
    {
        string m = (raw ?? string.Empty).ToLowerInvariant();
        if (m.Contains("бетон") || m.Contains("concrete") || m.Contains("жб") || m.Contains("ж/б")) return "concrete";
        if (m.Contains("кирпич") || m.Contains("brick") || m.Contains("masonry") || m.Contains("кладк")) return "brick";
        if (m.Contains("стекл") || m.Contains("glass") || m.Contains("glaz")) return "glass";
        if (m.Contains("стал") || m.Contains("steel") || m.Contains("metal") || m.Contains("металл")) return "steel";
        if (m.Contains("дерев") || m.Contains("wood") || m.Contains("timber") || m.Contains("брус")) return "wood";
        return type switch
        {
            ElementTypes.Window or ElementTypes.CurtainWall => "glass",
            ElementTypes.Door => "wood",
            ElementTypes.Wall => "brick",
            _ => "concrete"
        };
    }

    /// <summary>Множитель единицы длины: IfcSIUnit (.MILLI. → 0.001) или IfcConversionBasedUnit (фут, дюйм).</summary>
    private static double LengthScale(ImportContext ctx)
    {
        var assignment = ctx.E.Values.FirstOrDefault(e => e.Type == "IFCUNITASSIGNMENT");
        var units = (assignment?.Args.ElementAtOrDefault(0) as List<object?> ?? []).Select(ctx.Get).Where(u => u is not null).ToList();
        foreach (var u in units)
        {
            if (u!.Type == "IFCSIUNIT" && Enum(u, 1) == "LENGTHUNIT") return SiPrefix(Enum(u, 2));
            if (u.Type == "IFCCONVERSIONBASEDUNIT" && Enum(u, 1) == "LENGTHUNIT")
            {
                if (ctx.Get(u.Args.ElementAtOrDefault(3)) is { Type: "IFCMEASUREWITHUNIT" } mwu)
                {
                    double factor = D(mwu.Args.ElementAtOrDefault(0));
                    double baseScale = ctx.Get(mwu.Args.ElementAtOrDefault(1)) is { Type: "IFCSIUNIT" } si ? SiPrefix(Enum(si, 2)) : 1;
                    if (factor > 0) return factor * baseScale;
                }
                return (Str(u, 2) ?? string.Empty).ToUpperInvariant() switch { "FOOT" => 0.3048, "INCH" => 0.0254, _ => 1 };
            }
        }
        return 1;
    }

    private static double SiPrefix(string? prefix) => prefix switch
    {
        "MILLI" => 0.001,
        "CENTI" => 0.01,
        "DECI" => 0.1,
        "KILO" => 1000,
        _ => 1
    };

    // ───────────── Размещения ─────────────

    private static Vec3 Point3(ImportContext ctx, StepEntity? p)
    {
        if (p?.Args.ElementAtOrDefault(0) is not List<object?> c) return default;
        return new Vec3(D(c.ElementAtOrDefault(0)), D(c.ElementAtOrDefault(1)), D(c.ElementAtOrDefault(2))) * ctx.Scale;
    }

    private static Vec3? Direction3(ImportContext ctx, StepEntity? d)
    {
        if (d?.Args.ElementAtOrDefault(0) is not List<object?> c) return null;
        return new Vec3(D(c.ElementAtOrDefault(0)), D(c.ElementAtOrDefault(1)), D(c.ElementAtOrDefault(2)));
    }

    /// <summary>IfcAxis2Placement3D / 2D в виде системы координат (относительной).</summary>
    private static Frame Axis2(ImportContext ctx, StepEntity? a)
    {
        if (a is null) return Frame.Identity;
        var origin = Point3(ctx, ctx.Get(a.Args.ElementAtOrDefault(0)));
        if (a.Type == "IFCAXIS2PLACEMENT2D")
            return Frame.FromAxes(origin, null, Direction3(ctx, ctx.Get(a.Args.ElementAtOrDefault(1))));
        return Frame.FromAxes(origin, Direction3(ctx, ctx.Get(a.Args.ElementAtOrDefault(1))), Direction3(ctx, ctx.Get(a.Args.ElementAtOrDefault(2))));
    }

    /// <summary>Мировая система IfcLocalPlacement с учётом всей цепочки PlacementRelTo.</summary>
    private static Frame Placement(ImportContext ctx, object? reference)
    {
        if (ctx.Get(reference) is not { Type: "IFCLOCALPLACEMENT" } lp) return Frame.Identity;
        if (ctx.Placements.TryGetValue(lp.Id, out var cached)) return cached;
        ctx.Placements[lp.Id] = Frame.Identity; // защита от циклов
        var parent = lp.Args.ElementAtOrDefault(0) is StepRef ? Placement(ctx, lp.Args[0]) : Frame.Identity;
        var frame = parent.Then(Axis2(ctx, ctx.Get(lp.Args.ElementAtOrDefault(1))));
        ctx.Placements[lp.Id] = frame;
        return frame;
    }

    // ───────────── Представления и профили ─────────────

    /// <summary>Твердотельные части представления элемента (Body; без Axis, FootPrint, Box — если есть тело).</summary>
    private static List<Solid> Representation(ImportContext ctx, StepEntity product)
    {
        var frame = Placement(ctx, product.Args.ElementAtOrDefault(5));
        var solids = new List<Solid>();
        if (ctx.Get(product.Args.ElementAtOrDefault(6)) is not { } shape) return solids;
        var reps = (shape.Args.ElementAtOrDefault(2) as List<object?> ?? []).Select(ctx.Get).Where(r => r is not null).ToList();
        var ordered = reps.OrderBy(r => Str(r!, 1) == "Body" ? 0 : Str(r!, 1) is "Axis" or "FootPrint" or "Box" ? 2 : 1).ToList();
        foreach (var rep in ordered)
        {
            if (Str(rep!, 1) is "Axis" or "FootPrint" or "Annotation") continue;
            foreach (var item in rep!.Args.ElementAtOrDefault(3) as List<object?> ?? [])
                CollectSolids(ctx, ctx.Get(item), frame, solids, 0);
            if (solids.Count > 0) break;
        }
        return solids;
    }

    private static void CollectSolids(ImportContext ctx, StepEntity? item, Frame frame, List<Solid> solids, int depth)
    {
        if (item is null || depth > 6) return;
        switch (item.Type)
        {
            case "IFCEXTRUDEDAREASOLID":
            case "IFCEXTRUDEDAREASOLIDTAPERED":
            {
                var profile = ReadProfile(ctx, ctx.Get(item.Args.ElementAtOrDefault(0)));
                if (profile is null) return;
                var position = frame.Then(Axis2(ctx, ctx.Get(item.Args.ElementAtOrDefault(1))));
                var dir = Direction3(ctx, ctx.Get(item.Args.ElementAtOrDefault(2))) ?? new Vec3(0, 0, 1);
                double depthValue = D(item.Args.ElementAtOrDefault(3)) * ctx.Scale;
                // Профиль лежит в плоскости XY системы тела; его собственное размещение (Origin/Axis) учитывается в контуре.
                solids.Add(new Solid
                {
                    Profile = profile,
                    Frame = position,
                    Direction = position.Direction(dir).Normalized,
                    Depth = depthValue
                });
                break;
            }
            case "IFCMAPPEDITEM":
            {
                if (ctx.Get(item.Args.ElementAtOrDefault(0)) is not { Type: "IFCREPRESENTATIONMAP" } map) return;
                var origin = Axis2(ctx, ctx.Get(map.Args.ElementAtOrDefault(0)));
                var target = Operator(ctx, ctx.Get(item.Args.ElementAtOrDefault(1)));
                var mapped = frame.Then(target).Then(origin);
                if (ctx.Get(map.Args.ElementAtOrDefault(1)) is { } rep)
                    foreach (var sub in rep.Args.ElementAtOrDefault(3) as List<object?> ?? [])
                        CollectSolids(ctx, ctx.Get(sub), mapped, solids, depth + 1);
                break;
            }
            case "IFCBOOLEANCLIPPINGRESULT":
            case "IFCBOOLEANRESULT":
                CollectSolids(ctx, ctx.Get(item.Args.ElementAtOrDefault(1)), frame, solids, depth + 1);
                break;
            case "IFCFACETEDBREP":
            case "IFCFACETEDBREPWITHVOIDS":
            case "IFCSHELLBASEDSURFACEMODEL":
            case "IFCFACEBASEDSURFACEMODEL":
            case "IFCTRIANGULATEDFACESET":
            case "IFCPOLYGONALFACESET":
            case "IFCBOUNDINGBOX":
            {
                var (points, normals) = MeshPoints(ctx, item, frame);
                if (points.Count > 0) solids.Add(new Solid { Mesh = points, FaceNormals = normals });
                break;
            }
        }
    }

    /// <summary>IfcCartesianTransformationOperator3D (MappingTarget) как система координат (масштаб — равномерный).</summary>
    private static Frame Operator(ImportContext ctx, StepEntity? op)
    {
        if (op is null) return Frame.Identity;
        var x = Direction3(ctx, ctx.Get(op.Args.ElementAtOrDefault(0)));
        var origin = Point3(ctx, ctx.Get(op.Args.ElementAtOrDefault(2)));
        var z = op.Args.Count > 4 ? Direction3(ctx, ctx.Get(op.Args[4])) : null;
        return Frame.FromAxes(origin, z, x);
    }

    private static (List<Vec3> Points, List<Vec3> Normals) MeshPoints(ImportContext ctx, StepEntity item, Frame frame)
    {
        var points = new List<Vec3>();
        var normals = new List<Vec3>();
        void Face(List<Vec3> loop)
        {
            points.AddRange(loop);
            if (loop.Count >= 3)
            {
                var n = (loop[1] - loop[0]).Cross(loop[2] - loop[0]);
                if (n.Length > 1e-12) normals.Add(n.Normalized);
            }
        }
        List<Vec3> Loop(StepEntity? polyLoop) =>
            (polyLoop?.Args.ElementAtOrDefault(0) as List<object?> ?? []).Select(p => frame.Point(Point3(ctx, ctx.Get(p)))).ToList();

        IEnumerable<StepEntity> Faces(StepEntity? shell) =>
            (shell?.Args.ElementAtOrDefault(0) as List<object?> ?? []).Select(ctx.Get).Where(f => f is not null)!;

        void Shell(StepEntity? shell)
        {
            foreach (var face in Faces(shell))
                foreach (var bound in face.Args.ElementAtOrDefault(0) as List<object?> ?? [])
                    if (ctx.Get(bound) is { } b && b.Type.StartsWith("IFCFACE", StringComparison.Ordinal) && b.Type.Contains("BOUND"))
                    {
                        Face(Loop(ctx.Get(b.Args.ElementAtOrDefault(0))));
                        break;
                    }
        }

        switch (item.Type)
        {
            case "IFCFACETEDBREP":
            case "IFCFACETEDBREPWITHVOIDS":
                Shell(ctx.Get(item.Args.ElementAtOrDefault(0)));
                break;
            case "IFCSHELLBASEDSURFACEMODEL":
            case "IFCFACEBASEDSURFACEMODEL":
                foreach (var s in item.Args.ElementAtOrDefault(0) as List<object?> ?? []) Shell(ctx.Get(s));
                break;
            case "IFCTRIANGULATEDFACESET":
            case "IFCPOLYGONALFACESET":
            {
                var coords = (ctx.Get(item.Args.ElementAtOrDefault(0))?.Args.ElementAtOrDefault(0) as List<object?> ?? [])
                    .OfType<List<object?>>().Select(c => frame.Point(new Vec3(D(c.ElementAtOrDefault(0)), D(c.ElementAtOrDefault(1)), D(c.ElementAtOrDefault(2))) * ctx.Scale)).ToList();
                if (item.Type == "IFCTRIANGULATEDFACESET" && item.Args.ElementAtOrDefault(3) is List<object?> tris)
                    foreach (var t in tris.OfType<List<object?>>())
                    {
                        var loop = t.Select(v => (int)D(v) - 1).Where(k => k >= 0 && k < coords.Count).Select(k => coords[k]).ToList();
                        Face(loop);
                    }
                else points.AddRange(coords);
                break;
            }
            case "IFCBOUNDINGBOX":
            {
                var corner = Point3(ctx, ctx.Get(item.Args.ElementAtOrDefault(0)));
                double x = D(item.Args.ElementAtOrDefault(1)) * ctx.Scale, y = D(item.Args.ElementAtOrDefault(2)) * ctx.Scale, z = D(item.Args.ElementAtOrDefault(3)) * ctx.Scale;
                for (int k = 0; k < 8; k++)
                    points.Add(frame.Point(corner + new Vec3((k & 1) * x, ((k >> 1) & 1) * y, ((k >> 2) & 1) * z)));
                break;
            }
        }
        return (points, normals);
    }

    private static Profile? ReadProfile(ImportContext ctx, StepEntity? p)
    {
        if (p is null) return null;
        var profile = new Profile();
        double S(int i) => D(p.Args.ElementAtOrDefault(i)) * ctx.Scale;
        void Position(int i)
        {
            if (ctx.Get(p.Args.ElementAtOrDefault(i)) is not { } pos) return;
            var f = Axis2(ctx, pos);
            profile.Origin = (f.Origin.X, f.Origin.Y);
            profile.Axis = (f.X.X, f.X.Y);
        }
        switch (p.Type)
        {
            case "IFCRECTANGLEPROFILEDEF":
                profile.Kind = ProfileKind.Rectangle;
                Position(2);
                profile.XDim = S(3);
                profile.YDim = S(4);
                break;
            case "IFCROUNDEDRECTANGLEPROFILEDEF":
                profile.Kind = ProfileKind.Rectangle;
                Position(2);
                profile.XDim = S(3);
                profile.YDim = S(4);
                break;
            case "IFCRECTANGLEHOLLOWPROFILEDEF":
                profile.Kind = ProfileKind.RectangleHollow;
                Position(2);
                profile.XDim = S(3);
                profile.YDim = S(4);
                profile.Wall = S(5);
                break;
            case "IFCCIRCLEPROFILEDEF":
                profile.Kind = ProfileKind.Circle;
                Position(2);
                profile.Radius = S(3);
                profile.XDim = profile.YDim = 2 * profile.Radius;
                break;
            case "IFCCIRCLEHOLLOWPROFILEDEF":
                profile.Kind = ProfileKind.CircleHollow;
                Position(2);
                profile.Radius = S(3);
                profile.Wall = S(4);
                profile.XDim = profile.YDim = 2 * profile.Radius;
                break;
            case "IFCELLIPSEPROFILEDEF":
                profile.Kind = ProfileKind.Ellipse;
                Position(2);
                profile.XDim = 2 * S(3);
                profile.YDim = 2 * S(4);
                break;
            case "IFCISHAPEPROFILEDEF":
            case "IFCASYMMETRICISHAPEPROFILEDEF":
                profile.Kind = ProfileKind.IShape;
                Position(2);
                profile.XDim = S(3);
                profile.YDim = S(4);
                profile.Web = S(5);
                profile.Flange = S(6);
                break;
            case "IFCARBITRARYCLOSEDPROFILEDEF":
            case "IFCARBITRARYPROFILEDEFWITHVOIDS":
            {
                profile.Kind = ProfileKind.Polygon;
                profile.Points = CurvePoints(ctx, ctx.Get(p.Args.ElementAtOrDefault(2)));
                if (profile.Points.Count > 1 && Math.Abs(profile.Points[0].U - profile.Points[^1].U) < 1e-9 && Math.Abs(profile.Points[0].V - profile.Points[^1].V) < 1e-9)
                    profile.Points.RemoveAt(profile.Points.Count - 1);
                if (profile.Points.Count < 3) return null;
                profile.XDim = profile.Width;
                profile.YDim = profile.Height;
                break;
            }
            case "IFCDERIVEDPROFILEDEF":
            {
                var parent = ReadProfile(ctx, ctx.Get(p.Args.ElementAtOrDefault(2)));
                if (parent is null) return null;
                var op = ctx.Get(p.Args.ElementAtOrDefault(3));
                if (op is not null)
                {
                    var x = Direction3(ctx, ctx.Get(op.Args.ElementAtOrDefault(0))) ?? new Vec3(1, 0, 0);
                    var o = Point3(ctx, ctx.Get(op.Args.ElementAtOrDefault(2)));
                    var ax = new Vec3(x.X, x.Y, 0).Normalized;
                    parent.Origin = (o.X + parent.Origin.X * ax.X - parent.Origin.Y * ax.Y, o.Y + parent.Origin.X * ax.Y + parent.Origin.Y * ax.X);
                    parent.Axis = (parent.Axis.X * ax.X - parent.Axis.Y * ax.Y, parent.Axis.X * ax.Y + parent.Axis.Y * ax.X);
                }
                return parent;
            }
            default:
            {
                // Прочие параметрические профили (L, T, U, C, Z…): габарит по первым двум размерам.
                Position(2);
                profile.Kind = ProfileKind.Unknown;
                profile.XDim = S(3);
                profile.YDim = S(4);
                if (profile.XDim <= 0 || profile.YDim <= 0) return null;
                break;
            }
        }
        return profile;
    }

    /// <summary>Точки кривой контура: IfcPolyline, IfcIndexedPolyCurve, IfcCompositeCurve (полилинейные сегменты).</summary>
    private static List<(double U, double V)> CurvePoints(ImportContext ctx, StepEntity? curve)
    {
        var pts = new List<(double U, double V)>();
        if (curve is null) return pts;
        switch (curve.Type)
        {
            case "IFCPOLYLINE":
                foreach (var r in curve.Args.ElementAtOrDefault(0) as List<object?> ?? [])
                {
                    var p = Point3(ctx, ctx.Get(r));
                    pts.Add((p.X, p.Y));
                }
                break;
            case "IFCINDEXEDPOLYCURVE":
            {
                var list = ctx.Get(curve.Args.ElementAtOrDefault(0))?.Args.ElementAtOrDefault(0) as List<object?> ?? [];
                foreach (var c in list.OfType<List<object?>>())
                    pts.Add((D(c.ElementAtOrDefault(0)) * ctx.Scale, D(c.ElementAtOrDefault(1)) * ctx.Scale));
                break;
            }
            case "IFCCOMPOSITECURVE":
                foreach (var seg in curve.Args.ElementAtOrDefault(0) as List<object?> ?? [])
                {
                    if (ctx.Get(seg) is not { } s) continue;
                    var sub = CurvePoints(ctx, ctx.Get(s.Args.ElementAtOrDefault(2)));
                    foreach (var p in sub)
                        if (pts.Count == 0 || Math.Abs(pts[^1].U - p.U) > 1e-9 || Math.Abs(pts[^1].V - p.V) > 1e-9) pts.Add(p);
                }
                break;
            case "IFCTRIMMEDCURVE":
                return CurvePoints(ctx, ctx.Get(curve.Args.ElementAtOrDefault(0)));
        }
        return pts;
    }

    // ───────────── Элемент сцены из геометрии ─────────────

    /// <summary>IFC (X, Y, Z) → сцена (x = X, y = Z, z = −Y).</summary>
    private static (double X, double Y, double Z) ToScene(Vec3 p) => (p.X, p.Z, -p.Y);

    /// <summary>Поворот в плане (градусы сцены) по горизонтальному направлению IFC.</summary>
    private static double Angle(Vec3 dir) => Math.Atan2(dir.Y, dir.X) * 180 / Math.PI;

    /// <summary>
    /// Положение, габариты, поворот и профиль элемента по его телу: вертикальная экструзия (стены, колонны, плиты),
    /// горизонтальная (балки) или габарит сетки (Brep, поверхности).
    /// </summary>
    private static bool BuildGeometry(ArchitecturalElement el, List<Solid> solids, Dictionary<string, object?>? props, ImportContext ctx)
    {
        var main = solids.FirstOrDefault(s => s.IsExtrusion) ?? solids[0];
        double? pW = props?.GetValueOrDefault("Width") as double?, pH = props?.GetValueOrDefault("Height") as double?, pD = props?.GetValueOrDefault("Depth") as double?;
        double? pRot = props?.GetValueOrDefault("RotationY") as double?;
        if (props?.GetValueOrDefault("RoofShape") is string roofShape) el.RoofShape = RoofShapes.Normalize(roofShape);
        bool singleExtrusion = solids.Count(s => s.IsExtrusion) == 1 && solids.All(s => s.IsExtrusion);

        if (main.IsExtrusion && singleExtrusion && Math.Abs(main.Direction.Z) > 0.99)
        {
            var p = main.Profile!;
            var pc = main.Frame.Point(new Vec3(p.Origin.X, p.Origin.Y, 0));
            var u = main.Frame.Direction(new Vec3(p.Axis.X, p.Axis.Y, 0)).Normalized;
            double height = main.Depth * Math.Abs(main.Direction.Z);
            double baseZ = main.Direction.Z > 0 ? pc.Z : pc.Z - height;
            double rot = Angle(u);
            var (cx, _, cz) = ToScene(pc);
            // Профиль в плоскости XY тела: ось Y профиля после поворота может смотреть вниз (зеркальная система).
            double flip = main.Frame.Direction(new Vec3(-p.Axis.Y, p.Axis.X, 0)).Dot(new Vec3(-u.Y, u.X, 0)) < 0 ? -1 : 1;

            el.SetRotationY(Round(rot));
            switch (p.Kind)
            {
                case ProfileKind.Circle:
                    el.ProfileType = ProfileTypes.Circle;
                    el.SetSize(Round(p.Width), Round(height), Round(p.Height));
                    break;
                case ProfileKind.Ellipse:
                    el.ProfileType = ProfileTypes.Circle;
                    el.SetSize(Round(p.XDim), Round(height), Round(p.YDim));
                    break;
                case ProfileKind.CircleHollow:
                    el.ProfileType = ProfileTypes.Pipe;
                    el.ProfileWallThickness = Round(p.Wall);
                    el.SetSize(Round(p.Width), Round(height), Round(p.Height));
                    break;
                case ProfileKind.IShape:
                    el.ProfileType = ProfileTypes.IBeam;
                    el.ProfileWallThickness = Round(p.Flange);
                    el.ProfileWebThickness = Round(p.Web);
                    el.SetSize(Round(p.XDim), Round(height), Round(p.YDim));
                    break;
                case ProfileKind.RectangleHollow:
                    if (el.Type == ElementTypes.Wall) el.Type = ElementTypes.Core;
                    el.SetSize(Round(p.XDim), Round(height), Round(p.YDim));
                    break;
                case ProfileKind.Polygon:
                {
                    var local = p.Points.Select(q => (U: q.U, V: q.V * flip)).ToList();
                    // Стена Renga/Revit, заданная контуром-прямоугольником, — обычная стена W × D (ось по длинной стороне).
                    if (el.Type is ElementTypes.Wall or ElementTypes.Window or ElementTypes.Door && TryRectangle(local, out var rc, out var rw, out var rd, out var ra))
                    {
                        var world = main.Frame.Point(new Vec3(rc.U, rc.V * flip, 0));
                        (cx, _, cz) = ToScene(world);
                        el.SetRotationY(Round(rot + ra));
                        el.SetSize(Round(rw), Round(height), Round(rd));
                        break;
                    }
                    bool own = props?.ContainsKey("ElementId") == true;
                    double ou = 0, ov = 0;
                    if (!own)
                    {
                        // Чужой файл: элемент — в центре габарита контура (удобный Gizmo), контур — относительно него.
                        ou = (local.Min(q => q.U) + local.Max(q => q.U)) / 2;
                        ov = (local.Min(q => q.V) + local.Max(q => q.V)) / 2;
                        var world = main.Frame.Point(new Vec3(p.Origin.X + ou * p.Axis.X - ov * flip * p.Axis.Y, p.Origin.Y + ou * p.Axis.Y + ov * flip * p.Axis.X, 0));
                        (cx, _, cz) = ToScene(world);
                    }
                    el.Polygon2D = local.Select(q => new Point2D(Round(q.U - ou), Round(-(q.V - ov)))).ToList();
                    if (el.Type == ElementTypes.Column) el.ProfileType = ProfileTypes.Polygon;
                    el.SetSize(Round(p.Width), Round(height), Round(p.Height));
                    break;
                }
                default:
                    el.SetSize(Round(p.XDim), Round(height), Round(p.YDim));
                    break;
            }
            el.SetPosition(Round(cx), Round(baseZ + el.Height / 2), Round(cz));
            if (el.Type == ElementTypes.Roof && props?.ContainsKey("RoofShape") != true) el.RoofShape = RoofShapes.Flat;
            ApplyOwnSizes(el, pW, pH, pD);
            return true;
        }

        if (main.IsExtrusion && singleExtrusion && Math.Abs(main.Direction.Z) < 0.02)
        {
            // Горизонтальная экструзия — балка (стержень вдоль своей оси): сечение D (поперёк) × H (по вертикали).
            var p = main.Profile!;
            var start = main.Frame.Point(new Vec3(p.Origin.X, p.Origin.Y, 0));
            var u = main.Frame.Direction(new Vec3(p.Axis.X, p.Axis.Y, 0)).Normalized;
            bool uVertical = Math.Abs(u.Z) > 0.7;
            double across = uVertical ? p.Height : p.Width, vertical = uVertical ? p.Width : p.Height;
            var center = start + main.Direction * (main.Depth / 2);
            var (cx, cy, cz) = ToScene(center);
            el.SetRotationY(Round(Angle(main.Direction)));
            el.SetSize(Round(main.Depth), Round(vertical), Round(across));
            el.SetPosition(Round(cx), Round(cy), Round(cz));
            switch (p.Kind)
            {
                case ProfileKind.Circle:
                case ProfileKind.Ellipse:
                    el.ProfileType = ProfileTypes.Circle;
                    break;
                case ProfileKind.CircleHollow:
                    el.ProfileType = ProfileTypes.Pipe;
                    el.ProfileWallThickness = Round(p.Wall);
                    break;
                case ProfileKind.IShape:
                    el.ProfileType = ProfileTypes.IBeam;
                    el.ProfileWallThickness = Round(p.Flange);
                    el.ProfileWebThickness = Round(p.Web);
                    break;
                default:
                    el.ProfileType = ProfileTypes.Rectangle;
                    break;
            }
            if (el.Type is not (ElementTypes.Beam or ElementTypes.Truss or ElementTypes.Diagrid))
            {
                // Горизонтально выдавленная стена/плита: габарит в системе её оси.
                ApplyBox(el, solids, Angle(main.Direction));
            }
            ApplyOwnSizes(el, pW, pH, pD);
            return true;
        }

        // Сетка, несколько тел или наклонная экструзия — габарит в системе поворота элемента.
        ApplyBox(el, solids, pRot ?? 0);
        if (el.Type == ElementTypes.Roof && props?.ContainsKey("RoofShape") != true)
            el.RoofShape = RoofFromNormals(solids.SelectMany(s => s.FaceNormals ?? []).ToList());
        ApplyOwnSizes(el, pW, pH, pD);
        return el.Width > 1e-4 && el.Height > 1e-4;
    }

    /// <summary>Габарит всех тел в системе, повёрнутой на angle (градусы сцены).</summary>
    private static void ApplyBox(ArchitecturalElement el, List<Solid> solids, double angle)
    {
        var pts = solids.SelectMany(s => s.Corners()).ToList();
        if (pts.Count == 0) return;
        double a = angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
        foreach (var p in pts)
        {
            double u = p.X * c + p.Y * s, v = -p.X * s + p.Y * c;
            u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            z0 = Math.Min(z0, p.Z); z1 = Math.Max(z1, p.Z);
        }
        double cu = (u0 + u1) / 2, cv = (v0 + v1) / 2;
        var center = new Vec3(cu * c - cv * s, cu * s + cv * c, (z0 + z1) / 2);
        var (x, y, z) = ToScene(center);
        el.SetRotationY(Round(angle));
        el.SetSize(Round(u1 - u0), Round(z1 - z0), Round(v1 - v0));
        el.SetPosition(Round(x), Round(y), Round(z));
    }

    /// <summary>Собственный экспорт хранит точные габариты в AIA_Parameters (окна и двери — с глубиной проёма).</summary>
    private static void ApplyOwnSizes(ArchitecturalElement el, double? w, double? h, double? d)
    {
        if (w is null || h is null || d is null) return;
        double cy = el.Y, baseY = el.BaseY;
        el.SetSize(Round(w.Value), Round(h.Value), Round(d.Value));
        // Отметка низа сохраняется (тело экспортируется от низа элемента).
        el.SetPosition(el.X, Math.Abs(h.Value - (el.TopY - el.BaseY)) < 1e-6 ? cy : Round(baseY + h.Value / 2), el.Z);
    }

    private static string RoofFromNormals(List<Vec3> normals)
    {
        var dirs = new HashSet<int>();
        foreach (var n in normals)
        {
            if (Math.Abs(n.Z) < 0.05 || Math.Abs(n.Z) > 0.995) continue;
            var m = n.Z < 0 ? n * -1 : n;
            dirs.Add((int)Math.Round(Math.Atan2(m.Y, m.X) * 180 / Math.PI / 5));
        }
        return dirs.Count switch { 0 => RoofShapes.Flat, 1 => RoofShapes.Shed, 2 => RoofShapes.Gable, _ => RoofShapes.Hip };
    }

    private static bool TryRectangle(List<(double U, double V)> pts, out (double U, double V) center, out double w, out double d, out double angle)
    {
        center = default;
        w = d = angle = 0;
        if (pts.Count != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            var a = pts[(i + 3) % 4];
            var b = pts[i];
            var c = pts[(i + 1) % 4];
            double ux = a.U - b.U, uy = a.V - b.V, vx = c.U - b.U, vy = c.V - b.V;
            double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
            if (lu < 1e-9 || lv < 1e-9 || Math.Abs((ux * vx + uy * vy) / (lu * lv)) > 1e-3) return false;
        }
        double e0 = Math.Sqrt(Math.Pow(pts[1].U - pts[0].U, 2) + Math.Pow(pts[1].V - pts[0].V, 2));
        double e1 = Math.Sqrt(Math.Pow(pts[2].U - pts[1].U, 2) + Math.Pow(pts[2].V - pts[1].V, 2));
        var (p, q) = e0 >= e1 ? (pts[0], pts[1]) : (pts[1], pts[2]);
        w = Math.Max(e0, e1);
        d = Math.Min(e0, e1);
        angle = Math.Atan2(q.V - p.V, q.U - p.U) * 180 / Math.PI;
        center = (pts.Average(x => x.U), pts.Average(x => x.V));
        return true;
    }

    /// <summary>
    /// Окно, дверь или сквозной проём в системе стены-хоста: ширина — вдоль стены, высота — по вертикали, положение —
    /// по центру габарита тела; глубина заполнения — из AIA_Parameters или по толщине стены.
    /// </summary>
    private static void FitToHost(ArchitecturalElement el, List<Solid> solids, ArchitecturalElement host, Dictionary<string, object?>? props)
    {
        var pts = solids.SelectMany(s => s.Corners()).ToList();
        if (pts.Count == 0) return;
        double a0 = double.MaxValue, a1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
        foreach (var p in pts)
        {
            var (x, y, z) = ToScene(p);
            var (lx, _) = ArchitecturalElement.RotateY(x - host.X, z - host.Z, -host.RotationY);
            a0 = Math.Min(a0, lx); a1 = Math.Max(a1, lx);
            y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        double along = (a0 + a1) / 2;
        var (wx, wz) = ArchitecturalElement.RotateY(along, 0, host.RotationY);
        double depth = props?.GetValueOrDefault("Depth") as double? ?? host.Depth + ParametricSolverService.OpeningClearance;
        double width = props?.GetValueOrDefault("Width") as double? ?? a1 - a0;
        double height = props?.GetValueOrDefault("Height") as double? ?? y1 - y0;
        el.HostElementId = host.Id;
        el.LevelIndex = host.LevelIndex;
        el.BuildingGroupId = host.BuildingGroupId;
        el.SetRotationY(host.RotationY);
        el.SetSize(Round(width), Round(height), Round(depth));
        el.SetPosition(Round(host.X + wx), Round(y0 + height / 2), Round(host.Z + wz));
    }

    /// <summary>Отверстие плиты: контур в системе профиля (прямоугольник или полигон), отметка и толщина — по плите-хосту.</summary>
    private static void FitSlabOpening(ArchitecturalElement el, List<Solid> solids, ArchitecturalElement host)
    {
        var main = solids.FirstOrDefault(s => s.IsExtrusion);
        if (main?.Profile is { } p && Math.Abs(main.Direction.Z) > 0.99)
        {
            var u = main.Frame.Direction(new Vec3(p.Axis.X, p.Axis.Y, 0)).Normalized;
            var pc = main.Frame.Point(new Vec3(p.Origin.X, p.Origin.Y, 0));
            var (cx, _, cz) = ToScene(pc);
            el.SetRotationY(Round(Angle(u)));
            if (p.Kind == ProfileKind.Polygon) el.Polygon2D = p.Points.Select(q => new Point2D(Round(q.U), Round(-q.V))).ToList();
            el.SetSize(Round(p.Width), Round(host.Height + ParametricSolverService.OpeningClearance), Round(p.Height));
            el.SetPosition(Round(cx), host.Y, Round(cz));
            return;
        }
        ApplyBox(el, solids, host.RotationY);
        el.SetSize(el.Width, Round(host.Height + ParametricSolverService.OpeningClearance), el.Depth);
        el.SetPosition(el.X, host.Y, el.Z);
    }
}
