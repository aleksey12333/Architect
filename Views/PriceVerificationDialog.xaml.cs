using AIArchitectStudio.Models.Economics;
using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio.Views;

/// <summary>
/// Веб-импорт прайса и обязательная проверка у пользователя. Этап 1 «Веб-импорт прайса по ссылке»: ссылки на страницы магазинов,
/// прошлые проекты и сметы, вставленный текст, индексация прошлых цен. Этап 2 «Проверка подтянутых расценок — всё ли верно?»:
/// таблица (отметка · материал в проекте · найденное наименование и пересчёт · ед. · старая · новая цена (правится) · Δ % · магазин /
/// источник · достоверность), сводка «Было → Станет» по стоимости каркаса и вопрос-подтверждение. Расценки применяются только кнопкой
/// «Да, всё верно — применить в смету»; «Отмена» оставляет смету без изменений.
/// </summary>
public sealed partial class PriceVerificationDialog : ContentDialog
{
    private readonly MainViewModel _vm;
    private readonly LocalizationService _loc;
    private readonly nint _hwnd;
    private readonly List<string> _paths = [];
    private PriceExtractionBatch? _batch;
    private TextBox? _urls, _text;
    private NumberBox? _index;
    private TextBlock? _filesText, _status, _summary;
    private double _before;

    /// <summary>Обработчик HTTP для съёмки и автотеста (страница магазина без сети); null — обычная загрузка.</summary>
    public HttpMessageHandler? Handler { get; set; }

    /// <summary>Проверенный пакет (после этапа 2); null — расценки не извлекались.</summary>
    public PriceExtractionBatch? Batch => _batch;

    /// <summary>Пользователь нажал «Да, всё верно — применить в смету».</summary>
    public bool Applied { get; private set; }

    public PriceVerificationDialog(MainViewModel viewModel, nint hwnd, PriceImportRequest? request = null, PriceExtractionBatch? batch = null)
    {
        _vm = viewModel;
        _loc = viewModel.Loc;
        _hwnd = hwnd;
        InitializeComponent();
        CloseButtonText = _loc.Get("W3PriceCancel");
        PrimaryButtonClick += OnPrimaryClick;
        Opened += (_, _) => FocusSink.Focus(FocusState.Programmatic);
        if (batch is not null) ShowBatch(batch);
        else
        {
            ShowSources(request);
            // Источники пришли из задания агента — извлечение сразу, пользователю остаётся проверить и подтвердить.
            if (request is { IsEmpty: false }) Opened += async (_, _) => await ExtractAsync();
        }
    }

    // ───────────────────────────── Этап 1: источники ─────────────────────────────

    private void ShowSources(PriceImportRequest? request)
    {
        TitleText.Text = _loc.Get("W3PriceSourcesTitle");
        SubtitleText.Text = _loc.Get("W3PriceSourcesHint");
        PrimaryButtonText = _loc.Get("W3PriceExtract");
        BodyPanel.Children.Clear();

        BodyPanel.Children.Add(Caption(_loc.Get("W3PriceUrls")));
        _urls = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, FontSize = 12, FontFamily = Mono,
            PlaceholderText = "https://…", Text = request is null ? string.Empty : string.Join(Environment.NewLine, request.Urls)
        };
        BodyPanel.Children.Add(_urls);

        BodyPanel.Children.Add(Caption(_loc.Get("W3PriceFiles")));
        var files = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        files.Children.Add(ActionButton("\uE8E5", _loc.Get("W3PricePickFiles"), async (_, _) => await PickFilesAsync()));
        if (request is not null) _paths.AddRange(request.Paths);
        _filesText = Text(FilesCaption(), 11.5, secondary: true, wrap: true);
        _filesText.MaxWidth = 900;
        files.Children.Add(_filesText);
        BodyPanel.Children.Add(files);

