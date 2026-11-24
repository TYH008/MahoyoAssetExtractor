using System.IO;
using VNAssetExplorer.Application;


namespace VNAssetExplorer.App.Services;

public interface IArchiveOpener
{
    IArchivePreviewSession Open(string path);
}

public interface IAvailableSpaceProvider
{
    long? GetAvailableBytes(string outputRoot);
}

public interface IArchiveSession : IDisposable
{
    IReadOnlyList<ArchiveEntryProjection> Entries { get; }
    Task<byte[]> ReadEntryPrefixAsync(int index, int maxBytes, CancellationToken cancellationToken = default) =>
        Task.FromException<byte[]>(new NotSupportedException("This test or preview session does not support entry prefix reads."));
    Task CopyEntryToAsync(int index, Stream destination, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This test or preview session does not support entry copies."));
    Task CopyHwOggPayloadToAsync(int index, Stream destination, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This test or preview session does not support HW/Ogg payload copies."));
    Task CopySupportedMp4ToAsync(int index, Stream destination, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This test or preview session does not support supported MP4 copies."));

    ExtractionPlan PlanExtraction(IReadOnlyList<int> archiveIndices, string parentOutputDirectory, IReadOnlyList<string>? protectedRoots = null, Func<string, long?>? availableSpaceProvider = null) =>
        throw new NotSupportedException("This test or preview session does not support extraction planning.");
    Task<ApplicationExtractionSummary> ExtractAsync(ExtractionPlan plan, IApplicationExtractionDecisionProvider? decisionProvider, CancellationToken cancellationToken = default, IProgress<ApplicationExtractionProgress>? progress = null) =>
        Task.FromException<ApplicationExtractionSummary>(new NotSupportedException("This test or preview session does not support extraction."));

    PreviewResult Preview(int index, CancellationToken cancellationToken = default);
    Task<PreviewResult> PreviewAsync(int index, CancellationToken cancellationToken = default) =>
        Task.Run(() => Preview(index, cancellationToken), cancellationToken);
}

public interface IArchivePreviewSession : IArchiveSession
{
}


