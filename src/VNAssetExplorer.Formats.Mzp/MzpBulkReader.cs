using System.Buffers.Binary;
using System.IO.Compression;

namespace VNAssetExplorer.Formats.Mzp;

public enum MzpBulkPhase { Decode, Compose, Encode }

public static class MzpBulkReader
{
    public const long MaxRgbaBytes = 256L * 1024 * 1024;
    public const int MaxTilePixels = 4 * 1024 * 1024;
    public const long MaxWorkingBytes = 320L * 1024 * 1024;

    public static bool Supports(MzpImageMetadata metadata) =>
        (metadata.BmpType == 0x000C && metadata.BmpDepth == 0x11)
        || (metadata.BmpType == 0x000B && metadata.BmpDepth == 0x14);

    internal static void ValidateBudget(MzpImageMetadata metadata)
    {
        var tilePixels = (ulong)metadata.TileWidth * metadata.TileHeight;
        // Live data: canvas, MZX list + returned HEP + indices, bounded IDAT buffer.
        var tileWorkingBytes = metadata.BmpType == 0x000B && metadata.BmpDepth == 0x14 ? 8UL : 3UL;
        var working = (ulong)metadata.Width * metadata.Height * 4 + tilePixels * tileWorkingBytes + 2 * 1024 * 1024;
        if (tilePixels > MaxTilePixels || working > MaxWorkingBytes)
            throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, "The MZP image exceeds the serial bulk working budget.");
    }

    public static void WritePng(Stream source, long entryLength, Stream destination,
        CancellationToken token = default, Action<MzpBulkPhase>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var origin = source.Position;
        var metadata = MzpImageReader.ReadBulkMetadataHeader(source, entryLength, token);
        if (!Supports(metadata))
            throw new MzpFormatException(MzpErrorCode.UnsupportedPreviewFormat, "The MZP image variant is not supported for bulk export.");
        using var entry = new SliceStream(source, origin, entryLength);
        var archive = MzpReader.Read(entry);
        var ranges = MzpReader.GetEntryRanges(archive, entryLength);
        if ((ulong)ranges.Length != (ulong)metadata.TileXCount * metadata.TileYCount)
            throw new MzpFormatException(MzpErrorCode.InvalidEntryRange, "The MZP tile count does not match its grid.");
        var canvas = new byte[checked((int)((ulong)metadata.Width * metadata.Height * 4))];
        for (var index = 0; index < ranges.Length; index++)
        {
            progress?.Invoke(MzpBulkPhase.Decode);
            token.ThrowIfCancellationRequested();
            var range = ranges[index];
            using var compressed = new SliceStream(source, origin + range.Start, range.Length);
            var direct = metadata.BmpType == 0x000B;
            var tilePixels = checked((int)(metadata.TileWidth * metadata.TileHeight));
            var decoded = MzxDecoder.Decode(compressed,
                direct ? checked(tilePixels * 4) : checked(tilePixels + 0x420), cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (direct)
            {
                if (decoded.Length != tilePixels * 4)
                    throw new MzpFormatException(MzpErrorCode.MzxOutputSizeMismatch, "The direct-color tile length does not match its three planes.");
                ComposeTile(canvas, metadata, index, null, decoded, token, progress);
            }
            else
            {
                using var hepStream = new MemoryStream(decoded, writable: false);
                var tile = HepReader.ReadTile(hepStream);
                ComposeTile(canvas, metadata, index, tile, null, token, progress);
            }
        }
        token.ThrowIfCancellationRequested();
        Encode(metadata, canvas, destination, token, progress);
    }

    private static void ComposeTile(byte[] canvas, MzpImageMetadata m, int index, HepTile? tile, byte[]? direct,
        CancellationToken token, Action<MzpBulkPhase>? progress)
    {
        if (tile is not null && (tile.Metadata.Width != m.TileWidth || tile.Metadata.Height != m.TileHeight))
            throw new HepFormatException(HepErrorCode.InvalidDimensions, "The HEP tile dimensions do not match the MZP grid.");
        var x = index % (int)m.TileXCount;
        var y = index / (int)m.TileXCount;
        var tw = (int)m.TileWidth; var th = (int)m.TileHeight; var crop = m.TileCrop;
        var sx = x == 0 ? 0 : crop; var sy = y == 0 ? 0 : crop;
        var ex = x == m.TileXCount - 1 ? tw : tw - crop;
        var ey = y == m.TileYCount - 1 ? th : th - crop;
        var dx = x * (tw - crop * 2) + sx;
        var dy = y * (th - crop * 2) + sy;
        for (var row = sy; row < ey && dy + row - sy < m.Height; row++)
        {
            progress?.Invoke(MzpBulkPhase.Compose);
            token.ThrowIfCancellationRequested();
            for (var col = sx; col < ex && dx + col - sx < m.Width; col++)
            {
                var pixel = row * tw + col;
                var offset = checked(((dy + row - sy) * (int)m.Width + dx + col - sx) * 4);
                if (direct is not null)
                {
                    // RGB high bits are packed 5/6/5, followed by a 3/2/3 low-bit
                    // plane and an unscaled alpha plane. This is not interleaved RGBA.
                    var count = tw * th;
                    var high = BinaryPrimitives.ReadUInt16LittleEndian(direct.AsSpan(pixel * 2, 2));
                    var low = direct[count * 2 + pixel];
                    canvas[offset] = (byte)(((high >> 11) << 3) | (low >> 5));
                    canvas[offset + 1] = (byte)((((high >> 5) & 63) << 2) | ((low >> 3) & 3));
                    canvas[offset + 2] = (byte)(((high & 31) << 3) | (low & 7));
                    canvas[offset + 3] = direct[count * 3 + pixel];
                }
                else if (tile is not null)
                {
                    var palette = tile.Indices.Span[pixel] * 4;
                    tile.Palette.Span.Slice(palette, 3).CopyTo(canvas.AsSpan(offset, 3));
                    canvas[offset + 3] = HepReader.MapAlpha(tile.Palette.Span[palette + 3]);
                }
            }
        }
    }

    private static void Encode(MzpImageMetadata m, byte[] canvas, Stream destination, CancellationToken token, Action<MzpBulkPhase>? progress)
    {
        destination.Write(new byte[] {137,80,78,71,13,10,26,10});
        Span<byte> ihdr = stackalloc byte[13];
        ihdr.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, m.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], m.Height);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(destination, "IHDR"u8, ihdr);
        using var chunks = new IdatStream(destination, token);
        using (var zlib = new ZLibStream(chunks, CompressionLevel.Fastest, leaveOpen: true))
        {
            var rowBytes = checked((int)m.Width * 4);
            for (var row = 0; row < m.Height; row++)
            {
                progress?.Invoke(MzpBulkPhase.Encode);
                token.ThrowIfCancellationRequested();
                zlib.WriteByte(0);
                zlib.Write(canvas.AsSpan(checked(row * rowBytes), rowBytes));
            }
        }
        chunks.Flush();
        token.ThrowIfCancellationRequested();
        Chunk(destination, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        var crc = (uint)i;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }).ToArray();

    private static void Chunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        destination.Write(word); destination.Write(type); destination.Write(data);
        var crc = uint.MaxValue;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); destination.Write(word);
    }

    private sealed class IdatStream(Stream destination, CancellationToken token) : Stream
    {
        private readonly byte[] buffer = new byte[64 * 1024];
        private int used;
        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                var n = Math.Min(data.Length, buffer.Length - used);
                data[..n].CopyTo(buffer.AsSpan(used)); used += n; data = data[n..];
                if (used == buffer.Length) Flush();
            }
        }
        public override void Flush() { token.ThrowIfCancellationRequested(); if (used > 0) { Chunk(destination, "IDAT"u8, buffer.AsSpan(0, used)); used = 0; } }
        public override void Write(byte[] b, int o, int n) => Write(b.AsSpan(o,n));
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
    }

    private sealed class SliceStream(Stream source, long origin, long length) : Stream
    {
        private long position;
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set { if (value < 0 || value > length) throw new IOException("Invalid slice position."); position = value; } }
        public override int Read(Span<byte> data) { var n=(int)Math.Min(data.Length,length-position); source.Position=origin+position; var read=source.Read(data[..n]); position+=read; return read; }
        public override int Read(byte[] b,int o,int n)=>Read(b.AsSpan(o,n));
        public override long Seek(long o,SeekOrigin s) { Position=s switch {SeekOrigin.Begin=>o,SeekOrigin.Current=>position+o,SeekOrigin.End=>length+o,_=>throw new ArgumentOutOfRangeException(nameof(s))}; return position; }
        public override void Flush() {} public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException(); public override void SetLength(long n)=>throw new NotSupportedException();
    }
}
