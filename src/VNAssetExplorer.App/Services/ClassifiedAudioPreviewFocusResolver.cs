using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

public static class ClassifiedAudioPreviewFocusResolver
{
    public static ClassifiedAudio? Resolve(
        ClassifiedAudio? pointerAudio,
        ClassifiedAudio? keyboardFocusedAudio,
        ClassifiedAudio? selectedItem) =>
        pointerAudio ?? keyboardFocusedAudio ?? selectedItem;
}