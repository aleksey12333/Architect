using System.Diagnostics;
using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.Verification;

namespace AIArchitectStudio.Services.Photo;

/// <summary>Итог подъёма по фото: паспорт снимка, модель, сверка формы, проверка каркаса (K_исп, статика, висящие, коллизии, смета).</summary>
public sealed class PhotoLiftResult
{
    public required PhotoArchitectureDescriptor Descriptor { get; init; }
    public required PhotoBimModel Model { get; init; }
    public FidelityReport? Fidelity { get; set; }
    public double MaxUtilization { get; set; }
    public int Hanging { get; set; }
    public int Clashes { get; set; }
    public int DoorColumnClashes { get; set; }
    public FeaAuditReport? Audit { get; set; }
    public double FrameCostRub { get; set; }
    public int FixesApplied { get; set; }
    public int Passes { get; set; }
    public double ElapsedMs { get; set; }
    public List<string> Log { get; } = [];

    /// <summary>Каркас физически корректен: K_исп ≤ 1,0, статика сходится, нет висящих узлов и дверей в колоннах.</summary>
    public bool IsStructurallySound => MaxUtilization <= 1.0 + 1e-9 && Hanging == 0 && DoorColumnClashes == 0 && (Audit?.Criteria.Take(2).All(c => c.Passed) ?? true);
}

