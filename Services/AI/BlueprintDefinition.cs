using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.Pdf;

namespace AIArchitectStudio.Services.AI;

/// <summary>Тип кровли в панели верификации чертежа.</summary>
public enum BlueprintRoofType
{
    Flat,
    FlatWithParapet,
    Gable,
    Hip
}

/// <summary>
/// Проверяемая пользователем параметрическая таблица здания (шаг «Чертёж → Проверка осей → 3D»): шаги цифровых и
/// буквенных осей, отметки уровней, сечения колонн и балок, толщины плит, стен и фундамента, тип кровли. Заполняется из
/// векторного слоя PDF (<see cref="ExtractedBlueprintAnnotations"/>) и превращается в блок <c>blueprint</c>, который
/// <see cref="BlueprintCompilerService"/> разворачивает без нейросети: колонны во всех узлах осей, балки между ними,
/// плиты, фундамент и оси на каждом этаже. Все размеры — только из таблицы, ничего не подставляется молча.
/// </summary>
public sealed class BlueprintDefinition
{
    public List<int> XStepsMm { get; set; } = [];
    public List<int> ZStepsMm { get; set; } = [];

    /// <summary>Отметки уровней, м, по возрастанию: N отметок — N−1 этажей, последняя — верх покрытия.</summary>
    public List<double> LevelElevationsM { get; set; } = [];

    public List<string> XAxisLabels { get; set; } = [];
    public List<string> ZAxisLabels { get; set; } = [];

    public string ColumnProfile { get; set; } = ProfileTypes.Rectangle;
    public int ColumnWidthMm { get; set; }
    public int ColumnDepthMm { get; set; }
    public int BeamWidthMm { get; set; }
    public int BeamHeightMm { get; set; }
    public int SlabThicknessMm { get; set; }
    public int WallThicknessMm { get; set; }
    public int ParapetThicknessMm { get; set; }
    public int ParapetHeightMm { get; set; }
    public int FoundationThicknessMm { get; set; }
    public BlueprintRoofType Roof { get; set; } = BlueprintRoofType.Flat;
    public string GroupName { get; set; } = "Корпус по чертежу";

    /// <summary>Какие поля заполнены из векторного слоя PDF (для подписи источника в панели).</summary>
    public List<string> FromPdf { get; } = [];

    /// <summary>Поля, подтверждённые листом или правкой пользователя («X», «Z», «levels», «column», «beam», «slab», «wall», «parapet», «foundation», «roof»).</summary>
    public HashSet<string> Confirmed { get; } = new(StringComparer.Ordinal);

    public double WidthM => XStepsMm.Sum() / 1000.0;
    public double DepthM => ZStepsMm.Sum() / 1000.0;
    public int StoreyCount => Math.Max(0, LevelElevationsM.Count - 1);

    // ───────────────────────────── Заполнение из PDF ─────────────────────────────

    /// <summary>
    /// Предзаполнение панели: шаги, отметки и сечения — из векторного слоя; недостающие сечения — типовыми значениями
    /// формы (их пользователь видит и правит до построения; в подписи источника они не помечены как «из PDF»).
    /// </summary>
    public static BlueprintDefinition FromAnnotations(ExtractedBlueprintAnnotations? a, string groupName)
    {
        var d = new BlueprintDefinition
        {
            GroupName = groupName,
            ColumnWidthMm = 400, ColumnDepthMm = 400,
            BeamWidthMm = 300, BeamHeightMm = 500,
            SlabThicknessMm = 200, WallThicknessMm = 0, ParapetThicknessMm = 250, ParapetHeightMm = 900,
            FoundationThicknessMm = 600
        };
        if (a is null) return d;
        if (a.SectionAndSpecNotes.Any(n => n.Contains("парапет", StringComparison.OrdinalIgnoreCase))) d.Roof = BlueprintRoofType.FlatWithParapet;
        if (a.XStepsMm.Count > 0) { d.XStepsMm = [.. a.XStepsMm]; d.FromPdf.Add("X"); }
        if (a.ZStepsMm.Count > 0) { d.ZStepsMm = [.. a.ZStepsMm]; d.FromPdf.Add("Z"); }
        if (a.XAxisLabels.Count == d.XStepsMm.Count + 1) d.XAxisLabels = [.. a.XAxisLabels];
        if (a.ZAxisLabels.Count == d.ZStepsMm.Count + 1) d.ZAxisLabels = [.. a.ZAxisLabels];
        if (a.LevelsM.Count >= 2) { d.LevelElevationsM = [.. a.LevelsM]; d.FromPdf.Add("levels"); }

        if (a.Best(SectionHintKind.Column) is { } c)
        {
            d.ColumnProfile = c.Profile is "circle" or "pipe" or "i_beam" ? c.Profile : ProfileTypes.Rectangle;
            d.ColumnWidthMm = c.B;
            d.ColumnDepthMm = c.H;
            d.FromPdf.Add("column");
        }
        if (a.Best(SectionHintKind.Beam) is { } b)
        {
            d.BeamWidthMm = Math.Min(b.B, b.H);
            d.BeamHeightMm = Math.Max(b.B, b.H);
            d.FromPdf.Add("beam");
        }
        if (a.Best(SectionHintKind.Slab) is { } s) { d.SlabThicknessMm = s.B; d.FromPdf.Add("slab"); }
        if (a.Sections.Where(h => h.Kind == SectionHintKind.Wall && h.Source.Contains("парапет", StringComparison.OrdinalIgnoreCase)).FirstOrDefault() is { } p)
        {
            d.ParapetThicknessMm = p.B;
            d.FromPdf.Add("parapet");
        }
        if (a.Sections.Where(h => h.Kind == SectionHintKind.Wall && !h.Source.Contains("парапет", StringComparison.OrdinalIgnoreCase))
                .GroupBy(h => h.B).OrderByDescending(g => g.Count()).FirstOrDefault() is { } w)
        {
            d.WallThicknessMm = w.Key;
            d.FromPdf.Add("wall");
        }
        if (a.Best(SectionHintKind.Foundation) is { Profile: "thickness" } f) { d.FoundationThicknessMm = f.B; d.FromPdf.Add("foundation"); }
        if (d.Roof == BlueprintRoofType.FlatWithParapet) d.FromPdf.Add("roof");
        foreach (var key in d.FromPdf) d.Confirmed.Add(key);
        return d;
    }

