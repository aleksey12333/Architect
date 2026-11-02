#if DEBUG
using System.IO.Compression;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Pdf;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест векторного слоя PDF (AIA_DEBUG_SELFTEST): Round-Trip листов ГОСТ через PdfNativeContentExtractor, «чужой» PDF
/// в стиле AutoCAD (WinAnsi + CP1251 без ToUnicode, посимвольный вывод, TJ, кривые Безье, повёрнутый текст, встроенное
/// изображение-скан), прямая компиляция 3D без ИИ и тени Г-образной плиты по реальному контуру.
/// </summary>
public static partial class CadSelfTestService
{
    private static readonly int[] DirectX = [3500, 3700, 6000, 3700, 3500];
    private static readonly int[] DirectZ = [1500, 4500, 4900, 1600];
    private static readonly double[] DirectLevels = [-1.83, -0.02, 2.83, 5.68, 8.53];

    private static string Steps(IEnumerable<int> s) => string.Join(", ", s);

    /// <summary>
    /// «Прямая компиляция 3D по параметрам (Без ИИ)»: шаги X 3500…, Z 1500…, 5 отметок (4 этажа) → JSON blueprint →
    /// компилятор → солвер. Проверяются габарит по осям 20.400 × 12.500 м, колонны во всех узлах на каждом этаже,
    /// балки, плиты, фундамент, парапет и оси.
    /// </summary>
    private static BlueprintDefinition DirectCompileDefinition() => new()
    {
        XStepsMm = [.. DirectX],
        ZStepsMm = [.. DirectZ],
        LevelElevationsM = [.. DirectLevels],
        ColumnProfile = ProfileTypes.Rectangle,
        ColumnWidthMm = 400, ColumnDepthMm = 400,
        BeamWidthMm = 300, BeamHeightMm = 500,
        SlabThicknessMm = 200, FoundationThicknessMm = 600,
        Roof = BlueprintRoofType.FlatWithParapet, ParapetThicknessMm = 250, ParapetHeightMm = 900,
        GroupName = "Прямая компиляция"
    };

    /// <summary>Путь приложения без нейросети: JSON таблицы → AiJsonExtractor → компилятор blueprint → солвер.</summary>
    private static SolverResult? CompileBlueprint(BlueprintDefinition d) =>
        AiJsonExtractor.TryExtract(d.ToSceneJson("selftest"), out var doc)
            ? new ParametricSolverService().Solve(new SolverInput { Document = doc, Mode = SolverMode.NewScene })
            : null;

