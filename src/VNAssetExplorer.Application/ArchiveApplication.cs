using System.Buffers.Binary;
using VNAssetExplorer.Formats.Hfa;
using VNAssetExplorer.Formats.Mzp;
using VNAssetExplorer.Core;
using Core = VNAssetExplorer.Core;

namespace VNAssetExplorer.Application;

public enum ApplicationErrorCode
{
    EntryNotFound,
    UnsupportedPreviewFormat,
    InvalidOrUnsafeFormat,
    ResourceLimitExceeded,
    AccessDenied,
    IoFailure,
    Cancelled,
    EmptySelection,
    InvalidSelection,
    UnsafeOutputPath,
    InsufficientSpace,
}

public sealed class ApplicationException(ApplicationErrorCode code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ApplicationErrorCode Code { get; } = code;
}

public sealed record ArchiveEntryProjection(int Index, string Name, long Offset, long Size);

public sealed record ArchiveSourceIdentity(string ArchivePath, string SourceDirectory, string ArchiveName)
{
    internal object Owner { get; } = new();
}

public enum ApplicationExistingFileAction { Skip, Overwrite, Rename, Cancel }
public sealed record ApplicationExistingFileDecision(ApplicationExistingFileAction Action, bool ApplyToRemaining = false);
public enum ApplicationEntryFailureAction { Continue, IgnoreSubsequent, Cancel }
public sealed record ApplicationConflictContext(int ArchiveIndex, string Name, string RelativePath);
public sealed record ApplicationFailureContext(int ArchiveIndex, string Name, string ErrorCategory);
public interface IApplicationExtractionDecisionProvider
{
    ApplicationExistingFileDecision DecideExistingFile(ApplicationConflictContext context);
    ApplicationEntryFailureAction DecideEntryFailure(ApplicationFailureContext context);
}
public sealed record ApplicationExtractionProgress(int Position, int Total, int ArchiveIndex, string Name, string Phase);
public sealed record ApplicationExtractionResult(int ArchiveIndex, string OriginalName, string? FinalRelativePath, string Status, long BytesWritten, string? ErrorCategory);
public sealed record ApplicationExtractionSummary(IReadOnlyList<ApplicationExtractionResult> Results, bool Cancelled);

public enum SpaceAvailabilityStatus { Unknown, Sufficient, Insufficient }

public sealed record ExtractionPlanEntry(int ArchiveIndex, string Name, long Offset, long Size);


public sealed class ExtractionPlan
{
    private readonly object owner;
    private readonly ExtractionPlanEntry[] entries;

    internal ExtractionPlan(object owner, string outputRoot, IReadOnlyList<ExtractionPlanEntry> entries, long totalBytes, SpaceAvailabilityStatus spaceStatus)
    {
        this.owner = owner;
        OutputRoot = outputRoot;
        this.entries = entries.ToArray();
        Entries = Array.AsReadOnly(this.entries);
        TotalBytes = totalBytes;
        SpaceStatus = spaceStatus;
    }

    public string OutputRoot { get; }
    public IReadOnlyList<ExtractionPlanEntry> Entries { get; }
    public long TotalBytes { get; }
    public SpaceAvailabilityStatus SpaceStatus { get; }

    internal bool BelongsTo(object candidate) => ReferenceEquals(owner, candidate);

    internal IReadOnlyList<ExtractionPlanEntry> SnapshotEntries => entries;
}

public sealed record PreviewResult(uint Width, uint Height, byte[] PngBytes);

public static class ArchiveApplication
{
    public static ArchiveSession OpenHfa(Stream source) => OpenHfa(source, identity: null);

    public static ArchiveSession OpenHfa(Stream source, string sourceArchivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchivePath);
        return OpenHfa(source, new ArchiveSourceIdentity(
            Path.GetFullPath(sourceArchivePath),
            Path.GetDirectoryName(Path.GetFullPath(sourceArchivePath))!,
            Path.GetFileName(sourceArchivePath)));
    }

    private static ArchiveSession OpenHfa(Stream source, ArchiveSourceIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var archive = HfaArchiveReader.Read(source);
            var entries = archive.Entries
                .Select((entry, index) => new ArchiveEntryProjection(index, entry.Name, entry.Offset, entry.Size))
                .ToArray();
            return new ArchiveSession(source, entries, identity);
        }
        catch (HfaFormatException error)
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The HFA archive is invalid or unsafe.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new ApplicationException(ApplicationErrorCode.AccessDenied, "The HFA archive could not be opened because access was denied.", error);
        }
        catch (IOException error)
        {
            throw new ApplicationException(ApplicationErrorCode.IoFailure, "The HFA archive could not be opened.", error);
        }
    }
}

