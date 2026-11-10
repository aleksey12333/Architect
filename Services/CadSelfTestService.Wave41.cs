#if DEBUG
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Photo;
using Microsoft.Extensions.DependencyInjection;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест волны 4.1 (офлайн, без ключа и сети — облачный шлюз подменён заглушкой HTTP): фильтр уличных помех и ненадёжного
/// силуэта на сложных снимках DemoKit/HardPhotos, этажность с поправкой на перспективу на синтетическом фасаде с известным
/// числом этажей, перекрёстная сверка паспорта с чтением снимка моделью, единый клиент шлюза (разведка идентификаторов, отказ по
/// снимку → матрица, откат Gemini → Luna, временный сбой → повтор, ключ 401 без откатов, ключ не в журнале), двухходовой протокол
/// «ответ → проверка ядром → доработка» до K_исп ≤ 1, новые формы синтеза (кольцо с двором и куполом, волна фасада, мансарда),
/// чистота строк.
/// </summary>
public static partial class CadSelfTestService
{
    private static readonly CultureInfo W41Ru = CultureInfo.GetCultureInfo("ru-RU");
    private const string W41Key = "sk-test-0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Только проверки волны 4.1 (AIA_DEBUG_WAVE41TEST=папка) — быстрый цикл калибровки; синтетические фасады — в папку.</summary>
    public static async Task<string> RunWave41OnlyAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        W41Dir = directory;
        var report = new StringBuilder();
        int failures = await CheckWave41Async(report);
        report.Insert(0, failures == 0 ? "ИТОГ: УСПЕХ\n" : $"ИТОГ: ОШИБОК {failures}\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "wave41.txt"), report.ToString(), new UTF8Encoding(true));
        return report.ToString();
    }

    private static string? W41Dir;

    private static async Task<int> CheckWave41Async(StringBuilder report)
    {
        int failures = 0;
        failures += await CheckHardPhotosCvAsync(report);
        failures += await CheckPerspectiveFloorsAsync(report);
        failures += CheckReconciler(report);
        failures += await CheckUnifiedClientAsync(report);
        failures += await CheckLiveDialogueProtocolAsync(report);
        failures += CheckWave41Shapes(report);
        failures += CheckWave41Strings(report);
        return failures;
    }

    // ═════════════════════════════ Сложные снимки ═════════════════════════════

    private static async Task<int> CheckHardPhotosCvAsync(StringBuilder report)
    {
        int failures = 0;
        var truths = HardPhotoManifest.Load();
        var lines = new List<string>();
        bool ok = truths.Count == 5;
        var results = new Dictionary<string, PhotoArchitectureDescriptor>();
        foreach (var t in truths)
        {
            string path = Path.Combine(HardPhotoManifest.Folder, t.File);
            if (!File.Exists(path)) { ok = false; lines.Add(t.File + " нет"); continue; }
            var (d, _, _) = await PhotoToBimService.AnalyzeAsync(await PhotoImageLoader.FromFileAsync(path));
            results[t.File] = d;
            lines.Add(string.Create(W41Ru, $"{t.File[..6]}: {PhotoArchitectureDescriptor.MorphologyCode(d.Morphology)}, эт. {d.Floors} (без перспективы {d.FloorsUncorrected}), силуэт {d.SegmentationQuality:0.00}, кроны {d.ClutterCells.Count} кл."));
        }
        if (ok)
        {
            var ring = results[truths[0].File];
            var arena = results[truths[1].File];
            var cascade = results[truths[2].File];
            var tower = results[truths[4].File];
            // Деревья перед фасадом найдены и отфильтрованы (палитра без зелени), аэрофото — силуэт помечен ненадёжным.
            bool vegetation = new[] { ring, cascade, tower }.All(d => d.ClutterCells.Count >= 20);
            bool notGreen = new[] { cascade, tower }.All(d => d.Palette.Where(p => p.Role is "wall" or "glass").All(p =>
            {
                CurvedDesign.TryParseHex(p.Hex, out var c);
                var (hue, sat, _) = PhotoArchitectureAnalyzer.Hsv((c.R, c.G, c.B));
                return !(hue is > 70 and < 160 && sat > 0.3);
            }));
            bool aerialFlagged = ring.SegmentationQuality < 0.55;
            bool arenaOk = arena.Morphology == PhotoMorphology.ArchSpan;
            bool towerOk = tower.Morphology == PhotoMorphology.Curved && tower.FacadeArcs >= 2 && tower.Floors >= 18 && tower.Floors <= 23;
            ok = vegetation && notGreen && aerialFlagged && arenaOk && towerOk;
            lines.Add($"кроны {vegetation}, палитра без зелени {notGreen}, аэрофото помечено {aerialFlagged}, арена {arenaOk}, волна башни {towerOk}");
        }
        Check(report, ref failures, ok, "сложные снимки DemoKit/HardPhotos — Визуальное ядро: " + string.Join("; ", lines));
        return failures;
    }

