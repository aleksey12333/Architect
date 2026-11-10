using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models;

/// <summary>Параметры рабочей сессии, сохраняемые вместе со сценой в .aia.</summary>
public sealed class SceneProjectParameters
{
    /// <summary>Ключ модели ИИ, с которой велась работа (local:qwen3b, gemini:…, web:…).</summary>
    public string AiModel { get; set; } = string.Empty;
    public bool AppendToSite { get; set; }
    public bool ShowGridAxes { get; set; } = true;
    public bool Wireframe { get; set; }
    public string ThemeName { get; set; } = string.Empty;

    /// <summary>Расценки на момент сохранения (для воспроизводимости сметы).</summary>
    public double ConcretePricePerM3 { get; set; }
    public double GlazingPricePerM2 { get; set; }

    /// <summary>Итоги сметы на момент сохранения (для предпросмотра файла без пересчёта).</summary>
    public double TotalConcreteM3 { get; set; }
    public double TotalGlazingM2 { get; set; }
    public double TotalCostRub { get; set; }

    /// <summary>v4: цена арматуры, итог стали и параметры площадки экспресс-расчёта (снег, ветер, сейсмика, бетон, арматура).</summary>
    public double RebarPricePerTon { get; set; }
    public double TotalSteelKg { get; set; }
    public StructuralLoadSettings? StructuralLoads { get; set; }

    /// <summary>Регион строительства (город): широта, долгота и часовой пояс для Солнца, теней и расчёта инсоляции.</summary>
    public string? SolarRegion { get; set; }

    /// <summary>Мини-смета монолитного каркаса (окно «Ведомость объёмов»): цены проекта восстанавливаются при открытии файла.</summary>
    public ProjectCostEstimate? CostEstimate { get; set; }
}

/// <summary>Смета монолитного каркаса проекта: расценки, объёмы и итог на момент сохранения.</summary>
public sealed class ProjectCostEstimate
{
    /// <summary>Цена бетона, ₽ за м³.</summary>
    public double ConcretePricePerM3 { get; set; }

    /// <summary>Цена арматуры, ₽ за тонну.</summary>
    public double RebarPricePerTon { get; set; }

    public double ConcreteM3 { get; set; }
    public double RebarKg { get; set; }

    /// <summary>Итоговая стоимость монолитного каркаса, ₽: бетон × цена + арматура (т) × цена.</summary>
    public double FrameCostRub { get; set; }

    public DateTime SavedAt { get; set; } = DateTime.Now;

    /// <summary>База цен поставщика, по которой посчитана смета (null — расценки проекта без поставщиков).</summary>
    public string? CatalogName { get; set; }

    public DateTime? CatalogDate { get; set; }

    /// <summary>
    /// Строки сметы с поставщиком и документом-основанием (null — смета по расценкам проекта без поставщиков и ручных правок:
    /// блок не пишется, файл совпадает с прежним форматом).
    /// </summary>
    public List<Economics.CostEstimateLine>? Lines { get; set; }

    public bool IsValid => double.IsFinite(ConcretePricePerM3) && ConcretePricePerM3 >= 0 && double.IsFinite(RebarPricePerTon) && RebarPricePerTon >= 0;

    public ProjectCostEstimate Clone()
    {
        var copy = (ProjectCostEstimate)MemberwiseClone();
        copy.Lines = Lines?.Select(l => l.Clone()).ToList();
        return copy;
    }
}

/// <summary>Файл проекта .aia (JSON): сцена, постройки участка, история чата ИИ и параметры.</summary>
public sealed class SceneProjectFile
{
    public const string Extension = ".aia";

