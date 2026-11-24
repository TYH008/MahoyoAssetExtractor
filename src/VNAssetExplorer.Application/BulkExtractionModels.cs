namespace VNAssetExplorer.Application;

public enum BulkExtractionCategory { Images, Audio, Video, GalleryImages, GalleryAudio }
public enum BulkExtractionOutcome { Completed, Cancelled, Stopped }
public enum BulkWriteOutcome { Written, ConflictSkipped }

public sealed class BulkExtractionRequest
{
    public BulkExtractionRequest(string sourceDirectory, string outputDirectory, IEnumerable<BulkExtractionCategory> categories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(categories);
        ValidateDirectorySyntax(sourceDirectory);
        ValidateDirectorySyntax(outputDirectory);
        var selected = categories.Distinct().Order().ToArray();
        if (selected.Length == 0 || selected.Any(category => !Enum.IsDefined(category)))
            throw new ArgumentException("Select at least one supported category.", nameof(categories));
        SourceDirectory = Path.GetFullPath(sourceDirectory);
        OutputDirectory = Path.GetFullPath(outputDirectory);
        Categories = Array.AsReadOnly(selected);
    }

    public string SourceDirectory { get; }
    public string OutputDirectory { get; }
    public IReadOnlyList<BulkExtractionCategory> Categories { get; }

    private static void ValidateDirectorySyntax(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.Contains('~'))
            throw new ArgumentException("Use an ordinary absolute directory path.", nameof(path));
        var relative = path[Path.GetPathRoot(path)!.Length..].TrimEnd('\\', '/');
        if (relative.Length > 0) VNAssetExplorer.Core.ExtractionPath.Normalize(relative);
    }
}

// Classification coverage is per category; unmatched counts must not be summed as unknown media.
public sealed record BulkCategoryResult(
    BulkExtractionCategory Category,
    int MatchedCount,
    int UnmatchedCount,
    int RecognitionFailedCount,
    int WrittenCount,
    int ConflictSkippedCount,
    int WriteFailedCount,
    int CancelledItemCount,
    int NotStartedCount);

public sealed class BulkExtractionResult
{
    public BulkExtractionResult(GameRootInventoryCoverage scanCoverage, GameRootInventoryOutcome scanOutcome,
        IEnumerable<BulkCategoryResult> categories, BulkExtractionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(scanCoverage);
        ArgumentNullException.ThrowIfNull(categories);
        ScanCoverage = scanCoverage;
        ScanOutcome = scanOutcome;
        Categories = Array.AsReadOnly(categories.ToArray());
        Outcome = outcome;
    }

    public GameRootInventoryCoverage ScanCoverage { get; }
    public GameRootInventoryOutcome ScanOutcome { get; }
    public IReadOnlyList<BulkCategoryResult> Categories { get; }
    // Set only after actual work/handles have finished, never on a cancellation request.
    public BulkExtractionOutcome Outcome { get; }
}
