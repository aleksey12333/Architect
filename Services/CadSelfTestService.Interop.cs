#if DEBUG
using System.Text;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест интероперабельности свойств пользователя (фаза 2.5): набор IfcPropertySet «AIA_UserParameters» с типами IFC по смыслу
/// параметра и его Round-Trip через импорт IFC, расширенные данные XData «AIA_USERPARAMS» на примитивах DXF (таблица APPID, чтение
/// значений обратно, импорт DXF не меняется), ведомость объёмов по значениям свойства (сумма строк ≡ итогам ведомости).
/// </summary>
public static partial class CadSelfTestService
{
    /// <summary>Свойства пользователя тестового каркаса — постоянные Guid, чтобы эталон (.aia, IFC, DXF) не зависел от прогона.</summary>
    internal static readonly ParameterDefinition FrameFireRating = Fixed("2b6f3c1e-8a14-4d2b-9f0e-5c7a1d3e9b01", "Огнестойкость", ParameterSpecType.Text);
    internal static readonly ParameterDefinition FrameMark = Fixed("2b6f3c1e-8a14-4d2b-9f0e-5c7a1d3e9b02", "Марка", ParameterSpecType.Text);
    internal static readonly ParameterDefinition FramePourStage = Fixed("2b6f3c1e-8a14-4d2b-9f0e-5c7a1d3e9b03", "Этап заливки", ParameterSpecType.Integer);
    internal static readonly ParameterDefinition FrameCover = Fixed("2b6f3c1e-8a14-4d2b-9f0e-5c7a1d3e9b04", "Защитный слой", ParameterSpecType.Length);
    internal static readonly ParameterDefinition FrameBearing = Fixed("2b6f3c1e-8a14-4d2b-9f0e-5c7a1d3e9b05", "Несущая", ParameterSpecType.YesNo);

    private static ParameterDefinition Fixed(string guid, string name, ParameterSpecType spec) => new()
    {
        Guid = Guid.Parse(guid),
        Name = name,
        SpecType = spec,
        StorageType = ParameterSpecs.StorageFor(spec)
    };

    /// <summary>
    /// Свойства пользователя каркаса (SCENE=frame и эталон «selftest-frame»): колонны — огнестойкость, марка по сечению, этап заливки
    /// по этажу и защитный слой; балки — огнестойкость и марка; плиты — огнестойкость и этап заливки; стены — «Несущая»; окно — марка.
    /// </summary>
    public static void ApplyFrameUserParameters(IEnumerable<ArchitecturalElement> elements)
    {
        var registry = ParameterRegistry.Shared;
        var fire = registry.Register(FrameFireRating)!;
        var mark = registry.Register(FrameMark)!;
        var stage = registry.Register(FramePourStage)!;
        var cover = registry.Register(FrameCover)!;
        var bearing = registry.Register(FrameBearing)!;
        foreach (var e in elements)
        {
            switch (e.Category)
            {
                case BimCategory.Column:
                    e.SetUserParameter(fire, ParameterValue.FromString(e.LevelIndex == 0 ? "R 150" : "R 90"));
                    e.SetUserParameter(mark, ParameterValue.FromString(e.EffectiveProfile switch
                    {
                        ProfileTypes.Circle => "К2",
                        ProfileTypes.Pipe => "К3",
                        ProfileTypes.IBeam => "К4",
                        _ => "К1"
                    }));
                    e.SetUserParameter(stage, ParameterValue.FromInt(e.LevelIndex + 1));
                    e.SetUserParameter(cover, ParameterValue.FromDouble(0.04));
                    break;
                case BimCategory.Beam:
                    e.SetUserParameter(fire, ParameterValue.FromString("R 90"));
                    e.SetUserParameter(mark, ParameterValue.FromString(QuantityScheduleService.IsRolledSteel(e) ? "Б2" : "Б1"));
                    break;
                case BimCategory.Slab:
                case BimCategory.Roof:
                    e.SetUserParameter(fire, ParameterValue.FromString("REI 60"));
                    e.SetUserParameter(stage, ParameterValue.FromInt(e.LevelIndex + 1));
                    e.SetUserParameter(cover, ParameterValue.FromDouble(0.025));
                    break;
                case BimCategory.Wall:
                    e.SetUserParameter(bearing, ParameterValue.FromBool(e.Material == "concrete"));
                    break;
                case BimCategory.Window:
                    e.SetUserParameter(mark, ParameterValue.FromString("ОК-1"));
                    break;
            }
        }
    }

