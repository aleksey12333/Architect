using System.Diagnostics;
using System.Text;

namespace AIArchitectStudio.Services.Pdf;

/// <summary>
/// Надпись листа после склейки глифов: текст, начало и конец базовой линии, кегль и угол (пункты PDF в системе
/// видимого листа с учётом /Rotate: X вправо, Y вверх).
/// </summary>
public sealed record PdfTextRun(string Text, double X, double Y, double EndX, double EndY, double FontSize, double AngleDeg)
{
    private double Rad => AngleDeg * Math.PI / 180;

    /// <summary>Центр строки (середина базовой линии, поднятая на половину высоты прописных).</summary>
    public double CenterX => (X + EndX) / 2 - Math.Sin(Rad) * FontSize * 0.35;

    public double CenterY => (Y + EndY) / 2 + Math.Cos(Rad) * FontSize * 0.35;

    public bool IsHorizontal => Math.Abs(Math.IEEERemainder(AngleDeg, 180)) < 10;

    public bool IsVertical => Math.Abs(Math.Abs(Math.IEEERemainder(AngleDeg, 180)) - 90) < 10;
}

public readonly record struct PdfLineSegment(double X1, double Y1, double X2, double Y2)
{
    public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
}

/// <summary>Замкнутая окружность листа (кривые Безье или многоугольник) — кандидат в кружок марки оси.</summary>
public readonly record struct PdfCircleMark(double X, double Y, double R);

/// <summary>Содержимое страницы из векторного слоя: надписи, отрезки и окружности.</summary>
public sealed class PdfPageVectorContent
{
    public required int Index { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public List<PdfTextRun> Runs { get; } = [];
    public List<PdfLineSegment> Segments { get; } = [];
    public List<PdfCircleMark> Circles { get; } = [];
    public bool SegmentsTruncated { get; set; }

    /// <summary>У страницы есть текстовый слой (иначе это скан — остаётся растровый рендер 2000 px).</summary>
    public bool HasTextLayer => Runs.Count > 0;
}

/// <summary>Результат чтения PDF: страницы, признак шифрования и замечания разбора.</summary>
public sealed class PdfNativeDocument
{
    public List<PdfPageVectorContent> Pages { get; } = [];
    public int PageCount { get; init; }
    public bool Encrypted { get; init; }
    public List<string> Warnings { get; } = [];
    public double ElapsedMs { get; set; }
}

/// <summary>
/// Нативный анализатор содержимого PDF (работает в паре с растровым рендером 2000 px): распаковывает потоки страниц
/// (/Contents, Form XObject, FlateDecode через ZLibStream), выполняет текстовые операторы BT…ET, Tf, Td, TD, Tm, T*, Tj, TJ,
/// ', " и графические cm, q/Q, m, l, re, c, v, y, h с отслеживанием матриц CTM и Tm, декодирует строки по шрифтам
/// (ToUnicode, Identity-H, WinAnsi / CP1251 / UTF-16BE, /Differences) и возвращает надписи с координатами, отрезки
/// и окружности листа. Подходит для PDF из AutoCAD, nanoCAD, Renga, Revit и собственного GostDrawingPdfExporter;
/// скан без текстового слоя даёт пустой список надписей без ошибок.
/// </summary>
public static class PdfNativeContentExtractor
{
    private const int MaxOperatorsPerPage = 4_000_000;
    private const int MaxSegmentsPerPage = 400_000;
    private const int MaxFormDepth = 12;

    public static PdfNativeDocument Read(string path, IReadOnlyCollection<int>? pages = null) => Read(File.ReadAllBytes(path), pages);

