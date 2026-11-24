namespace VNAssetExplorer.Formats.Hfa;

internal static class HfaEntryName
{
    private static readonly char[] InvalidWindowsNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedDeviceStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsSafe(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.EndsWith(' ')
            || name.EndsWith('.')
            || name.IndexOfAny(InvalidWindowsNameCharacters) >= 0
            || name.Any(char.IsControl))
        {
            return false;
        }

        var stem = name.Split('.', 2)[0];
        return !ReservedDeviceStems.Contains(stem);
    }
}
