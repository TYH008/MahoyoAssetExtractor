using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

public sealed record BatchPngConflictRequest(int ConflictCount);

public interface IBatchPngOutputFolderPicker
{
    string? PickOutputFolder(string? recentFolder);
}

public interface IBatchPngConflictConfirmationService
{
    BatchPngConflictDecision Decide(BatchPngConflictRequest request);
}

public static class BatchPngExportPreparation
{
    public static BatchPngExportPlan? TryPrepare(
        IEnumerable<ArchiveEntryProjection> entries,
        IBatchPngOutputFolderPicker folderPicker,
        IBatchPngConflictConfirmationService conflictConfirmation,
        IEnumerable<string> existingFileNames,
        string? recentFolder)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(conflictConfirmation);
        ArgumentNullException.ThrowIfNull(existingFileNames);

        var outputFolder = folderPicker.PickOutputFolder(recentFolder);
        if (outputFolder is null) return null;

        var entrySnapshot = entries.ToArray();
        var existingSnapshot = existingFileNames.ToArray();
        var probe = BatchPngExportPlan.Create(
            entrySnapshot,
            outputFolder,
            existingSnapshot,
            BatchPngConflictDecision.RenameAll);

        var decision = probe.ConflictCount == 0
            ? BatchPngConflictDecision.RenameAll
            : conflictConfirmation.Decide(new BatchPngConflictRequest(probe.ConflictCount));

        return BatchPngExportPlan.Create(entrySnapshot, outputFolder, existingSnapshot, decision);
    }
}