    /// <summary>Чтение страниц (индексы с 0; null — все страницы).</summary>
    public static PdfNativeDocument Read(byte[] data, IReadOnlyCollection<int>? pages = null)
    {
        var sw = Stopwatch.StartNew();
        PdfFile file;
        try
        {
            file = new PdfFile(data);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or OverflowException)
        {
            var broken = new PdfNativeDocument { PageCount = 0 };
            broken.Warnings.Add("Структура PDF не распознана: " + ex.Message);
            return broken;
        }

        var infos = file.Pages();
        var doc = new PdfNativeDocument { PageCount = infos.Count, Encrypted = file.Encrypted };
        if (file.Encrypted)
        {
            doc.Warnings.Add("PDF зашифрован — текстовый слой недоступен, используется растровый рендер");
            return doc;
        }

        for (int i = 0; i < infos.Count; i++)
        {
            if (pages is not null && !pages.Contains(i)) continue;
            var info = infos[i];
            var box = info.MediaBox.Length == 4 ? info.MediaBox : [0, 0, 595, 842];
            double x0 = Math.Min(box[0], box[2]), x1 = Math.Max(box[0], box[2]), y0 = Math.Min(box[1], box[3]), y1 = Math.Max(box[1], box[3]);
            bool swap = info.Rotate is 90 or 270;
            var content = new PdfPageVectorContent { Index = i, Width = swap ? y1 - y0 : x1 - x0, Height = swap ? x1 - x0 : y1 - y0 };
            try
            {
                var interpreter = new Interpreter(file, content, BaseMatrix(info.Rotate, x0, y0, x1, y1));
                foreach (var stream in ContentStreams(file, info.Dict))
                    interpreter.Run(stream, info.Resources, 0);
                interpreter.Flush();
                var merged = MergeRuns(content.Runs);
                content.Runs.Clear();
                content.Runs.AddRange(merged);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or InvalidCastException or OverflowException or NullReferenceException)
            {
                doc.Warnings.Add($"Страница {i + 1}: поток содержимого прочитан частично ({ex.GetType().Name})");
            }
            doc.Pages.Add(content);
        }
        doc.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return doc;
    }

    /// <summary>Матрица «пространство страницы → видимый лист» с учётом /Rotate и начала /MediaBox.</summary>
    private static Matrix BaseMatrix(int rotate, double x0, double y0, double x1, double y1) => rotate switch
    {
        90 => new Matrix(0, -1, 1, 0, -y0, x1),
        180 => new Matrix(-1, 0, 0, -1, x1, y1),
        270 => new Matrix(0, 1, -1, 0, y1, -x0),
        _ => new Matrix(1, 0, 0, 1, -x0, -y0)
    };

    private static IEnumerable<byte[]> ContentStreams(PdfFile file, PdfDict page)
    {
        var contents = file.Resolve(page.Raw("Contents"));
        var list = contents is List<object?> arr ? arr.Select(x => file.Resolve(x)).ToList() : [contents];
        // Части одного потока могут обрываться посреди оператора — склеиваются через пробел.
        var all = new MemoryStream();
        foreach (var item in list)
            if (item is PdfStream s)
            {
                var data = file.Decode(s);
                all.Write(data, 0, data.Length);
                all.WriteByte((byte)'\n');
            }
        yield return all.ToArray();
    }

    // ───────────────────────────── Матрицы ─────────────────────────────

    internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

        /// <summary>this × m (векторы-строки, как в спецификации PDF).</summary>
        public Matrix Times(Matrix m) => new(
            A * m.A + B * m.C, A * m.B + B * m.D,
            C * m.A + D * m.C, C * m.B + D * m.D,
            E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    }

    // ───────────────────────────── Интерпретатор ─────────────────────────────

    private sealed class GraphicsState
    {
        public Matrix Ctm;
        public PdfFontDecoder? Font;
        public double FontSize = 1, CharSpacing, WordSpacing, HScale = 1, Leading, Rise;

        public GraphicsState Clone() => (GraphicsState)MemberwiseClone();
    }

    private sealed class Interpreter(PdfFile file, PdfPageVectorContent page, Matrix baseMatrix)
    {
        private readonly Stack<GraphicsState> _stack = new();
        private GraphicsState _gs = new() { Ctm = baseMatrix };
        private Matrix _tm = Matrix.Identity, _tlm = Matrix.Identity;
        private readonly Dictionary<object, PdfFontDecoder> _fonts = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _activeForms = new(ReferenceEqualityComparer.Instance);
        private int _ops;

