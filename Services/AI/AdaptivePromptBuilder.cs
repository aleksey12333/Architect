using System.Text;

namespace AIArchitectStudio.Services.AI;

/// <summary>Тип загруженного материала: от него зависит, как ИИ читает листы.</summary>
public enum BlueprintKind
{
    /// <summary>ИИ сам определяет тип материала по листам.</summary>
    Auto,

    /// <summary>Конструктивный проект: КЖ / КМ, каркасные и монолитные здания.</summary>
    Structural,

    /// <summary>Архитектурный план: АР / БТИ / поэтажная планировка.</summary>
    Architectural,

    /// <summary>Свободный эскиз или фотография реального здания.</summary>
    SketchOrPhoto
}

/// <summary>
/// Динамическая маршрутизация промптов для чертежей, PDF, эскизов и фото. Вместо одного статичного ТЗ на все случаи
/// собирает инструкцию под тип материала: что именно считывать с листов (оси, отметки, сечения, контуры, отверстия),
/// запрет на выдумывание размеров и компактный параметрический блок <c>blueprint</c>, который разворачивает
/// <see cref="BlueprintCompilerService"/> — без лимита на число элементов и без «урезания» проекта.
/// </summary>
public static class AdaptivePromptBuilder
{
    public static BlueprintKind KindFor(VisionMode mode, BlueprintKind requested) => mode switch
    {
        VisionMode.FloorPlan2DTo3D => requested == BlueprintKind.Auto ? BlueprintKind.Architectural : requested,
        VisionMode.HandSketchTo3D or VisionMode.RealPhotoTo3D => requested == BlueprintKind.Auto ? BlueprintKind.SketchOrPhoto : requested,
        _ => requested
    };

    public static string KindTitle(BlueprintKind kind) => kind switch
    {
        BlueprintKind.Structural => "Конструктивный проект (КЖ / КМ)",
        BlueprintKind.Architectural => "Архитектурный план (АР / БТИ)",
        BlueprintKind.SketchOrPhoto => "Эскиз / фото",
        _ => "Автоопределение"
    };

    /// <summary>Инструкция анализа изображений: общие правила точности + правила типа материала + схема blueprint.</summary>
    public static string Build(VisionMode mode, BlueprintKind requested, int pageCount)
    {
        var kind = KindFor(mode, requested);
        var sb = new StringBuilder();
        sb.AppendLine(pageCount > 1
            ? $"АНАЛИЗ КОМПЛЕКТА ЛИСТОВ: передано изображений — {pageCount} (перед каждым указан номер листа PDF)."
            : "АНАЛИЗ ИЗОБРАЖЕНИЯ.");
        sb.AppendLine(AccuracyRules);
        sb.AppendLine();

        switch (kind)
        {
            case BlueprintKind.Structural:
                sb.AppendLine(StructuralRules);
                break;
            case BlueprintKind.Architectural:
                sb.AppendLine(ArchitecturalRules);
                break;
            case BlueprintKind.SketchOrPhoto:
                sb.AppendLine(mode == VisionMode.RealPhotoTo3D ? PhotoRules : SketchRules);
                break;
            default:
                sb.AppendLine("""
ШАГ 1 — ОПРЕДЕЛИ ТИП МАТЕРИАЛА и запиши его в поле "sourceKind":
- "structural" — рабочий конструктивный проект (марки КЖ/КМ, планы колонн, балок и плит, спецификации сечений);
- "architectural" — архитектурный план (АР, БТИ, поэтажная планировка со стенами, окнами и дверями);
- "sketch" — эскиз от руки или фотография здания.
ШАГ 2 — следуй правилам соответствующего раздела.
""");
                sb.AppendLine(StructuralRules);
                sb.AppendLine();
                sb.AppendLine(ArchitecturalRules);
                sb.AppendLine();
                sb.AppendLine(SketchRules);
                break;
        }
        sb.AppendLine();
        sb.AppendLine(BlueprintSchema);
        return sb.ToString();
    }

