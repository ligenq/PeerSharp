namespace PeerSharp.Internals.Network;

internal interface IPortListener : IAsyncDisposable
{
    int Port { get; }

    void Start(int port);

    void Stop();
}