    /// <summary>
    /// v3: координационные оси построек, профили сечений, формы кровли, проёмы wall_opening / slab_opening;
    /// v4: многослойные пироги конструкций (layers) и параметры площадки для экспресс-расчёта;
    /// v5: закреплённые размеры 3D-рулетки (pinnedDimensions);
    /// v6: система параметров — определения общих параметров (parameterDefinitions) и значения пользовательских параметров
    /// элементов (блок «parameters» элемента); поля v5 пишутся как раньше, без пользовательских параметров блоков нет.
    /// </summary>
    public const int CurrentFormatVersion = 6;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string ProjectName { get; set; } = "Новый проект";
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public string AppVersion { get; set; } = "1.0";
    public string ViewMode { get; set; } = "Perspective3D";
    public int ActiveLevelFilter { get; set; } = -1;
    public SceneProjectParameters Parameters { get; set; } = new();
    public List<BuildingGroup> BuildingGroups { get; set; } = [];
    public List<ArchitecturalElement> Elements { get; set; } = [];

    /// <summary>Явные координационные оси построек (с чертежа или из редактора осей).</summary>
    public List<StructuralGridAxis> GridAxes { get; set; } = [];

    /// <summary>Пользовательские размеры, закреплённые рулеткой (v5).</summary>
    public List<PinnedDimension> PinnedDimensions { get; set; } = [];

    /// <summary>
    /// v6: определения пользовательских параметров, значения которых есть у элементов файла (Guid, имя, тип хранения, смысл).
    /// Заполняется при записи (<see cref="SaveAsync"/>); null — значений нет, блок не пишется.
    /// </summary>
    public List<ParameterDefinition>? ParameterDefinitions { get; set; }

    /// <summary>
    /// v6 (фаза 3): этажи построек — Id, имя, постройка, отметка (номер этажа не хранится: это позиция по отметке).
    /// Пишется при записи (<see cref="SaveAsync"/>); у файла без блока этажи восстанавливаются из LevelElevations по LevelIndex элементов.
    /// </summary>
    public List<BimLevel>? Levels { get; set; }

    /// <summary>
    /// v6 (фаза 4): типы, используемые элементами файла (Guid — UUID v5 сигнатуры, имя, значения типа). Элементы хранят и поля v5,
    /// поэтому при чтении тип каждого элемента восстанавливается по его значениям; из блока берутся имена типов.
    /// </summary>
    public List<BimElementTypeRecord>? Types { get; set; }

    /// <summary>Этажи прочитанного файла с привязанными элементами (передаются документу при открытии).</summary>
    [JsonIgnore] public BimLevelTable? LevelTable { get; private set; }

    public List<ChatMessageModel> ChatHistory { get; set; } = [];

