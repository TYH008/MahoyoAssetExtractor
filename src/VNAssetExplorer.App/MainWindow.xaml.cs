using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VNAssetExplorer.App.Localization;
using VNAssetExplorer.App.ViewModels;

namespace VNAssetExplorer.App;

public partial class MainWindow : Window
{
    private readonly BulkExtractionViewModel viewModel;
    private readonly Func<MessageBoxResult> closingConfirmation;
    private bool shutdownWaiting;
    private bool finalCloseAllowed;

    public MainWindow() : this(null) { }

    internal MainWindow(BulkExtractionViewModel? viewModel, Func<MessageBoxResult>? confirmation = null,
        LanguagePreference? preference = null)
    {
        InitializeComponent();
        if (viewModel is null)
        {
            preference ??= new LanguagePreference();
            viewModel = new BulkExtractionViewModel(language: preference.Read(), saveLanguage: preference.Save,
                confirmAllImages: ConfirmAllImages);
        }
        this.viewModel = viewModel;
        closingConfirmation = confirmation ?? ConfirmClose;
        DataContext = viewModel;
        SourceInitialized += (_, _) => ApplyThemedTitleBar();
        Closing += MainWindow_OnClosing;
    }

    private AllImagesChoice ConfirmAllImages()
    {
        var text = viewModel.Text;
        var choice = ExtractionChoiceDialog.Show(this, text["ImageWarningTitle"], text["ImageWarningMessage"],
            text["Back"], text["SwitchToGallery"], text["ContinueAllImages"]);
        return choice switch { 1 => AllImagesChoice.Gallery, 2 => AllImagesChoice.Continue, _ => AllImagesChoice.Back };
    }

    private MessageBoxResult ConfirmClose()
    {
        var text = viewModel.Text;
        return ExtractionChoiceDialog.Show(this, text["CloseTitle"], text["CloseMessage"],
            text["Back"], text["CancelAndClose"]) == 1 ? MessageBoxResult.Yes : MessageBoxResult.No;
    }

    private void ApplyThemedTitleBar()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(this).Handle;
        var darkMode = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        // COLORREF stores red in the low byte, unlike the XAML #RRGGBB value.
        var captionColor = 0x00381B10; // #101B38
        var textColor = 0x00F8EDE6; // #E6EDF8
        _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    private async void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (finalCloseAllowed) return;
        e.Cancel = true;
        if (shutdownWaiting) return;
        // Guard before the modal dialog: its nested dispatcher can receive another close.
        shutdownWaiting = true;
        if (viewModel.IsBusy && closingConfirmation() != MessageBoxResult.Yes)
        {
            shutdownWaiting = false;
            return;
        }
        await viewModel.ShutdownAsync();
        finalCloseAllowed = true;
        // Avoid a nested Close() while WPF is still raising the first Closing event.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
