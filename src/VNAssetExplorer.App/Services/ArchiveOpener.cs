using System.IO;
using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

public sealed class ArchiveOpener : IArchiveOpener
{
    private readonly IAvailableSpaceProvider availableSpaceProvider;

    public ArchiveOpener(IAvailableSpaceProvider? availableSpaceProvider = null)
    {
        this.availableSpaceProvider = availableSpaceProvider ?? new DriveInfoAvailableSpaceProvider();
    }
    public IArchivePreviewSession Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var session = ArchiveApplication.OpenHfa(stream, Path.GetFullPath(path));
            return new ArchiveSessionGate(new ArchivePreviewSession(stream, session, availableSpaceProvider));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private sealed class ArchivePreviewSession(Stream stream, ArchiveSession session, IAvailableSpaceProvider availableSpaceProvider) : IArchivePreviewSession
    {
        public IReadOnlyList<ArchiveEntryProjection> Entries => session.Entries;

        public Task<byte[]> ReadEntryPrefixAsync(int index, int maxBytes, CancellationToken cancellationToken = default) =>
            Task.Run(() => session.ReadEntryPrefix(index, maxBytes, cancellationToken), cancellationToken);

        public Task CopyEntryToAsync(int index, Stream destination, CancellationToken cancellationToken = default) =>
            session.CopyEntryToAsync(index, destination, cancellationToken);

        public Task CopyHwOggPayloadToAsync(int index, Stream destination, CancellationToken cancellationToken = default) =>
            session.CopyHwOggPayloadToAsync(index, destination, cancellationToken);

        public Task CopySupportedMp4ToAsync(int index, Stream destination, CancellationToken cancellationToken = default) =>
            session.CopySupportedMp4ToAsync(index, destination, cancellationToken);


        public PreviewResult Preview(int index, CancellationToken cancellationToken = default) =>
            session.PreviewMzp(index, cancellationToken);

        public ExtractionPlan PlanExtraction(IReadOnlyList<int> archiveIndices, string parentOutputDirectory, IReadOnlyList<string>? protectedRoots = null, Func<string, long?>? ignored = null) =>
            session.PlanExtraction(archiveIndices, parentOutputDirectory, protectedRoots, availableSpaceProvider.GetAvailableBytes);

        public Task<ApplicationExtractionSummary> ExtractAsync(ExtractionPlan plan, IApplicationExtractionDecisionProvider? decisionProvider, CancellationToken cancellationToken = default, IProgress<ApplicationExtractionProgress>? progress = null) =>
            session.ExtractAsync(plan, decisionProvider, cancellationToken, progress);

        public void Dispose()
        {
            session.Dispose();
            stream.Dispose();
        }
    }
}

public sealed class DriveInfoAvailableSpaceProvider : IAvailableSpaceProvider
{
    public long? GetAvailableBytes(string outputRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(outputRoot));
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
    }
}
