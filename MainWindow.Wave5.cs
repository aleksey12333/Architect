using AIArchitectStudio.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace AIArchitectStudio;

/// <summary>Волна 5: вход в студию «Градостроительный план (ГПЗУ) и охранные зоны» (меню «Открыть», событие модели представления).</summary>
public sealed partial class MainWindow
{
    private bool _gpzuOpen;

    private void InitializeWave5() => ViewModel.GpzuStudioRequested += async (_, _) => await ShowGpzuStudioAsync();

    private async void OnOpenGpzuClick(object sender, RoutedEventArgs e) => await ShowGpzuStudioAsync();

    /// <summary>Открывает студию ГПЗУ (одно окно за раз); debug — сценарий автотеста или съёмки после открытия.</summary>
    private async Task<GpzuStudioDialog?> ShowGpzuStudioAsync(Func<GpzuStudioDialog, Task>? debug = null)
    {
        if (_gpzuOpen || AnyDialogOpen) return null;
        _gpzuOpen = true;
        _block34Open = true;
        try
        {
            var dialog = new GpzuStudioDialog(ViewModel, WindowNative.GetWindowHandle(this)) { XamlRoot = Content.XamlRoot, RequestedTheme = DialogTheme };
            if (debug is not null) dialog.Opened += async (_, _) => await debug(dialog);
            await dialog.ShowAsync();
            return dialog;
        }
        finally
        {
            _gpzuOpen = false;
            _block34Open = false;
            ShowPendingConstraints();
        }
    }
}