    private static async Task<int> CheckUserParameterExchangeAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        var registry = ParameterRegistry.Shared;
        var (elements, groups, axes) = CreateFrameScene();
        ApplyFrameUserParameters(elements);
        // Все смыслы значений и «опасные» имена: свойство «Width» не должно подменить служебный габарит при импорте.
        var mass = registry.Define("Масса закладной (самотест)", ParameterSpecType.Mass);
        var load = registry.Define("Нагрузка на узел (самотест)", ParameterSpecType.Force);
        var density = registry.Define("Плотность заполнителя (самотест)", ParameterSpecType.Density);
        var area = registry.Define("Площадь опирания (самотест)", ParameterSpecType.Area);
        var volume = registry.Define("Объём закладной (самотест)", ParameterSpecType.Volume);
        var ratio = registry.Define("Коэффициент (самотест)", ParameterSpecType.Number);
        var note = registry.Define("Примечание (самотест)", ParameterSpecType.Text);
        var link = registry.Define("Связанная колонна (самотест)", ParameterSpecType.ElementReference);
        var spoof = registry.Define("Width", ParameterSpecType.Text);
        var column = elements.First(e => e.Category == BimCategory.Column && e.EffectiveProfile == ProfileTypes.Rectangle);
        var beam = elements.First(e => e.Category == BimCategory.Beam);
        column.SetUserParameter(mass, ParameterValue.FromDouble(12.75));
        column.SetUserParameter(load, ParameterValue.FromDouble(-1.5e5));
        column.SetUserParameter(density, ParameterValue.FromDouble(2400));
        column.SetUserParameter(area, ParameterValue.FromDouble(0.1 + 0.2));
        column.SetUserParameter(volume, ParameterValue.FromDouble(1e-5));
        column.SetUserParameter(ratio, ParameterValue.FromDouble(1.0 / 3));
        column.SetUserParameter(spoof, ParameterValue.FromString("не габарит"));
        beam.SetUserParameter(note, ParameterValue.FromString("Длинное «примечание» с апострофом ' и обратной чертой \\ " + new string('ж', 300)));
        beam.SetUserParameter(link, ParameterValue.FromElementId(column.Id));
        var carriers = elements.Where(e => e.UserParameters is not null).ToList();
        int values = carriers.Sum(e => e.Parameters.UserCount);

