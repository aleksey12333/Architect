#if DEBUG
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.Pdf;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест финальной полировки: групповой поворот вокруг общего центра, вершины Polygon2D, коллизии прогулки, регион Солнца
/// и инсоляция по СанПиН, Font Subsetting и альбом ГОСТ из 3 листов, окно верификации плоского DXF, локализация подписей чата.
/// </summary>
public static partial class CadSelfTestService
{
    private static bool Check(StringBuilder report, ref int failures, bool ok, string text)
    {
        if (!ok) failures++;
        report.AppendLine($"  {(ok ? "OK" : "ОШИБКА")}: {text}");
        return ok;
    }

    // ═════════════════════════════ 1. Групповой поворот ═════════════════════════════

    /// <summary>
    /// Колонны, балка и стена с окном и дверью поворачиваются на 37° как один жёсткий блок: центры — по формуле вращения вокруг
    /// центра габарита группы, собственный поворот — +37°, попарные расстояния сохраняются, окно остаётся в стене; 1 шаг Undo;
    /// 4 × 90° возвращают группу на место.
    /// </summary>
    private static int CheckGroupRotation(StringBuilder report)
    {
        int failures = 0;
        var scene = FrameSceneState();
        string[] names = ["Колонна 1-11", "Колонна 1-21", "Колонна 1-12", "Балка 1-Б1", "Стена по оси А"];
        var group = names.Select(n => scene.Elements.First(e => e.Name == n)).ToList();
        var ids = group.Select(e => e.Id).ToList();
        var wall = group[^1];
        var window = scene.Elements.First(e => e.Name == "Окно ОК-1");
        var other = scene.Elements.First(e => e.Name == "Колонна 1-43");
        var before = scene.Elements.ToDictionary(e => e.Id, e => (e.X, e.Y, e.Z, e.RotationY));
        var (lwx, lwz) = ArchitecturalElement.RotateY(window.X - wall.X, window.Z - wall.Z, -wall.RotationY);
        var pivot = SceneStateService.GroupPivot(group);
        int depth = scene.UndoDepth;
        scene.SetSelection(group, group[0]);

        const double angle = 37;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var used = scene.RotateGroup(scene.SelectedElements.ToList(), angle);
        sw.Stop();
        double posErr = 0, rotErr = 0, distErr = 0, moved = 0;
        foreach (var e in group)
        {
            var b = before[e.Id];
            var (rx, rz) = ArchitecturalElement.RotateY(b.X - pivot.X, b.Z - pivot.Z, angle);
            posErr = Math.Max(posErr, Math.Abs(e.X - (pivot.X + rx)) + Math.Abs(e.Z - (pivot.Z + rz)) + Math.Abs(e.Y - b.Y));
            rotErr = Math.Max(rotErr, Math.Abs(ArchitecturalElement.NormalizeAngle(e.RotationY - b.RotationY - angle)));
            moved = Math.Max(moved, Math.Sqrt((e.X - b.X) * (e.X - b.X) + (e.Z - b.Z) * (e.Z - b.Z)));
        }
        for (int i = 0; i < group.Count; i++)
            for (int j = i + 1; j < group.Count; j++)
            {
                var (a0, b0) = (before[group[i].Id], before[group[j].Id]);
                double d0 = Math.Sqrt((a0.X - b0.X) * (a0.X - b0.X) + (a0.Z - b0.Z) * (a0.Z - b0.Z));
                double d1 = Math.Sqrt((group[i].X - group[j].X) * (group[i].X - group[j].X) + (group[i].Z - group[j].Z) * (group[i].Z - group[j].Z));
                distErr = Math.Max(distErr, Math.Abs(d1 - d0));
            }
        var (awx, awz) = ArchitecturalElement.RotateY(window.X - wall.X, window.Z - wall.Z, -wall.RotationY);
        bool windowOk = Math.Abs(awx - lwx) + Math.Abs(awz - lwz) < 1e-9 && Math.Abs(ArchitecturalElement.NormalizeAngle(window.RotationY - before[window.Id].RotationY - angle)) < 1e-9;
        bool otherOk = other.X == before[other.Id].X && other.Z == before[other.Id].Z && other.RotationY == before[other.Id].RotationY;
        Check(report, ref failures, posErr < 1e-9 && rotErr < 1e-9 && used == pivot && moved > 1.0,
            $"поворот группы из {group.Count} элементов на {angle:0}° вокруг общего центра ({pivot.X:0.000}; {pivot.Z:0.000}): отклонение центров от x' = P + R(δ)(x − P) " +
            $"{posErr:0.0e+0} м, собственных углов {rotErr:0.0e+0}°, наибольшее смещение центра {moved:0.00} м, {sw.Elapsed.TotalMilliseconds:0.00} мс");
        Check(report, ref failures, distErr < 1e-9 && windowOk && otherOk,
            $"жёсткий блок: попарные расстояния сохранены (Δ {distErr:0.0e+0} м), окно стены на месте в её системе, невыделенная колонна не сдвинута");
        Check(report, ref failures, scene.UndoDepth == depth + 1, $"один шаг Undo на весь поворот группы ({scene.UndoDepth - depth})");

        scene.Undo();
        bool undoOk = ids.Select(scene.FindById).All(e => e is not null && Math.Abs(e.X - before[e.Id].X) + Math.Abs(e.Z - before[e.Id].Z) < 1e-12 && e.RotationY == before[e.Id].RotationY);
        Check(report, ref failures, undoOk, "Undo вернул исходные центры и углы всех элементов группы");

        var fresh = ids.Select(id => scene.FindById(id)!).ToList();
        for (int k = 0; k < 4; k++) scene.RotateGroup(fresh, 90);
        double loopErr = fresh.Max(e => Math.Abs(e.X - before[e.Id].X) + Math.Abs(e.Z - before[e.Id].Z) + Math.Abs(ArchitecturalElement.NormalizeAngle(e.RotationY - before[e.Id].RotationY)));
        Check(report, ref failures, loopErr < 1e-9, $"4 × 90° (кнопка «Повернуть на 90°») возвращают группу на место: отклонение {loopErr:0.0e+0}");
        return failures;
    }