        // Текущий контур: отрезки до закраски, текущая точка, начало подконтура, сведения для поиска окружностей.
        private readonly List<PdfLineSegment> _pending = [];
        private readonly List<PdfCircleMark> _pendingCircles = [];
        private readonly List<(double X, double Y)> _subPoints = [];
        private (double X, double Y) _cur, _start;
        private int _subCurves, _subLines;
        private double _bx0, _by0, _bx1, _by1;

        public void Run(byte[] content, PdfDict? resources, int depth)
        {
            var lexer = new PdfLexer(content);
            var args = new List<object?>(8);
            while (_ops < MaxOperatorsPerPage)
            {
                var token = lexer.ReadObject(refs: false);
                if (token == PdfLexer.EndOfData) break;
                if (token is not PdfKeyword op)
                {
                    if (args.Count < 64) args.Add(token);
                    continue;
                }
                _ops++;
                if (op.Name == "BI")
                {
                    // Встроенное изображение: словарь до ID, данные до EI.
                    while (lexer.ReadToken() is var t && t != PdfLexer.EndOfData && t is not PdfKeyword { Name: "ID" }) { }
                    lexer.SkipInlineImageData();
                    args.Clear();
                    continue;
                }
                Execute(op.Name, args, resources, depth);
                args.Clear();
            }
        }

        private static double N(List<object?> a, int i) => i < a.Count && a[i] is double d ? d : 0;