        // ── IFC: отдельный набор у каждого элемента со свойствами, типы по смыслу, единицы массы/силы/плотности объявлены.
        var export = new CadExportService();
        string ifcPath = Path.Combine(directory, "user_parameters.ifc");
        var ifcResult = await export.ExportAsync(CadExportFormat.Ifc, ifcPath, elements.Select(e => e.Clone()).ToList(), groups, "Свойства пользователя", axes);
        string ifcText = await File.ReadAllTextAsync(ifcPath);
        var (entities, _) = CadIfcImporterService.ParseStep(ifcText);
        var userSets = entities.Values.Where(e => e.Type == "IFCPROPERTYSET" && e.Args.ElementAtOrDefault(2) as string == CadExportService.UserPsetName).ToList();
        var linked = entities.Values.Where(e => e.Type == "IFCRELDEFINESBYPROPERTIES" && e.Args.ElementAtOrDefault(5) is CadIfcImporterService.StepRef r && userSets.Any(s => s.Id == r.Id)).ToList();
        var singles = userSets.SelectMany(s => s.Args.ElementAtOrDefault(4) as List<object?> ?? []).OfType<CadIfcImporterService.StepRef>()
            .Select(r => entities[r.Id]).ToList();
        var types = singles.Select(p => (p.Args.ElementAtOrDefault(2) as CadIfcImporterService.StepTyped)?.Name).ToHashSet();
        string[] expectedTypes = ["IFCLABEL", "IFCTEXT", "IFCINTEGER", "IFCREAL", "IFCBOOLEAN", "IFCIDENTIFIER", "IFCLENGTHMEASURE", "IFCAREAMEASURE",
            "IFCVOLUMEMEASURE", "IFCMASSMEASURE", "IFCMASSDENSITYMEASURE", "IFCFORCEMEASURE"];
        bool units = ifcText.Contains("IFCSIUNIT(*,.MASSUNIT.,.KILO.,.GRAM.)", StringComparison.Ordinal) && ifcText.Contains("IFCSIUNIT(*,.FORCEUNIT.,$,.NEWTON.)", StringComparison.Ordinal) &&
                     ifcText.Contains(".MASSDENSITYUNIT.", StringComparison.Ordinal);
        bool guids = singles.All(p => Guid.TryParse(p.Args.ElementAtOrDefault(1) as string, out var g) && registry.Find(g) is { } d && d.Name == p.Args[0] as string);
        string plainIfc = Path.Combine(directory, "user_parameters_plain.ifc");
        var (plain, plainGroups, plainAxes) = CreateFrameScene();
        await export.ExportAsync(CadExportFormat.Ifc, plainIfc, plain, plainGroups, "Свойства пользователя", plainAxes);
        string plainText = await File.ReadAllTextAsync(plainIfc);
        bool plainClean = !plainText.Contains(CadExportService.UserPsetName, StringComparison.Ordinal) && !plainText.Contains(".MASSUNIT.", StringComparison.Ordinal);
        Check(report, ref failures, userSets.Count == carriers.Count && linked.Count == carriers.Count && singles.Count == values &&
                                    expectedTypes.All(types.Contains) && units && guids && plainClean,
            $"IFC: IfcPropertySet «{CadExportService.UserPsetName}» × {userSets.Count} (элементов со свойствами {carriers.Count}) через IfcRelDefinesByProperties, " +
            $"значений {singles.Count} из {values}; типы {string.Join(", ", expectedTypes.Where(types.Contains).Select(t => t[3..]))}; описание свойства — Guid определения; " +
            "единицы кг, Н, кг/м³ объявлены; файл без свойств — без набора и новых единиц");

        // ── IFC Round-Trip: значения до бита (длина ×1 — метры), ссылка на элемент, Guid определений; «Width» не меняет габарит.
        var imported = await new CadIfcImporterService().ImportAsync(ifcPath);
        var mismatched = new List<string>();
        foreach (var src in elements)
        {
            var dst = imported.Elements.FirstOrDefault(e => e.Id == src.Id);
            if (dst is null) { if (src.UserParameters is not null) mismatched.Add($"нет {src.Name}"); continue; }
            if (!SameUserValues(src, dst)) mismatched.Add(src.Name);
        }
        var importedColumn = imported.Elements.First(e => e.Id == column.Id);
        bool sizes = Math.Abs(importedColumn.Width - column.Width) < Tol && Math.Abs(importedColumn.Depth - column.Depth) < Tol;
        Check(report, ref failures, mismatched.Count == 0 && sizes && imported.Report.Any(l => l.Contains(CadExportService.UserPsetName, StringComparison.Ordinal)),
            $"IFC Round-Trip свойств: {carriers.Count} элементов, значения ≡ исходным до бита (текст 300+ знаков, апостроф, кириллица, 1/3, 0,1 + 0,2, 1e-5, −150 кН, ссылка на колонну), " +
            "Guid определений сохранены; свойство «Width» не подменило габарит колонны" + (mismatched.Count > 0 ? ": " + string.Join(", ", mismatched.Take(5)) : string.Empty));

