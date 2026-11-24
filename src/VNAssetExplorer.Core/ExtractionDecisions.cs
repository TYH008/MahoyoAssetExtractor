namespace VNAssetExplorer.Core;

public enum ExistingFileAction
{
    Skip,
    Overwrite,
    Rename,
    Cancel,
}

public readonly record struct ExistingFileDecision(ExistingFileAction Action, bool ApplyToRemaining = false);

public enum EntryFailureAction
{
    Continue,
    IgnoreSubsequent,
    Cancel,
}

public interface IExtractionDecisionProvider
{
    ExistingFileDecision DecideExistingFile(ExtractionEntry entry, string relativePath);

    EntryFailureAction DecideEntryFailure(ExtractionEntry entry, string errorCategory) => EntryFailureAction.Cancel;
}

public sealed class SkipExistingFilesDecisionProvider : IExtractionDecisionProvider
{
    public static SkipExistingFilesDecisionProvider Instance { get; } = new();

    private SkipExistingFilesDecisionProvider()
    {
    }

    public ExistingFileDecision DecideExistingFile(ExtractionEntry entry, string relativePath) =>
        new(ExistingFileAction.Skip);
}
