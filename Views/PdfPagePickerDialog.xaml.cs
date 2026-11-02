using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Pdf;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace AIArchitectStudio.Views;

/// <summary>Страница PDF в диалоге выбора: миниатюра, номер, вид листа и отметка «отправить ИИ».</summary>
public sealed partial class PdfPageItem : ObservableObject
{
    public int Index { get; set; }
    public string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string KindText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BitmapImage? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public PdfPageAnalysis? Analysis { get; set; }
}

/// <summary>
/// Многостраничный инспектор PDF: миниатюры всех страниц, «Автовыбор листов с чертежами» (пропуск обложек и пустых
/// листов), ручные галочки, тип материала и комментарий для ИИ. Справа — «Предпросмотр параметров здания (верификация
/// перед 3D)»: шаги осей, отметки уровней и сечения, прочитанные из векторного слоя выбранных листов
/// (<see cref="PdfNativeContentExtractor"/>); их можно поправить и построить 3D через ИИ или прямой компиляцией без ИИ.
/// </summary>
public sealed partial class PdfPagePickerDialog : ContentDialog
{
    private const int MaxPreviewPages = 80;

    private readonly string _path;
    private readonly LocalizationService _loc;
    private bool _closed;
    private PdfNativeDocument? _native;
    private bool _filling;
    private bool _userEdited;
    private readonly HashSet<string> _edited = [];
    private List<string> _validation = [];

    public ObservableCollection<PdfPageItem> Pages { get; } = [];

    public PdfDocument? Pdf { get; private set; }

    public IReadOnlyList<int> SelectedPages => Pages.Where(p => p.IsSelected).Select(p => p.Index).ToList();

    public BlueprintKind Kind => KindBox.SelectedIndex switch
    {
        1 => BlueprintKind.Structural,
        2 => BlueprintKind.Architectural,
        3 => BlueprintKind.SketchOrPhoto,
        _ => BlueprintKind.Auto
    };

    public string Comment => CommentBox.Text.Trim();

    /// <summary>Факты векторного слоя выбранных листов (размерные цепочки, отметки, марки осей, сечения).</summary>
    public ExtractedBlueprintAnnotations? Facts { get; private set; }

    /// <summary>Проверенная таблица параметров (null — поля не разобраны).</summary>
    public BlueprintDefinition? Definition { get; private set; }

    /// <summary>Таблица готова к прямой компиляции без ИИ.</summary>
    public bool IsDefinitionValid => Definition is not null && _validation.Count == 0;

    public PdfPagePickerDialog(string path, LocalizationService loc, BlueprintKind initialKind)
    {
        _path = path;
        _loc = loc;
        InitializeComponent();

        Title = loc.Format("PdfDialogTitle", Path.GetFileName(path), "…");
        PrimaryButtonText = loc.Get("PdfBuildAi");
        SecondaryButtonText = loc.Get("PdfBuildDirect");
        CloseButtonText = loc.Get("Cancel");
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        AutoText.Text = loc.Get("PdfAutoSelect");
        AllButton.Content = loc.Get("PdfSelectAll");
        NoneButton.Content = loc.Get("PdfSelectNone");
        HintText.Text = loc.Get("PdfAutoHint");
        CommentBox.PlaceholderText = loc.Get("PdfComment");
        foreach (var key in new[] { "PdfKindAuto", "PdfKindStructural", "PdfKindArchitectural", "PdfKindSketch" })
            KindBox.Items.Add(loc.Get(key));
        KindBox.SelectedIndex = initialKind switch
        {
            BlueprintKind.Structural => 1,
            BlueprintKind.Architectural => 2,
            BlueprintKind.SketchOrPhoto => 3,
            _ => 0
        };
        PagesView.ItemsSource = Pages;

        VerifyTitle.Text = loc.Get("PdfVerifyTitle");
        VerifySubtitle.Text = loc.Get("PdfVerifySubtitle");
        RefreshText.Text = loc.Get("PdfVerifyRefresh");
        ToolTipService.SetToolTip(RefreshButton, loc.Get("PdfVerifyRefreshTip"));
        SourceText.Text = loc.Get("PdfVerifyReading");
        XStepsLabel.Text = loc.Get("PdfVerifyXSteps");
        ZStepsLabel.Text = loc.Get("PdfVerifyZSteps");
        LevelsLabel.Text = loc.Get("PdfVerifyLevels");
        ColumnsLabel.Text = loc.Get("PdfVerifyColumns");
        BeamsLabel.Text = loc.Get("PdfVerifyBeams");
        SlabLabel.Text = loc.Get("PdfVerifySlab");
        WallsLabel.Text = loc.Get("PdfVerifyWalls");
        FoundationLabel.Text = loc.Get("PdfVerifyFoundation");
        RoofLabel.Text = loc.Get("PdfVerifyRoof");
        ParapetLabel.Text = loc.Get("PdfVerifyParapet");
        _filling = true;
        foreach (var key in new[] { "PdfProfileRect", "PdfProfileCircle", "PdfProfilePipe", "PdfProfileIBeam" }) ColumnProfileBox.Items.Add(loc.Get(key));
        foreach (var key in new[] { "PdfRoofFlat", "PdfRoofParapet", "PdfRoofGable", "PdfRoofHip" }) RoofBox.Items.Add(loc.Get(key));
        Fill(BlueprintDefinition.FromAnnotations(null, GroupName()));
        _filling = false;
        Validate();

        Opened += async (_, _) => await LoadPagesAsync();
        Closed += (_, _) => _closed = true;
    }