    private static SolverResult? CheckDirectCompile(StringBuilder report, ref int failures)
    {
        var def = DirectCompileDefinition();
        var validation = def.Validate(true);
        // Прогрев JIT, затем замер (как в приложении: JSON → AiJsonExtractor → компилятор → солвер).
        CompileBlueprint(def);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = CompileBlueprint(def);
        sw.Stop();
        if (result is null || validation.Count > 0)
        {
            failures++;
            report.AppendLine($"  ОШИБКА: таблица не прошла проверку ({string.Join("; ", validation)}) или JSON не разобран");
            return null;
        }

        var numeric = result.GridAxes.Where(a => a.IsNumeric).OrderBy(a => a.Coordinate).ToList();
        var letters = result.GridAxes.Where(a => !a.IsNumeric).OrderBy(a => a.Coordinate).ToList();
        double spanX = numeric.Count > 0 ? numeric[^1].Coordinate - numeric[0].Coordinate : 0;
        double spanZ = letters.Count > 0 ? letters[^1].Coordinate - letters[0].Coordinate : 0;
        var columns = result.Elements.Where(e => e.Type == ElementTypes.Column).ToList();
        double colSpanX = columns.Max(c => c.X) - columns.Min(c => c.X), colSpanZ = columns.Max(c => c.Z) - columns.Min(c => c.Z);
        bool axesOk = numeric.Count == 6 && letters.Count == 5 && Math.Abs(spanX - 20.4) < 1e-6 && Math.Abs(spanZ - 12.5) < 1e-6 &&
                      Math.Abs(colSpanX - 20.4) < 1e-6 && Math.Abs(colSpanZ - 12.5) < 1e-6 &&
                      numeric.Zip(numeric.Skip(1), (a, b) => (int)Math.Round((b.Coordinate - a.Coordinate) * 1000)).SequenceEqual(DirectX) &&
                      letters.Zip(letters.Skip(1), (a, b) => (int)Math.Round((b.Coordinate - a.Coordinate) * 1000)).Reverse().SequenceEqual(DirectZ);
        if (!axesOk) failures++;
        report.AppendLine($"  {(axesOk ? "OK" : "ОШИБКА")}: оси {numeric.Count} × {letters.Count} ({string.Join(" ", numeric.Select(a => a.Label))} / {string.Join(" ", letters.Select(a => a.Label))}), " +
                          $"габарит по осям {spanX:0.000} × {spanZ:0.000} м, по центрам колонн {colSpanX:0.000} × {colSpanZ:0.000} м (ожидается 20.400 × 12.500)");

        var perLevel = new List<string>();
        bool levelsOk = true;
        for (int level = 0; level < 4; level++)
        {
            double elevation = DirectLevels[level], height = DirectLevels[level + 1] - elevation;
            var cols = columns.Where(c => c.LevelIndex == level).ToList();
            int beams = result.Elements.Count(e => e.Type == ElementTypes.Beam && e.LevelIndex == level);
            var plates = result.Elements.Where(e => e.LevelIndex == level && (e.Type == ElementTypes.Slab || e.Type == ElementTypes.Roof && e.IsFlatRoof)).ToList();
            bool nodes = cols.Count == 30 && cols.All(c => numeric.Any(a => Math.Abs(a.Coordinate - c.X) < 1e-6) && letters.Any(a => Math.Abs(a.Coordinate - c.Z) < 1e-6));
            bool heights = cols.All(c => Math.Abs(c.BaseY - elevation) < 1e-6 && Math.Abs(c.Height - (height - 0.2)) < 1e-6);
            bool slabTop = plates.Count == 1 && Math.Abs(plates[0].TopY - DirectLevels[level + 1]) < 1e-6 && Math.Abs(plates[0].Width - 20.8) < 1e-6 && Math.Abs(plates[0].Depth - 12.9) < 1e-6;
            bool ok = nodes && heights && beams == 49 && slabTop;
            levelsOk &= ok;
            perLevel.Add($"{level}: колонн {cols.Count}, балок {beams}, {(plates.FirstOrDefault()?.Type == ElementTypes.Roof ? "покрытие" : "плита")} верх {(plates.Count > 0 ? GostDrawingPdfExporter.Elevation(plates[0].TopY) : "—")}");
        }
        if (!levelsOk) failures++;
        report.AppendLine($"  {(levelsOk ? "OK" : "ОШИБКА")}: на всех уровнях колонны во всех 30 узлах (низ — отметка уровня, H = этаж − плита), 49 балок, плита по контуру 20.8 × 12.9 м:");
        report.AppendLine("    " + string.Join("; ", perLevel));

        var foundation = result.Elements.Where(e => e.Type == ElementTypes.Foundation).ToList();
        var parapets = result.Elements.Where(e => e.Type == ElementTypes.Wall && e.Name.StartsWith("Парапет", StringComparison.Ordinal)).ToList();
        bool restOk = foundation.Count == 1 && Math.Abs(foundation[0].TopY - DirectLevels[0]) < 1e-6 && Math.Abs(foundation[0].Height - 0.6) < 1e-6 &&
                      parapets.Count == 4 && parapets.All(p => Math.Abs(p.BaseY - DirectLevels[^1]) < 1e-6 && Math.Abs(p.Height - 0.9) < 1e-6 && Math.Abs(p.Depth - 0.25) < 1e-6) &&
                      result.Report.PhysicsErrors == 0 && sw.Elapsed.TotalMilliseconds < 200;
        if (!restOk) failures++;
        report.AppendLine($"  {(restOk ? "OK" : "ОШИБКА")}: фундаментная плита верх {GostDrawingPdfExporter.Elevation(foundation.FirstOrDefault()?.TopY ?? 0)} (600 мм), парапетов {parapets.Count} × 250 мм на {GostDrawingPdfExporter.Elevation(DirectLevels[^1])}, " +
                          $"ошибок аудита {result.Report.PhysicsErrors}; элементов {result.Elements.Count}, время {sw.Elapsed.TotalMilliseconds:0.0} мс без запросов к нейросети");
        return result;
    }

