using System.Buffers.Binary;

namespace VNAssetExplorer.Application;

public sealed record ClassifiedVideo(
    string RootPath,
    string ArchivePath,
    string ArchiveName,
    int ArchiveOrdinal,
    int ArchiveEntryIndex,
    int ArchiveEntryCount,
    int ResourceOrdinal,
    string Name,
    long Size,
    string Container,
    string Codec,
    bool HasAudio,
    long? DurationMilliseconds = null,
    uint? Width = null,
    uint? Height = null,
    double? FrameRate = null)
{
    public string SourcePosition => $"第 {ArchiveEntryIndex + 1} / {ArchiveEntryCount} 项";
    public string ResolutionText => Width is > 0 && Height is > 0 ? $"{Width} × {Height}" : "未知";
    public string FrameRateText => FrameRate is > 0 ? $"{FrameRate:0.###} fps" : "未知";
    public string DurationText => DurationMilliseconds is >= 0 ? TimeSpan.FromMilliseconds(DurationMilliseconds.Value).ToString(@"m\:ss") : "未知";
}

public readonly record struct Mp4VideoProbe(
    string Container,
    string Codec,
    bool HasAudio,
    long? DurationMilliseconds = null,
    uint? Width = null,
    uint? Height = null,
    double? FrameRate = null);

public enum Mp4VideoProbeUnknownReason { UnsupportedOrInvalid, ResourceLimit }

public readonly record struct Mp4VideoProbeResult(Mp4VideoProbe? Supported, Mp4VideoProbeUnknownReason? UnknownReason)
{
    public bool IsSupported => Supported is not null;
    public static Mp4VideoProbeResult Unknown(Mp4VideoProbeUnknownReason reason) => new(null, reason);
}

public sealed record VideoClassificationResult(
    string RootPath,
    IReadOnlyList<ClassifiedVideo> Videos,
    int ScannedEntryCount,
    int UnsupportedEntryCount,
    int FailedEntryCount,
    bool IsComplete,
    bool IsCancelled);

public sealed class VideoClassificationService
{
    private static readonly HashSet<uint> SupportedBrands = [FourCc("isom"), FourCc("iso2"), FourCc("mp41"), FourCc("mp42"), FourCc("avc1")];
    private static readonly HashSet<uint> AvcSampleEntries = [FourCc("avc1"), FourCc("avc3")];
    private static readonly HashSet<uint> ContainerBoxes = [FourCc("moov"), FourCc("trak"), FourCc("mdia"), FourCc("minf"), FourCc("stbl")];

