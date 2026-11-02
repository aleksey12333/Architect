---
name: helix-winui-3d
description: Правила работы с 3D-движком HelixToolkit.WinUI, SharpDX.Core, камерами, PhongMaterials, 2D/3D видами, координатными осями и 3D-Gizmo в WinUI 3. Загружай при работе с Viewport3DControl и 3D-геометрией.
---
# Стандарты HelixToolkit.WinUI (SharpDX)
1. Используй пространства имен `HelixToolkit.WinUI` и `HelixToolkit.SharpDX.Core`. Не используй классы из `HelixToolkit.Wpf`.
2. Создавай `Viewport3DX` и `EffectsManager = new DefaultEffectsManager()` строго программно в событии `Loaded` контрола (защита от COM-ошибки `0xc000027b`).
3. Для геометрии используй `MeshBuilder` (`AddBox`, `AddCylinder`, `AddPolygon`, `AddExtrudedGeometry`, `ToMeshGeometry3D()`), оборачивая элементы в `MeshGeometryModel3D` / `SceneNode` с материалами `PhongMaterials`.
4. Вектор верха всегда Y-Up: `ModelUpDirection = new Vector3(0, 1, 0)`. Земля на `Y = 0`.
5. В обработчике кликов мыши по 3D (`OnMouse3DDown`) не блокируй глобально `e.Handled = true`, чтобы сохранялась работа `ViewCube` и вращения камеры `Trackball`.