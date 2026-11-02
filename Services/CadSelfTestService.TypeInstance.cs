#if DEBUG
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест этажей (фаза 3) и Type-Instance (фаза 4): сигнатура и реестр типов на всех сценах эталона, «Дублировать тип»
/// в сеттерах фасада, «Изменить тип», Undo/Redo, вычисляемый LevelIndex, .aia v6 (блоки levels/types) и миграция файлов без этажей.
/// </summary>
public static partial class CadSelfTestService
{
    private static async Task<int> CheckLevelsAndTypesAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        var registry = BimTypeRegistry.Shared;

        // 1. Сигнатура: на всех сценах эталона тип экземпляра — тип его значений (тот же объект реестра), скаляры фасада ≡ типу,
        //    габариты типа ≡ Size до бита, Guid — UUID v5 сигнатуры; одинаковые значения — один тип.
        var scenes = BaselineSources().ToList();
        int elements = 0, types = 0, broken = 0;
        var sceneStats = new List<string>();
        foreach (var s in scenes)
        {
            var used = new HashSet<Guid>();
            foreach (var e in s.Elements)
            {
                elements++;
                var t = e.ElementType;
                used.Add(t.UniqueId);
                var again = registry.Resolve(e.Type, e.Material, e.ProfileType, e.RoofShape, e.ProfileWallThickness, e.ProfileWebThickness, e.Size, e.Layers);
                bool same = ReferenceEquals(again, t) && t.Material == e.Material && t.ProfileType == e.ProfileType && t.RoofShape == e.RoofShape &&
                            SameDouble(t.ProfileWallThickness, e.ProfileWallThickness) && SameDouble(t.ProfileWebThickness, e.ProfileWebThickness) &&
                            (!t.Dimensions.HasFlag(TypeDimensions.Width) || SameDouble(t.Width, e.Size[0])) &&
                            (!t.Dimensions.HasFlag(TypeDimensions.Height) || SameDouble(t.Height, e.Size[1])) &&
                            (!t.Dimensions.HasFlag(TypeDimensions.Depth) || SameDouble(t.Depth, e.Size[2])) &&
                            SameDouble(e.Width, e.Size[0]) && SameDouble(e.Height, e.Size[1]) && SameDouble(e.Depth, e.Size[2]) &&
                            t.UniqueId == BimUniqueId.Create(BimUniqueId.TypeNamespace, t.Signature) && t.Layers.Count == e.Layers.Count;
                if (!same) broken++;
            }
            types += used.Count;
            sceneStats.Add($"{s.Name} {s.Elements.Count}/{used.Count}");
        }
        Check(report, ref failures, broken == 0,
            $"сигнатура типа: {elements} элементов 7 сцен эталона → {types} типов (экземпляров/типов: {string.Join(", ", sceneStats)}); тип = поиск по сигнатуре " +
            "(тот же объект реестра), материал/профиль/толщины/кровля фасада ≡ типу, габариты типа ≡ Size до бита, Guid = UUID v5 сигнатуры" +
            (broken > 0 ? $"; расхождений {broken}" : string.Empty));

