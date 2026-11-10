using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Gpzu;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Волна 5 — студия «Градостроительный план (ГПЗУ) и охранные зоны»: документ ГПЗУ и топосъёмка сеанса, видимость слоёв участка
/// во вьюпорте, перенос участка в сцену одним шагом журнала, проверка посадки здания и экспертиза ГПЗУ через модель.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly HashSet<string> _hiddenSiteLayers = new(StringComparer.Ordinal);

    /// <summary>Открытый в студии ГПЗУ (сеанс; в сцену попадает только по кнопке «Построить участок…»).</summary>
    public GpzuDocument? GpzuDocument { get; private set; }

    /// <summary>Топосъёмка ИГДИ сеанса (сети, здания, отметки).</summary>
    public TopoSurvey? GpzuTopo { get; private set; }

    /// <summary>Последняя проверка посадки (для студии и ленты).</summary>
    public GpzuComplianceReport? LastGpzuCompliance { get; private set; }

    public event EventHandler? SiteLayersChanged;
    public event EventHandler? GpzuStudioRequested;

    public bool IsSiteLayerVisible(string id) => !_hiddenSiteLayers.Contains(id);

    public void SetSiteLayerVisible(string id, bool visible)
    {
        bool changed = visible ? _hiddenSiteLayers.Remove(id) : _hiddenSiteLayers.Add(id);
        if (changed) SiteLayersChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenGpzuStudio() => GpzuStudioRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Чтение ГПЗУ (PDF/XML/JSON/TXT/CSV) в фоне; с уже загруженной топосъёмкой — сразу сведение сетей и отметок.</summary>
    public async Task<GpzuDocument> LoadGpzuAsync(string path, bool deepRead = true)
    {
        var doc = await Task.Run(() => GpzuParser.Load(path));
        // Волна 6: помимо координат — полное чтение текста документа (все «НО», сноски, условия) ядром и моделью; итог — в окно
        // подтверждения, когда студия закроется (одно окно за раз).
        if (deepRead && !IsSelfTestRun && !IsDebugAutomation)
            _ = Task.Run(async () =>
            {
                var text = DeepDocumentConstraintService.Read(path);
                await RunOnUiAsync(() => ExtractConstraintsAsync(text, DefaultConstraintModelKey, openDialog: true));
            });
        if (GpzuTopo is { } topo && doc.Boundary.Count > 2)
        {
            var refreshed = await Task.Run(() => TopoSurveyReader.Read(topo.SourceFile, doc.Boundary));
            GpzuTopo = refreshed;
            SiteBoundaryBuilder.Merge(doc, refreshed);
        }
        GpzuDocument = doc;
        return doc;
    }

    /// <summary>Чтение топосъёмки DWG/DXF (окно интереса — участок ГПЗУ, если он уже открыт) и сведение с ГПЗУ.</summary>
    public async Task<TopoSurvey> LoadTopoAsync(string path)
    {
        var topo = await Task.Run(() => TopoSurveyReader.Read(path, GpzuDocument?.Boundary));
        GpzuTopo = topo;
        if (GpzuDocument is { Boundary.Count: > 2 } doc) SiteBoundaryBuilder.Merge(doc, topo);
        return topo;
    }

    /// <summary>План участка по документу и топосъёмке сеанса (без изменения сцены).</summary>
    public GpzuSitePlan? BuildGpzuPlan() => GpzuDocument is { Boundary.Count: > 2 } doc ? SiteBoundaryBuilder.Build(doc, GpzuTopo) : null;

    /// <summary>
    /// «Построить участок и охранные зоны в 3D-сцене»: участок ГПЗУ ставится в документ одним шагом журнала, проверка посадки
    /// существующего здания — в ленту, камера — на участок.
    /// </summary>
    public GpzuComplianceReport? BuildSiteInScene(GpzuSitePlan plan)
    {
        plan = WithDeferredConstraints(plan);
        Scene.ApplySitePlan(plan, Loc.Format("W5UndoName", plan.Document.Cadastral));
        var report = VerifyGpzuSite();
        var doc = plan.Document;
        ChatMessages.Add(ChatMessageModel.Assistant(string.Create(CultureInfo.CurrentCulture,
            $"{Loc.Get("W5BuiltTitle")}: {doc.Cadastral} · {plan.Boundary.Count} {Loc.Get("W5Points")} · {plan.Area:N2} м² · {Loc.Get("W5ZonesCount")} {plan.Zones.Count}") +
            (report is null ? string.Empty : "\n" + Summary(report)), Loc.Get("W5Engine")));
        ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
        return report;
    }

    /// <summary>
    /// Новая сцена: здание шаблона presetId, посаженное на участок подбором сдвига (внутри границы, с наибольшим запасом до
    /// коридоров сетей); затем участок ГПЗУ — отдельным шагом журнала. false — здание на участке не помещается.
    /// </summary>
    public bool PlaceTemplateOnSite(GpzuSitePlan plan, string presetId = "frame")
    {
        var demo = Services.PresetScenesService.Create(presetId, Loc.Language);
        if (GpzuComplianceVerifier.FindPlacement(plan, demo.Elements) is not { } offset) return false;
        foreach (var e in demo.Elements) e.Position = [e.X + offset.Dx, e.Y, e.Z + offset.Dz];
        foreach (var g in demo.Groups)
        {
            g.OriginX += offset.Dx;
            g.OriginZ += offset.Dz;
        }
        Scene.LoadScene(demo.Elements, demo.Groups, recordUndo: false, clearHistory: true, axes: []);
        Scene.ProjectName = demo.Title;
        BuildSiteInScene(plan);
        return true;
    }

    /// <summary>Проверка посадки текущей сцены на участок ГПЗУ (null — участка нет).</summary>
    public GpzuComplianceReport? VerifyGpzuSite()
    {
        if (Scene.SitePlan is not { } plan) return null;
        LastGpzuCompliance = GpzuComplianceVerifier.Verify(plan, Scene.Elements.ToList());
        return LastGpzuCompliance;
    }

    public string Summary(GpzuComplianceReport r) =>
        string.Create(CultureInfo.CurrentCulture, $"{Loc.Get("W5Compliance")}: {(r.Passed ? Loc.Get("W5Passed") : Loc.Format("W5Failed", r.Critical))} · ") +
        string.Join(" · ", r.Findings.Where(f => f.Severity != GpzuSeverity.Info).Select(f => (f.Severity == GpzuSeverity.Ok ? string.Empty : Loc.Get("W5Violation") + ": ") + f.Title));

    /// <summary>Экспертиза ГПЗУ моделью облачного сервиса (без сервиса — офлайн-заключение ядра); итог — в ленту чата.</summary>
    public async Task<GpzuExpertiseResult?> RunGpzuExpertiseAsync(string model, CancellationToken ct = default)
    {
        if (GpzuDocument is not { } doc) return null;
        var report = Scene.SitePlan is not null ? VerifyGpzuSite() : null;
        var result = await GpzuExpertiseService.AskAsync(UnifiedCloudAiClient.Shared, model, doc, GpzuTopo, report, TimeSpan.FromSeconds(150), ct);
        string header = result.Offline ? Loc.Get("W5Engine") : Loc.Get("W41Cloud") + " · " + result.Model;
        ChatMessages.Add(ChatMessageModel.Assistant(Loc.Get("W5ExpertiseTitle") + "\n" + result.Text, header));
        return result;
    }
}