public sealed class ArchiveSession : IDisposable
{
    public const int MaxEntryPrefixBytes = 4096;
    private const int MaxMp4FtypBytes = 4096;
    private const int MaxMp4MoovBytes = 1024 * 1024;
    private const int MaxMp4MetadataBytes = 1024 * 1024;
    private const int MaxMp4TopLevelBoxes = 4096;
    private readonly Stream source;
    private readonly ArchiveSourceIdentity? sourceIdentity;
    private bool disposed;

    internal ArchiveSession(Stream source, IReadOnlyList<ArchiveEntryProjection> entries, ArchiveSourceIdentity? sourceIdentity)
    {
        this.source = source;
        Entries = entries;
        this.sourceIdentity = sourceIdentity;
    }

    public IReadOnlyList<ArchiveEntryProjection> Entries { get; }
    public ArchiveSourceIdentity? SourceIdentity => sourceIdentity;

    public byte[] ReadEntryPrefix(int index, int maxBytes, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (maxBytes is < 0 or > MaxEntryPrefixBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = GetValidatedEntry(index);
            var snapshot = new byte[checked((int)Math.Min(entry.Size, maxBytes))];
            source.Position = entry.Offset;
            ReadExactly(source, snapshot, cancellationToken);
            return snapshot;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { throw new ApplicationException(ApplicationErrorCode.Cancelled, "The entry prefix read was cancelled.", error); }
        catch (EndOfStreamException error) { throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The archive entry is truncated.", error); }
        catch (UnauthorizedAccessException error) { throw new ApplicationException(ApplicationErrorCode.AccessDenied, "The archive could not be read because access was denied.", error); }
        catch (IOException error) { throw new ApplicationException(ApplicationErrorCode.IoFailure, "The archive could not be read.", error); }
    }

    public async Task CopyEntryToAsync(int index, Stream destination, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = GetValidatedEntry(index);
            source.Position = entry.Offset;
            await CopyExactlyAsync(source, destination, entry.Size, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { throw new ApplicationException(ApplicationErrorCode.Cancelled, "The entry copy was cancelled.", error); }
        catch (EndOfStreamException error) { throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The archive entry is truncated.", error); }
        catch (UnauthorizedAccessException error) { throw new ApplicationException(ApplicationErrorCode.AccessDenied, "The archive could not be read because access was denied.", error); }
        catch (IOException error) { throw new ApplicationException(ApplicationErrorCode.IoFailure, "The archive could not be read.", error); }
    }

    public async Task CopyHwOggPayloadToAsync(int index, Stream destination, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = GetValidatedEntry(index);
            var prefix = ReadEntryPrefix(index, MaxEntryPrefixBytes, cancellationToken);
            if (!AudioClassificationService.TryProbeHwOggVorbis(prefix, entry.Size, out var probe))
            {
                throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The archive entry is not a supported HW/Ogg Vorbis resource.");
            }
            source.Position = checked(entry.Offset + 64);
            await CopyExactlyAsync(source, destination, probe.PayloadLength, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { throw new ApplicationException(ApplicationErrorCode.Cancelled, "The HW/Ogg payload copy was cancelled.", error); }
        catch (EndOfStreamException error) { throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The archive entry is truncated.", error); }
        catch (UnauthorizedAccessException error) { throw new ApplicationException(ApplicationErrorCode.AccessDenied, "The archive could not be read because access was denied.", error); }
        catch (IOException error) { throw new ApplicationException(ApplicationErrorCode.IoFailure, "The archive could not be read.", error); }
    }

    public Mp4VideoProbeResult ProbeSupportedMp4(int index, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ProbeSupportedMp4Core(GetValidatedEntry(index), cancellationToken);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { throw new ApplicationException(ApplicationErrorCode.Cancelled, "The MP4 probe was cancelled.", error); }
        catch (EndOfStreamException) { return Mp4VideoProbeResult.Unknown(Mp4VideoProbeUnknownReason.UnsupportedOrInvalid); }
        catch (UnauthorizedAccessException error) { throw new ApplicationException(ApplicationErrorCode.AccessDenied, "The archive could not be read because access was denied.", error); }
        catch (IOException error) { throw new ApplicationException(ApplicationErrorCode.IoFailure, "The archive could not be read.", error); }
    }

    public async Task CopySupportedMp4ToAsync(int index, Stream destination, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        try
        {
            var entry = GetValidatedEntry(index);
            var probe = ProbeSupportedMp4(index, cancellationToken);
            if (!probe.IsSupported)
                throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The archive entry is not a supported MP4/H.264 resource.");
            source.Position = entry.Offset;
            await CopyExactlyAsync(source, destination, entry.Size, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { throw new ApplicationException(ApplicationErrorCode.Cancelled, "The MP4 copy was cancelled.", error); }
        catch (EndOfStreamException error) { throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The archive entry is truncated.", error); }
        catch (UnauthorizedAccessException error) { throw new ApplicationException(ApplicationErrorCode.AccessDenied, "The archive could not be read because access was denied.", error); }
        catch (IOException error) { throw new ApplicationException(ApplicationErrorCode.IoFailure, "The archive could not be read.", error); }
    }

    private Mp4VideoProbeResult ProbeSupportedMp4Core(ArchiveEntryProjection entry, CancellationToken cancellationToken)
    {
        var reader = new BoundedEntryReader(source, entry, cancellationToken);
        byte[]? ftyp = null;
        byte[]? moov = null;
        var topLevelCount = 0;
        while (reader.Position < entry.Size)
        {
            if (++topLevelCount > MaxMp4TopLevelBoxes) return Mp4VideoProbeResult.Unknown(Mp4VideoProbeUnknownReason.ResourceLimit);
            if (!reader.TryReadBoxHeader(out var type, out var size, out var headerSize)) return Mp4VideoProbeResult.Unknown(Mp4VideoProbeUnknownReason.UnsupportedOrInvalid);
            if (size < headerSize || size > entry.Size - reader.Position + headerSize) return Mp4VideoProbeResult.Unknown(Mp4VideoProbeUnknownReason.UnsupportedOrInvalid);
            var payloadSize = size - headerSize;
            if (type == VideoClassificationService.FourCc("ftyp"))
            {
                if (ftyp is not null || size > MaxMp4FtypBytes) return Mp4VideoProbeResult.Unknown(Mp4VideoProbeUnknownReason.ResourceLimit);
                ftyp = reader.ReadCurrentBox(size, MaxMp4MetadataBytes);
            }
            else if (type == VideoClassificationService.FourCc("moov"))
            {
                if (moov is not null || size > MaxMp4MoovBytes || (ftyp?.Length ?? 0) + size > MaxMp4MetadataBytes)
                    return Mp4VideoProbeResult.Unknown(Mp4VideoProbeUnknownReason.ResourceLimit);
                moov = reader.ReadCurrentBox(size, MaxMp4MetadataBytes);
            }
            else reader.Skip(payloadSize);
        }
        if (ftyp is null || moov is null) return Mp4VideoProbeResult.Unknown(Mp4VideoProbeUnknownReason.UnsupportedOrInvalid);
        return VideoClassificationService.TryProbeMp4H264(ftyp.Concat(moov).ToArray(), ftyp.Length + moov.Length, out var probe)
            ? new Mp4VideoProbeResult(probe, null)
            : Mp4VideoProbeResult.Unknown(Mp4VideoProbeUnknownReason.UnsupportedOrInvalid);
    }

    private sealed class BoundedEntryReader(Stream source, ArchiveEntryProjection entry, CancellationToken cancellationToken)
    {
        private readonly Stream source = source;
        private readonly ArchiveEntryProjection entry = entry;
        private readonly CancellationToken cancellationToken = cancellationToken;
        public long Position { get; private set; }

        public bool TryReadBoxHeader(out uint type, out long size, out int headerSize)
        {
            type = 0;
            size = 0;
            headerSize = 0;
            if (entry.Size - Position < 8) return false;
            BoxStart = Position;
            var header = ReadAtCurrent(8);
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(header);
            type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            headerSize = 8;
            if (size32 == 1)
            {
                if (entry.Size - Position < 8) return false;
                var extended = ReadAtCurrent(8);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(extended));
                headerSize = 16;
            }
            else size = size32 == 0 ? entry.Size - BoxStart : size32;
            return size >= headerSize;
        }

        public byte[] ReadCurrentBox(long size, int budget)
        {
            if (size > budget || size > int.MaxValue) throw new EndOfStreamException("The MP4 metadata exceeds its safety budget.");
            if (size > entry.Size - BoxStart) throw new EndOfStreamException("The MP4 box exceeds the entry.");
            Position = BoxStart;
            return ReadAtCurrent(checked((int)size));
        }

        public void Skip(long count)
        {
            if (count < 0 || count > entry.Size - Position) throw new EndOfStreamException("The MP4 box exceeds the entry.");
            Position = checked(Position + count);
        }

        private byte[] ReadAtCurrent(int count)
        {
            if (count < 0 || count > entry.Size - Position) throw new EndOfStreamException("The MP4 read exceeds the entry.");
            cancellationToken.ThrowIfCancellationRequested();
            source.Position = checked(entry.Offset + Position);
            var bytes = new byte[count];
            ReadExactly(source, bytes, cancellationToken);
            Position = checked(Position + count);
            return bytes;
        }

        private long BoxStart { get; set; }
    }

    public ExtractionPlan PlanExtraction(
        IReadOnlyList<int> archiveIndices,
        string parentOutputDirectory,
        IReadOnlyList<string>? protectedRoots = null,
        Func<string, long?>? availableSpaceProvider = null)
    {
        EnsureNotDisposed();
        var identity = sourceIdentity ?? throw new ApplicationException(ApplicationErrorCode.UnsafeOutputPath, "This archive session has no trusted file identity.");
        return PlanExtractionCore(archiveIndices, parentOutputDirectory, identity.SourceDirectory, identity.ArchiveName, protectedRoots, availableSpaceProvider);
    }

    public ExtractionPlan PlanExtraction(
        IReadOnlyList<int> archiveIndices,
        string parentOutputDirectory,
        string sourceDirectory,
        string? archiveName = null,
        IReadOnlyList<string>? protectedRoots = null,
        Func<string, long?>? availableSpaceProvider = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(archiveIndices);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        return PlanExtractionCore(archiveIndices, parentOutputDirectory, sourceDirectory, archiveName, protectedRoots, availableSpaceProvider);
    }

    private ExtractionPlan PlanExtractionCore(
        IReadOnlyList<int> archiveIndices,
        string parentOutputDirectory,
        string sourceDirectory,
        string? archiveName,
        IReadOnlyList<string>? protectedRoots,
        Func<string, long?>? availableSpaceProvider)
    {
        if (archiveIndices.Count == 0)
        {
            throw new ApplicationException(ApplicationErrorCode.EmptySelection, "At least one archive entry must be selected.");
        }

        var parent = Path.GetFullPath(parentOutputDirectory);
        var rootName = string.IsNullOrWhiteSpace(archiveName) ? "archive" : Path.GetFileNameWithoutExtension(archiveName);
        var outputRoot = Path.GetFullPath(Path.Combine(parent, rootName));
        EnsureOutputRootSafe(outputRoot, sourceDirectory, protectedRoots);
        var selected = archiveIndices.Select(index =>
        {
            if ((uint)index >= (uint)Entries.Count)
            {
                throw new ApplicationException(ApplicationErrorCode.InvalidSelection, "The selected archive entry was not found.");
            }
            var entry = Entries[index];
            return new ExtractionPlanEntry(entry.Index, entry.Name, entry.Offset, entry.Size);
        }).ToArray();
        var total = selected.Aggregate(0L, (sum, entry) => checked(sum + entry.Size));
        long? available;
        try
        {
            available = availableSpaceProvider?.Invoke(outputRoot);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new ApplicationException(ApplicationErrorCode.AccessDenied, "Available output space could not be determined because access was denied.", error);
        }
        catch (IOException error)
        {
            throw new ApplicationException(ApplicationErrorCode.IoFailure, "Available output space could not be determined.", error);
        }
        var status = available is null ? SpaceAvailabilityStatus.Unknown
            : available.Value < 0 ? throw new ApplicationException(ApplicationErrorCode.IoFailure, "Available output space could not be determined.")
            : available.Value < total ? SpaceAvailabilityStatus.Insufficient : SpaceAvailabilityStatus.Sufficient;
        if (status == SpaceAvailabilityStatus.Insufficient)
        {
            throw new ApplicationException(ApplicationErrorCode.InsufficientSpace, "The output volume does not have enough available space.");
        }
        return new ExtractionPlan(this, outputRoot, selected, total, status);
    }


    public Task<ApplicationExtractionSummary> ExtractAsync(
        ExtractionPlan plan,
        IApplicationExtractionDecisionProvider? decisionProvider,
        CancellationToken cancellationToken = default,
        IProgress<ApplicationExtractionProgress>? progress = null)
    {
        var adapter = decisionProvider is null ? null : new ApplicationDecisionAdapter(decisionProvider);
        Action<Core.ExtractionProgress>? coreProgress = progress is null ? null : value =>
            progress.Report(new ApplicationExtractionProgress(value.Position, value.Total, value.ArchiveIndex, value.Name, value.Phase.ToString()));
        return ExtractApplicationAsync(plan, adapter, cancellationToken, coreProgress);
    }

    private async Task<ApplicationExtractionSummary> ExtractApplicationAsync(ExtractionPlan plan, Core.IExtractionDecisionProvider? provider, CancellationToken token, Action<Core.ExtractionProgress>? progress)
    {
        var summary = await ExtractCoreAsync(plan, provider, token, progress);
        return new ApplicationExtractionSummary(summary.Results.Select(result => new ApplicationExtractionResult(
            result.ArchiveIndex, result.OriginalName, result.FinalRelativePath, result.Status.ToString(), result.BytesWritten, result.ErrorCategory)).ToArray(), summary.Cancelled);
    }

    public Task<ExtractionSummary> ExtractAsync(
        ExtractionPlan plan,
        IExtractionDecisionProvider? decisionProvider = null,
        CancellationToken cancellationToken = default,
        Action<ExtractionProgress>? progress = null)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.BelongsTo(this))
        {
            throw new InvalidOperationException("An extraction plan can only be executed by the session that created it.");
        }
        var entries = plan.SnapshotEntries.Select(entry => new ExtractionEntry(entry.ArchiveIndex, entry.Name, entry.Offset, entry.Size)).ToArray();
        return ArchiveExtractor.ExtractAsync(source, plan.OutputRoot, entries, decisionProvider, cancellationToken, progress);
    }

    private Task<ExtractionSummary> ExtractCoreAsync(ExtractionPlan plan, Core.IExtractionDecisionProvider? provider, CancellationToken token, Action<Core.ExtractionProgress>? progress)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.BelongsTo(this)) throw new InvalidOperationException("An extraction plan can only be executed by the session that created it.");
        var entries = plan.SnapshotEntries.Select(entry => new ExtractionEntry(entry.ArchiveIndex, entry.Name, entry.Offset, entry.Size)).ToArray();
        return ArchiveExtractor.ExtractAsync(source, plan.OutputRoot, entries, provider, token, progress);
    }

    private sealed class ApplicationDecisionAdapter(IApplicationExtractionDecisionProvider provider) : Core.IExtractionDecisionProvider
    {
        public Core.ExistingFileDecision DecideExistingFile(Core.ExtractionEntry entry, string relativePath)
        {
            var decision = provider.DecideExistingFile(new ApplicationConflictContext(entry.ArchiveIndex, entry.Name, relativePath));
            return new Core.ExistingFileDecision(decision.Action switch
            {
                ApplicationExistingFileAction.Skip => Core.ExistingFileAction.Skip,
                ApplicationExistingFileAction.Overwrite => Core.ExistingFileAction.Overwrite,
                ApplicationExistingFileAction.Rename => Core.ExistingFileAction.Rename,
                ApplicationExistingFileAction.Cancel => Core.ExistingFileAction.Cancel,
                _ => throw new InvalidOperationException("Unsupported existing-file decision.")
            }, false);
        }

        public Core.EntryFailureAction DecideEntryFailure(Core.ExtractionEntry entry, string errorCategory) =>
            provider.DecideEntryFailure(new ApplicationFailureContext(entry.ArchiveIndex, entry.Name, errorCategory)) switch
            {
                ApplicationEntryFailureAction.Continue => Core.EntryFailureAction.Continue,
                ApplicationEntryFailureAction.IgnoreSubsequent => Core.EntryFailureAction.IgnoreSubsequent,
                ApplicationEntryFailureAction.Cancel => Core.EntryFailureAction.Cancel,
                _ => throw new InvalidOperationException("Unsupported entry-failure decision.")
            };
    }

    public PreviewResult PreviewMzp(int index, CancellationToken cancellationToken = default)
    {
        MzpPipelineDiagnostics.PreviewPipelineStarted();
        EnsureNotDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            throw new ApplicationException(ApplicationErrorCode.Cancelled, "The preview operation was cancelled.");
        }
        if ((uint)index >= (uint)Entries.Count)
        {
            throw new ApplicationException(ApplicationErrorCode.EntryNotFound, "The requested archive entry was not found.");
        }

        try
        {
            var entry = Entries[index];
            if (entry.Size > MzpLimits.MaxInputBytes)
            {
                throw new ApplicationException(ApplicationErrorCode.ResourceLimitExceeded, "The archive entry exceeds the preview resource limit.");
            }
            if (entry.Offset < 0 || entry.Size < 0 || entry.Offset > source.Length || entry.Size > source.Length - entry.Offset)
            {
                throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The archive entry range is invalid or unsafe.");
            }

            source.Position = entry.Offset;
            using var payload = new MemoryStream(checked((int)entry.Size));
            CopyExactly(source, payload, entry.Size, cancellationToken);
            payload.Position = 0;
            var preview = MzpPreviewReader.Read(payload, cancellationToken);
            return new PreviewResult(preview.Width, preview.Height, preview.PngBytes);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            throw new ApplicationException(ApplicationErrorCode.Cancelled, "The preview operation was cancelled.", error);
        }
        catch (MzpFormatException error)
        {
            var code = error.Code is MzpErrorCode.UnsupportedPreviewFormat or MzpErrorCode.WrongMagic
                ? ApplicationErrorCode.UnsupportedPreviewFormat
                : error.Code == MzpErrorCode.ResourceLimitExceeded || error.Code == MzpErrorCode.MzxOutputLimitExceeded
                    ? ApplicationErrorCode.ResourceLimitExceeded
                    : ApplicationErrorCode.InvalidOrUnsafeFormat;
            throw new ApplicationException(code, "The MZP preview could not be decoded safely.", error);
        }
        catch (HepFormatException error)
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The HEP image could not be decoded safely.", error);
        }
        catch (EndOfStreamException error)
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The archive entry is truncated.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new ApplicationException(ApplicationErrorCode.AccessDenied, "The archive could not be read because access was denied.", error);
        }
        catch (IOException error)
        {
            throw new ApplicationException(ApplicationErrorCode.IoFailure, "The archive could not be read.", error);
        }
    }

    public MzpImageMetadata ProbeMzpMetadata(int index, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if ((uint)index >= (uint)Entries.Count)
            throw new ApplicationException(ApplicationErrorCode.EntryNotFound, "The requested archive entry was not found.");
        var entry = Entries[index];
        if (!entry.Name.EndsWith(".mzp", StringComparison.OrdinalIgnoreCase))
            throw new ApplicationException(ApplicationErrorCode.UnsupportedPreviewFormat, "The archive entry is not an MZP image.");
        if (entry.Size > MzpLimits.MaxInputBytes || entry.Offset < 0 || entry.Size < 0 || entry.Offset > source.Length || entry.Size > source.Length - entry.Offset)
            throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The archive entry range is invalid or unsafe.");
        source.Position = entry.Offset;
        var metadata = MzpImageReader.ReadMetadataHeader(source, entry.Size, cancellationToken);
        if (metadata.BmpType != 0x000C || metadata.BmpDepth != 0x11)
            throw new ApplicationException(ApplicationErrorCode.UnsupportedPreviewFormat, "The MZP image variant is not supported.");
        return metadata;
    }

    public MzpImageMetadata ProbeBulkMzpMetadata(int index, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var entry = GetValidatedEntry(index);
        if (!entry.Name.EndsWith(".mzp", StringComparison.OrdinalIgnoreCase))
            throw new ApplicationException(ApplicationErrorCode.UnsupportedPreviewFormat, "The archive entry is not an MZP image.");
        try
        {
            source.Position = entry.Offset;
            var metadata = MzpImageReader.ReadBulkMetadataHeader(source, entry.Size, cancellationToken);
            if (!MzpBulkReader.Supports(metadata))
                throw new ApplicationException(ApplicationErrorCode.UnsupportedPreviewFormat, "The MZP image variant is not supported.");
            return metadata;
        }
        catch (MzpFormatException error) { throw BulkMzpError(error); }
        catch (IOException error) { throw new ApplicationException(ApplicationErrorCode.IoFailure, "The bulk image metadata could not be read.", error); }
    }

    public void WriteBulkMzpPng(int index, Stream destination, CancellationToken cancellationToken = default,
        Action<MzpBulkPhase>? progress = null)
    {
        EnsureNotDisposed();
        var entry = GetValidatedEntry(index);
        try
        {
            source.Position = entry.Offset;
            MzpBulkReader.WritePng(source, entry.Size, destination, cancellationToken, progress);
        }
        catch (MzpFormatException error) { throw BulkMzpError(error); }
        catch (HepFormatException error) { throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The bulk HEP tile is invalid.", error); }
        catch (EndOfStreamException error) { throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The bulk image entry is truncated.", error); }
        catch (IOException error) { throw new ApplicationException(ApplicationErrorCode.IoFailure, "The bulk image could not be read or written.", error); }
    }

    private static ApplicationException BulkMzpError(MzpFormatException error) => new(
        error.Code is MzpErrorCode.WrongMagic or MzpErrorCode.UnsupportedPreviewFormat
            ? ApplicationErrorCode.UnsupportedPreviewFormat
            : error.Code is MzpErrorCode.ResourceLimitExceeded or MzpErrorCode.MzxOutputLimitExceeded
                ? ApplicationErrorCode.ResourceLimitExceeded : ApplicationErrorCode.InvalidOrUnsafeFormat,
        "The MZP bulk image could not be processed safely.", error);

    public void Dispose()
    {
        disposed = true;
    }

    private void EnsureNotDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(ArchiveSession));
        }
    }

