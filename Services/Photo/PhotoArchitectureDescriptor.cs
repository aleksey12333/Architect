using System.Globalization;
using System.Text;

namespace AIArchitectStudio.Services.Photo;

/// <summary>Морфология здания на снимке (профиль подъёма в студии «Реконструкция по фото и плану»).</summary>
public enum PhotoMorphology
{
    /// <summary>Определить по снимку.</summary>
    Auto,
    /// <summary>Радиусный / изогнутый фасад (дуга в плане).</summary>
    Curved,
    /// <summary>Скруглённый корпус (прямоугольник со скруглёнными углами).</summary>
    Rounded,
    /// <summary>Каскадный с уступами и террасами.</summary>
    Cascading,
    /// <summary>Треугольный / скошенный (острый угол на стрелке).</summary>
    Triangular,
    /// <summary>Ротонда под куполом с колоннадой.</summary>
    Rotunda,
    /// <summary>Мост / арка / большепролётный зал под сводом.</summary>
    ArchSpan,
    /// <summary>Скатный дом.</summary>
    Pitched,
    /// <summary>Прямоугольный корпус.</summary>
    Box
}

/// <summary>Покрытие по снимку (Mansard — волна 4.1: мансардные этажи с крутым скатом, «утюг»).</summary>
public enum PhotoRoofKind { Flat, Parapet, Dome, Vault, Gable, Cone, Mansard }

/// <summary>Цвет палитры фасада: «#RRGGBB», доля пикселей здания, роль, центр области на снимке (0…1).</summary>
public sealed record PhotoPaletteColor(string Hex, double Share, string Role, double X, double Y);

/// <summary>Ярус (уступ) силуэта: границы по ширине снимка (0…1), высота верха над основанием (доля высоты), этажей.</summary>
public sealed record PhotoTier(double X0, double X1, double TopFraction, int Floors);

/// <summary>
/// Оптический паспорт здания по снимку (Визуальное ядро): силуэт и ярусы, морфология и уверенность с доводами, этажность и высота
/// этажа по периодичности фасада, оценка размеров, кривизна фасада (стрела дуги к хорде), радиус скругления углов, угол на стрелке
/// (по точкам схода карниза и цоколя), стилобат, доля и ритм остекления, тип покрытия, купол и колоннада, палитра фасада HEX.
/// Координаты оверлеев — доли ширины и высоты снимка (0…1, y вниз).
/// </summary>
public sealed class PhotoArchitectureDescriptor
{
    public string SourceName { get; set; } = string.Empty;
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    public PhotoMorphology Morphology { get; set; } = PhotoMorphology.Box;
    public double Confidence { get; set; }
    public List<string> Evidence { get; } = [];

    public int Floors { get; set; } = 5;
    public double FloorHeight { get; set; } = 3.3;
    public double HeightM { get; set; }
    public double WidthM { get; set; }
    public double DepthM { get; set; }

    /// <summary>Отношение стрелы дуги фасада к его хорде (0 — прямой фасад).</summary>
    public double ArcBulgeRatio { get; set; }

    /// <summary>Отношение радиуса скругления углов к меньшей стороне плана.</summary>
    public double CornerRadiusRatio { get; set; }

    /// <summary>Угол между фасадами на ближнем углу (градусы; 0 — не определён, 90 — прямой угол).</summary>
    public double CornerAngleDeg { get; set; }

    public int PodiumFloors { get; set; }
    public double PodiumWidthRatio { get; set; } = 1;

    public double GlazingRatio { get; set; }
    public int WindowColumns { get; set; }
    public double WindowPeriodPx { get; set; }
    public double FloorPeriodPx { get; set; }

    public PhotoRoofKind Roof { get; set; } = PhotoRoofKind.Flat;
    public double DomeWidthRatio { get; set; }
    public double DomeHeightRatio { get; set; }
    public int ColonnadeColumns { get; set; }

