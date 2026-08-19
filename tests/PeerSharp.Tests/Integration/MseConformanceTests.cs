using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// PeerSharp's outgoing MSE handshake, decoded by an independent implementation.
///
/// <para>
/// Every other encryption test in this suite runs PeerSharp's encryptor against PeerSharp's decryptor.
/// Those agree by construction, and would agree just as readily on a wrong key derivation or the wrong
/// keystream discard - the two sides would simply be wrong together. That is not a hypothetical
/// concern: the same shape of blind spot hid a bitfield ordering bug that only real clients could see.
/// </para>
///
/// <para>
/// It matters here more than anywhere else. Measured against a live swarm, 43 of 44 incomplete peers
/// connected over MSE and one in plaintext, and forcing plaintext dropped the run from 127 peers to 12.
/// Encryption is the production path; the rest of the suite barely touches it.
/// </para>
/// </summary>
[Collection("Integration")]
public class MseConformanceTests : IDisposable
{
    private readonly string _path;
    private readonly ILoggerFactory _loggerFactory;

    public MseConformanceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpMse_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    [Fact(Timeout = 90000)]
    public async Task OutgoingHandshake_IsReadableByAnIndependentImplementation()
    {
        var result = await RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.True(result.HandshakeCompleted, "The MSE handshake never completed against an independent responder.");
    }

    [Fact(Timeout = 90000)]
    public async Task OutgoingHandshake_CarriesTheBitTorrentHandshakeAsInitialPayload()
    {
        // MSE attaches the BitTorrent handshake as IA, so the peer can verify the info hash without an
        // extra round trip. Sending it separately still works with lenient peers but wastes one.
        var result = await RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.True(
            result.InitialPayload.Length >= 68,
            $"IA carried {result.InitialPayload.Length} bytes; expected at least the 68 byte BitTorrent handshake.");

        Assert.Equal(19, result.InitialPayload[0]);
        Assert.Equal("BitTorrent protocol", System.Text.Encoding.ASCII.GetString(result.InitialPayload, 1, 19));
        Assert.Equal(result.InfoHash, result.InitialPayload.AsSpan(28, 20).ToArray());
    }

    [Fact(Timeout = 90000)]
    public async Task OutgoingHandshake_OffersRc4()
    {
        var result = await RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.True(
            (result.CryptoProvide & 0x02) != 0,
            $"crypto_provide was 0x{result.CryptoProvide:X8}, which does not offer RC4. Peers that require " +
            "encryption will refuse the connection.");
    }

    [Fact(Timeout = 90000)]
    public async Task EncryptedPayload_DecodesAsAConformantOpeningSequence()
    {
        // The handshake is only half of it: the payload stream that follows has to decrypt to well
        // formed BitTorrent messages, in the order a strict peer expects.
        var result = await RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
        Assert.NotEmpty(result.Messages);

        var beforeAdvertisement = result.Messages
            .TakeWhile(static m => m.Id is not (5 or 14 or 15))
            .ToArray();

        Assert.True(
            result.Messages.Any(static m => m.Id is 5 or 14 or 15),
            $"No piece advertisement arrived over the encrypted stream. Sequence: {Format(result.Messages)}");

        Assert.True(
            beforeAdvertisement.All(static m => m.Id == 20),
            $"A BitTorrent message preceded the advertisement over MSE. Sequence: {Format(result.Messages)}");

        foreach (var (id, length) in result.Messages)
        {
            int? expected = id switch
            {
                0 or 1 or 2 or 3 or 14 or 15 => 1,
                4 or 13 or 17 => 5,
                6 or 8 or 16 => 13,
                9 => 3,
                _ => null
            };

            if (expected is { } exact)
            {
                Assert.True(length == exact, $"Message id {id} was {length} bytes; parsers require {exact}. Sequence: {Format(result.Messages)}");
            }
        }
    }

    private static string Format(IReadOnlyList<(byte Id, int Length)> messages)
    {
        return messages.Count == 0 ? "(nothing)" : string.Join(" -> ", messages.Select(static m => $"{m.Id}[{m.Length}]"));
    }

    private sealed record Result(
        bool HandshakeCompleted,
        byte[] InitialPayload,
        uint CryptoProvide,
        byte[] InfoHash,
        IReadOnlyList<(byte Id, int Length)> Messages,
        string? Failure);

    private async Task<Result> RunAsync(CancellationToken cancellationToken)
    {
        const string fileName = "mse.bin";
        byte[] payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_path, fileName), payload, cancellationToken: TestContext.Current.CancellationToken);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(64 * 1024)
            .AddFile(fileName, payload)
            .Build();

        byte[] infoHash = torrentFile.InfoHash.ToArray();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var messages = new List<(byte Id, int Length)>();
        byte[] initialPayload = [];
        uint cryptoProvide = 0;
        bool completed = false;
        string? failure = null;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var responderTask = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(TimeSpan.FromSeconds(20));

                var mse = new MseResponder(infoHash);
                await mse.AcceptAsync(stream, idle.Token);

                completed = true;
                initialPayload = mse.InitialPayload;
                cryptoProvide = mse.CryptoProvide;

                // Answer with our own BitTorrent handshake over the encrypted stream, so the peer
                // proceeds to send its opening messages.
                byte[] ours = new byte[68];
                ours[0] = 19;
                "BitTorrent protocol"u8.CopyTo(ours.AsSpan(1));
                ours[25] |= 0x10; // ltep
                ours[27] |= 0x04; // fast extension
                infoHash.CopyTo(ours.AsSpan(28));
                for (int i = 0; i < 20; i++)
                {
                    ours[48 + i] = (byte)('Z' - (i % 26));
                }

                await mse.WritePayloadAsync(stream, ours, idle.Token);

                byte[] header = new byte[4];
                while (messages.Count < 3 && !idle.IsCancellationRequested)
                {
                    await mse.ReadPayloadAsync(stream, header, 4, idle.Token);
                    int length = BinaryPrimitives.ReadInt32BigEndian(header);
                    if (length is 0 or > 1 << 20)
                    {
                        continue; // keepalive, or something we will not buffer
                    }

                    byte[] body = new byte[length];
                    await mse.ReadPayloadAsync(stream, body, length, idle.Token);
                    lock (messages)
                    {
                        messages.Add((body[0], length));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Idle timeout: whatever was decoded stands.
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                done.TrySetResult(true);
            }
        });

        var settings = new Settings
        {
            Files = { DefaultDownloadPath = _path },
            Connection =
            {
                TcpPort = 0,
                UdpPort = 0,
                EnableLsd = false,
                EnableUtpIn = false,
                EnableUtpOut = false,
                PreferUtp = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,

                // Require, so the connection can only proceed over MSE.
                Encryption = Encryption.Require
            },
            Dht = { Enabled = false }
        };

        await using var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = settings
        });

        await engine.InitializeAsync();

        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await torrent.ForceRecheckAsync());
        await torrent.StartAsync();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            engine.OnPeersFound(torrent.Hash, [new IPEndPoint(IPAddress.Loopback, port)]);
            await Task.Delay(250);
        }

        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(25)));
        listener.Stop();
        try { await responderTask; } catch { /* Listener stopped underneath it. */ }

        lock (messages)
        {
            return new Result(completed, initialPayload, cryptoProvide, infoHash, [.. messages], failure);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _loggerFactory.Dispose();
        try
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }
    }
}
