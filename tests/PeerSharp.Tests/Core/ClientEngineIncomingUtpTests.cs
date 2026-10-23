using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utp;

namespace PeerSharp.Tests.Core;

public sealed class ClientEngineIncomingUtpTests
{
    [Theory(Timeout = 15000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(20)]
    [InlineData(67)]
    public async Task PeerFinBeforeTheHandshakeCompletesIsAnOrdinaryRefusal(int bytesSent)
    {
        var timeProvider = new FakeTimeProvider();
        using var logs = new HandshakeLogs();
        await using var engine = ClientEngine.Create(new Settings(), timeProvider: timeProvider, loggerFactory: logs);
        await using var manager = new UtpManager(timeProvider);
        await using var stream = new UtpStream(manager, new IPEndPoint(IPAddress.Loopback, 12345), 100, 101, timeProvider);
        BeginIncoming(stream);
        byte[] handshake = Handshake();
        ushort sequence = 11;
        if (bytesSent > 0) Deliver(stream, MessageType.ST_DATA, sequence++, handshake[..bytesSent]);
        Deliver(stream, MessageType.ST_FIN, sequence, []);

        await HandleIncomingAsync(engine, stream).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(logs.Exceptions);
        Assert.Empty(engine.GetTorrents());
        Assert.Contains(logs.Messages, message => message.Contains("did not complete", StringComparison.Ordinal));
        Assert.True(stream.State is UtpState.Closing or UtpState.Closed);
    }

    [Theory(Timeout = 15000)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(67)]
    public async Task OneDeadlineEndsBothTheTlsProbeAndTheRemainingHandshakeWithoutExceptions(int bytesSent)
    {
        var timeProvider = new FakeTimeProvider();
        using var logs = new HandshakeLogs();
        await using var engine = ClientEngine.Create(new Settings(), timeProvider: timeProvider, loggerFactory: logs);
        await using var manager = new UtpManager(timeProvider);
        await using var stream = new UtpStream(manager, new IPEndPoint(IPAddress.Loopback, 12345), 100, 101, timeProvider);
        BeginIncoming(stream);
        if (bytesSent > 0) Deliver(stream, MessageType.ST_DATA, 11, Handshake()[..bytesSent]);
        Task handling = HandleIncomingAsync(engine, stream);
        Assert.False(handling.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(9));
        Assert.False(handling.IsCompleted);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(logs.Exceptions);
        Assert.Contains(logs.Messages, message => message.Contains("did not complete", StringComparison.Ordinal));
        Assert.True(stream.State is UtpState.Closing or UtpState.Closed);
    }

    [Fact(Timeout = 15000)]
    public async Task FragmentedProbePreservesTheCompletePlaintextHandshake()
    {
        var timeProvider = new FakeTimeProvider();
        using var logs = new HandshakeLogs();
        await using var engine = ClientEngine.Create(new Settings(), timeProvider: timeProvider, loggerFactory: logs);
        await using var manager = new UtpManager(timeProvider);
        await using var stream = new UtpStream(manager, new IPEndPoint(IPAddress.Loopback, 12345), 100, 101, timeProvider);
        BeginIncoming(stream);
        byte[] handshake = Handshake();
        Task handling = HandleIncomingAsync(engine, stream);
        // Each byte of the probe arrives in its own uTP packet, followed by the rest of the handshake.
        for (ushort i = 0; i < 6; i++) Deliver(stream, MessageType.ST_DATA, (ushort)(11 + i), [handshake[i]]);
        Deliver(stream, MessageType.ST_DATA, 17, handshake[6..]);
        await handling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(logs.Exceptions);
        Assert.Contains(logs.Messages, message => message.Contains("unknown info hash " + new InfoHash(handshake.AsSpan(28, 20)), StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("did not complete", StringComparison.Ordinal));
    }

    private static byte[] Handshake()
    {
        var info = new TorrentFileMetadata();
        info.Info.Hash = InfoHash.CreateRandom();
        return PeerHandshake.Create(info.Info, new byte[20]);
    }

    private static Task HandleIncomingAsync(ClientEngine engine, UtpStream stream) =>
        (Task)typeof(ClientEngine).GetMethod("HandleIncomingUtpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [stream])!;

    private static void BeginIncoming(UtpStream stream)
    {
        Deliver(stream, MessageType.ST_SYN, 10, []);
        Deliver(stream, MessageType.ST_STATE, 10, []);
        Assert.Equal(UtpState.Connected, stream.State);
    }

    private static void Deliver(UtpStream stream, MessageType type, ushort sequence, byte[] payload) =>
        stream.ProcessPacketWithSack(new MessageHeader
        {
            TypeVer = (byte)((byte)type << 4 | MessageHeader.CurrentVersion),
            SeqNr = sequence,
            AckNr = (ushort)(stream.SeqNr - 1),
            WndSize = 65535
        }, payload, 0, null, null, stream.RemoteEndPoint);

    private sealed class HandshakeLogs : ILoggerFactory, ILogger
    {
        public ConcurrentQueue<Exception> Exceptions { get; } = new();
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
            if (exception != null) Exceptions.Enqueue(exception);
        }
    }
}