        // ── DXF: XData на каждом 3DFACE и LINE элемента, APPID зарегистрирован, значения читаются обратно; импорт DXF как без свойств.
        string dxfPath = Path.Combine(directory, "user_parameters.dxf");
        var dxfResult = await export.ExportAsync(CadExportFormat.Dxf, dxfPath, elements.Select(e => e.Clone()).ToList(), groups, "Свойства пользователя", axes);
        string dxfText = await File.ReadAllTextAsync(dxfPath);
        var xdata = ReadDxfXData(dxfText, out int carrierEntities, out int bareEntities, out bool appId, out int fullRecords);
        var dxfMismatch = new List<string>();
        foreach (var src in carriers)
        {
            if (!xdata.TryGetValue(src.Id, out var read)) { dxfMismatch.Add("нет " + src.Name); continue; }
            foreach (var (guid, value) in src.UserParameters!)
            {
                var d = registry.Find(guid)!;
                if (!read.TryGetValue(guid, out var got) || got.Name != d.Name || got.Spec != d.SpecType.ToString() || !SameXDataValue(value, got))
                    dxfMismatch.Add($"{src.Name}.{d.Name}");
            }
        }
        int perElement = carriers.Sum(e => DxfEntitiesOf(dxfText, e));
        string plainDxf = Path.Combine(directory, "user_parameters_plain.dxf");
        var plainDxfResult = await export.ExportAsync(CadExportFormat.Dxf, plainDxf, CreateFrameScene().Elements, groups, "Свойства пользователя", axes);
        // XData — на контуре и первой грани, а не на каждой грани объёма: файл растёт умеренно.
        bool compact = dxfResult.Bytes < plainDxfResult.Bytes * 1.5;
        string plainDxfText = await File.ReadAllTextAsync(plainDxf);
        ReadDxfXData(plainDxfText, out _, out int plainEntities, out _);
        var liftWith = CadDxfImporterService.LiftTo3D(await new CadDxfImporterService().LoadAsync(dxfPath), new DxfLiftOptions(FrameGroup));
        var liftPlain = CadDxfImporterService.LiftTo3D(await new CadDxfImporterService().LoadAsync(plainDxf), new DxfLiftOptions(FrameGroup));
        bool sameLift = liftWith.Elements.Count == liftPlain.Elements.Count &&
                        liftWith.Elements.Zip(liftPlain.Elements).All(z => z.First.Type == z.Second.Type && Math.Abs(z.First.X - z.Second.X) < Tol && Math.Abs(z.First.Y - z.Second.Y) < Tol &&
                                                                          Math.Abs(z.First.Z - z.Second.Z) < Tol && Math.Abs(z.First.Width - z.Second.Width) < Tol);
        bool plainDxfClean = !plainDxfText.Contains(CadExportService.UserXDataApp, StringComparison.Ordinal) && !plainDxfText.Contains("\nAPPID\n", StringComparison.Ordinal);
        Check(report, ref failures, appId && dxfMismatch.Count == 0 && xdata.Count == carriers.Count && fullRecords == carriers.Count && carrierEntities == perElement &&
                                    carrierEntities > fullRecords && carrierEntities + bareEntities == plainEntities && sameLift && plainDxfClean && compact,
            $"DXF: XData «{CadExportService.UserXDataApp}» (APPID зарегистрирован) у {xdata.Count} элементов — полная запись на первом примитиве ({fullRecords}), " +
            $"ссылка по Id элемента на LINE контура (всего с XData {carrierEntities}), " +
            $"без XData — {bareEntities} (всего {plainEntities}, как в файле без свойств; {plainDxfResult.Bytes / 1024} → {dxfResult.Bytes / 1024} КБ); " +
            $"имя, Guid, смысл и значение (1040 СИ / 1070 / 1000) каждого свойства прочитаны обратно ≡ исходным; распознавание DXF ({liftWith.Elements.Count} эл.) — как у файла без XData; " +
            "файл без свойств — без XData и APPID" + (dxfMismatch.Count > 0 ? ": " + string.Join(", ", dxfMismatch.Take(5)) : string.Empty));

