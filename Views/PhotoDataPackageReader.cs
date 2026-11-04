using System.Runtime.InteropServices.WindowsRuntime;
using AIArchitectStudio.Services.Photo;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AIArchitectStudio.Views;

/// <summary>
/// Снимок из буфера обмена или перетаскивания (Drag &amp; Drop из браузера и проводника): картинка (Bitmap), файл изображения,
/// ссылка (WebLink / Uri / текст), HTML-фрагмент браузера (img src, srcset, og:image) — ссылка скачивается <see cref="PhotoImageLoader"/>.
/// </summary>
public static class PhotoDataPackageReader
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"];

    public static bool MayContainImage(DataPackageView view) =>
        view.Contains(StandardDataFormats.Bitmap) || view.Contains(StandardDataFormats.StorageItems) || view.Contains(StandardDataFormats.WebLink) ||
        view.Contains(StandardDataFormats.Uri) || view.Contains(StandardDataFormats.Html) || view.Contains(StandardDataFormats.Text);

    /// <summary>Снимок из пакета данных; null — изображения нет.</summary>
    public static async Task<LoadedPhoto?> ReadAsync(DataPackageView view, HttpMessageHandler? handler = null)
    {
        if (view.Contains(StandardDataFormats.StorageItems))
            foreach (var item in await view.GetStorageItemsAsync())
                if (item is StorageFile file && ImageExtensions.Contains(Path.GetExtension(file.Path).ToLowerInvariant()))
                    return await PhotoImageLoader.FromFileAsync(file.Path);
        if (view.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await view.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            if (bytes.Length > 0) return new LoadedPhoto(bytes, PhotoImageLoader.SniffMime(bytes) ?? "image/png", "Снимок из буфера обмена.png", null);
        }
        if (view.Contains(StandardDataFormats.Html))
        {
            string html = await view.GetHtmlFormatAsync();
            string fragment = HtmlFormatHelper.GetStaticFragment(html);
            Uri? baseUri = null;
            var source = System.Text.RegularExpressions.Regex.Match(html, @"SourceURL:(\S+)");
            if (source.Success) Uri.TryCreate(source.Groups[1].Value, UriKind.Absolute, out baseUri);
            if (PhotoImageLoader.ExtractImageUrl(string.IsNullOrEmpty(fragment) ? html : fragment, baseUri) is { } imageUrl)
                return await PhotoImageLoader.FromUrlAsync(imageUrl, handler);
        }
        foreach (var format in new[] { StandardDataFormats.WebLink, StandardDataFormats.Uri })
            if (view.Contains(format))
            {
                var uri = format == StandardDataFormats.WebLink ? await view.GetWebLinkAsync() : await view.GetUriAsync();
                if (uri is not null && PhotoImageLoader.LooksLikeUrl(uri.ToString())) return await PhotoImageLoader.FromUrlAsync(uri.ToString(), handler);
            }
        if (view.Contains(StandardDataFormats.Text))
        {
            string text = (await view.GetTextAsync()).Trim();
            if (PhotoImageLoader.LooksLikeUrl(text)) return await PhotoImageLoader.FromUrlAsync(text, handler);
        }
        return null;
    }
}
