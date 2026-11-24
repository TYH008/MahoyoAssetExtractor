namespace VNAssetExplorer.Core;

public static class ExtractionPath
{
    private static readonly char[] InvalidWindowsNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedDeviceStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path[0] is '/' or '\\' || Path.IsPathRooted(path))
        {
            throw new ArgumentException("The extraction path must be relative.", nameof(path));
        }

        var segments = path.Replace('\\', '/').Split('/');
        foreach (var segment in segments)
        {
            ValidateSegment(segment, path);
        }

        return string.Join('/', segments);
    }

    public static string ResolveUnderRoot(string outputRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        var root = Path.GetFullPath(outputRoot);
        var normalized = Normalize(relativePath);
        var candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The extraction path escapes the output root.", nameof(relativePath));
        }

        return candidate;
    }

    public static void EnsureNoReparsePoints(string outputRoot, string destination)
    {
        var root = Path.GetFullPath(outputRoot);
        var current = Path.GetDirectoryName(Path.GetFullPath(destination));
        while (current is not null)
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Extraction through a reparse point is not allowed.");
            }

            if (Path.GetPathRoot(current)?.Equals(current, StringComparison.OrdinalIgnoreCase) == true)
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static void ValidateSegment(string segment, string path)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || segment is "." or ".."
            || segment.EndsWith(' ')
            || segment.EndsWith('.')
            || segment.IndexOfAny(InvalidWindowsNameCharacters) >= 0
            || segment.Any(char.IsControl))
        {
            throw new ArgumentException("The extraction path contains an unsafe segment.", nameof(path));
        }

        var stem = segment.Split('.', 2)[0];
        if (ReservedDeviceStems.Contains(stem))
        {
            throw new ArgumentException("The extraction path uses a reserved Windows device name.", nameof(path));
        }
    }
}
