using System.Globalization;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Economics;
using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AIArchitectStudio.Views;

/// <summary>
/// «Ведомость объёмов»: бетон по конструкциям, арматура по классам и диаметрам, остекление и примерная масса проката, и
/// смета-калькулятор монолитного каркаса: цены бетона (₽/м³) и арматуры (₽/т) пересчитывают итог на лету при вводе, через
/// полсекунды становятся расценками проекта, «Сохранить смету в проект» записывает их в .aia. Пока окно открыто, ведомость
/// пересчитывается вместе со сметой (после каждого экспресс-расчёта сцены). «Копировать» — таблица с табуляциями для Excel;
/// окно при этом не закрывается.
/// </summary>
public sealed partial class QuantityScheduleDialog : ContentDialog
{
    private readonly MainViewModel _vm;
    private readonly LocalizationService _loc;
    private readonly Func<Task<bool>>? _saveProject;
    private readonly Func<Task<string?>>? _pickPriceFile;
    private readonly Func<Task<string?>>? _pickTemplatePath;
    private readonly DispatcherTimer _commitTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private QuantitySchedule _schedule = QuantitySchedule.Empty;
    private PricedEstimate _priced = PricedEstimate.Empty;
    private bool _saving;

    /// <param name="saveProject">Сохранение проекта (текущий путь или выбор файла); true — проект записан.</param>
    /// <param name="pickPriceFile">Выбор файла прайса (CSV / JSON) для загрузки.</param>
    /// <param name="pickTemplatePath">Выбор пути шаблона прайса (CSV для Excel или JSON).</param>
    public QuantityScheduleDialog(MainViewModel viewModel, Func<Task<bool>>? saveProject = null, Func<Task<string?>>? pickPriceFile = null,
        Func<Task<string?>>? pickTemplatePath = null)
    {
        _vm = viewModel;
        _loc = viewModel.Loc;
        _saveProject = saveProject;
        _pickPriceFile = pickPriceFile;
        _pickTemplatePath = pickTemplatePath;
        InitializeComponent();
        PrimaryButtonText = _loc.Get("QtyCopy");
        SecondaryButtonText = saveProject is null ? string.Empty : _loc.Get("CostSave");
        CloseButtonText = _loc.Get("Close");
        CostTitleText.Text = _loc.Get("CostTitle");
        CatalogLabel.Text = _loc.Get("PriceCatalog");
        LoadPriceText.Text = _loc.Get("PriceLoad");
        PullPriceText.Text = _loc.Get("W3PricePull");
        ToolTipService.SetToolTip(PullPriceButton, _loc.Get("W3PricePullTip"));
        TemplateText.Text = _loc.Get("PriceTemplate");
        ToolTipService.SetToolTip(LoadPriceButton, _loc.Get("PriceLoadTip"));
        ToolTipService.SetToolTip(TemplateButton, _loc.Get("PriceTemplateTip"));
        LoadPriceButton.IsEnabled = pickPriceFile is not null;
        TemplateButton.IsEnabled = pickTemplatePath is not null;
        CostTotalCaption.Text = _loc.Get("CostTotal");
        _commitTimer.Tick += (_, _) => CommitPrices();
        Opened += (_, _) => FocusSink.Focus(FocusState.Programmatic);
        Closed += (_, _) =>
        {
            _commitTimer.Stop();
            CommitPrices();
        };
        _vm.PricingChanged += OnPricingChanged;
        Closed += (_, _) => _vm.PricingChanged -= OnPricingChanged;
        TitleText.Text = _loc.Get("QtyTitle");
        ConcreteCaption.Text = _loc.Get("QtyConcrete");
        RebarCaption.Text = _loc.Get("QtyRebar");
        GlazingCaption.Text = _loc.Get("QtyGlazing");
        RolledCaption.Text = _loc.Get("QtyRolled");
        ConcreteTitle.Text = _loc.Get("QtyConcreteTitle");
        GlazingTitle.Text = _loc.Get("QtyGlazingTitle");
        RebarTitle.Text = _loc.Get("QtyRebarTitle");
        RolledTitle.Text = _loc.Get("QtyRolledTitle");
        NoteText.Text = _loc.Get("QtyNote");

        Refresh();
        _vm.Analytics.SpecificationChanged += OnSpecificationChanged;
        Closed += (_, _) => _vm.Analytics.SpecificationChanged -= OnSpecificationChanged;
    }

    private CultureInfo Culture => _loc.Culture;

    private void OnSpecificationChanged(object? sender, BimSpecification e) => DispatcherQueue.TryEnqueue(Refresh);

