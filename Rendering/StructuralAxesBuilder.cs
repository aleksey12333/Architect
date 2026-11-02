using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using System.Numerics;

namespace AIArchitectStudio.Rendering;

/// <summary>Строительная координационная ось: подпись и отрезок на отметке этажа.</summary>
public sealed record GridAxis(string Label, Vector3 Start, Vector3 End, Vector3 LabelStart, Vector3 LabelEnd);

/// <summary>Размер между соседними осями (мм).</summary>
public sealed record GridDimension(string Text, Vector3 Position);

/// <summary>Ось постройки в плане: цифровая (x = Coordinate) или буквенная (z = Coordinate), с границами отрезка.</summary>
public sealed record PlanAxis(string Label, bool IsNumeric, double Coordinate, double From, double To);

/// <summary>Отметка этажа постройки (низ несущих конструкций этажа или верх перекрытия под ним).</summary>
public sealed record LevelMark(int Level, double Elevation);

/// <summary>Сетка осей одной постройки: оси в плане, отметки всех этажей и размеры между осями.</summary>
public sealed class BuildingGrid
{
    public required string GroupId { get; init; }
    public required List<PlanAxis> Axes { get; init; }
    public required List<LevelMark> Levels { get; init; }
    public bool IsExplicit { get; init; }
    public double MinX { get; init; }
    public double MaxX { get; init; }
    public double MinZ { get; init; }
    public double MaxZ { get; init; }
    public double TopY { get; init; }

    public IEnumerable<PlanAxis> NumericAxes => Axes.Where(a => a.IsNumeric).OrderBy(a => a.Coordinate);

    /// <summary>Буквенные оси: «А» — внизу плана (наибольшая Z), далее вверх.</summary>
    public IEnumerable<PlanAxis> LetterAxes => Axes.Where(a => !a.IsNumeric).OrderByDescending(a => a.Coordinate);

    /// <summary>Оси на отметке elevation (с пузырьками марок на обоих концах).</summary>
    public List<GridAxis> AxesAt(float elevation)
    {
        const float bubble = 1.3f;
        var list = new List<GridAxis>();
        foreach (var a in Axes)
        {
            float c = (float)a.Coordinate, f = (float)a.From, t = (float)a.To;
            list.Add(a.IsNumeric
                ? new GridAxis(a.Label, new Vector3(c, elevation, f), new Vector3(c, elevation, t), new Vector3(c, elevation, f - bubble), new Vector3(c, elevation, t + bubble))
                : new GridAxis(a.Label, new Vector3(f, elevation, c), new Vector3(t, elevation, c), new Vector3(f - bubble, elevation, c), new Vector3(t + bubble, elevation, c)));
        }
        return list;
    }

    /// <summary>Размеры между соседними осями на отметке elevation (мм).</summary>
    public List<GridDimension> DimensionsAt(float elevation)
    {
        var dims = new List<GridDimension>();
        var nums = NumericAxes.ToList();
        for (int i = 1; i < nums.Count; i++)
            dims.Add(new GridDimension(StructuralAxesBuilder.Millimeters(nums[i].Coordinate - nums[i - 1].Coordinate),
                new Vector3((float)(nums[i].Coordinate + nums[i - 1].Coordinate) / 2, elevation, (float)nums[i].To + 0.35f)));
        var letters = LetterAxes.ToList();
        for (int i = 1; i < letters.Count; i++)
            dims.Add(new GridDimension(StructuralAxesBuilder.Millimeters(letters[i - 1].Coordinate - letters[i].Coordinate),
                new Vector3((float)letters[i].From + 0.35f, elevation, (float)(letters[i].Coordinate + letters[i - 1].Coordinate) / 2)));
        return dims;
    }
}

/// <summary>
/// Координационные оси построек. Для каждой постройки (BuildingGroupId) берутся явные оси (считанные с чертежа или
/// введённые пользователем), а без них — автоматические по несущим конструкциям: цифровые (1, 2, 3…) слева направо по X,
/// буквенные (А, Б, В…) снизу вверх на плане (от +Z к −Z). Сетка строится на отметке каждого этажа постройки.
/// </summary>
public static class StructuralAxesBuilder
{
    /// <summary>Буквы русского алфавита для осей по ГОСТ 21.101 (без Ё, З, Й, О, Х, Ц, Ч, Щ, Ъ, Ы, Ь).</summary>
    private static readonly string[] Letters =
        ["А", "Б", "В", "Г", "Д", "Е", "Ж", "И", "К", "Л", "М", "Н", "П", "Р", "С", "Т", "У", "Ф", "Ш", "Э", "Ю", "Я"];

    private const double MergeTolerance = 0.3;
    private const int MaxAxesPerDirection = 40;
    private const double Margin = 2.5;

