using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;

namespace PeerSharp.WebTorrent.Network;

[ExcludeFromCodeCoverage]
internal sealed class SystemWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    private readonly int _maxMessageBytes;

    public SystemWebSocketConnectionFactory(int maxMessageBytes)
    {
        _maxMessageBytes = maxMessageBytes;
    }

    public IWebSocketConnection Create() => new SystemWebSocketConnection(_maxMessageBytes);
}

[ExcludeFromCodeCoverage]
internal sealed class SystemWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly int _maxMessageBytes;

    public SystemWebSocketConnection(int maxMessageBytes)
    {
        _maxMessageBytes = Math.Max(1, maxMessageBytes);
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        return _webSocket.ConnectAsync(uri, cancellationToken);
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        await _webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using var ms = new CappedMemoryStream(_maxMessageBytes);

        while (true)
        {
            var result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return string.Empty;
            }

            await ms.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Best-effort close during disposal.
            }
        }

        _webSocket.Dispose();
    }
}
