using VNAssetExplorer.Formats.Mzp;

namespace VNAssetExplorer.Application;

public sealed record ClassifiedImage(
    string RootPath,
    string ArchivePath,
    string ArchiveName,
    int ArchiveOrdinal,
    int ArchiveEntryIndex,
    int ArchiveEntryCount,
    int ResourceOrdinal,
    string Name,
    long Size,
    string Type,
    uint Width,
    uint Height)
{
    public string SourcePosition => $"第 {ArchiveEntryIndex + 1} / {ArchiveEntryCount} 项";
}

public sealed record ImageClassificationResult(
    string RootPath,
    IReadOnlyList<ClassifiedImage> Images,
    int ScannedEntryCount,
    int FailedEntryCount,
    bool IsComplete,
    bool IsCancelled);

public sealed record ImageClassificationProgress(int Position, int Total, int ConfirmedImages);
public sealed record ImageClassificationBatch(IReadOnlyList<ClassifiedImage> Images, bool IsComplete);

public interface IImageMetadataProbe
{
    MzpImageMetadata Probe(ArchiveSession session, int index, CancellationToken cancellationToken);
}

public interface IImageClassificationPreviewService
{
    Task<PreviewResult> PreviewAsync(ClassifiedImage image, CancellationToken cancellationToken = default);
}

public sealed class DefaultImageMetadataProbe : IImageMetadataProbe
{
    public MzpImageMetadata Probe(ArchiveSession session, int index, CancellationToken cancellationToken) => session.ProbeMzpMetadata(index, cancellationToken);
}

public sealed class ImageClassificationPreviewService : IImageClassificationPreviewService
{
    public Task<PreviewResult> PreviewAsync(ClassifiedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Task.Run(() =>
        {
            using var stream = new FileStream(image.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var session = ArchiveApplication.OpenHfa(stream, image.ArchivePath);
            return session.PreviewMzp(image.ArchiveEntryIndex, cancellationToken);
        }, CancellationToken.None);
    }
}

public sealed class ImageClassificationService
{
    private readonly IImageMetadataProbe metadataProbe;
    private readonly IImageClassificationPreviewService previewService;

    public ImageClassificationService(IImageMetadataProbe? metadataProbe = null, IImageClassificationPreviewService? previewService = null)
    {
        this.metadataProbe = metadataProbe ?? new DefaultImageMetadataProbe();
        this.previewService = previewService ?? new ImageClassificationPreviewService();
    }

    public Task<PreviewResult> PreviewAsync(ClassifiedImage image, CancellationToken cancellationToken = default) => previewService.PreviewAsync(image, cancellationToken);

    public Task<ImageClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken = default, IProgress<ImageClassificationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return Task.Run(() => Classify(inventory, cancellationToken, progress, null), CancellationToken.None);
    }

    public Task<ImageClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken, IProgress<ImageClassificationProgress>? progress, IProgress<ImageClassificationBatch>? batches)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return Task.Run(() => Classify(inventory, cancellationToken, progress, batches), CancellationToken.None);
    }

    private ImageClassificationResult Classify(GameRootInventoryResult inventory, CancellationToken cancellationToken, IProgress<ImageClassificationProgress>? progress, IProgress<ImageClassificationBatch>? batches)
    {
        var images = new List<ClassifiedImage>();
        var scanned = 0;
        var failed = 0;
        var rootPath = Path.GetFullPath(inventory.RootPath);
        var archiveEntryCounts = inventory.Archives.Select(archive => CountEntries(archive.FullPath)).ToArray();
        var total = archiveEntryCounts.Sum();
        var resourceOffset = 0;
        var publishedCount = 0;
        try
        {
            for (var archiveOrdinal = 0; archiveOrdinal < inventory.Archives.Count; archiveOrdinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archive = inventory.Archives[archiveOrdinal];
                using var stream = new FileStream(archive.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var session = ArchiveApplication.OpenHfa(stream, archive.FullPath);
                for (var entryIndex = 0; entryIndex < session.Entries.Count; entryIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanned++;
                    var entry = session.Entries[entryIndex];
                    try
                    {
                        var metadata = metadataProbe.Probe(session, entryIndex, cancellationToken);
                        images.Add(new ClassifiedImage(rootPath, archive.FullPath, archive.Name, archiveOrdinal + 1, entryIndex, session.Entries.Count, resourceOffset + entryIndex + 1, entry.Name, entry.Size, "图片", metadata.Width, metadata.Height));
                    }
                    catch (ApplicationException) when (!cancellationToken.IsCancellationRequested) { failed++; }
                    catch (MzpFormatException) when (!cancellationToken.IsCancellationRequested) { failed++; }
                    catch (EndOfStreamException) when (!cancellationToken.IsCancellationRequested) { failed++; }
                    catch (OverflowException) when (!cancellationToken.IsCancellationRequested) { failed++; }
                    progress?.Report(new ImageClassificationProgress(scanned, total, images.Count));
                    if (batches is not null && (images.Count - publishedCount >= 100 || entryIndex == session.Entries.Count - 1))
                    {
                        batches.Report(new ImageClassificationBatch(images.Skip(publishedCount).ToArray(), false));
                        publishedCount = images.Count;
                    }
                }
                resourceOffset += archiveEntryCounts[archiveOrdinal];
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ImageClassificationResult(rootPath, images.AsReadOnly(), scanned, failed, false, true);
        }
        batches?.Report(new ImageClassificationBatch(Array.Empty<ClassifiedImage>(), true));
        return new ImageClassificationResult(rootPath, images.AsReadOnly(), scanned, failed, true, false);
    }

    private static int CountEntries(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var session = ArchiveApplication.OpenHfa(stream, path);
            return session.Entries.Count;
        }
        catch { return 0; }
    }
}
