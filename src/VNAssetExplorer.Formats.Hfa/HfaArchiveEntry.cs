using VNAssetExplorer.Core;

namespace VNAssetExplorer.Formats.Hfa;

public sealed class HfaArchiveEntry
{
    public HfaArchiveEntry(ArchiveEntry entry, IReadOnlyList<uint> formatMetadata)
    {
        Entry = entry;
        FormatMetadata = formatMetadata;
    }

    public ArchiveEntry Entry { get; }

    public string Name => Entry.Name;

    public long Offset => Entry.Offset;

    public long Size => Entry.Size;

    public IReadOnlyList<uint> FormatMetadata { get; }
}
