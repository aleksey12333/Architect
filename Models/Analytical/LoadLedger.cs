namespace AIArchitectStudio.Models.Analytical;

/// <summary>
/// Книга вертикальных нагрузок расчёта (расчётные значения, кН): по каждому элементу схемы — пришло сверху, собственная
/// приложенная нагрузка (вес, полезная, снег), передано опорам, отдано грунту и не нашло опоры. Плюс рёбра пути передачи
/// (откуда → куда, −1 — грунт) и баланс балок. Заполняется солвером попутно; на сами усилия и K_исп не влияет.
/// </summary>
public sealed class LoadLedger
{
    public LoadLedger(IReadOnlyList<AnalyticalElement> elements)
    {
        Elements = elements;
        int n = elements.Count;
        In = new double[n];
        Self = new double[n];
        Out = new double[n];
        Ground = new double[n];
        Lost = new double[n];
    }

    /// <summary>Индекс получателя «грунт» в <see cref="Edges"/>.</summary>
    public const int GroundIndex = -1;

    public IReadOnlyList<AnalyticalElement> Elements { get; }
    public double[] In { get; }
    public double[] Self { get; }
    public double[] Out { get; }
    public double[] Ground { get; }
    public double[] Lost { get; }

    /// <summary>Рёбра передачи нагрузки (индекс элемента схемы → индекс получателя или <see cref="GroundIndex"/>), без повторов.</summary>
    public HashSet<(int From, int To)> Edges { get; } = [];

    /// <summary>Балки и фермы: собранная нагрузка и сумма опорных реакций, кН (индекс элемента схемы).</summary>
    public Dictionary<int, (double Load, double Reactions)> Beams { get; } = [];

    public double TotalSelf => Self.Sum();
    public double TotalGround => Ground.Sum();
    public double TotalLost => Lost.Sum();
}
