using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using AIArchitectStudio.Services.AI;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AIArchitectStudio.Services;

/// <summary>
/// Детерминированная трассировка растрового поэтажного плана (PNG/JPG, страница PDF в 2000 px): бинаризация по Оцу, толстые
/// тёмные штрихи вдоль X и Y листа (длина ≥ 3 % листа, толщина ≥ 3 px — тонкие размерные, мебель, штриховка и надписи
/// отсеиваются) → компоненты связности → оси и толщины стен; разрывы соосных стен 0.5…3 м — проёмы: с тонкими линиями
/// внутри (условное окно) — окно, пустой — дверь. Масштаб — по калибровочному размеру (габарит по наружным граням, мм)
/// или по толщине наружной стены 300 мм.
/// </summary>
public static class RasterPlanTracer
{
    public const double DefaultExteriorWallMm = 300;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Пиксели BGRA8 плана (строки сверху вниз).</summary>
    public sealed record RasterImage(byte[] Bgra, int Width, int Height);

    /// <param name="calibrationWidthMm">Габарит плана по наружным граням вдоль X листа, мм (0 — по толщине наружной стены).</param>
    public static RecognizedPlan Trace(RasterImage image, string name, double calibrationWidthMm = 0, byte[]? sourceBytes = null, string? mime = null)
    {
        int w = image.Width, h = image.Height;
        var dark = Binarize(image);
        int minRun = Math.Max(12, (int)Math.Round(Math.Min(w, h) * 0.03));
        const int minThick = 3;

        // Длины горизонтальных и вертикальных серий тёмных пикселей.
        var runH = new int[w * h];
        var runV = new int[w * h];
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (x < w)
            {
                if (!dark[y * w + x]) { x++; continue; }
                int s = x;
                while (x < w && dark[y * w + x]) x++;
                for (int k = s; k < x; k++) runH[y * w + k] = x - s;
            }
        }
        for (int x = 0; x < w; x++)
        {
            int y = 0;
            while (y < h)
            {
                if (!dark[y * w + x]) { y++; continue; }
                int s = y;
                while (y < h && dark[y * w + x]) y++;
                for (int k = s; k < y; k++) runV[k * w + x] = y - s;
            }
        }
        // Толстые штрихи: длинная серия вдоль оси и толщина (серия длинных серий поперёк) не меньше minThick.
        var hMask = new bool[w * h];
        var vMask = new bool[w * h];
        for (int x = 0; x < w; x++)
        {
            int y = 0;
            while (y < h)
            {
                if (runH[y * w + x] < minRun) { y++; continue; }
                int s = y;
                while (y < h && runH[y * w + x] >= minRun) y++;
                if (y - s >= minThick) for (int k = s; k < y; k++) hMask[k * w + x] = true;
            }
        }
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (x < w)
            {
                if (runV[y * w + x] < minRun) { x++; continue; }
                int s = x;
                while (x < w && runV[y * w + x] >= minRun) x++;
                if (x - s >= minThick) for (int k = s; k < x; k++) vMask[y * w + k] = true;
            }
        }

        var pieces = new List<(bool Horizontal, double Axis, double From, double To, double Thick)>();
        foreach (var (mask, horizontal) in new[] { (hMask, true), (vMask, false) })
            foreach (var c in Components(mask, w, h))
            {
                int length = horizontal ? c.X1 - c.X0 + 1 : c.Y1 - c.Y0 + 1;
                double thick = c.Count / (double)Math.Max(1, length);
                if (length < minRun || length < 2 * thick) continue;
                double axis = horizontal ? (c.Y0 + c.Y1 + 1) / 2.0 : (c.X0 + c.X1 + 1) / 2.0;
                pieces.Add(horizontal ? (true, axis, c.X0, c.X1 + 1, thick) : (false, axis, c.Y0, c.Y1 + 1, thick));
            }

        var plan = new RecognizedPlan { Name = name, Source = PlanSourceKind.Raster, PixelWidth = w, PixelHeight = h, Image = sourceBytes, ImageMime = mime };
        if (pieces.Count == 0)
        {
            plan.Notes.Add("Толстых штрихов стен на изображении не найдено");
            return plan;
        }

        // Масштаб, мм/пиксель.
        double minX = pieces.Min(p => p.Horizontal ? p.From : p.Axis - p.Thick / 2), maxX = pieces.Max(p => p.Horizontal ? p.To : p.Axis + p.Thick / 2);
        double mmPerPx;
        if (calibrationWidthMm > 0)
        {
            mmPerPx = calibrationWidthMm / Math.Max(1, maxX - minX);
            plan.ScaleSource = string.Format(Ru, "по калибровочному размеру {0:N0} мм", calibrationWidthMm);
        }
        else
        {
            var thicknesses = pieces.Select(p => p.Thick).OrderBy(t => t).ToList();
            double thick = thicknesses[(int)Math.Floor((thicknesses.Count - 1) * 0.75)];
            mmPerPx = DefaultExteriorWallMm / Math.Max(1, thick);
            plan.ScaleSource = string.Format(Ru, "по толщине наружной стены {0:0} мм", DefaultExteriorWallMm);
        }
        plan.MmPerPixel = mmPerPx;
        double m = mmPerPx / 1000;
        foreach (var p in pieces)
        {
            double t = Math.Round(p.Thick * m / 0.01) * 0.01;
            plan.Walls.Add(p.Horizontal
                ? new PlanWall(p.From * m, (h - p.Axis) * m, p.To * m, (h - p.Axis) * m, Math.Max(0.08, t))
                : new PlanWall(p.Axis * m, (h - p.To) * m, p.Axis * m, (h - p.From) * m, Math.Max(0.08, t)));
        }

        // Проёмы: разрывы соосных штрихов 0.5…3 м (окно — тонкие линии в разрыве, иначе дверь).
        foreach (bool horizontal in new[] { true, false })
        {
            var line = pieces.Where(p => p.Horizontal == horizontal).OrderBy(p => p.Axis).ThenBy(p => p.From).ToList();
            for (int i = 0; i < line.Count; i++)
                for (int j = 0; j < line.Count; j++)
                {
                    var (a, b) = (line[i], line[j]);
                    if (i == j || Math.Abs(a.Axis - b.Axis) > Math.Max(a.Thick, b.Thick) / 2) continue;
                    double gap = b.From - a.To;
                    if (gap * m < 0.5 || gap * m > 3.0) continue;
                    // Нет ли между ними третьего штриха той же оси.
                    if (line.Any(c => Math.Abs(c.Axis - a.Axis) <= c.Thick / 2 && c.From < b.From - 1 && c.To > a.To + 1)) continue;
                    double t = Math.Max(a.Thick, b.Thick);
                    int darkCount = 0, total = 0;
                    for (int s = (int)a.To + 1; s < (int)b.From - 1; s++)
                        for (int q = (int)(a.Axis - t / 2); q <= (int)(a.Axis + t / 2); q++)
                        {
                            int x = horizontal ? s : q, y = horizontal ? q : s;
                            if (x < 0 || y < 0 || x >= w || y >= h) continue;
                            total++;
                            if (dark[y * w + x]) darkCount++;
                        }
                    bool window = total > 0 && darkCount / (double)total > 0.04;
                    double c0 = (a.To + b.From) / 2;
                    plan.Openings.Add(horizontal
                        ? new PlanOpening(c0 * m, (h - a.Axis) * m, gap * m, window ? PlanOpeningKind.Window : PlanOpeningKind.Door)
                        : new PlanOpening(a.Axis * m, (h - c0) * m, gap * m, window ? PlanOpeningKind.Window : PlanOpeningKind.Door));
                }
        }
        plan.Notes.Add(string.Format(Ru, "Штрихов стен {0}, проёмов {1}, масштаб {2:0.0} мм/пикс ({3})", pieces.Count, plan.Openings.Count, mmPerPx, plan.ScaleSource));
        return plan;
    }

    private static bool[] Binarize(RasterImage image)
    {
        int n = image.Width * image.Height;
        var luma = new byte[n];
        var histogram = new int[256];
        for (int i = 0; i < n; i++)
        {
            int b = image.Bgra[i * 4], g = image.Bgra[i * 4 + 1], r = image.Bgra[i * 4 + 2], a = image.Bgra[i * 4 + 3];
            // Прозрачные пиксели — белый лист.
            int y = (r * 299 + g * 587 + b * 114) / 1000;
            y = (y * a + 255 * (255 - a)) / 255;
            luma[i] = (byte)y;
            histogram[y]++;
        }
        // Порог Оцу, ограниченный 60…170 (линии чертежа тёмные, фон светлый).
        double sum = 0;
        for (int t = 0; t < 256; t++) sum += t * histogram[t];
        double sumB = 0, best = -1;
        int wB = 0, threshold = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += histogram[t];
            if (wB == 0) continue;
            int wF = n - wB;
            if (wF == 0) break;
            sumB += t * histogram[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) (best, threshold) = (between, t);
        }
        threshold = Math.Clamp(threshold, 60, 170);
        var dark = new bool[n];
        for (int i = 0; i < n; i++) dark[i] = luma[i] < threshold;
        return dark;
    }

    private readonly record struct Component(int X0, int Y0, int X1, int Y1, int Count);

    private static List<Component> Components(bool[] mask, int w, int h)
    {
        var seen = new bool[mask.Length];
        var result = new List<Component>();
        var stack = new Stack<int>();
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, count = 0;
            stack.Push(i);
            seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                count++;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                if (x > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (x < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (y > 0 && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (y < h - 1 && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            result.Add(new Component(x0, y0, x1, y1, count));
        }
        return result;
    }

    // ───────────────────────────── Загрузка файла плана ─────────────────────────────

    /// <summary>Растровый план из PNG/JPG/BMP или первой страницы PDF (рендер 2000 px), DXF — по парам линий стен.</summary>
    public static async Task<RecognizedPlan> LoadAsync(string path, double calibrationWidthMm = 0)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string name = Path.GetFileName(path);
        if (ext == ".dxf")
        {
            var drawing = await new CadDxfImporterService().LoadAsync(path);
            return PlanToBimPipelineService.FromDxf(drawing);
        }
        byte[] bytes;
        string mime;
        if (ext == ".pdf")
        {
            var pdf = await PdfBlueprintExtractor.OpenAsync(path);
            var page = (await PdfBlueprintExtractor.RenderForAiAsync(pdf, [0]))[0];
            (bytes, mime) = (page.Bytes, page.MimeType);
        }
        else
        {
            bytes = await File.ReadAllBytesAsync(path);
            mime = ext is ".png" ? "image/png" : ext is ".bmp" ? "image/bmp" : "image/jpeg";
        }
        var image = await DecodeAsync(bytes);
        var plan = Trace(image, name, calibrationWidthMm, bytes, mime);
        return ext == ".pdf" ? Retag(plan, PlanSourceKind.Pdf) : plan;
    }

    private static RecognizedPlan Retag(RecognizedPlan plan, PlanSourceKind source)
    {
        var copy = new RecognizedPlan
        {
            Name = plan.Name, Source = source, Walls = plan.Walls, Openings = plan.Openings, MmPerPixel = plan.MmPerPixel, ScaleSource = plan.ScaleSource,
            PixelWidth = plan.PixelWidth, PixelHeight = plan.PixelHeight, Image = plan.Image, ImageMime = plan.ImageMime
        };
        copy.Notes.AddRange(plan.Notes);
        return copy;
    }

    /// <summary>Декодирование PNG/JPG в BGRA8 (длинная сторона не больше 2400 px).</summary>
    public static async Task<RasterImage> DecodeAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        uint w = decoder.PixelWidth, h = decoder.PixelHeight;
        double k = Math.Min(1, 2400.0 / Math.Max(w, h));
        var transform = new BitmapTransform { ScaledWidth = (uint)Math.Max(1, Math.Round(w * k)), ScaledHeight = (uint)Math.Max(1, Math.Round(h * k)), InterpolationMode = BitmapInterpolationMode.Fant };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return new RasterImage(data.DetachPixelData(), (int)transform.ScaledWidth, (int)transform.ScaledHeight);
    }
}
