using System.IO;

namespace VNAssetExplorer.App.Services;

public interface ICompatibleVideoWriter
{
    Task WriteMp4Async(string destinationPath, Func<Stream, CancellationToken, Task> copyPayload, bool allowOverwrite, CancellationToken cancellationToken);
}

public sealed record CompatibleVideoSaveTarget(string FullPath, bool AllowOverwrite);
public interface ICompatibleVideoSaveDialog { CompatibleVideoSaveTarget? PickSaveTarget(string suggestedFileName); }

public sealed class CompatibleVideoSaveDialog : ICompatibleVideoSaveDialog
{
    public CompatibleVideoSaveTarget? PickSaveTarget(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "保存兼容 MP4 视频", Filter = "MP4 视频 (*.mp4)|*.mp4", DefaultExt = ".mp4", AddExtension = true, CheckPathExists = true, OverwritePrompt = true, FileName = suggestedFileName };
        return dialog.ShowDialog() == true ? new CompatibleVideoSaveTarget(dialog.FileName, File.Exists(dialog.FileName)) : null;
    }
}

public sealed class AtomicCompatibleVideoWriter : ICompatibleVideoWriter
{
    internal Task<VNAssetExplorer.Application.BulkWriteOutcome> WriteMp4Async(
        BulkAtomicFileTransaction transaction, Func<Stream, CancellationToken, Task> copyPayload, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(copyPayload);
        return transaction.WriteAsync((stream, cancellation, beforeFlush) => WritePayloadAsync(stream, copyPayload, cancellation, beforeFlush), 80 * 1024, token);
    }

    private static async Task WritePayloadAsync(Stream destination, Func<Stream, CancellationToken, Task> copyPayload, CancellationToken token, Action? beforeFlush = null)
    {
        await copyPayload(destination, token).ConfigureAwait(false);
        beforeFlush?.Invoke();
        await destination.FlushAsync(token).ConfigureAwait(false);
        ((FileStream)destination).Flush(flushToDisk: true);
    }

    public async Task WriteMp4Async(string destinationPath, Func<Stream, CancellationToken, Task> copyPayload, bool allowOverwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(copyPayload);
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath) || !string.Equals(Path.GetExtension(destinationPath), ".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("目标必须是绝对 MP4 路径。", nameof(destinationPath));
        var directory = Path.GetDirectoryName(destinationPath);
        if (directory is null || !Directory.Exists(directory)) throw new DirectoryNotFoundException("目标目录不存在。");
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不接受重解析目标目录或其祖先。");
        var temporaryPath = Path.Combine(directory, ".vnasset-video-" + Path.GetRandomFileName() + ".tmp");
        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 80 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await WritePayloadAsync(destination, copyPayload, cancellationToken).ConfigureAwait(false);
            }
            for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse target directories are not allowed at commit.");
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                var attributes = File.GetAttributes(destinationPath);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.System)) != 0 || !allowOverwrite) throw new IOException("目标不是可安全替换的普通文件。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, allowOverwrite);
            committed = true;
        }
        finally { if (!committed) try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { } }
    }
}
