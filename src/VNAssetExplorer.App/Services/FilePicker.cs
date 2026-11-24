using Microsoft.Win32;

namespace VNAssetExplorer.App.Services;

public sealed class FilePicker : IFilePicker
{
    public string? PickHfaFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开 HFA 归档",
            Filter = "HFA 归档 (*.hfa)|*.hfa|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
