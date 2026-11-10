using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AIArchitectStudio.Views;

/// <summary>
/// Волна 6 в стартовом окне: выбор Среды девелопера (проект откроется в ней; переключатель есть и в верхнем баре редактора),
/// восстановление последней рабочей сессии из автосохранения в 1 клик, «Свернуть меню» — окно сжимается в стеклянную капсулу
/// в правом нижнем углу рабочей области (сцена редактора за ней видна), «Развернуть» возвращает прежний размер и место.
/// </summary>
public sealed partial class WelcomeWindow
{
    private RectInt32? _expandedRect;
    private bool _syncingEnvironment;

    /// <summary>Меню свёрнуто в капсулу.</summary>
    public bool IsCollapsedToCapsule { get; private set; }

    private void InitializeWave6()
    {
        _syncingEnvironment = true;
        (_viewModel.ActiveEnvironment switch
        {
            DeveloperEnvironment.Site => EnvSiteRadio,
            DeveloperEnvironment.Interiors => EnvInteriorsRadio,
            _ => EnvBuildingRadio
        }).IsChecked = true;
        _syncingEnvironment = false;
        if (AutoSaveService.TryReadSession() is { } info)
        {
            RestoreSessionButton.Visibility = Visibility.Visible;
            RestoreSessionText.Text = L.Format("W6RestoreHint", info.ProjectName, info.SavedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), info.Elements) +
                                      (info.HasSite ? " · " + L.Get("W6RestoreWithSite") : string.Empty);
        }
    }

    private void OnEnvironmentChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingEnvironment || sender is not RadioButton { Tag: string code }) return;
        _viewModel.ActiveEnvironment = WorkspaceEnvironmentService.Parse(code, _viewModel.ActiveEnvironment);
    }

    private async void OnRestoreSessionClick(object sender, RoutedEventArgs e) => await CompleteAsync(StartupChoice.Restore);

    private void OnCollapseMenuClick(object sender, RoutedEventArgs e) => CollapseToCapsule();

    private void OnExpandMenuClick(object sender, RoutedEventArgs e) => ExpandFromCapsule();

    /// <summary>Капсула 380 × 58 в правом нижнем углу рабочей области экрана.</summary>
    public void CollapseToCapsule()
    {
        if (IsCollapsedToCapsule) return;
        IsCollapsedToCapsule = true;
        _expandedRect = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        int w = (int)Math.Round(380 * scale), h = (int)Math.Round(58 * scale), margin = (int)Math.Round(20 * scale);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        MainLayer.Visibility = Visibility.Collapsed;
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        WindowButtons.Visibility = Visibility.Collapsed;
        DragArea.Margin = new Thickness(0, 0, 170, 0);
        CapsuleLayer.Visibility = Visibility.Visible;
        CapsuleLayer.Opacity = 0;
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - margin, area.Y + area.Height - h - margin, w, h));
        WindowChrome.Fade(CapsuleLayer, 0, 1, 0.97, 1, 220).Begin();
        // Капсула парит поверх редактора: сцену можно осматривать, меню — развернуть в 1 клик.
        if (AppWindow.Presenter is OverlappedPresenter overlapped) overlapped.IsAlwaysOnTop = true;
        if (App.MainWindowInstance is { } editor) editor.Activate();
    }

    public void ExpandFromCapsule()
    {
        if (!IsCollapsedToCapsule) return;
        IsCollapsedToCapsule = false;
        if (AppWindow.Presenter is OverlappedPresenter overlapped) overlapped.IsAlwaysOnTop = false;
        CapsuleLayer.Visibility = Visibility.Collapsed;
        MainLayer.Visibility = Visibility.Visible;
        MainLayer.Opacity = 1;
        WindowButtons.Visibility = Visibility.Visible;
        DragArea.Margin = new Thickness(0, 0, 128, 0);
        if (_expandedRect is { } r) AppWindow.MoveAndResize(r);
        WindowChrome.Fade(Root, 0.6, 1, 0.985, 1, 240).Begin();
        Activate();
    }
}
