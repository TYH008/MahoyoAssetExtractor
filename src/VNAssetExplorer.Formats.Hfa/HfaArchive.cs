namespace VNAssetExplorer.Formats.Hfa;

public sealed class HfaArchive
{
    public HfaArchive(long dataStartOffset, IReadOnlyList<HfaArchiveEntry> entries)
    {
        DataStartOffset = dataStartOffset;
        Entries = entries;
    }

    public long DataStartOffset { get; }

    public IReadOnlyList<HfaArchiveEntry> Entries { get; }
}
