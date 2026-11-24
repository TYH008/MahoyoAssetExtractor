using System.Globalization;
using VNAssetExplorer.Application;
using VNAssetExplorer.Core;
using VNAssetExplorer.Formats.Hfa;
using VNAssetExplorer.Formats.Mzp;

namespace VNAssetExplorer.Cli;

public static class HfaCli
{
    private const string Usage = "用法：VNAssetExplorer.Cli <archive.hfa> | VNAssetExplorer.Cli extract <archive.hfa> <output-directory> [entry-index] | VNAssetExplorer.Cli preview-mzp <image.mzp> <output.png> | VNAssetExplorer.Cli preview-hfa-mzp <archive.hfa> <entry-index> <output.png>";

    public static int Run(string[] args, TextWriter output, TextWriter error, TextReader? input = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        input ??= TextReader.Null;

        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            output.WriteLine(Usage);
            return 0;
        }

        if (args.Length == 0)
        {
            error.WriteLine(Usage);
            return 2;
        }

        if (args.Length is 3 or 4 && args[0] == "extract")
        {
            return RunExtract(args[1], args[2], args.Length == 4 ? args[3] : null, output, error, input);
        }

        if (args.Length == 3 && args[0] == "preview-mzp")
        {
            return RunPreviewMzp(args[1], args[2], output, error);
        }

        if (args.Length == 4 && args[0] == "preview-hfa-mzp")
        {
            return RunPreviewHfaMzp(args[1], args[2], args[3], output, error);
        }

        if (args.Length != 1 || args[0].StartsWith('-'))
        {
            error.WriteLine($"参数错误。{Usage}");
            return 2;
        }

        var path = args[0];
        if (Directory.Exists(path))
        {
            error.WriteLine("指定路径不是文件。");
            return 3;
        }