    private void OnPricingChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var s = _schedule = _vm.BuildQuantitySchedule();
        _priced = _vm.BuildPricedEstimate(s);
        SubtitleText.Text = _loc.Format("QtySubtitle", s.ElementCount, DateTime.Now.ToString("HH:mm:ss", Culture));

        ConcreteValue.Text = s.ConcreteTotal.ToString("N2", Culture);
        ConcreteUnit.Text = _loc.Get("UnitM3");
        (RebarValue.Text, RebarUnit.Text) = Mass(s.Rebar.Total);
        GlazingValue.Text = s.GlazingTotal.ToString("N1", Culture);
        GlazingUnit.Text = _loc.Get("UnitM2");
        (RolledValue.Text, RolledUnit.Text) = Mass(s.RolledSteelTotal);

        // Суммы групп — по цене строки сметы (бетон класса проекта, арматура, прокат, остекление).
        double PriceOf(string key) => _priced.Line(key)?.UnitPriceRub ?? 0;
        string Rub(double v) => v.ToString("N0", Culture);
        string sumHeader = _loc.Get("QtyColSum");

        ConcreteRows.Children.Clear();
        AddHeader(ConcreteRows, _loc.Get("QtyColGroup"), _loc.Get("QtyColCount"), _loc.Get("UnitM3"), sumHeader);
        foreach (var c in s.Concrete)
            AddRow(ConcreteRows, _loc.Get(c.GroupKey), c.Count.ToString("N0", Culture), c.Volume.ToString("N2", Culture), sum: Rub(c.Volume * PriceOf(CostEstimateService.Concrete)));
        if (s.Concrete.Count == 0) AddEmpty(ConcreteRows, _loc.Get("QtyNoConcrete"));
        AddRow(ConcreteRows, _loc.Get("QtyTotal"), string.Empty, s.ConcreteTotal.ToString("N2", Culture), total: true, sum: Rub(_priced.Line(CostEstimateService.Concrete)?.SumRub ?? 0));

        GlazingRows.Children.Clear();
        AddHeader(GlazingRows, _loc.Get("QtyColItem"), _loc.Get("QtyColCount"), _loc.Get("UnitM2"), sumHeader);
        AddRow(GlazingRows, _loc.Get("QtyWindows"), s.WindowCount.ToString("N0", Culture), s.WindowArea.ToString("N1", Culture), sum: Rub(s.WindowArea * PriceOf(CostEstimateService.Glazing)));
        AddRow(GlazingRows, _loc.Get("QtyCurtainWalls"), s.CurtainWallCount.ToString("N0", Culture), s.CurtainWallArea.ToString("N1", Culture), sum: Rub(s.CurtainWallArea * PriceOf(CostEstimateService.Glazing)));
        AddRow(GlazingRows, _loc.Get("QtyTotal"), string.Empty, s.GlazingTotal.ToString("N1", Culture), total: true, sum: Rub(_priced.Line(CostEstimateService.Glazing)?.SumRub ?? 0));

        RebarRows.Children.Clear();
        AddHeader(RebarRows, _loc.Get("QtyColClass"), _loc.Get("QtyColDiameters"), _loc.Get("UnitKg"), sumHeader);
        string working = StructuralNorms.Title(s.WorkingClass == RebarClass.A240 ? RebarClass.A400 : s.WorkingClass);
        if (s.Rebar.Total <= 0) AddEmpty(RebarRows, _loc.Get("QtyNoRebar"));
        else
        {
            if (s.Rebar.WorkingTotal > 0) AddRow(RebarRows, working, Diameters(s.Rebar.Working), Kg(s.Rebar.WorkingTotal), sum: Rub(_priced.Line(CostEstimateService.RebarWorking)?.SumRub ?? 0));
            if (s.Rebar.A240Total > 0) AddRow(RebarRows, "А240", Diameters(s.Rebar.A240), Kg(s.Rebar.A240Total), sum: Rub(_priced.Line(CostEstimateService.RebarA240)?.SumRub ?? 0));
            if (s.Rebar.PlatesTotal > 0) AddRow(RebarRows, _loc.Get("QtyPlates"), string.Join(", ", s.Rebar.Plates.Keys.Select(t => "−" + t)), Kg(s.Rebar.PlatesTotal),
                sum: Rub(_priced.Line(CostEstimateService.Embedded)?.SumRub ?? 0));
        }
        double rebarSum = (_priced.Line(CostEstimateService.RebarWorking)?.SumRub ?? 0) + (_priced.Line(CostEstimateService.RebarA240)?.SumRub ?? 0) + (_priced.Line(CostEstimateService.Embedded)?.SumRub ?? 0);
        AddRow(RebarRows, _loc.Get("QtyTotal"), string.Empty, Kg(s.Rebar.Total), total: true, sum: Rub(rebarSum));

