#if DEBUG
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Verification;
using AIArchitectStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест финального аудита: параллельная подготовка КЭ-расчёта даёт тот же результат до бита, что и последовательная,
/// и не зависит от прогона; адрес Ollama из настроек нормализуется без исключений; дочерний процесс входит в задание
/// «завершить вместе с приложением»; сбой стороннего плагина не прерывает конвейер Совета; правка оси на месте — шаг Undo.
/// </summary>
public static partial class CadSelfTestService
{
    private static async Task<int> CheckProductionAuditAsync(StringBuilder report)
    {
        int failures = 0;
        CheckAnalysisDeterminism(report, ref failures);
        CheckOllamaEndpoints(report, ref failures);
        CheckChildProcessJob(report, ref failures);
        CheckPipelineResilience(report, ref failures);
        failures += await CheckAxisEditUndoAsync(report);
        return failures;
    }

    /// <summary>Башня, каркас и коттедж: параллельно и последовательно, три прогона подряд — отпечатки всех результатов равны.</summary>
    private static void CheckAnalysisDeterminism(StringBuilder report, ref int failures)
    {
        var (frame, frameGroups, _) = CreateFrameScene();
        var tower = PresetScenesService.Create(PresetScenesService.Tower);
        var cottage = PresetScenesService.Create(PresetScenesService.Cottage);
        var loads = new StructuralLoadSettings { Seismicity = 8 };
        foreach (var (title, elements, groups) in new (string, IReadOnlyList<ArchitecturalElement>, IReadOnlyList<BuildingGroup>)[]
                 {
                     ("башня 30 эт.", tower.Elements, tower.Groups), ("каркас", frame, frameGroups), ("коттедж", cottage.Elements, cottage.Groups)
                 })
        {
            string sequential = AnalysisFingerprint(StructuralAnalysisService.Analyze(elements, groups, loads, parallel: false));
            var runs = Enumerable.Range(0, 3).Select(_ => AnalysisFingerprint(StructuralAnalysisService.Analyze(elements, groups, loads))).ToList();
            bool same = runs.All(r => r == sequential);
            Check(report, ref failures, same,
                $"КЭ-расчёт «{title}»: параллельная подготовка плит ≡ последовательной до бита, 3 прогона совпали (отпечаток {sequential.Length} симв.)");
        }

        // Худший из 20 прогонов башни — диагностика запаса до норматива (норматив проверяется медианой в разделе КЭ-расчёта).
        var times = Enumerable.Range(0, 20).Select(_ => StructuralAnalysisService.Analyze(tower.Elements, tower.Groups, new StructuralLoadSettings()).ElapsedMilliseconds).Order().ToList();
        report.AppendLine($"    Башня, 20 прогонов: мин. {times[0]:0.00}, медиана {times[10]:0.00}, p90 {times[18]:0.00}, макс. {times[^1]:0.00} мс; ядер {Environment.ProcessorCount}");
    }

