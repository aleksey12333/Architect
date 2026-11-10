#if DEBUG
using AIArchitectStudio.Models.Layouts;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.Services.Layouts;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Xaml;

namespace AIArchitectStudio;

/// <summary>
/// Только Debug — стенд Волны 7:
/// <list type="bullet">
/// <item>AIA_DEBUG_WAVE7TEST=папка — только проверки Волны 7 (wave7.txt), выход при EXIT=1.</item>
/// <item>AIA_DEBUG_W7SHOT=section1 — раздел «1. Генплан и ГПЗУ»: демонстрационный участок, корпус + ТП + паркинг, один правый док;
/// audit — окно «Градостроительные ограничения и ЗОУИТ» после 4-этапного анализа эталонного ГПЗУ с топосъёмкой (вкладка сведений);
/// multiplan — раздел 3: жилой корпус 50 эт., мульти-план «Подвал | 1–5 | 6–10 | 11–50»; basement — подвал с узлами ввода и
/// магистралями, наложение 5-го и 6-го этажей после зеркальной планировки пакета 6–10 (нарушения мокрых зон и разрывы стояков).</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Видимость панелей для автотеста изоляции разделов.</summary>
    internal (bool Inspector, bool SectionDock, bool Structural, bool SitePalette, bool BuildingPalette, bool LayoutPalette, bool StressToggle, bool StructuralSegment,
        bool Chips, bool Assistant, bool PlanHost) DebugChrome()
    {
        static bool V(UIElement e) => e.Visibility == Visibility.Visible;
        return (V(InspectorIsland), V(EnvironmentPanel), V(StructuralPanel), V(SitePalette), V(AddElementButton), V(LayoutPalette), V(DisplayMenuButton), V(StructuralSegment),
            V(CouncilBar), V(AssistantButton), V(FloorPlanHost));
    }

    /// <summary>Подписи вкладок правого дока раздела (автотест).</summary>
    internal IReadOnlyList<string> DebugDockTabs() =>
        EnvironmentPanelTabs.Children.OfType<Microsoft.UI.Xaml.Controls.Border>().SelectMany(b => (b.Child as Microsoft.UI.Xaml.Controls.Grid)?.Children.OfType<UIElement>() ?? [])
            .OfType<Microsoft.UI.Xaml.Controls.RadioButton>().Select(r => r.Content as string ?? string.Empty).ToList();

    /// <summary>Тексты содержимого правого дока (автотест: нет «Конструктора лестниц» и инструментов КЖ в разделе 1).</summary>
    internal string DebugDockText()
    {
        var sb = new System.Text.StringBuilder();
        void Walk(DependencyObject o)
        {
            if (o is Microsoft.UI.Xaml.Controls.TextBlock t) sb.Append(t.Text).Append('|');
            int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(o, i));
        }
        Walk(EnvironmentPanel);
        return sb.ToString();
    }

    internal void DebugRefreshSectionDock() => BuildSectionDock();

    private void InitializeWave7Debug()
    {
        // AIA_DEBUG_SECTION=site|building|interiors — раздел при съёмке (settings.json не меняется: сценарий Debug).
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_SECTION") is { Length: > 0 } section)
            ViewModel.ActiveEnvironment = WorkspaceEnvironmentService.Parse(section, ViewModel.ActiveEnvironment);
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_WAVE7TEST") is { Length: > 0 } testDir)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(1500);
                await CadSelfTestService.RunWave7OnlyAsync(testDir);
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0);
            };
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_W7SHOT") is not { Length: > 0 } shot) return;
        RootGrid.Loaded += async (_, _) =>
        {
            await Task.Delay(1800);
            try
            {
                switch (shot)
                {
                    case "section1": await ShotSection1Async(); break;
                    case "audit": await ShotAuditAsync(); break;
                    case "multiplan": await ShotLayoutsAsync(basement: false); break;
                    case "basement": await ShotLayoutsAsync(basement: true); break;
                }
            }
            catch (Exception ex)
            {
                App.WriteCrashLog("w7shot", ex);
            }
        };
    }

    private async Task ShotSection1Async()
    {
        ViewModel.StartEmptyProject();
        ViewModel.ActiveEnvironment = DeveloperEnvironment.Site;
        await ViewModel.LoadDemoSiteAsync();
        ViewModel.AddStructureToSite(SiteStructureKind.ResidentialSection, 9);
        ViewModel.AddStructureToSite(SiteStructureKind.TransformerSubstation);
        ViewModel.AddStructureToSite(SiteStructureKind.Parking);
        ViewModel.IsHistoryOpen = false;
        await Task.Delay(700);
        if (ViewModel.Scene.SitePlan is { } plan) ViewModel.SelectedSiteVertex = SiteVertexRef.Boundary(plan.PointNumbers.ToList().IndexOf("2"));
        ViewModel.SiteDockTab = 0;
        await Task.Delay(500);
        Viewport.DebugSetCamera(new SharpDX.Vector3(96f, 84f, 112f), new SharpDX.Vector3(-4f, -4f, 2f));
    }

    private async Task ShotAuditAsync()
    {
        ViewModel.StartEmptyProject();
        ViewModel.ActiveEnvironment = DeveloperEnvironment.Site;
        await ViewModel.LoadDemoSiteAsync();
        string kit = Path.Combine(AppContext.BaseDirectory, "DemoKit", "GPZU");
        string pdf = Directory.GetFiles(kit, "*.pdf").First();
        string key = Environment.GetEnvironmentVariable("AIA_DEBUG_W6MODEL") ?? "core";
        await ViewModel.AnalyzeDocumentAsync(pdf, key, openDialog: false);
        ViewModel.IsHistoryOpen = false;
        await Task.Delay(400);
        await ShowConstraintDialogAsync(ConstraintDialogMode.Document, async d =>
        {
            await Task.Delay(400);
            d.DebugView(Environment.GetEnvironmentVariable("AIA_DEBUG_W7VIEW") is { } v && int.TryParse(v, out int n) ? n : 1);
        });
    }

    private async Task ShotLayoutsAsync(bool basement)
    {
        ViewModel.StartEmptyProject();
        ViewModel.ActiveEnvironment = DeveloperEnvironment.Interiors;
        ViewModel.IsHistoryOpen = false;
        ViewModel.PlaceHighRiseWithLayout();
        await Task.Delay(500);
        if (basement && ViewModel.Scene.BuildingLayout is { } plan && plan.Floors.Skip(1).FirstOrDefault() is { } second)
        {
            ViewModel.EditLayoutPackage(second.Id, "mirror");
            ViewModel.RefreshStackReport();
            ViewModel.LayoutView = LayoutViewKind.Basement;
            ViewModel.LayoutDockTab = 2;
        }
        else
        {
            ViewModel.LayoutView = LayoutViewKind.MultiPlan;
            ViewModel.LayoutDockTab = 0;
        }
        ViewModel.IsMultiPlanOpen = true;
        await Task.Delay(600);
        RefreshPlanView();
    }
}
#endif