    public List<PhotoTier> Tiers { get; } = [];
    public List<PhotoPaletteColor> Palette { get; } = [];

    // ── Волна 4.1: сложные реальные снимки и перекрёстная сверка с моделью со зрением ──

    /// <summary>Доверие к силуэту 0…1 (маска на весь кадр, касание краёв — низкое).</summary>
    public double SegmentationQuality { get; set; } = 1;

    /// <summary>Доля столбцов здания, перекрытых растительностью у основания (грани достроены до тротуара).</summary>
    public double OccludedShare { get; set; }

    /// <summary>Прямая основания (тротуара) — доли высоты кадра у левого и правого края; null — не найдена.</summary>
    public double? BaseLineY0 { get; set; }
    public double? BaseLineY1 { get; set; }

    /// <summary>Точка схода вертикалей (доли кадра; y &lt; 0 — над кадром, съёмка снизу); null — вертикали параллельны.</summary>
    public double? VanishingY { get; set; }
    public double? VanishingX { get; set; }

    /// <summary>Этажность без поправки на перспективу (для сравнения) и сила периодичности межэтажных линий 0…1.</summary>
    public int FloorsUncorrected { get; set; }
    public double FloorConfidence { get; set; }

    /// <summary>Клетки отфильтрованной растительности (доли кадра) — оверлей студии.</summary>
    public List<(double X0, double Y0, double X1, double Y1)> ClutterCells { get; } = [];

    /// <summary>Число дуг вдоль главного фасада: 1 — C-дуга, 2 — S-образный, 3 и более — волна (знак первой — по ArcBulgeRatio).</summary>
    public int FacadeArcs { get; set; } = 1;

    /// <summary>Кольцевое здание с внутренним двором-атриумом (под куполом).</summary>
    public bool HasCourtyard { get; set; }

    /// <summary>Консольные сдвиги объёмов (каскад).</summary>
    public int Cantilevers { get; set; }

    /// <summary>Наклонные пилоны / контрфорсы большепролётного зала, увиденные на снимке (0 — не видны или не известно).</summary>
    public int InclinedPylons { get; set; }

    /// <summary>Источник ключевых чисел после сверки с моделью: «Визуальное ядро» / «модель со зрением» (для отчёта).</summary>
    public List<string> ReconcileNotes { get; } = [];

    public string WallHex { get; set; } = "#D8D2C4";
    public string GlassHex { get; set; } = "#6F8FA8";
    public string RoofHex { get; set; } = "#7A7F86";
    public string AccentHex { get; set; } = "#B9B2A4";
    public string FrameHex { get; set; } = "#3A3F45";

    // ── Оверлеи для студии ──
    public double BoxX0 { get; set; }
    public double BoxX1 { get; set; } = 1;
    public double BoxTop { get; set; }
    public double BoxBase { get; set; } = 1;
    public List<(double X, double Y)> TopProfile { get; } = [];
    public List<(double X, double Y)> BottomProfile { get; } = [];
    public List<double> FloorLines { get; } = [];
    public List<((double X, double Y) From, (double X, double Y) To)> CurvatureVectors { get; } = [];
    public List<((double X, double Y) From, (double X, double Y) To)> VanishingLines { get; } = [];
    public double AnalysisMs { get; set; }

