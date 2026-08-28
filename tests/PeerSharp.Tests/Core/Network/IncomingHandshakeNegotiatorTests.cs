using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PeerSharp.Tests.Core.Network;

/// <summary>
/// Deciding what an inbound peer is speaking before it has said anything we can rely on.
/// </summary>
/// <remarks>
/// This runs before anything is authenticated, on every connection the engine accepts, and it had no
/// tests. Getting it wrong does not corrupt data - it silently refuses peers, which looks like a
/// quiet swarm rather than like a defect, and is how the inbound uTP path came to reject every
/// encrypted peer for as long as it did.
/// </remarks>
public class IncomingHandshakeNegotiatorTests
{
    /// <summary>
    /// A private key whose public key begins with 0x13 - the same byte that introduces a plaintext
    /// BitTorrent handshake. Found by search; a real peer hits this roughly once in 256 connections,
    /// because an MSE key is indistinguishable from random bytes by design.
    /// </summary>
    private const int AmbiguousKeySeed = 32;

    [Fact(Timeout = 30_000)]
    public async Task APlaintextHandshakeIsReadWhole()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        var resolver = new SingleTorrentResolver(torrent);

        using var pair = await StreamPair.CreateAsync();

        byte[] handshake = BuildBitTorrentHandshake(torrent.Hash.ToArray(), Encoding.ASCII.GetBytes("-PS0001-PLAINTEXT000"));
        var negotiate = IncomingHandshakeNegotiator.NegotiateAsync(
            pair.Server, resolver, NullLogger.Instance, TestContext.Current.CancellationToken);

        await pair.Client.WriteAsync(handshake, TestContext.Current.CancellationToken);
        var result = await negotiate;

