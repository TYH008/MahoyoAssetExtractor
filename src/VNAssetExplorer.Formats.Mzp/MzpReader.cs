namespace VNAssetExplorer.Formats.Mzp;

using System.Buffers.Binary;
using System.IO.Compression;

public enum MzpErrorCode
{
    WrongMagic,
    TruncatedHeader,
    TruncatedDirectory,
    EntryRangePastEnd,
    WrongMzxMagic,
    TruncatedMzxCommand,
    MzxOutputSizeMismatch,
    MzxOutputLimitExceeded,
    MzxCommandLimitExceeded,
    InvalidEntryRange,
    ResourceLimitExceeded,
    UnsupportedPreviewFormat,
}

public sealed class MzpFormatException(MzpErrorCode code, string message) : Exception(message)
{
    public MzpErrorCode Code { get; } = code;
}

public sealed record MzpEntry(long Offset, long SectorCount, long SizeBytesLow);

public sealed record MzpArchive(long DataStartOffset, IReadOnlyList<MzpEntry> Entries);

public static class MzpLimits
{
    public const int MaxInputBytes = 256 * 1024 * 1024;
    public const long MaxCanvasPixels = 16 * 1024 * 1024;
    public const long MaxRgbaBytes = 64 * 1024 * 1024;
}

internal static class MzpPipelineDiagnostics
{
    public static int FullPreviewCalls;
    public static int MzxDecodeCalls;
    public static int HepConversionCalls;
    public static int RgbaCompositionCalls;
    public static int PngEncodingCalls;

    public static void Reset()
    {
        FullPreviewCalls = 0;
        MzxDecodeCalls = 0;
        HepConversionCalls = 0;
        RgbaCompositionCalls = 0;
        PngEncodingCalls = 0;
    }

    public static void PreviewPipelineStarted()
    {
        Interlocked.Increment(ref FullPreviewCalls);
    }
}

public sealed record MzpImageMetadata(
    uint Width,
    uint Height,
    uint TileWidth,
    uint TileHeight,
    uint TileXCount,
    uint TileYCount,
    ushort BmpType,
    byte BmpDepth,
    byte TileCrop);

public sealed record MzpImage(uint Width, uint Height, ReadOnlyMemory<byte> Rgba);

public static class MzpPngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Encode(MzpImage image)
    {
        Interlocked.Increment(ref MzpPipelineDiagnostics.PngEncodingCalls);
        ArgumentNullException.ThrowIfNull(image);
        var width = checked((int)image.Width);
        var height = checked((int)image.Height);
        if (image.Rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException("The RGBA buffer does not match the image dimensions.", nameof(image));
        }

        using var result = new MemoryStream();
        result.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], image.Height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(result, "IHDR"u8, ihdr);

        var scanlines = new byte[checked(height * (width * 4 + 1))];
        for (var row = 0; row < height; row++)
        {
            var destination = row * (width * 4 + 1);
            image.Rgba.Span.Slice(row * width * 4, width * 4).CopyTo(scanlines.AsSpan(destination + 1));
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(scanlines);
        }

        WriteChunk(result, "IDAT"u8, compressed.ToArray());
        WriteChunk(result, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return result.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ComputeCrc(type, data));
        stream.Write(checksum);
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var value in type)
        {
            crc = UpdateCrc(crc, value);
        }
        foreach (var value in data)
        {
            crc = UpdateCrc(crc, value);
        }
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc;
    }
}

public static class MzpImageComposer
{
    public static MzpImage Compose(MzpImageMetadata metadata, IReadOnlyList<byte[]> tiles)
    {
        Interlocked.Increment(ref MzpPipelineDiagnostics.RgbaCompositionCalls);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(tiles);
        var expectedTileCount = (ulong)metadata.TileXCount * metadata.TileYCount;
        if (expectedTileCount > int.MaxValue)
        {
            throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, "The MZP tile count exceeds the preview resource limit.");
        }

        var expectedTiles = (int)expectedTileCount;
        if (tiles.Count != expectedTiles)
        {
            throw new ArgumentException("The tile count does not match the image metadata.", nameof(tiles));
        }

