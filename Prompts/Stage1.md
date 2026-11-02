Прочитай CLAUDE.md. Создай проект `AIArchitectStudio` с нуля в текущей папке (не трогая папку `LocalModels/`).
Выполни **Этап 1 (Ядро BIM-данных, Движок 5 тем оформления и Студийный интерфейс)**:

1. Создай `.sln` и `AIArchitectStudio.csproj` (WinUI 3, .NET 8, `WindowsPackageType=None`, `UseWinUI=true`).
   - Подключи пакеты: `HelixToolkit.WinUI`, `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `LLamaSharp`, `LLamaSharp.Backend.DirectML`.
   - Исключи `LocalModels/**` из копирования в Output (`CopyToOutputDirectory = Never`).

2. Создай расширенные BIM-модели в `Models/`:
   - `ArchitecturalElement.cs`:
     - Поля: `Id`, `Name`, `Type` (`wall`, `curtain_wall`, `column`, `core`, `slab`, `cantilever_slab`, `shell`, `beam`, `truss`, `diagrid`, `roof`, `foundation`, `stairs`, `window`, `door`, `pool`, `terrace`, `asset`), `BuildingGroupId` (например, `"MainHouse"`, `"BathhousePool"`), `LevelIndex` (этаж: 0, 1, 2...).
     - Геометрия: `Position[3]`, `Size[3]`, `Rotation[3]`, список точек контура `Polygon2D` (для уникальных и консольных перекрытий), `HostElementId` (ID стены-родителя для окон и дверей).
     - Параметрические директивы для высоток: `RepeatLevels`, `LevelHeight`, `TwistAnglePerFloor` (закручивание башни в градусах на этаж), `TaperScalePerFloor` (сужение кверху).
   - `BuildingGroup.cs`, `BimSpecification.cs`, `SolverReport.cs`, `ChatMessageModel.cs`, `SceneProjectFile.cs`.

3. Создай сервисы ядра в `Services/`:
   - `SettingsService.cs`: хранение Gemini API Key, темы, языка (`RU`/`EN`), выбранной модели и базовых расценок.
   - `ThemeService.cs`: мгновенное переключение 5 тем оформления (`Studio Dark`, `Architectural Daylight`, `Blueprint CAD`, `Obsidian Gold`, `Nordic Slate`).
   - `LocalizationService.cs`: динамический перевод `RU` (по умолчанию) / `EN`.
   - `SceneStateService.cs`: хранит все элементы, список `BuildingGroups`, фильтр этажа (`ActiveLevelFilter`), `SelectedElement`, стеки `Undo/Redo` и логику зависимого перемещения (при сдвиге стены автоматически сдвигаются все её окна и двери с `HostElementId == wall.Id`).

4. Создай минималистичный студийный интерфейс в `MainWindow.xaml`:
   - **Кастомный TitleBar (`48px`):**
     - Слева: логотип `AI ARCHITECT STUDIO` + переключатель вида `[3D Перспектива | 2D План (AutoCAD) | Фасад/Разрез]`.
     - По центру: капсула инструментов: `Проект (.aia)`, `Экспорт [IFC / DXF / OBJ]`, `Из картинки [2D План | Эскиз | Фото здания]`, `Undo`, `Redo`, `Оси А-1`, `Каркас`.
     - Справа: фильтр этажа (`Все этажи` / `Этаж 1..N`), выбор темы (`5 тем`), язык `RU/EN`, `Настройки`.
   - **Рабочая зона:**
     - Центр: контейнер 3D/2D вьюпорта + плавающая панель быстрого действия при выделении объекта.
     - Правая панель (`390px`) с 3 вкладками:
       1) **ИИ-Проектировщик:** выбор модели ИИ, режим (`[Новая сцена]` / `[+ Добавить постройку на участок]`), быстрые пресеты, лента чата с отчетом C#-солвера, `ErrorInfoBar` с кнопкой `«Код ошибки»`, поле ввода.
       2) **Дерево BIM & Инспектор:** дерево по группам построек и этажам + точный `Transform Inspector` (`Pos X/Y/Z`, `Size W/H/D`, `Rotation Y`, `Level`, `Material`) с мгновенным откликом.
       3) **BIM-Смета:** карточки объемов бетона, остекления, площади застройки, этажности и стоимости по каждой постройке и всему участку.

5. Проверь сборку `dotnet build --nologo -v q` и напиши краткий отчет на русском языке.