    /// <summary>
    /// Волна 6: участок ГПЗУ проекта (живая земля), подтверждённые ограничения и активная Среда девелопера. null — блок не пишется
    /// (проекты без участка и эталон ядра не меняются); старые версии программы блок пропускают.
    /// </summary>
    public SiteWorkspaceRecord? Site { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Атомарная запись: сначала во временный файл, затем замена (сбой не портит прежний проект, временный файл удаляется).
    /// Сериализация — в пуле потоков: снимок уже состоит из клонов, большая сцена не замораживает интерфейс.
    /// </summary>
    public async Task SaveAsync(string path)
    {
        SavedAt = DateTime.Now;
        CaptureParameterDefinitions(ParameterRegistry.Shared);
        CaptureLevels();
        CaptureTypes();
        string tmp = path + ".tmp";
        await Task.Run(async () =>
        {
            try
            {
                await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, this, JsonOptions).ConfigureAwait(false);
                }
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Определения всех Guid, встречающихся в значениях элементов: из реестра, иначе из уже прочитанного блока файла, иначе
    /// запасное по типу значения. Порядок — по имени и Guid (запись детерминирована).
    /// </summary>
    private void CaptureParameterDefinitions(ParameterRegistry registry)
    {
        var known = new Dictionary<Guid, ParameterDefinition>();
        foreach (var d in ParameterDefinitions ?? [])
            if (d is { IsValid: true }) known.TryAdd(d.Guid, d);
        var used = new Dictionary<Guid, ParameterDefinition>();
        foreach (var e in Elements)
        {
            if (e.UserParameters is not { } values) continue;
            foreach (var (guid, value) in values)
                if (!used.ContainsKey(guid))
                    used[guid] = registry.Find(guid) ?? known.GetValueOrDefault(guid) ?? ParameterDefinition.Fallback(guid, value);
        }
        ParameterDefinitions = used.Count == 0 ? null : [.. used.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Guid)];
    }

    /// <summary>
    /// Этажи файла: элементы без этажа документа привязываются к этажам постройки (таблица документа, к которой уже привязаны
    /// другие элементы той же постройки, иначе — новая таблица файла), отметки синхронизируются с геометрией. Порядок —
    /// постройки по первому появлению, этажи по номеру; Id детерминированы (постройка + номер), запись воспроизводима.
    /// </summary>
    private void CaptureLevels()
    {
        if (Elements.Count == 0)
        {
            Levels = null;
            return;
        }
        var tableOf = new Dictionary<string, BimLevelTable>(StringComparer.Ordinal);
        foreach (var e in Elements)
            if (e.Level?.Table is { } t) tableOf.TryAdd(e.BuildingGroupId, t);
        BimLevelTable? capture = null;
        var groups = new List<string>();
        foreach (var e in Elements)
        {
            if (!tableOf.TryGetValue(e.BuildingGroupId, out var table)) tableOf[e.BuildingGroupId] = table = capture ??= new BimLevelTable();
            table.Bind(e);
            if (!groups.Contains(e.BuildingGroupId)) groups.Add(e.BuildingGroupId);
        }
        foreach (var table in tableOf.Values.Distinct()) table.Synchronize(Elements.Where(e => e.Level?.Table == table));
        Levels = [.. groups.SelectMany(g => tableOf[g].LevelsOf(g)).Select(l => l.Clone())];
    }

    /// <summary>Типы элементов файла в порядке первого использования (только используемые; реестр приложения общий).</summary>
    private void CaptureTypes()
    {
        var seen = new HashSet<Guid>();
        var records = new List<BimElementTypeRecord>();
        foreach (var e in Elements)
            if (e.ElementType is var t && seen.Add(t.UniqueId))
                records.Add(BimElementTypeRecord.From(t));
        Types = records.Count == 0 ? null : records;
    }

    /// <summary>
    /// Чтение этажей и типов: этажи из блока (номер — позиция по отметке, привязка по levelId), у файла без блока — из
    /// LevelElevations по старому LevelIndex; тип каждого элемента восстановлен по его значениям, из блока типов — имена.
    /// </summary>
    private void RestoreLevelsAndTypes(BimTypeRegistry registry)
    {
        var levels = (Levels ?? []).Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Id) && double.IsFinite(l.Elevation)).ToList();
        LevelTable = levels.Count > 0 ? BimLevelTable.FromLevels(levels, Elements) : BimLevelTable.FromElevations(Elements);
        Levels = [.. LevelTable.All];
        foreach (var record in Types ?? [])
            if (record is { Name.Length: > 0 } && registry.Find(record.UniqueId) is { } type)
                type.Name = record.Name;
    }

