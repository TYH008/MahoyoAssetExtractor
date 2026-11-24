namespace VNAssetExplorer.Core;

public sealed record ExtractionEntry(int ArchiveIndex, string Name, long Offset, long Size);
