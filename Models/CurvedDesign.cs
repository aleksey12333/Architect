using System.Globalization;

namespace AIArchitectStudio.Models;

/// <summary>
/// Криволинейная и непрямоугольная геометрия элементов (волна 4): общие параметры с постоянными Guid — прогиб дуги стены, витража и
/// арочной балки («ArcBulge», стрела дуги по локальной +Z, у балки — вверх), биссектрисная стыковка торцов стен под произвольным углом
/// (углы реза начала и конца), скругление углов контура плит и кровель (радиус; контур запекается в Polygon2D, чтобы расчёт, смета,
/// экспорт и тени видели тот же контур), арочный верх витража (стрела подъёма верхней кромки), цвет фасада с фотографии.
/// Без параметров (эталонные сцены) все функции возвращают прежние значения: одна проверка UserParameters на null.
/// </summary>
public static class CurvedDesign
{
    public static readonly Guid ArcBulgeGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6ea1");
    public static readonly Guid MiterStartGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6ea2");
    public static readonly Guid MiterEndGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6ea3");
    public static readonly Guid CornerRadiusGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6ea4");
    public static readonly Guid FacadeColorGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6ea5");
    public static readonly Guid ArchRiseGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6ea6");
    public static readonly Guid BaseContourGuid = new("8a3e5d21-4b6c-4f0e-9d17-3c2b1a0f6ea7");

    public const string Group = "Форма (криволинейная геометрия)";

    /// <summary>Наибольший угол реза торца, градусы (острее — торец вырождается в клин).</summary>
    public const double MaxMiterDegrees = 80;

