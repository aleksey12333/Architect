---
name: bim-cad-export
description: Спецификация экспорта 3D/2D геометрии в форматы IFC (Renga, Revit), DXF (AutoCAD) и Wavefront OBJ. Загружай при реализации CadExportService.
---
# Правила экспорта BIM / CAD
1. **DXF (AutoCAD):** Формируй валидные секции `HEADER`, `TABLES` (слои `WALLS`, `COLUMNS`, `SLABS`, `WINDOWS`, `DOORS`, `AXES`), `ENTITIES` (`LINE` для 2D-осей и контуров, `3DFACE` для 3D-объемов) и `EOF`. Все числа с плавающей точкой записывай через `CultureInfo.InvariantCulture`.
2. **IFC (Renga / Revit):** Формируй текстовый файл `ISO-10303-21;` (`IFC2X3`) с иерархией `IFCPROJECT` -> `IFCSITE` -> `IFCBUILDING` -> `IFCBUILDINGSTOREY` (по этажам `LevelIndex`) -> элементы (`IFCWALLSTANDARDCASE`, `IFCSLAB`, `IFCCOLUMN`, `IFCWINDOW`, `IFCDOOR`) с геометрией `IFCEXTRUDEDAREASOLID`.
3. **OBJ + MTL:** Записывай вершины (`v`), нормали (`vn`) и полигоны (`f`) с учетом мировых координат и масштаба каждого элемента.