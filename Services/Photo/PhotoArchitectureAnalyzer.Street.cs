namespace AIArchitectStudio.Services.Photo;

/// <summary>
/// Волна 4.1 — Визуальное ядро на сложных реальных снимках:
/// <list type="number">
/// <item>Уличные помехи переднего плана: растительность (зелёные и жёлто-зелёные фрактальные пятна с высокой локальной
/// текстурой) — фон, а не здание и не цвет цоколя; после сегментации грани фасада достраиваются сквозь кроны вниз до линии
/// тротуара (устойчивая прямая основания по неперекрытым столбцам), всё ниже неё (машины, павильоны) отсекается.</item>
/// <item>Небо против стекла: облака и блики в витраже не принимаются за небо — у фасада регулярная сетка импостов и межэтажных
/// рассечек (плотность резких осевых перепадов яркости), у неба её нет; заливка фона в такие пиксели не идёт.</item>
/// <item>Этажность с поправкой на перспективу: точка схода вертикалей V_y по отрезкам вертикальных граней (RANSAC), ряд
/// межэтажных перепадов перестраивается в координату u = 1/(y − V_y), в которой этажи идут с постоянным шагом.</item>
/// <item>Качество сегментации: маска, упирающаяся в края кадра (аэрофото, «уличный каньон» соседних домов), снижает доверие
/// к морфологии Визуального ядра — решение за моделью со зрением (перекрёстная сверка паспорта и ответа модели).</item>
/// </list>
/// </summary>
public static partial class PhotoArchitectureAnalyzer
{
#if DEBUG
    /// <summary>Только Debug: этажность по полосам последнего анализа (этажей/период/сила).</summary>
    public static string LastStrips { get; private set; } = string.Empty;
#endif

    // ───────────────────────────── Карты помех и структуры ─────────────────────────────

