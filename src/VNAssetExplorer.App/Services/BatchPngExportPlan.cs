using System.IO;
using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

public enum BatchPngConflictDecision
{
    SkipAll,
    RenameAll,
    Cancel,
}

public enum BatchPngPlannedStatus
{
    Planned,
    NonImageSkipped,
    ConflictSkipped,
    Cancelled,
}

public sealed record BatchPngPlanItem(
    ArchiveEntryProjection Entry,
    string? FinalPath,
    BatchPngPlannedStatus Status);

public sealed class BatchPngExportPlan
{
    private BatchPngExportPlan(
        IReadOnlyList<BatchPngPlanItem> items,
        int conflictCount,
        int nonImageCount,
        bool cancelled)
    {
        Items = items;
        ConflictCount = conflictCount;
        NonImageCount = nonImageCount;
        Cancelled = cancelled;
    }

    public IReadOnlyList<BatchPngPlanItem> Items { get; }
    public int ConflictCount { get; }
    public int NonImageCount { get; }
    public bool Cancelled { get; }

    public static BatchPngExportPlan Create(
        IEnumerable<ArchiveEntryProjection> entries,
        string outputDirectory,
        IEnumerable<string> existingFileNames,
        BatchPngConflictDecision decision)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(existingFileNames);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("输出目录不能为空。", nameof(outputDirectory));
        }

        var frozenEntries = entries.ToArray();
        var outputRoot = Path.GetFullPath(outputDirectory);
        var reserved = new HashSet<string>(existingFileNames, StringComparer.OrdinalIgnoreCase);
        var items = new List<BatchPngPlanItem>();
        var conflictCount = 0;
        var nonImageCount = 0;

        foreach (var entry in frozenEntries)
        {
            if (!IsImageCandidate(entry))
            {
                nonImageCount++;
                items.Add(new BatchPngPlanItem(entry, null, BatchPngPlannedStatus.NonImageSkipped));
                continue;
            }

            var baseName = PreviewPngFilename.Suggest(entry.Name);
            var hasConflict = reserved.Contains(baseName);
            if (hasConflict) conflictCount++;

            if (hasConflict && decision == BatchPngConflictDecision.Cancel)
            {
                return new BatchPngExportPlan(
                    frozenEntries.Select(entry => new BatchPngPlanItem(
                        entry,
                        null,
                        BatchPngPlannedStatus.Cancelled)).ToArray(),
                    conflictCount,
                    frozenEntries.Count(entry => !IsImageCandidate(entry)),
                    cancelled: true);
            }

            if (hasConflict && decision == BatchPngConflictDecision.SkipAll)
            {
                items.Add(new BatchPngPlanItem(entry, null, BatchPngPlannedStatus.ConflictSkipped));
                continue;
            }

            var finalName = hasConflict
                ? FindAvailableNumberedName(baseName, reserved)
                : baseName;
            reserved.Add(finalName);
            items.Add(new BatchPngPlanItem(
                entry,
                Path.Combine(outputRoot, finalName),
                BatchPngPlannedStatus.Planned));
        }

        return new BatchPngExportPlan(items.ToArray(), conflictCount, nonImageCount, cancelled: false);
    }

    private static string FindAvailableNumberedName(string baseName, ISet<string> reserved)
    {
        var stem = Path.GetFileNameWithoutExtension(baseName);
        for (var number = 1; ; number++)
        {
            var candidate = $"{stem} ({number}).png";
            if (!reserved.Contains(candidate)) return candidate;
        }
    }

    private static bool IsImageCandidate(ArchiveEntryProjection entry) =>
        entry.Name.EndsWith(".mzp", StringComparison.OrdinalIgnoreCase);
}
