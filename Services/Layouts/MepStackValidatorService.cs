using System.Globalization;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Models.Layouts;

namespace AIArchitectStudio.Services.Layouts;

public enum StackSeverity { Ok, Warning, Critical }

/// <summary>Замечание вертикальной проверки: код, суть, норма, пакеты (нижний / верхний), помещения и точка плана для подсветки.</summary>
public sealed record StackFinding(StackSeverity Severity, string Code, string Title, string Detail, string Norm, string? LowerPackageId, string? UpperPackageId,
    IReadOnlyList<string> RoomIds, double X, double Z);

/// <summary>Итог проверки стояков, мокрых и газовых зон.</summary>
public sealed class StackReport
{
    public List<StackFinding> Findings { get; } = [];
    public int Critical => Findings.Count(f => f.Severity == StackSeverity.Critical);
    public bool Passed => Critical == 0;
    public int PairsChecked { get; set; }
    public int RisersChecked { get; set; }
    public int WetConflicts => Findings.Count(f => f.Code.StartsWith("MEP-WET", StringComparison.Ordinal) || f.Code == "MEP-KITCHEN");
    public int GasConflicts => Findings.Count(f => f.Code.StartsWith("MEP-GAS", StringComparison.Ordinal));
    public int RiserBreaks => Findings.Count(f => f.Code is "MEP-RISER-BREAK" or "MEP-RISER-MAIN");
}