        var width = checked((int)metadata.Width);
        var height = checked((int)metadata.Height);
        var canvasPixels = (long)width * height;
        if (canvasPixels > MzpLimits.MaxCanvasPixels || canvasPixels * 4 > MzpLimits.MaxRgbaBytes)
        {
            throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, "The MZP image exceeds the preview resource limit.");
        }
        var tileWidth = checked((int)metadata.TileWidth);
        var tileHeight = checked((int)metadata.TileHeight);
        var crop = metadata.TileCrop;
        var rgba = new byte[checked(width * height * 4)];
        for (var y = 0; y < metadata.TileYCount; y++)
        {
            for (var x = 0; x < metadata.TileXCount; x++)
            {
                var tile = tiles[checked((int)(y * metadata.TileXCount + x))];
                var sourceRowStart = y == 0 ? 0 : crop;
                var sourceRowEnd = y == metadata.TileYCount - 1 ? tileHeight : tileHeight - crop;
                var sourceColumnStart = x == 0 ? 0 : crop;
                var sourceColumnEnd = x == metadata.TileXCount - 1 ? tileWidth : tileWidth - crop;
                var destinationX = checked((int)(x * (tileWidth - crop * 2) + sourceColumnStart));
                var destinationY = checked((int)(y * (tileHeight - crop * 2) + sourceRowStart));
                for (var sourceY = sourceRowStart; sourceY < sourceRowEnd; sourceY++)
                {
                    var count = sourceColumnEnd - sourceColumnStart;
                    var sourceOffset = checked((sourceY * tileWidth + sourceColumnStart) * 4);
                    var destinationOffset = checked(((destinationY + sourceY - sourceRowStart) * width + destinationX) * 4);
                    tile.AsSpan(sourceOffset, count * 4).CopyTo(rgba.AsSpan(destinationOffset, count * 4));
                }
            }
        }

        return new MzpImage(metadata.Width, metadata.Height, rgba);
    }
}

