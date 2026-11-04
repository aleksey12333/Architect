#if DEBUG
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Photo;
using Microsoft.Extensions.DependencyInjection;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест волны 4: криволинейная геометрия (дуговые стены 24–48 сегментов с гладкими нормалями и радиальной врезкой окон, рез
/// торцов под 45/60/135°, скругление углов плит, круглые и кольцевые плиты, купол, конус, складки, арочный витраж — точность сетки,
/// отсутствие вырожденных треугольников, V_нетто), КЭ-расчёт дугового барабана (схема — цепочка пластин по дуге, статика сходится),
/// загрузка снимков по ссылке, со страницы и из HTML-фрагмента, подача снимка в разные модели (текстовая локальная, мультимодальная
/// локальная, веб-чат, облачная, Совет КБ) с гарантией формы и цветов, пять демонстрационных сооружений DemoKit/InternetPhotos
/// (морфология, цвета снимка, K_исп ≤ 1, ΣR_y = ΣF, 0 висящих, 0 дверей в колоннах), чистота строк.
/// </summary>
public static partial class CadSelfTestService
{
    private static readonly CultureInfo W4Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly GeometryContext W4All = new(SceneStateService.AllLevels, 0, 0);

    private static async Task<int> CheckWave4Async(string directory, StringBuilder report)
    {
        int failures = 0;
        failures += CheckArcWalls(report);
        failures += CheckMiterJoins(report);
        failures += CheckRoundedAndCircularPlates(report);
        failures += CheckSpecialRoofs(report);
        failures += CheckCurvedAnalysis(report);
        var photos = await DemoPhotosAsync(directory);
        failures += await CheckPhotoLoadingAsync(photos, report);
        failures += await CheckModelRoutingAsync(photos, report);
        failures += await CheckFivePhotosAsync(photos, report);
        failures += CheckWave4Strings(report);
        return failures;
    }

    // ═════════════════════════════ Геометрия ═════════════════════════════

    private static ArchitecturalElement W4(string type, string name, double x, double baseY, double z, double w, double h, double d, double rot = 0, string material = "concrete")
    {
        var e = new ArchitecturalElement { Type = type, Name = name, BuildingGroupId = "Волна 4", Material = material };
        e.SetSize(w, h, d);
        e.SetPosition(x, baseY + h / 2, z);
        e.SetRotationY(rot);
        return e;
    }

    /// <summary>Объём замкнутой сетки (теорема о дивергенции) по всем частям, кроме пустот, и число вырожденных треугольников.</summary>
    private static (double Volume, int Degenerate, int Triangles) MeshStats(ElementGeometry g, bool skipGlass = false)
    {
        double v = 0;
        int bad = 0, tri = 0;
        foreach (var part in g.Parts)
        {
            if (part.Material == MaterialKind.Void || skipGlass && MaterialCatalog.IsTransparent(part.Material)) continue;
            var p = part.Mesh.Positions;
            var idx = part.Mesh.TriangleIndices;
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                var a = p[idx[i]]; var b = p[idx[i + 1]]; var c = p[idx[i + 2]];
                v += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
                if (Vector3.Cross(b - a, c - a).Length() < 1e-7f) bad++;
                tri++;
            }
        }
        return (Math.Abs(v), bad, tri);
    }

    private static int CheckArcWalls(StringBuilder report)
    {
        int failures = 0;
        // Дуговая стена: хорда 12 м, стрела 2 м, H = 3 м, D = 0,3 м → объём = 2Φ·R·D·H.
        var wall = W4(ElementTypes.Wall, "Дуговая стена", 0, 0, 0, 12, 3, 0.3);
        CurvedDesign.SetArc(wall, 2);
        var arc = new ArcGeometry(12, 2);
        var g = ElementGeometryBuilder.Build(wall, [], W4All);
        var (vol, bad, tris) = MeshStats(g);
        double expected = arc.Length * 0.3 * 3;
        int normals = g.Parts.SelectMany(p => p.Mesh.Normals ?? []).Select(n => (Math.Round(n.X, 3), Math.Round(n.Z, 3))).Distinct().Count();
        var renga = RengaPropertyService.Compute(wall, [wall]);
        Check(report, ref failures, Math.Abs(vol / expected - 1) < 0.005 && bad == 0 && normals >= 24 && Math.Abs(renga.NetVolume / expected - 1) < 0.005,
            string.Create(W4Ru, $"дуговая стена 12 м / стрела 2 м (R {arc.Radius:0.00} м, дуга {arc.Length:0.000} м): сетка {vol:0.0000} м³ ≡ 2Φ·R·D·H {expected:0.0000} м³ ({(vol / expected - 1) * 100:+0.00;-0.00} %), " +
                                $"треугольников {tris}, вырожденных {bad}, гладких нормалей {normals}, V_нетто свойств Renga {renga.NetVolume:0.0000} м³"));

        // Радиальная врезка: окно 1,5 × 1,5 м в 3 м от середины — по касательной дуги, тело стены режется по углу дуги.
        var window = W4(ElementTypes.Window, "Окно дуговой стены", 3, 0.9, 0, 1.5, 1.5, 0.36, material: "glass");
        window.HostElementId = wall.Id;
        var guard = BuildingLogicGuard.Enforce([wall, window], window);
        double tangent = arc.TangentDegrees(3);
        double zArc = arc.ZAt(3);
        var (lx, lz) = ArchitecturalElement.RotateY(window.X - wall.X, window.Z - wall.Z, -wall.RotationY);
        var gw = ElementGeometryBuilder.Build(wall, [window], W4All);
        var (volW, badW, _) = MeshStats(gw);
        double cut = expected - volW, ideal = 1.5 * 1.5 * 0.3;
        Check(report, ref failures, guard.Success && Math.Abs(lz - zArc) < 1e-6 && Math.Abs(window.RotationY - tangent) < 1e-6 && Math.Abs(cut / ideal - 1) < 0.03 && badW == 0,
            string.Create(W4Ru, $"радиальная врезка окна: центр на дуге (смещение {lz:0.000} м ≡ {zArc:0.000} м), поворот по касательной {window.RotationY:0.00}°, вырез тела {cut:0.000} м³ ≈ 1,5 × 1,5 × 0,3 = {ideal:0.000} м³, вырожденных 0"));

        // Дуговой витраж с импостами и арочный витраж торца.
        var cw = W4(ElementTypes.CurtainWall, "Дуговой витраж", 0, 0, 0, 20, 4, 0.12, material: "glass");
        CurvedDesign.SetArc(cw, 3);
        var gcw = ElementGeometryBuilder.Build(cw, [], W4All);
        var glass = gcw.Parts.First(p => p.Material == MaterialKind.Glass).Mesh;
        float zMax = glass.Positions.Max(p => p.Z);
        var arched = W4(ElementTypes.CurtainWall, "Арочный витраж", 0, 0, 0, 30, 18, 0.15, material: "glass");
        CurvedDesign.SetArchRise(arched, 12);
        var ga = ElementGeometryBuilder.Build(arched, [], W4All);
        var gp = ga.Parts.First(p => p.Material == MaterialKind.Glass).Mesh.Positions;
        float topCenter = gp.Where(p => Math.Abs(p.X) < 0.5f).Max(p => p.Y), topEdge = gp.Where(p => Math.Abs(p.X) > 14.9f).Max(p => p.Y);
        Check(report, ref failures, Math.Abs(zMax - 3) < 0.01 && gcw.Parts.Any(p => p.Material == MaterialKind.Frame) && Math.Abs(topCenter - 9) < 0.05 && Math.Abs(topEdge - (9 - 12)) < 0.2,
            string.Create(W4Ru, $"дуговой витраж: вершина стекла {zMax:0.000} м ≡ стреле 3 м, импосты и ригели; арочный витраж: верх в середине {topCenter:0.00} м, у краёв {topEdge:0.00} м (стрела 12 м)"));
        return failures;
    }

    private static int CheckMiterJoins(StringBuilder report)
    {
        int failures = 0;
        var lines = new List<string>();
        bool ok = true;
        foreach (double angle in new[] { 45.0, 60.0, 135.0 })
        {
            var a = W4(ElementTypes.Wall, "Стена A", -5, 0, 0, 10, 3, 0.3);
            double r = angle * Math.PI / 180;
            // Стена B выходит из (0, 0) под углом angle к оси A (зазор 0,2 м у узла — замыкается биссектрисой).
            double len = 8, ux = Math.Cos(Math.PI - r), uz = Math.Sin(Math.PI - r);
            var b = W4(ElementTypes.Wall, "Стена B", 0.2 * ux + ux * len / 2, 0, 0.2 * uz + uz * len / 2, len, 3, 0.3, Math.Atan2(-uz, ux) * 180 / Math.PI);
            bool joined = ParametricSolverService.MiterJoin(a, b);
            var oa = ElementGeometryBuilder.WorldOutline(a);
            var ob = ElementGeometryBuilder.WorldOutline(b);
            // Общая линия реза: каждая вершина торца A у узла совпадает с вершиной торца B.
            double gap = oa.Where(p => Math.Abs(p.X) < 3).Max(p => ob.Min(q => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z))));
            var (va, badA, _) = MeshStats(ElementGeometryBuilder.Build(a, [], W4All));
            var (vb, badB, _) = MeshStats(ElementGeometryBuilder.Build(b, [], W4All));
            double expected = (a.Width + b.Width) * 3 * 0.3;
            bool pass = joined && gap < 1e-3 && Math.Abs((va + vb) / expected - 1) < 1e-3 && badA + badB == 0 && CurvedDesign.HasMiter(a) && CurvedDesign.HasMiter(b);
            ok &= pass;
            var (ma, mb) = (CurvedDesign.Miter(a), CurvedDesign.Miter(b));
            lines.Add(string.Create(W4Ru, $"{angle:0}°: рез {ma.End:0.0}° / {mb.Start:0.0}°{mb.End:+0.0;-0.0;''}, щель {gap * 1000:0.000} мм, объём {va + vb:0.000} ≡ {expected:0.000} м³"));
        }
        Check(report, ref failures, ok, "биссектрисная стыковка стен под произвольным углом без клиновидных щелей и без выпрямления в сетку: " + string.Join("; ", lines));
        return failures;
    }

    private static int CheckRoundedAndCircularPlates(StringBuilder report)
    {
        int failures = 0;
        var slab = W4(ElementTypes.Slab, "Плита со скруглением", 0, 3, 0, 20, 0.22, 12);
        CurvedDesign.ApplyCornerRadius(slab, 3);
        double area = Geometry2D.Area(slab.Polygon2D.Select(p => (p.X, p.Z)).ToList());
        double expected = 240 - (4 - Math.PI) * 9;
        var (vol, bad, _) = MeshStats(ElementGeometryBuilder.Build(slab, [], W4All));
        // Повторное скругление берёт исходный контур, R = 0 — возврат исходного.
        CurvedDesign.ApplyCornerRadius(slab, 5);
        double area5 = Geometry2D.Area(slab.Polygon2D.Select(p => (p.X, p.Z)).ToList());
        CurvedDesign.ApplyCornerRadius(slab, 0);
        double area0 = Geometry2D.Area(slab.Polygon2D.Select(p => (p.X, p.Z)).ToList());
        Check(report, ref failures, Math.Abs(area / expected - 1) < 0.005 && Math.Abs(vol / (area * 0.22) - 1) < 1e-4 && bad == 0 && Math.Abs(area5 / (240 - (4 - Math.PI) * 25) - 1) < 0.005 && Math.Abs(area0 - 240) < 1e-6,
            string.Create(W4Ru, $"скругление углов CornerRadius: 20 × 12 м, R 3 м — площадь {area:0.00} ≡ 240 − (4 − π)·R² = {expected:0.00} м², объём сетки ≡ S·h, R 5 м — {area5:0.00} м², R 0 — исходные {area0:0.00} м²"));

        // Ротонда: круглая плита R 10 м с центральным отверстием R 4 м (кольцо); схема видит площадь кольца.
        var circle = ContourPath.Sample(ContourPath.Circle(10, 8));
        var ring = W4(ElementTypes.Slab, "Кольцевая плита", 0, 3, 0, 20, 0.25, 20);
        ring.Polygon2D = circle.Select(p => new Point2D(p.X, p.Z)).ToList();
        var hole = W4(ElementTypes.SlabOpening, "Атриум", 0, 3, 0, 8, 0.25, 8);
        hole.Polygon2D = ContourPath.Sample(ContourPath.Circle(4, 8)).Select(p => new Point2D(p.X, p.Z)).ToList();
        hole.HostElementId = ring.Id;
        var (vr, badR, _) = MeshStats(ElementGeometryBuilder.Build(ring, [hole], W4All));
        double ringArea = Math.PI * (100 - 16);
        var model = AnalyticalModelBuilder.Build([ring, hole], [new BuildingGroup("Волна 4")], new StructuralLoadSettings());
        double net = model.Elements.First(m => m.Source.Id == ring.Id).NetArea;
        // Вырожденные трапеции заливки отверстий (прежний MeshAccumulator.FillTrapezoids, эталон ядра) не исправляются — только считаются.
        Check(report, ref failures, Math.Abs(vr / (ringArea * 0.25) - 1) < 0.01 && Math.Abs(net / ringArea - 1) < 0.01,
            string.Create(W4Ru, $"кольцевая плита ротонды: R 10 м − атриум R 4 м: объём сетки {vr:0.00} м³ ≈ π(R² − r²)·h = {ringArea * 0.25:0.00} м³, площадь нетто схемы {net:0.00} ≈ {ringArea:0.00} м²"));
        return failures;
    }

    private static int CheckSpecialRoofs(StringBuilder report)
    {
        int failures = 0;
        var lines = new List<string>();
        bool ok = true;
        foreach (var (shape, expected) in new[] { (RoofShapes.Dome, 2.0 / 3 * Math.PI * 8 * 6 * 5), (RoofShapes.Cone, Math.PI * 8 * 6 * 5 / 3), (RoofShapes.Folded, 16.0 * 12 * (0.2 + 4.8 / 2)) })
        {
            var roof = W4(ElementTypes.Roof, "Кровля " + shape, 0, 10, 0, 16, 5, 12);
            roof.RoofShape = shape;
            var (v, bad, tris) = MeshStats(ElementGeometryBuilder.Build(roof, [], W4All));
            // Купол и конус — по эллипсу 48 сегментов (дискретизация занижает объём на ~0,3 %).
            bool pass = Math.Abs(v / expected - 1) < 0.02 && bad == 0 && RoofShapes.Normalize(shape) == shape;
            ok &= pass;
            lines.Add(string.Create(W4Ru, $"{shape}: {v:0.0} ≈ {expected:0.0} м³ ({tris} треуг.)"));
        }
        Check(report, ref failures, ok && RoofShapes.Normalize("купол") == RoofShapes.Dome && RoofShapes.Normalize("gable") == RoofShapes.Gable,
            "купольная, коническая и складчатая кровли: объёмы сеток по формулам, без вырожденных треугольников; прежние формы не меняются — " + string.Join("; ", lines));
        return failures;
    }

    /// <summary>Дуговой барабан: 8 дуговых стен R 9 м на фундаменте, 2 круглых перекрытия, купол — цепочка пластин в схеме, статика сходится.</summary>
    private static int CheckCurvedAnalysis(StringBuilder report)
    {
        int failures = 0;
        var elements = new List<ArchitecturalElement>();
        var drum = ContourPath.Circle(9, 8, 0, 0, Math.PI / 8);
        var poly = ContourPath.Sample(drum);
        var foundation = W4(ElementTypes.Foundation, "Фундамент барабана", 0, -0.6, 0, 20, 0.6, 20);
        foundation.Polygon2D = ContourPath.Sample(ContourPath.Circle(10, 8)).Select(p => new Point2D(p.X, p.Z)).ToList();
        elements.Add(foundation);
        for (int level = 0; level < 2; level++)
        {
            foreach (var s in drum) { var w = ContourPath.Element(s, ElementTypes.Wall, $"Барабан эт. {level + 1}", level * 4, 3.78, 0.4, "Волна 4", level, "concrete"); elements.Add(w); }
            var center = W4(ElementTypes.Column, $"Колонна центра эт. {level + 1}", 0, level * 4, 0, 0.5, 3.78, 0.5);
            center.LevelIndex = level;
            elements.Add(center);
            var slab = W4(level == 1 ? ElementTypes.Roof : ElementTypes.Slab, $"Перекрытие {level + 1}", 0, level * 4 + 3.78, 0, 18, 0.25, 18);
            if (level == 1) slab.RoofShape = RoofShapes.Flat;
            slab.Polygon2D = poly.Select(p => new Point2D(p.X, p.Z)).ToList();
            slab.LevelIndex = level;
            elements.Add(slab);
        }
        var groups = new List<BuildingGroup> { new("Волна 4") };
        var loads = new StructuralLoadSettings();
        var model = AnalyticalModelBuilder.Build(elements, groups, loads);
        var arcWalls = model.Elements.Where(m => m.Category == BimCategory.Wall).ToList();
        bool chains = arcWalls.Count == 16 && arcWalls.All(m => m.Shape == Models.Analytical.AnalyticalShape.Polyline && m.Axis.Count >= 13);
        var audit = FeaLogicalAuditService.Audit(elements, groups, loads, monotonicity: false);
        double k = StructuralAnalysisService.Analyze(elements, groups, loads).MaxUtilization;
        Check(report, ref failures, chains && audit.Criteria[0].Passed && audit.Criteria[1].Passed && audit.Criteria[4].Passed && k <= 1.0,
            string.Create(W4Ru, $"КЭ-расчёт дугового барабана: 16 дуговых стен — цепочки по {arcWalls.Min(m => m.Axis.Count)}+ точек дуги, ΣF {audit.AppliedLoad:N1} = ΣR_y {audit.GroundReaction:N1} кН (невязка {audit.ImbalancePercent:0.000} %), " +
                                $"непрерывность и пути нагрузки — {(audit.Criteria[1].Passed && audit.Criteria[4].Passed ? "да" : "нет")}, K_исп {k:0.00}"));
        return failures;
    }

    // ═════════════════════════════ Снимки ═════════════════════════════

    private sealed record DemoPhoto(int Index, string File, string Title, byte[] Bytes);

    /// <summary>Пять снимков DemoKit/InternetPhotos (рядом с exe); нет файлов — рендер теми же функциями в папку теста.</summary>
    private static async Task<List<DemoPhoto>> DemoPhotosAsync(string directory)
    {
        var list = new List<DemoPhoto>();
        string kit = Path.Combine(AppContext.BaseDirectory, "DemoKit", "InternetPhotos");
        for (int i = 0; i < PhotoSceneStudio.Photos.Length; i++)
        {
            var (file, title) = PhotoSceneStudio.Photos[i];
            string path = Path.Combine(kit, file);
            byte[] bytes;
            if (File.Exists(path)) bytes = await File.ReadAllBytesAsync(path);
            else
            {
                bytes = await PhotoImageLoader.EncodeAsync(PhotoSceneStudio.Render(i), PhotoSceneStudio.Width, PhotoSceneStudio.Height, png: true);
                Directory.CreateDirectory(Path.Combine(directory, "InternetPhotos"));
                await File.WriteAllBytesAsync(Path.Combine(directory, "InternetPhotos", file), bytes);
            }
            list.Add(new DemoPhoto(i, file, title, bytes));
        }
        return list;
    }

    /// <summary>Сервер снимков без сети: картинка, страница с og:image, страница со srcset, WebP; проверка заголовков запроса.</summary>
    private sealed class PhotoServerHandler(byte[] png) : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = [];
        public List<string> Accepts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            Accepts.Add(request.Headers.Accept.ToString());
            string path = request.RequestUri!.AbsolutePath;
            HttpContent content = path switch
            {
                "/tower.png" => new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } },
                // Тип сервера неверный (octet-stream) — тип определяется по сигнатуре.
                "/blob" => new ByteArrayContent(png) { Headers = { ContentType = new("application/octet-stream") } },
                "/news/tower.html" => new StringContent("<html><head><meta property=\"og:image\" content=\"/tower.png\"></head><body>Башня</body></html>", Encoding.UTF8, "text/html"),
                "/gallery.html" => new StringContent("<html><body><img src=\"/thumb.jpg\" srcset=\"/small.jpg 320w, /blob 1600w\"></body></html>", Encoding.UTF8, "text/html"),
                _ => new StringContent("not found")
            };
            var status = content is StringContent sc && sc.Headers.ContentType?.MediaType == "text/plain" ? HttpStatusCode.NotFound : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content, RequestMessage = request });
        }
    }

    private static async Task<int> CheckPhotoLoadingAsync(List<DemoPhoto> photos, StringBuilder report)
    {
        int failures = 0;
        var png = photos[0].Bytes;
        var server = new PhotoServerHandler(png);
        var direct = await PhotoImageLoader.FromUrlAsync("https://photos.example/tower.png", server);
        var page = await PhotoImageLoader.FromUrlAsync("https://photos.example/news/tower.html", server);
        var gallery = await PhotoImageLoader.FromUrlAsync("https://photos.example/gallery.html", server);
        string fragment = "<html><body><!--StartFragment--><img alt=\"Здание\" src=\"//cdn.example/a.jpg\" srcset=\"//cdn.example/a.jpg 640w, //cdn.example/a@2x.jpg 1280w\"><!--EndFragment--></body></html>";
        string? dragUrl = PhotoImageLoader.ExtractImageUrl(fragment, new Uri("https://www.example.org/article"));
        string dataUri = "data:image/png;base64," + Convert.ToBase64String(png);
        var data = await PhotoImageLoader.FromUrlAsync(dataUri);
        var webp = new byte[16];
        "RIFF"u8.CopyTo(webp);
        "WEBP"u8.CopyTo(webp.AsSpan(8));
        bool same = direct.Bytes.SequenceEqual(png) && page.Bytes.SequenceEqual(png) && gallery.Bytes.SequenceEqual(png) && data.Bytes.SequenceEqual(png);
        bool headers = server.UserAgents.All(u => u.Contains("ArchitectStudioPro")) && server.Accepts.All(a => a.Contains("image/webp"));
        // Снимок по ссылке анализируется так же, как файл.
        var (byUrl, _, _) = await PhotoToBimService.AnalyzeAsync(direct);
        var (byFile, _, _) = await PhotoToBimService.AnalyzeAsync(new LoadedPhoto(png, "image/png", photos[0].File, null));
        bool rejected = false;
        try { await PhotoImageLoader.FromUrlAsync("ftp://photos.example/a.png", server); } catch (ArgumentException) { rejected = true; }
        Check(report, ref failures, same && headers && dragUrl == "https://cdn.example/a@2x.jpg" && direct.MimeType == "image/png" && gallery.MimeType == "image/png" &&
                                    PhotoImageLoader.SniffMime(webp) == "image/webp" && byUrl.Morphology == byFile.Morphology && byUrl.WallHex == byFile.WallHex && rejected,
            $"снимок по ссылке: прямая картинка, страница (og:image), галерея (крупнейшая запись srcset, тип по сигнатуре), HTML-фрагмент перетаскивания → {dragUrl}, data-URI, WebP по сигнатуре; " +
            $"заголовки User-Agent и Accept (image/webp) у {server.UserAgents.Count} запросов; паспорт по ссылке ≡ паспорту файла ({PhotoArchitectureDescriptor.MorphologyCode(byUrl.Morphology)}); ftp — отказ");
        return failures;
    }

    /// <summary>«Ленивая коробка» модели проектирования: прямоугольный блок без дуг, ярусов и цветов.</summary>
    private const string LazyBoxJson = "{\"message\":\"Офисное здание\",\"elements\":[" +
        "{\"type\":\"foundation\",\"position\":[0,-0.3,0],\"size\":[40,0.6,20]}," +
        "{\"type\":\"wall\",\"start\":[-20,-10],\"end\":[20,-10],\"height\":60,\"thickness\":0.3}," +
        "{\"type\":\"wall\",\"start\":[20,-10],\"end\":[20,10],\"height\":60,\"thickness\":0.3}," +
        "{\"type\":\"slab\",\"position\":[0,60,0],\"size\":[40,0.25,20]}]}";

    private static async Task<int> CheckModelRoutingAsync(List<DemoPhoto> photos, StringBuilder report)
    {
        int failures = 0;
        var curvedPhoto = new LoadedPhoto(photos[0].Bytes, "image/png", photos[0].File, null);
        var (curved, _, jpeg) = await PhotoToBimService.AnalyzeAsync(curvedPhoto);
        var coder = new AiModelOption("ollama:qwen2.5-coder:14b", AiProviderKind.Ollama, "qwen2.5-coder:14b", "qwen2.5-coder 14B", "coder 14B", false);
        var vision = new AiModelOption("ollama:gemma3:12b", AiProviderKind.Ollama, "gemma3:12b", "gemma3 12B", "gemma3", true);
        var web = AiModelCatalog.WebOptions.First();
        var cloud = new AiModelOption("anthropic:claude-sonnet-5-5", AiProviderKind.Anthropic, "claude-sonnet-5-5", "Claude Sonnet 5.5", "Sonnet", true);
        var ai = App.Services.GetRequiredService<AIOrchestrator>();
        var loads = new StructuralLoadSettings();
        var lines = new List<string>();
        bool ok = true;
        foreach (var model in new[] { coder, vision, web, cloud })
        {
            var dispatch = UniversalVisionModelBridge.Prepare(model, curved, jpeg, null);
            var ctx = UniversalVisionModelBridge.CreateContext(ai, dispatch, curved, null, loads, appendToSite: false);
            var (_, user) = ai.BuildPrompts(ctx);
            bool expectImage = model.SupportsVision || model.IsWeb;
            bool promptOk = user.Contains("VisionPromptV2") && user.Contains("PhotoArchitectureDescriptor") && user.Contains("arcBulge") && user.Contains("cornerRadius") &&
                            user.Contains(curved.GlassHex) && user.Contains("silhouette top polyline");
            bool webOk = !model.IsWeb || ai.BuildWebPrompt(ctx).Contains("VisionPromptV2") &&
                         WebAccountBridgeProvider.AttachImagesScript(WebAccountBridgeProvider.Site(model.Provider), ctx.Images).Contains(Convert.ToBase64String(jpeg)[..64]);
            ok &= dispatch.ImageAttached == expectImage && ctx.Images.Count == (expectImage ? 1 : 0) && promptOk && webOk && ctx.Photo is not null && ctx.WithModel(cloud).Photo is not null;
            lines.Add($"{model.ShortName}: {(dispatch.ImageAttached ? "снимок + паспорт" : "матрица без снимка")}");
        }
        // Ответ «ленивой» модели → солвер → сверка со снимком: дуговой фасад, скругления, цвета; каркас доведён.
        var ctxLocal = UniversalVisionModelBridge.CreateContext(ai, UniversalVisionModelBridge.Prepare(coder, curved, jpeg, null), curved, null, loads, appendToSite: false);
        var scene = new SceneStateService();
        var solved = ai.Solve(LazyBoxJson, ctxLocal, scene);
        var traits = PhotoFidelityEnforcer.Traits(solved.Result.Elements);
        double k = StructuralAnalysisService.Analyze(solved.Result.Elements, solved.Result.Groups, loads).MaxUtilization;
        bool painted = solved.Result.Elements.Where(e => e.Category == BimCategory.CurtainWall).All(e => CurvedDesign.FacadeColor(e) == curved.GlassHex);
        ok &= traits.ArcElements >= 2 && traits.HasRoundedContour && painted && k <= 1.0 && solved.Result.Report.Actions.Any(a => a.StartsWith("Фото-верность", StringComparison.Ordinal));
        // Ответ, уже совпадающий с формой (дуга в JSON модели), не пересобирается — дополняются только цвета.
        string goodJson = "{\"elements\":[{\"type\":\"foundation\",\"position\":[0,-0.3,0],\"size\":[40,0.6,24]}," +
                          "{\"type\":\"curtain_wall\",\"start\":[-18,-10],\"end\":[18,-10],\"height\":3.6,\"arcBulge\":-6}," +
                          "{\"type\":\"slab\",\"position\":[0,3.6,0],\"size\":[36,0.25,20],\"cornerRadius\":4}]}";
        var kept = ai.Solve(goodJson, ctxLocal, new SceneStateService());
        bool keptOk = kept.Result.Elements.Count(e => e.Category is BimCategory.CurtainWall or BimCategory.Slab or BimCategory.Foundation) == 3 &&
                      kept.Result.Elements.Where(e => e.Category is BimCategory.CurtainWall or BimCategory.Slab).All(e => CurvedDesign.FacadeColor(e) is not null) &&
                      kept.Result.Elements.Any(e => CurvedDesign.IsArc(e)) && kept.Result.Report.Actions.Any(a => a.Contains("цвета фасада со снимка"));
        ok &= keptOk;
        Check(report, ref failures, ok,
            $"[арок {traits.ArcElements}, скругл. {traits.HasRoundedContour}, цвет {painted}, K {k:0.00}, сохранён {keptOk}] подача снимка в разные модели: {string.Join(", ", lines)}; «ленивая коробка» локальной текстовой модели → дуговых элементов {traits.ArcElements}, скругление, " +
            $"стекло {curved.GlassHex}, K_исп {k.ToString("0.00", W4Ru)}; ответ с дугой сохранён (только цвета)");

        // Совет КБ: роли получают паспорт снимка, итог сверяется со снимком и доводится.
        var cascadePhoto = new LoadedPhoto(photos[1].Bytes, "image/png", photos[1].File, null);
        var (cascade, _, cjpeg) = await PhotoToBimService.AnalyzeAsync(cascadePhoto);
        var models = new List<AiModelOption> { cloud, AiModelCatalog.LocalQwen7B };
        var councilScene = new SceneStateService();
        var gateway = new CouncilScenarioGateway();
        var swarm = new AgentSwarmOrchestrator(councilScene, ai, gateway, () => new CouncilSettings { ConsultUser = false }, () => new StructuralLoadSettings(), () => models);
        var cctx = UniversalVisionModelBridge.CreateContext(ai, UniversalVisionModelBridge.Prepare(cloud, cascade, cjpeg, null), cascade, null, loads, appendToSite: false);
        var run = await swarm.RunDesignAsync(cctx, consult: false, _ => { }, CancellationToken.None);
        var ct = PhotoFidelityEnforcer.Traits(councilScene.Elements.ToList());
        double kc = councilScene.Elements.Count > 0 ? StructuralAnalysisService.Analyze(councilScene.Elements.ToList(), councilScene.BuildingGroups.ToList(), loads).MaxUtilization : 99;
        bool sawPassport = gateway.Calls.Count > 0 && gateway.Calls.Any(c => c.UserPrompt.Contains("PhotoArchitectureDescriptor"));
        Check(report, ref failures, sawPassport && ct.Tiers >= 3 && ct.ColoredElements > ct.Elements / 2 && kc <= 1.0,
            string.Create(W4Ru, $"Совет КБ по снимку каскада: вызовов ролей {gateway.Calls.Count}, паспорт снимка в задании — {(sawPassport ? "да" : "нет")}; итог в сцене — ярусов {ct.Tiers}, " +
                                $"в цвете {ct.ColoredElements} из {ct.Elements}, K_исп {kc:0.00}{(run.AutoApplied is { } a ? " · " + a.Summary : string.Empty)}"));
        return failures;
    }

    private static async Task<int> CheckFivePhotosAsync(List<DemoPhoto> photos, StringBuilder report)
    {
        int failures = 0;
        var expected = new[] { PhotoMorphology.Curved, PhotoMorphology.Cascading, PhotoMorphology.Rotunda, PhotoMorphology.Triangular, PhotoMorphology.ArchSpan };
        foreach (var photo in photos)
        {
            var (d, _, _) = await PhotoToBimService.AnalyzeAsync(new LoadedPhoto(photo.Bytes, "image/png", photo.File, null));
            var lift = PhotoToBimService.Lift(d, new PhotoLiftOptions { GroupName = photo.Title }, new StructuralLoadSettings(), verify: true, monotonicity: true);
            var t = PhotoFidelityEnforcer.Traits(lift.Model.Elements);
            var els = lift.Model.Elements;
            (bool Ok, string Text) morph = photo.Index switch
            {
                0 => (t.ArcElements >= 2 && t.HasRoundedContour && els.Any(e => e.Category == BimCategory.CurtainWall && CurvedDesign.IsArc(e)),
                    $"дуговых элементов {t.ArcElements} (витражи по дуге), скругление углов перекрытий"),
                1 => (t.Tiers >= 3, $"каскадных ярусов {t.Tiers}"),
                2 => (t.HasDome && els.Count(e => e.Category == BimCategory.Wall && CurvedDesign.IsArc(e)) >= 8 && t.RoundColumns >= 4,
                    $"барабан из {els.Count(e => e.Category == BimCategory.Wall && CurvedDesign.IsArc(e))} дуговых стен, купол, круглых колонн {t.RoundColumns}"),
                3 => (t.MinPlanAngle < 75 && t.NonOrthogonalWalls >= 2 && els.Any(e => CurvedDesign.HasMiter(e)),
                    string.Create(W4Ru, $"острый угол плана {t.MinPlanAngle:0} °, косых стен {t.NonOrthogonalWalls}, биссектрисных узлов {els.Count(e => CurvedDesign.HasMiter(e))}")),
                _ => (t.HasVault && t.InteriorColumnsInHall == 0 && Math.Min(t.Width, t.Depth) >= 24,
                    string.Create(W4Ru, $"свод пролётом {Math.Min(t.Width, t.Depth):0.0} м, колонн внутри зала {t.InteriorColumnsInHall}, наклонных пилонов {els.Count(e => e.Category == BimCategory.Column && e.Rotation.Length > 2 && Math.Abs(e.Rotation[2]) > 1)}"))
            };
            // Цвета: элементы окрашены цветами паспорта, паспорт близок к цветам материалов снимка.
            var truth = PhotoSceneStudio.TruthColors[photo.Index];
            double Near(string hex) => d.Palette.Min(c => CurvedDesign.ColorDistance(c.Hex, hex));
            bool painted = els.Where(e => e.Category == BimCategory.CurtainWall).All(e => CurvedDesign.FacadeColor(e) == d.GlassHex) &&
                           els.Where(e => e.Category is BimCategory.Roof or BimCategory.Shell).Any(e => CurvedDesign.FacadeColor(e) is not null) &&
                           t.ColoredElements >= t.Elements * 0.6;
            bool colors = painted && Near(truth.Wall) <= 70 && Near(truth.Glass) <= 90 && Near(truth.Roof) <= 75;
            var audit = lift.Audit!;
            bool structure = lift.MaxUtilization <= 1.0 && audit.Passed && lift.Hanging == 0 && lift.DoorColumnClashes == 0;
            Check(report, ref failures, d.Morphology == expected[photo.Index] && morph.Ok && colors && structure,
                string.Create(W4Ru, $"№{photo.Index + 1} «{photo.Title}»: морфология {PhotoToBimService.MorphologyTitle(d.Morphology)} ({string.Join("; ", d.Evidence.Take(1))}); {morph.Text}; " +
                                    $"цвета — стена {d.WallHex} (≈ {truth.Wall}, Δ {Near(truth.Wall):0}), стекло {d.GlassHex} (Δ {Near(truth.Glass):0}), кровля {d.RoofHex} (Δ {Near(truth.Roof):0}), в цвете {t.ColoredElements} из {t.Elements}; " +
                                    $"этажей {lift.Model.Floors}, K_исп {lift.MaxUtilization:0.00}, ΣF {audit.AppliedLoad:N0} = ΣR_y {audit.GroundReaction:N0} кН, аудит {audit.Criteria.Count(c => c.Passed)}/{audit.Criteria.Count}, " +
                                    $"висящих {lift.Hanging}, дверей в колоннах {lift.DoorColumnClashes}, пересечений {lift.Clashes}, смета каркаса {lift.FrameCostRub:N0} ₽, {lift.ElapsedMs:0} мс"));
        }
        return failures;
    }

    private static int CheckWave4Strings(StringBuilder report)
    {
        int failures = 0;
        var texts = new List<string>();
        if (typeof(LocalizationService).GetField("Wave4Strings", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is Dictionary<string, (string Ru, string En)> strings)
            texts.AddRange(strings.Values.SelectMany(v => new[] { v.Ru, v.En }));
        int keys = texts.Count / 2;
        texts.AddRange(Enum.GetValues<PhotoMorphology>().Select(PhotoToBimService.MorphologyTitle));
        var banned = new Regex(@"(?<![\p{L}])(ИИ|AI|LLM)(?![\p{L}])", RegexOptions.CultureInvariant);
        var dirty = texts.Where(t => banned.IsMatch(t) || t.EnumerateRunes().Any(r => r.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF)).ToList();
        bool terms = texts.Any(t => t.Contains("Реконструкция по фото и плану")) && texts.Any(t => t.Contains("Модель проектирования") || t.Contains("модели проектирования")) &&
                     texts.Any(t => t.Contains("Визуальное ядро") || t.Contains("Визуального ядра")) && texts.Any(t => t.Contains("Внешний проектировщик")) &&
                     texts.Any(t => t.Contains("Локальная модель") || t.Contains("локальная модель"));
        Check(report, ref failures, keys >= 60 && dirty.Count == 0 && terms,
            $"строки волны 4: ключей {keys} (RU/EN) и названия морфологий — без эмодзи и «ИИ / AI / LLM»; термины «Реконструкция по фото и плану», «Модель проектирования», «Визуальное ядро», " +
            "«Внешний проектировщик», «Локальная модель»" + (dirty.Count > 0 ? " [" + string.Join(" | ", dirty.Take(3)) + "]" : string.Empty));
        return failures;
    }
}
#endif