    /// <summary>
    /// Синтетический фасад N этажей, снятый снизу вверх (вертикали сходятся в V_y над кадром), с деревьями у основания и облачным
    /// небом: Визуальное ядро должно найти точку схода и насчитать N ± 1 этажей, отфильтровать кроны.
    /// </summary>
    private static async Task<int> CheckPerspectiveFloorsAsync(StringBuilder report)
    {
        int failures = 0;
        var lines = new List<string>();
        bool ok = true;
        foreach (var (floors, vyFrac) in new[] { (16, -1.4), (24, -2.2), (10, -1.1) })
        {
            var img = SyntheticTiltedFacade(floors, vyFrac, 1280, 800, seed: floors);
            var d = PhotoArchitectureAnalyzer.Analyze(img, $"synthetic_{floors}.png");
            if (W41Dir is not null)
            {
                string trace = PhotoArchitectureAnalyzer.LastTrace.Replace(Environment.NewLine, " / ");
                await File.WriteAllBytesAsync(Path.Combine(W41Dir, $"synthetic_{floors}.png"), await PhotoImageLoader.EncodeAsync(img.Bgra, img.Width, img.Height, png: true));
                lines.Add("трасса: " + trace);
            }
            bool hit = Math.Abs(d.Floors - floors) <= 1 && d.VanishingY is < 0;
            ok &= hit;
            lines.Add(string.Create(W41Ru, $"{floors} эт. (V_y {vyFrac:0.0} H): найдено {d.Floors}, без поправки {d.FloorsUncorrected}, V_y {d.VanishingY:0.00}, кроны {d.ClutterCells.Count} кл."));
        }
        Check(report, ref failures, ok, "этажность с поправкой на перспективу (синтетический фасад снизу вверх с кронами и облаками): " + string.Join("; ", lines));
        return failures;
    }

