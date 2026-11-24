namespace VNAssetExplorer.Formats.Hfa;

public sealed record HfaReaderOptions
{
    public const int DefaultMaxEntryCount = 1_000_000;

    public int MaxEntryCount { get; init; } = DefaultMaxEntryCount;
}