    private static class Definitions
    {
        public static readonly ParameterDefinition ArcBulge = StructuralRole.Register(new ParameterDefinition
        {
            Guid = ArcBulgeGuid, Name = "Стрела дуги", SpecType = ParameterSpecType.Length, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition MiterStart = StructuralRole.Register(new ParameterDefinition
        {
            Guid = MiterStartGuid, Name = "Угол реза начала", SpecType = ParameterSpecType.Angle, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition MiterEnd = StructuralRole.Register(new ParameterDefinition
        {
            Guid = MiterEndGuid, Name = "Угол реза конца", SpecType = ParameterSpecType.Angle, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition CornerRadius = StructuralRole.Register(new ParameterDefinition
        {
            Guid = CornerRadiusGuid, Name = "Радиус скругления углов", SpecType = ParameterSpecType.Length, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition FacadeColor = StructuralRole.Register(new ParameterDefinition
        {
            Guid = FacadeColorGuid, Name = "Цвет фасада", SpecType = ParameterSpecType.Text, StorageType = ParameterStorageType.String, Group = Group
        });

        public static readonly ParameterDefinition ArchRise = StructuralRole.Register(new ParameterDefinition
        {
            Guid = ArchRiseGuid, Name = "Стрела арочного верха", SpecType = ParameterSpecType.Length, StorageType = ParameterStorageType.Double, Group = Group
        });

        public static readonly ParameterDefinition BaseContour = StructuralRole.Register(new ParameterDefinition
        {
            Guid = BaseContourGuid, Name = "Исходный контур", SpecType = ParameterSpecType.Text, StorageType = ParameterStorageType.String, Group = Group
        });
    }

    // ───────────────────────────── Чтение ─────────────────────────────

    private static double Read(ArchitecturalElement e, Guid guid)
    {
        if (e.UserParameters is null) return 0;
        var v = e.GetUserParameter(guid);
        if (v.IsEmpty) return 0;
        double d = v.AsDouble();
        return double.IsFinite(d) ? d : 0;
    }

    /// <summary>Стрела дуги, м (знак — сторона прогиба по локальной +Z; у балки — вверх). Не больше 0,49 хорды.</summary>
    public static double ArcBulge(ArchitecturalElement e)
    {
        double s = Read(e, ArcBulgeGuid);
        if (Math.Abs(s) < 1e-3) return 0;
        double limit = Math.Max(0.01, e.Width) * 0.49;
        return Math.Clamp(s, -limit, limit);
    }

    public static bool IsArc(ArchitecturalElement e) => e.UserParameters is not null && ArcBulge(e) != 0;

    /// <summary>Углы реза торцов (градусы): торец начала — x = −W/2 + z·tg(αн), конца — x = +W/2 + z·tg(αк).</summary>
    public static (double Start, double End) Miter(ArchitecturalElement e) =>
        e.UserParameters is null ? (0, 0) : (Math.Clamp(Read(e, MiterStartGuid), -MaxMiterDegrees, MaxMiterDegrees), Math.Clamp(Read(e, MiterEndGuid), -MaxMiterDegrees, MaxMiterDegrees));

    public static bool HasMiter(ArchitecturalElement e)
    {
        if (e.UserParameters is null) return false;
        var (a, b) = Miter(e);
        return Math.Abs(a) > 0.01 || Math.Abs(b) > 0.01;
    }

    public static double CornerRadius(ArchitecturalElement e) => Math.Max(0, Read(e, CornerRadiusGuid));

    public static double ArchRise(ArchitecturalElement e) => Math.Clamp(Read(e, ArchRiseGuid), 0, Math.Max(0, e.Height - 0.1));

    /// <summary>Цвет фасада «#RRGGBB» (с фотографии) или null.</summary>
    public static string? FacadeColor(ArchitecturalElement e)
    {
        if (e.UserParameters is null) return null;
        var v = e.GetUserParameter(FacadeColorGuid);
        if (v.IsEmpty) return null;
        string hex = (v.AsString() ?? string.Empty).Trim();
        return TryParseHex(hex, out _) ? hex : null;
    }

    /// <summary>Элемент несёт криволинейную форму (дуга, рез торцов, арочный верх).</summary>
    public static bool HasShape(ArchitecturalElement e) => e.UserParameters is not null && (IsArc(e) || HasMiter(e) || ArchRise(e) > 0.01);

    /// <summary>Отношение длины оси к хорде: 1 — прямая; у дуги — L_дуги / W (объём стены, смета, свойства Renga).</summary>
    public static double LengthFactor(ArchitecturalElement e)
    {
        if (e.UserParameters is null) return 1;
        double s = ArcBulge(e);
        if (s == 0) return 1;
        var arc = new ArcGeometry(Math.Max(0.01, e.Width), s);
        return arc.Length / arc.Chord;
    }

    // ───────────────────────────── Запись ─────────────────────────────

    public static void SetArc(ArchitecturalElement e, double bulge)
    {
        if (Math.Abs(bulge) < 1e-3) e.RemoveUserParameter(ArcBulgeGuid);
        else e.SetUserParameter(Definitions.ArcBulge, ParameterValue.FromDouble(bulge));
    }

    public static void SetMiter(ArchitecturalElement e, double startDeg, double endDeg)
    {
        if (Math.Abs(startDeg) > 0.01) e.SetUserParameter(Definitions.MiterStart, ParameterValue.FromDouble(Math.Clamp(startDeg, -MaxMiterDegrees, MaxMiterDegrees)));
        else e.RemoveUserParameter(MiterStartGuid);
        if (Math.Abs(endDeg) > 0.01) e.SetUserParameter(Definitions.MiterEnd, ParameterValue.FromDouble(Math.Clamp(endDeg, -MaxMiterDegrees, MaxMiterDegrees)));
        else e.RemoveUserParameter(MiterEndGuid);
    }

    public static void SetArchRise(ArchitecturalElement e, double rise)
    {
        if (rise < 0.01) e.RemoveUserParameter(ArchRiseGuid);
        else e.SetUserParameter(Definitions.ArchRise, ParameterValue.FromDouble(rise));
    }

    public static void SetFacadeColor(ArchitecturalElement e, string? hex)
    {
        if (hex is null || !TryParseHex(hex, out _)) e.RemoveUserParameter(FacadeColorGuid);
        else e.SetUserParameter(Definitions.FacadeColor, ParameterValue.FromString(hex.Trim().ToUpperInvariant()));
    }

    /// <summary>
    /// Скругление углов контура радиусом r (м): исходный контур (Polygon2D или прямоугольник W × D) сохраняется в «Исходный контур»,
    /// в Polygon2D запекается контур с дугами (шаг ≤ 7,5°). Повторный вызов скругляет исходный контур, а не уже скруглённый; r = 0 —
    /// возврат исходного контура. Центр, W × D и отметки не меняются.
    /// </summary>
    public static void ApplyCornerRadius(ArchitecturalElement e, double radius)
    {
        var stored = ReadBaseContour(e);
        var baseContour = stored ?? (e.Polygon2D.Count >= 3
            ? e.Polygon2D.Select(p => (p.X, p.Z)).ToList()
            : [(-e.Width / 2, -e.Depth / 2), (e.Width / 2, -e.Depth / 2), (e.Width / 2, e.Depth / 2), (-e.Width / 2, e.Depth / 2)]);
        if (radius < 0.01)
        {
            e.RemoveUserParameter(CornerRadiusGuid);
            if (stored is not null)
            {
                e.Polygon2D = stored.Select(p => new Point2D(p.X, p.Z)).ToList();
                e.RemoveUserParameter(BaseContourGuid);
            }
            return;
        }
        if (stored is null) e.SetUserParameter(Definitions.BaseContour, ParameterValue.FromString(FormatContour(baseContour)));
        e.SetUserParameter(Definitions.CornerRadius, ParameterValue.FromDouble(radius));
        var path = ContourPath.Fillet(baseContour, radius);
        e.Polygon2D = ContourPath.Sample(path).Select(p => new Point2D(p.X, p.Z)).ToList();
    }

    private static List<(double X, double Z)>? ReadBaseContour(ArchitecturalElement e)
    {
        if (e.UserParameters is null) return null;
        var v = e.GetUserParameter(BaseContourGuid);
        if (v.IsEmpty) return null;
        var pts = new List<(double X, double Z)>();
        foreach (var token in (v.AsString() ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = token.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                pts.Add((x, z));
        }
        return pts.Count >= 3 ? pts : null;
    }

    private static string FormatContour(IEnumerable<(double X, double Z)> pts) =>
        string.Join(";", pts.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:R} {p.Z:R}")));

    // ───────────────────────────── Цвет ─────────────────────────────

    public static bool TryParseHex(string? hex, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var t = hex.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        rgb = ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static string Hex(byte r, byte g, byte b) => string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

    /// <summary>Расстояние цветов в RGB (0…441).</summary>
    public static double ColorDistance(string a, string b)
    {
        if (!TryParseHex(a, out var x) || !TryParseHex(b, out var y)) return double.MaxValue;
        double dr = x.R - y.R, dg = x.G - y.G, db = x.B - y.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }
}

/// <summary>
/// Дуга окружности по хорде c и стреле s (знак — сторона прогиба по локальной +Z): радиус R = (c²/4 + s²) / (2|s|), полуугол Φ
/// (sin Φ = c / 2R), центр на оси Z (zc = s − sign(s)·R). Параметр φ ∈ [−Φ, Φ]: точка средней линии (R sin φ, zc + sign·R cos φ).
/// </summary>
public readonly record struct ArcGeometry(double Chord, double Sagitta)
{
    public double Sign => Sagitta >= 0 ? 1 : -1;
    public double Radius => (Chord * Chord / 4 + Sagitta * Sagitta) / (2 * Math.Max(1e-9, Math.Abs(Sagitta)));
    public double CenterZ => Sagitta - Sign * Radius;

    /// <summary>Полуугол дуги, рад (стрела больше половины хорды — дуга больше полуокружности).</summary>
    public double HalfAngle
    {
        get
        {
            double r = Radius;
            double half = Math.Asin(Math.Clamp(Chord / 2 / r, -1, 1));
            return Math.Abs(Sagitta) > Chord / 2 ? Math.PI - half : half;
        }
    }

    public double Length => 2 * HalfAngle * Radius;

    /// <summary>Угол φ для хордовой координаты x (−c/2…c/2).</summary>
    public double AngleAt(double x) => Math.Asin(Math.Clamp(x / Radius, -1, 1));

    /// <summary>Точка дуги с радиальным смещением offset (наружу от центра, м).</summary>
    public (double X, double Z) Point(double phi, double offset = 0)
    {
        double r = Radius + offset;
        return (r * Math.Sin(phi), CenterZ + Sign * r * Math.Cos(phi));
    }

    /// <summary>Наружная (от центра) нормаль средней линии в точке φ.</summary>
    public (double X, double Z) Normal(double phi) => (Math.Sin(phi), Sign * Math.Cos(phi));

    /// <summary>Смещение дуги от хорды по Z в хордовой координате x.</summary>
    public double ZAt(double x) => Point(AngleAt(x)).Z;

    /// <summary>Угол касательной к хорде (градусы, в соглашении RotationY: локальная X → (cos, −sin)).</summary>
    public double TangentDegrees(double x)
    {
        double phi = AngleAt(x);
        // Касательная — производная точки по φ: (cos φ, −sign·sin φ).
        double tx = Math.Cos(phi), tz = -Sign * Math.Sin(phi);
        return Math.Atan2(-tz, tx) * 180 / Math.PI;
    }
}

/// <summary>Отрезок контура: от (X1, Z1) до (X2, Z2), стрела дуги Bulge &gt; 0 — наружу (вправо по ходу обхода против часовой).</summary>
public readonly record struct PathSegment(double X1, double Z1, double X2, double Z2, double Bulge)
{
    public double Chord => Math.Sqrt((X2 - X1) * (X2 - X1) + (Z2 - Z1) * (Z2 - Z1));
    public bool IsArc => Math.Abs(Bulge) > 1e-4;
}

/// <summary>
/// Контуры из прямых и дуг: прямоугольник со скруглёнными углами, окружность, многоугольник со скруглением вершин (филе), дискретизация
/// в многоугольник Polygon2D и стены/витражи по участкам контура (центр — середина хорды, поворот — по хорде, «Стрела дуги» — по знаку
/// локальной +Z).
/// </summary>
public static class ContourPath
{
    /// <summary>Шаг дискретизации дуги, градусы.</summary>
    public const double SampleStepDegrees = 7.5;

    public static List<PathSegment> Polygon(IReadOnlyList<(double X, double Z)> points)
    {
        var ccw = SignedArea(points) >= 0 ? points.ToList() : points.Reverse().ToList();
        var path = new List<PathSegment>(ccw.Count);
        for (int i = 0; i < ccw.Count; i++)
        {
            var a = ccw[i];
            var b = ccw[(i + 1) % ccw.Count];
            path.Add(new PathSegment(a.X, a.Z, b.X, b.Z, 0));
        }
        return path;
    }

    /// <summary>Прямоугольник W × D (центр в нуле) со скруглением углов r.</summary>
    public static List<PathSegment> RoundedRect(double w, double d, double r) =>
        Fillet([(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)], r);

    /// <summary>Окружность радиуса r из n дуг (центр cx, cz).</summary>
    public static List<PathSegment> Circle(double r, int arcs = 8, double cx = 0, double cz = 0, double phase = 0)
    {
        var path = new List<PathSegment>(arcs);
        double sag = r * (1 - Math.Cos(Math.PI / arcs));
        for (int i = 0; i < arcs; i++)
        {
            double a0 = phase + 2 * Math.PI * i / arcs, a1 = phase + 2 * Math.PI * (i + 1) / arcs;
            // Обход против часовой в осях (X, Z): угол растёт от X к Z.
            path.Add(new PathSegment(cx + r * Math.Cos(a0), cz + r * Math.Sin(a0), cx + r * Math.Cos(a1), cz + r * Math.Sin(a1), sag));
        }
        return path;
    }

    /// <summary>
    /// Многоугольник со скруглением вершин радиусом r: в каждой вершине — дуга, касательная к обоим рёбрам (у выпуклой вершины
    /// стрела наружу, у входящей — внутрь); радиус уменьшается, если ребро короче суммы касательных отрезков.
    /// </summary>
    public static List<PathSegment> Fillet(IReadOnlyList<(double X, double Z)> points, double r)
    {
        var p = SignedArea(points) >= 0 ? points.ToList() : points.Reverse().ToList();
        int n = p.Count;
        if (r <= 1e-4 || n < 3) return Polygon(p);
        var tangentIn = new (double X, double Z)[n];
        var tangentOut = new (double X, double Z)[n];
        var bulge = new double[n];
        var hasArc = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var prev = p[(i - 1 + n) % n];
            var cur = p[i];
            var next = p[(i + 1) % n];
            double l1 = Dist(prev, cur), l2 = Dist(cur, next);
            if (l1 < 1e-6 || l2 < 1e-6)
            {
                tangentIn[i] = tangentOut[i] = cur;
                continue;
            }
            var d1 = ((cur.X - prev.X) / l1, (cur.Z - prev.Z) / l1);
            var d2 = ((next.X - cur.X) / l2, (next.Z - cur.Z) / l2);
            double cos = Math.Clamp(d1.Item1 * d2.Item1 + d1.Item2 * d2.Item2, -1, 1);
            double turn = Math.Acos(cos); // угол поворота направления
            double cross = d1.Item1 * d2.Item2 - d1.Item2 * d2.Item1;
            if (turn < 1e-3 || turn > Math.PI - 1e-3)
            {
                tangentIn[i] = tangentOut[i] = cur;
                continue;
            }
            double rr = r;
            double t = rr * Math.Tan(turn / 2);
            double limit = Math.Min(l1, l2) * 0.49;
            if (t > limit)
            {
                t = limit;
                rr = t / Math.Tan(turn / 2);
            }
            tangentIn[i] = (cur.X - d1.Item1 * t, cur.Z - d1.Item2 * t);
            tangentOut[i] = (cur.X + d2.Item1 * t, cur.Z + d2.Item2 * t);
            double sag = rr * (1 - Math.Cos(turn / 2));
            bulge[i] = cross > 0 ? sag : -sag;
            hasArc[i] = true;
        }
        var path = new List<PathSegment>(2 * n);
        for (int i = 0; i < n; i++)
        {
            if (hasArc[i]) path.Add(new PathSegment(tangentIn[i].X, tangentIn[i].Z, tangentOut[i].X, tangentOut[i].Z, bulge[i]));
            int j = (i + 1) % n;
            var a = tangentOut[i];
            var b = tangentIn[j];
            if (Dist(a, b) > 1e-6) path.Add(new PathSegment(a.X, a.Z, b.X, b.Z, 0));
        }
        return path;
    }

    /// <summary>Многоугольник по контуру (дуги — с шагом ≤ 7,5°), обход против часовой.</summary>
    public static List<(double X, double Z)> Sample(IReadOnlyList<PathSegment> path, double stepDegrees = SampleStepDegrees)
    {
        var pts = new List<(double X, double Z)>();
        foreach (var s in path)
        {
            pts.Add((s.X1, s.Z1));
            if (!s.IsArc) continue;
            var arc = new ArcGeometry(s.Chord, s.Bulge);
            double phi = arc.HalfAngle;
            int n = Math.Max(2, (int)Math.Ceiling(2 * phi * 180 / Math.PI / stepDegrees));
            for (int k = 1; k < n; k++)
            {
                double a = -phi + 2 * phi * k / n;
                pts.Add(ToWorld(s, arc.Point(a)));
            }
        }
        // Удаление совпадающих точек (стык дуги и прямой).
        var clean = new List<(double X, double Z)>(pts.Count);
        foreach (var q in pts)
            if (clean.Count == 0 || Dist(clean[^1], q) > 1e-6) clean.Add(q);
        if (clean.Count > 1 && Dist(clean[0], clean[^1]) < 1e-6) clean.RemoveAt(clean.Count - 1);
        return clean;
    }

    /// <summary>
    /// Точка в локальной системе отрезка (x вдоль хорды от середины, z — по стреле наружу) в мировые координаты.
    /// Наружная нормаль хорды при обходе против часовой — (dz, −dx).
    /// </summary>
    public static (double X, double Z) ToWorld(PathSegment s, (double X, double Z) local)
    {
        double c = Math.Max(1e-9, s.Chord);
        double ux = (s.X2 - s.X1) / c, uz = (s.Z2 - s.Z1) / c;
        double nx = uz, nz = -ux;
        double mx = (s.X1 + s.X2) / 2, mz = (s.Z1 + s.Z2) / 2;
        return (mx + ux * local.X + nx * local.Z, mz + uz * local.X + nz * local.Z);
    }

    /// <summary>Поворот RotationY элемента вдоль хорды отрезка (локальная X → направление обхода).</summary>
    public static double RotationOf(PathSegment s) => Math.Atan2(-(s.Z2 - s.Z1), s.X2 - s.X1) * 180 / Math.PI;

    /// <summary>
    /// Стена или витраж по участку контура: центр — середина хорды (+ смещение по нормали внутрь на inset), длина — хорда,
    /// у дуги — «Стрела дуги» по локальной +Z (локальная +Z = −наружная нормаль, поэтому знак меняется).
    /// </summary>
    public static ArchitecturalElement Element(PathSegment s, string type, string name, double baseY, double height, double thickness, string group,
        int level, string material, double inset = 0)
    {
        double c = s.Chord;
        double ux = (s.X2 - s.X1) / Math.Max(1e-9, c), uz = (s.Z2 - s.Z1) / Math.Max(1e-9, c);
        double nx = uz, nz = -ux; // наружу
        double mx = (s.X1 + s.X2) / 2 - nx * inset, mz = (s.Z1 + s.Z2) / 2 - nz * inset;
        double chord = c, bulge = s.Bulge;
        if (s.IsArc && Math.Abs(inset) > 1e-9)
        {
            // Параллельная дуга внутрь: радиус R − inset при том же угле (хорда и стрела масштабируются).
            var arc = new ArcGeometry(c, s.Bulge);
            double k = (arc.Radius - arc.Sign * inset) / arc.Radius;
            // Середина параллельной дуги: вершина смещается на inset внутрь, хорда — на inset − s(1 − k).
            chord = c * k;
            bulge = s.Bulge * k;
            double apexShift = inset;
            mx = (s.X1 + s.X2) / 2 + nx * (s.Bulge - apexShift - bulge);
            mz = (s.Z1 + s.Z2) / 2 + nz * (s.Bulge - apexShift - bulge);
        }
        var e = new ArchitecturalElement { Type = type, Name = name, BuildingGroupId = group, Material = material, LevelIndex = level };
        e.SetSize(Math.Max(0.1, chord), height, thickness);
        e.SetPosition(mx, baseY + height / 2, mz);
        e.SetRotationY(RotationOf(s));
        if (s.IsArc) CurvedDesign.SetArc(e, -Math.Round(bulge, 3));
        return e;
    }

    private static double Dist((double X, double Z) a, (double X, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Ориентированная площадь (против часовой в осях X, Z — положительна).</summary>
    public static double SignedArea(IReadOnlyList<(double X, double Z)> poly)
    {
        double sum = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            sum += a.X * b.Z - b.X * a.Z;
        }
        return sum / 2;
    }
}
