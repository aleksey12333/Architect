using System.Text;
using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio.Views;

/// <summary>
/// «Аудит расчётной схемы»: плитки баланса (ΣF_верт, ΣR_y грунта, невязка, критерии), карточки пяти критериев логики КЭ-расчёта с
/// нарушителями, таблица напряжений несущих элементов (N, M, σ_max, R, τ, R_s, η, K_исп) и узлов продавливания (положение колонны,
/// τ = F / (u·h0), F_sw,ult). «Копировать отчёт» — текст для пояснительной записки, окно не закрывается.
/// </summary>
public sealed partial class FeaAuditDialog : ContentDialog
{
    private readonly FeaAuditReport _report;
    private readonly LocalizationService _loc;

    public FeaAuditDialog(MainViewModel viewModel, FeaAuditReport report)
    {
        _report = report;
        _loc = viewModel.Loc;
        InitializeComponent();
        CloseButtonText = _loc.Get("Close");
        TitleText.Text = _loc.Get("W2AuditTitle");
        var c = _loc.Culture;
        int passed = report.Criteria.Count(x => x.Passed);
        SubtitleText.Text = _loc.Format("W2AuditSubtitle", report.ElementCount, report.ElapsedMs.ToString("0", c),
            report.Passed ? _loc.Get("W2AuditOk") : _loc.Get("W2AuditFail"));
        if (!report.Passed)
        {
            VerdictBadge.Background = Hex("#FF453A", 0x26);
            VerdictGlyph.Glyph = "\uE7BA";
            VerdictGlyph.Foreground = Hex("#FF453A");
        }
        HeaderActions.Children.Add(ActionButton("\uE8C8", _loc.Get("W2AuditCopy"), (_, _) => CopyReport()));
        Opened += (_, _) => FocusSink.Focus(FocusState.Programmatic);

        AddTile(0, _loc.Get("W2AuditApplied"), report.AppliedLoad.ToString("N1", c) + " кН", null);
        AddTile(1, _loc.Get("W2AuditGround"), report.GroundReaction.ToString("N1", c) + " кН", null);
        AddTile(2, _loc.Get("W2AuditImbalance"), report.ImbalancePercent.ToString("0.000", c) + " %",
            Math.Abs(report.ImbalancePercent) <= FeaLogicalAuditService.BalanceTolerance * 100 ? "#30D158" : "#FF453A");
        AddTile(3, _loc.Get("W2AuditCriteria"), $"{passed} / {report.Criteria.Count}", report.Passed ? "#30D158" : "#FF9F0A");

        BodyPanel.Children.Add(Caption(_loc.Get("W2AuditCriteriaCaption")));
        foreach (var criterion in report.Criteria) BodyPanel.Children.Add(CriterionCard(criterion));
        BodyPanel.Children.Add(Caption(_loc.Get("W2AuditStressCaption")));
        BodyPanel.Children.Add(StressTable());
        if (report.Punching.Count > 0)
        {
            BodyPanel.Children.Add(Caption(_loc.Get("W2AuditPunchCaption")));
            BodyPanel.Children.Add(PunchingTable());
        }
    }

    private void AddTile(int column, string caption, string value, string? hex)
    {
        TilesGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(Caption(caption));
        var text = Text(value, 18, strong: true, mono: true);
        if (hex is not null) text.Foreground = Hex(hex);
        panel.Children.Add(text);
        var card = Card(panel, 12);
        Grid.SetColumn(card, column);
        TilesGrid.Children.Add(card);
    }

