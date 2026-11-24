using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

public sealed class BatchPngSelectionSnapshot
{
    private BatchPngSelectionSnapshot(IReadOnlyList<ArchiveEntryProjection> entries)
    {
        Entries = entries;
        SelectedIndices = entries.Select(entry => entry.Index).ToArray();
    }

    public IReadOnlyList<ArchiveEntryProjection> Entries { get; }
    public IReadOnlyList<int> SelectedIndices { get; }

    public static BatchPngSelectionSnapshot Capture(
        IEnumerable<ArchiveEntryProjection> entries,
        IEnumerable<int> selectedIndices)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(selectedIndices);

        var selected = selectedIndices.ToHashSet();
        var snapshot = entries
            .Where(entry => selected.Contains(entry.Index))
            .ToArray();
        return new BatchPngSelectionSnapshot(snapshot);
    }
}
