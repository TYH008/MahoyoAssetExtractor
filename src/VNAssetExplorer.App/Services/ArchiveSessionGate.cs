using System.IO;
using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

/// <summary>Provides one I/O gate for all operations on one archive session.</summary>
public sealed class ArchiveSessionGate : IArchivePreviewSession, IAsyncDisposable
{
    private readonly IArchiveSession inner;
    private readonly SemaphoreSlim ioGate = new(1, 1);
    private int disposed;

    public ArchiveSessionGate(IArchiveSession inner)
    {
        this.inner = inner;
    }

    public IReadOnlyList<ArchiveEntryProjection> Entries => inner.Entries;

    public async Task<byte[]> ReadEntryPrefixAsync(int index, int maxBytes, CancellationToken cancellationToken = default)
    {
        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return await inner.ReadEntryPrefixAsync(index, maxBytes, cancellationToken).ConfigureAwait(false);
        }
        finally { ioGate.Release(); }
    }

    public async Task CopyEntryToAsync(int index, Stream destination, CancellationToken cancellationToken = default)
    {
        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            await inner.CopyEntryToAsync(index, destination, cancellationToken).ConfigureAwait(false);
        }
        finally { ioGate.Release(); }
    }

    public async Task CopyHwOggPayloadToAsync(int index, Stream destination, CancellationToken cancellationToken = default)
    {
        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            await inner.CopyHwOggPayloadToAsync(index, destination, cancellationToken).ConfigureAwait(false);
        }
        finally { ioGate.Release(); }
    }

    public async Task CopySupportedMp4ToAsync(int index, Stream destination, CancellationToken cancellationToken = default)
    {
        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            await inner.CopySupportedMp4ToAsync(index, destination, cancellationToken).ConfigureAwait(false);
        }
        finally { ioGate.Release(); }
    }


    public async Task<PreviewResult> PreviewAsync(int index, CancellationToken cancellationToken = default)
    {
        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return await Task.Run(() => inner.Preview(index, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ioGate.Release();
        }
    }

    public async Task<ApplicationExtractionSummary> ExtractAsync(ExtractionPlan plan, IApplicationExtractionDecisionProvider? decisionProvider, CancellationToken cancellationToken = default, IProgress<ApplicationExtractionProgress>? progress = null)
    {
        await ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return await inner.ExtractAsync(plan, decisionProvider, cancellationToken, progress).ConfigureAwait(false);
        }
        finally
        {
            ioGate.Release();
        }
    }

    public ExtractionPlan PlanExtraction(IReadOnlyList<int> archiveIndices, string parentOutputDirectory, IReadOnlyList<string>? protectedRoots = null, Func<string, long?>? availableSpaceProvider = null)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        return inner.PlanExtraction(archiveIndices, parentOutputDirectory, protectedRoots, availableSpaceProvider);
    }

    public PreviewResult Preview(int index, CancellationToken cancellationToken = default) =>
        PreviewAsync(index, cancellationToken).GetAwaiter().GetResult();

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            inner.Dispose();
        }
        finally
        {
            ioGate.Release();
            ioGate.Dispose();
        }
    }
}