        private void Execute(string op, List<object?> a, PdfDict? resources, int depth)
        {
            switch (op)
            {
                // Графическое состояние.
                case "q": _stack.Push(_gs.Clone()); break;
                case "Q": if (_stack.Count > 0) _gs = _stack.Pop(); break;
                case "cm" when a.Count >= 6: _gs.Ctm = new Matrix(N(a, 0), N(a, 1), N(a, 2), N(a, 3), N(a, 4), N(a, 5)).Times(_gs.Ctm); break;

                // Текст.
                case "BT": _tm = _tlm = Matrix.Identity; break;
                case "ET": break;
                case "Tf":
                    if (a.Count >= 2) _gs.FontSize = N(a, 1);
                    if (a.Count >= 1 && a[0] is PdfName fontName) _gs.Font = FontFor(resources, fontName.Value);
                    break;
                case "Tc": _gs.CharSpacing = N(a, 0); break;
                case "Tw": _gs.WordSpacing = N(a, 0); break;
                case "Tz": _gs.HScale = N(a, 0) / 100; break;
                case "TL": _gs.Leading = N(a, 0); break;
                case "Ts": _gs.Rise = N(a, 0); break;
                case "Td": MoveText(N(a, 0), N(a, 1)); break;
                case "TD":
                    _gs.Leading = -N(a, 1);
                    MoveText(N(a, 0), N(a, 1));
                    break;
                case "Tm" when a.Count >= 6: _tm = _tlm = new Matrix(N(a, 0), N(a, 1), N(a, 2), N(a, 3), N(a, 4), N(a, 5)); break;
                case "T*": MoveText(0, -_gs.Leading); break;
                case "Tj": if (a.Count >= 1 && a[0] is PdfString s) ShowText(s.Bytes); break;
                case "'":
                    MoveText(0, -_gs.Leading);
                    if (a.Count >= 1 && a[^1] is PdfString s1) ShowText(s1.Bytes);
                    break;
                case "\"":
                    _gs.WordSpacing = N(a, 0);
                    _gs.CharSpacing = N(a, 1);
                    MoveText(0, -_gs.Leading);
                    if (a.Count >= 3 && a[2] is PdfString s2) ShowText(s2.Bytes);
                    break;
                case "TJ": if (a.Count >= 1 && a[0] is List<object?> items) ShowArray(items); break;

                // Построение контура.
                case "m":
                    EndSubpath();
                    _cur = _start = _gs.Ctm.Apply(N(a, 0), N(a, 1));
                    BeginSubpath(_cur);
                    break;
                case "l":
                {
                    var p = _gs.Ctm.Apply(N(a, 0), N(a, 1));
                    AddLine(_cur, p);
                    _cur = p;
                    break;
                }
                case "c":
                    Curve(_gs.Ctm.Apply(N(a, 0), N(a, 1)), _gs.Ctm.Apply(N(a, 2), N(a, 3)), _gs.Ctm.Apply(N(a, 4), N(a, 5)));
                    break;
                case "v":
                    Curve(_cur, _gs.Ctm.Apply(N(a, 0), N(a, 1)), _gs.Ctm.Apply(N(a, 2), N(a, 3)));
                    break;
                case "y":
                {
                    var end = _gs.Ctm.Apply(N(a, 2), N(a, 3));
                    Curve(_gs.Ctm.Apply(N(a, 0), N(a, 1)), end, end);
                    break;
                }
                case "h":
                    if (_cur != _start) AddLine(_cur, _start);
                    _cur = _start;
                    break;
                case "re":
                {
                    EndSubpath();
                    double x = N(a, 0), y = N(a, 1), w = N(a, 2), h = N(a, 3);
                    var p0 = _gs.Ctm.Apply(x, y);
                    var p1 = _gs.Ctm.Apply(x + w, y);
                    var p2 = _gs.Ctm.Apply(x + w, y + h);
                    var p3 = _gs.Ctm.Apply(x, y + h);
                    BeginSubpath(p0);
                    AddLine(p0, p1);
                    AddLine(p1, p2);
                    AddLine(p2, p3);
                    AddLine(p3, p0);
                    _cur = _start = p0;
                    break;
                }
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                    if (op is "s" or "b" or "b*" && _cur != _start) AddLine(_cur, _start);
                    CommitPath();
                    break;
                case "n":
                    EndSubpath();
                    _pending.Clear();
                    _pendingCircles.Clear();
                    break;

                // Form XObject.
                case "Do" when a.Count >= 1 && a[0] is PdfName xName && depth < MaxFormDepth:
                {
                    var xobjects = file.Dict(resources?.Raw("XObject"));
                    if (file.Resolve(xobjects?.Raw(xName.Value)) is not PdfStream form || file.NameOf(form.Dict, "Subtype") != "Form") break;
                    if (!_activeForms.Add(form)) break;
                    var saved = _gs.Clone();
                    var (tm, tlm) = (_tm, _tlm);
                    if (file.Numbers(form.Dict.Raw("Matrix")) is { Length: 6 } m)
                        _gs.Ctm = new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]).Times(_gs.Ctm);
                    Run(file.Decode(form), file.Dict(form.Dict.Raw("Resources")) ?? resources, depth + 1);
                    _gs = saved;
                    (_tm, _tlm) = (tm, tlm);
                    _activeForms.Remove(form);
                    break;
                }
            }
        }

        private PdfFontDecoder? FontFor(PdfDict? resources, string name)
        {
            var fonts = file.Dict(resources?.Raw("Font"));
            var raw = fonts?.Raw(name);
            if (raw is null) return null;
            var dict = file.Dict(raw);
            if (dict is null) return null;
            if (!_fonts.TryGetValue(dict, out var decoder))
            {
                decoder = PdfFontDecoder.Create(file, dict);
                _fonts[dict] = decoder;
            }
            return decoder;
        }

        private void MoveText(double tx, double ty)
        {
            _tlm = new Matrix(1, 0, 0, 1, tx, ty).Times(_tlm);
            _tm = _tlm;
        }

        private Matrix RenderMatrix() =>
            new Matrix(_gs.FontSize * _gs.HScale, 0, 0, _gs.FontSize, 0, _gs.Rise).Times(_tm).Times(_gs.Ctm);

