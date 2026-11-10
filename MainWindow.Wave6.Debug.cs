#if DEBUG
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.ViewModels;

namespace AIArchitectStudio;

/// <summary>
/// Только Debug — стенд Волны 6:
/// <list type="bullet">
/// <item>AIA_DEBUG_WAVE6TEST=папка — только проверки Волны 6 (wave6.txt), выход при EXIT=1.</item>
/// <item>AIA_DEBUG_W6SHOT=site — раздел 1: демонстрационный участок как земля проекта (рельеф, грунт, сети), корпус ЖК + ТП + паркинг,
/// точка № 2 перенесена мышиным путём, начальный экран свёрнут в капсулу; constraints[;файл] — окно «Градостроительные ограничения и ЗОУИТ»
/// документа (W6MODEL=core|sonnet|luna|opus|local); walk — проход по лестнице к двери второго этажа (упор в стену клетки, дверь открыта).</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeWave6Debug()
    {
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_WAVE6TEST") is { Length: > 0 } testDir)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(1500);
                await CadSelfTestService.RunWave6OnlyAsync(testDir);
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0);
            };
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_W6SHOT") is not { Length: > 0 } shot) return;
        RootGrid.Loaded += async (_, _) =>
        {
            await Task.Delay(1800);
            try
            {
                var parts = shot.Split(';');
                switch (parts[0])
                {
                    case "site":
                        await ShotSiteAsync();
                        break;
                    case "constraints":
                        await ShotConstraintsAsync(parts.Length > 1 ? parts[1] : null);
                        break;
                    case "walk":
                        await ShotWalkAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                App.WriteCrashLog("w6shot", ex);
            }
        };
    }

    private async Task ShotSiteAsync()
    {
        ViewModel.StartEmptyProject();
        ViewModel.ActiveEnvironment = DeveloperEnvironment.Site;
        await ViewModel.LoadDemoSiteAsync();
        ViewModel.AddStructureToSite(SiteStructureKind.ResidentialSection, 9);
        ViewModel.AddStructureToSite(SiteStructureKind.TransformerSubstation);
        ViewModel.AddStructureToSite(SiteStructureKind.Parking);
        await Task.Delay(700);
        if (ViewModel.Scene.SitePlan is { } plan)
        {
            int i2 = plan.PointNumbers.ToList().IndexOf("2");
            var p = plan.Boundary[i2];
            Viewport.DebugDragSiteVertex(SiteVertexRef.Boundary(i2), p.X, p.Z - 5);
            ViewModel.SelectedSiteVertex = SiteVertexRef.Boundary(i2);
        }
        await Task.Delay(900);
        Viewport.DebugSetCamera(new SharpDX.Vector3(96f, 84f, 112f), new SharpDX.Vector3(-4f, -4f, 2f));
        await Task.Delay(600);
        if (ViewModel.Scene.SitePlan is { } p2) Viewport.DebugDragSiteVertex(ViewModel.SelectedSiteVertex ?? SiteVertexRef.Boundary(1), p2.Boundary[1].X, p2.Boundary[1].Z);
        (Microsoft.UI.Xaml.Application.Current as App)?.ShowStartScreenFromEditor();
        await Task.Delay(1800);
        App.StartScreenWindow?.CollapseToCapsule();
    }

    private async Task ShotConstraintsAsync(string? file)
    {
        ViewModel.StartEmptyProject();
        ViewModel.ActiveEnvironment = DeveloperEnvironment.Site;
        await ViewModel.LoadDemoSiteAsync();
        string key = Environment.GetEnvironmentVariable("AIA_DEBUG_W6MODEL") ?? "core";
        if (file is { Length: > 0 } && File.Exists(file)) await ViewModel.ExtractConstraintsAsync(file, key, openDialog: false);
        else
        {
            // Документ с особыми условиями в тексте: эталонный ГПЗУ целиком + нестандартные условия в сносках.
            string kit = Path.Combine(AppContext.BaseDirectory, "DemoKit", "GPZU");
            string pdf = Directory.GetFiles(kit, "*.pdf").First();
            var text = await Task.Run(() => DeepDocumentConstraintService.Read(pdf));
            var hidden = DeepDocumentConstraintService.FromText(CadSelfTestService.W6HiddenConditionsText, "ГПЗУ-2026-0006.txt");
            foreach (var (page, t) in hidden.Pages) text.Pages.Add((text.Pages.Count + 1, t));
            text.Sentences.AddRange(hidden.Sentences.Select(s => s with { Page = s.Page + text.Pages.Count - hidden.Pages.Count }));
            await ViewModel.ExtractConstraintsAsync(text, key, openDialog: false);
        }
        await Task.Delay(400);
        await ShowConstraintDialogAsync(ConstraintDialogMode.Document, async d =>
        {
            await Task.Delay(500);
            d.DebugShowcase();
        });
    }

    private async Task ShotWalkAsync()
    {
        var elements = CadSelfTestService.CreateWalkTestScene();
        const string g = "w6-walk";
        ArchitecturalElement E(string type, string name, double x, double y, double z, double w, double h, double d, double rot = 0, string material = "concrete") => new()
        {
            Type = type, Name = name, BuildingGroupId = g, LevelIndex = y > 2 ? 1 : 0, Material = material,
            Size = [w, h, d], Position = [x, y + h / 2, z], Rotation = [0, rot, 0]
        };
        // Стена лестничной клетки слева от марша и перегородка второго этажа с дверью перед выходом с лестницы.
        elements.Add(E(ElementTypes.Wall, "Стена лестничной клетки", -3.85, 0, 0.5, 8.0, 5.8, 0.2, 90));
        var partition = E(ElementTypes.Wall, "Перегородка 2 этажа", 0, 3.0, 4.4, 16, 2.8, 0.15, 0, "brick");
        elements.Add(partition);
        var door = E(ElementTypes.Door, "Дверь 2-Д1", -3, 3.0, 4.4, 1.0, 2.1, 0.21, 0, "timber");
        door.HostElementId = partition.Id;
        elements.Add(door);
        elements.Add(E(ElementTypes.Wall, "Наружная стена 2 этажа", 0, 3.0, 6, 16, 2.8, 0.3));
        elements.Add(E(ElementTypes.Wall, "Торцевая стена", 8, 0, 0, 12, 5.8, 0.3, 90));
        elements.Add(E(ElementTypes.Slab, "Покрытие", 0, 5.8, 0, 16, 0.25, 12));
        ViewModel.Scene.LoadScene(elements, [new BuildingGroup { Id = g, Name = "Проектируемый корпус" }], recordUndo: false, clearHistory: true, axes: []);
        ViewModel.ActiveEnvironment = DeveloperEnvironment.Interiors;
        await Task.Delay(900);
        ViewModel.IsWalkthrough = true;
        await Task.Delay(900);
        // На лестнице: ноги на ступени, взгляд вверх по маршу к двери второго этажа.
        Viewport.DebugWalkPose(-2.95, -3.6, 0, 180, 4);
        Viewport.DebugWalkSteps(0, 1, 6.0);
        Viewport.DebugToggleDoorAhead();
        Viewport.DebugWalkSteps(-1, 0, 0.8);
        Viewport.DebugHoldWalkStatus(true);
        Viewport.DebugWalkSteps(1, 0, 0.55);
        Viewport.DebugWalkSteps(0, -1, 2.3);
        Viewport.DebugWalkPose(Viewport.DebugWalkX, Viewport.DebugWalkZ, Viewport.DebugWalkFeet, 180, -2);
    }
}
#endif
