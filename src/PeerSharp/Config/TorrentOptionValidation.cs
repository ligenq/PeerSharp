namespace PeerSharp.Config;

internal static class TorrentOptionValidation
{
    public static void ValidateStrategy(DownloadStrategy value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
    }

    public static void ValidateRatio(float? value)
    {
        if (value is { } ratio && (!float.IsFinite(ratio) || ratio < 0))
            throw new ArgumentOutOfRangeException(nameof(value), "Ratio must be finite and non-negative.");
    }

    public static void ValidateSeedTime(TimeSpan? value)
    {
        if (value is { } time && time < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value));
    }
}
