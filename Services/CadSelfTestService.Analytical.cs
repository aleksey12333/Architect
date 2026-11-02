#if DEBUG
using System.Diagnostics;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Analytical;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест расчётной схемы (фаза 5): на всех сценах эталона стержни и пластины несут свои жёсткости и узлы (общие в точках
/// сопряжения, опоры — только на грунте и под фундаментами), солвер по готовой схеме ≡ расчёту по элементам до бита и схему
/// не меняет (повторный расчёт той же схемы — тот же результат), построение и расчёт вместе укладываются в норматив.
/// </summary>
public static partial class CadSelfTestService
{
    private static int CheckAnalyticalModel(StringBuilder report)
    {
        int failures = 0;
        var loads = new StructuralLoadSettings { Seismicity = 8 };
        var scenes = BaselineSources().ToList();

        // 1. Состав схемы: колонны, ядра, диагрид, балки, фермы — стержни с A, Iy, Iz, E, G, J; плиты, стены, фундаменты — пластины
        //    с контуром; узлы пронумерованы подряд, общие узлы колонна ↔ балка есть, жёсткие опоры — только на грунте и под фундаментами.
        var broken = new List<string>();
        var stats = new List<string>();
        foreach (var scene in scenes)
        {
            var model = AnalyticalModelBuilder.Build(scene.Elements, scene.Groups, loads);
            foreach (var m in model.Elements)
            {
                bool stick = m.Category is BimCategory.Column or BimCategory.Core or BimCategory.Diagrid or BimCategory.Beam or BimCategory.Truss;
                if (stick != m is AnalyticalMember) broken.Add($"{scene.Name}: {m} — не тот вид элемента схемы");
                if (m is AnalyticalMember bar)
                {
                    bool profiled = bar.Role == AnalyticalRole.Vertical || bar.Category == BimCategory.Beam && bar.Shape == AnalyticalShape.Segment;
                    if (bar.StartNode is null || bar.EndNode is null) broken.Add($"{scene.Name}: {m} без узлов");
                    else if (profiled && !(bar.A > 0 && bar.Iy > 0 && bar.Iz > 0 && bar.E > 0 && bar.G > 0 && bar.J > 0 && bar.Iy >= bar.Iz))
                        broken.Add($"{scene.Name}: {m} — A {bar.A}, Iy {bar.Iy}, Iz {bar.Iz}, E {bar.E}, G {bar.G}, J {bar.J}");
                    else if (bar.Role == AnalyticalRole.Line && bar.Supports.Count > 0 && bar.SupportStations.Count != bar.Supports.Count)
                        broken.Add($"{scene.Name}: {m} — опор {bar.Supports.Count}, на оси {bar.SupportStations.Count}");
                }
                else if (m is AnalyticalPanel panel && (panel.Contour.Count < 3 || panel.Role == AnalyticalRole.Slab && panel.Tributary is null))
                    broken.Add($"{scene.Name}: {m} — узлов контура {panel.Contour.Count}, грузовые площади {(panel.Tributary is null ? "нет" : "есть")}");
            }
            for (int i = 0; i < model.Nodes.Count; i++)
                if (model.Nodes[i].Id != i || model.Nodes[i].FirstEquation != i * AnalyticalNode.DegreesOfFreedom) broken.Add($"{scene.Name}: узел {i} — номер {model.Nodes[i].Id}");
            var foundationNodes = model.Panels.Where(p => p.Role == AnalyticalRole.Foundation).SelectMany(p => p.Contour).ToHashSet();
            var badSupports = model.Nodes.Where(n => n.IsFixedSupport && n.Y > AnalyticalModel.GroundLevel && !foundationNodes.Contains(n)).ToList();
            if (badSupports.Count > 0) broken.Add($"{scene.Name}: опоры не на грунте — {string.Join(", ", badSupports.Take(3))}");
            var columnTops = model.Members.Where(b => b.Category == BimCategory.Column).Select(b => b.EndNode).ToHashSet();
            int shared = model.Members.Where(b => b.Role == AnalyticalRole.Line).SelectMany(b => b.Nodes).Count(columnTops.Contains);
            stats.Add($"{scene.Name} {model.Members.Count}/{model.Panels.Count}/{model.Nodes.Count} (общих колонна-балка {shared}, опор {model.Nodes.Count(n => n.IsFixedSupport)})");
            if (scene.Name == "preset-frame" && shared == 0) broken.Add($"{scene.Name}: нет общих узлов колонн и балок");
        }
        Check(report, ref failures, broken.Count == 0,
            $"расчётная схема на {scenes.Count} сценах эталона — стержни (A, Iy ≥ Iz, E, G, J > 0, опоры на оси), пластины (контур, грузовые площади), " +
            $"узлы с 6 степенями свободы подряд, жёсткие опоры только на грунте и под фундаментами; стержней/пластин/узлов: {string.Join("; ", stats)}" +
            (broken.Count > 0 ? ": " + string.Join("; ", broken.Take(5)) : string.Empty));

        // 2. Солвер по готовой схеме ≡ расчёту по элементам до бита; повторный расчёт той же схемы — тот же результат (схема не меняется).
        var mismatch = new List<string>();
        foreach (var scene in scenes)
        {
            string direct = AnalysisFingerprint(StructuralAnalysisService.Analyze(scene.Elements, scene.Groups, loads));
            var model = AnalyticalModelBuilder.Build(scene.Elements, scene.Groups, loads, parallel: false);
            string first = AnalysisFingerprint(StructuralAnalysisService.Analyze(model));
            string second = AnalysisFingerprint(StructuralAnalysisService.Analyze(model));
            if (first != direct || second != direct) mismatch.Add($"{scene.Name}: по схеме {(first == direct ? "≡" : "≠")}, повторно {(second == direct ? "≡" : "≠")}");
        }
        Check(report, ref failures, mismatch.Count == 0,
            $"Analyze(IAnalyticalModel) ≡ Analyze(элементы) до бита на {scenes.Count} сценах, повторный расчёт той же схемы совпадает" +
            (mismatch.Count > 0 ? ": " + string.Join("; ", mismatch) : string.Empty));

        // 3. Время: построение схемы и расчёт по ней на башне 30 этажей (медиана 9 прогонов после прогрева).
        var tower = PresetScenesService.Create(PresetScenesService.Tower);
        StructuralAnalysisService.Analyze(tower.Elements, tower.Groups, loads);
        var build = new List<double>();
        var solve = new List<double>();
        var total = new List<double>();
        for (int i = 0; i < 9; i++)
        {
            var sw = Stopwatch.StartNew();
            var model = AnalyticalModelBuilder.Build(tower.Elements, tower.Groups, loads);
            build.Add(sw.Elapsed.TotalMilliseconds);
            solve.Add(StructuralAnalysisService.Analyze(model).ElapsedMilliseconds);
            total.Add(StructuralAnalysisService.Analyze(tower.Elements, tower.Groups, loads).ElapsedMilliseconds);
        }
        double Median(List<double> v) => v.Order().ElementAt(v.Count / 2);
        var towerModel = AnalyticalModelBuilder.Build(tower.Elements, tower.Groups, loads);
        Check(report, ref failures, Median(total) < 10,
            $"башня 30 эт.: схема {Median(build):0.00} мс (стержней {towerModel.Members.Count}, пластин {towerModel.Panels.Count}, узлов {towerModel.Nodes.Count}, " +
            $"уравнений {((IAnalyticalModel)towerModel).EquationCount}) + расчёт {Median(solve):0.00} мс, вместе {Median(total):0.00} мс (норматив < 10 мс)");
        return failures;
    }
}
#endif
