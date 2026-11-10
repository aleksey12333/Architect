using System.Globalization;
using System.Numerics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.Pdf;

namespace AIArchitectStudio.Services;

public enum GostSheetFormat
{
    A3,
    A2
}

public enum GostDrawingView
{
    /// <summary>План текущего этажа.</summary>
    FloorPlan,

    /// <summary>Схема расположения элементов каркаса.</summary>
    FrameScheme,

    /// <summary>Разрез по активной плоскости Section Box.</summary>
    Section
}

/// <summary>Задание на выпуск листа: снимок сцены (клоны), вид, формат и параметры разреза.</summary>
public sealed record GostDrawingRequest(
    GostSheetFormat Format,
    GostDrawingView View,
    IReadOnlyList<ArchitecturalElement> Elements,
    IReadOnlyList<BuildingGroup> Groups,
    IReadOnlyList<StructuralGridAxis> Axes,
    string ProjectName,
    string? GroupId,
    int Level,
    SectionBox? Box,
    SectionFace? ActiveFace,
    int ScaleDenominator = 0,
    StructuralLoadSettings? Loads = null,
    IReadOnlyList<PinnedDimension>? Dimensions = null);

public sealed record GostDrawingResult(string Path, string SheetTitle, int ScaleDenominator, int SpecRows, int ElementCount, IReadOnlyList<string> Details)
{
    /// <summary>Пользовательских размеров рулетки, выведенных на лист (в проекции вида).</summary>
    public int PinnedDimensions { get; init; }

    /// <summary>Статистика удаления линий заднего плана (только вид «Разрез»).</summary>
    public SectionOcclusion? Occlusion { get; init; }

    /// <summary>Размер файла и шрифта-подмножества.</summary>
    public PdfSaveInfo? Pdf { get; init; }

    /// <summary>Автопроверка размерных цепочек листа (ГОСТ 2.307-2011): замыкание, числа, наложения текста.</summary>
    public DimensionCheckResult? DimensionCheck { get; init; }

    /// <summary>Размерные цепочки и рамки текстов листа.</summary>
    public DrawingDimensionLayout? DimensionLayout { get; init; }
}

/// <summary>Лист альбома: номер, наименование изображения, формат и масштаб (0 — лист таблиц).</summary>
public sealed record GostAlbumSheet(int Number, string Title, GostSheetFormat Format, int ScaleDenominator);

/// <summary>Итог выпуска альбома: листы, отчёт, размер PDF и шрифта, окклюзия разреза, строки таблиц листа 3.</summary>
public sealed record GostAlbumResult(string Path, IReadOnlyList<GostAlbumSheet> Sheets, IReadOnlyList<string> Details, PdfSaveInfo Pdf,
    SectionOcclusion? Occlusion, int SpecRows, int SteelRows)
{
    /// <summary>Пользовательских размеров рулетки на листах плана и разреза.</summary>
    public int PinnedDimensions { get; init; }

    /// <summary>Автопроверка размерных цепочек плана и разреза (ГОСТ 2.307-2011).</summary>
    public DimensionCheckResult? DimensionCheck { get; init; }

    /// <summary>Размерные цепочки листов плана и разреза.</summary>
    public IReadOnlyList<DrawingDimensionLayout> DimensionLayouts { get; init; } = [];
}

/// <summary>Окклюзия разреза: отрезков заднего плана, сколько из них обрезано сечениями, видимых участков, проверка инварианта.</summary>
public sealed record SectionOcclusion(int BeyondSegments, int VisibleSegments, int ClippedSegments, bool Verified);

