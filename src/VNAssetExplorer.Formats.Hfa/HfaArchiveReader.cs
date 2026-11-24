using System.Buffers.Binary;
using System.Text;
using VNAssetExplorer.Core;

namespace VNAssetExplorer.Formats.Hfa;

public static class HfaArchiveReader
{
    private const int HeaderSize = 0x10;
    private const int EntrySize = 0x80;
    private const int NameSize = 0x60;
    private const int MetadataCount = 6;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HUNEXGGEFA10");
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// Reads metadata for the candidate HUNEXGGEFA10 layout without taking ownership of the stream.
    /// On success, the stream is positioned at the end of the directory table.
    /// </summary>
    public static HfaArchive Read(Stream stream, HfaReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new HfaReaderOptions();

        ValidateStream(stream);

        stream.Position = 0;
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);

        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new HfaFormatException(HfaErrorCode.WrongMagic, "The HFA magic is not recognized.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[Magic.Length..]);
        if (count > options.MaxEntryCount)
        {
            throw new HfaFormatException(HfaErrorCode.EntryCountTooLarge, "The HFA entry count exceeds the configured safety limit.");
        }

        var dataStart = checked(HeaderSize + (long)count * EntrySize);
        if (dataStart > stream.Length)
        {
            throw new HfaFormatException(HfaErrorCode.TruncatedDirectory, "The HFA directory is truncated.");
        }

        var entries = new List<HfaArchiveEntry>((int)count);
        var record = new byte[EntrySize];
        for (var index = 0U; index < count; index++)
        {
            stream.ReadExactly(record);
            entries.Add(ReadEntry(record, dataStart, stream.Length));
        }

        return new HfaArchive(dataStart, entries.AsReadOnly());
    }

    private static void ValidateStream(Stream stream)
    {
        if (!stream.CanRead)
        {
            throw new HfaFormatException(HfaErrorCode.StreamNotReadable, "The HFA stream is not readable.");
        }

        if (!stream.CanSeek)
        {
            throw new HfaFormatException(HfaErrorCode.StreamNotSeekable, "The HFA stream is not seekable.");
        }

        if (stream.Length < HeaderSize)
        {
            throw new HfaFormatException(HfaErrorCode.TruncatedHeader, "The HFA header is truncated.");
        }
    }

    private static HfaArchiveEntry ReadEntry(byte[] record, long dataStart, long archiveLength)
    {
        var name = ReadName(record);
        if (!HfaEntryName.IsSafe(name))
        {
            throw new HfaFormatException(HfaErrorCode.UnsafeEntryName, "The HFA entry name is unsafe for extraction.");
        }

        var relativeOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x60, sizeof(uint)));
        var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x64, sizeof(uint)));
        if (relativeOffset > archiveLength - dataStart
            || size > archiveLength - dataStart - relativeOffset)
        {
            throw new HfaFormatException(HfaErrorCode.EntryRangePastEnd, "The HFA entry range extends past the archive.");
        }

        var metadata = new uint[MetadataCount];
        for (var index = 0; index < metadata.Length; index++)
        {
            metadata[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                record.AsSpan(0x68 + index * sizeof(uint), sizeof(uint)));
        }

        var entry = new ArchiveEntry(name, dataStart + relativeOffset, size);
        return new HfaArchiveEntry(entry, Array.AsReadOnly(metadata));
    }

    private static string ReadName(byte[] record)
    {
        var nul = Array.IndexOf(record, (byte)0, 0, NameSize);
        var nameLength = nul >= 0 ? nul : NameSize;
        if (nul >= 0 && record.AsSpan(nul + 1, NameSize - nul - 1).ContainsAnyExcept((byte)0))
        {
            throw new HfaFormatException(HfaErrorCode.NonCanonicalNamePadding, "The HFA name field contains data after its terminator.");
        }

        try
        {
            return StrictUtf8.GetString(record, 0, nameLength);
        }
        catch (DecoderFallbackException error)
        {
            throw new HfaFormatException(HfaErrorCode.InvalidUtf8Name, "The HFA entry name is not valid UTF-8.", error);
        }
    }
}
