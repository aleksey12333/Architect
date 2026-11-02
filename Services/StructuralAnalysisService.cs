using System.Diagnostics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Analytical;
using AIArchitectStudio.Models.Transactions;
using Microsoft.UI.Dispatching;

namespace AIArchitectStudio.Services;

/// <summary>
/// Экспресс-расчёт конструктива сцены по СП 20.13330 (нагрузки), СП 63.13330 (ж/б), СП 16.13330 (сталь) и СП 14.13330
/// (сейсмика) по расчётной схеме <see cref="IAnalyticalModel"/> (строит <see cref="AnalyticalModelBuilder"/>). Вертикальные
/// нагрузки собираются сверху вниз: равномерная нагрузка пластин уже распределена схемой по грузовым площадям опор,
/// стержни-балки распределяют нагрузку между опорами по правилу рычага; колонны и стены накапливают продольную силу N со всех
/// вышележащих этажей и передают её на нижележащую колонну, сквозь плиту на колонну под ней, на балку, плиту (с проверкой
/// продавливания) или фундамент (давление на грунт). Горизонтальные силы — ветер по этажам и сейсмика по первой форме
/// (K1 = 0.4, Kψ = 1.3) — распределяются между стойками пропорционально изгибной жёсткости EI.
/// Время пересчёта типовой сцены — единицы миллисекунд.
/// </summary>
public sealed class StructuralAnalysisService
{
    private readonly SceneStateService _scene;
    private readonly SettingsService _settings;
    private readonly DispatcherQueue? _dispatcher;
    private int _queued;

    public StructuralAnalysisResult Current { get; private set; } = StructuralAnalysisResult.Empty;

    /// <summary>Расчёт обновлён (всегда в UI-потоке).</summary>
    public event EventHandler<StructuralAnalysisResult>? AnalysisChanged;

    public StructuralAnalysisService(SceneStateService scene, SettingsService settings)
    {
        _scene = scene;
        _settings = settings;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        // Любое изменение документа (правка, Undo/Redo, загрузка) — один пересчёт за кадр UI; при выключенном «Пересчёте при
        // перетаскивании» правки геометрии (Gizmo, ручки, инспектор) ждут паузы 350 мс — схема считается после отпускания.
        _scene.DocumentChanged += (_, e) =>
        {
            if (!_settings.Current.Engineering.LiveRecalculation && e.Reason == DocumentChangeReason.Edit && !e.IsStructural && !e.IsReset) Defer();
            else Schedule();
        };
        _settings.SettingsChanged += (_, _) => Schedule();
        Recalculate();
        WarmUp();
    }