        private void ShowArray(List<object?> items)
        {
            var text = new StringBuilder();
            (double X, double Y)? start = null;
            Matrix startTrm = default;
            foreach (var item in items)
            {
                if (item is PdfString s)
                {
                    if (start is null)
                    {
                        startTrm = RenderMatrix();
                        start = startTrm.Apply(0, 0);
                    }
                    Advance(s.Bytes, text);
                }
                else if (item is double adjust)
                {
                    double em = -adjust / 1000;
                    // Большой отступ внутри TJ — разрыв надписи (CAD позиционирует так отдельные слова и числа).
                    if (em > 0.9 && start is not null)
                    {
                        Emit(text, start.Value, startTrm);
                        text.Clear();
                        start = null;
                    }
                    else if (em > 0.22 && text.Length > 0 && text[^1] != ' ') text.Append(' ');
                    _tm = new Matrix(1, 0, 0, 1, em * _gs.FontSize * _gs.HScale, 0).Times(_tm);
                }
            }
            if (start is not null) Emit(text, start.Value, startTrm);
        }

        private void ShowText(byte[] bytes)
        {
            var trm = RenderMatrix();
            var start = trm.Apply(0, 0);
            var text = new StringBuilder();
            Advance(bytes, text);
            Emit(text, start, trm);
        }

        private void Advance(byte[] bytes, StringBuilder text)
        {
            if (_gs.Font is not { } font) return;
            foreach (var g in font.Decode(bytes))
            {
                foreach (char ch in g.Text)
                    if (ch >= ' ' && ch != '�' && !char.IsControl(ch)) text.Append(ch);
                double tx = (g.Width / 1000 * _gs.FontSize + _gs.CharSpacing + (g.IsSpace ? _gs.WordSpacing : 0)) * _gs.HScale;
                _tm = new Matrix(1, 0, 0, 1, tx, 0).Times(_tm);
            }
        }

        private void Emit(StringBuilder text, (double X, double Y) start, Matrix trm)
        {
            var s = text.ToString();
            if (s.Trim().Length == 0) return;
            var end = RenderMatrix().Apply(0, 0);
            double size = Math.Sqrt(trm.C * trm.C + trm.D * trm.D);
            double angle = Math.Atan2(trm.B, trm.A) * 180 / Math.PI;
            if (size < 1e-6 || !double.IsFinite(size)) return;
            page.Runs.Add(new PdfTextRun(s, start.X, start.Y, end.X, end.Y, size, angle));
        }

        // ─── Контуры ───

        private void BeginSubpath((double X, double Y) p)
        {
            _subPoints.Clear();
            _subPoints.Add(p);
            _subCurves = _subLines = 0;
            _bx0 = _bx1 = p.X;
            _by0 = _by1 = p.Y;
        }

        private void Grow((double X, double Y) p)
        {
            _bx0 = Math.Min(_bx0, p.X); _bx1 = Math.Max(_bx1, p.X);
            _by0 = Math.Min(_by0, p.Y); _by1 = Math.Max(_by1, p.Y);
        }

        private void AddLine((double X, double Y) a, (double X, double Y) b)
        {
            if (_subPoints.Count == 0) BeginSubpath(a);
            _pending.Add(new PdfLineSegment(a.X, a.Y, b.X, b.Y));
            _subLines++;
            if (_subPoints.Count < 400) _subPoints.Add(b);
            Grow(b);
        }

        private void Curve((double X, double Y) c1, (double X, double Y) c2, (double X, double Y) end)
        {
            if (_subPoints.Count == 0) BeginSubpath(_cur);
            _subCurves++;
            Grow(c1);
            Grow(c2);
            Grow(end);
            _cur = end;
        }

