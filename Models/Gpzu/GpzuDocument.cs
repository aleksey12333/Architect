using System.Globalization;
using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models.Gpzu;

/// <summary>
/// Характерная точка в геодезической системе (МСК-66 / система ЕГРН): X — север, Y — восток (метры), как в таблицах ГПЗУ.
/// Номер — строка («1», «4», «н1»): у таблиц со склеенными ячейками номер берётся из своей строки.
/// </summary>
public sealed record GeoPoint(string Number, double X, double Y);

/// <summary>Вид зоны / слоя участка (определяет цвет, штриховку и правило проверки посадки).</summary>
public enum GpzuZoneKind
{
    /// <summary>Место допустимого размещения ОКС (пятно застройки по регламенту).</summary>
    AllowedArea,
    /// <summary>Зарегистрированная охранная зона транспорта (ЗОУИТ 66:00-6.*).</summary>
    TransportZone,
    /// <summary>Подзона приаэродромной территории (ПАТ Кольцово / Арамиль), в т. ч. с ограничением абсолютной высоты.</summary>
    AirfieldSubzone,
    /// <summary>Местная ЗОУИТ из кадастрового слоя топосъёмки (66:41-6.*).</summary>
    LocalZone,
    HeatNetwork,
    GasPipeline,
    PowerCable,
    WaterSupply,
    Sewer,
    Communication
}

/// <summary>Зона с особыми условиями использования территории или коридор охраны сети: контур, ограничения, цвет, источник.</summary>
public sealed class GpzuZone
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Реестровый номер ЗОУИТ («66:00-6.1909»), марка подзоны или слой сети.</summary>
    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
    public GpzuZoneKind Kind { get; init; }

    /// <summary>Номер по экспликации чертежа ГПЗУ (0 — нет).</summary>
    public int ExplicationNo { get; init; }

    /// <summary>Текст ограничений (раздел 5 ГПЗУ или норматив охранной зоны сети).</summary>
    public string Restriction { get; init; } = string.Empty;

    /// <summary>Ограничение абсолютной высоты объектов, м БС (null — нет): нижняя граница диапазона.</summary>
    public double? HeightLimitMin { get; init; }

    /// <summary>Верхняя граница диапазона ограничения высоты, м БС (= Min, если значение одно).</summary>
    public double? HeightLimitMax { get; init; }

    /// <summary>Контур зоны (замкнутый, без повтора первой точки). Пусто — зона без координат в документе.</summary>
    public List<GeoPoint> Polygon { get; init; } = [];

    /// <summary>Оси трассы сети (для коридоров), каждая — ломаная в МСК.</summary>
    public List<List<GeoPoint>> Lines { get; init; } = [];

    /// <summary>Полуширина охранного коридора сети, м (0 — не коридор).</summary>
    public double CorridorHalfWidth { get; init; }

    /// <summary>Норматив ширины коридора («ПП РФ № 160 — 1 м от крайнего кабеля»).</summary>
    public string Normative { get; init; } = string.Empty;

    /// <summary>Откуда взята зона: «ГПЗУ, раздел 5.1», «ГПЗУ, Приложение № 1, стр. 16», «ИГДИ DWG, слой Теплосети».</summary>
    public string Source { get; init; } = string.Empty;

    public string ColorHex { get; init; } = "#3B82F6";

    /// <summary>Зона покрывает весь участок (координаты совпадают с границей) — на чертеже рисуется смещённым штрихом.</summary>
    public bool CoversWholeParcel { get; init; }

    public bool IsNetwork => Kind is GpzuZoneKind.HeatNetwork or GpzuZoneKind.GasPipeline or GpzuZoneKind.PowerCable or GpzuZoneKind.WaterSupply
        or GpzuZoneKind.Sewer or GpzuZoneKind.Communication;

    public string HeightText(CultureInfo culture) => HeightLimitMin is not { } min ? string.Empty
        : HeightLimitMax is { } max && Math.Abs(max - min) > 0.005
            ? string.Create(culture, $"Hабс {min:0.00}…{max:0.00} м БС")
            : string.Create(culture, $"Hабс {min:0.00} м БС");
}

/// <summary>Объект капитального строительства на участке (раздел 3.1 ГПЗУ) — номер по чертежу, кадастровый номер, контур из топосъёмки.</summary>
public sealed class GpzuCapitalObject
{
    public int Number { get; init; }
    public string Name { get; init; } = "Объект капитального строительства";
    public string Cadastral { get; init; } = string.Empty;

    /// <summary>Контур здания из топосъёмки (может отсутствовать — тогда объект только в ведомости).</summary>
    public List<GeoPoint> Footprint { get; set; } = [];

    /// <summary>Высота объёма для 3D, м (оценка: по этажности из топосъёмки, иначе 6 м).</summary>
    public double Height { get; set; } = 6;
}

/// <summary>Градостроительный регламент (раздел 2 ГПЗУ): территориальная зона, подзона, предельные параметры.</summary>
public sealed class GpzuRegulation
{
    public string ZoneCode { get; init; } = string.Empty;
    public string ZoneName { get; init; } = string.Empty;
    public string Subzone { get; init; } = string.Empty;
    public double? MaxLivingArea { get; init; }
    public int? MinParkingPlaces { get; init; }
    public double? MinParcelArea { get; init; }
    public double? MaxBuildingPercent { get; init; }
    public int? MaxFloors { get; init; }
    public double? MaxHeight { get; init; }
    public List<string> PermittedUses { get; init; } = [];
    public string Act { get; init; } = string.Empty;
}

