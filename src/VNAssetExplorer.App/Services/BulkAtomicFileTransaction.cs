using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

internal sealed class BulkAtomicFileTransaction(BulkOutputDirectoryGuard guard, SafeFileHandle directory, string finalPath, BulkOutputHooks? hooks)
{
    internal async Task<BulkWriteOutcome> WriteAsync(Func<Stream, CancellationToken, Action, Task> write, int bufferSize, CancellationToken token)
    {
        guard.Revalidate();
        var leaf = Path.GetFileName(finalPath);
        if (guard.ExistingFile(directory, leaf)) return BulkWriteOutcome.ConflictSkipped;
        hooks?.At("BeforeTempCreate", finalPath);
        guard.Revalidate();
        var status = BulkOutputDirectoryGuard.OpenRelative(directory, ".vnasset-bulk-" + Guid.NewGuid().ToString("N") + ".tmp", 0x40110000, 0, 2, 0x200060, out var original);
        if (status != 0) { original.Dispose(); throw BulkOutputDirectoryGuard.Error("Create temporary file", status); }
        FileStream? stream = null;
        var committed = false;
        Exception? failure = null;
        try
        {
            // FileStream is the sole owner of the original synchronous native handle.
            stream = new FileStream(original, FileAccess.Write, bufferSize, isAsync: false);
            token.ThrowIfCancellationRequested();
            hooks?.At("BeforeWrite", finalPath);
            await write(stream, token, () =>
            {
                hooks?.At("DuringWrite", finalPath);
                hooks?.At("BeforeFlush", finalPath);
            }).ConfigureAwait(false);
            hooks?.At("BeforeCommit", finalPath);
            token.ThrowIfCancellationRequested();
            guard.Revalidate();
            status = Rename(original, leaf);
            if (status == 0)
            {
                committed = true;
                hooks?.At("Committed", finalPath);
                return BulkWriteOutcome.Written;
            }
            if (status == BulkOutputDirectoryGuard.NameCollision && guard.ExistingFile(directory, leaf))
                return BulkWriteOutcome.ConflictSkipped;
            throw BulkOutputDirectoryGuard.Error("Commit", status);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try
            {
                if (!committed)
                {
                    Exception? hookFailure = null;
                    try { hooks?.At("BeforeCleanup", finalPath); } catch (Exception error) { hookFailure = error; }
                    var deletion = Marshal.AllocHGlobal(1);
                    try
                    {
                        Marshal.WriteByte(deletion, 1);
                        var cleanup = NtSetInformationFile(original, out _, deletion, 1, 13);
                        if (cleanup != 0) throw BulkOutputDirectoryGuard.Error("Temporary disposition cleanup", cleanup);
                    }
                    finally { Marshal.FreeHGlobal(deletion); }
                    if (hookFailure is not null) throw hookFailure;
                }
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Uncommitted temporary cleanup failed.", failure is null ? [cleanupError] : [failure, cleanupError]);
            }
            finally
            {
                if (stream is not null) stream.Dispose();
                else original.Dispose();
            }
        }
    }

    private static int Rename(SafeFileHandle original, string leaf)
    {
        var name = Encoding.Unicode.GetBytes(leaf);
        // BOOLEAN, aligned HANDLE(NULL), ULONG, WCHAR[]: FileRenameInformation.
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        var buffer = Marshal.AllocHGlobal(nameOffset + name.Length);
        try
        {
            Marshal.Copy(new byte[nameOffset + name.Length], 0, buffer, nameOffset + name.Length);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            return NtSetInformationFile(original, out _, buffer, (uint)(nameOffset + name.Length), 10);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out BulkOutputDirectoryGuard.IoStatus io, IntPtr information, uint length, int informationClass);
}
