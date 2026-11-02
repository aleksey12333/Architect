#if DEBUG
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест системы параметров (фаза 2): каталог встроенных параметров и слоты ParameterSet, фасад ≡ слоты на всех сценах эталона,
/// чтение/запись без аллокаций, семантика уведомлений прежних [ObservableProperty], общие параметры по Guid (реестр, приведение
/// типов, клоны, Undo/Redo), .aia v6 (блоки parameterDefinitions / parameters, Round-Trip всех типов хранения) и миграция v5 → v6.
/// </summary>
public static partial class CadSelfTestService
{
    private static async Task<int> CheckParameterSystemAsync(string directory, StringBuilder report)
    {
        int failures = 0;

        // 1. Каталог: строка таблицы i — параметр i, слоты уникальны в своём буфере и не выходят за его размер.
        var table = BuiltInParameters.All;
        bool ordered = table.Select((p, i) => (int)p.Id == i).All(x => x) && table.Count == Enum.GetValues<BuiltInParameter>().Length;
        bool slots = table.Where(p => !p.IsVectorComponent).GroupBy(p => SlotBuffer(p.StorageType)).All(g => g.Select(p => p.Slot).Distinct().Count() == g.Count()) &&
                     table.All(p => p.Slot >= 0 && p.Slot < SlotCount(p)) &&
                     table.Where(p => p.IsVectorComponent).Select(p => (p.Slot, p.Component)).Distinct().Count() == table.Count(p => p.IsVectorComponent);
        Check(report, ref failures, ordered && slots,
            $"каталог BuiltInParameter: {table.Count} параметров — double {DoubleSlot.Count}, int {IntSlot.Count}, string/ElementId {StringSlot.Count}, bool {BoolSlot.Count} слотов " +
            $"и {VectorSlot.Count} вектора [X, Y, Z] (центр, габариты W/H/D, поворот); таблица по индексу перечисления, слоты без пересечений");

        // 2. Фасад ≡ слоты: каждое свойство каждого элемента всех сцен эталона читается из ParameterSet до бита.
        var elements = BaselineSources().SelectMany(s => s.Elements).ToList();
        var mismatches = new List<string>();
        foreach (var e in elements)
            foreach (var p in table)
                if (!SameBits(FacadeValue(e, p.Id), e.Get(p.Id)))
                    mismatches.Add($"{e.Name}.{p.Name}");
        Check(report, ref failures, mismatches.Count == 0,
            $"фасад ≡ ParameterSet: {elements.Count} элементов 7 сцен эталона × {table.Count} встроенных параметров — значения свойств и слотов совпадают до бита" +
            (mismatches.Count > 0 ? ": " + string.Join(", ", mismatches.Take(5)) : string.Empty));

        // 3. Горячий путь без аллокаций: чтение Width/Height/Depth/Material/LevelHeight и Get(BuiltInParameter), запись LevelHeight.
        var hot = elements.First(x => x.Category == BimCategory.Column);
        var probe = hot.Clone();
        double sink = HotLoop(probe, 20_000);
        var sw = new Stopwatch();
        long before = GC.GetAllocatedBytesForCurrentThread();
        sw.Start();
        sink += HotLoop(probe, 1_000_000);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double ns = sw.Elapsed.TotalMilliseconds * 1e6 / 1_000_000;
        Check(report, ref failures, allocated == 0 && double.IsFinite(sink),
            $"слоты без упаковки: 1 000 000 итераций (Width, Height, Depth, Material, LevelHeight, GetDouble/GetInt по BuiltInParameter, запись LevelHeight) — " +
            $"выделено {allocated} байт, {ns:0.0} нс на итерацию");

        // 4. Уведомления прежних [ObservableProperty]: порядок событий, равное значение — без событий, −0 не заменяет +0, Category до «Type».
        var e1 = new ArchitecturalElement { Id = "el_param0001" };
        var changed = new List<string?>();
        var changing = new List<string?>();
        e1.PropertyChanged += (_, a) => changed.Add(a.PropertyName);
        e1.PropertyChanging += (_, a) => changing.Add(a.PropertyName);
        var size = new double[] { 2, 3, 0.3 };
        e1.Size = size;
        bool sizeEvents = changed.SequenceEqual(["Size", "Width", "Height", "Depth", "BaseY", "TopY"]) && changing.SequenceEqual(["Size"]) && ReferenceEquals(e1.Size, size);
        changed.Clear();
        changing.Clear();
        e1.Size = size;
        e1.LevelHeight = 3.0;
        e1.TwistAnglePerFloor = -0.0;
        bool quiet = changed.Count == 0 && changing.Count == 0 && BitConverter.DoubleToInt64Bits(e1.TwistAnglePerFloor) == 0;
        BimCategory seen = BimCategory.Unknown;
        PropertyChangedEventHandler onType = (_, a) => { if (a.PropertyName == "Type") seen = e1.Category; };
        e1.PropertyChanged += onType;
        e1.Type = ElementTypes.Column;
        e1.PropertyChanged -= onType;
        bool typeEvents = seen == BimCategory.Column && changed.SkipWhile(n => n != "Type").Take(4).SequenceEqual(["Type", "DisplayTitle", "Glyph", "Category"]);
        changed.Clear();
        bool viaId = e1.Set(BuiltInParameter.Width, ParameterValue.FromDouble(2.5)) && e1.Width == 2.5 && !ReferenceEquals(e1.Size, size) && size[0] == 2 &&
                     changed.Contains("Width") && !e1.Set(BuiltInParameter.Width, ParameterValue.FromDouble(2.5)) &&
                     e1.Set(BuiltInParameter.LevelIndex, ParameterValue.FromDouble(2.0)) && e1.LevelIndex == 2 &&
                     e1.Set(BuiltInParameter.HostElement, ParameterValue.FromElementId("el_host")) && e1.HostElementId == "el_host" &&
                     e1.Set(BuiltInParameter.HostElement, ParameterValue.FromElementId("")) && e1.HostElementId is null;
        Check(report, ref failures, sizeEvents && quiet && typeEvents && viaId,
            "уведомления как у [ObservableProperty]: Size → Size, Width, Height, Depth, BaseY, TopY (PropertyChanging — Size), массив хранится как есть; " +
            "равное значение и −0 вместо +0 — без событий; Category обновлена до «Type»; Set(BuiltInParameter) идёт через свойства фасада");

        // 5. Общие параметры: реестр по имени и смыслу, приведение типов, клоны независимы, Undo/Redo — как у встроенных.
        var registry = ParameterRegistry.Shared;
        var mark = registry.Define("Марка (самотест)", ParameterSpecType.Text);
        var fire = registry.Define("Предел огнестойкости, мин (самотест)", ParameterSpecType.Integer);
        var offset = registry.Define("Отступ (самотест)", ParameterSpecType.Length);
        var load = registry.Define("Нагрузка (самотест)", ParameterSpecType.Force);
        var bearing = registry.Define("Несущий (самотест)", ParameterSpecType.YesNo);
        var link = registry.Define("Связанный элемент (самотест)", ParameterSpecType.ElementReference);
        bool shared = ReferenceEquals(registry.Define("марка (САМОТЕСТ)", ParameterSpecType.Text), mark) &&
                      registry.Define("Марка (самотест)", ParameterSpecType.Number).Guid != mark.Guid && registry.Find(mark.Guid) == mark &&
                      registry.Register(new ParameterDefinition { Guid = mark.Guid, Name = "Другое имя", StorageType = ParameterStorageType.Double, SpecType = ParameterSpecType.Number }) == mark;
        var e2 = new ArchitecturalElement { Id = "el_param0002", Type = ElementTypes.Column };
        bool typed = e2.SetUserParameter(fire, ParameterValue.FromDouble(90.4)) && e2.GetUserParameter(fire.Guid) == ParameterValue.FromInt(90) &&
                     e2.SetUserParameter(offset, ParameterValue.FromString("0,25")) && e2.GetUserParameter(offset.Guid).AsDouble() == 0.25 &&
                     ParameterSpecs.ToDisplay(offset.SpecType, 0.25) == 250 && ParameterSpecs.FromDisplay(load.SpecType, 12.5) == 12_500 &&
                     !e2.SetUserParameter(offset, ParameterValue.FromString("abc")) && !e2.SetUserParameter(fire, ParameterValue.FromDouble(90));
        var copy = e2.Clone();
        copy.SetUserParameter(fire, ParameterValue.FromInt(120));
        bool clones = e2.GetUserParameter(fire.Guid).AsInt() == 90 && copy.GetUserParameter(fire.Guid).AsInt() == 120 &&
                      copy.GetUserParameter(offset.Guid) == e2.GetUserParameter(offset.Guid) && e2.Parameters.UserCount == 2;

        var (frame, frameGroups, frameAxes) = CreateFrameScene();
        var scene = new SceneStateService();
        scene.LoadScene(frame.Select(x => x.Clone()).ToList(), frameGroups.Select(g => g.Clone()).ToList(), recordUndo: false, clearHistory: true,
            axes: frameAxes.Select(a => a.Clone()).ToList());
        var columns = scene.Elements.Where(x => x.Category == BimCategory.Column).Take(3).ToList();
        int depth = scene.UndoDepth;
        scene.UpdateElements(columns, x => x.SetUserParameter(mark, ParameterValue.FromString("К1")));
        bool step = scene.UndoDepth == depth + 1 && columns.All(x => x.GetUserParameter(mark.Guid).AsString() == "К1");
        bool undone = scene.Undo() && scene.Elements.Where(x => x.Category == BimCategory.Column).Take(3).All(x => x.GetUserParameter(mark.Guid).IsEmpty);
        bool redone = scene.Redo() && scene.Elements.Where(x => x.Category == BimCategory.Column).Take(3).All(x => x.GetUserParameter(mark.Guid).AsString() == "К1");
        Check(report, ref failures, shared && typed && clones && step && undone && redone,
            $"общие параметры: одно определение на имя и смысл без учёта регистра (Guid {mark.Guid:D}), известный Guid не переопределяется; " +
            "целое 90,4 → 90, длина «0,25» м → 250 мм, 12,5 кН → 12 500 Н, «abc» в длину отклонено; Clone() независим; правка 3 колонн — 1 шаг Undo, Undo/Redo");

        // 6. .aia v6: значения всех типов хранения переживают запись и чтение; без пользовательских параметров новых блоков нет.
        var withParams = frame.Select(x => x.Clone()).ToList();
        var wall = withParams.First(x => x.Category == BimCategory.Wall);
        var column = withParams.First(x => x.Category == BimCategory.Column);
        wall.SetUserParameter(mark, ParameterValue.FromString("НС-1 «наружная»"));
        wall.SetUserParameter(fire, ParameterValue.FromInt(150));
        wall.SetUserParameter(bearing, ParameterValue.FromBool(true));
        wall.SetUserParameter(link, ParameterValue.FromElementId(column.Id));
        column.SetUserParameter(offset, ParameterValue.FromDouble(0.1 + 0.2));
        column.SetUserParameter(load, ParameterValue.FromDouble(-1.5e5));
        string v6Path = Path.Combine(directory, "parameters_v6.aia");
        await new SceneProjectFile { ProjectName = "Параметры", Elements = withParams, BuildingGroups = frameGroups.Select(g => g.Clone()).ToList() }.SaveAsync(v6Path);
        string v6Text = await File.ReadAllTextAsync(v6Path);
        var v6 = await SceneProjectFile.LoadAsync(v6Path);
        bool sameValues = v6.Elements.Count == withParams.Count && v6.Elements.Zip(withParams).All(z => SameUserValues(z.First, z.Second));
        var defs = v6.ParameterDefinitions ?? [];
        bool blocks = v6.FormatVersion == 6 && v6Text.Contains("\"formatVersion\": 6", StringComparison.Ordinal) &&
                      v6Text.Contains("\"parameterDefinitions\"", StringComparison.Ordinal) && CountOf(v6Text, "\"parameters\": {") == 3 &&
                      defs.Count == 6 && defs.Select(d => d.Guid).ToHashSet().SetEquals(new[] { mark, fire, offset, load, bearing, link }.Select(d => d.Guid)) &&
                      defs.All(d => registry.Find(d.Guid) is { } r && r.StorageType == d.StorageType && r.SpecType == d.SpecType);
        string plainPath = Path.Combine(directory, "parameters_plain.aia");
        await new SceneProjectFile { ProjectName = "Без параметров", Elements = frame.Select(x => x.Clone()).ToList(), BuildingGroups = frameGroups.Select(g => g.Clone()).ToList() }
            .SaveAsync(plainPath);
        string plainText = await File.ReadAllTextAsync(plainPath);
        bool plain = !plainText.Contains("parameterDefinitions", StringComparison.Ordinal) && CountOf(plainText, "\"parameters\":") == 1;
        Check(report, ref failures, sameValues && blocks && plain,
            $".aia v6: определений {defs.Count} (текст, целое, длина, сила, да/нет, ссылка на элемент), значения 2 элементов после записи и чтения ≡ исходным до бита; " +
            $"файл без пользовательских параметров — без блоков parameterDefinitions и parameters элементов ({plainText.Length / 1024} КБ)");

        // 7. Миграция v5 → v6: файл v5 открывается без потерь (элементы ≡ до бита), список определений пуст, номер версии файла сохранён;
        //    значения v6 приводятся к типу определения, значения без определения и непредставимые отбрасываются.
        string v5Path = Path.Combine(directory, "parameters_v5.aia");
        await File.WriteAllTextAsync(v5Path, plainText.Replace("\"formatVersion\": 6,", "\"formatVersion\": 5,"), new UTF8Encoding(false));
        var v5 = await SceneProjectFile.LoadAsync(v5Path);
        var plainLoaded = await SceneProjectFile.LoadAsync(plainPath);
        bool migrated = v5.FormatVersion == 5 && v5.ParameterDefinitions is { Count: 0 } && v5.Elements.All(x => x.UserParameters is null) &&
                        ElementRecords(v5.Elements, v5.BuildingGroups).SequenceEqual(ElementRecords(plainLoaded.Elements, plainLoaded.BuildingGroups));
        var orphan = Guid.NewGuid();
        string coerced = v6Text
            .Replace($"\"{fire.Guid:D}\": 150", $"\"{fire.Guid:D}\": 149.6")
            .Replace($"\"{offset.Guid:D}\": ", $"\"{orphan:D}\": 7, \"{offset.Guid:D}\": ");
        string coercedPath = Path.Combine(directory, "parameters_coerced.aia");
        await File.WriteAllTextAsync(coercedPath, coerced, new UTF8Encoding(false));
        var c6 = await SceneProjectFile.LoadAsync(coercedPath);
        var cw = c6.Elements.First(x => x.Id == wall.Id);
        var cc = c6.Elements.First(x => x.Id == column.Id);
        bool coercion = cw.GetUserParameter(fire.Guid) == ParameterValue.FromInt(150) && cc.GetUserParameter(orphan).IsEmpty &&
                        cc.GetUserParameter(offset.Guid).AsDouble() == 0.1 + 0.2 && cc.Parameters.UserCount == 2;
        bool future = false;
        try
        {
            string futurePath = Path.Combine(directory, "parameters_v7.aia");
            await File.WriteAllTextAsync(futurePath, plainText.Replace("\"formatVersion\": 6,", "\"formatVersion\": 7,"), new UTF8Encoding(false));
            await SceneProjectFile.LoadAsync(futurePath);
        }
        catch (InvalidDataException)
        {
            future = true;
        }
        Check(report, ref failures, migrated && coercion && future,
            $"миграция v5 → v6: файл v5 из {v5.Elements.Count} элементов открыт без изменений до бита, определений нет, версия файла {v5.FormatVersion} сохранена; " +
            "в v6 целое 149,6 → 150, значение без определения отброшено; файл v7 отклонён");

        // 8. JSON элемента без пользовательских параметров не меняется (нет ключа parameters), со значениями — блок по Guid.
        string bare = JsonSerializer.Serialize(new ArchitecturalElement { Id = "el_param0003" }, SceneProjectFile.JsonOptions);
        string rich = JsonSerializer.Serialize(e2, SceneProjectFile.JsonOptions);
        var back = JsonSerializer.Deserialize<ArchitecturalElement>(rich, SceneProjectFile.JsonOptions)!;
        back.NormalizeUserParameters(registry.Find);
        bool json = !bare.Contains("\"parameters\"", StringComparison.Ordinal) && rich.Contains($"\"{fire.Guid:D}\": 90", StringComparison.Ordinal) && SameUserValues(back, e2) &&
                    rich.IndexOf("\"id\"", StringComparison.Ordinal) == bare.IndexOf("\"id\"", StringComparison.Ordinal) &&
                    rich.IndexOf("\"layers\"", StringComparison.Ordinal) < rich.IndexOf("\"parameters\"", StringComparison.Ordinal);
        Check(report, ref failures, json,
            "JSON элемента: без пользовательских параметров ключа parameters нет (поля и порядок v5), со значениями — объект «Guid → значение» последним полем после layers, чтение ≡ записи");
        return failures;
    }