        // 2. «Дублировать тип»: правка сечения одной колонны не меняет тип и другие колонны; возврат значения — прежний тип;
        //    один шаг Undo, Undo/Redo восстанавливают тип; правка габарита экземпляра (высота колонны) тип не меняет.
        var (frame, frameGroups, frameAxes) = CreateFrameScene();
        var scene = new SceneStateService();
        scene.LoadScene(frame.Select(x => x.Clone()).ToList(), frameGroups.Select(g => g.Clone()).ToList(), recordUndo: false, clearHistory: true,
            axes: frameAxes.Select(a => a.Clone()).ToList());
        var columnType = scene.Types.Values.Where(t => t.ElementTypeName == ElementTypes.Column).OrderByDescending(t => scene.InstancesOf(t).Count).First();
        var columns = scene.InstancesOf(columnType);
        var target = columns[0];
        string targetId = target.Id;
        double oldWidth = target.Width;
        int typesBefore = registry.Count, depth = scene.UndoDepth;
        scene.UpdateElement(target, x => x.SetSize(0.5, x.Height, x.Depth));
        var newType = target.ElementType;
        bool duplicated = newType.UniqueId != columnType.UniqueId && target.Width == 0.5 && newType.Width == 0.5 && columnType.Width == oldWidth &&
                          columns.Skip(1).All(c => c.TypeId == columnType.UniqueId && c.Width == oldWidth) && scene.UndoDepth == depth + 1 &&
                          scene.Types.ContainsKey(newType.UniqueId) && registry.Find(newType.UniqueId) == newType;
        bool undo = scene.Undo() && scene.FindById(targetId) is { } u && u.TypeId == columnType.UniqueId && u.Width == oldWidth;
        bool redo = scene.Redo() && scene.FindById(targetId) is { } r && r.TypeId == newType.UniqueId && r.Width == 0.5;
        target = scene.FindById(targetId)!;
        target.SetSize(oldWidth, target.Height, target.Depth);
        bool dedup = target.TypeId == columnType.UniqueId && ReferenceEquals(target.ElementType, columnType);
        target.SetSize(oldWidth, target.Height + 0.3, target.Depth);
        bool instanceDim = target.TypeId == columnType.UniqueId;
        target.SetSize(oldWidth, target.Height - 0.3, target.Depth);
        Check(report, ref failures, duplicated && dedup && instanceDim && undo && redo,
            $"«Дублировать тип»: ширина 1 из {columns.Count} колонн «{columnType.Name}» → 500 мм — новый тип «{newType.Name}», прежний тип и {columns.Count - 1} колонн " +
            $"без изменений (реестр {typesBefore} → {registry.Count}); возврат 400 мм — тот же объект прежнего типа; высота колонны (габарит экземпляра) тип не меняет; " +
            "1 шаг Undo, Undo/Redo переключают тип");