    /// <summary>Растительность и плотность резких осевых перепадов яркости (сетка импостов) по кадру.</summary>
    private static void PrepareStreet(Work k)
    {
        int w = k.W, h = k.H, n = w * h;
        k.Veg = new bool[n];
        k.Struct = new float[n];
        // Локальная текстура: СКО яркости в окне 5 × 5 (интегральные изображения).
        var mean = BoxMean(k.L, w, h, 2);
        var sq = new float[n];
        for (int i = 0; i < n; i++) sq[i] = k.L[i] * k.L[i];
        var meanSq = BoxMean(sq, w, h, 2);
        for (int i = 0; i < n; i++)
        {
            float sd = MathF.Sqrt(MathF.Max(0, meanSq[i] - mean[i] * mean[i]));
            var (hue, sat, val) = Hsv((k.R[i], k.G[i], k.B[i]));
            bool green = hue is >= 62 and <= 165 && sat > 0.2 && val is > 0.08 and < 0.9 && k.G[i] >= k.R[i] * 0.95f && k.G[i] >= k.B[i] * 1.02f;
            // Тень в кроне: тёмно-зелёный с текстурой.
            bool darkGreen = hue is >= 70 and <= 170 && sat > 0.28 && val is > 0.04 and <= 0.3;
            k.Veg[i] = (green && sd > 5.5f) || (darkGreen && sd > 4f);
        }
        // Чистка: одиночные пиксели — не растительность, мелкие дыры в кроне — растительность.
        k.Veg = Dilate(Erode(k.Veg, w, h), w, h);
        k.Veg = Erode(Dilate(k.Veg, w, h), w, h);

        // Сетка фасада: резкие перепады обеих осей рядом (импосты + рассечки, окна). Одиночная линия (горизонт, провод, кромка
        // облака) даёт только одну ось и сеткой не считается.
        var edgeV = new float[n];
        var edgeH = new float[n];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                float gx = MathF.Abs(k.L[i + 1] - k.L[i - 1]), gy = MathF.Abs(k.L[i + w] - k.L[i - w]);
                if (gx >= 16 && gx >= 2.2f * gy) edgeV[i] = 1;
                if (gy >= 16 && gy >= 2.2f * gx) edgeH[i] = 1;
            }
        var sv = BoxMean(edgeV, w, h, 4);
        var sh = BoxMean(edgeH, w, h, 4);
        for (int i = 0; i < n; i++) k.Struct[i] = MathF.Min(sv[i], sh[i]);
    }

    /// <summary>Среднее в окне (2r + 1)² по интегральному изображению (у краёв — по доступной части окна).</summary>
    private static float[] BoxMean(float[] v, int w, int h, int r)
    {
        var integral = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double row = 0;
            for (int x = 0; x < w; x++)
            {
                row += v[y * w + x];
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row;
            }
        }
        var result = new float[v.Length];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                double s = integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1] - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
                result[y * w + x] = (float)(s / ((x1 - x0) * (y1 - y0)));
            }
        }
        return result;
    }

    /// <summary>Пиксель с сеткой фасада (импосты, рассечки, окна): в окне 9 × 9 ≥ 3,5 % резких перепадов каждой из осей.</summary>
    private static bool IsFacadeStructure(Work k, int i) => k.Struct.Length > 0 && k.Struct[i] >= 0.035f;

    // ───────────────────────────── Достройка сквозь помехи ─────────────────────────────

    /// <summary>
    /// Достройка фасада сквозь кроны и отсечение уличных объектов: прямая основания (низ здания) — по столбцам, где под маской
    /// не растительность (медианный МНК, два прохода с отбрасыванием выбросов); в столбцах, перекрытых кроной или закрытых фоном
    /// у низа, маска продолжается вниз до прямой; пиксели маски ниже прямой + 3 px (машины, павильоны, ограждения) снимаются.
    /// Возвращает долю перекрытых столбцов.
    /// </summary>
    private static double RestoreOccluded(Work k, PhotoArchitectureDescriptor d)
    {
        int w = k.W, h = k.H;
        var top = new int[w];
        var bottom = new int[w];
        int x0 = w, x1 = -1;
        for (int x = 0; x < w; x++)
        {
            top[x] = bottom[x] = -1;
            for (int y = 0; y < h; y++)
                if (k.Mask[y * w + x]) { if (top[x] < 0) top[x] = y; bottom[x] = y; }
            if (top[x] >= 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); }
        }
        if (x1 - x0 < 16) return 0;
        // Столбец перекрыт, если в 6 px под низом маски — растительность (или растительность — у самого низа маски).
        bool Occluded(int x)
        {
            if (bottom[x] < 0) return false;
            int veg = 0, cnt = 0;
            for (int y = bottom[x] - 2; y <= Math.Min(h - 1, bottom[x] + 6); y++)
            {
                if (y < 0) continue;
                cnt++;
                if (k.Veg[y * w + x]) veg++;
            }
            return cnt > 0 && veg >= cnt * 0.35;
        }
        var clear = new List<(double X, double Y)>();
        var occluded = new List<int>();
        for (int x = x0; x <= x1; x++)
        {
            if (bottom[x] < 0) continue;
            if (Occluded(x)) occluded.Add(x);
            else if (bottom[x] < h - 2) clear.Add((x, bottom[x]));
        }
        double share = (double)occluded.Count / Math.Max(1, x1 - x0 + 1);
        if (clear.Count < 12 || occluded.Count == 0 && share == 0)
        {
            d.OccludedShare = share;
            return share;
        }
        // Прямая основания: МНК с отбрасыванием 30 % худших по невязке.
        var (a, b) = Line(clear);
        for (int pass = 0; pass < 2; pass++)
        {
            var res = clear.Select(p => (p, r: Math.Abs(p.Y - (a * p.X + b)))).OrderBy(t => t.r).Take(Math.Max(8, (int)(clear.Count * 0.7))).Select(t => t.p).ToList();
            (a, b) = Line(res);
        }
        if (Math.Abs(a) > 0.6) { d.OccludedShare = share; return share; }
        int restored = 0, trimmed = 0;
        for (int x = x0; x <= x1; x++)
        {
            if (top[x] < 0) continue;
            int baseY = (int)Math.Round(a * x + b);
            if (baseY <= top[x] + 2 || baseY >= h) continue;
            // Перекрытый столбец: маска вниз до основания (растительность и фон между ними — тело здания за кроной).
            if (bottom[x] < baseY - 1 && (Occluded(x) || VegShare(k, x, bottom[x] + 1, baseY) > 0.4))
            {
                for (int y = bottom[x] + 1; y <= baseY; y++) k.Mask[y * w + x] = true;
                restored++;
            }
            // Ниже тротуара — улица: машины, остановки, ограждения.
            for (int y = baseY + 4; y <= bottom[x]; y++)
                if (k.Mask[y * w + x]) { k.Mask[y * w + x] = false; trimmed++; }
        }
        // Боковые грани у низа: ряды под линией верха крон получают ширину ряда над кронами (грань продолжается до тротуара).
        int yBase = (int)Math.Round(a * (x0 + x1) / 2 + b);
        int yVegTop = h;
        for (int y = Math.Min(h - 1, yBase); y > 0 && y > yBase - h / 2; y--)
        {
            int veg = 0;
            for (int x = x0; x <= x1; x++) if (k.Veg[y * w + x]) veg++;
            if (veg > (x1 - x0) * 0.05) yVegTop = y; else if (yVegTop < h) break;
        }
        if (yVegTop < yBase - 3)
        {
            int ya = Math.Max(0, yVegTop - 4);
            int left = -1, right = -1;
            for (int x = 0; x < w; x++) if (k.Mask[ya * w + x]) { if (left < 0) left = x; right = x; }
            if (left >= 0)
                for (int y = ya + 1; y <= Math.Min(h - 1, yBase); y++)
                    for (int x = left; x <= right; x++)
                    {
                        int i = y * w + x;
                        if (!k.Mask[i] && (k.Veg[i] || VegShare(k, x, y - 3, y + 3) > 0.3)) { k.Mask[i] = true; restored++; }
                    }
        }
        d.OccludedShare = share;
        if (restored > 0 || trimmed > 0)
            d.Evidence.Add($"уличные помехи: растительность перед фасадом в {share:P0} столбцов — грани фасада достроены сквозь кроны до тротуара ({restored} px), " +
                           $"улица ниже основания отсечена ({trimmed} px)");
        d.BaseLineY0 = b / h;
        d.BaseLineY1 = (a * w + b) / h;
        return share;
    }

    /// <summary>Клетки 8 × 8 px работы с растительностью ≥ 50 % — оверлей «отфильтрованные помехи» студии.</summary>
    private static void VegetationOverlay(Work k, PhotoArchitectureDescriptor d)
    {
        const int cell = 8;
        for (int y0 = 0; y0 + cell <= k.H; y0 += cell)
            for (int x0 = 0; x0 + cell <= k.W; x0 += cell)
            {
                int veg = 0;
                for (int y = y0; y < y0 + cell; y++)
                    for (int x = x0; x < x0 + cell; x++) if (k.Veg[y * k.W + x]) veg++;
                if (veg >= cell * cell / 2) d.ClutterCells.Add(((double)x0 / k.W, (double)y0 / k.H, (double)(x0 + cell) / k.W, (double)(y0 + cell) / k.H));
            }
    }

    private static double VegShare(Work k, int x, int ya, int yb)
    {
        int veg = 0, n = 0;
        for (int y = Math.Max(0, ya); y <= Math.Min(k.H - 1, yb); y++) { n++; if (k.Veg[y * k.W + x]) veg++; }
        return n == 0 ? 0 : (double)veg / n;
    }

    // ───────────────────────────── Качество сегментации ─────────────────────────────

    /// <summary>
    /// Доверие к силуэту 0…1: маска, занимающая почти весь кадр или упирающаяся в обе боковые кромки (аэрофото, соседние дома
    /// «уличного каньона»), — низкое; силуэт на небе с отступами от краёв — высокое.
    /// </summary>
    private static void SegmentationQuality(Work k, PhotoArchitectureDescriptor d)
    {
        int w = k.W, h = k.H;
        int area = k.Mask.Count(m => m);
        double fill = (double)area / (w * h);
        int left = 0, right = 0, topEdge = 0;
        // Морфология обнуляет 2 px у кромок — касание проверяется на 3-м столбце и строке.
        for (int y = 0; y < h; y++) { if (k.Mask[y * w + 3]) left++; if (k.Mask[y * w + w - 4]) right++; }
        for (int x = 0; x < w; x++) if (k.Mask[3 * w + x]) topEdge++;
        double l = (double)left / h, r = (double)right / h, t = (double)topEdge / w;
        double q = 1.0;
        if (fill > 0.78) q -= Math.Min(0.6, (fill - 0.78) * 3);
        if (l > 0.45 && r > 0.45) q -= 0.35;
        else if (l > 0.6 || r > 0.6) q -= 0.15;
        if (t > 0.6) q -= 0.25;
        if (d.OccludedShare > 0.3) q -= 0.1;
        d.SegmentationQuality = Math.Clamp(q, 0.05, 1);
        if (d.SegmentationQuality < 0.55)
            d.Evidence.Add($"силуэт ненадёжен (доверие {d.SegmentationQuality:0.00}: кадр заполнен {fill:P0}, касание краёв слева {l:P0} / справа {r:P0} / сверху {t:P0}) — " +
                           "морфологию и этажность уточняет модель со зрением");
    }

    // ───────────────────────────── Точка схода вертикалей ─────────────────────────────

    private sealed record VSeg(double A, double B, double Y0, double Y1, double Weight)
    {
        public double Xm => A * (Y0 + Y1) / 2 + B;
        public double Ym => (Y0 + Y1) / 2;
    }

    /// <summary>
    /// Точка схода вертикалей: отрезки вертикальных граней (цепочки резких горизонтальных перепадов одной полярности длиной
    /// ≥ 4 % высоты кадра, прямая x = a·y + b) внутри здания; RANSAC по парам отрезков, инлайер — отклонение направления на точку
    /// схода ≤ 0,7°. Возвращает V_y (пиксели работы, null — вертикали параллельны или их мало) и число инлайеров.
    /// </summary>
    private static (double? Vy, double Vx, int Inliers, int Segments) VerticalVanishing(Work k)
    {
        int w = k.W, h = k.H;
        var used = new bool[w * h];
        var segs = new List<VSeg>();
        int minLen = Math.Max(10, h / 25);
        bool Edge(int x, int y, int sign)
        {
            if (x < 1 || x >= w - 1 || y < 1 || y >= h - 1) return false;
            int i = y * w + x;
            if (!k.Mask[i]) return false;
            float gx = k.L[i + 1] - k.L[i - 1], gy = k.L[i + w] - k.L[i - w];
            return Math.Sign(gx) == sign && MathF.Abs(gx) >= 18 && MathF.Abs(gx) >= 2.5f * MathF.Abs(gy);
        }
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                if (used[i]) continue;
                foreach (int sign in new[] { -1, 1 })
                {
                    if (!Edge(x, y, sign)) continue;
                    var pts = new List<(double X, double Y)>();
                    int cx = x, gap = 0;
                    for (int yy = y; yy < h - 1 && gap <= 1; yy++)
                    {
                        int best = -1;
                        for (int dx = 0; dx <= 1 && best < 0; dx++)
                            foreach (int s in dx == 0 ? new[] { 0 } : new[] { -1, 1 })
                                if (Edge(cx + s, yy, sign)) { best = cx + s; break; }
                        if (best < 0) { gap++; continue; }
                        gap = 0;
                        cx = best;
                        used[yy * w + cx] = true;
                        pts.Add((cx, yy));
                    }
                    if (pts.Count < minLen) continue;
                    // x = a·y + b (МНК по y).
                    double my = pts.Average(p => p.Y), mx = pts.Average(p => p.X);
                    double syy = pts.Sum(p => (p.Y - my) * (p.Y - my)), sxy = pts.Sum(p => (p.Y - my) * (p.X - mx));
                    if (syy < 1e-6) continue;
                    double a = sxy / syy, b = mx - a * my;
                    double rms = Math.Sqrt(pts.Average(p => Math.Pow(p.X - (a * p.Y + b), 2)));
                    if (rms > 0.9 || Math.Abs(a) > 0.45) continue;
                    segs.Add(new VSeg(a, b, pts[0].Y, pts[^1].Y, pts.Count));
                }
            }
        if (segs.Count < 6) return (null, 0, 0, segs.Count);
        segs = segs.OrderByDescending(s => s.Weight).Take(260).ToList();
        double totalW = segs.Sum(s => s.Weight);
        const double tol = 0.7 * Math.PI / 180;
        double Score(double vx, double vy, bool infinite, double meanA, out int inliers)
        {
            double score = 0;
            inliers = 0;
            foreach (var s in segs)
            {
                double ang;
                if (infinite) ang = Math.Abs(Math.Atan(s.A) - Math.Atan(meanA));
                else
                {
                    double dx = vx - s.Xm, dy = vy - s.Ym;
                    double toVp = Math.Atan2(dx, dy), own = Math.Atan(s.A);
                    // Направление отрезка без знака: сравнение по модулю π.
                    ang = Math.Abs(Math.IEEERemainder(toVp - own, Math.PI));
                }
                if (ang <= tol) { score += s.Weight; inliers++; }
            }
            return score;
        }
        // Гипотеза «вертикали параллельны» (фронтальная съёмка без наклона).
        double medianA = segs.Select(s => s.A).OrderBy(v => v).ElementAt(segs.Count / 2);
        double bestScore = Score(0, 0, true, medianA, out int bestIn);
        double? bestVy = null;
        double bestVx = 0;
        var rnd = new Random(17);
        int top = Math.Min(segs.Count, 80);
        for (int iter = 0; iter < 900; iter++)
        {
            var s1 = segs[rnd.Next(top)];
            var s2 = segs[rnd.Next(segs.Count)];
            if (Math.Abs(s1.A - s2.A) < 0.004 || Math.Abs(s1.Xm - s2.Xm) < w * 0.05) continue;
            double vy = (s2.B - s1.B) / (s1.A - s2.A), vx = s1.A * vy + s1.B;
            if (Math.Abs(vy - h / 2.0) < h * 0.6) continue;
            double score = Score(vx, vy, false, 0, out int inl);
            if (score > bestScore * 1.08)
            {
                bestScore = score;
                bestIn = inl;
                bestVy = vy;
                bestVx = vx;
            }
        }
        if (bestVy is { } v && (bestScore < totalW * 0.3 || Math.Abs(v) > 40 * h)) bestVy = null;
        return (bestVy, bestVx, bestIn, segs.Count);
    }

    // ───────────────────────────── Этажность с поправкой на перспективу ─────────────────────────────

    /// <summary>
    /// Число этажей тела здания (от основания до верха тела bodyTopPx): ряд средних |∂L/∂y| по центральной части фасада,
    /// перестроенный в равномерную сетку по u = 1/(y − V_y) (вертикальная прямая в проекции: 1/t линейно по высоте), период —
    /// автокорреляцией. Без точки схода — прежний счёт по пикселям. Доверие — сила пика автокорреляции.
    /// </summary>
    private static (int Floors, double Period, double Confidence) PerspectiveFloors(Work k, double bodyTopPx, double? vy)
    {
        // Узкие вертикальные полосы по центральной части тела: на угловом виде межэтажные линии двух фасадов наклонены к своим
        // точкам схода и в широком окне смазываются; в полосе шириной ~4 % они почти горизонтальны. Этажность — медиана полос.
        int strips = 9, sw = Math.Max(4, k.Wpx / 26);
        double maxTop = k.Top.Where(t => t >= 0).Select(t => (double)(k.YBase - t)).DefaultIfEmpty(0).Max();
        var counts = new List<(int Floors, double Period, double Strength)>();
        for (int s = 0; s < strips; s++)
        {
            int xc = k.X0 + (int)(k.Wpx * (0.18 + 0.64 * s / (strips - 1)));
            int xa = Math.Max(k.X0, xc - sw / 2), xb = Math.Min(k.X1, xc + sw / 2);
            // Местный верх и низ полосы (верх тела — не выше bodyTopPx над основанием).
            var tops = Enumerable.Range(xa, xb - xa + 1).Where(x => k.Top[x] >= 0).Select(x => k.Top[x]).ToList();
            var bottoms = Enumerable.Range(xa, xb - xa + 1).Where(x => k.Bottom[x] >= 0).Select(x => k.Bottom[x]).ToList();
            if (tops.Count == 0) continue;
            int ya = Math.Max(tops.Max(), (int)Math.Round(k.YBase - bodyTopPx)) + 2, yb = bottoms.Min() - 2;
            int n = yb - ya;
            if (n < 24 || (k.YBase - ya) < 0.5 * maxTop) continue;
            var sig = new double[n + 1];
            for (int y = ya; y <= yb; y++)
            {
                double acc = 0; int c = 0;
                for (int x = xa; x <= xb; x++)
                {
                    int i = y * k.W + x;
                    if (y + 1 >= k.H || !k.Mask[i] || !k.Mask[i + k.W] || k.Veg[i]) continue;
                    acc += Math.Abs(k.L[i + k.W] - k.L[i]);
                    c++;
                }
                sig[y - ya] = c > 0 ? acc / c : 0;
            }
            double[] series = sig;
            if (vy is { } v && Math.Abs(ya - v) > 1 && Math.Abs(yb - v) > 1)
            {
                double ua = 1 / (ya - v), ub = 1 / (yb - v);
                series = new double[n + 1];
                for (int j = 0; j <= n; j++)
                {
                    double u = ua + (ub - ua) * j / n;
                    double y = v + 1 / u - ya;
                    int y0 = Math.Clamp((int)Math.Floor(y), 0, n), y1 = Math.Min(n, y0 + 1);
                    double t = Math.Clamp(y - y0, 0, 1);
                    series[j] = sig[y0] * (1 - t) + sig[y1] * t;
                }
            }
            var (period, strength) = PeriodWithStrength(series, 4, n / 2);
            if (period <= 0 || strength < 0.15) continue;
            // Полоса покрывает от местного низа до местного верха — все этажи фасада в этой точке.
            counts.Add(((int)Math.Round((n + 4) / period), period, strength));
        }
#if DEBUG
        LastStrips = string.Join(" ", counts.Select(c => $"{c.Floors}/{c.Period:0.0}/{c.Strength:0.00}"));
#endif
        if (counts.Count < 2) return (0, 0, 0);
        var ordered = counts.OrderBy(c => c.Floors).ToList();
        var med = ordered[ordered.Count / 2];
        // Доверие: сила периодичности × согласие полос (доля полос в пределах ±12 % медианы).
        double agree = (double)counts.Count(c => Math.Abs(c.Floors - med.Floors) <= Math.Max(1, med.Floors * 0.12)) / counts.Count;
        return (med.Floors, med.Period, Math.Clamp(counts.Average(c => c.Strength) * agree, 0, 1));
    }

    /// <summary>
    /// Изгиб плана по кромке покрытия: у плоского фасада кромка плоской кровли проецируется прямой (и при перспективе), у
    /// дугового — кривой. Верх тела (столбцы выше 55 % максимума) без купола и ступеней: остаток прямой ≥ 1,6 % ширины тела, без
    /// излома (двухотрезочная аппроксимация не лучше гладкой); число чередующихся долей остатка ≥ 1,5 % — число дуг фасада
    /// (C — 1, S — 2, волна — 3+), знак центральной доли — выпуклость к зрителю (верх выше прямой — выпуклая).
    /// </summary>
    private static (bool Curved, int Arcs, double SignedRatio) TopEdgeWave(Work k, double[] hTop)
    {
        int n = hTop.Length;
        double max = hTop.Max();
        int b0 = 0, b1 = n - 1;
        while (b0 < n - 1 && hTop[b0] < 0.55 * max) b0++;
        while (b1 > b0 && hTop[b1] < 0.55 * max) b1--;
        int span = b1 - b0 + 1;
        if (span < 40) return (false, 0, 0);
        // Отступ от боковых граней (скругления углов и обрез силуэта).
        int a = b0 + span / 14, b = b1 - span / 14;
        var s = Smooth(hTop.Skip(a).Take(b - a + 1).ToArray(), Math.Max(2, span / 60));
        var pts = s.Select((v, i) => ((double)i, v)).ToList();
        var (la, lb) = Line(pts);
        var res = s.Select((v, i) => v - (la * i + lb)).ToArray();
        double rms = Math.Sqrt(res.Average(r => r * r));
        if (rms < 0.016 * span * 0.5 && rms < 0.012 * k.Hpx) return (false, 0, 0);
        // Резкие скачки (ступени, купол) — не дуга.
        for (int i = 2; i < s.Length - 2; i++) if (Math.Abs(s[i + 2] - s[i - 2]) > 0.05 * k.Hpx) return (false, 0, 0);
        // Экстремумы самого профиля с гистерезисом (выступ ≥ 3 % высоты — мелкие «заусенцы» облаков у кромки не считаются):
        // горб — максимум, впадина — минимум. Концы — не экстремумы.
        double thr = Math.Max(2, 0.03 * k.Hpx);
        var extrema = new List<(int I, double V, bool Max)>();
        int dir = 0;
        int lastI = 0;
        double lastV = s[0];
        for (int i = 1; i < s.Length; i++)
        {
            if (dir >= 0 && s[i] > lastV) { lastV = s[i]; lastI = i; if (dir == 0 && s[i] - s[0] >= thr) dir = 1; continue; }
            if (dir <= 0 && s[i] < lastV) { lastV = s[i]; lastI = i; if (dir == 0 && s[0] - s[i] >= thr) dir = -1; continue; }
            if (dir == 1 && lastV - s[i] >= thr) { extrema.Add((lastI, lastV, true)); dir = -1; lastV = s[i]; lastI = i; }
            else if (dir == -1 && s[i] - lastV >= thr) { extrema.Add((lastI, lastV, false)); dir = 1; lastV = s[i]; lastI = i; }
        }
        extrema.RemoveAll(e => e.I < s.Length * 0.06 || e.I > s.Length * 0.94);
        if (extrema.Count == 0 || extrema.Count > 5) return (false, 0, 0);
        if (extrema.Count == 1)
        {
            // Один горб: дуга, только если он гладкий (парабола не хуже двух прямых) — излом на стрелке угла дугой не считается.
            double two = FitTwoLines(s, extrema[0].I);
            double quad = FitQuadratic(s, out _);
            if (two < quad * 0.85 || !extrema[0].Max) return (false, 0, 0);
        }
        // Центральный экстремум: горб (выше соседей) — середина фасада ближе к зрителю → выпуклая; впадина — вогнутая.
        var central = extrema[extrema.Count / 2];
        double amp = extrema.Count > 1 ? extrema.Max(e => e.V) - extrema.Min(e => e.V) : Math.Abs(res[central.I]);
        double ratio = Math.Clamp(amp / span * 4, 0.08, 0.3) * (central.Max ? 1 : -1);
        return (true, extrema.Count, ratio);
    }

    /// <summary>
    /// То же, что <see cref="Period"/> (наименьший лаг с автокорреляцией ≥ 80 % наибольшей), плюс нормированная сила пика (0…1).
    /// Гармоническая сумма r(L) + r(2L) + … проверялась на реальных снимках и уступила: низкочастотный контраст (стилобат —
    /// башня) перетягивает её на длинные лаги. Ошибки полупериода закрывает сверка с моделью со зрением.
    /// </summary>
    private static (double Period, double Strength) PeriodWithStrength(double[] sig, int minLag, int maxLag)
    {
        double p = Period(sig, minLag, maxLag);
        if (p <= 0) return (0, 0);
        int n = sig.Length;
        var trend = Smooth(sig, 8);
        var v = sig.Select((x, i) => x - trend[i]).ToArray();
        double var0 = v.Sum(x => x * x);
        int lag = (int)Math.Round(p);
        double acc = 0;
        for (int i = 0; i + lag < n; i++) acc += v[i] * v[i + lag];
        return (p, Math.Clamp(acc / Math.Max(1e-9, var0) * n / Math.Max(1, n - lag), 0, 1));
    }
}
