using System.Globalization;
using System.Text;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Точка чертежа DXF в метрах: X — вправо, Y — вверх листа (север плана), Z — отметка. Сцена: (X, Z, −Y).</summary>
public readonly record struct DxfPoint(double X, double Y, double Z = 0)
{
    public static DxfPoint operator +(DxfPoint a, DxfPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static DxfPoint operator -(DxfPoint a, DxfPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static DxfPoint operator *(DxfPoint a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public double Length2D => Math.Sqrt(X * X + Y * Y);
    public double Distance2D(DxfPoint o) => (this - o).Length2D;
}

public enum DxfEntityKind
{
    Line,
    Polyline,
    Circle,
    Arc,
    Text,
    Face
}

/// <summary>Примитив чертежа (уже в метрах и со сдвигом центрирования).</summary>
public sealed class DxfEntity
{
    public DxfEntityKind Kind { get; init; }
    public string Layer { get; init; } = "0";
    public List<DxfPoint> Points { get; init; } = [];
    public bool Closed { get; init; }
    public double Radius { get; init; }
    public double StartAngle { get; init; }
    public double EndAngle { get; init; }
    public string Text { get; init; } = string.Empty;
    public double Height { get; init; }
}

/// <summary>
/// Контур подложки, доступный для клика: цепочка отрезков, полилиния, окружность или дуга (координаты чертежа, метры).
/// </summary>
public sealed class DxfContour
{
    public int Id { get; init; }
    public string Layer { get; init; } = "0";
    public DxfLayerCategory Category { get; init; }
    public List<DxfPoint> Points { get; init; } = [];
    public bool Closed { get; init; }
    public bool IsCircle { get; init; }
    public double Radius { get; init; }

    public double MinZ => Points.Count == 0 ? 0 : Points.Min(p => p.Z);
    public double MaxZ => Points.Count == 0 ? 0 : Points.Max(p => p.Z);

    public DxfPoint Center
    {
        get
        {
            if (IsCircle || Points.Count == 0) return Points.Count > 0 ? Points[0] : default;
            var pts = Closed && Points.Count > 1 && Points[0].Distance2D(Points[^1]) < 1e-6 ? Points.Take(Points.Count - 1) : Points;
            double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
            return new DxfPoint((x0 + x1) / 2, (y0 + y1) / 2, MinZ);
        }
    }

    /// <summary>Отрезки контура в плане (окружность — 48-угольник).</summary>
    public IEnumerable<(DxfPoint A, DxfPoint B)> Segments()
    {
        if (IsCircle)
        {
            const int n = 48;
            var c = Points[0];
            for (int i = 0; i < n; i++)
            {
                double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n;
                yield return (new DxfPoint(c.X + Radius * Math.Cos(a0), c.Y + Radius * Math.Sin(a0), c.Z),
                              new DxfPoint(c.X + Radius * Math.Cos(a1), c.Y + Radius * Math.Sin(a1), c.Z));
            }
            yield break;
        }
        for (int i = 0; i + 1 < Points.Count; i++) yield return (Points[i], Points[i + 1]);
        if (Closed && Points.Count > 2 && Points[0].Distance2D(Points[^1]) > 1e-9) yield return (Points[^1], Points[0]);
    }

    /// <summary>Расстояние от точки плана до ближайшего отрезка контура и сам отрезок.</summary>
    public (double Distance, DxfPoint A, DxfPoint B) Nearest(DxfPoint p)
    {
        var best = (double.MaxValue, default(DxfPoint), default(DxfPoint));
        foreach (var (a, b) in Segments())
        {
            double d = CadDxfImporterService.DistanceToSegment(p, a, b);
            if (d < best.Item1) best = (d, a, b);
        }
        return best;
    }

    /// <summary>Вершины замкнутого контура без повтора первой точки.</summary>
    public List<DxfPoint> Ring()
    {
        if (IsCircle) return Segments().Select(s => s.A).ToList();
        var list = Points.ToList();
        if (list.Count > 1 && list[0].Distance2D(list[^1]) < 1e-6) list.RemoveAt(list.Count - 1);
        return list;
    }
}

/// <summary>Назначение слоя по имени (стандартные слои AutoCAD RU/EN и слои экспорта AI Architect Studio).</summary>
public enum DxfLayerCategory
{
    Unknown,
    Wall,
    CurtainWall,
    Column,
    Beam,
    Slab,
    Foundation,
    Roof,
    Window,
    Door,
    Opening,
    Axis,
    Stairs,
    Pool,
    Terrace,
    Asset
}

/// <summary>Загруженный чертёж DXF: примитивы в метрах, слои, масштаб единиц, сдвиг центрирования и контуры для клика.</summary>
public sealed class DxfDrawing
{
    public required string FileName { get; init; }
    public required List<DxfEntity> Entities { get; init; }
    public required Dictionary<string, int> Layers { get; init; }
    public required List<DxfContour> Contours { get; init; }

    /// <summary>Множитель «единица чертежа → метр» (0.001 — миллиметры AutoCAD).</summary>
    public double UnitScale { get; init; } = 1;

    public string UnitsTitle { get; init; } = "м";

    /// <summary>Как определены единицы: $INSUNITS или по габаритам.</summary>
    public string UnitsSource { get; init; } = string.Empty;

    /// <summary>Сдвиг (м), прибавленный к координатам при центрировании чертежа относительно (0, 0).</summary>
    public DxfPoint Offset { get; init; }

    /// <summary>Файл экспорта AI Architect Studio (координаты не центрируются — точный Round-Trip).</summary>
    public bool IsOwnExport { get; init; }

    public double MinX { get; init; }
    public double MaxX { get; init; }
    public double MinY { get; init; }
    public double MaxY { get; init; }

    public int FaceCount => Entities.Count(e => e.Kind == DxfEntityKind.Face);
}

/// <summary>
/// Параметры автоподъёма 2D-чертежа в 3D (для плоских чертежей без отметок) — подтверждаются пользователем в окне верификации
/// DXF: высота этажа и стен, толщины плит, фундамента и покрытия, сечения колонн и балок (0 — по контуру чертежа), окна и двери.
/// У трёхмерного чертежа (3DFACE) высоты берутся из граней, а эти значения — только запасные.
/// </summary>
public sealed record DxfLiftOptions(string GroupId, double BaseElevation = 0, double LevelHeight = 3.0, int LevelIndex = 0)
{
    /// <summary>Высота стен, м (0 — равна высоте этажа).</summary>
    public double WallHeight { get; init; }
    public double SlabThickness { get; init; } = 0.2;
    public double FoundationThickness { get; init; } = 0.5;
    public double RoofThickness { get; init; } = 0.25;

    /// <summary>Сечение колонн W × D, м (0 — по контуру чертежа).</summary>
    public double ColumnWidth { get; init; }
    public double ColumnDepth { get; init; }

    /// <summary>Балки: высота сечения H и ширина B, м (ширина 0 — по контуру чертежа).</summary>
    public double BeamHeight { get; init; } = 0.5;
    public double BeamWidth { get; init; }

    public double WindowHeight { get; init; } = 1.5;
    public double WindowSill { get; init; } = 0.9;
    public double DoorHeight { get; init; } = 2.1;

    /// <summary>Фактическая высота стен.</summary>
    public double EffectiveWallHeight => WallHeight > 0 ? WallHeight : LevelHeight;
}

/// <summary>
/// Что распознаётся в чертеже до подъёма в 3D (для окна верификации): число контуров по категориям, типичные сечения колонн
/// и толщины стен по чертежу, наличие 3DFACE (трёхмерный чертёж — высоты по граням).
/// </summary>
public sealed record DxfLiftPreview(string FileName, bool Is3D, int Walls, int PairedWalls, double? WallThickness, int Columns, (double W, double D, bool Circle)? ColumnSection,
    int Beams, double? BeamWidth, int Slabs, int Foundations, int Roofs, int Windows, int Doors, int Openings, int Axes, double WidthM, double DepthM);

/// <summary>Итог автоподъёма: элементы, координационные оси и отчёт.</summary>
public sealed record DxfLiftResult(List<ArchitecturalElement> Elements, List<StructuralGridAxis> Axes, List<string> Report);

/// <summary>
/// Прямой импорт AutoCAD DXF без участия ИИ: точный C#-парсер секций HEADER ($INSUNITS), TABLES (LAYER), BLOCKS (вставки
/// INSERT разворачиваются) и ENTITIES (LINE, LWPOLYLINE, POLYLINE/VERTEX, CIRCLE, ARC, TEXT, MTEXT, 3DFACE), перевод единиц
/// в метры (миллиметры AutoCAD → ×0.001) с точностью 0.1 мм и центрирование чертежа. Подложка показывается во вьюпорте,
/// а автоподъём распознаёт стены, колонны, балки, плиты, окна, двери, проёмы и оси по слоям и геометрии.
/// </summary>
public sealed class CadDxfImporterService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Подпись файлов собственного экспорта (комментарий 999 в начале DXF).</summary>
    public const string ExportSignature = "AI Architect Studio CadExportService";

    /// <summary>Точность координат импорта: 0.1 мм.</summary>
    public const double Precision = 1e-4;

    public Task<DxfDrawing> LoadAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Parse(DecodeText(File.ReadAllBytes(path)), Path.GetFileName(path)), ct);

    /// <summary>Текст DXF: UTF-8 (AutoCAD 2007+), иначе кодовая страница Windows-1251 (ANSI_1251 старых версий).</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length > 22 && Encoding.ASCII.GetString(bytes, 0, 22).StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
            throw new InvalidDataException("Двоичный DXF не поддерживается: сохраните чертёж как ASCII DXF.");
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    // ═════════════════════════════ Парсер ═════════════════════════════

    private sealed class RawEntity
    {
        public string Type = string.Empty;
        public readonly List<(int Code, string Value)> Codes = [];

        public string Str(int code, string fallback = "")
        {
            foreach (var (c, v) in Codes)
                if (c == code) return v;
            return fallback;
        }

        public double Num(int code, double fallback = 0)
        {
            foreach (var (c, v) in Codes)
                if (c == code && double.TryParse(v.Trim(), NumberStyles.Float, Inv, out var d)) return d;
            return fallback;
        }

        public int Int(int code, int fallback = 0) => (int)Math.Round(Num(code, fallback));

        public bool Has(int code) => Codes.Any(c => c.Code == code);
    }

    private sealed class Block
    {
        public string Name = string.Empty;
        public DxfPoint Base;
        public readonly List<RawEntity> Entities = [];
    }

    /// <summary>Разбор текста DXF в чертёж (координаты — в метрах, центрированы для чужих чертежей).</summary>
    public static DxfDrawing Parse(string text, string fileName)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var pairs = new List<(int Code, string Value)>(lines.Length / 2);
        bool own = false;
        for (int i = 0; i + 1 < lines.Length; i += 2)
        {
            if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, Inv, out int code))
            {
                // Сбой выравнивания пар (пустые строки в конце, лишние переводы) — пробуем сдвинуться на строку.
                i--;
                continue;
            }
            string value = lines[i + 1].TrimEnd();
            if (code == 999 && value.Contains(ExportSignature, StringComparison.Ordinal)) own = true;
            pairs.Add((code, value));
        }

        int insUnits = 0;
        var layers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var blocks = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        var entities = new List<RawEntity>();

        string section = string.Empty;
        Block? currentBlock = null;
        RawEntity? current = null;
        string? headerVar = null;

        void Flush()
        {
            if (current is null) return;
            if (section == "ENTITIES") entities.Add(current);
            else if (section == "BLOCKS" && currentBlock is not null && current.Type is not ("BLOCK" or "ENDBLK")) currentBlock.Entities.Add(current);
            else if (section == "TABLES" && current.Type == "LAYER")
            {
                string name = current.Str(2);
                if (name.Length > 0) layers[name] = Math.Abs(current.Int(62, 7));
            }
            current = null;
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            var (code, value) = pairs[i];
            if (code == 0)
            {
                Flush();
                string type = value.Trim().ToUpperInvariant();
                switch (type)
                {
                    case "SECTION":
                        section = i + 1 < pairs.Count && pairs[i + 1].Code == 2 ? pairs[i + 1].Value.Trim().ToUpperInvariant() : string.Empty;
                        i++;
                        continue;
                    case "ENDSEC":
                        section = string.Empty;
                        continue;
                    case "EOF":
                        i = pairs.Count;
                        continue;
                }
                if (section == "BLOCKS" && type == "BLOCK")
                {
                    currentBlock = new Block();
                    current = new RawEntity { Type = type };
                    // Имя и базовая точка блока читаются из его кодов.
                    int j = i + 1;
                    for (; j < pairs.Count && pairs[j].Code != 0; j++)
                    {
                        var (c, v) = pairs[j];
                        if (c == 2) currentBlock.Name = v.Trim();
                        else if (c == 10) currentBlock.Base = currentBlock.Base with { X = ParseD(v) };
                        else if (c == 20) currentBlock.Base = currentBlock.Base with { Y = ParseD(v) };
                        else if (c == 30) currentBlock.Base = currentBlock.Base with { Z = ParseD(v) };
                    }
                    if (currentBlock.Name.Length > 0) blocks[currentBlock.Name] = currentBlock;
                    current = null;
                    i = j - 1;
                    continue;
                }
                if (section == "BLOCKS" && type == "ENDBLK")
                {
                    currentBlock = null;
                    continue;
                }
                current = new RawEntity { Type = type };
                continue;
            }

            if (section == "HEADER")
            {
                if (code == 9) headerVar = value.Trim().ToUpperInvariant();
                else if (headerVar == "$INSUNITS" && code == 70) insUnits = (int)ParseD(value);
                continue;
            }
            current?.Codes.Add((code, value));
        }
        Flush();

        // Примитивы в единицах чертежа (вставки блоков развёрнуты).
        var raw = new List<DxfEntity>();
        BuildEntities(entities, raw, blocks, Transform2.Identity, null, 0);

        // Единицы: $INSUNITS, иначе — по габаритам (чертёж в тысячах — миллиметры AutoCAD).
        var (scale, unitsTitle, unitsSource) = ResolveUnits(insUnits, raw);

        var extentPoints = raw.Where(e => e.Kind != DxfEntityKind.Text).SelectMany(e => e.Points).ToList();
        double minX = 0, maxX = 0, minY = 0, maxY = 0;
        if (extentPoints.Count > 0)
        {
            minX = extentPoints.Min(p => p.X) * scale; maxX = extentPoints.Max(p => p.X) * scale;
            minY = extentPoints.Min(p => p.Y) * scale; maxY = extentPoints.Max(p => p.Y) * scale;
        }
        // Центрирование относительно (0, 0) с шагом 1 мм; свой экспорт уже в координатах сцены.
        var offset = own ? default : new DxfPoint(-Math.Round((minX + maxX) / 2, 3), -Math.Round((minY + maxY) / 2, 3));

        DxfPoint Map(DxfPoint p) => new(Round(p.X * scale + offset.X), Round(p.Y * scale + offset.Y), Round(p.Z * scale));
        var mapped = raw.Select(e => new DxfEntity
        {
            Kind = e.Kind,
            Layer = e.Layer,
            Points = e.Points.Select(Map).ToList(),
            Closed = e.Closed,
            Radius = Round(e.Radius * scale),
            StartAngle = e.StartAngle,
            EndAngle = e.EndAngle,
            Text = e.Text,
            Height = e.Height * scale
        }).ToList();

        foreach (var layer in mapped.Select(e => e.Layer).Distinct())
            if (!layers.ContainsKey(layer)) layers[layer] = 7;

        return new DxfDrawing
        {
            FileName = fileName,
            Entities = mapped,
            Layers = layers,
            Contours = BuildContours(mapped),
            UnitScale = scale,
            UnitsTitle = unitsTitle,
            UnitsSource = unitsSource,
            Offset = offset,
            IsOwnExport = own,
            MinX = minX + offset.X,
            MaxX = maxX + offset.X,
            MinY = minY + offset.Y,
            MaxY = maxY + offset.Y
        };
    }

    private static double Round(double v) => Math.Round(v, 7);

    private static double ParseD(string s) => double.TryParse(s.Trim(), NumberStyles.Float, Inv, out var d) ? d : 0;

    private static (double Scale, string Title, string Source) ResolveUnits(int insUnits, List<DxfEntity> raw)
    {
        switch (insUnits)
        {
            case 1: return (0.0254, "дюймы", "$INSUNITS = 1");
            case 2: return (0.3048, "футы", "$INSUNITS = 2");
            case 4: return (0.001, "мм", "$INSUNITS = 4");
            case 5: return (0.01, "см", "$INSUNITS = 5");
            case 6: return (1, "м", "$INSUNITS = 6");
            case 14: return (0.1, "дм", "$INSUNITS = 14");
        }
        var pts = raw.Where(e => e.Kind != DxfEntityKind.Text).SelectMany(e => e.Points).ToList();
        if (pts.Count == 0) return (1, "м", "по умолчанию");
        double extent = Math.Max(pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y));
        return extent >= 1000
            ? (0.001, "мм", $"по габаритам ({extent:0} ед. — миллиметры)")
            : (1, "м", $"по габаритам ({extent:0.###} ед. — метры)");
    }

    /// <summary>Аффинное преобразование плана для вставок блоков: p' = R·S·p + T (Z — масштаб по Z и сдвиг).</summary>
    private readonly record struct Transform2(double A, double B, double C, double D, double Tx, double Ty, double Sz, double Tz)
    {
        public static Transform2 Identity => new(1, 0, 0, 1, 0, 0, 1, 0);

        public DxfPoint Apply(DxfPoint p) => new(A * p.X + B * p.Y + Tx, C * p.X + D * p.Y + Ty, Sz * p.Z + Tz);

        public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));

        public double Rotation => Math.Atan2(C, A) * 180 / Math.PI;

        public bool Mirrored => A * D - B * C < 0;

        public Transform2 Then(Transform2 o) => new(
            o.A * A + o.B * C, o.A * B + o.B * D,
            o.C * A + o.D * C, o.C * B + o.D * D,
            o.A * Tx + o.B * Ty + o.Tx, o.C * Tx + o.D * Ty + o.Ty,
            o.Sz * Sz, o.Sz * Tz + o.Tz);
    }

    private static void BuildEntities(List<RawEntity> source, List<DxfEntity> output, Dictionary<string, Block> blocks, Transform2 t, string? insertLayer, int depth)
    {
        for (int i = 0; i < source.Count; i++)
        {
            var e = source[i];
            string layer = e.Str(8, "0").Trim();
            // Примитивы слоя «0» внутри блока наследуют слой вставки.
            if (insertLayer is not null && layer == "0") layer = insertLayer;
            bool flip = e.Num(230, 1) < 0;
            DxfPoint P(int code) => t.Apply(new DxfPoint(flip ? -e.Num(code) : e.Num(code), e.Num(code + 10), e.Num(code + 20)));

            switch (e.Type)
            {
                case "LINE":
                    output.Add(new DxfEntity { Kind = DxfEntityKind.Line, Layer = layer, Points = [P(10), P(11)] });
                    break;

                case "3DFACE":
                {
                    var pts = new List<DxfPoint> { P(10), P(11), P(12), P(13) };
                    output.Add(new DxfEntity { Kind = DxfEntityKind.Face, Layer = layer, Points = pts });
                    break;
                }

                case "CIRCLE":
                    output.Add(new DxfEntity { Kind = DxfEntityKind.Circle, Layer = layer, Points = [P(10)], Radius = e.Num(40) * t.Scale });
                    break;

                case "ARC":
                {
                    double rot = t.Rotation;
                    double a0 = e.Num(50), a1 = e.Num(51);
                    if (flip) (a0, a1) = (180 - a1, 180 - a0);
                    if (t.Mirrored) (a0, a1) = (-a1, -a0);
                    output.Add(new DxfEntity
                    {
                        Kind = DxfEntityKind.Arc, Layer = layer, Points = [P(10)], Radius = e.Num(40) * t.Scale,
                        StartAngle = a0 + rot, EndAngle = a1 + rot
                    });
                    break;
                }

                case "LWPOLYLINE":
                {
                    var verts = new List<(double X, double Y, double Bulge)>();
                    double elevation = e.Num(38);
                    foreach (var (c, v) in e.Codes)
                    {
                        if (c == 10) verts.Add((ParseD(v), 0, 0));
                        else if (c == 20 && verts.Count > 0) verts[^1] = verts[^1] with { Y = ParseD(v) };
                        else if (c == 42 && verts.Count > 0) verts[^1] = verts[^1] with { Bulge = ParseD(v) };
                    }
                    bool closed = (e.Int(70) & 1) != 0;
                    var pts = FlattenBulges(verts.Select(v => (new DxfPoint(flip ? -v.X : v.X, v.Y, elevation), flip ? -v.Bulge : v.Bulge)).ToList(), closed);
                    if (pts.Count >= 2)
                        output.Add(new DxfEntity { Kind = DxfEntityKind.Polyline, Layer = layer, Points = pts.Select(t.Apply).ToList(), Closed = closed });
                    break;
                }

                case "POLYLINE":
                {
                    int flags = e.Int(70);
                    bool closed = (flags & 1) != 0;
                    bool mesh = (flags & (16 | 64)) != 0;
                    var verts = new List<(DxfPoint P, double Bulge)>();
                    int j = i + 1;
                    for (; j < source.Count && source[j].Type == "VERTEX"; j++)
                    {
                        var v = source[j];
                        if ((v.Int(70) & 128) != 0 && v.Num(10) == 0 && v.Num(20) == 0) continue; // запись грани многогранной сетки
                        verts.Add((new DxfPoint(flip ? -v.Num(10) : v.Num(10), v.Num(20), v.Num(30)), flip ? -v.Num(42) : v.Num(42)));
                    }
                    if (j < source.Count && source[j].Type == "SEQEND") j++;
                    i = j - 1;
                    if (mesh || verts.Count < 2) break;
                    var pts = FlattenBulges(verts, closed);
                    output.Add(new DxfEntity { Kind = DxfEntityKind.Polyline, Layer = layer, Points = pts.Select(t.Apply).ToList(), Closed = closed });
                    break;
                }

                case "TEXT":
                case "MTEXT":
                {
                    bool aligned = e.Type == "TEXT" && (e.Int(72) != 0 || e.Int(73) != 0) && e.Has(11);
                    string content = e.Type == "MTEXT"
                        ? CleanMText(string.Concat(e.Codes.Where(c => c.Code == 3).Select(c => c.Value)) + e.Str(1))
                        : DecodeEscapes(e.Str(1));
                    if (content.Trim().Length == 0) break;
                    output.Add(new DxfEntity
                    {
                        Kind = DxfEntityKind.Text, Layer = layer, Points = [aligned ? P(11) : P(10)], Text = content.Trim(), Height = e.Num(40) * t.Scale
                    });
                    break;
                }

                case "INSERT":
                {
                    if (depth > 8 || !blocks.TryGetValue(e.Str(2).Trim(), out var block)) break;
                    double sx = e.Num(41, 1), sy = e.Num(42, 1), sz = e.Num(43, 1);
                    double rot = e.Num(50) * Math.PI / 180;
                    double cos = Math.Cos(rot), sin = Math.Sin(rot);
                    var ins = new DxfPoint(e.Num(10), e.Num(20), e.Num(30));
                    // Локально: сдвиг на −база, масштаб, поворот, перенос в точку вставки; затем — родительское преобразование.
                    var local = new Transform2(cos * sx, -sin * sy, sin * sx, cos * sy,
                        ins.X - (cos * sx * block.Base.X - sin * sy * block.Base.Y),
                        ins.Y - (sin * sx * block.Base.X + cos * sy * block.Base.Y),
                        sz, ins.Z - sz * block.Base.Z);
                    if (flip) local = local with { A = -local.A, B = -local.B, Tx = -local.Tx };
                    BuildEntities(block.Entities, output, blocks, local.Then(t), layer, depth + 1);
                    break;
                }
            }
        }
    }

    /// <summary>Дуговые сегменты полилиний (bulge) — ломаной с шагом не более 10°.</summary>
    private static List<DxfPoint> FlattenBulges(List<(DxfPoint P, double Bulge)> verts, bool closed)
    {
        var result = new List<DxfPoint>();
        int n = verts.Count;
        for (int i = 0; i < n; i++)
        {
            var (p, bulge) = verts[i];
            result.Add(p);
            if (!closed && i == n - 1) break;
            var q = verts[(i + 1) % n].P;
            if (Math.Abs(bulge) < 1e-9) continue;
            double theta = 4 * Math.Atan(bulge);
            double chord = p.Distance2D(q);
            if (chord < 1e-12) continue;
            double r = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
            var mid = new DxfPoint((p.X + q.X) / 2, (p.Y + q.Y) / 2, p.Z);
            double h = Math.Sqrt(Math.Max(0, r * r - chord * chord / 4));
            var dir = new DxfPoint((q.X - p.X) / chord, (q.Y - p.Y) / chord);
            // Центр слева от хорды при положительном bulge и дуге меньше полуокружности.
            double side = (bulge > 0 ? 1 : -1) * (Math.Abs(theta) > Math.PI ? -1 : 1);
            var center = new DxfPoint(mid.X - dir.Y * h * side, mid.Y + dir.X * h * side, p.Z);
            double a0 = Math.Atan2(p.Y - center.Y, p.X - center.X);
            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(theta) / (Math.PI / 18)));
            for (int k = 1; k < steps; k++)
            {
                double a = a0 + theta * k / steps;
                result.Add(new DxfPoint(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a), p.Z));
            }
        }
        return result;
    }

    /// <summary>Декодирование \U+XXXX (кириллица DXF R12) и %%-кодов AutoCAD.</summary>
    public static string DecodeEscapes(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 7 <= s.Length && s[i + 1] is 'U' or 'u' && s[i + 2] == '+' &&
                int.TryParse(s.AsSpan(i + 3, 4), NumberStyles.HexNumber, Inv, out int code))
            {
                sb.Append((char)code);
                i += 6;
                continue;
            }
            if (s[i] == '%' && i + 2 < s.Length && s[i + 1] == '%')
            {
                char c = char.ToLowerInvariant(s[i + 2]);
                string? rep = c switch { 'c' => "⌀", 'd' => "°", 'p' => "±", _ => null };
                if (rep is not null)
                {
                    sb.Append(rep);
                    i += 2;
                    continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Текст MTEXT без кодов форматирования ({\fArial|b0;...}, \P — перенос строки).</summary>
    public static string CleanMText(string s)
    {
        s = DecodeEscapes(s);
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '{' or '}') continue;
            if (c == '\\' && i + 1 < s.Length)
            {
                char k = s[i + 1];
                if (k is 'P' or 'p') { sb.Append('\n'); i++; continue; }
                if (k is '\\' or '{' or '}') { sb.Append(k); i++; continue; }
                if (k == '~') { sb.Append(' '); i++; continue; }
                // \f…; \H…; \C…; \W…; \Q…; \T…; \A…; \L \l \O \o \K \k
                if ("LlOoKk".Contains(k)) { i++; continue; }
                int end = s.IndexOf(';', i);
                if (end > 0) { i = end; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ═════════════════════════════ Контуры подложки ═════════════════════════════

    public static DxfLayerCategory Classify(string layer)
    {
        string n = layer.ToUpperInvariant();
        bool Has(params string[] keys) => keys.Any(k => n.Contains(k, StringComparison.Ordinal));
        if (Has("OPENING", "ПРОЕМ", "ПРОЁМ", "ОТВЕРСТ")) return DxfLayerCategory.Opening;
        if (Has("GRID", "AXIS", "AXES", "ОСИ", "ОСЬ", "ОСЕВ")) return DxfLayerCategory.Axis;
        if (Has("CURTAIN", "ВИТРАЖ")) return DxfLayerCategory.CurtainWall;
        if (Has("WINDOW", "ОКН", "ОКОН")) return DxfLayerCategory.Window;
        if (Has("DOOR", "ДВЕР")) return DxfLayerCategory.Door;
        if (Has("COLUMN", "КОЛОН") || n.StartsWith("S-COL", StringComparison.Ordinal)) return DxfLayerCategory.Column;
        if (Has("BEAM", "БАЛК", "РИГЕЛ", "FRAME")) return DxfLayerCategory.Beam;
        if (Has("FOUNDATION", "ФУНДАМ", "РОСТВЕРК")) return DxfLayerCategory.Foundation;
        if (Has("ROOF", "КРОВЛ", "ПОКРЫТ")) return DxfLayerCategory.Roof;
        if (Has("STAIR", "ЛЕСТН")) return DxfLayerCategory.Stairs;
        if (Has("POOL", "БАССЕЙН")) return DxfLayerCategory.Pool;
        if (Has("TERRACE", "ТЕРРАС")) return DxfLayerCategory.Terrace;
        if (Has("SLAB", "ПЛИТ", "ПЕРЕКР", "FLOOR")) return DxfLayerCategory.Slab;
        if (Has("WALL", "СТЕН", "STENA")) return DxfLayerCategory.Wall;
        if (Has("ASSET")) return DxfLayerCategory.Asset;
        return DxfLayerCategory.Unknown;
    }

    /// <summary>
    /// Контуры для клика и автоподъёма: полилинии, окружности, дуги и цепочки отрезков (сначала — последовательные отрезки
    /// в порядке файла, как пишет AutoCAD при расчленении полилиний; затем — компоненты графа, где у всех вершин степень 2).
    /// </summary>
    private static List<DxfContour> BuildContours(List<DxfEntity> entities)
    {
        var result = new List<DxfContour>();
        int id = 0;
        DxfContour Make(string layer, List<DxfPoint> pts, bool closed, bool circle = false, double radius = 0) => new()
        {
            Id = id++, Layer = layer, Category = Classify(layer), Points = pts, Closed = closed, IsCircle = circle, Radius = radius
        };

        var loose = new List<DxfEntity>();
        foreach (var e in entities)
        {
            switch (e.Kind)
            {
                case DxfEntityKind.Polyline:
                {
                    bool closed = e.Closed || (e.Points.Count > 3 && e.Points[0].Distance2D(e.Points[^1]) < 1e-6 && Math.Abs(e.Points[0].Z - e.Points[^1].Z) < 1e-6);
                    result.Add(Make(e.Layer, e.Points.ToList(), closed));
                    break;
                }
                case DxfEntityKind.Circle:
                    result.Add(Make(e.Layer, [e.Points[0]], true, circle: true, radius: e.Radius));
                    break;
                case DxfEntityKind.Arc:
                {
                    var c = e.Points[0];
                    double a0 = e.StartAngle, a1 = e.EndAngle;
                    while (a1 <= a0) a1 += 360;
                    int steps = Math.Max(2, (int)Math.Ceiling((a1 - a0) / 7.5));
                    var pts = Enumerable.Range(0, steps + 1)
                        .Select(k => (a0 + (a1 - a0) * k / steps) * Math.PI / 180)
                        .Select(a => new DxfPoint(c.X + e.Radius * Math.Cos(a), c.Y + e.Radius * Math.Sin(a), c.Z)).ToList();
                    result.Add(Make(e.Layer, pts, false));
                    break;
                }
                case DxfEntityKind.Line:
                    loose.Add(e);
                    break;
            }
        }

        // 1) Последовательные цепочки отрезков одного слоя.
        var remaining = new List<DxfEntity>();
        var chain = new List<DxfEntity>();
        void FlushChain()
        {
            if (chain.Count >= 3 && Same(chain[^1].Points[1], chain[0].Points[0]))
                result.Add(Make(chain[0].Layer, chain.Select(l => l.Points[0]).ToList(), true));
            else remaining.AddRange(chain);
            chain.Clear();
        }
        foreach (var line in loose)
        {
            if (chain.Count > 0 && (line.Layer != chain[0].Layer || !Same(chain[^1].Points[1], line.Points[0]))) FlushChain();
            chain.Add(line);
            if (chain.Count >= 3 && Same(chain[^1].Points[1], chain[0].Points[0])) FlushChain();
        }
        FlushChain();

        // 2) Замкнутые циклы графа (все вершины степени 2) из оставшихся отрезков.
        var used = new HashSet<DxfEntity>();
        foreach (var layerGroup in remaining.GroupBy(l => l.Layer))
        {
            var byKey = new Dictionary<(long, long, long), List<DxfEntity>>();
            foreach (var l in layerGroup)
                foreach (var p in l.Points)
                {
                    var k = Key(p);
                    if (!byKey.TryGetValue(k, out var list)) byKey[k] = list = [];
                    list.Add(l);
                }
            foreach (var start in layerGroup)
            {
                if (used.Contains(start)) continue;
                // Обход компоненты.
                var comp = new List<DxfEntity>();
                var stack = new Stack<DxfEntity>();
                stack.Push(start);
                var seen = new HashSet<DxfEntity> { start };
                while (stack.Count > 0)
                {
                    var l = stack.Pop();
                    comp.Add(l);
                    foreach (var p in l.Points)
                        foreach (var nb in byKey[Key(p)])
                            if (seen.Add(nb)) stack.Push(nb);
                }
                bool cycle = comp.Count >= 3 && comp.SelectMany(l => l.Points).GroupBy(Key).All(g => g.Count() == 2);
                if (!cycle)
                {
                    foreach (var l in comp) used.Add(l);
                    foreach (var l in comp) result.Add(Make(l.Layer, l.Points.ToList(), false));
                    continue;
                }
                // Упорядочивание вершин цикла.
                var ordered = new List<DxfPoint> { comp[0].Points[0] };
                var cur = comp[0];
                var tip = comp[0].Points[1];
                var visited = new HashSet<DxfEntity> { cur };
                while (visited.Count < comp.Count)
                {
                    ordered.Add(tip);
                    var nextLine = byKey[Key(tip)].FirstOrDefault(l => !visited.Contains(l) && comp.Contains(l));
                    if (nextLine is null) break;
                    visited.Add(nextLine);
                    tip = Same(nextLine.Points[0], tip) ? nextLine.Points[1] : nextLine.Points[0];
                }
                foreach (var l in comp) used.Add(l);
                result.Add(Make(comp[0].Layer, ordered, true));
            }
        }
        return result;
    }

    private static (long, long, long) Key(DxfPoint p) => ((long)Math.Round(p.X / Precision), (long)Math.Round(p.Y / Precision), (long)Math.Round(p.Z / Precision));

    private static bool Same(DxfPoint a, DxfPoint b) => Math.Abs(a.X - b.X) <= Precision && Math.Abs(a.Y - b.Y) <= Precision && Math.Abs(a.Z - b.Z) <= Precision;

    public static double DistanceToSegment(DxfPoint p, DxfPoint a, DxfPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 < 1e-18 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double x = a.X + dx * t - p.X, y = a.Y + dy * t - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    // ═════════════════════════════ Автоподъём 2D → 3D ═════════════════════════════

    /// <summary>Грани 3DFACE слоя (из 3D-экспорта) для определения высот элементов по их плановым контурам.</summary>
    private sealed class FaceIndex
    {
        private readonly Dictionary<string, List<(double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ, DxfPoint[] P)>> _byLayer = [];

        public FaceIndex(IEnumerable<DxfEntity> faces)
        {
            foreach (var f in faces)
            {
                if (!_byLayer.TryGetValue(f.Layer, out var list)) _byLayer[f.Layer] = list = [];
                var p = f.Points.ToArray();
                list.Add((p.Min(x => x.X), p.Max(x => x.X), p.Min(x => x.Y), p.Max(x => x.Y), p.Min(x => x.Z), p.Max(x => x.Z), p));
            }
        }

        public bool Any => _byLayer.Count > 0;

        /// <summary>
        /// Высота тела над контуром: грани внутри габарита контура, начиная от его отметки, наращиваются по вертикальной
        /// связности до следующего контура того же слоя выше (окно — рама из нескольких брусков, стена над стеной не сливаются).
        /// </summary>
        public double? Height(string layer, double x0, double x1, double y0, double y1, double z, double limitZ)
        {
            if (!_byLayer.TryGetValue(layer, out var list)) return null;
            const double tol = 2e-4;
            var inside = list.Where(f => f.MinX >= x0 - tol && f.MaxX <= x1 + tol && f.MinY >= y0 - tol && f.MaxY <= y1 + tol &&
                                         f.MinZ >= z - tol && f.MinZ < limitZ - tol).ToList();
            double top = z;
            bool grew = true, any = false;
            while (grew)
            {
                grew = false;
                foreach (var f in inside)
                    if (f.MinZ <= top + tol && f.MaxZ > top + tol)
                    {
                        top = Math.Min(f.MaxZ, limitZ);
                        grew = any = true;
                    }
            }
            return any && top - z > 1e-4 ? top - z : null;
        }

        /// <summary>Наклонные грани над контуром (форма скатной кровли): число различных направлений скатов.</summary>
        public int SlopeDirections(string layer, double x0, double x1, double y0, double y1, double z)
        {
            if (!_byLayer.TryGetValue(layer, out var list)) return 0;
            var dirs = new HashSet<(int, int)>();
            foreach (var f in list)
            {
                if (f.MinX < x0 - 1e-3 || f.MaxX > x1 + 1e-3 || f.MinY < y0 - 1e-3 || f.MaxY > y1 + 1e-3 || f.MinZ < z - 1e-3) continue;
                var a = f.P[0];
                var b = f.P[1];
                var c = f.P[2];
                var u = b - a;
                var v = c - a;
                double nx = u.Y * v.Z - u.Z * v.Y, ny = u.Z * v.X - u.X * v.Z, nz = u.X * v.Y - u.Y * v.X;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-9) continue;
                nx /= len; ny /= len; nz /= len;
                if (Math.Abs(nz) < 0.05 || Math.Abs(nz) > 0.995) continue;
                if (nz < 0) { nx = -nx; ny = -ny; }
                dirs.Add(((int)Math.Round(Math.Atan2(ny, nx) * 180 / Math.PI / 5), 0));
            }
            return dirs.Count;
        }

        /// <summary>Грани слоя внутри габарита (для разбора сечения балки по продольным граням).</summary>
        public List<DxfPoint[]> FacesIn(string layer, double x0, double x1, double y0, double y1, double z0, double z1)
        {
            var result = new List<DxfPoint[]>();
            if (!_byLayer.TryGetValue(layer, out var list)) return result;
            const double tol = 1e-3;
            foreach (var f in list)
                if (f.MinX >= x0 - tol && f.MaxX <= x1 + tol && f.MinY >= y0 - tol && f.MaxY <= y1 + tol && f.MinZ >= z0 - tol && f.MaxZ <= z1 + tol)
                    result.Add(f.P);
            return result;
        }

        /// <summary>Минимальный радиус граней тела внутри окружности (внутренняя стенка трубы).</summary>
        public double? InnerRadius(string layer, DxfPoint center, double radius, double z)
        {
            if (!_byLayer.TryGetValue(layer, out var list)) return null;
            double min = double.MaxValue;
            foreach (var f in list)
            {
                if (f.MinZ < z - 1e-3 || Math.Abs(f.MaxZ - f.MinZ) < 1e-6) continue;
                double d = f.P.Max(p => Math.Sqrt((p.X - center.X) * (p.X - center.X) + (p.Y - center.Y) * (p.Y - center.Y)));
                if (d > radius * 1.02) continue;
                double dmin = f.P.Min(p => Math.Sqrt((p.X - center.X) * (p.X - center.X) + (p.Y - center.Y) * (p.Y - center.Y)));
                min = Math.Min(min, dmin);
            }
            return min < radius * 0.97 ? min : null;
        }
    }

    /// <summary>
    /// Режим А — автоподъём 3D из DXF в 1 клик: элементы по слоям (включая слои собственного экспорта) и геометрической
    /// эвристике; высоты — по граням 3DFACE (если чертёж трёхмерный) или по параметрам этажа.
    /// </summary>
    public static DxfLiftResult LiftTo3D(DxfDrawing drawing, DxfLiftOptions options)
    {
        var report = new List<string>();
        var elements = new List<ArchitecturalElement>();
        var faces = new FaceIndex(drawing.Entities.Where(e => e.Kind == DxfEntityKind.Face));
        bool is3D = faces.Any;
        string gid = options.GroupId;
        int counter = 0;

        ArchitecturalElement New(string type, string name, string material) => new()
        {
            Id = ArchitecturalElement.NewId(),
            Type = type,
            Name = $"{name} {++counter}",
            BuildingGroupId = gid,
            Material = material
        };

        // Отметка контура: для трёхмерного чертежа — Z контура, для плоского — отметка этажа.
        double BaseOf(DxfContour c) => is3D ? c.MinZ : options.BaseElevation;

        double LimitAbove(DxfContour c, (double X0, double X1, double Y0, double Y1) box)
        {
            double limit = double.MaxValue;
            foreach (var o in drawing.Contours)
            {
                if (o.Layer != c.Layer || ReferenceEquals(o, c) || !o.Closed || o.MinZ <= c.MinZ + 1e-3) continue;
                var ob = Box(o);
                double ox = Math.Min(box.X1, ob.X1) - Math.Max(box.X0, ob.X0), oy = Math.Min(box.Y1, ob.Y1) - Math.Max(box.Y0, ob.Y0);
                if (ox > 0.5 * (box.X1 - box.X0) && oy > 0.5 * (box.Y1 - box.Y0)) limit = Math.Min(limit, o.MinZ);
            }
            return limit;
        }

        double HeightOf(DxfContour c, double fallback)
        {
            if (!is3D) return fallback;
            var b = Box(c);
            return faces.Height(c.Layer, b.X0, b.X1, b.Y0, b.Y1, c.MinZ, LimitAbove(c, b)) is { } h ? R(h) : fallback;
        }

        var closed = drawing.Contours.Where(c => c.Closed && (c.IsCircle || c.Ring().Count >= 3)).ToList();
        var walls = new List<ArchitecturalElement>();
        var slabs = new List<ArchitecturalElement>();
        var openingContours = new List<DxfContour>();
        var fillContours = new List<(DxfContour C, string Type)>();

        foreach (var c in closed)
        {
            var cat = c.Category;
            // Чужой чертёж: на неизвестных слоях окружности и небольшие прямоугольники 200..1000 мм — колонны.
            if (cat == DxfLayerCategory.Unknown && IsColumnLike(c)) cat = DxfLayerCategory.Column;
            switch (cat)
            {
                case DxfLayerCategory.Column:
                {
                    if (!c.IsCircle && !IsColumnLike(c) && c.Category != DxfLayerCategory.Column) break;
                    var col = New(ElementTypes.Column, "Колонна", "concrete");
                    FitColumn(col, c, faces, BaseOf(c));
                    double h = HeightOf(c, options.LevelHeight);
                    col.SetSize(col.Width, h, col.Depth);
                    // Плоский чертёж: сечение, подтверждённое в окне верификации, заменяет контур (круг — диаметр W).
                    if (!is3D && options.ColumnWidth > 0)
                        col.SetSize(options.ColumnWidth, h, col.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe ? options.ColumnWidth : options.ColumnDepth > 0 ? options.ColumnDepth : options.ColumnWidth);
                    col.PlaceOnBase(R(BaseOf(c)));
                    elements.Add(col);
                    break;
                }
                case DxfLayerCategory.Wall:
                case DxfLayerCategory.CurtainWall:
                {
                    var type = cat == DxfLayerCategory.CurtainWall ? ElementTypes.CurtainWall : ElementTypes.Wall;
                    var wall = New(type, type == ElementTypes.Wall ? "Стена" : "Витраж", type == ElementTypes.Wall ? "brick" : "glass");
                    if (!FitRectangle(wall, c, preferLongest: !drawing.IsOwnExport, alwaysRect: false))
                        FitPolygon(wall, c);
                    double h = HeightOf(c, options.EffectiveWallHeight);
                    wall.SetSize(wall.Width, h, wall.Depth);
                    wall.PlaceOnBase(R(BaseOf(c)));
                    elements.Add(wall);
                    walls.Add(wall);
                    break;
                }
                case DxfLayerCategory.Beam:
                {
                    var beam = New(ElementTypes.Beam, "Балка", "concrete");
                    FitRectangle(beam, c, preferLongest: true, alwaysRect: true);
                    double h = HeightOf(c, options.BeamHeight);
                    beam.SetSize(beam.Width, h, !is3D && options.BeamWidth > 0 ? options.BeamWidth : beam.Depth);
                    beam.PlaceOnBase(R(is3D ? c.MinZ : options.BaseElevation + options.LevelHeight - h));
                    if (is3D) DetectBeamProfile(beam, c, faces);
                    elements.Add(beam);
                    break;
                }
                case DxfLayerCategory.Slab:
                case DxfLayerCategory.Foundation:
                case DxfLayerCategory.Roof:
                case DxfLayerCategory.Terrace:
                case DxfLayerCategory.Pool:
                case DxfLayerCategory.Stairs:
                case DxfLayerCategory.Asset:
                {
                    var (type, name, material, thickness) = cat switch
                    {
                        DxfLayerCategory.Foundation => (ElementTypes.Foundation, "Фундамент", "concrete", options.FoundationThickness),
                        DxfLayerCategory.Roof => (ElementTypes.Roof, "Кровля", "metal", options.RoofThickness),
                        DxfLayerCategory.Terrace => (ElementTypes.Terrace, "Терраса", "wood", 0.15),
                        DxfLayerCategory.Pool => (ElementTypes.Pool, "Бассейн", "tile", 1.5),
                        DxfLayerCategory.Stairs => (ElementTypes.Stairs, "Лестница", "concrete", options.LevelHeight),
                        DxfLayerCategory.Asset => (ElementTypes.Asset, "Объект", "generic", 1.0),
                        _ => (ElementTypes.Slab, "Плита", "concrete", options.SlabThickness)
                    };
                    var el = New(type, name, material);
                    if (!FitRectangle(el, c, preferLongest: !drawing.IsOwnExport, alwaysRect: false)) FitPolygon(el, c);
                    double h = HeightOf(c, thickness);
                    el.SetSize(el.Width, h, el.Depth);
                    double baseY = is3D ? c.MinZ
                        : type is ElementTypes.Slab or ElementTypes.Roof ? options.BaseElevation + options.LevelHeight
                        : type is ElementTypes.Foundation ? options.BaseElevation - h
                        : options.BaseElevation;
                    el.PlaceOnBase(R(baseY));
                    if (type == ElementTypes.Roof)
                    {
                        var b = Box(c);
                        int slopes = is3D ? faces.SlopeDirections(c.Layer, b.X0, b.X1, b.Y0, b.Y1, c.MinZ) : 0;
                        el.RoofShape = slopes switch { 0 => RoofShapes.Flat, 1 => RoofShapes.Shed, 2 => RoofShapes.Gable, _ => RoofShapes.Hip };
                    }
                    elements.Add(el);
                    if (el.CanHostSlabOpenings) slabs.Add(el);
                    break;
                }
                case DxfLayerCategory.Window:
                    fillContours.Add((c, ElementTypes.Window));
                    break;
                case DxfLayerCategory.Door:
                    fillContours.Add((c, ElementTypes.Door));
                    break;
                case DxfLayerCategory.Opening:
                    openingContours.Add(c);
                    break;
            }
        }

        // Спаренные параллельные отрезки (100..600 мм) — стены чертежа без замкнутых контуров.
        int pairedWalls = 0;
        foreach (var (a, b, thickness, layer) in PairedSegments(drawing.Contours.Where(c => !c.Closed && c.Category is DxfLayerCategory.Wall or DxfLayerCategory.Unknown).ToList()))
        {
            var wall = New(ElementTypes.Wall, "Стена", "brick");
            double len = a.Distance2D(b);
            var mid = new DxfPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            wall.SetSize(R(len), options.EffectiveWallHeight, R(thickness));
            wall.SetPosition(R(mid.X), 0, R(-mid.Y));
            wall.SetRotationY(R(Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI));
            wall.PlaceOnBase(R(is3D ? a.Z : options.BaseElevation));
            elements.Add(wall);
            walls.Add(wall);
            pairedWalls++;
        }

        // Окна и двери: прямоугольный контур в плане, привязка к ближайшей стене.
        foreach (var (c, type) in fillContours)
        {
            var el = New(type, type == ElementTypes.Window ? "Окно" : "Дверь", type == ElementTypes.Window ? "glass" : "wood");
            FitRectangle(el, c, preferLongest: true, alwaysRect: true);
            double h = HeightOf(c, type == ElementTypes.Window ? options.WindowHeight : options.DoorHeight);
            el.SetSize(el.Width, h, el.Depth);
            el.PlaceOnBase(R(is3D ? c.MinZ : options.BaseElevation + (type == ElementTypes.Window ? options.WindowSill : 0)));
            if (NearestWall(walls, el.X, el.Z, el.Y) is { } host)
            {
                el.HostElementId = host.Id;
                el.SetRotationY(host.RotationY);
                if (!is3D) el.SetSize(el.Width, el.Height, host.Depth + ParametricSolverService.OpeningClearance);
            }
            elements.Add(el);
        }

        // Проёмы: вертикальный контур — сквозной проём стены, горизонтальный — отверстие плиты.
        int wallOpenings = 0, slabOpenings = 0;
        foreach (var c in openingContours)
        {
            var ring = c.Ring();
            if (ring.Count < 3) continue;
            bool vertical = c.MaxZ - c.MinZ > 1e-3;
            if (vertical)
            {
                // Контур в плоскости стены: направление — по наибольшему горизонтальному разносу.
                var lo = ring.OrderBy(p => p.X).ThenBy(p => p.Y).First();
                var hi = ring.OrderByDescending(p => p.Distance2D(lo)).First();
                double width = lo.Distance2D(hi), height = c.MaxZ - c.MinZ;
                var mid = new DxfPoint((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2);
                var op = New(ElementTypes.WallOpening, "Проём", "void");
                op.SetSize(R(width), R(height), 0.3);
                op.SetPosition(R(mid.X), R(c.MinZ + height / 2), R(-mid.Y));
                op.SetRotationY(R(Math.Atan2(hi.Y - lo.Y, hi.X - lo.X) * 180 / Math.PI));
                if (NearestWall(walls, op.X, op.Z, op.Y) is { } host)
                {
                    op.HostElementId = host.Id;
                    op.SetRotationY(host.RotationY);
                    op.SetSize(op.Width, op.Height, R(host.Depth + ParametricSolverService.OpeningClearance));
                }
                elements.Add(op);
                wallOpenings++;
            }
            else
            {
                var hole = New(ElementTypes.SlabOpening, "Отверстие", "void");
                if (!FitRectangle(hole, c, preferLongest: false, alwaysRect: false)) FitPolygon(hole, c);
                var host = slabs.Where(s => SceneStateService.PointInPolygon(hole.X, hole.Z, AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(s)))
                    .OrderBy(s => Math.Abs(s.BaseY - c.MinZ)).FirstOrDefault();
                if (host is not null && (!is3D || Math.Abs(host.BaseY - c.MinZ) < 0.3))
                {
                    hole.HostElementId = host.Id;
                    hole.SetSize(hole.Width, R(host.Height + ParametricSolverService.OpeningClearance), hole.Depth);
                    hole.SetPosition(hole.X, host.Y, hole.Z);
                    hole.LevelIndex = host.LevelIndex;
                }
                else
                {
                    hole.SetSize(hole.Width, 0.3, hole.Depth);
                    hole.PlaceOnBase(R(c.MinZ));
                }
                elements.Add(hole);
                slabOpenings++;
            }
        }

        AssignLevels(elements, is3D ? null : options.LevelIndex);

        var axes = ExtractAxes(drawing, gid);
        if (axes.Count == 0) report.Add("Координационные оси: не найдены (будут построены автоматически по несущим)");

        report.Insert(0, $"DXF «{drawing.FileName}»: единицы — {drawing.UnitsTitle} ({drawing.UnitsSource}), масштаб ×{drawing.UnitScale.ToString("0.####", Inv)}" +
                         (drawing.IsOwnExport ? ", файл Architect Studio Pro — координаты без сдвига" : $", центрирование: сдвиг ({drawing.Offset.X:0.###}; {drawing.Offset.Y:0.###}) м"));
        report.Add($"Распознано: стен {elements.Count(e => e.Type == ElementTypes.Wall)} (из спаренных линий {pairedWalls}), колонн {elements.Count(e => e.Type == ElementTypes.Column)}, " +
                   $"балок {elements.Count(e => e.Type == ElementTypes.Beam)}, плит {elements.Count(e => e.Type is ElementTypes.Slab or ElementTypes.Foundation)}, " +
                   $"окон {elements.Count(e => e.Type == ElementTypes.Window)}, дверей {elements.Count(e => e.Type == ElementTypes.Door)}, " +
                   $"проёмов стен {wallOpenings}, отверстий плит {slabOpenings}, осей {axes.Count}");
        report.Add(is3D
            ? $"Высоты и отметки — по граням 3DFACE ({drawing.FaceCount:N0})"
            : $"Плоский чертёж (параметры подтверждены): этаж {options.LevelHeight:0.###} м, стены {options.EffectiveWallHeight:0.###} м, отметка {options.BaseElevation:0.###} м, " +
              $"плиты {options.SlabThickness * 1000:0} мм, фундамент {options.FoundationThickness * 1000:0} мм, покрытие {options.RoofThickness * 1000:0} мм, " +
              $"колонны {(options.ColumnWidth > 0 ? $"{options.ColumnWidth * 1000:0}×{(options.ColumnDepth > 0 ? options.ColumnDepth : options.ColumnWidth) * 1000:0} мм" : "по чертежу")}, " +
              $"балки {(options.BeamWidth > 0 ? $"{options.BeamWidth * 1000:0}" : "B по чертежу")}×{options.BeamHeight * 1000:0} мм");
        return new DxfLiftResult(elements, axes, report);
    }

    /// <summary>
    /// Предпросмотр автоподъёма для окна верификации: те же правила распознавания, что в LiftTo3D (слои + эвристики), без
    /// создания элементов — число стен, колонн, балок, плит, окон, дверей, проёмов и осей, типичное сечение колонн и толщина стен.
    /// </summary>
    public static DxfLiftPreview AnalyzeLift(DxfDrawing drawing)
    {
        bool is3D = drawing.FaceCount > 0;
        var closed = drawing.Contours.Where(c => c.Closed && (c.IsCircle || c.Ring().Count >= 3)).ToList();
        int walls = 0, columns = 0, beams = 0, slabs = 0, foundations = 0, roofs = 0, windows = 0, doors = 0, openings = 0;
        var columnSizes = new List<(double W, double D, bool Circle)>();
        var beamWidths = new List<double>();
        var noFaces = new FaceIndex([]);
        foreach (var c in closed)
        {
            var cat = c.Category;
            if (cat == DxfLayerCategory.Unknown && IsColumnLike(c)) cat = DxfLayerCategory.Column;
            switch (cat)
            {
                case DxfLayerCategory.Column when c.IsCircle || IsColumnLike(c) || c.Category == DxfLayerCategory.Column:
                    columns++;
                    var probe = new ArchitecturalElement { Type = ElementTypes.Column };
                    FitColumn(probe, c, noFaces, 0);
                    columnSizes.Add((Math.Round(probe.Width, 3), Math.Round(probe.Depth, 3), probe.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe));
                    break;
                case DxfLayerCategory.Wall or DxfLayerCategory.CurtainWall: walls++; break;
                case DxfLayerCategory.Beam:
                    beams++;
                    var beam = new ArchitecturalElement { Type = ElementTypes.Beam };
                    FitRectangle(beam, c, preferLongest: true, alwaysRect: true);
                    beamWidths.Add(Math.Round(beam.Depth, 3));
                    break;
                case DxfLayerCategory.Slab or DxfLayerCategory.Terrace: slabs++; break;
                case DxfLayerCategory.Foundation: foundations++; break;
                case DxfLayerCategory.Roof: roofs++; break;
                case DxfLayerCategory.Window: windows++; break;
                case DxfLayerCategory.Door: doors++; break;
                case DxfLayerCategory.Opening: openings++; break;
            }
        }
        var paired = PairedSegments(drawing.Contours.Where(c => !c.Closed && c.Category is DxfLayerCategory.Wall or DxfLayerCategory.Unknown).ToList()).ToList();
        double? wallThickness = paired.Count > 0 ? paired.GroupBy(p => Math.Round(p.Thickness, 3)).OrderByDescending(g => g.Count()).First().Key : null;
        (double W, double D, bool Circle)? columnSection = columnSizes.Count > 0 ? columnSizes.GroupBy(s => s).OrderByDescending(g => g.Count()).First().Key : null;
        double? beamWidth = beamWidths.Count > 0 ? beamWidths.GroupBy(b => b).OrderByDescending(g => g.Count()).First().Key : null;
        int axes = ExtractAxes(drawing, "preview").Count;
        return new DxfLiftPreview(drawing.FileName, is3D, walls, paired.Count, wallThickness, columns, columnSection, beams, beamWidth, slabs, foundations, roofs,
            windows, doors, openings, axes, drawing.MaxX - drawing.MinX, drawing.MaxY - drawing.MinY);
    }

    // ═════════════════════════════ Режим Б: создание по клику на подложке ═════════════════════════════

    /// <summary>Действия пилюли подложки.</summary>
    public const string MakeWall = "wall", MakeColumn = "column", MakeBeam = "beam", MakeSlab = "slab", MakeOpening = "opening";

    /// <summary>
    /// Элемент по выбранному контуру подложки (в стиле Shapr3D): стена по линии (толщина — по парной параллельной линии
    /// 100..600 мм, иначе 300 мм), колонна в узле (окружность/малый контур — по сечению, иначе 400 × 400 в ближайшей вершине),
    /// балка по линии под перекрытием, плита по замкнутому контуру, проём — отверстие плиты по контуру или проём ближайшей стены.
    /// </summary>
    public static (List<ArchitecturalElement> Elements, string? Problem) CreateFromContour(DxfDrawing drawing, DxfContour contour, DxfPoint click,
        string action, DxfLiftOptions o, IReadOnlyList<ArchitecturalElement> scene)
    {
        var (_, a, b) = contour.Nearest(click);
        double len = a.Distance2D(b);
        var created = new List<ArchitecturalElement>();
        ArchitecturalElement New(string type, string name, string material) => new()
        {
            Type = type, Name = name, BuildingGroupId = o.GroupId, LevelIndex = o.LevelIndex, Material = material
        };

        switch (action)
        {
            case MakeWall:
            {
                if (len < 0.05) return (created, "Слишком короткий отрезок для стены");
                var (p0, p1, thickness) = WallFromLine(drawing, contour, a, b);
                var wall = New(ElementTypes.Wall, "Стена", "brick");
                wall.SetSize(R(p0.Distance2D(p1)), o.LevelHeight, R(thickness));
                wall.SetPosition(R((p0.X + p1.X) / 2), 0, R(-(p0.Y + p1.Y) / 2));
                wall.SetRotationY(R(Math.Atan2(p1.Y - p0.Y, p1.X - p0.X) * 180 / Math.PI));
                wall.PlaceOnBase(o.BaseElevation);
                created.Add(wall);
                break;
            }
            case MakeColumn:
            {
                var col = New(ElementTypes.Column, "Колонна", "concrete");
                var box = Box(contour);
                if (contour.IsCircle || contour.Closed && Math.Max(box.X1 - box.X0, box.Y1 - box.Y0) <= 1.5)
                    FitColumn(col, contour, new FaceIndex([]), o.BaseElevation);
                else
                {
                    // Узел — ближайшая к клику вершина контура.
                    var node = contour.Points.OrderBy(p => p.Distance2D(click)).First();
                    col.SetSize(0.4, 1, 0.4);
                    col.SetPosition(R(node.X), 0, R(-node.Y));
                }
                col.SetSize(col.Width, o.LevelHeight, col.Depth);
                col.PlaceOnBase(o.BaseElevation);
                created.Add(col);
                break;
            }
            case MakeBeam:
            {
                if (len < 0.1) return (created, "Слишком короткий отрезок для балки");
                var beam = New(ElementTypes.Beam, "Балка", "concrete");
                beam.SetSize(R(len), 0.5, 0.3);
                beam.SetPosition(R((a.X + b.X) / 2), 0, R(-(a.Y + b.Y) / 2));
                beam.SetRotationY(R(Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI));
                beam.PlaceOnBase(o.BaseElevation + o.LevelHeight - 0.5);
                created.Add(beam);
                break;
            }
            case MakeSlab:
            {
                if (!contour.Closed) return (created, "Плита строится только по замкнутому контуру");
                var slab = New(ElementTypes.Slab, "Плита", "concrete");
                if (!FitRectangle(slab, contour, preferLongest: true, alwaysRect: false)) FitPolygon(slab, contour);
                slab.SetSize(slab.Width, 0.2, slab.Depth);
                slab.PlaceOnBase(o.BaseElevation + o.LevelHeight);
                created.Add(slab);
                break;
            }
            case MakeOpening:
            {
                var center = contour.Closed ? contour.Center : click;
                var host = scene.Where(s => s.CanHostSlabOpenings && contour.Closed &&
                                            SceneStateService.PointInPolygon(center.X, -center.Y, AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(s)))
                    .OrderBy(s => Math.Abs(s.TopY - (o.BaseElevation + o.LevelHeight + 0.2))).FirstOrDefault();
                if (host is not null)
                {
                    var hole = New(ElementTypes.SlabOpening, "Отверстие", "void");
                    if (!FitRectangle(hole, contour, preferLongest: false, alwaysRect: false)) FitPolygon(hole, contour);
                    hole.HostElementId = host.Id;
                    hole.LevelIndex = host.LevelIndex;
                    hole.BuildingGroupId = host.BuildingGroupId;
                    hole.SetSize(hole.Width, R(host.Height + ParametricSolverService.OpeningClearance), hole.Depth);
                    hole.SetPosition(hole.X, host.Y, hole.Z);
                    created.Add(hole);
                    break;
                }
                var walls = scene.Where(w => ElementTypes.IsHostType(w.Type) && w.Polygon2D.Count < 3).ToList();
                var wall = NearestWall(walls, click.X, -click.Y, o.BaseElevation + 1.0);
                if (wall is null) return (created, "Рядом нет стены или плиты для проёма");
                var (lx, _) = ArchitecturalElement.RotateY(click.X - wall.X, -click.Y - wall.Z, -wall.RotationY);
                double width = Math.Clamp(len is > 0.3 and < 4 ? len : 1.2, 0.3, Math.Max(0.3, wall.Width - 0.2));
                lx = Math.Clamp(lx, -wall.Width / 2 + width / 2, wall.Width / 2 - width / 2);
                double height = Math.Min(1.5, wall.Height - 0.2), sill = Math.Min(0.9, Math.Max(0, wall.Height - height - 0.1));
                var (wx, wz) = ArchitecturalElement.RotateY(lx, 0, wall.RotationY);
                var op = New(ElementTypes.WallOpening, "Проём", "void");
                op.HostElementId = wall.Id;
                op.LevelIndex = wall.LevelIndex;
                op.BuildingGroupId = wall.BuildingGroupId;
                op.SetSize(R(width), R(height), R(wall.Depth + ParametricSolverService.OpeningClearance));
                op.SetPosition(R(wall.X + wx), R(wall.BaseY + sill + height / 2), R(wall.Z + wz));
                op.SetRotationY(wall.RotationY);
                created.Add(op);
                break;
            }
            default:
                return (created, "Неизвестное действие");
        }
        return (created, null);
    }

    /// <summary>Ось стены по линии: при парной параллельной линии на 100..600 мм — середина между ними и толщина по зазору.</summary>
    private static (DxfPoint A, DxfPoint B, double Thickness) WallFromLine(DxfDrawing drawing, DxfContour contour, DxfPoint a, DxfPoint b)
    {
        double len = a.Distance2D(b);
        var u = new DxfPoint((b.X - a.X) / len, (b.Y - a.Y) / len);
        (double Dist, double Side, double T0, double T1)? best = null;
        // Парная линия — только на слоях стен (или неизвестных): оси, проёмы и мебель толщину стены не задают.
        foreach (var c in drawing.Contours.Where(c => c.Category is DxfLayerCategory.Wall or DxfLayerCategory.CurtainWall or DxfLayerCategory.Unknown))
            foreach (var (p, q) in c.Segments())
            {
                if (ReferenceEquals(c, contour) && p.Equals(a) && q.Equals(b)) continue;
                var d = q - p;
                double l2 = d.Length2D;
                if (l2 < 0.1 || Math.Abs((u.X * d.Y - u.Y * d.X) / l2) > 0.0175) continue;
                double side = -(p.X - a.X) * u.Y + (p.Y - a.Y) * u.X;
                double dist = Math.Abs(side);
                if (dist < 0.1 || dist > 0.6) continue;
                double tp = (p.X - a.X) * u.X + (p.Y - a.Y) * u.Y, tq = (q.X - a.X) * u.X + (q.Y - a.Y) * u.Y;
                double t0 = Math.Max(0, Math.Min(tp, tq)), t1 = Math.Min(len, Math.Max(tp, tq));
                if (t1 - t0 < 0.5 * len) continue;
                if (best is null || dist < best.Value.Dist) best = (dist, side, t0, t1);
            }
        if (best is not { } m) return (a, b, 0.3);
        var n = new DxfPoint(-u.Y * m.Side / 2, u.X * m.Side / 2);
        return (a + u * m.T0 + n, a + u * m.T1 + n, m.Dist);
    }

    /// <summary>Округление до точности импорта 0.1 мм (без «−0»).</summary>
    private static double R(double v)
    {
        double r = Math.Round(Math.Round(v / Precision) * Precision, 4);
        return r == 0 ? 0 : r;
    }

    private static (double X0, double X1, double Y0, double Y1) Box(DxfContour c)
    {
        if (c.IsCircle)
        {
            var p = c.Points[0];
            return (p.X - c.Radius, p.X + c.Radius, p.Y - c.Radius, p.Y + c.Radius);
        }
        return (c.Points.Min(p => p.X), c.Points.Max(p => p.X), c.Points.Min(p => p.Y), c.Points.Max(p => p.Y));
    }

    /// <summary>Колонна по геометрии: окружность или замкнутый прямоугольник 200..1000 мм.</summary>
    private static bool IsColumnLike(DxfContour c)
    {
        if (c.IsCircle) return c.Radius * 2 is >= 0.2 and <= 1.0;
        var ring = c.Ring();
        if (ring.Count != 4 || !IsRectangle(ring)) return false;
        double a = ring[0].Distance2D(ring[1]), b = ring[1].Distance2D(ring[2]);
        return a is >= 0.2 and <= 1.0 && b is >= 0.2 and <= 1.0;
    }

    private static bool IsRectangle(List<DxfPoint> ring)
    {
        if (ring.Count != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            var a = ring[(i + 3) % 4];
            var b = ring[i];
            var c = ring[(i + 1) % 4];
            var u = a - b;
            var v = c - b;
            double lu = u.Length2D, lv = v.Length2D;
            if (lu < 1e-6 || lv < 1e-6) return false;
            if (Math.Abs((u.X * v.X + u.Y * v.Y) / (lu * lv)) > 1e-3) return false;
        }
        return true;
    }

    /// <summary>
    /// Прямоугольный контур → центр, W × D и поворот. preferLongest — W по длинной стороне (иначе — по первой стороне,
    /// как пишет контуры экспорт AI Architect Studio). false — контур не прямоугольник.
    /// </summary>
    private static bool FitRectangle(ArchitecturalElement e, DxfContour c, bool preferLongest, bool alwaysRect)
    {
        var ring = c.Ring();
        if (!IsRectangle(ring))
        {
            if (!alwaysRect) return false;
            // Минимальный по площади описывающий прямоугольник по направлениям сторон.
            double bestArea = double.MaxValue;
            (double Angle, double W, double D, DxfPoint C) best = default;
            for (int i = 0; i < ring.Count; i++)
            {
                var d = ring[(i + 1) % ring.Count] - ring[i];
                if (d.Length2D < 1e-6) continue;
                double ang = Math.Atan2(d.Y, d.X), cos = Math.Cos(ang), sin = Math.Sin(ang);
                double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
                foreach (var p in ring)
                {
                    double u = p.X * cos + p.Y * sin, v = -p.X * sin + p.Y * cos;
                    u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
                }
                double area = (u1 - u0) * (v1 - v0);
                if (area >= bestArea) continue;
                bestArea = area;
                double cu = (u0 + u1) / 2, cv = (v0 + v1) / 2;
                best = (ang, u1 - u0, v1 - v0, new DxfPoint(cu * cos - cv * sin, cu * sin + cv * cos));
            }
            if (best.W > best.D) Apply(e, best.C, best.W, best.D, best.Angle);
            else Apply(e, best.C, best.D, best.W, best.Angle + Math.PI / 2);
            return true;
        }

        var s0 = ring[1] - ring[0];
        var s1 = ring[2] - ring[1];
        bool firstIsLong = !preferLongest || s0.Length2D >= s1.Length2D - 1e-9;
        var dir = firstIsLong ? s0 : s1;
        double w = firstIsLong ? s0.Length2D : s1.Length2D, dd = firstIsLong ? s1.Length2D : s0.Length2D;
        var center = new DxfPoint(ring.Average(p => p.X), ring.Average(p => p.Y));
        Apply(e, center, w, dd, Math.Atan2(dir.Y, dir.X));
        return true;

        static void Apply(ArchitecturalElement e, DxfPoint center, double w, double d, double angle)
        {
            e.SetSize(R(w), e.Height, R(d));
            e.SetPosition(R(center.X), e.Y, R(-center.Y));
            e.SetRotationY(R(angle * 180 / Math.PI));
        }
    }

    /// <summary>Произвольный контур → Polygon2D относительно центра габарита (сцена: x, z = −y).</summary>
    private static void FitPolygon(ArchitecturalElement e, DxfContour c)
    {
        var ring = c.Ring();
        double cx = Math.Round((ring.Min(p => p.X) + ring.Max(p => p.X)) / 2, 3), cy = Math.Round((ring.Min(p => p.Y) + ring.Max(p => p.Y)) / 2, 3);
        e.SetPosition(R(cx), e.Y, R(-cy));
        e.SetRotationY(0);
        e.Polygon2D = ring.Select(p => new Point2D(R(p.X - cx), R(-(p.Y - cy)))).ToList();
        e.SetSize(R(ring.Max(p => p.X) - ring.Min(p => p.X)), e.Height, R(ring.Max(p => p.Y) - ring.Min(p => p.Y)));
    }

    /// <summary>
    /// Сечение колонны по контуру: окружность / 24-угольник (круг или эллипс; труба — если по граням 3DFACE видна
    /// внутренняя стенка), 12-угольник двутавра, прямоугольник или произвольный полигон.
    /// </summary>
    private static void FitColumn(ArchitecturalElement col, DxfContour c, FaceIndex faces, double baseZ)
    {
        if (c.IsCircle)
        {
            col.ProfileType = ProfileTypes.Circle;
            col.SetSize(R(2 * c.Radius), col.Height, R(2 * c.Radius));
            col.SetPosition(R(c.Points[0].X), col.Y, R(-c.Points[0].Y));
            if (faces.InnerRadius(c.Layer, c.Points[0], c.Radius, baseZ) is { } inner)
            {
                col.ProfileType = ProfileTypes.Pipe;
                col.ProfileWallThickness = R(c.Radius - inner);
            }
            return;
        }

        var ring = c.Ring();
        var center = new DxfPoint(ring.Average(p => p.X), ring.Average(p => p.Y));
        if (ring.Count >= 16 && IsEllipse(ring, center, out double ax, out double ay, out double angle))
        {
            col.ProfileType = ProfileTypes.Circle;
            col.SetSize(R(2 * ax), col.Height, R(2 * ay));
            col.SetPosition(R(center.X), col.Y, R(-center.Y));
            col.SetRotationY(R(angle * 180 / Math.PI));
            double r = Math.Min(ax, ay);
            if (faces.InnerRadius(c.Layer, center, Math.Max(ax, ay), baseZ) is { } inner && inner < r * 0.97)
            {
                col.ProfileType = ProfileTypes.Pipe;
                col.ProfileWallThickness = R(r - inner);
            }
            return;
        }
        if (ring.Count == 12 && TryIShape(ring, out var w, out var d, out var tf, out var tw, out var ang, out var mid))
        {
            col.ProfileType = ProfileTypes.IBeam;
            col.ProfileWallThickness = R(tf);
            col.ProfileWebThickness = R(tw);
            col.SetSize(R(w), col.Height, R(d));
            col.SetPosition(R(mid.X), col.Y, R(-mid.Y));
            col.SetRotationY(R(ang * 180 / Math.PI));
            return;
        }
        if (FitRectangle(col, c, preferLongest: false, alwaysRect: false))
        {
            col.ProfileType = ProfileTypes.Rectangle;
            return;
        }
        FitPolygon(col, c);
        col.ProfileType = ProfileTypes.Polygon;
    }

    /// <summary>
    /// Сечение балки по 3D-граням: вершины продольных граней (идут по всей длине балки — грани соседних балок в узле
    /// не попадают) в начальном торце. 4 точки — прямоугольник, 12 — двутавр (полка и стенка по уровням вершин),
    /// 16 и более — круг; внутреннее кольцо вершин — труба.
    /// </summary>
    private static void DetectBeamProfile(ArchitecturalElement beam, DxfContour c, FaceIndex faces)
    {
        var b = Box(c);
        double a = beam.RotationY * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double cx = beam.X, cy = -beam.Z, half = beam.Width / 2, zc = beam.Y;
        double U(DxfPoint p) => (p.X - cx) * cos + (p.Y - cy) * sin;
        var pts = faces.FacesIn(c.Layer, b.X0, b.X1, b.Y0, b.Y1, beam.BaseY, beam.TopY)
            .Where(f => f.Max(U) - f.Min(U) >= 0.9 * beam.Width)
            .SelectMany(f => f).ToList();
        if (pts.Count == 0) return;
        var cap = pts.Where(p => Math.Abs(U(p) + half) < 2e-3)
            .Select(p => (V: Math.Round(-(p.X - cx) * sin + (p.Y - cy) * cos, 4), Z: Math.Round(p.Z - zc, 4)))
            .Distinct().ToList();
        if (cap.Count == 12)
        {
            var zs = cap.Select(q => q.Z).Distinct().Order().ToList();
            var vs = cap.Select(q => q.V).Distinct().Order().ToList();
            if (zs.Count == 4 && vs.Count == 4)
            {
                beam.ProfileType = ProfileTypes.IBeam;
                beam.ProfileWallThickness = R(zs[1] - zs[0]);
                beam.ProfileWebThickness = R(vs[2] - vs[1]);
            }
        }
        else if (cap.Count >= 16)
        {
            double ry = Math.Max(1e-6, beam.Height / 2), rv = Math.Max(1e-6, beam.Depth / 2);
            var inner = cap.Where(q => Math.Sqrt(q.V * q.V / (rv * rv) + q.Z * q.Z / (ry * ry)) < 0.97).ToList();
            beam.ProfileType = inner.Count >= 8 ? ProfileTypes.Pipe : ProfileTypes.Circle;
            if (inner.Count >= 8) beam.ProfileWallThickness = R(ry - inner.Max(q => Math.Abs(q.Z)));
        }
    }

    private static bool IsEllipse(List<DxfPoint> ring, DxfPoint center, out double ax, out double ay, out double angle)
    {
        var first = ring[0] - center;
        angle = Math.Atan2(first.Y, first.X);
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var local = ring.Select(p => p - center).Select(p => (U: p.X * cos + p.Y * sin, V: -p.X * sin + p.Y * cos)).ToList();
        ax = local.Max(p => Math.Abs(p.U));
        ay = local.Max(p => Math.Abs(p.V));
        if (ax < 1e-6 || ay < 1e-6) return false;
        double a = ax, b = ay;
        return local.All(p => Math.Abs(p.U * p.U / (a * a) + p.V * p.V / (b * b) - 1) < 0.02);
    }

    /// <summary>Контур двутавра (как пишет экспорт: полки по краям, стенка по оси).</summary>
    private static bool TryIShape(List<DxfPoint> ring, out double w, out double d, out double tf, out double tw, out double angle, out DxfPoint center)
    {
        w = d = tf = tw = angle = 0;
        center = default;
        var e0 = ring[1] - ring[0];
        w = e0.Length2D;
        if (w < 1e-6) return false;
        angle = Math.Atan2(e0.Y, e0.X);
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var mid = new DxfPoint(ring.Average(p => p.X), ring.Average(p => p.Y));
        var local = ring.Select(p => p - mid).Select(p => (U: p.X * cos + p.Y * sin, V: -p.X * sin + p.Y * cos)).ToList();
        double v0 = local.Min(p => p.V), v1 = local.Max(p => p.V), u0 = local.Min(p => p.U), u1 = local.Max(p => p.U);
        d = v1 - v0;
        tf = Math.Abs(local[2].V - local[1].V);
        tw = 2 * Math.Abs(local[3].U - (u0 + u1) / 2);
        center = new DxfPoint(mid.X + ((u0 + u1) / 2) * cos - ((v0 + v1) / 2) * sin, mid.Y + ((u0 + u1) / 2) * sin + ((v0 + v1) / 2) * cos);
        return tf > 1e-4 && tf < d / 2 && tw > 1e-4 && tw < w;
    }

    /// <summary>Спаренные параллельные отрезки на расстоянии 100..600 мм с перекрытием проекций → ось и толщина стены.</summary>
    private static IEnumerable<(DxfPoint A, DxfPoint B, double Thickness, string Layer)> PairedSegments(List<DxfContour> open)
    {
        var segs = open.SelectMany(c => c.Segments().Select(s => (s.A, s.B, c.Layer, Known: c.Category == DxfLayerCategory.Wall)))
            .Where(s => s.A.Distance2D(s.B) >= 0.3).ToList();
        var used = new bool[segs.Count];
        var candidates = new List<(int I, int J, double Dist, double T0, double T1)>();
        for (int i = 0; i < segs.Count; i++)
        {
            var (a, b, _, knownA) = segs[i];
            var dir = b - a;
            double len = dir.Length2D;
            var u = new DxfPoint(dir.X / len, dir.Y / len);
            for (int j = i + 1; j < segs.Count; j++)
            {
                var (c, d, _, knownB) = segs[j];
                var dir2 = d - c;
                double len2 = dir2.Length2D;
                double cross = (u.X * dir2.Y - u.Y * dir2.X) / len2;
                if (Math.Abs(cross) > 0.0175) continue; // ≤ 1°
                double dist = Math.Abs(-(c.X - a.X) * u.Y + (c.Y - a.Y) * u.X);
                if (dist < 0.1 || dist > 0.6) continue;
                double tc = (c.X - a.X) * u.X + (c.Y - a.Y) * u.Y, td = (d.X - a.X) * u.X + (d.Y - a.Y) * u.Y;
                double t0 = Math.Max(0, Math.Min(tc, td)), t1 = Math.Min(len, Math.Max(tc, td));
                double overlap = t1 - t0;
                double shorter = Math.Min(len, len2);
                bool known = knownA || knownB;
                if (overlap < 0.3 || overlap < 0.5 * shorter || (!known && overlap < 1.0)) continue;
                candidates.Add((i, j, dist, t0, t1));
            }
        }
        foreach (var (i, j, dist, t0, t1) in candidates.OrderBy(c => c.Dist))
        {
            if (used[i] || used[j]) continue;
            used[i] = used[j] = true;
            var (a, b, layer, _) = segs[i];
            var dir = b - a;
            double len = dir.Length2D;
            var u = new DxfPoint(dir.X / len, dir.Y / len);
            var (c, _, _, _) = segs[j];
            // Сдвиг к середине между линиями.
            double side = -(c.X - a.X) * u.Y + (c.Y - a.Y) * u.X;
            var n = new DxfPoint(-u.Y * side / 2, u.X * side / 2);
            var p0 = a + u * t0 + n;
            var p1 = a + u * t1 + n;
            yield return (p0 with { Z = a.Z }, p1 with { Z = a.Z }, dist, layer);
        }
    }

    private static ArchitecturalElement? NearestWall(List<ArchitecturalElement> walls, double x, double z, double y)
    {
        ArchitecturalElement? best = null;
        double bestD = double.MaxValue;
        foreach (var w in walls)
        {
            if (y < w.BaseY - 0.2 || y > w.TopY + 0.2) continue;
            var (lx, lz) = ArchitecturalElement.RotateY(x - w.X, z - w.Z, -w.RotationY);
            if (Math.Abs(lx) > w.Width / 2 + 0.05) continue;
            double d = Math.Abs(lz);
            if (d > w.Depth / 2 + 0.35 || d >= bestD) continue;
            best = w;
            bestD = d;
        }
        return best;
    }

    /// <summary>
    /// Этажи по отметкам низа несущих (стены, колонны, витражи, лестницы): перекрытия относятся к этажу под ними,
    /// окна, двери и проёмы — к этажу стены-хоста.
    /// </summary>
    private static void AssignLevels(List<ArchitecturalElement> elements, int? fixedLevel)
    {
        if (fixedLevel is { } lvl)
        {
            foreach (var e in elements) e.LevelIndex = lvl;
            return;
        }
        var bases = elements.Where(e => e.Type is ElementTypes.Wall or ElementTypes.Column or ElementTypes.CurtainWall or ElementTypes.Stairs)
            .Select(e => Math.Round(e.BaseY, 2)).Distinct().Order().ToList();
        var levels = new List<double>();
        foreach (var b in bases)
            if (levels.Count == 0 || b - levels[^1] > 0.5) levels.Add(b);
        if (levels.Count == 0) levels.Add(0);

        int LevelOf(double y, bool ceiling)
        {
            int idx = 0;
            for (int i = 0; i < levels.Count; i++)
                if (ceiling ? levels[i] < y - 0.05 : levels[i] <= y + 0.05) idx = i;
            return idx;
        }

        var byId = elements.ToDictionary(e => e.Id);
        foreach (var e in elements)
        {
            if (e.HostElementId is not null) continue;
            e.LevelIndex = e.Type switch
            {
                ElementTypes.Foundation or ElementTypes.Pool => 0,
                ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Roof or ElementTypes.Shell or ElementTypes.Beam => LevelOf(e.BaseY, ceiling: true),
                _ => LevelOf(e.BaseY, ceiling: false)
            };
        }
        foreach (var e in elements)
            if (e.HostElementId is { } h && byId.TryGetValue(h, out var host)) e.LevelIndex = host.LevelIndex;
    }

    /// <summary>
    /// Координационные оси: длинные отрезки на слоях осей (или на любом слое — с кружками марок на концах); марка —
    /// текст на продолжении оси у её конца. Повторы по этажам и разрывы осей объединяются.
    /// </summary>
    public static List<StructuralGridAxis> ExtractAxes(DxfDrawing drawing, string groupId)
    {
        var texts = drawing.Entities.Where(e => e.Kind == DxfEntityKind.Text).ToList();
        var circles = drawing.Entities.Where(e => e.Kind == DxfEntityKind.Circle && e.Radius * 2 is >= 0.2 and <= 1.6).ToList();
        var candidates = new List<(bool Numeric, double Coordinate, DxfPoint A, DxfPoint B, string? Label)>();

        foreach (var c in drawing.Contours.Where(c => !c.Closed && !c.IsCircle && c.Points.Count == 2))
        {
            var a = c.Points[0];
            var b = c.Points[1];
            double len = a.Distance2D(b);
            if (len < 2.0) continue;
            bool vertical = Math.Abs(a.X - b.X) <= 1e-3 * Math.Max(1, len);
            bool horizontal = Math.Abs(a.Y - b.Y) <= 1e-3 * Math.Max(1, len);
            if (!vertical && !horizontal) continue;

            bool axisLayer = c.Category == DxfLayerCategory.Axis;
            string? label = LabelNear(a, b, texts);
            bool bubbled = circles.Any(ci => OnExtension(ci.Points[0], a, b, ci.Radius * 2.5 + 0.2));
            if (!axisLayer && !(bubbled && len >= 3.0)) continue;
            if (c.Category is not (DxfLayerCategory.Axis or DxfLayerCategory.Unknown)) continue;
            candidates.Add(vertical
                ? (true, Math.Round((a.X + b.X) / 2, 4), a, b, label)
                : (false, Math.Round(-(a.Y + b.Y) / 2, 4), a, b, label));
        }

        var axes = new List<StructuralGridAxis>();
        foreach (var group in candidates.GroupBy(c => (c.Numeric, Math.Round(c.Coordinate * 1000))))
        {
            var label = group.Select(g => g.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            axes.Add(new StructuralGridAxis(label ?? string.Empty, group.Key.Numeric ? StructuralGridAxis.AlongZ : StructuralGridAxis.AlongX, group.First().Coordinate, groupId));
        }
        // Оси без марки: нумерация по порядку (цифровые — слева направо, буквенные — снизу вверх).
        var nums = axes.Where(a => a.IsNumeric).OrderBy(a => a.Coordinate).ToList();
        for (int i = 0; i < nums.Count; i++) if (nums[i].Label.Length == 0) nums[i].Label = (i + 1).ToString(Inv);
        var letters = axes.Where(a => !a.IsNumeric).OrderByDescending(a => a.Coordinate).ToList();
        for (int i = 0; i < letters.Count; i++) if (letters[i].Label.Length == 0) letters[i].Label = AIArchitectStudio.Rendering.StructuralAxesBuilder.Letter(i);
        return axes;
    }

    private static bool OnExtension(DxfPoint p, DxfPoint a, DxfPoint b, double reach)
    {
        var dir = b - a;
        double len = dir.Length2D;
        var u = new DxfPoint(dir.X / len, dir.Y / len);
        double t = (p.X - a.X) * u.X + (p.Y - a.Y) * u.Y;
        double off = Math.Abs(-(p.X - a.X) * u.Y + (p.Y - a.Y) * u.X);
        return off < 0.3 && (t < 0 && t > -reach || t > len && t < len + reach);
    }

    private static string? LabelNear(DxfPoint a, DxfPoint b, List<DxfEntity> texts)
    {
        string? best = null;
        double bestD = double.MaxValue;
        foreach (var t in texts)
        {
            var p = t.Points[0];
            if (!OnExtension(p, a, b, 3.0)) continue;
            string s = t.Text.Trim();
            if (s.Length is 0 or > 4) continue;
            double d = Math.Min(p.Distance2D(a), p.Distance2D(b));
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }
}
