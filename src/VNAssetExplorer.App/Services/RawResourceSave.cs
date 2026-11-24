using System.IO;

namespace VNAssetExplorer.App.Services;

public sealed record RawResourceSaveTarget(string FullPath, bool AllowOverwrite);

public interface IRawResourceSaveDialog
{
    RawResourceSaveTarget? PickSaveTarget(string suggestedFileName);
}

public sealed class RawResourceSaveDialog : IRawResourceSaveDialog
{
    public RawResourceSaveTarget? PickSaveTarget(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存原样资源",
            Filter = "原始资源 (*.*)|*.*",
            AddExtension = false,
            CheckPathExists = true,
            OverwritePrompt = true,
            FileName = Path.GetFileName(suggestedFileName),
        };
        if (dialog.ShowDialog() != true) return null;
        return new RawResourceSaveTarget(dialog.FileName, File.Exists(dialog.FileName));
    }
}

public interface IRawResourceWriter
{
    Task WriteAsync(string destinationPath, Func<Stream, CancellationToken, Task> copyResource, bool allowOverwrite, CancellationToken cancellationToken);
}

public sealed class AtomicRawResourceWriter : IRawResourceWriter
{
    private readonly Func<string, string, bool, CancellationToken, Task> commit;

    public AtomicRawResourceWriter()
        : this(static (temporaryPath, destinationPath, overwrite, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite);
            return Task.CompletedTask;
        })
    {
    }

    internal AtomicRawResourceWriter(Func<string, string, bool, CancellationToken, Task> commit) => this.commit = commit;

    public async Task WriteAsync(string destinationPath, Func<Stream, CancellationToken, Task> copyResource, bool allowOverwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(copyResource);
        ValidateDestination(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath)!;
        var temporaryPath = Path.Combine(directory, ".vnasset-raw-" + Path.GetRandomFileName() + ".tmp");
        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await copyResource(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
            ValidateCommitTarget(destinationPath, allowOverwrite);
            cancellationToken.ThrowIfCancellationRequested();
            await commit(temporaryPath, destinationPath, allowOverwrite, cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            if (!committed) TryDeleteTemporary(temporaryPath);
        }
    }

    private static void ValidateDestination(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath)) throw new ArgumentException("必须使用绝对路径。", nameof(destinationPath));
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
        if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0 || (attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
            throw new IOException("目标不是可安全替换的普通文件。");
        if (!allowOverwrite) throw new IOException("目标文件已存在且未获得覆盖授权。");
    }

    private static void TryDeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