public static class MzpImageReader
{
    public static MzpImageMetadata ReadMetadata(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length > MzpLimits.MaxInputBytes)
            throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, "The MZP metadata stream exceeds the resource limit.");
        return ReadMetadataHeader(stream, stream.Length, default, enforceResourceLimits: false);
    }

    public static MzpImageMetadata ReadMetadataHeader(Stream stream, long entryLength, CancellationToken cancellationToken = default)
        => ReadMetadataHeader(stream, entryLength, cancellationToken, enforceResourceLimits: true);

    public static MzpImageMetadata ReadBulkMetadataHeader(Stream stream, long entryLength, CancellationToken cancellationToken = default)
        => ReadMetadataHeader(stream, entryLength, cancellationToken, enforceResourceLimits: true, bulk: true);

    private static MzpImageMetadata ReadMetadataHeader(Stream stream, long entryLength, CancellationToken cancellationToken, bool enforceResourceLimits, bool bulk = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek || entryLength < 0 || entryLength > stream.Length - stream.Position)
            throw new MzpFormatException(MzpErrorCode.TruncatedHeader, "The MZP metadata source is truncated.");
        cancellationToken.ThrowIfCancellationRequested();
        if (bulk && entryLength > MzpLimits.MaxInputBytes)
            throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, "The MZP input exceeds the bulk resource limit.");
        var entryStart = stream.Position;
        using var bounded = new BoundedReadStream(stream, entryStart, entryLength);
        var archive = MzpReader.Read(bounded, entryLength);
        if (archive.Entries.Count == 0)
            throw new MzpFormatException(MzpErrorCode.TruncatedDirectory, "The MZP image has no metadata entry.");
        if (archive.Entries.Count > 1)
            _ = MzpReader.GetEntryRanges(archive, entryLength);
            var metadataStart = checked(archive.DataStartOffset + archive.Entries[0].Offset);
            var metadataEnd = archive.Entries.Count > 1
                ? checked(archive.DataStartOffset + archive.Entries[1].Offset)
                : entryLength;
        if (metadataStart < archive.DataStartOffset || metadataEnd <= metadataStart || metadataEnd > entryLength)
            throw new MzpFormatException(MzpErrorCode.InvalidEntryRange, "The MZP metadata entry range is invalid.");
        var metadataLength = checked(metadataEnd - metadataStart);
        if (metadataLength < 16)
            throw new MzpFormatException(MzpErrorCode.EntryRangePastEnd, "The MZP image metadata entry is truncated.");
        bounded.Position = metadataStart;
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[16];
        bounded.ReadExactly(header);
        var result = new MzpImageMetadata(
            BinaryPrimitives.ReadUInt16LittleEndian(header), BinaryPrimitives.ReadUInt16LittleEndian(header[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[4..]), BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[8..]), BinaryPrimitives.ReadUInt16LittleEndian(header[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[12..]), header[14], header[15]);
        if (enforceResourceLimits) ValidateMetadata(result, bulk);
        if (bulk) MzpBulkReader.ValidateBudget(result);
        return result;
    }

    private static void ValidateMetadata(MzpImageMetadata result, bool bulk = false)
    {
        if (result.Width == 0 || result.Height == 0 || result.TileWidth == 0 || result.TileHeight == 0 || result.TileXCount == 0 || result.TileYCount == 0)
            throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, "The MZP image dimensions or tile geometry are invalid.");
        var pixels = checked((ulong)result.Width * result.Height);
        var rgba = checked(pixels * 4);
        var tiles = checked((ulong)result.TileXCount * result.TileYCount);
        if (pixels > (ulong)(bulk ? MzpBulkReader.MaxRgbaBytes / 4 : MzpLimits.MaxCanvasPixels)
            || rgba > (ulong)(bulk ? MzpBulkReader.MaxRgbaBytes : MzpLimits.MaxRgbaBytes) || tiles > 1_000_000)
            throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, bulk
                ? "The MZP image exceeds the bulk resource limit."
                : "The MZP image exceeds the preview resource limit.");

        try
        {
            var crop = (ulong)result.TileCrop;
            var tileWidth = (ulong)result.TileWidth;
            var tileHeight = (ulong)result.TileHeight;

            // MzpImageComposer keeps the complete outer edge when an axis has
            // one tile. Crop is used only between adjacent tiles on that axis.
            var spanWidth = result.TileXCount > 1
                ? checked(tileWidth - checked(crop * 2))
                : tileWidth;
            var spanHeight = result.TileYCount > 1
                ? checked(tileHeight - checked(crop * 2))
                : tileHeight;
            if ((result.TileXCount > 1 && spanWidth == 0) || (result.TileYCount > 1 && spanHeight == 0))
                throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, "The MZP tile grid has no drawable span between adjacent tiles.");

            // The declared canvas may clip padding at the outer edges of the
            // tile grid. Require the raw grid to contain the canvas; do not
            // require the drawable-span formula to equal it exactly.
            var coveredWidth = checked((ulong)result.TileXCount * tileWidth);
            var coveredHeight = checked((ulong)result.TileYCount * tileHeight);
            if (coveredWidth < result.Width || coveredHeight < result.Height)
                throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, "The MZP tile grid cannot cover the declared canvas.");
        }
        catch (OverflowException)
        {
            throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, "The MZP tile grid exceeds the preview resource limit.");
        }
    }

    private sealed class BoundedReadStream(Stream inner, long origin, long length) : Stream
    {
        private long position;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => position = value; }
        public override int Read(byte[] buffer, int offset, int count) { var n = (int)Math.Min(count, length - position); if (n <= 0) return 0; inner.Position = origin + position; var read = inner.Read(buffer, offset, n); position += read; return read; }
        public override int Read(Span<byte> buffer) { var n = Math.Min(buffer.Length, checked((int)(length - position))); if (n <= 0) return 0; inner.Position = origin + position; var read = inner.Read(buffer[..n]); position += read; return read; }
        public override long Seek(long offset, SeekOrigin originKind) { var next = originKind switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, SeekOrigin.End => length + offset, _ => throw new ArgumentOutOfRangeException(nameof(originKind)) }; if (next < 0 || next > length) throw new IOException(); position = next; return position; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public enum HepErrorCode
{
    WrongMagic,
    TruncatedHeader,
    DeclaredSizeMismatch,
    InvalidDimensions,
}