    /// <summary>
    /// Миграция v5 → v6: в файлах до v6 блоков нет — пустой список определений, значений у элементов нет. Определения v6
    /// регистрируются в общем реестре (известный Guid — прежнее определение), значения приводятся к типу хранения определения;
    /// значения без определения и непредставимые отбрасываются. Номер версии прочитанного файла не меняется: по нему
    /// загрузчик проекта решает, какие параметры сессии файла применимы.
    /// </summary>
    private void MigrateParameters(ParameterRegistry registry)
    {
        ParameterDefinitions ??= [];
        ParameterDefinitions.RemoveAll(d => d is null || !d.IsValid);
        var resolved = new Dictionary<Guid, ParameterDefinition>();
        foreach (var d in ParameterDefinitions)
            if (registry.Register(d) is { } registered) resolved.TryAdd(d.Guid, registered);
        foreach (var e in Elements)
            e.NormalizeUserParameters(guid => resolved.GetValueOrDefault(guid) ?? registry.Find(guid));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Временный файл занят — исходный проект не тронут, остаток перезапишет следующее сохранение.
        }
    }

    /// <summary>Чтение и разбор JSON — в пуле потоков (сцену в SceneStateService загружает вызывающая сторона в UI-потоке).</summary>
    public static async Task<SceneProjectFile> LoadAsync(string path)
    {
        var file = await Task.Run(async () =>
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            return await JsonSerializer.DeserializeAsync<SceneProjectFile>(stream, JsonOptions).ConfigureAwait(false);
        }).ConfigureAwait(true) ?? throw new InvalidDataException("Файл проекта пуст или повреждён.");
        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException($"Версия формата {file.FormatVersion} новее поддерживаемой ({CurrentFormatVersion}).");
        file.Normalize();
        return file;
    }

    /// <summary>Восстанавливает корректность массивов, групп и привязок после загрузки (в т.ч. файлов версии 1).</summary>
    private void Normalize()
    {
        Parameters ??= new SceneProjectParameters();
        Parameters.StructuralLoads?.Sanitize();
        if (Parameters.CostEstimate is { IsValid: false }) Parameters.CostEstimate = null;
        BuildingGroups ??= [];
        Elements ??= [];
        GridAxes ??= [];
        ChatHistory ??= [];
        PinnedDimensions ??= [];
        GridAxes.RemoveAll(a => a is null || !double.IsFinite(a.Coordinate) || string.IsNullOrWhiteSpace(a.Label));
        PinnedDimensions.RemoveAll(d => d is null || !d.IsValid);
        foreach (var d in PinnedDimensions)
        {
            if (string.IsNullOrWhiteSpace(d.Id)) d.Id = PinnedDimension.NewId();
            d.Label ??= string.Empty;
        }

        foreach (var e in Elements)
        {
            e.ProfileType = ProfileTypes.Normalize(e.ProfileType);
            e.RoofShape = RoofShapes.Normalize(e.RoofShape);
            if (e.Position is not { Length: 3 }) e.Position = [0, 0, 0];
            if (e.Size is not { Length: 3 }) e.Size = [1, 1, 1];
            if (e.Rotation is not { Length: 3 }) e.Rotation = [0, 0, 0];
            e.Polygon2D ??= [];
            e.Layers ??= [];
            e.Layers.RemoveAll(l => l is null || !double.IsFinite(l.ThicknessMm) || l.ThicknessMm <= 0);
            foreach (var l in e.Layers)
            {
                l.Name ??= "Слой";
                l.ColorHex = string.IsNullOrWhiteSpace(l.ColorHex) ? MaterialLayers.DefaultColor(l.Role, l.Name) : l.ColorHex;
                if (!double.IsFinite(l.DensityKgM3) || l.DensityKgM3 < 0) l.DensityKgM3 = MaterialLayers.DefaultDensity(l.Role, l.Name);
            }
            if (e.RepeatLevels < 1) e.RepeatLevels = 1;
            if (e.TaperScalePerFloor <= 0) e.TaperScalePerFloor = 1;
            if (string.IsNullOrWhiteSpace(e.BuildingGroupId)) e.BuildingGroupId = BuildingGroup.DefaultGroupId;
            if (string.IsNullOrWhiteSpace(e.Id)) e.Id = ArchitecturalElement.NewId();
            e.RefreshType();
        }

        // Проёмы, чья стена-родитель отсутствует в файле, становятся самостоятельными элементами.
        var ids = Elements.Select(e => e.Id).ToHashSet();
        foreach (var e in Elements.Where(e => e.HostElementId is not null && !ids.Contains(e.HostElementId)))
            e.HostElementId = null;

        foreach (var gid in Elements.Select(e => e.BuildingGroupId).Distinct())
        {
            if (BuildingGroups.All(g => g.Id != gid))
                BuildingGroups.Add(new BuildingGroup(gid));
        }

        MigrateParameters(ParameterRegistry.Shared);
        RestoreLevelsAndTypes(BimTypeRegistry.Shared);
    }
}
