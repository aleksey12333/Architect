using AIArchitectStudio.Models.Layouts;

namespace AIArchitectStudio.Services;

/// <summary>
/// Волна 7: поэтажная планировка здания (Раздел «3. Планировки и ИОС»). План неизменяемый; смена — шаг журнала (снимок состояния
/// документа хранит ссылку, как у участка ГПЗУ), Undo/Redo возвращает прежний план. В расчёт, смету и экспорт элементов не входит.
/// </summary>
public sealed partial class SceneStateService
{
    public BuildingLayoutPlan? BuildingLayout { get; private set; }

    /// <summary>Планировка появилась, сменилась (правка пакета, согласование стояков, Undo/Redo) или убрана.</summary>
    public event EventHandler? BuildingLayoutChanged;

    private void SetBuildingLayout(BuildingLayoutPlan? plan)
    {
        if (ReferenceEquals(BuildingLayout, plan)) return;
        BuildingLayout = plan;
        BuildingLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ставит (или убирает при null) планировку здания ровно одним шагом журнала с именем name.</summary>
    public void ApplyBuildingLayout(BuildingLayoutPlan? plan, string name = "Планировки: пакеты этажей")
    {
        if (ReferenceEquals(BuildingLayout, plan)) return;
        using var transaction = BeginUndoTransaction(name);
        SetBuildingLayout(plan);
        IsDirty = true;
    }
}
