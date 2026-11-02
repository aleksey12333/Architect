#if DEBUG
using System.Diagnostics;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Transactions;
using AIArchitectStudio.Services.Verification;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест транзакций (фаза 7): шаг Undo хранит дельты (слоты, тип, этаж, состав), а не клон сцены; 1000 правок длины
/// колонны + 1000 Undo + 1000 Redo возвращают координаты, геометрию и КЭ-расчёт до бита, история не течёт (слабые ссылки,
/// память на шаг) и укладывается в миллисекунды; цепочка разнородных операций откатывается и повторяется до бита;
/// DocumentChanged несёт только затронутые элементы; солвер и песочница Совета журнал не пишут.
/// </summary>
public static partial class CadSelfTestService
{
    private static int CheckTransactions(StringBuilder report)
    {
        int failures = 0;
        CheckTransactionDeltas(report, ref failures);
        CheckThousandEdits(report, ref failures);
        CheckOperationHistory(report, ref failures);
        CheckDocumentChanged(report, ref failures);
        CheckSandboxJournal(report, ref failures);
        return failures;
    }

    private static SceneStateService LoadPreset(string id)
    {
        var preset = PresetScenesService.Create(id, LocalizationService.Russian);
        var scene = new SceneStateService();
        scene.LoadScene(preset.Elements, preset.Groups, recordUndo: false, clearHistory: true, axes: preset.Axes);
        return scene;
    }

    /// <summary>Полное состояние документа: элементы (Id, тип, слепок эталона с геометрией), постройки, оси, размеры, выделение.</summary>
    private static string DocumentFingerprint(SceneStateService s)
    {
        var elements = s.Elements.ToList();
        var records = ElementRecords(elements, [.. s.BuildingGroups]);
        var sb = new StringBuilder();
        for (int i = 0; i < elements.Count; i++)
            sb.Append(elements[i].Id).Append('|').Append(elements[i].TypeId.ToString("N")).Append('|').Append(records[i]).Append('\n');
        foreach (var g in s.BuildingGroups)
            sb.Append("g|").Append(g.Id).Append('|').Append(g.Name).Append('|').Append(Bits(g.OriginX)).Append(',').Append(Bits(g.OriginZ)).Append('\n');
        foreach (var a in s.GridAxes)
            sb.Append("a|").Append(a.Label).Append('|').Append(a.Direction).Append('|').Append(Bits(a.Coordinate)).Append('|').Append(a.BuildingGroupId).Append('\n');
        foreach (var d in s.PinnedDimensions)
            sb.Append("d|").Append(d.Id).Append('|').Append(string.Join(",", d.Start.Select(Bits))).Append(';').Append(string.Join(",", d.End.Select(Bits))).Append('\n');
        sb.Append("sel|").Append(s.SelectedElement?.Id).Append('|').Append(string.Join(",", s.SelectedElements.Select(e => e.Id)));
        return Sha256Hex(sb.ToString());
    }

