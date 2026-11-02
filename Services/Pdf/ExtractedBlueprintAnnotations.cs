using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AIArchitectStudio.Services.Pdf;

/// <summary>Размерное число листа (мм): ориентация цепочки, положение вдоль неё и смещение линии цепочки.</summary>
public sealed record DimensionValue(int Page, int ValueMm, bool Horizontal, double Along, double Offset, double FontSize, bool HasDimensionLine);

/// <summary>Размерная цепочка: значения одной размерной линии по порядку (слева направо / снизу вверх).</summary>
public sealed class DimensionChain
{
    public required int Page { get; init; }
    public required bool Horizontal { get; init; }
    public required double Offset { get; init; }
    public List<DimensionValue> Values { get; } = [];
    public int SumMm => Values.Sum(v => v.ValueMm);
    public List<int> StepsMm => Values.Select(v => v.ValueMm).ToList();

    /// <summary>Сумма цепочки совпала с общим размером, проставленным параллельно.</summary>
    public bool ConfirmedByOverall { get; set; }

    /// <summary>Сколько значений стоят посередине между соседними марками осей.</summary>
    public int AxisMatches { get; set; }

    public double Score => Values.Count + (ConfirmedByOverall ? 10 : 0) + 3 * AxisMatches + (Values.All(v => v.ValueMm >= 300) ? 1 : 0);
}

/// <summary>Высотная отметка «+2,830» / «-1,830» / «±0,000» (м).</summary>
public sealed record ElevationMark(int Page, string Text, double Meters, double X, double Y);

/// <summary>Марка координационной оси (цифра или буква), найденная в кружке или по выравниванию.</summary>
public sealed record AxisLabel(int Page, string Label, bool IsNumeric, double X, double Y, bool InCircle);

public enum SectionHintKind
{
    Column,
    Beam,
    Slab,
    Wall,
    Foundation
}

/// <summary>Сечение или толщина из текста листа: «Колонны К1 400х400», «плита толщиной 200 мм», строка спецификации.</summary>
public sealed record SectionHint(SectionHintKind Kind, string? Mark, string Profile, int B, int H, string Source);

/// <summary>
/// Строительная семантика векторного слоя PDF: размерные числа и цепочки по X и Z, высотные отметки, марки осей,
/// сечения/толщины из общих указаний и спецификаций, класс бетона и арматуры. Плюс рекомендованные значения для
/// панели верификации и блок «точных данных» для промпта ИИ.
/// </summary>
public sealed class ExtractedBlueprintAnnotations
{
    public List<DimensionValue> DimensionValuesMm { get; } = [];
    public List<DimensionChain> HorizontalChains { get; } = [];
    public List<DimensionChain> VerticalChains { get; } = [];
    public List<ElevationMark> ElevationMarks { get; } = [];
    public List<AxisLabel> AxisLabels { get; } = [];
    public List<string> SectionAndSpecNotes { get; } = [];
    public List<SectionHint> Sections { get; } = [];
    public List<string> Marks { get; } = [];
    public List<string> RebarClasses { get; } = [];
    public string? ConcreteClass { get; set; }

    public int PagesAnalyzed { get; set; }
    public int PagesWithText { get; set; }
    public int TextRuns { get; set; }
    public int Segments { get; set; }
    public int Circles { get; set; }
    public double ElapsedMs { get; set; }
    public List<string> Warnings { get; } = [];

    /// <summary>Рекомендованные шаги цифровых осей (по X) и буквенных (по Z), мм.</summary>
    public List<int> XStepsMm { get; } = [];
    public List<int> ZStepsMm { get; } = [];
    public DimensionChain? XChain { get; set; }
    public DimensionChain? ZChain { get; set; }

    /// <summary>Марки осей по порядку цепочки (если их число = шагов + 1).</summary>
    public List<string> XAxisLabels { get; } = [];
    public List<string> ZAxisLabels { get; } = [];

    /// <summary>Различные отметки по возрастанию, м.</summary>
    public List<double> LevelsM { get; } = [];

    public bool HasTextLayer => PagesWithText > 0;

    public bool HasFacts => DimensionValuesMm.Count + ElevationMarks.Count + AxisLabels.Count + Sections.Count + SectionAndSpecNotes.Count > 0;

