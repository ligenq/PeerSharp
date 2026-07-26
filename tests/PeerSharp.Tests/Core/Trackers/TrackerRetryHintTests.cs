using PeerSharp.BEncoding;
using PeerSharp.Internals.Trackers;

namespace PeerSharp.Tests.Core.Trackers;

/// <summary>
/// BEP 31: parsing of the <c>retry in</c> key. The value is either a positive integer count of
/// minutes or the string <c>never</c>; everything else has to read as "no hint" so the caller falls
/// back to its own backoff rather than inventing a delay from a malformed value.
/// </summary>
public class TrackerRetryHintTests
{
    private static BDict Failure(IBNode? retryIn)
    {
        var dict = new BDict();
        dict.Dict["failure reason"] = new BString(System.Text.Encoding.UTF8.GetBytes("Not a tracker"));
        if (retryIn != null)
        {
            dict.Dict["retry in"] = retryIn;
        }

        return dict;
    }

    [Fact]
    public void TryParse_NoRetryInKey_ReturnsNull()
    {
        Assert.Null(TrackerRetryHint.TryParse(Failure(null)));
    }

    [Fact]
    public void TryParse_Never_ReturnsNeverRetry()
    {
        var hint = TrackerRetryHint.TryParse(Failure(new BString("never"u8.ToArray())));

        Assert.NotNull(hint);
        Assert.True(hint.Value.Never);
    }

    [Fact]
    public void TryParse_PositiveInteger_IsReadAsMinutes()
    {
        var hint = TrackerRetryHint.TryParse(Failure(new BNumber(15)));

        Assert.NotNull(hint);
        Assert.False(hint.Value.Never);
        Assert.Equal(TimeSpan.FromMinutes(15), hint.Value.RetryIn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void TryParse_NonPositiveInteger_ReturnsNull(long minutes)
    {
        // The spec says positive. A zero would mean "retry immediately", which is exactly what a
        // failing tracker should not be able to ask for.
        Assert.Null(TrackerRetryHint.TryParse(Failure(new BNumber(minutes))));
    }

    [Fact]
    public void TryParse_ImplausiblyLargeInteger_SaturatesInsteadOfOverflowing()
    {
        // TimeSpan.FromMinutes(long.MaxValue) throws; the hint must survive a hostile value so the
        // caller can clamp it.
        var hint = TrackerRetryHint.TryParse(Failure(new BNumber(long.MaxValue)));

        Assert.NotNull(hint);
        Assert.False(hint.Value.Never);
        Assert.True(hint.Value.RetryIn > TimeSpan.FromDays(365));
    }

    [Fact]
    public void TryParse_UnrecognisedString_ReturnsNull()
    {
        Assert.Null(TrackerRetryHint.TryParse(Failure(new BString("soon"u8.ToArray()))));
    }

    [Fact]
    public void TryParse_NeverIsCaseInsensitive()
    {
        var hint = TrackerRetryHint.TryParse(Failure(new BString("Never"u8.ToArray())));

        Assert.NotNull(hint);
        Assert.True(hint.Value.Never);
    }
}