    // ═════════════════════════════ 2. Вершины Polygon2D ═════════════════════════════

    /// <summary>
    /// Терраса у бассейна (П-образный контур из 8 вершин): перенос вершины +600 мм — площадь +2.40 м² ровно, W 9000 → 9600 мм;
    /// вставка вершины на ребре и её удаление; самопересечение отклоняется; OSnap вершины полигональной плиты к узлу осей 2/Б.
    /// </summary>
    private static int CheckContourVertices(StringBuilder report)
    {
        int failures = 0;
        var demo = PresetScenesService.Create(PresetScenesService.Cottage, LocalizationService.Russian);
        var scene = new SceneStateService();
        scene.LoadScene(demo.Elements, demo.Groups, recordUndo: false, clearHistory: true);
        var deck = scene.Elements.First(e => e.Name == "Терраса у бассейна");
        double area0 = deck.PlanArea(), w0 = deck.Width;
        int depth = scene.UndoDepth;
        var v1 = SceneStateService.WorldVertices(deck)[1];
        bool moved = scene.MoveVertex(deck, 1, v1.X + 0.6, v1.Z);
        double area1 = deck.PlanArea();
        Check(report, ref failures, moved && Math.Abs(area1 - area0 - 2.4) < 1e-9 && Math.Abs(deck.Width - (w0 + 0.6)) < 1e-9 && scene.UndoDepth == depth + 1 && SceneStateService.IsContourEditable(deck),
            $"перенос вершины 2 террасы на +600 мм по X: площадь {area0:0.00} → {area1:0.00} м² (+{area1 - area0:0.00}), W {w0 * 1000:0} → {deck.Width * 1000:0} мм, шагов Undo {scene.UndoDepth - depth}");

        int count = deck.Polygon2D.Count;
        int inserted = scene.InsertVertex(deck, 0);
        var mid = deck.Polygon2D[Math.Max(0, inserted)];
        var a = deck.Polygon2D[0];
        var b = deck.Polygon2D[(inserted + 1) % deck.Polygon2D.Count];
        bool insertOk = inserted == 1 && deck.Polygon2D.Count == count + 1 && Math.Abs(mid.X - (a.X + b.X) / 2) < 1e-9 && Math.Abs(mid.Z - (a.Z + b.Z) / 2) < 1e-9 && Math.Abs(deck.PlanArea() - area1) < 1e-9;
        bool removeOk = scene.RemoveVertex(deck, inserted) && deck.Polygon2D.Count == count && Math.Abs(deck.PlanArea() - area1) < 1e-9;
        Check(report, ref failures, insertOk && removeOk && scene.UndoDepth == depth + 3,
            $"вставка вершины в середине ребра 1 → {count + 1} вершин (площадь не меняется), удаление → {deck.Polygon2D.Count}; каждая операция — шаг Undo");

        var bad = SceneStateService.WorldVertices(deck)[0];
        int depthBad = scene.UndoDepth;
        var far = SceneStateService.WorldVertices(deck)[3];
        bool rejected = !scene.MoveVertex(deck, 0, far.X + 3, far.Z + 3) && SceneStateService.WorldVertices(deck)[0] == bad && scene.UndoDepth == depthBad;
        var triangle = new ArchitecturalElement { Type = ElementTypes.Slab, Name = "Тест: треугольная плита", Polygon2D = [new(0, 0), new(4, 0), new(0, 3)] };
        triangle.SetSize(4, 0.2, 3);
        scene.AddElement(triangle, recordUndo: false);
        bool minOk = !scene.RemoveVertex(triangle, 0) && triangle.Polygon2D.Count == 3;
        Check(report, ref failures, rejected && minOk, "самопересекающийся контур отклонён без шага Undo; у треугольника вершину удалить нельзя (минимум 3)");

        scene.Undo();
        scene.Undo();
        scene.Undo();
        var restored = scene.Elements.First(e => e.Name == "Терраса у бассейна");
        Check(report, ref failures, Math.Abs(restored.PlanArea() - area0) < 1e-9 && restored.Polygon2D.Count == count,
            $"три Undo вернули исходный контур ({restored.Polygon2D.Count} вершин, {restored.PlanArea():0.00} м²)");

        // OSnap: вершина L-образной плиты каркаса ведётся к узлу осей 2/Б.
        var frame = FrameSceneState();
        var slab = new ArchitecturalElement
        {
            Id = "el_test_poly_slab", Type = ElementTypes.Slab, Name = "Тест: полигональная плита", BuildingGroupId = FrameGroup, Material = "concrete",
            Polygon2D = [new(-9, -6), new(3, -6), new(3, 0), new(9, 0), new(9, 6), new(-9, 6)]
        };
        slab.SetSize(18, 0.2, 12);
        slab.SetPosition(0, 2.73, 0);
        frame.AddElement(slab, recordUndo: false);
        var targets = SmartSnapService.BuildTargets(frame.Elements.ToList(), frame.GridAxes.ToList(), new HashSet<string> { slab.Id });
        var hit = SmartSnapService.SnapPoint(-2.88, slab.TopY, 0.14, targets);
        bool snapOk = hit is { Target.Kind: SnapKind.GridIntersection } && hit.Label == "Ось 2 / Б" && Math.Abs(hit.Contact.X + 3) < 1e-9 && Math.Abs(hit.Contact.Z) < 1e-9 &&
                      frame.MoveVertex(slab, 2, hit.Contact.X, hit.Contact.Z) && SceneStateService.WorldVertices(slab)[2] == (-3.0, 0.0);
        var geometry = ElementGeometryBuilder.Build(slab, frame.GetOpeningsFor(slab), new GeometryContext(SceneStateService.AllLevels, 0, 0));
        var free = SmartSnapService.SnapPoint(-5.5, slab.TopY, 2.9, targets);
        Check(report, ref failures, snapOk && !geometry.IsEmpty && free is null,
            $"OSnap вершины: из (−2.880; 0.140) — «{hit?.Label ?? "нет"}» → ({hit?.Contact.X:0.000}; {hit?.Contact.Z:0.000}), меш перестроен; вдали от целей — без привязки ({free?.Label ?? "нет"})");
        return failures;
    }