/// <summary>
/// Выпуск рабочего чертежа в векторный PDF по ГОСТ Р 21.101-2020 (СПДС): внешняя рамка и рамка чертежа (20 мм слева,
/// 5 мм сверху, справа и снизу), дополнительные графы на левом поле (согласование, инв. № подл., подп. и дата,
/// взам. инв. №), основная надпись по форме 3 (185 × 55 мм), спецификация элементов над штампом и векторное поле
/// чертежа в масштабе 1:50 / 1:100 / автоподбор: координационные оси в кружках Ø8 мм с размерными цепочками в мм,
/// стены, заштрихованные сечения колонн К1…, балки Б1…, плиты и перечёркнутые отверстия, на разрезе — высотные отметки.
/// Линии по ГОСТ 2.303: основная 0.5 мм, тонкая 0.18 мм, осевая — штрихпунктирная тонкая.
/// </summary>
public sealed partial class GostDrawingPdfExporter
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private const double Main = 0.5;
    private const double Thin = 0.18;
    private const double Medium = 0.35;
    private const double StampW = 185, StampH = 55, Stamp2aH = 15;
    private const double Bubble = 4.0; // радиус марки оси (Ø8 мм)
    private static readonly int[] Scales = [50, 100, 200, 400, 500, 800, 1000, 2000];

    public static (double W, double H) SheetSize(GostSheetFormat format) => format == GostSheetFormat.A2 ? (594, 420) : (420, 297);

    public static string FormatTitle(GostSheetFormat format) => format == GostSheetFormat.A2 ? "А2" : "А3";

    /// <summary>Отметка уровня по СПДС: «±0,000», «+2,830», «-1,830».</summary>
    public static string Elevation(double meters)
    {
        double v = Math.Round(meters, 3);
        if (Math.Abs(v) < 0.0005) return "±0,000";
        return (v > 0 ? "+" : "-") + Math.Abs(v).ToString("0.000", Ru);
    }

    public Task<GostDrawingResult> ExportAsync(GostDrawingRequest request, string path, CancellationToken ct = default) =>
        Task.Run(() => Export(request, path), ct);

    // ═════════════════════════════ Модель листа ═════════════════════════════

    /// <summary>Перевод координат чертежа (м, U вправо, V вверх) в мм листа.</summary>
    private sealed class Mapper
    {
        public double K = 10; // мм листа на метр
        public double U0, V0, X0, Y0;
        public (double X, double Y) P(double u, double v) => (X0 + (u - U0) * K, Y0 + (v - V0) * K);
        public (double X, double Y) P((double U, double V) p) => P(p.U, p.V);
    }

    private sealed class Drawing
    {
        public readonly List<Action<PdfVectorPage, Mapper>> Calls = [];
        public double MinU = double.MaxValue, MaxU = double.MinValue, MinV = double.MaxValue, MaxV = double.MinValue;

        public void Track(IEnumerable<(double U, double V)> pts)
        {
            foreach (var (u, v) in pts)
            {
                MinU = Math.Min(MinU, u); MaxU = Math.Max(MaxU, u);
                MinV = Math.Min(MinV, v); MaxV = Math.Max(MaxV, v);
            }
        }

        public bool IsEmpty => MinU > MaxU;
    }

    private sealed record SpecRow(string Mark, string Name, string Section, int Count, double? Volume, IReadOnlyList<ArchitecturalElement> Items);

    /// <summary>Строка ведомости расхода стали: марка и расход на один элемент марки.</summary>
    private sealed record SteelRow(string Mark, SteelBreakdown Steel);

    // ═════════════════════════════ Выпуск ═════════════════════════════

    /// <summary>Компоновка листа: чертёж со спецификацией и ведомостью стали над штампом, только поле чертежа (альбом) или таблицы.</summary>
    private enum SheetLayout
    {
        Standard,
        DrawingOnly,
        Tables
    }

    /// <summary>Общие данные выпуска: постройка, её поэтажные экземпляры, отметки, сетка осей, этаж и экспресс-расчёт K_исп.</summary>
    private sealed class SheetContext
    {
        public required GostDrawingRequest Request { get; init; }
        public required string GroupId { get; init; }
        public required BuildingGroup? Group { get; init; }
        public required List<ArchitecturalElement> GroupItems { get; init; }
        public required List<ArchitecturalElement> Instances { get; init; }
        public required List<LevelMark> Levels { get; init; }
        public required BuildingGrid? Grid { get; init; }
        public required int Level { get; init; }
        public required double LevelElevation { get; init; }
        public required StructuralLoadSettings Loads { get; init; }
        public required StructuralAnalysisResult Analysis { get; init; }
        public required HashSet<string> Punching { get; init; }
    }

    /// <summary>Изображение листа: геометрия поля чертежа (в метрах), спецификация марок, ведомость стали, оси и отметки.</summary>
    private sealed class SheetContent
    {
        public required Drawing Drawing { get; init; }
        public required List<SpecRow> SpecRows { get; init; }
        public required List<SteelRow> SteelRows { get; init; }
        public required string Title { get; init; }
        public required string Mark { get; init; }
        public required int Drawn { get; init; }
        public required bool IsSection { get; init; }
        public List<(string Label, double U)> AxisMarks { get; init; } = [];
        public List<(string Label, double V)> RowMarks { get; init; } = [];
        public List<(double V, string Text)> LevelMarks { get; init; } = [];
        public SectionOcclusion? Occlusion { get; init; }

        /// <summary>Габарит изображённых конструкций (м чертежа) — крайние точки размерных цепочек.</summary>
        public (double MinU, double MaxU, double MinV, double MaxV) Extents { get; init; }

        /// <summary>Цепочка 1 плана: границы проёмов и простенков наружной стены внизу (U) и слева (V).</summary>
        public List<double> FacadeU { get; init; } = [];
        public List<double> FacadeV { get; init; } = [];

        /// <summary>Выведено пользовательских размеров рулетки.</summary>
        public int PinnedDimensions { get; init; }
    }

    /// <summary>Итог компоновки листа: масштаб, выведено строк спецификации и ведомости стали.</summary>
    private readonly record struct RenderedSheet(int Scale, int SpecRows, int SteelRows, DrawingDimensionLayout? Layout = null);

    private static TrueTypeFont LoadFont() => TrueTypeFont.LoadSystem("isocpeur.ttf", "ISOCPEUR.TTF", "arial.ttf", "segoeui.ttf", "tahoma.ttf");

    /// <param name="subsetFont">false — встроить полный файл шрифта (сравнение размера в автотесте).</param>
    public static GostDrawingResult Export(GostDrawingRequest req, string path, bool subsetFont = true)
    {
        var font = LoadFont();
        var ctx = CreateContext(req);
        var content = Prepare(ctx, req.View);
        var doc = new PdfVectorDocument(font) { Title = $"{req.ProjectName} — {content.Title}", SubsetFont = subsetFont };
        var details = new List<string>();
        var sheet = Render(doc, ctx, content, req.Format, SheetLayout.Standard, req.ScaleDenominator, 1, 1, Code(req, content.Mark), details, []);
        var info = doc.Save(path);
        var (sheetW, sheetH) = SheetSize(req.Format);
        details.Insert(0, $"Лист {FormatTitle(req.Format)} ({sheetW:0}×{sheetH:0} мм), масштаб 1:{sheet.Scale}, {FontLine(info)}");
        details.Add($"Элементов на чертеже: {content.Drawn}; строк спецификации: {sheet.SpecRows}; осей: {content.AxisMarks.Count + content.RowMarks.Count}; отметок уровней: {content.LevelMarks.Count}");
        AddContentDetails(details, content, sheet, ctx);
        // Автопроверка размеров перед выпуском: замыкание цепочек, числа ≡ геометрии, рамки текстов без наложений.
        var check = DrawingDimensionVerifier.Verify(sheet.Layout is { } layout ? [layout] : []);
        details.Add("Размеры по ГОСТ 2.307-2011: " + check.Summary(Ru));
        return new GostDrawingResult(path, content.Title, sheet.Scale, sheet.SpecRows, content.Drawn, details)
        {
            Occlusion = content.Occlusion,
            Pdf = info,
            PinnedDimensions = content.PinnedDimensions,
            DimensionCheck = check,
            DimensionLayout = sheet.Layout
        };
    }

    public Task<GostAlbumResult> ExportAlbumAsync(GostDrawingRequest request, string path, CancellationToken ct = default) =>
        Task.Run(() => ExportAlbum(request, path), ct);

    /// <summary>
    /// «Выпуск альбома чертежей (3 листа PDF)»: лист 1 — крупный план этажа с осями и размерными цепочками (1:100 на А3,
    /// если помещается, иначе на А2; поле чертежа без таблиц), лист 2 — разрез по активному Section Box с высотными отметками
    /// уровней, лист 3 — спецификация элементов этажа, ведомость расхода стали и ведомость листов. Первый лист — основная
    /// надпись по форме 3, последующие — по форме 2а (185 × 15 мм); шрифт — подмножество использованных глифов.
    /// </summary>
    public static GostAlbumResult ExportAlbum(GostDrawingRequest req, string path)
    {
        var font = LoadFont();
        var ctx = CreateContext(req);
        // Сначала спецификация этажа (порядок марок плана), затем план и разрез — с общей нумерацией марок.
        var registry = new MarkRegistry();
        var tables = PrepareFloorTables(ctx, registry);
        var plan = Prepare(ctx, GostDrawingView.FloorPlan, registry);
        var section = Prepare(ctx, GostDrawingView.Section, registry);
        string code = Code(req, "АС");
        var doc = new PdfVectorDocument(font) { Title = $"{req.ProjectName} — альбом рабочих чертежей" };
        var details = new List<string>();

        var (planFormat, planScale) = AlbumFormat(plan, preferredScale: req.ScaleDenominator > 0 ? req.ScaleDenominator : 100, sheetNo: 1);
        var (sectionFormat, sectionScale) = AlbumFormat(section, preferredScale: req.ScaleDenominator > 0 ? req.ScaleDenominator : 100, sheetNo: 2);
        var sheetList = new List<(string Title, string Note)>
        {
            ($"{plan.Title}. М 1:{planScale}", FormatTitle(planFormat)),
            ($"{section.Title}. М 1:{sectionScale}", FormatTitle(sectionFormat)),
            (tables.Title, FormatTitle(GostSheetFormat.A3))
        };

        var s1 = Render(doc, ctx, plan, planFormat, SheetLayout.DrawingOnly, planScale, 1, 3, code, details, sheetList);
        var s2 = Render(doc, ctx, section, sectionFormat, SheetLayout.DrawingOnly, sectionScale, 2, 3, code, details, sheetList);
        var s3 = Render(doc, ctx, tables, GostSheetFormat.A3, SheetLayout.Tables, 0, 3, 3, code, details, sheetList);
        var info = doc.Save(path);

        var sheets = new List<GostAlbumSheet>
        {
            new(1, plan.Title, planFormat, s1.Scale),
            new(2, section.Title, sectionFormat, s2.Scale),
            new(3, tables.Title, GostSheetFormat.A3, 0)
        };
        details.Insert(0, $"Альбом {code}: листов {info.Pages}, {info.Bytes / 1024.0:0} КБ, {FontLine(info)}");
        details.Insert(1, $"Лист 1 — {plan.Title}, {FormatTitle(planFormat)}, М 1:{s1.Scale}, элементов {plan.Drawn}, осей {plan.AxisMarks.Count + plan.RowMarks.Count}");
        details.Insert(2, $"Лист 2 — {section.Title}, {FormatTitle(sectionFormat)}, М 1:{s2.Scale}, сечений {section.Drawn}, отметок уровней {section.LevelMarks.Count}");
        details.Insert(3, $"Лист 3 — {tables.Title}: строк спецификации {s3.SpecRows}, марок в ведомости стали {s3.SteelRows}");
        if (section.Occlusion is { } o)
            details.Add($"Окклюзия разреза: линий заднего плана {o.BeyondSegments}, обрезано сечениями {o.ClippedSegments}, видимых участков {o.VisibleSegments}" +
                        (o.Verified ? " — ни один видимый участок не проходит по заливке сечений" : " — ОШИБКА: линия поверх сечения"));
        if (plan.PinnedDimensions + section.PinnedDimensions > 0)
            details.Add($"Пользовательские размеры рулетки: на плане {plan.PinnedDimensions}, на разрезе {section.PinnedDimensions}");
        var layouts = new[] { s1.Layout, s2.Layout }.OfType<DrawingDimensionLayout>().ToList();
        var check = DrawingDimensionVerifier.Verify(layouts);
        details.Add("Размеры по ГОСТ 2.307-2011: " + check.Summary(Ru));
        return new GostAlbumResult(path, sheets, details, info, section.Occlusion, s3.SpecRows, s3.SteelRows)
        {
            PinnedDimensions = plan.PinnedDimensions + section.PinnedDimensions,
            DimensionCheck = check,
            DimensionLayouts = layouts
        };
    }

    private static string FontLine(PdfSaveInfo info) =>
        $"шрифт {info.BaseFont} (подмножество {info.Glyphs} глифов: {info.FontBytes / 1024.0:0.#} КБ вместо {info.FullFontBytes / 1024.0:0} КБ, в {info.FontReduction:0} раз меньше; TrueType, Identity-H)";

    private static string Code(GostDrawingRequest req, string mark) => $"{DateTime.Now:yyyy}-{(Math.Abs(StableHash(req.ProjectName)) % 90) + 10}-{mark}";

    private static void AddContentDetails(List<string> details, SheetContent content, RenderedSheet sheet, SheetContext ctx)
    {
        if (content.PinnedDimensions > 0)
            details.Add($"Пользовательских размеров рулетки на листе: {content.PinnedDimensions} (в проекции вида, засечки 45°)");
        if (content.Occlusion is { } o)
            details.Add($"Окклюзия разреза: линий заднего плана {o.BeyondSegments}, обрезано сечениями {o.ClippedSegments}, видимых участков {o.VisibleSegments}" +
                        (o.Verified ? " — ни один видимый участок не проходит по заливке сечений" : " — ОШИБКА: линия поверх сечения"));
        if (sheet.SteelRows > 0)
            details.Add($"Ведомость расхода стали на элемент: марок {sheet.SteelRows}, на марку {string.Join(", ", content.SteelRows.Take(sheet.SteelRows).Select(s => $"{s.Mark} {s.Steel.Total.ToString("0.0", Ru)} кг"))}" +
                        $" (K_исп по экспресс-расчёту, {ctx.Analysis.ElapsedMilliseconds.ToString("0.0", Ru)} мс)");
    }

    private static SheetContext CreateContext(GostDrawingRequest req)
    {
        string groupId = req.GroupId is { } g && req.Elements.Any(e => e.BuildingGroupId == g)
            ? g
            : req.Elements.GroupBy(e => e.BuildingGroupId).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? BuildingGroup.DefaultGroupId;
        var groupItems = req.Elements.Where(e => e.BuildingGroupId == groupId).ToList();
        var instances = groupItems.SelectMany(Expand).ToList();
        var levels = StructuralAxesBuilder.LevelElevations(groupItems);
        int level = Math.Clamp(req.Level < 0 ? 0 : req.Level, 0, Math.Max(0, instances.Count == 0 ? 0 : instances.Max(i => i.LevelIndex)));
        // Ведомость расхода стали: армирование марок ж/б по K_исп экспресс-расчёта (СП 63.13330).
        var loads = req.Loads ?? new StructuralLoadSettings();
        var analysis = StructuralAnalysisService.Analyze(req.Elements, req.Groups, loads);
        return new SheetContext
        {
            Request = req,
            GroupId = groupId,
            Group = req.Groups.FirstOrDefault(x => x.Id == groupId),
            GroupItems = groupItems,
            Instances = instances,
            Levels = levels,
            Grid = StructuralAxesBuilder.BuildGrids(groupItems, req.Axes.Where(a => a.BuildingGroupId == groupId).ToList()).FirstOrDefault(),
            Level = level,
            LevelElevation = levels.FirstOrDefault(l => l.Level == level)?.Elevation ?? 0,
            Loads = loads,
            Analysis = analysis,
            Punching = BimAnalyticsService.PunchingReinforcedIds(analysis)
        };
    }

    /// <summary>Поле чертежа вида view: геометрия, марки и строки спецификации, оси, отметки, ведомость стали по маркам.</summary>
    private static SheetContent Prepare(SheetContext ctx, GostDrawingView view, MarkRegistry? registry = null)
    {
        var req = ctx.Request;
        var drawing = new Drawing();
        var specRows = new List<SpecRow>();
        string title, mark;
        int drawn;
        var axisMarks = new List<(string Label, double U)>();
        var rowMarks = new List<(string Label, double V)>();
        var levelMarks = new List<(double V, string Text)>();
        SectionOcclusion? occlusion = null;
        bool section = view == GostDrawingView.Section;
        int pinned = 0;
        (double MinU, double MaxU, double MinV, double MaxV) extents = default;
        List<double> facadeU = [], facadeV = [];
        void Capture() => extents = drawing.IsEmpty ? (0, 6, 0, 6) : (drawing.MinU, drawing.MaxU, drawing.MinV, drawing.MaxV);

        switch (view)
        {
            case GostDrawingView.FrameScheme:
            {
                var slabsAbove = ctx.Instances.Where(e => e.LevelIndex == ctx.Level && e.Type is ElementTypes.Slab or ElementTypes.CantileverSlab).ToList();
                double elev = slabsAbove.Count > 0 ? slabsAbove.Max(s => s.TopY) : ctx.Levels.FirstOrDefault(l => l.Level == ctx.Level + 1)?.Elevation ?? ctx.LevelElevation;
                title = $"Схема расположения элементов каркаса. План на отм. {Elevation(elev)}";
                mark = "КЖ";
                drawn = DrawFrameScheme(drawing, ctx.Instances, req.Elements, ctx.Level, specRows, registry);
                Capture();
                pinned = DrawPinnedDimensions(drawing, PlanDimensions(ctx), p => Plan(p.X, p.Z));
                break;
            }
            case GostDrawingView.Section:
            {
                var (sectionTitle, n, _, u, count, sectionLevels) = DrawSection(drawing, ctx.Instances, ctx.GroupItems, req, specRows, ctx.Levels, out var occ, registry);
                occlusion = occ;
                Capture();
                title = sectionTitle;
                mark = "КЖ";
                drawn = count;
                levelMarks = sectionLevels;
                // Размеры рулетки — в проекции на плоскость разреза (U вдоль разреза, V — высота).
                pinned = DrawPinnedDimensions(drawing, req.Dimensions ?? [], p => (u.X * p.X + u.Y * p.Y + u.Z * p.Z, p.Y));
                if (ctx.Grid is not null)
                {
                    // Оси, перпендикулярные плоскости разреза: при плоскости z = const — цифровые (x), при x = const — буквенные (z).
                    bool alongX = Math.Abs(n.Z) > 0.5;
                    foreach (var a in ctx.Grid.Axes.Where(a => a.IsNumeric == alongX))
                    {
                        var p = alongX ? new Vector3((float)a.Coordinate, 0, 0) : new Vector3(0, 0, (float)a.Coordinate);
                        axisMarks.Add((a.Label, BimMath.Dot(p, u)));
                    }
                }
                break;
            }
            default:
            {
                title = $"План этажа на отм. {Elevation(ctx.LevelElevation)}";
                mark = "АР";
                drawn = DrawFloorPlan(drawing, ctx.Instances, req.Elements, ctx.Level, specRows, registry);
                Capture();
                (facadeU, facadeV) = FacadeChains(ctx.Instances, req.Elements, ctx.Level, extents);
                pinned = DrawPinnedDimensions(drawing, PlanDimensions(ctx), p => Plan(p.X, p.Z));
                break;
            }
        }

        if (!section && ctx.Grid is not null)
        {
            foreach (var a in ctx.Grid.NumericAxes) axisMarks.Add((a.Label, a.Coordinate));
            foreach (var a in ctx.Grid.LetterAxes) rowMarks.Add((a.Label, -a.Coordinate));
        }
        if (drawing.IsEmpty) drawing.Track([(0, 0), (6, 6)]);
        foreach (var a in axisMarks) drawing.Track([(a.U, drawing.MinV)]);
        foreach (var r in rowMarks) drawing.Track([(drawing.MinU, r.V)]);
        foreach (var l in levelMarks) drawing.Track([(drawing.MinU, l.V)]);

        return new SheetContent
        {
            Drawing = drawing,
            SpecRows = specRows,
            SteelRows = SteelRowsFor(specRows, ctx),
            Title = title,
            Mark = mark,
            Drawn = drawn,
            IsSection = section,
            AxisMarks = axisMarks,
            RowMarks = rowMarks,
            LevelMarks = levelMarks,
            Occlusion = occlusion,
            PinnedDimensions = pinned,
            Extents = extents,
            FacadeU = facadeU,
            FacadeV = facadeV
        };
    }

    /// <summary>
    /// Цепочка 1 (ближняя) плана по ГОСТ 21.501: границы проёмов и простенков наружной стены нижнего фасада (U) и левого (V) —
    /// стены вдоль стороны, наружная грань которых в 0,6 м от габарита; без проёмов на стороне — пустой список.
    /// </summary>
    private static (List<double> U, List<double> V) FacadeChains(List<ArchitecturalElement> items, IReadOnlyList<ArchitecturalElement> scene, int level,
        (double MinU, double MaxU, double MinV, double MaxV) e)
    {
        var (walls, _, hosted, _, _) = PlanSets(items, scene, level);
        var bottom = new List<double>();
        var left = new List<double>();
        foreach (var w in walls.Where(w => w.Polygon2D.Count < 3 && w.Type == ElementTypes.Wall))
        {
            double r = ((ArchitecturalElement.NormalizeAngle(w.RotationY) % 180) + 180) % 180;
            bool alongU = r < 1 || r > 179, alongV = Math.Abs(r - 90) < 1;
            if (!alongU && !alongV) continue;
            var outline = PlanOutline(w);
            bool atBottom = alongU && outline.Min(p => p.V) - e.MinV < 0.6;
            bool atLeft = alongV && outline.Min(p => p.U) - e.MinU < 0.6;
            if (!atBottom && !atLeft) continue;
            foreach (var o in hosted.Where(h => h.HostElementId == w.Id))
            {
                var (lx, _) = ArchitecturalElement.RotateY(o.X - w.X, o.Z - w.Z, -w.RotationY);
                var a = Local(w, lx - o.Width / 2, 0);
                var b = Local(w, lx + o.Width / 2, 0);
                if (atBottom) bottom.AddRange([a.U, b.U]);
                else left.AddRange([a.V, b.V]);
            }
        }
        static List<double> Chain(List<double> edges, double lo, double hi)
        {
            if (edges.Count == 0) return [];
            var pts = edges.Where(x => x > lo + 1e-3 && x < hi - 1e-3).Append(lo).Append(hi).OrderBy(x => x).ToList();
            var result = new List<double>();
            foreach (double x in pts)
                if (result.Count == 0 || x - result[^1] > 0.0015) result.Add(x);
            return result;
        }
        return (Chain(bottom, e.MinU, e.MaxU), Chain(left, e.MinV, e.MaxV));
    }

    /// <summary>Размеры рулетки этажа плана: отрезок по высоте пересекает интервал от отметки этажа до отметки следующего.</summary>
    private static List<PinnedDimension> PlanDimensions(SheetContext ctx)
    {
        double low = ctx.Level == 0 ? double.MinValue : ctx.LevelElevation - 0.05;
        double high = ctx.Levels.FirstOrDefault(l => l.Level == ctx.Level + 1)?.Elevation - 0.01 ?? double.MaxValue;
        return (ctx.Request.Dimensions ?? []).Where(d => Math.Max(d.Start[1], d.End[1]) >= low && Math.Min(d.Start[1], d.End[1]) < high).ToList();
    }

    /// <summary>
    /// Пользовательские размеры рулетки на поле чертежа по ГОСТ 2.307: тонкая размерная линия с выступом 2 мм за засечки,
    /// засечки 45° основной линией, значение в мм над линией (читается снизу или справа). Значение — длина проекции на
    /// плоскость вида; проекции короче 50 мм не выводятся. Возвращает число выведенных размеров.
    /// </summary>
    private static int DrawPinnedDimensions(Drawing dr, IReadOnlyList<PinnedDimension> dimensions, Func<(double X, double Y, double Z), (double U, double V)> project)
    {
        int count = 0;
        foreach (var d in dimensions.Where(d => d.IsValid))
        {
            var a = project(d.A);
            var b = project(d.B);
            double length = MeasurementService.ProjectedLength(a, b);
            if (length < 0.05) continue;
            string text = string.IsNullOrWhiteSpace(d.Label) ? MeasurementService.GostValue(length) : d.Label;
            dr.Track([a, b]);
            dr.Calls.Add((page, m) =>
            {
                var (x1, y1) = m.P(a);
                var (x2, y2) = m.P(b);
                double dx = x2 - x1, dy = y2 - y1, l = Math.Sqrt(dx * dx + dy * dy);
                if (l < 1e-6) return;
                double ux = dx / l, uy = dy / l, nx = -uy, ny = ux;
                page.StrokeGray(0);
                page.LineWidth(Thin);
                page.Line(x1 - ux * 2, y1 - uy * 2, x2 + ux * 2, y2 + uy * 2);
                page.LineWidth(Main);
                double tx = (ux + nx) * 1.2, ty = (uy + ny) * 1.2;
                page.Line(x1 - tx, y1 - ty, x1 + tx, y1 + ty);
                page.Line(x2 - tx, y2 - ty, x2 + tx, y2 + ty);
                // Текст над линией и читаемый снизу/справа: угол приводится к (−90°; 90°].
                double angle = Math.Atan2(uy, ux) * 180 / Math.PI;
                if (angle > 90.0001) angle -= 180;
                else if (angle <= -90) angle += 180;
                double rad = angle * Math.PI / 180;
                double mx = (x1 + x2) / 2 - Math.Sin(rad) * 1.0, my = (y1 + y2) / 2 + Math.Cos(rad) * 1.0;
                page.Text(text, mx, my, 2.5, PdfTextAlign.Center, angle);
            });
            count++;
        }
        return count;
    }

    /// <summary>
    /// Лист таблиц альбома: спецификация всех элементов этажа — марки плана (стены, колонны, проёмы, отверстия пола, лестницы)
    /// с той же нумерацией, что на листе 1, и элементы перекрытия этажа (балки, плиты, их отверстия) — и ведомость стали.
    /// </summary>
    private static SheetContent PrepareFloorTables(SheetContext ctx, MarkRegistry? registry = null)
    {
        var rows = new List<SpecRow>();
        var level = ctx.Level;
        var planItems = PlanMarkItems(ctx.Instances, ctx.Request.Elements, level);
        var beams = ctx.Instances.Where(e => e.LevelIndex == level && e.Type == ElementTypes.Beam && e.Polygon2D.Count < 3).ToList();
        var slabs = ctx.Instances.Where(e => e.LevelIndex == level && e.Type is ElementTypes.Slab or ElementTypes.CantileverSlab || e.LevelIndex == level && e.IsFlatRoof).ToList();
        var holes = slabs.SelectMany(s => SceneStateService.OpeningsFor(s, ctx.Request.Elements)).Where(h => !planItems.Contains(h)).Distinct().ToList();
        Marks(planItems.Concat(beams).Concat(slabs).Concat(holes).Distinct(), ctx.Request.Elements, rows, registry);
        return new SheetContent
        {
            Drawing = new Drawing(),
            SpecRows = rows,
            SteelRows = SteelRowsFor(rows, ctx),
            Title = $"Спецификация элементов этажа {level + 1} (отм. {Elevation(ctx.LevelElevation)}). Ведомость расхода стали",
            Mark = "КЖ",
            Drawn = rows.Sum(r => r.Count),
            IsSection = false
        };
    }

    /// <summary>Ведомость расхода стали на элемент: армирование каждой марки ж/б по K_исп экспресс-расчёта.</summary>
    private static List<SteelRow> SteelRowsFor(List<SpecRow> specRows, SheetContext ctx)
    {
        var steelRows = new List<SteelRow>();
        foreach (var row in specRows)
        {
            var reinforced = row.Items.Where(ReinforcementCalculator.IsReinforced).ToList();
            if (reinforced.Count == 0) continue;
            var sum = new SteelBreakdown();
            foreach (var item in reinforced)
            {
                string sourceId = item.Id.Split(':')[0];
                double k = ctx.Analysis.For(sourceId)?.Utilization ?? 0;
                double volume = ReinforcementCalculator.ReinforcedVolume(item, ConcreteVolume(item, ctx.Request.Elements));
                sum.Add(ReinforcementCalculator.Compute(item, volume, k, ctx.Punching.Contains(sourceId)));
            }
            var perElement = new SteelBreakdown();
            perElement.Add(sum, 1.0 / reinforced.Count);
            if (perElement.Total > 0.05) steelRows.Add(new SteelRow(row.Mark, perElement));
        }
        return steelRows;
    }

    /// <summary>Отступы поля чертежа от области листа (мм): слева — под марки буквенных осей, снизу — под размерные цепочки.</summary>
    private static (double L, double R, double B, double T) Pads(bool section) => section ? (28, 40, 34, 24) : (42, 16, 42, 30);

    /// <summary>Области поля чертежа: левее основной надписи на всю высоту и во всю ширину над таблицами (штампом).</summary>
    private static (double X0, double X1, double Y0, double Y1)[] Areas(double sheetW, double sheetH, double tablesTop)
    {
        double frameL = 20, frameB = 5, frameR = sheetW - 5, frameT = sheetH - 5, stampX = frameR - StampW;
        return
        [
            (frameL + 6, stampX - 6, frameB + 6, frameT - 6),
            (frameL + 6, frameR - 6, tablesTop + 6, frameT - 6)
        ];
    }

    private static bool FitsScale(SheetContent c, (double X0, double X1, double Y0, double Y1) area, int scale)
    {
        var (pl, pr, pb, pt) = Pads(c.IsSection);
        double k = 1000.0 / scale;
        double wM = Math.Max(1, c.Drawing.MaxU - c.Drawing.MinU), hM = Math.Max(1, c.Drawing.MaxV - c.Drawing.MinV);
        return wM * k <= area.X1 - area.X0 - pl - pr && hM * k <= area.Y1 - area.Y0 - pb - pt;
    }

    /// <summary>Формат листа альбома: А3 в масштабе preferredScale, если поле помещается, иначе А2 (при необходимости — мельче).</summary>
    private static (GostSheetFormat Format, int Scale) AlbumFormat(SheetContent c, int preferredScale, int sheetNo)
    {
        double stampH = sheetNo == 1 ? StampH : Stamp2aH;
        foreach (var format in new[] { GostSheetFormat.A3, GostSheetFormat.A2 })
        {
            var (w, h) = SheetSize(format);
            if (Areas(w, h, 5 + stampH).Any(a => FitsScale(c, a, preferredScale))) return (format, preferredScale);
        }
        var (w2, h2) = SheetSize(GostSheetFormat.A2);
        var areas = Areas(w2, h2, 5 + stampH);
        int scale = Scales.FirstOrDefault(s => s > preferredScale && areas.Any(a => FitsScale(c, a, s)));
        return (GostSheetFormat.A2, scale > 0 ? scale : Scales[^1]);
    }

    /// <summary>
    /// Компоновка листа в документе: рамка и графы, поле чертежа в подобранном масштабе (или таблицы), спецификация и ведомость
    /// стали над штампом (стандартный лист), основная надпись формы 3 (первый лист) или 2а (последующие листы альбома).
    /// </summary>
    private static RenderedSheet Render(PdfVectorDocument doc, SheetContext ctx, SheetContent c, GostSheetFormat format, SheetLayout layout,
        int scaleDenominator, int sheetNo, int sheetCount, string code, List<string> details, IReadOnlyList<(string Title, string Note)> sheetList)
    {
        var req = ctx.Request;
        var (sheetW, sheetH) = SheetSize(format);
        var page = doc.AddPage(sheetW, sheetH);
        double frameB = 5, frameR = sheetW - 5, frameT = sheetH - 5;
        double stampX = frameR - StampW;
        double stampH = sheetNo == 1 ? StampH : Stamp2aH;
        string buildingName = ctx.Group?.Name ?? ctx.GroupId;

        void Stamp(string sheetName)
        {
            if (sheetNo == 1) DrawStamp(page, stampX, frameB, code, req.ProjectName, buildingName, sheetName, sheetNo, sheetCount);
            else DrawStampNext(page, stampX, frameB, code, sheetNo);
        }

        if (layout == SheetLayout.Tables)
        {
            var (spec, steel) = DrawTablesSheet(page, c, sheetW, sheetH, stampH, ctx.Loads.Rebar, sheetList, details);
            DrawFrame(page, sheetW, sheetH, format);
            Stamp(c.Title);
            return new RenderedSheet(0, spec, steel);
        }

        // ── Таблицы над штампом (стандартный лист): ведомость стали и спецификация.
        List<SpecRow> rows = [];
        List<SteelRow> steelRows = [];
        double specTableTop = frameB + stampH, tablesTop = frameB + stampH, steelH = 0;
        if (layout == SheetLayout.Standard)
        {
            double budget = frameT - frameB - stampH - 80;
            steelRows = c.SteelRows.Take(Math.Max(0, Math.Min(c.SteelRows.Count, (int)((budget * 0.45 - SteelHeaderH - 9) / SteelRowH)))).ToList();
            steelH = steelRows.Count > 0 ? SteelHeaderH + steelRows.Count * SteelRowH + 9 : 0;
            rows = c.SpecRows.Take(Math.Max(1, (int)((budget - steelH) / 8))).ToList();
            specTableTop = frameB + stampH + 15 + rows.Count * 8;
            tablesTop = specTableTop + 9 + steelH;
        }

        // ── Поле чертежа: наименьший знаменатель масштаба, при котором чертёж помещается в одну из областей.
        var drawing = c.Drawing;
        var (padL, padR, padB, padT) = Pads(c.IsSection);
        var areas = Areas(sheetW, sheetH, tablesTop);
        int bestScale = Scales[^1];
        var bestArea = areas[0];
        foreach (var area in areas)
            foreach (int s in Scales)
            {
                if (!FitsScale(c, area, s)) continue;
                if (s < bestScale) { bestScale = s; bestArea = area; }
                break;
            }
        int scale = bestScale;
        if (scaleDenominator > 0)
        {
            var fit = areas.FirstOrDefault(a => FitsScale(c, a, scaleDenominator));
            if (fit != default)
            {
                scale = scaleDenominator;
                bestArea = fit;
            }
            else details.Add($"Масштаб 1:{scaleDenominator} не помещается на формат {FormatTitle(format)} — применён 1:{bestScale}");
        }

        double wM = Math.Max(1, drawing.MaxU - drawing.MinU), hM = Math.Max(1, drawing.MaxV - drawing.MinV);
        var map = new Mapper { K = 1000.0 / scale, U0 = drawing.MinU, V0 = drawing.MinV };
        double contentW = wM * map.K, contentH = hM * map.K;
        double availW = bestArea.X1 - bestArea.X0 - padL - padR, availH = bestArea.Y1 - bestArea.Y0 - padB - padT;
        map.X0 = bestArea.X0 + padL + (availW - contentW) / 2;
        map.Y0 = bestArea.Y0 + padB + (availH - contentH) / 2;

        page.StrokeGray(0);
        page.FillGray(0);
        foreach (var call in drawing.Calls) call(page, map);

        var (bx0, by0) = map.P(drawing.MinU, drawing.MinV);
        var (bx1, by1) = map.P(drawing.MaxU, drawing.MaxV);
        var dims = new DrawingDimensionLayout { Sheet = c.Title };
        if (c.IsSection)
        {
            DrawSectionAxes(page, map, c.AxisMarks, by0, by1, c.Extents, dims);
            DrawStoreyChain(page, map, c.LevelMarks, bx0, dims);
        }
        else DrawPlanAxes(page, map, c, bx0, by0, bx1, by1, dims);
        DrawLevelMarks(page, map, c.LevelMarks, bx1, dims);

        // Название изображения над полем чертежа.
        string head = $"{c.Title}   М 1:{scale}";
        double headY = by1 + (c.IsSection ? 12 : 18);
        double cx = (bx0 + bx1) / 2;
        page.LineWidth(Thin);
        page.Text(head, cx, headY, 3.5, PdfTextAlign.Center);
        double hw = page.TextWidth(head, 3.5);
        page.Line(cx - hw / 2, headY - 1.2, cx + hw / 2, headY - 1.2);

        DrawFrame(page, sheetW, sheetH, format);
        if (layout == SheetLayout.Standard)
        {
            DrawSpecification(page, stampX, frameB + stampH, rows);
            if (steelRows.Count > 0) DrawSteelStatement(page, stampX, specTableTop + 9, steelRows, ctx.Loads.Rebar);
        }
        Stamp($"{c.Title}. М 1:{scale}");
        return new RenderedSheet(scale, rows.Count, steelRows.Count, dims);
    }

    /// <summary>
    /// Лист таблиц альбома (А3): слева — спецификация элементов этажа на всю высоту поля, справа — ведомость расхода стали
    /// на элемент и ведомость листов альбома над основной надписью. Возвращает выведенные строки обеих таблиц.
    /// </summary>
    private static (int SpecRows, int SteelRows) DrawTablesSheet(PdfVectorPage page, SheetContent c, double sheetW, double sheetH, double stampH,
        RebarClass rebar, IReadOnlyList<(string Title, string Note)> sheetList, List<string> details)
    {
        double frameL = 20, frameB = 5, frameR = sheetW - 5, frameT = sheetH - 5;
        double top = frameT - 12;

        // Спецификация — левая колонка.
        double leftX = frameL + 8;
        int specCapacity = Math.Max(1, (int)((top - (frameB + 8) - 15) / 8));
        var specRows = c.SpecRows.Take(specCapacity).ToList();
        if (c.SpecRows.Count > specRows.Count) details.Add($"Спецификация этажа: выведено {specRows.Count} из {c.SpecRows.Count} марок (ограничение высоты листа А3)");
        DrawSpecification(page, leftX, top - (15 + specRows.Count * 8), specRows, "Спецификация элементов этажа");

        // Правая колонка: ведомость стали и ведомость листов.
        double rightX = frameR - StampW;
        double listH = 8 + sheetList.Count * 8;
        double bottomLimit = frameB + stampH + 12 + listH + 12;
        int steelCapacity = Math.Max(0, (int)((top - bottomLimit - SteelHeaderH) / SteelRowH));
        var steelRows = c.SteelRows.Take(steelCapacity).ToList();
        if (c.SteelRows.Count > steelRows.Count) details.Add($"Ведомость стали: выведено {steelRows.Count} из {c.SteelRows.Count} марок");
        if (steelRows.Count > 0) DrawSteelStatement(page, rightX, top - (SteelHeaderH + steelRows.Count * SteelRowH), steelRows, rebar);
        else page.Text("Монолитных армируемых элементов на этаже нет", rightX + StampW / 2, top - 6, 2.5, PdfTextAlign.Center);
        if (sheetList.Count > 0) DrawSheetList(page, rightX, frameB + stampH + 12, sheetList);
        return (specRows.Count, steelRows.Count);
    }

    /// <summary>Ведомость листов альбома (ГОСТ Р 21.101): Лист (15) · Наименование (140) · Примечание (30).</summary>
    private static void DrawSheetList(PdfVectorPage page, double x, double bottom, IReadOnlyList<(string Title, string Note)> sheets)
    {
        double[] cols = [15, 140, 30];
        const double headerH = 8, rowH = 8;
        double totalH = headerH + sheets.Count * rowH, top = bottom + totalH;
        page.Text("Ведомость листов альбома", x + StampW / 2, top + 3, 3.5, PdfTextAlign.Center);
        page.LineWidth(Main);
        page.Rect(x, bottom, StampW, totalH);
        page.Line(x, top - headerH, x + StampW, top - headerH);
        page.Line(x + cols[0], bottom, x + cols[0], top);
        page.Line(x + cols[0] + cols[1], bottom, x + cols[0] + cols[1], top);
        page.Text("Лист", x + cols[0] / 2, top - headerH / 2 - 1.25, 2.5, PdfTextAlign.Center);
        page.Text("Наименование", x + cols[0] + cols[1] / 2, top - headerH / 2 - 1.25, 2.5, PdfTextAlign.Center);
        page.Text("Примечание", x + cols[0] + cols[1] + cols[2] / 2, top - headerH / 2 - 1.25, 2.5, PdfTextAlign.Center);
        page.LineWidth(Thin);
        for (int i = 0; i < sheets.Count; i++)
        {
            double ry = top - headerH - (i + 1) * rowH;
            if (i < sheets.Count - 1) page.Line(x, ry, x + StampW, ry);
            double ty = ry + rowH / 2 - 1.25;
            page.Text((i + 1).ToString(Ru), x + cols[0] / 2, ty, 2.5, PdfTextAlign.Center);
            FitLeft(page, sheets[i].Title, x + cols[0] + 1.5, ty, 2.5, cols[1] - 3);
            FitCenter(page, sheets[i].Note, x + cols[0] + cols[1] + cols[2] / 2, ty, 2.5, cols[2] - 2);
        }
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }

    /// <summary>Параметрический элемент (RepeatLevels) — поэтажные экземпляры с закруткой и сужением.</summary>
    private static IEnumerable<ArchitecturalElement> Expand(ArchitecturalElement e)
    {
        int repeat = Math.Max(1, e.RepeatLevels);
        if (repeat == 1 || e.Type == ElementTypes.Diagrid)
        {
            yield return e;
            yield break;
        }
        double taper = e.TaperScalePerFloor <= 0 ? 1 : e.TaperScalePerFloor, scale = 1;
        for (int i = 0; i < repeat; i++, scale *= taper)
        {
            var c = e.Clone();
            c.Id = i == 0 ? e.Id : $"{e.Id}:{i}";
            c.RepeatLevels = 1;
            c.TwistAnglePerFloor = 0;
            c.TaperScalePerFloor = 1;
            c.LevelIndex = e.LevelIndex + i;
            c.SetPosition(e.X, e.Y + e.LevelHeight * i, e.Z);
            c.SetSize(e.Width * scale, e.Height, e.Depth * scale);
            c.SetRotationY(e.RotationY + e.TwistAnglePerFloor * i);
            c.Polygon2D = e.Polygon2D.Select(p => new Point2D(p.X * scale, p.Z * scale)).ToList();
            yield return c;
        }
    }

    // ═════════════════════════════ Геометрия плана ═════════════════════════════

    /// <summary>Мировая точка плана → координаты чертежа (U = x, V = −z: север вверх).</summary>
    private static (double U, double V) Plan(double x, double z) => (x, -z);

    /// <summary>Контур элемента в плане по его профилю (окружность — 48-угольник, двутавр — I-контур).</summary>
    private static List<(double U, double V)> PlanOutline(ArchitecturalElement e)
    {
        List<(double X, double Z)> local;
        double w = e.Width, d = e.Depth;
        string profile = e.EffectiveProfile;
        if (e.Polygon2D.Count >= 3) local = e.Polygon2D.Select(p => (p.X, p.Z)).ToList();
        else if (e.Type == ElementTypes.Column && profile is ProfileTypes.Circle or ProfileTypes.Pipe)
            local = Enumerable.Range(0, 48).Select(i => 2 * Math.PI * i / 48).Select(a => (Math.Cos(a) * w / 2, Math.Sin(a) * d / 2)).ToList();
        else if (e.Type == ElementTypes.Column && profile == ProfileTypes.IBeam)
        {
            var (tf, tw) = e.ProfileThicknesses(w, d);
            double x = w / 2, z = d / 2;
            local = [(-x, -z), (x, -z), (x, -z + tf), (tw / 2, -z + tf), (tw / 2, z - tf), (x, z - tf),
                     (x, z), (-x, z), (-x, z - tf), (-tw / 2, z - tf), (-tw / 2, -z + tf), (-x, -z + tf)];
        }
        else local = [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)];
        return local.Select(p => Local(e, p.X, p.Z)).ToList();
    }

    /// <summary>Локальная точка элемента (x вдоль, z поперёк) → чертёж.</summary>
    private static (double U, double V) Local(ArchitecturalElement e, double lx, double lz)
    {
        var (rx, rz) = ArchitecturalElement.RotateY(lx, lz, e.RotationY);
        return Plan(e.X + rx, e.Z + rz);
    }

    private static void Poly(Drawing dr, List<(double U, double V)> pts, double width, bool closed = true, double[]? dash = null, double? fill = null)
    {
        dr.Track(pts);
        dr.Calls.Add((page, m) =>
        {
            var p = pts.Select(m.P).ToList();
            if (fill is { } g)
            {
                page.FillGray(g);
                page.Polygon(p, fill: true, stroke: false);
                page.FillGray(0);
            }
            page.LineWidth(width);
            page.Dash(dash ?? []);
            if (closed) page.Polygon(p, fill: false, stroke: true);
            else page.Polyline(p, false);
            page.Dash();
        });
    }

    private static void Seg(Drawing dr, (double U, double V) a, (double U, double V) b, double width, double[]? dash = null, double gray = 0)
    {
        dr.Track([a, b]);
        dr.Calls.Add((page, m) =>
        {
            var (x1, y1) = m.P(a);
            var (x2, y2) = m.P(b);
            page.StrokeGray(gray);
            page.LineWidth(width);
            page.Dash(dash ?? []);
            page.Line(x1, y1, x2, y2);
            page.Dash();
            page.StrokeGray(0);
        });
    }

    private static void Hatch(Drawing dr, List<List<(double U, double V)>> regions, double step = 1.2)
    {
        dr.Calls.Add((page, m) =>
            page.Hatch(regions.Select(r => (IReadOnlyList<(double, double)>)r.Select(m.P).ToList()).ToList(), step, 45, 0.13));
    }

    /// <summary>Надпись у точки чертежа со смещением в мм листа.</summary>
    private static void Label(Drawing dr, (double U, double V) at, string text, double dx, double dy, double height = 2.5, PdfTextAlign align = PdfTextAlign.Left)
    {
        dr.Calls.Add((page, m) =>
        {
            var (x, y) = m.P(at);
            page.Text(text, x + dx, y + dy, height, align);
        });
    }

    // ═════════════════════════════ Марки и спецификация ═════════════════════════════

    private static bool IsMonolithic(string? material)
    {
        string m = (material ?? string.Empty).ToLowerInvariant();
        return !(m.Contains("wood") || m.Contains("timber") || m.Contains("steel") || m.Contains("glass") || m.Contains("metal"));
    }

    /// <summary>Площадь сечения стержня b × h по профилю (м²).</summary>
    private static double SectionArea(ArchitecturalElement e, double b, double h)
    {
        var (t, tw) = e.ProfileThicknesses(b, h);
        return e.EffectiveProfile switch
        {
            ProfileTypes.Circle => Math.PI / 4 * b * h,
            ProfileTypes.Pipe => Math.PI / 4 * (b * h - Math.Max(0, b - 2 * t) * Math.Max(0, h - 2 * t)),
            ProfileTypes.IBeam => 2 * b * t + Math.Max(0, h - 2 * t) * tw,
            ProfileTypes.Polygon when e.Type == ElementTypes.Column => e.PlanArea(),
            _ => b * h
        };
    }

    private static string Mm(double m) => Math.Round(m * 1000).ToString("0", Ru);

    private static string SectionText(ArchitecturalElement e, double b, double h)
    {
        var (t, _) = e.ProfileThicknesses(b, h);
        return e.EffectiveProfile switch
        {
            ProfileTypes.Circle => Math.Abs(b - h) < 1e-3 ? $"Ø{Mm(b)}" : $"Ø{Mm(b)}×{Mm(h)}",
            ProfileTypes.Pipe => $"Труба Ø{Mm(b)}×{Mm(t)}",
            ProfileTypes.IBeam => $"Двутавр {Mm(h)}×{Mm(b)}",
            ProfileTypes.Polygon => "по контуру",
            _ => $"{Mm(b)}×{Mm(h)}"
        };
    }

    /// <summary>
    /// Марки по одинаковым сечениям: К1, К2… (колонны), Б1… (балки), П1… (плиты), С1… (стены), ОК1/Д1 (окна, двери),
    /// ОП1… (отверстия). Строки спецификации — количество и объём бетона марки.
    /// </summary>
    private static Dictionary<string, string> Marks(IEnumerable<ArchitecturalElement> items, IReadOnlyList<ArchitecturalElement> scene, List<SpecRow> rows, MarkRegistry? registry = null)
    {
        var result = new Dictionary<string, string>();
        var groups = new List<(string Prefix, string Key, string Name, string Section, List<ArchitecturalElement> Items)>();
        foreach (var e in items)
        {
            string material = (e.Material ?? string.Empty).ToLowerInvariant();
            bool steel = material.Contains("steel") || material.Contains("metal");
            (string Prefix, string Key, string Name, string Section)? key = e.Type switch
            {
                ElementTypes.Column => ("К", $"{e.EffectiveProfile}|{Mm(e.Width)}|{Mm(e.Depth)}|{Mm(e.ProfileThicknesses(e.Width, e.Depth).Wall)}|{material}",
                    steel ? "Колонна стальная" : "Колонна монолитная ж/б", SectionText(e, e.Width, e.Depth)),
                ElementTypes.Beam => ("Б", $"{e.EffectiveProfile}|{Mm(e.Depth)}|{Mm(e.Height)}|{material}",
                    steel ? "Балка стальная" : "Балка монолитная ж/б", SectionText(e, e.Depth, e.Height)),
                ElementTypes.Slab or ElementTypes.CantileverSlab => ("П", $"{Mm(e.Height)}|{material}",
                    e.Type == ElementTypes.CantileverSlab ? "Плита консольная монолитная" : "Плита перекрытия монолитная", $"h={Mm(e.Height)}"),
                ElementTypes.Foundation => ("Фм", Mm(e.Height), "Фундаментная плита", $"h={Mm(e.Height)}"),
                ElementTypes.Roof => ("Пп", Mm(e.Height), e.IsFlatRoof ? "Плита покрытия" : "Кровля", e.IsFlatRoof ? $"h={Mm(e.Height)}" : $"H={Mm(e.Height)}"),
                ElementTypes.Wall or ElementTypes.Core => ("С", $"{Mm(e.Depth)}|{material}|{e.Type}",
                    e.Type == ElementTypes.Core ? "Ядро жёсткости монолитное" : IsMonolithic(material) && material.Contains("concrete") ? "Стена монолитная ж/б" : "Стена " + WallMaterial(material),
                    $"t={Mm(e.Depth)}"),
                ElementTypes.CurtainWall => ("Вт", Mm(e.Height), "Витраж", $"H={Mm(e.Height)}"),
                ElementTypes.Window => ("ОК", $"{Mm(e.Width)}|{Mm(e.Height)}", "Окно", $"{Mm(e.Width)}×{Mm(e.Height)}"),
                ElementTypes.Door => ("Д", $"{Mm(e.Width)}|{Mm(e.Height)}", "Дверь", $"{Mm(e.Width)}×{Mm(e.Height)}"),
                ElementTypes.WallOpening => ("Пр", $"{Mm(e.Width)}|{Mm(e.Height)}", "Проём в стене", $"{Mm(e.Width)}×{Mm(e.Height)}"),
                ElementTypes.SlabOpening => ("ОП", e.Polygon2D.Count >= 3 ? $"poly{e.PlanArea():0.000}" : $"{Mm(e.Width)}|{Mm(e.Depth)}", "Отверстие в плите",
                    e.Polygon2D.Count >= 3 ? $"по контуру, {e.PlanArea().ToString("0.00", Ru)} м²" : $"{Mm(e.Width)}×{Mm(e.Depth)}"),
                ElementTypes.Stairs => ("ЛМ", $"{Mm(e.Width)}|{Mm(e.Depth)}", "Лестничный марш", $"{Mm(e.Width)}×{Mm(e.Depth)}"),
                _ => null
            };
            if (key is not { } k) continue;
            var existing = groups.FindIndex(x => x.Prefix == k.Prefix && x.Key == k.Key);
            if (existing < 0) groups.Add((k.Prefix, k.Key, k.Name, k.Section, [e]));
            else groups[existing].Items.Add(e);
        }

        foreach (var prefixGroup in groups.GroupBy(x => x.Prefix))
        {
            int n = 1;
            foreach (var gr in prefixGroup)
            {
                // Альбом: одна нумерация марок на всех листах (та же марка у того же сечения на плане, разрезе и в спецификации).
                string mark = registry?.Mark(gr.Prefix, gr.Key) ?? $"{gr.Prefix}{n++}";
                foreach (var e in gr.Items) result[e.Id] = mark;
                double? volume = null;
                var sample = gr.Items[0];
                if (ElementTypes.IsConcreteType(sample.Type) && IsMonolithic(sample.Material))
                    volume = gr.Items.Sum(e => ConcreteVolume(e, scene));
                rows.Add(new SpecRow(mark, gr.Name, gr.Section, gr.Items.Count, volume, gr.Items));
            }
        }
        return result;
    }

    /// <summary>Общая нумерация марок альбома: сечение (префикс + ключ) → марка; новые сечения получают следующий номер префикса.</summary>
    private sealed class MarkRegistry
    {
        private readonly Dictionary<(string Prefix, string Key), string> _marks = [];
        private readonly Dictionary<string, int> _next = [];

        public string Mark(string prefix, string key)
        {
            if (_marks.TryGetValue((prefix, key), out var mark)) return mark;
            int n = _next.GetValueOrDefault(prefix) + 1;
            _next[prefix] = n;
            return _marks[(prefix, key)] = $"{prefix}{n}";
        }
    }

    private static string WallMaterial(string material) =>
        material.Contains("brick") ? "кирпичная" : material.Contains("wood") || material.Contains("timber") ? "деревянная" :
        material.Contains("block") ? "из блоков" : material.Contains("glass") ? "стеклянная" : "";

    private static double ConcreteVolume(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> scene)
    {
        switch (e.Type)
        {
            case ElementTypes.Column:
                return SectionArea(e, e.Width, e.Depth) * e.Height;
            case ElementTypes.Beam:
                return SectionArea(e, e.Depth, e.Height) * e.Width;
            case ElementTypes.Wall:
            {
                // Пирог стены: в объём бетона входят только бетонные слои.
                double t = e.Layers.Count > 0 ? MaterialLayers.ConcreteThickness(e) : e.Depth;
                double gross = e.Width * e.Height * t;
                double openings = scene.Where(o => o.HostElementId == e.Id.Split(':')[0] && ElementTypes.IsHostedType(o.Type))
                    .Sum(o => Math.Min(o.Width, e.Width) * Math.Min(o.Height, e.Height) * t);
                return Math.Max(0, gross - openings);
            }
            case ElementTypes.Core:
            {
                double t = Math.Clamp(Math.Min(e.Width, e.Depth) * 0.06, 0.25, 0.5);
                return (e.Width * e.Depth - Math.Max(0, e.Width - 2 * t) * Math.Max(0, e.Depth - 2 * t)) * e.Height;
            }
            default:
            {
                double area = e.PlanArea();
                if (e.CanHostSlabOpenings)
                    area -= SceneStateService.OpeningsFor(e, scene).Sum(o => Math.Min(o.PlanArea(), e.PlanArea()));
                double t = e.Layers.Count > 0 && MaterialLayers.DrivesThickness(e) ? MaterialLayers.ConcreteThickness(e) : e.Height;
                return Math.Max(0, area) * t;
            }
        }
    }

    // ═════════════════════════════ План этажа ═════════════════════════════

    /// <summary>Наборы плана этажа: стены (с ядром и витражами), колонны, проёмы, лестницы и отверстия в перекрытии пола этажа.</summary>
    private static (List<ArchitecturalElement> Walls, List<ArchitecturalElement> Columns, List<ArchitecturalElement> Hosted, List<ArchitecturalElement> Stairs, List<ArchitecturalElement> FloorHoles)
        PlanSets(List<ArchitecturalElement> items, IReadOnlyList<ArchitecturalElement> scene, int level)
    {
        var onLevel = items.Where(e => e.LevelIndex == level).ToList();
        var hosted = onLevel.Where(e => ElementTypes.IsHostedType(e.Type)).ToList();
        var walls = onLevel.Where(e => e.Type is ElementTypes.Wall or ElementTypes.Core or ElementTypes.CurtainWall).ToList();
        var columns = onLevel.Where(e => e.Type == ElementTypes.Column).ToList();
        var stairs = onLevel.Where(e => e.Type == ElementTypes.Stairs).ToList();
        // Отверстия в перекрытии пола этажа (плиты этажа ниже или фундамент первого этажа).
        var floorSlabs = items.Where(e => e.CanHostSlabOpenings && (level == 0 ? e.Type == ElementTypes.Foundation || e.LevelIndex == 0 && e.Type == ElementTypes.Slab && e.BaseY < 0.1 : e.LevelIndex == level - 1)).ToList();
        var floorHoles = floorSlabs.SelectMany(s => SceneStateService.OpeningsFor(s, scene)).Distinct().ToList();
        return (walls, columns, hosted, stairs, floorHoles);
    }

    /// <summary>Элементы плана в порядке присвоения марок (одинаковая нумерация на плане и в спецификации этажа альбома).</summary>
    private static List<ArchitecturalElement> PlanMarkItems(List<ArchitecturalElement> items, IReadOnlyList<ArchitecturalElement> scene, int level)
    {
        var (walls, columns, hosted, stairs, floorHoles) = PlanSets(items, scene, level);
        return walls.Concat(columns).Concat(hosted).Concat(floorHoles).Concat(stairs).ToList();
    }

    private static int DrawFloorPlan(Drawing dr, List<ArchitecturalElement> items, IReadOnlyList<ArchitecturalElement> scene, int level, List<SpecRow> rows, MarkRegistry? registry = null)
    {
        var (walls, columns, hosted, stairs, floorHoles) = PlanSets(items, scene, level);
        var marks = Marks(PlanMarkItems(items, scene, level), scene, rows, registry);

        foreach (var w in walls)
        {
            if (w.Polygon2D.Count >= 3 || w.Type == ElementTypes.CurtainWall)
            {
                var outline = PlanOutline(w);
                Poly(dr, outline, w.Type == ElementTypes.CurtainWall ? Thin : Main, fill: w.Type == ElementTypes.CurtainWall ? null : 0.86);
                if (w.Type == ElementTypes.CurtainWall && w.Polygon2D.Count < 3)
                    Seg(dr, Local(w, -w.Width / 2, 0), Local(w, w.Width / 2, 0), Thin);
                continue;
            }
            if (w.Type == ElementTypes.Core)
            {
                double t = Math.Clamp(Math.Min(w.Width, w.Depth) * 0.06, 0.25, 0.5);
                Poly(dr, PlanOutline(w), Main, fill: 0.86);
                var inner = new[] { (-w.Width / 2 + t, -w.Depth / 2 + t), (w.Width / 2 - t, -w.Depth / 2 + t), (w.Width / 2 - t, w.Depth / 2 - t), (-w.Width / 2 + t, w.Depth / 2 - t) }
                    .Select(p => Local(w, p.Item1, p.Item2)).ToList();
                Poly(dr, inner, Main, fill: 1.0);
                continue;
            }
            DrawWallWithOpenings(dr, w, hosted.Where(h => h.HostElementId == w.Id).ToList());
        }

        foreach (var c in columns) DrawColumn(dr, c, marks.GetValueOrDefault(c.Id));
        foreach (var s in stairs)
        {
            Poly(dr, PlanOutline(s), Thin);
            int steps = Math.Clamp((int)Math.Round(s.Height / 0.17), 3, 40);
            for (int i = 1; i < steps; i++)
            {
                double z = -s.Depth / 2 + s.Depth * i / steps;
                Seg(dr, Local(s, -s.Width / 2, z), Local(s, s.Width / 2, z), Thin);
            }
            Seg(dr, Local(s, 0, s.Depth / 2 - 0.2), Local(s, 0, -s.Depth / 2 + 0.2), Thin);
        }
        foreach (var hole in floorHoles) DrawSlabHole(dr, hole, marks.GetValueOrDefault(hole.Id));
        return walls.Count + columns.Count + hosted.Count + stairs.Count + floorHoles.Count;
    }

    /// <summary>Стена в плане: участки между проёмами (заливка + основная линия), окна — три тонкие линии, двери — полотно и дуга.</summary>
    private static void DrawWallWithOpenings(Drawing dr, ArchitecturalElement w, List<ArchitecturalElement> openings)
    {
        double half = w.Width / 2, t = w.Depth / 2;
        var gaps = new List<(double A, double B, ArchitecturalElement O)>();
        foreach (var o in openings)
        {
            var (lx, _) = ArchitecturalElement.RotateY(o.X - w.X, o.Z - w.Z, -w.RotationY);
            double a = Math.Max(-half, lx - o.Width / 2), b = Math.Min(half, lx + o.Width / 2);
            if (b - a > 0.05) gaps.Add((a, b, o));
        }
        gaps.Sort((x, y) => x.A.CompareTo(y.A));
        double cursor = -half;
        void Piece(double a, double b)
        {
            if (b - a < 0.001) return;
            Poly(dr, [Local(w, a, -t), Local(w, b, -t), Local(w, b, t), Local(w, a, t)], Main, fill: 0.86);
        }
        foreach (var (a, b, o) in gaps)
        {
            Piece(cursor, Math.Max(cursor, a));
            cursor = Math.Max(cursor, b);
            switch (o.Type)
            {
                case ElementTypes.Window:
                    Seg(dr, Local(w, a, -t), Local(w, b, -t), Thin);
                    Seg(dr, Local(w, a, t), Local(w, b, t), Thin);
                    Seg(dr, Local(w, a, 0), Local(w, b, 0), Thin);
                    break;
                case ElementTypes.Door:
                {
                    // Полотно, открытое на 90°, и дуга открывания (тонкие линии).
                    double leaf = b - a;
                    Seg(dr, Local(w, a, t), Local(w, a, t + leaf), Medium);
                    var hinge = Local(w, a, t);
                    double startAngle = Math.Atan2(Local(w, b, t).V - hinge.V, Local(w, b, t).U - hinge.U) * 180 / Math.PI;
                    double endAngle = Math.Atan2(Local(w, a, t + leaf).V - hinge.V, Local(w, a, t + leaf).U - hinge.U) * 180 / Math.PI;
                    if (endAngle - startAngle > 180) endAngle -= 360;
                    if (startAngle - endAngle > 180) endAngle += 360;
                    dr.Track([Local(w, a, t + leaf)]);
                    dr.Calls.Add((page, m) =>
                    {
                        var (hx, hy) = m.P(hinge);
                        page.LineWidth(Thin);
                        page.Arc(hx, hy, leaf * m.K, startAngle, endAngle);
                    });
                    break;
                }
                default:
                    // Сквозной проём: тонкие диагонали по габариту.
                    Seg(dr, Local(w, a, -t), Local(w, b, t), Thin, [1.5, 1]);
                    break;
            }
        }
        Piece(cursor, half);
    }

    private static void DrawColumn(Drawing dr, ArchitecturalElement c, string? mark)
    {
        var outline = PlanOutline(c);
        dr.Track(outline);
        var regions = new List<List<(double U, double V)>> { outline };
        if (c.EffectiveProfile == ProfileTypes.Pipe)
        {
            double t = c.ProfileThicknesses(c.Width, c.Depth).Wall;
            regions.Add(Enumerable.Range(0, 48).Select(i => 2 * Math.PI * (48 - i) / 48)
                .Select(a => Local(c, Math.Cos(a) * (c.Width / 2 - t), Math.Sin(a) * (c.Depth / 2 - t))).ToList());
        }
        Hatch(dr, regions, 1.0);
        Poly(dr, outline, Main);
        if (regions.Count > 1) Poly(dr, regions[1], Main);
        if (mark is not null)
        {
            var at = Plan(c.X, c.Z);
            double r = Math.Max(c.Width, c.Depth) / 2;
            dr.Calls.Add((page, m) =>
            {
                var (x, y) = m.P(at);
                page.Text(mark, x + r * m.K + 0.8, y + r * m.K + 0.8, 2.5);
            });
        }
    }

    private static void DrawSlabHole(Drawing dr, ArchitecturalElement hole, string? mark)
    {
        var outline = PlanOutline(hole);
        Poly(dr, outline, Main);
        // Перечёркивание отверстия по ГОСТ: диагонали габарита.
        if (outline.Count == 4)
        {
            Seg(dr, outline[0], outline[2], Thin);
            Seg(dr, outline[1], outline[3], Thin);
        }
        else
        {
            double u0 = outline.Min(p => p.U), u1 = outline.Max(p => p.U), v0 = outline.Min(p => p.V), v1 = outline.Max(p => p.V);
            Seg(dr, (u0, v0), (u1, v1), Thin);
            Seg(dr, (u0, v1), (u1, v0), Thin);
        }
        string size = hole.Polygon2D.Count >= 3 ? "" : $"{Mm(hole.Width)}×{Mm(hole.Depth)}";
        // Марка и размер — под контуром отверстия (сверху обычно проходят балки и оси).
        var bottom = outline.OrderBy(p => p.V).ThenBy(p => p.U).First();
        Label(dr, bottom, $"{mark ?? "ОП"}{(size.Length > 0 ? " " + size : "")}", 0.5, -3.6, 2.5);
    }

    // ═════════════════════════════ Схема каркаса ═════════════════════════════

    private static int DrawFrameScheme(Drawing dr, List<ArchitecturalElement> items, IReadOnlyList<ArchitecturalElement> scene, int level, List<SpecRow> rows, MarkRegistry? registry = null)
    {
        var columns = items.Where(e => e.LevelIndex == level && e.Type == ElementTypes.Column).ToList();
        var beams = items.Where(e => e.LevelIndex == level && e.Type == ElementTypes.Beam && e.Polygon2D.Count < 3).ToList();
        var slabs = items.Where(e => e.LevelIndex == level && e.Type is ElementTypes.Slab or ElementTypes.CantileverSlab).ToList();
        var walls = items.Where(e => e.LevelIndex == level && e.Type is ElementTypes.Wall or ElementTypes.Core).ToList();
        var holes = slabs.SelectMany(s => SceneStateService.OpeningsFor(s, scene)).Distinct().ToList();

        var marks = Marks(columns.Concat(beams).Concat(slabs).Concat(walls.Where(w => IsMonolithic(w.Material) && (w.Material ?? "").Contains("concrete", StringComparison.OrdinalIgnoreCase) || w.Type == ElementTypes.Core)).Concat(holes), scene, rows, registry);

        foreach (var s in slabs)
        {
            var outline = PlanOutline(s);
            Poly(dr, outline, Medium);
            if (marks.TryGetValue(s.Id, out var mark))
            {
                // Марка плиты в кружке у угла контура.
                var corner = outline.OrderBy(p => p.U - p.V).First();
                dr.Calls.Add((page, m) =>
                {
                    var (x, y) = m.P(corner);
                    page.LineWidth(Thin);
                    page.Circle(x + 7, y - 7, 3.2);
                    page.Text(mark, x + 7, y - 8.25, 2.5, PdfTextAlign.Center);
                });
            }
        }
        foreach (var w in walls)
        {
            var outline = PlanOutline(w);
            Poly(dr, outline, Medium, fill: 0.9);
        }
        foreach (var b in beams)
        {
            var outline = PlanOutline(b);
            Poly(dr, outline, Medium, dash: [4, 1.5]);
            if (marks.TryGetValue(b.Id, out var mark))
                Label(dr, Local(b, 0, -b.Depth / 2), mark, 0, 1.0, 2.5, PdfTextAlign.Center);
        }
        foreach (var c in columns) DrawColumn(dr, c, marks.GetValueOrDefault(c.Id));
        foreach (var hole in holes) DrawSlabHole(dr, hole, marks.GetValueOrDefault(hole.Id));
        return columns.Count + beams.Count + slabs.Count + walls.Count + holes.Count;
    }

    // ═════════════════════════════ Разрез ═════════════════════════════

    private static (string Title, Vector3 N, float D, Vector3 U, int Count, List<(double V, string Text)> Levels) DrawSection(
        Drawing dr, List<ArchitecturalElement> items, List<ArchitecturalElement> groupItems, GostDrawingRequest req, List<SpecRow> rows, List<LevelMark> levels,
        out SectionOcclusion occlusion, MarkRegistry? registry = null)
    {
        var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds(groupItems.Where(e => !ElementTypes.IsVoidType(e.Type)));
        double minY = groupItems.Count > 0 ? groupItems.Min(e => e.BaseY) : 0, maxY = groupItems.Count > 0 ? items.Max(e => e.TopY) : 3;
        var box = req.Box ?? new SectionBox(minX - 1, maxX + 1, minY - 1, maxY + 1, minZ - 1, maxZ + 1);
        var face = req.ActiveFace is { } f && f is not (SectionFace.MinY or SectionFace.MaxY) ? f : SectionFace.MaxZ;
        if (req.Box is null || req.ActiveFace is null or SectionFace.MinY or SectionFace.MaxY)
        {
            // Без активного вертикального сечения — продольный разрез по середине здания.
            bool longX = maxX - minX >= maxZ - minZ;
            face = longX ? SectionFace.MaxZ : SectionFace.MaxX;
            box = longX ? box with { MaxZ = (minZ + maxZ) / 2 } : box with { MaxX = (minX + maxX) / 2 };
        }
        var (n, d) = box.Plane(face);
        var look = BimMath.Negate(n);
        var u = BimMath.Normalize(BimMath.Cross(look, Vector3.UnitY));
        var v = Vector3.UnitY;
        bool planeAlongX = face is SectionFace.MinZ or SectionFace.MaxZ;
        string name = planeAlongX == (maxX - minX >= maxZ - minZ) ? "1-1" : "2-2";

        // Ограничение поля разреза остальными гранями коробки.
        double uA = Math.Min(BimMath.Dot(box.Min, u), BimMath.Dot(box.Max, u)), uB = Math.Max(BimMath.Dot(box.Min, u), BimMath.Dot(box.Max, u));
        double vA = box.MinY, vB = box.MaxY;

        var cut = new List<ArchitecturalElement>();
        var cutRegions = new List<(ArchitecturalElement E, List<List<(double U, double V)>> Fill, List<((double U, double V) P, (double U, double V) Q)> Outline)>();
        var beyond = new List<((double U, double V) A, (double U, double V) B)>();
        foreach (var e in items)
        {
            if (ElementTypes.IsVoidType(e.Type)) continue;
            var group = req.Groups.FirstOrDefault(g => g.Id == e.BuildingGroupId);
            ElementGeometry geometry;
            try
            {
                geometry = ElementGeometryBuilder.Build(e, SceneStateService.OpeningsFor(e, req.Elements), new GeometryContext(SceneStateService.AllLevels, group?.OriginX ?? 0, group?.OriginZ ?? 0));
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (geometry.IsEmpty) continue;

            var (fill, outline) = SectionBoxClipper.PlaneSection(geometry, n, d, u, v);
            var regions = fill.Select(t => ClipRect([(t.A.X, t.A.Y), (t.B.X, t.B.Y), (t.C.X, t.C.Y), (t.D.X, t.D.Y)], uA, uB, vA, vB))
                .Where(r => r.Count >= 3).ToList();
            if (regions.Count > 0)
            {
                // Совпадающие встречные отрезки (стыки частей одного тела) — не контур.
                var keys = new HashSet<(long, long, long, long)>(outline.Select(s => Key(s.P, s.Q)));
                var edges = outline.Where(s => !keys.Contains(Key(s.Q, s.P)))
                    .Select(s => ((double)s.P.X, (double)s.P.Y, (double)s.Q.X, (double)s.Q.Y))
                    .Select(s => ClipSegment2D((s.Item1, s.Item2), (s.Item3, s.Item4), uA, uB, vA, vB))
                    .Where(s => s is not null).Select(s => s!.Value).ToList();
                cutRegions.Add((e, regions, edges));
                cut.Add(e);
            }

            // Видимые за плоскостью рёбра (на сохраняемой стороне, внутри коробки) — тонкими линиями.
            if (geometry.Edges is { } sourceEdges)
            {
                var pos = sourceEdges.Positions;
                var idx = sourceEdges.Indices;
                for (int i = 0; i + 1 < idx.Count; i += 2)
                {
                    var p = BimMath.TransformCoordinate(pos[idx[i]], geometry.Transform);
                    var q = BimMath.TransformCoordinate(pos[idx[i + 1]], geometry.Transform);
                    if (!SectionBoxClipper.ClipSegment(ref p, ref q, box)) continue;
                    var a = (Math.Round(BimMath.Dot(p, u), 4), Math.Round(BimMath.Dot(p, v), 4));
                    var b = (Math.Round(BimMath.Dot(q, u), 4), Math.Round(BimMath.Dot(q, v), 4));
                    if (Math.Abs(a.Item1 - b.Item1) + Math.Abs(a.Item2 - b.Item2) < 1e-4) continue;
                    beyond.Add((a, b));
                }
            }
        }

        // Проекция — тонкие серые линии (без дублей), сечения — штриховка и основная линия. Hidden Line Occlusion: участки
        // линий заднего плана, закрытые сечениями (Cut Polygons), вырезаются геометрически, а не только закрашиваются.
        var distinct = beyond.Distinct().ToList();
        var occluders = cutRegions.SelectMany(c => c.Fill).ToList();
        var visible = OccludeBehind(distinct, occluders);
        foreach (var (a, b) in visible) Seg(dr, a, b, 0.13, gray: 0.45);
        var unchanged = visible.ToHashSet();
        occlusion = new SectionOcclusion(distinct.Count, visible.Count, distinct.Count(s => !unchanged.Contains(s)), NoVisibleInside(visible, occluders));
        var marks = Marks(cut, req.Elements, rows, registry);
        foreach (var (e, regions, edges) in cutRegions)
        {
            dr.Track(regions.SelectMany(r => r));
            dr.Calls.Add((page, m) =>
            {
                page.FillGray(0.78);
                foreach (var r in regions) page.Polygon(r.Select(m.P).ToList(), fill: true, stroke: false);
                page.FillGray(0);
            });
            Hatch(dr, regions, 1.4);
            foreach (var (p, q) in edges) Seg(dr, p, q, Main);
            // Марка колонны — справа от сечения на середине высоты (у плит и балок марки читаются из спецификации).
            if (marks.TryGetValue(e.Id, out var mark) && e.Type == ElementTypes.Column)
            {
                var pts = regions.SelectMany(r => r).ToList();
                Label(dr, (pts.Max(p => p.U), (pts.Min(p => p.V) + pts.Max(p => p.V)) / 2), mark, 0.8, -1.25, 2.2);
            }
        }
        // Линия земли.
        if (minY < -0.05)
        {
            double g0 = cutRegions.Count > 0 ? cutRegions.SelectMany(c => c.Fill).SelectMany(r => r).Min(p => p.U) - 2 : uA;
            double g1 = cutRegions.Count > 0 ? cutRegions.SelectMany(c => c.Fill).SelectMany(r => r).Max(p => p.U) + 2 : uB;
            Seg(dr, (g0, 0), (g1, 0), Medium);
        }

        var marksList = new List<(double V, string Text)>();
        foreach (var l in levels) marksList.Add((l.Elevation, Elevation(l.Elevation)));
        var solidItems = items.Where(e => !ElementTypes.IsVoidType(e.Type)).ToList();
        if (solidItems.Count > 0)
        {
            double bottom = solidItems.Min(e => e.BaseY);
            double top = solidItems.Max(e => e.TopY);
            if (marksList.All(x => Math.Abs(x.V - bottom) > 0.005)) marksList.Add((bottom, Elevation(bottom)));
            if (marksList.All(x => Math.Abs(x.V - top) > 0.005)) marksList.Add((top, Elevation(top)));
        }
        return ($"Разрез {name}", n, d, u, cut.Count, marksList.OrderBy(x => x.V).ToList());
    }

    /// <summary>
    /// Окклюзия в 2D-проекции разреза: из каждого отрезка заднего плана вычитаются интервалы, лежащие внутри выпуклых
    /// частей заливки сечений (алгоритм Сайруса — Бека), остаются только видимые участки.
    /// </summary>
    internal static List<((double U, double V) A, (double U, double V) B)> OccludeBehind(
        IReadOnlyList<((double U, double V) A, (double U, double V) B)> segments, IReadOnlyList<List<(double U, double V)>> occluders)
    {
        var boxes = occluders.Where(o => o.Count >= 3)
            .Select(o => (Poly: o, U0: o.Min(p => p.U), U1: o.Max(p => p.U), V0: o.Min(p => p.V), V1: o.Max(p => p.V))).ToList();
        var result = new List<((double U, double V), (double U, double V))>(segments.Count);
        var hidden = new List<(double T0, double T1)>();
        foreach (var (a, b) in segments)
        {
            hidden.Clear();
            double su0 = Math.Min(a.U, b.U), su1 = Math.Max(a.U, b.U), sv0 = Math.Min(a.V, b.V), sv1 = Math.Max(a.V, b.V);
            foreach (var box in boxes)
            {
                if (box.U1 < su0 || box.U0 > su1 || box.V1 < sv0 || box.V0 > sv1) continue;
                if (ClipConvex(box.Poly, a, b, out double t0, out double t1) && t1 - t0 > 1e-9) hidden.Add((t0, t1));
            }
            if (hidden.Count == 0)
            {
                result.Add((a, b));
                continue;
            }
            hidden.Sort((x, y) => x.T0.CompareTo(y.T0));
            double length = Math.Sqrt((b.U - a.U) * (b.U - a.U) + (b.V - a.V) * (b.V - a.V));
            double minPiece = length > 0 ? 1e-4 / length : 1;
            double cursor = 0;
            foreach (var (t0, t1) in hidden)
            {
                if (t0 - cursor > minPiece) result.Add((Lerp(a, b, cursor), Lerp(a, b, t0)));
                cursor = Math.Max(cursor, t1);
            }
            if (1 - cursor > minPiece) result.Add((Lerp(a, b, cursor), b));
        }
        return result;

        static (double U, double V) Lerp((double U, double V) p, (double U, double V) q, double t) => (p.U + (q.U - p.U) * t, p.V + (q.V - p.V) * t);
    }

    /// <summary>Интервал параметра t ∈ [0, 1], на котором отрезок AB лежит в выпуклом многоугольнике (с границей).</summary>
    private static bool ClipConvex(List<(double U, double V)> poly, (double U, double V) a, (double U, double V) b, out double t0, out double t1)
    {
        t0 = 0;
        t1 = 1;
        double area = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            area += p.U * q.V - q.U * p.V;
        }
        if (Math.Abs(area) < 1e-12) return false;
        double sign = area > 0 ? 1 : -1;
        double du = b.U - a.U, dv = b.V - a.V;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            double eu = q.U - p.U, ev = q.V - p.V;
            if (eu * eu + ev * ev < 1e-16) continue;
            // Внутренняя нормаль ребра: для обхода против часовой — (−ev, eu).
            double nu = -ev * sign, nv = eu * sign;
            double num = nu * (a.U - p.U) + nv * (a.V - p.V) + 1e-7;
            double den = nu * du + nv * dv;
            if (Math.Abs(den) < 1e-15)
            {
                if (num < 0) return false;
                continue;
            }
            double t = -num / den;
            if (den > 0) t0 = Math.Max(t0, t);
            else t1 = Math.Min(t1, t);
            if (t0 > t1) return false;
        }
        return true;
    }

    /// <summary>Проверка окклюзии: середина ни одного видимого участка не лежит внутри сечения.</summary>
    internal static bool NoVisibleInside(IReadOnlyList<((double U, double V) A, (double U, double V) B)> visible, IReadOnlyList<List<(double U, double V)>> occluders)
    {
        var boxes = occluders.Where(o => o.Count >= 3)
            .Select(o => (Poly: o, U0: o.Min(p => p.U), U1: o.Max(p => p.U), V0: o.Min(p => p.V), V1: o.Max(p => p.V))).ToList();
        foreach (var (a, b) in visible)
        {
            var mid = ((a.U + b.U) / 2, (a.V + b.V) / 2);
            foreach (var box in boxes)
                if (mid.Item1 >= box.U0 && mid.Item1 <= box.U1 && mid.Item2 >= box.V0 && mid.Item2 <= box.V1 && InsideConvex(box.Poly, mid, 1e-4)) return false;
        }
        return true;
    }

    /// <summary>Точка строго внутри выпуклого многоугольника (с запасом eps от рёбер).</summary>
    internal static bool InsideConvex(List<(double U, double V)> poly, (double U, double V) pt, double eps)
    {
        if (poly.Count < 3) return false;
        double area = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            area += p.U * q.V - q.U * p.V;
        }
        if (Math.Abs(area) < 1e-12) return false;
        double sign = area > 0 ? 1 : -1;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            double eu = q.U - p.U, ev = q.V - p.V, len = Math.Sqrt(eu * eu + ev * ev);
            if (len < 1e-8) continue;
            if ((-ev * sign * (pt.U - p.U) + eu * sign * (pt.V - p.V)) / len <= eps) return false;
        }
        return true;
    }

    private static (long, long, long, long) Key(Vector2 p, Vector2 q) =>
        ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(q.X * 1e4), (long)Math.Round(q.Y * 1e4));

    /// <summary>Отсечение многоугольника прямоугольником [u0, u1] × [v0, v1].</summary>
    private static List<(double U, double V)> ClipRect(List<(double U, double V)> poly, double u0, double u1, double v0, double v1)
    {
        List<(double U, double V)> Clip(List<(double U, double V)> input, Func<(double U, double V), double> dist)
        {
            var output = new List<(double U, double V)>();
            for (int i = 0; i < input.Count; i++)
            {
                var cur = input[i];
                var prev = input[(i + input.Count - 1) % input.Count];
                double dc = dist(cur), dp = dist(prev);
                if (dc <= 0)
                {
                    if (dp > 0) output.Add(Lerp(prev, cur, dp / (dp - dc)));
                    output.Add(cur);
                }
                else if (dp <= 0) output.Add(Lerp(prev, cur, dp / (dp - dc)));
            }
            return output;
        }
        static (double, double) Lerp((double U, double V) a, (double U, double V) b, double t) => (a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t);
        var r = poly;
        r = Clip(r, p => u0 - p.U); if (r.Count < 3) return r;
        r = Clip(r, p => p.U - u1); if (r.Count < 3) return r;
        r = Clip(r, p => v0 - p.V); if (r.Count < 3) return r;
        return Clip(r, p => p.V - v1);
    }

    private static ((double U, double V), (double U, double V))? ClipSegment2D((double U, double V) a, (double U, double V) b, double u0, double u1, double v0, double v1)
    {
        double t0 = 0, t1 = 1, du = b.U - a.U, dv = b.V - a.V;
        bool Edge(double p, double q)
        {
            if (Math.Abs(p) < 1e-12) return q >= -1e-9;
            double r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
        if (!Edge(-du, a.U - u0) || !Edge(du, u1 - a.U) || !Edge(-dv, a.V - v0) || !Edge(dv, v1 - a.V)) return null;
        return ((a.U + du * t0, a.V + dv * t0), (a.U + du * t1, a.V + dv * t1));
    }

    // ═════════════════════════════ Оси, размеры, отметки ═════════════════════════════

    /// <summary>
    /// Координационные оси плана и трёхрядные замкнутые размерные цепочки по ГОСТ 2.307-2011 / ГОСТ 21.501 снизу и слева: ряд 1
    /// (ближний, 8 мм от контура) — проёмы и простенки наружной стены от грани до грани, ряд 2 — межосевые расстояния, ряд 3 — общий
    /// размер между крайними осями (Σ ряда 2 ≡ ряду 3). Значения — разности точек, округлённых до целых мм (Σ звеньев ≡ габариту
    /// цепочки), узкие звенья — вразбежку или на полке без наложения текстов.
    /// </summary>
    private static void DrawPlanAxes(PdfVectorPage page, Mapper m, SheetContent c, double x0, double y0, double x1, double y1, DrawingDimensionLayout layout)
    {
        double axisBottom = y0 - 30, axisTop = y1 + 6, axisLeft = x0 - 30, axisRight = x1 + 6;
        var cols = c.AxisMarks;
        var rows = c.RowMarks;
        var xs = cols.Select(a => (a.Label, X: m.P(a.U, 0).X)).OrderBy(a => a.X).ToList();
        var ys = rows.Select(r => (r.Label, Y: m.P(0, r.V).Y)).OrderBy(r => r.Y).ToList();

        page.LineWidth(Thin);
        page.Dash(8, 1.5, 0.5, 1.5);
        foreach (var (_, x) in xs) page.Line(x, axisBottom, x, axisTop);
        foreach (var (_, y) in ys) page.Line(axisLeft, y, axisRight, y);
        page.Dash();

        foreach (var (label, x) in xs)
        {
            AxisBubble(page, x, axisBottom - Bubble, label);
            AxisBubble(page, x, axisTop + Bubble, label);
        }
        foreach (var (label, y) in ys)
        {
            AxisBubble(page, axisLeft - Bubble, y, label);
            AxisBubble(page, axisRight + Bubble, y, label);
        }

        var e = c.Extents;
        void Side(string side, bool horizontal, double lo, double hi, List<double> axes, List<double> facade, double edge)
        {
            if (hi - lo < 0.05) return;
            if (facade.Count >= 2) Chain(page, m, layout, side, 1, horizontal, facade, edge - 8);
            var ordered = axes.OrderBy(a => a).ToList();
            if (ordered.Count >= 2)
            {
                Chain(page, m, layout, side, 2, horizontal, ordered, edge - 16);
                Chain(page, m, layout, side, 3, horizontal, [ordered[0], ordered[^1]], edge - 24);
            }
            else Chain(page, m, layout, side, 3, horizontal, [lo, hi], edge - 24);
        }
        Side("низ плана", true, e.MinU, e.MaxU, cols.Select(a => a.U).ToList(), c.FacadeU, y0);
        Side("левая сторона плана", false, e.MinV, e.MaxV, rows.Select(r => r.V).ToList(), c.FacadeV, x0);
    }

    /// <summary>Оси на разрезе: вертикальные штрихпунктирные линии, марки снизу, межосевая цепочка и общий размер под разрезом.</summary>
    private static void DrawSectionAxes(PdfVectorPage page, Mapper m, List<(string Label, double U)> cols, double y0, double y1,
        (double MinU, double MaxU, double MinV, double MaxV) extents, DrawingDimensionLayout layout)
    {
        var xs = cols.Select(c => (c.Label, X: m.P(c.U, 0).X)).OrderBy(c => c.X).ToList();
        double axisBottom = y0 - 20, axisTop = y1 + 4;
        page.LineWidth(Thin);
        page.Dash(8, 1.5, 0.5, 1.5);
        foreach (var (_, x) in xs) page.Line(x, axisBottom, x, axisTop);
        page.Dash();
        foreach (var (label, x) in xs) AxisBubble(page, x, axisBottom - Bubble, label);
        if (cols.Count >= 2)
        {
            var us = cols.Select(c => c.U).OrderBy(u => u).ToList();
            Chain(page, m, layout, "разрез, оси", 2, true, us, y0 - 7);
            Chain(page, m, layout, "разрез, оси", 3, true, [us[0], us[^1]], y0 - 14);
        }
    }

    /// <summary>Цепочка высот этажей слева от разреза (ряд 2) и общая высота (ряд 3) по отметкам уровней.</summary>
    private static void DrawStoreyChain(PdfVectorPage page, Mapper m, List<(double V, string Text)> marks, double x0, DrawingDimensionLayout layout)
    {
        var vs = new List<double>();
        foreach (double v in marks.Select(x => x.V).OrderBy(v => v))
            if (vs.Count == 0 || v - vs[^1] > 0.0015) vs.Add(v);
        if (vs.Count < 2) return;
        Chain(page, m, layout, "разрез, высоты", 2, false, vs, x0 - 9);
        Chain(page, m, layout, "разрез, высоты", 3, false, [vs[0], vs[^1]], x0 - 17);
    }

    private static void AxisBubble(PdfVectorPage page, double cx, double cy, string label)
    {
        page.LineWidth(Thin);
        page.FillGray(1);
        page.Circle(cx, cy, Bubble, fill: true, stroke: true);
        page.FillGray(0);
        double h = label.Length > 2 ? 2.5 : 3.5;
        page.Text(label, cx, cy - h / 2, h, PdfTextAlign.Center);
    }

    private const double DimText = 2.5;

    /// <summary>
    /// Размерная цепочка по точкам модели (м чертежа): точки округляются до целых мм модели, звенья печатаются разностями (Σ ≡ габариту),
    /// размерная линия с выступом 2 мм, засечки 45° основной линией, выносные линии от точек. Текст звена — над линией по центру, если
    /// помещается; иначе вразбежку под линией, затем на полке (выноска 45° и горизонтальная полка) — первое место без наложения.
    /// </summary>
    private static void Chain(PdfVectorPage page, Mapper m, DrawingDimensionLayout layout, string side, int row, bool horizontal, List<double> points, double offset)
    {
        if (points.Count < 2) return;
        var mm = points.Select(p => (long)Math.Round(p * 1000)).ToList();
        var model = mm.Select(v => v / 1000.0).ToList();
        var at = model.Select(v => horizontal ? m.P(v, 0).X : m.P(0, v).Y).ToList();
        var values = new List<long>();
        for (int i = 0; i + 1 < mm.Count; i++) values.Add(mm[i + 1] - mm[i]);

        page.LineWidth(Thin);
        if (horizontal) page.Line(at[0] - 2, offset, at[^1] + 2, offset);
        else page.Line(offset, at[0] - 2, offset, at[^1] + 2);
        foreach (double p in at)
        {
            page.LineWidth(Main);
            if (horizontal) page.Line(p - 1.2, offset - 1.2, p + 1.2, offset + 1.2);
            else page.Line(offset - 1.2, p - 1.2, offset + 1.2, p + 1.2);
            page.LineWidth(Thin);
            // Выносная линия: с выступом 1–2 мм за размерную линию.
            if (horizontal) page.Line(p, offset - 2, p, offset + 2.5);
            else page.Line(offset - 2.5, p, offset + 2, p);
        }

        // Локальная система цепочки: a — вдоль, c — поперёк (наружу от размерной линии, текст «над» линией).
        DimensionTextBox Box(double a0, double a1, double c0, double c1, string text) => horizontal
            ? new DimensionTextBox(a0, offset + c0, a1, offset + c1, text, $"{side}, ряд {row}")
            : new DimensionTextBox(offset - c1, a0, offset - c0, a1, text, $"{side}, ряд {row}");
        void Put(string text, double a, double c, PdfTextAlign align)
        {
            if (horizontal) page.Text(text, a, offset + c, DimText, align);
            else page.Text(text, offset - c, a, DimText, align, 90);
        }
        (double, double) Pt(double a, double c) => horizontal ? (a, offset + c) : (offset - c, a);

        for (int i = 0; i < values.Count; i++)
        {
            string text = values[i].ToString("0", Ru);
            double w = page.TextWidth(text, DimText);
            double mid = (at[i] + at[i + 1]) / 2, length = Math.Abs(at[i + 1] - at[i]);
            bool fits = w + 0.8 <= length;
            var above = Box(mid - w / 2, mid + w / 2, 1.0, 1.0 + DimText, text);
            var below = Box(mid - w / 2, mid + w / 2, -1.1 - DimText, -1.1, text);
            // Текст шире звена допустим над линией, пока он не задевает соседние числа (иначе — вразбежку под линией, затем на полке).
            if (layout.IsFree(above) && (fits || w <= length + 4))
            {
                Put(text, mid, 1.0, PdfTextAlign.Center);
                layout.Texts.Add(above);
                if (!fits) layout.Relocated++;
                continue;
            }
            if (layout.IsFree(below) && (fits || w <= length + 4))
            {
                Put(text, mid, -1.1 - DimText + 0.35, PdfTextAlign.Center);
                layout.Texts.Add(below);
                layout.Relocated++;
                continue;
            }
            // Полка: выноска от середины звена под 45° и горизонтальная полка вправо или влево, выше на 4,5…12 мм.
            bool placed = false;
            foreach (double lift in new[] { 4.5, 8.0, 11.5 })
                foreach (int dir in new[] { 1, -1 })
                {
                    if (placed) break;
                    double a0 = dir > 0 ? mid + lift * 0.5 : mid - lift * 0.5 - w - 1;
                    var shelf = Box(a0 + 0.5, a0 + 0.5 + w, lift + 0.3, lift + 0.3 + DimText, text);
                    if (!layout.IsFree(shelf)) continue;
                    page.LineWidth(Thin);
                    var (sx, sy) = Pt(mid, 0);
                    var (kx, ky) = Pt(dir > 0 ? a0 : a0 + w + 1, lift);
                    page.Line(sx, sy, kx, ky);
                    var (ex, ey) = Pt(dir > 0 ? a0 + w + 1 : a0, lift);
                    page.Line(kx, ky, ex, ey);
                    Put(text, a0 + 0.5, lift + 0.3, PdfTextAlign.Left);
                    layout.Texts.Add(shelf);
                    layout.Relocated++;
                    placed = true;
                    break;
                }
            if (placed) continue;
            // Места нет — над линией (наложение покажет автопроверка).
            Put(text, mid, 1.0, PdfTextAlign.Center);
            layout.Texts.Add(above);
        }
        string title = row switch { 1 => "проёмы и простенки", 2 => "межосевые / по уровням", _ => "общий габарит" };
        layout.Chains.Add(new DimensionChain(side, row, $"{side}, ряд {row} ({title})", horizontal, at, mm, values, points, m.K));
    }

    /// <summary>Высотные отметки справа от разреза: выносная линия, знак отметки (стрелка 45°), полка и значение.</summary>
    private static void DrawLevelMarks(PdfVectorPage page, Mapper m, List<(double V, string Text)> marks, double xRight, DrawingDimensionLayout layout)
    {
        double lastY = double.MinValue;
        int shift = 0;
        foreach (var (v, text) in marks.OrderBy(x => x.V))
        {
            double y = m.P(0, v).Y;
            shift = y - lastY < 7 ? shift + 1 : 0;
            lastY = y;
            double xm = xRight + 10 + shift * 20;
            page.LineWidth(Thin);
            page.Line(xRight + 1.5, y, xm + 2.5, y);
            page.LineWidth(Medium);
            page.Line(xm, y, xm - 2.2, y + 2.2);
            page.Line(xm, y, xm + 2.2, y + 2.2);
            page.LineWidth(Thin);
            page.Line(xm, y, xm, y + 6);
            double tw = page.TextWidth(text, 2.5);
            page.Line(xm, y + 6, xm + tw + 2, y + 6);
            page.Text(text, xm + 1, y + 6.8, 2.5);
            layout.Texts.Add(new DimensionTextBox(xm + 1, y + 6.8, xm + 1 + tw, y + 6.8 + 2.5, text, "отметки уровней"));
        }
    }

    // ═════════════════════════════ Рамка, графы, штамп, спецификация ═════════════════════════════

    private static void DrawFrame(PdfVectorPage page, double w, double h, GostSheetFormat format)
    {
        // Внешняя рамка по краю листа (тонкая) и рамка поля чертежа (основная): 20 мм слева, по 5 мм с остальных сторон.
        page.LineWidth(Thin);
        page.Rect(0, 0, w, h);
        page.LineWidth(0.7);
        page.Rect(20, 5, w - 25, h - 10);

        // Дополнительные графы на левом поле (ГОСТ Р 21.101): 19 — Инв. № подл., 20 — Подп. и дата, 21 — Взам. инв. №.
        double x0 = 8, x1 = 20;
        var graphs = new (string Title, double Height)[] { ("Инв. № подл.", 25), ("Подп. и дата", 35), ("Взам. инв. №", 25) };
        double y = 5;
        page.LineWidth(Main);
        page.Rect(x0, 5, x1 - x0, 85);
        foreach (var (title, height) in graphs)
        {
            page.LineWidth(Main);
            page.Line(x0, y + height, x1, y + height);
            page.Line(x0 + 5, y, x0 + 5, y + height);
            page.Text(title, x0 + 3.6, y + height / 2, 2.5, PdfTextAlign.Center, 90);
            y += height;
        }

        // Таблица «Согласовано» на левом поле над графами.
        double sy = 95;
        page.LineWidth(Main);
        page.Rect(x0, sy, x1 - x0, 75);
        page.Line(x0 + 5, sy, x0 + 5, sy + 75);
        page.Text("Согласовано", x0 + 3.6, sy + 37.5, 2.5, PdfTextAlign.Center, 90);
        page.LineWidth(Thin);
        foreach (double row in new double[] { 25, 50 }) page.Line(x0 + 5, sy + row, x1, sy + row);

        // Обозначение формата и «Копировал» под рамкой.
        page.Text("Копировал", w - 150, 1.6, 2.5);
        page.Text($"Формат {FormatTitle(format)}", w - 30, 1.6, 2.5);
    }

    /// <summary>
    /// Основная надпись по форме 3 (185 × 55 мм): слева графы изменений и подписей (10/10/10/10/15/10 мм, строки по 5 мм),
    /// справа — обозначение (15 мм), наименование объекта (10 мм), наименование здания со стадией/листом/листов (15 мм)
    /// и наименование изображения с организацией (15 мм).
    /// </summary>
    private static void DrawStamp(PdfVectorPage page, double x, double y, string code, string objectName, string buildingName, string sheetName,
        int sheetNo = 1, int sheetCount = 1)
    {
        double top = y + StampH;
        page.FillGray(1);
        page.Rect(x, y, StampW, StampH, fill: true, stroke: false);
        page.FillGray(0);
        page.LineWidth(Main);
        page.Rect(x, y, StampW, StampH);

        // Левая часть: 11 строк по 5 мм.
        double[] cols = [10, 10, 10, 10, 15, 10];
        double cx = x;
        page.LineWidth(Main);
        for (int i = 0; i < cols.Length - 1; i++)
        {
            cx += cols[i];
            // Линии «Изм. | Кол.уч.» и «Лист | № док.» — только в графах изменений: ниже должность и фамилия занимают по 20 мм.
            page.Line(cx, i is 0 or 2 ? top - 15 : y, cx, top);
        }
        page.Line(x + 65, y, x + 65, top);
        for (int r = 1; r <= 10; r++)
        {
            double ly = top - r * 5;
            page.LineWidth(r == 3 || r == 2 ? Main : Thin);
            page.Line(x, ly, x + 65, ly);
        }
        var headers = new[] { "Изм.", "Кол.уч.", "Лист", "№ док.", "Подп.", "Дата" };
        cx = x;
        for (int i = 0; i < headers.Length; i++)
        {
            page.Text(headers[i], cx + cols[i] / 2, top - 14.1, 1.8, PdfTextAlign.Center);
            cx += cols[i];
        }
        var roles = new[] { ("Разраб.", "Studio Pro"), ("Пров.", ""), ("ГИП", ""), ("", ""), ("Н.контр.", ""), ("Утв.", "") };
        for (int i = 0; i < roles.Length; i++)
        {
            double ly = top - 15 - (i + 1) * 5 + 1.3;
            page.Text(roles[i].Item1, x + 1, ly, 2.0);
            page.Text(roles[i].Item2, x + 21, ly, 2.0);
            if (i == 0) page.Text(DateTime.Now.ToString("MM.yy", Ru), x + 55 + 5, ly, 2.0, PdfTextAlign.Center);
        }

        // Правая часть.
        double rx = x + 65;
        page.LineWidth(Main);
        page.Line(rx, top - 15, x + StampW, top - 15);
        page.Line(rx, top - 25, x + StampW, top - 25);
        page.Line(rx, top - 40, x + StampW, top - 40);
        page.Line(rx + 70, y, rx + 70, top - 25);
        page.Line(rx + 85, top - 40, rx + 85, top - 25);
        page.Line(rx + 100, top - 40, rx + 100, top - 25);
        page.LineWidth(Thin);
        page.Line(rx + 70, top - 30, x + StampW, top - 30);

        page.Text(code, rx + 60, top - 9.5, 5, PdfTextAlign.Center);
        FitText(page, objectName, rx + 60, top - 21.6, 3.5, 116);
        FitText(page, buildingName, rx + 35, top - 33.8, 3.0, 66);
        page.Text("Стадия", rx + 77.5, top - 28.8, 2.0, PdfTextAlign.Center);
        page.Text("Лист", rx + 92.5, top - 28.8, 2.0, PdfTextAlign.Center);
        page.Text("Листов", rx + 110, top - 28.8, 2.0, PdfTextAlign.Center);
        page.Text("Р", rx + 77.5, top - 36.6, 3.5, PdfTextAlign.Center);
        page.Text(sheetNo.ToString(Ru), rx + 92.5, top - 36.6, 3.5, PdfTextAlign.Center);
        page.Text(sheetCount.ToString(Ru), rx + 110, top - 36.6, 3.5, PdfTextAlign.Center);
        WrapText(page, sheetName, rx + 2, top - 44.5, 2.5, 66, 3);
        page.Text("Architect Studio Pro", rx + 95, top - 48.6, 3.0, PdfTextAlign.Center);
        page.Text("Проектное бюро", rx + 95, top - 52.4, 2.0, PdfTextAlign.Center);
    }

    /// <summary>
    /// Основная надпись последующих листов по форме 2а (185 × 15 мм): графы изменений (10/10/10/10/15/10 мм, три строки по 5 мм,
    /// заголовки — в нижней), обозначение документа (110 мм) и номер листа (10 мм).
    /// </summary>
    private static void DrawStampNext(PdfVectorPage page, double x, double y, string code, int sheetNo)
    {
        double top = y + Stamp2aH;
        page.FillGray(1);
        page.Rect(x, y, StampW, Stamp2aH, fill: true, stroke: false);
        page.FillGray(0);
        page.LineWidth(Main);
        page.Rect(x, y, StampW, Stamp2aH);
        double[] cols = [10, 10, 10, 10, 15, 10];
        double cx = x;
        for (int i = 0; i < cols.Length; i++)
        {
            cx += cols[i];
            page.Line(cx, y, cx, top);
        }
        page.Line(x + StampW - 10, y, x + StampW - 10, top);
        page.Line(x + StampW - 10, top - 7, x + StampW, top - 7);
        page.LineWidth(Thin);
        page.Line(x, top - 5, x + 65, top - 5);
        page.LineWidth(Main);
        page.Line(x, y + 5, x + 65, y + 5);
        var headers = new[] { "Изм.", "Кол.уч.", "Лист", "№ док.", "Подп.", "Дата" };
        cx = x;
        for (int i = 0; i < headers.Length; i++)
        {
            page.Text(headers[i], cx + cols[i] / 2, y + 1.6, 1.8, PdfTextAlign.Center);
            cx += cols[i];
        }
        FitText(page, code, x + 65 + 55, y + 5.2, 5, 104);
        page.Text("Лист", x + StampW - 5, top - 4.6, 2.0, PdfTextAlign.Center);
        page.Text(sheetNo.ToString(Ru), x + StampW - 5, y + 2.4, 3.5, PdfTextAlign.Center);
    }

    private static void FitText(PdfVectorPage page, string text, double cx, double y, double height, double maxWidth)
    {
        double h = height;
        while (h > 1.6 && page.TextWidth(text, h) > maxWidth) h -= 0.25;
        page.Text(text, cx, y, h, PdfTextAlign.Center);
    }

    private static void WrapText(PdfVectorPage page, string text, double x, double y, double height, double maxWidth, int maxLines)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        string current = string.Empty;
        foreach (var word in words)
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (page.TextWidth(candidate, height) > maxWidth && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        for (int i = 0; i < Math.Min(lines.Count, maxLines); i++) page.Text(lines[i], x, y - i * (height + 1.4), height);
    }

    /// <summary>Спецификация элементов над основной надписью: Марка (15) · Наименование (70) · Сечение, мм (40) · Кол., шт. (20) · Объём бетона, м³ (40).</summary>
    private static void DrawSpecification(PdfVectorPage page, double x, double bottom, List<SpecRow> rows, string title = "Спецификация элементов")
    {
        double[] cols = [15, 70, 40, 20, 40];
        string[] headers = ["Марка", "Наименование", "Сечение, мм", "Кол., шт.", "Объём бетона, м³"];
        double headerH = 15, rowH = 8;
        double totalH = headerH + rows.Count * rowH;
        double top = bottom + totalH;

        page.FillGray(1);
        page.Rect(x, bottom, StampW, totalH, fill: true, stroke: false);
        page.FillGray(0);
        page.Text(title, x + StampW / 2, top + 3, 3.5, PdfTextAlign.Center);
        page.LineWidth(Main);
        page.Rect(x, bottom, StampW, totalH);
        page.Line(x, top - headerH, x + StampW, top - headerH);
        double cx = x;
        for (int i = 0; i < cols.Length; i++)
        {
            if (i > 0) page.Line(cx, bottom, cx, top);
            page.Text(headers[i], cx + cols[i] / 2, top - headerH / 2 - 1.2, 2.5, PdfTextAlign.Center);
            cx += cols[i];
        }
        page.LineWidth(Thin);
        for (int r = 0; r < rows.Count; r++)
        {
            double ry = top - headerH - (r + 1) * rowH;
            if (r < rows.Count - 1) page.Line(x, ry, x + StampW, ry);
            var row = rows[r];
            double ty = ry + rowH / 2 - 1.25;
            page.Text(row.Mark, x + cols[0] / 2, ty, 2.5, PdfTextAlign.Center);
            FitLeft(page, row.Name, x + cols[0] + 1.5, ty, 2.5, cols[1] - 3);
            FitCenter(page, row.Section, x + cols[0] + cols[1] + cols[2] / 2, ty, 2.5, cols[2] - 2);
            page.Text(row.Count.ToString(Ru), x + cols[0] + cols[1] + cols[2] + cols[3] / 2, ty, 2.5, PdfTextAlign.Center);
            page.Text(row.Volume is { } v ? v.ToString("0.00", Ru) : "—", x + StampW - cols[4] / 2, ty, 2.5, PdfTextAlign.Center);
        }
    }

    private const double SteelHeaderH = 18, SteelRowH = 6;

    /// <summary>
    /// Ведомость расхода стали на элемент, кг (ГОСТ 21.501): Марка элемента | Изделия арматурные — арматура класса А240 ГОСТ 5781-82
    /// (Ø6, Ø8, Ø10, Итого) и рабочая А400 / А500С (Ø8…Ø25, при наличии Ø28; Итого) | Изделия закладные — прокат С245 | Всего.
    /// Строки 6 мм, шапка из трёх ярусов по 6 мм; ширина таблицы — 185 мм, как у основной надписи.
    /// </summary>
    private static void DrawSteelStatement(PdfVectorPage page, double x, double bottom, List<SteelRow> rows, RebarClass rebarClass)
    {
        int[] a240 = [6, 8, 10];
        var working = new List<int> { 8, 10, 12, 16, 20, 25 };
        foreach (int d in rows.SelectMany(r => r.Steel.Working.Keys).Distinct().Order())
            if (!working.Contains(d)) working.Add(d);
        working.Sort();
        bool a500 = rebarClass == RebarClass.A500C;
        string workingTitle = a500 ? "Арматура класса А500С ГОСТ 34028-2016" : "Арматура класса А400 ГОСТ 5781-82";

        const double markW = 16, plateW = 15, totalW = 16;
        int cols = a240.Length + 1 + working.Count + 1;
        double cw = (StampW - markW - plateW - totalW) / cols;
        double totalH = SteelHeaderH + rows.Count * SteelRowH;
        double top = bottom + totalH;
        double r1 = top - 6, r2 = top - 12, r3 = top - SteelHeaderH;
        double xA = x + markW, xW = xA + (a240.Length + 1) * cw, xP = xW + (working.Count + 1) * cw, xT = xP + plateW;

        page.FillGray(1);
        page.Rect(x, bottom, StampW, totalH, fill: true, stroke: false);
        page.FillGray(0);
        page.Text("Ведомость расхода стали на элемент, кг", x + StampW / 2, top + 3, 3.5, PdfTextAlign.Center);

        // Сетка шапки — основной линией.
        page.LineWidth(Main);
        page.Rect(x, bottom, StampW, totalH);
        page.Line(x, r3, x + StampW, r3);
        page.Line(xA, bottom, xA, top);
        page.Line(xP, bottom, xP, top);
        page.Line(xT, bottom, xT, top);
        page.Line(xA, r1, xP, r1);
        page.Line(xA, r2, xP, r2);
        page.Line(xW, bottom, xW, r1);
        for (int i = 1; i <= a240.Length; i++) page.Line(xA + i * cw, bottom, xA + i * cw, r2);
        for (int i = 1; i <= working.Count; i++) page.Line(xW + i * cw, bottom, xW + i * cw, r2);

        FitCenter(page, "Марка", x + markW / 2, top - 7.6, 2.5, markW - 1.5);
        FitCenter(page, "элемента", x + markW / 2, top - 11.2, 2.5, markW - 1.5);
        page.Text("Изделия арматурные", (xA + xP) / 2, top - 4.2, 2.5, PdfTextAlign.Center);
        FitCenter(page, "Арматура класса А240 ГОСТ 5781-82", (xA + xW) / 2, top - 10.2, 2.0, xW - xA - 1.5);
        FitCenter(page, workingTitle, (xW + xP) / 2, top - 10.2, 2.0, xP - xW - 1.5);
        FitCenter(page, "Изделия", xP + plateW / 2, top - 4.6, 2.0, plateW - 1.5);
        FitCenter(page, "закладные", xP + plateW / 2, top - 7.6, 2.0, plateW - 1.5);
        FitCenter(page, "Прокат", xP + plateW / 2, top - 11.6, 2.0, plateW - 1.5);
        page.Text("С245", xP + plateW / 2, top - 15.6, 2.5, PdfTextAlign.Center);
        page.Text("Всего", xT + totalW / 2, top - 10.2, 2.5, PdfTextAlign.Center);
        for (int i = 0; i < a240.Length; i++) page.Text($"Ø{a240[i]}", xA + (i + 0.5) * cw, r3 + 1.9, 2.5, PdfTextAlign.Center);
        page.Text("Итого", xA + (a240.Length + 0.5) * cw, r3 + 1.9, 2.0, PdfTextAlign.Center);
        for (int i = 0; i < working.Count; i++) page.Text($"Ø{working[i]}", xW + (i + 0.5) * cw, r3 + 1.9, 2.5, PdfTextAlign.Center);
        page.Text("Итого", xW + (working.Count + 0.5) * cw, r3 + 1.9, 2.0, PdfTextAlign.Center);

        static string Kg(double v) => v > 0.005 ? v.ToString(v >= 100 ? "0" : "0.0", Ru) : "—";
        page.LineWidth(Thin);
        for (int r = 0; r < rows.Count; r++)
        {
            double ry = r3 - (r + 1) * SteelRowH;
            if (r < rows.Count - 1) page.Line(x, ry, x + StampW, ry);
            var row = rows[r];
            var s = row.Steel;
            double ty = ry + SteelRowH / 2 - 1.1;
            page.Text(row.Mark, x + markW / 2, ty, 2.5, PdfTextAlign.Center);
            for (int i = 0; i < a240.Length; i++)
                FitCenter(page, Kg(s.A240.GetValueOrDefault(a240[i])), xA + (i + 0.5) * cw, ty, 2.2, cw - 1);
            FitCenter(page, Kg(s.A240Total), xA + (a240.Length + 0.5) * cw, ty, 2.2, cw - 1);
            for (int i = 0; i < working.Count; i++)
                FitCenter(page, Kg(s.Working.GetValueOrDefault(working[i])), xW + (i + 0.5) * cw, ty, 2.2, cw - 1);
            FitCenter(page, Kg(s.WorkingTotal), xW + (working.Count + 0.5) * cw, ty, 2.2, cw - 1);
            FitCenter(page, Kg(s.PlatesTotal), xP + plateW / 2, ty, 2.2, plateW - 1);
            FitCenter(page, Kg(s.Total), xT + totalW / 2, ty, 2.5, totalW - 1);
        }
    }

    private static void FitLeft(PdfVectorPage page, string text, double x, double y, double height, double maxWidth)
    {
        double h = height;
        while (h > 1.5 && page.TextWidth(text, h) > maxWidth) h -= 0.2;
        page.Text(text, x, y, h);
    }

    private static void FitCenter(PdfVectorPage page, string text, double cx, double y, double height, double maxWidth)
    {
        double h = height;
        while (h > 1.5 && page.TextWidth(text, h) > maxWidth) h -= 0.2;
        page.Text(text, cx, y, h, PdfTextAlign.Center);
    }
}