    /// <summary>Сетки осей всех построек с отметками их этажей.</summary>
    public static List<BuildingGrid> BuildGrids(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<StructuralGridAxis> explicitAxes)
    {
        var grids = new List<BuildingGrid>();
        var groupIds = elements.Select(e => e.BuildingGroupId).Concat(explicitAxes.Select(a => a.BuildingGroupId)).Distinct();
        foreach (var gid in groupIds)
        {
            var items = elements.Where(e => e.BuildingGroupId == gid).ToList();
            var manual = explicitAxes.Where(a => a.BuildingGroupId == gid).ToList();
            var axes = manual.Count > 0 ? manual : AutoAxes(items, gid);
            if (axes.Count == 0) continue;
            if (axes.Count(a => a.IsNumeric) > MaxAxesPerDirection || axes.Count(a => !a.IsNumeric) > MaxAxesPerDirection) continue;

            double minX, maxX, minZ, maxZ;
            if (items.Count > 0) (minX, maxX, minZ, maxZ) = SceneStateService.Bounds(items.Where(e => !ElementTypes.IsVoidType(e.Type)));
            else (minX, maxX, minZ, maxZ) = (0, 0, 0, 0);
            foreach (var a in axes)
            {
                if (a.IsNumeric) { minX = Math.Min(minX, a.Coordinate); maxX = Math.Max(maxX, a.Coordinate); }
                else { minZ = Math.Min(minZ, a.Coordinate); maxZ = Math.Max(maxZ, a.Coordinate); }
            }
            double x0 = minX - Margin, x1 = maxX + Margin, z0 = minZ - Margin, z1 = maxZ + Margin;

            grids.Add(new BuildingGrid
            {
                GroupId = gid,
                Axes = axes.Select(a => a.IsNumeric
                    ? new PlanAxis(a.Label, true, a.Coordinate, z0, z1)
                    : new PlanAxis(a.Label, false, a.Coordinate, x0, x1)).ToList(),
                Levels = LevelElevations(items),
                IsExplicit = manual.Count > 0,
                MinX = minX, MaxX = maxX, MinZ = minZ, MaxZ = maxZ,
                TopY = items.Count > 0 ? items.Max(e => e.RepeatLevels > 1 ? e.TopY + e.LevelHeight * (e.RepeatLevels - 1) : e.TopY) : 3
            });
        }
        return grids;
    }

    /// <summary>
    /// Автоматические оси постройки по несущим конструкциям: стены вдоль X/Z и колонны регулярной ортогональной сетки
    /// (заполненность узлов ≥ 40 %; радиальные и свободные схемы осей А-1 не имеют).
    /// </summary>
    public static List<StructuralGridAxis> AutoAxes(IReadOnlyList<ArchitecturalElement> items, string groupId)
    {
        var xs = new List<double>();
        var zs = new List<double>();
        var columnXs = new List<double>();
        var columnZs = new List<double>();
        int columnCount = 0;
        foreach (var e in items)
        {
            switch (e.Type)
            {
                case ElementTypes.Column:
                    columnXs.Add(e.X);
                    columnZs.Add(e.Z);
                    columnCount++;
                    break;
                case ElementTypes.Wall:
                case ElementTypes.CurtainWall:
                    if (e.Polygon2D.Count >= 3) break;
                    double rot = Math.Abs(ArchitecturalElement.NormalizeAngle(e.RotationY));
                    if (rot < 3 || Math.Abs(rot - 180) < 3) zs.Add(e.Z);        // стена вдоль X → буквенная ось
                    else if (Math.Abs(rot - 90) < 3) xs.Add(e.X);               // стена вдоль Z → цифровая ось
                    break;
            }
        }

        if (columnCount > 0)
        {
            int nx = Cluster([.. columnXs]).Count, nz = Cluster([.. columnZs]).Count;
            bool regularGrid = nx * nz <= 4 || columnCount >= 0.4 * nx * nz;
            if (regularGrid)
            {
                xs.AddRange(columnXs);
                zs.AddRange(columnZs);
            }
        }

        var result = new List<StructuralGridAxis>();
        var xAxes = Cluster(xs);
        for (int i = 0; i < xAxes.Count; i++)
            result.Add(new StructuralGridAxis((i + 1).ToString(), StructuralGridAxis.AlongZ, xAxes[i], groupId));
        var zAxes = Cluster(zs).OrderByDescending(z => z).ToList();
        for (int i = 0; i < zAxes.Count; i++)
            result.Add(new StructuralGridAxis(Letter(i), StructuralGridAxis.AlongX, zAxes[i], groupId));
        return result;
    }

