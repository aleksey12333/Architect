using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AIArchitectStudio.Services.AI;

/// <summary>Вид листа по визуальному анализу миниатюры.</summary>
public enum PdfSheetKind
{
    Blank,
    Cover,
    Sparse,
    TextOrTable,
    Drawing
}

/// <summary>Миниатюра страницы и её характеристики: доля «чернил», покрытие листа, линии рамок/таблиц.</summary>
public sealed record PdfPageAnalysis(int Index, byte[] ThumbnailPng, double Ink, double Coverage, double LongLines, PdfSheetKind Kind, double Score);

/// <summary>
/// Многостраничный инспектор PDF-чертежей. Раньше растеризовалась только первая страница (в рабочих проектах это
/// титульная обложка без чертежей); теперь каждая страница получает миниатюру и оценку информативности, автовыбор
/// пропускает обложки, пустые и разреженные листы и отбирает листы с чертежами, таблицами сечений и общими указаниями,
/// а выбранные страницы рендерятся для ИИ в высоком разрешении (2000 px по длинной стороне).
/// </summary>
public static class PdfBlueprintExtractor
{
    public const uint AiLongSide = 2000;
    public const int MaxAutoPages = 8;
    public const int MaxPagesForAi = 12;
    private const uint ThumbnailLongSide = 260;

