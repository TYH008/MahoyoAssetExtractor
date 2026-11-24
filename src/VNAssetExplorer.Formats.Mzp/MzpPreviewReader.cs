namespace VNAssetExplorer.Formats.Mzp;

public static class MzpPreviewReader
{
    public static MzpPreview Read(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The MZP preview stream must be readable and seekable.", nameof(stream));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Length > MzpLimits.MaxInputBytes)
        {
            throw new MzpFormatException(MzpErrorCode.ResourceLimitExceeded, "The MZP input exceeds the preview resource limit.");
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = buffer.ToArray();
        using var mzpStream = new MemoryStream(bytes, writable: false);
        var archive = MzpReader.Read(mzpStream);
        var metadata = MzpImageReader.ReadMetadata(new MemoryStream(bytes, writable: false));
        if (metadata.BmpType != 0x000C || metadata.BmpDepth != 0x11)
        {
            throw new MzpFormatException(MzpErrorCode.UnsupportedPreviewFormat, "The MZP image variant is not supported.");
        }

        var ranges = MzpReader.GetEntryRanges(archive, bytes.Length);
        var tiles = new List<byte[]>(ranges.Length);
        foreach (var range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var compressed = new MemoryStream(bytes, range.Start, range.Length, writable: false);
            var decoded = MzxDecoder.Decode(compressed);
            cancellationToken.ThrowIfCancellationRequested();
            var hep = HepReader.ReadTile(new MemoryStream(decoded, writable: false));
            tiles.Add(HepReader.ToRgba(hep));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var image = MzpImageComposer.Compose(metadata, tiles);
        return new MzpPreview(metadata.Width, metadata.Height, MzpPngEncoder.Encode(image));
    }
}

public sealed record MzpPreview(uint Width, uint Height, byte[] PngBytes);