    /// <summary>Все числовые поля результатов элементов, узлов продавливания и сдвига в основании — в точной записи double.</summary>
    private static string AnalysisFingerprint(StructuralAnalysisResult r)
    {
        var sb = new StringBuilder();
        string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        foreach (var (id, e) in r.Elements.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(id).Append(':').Append(D(e.AxialForce)).Append(',').Append(D(e.Moment)).Append(',').Append(D(e.Shear)).Append(',').Append(D(e.Span))
                .Append(',').Append(D(e.DeflectionMm)).Append(',').Append(D(e.DeflectionLimitMm)).Append(',').Append(D(e.Slenderness)).Append(',').Append(D(e.Phi))
                .Append(',').Append(D(e.DesignLoad)).Append(',').Append(D(e.KStrength)).Append(',').Append(D(e.KStability)).Append(',').Append(D(e.KShear))
                .Append(',').Append(D(e.KDeflection)).Append(',').Append(D(e.KPunching)).Append(',').Append(D(e.KSoil)).Append(',').Append(e.Punching)
                .Append(',').Append(e.IsCantilever).Append(',').Append(e.Floor).Append(',').Append(string.Join("|", e.Notes)).Append(';');
        foreach (var n in r.PunchingNodes)
            sb.Append(n.ColumnId).Append('>').Append(n.SlabId).Append(':').Append(D(n.Force)).Append(',').Append(D(n.Capacity)).Append(',').Append(D(n.Perimeter)).Append(';');
        foreach (var (g, (wind, seismic)) in r.BaseShear.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(g).Append('=').Append(D(wind)).Append('/').Append(D(seismic)).Append(';');
        return sb.ToString();
    }

    /// <summary>Ошибка ввода адреса сервера в настройках не роняет запуск движка исключением разбора URI.</summary>
    private static void CheckOllamaEndpoints(StringBuilder report, ref int failures)
    {
        var cases = new (string? Input, string Expected)[]
        {
            (null, "http://127.0.0.1:11434/"),
            ("", "http://127.0.0.1:11434/"),
            ("http://localhost:11434", "http://127.0.0.1:11434/"),
            ("localhost:11500", "http://127.0.0.1:11500/"),
            ("http://gpu-box:8080/", "http://gpu-box:8080/"),
            ("https://ollama.example.com", "https://ollama.example.com/"),
            ("ftp://server/x", "http://127.0.0.1:11434/"),
            ("адрес с пробелами ::", "http://127.0.0.1:11434/")
        };
        var bad = new List<string>();
        foreach (var (input, expected) in cases)
        {
            string actual;
            try
            {
                actual = OllamaProcessManager.NormalizeEndpoint(input).ToString();
            }
            catch (UriFormatException ex)
            {
                actual = "исключение " + ex.GetType().Name;
            }
            if (actual != expected) bad.Add($"«{input}» → {actual} (ожидалось {expected})");
        }
        Check(report, ref failures, bad.Count == 0,
            $"адрес Ollama из настроек: {cases.Length} вариантов (без схемы, localhost → 127.0.0.1, ftp, мусор) нормализуются без исключений" +
            (bad.Count > 0 ? ": " + string.Join("; ", bad) : string.Empty));
    }

    /// <summary>Объект задания Windows создаётся (размеры структур P/Invoke верны) и принимает дочерний процесс.</summary>
    private static void CheckChildProcessJob(StringBuilder report, ref int failures)
    {
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        if (!File.Exists(ping))
        {
            report.AppendLine("    Задание Windows: ping.exe не найден — проверка пропущена");
            return;
        }
        using var child = Process.Start(new ProcessStartInfo(ping, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false });
        bool assigned = child is not null && ChildProcessJob.TryAssign(child);
        try
        {
            child?.Kill();
            child?.WaitForExit(3000);
        }
        catch (InvalidOperationException)
        {
            // Уже завершён.
        }
        Check(report, ref failures, assigned,
            "ollama serve и его раннеры входят в задание Windows JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE — при аварийном завершении приложения сирот не остаётся");
    }

    /// <summary>Сторонний плагин бросает исключение не из «ожидаемого» списка — конвейер выпускает отчёт с пометкой -FAIL.</summary>
    private static void CheckPipelineResilience(StringBuilder report, ref int failures)
    {
        var pipeline = BimVerificationPipeline.CreateDefault();
        pipeline.Register(new FaultyVerifier(new InvalidCastException("сбой стороннего плагина")));
        var (elements, groups, axes) = CreateCouncilTestScene();
        VerificationReport? result = null;
        string error = string.Empty;
        try
        {
            result = pipeline.Run(new VerificationContext(elements.Select(e => e.Clone()).ToList(), groups, axes, new StructuralLoadSettings()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        bool ok = result is not null && result.Findings.Any(f => f.Code == "TESTFAULT-FAIL") && result.CriticalCount > 0;
        Check(report, ref failures, ok,
            $"конвейер Совета: сбой стороннего плагина (InvalidCastException) — замечание TESTFAULT-FAIL, остальные {pipeline.Verifiers.Count - 1} проверщиков отработали " +
            (result is null ? $"(конвейер прерван: {error})" : $"(критичных {result.CriticalCount})"));
    }

    /// <summary>Проверщик автотеста: конструктор с параметром — автообнаружение конвейера его не подключает.</summary>
    private sealed class FaultyVerifier(Exception error) : IBimDesignVerifier
    {
        public string Id => "TESTFAULT";
        public string Title => "Неисправный плагин автотеста";
        public int Order => 999;
        public IEnumerable<VerificationFinding> Verify(VerificationContext context) => throw error;
    }

    /// <summary>
    /// Вкладка «Оси»: правка координаты и подписи явной оси на месте (как TwoWay-привязка поля) записывает шаг Undo
    /// с прежним значением; Undo возвращает ось.
    /// </summary>
    private static async Task<int> CheckAxisEditUndoAsync(StringBuilder report)
    {
        int failures = 0;
        var vm = App.Services.GetRequiredService<MainViewModel>();
        var (elements, groups, axes) = CreateFrameScene();
        vm.Scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes);
        vm.IsAxesTab = true;
        vm.SelectedAxesGroup = groups[0].Id;
        await Task.Yield();
        var axis = vm.EditableAxes.FirstOrDefault(a => a.IsNumeric);
        if (axis is null)
        {
            Check(report, ref failures, false, "правка оси на месте: в редакторе осей нет явных осей каркаса");
            return failures;
        }
        string label = axis.Label;
        double before = axis.Coordinate;
        int depth = vm.Scene.UndoDepth;
        axis.Coordinate = before + 1.5;
        int afterCoordinate = vm.Scene.UndoDepth;
        axis.Label = label + "'";
        int afterLabel = vm.Scene.UndoDepth;
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        var restored = vm.Scene.GridAxes.FirstOrDefault(a => a.IsNumeric && a.BuildingGroupId == groups[0].Id && a.Label == label);
        bool ok = afterCoordinate == depth + 1 && afterLabel == depth + 2 && restored is not null && restored.Coordinate == before;
        Check(report, ref failures, ok,
            $"правка оси «{label}» на месте: координата и подпись — по шагу Undo ({depth} → {afterCoordinate} → {afterLabel}), Undo вернул X = {restored?.Coordinate.ToString("0.###", CultureInfo.InvariantCulture) ?? "—"} м");

        // Вкладка и сцена — как при запуске (Пресет 1), история очищена.
        vm.IsInspectorTab = true;
        var cottage = PresetScenesService.Create(PresetScenesService.Cottage);
        vm.Scene.LoadScene(cottage.Elements.Select(e => e.Clone()).ToList(), cottage.Groups.Select(g => g.Clone()).ToList(), recordUndo: false, clearHistory: true, axes: [], dimensions: []);
        return failures;
    }
}
#endif
