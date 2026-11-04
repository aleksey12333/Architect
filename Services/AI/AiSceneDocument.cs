using System.Globalization;
using System.Text;
using System.Text.Json;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services.AI;

/// <summary>Высокоуровневое описание элемента от нейросети. Все поля необязательны (частичное обновление в режиме modify).</summary>
public sealed class AiElementSpec
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Name { get; set; }
    public string? Group { get; set; }
    public int? Level { get; set; }
    public double[]? Position { get; set; }
    public double[]? Size { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? Depth { get; set; }
    public double? Thickness { get; set; }
    public double? RotationY { get; set; }
    public double? RotationX { get; set; }
    public double? RotationZ { get; set; }
    public (double X, double Z)? Start { get; set; }
    public (double X, double Z)? End { get; set; }
    public List<Point2D>? Polygon { get; set; }
    public string? Host { get; set; }
    public double? Offset { get; set; }
    public double? Sill { get; set; }
    public double? BaseY { get; set; }
    public int? RepeatLevels { get; set; }
    public double? LevelHeight { get; set; }
    public double? Twist { get; set; }
    public double? Taper { get; set; }
    public string? Material { get; set; }

    /// <summary>Форма сечения колонны/балки: rectangle, circle, pipe, i_beam, polygon.</summary>
    public string? Profile { get; set; }

    /// <summary>Толщина стенки трубы / полки двутавра, м.</summary>
    public double? ProfileWallThickness { get; set; }

    /// <summary>Толщина стенки двутавра, м.</summary>
    public double? ProfileWebThickness { get; set; }

    /// <summary>Форма кровли: flat, gable, hip, shed.</summary>
    public string? RoofShape { get; set; }

    /// <summary>Пирог конструкции (Конструктор КБ): слои снизу вверх / от грани +Z; null — не задан.</summary>
    public List<MaterialLayer>? Layers { get; set; }

    /// <summary>Готовый пирог по имени: floor, flat_roof, foundation, exterior_wall.</summary>
    public string? LayersPreset { get; set; }

    /// <summary>
    /// Точная отметка с чертежа (BlueprintCompilerService): авто-гравитация солвера элемент не передвигает.
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>Стрела дуги стены/витража/арки, м (волна 4).</summary>
    public double? ArcBulge { get; set; }

    /// <summary>Радиус скругления углов контура плиты/кровли/фундамента, м.</summary>
    public double? CornerRadius { get; set; }

    /// <summary>Стрела арочного верха витража, м.</summary>
    public double? ArchRise { get; set; }

    /// <summary>Углы реза торцов стены, градусы.</summary>
    public double? MiterStart { get; set; }
    public double? MiterEnd { get; set; }

    /// <summary>Цвет фасада «#RRGGBB».</summary>
    public string? ColorHex { get; set; }

    public bool HasShape => ArcBulge is not null || CornerRadius is not null || ArchRise is not null || MiterStart is not null || MiterEnd is not null || ColorHex is not null;

    /// <summary>ИИ явно задал отметку (третья координата position, y или baseY).</summary>
    public bool HasExplicitY => BaseY.HasValue || Position is { Length: >= 3 };
}

public sealed class AiGroupSpec
{
    public string Id { get; set; } = string.Empty;
    public string? Purpose { get; set; }
}

/// <summary>Разобранный JSON-ответ ИИ: режим, постройки, элементы, удаления и комментарий.</summary>
public sealed class AiSceneDocument
{
    public string? Mode { get; set; }
    public string? Message { get; set; }
    public List<AiGroupSpec> Groups { get; } = [];
    public List<AiElementSpec> Elements { get; } = [];
    public List<string> DeleteIds { get; } = [];
    public List<string> ParseWarnings { get; } = [];
    public bool WasRepaired { get; set; }
    public string RawJson { get; set; } = string.Empty;

