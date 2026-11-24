using System.IO;
using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

public sealed record BatchPngExportProgress(int Position, int Total, string Name);

public sealed record BatchPngExportSummary(
    int WrittenCount,
    int NonImageSkippedCount,
    int ConflictSkippedCount,
    int FailedCount,
    int CancelledCount,
    int NotStartedCount);

public sealed class BatchPngExportExecutor
{
    public async Task<BatchPngExportSummary> ExecuteAsync(
        BatchPngSelectionSnapshot selection,
        IArchivePreviewSession session,
        BatchPngExportPlan plan,
        IPreviewPngWriter writer,
        CancellationToken cancellationToken,
        IProgress<BatchPngExportProgress>? progress = null,
        SemaphoreSlim? previewGate = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(writer);

        var written = 0;
        var failed = 0;
        var cancelled = 0;
        var notStarted = 0;
        var conflictSkipped = plan.Items.Count(item => item.Status == BatchPngPlannedStatus.ConflictSkipped);

        if (plan.Cancelled)
        {
            return new BatchPngExportSummary(
                WrittenCount: 0,
                NonImageSkippedCount: 0,
                ConflictSkippedCount: 0,
                FailedCount: 0,
                CancelledCount: 0,
                NotStartedCount: plan.Items.Count);
        }

        var plannedItems = plan.Items.Where(item => item.Status == BatchPngPlannedStatus.Planned && item.FinalPath is not null).ToArray();
        for (var plannedPosition = 0; plannedPosition < plannedItems.Length; plannedPosition++)
        {
            var item = plannedItems[plannedPosition];

            if (cancellationToken.IsCancellationRequested)
            {
                notStarted += plannedItems.Length - plannedPosition;
                break;
            }

            try
            {
                progress?.Report(new BatchPngExportProgress(plannedPosition + 1, plannedItems.Length, Path.GetFileName(item.FinalPath!)));
                PreviewResult preview;
                if (previewGate is null)
                {
                    preview = await session.PreviewAsync(item.Entry.Index, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await previewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        preview = await session.PreviewAsync(item.Entry.Index, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        previewGate.Release();
                    }
                }
                await writer.WriteAsync(item.FinalPath!, preview.PngBytes, allowOverwrite: false, cancellationToken).ConfigureAwait(false);
                written++;
                if (cancellationToken.IsCancellationRequested)
                {
                    notStarted += plannedItems.Length - plannedPosition - 1;
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled++;
                notStarted += plannedItems.Length - plannedPosition - 1;
                break;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancelled++;
                notStarted += plannedItems.Length - plannedPosition - 1;
                break;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        return new BatchPngExportSummary(
            written,
            plan.NonImageCount,
            conflictSkipped,
            failed,
            cancelled,
            notStarted);
    }
}