    /// <summary>
    /// Пользовательская часть запроса для комплекта листов: какие страницы выбраны, тип материала, точные факты
    /// векторного слоя PDF (размерные цепочки, отметки, марки осей, сечения) и параметры, проверенные пользователем.
    /// Блоки одинаково попадают к Gemini, Claude, ChatGPT, OpenRouter, локальному Qwen и в веб-мост.
    /// </summary>
    public static string DescribeSheets(IReadOnlyList<string> labels, int totalPages, BlueprintKind kind, string? fileName,
        Pdf.ExtractedBlueprintAnnotations? vectorFacts = null, BlueprintDefinition? verified = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(fileName)) sb.AppendLine($"Файл: {fileName}");
        if (labels.Count > 0)
            sb.AppendLine($"Выбранные листы ({labels.Count} из {totalPages}): {string.Join(", ", labels)}.");
        if (kind != BlueprintKind.Auto)
            sb.AppendLine($"Тип материала (выбран пользователем): {KindTitle(kind)}.");
        if (vectorFacts?.ToPromptBlock() is { Length: > 0 } facts)
        {
            sb.AppendLine();
            sb.AppendLine(facts);
        }
        if (verified is not null)
        {
            sb.AppendLine();
            sb.AppendLine(verified.ToPromptBlock());
        }
        return sb.ToString().TrimEnd();
    }

    private const string AccuracyRules = """
ПРАВИЛА ТОЧНОСТИ (обязательны):
- НИЧЕГО НЕ ПРИДУМЫВАЙ. Каждый размер бери с листов: размерные цепочки между осями, габариты здания по крайним осям,
  отметки уровней с разрезов (±0.000, +3.300 …), сечения из спецификаций и общих указаний, контуры плит и кровли, отверстия.
- Если величины на листах нет — не подставляй «типовую»: опусти поле (компилятор пометит это в отчёте) и перечисли
  допущения в "message".
- Не урезай проект и не сокращай число элементов: повторяющиеся колонны, балки, стены и этажи описывай правилами
  блока "blueprint" (расстановки по осям), а не поштучно — так JSON остаётся коротким при любом размере здания.
- Сохраняй формы как на чертеже: квадратные колонны — квадратные, прямоугольные — по своим сторонам, круглые — круглые,
  двутавры и трубы — профилями i_beam / pipe. Плоское покрытие не заменяй двускатной кровлей и наоборот.
- Сопоставляй листы между собой: план — оси, расстановки и контуры; разрез — отметки и высоты; спецификация — сечения.
- Размеры с чертежей указывай в миллиметрах и ставь "units": "mm".
""";

    private const string StructuralRules = """
КОНСТРУКТИВНЫЙ ПРОЕКТ (КЖ / КМ, каркасные и монолитные здания) — считывай:
- Общие указания и спецификации: марки и сечения колонн (К1 400×400, К2 Ø500), балок/ригелей (Б1 300×500, двутавр 40Б1,
  труба 325×10), толщины плит и стен → таблица "sections".
- План расположения колонн: шаги осей по размерным цепочкам → "gridAxes"; марка колонны в каждом узле → "columnPlacements"
  (узлы без колонн — в "except"; разные марки — отдельные расстановки).
- Планы балок (если каркас балочный): направление, оси, пролёты, сечения → "beamPlacements". Безбалочный каркас — без балок.
- Планы плит: толщина, контур перекрытия и покрытия → "contourPolygon" / "slabs"; ВСЕ отверстия (лестницы, лифты,
  шахты, проёмы под коммуникации) с привязкой к осям → "openings" с "type": "slab_opening".
- Монолитные стены, диафрагмы и ядра жёсткости — "wallSegments" с толщиной с чертежа.
- Разрезы: отметки верха плит / низа этажей → "levels" (elevation, height, slabThickness).
""";

    private const string ArchitecturalRules = """
АРХИТЕКТУРНЫЙ ПЛАН (АР / БТИ / поэтажная планировка) — считывай:
- Размерные цепочки и координационные оси (если есть) → "gridAxes"; без осей — координаты [x, y] от левого нижнего угла.
- Наружные стены и их толщины, внутренние стены и перегородки с толщинами как на плане → "wallSegments".
- Окна, двери и проёмы по экспликации/ведомости: ширина, высота, положение вдоль стены → "openings" (window / door / wall_opening).
- Отметки и высоты этажей с разреза или фасада → "levels"; одинаковые этажи — "levels": "all".
- Контур перекрытий и кровли по плану → "contourPolygon"; тип кровли с фасада/разреза → "roof".
- Мебель, сантехнику и подписи помещений не строй.
""";

    private const string SketchRules = """
ЭСКИЗ ОТ РУКИ (это не рабочий чертёж): пойми замысел — форму плана, этажность, тип кровли, крупные проёмы, консоли.
Подписанные размеры используй точно; неподписанные оценивай по пропорциям изображения и одному известному размеру
(высота этажа, дверь). Неровные линии выпрямляй, углы около 90° считай прямыми. Перечисли оценки в "message".
""";

    private const string PhotoRules = """
ФОТО РЕАЛЬНОГО ЗДАНИЯ: определи этажность, пропорции фасада, тип кровли (плоская / двускатная / вальмовая), ритм окон,
балконы и консоли, материалы. Масштаб оценивай по высоте этажа и ширине окон, оценки перечисли в "message".
Невидимые стороны достраивай симметрично видимым. Типовые этажи — блоком "blueprint" с "levels".
""";

    /// <summary>Компактная схема параметрического блока (общая для всех видов чертежей).</summary>
    public const string BlueprintSchema = """
ПАРАМЕТРИЧЕСКИЙ БЛОК "blueprint" (C#-компилятор развернёт его в элементы на всех этажах без потерь):
"blueprint": {
  "group": "Корпус 1", "units": "mm",
  "gridAxes": { "x": [{"label":"1","pos":0},{"label":"2","pos":6000}], "z": [{"label":"А","pos":0},{"label":"Б","pos":7200}] },
  "levels": [ {"name":"1 этаж","elevation":0,"height":3300,"slabThickness":220} ],
  "sections": { "К1": {"profile":"rectangle","b":400,"h":400}, "К2": {"profile":"circle","d":500},
                "Б1": {"profile":"rectangle","b":300,"h":500}, "Б2": {"profile":"i_beam","b":180,"h":400,"tw":8,"tf":13},
                "С1": {"profile":"pipe","d":325,"t":10} },
  "contourPolygon": [["1","А"],["6","А"],["6","Г"],["1","Г"]],
  "columnPlacements": [ {"section":"К1","at":"1-6/А-Г","levels":"all","except":[["3","Б"]]} ],
  "beamPlacements": [ {"section":"Б1","along":"x","axes":"А-Г","span":"1-6","levels":"all"},
                      {"section":"Б1","from":["1","А"],"to":["1","Г"],"levels":[0,1]} ],
  "wallSegments": [ {"id":"w1","from":["1","А"],"to":["6","А"],"thickness":250,"levels":"all"},
                    {"id":"w2","polyline":[[0,0],[12000,0],[12000,9000]],"thickness":380} ],
  "slabs": [ {"levels":"all","polygon":"contour","thickness":220} ],
  "openings": [ {"type":"slab_opening","between":["2","3","Б","В"],"levels":"all","name":"Лестничная клетка"},
                {"type":"slab_opening","at":["4","В",1200,-800],"size":[1500,2100],"levels":[1,2],"name":"Лифт"},
                {"type":"window","wall":"w1","offset":-1500,"width":1500,"height":1500,"sill":900},
                {"type":"wall_opening","wall":"w1","offset":2400,"width":1000,"height":2100} ],
  "roof": {"type":"flat"} или {"type":"flat","parapet":{"height":900,"thickness":250}} или {"type":"gable","rise":2500,"overhang":500},
  "foundation": {"thickness":600,"offset":300}
}
Пояснения: точки — ["2","Б"] (пересечение осей), ["2","Б",dx,dy] (со смещением) или [x, y] как на листе: x — вправо от оси «1»,
y — вверх по листу от оси «А»; pos буквенных осей отсчитывается вверх по листу. Диапазоны «1-6», «А-Г», узлы «1-6/А-Г»,
"all". "levels" — индексы этажей с 0, «0-3» или "all". Колонна b × h — сечение в плане (b вдоль осей 1-2-3); балка b × h —
ширина × высота. Колонны стоят на отметке этажа и доходят до низа плиты, балки — под плитой, плита — верхом на отметке
следующего этажа. Если "slabs" не задан, плиты строятся по "contourPolygon" на каждом этаже; при "roof": {"type":"flat"}
верхняя плита становится плоским покрытием. Уникальные объекты (лестницы, купола, наклонные элементы) добавляй
поштучно в "elements" рядом с "blueprint".
""";
}
