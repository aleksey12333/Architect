using System.Diagnostics;

namespace AIArchitectStudio.Services.Photo;

/// <summary>
/// Визуальное ядро: анализ снимка здания без нейросети (чистые функции по пикселям). Конвейер:
/// <list type="number">
/// <item>Уменьшение до 400 px по ширине (усреднение блоками).</item>
/// <item>Модель фона по каждой строке — медианы левой и правой кромок кадра (небо сверху, земля снизу), порог по шуму кромок;
/// тени на земле (та же цветность, темнее) — фон; наибольшая связная область у центра кадра — здание, дыры заполняются.</item>
/// <item>Профили силуэта: верх и низ по столбцам, ширина по строкам (стилобат — устойчивое сужение в нижней трети).</item>
/// <item>Ярусы — горизонтальные участки верха силуэта; купол и свод — «горб» с эллиптической формой (СКО аппроксимации),
/// скатная кровля — две прямые к коньку.</item>
/// <item>Угол на ближнем углу — по точкам схода линии карниза и линии цоколя каждого фасада (горизонтальный угол обзора 60°);
/// изогнутый фасад — гладкий верх без излома (доля второй разности в окне излома, сравнение квадратичной и двухотрезочной
/// аппроксимаций) и плавный перепад яркости по фасаду.</item>
/// <item>Этажность и ритм окон — автокорреляция перепадов яркости по высоте и по ширине фасада.</item>
/// <item>Палитра — k-средних (k = 6, детерминированная инициализация) по пикселям здания: стена, стекло, кровля, акцент, обвязка.</item>
/// </list>
/// </summary>
public static partial class PhotoArchitectureAnalyzer
{
    public const int WorkWidth = 640;

#if DEBUG
    /// <summary>Только Debug: маска здания последнего анализа (для отладки сегментации).</summary>
    public static (bool[] Mask, int W, int H)? LastMask { get; private set; }

    /// <summary>Только Debug: трассировка признаков последнего анализа.</summary>
    public static string LastTrace { get; private set; } = string.Empty;

    /// <summary>Только Debug: снимок с маской, растительностью и линией основания (BGRA, размер работы).</summary>
    public static (byte[] Bgra, int W, int H)? LastComposite { get; private set; }
#endif

    private sealed class Work
    {
        public int W, H;
        public float[] R = [], G = [], B = [], L = [];
        public bool[] Mask = [];
        /// <summary>Растительность переднего плана (волна 4.1).</summary>
        public bool[] Veg = [];
        /// <summary>Плотность резких осевых перепадов (сетка импостов фасада) в окне 9 × 9.</summary>
        public float[] Struct = [];
        public int[] Top = [], Bottom = [];
        public int X0, X1, YTop, YBase;
        public int Wpx => X1 - X0 + 1;
        public int Hpx => YBase - YTop + 1;
        public float Lum(int x, int y) => L[y * W + x];
        public bool In(int x, int y) => Mask[y * W + x];
    }

    public static PhotoArchitectureDescriptor Analyze(RasterPlanTracer.RasterImage image, string name)
    {
        var sw = Stopwatch.StartNew();
        var d = new PhotoArchitectureDescriptor { SourceName = name, PixelWidth = image.Width, PixelHeight = image.Height };
        var k = Downscale(image);
        PrepareStreet(k);
        Segment(k);
        RestoreOccluded(k, d);
        FillHoles(k.Mask, k.W, k.H);
        SegmentationQuality(k, d);
        VegetationOverlay(k, d);
        if (!Profiles(k))
        {
            d.Morphology = PhotoMorphology.Box;
            d.Confidence = 0.2;
            d.Evidence.Add("здание не отделено от фона — принят прямоугольный корпус");
            Finish(d, k, sw);
            return d;
        }

        d.BoxX0 = (double)k.X0 / k.W;
        d.BoxX1 = (double)(k.X1 + 1) / k.W;
        d.BoxTop = (double)k.YTop / k.H;
        d.BoxBase = (double)(k.YBase + 1) / k.H;
        for (int x = k.X0; x <= k.X1; x += 2)
        {
            if (k.Top[x] < 0) continue;
            d.TopProfile.Add(((double)x / k.W, (double)k.Top[x] / k.H));
            d.BottomProfile.Add(((double)x / k.W, (double)(k.Bottom[x] + 1) / k.H));
        }

        Palette(k, d);
        var hTop = SmoothedHeights(k);
        int peak = PeakIndex(k, hTop);
        var dome = DetectDome(k, hTop, peak);
        var archEnd = DetectArchEnd(k, hTop);
        var tiers = DetectSteppedTiers(k, hTop);
        bool cornerView = CornerView(k, hTop, peak);
#if DEBUG
        LastMask = (k.Mask, k.W, k.H);
        {
            // Композиция для калибровки: фон затемнён, растительность — зелёным, линия основания — красной.
            var px = new byte[k.W * k.H * 4];
            for (int i = 0; i < k.Mask.Length; i++)
            {
                float f = k.Mask[i] ? 1f : 0.35f;
                float r = k.R[i] * f, g = k.G[i] * f, b = k.B[i] * f;
                if (k.Veg[i]) { r = r * 0.4f; g = g * 0.4f + 150; b *= 0.4f; }
                px[i * 4] = (byte)Math.Clamp(b, 0, 255); px[i * 4 + 1] = (byte)Math.Clamp(g, 0, 255); px[i * 4 + 2] = (byte)Math.Clamp(r, 0, 255); px[i * 4 + 3] = 255;
            }
            if (d.BaseLineY0 is { } by0 && d.BaseLineY1 is { } by1)
                for (int x = 0; x < k.W; x++)
                {
                    int y = (int)Math.Round((by0 + (by1 - by0) * x / k.W) * k.H);
                    if (y >= 0 && y < k.H) { int o = (y * k.W + x) * 4; px[o] = 0; px[o + 1] = 0; px[o + 2] = 255; }
                }
            LastComposite = (px, k.W, k.H);
        }
        var tr = new System.Text.StringBuilder();
        tr.AppendLine($"[trace] segQ={d.SegmentationQuality:0.00} occluded={d.OccludedShare:0.00} vp={(d.VanishingY is { } vyy ? vyy.ToString("0.00") : "-")} floorsRaw={d.FloorsUncorrected} floorConf={d.FloorConfidence:0.00}");
        tr.AppendLine($"[trace] W={k.W} H={k.H} box x {k.X0}..{k.X1} y {k.YTop}..{k.YBase} n={hTop.Length} Hpx={k.Hpx} peak={peak} corner={cornerView} dome={(dome is null ? "-" : dome.ToString())} arch={(archEnd is null ? "-" : archEnd.ToString())} tiers={tiers.Count}");
        tr.Append("[trace] hTop: ");
        for (int i = 0; i < hTop.Length; i += Math.Max(1, hTop.Length / 60)) tr.Append(Math.Round(hTop[i])).Append(' ');
        tr.AppendLine();
        tr.Append("[trace] bottom: ");
        for (int i = 0; i < hTop.Length; i += Math.Max(1, hTop.Length / 60)) tr.Append(k.Bottom[k.X0 + i]).Append(' ');
        tr.AppendLine();
        tr.AppendLine("[trace] bend: " + FloorLineCurvature(k, hTop, peak));
        LastTrace = tr.ToString();
#endif
        double bodyTopPx = dome is { } hm ? hm.BaseLevel : archEnd is { } ae ? ae.SpringLevel : hTop.Max();

        // Этажность — по периодичности перепадов яркости на теле здания.
        double floorPeriod = RowPeriod(k, (int)Math.Round(k.YBase - bodyTopPx));
        d.FloorPeriodPx = floorPeriod;
        double windowPeriod = ColumnPeriod(k, floorPeriod);
        d.WindowPeriodPx = windowPeriod;
        d.WindowColumns = windowPeriod > 0 ? Math.Max(1, (int)Math.Round(k.Wpx / windowPeriod)) : 0;
        int floors = floorPeriod > 0 ? (int)Math.Round(bodyTopPx / floorPeriod) : 0;
        d.FloorsUncorrected = floors;
        // Волна 4.1: поправка на перспективное сжатие верхних этажей (точка схода вертикалей).
        var vp = VerticalVanishing(k);
        if (vp.Vy is { } vyv) { d.VanishingY = vyv / k.H; d.VanishingX = vp.Vx / k.W; }
        var (pf, _, strength) = PerspectiveFloors(k, bodyTopPx, vp.Vy);
        if (pf > 0 && strength >= 0.12)
        {
            d.FloorConfidence = strength;
            if (pf != floors)
                d.Evidence.Add(vp.Vy is { } vy
                    ? $"перспектива: вертикали сходятся в V_y = {vy / k.H:0.00} высоты кадра ({vp.Inliers} из {vp.Segments} граней) — этажность {floors} → {pf} по шагу в координате u = 1/(y − V_y), медиана узких полос"
                    : $"этажность {floors} → {pf}: медиана узких вертикальных полос (наклонные межэтажные линии углового вида не смазываются)");
            floors = pf;
        }
#if DEBUG
        LastTrace += $"[trace] strips: {LastStrips} → {pf} (conf {strength:0.00}), raw {d.FloorsUncorrected}, vp {(vp.Vy is { } tv ? (tv / k.H).ToString("0.00") : "-")}{Environment.NewLine}";
#endif
        double aspect = (double)k.Wpx / k.Hpx;

        if (archEnd is { } v && aspect >= 1.3 && dome is null)
        {
            d.Morphology = PhotoMorphology.ArchSpan;
            d.Roof = PhotoRoofKind.Vault;
            d.DomeWidthRatio = v.Width / Math.Max(1.0, k.Wpx);
            d.DomeHeightRatio = v.Rise / Math.Max(1.0, k.Hpx);
            d.Confidence = Math.Clamp(0.55 + v.Rise / k.Hpx, 0.55, 0.93);
            d.Evidence.Add($"свод: арочный торец у края силуэта (подъём {v.Rise / k.Hpx:P0} высоты на {v.Width / k.Wpx:P0} ширины, гладкое уменьшение уклона) и пологий конёк на {v.RidgeShare:P0} ширины");
            floors = 1;
        }
        else if (dome is { } dm)
        {
            d.Morphology = PhotoMorphology.Rotunda;
            d.Roof = PhotoRoofKind.Dome;
            d.DomeWidthRatio = dm.WidthRatio;
            d.DomeHeightRatio = dm.Height / Math.Max(1, k.Hpx);
            d.ColonnadeColumns = Colonnade(k, dm);
            d.Confidence = Math.Clamp(1 - dm.CircleError * 4, 0.5, 0.95);
            d.Evidence.Add($"купол: скруглённый горб шириной {dm.WidthRatio:P0} фасада на плечах (уклон у вершины / у основания {dm.Roundness:0.00}, СКО окружности {dm.CircleError:0.000})");
            if (d.ColonnadeColumns > 0) d.Evidence.Add($"колоннада: {d.ColonnadeColumns} колонн в нижней зоне");
            if (RegionColor(k, dm.From, dm.To, dm.BaseLevel) is { } domeHex) d.RoofHex = domeHex;
        }
        else if (tiers.Count >= 3)
        {
            d.Morphology = PhotoMorphology.Cascading;
            d.Roof = PhotoRoofKind.Parapet;
            d.Confidence = Math.Clamp(0.55 + 0.08 * tiers.Count, 0.55, 0.95);
            d.Evidence.Add($"каскад: {tiers.Count} ярусов-уступов, разделённых вертикальными скачками силуэта");
        }
        else if (!cornerView && IsPointed(k, hTop, peak))
        {
            d.Morphology = PhotoMorphology.Pitched;
            d.Roof = PhotoRoofKind.Gable;
            d.DomeHeightRatio = (hTop[peak] - Math.Min(hTop[0], hTop[^1])) / Math.Max(1, k.Hpx);
            d.Confidence = 0.7;
            d.Evidence.Add("скатная кровля: две прямые к коньку при горизонтальном цоколе");
        }
        else
        {
            var corner = cornerView ? CornerAnalysis(k, hTop, d) : new Corner(0, false, 1, 1, 0, 0);
            var bend = FloorLineCurvature(k, hTop, peak);
            var wave = TopEdgeWave(k, hTop);
            bool waveCurved = wave.Curved && (wave.Arcs >= 2 || !cornerView);
            if (bend.Curved && !(corner.Angle is > 0 and < 72 && bend.Sagitta < 0.02) || waveCurved)
            {
                d.Morphology = PhotoMorphology.Curved;
                // Стрела линии этажа на снимке сжата перспективой (вид под углом ~30–40° к фасаду): коэффициент 5 к стреле дуги плана.
                d.ArcBulgeRatio = bend.Curved ? Math.Clamp(bend.Sagitta * 5, 0.12, 0.3) : Math.Abs(wave.SignedRatio);
                if (waveCurved)
                {
                    d.FacadeArcs = wave.Arcs;
                    d.ArcBulgeRatio = Math.Abs(d.ArcBulgeRatio) * Math.Sign(wave.SignedRatio);
                }
                d.CornerRadiusRatio = 0.18;
                d.Confidence = Math.Clamp(0.55 + (0.5 - bend.Ratio) * 0.8, 0.55, 0.92);
                if (bend.Curved) d.Evidence.Add($"изогнутый фасад: межэтажные линии — дуги (остаток параболы / прямой {bend.Ratio:0.00}, стрела {bend.Sagitta:P1} ширины фасада)");
                if (waveCurved)
                    d.Evidence.Add($"кромка покрытия — кривая из {wave.Arcs} {(wave.Arcs == 1 ? "дуги" : "чередующихся дуг")} " +
                                   $"({(wave.Arcs >= 3 ? "волна" : wave.Arcs == 2 ? "S-образный фасад" : "C-дуга")}, центр {(wave.SignedRatio < 0 ? "вогнутый" : "выпуклый")}, стрела/хорда {Math.Abs(wave.SignedRatio):0.00})");
                AddCurvature(k, d, peak);
            }
            else if (corner.Angle is > 0 and < 72)
            {
                d.Morphology = PhotoMorphology.Triangular;
                d.CornerAngleDeg = corner.Angle;
                d.CornerRadiusRatio = Math.Max(0.05, corner.Rounding);
                d.Confidence = Math.Clamp(0.5 + (72 - corner.Angle) / 60, 0.5, 0.92);
                d.Evidence.Add($"острый угол на стрелке: {corner.Angle:0} ° по точкам схода карниза и цоколя; фасады прямые (межэтажные линии — прямые)");
            }
            else if (corner.Rounding > 0.05)
            {
                d.Morphology = PhotoMorphology.Rounded;
                d.CornerAngleDeg = corner.Angle;
                d.CornerRadiusRatio = corner.Rounding;
                d.Confidence = 0.6;
                d.Evidence.Add($"скруглённый угол: переход яркости на {corner.Rounding:P0} ширины");
            }
            else
            {
                d.Morphology = PhotoMorphology.Box;
                d.CornerAngleDeg = corner.Angle;
                d.Confidence = 0.55;
                d.Evidence.Add(corner.Angle > 0 ? $"прямой угол {corner.Angle:0} °" : "фронтальный прямоугольный фасад");
            }
        }

        foreach (var t in tiers) d.Tiers.Add(t with { Floors = floorPeriod > 0 ? Math.Max(1, (int)Math.Round(t.TopFraction * k.Hpx / floorPeriod)) : 0 });
        if (d.Morphology is PhotoMorphology.Curved or PhotoMorphology.Box or PhotoMorphology.Rounded or PhotoMorphology.Triangular)
            Podium(k, d, floorPeriod);

        Dimensions(d, k, floors, bodyTopPx);
        for (int i = 1; i < Math.Min(d.Floors, 80); i++)
            d.FloorLines.Add((k.YBase + 1 - bodyTopPx * i / d.Floors) / k.H);
        Finish(d, k, sw);
        return d;
    }