        RolledRows.Children.Clear();
        AddHeader(RolledRows, _loc.Get("QtyColProfile"), _loc.Get("QtyColLength"), _loc.Get("UnitKg"), sumHeader);
        if (s.RolledSteel.Count == 0) AddEmpty(RolledRows, _loc.Get("QtyNoRolled"));
        foreach (var r in s.RolledSteel.Take(8))
            AddRow(RolledRows, $"{_loc.Get(r.GroupKey)} {r.Profile}", $"{r.Count.ToString("N0", Culture)} · {r.Length.ToString("N1", Culture)}", Kg(r.Mass), sum: Rub(r.Mass / 1000 * PriceOf(CostEstimateService.Rolled)));
        if (s.RolledSteel.Count > 8)
            AddRow(RolledRows, _loc.Format("QtyMore", s.RolledSteel.Count - 8), string.Empty, Kg(s.RolledSteel.Skip(8).Sum(r => r.Mass)));
        AddRow(RolledRows, _loc.Get("QtyTotal"), string.Empty, Kg(s.RolledSteelTotal), total: true, sum: Rub(_priced.Line(CostEstimateService.Rolled)?.SumRub ?? 0));
        RefreshParameters();
        RefreshCatalogs();
        BuildPricedRows();
        RefreshCost();
    }

    // ───────────────────────────── Свойства пользователя ─────────────────────────────

    /// <summary>Свойства, по которым можно сгруппировать ведомость (порядок пунктов ParamCombo).</summary>
    private List<ParameterDefinition> _groupOptions = [];
    private Guid _groupBy;
    private ParameterQuantityBreakdown? _breakdown;
    private bool _syncingGroups;

    /// <summary>
    /// Карточка «Свойства пользователя»: список свойств модели, выбранное (по умолчанию первое) сохраняется между пересчётами;
    /// строки — значение, число элементов, бетон, арматура, остекление и прокат элементов с этим значением, «не задано» — последней.
    /// </summary>
    private void RefreshParameters()
    {
        _groupOptions = [.. _schedule.UserParameters];
        ViewSwitch.Visibility = _groupOptions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MembersViewButton.Content = _loc.Get("QtyViewMembers");
        ParamsViewButton.Content = _loc.Get("QtyViewParams");
        _breakdown = null;
        if (_groupOptions.Count == 0)
        {
            MembersViewButton.IsChecked = true;
            ApplyView();
            return;
        }

        ParamTitle.Text = _loc.Get("QtyParamTitle");
        ParamGroupLabel.Text = _loc.Get("QtyParamGroupBy");
        int selected = Math.Max(0, _groupOptions.FindIndex(d => d.Guid == _groupBy));
        _groupBy = _groupOptions[selected].Guid;
        _syncingGroups = true;
        ParamCombo.Items.Clear();
        foreach (var d in _groupOptions) ParamCombo.Items.Add(d.Name);
        ParamCombo.SelectedIndex = selected;
        _syncingGroups = false;

        var b = _breakdown = _vm.BuildParameterBreakdown(_groupOptions[selected]);
        ParamRows.Children.Clear();
        AddParamRow(ParamRows, [_loc.Get("QtyParamValue"), _loc.Get("QtyParamCount"), _loc.Get("QtyConcrete") + ", " + _loc.Get("UnitM3"),
            _loc.Get("QtyRebar") + ", " + _loc.Get("UnitKg"), _loc.Get("QtyGlazing") + ", " + _loc.Get("UnitM2"), _loc.Get("QtyRolled") + ", " + _loc.Get("UnitKg")], header: true);
        const int shown = 14;
        foreach (var g in b.Groups.Take(shown)) AddParamRow(ParamRows, ParamCells(GroupTitle(b, g), g.Count, g.Concrete, g.Rebar, g.Glazing, g.RolledSteel));
        if (b.Groups.Count > shown)
        {
            var rest = b.Groups.Skip(shown).ToList();
            AddParamRow(ParamRows, ParamCells(_loc.Format("QtyParamMore", rest.Count), rest.Sum(g => g.Count), rest.Sum(g => g.Concrete), rest.Sum(g => g.Rebar),
                rest.Sum(g => g.Glazing), rest.Sum(g => g.RolledSteel)));
        }
        AddParamRow(ParamRows, ParamCells(_loc.Get("QtyTotal"), b.Groups.Sum(g => g.Count), b.Groups.Sum(g => g.Concrete), b.Groups.Sum(g => g.Rebar),
            b.Groups.Sum(g => g.Glazing), b.Groups.Sum(g => g.RolledSteel)), total: true);
        ApplyView();
    }

    /// <summary>Сегмент шапки: таблицы по конструкциям или ведомость по свойству — в той же ячейке, высота окна не скачет.</summary>
    private void ApplyView()
    {
        bool byParams = ParamsViewButton.IsChecked == true && _groupOptions.Count > 0;
        if (byParams && TablesGrid.ActualHeight > 0) ParamCard.MinHeight = TablesGrid.ActualHeight;
        TablesGrid.Visibility = byParams ? Visibility.Collapsed : Visibility.Visible;
        ParamCard.Visibility = byParams ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnViewChecked(object sender, RoutedEventArgs e)
    {
        if (TablesGrid is not null) ApplyView();
    }

    /// <summary>Открыть вид «По свойствам» (съёмка AIA_DEBUG_QUANTITIES=params).</summary>
    public void ShowParameterView() => ParamsViewButton.IsChecked = true;

    private string GroupTitle(ParameterQuantityBreakdown b, ParameterQuantityGroup g) =>
        g.IsUnset ? _loc.Get("QtyParamUnset") : ParameterSpecs.Format(b.Definition.SpecType, g.Value, Culture, !_loc.IsRussian) is { Length: > 0 } text ? text : "—";

    private string[] ParamCells(string title, int count, double concrete, double rebar, double glazing, double rolled) =>
        [title, count.ToString("N0", Culture), concrete.ToString("N2", Culture), Kg(rebar), glazing.ToString("N1", Culture), Kg(rolled)];

    private void OnParamGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGroups || ParamCombo.SelectedIndex < 0 || ParamCombo.SelectedIndex >= _groupOptions.Count) return;
        _groupBy = _groupOptions[ParamCombo.SelectedIndex].Guid;
        RefreshParameters();
    }

    /// <summary>Строка таблицы свойств: значение растягивается, пять числовых колонок по 92 px справа.</summary>
    private static void AddParamRow(Panel panel, string[] cells, bool header = false, bool total = false)
    {
        var row = new Grid { ColumnSpacing = 8, MinHeight = header ? 18 : 24 };
        if (total)
        {
            row.BorderBrush = (Brush)Application.Current.Resources["StudioBorderBrush"];
            row.BorderThickness = new Thickness(0, 1, 0, 0);
            row.Margin = new Thickness(0, 4, 0, 0);
            row.Padding = new Thickness(0, 4, 0, 0);
        }
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 1; i < cells.Length; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 1 ? 64 : 92) });
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = Cell(cells[i], i, strong: total, secondary: header || (i == 1 && !total), mono: !header && i > 0);
            if (header) cell.FontSize = 10;
            row.Children.Add(cell);
        }
        panel.Children.Add(row);
    }

    // ───────────────────────────── Смета монолитного каркаса ─────────────────────────────

    // ───────────────────────────── Базы цен ─────────────────────────────

    private List<MaterialPriceCatalog> _catalogs = [];
    private bool _syncingCatalogs;

    /// <summary>Список «База цен»: расценки проекта и загруженные прайс-листы (название · дата актуализации).</summary>
    private void RefreshCatalogs()
    {
        _catalogs = [.. _vm.PriceCatalogs];
        var active = _vm.ActivePriceCatalog;
        _syncingCatalogs = true;
        CatalogCombo.Items.Clear();
        CatalogCombo.Items.Add(_loc.Get("PriceProjectRates"));
        foreach (var c in _catalogs) CatalogCombo.Items.Add(c.Title);
        CatalogCombo.SelectedIndex = active is null ? 0 : Math.Max(0, _catalogs.FindIndex(c => c.Name == active.Name) + 1);
        _syncingCatalogs = false;
        CatalogInfoText.Text = active is null
            ? _loc.Get("PriceProjectRatesHint")
            : _loc.Format("PriceCatalogInfo", active.Name, active.UpdatedAt.ToString("dd.MM.yyyy", Culture), active.Items.Count,
                string.Join(", ", active.Items.Select(i => i.SupplierName).Where(n => n.Length > 0).Distinct().Take(3)) is { Length: > 0 } names ? " · " + names : string.Empty);
        CatalogInfoText.Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"];
    }

    private void OnCatalogChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCatalogs || CatalogCombo.SelectedIndex < 0) return;
        int index = CatalogCombo.SelectedIndex;
        _vm.SelectPriceCatalog(index == 0 || index > _catalogs.Count ? null : _catalogs[index - 1].Name);
    }

    /// <summary>Пользователь выбрал «Подтянуть цены по ссылке / из сметы…»: окно закрывается, главное окно открывает веб-импорт и возвращает ведомость.</summary>
    public bool PullPricesRequested { get; private set; }

    private void OnPullPriceClick(object sender, RoutedEventArgs e)
    {
        PullPricesRequested = true;
        Hide();
    }

    private async void OnLoadPriceClick(object sender, RoutedEventArgs e)
    {
        if (_pickPriceFile is null) return;
        string? path = await _pickPriceFile();
        if (path is null) return;
        try
        {
            _vm.ImportPriceCatalog(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            CatalogInfoText.Text = _loc.Format("PriceImportFailed", ex.Message);
            CatalogInfoText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A));
        }
    }

    private async void OnTemplateClick(object sender, RoutedEventArgs e)
    {
        if (_pickTemplatePath is null) return;
        string? path = await _pickTemplatePath();
        if (path is null) return;
        try
        {
            await _vm.ExportPriceTemplateAsync(path);
            CatalogInfoText.Text = _loc.Format("PriceTemplateSavedShort", Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CatalogInfoText.Text = ex.Message;
        }
    }

    // ───────────────────────────── Строки доказательной сметы ─────────────────────────────

    /// <summary>Строка таблицы сметы: позиция, поля цены и поставщика, сумма.</summary>
    private sealed record PricedRow(PricedLine Line, TextBox Price, TextBox Supplier, TextBlock Sum);

    private readonly List<PricedRow> _rows = [];
    private bool _editing;

    private static readonly int[] PricedColumns = [-1, 74, 30, 104, 168, 156, 104, 22];

    /// <summary>
    /// Таблица сметы: позиция (не входящие в итог каркаса — приглушённо), количество, единица, цена за единицу (правка),
    /// поставщик (правка), документ-основание, сумма; у ручных правок — кнопка возврата к цене базы. Пока поле в фокусе,
    /// таблица не пересобирается (пересчёт ведомости не сбивает ввод).
    /// </summary>
    private void BuildPricedRows()
    {
        if (_editing)
        {
            // Поля не пересобираются — строки получают новые цены базы для сравнения при следующем вводе.
            for (int i = 0; i < _rows.Count; i++)
                if (_priced.Line(_rows[i].Line.Key) is { } fresh) _rows[i] = _rows[i] with { Line = fresh };
            return;
        }
        _rows.Clear();
        PricedRows.Children.Clear();
        var header = PricedGrid(header: true);
        string[] titles = [_loc.Get("PriceColItem"), _loc.Get("QtyColCount"), string.Empty, _loc.Get("PriceColPrice"), _loc.Get("PriceColSupplier"), _loc.Get("PriceColSource"), _loc.Get("QtyColSum")];
        for (int i = 0; i < titles.Length; i++)
        {
            var cell = At(Cell(titles[i], i, strong: false, secondary: true, mono: false), i, 10);
            if (i is 4 or 5) cell.HorizontalAlignment = HorizontalAlignment.Left;
            header.Children.Add(cell);
        }
        PricedRows.Children.Add(header);
        foreach (var line in _priced.Lines)
        {
            if (line.Quantity <= 0 && line.Key is CostEstimateService.Rolled or CostEstimateService.Glazing or CostEstimateService.Embedded) continue;
            var row = PricedGrid();
            var title = Cell(line.Title, 0, strong: false, secondary: !line.InFrameTotal, mono: false);
            ToolTipService.SetToolTip(title, line.InFrameTotal ? line.Category : _loc.Get("PriceNotInTotal"));
            row.Children.Add(title);
            row.Children.Add(Cell(line.Quantity.ToString(line.Unit == "т" ? "N3" : "N2", Culture), 1, strong: false, secondary: false, mono: true));
            row.Children.Add(Cell(line.Unit, 2, strong: false, secondary: true, mono: false));
            var price = Field(line.UnitPriceRub.ToString("#,0.##", Culture), mono: true, right: true, line.Key);
            Grid.SetColumn(price.Host, 3);
            row.Children.Add(price.Host);
            var supplier = Field(line.Supplier, mono: false, right: false, line.Key);
            supplier.Box.PlaceholderText = _loc.Get("PriceSupplierPlaceholder");
            Grid.SetColumn(supplier.Host, 4);
            row.Children.Add(supplier.Host);
            var source = Cell(line.SourceDocument, 5, strong: false, secondary: true, mono: false);
            source.FontSize = 11;
            source.HorizontalAlignment = HorizontalAlignment.Left;
            ToolTipService.SetToolTip(source, line.SourceDocument);
            if (line.Origin == PriceOrigin.Manual) source.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
            row.Children.Add(source);
            var sum = Cell(line.SumRub.ToString("N0", Culture), 6, strong: line.InFrameTotal, secondary: !line.InFrameTotal, mono: true);
            row.Children.Add(sum);
            if (line.Origin == PriceOrigin.Manual)
            {
                var reset = new Button
                {
                    Content = new FontIcon { Glyph = "", FontSize = 10 },
                    Width = 22, Height = 22, Padding = new Thickness(0), CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Tag = line.Key
                };
                ToolTipService.SetToolTip(reset, _loc.Get("PriceResetLine"));
                reset.Click += (s, _) => { if (s is FrameworkElement { Tag: string key }) _vm.ResetEstimateLine(key); };
                Grid.SetColumn(reset, 7);
                row.Children.Add(reset);
            }
            _rows.Add(new PricedRow(line, price.Box, supplier.Box, sum));
            PricedRows.Children.Add(row);
        }
    }

    private static Grid PricedGrid(bool header = false)
    {
        var row = new Grid { ColumnSpacing = 8, MinHeight = header ? 18 : 30 };
        foreach (int w in PricedColumns)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        if (!header)
        {
            row.BorderBrush = (Brush)Application.Current.Resources["StudioBorderBrush"];
            row.BorderThickness = new Thickness(0, 1, 0, 0);
        }
        return row;
    }

    private static TextBlock At(TextBlock cell, int column, double size)
    {
        Grid.SetColumn(cell, column);
        cell.FontSize = size;
        return cell;
    }

    /// <summary>Поле ввода в строке сметы (стиль полей координат инспектора).</summary>
    private (Border Host, TextBox Box) Field(string text, bool mono, bool right, string key)
    {
        var box = new TextBox
        {
            Text = text,
            FontSize = 12,
            MinHeight = 24,
            Padding = new Thickness(6, 3, 6, 3),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = key
        };
        if (mono) box.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
        if (right) box.InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Number) } };
        box.GotFocus += (_, _) => _editing = true;
        box.LostFocus += (_, _) =>
        {
            _editing = false;
            CommitPrices();
        };
        box.TextChanged += (_, _) =>
        {
            RefreshCost();
            _commitTimer.Stop();
            _commitTimer.Start();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            CommitPrices();
        };
        var host = new Border
        {
            Style = (Style)Application.Current.Resources["CoordinateFieldStyle"],
            Background = (Brush)Application.Current.Resources["CardBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Child = box
        };
        return (host, box);
    }

    /// <summary>Цена из поля: пробелы и неразрывные пробелы разрядов игнорируются, запятая и точка — дробная часть.</summary>
    private static bool TryParsePrice(string text, out double value) =>
        MaterialPriceCatalog.TryParsePrice(text, out value) && value < 1e9 && text.Any(char.IsDigit);

    /// <summary>Итог каркаса по введённым (ещё не применённым) ценам — пересчёт на каждое нажатие клавиши.</summary>
    private void RefreshCost()
    {
        double total = 0, all = 0;
        bool valid = true;
        foreach (var r in _rows)
        {
            bool ok = TryParsePrice(r.Price.Text, out double price);
            valid &= ok;
            r.Price.Foreground = PriceBrush(ok);
            double sum = r.Line.Quantity * (ok ? price : r.Line.UnitPriceRub);
            r.Sum.Text = sum.ToString("N0", Culture);
            all += sum;
            if (r.Line.InFrameTotal) total += sum;
        }
        if (_rows.Count == 0) (total, all) = (_priced.FrameTotal, _priced.GrandTotal);
        CostTotalValue.Text = total.ToString("N0", Culture) + " ₽";
        CostBreakdownText.Text = _loc.Format("PriceBreakdown", total.ToString("N0", Culture), all.ToString("N0", Culture),
            _vm.Settings.Current.Engineering.IncludeSteelAndGlazing ? _loc.Get("PriceTotalFull") : _loc.Get("PriceTotalFrame"));

        var saved = _vm.ProjectEstimate;
        bool matches = valid && saved is not null && _vm.Scene.ProjectFilePath is not null && !_vm.Scene.IsDirty && Math.Abs(saved.FrameCostRub - Math.Round(total)) < 0.5;
        CostSavedText.Text = matches ? _loc.Format("CostSavedAt", saved!.SavedAt.ToString("dd.MM.yyyy HH:mm", Culture))
            : _saveProject is null ? string.Empty : _loc.Get("CostUnsaved");
        CostSavedText.Foreground = (Brush)Application.Current.Resources[matches ? "AccentBrush" : "TextSecondaryBrush"];
    }

    private static Brush PriceBrush(bool valid) =>
        valid ? (Brush)Application.Current.Resources["TextPrimaryBrush"] : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A));

    /// <summary>
    /// Введённые цены и поставщики: у расценок проекта без поставщика цена бетона и рабочей арматуры становится расценкой проекта
    /// (как раньше), остальное — ручная правка строки с поставщиком и основанием (сохраняется в проект).
    /// </summary>
    private void CommitPrices()
    {
        _commitTimer.Stop();
        foreach (var r in _rows.ToList())
        {
            if (!TryParsePrice(r.Price.Text, out double price)) continue;
            string supplier = r.Supplier.Text.Trim();
            var line = r.Line;
            bool priceChanged = Math.Abs(price - line.UnitPriceRub) > 0.004;
            bool supplierChanged = !string.Equals(supplier, line.Supplier, StringComparison.Ordinal);
            if (!priceChanged && !supplierChanged) continue;
            bool projectRate = _vm.ActivePriceCatalog is null && line.Origin == PriceOrigin.ProjectRates && supplier.Length == 0 &&
                               line.Key is CostEstimateService.Concrete or CostEstimateService.RebarWorking;
            if (projectRate)
            {
                var s = _vm.Settings.Current;
                _vm.SetEstimatePrices(line.Key == CostEstimateService.Concrete ? price : s.ConcretePricePerM3, line.Key == CostEstimateService.RebarWorking ? price : s.RebarPricePerTon);
            }
            else _vm.SetEstimateLine(line.Key, price, supplier, priceChanged || line.Origin != PriceOrigin.Manual ? null : line.SourceDocument);
        }
    }

    /// <summary>Съёмка (AIA_DEBUG_PRICEEDIT): ручная правка строки сметы тем же путём, что и ввод в окне.</summary>
    public void DebugEditLine(string key, double price, string supplier)
    {
        var row = _rows.FirstOrDefault(r => r.Line.Key == key);
        if (row is null) return;
        row.Price.Text = price.ToString("0.##", Culture);
        row.Supplier.Text = supplier;
        CommitPrices();
    }

    /// <summary>«Сохранить смету в проект»: цены применяются, проект сохраняется в .aia вместе со сметой; окно остаётся открытым.</summary>
    private async void OnSaveEstimateClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_saving || _saveProject is null) return;
        var deferral = args.GetDeferral();
        _saving = true;
        IsSecondaryButtonEnabled = false;
        try
        {
            CommitPrices();
            if (await _saveProject())
            {
                _vm.ReportCostEstimateSaved();
                SecondaryButtonText = _loc.Get("CostSaved");
            }
        }
        finally
        {
            _saving = false;
            IsSecondaryButtonEnabled = true;
            deferral.Complete();
            RefreshCost();
        }
    }

    private (string Value, string Unit) Mass(double kg) =>
        kg >= 10000 ? ((kg / 1000).ToString("N2", Culture), _loc.Get("UnitT")) : (kg.ToString("N0", Culture), _loc.Get("UnitKg"));

    private string Kg(double kg) => kg.ToString("N0", Culture);

    private static string Diameters(SortedDictionary<int, double> byDiameter) => string.Join(" ", byDiameter.Keys.Select(d => "Ø" + d));

    // ───────────────────────────── Строки таблиц ─────────────────────────────

    private static Grid NewRow()
    {
        var row = new Grid { ColumnSpacing = 8, MinHeight = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        return row;
    }

    private static TextBlock Cell(string text, int column, bool strong, bool secondary, bool mono)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = column == 0 ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            Foreground = (Brush)Application.Current.Resources[secondary ? "TextSecondaryBrush" : "TextPrimaryBrush"]
        };
        if (mono) cell.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static void AddHeader(Panel panel, string a, string b, string c, string d = "")
    {
        var row = NewRow();
        row.MinHeight = 18;
        foreach (var (text, i) in new[] { (a, 0), (b, 1), (c, 2), (d, 3) })
        {
            var cell = Cell(text, i, strong: false, secondary: true, mono: false);
            cell.FontSize = 10;
            row.Children.Add(cell);
        }
        panel.Children.Add(row);
    }

    /// <param name="sum">Сумма позиции по цене строки сметы, ₽ (пусто — не выводится).</param>
    private static void AddRow(Panel panel, string label, string middle, string value, bool total = false, string sum = "")
    {
        var row = NewRow();
        if (total)
        {
            row.BorderBrush = (Brush)Application.Current.Resources["StudioBorderBrush"];
            row.BorderThickness = new Thickness(0, 1, 0, 0);
            row.Margin = new Thickness(0, 4, 0, 0);
            row.Padding = new Thickness(0, 4, 0, 0);
        }
        row.Children.Add(Cell(label, 0, total, secondary: false, mono: false));
        row.Children.Add(Cell(middle, 1, false, secondary: true, mono: true));
        row.Children.Add(Cell(value, 2, total, secondary: false, mono: true));
        row.Children.Add(Cell(sum, 3, total, secondary: !total, mono: true));
        panel.Children.Add(row);
    }

    private static void AddEmpty(Panel panel, string text) => panel.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 11,
        Margin = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"]
    });

    // ───────────────────────────── Копирование ─────────────────────────────

    /// <summary>Таблица с табуляциями (вставка в Excel); окно остаётся открытым.</summary>
    private void OnCopyClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        CommitPrices();
        var s = _schedule;
        var priced = _priced = _vm.BuildPricedEstimate(s);
        var sb = new StringBuilder();
        sb.AppendLine(_loc.Get("QtyTitle"));
        sb.AppendLine(_vm.ActivePriceCatalog is { } catalog
            ? $"{_loc.Get("PriceCatalog")}\t{catalog.Name}\t{catalog.UpdatedAt.ToString("dd.MM.yyyy", Culture)}"
            : $"{_loc.Get("PriceCatalog")}\t{_loc.Get("PriceProjectRates")}");
        sb.AppendLine();
        sb.AppendLine(_loc.Get("CostTitle"));
        sb.Append(CostEstimateService.ToTsv(priced, Culture));
        sb.AppendLine();
        sb.AppendLine($"{_loc.Get("QtyConcreteTitle")}\t{_loc.Get("QtyColCount")}\t{_loc.Get("UnitM3")}");
        foreach (var c in s.Concrete) sb.AppendLine($"{_loc.Get(c.GroupKey)}\t{c.Count}\t{c.Volume.ToString("0.00", Culture)}");
        sb.AppendLine($"{_loc.Get("QtyTotal")}\t\t{s.ConcreteTotal.ToString("0.00", Culture)}");
        sb.AppendLine();
        sb.AppendLine($"{_loc.Get("QtyRebarTitle")}\t\t{_loc.Get("UnitKg")}");
        string working = StructuralNorms.Title(s.WorkingClass == RebarClass.A240 ? RebarClass.A400 : s.WorkingClass);
        foreach (var (d, kg) in s.Rebar.Working) sb.AppendLine($"{working}\tØ{d}\t{kg.ToString("0", Culture)}");
        foreach (var (d, kg) in s.Rebar.A240) sb.AppendLine($"А240\tØ{d}\t{kg.ToString("0", Culture)}");
        foreach (var (t, kg) in s.Rebar.Plates) sb.AppendLine($"{_loc.Get("QtyPlates")}\t−{t}\t{kg.ToString("0", Culture)}");
        sb.AppendLine($"{_loc.Get("QtyTotal")}\t\t{s.Rebar.Total.ToString("0", Culture)}");
        sb.AppendLine();
        sb.AppendLine($"{_loc.Get("QtyGlazingTitle")}\t{_loc.Get("QtyColCount")}\t{_loc.Get("UnitM2")}");
        sb.AppendLine($"{_loc.Get("QtyWindows")}\t{s.WindowCount}\t{s.WindowArea.ToString("0.0", Culture)}");
        sb.AppendLine($"{_loc.Get("QtyCurtainWalls")}\t{s.CurtainWallCount}\t{s.CurtainWallArea.ToString("0.0", Culture)}");
        sb.AppendLine();
        sb.AppendLine($"{_loc.Get("QtyRolledTitle")}\t{_loc.Get("QtyColCount")}\t{_loc.Get("QtyColLength")}\t{_loc.Get("UnitKg")}");
        foreach (var r in s.RolledSteel)
            sb.AppendLine($"{_loc.Get(r.GroupKey)} {r.Profile}\t{r.Count}\t{r.Length.ToString("0.0", Culture)}\t{r.Mass.ToString("0", Culture)}");
        sb.AppendLine($"{_loc.Get("QtyTotal")}\t\t\t{s.RolledSteelTotal.ToString("0", Culture)}");
        if (_breakdown is { } b)
        {
            sb.AppendLine();
            sb.AppendLine($"{_loc.Get("QtyParamTitle")}: {b.Definition.Name}\t{_loc.Get("QtyParamCount")}\t{_loc.Get("UnitM3")}\t{_loc.Get("QtyRebar")}, {_loc.Get("UnitKg")}\t" +
                          $"{_loc.Get("QtyGlazing")}, {_loc.Get("UnitM2")}\t{_loc.Get("QtyRolled")}, {_loc.Get("UnitKg")}");
            foreach (var g in b.Groups)
                sb.AppendLine($"{GroupTitle(b, g)}\t{g.Count}\t{g.Concrete.ToString("0.00", Culture)}\t{g.Rebar.ToString("0", Culture)}\t" +
                              $"{g.Glazing.ToString("0.0", Culture)}\t{g.RolledSteel.ToString("0", Culture)}");
        }

        var package = new DataPackage();
        package.SetText(sb.ToString());
        Clipboard.SetContent(package);
        PrimaryButtonText = _loc.Get("QtyCopied");
    }
}