public sealed class HepFormatException(HepErrorCode code, string message) : Exception(message)
{
    public HepErrorCode Code { get; } = code;
}

public sealed record HepMetadata(long DeclaredSize, uint Width, uint Height, uint Transparency);

public sealed record HepTile(HepMetadata Metadata, ReadOnlyMemory<byte> Indices, ReadOnlyMemory<byte> Palette);

public static class HepReader
{
    private const int HeaderSize = 0x20;
    private const int PaletteSize = 0x400;
    private static readonly byte[] Magic = "HEP\0"u8.ToArray();

    public static HepMetadata Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The HEP stream must be readable and seekable.", nameof(stream));
        }

        stream.Position = 0;
        Span<byte> header = stackalloc byte[HeaderSize];
        try
        {
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            throw new HepFormatException(HepErrorCode.TruncatedHeader, "The HEP header is truncated.");
        }

        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new HepFormatException(HepErrorCode.WrongMagic, "The HEP magic is not recognized.");
        }

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(header[0x14..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(header[0x18..]);
        var transparency = BinaryPrimitives.ReadUInt32LittleEndian(header[0x1C..]);
        if (width == 0 || height == 0)
        {
            throw new HepFormatException(HepErrorCode.InvalidDimensions, "The HEP dimensions must be non-zero.");
        }

        var expectedSize = checked((ulong)HeaderSize + (ulong)width * height + PaletteSize);
        if (expectedSize != declaredSize || declaredSize != (ulong)stream.Length)
        {
            throw new HepFormatException(HepErrorCode.DeclaredSizeMismatch, "The HEP declared size does not match its dimensions or stream length.");
        }

        return new HepMetadata(declaredSize, width, height, transparency);
    }

    public static byte MapAlpha(byte alpha)
    {
        return (alpha & 0x80) != 0
            ? byte.MaxValue
            : (byte)((alpha << 1) | (alpha >> 6));
    }

    public static byte[] ToRgba(HepTile tile)
    {
        Interlocked.Increment(ref MzpPipelineDiagnostics.HepConversionCalls);
        ArgumentNullException.ThrowIfNull(tile);
        var rgba = new byte[checked(tile.Indices.Length * 4)];
        for (var index = 0; index < tile.Indices.Length; index++)
        {
            var paletteOffset = tile.Indices.Span[index] * 4;
            var outputOffset = index * 4;
            tile.Palette.Span.Slice(paletteOffset, 3).CopyTo(rgba.AsSpan(outputOffset, 3));
            rgba[outputOffset + 3] = MapAlpha(tile.Palette.Span[paletteOffset + 3]);
        }

        return rgba;
    }

    public static HepTile ReadTile(Stream stream)
    {
        var metadata = Read(stream);
        var pixelCount = checked((int)((ulong)metadata.Width * metadata.Height));
        var indices = new byte[pixelCount];
        stream.Position = HeaderSize;
        stream.ReadExactly(indices);
        var palette = new byte[PaletteSize];
        stream.ReadExactly(palette);
        return new HepTile(metadata, indices, palette);
    }
}

public static class MzpReader
{
    private const int HeaderSize = 8;
    private const int EntrySize = 8;
    private const int SectorSize = 0x800;
    private static readonly byte[] Magic = "mrgd00"u8.ToArray();

    public static MzpArchive Read(Stream stream) => Read(stream, stream.Length);

    public static MzpArchive Read(Stream stream, long availableLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The MZP stream must be readable and seekable.", nameof(stream));
        }

