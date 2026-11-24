using System.IO;

namespace VNAssetExplorer.App.Services;

public interface ICompatibleAudioWriter
{
    Task WriteOggAsync(string destinationPath, Func<Stream, CancellationToken, Task> copyPayload, bool allowOverwrite, CancellationToken cancellationToken);
}

public sealed record CompatibleAudioSaveTarget(string FullPath, bool AllowOverwrite);

public interface ICompatibleAudioSaveDialog
{
    CompatibleAudioSaveTarget? PickSaveTarget(string suggestedFileName);
}

public sealed class CompatibleAudioSaveDialog : ICompatibleAudioSaveDialog
{
    public CompatibleAudioSaveTarget? PickSaveTarget(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存兼容 Ogg 音频",
            Filter = "Ogg Vorbis 音频 (*.ogg)|*.ogg",
            DefaultExt = ".ogg",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = true,
            FileName = suggestedFileName,
        };
        if (dialog.ShowDialog() != true) return null;
        return new CompatibleAudioSaveTarget(dialog.FileName, File.Exists(dialog.FileName));
    }
}

public sealed class AtomicCompatibleAudioWriter : ICompatibleAudioWriter
{
    internal Task<VNAssetExplorer.Application.BulkWriteOutcome> WriteOggAsync(
        BulkAtomicFileTransaction transaction, Func<Stream, CancellationToken, Task> copyPayload, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(copyPayload);
        return transaction.WriteAsync((stream, cancellation, beforeFlush) => WritePayloadAsync(stream, copyPayload, cancellation, beforeFlush), 64 * 1024, token);
    }

    private static async Task WritePayloadAsync(Stream destination, Func<Stream, CancellationToken, Task> copyPayload, CancellationToken token, Action? beforeFlush = null)
    {
        await copyPayload(destination, token).ConfigureAwait(false);
        beforeFlush?.Invoke();
        await destination.FlushAsync(token).ConfigureAwait(false);
        ((FileStream)destination).Flush(flushToDisk: true);
    }

    public async Task WriteOggAsync(string destinationPath, Func<Stream, CancellationToken, Task> copyPayload, bool allowOverwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(copyPayload);
        ValidateDestination(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath)!;
        var temporaryPath = Path.Combine(directory, ".vnasset-audio-" + Path.GetRandomFileName() + ".tmp");
        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await WritePayloadAsync(destination, copyPayload, cancellationToken).ConfigureAwait(false);
            }
            ValidateDestination(destinationPath);
            ValidateCommitTarget(destinationPath, allowOverwrite);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, allowOverwrite);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    private static void ValidateDestination(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath)) throw new ArgumentException("必须使用绝对路径。", nameof(destinationPath));
        if (!string.Equals(Path.GetExtension(destinationPath), ".ogg", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("目标必须是 Ogg 文件。", nameof(destinationPath));
        var directory = Path.GetDirectoryName(destinationPath);
        if (directory is null || !Directory.Exists(directory)) throw new DirectoryNotFoundException("目标目录不存在。");
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不接受重解析目标目录或其祖先。");
        }
    }

    private static void ValidateCommitTarget(string destinationPath, bool allowOverwrite)
    {
        if (!File.Exists(destinationPath) && !Directory.Exists(destinationPath)) return;
        var attributes = File.GetAttributes(destinationPath);
        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
        {
            throw new IOException("目标不是可安全替换的普通文件。");
        }
        if (!allowOverwrite) throw new IOException("目标文件已存在且未获得覆盖授权。");
    }
}
