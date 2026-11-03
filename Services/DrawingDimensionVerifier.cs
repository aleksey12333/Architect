using System.Globalization;

namespace AIArchitectStudio.Services;

/// <summary>
/// Размерная цепочка листа: сторона (низ/лево плана, разрез), ряд от контура (1 — проёмы и простенки, 2 — межосевые, 3 — общий
/// габарит), координаты точек на листе (мм), точки в мм модели (целые — по ним печатаются значения), печатные значения звеньев и
/// исходные расстояния модели (м) для сверки.
/// </summary>
public sealed record DimensionChain(string Side, int Row, string Title, bool Horizontal, IReadOnlyList<double> SheetPositions,
    IReadOnlyList<long> ModelMm, IReadOnlyList<long> Values, IReadOnlyList<double> RealMeters, double K)
{
    public long Total => ModelMm.Count >= 2 ? ModelMm[^1] - ModelMm[0] : 0;
}

/// <summary>Рамка текста на листе (мм): размерные числа и отметки уровней.</summary>
public readonly record struct DimensionTextBox(double X0, double Y0, double X1, double Y1, string Text, string Owner)
{
    public bool Overlaps(DimensionTextBox o, double gap = 0.15) =>
        X0 < o.X1 - gap && o.X0 < X1 - gap && Y0 < o.Y1 - gap && o.Y0 < Y1 - gap;
}

/// <summary>Размеры, выведенные на лист: цепочки и рамки текстов (заполняется при компоновке листа).</summary>
public sealed class DrawingDimensionLayout
{
    public string Sheet { get; init; } = string.Empty;
    public List<DimensionChain> Chains { get; } = [];
    public List<DimensionTextBox> Texts { get; } = [];

    /// <summary>Размеры, вынесенные на полку или вразбежку (узкие звенья).</summary>
    public int Relocated { get; set; }

    /// <summary>Свободное место для текста: рамка не пересекается с уже размещёнными.</summary>
    public bool IsFree(DimensionTextBox box) => Texts.All(t => !t.Overlaps(box));
}

/// <summary>Итог автопроверки размеров листа (или альбома).</summary>
public sealed record DimensionCheckResult(int Chains, int Segments, int ClosedChains, int Overlaps, int ValueMismatches, int Relocated, IReadOnlyList<string> Problems)
{
    public bool Passed => Problems.Count == 0;

    public string Summary(CultureInfo culture) =>
        $"размерных цепочек {Chains} (звеньев {Segments}), Σ звеньев = габарит у {ClosedChains} из {Chains}, расхождений чисел с геометрией {ValueMismatches}, " +
        $"наложений текста {Overlaps}, вынесено на полку / вразбежку {Relocated}" + (Passed ? string.Empty : " — " + string.Join("; ", Problems.Take(4)));
}

/// <summary>
/// Автопроверщик размеров листа ГОСТ 2.307-2011 перед выпуском: 1) сумма звеньев каждой цепочки строго равна её габариту, а межосевая
/// цепочка стороны — общему размеру между крайними осями (Σ l_i == L_общ); 2) каждое число равно реальному расстоянию в мм (точки модели
/// и положение на листе в масштабе, допуск 1 мм); 3) рамки текстов размеров и отметок не пересекаются (0 наложений).
/// </summary>
public static class DrawingDimensionVerifier
{
    public static DimensionCheckResult Verify(IEnumerable<DrawingDimensionLayout> layouts)
    {
        var problems = new List<string>();
        int chains = 0, segments = 0, closed = 0, overlaps = 0, mismatches = 0, relocated = 0;
        foreach (var layout in layouts)
        {
            relocated += layout.Relocated;
            foreach (var chain in layout.Chains)
            {
                chains++;
                segments += chain.Values.Count;
                long sum = chain.Values.Sum();
                if (sum == chain.Total && chain.Values.Count == chain.ModelMm.Count - 1) closed++;
                else problems.Add($"{layout.Sheet}: {chain.Title} — Σ {sum} ≠ {chain.Total} мм");
                for (int i = 0; i < chain.Values.Count; i++)
                {
                    double sheet = Math.Abs(chain.SheetPositions[i + 1] - chain.SheetPositions[i]) / chain.K * 1000;
                    double real = Math.Abs(chain.RealMeters[i + 1] - chain.RealMeters[i]) * 1000;
                    if (Math.Abs(sheet - chain.Values[i]) > 1.0 || Math.Abs(real - chain.Values[i]) > 1.0)
                    {
                        mismatches++;
                        problems.Add($"{layout.Sheet}: {chain.Title}, звено {i + 1}: {chain.Values[i]} мм, по листу {sheet:0.#}, по модели {real:0.#}");
                    }
                }
            }
            // Межосевая цепочка (ряд 2) замыкается на общий размер (ряд 3) той же стороны.
            foreach (var side in layout.Chains.GroupBy(c => c.Side))
            {
                var overall = side.FirstOrDefault(c => c.Row == 3);
                if (overall is null) continue;
                foreach (var c in side.Where(c => c.Row == 2 && c.Total != overall.Total))
                    problems.Add($"{layout.Sheet}: {c.Title} — Σ {c.Total} мм ≠ общему габариту {overall.Total} мм");
            }
            var texts = layout.Texts;
            for (int i = 0; i < texts.Count; i++)
                for (int j = i + 1; j < texts.Count; j++)
                    if (texts[i].Overlaps(texts[j]))
                    {
                        overlaps++;
                        problems.Add($"{layout.Sheet}: наложение «{texts[i].Text}» ({texts[i].Owner}) и «{texts[j].Text}» ({texts[j].Owner})");
                    }
        }
        return new DimensionCheckResult(chains, segments, closed, overlaps, mismatches, relocated, problems);
    }
}