    private static void Finish(PhotoArchitectureDescriptor d, Work k, Stopwatch sw)
    {
        if (d.Palette.Count == 0) Palette(k, d);
        sw.Stop();
        d.AnalysisMs = sw.Elapsed.TotalMilliseconds;
    }

    // ───────────────────────────── Подготовка ─────────────────────────────

    private static Work Downscale(RasterPlanTracer.RasterImage img)
    {
        int w = Math.Min(WorkWidth, img.Width);
        int h = Math.Max(8, (int)Math.Round((double)img.Height * w / img.Width));
        var k = new Work { W = w, H = h, R = new float[w * h], G = new float[w * h], B = new float[w * h], L = new float[w * h] };
        double sx = (double)img.Width / w, sy = (double)img.Height / h;
        for (int y = 0; y < h; y++)
        {
            int ya = (int)(y * sy), yb = Math.Max(ya + 1, (int)((y + 1) * sy));
            for (int x = 0; x < w; x++)
            {
                int xa = (int)(x * sx), xb = Math.Max(xa + 1, (int)((x + 1) * sx));
                double r = 0, g = 0, b = 0;
                int n = 0;
                for (int yy = ya; yy < yb && yy < img.Height; yy++)
                    for (int xx = xa; xx < xb && xx < img.Width; xx++)
                    {
                        int o = (yy * img.Width + xx) * 4;
                        b += img.Bgra[o]; g += img.Bgra[o + 1]; r += img.Bgra[o + 2];
                        n++;
                    }
                n = Math.Max(1, n);
                int i = y * w + x;
                k.R[i] = (float)(r / n); k.G[i] = (float)(g / n); k.B[i] = (float)(b / n);
                k.L[i] = 0.299f * k.R[i] + 0.587f * k.G[i] + 0.114f * k.B[i];
            }
        }
        return k;
    }

    // ───────────────────────────── Отделение здания от фона ─────────────────────────────