/// <summary>
/// Подъём 3D BIM-модели по фото: анализ снимка Визуальным ядром → синтез (или сверка ответа внешней модели) → доводка каркаса
/// двухпроходным шлюзом достоверности на песочнице (утолщения, сечения, опоры — <see cref="VerificationGateService"/>), стабилизация
/// до K_исп ≤ 1,0 → логический аудит КЭ-расчёта (ΣR_y = ΣF_верт, непрерывность путей нагрузки), аудит опор (висящие узлы),
/// проверка пересечений и дверей с колоннами, смета каркаса. Живая сцена не меняется — результат вставляется вызывающим кодом.
/// </summary>
public static class PhotoToBimService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static async Task<(PhotoArchitectureDescriptor Descriptor, RasterPlanTracer.RasterImage Raster, byte[] Jpeg)> AnalyzeAsync(LoadedPhoto photo)
    {
        var (jpeg, _, _, raster) = await PhotoImageLoader.NormalizeAsync(photo.Bytes);
        var descriptor = await Task.Run(() => PhotoArchitectureAnalyzer.Analyze(raster, photo.Name));
        return (descriptor, raster, jpeg);
    }

    /// <summary>Визуальное ядро без модели проектирования: синтез по паспорту и проверка каркаса.</summary>
    public static PhotoLiftResult Lift(PhotoArchitectureDescriptor descriptor, PhotoLiftOptions? options = null, StructuralLoadSettings? loads = null, bool verify = true,
        bool monotonicity = false)
    {
        var sw = Stopwatch.StartNew();
        var model = PhotoBimSynthesizer.Build(descriptor, options);
        var result = new PhotoLiftResult { Descriptor = descriptor, Model = model };
        result.Log.AddRange(model.Log);
        var traits = PhotoFidelityEnforcer.Traits(model.Elements);
        result.Fidelity = new FidelityReport(1, PhotoFidelityEnforcer.Score(traits, model.Morphology), false, [], traits, traits);
        Finish(result, loads ?? new StructuralLoadSettings(), verify, monotonicity);
        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>Ответ внешней/локальной модели (элементы после солвера) → сверка со снимком → доводка каркаса.</summary>
    public static PhotoLiftResult FromModelAnswer(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, PhotoArchitectureDescriptor descriptor,
        PhotoLiftOptions? options = null, StructuralLoadSettings? loads = null, bool verify = true)
    {
        var sw = Stopwatch.StartNew();
        var (model, fidelity) = PhotoFidelityEnforcer.Enforce(elements, groups, descriptor, options);
        var result = new PhotoLiftResult { Descriptor = descriptor, Model = model, Fidelity = fidelity };
        result.Log.AddRange(fidelity.Corrections);
        result.Log.AddRange(model.Log);
        Finish(result, loads ?? new StructuralLoadSettings(), verify, monotonicity: false);
        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>
    /// Хук солвера (<see cref="AI.AIOrchestrator.Solve(string, AI.AiRequestContext, SceneStateService)"/>): ответ любой модели по
    /// снимку заменяется проверенной моделью с формой и цветами снимка; в отчёт солвера — наложенные исправления и итоги проверки.
    /// </summary>
    public static PhotoLiftResult EnforceSolverResult(SolverResult solver, PhotoArchitectureDescriptor descriptor, PhotoLiftOptions? options, StructuralLoadSettings loads)
    {
        var lift = FromModelAnswer(solver.Elements.ToList(), solver.Groups.ToList(), descriptor, options, loads);
        solver.Elements.Clear();
        solver.Elements.AddRange(lift.Model.Elements);
        solver.Groups.Clear();
        solver.Groups.AddRange(lift.Model.Groups);
        if (lift.Fidelity is { Rebuilt: true }) solver.GridAxes.Clear();
        solver.Report.Actions.Add(string.Create(Ru, $"Сверка со снимком «{descriptor.SourceName}»: форма {lift.Fidelity?.ScoreBefore:0.00} → {lift.Fidelity?.ScoreAfter:0.00}"));
        foreach (var line in lift.Log) solver.Report.Actions.Add("Фото-верность: " + line);
        solver.Report.Actions.Add(Summary(lift));
        solver.Report.GeneratedElements = lift.Model.Elements.Count;
        return lift;
    }

    /// <summary>Проверка и доводка каркаса на песочнице (модель меняется на месте: элементы песочницы с прежними Id).</summary>
    public static void Finish(PhotoLiftResult result, StructuralLoadSettings loads, bool verify, bool monotonicity)
    {
        var model = result.Model;
        var swSize = Stopwatch.StartNew();
        int sized = PreSize(model.Elements, model.Groups, loads, result.Log);
        if (sized > 0) result.Log.Add(string.Create(Ru, $"Подбор сечений по КЭ-расчёту: изменений {sized} за {swSize.ElapsedMilliseconds} мс"));
        var sandbox = DesignFixEngine.Sandbox(model.Elements, model.Groups, model.Axes);
        if (verify)
        {
            var swGate = Stopwatch.StartNew();
            // Волна 4.1 — бюджет времени: у большой модели, которую подбор не довёл до K_исп ≤ 1, проверщик несущей способности (KE)
            // моделирует варианты полным расчётом на каждый перегруженный элемент — минуты. Тогда он пропускается: решает резервный
            // каркас ядра (диалог) или пользователь видит K_исп > 1 в сводке, а не ждёт.
            BimVerificationPipeline? pipeline = null;
            if (model.Elements.Count > 350 && StructuralAnalysisService.Analyze(model.Elements, model.Groups, loads).MaxUtilization > 1.0)
            {
                pipeline = new BimVerificationPipeline();
                foreach (var v in BimVerificationPipeline.CreateDefault().Verifiers.Where(v => v.Id != "KE")) pipeline.Register(v);
                result.Log.Add("Бюджет времени: подбор не довёл большую модель до K_исп ≤ 1 — проверщик несущей способности (KE) пропущен");
            }
            var gate = VerificationGateService.Inspect(sandbox, loads, "Визуальное ядро · Реконструкция по фото", pipeline: pipeline, cascade: true);
            result.Log.Add(string.Create(Ru, $"Шлюз достоверности: проверщиков {gate.VerifiersRun}, замечаний {gate.Fixes.Count + gate.Unresolved.Count}, {swGate.ElapsedMilliseconds} мс"));
            result.Passes = gate.Passes;
            if (gate.Fixes.Count > 0)
            {
                var applied = VerificationGateService.ApplyAccepted(sandbox, gate, loads);
                result.FixesApplied = applied.Applied;
                result.Log.Add($"Двухпроходная доводка каркаса: правок {applied.Applied} (проходов {gate.Passes}), K_исп {Fmt(gate.KBefore)} → {Fmt(applied.KAfter)}");
            }
            else result.Log.Add($"Двухпроходная проверка: замечаний нет, K_исп {Fmt(gate.KBefore)}");
        }
        var elements = sandbox.Elements.Select(e => e.Clone(keepId: true)).ToList();
        model.Elements.Clear();
        model.Elements.AddRange(elements);

        var analysis = StructuralAnalysisService.Analyze(elements, model.Groups, loads);
        result.MaxUtilization = analysis.MaxUtilization;
        result.FrameCostRub = VerificationGateService.FrameCost(sandbox, analysis, loads, GateCostRates.Default);
        var audited = elements.Select(e => e.Clone(keepId: true)).ToList();
        StructuralAuditor.Audit(audited, new SolverReport());
        result.Hanging = audited.Count(e => e.HasPhysicsError && e.Category is not (BimCategory.Window or BimCategory.Door));
        var clashes = ClashDetectionService.Detect(elements);
        result.Clashes = clashes.Count;
        result.DoorColumnClashes = DoorColumnConflicts(elements);
        result.Audit = FeaLogicalAuditService.Audit(elements, model.Groups, loads, monotonicity);
        if (result.MaxUtilization > 1.0)
            foreach (var e in elements.Where(e => analysis.For(e.Id) is { Utilization: > 1.0 }).OrderByDescending(e => analysis.For(e.Id)!.Utilization).Take(5))
            {
                var r = analysis.For(e.Id)!;
                result.Log.Add(string.Create(Ru, $"Перегрузка: {e.DisplayTitle} ({e.Type}) K {r.Utilization:0.00}: прочн. {r.KStrength:0.00}, уст. {r.KStability:0.00}, срез {r.KShear:0.00}, прогиб {r.KDeflection:0.00}, продавл. {r.KPunching:0.00}, грунт {r.KSoil:0.00}; пролёт {r.Span:0.0} м{(r.IsCantilever ? " консоль" : "")}, M {r.Moment:0} кН·м, q {r.DesignLoad:0.0}, h {e.Height:0.00}, низ {e.BaseY:0.00}"));
            }
        foreach (var e in audited.Where(e => e.HasPhysicsError && e.Category is not (BimCategory.Window or BimCategory.Door)).Take(5))
            result.Log.Add($"Без опоры: {e.DisplayTitle} ({e.Type}) низ {e.BaseY:0.##} м, X {e.X:0.##} Z {e.Z:0.##}; ниже: " + string.Join(", ", audited.Where(o => o.TopY <= e.BaseY + 0.3 && o.TopY > e.BaseY - 1.0 && Math.Abs(o.X - e.X) < 1.5 && Math.Abs(o.Z - e.Z) < 1.5).Select(o => $"{o.Type} верх {o.TopY:0.##} X {o.X:0.##} Z {o.Z:0.##}")));
        result.Log.Add(Summary(result));
    }

    /// <summary>
    /// Поэлементный подбор до K_исп ≤ 0,95 (до 12 проходов КЭ-расчёта): плита/покрытие +30 мм (продавливание +50 мм, верх на месте,
    /// до 450 мм), колонна +100 мм (до 1,4 м), стена +50 мм (до 0,8 м), ядро +0,6 м, фундамент — давление на грунт: контур +1 м и +100 мм,
    /// иначе +100 мм (верх на месте), балка +100 мм по высоте.
    /// </summary>
    public static int PreSize(List<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, StructuralLoadSettings loads, List<string>? log = null)
    {
        int changes = 0;
        for (int pass = 0; pass < 18; pass++)
        {
            var analysis = StructuralAnalysisService.Analyze(elements, groups, loads);
            var over = elements.Where(e => analysis.For(e.Id) is { Utilization: > 0.95 }).ToList();
            int before = changes;
            // Пропорции плит (СП 63, рекомендации проверщика SPAN): h ≥ L/30, консоль — h ≥ L/10.
            foreach (var e in elements)
            {
                if (e.Category is not (BimCategory.Slab or BimCategory.CantileverSlab) && !(e.Category == BimCategory.Roof && e.IsFlatRoof)) continue;
                if (analysis.For(e.Id) is not { Span: > 0 } rs) continue;
                double need = Math.Ceiling(rs.Span / (rs.IsCantilever ? 10 : 30) * 100) / 100;
                if (e.Height < need - 1e-3 && need <= 0.6) { Thicken(e, need - e.Height); changes++; }
            }
            if (over.Count == 0 && changes == before) break;
            foreach (var e in over)
            {
                var r = analysis.For(e.Id)!;
                switch (e.Category)
                {
                    case BimCategory.Slab or BimCategory.CantileverSlab or BimCategory.Terrace:
                    case BimCategory.Roof when e.IsFlatRoof:
                        if (e.Height < 0.45) { Thicken(e, r.KPunching >= r.KStrength ? 0.05 : 0.03); changes++; }
                        break;
                    case BimCategory.Column:
                        if (e.Width < 1.4)
                        {
                            e.SetSize(Math.Min(1.4, e.Width + 0.1), e.Height, Math.Min(1.4, e.Depth + 0.1));
                            changes++;
                        }
                        break;
                    case BimCategory.Wall:
                        if (e.Depth < 0.8)
                        {
                            e.SetSize(e.Width, e.Height, Math.Min(0.8, e.Depth + 0.05));
                            foreach (var o in elements.Where(o => o.HostElementId == e.Id)) o.SetSize(o.Width, o.Height, e.Depth + ParametricSolverService.OpeningClearance);
                            changes++;
                        }
                        break;
                    case BimCategory.Core:
                        if (e.Width < 10) { e.SetSize(e.Width + 0.6, e.Height, e.Depth + 0.6); changes++; }
                        break;
                    case BimCategory.Foundation:
                        if (r.KSoil >= Math.Max(Math.Max(r.KStrength, r.KShear), r.KPunching) && e.Polygon2D.Count >= 3)
                        {
                            var world = e.Polygon2D.Select(p => (e.X + p.X, e.Z + p.Z)).ToList();
                            var grown = PhotoBimSynthesizer.Offset(world, 0.5);
                            var (minX, maxX, minZ, maxZ) = Geometry2D.Bounds(grown);
                            double cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
                            e.Polygon2D = grown.Select(p => new Point2D(p.X - cx, p.Z - cz)).ToList();
                            e.SetSize(maxX - minX, e.Height, maxZ - minZ);
                            e.SetPosition(cx, e.Y, cz);
                        }
                        if (e.Height < 4.0) Thicken(e, 0.15);
                        changes++;
                        break;
                    case BimCategory.Beam:
                        if (e.Height < 1.5) { e.SetSize(e.Width, e.Height + 0.1, e.Depth); changes++; }
                        break;
                }
            }
            log?.Add(string.Create(Ru, $"Подбор, проход {pass + 1}: K_исп max {analysis.MaxUtilization:0.00}, перегружено {over.Count}, изменено {changes - before}"));
            if (changes == before) break;
        }
        return changes;
    }

    /// <summary>
    /// Утолщение: плиты и покрытия — вверх (низ на опорах, как <see cref="DesignFixEngine.ThickenSlab"/>: колонны и стены ниже
    /// остаются опорами), фундамент — вниз (верх на отметке 0).
    /// </summary>
    private static void Thicken(ArchitecturalElement e, double dt)
    {
        if (e.Category != BimCategory.Foundation)
        {
            double bottom = e.BaseY;
            e.SetSize(e.Width, e.Height + dt, e.Depth);
            e.SetPosition(e.X, bottom + e.Height / 2, e.Z);
            return;
        }
        double top = e.TopY;
        e.SetSize(e.Width, e.Height + dt, e.Depth);
        e.SetPosition(e.X, top - e.Height / 2, e.Z);
    }

    /// <summary>Двери, полотно которых в плане пересекает колонну того же этажа.</summary>
    public static int DoorColumnConflicts(IReadOnlyList<ArchitecturalElement> elements)
    {
        int n = 0;
        var columns = elements.Where(e => e.Category == BimCategory.Column).ToList();
        foreach (var door in elements.Where(e => e.Category == BimCategory.Door))
        {
            var o = ClashDetectionService.PlanOutline(door);
            foreach (var c in columns)
            {
                if (c.TopY <= door.BaseY + 0.05 || c.BaseY >= door.TopY - 0.05) continue;
                if (Geometry2D.IntersectionArea(Geometry2D.ConvexHull(o), Geometry2D.ConvexHull(ClashDetectionService.PlanOutline(c))) > 1e-4) { n++; break; }
            }
        }
        return n;
    }

    public static string Summary(PhotoLiftResult r) => string.Create(Ru,
        $"Этажей: {r.Model.Floors} · {MorphologyTitle(r.Model.Morphology)} · K_исп {Fmt(r.MaxUtilization)} · смета каркаса {r.FrameCostRub:N0} ₽ · " +
        $"ΣF {r.Audit?.AppliedLoad:N0} кН = ΣR {r.Audit?.GroundReaction:N0} кН · висящих {r.Hanging} · дверей в колоннах {r.DoorColumnClashes}");

    public static string MorphologyTitle(PhotoMorphology m) => m switch
    {
        PhotoMorphology.Curved => "радиусный / изгиб",
        PhotoMorphology.Rounded => "скруглённый корпус",
        PhotoMorphology.Cascading => "каскадный с уступами",
        PhotoMorphology.Triangular => "треугольный / скошенный",
        PhotoMorphology.Rotunda => "ротонда / купол",
        PhotoMorphology.ArchSpan => "мост / арка / зал",
        PhotoMorphology.Pitched => "скатный дом",
        PhotoMorphology.Box => "прямоугольный корпус",
        _ => "авто по фото"
    };

    private static string Fmt(double k) => k.ToString("0.00", Ru);
}
