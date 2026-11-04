using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AIArchitectStudio.Services.Photo;

/// <summary>Загруженное изображение: байты (как пришли), тип, имя для подписи, адрес источника.</summary>
public sealed record LoadedPhoto(byte[] Bytes, string MimeType, string Name, string? SourceUrl);

/// <summary>
/// Загрузка снимка здания из любого источника: прямая ссылка на картинку (http/https, JPEG/PNG/WebP/GIF/BMP), ссылка на страницу
/// (картинка берётся из og:image / twitter:image / link image_src / самой крупной srcset / первого img), HTML-фрагмент при
/// перетаскивании картинки из браузера, data-URI, файл. Заголовки User-Agent и Accept как у браузера, предел 25 МБ, таймаут 30 с;
/// декодирование — системными кодеками Windows (WebP — кодеком «Расширения для изображений WebP», встроен в Windows 11).
/// </summary>
public static partial class PhotoImageLoader
{
    public const long MaxBytes = 25L * 1024 * 1024;
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ArchitectStudioPro/1.0 PhotoImport";

    public static bool LooksLikeUrl(string? text) =>
        text is not null && (text.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             text.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                             text.TrimStart().StartsWith("data:image/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Скачивание по ссылке (картинка или страница с картинкой), handler — для автотеста без сети.</summary>
    public static async Task<LoadedPhoto> FromUrlAsync(string url, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        url = url.Trim();
        if (TryParseDataUri(url, out var dataBytes, out var dataMime)) return new LoadedPhoto(dataBytes, dataMime, "image." + Extension(dataMime), null);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Ссылка должна начинаться с http:// или https://");
        using var client = handler is null ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true }) : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(30);
        for (int hop = 0; hop < 3; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("image/avif,image/webp,image/png,image/jpeg,image/*;q=0.8,text/html;q=0.6,*/*;q=0.5");
            request.Headers.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Сервер ответил {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
            if (response.Content.Headers.ContentLength is > MaxBytes) throw new InvalidDataException("Изображение больше 25 МБ");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.LongLength > MaxBytes) throw new InvalidDataException("Изображение больше 25 МБ");
            string mime = response.Content.Headers.ContentType?.MediaType ?? SniffMime(bytes) ?? "application/octet-stream";
            string? sniffed = SniffMime(bytes);
            if (sniffed is not null) mime = sniffed;
            if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return new LoadedPhoto(bytes, mime, NameFrom(uri, mime), uri.ToString());
            // Страница: картинка из разметки.
            string html = System.Text.Encoding.UTF8.GetString(bytes);
            var next = ExtractImageUrl(html, uri) ?? throw new InvalidDataException("На странице не найдено изображение здания");
            if (TryParseDataUri(next, out var inline, out var inlineMime)) return new LoadedPhoto(inline, inlineMime, NameFrom(uri, inlineMime), uri.ToString());
            uri = new Uri(next);
        }
        throw new InvalidDataException("Слишком много переходов по ссылкам");
    }

