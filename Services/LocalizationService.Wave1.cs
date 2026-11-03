namespace AIArchitectStudio.Services;

/// <summary>
/// Строки волны 1 продуктового ревью: начальный экран из редактора, боковой док Коллоквиума, конструктор лестниц и полых кровель,
/// конструктивная логика проёмов (отдельный словарь, общий поиск — <see cref="LocalizationService.Get(string)"/>).
/// </summary>
public sealed partial class LocalizationService
{
    private static readonly Dictionary<string, (string Ru, string En)> Wave1Strings = new()
    {
        // ── Начальный экран ──
        ["W1StartScreen"] = ("Начальный экран", "Start screen"),
        ["W1StartScreenTip"] = ("Вернуться на стартовый экран выбора проекта", "Back to the project start screen"),
        ["W1StartConfirmTitle"] = ("Перейти на начальный экран?", "Go to the start screen?"),
        ["W1StartConfirm"] = ("В текущем проекте есть несохранённые изменения. Если на начальном экране выбрать другой проект или шаблон, они будут потеряны. Закрыть начальный экран — вернуться к проекту без изменений.",
            "The current project has unsaved changes. Choosing another project or template on the start screen discards them. Closing the start screen returns to the project unchanged."),
        ["W1Continue"] = ("Продолжить", "Continue"),
        ["W1Cancel"] = ("Отмена", "Cancel"),

        // ── Боковой док Коллоквиума ──
        ["W1DockCollapse"] = ("Свернуть в полосу", "Collapse to a strip"),
        ["W1DockExpand"] = ("Развернуть Коллоквиум", "Expand the colloquium"),
        ["W1DockClose"] = ("Закрыть ленту", "Close the feed"),
        ["W1DockReview"] = ("Лист согласования", "Review sheet"),

        // ── Конструктор лестниц ──
        ["W1StairTitle"] = ("Конструктор лестниц", "Staircase designer"),
        ["W1StairHint"] = ("Форма в плане и конструкция марша; над лестницей в перекрытии прорезается сквозной проём", "Plan shape and flight structure; a through opening is cut in the slab above"),
        ["W1StairShape"] = ("Форма", "Shape"),
        ["W1StairConstruction"] = ("Конструкция", "Structure"),
        ["W1StairStraight"] = ("Прямой одномаршевый", "Straight single flight"),
        ["W1StairLShaped"] = ("Г-образный, площадка 90°", "L-shaped, 90° landing"),
        ["W1StairUShaped"] = ("П-образный, площадка 180°", "U-shaped, 180° landing"),
        ["W1StairThreeFlight"] = ("Трёхмаршевый вокруг шахты", "Three flights around a shaft"),
        ["W1StairSpiral"] = ("Винтовой", "Spiral"),
        ["W1StairMonolithicSlab"] = ("Монолитный по наклонной плите", "Monolithic on a waist slab"),
        ["W1StairStringers"] = ("На двух косоурах", "On two stringers"),
        ["W1StairCantilever"] = ("Консольный на больцах", "Cantilever on bolts"),
        ["W1StairPreview"] = ("Этаж {0} мм → {1} подступенков по {2} мм · маршей {3} · клетка {4} × {5} мм", "Storey {0} mm → {1} risers of {2} mm · flights {3} · stairwell {4} × {5} mm"),
        ["W1StairDone"] = ("Лестница «{0}», {1}: {2} подступенков по {3} мм, маршей {4}, клетка {5} × {6} мм, проёмов в перекрытиях: {7}. Один шаг Undo.",
            "Staircase “{0}”, {1}: {2} risers of {3} mm, {4} flights, stairwell {5} × {6} mm, slab openings: {7}. One Undo step."),

        // ── Полые кровли ──
        ["W1RoofTitle"] = ("Конструктор кровли", "Roof designer"),
        ["W1RoofHint"] = ("Полые скаты заданной толщины со свесами — под кровлей остаётся чердак или мансарда", "Hollow slopes of a set thickness with eaves — an attic or loft stays underneath"),
        ["W1RoofThickness"] = ("Толщина ската, мм", "Slope thickness, mm"),
        ["W1RoofOverhang"] = ("Свес карниза, мм", "Eave overhang, mm"),
        ["W1Roof_gable_hollow"] = ("Двускатная", "Gable"),
        ["W1Roof_hip_hollow"] = ("Вальмовая", "Hip"),
        ["W1Roof_shed_hollow"] = ("Односкатная", "Shed"),
        ["W1Roof_flat_parapet"] = ("С парапетом", "With parapet"),
        ["W1Roof_mansard"] = ("Мансардная", "Mansard"),
        ["W1Roof_barrel_vault"] = ("Сводчатая", "Barrel vault"),
        ["W1RoofDone"] = ("Кровля «{0}» — «{1}»: уклон {2}°, пролёт {3} мм, высота {4} мм, скат {5} мм, свес {6} мм. Один шаг Undo.",
            "Roof “{0}” — “{1}”: pitch {2}°, span {3} mm, height {4} mm, slope {5} mm, overhang {6} mm. One Undo step."),

        // ── Логика проёмов ──
        ["W1GuardTitle"] = ("Проём в стене", "Wall opening")
    };
}