    private static int SlotBuffer(ParameterStorageType storage) => storage is ParameterStorageType.ElementId ? (int)ParameterStorageType.String : (int)storage;

    private static int SlotCount(BuiltInParameterInfo p) => p.IsVectorComponent ? VectorSlot.Count : p.StorageType switch
    {
        ParameterStorageType.Double => DoubleSlot.Count,
        ParameterStorageType.Int => IntSlot.Count,
        ParameterStorageType.Bool => BoolSlot.Count,
        _ => StringSlot.Count
    };

    /// <summary>Значение встроенного параметра через свойство фасада (независимо от таблицы слотов).</summary>
    private static ParameterValue FacadeValue(ArchitecturalElement e, BuiltInParameter p) => p switch
    {
        BuiltInParameter.Name => ParameterValue.FromString(e.Name),
        BuiltInParameter.ElementType => ParameterValue.FromString(e.Type),
        BuiltInParameter.BuildingGroup => ParameterValue.FromString(e.BuildingGroupId),
        BuiltInParameter.Material => ParameterValue.FromString(e.Material),
        BuiltInParameter.ProfileType => ParameterValue.FromString(e.ProfileType),
        BuiltInParameter.RoofShape => ParameterValue.FromString(e.RoofShape),
        BuiltInParameter.HostElement => ParameterValue.FromElementId(e.HostElementId),
        BuiltInParameter.LevelIndex => ParameterValue.FromInt(e.LevelIndex),
        BuiltInParameter.RepeatLevels => ParameterValue.FromInt(e.RepeatLevels),
        BuiltInParameter.StepCount => ParameterValue.FromInt(e.StepCount),
        BuiltInParameter.LevelHeight => ParameterValue.FromDouble(e.LevelHeight),
        BuiltInParameter.TwistAnglePerFloor => ParameterValue.FromDouble(e.TwistAnglePerFloor),
        BuiltInParameter.TaperScalePerFloor => ParameterValue.FromDouble(e.TaperScalePerFloor),
        BuiltInParameter.ProfileWallThickness => ParameterValue.FromDouble(e.ProfileWallThickness),
        BuiltInParameter.ProfileWebThickness => ParameterValue.FromDouble(e.ProfileWebThickness),
        BuiltInParameter.HasPhysicsError => ParameterValue.FromBool(e.HasPhysicsError),
        BuiltInParameter.PositionX => ParameterValue.FromDouble(e.X),
        BuiltInParameter.PositionY => ParameterValue.FromDouble(e.Y),
        BuiltInParameter.PositionZ => ParameterValue.FromDouble(e.Z),
        BuiltInParameter.Width => ParameterValue.FromDouble(e.Width),
        BuiltInParameter.Height => ParameterValue.FromDouble(e.Height),
        BuiltInParameter.Depth => ParameterValue.FromDouble(e.Depth),
        BuiltInParameter.RotationX => ParameterValue.FromDouble(e.Rotation.Length > 0 ? e.Rotation[0] : 0),
        BuiltInParameter.RotationY => ParameterValue.FromDouble(e.RotationY),
        BuiltInParameter.RotationZ => ParameterValue.FromDouble(e.Rotation.Length > 2 ? e.Rotation[2] : 0),
        BuiltInParameter.LevelId => ParameterValue.FromElementId(e.LevelId),
        _ => ParameterValue.Empty
    };

