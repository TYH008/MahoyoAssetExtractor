namespace VNAssetExplorer.Core;

public enum ExtractionStatus
{
    Written,
    Skipped,
    Renamed,
    Failed,
    Cancelled,
}

public sealed record ExtractionResult(
    int ArchiveIndex,
    string OriginalName,
    string? FinalRelativePath,
    ExtractionStatus Status,
    long BytesWritten,
    string? ErrorCategory = null);

public sealed class ExtractionSummary
{
    public ExtractionSummary(IReadOnlyList<ExtractionResult> results, bool cancelled = false)
    {
        Results = results;
        Cancelled = cancelled;
    }

    public IReadOnlyList<ExtractionResult> Results { get; }

    public bool Cancelled { get; }
}