    /// <summary>
    /// Адрес картинки в HTML (страница или фрагмент перетаскивания из браузера): og:image, twitter:image, link rel=image_src,
    /// самая крупная запись srcset, src / data-src первого img; относительные адреса — от baseUri.
    /// </summary>
    public static string? ExtractImageUrl(string html, Uri? baseUri)
    {
        string? Resolve(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = WebUtility.HtmlDecode(raw.Trim());
            if (raw.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return raw;
            if (raw.StartsWith("//")) raw = (baseUri?.Scheme ?? "https") + ":" + raw;
            return Uri.TryCreate(baseUri, raw, out var abs) || Uri.TryCreate(raw, UriKind.Absolute, out abs) ? abs.ToString() : null;
        }
        foreach (var pattern in new[] { MetaOg(), MetaTwitter(), LinkImageSrc() })
            if (pattern.Match(html) is { Success: true } m && Resolve(m.Groups["u"].Value) is { } u) return u;
        foreach (Match img in ImgTag().Matches(html))
        {
            string tag = img.Value;
            if (SrcSet().Match(tag) is { Success: true } set)
            {
                var best = set.Groups["v"].Value.Split(',')
                    .Select(part => part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length >= 1)
                    .Select(p => (Url: p[0], W: p.Length > 1 && double.TryParse(p[1].TrimEnd('w', 'x'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w) ? w : 1))
                    .OrderByDescending(p => p.W).FirstOrDefault();
                if (best.Url is not null && Resolve(best.Url) is { } bu) return bu;
            }
            foreach (var attr in new[] { DataSrc(), Src() })
                if (attr.Match(tag) is { Success: true } a && Resolve(a.Groups["u"].Value) is { } su) return su;
        }
        return null;
    }

    public static bool TryParseDataUri(string text, out byte[] bytes, out string mime)
    {
        bytes = [];
        mime = string.Empty;
        var m = DataUri().Match(text.Trim());
        if (!m.Success) return false;
        try
        {
            bytes = Convert.FromBase64String(m.Groups["d"].Value);
            mime = m.Groups["m"].Value.ToLowerInvariant();
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static async Task<LoadedPhoto> FromFileAsync(string path) =>
        new(await File.ReadAllBytesAsync(path), SniffMime(await File.ReadAllBytesAsync(path)) ?? "image/jpeg", Path.GetFileName(path), null);

    /// <summary>Тип по сигнатуре: PNG, JPEG, WebP, GIF, BMP, TIFF; null — не картинка.</summary>
    public static string? SniffMime(byte[] b)
    {
        if (b.Length < 12) return null;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return "image/gif";
        if (b[0] == 'B' && b[1] == 'M') return "image/bmp";
        if (b[0] == 'I' && b[1] == 'I' && b[2] == 42 || b[0] == 'M' && b[1] == 'M' && b[3] == 42) return "image/tiff";
        return null;
    }

    private static string Extension(string mime) => mime switch
    {
        "image/png" => "png",
        "image/webp" => "webp",
        "image/gif" => "gif",
        "image/bmp" => "bmp",
        _ => "jpg"
    };

    private static string NameFrom(Uri uri, string mime)
    {
        string last = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
        if (last.Length is 0 or > 80) last = uri.Host + "." + Extension(mime);
        return last.Contains('.') ? last : last + "." + Extension(mime);
    }

    /// <summary>
    /// Нормализация для моделей проектирования: декодирование любым кодеком, длинная сторона ≤ 1600 px, JPEG 0,9 (единый формат для
    /// облака, локальных моделей и веб-чатов).
    /// </summary>
    public static async Task<(byte[] Jpeg, int Width, int Height, RasterPlanTracer.RasterImage Raster)> NormalizeAsync(byte[] bytes)
    {
        var raster = await RasterPlanTracer.DecodeAsync(bytes);
        double k = Math.Min(1, 1600.0 / Math.Max(raster.Width, raster.Height));
        var scaled = k < 1 ? Resize(raster, (int)Math.Round(raster.Width * k), (int)Math.Round(raster.Height * k)) : raster;
        var jpeg = await EncodeAsync(scaled.Bgra, scaled.Width, scaled.Height, png: false);
        return (jpeg, scaled.Width, scaled.Height, raster);
    }

    public static RasterPlanTracer.RasterImage Resize(RasterPlanTracer.RasterImage src, int w, int h)
    {
        var dst = new byte[w * h * 4];
        double sx = (double)src.Width / w, sy = (double)src.Height / h;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, (int)((x + 1) * sx)), y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, (int)((y + 1) * sy));
                int b = 0, g = 0, r = 0, n = 0;
                for (int yy = y0; yy < y1 && yy < src.Height; yy++)
                    for (int xx = x0; xx < x1 && xx < src.Width; xx++)
                    {
                        int o = (yy * src.Width + xx) * 4;
                        b += src.Bgra[o]; g += src.Bgra[o + 1]; r += src.Bgra[o + 2]; n++;
                    }
                n = Math.Max(1, n);
                int d = (y * w + x) * 4;
                dst[d] = (byte)(b / n); dst[d + 1] = (byte)(g / n); dst[d + 2] = (byte)(r / n); dst[d + 3] = 255;
            }
        return new RasterPlanTracer.RasterImage(dst, w, h);
    }

    /// <summary>BGRA8 → PNG или JPEG (0,9).</summary>
    public static async Task<byte[]> EncodeAsync(byte[] bgra, int width, int height, bool png)
    {
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = png
            ? await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream)
            : await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream,
                new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.9, Windows.Foundation.PropertyType.Single) });
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    [GeneratedRegex(@"<meta[^>]+(?:property|name)\s*=\s*[""']og:image(?::secure_url)?[""'][^>]*content\s*=\s*[""'](?<u>[^""']+)[""']|<meta[^>]+content\s*=\s*[""'](?<u>[^""']+)[""'][^>]*(?:property|name)\s*=\s*[""']og:image[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaOg();

    [GeneratedRegex(@"<meta[^>]+name\s*=\s*[""']twitter:image[""'][^>]*content\s*=\s*[""'](?<u>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTwitter();

    [GeneratedRegex(@"<link[^>]+rel\s*=\s*[""']image_src[""'][^>]*href\s*=\s*[""'](?<u>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex LinkImageSrc();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTag();

    [GeneratedRegex(@"\bsrcset\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex SrcSet();

    [GeneratedRegex(@"\bdata-src\s*=\s*[""'](?<u>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex DataSrc();

    [GeneratedRegex(@"\bsrc\s*=\s*[""'](?<u>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex Src();

    [GeneratedRegex(@"^data:(?<m>image/[a-z0-9.+-]+);base64,(?<d>[A-Za-z0-9+/=\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DataUri();
}