    // ═════════════════════════════ 3. Коллизии прогулки ═════════════════════════════

    /// <summary>
    /// Сфера R = 0.25 м: глухая стена по оси А останавливает на 0.25 м от грани, дверь Д-1 и пустой проём в пол пропускают, окно
    /// и проём с подоконником 0.8 м — нет, колонны (прямоугольная и круглая) останавливают, вдоль стены — скольжение.
    /// </summary>
    private static int CheckWalkCollision(StringBuilder report)
    {
        int failures = 0;
        var (elements, _, _) = CreateFrameScene();
        // Тестовая перегородка со сквозным проёмом в пол (wall_opening 1200 × 2200 мм).
        var partition = new ArchitecturalElement { Id = "el_test_walk_wall", Type = ElementTypes.Wall, Name = "Тест: перегородка", BuildingGroupId = FrameGroup, Material = "block" };
        partition.SetSize(6, 2.65, 0.12);
        partition.SetPosition(13, -0.02 + 1.325, -12);
        var gap = new ArchitecturalElement
        {
            Id = "el_test_walk_gap", Type = ElementTypes.WallOpening, Name = "Тест: проём в пол", BuildingGroupId = FrameGroup, Material = "void", HostElementId = partition.Id
        };
        gap.SetSize(1.2, 2.2, 0.12 + ParametricSolverService.OpeningClearance);
        gap.SetPosition(13, -0.02 + 1.1, -12);
        elements.Add(partition);
        elements.Add(gap);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var obstacles = WalkCollisionService.BuildObstacles(elements);
        sw.Stop();
        const double feet = -0.02, r = WalkCollisionService.PlayerRadius;
        report.AppendLine($"  Препятствий: {obstacles.Count} (стены по участкам между проёмами, колонны, ядро), {sw.Elapsed.TotalMilliseconds:0.0} мс");

        var wallHit = WalkCollisionService.Move(obstacles, 0, 4.5, 0, 3, feet);
        double wallStop = 6.15 - 0.15 - r;
        Check(report, ref failures, wallHit.Blocked && Math.Abs(wallHit.Z - wallStop) < 1e-6 && !WalkCollisionService.Overlaps(obstacles, wallHit.X, wallHit.Z, feet),
            $"глухая стена по оси А: упор на z = {wallHit.Z:0.000} (грань 6.000 − R 0.250 = {wallStop:0.000}), сфера не пересекает стену");

        var door = WalkCollisionService.Move(obstacles, -5.4, 4.5, 0, 4, feet);
        Check(report, ref failures, !door.Blocked && Math.Abs(door.Z - 8.5) < 1e-6,
            $"дверь Д-1 (1000 мм): проход насквозь — z 4.500 → {door.Z:0.000}");

        var window = WalkCollisionService.Move(obstacles, 2.2, 4.5, 0, 4, feet);
        Check(report, ref failures, window.Blocked && window.Z < 6.0 - r + 1e-6, $"окно ОК-1: упор в подоконную часть на z = {window.Z:0.000}");

        var floorGap = WalkCollisionService.Move(obstacles, 13, -10.5, 0, -3, feet);
        var beside = WalkCollisionService.Move(obstacles, 11, -10.5, 0, -3, feet);
        var sill = WalkCollisionService.Move(obstacles, -7.5, -1.5, -3, 0, feet);
        Check(report, ref failures, !floorGap.Blocked && Math.Abs(floorGap.Z + 13.5) < 1e-6 && beside.Blocked && sill.Blocked && sill.X > -9.0 - 1e-6,
            $"wall_opening в пол: проход z −10.500 → {floorGap.Z:0.000}; рядом — упор (z = {beside.Z:0.000}); проём П-1 с подоконником 800 мм — упор (x = {sill.X:0.000})");

        var column = WalkCollisionService.Move(obstacles, -3, -2, 0, 4, feet);
        var round = WalkCollisionService.Move(obstacles, -7, 0, -3, 0, feet);
        Check(report, ref failures, column.Blocked && Math.Abs(column.Z - (-0.2 - r)) < 1e-6 && round.Blocked && Math.Abs(round.X - (-9 + 0.225 + r)) < 1e-6,
            $"колонна 400 × 600 (повёрнута): упор на z = {column.Z:0.000}; круглая Ø450: упор на x = {round.X:0.000} (центр −9.000 + 0.225 + R)");

        var slide = WalkCollisionService.Move(obstacles, 0, 5.5, 1.0, 1.0, feet);
        Check(report, ref failures, slide.Blocked && Math.Abs(slide.Z - wallStop) < 1e-6 && slide.X > 0.95,
            $"скольжение вдоль стены по диагонали: x 0.000 → {slide.X:0.000}, z прижат к {slide.Z:0.000}");

        // Производительность: башня (повторы этажей, закрутка) — препятствия и 1000 шагов WASD.
        var tower = PresetScenesService.Create(PresetScenesService.Tower, LocalizationService.Russian);
        sw.Restart();
        var towerObstacles = WalkCollisionService.BuildObstacles(tower.Elements);
        double build = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        double x = 0, z = 0;
        for (int i = 0; i < 1000; i++) (x, z, _) = WalkCollisionService.Move(towerObstacles, x, z, 0.03 * Math.Cos(i * 0.01), 0.03 * Math.Sin(i * 0.01), 0.0);
        Check(report, ref failures, sw.Elapsed.TotalMilliseconds < 500,
            $"«{tower.Title}»: препятствий {towerObstacles.Count} за {build:0.0} мс, 1000 шагов прогулки — {sw.Elapsed.TotalMilliseconds:0.0} мс");
        return failures;
    }