    public SectionHint? Best(SectionHintKind kind) =>
        Sections.Where(s => s.Kind == kind)
            .GroupBy(s => (s.Profile, s.B, s.H))
            .OrderByDescending(g => g.Any(s => s.Mark is not null))
            .ThenByDescending(g => g.Count())
            .Select(g => g.First())
            .FirstOrDefault();

    public static string Mm(int v) => v.ToString("#,0", CultureInfo.GetCultureInfo("ru-RU")).Replace(' ', ' ');

    public static string ElevationText(double meters)
    {
        double v = Math.Round(meters, 3);
        if (Math.Abs(v) < 0.0005) return "±0,000";
        return (v > 0 ? "+" : "-") + Math.Abs(v).ToString("0.000", CultureInfo.GetCultureInfo("ru-RU"));
    }

    /// <summary>Короткая сводка для панели верификации.</summary>
    public string Summary(bool russian)
    {
        if (!HasTextLayer)
            return russian ? "Текстового слоя нет (скан) — числа читает ИИ с растра 2000 px; параметры можно ввести вручную"
                : "No text layer (scan) — the AI reads the 2000 px raster; parameters can be entered manually";
        return russian
            ? $"Векторный слой: размеров {DimensionValuesMm.Count}, цепочек {HorizontalChains.Count + VerticalChains.Count}, отметок {LevelsM.Count}, марок осей {AxisLabels.Select(a => a.Label).Distinct().Count()}, сечений {Sections.Count} · {ElapsedMs:0} мс"
            : $"Vector layer: {DimensionValuesMm.Count} dimensions, {HorizontalChains.Count + VerticalChains.Count} chains, {LevelsM.Count} levels, {AxisLabels.Select(a => a.Label).Distinct().Count()} axis marks, {Sections.Count} sections · {ElapsedMs:0} ms";
    }

    /// <summary>Блок «ТОЧНЫЕ ДАННЫЕ, ИЗВЛЕЧЕННЫЕ ИЗ ВЕКТОРНОГО СЛОЯ PDF» для пользовательской части промпта.</summary>
    public string ToPromptBlock()
    {
        if (!HasTextLayer || !HasFacts) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("ТОЧНЫЕ ДАННЫЕ, ИЗВЛЕЧЕННЫЕ ИЗ ВЕКТОРНОГО СЛОЯ PDF (текст и линии прочитаны из файла напрямую, без распознавания пикселей):");
        void Chains(string title, List<DimensionChain> chains)
        {
            foreach (var c in chains.Where(c => c.Values.Count >= 2).OrderByDescending(c => c.Score).Take(6))
                sb.AppendLine($"- {title}, лист {c.Page + 1}: {string.Join(" · ", c.StepsMm)} (сумма {c.SumMm} мм" +
                              (c.ConfirmedByOverall ? ", совпадает с общим размером" : string.Empty) +
                              (c.AxisMatches > 0 ? $", между марками осей: {c.AxisMatches}" : string.Empty) + ")");
        }
        Chains("Размерная цепочка по горизонтали (X), мм", HorizontalChains);
        Chains("Размерная цепочка по вертикали (Z), мм", VerticalChains);
        var overall = HorizontalChains.Concat(VerticalChains).Where(c => c.Values.Count == 1).Select(c => c.Values[0].ValueMm).Distinct().OrderDescending().Take(12).ToList();
        if (overall.Count > 0) sb.AppendLine($"- Отдельные размеры, мм: {string.Join(", ", overall)}");
        if (XStepsMm.Count > 0) sb.AppendLine($"- Шаги цифровых осей (X), мм: {string.Join(", ", XStepsMm)} → всего {XStepsMm.Sum()} мм" + (XAxisLabels.Count == XStepsMm.Count + 1 ? $" (оси {string.Join(", ", XAxisLabels)})" : string.Empty));
        if (ZStepsMm.Count > 0) sb.AppendLine($"- Шаги буквенных осей (Z), мм: {string.Join(", ", ZStepsMm)} → всего {ZStepsMm.Sum()} мм" + (ZAxisLabels.Count == ZStepsMm.Count + 1 ? $" (оси {string.Join(", ", ZAxisLabels)})" : string.Empty));
        if (LevelsM.Count > 0) sb.AppendLine($"- Высотные отметки, м: {string.Join("; ", LevelsM.Select(ElevationText))}");
        var numeric = AxisLabels.Where(a => a.IsNumeric).Select(a => a.Label).Distinct().OrderBy(l => int.TryParse(l, out int n) ? n : 999).ToList();
        var letters = AxisLabels.Where(a => !a.IsNumeric).Select(a => a.Label).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList();
        if (numeric.Count + letters.Count > 0)
            sb.AppendLine($"- Марки координационных осей: цифровые {(numeric.Count > 0 ? string.Join(", ", numeric) : "нет")}; буквенные {(letters.Count > 0 ? string.Join(", ", letters) : "нет")}");
        foreach (var s in Sections.GroupBy(s => (s.Kind, s.Mark, s.Profile, s.B, s.H)).Select(g => g.First()).Take(16))
            sb.AppendLine($"- Сечение: {KindTitle(s.Kind)}{(s.Mark is null ? string.Empty : " " + s.Mark)} — {SectionText(s)} (из текста «{Clip(s.Source, 70)}»)");
        if (ConcreteClass is not null) sb.AppendLine($"- Класс бетона: {ConcreteClass}");
        if (RebarClasses.Count > 0) sb.AppendLine($"- Арматура: {string.Join(", ", RebarClasses)}");
        if (Marks.Count > 0) sb.AppendLine($"- Марки элементов на листах: {string.Join(", ", Marks.Take(30))}");
        foreach (var note in SectionAndSpecNotes.Take(12)) sb.AppendLine($"- Указание: «{Clip(note, 150)}»");
        sb.AppendLine("ТРЕБОВАНИЕ: используй эти числа как ТОЧНЫЕ значения вместо оценки по пикселям изображения — шаги осей для \"gridAxes\", " +
                      "отметки для \"levels\", сечения для \"sections\". Если изображение расходится с таблицей, верь таблице и укажи расхождение в \"message\".");
        return sb.ToString().TrimEnd();
    }

