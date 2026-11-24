namespace VNAssetExplorer.App.Services;

// Music-room track table in the verified Steam WoH.exe, adjacent to mu_* UI resources.
internal static class GalleryAudioCatalog
{
    private static readonly HashSet<string> TrackNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "M01.hw", "M02.hw", "M01s.hw", "M04.hw", "M03.hw", "M06.hw", "M05.hw",
        "M08.hw", "M07.hw", "M10.hw", "M09.hw", "M12.hw", "M11.hw",
        "M14.hw", "M13.hw", "M16.hw", "M15.hw", "M18.hw", "M17.hw",
        "M21.hw", "M19.hw", "M23.hw", "M22.hw", "M25.hw", "M24.hw",
        "M27.hw", "M26.hw", "M29.hw", "M28.hw", "M31.hw", "M30.hw",
        "M33.hw", "M32.hw", "M35.hw", "M34.hw", "M37.hw", "M36.hw",
        "M39.hw", "M38.hw", "M41.hw", "M40.hw", "M43.hw", "M42.hw",
        "M45.hw", "M44.hw", "M47.hw", "M46.hw", "M49.hw", "M48.hw",
        "M51.hw", "M50.hw", "M53.hw", "M52.hw", "M55.hw", "M54.hw",
        "M57.hw", "M56.hw", "M59.hw", "M58.hw", "M61.hw", "M60.hw",
        "M63.hw", "M62.hw", "x_M64.hw",
    };

    internal static bool Contains(string name) => TrackNames.Contains(name);
}