    // ═════════════════════════════ 4. Регион и инсоляция ═════════════════════════════

    /// <summary>
    /// Солнце по региону проекта (Москва / Сочи / Владивосток / Южно-Сахалинск / Мурманск): местное время кульминации и высота
    /// в равноденствие; зоны и нормы СанПиН; окно на юг без затенения выполняет норму, за 40-метровым корпусом — нет.
    /// </summary>
    private static int CheckInsolation(StringBuilder report)
    {
        int failures = 0;
        foreach (var id in new[] { "moscow", "sochi", "vladivostok", "yuzhno_sakhalinsk" })
        {
            var loc = SolarLocations.Resolve(id);
            double bestT = 0, bestAlt = -90;
            for (double t = 8; t <= 18; t += 1.0 / 120)
            {
                var s = SolarShadowService.Compute(t, SolarSeason.Equinox, loc);
                if (s.AltitudeDeg > bestAlt) { bestAlt = s.AltitudeDeg; bestT = t; }
            }
            double noon = 12 - (loc.Longitude - loc.UtcOffset * 15) / 15;
            Check(report, ref failures, Math.Abs(bestT - noon) < 0.02 && Math.Abs(bestAlt - (90 - loc.Latitude)) < 0.05,
                $"{loc.NameRu} ({loc.Coordinates(true)}): кульминация в {SolarShadowService.TimeText(bestT)} (расчёт {SolarShadowService.TimeText(noon)}), высота {bestAlt:0.00}° = 90° − φ");
        }
        var zones = new[] { ("moscow", InsolationZone.Central, 2.0), ("sochi", InsolationZone.South, 1.5), ("murmansk", InsolationZone.North, 2.5), ("vladivostok", InsolationZone.South, 1.5) }
            .Select(z => (z, InsolationService.NormFor(SolarLocations.Resolve(z.Item1).Latitude))).ToList();
        Check(report, ref failures, zones.All(p => p.Item2.Zone == p.z.Item2 && p.Item2.RequiredHours == p.z.Item3),
            "зоны СанПиН 1.2.3685-21: " + string.Join("; ", zones.Select(p => $"{SolarLocations.Resolve(p.z.Item1).NameRu} — {p.Item2.ZoneTitle(true)}, {p.Item2.RequiredHours:0.0} ч с {p.Item2.DateTitle(true)}")));

        // Окно на юг (+Z) в стене 6 м, перекрытие этажа — с северной стороны.
        var scene = new List<ArchitecturalElement>();
        var wall = new ArchitecturalElement { Id = "ins_wall", Type = ElementTypes.Wall, Name = "Тест: южная стена", Material = "brick" };
        wall.SetSize(6, 3, 0.3);
        wall.SetPosition(0, 1.5, 0);
        var floor = new ArchitecturalElement { Id = "ins_floor", Type = ElementTypes.Slab, Name = "Тест: перекрытие" };
        floor.SetSize(6, 0.2, 6);
        floor.SetPosition(0, -0.1, -3.15);
        var win = new ArchitecturalElement { Id = "ins_window", Type = ElementTypes.Window, Name = "Тест: окно на юг", Material = "glass", HostElementId = wall.Id };
        win.SetSize(1.5, 1.5, 0.36);
        win.SetPosition(0, 0.9 + 0.75, 0);
        scene.AddRange([wall, floor, win]);
        var moscow = SolarLocations.Resolve("moscow");
        var open = InsolationService.Analyze(scene, [win], moscow);
        var w0 = open.Windows[0];
        Check(report, ref failures, w0.Passes && w0.ContinuousHours > 6 && w0.Normal.Z > 0.99 && w0.Intervals.Count == 1,
            $"окно на юг без затенения, Москва, {open.Norm.DateTitle(true)}: непрерывно {InsolationService.HoursText(w0.ContinuousHours, true)} " +
            $"({SolarShadowService.TimeText(w0.Intervals[0].Start)}–{SolarShadowService.TimeText(w0.Intervals[0].End)}), наружу — на юг, норма {InsolationService.HoursText(open.Norm.RequiredHours, true)} выполнена, {open.ElapsedMilliseconds:0.0} мс");

        var block = new ArchitecturalElement { Id = "ins_block", Type = ElementTypes.Wall, Name = "Тест: корпус 40 м", Material = "concrete" };
        block.SetSize(80, 40, 10);
        block.SetPosition(0, 20, 20);
        var shaded = InsolationService.Analyze([.. scene, block], [win], moscow).Windows[0];
        Check(report, ref failures, !shaded.Passes && shaded.ContinuousHours < 0.5,
            $"тот же проём за корпусом 80 × 40 м в 15 м к югу: непрерывно {InsolationService.HoursText(shaded.ContinuousHours, true)} — норма не выполнена");

        var sochi = InsolationService.Analyze(scene, [win], SolarLocations.Resolve("sochi"));
        var vlad = InsolationService.Analyze(scene, [win], SolarLocations.Resolve("vladivostok"));
        Check(report, ref failures, sochi.Norm.Zone == InsolationZone.South && sochi.Windows[0].Passes && Math.Abs(vlad.Sunrise - sochi.Sunrise) > 0.3,
            $"регион влияет на расчёт: Сочи ({sochi.Norm.DateTitle(true)}) восход {SolarShadowService.TimeText(sochi.Sunrise)}, непрерывно {InsolationService.HoursText(sochi.Windows[0].ContinuousHours, true)}; " +
            $"Владивосток — восход {SolarShadowService.TimeText(vlad.Sunrise)}");

        var cottage = PresetScenesService.Create(PresetScenesService.Cottage, LocalizationService.Russian);
        var windows = cottage.Elements.Where(e => e.Type == ElementTypes.Window).ToList();
        var all = InsolationService.Analyze(cottage.Elements, windows, moscow);
        Check(report, ref failures, all.Windows.Count == windows.Count && all.ElapsedMilliseconds < 3000,
            $"«{cottage.Title}»: окон {all.Windows.Count}, норма выполнена — {all.PassCount}, нет — {all.FailCount}, {all.ElapsedMilliseconds:0} мс");
        return failures;
    }

