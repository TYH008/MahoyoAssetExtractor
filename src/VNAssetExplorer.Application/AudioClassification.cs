using System.Buffers.Binary;

namespace VNAssetExplorer.Application;

public sealed record ClassifiedAudio(
    string RootPath,
    string ArchivePath,
    string ArchiveName,
    int ArchiveOrdinal,
    int ArchiveEntryIndex,
    int ArchiveEntryCount,
    int ResourceOrdinal,
    string Name,
    long Size,
    string Type)
{
    public string SourcePosition => $"第 {ArchiveEntryIndex + 1} / {ArchiveEntryCount} 项";
}

public sealed record AudioClassificationResult(
    string RootPath,
    IReadOnlyList<ClassifiedAudio> Audio,
    int ScannedEntryCount,
    int UnsupportedEntryCount,
    int FailedEntryCount,
    bool IsComplete,
    bool IsCancelled);

public sealed class AudioClassificationService
{
    public Task<AudioClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return Task.Run(() => Classify(inventory, cancellationToken), CancellationToken.None);
    }

    private static AudioClassificationResult Classify(GameRootInventoryResult inventory, CancellationToken cancellationToken)
    {
        var audio = new List<ClassifiedAudio>();
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
                        var prefix = session.ReadEntryPrefix(entryIndex, ArchiveSession.MaxEntryPrefixBytes, cancellationToken);
                        if (TryProbeHwOggVorbis(prefix, entry.Size, out _))
                        {
                            audio.Add(new ClassifiedAudio(rootPath, archive.FullPath, archive.Name, archiveOrdinal + 1, entryIndex, session.Entries.Count, resourceOrdinal, entry.Name, entry.Size, "Ogg Vorbis"));
                        }
                        else unsupported++;
                    }
                    catch (ApplicationException error) when (error.Code == ApplicationErrorCode.Cancelled && cancellationToken.IsCancellationRequested)
                    {
                        return new AudioClassificationResult(rootPath, audio.AsReadOnly(), scanned, unsupported, failed, false, true);
                    }
                    catch (ApplicationException) when (!cancellationToken.IsCancellationRequested)
                    {
                        failed++;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AudioClassificationResult(rootPath, audio.AsReadOnly(), scanned, unsupported, failed, false, true);
        }

        return new AudioClassificationResult(rootPath, audio.AsReadOnly(), scanned, unsupported, failed, true, false);
    }

    public static bool TryProbeHwOggVorbis(ReadOnlySpan<byte> bytes, long entrySize, out HwAudioProbe probe)
    {
        probe = default;
        if (entrySize < 64 || bytes.Length < 64 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 64 || !bytes.Slice(4, 4).SequenceEqual("hw  "u8)) return false;
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8));
        var totalSamples = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12));
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(16));
        var channels = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(20));
        var loopFlag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24));
        var loopStart = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(28));
        var loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(32));
        if (payloadLength != entrySize - 64 || totalSamples == 0 || sampleRate != 48000 || channels is < 1 or > 2 || !bytes.Slice(36, 28).SequenceEqual(stackalloc byte[28])) return false;
        if (loopFlag == 0 ? loopStart != 0 || loopEnd != 0 : loopFlag != 1 || loopStart >= loopEnd || loopEnd > totalSamples) return false;
        var payload = bytes[64..];
        if (payload.Length < 28 || !payload[..4].SequenceEqual("OggS"u8) || payload[5] != 2 || payload[26] == 0 || payload[27] < 30) return false;
        var packet = payload.Slice(28);
        if (packet.Length < 30 || packet[0] != 1 || !packet.Slice(1, 6).SequenceEqual("vorbis"u8) || packet[11] != channels || BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(12)) != sampleRate) return false;
        probe = new HwAudioProbe(payloadLength, totalSamples, sampleRate, channels, loopFlag == 1, loopStart, loopEnd);
        return true;
    }
}

public readonly record struct HwAudioProbe(uint PayloadLength, uint TotalSamples, uint SampleRate, uint Channels, bool HasLoop, uint LoopStart, uint LoopEnd);