    // ───────────────────────────── Разбор полей ввода ─────────────────────────────

    /// <summary>Шаги «3500, 3700, 6000» / «3500 3700» / «5x6000» (повтор) → список мм.</summary>
    public static List<int>? ParseSteps(string text, out string? error)
    {
        error = null;
        var result = new List<int>();
        foreach (var raw in Regex.Split(text.Trim(), @"[,;\s]+").Where(t => t.Length > 0))
        {
            var rep = Regex.Match(raw, @"^(\d{1,2})[xхХX×*](\d+(?:[.,]\d+)?)$");
            if (rep.Success)
            {
                int count = int.Parse(rep.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!TryMm(rep.Groups[2].Value, out int step)) { error = raw; return null; }
                result.AddRange(Enumerable.Repeat(step, Math.Clamp(count, 1, 60)));
                continue;
            }
            if (!TryMm(raw, out int v)) { error = raw; return null; }
            result.Add(v);
        }
        return result;

        static bool TryMm(string s, out int v)
        {
            v = 0;
            if (!double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d <= 0 || d > 200000) return false;
            v = (int)Math.Round(d);
            return v > 0;
        }
    }

    /// <summary>Отметки «-1.830; -0,020; +2,830, +5.680» (запятая — и десятичный разделитель, и разделитель списка) → м.</summary>
    public static List<double>? ParseLevels(string text, out string? error)
    {
        error = null;
        var result = new List<double>();
        var normalized = text.Replace('−', '-').Replace('–', '-').Trim();
        foreach (var raw in Regex.Split(normalized, @"[;\s]+|,(?=\s*[+\-±])|,(?=\s)").Select(t => t.Trim().TrimEnd(',')).Where(t => t.Length > 0))
        {
            var s = raw.Replace("±", string.Empty).Replace(',', '.');
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || Math.Abs(v) > 1000) { error = raw; return null; }
            result.Add(Math.Round(v, 3));
        }
        return result.Distinct().Order().ToList();
    }

    public static string FormatSteps(IEnumerable<int> steps) => string.Join(", ", steps);

    public static string FormatLevels(IEnumerable<double> levels) =>
        string.Join("; ", levels.Select(l => Math.Abs(l) < 0.0005 ? "0.000" : (l > 0 ? "+" : "-") + Math.Abs(l).ToString("0.000", CultureInfo.InvariantCulture)));

    /// <summary>Проверка перед компиляцией: список замечаний (пусто — можно строить).</summary>
    public List<string> Validate(bool russian)
    {
        var errors = new List<string>();
        if (XStepsMm.Count == 0) errors.Add(russian ? "Нет шагов цифровых осей (X)" : "No numeric axis steps (X)");
        if (ZStepsMm.Count == 0) errors.Add(russian ? "Нет шагов буквенных осей (Z)" : "No letter axis steps (Z)");
        if (XStepsMm.Concat(ZStepsMm).Any(s => s < 100)) errors.Add(russian ? "Шаг осей меньше 100 мм" : "Axis step below 100 mm");
        if (LevelElevationsM.Count < 2) errors.Add(russian ? "Нужно не менее двух отметок (низ и верх этажа)" : "At least two elevations are required");
        for (int i = 0; i + 1 < LevelElevationsM.Count; i++)
            if (LevelElevationsM[i + 1] - LevelElevationsM[i] < 0.5)
            {
                errors.Add(russian ? $"Высота этажа {i + 1} меньше 0.5 м" : $"Storey {i + 1} is lower than 0.5 m");
                break;
            }
        if (ColumnWidthMm < 50 || ColumnDepthMm < 50) errors.Add(russian ? "Сечение колонн меньше 50 мм" : "Column section below 50 mm");
        if (BeamWidthMm < 0 || BeamHeightMm < 0) errors.Add(russian ? "Отрицательное сечение балок" : "Negative beam section");
        if (SlabThicknessMm < 0 || WallThicknessMm < 0 || FoundationThicknessMm < 0) errors.Add(russian ? "Отрицательная толщина" : "Negative thickness");
        if (Roof == BlueprintRoofType.FlatWithParapet && (ParapetThicknessMm <= 0 || ParapetHeightMm <= 0))
            errors.Add(russian ? "Укажите толщину и высоту парапета" : "Set the parapet thickness and height");
        if (LevelElevationsM.Count >= 2 && SlabThicknessMm / 1000.0 >= LevelElevationsM.Zip(LevelElevationsM.Skip(1), (a, b) => b - a).Min())
            errors.Add(russian ? "Плита толще высоты этажа" : "Slab is thicker than a storey");
        return errors;
    }

    // ───────────────────────────── Блок blueprint ─────────────────────────────

    /// <summary>JSON ответа «как от ИИ» с блоком blueprint — тот же путь, что и ответ нейросети (солвер, Undo, отчёт).</summary>
    public string ToSceneJson(string message)
    {
        var root = new JsonObject
        {
            ["message"] = message,
            ["blueprint"] = ToBlueprint()
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public JsonObject ToBlueprint()
    {
        string X(int i) => XAxisLabels.Count == XStepsMm.Count + 1 ? XAxisLabels[i] : (i + 1).ToString(CultureInfo.InvariantCulture);
        string Z(int i) => ZAxisLabels.Count == ZStepsMm.Count + 1 ? ZAxisLabels[i] : StructuralAxesBuilder.Letter(i);

        var xs = new JsonArray();
        int pos = 0;
        xs.Add(new JsonObject { ["label"] = X(0), ["pos"] = 0 });
        for (int i = 0; i < XStepsMm.Count; i++)
        {
            pos += XStepsMm[i];
            xs.Add(new JsonObject { ["label"] = X(i + 1), ["pos"] = pos });
        }
        var zs = new JsonArray();
        pos = 0;
        zs.Add(new JsonObject { ["label"] = Z(0), ["pos"] = 0 });
        for (int i = 0; i < ZStepsMm.Count; i++)
        {
            pos += ZStepsMm[i];
            zs.Add(new JsonObject { ["label"] = Z(i + 1), ["pos"] = pos });
        }

        var levels = new JsonArray();
        for (int i = 0; i + 1 < LevelElevationsM.Count; i++)
        {
            double e = LevelElevationsM[i], h = LevelElevationsM[i + 1] - e;
            levels.Add(new JsonObject
            {
                ["name"] = $"Этаж {i + 1} ({ExtractedBlueprintAnnotations.ElevationText(e)})",
                ["elevation"] = Math.Round(e * 1000),
                ["height"] = Math.Round(h * 1000),
                ["slabThickness"] = SlabThicknessMm
            });
        }

        var column = new JsonObject { ["profile"] = ColumnProfile };
        if (ColumnProfile is ProfileTypes.Circle or ProfileTypes.Pipe)
        {
            column["d"] = ColumnWidthMm;
            if (ColumnDepthMm != ColumnWidthMm && ColumnProfile == ProfileTypes.Circle) { column["b"] = ColumnWidthMm; column["h"] = ColumnDepthMm; }
        }
        else
        {
            column["b"] = ColumnWidthMm;
            column["h"] = ColumnDepthMm;
        }
        var sections = new JsonObject { ["К1"] = column };
        bool beams = BeamWidthMm > 0 && BeamHeightMm > 0;
        if (beams) sections["Б1"] = new JsonObject { ["profile"] = ProfileTypes.Rectangle, ["b"] = BeamWidthMm, ["h"] = BeamHeightMm };

        var bp = new JsonObject
        {
            ["group"] = GroupName,
            ["units"] = "mm",
            ["gridAxes"] = new JsonObject { ["x"] = xs, ["z"] = zs },
            // Край плит и фундамента — по наружным граням крайних колонн.
            ["contourOffset"] = Math.Max(ColumnWidthMm, ColumnDepthMm) / 2.0,
            ["levels"] = levels,
            ["sections"] = sections,
            ["columnPlacements"] = new JsonArray(new JsonObject { ["section"] = "К1", ["at"] = "all", ["levels"] = "all" })
        };
        if (beams)
            bp["beamPlacements"] = new JsonArray(
                new JsonObject { ["section"] = "Б1", ["along"] = "x", ["axes"] = "all", ["span"] = "all", ["levels"] = "all" },
                new JsonObject { ["section"] = "Б1", ["along"] = "z", ["axes"] = "all", ["span"] = "all", ["levels"] = "all" });
        if (SlabThicknessMm > 0) bp["slabs"] = new JsonArray(new JsonObject { ["levels"] = "all", ["polygon"] = "contour", ["thickness"] = SlabThicknessMm });
        if (WallThicknessMm > 0)
        {
            // Наружные стены по крайним осям (контур сетки), на всех этажах.
            string x0 = X(0), x1 = X(XStepsMm.Count), z0 = Z(0), z1 = Z(ZStepsMm.Count);
            bp["wallSegments"] = new JsonArray(new JsonObject
            {
                ["id"] = "НС",
                ["polyline"] = new JsonArray(new JsonArray(x0, z0), new JsonArray(x1, z0), new JsonArray(x1, z1), new JsonArray(x0, z1)),
                ["closed"] = true,
                ["thickness"] = WallThicknessMm,
                ["levels"] = "all"
            });
        }
        var roof = new JsonObject
        {
            ["type"] = Roof switch { BlueprintRoofType.Gable => RoofShapes.Gable, BlueprintRoofType.Hip => RoofShapes.Hip, _ => RoofShapes.Flat }
        };
        if (Roof == BlueprintRoofType.FlatWithParapet && ParapetHeightMm > 0 && ParapetThicknessMm > 0)
            roof["parapet"] = new JsonObject { ["height"] = ParapetHeightMm, ["thickness"] = ParapetThicknessMm };
        bp["roof"] = roof;
        if (FoundationThicknessMm > 0) bp["foundation"] = new JsonObject { ["thickness"] = FoundationThicknessMm, ["polygon"] = "contour" };
        return bp;
    }

    /// <summary>Блок «параметры, проверенные пользователем» для промпта ИИ (наивысший приоритет).</summary>
    public string ToPromptBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ПАРАМЕТРЫ ЗДАНИЯ, ПРОВЕРЕННЫЕ ПОЛЬЗОВАТЕЛЕМ ПЕРЕД 3D (наивысший приоритет — используй без изменений):");
        if (XStepsMm.Count > 0) sb.AppendLine($"- Шаги цифровых осей по X, мм: {FormatSteps(XStepsMm)} (сумма {XStepsMm.Sum()} мм)");
        if (ZStepsMm.Count > 0) sb.AppendLine($"- Шаги буквенных осей по Z, мм: {FormatSteps(ZStepsMm)} (сумма {ZStepsMm.Sum()} мм)");
        if (LevelElevationsM.Count >= 2)
            sb.AppendLine($"- Отметки уровней, м: {string.Join("; ", LevelElevationsM.Select(ExtractedBlueprintAnnotations.ElevationText))} (этажей {StoreyCount}, последняя — верх покрытия)");
        // Типовые значения формы, которые пользователь не подтверждал, передаются как ориентир, а не как факт.
        string Mark(string key) => Confirmed.Contains(key) ? string.Empty : " (типовое значение формы — сверь с листами)";
        sb.AppendLine($"- Колонны: {ColumnProfile} {ColumnWidthMm}×{ColumnDepthMm} мм{Mark("column")}");
        sb.AppendLine($"- Балки: {(BeamWidthMm > 0 && BeamHeightMm > 0 ? $"{BeamWidthMm}×{BeamHeightMm} мм" : "нет (безбалочный каркас)")}{Mark("beam")}");
        sb.AppendLine($"- Плита: {SlabThicknessMm} мм{Mark("slab")}; наружные стены: {(WallThicknessMm > 0 ? WallThicknessMm + " мм" : "нет")}{Mark("wall")}; " +
                      $"фундаментная плита: {(FoundationThicknessMm > 0 ? FoundationThicknessMm + " мм" : "нет")}{Mark("foundation")}");
        sb.AppendLine($"- Кровля: {Roof switch { BlueprintRoofType.FlatWithParapet => $"плоская с парапетом толщиной {ParapetThicknessMm} мм и высотой {ParapetHeightMm} мм", BlueprintRoofType.Gable => "двускатная", BlueprintRoofType.Hip => "вальмовая", _ => "плоская" }}{Mark("roof")}");
        return sb.ToString().TrimEnd();
    }
}