        BodyPanel.Children.Add(Caption(_loc.Get("W3PriceText")));
        _text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, FontSize = 12, Text = request?.Text ?? string.Empty };
        BodyPanel.Children.Add(_text);

        var index = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        index.Children.Add(Text(_loc.Get("W3PriceIndex"), 12));
        _index = new NumberBox { Value = request?.IndexPercent ?? 0, Minimum = -50, Maximum = 200, SmallChange = 1, Width = 120, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        index.Children.Add(_index);
        BodyPanel.Children.Add(index);

        _status = Text(string.Empty, 11.5, secondary: true, wrap: true);
        FooterPanel.Children.Clear();
        FooterPanel.Children.Add(_status);
    }

    private string FilesCaption() => _paths.Count == 0 ? _loc.Get("W3PriceNoFiles") : string.Join(" · ", _paths.Select(Path.GetFileName));

    private async Task PickFilesAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in new[] { ".aia", ".csv", ".json", ".txt" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var picked = await picker.PickMultipleFilesAsync();
        if (picked is null) return;
        foreach (var f in picked) if (!_paths.Contains(f.Path, StringComparer.OrdinalIgnoreCase)) _paths.Add(f.Path);
        if (_filesText is not null) _filesText.Text = FilesCaption();
    }

    private PriceImportRequest CurrentRequest() => new(
        (_urls?.Text ?? string.Empty).Split(['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries).ToList(),
        _paths.ToList(),
        _text?.Text ?? string.Empty,
        _index is { } n && double.IsFinite(n.Value) ? n.Value : 0);

    private async Task ExtractAsync()
    {
        var request = CurrentRequest();
        if (request.IsEmpty)
        {
            if (_status is not null) _status.Text = _loc.Get("W3PriceNothing");
            return;
        }
        IsPrimaryButtonEnabled = false;
        BusyRing.IsActive = true;
        BusyRing.Visibility = Visibility.Visible;
        if (_status is not null) _status.Text = _loc.Get("W3PriceExtracting");
        try
        {
            ShowBatch(await _vm.ExtractPricesAsync(request, handler: Handler));
        }
        finally
        {
            BusyRing.IsActive = false;
            BusyRing.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = true;
        }
    }

    // ───────────────────────────── Этап 2: проверка ─────────────────────────────

    private void ShowBatch(PriceExtractionBatch batch)
    {
        _batch = batch;
        _before = _vm.FrameTotalWith(null);
        TitleText.Text = _loc.Get("W3PriceVerifyTitle");
        HeaderGlyph.Glyph = "\uE9D5";
        PrimaryButtonText = _loc.Get("W3PriceApply");
        BodyPanel.Children.Clear();
        foreach (var w in batch.Warnings)
        {
            var warn = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            warn.Children.Add(Glyph("\uE7BA", 12, "TextSecondaryBrush"));
            var t = Text(w, 11.5, wrap: true);
            t.MaxWidth = 1080;
            warn.Children.Add(t);
            BodyPanel.Children.Add(warn);
        }
        if (batch.Sources.Count > 0) BodyPanel.Children.Add(Text(string.Join(" · ", batch.Sources), 11, secondary: true, wrap: true));
        BodyPanel.Children.Add(Table(batch));

        FooterPanel.Children.Clear();
        _summary = Text(string.Empty, 13, strong: true, wrap: true);
        FooterPanel.Children.Add(Card(_summary, 10, accent: true));
        var question = Text(_loc.Get("W3PriceQuestion"), 12.5, wrap: true);
        FooterPanel.Children.Add(question);
        Refresh();
    }

    private static readonly double[] ColumnWidths = [30, 150, 270, 46, 96, 110, 64, 230, 110];

    private Grid Table(PriceExtractionBatch batch)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        foreach (double w in ColumnWidths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        string[] headers = ["", _loc.Get("W3PriceColMaterial"), _loc.Get("W3PriceColFound"), _loc.Get("W3PriceColUnit"), _loc.Get("W3PriceColOld"), _loc.Get("W3PriceColNew"),
            _loc.Get("W3PriceColDelta"), _loc.Get("W3PriceColSource"), _loc.Get("W3PriceColConfidence")];
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < headers.Length; c++)
        {
            var h = Text(headers[c], 10.5, strong: true, secondary: true);
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }
        var culture = _loc.Culture;
        int row = 1;
        foreach (var item in batch.Items)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var band = new Border { Background = Res("TrackBrush"), CornerRadius = new CornerRadius(8), Margin = new Thickness(-4, 0, -4, 0) };
            Grid.SetRow(band, row);
            Grid.SetColumnSpan(band, ColumnWidths.Length);
            grid.Children.Add(band);

            var check = new CheckBox { IsChecked = item.IsChecked, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            check.Checked += (_, _) => { item.IsChecked = true; Refresh(); };
            check.Unchecked += (_, _) => { item.IsChecked = false; Refresh(); };
            Place(check, 0);

            var material = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Padding = new Thickness(0, 6, 0, 6) };
            material.Children.Add(Text(item.ProjectTitle, 12, strong: true));
            material.Children.Add(Text(item.InEstimate ? item.Category : _loc.Get("W3PriceReference"), 10.5, secondary: true, wrap: true));
            Place(material, 1);

            var found = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Padding = new Thickness(0, 6, 0, 6) };
            found.Children.Add(Text(item.FoundName, 11.5, wrap: true));
            string source = $"{item.SourcePrice.ToString("N2", culture)} ₽" + (item.SourceUnit.Length > 0 ? $"/{item.SourceUnit}" : string.Empty) + $" · {item.Method}";
            found.Children.Add(Text(source, 10.5, secondary: true, wrap: true));
            if (item.Conversion.Length > 0)
            {
                var conv = Text(item.Conversion, 10.5, wrap: true, mono: true);
                conv.Foreground = Res("AccentBrush");
                found.Children.Add(conv);
            }
            Place(found, 2);

            Place(Text(item.Unit, 12), 3);
            var old = Text(item.OldPriceRub is { } o ? o.ToString("N0", culture) : "—", 12, mono: true);
            old.HorizontalAlignment = HorizontalAlignment.Right;
            Place(old, 4);

            var delta = Text(string.Empty, 12, strong: true, mono: true);
            delta.HorizontalAlignment = HorizontalAlignment.Right;
            void UpdateDelta()
            {
                delta.Text = item.OldPriceRub is > 0 ? (item.DeltaPercent >= 0 ? "+" : "−") + Math.Abs(item.DeltaPercent).ToString("0.0", culture) : "—";
                delta.Foreground = item.DeltaPercent > 0.05 ? Hex("#FF9F0A") : item.DeltaPercent < -0.05 ? Hex("#30D158") : Res("TextSecondaryBrush");
            }
            var price = new TextBox
            {
                Text = item.NewPriceRub.ToString("N2", culture), FontSize = 12, FontFamily = Mono, MinHeight = 28, Padding = new Thickness(8, 4, 8, 4),
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6)
            };
            price.TextChanged += (_, _) =>
            {
                if (!MaterialPriceCatalog.TryParsePrice(price.Text, out var v) || v < 0) return;
                item.NewPriceRub = v;
                UpdateDelta();
                Refresh();
            };
            Place(price, 5);
            UpdateDelta();
            Place(delta, 6);

            var shop = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            shop.Children.Add(Text(item.Supplier, 11.5, strong: true));
            var doc = Text(item.SourceDocument, 10, secondary: true, wrap: true);
            doc.MaxHeight = 42;
            shop.Children.Add(doc);
            Place(shop, 7);

            var confidence = Chip(item.ConfidenceText, item.ConfidenceColorHex, 10);
            confidence.HorizontalAlignment = HorizontalAlignment.Left;
            Place(confidence, 8);
            row++;

            void Place(FrameworkElement el, int column)
            {
                Grid.SetRow(el, row);
                Grid.SetColumn(el, column);
                if (el.VerticalAlignment == VerticalAlignment.Stretch) el.VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(el);
            }
        }
        return grid;
    }

    private void Refresh()
    {
        if (_batch is null || _summary is null) return;
        var c = _loc.Culture;
        double after = _vm.FrameTotalAfter(_batch);
        double diff = after - _before;
        _summary.Text = _loc.Format("W3PriceSummary", _before.ToString("N0", c), after.ToString("N0", c), (diff >= 0 ? "+" : "−") + Math.Abs(diff).ToString("N0", c));
        SubtitleText.Text = _loc.Format("W3PriceVerifySubtitle", _batch.Items.Count, _batch.Sources.Count, _batch.Checked.Count());
        IsPrimaryButtonEnabled = _batch.Checked.Any();
    }

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_batch is null)
        {
            // Этап 1: окно остаётся открытым — извлечение и переход к проверке.
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                await ExtractAsync();
            }
            finally
            {
                deferral.Complete();
            }
            return;
        }
        _vm.ApplyPriceBatch(_batch);
        Applied = true;
    }

#if DEBUG
    /// <summary>Съёмка: извлечь по текущим источникам без нажатия кнопки.</summary>
    public Task DebugExtractAsync() => ExtractAsync();
#endif
}