    private static void Segment(Work k)
    {
        int w = k.W, h = k.H, m = Math.Max(3, (int)(w * 0.03));
        var bgL = new (float R, float G, float B)[h];
        var bgR = new (float R, float G, float B)[h];
        var noise = new float[h];
        for (int y = 0; y < h; y++)
        {
            bgL[y] = Median(k, y, 0, m);
            bgR[y] = Median(k, y, w - m, w);
            float dev = 0;
            for (int x = 0; x < m; x++)
            {
                dev += Dist(k, y * w + x, bgL[y]);
                dev += Dist(k, y * w + w - 1 - x, bgR[y]);
            }
            noise[y] = dev / (2 * m);
        }
        // Фон — заливка от кромок кадра по гладкости цвета (сглаженного 3 × 3): сосед принимается, если отличается от текущего
        // меньше чем на 13 (небо, облака, мощение) и не слишком далёк от модели фона строки (≤ 75, похож на небо или тень).
        var br = Blur(k.R, w, h); var bgc = Blur(k.G, w, h); var bb = Blur(k.B, w, h);
        var background = new bool[w * h];
        // Происхождение заливки: 1 — от неба (верхняя кромка и верх боковых), 2 — от земли.
        var origin = new byte[w * h];
        var queue = new Queue<int>();
        void Seed(int i, byte from)
        {
            if (background[i]) return;
            background[i] = true;
            origin[i] = from;
            queue.Enqueue(i);
        }
        for (int x = 0; x < w; x++) { Seed(x, 1); Seed((h - 1) * w + x, 2); }
        for (int y = 0; y < h; y++) { byte from = y < h * 0.45 ? (byte)1 : (byte)2; Seed(y * w, from); Seed(y * w + w - 1, from); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w, y = i / w;
            bool fromSky = origin[i] == 1;
            for (int dir = 0; dir < 4; dir++)
            {
                int nx = x + (dir == 0 ? 1 : dir == 1 ? -1 : 0), ny = y + (dir == 2 ? 1 : dir == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int q = ny * w + nx;
                if (background[q]) continue;
                // Растительность переднего плана — всегда фон (фасад за кроной достраивается после сегментации).
                if (k.Veg[q]) { background[q] = true; origin[q] = origin[i]; queue.Enqueue(q); continue; }
                // Заливка неба не входит в сетку импостов (облака и блики в витраже — фасад, а не небо).
                if (fromSky && IsFacadeStructure(k, q)) continue;
                bool smooth = fromSky && k.Struct[q] < 0.012f;
                float dr = br[q] - br[i], dg = bgc[q] - bgc[i], db = bb[q] - bb[i];
                float step2 = dr * dr + dg * dg + db * db;
                // Пасмурное небо: мягкие перепады облаков допускаются шире там, где нет сетки фасада.
                float stepLimit = smooth ? 19f : 13f;
                if (step2 >= stepLimit * stepLimit) continue;
                float t = (float)nx / Math.Max(1, w - 1);
                var a = bgL[ny];
                var b2 = bgR[ny];
                var model = (R: a.R + (b2.R - a.R) * t, G: a.G + (b2.G - a.G) * t, B: a.B + (b2.B - a.B) * t);
                bool sky = (a.B + b2.B) / 2 - (a.R + b2.R) / 2 > 12;
                float dm = Dist(k, q, model);
                bool cloudy = false;
                if (smooth)
                {
                    var (_, sat, val) = Hsv((k.R[q], k.G[q], k.B[q]));
                    // Серые облака и пересвеченное у солнца небо (белёсое, без сетки).
                    // Тёплый светлый камень (R заметно больше B) небом не считается.
                    bool neutral = k.B[q] >= k.R[q] - 12;
                    cloudy = neutral && (sat < 0.16 && dm <= 70 || sat < 0.2 && val > 0.72);
                }
                if (!cloudy && (sky ? dm > 30 && !IsSkyLike(k, q, model) : dm > 45 && !IsShadow(k, q, model))) continue;
                background[q] = true;
                origin[q] = origin[i];
                queue.Enqueue(q);
            }
        }
        for (int i = 0; i < background.Length; i++) if (k.Veg[i]) background[i] = true;
        // Вторая заливка — тени на земле: от фона в тёмные пиксели той же цветности, что модель строки (только строки земли).
        foreach (int i0 in Enumerable.Range(0, w * h).Where(i => background[i])) queue.Enqueue(i0);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w, y = i / w;
            for (int dir = 0; dir < 4; dir++)
            {
                int nx = x + (dir == 0 ? 1 : dir == 1 ? -1 : 0), ny = y + (dir == 2 ? 1 : dir == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int q = ny * w + nx;
                if (background[q]) continue;
                var a = bgL[ny];
                var b2 = bgR[ny];
                if ((a.B + b2.B) / 2 - (a.R + b2.R) / 2 > 12) continue;
                float t = (float)nx / Math.Max(1, w - 1);
                var model = (R: a.R + (b2.R - a.R) * t, G: a.G + (b2.G - a.G) * t, B: a.B + (b2.B - a.B) * t);
                if (!IsShadow(k, q, model)) continue;
                background[q] = true;
                queue.Enqueue(q);
            }
        }        var fg = new bool[w * h];
        for (int i = 0; i < fg.Length; i++) fg[i] = !background[i];
        // Открытие радиусом 2: тонкие линии (горизонт, провода, мачты) отрываются от силуэта.
        fg = Close(Dilate(Dilate(Erode(Erode(fg, w, h), w, h), w, h), w, h), w, h);
        k.Mask = CentralComponents(fg, w, h);
        k.Mask = Erode(Erode(Dilate(Dilate(k.Mask, w, h), w, h), w, h), w, h);
        FillHoles(k.Mask, w, h);
    }

    private static (float R, float G, float B) Median(Work k, int y, int x0, int x1)
    {
        var r = new List<float>(); var g = new List<float>(); var b = new List<float>();
        for (int x = x0; x < x1; x++)
        {
            int i = y * k.W + x;
            r.Add(k.R[i]); g.Add(k.G[i]); b.Add(k.B[i]);
        }
        r.Sort(); g.Sort(); b.Sort();
        return (r[r.Count / 2], g[g.Count / 2], b[b.Count / 2]);
    }

    private static float Dist(Work k, int i, (float R, float G, float B) c)
    {
        float dr = k.R[i] - c.R, dg = k.G[i] - c.G, db = k.B[i] - c.B;
        return MathF.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static float[] Blur(float[] v, int w, int h)
    {
        var r = new float[v.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        s += v[yy * w + xx]; n++;
                    }
                r[y * w + x] = s / n;
            }
        return r;
    }

    private static bool IsSkyLike(Work k, int i, (float R, float G, float B) bg)
    {
        float s1 = k.R[i] + k.G[i] + k.B[i] + 1e-3f, s0 = bg.R + bg.G + bg.B + 1e-3f;
        float ratio = s1 / s0;
        if (ratio is < 0.78f or > 1.3f) return false;
        float dr = k.R[i] / s1 - bg.R / s0, dg = k.G[i] / s1 - bg.G / s0;
        // Облака светлее и белее неба: допускается сдвиг к нейтральному при большей яркости.
        float tol = ratio > 1.02f ? 0.05f : 0.025f;
        return MathF.Sqrt(dr * dr + dg * dg) < tol;
    }

    /// <summary>Тень на земле: та же цветность, что у фона строки, и яркость 25…92 % фона.</summary>
    private static bool IsShadow(Work k, int i, (float R, float G, float B) bg)
    {
        float s1 = k.R[i] + k.G[i] + k.B[i] + 1e-3f, s0 = bg.R + bg.G + bg.B + 1e-3f;
        float ratio = s1 / s0;
        if (ratio is < 0.25f or > 0.92f) return false;
        float dr = k.R[i] / s1 - bg.R / s0, dg = k.G[i] / s1 - bg.G / s0;
        return MathF.Sqrt(dr * dr + dg * dg) < 0.022f;
    }

    private static bool[] Open(bool[] m, int w, int h) => Dilate(Erode(m, w, h), w, h);
    private static bool[] Close(bool[] m, int w, int h) => Erode(Dilate(m, w, h), w, h);

    private static bool[] Erode(bool[] m, int w, int h)
    {
        var r = new bool[m.Length];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                r[i] = m[i] && m[i - 1] && m[i + 1] && m[i - w] && m[i + w];
            }
        return r;
    }

    private static bool[] Dilate(bool[] m, int w, int h)
    {
        var r = new bool[m.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                r[i] = m[i] || x > 0 && m[i - 1] || x < w - 1 && m[i + 1] || y > 0 && m[i - w] || y < h - 1 && m[i + w];
            }
        return r;
    }

    /// <summary>
    /// Главная связная область у центра кадра и все крупные (≥ 0,4 % кадра) области, чей габарит пересекается с её габаритом,
    /// расширенным на 6 % ширины: стилобат, отделённый от башни светлой кровлей, крылья за портиком.
    /// </summary>
    private static bool[] CentralComponents(bool[] m, int w, int h)
    {
        var main = LargestCentralComponent(m, w, h);
        int x0 = w, x1 = -1, y0 = h, y1 = -1;
        for (int i = 0; i < main.Length; i++)
            if (main[i]) { int x = i % w, y = i / w; x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        if (x1 < 0) return main;
        int pad = (int)(w * 0.06);
        var label = new bool[m.Length];
        var stack = new Stack<int>();
        var members = new List<int>();
        for (int s = 0; s < m.Length; s++)
        {
            if (!m[s] || main[s] || label[s]) continue;
            members.Clear();
            label[s] = true;
            stack.Push(s);
            int ax = w, bx = -1, ay = h, by = -1;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                members.Add(i);
                int x = i % w, y = i / w;
                ax = Math.Min(ax, x); bx = Math.Max(bx, x); ay = Math.Min(ay, y); by = Math.Max(by, y);
                foreach (int q in new[] { x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1 })
                    if (q >= 0 && m[q] && !label[q] && !main[q]) { label[q] = true; stack.Push(q); }
            }
            bool near = ax <= x1 + pad && bx >= x0 - pad && ay <= y1 + pad && by >= y0 - pad;
            if (near && members.Count >= w * h * 0.004)
                foreach (int i in members) main[i] = true;
        }
        return main;
    }

