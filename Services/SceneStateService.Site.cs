using AIArchitectStudio.Models.Gpzu;

namespace AIArchitectStudio.Services;

/// <summary>
/// Волна 5: участок ГПЗУ документа — граница, охранные зоны и коридоры сетей, существующие ОКС, потолок высоты ПАТ.
/// План неизменяемый; смена — шаг журнала (снимок состояния документа хранит ссылку), Undo/Redo возвращает прежний план.
/// В расчёт, смету и экспорт элементов не входит — это подоснова вьюпорта и проверки посадки.
/// </summary>
public sealed partial class SceneStateService
{
    public GpzuSitePlan? SitePlan { get; private set; }

    /// <summary>Участок появился, сменился или убран (вьюпорт перестраивает подоснову, студия и проверка — свои панели).</summary>
    public event EventHandler? SitePlanChanged;

    private void SetSitePlan(GpzuSitePlan? plan)
    {
        if (ReferenceEquals(SitePlan, plan)) return;
        SitePlan = plan;
        SitePlanChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ставит (или убирает при null) участок ГПЗУ одним шагом журнала с именем name.</summary>
    public void ApplySitePlan(GpzuSitePlan? plan, string name = "Участок ГПЗУ и охранные зоны")
    {
        if (ReferenceEquals(SitePlan, plan)) return;
        using var transaction = BeginUndoTransaction(name);
        SetSitePlan(plan);
        IsDirty = true;
    }
}
