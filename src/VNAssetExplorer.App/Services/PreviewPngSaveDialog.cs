using System.IO;
using Microsoft.Win32;

namespace VNAssetExplorer.App.Services;

public sealed record PreviewPngSaveTarget(string FullPath, bool AllowOverwrite);

public interface IPreviewPngSaveDialog
{
    PreviewPngSaveTarget? PickSaveTarget(string suggestedFileName);
}

public static class PreviewPngFilename
{
    public static string Suggest(string? entryName)
    {
        var leaf = string.IsNullOrWhiteSpace(entryName) ? string.Empty : Path.GetFileName(entryName);
        var stem = Path.GetFileNameWithoutExtension(leaf);
        if (string.IsNullOrWhiteSpace(stem)) return "preview.png";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(stem.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        cleaned = cleaned.TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned) || IsWindowsDeviceName(cleaned)) return "preview.png";
        return cleaned + ".png";
    }

    private static bool IsWindowsDeviceName(string value)
    {
        var stem = value.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3]));
    }
}

public sealed class PreviewPngSaveDialog : IPreviewPngSaveDialog
{
    public PreviewPngSaveTarget? PickSaveTarget(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存当前预览 PNG",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = true,
            FileName = suggestedFileName,
        };

        if (dialog.ShowDialog() != true) return null;
        return new PreviewPngSaveTarget(dialog.FileName, File.Exists(dialog.FileName));
    }
}