        stream.Position = 0;
        Span<byte> header = stackalloc byte[HeaderSize];
        try
        {
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            throw new MzpFormatException(MzpErrorCode.TruncatedHeader, "The MZP header is truncated.");
        }

        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new MzpFormatException(MzpErrorCode.WrongMagic, "The MZP magic is not recognized.");
        }

        var count = BitConverter.ToUInt16(header[Magic.Length..]);
        var dataStart = checked((long)HeaderSize + (long)count * EntrySize);
        if (dataStart > availableLength)
        {
            throw new MzpFormatException(MzpErrorCode.TruncatedDirectory, "The MZP directory is truncated.");
        }

        var entries = new List<MzpEntry>(count);
        Span<byte> record = stackalloc byte[EntrySize];
        for (var index = 0; index < count; index++)
        {
            stream.ReadExactly(record);
            var sectorOffset = BitConverter.ToUInt16(record);
            var byteOffset = BitConverter.ToUInt16(record[2..]);
            var sectorCount = BitConverter.ToUInt16(record[4..]);
            var sizeBytesLow = BitConverter.ToUInt16(record[6..]);
            if (byteOffset >= SectorSize)
            {
                throw new MzpFormatException(MzpErrorCode.EntryRangePastEnd, "The MZP byte offset is outside its sector.");
            }

            var relativeOffset = checked((long)sectorOffset * SectorSize + byteOffset);
            var absoluteOffset = checked(dataStart + relativeOffset);
            if (absoluteOffset >= availableLength)
            {
                throw new MzpFormatException(MzpErrorCode.EntryRangePastEnd, "The MZP entry starts past the file end.");
            }

            entries.Add(new MzpEntry(relativeOffset, sectorCount, sizeBytesLow));
        }

        return new MzpArchive(dataStart, entries.AsReadOnly());
    }

    public static (int Start, int Length)[] GetEntryRanges(MzpArchive archive, long streamLength)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (streamLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamLength));
        }

        try
        {
            var starts = archive.Entries
                .Select(entry => checked(archive.DataStartOffset + entry.Offset))
                .ToArray();
            var ranges = new (int Start, int Length)[Math.Max(0, starts.Length - 1)];
            for (var index = 0; index < starts.Length; index++)
            {
                if (starts[index] < archive.DataStartOffset || starts[index] >= streamLength
                    || (index > 0 && starts[index] <= starts[index - 1]))
                {
                    throw new MzpFormatException(MzpErrorCode.InvalidEntryRange, "The MZP entries do not form strictly increasing file ranges.");
                }

                if (index == 0)
                {
                    continue;
                }

                var end = index + 1 < starts.Length ? starts[index + 1] : streamLength;
                if (end <= starts[index] || end > streamLength)
                {
                    throw new MzpFormatException(MzpErrorCode.InvalidEntryRange, "The MZP entry range is empty or outside the file.");
                }

                ranges[index - 1] = (checked((int)starts[index]), checked((int)(end - starts[index])));
            }

            return ranges;
        }
        catch (OverflowException)
        {
            throw new MzpFormatException(MzpErrorCode.InvalidEntryRange, "The MZP entry range cannot be represented safely.");
        }
    }
}

public static class MzxDecoder
{
    private const int DefaultMaxOutputSize = 64 * 1024 * 1024;
    private const int DefaultMaxCommandCount = 1_000_000;
    private static readonly byte[] Magic = "MZX0"u8.ToArray();

