using System.IO;

namespace VNAssetExplorer.App.Services;

public interface IPreviewPngWriter
{
    // Cancellation is honored before commit; once commit succeeds, this method returns normally.
    Task WriteAsync(string destinationPath, ReadOnlyMemory<byte> pngBytes, bool allowOverwrite, CancellationToken cancellationToken);
}

public sealed class AtomicPreviewPngWriter : IPreviewPngWriter
{
    internal Task<VNAssetExplorer.Application.BulkWriteOutcome> WriteAsync(
        BulkAtomicFileTransaction transaction, ReadOnlyMemory<byte> pngBytes, CancellationToken token) =>
        transaction.WriteAsync((stream, cancellation, beforeFlush) => WritePayloadAsync(stream, pngBytes, cancellation, beforeFlush), 64 * 1024, token);

    internal Task<VNAssetExplorer.Application.BulkWriteOutcome> WriteAsync(
        BulkAtomicFileTransaction transaction, Func<CancellationToken, ReadOnlyMemory<byte>> payload, CancellationToken token) =>
        transaction.WriteAsync((stream, cancellation, beforeFlush) => WritePayloadAsync(stream, payload(cancellation), cancellation, beforeFlush), 64 * 1024, token);

    private static async Task WritePayloadAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken token, Action? beforeFlush = null)
    {
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        beforeFlush?.Invoke();
        await stream.FlushAsync(token).ConfigureAwait(false);
        ((FileStream)stream).Flush(flushToDisk: true);
    }

    private readonly Func<string, string, bool, CancellationToken, Task> commit;

    public AtomicPreviewPngWriter()
        : this(static (temporaryPath, destinationPath, overwrite, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite);
            return Task.CompletedTask;
        })
    {
    }

    internal AtomicPreviewPngWriter(Func<string, string, bool, CancellationToken, Task> commit)
    {
        this.commit = commit;
    }

    public async Task WriteAsync(
        string destinationPath,
        ReadOnlyMemory<byte> pngBytes,
        bool allowOverwrite,
        CancellationToken cancellationToken)
    {
        ValidateDestination(destinationPath);

        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("目标目录无效。", nameof(destinationPath));
        var temporaryPath = Path.Combine(directory, ".vnasset-preview-" + Path.GetRandomFileName() + ".tmp");
        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await WritePayloadAsync(stream, pngBytes, cancellationToken).ConfigureAwait(false);
            }

            ValidateDestination(destinationPath);
            ValidateCommitTarget(destinationPath, allowOverwrite);
            cancellationToken.ThrowIfCancellationRequested();
            await commit(temporaryPath, destinationPath, allowOverwrite, cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                TryDeleteTemporary(temporaryPath);
            }
        }
    }

    private static void ValidateDestination(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("必须使用绝对路径。", nameof(destinationPath));
        }

        if (!string.Equals(Path.GetExtension(destinationPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("目标必须是 PNG 文件。", nameof(destinationPath));
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("目标目录不存在。");
        }

        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("不接受重解析目标目录或其祖先。");
            }
            current = current.Parent;
        }
    }

    private static void ValidateCommitTarget(string destinationPath, bool allowOverwrite)
    {
        if (!File.Exists(destinationPath) && !Directory.Exists(destinationPath))
        {
            return;
        }

        var attributes = File.GetAttributes(destinationPath);
        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
        {
            throw new IOException("目标不是可安全替换的普通文件。");
        }

        if (!allowOverwrite)
        {
            throw new IOException("目标文件已存在且未获得覆盖授权。");
        }
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the primary write/commit result; callers report that cleanup was attempted.
        }
    }
}