    // ═════════════════════════════ 5. Font Subsetting и альбом ГОСТ ═════════════════════════════

    /// <summary>
    /// Подмножество шрифта: только использованные глифы (cmap и ширины совпадают с исходным шрифтом), лист А3 с подмножеством
    /// в разы меньше листа с полным шрифтом; альбом из 3 листов: план 1:100, разрез с отметками, спецификация и ведомость стали.
    /// </summary>
    private static async Task<int> CheckAlbumAndFontAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        var font = TrueTypeFont.LoadSystem("isocpeur.ttf", "ISOCPEUR.TTF", "arial.ttf", "segoeui.ttf", "tahoma.ttf");
        const string sample = "План этажа на отм. +2,830 · К1 Б2 П1 Ø400 ±0,000 АБВГДЕЁЖЗИЙ 0123456789";
        var used = new Dictionary<ushort, int>();
        foreach (var rune in sample.EnumerateRunes()) used.TryAdd(font.Glyph(rune.Value), rune.Value);
        var subset = TrueTypeSubsetter.Subset(font.Data, used, font.PostScriptName);
        var parsed = TrueTypeFont.Parse(subset.Data, "subset");
        bool mapOk = sample.EnumerateRunes().All(r => parsed.Glyph(r.Value) == font.Glyph(r.Value) && parsed.Advance(font.Glyph(r.Value)) == font.Advance(font.Glyph(r.Value)));
        Check(report, ref failures, mapOk && subset.Data.Length * (font.Data.Length > 300_000 ? 8 : 2) < font.Data.Length && subset.Tag.Length == 6 && subset.Tag.All(char.IsAsciiLetterUpper),
            $"подмножество «{subset.Tag}+{font.PostScriptName}»: глифов {subset.GlyphCount} (с .notdef и компонентами), {subset.Data.Length / 1024.0:0.0} КБ из {font.Data.Length / 1024.0:0} КБ " +
            $"(в {(double)font.Data.Length / subset.Data.Length:0} раз меньше), cmap и ширины совпадают с исходным шрифтом");

