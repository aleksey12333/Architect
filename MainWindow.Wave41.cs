#if DEBUG
using System.Globalization;
using System.Text;
using System.Text.Json;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Photo;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio;

/// <summary>
/// Только Debug — стенд волны 4.1 (живые испытания через облачный сервис сеанса, ключ — только из переменных окружения):
/// <list type="bullet">
/// <item>AIA_DEBUG_LIVEPHOTO=папка снимков (+ LIVEMODELS=sonnet-5.5,gpt-5.6-luna, LIVEOUT=папка, LIVEONLY=подстрока, LIVEREVISIONS=1) —
/// диалог по каждому снимку каждой моделью; live_report.json, live_log.txt (журнал вызовов без ключа), ответы ходов, рендеры моделей.</item>
/// <item>AIA_DEBUG_LIVEREPLAY=папка отчёта — витрина «снимок → модель Sonnet → модель Luna» по сохранённому прогону (без сети).</item>
/// <item>AIA_DEBUG_LIVECHAT=путь снимка;модель — тот же диалог в главном окне: лента обмена с моделью и проверки ядром в чате, вставка итога.</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private sealed class LiveRun
    {
        public string Photo { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string ModelUsed { get; set; } = string.Empty;
        public bool ImagesDelivered { get; set; }
        public int Turns { get; set; }
        public int ChosenTurn { get; set; }
        public long TokensIn { get; set; }
        public long TokensOut { get; set; }
        public double ModelSeconds { get; set; }
        public double TotalSeconds { get; set; }
        public int Floors { get; set; }
        public string Morphology { get; set; } = string.Empty;
        public double K { get; set; }
        public string Audit { get; set; } = string.Empty;
        public double SumF { get; set; }
        public double SumR { get; set; }
        public bool Rebuilt { get; set; }
        public bool CoreFrame { get; set; }
        public double FidelityBefore { get; set; }
        public double FidelityAfter { get; set; }
        public string CvMorphology { get; set; } = string.Empty;
        public int CvFloors { get; set; }
        public string? Reading1 { get; set; }
        public string? Reading2 { get; set; }
        public string? HelperReading { get; set; }
        public string? Critique1 { get; set; }
        public string? Critique2 { get; set; }
        public List<string> Remarks1 { get; set; } = [];
        public List<string> Remarks2 { get; set; } = [];
        public List<string> Reconcile { get; set; } = [];
        public List<string> Checks { get; set; } = [];
        public int ChecksPassed { get; set; }
        public int ChecksTotal { get; set; }
        public string Render { get; set; } = string.Empty;
        public string? Error { get; set; }
    }

    private static readonly JsonSerializerOptions LiveJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private void InitializeWave41Debug()
    {
        InitializeWave5Debug();
        // AIA_DEBUG_WAVE41TEST=папка — только проверки волны 4.1 (wave41.txt), выход при EXIT=1.
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_WAVE41TEST") is { Length: > 0 } testDir)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(1500);
                await CadSelfTestService.RunWave41OnlyAsync(testDir);
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0);
            };
        // AIA_DEBUG_RINGPROFILE=папка — время этапов сборки кольцевого атриума (синтез, подбор, каждый проверщик шлюза).
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_RINGPROFILE") is { Length: > 0 } profDir)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(1000);
                var sb = new StringBuilder();
                async Task Flush() => await File.WriteAllTextAsync(Path.Combine(profDir, "ringprofile.txt"), sb.ToString(), Encoding.UTF8);
                Directory.CreateDirectory(profDir);
                var d = new PhotoArchitectureDescriptor { SourceName = "ring", Morphology = PhotoMorphology.Rotunda, HasCourtyard = true, Floors = 9, FloorHeight = 4, WidthM = 86, DepthM = 86, ColonnadeColumns = 10, Roof = PhotoRoofKind.Dome };
                var loads = new Models.StructuralLoadSettings();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var model = PhotoBimSynthesizer.Build(d);
                sb.AppendLine($"synth {sw.ElapsedMilliseconds} ms, elements {model.Elements.Count}, columns {model.Elements.Count(e => e.Category == Models.BimCategory.Column)}");
                sw.Restart();
                var log = new List<string>();
                await Task.Run(() => PhotoToBimService.PreSize(model.Elements, model.Groups, loads, log));
                sb.AppendLine($"presize {sw.ElapsedMilliseconds} ms: " + string.Join(" | ", log));
                var an = Services.StructuralAnalysisService.Analyze(model.Elements, model.Groups, loads);
                foreach (var e in model.Elements.Where(e => an.For(e.Id) is { Utilization: > 0.95 }).OrderByDescending(e => an.For(e.Id)!.Utilization).Take(10))
                {
                    var r = an.For(e.Id)!;
                    sb.AppendLine($"  over: {e.Name} ({e.Type}, {e.Material}) K {r.Utilization:0.00} str {r.KStrength:0.00} stab {r.KStability:0.00} shear {r.KShear:0.00} defl {r.KDeflection:0.00} punch {r.KPunching:0.00} soil {r.KSoil:0.00} span {r.Span:0.0} W {e.Width:0.00} H {e.Height:0.00} D {e.Depth:0.00}");
                }
                await Flush();
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_RINGPROFILEFAST") == "1") { if (Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0); return; }
                var sbx = Services.Verification.DesignFixEngine.Sandbox(model.Elements, model.Groups, model.Axes);
                var vctx = Services.Verification.VerificationContext.FromScene(sbx, loads, null);
                foreach (var v in Services.Verification.BimVerificationPipeline.CreateDefault().Verifiers)
                {
                    sw.Restart();
                    var task = Task.Run(() => v.Verify(vctx).Count());
                    bool done = await Task.WhenAny(task, Task.Delay(120000)) == task;
                    sb.AppendLine($"  {v.Id}: {(done ? task.Result + " findings" : "TIMEOUT")} {sw.ElapsedMilliseconds} ms");
                    await Flush();
                }
                sw.Restart();
                var gate = await Task.Run(() => Services.Verification.VerificationGateService.Inspect(Services.Verification.DesignFixEngine.Sandbox(model.Elements, model.Groups, model.Axes), loads, "профиль", cascade: true));
                sb.AppendLine($"gate {sw.ElapsedMilliseconds} ms, fixes {gate.Fixes.Count}, unresolved {gate.Unresolved.Count}, passes {gate.Passes}");
                await Flush();
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0);
            };
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_LIVEPHOTO") is { Length: > 0 } liveDir)
            RootGrid.Loaded += async (_, _) => await RunLivePhotosAsync(liveDir);
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_LIVEREPLAY") is { Length: > 0 } replayDir)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2600);
                await ShowLiveShowcaseAsync(replayDir);
            };
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_LIVECHATREPLAY") is { Length: > 0 } replaySpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2600);
                await ReplayLiveChatAsync(replaySpec);
            };
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_LIVECHAT") is { Length: > 0 } chatSpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2600);
                await WaitGatewayAsync();
                var parts = chatSpec.Split(';');
                var photo = await PhotoImageLoader.FromFileAsync(parts[0]);
                var (descriptor, _, jpeg) = await PhotoToBimService.AnalyzeAsync(photo);
                await ViewModel.RunCloudPhotoDialogueAsync(parts.Length > 1 ? parts[1] : UnifiedCloudAiClient.Luna56.Alias, descriptor,
                    new PhotoLiftOptions { GroupName = Path.GetFileNameWithoutExtension(photo.Name) }, jpeg, photo.Name, null,
                    helper: parts.Length > 2 ? parts[2] : UnifiedCloudAiClient.Gemini31Pro.Alias);
            };
    }

    /// <summary>
    /// Офлайн-повтор живого диалога в чате (AIA_DEBUG_LIVECHATREPLAY=папка отчёта;снимок;модель): заглушка шлюза отдаёт сохранённые
    /// ответы ходов 1 и 2 этой модели, ядро расчётов проверяет каждый ход заново — лента самоисправления без сети.
    /// </summary>
    private async Task ReplayLiveChatAsync(string spec)
    {
        var parts = spec.Split(';');
        string dir = parts[0], photoPath = parts[1], model = parts.Length > 2 ? parts[2] : UnifiedCloudAiClient.Luna56.Alias;
        string stem = Path.GetFileNameWithoutExtension(photoPath) + "_" + model.Replace('.', '_');
        var turns = new List<string>();
        for (int t = 1; t <= 3; t++)
            if (File.Exists(Path.Combine(dir, $"{stem}_turn{t}.txt"))) turns.Add(await File.ReadAllTextAsync(Path.Combine(dir, $"{stem}_turn{t}.txt")));
        string id = UnifiedCloudAiClient.SpecOf(model)?.Patterns[0] ?? model;
        var client = new UnifiedCloudAiClient(new UnifiedGatewayConfig("https://replay.invalid/api", "sk-replay-offline"), new ReplayGatewayHandler(id, turns));
        await client.DiscoverAsync();
        UnifiedCloudAiClient.Shared = client;
        var photo = await PhotoImageLoader.FromFileAsync(photoPath);
        var (descriptor, _, jpeg) = await PhotoToBimService.AnalyzeAsync(photo);
        int start = ViewModel.ChatMessages.Count;
        await ViewModel.RunCloudPhotoDialogueAsync(model, descriptor, new PhotoLiftOptions { GroupName = Path.GetFileNameWithoutExtension(photo.Name) }, jpeg, photo.Name, null);
        // Кадр самоисправления: лента — к первому ответу модели (ниже — замечания ядра и ход 2).
        await Task.Delay(1500);
        var answer = ViewModel.ChatMessages.Skip(start).FirstOrDefault(m => m.Header?.Contains(L.Get("W41Cloud"), StringComparison.Ordinal) == true);
        if (answer is not null) ChatList.ScrollIntoView(answer, ScrollIntoViewAlignment.Leading);
    }

    /// <summary>Заглушка шлюза для повтора: /v1/models — одна модель, /chat/completions — сохранённые ответы по порядку.</summary>
    private sealed class ReplayGatewayHandler(string modelId, IReadOnlyList<string> turns) : HttpMessageHandler
    {
        private int _n;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/models", StringComparison.Ordinal))
                json = JsonSerializer.Serialize(new { data = new[] { new { id = modelId, @object = "model" } }, @object = "list" });
            else
            {
                await Task.Delay(900, ct);
                string text = turns.Count == 0 ? "{}" : turns[Math.Min(_n++, turns.Count - 1)];
                json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = text }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 7800, completion_tokens = text.Length / 4 } });
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task WaitGatewayAsync()
    {
        for (int i = 0; i < 300 && UnifiedCloudAiClient.Shared is not { IsDiscovered: true }; i++) await Task.Delay(100);
    }

    private static double LiveAzimuth(PhotoMorphology m) => m switch { PhotoMorphology.Cascading => 32, PhotoMorphology.Triangular => 6, PhotoMorphology.ArchSpan => -38, _ => -30 };
    private static double LiveElevation(PhotoMorphology m) => m == PhotoMorphology.Rotunda ? 34 : 18;

    private async Task RunLivePhotosAsync(string dir)
    {
        await Task.Delay(1500);
        await WaitGatewayAsync();
        string outDir = Environment.GetEnvironmentVariable("AIA_DEBUG_LIVEOUT") is { Length: > 0 } o ? o : Path.Combine(dir, "live");
        Directory.CreateDirectory(outDir);
        var models = (Environment.GetEnvironmentVariable("AIA_DEBUG_LIVEMODELS") ?? string.Join(",", UniversalVisionModelBridge.WorkhorseModels)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string only = Environment.GetEnvironmentVariable("AIA_DEBUG_LIVEONLY") ?? string.Empty;
        int revisions = int.TryParse(Environment.GetEnvironmentVariable("AIA_DEBUG_LIVEREVISIONS"), out var rv) ? rv : 1;
        var truths = HardPhotoManifest.Load(dir);
        var log = new StringBuilder();
        var runs = new List<LiveRun>();
        string reportPath = Path.Combine(outDir, "live_report.json");
        if (File.Exists(reportPath) && Environment.GetEnvironmentVariable("AIA_DEBUG_LIVEAPPEND") == "1")
            runs = JsonSerializer.Deserialize<List<LiveRun>>(await File.ReadAllTextAsync(reportPath)) ?? [];
        var client = UnifiedCloudAiClient.Shared;
        log.AppendLine($"Шлюз: {(client is null ? "не подключён" : client.Config.ToString())}; моделей пула {client?.Models.Count ?? 0}: " +
                       string.Join(", ", client?.Models.Select(m => $"{m.Spec.Title} = {m.Id}") ?? []));
        string liveLog = Path.Combine(outDir, "live_log_" + DateTime.Now.ToString("yyyyMMdd") + ".txt");
        if (client is not null)
            client.CallCompleted += (_, r) =>
            {
                string text;
                lock (log) { log.AppendLine(UnifiedCloudAiClient.Describe(r)); text = log.ToString(); }
                try { File.WriteAllText(liveLog, text, Encoding.UTF8); } catch (IOException) { }
            };
        var loads = new Models.StructuralLoadSettings();
        foreach (var file in Directory.GetFiles(dir).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                     .Where(f => only.Length == 0 || only.Split(',').Any(o => Path.GetFileName(f).Contains(o.Trim(), StringComparison.OrdinalIgnoreCase))).OrderBy(f => f))
        {
            var photo = await PhotoImageLoader.FromFileAsync(file);
            var (cv, _, jpeg) = await PhotoToBimService.AnalyzeAsync(photo);
            var truth = HardPhotoManifest.For(file, truths);
            foreach (var model in models)
            {
                var run = new LiveRun { Photo = Path.GetFileName(file), Title = truth?.Title ?? photo.Name, Model = model, CvMorphology = PhotoArchitectureDescriptor.MorphologyCode(cv.Morphology), CvFloors = cv.Floors };
                runs.RemoveAll(r => r.Photo == run.Photo && r.Model == model);
                runs.Add(run);
                lock (log) log.AppendLine($"=== {run.Photo} · {model} · Визуальное ядро: {run.CvMorphology}, этажей {cv.Floors}, силуэт {cv.SegmentationQuality:0.00}");
                try
                {
                    if (client is null) throw new InvalidOperationException("облачный сервис не подключён");
                    var res = await UniversalVisionModelBridge.RunDialogueAsync(client, App.Services.GetService(typeof(AIOrchestrator)) as AIOrchestrator ?? throw new InvalidOperationException(),
                        model, cv, jpeg, new PhotoLiftOptions { GroupName = Path.GetFileNameWithoutExtension(file) }, loads, null, revisions,
                        fast: Environment.GetEnvironmentVariable("AIA_DEBUG_LIVEFULL") != "1", helper: Environment.GetEnvironmentVariable("AIA_DEBUG_LIVEHELPER"));
                    run.HelperReading = res.HelperReading?.Describe();
                    string stem = Path.GetFileNameWithoutExtension(file) + "_" + model.Replace('.', '_');
                    await File.WriteAllTextAsync(Path.Combine(outDir, stem + "_turn1.txt"), res.Turn1Text, Encoding.UTF8);
                    if (res.Turn2Text.Length > 0) await File.WriteAllTextAsync(Path.Combine(outDir, stem + "_turn2.txt"), res.Turn2Text, Encoding.UTF8);
                    await File.WriteAllTextAsync(Path.Combine(outDir, stem + "_dialogue.txt"), string.Join(Environment.NewLine + Environment.NewLine, res.Events.Where(e => e.Kind != "stream").Select(e => $"[{e.Kind} {e.Turn}] {e.Text}")), Encoding.UTF8);
                    var lift = res.Lift!;
                    run.ModelUsed = res.ModelUsed;
                    run.ImagesDelivered = res.ImagesDelivered;
                    run.Turns = res.Calls.Count;
                    run.ChosenTurn = res.ChosenTurn;
                    run.TokensIn = res.InputTokens;
                    run.TokensOut = res.OutputTokens;
                    run.ModelSeconds = Math.Round(res.ModelSeconds, 1);
                    run.TotalSeconds = Math.Round(res.TotalMs / 1000, 1);
                    run.Floors = lift.Model.Floors;
                    run.Morphology = PhotoArchitectureDescriptor.MorphologyCode(lift.Model.Morphology);
                    run.K = Math.Round(lift.MaxUtilization, 3);
                    run.Audit = $"{lift.Audit?.Criteria.Count(c => c.Passed)}/{lift.Audit?.Criteria.Count}";
                    run.SumF = Math.Round(lift.Audit?.AppliedLoad ?? 0, 1);
                    run.SumR = Math.Round(lift.Audit?.GroundReaction ?? 0, 1);
                    run.Rebuilt = (lift.Fidelity?.Rebuilt ?? false) || res.CoreFrame;
                    run.CoreFrame = res.CoreFrame;
                    run.FidelityBefore = Math.Round(lift.Fidelity?.ScoreBefore ?? 0, 2);
                    run.FidelityAfter = Math.Round(lift.Fidelity?.ScoreAfter ?? 0, 2);
                    run.Reading1 = res.Reading1?.Describe();
                    run.Reading2 = res.Reading2?.Describe();
                    run.Critique1 = res.Critique1?.Summary();
                    run.Critique2 = res.Critique2?.Summary();
                    run.Remarks1 = res.Critique1?.Items.Select(i => i.Text).ToList() ?? [];
                    run.Remarks2 = res.Critique2?.Items.Select(i => i.Text).ToList() ?? [];
                    run.Reconcile = res.Target?.ReconcileNotes.ToList() ?? [];
                    run.Error = res.Error;
                    if (truth is not null)
                    {
                        var checks = HardPhotoManifest.Evaluate(lift, truth);
                        run.Checks = checks.Select(c => $"{(c.Passed ? "OK" : "НЕТ")} {c.Name}: {c.Detail}").ToList();
                        run.ChecksPassed = checks.Count(c => c.Passed);
                        run.ChecksTotal = checks.Count;
                    }
                    var m = lift.Model.Morphology;
                    var px = await Task.Run(() => ElementPreviewRenderer.Render(lift.Model.Elements, 900, 600, LiveAzimuth(m), LiveElevation(m), false, 2, shadows: true));
                    run.Render = stem + ".png";
                    await File.WriteAllBytesAsync(Path.Combine(outDir, run.Render), await PhotoImageLoader.EncodeAsync(px, 900, 600, png: true));
                    lock (log)
                    {
                        log.AppendLine($"  итог: {run.Morphology}, этажей {run.Floors}, K {run.K:0.00}, аудит {run.Audit}, ΣF {run.SumF:N0} = ΣR {run.SumR:N0}, пересборка {run.Rebuilt}, форма {run.FidelityBefore:0.00} → {run.FidelityAfter:0.00}, " +
                                      $"точность {run.ChecksPassed}/{run.ChecksTotal}, токенов {run.TokensIn:N0} → {run.TokensOut:N0}, модель {run.ModelSeconds:0.0} с, всего {run.TotalSeconds:0.0} с");
                        foreach (var c in run.Checks) log.AppendLine("    " + c);
                    }
                }
                catch (Exception ex)
                {
                    run.Error = ex.Message;
                    lock (log) log.AppendLine("  СБОЙ: " + (client?.Redact(ex.ToString()) ?? ex.ToString()));
                }
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(runs, LiveJson), Encoding.UTF8);
                string logText;
                lock (log) logText = log.ToString();
                await File.AppendAllTextAsync(Path.Combine(outDir, "live_log.txt"), string.Empty);
                await File.WriteAllTextAsync(Path.Combine(outDir, "live_log_" + DateTime.Now.ToString("yyyyMMdd") + ".txt"), logText, Encoding.UTF8);
            }
        }
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_LIVESHOWCASE") == "1") await ShowLiveShowcaseAsync(outDir, dir);
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0);
    }

    /// <summary>Подпись модели прогона: запрошенная, а при откате шлюза — «запрошенная → ответившая».</summary>
    private static string ChipTitle(LiveRun run)
    {
        string asked = UnifiedCloudAiClient.SpecOf(run.Model)?.Title ?? run.Model;
        string used = run.ModelUsed is { Length: > 0 } u ? UnifiedCloudAiClient.SpecOf(u)?.Title ?? u : asked;
        return used == asked ? asked : asked + " → " + used;
    }

    /// <summary>Витрина живого прогона: столбец на снимок — снимок, модель Sonnet 5.5, модель GPT 5.6 Luna, сводки.</summary>
    private async Task ShowLiveShowcaseAsync(string outDir, string? photoDir = null)
    {
        var runs = JsonSerializer.Deserialize<List<LiveRun>>(await File.ReadAllTextAsync(Path.Combine(outDir, "live_report.json"))) ?? [];
        photoDir ??= Environment.GetEnvironmentVariable("AIA_DEBUG_LIVEPHOTOS") ?? HardPhotoManifest.Folder;
        var photos = runs.Select(r => r.Photo).Distinct().OrderBy(p => p).ToList();
        var models = runs.Select(r => r.Model).Distinct().ToList();
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 6 };
        foreach (var _ in photos) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = 2 + models.Count * 2;
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        for (int i = 0; i < photos.Count; i++)
        {
            void Put(UIElement e, int row) { Grid.SetColumn((FrameworkElement)e, i); Grid.SetRow((FrameworkElement)e, row); grid.Children.Add(e); }
            var first = runs.First(r => r.Photo == photos[i]);
            Put(Text($"{i + 1}. {first.Title}", 11.5, strong: true, wrap: true), 0);
            string path = Path.Combine(photoDir, photos[i]);
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                _ = LoadBitmapAsync(bmp, await File.ReadAllBytesAsync(path));
                Put(new Border { CornerRadius = new CornerRadius(8), Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill, Height = 150 } }, 1);
            }
            for (int m = 0; m < models.Count; m++)
            {
                var run = runs.FirstOrDefault(r => r.Photo == photos[i] && r.Model == models[m]);
                if (run is null) continue;
                var cell = new Grid();
                if (File.Exists(Path.Combine(outDir, run.Render)))
                {
                    var bmp = new BitmapImage();
                    _ = LoadBitmapAsync(bmp, await File.ReadAllBytesAsync(Path.Combine(outDir, run.Render)));
                    cell.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = Hex("#F2F2F7"), Child = new Image { Source = bmp, Stretch = Stretch.Uniform, Height = 150 } });
                }
                var chip = new Border { Background = Hex(m == 0 ? "#D9A06A" : "#5E9C88"), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = ChipTitle(run), FontSize = 10.5, Foreground = Hex("#FFFFFF") } };
                cell.Children.Add(chip);
                Put(cell, 2 + m * 2);
                var caption = new StackPanel { Spacing = 1 };
                bool sound = run.K <= 1.0 && run.Audit.StartsWith("5/5", StringComparison.Ordinal);
                var k = Text(string.Create(ru, $"Этажей {run.Floors} · K_исп {run.K:0.00} · аудит {run.Audit} · точность {run.ChecksPassed}/{run.ChecksTotal}"), 11, strong: true, wrap: true);
                k.Foreground = Hex(sound ? "#30D158" : "#FF9F0A");
                caption.Children.Add(k);
                string rebuilt = run.Rebuilt ? " · форма по паспорту" : "", vision = run.ImagesDelivered ? "" : " · снимок не принят шлюзом";
                caption.Children.Add(Text(string.Create(ru, $"{run.Morphology} · ход {run.ChosenTurn} из {run.Turns}{rebuilt} · токенов {run.TokensIn:N0} → {run.TokensOut:N0} · {run.ModelSeconds:0} с{vision}"),
                    10, secondary: true, wrap: true));
                Put(caption, 3 + m * 2);
            }
        }
        var body = new StackPanel { Spacing = 10, Width = 1560 };
        body.Children.Add(Text(L.Get("W41ShowcaseHint"), 11, secondary: true, wrap: true));
        body.Children.Add(grid);
        var dialog = new ContentDialog
        {
            Title = L.Get("W41Showcase"), Content = new ScrollViewer { Content = body, MaxHeight = 980 }, CloseButtonText = L.Get("W41Close"), XamlRoot = Content.XamlRoot,
            RequestedTheme = DialogTheme, Background = Res("IslandBrush"), CornerRadius = new CornerRadius(14)
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1640.0;
        dialog.Resources["ContentDialogMaxHeight"] = 1100.0;
        await dialog.ShowAsync();
    }
}
#endif
