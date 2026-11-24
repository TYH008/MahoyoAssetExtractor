using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

public static class PreviewFocusResolver
{
    public static ArchiveEntryProjection? Resolve(
        ArchiveEntryProjection? pointerEntry,
        ArchiveEntryProjection? keyboardFocusedEntry,
        ArchiveEntryProjection? selectedItem) =>
        pointerEntry ?? keyboardFocusedEntry ?? selectedItem;
}