using System.IO;
using VNAssetExplorer.Application;
using VNAssetExplorer.Formats.Mzp;
using AppException = VNAssetExplorer.Application.ApplicationException;

namespace VNAssetExplorer.App.Services;

public enum BulkMediaPhase { Scanning, Classifying, Writing }
// Counters reset for each (ArchivePath, Category). KnownTotalCount is all entries
// in that opened archive, not a media-match or game-global total. A processed
// entry includes unmatched, recognition failure, written, skipped, failed or cancelled.
public sealed record BulkMediaProgress(BulkMediaPhase Phase, string? ArchivePath = null,
    BulkExtractionCategory? Category = null, int ClassifiedCount = 0, int EntryCount = 0,
    int ProcessedCount = 0, int? KnownTotalCount = null, bool UnitCompleted = false);
public sealed record BulkMediaFailure(string Phase, string? ArchivePath, int? EntryIndex,
    BulkExtractionCategory? Category, Exception Error);
public sealed record BulkMediaCoverage(BulkExtractionCategory Category, int ClassifiedCount);
// NotStartedCount is category work in opened archives, not guessed media matches.
// NotStartedArchiveCount covers archives whose entry count is still unknown.
public sealed record BulkMediaExtractionReport(BulkExtractionResult Result,
    IReadOnlyList<BulkMediaCoverage> ClassificationCoverage, int OpenedArchiveCount,
    int FailedArchiveCount, int NotStartedArchiveCount, int FailureCount,
    IReadOnlyList<BulkMediaFailure> Failures);

public interface IBulkMediaExtractionService
{
    Task<BulkMediaExtractionReport> ExtractAsync(BulkExtractionRequest request,
        CancellationToken cancellationToken = default, IProgress<BulkMediaProgress>? progress = null);
}

public sealed class BulkMediaExtractionService : IBulkMediaExtractionService
{
    internal Action<MzpBulkPhase>? ImagePhase { get; set; }
    private readonly GameRootInventoryService inventoryService;
    internal Action<string, int, BulkExtractionCategory>? BeforeClassification { get; set; }
    internal BulkOutputHooks? OutputHooks { get; set; }
    internal Func<string, Stream>? OpenSource { get; set; }

    public BulkMediaExtractionService(GameRootInventoryService? inventoryService = null) =>
        this.inventoryService = inventoryService ?? new GameRootInventoryService();

    public Task<BulkMediaExtractionReport> ExtractAsync(BulkExtractionRequest request,
        CancellationToken cancellationToken = default, IProgress<BulkMediaProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        // None deliberately: completion includes disposal, even when cancellation was already requested.
        return Task.Run(() => RunAsync(request, cancellationToken, progress), CancellationToken.None);
    }

