using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

public static class ClassifiedImagePreviewFocusResolver
{
    public static ClassifiedImage? Resolve(
        ClassifiedImage? pointerImage,
        ClassifiedImage? keyboardFocusedImage,
        ClassifiedImage? selectedItem) =>
        pointerImage ?? keyboardFocusedImage ?? selectedItem;
}