    private ArchiveEntryProjection GetValidatedEntry(int index)
    {
        if ((uint)index >= (uint)Entries.Count)
        {
            throw new ApplicationException(ApplicationErrorCode.EntryNotFound, "The requested archive entry was not found.");
        }

        var entry = Entries[index];
        if (entry.Offset < 0 || entry.Size < 0 || entry.Offset > source.Length || entry.Size > source.Length - entry.Offset)
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidOrUnsafeFormat, "The archive entry range is invalid or unsafe.");
        }

        return entry;
    }

    private static void EnsureOutputRootSafe(string outputRoot, string? sourceDirectory, IReadOnlyList<string>? protectedRoots)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(outputRoot);
        foreach (var protectedRoot in (protectedRoots ?? []).Append(sourceDirectory).Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var normalizedProtected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot!));
            if (normalizedRoot.Equals(normalizedProtected, StringComparison.OrdinalIgnoreCase)
                || normalizedRoot.StartsWith(normalizedProtected + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApplicationException(ApplicationErrorCode.UnsafeOutputPath, "The output path must be outside protected directories.");
            }
        }
        if (Directory.Exists(outputRoot) && (File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ApplicationException(ApplicationErrorCode.UnsafeOutputPath, "The output path is a reparse point.");
        }

        var current = Path.GetDirectoryName(normalizedRoot);
        while (current is not null)
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ApplicationException(ApplicationErrorCode.UnsafeOutputPath, "The output path has a reparse-point ancestor.");
            }
            if (Path.GetPathRoot(current)?.Equals(current, StringComparison.OrdinalIgnoreCase) == true)
            {
                break;
            }
            current = Path.GetDirectoryName(current);
        }
    }

    private static void CopyExactly(Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("The archive entry is truncated.");
            }
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void ReadExactly(Stream source, byte[] destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(destination, offset, destination.Length - offset);
            if (read == 0) throw new EndOfStreamException("The archive entry is truncated.");
            offset += read;
        }
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The archive entry is truncated.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }
}
