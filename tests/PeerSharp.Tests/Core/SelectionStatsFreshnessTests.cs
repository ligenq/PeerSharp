using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Core;

/// <summary>
/// Keeping the selected-piece counters honest when the piece map changes without telling them.
///
/// <para>
/// The counters behind <see cref="ITorrent.SelectionFinished"/> and the selection progress are
/// maintained incrementally, one notification per verified piece. That is only correct while every
/// change to the piece map arrives that way, and a recheck does not: it fills the map directly. A
/// torrent holding every piece therefore reported its selection unfinished, indefinitely.
/// </para>
///
/// <para>
/// It was found through BEP 21, which is derived from the same answer: a complete seed never
/// advertised <c>upload_only</c>, so peers were never told it had stopped downloading. The visible
/// symptom was somewhere else entirely, which is the usual shape of a cached value that nothing
/// checks.
/// </para>
/// </summary>
public class SelectionStatsFreshnessTests : IDisposable
{
    private const int PieceLength = 16 * 1024;
    private const int PieceCount = 6;

    private readonly ILoggerFactory _loggerFactory;
    private readonly string _path;

    public SelectionStatsFreshnessTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpSelStats_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
    }

    [Fact(Timeout = 60000)]
    public async Task ARecheckedCompleteTorrentHasFinishedItsSelection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (engine, torrent) = await CreateCompleteTorrentAsync(cancellationToken);

        await using (engine)
        {
            Assert.Equal(PieceCount, await torrent.ForceRecheckAsync(cancellationToken: cancellationToken));

            Assert.True(torrent.Finished, "The recheck found every piece, so the torrent is complete.");
            Assert.True(
                torrent.SelectionFinished,
                "Every piece is present but the selection reports itself unfinished. The counters behind " +
                "it are updated one verified piece at a time and a recheck fills the piece map directly, " +
                "so they never caught up - and BEP 21 upload_only is read from this, meaning a complete " +
                "seed never tells anyone it has stopped downloading.");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task ARecheckedCompleteTorrentReportsFullProgress()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (engine, torrent) = await CreateCompleteTorrentAsync(cancellationToken);

        await using (engine)
        {
            await torrent.ForceRecheckAsync(cancellationToken: cancellationToken);

            Assert.Equal(1.0f, torrent.Progress, 3);
        }
    }

    /// <summary>
    /// A repeated notification for a piece already counted must not push the count past the total.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CountingTheSamePieceTwiceDoesNotOvershoot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (engine, torrent) = await CreateCompleteTorrentAsync(cancellationToken);

        await using (engine)
        {
            await torrent.ForceRecheckAsync(cancellationToken: cancellationToken);

            var internals = (Torrent)torrent;
            for (int i = 0; i < PieceCount; i++)
            {
                internals.OnPieceVerified(i);
                internals.OnPieceVerified(i);
            }

            // Progress is clamped and the finished check is a >=, so both survive an overshoot.
            // The byte count is neither: it multiplies the piece count out, and feeds what the
            // tracker is told is left to download.
            ulong size = (ulong)PieceLength * PieceCount;

            Assert.True(
                torrent.FinishedSelectedBytes <= size,
                $"Counted {torrent.FinishedSelectedBytes} finished bytes in a {size} byte torrent. The " +
                "same piece was reported verified twice and was counted twice, which inflates what we " +
                "report having and understates what the tracker is told is left.");
        }
    }

    private async Task<(ClientEngine Engine, ITorrent Torrent)> CreateCompleteTorrentAsync(
        CancellationToken cancellationToken)
    {
        const string fileName = "selection.bin";
        byte[] payload = new byte[PieceLength * PieceCount];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_path, fileName), payload, cancellationToken);

        var settings = new Settings { Files = { DefaultDownloadPath = _path } };
        settings.Connection.TcpPort = 0;
        settings.Connection.UdpPort = 0;
        settings.Connection.EnableLsd = false;
        settings.Connection.UpnpPortMapping = false;
        settings.Connection.NatPmpPortMapping = false;
        settings.Dht.Enabled = false;

        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = settings
        });

        await engine.InitializeAsync(cancellationToken);

        var torrentFile = new ApiTorrentFileBuilder()
            .WithName(fileName)
            .WithPieceLength(PieceLength)
            .AddFile(fileName, payload)
            .Build();

        var torrent = await engine.AddTorrentAsync(
            torrentFile, new AddTorrentOptions { StartImmediately = false });

        return (engine, torrent);
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