        /// <summary>Окружность: 4+ кривых Безье с квадратным габаритом или многоугольник из 12+ равноудалённых вершин.</summary>
        private void EndSubpath()
        {
            if (_subPoints.Count == 0) return;
            double w = _bx1 - _bx0, h = _by1 - _by0;
            if (w > 0.5 && h > 0.5 && w / h is > 0.85 and < 1.18)
            {
                double cx = (_bx0 + _bx1) / 2, cy = (_by0 + _by1) / 2, r = (w + h) / 4;
                if (_subCurves >= 4 && _subLines <= 1) _pendingCircles.Add(new PdfCircleMark(cx, cy, r));
                else if (_subCurves == 0 && _subLines >= 12)
                {
                    var d = _subPoints.Select(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy))).ToList();
                    double mean = d.Average();
                    if (mean > 0 && d.All(x => Math.Abs(x - mean) < mean * 0.06)) _pendingCircles.Add(new PdfCircleMark(cx, cy, mean));
                }
            }
            _subPoints.Clear();
        }

        private void CommitPath()
        {
            EndSubpath();
            foreach (var s in _pending)
            {
                if (page.Segments.Count >= MaxSegmentsPerPage)
                {
                    page.SegmentsTruncated = true;
                    break;
                }
                if (s.Length > 1e-6) page.Segments.Add(s);
            }
            if (page.Circles.Count < 20000) page.Circles.AddRange(_pendingCircles);
            _pending.Clear();
            _pendingCircles.Clear();
        }

        public void Flush() => CommitPath();
    }

    // ───────────────────────────── Склейка надписей ─────────────────────────────

    /// <summary>
    /// Склейка глифов и фрагментов в строки: одинаковый угол, общая базовая линия (±0.3 кегля), зазор по направлению
    /// письма не более кегля (при зазоре больше 0.22 кегля вставляется пробел). CAD-программы часто выводят каждую
    /// букву отдельным оператором — после склейки «3», «5», «0», «0» становятся числом «3500».
    /// </summary>
    internal static List<PdfTextRun> MergeRuns(List<PdfTextRun> runs)
    {
        var result = new List<PdfTextRun>(runs.Count);
        foreach (var bucket in runs.Where(r => r.Text.Trim().Length > 0).GroupBy(r => NormalizeAngle(Math.Round(r.AngleDeg))))
        {
            double rad = bucket.Key * Math.PI / 180, ux = Math.Cos(rad), uy = Math.Sin(rad), nx = -uy, ny = ux;
            var items = bucket.Select(r => (Run: r, S: r.X * ux + r.Y * uy, E: r.EndX * ux + r.EndY * uy, B: r.X * nx + r.Y * ny))
                .OrderBy(i => i.B).ThenBy(i => i.S).ToList();

            int at = 0;
            while (at < items.Count)
            {
                // Строка — все фрагменты с базовой линией в пределах 0.3 кегля от первого.
                double b0 = items[at].B, tol = 0.3 * items[at].Run.FontSize;
                int end = at;
                while (end < items.Count && Math.Abs(items[end].B - b0) <= tol) end++;
                var line = items.GetRange(at, end - at).OrderBy(i => i.S).ToList();
                at = end;

                var cur = line[0];
                var text = new StringBuilder(cur.Run.Text);
                for (int k = 1; k <= line.Count; k++)
                {
                    bool join = false;
                    if (k < line.Count)
                    {
                        var next = line[k];
                        double size = Math.Max(cur.Run.FontSize, next.Run.FontSize);
                        double gap = next.S - cur.E;
                        bool similar = Math.Abs(cur.Run.FontSize - next.Run.FontSize) <= 0.3 * size;
                        if (similar && gap <= 1.0 * size && gap >= -0.5 * size)
                        {
                            if (gap > 0.22 * size && text.Length > 0 && text[^1] != ' ' && !next.Run.Text.StartsWith(' ')) text.Append(' ');
                            text.Append(next.Run.Text);
                            cur = (cur.Run with { EndX = next.Run.EndX, EndY = next.Run.EndY }, cur.S, next.E, cur.B);
                            join = true;
                        }
                    }
                    if (join) continue;
                    var t = text.ToString().Trim();
                    if (t.Length > 0) result.Add(cur.Run with { Text = t });
                    if (k < line.Count)
                    {
                        cur = line[k];
                        text.Clear().Append(cur.Run.Text);
                    }
                }
            }
        }
        return result;
    }

    private static double NormalizeAngle(double a)
    {
        a %= 360;
        if (a <= -180) a += 360;
        if (a > 180) a -= 360;
        return a == -180 ? 180 : a;
    }
}
