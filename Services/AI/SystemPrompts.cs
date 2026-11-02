using System.Globalization;
using System.Text;
using System.Text.Json;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services.AI;

/// <summary>Режим «Из картинки».</summary>
public enum VisionMode
{
    None,
    FloorPlan2DTo3D,
    HandSketchTo3D,
    RealPhotoTo3D,

    /// <summary>Комплект листов (многостраничный PDF / чертёж): адаптивный конвейер с блоком blueprint.</summary>
    DrawingSet
}

/// <summary>Режим применения ответа ИИ к сцене.</summary>
public enum SolverMode
{
    NewScene,
    AppendToSite,
    Modify
}

/// <summary>
/// Скрытое системное ТЗ для нейросетей: правила Y-Up, универсальная JSON-схема (от бани до небоскрёба),
/// точечное редактирование выбранного элемента и три Vision-режима.
/// </summary>
public static class SystemPrompts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Полное ТЗ для облачных моделей и веб-аккаунтов.</summary>
    public const string Core = """
Ты — ИИ-архитектор студии AI Architect Studio (уровень Renga / Revit). Ты проектируешь здания и отвечаешь ТОЛЬКО одним JSON-объектом в блоке ```json без пояснений до и после.

СИСТЕМА КООРДИНАТ (Y-Up, метры):
- Земля — плоскость XZ при Y = 0, высота растёт вверх по оси Y.
- position = [x, y, z] — геометрический центр элемента. Центр элемента высотой H на отметке BaseY равен BaseY + H / 2.
- size = [ширина вдоль локальной X, высота по Y, глубина/толщина по локальной Z].
- rotation — поворот вокруг оси Y в градусах (число).
- Отметки по Y можно не считать: C#-солвер сам ставит стены на перекрытие своего этажа, перекрытия — на верх стен, кровлю — на верхнее перекрытие.

ФОРМАТ ОТВЕТА:
{
  "mode": "new" | "append" | "modify",
  "message": "1-2 предложения по-русски: что построено или изменено",
  "buildingGroups": [ { "id": "Главный дом", "purpose": "residential" } ],
  "elements": [ ... ],
  "delete": [ "id элементов для удаления (только mode = modify)" ]
}

ТИПЫ ЭЛЕМЕНТОВ: foundation, wall, curtain_wall, column, core, beam, truss, diagrid, slab, cantilever_slab, shell, roof, stairs, window, door, pool, terrace, asset, wall_opening, slab_opening.

ПОЛЯ ЭЛЕМЕНТА:
- "id" (короткий уникальный, например "w1"), "type", "name" (по-русски), "group" (id постройки), "level" (этаж с 0), "material".
- Стены и витражи (wall, curtain_wall) задавай отрезком оси в плане: "start": [x, z], "end": [x, z], "height": 3.0, "thickness": 0.3.
  Наружные стены этажа образуют замкнутый контур: конец одной стены = начало следующей (углы солвер сомкнёт сам).
- Окна и двери: "host": "<id стены>", "offset": смещение центра проёма вдоль стены от её середины (м), "width", "height", "sill" (высота низа проёма над полом этажа; у двери 0). Проём должен помещаться в стену.
- Остальные элементы: "position": [x, z] или [x, y, z], "size": [w, h, d], "rotation".
- Полигональные плиты (cantilever_slab, slab, foundation, terrace): "polygon": [[x, z], ...] в локальных координатах относительно position.
- Плита (slab) уровня L лежит НАД стенами этажа L (перекрытие этажа L). Плита на земле — это foundation.
- Кровля (roof) и купол (shell): "level" = номер верхнего этажа; солвер положит их на верхнее перекрытие.
  Форма кровли — строго как в задании: "roofType": "flat" (плоское покрытие толщиной "thickness" по контуру "polygon") |
  "gable" | "hip" | "shed" (высота конька — "height"). Не меняй плоскую кровлю на скатную и наоборот.
- Колонны и балки: "profile": "rectangle" (сечение строго W × D, квадрат — W = D) | "circle" ("diameter") | "pipe" ("diameter",
  "tf" — стенка) | "i_beam" ("tw" — стенка, "tf" — полка). Балка — отрезком "start"/"end", "height" — высота сечения, "thickness" — ширина.
- Проёмы: "wall_opening" — сквозной проём в стене без заполнения (как окно: "host", "offset", "width", "height", "sill");
  "slab_opening" — отверстие в плите/покрытии/фундаменте (лестница, лифт, шахта): "level", "position": [x, z],
  "size": [ширина, 0, длина] или "polygon"; солвер привяжет его к плите этажа.
- Бассейн (pool) — чаша, утопленная в землю: size = [ширина, глубина чаши, длина]. Терраса (terrace) — настил на земле.

БОЛЬШИЕ ЗДАНИЯ: каркас по сетке осей и одинаковые этажи описывай компактным блоком "blueprint" (оси, уровни, сечения,
расстановки колонн/балок/стен, плиты, отверстия, кровля) — C#-компилятор развернёт его на все этажи без потерь.

ПАРАМЕТРИКА ВЫСОТОК (не перечисляй этажи вручную — C#-солвер развернёт их сам):
- "repeatLevels": N — элемент повторяется на N этажах вверх с шагом "levelHeight" (м).
- "twistAnglePerFloor": поворот каждого следующего этажа в градусах (закрученная башня).
- "taperScalePerFloor": масштаб плана каждого следующего этажа (0.98 — сужение кверху).
- Ядро жёсткости (core) с repeatLevels станет сквозным на всю высоту. Диагрид (diagrid) с repeatLevels — наклонная сетка-оболочка фасада.
- Типовой этаж башни: cantilever_slab с polygon + curtain_wall (polygon того же контура) + core + diagrid, все с одинаковыми repeatLevels/levelHeight/twist/taper.

ПРАВИЛА:
- Реалистичные размеры: стена 0.2–0.5 м, высота этажа 2.7–4.2 м, окно ~1.2–1.8 × 1.4–1.6 м, дверь 0.9–1.2 × 2.1–2.4 м.
- Несколько построек на участке — разные "group" и разнесённые координаты (баня, гараж, бассейн с террасой).
- Не используй эмодзи. Числа — только числами, без единиц измерения.
""";

    /// <summary>Компактное ТЗ для локальных моделей с контекстом 2048 токенов.</summary>
    public const string Compact = """
You are an architect AI. Reply with ONE JSON object only, no prose.
Coordinates: Y-up, meters, ground is XZ plane at Y=0. Y elevations are computed by the C# solver.
Schema: {"mode":"new","message":"short Russian summary","elements":[...]}
Element fields: id, type, name (Russian), group, level (0-based), material.
Types: foundation, wall, curtain_wall, column, core, beam, truss, diagrid, slab, cantilever_slab, shell, roof, stairs, window, door, pool, terrace, asset, wall_opening, slab_opening.
Column/beam "profile": rectangle|circle|pipe|i_beam. Roof "roofType": flat|gable|hip|shed.
Walls: "start":[x,z],"end":[x,z],"height":3,"thickness":0.3. Close the outline: next wall starts where previous ends.
Window/door: "host":"<wall id>","offset":along-wall shift from wall center,"width","height","sill" (door sill 0).
Others: "position":[x,z],"size":[w,h,d],"rotation":deg. Slab of level L sits on top of level L walls. Roof: level = top floor.
Towers: one element with "repeatLevels":N,"levelHeight":3.6,"twistAnglePerFloor":deg,"taperScalePerFloor":0.99 (solver expands floors).
Example: {"mode":"new","message":"Дом 10x8","elements":[{"id":"f","type":"foundation","position":[0,0],"size":[10.4,0.5,8.4]},{"id":"w1","type":"wall","start":[-5,4],"end":[5,4],"height":3,"thickness":0.3},{"id":"d1","type":"door","host":"w1","offset":0,"width":1,"height":2.2,"sill":0},{"id":"r","type":"roof","level":0,"position":[0,0],"size":[11,2.5,9]}]}
""";

    /// <summary>
    /// Системное ТЗ под модель: компактное для локальных моделей, полное — для облака и веба; для изображений и PDF —
    /// плюс адаптивная инструкция под тип материала (<see cref="AdaptivePromptBuilder"/>).
    /// </summary>
    public static string BuildSystem(AiModelOption model, VisionMode vision, BlueprintKind kind = BlueprintKind.Auto, int imageCount = 1)
    {
        if (model.IsLocal) return Compact;
        return vision == VisionMode.None ? Core : Core + "\n" + AdaptivePromptBuilder.Build(vision, kind, imageCount);
    }

    /// <summary>
    /// Текст пользовательского сообщения: задача + режим (новая сцена / постройка на участок / правка выбранного)
    /// + компактный контекст сцены и выбранного элемента.
    /// </summary>
    public static string BuildUser(string task, SolverMode mode, SceneContext scene, bool compact)
    {
        var sb = new StringBuilder();
        switch (mode)
        {
            case SolverMode.Modify when scene.Selected is not null:
                sb.AppendLine(compact
                    ? "MODE: modify. Change ONLY the selected element (and its openings). Reply {\"mode\":\"modify\",\"elements\":[{\"id\":\"<same id>\", changed fields only}],\"delete\":[]}. New elements get new ids."
                    : "РЕЖИМ: modify — правка выбранного элемента. Измени ТОЛЬКО его (и при необходимости связанные проёмы). " +
                      "Верни {\"mode\":\"modify\",\"elements\":[{\"id\":\"<тот же id>\", только изменённые поля}],\"delete\":[...]}. " +
                      "Новые элементы добавляй с новыми id; для удаления перечисли id в \"delete\".");
                sb.AppendLine(compact ? "SELECTED:" : "ВЫБРАННЫЙ ЭЛЕМЕНТ (SelectedElement):");
                sb.AppendLine(Describe(scene.Selected));
                if (scene.Host is not null)
                {
                    sb.AppendLine(compact ? "HOST WALL:" : "СТЕНА-РОДИТЕЛЬ:");
                    sb.AppendLine(Describe(scene.Host));
                }
                if (scene.Hosted.Count > 0)
                {
                    sb.AppendLine(compact ? "OPENINGS IN IT:" : "ПРОЁМЫ В ЭЛЕМЕНТЕ:");
                    foreach (var h in scene.Hosted.Take(compact ? 4 : 12)) sb.AppendLine(Describe(h));
                }
                break;

            case SolverMode.AppendToSite:
                sb.AppendLine(compact
                    ? "MODE: append. Create ONLY the NEW building with a NEW group id, centered at origin. The solver places it on a free spot of the site."
                    : "РЕЖИМ: append — добавь на участок ТОЛЬКО НОВУЮ постройку с новым \"group\" (не повторяй существующие). " +
                      "Строй её вокруг начала координат: C#-солвер сам поставит её на свободное место участка с отступом 4 м.");
                if (scene.ExistingGroups.Count > 0)
                    sb.AppendLine((compact ? "EXISTING: " : "УЖЕ НА УЧАСТКЕ: ") + string.Join(", ", scene.ExistingGroups));
                break;

            default:
                sb.AppendLine(compact ? "MODE: new scene." : "РЕЖИМ: new — новая сцена с нуля.");
                break;
        }
        sb.AppendLine();
        sb.AppendLine(compact ? "TASK:" : "ЗАДАЧА ПОЛЬЗОВАТЕЛЯ:");
        sb.Append(task.Trim());
        return sb.ToString();
    }

    /// <summary>Компактный JSON элемента для контекста ИИ.</summary>
    public static string Describe(ArchitecturalElement e)
    {
        var map = new Dictionary<string, object?>
        {
            ["id"] = e.Id,
            ["type"] = e.Type,
            ["name"] = e.Name,
            ["group"] = e.BuildingGroupId,
            ["level"] = e.LevelIndex,
            ["position"] = Round(e.Position),
            ["size"] = Round(e.Size),
            ["rotation"] = Math.Round(e.RotationY, 2),
            ["material"] = e.Material
        };
        if (e.HostElementId is not null) map["host"] = e.HostElementId;
        if (ElementTypes.IsProfiledType(e.Type)) map["profile"] = e.EffectiveProfile;
        if (e.Type == ElementTypes.Roof) map["roofType"] = RoofShapes.Normalize(e.RoofShape);
        if (e.RepeatLevels > 1)
        {
            map["repeatLevels"] = e.RepeatLevels;
            map["levelHeight"] = e.LevelHeight;
            map["twistAnglePerFloor"] = e.TwistAnglePerFloor;
            map["taperScalePerFloor"] = e.TaperScalePerFloor;
        }
        if (e.Polygon2D.Count >= 3) map["polygon"] = e.Polygon2D.Select(p => new[] { Math.Round(p.X, 3), Math.Round(p.Z, 3) });
        return JsonSerializer.Serialize(map, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static double[] Round(double[] v) => v.Select(x => Math.Round(x, 3)).ToArray();

    /// <summary>Полный промпт для веб-аккаунта: системное ТЗ + задача в одном сообщении.</summary>
    public static string BuildWebPrompt(string system, string user) =>
        $"{system}\n\n---\n{user}\n\nОтветь одним блоком ```json со всей сценой.";

    public static string F(double value) => value.ToString("0.###", Inv);
}

/// <summary>Снимок контекста сцены для промпта (выбранный элемент, его стена и проёмы, постройки участка).</summary>
public sealed record SceneContext(ArchitecturalElement? Selected, ArchitecturalElement? Host, IReadOnlyList<ArchitecturalElement> Hosted, IReadOnlyList<string> ExistingGroups)
{
    public static readonly SceneContext Empty = new(null, null, [], []);
}
