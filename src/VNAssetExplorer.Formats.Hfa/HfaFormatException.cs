namespace VNAssetExplorer.Formats.Hfa;

public sealed class HfaFormatException : Exception
{
    public HfaFormatException(HfaErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public HfaErrorCode Code { get; }
}
