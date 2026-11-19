using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Integration;

[Collection("Integration")]
public sealed class EncryptedTransferRecoveryTests
{
    private const int BlockSize = ProtocolConstants.BlockSize;
    private const int PieceSize = 2 * BlockSize;
    private const string FileName = "transfer.bin";

    [Fact(Timeout = 45000)]
    [Trait("Suite", "ReplayableFaults")]
    public async Task AReplacementPeerCompletesTheDownloadAfterAnEncryptedWriteIsLost()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] payload = new byte[2 * PieceSize];
        new Random(711).NextBytes(payload);
        var file = new PeerSharp.Core.TorrentFileBuilder().WithName(FileName).WithPieceLength(PieceSize)
            .AddFile(FileName, payload).Build();
        var directory = Directory.CreateTempSubdirectory("PeerSharpEncryptedGap-");
        try
        {
            await using var engine = CreateEngine(directory.FullName);
            await engine.InitializeAsync(ct);
            var torrent = await engine.AddTorrentAsync(file, cancellationToken: ct);

            // A successful piece establishes that the connection can carry verified data before the loss.
            using var firstListener = new TcpListener(IPAddress.Loopback, 0);
            using var first = await ConnectAsync(firstListener, engine, torrent.Hash, ct);
            using var firstStream = first.GetStream();
            var firstProtocol = await OpenAsync(firstStream, torrent.Hash, 0x80, peerId: (byte)'A', ct);
            for (int i = 0; i < 2; i++)
            {
                var (piece, offset) = await ReadRequestAsync(firstProtocol, firstStream, ct);
                Assert.Equal(0, piece);
                await firstProtocol.WritePayloadAsync(firstStream, PieceFrame(piece, offset, payload), ct);
            }
            await TorrentTestUtility.WaitUntilAsync(() => torrent.PiecesReceived == 1, because: "the first piece to verify");

            var have = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(have, 1);
            await firstProtocol.WritePayloadAsync(firstStream, Frame(4, have), ct);
            var firstRequest = await ReadRequestAsync(firstProtocol, firstStream, ct);
            var secondRequest = await ReadRequestAsync(firstProtocol, firstStream, ct);
            Assert.Equal(1, firstRequest.Piece);
            Assert.Equal(1, secondRequest.Piece);
            Assert.NotEqual(firstRequest.Offset, secondRequest.Offset);

            byte[] firstBlock = PieceFrame(1, firstRequest.Offset, payload);
            byte[] secondBlock = PieceFrame(1, secondRequest.Offset, payload);
            // Model a sender losing queued bytes after preparing them for the wire. The receiver
            // gets part of the first block and then the next write from a later stream position.
            await firstProtocol.WritePayloadWithGapAsync(firstStream, [.. firstBlock, .. secondBlock],
                firstBlock.Length - 64, 64, ct);
            first.Client.Shutdown(SocketShutdown.Send);
            await TorrentTestUtility.WaitUntilAsync(() => torrent.Peers.ConnectedCount == 0,
                because: "the interrupted peer to disconnect");
            firstListener.Stop();
            Assert.Equal(1, torrent.PiecesReceived);
            Assert.False(torrent.Finished);

            using var replacementListener = new TcpListener(IPAddress.Loopback, 0);
            using var replacement = await ConnectAsync(replacementListener, engine, torrent.Hash, ct);
            using var replacementStream = replacement.GetStream();
            var replacementProtocol = await OpenAsync(replacementStream, torrent.Hash, 0xc0, peerId: (byte)'B', ct);
            using var servingCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var served = new HashSet<int>();
            Task serving = ServeRemainingAsync();
            try
            {
                await TorrentTestUtility.WaitUntilAsync(() => torrent.Finished || serving.IsCompleted,
                    timeoutMs: 15000, because: "the replacement peer to finish the download");
                if (serving.IsCompleted) await serving;
                Assert.True(torrent.Finished);
            }
            finally
            {
                await servingCancellation.CancelAsync();
                await serving;
            }

            Assert.Equal(new[] { 0, BlockSize }, served.Order().ToArray());
            await torrent.StopAsync(ct);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(directory.FullName, FileName), ct));

            async Task ServeRemainingAsync()
            {
                var token = servingCancellation.Token;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var (piece, offset) = await ReadRequestAsync(replacementProtocol, replacementStream, token);
                        Assert.Equal(1, piece);
                        served.Add(offset);
                        await replacementProtocol.WritePayloadAsync(replacementStream, PieceFrame(piece, offset, payload), token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (IOException) when (torrent.Finished) { }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ClientEngine CreateEngine(string path) => ClientEngine.Create(new TorrentClientOptions
    {
        Settings = new Settings
        {
            Files = { DefaultDownloadPath = path },
            Dht = { Enabled = false },
            Session = { Enabled = false },
            Connection =
            {
                BindAddress = IPAddress.Loopback,
                TcpPort = 0,
                UdpPort = 0,
                EnableUtpIn = false,
                EnableUtpOut = false,
                EnableLsd = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                Encryption = Encryption.Require
            }
        }
    });

    private static async Task<TcpClient> ConnectAsync(TcpListener listener, ClientEngine engine, InfoHash hash, CancellationToken ct)
    {
        listener.Start();
        engine.OnPeersFound(hash, [(IPEndPoint)listener.LocalEndpoint]);
        return await listener.AcceptTcpClientAsync(ct);
    }

    private static async Task<MseResponder> OpenAsync(NetworkStream stream, InfoHash hash, byte bitfield, byte peerId, CancellationToken ct)
    {
        byte[] infoHash = hash.ToArray();
        var handshake = new byte[68];
        handshake[0] = 19;
        "BitTorrent protocol"u8.CopyTo(handshake.AsSpan(1));
        infoHash.CopyTo(handshake, 28);
        handshake.AsSpan(48).Fill(peerId);

        var mse = new MseResponder(infoHash);
        await mse.AcceptAsync(stream, ct, trailingPayload: handshake);
        await mse.WritePayloadAsync(stream, [.. Frame(5, [bitfield]), .. Frame(1, [])], ct);
        return mse;
    }

    private static async Task<(int Piece, int Offset)> ReadRequestAsync(MseResponder protocol, NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        while (true)
        {
            await protocol.ReadPayloadAsync(stream, header, header.Length, ct);
            int length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length == 0) continue;
            Assert.InRange(length, 1, 1024);
            var body = new byte[length];
            await protocol.ReadPayloadAsync(stream, body, length, ct);
            if (body[0] != 6) continue;

            Assert.Equal(13, body.Length);
            int offset = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(5));
            Assert.True(offset is 0 or BlockSize);
            Assert.Equal(BlockSize, BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(9)));
            return (BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(1)), offset);
        }
    }

    private static byte[] PieceFrame(int piece, int offset, byte[] payload)
    {
        byte[] body = new byte[8 + BlockSize];
        BinaryPrimitives.WriteInt32BigEndian(body, piece);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4), offset);
        payload.AsSpan(piece * PieceSize + offset, BlockSize).CopyTo(body.AsSpan(8));
        return Frame(7, body);
    }

    private static byte[] Frame(byte id, byte[] payload)
    {
        var frame = new byte[5 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, 1 + payload.Length);
        frame[4] = id;
        payload.CopyTo(frame, 5);
        return frame;
    }
}
