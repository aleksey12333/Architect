using System.Globalization;
using System.Text;
using AIArchitectStudio.Models.Gpzu;

namespace AIArchitectStudio.Services.Gpzu;

/// <summary>Текстовая сводка разбора ГПЗУ и топосъёмки (отчёт автотеста, копирование из студии, промпт экспертизы).</summary>
public static class GpzuReport
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Describe(GpzuDocument doc, TopoSurvey? topo = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ГПЗУ № {doc.Number} от {doc.IssueDate}");
        sb.AppendLine($"Заявитель: {doc.Applicant} (заявление {doc.Application})");
        sb.AppendLine($"Адрес: {doc.Address}");
        sb.AppendLine(string.Create(Ru, $"Кадастровый номер: {doc.Cadastral}; площадь по документу {doc.DeclaredArea:N0} м², по Гауссу {doc.GaussArea:N2} м² (Δ {doc.GaussArea - doc.DeclaredArea:+0.00;-0.00} м²)"));
        var r = doc.Regulation;
        sb.AppendLine($"Территориальная зона: {r.ZoneCode} {r.ZoneName}, подзона {r.Subzone}");
        sb.AppendLine(string.Create(Ru, $"Регламент: жилых помещений не более {r.MaxLivingArea:N0} м², машино-мест не менее {r.MinParkingPlaces}, мин. площадь участка {r.MinParcelArea:N0} м²"));
        if (r.PermittedUses.Count > 0) sb.AppendLine("Основные ВРИ: " + string.Join("; ", r.PermittedUses));
        sb.AppendLine($"Характерных точек границы: {doc.Boundary.Count} ({doc.CoordinateSystem})");
        foreach (var p in doc.Boundary) sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {p.Number,3}  X {p.X,11:0.00}  Y {p.Y,12:0.00}"));
        sb.AppendLine($"Таблиц координат: {doc.Tables.Count}");
        foreach (var t in doc.Tables)
            sb.AppendLine($"  стр. {t.FirstPage}{(t.LastPage != t.FirstPage ? "–" + t.LastPage : "")}: {t.PointCount} т.{(t.SameAsBoundary ? ", = граница" : "")}{(t.HadMergedCells ? ", склеенные ячейки" : "")} — {t.Title}");
        sb.AppendLine($"Зон: {doc.Zones.Count}");
        foreach (var z in doc.Zones)
            sb.AppendLine($"  [{z.Kind}] {(z.ExplicationNo > 0 ? "п. " + z.ExplicationNo + " " : "")}{z.Name} {z.HeightText(Ru)} — точек {z.Polygon.Count}, линий {z.Lines.Count}{(z.CoversWholeParcel ? ", весь участок" : "")}; {z.Source}");
        sb.AppendLine($"ОКС: {doc.Objects.Count} — " + string.Join(", ", doc.Objects.Select(o => "№ " + o.Number + (o.Cadastral.Length > 0 ? " (" + o.Cadastral + ")" : "") + (o.Footprint.Count > 0 ? " контур" : ""))));
        if (doc.GroundElevation is { } g) sb.AppendLine(string.Create(Ru, $"Отметка земли: {g:0.00} м БС; потолок {doc.CeilingAbs:0.00} м БС → допустимая высота {doc.CeilingAbs - g:0.0} м"));
        foreach (var w in doc.Warnings) sb.AppendLine("  ! " + w);
        if (topo is not null)
        {
            sb.AppendLine($"Топосъёмка: {Path.GetFileName(topo.SourceFile)} {topo.Version}, единицы {topo.Units}, примитивов {topo.EntityCount} (развёрнуто {topo.FlattenedCount}), {topo.ElapsedMs:0} мс");
            foreach (var g2 in topo.Networks.GroupBy(n => n.Kind))
                sb.AppendLine(string.Create(Ru, $"  сеть {g2.Key}: трасс {g2.Count()}, длина {g2.Sum(n => n.Length):N1} м, слои {string.Join(", ", g2.Select(n => n.Layer).Distinct())}"));
            sb.AppendLine($"  зданий {topo.Buildings.Count}, ЗОУИТ кадастрового слоя {topo.CadastralZones.Count} ({string.Join(", ", topo.CadastralZones.Select(z => z.Label).Where(l => l.Length > 0).Distinct())}), участков {topo.Parcels.Count}, ОКС {topo.CapitalContours.Count}, отметок {topo.Elevations.Count}");
            foreach (var w in topo.Warnings) sb.AppendLine("  ! " + w);
        }
        return sb.ToString();
    }
}