    private static bool[] LargestCentralComponent(bool[] m, int w, int h)
    {
        var label = new int[m.Length];
        int best = -1;
        double bestScore = 0;
        int next = 1;
        var stack = new Stack<int>();
        var members = new List<int>();
        var keep = new bool[m.Length];
        List<int>? bestMembers = null;
        for (int s = 0; s < m.Length; s++)
        {
            if (!m[s] || label[s] != 0) continue;
            members.Clear();
            label[s] = next;
            stack.Push(s);
            long sx = 0, sy = 0;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                members.Add(i);
                int x = i % w, y = i / w;
                sx += x; sy += y;
                if (x > 0 && m[i - 1] && label[i - 1] == 0) { label[i - 1] = next; stack.Push(i - 1); }
                if (x < w - 1 && m[i + 1] && label[i + 1] == 0) { label[i + 1] = next; stack.Push(i + 1); }
                if (y > 0 && m[i - w] && label[i - w] == 0) { label[i - w] = next; stack.Push(i - w); }
                if (y < h - 1 && m[i + w] && label[i + w] == 0) { label[i + w] = next; stack.Push(i + w); }
            }
            double cx = (double)sx / members.Count / w - 0.5, cy = (double)sy / members.Count / h - 0.5;
            double score = members.Count * (1.2 - Math.Min(1, Math.Sqrt(cx * cx + cy * cy)));
            if (score > bestScore)
            {
                bestScore = score;
                best = next;
                bestMembers = [.. members];
            }
            next++;
        }
        if (best < 0 || bestMembers is null) return keep;
        foreach (int i in bestMembers) keep[i] = true;
        return keep;
    }

    /// <summary>Заполнение дыр: всё, что не достижимо от кромок кадра по фону, — здание.</summary>
    private static void FillHoles(bool[] m, int w, int h)
    {
        var outside = new bool[m.Length];
        var stack = new Stack<int>();
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            int x = i % w, y = i / w;
            if (x > 0) Seed(i - 1);
            if (x < w - 1) Seed(i + 1);
            if (y > 0) Seed(i - w);
            if (y < h - 1) Seed(i + w);
        }
        for (int i = 0; i < m.Length; i++) if (!outside[i]) m[i] = true;

        void Seed(int i)
        {
            if (m[i] || outside[i]) return;
            outside[i] = true;
            stack.Push(i);
        }
    }

    // ───────────────────────────── Профили силуэта ─────────────────────────────

    private static bool Profiles(Work k)
    {
        k.Top = new int[k.W];
        k.Bottom = new int[k.W];
        int count = 0;
        k.X0 = k.W; k.X1 = -1; k.YTop = k.H; k.YBase = -1;
        for (int x = 0; x < k.W; x++)
        {
            k.Top[x] = -1; k.Bottom[x] = -1;
            for (int y = 0; y < k.H; y++)
                if (k.In(x, y)) { if (k.Top[x] < 0) k.Top[x] = y; k.Bottom[x] = y; count++; }
            if (k.Top[x] < 0) continue;
            k.X0 = Math.Min(k.X0, x); k.X1 = Math.Max(k.X1, x);
            k.YTop = Math.Min(k.YTop, k.Top[x]); k.YBase = Math.Max(k.YBase, k.Bottom[x]);
        }
        return count > k.W * k.H * 0.02 && k.X1 > k.X0 + 8 && k.YBase > k.YTop + 8;
    }

    /// <summary>Высота верха силуэта над основанием по столбцам (пиксели), медианный фильтр 5.</summary>
    private static double[] SmoothedHeights(Work k)
    {
        var raw = new double[k.Wpx];
        for (int x = k.X0; x <= k.X1; x++) raw[x - k.X0] = k.Top[x] < 0 ? 0 : k.YBase - k.Top[x];
        var s = new double[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            var win = new List<double>();
            for (int j = Math.Max(0, i - 2); j <= Math.Min(raw.Length - 1, i + 2); j++) win.Add(raw[j]);
            win.Sort();
            s[i] = win[win.Count / 2];
        }
        return s;
    }

    private sealed record Dome(int From, int To, double BaseLevel, double Height, double WidthRatio, double CircleError, double Roundness);

    private sealed record ArchEnd(int Side, double Width, double Rise, double SpringLevel, double RidgeShare);

    private sealed record Bend(bool Curved, double Ratio, double Sagitta);

    /// <summary>Вершина силуэта: середина участка в пределах 1 % высоты от максимума.</summary>
    private static int PeakIndex(Work k, double[] hTop)
    {
        double max = hTop.Max();
        int peak = Array.IndexOf(hTop, max);
        int pa = peak, pb = peak;
        while (pa > 0 && hTop[pa - 1] >= max - 0.01 * k.Hpx) pa--;
        while (pb < hTop.Length - 1 && hTop[pb + 1] >= max - 0.01 * k.Hpx) pb++;
        return (pa + pb) / 2;
    }

    /// <summary>
    /// Купол: скруглённый горб вокруг вершины между «плечами» (участками, где профиль выполаживается), спад ≥ 12 % высоты с обеих
    /// сторон, ширина 12…65 % силуэта, уклон у вершины много меньше уклона у основания (не конёк), окружность по МНК (Каса) — СКО &lt; 8 %.
    /// </summary>
    private static Dome? DetectDome(Work k, double[] hTop, int peak)
    {
        int n = hTop.Length;
        double H = k.Hpx, top = hTop[peak];
        int win = Math.Max(3, n / 25);
        int Walk(int dir)
        {
            int i = peak;
            while (i + dir >= 0 && i + dir < n)
            {
                if (hTop[i + dir] > hTop[i] + 1) break;
                int j = i + dir * win;
                if (j >= 0 && j < n && Math.Abs(hTop[i] - hTop[j]) < 0.012 * H && top - hTop[i] > 0.08 * H) break;
                i += dir;
            }
            return i;
        }
        int from = Walk(-1), to = Walk(1);
        double dropL = top - hTop[from], dropR = top - hTop[to];
        int width = to - from;
        if (dropL < 0.12 * H || dropR < 0.12 * H || width < 0.12 * n || width > 0.65 * n) return null;
        double a = width / 2.0;
        double Slope(int side, double frac)
        {
            int i0 = Math.Clamp(peak + side * (int)Math.Round(a * frac), 0, n - 1);
            int i1 = Math.Clamp(peak + side * (int)Math.Round(a * (frac + 0.12)), 0, n - 1);
            return Math.Abs(hTop[i1] - hTop[i0]) / Math.Max(1, Math.Abs(i1 - i0));
        }
        double nearApex = (Slope(-1, 0.05) + Slope(1, 0.05)) / 2, nearBase = (Slope(-1, 0.7) + Slope(1, 0.7)) / 2;
        double roundness = nearApex / Math.Max(1e-6, nearBase);
        if (roundness > 0.45) return null;
        // Окружность (Каса) по точкам горба.
        var pts = Enumerable.Range(from, width + 1).Select(i => ((double)i, hTop[i])).ToList();
        var (cx, cy, r) = FitCircle(pts);
        if (r <= 0) return null;
        double rms = Math.Sqrt(pts.Average(p => Math.Pow(Math.Sqrt((p.Item1 - cx) * (p.Item1 - cx) + (p.Item2 - cy) * (p.Item2 - cy)) - r, 2)));
        double err = rms / Math.Max(1, a);
        if (err > 0.08) return null;
        double baseLevel = Math.Max(hTop[from], hTop[to]);
        return new Dome(from, to, baseLevel, top - baseLevel, (double)width / n, err, roundness);
    }

    /// <summary>Медианный цвет пикселей здания над уровнем base (купол) в столбцах from…to.</summary>
    private static string? RegionColor(Work k, int from, int to, double baseLevel)
    {
        var r = new List<float>(); var g = new List<float>(); var b = new List<float>();
        for (int i = from; i <= to; i++)
        {
            int x = k.X0 + i;
            if (x < 0 || x >= k.W || k.Top[x] < 0) continue;
            int yLow = (int)(k.YBase - baseLevel);
            for (int y = k.Top[x] + 1; y < yLow; y++)
            {
                if (!k.In(x, y)) continue;
                int p = y * k.W + x;
                r.Add(k.R[p]); g.Add(k.G[p]); b.Add(k.B[p]);
            }
        }
        if (r.Count < 20) return null;
        r.Sort(); g.Sort(); b.Sort();
        return Models.CurvedDesign.Hex((byte)r[r.Count / 2], (byte)g[g.Count / 2], (byte)b[b.Count / 2]);
    }

    private static (double Cx, double Cy, double R) FitCircle(List<(double X, double Y)> pts)
    {
        // Алгебраическая окружность: x² + y² + Dx + Ey + F = 0 (МНК, нормальные уравнения 3 × 3).
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
        int n = pts.Count;
        foreach (var (x, y) in pts)
        {
            double z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y; sxz += x * z; syz += y * z; sz += z;
        }
        double[,] m = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        double[] rhs = [-sxz, -syz, -sz];
        double det = Det3(m);
        if (Math.Abs(det) < 1e-12) return (0, 0, 0);
        double[] c = new double[3];
        for (int col = 0; col < 3; col++)
        {
            var mc = (double[,])m.Clone();
            for (int row = 0; row < 3; row++) mc[row, col] = rhs[row];
            c[col] = Det3(mc) / det;
        }
        double cx = -c[0] / 2, cy = -c[1] / 2;
        double r2 = cx * cx + cy * cy - c[2];
        return r2 > 0 ? (cx, cy, Math.Sqrt(r2)) : (0, 0, 0);
    }

    /// <summary>
    /// Арочный торец свода у края силуэта: от края внутрь профиль поднимается ≥ 15 % высоты на ≥ 8 % ширины с плавным уменьшением
    /// уклона (не скачок: подъём 10 → 90 % занимает ≥ 5 % ширины), дальше — пологий конёк (|уклон| &lt; 0,3) на ≥ 30 % ширины.
    /// </summary>
    private static ArchEnd? DetectArchEnd(Work k, double[] hTop)
    {
        int n = hTop.Length;
        double H = k.Hpx;
        foreach (int side in new[] { -1, 1 })
        {
            int start = side < 0 ? 0 : n - 1, dir = -side;
            double spring = hTop[start];
            int crown = start, limit = (int)(n * 0.45);
            int step = Math.Max(2, n / 80);
            for (int s = step; s < limit; s += 1)
            {
                int i = start + dir * s;
                if (i < step || i >= n - step) break;
                double slope = (hTop[i] - hTop[i - dir * step]) / step;
                if (slope < 0.3 && hTop[i] - spring > 0.1 * H) { crown = i; break; }
            }
            if (crown == start) continue;
            double rise = hTop[crown] - spring;
            int width = Math.Abs(crown - start);
            if (rise < 0.15 * H || width < 0.08 * n) continue;
            int i10 = start, i90 = start;
            for (int s = 0; s <= width; s++)
            {
                int i = start + dir * s;
                if (hTop[i] - spring < 0.1 * rise) i10 = i;
                if (hTop[i] - spring < 0.9 * rise) i90 = i;
            }
            if (Math.Abs(i90 - i10) < 0.05 * n) continue;
            // Волна 4.1: арка выполаживается к вершине — уклон у пяты заметно круче уклона у замка; прямая наклонная кромка
            // (боковая грань фасада в перспективе снизу вверх) имеет постоянный уклон и аркой не считается.
            int i30 = start, i70 = start;
            for (int s = 0; s <= width; s++)
            {
                int i = start + dir * s;
                if (hTop[i] - spring < 0.3 * rise) i30 = i;
                if (hTop[i] - spring < 0.7 * rise) i70 = i;
            }
            double early = 0.2 * rise / Math.Max(1, Math.Abs(i30 - i10)), late = 0.2 * rise / Math.Max(1, Math.Abs(i90 - i70));
            if (early < 1.6 * late) continue;
            // Конёк: от вершины арки внутрь — пологая линия.
            int ridge = 0;
            for (int s = 1; crown + dir * s >= 0 && crown + dir * s < n; s++)
            {
                int i = crown + dir * s;
                if (Math.Abs(hTop[i] - hTop[crown]) > 0.3 * s + 0.04 * H) break;
                ridge = s;
            }
            if (ridge < 0.3 * n) continue;
            return new ArchEnd(side, width, rise, spring, (double)ridge / n);
        }
        return null;
    }

    /// <summary>
    /// Ярусы каскада: профиль верха делится вертикальными скачками (≥ 5 % высоты на ≤ 4 пикселях); участки шириной ≥ 6 % с линейной
    /// аппроксимацией |уклон| ≤ 0,15 и СКО ≤ 1,5 % высоты — горизонтальные площадки. Каскад — ≥ 3 различных уровня (шаг ≥ 4 %), монотонно
    /// или «пирамидой».
    /// </summary>
    private static List<PhotoTier> DetectSteppedTiers(Work k, double[] hTop)
    {
        int n = hTop.Length;
        double H = k.Hpx;
        var cuts = new List<int> { 0 };
        for (int i = 2; i < n - 2; i++)
            if (Math.Abs(hTop[i + 2] - hTop[i - 2]) >= 0.05 * H && i - cuts[^1] > 3) cuts.Add(i);
        cuts.Add(n - 1);
        var plateaus = new List<(int A, int B, double Level)>();
        for (int c = 0; c + 1 < cuts.Count; c++)
        {
            int a = cuts[c] + 3, b = cuts[c + 1] - 3;
            if (b - a < 0.06 * n) continue;
            var pts = Enumerable.Range(a, b - a + 1).Select(i => ((double)i, hTop[i])).ToList();
            var (slope, icept) = Line(pts);
            double rms = Math.Sqrt(pts.Average(p => Math.Pow(p.Item2 - (slope * p.Item1 + icept), 2)));
            // Волна 4.1: на угловом виде кромки уступов наклонены перспективой — допускается уклон до 0,4 (прямая без излома).
            if (Math.Abs(slope) > 0.4 || rms > 0.022 * H) continue;
            plateaus.Add((a, b, pts.Average(p => p.Item2)));
        }
        var levels = plateaus.Select(p => p.Level).OrderBy(v => v).Aggregate(new List<double>(), (acc, v) => { if (acc.Count == 0 || v - acc[^1] >= 0.04 * H) acc.Add(v); return acc; });
        if (levels.Count < 3) return [];
        // «Пирамида» с одним допустимым нарушением (реальные каскады с консольными сдвигами не строго монотонны).
        int peakIdx = plateaus.IndexOf(plateaus.MaxBy(p => p.Level));
        int violations = 0;
        for (int i = 1; i <= peakIdx; i++) if (plateaus[i].Level < plateaus[i - 1].Level - 0.02 * H) violations++;
        for (int i = peakIdx + 1; i < plateaus.Count; i++) if (plateaus[i].Level > plateaus[i - 1].Level + 0.02 * H) violations++;
        if (violations > (plateaus.Count >= 5 ? 1 : 0)) return [];
        return plateaus.Select(p => new PhotoTier((double)(p.A + k.X0) / k.W, (double)(p.B + 1 + k.X0) / k.W, p.Level / H, 0)).ToList();
    }

    /// <summary>Вид на угол: самая низкая точка цоколя (ближайший угол) — у вершины силуэта, перепад цоколя ≥ 2,5 % высоты.</summary>
    private static bool CornerView(Work k, double[] hTop, int peak)
    {
        int n = hTop.Length;
        double max = hTop.Max();
        int lowest = -1, highest = int.MaxValue, at = -1;
        for (int i = 0; i < n; i++)
        {
            if (hTop[i] < 0.55 * max) continue;
            int b = k.Bottom[k.X0 + i];
            if (b > lowest) { lowest = b; at = i; }
            highest = Math.Min(highest, b);
        }
        return at >= 0 && Math.Abs(at - peak) < 0.08 * n && lowest - highest > 0.025 * k.Hpx;
    }

    /// <summary>Конёк: уклон у вершины такой же, как на скатах (две прямые), спад 10…50 % высоты с обеих сторон.</summary>
    private static bool IsPointed(Work k, double[] hTop, int peak)
    {
        int n = hTop.Length;
        if (peak < n * 0.2 || peak > n * 0.8) return false;
        double dropL = hTop[peak] - hTop[0], dropR = hTop[peak] - hTop[^1];
        if (dropL < 0.1 * k.Hpx || dropR < 0.1 * k.Hpx || dropL > 0.5 * k.Hpx || dropR > 0.5 * k.Hpx) return false;
        double near = (Math.Abs(hTop[peak] - hTop[Math.Max(0, peak - n / 12)]) + Math.Abs(hTop[peak] - hTop[Math.Min(n - 1, peak + n / 12)])) / 2;
        double far = (Math.Abs(hTop[Math.Max(0, peak - n / 3)] - hTop[Math.Max(0, peak - n / 3 - n / 12)]) + Math.Abs(hTop[Math.Min(n - 1, peak + n / 3)] - hTop[Math.Min(n - 1, peak + n / 3 + n / 12)])) / 2;
        return far > 0 && near / far > 0.6;
    }

    /// <summary>
    /// Изгиб фасада по межэтажным линиям: перспектива сохраняет прямые, поэтому у плоского фасада линии этажей прямые, у дугового —
    /// кривые. На широкой стороне от вершины в трёх полосах (35 / 50 / 65 % высоты) прослеживается сильнейший горизонтальный перепад
    /// яркости; остаток параболы против прямой &lt; 0,55 и стрела ≥ 1,2 % ширины хотя бы в двух полосах — фасад изогнут.
    /// </summary>
    private static Bend FloorLineCurvature(Work k, double[] hTop, int peak)
    {
        int n = hTop.Length;
        double max = hTop.Max();
        int b0 = 0, b1 = n - 1;
        while (b0 < n - 1 && hTop[b0] < 0.55 * max) b0++;
        while (b1 > b0 && hTop[b1] < 0.55 * max) b1--;
        // Широкая сторона от вершины; без выраженной вершины — всё тело.
        int a = b0, b = b1;
        if (peak - b0 > b1 - peak && peak - b0 > 0.3 * (b1 - b0)) b = peak - (b1 - b0) / 30;
        else if (b1 - peak > 0.3 * (b1 - b0)) a = peak + (b1 - b0) / 30;
        a += (b - a) / 12; b -= (b - a) / 12;
        if (b - a < 24) return new Bend(false, 1, 0);
        double period = Math.Max(4, k.Hpx / 30.0);
        var ratios = new List<double>();
        var sags = new List<double>();
        foreach (double frac in new[] { 0.3, 0.4, 0.5, 0.6, 0.7 })
        {
            // Слежение за линией этажа: старт — сильнейший горизонтальный перепад у середины участка, далее влево и вправо
            // по столбцам с поиском в ±2 px от предыдущей точки.
            int xm = k.X0 + (a + b) / 2;
            int yc = (int)(k.YBase - max * frac);
            double Grad(int x, int y) => y >= 1 && y < k.H - 1 && k.In(x, y) && k.In(x, y + 1) ? Math.Abs(k.Lum(x, y + 1) - k.Lum(x, y)) : 0;
            int y0 = -1;
            double g0 = 0;
            for (int y = yc - (int)period; y <= yc + (int)period; y++)
                if (Grad(xm, y) > g0) { g0 = Grad(xm, y); y0 = y; }
            if (y0 < 0 || g0 < 8) continue;
            var pts = new List<(double X, double Y)> { (xm - k.X0, y0) };
            foreach (int dir in new[] { -1, 1 })
            {
                int y = y0, miss = 0;
                for (int x = xm + dir; x >= k.X0 + a && x <= k.X0 + b; x += dir)
                {
                    int by = y;
                    double best = 0;
                    for (int dy = -2; dy <= 2; dy++)
                        if (Grad(x, y + dy) > best) { best = Grad(x, y + dy); by = y + dy; }
                    if (best < 6) { if (++miss > 6) break; continue; }
                    miss = 0;
                    y = by;
                    pts.Add((x - k.X0, y));
                }
            }
            if (pts.Count < (b - a) * 0.7) continue;
            pts.Sort((p, q) => p.X.CompareTo(q.X));
            var (la, lb) = Line(pts);
            double lineErr = Math.Sqrt(pts.Average(p => Math.Pow(p.Y - (la * p.X + lb), 2)));
            var arr = pts.Select(p => p.Y).ToArray();
            double quadErr = FitQuadratic(arr, out double curvature);
            double span = pts[^1].X - pts[0].X;
            // Стрела параболы на участке в долях его ширины: |c|·(n/2)² с c на единицу индекса.
            double sagitta = Math.Abs(curvature) * arr.Length * arr.Length / 4 / Math.Max(1, span);
            if (lineErr < 0.35) { ratios.Add(1); sags.Add(0); continue; }
            ratios.Add(quadErr / lineErr);
            sags.Add(sagitta);
        }        if (ratios.Count == 0) return new Bend(false, 1, 0);
        int curvedBands = ratios.Zip(sags).Count(p => p.First < 0.55 && p.Second >= 0.012);
        double ratio = ratios.OrderBy(r => r).ElementAt(ratios.Count / 2), sag = sags.OrderBy(s => s).ElementAt(sags.Count / 2);
        return new Bend(curvedBands >= Math.Min(2, ratios.Count), ratio, sag);
    }

    private static void AddCurvature(Work k, PhotoArchitectureDescriptor d, int peak)
    {
        for (int i = 1; i <= 5; i++)
        {
            int x = k.X0 + k.Wpx * i / 6;
            if (k.Top[x] < 0) continue;
            double nx = (double)x / k.W, ny = (double)k.Top[x] / k.H;
            d.CurvatureVectors.Add(((nx, ny), (nx + (x - (k.X0 + peak)) * 0.1 / k.W, ny - 0.05)));
        }
    }
    private sealed record Corner(double Angle, bool Smooth, double KinkShare, double LuminanceStep, double Bulge, double Rounding);

    /// <summary>
    /// Ближний угол: точки схода фасадов слева и справа от высшей точки (пересечение прямых карниза и цоколя), угол между
    /// направлениями на них (фокус по горизонтальному углу обзора 60°). Гладкость: доля второй разности верха в окне 10 % у
    /// максимума, сравнение квадратичной и двухотрезочной аппроксимаций, перепад яркости по середине высоты фасада.
    /// </summary>
    private static Corner CornerAnalysis(Work k, double[] hTop, PhotoArchitectureDescriptor d)
    {
        int n = hTop.Length;
        // Тело здания — столбцы выше 55 % максимума (без стилобата).
        double max = hTop.Max();
        int b0 = 0, b1 = n - 1;
        while (b0 < n - 1 && hTop[b0] < 0.55 * max) b0++;
        while (b1 > b0 && hTop[b1] < 0.55 * max) b1--;
        int span = b1 - b0 + 1;
        if (span < 20) return new Corner(0, false, 1, 1, 0, 0);
        int xc = b0;
        for (int i = b0; i <= b1; i++) if (hTop[i] > hTop[xc]) xc = i;
        // Плоский максимум — середина плато.
        int xa = xc, xb = xc;
        while (xa > b0 && hTop[xa - 1] >= hTop[xc] - 0.5) xa--;
        while (xb < b1 && hTop[xb + 1] >= hTop[xc] - 0.5) xb++;
        xc = (xa + xb) / 2;

        // Вторая разность верха (окно 7) и её концентрация у максимума.
        var s = new double[span];
        for (int i = 0; i < span; i++)
        {
            double acc = 0; int c = 0;
            for (int j = Math.Max(0, i - 3); j <= Math.Min(span - 1, i + 3); j++) { acc += hTop[b0 + j]; c++; }
            s[i] = acc / c;
        }
        double total = 0, local = 0;
        int window = Math.Max(3, span / 20);
        for (int i = 1; i < span - 1; i++)
        {
            double dd = Math.Abs(s[i + 1] - 2 * s[i] + s[i - 1]);
            total += dd;
            if (Math.Abs(b0 + i - xc) <= window) local += dd;
        }
        double kink = total > 1e-6 ? local / total : 0;

        // Квадратичная и двухотрезочная аппроксимации верха.
        double quadErr = FitQuadratic(s, out double curvature);
        double twoErr = FitTwoLines(s, xc - b0);

        // Перепад яркости по середине высоты тела.
        var lum = new double[span];
        int yMid0 = k.YBase - (int)(max * 0.6), yMid1 = k.YBase - (int)(max * 0.4);
        for (int i = 0; i < span; i++)
        {
            int x = k.X0 + b0 + i;
            var vals = new List<float>();
            for (int y = Math.Max(0, yMid0); y <= Math.Min(k.H - 1, yMid1); y++) if (k.In(x, y)) vals.Add(k.Lum(x, y));
            vals.Sort();
            lum[i] = vals.Count > 0 ? vals[vals.Count / 2] : 0;
        }
        var lumS = Smooth(lum, Math.Max(2, span / 40));
        double range = Math.Max(1, lumS.Max() - lumS.Min());
        double step = 0;
        int dx = Math.Max(2, span / 60);
        for (int i = dx; i < span - dx; i++) step = Math.Max(step, Math.Abs(lumS[i + dx] - lumS[i - dx]) / range);
        // Ширина перехода яркости у угла (скругление): участок у xc, где яркость между уровнями граней.
        double rounding = 0;
        {
            int ci = Math.Clamp(xc - b0, 1, span - 2);
            double left = lumS.Take(Math.Max(1, ci - span / 10)).DefaultIfEmpty(lumS[0]).Average();
            double right = lumS.Skip(Math.Min(span - 1, ci + span / 10)).DefaultIfEmpty(lumS[^1]).Average();
            if (Math.Abs(left - right) > 0.2 * range)
            {
                double lo = Math.Min(left, right) + 0.15 * Math.Abs(left - right), hi = Math.Max(left, right) - 0.15 * Math.Abs(left - right);
                int count = 0;
                for (int i = Math.Max(0, ci - span / 8); i <= Math.Min(span - 1, ci + span / 8); i++) if (lumS[i] > lo && lumS[i] < hi) count++;
                rounding = (double)count / span;
            }
        }

        // Точки схода слева и справа.
        double angle = 0;
        int leftA = b0 + span / 50, leftB = xc - span / 25, rightA = xc + span / 25, rightB = b1 - span / 50;
        if (leftB - leftA > span / 8 && rightB - rightA > span / 8)
        {
            var vl = Vanishing(k, leftA, leftB);
            var vr = Vanishing(k, rightA, rightB);
            double cxImg = k.W / 2.0, cyImg = k.H / 2.0, f = 0.5 * k.W / Math.Tan(30 * Math.PI / 180);
            if (vl is { } l && vr is { } r && l.X < k.X0 + xc && r.X > k.X0 + xc)
            {
                double ax = l.X - cxImg, ay = l.Y - cyImg, bx = r.X - cxImg, by = r.Y - cyImg;
                double dot = ax * bx + ay * by + f * f;
                double na = Math.Sqrt(ax * ax + ay * ay + f * f), nb = Math.Sqrt(bx * bx + by * by + f * f);
                angle = Math.Acos(Math.Clamp(dot / (na * nb), -1, 1)) * 180 / Math.PI;
                d.VanishingLines.Add((((double)(k.X0 + xc) / k.W, (double)k.Top[k.X0 + xc] / k.H), (l.X / k.W, l.Y / k.H)));
                d.VanishingLines.Add((((double)(k.X0 + xc) / k.W, (double)k.Top[k.X0 + xc] / k.H), (r.X / k.W, r.Y / k.H)));
            }
        }

        bool smooth = kink < 0.42 && quadErr <= twoErr * 1.2 && step < 0.42 && Math.Abs(curvature) > 1e-5;
        // Стрела дуги: подъём квадратичной вершины над концами, перспектива сжимает план — коэффициент 2,4.
        double rise = Math.Abs(curvature) * span * span / 4;
        double bulge = Math.Clamp(2.4 * rise / span, 0.08, 0.3);
        if (smooth)
            for (int i = 1; i <= 5; i++)
            {
                int x = k.X0 + b0 + span * i / 6;
                double nx = (double)x / k.W, ny = (double)k.Top[x] / k.H;
                d.CurvatureVectors.Add(((nx, ny), (nx + (x - (k.X0 + xc)) * 0.12 / k.W, ny - 0.05)));
            }
        return new Corner(angle, smooth, kink, step, bulge, rounding);
    }

    /// <summary>Точка схода фасада на участке столбцов: пересечение прямых верха и низа силуэта (МНК).</summary>
    private static (double X, double Y)? Vanishing(Work k, int a, int b)
    {
        var top = new List<(double X, double Y)>();
        var bottom = new List<(double X, double Y)>();
        for (int i = a; i <= b; i++)
        {
            int x = k.X0 + i;
            if (x < 0 || x >= k.W || k.Top[x] < 0) continue;
            top.Add((x, k.Top[x]));
            bottom.Add((x, k.Bottom[x]));
        }
        if (top.Count < 8) return null;
        var (ta, tb) = Line(top);
        var (ba, bb) = Line(bottom);
        if (Math.Abs(ta - ba) < 0.01) return null;
        double xv = (bb - tb) / (ta - ba);
        return (xv, ta * xv + tb);
    }

    private static (double A, double B) Line(List<(double X, double Y)> pts)
    {
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double sxx = pts.Sum(p => (p.X - mx) * (p.X - mx)), sxy = pts.Sum(p => (p.X - mx) * (p.Y - my));
        double a = sxx > 1e-9 ? sxy / sxx : 0;
        return (a, my - a * mx);
    }

    private static double FitQuadratic(double[] s, out double curvature)
    {
        int n = s.Length;
        double sx = 0, sx2 = 0, sx3 = 0, sx4 = 0, sy = 0, sxy = 0, sx2y = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n - 0.5;
            sx += x; sx2 += x * x; sx3 += x * x * x; sx4 += x * x * x * x;
            sy += s[i]; sxy += x * s[i]; sx2y += x * x * s[i];
        }
        // Нормальные уравнения 3 × 3 (Крамер).
        double[,] m = { { n, sx, sx2 }, { sx, sx2, sx3 }, { sx2, sx3, sx4 } };
        double[] r = [sy, sxy, sx2y];
        double det = Det3(m);
        if (Math.Abs(det) < 1e-12) { curvature = 0; return double.MaxValue; }
        double[] coef = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var mc = (double[,])m.Clone();
            for (int row = 0; row < 3; row++) mc[row, c] = r[row];
            coef[c] = Det3(mc) / det;
        }
        double err = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n - 0.5;
            double v = coef[0] + coef[1] * x + coef[2] * x * x;
            err += (s[i] - v) * (s[i] - v);
        }
        curvature = coef[2] / ((double)n * n);
        return Math.Sqrt(err / n);
    }

    private static double Det3(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    private static double FitTwoLines(double[] s, int split)
    {
        split = Math.Clamp(split, 2, s.Length - 3);
        double err = 0;
        foreach (var (a, b) in new[] { (0, split), (split, s.Length - 1) })
        {
            var pts = Enumerable.Range(a, b - a + 1).Select(i => ((double)i, s[i])).ToList();
            var (la, lb) = Line(pts);
            err += pts.Sum(p => (p.Item2 - (la * p.Item1 + lb)) * (p.Item2 - (la * p.Item1 + lb)));
        }
        return Math.Sqrt(err / s.Length);
    }

    private static double[] Smooth(double[] v, int r)
    {
        var s = new double[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            double acc = 0; int c = 0;
            for (int j = Math.Max(0, i - r); j <= Math.Min(v.Length - 1, i + r); j++) { acc += v[j]; c++; }
            s[i] = acc / c;
        }
        return s;
    }

    // ───────────────────────────── Периодичность фасада ─────────────────────────────

    /// <summary>Период по высоте (этаж): автокорреляция перепадов яркости по строкам центральной части тела здания.</summary>
    private static double RowPeriod(Work k, int bodyTop)
    {
        int xa = k.X0 + k.Wpx / 5, xb = k.X1 - k.Wpx / 5;
        int ya = Math.Max(k.YTop, bodyTop) + 2, yb = k.YBase - 2;
        int n = yb - ya;
        if (n < 16 || xb <= xa) return 0;
        var sig = new double[n];
        for (int y = ya; y < yb; y++)
        {
            double acc = 0; int c = 0;
            for (int x = xa; x <= xb; x++)
            {
                if (!k.In(x, y) || !k.In(x, y + 1)) continue;
                acc += Math.Abs(k.Lum(x, y + 1) - k.Lum(x, y));
                c++;
            }
            sig[y - ya] = c > 0 ? acc / c : 0;
        }
        return Period(sig, 3, n / 2);
    }

    /// <summary>Период по ширине (ритм окон) в полосе высотой в один этаж посередине тела.</summary>
    private static double ColumnPeriod(Work k, double floorPeriod)
    {
        int band = (int)Math.Max(4, floorPeriod > 0 ? floorPeriod : k.Hpx / 10.0);
        int yc = (k.YTop + k.YBase) / 2;
        int xa = k.X0 + 2, xb = k.X1 - 2;
        int n = xb - xa;
        if (n < 16) return 0;
        var sig = new double[n];
        for (int x = xa; x < xb; x++)
        {
            double acc = 0; int c = 0;
            for (int y = yc - band / 2; y <= yc + band / 2; y++)
            {
                if (y < 0 || y >= k.H || !k.In(x, y) || !k.In(x + 1, y)) continue;
                acc += Math.Abs(k.Lum(x + 1, y) - k.Lum(x, y));
                c++;
            }
            sig[x - xa] = c > 0 ? acc / c : 0;
        }
        return Period(sig, 3, n / 3);
    }

    /// <summary>Наименьший лаг с автокорреляцией ≥ 80 % максимальной (после вычитания скользящего среднего).</summary>
    private static double Period(double[] sig, int minLag, int maxLag)
    {
        int n = sig.Length;
        var trend = Smooth(sig, 8);
        var v = sig.Select((x, i) => x - trend[i]).ToArray();
        double var0 = v.Sum(x => x * x);
        if (var0 < 1e-9 || maxLag <= minLag) return 0;
        var r = new double[maxLag + 2];
        for (int lag = minLag; lag <= maxLag + 1 && lag < n; lag++)
        {
            double acc = 0;
            for (int i = 0; i + lag < n; i++) acc += v[i] * v[i + lag];
            r[lag] = acc / var0 * n / (n - lag);
        }
        double best = 0;
        for (int lag = minLag + 1; lag <= maxLag && lag + 1 < r.Length; lag++)
            if (r[lag] > r[lag - 1] && r[lag] >= r[lag + 1]) best = Math.Max(best, r[lag]);
        if (best < 0.12) return 0;
        for (int lag = minLag + 1; lag <= maxLag && lag + 1 < r.Length; lag++)
            if (r[lag] > r[lag - 1] && r[lag] >= r[lag + 1] && r[lag] >= 0.8 * best)
            {
                // Параболическое уточнение вершины.
                double a = r[lag - 1], b = r[lag], c = r[lag + 1];
                double den = a - 2 * b + c;
                return lag + (Math.Abs(den) > 1e-9 ? 0.5 * (a - c) / den : 0);
            }
        return 0;
    }

    // ───────────────────────────── Колоннада, стилобат ─────────────────────────────

    /// <summary>Число колонн портика: светлые вертикали в нижней зоне под куполом (пики яркости после вычитания тренда).</summary>
    private static int Colonnade(Work k, Dome dome)
    {
        int xa = k.X0 + dome.From, xb = k.X0 + dome.To;
        int ya = k.YBase - (int)(dome.BaseLevel * 0.55), yb = k.YBase - (int)(dome.BaseLevel * 0.15);
        int n = xb - xa;
        if (n < 12 || yb <= ya) return 0;
        var lum = new double[n];
        for (int i = 0; i < n; i++)
        {
            var vals = new List<float>();
            for (int y = ya; y <= yb; y++) if (k.In(xa + i, y)) vals.Add(k.Lum(xa + i, y));
            vals.Sort();
            lum[i] = vals.Count > 0 ? vals[vals.Count / 2] : 0;
        }
        var trend = Smooth(lum, Math.Max(4, n / 6));
        var v = Smooth(lum.Select((x, i) => x - trend[i]).ToArray(), 1);
        double range = Math.Max(1, v.Max() - v.Min());
        int peaks = 0, last = -100, minGap = Math.Max(3, n / 20);
        for (int i = 1; i < n - 1; i++)
            if (v[i] > v[i - 1] && v[i] >= v[i + 1] && v[i] > 0.18 * range && i - last >= minGap) { peaks++; last = i; }
        return peaks is >= 3 and <= 14 ? peaks : 0;
    }

    private static void Podium(Work k, PhotoArchitectureDescriptor d, double floorPeriod)
    {
        var width = new double[k.H];
        for (int y = k.YTop; y <= k.YBase; y++)
        {
            int a = -1, b = -1;
            for (int x = k.X0; x <= k.X1; x++) if (k.In(x, y)) { if (a < 0) a = x; b = x; }
            width[y] = a < 0 ? 0 : b - a + 1;
        }
        int lowRows = Math.Max(2, k.Hpx / 14);
        double baseW = Enumerable.Range(k.YBase - lowRows, lowRows).Where(y => y >= 0).Select(y => width[y]).DefaultIfEmpty(0).Average();
        if (baseW <= 0) return;
        for (int y = k.YBase - lowRows; y > k.YBase - k.Hpx * 0.4; y--)
        {
            if (width[y] >= 0.86 * baseW) continue;
            double above = Enumerable.Range(Math.Max(k.YTop, y - lowRows), lowRows).Select(t => width[t]).Average();
            if (above >= 0.86 * baseW) continue;
            double podiumPx = k.YBase - y;
            d.PodiumFloors = floorPeriod > 0 ? Math.Clamp((int)Math.Round(podiumPx / floorPeriod), 1, 6) : 2;
            d.PodiumWidthRatio = above / baseW;
            d.Evidence.Add($"стилобат: {d.PodiumFloors} эт., шире башни в {baseW / above:0.00} раза");
            return;
        }
    }

    // ───────────────────────────── Размеры и палитра ─────────────────────────────

    private static void Dimensions(PhotoArchitectureDescriptor d, Work k, int floors, double bodyTopPx)
    {
        d.FloorHeight = d.Morphology switch
        {
            PhotoMorphology.Rotunda => 5.0,
            PhotoMorphology.ArchSpan => 8.0,
            PhotoMorphology.Cascading => 3.1,
            PhotoMorphology.Triangular => 3.4,
            PhotoMorphology.Pitched => 3.0,
            _ => d.GlazingRatio > 0.45 ? 3.6 : 3.3
        };
        if (floors <= 0) floors = (int)Math.Round(Math.Clamp(bodyTopPx / Math.Max(1, k.Wpx) * 8, 1, 40));
        d.Floors = d.Morphology == PhotoMorphology.ArchSpan ? 1 : Math.Clamp(floors, 1, 60);
        double bodyH = d.Floors * d.FloorHeight;
        double mPerPx = bodyH / Math.Max(1, bodyTopPx);
        d.HeightM = k.Hpx * mPerPx;
        d.WidthM = Math.Clamp(k.Wpx * mPerPx * 0.85, 8, 260);
        d.DepthM = d.Morphology switch
        {
            PhotoMorphology.Rotunda => d.WidthM * 0.6,
            PhotoMorphology.ArchSpan => d.WidthM * 1.6,
            PhotoMorphology.Triangular => d.WidthM,
            PhotoMorphology.Cascading => Math.Clamp(d.WidthM * 0.28, 12, 22),
            _ => Math.Clamp(d.WidthM * 0.55, 10, 60)
        };
    }

    private static void Palette(Work k, PhotoArchitectureDescriptor d)
    {
        var px = new List<(float R, float G, float B, int X, int Y)>();
        int step = Math.Max(1, (int)Math.Sqrt(Math.Max(1, k.Mask.Count(m => m)) / 9000.0));
        // Волна 4.1: по маске, сжатой на 2 px, — смешанные пиксели кромки (кайма неба) не занимают кластер палитры.
        var inner = k.Mask.Length > 0 ? Erode(Erode(k.Mask, k.W, k.H), k.W, k.H) : k.Mask;
        if (inner.Count(m => m) < k.Mask.Count(m => m) / 3) inner = k.Mask;
        for (int y = 0; y < k.H; y += step)
            for (int x = 0; x < k.W; x += step)
                if (k.Mask.Length > 0 && inner[y * k.W + x] && !(k.Veg.Length > 0 && k.Veg[y * k.W + x]))
                {
                    // Кроны перед фасадом (достроенная маска) — не цвет здания.
                    int i = y * k.W + x;
                    px.Add((k.R[i], k.G[i], k.B[i], x, y));
                }
        if (px.Count < 12) return;
        const int K = 6;
        // Детерминированная инициализация: самый далёкий от среднего, затем дальние от выбранных.
        var centers = new List<(float R, float G, float B)>();
        var mean = (R: px.Average(p => p.R), G: px.Average(p => p.G), B: px.Average(p => p.B));
        centers.Add(Farthest(px, [((float)mean.R, (float)mean.G, (float)mean.B)]));
        while (centers.Count < K) centers.Add(Farthest(px, centers));
        var assign = new int[px.Count];
        for (int iter = 0; iter < 14; iter++)
        {
            for (int i = 0; i < px.Count; i++) assign[i] = Nearest(px[i], centers);
            for (int c = 0; c < K; c++)
            {
                double r = 0, g = 0, b = 0; int n = 0;
                for (int i = 0; i < px.Count; i++) if (assign[i] == c) { r += px[i].R; g += px[i].G; b += px[i].B; n++; }
                if (n > 0) centers[c] = ((float)(r / n), (float)(g / n), (float)(b / n));
            }
        }
        var clusters = Enumerable.Range(0, K).Select(c =>
        {
            var members = Enumerable.Range(0, px.Count).Where(i => assign[i] == c).ToList();
            double share = (double)members.Count / px.Count;
            double cx = members.Count > 0 ? members.Average(i => px[i].X) / k.W : 0.5, cy = members.Count > 0 ? members.Average(i => px[i].Y) / k.H : 0.5;
            return (Color: centers[c], Share: share, X: cx, Y: cy, Index: c);
        }).Where(c => c.Share > 0.005).OrderByDescending(c => c.Share).ToList();

        bool GlassLike((float R, float G, float B) c)
        {
            var (hue, sat, val) = Hsv(c);
            return (hue is >= 165 and <= 250 && sat > 0.14 && val < 0.86) || val < 0.24;
        }
        var opaque = clusters.Where(c => !GlassLike(c.Color)).ToList();
        var glass = clusters.Where(c => GlassLike(c.Color)).ToList();
        var wall = opaque.FirstOrDefault(c => Hsv(c.Color).Val >= 0.3);
        if (wall.Share == 0 && opaque.Count > 0) wall = opaque[0];
        if (wall.Share == 0) wall = clusters[0];
        // Волна 4.1: близкий по цвету (Δ < 48), но светлее непрозрачный кластер — освещённая грань того же материала (стена в тени
        // смешана с окнами); цвет материала — по освещённой грани.
        var lit = opaque.Where(c => c.Index != wall.Index && c.Share >= Math.Max(0.006, wall.Share * 0.1) && ColorGap(c.Color, wall.Color) < 48 &&
                                    Hsv(c.Color).Val > Hsv(wall.Color).Val + 0.04).OrderByDescending(c => Hsv(c.Color).Val).FirstOrDefault();
        if (lit.Share > 0) wall = lit;
        var glassMain = glass.Count > 0 ? glass[0] : clusters.OrderBy(c => Hsv(c.Color).Val).First();
        var accent = opaque.FirstOrDefault(c => c.Index != wall.Index && ColorGap(c.Color, wall.Color) > 28);
        if (accent.Share == 0) accent = wall;
        var frame = clusters.OrderBy(c => Hsv(c.Color).Val).First();

        // Кровля — преобладающий кластер у верха силуэта (4 % высоты под верхом каждого столбца).
        var roofVotes = new int[K];
        if (k.Top.Length == k.W)
            for (int x = k.X0; x <= k.X1; x++)
            {
                if (k.Top[x] < 0) continue;
                int band = Math.Max(2, k.Hpx / 25);
                for (int y = k.Top[x]; y < Math.Min(k.H, k.Top[x] + band); y++)
                {
                    int i = y * k.W + x;
                    roofVotes[Nearest((k.R[i], k.G[i], k.B[i], x, y), centers)]++;
                }
            }
        int roofIndex = Array.IndexOf(roofVotes, roofVotes.Max());
        var roof = roofVotes.Max() > 0 ? centers[roofIndex] : wall.Color;

        d.WallHex = Hex(wall.Color);
        d.GlassHex = Hex(glassMain.Color);
        d.AccentHex = Hex(accent.Color);
        d.FrameHex = Hex(frame.Color);
        d.RoofHex = Hex(roof);
        d.GlazingRatio = glass.Sum(c => c.Share);
        d.Palette.Clear();
        foreach (var c in clusters)
        {
            string role = c.Index == wall.Index ? "wall" : c.Index == glassMain.Index ? "glass" : c.Index == roofIndex ? "roof" : c.Index == frame.Index ? "frame"
                : c.Index == accent.Index ? "accent" : GlassLike(c.Color) ? "glass" : "facade";
            d.Palette.Add(new PhotoPaletteColor(Hex(c.Color), c.Share, role, c.X, c.Y));
        }
    }

    private static (float R, float G, float B) Farthest(List<(float R, float G, float B, int X, int Y)> px, List<(float R, float G, float B)> chosen)
    {
        double best = -1;
        (float, float, float) pick = (px[0].R, px[0].G, px[0].B);
        foreach (var p in px)
        {
            double dmin = chosen.Min(c => (p.R - c.R) * (p.R - c.R) + (p.G - c.G) * (p.G - c.G) + (p.B - c.B) * (p.B - c.B));
            if (dmin > best) { best = dmin; pick = (p.R, p.G, p.B); }
        }
        return pick;
    }

    private static int Nearest((float R, float G, float B, int X, int Y) p, List<(float R, float G, float B)> centers)
    {
        int best = 0;
        double bd = double.MaxValue;
        for (int c = 0; c < centers.Count; c++)
        {
            double d = (p.R - centers[c].R) * (p.R - centers[c].R) + (p.G - centers[c].G) * (p.G - centers[c].G) + (p.B - centers[c].B) * (p.B - centers[c].B);
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }

    private static double ColorGap((float R, float G, float B) a, (float R, float G, float B) b) =>
        Math.Sqrt((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));

    public static (double Hue, double Sat, double Val) Hsv((float R, float G, float B) c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double hue = delta < 1e-9 ? 0 : max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, max < 1e-9 ? 0 : delta / max, max);
    }

    private static string Hex((float R, float G, float B) c) =>
        Models.CurvedDesign.Hex((byte)Math.Clamp((int)Math.Round(c.R), 0, 255), (byte)Math.Clamp((int)Math.Round(c.G), 0, 255), (byte)Math.Clamp((int)Math.Round(c.B), 0, 255));
}
