using System.Diagnostics;
using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Analytical;

namespace AIArchitectStudio.Services;

/// <summary>Напряжения в несущем элементе (МПа) против расчётного сопротивления материала и приведённого сопротивления сечения.</summary>
/// <param name="SigmaN">N / A.</param>
/// <param name="SigmaMx">Mx / Wx (изгиб в плоскости наибольшей жёсткости: балка, плита).</param>
/// <param name="SigmaMy">My / Wy (изгиб из плоскости: случайный эксцентриситет и горизонтальные силы стойки).</param>
/// <param name="R">Расчётное сопротивление материала: Rb (бетон), Ry (сталь), Rc / Rи (дерево, кладка).</param>
/// <param name="RSection">Приведённое сопротивление сечения с арматурой: сжатие N0 / A, изгиб M_ult / W (ж/б); у стали и дерева — R.</param>
/// <param name="StressRatio">η = γn·max(σN / R_N, σM / R_M(N)) (ж/б) или γn·(σN + σM) / R (сталь, дерево, кладка).</param>
public sealed record ElementStressRow(
    string ElementId, string Title, string Kind, string Material,
    double N, double Mx, double My, double Q,
    double SigmaN, double SigmaMx, double SigmaMy, double R, double RSection,
    double Tau, double RShear, double StressRatio, double Utilization, string Governing, bool Reference = false)
{
    /// <summary>σ_max = N/A + Mx/Wx + My/Wy (крайнее волокно), МПа.</summary>
    public double SigmaMax => SigmaN + SigmaMx + SigmaMy;
}

/// <summary>Узел продавливания: положение колонны (внутренняя — 4 грани контура, крайняя — 3, угловая — 2), τ = F / (u·h0), МПа.</summary>
public sealed record PunchingStressRow(string ColumnId, string SlabId, string Title, string Position, int Faces, double Force, double Perimeter, double H0,
    double Tau, double Rbt, double ConcreteCapacity, double SteelCapacity, double Ratio);

/// <summary>Критерий логического аудита: номер, название, итог, пояснение с числами и элементы-нарушители.</summary>
public sealed record FeaAuditCriterion(int Number, string Title, bool Passed, string Detail, IReadOnlyList<string> Offenders);

/// <summary>Итог аудита расчётной схемы: баланс вертикальных сил, 5 критериев, таблицы напряжений и продавливания.</summary>
public sealed class FeaAuditReport
{
    public double AppliedLoad { get; init; }
    public double GroundReaction { get; init; }
    public double LostLoad { get; init; }
    public double ImbalancePercent => AppliedLoad > 0 ? (AppliedLoad - GroundReaction) / AppliedLoad * 100 : 0;
    public List<FeaAuditCriterion> Criteria { get; } = [];
    public List<ElementStressRow> Stresses { get; } = [];
    public List<PunchingStressRow> Punching { get; } = [];
    public int ElementCount { get; init; }
    public double ElapsedMs { get; set; }
    public bool Passed => Criteria.All(c => c.Passed);

    public ElementStressRow? StressOf(string id) => Stresses.FirstOrDefault(s => s.ElementId == id);
}

