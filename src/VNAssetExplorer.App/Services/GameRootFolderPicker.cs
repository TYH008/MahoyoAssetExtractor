using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

public interface IGameRootFolderPicker
{
    string? PickGameRoot();
}

public interface IGameRootInventoryService
{
    Task<GameRootInventoryResult> ScanAsync(string rootPath, CancellationToken cancellationToken = default);
}

public interface IImageClassificationService
{
    Task<ImageClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken = default, IProgress<ImageClassificationProgress>? progress = null);
}

public interface IAudioClassificationService
{
    Task<AudioClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken = default);
}

public interface IVideoClassificationService
{
    Task<VideoClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken = default);
}

public interface IImageClassificationBatchService
{
    Task<ImageClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken, IProgress<ImageClassificationProgress>? progress, IProgress<ImageClassificationBatch>? batches);
}

public interface IImageClassificationPreviewService
{
    Task<PreviewResult> PreviewAsync(ClassifiedImage image, CancellationToken cancellationToken = default);
}

public sealed class ImageClassificationServiceAdapter(ImageClassificationService service) : IImageClassificationService, IImageClassificationBatchService, IImageClassificationPreviewService
{
    public Task<ImageClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken = default, IProgress<ImageClassificationProgress>? progress = null) => service.ClassifyAsync(inventory, cancellationToken, progress);
    public Task<ImageClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken, IProgress<ImageClassificationProgress>? progress, IProgress<ImageClassificationBatch>? batches) => service.ClassifyAsync(inventory, cancellationToken, progress, batches);
    public Task<PreviewResult> PreviewAsync(ClassifiedImage image, CancellationToken cancellationToken = default) => service.PreviewAsync(image, cancellationToken);
}

public sealed class AudioClassificationServiceAdapter(AudioClassificationService service) : IAudioClassificationService
{
    public Task<AudioClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken = default) => service.ClassifyAsync(inventory, cancellationToken);
}

public sealed class VideoClassificationServiceAdapter(VideoClassificationService service) : IVideoClassificationService
{
    public Task<VideoClassificationResult> ClassifyAsync(GameRootInventoryResult inventory, CancellationToken cancellationToken = default) => service.ClassifyAsync(inventory, cancellationToken);
}

public sealed class GameRootFolderPicker : IGameRootFolderPicker
{
    private readonly Func<string>? title;

    public GameRootFolderPicker() { }
    internal GameRootFolderPicker(Func<string> title) => this.title = title;

    public string? PickGameRoot()
    {
        if (title is not null)
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = title(), Multiselect = false };
            return picker.ShowDialog() == true ? picker.FolderName : null;
        }
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择视觉小说游戏根目录" };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}

public sealed class GameRootInventoryServiceAdapter(GameRootInventoryService service) : IGameRootInventoryService
{
    public Task<GameRootInventoryResult> ScanAsync(string rootPath, CancellationToken cancellationToken = default) => service.ScanAsync(rootPath, cancellationToken);
}