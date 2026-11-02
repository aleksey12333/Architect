using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AIArchitectStudio.Views;

/// <summary>
/// Паспорт расчёта элемента («Обоснование по СП»): четыре раздела — сводка и вердикт с нормативной базой, паспорт материала
/// (таблица СП или сертификат), сбор нагрузок и граничные условия по расчётной схеме, пошаговый расчёт с формулами, подстановкой
/// и сверкой K с расчётной схемой. «Копировать расчётную записку» — текст для пояснительной записки ПД (окно не закрывается),
/// «Задать свои характеристики материала» — R, E, ρ по сертификату для элемента или для класса материала проекта; после
/// применения паспорт пересчитывается.
/// </summary>
public sealed partial class CalculationPassportDialog : ContentDialog
{
    private readonly MainViewModel _vm;
    private readonly LocalizationService _loc;
    private readonly string _elementId;
    private ElementCalculationPassport? _passport;

    public CalculationPassportDialog(MainViewModel viewModel, string elementId)
    {
        _vm = viewModel;
        _loc = viewModel.Loc;
        _elementId = elementId;
        InitializeComponent();
        PrimaryButtonText = _loc.Get("PassportCopy");
        SecondaryButtonText = _loc.Get("PassportMaterial");
        CloseButtonText = _loc.Get("Close");
        TitleText.Text = _loc.Get("PassportTitle");
        EditorTitle.Text = _loc.Get("PassportEditorTitle");
        ScopeElementRadio.Content = _loc.Get("PassportScopeElement");
        ScopeClassRadio.Content = _loc.Get("PassportScopeClass");
        StrengthLabel.Text = _loc.Get("PassportStrength");
        ModulusLabel.Text = _loc.Get("PassportModulus");
        DensityLabel.Text = _loc.Get("PassportDensity");
        ApplyCertificateButton.Content = _loc.Get("PassportApply");
        ResetCertificateButton.Content = _loc.Get("PassportResetSp");
        Opened += (_, _) => FocusSink.Focus(FocusState.Programmatic);
        Refresh();
    }

    private CultureInfo Culture => _loc.Culture;

    /// <summary>Открыть редактор характеристик материала (съёмка AIA_DEBUG_PASSPORT=…;material).</summary>
    public void ShowMaterialEditor() => SetEditorVisible(true);

    /// <summary>Прокрутить к разделу «Пошаговый расчёт» (съёмка AIA_DEBUG_PASSPORT=…;steps).</summary>
    public void ScrollToSteps()
    {
        if (Body.Children.Count < 4 || Body.Children[3] is not FrameworkElement steps) return;
        var origin = steps.TransformToVisual(Body).TransformPoint(new Windows.Foundation.Point(0, 0));
        BodyScroll.ChangeView(null, origin.Y, null, disableAnimation: true);
    }

