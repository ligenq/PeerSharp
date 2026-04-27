using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using PeerSharp.WebTorrent.Trackers;

namespace PeerSharp.WebTorrent.Tests;

public class TrackerConnectFailureClassifierTests
{
    [Fact]
    public void IsExpected_ReturnsTrue_ForAuthenticationException()
    {
        var ex = new AuthenticationException("cert mismatch");
        Assert.True(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.True(TrackerConnectFailureClassifier.IsTerminal(ex));
        Assert.Equal("TLS certificate validation failed", TrackerConnectFailureClassifier.Describe(ex));
    }

    [Fact]
    public void IsExpected_ReturnsTrue_WhenAuthenticationExceptionIsInner()
    {
        var ex = new InvalidOperationException("wrapper", new AuthenticationException("inner"));
        Assert.True(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.True(TrackerConnectFailureClassifier.IsTerminal(ex));
        Assert.Equal("TLS certificate validation failed", TrackerConnectFailureClassifier.Describe(ex));
    }

    [Theory]
    [InlineData(SocketError.TryAgain)]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    public void IsExpected_ReturnsTrue_ForKnownSocketErrors(SocketError error)
    {
        var ex = new SocketException((int)error);
        Assert.True(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.False(TrackerConnectFailureClassifier.IsTerminal(ex));
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NoData)]
    public void IsTerminal_ReturnsTrue_ForPermanentNameResolutionFailures(SocketError error)
    {
        var ex = new SocketException((int)error);
        Assert.True(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.True(TrackerConnectFailureClassifier.IsTerminal(ex));
    }

    [Fact]
    public void IsExpected_ReturnsTrue_ForTimeoutException()
    {
        var ex = new TimeoutException("tracker connect timed out");
        Assert.True(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.False(TrackerConnectFailureClassifier.IsTerminal(ex));
        Assert.Equal("tracker connect timed out", TrackerConnectFailureClassifier.Describe(ex));
    }

    [Fact]
    public void Describe_DnsFailure_ReturnsFriendlyMessage()
    {
        var ex = new SocketException((int)SocketError.HostNotFound);
        Assert.Equal("DNS lookup failed", TrackerConnectFailureClassifier.Describe(ex));
    }

    [Fact]
    public void Describe_OtherSocketError_IncludesCode()
    {
        var ex = new SocketException((int)SocketError.ConnectionRefused);
        Assert.Equal($"socket error ({SocketError.ConnectionRefused})", TrackerConnectFailureClassifier.Describe(ex));
    }

    [Fact]
    public void IsExpected_ReturnsFalse_ForUnexpectedSocketError()
    {
        var ex = new SocketException((int)SocketError.AccessDenied);
        Assert.False(TrackerConnectFailureClassifier.IsExpected(ex));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public void IsExpected_ReturnsTrue_For4xxHttpStatus(HttpStatusCode status)
    {
        var ex = new HttpRequestException("request failed", inner: null, statusCode: status);
        Assert.True(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.Equal(status is HttpStatusCode.RequestTimeout ? false : (int)status != 429, TrackerConnectFailureClassifier.IsTerminal(ex));
        Assert.Equal($"HTTP {(int)status} from tracker", TrackerConnectFailureClassifier.Describe(ex));
    }

    [Fact]
    public void IsExpected_ReturnsFalse_For5xxHttpStatus()
    {
        var ex = new HttpRequestException("server error", inner: null, statusCode: HttpStatusCode.InternalServerError);
        Assert.False(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.False(TrackerConnectFailureClassifier.IsTerminal(ex));
    }

    [Fact]
    public void IsExpected_ReturnsFalse_ForHttpRequestExceptionWithoutStatus()
    {
        var ex = new HttpRequestException("connection failed");
        Assert.False(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.False(TrackerConnectFailureClassifier.IsTerminal(ex));
    }

    [Fact]
    public void IsExpected_ReturnsTrue_ForWebSocketExceptionWithStatusCodeMessage()
    {
        var ex = new WebSocketException("The server returned status code '404' when status code '101' was expected.");
        Assert.True(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.True(TrackerConnectFailureClassifier.IsTerminal(ex));
        Assert.Equal(ex.Message, TrackerConnectFailureClassifier.Describe(ex));
    }

    [Fact]
    public void IsTerminal_ReturnsFalse_ForWebSocket429Handshake()
    {
        var ex = new WebSocketException("The server returned status code '429' when status code '101' was expected.");
        Assert.True(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.False(TrackerConnectFailureClassifier.IsTerminal(ex));
    }

    [Fact]
    public void IsExpected_ReturnsFalse_ForGenericException()
    {
        var ex = new InvalidOperationException("nope");
        Assert.False(TrackerConnectFailureClassifier.IsExpected(ex));
        Assert.False(TrackerConnectFailureClassifier.IsTerminal(ex));
    }

    [Fact]
    public void Describe_FallsBackToExceptionMessage_WhenUnclassified()
    {
        var ex = new InvalidOperationException("some unexpected failure");
        Assert.Equal("some unexpected failure", TrackerConnectFailureClassifier.Describe(ex));
    }

    [Fact]
    public void IsExpected_WalksInnerExceptions()
    {
        var inner = new SocketException((int)SocketError.HostNotFound);
        var middle = new HttpRequestException("dns", inner);
        var outer = new InvalidOperationException("outer", middle);

        Assert.True(TrackerConnectFailureClassifier.IsExpected(outer));
        Assert.True(TrackerConnectFailureClassifier.IsTerminal(outer));
        Assert.Equal("DNS lookup failed", TrackerConnectFailureClassifier.Describe(outer));
    }
}
