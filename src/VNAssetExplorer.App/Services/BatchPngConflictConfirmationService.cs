using System.Windows;

namespace VNAssetExplorer.App.Services;

public sealed class BatchPngConflictConfirmationService : IBatchPngConflictConfirmationService
{
    private readonly Func<System.Windows.Window?> ownerProvider;

    public BatchPngConflictConfirmationService(Func<System.Windows.Window?>? ownerProvider = null)
    {
        this.ownerProvider = ownerProvider ?? AppOwnerProvider.ResolveMainWindow;
    }

    public BatchPngConflictDecision Decide(BatchPngConflictRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialog = SemanticChoiceDialog.BatchConflict(request.ConflictCount);
        return (BatchPngConflictDecision)dialog.Resolve(dialog.Show(ownerProvider()));
    }

    internal Window CreateDialogWindow(BatchPngConflictRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SemanticChoiceDialog.BatchConflict(request.ConflictCount).CreateWindow(ownerProvider());
    }
}