    /// <summary>
    /// Векторный слой PDF: листы ГОСТ тестового каркаса и прямой компиляции читаются PdfNativeContentExtractor; шаги осей,
    /// отметки, марки осей и сечения должны совпасть с исходными данными. Плюс «чужой» PDF и скан без текста.
    /// </summary>
    private static async Task<int> CheckPdfExtractorAsync(string directory, StringBuilder report, SolverResult? compiled)
    {
        int failures = 0;
        bool Check(bool ok, string text)
        {
            if (!ok) failures++;
            report.AppendLine($"  {(ok ? "OK" : "ОШИБКА")}: {text}");
            return ok;
        }
        string Labels(ExtractedBlueprintAnnotations a, bool numeric) =>
            string.Join(" ", a.AxisLabels.Where(l => l.IsNumeric == numeric).Select(l => l.Label).Distinct().OrderBy(l => int.TryParse(l, out int n) ? n : 0).ThenBy(l => l, StringComparer.Ordinal));
        string Levels(ExtractedBlueprintAnnotations a) => string.Join("; ", a.LevelsM.Select(ExtractedBlueprintAnnotations.ElevationText));

        // 1) План каркаса А3 тестовой сцены: 4 × 3 оси с шагом 6000.
        var frame = BlueprintAnnotationAnalyzer.Extract(Path.Combine(directory, "gost_frame_A3.pdf"));
        Check(frame.XStepsMm.SequenceEqual([6000, 6000, 6000]) && frame.ZStepsMm.SequenceEqual([6000, 6000]) && frame.XChain?.ConfirmedByOverall == true &&
              frame.XAxisLabels.SequenceEqual(["1", "2", "3", "4"]) && frame.ZAxisLabels.SequenceEqual(["А", "Б", "В"]) && frame.LevelsM.Contains(2.83),
            $"gost_frame_A3.pdf — X: {Steps(frame.XStepsMm)} (общий размер {(frame.XChain?.ConfirmedByOverall == true ? "подтверждён" : "нет")}), Z: {Steps(frame.ZStepsMm)}, " +
            $"оси {Labels(frame, true)} / {Labels(frame, false)}, отметки {Levels(frame)}; надписей {frame.TextRuns}, отрезков {frame.Segments}, окружностей {frame.Circles}, {frame.ElapsedMs:0} мс");

        // 2) Разрез А3: высотные отметки всех уровней и цепочка цифровых осей под разрезом.
        var section = BlueprintAnnotationAnalyzer.Extract(Path.Combine(directory, "gost_section_A3.pdf"));
        double[] expectedMarks = [-0.62, -0.02, 2.83, 5.68, 8.53];
        Check(expectedMarks.All(m => section.LevelsM.Any(l => Math.Abs(l - m) < 1e-6)) && section.XStepsMm.SequenceEqual([6000, 6000, 6000]) &&
              section.AxisLabels.Where(l => l.IsNumeric).Select(l => l.Label).Distinct().Count() == 4,
            $"gost_section_A3.pdf — отметки {Levels(section)}, цепочка {Steps(section.XStepsMm)}, оси {Labels(section, true)}");

        // 3) Спецификация листа А2 (этаж 2, все колонны 400×400): марки и сечения из строк таблицы.
        var spec = BlueprintAnnotationAnalyzer.Extract(Path.Combine(directory, "gost_frame_A2.pdf"));
        var column = spec.Best(SectionHintKind.Column);
        var beam = spec.Best(SectionHintKind.Beam);
        var slab = spec.Best(SectionHintKind.Slab);
        Check(column is { Mark: "К1", B: 400, H: 400, Profile: "rectangle" } && beam is { B: 300, H: 500 } && slab is { B: 200 } && spec.Marks.Contains("К1") && spec.Marks.Contains("П1"),
            $"gost_frame_A2.pdf — спецификация: {string.Join("; ", spec.Sections.Select(s => $"{s.Mark ?? "?"} {ExtractedBlueprintAnnotations.SectionText(s)}"))}");

        // 4) Цикл «таблица → 3D → лист ГОСТ → векторный слой → таблица» для прямой компиляции.
        if (compiled is not null)
        {
            var group = compiled.Groups[0];
            string planPath = Path.Combine(directory, "direct_frame_A3.pdf"), cutPath = Path.Combine(directory, "direct_section_A3.pdf");
            GostDrawingPdfExporter.Export(new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.FrameScheme, compiled.Elements, compiled.Groups, compiled.GridAxes,
                "Самопроверка · прямая компиляция", group.Id, 0, null, null), planPath);
            var cut = GostDrawingPdfExporter.Export(new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.Section, compiled.Elements, compiled.Groups, compiled.GridAxes,
                "Самопроверка · прямая компиляция", group.Id, 0, null, null), cutPath);
            await RenderPdfAsync(planPath, Path.ChangeExtension(planPath, ".png"), 3200);
            await RenderPdfAsync(cutPath, Path.ChangeExtension(cutPath, ".png"), 3200);
            var plan = BlueprintAnnotationAnalyzer.Extract(planPath);
            var cutFacts = BlueprintAnnotationAnalyzer.Extract(cutPath);
            var back = BlueprintDefinition.FromAnnotations(plan, "Round-Trip");
            Check(plan.XStepsMm.SequenceEqual(DirectX) && plan.ZStepsMm.SequenceEqual(DirectZ) && back.XStepsMm.Sum() == 20400 && back.ZStepsMm.Sum() == 12500 &&
                  plan.XAxisLabels.SequenceEqual(["1", "2", "3", "4", "5", "6"]) && plan.ZAxisLabels.SequenceEqual(["А", "Б", "В", "Г", "Д"]),
                $"direct_frame_A3.pdf — X: {Steps(plan.XStepsMm)} (Σ {back.XStepsMm.Sum()}), Z: {Steps(plan.ZStepsMm)} (Σ {back.ZStepsMm.Sum()}), оси {string.Join(" ", plan.XAxisLabels)} / {string.Join(" ", plan.ZAxisLabels)}");
            Check(DirectLevels.All(m => cutFacts.LevelsM.Any(l => Math.Abs(l - m) < 1e-6)) && cut.Occlusion is { Verified: true },
                $"direct_section_A3.pdf — отметки {Levels(cutFacts)}; окклюзия: обрезано {cut.Occlusion?.ClippedSegments ?? 0} линий заднего плана");
            string block = plan.ToPromptBlock();
            Check(block.Contains("ТОЧНЫЕ ДАННЫЕ, ИЗВЛЕЧЕННЫЕ ИЗ ВЕКТОРНОГО СЛОЯ PDF", StringComparison.Ordinal) && block.Contains("3500 · 3700 · 6000 · 3700 · 3500", StringComparison.Ordinal),
                $"блок промпта ИИ: {block.Split('\n').Length} строк, цепочка X и марки осей включены");
        }

        // 5) «Чужой» PDF (AutoCAD-стиль): WinAnsi и CP1251 без ToUnicode, посимвольный вывод, TJ, повёрнутые размеры,
        //    окружности кривыми Безье, FlateDecode; вторая страница — скан (встроенное изображение без текста).
        string foreignPath = Path.Combine(directory, "foreign_autocad.pdf");
        await File.WriteAllBytesAsync(foreignPath, BuildForeignPdf());
        var native = PdfNativeContentExtractor.Read(foreignPath);
        var foreign = BlueprintAnnotationAnalyzer.Analyze(native);
        var fc = foreign.Best(SectionHintKind.Column);
        var fs = foreign.Best(SectionHintKind.Slab);
        Check(foreign.XStepsMm.SequenceEqual([6000, 7200, 6000]) && foreign.XChain?.ConfirmedByOverall == true && foreign.ZStepsMm.SequenceEqual([6600, 4800]) &&
              foreign.XAxisLabels.SequenceEqual(["1", "2", "3", "4"]) && foreign.ZAxisLabels.SequenceEqual(["А", "Б", "В"]) &&
              foreign.LevelsM.SequenceEqual([0.0, 3.3, 6.6]) && fc is { Mark: "К1", B: 400, H: 400 } && fs is { B: 220 } &&
              foreign.ConcreteClass == "B25" && foreign.RebarClasses.Contains("А500С"),
            $"foreign_autocad.pdf — X: {Steps(foreign.XStepsMm)}, Z: {Steps(foreign.ZStepsMm)}, оси {string.Join(" ", foreign.XAxisLabels)} / {string.Join(" ", foreign.ZAxisLabels)}, " +
            $"отметки {Levels(foreign)}, колонны {(fc is null ? "—" : ExtractedBlueprintAnnotations.SectionText(fc))}, плита {(fs is null ? "—" : fs.B + " мм")}, бетон {foreign.ConcreteClass ?? "—"}, арматура {string.Join(",", foreign.RebarClasses)}");
        Check(native.Pages.Count == 2 && !native.Pages[1].HasTextLayer && native.Warnings.Count == 0,
            $"страница-скан без текстового слоя: надписей {native.Pages.ElementAtOrDefault(1)?.Runs.Count ?? -1}, предупреждений {native.Warnings.Count} — остаётся растровый рендер 2000 px");
        return failures;
    }

    /// <summary>Тени Г-образной плиты: тень повторяет реальный контур (во «внутреннем углу» тени нет), объединение = растр.</summary>
    private static int CheckPolygonShadow(StringBuilder report)
    {
        int failures = 0;
        var slab = new ArchitecturalElement { Id = "shadow_L", Type = ElementTypes.Slab, Name = "Тест: Г-образная плита" };
        slab.SetSize(8, 0.3, 8);
        slab.PlaceOnBase(6);
        slab.Polygon2D = [new(-4, -4), new(4, -4), new(4, -1), new(-1, -1), new(-1, 4), new(-4, 4)];
        var sun = SolarShadowService.Compute(13, SolarSeason.Summer);
        var shadow = SolarShadowService.Build([slab], sun);
        double sx = sun.DirX / sun.DirY, sz = sun.DirZ / sun.DirY, t = slab.BaseY + slab.Height / 2 - SolarShadowService.GroundY;
        bool InShadow(double lx, double lz) => shadow.Silhouettes.Any(p => SceneStateService.PointInPolygon(slab.X + lx - sx * t, slab.Z + lz - sz * t, p));
        bool notch = !InShadow(2, 2) && InShadow(-2.5, 2) && InShadow(2, -2.5) && InShadow(-2.5, -2.5);
        var all = shadow.Silhouettes.SelectMany(p => p).ToList();
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minZ = all.Min(p => p.Z), maxZ = all.Max(p => p.Z);
        const double cell = 0.04;
        int covered = 0;
        for (double x = minX + cell / 2; x < maxX; x += cell)
            for (double z = minZ + cell / 2; z < maxZ; z += cell)
                if (shadow.Silhouettes.Any(p => SceneStateService.PointInPolygon(x, z, p))) covered++;
        double raster = covered * cell * cell;
        double hull = Math.Abs(SolarShadowService.SignedArea(SolarShadowService.ConvexHull(all)));
        bool ok = notch && shadow.Silhouettes.Count >= 2 && Math.Abs(shadow.Area - raster) / raster < 0.01 && shadow.Area < hull - 8;
        if (!ok) failures++;
        report.AppendLine($"  {(ok ? "OK" : "ОШИБКА")}: Г-образная плита на +6,000 в {SolarShadowService.TimeText(13)}: частей контура {shadow.Silhouettes.Count}, площадь тени {shadow.Area:0.00} м² " +
                          $"(растр {raster:0.00} м², выпуклая оболочка {hull:0.00} м²), во внутреннем углу тени {(notch ? "нет" : "ЕСТЬ")}");
        return failures;
    }

    /// <summary>PDF «как из AutoCAD»: простые шрифты без ToUnicode (WinAnsi и CP1251), текст по символам, TJ, повороты, Безье.</summary>
    private static byte[] BuildForeignPdf()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("0.###", inv);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1251 = Encoding.GetEncoding(1251);
        string Hex(string s) => "<" + Convert.ToHexString(cp1251.GetBytes(s)) + ">";

        var c = new StringBuilder("0.5 w\n");
        void Line(double x1, double y1, double x2, double y2) => c.Append($"{F(x1)} {F(y1)} m {F(x2)} {F(y2)} l S\n");
        void Circle(double cx, double cy, double r)
        {
            double k = r * 0.5523;
            c.Append($"{F(cx + r)} {F(cy)} m {F(cx + r)} {F(cy + k)} {F(cx + k)} {F(cy + r)} {F(cx)} {F(cy + r)} c ")
             .Append($"{F(cx - k)} {F(cy + r)} {F(cx - r)} {F(cy + k)} {F(cx - r)} {F(cy)} c ")
             .Append($"{F(cx - r)} {F(cy - k)} {F(cx - k)} {F(cy - r)} {F(cx)} {F(cy - r)} c ")
             .Append($"{F(cx + k)} {F(cy - r)} {F(cx + r)} {F(cy - k)} {F(cx + r)} {F(cy)} c h S\n");
        }
        void Text(string font, double x, double y, string pdfString, bool rotated = false) =>
            c.Append($"BT /{font} 10 Tf {(rotated ? "0 1 -1 0" : "1 0 0 1")} {F(x)} {F(y)} Tm {pdfString} Tj ET\n");

        // Цифровые оси: линии, кружки и марки (WinAnsi, ширина глифа по умолчанию 500 → 5 pt на символ).
        double[] ax = [100, 270, 474, 644];
        for (int i = 0; i < ax.Length; i++)
        {
            Line(ax[i], 80, ax[i], 740);
            Circle(ax[i], 760, 10);
            Text("F1", ax[i] - 2.5, 756.5, $"({i + 1})");
        }
        // Буквенные оси: кириллица в CP1251 без ToUnicode.
        double[] ay = [200, 380, 520];
        string[] letters = ["А", "Б", "В"];
        for (int i = 0; i < ay.Length; i++)
        {
            Line(80, ay[i], 700, ay[i]);
            Circle(50, ay[i], 10);
            Text("F2", 47.5, ay[i] - 3.5, Hex(letters[i]));
        }
        // Горизонтальная цепочка: «6000» одним Tj, «7200» посимвольно (как AutoCAD), «6000» через TJ; общий размер 19200 через TJ.
        Line(100, 57, 644, 57);
        Text("F1", 175, 60, "(6000)");
        for (int k = 0; k < 4; k++) Text("F1", 362 + k * 5, 60, $"({"7200"[k]})");
        c.Append("BT /F1 10 Tf 1 0 0 1 549 60 Tm [(60) -5 (00)] TJ ET\n");
        Line(100, 27, 644, 27);
        c.Append("BT /F1 10 Tf 1 0 0 1 359.5 30 Tm [(19) -10 (200)] TJ ET\n");
        // Вертикальная цепочка между буквенными осями — текст повёрнут на 90°, линия справа от базовой.
        Line(63, 200, 63, 520);
        Text("F1", 60, 280, "(6600)", rotated: true);
        Text("F1", 60, 440, "(4800)", rotated: true);
        // Высотные отметки (± — байт 0xB1 WinAnsi).
        Text("F1", 720, 600, "(+3,300)");
        Text("F1", 720, 300, "(\\2610,000)");
        Text("F1", 720, 250, "(+6,600)");
        // Общие указания в CP1251.
        Text("F2", 750, 700, Hex("Колонны К1 сечением 400х400 мм"));
        Text("F2", 750, 680, Hex("Плита перекрытия толщиной 220 мм"));
        Text("F2", 750, 660, Hex("Бетон класса B25, арматура А500С"));

        byte[] page1 = Encoding.Latin1.GetBytes(c.ToString());
        byte[] page1Z;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(page1);
            page1Z = ms.ToArray();
        }
        // Скан: только встроенное изображение 2 × 2 (байты данных — двоичные).
        byte[] page2 = [.. Encoding.Latin1.GetBytes("q 400 0 0 300 100 100 cm BI /W 2 /H 2 /CS /G /BPC 8 ID "), 0x00, 0xFF, 0x45, 0x49, .. Encoding.Latin1.GetBytes(" EI Q\n")];

        var objects = new List<byte[]>
        {
            Encoding.Latin1.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.Latin1.GetBytes("<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 /MediaBox [0 0 1190 842] >>"),
            Encoding.Latin1.GetBytes("<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 7 0 R >>"),
            Encoding.Latin1.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Encoding.Latin1.GetBytes("<< /Type /Font /Subtype /TrueType /BaseFont /ArialMT >>"),
            Encoding.Latin1.GetBytes("<< /Type /Page /Parent 2 0 R /Resources << >> /Contents 8 0 R >>"),
            StreamObject($"<< /Length {page1Z.Length} /Filter /FlateDecode >>", page1Z),
            StreamObject($"<< /Length {page2.Length} >>", page2)
        };
        static byte[] StreamObject(string dict, byte[] data) =>
            [.. Encoding.Latin1.GetBytes(dict + "\nstream\n"), .. data, .. Encoding.Latin1.GetBytes("\nendstream")];
        using var pdf = new MemoryStream();
        void W(string s) => pdf.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.4\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            W($"{i + 1} 0 obj\n");
            pdf.Write(objects[i]);
            W("\nendobj\n");
        }
        long xref = pdf.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }
}
#endif