    public PhotoArchitectureDescriptor Clone()
    {
        var copy = new PhotoArchitectureDescriptor
        {
            SourceName = SourceName, PixelWidth = PixelWidth, PixelHeight = PixelHeight, Morphology = Morphology, Confidence = Confidence,
            Floors = Floors, FloorHeight = FloorHeight, HeightM = HeightM, WidthM = WidthM, DepthM = DepthM, ArcBulgeRatio = ArcBulgeRatio,
            CornerRadiusRatio = CornerRadiusRatio, CornerAngleDeg = CornerAngleDeg, PodiumFloors = PodiumFloors, PodiumWidthRatio = PodiumWidthRatio,
            GlazingRatio = GlazingRatio, WindowColumns = WindowColumns, WindowPeriodPx = WindowPeriodPx, FloorPeriodPx = FloorPeriodPx, Roof = Roof,
            DomeWidthRatio = DomeWidthRatio, DomeHeightRatio = DomeHeightRatio, ColonnadeColumns = ColonnadeColumns, WallHex = WallHex,
            GlassHex = GlassHex, RoofHex = RoofHex, AccentHex = AccentHex, FrameHex = FrameHex, BoxX0 = BoxX0, BoxX1 = BoxX1, BoxTop = BoxTop,
            BoxBase = BoxBase, AnalysisMs = AnalysisMs, SegmentationQuality = SegmentationQuality, OccludedShare = OccludedShare, BaseLineY0 = BaseLineY0,
            BaseLineY1 = BaseLineY1, VanishingX = VanishingX, VanishingY = VanishingY, FloorsUncorrected = FloorsUncorrected, FloorConfidence = FloorConfidence,
            FacadeArcs = FacadeArcs, HasCourtyard = HasCourtyard, Cantilevers = Cantilevers, InclinedPylons = InclinedPylons
        };
        copy.ClutterCells.AddRange(ClutterCells);
        copy.ReconcileNotes.AddRange(ReconcileNotes);
        copy.Evidence.AddRange(Evidence);
        copy.Tiers.AddRange(Tiers);
        copy.Palette.AddRange(Palette);
        copy.TopProfile.AddRange(TopProfile);
        copy.BottomProfile.AddRange(BottomProfile);
        copy.FloorLines.AddRange(FloorLines);
        copy.CurvatureVectors.AddRange(CurvatureVectors);
        copy.VanishingLines.AddRange(VanishingLines);
        return copy;
    }

    public static string MorphologyCode(PhotoMorphology m) => m switch
    {
        PhotoMorphology.Curved => "curved",
        PhotoMorphology.Rounded => "rounded",
        PhotoMorphology.Cascading => "cascading",
        PhotoMorphology.Triangular => "triangular",
        PhotoMorphology.Rotunda => "rotunda",
        PhotoMorphology.ArchSpan => "arch_span",
        PhotoMorphology.Pitched => "pitched",
        PhotoMorphology.Box => "box",
        _ => "auto"
    };

    public static string RoofCode(PhotoRoofKind r) => r switch
    {
        PhotoRoofKind.Parapet => "flat_parapet",
        PhotoRoofKind.Dome => "dome",
        PhotoRoofKind.Vault => "barrel_vault",
        PhotoRoofKind.Gable => "gable",
        PhotoRoofKind.Cone => "cone",
        PhotoRoofKind.Mansard => "mansard",
        _ => "flat"
    };

