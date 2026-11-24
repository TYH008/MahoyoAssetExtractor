namespace VNAssetExplorer.Formats.Hfa;

public enum HfaErrorCode
{
    StreamNotReadable,
    StreamNotSeekable,
    TruncatedHeader,
    WrongMagic,
    EntryCountTooLarge,
    TruncatedDirectory,
    InvalidUtf8Name,
    NonCanonicalNamePadding,
    UnsafeEntryName,
    EntryRangePastEnd,
}