    private void Refresh()
    {
        _passport = _vm.BuildPassport(_elementId);
        Body.Children.Clear();
        if (_passport is not { } p)
        {
            SubtitleText.Text = _loc.Get("PassportNone");
            VerdictPill.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = false;
            return;
        }
        SubtitleText.Text = $"{p.CategoryTitle} «{p.ElementTitle}»{(p.Floor > 0 ? $" · этаж +{p.Floor}" : string.Empty)}{(p.SectionText.Length > 0 ? " · " + p.SectionText : string.Empty)}";
        var color = StressPalette.ToUi(StressPalette.ColorFor(p.Utilization));
        var brush = new SolidColorBrush(color);
        VerdictPill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, color.R, color.G, color.B));
        KValueText.Text = p.Utilization.ToString("0.00", Culture);
        KValueText.Foreground = brush;
        VerdictText.Text = p.IsSafe ? _loc.Get("PassportSafe") : _loc.Get("PassportOverload");
        VerdictText.Foreground = brush;
        GoverningText.Text = _loc.Format("PassportGoverning", p.Governing);

        Body.Children.Add(SummarySection(p));
        Body.Children.Add(MaterialSection(p));
        Body.Children.Add(LoadsSection(p));
        Body.Children.Add(StepsSection(p));
        FillEditor(p);
    }

    // ───────────────────────────── Разделы ─────────────────────────────

    private Border SummarySection(ElementCalculationPassport p)
    {
        var panel = SectionPanel(1, _loc.Get("PassportSection1"), out var card);
        var badge = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        badge.Children.Add(new FontIcon
        {
            Glyph = p.MatchesSolver ? "" : "",
            FontSize = 12,
            Foreground = new SolidColorBrush(p.MatchesSolver ? Windows.UI.Color.FromArgb(0xFF, 0x30, 0xD1, 0x58) : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x9F, 0x0A))
        });
        badge.Children.Add(Text(p.MatchesSolver
            ? _loc.Format("PassportVerified", p.RecomputedChecks, p.Checks.Count())
            : _loc.Format("PassportMismatch", p.RecomputedUtilization.ToString("0.0000", Culture)), 11, secondary: true));
        panel.Children.Add(Text(p.Verdict + (p.GammaN != 1 ? $" · γn = {p.GammaN.ToString("0.0", Culture)}" : string.Empty), 12));
        panel.Children.Add(badge);
        panel.Children.Add(Caption(_loc.Get("PassportNorms")));
        foreach (var n in p.Norms)
        {
            var row = Row([n.Document, n.Clause, n.Subject], [128, 150, -1], mono: false, firstStrong: true);
            row.MinHeight = 18;
            foreach (var cell in row.Children.OfType<TextBlock>()) cell.FontSize = 11;
            panel.Children.Add(row);
        }
        return card;
    }

    private Border MaterialSection(ElementCalculationPassport p)
    {
        var panel = SectionPanel(2, _loc.Get("PassportSection2"), out var card);
        panel.Children.Add(Text(p.MaterialTitle, 12, strong: true));
        var source = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        source.Children.Add(new FontIcon
        {
            Glyph = p.MaterialFromCertificate ? "" : "",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources[p.MaterialFromCertificate ? "AccentBrush" : "TextSecondaryBrush"]
        });
        var sourceText = Text(_loc.Get("PassportSource") + ": " + p.MaterialSource, 11, secondary: !p.MaterialFromCertificate);
        sourceText.TextWrapping = TextWrapping.Wrap;
        sourceText.MaxWidth = 740;
        source.Children.Add(sourceText);
        panel.Children.Add(source);
        panel.Children.Add(Row([_loc.Get("PassportColSymbol"), _loc.Get("PassportColProperty"), _loc.Get("PassportColValue"), _loc.Get("PassportColSource")], [56, -1, 110, 240], header: true));
        foreach (var m in p.MaterialProperties)
            panel.Children.Add(Row([m.Symbol, m.Title, $"{ElementCalculationPassport.Format(m.Value, Culture)} {m.Unit}".Trim(), m.Source], [56, -1, 110, 240], monoColumn: 2));
        return card;
    }

    private Border LoadsSection(ElementCalculationPassport p)
    {
        var panel = SectionPanel(3, _loc.Get("PassportSection3"), out var card);
        panel.Children.Add(Row([_loc.Get("PassportColLoad"), _loc.Get("PassportColDetail"), _loc.Get("PassportColValue"), _loc.Get("PassportColClause")], [200, -1, 100, 170], header: true));
        foreach (var l in p.Loads)
            panel.Children.Add(Row([l.Title, l.Detail, $"{ElementCalculationPassport.Format(l.Value, Culture)} {l.Unit}".Trim(), l.Clause], [200, -1, 100, 170], monoColumn: 2,
                strongRow: l.Title.StartsWith("Расчётная", StringComparison.Ordinal)));
        if (p.Boundary.Count > 0)
        {
            panel.Children.Add(Caption(_loc.Get("PassportBoundary")));
            foreach (var b in p.Boundary) panel.Children.Add(Text("· " + b, 11, secondary: true, wrap: true));
        }
        return card;
    }

    private Border StepsSection(ElementCalculationPassport p)
    {
        var panel = SectionPanel(4, _loc.Get("PassportSection4"), out var card);
        int index = 1;
        foreach (var s in p.Steps)
        {
            var step = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), BorderBrush = (Brush)Application.Current.Resources["StudioBorderBrush"], BorderThickness = new Thickness(0, index == 1 ? 0 : 1, 0, 0) };
            step.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            step.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            step.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            var number = Text($"4.{index++}", 11, secondary: true);
            number.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
            step.Children.Add(number);

            var text = new StackPanel { Spacing = 2 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(Text(s.Title, 12, strong: true));
            if (s.Clause.Length > 0)
                head.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 1, 6, 1),
                    Background = (Brush)Application.Current.Resources["AccentSoftBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = s.Clause, FontSize = 10, Foreground = (Brush)Application.Current.Resources["AccentBrush"], TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 }
                });
            text.Children.Add(head);
            if (s.Formula.Length > 0) text.Children.Add(Mono(s.Formula, primary: true));
            if (s.Substitution.Length > 0) text.Children.Add(Mono(s.Substitution, primary: false));
            Grid.SetColumn(text, 1);
            step.Children.Add(text);

            var value = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Spacing = 2 };
            if (s.IsCheck)
            {
                var color = StressPalette.ToUi(StressPalette.ColorFor(s.Value));
                var k = Text("K = " + s.Value.ToString("0.000", Culture), 13, strong: true);
                k.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
                k.Foreground = new SolidColorBrush(color);
                k.HorizontalAlignment = HorizontalAlignment.Right;
                value.Children.Add(k);
                var mark = Text(!s.Recomputed ? _loc.Get("PassportBySchema") : s.Verified ? _loc.Get("PassportStepVerified") : _loc.Format("PassportStepSolver", s.SolverValue.ToString("0.000", Culture)), 10, secondary: true);
                mark.HorizontalAlignment = HorizontalAlignment.Right;
                value.Children.Add(mark);
            }
            else if (!double.IsNaN(s.Value))
            {
                var v = Text($"{ElementCalculationPassport.Format(s.Value, Culture)} {s.Unit}".Trim(), 12);
                v.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
                v.HorizontalAlignment = HorizontalAlignment.Right;
                value.Children.Add(v);
            }
            Grid.SetColumn(value, 2);
            step.Children.Add(value);
            panel.Children.Add(step);
        }
        if (p.Notes.Count > 0)
        {
            panel.Children.Add(Caption(_loc.Get("PassportNotes")));
            foreach (var n in p.Notes) panel.Children.Add(Text("· " + n, 11, secondary: true, wrap: true));
        }
        return card;
    }

    // ───────────────────────────── Элементы разметки ─────────────────────────────

    private StackPanel SectionPanel(int number, string title, out Border card)
    {
        var panel = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new Border
        {
            Style = (Style)Resources["SectionNumberStyle"],
            Child = new TextBlock { Text = number.ToString(Culture), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["AccentBrush"] }
        });
        head.Children.Add(new TextBlock { Text = title, Style = (Style)Resources["SectionTitleStyle"] });
        panel.Children.Add(head);
        card = new Border { Style = (Style)Resources["PassportCardStyle"], Child = panel };
        return panel;
    }

    private static TextBlock Text(string text, double size, bool strong = false, bool secondary = false, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)Application.Current.Resources[secondary ? "TextSecondaryBrush" : "TextPrimaryBrush"]
    };

    private static TextBlock Mono(string text, bool primary) => new()
    {
        Text = text,
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
        Foreground = (Brush)Application.Current.Resources[primary ? "TextPrimaryBrush" : "TextSecondaryBrush"]
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 6, 0, 0),
        Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"]
    };

    /// <summary>Строка таблицы: ширины колонок (−1 — растягивается), моноширинная колонка значений.</summary>
    private static Grid Row(string[] cells, int[] widths, bool header = false, bool mono = false, int monoColumn = -1, bool firstStrong = false, bool strongRow = false)
    {
        var row = new Grid { ColumnSpacing = 10, MinHeight = header ? 18 : 22 };
        foreach (int w in widths)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        for (int i = 0; i < cells.Length && i < widths.Length; i++)
        {
            var cell = Text(cells[i], header ? 10 : 11.5, strong: strongRow || (firstStrong && i == 0), secondary: header || (!strongRow && i > 0 && i != monoColumn));
            if (!header && (mono || i == monoColumn))
            {
                cell.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
                cell.HorizontalAlignment = HorizontalAlignment.Right;
            }
            ToolTipService.SetToolTip(cell, cells[i].Length > 28 ? cells[i] : null);
            Grid.SetColumn(cell, i);
            row.Children.Add(cell);
        }
        return row;
    }

    // ───────────────────────────── Характеристики материала ─────────────────────────────

    private void SetEditorVisible(bool visible)
    {
        MaterialEditor.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BodyScroll.MaxHeight = visible ? 470 : 600;
    }

    private void FillEditor(ElementCalculationPassport p)
    {
        bool element = ScopeElementRadio.IsChecked == true;
        string code = ClassCode();
        double R(string symbol) => p.MaterialProperties.FirstOrDefault(m => m.Symbol is var s && (s == symbol))?.Value ?? double.NaN;
        double strength = R("Rb") is var rb && !double.IsNaN(rb) ? rb : R("Ry") is var ry && !double.IsNaN(ry) ? ry : R("R");
        double modulus = R("Eb") is var eb && !double.IsNaN(eb) ? eb : R("E");
        StrengthBox.Text = Num(strength);
        ModulusBox.Text = Num(modulus);
        DensityBox.Text = Num(R("ρ"));
        EditorHint.Text = element ? _loc.Get("PassportEditorHintElement") : _loc.Format("PassportEditorHintClass", code);
    }

    /// <summary>Марка проекта, к которой относится материал элемента: класс бетона или С245.</summary>
    private string ClassCode() => _passport?.MaterialTitle.StartsWith("Сталь", StringComparison.Ordinal) == true
        ? MaterialLibrary.SteelC245
        : StructuralNorms.Title(_vm.Settings.Current.StructuralLoads.Concrete);

    private string Num(double v) => double.IsFinite(v) ? v.ToString("0.###", Culture) : string.Empty;

    private static double Parse(string text)
    {
        string clean = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace(',', '.');
        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v > 0 ? v : double.NaN;
    }

    private void OnScopeChecked(object sender, RoutedEventArgs e)
    {
        if (_passport is { } p && StrengthBox is not null) FillEditor(p);
    }

    private void OnMaterialClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        SetEditorVisible(MaterialEditor.Visibility != Visibility.Visible);
    }

    private void OnApplyCertificateClick(object sender, RoutedEventArgs e)
    {
        double strength = Parse(StrengthBox.Text), modulus = Parse(ModulusBox.Text), density = Parse(DensityBox.Text);
        if (ScopeElementRadio.IsChecked == true)
        {
            var element = _vm.Scene.Elements.FirstOrDefault(x => x.Id == _elementId);
            if (element is null) return;
            if (!ReferenceEquals(_vm.Scene.SelectedElement, element)) _vm.SelectElement(element);
            _vm.ApplyElementCertificate(strength, modulus, density);
        }
        else ApplyClass(strength, modulus, density, reset: false);
        Refresh();
    }

    private void OnResetCertificateClick(object sender, RoutedEventArgs e)
    {
        if (ScopeElementRadio.IsChecked == true)
        {
            var element = _vm.Scene.Elements.FirstOrDefault(x => x.Id == _elementId);
            if (element is null) return;
            if (!ReferenceEquals(_vm.Scene.SelectedElement, element)) _vm.SelectElement(element);
            _vm.ApplyElementCertificate(double.NaN, double.NaN, double.NaN);
        }
        else ApplyClass(double.NaN, double.NaN, double.NaN, reset: true);
        Refresh();
    }

    /// <summary>Сертификат класса проекта (библиотека материалов): прочие марки и цены не меняются.</summary>
    private void ApplyClass(double strength, double modulus, double density, bool reset)
    {
        string code = ClassCode();
        var current = _vm.Settings.Current.StructuralLoads.Materials?.Select(g => g.Clone()).ToList() ?? [];
        var norm = MaterialLibrary.Standard(code)!;
        var grade = current.FirstOrDefault(g => g.Code == norm.Code) ?? norm.Clone();
        current.RemoveAll(g => g.Code == norm.Code);
        grade.Strength = reset || double.IsNaN(strength) ? norm.Strength : strength;
        grade.ElasticModulus = reset || double.IsNaN(modulus) ? norm.ElasticModulus : modulus;
        grade.Density = reset || double.IsNaN(density) ? norm.Density : density;
        grade.Certificate = reset ? null : grade.Certificate ?? _loc.Get("PassportCertificateDefault");
        current.Add(grade);
        _vm.ApplyMaterialLibrary(current);
    }

    // ───────────────────────────── Копирование ─────────────────────────────

    private void OnCopyClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_passport is null) return;
        var package = new DataPackage();
        package.SetText(_passport.ToReportText(Culture));
        Clipboard.SetContent(package);
        PrimaryButtonText = _loc.Get("QtyCopied");
    }
}
