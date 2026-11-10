namespace AIArchitectStudio.Models.Gpzu;

/// <summary>Точка плана сцены (метры): X — восток, Z — юг (север плана — −Z, как у Солнца и осей).</summary>
public readonly record struct SitePoint(double X, double Z);

/// <summary>Сторона границы участка: номера точек, длина, середина (для подписи) и дирекционный угол.</summary>
public sealed record SiteSegment(string From, string To, double Length, SitePoint Mid, double Bearing);

/// <summary>Зона в координатах сцены: исходная зона ГПЗУ/топосъёмки, контур, оси трассы, полоса коридора по сегментам.</summary>
public sealed record SiteZone(GpzuZone Zone, IReadOnlyList<SitePoint> Polygon, IReadOnlyList<IReadOnlyList<SitePoint>> Lines,
    IReadOnlyList<IReadOnlyList<SitePoint>> Corridor, int Order)
{
    public string Id => Zone.Id;
}

/// <summary>Существующий объект (ОКС из ГПЗУ с контуром или здание топосъёмки): номер, подпись, контур, высота.</summary>
public sealed record SiteObject(int Number, string Label, IReadOnlyList<SitePoint> Footprint, double Height);

/// <summary>
/// Участок ГПЗУ, перенесённый в сцену: начало координат — центроид контура (X₀, Y₀) в МСК; восток → +X сцены, север → −Z.
/// Неизменяемый: шаг журнала «Участок ГПЗУ и охранные зоны» хранит ссылку на прежний и новый план.
/// </summary>
public sealed class GpzuSitePlan
{
    public required GpzuDocument Document { get; init; }
    public TopoSurvey? Topo { get; init; }

    /// <summary>Начало координат сцены в МСК (X — север, Y — восток).</summary>
    public double OriginX { get; init; }
    public double OriginY { get; init; }

    public required IReadOnlyList<SitePoint> Boundary { get; init; }
    public required IReadOnlyList<string> PointNumbers { get; init; }
    public required IReadOnlyList<SiteSegment> Segments { get; init; }
    public required IReadOnlyList<SiteZone> Zones { get; init; }
    public required IReadOnlyList<SiteObject> Existing { get; init; }

    /// <summary>Отметка земли участка, м БС (0 — неизвестна).</summary>
    public double GroundElevation { get; init; }

    /// <summary>Высота плоскости ограничения над землёй, м (Hабс − отметка земли); null — ограничения нет или отметка неизвестна.</summary>
    public double? CeilingHeight { get; init; }

    /// <summary>Абсолютная отметка потолка, м БС.</summary>
    public double? CeilingAbs { get; init; }

    public double Area => Document.GaussArea;

    // ── Волна 6: живая земля проекта ──

    /// <summary>Отметки поворотных точек границы, м БС (по индексу точки; пусто или 0 — по рельефу топосъёмки / земле участка).</summary>
    public IReadOnlyList<double> VertexElevations { get; init; } = [];

    /// <summary>Высотные отметки рельефа в координатах сцены (топосъёмка в границах интереса).</summary>
    public IReadOnlyList<TerrainSample> Relief { get; init; } = [];

    /// <summary>Подтверждённые пользователем ограничения и «НО» документа / норм (действуют в проверке посадки и на участке).</summary>
    public IReadOnlyList<ProjectConstraintItem> Constraints { get; init; } = [];

    /// <summary>Толщина грунтового основания платформы участка, м (подземные сети — в его толще).</summary>
    public double SoilThickness { get; init; } = 3.0;

    /// <summary>Отметка поворотной точки i, м БС (0 — не задана).</summary>
    public double VertexElevation(int i) => i >= 0 && i < VertexElevations.Count ? VertexElevations[i] : 0;

    /// <summary>Рельеф участка: отметки топосъёмки + заданные отметки поворотных точек (площадки зданий — у вызывающего).</summary>
    public SiteTerrain Terrain(IReadOnlyList<TerrainPad>? pads = null)
    {
        var samples = new List<TerrainSample>(Relief);
        for (int i = 0; i < Boundary.Count; i++)
            if (VertexElevation(i) > 0) samples.Add(new TerrainSample(Boundary[i].X, Boundary[i].Z, VertexElevation(i)));
        return new SiteTerrain(samples, GroundElevation, pads);
    }

    /// <summary>Копия плана с заменой отдельных частей (шаг журнала хранит прежний и новый планы — оба неизменяемы).</summary>
    public GpzuSitePlan With(GpzuDocument? document = null, IReadOnlyList<SitePoint>? boundary = null, IReadOnlyList<string>? numbers = null,
        IReadOnlyList<SiteSegment>? segments = null, IReadOnlyList<SiteZone>? zones = null, IReadOnlyList<SiteObject>? existing = null,
        IReadOnlyList<double>? vertexElevations = null, IReadOnlyList<ProjectConstraintItem>? constraints = null, IReadOnlyList<TerrainSample>? relief = null) => new()
    {
        Document = document ?? Document,
        Topo = Topo,
        OriginX = OriginX,
        OriginY = OriginY,
        Boundary = boundary ?? Boundary,
        PointNumbers = numbers ?? PointNumbers,
        Segments = segments ?? Segments,
        Zones = zones ?? Zones,
        Existing = existing ?? Existing,
        GroundElevation = GroundElevation,
        CeilingHeight = CeilingHeight,
        CeilingAbs = CeilingAbs,
        VertexElevations = vertexElevations ?? VertexElevations,
        Relief = relief ?? Relief,
        Constraints = constraints ?? Constraints,
        SoilThickness = SoilThickness
    };

    /// <summary>Габарит контура в сцене.</summary>
    public (double MinX, double MaxX, double MinZ, double MaxZ) Bounds =>
        (Boundary.Min(p => p.X), Boundary.Max(p => p.X), Boundary.Min(p => p.Z), Boundary.Max(p => p.Z));

    /// <summary>Перевод точки МСК в сцену.</summary>
    public SitePoint ToScene(double x, double y) => new(y - OriginY, -(x - OriginX));

    /// <summary>Обратный перевод: сцена → МСК (X — север, Y — восток).</summary>
    public (double X, double Y) ToGeo(double sceneX, double sceneZ) => (OriginX - sceneZ, OriginY + sceneX);
}
