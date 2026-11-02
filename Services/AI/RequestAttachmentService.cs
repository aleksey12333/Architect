using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AIArchitectStudio.Services.AI;

/// <summary>Вид вложения запроса: авто-снимок 3D-вида, проблемный узел, поэтажный план или эскиз.</summary>
public enum AttachmentKind
{
    ViewportSnapshot,
    NodeSnapshot,
    PlanImage,
    Sketch
}

/// <summary>Изображение, прикреплённое к запросу проектировщику (JPEG/PNG в памяти — без буфера обмена пользователя).</summary>
public sealed class RequestAttachment
{
    public string Id { get; init; } = string.Empty;
    public AttachmentKind Kind { get; init; }
    public byte[] Bytes { get; init; } = [];
    public string MimeType { get; init; } = "image/jpeg";
    public string Title { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    /// <summary>Снимок обновляется сам при каждой отправке (переключатель «Авто-снимок вида»).</summary>
    public bool IsAuto { get; init; }

    public string FileName => Kind switch
    {
        AttachmentKind.ViewportSnapshot => $"scene-view-{CapturedAt:HHmmss}.jpg",
        AttachmentKind.NodeSnapshot => $"scene-node-{CapturedAt:HHmmss}.jpg",
        _ => Path.GetFileNameWithoutExtension(Title) + (MimeType == "image/png" ? ".png" : ".jpg")
    };

    public AiImage ToAiImage() => new(Bytes, MimeType, Title);
}

/// <summary>
/// Визуальный контекст запроса: кадр вьюпорта (чистый рендер без интерфейса) или файл плана/эскиза кодируется в JPEG
/// и прикладывается к запросу как <see cref="AiImage"/> — облачным моделям в теле запроса, веб-проектировщику —
/// программной вставкой файла в поле ввода сайта (<see cref="WebAccountBridgeProvider.AttachImagesScript"/>), без Ctrl+V.
/// </summary>
public static class RequestAttachmentService
{
    /// <summary>Длинная сторона авто-снимка, px.</summary>
    public const int SnapshotLongSide = 1600;

    public const int MaxAttachments = 4;

    /// <summary>Кадр BGRA8 → JPEG (качество 0.88).</summary>
    public static async Task<RequestAttachment> FromPixelsAsync(byte[] bgra, int width, int height, AttachmentKind kind, string title, bool isAuto)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.88, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return new RequestAttachment
        {
            Id = "att_" + Guid.NewGuid().ToString("N")[..8], Kind = kind, Bytes = bytes, MimeType = "image/jpeg", Title = title, Width = width, Height = height, IsAuto = isAuto
        };
    }

    /// <summary>Файл плана или эскиза (уменьшение до 2048 px — <see cref="ImagePreparer"/>).</summary>
    public static async Task<RequestAttachment> FromFileAsync(string path, AttachmentKind kind)
    {
        var (bytes, mime) = await ImagePreparer.PrepareAsync(path);
        var (w, h) = await SizeAsync(bytes);
        return new RequestAttachment { Id = "att_" + Guid.NewGuid().ToString("N")[..8], Kind = kind, Bytes = bytes, MimeType = mime, Title = Path.GetFileName(path), Width = w, Height = h };
    }

    public static async Task<(int Width, int Height)> SizeAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    /// <summary>Подпись вложений для пользовательской части промпта (что на изображениях и как их использовать).</summary>
    public static string DescribeForPrompt(IReadOnlyList<RequestAttachment> attachments)
    {
        if (attachments.Count == 0) return string.Empty;
        var lines = attachments.Select((a, i) => $"{i + 1}) " + a.Kind switch
        {
            AttachmentKind.ViewportSnapshot => $"снимок текущего 3D-вида модели ({a.Width}×{a.Height} px) — ориентир по существующей сцене, не чертёж",
            AttachmentKind.NodeSnapshot => $"снимок проблемного узла модели с выделением ({a.Width}×{a.Height} px)",
            AttachmentKind.PlanImage => $"поэтажный план «{a.Title}» — размеры и привязки брать с плана",
            _ => $"эскиз «{a.Title}»"
        });
        return "ПРИЛОЖЕННЫЕ ИЗОБРАЖЕНИЯ:\n" + string.Join("\n", lines);
    }
}