    /// <summary>Координационные оси, считанные с чертежа (блок blueprint.gridAxes).</summary>
    public List<StructuralGridAxis> GridAxes { get; } = [];

    /// <summary>Тип исходного материала, определённый ИИ (structural / architectural / sketch).</summary>
    public string? SourceKind { get; set; }

    /// <summary>Отчёт компилятора чертежа: сколько осей, этажей и элементов развёрнуто из параметрических правил.</summary>
    public List<string> CompilerActions { get; } = [];
}

/// <summary>
/// Надёжный извлекатель JSON из любого ответа ИИ (```json-блоки, текст вокруг, комментарии, висячие запятые,
/// обрезанный по лимиту токенов ответ) и толерантный разбор схемы (регистр ключей, синонимы полей и типов).
/// </summary>
public static class AiJsonExtractor
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128
    };

    /// <summary>Быстрая проверка: содержит ли текст JSON сцены (для перехватчиков веб-моста).</summary>
    public static bool ContainsSceneJson(string? text) => !string.IsNullOrWhiteSpace(text) && TryExtract(text, out _);

    public static bool TryExtract(string text, out AiSceneDocument document)
    {
        document = null!;
        AiSceneDocument? best = null;
        // Рассуждающие модели (deepseek-r1 и т.п.): фигурные скобки внутри <think>…</think> не должны попадать в разбор.
        if (ReasoningText.HasReasoningTags(text))
        {
            var answer = ReasoningText.Separate(text).Answer;
            if (answer.Length > 0) text = answer;
        }
        foreach (var (candidate, repaired) in Candidates(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate, Options);
                var parsed = Parse(doc.RootElement);
                if (parsed is null) continue;
                parsed.WasRepaired = repaired;
                parsed.RawJson = candidate;
                if (best is null || parsed.Elements.Count + parsed.DeleteIds.Count >= best.Elements.Count + best.DeleteIds.Count)
                    best = parsed;
            }
            catch (JsonException)
            {
                // Кандидат не является корректным JSON — пробуем следующий.
            }
        }
        if (best is null) return false;
        document = best;
        return true;
    }

    // ───────────────────────────── Поиск кандидатов ─────────────────────────────

    private static IEnumerable<(string Json, bool Repaired)> Candidates(string text)
    {
        var seen = new HashSet<string>();

        // 1. Блоки кода ```json … ``` (и просто ``` … ```).
        int pos = 0;
        while ((pos = text.IndexOf("```", pos, StringComparison.Ordinal)) >= 0)
        {
            int lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0) break;
            int close = text.IndexOf("```", lineEnd, StringComparison.Ordinal);
            string block = close < 0 ? text[(lineEnd + 1)..] : text[(lineEnd + 1)..close];
            foreach (var c in BalancedObjects(block))
                if (seen.Add(c.Json)) yield return c;
            if (close < 0) break;
            pos = close + 3;
        }

        // 2. Сбалансированные объекты/массивы в произвольном тексте.
        foreach (var c in BalancedObjects(text))
            if (seen.Add(c.Json)) yield return c;
    }

    /// <summary>Находит JSON-объекты верхнего уровня; незакрытый хвост чинится закрытием скобок после последнего целого элемента.</summary>
    private static IEnumerable<(string Json, bool Repaired)> BalancedObjects(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOfAny(['{', '['], i);
            if (start < 0) yield break;

            var stack = new Stack<char>();
            bool inString = false, escape = false;
            int end = -1, safeEnd = -1;
            char[]? safeStack = null;

            for (int j = start; j < text.Length; j++)
            {
                char ch = text[j];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (ch == '\\') escape = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                switch (ch)
                {
                    case '"': inString = true; break;
                    case '{': stack.Push('}'); break;
                    case '[': stack.Push(']'); break;
                    case '}':
                    case ']':
                        if (stack.Count == 0 || stack.Peek() != ch) { j = text.Length; break; }
                        stack.Pop();
                        if (stack.Count == 0) { end = j; j = text.Length; break; }
                        if (stack.Peek() == ']') { safeEnd = j; safeStack = stack.ToArray(); }
                        break;
                }
            }

            if (end > start)
            {
                yield return (text[start..(end + 1)], false);
                i = end + 1;
            }
            else
            {
                if (safeEnd > start && safeStack is not null)
                {
                    var sb = new StringBuilder(text, start, safeEnd - start + 1, safeEnd - start + 16);
                    foreach (var closer in safeStack) sb.Append(closer);
                    yield return (sb.ToString(), true);
                }
                i = start + 1;
            }
        }
    }

    // ───────────────────────────── Разбор схемы ─────────────────────────────

    private static AiSceneDocument? Parse(JsonElement root)
    {
        var doc = new AiSceneDocument();
        if (root.ValueKind == JsonValueKind.Array)
        {
            ReadElements(root, doc, null);
            return doc.Elements.Count > 0 ? doc : null;
        }
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Обёртка {"scene": {...}} / {"result": {...}}.
        foreach (var wrapper in new[] { "scene", "result", "project", "data" })
            if (Prop(root, wrapper) is { ValueKind: JsonValueKind.Object } inner && (Prop(inner, "elements") is not null || Prop(inner, "buildings") is not null))
                return Parse(inner);

        doc.Mode = Str(root, "mode", "action");
        doc.Message = Str(root, "message", "comment", "summary", "description");
        doc.SourceKind = Str(root, "sourceKind", "source", "drawingType");

        // Компактные параметрические правила здания (оси, уровни, расстановки) — разворачиваются C#-компилятором чертежа.
        if (Prop(root, "blueprint", "structure", "parametric") is { ValueKind: JsonValueKind.Object } blueprint)
            BlueprintCompilerService.Compile(blueprint, doc);
        else if (Prop(root, "gridAxes", "columnPlacements", "beamPlacements", "wallSegments") is not null)
            BlueprintCompilerService.Compile(root, doc);

        if (Prop(root, "buildingGroups", "groups", "buildings") is { ValueKind: JsonValueKind.Array } groups)
        {
            foreach (var g in groups.EnumerateArray())
            {
                if (g.ValueKind == JsonValueKind.String)
                {
                    doc.Groups.Add(new AiGroupSpec { Id = g.GetString() ?? string.Empty });
                    continue;
                }
                if (g.ValueKind != JsonValueKind.Object) continue;
                var id = Str(g, "id", "name", "buildingGroupId") ?? $"Постройка {doc.Groups.Count + 1}";
                doc.Groups.Add(new AiGroupSpec { Id = id, Purpose = Str(g, "purpose", "kind", "type") });
                if (Prop(g, "elements") is { ValueKind: JsonValueKind.Array } nested) ReadElements(nested, doc, id);
            }
        }

        if (Prop(root, "elements", "items", "objects") is { ValueKind: JsonValueKind.Array } elements)
            ReadElements(elements, doc, null);
        foreach (var extra in new[] { "add", "updates", "modify" })
            if (Prop(root, extra) is { ValueKind: JsonValueKind.Array } more)
                ReadElements(more, doc, null);

        if (Prop(root, "delete", "deleteIds", "remove", "removeIds") is { ValueKind: JsonValueKind.Array } del)
            foreach (var d in del.EnumerateArray())
            {
                var id = d.ValueKind == JsonValueKind.String ? d.GetString() : d.ValueKind == JsonValueKind.Object ? Str(d, "id") : null;
                if (!string.IsNullOrWhiteSpace(id)) doc.DeleteIds.Add(id);
            }

        // Один элемент без обёртки: {"type": "wall", ...}.
        if (doc.Elements.Count == 0 && doc.DeleteIds.Count == 0 && Str(root, "type") is not null && Prop(root, "elements") is null)
            ReadElement(root, doc, null);

        return doc.Elements.Count > 0 || doc.DeleteIds.Count > 0 ? doc : null;
    }

    private static void ReadElements(JsonElement array, AiSceneDocument doc, string? group)
    {
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object) ReadElement(item, doc, group);
    }

    private static void ReadElement(JsonElement e, AiSceneDocument doc, string? group)
    {
        var spec = new AiElementSpec
        {
            Id = Str(e, "id", "elementId"),
            Type = Str(e, "type", "kind", "category"),
            Name = Str(e, "name", "title", "label"),
            Group = Str(e, "group", "buildingGroupId", "building", "buildingGroup") ?? group,
            Level = Int(e, "level", "levelIndex", "floor", "storey"),
            Position = Vector(e, "position", "pos", "center", "location"),
            Size = Vector(e, "size", "dimensions", "dims"),
            Width = Num(e, "width", "length", "w"),
            Height = Num(e, "height", "h"),
            Depth = Num(e, "depth", "d"),
            Thickness = Num(e, "thickness", "wallThickness"),
            Start = PointXZ(e, "start", "from", "p1", "a"),
            End = PointXZ(e, "end", "to", "p2", "b"),
            Polygon = Polygon(e, "polygon2D", "polygon", "outline", "contour", "points"),
            Host = Str(e, "host", "hostId", "hostElementId", "wall", "wallId"),
            Offset = Num(e, "offset", "offsetAlongWall", "positionAlongWall"),
            Sill = Num(e, "sill", "sillHeight", "elevation"),
            BaseY = Num(e, "baseY", "y", "bottom"),
            RepeatLevels = Int(e, "repeatLevels", "repeat", "floors", "levels"),
            LevelHeight = Num(e, "levelHeight", "floorHeight", "storeyHeight"),
            Twist = Num(e, "twistAnglePerFloor", "twist", "twistPerFloor"),
            Taper = Num(e, "taperScalePerFloor", "taper", "taperPerFloor"),
            Material = Str(e, "material"),
            Profile = Str(e, "profile", "profileType", "sectionType", "crossSection", "section"),
            ProfileWallThickness = Num(e, "flangeThickness", "tf", "pipeThickness", "tubeThickness", "profileThickness"),
            ProfileWebThickness = Num(e, "webThickness", "tw"),
            RoofShape = Str(e, "roofType", "roofShape", "roofForm", "shape"),
            // Волна 4: криволинейная форма и цвет фасада с фотографии.
            ArcBulge = Num(e, "arcBulge", "bulge", "sagitta", "arcRise"),
            CornerRadius = Num(e, "cornerRadius", "filletRadius", "roundCorners"),
            ArchRise = Num(e, "archRise", "archTop", "topArch"),
            MiterStart = Num(e, "miterStart", "joinStartDeg"),
            MiterEnd = Num(e, "miterEnd", "joinEndDeg"),
            ColorHex = Str(e, "colorHex", "facadeColor", "facadeHex")
        };

        // Пирог: имя готового набора или массив слоёв {name, thickness (мм), role, density}.
        if (Prop(e, "layers", "layerSet", "materialLayers") is { } layers)
        {
            if (layers.ValueKind == JsonValueKind.String) spec.LayersPreset = layers.GetString();
            else if (layers.ValueKind == JsonValueKind.Array)
            {
                var list = new List<MaterialLayer>();
                foreach (var l in layers.EnumerateArray())
                {
                    if (l.ValueKind != JsonValueKind.Object) continue;
                    string name = Str(l, "name", "material", "title") ?? "Слой";
                    double t = Num(l, "thickness", "thicknessMm", "t", "mm") ?? 0;
                    if (t > 0 && t < 2) t *= 1000; // толщина в метрах
                    if (t < 1) continue;
                    var role = MaterialLayers.ParseRole(Str(l, "role", "function", "purpose") ?? MaterialLayers.InferRole(name).ToString());
                    list.Add(new MaterialLayer(name, Math.Round(t, 1), Num(l, "density", "rho") ?? MaterialLayers.DefaultDensity(role, name), role));
                }
                if (list.Count > 0) spec.Layers = list;
            }
        }

        // Круглое сечение задано диаметром.
        if (Num(e, "diameter", "dia") is { } diameter)
        {
            spec.Width ??= diameter;
            spec.Depth ??= diameter;
            spec.Profile ??= ProfileTypes.Circle;
        }

        if (Prop(e, "rotation", "rot", "rotationY", "angle") is { } rot)
        {
            if (rot.ValueKind == JsonValueKind.Number) spec.RotationY = rot.GetDouble();
            else if (Vector(rot) is { } r)
            {
                if (r.Length == 1) spec.RotationY = r[0];
                else if (r.Length >= 3) { spec.RotationX = r[0]; spec.RotationY = r[1]; spec.RotationZ = r[2]; }
            }
            else if (rot.ValueKind == JsonValueKind.String && TryNumber(rot.GetString(), out var rv)) spec.RotationY = rv;
        }

        // x/z как отдельные поля.
        if (spec.Position is null && (Num(e, "x") is { } x) && (Num(e, "z") is { } z))
            spec.Position = Num(e, "y") is { } yy ? [x, yy, z] : [x, z];

        if (spec.Type is null && spec.Id is null && spec.Name is null) return;
        doc.Elements.Add(spec);
    }

    // ───────────────────────────── Доступ к полям без учёта регистра ─────────────────────────────

    public static JsonElement? Prop(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            foreach (var p in obj.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null)
                    return p.Value;
        return null;
    }

    internal static string? Str(JsonElement obj, params string[] names)
    {
        if (Prop(obj, names) is not { } v) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    internal static double? Num(JsonElement obj, params string[] names) => Prop(obj, names) is { } v ? Number(v) : null;

    internal static int? Int(JsonElement obj, params string[] names) =>
        Num(obj, names) is { } d && double.IsFinite(d) ? (int)Math.Round(d) : null;

    internal static double? Number(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when TryNumber(v.GetString(), out var d) => d,
        _ => null
    };

    internal static bool TryNumber(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().Replace(',', '.').TrimEnd('m', 'м', '°', ' ');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    internal static double[]? Vector(JsonElement obj, params string[] names) => Prop(obj, names) is { } v ? Vector(v) : null;

    internal static double[]? Vector(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array)
        {
            var list = new List<double>();
            foreach (var item in v.EnumerateArray())
                if (Number(item) is { } d) list.Add(d);
            return list.Count > 0 ? list.ToArray() : null;
        }
        if (v.ValueKind == JsonValueKind.Object)
        {
            double? x = Num(v, "x", "width", "w"), y = Num(v, "y", "height", "h"), z = Num(v, "z", "depth", "d");
            if (x is null && y is null && z is null) return null;
            return y is null && x is not null && z is not null ? [x.Value, z.Value] : [x ?? 0, y ?? 0, z ?? 0];
        }
        return null;
    }

    private static (double X, double Z)? PointXZ(JsonElement obj, params string[] names)
    {
        if (Vector(obj, names) is not { } v) return null;
        return v.Length switch
        {
            2 => (v[0], v[1]),
            >= 3 => (v[0], v[2]),
            _ => null
        };
    }

    private static List<Point2D>? Polygon(JsonElement obj, params string[] names)
    {
        if (Prop(obj, names) is not { ValueKind: JsonValueKind.Array } arr) return null;
        var points = new List<Point2D>();
        foreach (var p in arr.EnumerateArray())
        {
            if (Vector(p) is { } v)
            {
                if (v.Length == 2) points.Add(new Point2D(v[0], v[1]));
                else if (v.Length >= 3) points.Add(new Point2D(v[0], v[2]));
            }
        }
        return points.Count >= 3 ? points : null;
    }
}
