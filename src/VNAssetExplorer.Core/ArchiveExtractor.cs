namespace VNAssetExplorer.Core;

public static class ArchiveExtractor
{
    private const int BufferSize = 64 * 1024;

    public static async Task<ExtractionSummary> ExtractAsync(
        Stream source,
        string outputRoot,
        IReadOnlyList<ExtractionEntry> entries,
        IExtractionDecisionProvider? decisionProvider = null,
        CancellationToken cancellationToken = default,
        Action<ExtractionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entries);
        decisionProvider ??= SkipExistingFilesDecisionProvider.Instance;
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The source stream must be readable and seekable.", nameof(source));
        }

        var planned = entries.Select(entry =>
        {
            ValidateRange(source.Length, entry);
            var normalized = ExtractionPath.Normalize(entry.Name);
            var destination = ExtractionPath.ResolveUnderRoot(outputRoot, normalized);
            return (Entry: entry, Normalized: normalized, Destination: destination);
        }).ToArray();

        var results = new List<ExtractionResult>(planned.Length);
        var buffer = new byte[BufferSize];
        ExistingFileDecision? remainingConflictDecision = null;
        var ignoreSubsequentEntryErrors = false;
        for (var itemIndex = 0; itemIndex < planned.Length; itemIndex++)
        {
            var item = planned[itemIndex];
            var position = itemIndex + 1;
            if (cancellationToken.IsCancellationRequested)
            {
                ReportProgress(progress, new ExtractionProgress(position, planned.Length, item.Entry.ArchiveIndex, item.Entry.Name, ExtractionProgressPhase.Cancelled));
                return new ExtractionSummary(results.AsReadOnly(), true);
            }

            var parent = Path.GetDirectoryName(item.Destination)!;
            ReportProgress(progress, new ExtractionProgress(
                results.Count + 1, planned.Length, item.Entry.ArchiveIndex, item.Entry.Name, ExtractionProgressPhase.Started));
            ExtractionPath.EnsureNoReparsePoints(outputRoot, item.Destination);
            Directory.CreateDirectory(parent);
            ExtractionPath.EnsureNoReparsePoints(outputRoot, item.Destination);
            var destination = item.Destination;
            var status = ExtractionStatus.Written;
            if (File.Exists(destination))
            {
                var decision = remainingConflictDecision
                    ?? decisionProvider.DecideExistingFile(item.Entry, item.Normalized);
                if (decision.ApplyToRemaining)
                {
                    remainingConflictDecision = decision;
                }

                switch (decision.Action)
                {
                    case ExistingFileAction.Skip:
                        results.Add(new ExtractionResult(
                            item.Entry.ArchiveIndex,
                            item.Entry.Name,
                            null,
                            ExtractionStatus.Skipped,
                            0));
                        ReportProgress(progress, new ExtractionProgress(position, planned.Length, item.Entry.ArchiveIndex, item.Entry.Name, ExtractionProgressPhase.Skipped));
                        continue;
                    case ExistingFileAction.Rename:
                        destination = FindRenamedDestination(destination);
                        status = ExtractionStatus.Renamed;
                        ReportProgress(progress, new ExtractionProgress(position, planned.Length, item.Entry.ArchiveIndex, item.Entry.Name, ExtractionProgressPhase.Renamed));
                        break;
                    case ExistingFileAction.Cancel:
                        ReportProgress(progress, new ExtractionProgress(position, planned.Length, item.Entry.ArchiveIndex, item.Entry.Name, ExtractionProgressPhase.Cancelled));
                        return new ExtractionSummary(results.AsReadOnly(), true);
                    case ExistingFileAction.Overwrite:
                        if (!IsSafeOverwriteTarget(destination))
                        {
                            results.Add(new ExtractionResult(
                                item.Entry.ArchiveIndex, item.Entry.Name, null,
                                ExtractionStatus.Failed, 0, "UnsafeDestination"));
                            continue;
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported existing-file decision.");
                }
            }

            var temporary = Path.Combine(parent, $".vnasset-{Guid.NewGuid():N}.tmp");
            try
            {
                source.Position = item.Entry.Offset;
                await using (var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyExactlyAsync(source, output, item.Entry.Size, buffer, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                File.Move(temporary, destination, status == ExtractionStatus.Written && File.Exists(destination));
                results.Add(new ExtractionResult(
                    item.Entry.ArchiveIndex,
                    item.Entry.Name,
                    Path.GetRelativePath(outputRoot, destination).Replace('\\', '/'),
                    status,
                    item.Entry.Size));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ReportProgress(progress, new ExtractionProgress(position, planned.Length, item.Entry.ArchiveIndex, item.Entry.Name, ExtractionProgressPhase.Cancelled));
                return new ExtractionSummary(results.AsReadOnly(), true);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException
                || exception is UnsafeDestinationException
                || exception is IOException ioException && !IsFatalIoFailure(ioException))
            {
                results.Add(new ExtractionResult(
                    item.Entry.ArchiveIndex,
                    item.Entry.Name,
                    null,
                    ExtractionStatus.Failed,
                    0,
                    exception is UnauthorizedAccessException ? "AccessDenied" : exception is UnsafeDestinationException ? "UnsafeDestination" : "IoFailure"));
                ReportProgress(progress, new ExtractionProgress(position, planned.Length, item.Entry.ArchiveIndex, item.Entry.Name, ExtractionProgressPhase.Failed));
                var action = ignoreSubsequentEntryErrors
                    ? EntryFailureAction.Continue
                    : decisionProvider.DecideEntryFailure(item.Entry, results[^1].ErrorCategory!);
                if (action == EntryFailureAction.Cancel)
                {
                    ReportProgress(progress, new ExtractionProgress(position, planned.Length, item.Entry.ArchiveIndex, item.Entry.Name, ExtractionProgressPhase.Cancelled));
                    return new ExtractionSummary(results.AsReadOnly(), true);
                }

                if (action == EntryFailureAction.IgnoreSubsequent)
                {
                    ignoreSubsequentEntryErrors = true;
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return new ExtractionSummary(results.AsReadOnly());
    }

    private static void ReportProgress(Action<ExtractionProgress>? progress, ExtractionProgress value)
    {
        try
        {
            progress?.Invoke(value);
        }
        catch
        {
            // Progress observers are non-authoritative and must not affect extraction.
        }
    }

    private static bool IsSafeOverwriteTarget(string destination)
    {
        try
        {
            var attributes = File.GetAttributes(destination);
            return (attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.ReparsePoint)) == 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsFatalIoFailure(IOException exception) =>
        exception.HResult is unchecked((int)0x80070070) // ERROR_DISK_FULL
            or unchecked((int)0x80070027); // ERROR_HANDLE_DISK_FULL

    private static string FindRenamedDestination(string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var stem = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        for (var attempt = 1; attempt <= 10_000; attempt++)
        {
            var candidate = Path.Combine(directory, $"{stem}.{attempt}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("No available numbered destination name was found.");
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long size,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var remaining = size;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("The source entry ended before its declared size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static void ValidateRange(long sourceLength, ExtractionEntry entry)
    {
        if (entry.Offset < 0
            || entry.Size < 0
            || entry.Offset > sourceLength
            || entry.Size > sourceLength - entry.Offset)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "The extraction range is outside the source stream.");
        }
    }
}
