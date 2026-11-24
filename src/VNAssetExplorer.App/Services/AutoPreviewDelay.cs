namespace VNAssetExplorer.App.Services;

public interface IAutoPreviewDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class TaskAutoPreviewDelay : IAutoPreviewDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