    /// <summary>Равенство с учётом битов double (−0 ≠ +0, как в отпечатках эталона).</summary>
    private static bool SameBits(ParameterValue a, ParameterValue b) =>
        a == b && (a.StorageType != ParameterStorageType.Double || BitConverter.DoubleToInt64Bits(a.AsDouble()) == BitConverter.DoubleToInt64Bits(b.AsDouble()));

    private static bool SameUserValues(ArchitecturalElement a, ArchitecturalElement b)
    {
        var x = a.UserParameters ?? new Dictionary<Guid, ParameterValue>();
        var y = b.UserParameters ?? new Dictionary<Guid, ParameterValue>();
        return x.Count == y.Count && x.All(p => y.TryGetValue(p.Key, out var v) && SameBits(p.Value, v));
    }

    private static double HotLoop(ArchitecturalElement e, int iterations)
    {
        double sum = 0;
        for (int i = 0; i < iterations; i++)
        {
            sum += e.Width + e.Height + e.Depth + e.LevelHeight + e.Material.Length;
            sum += e.Parameters.GetDouble(BuiltInParameter.Depth) + e.Parameters.GetInt(BuiltInParameter.LevelIndex);
            e.LevelHeight = (i & 1) == 0 ? 3.0 : 3.3;
        }
        return sum;
    }
}
#endif
