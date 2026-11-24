using VNAssetExplorer.Formats.Hfa;

namespace VNAssetExplorer.Application;

public sealed record InventoryPath(string Path, bool IsDirectory);
public sealed record InventoryDirectoryReadResult(IReadOnlyList<InventoryPath> Entries, Exception? Error);
public sealed record InventoryFileReadResult(long Size, Exception? Error);
public interface IDirectoryInventorySource
{
    InventoryDirectoryReadResult Enumerate(string path);
    InventoryFileReadResult ReadFile(string path);
    bool IsReparsePoint(string path);
}
public sealed record HfaProbeResult(bool IsRecognized);
public interface IHfaArchiveProbe
{
    HfaProbeResult Probe(string path);
}

public enum GameRootInventoryOutcome
{
    Completed,
    Cancelled,
    Error,
}

public sealed record GameRootInventoryCoverage(
    int ScannedFileCount,
    int ConfirmedArchiveCount,
    int UnrecognizedCount,
    int UnreadableCount,
    int UnreadableDirectoryCount,
    int SkippedReparsePathCount);

public sealed record DiscoveredArchive(
    string FullPath,
    string RelativePath,
    string Name,
    long Size)
{
    public string ContainingFolder =>
        Path.GetDirectoryName(FullPath) is { Length: > 0 } folder
            ? Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : "游戏根目录";
}

public sealed class GameRootInventoryResult
{
    public GameRootInventoryResult(
        string rootPath,
        IEnumerable<DiscoveredArchive> archives,
        GameRootInventoryCoverage coverage,
        GameRootInventoryOutcome outcome,
        Exception? error = null)
    {
        RootPath = rootPath;
        Archives = Array.AsReadOnly(archives.ToArray());
        Coverage = coverage;
        Outcome = outcome;
        Error = error;
    }

    public string RootPath { get; }
    public IReadOnlyList<DiscoveredArchive> Archives { get; }
    public GameRootInventoryCoverage Coverage { get; }
    public GameRootInventoryOutcome Outcome { get; }
    public Exception? Error { get; }
}

public sealed class GameRootInventoryService
{
    private readonly IDirectoryInventorySource? source;
    private readonly IHfaArchiveProbe? probe;

    public GameRootInventoryService(IDirectoryInventorySource? source = null, IHfaArchiveProbe? probe = null)
    {
        this.source = source;
        this.probe = probe;
    }

    public Task<GameRootInventoryResult> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return Task.Run(() => Scan(rootPath, cancellationToken), CancellationToken.None);
    }

    private GameRootInventoryResult Scan(string rootPath, CancellationToken cancellationToken)
    {
        if (source is not null && probe is not null)
        {
            return ScanFromSource(rootPath, cancellationToken);
        }
        var normalizedRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(normalizedRoot))
        {
            return new GameRootInventoryResult(normalizedRoot, [], new(0, 0, 0, 0, 0, 0), GameRootInventoryOutcome.Error,
                new DirectoryNotFoundException(normalizedRoot));
        }

        var archives = new List<DiscoveredArchive>();
        var coverage = new MutableCoverage();
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);

        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                string[] children;
                try
                {
                    children = Directory.GetFileSystemEntries(directory);
                }
                catch (UnauthorizedAccessException error)
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(directory, normalizedRoot))
                    {
                        return new GameRootInventoryResult(normalizedRoot, [], new(0, 0, 0, 0, 0, 0), GameRootInventoryOutcome.Error, error);
                    }
                    coverage.UnreadableDirectoryCount++;
                    continue;
                }
                catch (IOException error)
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(directory, normalizedRoot))
                    {
                        return new GameRootInventoryResult(normalizedRoot, [], new(0, 0, 0, 0, 0, 0), GameRootInventoryOutcome.Error, error);
                    }
                    coverage.UnreadableDirectoryCount++;
                    continue;
                }

                foreach (var child in children.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Directory.Exists(child))
                    {
                        if (IsReparsePoint(child)) coverage.SkippedReparsePathCount++;
                        else pending.Push(child);
                        continue;
                    }

                    coverage.ScannedFileCount++;
                    if (!Path.GetExtension(child).Equals(".hfa", StringComparison.OrdinalIgnoreCase))
                    {
                        coverage.UnrecognizedCount++;
                        continue;
                    }

                    try
                    {
                        var info = new FileInfo(child);
                        using var stream = new FileStream(child, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var session = ArchiveApplication.OpenHfa(stream, child);
                        archives.Add(new DiscoveredArchive(
                            Path.GetFullPath(child),
                            Path.GetRelativePath(normalizedRoot, child),
                            info.Name,
                            info.Length));
                        coverage.ConfirmedArchiveCount++;
                    }
                    catch (UnauthorizedAccessException) { coverage.UnreadableCount++; }
                    catch (IOException) { coverage.UnreadableCount++; }
                    catch (ApplicationException) { coverage.UnrecognizedCount++; }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BuildResult(normalizedRoot, archives, coverage, GameRootInventoryOutcome.Cancelled);
        }

        return BuildResult(normalizedRoot, archives, coverage, GameRootInventoryOutcome.Completed);
    }

    private GameRootInventoryResult ScanFromSource(string rootPath, CancellationToken cancellationToken)
    {
        var coverage = new MutableCoverage();
        var archives = new List<DiscoveredArchive>();
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var listing = source!.Enumerate(directory);
            if (listing.Error is not null)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(directory, rootPath))
                {
                    return new GameRootInventoryResult(rootPath, [], new(0, 0, 0, 0, 0, 0), GameRootInventoryOutcome.Error, listing.Error);
                }
                coverage.UnreadableDirectoryCount++;
                continue;
            }
            foreach (var entry in listing.Entries)
            {
                if (entry.IsDirectory)
                {
                    if (source.IsReparsePoint(entry.Path)) coverage.SkippedReparsePathCount++;
                    else pending.Push(entry.Path);
                    continue;
                }
                coverage.ScannedFileCount++;
                if (!Path.GetExtension(entry.Path).Equals(".hfa", StringComparison.OrdinalIgnoreCase))
                {
                    coverage.UnrecognizedCount++;
                    continue;
                }
                var file = source.ReadFile(entry.Path);
                if (file.Error is not null) { coverage.UnreadableCount++; continue; }
                try
                {
                    if (!probe!.Probe(entry.Path).IsRecognized) { coverage.UnrecognizedCount++; continue; }
                }
                catch (UnauthorizedAccessException) { coverage.UnreadableCount++; continue; }
                catch (IOException) { coverage.UnreadableCount++; continue; }
                catch (ApplicationException) { coverage.UnrecognizedCount++; continue; }
                coverage.ConfirmedArchiveCount++;
                archives.Add(new DiscoveredArchive(entry.Path, entry.Path, Path.GetFileName(entry.Path), file.Size));
            }
        }
        return BuildResult(rootPath, archives, coverage, GameRootInventoryOutcome.Completed);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static GameRootInventoryResult BuildResult(string root, List<DiscoveredArchive> archives, MutableCoverage coverage, GameRootInventoryOutcome outcome) =>
        new(root, archives.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase), coverage.Snapshot(), outcome);

    private sealed class MutableCoverage
    {
        public int ScannedFileCount;
        public int ConfirmedArchiveCount;
        public int UnrecognizedCount;
        public int UnreadableCount;
        public int UnreadableDirectoryCount;
        public int SkippedReparsePathCount;
        public GameRootInventoryCoverage Snapshot() => new(ScannedFileCount, ConfirmedArchiveCount, UnrecognizedCount, UnreadableCount, UnreadableDirectoryCount, SkippedReparsePathCount);
    }
}