        // ── Ведомость по свойствам: варианты группировки, сумма строк ≡ итогам ведомости, строка «не задано» — последней.
        var analysis = StructuralAnalysisService.Analyze(elements, groups, new StructuralLoadSettings());
        var punching = BimAnalyticsService.PunchingReinforcedIds(analysis);
        var schedule = QuantityScheduleService.Calculate(elements, analysis.UtilizationById, punching);
        var byMark = QuantityScheduleService.GroupByParameter(elements, FrameMark, analysis.UtilizationById, punching);
        var byStage = QuantityScheduleService.GroupByParameter(elements, FramePourStage, analysis.UtilizationById, punching);
        static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(b));
        bool Sums(ParameterQuantityBreakdown b) =>
            Near(b.Groups.Sum(g => g.Concrete), schedule.ConcreteTotal) && Near(b.Groups.Sum(g => g.Rebar), schedule.Rebar.Total) &&
            Near(b.Groups.Sum(g => g.Glazing), schedule.GlazingTotal) && Near(b.Groups.Sum(g => g.RolledSteel), schedule.RolledSteelTotal) &&
            b.Groups.Where(g => !g.IsUnset).Sum(g => g.Count) == elements.Count(e => !e.GetUserParameter(b.Definition.Guid).IsEmpty) &&
            b.Groups.Count(g => g.IsUnset) <= 1 && (b.Groups.Count == 0 || !b.Groups.Take(b.Groups.Count - 1).Any(g => g.IsUnset));
        bool options = new[] { FrameFireRating, FrameMark, FramePourStage, FrameCover, FrameBearing }.All(d => schedule.UserParameters.Any(u => u.Guid == d.Guid));
        var stageValues = byStage.Groups.Where(g => !g.IsUnset).Select(g => g.Value.AsInt()).ToList();
        var k1 = byMark.Groups.FirstOrDefault(g => g.Value.AsString() == "К1");
        bool scheduleOk = options && Sums(byMark) && Sums(byStage) && stageValues.SequenceEqual([1, 2, 3]) && k1 is { Count: > 0, Concrete: > 0, RolledSteel: 0 } &&
                          byMark.Groups.FirstOrDefault(g => g.Value.AsString() == "К4") is { RolledSteel: > 0 };
        Check(report, ref failures, scheduleOk,
            $"ведомость по свойствам: вариантов группировки {schedule.UserParameters.Count}; «Марка» — {byMark.Groups.Count} строк (К1: {k1?.Count} эл., бетон {k1?.Concrete:0.00} м³), " +
            $"«Этап заливки» — этапы {string.Join(", ", stageValues)} и «не задано» последней; сумма строк ≡ итогам ведомости (бетон {schedule.ConcreteTotal:0.00} м³, " +
            $"арматура {schedule.Rebar.Total:0} кг, остекление, прокат)");
        report.AppendLine($"  IFC {ifcResult.Bytes / 1024} КБ, DXF {dxfResult.Bytes / 1024} КБ; {string.Join(" | ", ifcResult.Details.Concat(dxfResult.Details).Where(d => d.Contains("Свойства пользователя", StringComparison.Ordinal)))}");
        return failures;
    }

    private sealed record XDataValue(string Name, string Spec, int Code, string Value);

    /// <summary>
    /// Разбор XData AIA_USERPARAMS из текста DXF: Id элемента → Guid → (имя, смысл, код и значение); считает примитивы ENTITIES с XData
    /// и без неё, проверяет регистрацию приложения в таблице APPID.
    /// </summary>
    private static Dictionary<string, Dictionary<Guid, XDataValue>> ReadDxfXData(string text, out int withXData, out int without, out bool appId) =>
        ReadDxfXData(text, out withXData, out without, out appId, out _);

    /// <param name="full">Примитивов с полной записью свойств (у остальных с XData — только ссылка на Id элемента).</param>
    private static Dictionary<string, Dictionary<Guid, XDataValue>> ReadDxfXData(string text, out int withXData, out int without, out bool appId, out int full)
    {
        full = 0;
        var lines = text.Split('\n');
        var pairs = new List<(int Code, string Value)>(lines.Length / 2);
        for (int i = 0; i + 1 < lines.Length; i += 2) pairs.Add((int.Parse(lines[i].Trim(), Inv), lines[i + 1]));
        var result = new Dictionary<string, Dictionary<Guid, XDataValue>>(StringComparer.Ordinal);
        withXData = without = 0;
        appId = false;
        string section = string.Empty;
        for (int i = 0; i < pairs.Count; i++)
        {
            var (code, value) = pairs[i];
            if (code == 2 && i > 0 && pairs[i - 1] is (0, "SECTION")) section = value;
            if (section == "TABLES" && code == 0 && value == "APPID" && pairs[i + 1] is (2, CadExportService.UserXDataApp)) appId = true;
            if (section != "ENTITIES" || code != 0 || value is not ("3DFACE" or "LINE")) continue;
            int j = i + 1;
            while (j < pairs.Count && pairs[j].Code != 0 && pairs[j].Code != 1001) j++;
            if (j >= pairs.Count || pairs[j].Code != 1001) { without++; continue; }
            withXData++;
            string id = DecodeDxf(pairs[j + 1].Value);
            var map = result.TryGetValue(id, out var m) ? m : result[id] = [];
            if (j + 2 < pairs.Count && pairs[j + 2] is (1002, "{")) full++;
            for (j += 2; j < pairs.Count && pairs[j].Code != 0; j++)
            {
                if (pairs[j] is not (1002, "{")) continue;
                var name = DecodeDxf(pairs[j + 1].Value);
                var guid = Guid.Parse(pairs[j + 2].Value);
                var spec = pairs[j + 3].Value;
                var (vc, vv) = pairs[j + 4];
                map.TryAdd(guid, new XDataValue(name, spec, vc, vc == 1000 ? DecodeDxf(vv) : vv));
                j += 5;
            }
        }
        return result;
    }

    /// <summary>Число примитивов 3DFACE/LINE элемента с XData (по Id в первой строке 1000).</summary>
    private static int DxfEntitiesOf(string text, ArchitecturalElement e) =>
        CountOf(text, $"1001\n{CadExportService.UserXDataApp}\n1000\n{e.Id}\n");

    private static bool SameXDataValue(ParameterValue value, XDataValue got) => value.StorageType switch
    {
        ParameterStorageType.Double => got.Code == 1040 && BitConverter.DoubleToInt64Bits(double.Parse(got.Value, Inv)) == BitConverter.DoubleToInt64Bits(value.AsDouble()),
        ParameterStorageType.Int or ParameterStorageType.Bool => got.Code == 1070 && int.Parse(got.Value, Inv) == value.AsInt(),
        _ => got.Code == 1000 && got.Value == (value.AsString()!.Length <= 250 ? value.AsString() : value.AsString()![..250])
    };

    /// <summary>Строка DXF: \U+XXXX → символ.</summary>
    private static string DecodeDxf(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 7 <= s.Length && s[i + 1] == 'U' && s[i + 2] == '+' &&
                int.TryParse(s.AsSpan(i + 3, 4), System.Globalization.NumberStyles.HexNumber, Inv, out int c))
            {
                sb.Append((char)c);
                i += 6;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
#endif