/// <summary>Распознанная таблица координат (для отчёта о разборе: имя, страницы, число точек, совпадение с границей).</summary>
public sealed record GpzuCoordinateTable(string Title, int FirstPage, int LastPage, int PointCount, bool SameAsBoundary, bool HadMergedCells);

/// <summary>
/// Градостроительный план земельного участка: реквизиты, граница (характерные точки МСК), регламент, ЗОУИТ и подзоны ПАТ,
/// объекты капитального строительства. Источник — PDF ГПЗУ (текстовый слой), XML/JSON/TXT/CSV с координатами.
/// </summary>
public sealed class GpzuDocument
{
    public string Number { get; set; } = string.Empty;
    public string IssueDate { get; set; } = string.Empty;
    public string Applicant { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Cadastral { get; set; } = string.Empty;

    /// <summary>Площадь участка по документу, м².</summary>
    public double DeclaredArea { get; set; }

    public string CoordinateSystem { get; set; } = "МСК-66";
    public List<GeoPoint> Boundary { get; set; } = [];
    public GpzuRegulation Regulation { get; set; } = new();
    public List<GpzuZone> Zones { get; set; } = [];
    public List<GpzuCapitalObject> Objects { get; set; } = [];
    public List<GpzuCoordinateTable> Tables { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    /// <summary>Отметка земли участка, м БС (из топосъёмки; null — неизвестна).</summary>
    public double? GroundElevation { get; set; }

    public string SourceFile { get; set; } = string.Empty;
    public int PageCount { get; set; }

    [JsonIgnore] public double GaussArea => Geodesy.Area(Boundary);

    /// <summary>Центроид контура (центр масс многоугольника) — начало координат сцены.</summary>
    [JsonIgnore] public (double X, double Y) Centroid => Geodesy.Centroid(Boundary);

    /// <summary>Наименьшая ограниченная абсолютная высота по всем зонам, м БС.</summary>
    [JsonIgnore]
    public double? CeilingAbs
    {
        get
        {
            var limits = Zones.Where(z => z.HeightLimitMin.HasValue).Select(z => z.HeightLimitMin!.Value).ToList();
            return limits.Count == 0 ? null : limits.Min();
        }
    }

    /// <summary>Зона, задающая наименьший потолок высоты (null — ограничений нет).</summary>
    [JsonIgnore] public GpzuZone? CeilingZone => Zones.Where(z => z.HeightLimitMin.HasValue).OrderBy(z => z.HeightLimitMin).FirstOrDefault();
}

/// <summary>Плоская геодезия контура: площадь Гаусса, центроид, длины сторон, дирекционные углы, попадание точки.</summary>
public static class Geodesy
{
    /// <summary>Площадь многоугольника по формуле Гаусса (трапеций), м², без знака.</summary>
    public static double Area(IReadOnlyList<GeoPoint> p) => Math.Abs(SignedArea(p));

    public static double SignedArea(IReadOnlyList<GeoPoint> p)
    {
        if (p.Count < 3) return 0;
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    public static (double X, double Y) Centroid(IReadOnlyList<GeoPoint> p)
    {
        if (p.Count == 0) return (0, 0);
        double a = SignedArea(p);
        if (Math.Abs(a) < 1e-9) return (p.Average(q => q.X), p.Average(q => q.Y));
        // Относительно первой точки — без потери точности на семизначных координатах.
        double x0 = p[0].X, y0 = p[0].Y, cx = 0, cy = 0, s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            double ax = p[i].X - x0, ay = p[i].Y - y0, bx = p[(i + 1) % p.Count].X - x0, by = p[(i + 1) % p.Count].Y - y0;
            double c = ax * by - bx * ay;
            s += c;
            cx += (ax + bx) * c;
            cy += (ay + by) * c;
        }
        return (x0 + cx / (3 * s), y0 + cy / (3 * s));
    }

    public static double Length(GeoPoint a, GeoPoint b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    /// <summary>Дирекционный угол стороны, градусы от севера по часовой (0…360).</summary>
    public static double Bearing(GeoPoint a, GeoPoint b)
    {
        double deg = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
        return deg < 0 ? deg + 360 : deg;
    }

    /// <summary>Точка внутри контура (чётность пересечений, X — север, Y — восток).</summary>
    public static bool Contains(IReadOnlyList<GeoPoint> poly, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Два контура совпадают (те же вершины в любом начале и направлении, допуск 1 см).</summary>
    public static bool SameRing(IReadOnlyList<GeoPoint> a, IReadOnlyList<GeoPoint> b, double tol = 0.01)
    {
        if (a.Count != b.Count || a.Count == 0) return false;
        int start = -1;
        for (int i = 0; i < b.Count; i++)
            if (Math.Abs(b[i].X - a[0].X) <= tol && Math.Abs(b[i].Y - a[0].Y) <= tol) { start = i; break; }
        if (start < 0) return false;
        bool Match(int dir)
        {
            for (int k = 0; k < a.Count; k++)
            {
                var q = b[((start + dir * k) % b.Count + b.Count) % b.Count];
                if (Math.Abs(q.X - a[k].X) > tol || Math.Abs(q.Y - a[k].Y) > tol) return false;
            }
            return true;
        }
        return Match(1) || Match(-1);
    }
}
