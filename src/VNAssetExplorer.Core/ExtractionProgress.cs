namespace VNAssetExplorer.Core;

public enum ExtractionProgressPhase
{
    Started,
    Skipped,
    Renamed,
    Failed,
    Cancelled,
}

public sealed record ExtractionProgress(
    int Position,
    int Total,
    int ArchiveIndex,
    string Name,
    ExtractionProgressPhase Phase);

internal sealed class UnsafeDestinationException : IOException
{
}