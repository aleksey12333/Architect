using System.Text.Json;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services.AI;

/// <summary>
/// Параметрический компилятор чертежа. Вместо сотен поштучных элементов ИИ возвращает компактные правила здания,
/// считанные с конкретного чертежа (блок <c>blueprint</c>): оси с реальными шагами, уровни с отметками из разреза,
/// таблицу сечений, расстановки колонн и балок по осям, сегменты стен, контур плит/кровли и отверстия.
/// Компилятор за миллисекунды разворачивает их в полный набор элементов на всех этажах без пропущенных узлов.
/// Ни один размер не зашит в код: всё берётся из правил, а то, чего в правилах нет, не выдумывается
/// (недостающие отметки помечаются в отчёте).
/// <para>
/// Координаты чертежа: X — вправо от оси «1», Y — вверх по листу от оси «А» (в сцене это −Z). Точки задаются числами
/// <c>[x, y]</c> или ссылками на пересечение осей <c>["2", "Б"]</c> (со смещением <c>["2", "Б", dx, dy]</c>), строкой «2/Б».
/// Диапазоны: «1-6», «А-Г», узлы «1-6/А-Г», «all». Этажи — индексы с 0, «0-3», «all», имена уровней.
/// </para>
/// </summary>
public static class BlueprintCompilerService
{
    private const int MaxElements = 6000;
    private const double OpeningClearance = 0.06;

    private sealed class Level
    {
        public int Index;
        public string Name = string.Empty;
        public double Elevation;
        public double Height;
        public double SlabThickness;
        public double Top => Elevation + Height;
    }

    private sealed record Section(string Id, string Profile, double B, double H, double Wall, double Web, List<Point2D>? Polygon);

    private sealed class Context
    {
        public required AiSceneDocument Doc;
        public string Group = BuildingGroup.DefaultGroupId;
        public double Unit = 1;
        public readonly List<(string Label, double Pos)> XAxes = [];
        public readonly List<(string Label, double Pos)> ZAxes = [];
        public readonly List<Level> Levels = [];
        public readonly Dictionary<string, Section> Sections = new(StringComparer.OrdinalIgnoreCase);
        public List<(double X, double Z)>? Contour;
        public double ShiftX, ShiftZ;
        public readonly List<AiElementSpec> Out = [];
        public readonly Dictionary<int, string> CeilingIds = [];
        public readonly Dictionary<string, HashSet<int>> WallLevels = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> WallPieces = new(StringComparer.OrdinalIgnoreCase);
        public int Columns, Beams, Walls, Slabs, Openings, Roofs;
        public bool Truncated;

        public void Warn(string text) => Doc.ParseWarnings.Add("Чертёж: " + text);

        public bool Emit(AiElementSpec spec)
        {
            if (Out.Count >= MaxElements)
            {
                Truncated = true;
                return false;
            }
            spec.Group ??= Group;
            spec.Pinned = true;
            Out.Add(spec);
            return true;
        }
    }

    // ───────────────────────────── Точка входа ─────────────────────────────

    public static void Compile(JsonElement bp, AiSceneDocument doc)
    {
        var ctx = new Context { Doc = doc };
        ctx.Group = Str(bp, "group", "buildingGroup", "buildingName") ?? doc.Groups.FirstOrDefault()?.Id ?? BuildingGroup.DefaultGroupId;

        ReadUnits(bp, ctx);
        ReadGrid(bp, ctx);
        ReadLevels(bp, ctx);
        ReadSections(bp, ctx);
        ReadContour(bp, ctx);
        ComputeShift(ctx);
        EmitGridAxes(ctx);

        Foundation(bp, ctx);
        Columns(bp, ctx);
        Beams(bp, ctx);
        Walls(bp, ctx);
        Slabs(bp, ctx);
        Parapet(bp, ctx);
        Openings(bp, ctx);

        doc.Elements.AddRange(ctx.Out);
        if (doc.Groups.All(g => g.Id != ctx.Group))
            doc.Groups.Add(new AiGroupSpec { Id = ctx.Group, Purpose = Str(bp, "purpose") });

        doc.CompilerActions.Add(
            $"Компилятор чертежа «{ctx.Group}»: оси {ctx.XAxes.Count}×{ctx.ZAxes.Count}, уровней {ctx.Levels.Count}" +
            (ctx.Levels.Count > 0 ? $" (отметки {string.Join("; ", ctx.Levels.Select(l => StructuralAxesBuilder.ElevationText(l.Elevation)))})" : string.Empty) +
            $", сечений {ctx.Sections.Count} → колонн {ctx.Columns}, балок {ctx.Beams}, стен {ctx.Walls}, плит {ctx.Slabs}, кровель {ctx.Roofs}, проёмов {ctx.Openings}" +
            (Math.Abs(ctx.Unit - 0.001) < 1e-9 ? " · размеры чертежа в мм" : string.Empty));
        if (ctx.Truncated) ctx.Warn($"достигнут лимит {MaxElements} элементов — часть правил не развёрнута");
    }

    // ───────────────────────────── Единицы, оси, уровни, сечения, контур ─────────────────────────────

    private static void ReadUnits(JsonElement bp, Context ctx)
    {
        switch (Str(bp, "units", "unit")?.ToLowerInvariant())
        {
            case "mm" or "мм": ctx.Unit = 0.001; return;
            case "cm" or "см": ctx.Unit = 0.01; return;
            case "m" or "м": ctx.Unit = 1; return;
        }
        // Единицы не указаны: шаги осей и отметки больше 200 бывают только в миллиметрах.
        double max = 0;
        foreach (var name in new[] { "gridAxes", "grid", "axes", "levels", "storeys" })
            if (AiJsonExtractor.Prop(bp, name) is { } el) max = Math.Max(max, MaxNumber(el));
        if (max > 200)
        {
            ctx.Unit = 0.001;
            ctx.Warn("единицы не указаны — по величине размеров приняты миллиметры");
        }
    }

    private static double MaxNumber(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => Math.Abs(el.GetDouble()),
        JsonValueKind.Array => el.EnumerateArray().Select(MaxNumber).DefaultIfEmpty(0).Max(),
        JsonValueKind.Object => el.EnumerateObject().Select(p => MaxNumber(p.Value)).DefaultIfEmpty(0).Max(),
        _ => 0
    };

    /// <summary>Длина в метрах.</summary>
    private static double L(Context ctx, double v) => v * ctx.Unit;

    /// <summary>Размер сечения/толщина в метрах: значения больше 5 м в сечении бывают только в миллиметрах.</summary>
    private static double T(Context ctx, double v)
    {
        double m = v * ctx.Unit;
        return m > 5 ? m / 1000 : m;
    }