/// <summary>
/// Логический аудит КЭ-экспресс-расчёта: независимая от солвера проверка того, что схема и результат физически осмысленны.
/// Напряжения элементов (МПа) — по усилиям результата и сечениям схемы: σ_max = N/A + Mx/Wx + My/Wy, τ = 1,5·Q / A_ср,
/// продавливание τ = F / (u·h0) с положением колонны и F_sw,ult. Пять критериев:
/// 1) статика: Σ реакций грунта = Σ приложенных расчётных нагрузок (ни одна нагрузка не потеряна);
/// 2) неразрывность потока усилий: N соосной колонны ниже ≥ N колонны выше, у балок Σ опорных реакций = собранной нагрузке,
///    у каждого элемента «пришло + своё = передано + грунту»;
/// 3) монотонность отклика: рост нагрузки увеличивает M, f, σ; рост пролёта — M, f, σ; рост сечения и класса бетона снижает K_исп;
/// 4) согласованность напряжений и K_исп: элемент с η &gt; 1 не может иметь K_исп &lt; 1;
/// 5) нет висящих нагруженных узлов: путь передачи каждого нагруженного элемента замыкается на грунт.
/// </summary>
public static class FeaLogicalAuditService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Допуск баланса сил: 0,1 % приложенной нагрузки (округления квадратурных точек и плавающей запятой).</summary>
    public const double BalanceTolerance = 1e-3;

    public static FeaAuditReport Audit(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, StructuralLoadSettings settings,
        bool monotonicity = true)
    {
        var sw = Stopwatch.StartNew();
        var model = AnalyticalModelBuilder.Build(elements, groups, settings);
        var result = StructuralAnalysisService.Analyze(model);
        var ledger = result.Ledger ?? new LoadLedger(model.Elements);
        var report = new FeaAuditReport
        {
            AppliedLoad = ledger.TotalSelf,
            GroundReaction = ledger.TotalGround,
            LostLoad = ledger.TotalLost,
            ElementCount = model.Elements.Count
        };
        report.Stresses.AddRange(Stresses(model, result));
        report.Punching.AddRange(PunchingRows(model, result));

        report.Criteria.Add(StaticBalance(report));
        report.Criteria.Add(Continuity(model, ledger));
        report.Criteria.Add(monotonicity
            ? Monotonicity(elements, groups, model.Settings, result)
            : new FeaAuditCriterion(3, "Монотонность отклика", true, "не проверялась (быстрый режим)", []));
        report.Criteria.Add(StressConsistency(report.Stresses));
        report.Criteria.Add(LoadPaths(model, ledger));
        report.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return report;
    }

    // ───────────────────────────── Напряжения ─────────────────────────────

    /// <summary>Напряжения несущих элементов по наихудшему этажу (элемент схемы с результатом, давшим K_исп элемента).</summary>
    public static List<ElementStressRow> Stresses(IAnalyticalModel model, StructuralAnalysisResult result)
    {
        var rows = new List<ElementStressRow>();
        var rebar = model.Settings.RebarProps();
        double gammaN = model.Settings.GammaN;
        foreach (var m in model.Elements)
        {
            if (!m.Checked || result.For(m.Source.Id) is not { } r || r.Floor != m.Floor || rows.Any(x => x.ElementId == m.Source.Id)) continue;
            var row = m switch
            {
                AnalyticalMember { Role: AnalyticalRole.Vertical, Category: BimCategory.Column } bar => ColumnStress(bar, r, rebar, gammaN),
                AnalyticalMember { Role: AnalyticalRole.Line } bar when bar.Category != BimCategory.Truss && bar.Shape != AnalyticalShape.Polyline => BeamStress(bar, r, rebar, gammaN),
                AnalyticalPanel { Role: AnalyticalRole.Slab } panel when r.Span > 0 => SlabStress(panel, r, rebar, gammaN, 0.03),
                AnalyticalPanel { Role: AnalyticalRole.Foundation } panel when r.Span > 0 => SlabStress(panel, r, rebar, gammaN, 0.07),
                AnalyticalPanel { Role: AnalyticalRole.Vertical, Category: BimCategory.Wall } wall => WallStress(wall, r, gammaN),
                _ => null
            };
            if (row is not null) rows.Add(row);
        }
        return rows.OrderByDescending(x => x.StressRatio).ToList();
    }

    private static string MaterialTitle(AnalyticalMaterial mat) => mat.Kind switch
    {
        AnalyticalMaterialKind.Concrete => "железобетон",
        AnalyticalMaterialKind.Steel => "сталь",
        AnalyticalMaterialKind.Timber => "дерево",
        AnalyticalMaterialKind.Masonry => "кладка",
        _ => "стекло"
    };

    private static ElementStressRow ColumnStress(AnalyticalMember m, ElementStructuralResult r, (double Rs, double Rsc, double Rsw) rebar, double gammaN)
    {
        var sec = m.Section;
        double n = r.AxialForce, moment = r.Moment, q = r.Shear;
        double a = Math.Max(1e-9, sec.A);
        double w = sec.IWeak / Math.Max(1e-6, sec.HWeak / 2);
        double sN = n / a / 1000, sM = moment / Math.Max(1e-9, w) / 1000;
        double tau = 1.5 * q / Math.Max(1e-9, sec.AShear > 0 ? sec.AShear : a) / 1000;
        double rMat = m.Material.Rc / 1000, rSection, ratio, rShear;
        switch (m.Material.Kind)
        {
            case AnalyticalMaterialKind.Concrete:
            {
                // Те же формулы, что у солвера: N0 = Rb·A + Rsc·μ·A; Mu(N) = M0 + 0,5·N·h·(1 − N/N0) (μ = 1,5 %, a = 40 мм).
                double mu = StructuralAnalysisService.RebarRatio(m, 0.015), cover = StructuralAnalysisService.RebarAxisDistance(m, 0.04);
                double n0 = m.Material.Rc * sec.A + rebar.Rsc * 1000 * mu * sec.A;
                double hs = sec.HStrong;
                double m0 = rebar.Rs * 1000 * (mu / 2 * sec.A) * Math.Max(0.05, hs - 2 * cover);
                double muN = n <= n0 ? m0 + 0.5 * n * hs * (1 - n / n0) : 0;
                double rN = n0 / a / 1000, rM = muN / Math.Max(1e-9, w) / 1000;
                rSection = rN;
                ratio = gammaN * Math.Max(rN > 0 ? sN / rN : 0, n > n0 ? double.PositiveInfinity : rM > 0 ? sM / rM : 0);
                if (double.IsInfinity(ratio)) ratio = gammaN * sN / Math.Max(1e-9, rN);
                rShear = m.Material.Rbt / 1000;
                break;
            }
            case AnalyticalMaterialKind.Steel:
                rSection = rMat;
                ratio = gammaN * (sN + sM) / Math.Max(1e-9, rMat);
                rShear = 0.58 * rMat;
                break;
            default:
            {
                double rc = Math.Max(100, m.Material.Rc) / 1000;
                double rb = m.Material.Kind == AnalyticalMaterialKind.Timber ? m.Material.Rbend / 1000 : 0.1 * rc;
                rMat = rc;
                rSection = rc;
                ratio = gammaN * (sN / rc + sM / Math.Max(1e-9, rb));
                rShear = (m.Material.Kind == AnalyticalMaterialKind.Timber ? 1.6e3 : 120) / 1000;
                break;
            }
        }
        return new ElementStressRow(m.Source.Id, m.Source.DisplayTitle, "Колонна", MaterialTitle(m.Material), n, 0, moment, q, sN, 0, sM, rMat, rSection,
            tau, rShear, ratio, r.Utilization, r.Governing);
    }

    private static ElementStressRow BeamStress(AnalyticalMember m, ElementStructuralResult r, (double Rs, double Rsc, double Rsw) rebar, double gammaN)
    {
        var sec = m.Section;
        double moment = r.Moment, q = r.Shear;
        double b = Math.Max(0.05, m.SectionB), h = Math.Max(0.05, m.SectionH);
        double w = m.Material.Kind == AnalyticalMaterialKind.Concrete ? b * h * h / 6 : sec.IStrong / Math.Max(1e-6, sec.HStrong / 2);
        double aShear = m.Material.Kind == AnalyticalMaterialKind.Concrete ? b * h : sec.AShear > 0 ? sec.AShear : sec.A;
        double sM = moment / Math.Max(1e-9, w) / 1000;
        double tau = 1.5 * q / Math.Max(1e-9, aShear) / 1000;
        double rMat = (m.Material.Kind == AnalyticalMaterialKind.Timber ? m.Material.Rbend : m.Material.Rc) / 1000, rSection, rShear;
        if (m.Material.Kind == AnalyticalMaterialKind.Concrete)
        {
            double mult = StructuralAnalysisService.RcUltimateMoment(m.Material.Rc, rebar.Rs, b, h, StructuralAnalysisService.RebarAxisDistance(m, 0.05), StructuralAnalysisService.RebarRatio(m, 0.012));
            rSection = mult / Math.Max(1e-9, w) / 1000;
            rShear = m.Material.Rbt / 1000;
        }
        else
        {
            rSection = rMat;
            rShear = m.Material.Kind == AnalyticalMaterialKind.Timber ? 1.6 : 0.58 * rMat;
        }
        double ratio = gammaN * sM / Math.Max(1e-9, rSection);
        return new ElementStructuralRowFactory(m, r).Row("Балка", MaterialTitle(m.Material), 0, moment, 0, q, 0, sM, 0, rMat, rSection, tau, rShear, ratio);
    }

    private static ElementStressRow SlabStress(AnalyticalPanel m, ElementStructuralResult r, (double Rs, double Rsc, double Rsw) rebar, double gammaN, double a)
    {
        double h = Math.Max(0.05, m.Thickness);
        double w = h * h / 6;
        double sM = r.Moment / w / 1000;
        double tau = 1.5 * r.Shear / h / 1000;
        double rMat, rSection, rShear;
        if (m.Material.Kind == AnalyticalMaterialKind.Concrete)
        {
            rMat = m.Material.Rc / 1000;
            rSection = StructuralAnalysisService.RcUltimateMoment(m.Material.Rc, rebar.Rs, 1, h, StructuralAnalysisService.RebarAxisDistance(m, a), StructuralAnalysisService.RebarRatio(m, 0.004)) / w / 1000;
            rShear = m.Material.Rbt / 1000;
        }
        else
        {
            rMat = (m.Material.Kind == AnalyticalMaterialKind.Steel ? m.Material.Rc : Math.Max(1e3, m.Material.Rbend)) / 1000;
            rSection = rMat;
            rShear = 0.58 * rMat;
        }
        double ratio = gammaN * sM / Math.Max(1e-9, rSection);
        string kind = m.Role == AnalyticalRole.Foundation ? "Фундаментная плита" : "Плита";
        return new ElementStructuralRowFactory(m, r).Row(kind, MaterialTitle(m.Material), 0, r.Moment, 0, r.Shear, 0, sM, 0, rMat, rSection, tau, rShear, ratio);
    }

    /// <summary>Стена: σ = N / (L·t) на всю длину (справочно; проверка в плоскости — по формуле солвера), η = σ / R.</summary>
    private static ElementStressRow WallStress(AnalyticalPanel m, ElementStructuralResult r, double gammaN)
    {
        double length = Math.Max(0.1, Math.Sqrt(Math.Pow(m.AxisEnd.X - m.AxisStart.X, 2) + Math.Pow(m.AxisEnd.Z - m.AxisStart.Z, 2)));
        double t = Math.Max(0.05, m.Thickness);
        double sN = r.AxialForce / (length * t) / 1000;
        double rMat = Math.Max(100, m.Material.Rc) / 1000;
        double tau = 1.5 * r.Shear / (length * t) / 1000;
        double rShear = m.Material.Kind == AnalyticalMaterialKind.Concrete ? m.Material.Rbt / 1000 : 0.12;
        return new ElementStructuralRowFactory(m, r).Row("Стена", MaterialTitle(m.Material), r.AxialForce, 0, 0, r.Shear, sN, 0, 0, rMat, rMat, tau, rShear, gammaN * sN / rMat,
            consistencyChecked: false);
    }

    /// <summary>Строка таблицы по элементу схемы и его результату.</summary>
    private readonly struct ElementStructuralRowFactory(AnalyticalElement m, ElementStructuralResult r)
    {
        public ElementStressRow Row(string kind, string material, double n, double mx, double my, double q, double sN, double sMx, double sMy, double rMat, double rSection,
            double tau, double rShear, double ratio, bool consistencyChecked = true) =>
            new(m.Source.Id, m.Source.DisplayTitle, kind, material, n, mx, my, q, sN, sMx, sMy, rMat, rSection, tau, rShear,
                ratio, r.Utilization, r.Governing, !consistencyChecked);
    }

    /// <summary>Узлы продавливания: положение по числу граней расчётного контура внутри плиты, τ = F / (u·h0).</summary>
    public static List<PunchingStressRow> PunchingRows(IAnalyticalModel model, StructuralAnalysisResult result)
    {
        var rows = new List<PunchingStressRow>();
        var byId = model.Elements.GroupBy(e => e.Source.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var node in result.PunchingNodes)
        {
            int faces = 4;
            if (byId.TryGetValue(node.SlabId, out var slab) && node.Contour.Count >= 8 && node.Contour.Count % 4 == 0)
            {
                int perSide = node.Contour.Count / 4;
                faces = 0;
                for (int side = 0; side < 4; side++)
                {
                    int inside = 0;
                    for (int k = 0; k < perSide; k++)
                    {
                        var p = node.Contour[side * perSide + k];
                        var q = node.Contour[(side * perSide + k + 1) % node.Contour.Count];
                        if (AnalyticalGeometry.InsideNet(slab, (p.X + q.X) / 2, (p.Z + q.Z) / 2)) inside++;
                    }
                    if (inside * 2 >= perSide) faces++;
                }
            }
            string position = faces >= 4 ? "внутренняя" : faces == 3 ? "крайняя" : "угловая";
            double rbt = byId.TryGetValue(node.SlabId, out var s) ? s.Material.Rbt / 1000 : 0;
            double tau = node.Force / Math.Max(1e-9, node.Perimeter * node.H0) / 1000;
            string title = byId.TryGetValue(node.ColumnId, out var c) ? c.Source.DisplayTitle : node.ColumnId;
            rows.Add(new PunchingStressRow(node.ColumnId, node.SlabId, title, position, faces, node.Force, node.Perimeter, node.H0, tau, rbt,
                node.Capacity - node.SteelCapacity, node.SteelCapacity, node.Ratio));
        }
        return rows;
    }

    // ───────────────────────────── Критерии ─────────────────────────────

    private static FeaAuditCriterion StaticBalance(FeaAuditReport report)
    {
        double applied = report.AppliedLoad, ground = report.GroundReaction, lost = report.LostLoad;
        double residual = applied - ground - lost;
        bool ok = applied <= 0 || (Math.Abs(residual) <= BalanceTolerance * applied && lost <= BalanceTolerance * applied);
        string detail = $"ΣF_верт = {applied.ToString("N1", Ru)} кН, ΣR_y грунта = {ground.ToString("N1", Ru)} кН, без опоры {lost.ToString("N1", Ru)} кН, " +
                        $"невязка {(applied > 0 ? residual / applied * 100 : 0).ToString("0.000", Ru)} %";
        return new FeaAuditCriterion(1, "Уравнение статики ΣR_y = ΣF_верт", ok, detail, []);
    }

    private static string Key(AnalyticalElement m) => m.Floor > 0 ? $"{m.Source.DisplayTitle} (этаж {m.Floor + 1})" : m.Source.DisplayTitle;

    private static FeaAuditCriterion Continuity(IAnalyticalModel model, LoadLedger ledger)
    {
        var offenders = new List<string>();
        var elements = model.Elements;
        int pairs = 0, beams = 0, conserved = 0;
        // Соосные колонны: N нижней ≥ N верхней (N = переданное вниз).
        var columns = elements.Where(e => e.Role == AnalyticalRole.Vertical && e.IsColumnLike).ToList();
        foreach (var upper in columns)
            foreach (var lower in columns)
            {
                // Верхняя колонна стоит на нижней напрямую или сквозь плиту перекрытия (зазор до 0,6 м).
                double gap = upper.Base - lower.Top;
                if (ReferenceEquals(upper, lower) || lower.GroupId != upper.GroupId || gap < -0.05 || gap > 0.6) continue;
                if (Math.Abs(lower.Center.X - upper.Center.X) > 0.05 || Math.Abs(lower.Center.Z - upper.Center.Z) > 0.05) continue;
                pairs++;
                double nu = ledger.Out[upper.Index] + ledger.Ground[upper.Index], nl = ledger.Out[lower.Index] + ledger.Ground[lower.Index];
                if (nl < nu - 1e-6 * Math.Max(1, nu)) offenders.Add($"{Key(lower)}: N = {nl:0.#} кН < N выше {nu:0.#} кН");
            }
        foreach (var (index, (load, reactions)) in ledger.Beams)
        {
            beams++;
            if (Math.Abs(load - reactions) > 1e-6 * Math.Max(1, load)) offenders.Add($"{Key(elements[index])}: нагрузка {load:0.##} кН ≠ Σ реакций {reactions:0.##} кН");
        }
        for (int i = 0; i < elements.Count; i++)
        {
            double inflow = ledger.In[i] + ledger.Self[i], outflow = ledger.Out[i] + ledger.Ground[i] + ledger.Lost[i];
            if (inflow <= 1e-9) continue;
            conserved++;
            if (Math.Abs(inflow - outflow) > BalanceTolerance * Math.Max(1, inflow))
                offenders.Add($"{Key(elements[i])}: пришло {inflow:0.##} кН, передано {outflow:0.##} кН");
        }
        return new FeaAuditCriterion(2, "Неразрывность потока усилий", offenders.Count == 0,
            $"соосных пар колонн {pairs}, балок {beams}, элементов с балансом «пришло = передано» {conserved}", offenders.Take(12).ToList());
    }

    private static FeaAuditCriterion StressConsistency(IReadOnlyList<ElementStressRow> rows)
    {
        var offenders = rows.Where(r => !r.Reference && r.StressRatio > 1 + 1e-9 && r.Utilization < 1 - 1e-9)
            .Select(r => $"{r.Title}: η = {r.StressRatio:0.###}, K_исп = {r.Utilization:0.###}").ToList();
        int checkedRows = rows.Count(r => !r.Reference);
        int over = rows.Count(r => !r.Reference && r.StressRatio > 1);
        return new FeaAuditCriterion(4, "Напряжения согласованы с K_исп", offenders.Count == 0,
            $"проверено элементов {checkedRows}, σ выше расчётного сопротивления сечения — {over}, из них с K_исп < 1 — {offenders.Count}", offenders);
    }

    private static FeaAuditCriterion LoadPaths(IAnalyticalModel model, LoadLedger ledger)
    {
        int n = model.Elements.Count;
        // Обратный обход от грунта: элемент заземлён, если хотя бы один получатель заземлён и потерь нет.
        var reverse = new List<int>[n];
        var grounded = new bool[n];
        var queue = new Queue<int>();
        foreach (var (from, to) in ledger.Edges)
        {
            if (to == LoadLedger.GroundIndex)
            {
                if (!grounded[from])
                {
                    grounded[from] = true;
                    queue.Enqueue(from);
                }
                continue;
            }
            (reverse[to] ??= []).Add(from);
        }
        while (queue.Count > 0)
        {
            int k = queue.Dequeue();
            if (reverse[k] is not { } sources) continue;
            foreach (int s in sources)
                if (!grounded[s])
                {
                    grounded[s] = true;
                    queue.Enqueue(s);
                }
        }
        var offenders = new List<string>();
        int loaded = 0;
        for (int i = 0; i < n; i++)
        {
            var m = model.Elements[i];
            double load = ledger.In[i] + ledger.Self[i];
            if (load <= 1e-6) continue;
            loaded++;
            if (!grounded[i] || ledger.Lost[i] > 1e-6)
                offenders.Add($"{Key(m)}: {(ledger.Lost[i] > 1e-6 ? $"без опоры {ledger.Lost[i]:0.#} кН" : "нет пути передачи до грунта")}");
        }
        return new FeaAuditCriterion(5, "Нет висящих нагруженных узлов", offenders.Count == 0,
            $"нагруженных элементов {loaded}, замкнуто на грунт {loaded - offenders.Count}", offenders.Take(12).ToList());
    }

    // ───────────────────────────── Монотонность ─────────────────────────────

    private static FeaAuditCriterion Monotonicity(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, StructuralLoadSettings settings,
        StructuralAnalysisResult baseline)
    {
        var offenders = new List<string>();
        var notes = new List<string>();

        // а) Нагрузка: полезная и снег ×1,25 — M, f и σ плит и балок не убывают.
        var heavier = settings.Clone();
        heavier.LiveLoadKPa = settings.LiveLoadKPa * 1.25;
        heavier.SnowKgM2 = Math.Min(600, settings.SnowKgM2 * 1.25);
        var loaded = StructuralAnalysisService.Analyze(elements, groups, heavier);
        int grown = 0, compared = 0;
        foreach (var (id, r0) in baseline.Elements)
        {
            if (loaded.For(id) is not { } r1 || r0.Moment <= 1e-9) continue;
            if (r0.Type is not (ElementTypes.Slab or ElementTypes.Beam or ElementTypes.Roof or ElementTypes.CantileverSlab)) continue;
            compared++;
            if (r1.Moment < r0.Moment - 1e-9 * r0.Moment || r1.DeflectionMm < r0.DeflectionMm - 1e-9 * Math.Max(1, r0.DeflectionMm))
                offenders.Add($"«{id}»: при q ×1,25 M {r0.Moment:0.##} → {r1.Moment:0.##}, f {r0.DeflectionMm:0.##} → {r1.DeflectionMm:0.##}");
            else if (r1.Moment > r0.Moment) grown++;
        }
        notes.Add($"q ×1,25: M и f выросли у {grown} из {compared}");

        // б) Класс бетона на ступень выше — K_исп ж/б элементов не растёт.
        var stronger = settings.Clone();
        int ci = StructuralNorms.ConcreteClasses.ToList().IndexOf(settings.Concrete);
        if (ci >= 0 && ci + 1 < StructuralNorms.ConcreteClasses.Count)
        {
            stronger.Concrete = StructuralNorms.ConcreteClasses[ci + 1];
            var better = StructuralAnalysisService.Analyze(elements, groups, stronger);
            int lowered = 0, checkedC = 0;
            foreach (var e in elements.Where(e => IsConcrete(e)))
            {
                if (baseline.For(e.Id) is not { } r0 || better.For(e.Id) is not { } r1) continue;
                checkedC++;
                if (r1.Utilization > r0.Utilization + 1e-9) offenders.Add($"{e.DisplayTitle}: {settings.Concrete} → {stronger.Concrete} K {r0.Utilization:0.###} → {r1.Utilization:0.###}");
                else if (r1.Utilization < r0.Utilization) lowered++;
            }
            notes.Add($"{StructuralNorms.Title(settings.Concrete)} → {StructuralNorms.Title(stronger.Concrete)}: K снизился у {lowered} из {checkedC}");
        }

        // в) Сечение самой нагруженной балки (или плиты) +20 % по высоте — её K_исп снижается.
        var worst = elements.Where(e => e.Category is BimCategory.Beam or BimCategory.Slab && e.RepeatLevels <= 1 && e.Polygon2D.Count < 3)
            .Select(e => (E: e, R: baseline.For(e.Id))).Where(x => x.R is { Utilization: > 0.05 }).OrderByDescending(x => x.R!.Utilization).Select(x => x.E).FirstOrDefault();
        if (worst is not null)
        {
            var copy = elements.Select(e => e.Clone()).ToList();
            var target = copy.First(e => e.Id == worst.Id);
            target.SetSize(target.Width, target.Height * 1.2, target.Depth);
            target.SetPosition(target.X, target.Y + target.Height / 12, target.Z);
            var deeper = StructuralAnalysisService.Analyze(copy, groups, settings);
            double k0 = baseline.For(worst.Id)!.Utilization, k1 = deeper.For(worst.Id)?.Utilization ?? k0;
            if (!(k1 < k0)) offenders.Add($"{worst.DisplayTitle}: h ×1,2 — K {k0:0.###} → {k1:0.###}");
            notes.Add($"h ×1,2 у «{worst.DisplayTitle}»: K {k0.ToString("0.###", Ru)} → {k1.ToString("0.###", Ru)}");
        }

        // г) Пролёт: эталонная балка на двух колоннах L = 6 м и 7,2 м — M, f, σ растут.
        var span = SpanProbe(settings, 6.0, 7.2);
        if (!(span.M1 > span.M0 && span.F1 > span.F0 && span.S1 > span.S0))
            offenders.Add($"балка L 6,0 → 7,2 м: M {span.M0:0.##} → {span.M1:0.##}, f {span.F0:0.##} → {span.F1:0.##}, σ {span.S0:0.##} → {span.S1:0.##}");
        notes.Add($"L 6,0 → 7,2 м: M {span.M0.ToString("0.#", Ru)} → {span.M1.ToString("0.#", Ru)} кН·м, f {span.F0.ToString("0.#", Ru)} → {span.F1.ToString("0.#", Ru)} мм, " +
                  $"σ {span.S0.ToString("0.##", Ru)} → {span.S1.ToString("0.##", Ru)} МПа");

        return new FeaAuditCriterion(3, "Монотонность отклика", offenders.Count == 0, string.Join("; ", notes), offenders.Take(12).ToList());
    }

    private static bool IsConcrete(ArchitecturalElement e) => (e.Material ?? string.Empty).Contains("concrete", StringComparison.OrdinalIgnoreCase);

    /// <summary>Эталонная ж/б балка 300 × 600 на двух колоннах с фундаментом: M, f (мм) и σ (МПа) при пролётах l0 и l1.</summary>
    public static (double M0, double M1, double F0, double F1, double S0, double S1) SpanProbe(StructuralLoadSettings settings, double l0, double l1)
    {
        (double M, double F, double S) Run(double length)
        {
            ArchitecturalElement Make(string type, string name, double x, double baseY, double z, double w, double h, double d)
            {
                var e = new ArchitecturalElement { Type = type, Name = name, BuildingGroupId = "Аудит", Material = "concrete" };
                e.SetSize(w, h, d);
                e.SetPosition(x, baseY + h / 2, z);
                return e;
            }
            var list = new List<ArchitecturalElement>
            {
                Make(ElementTypes.Foundation, "Фундамент", length / 2, -0.6, 0, length + 1.2, 0.6, 1.6),
                Make(ElementTypes.Column, "Колонна 1", 0, 0, 0, 0.4, 3.0, 0.4),
                Make(ElementTypes.Column, "Колонна 2", length, 0, 0, 0.4, 3.0, 0.4),
                Make(ElementTypes.Beam, "Балка", length / 2, 2.4, 0, length + 0.4, 0.6, 0.3)
            };
            var model = AnalyticalModelBuilder.Build(list, [new BuildingGroup("Аудит")], settings, parallel: false);
            var result = StructuralAnalysisService.Analyze(model);
            var beam = list[3];
            var r = result.For(beam.Id);
            var row = Stresses(model, result).FirstOrDefault(x => x.ElementId == beam.Id);
            return (r?.Moment ?? 0, r?.DeflectionMm ?? 0, row?.SigmaMax ?? 0);
        }
        var a = Run(l0);
        var b = Run(l1);
        return (a.M, b.M, a.F, b.F, a.S, b.S);
    }
}
