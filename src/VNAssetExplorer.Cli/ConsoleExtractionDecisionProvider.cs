using VNAssetExplorer.Core;

namespace VNAssetExplorer.Cli;

internal sealed class ConsoleExtractionDecisionProvider(TextReader input, TextWriter output) : IExtractionDecisionProvider
{
    public ExistingFileDecision DecideExistingFile(ExtractionEntry entry, string relativePath)
    {
        output.WriteLine($"目标文件已存在：{relativePath}");
        output.WriteLine("请选择：[S]跳过（默认） [O]覆盖 [R]重命名 [C]取消");
        var choice = input.ReadLine()?.Trim().ToUpperInvariant();
        var action = choice switch
        {
            "O" => ExistingFileAction.Overwrite,
            "R" => ExistingFileAction.Rename,
            "C" => ExistingFileAction.Cancel,
            _ => ExistingFileAction.Skip,
        };

        output.WriteLine("应用到后续所有冲突？[y/N]");
        var apply = string.Equals(input.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        return new ExistingFileDecision(action, apply);
    }

    public EntryFailureAction DecideEntryFailure(ExtractionEntry entry, string errorCategory)
    {
        output.WriteLine($"条目 {entry.ArchiveIndex}（{entry.Name}）提取失败：{errorCategory}");
        output.WriteLine("请选择：[C]继续 [I]忽略后续条目错误 [X]取消（默认）");
        return input.ReadLine()?.Trim().ToUpperInvariant() switch
        {
            "C" => EntryFailureAction.Continue,
            "I" => EntryFailureAction.IgnoreSubsequent,
            _ => EntryFailureAction.Cancel,
        };
    }
}
