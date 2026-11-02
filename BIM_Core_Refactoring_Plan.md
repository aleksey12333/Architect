# Architect Studio Pro — аналитический отчёт и план рефакторинга ядра на BIM-паттерн Type-Instance

## 0. Контекст проекта (для внешнего ревьюера)

- **Продукт:** Architect Studio Pro — настольная BIM/CAD-система на C# / .NET, WinUI 3 (unpackaged), MVVM на CommunityToolkit.Mvvm 8.4.2 (`[ObservableProperty] public partial …`, `LangVersion preview`), 3D — HelixToolkit.WinUI 2.27.3 (SharpDX).
- **Система координат:** Y-Up, земля — плоскость XZ при Y = 0, центр элемента высотой H на отметке BaseY = BaseY + H / 2. Внутренние единицы — метры.
- **Основные подсистемы:** параметрический солвер (`ParametricSolverService`: гравитация, узлы стен, вырезы проёмов, размножение этажей), КЭ-экспресс-расчёт (`StructuralAnalysisService`, норматив < 10 мс на сцену), Clash Detection, сметы и ведомости, армирование, инсоляция по СанПиН, экспорт IFC2X3 / DXF R12 / OBJ, импорт IFC / DXF / PDF, листы и альбомы ГОСТ (собственный векторный PDF), мультиагентный «Совет КБ» (несколько LLM + C#-проверщики `IBimDesignVerifier`), проект `.aia` (JSON, формат v5).
- **Ограничение рефакторинга:** встроенный автотест `AIA_DEBUG_SELFTEST` (169 проверок, в т.ч. совпадение результатов КЭ-расчёта «до бита» между параллельным и последовательным режимами, Round-Trip IFC/DXF/.aia, «ровно 1 шаг Undo» для пакетных правок) должен проходить после каждой фазы.
- **Эталонная целевая архитектура (по образу Revit API / Renga API):** `IBimElement` (Guid, Id, Name), паттерн Type-Instance (`IBimElementType` / `IBimInstance` с `LocationPoint` / `LocationCurve`, поворотом, уровнем), система параметров (`IParameterManager`, `ParameterSet`, Shared Parameters), геометрический слой (`IGeometryElement`: Solid / Mesh), аналитическая модель (`IAnalyticalModel`: линии балок, пластины перекрытий, узлы), отделённая от 3D-геометрии.

---

## 1. Текущее состояние ядра

Вся модель — один класс `Models/ArchitecturalElement.cs`: `sealed partial class ArchitecturalElement : ObservableObject`, 21 поле на все 20 типов элементов:

```csharp
public sealed partial class ArchitecturalElement : ObservableObject
{
    public partial string Id { get; set; } = NewId();          // "el_" + 10 hex от Guid
    public partial string Name { get; set; }
    public partial string Type { get; set; } = "wall";          // строка: column, beam, slab, wall, roof…
    public partial string BuildingGroupId { get; set; }
    public partial int LevelIndex { get; set; }
    public partial double[] Position { get; set; }              // центр [X, Y, Z], м
    public partial double[] Size { get; set; }                  // [W (локальная X), H (Y), D (локальная Z)], м
    public partial double[] Rotation { get; set; }              // [X, Y, Z], градусы
    public partial List<Point2D> Polygon2D { get; set; }        // контур в плане (локальный)
    public partial string? HostElementId { get; set; }          // проём → стена
    public partial int RepeatLevels { get; set; } = 1;          // один элемент = N этажей
    public partial double LevelHeight { get; set; } = 3.0;
    public partial double TwistAnglePerFloor { get; set; }
    public partial double TaperScalePerFloor { get; set; } = 1.0;
    public partial string Material { get; set; } = "concrete";  // свободная строка
    public partial bool HasPhysicsError { get; set; }
    public partial string ProfileType { get; set; }             // rectangle, circle, pipe, i_beam, polygon
    public partial double ProfileWallThickness { get; set; }
    public partial double ProfileWebThickness { get; set; }
    public partial string RoofShape { get; set; }               // flat, gable, hip, shed
    public partial int StepCount { get; set; }
    public partial List<MaterialLayer> Layers { get; set; }     // пирог IfcMaterialLayerSet
}
```

| Аспект | Сейчас | Аналог в Revit / Renga |
|---|---|---|
| Категория | строка `Type` (`"column"`, `"wall"`…) | `BuiltInCategory` (enum) |
| Идентичность | `Id = "el_" + 10 hex` от Guid, постоянного GUID нет | `ElementId` + `UniqueId` (GUID) |
| Положение | `Position` (центр) + `Size [W,H,D]` + `Rotation` | `LocationPoint` / `LocationCurve` / Sketch |
| Сечение, материал, пирог | в каждом экземпляре (`ProfileType`, `Material`-строка, `Layers`) | свойства типа (`ElementType`, `CompoundStructure`) |
| Этаж | `int LevelIndex`, отметки восстанавливаются из геометрии (`StructuralAxesBuilder.LevelElevations`, 10+ вызовов) | элемент `Level` + `BaseOffset` |
| Параметры | жёсткие C#-свойства, пользовательских нет | `ParameterSet`, `BuiltInParameter`, Shared Parameters |
| Геометрия | `ElementGeometryBuilder.Build` → сразу Helix `MeshGeometry3D` (SharpDX) | `GeometryElement` / `Solid`, независимые от рендера |
| Аналитика | приватный вложенный `Member` внутри `StructuralAnalysisService` (133 КБ), пересоздаётся при каждом расчёте | `AnalyticalMember` / `AnalyticalPanel` |
| Undo | снимок-клон всей сцены на каждый шаг (до 100) | `Transaction` с дельтами изменений |
| Хранилище | `SceneStateService.Elements` — `ObservableCollection<ArchitecturalElement>`, событие `SceneChanged` → полный `RebuildScene` вьюпорта | `Document` с индексами и `DocumentChanged(added, modified, deleted)` |
| Файл `.aia` | дамп полей класса (`SceneProjectFile`, v5) | схема параметров и типов |

**Связность кода (поиск по проекту):**

| Использование | Вхождений | Файлов |
|---|---|---|
| `ArchitecturalElement` | 737 | 55 |
| `.Width / .Height / .Depth` | 870 | 51 |
| `ElementTypes.*` | 1111 | 54 |
| `.Type == ElementTypes…` | 344 | 48 |
| `LevelIndex` | 208 | 32 |
| `Polygon2D` | 177 | 33 |
| `HostElementId` | 144 | 35 |
| `RepeatLevels` | 107 | 28 |
| `SetSize(` | 107 | 18 |
| `new ArchitecturalElement` | 48 | 13 |

**Вывод:** переписать всё за один заход нельзя. Безопасный путь — постепенная замена («Strangler Fig»): `ArchitecturalElement` остаётся фасадом, а данные под ним переезжают в новые структуры.

---

## 2. Узкие места на пути к уровню Revit / Renga

1. **Один класс на все категории.** У колонны есть `RoofShape` и `StepCount`, у стены — `ProfileWebThickness`. Модель не знает, какие параметры осмысленны для категории, поэтому сами параметры описать нельзя, а инспектор собирается вручную.
2. **Категория и материал — свободные строки.** Расчёт определяет материал через `ToLowerInvariant()` и `Contains("tile")`. Библиотеки материалов (E, Rb, плотность, внешний вид) нет, хотя нормативные данные уже есть в `StructuralNorms`.
3. **Нет типов.** Каждая из 200 колонн хранит свою копию «400×400, B25». Отсюда:
   - нельзя «изменить тип — поменять все К1»;
   - марки альбома ГОСТ (`MarkRegistry`) угадываются по совпадению значений;
   - в IFC нет `IfcColumnType` + `IfcRelDefinesByType`, которых ждут Renga и Revit;
   - спецификации группируются по числам, а не по типам.
4. **Положение задаётся как «центр + коробка».** У стены и балки нет `LocationCurve`. ИИ присылает `Start/End`, но они превращаются в центр и длину и теряются. Следствия: нет дуговых стен, наклон балки — через `Rotation X/Z`, узлы стен (`JoinWallCorners`) правят размеры, а не связи.
5. **Этажи не являются элементами.** Отметки вычисляются обратно из геометрии, гравитация солвера двигает элементы, а не держит привязку к этажу. Подъём этажа на 300 мм не передаётся элементам: зависимости «элемент → уровень» нет.
6. **`RepeatLevels` / `Twist` / `Taper`: один элемент — это N физических объектов.** IFC, сметы, марки, коллизии и армирование каждый по-своему разворачивают повторы (107 мест). В Revit это был бы генератор (массив / группа), порождающий отдельные экземпляры со своими Id.
7. **Геометрия завязана на Helix / SharpDX.** Ядро экспорта, разрезов и анализа зависит от библиотеки рендера, поэтому нельзя считать без WinUI (сервер, консольные тесты, отдельный пакет `Core`).
8. **Расчётная модель одноразовая и закрытая.** Её нельзя сохранить, нельзя задать шарниры, опоры или расчётные смещения, нельзя передать в ЛИРА / SCAD. Кроме того, `StructuralTopology` в верификации строит ту же топологию второй раз.
9. **Undo и уведомления работают на уровне всей сцены.** Каждый шаг клонирует все элементы, `SceneChanged` перестраивает весь вьюпорт. На 50 тыс. элементов это не масштабируется. Мелочь того же рода: `SetSelection` использует `elements.Where(Elements.Contains)` — квадратичная сложность.
10. **Связи элементов — только `HostElementId` (проём → стена).** Нет общей модели связей: стыковки, «верх стены к кровле», «плита опирается на стены», зависимость от осей.
11. **Модель зависит от UI.** `ObservableObject` из CommunityToolkit сидит прямо в `Models`, а формат `.aia` — дамп полей класса: любая перестановка полей меняет формат файла.

---

## 3. Целевая архитектура

```
BimDocument
 ├─ Elements: Dictionary<ElementId, IBimElement>   (+ индексы по категории, этажу, постройке, хосту)
 ├─ Types:    IBimElementType   — свойства «семейства»: сечение, материал, пирог, форма кровли
 ├─ Levels:   BimLevel          — Id, Guid, Name, Elevation, BuildingId
 ├─ Grids, Buildings, Materials, Dimensions — тоже IBimElement
 ├─ ParameterRegistry           — встроенные + общие (Guid) определения
 ├─ GeometryCache               — IGeometryElement по Id + штамп версии
 ├─ AnalyticalModel             — строится из документа, с сохраняемыми правками
 └─ TransactionManager          — записи изменений вместо снимков
```

- **`IBimElement`:** `ElementId Id` (строка `el_…` сохраняется, чтобы не ломать `.aia`, IFC Tag и Undo), `Guid UniqueId`, `Name`, `BimCategory Category`, `ParameterSet Parameters`.
- **`IBimInstance : IBimElement`:**
  - `TypeId`;
  - `Location` — один из вариантов: `LocationPoint` (колонна, окно), `LocationCurve` (стена, балка: Start/End, позже дуга), `SketchProfile` (плита, кровля, фундамент: Polygon2D);
  - `LevelId` + `BaseOffset`, `RotationY`, `HostId`.
- **`ParameterSet`:** типизированные значения (`StorageType`: Double / Int / String / ElementId / Bool) и единица (`SpecType`: длина, угол, площадь, объём; внутри — СИ, метры).
  - Встроенные параметры (`BuiltInParameter`) — **в массиве фиксированных слотов**, без словаря и без упаковки значений (boxing). Иначе не удержать КЭ-расчёт < 10 мс и совпадение «до бита».
  - Пользовательские и общие параметры — в словаре по `Guid` определения.

### Что относится к типу, а что к экземпляру

| Категория | Тип | Экземпляр |
|---|---|---|
| Колонна | профиль, b × h (W, D), толщины трубы / двутавра, материал | точка, поворот, этаж, высота (H), смещения низа / верха |
| Балка | профиль, сечение D × H, материал | кривая (длина = W), этаж, смещение |
| Стена | пирог → толщина D, материал | кривая (длина W), высота H, этаж, смещение |
| Плита / кровля / фундамент | пирог → толщина H, форма кровли | контур Polygon2D, этаж, смещение |
| Окно / дверь | W × H, заполнение | хост, смещение вдоль стены, подоконник |
| Ось, этаж, размер, постройка | — | собственные параметры |

---

## 4. План рефакторинга без поломки `AIA_DEBUG_SELFTEST`

**Решения ревью (2026-10-29):** фасад `ArchitecturalElement` на всё время миграции (Strangler Fig); `RepeatLevels` порождают реальные экземпляры со ссылкой на генератор; положение по Y — `LevelId` + `BaseOffset`, абсолютный Y кэшируется только для аналитики; IFC2X3 до конца фазы 6.

**Статус (2026-10-30):** фаза 2.5 завершена; фаза 3 (шаг 1: `BimLevel`, `BimLevelTable`, слот `LevelId`, вычисляемый `LevelIndex`, этажи в `.aia` и миграция из `LevelElevations`) и ядро фазы 4 (`IBimElementType` / `BimElementType`, сигнатура `BimTypeKey`, реестр `BimTypeRegistry`, «Дублировать тип» в сеттерах фасада, «Изменить тип», блок `types` в `.aia`) выполнены — 202 проверки, эталон перезаписан только из-за новых блоков `.aia` (без них тексты совпали до бита). Остаток фазы 3: солвер на ограничения «элемент → этаж»; остаток фазы 4: `IfcColumnType` / `IfcRelDefinesByType`, марки ГОСТ по типу, UI «Изменить тип», очистка неиспользуемых типов реестра.

**Статус до 2026-10-30:** фазы 0, 1 и 2 выполнены (191 проверка, эталон `SelfTest/selftest-baseline.json` совпадает до бита; при переходе на `.aia` v6 эталон перезаписан один раз — штамп `formatVersion` в тексте `.aia` нормализуется, как дата записи, все остальные части совпали с прежним эталоном до бита). Остаток фазы 2 — `AIA_UserParameters` в IFC, атрибуты DXF и колонки ведомости. Остаток фазы 0 — «замороженные» `.aia` v1–v4; остаток фазы 1 — `IBimElement` у `StructuralGridAxis` (нужен сохраняемый Id, т. е. `.aia` v6) и перевод оставшихся строковых сравнений (экспорт, импорт, солвер, верификаторы, UI).

**Общее правило:** каждая фаза заканчивается полным самотестом. Все 169 проверок должны пройти, а эталонные отпечатки из фазы 0 — совпасть до бита. `AiSceneDocument`, промпты и коды `AIA-*` не меняются: ИИ продолжает работать через фасад.

### Фаза 0. Страховочная сетка (обязательна перед любой правкой)
- Добавить в самотест раздел «Эталон ядра». Для каждого из 4 пресетов, `SCENE=frame` и `SCENE=council` снимаются отпечатки:
  - элементы после солвера, ключ — индекс + имя (Id у пресетов случайные);
  - КЭ-отпечаток (уже есть в «Финальном аудите»);
  - текст IFC / DXF / OBJ с заменой Id на порядковые номера;
  - смета и ведомость;
  - Round-Trip `.aia` v5.
- Эталон пишется один раз с текущей сборки в `selftest-baseline.json`, дальше — только сравнение.
- Положить в тесты «замороженные» файлы `.aia` v1–v5 — гарантия чтения старых проектов.

### Фаза 1. Идентичность и категории (поведение не меняется)
- Ввести `IBimElement` и реализовать его в `ArchitecturalElement`, `StructuralGridAxis`, `BuildingGroup`, `PinnedDimension`.
- Добавить постоянный `UniqueId`; для старых файлов он вычисляется детерминированно из `Id` (UUID v5), чтобы GUID в IFC не менялись между сохранениями.
- Ввести `enum BimCategory` и свойство `Category`, вычисляемое из строки `Type`. Строка остаётся тем, что сериализуется и что видит ИИ.
- 344 сравнения переводятся на `Category` / `ElementTypes.IsXxx` постепенно, файл за файлом (механическая замена).

### Фаза 2. Система параметров под фасадом
- `ParameterRegistry` (встроенные определения + общие из файла проекта) и `ParameterSet`.
- Свойства `Width`, `Material`, `ProfileType`… становятся тонкими обёртками над слотами `ParameterSet`. Генерацию `[ObservableProperty]` для них заменяет ручной `OnPropertyChanged`; внешне всё остаётся прежним.
- Хранимое значение `double` не меняется → расчёт совпадает до бита (это проверяет отпечаток фазы 0).
- `.aia` v6 добавляет блоки `parameterDefinitions` и `parameters` для пользовательских значений; поля v5 пишутся как раньше.
- Новые возможности фазы:
  - раздел инспектора «Свойства пользователя»;
  - пользовательские параметры в IFC в отдельном наборе `AIA_UserParameters` (параллельно с `AIA_Parameters`);
  - пользовательские параметры в DXF-атрибутах и ведомости.

### Фаза 3. Этажи как элементы
- Добавить `BimLevel` (по постройке). При загрузке старых файлов этажи создаются из `LevelElevations`.
- Экземпляр получает `LevelId`; `LevelIndex` становится вычисляемым (индекс этажа в отсортированном списке) — 208 мест использования не ломаются.
- На первом шаге `Position.Y` остаётся абсолютным, этаж — метаданными; аудитор проверяет `BaseY` = отметка этажа + смещение.
- Переход солвера на ограничения «элемент → этаж» — отдельный шаг с новыми проверками («поднять этаж на 300 мм → элементы следуют»).

### Фаза 4. Type-Instance
- `BimElementType` с подписью типа: категория + профиль + размеры типа + материал + пирог.
- Ключевое правило совместимости: старые сеттеры (`SetSize`, `ProfileType =`, `ApplyProfile`) меняют только этот экземпляр. Фасад вызывает `Document.ResolveType(signature)`: находит существующий тип или создаёт новый и переназначает `TypeId` (семантика Revit «дублировать тип»).
- Новая операция «Изменить тип» меняет все экземпляры типа — новый функционал с новыми проверками, существующие не затрагивает.
- Миграция v5 → v6: экземпляры с одинаковой подписью объединяются в типы; порядок и Id элементов не меняются.
- Марки ГОСТ (`MarkRegistry`) берутся из марки типа.
- IFC: `IfcColumnType` / `IfcBeamType` / `IfcWallType` + `IfcRelDefinesByType`; Round-Trip собственного экспорта читает тип обратно.
- Undo, клоны песочницы Совета и `DesignFixEngine` копируют и таблицу типов; утолщение плиты Советом пересобирает её тип.

### Фаза 5. Расчётная модель (`IAnalyticalModel`)
- Вынести `Member` / `SlabPlan` из `StructuralAnalysisService` в открытые `AnalyticalMember` (кривая), `AnalyticalPanel` (контур + отверстия) и `AnalyticalNode`; построитель — `AnalyticalModelBuilder`.
- Первый шаг — **чистое перемещение кода** с проверкой отпечатка «до бита» (последовательный и параллельный режимы).
- Затем `StructuralTopology` верификации и `ReinforcementCalculator` читают ту же модель, без дублирования.
- Позже: сохраняемые правки как параметры экземпляра (шарниры, опоры, расчётные смещения), экспорт в форматы МКЭ-комплексов.

### Фаза 6. Геометрический слой (`IGeometryElement`)
- `BimMesh` (`System.Numerics`, без SharpDX) и описания тел (`Extrusion` / `Sweep` / `Brep`); IFC-экспорт берёт экструзии прямо из описаний, без обратного разбора.
- `ElementGeometryBuilder` переходит на `BimMesh`, Helix получает его через адаптер `HelixMeshAdapter` в `Rendering`.
- Кэш геометрии по Id + версия; перестройка — только изменённых элементов и их зависимых (хост → проёмы, плита → отверстия, этаж → элементы) вместо полного `RebuildScene`.
- После этого ядро выносится в отдельную сборку `AIArchitectStudio.Core` без WinUI, а самотест ядра запускается консольно.

### Фаза 7. Транзакции с записью изменений
- `Transaction` собирает `ElementChange` (параметры до и после, добавлен или удалён); Undo применяет обратные изменения.
- Внешняя семантика `BeginUndoTransaction` / `Discard` и проверка «ровно 1 шаг Undo» сохраняются.
- Событие `DocumentChanged(added, modified, deleted)` заменяет широковещательный `SceneChanged`.
- **Статус (2026-10-30): выполнена.** `Models/Transactions/` (`BimTransaction`, `IElementChange`: `ParameterChange`, `TypeChange`, `LevelChange`, `ElementFieldChange`, `LayerContentChange`, `ElementAdded`/`ElementDeleted`/`ElementsCleared`; `TransactionManager`, `DocumentChangedEventArgs`), история 1000 шагов, шаг правки колонны ~570 Б против ~113 КБ снимка сцены «Каркас»; вьюпорт перестраивает только затронутые элементы; 217 проверок, эталон ядра совпал до бита без перезаписи. `SceneChanged` оставлен как производный сигнал для списков UI. Не сделано: этажи `BimLevelTable` вне журнала (как и при снимках), правки внутри `LoadScene` из песочницы Совета пишутся как «очистка + добавление» (ссылками), а не как поэлементный дифф по Id.

### Фаза 8. Дальше (уровень Revit)
Связи и стыковки, стадии (Phases), рабочие наборы и совместная работа (журнал изменений по элементам это делает возможным), редактор семейств с формулами, спецификации и фильтры видов по параметрам, IFC4 с типами.

---

## 5. Риски и чего не делать

- **Не делать `Dictionary<string, object>` для встроенных параметров.** Упаковка значений и поиск по строке в горячих циклах КЭ-расчёта и геометрии убьют норматив < 10 мс.
- **Не переименовывать JSON-поля v5 и `Id`.** От них зависят IFC Round-Trip, `AIA_Parameters`, проверки самотеста и история Undo.
- **Не переводить солвер на привязки к этажам одновременно с введением `BimLevel`.** Это два разных шага, иначе нельзя понять, откуда пришло расхождение.
- **Главный источник поломок — пересборка `ElementGeometryBuilder` в фазе 6.** Отпечаток экспорта из фазы 0 тут обязателен.
- **Порядок:** фазы 0–2 почти бесплатны по риску и сразу дают пользовательские параметры. Фаза 4 — самая большая по объёму. Фазы 5 и 6 открывают путь к МКЭ-комплексам и отдельному ядру.

---

## 6. Вопросы для ревью

1. Оправдан ли фасад `ArchitecturalElement` поверх `BimDocument` на время миграции, или лучше сразу ввести параллельную иерархию классов по категориям (`BimColumn`, `BimWall`…)?
2. Как лучше представить `RepeatLevels` / `Twist` / `Taper` в модели Type-Instance: генератор с виртуальными экземплярами (без хранения) или реальные экземпляры со ссылкой на генератор?
3. Хранить ли `Position.Y` абсолютно или как `Level.Elevation + BaseOffset` после фазы 3 — и как при этом сохранить совпадение КЭ-расчёта «до бита»?
4. Стоит ли сразу целиться в IFC4 (`IfcRelDefinesByType`, `IfcStructuralAnalysisModel`) или оставаться на IFC2X3 ради совместимости с Renga?
