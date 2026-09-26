using System.Net;
using System.Net.Sockets;
using PeerSharp.Internals.Network;

namespace PeerSharp.Tests.Core.Network;

/// <summary>
/// A peer connection's socket: every way it can end is end of input, never an exception, because a
/// swarm's connections end all the time and every reader treats the two alike.
/// </summary>
public sealed class SocketStreamTests : IDisposable
{
    private readonly Socket _ours;
    private readonly Socket _theirs;

    public SocketStreamTests()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        _ours = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _ours.Connect(listener.LocalEndPoint!);
        _theirs = listener.Accept();
    }

    public void Dispose()
    {
        _ours.Dispose();
        _theirs.Dispose();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadsAndWritesReachTheOtherEnd()
    {
        await using var stream = new SocketStream(_ours);

        await stream.WriteAsync("ping"u8.ToArray(), TestContext.Current.CancellationToken);
        byte[] ping = new byte[4];
        await ReceiveExactlyAsync(_theirs, ping);
        await _theirs.SendAsync("pong"u8.ToArray(), SocketFlags.None, TestContext.Current.CancellationToken);
        byte[] pong = new byte[4];
        await stream.ReadExactlyAsync(pong, TestContext.Current.CancellationToken);

        Assert.Equal("ping"u8.ToArray(), ping);
        Assert.Equal("pong"u8.ToArray(), pong);
    }

    [Fact(Timeout = 30000)]
    public async Task AReset_IsEndOfInput_WithTheReasonKept()
    {
        await using var stream = new SocketStream(_ours);

        // Closing with a zero linger sends RST rather than FIN.
        _theirs.LingerState = new LingerOption(true, 0);
        _theirs.Close();

        Assert.Equal(0, await stream.ReadAsync(new byte[16], TestContext.Current.CancellationToken));
        Assert.Equal(SocketError.ConnectionReset, stream.EndedBy);
    }

    [Fact(Timeout = 30000)]
    public async Task ACleanClose_IsEndOfInput_WithNothingToReport()
    {
        await using var stream = new SocketStream(_ours);

        _theirs.Shutdown(SocketShutdown.Send);

        Assert.Equal(0, await stream.ReadAsync(new byte[16], TestContext.Current.CancellationToken));
        Assert.Equal(SocketError.Success, stream.EndedBy);
    }

    [Fact(Timeout = 30000)]
    public async Task DisposingEndsAPendingRead()
    {
        // How a connection being torn down ends its receive loop.
        var stream = new SocketStream(_ours);
        var reading = stream.ReadAsync(new byte[16], TestContext.Current.CancellationToken);
        Assert.False(reading.IsCompleted);

        await stream.DisposeAsync();

        Assert.Equal(0, await reading);
    }

    [Fact(Timeout = 30000)]
    public async Task CancellingARead_GivesUpTheConnection_WithoutThrowing()
    {
        await using var stream = new SocketStream(_ours);
        using var cts = new CancellationTokenSource();

        var reading = stream.ReadAsync(new byte[16], cts.Token);
        await cts.CancelAsync();

        Assert.Equal(0, await reading);
        Assert.Equal(0, await stream.ReadAsync(new byte[16], TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30000)]
    public async Task AbortingEndsAPendingRead()
    {
        // A handshake's deadline, which closes the connection instead of cancelling the read.
        await using var stream = new SocketStream(_ours, ownsSocket: false);
        var reading = stream.ReadAsync(new byte[16], TestContext.Current.CancellationToken);

        stream.Abort();

        Assert.Equal(0, await reading);
    }

    [Fact(Timeout = 30000)]
    public async Task NotOwningTheSocket_DisposingLeavesItOpen()
    {
        // The incoming negotiation reads through one of these, then hands the socket to the peer.
        var negotiation = new SocketStream(_ours, ownsSocket: false);
        await negotiation.DisposeAsync();

        await using var peer = new SocketStream(_ours);
        await _theirs.SendAsync("hi"u8.ToArray(), SocketFlags.None, TestContext.Current.CancellationToken);
        byte[] hi = new byte[2];
        await peer.ReadExactlyAsync(hi, TestContext.Current.CancellationToken);

        Assert.Equal("hi"u8.ToArray(), hi);
    }

    [Fact(Timeout = 30000)]
    public async Task WritingToAClosedConnection_StillThrows()
    {
        // A write cannot say it failed any other way, and must not pretend the bytes went out.
        var stream = new SocketStream(_ours);
        stream.Abort();

        await Assert.ThrowsAsync<IOException>(async () => await stream.WriteAsync(new byte[16], TestContext.Current.CancellationToken));
        await stream.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public void TheSynchronousPathsBehaveTheSame()
    {
        using var stream = new SocketStream(_ours);

        stream.Write("abc"u8);
        byte[] received = new byte[3];
        int count = 0;
        while (count < 3)
        {
            count += _theirs.Receive(received.AsSpan(count));
        }

        _theirs.LingerState = new LingerOption(true, 0);
        _theirs.Close();

        Assert.Equal("abc"u8.ToArray(), received);
        Assert.Equal(0, stream.Read(new byte[16], 0, 16));
        Assert.Equal(SocketError.ConnectionReset, stream.EndedBy);
    }

    [Fact]
    public void ItIsANetworkStreamInShapeOnly()
    {
        using var stream = new SocketStream(_ours);

        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 1);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        stream.Flush();
        Assert.True(stream.FlushAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
    }

    private static async Task ReceiveExactlyAsync(Socket socket, byte[] buffer)
    {
        int count = 0;
        while (count < buffer.Length)
        {
            count += await socket.ReceiveAsync(buffer.AsMemory(count), SocketFlags.None, TestContext.Current.CancellationToken);
        }
    }
}