    private async Task<BulkMediaExtractionReport> RunAsync(BulkExtractionRequest request,
        CancellationToken token, IProgress<BulkMediaProgress>? progress)
    {
        var counts = request.Categories.ToDictionary(c => c, c => new Counts(c));
        var failures = new List<BulkMediaFailure>();
        var failureCount = 0;
        var opened = 0;
        var failedArchives = 0;
        var attemptedArchives = 0;
        GameRootInventoryResult inventory = new(request.SourceDirectory, [], new(0, 0, 0, 0, 0, 0), GameRootInventoryOutcome.Error);
        var outcome = BulkExtractionOutcome.Completed;
        void Failure(string phase, string? archive, int? index, BulkExtractionCategory? category, Exception error)
        {
            failureCount++;
            if (failures.Count < 100) failures.Add(new(phase, archive, index, category, error));
        }

        try
        {
            token.ThrowIfCancellationRequested();
            _ = BulkExtractionOutputPlan.Create(request, []);
            if (request.Categories.Contains(BulkExtractionCategory.GalleryImages) || request.Categories.Contains(BulkExtractionCategory.GalleryAudio))
                GalleryImageCatalog.ValidateGameExecutable(request.SourceDirectory);
            progress?.Report(new(BulkMediaPhase.Scanning));
            inventory = await inventoryService.ScanAsync(request.SourceDirectory, token).ConfigureAwait(false);
            if (inventory.Outcome == GameRootInventoryOutcome.Cancelled) token.ThrowIfCancellationRequested();
            if (inventory.Outcome == GameRootInventoryOutcome.Error)
                throw inventory.Error ?? new IOException("Source inventory failed.");
            foreach (var archive in inventory.Archives)
            {
                token.ThrowIfCancellationRequested();
                // Retain only this archive's source-directory chain, and reject unsafe source leaves.
                using var sourceGuard = new BulkOutputDirectoryGuard();
                var archivePath = BulkOutputDirectoryGuard.ValidatePath(archive.FullPath);
                sourceGuard.Acquire(Path.GetDirectoryName(archivePath)!, create: false);
                _ = BulkExtractionOutputPlan.Create(request, [new(archivePath, 0, "validation", request.Categories[0])]);
                attemptedArchives++;
                Stream source;
                ArchiveSession session;
                try
                {
                    if ((File.GetAttributes(archivePath) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new InvalidOperationException("Unsafe source archive path.");
                    source = OpenSource?.Invoke(archivePath) ?? new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try { session = ArchiveApplication.OpenHfa(source, archivePath); }
                    catch { source.Dispose(); throw; }
                }
                catch (Exception error) when (IsSourceFailure(error))
                {
                    failedArchives++;
                    Failure("OpenArchive", archivePath, null, null, error);
                    continue;
                }
                using (source)
                using (session)
                {
                    opened++;
                    foreach (var c in counts.Values) c.NotStarted += session.Entries.Count;
                    var processed = request.Categories.ToDictionary(c => c, _ => 0);
                    var classifiedStart = counts.ToDictionary(pair => pair.Key, pair => pair.Value.Classified);
                    void Publish(BulkMediaPhase phase, BulkExtractionCategory category) => progress?.Report(new(
                        phase, archivePath, category, counts[category].Classified - classifiedStart[category],
                        session.Entries.Count, processed[category], session.Entries.Count,
                        processed[category] == session.Entries.Count));
                    foreach (var category in request.Categories) Publish(BulkMediaPhase.Classifying, category);
                    foreach (var entry in session.Entries)
                    foreach (var category in request.Categories)
                    {
                        token.ThrowIfCancellationRequested();
                        var count = counts[category];
                        count.NotStarted--;
                        var phase = BulkMediaPhase.Classifying;
                        try
                        {
                            try
                            {
                                Publish(phase, category);
                                BeforeClassification?.Invoke(archivePath, entry.Index, category);
                                token.ThrowIfCancellationRequested();
                                var matched = Probe(session, entry, category, token);
                                count.Classified++;
                                if (!matched) { count.Unmatched++; continue; }
                                count.Matched++;
                            }
                            catch (Exception error) when (IsCancellation(error, token))
                            {
                                count.Cancelled++;
                                throw new OperationCanceledException(token);
                            }
                            catch (Exception error) when (IsSourceFailure(error))
                            {
                                count.Classified++;
                                count.RecognitionFailed++;
                                Failure("Classification", archivePath, entry.Index, category, error);
                                continue;
                            }

                            try
                            {
                                phase = BulkMediaPhase.Writing;
                                Publish(phase, category);
                                var plan = BulkExtractionOutputPlan.Create(request, [new(archivePath, entry.Index, entry.Name, category)]);
                                plan.Hooks = OutputHooks;
                                var item = plan.Items[0];
                                var written = category switch
                                {
                                    BulkExtractionCategory.Images or BulkExtractionCategory.GalleryImages => await plan.WriteBulkPngAsync(item, category, (stream, ct) => CopySourceAsync(stream, destination =>
                                    {
                                        session.WriteBulkMzpPng(entry.Index, destination, ct, ImagePhase);
                                        return Task.CompletedTask;
                                    }).GetAwaiter().GetResult(), token).ConfigureAwait(false),
                                    BulkExtractionCategory.Audio => await plan.WriteOggAsync(item, (stream, ct) => CopySourceAsync(stream, destination => session.CopyHwOggPayloadToAsync(entry.Index, destination, ct)), token).ConfigureAwait(false),
                                    BulkExtractionCategory.GalleryAudio => await plan.WriteOggAsync(item, category, (stream, ct) => CopySourceAsync(stream, destination => session.CopyHwOggPayloadToAsync(entry.Index, destination, ct)), token).ConfigureAwait(false),
                                    BulkExtractionCategory.Video => await plan.WriteMp4Async(item, (stream, ct) => CopySourceAsync(stream, destination => session.CopySupportedMp4ToAsync(entry.Index, destination, ct)), token).ConfigureAwait(false),
                                    _ => throw new InvalidOperationException(),
                                };
                                if (written == BulkWriteOutcome.Written) count.Written++;
                                else count.Skipped++;
                            }
                            catch (Exception error) when (IsCancellation(error, token))
                            {
                                count.Cancelled++;
                                throw new OperationCanceledException(token);
                            }
                            catch (AppException error) when (error.Code is ApplicationErrorCode.InvalidOrUnsafeFormat or ApplicationErrorCode.UnsupportedPreviewFormat
                                or ApplicationErrorCode.ResourceLimitExceeded or ApplicationErrorCode.IoFailure or ApplicationErrorCode.AccessDenied)
                            {
                                count.WriteFailed++;
                                Failure("Extraction", archivePath, entry.Index, category, error);
                            }
                            catch (SourceCopyException error)
                            {
                                count.WriteFailed++;
                                Failure("Extraction", archivePath, entry.Index, category, error.InnerException!);
                            }
                            catch (Exception error)
                            {
                                count.WriteFailed++;
                                Failure("Output", archivePath, entry.Index, category, error);
                                outcome = BulkExtractionOutcome.Stopped;
                                return Report();
                            }
                        }
                        finally
                        {
                            processed[category]++;
                            Publish(phase, category);
                        }
                    }
                }
            }
            // A late request must not turn a committed item into a cancelled item.
            if (token.IsCancellationRequested) outcome = BulkExtractionOutcome.Cancelled;
        }
        catch (Exception error) when (IsCancellation(error, token))
        {
            outcome = BulkExtractionOutcome.Cancelled;
            if (inventory.Outcome == GameRootInventoryOutcome.Error && inventory.Error is null)
                inventory = new(request.SourceDirectory, [], inventory.Coverage, GameRootInventoryOutcome.Cancelled);
        }
        catch (Exception error)
        {
            outcome = BulkExtractionOutcome.Stopped;
            Failure("SafetyOrScan", null, null, null, error);
        }
        return Report();

        BulkMediaExtractionReport Report() => new(
            new(inventory.Coverage, inventory.Outcome, counts.Values.Select(c => c.Result()), outcome),
            Array.AsReadOnly(counts.Values.Select(c => new BulkMediaCoverage(c.Category, c.Classified)).ToArray()),
            opened, failedArchives, inventory.Archives.Count - attemptedArchives, failureCount, failures.AsReadOnly());
    }

    private static bool Probe(ArchiveSession session, ArchiveEntryProjection entry, BulkExtractionCategory category, CancellationToken token)
    {
        switch (category)
        {
            case BulkExtractionCategory.Images:
                if (!entry.Name.EndsWith(".mzp", StringComparison.OrdinalIgnoreCase)) return false;
                _ = session.ProbeBulkMzpMetadata(entry.Index, token);
                return true;
            case BulkExtractionCategory.GalleryImages:
                if (!GalleryImageCatalog.Contains(entry.Name)) return false;
                _ = session.ProbeBulkMzpMetadata(entry.Index, token);
                return true;
            case BulkExtractionCategory.Audio:
                return AudioClassificationService.TryProbeHwOggVorbis(session.ReadEntryPrefix(entry.Index, ArchiveSession.MaxEntryPrefixBytes, token), entry.Size, out _);
            case BulkExtractionCategory.GalleryAudio:
                return GalleryAudioCatalog.Contains(entry.Name) && AudioClassificationService.TryProbeHwOggVorbis(session.ReadEntryPrefix(entry.Index, ArchiveSession.MaxEntryPrefixBytes, token), entry.Size, out _);
            case BulkExtractionCategory.Video:
                return session.ProbeSupportedMp4(entry.Index, token).IsSupported;
            default: throw new InvalidOperationException();
        }
    }

    private static bool IsCancellation(Exception error, CancellationToken token) => token.IsCancellationRequested &&
        (error is OperationCanceledException || error is AppException { Code: ApplicationErrorCode.Cancelled });
    private static bool IsSourceFailure(Exception error) => error is AppException or IOException or UnauthorizedAccessException or MzpFormatException or OverflowException;

    private static async Task CopySourceAsync(Stream destination, Func<Stream, Task> copy)
    {
        // ArchiveSession wraps both source and destination IOException. Track destination errors
        // before that wrapping so an output failure can never be treated as an item failure.
        var tracked = new DestinationStream(destination);
        try { await copy(tracked).ConfigureAwait(false); }
        catch (Exception error) when (tracked.Failed && error is not OperationCanceledException
            && error is not AppException { Code: ApplicationErrorCode.Cancelled })
        {
            throw new IOException("Output payload write failed.", error);
        }
        catch (AppException error) when (!tracked.Failed && error.Code is not ApplicationErrorCode.Cancelled)
        {
            throw new SourceCopyException(error);
        }
    }

    private sealed class SourceCopyException(Exception inner) : Exception("Source payload copy failed.", inner);
    private sealed class DestinationStream(Stream destination) : Stream
    {
        public bool Failed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => destination.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            try { destination.Write(buffer, offset, count); }
            catch { Failed = true; throw; }
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            try { await destination.WriteAsync(buffer, token).ConfigureAwait(false); }
            catch { Failed = true; throw; }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    }

    private sealed class Counts(BulkExtractionCategory category)
    {
        public BulkExtractionCategory Category { get; } = category;
        public int Classified, Matched, Unmatched, RecognitionFailed, Written, Skipped, WriteFailed, Cancelled, NotStarted;
        public BulkCategoryResult Result() => new(Category, Matched, Unmatched, RecognitionFailed, Written, Skipped, WriteFailed, Cancelled, NotStarted);
    }
}