    public Task<VideoClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return Task.Run(() => Classify(inventory, cancellationToken), CancellationToken.None);
    }

    private static VideoClassificationResult Classify(GameRootInventoryResult inventory, CancellationToken cancellationToken)
    {
        var videos = new List<ClassifiedVideo>();
        var scanned = 0;
        var unsupported = 0;
        var failed = 0;
        var rootPath = Path.GetFullPath(inventory.RootPath);
        var resourceOrdinal = 0;
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
                    resourceOrdinal++;
                    var entry = session.Entries[entryIndex];
                    try
                    {
                        var probe = session.ProbeSupportedMp4(entryIndex, cancellationToken);
                        if (probe.Supported is { } supported)
                            videos.Add(new ClassifiedVideo(rootPath, archive.FullPath, archive.Name, archiveOrdinal + 1, entryIndex, session.Entries.Count, resourceOrdinal, entry.Name, entry.Size, supported.Container, supported.Codec, supported.HasAudio, supported.DurationMilliseconds, supported.Width, supported.Height, supported.FrameRate));
                        else unsupported++;
                    }
                    catch (ApplicationException error) when (error.Code == ApplicationErrorCode.Cancelled && cancellationToken.IsCancellationRequested)
                    {
                        return new VideoClassificationResult(rootPath, videos.AsReadOnly(), scanned, unsupported, failed, false, true);
                    }
                    catch (ApplicationException) when (!cancellationToken.IsCancellationRequested) { failed++; }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new VideoClassificationResult(rootPath, videos.AsReadOnly(), scanned, unsupported, failed, false, true);
        }
        return new VideoClassificationResult(rootPath, videos.AsReadOnly(), scanned, unsupported, failed, true, false);
    }

    public static bool TryProbeMp4H264(ReadOnlySpan<byte> bytes, long entrySize, out Mp4VideoProbe probe)
    {
        probe = default;
        if (entrySize < 16 || entrySize > bytes.Length) return false;
        var reader = new SpanReader(bytes[..checked((int)entrySize)]);
        if (!reader.TryReadTopLevel(out var ftyp) || ftyp.Type != FourCc("ftyp") || !IsSupportedFtyp(reader.Bytes(ftyp))) return false;

        var hasVideo = false;
        var hasAudio = false;
        long? durationMilliseconds = null;
        var durationConflicted = false;
        VideoTrackMetadata? videoMetadata = null;
        while (reader.TryReadTopLevel(out var box))
        {
            if (box.Type != FourCc("moov")) continue;
            if (!TryReadMoov(reader, box, ref hasVideo, ref hasAudio, ref durationMilliseconds, ref durationConflicted, ref videoMetadata)) return false;
        }
        if (!reader.AtEnd || !hasVideo) return false;
        probe = new Mp4VideoProbe("MP4", "H.264", hasAudio, durationConflicted ? null : durationMilliseconds, videoMetadata?.PublishedWidth, videoMetadata?.PublishedHeight, videoMetadata?.PublishedFrameRate);
        return true;
    }

    private static bool IsSupportedFtyp(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8 || payload.Length % 4 != 0) return false;
        if (SupportedBrands.Contains(BinaryPrimitives.ReadUInt32BigEndian(payload))) return true;
        for (var offset = 8; offset < payload.Length; offset += 4)
            if (SupportedBrands.Contains(BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]))) return true;
        return false;
    }

    private static bool TryReadMoov(SpanReader reader, Box moov, ref bool hasVideo, ref bool hasAudio, ref long? durationMilliseconds, ref bool durationConflicted, ref VideoTrackMetadata? videoMetadata)
    {
        var childReader = reader.SubReader(moov.PayloadStart, moov.End);
        while (childReader.TryReadTopLevel(out var child))
        {
            if (child.Type == FourCc("mvhd"))
            {
                if (TryReadDuration(childReader.Bytes(child), out var parsedDuration)) MergeTrustedValue(ref durationMilliseconds, ref durationConflicted, parsedDuration);
                continue;
            }
            if (child.Type != FourCc("trak")) continue;
            if (!TryReadTrack(childReader, child, 0, out var handler, out var avc, out var trackMetadata)) return false;
            if (handler == FourCc("vide") && avc)
            {
                hasVideo = true;
                if (trackMetadata is not null)
                {
                    MergeTrustedValue(ref durationMilliseconds, ref durationConflicted, trackMetadata.DurationMilliseconds);
                    if (videoMetadata is null) videoMetadata = trackMetadata;
                    else videoMetadata.MergeReliableVideoTrack(trackMetadata);
                }
            }
            else if (handler == FourCc("soun")) hasAudio = true;
        }
        return childReader.AtEnd;
    }

    private static bool TryReadTrack(SpanReader reader, Box track, int depth, out uint handler, out bool avc, out VideoTrackMetadata? metadata)
    {
        handler = 0;
        avc = false;
        metadata = new VideoTrackMetadata();
        if (depth > 8) return false;
        var childReader = reader.SubReader(track.PayloadStart, track.End);
        while (childReader.TryReadTopLevel(out var child))
        {
            if (child.Type == FourCc("hdlr"))
            {
                var payload = childReader.Bytes(child);
                if (payload.Length < 12) return false;
                handler = BinaryPrimitives.ReadUInt32BigEndian(payload[8..]);
            }
            else if (child.Type == FourCc("stsd"))
            {
                var payload = childReader.Bytes(child);
                if (payload.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) == 0) return false;
                var entrySize = BinaryPrimitives.ReadUInt32BigEndian(payload[8..]);
                if (entrySize < 8 || entrySize > payload.Length - 8) return false;
                avc = AvcSampleEntries.Contains(BinaryPrimitives.ReadUInt32BigEndian(payload[12..]));
            }
            else if (child.Type == FourCc("tkhd"))
            {
                TryReadTkhd(childReader.Bytes(child), metadata);
            }
            else if (child.Type == FourCc("mdhd"))
            {
                if (TryReadTimeScale(childReader.Bytes(child), out var timeScale, out var duration))
                {
                    metadata.TimeScale = timeScale;
                    if (duration > 0) metadata.DurationMilliseconds = ToMilliseconds(timeScale, duration);
                }
            }
            else if (child.Type == FourCc("stts"))
            {
                TryReadFrameRate(childReader.Bytes(child), metadata);
            }
            else if (ContainerBoxes.Contains(child.Type))
            {
                if (!TryReadTrack(childReader, child, depth + 1, out var nestedHandler, out var nestedAvc, out var nestedMetadata)) return false;
                if (nestedHandler != 0) handler = nestedHandler;
                avc |= nestedAvc;
                metadata.Merge(nestedMetadata);
            }
        }
        return childReader.AtEnd;
    }

    private static bool TryReadDuration(ReadOnlySpan<byte> payload, out long? milliseconds)
    {
        milliseconds = null;
        if (!TryReadTimeScale(payload, out var timeScale, out var duration) || duration == 0) return false;
        milliseconds = ToMilliseconds(timeScale, duration);
        if (milliseconds is null) return false;
        return true;
    }

    private static long? ToMilliseconds(uint timeScale, ulong duration)
    {
        try { return checked((long)(checked(duration * 1000UL) / timeScale)); }
        catch (OverflowException) { return null; }
    }

    private static void MergeTrustedValue<T>(ref T? current, ref bool conflicted, T? candidate) where T : struct, IEquatable<T>
    {
        if (conflicted || candidate is null) return;
        if (current is null) current = candidate;
        else if (!current.Value.Equals(candidate.Value))
        {
            current = null;
            conflicted = true;
        }
    }

    private static bool TryReadTimeScale(ReadOnlySpan<byte> payload, out uint timeScale, out ulong duration)
    {
        timeScale = 0;
        duration = 0;
        if (payload.Length < 4) return false;
        var version = payload[0];
        var offset = version == 0 ? 12 : version == 1 ? 20 : -1;
        var durationLength = version == 1 ? 8 : 4;
        if (offset < 0 || payload.Length < offset + 4 + durationLength) return false;
        timeScale = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
        if (timeScale == 0) return false;
        duration = durationLength == 8 ? BinaryPrimitives.ReadUInt64BigEndian(payload[(offset + 4)..]) : BinaryPrimitives.ReadUInt32BigEndian(payload[(offset + 4)..]);
        return true;
    }

    private static bool TryReadTkhd(ReadOnlySpan<byte> payload, VideoTrackMetadata metadata)
    {
        if (payload.Length < 4) return false;
        var offset = payload[0] == 0 ? 76 : payload[0] == 1 ? 88 : -1;
        if (offset < 0 || payload.Length < offset + 8) return false;
        var widthFixed = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
        var heightFixed = BinaryPrimitives.ReadUInt32BigEndian(payload[(offset + 4)..]);
        if (widthFixed == 0 || heightFixed == 0 || (widthFixed & 0xffff) != 0 || (heightFixed & 0xffff) != 0) return true;
        metadata.Width = widthFixed >> 16;
        metadata.Height = heightFixed >> 16;
        return true;
    }

    private static bool TryReadFrameRate(ReadOnlySpan<byte> payload, VideoTrackMetadata metadata)
    {
        if (payload.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) != 1) return false;
        var sampleCount = BinaryPrimitives.ReadUInt32BigEndian(payload[8..]);
        var sampleDelta = BinaryPrimitives.ReadUInt32BigEndian(payload[12..]);
        if (sampleCount == 0 || sampleDelta == 0) return true;
        metadata.SampleDelta = sampleDelta;
        return true;
    }

    private sealed class VideoTrackMetadata
    {
        public long? DurationMilliseconds { get; set; }
        public uint? Width { get; set; }
        public uint? Height { get; set; }
        public uint? TimeScale { get; set; }
        public uint? SampleDelta { get; set; }
        public double? FrameRate => TimeScale is > 0 && SampleDelta is > 0 ? (double)TimeScale.Value / SampleDelta.Value : null;
        public uint? PublishedWidth => widthConflicted ? null : Width;
        public uint? PublishedHeight => heightConflicted ? null : Height;
        public double? PublishedFrameRate => frameRateConflicted ? null : FrameRate;
        private bool widthConflicted;
        private bool heightConflicted;
        private bool frameRateConflicted;

        public void MergeReliableVideoTrack(VideoTrackMetadata other)
        {
            var width = Width;
            MergeTrustedValue(ref width, ref widthConflicted, other.Width);
            Width = width;
            var height = Height;
            MergeTrustedValue(ref height, ref heightConflicted, other.Height);
            Height = height;
            var frameRate = FrameRate;
            MergeTrustedValue(ref frameRate, ref frameRateConflicted, other.FrameRate);
            if (!frameRateConflicted && FrameRate is null && frameRate is not null)
            {
                TimeScale = other.TimeScale;
                SampleDelta = other.SampleDelta;
            }
        }

        public void Merge(VideoTrackMetadata? other)
        {
            if (other is null) return;
            DurationMilliseconds ??= other.DurationMilliseconds;
            Width ??= other.Width;
            Height ??= other.Height;
            TimeScale ??= other.TimeScale;
            SampleDelta ??= other.SampleDelta;
        }
    }

    internal static uint FourCc(string value) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(value));

    private readonly record struct Box(uint Type, int PayloadStart, int End);

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> bytes;
        private int position;
        public SpanReader(ReadOnlySpan<byte> bytes) => this.bytes = bytes;
        public ReadOnlySpan<byte> Bytes(Box box) => bytes[box.PayloadStart..box.End];
        public bool AtEnd => position == bytes.Length;
        public SpanReader SubReader(int start, int end) => new(bytes[start..end]);

        public bool TryReadTopLevel(out Box box)
        {
            box = default;
            if (position == bytes.Length) return false;
            if (bytes.Length - position < 8) return false;
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(bytes[position..]);
            var type = BinaryPrimitives.ReadUInt32BigEndian(bytes[(position + 4)..]);
            var header = 8;
            ulong size = size32;
            if (size32 == 1)
            {
                if (bytes.Length - position < 16) return false;
                size = BinaryPrimitives.ReadUInt64BigEndian(bytes[(position + 8)..]);
                header = 16;
            }
            else if (size32 == 0) size = (ulong)(bytes.Length - position);
            if (size < (ulong)header || size > (ulong)(bytes.Length - position)) return false;
            var end = checked(position + (int)size);
            box = new Box(type, position + header, end);
            position = end;
            return true;
        }
    }
}
