#if DEBUG
using System.Globalization;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.Pdf;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест архитектурной подачи: 3D-рулетка (обмер, засечки 45°, капсула), закреплённые размеры (Undo/Redo, .aia v5),
/// выгрузка в DXF (слой DIMENSIONS, ALIGNED DIMENSION) и на листы ГОСТ (план и разрез), альфа рендера 4K.
/// </summary>
public static partial class CadSelfTestService
{
    private static async Task<int> CheckMeasurementAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        var (elements, groups, axes) = CreateFrameScene();
        var scene = new SceneStateService();
        scene.LoadScene(elements.Select(e => e.Clone()).ToList(), groups.Select(g => g.Clone()).ToList(), recordUndo: false, clearHistory: true, axes: axes.Select(a => a.Clone()).ToList());

        // ── Обмер (0, 0, 0) → (3000, 4000, 0) мм: L = 5000 мм, наклон atan(4/3) = 53,13°.
        var m = MeasurementService.Measure((0, 0, 0), (3.0, 4.0, 0));
        var (length, projections, slope) = MeasurementService.CapsuleLines(m);
        Check(report, ref failures, Math.Abs(m.Length - 5.0) < 1e-9 && Math.Abs(m.Dx - 3.0) < 1e-9 && Math.Abs(m.Dy - 4.0) < 1e-9 && m.Dz == 0 &&
                                    Math.Abs(m.SlopeDegrees - 53.130102354) < 1e-6 && length == "L = 5 000 мм" && projections.Contains("ΔY 4 000", StringComparison.Ordinal) && slope == "Наклон 53,13°",
            $"рулетка (0,0,0) → (3000,4000,0): {length} · {projections} · {slope}");

        // ── Засечки 45° по ГОСТ 2.307: линия с выступом за засечки, засечки под 45° к размерной линии.
        var perp = MeasurementService.Perpendicular((0.6, 0.8, 0), (0, 0, -1));
        var strokes = MeasurementService.Strokes((0, 0, 0), (3.0, 4.0, 0), perp, 0.2, 0.09);
        var line = strokes.First(s => !s.IsTick);
        var ticks = strokes.Where(s => s.IsTick).ToList();
        double TickAngle(DimensionStroke s)
        {
            var t = MeasurementService.Normalize(MeasurementService.Sub(s.B, s.A));
            return Math.Acos(Math.Clamp(Math.Abs(MeasurementService.Dot(t, (0.6, 0.8, 0))), 0, 1)) * 180 / Math.PI;
        }
        double lineLength = MeasurementService.Norm(MeasurementService.Sub(line.B, line.A));
        Check(report, ref failures, ticks.Count == 2 && ticks.All(t => Math.Abs(TickAngle(t) - 45) < 1e-6) && Math.Abs(lineLength - 5.18) < 1e-9 &&
                                    ticks.All(t => Math.Abs(MeasurementService.Norm(MeasurementService.Sub(t.B, t.A)) - 0.2) < 1e-9),
            $"3D-размер: размерная линия {lineLength * 1000:0} мм (выступ 2 × 90 мм), засечки 200 мм под {TickAngle(ticks[0]):0.00}° и {TickAngle(ticks[1]):0.00}°");

        // ── PinnedDimensions: закрепление — один шаг Undo, длина 5000 мм; Undo убирает размер, Redo возвращает.
        int depth = scene.UndoDepth;
        int events = 0;
        scene.DimensionsChanged += (_, _) => events++;
        var pinned = scene.AddPinnedDimension((0, 0, 0), (3.0, 4.0, 0));
        bool added = pinned is not null && scene.PinnedDimensions.Count == 1 && scene.UndoDepth == depth + 1 && Math.Abs(pinned.Length - 5.0) < 1e-9 &&
                     MeasurementService.LabelOf(pinned) == "5000";
        bool undone = scene.Undo() && scene.PinnedDimensions.Count == 0 && scene.UndoDepth == depth;
        bool redone = scene.Redo() && scene.PinnedDimensions.Count == 1 && scene.PinnedDimensions[0].Id == pinned?.Id;
        bool zero = scene.AddPinnedDimension((1, 1, 1), (1, 1, 1)) is null && scene.PinnedDimensions.Count == 1;
        Check(report, ref failures, added && undone && redone && zero && events >= 3,
            $"PinnedDimensions: размер {pinned?.Length * 1000:0} мм закреплён одним шагом Undo ({depth} → {depth + 1}), Undo убирает ({(undone ? "да" : "нет")}), " +
            $"Redo возвращает ({(redone ? "да" : "нет")}), нулевой размер отклонён, событий DimensionsChanged: {events}");

