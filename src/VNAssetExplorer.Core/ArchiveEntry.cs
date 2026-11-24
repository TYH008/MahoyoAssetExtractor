namespace VNAssetExplorer.Core;

public sealed record ArchiveEntry(
    string Name,
    long Offset,
    long Size);