        if (!File.Exists(path))
        {
            error.WriteLine("指定文件不存在。");
            return 3;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var archive = HfaArchiveReader.Read(stream);
            WriteListing(archive, Path.GetFileName(path), output);
            return 0;
        }
        catch (HfaFormatException exception)
        {
            error.WriteLine(MapFormatError(exception.Code));
            return 4;
        }
        catch (IOException)
        {
            error.WriteLine("读取文件时发生 I/O 错误。");
            return 5;
        }
        catch (UnauthorizedAccessException)
        {
            error.WriteLine("没有权限读取指定文件。");
            return 5;
        }
    }

    private static int RunPreviewMzp(string imagePath, string outputPath, TextWriter output, TextWriter error)
    {
        if (Directory.Exists(imagePath) || !File.Exists(imagePath))
        {
            error.WriteLine("指定的 MZP 文件不存在或不是文件。");
            return 3;
        }

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MzpLimits.MaxInputBytes)
            {
                error.WriteLine("MZP 输入文件超过当前单文件预览的安全大小限制。");
                return 4;
            }

            var preview = MzpPreviewReader.Read(stream);
            File.WriteAllBytes(outputPath, preview.PngBytes);
            output.WriteLine($"已生成：{outputPath}");
            return 0;
        }
        catch (MzpFormatException exception)
        {
            error.WriteLine(exception.Code == MzpErrorCode.ResourceLimitExceeded
                ? "MZP 资源规模超过当前单文件预览的安全限制。"
                : "MZP/MZX 结构不完整或无法安全解码。");
            return 4;
        }
        catch (HepFormatException)
        {
            error.WriteLine("HEP 图片结构不完整或无法安全读取。");
            return 4;
        }
        catch (IOException)
        {
            error.WriteLine("读取 MZP 或写入 PNG 时发生 I/O 错误。");
            return 5;
        }
        catch (UnauthorizedAccessException)
        {
            error.WriteLine("没有权限读取 MZP 或写入 PNG 输出文件。");
            return 5;
        }
    }

    private static int RunPreviewHfaMzp(
        string archivePath,
        string entryIndexText,
        string outputPath,
        TextWriter output,
        TextWriter error)
    {
        if (Directory.Exists(archivePath) || !File.Exists(archivePath))
        {
            error.WriteLine("指定的 HFA 文件不存在或不是文件。");
            return 3;
        }

        if (!int.TryParse(entryIndexText, NumberStyles.None, CultureInfo.InvariantCulture, out var entryIndex)
            || entryIndex < 0)
        {
            error.WriteLine("条目序号必须是非负整数。");
            return 2;
        }

        if (File.Exists(outputPath))
        {
            error.WriteLine("PNG 输出文件已存在；为避免覆盖用户文件，预览已停止。");
            return 5;
        }

        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var session = ArchiveApplication.OpenHfa(stream);
            var preview = session.PreviewMzp(entryIndex);
            WritePngWithoutPartialOutput(outputPath, preview.PngBytes);
            output.WriteLine($"已生成：{outputPath}（{preview.Width}×{preview.Height}）");
            return 0;
        }
        catch (VNAssetExplorer.Application.ApplicationException exception)
        {
            error.WriteLine(MapApplicationError(exception));
            return exception.Code switch
            {
                ApplicationErrorCode.EntryNotFound => 2,
                ApplicationErrorCode.AccessDenied or ApplicationErrorCode.IoFailure => 5,
                ApplicationErrorCode.Cancelled => 6,
                _ => 4,
            };
        }
        catch (IOException)
        {
            error.WriteLine("读取 HFA 或写入 PNG 时发生 I/O 错误。");
            return 5;
        }
        catch (UnauthorizedAccessException)
        {
            error.WriteLine("没有权限读取 HFA 或写入 PNG 输出文件。");
            return 5;
        }
    }

    private static string MapApplicationError(VNAssetExplorer.Application.ApplicationException exception)
    {
        if (exception.Code == ApplicationErrorCode.InvalidOrUnsafeFormat
            && ContainsMzpError(exception, MzpErrorCode.WrongMagic))
        {
            return "指定条目不是当前支持的 MZP 预览格式。";
        }

        return exception.Code switch
        {
            ApplicationErrorCode.EntryNotFound => "指定的 HFA 条目序号不存在。",
            ApplicationErrorCode.UnsupportedPreviewFormat => "指定条目不是当前支持的 MZP 预览格式。",
            ApplicationErrorCode.ResourceLimitExceeded => "MZP 资源规模超过当前预览的安全限制。",
            ApplicationErrorCode.AccessDenied => "没有权限读取 HFA 条目。",
            ApplicationErrorCode.IoFailure => "读取 HFA 条目时发生 I/O 错误。",
            ApplicationErrorCode.Cancelled => "预览操作已取消。",
            _ => "HFA 条目或 MZP 结构不完整，无法安全预览。",
        };
    }

    private static bool ContainsMzpError(Exception exception, MzpErrorCode code)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is MzpFormatException { Code: var actualCode } && actualCode == code)
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDeleteOutput(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WritePngWithoutPartialOutput(string outputPath, byte[] pngBytes)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, pngBytes);
            File.Move(temporaryPath, outputPath);
        }
        finally
        {
            TryDeleteOutput(temporaryPath);
        }
    }

    private static int RunExtract(
        string archivePath,
        string outputDirectory,
        string? archiveIndexText,
        TextWriter output,
        TextWriter error,
        TextReader input)
    {
        if (Directory.Exists(archivePath) || !File.Exists(archivePath))
        {
            error.WriteLine("指定的归档文件不存在或不是文件。");
            return 3;
        }

        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var archive = HfaArchiveReader.Read(stream);
            var indexedEntries = archive.Entries
                .Select((entry, index) => new ExtractionEntry(index, entry.Name, entry.Offset, entry.Size));
            ExtractionEntry[] entries;
            if (archiveIndexText is null)
            {
                entries = indexedEntries.ToArray();
            }
            else if (int.TryParse(archiveIndexText, NumberStyles.None, CultureInfo.InvariantCulture, out var archiveIndex)
                && archiveIndex >= 0
                && archiveIndex < archive.Entries.Count)
            {
                entries = [indexedEntries.ElementAt(archiveIndex)];
            }
            else
            {
                error.WriteLine("条目序号无效。");
                return 2;
            }

            var decisions = new ConsoleExtractionDecisionProvider(input, output);
            var summary = ArchiveExtractor.ExtractAsync(stream, outputDirectory, entries, decisions).GetAwaiter().GetResult();
            var written = summary.Results.Count(result => result.Status is ExtractionStatus.Written or ExtractionStatus.Renamed);
            var skipped = summary.Results.Count(result => result.Status == ExtractionStatus.Skipped);
            var failed = summary.Results.Count(result => result.Status == ExtractionStatus.Failed);
            output.WriteLine($"成功：{written}，跳过：{skipped}，失败：{failed}");
            return summary.Cancelled ? 6 : 0;
        }
        catch (HfaFormatException exception)
        {
            error.WriteLine(MapFormatError(exception.Code));
            return 4;
        }
        catch (IOException)
        {
            error.WriteLine("读取或写入文件时发生 I/O 错误。");
            return 5;
        }
        catch (UnauthorizedAccessException)
        {
            error.WriteLine("没有权限读取归档或写入输出目录。");
            return 5;
        }
        catch (ArgumentException)
        {
            error.WriteLine("归档包含不安全的输出路径，已停止提取。");
            return 4;
        }
    }

    private static string MapFormatError(HfaErrorCode code) => code switch
    {
        HfaErrorCode.WrongMagic => "无法识别该文件为候选 HUNEXGGEFA10 归档。",
        HfaErrorCode.TruncatedHeader or HfaErrorCode.TruncatedDirectory => "归档结构不完整，无法安全读取。",
        HfaErrorCode.EntryCountTooLarge => "归档条目数量超出安全限制。",
        HfaErrorCode.InvalidUtf8Name or HfaErrorCode.NonCanonicalNamePadding => "归档包含无法安全解析的文件名。",
        HfaErrorCode.UnsafeEntryName => "归档包含不安全的文件名。",
        HfaErrorCode.EntryRangePastEnd => "归档条目范围超出文件末尾。",
        _ => "无法按照候选 HFA 布局安全读取该文件。",
    };

    private static void WriteListing(HfaArchive archive, string fileName, TextWriter output)
    {
        output.WriteLine("格式：HUNEXGGEFA10（候选布局）");
        output.WriteLine($"文件：{fileName}");
        output.WriteLine($"条目数：{archive.Entries.Count.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"数据区起点：{archive.DataStartOffset.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine();
        output.WriteLine("序号  偏移量  大小  名称");

        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            output.WriteLine(
                $"{index:D4}  {entry.Offset.ToString(CultureInfo.InvariantCulture)}  "
                + $"{entry.Size.ToString(CultureInfo.InvariantCulture)}  {entry.Name}");
        }
    }
}