    /// <summary>
    /// Отметки этажей постройки: низ стен/колонн/витражей каждого этажа (с учётом RepeatLevels) и верх перекрытий
    /// (отметка следующего этажа, в том числе покрытия над последним этажом).
    /// </summary>
    public static List<LevelMark> LevelElevations(IReadOnlyList<ArchitecturalElement> items)
    {
        var map = new SortedDictionary<int, double>();
        void Put(int level, double y)
        {
            y = Math.Round(y, 3);
            map[level] = map.TryGetValue(level, out var old) ? Math.Min(old, y) : y;
        }

        foreach (var e in items)
        {
            int repeat = Math.Max(1, e.RepeatLevels);
            if (e.Type is ElementTypes.Wall or ElementTypes.CurtainWall or ElementTypes.Column or ElementTypes.Stairs)
                for (int i = 0; i < repeat; i++) Put(e.LevelIndex + i, e.BaseY + e.LevelHeight * i);
        }
        foreach (var e in items)
        {
            int repeat = Math.Max(1, e.RepeatLevels);
            if (e.Type is ElementTypes.Slab or ElementTypes.CantileverSlab || e.IsFlatRoof)
                for (int i = 0; i < repeat; i++)
                {
                    int next = e.LevelIndex + i + 1;
                    if (!map.ContainsKey(next)) map[next] = Math.Round(e.TopY + e.LevelHeight * i, 3);
                }
        }
        if (map.Count == 0)
        {
            var foundation = items.Where(e => e.Type == ElementTypes.Foundation).ToList();
            map[0] = foundation.Count > 0 ? foundation.Max(f => f.TopY) : 0;
        }
        return map.Select(p => new LevelMark(p.Key, p.Value)).ToList();
    }

    /// <summary>
    /// Оси всех построек на отметке первого этажа (для DXF и совместимости): цифровые и буквенные с размерами.
    /// Режим фасада — только цифровые оси вертикальными линиями от земли до верха здания.
    /// </summary>
    public static (List<GridAxis> Axes, List<GridDimension> Dimensions) Build(IReadOnlyList<ArchitecturalElement> elements,
        IReadOnlyList<StructuralGridAxis>? explicitAxes = null, bool elevation = false)
    {
        var axes = new List<GridAxis>();
        var dims = new List<GridDimension>();
        foreach (var grid in BuildGrids(elements, explicitAxes ?? []))
        {
            if (elevation)
            {
                var (a, d) = ElevationAxes(grid);
                axes.AddRange(a);
                dims.AddRange(d);
                continue;
            }
            float y = (float)(grid.Levels.Count > 0 ? grid.Levels[0].Elevation : 0) + 0.02f;
            axes.AddRange(grid.AxesAt(y));
            dims.AddRange(grid.DimensionsAt(y));
        }
        return (axes, dims);
    }

    /// <summary>Фасад (вид спереди): цифровые оси вертикальными линиями от земли до верха постройки, марки — под землёй.</summary>
    public static (List<GridAxis> Axes, List<GridDimension> Dimensions) ElevationAxes(BuildingGrid grid)
    {
        const float bubble = 1.3f, bottom = -1.2f;
        var axes = new List<GridAxis>();
        var dims = new List<GridDimension>();
        float top = (float)grid.TopY + 1.0f;
        float front = (float)grid.MaxZ + 1.0f;
        var nums = grid.NumericAxes.ToList();
        for (int i = 0; i < nums.Count; i++)
        {
            float x = (float)nums[i].Coordinate;
            axes.Add(new GridAxis(nums[i].Label,
                new Vector3(x, bottom, front), new Vector3(x, top, front),
                new Vector3(x, bottom - bubble, front), new Vector3(x, top + bubble, front)));
            if (i > 0)
                dims.Add(new GridDimension(Millimeters(nums[i].Coordinate - nums[i - 1].Coordinate),
                    new Vector3((float)(nums[i].Coordinate + nums[i - 1].Coordinate) / 2, bottom - 0.2f, front)));
        }
        return (axes, dims);
    }

    public static string Letter(int index) =>
        index < Letters.Length ? Letters[index] : Letters[index % Letters.Length] + (index / Letters.Length).ToString();

    public static string Millimeters(double meters) => Math.Round(Math.Abs(meters) * 1000).ToString("0");

    /// <summary>Отметка уровня в формате чертежа: «±0.000», «+3.300», «−1.200».</summary>
    public static string ElevationText(double meters)
    {
        double v = Math.Round(meters, 3);
        if (Math.Abs(v) < 0.0005) return "±0.000";
        return (v > 0 ? "+" : "−") + Math.Abs(v).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Объединяет близкие координаты (допуск 0.3 м) в одну ось.</summary>
    private static List<double> Cluster(List<double> values)
    {
        var result = new List<double>();
        if (values.Count == 0) return result;
        values.Sort();
        double sum = values[0];
        int count = 1;
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] - sum / count <= MergeTolerance)
            {
                sum += values[i];
                count++;
            }
            else
            {
                result.Add(Math.Round(sum / count, 3));
                sum = values[i];
                count = 1;
            }
        }
        result.Add(Math.Round(sum / count, 3));
        return result;
    }
}
