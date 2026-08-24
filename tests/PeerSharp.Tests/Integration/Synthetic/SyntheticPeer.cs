using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>
/// A BitTorrent peer written for the tests, sharing no code with PeerSharp.
///
/// <para>
/// It exists because the reference implementation cannot test the half of interop that matters most.
/// libtorrent is a conformant client: it will never send a malformed message, never pick an awkward
/// extension id to see whether we route by it, and never hang up at exactly the wrong moment. Every
/// interop defect found so far has been PeerSharp agreeing with itself and with nothing else, and
/// catching that needs a counterpart that behaves in ways a well-behaved client never would.
/// libtorrent reached the same conclusion about their own engine - <c>simulation/fake_peer.hpp</c> in
/// their tree is this, including a <c>send_invalid_message</c>.
/// </para>
///
/// <para>
/// It does not implement BitTorrent. It writes bytes, records the bytes that come back, and lets a
/// test make claims about them. Frames are kept raw: assertions run against what actually crossed the
/// socket rather than against PeerSharp's reading of it, because a decoder defect that is allowed to
/// interpret its own output will always look correct.
/// </para>
/// </summary>
internal sealed class SyntheticPeer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly SyntheticPeerOptions _options;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<SyntheticConnection> _connections = [];
    private readonly List<TcpClient> _clients = [];
    private readonly Task _acceptLoop;
    private readonly byte[] _peerId = BuildPeerId();

    private SyntheticPeer(TcpListener listener, SyntheticPeerOptions options)
    {
        _listener = listener;
        _options = options;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Binds a loopback port and begins accepting.</summary>
    public static SyntheticPeer Start(SyntheticPeerOptions options)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new SyntheticPeer(listener, options);
    }

    public int Port { get; }

    public IPEndPoint EndPoint => new(IPAddress.Loopback, Port);

    /// <summary>How many times PeerSharp has dialled, including attempts this peer then abandoned.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_connections)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>A snapshot of every connection so far, in the order they arrived.</summary>
    public IReadOnlyList<SyntheticConnection> Connections
    {
        get
        {
            lock (_connections)
            {
                return [.. _connections];
            }
        }
    }

    /// <summary>Waits for the connection at <paramref name="ordinal"/> (zero-based) to arrive.</summary>
    public async Task<SyntheticConnection> WaitForConnectionAsync(
        int ordinal, TimeSpan timeout, CancellationToken cancellationToken)
    {
        bool arrived = await WaitForAsync(() => ConnectionCount > ordinal, timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!arrived)
        {
            throw new TimeoutException(
                $"Connection {ordinal + 1} never arrived within {timeout.TotalSeconds:0.#}s; " +
                $"{ConnectionCount} connection(s) were made in total.");
        }

        return Connections[ordinal];
    }

    /// <summary>Polls a condition. Simpler than a signal per thing a test might wait on, and enough here.</summary>
    public static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return condition();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            SyntheticConnection connection;
            lock (_connections)
            {
                _clients.Add(client);
                connection = new SyntheticConnection(_connections.Count);
                _connections.Add(connection);
            }

            // Each connection runs on its own so a peer that hangs up on the first does not stall the
            // second - which is the whole subject of the fast-reconnect test.
            _ = Task.Run(() => ServeAsync(client, connection));
        }
    }

    private async Task ServeAsync(TcpClient client, SyntheticConnection connection)
    {
        try
        {
            using var stream = client.GetStream();
            connection.AttachStream(stream);

            // The first byte decides what this is. A plaintext handshake opens with the protocol
            // string's length; anything else is the MSE key exchange, which begins with a Diffie-
            // Hellman public key and is indistinguishable from random.
            byte[] first = new byte[1];
            if (await stream.ReadAsync(first, _stopping.Token).ConfigureAwait(false) == 0)
            {
                connection.Complete();
                return;
            }

            bool plaintext = first[0] == 19;
            connection.RecordOpening(plaintext);

            if (_options.HangUpDuringHandshake)
            {
                // Hard reset rather than a graceful close: this imitates a peer at its connection
                // limit, or one that has gone away, and it must not look like an orderly refusal.
                client.Client.LingerState = new LingerOption(true, 0);
                client.Close();
                connection.Complete();
                return;
            }

            if (!plaintext)
            {
                // The synthetic peer speaks no MSE. Tests that get here are about what PeerSharp does
                // when encryption goes unanswered, so the connection is left to time out.
                connection.Complete();
                return;
            }

            byte[] rest = new byte[67];
            await ReadExactlyAsync(stream, rest, _stopping.Token).ConfigureAwait(false);
            connection.RecordHandshake(rest);

            await stream.WriteAsync(BuildHandshake(rest.AsSpan(27, 20)), _stopping.Token).ConfigureAwait(false);

            if (_options.AdvertiseExtensionProtocol)
            {
                await SendExtensionHandshakeAsync(stream).ConfigureAwait(false);
            }

            // Everything we owe the peer has gone out, so a test may now send whatever it likes.
            connection.MarkReady();

            await ReadFramesAsync(stream, connection).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // A closed connection ends this connection's story, not the test's.
        }
        finally
        {
            connection.Complete();
        }
    }

    /// <summary>Our 68-byte handshake, echoing the info hash the dialler asked for.</summary>
    private byte[] BuildHandshake(ReadOnlySpan<byte> infoHash)
    {
        byte[] handshake = new byte[68];
        handshake[0] = 19;
        "BitTorrent protocol"u8.CopyTo(handshake.AsSpan(1));

        if (_options.AdvertiseExtensionProtocol)
        {
            handshake[25] |= 0x10; // BEP 10 extension protocol.
        }

        handshake[27] |= 0x04; // BEP 6 fast extension.

        infoHash.CopyTo(handshake.AsSpan(28));
        _peerId.CopyTo(handshake.AsSpan(48));

        return handshake;
    }

    /// <summary>
    /// A peer id unique to this instance. It has to be: a peer id identifies a peer rather than a
    /// connection, so two synthetic peers sharing one are a single peer dialled twice, and PeerSharp
    /// correctly closes the second as a duplicate. A test that stands two of them up and wonders why
    /// only one gets talked to has found its own bug.
    /// </summary>
    private static byte[] BuildPeerId()
    {
        byte[] peerId = new byte[20];
        Encoding.ASCII.GetBytes("-SY0001-").CopyTo(peerId.AsSpan());
        System.Security.Cryptography.RandomNumberGenerator.Fill(peerId.AsSpan(8));
        return peerId;
    }

    /// <summary>
    /// Our BEP 10 handshake. The extension ids here are the ones we are telling PeerSharp to address
    /// us by, and the numbers are chosen by us alone - that is the property the ut_metadata test turns
    /// on.
    /// </summary>
    private async Task SendExtensionHandshakeAsync(NetworkStream stream)
    {
        var extensions = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var pair in _options.Extensions)
        {
            extensions[pair.Key] = pair.Value;
        }

        var handshake = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["m"] = extensions,
            ["v"] = "SyntheticPeer/1.0",
            ["p"] = (long)Port
        };

        if (_options.MetadataSize is { } size)
        {
            handshake["metadata_size"] = size;
        }

        byte[] body = SyntheticBencode.Encode(handshake);
        byte[] payload = new byte[2 + body.Length];
        payload[0] = 20; // Extended.
        payload[1] = 0;  // The handshake is always extended id zero.
        body.CopyTo(payload.AsSpan(2));

        await SendFrameAsync(stream, payload).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(NetworkStream stream, byte[] payload)
    {
        byte[] framed = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(4));
        await stream.WriteAsync(framed, _stopping.Token).ConfigureAwait(false);
        await stream.FlushAsync(_stopping.Token).ConfigureAwait(false);
    }

    private async Task ReadFramesAsync(NetworkStream stream, SyntheticConnection connection)
    {
        byte[] header = new byte[4];
        while (!_stopping.IsCancellationRequested)
        {
            await ReadExactlyAsync(stream, header, _stopping.Token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32BigEndian(header);

            if (length == 0)
            {
                connection.Record(new WireFrame(WireFrame.KeepAlive, []));
                continue;
            }

            if (length < 0 || length > 1 << 20)
            {
                throw new InvalidOperationException($"A frame claimed {length} bytes, which no message can be.");
            }

            byte[] payload = new byte[length];
            await ReadExactlyAsync(stream, payload, _stopping.Token).ConfigureAwait(false);
            connection.Record(new WireFrame(payload[0], payload[1..]));
        }
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int received = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (received == 0)
            {
                throw new IOException("The peer closed the connection.");
            }

            read += received;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        lock (_connections)
        {
            foreach (var client in _clients)
            {
                try
                {
                    client.Close();
                }
                catch (SocketException)
                {
                    // Already gone.
                }
            }
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Shutdown races the accept.
        }

        _stopping.Dispose();
    }
}