    /// <summary>
    /// Матрица компьютерного зрения для модели проектирования (текстовой и мультимодальной): все числа паспорта — точно, с единицами,
    /// в метрах и пикселях; правила совпадения формы с картинкой.
    /// </summary>
    public string ToPromptMatrix()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("PhotoArchitectureDescriptor (оптический паспорт снимка, Визуальное ядро):");
        sb.AppendLine(string.Create(ci, $"- source: {SourceName} · {PixelWidth}×{PixelHeight} px · analysis {AnalysisMs:0} ms"));
        sb.AppendLine(string.Create(ci, $"- morphology: {MorphologyCode(Morphology)} (confidence {Confidence:0.00}); evidence: {string.Join("; ", Evidence)}"));
        sb.AppendLine(string.Create(ci, $"- floors: {Floors}, floorHeight: {FloorHeight:0.00} m, totalHeight: {HeightM:0.0} m, footprint ≈ {WidthM:0.0} × {DepthM:0.0} m"));
        string low = SegmentationQuality < 0.55 ? " (LOW: building touches frame edges / neighbours — trust what you see on the image for shape and floors)" : string.Empty;
        string vp = VanishingY is { } vy ? string.Create(ci, $"; vertical vanishing point y = {vy:0.00} of image height") : "; verticals parallel";
        sb.AppendLine(string.Create(ci, $"- silhouette reliability: {SegmentationQuality:0.00}{low}; floor rhythm strength {FloorConfidence:0.00}; floors without perspective correction {FloorsUncorrected}{vp}"));
        if (OccludedShare > 0.02) sb.AppendLine(string.Create(ci, $"- foreground occluders (trees, cars) cover {OccludedShare:P0} of the facade base — facade edges were extended to the sidewalk"));
        if (FacadeArcs > 1) sb.AppendLine($"- main facade consists of {FacadeArcs} alternating arcs (S / wave plan)");
        if (HasCourtyard) sb.AppendLine("- ring building with an inner courtyard / atrium");
        if (InclinedPylons > 0) sb.AppendLine($"- inclined pylons / buttresses along the vault: {InclinedPylons}");
        if (ReconcileNotes.Count > 0) sb.AppendLine("- reconciled with vision model: " + string.Join("; ", ReconcileNotes));
        sb.AppendLine(string.Create(ci, $"- silhouette box (fractions of image): x {BoxX0:0.000}…{BoxX1:0.000}, top {BoxTop:0.000}, base {BoxBase:0.000}"));
        sb.Append("- silhouette top polyline (x,y fractions): ");
        sb.AppendLine(string.Join(" ", TopProfile.Where((_, i) => i % Math.Max(1, TopProfile.Count / 24) == 0).Select(p => string.Create(ci, $"{p.X:0.000},{p.Y:0.000}"))));
        if (Tiers.Count > 0)
        {
            sb.AppendLine($"- tiers (setbacks), {Tiers.Count}:");
            foreach (var t in Tiers) sb.AppendLine(string.Create(ci, $"  · x {t.X0:0.000}…{t.X1:0.000}, top {t.TopFraction:0.000} of height, floors {t.Floors}"));
        }
        sb.AppendLine(string.Create(ci, $"- arcBulge (sagitta / chord of curved facade): {ArcBulgeRatio:0.000} → arcBulge ≈ {ArcBulgeRatio * WidthM:0.00} m on chord {WidthM:0.0} m"));
        sb.AppendLine(string.Create(ci, $"- cornerRadius / short side: {CornerRadiusRatio:0.000} → cornerRadius ≈ {CornerRadiusRatio * Math.Min(WidthM, Math.Max(1, DepthM)):0.00} m"));
        if (CornerAngleDeg > 0) sb.AppendLine(string.Create(ci, $"- plan angle at the near corner (vanishing points): {CornerAngleDeg:0.0}°"));
        if (PodiumFloors > 0) sb.AppendLine(string.Create(ci, $"- podium (stylobate): {PodiumFloors} floors, wider than tower ×{1 / Math.Max(0.2, PodiumWidthRatio):0.00}"));
        sb.AppendLine(string.Create(ci, $"- glazing ratio: {GlazingRatio:0.00}; window columns: {WindowColumns}; window period {WindowPeriodPx:0.0} px; floor period {FloorPeriodPx:0.0} px"));
        sb.AppendLine($"- roof: {RoofCode(Roof)}" + (Roof == PhotoRoofKind.Dome ? string.Create(ci, $", dome width {DomeWidthRatio:0.00} of facade, dome height {DomeHeightRatio:0.00} of total") : string.Empty));
        if (ColonnadeColumns > 0) sb.AppendLine($"- colonnade / portico columns: {ColonnadeColumns}");
        sb.AppendLine($"- facade palette HEX: wall {WallHex}, glass {GlassHex}, roof {RoofHex}, accent {AccentHex}, frame {FrameHex}; all: " +
                      string.Join(", ", Palette.Select(p => string.Create(ci, $"{p.Hex} {p.Share:0.00} {p.Role}"))));
        return sb.ToString();
    }
}