    public static string KindTitle(SectionHintKind kind) => kind switch
    {
        SectionHintKind.Column => "колонна",
        SectionHintKind.Beam => "балка",
        SectionHintKind.Slab => "плита",
        SectionHintKind.Wall => "стена",
        _ => "фундамент"
    };

    public static string SectionText(SectionHint s) => s.Profile switch
    {
        "circle" => $"Ø{s.B}",
        "pipe" => $"труба Ø{s.B}",
        "i_beam" => $"двутавр {s.H}×{s.B}",
        "thickness" => $"толщина {s.B} мм",
        _ => $"{s.B}×{s.H}"
    };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Классификатор надписей и линий листа в строительные факты (<see cref="ExtractedBlueprintAnnotations"/>).</summary>
public static class BlueprintAnnotationAnalyzer
{
    private static readonly Regex SingleDim = new(@"^\(?(\d{1,2} \d{3}|\d{3,5})\)?$", RegexOptions.Compiled);
    private static readonly Regex RepeatDim = new(@"^(\d{1,2})\s*[xхХX×*]\s*(\d{1,2} \d{3}|\d{3,5})(?:\s*=\s*(\d{1,2} \d{3}|\d{3,6}))?$", RegexOptions.Compiled);
    private static readonly Regex Elevation = new(@"(?<![\d.,])([+\-±])\s?(\d{1,3}[,.]\d{3})(?![\d])", RegexOptions.Compiled);
    private static readonly Regex AxisText = new(@"^(\d{1,2}|[А-ЯЁA-Z])['’]?$", RegexOptions.Compiled);
    private static readonly Regex MarkPrefix = new(@"^(ФПм|ФП|Фм|ФМ|Пм|Пп|ПП|Км|КМ|Бм|БМ|См|СМ|Ф|П|К|Б|Р|С)(\d{1,3})(?=$|[\s.,;:])", RegexOptions.Compiled);
    private static readonly Regex MarkAnywhere = new(@"(?<![\p{L}\d])(ФПм|ФП|Фм|Пм|Пп|Км|Бм|См|Ф|П|К|Б|Р|С)(\d{1,3})(?![\p{L}\d])", RegexOptions.Compiled);
    private static readonly Regex RectSection = new(@"(\d{2,4})\s*[xхХX×*]\s*(\d{2,4})", RegexOptions.Compiled);
    private static readonly Regex Diameter = new(@"[ØøⲐ⌀]\s*(\d{2,4})(?:\s*[xхХX×*]\s*(\d{1,4}))?", RegexOptions.Compiled);
    private static readonly Regex ThicknessEq = new(@"(?<![\p{L}])[hHtTδ]\s*=\s*(\d{2,4})", RegexOptions.Compiled);
    private static readonly Regex ThicknessWords = new(@"(?:толщин\w*|толщ\.?)\s*(?:не\s+менее\s+)?(\d{2,4})|(\d{2,4})\s*мм", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Concrete = new(@"(?<![\p{L}\d])([BВ])\s?(\d{2}(?:[,.]5)?)(?![\d])", RegexOptions.Compiled);
    private static readonly Regex Rebar = new(@"(?<![\p{L}\d])([АA])\s?(240|400|500)\s?([СC])?(?![\d])", RegexOptions.Compiled);
    private static readonly Regex KindWord = new(@"(колонн|стойк|пилон|балк|ригел|прогон|плит|перекрыти|покрыти|стен|диафрагм|парапет|фундамент|ростверк)\w*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string GostExcludedLetters = "ЁЗЙОХЦЧЩЪЫЬ";

    public static ExtractedBlueprintAnnotations Analyze(PdfNativeDocument doc, IEnumerable<int>? pages = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new ExtractedBlueprintAnnotations();
        result.Warnings.AddRange(doc.Warnings);
        var set = pages?.ToHashSet();
        var notes = new HashSet<string>(StringComparer.Ordinal);
        var marks = new HashSet<string>(StringComparer.Ordinal);
        var rebar = new HashSet<string>(StringComparer.Ordinal);
        var axisPositions = new Dictionary<int, (Family? Numeric, Family? Letter)>();

        foreach (var page in doc.Pages)
        {
            if (set is not null && !set.Contains(page.Index)) continue;
            result.PagesAnalyzed++;
            if (!page.HasTextLayer) continue;
            result.PagesWithText++;
            result.TextRuns += page.Runs.Count;
            result.Segments += page.Segments.Count;
            result.Circles += page.Circles.Count;

            var index = new SegmentIndex(page.Segments);
            var labels = AxisLabelsOf(page);
            result.AxisLabels.AddRange(labels);
            axisPositions[page.Index] = (Family.Of(labels.Where(l => l.IsNumeric)), Family.Of(labels.Where(l => !l.IsNumeric)));

            var values = new List<DimensionValue>();
            foreach (var run in page.Runs)
            {
                string text = Normalize(run.Text);
                foreach (Match m in Elevation.Matches(text))
                {
                    double v = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                    if (m.Groups[1].Value == "-") v = -v;
                    else if (m.Groups[1].Value == "±" && v > 0.0005) continue;
                    if (!result.ElevationMarks.Any(e => e.Page == page.Index && Math.Abs(e.Meters - v) < 0.0005 && Math.Abs(e.X - run.CenterX) < 1 && Math.Abs(e.Y - run.CenterY) < 1))
                        result.ElevationMarks.Add(new ElevationMark(page.Index, m.Value, v, run.CenterX, run.CenterY));
                }
                if (run.IsHorizontal || run.IsVertical) values.AddRange(DimensionsOf(page.Index, run, text, index));
                ReadNotes(run, text, result, notes, marks, rebar);
            }
            ReadSpecRows(page, result, marks);

            result.DimensionValuesMm.AddRange(values);
            foreach (bool horizontal in new[] { true, false })
            {
                var chains = BuildChains(page.Index, values.Where(v => v.Horizontal == horizontal).ToList(), horizontal);
                (horizontal ? result.HorizontalChains : result.VerticalChains).AddRange(chains);
            }
        }

        // Шаги осей: цепочка с наибольшей оценкой (подтверждение общим размером, совпадение с марками осей, длина).
        foreach (var chain in result.HorizontalChains.Concat(result.VerticalChains))
        {
            var (numeric, letter) = axisPositions.GetValueOrDefault(chain.Page);
            var family = numeric?.Horizontal == chain.Horizontal ? numeric : letter?.Horizontal == chain.Horizontal ? letter : null;
            chain.AxisMatches = family is null ? 0 : family.MidpointMatches(chain);
        }
        DimensionChain? Pick(bool numericAxes)
        {
            var candidates = result.HorizontalChains.Concat(result.VerticalChains).Where(c => c.Values.Count >= 1).Where(c =>
            {
                var (numeric, letter) = axisPositions.GetValueOrDefault(c.Page);
                // Ориентация семейства осей на листе: цифровые обычно идут слева направо (цепочка по горизонтали).
                bool numericHorizontal = numeric is not null ? numeric.Horizontal : letter is not null ? !letter.Horizontal : true;
                return c.Horizontal == (numericAxes ? numericHorizontal : !numericHorizontal);
            }).ToList();
            var multi = candidates.Where(c => c.Values.Count >= 2).ToList();
            return (multi.Count > 0 ? multi : candidates).OrderByDescending(c => c.Score).ThenByDescending(c => c.SumMm).FirstOrDefault();
        }
        result.XChain = Pick(numericAxes: true);
        result.ZChain = Pick(numericAxes: false);
        if (result.XChain is { } xc)
        {
            result.XStepsMm.AddRange(xc.StepsMm);
            if (axisPositions.GetValueOrDefault(xc.Page).Numeric is { } f && f.Labels.Count == xc.Values.Count + 1) result.XAxisLabels.AddRange(f.Labels);
        }
        if (result.ZChain is { } zc)
        {
            result.ZStepsMm.AddRange(zc.StepsMm);
            if (axisPositions.GetValueOrDefault(zc.Page).Letter is { } f && f.Labels.Count == zc.Values.Count + 1) result.ZAxisLabels.AddRange(f.Labels);
        }

        foreach (var v in result.ElevationMarks.Select(e => Math.Round(e.Meters, 3)).Distinct().Order())
            result.LevelsM.Add(v);
        result.SectionAndSpecNotes.AddRange(notes.Take(40));
        result.Marks.AddRange(marks.OrderBy(m => m, StringComparer.Ordinal));
        result.RebarClasses.AddRange(rebar.Order());
        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>Чтение файла и анализ выбранных страниц одним вызовом.</summary>
    public static ExtractedBlueprintAnnotations Extract(string path, IEnumerable<int>? pages = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = pages?.ToList();
        var doc = PdfNativeContentExtractor.Read(path, list);
        var result = Analyze(doc, list);
        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    private static string Normalize(string s) =>
        s.Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ').Replace('−', '-').Replace('–', '-').Trim();

    // ───────────────────────────── Размеры ─────────────────────────────

    private static IEnumerable<DimensionValue> DimensionsOf(int page, PdfTextRun run, string text, SegmentIndex index)
    {
        static int? Parse(string s) => int.TryParse(s.Replace(" ", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
        static bool Valid(int v) => v is >= 100 and <= 60000 && v % 5 == 0;

        bool horizontal = run.IsHorizontal;
        double along = horizontal ? run.CenterX : run.CenterY;
        double offset = horizontal ? run.Y : run.X;
        var single = SingleDim.Match(text);
        if (single.Success && Parse(single.Groups[1].Value) is { } value && Valid(value))
        {
            yield return new DimensionValue(page, value, horizontal, along, offset, run.FontSize, index.HasDimensionLine(run));
            yield break;
        }
        var repeat = RepeatDim.Match(text);
        if (repeat.Success && Parse(repeat.Groups[1].Value) is { } count && count is >= 2 and <= 60 && Parse(repeat.Groups[2].Value) is { } step && Valid(step))
        {
            // «5х6000=30000»: шаг повторяется count раз; общий размер (если указан) должен совпасть.
            if (repeat.Groups[3].Success && Parse(repeat.Groups[3].Value) is { } total && total != count * step) yield break;
            bool line = index.HasDimensionLine(run);
            for (int k = 0; k < count; k++)
                yield return new DimensionValue(page, step, horizontal, along + k * 1e-6, offset, run.FontSize, line);
        }
    }

    private static List<DimensionChain> BuildChains(int page, List<DimensionValue> values, bool horizontal)
    {
        var chains = new List<DimensionChain>();
        if (values.Count == 0) return chains;
        // На листе с размерными линиями — только числа над линиями (числа таблиц и указаний не в счёт).
        var withLines = values.Where(v => v.HasDimensionLine).ToList();
        var source = withLines.Count >= 2 ? withLines : values;
        var sorted = source.OrderBy(v => v.Offset).ToList();
        int at = 0;
        while (at < sorted.Count)
        {
            double o0 = sorted[at].Offset, tol = 0.5 * sorted[at].FontSize;
            int end = at;
            while (end < sorted.Count && Math.Abs(sorted[end].Offset - o0) <= tol) end++;
            var group = sorted.GetRange(at, end - at).OrderBy(v => v.Along).ToList();
            at = end;
            // Разрыв больше 25 кеглей разделяет независимые цепочки на одной высоте (два плана на листе).
            var chain = new DimensionChain { Page = page, Horizontal = horizontal, Offset = o0 };
            foreach (var v in group)
            {
                if (chain.Values.Count > 0 && v.Along - chain.Values[^1].Along > 25 * v.FontSize)
                {
                    chains.Add(chain);
                    chain = new DimensionChain { Page = page, Horizontal = horizontal, Offset = o0 };
                }
                chain.Values.Add(v);
            }
            chains.Add(chain);
        }
        // Подтверждение общим размером: параллельное число, равное сумме цепочки, в её пределах по длине.
        foreach (var c in chains.Where(c => c.Values.Count >= 2))
        {
            double a0 = c.Values[0].Along, a1 = c.Values[^1].Along, margin = 4 * c.Values[0].FontSize;
            c.ConfirmedByOverall = values.Any(v => !c.Values.Contains(v) && Math.Abs(v.ValueMm - c.SumMm) <= 2 &&
                                                   v.Along >= a0 - margin && v.Along <= a1 + margin);
        }
        return chains;
    }

    /// <summary>Индекс горизонтальных и вертикальных отрезков листа для поиска размерной линии под числом.</summary>
    private sealed class SegmentIndex
    {
        private readonly (double Key, double Min, double Max)[] _h;
        private readonly (double Key, double Min, double Max)[] _v;

        public SegmentIndex(List<PdfLineSegment> segments)
        {
            var h = new List<(double, double, double)>();
            var v = new List<(double, double, double)>();
            foreach (var s in segments)
            {
                double dx = Math.Abs(s.X2 - s.X1), dy = Math.Abs(s.Y2 - s.Y1);
                if (dy <= 0.02 * dx) h.Add(((s.Y1 + s.Y2) / 2, Math.Min(s.X1, s.X2), Math.Max(s.X1, s.X2)));
                else if (dx <= 0.02 * dy) v.Add(((s.X1 + s.X2) / 2, Math.Min(s.Y1, s.Y2), Math.Max(s.Y1, s.Y2)));
            }
            _h = h.OrderBy(x => x.Item1).ToArray();
            _v = v.OrderBy(x => x.Item1).ToArray();
        }

        /// <summary>Параллельная линия в полосе от −0.15 до +1.3 кегля «под» базовой линией, проходящая под центром надписи.</summary>
        public bool HasDimensionLine(PdfTextRun run)
        {
            double fs = run.FontSize, rad = run.AngleDeg * Math.PI / 180;
            double downX = Math.Sin(rad), downY = -Math.Cos(rad);
            if (run.IsHorizontal)
            {
                // Полоса по Y: y = baseline + t·downY, t ∈ [−0.15, 1.3]·fs.
                double y1 = run.Y + downY * -0.15 * fs, y2 = run.Y + downY * 1.3 * fs;
                return Any(_h, Math.Min(y1, y2), Math.Max(y1, y2), (run.X + run.EndX) / 2, fs);
            }
            double x1 = run.X + downX * -0.15 * fs, x2 = run.X + downX * 1.3 * fs;
            return Any(_v, Math.Min(x1, x2), Math.Max(x1, x2), (run.Y + run.EndY) / 2, fs);
        }

        private static bool Any((double Key, double Min, double Max)[] items, double lo, double hi, double center, double fs)
        {
            int i = LowerBound(items, lo);
            for (; i < items.Length && items[i].Key <= hi; i++)
                if (items[i].Max - items[i].Min >= fs && items[i].Min - 0.1 * fs <= center && items[i].Max + 0.1 * fs >= center) return true;
            return false;
        }

        private static int LowerBound((double Key, double Min, double Max)[] items, double key)
        {
            int lo = 0, hi = items.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (items[mid].Key < key) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }

    // ───────────────────────────── Марки осей ─────────────────────────────

    private static List<AxisLabel> AxisLabelsOf(PdfPageVectorContent page)
    {
        var all = new List<AxisLabel>();
        foreach (var run in page.Runs)
        {
            string t = run.Text.Trim();
            var m = AxisText.Match(t);
            if (!m.Success) continue;
            string label = m.Groups[1].Value;
            bool numeric = char.IsDigit(label[0]);
            if (numeric && int.Parse(label, CultureInfo.InvariantCulture) is < 1 or > 60) continue;
            if (!numeric && GostExcludedLetters.Contains(label[0])) continue;
            double cx = run.CenterX, cy = run.CenterY;
            bool inCircle = page.Circles.Any(c => c.R >= 0.45 * run.FontSize && c.R <= 3.5 * run.FontSize &&
                                                  Math.Abs(c.X - cx) < 0.6 * c.R && Math.Abs(c.Y - cy) < 0.6 * c.R);
            all.Add(new AxisLabel(page.Index, label, numeric, cx, cy, inCircle));
        }
        if (all.Any(a => a.InCircle)) return all.Where(a => a.InCircle).ToList();
        // Без кружков: марка принимается, если повторяется на другом конце оси (то же X или то же Y).
        return all.Where(a => all.Any(b => b != a && b.Label == a.Label &&
                                           (Math.Abs(b.X - a.X) < 2 || Math.Abs(b.Y - a.Y) < 2))).ToList();
    }

    /// <summary>Семейство осей листа: марки по порядку, их положение и направление раскладки.</summary>
    private sealed class Family
    {
        public required List<string> Labels { get; init; }
        public required List<double> Positions { get; init; }
        public required bool Horizontal { get; init; }

        public static Family? Of(IEnumerable<AxisLabel> labels)
        {
            var byLabel = labels.GroupBy(l => l.Label).Select(g => (Label: g.Key, X: g.Average(l => l.X), Y: g.Average(l => l.Y))).ToList();
            if (byLabel.Count < 2) return null;
            double spreadX = byLabel.Max(l => l.X) - byLabel.Min(l => l.X), spreadY = byLabel.Max(l => l.Y) - byLabel.Min(l => l.Y);
            bool horizontal = spreadX >= spreadY;
            var ordered = byLabel.OrderBy(l => horizontal ? l.X : l.Y).ToList();
            return new Family { Labels = ordered.Select(l => l.Label).ToList(), Positions = ordered.Select(l => horizontal ? l.X : l.Y).ToList(), Horizontal = horizontal };
        }

        /// <summary>Число значений цепочки, стоящих посередине между соседними осями.</summary>
        public int MidpointMatches(DimensionChain chain)
        {
            int matches = 0;
            for (int i = 0; i + 1 < Positions.Count; i++)
            {
                double mid = (Positions[i] + Positions[i + 1]) / 2, gap = Positions[i + 1] - Positions[i];
                double tol = Math.Max(0.2 * gap, chain.Values[0].FontSize);
                if (chain.Values.Any(v => Math.Abs(v.Along - mid) <= tol)) matches++;
            }
            return matches;
        }
    }

    // ───────────────────────────── Сечения и указания ─────────────────────────────

    private static SectionHintKind? KindOf(string word)
    {
        word = word.ToLowerInvariant();
        return word.StartsWith("колонн") || word.StartsWith("стойк") || word.StartsWith("пилон") ? SectionHintKind.Column
            : word.StartsWith("балк") || word.StartsWith("ригел") || word.StartsWith("прогон") ? SectionHintKind.Beam
            : word.StartsWith("плит") || word.StartsWith("перекрыти") || word.StartsWith("покрыти") ? SectionHintKind.Slab
            : word.StartsWith("стен") || word.StartsWith("диафрагм") || word.StartsWith("парапет") ? SectionHintKind.Wall
            : word.StartsWith("фундамент") || word.StartsWith("ростверк") ? SectionHintKind.Foundation
            : null;
    }

    private static SectionHintKind? KindOfMark(string prefix) => prefix.ToUpperInvariant() switch
    {
        "К" or "КМ" => SectionHintKind.Column,
        "Б" or "БМ" or "Р" => SectionHintKind.Beam,
        "П" or "ПМ" or "ПП" => SectionHintKind.Slab,
        "С" or "СМ" => SectionHintKind.Wall,
        "Ф" or "ФМ" or "ФП" or "ФПМ" => SectionHintKind.Foundation,
        _ => null
    };

    /// <summary>Сечение из текста: прямоугольник «400х400», круг «Ø500», труба, двутавр, толщина «h=200» / «толщиной 200 мм».</summary>
    private static (string Profile, int B, int H)? ParseSection(string text, SectionHintKind kind)
    {
        string lower = text.ToLowerInvariant();
        static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
        if (kind is SectionHintKind.Column or SectionHintKind.Beam)
        {
            var d = Diameter.Match(text);
            if (lower.Contains("труб") && d.Success) return ("pipe", I(d.Groups[1].Value), I(d.Groups[1].Value));
            var r = RectSection.Match(text);
            if ((lower.Contains("двутавр") || lower.Contains("i-")) && r.Success)
            {
                int a = I(r.Groups[1].Value), b = I(r.Groups[2].Value);
                return ("i_beam", Math.Min(a, b), Math.Max(a, b));
            }
            if (d.Success) return ("circle", I(d.Groups[1].Value), d.Groups[2].Success ? I(d.Groups[2].Value) : I(d.Groups[1].Value));
            if (r.Success) return ("rectangle", I(r.Groups[1].Value), I(r.Groups[2].Value));
            return null;
        }
        var t = ThicknessEq.Match(text);
        if (t.Success) return ("thickness", I(t.Groups[1].Value), I(t.Groups[1].Value));
        var w = ThicknessWords.Match(text);
        if (w.Success)
        {
            int v = I(w.Groups[1].Success ? w.Groups[1].Value : w.Groups[2].Value);
            return ("thickness", v, v);
        }
        return null;
    }

    private static void ReadNotes(PdfTextRun run, string text, ExtractedBlueprintAnnotations result, HashSet<string> notes, HashSet<string> marks, HashSet<string> rebar)
    {
        foreach (Match m in MarkAnywhere.Matches(text)) marks.Add(m.Value);
        foreach (Match m in Rebar.Matches(text)) rebar.Add($"А{m.Groups[2].Value}{(m.Groups[3].Success ? "С" : string.Empty)}");
        string lower = text.ToLowerInvariant();
        foreach (Match m in Concrete.Matches(text))
        {
            double v = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            bool context = lower.Contains("бетон") || lower.Contains("класс") || text.Trim().Length <= 6;
            if (context && v is >= 7.5 and <= 60 && result.ConcreteClass is null) result.ConcreteClass = "B" + m.Groups[2].Value.Replace('.', ',');
        }

        // Указания с ключевым словом конструкции и размером: «колонны 400х400», «плита толщиной 200 мм».
        var kw = KindWord.Match(text);
        if (!kw.Success || KindOf(kw.Value) is not { } kind) return;
        var tail = text[kw.Index..];
        if (ParseSection(tail, kind) is not { } section || section.B < 20 || section.B > 3000) return;
        var mark = MarkAnywhere.Match(text) is { Success: true } mm ? mm.Value : null;
        result.Sections.Add(new SectionHint(kind, mark, section.Profile, section.B, section.H, text));
        if (text.Length >= 6) notes.Add(text.Length > 220 ? text[..220] : text);
    }

    /// <summary>Строки спецификаций: марка в начале строки таблицы + сечение в той же строке правее («К1 … 400×400», «П1 … h=200»).</summary>
    private static void ReadSpecRows(PdfPageVectorContent page, ExtractedBlueprintAnnotations result, HashSet<string> marks)
    {
        foreach (var run in page.Runs.Where(r => r.IsHorizontal))
        {
            var m = MarkPrefix.Match(run.Text.Trim());
            if (!m.Success || KindOfMark(m.Groups[1].Value) is not { } kind) continue;
            string mark = m.Value;
            marks.Add(mark);
            var row = page.Runs.Where(r => r.IsHorizontal && r != run && Math.Abs(r.CenterY - run.CenterY) < 0.4 * run.FontSize &&
                                           r.X > run.X && r.X - run.X < 0.6 * page.Width)
                .OrderBy(r => r.X).Select(r => Normalize(r.Text)).ToList();
            string rowText = string.Join(" ", new[] { Normalize(run.Text) }.Concat(row));
            // Сечение — в ближайших ячейках строки (наименование может содержать слово конструкции без размеров).
            if (ParseSection(rowText[m.Length..], kind) is not { } section || section.B < 20 || section.B > 3000) continue;
            if (result.Sections.Any(s => s.Mark == mark && s.Kind == kind && s.B == section.B && s.H == section.H)) continue;
            result.Sections.Add(new SectionHint(kind, mark, section.Profile, section.B, section.H, rowText));
        }
    }
}