    private static void ReadGrid(JsonElement bp, Context ctx)
    {
        if (AiJsonExtractor.Prop(bp, "gridAxes", "grid", "axes") is not { } g) return;

        if (g.ValueKind == JsonValueKind.Array)
        {
            // [{label, direction: "x"|"z", pos}]
            foreach (var a in g.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                string dir = (Str(a, "direction", "dir", "type") ?? "x").ToLowerInvariant();
                double pos = L(ctx, Num(a, "pos", "position", "coordinate", "offset") ?? 0);
                string? label = Str(a, "label", "name", "tag");
                bool letter = dir is "z" or "y" or "letter" or "v" or "буквенная" || (label is not null && label.Length > 0 && char.IsLetter(label[0]));
                if (letter) ctx.ZAxes.Add((label ?? StructuralAxesBuilder.Letter(ctx.ZAxes.Count), pos));
                else ctx.XAxes.Add((label ?? (ctx.XAxes.Count + 1).ToString(), pos));
            }
        }
        else if (g.ValueKind == JsonValueKind.Object)
        {
            ReadAxisList(g, ctx, ctx.XAxes, numeric: true, ["x", "numeric", "digits", "u"], ["xSpacings", "xSteps", "spacingsX", "stepsX"]);
            ReadAxisList(g, ctx, ctx.ZAxes, numeric: false, ["z", "y", "letters", "v"], ["zSpacings", "ySpacings", "zSteps", "ySteps", "spacingsZ", "spacingsY"]);
        }

        // Оси одного направления — по возрастанию координаты (буквенные: «А» внизу листа).
        ctx.XAxes.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        ctx.ZAxes.Sort((a, b) => a.Pos.CompareTo(b.Pos));
    }

    private static void ReadAxisList(JsonElement g, Context ctx, List<(string Label, double Pos)> target, bool numeric, string[] listNames, string[] spacingNames)
    {
        string Label(int i) => numeric ? (i + 1).ToString() : StructuralAxesBuilder.Letter(i);

        if (AiJsonExtractor.Prop(g, listNames) is { ValueKind: JsonValueKind.Array } list)
        {
            int i = 0;
            foreach (var item in list.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case JsonValueKind.Number:
                        target.Add((Label(i), L(ctx, item.GetDouble())));
                        break;
                    case JsonValueKind.Object:
                        target.Add((Str(item, "label", "name", "tag") ?? Label(i), L(ctx, Num(item, "pos", "position", "coordinate", "offset") ?? 0)));
                        break;
                    case JsonValueKind.String when AiJsonExtractor.TryNumber(item.GetString(), out var v):
                        target.Add((Label(i), L(ctx, v)));
                        break;
                }
                i++;
            }
            return;
        }

