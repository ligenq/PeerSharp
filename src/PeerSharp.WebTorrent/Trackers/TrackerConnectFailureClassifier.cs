using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;

namespace PeerSharp.WebTorrent.Trackers;

internal static class TrackerConnectFailureClassifier
{
    public static bool IsExpected(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case AuthenticationException:
                case TimeoutException:
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain or SocketError.ConnectionRefused or SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable }:
                    return true;
                case HttpRequestException httpEx when httpEx.StatusCode is HttpStatusCode code && (int)code is >= 400 and < 500:
                    return true;
                case WebSocketException wsEx when wsEx.Message.Contains("status code", StringComparison.OrdinalIgnoreCase):
                    return true;
            }
        }

        return false;
    }

    public static bool IsTerminal(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case AuthenticationException:
                    return true;
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }:
                    return true;
                case HttpRequestException httpEx when httpEx.StatusCode is HttpStatusCode code && IsTerminalStatusCode(code):
                    return true;
                case WebSocketException wsEx when TryGetHandshakeStatusCode(wsEx.Message, out var statusCode) && IsTerminalStatusCode(statusCode):
                    return true;
            }
        }

        return false;
    }

    public static string Describe(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case AuthenticationException:
                    return "TLS certificate validation failed";
                case SocketException sockEx when sockEx.SocketErrorCode == SocketError.HostNotFound:
                    return "DNS lookup failed";
                case SocketException sockEx:
                    return $"socket error ({sockEx.SocketErrorCode})";
                case TimeoutException:
                    return "tracker connect timed out";
                case HttpRequestException httpEx when httpEx.StatusCode.HasValue:
                    return $"HTTP {(int)httpEx.StatusCode.Value} from tracker";
                case WebSocketException wsEx when wsEx.Message.Contains("status code", StringComparison.OrdinalIgnoreCase):
                    return wsEx.Message;
            }
        }

        return ex.Message;
    }

    private static bool IsTerminalStatusCode(HttpStatusCode statusCode)
        => (int)statusCode is >= 400 and < 500
            && statusCode is not HttpStatusCode.RequestTimeout
            && (int)statusCode != 429;

    private static bool TryGetHandshakeStatusCode(string message, out HttpStatusCode statusCode)
    {
        statusCode = default;

        const string marker = "status code '";
        int markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        int digitsStart = markerIndex + marker.Length;
        int digitsEnd = digitsStart;
        while (digitsEnd < message.Length && char.IsDigit(message[digitsEnd]))
        {
            digitsEnd++;
        }

        if (digitsEnd == digitsStart || !int.TryParse(message.AsSpan(digitsStart, digitsEnd - digitsStart), out int value))
        {
            return false;
        }

        statusCode = (HttpStatusCode)value;
        return true;
    }
}