    private Border CriterionCard(FeaAuditCriterion criterion)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string hex = criterion.Passed ? "#30D158" : "#FF453A";
        grid.Children.Add(GlyphBadge(criterion.Passed ? "\uE73E" : "\uE711", 28, Hex(hex, 0x26), Hex(hex)));
        var body = new StackPanel { Spacing = 3 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Text($"{criterion.Number}. {criterion.Title}", 13, strong: true));
        head.Children.Add(Chip(criterion.Passed ? _loc.Get("W2AuditPassed") : _loc.Get("W2AuditFailed"), hex));
        body.Children.Add(head);
        body.Children.Add(Text(criterion.Detail, 11.5, secondary: true, wrap: true));
        foreach (string offender in criterion.Offenders.Take(6))
            body.Children.Add(Text("· " + offender, 11, wrap: true, mono: true));
        if (criterion.Offenders.Count > 6) body.Children.Add(Text(_loc.Format("W2AuditMore", criterion.Offenders.Count - 6), 11, secondary: true));
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return Card(grid, 12);
    }

    private static Grid Table(IReadOnlyList<(string Header, double Width)> columns)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        foreach (var (_, width) in columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width > 0 ? new GridLength(width) : new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < columns.Count; i++)
        {
            var header = Text(columns[i].Header, 10.5, strong: true, secondary: true);
            if (i > 0) header.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(header, i);
            grid.Children.Add(header);
        }
        return grid;
    }

    private static void Row(Grid grid, IReadOnlyList<string> cells, string? ratioHex = null)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < cells.Count; i++)
        {
            var t = Text(cells[i], 11.5, mono: i > 0);
            if (i > 0) t.HorizontalAlignment = HorizontalAlignment.Right;
            if (i == cells.Count - 2 && ratioHex is not null) t.Foreground = Hex(ratioHex);
            Grid.SetRow(t, row);
            Grid.SetColumn(t, i);
            grid.Children.Add(t);
        }
    }

    private static string RatioHex(double ratio) => ratio > 1 ? "#FF453A" : ratio > 0.95 ? "#FF9F0A" : ratio > 0.7 ? "#FFD60A" : "#30D158";

    private Border StressTable()
    {
        var c = _loc.Culture;
        var grid = Table([
            (_loc.Get("W2ColElement"), 0), ("N, кН", 70), ("M, кН·м", 74), ("σmax, МПа", 80), ("R, МПа", 66), ("τ, МПа", 62), ("Rs, МПа", 62), ("η", 52), ("Kисп", 52)
        ]);
        foreach (var r in _report.Stresses.Take(18))
        {
            string ratio = r.Reference ? "(" + r.StressRatio.ToString("0.00", c) + ")" : r.StressRatio.ToString("0.00", c);
            Row(grid, [
                $"{r.Title} · {r.Kind}", r.N > 0.05 ? r.N.ToString("N0", c) : "—", (r.Mx + r.My) > 0.005 ? (r.Mx + r.My).ToString("N1", c) : "—",
                r.SigmaMax.ToString("0.00", c), r.RSection.ToString("0.0", c), r.Tau.ToString("0.00", c), r.RShear.ToString("0.00", c), ratio, r.Utilization.ToString("0.00", c)
            ], RatioHex(r.StressRatio));
        }
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(grid);
        panel.Children.Add(Text(_loc.Format("W2AuditStressNote", _report.Stresses.Count), 10.5, secondary: true, wrap: true));
        return Card(panel, 12);
    }

    private Border PunchingTable()
    {
        var c = _loc.Culture;
        var grid = Table([
            (_loc.Get("W2ColColumn"), 0), (_loc.Get("W2ColPosition"), 110), ("F, кН", 70), ("u, м", 58), ("h0, мм", 60), ("τ, МПа", 64), ("Rbt, МПа", 70), ("Fsw, кН", 70), ("K", 52)
        ]);
        foreach (var p in _report.Punching.Take(14))
            Row(grid, [
                p.Title, $"{p.Position} · {p.Faces}", p.Force.ToString("N0", c), p.Perimeter.ToString("0.00", c), (p.H0 * 1000).ToString("0", c),
                p.Tau.ToString("0.000", c), p.Rbt.ToString("0.00", c), p.SteelCapacity > 0 ? p.SteelCapacity.ToString("N0", c) : "—", p.Ratio.ToString("0.00", c)
            ], RatioHex(p.Ratio));
        return Card(grid, 12);
    }

    /// <summary>Текст отчёта аудита (для записки): баланс, критерии, напряжения и продавливание.</summary>
    public static string ReportText(FeaAuditReport report)
    {
        var sb = new StringBuilder();
        var c = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        sb.AppendLine("АУДИТ РАСЧЁТНОЙ СХЕМЫ");
        sb.AppendLine($"ΣF_верт = {report.AppliedLoad.ToString("N1", c)} кН; ΣR_y = {report.GroundReaction.ToString("N1", c)} кН; невязка {report.ImbalancePercent.ToString("0.000", c)} %");
        foreach (var k in report.Criteria)
        {
            sb.AppendLine($"{k.Number}. {k.Title}: {(k.Passed ? "выполнено" : "НЕ ВЫПОЛНЕНО")} — {k.Detail}");
            foreach (string o in k.Offenders) sb.AppendLine("   · " + o);
        }
        sb.AppendLine("Элемент\tN, кН\tM, кН·м\tσmax, МПа\tR, МПа\tτ, МПа\tRs, МПа\tη\tKисп");
        foreach (var r in report.Stresses)
            sb.AppendLine($"{r.Title}\t{r.N.ToString("0.#", c)}\t{(r.Mx + r.My).ToString("0.##", c)}\t{r.SigmaMax.ToString("0.###", c)}\t{r.RSection.ToString("0.##", c)}\t" +
                          $"{r.Tau.ToString("0.###", c)}\t{r.RShear.ToString("0.###", c)}\t{r.StressRatio.ToString("0.###", c)}\t{r.Utilization.ToString("0.###", c)}");
        foreach (var p in report.Punching)
            sb.AppendLine($"Продавливание «{p.Title}» ({p.Position}, граней {p.Faces}): F = {p.Force.ToString("0.#", c)} кН, u = {p.Perimeter.ToString("0.###", c)} м, " +
                          $"τ = {p.Tau.ToString("0.###", c)} МПа, Rbt = {p.Rbt.ToString("0.##", c)} МПа, F_sw,ult = {p.SteelCapacity.ToString("0.#", c)} кН, K = {p.Ratio.ToString("0.###", c)}");
        return sb.ToString();
    }

    private void CopyReport()
    {
        var package = new DataPackage();
        package.SetText(ReportText(_report));
        Clipboard.SetContent(package);
    }
}
