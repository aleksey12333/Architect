#if DEBUG
using System.Text;
using System.Text.Json;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест идентичности и категорий BIM: отображение строкового Type ↔ BimCategory, предикаты категорий против прежних
/// наборов типов, UniqueId по RFC 4122 (UUID v5) от Id, неизменность формата .aia (Category и UniqueId не сериализуются).
/// </summary>
public static partial class CadSelfTestService
{
    private static int CheckBimIdentity(StringBuilder report)
    {
        int failures = 0;

        // 1. Каждый тип ElementTypes — своя категория элемента и обратно; чужая строка (в т.ч. другой регистр) — Unknown, как у ==.
        var categories = ElementTypes.All.Select(BimCategories.FromType).ToList();
        bool mapping = categories.Distinct().Count() == ElementTypes.All.Count && categories.All(BimCategories.IsElement) &&
                       ElementTypes.All.All(t => BimCategories.ToType(BimCategories.FromType(t)) == t) &&
                       ElementTypes.All.All(t => BimCategories.FromType(t).ToString() == ToPascal(t)) &&
                       BimCategories.FromType("Column") == BimCategory.Unknown && BimCategories.FromType(null) == BimCategory.Unknown &&
                       BimCategories.ToType(BimCategory.Building) is null;
        Check(report, ref failures, mapping,
            $"BimCategory: {ElementTypes.All.Count} типов ElementTypes ↔ {categories.Distinct().Count()} категорий без потерь, имена совпадают, «Column»/null → Unknown (порядковое сравнение, как ==)");

        // 2. Предикаты по категории дают ровно прежние наборы строковых типов.
        var expected = new (string Title, Func<BimCategory, bool> Predicate, string[] Types)[]
        {
            ("хосты проёмов", ElementTypes.IsHostType, [ElementTypes.Wall, ElementTypes.CurtainWall, ElementTypes.Core]),
            ("проёмы в стене", ElementTypes.IsHostedType, [ElementTypes.Window, ElementTypes.Door, ElementTypes.WallOpening]),
            ("хосты отверстий плит", ElementTypes.IsSlabHostType, [ElementTypes.Slab, ElementTypes.CantileverSlab, ElementTypes.Foundation, ElementTypes.Roof, ElementTypes.Terrace]),
            ("пустоты", ElementTypes.IsVoidType, [ElementTypes.WallOpening, ElementTypes.SlabOpening]),
            ("стержни с профилем", ElementTypes.IsProfiledType, [ElementTypes.Column, ElementTypes.Beam, ElementTypes.Diagrid]),
            ("перекрытия фильтра этажа", ElementTypes.IsCeilingType, [ElementTypes.Slab, ElementTypes.Roof, ElementTypes.Shell]),
            ("бетонные", ElementTypes.IsConcreteType, [ElementTypes.Foundation, ElementTypes.Wall, ElementTypes.Column, ElementTypes.Core, ElementTypes.Beam, ElementTypes.Slab,
                ElementTypes.CantileverSlab, ElementTypes.Shell, ElementTypes.Stairs, ElementTypes.Terrace, ElementTypes.Pool]),
            ("остеклённые", ElementTypes.IsGlazingType, [ElementTypes.CurtainWall, ElementTypes.Window])
        };
        var wrong = expected.Where(x => !ElementTypes.All.Append("unknown").All(t => x.Predicate(BimCategories.FromType(t)) == x.Types.Contains(t))).Select(x => x.Title).ToList();
        Check(report, ref failures, wrong.Count == 0,
            $"предикаты категорий ({expected.Length}: хосты, проёмы, пустоты, профили, бетон, остекление…) ≡ прежним наборам строковых типов" + (wrong.Count > 0 ? ": " + string.Join(", ", wrong) : string.Empty));

        // 3. Category элемента следует за Type и уведомляет привязки.
        var e = new ArchitecturalElement { Id = "el_test0001" };
        var changed = new List<string?>();
        e.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        bool byDefault = e.Category == BimCategory.Wall;
        e.Type = ElementTypes.Column;
        bool column = e.Category == BimCategory.Column && changed.Contains(nameof(ArchitecturalElement.Category));
        e.Type = "Column";
        bool unknown = e.Category == BimCategory.Unknown;
        e.Type = ElementTypes.Roof;
        e.RoofShape = RoofShapes.Flat;
        bool roof = e.Category == BimCategory.Roof && e.IsFlatRoof && e.CanHostSlabOpenings;
        Check(report, ref failures, byDefault && column && unknown && roof,
            "Category элемента: по умолчанию Wall, следует за Type (PropertyChanged «Category»), «Column» → Unknown, плоская кровля — хост отверстий");

        // 4. UniqueId — UUID v5 (RFC 4122): эталонный вектор, версия 5, вариант, детерминизм от Id, смена Id и клоны.
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        bool vector = BimUniqueId.Create(dns, "python.org") == new Guid("886313e1-3b8a-5372-9b90-0c9aee199e5d");
        var a = new ArchitecturalElement { Id = "el_test0001" };
        var b = new ArchitecturalElement { Id = new string("el_test0001".ToCharArray()) };
        string text = a.UniqueId.ToString();
        bool v5 = text[14] == '5' && "89ab".Contains(text[19]);
        bool stable = a.UniqueId == b.UniqueId && a.UniqueId == BimUniqueId.Create(BimUniqueId.ElementNamespace, "el_test0001") && a.UniqueId == e.UniqueId;
        var before = a.UniqueId;
        a.Id = "el_test0002";
        bool follows = a.UniqueId != before && a.UniqueId == BimUniqueId.Create(BimUniqueId.ElementNamespace, "el_test0002");
        bool clones = a.Clone().UniqueId == a.UniqueId && a.Clone(keepId: false).UniqueId != a.UniqueId;
        bool spaces = new BuildingGroup("el_test0001").UniqueId != b.UniqueId && new PinnedDimension { Id = "el_test0001" }.UniqueId != b.UniqueId;
        Check(report, ref failures, vector && v5 && stable && follows && clones && spaces,
            $"UniqueId: UUID v5 по RFC 4122 (вектор «python.org» {(vector ? "совпал" : "НЕ совпал")}), «el_test0001» → {before}, " +
            "одинаков для равных Id, меняется вместе с Id, сохраняется Clone() и обновляется Clone(keepId: false); постройки и размеры — свои пространства имён");

        // 5. Формат .aia не меняется: Category и UniqueId не сериализуются и восстанавливаются из Type/Id после чтения.
        var source = new ArchitecturalElement { Id = "el_test0003", Type = ElementTypes.SlabOpening, Name = "Отверстие" };
        string json = JsonSerializer.Serialize(source, SceneProjectFile.JsonOptions);
        var restored = JsonSerializer.Deserialize<ArchitecturalElement>(json, SceneProjectFile.JsonOptions)!;
        string groupJson = JsonSerializer.Serialize(new BuildingGroup("Гараж"), SceneProjectFile.JsonOptions);
        string dimensionJson = JsonSerializer.Serialize(new PinnedDimension((0, 0, 0), (3, 4, 0)), SceneProjectFile.JsonOptions);
        bool hidden = new[] { json, groupJson, dimensionJson }.All(s => !s.Contains("\"category\"", StringComparison.Ordinal) && !s.Contains("\"uniqueId\"", StringComparison.Ordinal));
        bool format = hidden && restored.Category == BimCategory.SlabOpening && restored.UniqueId == source.UniqueId;
        Check(report, ref failures, format, "поля .aia без изменений: Category и UniqueId элементов, построек и размеров не пишутся в JSON, после чтения восстанавливаются из Type и Id");

        // 6. Все сцены: категория каждого элемента известна, UniqueId без коллизий; IBimElement — общий вид элементов, построек и размеров.
        var scenes = PresetScenesService.All.Select(p => PresetScenesService.Create(p.Id)).ToList();
        var all = scenes.SelectMany(s => s.Elements).ToList();
        var bim = all.Cast<IBimElement>().Concat(scenes.SelectMany(s => s.Groups)).Append(new PinnedDimension((0, 0, 0), (1, 0, 0), "L1")).ToList();
        bool known = all.All(x => x.Category == BimCategories.FromType(x.Type) && x.Category != BimCategory.Unknown);
        bool unique = bim.Select(x => x.UniqueId).Distinct().Count() == bim.Select(x => (x.Category, x.Id)).Distinct().Count();
        Check(report, ref failures, known && unique,
            $"пресеты: {all.Count} элементов — категории известны и равны FromType(Type); {bim.Count} объектов IBimElement " +
            $"({string.Join(", ", bim.GroupBy(x => x.Category).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} {g.Count()}"))}…) — UniqueId без коллизий");
        return failures;
    }

    /// <summary>curtain_wall → CurtainWall: имена категорий совпадают с константами ElementTypes.</summary>
    private static string ToPascal(string type) =>
        string.Concat(type.Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}
#endif