        if (AiJsonExtractor.Prop(g, spacingNames) is { ValueKind: JsonValueKind.Array } spacings)
        {
            double pos = 0;
            var labels = AiJsonExtractor.Prop(g, numeric ? "xLabels" : "zLabels", numeric ? "numericLabels" : "letterLabels") is { ValueKind: JsonValueKind.Array } ls
                ? ls.EnumerateArray().Select(l => l.ToString()).ToList()
                : [];
            target.Add((labels.ElementAtOrDefault(0) ?? Label(0), 0));
            int i = 1;
            foreach (var s in spacings.EnumerateArray())
            {
                if (AiJsonExtractor.Number(s) is not { } step) continue;
                pos += L(ctx, step);
                target.Add((labels.ElementAtOrDefault(i) ?? Label(i), pos));
                i++;
            }
        }
    }

    private static void ReadLevels(JsonElement bp, Context ctx)
    {
        double defaultSlab = AiJsonExtractor.Prop(bp, "slabThickness", "slabT") is { } st && AiJsonExtractor.Number(st) is { } stv ? T(ctx, stv) : -1;
        var raw = new List<(string? Name, double? Elevation, double? Height, double? Slab)>();

        if (AiJsonExtractor.Prop(bp, "levels", "storeys", "floors") is { ValueKind: JsonValueKind.Array } levels)
        {
            foreach (var l in levels.EnumerateArray())
            {
                if (l.ValueKind == JsonValueKind.Number) raw.Add((null, L(ctx, l.GetDouble()), null, null));
                else if (l.ValueKind == JsonValueKind.Object)
                    raw.Add((Str(l, "name", "title", "label"),
                        Num(l, "elevation", "elev", "mark", "z", "level") is { } e ? L(ctx, e) : null,
                        Num(l, "height", "floorHeight", "storeyHeight", "h") is { } h ? L(ctx, h) : null,
                        Num(l, "slabThickness", "slab", "slabT") is { } s ? T(ctx, s) : null));
            }
        }
        else if (AiJsonExtractor.Prop(bp, "levelHeights", "floorHeights", "storeyHeights") is { ValueKind: JsonValueKind.Array } heights)
        {
            double elevation = Num(bp, "baseElevation", "groundElevation") is { } b ? L(ctx, b) : 0;
            foreach (var h in heights.EnumerateArray())
            {
                if (AiJsonExtractor.Number(h) is not { } hv) continue;
                raw.Add((null, elevation, L(ctx, hv), null));
                elevation += L(ctx, hv);
            }
        }

        if (raw.Count == 0)
        {
            double h = Num(bp, "height", "floorHeight") is { } fh ? L(ctx, fh) : 3.0;
            raw.Add((null, 0, h, null));
            if (Num(bp, "height", "floorHeight") is null)
                ctx.Warn("уровни и высоты этажей не заданы — принят один этаж высотой 3.0 м (уточните отметки по разрезу)");
        }

        // Недостающие отметки — накопительно по высотам.
        double running = raw[0].Elevation ?? 0;
        var resolved = new List<Level>();
        for (int i = 0; i < raw.Count; i++)
        {
            double elevation = raw[i].Elevation ?? running;
            resolved.Add(new Level { Name = raw[i].Name ?? $"Этаж {i + 1}", Elevation = elevation, Height = raw[i].Height ?? 0, SlabThickness = raw[i].Slab ?? defaultSlab });
            running = elevation + (raw[i].Height ?? 0);
        }
        resolved.Sort((a, b) => a.Elevation.CompareTo(b.Elevation));
        for (int i = 0; i < resolved.Count; i++)
        {
            var l = resolved[i];
            l.Index = i;
            if (l.Height <= 0)
            {
                if (i + 1 < resolved.Count) l.Height = resolved[i + 1].Elevation - l.Elevation;
                else if (i > 0) l.Height = resolved[i - 1].Height;
                else l.Height = 3.0;
                if (i + 1 >= resolved.Count) ctx.Warn($"высота верхнего уровня «{l.Name}» не задана — принята {l.Height:0.###} м");
            }
            if (l.SlabThickness < 0)
            {
                l.SlabThickness = 0.2;
                if (i == 0) ctx.Warn("толщина плит не указана — принята 200 мм");
            }
            ctx.Levels.Add(l);
        }
    }

    private static void ReadSections(JsonElement bp, Context ctx)
    {
        if (AiJsonExtractor.Prop(bp, "sections", "profiles", "sectionTable") is not { } s) return;
        if (s.ValueKind == JsonValueKind.Object)
            foreach (var p in s.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object) ctx.Sections[p.Name] = ParseSection(p.Name, p.Value, ctx);
                else if (p.Value.ValueKind == JsonValueKind.String) ctx.Sections[p.Name] = ParseSectionText(p.Name, p.Value.GetString()!, ctx);
            }
        else if (s.ValueKind == JsonValueKind.Array)
            foreach (var item in s.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && Str(item, "id", "name", "mark") is { } id)
                    ctx.Sections[id] = ParseSection(id, item, ctx);
    }

    private static Section ParseSection(string id, JsonElement el, Context ctx)
    {
        double? d = Num(el, "d", "diameter", "dia") is { } dv ? T(ctx, dv) : null;
        double b = Num(el, "b", "width", "w") is { } bv ? T(ctx, bv) : d ?? 0.4;
        double h = Num(el, "h", "height", "depth") is { } hv ? T(ctx, hv) : d ?? b;
        string profile = ProfileTypes.Normalize(Str(el, "profile", "type", "shape") ?? (d is not null ? ProfileTypes.Circle : null));
        double wall = Num(el, "tf", "t", "wallThickness", "flangeThickness") is { } tv ? T(ctx, tv) : 0;
        double web = Num(el, "tw", "webThickness") is { } wv ? T(ctx, wv) : 0;
        List<Point2D>? polygon = null;
        if (AiJsonExtractor.Prop(el, "points", "polygon", "outline") is { ValueKind: JsonValueKind.Array } pts)
        {
            polygon = pts.EnumerateArray().Select(AiJsonExtractor.Vector).Where(v => v is { Length: >= 2 })
                .Select(v => new Point2D(T(ctx, v![0]), -T(ctx, v[1]))).ToList();
            if (polygon.Count >= 3) profile = ProfileTypes.Polygon;
            else polygon = null;
        }
        return new Section(id, profile, b, h, wall, web, polygon);
    }

    /// <summary>Сечение строкой: «400x400», «Ø500», «труба 325x10», «I40», «двутавр 200x400».</summary>
    private static Section ParseSectionText(string id, string text, Context ctx)
    {
        var t = text.Trim().ToLowerInvariant();
        static double Parse(string s) => double.Parse(s.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
        string profile = t.Contains('ø') || t.Contains("d=") || t.Contains("круг") || t.Contains("circle") ? ProfileTypes.Circle
            : t.Contains("труб") || t.Contains("pipe") ? ProfileTypes.Pipe
            : t.Contains("двутавр") || t.StartsWith('i') || t.Contains("heb") || t.Contains("ipe") ? ProfileTypes.IBeam
            : ProfileTypes.Rectangle;

        // Размеры «A×B» важнее номеров марок («40Б1»); сортаментный двутавр без размеров — высота по номеру (см).
        var pair = System.Text.RegularExpressions.Regex.Match(t, @"(\d+(?:[.,]\d+)?)\s*[xх×*]\s*(\d+(?:[.,]\d+)?)");
        var numbers = pair.Success
            ? [Parse(pair.Groups[1].Value), Parse(pair.Groups[2].Value)]
            : System.Text.RegularExpressions.Regex.Matches(t, @"\d+(?:[.,]\d+)?").Select(m => Parse(m.Value)).ToList();
        if (profile == ProfileTypes.IBeam)
        {
            if (pair.Success)
            {
                double hh = T(ctx, Math.Max(numbers[0], numbers[1])), bb = T(ctx, Math.Min(numbers[0], numbers[1]));
                return new Section(id, profile, bb, hh, 0, 0, null);
            }
            var gost = System.Text.RegularExpressions.Regex.Match(t, @"(\d+)\s*[бкшb]");
            if (gost.Success)
            {
                double hh = Parse(gost.Groups[1].Value) / 100;
                return new Section(id, profile, hh * 0.5, hh, 0, 0, null);
            }
        }
        double a = numbers.Count > 0 ? T(ctx, numbers[0]) : 0.4;
        double b = numbers.Count > 1 ? T(ctx, numbers[1]) : a;
        return profile switch
        {
            ProfileTypes.Circle => new Section(id, profile, a, a, 0, 0, null),
            ProfileTypes.Pipe => new Section(id, profile, a, a, numbers.Count > 1 ? b : 0, 0, null),
            ProfileTypes.IBeam => new Section(id, profile, numbers.Count > 1 ? b : a * 0.5, a, 0, 0, null),
            _ => new Section(id, profile, a, b, 0, 0, null)
        };
    }

    private static void ReadContour(JsonElement bp, Context ctx)
    {
        if (AiJsonExtractor.Prop(bp, "contourPolygon", "contour", "outline", "footprint") is { ValueKind: JsonValueKind.Array } c)
        {
            var points = c.EnumerateArray().Select(p => Point(p, ctx)).Where(p => p is not null).Select(p => p!.Value).ToList();
            if (points.Count >= 3) ctx.Contour = points;
        }
        if (ctx.Contour is null && ctx.XAxes.Count >= 2 && ctx.ZAxes.Count >= 2)
        {
            // Контур по крайним осям с выносом на заданное расстояние (или на половину наибольшего сечения колонн).
            double offset = Num(bp, "contourOffset", "slabOffset", "edgeOffset") is { } o ? T(ctx, o)
                : ctx.Sections.Values.Select(s => Math.Max(s.B, s.H) / 2).DefaultIfEmpty(0).Max();
            double x0 = ctx.XAxes[0].Pos - offset, x1 = ctx.XAxes[^1].Pos + offset;
            double z0 = -ctx.ZAxes[^1].Pos - offset, z1 = -ctx.ZAxes[0].Pos + offset;
            ctx.Contour = [(x0, z1), (x1, z1), (x1, z0), (x0, z0)];
        }
    }

    /// <summary>Центр сетки осей (или контура) переносится в начало координат сцены.</summary>
    private static void ComputeShift(Context ctx)
    {
        var xs = ctx.XAxes.Select(a => a.Pos).ToList();
        var zs = ctx.ZAxes.Select(a => -a.Pos).ToList();
        if (ctx.Contour is { } contour)
        {
            if (xs.Count == 0) xs.AddRange(contour.Select(p => p.X));
            if (zs.Count == 0) zs.AddRange(contour.Select(p => p.Z));
        }
        if (xs.Count > 0) ctx.ShiftX = -(xs.Min() + xs.Max()) / 2;
        if (zs.Count > 0) ctx.ShiftZ = -(zs.Min() + zs.Max()) / 2;
    }

    private static void EmitGridAxes(Context ctx)
    {
        foreach (var (label, pos) in ctx.XAxes)
            ctx.Doc.GridAxes.Add(new StructuralGridAxis(label, StructuralGridAxis.AlongZ, Math.Round(pos + ctx.ShiftX, 4), ctx.Group));
        foreach (var (label, pos) in ctx.ZAxes)
            ctx.Doc.GridAxes.Add(new StructuralGridAxis(label, StructuralGridAxis.AlongX, Math.Round(-pos + ctx.ShiftZ, 4), ctx.Group));
    }

    // ───────────────────────────── Точки, диапазоны, этажи ─────────────────────────────

    private static double? AxisX(Context ctx, string label) =>
        ctx.XAxes.FirstOrDefault(a => string.Equals(a.Label, label.Trim(), StringComparison.OrdinalIgnoreCase)) is { Label: not null } x ? x.Pos : null;

    private static double? AxisZ(Context ctx, string label) =>
        ctx.ZAxes.FirstOrDefault(a => string.Equals(a.Label, label.Trim(), StringComparison.OrdinalIgnoreCase)) is { Label: not null } z ? -z.Pos : null;

    /// <summary>Точка плана (до переноса в центр): числа [x, y] чертежа, ссылка на пересечение осей или строка «2/Б».</summary>
    private static (double X, double Z)? Point(JsonElement p, Context ctx)
    {
        switch (p.ValueKind)
        {
            case JsonValueKind.String:
                return AxisPoint(p.GetString()!.Split('/', '\\', '×'), ctx, 0, 0);
            case JsonValueKind.Object:
                if (AiJsonExtractor.Prop(p, "axis", "axes", "node") is { } axisRef && Point(axisRef, ctx) is { } basePoint)
                    return (basePoint.X + L(ctx, Num(p, "dx") ?? 0), basePoint.Z - L(ctx, Num(p, "dy", "dz") ?? 0));
                if (Num(p, "x") is { } ox && (Num(p, "y", "z") is { } oy))
                    return (L(ctx, ox), -L(ctx, oy));
                return null;
            case JsonValueKind.Array:
                var items = p.EnumerateArray().ToList();
                // Пара строк — ссылка на пересечение осей (марка цифровой оси «1» — тоже строка), иначе числа [x, y].
                if (items.Count >= 2 && items[0].ValueKind == JsonValueKind.String && items[1].ValueKind == JsonValueKind.String)
                {
                    double dx = items.Count > 2 && AiJsonExtractor.Number(items[2]) is { } a ? L(ctx, a) : 0;
                    double dy = items.Count > 3 && AiJsonExtractor.Number(items[3]) is { } b ? L(ctx, b) : 0;
                    if (AxisPoint([items[0].GetString()!, items[1].GetString()!], ctx, dx, dy) is { } axisPoint) return axisPoint;
                }
                if (items.Count >= 2 && AiJsonExtractor.Number(items[0]) is { } x && AiJsonExtractor.Number(items[1]) is { } y)
                    return (L(ctx, x), -L(ctx, y));
                return null;
            default:
                return null;
        }
    }

    private static (double X, double Z)? AxisPoint(IReadOnlyList<string> labels, Context ctx, double dx, double dy)
    {
        if (labels.Count < 2) return null;
        double? x = AxisX(ctx, labels[0]) ?? AxisX(ctx, labels[1]);
        double? z = AxisZ(ctx, labels[1]) ?? AxisZ(ctx, labels[0]);
        if (x is null || z is null) return null;
        return (x.Value + dx, z.Value - dy);
    }

    /// <summary>Подписи осей из диапазона «1-6», «А-Г», списка ["1","3"] или «all».</summary>
    private static List<(string Label, double Pos)> AxisRange(JsonElement? el, List<(string Label, double Pos)> axes)
    {
        if (el is null) return axes.ToList();
        var v = el.Value;
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().SelectMany(item => AxisRange(item, axes)).Distinct().ToList();
        string text = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : v.ToString();
        if (text.Length == 0 || text.Equals("all", StringComparison.OrdinalIgnoreCase) || text == "*" || text.Equals("все", StringComparison.OrdinalIgnoreCase))
            return axes.ToList();

        int Index(string label) => axes.FindIndex(a => string.Equals(a.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));
        int exact = Index(text);
        if (exact >= 0) return [axes[exact]];
        var parts = text.Split(['-', '–', '—', '…'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && Index(parts[0]) is var i0 && Index(parts[1]) is var i1 && i0 >= 0 && i1 >= 0)
            return axes.Skip(Math.Min(i0, i1)).Take(Math.Abs(i1 - i0) + 1).ToList();
        return text.Split(',', ';', ' ').Select(Index).Where(i => i >= 0).Select(i => axes[i]).ToList();
    }

    /// <summary>Узлы сетки: «all», «1-6/А-Г», объект {x:"1-6", z:"А-Г"} или список точек.</summary>
    private static List<((double X, double Z) P, string Tag)> Nodes(JsonElement? el, Context ctx)
    {
        var result = new List<((double, double), string)>();
        void Grid(JsonElement? xs, JsonElement? zs)
        {
            foreach (var x in AxisRange(xs, ctx.XAxes))
                foreach (var z in AxisRange(zs, ctx.ZAxes))
                    result.Add(((x.Pos, -z.Pos), $"{x.Label}/{z.Label}"));
        }

        if (el is null) { Grid(null, null); return result; }
        var v = el.Value;
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                var text = v.GetString()!.Trim();
                var parts = text.Split('/', '\\', '×');
                if (parts.Length == 2)
                {
                    using var xs = JsonDocument.Parse(JsonSerializer.Serialize(parts[0]));
                    using var zs = JsonDocument.Parse(JsonSerializer.Serialize(parts[1]));
                    // «Б/2» — буквы и цифры в любом порядке.
                    bool swapped = AxisRange(xs.RootElement, ctx.XAxes).Count == 0;
                    Grid(swapped ? zs.RootElement.Clone() : xs.RootElement.Clone(), swapped ? xs.RootElement.Clone() : zs.RootElement.Clone());
                }
                else Grid(null, null);
                break;
            case JsonValueKind.Object:
                Grid(AiJsonExtractor.Prop(v, "x", "numeric", "digits"), AiJsonExtractor.Prop(v, "z", "y", "letters"));
                break;
            case JsonValueKind.Array:
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString()!.Contains('-'))
                    {
                        result.AddRange(Nodes(item, ctx));
                        continue;
                    }
                    if (Point(item, ctx) is { } p) result.Add((p, item.ValueKind == JsonValueKind.Array ? string.Join("/", item.EnumerateArray().Take(2).Select(i => i.ToString())) : item.ToString()));
                }
                break;
        }
        return result;
    }

    /// <summary>Индексы этажей: «all», число, «0-3», массив чисел или имён уровней, «top».</summary>
    private static List<Level> LevelsOf(JsonElement? el, Context ctx)
    {
        if (el is null) return ctx.Levels.ToList();
        var v = el.Value;
        var result = new List<Level>();
        void AddText(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || text.Equals("all", StringComparison.OrdinalIgnoreCase) || text.Equals("все", StringComparison.OrdinalIgnoreCase))
            {
                result.AddRange(ctx.Levels);
                return;
            }
            if (text.Equals("top", StringComparison.OrdinalIgnoreCase) || text.Equals("roof", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.Levels.Count > 0) result.Add(ctx.Levels[^1]);
                return;
            }
            if (ctx.Levels.FirstOrDefault(l => string.Equals(l.Name, text, StringComparison.OrdinalIgnoreCase)) is { } named)
            {
                result.Add(named);
                return;
            }
            var parts = text.Split(['-', '–', '—'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b))
                result.AddRange(ctx.Levels.Where(l => l.Index >= Math.Min(a, b) && l.Index <= Math.Max(a, b)));
            else if (int.TryParse(text, out int single) && ctx.Levels.FirstOrDefault(l => l.Index == single) is { } one)
                result.Add(one);
        }

        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                if (ctx.Levels.FirstOrDefault(l => l.Index == (int)Math.Round(v.GetDouble())) is { } lv) result.Add(lv);
                break;
            case JsonValueKind.String:
                AddText(v.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number) { if (ctx.Levels.FirstOrDefault(l => l.Index == (int)Math.Round(item.GetDouble())) is { } li) result.Add(li); }
                    else if (item.ValueKind == JsonValueKind.String) AddText(item.GetString()!);
                }
                break;
        }
        return result.Distinct().OrderBy(l => l.Index).ToList();
    }

    private static Section? ResolveSection(JsonElement placement, Context ctx, string kind)
    {
        if (Str(placement, "section", "mark", "profileId") is { } id)
        {
            if (ctx.Sections.TryGetValue(id, out var s)) return s;
            if (System.Text.RegularExpressions.Regex.IsMatch(id, @"\d")) return ctx.Sections[id] = ParseSectionText(id, id, ctx);
            ctx.Warn($"сечение «{id}» не найдено в таблице сечений — {kind} пропущены");
            return null;
        }
        if (Num(placement, "b", "width", "d", "diameter", "h") is not null) return ParseSection($"{kind}-{ctx.Sections.Count + 1}", placement, ctx);
        ctx.Warn($"для расстановки «{kind}» не указано сечение — элементы пропущены");
        return null;
    }

    private static (double X, double Z) W(Context ctx, (double X, double Z) p) => (p.X + ctx.ShiftX, p.Z + ctx.ShiftZ);

    // ───────────────────────────── Колонны и балки ─────────────────────────────

    private static void Columns(JsonElement bp, Context ctx)
    {
        if (AiJsonExtractor.Prop(bp, "columnPlacements", "columns") is not { ValueKind: JsonValueKind.Array } placements) return;
        foreach (var pl in placements.EnumerateArray())
        {
            if (pl.ValueKind != JsonValueKind.Object || ResolveSection(pl, ctx, "колонны") is not { } section) continue;
            var nodes = Nodes(AiJsonExtractor.Prop(pl, "at", "nodes", "axes", "grid", "points"), ctx);
            if (AiJsonExtractor.Prop(pl, "except", "exclude", "skip") is { } except)
            {
                var skip = Nodes(except, ctx).Select(n => n.P).ToList();
                nodes = nodes.Where(n => !skip.Any(s => Math.Abs(s.X - n.P.X) < 1e-4 && Math.Abs(s.Z - n.P.Z) < 1e-4)).ToList();
            }
            double rotation = Num(pl, "rotation", "angle") ?? 0;
            double? explicitHeight = Num(pl, "height") is { } hh ? L(ctx, hh) : null;
            string material = Str(pl, "material") ?? (section.Profile is ProfileTypes.IBeam or ProfileTypes.Pipe ? "steel" : "concrete");

            foreach (var level in LevelsOf(AiJsonExtractor.Prop(pl, "levels", "level", "floors"), ctx))
                foreach (var (p, tag) in nodes)
                {
                    var (x, z) = W(ctx, p);
                    double h = explicitHeight ?? Math.Max(0.3, level.Height - level.SlabThickness);
                    if (!ctx.Emit(new AiElementSpec
                    {
                        Type = ElementTypes.Column,
                        Name = $"Колонна {section.Id} · {tag} · {level.Name}",
                        Level = level.Index,
                        Position = [x, z],
                        Size = [section.B, h, section.H],
                        BaseY = level.Elevation,
                        RotationY = rotation,
                        Profile = section.Profile,
                        ProfileWallThickness = section.Wall > 0 ? section.Wall : null,
                        ProfileWebThickness = section.Web > 0 ? section.Web : null,
                        Polygon = section.Polygon?.Select(pt => pt.Clone()).ToList(),
                        Material = material
                    })) return;
                    ctx.Columns++;
                }
        }
    }

    private static void Beams(JsonElement bp, Context ctx)
    {
        if (AiJsonExtractor.Prop(bp, "beamPlacements", "beams") is not { ValueKind: JsonValueKind.Array } placements) return;
        foreach (var pl in placements.EnumerateArray())
        {
            if (pl.ValueKind != JsonValueKind.Object || ResolveSection(pl, ctx, "балки") is not { } section) continue;
            var segments = new List<((double X, double Z) A, (double X, double Z) B, string Tag)>();

            if (AiJsonExtractor.Prop(pl, "from", "start") is { } f && AiJsonExtractor.Prop(pl, "to", "end") is { } t)
            {
                if (Point(f, ctx) is { } a && Point(t, ctx) is { } b) segments.Add((a, b, $"{f}-{t}".Replace("\"", string.Empty)));
            }
            else
            {
                string along = (Str(pl, "along", "direction", "dir") ?? "x").ToLowerInvariant();
                bool alongX = along is "x" or "horizontal" or "numeric" or "u";
                bool continuous = AiJsonExtractor.Prop(pl, "continuous") is { ValueKind: JsonValueKind.True };
                // Балки вдоль X лежат на буквенных осях и перекрывают пролёты между цифровыми осями (и наоборот).
                var lines = AxisRange(AiJsonExtractor.Prop(pl, "axes", "lines", "on"), alongX ? ctx.ZAxes : ctx.XAxes);
                var span = AxisRange(AiJsonExtractor.Prop(pl, "span", "between", "range"), alongX ? ctx.XAxes : ctx.ZAxes).OrderBy(a => a.Pos).ToList();
                foreach (var line in lines)
                {
                    if (span.Count < 2) continue;
                    var stops = continuous ? [span[0], span[^1]] : span;
                    for (int i = 0; i + 1 < stops.Count; i++)
                    {
                        var a = alongX ? (stops[i].Pos, -line.Pos) : (line.Pos, -stops[i].Pos);
                        var b = alongX ? (stops[i + 1].Pos, -line.Pos) : (line.Pos, -stops[i + 1].Pos);
                        segments.Add((a, b, $"{line.Label}/{stops[i].Label}-{stops[i + 1].Label}"));
                    }
                }
            }

            string material = Str(pl, "material") ?? (section.Profile is ProfileTypes.IBeam or ProfileTypes.Pipe ? "steel" : "concrete");
            foreach (var level in LevelsOf(AiJsonExtractor.Prop(pl, "levels", "level", "floors"), ctx))
            {
                // Верх балки — под плитой перекрытия своего этажа.
                double top = level.Top - level.SlabThickness;
                foreach (var (a, b, tag) in segments)
                {
                    var wa = W(ctx, a);
                    var wb = W(ctx, b);
                    if (!ctx.Emit(new AiElementSpec
                    {
                        Type = ElementTypes.Beam,
                        Name = $"Балка {section.Id} · {tag} · {level.Name}",
                        Level = level.Index,
                        Start = wa,
                        End = wb,
                        Height = section.H,
                        Thickness = section.B,
                        BaseY = top - section.H,
                        Profile = section.Profile == ProfileTypes.Polygon ? ProfileTypes.Rectangle : section.Profile,
                        ProfileWallThickness = section.Wall > 0 ? section.Wall : null,
                        ProfileWebThickness = section.Web > 0 ? section.Web : null,
                        Material = material
                    })) return;
                    ctx.Beams++;
                }
            }
        }
    }

    // ───────────────────────────── Стены ─────────────────────────────

    private static void Walls(JsonElement bp, Context ctx)
    {
        if (AiJsonExtractor.Prop(bp, "wallSegments", "walls") is not { ValueKind: JsonValueKind.Array } segments) return;
        int n = 0;
        foreach (var seg in segments.EnumerateArray())
        {
            if (seg.ValueKind != JsonValueKind.Object) continue;
            n++;
            string id = Str(seg, "id", "mark", "name") ?? $"w{n}";
            var pieces = new List<((double X, double Z) A, (double X, double Z) B)>();
            if (AiJsonExtractor.Prop(seg, "polyline", "points", "path") is { ValueKind: JsonValueKind.Array } path)
            {
                var pts = path.EnumerateArray().Select(p => Point(p, ctx)).Where(p => p is not null).Select(p => p!.Value).ToList();
                for (int i = 0; i + 1 < pts.Count; i++) pieces.Add((pts[i], pts[i + 1]));
                if (AiJsonExtractor.Prop(seg, "closed") is { ValueKind: JsonValueKind.True } && pts.Count > 2) pieces.Add((pts[^1], pts[0]));
            }
            else if (AiJsonExtractor.Prop(seg, "from", "start") is { } f && AiJsonExtractor.Prop(seg, "to", "end") is { } t && Point(f, ctx) is { } a && Point(t, ctx) is { } b)
                pieces.Add((a, b));
            if (pieces.Count == 0)
            {
                ctx.Warn($"стена «{id}»: не удалось определить точки по осям");
                continue;
            }

            double? thickness = Num(seg, "thickness", "t", "width") is { } tv ? T(ctx, tv) : null;
            if (thickness is null) ctx.Warn($"стена «{id}»: толщина не указана — принята 0.2 м");
            double? explicitHeight = Num(seg, "height") is { } hv ? L(ctx, hv) : null;
            string type = ParametricSolverService.NormalizeType(Str(seg, "type") ?? ElementTypes.Wall, out _) is ElementTypes.CurtainWall ? ElementTypes.CurtainWall : ElementTypes.Wall;
            string material = Str(seg, "material") ?? (type == ElementTypes.CurtainWall ? "glass" : "concrete");

            var levels = LevelsOf(AiJsonExtractor.Prop(seg, "levels", "level", "floors"), ctx);
            ctx.WallLevels[id] = levels.Select(l => l.Index).ToHashSet();
            ctx.WallPieces[id] = pieces.Count;
            foreach (var level in levels)
                for (int i = 0; i < pieces.Count; i++)
                {
                    var (a, b) = pieces[i];
                    if (!ctx.Emit(new AiElementSpec
                    {
                        Id = pieces.Count == 1 ? $"bp:{id}@{level.Index}" : $"bp:{id}.{i + 1}@{level.Index}",
                        Type = type,
                        Name = $"{(type == ElementTypes.CurtainWall ? "Витраж" : "Стена")} {id}{(pieces.Count > 1 ? "." + (i + 1) : string.Empty)} · {level.Name}",
                        Level = level.Index,
                        Start = W(ctx, a),
                        End = W(ctx, b),
                        Height = explicitHeight ?? Math.Max(0.3, level.Height - level.SlabThickness),
                        Thickness = thickness ?? 0.2,
                        BaseY = level.Elevation,
                        Material = material
                    })) return;
                    ctx.Walls++;
                }
        }
    }

    // ───────────────────────────── Фундамент, плиты, кровля ─────────────────────────────

    private static void Foundation(JsonElement bp, Context ctx)
    {
        if (AiJsonExtractor.Prop(bp, "foundation") is not { ValueKind: JsonValueKind.Object } f || ctx.Levels.Count == 0) return;
        var polygon = PolygonOf(AiJsonExtractor.Prop(f, "polygon", "contour"), ctx);
        if (polygon is null) return;
        double offset = Num(f, "offset", "overhang") is { } o ? T(ctx, o) : 0;
        if (offset > 0) polygon = OffsetPolygon(polygon, offset);
        double t = Num(f, "thickness", "t", "height") is { } tv ? T(ctx, tv) : 0.5;
        EmitPlate(ctx, ElementTypes.Foundation, "Фундаментная плита", ctx.Levels[0], polygon, t, ctx.Levels[0].Elevation - t, "bp:foundation");
    }

    /// <summary>
    /// Плиты перекрытий строго по контуру проекта (по умолчанию — по одной на этаж, верх плиты = отметка следующего этажа).
    /// Верхняя плита при плоской кровле становится плоским покрытием (roof flat) — без подмены на двускатную и наоборот.
    /// </summary>
    private static void Slabs(JsonElement bp, Context ctx)
    {
        var roof = AiJsonExtractor.Prop(bp, "roof", "roofing", "cover");
        string? roofShape = roof is { ValueKind: JsonValueKind.Object } r ? RoofShapes.Normalize(Str(r, "type", "shape", "form"), RoofShapes.Flat) : null;
        var top = ctx.Levels.LastOrDefault();

        var plates = new List<(Level Level, List<(double X, double Z)> Polygon, double Thickness, string Type, string? Name)>();
        if (AiJsonExtractor.Prop(bp, "slabs", "floorSlabs", "plates") is { ValueKind: JsonValueKind.Array } slabs)
        {
            foreach (var s in slabs.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var polygon = PolygonOf(AiJsonExtractor.Prop(s, "polygon", "contour", "outline"), ctx);
                if (polygon is null)
                {
                    ctx.Warn("плита без контура и без осей — пропущена");
                    continue;
                }
                string type = ParametricSolverService.NormalizeType(Str(s, "type") ?? ElementTypes.Slab, out _);
                if (!ElementTypes.IsSlabHostType(type)) type = ElementTypes.Slab;
                foreach (var level in LevelsOf(AiJsonExtractor.Prop(s, "levels", "level", "floors"), ctx))
                {
                    double t = Num(s, "thickness", "t") is { } tv ? T(ctx, tv) : level.SlabThickness;
                    plates.Add((level, polygon, t, type, Str(s, "name")));
                }
            }
        }
        else if (ctx.Contour is { } contour)
        {
            foreach (var level in ctx.Levels)
                if (level.SlabThickness > 0) plates.Add((level, contour, level.SlabThickness, ElementTypes.Slab, null));
        }

        foreach (var (level, polygon, t, type, name) in plates)
        {
            bool isCover = level == top && roofShape == RoofShapes.Flat && type == ElementTypes.Slab;
            string id = $"bp:slab@{level.Index}" + (ctx.CeilingIds.ContainsKey(level.Index) ? $".{ctx.Slabs}" : string.Empty);
            if (isCover)
            {
                EmitPlate(ctx, ElementTypes.Roof, name ?? "Плоское покрытие", level, polygon, t, level.Top - t, id, RoofShapes.Flat);
                ctx.Roofs++;
            }
            else
            {
                EmitPlate(ctx, type, name ?? $"Плита перекрытия · {level.Name}", level, polygon, t, level.Top - t, id);
                ctx.Slabs++;
            }
            ctx.CeilingIds.TryAdd(level.Index, id);
        }

        // Скатная кровля (или плоская без плиты верхнего этажа) — над верхним перекрытием.
        if (roof is not { ValueKind: JsonValueKind.Object } roofEl || top is null) return;
        if (roofShape == RoofShapes.Flat && plates.Any(p => p.Level == top)) return;

        var roofPolygon = PolygonOf(AiJsonExtractor.Prop(roofEl, "polygon", "contour", "outline"), ctx);
        if (roofPolygon is null) return;
        double overhang = Num(roofEl, "overhang", "offset", "eaves") is { } ov ? T(ctx, ov) : 0;
        if (overhang > 0) roofPolygon = OffsetPolygon(roofPolygon, overhang);
        if (roofShape == RoofShapes.Flat)
        {
            double t = Num(roofEl, "thickness", "t") is { } tv ? T(ctx, tv) : top.SlabThickness;
            EmitPlate(ctx, ElementTypes.Roof, "Плоское покрытие", top, roofPolygon, t, top.Top - t, $"bp:roof", RoofShapes.Flat);
            ctx.CeilingIds.TryAdd(top.Index, "bp:roof");
        }
        else
        {
            double span = Math.Min(roofPolygon.Max(p => p.X) - roofPolygon.Min(p => p.X), roofPolygon.Max(p => p.Z) - roofPolygon.Min(p => p.Z));
            double rise;
            if (Num(roofEl, "rise", "height", "ridgeHeight") is { } rv) rise = L(ctx, rv);
            else if (Num(roofEl, "slope", "pitch", "angle") is { } deg) rise = Math.Tan(deg * Math.PI / 180) * span / (roofShape == RoofShapes.Shed ? 1 : 2);
            else
            {
                rise = span * 0.25;
                ctx.Warn($"высота конька кровли не указана — принята по уклону 1:2 ({rise:0.##} м)");
            }
            EmitPlate(ctx, ElementTypes.Roof, "Кровля", top, roofPolygon, Math.Max(0.1, rise), top.Top, "bp:roof", roofShape);
        }
        ctx.Roofs++;
    }

    /// <summary>
    /// Парапет плоской кровли ("roof": {"type": "flat", "parapet": {"height", "thickness"}}): стены по контуру покрытия,
    /// смещённому внутрь на половину толщины (наружная грань — по краю плиты), низ — на верху покрытия.
    /// </summary>
    private static void Parapet(JsonElement bp, Context ctx)
    {
        if (AiJsonExtractor.Prop(bp, "roof", "roofing", "cover") is not { ValueKind: JsonValueKind.Object } roof || ctx.Levels.Count == 0) return;
        if (AiJsonExtractor.Prop(roof, "parapet") is not { ValueKind: JsonValueKind.Object } p) return;
        if (RoofShapes.Normalize(Str(roof, "type", "shape", "form"), RoofShapes.Flat) != RoofShapes.Flat)
        {
            ctx.Warn("парапет задан у скатной кровли — не построен");
            return;
        }
        double h = Num(p, "height", "h") is { } hv ? T(ctx, hv) : 0;
        double t = Num(p, "thickness", "t") is { } tv ? T(ctx, tv) : 0;
        var polygon = PolygonOf(AiJsonExtractor.Prop(roof, "polygon", "contour", "outline"), ctx);
        if (h <= 0 || t <= 0 || polygon is null)
        {
            ctx.Warn("парапет: не заданы высота, толщина или контур покрытия — не построен");
            return;
        }
        var top = ctx.Levels[^1];
        var inner = OffsetPolygon(polygon, -t / 2);
        for (int i = 0; i < inner.Count; i++)
        {
            if (!ctx.Emit(new AiElementSpec
            {
                Id = $"bp:parapet.{i + 1}",
                Type = ElementTypes.Wall,
                Name = $"Парапет {i + 1}",
                Level = top.Index,
                Start = W(ctx, inner[i]),
                End = W(ctx, inner[(i + 1) % inner.Count]),
                Height = h,
                Thickness = t,
                BaseY = top.Top,
                Material = Str(p, "material") ?? "concrete"
            })) return;
            ctx.Walls++;
        }
    }

    private static void EmitPlate(Context ctx, string type, string name, Level level, List<(double X, double Z)> polygon, double thickness, double baseY, string id, string? roofShape = null)
    {
        var world = polygon.Select(p => W(ctx, p)).ToList();
        double minX = world.Min(p => p.X), maxX = world.Max(p => p.X), minZ = world.Min(p => p.Z), maxZ = world.Max(p => p.Z);
        double cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
        bool rectangle = world.Count == 4 && world.All(p => (Math.Abs(p.X - minX) < 1e-6 || Math.Abs(p.X - maxX) < 1e-6) && (Math.Abs(p.Z - minZ) < 1e-6 || Math.Abs(p.Z - maxZ) < 1e-6));
        ctx.Emit(new AiElementSpec
        {
            Id = id,
            Type = type,
            Name = name,
            Level = level.Index,
            Position = [cx, cz],
            Size = [maxX - minX, thickness, maxZ - minZ],
            Polygon = rectangle ? null : world.Select(p => new Point2D(Math.Round(p.X - cx, 4), Math.Round(p.Z - cz, 4))).ToList(),
            BaseY = baseY,
            RoofShape = roofShape,
            Material = type == ElementTypes.Roof && roofShape != RoofShapes.Flat ? "metal_roof" : "concrete"
        });
    }

    private static List<(double X, double Z)>? PolygonOf(JsonElement? el, Context ctx)
    {
        if (el is null || el.Value.ValueKind == JsonValueKind.String && el.Value.GetString() is "contour" or "outline" or "контур")
            return ctx.Contour;
        if (el.Value.ValueKind != JsonValueKind.Array) return ctx.Contour;
        var pts = el.Value.EnumerateArray().Select(p => Point(p, ctx)).Where(p => p is not null).Select(p => p!.Value).ToList();
        return pts.Count >= 3 ? pts : ctx.Contour;
    }

    /// <summary>Смещение контура наружу на d (свес кровли, вынос плиты): пересечение смещённых сторон.</summary>
    private static List<(double X, double Z)> OffsetPolygon(List<(double X, double Z)> poly, double d)
    {
        double area = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            area += a.X * b.Z - b.X * a.Z;
        }
        double sign = area > 0 ? 1 : -1;
        var result = new List<(double X, double Z)>(poly.Count);
        for (int i = 0; i < poly.Count; i++)
        {
            var prev = poly[(i - 1 + poly.Count) % poly.Count];
            var cur = poly[i];
            var next = poly[(i + 1) % poly.Count];
            var n1 = Normal(prev, cur, sign);
            var n2 = Normal(cur, next, sign);
            double bx = n1.X + n2.X, bz = n1.Z + n2.Z;
            double len = Math.Sqrt(bx * bx + bz * bz);
            if (len < 1e-9) { result.Add(cur); continue; }
            double cos = (n1.X * bx + n1.Z * bz) / len;
            double k = d / Math.Max(0.2, cos);
            result.Add((cur.X + bx / len * k, cur.Z + bz / len * k));
        }
        return result;

        static (double X, double Z) Normal((double X, double Z) a, (double X, double Z) b, double sign)
        {
            double dx = b.X - a.X, dz = b.Z - a.Z, l = Math.Sqrt(dx * dx + dz * dz);
            return l < 1e-9 ? (0, 0) : (sign * dz / l, -sign * dx / l);
        }
    }

    // ───────────────────────────── Проёмы и отверстия ─────────────────────────────

    private static void Openings(JsonElement bp, Context ctx)
    {
        if (AiJsonExtractor.Prop(bp, "openings", "holes", "voids") is not { ValueKind: JsonValueKind.Array } openings) return;
        int n = 0;
        foreach (var o in openings.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object) continue;
            n++;
            string type = ParametricSolverService.NormalizeType(Str(o, "type", "kind") ?? ElementTypes.SlabOpening, out _);
            string? name = Str(o, "name", "purpose", "label");

            if (type == ElementTypes.SlabOpening)
            {
                var outline = OpeningOutline(o, ctx);
                if (outline is null)
                {
                    ctx.Warn($"отверстие {name ?? n.ToString()}: не заданы положение и размер");
                    continue;
                }
                var world = outline.Select(p => W(ctx, p)).ToList();
                double minX = world.Min(p => p.X), maxX = world.Max(p => p.X), minZ = world.Min(p => p.Z), maxZ = world.Max(p => p.Z);
                double cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
                bool rect = world.Count == 4;
                // По умолчанию отверстие сквозное через все перекрытия (лестничная клетка, лифт, шахта).
                var levels = LevelsOf(AiJsonExtractor.Prop(o, "levels", "level", "floors"), ctx);
                foreach (var level in levels)
                {
                    if (!ctx.Emit(new AiElementSpec
                    {
                        Type = ElementTypes.SlabOpening,
                        Name = name ?? $"Отверстие {n}",
                        Level = level.Index,
                        Host = ctx.CeilingIds.GetValueOrDefault(level.Index),
                        Position = [cx, cz],
                        Size = [maxX - minX, level.SlabThickness + OpeningClearance, maxZ - minZ],
                        Polygon = rect ? null : world.Select(p => new Point2D(Math.Round(p.X - cx, 4), Math.Round(p.Z - cz, 4))).ToList(),
                        Material = "void"
                    })) return;
                    ctx.Openings++;
                }
                continue;
            }

            // Окно, дверь или сквозной проём стены.
            string? wall = Str(o, "wall", "host", "wallId");
            double? width = Num(o, "width", "w") is { } wv ? T(ctx, wv) : null;
            double? height = Num(o, "height", "h") is { } hv ? T(ctx, hv) : null;
            double? sill = Num(o, "sill", "sillHeight", "bottom") is { } sv ? T(ctx, sv) : null;
            double? offset = Num(o, "offset", "positionAlongWall") is { } ofv ? L(ctx, ofv) : null;
            var at = AiJsonExtractor.Prop(o, "at", "center", "position") is { } atEl ? Point(atEl, ctx) : null;
            var levelsFor = LevelsOf(AiJsonExtractor.Prop(o, "levels", "level", "floors"), ctx);
            if (wall is not null && ctx.WallLevels.TryGetValue(wall, out var wallLevels))
                levelsFor = levelsFor.Where(l => wallLevels.Contains(l.Index)).ToList();
            // Стена-полилиния: проём ставится в указанный участок ("segment", с 1), по умолчанию — в первый.
            string wallKey = wall ?? string.Empty;
            if (wall is not null && ctx.WallPieces.GetValueOrDefault(wall) > 1)
                wallKey = $"{wall}.{Math.Clamp((int)(Num(o, "segment", "piece") ?? 1), 1, ctx.WallPieces[wall])}";

            foreach (var level in levelsFor)
            {
                var spec = new AiElementSpec
                {
                    Type = type is ElementTypes.Window or ElementTypes.Door ? type : ElementTypes.WallOpening,
                    Name = name,
                    Level = level.Index,
                    Host = wall is null ? null : $"bp:{wallKey}@{level.Index}",
                    Offset = offset,
                    Width = width,
                    Height = height,
                    Sill = sill ?? (type == ElementTypes.Door ? 0 : null),
                    Position = at is { } p ? [W(ctx, p).X, W(ctx, p).Z] : null,
                    Material = type == ElementTypes.Door ? "wood" : type == ElementTypes.Window ? "glass" : "void"
                };
                if (!ctx.Emit(spec)) return;
                // Отметку проёма солвер считает от низа стены-хоста (подоконник); проём без стены примагнитится к ближайшей.
                spec.Pinned = false;
                ctx.Openings++;
            }
        }
    }

    /// <summary>Контур отверстия плиты: полигон, ячейка между осями «2-3/Б-В» или центр + размер.</summary>
    private static List<(double X, double Z)>? OpeningOutline(JsonElement o, Context ctx)
    {
        if (AiJsonExtractor.Prop(o, "polygon", "outline", "contour") is { ValueKind: JsonValueKind.Array } poly)
        {
            var pts = poly.EnumerateArray().Select(p => Point(p, ctx)).Where(p => p is not null).Select(p => p!.Value).ToList();
            if (pts.Count >= 3) return pts;
        }

        double inset = Num(o, "inset", "margin") is { } iv ? T(ctx, iv) : 0;
        if (AiJsonExtractor.Prop(o, "between", "bay", "cell") is { } bay)
        {
            var labels = bay.ValueKind == JsonValueKind.Array
                ? bay.EnumerateArray().Select(b => b.ToString()).ToList()
                : bay.ToString().Split('/', ',', ' ', '-').Where(s => s.Length > 0).ToList();
            var xs = labels.Select(l => AxisX(ctx, l)).Where(v => v is not null).Select(v => v!.Value).ToList();
            var zs = labels.Select(l => AxisZ(ctx, l)).Where(v => v is not null).Select(v => v!.Value).ToList();
            if (xs.Count >= 2 && zs.Count >= 2)
            {
                double x0 = xs.Min() + inset, x1 = xs.Max() - inset, z0 = zs.Min() + inset, z1 = zs.Max() - inset;
                return [(x0, z1), (x1, z1), (x1, z0), (x0, z0)];
            }
        }

        if (AiJsonExtractor.Prop(o, "at", "center", "position") is { } at && Point(at, ctx) is { } c)
        {
            var size = AiJsonExtractor.Vector(o, "size", "dimensions");
            double w = size is { Length: >= 1 } ? T(ctx, size[0]) : Num(o, "width", "w") is { } wv ? T(ctx, wv) : 0;
            double d = size is { Length: >= 2 } ? T(ctx, size[^1]) : Num(o, "depth", "length", "d") is { } dv ? T(ctx, dv) : w;
            if (Num(o, "diameter") is { } dia)
            {
                double r = T(ctx, dia) / 2;
                return Enumerable.Range(0, 24).Select(i => 2 * Math.PI * i / 24).Select(a => (c.X + Math.Cos(a) * r, c.Z + Math.Sin(a) * r)).ToList();
            }
            if (w <= 0 || d <= 0) return null;
            return [(c.X - w / 2, c.Z + d / 2), (c.X + w / 2, c.Z + d / 2), (c.X + w / 2, c.Z - d / 2), (c.X - w / 2, c.Z - d / 2)];
        }
        return null;
    }

    private static string? Str(JsonElement obj, params string[] names) => AiJsonExtractor.Str(obj, names);

    private static double? Num(JsonElement obj, params string[] names) => AiJsonExtractor.Num(obj, names);
}