    private string GroupName() => Path.GetFileNameWithoutExtension(_path);

    private async Task LoadPagesAsync()
    {
        try
        {
            Pdf = await PdfBlueprintExtractor.OpenAsync(_path);
            int total = (int)Pdf.PageCount;
            Title = _loc.Format("PdfDialogTitle", Path.GetFileName(_path), total);
            int shown = Math.Min(total, MaxPreviewPages);
            for (int i = 0; i < shown; i++)
            {
                var item = new PdfPageItem { Index = i, Title = _loc.Format("PdfPageShort", i + 1) };
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PdfPageItem.IsSelected)) UpdateCount();
                };
                Pages.Add(item);
            }

            // Векторный слой читается в фоне параллельно с растровыми миниатюрами.
            var pages = Enumerable.Range(0, shown).ToList();
            var nativeTask = Task.Run(() => PdfNativeContentExtractor.Read(_path, pages));

            for (int i = 0; i < shown && !_closed; i++)
            {
                LoadingText.Text = _loc.Format("PdfAnalyzing", i + 1, shown);
                var analysis = await PdfBlueprintExtractor.AnalyzeAsync(Pdf, i);
                var item = Pages[i];
                item.Analysis = analysis;
                item.KindText = PdfBlueprintExtractor.KindTitle(analysis.Kind, _loc.IsRussian);
                item.Thumbnail = await ToBitmapAsync(analysis.ThumbnailPng);
                if (i == 0) LoadingPanel.Visibility = Visibility.Collapsed;
            }
            LoadingPanel.Visibility = Visibility.Collapsed;
            AutoSelect();
            if (total > shown) HintText.Text = _loc.Get("PdfAutoHint") + " · " + _loc.Format("PdfTooMany", shown);

            try
            {
                _native = await nativeTask;
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or InvalidOperationException or ArgumentException
                                           or IndexOutOfRangeException or OverflowException or NotSupportedException)
            {
                // Векторный слой — уточнение: при сбое разбора (повреждённый или нестандартный PDF) листы остаются растровыми.
                App.WriteCrashLog("pdf-native", ex);
                _native = new PdfNativeDocument();
            }
            RefreshFromPdf(force: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingText.Text = _loc.Format("ErrorImageRead", ex.Message);
        }
    }

    private static async Task<BitmapImage> ToBitmapAsync(byte[] png)
    {
        var bitmap = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void AutoSelect()
    {
        var analyses = Pages.Where(p => p.Analysis is not null).Select(p => p.Analysis!).ToList();
        var chosen = PdfBlueprintExtractor.AutoSelect(analyses).ToHashSet();
        foreach (var p in Pages) p.IsSelected = chosen.Contains(p.Index);
        UpdateCount();
    }

    private void UpdateCount()
    {
        int selected = Pages.Count(p => p.IsSelected);
        CountText.Text = _loc.Format("PdfSelectedCount", selected, Pdf?.PageCount ?? (uint)Pages.Count, PdfBlueprintExtractor.MaxPagesForAi);
        IsPrimaryButtonEnabled = selected > 0;
        // Пока пользователь не правил таблицу — она следует за выбором листов.
        if (!_userEdited) RefreshFromPdf(force: false);
    }

    private void OnAutoSelectClick(object sender, RoutedEventArgs e) => AutoSelect();

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var p in Pages) p.IsSelected = true;
        UpdateCount();
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var p in Pages) p.IsSelected = false;
        UpdateCount();
    }

    private void OnPageClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PdfPageItem item) item.IsSelected = !item.IsSelected;
        UpdateCount();
    }

    // ───────────────────────────── Верификация параметров ─────────────────────────────

    private void OnRefreshFromPdfClick(object sender, RoutedEventArgs e)
    {
        _userEdited = false;
        _edited.Clear();
        RefreshFromPdf(force: true);
    }

    /// <summary>Факты векторного слоя выбранных листов → таблица (если пользователь её ещё не правил).</summary>
    private void RefreshFromPdf(bool force)
    {
        if (_native is null || (_userEdited && !force)) return;
        var selected = SelectedPages;
        Facts = BlueprintAnnotationAnalyzer.Analyze(_native, selected.Count > 0 ? selected : null);
        _filling = true;
        Fill(BlueprintDefinition.FromAnnotations(Facts, GroupName()));
        _filling = false;
        Validate();
    }

    private void Fill(BlueprintDefinition d)
    {
        XStepsBox.Text = BlueprintDefinition.FormatSteps(d.XStepsMm);
        ZStepsBox.Text = BlueprintDefinition.FormatSteps(d.ZStepsMm);
        LevelsBox.Text = BlueprintDefinition.FormatLevels(d.LevelElevationsM);
        ColumnProfileBox.SelectedIndex = d.ColumnProfile switch
        {
            ProfileTypes.Circle => 1,
            ProfileTypes.Pipe => 2,
            ProfileTypes.IBeam => 3,
            _ => 0
        };
        ColumnWBox.Text = Int(d.ColumnWidthMm);
        ColumnDBox.Text = Int(d.ColumnDepthMm);
        BeamWBox.Text = Int(d.BeamWidthMm);
        BeamHBox.Text = Int(d.BeamHeightMm);
        SlabBox.Text = Int(d.SlabThicknessMm);
        WallsBox.Text = Int(d.WallThicknessMm);
        FoundationBox.Text = Int(d.FoundationThicknessMm);
        ParapetTBox.Text = Int(d.ParapetThicknessMm);
        ParapetHBox.Text = Int(d.ParapetHeightMm);
        RoofBox.SelectedIndex = (int)d.Roof;
        _axisLabels = (d.XAxisLabels, d.ZAxisLabels, d.XStepsMm.Count, d.ZStepsMm.Count);
        _fromPdf = [.. d.FromPdf];
    }

    private (List<string> X, List<string> Z, int XCount, int ZCount) _axisLabels = ([], [], 0, 0);
    private List<string> _fromPdf = [];

    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);

    private void OnFieldChanged(object sender, TextChangedEventArgs e) => OnEdited(sender);

    private void OnFieldSelectionChanged(object sender, SelectionChangedEventArgs e) => OnEdited(sender);

    private void OnEdited(object sender)
    {
        if (_filling) return;
        _userEdited = true;
        if (sender is FrameworkElement { Tag: string tag }) _edited.Add(tag);
        Validate();
    }

    /// <summary>Разбор полей, подсказки (суммы, этажность) и итог проверки; кнопка прямой компиляции — только при валидной таблице.</summary>
    private void Validate()
    {
        bool ru = _loc.IsRussian;
        var problems = new List<string>();
        var x = BlueprintDefinition.ParseSteps(XStepsBox.Text, out var xErr);
        var z = BlueprintDefinition.ParseSteps(ZStepsBox.Text, out var zErr);
        var levels = BlueprintDefinition.ParseLevels(LevelsBox.Text, out var lErr);
        if (xErr is not null) problems.Add(_loc.Get("PdfFieldX") + ": " + _loc.Format("PdfVerifyParseError", xErr));
        if (zErr is not null) problems.Add(_loc.Get("PdfFieldZ") + ": " + _loc.Format("PdfVerifyParseError", zErr));
        if (lErr is not null) problems.Add(_loc.Get("PdfFieldLevels") + ": " + _loc.Format("PdfVerifyParseError", lErr));

        int? Field(TextBox box, string key)
        {
            var text = box.Text.Trim();
            if (text.Length == 0) return 0;
            if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0 && v < 100000) return (int)Math.Round(v);
            problems.Add(_loc.Get(key) + ": " + _loc.Format("PdfVerifyParseError", text));
            return null;
        }
        var cw = Field(ColumnWBox, "PdfFieldColumn");
        var cd = Field(ColumnDBox, "PdfFieldColumn");
        var bw = Field(BeamWBox, "PdfFieldBeam");
        var bh = Field(BeamHBox, "PdfFieldBeam");
        var slab = Field(SlabBox, "PdfFieldSlab");
        var wall = Field(WallsBox, "PdfFieldWall");
        var foundation = Field(FoundationBox, "PdfFieldFoundation");
        var pt = Field(ParapetTBox, "PdfFieldParapet");
        var ph = Field(ParapetHBox, "PdfFieldParapet");

        XSumText.Text = x is { Count: > 0 } ? _loc.Format("PdfVerifySum", ExtractedBlueprintAnnotations.Mm(x.Sum()), x.Count + 1) : string.Empty;
        ZSumText.Text = z is { Count: > 0 } ? _loc.Format("PdfVerifySum", ExtractedBlueprintAnnotations.Mm(z.Sum()), z.Count + 1) : string.Empty;
        LevelsInfoText.Text = levels is { Count: >= 2 }
            ? _loc.Format("PdfVerifyStoreys", levels.Count - 1, ExtractedBlueprintAnnotations.ElevationText(levels[0]), ExtractedBlueprintAnnotations.ElevationText(levels[^1]))
            : _loc.Get("PdfVerifyLevelsHint");
        ParapetTBox.IsEnabled = ParapetHBox.IsEnabled = RoofBox.SelectedIndex == (int)BlueprintRoofType.FlatWithParapet;
        LevelsInfoText.FontFamily = levels is { Count: >= 2 } ? (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["MonoFont"] : new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Text");

        if (problems.Count == 0 && x is not null && z is not null && levels is not null && cw is { } w && cd is { } d && bw is { } bwv && bh is { } bhv &&
            slab is { } s && wall is { } wl && foundation is { } f && pt is { } ptv && ph is { } phv)
        {
            var def = new BlueprintDefinition
            {
                XStepsMm = x,
                ZStepsMm = z,
                LevelElevationsM = levels,
                ColumnProfile = ColumnProfileBox.SelectedIndex switch { 1 => ProfileTypes.Circle, 2 => ProfileTypes.Pipe, 3 => ProfileTypes.IBeam, _ => ProfileTypes.Rectangle },
                ColumnWidthMm = w,
                ColumnDepthMm = d,
                BeamWidthMm = bwv,
                BeamHeightMm = bhv,
                SlabThicknessMm = s,
                WallThicknessMm = wl,
                FoundationThicknessMm = f,
                ParapetThicknessMm = ptv,
                ParapetHeightMm = phv,
                Roof = (BlueprintRoofType)Math.Max(0, RoofBox.SelectedIndex),
                GroupName = GroupName()
            };
            // Марки осей с листа сохраняются, пока число шагов не изменилось.
            if (x.Count == _axisLabels.XCount) def.XAxisLabels = [.. _axisLabels.X];
            if (z.Count == _axisLabels.ZCount) def.ZAxisLabels = [.. _axisLabels.Z];
            foreach (var key in _fromPdf.Concat(_edited)) def.Confirmed.Add(key);
            def.FromPdf.AddRange(_fromPdf);
            Definition = def;
            problems.AddRange(def.Validate(ru));
        }
        else Definition = null;
        _validation = problems;

        // Источник: что прочитано из PDF, что осталось типовым.
        var allKeys = new[] { "X", "Z", "levels", "column", "beam", "slab", "wall", "foundation" };
        string Title(string key) => _loc.Get(key switch
        {
            "X" => "PdfFieldX", "Z" => "PdfFieldZ", "levels" => "PdfFieldLevels", "column" => "PdfFieldColumn", "beam" => "PdfFieldBeam",
            "slab" => "PdfFieldSlab", "wall" => "PdfFieldWall", "parapet" => "PdfFieldParapet", _ => "PdfFieldFoundation"
        });
        var fromPdf = _fromPdf.Where(k => allKeys.Contains(k) || k == "parapet").Select(Title).ToList();
        var typical = allKeys.Where(k => !_fromPdf.Contains(k) && !_edited.Contains(k) && k is not ("X" or "Z" or "levels")).Select(Title).ToList();
        var source = new List<string>();
        if (Facts is not null) source.Add(Facts.Summary(ru));
        if (fromPdf.Count > 0) source.Add(_loc.Format("PdfVerifyFromPdf", string.Join(", ", fromPdf)));
        if (typical.Count > 0) source.Add(_loc.Format("PdfVerifyDefaults", string.Join(", ", typical)));
        if (_native is not null) SourceText.Text = string.Join("\n", source);

        bool ok = problems.Count == 0 && Definition is not null;
        StatusGlyph.Glyph = ok ? "" : "";
        StatusGlyph.Foreground = XamlHelpers.SeverityBrush(critical: false);
        if (ok) StatusGlyph.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"];
        StatusText.Text = ok
            ? _loc.Format("PdfVerifyReady", Definition!.WidthM.ToString("0.000", CultureInfo.InvariantCulture), Definition.DepthM.ToString("0.000", CultureInfo.InvariantCulture), Definition.StoreyCount)
            : _loc.Format("PdfVerifyInvalid", string.Join("; ", problems.Take(3)));
        IsSecondaryButtonEnabled = ok;
    }

#if DEBUG
    /// <summary>Только Debug (AIA_DEBUG_PDF): дождаться анализа страниц и векторного слоя для съёмки диалога.</summary>
    public async Task WaitLoadedAsync()
    {
        while (!_closed && (Pdf is null || Pages.Any(p => p.Analysis is null) || _native is null)) await Task.Delay(100);
    }
#endif
}
