using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Analytical;

namespace AIArchitectStudio.Services;

/// <summary>
/// Генератор инженерного обоснования («Паспорт расчёта») несущего элемента по СП 63.13330.2018, СП 20.13330.2016, СП 16.13330.2017
/// и ГОСТ 27751-2014. Строится по запросу (выбранный элемент, окно паспорта) из расчётной схемы <see cref="IAnalyticalModel"/>
/// и результата <see cref="StructuralAnalysisService"/>: формулы проверок повторяются здесь в том же порядке операций, что и в
/// солвере, поэтому K проверок, вычисленный паспортом по числам элемента, совпадает с K расчёта до бита (проверки, зависящие
/// от распределения сил по схеме — прогиб, давление на грунт, — берутся из результата и помечаются). В горячем цикле расчёта
/// ничего не формируется: отчёт стоит один дополнительный расчёт схемы при открытии паспорта.
/// </summary>
public static class CalculationJustificationService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public const string Sp63 = "СП 63.13330.2018", Sp20 = "СП 20.13330.2016", Sp16 = "СП 16.13330.2017", Gost27751 = "ГОСТ 27751-2014", Sp14 = "СП 14.13330.2018";

    /// <summary>Паспорт по снимку сцены: схема строится и считается заново (тот же результат, что и у экспресс-расчёта).</summary>
    public static (ElementCalculationPassport? Passport, StructuralAnalysisResult Result) Build(string elementId, IReadOnlyList<ArchitecturalElement> elements,
        IReadOnlyList<BuildingGroup> groups, StructuralLoadSettings loads)
    {
        var model = AnalyticalModelBuilder.Build(elements, groups, loads);
        var result = StructuralAnalysisService.Analyze(model);
        return (Build(elementId, model, result), result);
    }

    /// <summary>Паспорт элемента по готовой схеме и её результату; null — элемент не несущий (нет результата расчёта).</summary>
    public static ElementCalculationPassport? Build(string elementId, IAnalyticalModel model, StructuralAnalysisResult result)
    {
        if (result.For(elementId) is not { } r) return null;
        var candidates = model.Elements.Where(m => m.Source.Id == elementId).ToList();
        var m = candidates.FirstOrDefault(x => x.Floor == r.Floor) ?? candidates.FirstOrDefault();
        if (m is null) return null;
        var ctx = new Context(model, result, m, r);
        ctx.Run();
        return ctx.ToPassport();
    }

    // ═════════════════════════════ Сборка паспорта ═════════════════════════════

    private sealed class Context(IAnalyticalModel model, StructuralAnalysisResult result, AnalyticalElement m, ElementStructuralResult r)
    {
        private readonly StructuralLoadSettings s = model.Settings;
        private readonly (double Rb, double Rbt, double Eb, double Creep) _concrete = model.Settings.ConcreteProps();
        private readonly (double Rs, double Rsc, double Rsw) _rebar = model.Settings.RebarProps();
        private readonly double _gn = model.Settings.GammaN;

        public readonly List<MaterialProperty> Material = [];
        public readonly List<NormReference> Norms = [];
        public readonly List<LoadContribution> Loads = [];
        public readonly List<string> Boundary = [];
        public readonly List<CalculationStep> Steps = [];
        public readonly List<string> Notes = [];
        private string _materialTitle = string.Empty, _materialSource = string.Empty, _section = string.Empty;
        private bool _certificate;

        // Пересчитанные (или принятые по схеме) K проверок.
        private double kStrength, kStability, kShear, kDeflection, kPunching, kSoil;

        private bool Concrete => m.Material.Kind == AnalyticalMaterialKind.Concrete;
        private bool Steel => m.Material.Kind == AnalyticalMaterialKind.Steel;
        private bool Timber => m.Material.Kind == AnalyticalMaterialKind.Timber;

        public void Run()
        {
            // Без пересчёта проверки берутся из результата (прогиб, давление на грунт, продавливание у повторяемых элементов).
            (kStrength, kStability, kShear, kDeflection, kPunching, kSoil) = (r.KStrength, r.KStability, r.KShear, r.KDeflection, r.KPunching, r.KSoil);
            DescribeNorms();
            DescribeMaterial();
            DescribeBoundary();
            switch (m.Role)
            {
                case AnalyticalRole.Vertical when m.Category == BimCategory.Wall: Wall((AnalyticalPanel)m); break;
                case AnalyticalRole.Vertical when m.Category == BimCategory.Core: Core((AnalyticalMember)m); break;
                case AnalyticalRole.Vertical when m.Category == BimCategory.Diagrid: Diagrid((AnalyticalMember)m); break;
                case AnalyticalRole.Vertical: Column((AnalyticalMember)m); break;
                case AnalyticalRole.Line: Beam((AnalyticalMember)m); break;
                case AnalyticalRole.Slab: Slab((AnalyticalPanel)m); break;
                case AnalyticalRole.Foundation: Foundation((AnalyticalPanel)m); break;
                case AnalyticalRole.Shell: Shell((AnalyticalPanel)m); break;
            }
            Punching();
            Deflection();
            if (_gn != 1)
                Steps.Add(new CalculationStep("Коэффициент надёжности по ответственности", $"{Gost27751}, п. 10.1, табл. 2",
                    "K_I = γn · K (прочность, устойчивость, срез, продавливание, основание)", $"γn = {F(_gn)} (КС-{s.ConsequenceClass})", _gn, string.Empty));
            foreach (var note in r.Notes) Notes.Add(note);
        }

        public ElementCalculationPassport ToPassport()
        {
            double recomputed = Math.Max(Math.Max(Math.Max(kStrength, kStability), Math.Max(kShear, kDeflection)), Math.Max(kPunching, kSoil));
            Steps.Add(new CalculationStep("Коэффициент использования", $"{Sp63}, п. 8.1",
                "K_исп = max(K_прочн, K_уст, K_Q, K_f, K_прод, K_осн)",
                $"max({F(kStrength)}; {F(kStability)}; {F(kShear)}; {F(kDeflection)}; {F(kPunching)}; {F(kSoil)})",
                recomputed, string.Empty, IsCheck: true, Verified: Same(recomputed, r.Utilization), SolverValue: r.Utilization));
            var p = new ElementCalculationPassport
            {
                ElementId = m.Source.Id,
                ElementTitle = m.Source.DisplayTitle,
                CategoryTitle = CategoryTitle(m.Category),
                Category = m.Category,
                Floor = r.Floor,
                SectionText = _section,
                Utilization = r.Utilization,
                RecomputedUtilization = recomputed,
                Governing = r.Governing,
                GammaN = _gn,
                MaterialTitle = _materialTitle,
                MaterialFromCertificate = _certificate,
                MaterialSource = _materialSource
            };
            p.MaterialProperties.AddRange(Material);
            p.Norms.AddRange(Norms);
            p.Loads.AddRange(Loads);
            p.Boundary.AddRange(Boundary);
            p.Steps.AddRange(Steps);
            p.Notes.AddRange(Notes);
            return p;
        }

        // ───────────── Нормативная база ─────────────

        private void DescribeNorms()
        {
            Norms.Add(new NormReference(Sp20, "Нагрузки и воздействия", "п. 7.2, табл. 7.1", "постоянные нагрузки, γf = 1,1 (ж/б, сталь) и 1,3 (изоляция, стяжки)"));
            Norms.Add(new NormReference(Sp20, "Нагрузки и воздействия", "п. 8.2, табл. 8.3", $"полезная нагрузка на перекрытия {F(s.LiveLoadKPa)} кПа, γf = 1,2"));
            Norms.Add(new NormReference(Sp20, "Нагрузки и воздействия", "п. 10.1, 10.12", $"снеговая нагрузка: S0 = {s.SnowKgM2:0} кг/м², нормативное значение 0,7·S0"));
            Norms.Add(new NormReference(Sp20, "Нагрузки и воздействия", "п. 11.1, табл. 11.1", $"ветровая нагрузка: w0 = {s.WindKgM2:0} кг/м², γf = 1,4"));
            if (Concrete)
            {
                Norms.Add(new NormReference(Sp63, "Бетонные и железобетонные конструкции", "п. 6.1, табл. 6.8, 6.11, 6.12", "расчётные сопротивления Rb, Rbt, модуль упругости Eb, ползучесть φb,cr"));
                Norms.Add(new NormReference(Sp63, "Бетонные и железобетонные конструкции", "п. 6.2.8, табл. 6.14", "расчётные сопротивления арматуры Rs, Rsc, Rsw"));
                string clause = m.Role switch
                {
                    AnalyticalRole.Vertical => "п. 8.1.4, 8.1.15, табл. 8.1 — внецентренно сжатые элементы, случайный эксцентриситет, φ",
                    AnalyticalRole.Line => "п. 8.1.8, 8.1.14, 8.1.33 — изгибаемые элементы, поперечная сила",
                    _ => "п. 8.1.8, 8.1.14, 8.1.32 — плиты: изгиб, поперечная сила без поперечной арматуры"
                };
                Norms.Add(new NormReference(Sp63, "Бетонные и железобетонные конструкции", clause.Split(" — ")[0], clause.Split(" — ")[1]));
                if (r.Punching != PunchingStatus.None || m.Role is AnalyticalRole.Slab or AnalyticalRole.Foundation)
                    Norms.Add(new NormReference(Sp63, "Бетонные и железобетонные конструкции", "п. 8.1.46–8.1.52", "расчёт на продавливание по контуру на h0/2 от грани колонны"));
                Norms.Add(new NormReference(Sp63, "Бетонные и железобетонные конструкции", "п. 8.2.15, 8.2.27", "прогибы: жёсткость 0,85·Eb·I / (1 + φb,cr) при длительной нагрузке"));
                Norms.Add(new NormReference(Sp63, "Бетонные и железобетонные конструкции", "п. 10.3.6", "минимальный процент армирования μmin"));
            }
            if (Steel)
                Norms.Add(new NormReference(Sp16, "Стальные конструкции", "п. 7.1.3, 8.2.1, табл. В.5", "центрально- и внецентренно сжатые стержни (φ, тип сечения «b»), изгиб, Ry"));
            if (m.Role is AnalyticalRole.Line or AnalyticalRole.Slab)
                Norms.Add(new NormReference(Sp20, "Нагрузки и воздействия", "прил. Д, табл. Д.1", "предельные прогибы: L/200, консоль 2c/150"));
            if (m.Role == AnalyticalRole.Foundation)
                Norms.Add(new NormReference("СП 22.13330.2016", "Основания зданий и сооружений", "п. 5.6", $"давление под подошвой против расчётного сопротивления грунта R = {s.SoilBearingKPa:0} кПа"));
            if (s.Seismicity > 6)
                Norms.Add(new NormReference(Sp14, "Строительство в сейсмических районах", "п. 5.5, 5.9", $"сейсмичность {s.Seismicity} баллов: A = {F(s.SeismicA)}, K1 = 0,4, Kψ = 1,3"));
            if (_gn != 1)
                Norms.Add(new NormReference(Gost27751, "Надёжность строительных конструкций и оснований", "п. 10.1, табл. 2", $"класс сооружения КС-{s.ConsequenceClass}: γn = {F(_gn)}"));
        }

        // ───────────── Паспорт материала ─────────────

        private void DescribeMaterial()
        {
            var mat = m.Material;
            var (certR, certE, certRho) = MaterialCertificate.Read(m.Instance);
            bool elementCertificate = !double.IsNaN(certR) || !double.IsNaN(certE) || !double.IsNaN(certRho);
            string Src(bool fromCertificate, string table) => fromCertificate ? "пользовательский сертификат материала" : table;
            switch (mat.Kind)
            {
                case AnalyticalMaterialKind.Concrete:
                {
                    string code = StructuralNorms.Title(s.Concrete);
                    var grade = MaterialLibrary.Find(s.Materials, code);
                    bool classCertificate = grade?.IsStructurallyOverridden == true;
                    _certificate = elementCertificate || classCertificate;
                    _materialTitle = $"Тяжёлый бетон класса {code}, арматура {StructuralNorms.Title(s.Rebar)}";
                    var norm = StructuralNorms.Concrete(s.Concrete);
                    _materialSource = elementCertificate ? "Пользовательский сертификат материала (свойства элемента «Материал (сертификат)»)"
                        : classCertificate ? $"Пользовательский сертификат материала: {grade!.Certificate ?? "библиотека материалов проекта"}"
                        : $"{Sp63}, табл. 6.8 (Бетон {code}: Rb = {F(norm.Rb)} МПа, Rbt = {F(norm.Rbt)} МПа), табл. 6.11 (Eb = {norm.Eb.ToString("N0", Ru)} МПа)";
                    string table = classCertificate ? "сертификат проекта" : $"{Sp63}, табл. 6.8";
                    Material.Add(new MaterialProperty("Rb", "расчётное сопротивление бетона сжатию", mat.Rc / 1000, "МПа", Src(!double.IsNaN(certR), table)));
                    Material.Add(new MaterialProperty("Rbt", "расчётное сопротивление бетона растяжению", mat.Rbt / 1000, "МПа", classCertificate ? "сертификат проекта" : $"{Sp63}, табл. 6.8"));
                    Material.Add(new MaterialProperty("Eb", "начальный модуль упругости бетона", mat.E / 1000, "МПа", Src(!double.IsNaN(certE), classCertificate ? "сертификат проекта" : $"{Sp63}, табл. 6.11")));
                    Material.Add(new MaterialProperty("φb,cr", "коэффициент ползучести (влажность 40–75 %)", _concrete.Creep, string.Empty, $"{Sp63}, табл. 6.12"));
                    Material.Add(new MaterialProperty("ρ", "плотность железобетона", mat.Density, "кг/м³", Src(!double.IsNaN(certRho), classCertificate ? "сертификат проекта" : $"{Sp20}, табл. 7.1 (2500 кг/м³)")));
                    var rebarGrade = MaterialLibrary.Find(s.Materials, StructuralNorms.Title(s.Rebar));
                    string rebarSrc = rebarGrade?.IsStructurallyOverridden == true ? "сертификат проекта" : $"{Sp63}, п. 6.2.8, табл. 6.14";
                    Material.Add(new MaterialProperty("Rs", $"арматура {StructuralNorms.Title(s.Rebar)} — растяжение", _rebar.Rs, "МПа", rebarSrc));
                    Material.Add(new MaterialProperty("Rsc", $"арматура {StructuralNorms.Title(s.Rebar)} — сжатие", _rebar.Rsc, "МПа", rebarSrc));
                    Material.Add(new MaterialProperty("Rsw", "поперечная арматура", _rebar.Rsw, "МПа", $"{Sp63}, табл. 6.14"));
                    Material.Add(new MaterialProperty("Es", "модуль упругости арматуры", StructuralNorms.Es, "МПа", $"{Sp63}, п. 6.2.12"));
                    break;
                }
                case AnalyticalMaterialKind.Steel:
                {
                    var grade = MaterialLibrary.Find(s.Materials, MaterialLibrary.SteelC245);
                    bool classCertificate = grade?.IsStructurallyOverridden == true;
                    _certificate = elementCertificate || classCertificate;
                    _materialTitle = "Сталь С245 (ГОСТ 27772-2015)";
                    _materialSource = elementCertificate ? "Пользовательский сертификат материала (свойства элемента)"
                        : classCertificate ? $"Пользовательский сертификат материала: {grade!.Certificate ?? "библиотека материалов проекта"}"
                        : $"{Sp16}, табл. В.5 (С245: Ry = {F(StructuralNorms.SteelRy)} МПа), E = {StructuralNorms.SteelE.ToString("N0", Ru)} МПа";
                    string table = classCertificate ? "сертификат проекта" : $"{Sp16}, табл. В.5";
                    Material.Add(new MaterialProperty("Ry", "расчётное сопротивление по пределу текучести", mat.Rc / 1000, "МПа", Src(!double.IsNaN(certR), table)));
                    Material.Add(new MaterialProperty("E", "модуль упругости", mat.E / 1000, "МПа", Src(!double.IsNaN(certE), classCertificate ? "сертификат проекта" : $"{Sp16}, табл. Г.10")));
                    Material.Add(new MaterialProperty("ρ", "плотность", mat.Density, "кг/м³", Src(!double.IsNaN(certRho), $"{Sp20}, табл. 7.1")));
                    break;
                }
                default:
                {
                    _certificate = elementCertificate;
                    _materialTitle = mat.Kind switch
                    {
                        AnalyticalMaterialKind.Timber => "Древесина (сосна, 2 сорт)",
                        AnalyticalMaterialKind.Masonry => "Каменная кладка",
                        _ => "Стекло"
                    };
                    string table = mat.Kind == AnalyticalMaterialKind.Timber ? "СП 64.13330.2017, табл. 3" : "СП 15.13330.2020, табл. 6.1";
                    _materialSource = elementCertificate ? "Пользовательский сертификат материала (свойства элемента)" : table;
                    Material.Add(new MaterialProperty("R", "расчётное сопротивление сжатию", mat.Rc / 1000, "МПа", Src(!double.IsNaN(certR), table)));
                    Material.Add(new MaterialProperty("E", "модуль упругости", mat.E / 1000, "МПа", Src(!double.IsNaN(certE), table)));
                    Material.Add(new MaterialProperty("ρ", "плотность", mat.Density, "кг/м³", Src(!double.IsNaN(certRho), table)));
                    break;
                }
            }
        }

        // ───────────── Граничные условия ─────────────

        private void DescribeBoundary()
        {
            var nodes = m.Nodes;
            int fixedCount = nodes.Count(n => n.IsFixedSupport);
            if (nodes.Count > 0)
                Boundary.Add($"Узлы расчётной схемы: {nodes.Count} (Id {string.Join(", ", nodes.Take(6).Select(n => n.Id))}{(nodes.Count > 6 ? ", …" : string.Empty)})" +
                             (fixedCount > 0 ? $", жёстких опор: {fixedCount}" : string.Empty));
            IEnumerable<AnalyticalElement> supports = m switch
            {
                AnalyticalMember { Role: AnalyticalRole.Line } bar => bar.Supports,
                AnalyticalPanel { Tributary: { } plan } => plan.Supports,
                _ => (m.Receivers ?? []).Where(x => x is not null).Select(x => x!)
            };
            var names = supports.Select(x => x.Source.DisplayTitle).Distinct().ToList();
            if (names.Count > 0)
                Boundary.Add($"Опоры: {string.Join(", ", names.Take(6))}{(names.Count > 6 ? $" и ещё {names.Count - 6}" : string.Empty)}");
            else if (m.Role is AnalyticalRole.Vertical && m.Base <= AnalyticalModel.GroundLevel)
                Boundary.Add("Опора: основание (низ элемента на уровне земли)");
            else if (m.Role == AnalyticalRole.Foundation)
                Boundary.Add($"Опора: грунт основания, R = {s.SoilBearingKPa:0} кПа");
            if (m.Role == AnalyticalRole.Vertical)
                Boundary.Add($"Отметки: низ {F(m.Base)} м, верх {F(m.Top)} м, высота {F(m.Height)} м");
        }

        // ───────────── Колонна ─────────────

        private void Column(AnalyticalMember bar)
        {
            var sec = bar.Section;
            double height = m.Height, n = r.AxialForce;
            _section = SectionTitle(bar);
            VerticalLoads(n);

            double i = Math.Sqrt(sec.IWeak / Math.Max(1e-9, sec.A));
            double l0 = StructuralAnalysisService.EffectiveLength(m, height);
            double lambda = l0 / Math.Max(1e-4, i);
            Steps.Add(new CalculationStep("Геометрические характеристики сечения", Concrete ? $"{Sp63}, п. 8.1.15" : $"{Sp16}, п. 7.1.3",
                "A, I_min, i = √(I_min / A)", $"A = {Cm2(sec.A)} см², I_y = {Cm4(sec.IStrong)} см⁴, I_z = {Cm4(sec.IWeak)} см⁴, i = {Mm(i)} мм", i * 1000, "мм"));
            string scheme = m.LengthFactor > 0
                ? $"l0 = {F(m.LengthFactor)}·H — закрепление концов задано в режиме «Конструкции»"
                : Concrete ? "l0 = 0,7·H — защемление в перекрытиях" : "l0 = H";
            Steps.Add(new CalculationStep("Гибкость", Concrete ? $"{Sp63}, п. 8.1.17 ({scheme})" : $"{Sp16}, п. 10.3.1 ({scheme})",
                "λ = l0 / i", $"λ = {F(l0)} м / {Mm(i)} мм = {F(lambda)}", lambda, string.Empty, SolverValue: r.Slenderness, Verified: Same(lambda, r.Slenderness)));
            double ecc = Math.Max(Math.Max(height / 600, sec.HWeak / 30), 0.01);
            double latM = r.Moment - n * ecc;
            Steps.Add(new CalculationStep("Расчётный момент", $"{Sp63}, п. 8.1.7 (случайный эксцентриситет)",
                "e_a = max(H/600; h/30; 10 мм), M = N·e_a + M_гор", $"e_a = {Mm(ecc)} мм, M = {F(n)}·{F(ecc)} + {F(Math.Max(0, latM))} = {F(r.Moment)} кН·м", r.Moment, "кН·м"));

            double moment = r.Moment;
            switch (m.Material.Kind)
            {
                case AnalyticalMaterialKind.Concrete:
                {
                    double mu = StructuralAnalysisService.RebarRatio(m, 0.015), a = StructuralAnalysisService.RebarAxisDistance(m, 0.04);
                    bool explicitRebar = m.RebarMu > 0;
                    double phi = StructuralAnalysisService.PhiConcrete(lambda / Math.Sqrt(12));
                    double n0 = m.Material.Rc * sec.A + _rebar.Rsc * 1000 * mu * sec.A;
                    double hs = sec.HStrong;
                    double m0 = _rebar.Rs * 1000 * (mu / 2 * sec.A) * Math.Max(0.05, hs - 2 * a);
                    double muN = n <= n0 ? m0 + 0.5 * n * hs * (1 - n / n0) : 0;
                    double b = sec.A / Math.Max(0.05, hs);
                    double qult = 1.5 * m.Material.Rbt * b * Math.Max(0.05, hs - a);
                    Steps.Add(new CalculationStep("Коэффициент продольного изгиба", $"{Sp63}, табл. 8.1 (длительная нагрузка)",
                        "φ = f(l0/h), l0/h = λ / √12", $"l0/h = {F(lambda / Math.Sqrt(12))} → φ = {F(phi)}", phi, string.Empty, SolverValue: r.Phi, Verified: Same(phi, r.Phi)));
                    Steps.Add(new CalculationStep("Предельная продольная сила", $"{Sp63}, п. 8.1.16",
                        explicitRebar ? $"N_ult = Rb·A + Rsc·As, As = n·π·d²/4 по заданным стержням (μ = {F(mu * 100)} %)" : "N_ult = Rb·A + Rsc·As, As = μ·A (μ = 1,5 %)", $"N_ult = {F(m.Material.Rc / 1000)}·10³·{F(sec.A)} + {F(_rebar.Rsc)}·10³·{F(mu * sec.A)} = {F(n0)} кН", n0, "кН"));
                    double ks = n / Math.Max(1e-6, phi * n0);
                    Check("Устойчивость (внецентренное сжатие)", $"{Sp63}, п. 8.1.4, 8.1.16", "K_уст = N / (φ·N_ult)",
                        $"K = {F(n)} / ({F(phi)}·{F(n0)})", G(ks), r.KStability, ref kStability);
                    Steps.Add(new CalculationStep("Предельный момент при действующей N", $"{Sp63}, п. 8.1.14, 8.1.20",
                        "M0 = Rs·(As/2)·(h − 2a), Mu(N) = M0 + 0,5·N·h·(1 − N/N_ult)", $"M0 = {F(m0)} кН·м, Mu = {F(muN)} кН·м (h = {Mm(hs)} мм, a = {Mm(a)} мм)", muN, "кН·м"));
                    double kst = n > n0 ? n / n0 : moment / Math.Max(1e-6, muN);
                    Check("Прочность нормального сечения", $"{Sp63}, п. 8.1.14", n > n0 ? "K = N / N_ult" : "K = M / Mu(N)",
                        n > n0 ? $"K = {F(n)} / {F(n0)}" : $"K = {F(moment)} / {F(muN)}", G(kst), r.KStrength, ref kStrength);
                    double kq = r.Shear / Math.Max(1e-6, qult);
                    Check("Поперечная сила от горизонтальных нагрузок", $"{Sp63}, п. 8.1.32", "Q_ult = 1,5·Rbt·b·h0, K = Q / Q_ult",
                        $"Q = {F(r.Shear)} кН, Q_ult = 1,5·{F(m.Material.Rbt / 1000)}·10³·{F(b)}·{F(Math.Max(0.05, hs - a))} = {F(qult)} кН", G(kq), r.KShear, ref kShear);
                    MuMinStep(mu, "колонна");
                    break;
                }
                case AnalyticalMaterialKind.Steel:
                {
                    double lambdaBar = lambda * Math.Sqrt(m.Material.Rc / m.Material.E);
                    double phi = StructuralAnalysisService.PhiSteel(lambdaBar);
                    double n0 = m.Material.Rc * sec.A;
                    double w = sec.IWeak / Math.Max(1e-6, sec.HWeak / 2);
                    Steps.Add(new CalculationStep("Коэффициент устойчивости", $"{Sp16}, п. 7.1.3, формула (8), тип сечения «b»",
                        "λ̄ = λ·√(Ry/E), φ = 0,5·(δ − √(δ² − 39,48·λ̄²)) / λ̄²", $"λ̄ = {F(lambdaBar)} → φ = {F(phi)}", phi, string.Empty, SolverValue: r.Phi, Verified: Same(phi, r.Phi)));
                    double ks = n / Math.Max(1e-6, phi * n0);
                    Check("Устойчивость", $"{Sp16}, п. 7.1.3, формула (7)", "K = N / (φ·A·Ry)", $"K = {F(n)} / ({F(phi)}·{F(n0)})", G(ks), r.KStability, ref kStability);
                    double kst = n / Math.Max(1e-6, n0) + moment / Math.Max(1e-6, m.Material.Rc * w);
                    Check("Прочность при сжатии с изгибом", $"{Sp16}, п. 9.1.1", "K = N/(A·Ry) + M/(W·Ry)",
                        $"K = {F(n)}/{F(n0)} + {F(moment)}/{F(m.Material.Rc * w)}", G(kst), r.KStrength, ref kStrength);
                    double kq = r.Shear / Math.Max(1e-6, 0.58 * m.Material.Rc * sec.AShear);
                    Check("Срез", $"{Sp16}, п. 8.2.1 (Rs = 0,58·Ry)", "K = Q / (0,58·Ry·A_s)", $"K = {F(r.Shear)} / {F(0.58 * m.Material.Rc * sec.AShear)}", G(kq), r.KShear, ref kShear);
                    break;
                }
                default:
                {
                    double phi = Timber
                        ? lambda <= 70 ? 1 - 0.8 * Math.Pow(lambda / 100, 2) : 3000 / (lambda * lambda)
                        : StructuralAnalysisService.PhiConcrete(lambda / Math.Sqrt(12));
                    double rc = Math.Max(100, m.Material.Rc);
                    double w = sec.IWeak / Math.Max(1e-6, sec.HWeak / 2);
                    double rb = Timber ? m.Material.Rbend : 0.1 * rc;
                    string sp = Timber ? "СП 64.13330.2017, п. 7.3" : "СП 15.13330.2020, п. 7.1";
                    Steps.Add(new CalculationStep("Коэффициент продольного изгиба", sp, Timber ? "φ = 1 − 0,8·(λ/100)² (λ ≤ 70), иначе 3000/λ²" : "φ = f(l0/h)",
                        $"φ = {F(phi)}", phi, string.Empty, SolverValue: r.Phi, Verified: Same(phi, r.Phi)));
                    Check("Устойчивость", sp, "K = N / (φ·R·A)", $"K = {F(n)} / ({F(phi)}·{F(rc * sec.A)})", G(n / Math.Max(1e-6, phi * rc * sec.A)), r.KStability, ref kStability);
                    Check("Прочность при сжатии с изгибом", sp, "K = N/(R·A) + M/(R_и·W)", $"K = {F(n)}/{F(rc * sec.A)} + {F(moment)}/{F(rb * w)}",
                        G(n / Math.Max(1e-6, rc * sec.A) + moment / Math.Max(1e-6, rb * w)), r.KStrength, ref kStrength);
                    Check("Срез", sp, "K = Q / (R_ск·A)", $"K = {F(r.Shear)} / {F((Timber ? 1.6e3 : 120) * sec.A)}",
                        G(r.Shear / Math.Max(1e-6, (Timber ? 1.6e3 : 120) * sec.A)), r.KShear, ref kShear);
                    break;
                }
            }
        }

        // ───────────── Стена, ядро, диагрид ─────────────

        private void Wall(AnalyticalPanel wall)
        {
            double n = r.AxialForce;
            double t = Math.Max(0.05, wall.Thickness), length = Math.Max(0.1, wall.Width), height = m.Height;
            _section = $"стена t = {Mm(t)} мм, L = {F(length)} м, H = {F(height)} м";
            VerticalLoads(n);
            double area = t * length;
            double l0 = 0.9 * height;
            double lambdaH = l0 / t;
            double slenderness = l0 / (t / Math.Sqrt(12));
            double phi = Timber ? Math.Max(0.2, 1 - 0.8 * Math.Pow(slenderness / 100, 2)) : StructuralAnalysisService.PhiConcrete(lambdaH);
            Steps.Add(new CalculationStep("Гибкость из плоскости", $"{Sp63}, п. 8.1.17 (l0 = 0,9·H)", "λ = l0 / i, i = t/√12; φ = f(l0/t)",
                $"l0 = {F(l0)} м, λ = {F(slenderness)}, l0/t = {F(lambdaH)} → φ = {F(phi)}", phi, string.Empty, SolverValue: r.Phi, Verified: Same(phi, r.Phi)));
            double eOut = Math.Max(Math.Max(t / 30, height / 600), 0.01);
            double mOut = n * eOut;
            double n0, m0In, m0Out, qult;
            if (Concrete)
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
                qult = ((Timber ? 1.6e3 : 120) + 0.8 * 0.7 * sigma) * area;
            }
            Steps.Add(new CalculationStep("Предельная продольная сила", $"{Sp63}, п. 8.1.16", Concrete ? "N_ult = Rb·A + Rsc·As (μ = 0,5 %)" : "N_ult = R·A",
                $"A = {F(area)} м², N_ult = {F(n0)} кН", n0, "кН"));
            double kStabilityRaw = n / Math.Max(1e-6, phi * n0);
            Check("Устойчивость", $"{Sp63}, п. 8.1.4", "K = N / (φ·N_ult)", $"K = {F(n)} / ({F(phi)}·{F(n0)})", G(kStabilityRaw), r.KStability, ref kStability);
            double nn = Math.Min(n, n0);
            double muIn = m0In + 0.5 * nn * length * (1 - nn / n0);
            double muOut = m0Out + 0.5 * nn * t * (1 - nn / n0);
            if (r.Moment > mOut)
            {
                double latM = r.Moment;
                double kIn = latM > 1e-6 ? latM / Math.Max(1e-6, muIn) : 0;
                double kOut = mOut / Math.Max(1e-6, muOut + (Concrete ? 0 : 0.5 * nn * t));
                double kst = n > n0 ? n / n0 : Math.Max(kIn, Math.Min(kOut, kStabilityRaw));
                Check("Прочность в плоскости и из плоскости", $"{Sp63}, п. 8.1.14", "K = max(M_гор / Mu,пл; min(N·e_a / Mu,из; K_уст))",
                    $"M_гор = {F(latM)} кН·м, Mu,пл = {F(muIn)} кН·м, N·e_a = {F(mOut)} кН·м, Mu,из = {F(muOut)} кН·м", G(kst), r.KStrength, ref kStrength);
            }
            else
                Taken("Прочность в плоскости и из плоскости", $"{Sp63}, п. 8.1.14", "K = max(M_гор / Mu,пл; min(N·e_a / Mu,из; K_уст))",
                    $"N·e_a = {F(mOut)} кН·м, Mu,пл = {F(muIn)} кН·м, Mu,из = {F(muOut)} кН·м; доля горизонтального момента — по жёсткостям EI схемы", r.KStrength);
            Check("Поперечная сила", $"{Sp63}, п. 8.1.32", Concrete ? "Q_ult = 0,75·Rbt·t·L·0,8" : "Q_ult = (R_ср + 0,8·0,7·σ)·A",
                $"Q = {F(r.Shear)} кН, Q_ult = {F(qult)} кН", G(r.Shear / Math.Max(1e-6, qult)), r.KShear, ref kShear);
        }

        private void Core(AnalyticalMember bar)
        {
            double n = r.AxialForce;
            double t = m.WallT, area = bar.Section.A, inertia = bar.Section.IStrong;
            _section = $"ядро {F(bar.SectionB)} × {F(bar.SectionH)} м, стенки t = {Mm(t)} мм";
            VerticalLoads(n);
            double i = Math.Sqrt(inertia / Math.Max(1e-6, area));
            double l0 = 2.0 * m.Height;
            double slenderness = l0 / Math.Max(1e-3, i);
            double phi = StructuralAnalysisService.PhiConcrete(slenderness / Math.Sqrt(12));
            Steps.Add(new CalculationStep("Гибкость консольной коробки", $"{Sp63}, п. 8.1.17 (l0 = 2·H)", "λ = l0 / i, φ = f(λ/√12)",
                $"i = {Mm(i)} мм, λ = {F(slenderness)} → φ = {F(phi)}", phi, string.Empty, SolverValue: r.Phi, Verified: Same(phi, r.Phi)));
            const double mu = 0.005;
            double side = Math.Min(bar.SectionB, bar.SectionH);
            double n0 = m.Material.Rc * area + _rebar.Rsc * 1000 * mu * area;
            double m0 = _rebar.Rs * 1000 * (mu / 2 * area) * Math.Max(0.1, side - 2 * t);
            double nn = Math.Min(n, n0);
            double mun = m0 + 0.5 * nn * side * (1 - nn / n0);
            double moment = r.Moment;
            Check("Устойчивость", $"{Sp63}, п. 8.1.4", "K = N / (φ·N_ult)", $"N_ult = {F(n0)} кН, K = {F(n)} / ({F(phi)}·{F(n0)})", G(n / Math.Max(1e-6, phi * n0)), r.KStability, ref kStability);
            Check("Прочность при сжатии с изгибом", $"{Sp63}, п. 8.1.14", n > n0 ? "K = N / N_ult" : "K = M / Mu(N)",
                $"M = {F(moment)} кН·м, Mu(N) = {F(mun)} кН·м", G(n > n0 ? n / n0 : moment / Math.Max(1e-6, mun)), r.KStrength, ref kStrength);
            Check("Поперечная сила", $"{Sp63}, п. 8.1.32", "Q_ult = 0,75·Rbt·2t·b", $"Q = {F(r.Shear)} кН, Q_ult = {F(0.75 * m.Material.Rbt * 2 * t * side)} кН",
                G(r.Shear / Math.Max(1e-6, 0.75 * m.Material.Rbt * 2 * t * side)), r.KShear, ref kShear);
        }

        private void Diagrid(AnalyticalMember bar)
        {
            double n = r.AxialForce;
            var pipe = bar.Section;
            var (len, cos) = bar.InclinedBar();
            _section = $"диагрид: {bar.BarCount} труб Ø{Mm(bar.SectionB)} мм на кольце R = {F(m.Radius)} м";
            VerticalLoads(n);
            double latM = r.Moment;
            double force = (n / bar.BarCount + 2 * latM / (bar.BarCount * Math.Max(0.5, m.Radius))) / Math.Max(0.2, cos);
            double i = Math.Sqrt(pipe.IWeak / Math.Max(1e-9, pipe.A));
            double lambda = len / Math.Max(1e-4, i);
            double phi = StructuralAnalysisService.PhiSteel(lambda * Math.Sqrt(m.Material.Rc / m.Material.E));
            Steps.Add(new CalculationStep("Усилие в наклонном стержне", $"{Sp16}, п. 7.1.3", "N_ст = (N/n + 2M/(n·R)) / cos θ",
                $"N_ст = ({F(n)}/{bar.BarCount} + 2·{F(latM)}/({bar.BarCount}·{F(m.Radius)})) / {F(cos)} = {F(force)} кН", force, "кН"));
            Check("Прочность стержня", $"{Sp16}, п. 7.1.1", "K = N_ст / (A·Ry)", $"K = {F(force)} / {F(m.Material.Rc * pipe.A)}", G(force / Math.Max(1e-6, m.Material.Rc * pipe.A)), r.KStrength, ref kStrength);
            Check("Устойчивость стержня", $"{Sp16}, п. 7.1.3", "K = N_ст / (φ·A·Ry)", $"λ = {F(lambda)}, φ = {F(phi)}", G(force / Math.Max(1e-6, phi * m.Material.Rc * pipe.A)), r.KStability, ref kStability);
            Check("Срез", $"{Sp16}, п. 8.2.1", "K = Q / (n·0,58·Ry·A_s)", $"Q = {F(r.Shear)} кН", G(r.Shear / Math.Max(1e-6, bar.BarCount * 0.58 * m.Material.Rc * pipe.AShear)), r.KShear, ref kShear);
        }

        // ───────────── Балка ─────────────

        private void Beam(AnalyticalMember bar)
        {
            double b = Math.Max(0.05, bar.SectionB), h = Math.Max(0.05, bar.SectionH);
            _section = SectionTitle(bar);
            double length = Math.Max(0.05, bar.AxisLength);
            Loads.Add(new LoadContribution("Собственный вес балки", m.SelfD / length, "кН/м", $"A·ρ·g·γf, γf = {F(StructuralNorms.GammaDead)}", $"{Sp20}, табл. 7.1"));
            Loads.Add(new LoadContribution("Распределённая нагрузка от плит и собственного веса", r.DesignLoad, "кН/м", "грузовые площади плит по схеме", $"{Sp20}, п. 8.2"));
            if (bar.Spans.Count > 0)
                Loads.Add(new LoadContribution("Пролётов", bar.Spans.Count, string.Empty, $"расчётный пролёт L = {F(r.Span)} м{(r.IsCantilever ? " (консоль)" : string.Empty)}", string.Empty));
            Steps.Add(new CalculationStep("Расчётный изгибающий момент", $"{Sp63}, п. 8.1.8", "M = q·L²/8 (q·L²/12 при монолитных узлах) + Σ P·a·b/L",
                $"L = {F(r.Span)} м, M = {F(r.Moment)} кН·м, Q = {F(r.Shear)} кН", r.Moment, "кН·м"));
            if (bar.Supports.Count == 0)
            {
                Notes.Add("Балка без опор: проверки прочности не выполняются, нагрузка передаётся вниз как есть.");
                return;
            }
            switch (m.Material.Kind)
            {
                case AnalyticalMaterialKind.Concrete:
                {
                    if (m.Shape == AnalyticalShape.Polyline) b = 0.6;
                    double a = StructuralAnalysisService.RebarAxisDistance(m, 0.05), mu = StructuralAnalysisService.RebarRatio(m, 0.012);
                    double mult = StructuralAnalysisService.RcUltimateMoment(m.Material.Rc, _rebar.Rs, b, h, a, mu);
                    double h0 = h - a;
                    double qsw = StructuralAnalysisService.BeamStirrupQsw(m, b, _rebar.Rsw);
                    double qult = 0.75 * m.Material.Rbt * b * h0 + 1.5 * qsw * h0;
                    double x = Math.Min(_rebar.Rs * 1000 * (mu * b * Math.Max(0.02, h - a)) / (m.Material.Rc * b), 0.5 * Math.Max(0.02, h - a));
                    Steps.Add(new CalculationStep("Предельный момент сечения", $"{Sp63}, п. 8.1.8, формулы (8.1), (8.3)",
                        m.RebarMu > 0 ? $"x = Rs·As / (Rb·b) ≤ 0,5·h0, M_ult = Rb·b·x·(h0 − x/2), As = n·π·d²/4 по заданным стержням (μ = {F(mu * 100)} %)"
                            : "x = Rs·As / (Rb·b) ≤ 0,5·h0, M_ult = Rb·b·x·(h0 − x/2), As = μ·b·h0 (μ = 1,2 %)",
                        $"b = {Mm(b)} мм, h0 = {Mm(h0)} мм (a = {Mm(a)} мм), x = {Mm(x)} мм, M_ult = {F(mult)} кН·м", mult, "кН·м"));
                    Check("Прочность по нормальному сечению", $"{Sp63}, п. 8.1.8", "K = M / M_ult", $"K = {F(r.Moment)} / {F(mult)}", G(mult > 0 ? r.Moment / mult : 0), r.KStrength, ref kStrength);
                    Check("Прочность по наклонному сечению", $"{Sp63}, п. 8.1.33, 8.1.34",
                        m.StirrupQsw > 0 ? "Q_ult = 0,75·Rbt·b·h0 + 1,5·qsw·h0, qsw = Rsw·Asw/s (Ø8 А240, 2 ветви, заданный шаг)" : "Q_ult = 0,75·Rbt·b·h0 + 1,5·qsw·h0, qsw = Rsw·Asw/s (μsw = 0,25 %)",
                        $"Q = {F(r.Shear)} кН, Q_ult = {F(qult)} кН", G(qult > 0 ? r.Shear / qult : 0), r.KShear, ref kShear);
                    MuMinStep(mu, "балка");
                    break;
                }
                case AnalyticalMaterialKind.Steel when m.Category == BimCategory.Truss:
                {
                    double chord = Math.Min(0.25, h * 0.15);
                    double force = r.Moment / Math.Max(0.1, h - chord);
                    double panel = Math.Max(0.5, h);
                    double i = Math.Sqrt(Math.Pow(Math.Min(b, chord), 2) / 12);
                    double phi = StructuralAnalysisService.PhiSteel(panel / Math.Max(1e-3, i) * Math.Sqrt(m.Material.Rc / m.Material.E));
                    Steps.Add(new CalculationStep("Усилие в поясе фермы", $"{Sp16}, п. 15.1", "N_п = M / (h − h_п)", $"N_п = {F(r.Moment)} / {F(Math.Max(0.1, h - chord))} = {F(force)} кН", force, "кН"));
                    Check("Прочность пояса", $"{Sp16}, п. 7.1.1", "K = N_п / (A_п·Ry)", $"A_п = {Cm2(bar.ChordArea)} см²", G(force / Math.Max(1e-6, m.Material.Rc * bar.ChordArea)), r.KStrength, ref kStrength);
                    Check("Устойчивость пояса", $"{Sp16}, п. 7.1.3", "K = N_п / (φ·A_п·Ry)", $"φ = {F(phi)}", G(force / Math.Max(1e-6, phi * m.Material.Rc * bar.ChordArea)), r.KStability, ref kStability);
                    kShear = r.KShear;
                    break;
                }
                default:
                {
                    var sec = bar.Section;
                    double w = sec.IStrong / Math.Max(1e-6, sec.HStrong / 2);
                    double rb = Timber ? m.Material.Rbend : m.Material.Rc;
                    double rs = Timber ? 1.6e3 : 0.58 * m.Material.Rc;
                    string sp = Timber ? "СП 64.13330.2017, п. 7.9" : $"{Sp16}, п. 8.2.1";
                    Check("Прочность при изгибе", sp, "K = M / (W·R)", $"W = {Cm3(w)} см³, K = {F(r.Moment)} / {F(rb * w)}", G(r.Moment / Math.Max(1e-6, rb * w)), r.KStrength, ref kStrength);
                    Check("Прочность на срез", sp, "K = Q / (R_s·A_s)", $"K = {F(r.Shear)} / {F(rs * sec.AShear)}", G(r.Shear / Math.Max(1e-6, rs * sec.AShear)), r.KShear, ref kShear);
                    break;
                }
            }
        }

        // ───────────── Плита и фундамент ─────────────

        private void Slab(AnalyticalPanel slab)
        {
            double h = Math.Max(0.05, slab.Thickness);
            _section = $"плита h = {Mm(h)} мм, A = {F(m.NetArea)} м²";
            SlabLoads(slab);
            var plan = slab.Tributary;
            if (plan is null || plan.Supports.Count == 0)
            {
                Taken("Плита без опор (по грунту)", $"{Sp63}, п. 8.1.8", "нагрузка передаётся на нижележащие конструкции", "конструктивная проверка", r.KStrength);
                kShear = r.KShear;
                return;
            }
            bool mono = Concrete && plan.Supports.All(sp => sp.Material.Kind == AnalyticalMaterialKind.Concrete);
            double kM = mono ? 12 : 8;
            double qd = slab.AreaLoadD;
            double uniform = plan.SpanFactor * qd * plan.Span * plan.Span / kM;
            double cant = qd * plan.Cantilever * plan.Cantilever / 2;
            Steps.Add(new CalculationStep("Расчётный пролёт и момент на 1 м ширины", $"{Sp63}, п. 8.1.8; работа плиты {(plan.SpanFactor < 1 ? "в двух направлениях" : "по балочной схеме")}",
                $"M = α·q·L²/{kM:0} + P/4 (пролёт), M_к = q·c²/2 (консоль)",
                $"q = {F(qd)} кПа, L = {F(plan.Span)} м, α = {F(plan.SpanFactor)}, c = {F(plan.Cantilever)} м → α·q·L²/{kM:0} = {F(uniform)}, q·c²/2 = {F(cant)}; " +
                $"M = {F(r.Moment)} кН·м/м{(r.Moment > Math.Max(uniform, cant) + 1e-9 ? " (с сосредоточенными силами P)" : string.Empty)}", r.Moment, "кН·м/м"));
            SlabCapacity(h, StructuralAnalysisService.RebarAxisDistance(m, 0.03));
        }

        private void Foundation(AnalyticalPanel f)
        {
            double h = Math.Max(0.05, f.Thickness);
            _section = $"фундаментная плита h = {Mm(h)} мм, A = {F(m.NetArea)} м²";
            Loads.Add(new LoadContribution("Нагрузка от вышележащих конструкций", r.AxialForce, "кН", "сумма опорных реакций колонн и стен", $"{Sp20}, п. 6.1"));
            Loads.Add(new LoadContribution("Собственный вес плиты", f.AreaSelfD, "кПа", $"h·ρ·g·γf, γf = {F(StructuralNorms.GammaDead)}", $"{Sp20}, табл. 7.1"));
            Loads.Add(new LoadContribution("Расчётное давление под подошвой", r.DesignLoad, "кПа", $"p = ΣN / A + g = {F(r.AxialForce)} / {F(m.NetArea)} + {F(f.AreaSelfD)}", "СП 22.13330.2016, п. 5.6"));
            Steps.Add(new CalculationStep("Момент от отпора грунта", $"{Sp63}, п. 8.1.8", "перевёрнутая плита на опорах-колоннах: M = q·L²/8, q = p − g",
                $"q = {F(Math.Max(0, r.DesignLoad - f.AreaSelfD))} кПа, L = {F(r.Span)} м, M = {F(r.Moment)} кН·м/м", r.Moment, "кН·м/м"));
            if (r.Moment == 0)
            {
                Taken("Прочность плиты", $"{Sp63}, п. 8.1.8", "опор над плитой нет — конструктивная проверка", string.Empty, r.KStrength);
                kShear = r.KShear;
            }
            else SlabCapacity(h, StructuralAnalysisService.RebarAxisDistance(m, 0.07));
            double pn = s.SoilBearingKPa > 0 ? r.KSoil / _gn * Math.Max(1, s.SoilBearingKPa) : 0;
            Taken("Давление на грунт основания", "СП 22.13330.2016, п. 5.6.7", "K = p_n / R", $"p_n = {F(pn)} кПа (нормативное), R = {F(s.SoilBearingKPa)} кПа", r.KSoil);
        }

        /// <summary>Проверки сечения плиты 1 м ширины: изгиб и поперечная сила (как FinishSlabChecks солвера).</summary>
        private void SlabCapacity(double h, double a)
        {
            double h0 = Math.Max(0.03, h - a);
            if (Concrete)
            {
                double mu = StructuralAnalysisService.RebarRatio(m, 0.004);
                double mult = StructuralAnalysisService.RcUltimateMoment(m.Material.Rc, _rebar.Rs, 1, h, a, mu);
                double qult = 0.5 * m.Material.Rbt * h0;
                Steps.Add(new CalculationStep("Предельный момент полосы 1 м", $"{Sp63}, п. 8.1.8",
                    m.RebarMu > 0 ? $"M_ult = Rb·b·x·(h0 − x/2), As = n·π·d²/4 на 1 м по заданной сетке (μ = {F(mu * 100)} %)" : "M_ult = Rb·b·x·(h0 − x/2), As = μ·b·h0 (μ = 0,4 %)",
                    $"h0 = {Mm(h0)} мм (a = {Mm(a)} мм), M_ult = {F(mult)} кН·м/м", mult, "кН·м/м"));
                Check("Прочность по нормальному сечению", $"{Sp63}, п. 8.1.8", "K = M / M_ult", $"K = {F(r.Moment)} / {F(mult)}", G(mult > 0 ? r.Moment / mult : 0), r.KStrength, ref kStrength);
                Check("Поперечная сила без поперечной арматуры", $"{Sp63}, п. 8.1.32", "Q_ult = 0,5·Rbt·b·h0", $"Q = {F(r.Shear)} кН/м, Q_ult = {F(qult)} кН/м",
                    G(qult > 0 ? r.Shear / qult : 0), r.KShear, ref kShear);
                MuMinStep(mu, "плита");
            }
            else
            {
                double w = h * h / 6;
                double rb = Steel ? m.Material.Rc : Math.Max(1e3, m.Material.Rbend);
                Check("Прочность при изгибе", Steel ? $"{Sp16}, п. 8.2.1" : "СП 64.13330.2017, п. 7.9", "K = M / (W·R)", $"W = {F(w)} м³/м", G(r.Moment / Math.Max(1e-6, rb * w)), r.KStrength, ref kStrength);
                Check("Срез", string.Empty, "K = Q / (0,58·R·h)", string.Empty, G(r.Shear / Math.Max(1e-6, 0.58 * rb * h)), r.KShear, ref kShear);
            }
        }

        private void Shell(AnalyticalPanel shell)
        {
            _section = $"оболочка t = {Mm(m.ThickStruct)} мм, план {F(shell.Width)} × {F(shell.Depth)} м";
            Loads.Add(new LoadContribution("Собственный вес и снег (μ ≈ 0,6)", r.AxialForce, "кН", "передаётся по контуру опирания", $"{Sp20}, прил. Б"));
            double nPhi = r.Shear;
            Check("Прочность меридиональных сечений", $"{Sp63}, п. 8.1.4", "σ = N_φ / t, K = N_φ / (t·Rb)", $"N_φ = {F(nPhi)} кН/м",
                G(nPhi / Math.Max(1e-6, m.ThickStruct * Math.Max(100, m.Material.Rc))), r.KStrength, ref kStrength);
            double radius = Math.Max(1, (shell.Width + shell.Depth) / 4 + shell.ModelHeight * shell.ModelHeight / Math.Max(1, shell.Width));
            double qcr = 0.2 * m.Material.E * 0.85 / (1 + _concrete.Creep) * Math.Pow(m.ThickStruct / radius, 2);
            Check("Местная устойчивость оболочки", "Руководство по проектированию оболочек, п. 4.12", "q_cr = 0,2·E·(t/R)², K = q / q_cr",
                $"R = {F(radius)} м, q_cr = {F(qcr)} кПа", G(r.DesignLoad / Math.Max(1e-6, qcr)), r.KStability, ref kStability);
        }

        // ───────────── Продавливание и прогиб ─────────────

        private void Punching()
        {
            if (r.Punching == PunchingStatus.None) return;
            var nodes = result.PunchingNodes.Where(n => n.ColumnId == m.Source.Id || n.SlabId == m.Source.Id).ToList();
            if (nodes.Count == 0) return;
            var worst = nodes.MaxBy(n => n.Ratio)!;
            bool steel = worst.SteelCapacity > 0;
            string formula = steel ? "F_ult = F_b,ult + F_sw,ult, F_sw,ult = 0,8·q_sw·u ≤ F_b,ult, K = F / F_ult" : "F_b,ult = Rbt·u·h0, K = F / F_b,ult";
            string subst = $"узел «{worst.ColumnId}»: F = {F(worst.Force)} кН, u = {F(worst.Perimeter)} м, h0 = {Mm(worst.H0)} мм, " +
                           (steel ? $"F_b,ult = {F(worst.Capacity - worst.SteelCapacity)} кН, F_sw,ult = {F(worst.SteelCapacity)} кН (каркасы Кр, п. 8.1.48)" : $"F_b,ult = {F(worst.Capacity)} кН") +
                           $" (узлов {nodes.Count})";
            // У поэтажно повторяемого элемента узлы всех этажей несут один Id — K узлов не пересчитывается по одному этажу.
            if (m.Source.RepeatLevels > 1)
            {
                Taken("Продавливание", $"{Sp63}, п. 8.1.46–8.1.52", formula, subst, r.KPunching);
                return;
            }
            double k = 0;
            foreach (var n in nodes) k = Math.Max(k, n.Ratio);
            Check("Продавливание", $"{Sp63}, п. 8.1.46–8.1.52", formula, subst + ". " + worst.Recommendation, G(k), r.KPunching, ref kPunching);
        }

        private void Deflection()
        {
            if (r.DeflectionLimitMm <= 0)
            {
                kDeflection = r.KDeflection;
                return;
            }
            Taken("Прогиб (II группа предельных состояний)", $"{Sp63}, п. 8.2.15, 8.2.27; {Sp20}, табл. Д.1",
                m.Role == AnalyticalRole.Line ? "f = 5·q_n·L⁴ / (384·B) (2/384 при защемлении), B = 0,85·Eb·I / (1 + φb,cr)" : "f = α·5·q_n·L⁴ / (384·B), B = 0,85·Eb·h³/12 / (1 + φb,cr)",
                $"f = {F(r.DeflectionMm)} мм, f_ult = {F(r.DeflectionLimitMm)} мм{(r.IsCantilever ? " (консоль)" : string.Empty)}", r.KDeflection);
        }

        // ───────────── Нагрузки ─────────────

        /// <summary>Грузовая цепочка стойки: собственный вес, плиты по грузовым площадям (реакции схемы), стойки выше, остаток — этажи выше.</summary>
        private void VerticalLoads(double n)
        {
            Loads.Add(new LoadContribution("Собственный вес элемента", m.SelfD, "кН", $"G = V·ρ·g·γf, ρ = {F(m.Material.Density)} кг/м³, γf = {F(StructuralNorms.GammaDead)}", $"{Sp20}, п. 7.2, табл. 7.1"));
            double fromSlabs = 0;
            foreach (var panel in model.Panels)
            {
                if (panel.Tributary is not { } plan) continue;
                int idx = plan.Supports.IndexOf(m);
                if (idx < 0 || idx >= plan.ReactD.Length || plan.ReactD[idx] <= 0) continue;
                double react = plan.ReactD[idx];
                fromSlabs += react;
                double area = panel.AreaLoadD > 0 ? react / panel.AreaLoadD : 0;
                Loads.Add(new LoadContribution($"Плита «{panel.Source.DisplayTitle}»", react, "кН",
                    $"A_груз = {F(area)} м² × q = {F(panel.AreaLoadD)} кПа ({(panel.Exposed ? "покрытие: снег" : "перекрытие: полезная")} + собственный вес)",
                    panel.Exposed ? $"{Sp20}, п. 10.1" : $"{Sp20}, п. 8.2, табл. 8.3"));
            }
            var above = model.Elements.Where(u => u.Role == AnalyticalRole.Vertical && u != m && u.Receivers is { } rec && rec.Contains(m)).ToList();
            foreach (var u in above.Take(4))
                Loads.Add(new LoadContribution($"{CategoryTitle(u.Category)} выше «{u.Source.DisplayTitle}»", result.For(u.Source.Id)?.AxialForce ?? 0, "кН",
                    "продольная сила верхнего этажа (с собственным весом)", string.Empty));
            double rest = n - m.SelfD - fromSlabs;
            if (rest > 0.05)
                Loads.Add(new LoadContribution("Вышележащие этажи и конструкции", rest, "кН", "N сверху по цепочке: стойки, балки и сосредоточенные силы плит", $"{Sp20}, п. 6.1"));
            Loads.Add(new LoadContribution("Расчётная продольная сила N", n, "кН", "сумма составляющих", string.Empty));
            if (r.Shear > 0.005)
                Loads.Add(new LoadContribution("Горизонтальная сила (ветер / сейсмика по EI)", r.Shear, "кН", "доля сдвига этажа по изгибной жёсткости стойки",
                    s.Seismicity > 6 ? $"{Sp14}, п. 5.5; {Sp20}, п. 11.1" : $"{Sp20}, п. 11.1"));
        }

        private void SlabLoads(AnalyticalPanel slab)
        {
            var e = slab.Instance;
            string selfDetail = e.Layers.Count > 0
                ? $"пирог из {e.Layers.Count} слоёв, γf = 1,1 (несущий) / 1,3 (прочие)"
                : $"h·ρ·g·γf = {F(Math.Max(0.01, e.Height))}·{F(slab.Material.Density)}·9,81·10⁻³·{F(StructuralNorms.GammaDead)}";
            Loads.Add(new LoadContribution("Собственный вес", slab.AreaSelfD, "кПа", selfDetail, $"{Sp20}, табл. 7.1"));
            double live = slab.AreaLoadD - slab.AreaSelfD;
            Loads.Add(slab.Exposed
                ? new LoadContribution("Снеговая нагрузка", live, "кПа", $"S = S0·μ, S0 = {F(s.SnowKPa)} кПа", $"{Sp20}, п. 10.1, 10.12")
                : new LoadContribution("Полезная нагрузка", live, "кПа", $"{F(s.LiveLoadKPa)} кПа × γf = {F(StructuralNorms.GammaLive)}", $"{Sp20}, п. 8.2, табл. 8.3"));
            Loads.Add(new LoadContribution("Расчётная нагрузка q", slab.AreaLoadD, "кПа", $"нормативная q_n = {F(slab.AreaLoadN)} кПа", string.Empty));
            if (slab.Tributary is { } plan)
                for (int i = 0; i < plan.Supports.Count && i < plan.ReactD.Length && i < 6; i++)
                    if (plan.ReactD[i] > 0)
                        Boundary.Add($"Реакция на «{plan.Supports[i].Source.DisplayTitle}»: {F(plan.ReactD[i])} кН (A_груз = {F(plan.ReactD[i] / Math.Max(1e-9, slab.AreaLoadD))} м²)");
        }

        // ───────────── Шаги ─────────────

        private void Check(string title, string clause, string formula, string substitution, double recomputed, double solver, ref double target)
        {
            target = recomputed;
            Steps.Add(new CalculationStep(title, clause, formula, substitution, recomputed, string.Empty, IsCheck: true, Verified: Same(recomputed, solver), SolverValue: solver));
        }

        private void Taken(string title, string clause, string formula, string substitution, double solver) =>
            Steps.Add(new CalculationStep(title, clause, formula, substitution, solver, string.Empty, IsCheck: true, Verified: true, SolverValue: solver, Recomputed: false));

        /// <summary>Множитель γn к проверке первой группы — те же операции, что в солвере (при γn = 1 значение не меняется).</summary>
        private double G(double k) => _gn != 1 ? k * _gn : k;

        private void MuMinStep(double mu, string what)
        {
            var (_, minimum) = ReinforcementCalculator.MuOf(m.Source.Type);
            Steps.Add(new CalculationStep("Минимальное армирование", $"{Sp63}, п. 10.3.6",
                "μ ≥ μmin", $"{what}: μ расчёта = {F(mu * 100)} % ≥ μmin = {F(minimum * 100)} %{(ReinforcementCalculator.StrictMinimum ? " (строгий контроль включён)" : string.Empty)}",
                mu * 100, "%"));
        }

        private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

        private static string F(double v) => ElementCalculationPassport.Format(v, Ru);
        private static string Mm(double m) => (m * 1000).ToString("0", Ru);
        private static string Cm2(double m2) => (m2 * 1e4).ToString("0.#", Ru);
        private static string Cm3(double m3) => (m3 * 1e6).ToString("N0", Ru);
        private static string Cm4(double m4) => (m4 * 1e8).ToString("N0", Ru);

        private static string SectionTitle(AnalyticalMember bar) => bar.Source.EffectiveProfile switch
        {
            ProfileTypes.Circle => $"круглое Ø{Mm(bar.SectionB)} мм",
            ProfileTypes.Pipe => $"труба Ø{Mm(bar.SectionB)} мм",
            ProfileTypes.IBeam => $"двутавр {Mm(bar.SectionH)} × {Mm(bar.SectionB)} мм",
            _ => $"{Mm(bar.SectionB)} × {Mm(bar.SectionH)} мм"
        };
    }

    public static string CategoryTitle(BimCategory c) => c switch
    {
        BimCategory.Column => "Колонна",
        BimCategory.Beam => "Балка",
        BimCategory.Truss => "Ферма",
        BimCategory.Slab => "Плита перекрытия",
        BimCategory.CantileverSlab => "Консольная плита",
        BimCategory.Roof => "Покрытие",
        BimCategory.Terrace => "Терраса",
        BimCategory.Foundation => "Фундаментная плита",
        BimCategory.Wall => "Стена",
        BimCategory.Core => "Ядро жёсткости",
        BimCategory.Diagrid => "Диагрид",
        BimCategory.Shell => "Оболочка",
        _ => "Элемент"
    };
}
