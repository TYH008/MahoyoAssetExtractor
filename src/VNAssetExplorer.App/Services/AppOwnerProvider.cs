using System.Windows;

namespace VNAssetExplorer.App.Services;

internal static class AppOwnerProvider
{
    public static Window? ResolveMainWindow() =>
        System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window is VNAssetExplorer.App.MainWindow) ??
        System.Windows.Application.Current?.MainWindow;
}