/// <summary>
/// Волна 7 — проверка вертикальной совместимости планировок и инженерных стояков (СП 54.13330, ПУЭ, СП 30.13330, СП 62.13330,
/// СП 402.1325800): на границе каждой пары пакетов (верхний этаж нижнего — нижний этаж верхнего, и подвал — 1-й этаж):
/// санузлы и ванные не над жилыми комнатами и кухнями (СП 54.13330.2016, п. 9.22), кухни не над жилыми комнатами (там же; допускается
/// только в двухуровневых квартирах), мокрые зоны не над коридорами МОП, электрощитовая не под мокрыми зонами (ПУЭ, п. 7.1.29);
/// газ: не в подвал, не над/под жилыми комнатами и коридорами, газоснабжение квартир в домах выше 10 этажей не предусматривается;
/// стояки В1/Т3/К1/Т1/ЭС верхних пакетов без разрывов спускаются через все нижние пакеты к магистрали подвала.
/// <see cref="Align"/> выравнивает несовпадающие мокрые зоны и стояки (новый план; в документ — одним шагом журнала).
/// </summary>
public static class MepStackValidatorService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    public const double Tolerance = 0.05;
    public const int GasMaxLevels = 10;

    public static StackReport Validate(BuildingLayoutPlan plan)
    {
        var report = new StackReport();
        var floors = plan.Floors.ToList();
        var pairs = new List<(FloorPackagePlan Lower, FloorPackagePlan Upper)>();
        if (plan.Basement is { } b && floors.Count > 0) pairs.Add((b, floors[0]));
        for (int i = 1; i < floors.Count; i++) pairs.Add((floors[i - 1], floors[i]));
        foreach (var (lower, upper) in pairs)
        {
            report.PairsChecked++;
            CheckPair(lower, upper, report);
        }

        // Газ: подвал, этажность, соседство с жилыми помещениями (проверено в CheckPair).
        bool anyGas = plan.Packages.Any(p => p.Rooms.Any(r => LayoutRooms.IsGas(r.Kind)) || p.Risers.Any(r => r.Kind == RiserKind.Gas));
        if (plan.Basement is { } bas && (bas.Rooms.Any(r => LayoutRooms.IsGas(r.Kind)) || plan.Inlets.Any(i => i.Kind == GpzuZoneKind.GasPipeline && i.IntoBasement)))
            report.Findings.Add(new StackFinding(StackSeverity.Critical, "MEP-GAS-BASEMENT", "Газопровод введён в подвал",
                "Ввод газопровода и газоиспользующее оборудование в подвале не допускаются — ввод на 1-й этаж в помещение с окном.",
                "СП 62.13330.2011, п. 5.2.1", bas.Id, null, [], plan.X0, plan.Z0));
        if (anyGas && plan.Levels > GasMaxLevels)
            report.Findings.Add(new StackFinding(StackSeverity.Critical, "MEP-GAS-HIGHRISE", string.Create(Ru, $"Газ в квартирах здания {plan.Levels} эт."),
                string.Create(Ru, $"Здание выше {GasMaxLevels} этажей: газоснабжение квартир не предусматривается — кухни с электроплитами, стояки Г убираются."),
                "СП 402.1325800.2018; задание на проектирование", null, null, [], 0, 0));

        // Стояки: каждый стояк верхнего пакета продолжается в пакете под ним (тот же вид, точка ± 5 см) — по цепочке до 1-го этажа;
        // замечание — на узел стояков квартиры (В1, Т3, К1 одной шахты) на каждой границе пакетов.
        for (int i = floors.Count - 1; i >= 1; i--)
        {
            var upper = floors[i];
            var below = floors[i - 1];
            var missing = new List<RiserStack>();
            foreach (var r in upper.Risers)
            {
                report.RisersChecked++;
                if (!below.Risers.Any(o => o.Kind == r.Kind && Math.Abs(o.X - r.X) <= Tolerance && Math.Abs(o.Z - r.Z) <= Tolerance)) missing.Add(r);
            }
            foreach (var group in missing.GroupBy(r => r.ApartmentId ?? r.Id))
            {
                var r = group.First();
                var room = below.Rooms.FirstOrDefault(x => x.Contains(r.X, r.Z));
                string kinds = string.Join(", ", group.Select(x => LayoutRooms.RiserTitle(x.Kind)));
                string apt = r.ApartmentId?.Split(':').Last() is { } a ? $" (кв. {a})" : string.Empty;
                report.Findings.Add(new StackFinding(StackSeverity.Critical, "MEP-RISER-BREAK",
                    string.Create(Ru, $"Разрыв стояков: {upper.FromLevel}-й этаж → {below.ToLevel}-й этаж"),
                    string.Create(Ru, $"{kinds}{apt} в точке ({r.X:0.00}; {r.Z:0.00}) м пакета «{upper.RangeText}» не продолжаются в пакете «{below.RangeText}»") +
                    (room is null ? "." : $": под ними — «{room.Name}»."),
                    "СП 30.13330.2020; СП 60.13330.2020", below.Id, upper.Id, room is null ? [] : [room.Id], r.X, r.Z));
            }
        }
        if (floors.Count > 0 && plan.Basement is not null)
        {
            foreach (var r in floors[0].Risers.Where(r => r.Kind != RiserKind.Gas))
            {
                report.RisersChecked++;
                bool fed = plan.Mains.Any(m => m.Kind == r.Kind && m.Path.Count > 0 && Math.Abs(m.Path[^1].X - r.X) <= Tolerance && Math.Abs(m.Path[^1].Z - r.Z) <= Tolerance);
                if (!fed)
                    report.Findings.Add(new StackFinding(StackSeverity.Critical, "MEP-RISER-MAIN",
                        string.Create(Ru, $"Стояк {LayoutRooms.RiserTitle(r.Kind)} не подключён к магистрали подвала"),
                        string.Create(Ru, $"Низ стояка ({r.X:0.00}; {r.Z:0.00}) м — без магистрали от {SourceOf(r.Kind)}."),
                        "СП 30.13330.2020", plan.Basement.Id, floors[0].Id, [], r.X, r.Z));
            }
        }

        // Итоги «без замечаний» — для сводки панели.
        if (report.WetConflicts == 0)
            report.Findings.Add(new StackFinding(StackSeverity.Ok, "MEP-WET-OK", "Мокрые зоны соосны по вертикали",
                string.Create(Ru, $"Проверено границ пакетов: {report.PairsChecked}; санузлы и кухни — над санузлами и кухнями, электрощитовая — не под мокрыми зонами."),
                "СП 54.13330.2016, п. 9.22; ПУЭ, п. 7.1.29", null, null, [], 0, 0));
        if (report.RiserBreaks == 0)
            report.Findings.Add(new StackFinding(StackSeverity.Ok, "MEP-RISER-OK", "Стояки сквозные до подвала",
                string.Create(Ru, $"Стояков проверено: {report.RisersChecked}; все подключены к магистралям подвала."), "СП 30.13330.2020", null, null, [], 0, 0));
        if (report.GasConflicts == 0)
            report.Findings.Add(new StackFinding(StackSeverity.Ok, "MEP-GAS-OK", anyGas ? "Газ — по правилам размещения" : "Газоснабжение квартир не предусмотрено",
                anyGas ? "Газ не в подвале, газовые кухни — над газовыми кухнями." : "Кухни с электроплитами.", "СП 62.13330.2011; СП 402.1325800.2018", null, null, [], 0, 0));
        return report;
    }

    private static string SourceOf(RiserKind k) => k switch
    {
        RiserKind.ColdWater => "водомерного узла",
        RiserKind.HotWater or RiserKind.Heating => "ИТП",
        RiserKind.Power => "ВРУ",
        _ => "выпуска канализации"
    };

    private static string FloorOf(FloorPackagePlan p, bool top) => p.IsBasement ? "подвала" : string.Create(Ru, $"{(top ? p.ToLevel : p.FromLevel)}-го этажа");

    /// <summary>
    /// Граница пакетов: по каждому помещению верхнего этажа — одно замечание (наибольшее перекрытие запрещённого помещения нижнего),
    /// «Кухня 6-го этажа над жилой комнатой 5-го этажа».
    /// </summary>
    private static void CheckPair(FloorPackagePlan lower, FloorPackagePlan upper, StackReport report)
    {
        foreach (var u in upper.Rooms)
        {
            bool sanitary = LayoutRooms.IsSanitary(u.Kind), kitchen = LayoutRooms.IsKitchen(u.Kind), gas = LayoutRooms.IsGas(u.Kind);
            if (!sanitary && !kitchen && !LayoutRooms.IsDwelling(u.Kind) && u.Kind != LayoutRoomKind.Corridor) continue;
            (string Code, string UpperLabel, string LowerLabel, string Norm, LayoutRoom Lower, double Overlap)? worst = null;
            foreach (var l in lower.Rooms)
            {
                double overlap = u.OverlapArea(l);
                if (overlap < 0.05) continue;
                (string Code, string Up, string Low, string Norm)? hit = null;
                if (sanitary && (LayoutRooms.IsDwelling(l.Kind) || LayoutRooms.IsKitchen(l.Kind)))
                    hit = ("MEP-WET-LIVING", u.Name, LayoutRooms.IsKitchen(l.Kind) ? "кухней" : "жилой комнатой", "СП 54.13330.2016, п. 9.22");
                else if (kitchen && LayoutRooms.IsDwelling(l.Kind))
                    hit = (gas ? "MEP-GAS-LIVING" : "MEP-KITCHEN", gas ? "Газовая кухня" : "Кухня", "жилой комнатой", gas ? "СП 54.13330.2016, п. 9.22; СП 402.1325800.2018" : "СП 54.13330.2016, п. 9.22");
                else if ((sanitary || kitchen) && l.Kind == LayoutRoomKind.Corridor)
                    hit = (gas ? "MEP-GAS-CORRIDOR" : "MEP-WET-CORRIDOR", gas ? "Газовая кухня" : "Мокрая зона", "коридором МОП", "СП 54.13330.2016, п. 9.22; СП 30.13330.2020");
                else if ((sanitary || kitchen) && l.Kind == LayoutRoomKind.Electrical)
                    hit = ("MEP-WET-ELECTRICAL", "Мокрая зона", "электрощитовой", "ПУЭ, п. 7.1.29");
                else if ((LayoutRooms.IsDwelling(u.Kind) || u.Kind == LayoutRoomKind.Corridor) && LayoutRooms.IsGas(l.Kind))
                    hit = ("MEP-GAS-UNDER", u.Kind == LayoutRoomKind.Corridor ? "Коридор МОП" : "Жилая комната", "газовой кухней", "СП 402.1325800.2018");
                if (hit is { } h && (worst is null || overlap > worst.Value.Overlap)) worst = (h.Code, h.Up, h.Low, h.Norm, l, overlap);
            }
            if (worst is not { } w) continue;
            var lr = w.Lower;
            report.Findings.Add(new StackFinding(StackSeverity.Critical, w.Code, string.Create(Ru, $"{w.UpperLabel} {FloorOf(upper, false)} над {w.LowerLabel} {FloorOf(lower, true)}"),
                string.Create(Ru, $"«{u.Name}» (кв. {u.ApartmentId?.Split(':').Last() ?? "—"}) перекрывает «{lr.Name}» на {w.Overlap:0.00} м²."),
                w.Norm, lower.Id, upper.Id, [u.Id, lr.Id], (Math.Max(u.X0, lr.X0) + Math.Min(u.X1, lr.X1)) / 2, (Math.Max(u.Z0, lr.Z0) + Math.Min(u.Z1, lr.Z1)) / 2));
        }
    }

    /// <summary>
    /// «Согласовать стояки и мокрые зоны»: снизу вверх каждая квартира пакета ставит мокрый блок к той стене, где он не попадает на
    /// жилые комнаты, кухни, коридоры и электрощитовую нижнего пакета и совпадает с его стояками; дом выше 10 этажей — без газа;
    /// газ из подвала — на 1-й этаж; электрощитовая — из-под мокрых зон; магистрали подвала — к низу каждого стояка.
    /// Возвращает новый план и число исправлений (0 — план прежний).
    /// </summary>
    public static (BuildingLayoutPlan Plan, int Changes) Align(BuildingLayoutPlan plan)
    {
        int changes = 0;
        var packages = plan.Packages.ToList();
        bool stripGas = plan.Levels > GasMaxLevels && packages.Any(p => p.Rooms.Any(r => LayoutRooms.IsGas(r.Kind)) || p.Risers.Any(r => r.Kind == RiserKind.Gas));
        if (stripGas)
        {
            for (int i = 0; i < packages.Count; i++)
            {
                var p = packages[i];
                packages[i] = p with
                {
                    Rooms = p.Rooms.Select(r => r.Kind == LayoutRoomKind.KitchenGas ? r with { Kind = LayoutRoomKind.Kitchen } : r).ToList(),
                    Risers = p.Risers.Where(r => r.Kind != RiserKind.Gas).ToList()
                };
            }
            changes++;
        }
        var floorIdx = packages.Select((p, i) => (p, i)).Where(t => !t.p.IsBasement).OrderBy(t => t.p.FromLevel).Select(t => t.i).ToList();
        for (int n = 1; n < floorIdx.Count; n++)
        {
            var lower = packages[floorIdx[n - 1]];
            var upper = packages[floorIdx[n]];
            var rooms = upper.Rooms.ToList();
            var risers = upper.Risers.ToList();
            var apartments = upper.Apartments.ToList();
            bool changed = false;
            for (int a = 0; a < apartments.Count; a++)
            {
                var apt = apartments[a];
                bool gas = rooms.Any(x => x.ApartmentId == apt.Id && x.Kind == LayoutRoomKind.KitchenGas);
                var current = (Rooms: rooms.Where(x => x.ApartmentId == apt.Id).ToList(), Risers: risers.Where(x => x.ApartmentId == apt.Id).ToList());
                var flipped = DeveloperLayoutGenerator.BuildApartment(apt with { WetAtHighX = !apt.WetAtHighX }, gas, plan.Mix.CorridorWidth);
                double keep = Penalty(current.Rooms, current.Risers, lower), flip = Penalty(flipped.Rooms, flipped.Risers, lower);
                if (flip + 1e-9 >= keep) continue;
                rooms.RemoveAll(x => x.ApartmentId == apt.Id);
                risers.RemoveAll(x => x.ApartmentId == apt.Id);
                rooms.AddRange(flipped.Rooms);
                risers.AddRange(flipped.Risers);
                apartments[a] = flipped.Apartment;
                changed = true;
                changes++;
            }
            if (changed) packages[floorIdx[n]] = upper with { Rooms = rooms, Risers = risers, Apartments = apartments };
        }
        var next = plan.With(packages: packages, gas: stripGas ? false : plan.GasSupply);
        // Электрощитовая подвала — из-под мокрых зон 1-го этажа.
        if (next.Basement is { } basement && next.Floors.FirstOrDefault() is { } first &&
            basement.Rooms.FirstOrDefault(r => r.Kind == LayoutRoomKind.Electrical) is { } vru && first.Rooms.Any(u => LayoutRooms.IsWet(u.Kind) && u.OverlapArea(vru) >= 0.05))
        {
            var rebuilt = DeveloperLayoutGenerator.BuildBasement(next, first);
            next = next.With(packages: next.Packages.Select(p => p.IsBasement ? rebuilt : p).ToList());
            changes++;
        }
        var inlets = next.Inlets.Select(i => i.Kind == GpzuZoneKind.GasPipeline && i.IntoBasement ? i with { IntoBasement = false } : i)
            .Where(i => !(stripGas && i.Kind == GpzuZoneKind.GasPipeline)).ToList();
        // Узлы ввода — в пределах помещений пересобранного подвала (ВРУ могла переехать).
        if (next.Basement is { } nb)
            inlets = inlets.Select(i => i.IntoBasement && nb.Room(i.TargetRoomId) is { } t && t.Kind != LayoutRoomKind.Corridor && (i.X < t.X0 + 0.3 || i.X > t.X1 - 0.3)
                ? i with { X = Math.Round(Math.Clamp(t.CenterX, t.X0 + 0.3, t.X1 - 0.3), 3) } : i).ToList();
        if (inlets.Count != next.Inlets.Count || inlets.Where((t, k) => !Equals(t, next.Inlets[k])).Any()) changes++;
        next = next.With(inlets: inlets);
        var mains = DeveloperLayoutGenerator.BuildMains(next);
        bool mainsChanged = mains.Count != plan.Mains.Count || mains.Zip(plan.Mains).Any(t => t.First.Kind != t.Second.Kind || !t.First.Path.SequenceEqual(t.Second.Path));
        if (mainsChanged) changes++;
        next = next.With(mains: mains);
        return changes == 0 ? (plan, 0) : (next, changes);
    }

    /// <summary>Штраф размещения мокрого блока квартиры над нижним пакетом: конфликты × 1000 − совпавшие стояки.</summary>
    private static double Penalty(IReadOnlyList<LayoutRoom> rooms, IReadOnlyList<RiserStack> risers, FloorPackagePlan lower)
    {
        double p = 0;
        foreach (var u in rooms)
        {
            bool sanitary = LayoutRooms.IsSanitary(u.Kind), kitchen = LayoutRooms.IsKitchen(u.Kind);
            if (!sanitary && !kitchen) continue;
            foreach (var l in lower.Rooms)
            {
                double o = u.OverlapArea(l);
                if (o < 0.05) continue;
                if (LayoutRooms.IsDwelling(l.Kind) || l.Kind is LayoutRoomKind.Corridor or LayoutRoomKind.Electrical || sanitary && LayoutRooms.IsKitchen(l.Kind)) p += 1000 * o;
            }
        }
        foreach (var r in risers)
            if (!lower.Risers.Any(o => o.Kind == r.Kind && Math.Abs(o.X - r.X) <= Tolerance && Math.Abs(o.Z - r.Z) <= Tolerance)) p += 10;
        return p;
    }
}
