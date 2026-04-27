namespace PeerSharp.WebTorrent.Network;

internal interface IWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    Task SendTextAsync(string text, CancellationToken cancellationToken);
    Task<string> ReceiveTextAsync(CancellationToken cancellationToken);
}

internal interface IWebSocketConnectionFactory
{
    IWebSocketConnection Create();
}
