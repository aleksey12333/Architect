#if DEBUG
using System.Text;
using AIArchitectStudio.Services.Gpzu;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AIArchitectStudio;

/// <summary>
/// Только Debug — стенд Волны 5 (ГПЗУ и охранные зоны):
/// <list type="bullet">
/// <item>AIA_DEBUG_GPZUPARSE=ГПЗУ.pdf;топосъёмка.dwg;отчёт.txt — разбор документов и сводка в файл (без окна), выход при EXIT=1
/// (+ GPZULINES=1 — строки текстового слоя).</item>
/// <item>AIA_DEBUG_GPZU=studio[;зона] — студия с демонстрационным комплектом DemoKit/GPZU (выбрана зона по Id);
/// 3d — шаблон «Каркасный корпус», посаженный на участок, и участок с охранными зонами в сцене;
/// sheet;путь.pdf[;путь.png] — лист ГПЗУ/ПЗУ (+ рендер первой страницы в PNG для просмотра).</item>
/// <item>AIA_DEBUG_WAVE5TEST=папка — только проверки Волны 5 (wave5.txt).</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeWave5Debug()
    {
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_WAVE5TEST") is { Length: > 0 } testDir)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(1500);
                await Services.CadSelfTestService.RunWave5OnlyAsync(testDir);
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0);
            };
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_GPZUPARSE") is { Length: > 0 } parseSpec)
            RootGrid.Loaded += async (_, _) =>
            {
                var parts = parseSpec.Split(';');
                var sb = new StringBuilder();
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var doc = await Task.Run(() => GpzuParser.Load(parts[0]));
                    sb.AppendLine($"PDF разобран за {sw.ElapsedMilliseconds} мс");
                    Models.Gpzu.TopoSurvey? topo = null;
                    if (parts.Length > 1 && parts[1].Length > 0)
                    {
                        topo = await Task.Run(() => TopoSurveyReader.Read(parts[1], doc.Boundary));
                        SiteBoundaryBuilder.Merge(doc, topo);
                    }
                    sb.Append(GpzuReport.Describe(doc, topo));
                    if (Environment.GetEnvironmentVariable("AIA_DEBUG_GPZULINES") == "1")
                    {
                        var pdf = Services.Pdf.PdfNativeContentExtractor.Read(parts[0]);
                        foreach (var page in pdf.Pages)
                            foreach (var line in GpzuParser.LinesOf(page))
                                sb.AppendLine($"[{line.Page + 1}] {line.Y:0.0} {line.X:0.0} | {line.Text}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("СБОЙ: " + ex);
                }
                await File.WriteAllTextAsync(parts.Length > 2 ? parts[2] : "gpzu.txt", sb.ToString(), Encoding.UTF8);
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0);
            };
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_GPZU") is { Length: > 0 } spec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(1800);
                var parts = spec.Split(';');
                string dir = Path.Combine(AppContext.BaseDirectory, "DemoKit", "GPZU");
                foreach (var f in Directory.GetFiles(dir, "*.pdf")) await ViewModel.LoadGpzuAsync(f);
                foreach (var f in Directory.GetFiles(dir, "*.dwg")) await ViewModel.LoadTopoAsync(f);
                switch (parts[0])
                {
                    case "studio":
                        if (Environment.GetEnvironmentVariable("AIA_DEBUG_GPZUBUILD") == "1" && ViewModel.BuildGpzuPlan() is { } built) ViewModel.PlaceTemplateOnSite(built);
                        await ShowGpzuStudioAsync(async d =>
                        {
                            if (parts.Length > 1) d.SelectZone(parts[1]);
                            await Task.CompletedTask;
                        });
                        break;
                    case "3d":
                        if (ViewModel.BuildGpzuPlan() is { } plan) ViewModel.PlaceTemplateOnSite(plan);
                        // Аэроракурс с юга (север плана вверху кадра) после «Показать всё»; AIA_DEBUG_GPZUCAM=x,y,z;tx,ty,tz — свой.
                        await Task.Delay(1600);
                        var cam = (Environment.GetEnvironmentVariable("AIA_DEBUG_GPZUCAM") ?? "-62,118,138;2,6,-4").Split(',', ';')
                            .Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        Viewport.DebugSetCamera(new SharpDX.Vector3(cam[0], cam[1], cam[2]), new SharpDX.Vector3(cam[3], cam[4], cam[5]));
                        break;
                    case "sheet":
                        if (ViewModel.BuildGpzuPlan() is { } sheetPlan)
                        {
                            ViewModel.PlaceTemplateOnSite(sheetPlan);
                            string pdfPath = parts.Length > 1 ? parts[1] : Path.Combine(Path.GetTempPath(), "gpzu_sheet.pdf");
                            var report = GpzuComplianceVerifier.Verify(sheetPlan, ViewModel.Scene.Elements.ToList());
                            await Task.Run(() => Services.GostDrawingPdfExporter.ExportGpzuSheet(sheetPlan, report, pdfPath, ViewModel.Scene.Elements.ToList()));
                            if (parts.Length > 2) await RenderPdfPageAsync(pdfPath, parts[2], 3600);
                        }
                        if (Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0);
                        break;
                }
            };
    }

    /// <summary>Рендер первой страницы PDF в PNG (системный растеризатор Windows.Data.Pdf) — для просмотра листа.</summary>
    private static async Task RenderPdfPageAsync(string pdfPath, string pngPath, uint width)
    {
        var file = await StorageFile.GetFileFromPathAsync(pdfPath);
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        using var page = pdf.GetPage(0);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = width, BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) });
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        await File.WriteAllBytesAsync(pngPath, bytes);
    }
}
#endif
