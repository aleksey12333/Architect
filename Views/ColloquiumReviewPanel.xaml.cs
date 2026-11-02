using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AIArchitectStudio.Views;

/// <summary>
/// Лист согласования правок локального аудитора: карточки <see cref="ReviewFixRow"/> с отметками; клик по карточке — элемент
/// выделяется и камера фокусируется на узле; «Принять выбранные правки» — одним шагом Undo.
/// </summary>
public sealed partial class ColloquiumReviewPanel : UserControl
{
    public ColloquiumReviewPanel()
    {
        InitializeComponent();
    }

    // Внутренние: x:Bind их видит (код привязок — часть класса), а генератору метаданных XAML они не нужны.
    internal MainViewModel ViewModel { get; private set; } = null!;

    internal LocalizationService L => ViewModel.Loc;

    public void Initialize(MainViewModel vm)
    {
        ViewModel = vm;
        Bindings.Update();
    }

    private void OnFixTapped(object sender, TappedRoutedEventArgs e)
    {
        // Отметка «принять» не фокусирует камеру — только клик по самой карточке.
        for (var d = e.OriginalSource as DependencyObject; d is not null && !ReferenceEquals(d, sender); d = VisualTreeHelper.GetParent(d))
            if (d is CheckBox or Button) return;
        if (sender is FrameworkElement { Tag: ReviewFixRow row }) ViewModel.FocusReviewFix(row);
    }

    private void OnFocusClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ReviewFixRow row }) ViewModel.FocusReviewFix(row);
    }
}