        // ── Удаление и новая сцена: удаление — шаг Undo, LoadScene(clearHistory) очищает, Undo новой сцены возвращает размеры.
        var second = scene.AddPinnedDimension((1.234, 0.5, -2.0), (5.0, 0.5, -2.0));
        bool removed = second is not null && scene.RemovePinnedDimension(second.Id) && scene.PinnedDimensions.Count == 1 && scene.Undo() && scene.PinnedDimensions.Count == 2;
        var keepScene = new SceneStateService();
        keepScene.LoadScene(elements.Select(e => e.Clone()).ToList(), groups.Select(g => g.Clone()).ToList(), recordUndo: false, clearHistory: true);
        keepScene.AddPinnedDimension((0, 0, 0), (1, 0, 0));
        keepScene.LoadScene(elements.Select(e => e.Clone()).ToList(), groups.Select(g => g.Clone()).ToList(), recordUndo: true, dimensions: []);
        bool replaced = keepScene.PinnedDimensions.Count == 0 && keepScene.Undo() && keepScene.PinnedDimensions.Count == 1;
        Check(report, ref failures, removed && replaced,
            $"удаление размера — шаг Undo (восстановлено: {(removed ? "да" : "нет")}); новая сцена сбрасывает размеры, Undo возвращает их вместе со сценой: {(replaced ? "да" : "нет")}");

        // ── .aia v5: закреплённые размеры сохраняются и открываются без потерь.
        string aiaPath = Path.Combine(directory, "measure_roundtrip.aia");
        var file = new SceneProjectFile
        {
            ProjectName = "Рулетка",
            Elements = scene.Elements.Select(e => e.Clone()).ToList(),
            BuildingGroups = scene.BuildingGroups.Select(g => g.Clone()).ToList(),
            PinnedDimensions = scene.PinnedDimensions.Select(d => d.Clone()).ToList()
        };
        await file.SaveAsync(aiaPath);
        var loaded = await SceneProjectFile.LoadAsync(aiaPath);
        var reopened = new SceneStateService();
        reopened.LoadScene(loaded.Elements, loaded.BuildingGroups, recordUndo: false, clearHistory: true, dimensions: loaded.PinnedDimensions);
        bool aiaOk = loaded.FormatVersion == SceneProjectFile.CurrentFormatVersion && SceneProjectFile.CurrentFormatVersion >= 5 &&
                     reopened.PinnedDimensions.Count == scene.PinnedDimensions.Count &&
                     reopened.PinnedDimensions.Zip(scene.PinnedDimensions).All(p => p.First.Id == p.Second.Id && p.First.Start.SequenceEqual(p.Second.Start) && p.First.End.SequenceEqual(p.Second.End)) &&
                     File.ReadAllText(aiaPath).Contains("\"pinnedDimensions\"", StringComparison.Ordinal);
        Check(report, ref failures, aiaOk, $".aia v{loaded.FormatVersion}: размеров {reopened.PinnedDimensions.Count} из {scene.PinnedDimensions.Count}, Id и точки совпадают");

        // ── DXF: ALIGNED DIMENSION на слое DIMENSIONS, определяющие точки 13/14 в Z-Up, значение 5000, блок изображения *D1.
        var dims = scene.PinnedDimensions.Select(d => d.Clone()).ToList();
        string dxfPath = Path.Combine(directory, "measure_dimensions.dxf");
        var dxf = await new CadExportService().ExportAsync(CadExportFormat.Dxf, dxfPath, elements.Select(e => e.Clone()).ToList(), groups, "Самопроверка · рулетка", axes, dims);
        var pairs = ReadDxfPairs(dxfPath);
        var dimension = DxfEntities(pairs, "ENTITIES").FirstOrDefault(e => e.Type == "DIMENSION" && e.Get(1) == "5000");
        bool layerDeclared = DxfEntities(pairs, "TABLES").Any(e => e.Type == "LAYER" && e.Get(2) == "DIMENSIONS");
        bool blockOk = dimension is not null && DxfEntities(pairs, "BLOCKS").Any(e => e.Type == "BLOCK" && e.Get(2) == dimension.Get(2));
        bool pointsOk = dimension is not null && Near(dimension, 13, 0, 0, 0) && Near(dimension, 14, 3, 0, 4);
        bool dxfOk = dimension is not null && dimension.Get(8) == "DIMENSIONS" && dimension.Get(70) == "1" && layerDeclared && blockOk && pointsOk &&
                     DxfEntities(pairs, "ENTITIES").Count(e => e.Type == "DIMENSION") == dims.Count;
        Check(report, ref failures, dxfOk,
            $"DXF: DIMENSION ×{DxfEntities(pairs, "ENTITIES").Count(e => e.Type == "DIMENSION")} (70 = 1, ALIGNED) на слое DIMENSIONS, текст «{dimension?.Get(1)}», блок {dimension?.Get(2)}, " +
            $"точки 13/14 = (0; 0; 0) → (3; 0; 4) м Z-Up: {(pointsOk ? "да" : "нет")} · {dxf.Details[0]}");

