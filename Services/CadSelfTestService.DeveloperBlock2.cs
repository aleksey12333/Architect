#if DEBUG
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Verification;
using Microsoft.Extensions.DependencyInjection;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест блока девелопера 2: конвейер «План → BIM» (зашумлённый векторный план с отклонением 1,8°, зазором в углу 120 мм,
/// дублем и проёмами; синтетический растровый план с окнами, дверями и пролётом больше 6 м → сетка колонн), шлюз
/// достоверности с листом правок (частичное и полное принятие, ровно одна транзакция, Undo/Redo до бита, откат генерации)
/// и вложения запроса (JPEG авто-снимка, вставка в контекст и в поле веб-проектировщика без буфера обмена).
/// </summary>
public static partial class CadSelfTestService
{
    public const string GateGroup = "Корпус · генератор";

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>
    /// Зашумлённый план 12 × 9 м (оси, м): нижняя стена повёрнута на 1,8°, верхняя не доходит до грани левой на 120 мм,
    /// наложенный дубль правой стены, несущая стена по оси 6 м и перегородка 100 мм; два окна, окно в торце, наружная и внутренняя двери.
    /// </summary>
    public static RecognizedPlan CreateNoisyPlan()
    {
        double tilt = Math.Tan(1.8 * Math.PI / 180) * 6;
        var plan = new RecognizedPlan { Name = "Зашумлённый план 12×9.dxf", Source = PlanSourceKind.Vector, ScaleSource = "м" };
        plan.Walls.Add(new PlanWall(0, -tilt, 12, tilt, 0.3));
        plan.Walls.Add(new PlanWall(12, 0, 12, 9, 0.3));
        plan.Walls.Add(new PlanWall(12, 9, 0.15 + 0.12, 9, 0.3));
        plan.Walls.Add(new PlanWall(0, 9, 0, 0, 0.3));
        plan.Walls.Add(new PlanWall(12.01, 1, 12.01, 7, 0.3));
        plan.Walls.Add(new PlanWall(6, 0, 6, 9, 0.2));
        plan.Walls.Add(new PlanWall(6, 4.5, 12, 4.5, 0.1));
        plan.Openings.Add(new PlanOpening(3, 0, 1.5, PlanOpeningKind.Window));
        plan.Openings.Add(new PlanOpening(9, 9, 1.5, PlanOpeningKind.Window));
        plan.Openings.Add(new PlanOpening(0, 4.5, 1.2, PlanOpeningKind.Window));
        plan.Openings.Add(new PlanOpening(9, 0, 1.0, PlanOpeningKind.Door));
        plan.Openings.Add(new PlanOpening(6, 2, 0.9, PlanOpeningKind.Door));
        return plan;
    }

    /// <summary>
    /// Синтетический растровый план 16 × 10 м, 20 мм/пикс: наружные стены 300 мм, несущая 200 мм по оси x = 10 м (зал 10 × 10 м —
    /// пролёт больше 6 м), перегородка 100 мм; 4 окна (тонкие линии в разрыве), 3 двери (пустой разрыв и дуга открывания),
    /// размерные линии и подписи — тонкие штрихи, которые трассировка отсеивает.
    /// </summary>
    public static RasterPlanTracer.RasterImage CreateDemoPlanRaster()
    {
        const double px = 50; // пикселей на метр (20 мм/пикс)
        const int margin = 70;
        int w = (int)(16 * px) + 2 * margin, h = (int)(10 * px) + 2 * margin;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (250, 250, 250, 255);
        void Rect(double x0, double y0, double x1, double y1, byte v = 28)
        {
            // Координаты плана, м (y вверх) → пиксели (y вниз).
            int a = margin + (int)Math.Round(Math.Min(x0, x1) * px), b = margin + (int)Math.Round(Math.Max(x0, x1) * px);
            int c = h - margin - (int)Math.Round(Math.Max(y0, y1) * px), d = h - margin - (int)Math.Round(Math.Min(y0, y1) * px);
            for (int y = Math.Max(0, c); y < Math.Min(h, d); y++)
                for (int x = Math.Max(0, a); x < Math.Min(w, b); x++)
                {
                    int k = (y * w + x) * 4;
                    (bgra[k], bgra[k + 1], bgra[k + 2]) = (v, v, v);
                }
        }
        void HLine(double x0, double x1, double y) => Rect(x0, y, x1, y + 1 / px, 90);
        void VLine(double x, double y0, double y1) => Rect(x, y0, x + 1 / px, y1, 90);
        // Наружные стены с проёмами (окна — разрывы с тремя тонкими линиями, двери — пустые разрывы).
        foreach (var (a, b) in new[] { (0.0, 3.0), (4.5, 12.0), (13.0, 16.0) }) Rect(a, 0, b, 0.3);
        foreach (var (a, b) in new[] { (0.0, 4.0), (5.5, 12.0), (13.5, 16.0) }) Rect(a, 9.7, b, 10);
        foreach (var (a, b) in new[] { (0.0, 4.0), (5.2, 10.0) }) Rect(0, a, 0.3, b);
        Rect(15.7, 0, 16, 10);
        // Несущая стена 200 мм (дверь 900 мм) и перегородка 100 мм (дверь 900 мм).
        foreach (var (a, b) in new[] { (0.0, 2.0), (2.9, 10.0) }) Rect(9.9, a, 10.1, b);
        foreach (var (a, b) in new[] { (10.0, 12.5), (13.4, 16.0) }) Rect(a, 4.95, b, 5.05);
        foreach (var (a, b, y) in new[] { (3.0, 4.5, 0.0), (4.0, 5.5, 9.7), (12.0, 13.5, 9.7) })
            foreach (double t in new[] { 0.05, 0.15, 0.25 }) HLine(a, b, y + t);
        foreach (double t in new[] { 0.05, 0.15, 0.25 }) VLine(t, 4.0, 5.2);
        // Дуга открывания наружной двери и размерные линии (тонкие).
        for (int s = 0; s <= 90; s++)
        {
            double ang = s * Math.PI / 180;
            Rect(12 + Math.Cos(ang) * 1.0, 0.3 + Math.Sin(ang) * 1.0, 12 + Math.Cos(ang) * 1.0 + 1 / px, 0.3 + Math.Sin(ang) * 1.0 + 1 / px, 120);
        }
        HLine(0, 16, -0.8);
        VLine(-0.8, 0, 10);
        foreach (double x in new[] { 0.0, 10.0, 16.0 }) VLine(x, -1.0, -0.6);
        return new RasterPlanTracer.RasterImage(bgra, w, h);
    }

    /// <summary>PNG синтетического плана (окно проверки, вложения, съёмка).</summary>
    public static async Task<byte[]> DemoPlanPngAsync()
    {
        var image = CreateDemoPlanRaster();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)image.Width, (uint)image.Height, 96, 96, image.Bgra);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    public static async Task<string> WriteDemoPlanAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        await File.WriteAllBytesAsync(path, await DemoPlanPngAsync());
        return path;
    }

    /// <summary>
    /// «Ответ внешнего генератора» с дефектами: безбалочный каркас 12 × 12 м, 4 этажа, плиты 160 мм, центральная колонна
    /// 200 × 200 мм (перегрузка K_исп &gt; 1.2 и продавливание плиты).
    /// </summary>
    public static (List<ArchitecturalElement> Elements, List<BuildingGroup> Groups, List<StructuralGridAxis> Axes) CreateGateDefectScene()
    {
        const string g = GateGroup;
        var list = new List<ArchitecturalElement>();
        int n = 0;
        ArchitecturalElement Add(string type, string name, double x, double baseY, double z, double w, double hgt, double d, int level = 0)
        {
            var e = new ArchitecturalElement { Id = $"el_gate{++n:D3}", Type = type, Name = name, BuildingGroupId = g, LevelIndex = level, Material = "concrete" };
            e.SetSize(w, hgt, d);
            e.SetPosition(x, baseY + hgt / 2, z);
            list.Add(e);
            return e;
        }
        double[] xs = [0, 6, 12];
        double[] zs = [0, 6, 12];
        const double slab = 0.16, colH = 3.0 - slab;
        Add(ElementTypes.Foundation, "Фундаментная плита", 6, -0.6, 6, 13.2, 0.6, 13.2);
        const int storeys = 4;
        for (int level = 0; level < storeys; level++)
        {
            double b = level * 3.0;
            bool top = level == storeys - 1;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    bool center = i == 1 && j == 1;
                    double s = center ? 0.2 : 0.4;
                    Add(ElementTypes.Column, $"Колонна {i + 1}/{StructuralAxesBuilderLetter(j)} · эт. {level + 1}", xs[i], b, zs[j], s, colH, s, level);
                }
            var plate = Add(top ? ElementTypes.Roof : ElementTypes.Slab, top ? "Плоская кровля" : $"Плита перекрытия {level + 1}", 6, b + colH, 6, 13.2, slab, 13.2, level);
            if (top) plate.RoofShape = RoofShapes.Flat;
        }
        var axes = new List<StructuralGridAxis>();
        for (int i = 0; i < 3; i++) axes.Add(new StructuralGridAxis((i + 1).ToString(Inv), StructuralGridAxis.AlongZ, xs[i], g));
        for (int j = 0; j < 3; j++) axes.Add(new StructuralGridAxis(StructuralAxesBuilderLetter(j), StructuralGridAxis.AlongX, zs[j], g));
        return (list, [new BuildingGroup(g, "office", "#BF5AF2")], axes);
    }

    private static async Task<int> CheckDeveloperBlock2Async(string directory, StringBuilder report)
    {
        int failures = 0;
        var inv = CultureInfo.InvariantCulture;

        // ═══ 1. Зашумлённый векторный план: Snap & Ortho, замыкание узлов, дубли, конструктивная триада ═══
        var noisy = CreateNoisyPlan();
        var built = PlanToBimPipelineService.Run(noisy, new PlanToBimOptions { Storeys = 2, GroupId = "План · тест" });
        var s = built.Normalization;
        var walls = built.Walls;
        bool ortho = walls.All(w => w.IsOrthogonal);
        var bottom = walls.FirstOrDefault(w => w.IsHorizontal && Math.Abs(w.Y1) < 0.01);
        var top = walls.FirstOrDefault(w => w.IsHorizontal && Math.Abs(w.Y1 - 9) < 0.01);
        var left = walls.FirstOrDefault(w => w.IsVertical && Math.Abs(w.X1) < 0.01);
        // Угол замкнут: верхняя стена дотянута до наружной грани левой (−0,15), левая — до внутренней грани верхней (8,85).
        bool corner = top is not null && left is not null && Math.Abs(Math.Min(top.X1, top.X2) + 0.15) < 1e-6 && Math.Abs(Math.Max(left.Y1, left.Y2) - 8.85) < 1e-6;
        bool merged = walls.Count(w => w.IsVertical && Math.Abs(w.X1 - 12) < 0.05) == 1 && s.DuplicatesMerged >= 1;
        var elements = built.Elements;
        var hosted = elements.Where(e => ElementTypes.IsHostedType(e.Category)).ToList();
        bool openingsOk = hosted.Count > 0 && hosted.All(o => o.HostElementId is { } hid && elements.FirstOrDefault(e => e.Id == hid) is { Category: BimCategory.Wall } host &&
            Math.Abs(ArchitecturalElement.RotateY(o.X - host.X, o.Z - host.Z, -host.RotationY).X) + o.Width / 2 <= host.Width / 2 + 1e-6 && o.BaseY >= host.BaseY - 1e-6 && o.TopY <= host.TopY + 1e-6);
        bool triad = elements.Count(e => e.Category == BimCategory.Foundation) == 1 && elements.Count(e => e.Category == BimCategory.Slab) == 1 &&
                     elements.Count(e => e.Category == BimCategory.Roof) == 1 && built.ExteriorWalls == 4 && built.InteriorWalls == 2;
        Check(report, ref failures, ortho && Math.Abs(s.MaxCorrectionDeg - 1.8) < 1e-6 && s.Orthogonalized >= 1 && bottom is not null && corner && Math.Abs(s.MaxGapClosedMm - 120) < 0.5 &&
                                    merged && triad && openingsOk && built.PhysicsErrors == 0 && built.MaxUtilization <= 1.0 && built.IsClosedContour && built.IsStructurallySound,
            string.Create(inv, $"план → BIM, зашумлённый план 12 × 9 м: выпрямлено стен {s.Orthogonalized} (отклонение {s.MaxCorrectionDeg:0.0}° → 0°), узлов замкнуто {s.CornersClosed} ") +
            string.Create(inv, $"(зазор {s.MaxGapClosedMm:0} мм, верхняя стена до наружной грани левой), дублей слито {s.DuplicatesMerged}; наружных стен {built.ExteriorWalls}, внутренних {built.InteriorWalls}, ") +
            $"фундамент + перекрытие + покрытие, проёмов в стенах-хостах {hosted.Count}; K_исп max {built.MaxUtilization.ToString("0.00", inv)}, без опоры {built.PhysicsErrors}, " +
            $"армирование {built.RebarCages} из {built.RebarCandidates} каркасов, {(built.RebarKg / 1000).ToString("0.00", inv)} т");

        // ═══ 2. Растровый план: трассировка стен и проёмов, масштаб, сетка колонн в пролёте 10 м ═══
        var raster = CreateDemoPlanRaster();
        var traced = RasterPlanTracer.Trace(raster, "Синтетический план 16×10.png");
        int windows = traced.Openings.Count(o => o.Kind == PlanOpeningKind.Window), doors = traced.Openings.Count(o => o.Kind == PlanOpeningKind.Door);
        var rasterBuilt = PlanToBimPipelineService.Run(traced, new PlanToBimOptions { Storeys = 2, GroupId = "План · растр" });
        var calibrated = RasterPlanTracer.Trace(raster, "Синтетический план 16×10.png", calibrationWidthMm: 16000);
        double widthM = rasterBuilt.WidthM, depthM = rasterBuilt.DepthM;
        bool columnsInHall = rasterBuilt.Elements.Where(e => e.Category == BimCategory.Column && e.LevelIndex == 0).Any(c => c.X < 0);
        bool rasterOk = Math.Abs(traced.MmPerPixel - 20) < 0.01 && Math.Abs(calibrated.MmPerPixel - 20) < 0.05 && windows == 4 && doors == 3 &&
                        rasterBuilt.ExteriorWalls == 4 && rasterBuilt.InteriorWalls == 2 && Math.Abs(widthM - 16) < 0.03 && Math.Abs(depthM - 10) < 0.03 &&
                        rasterBuilt.Columns >= 2 && columnsInHall && rasterBuilt.PhysicsErrors == 0 && rasterBuilt.MaxUtilization <= 1.0 && rasterBuilt.IsStructurallySound &&
                        rasterBuilt.Axes.Count >= 5;
        Check(report, ref failures, rasterOk,
            $"план → BIM, растровый план {raster.Width}×{raster.Height} px: масштаб {traced.MmPerPixel.ToString("0.0", inv)} мм/пикс ({traced.ScaleSource}; калибровка 16 000 мм → {calibrated.MmPerPixel.ToString("0.00", inv)}), " +
            $"стен {rasterBuilt.ExteriorWalls} + {rasterBuilt.InteriorWalls}, окон {windows}, дверей {doors} (размерные линии и дуга открывания отсеяны), габарит {widthM.ToString("0.00", inv)} × {depthM.ToString("0.00", inv)} м; " +
            $"зал 10 × 10 м → колонн {rasterBuilt.Columns} на {rasterBuilt.Storeys} этажа, осей {rasterBuilt.Axes.Count}, плиты {rasterBuilt.SlabThickness * 1000:0} мм, " +
            $"K_исп max {rasterBuilt.MaxUtilization.ToString("0.00", inv)} ({rasterBuilt.GoverningElement}), без опоры {rasterBuilt.PhysicsErrors}, проходов подбора {rasterBuilt.SizingPasses}");

        // ═══ 3. Шлюз достоверности: лист правок, частичное и полное принятие, одна транзакция, Undo до бита, откат генерации ═══
        var scene = new SceneStateService();
        scene.LoadScene([], [], recordUndo: false, clearHistory: true, axes: []);
        string empty = DocumentFingerprint(scene);
        var (defect, defectGroups, defectAxes) = CreateGateDefectScene();
        scene.LoadScene(defect, defectGroups, recordUndo: true, axes: defectAxes, dimensions: []);
        string generator = DocumentFingerprint(scene);
        var loads = new StructuralLoadSettings();
        var gate = VerificationGateService.Inspect(scene, loads, "ChatGPT · веб-аккаунт");
        var fixes = gate.Fixes;
        var column = fixes.FirstOrDefault(f => f.Code == "KE-COL");
        var punching = fixes.FirstOrDefault(f => f.Code == "SEIS-NODE");
        bool described = fixes.Count >= 2 && fixes.All(f => f.IsAccepted && f.AuthorRole.Length > 0 && f.DefectDescription.Length > 0 && f.ProposedActionDescription.Length > 0 &&
                                                            f.Changes.Count >= 2 && f.BeforeSnapshot.Count > 0 && f.AfterSnapshot.Count > 0 && f.NormReference.Length > 0) &&
                         column is { KBefore: > 1.2 } && column.KAfter < 1.0 && column.DeltaKisp < 0 && Math.Abs(column.DeltaCostRub) > 1 && column.AuthorRole.Contains("СП 63") &&
                         column.Changes.Any(c => c.Parameter.StartsWith("Сечение") && c.Before == "200 × 200") && punching is { KBefore: > 1.0 } && punching.KAfter < punching.KBefore &&
                         gate.KBefore > 1.2 && gate.KAfterAll < gate.KBefore && gate.CostAfterAllRub > gate.CostBeforeRub;
        // Частичное принятие (1 из N) на копии: меняется только элемент принятой правки.
        var partial = DesignFixEngine.Sandbox(scene);
        var first = fixes[0];
        foreach (var f in fixes) f.IsAccepted = ReferenceEquals(f, first);
        var partialResult = VerificationGateService.ApplyAccepted(partial, gate, loads);
        bool partialOk = partialResult.Accepted == 1 && partial.Transactions.UndoCount == 1 &&
                         VerificationGateService.SnapshotOf(partial.FindById(first.ElementId)!).SequenceEqual(first.AfterSnapshot) &&
                         fixes.Skip(1).Where(f => f.ElementId != first.ElementId).All(f => VerificationGateService.SnapshotOf(partial.FindById(f.ElementId)!).SequenceEqual(f.BeforeSnapshot));
        // Полное принятие на живой сцене: ровно один шаг журнала; Undo ≡ сцене генератора до бита, Redo ≡ принятому, «Отменить всё» ≡ до генерации.
        foreach (var f in fixes) f.IsAccepted = true;
        int undoBefore = scene.Transactions.UndoCount;
        var applied = VerificationGateService.ApplyAccepted(scene, gate, loads);
        int steps = scene.Transactions.UndoCount - undoBefore;
        string accepted = DocumentFingerprint(scene);
        string stepName = scene.Transactions.UndoStack[^1].Name;
        scene.Undo();
        string undone = DocumentFingerprint(scene);
        scene.Redo();
        string redone = DocumentFingerprint(scene);
        bool reverted = VerificationGateService.RevertGeneration(scene, gate);
        string beforeGeneration = DocumentFingerprint(scene);
        bool fullOk = applied.Accepted == fixes.Count && applied.Applied >= fixes.Select(f => f.ElementId).Distinct().Count() && steps == 1 && stepName == "Правки локального аудитора" && accepted != generator &&
                      undone == generator && redone == accepted && reverted && beforeGeneration == empty && applied.KAfter < gate.KBefore;
        Check(report, ref failures, described && partialOk && fullOk,
            $"шлюз достоверности: проверщиков {gate.VerifiersRun}, правок {fixes.Count} ({string.Join(", ", fixes.Select(f => f.Code))}); «{column?.ElementName}»: K_исп {column?.KBefore.ToString("0.00", inv)} → {column?.KAfter.ToString("0.00", inv)}, " +
            $"Δсметы {column?.DeltaCostRub.ToString("+0;-0", inv)} ₽, «{column?.AuthorRole}»; продавливание {punching?.KBefore.ToString("0.00", inv)} → {punching?.KAfter.ToString("0.00", inv)}; " +
            $"всего K_исп {gate.KBefore.ToString("0.00", inv)} → {gate.KAfterAll.ToString("0.00", inv)}, смета {gate.CostBeforeRub.ToString("N0", inv)} → {gate.CostAfterAllRub.ToString("N0", inv)} ₽; " +
            $"принята 1 из {fixes.Count} — меняется только «{first.ElementName}»; принято всё — шагов журнала {steps} («{stepName}»), Undo ≡ сцене генератора до бита, Redo ≡ принятому, «Отменить всё» ≡ до генерации" +
            (partialOk ? string.Empty : " [частичное принятие: " + string.Join("; ", fixes.Select(f => $"{f.Code} {string.Join(",", VerificationGateService.SnapshotOf(partial.FindById(f.ElementId)!).Values)}")) + "]"));

        // ═══ 4. Вложения запроса: авто-снимок (JPEG), план, контекст запроса и вставка в веб-проектировщика без буфера обмена ═══
        uint clipboardBefore = GetClipboardSequenceNumber();
        var frame = await RequestAttachmentService.FromPixelsAsync(raster.Bgra, raster.Width, raster.Height, AttachmentKind.ViewportSnapshot, "Снимок вида · 3D", isAuto: true);
        string planPath = await WriteDemoPlanAsync(Path.Combine(directory, "demo-plan.png"));
        var planAttachment = await RequestAttachmentService.FromFileAsync(planPath, AttachmentKind.PlanImage);
        var vm = App.Services.GetService<ViewModels.MainViewModel>();
        RequestAttachment? live = vm is null ? null : await vm.CaptureViewSnapshotAsync();
        var attachments = new List<RequestAttachment> { live ?? frame, planAttachment };
        var ai = App.Services.GetRequiredService<AIOrchestrator>();
        var web = AiModelCatalog.WebOptions[0];
        string description = RequestAttachmentService.DescribeForPrompt(attachments);
        var context = ai.CreateContext("Проверь узел опирания", web, appendToSite: false, VisionMode.None, attachments.Select(a => a.ToAiImage()).ToList(), attachments[0].FileName,
            BlueprintKind.Auto, description);
        string prompt = ai.BuildWebPrompt(context);
        string script = WebAccountBridgeProvider.AttachImagesScript(WebAccountBridgeProvider.Site(web.Provider), context.Images);
        var parsed = WebAccountBridgeProvider.ParseAttach("\"{\\\"ok\\\":true,\\\"via\\\":\\\"file-input\\\",\\\"count\\\":2}\"");
        uint clipboardAfter = GetClipboardSequenceNumber();
        bool jpeg = frame.Bytes is [0xFF, 0xD8, ..] && frame.Width == raster.Width && frame.Height == raster.Height && frame.MimeType == "image/jpeg";
        bool liveOk = live is null || live.Bytes is [0xFF, 0xD8, ..] && Math.Max(live.Width, live.Height) == RequestAttachmentService.SnapshotLongSide;
        bool attachOk = jpeg && liveOk && planAttachment is { Kind: AttachmentKind.PlanImage, Bytes.Length: > 0 } && context.Images.Count == 2 && context.Image is { Length: > 0 } &&
                        prompt.Contains("ПРИЛОЖЕННЫЕ ИЗОБРАЖЕНИЯ") && prompt.Contains("снимок") && prompt.Contains("поэтажный план") &&
                        script.Contains(context.Images[1].Base64[..64]) && script.Contains("DataTransfer") && script.Contains("input[type=\"file\"]") &&
                        !script.Contains("navigator.clipboard") && parsed is (true, "file-input", 2) && clipboardBefore == clipboardAfter;
        Check(report, ref failures, attachOk,
            $"вложения запроса: авто-снимок {(live is null ? $"{frame.Width}×{frame.Height} (кадр без вьюпорта)" : $"живого вьюпорта {live.Width}×{live.Height}")} JPEG {(live ?? frame).Bytes.Length / 1024} КБ, " +
            $"план «{planAttachment.Title}» {planAttachment.Width}×{planAttachment.Height}; в контексте запроса {context.Images.Count} изображения и подпись «ПРИЛОЖЕННЫЕ ИЗОБРАЖЕНИЯ»; " +
            $"вставка в поле веб-проектировщика — файлы из base64 через DataTransfer (input[type=file] / paste / drop), скрипт {script.Length / 1024} КБ; " +
            $"буфер обмена пользователя не тронут (номер последовательности {clipboardBefore} = {clipboardAfter})");
        return failures;
    }
}
#endif