        Assert.True(result.Success);
        Assert.Null(result.Encryption);
        Assert.Equal(handshake, result.Handshake);
        Assert.Equal(torrent.Hash, result.InfoHash);
    }

    [Fact(Timeout = 30_000)]
    public async Task APlaintextHandshakeSplitAcrossReadsIsStillReadWhole()
    {
        // TCP gives no guarantee the 68 bytes arrive together, and a peer that sends the length byte
        // and then pauses is ordinary rather than hostile.
        var torrent = TorrentTestUtility.CreateMinimal();
        var resolver = new SingleTorrentResolver(torrent);

        using var pair = await StreamPair.CreateAsync();

        byte[] handshake = BuildBitTorrentHandshake(torrent.Hash.ToArray(), Encoding.ASCII.GetBytes("-PS0001-SPLIT0000000"));
        var negotiate = IncomingHandshakeNegotiator.NegotiateAsync(
            pair.Server, resolver, NullLogger.Instance, TestContext.Current.CancellationToken);

        foreach (var slice in new[] { handshake[..1], handshake[1..20], handshake[20..67], handshake[67..] })
        {
            await pair.Client.WriteAsync(slice, TestContext.Current.CancellationToken);
            await pair.Client.FlushAsync(TestContext.Current.CancellationToken);
            await Task.Delay(15, TestContext.Current.CancellationToken);
        }

        var result = await negotiate;

        Assert.True(result.Success);
        Assert.Equal(handshake, result.Handshake);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnEncryptedPeerIsNegotiated()
    {
        var result = await NegotiateEncryptedAsync(new DiffieHellman());

        Assert.True(result.Success);
        Assert.NotNull(result.Encryption);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnEncryptedPeerWhoseKeyBeginsLikeAPlaintextHandshakeIsStillNegotiated()
    {
        // The opening byte is the only thing separating the two protocols, and an MSE key is random,
        // so one key in 256 starts with the byte a plaintext handshake starts with. Reading those 96
        // random bytes as a handshake yields an info hash belonging to no torrent, and the peer is
        // dropped - rare enough to look like ordinary swarm churn and never like a bug.
        var result = await NegotiateEncryptedAsync(new DiffieHellman(AmbiguousKey()));

        Assert.True(result.Success, "an encrypted peer was refused because its key began with 0x13");
        Assert.NotNull(result.Encryption);
    }

    [Fact(Timeout = 30_000)]
    public async Task APeerThatSaysNothingIsRefused()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        using var pair = await StreamPair.CreateAsync();

        var negotiate = IncomingHandshakeNegotiator.NegotiateAsync(
            pair.Server, new SingleTorrentResolver(torrent), NullLogger.Instance, TestContext.Current.CancellationToken);

        pair.Client.Dispose();

        Assert.False((await negotiate).Success);
    }

    [Fact(Timeout = 30_000)]
    public async Task APeerThatSendsGarbageIsRefusedRatherThanHanging()
    {
        // Not a handshake and not a key exchange. The encryption handshake caps how much it will
        // buffer, so this has to end in a refusal rather than reading until the connection dies.
        var torrent = TorrentTestUtility.CreateMinimal();
        using var pair = await StreamPair.CreateAsync();

        var negotiate = IncomingHandshakeNegotiator.NegotiateAsync(
            pair.Server, new SingleTorrentResolver(torrent), NullLogger.Instance, TestContext.Current.CancellationToken);

        byte[] noise = new byte[4096];
        RandomNumberGenerator.Fill(noise);
        noise[0] = 0x99; // Not 19, so it is taken for a key exchange.

        try
        {
            for (int i = 0; i < 8; i++)
            {
                await pair.Client.WriteAsync(noise, TestContext.Current.CancellationToken);
            }
        }
        catch (IOException)
        {
            // The negotiator giving up first closes the connection under us, which is the point.
        }

        Assert.False((await negotiate).Success);
    }

    private static async Task<IncomingHandshakeNegotiator.Result> NegotiateEncryptedAsync(DiffieHellman exchange)
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        byte[] infoHash = torrent.Hash.ToArray();
        var resolver = new SingleTorrentResolver(torrent);

        using var pair = await StreamPair.CreateAsync();

        // Bounded on both sides. A negotiator that refuses this peer simply stops answering, so
        // without a deadline the test would hang rather than report which side gave up.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        var negotiate = IncomingHandshakeNegotiator.NegotiateAsync(
            pair.Server, resolver, NullLogger.Instance, deadline.Token);

        using var initiator = new ProtocolEncryptionHandshake(infoHash, initiator: true, exchange)
        {
            InitialPayload = BuildBitTorrentHandshake(infoHash, Encoding.ASCII.GetBytes("-PS0001-ENCRYPTED000"))
        };

        await pair.Client.WriteAsync(initiator.Initiate(), deadline.Token);

        try
        {
            byte[] buffer = new byte[8192];
            while (!initiator.IsComplete && !initiator.IsError)
            {
                int read = await pair.Client.ReadAsync(buffer, deadline.Token);
                if (read == 0)
                {
                    break;
                }

                byte[] reply = initiator.HandleIncoming(buffer[..read]);
                if (reply.Length > 0)
                {
                    await pair.Client.WriteAsync(reply, deadline.Token);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The far side stopped talking. Whatever the negotiator concluded is the answer.
        }

        try
        {
            return await negotiate;
        }
        catch (OperationCanceledException)
        {
            return IncomingHandshakeNegotiator.Failed;
        }
    }

    private static byte[] AmbiguousKey()
    {
        byte[] key = new byte[96];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)((i * 31 + (AmbiguousKeySeed * 7) + 11) & 0xFF);
        }

        return key;
    }

    private static byte[] BuildBitTorrentHandshake(byte[] infoHash, byte[] peerId)
    {
        byte[] message = new byte[68];
        message[0] = 19;
        "BitTorrent protocol"u8.CopyTo(message.AsSpan(1));
        message[25] = 0x10;
        infoHash.CopyTo(message.AsSpan(28));
        peerId.CopyTo(message.AsSpan(48));
        return message;
    }

    private sealed class SingleTorrentResolver(ITorrent torrent) : ITorrentResolver
    {
        public ITorrent? GetTorrent(InfoHash hash) => torrent.Hash == hash ? torrent : null;

        public IReadOnlyList<ITorrent> GetTorrents() => [torrent];
    }

    /// <summary>A connected pair of loopback streams, so both ends behave like a real socket.</summary>
    private sealed class StreamPair : IDisposable
    {
        private readonly TcpClient _clientSocket;
        private readonly TcpClient _serverSocket;
        private readonly TcpListener _listener;

        private StreamPair(TcpListener listener, TcpClient clientSocket, TcpClient serverSocket)
        {
            _listener = listener;
            _clientSocket = clientSocket;
            _serverSocket = serverSocket;
            Client = clientSocket.GetStream();
            Server = serverSocket.GetStream();
        }

        public NetworkStream Client { get; }
        public NetworkStream Server { get; }

        public static async Task<StreamPair> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await listener.AcceptTcpClientAsync();
            await connect;

            return new StreamPair(listener, client, server);
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
            _clientSocket.Dispose();
            _serverSocket.Dispose();
            _listener.Stop();
        }
    }
}