    public static byte[] Decode(
        Stream stream,
        int maxOutputSize = DefaultMaxOutputSize,
        int maxCommandCount = DefaultMaxCommandCount,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref MzpPipelineDiagnostics.MzxDecodeCalls);
        ArgumentNullException.ThrowIfNull(stream);
        if (maxOutputSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputSize));
        }
        if (maxCommandCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCommandCount));
        }
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The MZX stream must be readable and seekable.", nameof(stream));
        }

        stream.Position = 0;
        Span<byte> header = stackalloc byte[8];
        try
        {
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            throw new MzpFormatException(MzpErrorCode.TruncatedMzxCommand, "The MZX header is truncated.");
        }

        if (!header[..4].SequenceEqual(Magic))
        {
            throw new MzpFormatException(MzpErrorCode.WrongMzxMagic, "The MZX magic is not recognized.");
        }

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (declaredSize > (uint)maxOutputSize)
        {
            throw new MzpFormatException(MzpErrorCode.MzxOutputLimitExceeded, "The MZX output exceeds the configured limit.");
        }

        if (declaredSize > int.MaxValue)
        {
            throw new MzpFormatException(MzpErrorCode.MzxOutputSizeMismatch, "The MZX output is too large.");
        }

        var output = new List<byte>((int)declaredSize);
        var ringBuffer = new byte[128];
        var ringPosition = 0;
        var commandCount = 0;
        while (output.Count < declaredSize)
        {
            if ((commandCount & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (commandCount++ >= maxCommandCount)
            {
                throw new MzpFormatException(MzpErrorCode.MzxCommandLimitExceeded, "The MZX command count exceeds the configured limit.");
            }

            var flags = stream.ReadByte();
            if (flags < 0)
            {
                throw new MzpFormatException(MzpErrorCode.TruncatedMzxCommand, "The MZX command stream is truncated.");
            }

            var command = flags & 0x03;
            var wordCount = (flags >> 2) + 1;
            if (command == 0)
            {
                var word = output.Count >= 2
                    ? new[] { output[^2], output[^1] }
                    : new byte[2];
                var remainingWords = ((int)declaredSize - output.Count + 1) / 2;
                if (wordCount > remainingWords)
                {
                    throw new MzpFormatException(MzpErrorCode.MzxOutputSizeMismatch, "The MZX RLE command exceeds the declared output size.");
                }

                var repeatCount = wordCount;
                for (var index = 0; index < repeatCount; index++)
                {
                    output.Add(word[0]);
                    output.Add(word[1]);
                }

                continue;
            }

            if (command == 2)
            {
                var ringOffset = (wordCount - 1) * 2;
                for (var index = 0; index < 2 && output.Count < declaredSize; index++)
                {
                    output.Add(ringBuffer[(ringOffset + index) % ringBuffer.Length]);
                }

                continue;
            }

            if (command == 1)
            {
                var distanceByte = stream.ReadByte();
                if (distanceByte < 0)
                {
                    throw new MzpFormatException(MzpErrorCode.TruncatedMzxCommand, "The MZX back-reference is truncated.");
                }

                var distance = (distanceByte + 1) * 2;
                if (distance > output.Count)
                {
                    throw new MzpFormatException(MzpErrorCode.MzxOutputSizeMismatch, "The MZX back-reference exceeds decoded output.");
                }

                var copy = new byte[distance];
                output.CopyTo(output.Count - distance, copy, 0, distance);
                var backrefRemaining = (int)declaredSize - output.Count;
                var requestedBytes = wordCount * 2;
                if (requestedBytes - backrefRemaining > 2)
                {
                    throw new MzpFormatException(MzpErrorCode.MzxOutputSizeMismatch, "The MZX back-reference exceeds the declared output size.");
                }

                var copyWords = Math.Min(wordCount, (backrefRemaining + 1) / 2);
                for (var index = 0; index < copyWords * 2; index++)
                {
                    output.Add(copy[index % distance]);
                }

                continue;
            }

            if (command != 3)
            {
                throw new MzpFormatException(MzpErrorCode.TruncatedMzxCommand, "This MZX slice only supports literal and RLE commands.");
            }

            var literal = new byte[checked(wordCount * 2)];
            var remainingBeforeLiteral = (int)declaredSize - output.Count;
            try
            {
                stream.ReadExactly(literal);
            }
            catch (EndOfStreamException)
            {
                throw new MzpFormatException(MzpErrorCode.TruncatedMzxCommand, "The MZX literal command is truncated.");
            }

            if (literal.Length > remainingBeforeLiteral)
            {
                if (output.Count > 0)
                {
                    output.AddRange(literal.AsSpan(0, remainingBeforeLiteral).ToArray());
                    break;
                }

                throw new MzpFormatException(MzpErrorCode.MzxOutputSizeMismatch, "The MZX command exceeds the declared output size.");
            }

            var remaining = (int)declaredSize - output.Count;
            output.AddRange(literal.AsSpan(0, Math.Min(remaining, literal.Length)).ToArray());
            foreach (var value in literal)
            {
                ringBuffer[ringPosition] = value;
                ringPosition = (ringPosition + 1) % ringBuffer.Length;
            }
        }

        return output.ToArray();
    }
}
