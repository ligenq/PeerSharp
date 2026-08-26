using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>
/// Closing connections that neither side can use, end to end.
///
/// <para>
/// <see cref="Core.Peers.RedundantConnectionPolicyTests"/> pins the decision; this pins that the
/// engine acts on it. The two are worth separating because the defect was never in the judgement -
/// there was no judgement - it was that nothing ever asked the question, and a peer with nothing left
/// to exchange sat connected until the two-minute idle timeout reaped it.
/// </para>
/// </summary>
[Collection("Integration")]
public class SyntheticPeerRedundancyTests : IDisposable
{
    private const int PieceLength = 16 * 1024;
    private const int PieceCount = 4;

    private readonly ILoggerFactory _loggerFactory;
    private readonly string _path;

    public SyntheticPeerRedundancyTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpRedundant_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
    }

    /// <summary>
    /// A seed connected to a seed is dropped rather than held to the idle timeout.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AConnectionBetweenTwoSeedsIsClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions());
        await using var engine = CreateEngine();
        var torrent = await AddSeedingTorrentAsync(engine, cancellationToken);

        var connection = await DialAsync(engine, torrent, peer, cancellationToken);
        await connection.Ready;

        // BEP 6 have-all: this peer holds the whole torrent, and so does the engine.
        await connection.SendFrameAsync(14, ReadOnlyMemory<byte>.Empty, cancellationToken);

        bool closed = await connection.WaitForCloseAsync(TimeSpan.FromSeconds(45), cancellationToken);

        Assert.True(
            closed,
            "Two seeds stayed connected. Neither will request anything of the other, so the connection " +
            "occupies a slot on both sides until the idle timeout notices two minutes from now - and a " +
            $"seed in a busy swarm fills up with them. Traffic: {connection.Describe()}");
    }

    /// <summary>
    /// The case a careless version of this rule breaks: a leecher must be kept, which is the entire
    /// point of seeding.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task APeerThatStillWantsPiecesIsKept()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions());
        await using var engine = CreateEngine();
        var torrent = await AddSeedingTorrentAsync(engine, cancellationToken);

        var connection = await DialAsync(engine, torrent, peer, cancellationToken);
        await connection.Ready;

        // BEP 6 have-none, then interested: a peer with nothing that wants everything.
        await connection.SendFrameAsync(15, ReadOnlyMemory<byte>.Empty, cancellationToken);
        await connection.SendFrameAsync(2, ReadOnlyMemory<byte>.Empty, cancellationToken);

        // Long enough for two of the engine's five-second health checks to have run.
        bool closed = await connection.WaitForCloseAsync(TimeSpan.FromSeconds(15), cancellationToken);

        Assert.False(
            closed,
            "A peer that has nothing and wants everything was disconnected by the redundancy rule. " +
            $"Serving exactly this peer is what seeding is. Traffic: {connection.Describe()}");
    }

    private ClientEngine CreateEngine()
    {
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
                Encryption = Encryption.Refuse
            },
            Dht = { Enabled = false }
        };

        return ClientEngine.Create(new TorrentClientOptions { LoggerFactory = _loggerFactory, Settings = settings });
    }

    private async Task<ITorrent> AddSeedingTorrentAsync(ClientEngine engine, CancellationToken cancellationToken)
    {
        await engine.InitializeAsync(cancellationToken);

        const string fileName = "redundant.bin";
        byte[] payload = new byte[PieceLength * PieceCount];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_path, fileName), payload, cancellationToken);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(PieceLength)
            .AddFile(fileName, payload)
            .Build();

        var torrent = await engine.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
        Assert.Equal(torrentFile.PieceCount, await torrent.ForceRecheckAsync());
        await torrent.StartAsync();
        return torrent;
    }

    private static async Task<SyntheticConnection> DialAsync(
        ClientEngine engine, ITorrent torrent, SyntheticPeer peer, CancellationToken cancellationToken)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (peer.ConnectionCount == 0 && deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            engine.OnPeersFound(torrent.Hash, [peer.EndPoint]);
            await Task.Delay(250, cancellationToken);
        }

        return await peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(10), cancellationToken);
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
