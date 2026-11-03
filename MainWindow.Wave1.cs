#if DEBUG
using Microsoft.UI.Xaml;

namespace AIArchitectStudio;

/// <summary>Только Debug: элементы компоновки для автотеста волны 1 (боковой док Коллоквиума, кнопка «Начальный экран», веб-панель).</summary>
public sealed partial class MainWindow
{
    internal FrameworkElement LayoutRoot => RootGrid;
    internal FrameworkElement ViewportElement => Viewport;
    internal FrameworkElement CouncilDockElement => CouncilDock;
    internal FrameworkElement CouncilDockExpandedElement => CouncilDockExpanded;
    internal FrameworkElement CouncilDockStripElement => CouncilDockStrip;
    internal FrameworkElement ReviewPanelElement => ReviewPanel;
    internal FrameworkElement StartScreenButtonElement => StartScreenButton;
    internal FrameworkElement TopLeftToolsElement => TopLeftTools;
    internal bool IsWebBrowserStarted => WebBridgePanel.IsBrowserStarted;
}
#endif