        // 3. «Изменить тип»: все экземпляры типа — на один новый тип одним шагом Undo; экземпляры других типов и КЭ остальных — прежние.
        var beamType = scene.Types.Values.First(t => t.ElementTypeName == ElementTypes.Beam && scene.InstancesOf(t).Count >= 3);
        var otherBeams = scene.Elements.Where(e => e.Category == BimCategory.Beam && e.TypeId != beamType.UniqueId).ToDictionary(e => e.Id, e => e.TypeId);
        var loads = new StructuralLoadSettings();
        var beforeAnalysis = StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), loads);
        depth = scene.UndoDepth;
        int edited = scene.UpdateTypeInstances(beamType, b => b.SetSize(b.Width, 0.6, b.Depth));
        var beamsNow = scene.Elements.Where(e => e.Category == BimCategory.Beam && !otherBeams.ContainsKey(e.Id)).ToList();
        bool editType = edited >= 3 && beamsNow.Count == edited && beamsNow.Select(b => b.TypeId).Distinct().Count() == 1 && beamsNow[0].ElementType.Height == 0.6 &&
                        beamsNow[0].TypeId != beamType.UniqueId && otherBeams.All(p => scene.FindById(p.Key)?.TypeId == p.Value) && scene.UndoDepth == depth + 1;
        bool editUndo = scene.Undo() && scene.Elements.Where(e => !otherBeams.ContainsKey(e.Id) && e.Category == BimCategory.Beam).All(b => b.TypeId == beamType.UniqueId);
        var afterAnalysis = StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), loads);
        bool kSame = AnalysisFingerprint(beforeAnalysis) == AnalysisFingerprint(afterAnalysis);
        Check(report, ref failures, editType && editUndo && kSame,
            $"«Изменить тип»: {edited} балок «{beamType.Name}» → высота 600 мм одним шагом Undo, все на одном новом типе, {otherBeams.Count} балок других типов не тронуты; " +
            "Undo возвращает тип, КЭ-расчёт после цикла правка → Undo ≡ исходному до бита");

        // 4. Этажи документа: каждый элемент привязан к этажу своей постройки, LevelIndex = позиция этажа по отметке,
        //    отметки — по геометрии (LevelElevations) и не убывают; LevelIndex = n выбирает этаж n (недостающий создаётся).
        var levels = scene.Levels;
        var frameLevels = levels.LevelsOf(FrameGroup);
        var marks = StructuralAxesBuilder.LevelElevations(scene.Elements.Where(e => e.BuildingGroupId == FrameGroup).ToList());
        bool bound = scene.Elements.All(e => e.Level is { } l && l.BuildingGroupId == e.BuildingGroupId && e.LevelIndex == l.Index && e.LevelId == l.Id &&
                                             levels.LevelsOf(e.BuildingGroupId).OrderBy(x => x.Elevation).ToList().IndexOf(l) == l.Index);
        bool elevations = frameLevels.Zip(frameLevels.Skip(1)).All(p => p.First.Elevation <= p.Second.Elevation) &&
                          marks.All(m => frameLevels.FirstOrDefault(l => l.Index == m.Level) is { } l && l.Elevation == m.Elevation);
        var probe = scene.Elements.First(e => e.Category == BimCategory.Column && e.LevelIndex == 0);
        int levelCount = frameLevels.Count;
        probe.LevelIndex = levelCount + 1;
        bool created = levels.LevelsOf(FrameGroup).Count == levelCount + 2 && probe.Level?.Index == levelCount + 1 && probe.LevelId == levels.LevelsOf(FrameGroup)[^1].Id;
        probe.LevelIndex = 0;
        bool back = probe.Level == frameLevels[0] && probe.LevelIndex == 0;
        Check(report, ref failures, bound && elevations && created && back,
            $"этажи документа: {scene.Elements.Count} элементов привязаны к {frameLevels.Count} этажам «{FrameGroup}» (отметки {string.Join(" / ", frameLevels.Select(l => l.Elevation.ToString("0.000", Inv)))} м ≡ LevelElevations), " +
            "LevelIndex = позиция этажа по отметке, LevelId = Id этажа; LevelIndex = n выбирает этаж n, недостающий создаётся");

        // 5. LevelIndex вычисляется: подъём отметки этажа выше соседнего меняет номера этажей и LevelIndex привязанных элементов
        //    без записи в элементы; копии (снимки Undo, песочницы) делят этаж с исходным.
        var table = new BimLevelTable();
        var a = new ArchitecturalElement { Id = "el_lvltest01", LevelIndex = 0 };
        var b2 = new ArchitecturalElement { Id = "el_lvltest02", LevelIndex = 1 };
        var c3 = new ArchitecturalElement { Id = "el_lvltest03", LevelIndex = 2 };
        foreach (var x in new[] { a, b2, c3 }) table.Bind(x);
        var clone = a.Clone();
        var l0 = a.Level!;
        table.SetElevation(l0, 7.5);
        bool dynamic = a.LevelIndex == 2 && clone.LevelIndex == 2 && b2.LevelIndex == 0 && c3.LevelIndex == 1 && a.LevelId == l0.Id &&
                       a.Parameters.GetInt(BuiltInParameter.LevelIndex) == 0 && a.Get(BuiltInParameter.LevelIndex).AsInt() == 2;
        table.SetElevation(l0, -1);
        bool restored = a.LevelIndex == 0 && b2.LevelIndex == 1 && c3.LevelIndex == 2;
        var detached = new ArchitecturalElement { LevelIndex = 4 };
        bool free = detached.Level is null && detached.LevelIndex == 4 && detached.LevelId is null;
        Check(report, ref failures, dynamic && restored && free,
            "LevelIndex вычисляемый: отметка этажа 1 → 7,500 м — элементы этажей 1/2/3 получают номера 3/1/2 (и клон — тот же этаж), слот элемента не переписывается; " +
            "отметка −1,000 — номера прежние; элемент вне документа хранит номер в слоте");

        // 6. .aia v6: блоки levels и types, у элементов levelId и typeId; после записи и чтения этажи (Id, имена, отметки),
        //    привязки, типы и имена типов ≡ исходным; элементы ≡ до бита.
        var renamed = scene.Types.Values.First(t => t.ElementTypeName == ElementTypes.Slab);
        string oldName = renamed.Name;
        ((BimElementType)renamed).Name = "П-1 (самотест)";
        string path = Path.Combine(directory, "levels_types_v6.aia");
        var saved = new SceneProjectFile
        {
            ProjectName = "Этажи и типы", BuildingGroups = scene.BuildingGroups.Select(g => g.Clone()).ToList(), Elements = scene.Elements.Select(e => e.Clone()).ToList()
        };
        await saved.SaveAsync(path);
        string text = await File.ReadAllTextAsync(path);
        ((BimElementType)renamed).Name = oldName;
        var loaded = await SceneProjectFile.LoadAsync(path);
        var savedLevels = scene.Levels.All;
        var loadedLevels = loaded.LevelTable?.All ?? [];
        bool levelsSame = savedLevels.Count == loadedLevels.Count && savedLevels.Zip(loadedLevels).All(p => p.First.Id == p.Second.Id && p.First.Name == p.Second.Name &&
                          p.First.BuildingGroupId == p.Second.BuildingGroupId && p.First.Elevation == p.Second.Elevation && p.First.Index == p.Second.Index);
        bool links = loaded.Elements.Zip(saved.Elements).All(p => p.First.LevelId == p.Second.LevelId && p.First.LevelIndex == p.Second.LevelIndex && p.First.TypeId == p.Second.TypeId);
        bool typeBlock = (saved.Types?.Count ?? 0) == scene.Types.Count && text.Contains("\"levels\": [", StringComparison.Ordinal) &&
                         text.Contains("\"types\": [", StringComparison.Ordinal) && CountOf(text, "\"typeId\":") == scene.Elements.Count &&
                         CountOf(text, "\"levelId\":") == scene.Elements.Count && renamed.Name == "П-1 (самотест)";
        ((BimElementType)renamed).Name = oldName;
        bool records = ElementRecords(loaded.Elements, loaded.BuildingGroups).SequenceEqual(ElementRecords(saved.Elements, saved.BuildingGroups));
        Check(report, ref failures, levelsSame && links && typeBlock && records,
            $".aia v6: блоки levels ({savedLevels.Count} этажей) и types ({saved.Types?.Count ?? 0} типов), levelId и typeId у {scene.Elements.Count} элементов; " +
            "после записи и чтения этажи (Id, имена, отметки, номера), привязки и типы ≡ исходным, имя типа из файла восстановлено, элементы ≡ до бита");

        // 7. Миграция файла без этажей и типов (до v6 и v6 фазы 2): этажи восстановлены из LevelElevations, элементы привязаны
        //    по старому LevelIndex, типы — по значениям; открытие в документе сохраняет Id этажей файла.
        var node = JsonNode.Parse(text)!.AsObject();
        node.Remove("levels");
        node.Remove("types");
        foreach (var item in node["elements"]!.AsArray())
        {
            item!.AsObject().Remove("levelId");
            item.AsObject().Remove("typeId");
        }
        node["formatVersion"] = 5;
        string oldPath = Path.Combine(directory, "levels_types_v5.aia");
        await File.WriteAllTextAsync(oldPath, node.ToJsonString(SceneProjectFile.JsonOptions), new UTF8Encoding(false));
        var old = await SceneProjectFile.LoadAsync(oldPath);
        var oldTable = old.LevelTable!;
        var oldMarks = StructuralAxesBuilder.LevelElevations(old.Elements.Where(e => e.BuildingGroupId == FrameGroup).ToList());
        bool migrated = old.FormatVersion == 5 && old.Elements.Zip(saved.Elements).All(p => p.First.LevelIndex == p.Second.LevelIndex && p.First.TypeId == p.Second.TypeId &&
                            p.First.Level is not null && p.First.Level.Index == p.First.LevelIndex) &&
                        oldMarks.All(m => oldTable.LevelsOf(FrameGroup).FirstOrDefault(l => l.Index == m.Level)?.Elevation == m.Elevation) &&
                        ElementRecords(old.Elements, old.BuildingGroups).SequenceEqual(ElementRecords(saved.Elements, saved.BuildingGroups));
        var opened = new SceneStateService();
        opened.LoadScene(loaded.Elements, loaded.BuildingGroups, recordUndo: false, clearHistory: true);
        bool openedIds = opened.Elements.All(e => e.Level?.Table == opened.Levels) && opened.Elements.Zip(saved.Elements).All(p => p.First.LevelId == p.Second.LevelId) &&
                         opened.Levels.LevelsOf(FrameGroup).Select(l => l.Id).SequenceEqual(loaded.LevelTable!.LevelsOf(FrameGroup).Select(l => l.Id));
        Check(report, ref failures, migrated && openedIds,
            $"миграция файла без этажей и типов: {oldTable.Count} этажей восстановлено из LevelElevations (отметки ≡), {old.Elements.Count} элементов привязаны по старому LevelIndex, " +
            "типы — по значениям (Guid ≡), элементы ≡ до бита; открытие проекта сохраняет Id этажей файла");
        return failures;
    }

    private static bool SameDouble(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
}
#endif