        var (elements, groups, axes) = CreateFrameScene();
        var plan = new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.FloorPlan, elements, groups, axes, "Самопроверка · каркасный корпус", FrameGroup, 0, null, null);
        string subsetPath = Path.Combine(directory, "gost_plan_A3.pdf"), fullPath = Path.Combine(directory, "gost_plan_A3_fullfont.pdf");
        var sub = GostDrawingPdfExporter.Export(plan, subsetPath);
        var full = GostDrawingPdfExporter.Export(plan, fullPath, subsetFont: false);
        long subBytes = new FileInfo(subsetPath).Length, fullBytes = new FileInfo(fullPath).Length;
        // Arial (~1 МБ) сжимается в 8+ раз; компактный ISOCPEUR (десятки КБ) — заметно меньше, но тоже кратно.
        bool bigFont = font.Data.Length > 300_000;
        Check(report, ref failures, sub.Pdf is { } info && info.FontReduction >= (bigFont ? 8 : 2) && (!bigFont || subBytes * 4 < fullBytes) && subBytes < fullBytes && info.BaseFont.Contains('+'),
            $"лист А3 «План этажа»: {subBytes / 1024.0:0} КБ с подмножеством против {fullBytes / 1024.0:0} КБ с полным шрифтом (в {(double)fullBytes / subBytes:0.0} раза), " +
            $"поток шрифта {sub.Pdf?.FontStreamBytes / 1024.0:0.0} КБ, {sub.Pdf?.Glyphs} глифов, шрифт в {sub.Pdf?.FontReduction:0} раз меньше");
        File.Delete(fullPath);

        // Альбом из 3 листов.
        string albumPath = Path.Combine(directory, "gost_album.pdf");
        var box = new SectionBox(-12, 12, -1.5, 10, -9, 0.05);
        var album = GostDrawingPdfExporter.ExportAlbum(new GostDrawingRequest(GostSheetFormat.A3, GostDrawingView.FloorPlan, elements, groups, axes,
            "Самопроверка · каркасный корпус", FrameGroup, 0, box, SectionFace.MaxZ, Loads: new StructuralLoadSettings()), albumPath);
        foreach (var d in album.Details) report.AppendLine("    " + d);
        for (uint i = 0; i < 3; i++) await RenderPdfAsync(albumPath, Path.Combine(directory, $"gost_album_{i + 1}.png"), 3200, i);
        var native = PdfNativeContentExtractor.Read(albumPath);
        string PageText(int i) => i < native.Pages.Count ? string.Join(" ", native.Pages[i].Runs.Select(r => r.Text)) : string.Empty;
        bool textOk = PageText(0).Contains("План этажа", StringComparison.Ordinal) && PageText(1).Contains("Разрез", StringComparison.Ordinal) &&
                      PageText(2).Contains("Спецификация элементов этажа", StringComparison.Ordinal) && PageText(2).Contains("Ведомость расхода стали", StringComparison.Ordinal) &&
                      PageText(2).Contains("Ведомость листов альбома", StringComparison.Ordinal);
        Check(report, ref failures, album.Pdf.Pages == 3 && native.Pages.Count == 3 && album.Sheets[0] is { ScaleDenominator: 100, Format: GostSheetFormat.A3 } && album.Sheets[1].ScaleDenominator > 0,
            $"альбом gost_album.pdf: листов {album.Pdf.Pages}, {album.Pdf.Bytes / 1024.0:0} КБ — " + string.Join("; ", album.Sheets.Select(s => $"{s.Number}) {s.Title} {GostDrawingPdfExporter.FormatTitle(s.Format)}{(s.ScaleDenominator > 0 ? $" М 1:{s.ScaleDenominator}" : string.Empty)}")));
        Check(report, ref failures, textOk && album.SpecRows >= 8 && album.SteelRows >= 1 && album.Occlusion is { Verified: true },
            $"содержание листов читается из векторного слоя: план, разрез (окклюзия проверена), лист 3 — спецификация этажа ({album.SpecRows} марок), ведомость стали ({album.SteelRows} марок), ведомость листов");
        // Единая нумерация марок: каждая марка колонн на плане и разрезе есть в спецификации листа 3.
        HashSet<string> ColumnMarks(int page) => native.Pages.ElementAtOrDefault(page)?.Runs
            .Select(r => r.Text.Trim()).Where(s => System.Text.RegularExpressions.Regex.IsMatch(s, @"^К\d+$")).ToHashSet() ?? [];
        var planMarks = ColumnMarks(0);
        var sectionMarks = ColumnMarks(1);
        var specMarks = ColumnMarks(2);
        Check(report, ref failures, planMarks.Count > 0 && sectionMarks.Count > 0 && planMarks.IsSubsetOf(specMarks) && sectionMarks.IsSubsetOf(specMarks),
            $"общая нумерация марок альбома: колонны плана {string.Join(" ", planMarks.Order())}, разреза {string.Join(" ", sectionMarks.Order())} — все есть в спецификации ({string.Join(" ", specMarks.Order())})");
        var levels = BlueprintAnnotationAnalyzer.Analyze(native, [1]);
        double[] expectedMarks = [-0.62, -0.02, 2.83, 5.68, 8.53];
        Check(report, ref failures, expectedMarks.All(m => levels.LevelsM.Any(l => Math.Abs(l - m) < 1e-6)),
            $"лист 2 — высотные отметки уровней: {string.Join("; ", levels.LevelsM.Select(ExtractedBlueprintAnnotations.ElevationText))}");
        return failures;
    }

    // ═════════════════════════════ 6. Верификация плоского DXF ═════════════════════════════

    /// <summary>
    /// Предпросмотр окна верификации (6 колонн, типичное 400 × 400, 2 стены из пар линий t = 300, 5 осей) и подъём с
    /// подтверждёнными параметрами: этаж 3300, стены 3000 на отметке +0,150, колонны 500 × 600 (круглые — Ø500).
    /// </summary>
    private static async Task<int> CheckDxfVerificationAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        string path = Path.Combine(directory, "foreign_verify.dxf");
        await File.WriteAllBytesAsync(path, BuildForeignDxf());
        var drawing = await new CadDxfImporterService().LoadAsync(path);
        var preview = CadDxfImporterService.AnalyzeLift(drawing);
        Check(report, ref failures, !preview.Is3D && preview.Columns == 6 && preview.ColumnSection is { W: 0.4, D: 0.4, Circle: false } && preview.PairedWalls == 2 &&
                                    preview.WallThickness is { } t && Math.Abs(t - 0.3) < 1e-9 && preview.Axes == 5,
            $"окно верификации: плоский чертёж, колонн {preview.Columns} (типичное {preview.ColumnSection?.W * 1000:0}×{preview.ColumnSection?.D * 1000:0}), " +
            $"стен из пар линий {preview.PairedWalls} (t = {preview.WallThickness * 1000:0} мм), осей {preview.Axes}, габарит {preview.WidthM:0.###} × {preview.DepthM:0.###} м");

        var options = new DxfLiftOptions("DXF · verify", 0.15, 3.3, 0)
        {
            WallHeight = 3.0, ColumnWidth = 0.5, ColumnDepth = 0.6, SlabThickness = 0.25, BeamHeight = 0.6
        };
        var lift = CadDxfImporterService.LiftTo3D(drawing, options);
        var cols = lift.Elements.Where(e => e.Type == ElementTypes.Column).ToList();
        var walls = lift.Elements.Where(e => e.Type == ElementTypes.Wall).ToList();
        bool colOk = cols.Count == 6 && cols.Where(c => c.EffectiveProfile == ProfileTypes.Rectangle).All(c => Math.Abs(c.Width - 0.5) < 1e-9 && Math.Abs(c.Depth - 0.6) < 1e-9) &&
                     cols.Where(c => c.EffectiveProfile == ProfileTypes.Circle).All(c => Math.Abs(c.Width - 0.5) < 1e-9 && Math.Abs(c.Depth - 0.5) < 1e-9) &&
                     cols.All(c => Math.Abs(c.Height - 3.3) < 1e-9 && Math.Abs(c.BaseY - 0.15) < 1e-9);
        bool wallOk = walls.Count == 2 && walls.All(w => Math.Abs(w.Height - 3.0) < 1e-9 && Math.Abs(w.BaseY - 0.15) < 1e-9 && Math.Abs(w.Depth - 0.3) < 1e-9);
        Check(report, ref failures, colOk && wallOk && lift.Report.Any(l => l.Contains("параметры подтверждены", StringComparison.Ordinal)),
            $"подъём с подтверждёнными параметрами: колонны {string.Join(", ", cols.Select(c => $"{c.Width * 1000:0}×{c.Depth * 1000:0}").Distinct())} H {cols.FirstOrDefault()?.Height * 1000:0} мм, " +
            $"стены H {walls.FirstOrDefault()?.Height * 1000:0} мм на отметке {GostDrawingPdfExporter.Elevation(walls.FirstOrDefault()?.BaseY ?? 0)}");
        return failures;
    }

    // ═════════════════════════════ 7. Зачистка ═════════════════════════════

    /// <summary>Подписи чата «Вы / Система / Ошибка» переключаются с языком; устаревший DemoSceneFactory удалён, пресеты — через PresetScenesService.</summary>
    private static int CheckCleanup(StringBuilder report)
    {
        int failures = 0;
        var system = ChatMessageModel.System("тест");
        var user = ChatMessageModel.User("тест");
        var error = new ChatMessageModel { Role = ChatRole.Error, Text = "тест" };
        int notified = 0;
        system.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChatMessageModel.Header)) notified++; };
        system.Language = user.Language = error.Language = LocalizationService.Russian;
        string ru = $"{system.Header} | {user.Header} | {error.Header}";
        system.Language = user.Language = error.Language = LocalizationService.English;
        string en = $"{system.Header} | {user.Header} | {error.Header}";
        Check(report, ref failures, ru.StartsWith("Система", StringComparison.Ordinal) && ru.Contains("| Вы ·", StringComparison.Ordinal) && ru.Contains("| Ошибка ·", StringComparison.Ordinal) &&
                                    en.StartsWith("System", StringComparison.Ordinal) && en.Contains("| User ·", StringComparison.Ordinal) && en.Contains("| Error ·", StringComparison.Ordinal) && notified >= 1,
            $"подписи чата через LocalizationService: RU «{ru}», EN «{en}», уведомление привязки Header");
        bool removed = typeof(SceneStateService).Assembly.GetType("AIArchitectStudio.Services.DemoSceneFactory") is null;
        var presets = new[] { PresetScenesService.Cottage, PresetScenesService.Tower, PresetScenesService.Pavilion }.Select(p => PresetScenesService.Create(p, LocalizationService.Russian)).ToList();
        Check(report, ref failures, removed && presets.All(p => p.Elements.Count > 0),
            $"DemoSceneFactory удалён, пресеты строит PresetScenesService: {string.Join(", ", presets.Select(p => $"«{p.Title}» ({p.Elements.Count})"))}");
        return failures;
    }
}
#endif