    public static async Task<PdfDocument> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        if (pdf.PageCount == 0) throw new ArgumentException("PDF has no pages.");
        return pdf;
    }

    /// <summary>Миниатюра и визуальный анализ страницы.</summary>
    public static async Task<PdfPageAnalysis> AnalyzeAsync(PdfDocument pdf, int index)
    {
        using var page = pdf.GetPage((uint)index);
        using var stream = await RenderAsync(page, ThumbnailLongSide, BitmapEncoder.PngEncoderId);
        var png = await ReadAllAsync(stream);

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;

        // Тёмные пиксели («чернила»), покрытие листа ячейками 12×12 и длинные прямые (рамки, таблицы, линии чертежа).
        const int cells = 12;
        var cellInk = new int[cells * cells];
        var cellTotal = new int[cells * cells];
        long dark = 0;
        int longRows = 0, longCols = 0;
        var colDark = new int[w];
        for (int y = 0; y < h; y++)
        {
            int rowDark = 0, run = 0, bestRun = 0;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int lum = (pixels[i] * 114 + pixels[i + 1] * 587 + pixels[i + 2] * 299) / 1000;
                int cell = Math.Min(cells - 1, y * cells / h) * cells + Math.Min(cells - 1, x * cells / w);
                cellTotal[cell]++;
                if (lum < 170)
                {
                    dark++;
                    rowDark++;
                    colDark[x]++;
                    cellInk[cell]++;
                    run++;
                    bestRun = Math.Max(bestRun, run);
                }
                else run = 0;
            }
            if (bestRun > w * 0.45) longRows++;
        }
        for (int x = 0; x < w; x++)
            if (colDark[x] > h * 0.45) longCols++;

        double ink = (double)dark / Math.Max(1, w * h);
        int covered = 0;
        for (int c = 0; c < cellInk.Length; c++)
            if (cellTotal[c] > 0 && (double)cellInk[c] / cellTotal[c] > 0.015) covered++;
        double coverage = (double)covered / cellInk.Length;
        double longLines = (double)(longRows + longCols) / Math.Max(1, w + h);

        var kind = ink < 0.003 ? PdfSheetKind.Blank
            : index == 0 && (coverage < 0.45 || ink < 0.03) ? PdfSheetKind.Cover
            : coverage < 0.25 ? PdfSheetKind.Sparse
            : longLines > 0.08 && ink < 0.09 ? PdfSheetKind.TextOrTable
            : PdfSheetKind.Drawing;
        // Чертёж — линии по всему листу; таблицы сечений и общие указания тоже информативны, но идут после чертежей.
        double score = kind switch
        {
            PdfSheetKind.Drawing => 1.0 + coverage + Math.Min(ink * 4, 0.6),
            PdfSheetKind.TextOrTable => 0.6 + coverage * 0.5,
            PdfSheetKind.Sparse => 0.2 + coverage,
            _ => 0
        };
        return new PdfPageAnalysis(index, png, ink, coverage, longLines, kind, score);
    }

    /// <summary>
    /// Автовыбор листов: без обложек, пустых и разреженных страниц; не больше <see cref="MaxAutoPages"/> самых
    /// информативных листов в исходном порядке.
    /// </summary>
    public static List<int> AutoSelect(IReadOnlyList<PdfPageAnalysis> pages, int max = MaxAutoPages)
    {
        var candidates = pages.Where(p => p.Kind is PdfSheetKind.Drawing or PdfSheetKind.TextOrTable).ToList();
        if (candidates.Count == 0) candidates = pages.Where(p => p.Kind != PdfSheetKind.Blank).ToList();
        if (candidates.Count == 0 && pages.Count > 0) candidates = [pages.MaxBy(p => p.Ink)!];
        return candidates.OrderByDescending(p => p.Score).Take(max).Select(p => p.Index).Order().ToList();
    }

    /// <summary>Рендер выбранных страниц для ИИ: JPEG 2000 px по длинной стороне, подпись «Лист N из M».</summary>
    public static async Task<List<AiImage>> RenderForAiAsync(PdfDocument pdf, IEnumerable<int> pages, uint longSide = AiLongSide)
    {
        var result = new List<AiImage>();
        foreach (int index in pages.Take(MaxPagesForAi))
        {
            using var page = pdf.GetPage((uint)index);
            using var png = await RenderAsync(page, longSide, BitmapEncoder.PngEncoderId);
            var jpeg = await ToJpegAsync(png, 0.9, longSide);
            result.Add(new AiImage(jpeg, "image/jpeg", $"Лист PDF: страница {index + 1} из {pdf.PageCount}"));
        }
        return result;
    }

    /// <summary>
    /// Сводный лист (сетка из выбранных страниц) для веб-чата, куда изображение вставляется одним Ctrl+V.
    /// </summary>
    public static async Task<byte[]> ComposeSheetAsync(IReadOnlyList<AiImage> images, uint maxSide = 2400)
    {
        if (images.Count == 1) return images[0].Bytes;
        int cols = (int)Math.Ceiling(Math.Sqrt(images.Count));
        int rows = (int)Math.Ceiling(images.Count / (double)cols);
        int cell = (int)(maxSide / Math.Max(cols, rows));
        int width = cols * cell, height = rows * cell;
        var canvas = new byte[width * height * 4];
        Array.Fill(canvas, (byte)255);

        for (int n = 0; n < images.Count; n++)
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(images[n].Bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            double scale = Math.Min((cell - 8.0) / decoder.PixelWidth, (cell - 8.0) / decoder.PixelHeight);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
                ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
            int ox = (n % cols) * cell + 4, oy = (n / cols) * cell + 4;
            for (int y = 0; y < transform.ScaledHeight; y++)
                System.Buffer.BlockCopy(pixels, (int)(y * transform.ScaledWidth * 4), canvas, ((oy + y) * width + ox) * 4, (int)transform.ScaledWidth * 4);
        }

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output,
            [new KeyValuePair<string, BitmapTypedValue>("ImageQuality", new BitmapTypedValue(0.9, Windows.Foundation.PropertyType.Single))]);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, canvas);
        await encoder.FlushAsync();
        return await ReadAllAsync(output);
    }

    public static string KindTitle(PdfSheetKind kind, bool russian) => kind switch
    {
        PdfSheetKind.Blank => russian ? "пустой" : "blank",
        PdfSheetKind.Cover => russian ? "обложка" : "cover",
        PdfSheetKind.Sparse => russian ? "мало данных" : "sparse",
        PdfSheetKind.TextOrTable => russian ? "текст / таблица" : "text / table",
        _ => russian ? "чертёж" : "drawing"
    };

    // ───────────────────────────── Растеризация ─────────────────────────────

    private static async Task<InMemoryRandomAccessStream> RenderAsync(PdfPage page, uint longSide, Guid encoderId)
    {
        var size = page.Size;
        double scale = longSide / Math.Max(1.0, Math.Max(size.Width, size.Height));
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * scale)),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            BitmapEncoderId = encoderId
        };
        var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, options);
        output.Seek(0);
        return output;
    }

    /// <summary>
    /// PNG → JPEG ровно longSide px по длинной стороне: PdfPage растеризует в DIP и на экранах 125–200 % отдаёт
    /// картинку крупнее заказанной.
    /// </summary>
    private static async Task<byte[]> ToJpegAsync(IRandomAccessStream png, double quality, uint longSide)
    {
        png.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(png);
        double scale = Math.Min(1.0, longSide / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output,
            [new KeyValuePair<string, BitmapTypedValue>("ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single))]);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform.ScaledWidth, transform.ScaledHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        return await ReadAllAsync(output);
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
    {
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
