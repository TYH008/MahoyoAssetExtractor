using Microsoft.Win32;
using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.Services;

public sealed record ExtractionConfirmationRequest(string Scope, int Count, string OutputRoot, long TotalBytes, SpaceAvailabilityStatus SpaceStatus, bool IsAll);

public interface IOutputFolderPicker { string? PickParentOutputDirectory(string? initialDirectory); }
public interface IExtractionConfirmationService { bool Confirm(ExtractionConfirmationRequest request); }
public interface IExtractionCancellationConfirmationService { bool ConfirmCancelExtraction(); }
public interface IExtractionDecisionDialogService
{
    ApplicationExistingFileDecision DecideExistingFile(ApplicationConflictContext context);
    ApplicationEntryFailureAction DecideEntryFailure(ApplicationFailureContext context);
}

public sealed class NoOutputFolderPicker : IOutputFolderPicker { public string? PickParentOutputDirectory(string? initialDirectory) => null; }
public sealed class CancelledExtractionConfirmationService : IExtractionConfirmationService { public bool Confirm(ExtractionConfirmationRequest request) => false; }

public sealed class OutputFolderPicker : IOutputFolderPicker, IBatchPngOutputFolderPicker
{
    private readonly Func<string>? title;

    public OutputFolderPicker() { }
    internal OutputFolderPicker(Func<string> title) => this.title = title;

    public string? PickParentOutputDirectory(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title?.Invoke() ?? "选择提取父目录", InitialDirectory = initialDirectory ?? string.Empty, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickOutputFolder(string? recentFolder) => PickParentOutputDirectory(recentFolder);
}

public sealed class ExtractionConfirmationService : IExtractionConfirmationService
{
    public bool Confirm(ExtractionConfirmationRequest request)
    {
        var warning = request.IsAll ? "\n这可能生成大量文件。" : string.Empty;
        var message = $"范围：{request.Scope}\n数量：{request.Count}\n总字节：{request.TotalBytes:N0}\n输出目录：\n{request.OutputRoot}\n空间状态：{request.SpaceStatus}{warning}\n\n确认开始原始资源提取吗？";
        return System.Windows.MessageBox.Show(message, "确认提取", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;
    }
}

public sealed class ExtractionCancellationConfirmationService : IExtractionCancellationConfirmationService
{
    public bool ConfirmCancelExtraction() =>
        System.Windows.MessageBox.Show("已完成文件会保留，当前未完成文件将由安全提取流程清理。\n\n取消当前提取吗？", "确认取消提取", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
}

public sealed class SkipExtractionDecisionProvider : IApplicationExtractionDecisionProvider
{
    public ApplicationExistingFileDecision DecideExistingFile(ApplicationConflictContext context) => new(ApplicationExistingFileAction.Skip);
    public ApplicationEntryFailureAction DecideEntryFailure(ApplicationFailureContext context) => ApplicationEntryFailureAction.Cancel;
}

public sealed class DialogExtractionDecisionProvider(IExtractionDecisionDialogService dialog) : IApplicationExtractionDecisionProvider
{
    public ApplicationExistingFileDecision DecideExistingFile(ApplicationConflictContext context) =>
        dialog.DecideExistingFile(context) with { ApplyToRemaining = false };

    public ApplicationEntryFailureAction DecideEntryFailure(ApplicationFailureContext context) =>
        dialog.DecideEntryFailure(context);
}

public sealed class MessageBoxExtractionDecisionDialog : IExtractionDecisionDialogService
{
    public static ApplicationExistingFileDecision MapExistingFileChoice(System.Windows.MessageBoxResult primary, System.Windows.MessageBoxResult? cancelEntireExtraction)
    {
        return primary switch
        {
            System.Windows.MessageBoxResult.Yes => new(ApplicationExistingFileAction.Overwrite),
            System.Windows.MessageBoxResult.No => new(ApplicationExistingFileAction.Rename),
            _ when cancelEntireExtraction == System.Windows.MessageBoxResult.Yes => new(ApplicationExistingFileAction.Cancel),
            _ => new(ApplicationExistingFileAction.Skip),
        };
    }

    public ApplicationExistingFileDecision DecideExistingFile(ApplicationConflictContext context)
    {
        var result = System.Windows.MessageBox.Show($"目标文件已存在：\n{context.RelativePath}\n\n是：覆盖\n否：重命名\n取消：跳过当前文件", "处理文件冲突", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Warning);
        var cancelChoice = result == System.Windows.MessageBoxResult.Cancel
            ? System.Windows.MessageBox.Show("是否取消整个提取？\n\n选择“否”将安全跳过当前文件。", "取消整个提取", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            : (System.Windows.MessageBoxResult?)null;
        return MapExistingFileChoice(result, cancelChoice);
    }

    public ApplicationEntryFailureAction DecideEntryFailure(ApplicationFailureContext context)
    {
        var result = System.Windows.MessageBox.Show($"条目处理失败：\n{context.Name}\n\n是：继续\n否：忽略后续普通错误\n取消：取消整个提取", "处理提取错误", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Error);
        return result switch
        {
            System.Windows.MessageBoxResult.Yes => ApplicationEntryFailureAction.Continue,
            System.Windows.MessageBoxResult.No => ApplicationEntryFailureAction.IgnoreSubsequent,
            _ => ApplicationEntryFailureAction.Cancel,
        };
    }
}