    /// <summary>Фасад-стеклянная сетка в проекции с наклоном камеры вверх: u = 1/(y − V_y) линейно по высоте, кроны у основания.</summary>
    private static RasterPlanTracer.RasterImage SyntheticTiltedFacade(int floors, double vyFrac, int w, int h, int seed)
    {
        var px = new byte[w * h * 4];
        double vy = vyFrac * h, cx = w / 2.0;
        double yBase = h * 0.93, yTop = h * 0.08;
        double ub = 1 / (yBase - vy), ut = 1 / (yTop - vy);
        double halfBase = w * 0.28;
        var rnd = new Random(seed);
        // Кроны: круги у основания.
        var trees = Enumerable.Range(0, 9).Select(i => (X: w * (0.12 + 0.09 * i) + rnd.Next(-20, 20), Y: yBase - rnd.Next(30, 90), R: rnd.Next(45, 80))).ToList();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                // Небо: пасмурный градиент с облачными пятнами.
                double cloud = 0.5 + 0.5 * Math.Sin(x * 0.013 + Math.Sin(y * 0.02) * 2) * Math.Cos(y * 0.017);
                double r = 120 + 30 * cloud, g = 126 + 30 * cloud, b = 138 + 30 * cloud;
                double t = (y - vy) / (yBase - vy);
                double half = halfBase * t;
                if (y >= yBase) { r = 70; g = 72; b = 76; }
                else if (y >= yTop && Math.Abs(x - cx) <= half)
                {
                    double u = 1 / (y - vy);
                    double z = (u - ub) / (ut - ub) * floors;             // этажи 0…floors снизу вверх
                    double xs = (x - cx) / Math.Max(1, half) * 14;          // 14 пролётов по ширине
                    bool band = z - Math.Floor(z) < 0.16;
                    bool mullion = Math.Abs(xs - Math.Round(xs)) < 0.06;
                    if (band) { r = 52; g = 60; b = 68; }
                    else if (mullion) { r = 40; g = 46; b = 52; }
                    else { double refl = 0.6 + 0.4 * Math.Sin(xs * 0.7 + z * 0.3); r = 70 * refl + 40; g = 125 * refl + 40; b = 140 * refl + 45; }
                }
                foreach (var tr in trees)
                {
                    double dx = x - tr.X, dy = y - tr.Y;
                    if (dx * dx + dy * dy > tr.R * tr.R) continue;
                    double n = (Math.Sin(x * 0.9) + Math.Cos(y * 1.1) + Math.Sin((x + y) * 0.37)) / 3;
                    r = 50 + 25 * n; g = 105 + 40 * n; b = 38 + 15 * n;
                }
                px[o] = (byte)Math.Clamp(b, 0, 255); px[o + 1] = (byte)Math.Clamp(g, 0, 255); px[o + 2] = (byte)Math.Clamp(r, 0, 255); px[o + 3] = 255;
            }
        return new RasterPlanTracer.RasterImage(px, w, h);
    }

    // ═════════════════════════════ Перекрёстная сверка ═════════════════════════════

    private static int CheckReconciler(StringBuilder report)
    {
        int failures = 0;
        var cv = new PhotoArchitectureDescriptor { SourceName = "test", Morphology = PhotoMorphology.Box, Confidence = 0.55, Floors = 26, FloorHeight = 3.3, HeightM = 86, WidthM = 60, DepthM = 30, FloorConfidence = 0.15, SegmentationQuality = 0.4 };
        // «box», но три дуги и вогнутый центр — изогнутый фасад; этажность 26 против 13 — кратная ошибка периода ядра.
        var r1 = PhotoReading.Parse("{\"photoReading\":{\"morphology\":\"box\",\"floors\":13,\"podiumFloors\":2,\"facadeArcs\":3,\"arcBulgeRatio\":-0.15,\"confidence\":0.8},\"elements\":[]}");
        var m1 = PhotoReadingReconciler.Merge(cv, r1, sawImage: true);
        bool a = m1.Morphology == PhotoMorphology.Curved && m1.FacadeArcs == 3 && m1.ArcBulgeRatio < 0 && m1.Floors == 13 && m1.PodiumFloors == 2 &&
                 m1.ReconcileNotes.Any(n => n.Contains("кратная ошибка периода"));
        // Модель без снимка (шлюз не принял) паспорт не меняет.
        var m2 = PhotoReadingReconciler.Merge(cv, r1, sawImage: false);
        bool b = m2.Morphology == PhotoMorphology.Box && m2.Floors == 26;
        // Кольцо с двором и мансарда «утюга» из чтения.
        var ring = PhotoReadingReconciler.Merge(cv, PhotoReading.Parse("{\"photoReading\":{\"morphology\":\"ring_atrium\",\"floors\":6,\"colonnadeColumns\":12,\"roof\":\"dome\"}}"), true);
        var flat = PhotoReadingReconciler.Merge(cv, PhotoReading.Parse("{\"scene\":{\"photoReading\":{\"morphology\":\"triangular\",\"floors\":\"12\",\"cornerAngleDeg\":\"62°\",\"roof\":\"mansard\"}}}"), true);
        bool c = ring.Morphology == PhotoMorphology.Rotunda && ring.HasCourtyard && ring.ColonnadeColumns == 12 && ring.Floors == 6 &&
                 flat.Morphology == PhotoMorphology.Triangular && flat.Roof == PhotoRoofKind.Mansard && Math.Abs(flat.CornerAngleDeg - 62) < 1e-9 && flat.Floors == 12;
        Check(report, ref failures, a && b && c,
            $"перекрёстная сверка паспорта с чтением снимка моделью: «box» + 3 дуги → {PhotoArchitectureDescriptor.MorphologyCode(m1.Morphology)} (дуг {m1.FacadeArcs}, центр {m1.ArcBulgeRatio:+0.00;−0.00}), " +
            $"этажность ядра 26 против 13 модели → {m1.Floors} (кратная ошибка периода); без снимка — паспорт без изменений ({b}); кольцо с двором ({ring.HasCourtyard}, колонн {ring.ColonnadeColumns}), " +
            $"«утюг» с мансардой ({flat.Roof}, угол {flat.CornerAngleDeg:0}°)");
        return failures;
    }

    // ═════════════════════════════ Единый клиент шлюза ═════════════════════════════

    /// <summary>Заглушка облачного шлюза: /v1/models и /v1/chat/completions по правилам сценария; тела запросов сохраняются.</summary>
    private sealed class GatewayStub(Func<string, JsonElement, int, (HttpStatusCode Status, string Body)> chat) : HttpMessageHandler
    {
        public List<(string Model, bool Image, int Messages, string Body)> Requests { get; } = [];
        public List<string> Authorization { get; } = [];
        private int _n;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v1/models", StringComparison.Ordinal))
            {
                string ids = string.Join(",", new[] { "gpt-6.1-terra", "gpt-5.6-luna", "claude-opus-5-5", "claude-sonnet-5-5", "claude-sonnet-5", "gemini-3.1-pro", "gemini-3.8-flash", "glm-5.3" }
                    .Select(i => $"{{\"id\":\"{i}\",\"object\":\"model\"}}"));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"data\":[{ids}],\"object\":\"list\"}}", Encoding.UTF8, "application/json") };
            }
            if (!path.EndsWith("/v1/chat/completions", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            string body = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string model = root.GetProperty("model").GetString()!;
            bool image = body.Contains("image_url", StringComparison.Ordinal);
            int messages = root.GetProperty("messages").GetArrayLength();
            // Тексты реплик раскодированы (сериализатор экранирует кириллицу как \uXXXX).
            var texts = new StringBuilder();
            foreach (var m in root.GetProperty("messages").EnumerateArray())
            {
                var c = m.GetProperty("content");
                if (c.ValueKind == JsonValueKind.String) texts.AppendLine(c.GetString());
                else foreach (var part in c.EnumerateArray()) if (part.TryGetProperty("text", out var t)) texts.AppendLine(t.GetString());
            }
            Requests.Add((model, image, messages, texts.ToString()));
            var (status, text) = chat(model, root, Interlocked.Increment(ref _n));
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static string ChatReply(string content, int prompt = 1200, int completion = 1) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } }, usage = new { prompt_tokens = prompt, completion_tokens = completion } });

    private const string GatewayError = "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"code\":\"api_error\",\"message\":\"The API gateway could not complete this request.\"}}";

    private static async Task<int> CheckUnifiedClientAsync(StringBuilder report)
    {
        int failures = 0;
        var config = new UnifiedGatewayConfig("https://gateway.example/api/llm", W41Key);
        var image = new AiImage([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4], "image/jpeg");
        int flaky = 0;
        var stub = new GatewayStub((model, root, n) => model switch
        {
            // Sonnet: шлюз отклоняет изображения (как на живом шлюзе), текст — отвечает.
            "claude-sonnet-5-5" when root.GetRawText().Contains("image_url") => (HttpStatusCode.BadGateway, GatewayError),
            "claude-sonnet-5-5" => (HttpStatusCode.OK, ChatReply("{\"message\":\"ok\",\"elements\":[]} — длинный ответ для оценки выходных токенов по длине")),
            "gemini-3.1-pro" => (HttpStatusCode.ServiceUnavailable, GatewayError),
            "gpt-5.6-luna" when root.GetRawText().Contains("flaky") && Interlocked.Increment(ref flaky) == 1 => (HttpStatusCode.GatewayTimeout, GatewayError),
            "gpt-5.6-luna" => (HttpStatusCode.OK, ChatReply("{\"message\":\"luna\"}", 900, 340)),
            _ => (HttpStatusCode.NotFound, GatewayError)
        });
        using var client = new UnifiedCloudAiClient(config, stub);
        var models = await client.DiscoverAsync();
        bool discovery = models.Count == 6 && models[0].Id == "claude-sonnet-5-5" && models[1].Id == "gpt-5.6-luna" && client.Resolve("Sonnet 5-5") == "claude-sonnet-5-5" &&
                         client.Resolve("gpt-5.6-luna") == "gpt-5.6-luna" && models.All(m => m.Option.Provider == AiProviderKind.UnifiedGateway);
        UnifiedChatRequest Req(string model, bool img, string text = "задача") => new() { Model = model, System = "s", Messages = [UnifiedMessage.User(text, img ? [image] : null)], Purpose = "тест" };
        // 1. Sonnet со снимком → 502 → та же модель с матрицей без снимка; второй запрос — сразу без снимка.
        var s1 = await client.ChatAsync(Req("sonnet-5.5", true));
        int before = stub.Requests.Count;
        var s2 = await client.ChatAsync(Req("sonnet-5.5", true));
        bool vision = s1.ModelUsed == "claude-sonnet-5-5" && s1.ImagesDropped && !s1.FellBack && client.IsVisionBlocked("claude-sonnet-5-5") &&
                      stub.Requests.Count - before == 1 && !stub.Requests[^1].Image && s1.OutputEstimated && s1.OutputTokens > 4;
        // 2. Gemini 3.1 Pro недоступен → бесшовно GPT 5.6 Luna (со снимком).
        var g = await client.ChatAsync(Req("Gemini 3.1 Pro", true));
        bool fallback = g.ModelRequested == "gemini-3.1-pro" && g.ModelUsed == "gpt-5.6-luna" && g.FellBack && g.ImagesDelivered && g.Text.Contains("luna");
        // 3. Временный сбой 504 → повтор той же попытки со снимком (снимок не снимается, Luna не помечается).
        var f = await client.ChatAsync(Req("gpt-5.6-luna", true, "flaky"));
        bool transient = f.ModelUsed == "gpt-5.6-luna" && f.ImagesDelivered && f.Attempts.Count == 2 && f.Attempts[0].Status == 504 && !client.IsVisionBlocked("gpt-5.6-luna");
        // 4. Ключ отклонён (401) — без откатов, понятная ошибка.
        var denied = new GatewayStub((_, _, _) => (HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"invalid key " + W41Key + "\"}}"));
        using var deniedClient = new UnifiedCloudAiClient(config, denied);
        string code = string.Empty, message = string.Empty;
        try { await deniedClient.ChatAsync(Req("gpt-5.6-luna", false)); }
        catch (AiProviderException ex) { code = ex.Code; message = ex.Message + ex.Details; }
        bool keyInvalid = code == "AIA-KEY-INVALID" && denied.Requests.Count == 1 && !message.Contains(W41Key);
        // 5. Ключ — только в заголовке Authorization, не в журнале и не в описании конфигурации; без переменных окружения — офлайн.
        string journal = string.Join("\n", client.Calls.Select(UnifiedCloudAiClient.Describe)) + config;
        bool secret = !journal.Contains(W41Key) && stub.Authorization.All(a => a == "Bearer " + W41Key) && stub.Requests.All(r => !r.Body.Contains(W41Key)) &&
                      (Environment.GetEnvironmentVariable(UnifiedGatewayConfig.KeyVariable) is { Length: > 0 } || UnifiedGatewayConfig.FromEnvironment() is null);
        // 6. Реестр: модели шлюза в меню первыми среди облачных, settings.json не меняется.
        var settings = App.Services.GetRequiredService<SettingsService>();
        string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIArchitectStudio", "settings.json");
        string? settingsBefore = File.Exists(settingsPath) ? await File.ReadAllTextAsync(settingsPath) : null;
        var registry = new AiModelRegistry(settings);
        registry.SetUnifiedModels(models.Select(m => m.Option).ToList());
        var all = registry.All;
        string? settingsAfter = File.Exists(settingsPath) ? await File.ReadAllTextAsync(settingsPath) : null;
        bool menu = all.First(o => o.IsCloud).Id == "unified:claude-sonnet-5-5" && all.Count(o => o.Provider == AiProviderKind.UnifiedGateway) == 6 && settingsBefore == settingsAfter &&
                    AiModelCatalog.FromId("unified:gpt-5.6-luna") is { Provider: AiProviderKind.UnifiedGateway, ModelId: "gpt-5.6-luna" };
        Check(report, ref failures, discovery && vision && fallback && transient && keyInvalid && secret && menu,
            $"единый клиент облачного шлюза: разведка /v1/models → {string.Join(", ", models.Select(m => m.Spec.Title + " = " + m.Id))} [{discovery}]; Sonnet со снимком → 502 → матрица без снимка, " +
            $"повторно — сразу без снимка [{vision}]; Gemini 3.1 Pro недоступен → {g.ModelUsed} [{fallback}]; 504 → повтор со снимком [{transient}]; 401 — без откатов [{keyInvalid}]; " +
            $"ключ только в Authorization, не в журнале [{secret}]; меню: Sonnet 5.5 первым, settings.json не меняется [{menu}]");
        return failures;
    }

    // ═════════════════════════════ Двухходовой протокол ═════════════════════════════

    private static async Task<int> CheckLiveDialogueProtocolAsync(StringBuilder report)
    {
        int failures = 0;
        var truth = HardPhotoManifest.Load().FirstOrDefault(t => t.File.StartsWith("Hard_5", StringComparison.Ordinal));
        string path = truth is null ? string.Empty : Path.Combine(HardPhotoManifest.Folder, truth.File);
        if (truth is null || !File.Exists(path))
        {
            Check(report, ref failures, false, "двухходовой протокол: нет снимка DemoKit/HardPhotos/Hard_5");
            return failures;
        }
        var (cv, _, jpeg) = await PhotoToBimService.AnalyzeAsync(await PhotoImageLoader.FromFileAsync(path));
        // Ход 1 — «ленивая коробка» с честным photoReading; ход 2 — фасад из трёх дуг разного знака на стилобате.
        string turn1 = "```json\n{\"photoReading\":{\"morphology\":\"box\",\"floors\":20,\"podiumFloors\":2,\"facadeArcs\":3,\"arcBulgeRatio\":-0.15,\"confidence\":0.85}," +
                       LazyBoxJson[1..] + "\n```";
        string turn2 = "{\"photoReading\":{\"morphology\":\"curved\",\"floors\":20,\"podiumFloors\":2,\"facadeArcs\":3,\"arcBulgeRatio\":-0.15,\"confidence\":0.9},\"message\":\"Волна\",\"elements\":[" +
                       "{\"type\":\"foundation\",\"position\":[0,-0.45,0],\"size\":[60,0.9,40]}," +
                       "{\"type\":\"curtain_wall\",\"start\":[-24,-10],\"end\":[-8,-10],\"height\":3.6,\"arcBulge\":2.4,\"repeatLevels\":20,\"levelHeight\":3.6}," +
                       "{\"type\":\"curtain_wall\",\"start\":[-8,-10],\"end\":[8,-10],\"height\":3.6,\"arcBulge\":-2.4,\"repeatLevels\":20,\"levelHeight\":3.6}," +
                       "{\"type\":\"curtain_wall\",\"start\":[8,-10],\"end\":[24,-10],\"height\":3.6,\"arcBulge\":2.4,\"repeatLevels\":20,\"levelHeight\":3.6}," +
                       "{\"type\":\"slab\",\"position\":[0,3.6,0],\"size\":[48,0.25,20],\"repeatLevels\":20,\"levelHeight\":3.6}]}";
        var stub = new GatewayStub((model, root, n) => (HttpStatusCode.OK, ChatReply(root.GetProperty("messages").GetArrayLength() <= 2 ? turn1 : turn2, 6400, 3900)));
        using var client = new UnifiedCloudAiClient(new UnifiedGatewayConfig("https://gateway.example/api/llm/v1", W41Key), stub);
        var ai = App.Services.GetRequiredService<AIOrchestrator>();
        var events = new List<PhotoDialogueEvent>();
        var result = await UniversalVisionModelBridge.RunDialogueAsync(client, ai, UnifiedCloudAiClient.Luna56.Alias, cv, jpeg, null, new StructuralLoadSettings(),
            new SyncProgress<PhotoDialogueEvent>(events.Add), fast: false);
        var chat = stub.Requests.Where(r => r.Model == "gpt-5.6-luna").ToList();
        bool protocol = chat.Count == 2 && chat[0].Image && chat[0].Messages == 2 && chat[1].Messages == 4 && chat[0].Body.Contains("photoReading") &&
                        chat[0].Body.Contains("PhotoArchitectureDescriptor") && chat[1].Body.Contains("Проверка твоего ответа расчётным и геометрическим ядром") &&
                        (chat[1].Body.Contains("чередующихся дуг") || chat[1].Body.Contains("arcBulge"));
        var lift = result.Lift!;
        var checks = HardPhotoManifest.Evaluate(lift, truth);
        bool final = result.Critique1 is { NeedsRevision: true } && result.Target?.Morphology == PhotoMorphology.Curved && lift.IsStructurallySound && (lift.Audit?.Passed ?? false) &&
                     PhotoFidelityEnforcer.Traits(lift.Model.Elements).AlternatingArcs >= 2 && events.Any(e => e.Kind == "review" && e.Turn == 2) && result.InputTokens == 12800;
        Check(report, ref failures, protocol && final,
            string.Create(W41Ru, $"двухходовой протокол «ответ → проверка ядром → доработка» (заглушка шлюза): ход 1 — снимок + паспорт + JSON v2, ход 2 — та же беседа (сообщений {chat.ElementAtOrDefault(1).Messages}) " +
                                 $"с замечаниями ядра ({result.Critique1?.Items.Count} шт.: {string.Join(" | ", result.Critique1?.Items.Take(2).Select(i => AiHttp.Truncate(i.Text, 70)) ?? [])}); " +
                                 $"итог хода {result.ChosenTurn}{(result.CoreFrame ? " (каркас ядра)" : "")}: {PhotoToBimService.Summary(lift)}; точность по эталону {checks.Count(c => c.Passed)}/{checks.Count}"));
        return failures;
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    // ═════════════════════════════ Новые формы синтеза ═════════════════════════════

    private static int CheckWave41Shapes(StringBuilder report)
    {
        int failures = 0;
        var loads = new StructuralLoadSettings();
        var ringD = new PhotoArchitectureDescriptor { SourceName = "ring", Morphology = PhotoMorphology.Rotunda, HasCourtyard = true, Floors = 6, FloorHeight = 4, WidthM = 86, DepthM = 86, ColonnadeColumns = 10, Roof = PhotoRoofKind.Dome };
        var waveD = new PhotoArchitectureDescriptor { SourceName = "wave", Morphology = PhotoMorphology.Curved, Floors = 20, FloorHeight = 3.6, WidthM = 52, DepthM = 26, FacadeArcs = 3, ArcBulgeRatio = -0.15, PodiumFloors = 2, CornerRadiusRatio = 0.18 };
        var flatD = new PhotoArchitectureDescriptor { SourceName = "flatiron", Morphology = PhotoMorphology.Triangular, Floors = 12, FloorHeight = 3.4, WidthM = 46, DepthM = 46, CornerAngleDeg = 58, CornerRadiusRatio = 0.12, Roof = PhotoRoofKind.Mansard };
        var lines = new List<string>();
        bool ok = true;
        foreach (var (d, name) in new[] { (ringD, "кольцо"), (waveD, "волна"), (flatD, "утюг") })
        {
            var lift = PhotoToBimService.Lift(d, new PhotoLiftOptions { GroupName = name }, loads, verify: true, monotonicity: false);
            var t = PhotoFidelityEnforcer.Traits(lift.Model.Elements);
            bool shape = name switch
            {
                "кольцо" => t.HasRingSlab && t.HasDome && t.RoundColumns >= 10 && lift.Model.Floors >= 6,
                "волна" => t.AlternatingArcs >= 3 && t.HasPodium && lift.Model.Floors == 20,
                _ => t.HasMansard && t.MinPlanAngle < 75 && t.NonOrthogonalWalls >= 2
            };
            bool sound = lift.IsStructurallySound && (lift.Audit?.Passed ?? false);
            ok &= shape && sound;
            if (!sound) lines.Add(name + " перегрузка: " + string.Join(" | ", lift.Log.Where(l => l.StartsWith("Перегрузка", StringComparison.Ordinal) || l.StartsWith("Без опоры", StringComparison.Ordinal)).Take(4)));
            lines.Add(string.Create(W41Ru, $"{name}: {t.Describe()}; K_исп {lift.MaxUtilization:0.00}, аудит {lift.Audit?.Criteria.Count(c => c.Passed)}/{lift.Audit?.Criteria.Count}, висящих {lift.Hanging} [{shape && sound}]"));
        }
        Check(report, ref failures, ok, "новые формы синтеза — " + string.Join("; ", lines));
        return failures;
    }

    private static int CheckWave41Strings(StringBuilder report)
    {
        int failures = 0;
        var texts = new List<string>();
        if (typeof(LocalizationService).GetField("Wave41Strings", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is Dictionary<string, (string Ru, string En)> strings)
            texts.AddRange(strings.Values.SelectMany(v => new[] { v.Ru, v.En }));
        int keys = texts.Count / 2;
        var banned = new Regex(@"(?<![\p{L}])(ИИ|AI|LLM|API|JSON|Ollama)(?![\p{L}])", RegexOptions.CultureInvariant);
        var dirty = texts.Where(t => banned.IsMatch(t) || t.EnumerateRunes().Any(r => r.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF)).ToList();
        bool terms = texts.Any(t => t.Contains("Облачный сервис")) && texts.Any(t => t.Contains("Ядро расчётов")) && texts.Any(t => t.Contains("Визуальным ядром"));
        Check(report, ref failures, keys >= 15 && dirty.Count == 0 && terms && AiModelCatalog.ProviderTitle(AiProviderKind.UnifiedGateway) == "Облачный сервис",
            $"строки волны 4.1: ключей {keys} (RU/EN), без эмодзи и разработческих терминов; «Облачный сервис», «Ядро расчётов»" + (dirty.Count > 0 ? " [" + string.Join(" | ", dirty.Take(3)) + "]" : string.Empty));
        return failures;
    }
}
#endif
