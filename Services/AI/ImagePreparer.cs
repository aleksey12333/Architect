using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AIArchitectStudio.Services.AI;

/// <summary>
/// Подготовка одиночного изображения для Vision: форматы, которые принимают все провайдеры (PNG/JPEG/WEBP),
/// и уменьшение до 2048 px по длинной стороне, чтобы не превышать лимиты запросов.
/// Многостраничные PDF проходят через диалог выбора листов (<see cref="PdfBlueprintExtractor"/>); если PDF всё же
/// передан сюда напрямую, растеризуется самый информативный лист (не титульная обложка).
/// </summary>
public static class ImagePreparer
{
    private const uint MaxSide = 2048;
    private const long MaxBytes = 4 * 1024 * 1024;

    public static async Task<(byte[] Bytes, string MimeType)> PrepareAsync(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        if (ext == ".pdf")
        {
            var pdf = await PdfBlueprintExtractor.OpenAsync(path);
            var pages = new List<PdfPageAnalysis>();
            for (int i = 0; i < Math.Min(pdf.PageCount, 40); i++) pages.Add(await PdfBlueprintExtractor.AnalyzeAsync(pdf, i));
            var best = PdfBlueprintExtractor.AutoSelect(pages, 1);
            var images = await PdfBlueprintExtractor.RenderForAiAsync(pdf, best.Count > 0 ? best : [0], MaxSide);
            return (images[0].Bytes, images[0].MimeType);
        }

        var original = await File.ReadAllBytesAsync(path);
        string mime = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => string.Empty
        };
        using var stream = await file.OpenReadAsync();
        return await PrepareStreamAsync(stream, original, mime);
    }

    private static async Task<(byte[] Bytes, string MimeType)> PrepareStreamAsync(IRandomAccessStream stream, byte[] original, string mime)
    {
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        uint width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
        bool tooLarge = Math.Max(width, height) > MaxSide || original.Length > MaxBytes;
        if (mime.Length > 0 && !tooLarge) return (original, mime);

        double scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output,
            [new KeyValuePair<string, BitmapTypedValue>("ImageQuality", new BitmapTypedValue(0.9, Windows.Foundation.PropertyType.Single))]);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform.ScaledWidth, transform.ScaledHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
        return (await ReadAllAsync(output), "image/jpeg");
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