    /// <summary>
    /// Прогрев JIT в фоне на демо-сценах (все ветви расчёта: плиты, балки, ядро, диагрид, оболочка, фундамент), затем пересчёт
    /// текущей сцены — легенда показывает время «тёплого» расчёта, а не первого запуска.
    /// </summary>
    private void WarmUp()
    {
        var loads = Loads.Clone();
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var id in new[] { PresetScenesService.Tower, PresetScenesService.Cottage, PresetScenesService.Pavilion })
                {
                    var demo = PresetScenesService.Create(id);
                    Analyze(demo.Elements, demo.Groups, loads);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                App.WriteCrashLog("analysis-warmup", ex);
            }
        }).ContinueWith(_ => Schedule(), TaskScheduler.Default);
    }

    /// <summary>Параметры площадки из настроек.</summary>
    public StructuralLoadSettings Loads => _settings.Current.StructuralLoads;

    /// <summary>
    /// Перетаскивание Gizmo шлёт десятки событий за кадр — пересчёт один раз на кадр UI. Вызывается и из фоновых потоков
    /// (сохранение настроек проверкой ключей, прогрев JIT) — флаг очереди атомарный.
    /// </summary>
    private void Schedule()
    {
        if (_dispatcher is null)
        {
            Recalculate();
            return;
        }
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        _dispatcher.TryEnqueue(() =>
        {
            Volatile.Write(ref _queued, 0);
            Recalculate();
        });
    }

    private DispatcherQueueTimer? _deferTimer;

    /// <summary>Пересчёт после паузы в правках (перетаскивание без живого пересчёта): каждая новая правка откладывает таймер.</summary>
    private void Defer()
    {
        if (_dispatcher is null)
        {
            Recalculate();
            return;
        }
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(Defer);
            return;
        }
        if (_deferTimer is null)
        {
            _deferTimer = _dispatcher.CreateTimer();
            _deferTimer.Interval = TimeSpan.FromMilliseconds(350);
            _deferTimer.IsRepeating = false;
            _deferTimer.Tick += (_, _) => Schedule();
        }
        _deferTimer.Stop();
        _deferTimer.Start();
    }

    /// <summary>Фоновый расчёт идёт; за время расчёта сцена изменилась — нужен ещё один по свежему снимку.</summary>
    private bool _running, _rerun;

    /// <summary>
    /// Пересчёт по снимку сцены в фоне: клоны элементов снимаются в UI-потоке (перетаскивание Gizmo не меняет данные под
    /// расчётом), сам расчёт — в пуле потоков, результат и событие — снова в UI-потоке. Изменения во время расчёта
    /// схлопываются в один повторный расчёт: UI не ждёт расчёта даже на больших моделях.
    /// </summary>
    public void Recalculate()
    {
        if (_dispatcher is null)
        {
            Current = Analyze(_scene.Elements.ToList(), _scene.BuildingGroups.ToList(), Loads);
            AnalysisChanged?.Invoke(this, Current);
            return;
        }
        if (!_dispatcher.HasThreadAccess)
        {
            Schedule();
            return;
        }
        if (_running)
        {
            _rerun = true;
            return;
        }
        _running = true;
        var elements = _scene.Elements.Select(e => e.Clone()).ToList();
        var groups = _scene.BuildingGroups.Select(g => g.Clone()).ToList();
        var loads = Loads.Clone();
        var dispatcher = _dispatcher;
        Task.Run(() => Analyze(elements, groups, loads))
            .ContinueWith(task => dispatcher.TryEnqueue(() => OnBackgroundCompleted(task)), TaskScheduler.Default);
    }

    private void OnBackgroundCompleted(Task<StructuralAnalysisResult> task)
    {
        _running = false;
        if (task.IsCompletedSuccessfully)
        {
            Current = task.Result;
            AnalysisChanged?.Invoke(this, Current);
        }
        else if (task.Exception?.InnerException is { } ex)
            App.WriteCrashLog("analysis", ex);
        if (!_rerun) return;
        _rerun = false;
        Recalculate();
    }

    /// <summary>Расчёт произвольного набора элементов (вьюпорт, автотест, листы ГОСТ): построение схемы и расчёт по ней.</summary>
    /// <param name="parallel">Связи и грузовые площади схемы в пуле потоков (false — в вызывающем потоке; результат одинаков до бита).</param>
    public static StructuralAnalysisResult Analyze(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, StructuralLoadSettings settings,
        int slabSamples = 220, bool parallel = true)
    {
        var sw = Stopwatch.StartNew();
        var model = AnalyticalModelBuilder.Build(elements, groups, settings, slabSamples, parallel);
        var result = Analyze(model);
        sw.Stop();
        result.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>Расчёт готовой расчётной схемы (схема не меняется — её можно считать повторно).</summary>
    public static StructuralAnalysisResult Analyze(IAnalyticalModel model)
    {
        var sw = Stopwatch.StartNew();
        var result = new StructuralAnalysisResult { Settings = model.Settings };
        new Solver(model).Run(result);
        sw.Stop();
        result.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    // ═════════════════════════════ Формулы норм (общие с паспортом расчёта) ═════════════════════════════

    /// <summary>φ для ж/б и кладки по l0/h (СП 63, табл. 8.1, при длительной нагрузке; за пределами — линейная экстраполяция).</summary>
    public static double PhiConcrete(double lambdaH)
    {
        (double L, double P)[] table = [(0, 0.92), (6, 0.92), (10, 0.90), (15, 0.83), (20, 0.70), (25, 0.58), (30, 0.48), (40, 0.32), (60, 0.15)];
        if (lambdaH <= 0) return 0.92;
        for (int i = 1; i < table.Length; i++)
            if (lambdaH <= table[i].L)
                return table[i - 1].P + (table[i].P - table[i - 1].P) * (lambdaH - table[i - 1].L) / (table[i].L - table[i - 1].L);
        return 0.1;
    }

    /// <summary>φ центрально-сжатого стального стержня, тип сечения «b» (СП 16.13330, формула 8).</summary>
    public static double PhiSteel(double lambdaBar)
    {
        if (lambdaBar <= 0.4) return 1;
        const double alpha = 0.04, beta = 0.09;
        double l2 = lambdaBar * lambdaBar;
        double delta = 9.87 * (1 - alpha + beta * lambdaBar) + l2;
        double disc = delta * delta - 39.48 * l2;
        double phi = disc > 0 ? 0.5 * (delta - Math.Sqrt(disc)) / l2 : 7.6 / l2;
        return Math.Clamp(phi, 0.05, 1);
    }

    /// <summary>
    /// Предельный момент прямоугольного ж/б сечения b × h при армировании μ (СП 63.13330, п. 8.1.8): x = min(Rs·As / (Rb·b), 0.5·h0),
    /// M_ult = Rb·b·x·(h0 − x/2). Rb — кПа (как Rc материала схемы), Rs — МПа.
    /// </summary>
    public static double RcUltimateMoment(double rbKPa, double rsMPa, double b, double h, double a, double mu)
    {
        double h0 = Math.Max(0.02, h - a);
        double rb = rbKPa, rs = rsMPa * 1000;
        double asArea = mu * b * h0;
        double x = Math.Min(rs * asArea / (rb * b), 0.5 * h0);
        return rb * b * x * (h0 - x / 2);
    }

    // ═════════════════════════════ Состояние расчёта ═════════════════════════════

    /// <summary>Сосредоточенная нагрузка: точка плана, расчётное и нормативное значения, источник и признак продавливания.</summary>
    private readonly record struct PointLoad(double X, double Z, double D, double N, AnalyticalElement? From, bool Punch = false);

    /// <summary>Накопленные при обходе сверху вниз силы и результат проверок элемента схемы (сама схема не меняется).</summary>
    private sealed class State
    {
        public double ReceivedD, ReceivedN;
        public double LatV, LatM;
        public readonly List<PointLoad> Points = [];

        /// <summary>Нагрузка от плит по грузовым площадям (у балки — распределённая вдоль оси).</summary>
        public readonly List<TributaryLoad> Distributed = [];
        public readonly List<(AnalyticalElement Column, double D)> ColumnReactions = [];
        public readonly ElementStructuralResult R = new();
        public bool HasResult;
    }

    private sealed class Solver(IAnalyticalModel model)
    {
        private const double Tol = AnalyticalModel.Tolerance;
        private const double GroundLevel = AnalyticalModel.GroundLevel;
        private readonly IReadOnlyList<AnalyticalElement> _elements = model.Elements;
        private readonly StructuralLoadSettings s = model.Settings;
        private readonly State[] _state = CreateStates(model.Elements.Count);
        private readonly (double Rb, double Rbt, double Eb, double Creep) _concrete = model.Settings.ConcreteProps();
        private readonly (double Rs, double Rsc, double Rsw) _rebar = model.Settings.RebarProps();
        private readonly List<PunchingNode> _nodes = [];
        private readonly Dictionary<string, (double Wind, double Seismic)> _baseShear = [];
        private readonly Dictionary<string, double> _phase = new(model.Profile.Phases);

        private static State[] CreateStates(int count)
        {
            var states = new State[count];
            for (int i = 0; i < count; i++) states[i] = new State();
            return states;
        }

        private State S(AnalyticalElement m) => _state[m.Index];

        /// <summary>Накопление времени подфазы и перезапуск секундомера.</summary>
        private void Tick(string phase, Stopwatch clock)
        {
            Tick(phase, clock.Elapsed.TotalMilliseconds);
            clock.Restart();
        }

        private void Tick(string phase, double milliseconds) => _phase[phase] = _phase.GetValueOrDefault(phase) + milliseconds;

        public void Run(StructuralAnalysisResult result)
        {
            var sw = Stopwatch.StartNew();
            LateralPass();
            double tLateral = sw.Elapsed.TotalMilliseconds;
            GravityPass();
            double tGravity = sw.Elapsed.TotalMilliseconds;
            var p = model.Profile;
            result.Profile = $"схема {p.ModelMs:0.0} · кровли {p.ExposedMs:0.0} · грузовые площади ∥ {p.PlansMs:0.0} · горизонт. {tLateral:0.0} · " +
                             $"вертик. {tGravity - tLateral:0.0} мс; стержней {model.Members.Count}, пластин {model.Panels.Count}, узлов {model.Nodes.Count}; " +
                             string.Join(", ", _phase.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {x.Value:0.0}"));

            // γn (ГОСТ 27751-2014, п. 10.1) умножает усилия проверок первой группы предельных состояний; КС-2 (γn = 1) — без изменений.
            double gammaN = s.GammaN;
            foreach (var m in _elements)
            {
                var st = S(m);
                if (!st.HasResult || !m.Checked) continue;
                var r = st.R;
                if (gammaN != 1) ApplyGammaN(r, gammaN);
                r.ElementId = m.Source.Id;
                r.Type = m.Source.Type;
                r.Floor = m.Floor;
                if (!result.Elements.TryGetValue(m.Source.Id, out var existing) || r.Utilization > existing.Utilization)
                    result.Elements[m.Source.Id] = r;
            }
            result.PunchingNodes.AddRange(_nodes.OrderByDescending(n => n.Ratio));
            foreach (var (k, v) in _baseShear) result.BaseShear[k] = v;
        }

        /// <summary>Проверки прочности, устойчивости, среза, продавливания и основания — с γn; прогиб (II группа) — без.</summary>
        private static void ApplyGammaN(ElementStructuralResult r, double gammaN)
        {
            r.KStrength *= gammaN;
            r.KStability *= gammaN;
            r.KShear *= gammaN;
            r.KPunching *= gammaN;
            r.KSoil *= gammaN;
        }

        // ───────────── Горизонтальные нагрузки: ветер и сейсмика ─────────────

        /// <summary>
        /// Поэтажные силы по постройке: ветер w = w0·k(z)·c·γf·B·h (местность B, c = 0.8 + 0.6; пульсация упрощённо ×1.25 при H &gt; 40 м);
        /// сейсмика S_k = K0·K1·Q_k·A·β·Kψ·η_k (СП 14.13330, первая форма — линейная по высоте, β по периоду T ≈ 0.1·n).
        /// Расчётной принимается схема с большим сдвигом в основании. Доля стойки — по EI.
        /// </summary>
        private void LateralPass()
        {
            foreach (var group in _elements.GroupBy(m => m.GroupId))
            {
                var verticals = group.Where(m => m.Role == AnalyticalRole.Vertical && m.Bearing).ToList();
                if (verticals.Count == 0) continue;
                double z0 = verticals.Min(v => v.Base);
                var tops = group.Where(m => m.Role is AnalyticalRole.Slab or AnalyticalRole.Foundation or AnalyticalRole.Shell).Select(m => m.Top)
                    .Concat(verticals.Select(v => v.Top))
                    .Where(z => z > z0 + 0.5).Order().ToList();
                var levels = new List<double> { z0 };
                foreach (var z in tops)
                    if (z - levels[^1] > 0.6) levels.Add(z);
                    else if (levels.Count > 1) levels[^1] = Math.Max(levels[^1], z);
                if (levels.Count < 2) continue;
                int n = levels.Count - 1;

                // Веса этажей (нормативные постоянные + 0.5 полезной / снеговой).
                var weights = new double[n + 1];
                foreach (var m in group)
                {
                    double mid = (m.Base + m.Top) / 2;
                    // Фундамент и всё, что ниже основания вертикальных конструкций, инерционных сил не создаёт.
                    if (mid <= z0 + 0.05) continue;
                    int k = Math.Clamp(levels.FindIndex(z => z >= mid - 1e-6), 1, n);
                    double extra = m.Role == AnalyticalRole.Slab ? m.NetArea * 0.5 * (m.Exposed ? s.SnowKPa * 0.7 : s.LiveLoadKPa) : 0;
                    double self = m.Role is AnalyticalRole.Slab or AnalyticalRole.Foundation ? ((AnalyticalPanel)m).AreaSelfN * m.NetArea : m.SelfN;
                    weights[k] += self + extra;
                }

                double minX = group.Min(m => m.MinX), maxX = group.Max(m => m.MaxX), minZ = group.Min(m => m.MinZ), maxZ = group.Max(m => m.MaxZ);
                double breadth = Math.Max(maxX - minX, maxZ - minZ);
                double height = levels[^1] - z0;
                var wind = new double[n + 1];
                var seismic = new double[n + 1];
                double sumQx = 0, sumQx2 = 0;
                for (int k = 1; k <= n; k++)
                {
                    double x = levels[k] - z0;
                    sumQx += weights[k] * x;
                    sumQx2 += weights[k] * x * x;
                }
                double period = 0.1 * n;
                double beta = period <= 0.1 ? 1 + 15 * period : period <= 0.4 ? 2.5 : Math.Max(0.8, 2.5 * Math.Sqrt(0.4 / period));
                for (int k = 1; k <= n; k++)
                {
                    double z = levels[k] - z0;
                    double kz = Math.Max(0.5, 0.65 * Math.Pow(Math.Max(z, 1) / 10, 0.4));
                    double below = levels[k] - levels[k - 1];
                    double above = k < n ? levels[k + 1] - levels[k] : 0;
                    double trib = (below + above) / 2;
                    wind[k] = s.WindKPa * kz * 1.4 * StructuralNorms.GammaWind * breadth * trib * (height > 40 ? 1.25 : 1);
                    double eta = sumQx2 > 0 ? z * sumQx / sumQx2 : 0;
                    // K0 = 1 (нормальный уровень ответственности), K1 = 0.4, Kψ = 1.3.
                    seismic[k] = 1.0 * 0.4 * weights[k] * s.SeismicA * beta * 1.3 * eta;
                }
                double windShear = wind.Sum(), seismicShear = seismic.Sum();
                _baseShear[group.Key!] = (windShear, seismicShear);
                var forces = seismicShear > windShear ? seismic : wind;

                // Жёсткость EI каждой стойки — один раз (момент инерции стойки уже в схеме).
                var stiffness = new double[verticals.Count];
                for (int u = 0; u < verticals.Count; u++) stiffness[u] = LateralStiffness(verticals[u]);
                for (int vi = 0; vi < verticals.Count; vi++)
                {
                    var v = verticals[vi];
                    int j = levels.FindIndex(1, z => z > v.Base + 0.1);
                    if (j < 1) continue;
                    double shear = 0, overturning = 0;
                    for (int k = j; k <= n; k++)
                    {
                        shear += forces[k];
                        overturning += forces[k] * (levels[k] - v.Base);
                    }
                    double storey = Math.Max(0.5, levels[j] - levels[j - 1]);
                    double mid = (levels[j - 1] + levels[j]) / 2;
                    // Стойки, пересекающие середину этажа (в порядке списка, сама стойка — в конце, если не пересекает).
                    double total = 0;
                    bool self = false;
                    for (int u = 0; u < verticals.Count; u++)
                    {
                        if (!(verticals[u].Base < mid && verticals[u].Top > mid)) continue;
                        total += stiffness[u];
                        self |= u == vi;
                    }
                    if (!self) total += stiffness[vi];
                    double share = total > 0 ? stiffness[vi] / total : 0;
                    var st = S(v);
                    st.LatV = share * shear;
                    // Колонна этажа — рамная стойка (точка нулевого момента посередине); ядро, стены на несколько этажей и диагрид-оболочка
                    // работают как консоль и воспринимают свою долю опрокидывающего момента.
                    bool frame = v.Height <= 1.5 * storey && v.Category != BimCategory.Diagrid;
                    st.LatM = frame ? st.LatV * v.Height / 2 : share * overturning;
                }
            }
        }

        /// <summary>Изгибная жёсткость EI для распределения горизонтальной силы (ж/б — с понижающим коэффициентом 0.85).</summary>
        private static double LateralStiffness(AnalyticalElement m) =>
            m.Material.E * (m.Material.Kind == AnalyticalMaterialKind.Concrete ? 0.85 : 1) * m.LateralInertia;

        // ───────────── Вертикальные нагрузки: обход сверху вниз ─────────────

        private void GravityPass()
        {
            var order = _elements
                .OrderByDescending(m => Math.Round(m.Base, 3))
                .ThenBy(m => m.Role switch
                {
                    AnalyticalRole.LoadOnly => 0, AnalyticalRole.Shell => 1, AnalyticalRole.Slab => 2, AnalyticalRole.Line => 3, AnalyticalRole.Vertical => 4, _ => 5
                })
                .ToList();
            var sw = new Stopwatch();
            foreach (var m in order)
            {
                sw.Restart();
                switch (m.Role)
                {
                    case AnalyticalRole.LoadOnly: ProcessLoadOnly(m); break;
                    case AnalyticalRole.Shell: ProcessShell((AnalyticalPanel)m); break;
                    case AnalyticalRole.Slab: ProcessSlab((AnalyticalPanel)m); break;
                    case AnalyticalRole.Line: ProcessLine((AnalyticalMember)m); break;
                    case AnalyticalRole.Vertical: ProcessVertical(m); break;
                    case AnalyticalRole.Foundation: ProcessFoundation((AnalyticalPanel)m); break;
                }
                Tick(RoleNames[(int)m.Role], sw.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>Имена фаз профиля по роли элемента (без Enum.ToString на каждом элементе).</summary>
        private static readonly string[] RoleNames = Enum.GetNames<AnalyticalRole>();

        // ───────────── Плиты ─────────────

        /// <summary>
        /// Плита в обходе сверху вниз: к реакциям от равномерной нагрузки (распределены схемой по грузовым площадям) добавляются
        /// сосредоточенные силы от вышележащих конструкций, реакции передаются опорам, затем — проверки и продавливание.
        /// </summary>
        private void ProcessSlab(AnalyticalPanel m)
        {
            var st = S(m);
            var plan = m.Tributary!;
            double qd = m.AreaLoadD, qn = m.AreaLoadN;
            st.R.DesignLoad = qd;
            var clock = Stopwatch.StartNew();
            var supports = plan.Supports;

            if (supports.Count == 0)
            {
                // Без опор (плита по грунту или висящая) — нагрузка передаётся вниз как есть.
                var loads = plan.Samples.Select(p => (p.X, p.Z, qd * p.A, qn * p.A)).Concat(st.Points.Select(p => (p.X, p.Z, p.D, p.N))).ToList();
                TransferDown(m, loads, punchSource: false, plan.Receivers);
                if (m.Base > GroundLevel) st.R.Notes.Add("Нет опор под плитой — нагрузка передаётся на нижележащие конструкции");
                FinishSlabChecks(m, qd, qn, 0, 0, 0, onGrade: true);
                return;
            }

            // Реакции равномерной нагрузки — копии из схемы: расчёт добавляет к ним силы сверху, схема остаётся прежней.
            var reactD = (double[])plan.ReactD.Clone();
            var reactN = (double[])plan.ReactN.Clone();
            var lineLists = new List<TributaryLoad>?[plan.LineLoads.Length];
            for (int i = 0; i < lineLists.Length; i++)
                if (plan.LineLoads[i] is { } source) lineLists[i] = [.. source];
            double maxPoint = 0;
            foreach (var p in st.Points)
            {
                var (i, _) = NearestIndex(supports, p.X, p.Z);
                reactD[i] += p.D;
                reactN[i] += p.N;
                if (supports[i].Role == AnalyticalRole.Line) (lineLists[i] ??= []).Add(new TributaryLoad(p.X, p.Z, p.D, p.N));
                maxPoint = Math.Max(maxPoint, p.D);
            }
            var reactions = new Dictionary<AnalyticalElement, (double D, double N)>();
            var lineLoads = new Dictionary<AnalyticalElement, List<TributaryLoad>>();
            for (int i = 0; i < supports.Count; i++)
            {
                if (reactD[i] == 0 && reactN[i] == 0) continue;
                reactions[supports[i]] = (reactD[i], reactN[i]);
                if (lineLists[i] is { } list) lineLoads[supports[i]] = list;
            }
            Tick("плиты: силы", clock);

            foreach (var (sp, (d, n)) in reactions)
            {
                if (sp.Role == AnalyticalRole.Vertical)
                {
                    var target = S(sp);
                    target.ReceivedD += d;
                    target.ReceivedN += n;
                    if (sp.IsColumnLike && Math.Abs(sp.Top - m.Base) <= Tol) st.ColumnReactions.Add((sp, d));
                }
            }
            foreach (var (line, loads) in lineLoads) S(line).Distributed.AddRange(loads);

            if (!m.Checked) return;
            bool mono = m.Material.Kind == AnalyticalMaterialKind.Concrete && reactions.Keys.All(sp => sp.Material.Kind == AnalyticalMaterialKind.Concrete);
            FinishSlabChecks(m, qd, qn, plan.Span, plan.Cantilever, maxPoint, onGrade: false, mono, plan.SpanFactor);
            CheckSlabPunching(m, qd, supports);
            Tick("плиты: проверки", clock);
        }

        private static (int Index, double Distance) NearestIndex(List<AnalyticalElement> supports, double x, double z)
        {
            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < supports.Count; i++)
            {
                if (supports[i].BoundDistance(x, z) >= bestD) continue;
                double d = supports[i].DistanceTo(x, z);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return (best, bestD);
        }

        private static (AnalyticalElement Support, double Distance) Nearest(List<AnalyticalElement> supports, double x, double z)
        {
            AnalyticalElement best = supports[0];
            double bestD = double.MaxValue;
            foreach (var sp in supports)
            {
                // Отсечка по габариту: такая опора не ближе текущей (результат совпадает с полным перебором).
                if (sp.BoundDistance(x, z) >= bestD) continue;
                double d = sp.DistanceTo(x, z);
                if (d < bestD)
                {
                    bestD = d;
                    best = sp;
                }
            }
            return (best, bestD);
        }

        /// <summary>Пролёт по точке плиты: у линейной опоры — 2d, у колонны — 2·max(|dx|, |dz|) в осях плиты за вычетом полусечения.</summary>
        private static double LocalSpan(AnalyticalElement slab, AnalyticalElement support, double x, double z, double distance)
        {
            if (!support.IsPointLike) return 2 * distance;
            var (dx, dz) = ArchitecturalElement.RotateY(x - support.Center.X, z - support.Center.Z, -slab.RotationY);
            double hx = support.Shape == AnalyticalShape.Circle ? support.Radius : (support.MaxX - support.MinX) / 2;
            double hz = support.Shape == AnalyticalShape.Circle ? support.Radius : (support.MaxZ - support.MinZ) / 2;
            return 2 * Math.Max(0, Math.Max(Math.Abs(dx) - hx, Math.Abs(dz) - hz));
        }

        /// <param name="spanFactor">Доля нагрузки, воспринимаемая пролётом в расчётном направлении (0.5 — квадратная плита, опёртая по контуру).</param>
        private void FinishSlabChecks(AnalyticalPanel m, double qd, double qn, double span, double cantilever, double maxPoint, bool onGrade, bool mono = true, double spanFactor = 1)
        {
            var st = S(m);
            var r = st.R;
            st.HasResult = m.Checked;
            double h = Math.Max(0.05, m.Thickness);
            double a = m.Role == AnalyticalRole.Foundation ? 0.07 : 0.03;
            double h0 = Math.Max(0.03, h - a);
            if (onGrade)
            {
                r.Moment = 0;
                r.Shear = 0;
                r.Span = 0;
                r.KStrength = 0.05;
                return;
            }

            double kM = mono ? 12 : 8;
            // Сосредоточенная сила P (колонна на плите) — момент P·L/4, распределённый на полосу шириной L: P/4 на 1 м.
            double mSpan = spanFactor * qd * span * span / kM + (span > 0 ? maxPoint / 4 : 0);
            double mCant = qd * cantilever * cantilever / 2;
            r.Moment = Math.Max(mSpan, mCant);
            r.IsCantilever = mCant > mSpan;
            r.Span = r.IsCantilever ? cantilever : span;
            r.Shear = Math.Max(qd * Math.Max(0, span / 2 - h0), qd * Math.Max(0, cantilever - h0));

            double ei = EffectiveEI(m.Material, h * h * h / 12);
            double fSpan = span > 0 ? spanFactor * (mono ? 2.0 : 5.0) / 384 * qn * Math.Pow(span, 4) / ei : 0;
            double fCant = cantilever > 0 ? qn * Math.Pow(cantilever, 4) / (8 * ei) : 0;
            double limSpan = span / 200, limCant = 2 * cantilever / 150;
            double kSpan = limSpan > 0 ? fSpan / limSpan : 0, kCant = limCant > 0 ? fCant / limCant : 0;
            bool cantGov = kCant > kSpan;
            r.DeflectionMm = (cantGov ? fCant : fSpan) * 1000;
            r.DeflectionLimitMm = (cantGov ? limCant : limSpan) * 1000;
            r.KDeflection = Math.Max(kSpan, kCant);

            if (m.Material.Kind == AnalyticalMaterialKind.Concrete)
            {
                double mult = RcMoment(m.Material, 1, h, a, 0.004);
                double qult = 0.5 * m.Material.Rbt * h0;
                r.KStrength = mult > 0 ? r.Moment / mult : 0;
                r.KShear = qult > 0 ? r.Shear / qult : 0;
            }
            else
            {
                double w = h * h / 6;
                double rb = m.Material.Kind == AnalyticalMaterialKind.Steel ? m.Material.Rc : Math.Max(1e3, m.Material.Rbend);
                r.KStrength = r.Moment / Math.Max(1e-6, rb * w);
                r.KShear = r.Shear / Math.Max(1e-6, 0.58 * rb * h);
            }
        }

        /// <summary>Жёсткость при длительном действии нагрузки: ж/б — 0.85·Eb·I / (1 + φb,cr) (СП 63, п. 8.2.27), прочие — E·I.</summary>
        private double EffectiveEI(AnalyticalMaterial mat, double inertia) =>
            mat.Kind == AnalyticalMaterialKind.Concrete ? mat.E * inertia * 0.85 / (1 + _concrete.Creep) : mat.E * inertia;

        /// <summary>
        /// Предельный момент прямоугольного ж/б сечения b × h при армировании μ (СП 63, п. 8.1.8): Rb — материала элемента (класс
        /// проекта или сертификат элемента; у бетона класса проекта Rc = Rb·1000 — то же значение до бита).
        /// </summary>
        private double RcMoment(AnalyticalMaterial mat, double b, double h, double a, double mu) => RcUltimateMoment(mat.Rc, _rebar.Rs, b, h, a, mu);

        // ───────────── Продавливание (СП 63.13330, п. 8.1.46) ─────────────

        /// <summary>
        /// Узлы продавливания плиты: колонны, на которые опирается плита (сила — реакция плиты за вычетом нагрузки внутри
        /// контура), и колонны/ядра, стоящие на плите без опоры под ней (сила — N сверху).
        /// </summary>
        private void CheckSlabPunching(AnalyticalPanel slab, double qd, List<AnalyticalElement> supports)
        {
            var st = S(slab);
            double h = Math.Max(0.05, slab.Thickness);
            double h0 = Math.Max(0.03, h - (slab.Role == AnalyticalRole.Foundation ? 0.07 : 0.03));
            var beams = supports.Where(sp => sp.Role == AnalyticalRole.Line).ToList();
            foreach (var (column, force) in st.ColumnReactions.GroupBy(c => c.Column).Select(g => (g.Key, g.Sum(x => x.D))))
            {
                // Колонна с ригелями в узле — плита опирается на балки, продавливания нет.
                if (beams.Any(b => b.DistanceTo(column.Center.X, column.Center.Z) < 0.6)) continue;
                AddPunchingNode(slab, column, force, qd, h0, slab.Base, offsetForLoad: h0 / 2);
            }
            foreach (var group in st.Points.Where(p => p.Punch && p.From is { IsColumnLike: true }).GroupBy(p => p.From!))
                AddPunchingNode(slab, group.Key, group.Sum(p => p.D), qd, h0, slab.Top, offsetForLoad: h0 / 2);
        }

        private void AddPunchingNode(AnalyticalElement slab, AnalyticalElement column, double force, double pressure, double h0, double y, double offsetForLoad)
        {
            var contour = PunchContour(column, h0 / 2);
            double u = 0;
            for (int i = 0; i < contour.Count; i++)
            {
                var p = contour[i];
                var q = contour[(i + 1) % contour.Count];
                double mx = (p.X + q.X) / 2, mz = (p.Z + q.Z) / 2;
                if (!AnalyticalGeometry.InsideNet(slab, mx, mz)) continue;
                u += Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Z - p.Z) * (q.Z - p.Z));
            }
            if (u < 0.05) return;
            double inside = Geometry2D.Area(PunchContour(column, offsetForLoad));
            double f = Math.Max(0, force - pressure * inside);
            double capacity = (slab.Material.Kind == AnalyticalMaterialKind.Concrete ? slab.Material.Rbt : _concrete.Rbt * 1000) * u * h0;
            double ratio = capacity > 0 ? f / capacity : 0;
            var status = ratio <= 1 ? PunchingStatus.Ok : ratio <= 2 ? PunchingStatus.NeedsReinforcement : PunchingStatus.Critical;
            var node = new PunchingNode
            {
                ColumnId = column.Source.Id,
                SlabId = slab.Source.Id,
                Force = f,
                Capacity = capacity,
                Perimeter = u,
                H0 = h0,
                Status = status,
                Location = [column.Center.X, y, column.Center.Z],
                Contour = contour
            };
            _nodes.Add(node);
            var slabR = S(slab).R;
            var columnR = S(column).R;
            foreach (var target in new[] { slabR, columnR })
            {
                target.KPunching = Math.Max(target.KPunching, ratio);
                if (status > target.Punching) target.Punching = status;
            }
            if (status != PunchingStatus.Ok)
            {
                string text = $"Узел «{column.Source.DisplayTitle}»: F = {f:0} кН > F_b,ult = {capacity:0} кН. {node.Recommendation}";
                if (!slabR.Notes.Contains(text)) slabR.Notes.Add(text);
                if (!columnR.Notes.Contains(text)) columnR.Notes.Add(text);
            }
        }

        /// <summary>Расчётный контур на расстоянии offset от граней колонны (круглой — окружность, прочих — прямоугольник).</summary>
        private static List<(double X, double Z)> PunchContour(AnalyticalElement column, double offset)
        {
            var bar = column as AnalyticalMember;
            if (column.Shape == AnalyticalShape.Circle && bar is not null)
            {
                double rx = bar.SectionB / 2 + offset, rz = bar.SectionH / 2 + offset;
                var ring = new List<(double X, double Z)>(48);
                for (int i = 0; i < 48; i++)
                {
                    double a = 2 * Math.PI * i / 48;
                    var (x, z) = ArchitecturalElement.RotateY(rx * Math.Cos(a), rz * Math.Sin(a), column.RotationY);
                    ring.Add((column.Center.X + x, column.Center.Z + z));
                }
                return ring;
            }
            var (minX, maxX, minZ, maxZ) = (column.MinX, column.MaxX, column.MinZ, column.MaxZ);
            if (column.Shape == AnalyticalShape.Polygon && !column.HasPolygon && bar is not null)
            {
                double hw = bar.SectionB / 2 + offset, hd = bar.SectionH / 2 + offset;
                var corners = new[] { (-hw, -hd), (hw, -hd), (hw, hd), (-hw, hd) };
                // Каждая сторона делится на 8 участков: контур у края плиты и у отверстий учитывается частично.
                var pts = new List<(double X, double Z)>();
                for (int i = 0; i < 4; i++)
                {
                    var a = corners[i];
                    var b = corners[(i + 1) % 4];
                    for (int k = 0; k < 8; k++)
                    {
                        double t = k / 8.0;
                        var (x, z) = ArchitecturalElement.RotateY(a.Item1 + (b.Item1 - a.Item1) * t, a.Item2 + (b.Item2 - a.Item2) * t, column.RotationY);
                        pts.Add((column.Center.X + x, column.Center.Z + z));
                    }
                }
                return pts;
            }
            minX -= offset; maxX += offset; minZ -= offset; maxZ += offset;
            var box = new List<(double X, double Z)>(32);
            for (int k = 0; k < 8; k++) box.Add((minX + (maxX - minX) * k / 8, minZ));
            for (int k = 0; k < 8; k++) box.Add((maxX, minZ + (maxZ - minZ) * k / 8));
            for (int k = 0; k < 8; k++) box.Add((maxX - (maxX - minX) * k / 8, maxZ));
            for (int k = 0; k < 8; k++) box.Add((minX, maxZ - (maxZ - minZ) * k / 8));
            return box;
        }

        // ───────────── Балки и фермы ─────────────

        /// <summary>
        /// Балка, ферма, кольцевая балка: нагрузка (собственный вес, плиты по грузовым площадям, сосредоточенные силы от колонн,
        /// стен и балок сверху) распределяется между соседними опорами стержня по правилу рычага; момент пролёта M = qL²/8
        /// (qL²/12 при монолитных узлах) + P·a·b/L, консоль — ΣP·x; прогиб 5qL⁴/384EI (2qL⁴/384EI при защемлении) и qc⁴/8EI.
        /// </summary>
        private void ProcessLine(AnalyticalMember m)
        {
            var st = S(m);
            double length = Math.Max(0.05, m.AxisLength);
            var axisPos = m.Stations;

            var uniform = axisPos.Select(p => (S: p.S, D: m.SelfD * p.Length / length, N: m.SelfN * p.Length / length)).ToList();
            uniform.AddRange(st.Distributed.Select(p => (m.ArcPosition(p.X, p.Z), p.D, p.N)));
            var concentrated = st.Points.Select(p => (S: m.ArcPosition(p.X, p.Z), p.D, p.N)).ToList();
            double totalD = uniform.Sum(p => p.D) + concentrated.Sum(p => p.D);
            double totalN = uniform.Sum(p => p.N) + concentrated.Sum(p => p.N);
            st.R.DesignLoad = uniform.Sum(p => p.D) / length;
            st.HasResult = true;

            if (m.Supports.Count == 0)
            {
                var pts = m.TransferPoints!;
                double sum = Math.Max(1e-9, pts.Sum(p => p.W));
                TransferDown(m, pts.Select(p => (p.X, p.Z, totalD * p.W / sum, totalN * p.W / sum)).ToList(), punchSource: false, m.Receivers);
                st.R.Notes.Add("Балка без опор: нет колонн, стен или ригелей под балкой");
                return;
            }

            var sup = m.SupportStations;
            var spans = m.Spans;
            bool truss = m.Category == BimCategory.Truss;
            var sec = m.Section;
            double ei = truss
                ? m.Material.E * 2 * m.ChordArea * Math.Pow(Math.Max(0.1, m.SectionH - Math.Min(0.25, m.SectionH * 0.15)) / 2, 2)
                : m.Shape == AnalyticalShape.Polyline ? EffectiveEI(m.Material, m.BendingInertia) : EffectiveEI(m.Material, sec.IStrong);
            ei = Math.Max(1e-6, ei);
            bool mono = m.Material.Kind == AnalyticalMaterialKind.Concrete && sup.All(x => x.Support.Material.Kind == AnalyticalMaterialKind.Concrete);
            double h0 = Math.Max(0.05, m.SectionH - 0.05);

            var spanUniform = new (double D, double N)[spans.Count];
            var spanPoints = spans.Select(_ => new List<(double X, double D, double N)>()).ToArray();
            var spanReact = new (double L, double R, double LN, double RN)[spans.Count];
            var leftCant = new List<(double X, double D, double N)>();
            var rightCant = new List<(double X, double D, double N)>();

            void Place(double s, double d, double n, bool point)
            {
                for (int i = 0; i < spans.Count; i++)
                {
                    double pos = m.AxisClosed && s < spans[i].S0 ? s + length : s;
                    if (pos < spans[i].S0 - 1e-9 || pos > spans[i].S1 + 1e-9) continue;
                    double L = spans[i].S1 - spans[i].S0, x = pos - spans[i].S0;
                    var rr = spanReact[i];
                    spanReact[i] = (rr.L + d * (L - x) / L, rr.R + d * x / L, rr.LN + n * (L - x) / L, rr.RN + n * x / L);
                    if (point) spanPoints[i].Add((x, d, n));
                    else spanUniform[i] = (spanUniform[i].D + d, spanUniform[i].N + n);
                    return;
                }
                if (s < sup[0].S) leftCant.Add((sup[0].S - s, d, n));
                else rightCant.Add((s - sup[^1].S, d, n));
            }

            foreach (var (s, d, n) in uniform) Place(s, d, n, point: false);
            foreach (var (s, d, n) in concentrated) Place(s, d, n, point: true);

            var reactions = new Dictionary<AnalyticalElement, (double D, double N)>();
            double maxMoment = 0, maxShear = 0, govSpan = 0, defl = 0, limit = 0;
            bool cantilever = false;

            void Govern(double moment, double span, bool cant, double f, double lim)
            {
                if (moment > maxMoment)
                {
                    maxMoment = moment;
                    govSpan = span;
                    cantilever = cant;
                }
                if (lim > 0 && (limit <= 0 || f / lim > defl / limit))
                {
                    defl = f;
                    limit = lim;
                }
            }

            for (int i = 0; i < spans.Count; i++)
            {
                double L = spans[i].S1 - spans[i].S0;
                double q = spanUniform[i].D / L, qn = spanUniform[i].N / L;
                double pointMoment = spanPoints[i].Sum(p => p.D * Math.Clamp(p.X, 0, L) * (L - Math.Clamp(p.X, 0, L)) / L);
                double pointDefl = spanPoints[i].Sum(p => p.N * Math.Pow(L, 3) / 48);
                double mSpan = q * L * L / (mono ? 12 : 8) + pointMoment * (mono ? 0.75 : 1);
                double f = ((mono ? 2.0 : 5.0) / 384 * qn * Math.Pow(L, 4) + pointDefl * (mono ? 0.5 : 1)) / ei;
                Govern(mSpan, L, false, f, L / 200);
                var rr = spanReact[i];
                maxShear = Math.Max(maxShear, Math.Max(rr.L, rr.R) - q * h0);
                Add(reactions, spans[i].Left, rr.L, rr.LN);
                Add(reactions, spans[i].Right, rr.R, rr.RN);
            }
            foreach (var (list, support) in new[] { (leftCant, sup[0].Support), (rightCant, sup[^1].Support) })
            {
                if (list.Count == 0) continue;
                double c = Math.Max(0.05, list.Max(x => x.X));
                double mc = list.Sum(x => x.D * x.X), loadD = list.Sum(x => x.D), loadN = list.Sum(x => x.N);
                double f = loadN / c * Math.Pow(c, 4) / (8 * ei);
                Govern(mc, c, true, f, 2 * c / 150);
                maxShear = Math.Max(maxShear, loadD);
                Add(reactions, support, loadD, loadN);
            }

            foreach (var (sp, (d, n)) in reactions)
            {
                if (sp.Role == AnalyticalRole.Vertical)
                {
                    var target = S(sp);
                    target.ReceivedD += d;
                    target.ReceivedN += n;
                    continue;
                }
                // Опирание на ригель — сосредоточенная сила в точке оси балки над ригелем.
                double at = sup.First(x => x.Support == sp).S;
                var point = axisPos.MinBy(p => Math.Abs(p.S - at));
                S(sp).Points.Add(new PointLoad(point.X, point.Z, d, n, m));
            }

            var r = st.R;
            r.Moment = maxMoment;
            r.Shear = Math.Max(0, maxShear);
            r.Span = govSpan;
            r.IsCantilever = cantilever;
            r.DeflectionMm = defl * 1000;
            r.DeflectionLimitMm = limit * 1000;
            r.KDeflection = limit > 0 ? defl / limit : 0;
            BeamCapacity(m, sec);
        }

        private static void Add(Dictionary<AnalyticalElement, (double D, double N)> map, AnalyticalElement key, double d, double n)
        {
            var cur = map.GetValueOrDefault(key);
            map[key] = (cur.D + d, cur.N + n);
        }

        private void BeamCapacity(AnalyticalMember m, AnalyticalSection sec)
        {
            var r = S(m).R;
            double b = Math.Max(0.05, m.SectionB), h = Math.Max(0.05, m.SectionH);
            switch (m.Material.Kind)
            {
                case AnalyticalMaterialKind.Concrete:
                {
                    if (m.Shape == AnalyticalShape.Polyline) b = 0.6;
                    double mult = RcMoment(m.Material, b, h, 0.05, 0.012);
                    double h0 = h - 0.05;
                    // Qb = 0.75·Rbt·b·h0 (c = 2h0) + Qsw = 1.5·qsw·h0, μsw = 0.25 % хомутов А240.
                    double qsw = _rebar.Rsw > 0 ? 170e3 * 0.0025 * b : 0;
                    double qult = 0.75 * m.Material.Rbt * b * h0 + 1.5 * qsw * h0;
                    r.KStrength = mult > 0 ? r.Moment / mult : 0;
                    r.KShear = qult > 0 ? r.Shear / qult : 0;
                    break;
                }
                case AnalyticalMaterialKind.Steel when m.Category == BimCategory.Truss:
                {
                    double chord = Math.Min(0.25, h * 0.15);
                    double force = r.Moment / Math.Max(0.1, h - chord);
                    double panel = Math.Max(0.5, h);
                    double i = Math.Sqrt(Math.Pow(Math.Min(b, chord), 2) / 12);
                    double phi = PhiSteel(panel / Math.Max(1e-3, i) * Math.Sqrt(m.Material.Rc / m.Material.E));
                    r.KStrength = force / Math.Max(1e-6, m.Material.Rc * m.ChordArea);
                    r.KStability = force / Math.Max(1e-6, phi * m.Material.Rc * m.ChordArea);
                    r.Slenderness = panel / Math.Max(1e-3, i);
                    r.Phi = phi;
                    r.KShear = 0;
                    break;
                }
                default:
                {
                    double w = sec.IStrong / Math.Max(1e-6, sec.HStrong / 2);
                    double rb = m.Material.Kind == AnalyticalMaterialKind.Timber ? m.Material.Rbend : m.Material.Rc;
                    double rs = m.Material.Kind == AnalyticalMaterialKind.Timber ? 1.6e3 : 0.58 * m.Material.Rc;
                    r.KStrength = r.Moment / Math.Max(1e-6, rb * w);
                    r.KShear = r.Shear / Math.Max(1e-6, rs * sec.AShear);
                    break;
                }
            }
        }

        // ───────────── Колонны, стены, ядра, диагрид ─────────────

        private void ProcessVertical(AnalyticalElement m)
        {
            var st = S(m);
            var r = st.R;
            st.HasResult = true;
            double nd = m.SelfD + st.ReceivedD, nn = m.SelfN + st.ReceivedN;
            r.AxialForce = nd;
            r.Shear = st.LatV;
            double height = m.Height;

            switch (m.Category)
            {
                case BimCategory.Wall:
                    WallCapacity((AnalyticalPanel)m, nd);
                    break;
                case BimCategory.Core:
                    CoreCapacity((AnalyticalMember)m, nd);
                    break;
                case BimCategory.Diagrid:
                    DiagridCapacity((AnalyticalMember)m, nd);
                    break;
                default:
                    ColumnCapacity((AnalyticalMember)m, nd, height);
                    break;
            }

            // Передача N вниз: колонна — в центре, стена — вдоль оси, ядро — по контуру коробки, диагрид — по кольцу.
            var pts = m.TransferPoints!;
            double sum = Math.Max(1e-9, pts.Sum(p => p.W));
            TransferDown(m, pts.Select(p => (p.X, p.Z, nd * p.W / sum, nn * p.W / sum)).ToList(), punchSource: m.IsColumnLike, m.Receivers);
        }

        private void ColumnCapacity(AnalyticalMember m, double n, double height)
        {
            var st = S(m);
            var r = st.R;
            var sec = m.Section;
            double i = Math.Sqrt(sec.IWeak / Math.Max(1e-9, sec.A));
            double l0 = (m.Material.Kind == AnalyticalMaterialKind.Concrete ? 0.7 : 1.0) * height;
            double lambda = l0 / Math.Max(1e-4, i);
            r.Slenderness = lambda;
            double eccentricity = Math.Max(Math.Max(height / 600, sec.HWeak / 30), 0.01);
            double moment = n * eccentricity + st.LatM;
            r.Moment = moment;

            switch (m.Material.Kind)
            {
                case AnalyticalMaterialKind.Concrete:
                {
                    const double mu = 0.015, a = 0.04;
                    double phi = PhiConcrete(lambda / Math.Sqrt(12));
                    double n0 = m.Material.Rc * sec.A + _rebar.Rsc * 1000 * mu * sec.A;
                    double hs = sec.HStrong;
                    double m0 = _rebar.Rs * 1000 * (mu / 2 * sec.A) * Math.Max(0.05, hs - 2 * a);
                    double mu_n = n <= n0 ? m0 + 0.5 * n * hs * (1 - n / n0) : 0;
                    double b = sec.A / Math.Max(0.05, hs);
                    double qult = 1.5 * m.Material.Rbt * b * Math.Max(0.05, hs - a);
                    r.Phi = phi;
                    r.KStability = n / Math.Max(1e-6, phi * n0);
                    r.KStrength = n > n0 ? n / n0 : moment / Math.Max(1e-6, mu_n);
                    r.KShear = r.Shear / Math.Max(1e-6, qult);
                    break;
                }
                case AnalyticalMaterialKind.Steel:
                {
                    double phi = PhiSteel(lambda * Math.Sqrt(m.Material.Rc / m.Material.E));
                    double n0 = m.Material.Rc * sec.A;
                    double w = sec.IWeak / Math.Max(1e-6, sec.HWeak / 2);
                    r.Phi = phi;
                    r.KStability = n / Math.Max(1e-6, phi * n0);
                    r.KStrength = n / Math.Max(1e-6, n0) + moment / Math.Max(1e-6, m.Material.Rc * w);
                    r.KShear = r.Shear / Math.Max(1e-6, 0.58 * m.Material.Rc * sec.AShear);
                    break;
                }
                default:
                {
                    // Дерево (СП 64.13330): φ = 1 − 0.8(λ/100)² при λ ≤ 70, иначе 3000/λ²; кладка — по таблице СП 15.
                    double phi = m.Material.Kind == AnalyticalMaterialKind.Timber
                        ? lambda <= 70 ? 1 - 0.8 * Math.Pow(lambda / 100, 2) : 3000 / (lambda * lambda)
                        : PhiConcrete(lambda / Math.Sqrt(12));
                    double rc = Math.Max(100, m.Material.Rc);
                    double w = sec.IWeak / Math.Max(1e-6, sec.HWeak / 2);
                    double rb = m.Material.Kind == AnalyticalMaterialKind.Timber ? m.Material.Rbend : 0.1 * rc;
                    r.Phi = phi;
                    r.KStability = n / Math.Max(1e-6, phi * rc * sec.A);
                    r.KStrength = n / Math.Max(1e-6, rc * sec.A) + moment / Math.Max(1e-6, rb * w);
                    r.KShear = r.Shear / Math.Max(1e-6, (m.Material.Kind == AnalyticalMaterialKind.Timber ? 1.6e3 : 120) * sec.A);
                    break;
                }
            }
        }

        /// <summary>
        /// Несущая стена: N на всю длину, гибкость из плоскости по несущей толщине (l0 = 0.9·H), момент от горизонтальных сил —
        /// в плоскости (ж/б: Mu(N) = M0 + 0.5·N·L·(1 − N/N0); кладка без армирования: 0.5·N·L·(1 − N/N0)) и от случайного
        /// эксцентриситета — из плоскости.
        /// </summary>
        private void WallCapacity(AnalyticalPanel m, double n)
        {
            var st = S(m);
            var r = st.R;
            double t = Math.Max(0.05, m.Thickness), length = Math.Max(0.1, m.Width), height = m.Height;
            double area = t * length;
            double l0 = 0.9 * height;
            double lambdaH = l0 / t;
            r.Slenderness = l0 / (t / Math.Sqrt(12));
            double phi = m.Material.Kind == AnalyticalMaterialKind.Timber ? Math.Max(0.2, 1 - 0.8 * Math.Pow(r.Slenderness / 100, 2)) : PhiConcrete(lambdaH);
            r.Phi = phi;
            double eOut = Math.Max(Math.Max(t / 30, height / 600), 0.01);
            double mOut = n * eOut;
            r.Moment = Math.Max(st.LatM, mOut);
            double n0, m0In, m0Out, qult;
            if (m.Material.Kind == AnalyticalMaterialKind.Concrete)
            {
                const double mu = 0.005, a = 0.03;
                n0 = m.Material.Rc * area + _rebar.Rsc * 1000 * mu * area;
                m0In = _rebar.Rs * 1000 * (mu / 2 * area) * Math.Max(0.1, length - 2 * a);
                m0Out = _rebar.Rs * 1000 * (mu / 2 * area) * Math.Max(0.02, t - 2 * a);
                qult = 0.75 * m.Material.Rbt * t * length * 0.8;
            }
            else
            {
                n0 = Math.Max(100, m.Material.Rc) * area;
                m0In = 0;
                m0Out = 0;
                double sigma = n / Math.Max(1e-6, area);
                qult = ((m.Material.Kind == AnalyticalMaterialKind.Timber ? 1.6e3 : 120) + 0.8 * 0.7 * sigma) * area;
            }
            double nn = Math.Min(n, n0);
            double muIn = m0In + 0.5 * nn * length * (1 - nn / n0);
            double muOut = m0Out + 0.5 * nn * t * (1 - nn / n0);
            r.KStability = n / Math.Max(1e-6, phi * n0);
            double kIn = st.LatM > 1e-6 ? st.LatM / Math.Max(1e-6, muIn) : 0;
            double kOut = mOut / Math.Max(1e-6, muOut + (m.Material.Kind == AnalyticalMaterialKind.Concrete ? 0 : 0.5 * nn * t));
            r.KStrength = n > n0 ? n / n0 : Math.Max(kIn, Math.Min(kOut, r.KStability));
            r.KShear = r.Shear / Math.Max(1e-6, qult);
        }

        /// <summary>Ядро жёсткости — консольная коробчатая стойка: N и момент от горизонтальных сил всех вышележащих этажей.</summary>
        private void CoreCapacity(AnalyticalMember m, double n)
        {
            var st = S(m);
            var r = st.R;
            double t = m.WallT;
            double area = m.Section.A;
            double inertia = m.Section.IStrong;
            double i = Math.Sqrt(inertia / Math.Max(1e-6, area));
            double l0 = 2.0 * m.Height;
            r.Slenderness = l0 / Math.Max(1e-3, i);
            double phi = PhiConcrete(r.Slenderness / Math.Sqrt(12));
            r.Phi = phi;
            const double mu = 0.005;
            double side = Math.Min(m.SectionB, m.SectionH);
            double n0 = m.Material.Rc * area + _rebar.Rsc * 1000 * mu * area;
            double m0 = _rebar.Rs * 1000 * (mu / 2 * area) * Math.Max(0.1, side - 2 * t);
            double nn = Math.Min(n, n0);
            double mun = m0 + 0.5 * nn * side * (1 - nn / n0);
            double moment = n * Math.Max(m.Height / 600, 0.01) + st.LatM;
            r.Moment = moment;
            r.KStability = n / Math.Max(1e-6, phi * n0);
            r.KStrength = n > n0 ? n / n0 : moment / Math.Max(1e-6, mun);
            r.KShear = r.Shear / Math.Max(1e-6, 0.75 * m.Material.Rbt * 2 * t * side);
        }

        /// <summary>Диагрид этажа: BarCount наклонных труб на кольце радиуса R; усилие стержня = (N/n + 2M/(n·R)) / cos θ.</summary>
        private void DiagridCapacity(AnalyticalMember m, double n)
        {
            var st = S(m);
            var r = st.R;
            var pipe = m.Section;
            var (len, cos) = m.InclinedBar();
            double force = (n / m.BarCount + 2 * st.LatM / (m.BarCount * Math.Max(0.5, m.Radius))) / Math.Max(0.2, cos);
            double i = Math.Sqrt(pipe.IWeak / Math.Max(1e-9, pipe.A));
            double lambda = len / Math.Max(1e-4, i);
            double phi = PhiSteel(lambda * Math.Sqrt(m.Material.Rc / m.Material.E));
            r.Slenderness = lambda;
            r.Phi = phi;
            r.Moment = st.LatM;
            r.KStrength = force / Math.Max(1e-6, m.Material.Rc * pipe.A);
            r.KStability = force / Math.Max(1e-6, phi * m.Material.Rc * pipe.A);
            r.KShear = r.Shear / Math.Max(1e-6, m.BarCount * 0.58 * m.Material.Rc * pipe.AShear);
        }

        // ───────────── Фундаменты ─────────────

        /// <summary>
        /// Фундаментная плита: давление на грунт p = ΣN / A (нормативное — против R грунта), изгиб от отпора грунта между опорами
        /// (колонны и стены — «опоры» перевёрнутой плиты), продавливание колоннами по контуру h0/2 за вычетом отпора в контуре.
        /// </summary>
        private void ProcessFoundation(AnalyticalPanel m)
        {
            var st = S(m);
            var r = st.R;
            st.HasResult = true;
            double area = m.NetArea;
            var (selfD, selfN) = (m.AreaSelfD, m.AreaSelfN);
            double loadD = st.Points.Sum(p => p.D), loadN = st.Points.Sum(p => p.N);
            double pD = loadD / area + selfD, pN = loadN / area + selfN;
            double net = Math.Max(0, pD - selfD);
            r.DesignLoad = pD;
            r.AxialForce = loadD;
            r.KSoil = pN / Math.Max(1, s.SoilBearingKPa);

            var sources = st.Points.Where(p => p.From is { Role: AnalyticalRole.Vertical }).Select(p => p.From!).Distinct().ToList();
            if (sources.Count > 0)
            {
                var hull = Geometry2D.ConvexHull(sources.SelectMany(sp => sp.Anchors())).ToArray();
                var disc = AnalyticalGeometry.InscribedDisc(hull);
                double span = 0, cantilever = 0;
                // Квадратурные точки подготовлены схемой; расстояние до оболочки опор — один раз на точку.
                foreach (var (x, z, _) in m.Samples!)
                {
                    var (sp, d) = Nearest(sources, x, z);
                    if (hull.Length < 3)
                    {
                        cantilever = Math.Max(cantilever, d);
                        continue;
                    }
                    bool contains = AnalyticalGeometry.InDisc(disc, x, z) || AnalyticalGeometry.Pip(hull, x, z);
                    double edge = contains ? 0 : AnalyticalGeometry.DistBoundary(hull, x, z);
                    if (contains || edge < 0.05) span = Math.Max(span, LocalSpan(m, sp, x, z, d));
                    else cantilever = Math.Max(cantilever, edge);
                }
                FinishSlabChecks(m, net, net / 1.15, span, cantilever, 0, onGrade: false);
                // Прогиб плиты на упругом основании ограничивается грунтом — проверка относится к осадкам.
                r.KDeflection = 0;
                r.DeflectionMm = 0;
                r.DeflectionLimitMm = 0;
                double h = Math.Max(0.05, m.ThickStruct);
                double h0 = Math.Max(0.05, h - 0.07);
                foreach (var group in st.Points.Where(p => p.Punch && p.From is { IsColumnLike: true }).GroupBy(p => p.From!))
                    // Отпор грунта внутри основания пирамиды продавливания (грани под 45° — контур на h0) уменьшает F.
                    AddPunchingNode(m, group.Key, group.Sum(p => p.D), net, h0, m.Top, offsetForLoad: h0);
            }
            else r.KStrength = 0.02;
            if (r.KSoil > 1) r.Notes.Add($"Давление под подошвой {pN:0} кПа больше R = {s.SoilBearingKPa:0} кПа — увеличьте площадь подошвы");
        }

        // ───────────── Нагрузки без проверки, оболочки ─────────────

        /// <summary>Ненесущий элемент (витраж, лестница, бассейн, терраса): собственный вес по оси или площади и силы сверху — вниз.</summary>
        private void ProcessLoadOnly(AnalyticalElement m)
        {
            // Ось с шагом 1 м (вес — длина участка) или квадратурные точки площадки (вес — площадь).
            var weights = m.TransferPoints!;
            double sum = Math.Max(1e-9, weights.Sum(p => p.W));
            var pts = weights.Select(p => (p.X, p.Z, m.SelfD * p.W / sum, m.SelfN * p.W / sum)).ToList();
            pts.AddRange(S(m).Points.Select(p => (p.X, p.Z, p.D, p.N)));
            TransferDown(m, pts, punchSource: false, m.Receivers);
        }

        /// <summary>Купол: собственный вес и снег (μ ≈ 0.6 в среднем по куполу) передаются по контуру опирания; σ = N_φ / t.</summary>
        private void ProcessShell(AnalyticalPanel m)
        {
            var st = S(m);
            double planArea = Math.PI * m.Width * m.Depth / 4;
            double total = m.SelfD + 0.6 * s.SnowKPa * planArea + st.Points.Sum(p => p.D);
            double totalN = m.SelfN + 0.42 * s.SnowKPa * planArea + st.Points.Sum(p => p.N);
            var ring = m.SupportRing!;
            double perimeter = Geometry2D.Perimeter(ring);
            TransferDown(m, ring.Select(p => (p.X, p.Z, total / ring.Count, totalN / ring.Count)).ToList(), punchSource: false, m.Receivers);
            var r = st.R;
            st.HasResult = true;
            double nPhi = total / Math.Max(0.1, perimeter);
            r.AxialForce = total;
            r.DesignLoad = total / Math.Max(0.1, planArea);
            r.Shear = nPhi;
            r.Span = Math.Max(m.Width, m.Depth);
            r.KStrength = nPhi / Math.Max(1e-6, m.ThickStruct * Math.Max(100, m.Material.Rc));
            // Местная устойчивость оболочки: q_cr = 0.2·E·(t/R)² (упрощённо, с коэффициентом несовершенств).
            double radius = Math.Max(1, (m.Width + m.Depth) / 4 + m.ModelHeight * m.ModelHeight / Math.Max(1, m.Width));
            double qcr = 0.2 * m.Material.E * 0.85 / (1 + _concrete.Creep) * Math.Pow(m.ThickStruct / radius, 2);
            r.KStability = r.DesignLoad / Math.Max(1e-6, qcr);
        }

        // ───────────── Передача нагрузки вниз ─────────────

        /// <summary>
        /// Каждая точка нагрузки уходит в опору под низом элемента: колонну/стену (верх совпадает с низом), сквозь плиту — колонну
        /// под плитой (без продавливания), балку, плиту или фундамент (точечная сила; от колонн — с проверкой продавливания),
        /// иначе — грунт (низ на уровне земли) или ближайший элемент ниже. Опоры находит схема.
        /// </summary>
        /// <param name="receivers">Получатели первых точек, найденные построителем схемы; остальные запрашиваются у схемы здесь.</param>
        private void TransferDown(AnalyticalElement m, List<(double X, double Z, double D, double N)> loads, bool punchSource, AnalyticalElement?[]? receivers = null)
        {
            if (loads.Count == 0) return;
            bool missing = false;
            for (int k = 0; k < loads.Count; k++)
            {
                var (x, z, d, n) = loads[k];
                var receiver = receivers is not null && k < receivers.Length ? receivers[k] : model.SupportBelow(m, x, z);
                if (receiver is null)
                {
                    if (m.Base > GroundLevel) missing = true;
                    continue;
                }
                if (receiver.Role == AnalyticalRole.Vertical)
                {
                    var target = S(receiver);
                    target.ReceivedD += d;
                    target.ReceivedN += n;
                }
                else S(receiver).Points.Add(new PointLoad(x, z, d, n, m, punchSource));
            }
            var notes = S(m).R.Notes;
            if (missing && !notes.Contains("Нет опоры снизу")) notes.Add("Нет опоры снизу");
        }
    }
}