    private static long SettledMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true);
    }

    // ───────────── 1. Шаг = дельты ─────────────

    /// <summary>Одна правка высоты колонны: в шаге только слоты этой колонны (центр, габариты, поворот), без клонов сцены.</summary>
    private static void CheckTransactionDeltas(StringBuilder report, ref int failures)
    {
        var scene = LoadPreset(PresetScenesService.Frame);
        var column = scene.Elements.First(e => e.Category == BimCategory.Column);
        double baseY = column.BaseY, h = column.Height + 0.25;
        scene.SetTransform(column, column.X, baseY + h / 2, column.Z, column.Width, h, column.Depth, column.RotationY, column.LevelIndex);
        var t = scene.Transactions.PeekUndo()!;
        var parameters = t.Changes.OfType<ParameterChange>().ToList();
        bool ok = t.Changes.Count <= 4 && t.Changes.All(c => ReferenceEquals(c.Element, column)) && !t.HasMembershipChanges &&
                  parameters.Any(p => p.Kind == ParameterSlotKind.Vector && p.Slot == VectorSlot.Size && p.OldValue.Reference is double[] { Length: 3 } old && old[1] == h - 0.25) &&
                  scene.Transactions.UndoCount == 1;
        Check(report, ref failures, ok,
            $"шаг «{t.Name}» на сцене из {scene.Elements.Count} элементов — {t.Changes.Count} изменения только у колонны " +
            $"({string.Join("; ", t.Changes.Select(c => c.ToString()))}), клонов сцены нет");
    }

    // ───────────── 2. 1000 правок + 1000 Undo + 1000 Redo ─────────────

    private static void CheckThousandEdits(StringBuilder report, ref int failures)
    {
        const int n = 1000;
        var loads = new StructuralLoadSettings { Seismicity = 8 };
        var scene = LoadPreset(PresetScenesService.Frame);
        var column = scene.Elements.First(e => e.Category == BimCategory.Column);
        string Analysis() => AnalysisFingerprint(StructuralAnalysisService.Analyze([.. scene.Elements], [.. scene.BuildingGroups], loads));
        string state0 = DocumentFingerprint(scene), analysis0 = Analysis();

        // Цена прежнего шага — клон всей сцены (для сравнения памяти).
        long m0 = SettledMemory();
        var snapshot = scene.Elements.Select(e => e.Clone()).ToList();
        long snapshotBytes = SettledMemory() - m0;
        GC.KeepAlive(snapshot);
        snapshot = null;

        int modifiedOnly = 0, otherEvents = 0;
        void Count(object? sender, DocumentChangedEventArgs e)
        {
            if (e is { IsReset: false, Added.Count: 0, Deleted.Count: 0, Modified.Count: 1 } && ReferenceEquals(e.Modified[0], column)) modifiedOnly++;
            else otherEvents++;
        }
        double baseY = column.BaseY, h0 = column.Height;
        // Прогрев (JIT, ёмкость списков истории и уведомлений) — короткий цикл и очистка истории, затем отсчёт памяти.
        for (int i = 1; i <= 50; i++)
            scene.SetTransform(column, column.X, baseY + (h0 + i * 0.001) / 2, column.Z, column.Width, h0 + i * 0.001, column.Depth, column.RotationY, column.LevelIndex);
        while (scene.Undo()) { }
        while (scene.Redo()) { }
        while (scene.Undo()) { }
        scene.ClearHistory();
        scene.DocumentChanged += Count;

        long before = SettledMemory();
        var sw = Stopwatch.StartNew();
        for (int i = 1; i <= n; i++)
        {
            double h = h0 + i * 0.001;
            scene.SetTransform(column, column.X, baseY + h / 2, column.Z, column.Width, h, column.Depth, column.RotationY, column.LevelIndex);
        }
        double editMs = sw.Elapsed.TotalMilliseconds;
        var (firstStep, firstPosition) = WeakFirstStep(scene.Transactions);
        int depth = scene.UndoDepth, stored = scene.Transactions.StoredChangeCount;
        long historyBytes = SettledMemory() - before;
        string state1 = DocumentFingerprint(scene), analysis1 = Analysis();

        sw.Restart();
        int undone = 0;
        while (scene.Undo()) undone++;
        double undoMs = sw.Elapsed.TotalMilliseconds;
        string stateU = DocumentFingerprint(scene), analysisU = Analysis();

        sw.Restart();
        int redone = 0;
        while (scene.Redo()) redone++;
        double redoMs = sw.Elapsed.TotalMilliseconds;
        string stateR = DocumentFingerprint(scene), analysisR = Analysis();
        scene.DocumentChanged -= Count;

        // Утечки: после очистки истории шаги и отменённые значения собираются сборщиком; второй такой же цикл
        // (1000 правок + 1000 Undo + 1000 Redo) не прибавляет памяти к первому.
        scene.ClearHistory();
        long after = SettledMemory();
        bool collected = !firstStep.IsAlive && !firstPosition.IsAlive;
        for (int i = 1; i <= n; i++)
        {
            double h = h0 + i * 0.001;
            scene.SetTransform(column, column.X, baseY + h / 2, column.Z, column.Width, h, column.Depth, column.RotationY, column.LevelIndex);
        }
        while (scene.Undo()) { }
        while (scene.Redo()) { }
        scene.ClearHistory();
        long leak = SettledMemory() - after;
        double perStep = historyBytes / (double)n;

        Check(report, ref failures, depth == n && undone == n && redone == n && stateU == state0 && analysisU == analysis0 && stateR == state1 && analysisR == analysis1 &&
                                    state1 != state0 && analysis1 != analysis0,
            $"{n} правок высоты колонны (шагов Undo {depth}) + {undone} Undo + {redone} Redo: после Undo координаты, контуры, тип и геометрия всех " +
            $"{scene.Elements.Count} элементов и КЭ-расчёт ≡ исходным до бита, после Redo ≡ результату правок");
        Check(report, ref failures, collected && perStep < Math.Max(2048, snapshotBytes / 20.0) && leak < 64 * 1024 && stored <= 4 * n,
            $"память: шаг {perStep:0} Б ({stored} изменений на {n} шагов) против {snapshotBytes / 1024.0:0} КБ снимка сцены (в {snapshotBytes / Math.Max(1, perStep):0} раз меньше); " +
            $"после очистки истории первый шаг и прежний массив центра колонны собраны сборщиком: {(collected ? "да" : "НЕТ")}; " +
            $"повторный цикл {n} + {n} + {n} прибавил {leak / 1024.0:0.0} КБ (норматив < 64 КБ)");
        Check(report, ref failures, editMs < 300 && undoMs < 300 && redoMs < 300 && modifiedOnly == 3 * n && otherEvents == 0,
            $"время: {n} правок {editMs:0.0} мс, {n} Undo {undoMs:0.0} мс, {n} Redo {redoMs:0.0} мс; DocumentChanged — {modifiedOnly} уведомлений " +
            $"«изменена 1 колонна» (прочих {otherEvents}), полной перестройки нет");
    }

    /// <summary>
    /// Слабые ссылки на первый шаг и на прежний массив центра колонны (значение «было» первой правки). Отдельный метод: в Debug
    /// временные значения живут до конца метода, где они созданы, и удерживали бы шаг от сборки.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Step, WeakReference Position) WeakFirstStep(TransactionManager journal)
    {
        var step = journal.UndoStack[0];
        var position = step.Changes.OfType<ParameterChange>().First(p => p.Kind == ParameterSlotKind.Vector && p.Slot == VectorSlot.Position).OldValue.Reference;
        return (new WeakReference(step), new WeakReference(position));
    }

    // ───────────── 3. Цепочка операций: Undo ≡ «до», Redo ≡ «после» ─────────────

    private static void CheckOperationHistory(StringBuilder report, ref int failures)
    {
        var scene = LoadPreset(PresetScenesService.Cottage);
        var ops = new List<(string Name, Func<bool> Run)>();
        ArchitecturalElement? Find(Func<ArchitecturalElement, bool> f) => scene.Elements.FirstOrDefault(f);
        var host = Find(e => e.Category == BimCategory.Wall && scene.GetHostedElements(e.Id).Any());

        ops.Add(("перенос и размеры стены с проёмами", () =>
        {
            if (host is null) return false;
            scene.SetTransform(host, host.X + 0.5, host.Y, host.Z - 0.3, host.Width + 0.4, host.Height, host.Depth, host.RotationY + 15, host.LevelIndex);
            return true;
        }));
        ops.Add(("Push/Pull стены", () => host is not null && scene.PushPull(host, PushPullFace.PosX, host.Width + 0.6) is not null));
        ops.Add(("поворот группы", () =>
        {
            var group = scene.Elements.Where(e => e.Category is BimCategory.Column or BimCategory.Slab && e.HostElementId is null).Take(3).ToList();
            if (group.Count < 2) group = scene.Elements.Where(e => e.HostElementId is null).Take(3).ToList();
            scene.RotateGroup(group, 30);
            return true;
        }));
        ops.Add(("дублирование стены", () => host is not null && scene.DuplicateElement(host, 3, 0) is not null));
        ops.Add(("массив", () => scene.CreateLinearArray([scene.Elements.First(e => e.HostElementId is null)], 3, 4, 0).Count > 0));
        ops.Add(("копировать этаж вверх", () => scene.CopyFloorUp(scene.Elements.Where(e => e.LevelIndex == 0 && e.HostElementId is null).Take(4).ToList()).Count > 0));
        ops.Add(("окно → проём (смена типа)", () =>
        {
            if (Find(e => e.Category == BimCategory.Window) is not { } window) return false;
            scene.DeleteElement(window);
            return true;
        }));
        ops.Add(("материал трёх стен (Дублировать тип)", () =>
        {
            var walls = scene.Elements.Where(e => e.Category == BimCategory.Wall).Take(3).ToList();
            scene.UpdateElements(walls, e => e.Material = "brick");
            return walls.Count > 0;
        }));
        ops.Add(("пирог плиты", () =>
        {
            if (Find(e => e.Category == BimCategory.Slab) is not { } slab) return false;
            var hosted = scene.GetOpeningsFor(slab);
            scene.UpdateElement(slab, e =>
            {
                e.Layers = MaterialLayers.PresetFor(e, "floor");
                MaterialLayers.ApplyThickness(e, hosted);
            });
            return true;
        }));
        ops.Add(("толщина плиты → несущий слой на месте", () =>
        {
            if (Find(e => e.Category == BimCategory.Slab && e.Layers.Count > 0) is not { } slab) return false;
            scene.PushPull(slab, PushPullFace.PosY, slab.Height + 0.05);
            if (MaterialLayers.AbsorbThickness(slab)) scene.NotifyElementsChanged([slab]);
            return true;
        }));
        ops.Add(("свойство пользователя", () =>
        {
            var mark = ParameterRegistry.Shared.Define("Марка (тест транзакций)", ParameterSpecType.Text);
            scene.PushUndo();
            foreach (var e in scene.Elements.Where(e => e.Category == BimCategory.Wall).Take(2)) e.SetUserParameter(mark, ParameterValue.FromString("СТ-1"));
            scene.NotifyElementsChanged([.. scene.Elements.Where(e => e.Category == BimCategory.Wall).Take(2)]);
            return true;
        }));
        ops.Add(("правка оси на месте", () =>
        {
            if (scene.GridAxes.FirstOrDefault() is not { } axis) return false;
            scene.PushUndo();
            axis.Coordinate += 1.2;
            axis.Label += "'";
            scene.NotifyGridAxesChanged();
            return true;
        }));
        ops.Add(("закреплённый размер", () => scene.AddPinnedDimension((0, 0, 0), (3, 4, 0)) is not null));
        ops.Add(("вершина контура", () =>
        {
            if (Find(e => e.Polygon2D.Count >= 3 && SceneStateService.IsContourEditable(e)) is not { } poly) return false;
            var p = poly.Polygon2D[0];
            var (wx, wz) = ArchitecturalElement.RotateY(p.X * 0.9, p.Z * 0.9, poly.RotationY);
            return scene.MoveVertex(poly, 0, poly.X + wx, poly.Z + wz);
        }));
        ops.Add(("этаж выше", () =>
        {
            if ((Find(e => e.Category == BimCategory.Column) ?? Find(e => e.HostElementId is null)) is not { } el) return false;
            scene.SetTransform(el, el.X, el.Y + 3, el.Z, el.Width, el.Height, el.Depth, el.RotationY, el.LevelIndex + 1);
            return true;
        }));
        ops.Add(("удаление набора", () =>
        {
            // Выделение вне шага не меняется (оно входит в шаг на момент PushUndo — как прежний снимок).
            var victims = scene.Elements.Where(e => e.HostElementId is null).Skip(2).Take(3).ToList();
            scene.RemoveElements(victims);
            return victims.Count > 0;
        }));
        ops.Add(("явная транзакция из трёх операций", () =>
        {
            using (scene.BeginUndoTransaction("Пакет автотеста"))
            {
                var a = scene.Elements.First(e => e.HostElementId is null);
                scene.Translate(a, 1, 0, 0);
                scene.AddElement(new ArchitecturalElement { Type = ElementTypes.Column, Name = "Колонна пакета", BuildingGroupId = a.BuildingGroupId, Position = [a.X, 1.5, a.Z + 2], Size = [0.4, 3, 0.4] });
                scene.RemoveElement(scene.Elements.Last(e => e.HostElementId is null && !ReferenceEquals(e, a)));
            }
            return true;
        }));
        ops.Add(("+ к участку", () =>
        {
            var pavilion = PresetScenesService.Create(PresetScenesService.Pavilion, LocalizationService.Russian);
            scene.AppendBuildings(pavilion.Groups, pavilion.Elements, axes: pavilion.Axes);
            return true;
        }));
        ops.Add(("новая сцена поверх (LoadScene с Undo)", () =>
        {
            var frame = PresetScenesService.Create(PresetScenesService.Frame, LocalizationService.Russian);
            scene.LoadScene(frame.Elements, frame.Groups, recordUndo: true, axes: frame.Axes, dimensions: []);
            return true;
        }));

        var states = new List<string> { DocumentFingerprint(scene) };
        var done = new List<string>();
        var singleStep = new List<string>();
        foreach (var (name, run) in ops)
        {
            int depth = scene.UndoDepth;
            if (!run()) continue;
            if (scene.UndoDepth != depth + 1) singleStep.Add($"{name}: шагов {scene.UndoDepth - depth}");
            done.Add(name);
            states.Add(DocumentFingerprint(scene));
        }

        var undoBad = new List<string>();
        for (int i = done.Count - 1; i >= 0; i--)
        {
            if (!scene.Undo() || DocumentFingerprint(scene) != states[i]) undoBad.Add(done[i]);
        }
        var redoBad = new List<string>();
        for (int i = 0; i < done.Count; i++)
        {
            if (!scene.Redo() || DocumentFingerprint(scene) != states[i + 1]) redoBad.Add(done[i]);
        }
        Check(report, ref failures, done.Count >= 15 && singleStep.Count == 0 && undoBad.Count == 0 && redoBad.Count == 0,
            $"{done.Count} операций подряд ({string.Join(", ", done)}) — каждая ровно 1 шаг; каждый Undo ≡ состоянию до операции, каждый Redo ≡ после " +
            "(элементы с геометрией, тип, этаж, пирог, свойства, постройки, оси, размеры, выделение — до бита)" +
            (singleStep.Count > 0 ? "; не 1 шаг: " + string.Join("; ", singleStep) : string.Empty) +
            (undoBad.Count > 0 ? "; Undo расходится: " + string.Join(", ", undoBad) : string.Empty) +
            (redoBad.Count > 0 ? "; Redo расходится: " + string.Join(", ", redoBad) : string.Empty));
    }

    // ───────────── 4. DocumentChanged ─────────────

    private static void CheckDocumentChanged(StringBuilder report, ref int failures)
    {
        var scene = LoadPreset(PresetScenesService.Cottage);
        var events = new List<DocumentChangedEventArgs>();
        int sceneChanged = 0;
        scene.DocumentChanged += (_, e) => events.Add(e);
        scene.SceneChanged += (_, _) => sceneChanged++;

        var wall = scene.Elements.First(e => e.Category == BimCategory.Wall && scene.GetHostedElements(e.Id).Any());
        var openings = scene.GetHostedElements(wall.Id).ToList();
        var problems = new List<string>();

        scene.Translate(wall, 0.5, 0, 0);
        var move = events.LastOrDefault();
        if (move is null || move.IsReset || move.Added.Count + move.Deleted.Count > 0 || move.Modified.Count != 1 + openings.Count || !move.Modified.Contains(wall) || sceneChanged != 0)
            problems.Add($"сдвиг стены: {move}, SceneChanged {sceneChanged}");

        var column = new ArchitecturalElement { Type = ElementTypes.Column, Name = "Колонна DocumentChanged", BuildingGroupId = wall.BuildingGroupId, Position = [wall.X, 1.5, wall.Z + 3], Size = [0.4, 3, 0.4] };
        scene.AddElement(column);
        if (events[^1] is not { Added: [var added], Modified.Count: 0, Deleted.Count: 0, IsReset: false } || !ReferenceEquals(added, column)) problems.Add($"добавление: {events[^1]}");

        scene.RemoveElement(wall);
        var removed = events[^1];
        if (removed.Deleted.Count != 1 + openings.Count || !removed.Deleted.Contains(wall) || removed.IsReset) problems.Add($"удаление стены с проёмами: {removed}");

        scene.Undo();
        var undo = events[^1];
        if (undo.Reason != DocumentChangeReason.Undo || undo.Added.Count != 1 + openings.Count || undo.IsReset || !scene.Elements.Contains(wall)) problems.Add($"Undo удаления: {undo}");

        int before = events.Count;
        using (scene.BeginUndoTransaction("Пакет"))
        {
            scene.Translate(column, 1, 0, 0);
            scene.RotateBy(column, 45);
            scene.Translate(wall, 0, 0, 0.5);
        }
        var batch = events.Skip(before).ToList();
        if (batch is not [{ Modified.Count: var mc, Added.Count: 0, Deleted.Count: 0 }] || mc != 2 + openings.Count) problems.Add($"явная транзакция: {batch.Count} событий ({string.Join("; ", batch)})");

        scene.ActiveLevelFilter = 0;
        if (events[^1] is not { IsReset: true, Reason: DocumentChangeReason.Filter }) problems.Add($"фильтр этажа: {events[^1]}");

        Check(report, ref failures, problems.Count == 0,
            $"DocumentChanged(added, modified, deleted): сдвиг стены — изменены стена и {openings.Count} проёма без SceneChanged, добавление — 1 добавлен, " +
            $"удаление — стена с проёмами, Undo — они же добавлены, явная транзакция из 3 правок — одно событие при закрытии, фильтр этажа — полная перестройка" +
            (problems.Count > 0 ? ": " + string.Join("; ", problems) : string.Empty));
    }

    // ───────────── 5. Солвер и песочница Совета ─────────────

    /// <summary>
    /// Солвер (ИИ, чертёж) строит элементы вне документа — журнала у них нет; песочница Совета — свой документ без открытого шага:
    /// сотни правок не пишут ничего. Выпуск варианта в живую сцену (LoadScene с Undo) — один шаг: прежние объекты элементов
    /// (ссылки, не клоны) + добавленные; Undo возвращает те же объекты.
    /// </summary>
    private static void CheckSandboxJournal(StringBuilder report, ref int failures)
    {
        var solved = CompileBlueprint(DirectCompileDefinition());
        bool solverClean = solved is not null && solved.Elements.All(e => e.Journal is null);

        var live = LoadPreset(PresetScenesService.Frame);
        var original = live.Elements.ToList();
        var sandbox = DesignFixEngine.Sandbox(live);
        foreach (var e in sandbox.Elements.Where(e => e.Category == BimCategory.Slab)) e.SetSize(e.Width, e.Height + 0.02, e.Depth);
        bool sandboxClean = sandbox.Transactions.UndoCount == 0 && sandbox.Transactions.OpenTransaction is null && live.UndoDepth == 0 &&
                            original.All(e => ReferenceEquals(e.Journal, live.Transactions)) && sandbox.Elements.All(e => ReferenceEquals(e.Journal, sandbox.Transactions));

        live.LoadScene(sandbox.Elements.ToList(), sandbox.BuildingGroups.ToList(), recordUndo: true);
        var step = live.Transactions.PeekUndo();
        int cleared = step?.Changes.OfType<ElementsCleared>().Sum(c => c.Removed.Count) ?? 0;
        int added = step?.Changes.OfType<ElementAdded>().Count() ?? 0;
        bool published = live.UndoDepth == 1 && cleared == original.Count && added == sandbox.Elements.Count;
        live.Undo();
        bool restored = live.Elements.Count == original.Count && live.Elements.Zip(original).All(p => ReferenceEquals(p.First, p.Second));

        Check(report, ref failures, solverClean && sandboxClean && published && restored,
            $"солвер: {solved?.Elements.Count ?? 0} элементов без журнала; песочница Совета: правка всех плит — 0 шагов, живая сцена не тронута; " +
            $"выпуск варианта — 1 шаг (−{cleared} ссылок на прежние элементы, +{added}), Undo вернул те же объекты: {(restored ? "да" : "НЕТ")}");
    }
}
#endif