        // Собственный импорт DXF не превращает размеры в элементы: результат подъёма в 3D совпадает с файлом без размеров.
        string plainPath = Path.Combine(directory, "measure_plain.dxf");
        await new CadExportService().ExportAsync(CadExportFormat.Dxf, plainPath, elements.Select(e => e.Clone()).ToList(), groups, "Самопроверка · рулетка", axes);
        var withDims = CadDxfImporterService.LiftTo3D(await new CadDxfImporterService().LoadAsync(dxfPath), new DxfLiftOptions(FrameGroup));
        var plain = CadDxfImporterService.LiftTo3D(await new CadDxfImporterService().LoadAsync(plainPath), new DxfLiftOptions(FrameGroup));
        Check(report, ref failures, withDims.Elements.Count == plain.Elements.Count && withDims.Axes.Count == plain.Axes.Count,
            $"DXF Round-Trip с размерами: элементов {withDims.Elements.Count} (без размеров {plain.Elements.Count}), осей {withDims.Axes.Count} — размеры не искажают импорт");

        // ── Листы ГОСТ: размеры в проекции вида — разрез 1-1 (плоскость z = const) показывает 5000, план — проекцию 3766.
        var box = new SectionBox(-12, 12, -1.5, 10, -9, 0.05);
        string sectionPath = Path.Combine(directory, "measure_gost_section.pdf");
        var section = GostDrawingPdfExporter.Export(new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.Section, elements, groups, axes,
            "Самопроверка · рулетка", FrameGroup, 0, box, SectionFace.MaxZ, Dimensions: dims), sectionPath);
        string planPath = Path.Combine(directory, "measure_gost_plan.pdf");
        var plan = GostDrawingPdfExporter.Export(new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.FloorPlan, elements, groups, axes,
            "Самопроверка · рулетка", FrameGroup, 0, null, null, Dimensions: dims), planPath);
        var sectionTexts = PdfNativeContentExtractor.Read(sectionPath).Pages.SelectMany(p => p.Runs).Select(r => r.Text.Trim()).ToList();
        var planTexts = PdfNativeContentExtractor.Read(planPath).Pages.SelectMany(p => p.Runs).Select(r => r.Text.Trim()).ToList();
        var sloped = PdfNativeContentExtractor.Read(sectionPath).Pages.SelectMany(p => p.Runs).FirstOrDefault(r => r.Text.Trim() == "5000");
        Check(report, ref failures, section.PinnedDimensions == 2 && sectionTexts.Contains("5000") && sloped is not null && Math.Abs(sloped.AngleDeg - 53.13) < 1.0 &&
                                    plan.PinnedDimensions == 2 && planTexts.Contains("3766") && section.Details.Any(d => d.Contains("рулетки", StringComparison.Ordinal)),
            $"ГОСТ PDF: на разрезе {section.PinnedDimensions} размера, «5000» под углом {sloped?.AngleDeg.ToString("0.00", CultureInfo.InvariantCulture) ?? "—"}°; " +
            $"на плане {plan.PinnedDimensions} (проекция «3766»: {(planTexts.Contains("3766") ? "да" : "нет")}) — извлечено PdfNativeContentExtractor");
        await RenderPdfAsync(sectionPath, Path.ChangeExtension(sectionPath, ".png"), 2400);

        // Альбом: размеры на листах плана и разреза.
        string albumPath = Path.Combine(directory, "measure_album.pdf");
        var album = GostDrawingPdfExporter.ExportAlbum(new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.FloorPlan, elements, groups, axes,
            "Самопроверка · рулетка", FrameGroup, 0, box, SectionFace.MaxZ, Dimensions: dims), albumPath);
        Check(report, ref failures, album.PinnedDimensions == 4, $"альбом ГОСТ: размеров рулетки на листах плана и разреза — {album.PinnedDimensions}");

        // ── Рендер 4K с прозрачным фоном: разностное извлечение альфы по кадрам на чёрном и белом фоне.
        byte[] black = [0, 0, 0, 255, 240, 240, 240, 255, 60, 60, 60, 255, 128, 64, 32, 255];
        byte[] white = [255, 255, 255, 255, 240, 240, 240, 255, 187, 187, 187, 255, 128, 64, 32, 255];
        var alpha = Views.Viewport3DControl.ExtractAlpha(black, white);
        Check(report, ref failures, alpha[3] == 0 && alpha[7] == 255 && alpha[4] == 240 && Math.Abs(alpha[11] - 128) <= 1 && Math.Abs(alpha[8] - 120) <= 2 && alpha[15] == 255 && alpha[12] == 128,
            $"рендер 4K, прозрачный фон: небо α = {alpha[3]}, гипс α = {alpha[7]}, тень/стекло α = {alpha[11]} (цвет {alpha[8]}), цветной пиксель без искажений ({alpha[12]}, {alpha[13]}, {alpha[14]})");

        // ── Материалы макета White Clay: единый матовый гипс #F4F4F6, рёбра #2C2C2E, стекло — полупрозрачный гипс.
        var clay = ArchitecturalMaterials.GetClay(MaterialKind.Concrete);
        var clayGlass = ArchitecturalMaterials.GetClay(MaterialKind.Glass);
        bool sameForAll = Enum.GetValues<MaterialKind>().Where(k => k is not (MaterialKind.Void or MaterialKind.Glass or MaterialKind.Water) && !k.ToString().EndsWith("Cut", StringComparison.Ordinal) && k != MaterialKind.SectionCut)
            .All(k => ArchitecturalMaterials.GetClay(k).DiffuseColor == ArchitecturalMaterials.ClayColor);
        Check(report, ref failures, sameForAll && clay.DiffuseColor == new SharpDX.Color4(0xF4 / 255f, 0xF4 / 255f, 0xF6 / 255f, 1f) && clay.SpecularShininess <= 8 &&
                                    clayGlass.DiffuseColor.Alpha is > 0.2f and < 0.8f && ArchitecturalMaterials.ClayEdgeColor == Windows.UI.Color.FromArgb(255, 0x2C, 0x2C, 0x2E),
            "White Clay: все материалы — матовый гипс #F4F4F6 (бетон, дерево, сталь, кровля, отделка), стекло и вода — полупрозрачный гипс, рёбра #2C2C2E");
        return failures;
    }

    /// <summary>Пары «код / значение» ASCII DXF.</summary>
    private static List<(int Code, string Value)> ReadDxfPairs(string path)
    {
        var lines = File.ReadAllLines(path);
        var pairs = new List<(int, string)>(lines.Length / 2);
        for (int i = 0; i + 1 < lines.Length; i += 2)
            if (int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)) pairs.Add((code, lines[i + 1].Trim()));
        return pairs;
    }

    private sealed record DxfTestEntity(string Type, List<(int Code, string Value)> Pairs)
    {
        public string? Get(int code) => Pairs.FirstOrDefault(p => p.Code == code).Value;

        public double Number(int code) => double.Parse(Get(code) ?? "NaN", CultureInfo.InvariantCulture);
    }

    /// <summary>Объекты секции DXF (группы от «0 ТИП» до следующего «0»).</summary>
    private static List<DxfTestEntity> DxfEntities(List<(int Code, string Value)> pairs, string section)
    {
        var result = new List<DxfTestEntity>();
        bool inside = false;
        DxfTestEntity? current = null;
        for (int i = 0; i < pairs.Count; i++)
        {
            var (code, value) = pairs[i];
            if (code == 0 && value == "SECTION" && i + 1 < pairs.Count && pairs[i + 1].Code == 2)
            {
                inside = pairs[i + 1].Value == section;
                current = null;
                continue;
            }
            if (!inside) continue;
            if (code == 0)
            {
                if (value == "ENDSEC") { inside = false; current = null; continue; }
                current = new DxfTestEntity(value, []);
                result.Add(current);
            }
            else current?.Pairs.Add((code, value));
        }
        return result;
    }

    private static bool Near(DxfTestEntity e, int code, double x, double y, double z) =>
        Math.Abs(e.Number(code) - x) < 1e-6 && Math.Abs(e.Number(code + 10) - y) < 1e-6 && Math.Abs(e.Number(code + 20) - z) < 1e-6;
}
#endif
