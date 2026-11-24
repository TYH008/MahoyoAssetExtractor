namespace VNAssetExplorer.App.Services;

public enum ExportCapability
{
    CompatiblePng,
    RawResource,
    Cancel,
}

public enum ExportMediaKind
{
    Image,
    Audio,
    Video,
}

public static class ExportCapabilityChooser
{
    public static ExportCapability Choose(System.Windows.Window? owner = null, ExportMediaKind kind = ExportMediaKind.Image)
    {
        var dialog = SemanticChoiceDialog.ExportCapability(kind);
        return (ExportCapability)dialog.Resolve(dialog.Show(owner));
    }

}
