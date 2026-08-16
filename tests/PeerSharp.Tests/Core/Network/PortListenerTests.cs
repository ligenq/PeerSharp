using System.Net;
using System.Net.Sockets;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Core.Network;

public class PortListenerTests
{
    private class MockTcpListener : ITcpListener
    {
        private readonly int _port;
        private readonly TaskCompletionSource _acceptStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _acceptCancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Threading.Channels.Channel<TcpClient> _clientChannel =
            System.Threading.Channels.Channel.CreateUnbounded<TcpClient>();

        public MockTcpListener(int port)
        {
            _port = port;
        }

        public Task AcceptStarted => _acceptStarted.Task;
        public Task AcceptCancelled => _acceptCancelled.Task;

        public void EnqueueClient(TcpClient client)
        {
            _clientChannel.Writer.TryWrite(client);
        }

        public void Start() { }
        public void Stop()
        {
            _acceptCancelled.TrySetResult();
        }
        public async Task<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken)
        {
            return await AcceptCoreAsync(cancellationToken);
        }

        public EndPoint? LocalEndpoint => new IPEndPoint(IPAddress.Any, _port);
        public void Dispose() { }

        private async Task<TcpClient> AcceptCoreAsync(CancellationToken cancellationToken)
        {
            _acceptStarted.TrySetResult();
            await using var reg = cancellationToken.Register(() => _acceptCancelled.TrySetResult());
            return await _clientChannel.Reader.ReadAsync(cancellationToken);
        }
    }

    private class MockTcpListenerFactory : ITcpListenerFactory
    {
        public IPAddress? LastAddress { get; private set; }
        public MockTcpListener? LastListener { get; private set; }
        public ITcpListener Create(IPAddress address, int port)
        {
            LastAddress = address;
            LastListener = new MockTcpListener(port);
            return LastListener;
        }
    }

    private class MockResolver : ITorrentResolver
    {
        public ITorrent? GetTorrent(InfoHash hash)
        {
            return null;
        }

        public IReadOnlyList<ITorrent> GetTorrents()
        {
            return Array.Empty<ITorrent>();
        }
    }

    [Fact]
    public void Start_StartsTcpListener()
    {
        var factory = new MockTcpListenerFactory();
        var listener = new PortListener(new MockResolver(), factory);

        listener.Start(5000);

        Assert.Equal(5000, listener.Port);
        listener.Stop();
    }

    [Fact]
    public void Start_WithBindAddress_PassesSpecificAddressToListenerFactory()
    {
        var factory = new MockTcpListenerFactory();
        var bindAddress = IPAddress.Parse("127.0.0.2");
        var listener = new PortListener(
            new MockResolver(),
            factory,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            bindAddress);

        listener.Start(5000);

        Assert.Equal(bindAddress, factory.LastAddress);
        listener.Stop();
    }

    [Fact]
    public async Task Stop_CancelsAcceptLoop()
    {
        var factory = new MockTcpListenerFactory();
        var listener = new PortListener(new MockResolver(), factory);

        listener.Start(0);
        var mock = factory.LastListener ?? throw new InvalidOperationException("Listener not created.");
        await mock.AcceptStarted;

        listener.Stop();

        var completed = await Task.WhenAny(
            mock.AcceptCancelled,
            Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(mock.AcceptCancelled, completed);
    }
}





