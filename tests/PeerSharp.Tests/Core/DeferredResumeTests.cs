using System.Reflection;
using System.Text.Json;
using PeerSharp.Internals;
using PeerSharp.Internals.Transfers;
using PeerSharp.PieceWriter;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core;

public sealed class DeferredResumeTests
{
    [Fact]
    public async Task StartIntentBypassesSpoolingAndAPauseCanStillDeferTheSameProgress()
    {
        var state = new TorrentStateData
        {
            Started = false, // Current add intent, not this historical flag, decides the restore path.
            UnfinishedPieces = [new() { Index = 0, Blocks = [true, false], Data = new byte[32768] }]
        };
        await using var torrent = TorrentTestUtility.CreateMinimal(Metadata(), resumeData: Resume(state), loadResumePiecesForStart: true);
        Assert.Null(Store(torrent.FileTransferInternal));
        Assert.Equal(1, ActivePieces(torrent.FileTransferInternal).Count);
        Assert.Equal(16384, torrent.GetFileInfo(0).DownloadedBytes);

        await torrent.DeferRestoredPiecesAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(Store(torrent.FileTransferInternal));
        Assert.Equal(0, ActivePieces(torrent.FileTransferInternal).Count);
        Assert.Empty(torrent.LocalState.UnfinishedPieces);
        Assert.Equal(16384, torrent.GetFileInfo(0).DownloadedBytes);
        torrent.FileTransferInternal.RestoreDeferredPieces();
        Assert.Equal(1, ActivePieces(torrent.FileTransferInternal).Count);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task AddOptionsAndSessionPauseOverrideTheSavedStartedFlag(bool savedStarted, bool startImmediately, bool paused)
    {
        string root = NewRoot();
        try
        {
            await using var engine = ClientEngine.Create(new TorrentClientOptions
            {
                Settings = new Settings
                {
                    Files = { DefaultDownloadPath = root },
                    Session = { Enabled = false },
                    Dht = { Enabled = false },
                    Connection = { EnableTcpIn = false, EnableTcpOut = false, EnableUtpIn = false, EnableUtpOut = false,
                    EnableLsd = false, UpnpPortMapping = false, NatPmpPortMapping = false }
                }
            });
            await engine.InitializeAsync(TestContext.Current.CancellationToken);
            if (paused) await engine.PauseAsync(TestContext.Current.CancellationToken);
            var file = new PeerSharp.Core.TorrentFileBuilder().WithName("data.bin").WithPieceLength(32768)
                .AddFile("data.bin", new byte[32768]).Build();
            var state = new TorrentStateData
            {
                Started = savedStarted,
                UnfinishedPieces = [new() { Index = 0, Blocks = [true, false], Data = new byte[32768] }]
            };
            var torrent = (Torrent)await engine.AddTorrentAsync(file,
                new AddTorrentOptions { StartImmediately = startImmediately, ResumeData = Resume(state) }, TestContext.Current.CancellationToken);
            bool running = startImmediately && !paused;
            Assert.Equal(running, torrent.Started);
            Assert.Equal(running ? 1 : 0, ActivePieces(torrent.FileTransferInternal).Count);
            Assert.Equal(!running, Store(torrent.FileTransferInternal) != null);
            Assert.Empty(torrent.LocalState.UnfinishedPieces);
            Assert.Equal(16384, torrent.GetFileInfo(0).DownloadedBytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MagnetKeepsPartialResumeDataUntilItsMetadataCanValidateIt(bool loadForStart)
    {
        var resolved = Metadata();
        var magnet = new TorrentFileMetadata();
        magnet.Info.Hash = resolved.Info.Hash;
        var state = new TorrentStateData
        {
            UnfinishedPieces = [new() { Index = 0, Blocks = [true, false], Data = new byte[32768] }]
        };
        await using var torrent = TorrentTestUtility.CreateMinimal(magnet, resumeData: Resume(state), loadResumePiecesForStart: loadForStart);
        Assert.False(torrent.HasMetadata);
        Assert.Single(torrent.LocalState.UnfinishedPieces);

        torrent.InfoFile = resolved;
        await torrent.ReinitializeAfterMetadataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(16384, torrent.GetFileInfo(0).DownloadedBytes);
        Assert.Single(torrent.FileTransferInternal.GetUnfinishedPiecesState());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecheckingDeferredOrActiveProgressDoesNotCountVerifiedBytesTwice(bool deferred)
    {
        string root = NewRoot();
        try
        {
            byte[] data = new byte[32768];
            new Random(1741).NextBytes(data);
            var metadata = Metadata();
            metadata.Info.Pieces[0] = SHA1.HashData(data);
            await File.WriteAllBytesAsync(Path.Combine(root, "data.bin"), data, TestContext.Current.CancellationToken);
            await using var torrent = TorrentTestUtility.CreateMinimal(metadata, root);
            List<TorrentStateData.UnfinishedPieceData> partial = [new() { Index = 0, Blocks = [true, false], Data = data }];
            if (deferred) torrent.FileTransferInternal.DeferUnfinishedPiecesState(partial);
            else torrent.FileTransferInternal.LoadUnfinishedPiecesState(partial);
            Assert.Equal(16384, torrent.GetFileInfo(0).DownloadedBytes);
            Assert.Equal(1, await torrent.ForceRecheckAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(32768, torrent.GetFileInfo(0).DownloadedBytes);
            Assert.Equal(0, torrent.FileTransferInternal.GetUnfinishedBytes());
            Assert.Equal(0, torrent.FileTransferInternal.GetUnfinishedSelectedBytes([new FileSelection { Selected = true }]));
            Assert.Empty(torrent.FileTransferInternal.GetUnfinishedPiecesState());
            var streamed = torrent.FileTransferInternal.CaptureUnfinishedPieces(streamed: true);
            using var lease = streamed.Lease;
            Assert.Empty(streamed.Pieces);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ActiveProgressClipsTheLastPartialBlock()
    {
        var metadata = Metadata();
        metadata.Info.FullSize = 17000;
        metadata.Info.Files[0].Size = 17000;
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
        torrent.FileTransferInternal.LoadUnfinishedPiecesState([new() { Index = 0, Blocks = [false, true], Data = new byte[17000] }]);
        Assert.Equal(616, torrent.FileTransferInternal.GetUnfinishedBytes());
        Assert.Equal(616, torrent.GetFileInfo(0).DownloadedBytes);
        Assert.Equal(616, torrent.FileTransferInternal.GetUnfinishedSelectedBytes([new FileSelection { Selected = true }]));
    }

    [Fact]
    public async Task InvalidResumePiecesIncludeTheirGeometryInWarnings()
    {
        var messages = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new RecordingProvider(messages)));
        await using var torrent = TorrentTestUtility.CreateMinimal(Metadata(), loggerFactory: loggerFactory);
        torrent.FileTransferInternal.DeferUnfinishedPiecesState([
            new() { Index = -1, Blocks = [true], Data = [1] },
            new() { Index = 10, Blocks = [true], Data = [1] },
            new() { Index = 0, Blocks = [true], Data = new byte[123] }
        ]);
        Assert.Contains(messages, m => m.Contains("-1", StringComparison.Ordinal) && m.Contains("outside this torrent's 1 pieces", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("10", StringComparison.Ordinal) && m.Contains("outside this torrent's 1 pieces", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("1 block flags and 123 bytes", StringComparison.Ordinal)
            && m.Contains("32768-byte piece", StringComparison.Ordinal) && m.Contains("expected 2 flags", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedReplacementDropsTheOldStoreButPreservesItsLeasedSnapshot()
    {
        string root = NewRoot();
        string blocked = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "blocked", TestContext.Current.CancellationToken);
        try
        {
            await using var torrent = TorrentTestUtility.CreateMinimal(Metadata());
            torrent.FileTransferInternal.DeferUnfinishedPiecesState([new() { Index = 0, Blocks = [true, false], Data = new byte[32768] }]);
            var snapshot = torrent.FileTransferInternal.CaptureUnfinishedPieces(streamed: true);
            using var lease = snapshot.Lease;
            // Scoped to this transfer, so a test creating a temporary file in a collection running
            // alongside this one cannot be redirected into the unusable path.
            torrent.FileTransferInternal.ResumeSpoolDirectory = blocked;
            torrent.FileTransferInternal.DeferUnfinishedPiecesState([new() { Index = 0, Blocks = [false, true], Data = new byte[32768] }]);
            torrent.FileTransferInternal.ResumeSpoolDirectory = null;
            Assert.Null(Store(torrent.FileTransferInternal));
            Assert.Equal(16384, torrent.FileTransferInternal.GetUnfinishedBytes());
            Assert.Equal(new[] { false, true }, Assert.Single(torrent.FileTransferInternal.GetUnfinishedPiecesState()).Blocks);
            Assert.Equal(new[] { true, false }, Assert.Single(snapshot.Pieces).Blocks);
            Assert.Equal(32768, snapshot.Pieces[0].DeferredData!.Value.Read().Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RecordingProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(messages);
        public void Dispose() { }
    }

    private sealed class RecordingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (messages) messages.Add(formatter(state, exception));
        }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSharpResumeReview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static TorrentResumeData Resume(TorrentStateData state) => new()
    {
        Data = JsonSerializer.SerializeToUtf8Bytes(state, PeerSharpJsonContext.Default.TorrentStateData)
    };

    private static DeferredPieceStore? Store(FileTransfer transfer) => (DeferredPieceStore?)typeof(FileTransfer)
        .GetField("_deferredPieces", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transfer);

    [Fact]
    public async Task StoppedTorrentKeepsProgressAndExportDataWithoutMaterializingBlocks()
    {
        byte[] data = new byte[32768];
        data.AsSpan(0, 16384).Fill(42);
        var state = new TorrentStateData
        {
            UnfinishedPieces = [new() { Index = 0, Blocks = [true, false], Data = data }]
        };
        var resume = new TorrentResumeData { Data = JsonSerializer.SerializeToUtf8Bytes(state, PeerSharpJsonContext.Default.TorrentStateData) };
        await using var torrent = TorrentTestUtility.CreateMinimal(Metadata(), resumeData: resume);

        Assert.False(torrent.Started);
        Assert.Equal(0, ActivePieces(torrent.FileTransferInternal).Count);
        Assert.Empty(torrent.LocalState.UnfinishedPieces);
        Assert.Equal(16384, torrent.FileTransferInternal.GetUnfinishedBytes());
        Assert.Equal(16384, torrent.GetFileInfo(0).DownloadedBytes);

        var exported = JsonSerializer.Deserialize(torrent.GetResumeData().Data, PeerSharpJsonContext.Default.TorrentStateData)!;
        Assert.Equal(data, Assert.Single(exported.UnfinishedPieces).Data);
        Assert.Equal(0, ActivePieces(torrent.FileTransferInternal).Count);

        torrent.FileTransferInternal.RestoreDeferredPieces();
        Assert.Equal(1, ActivePieces(torrent.FileTransferInternal).Count);
        Assert.Equal(data, Assert.Single(torrent.FileTransferInternal.GetUnfinishedPiecesState()).Data);
        torrent.FileTransferInternal.RestoreDeferredPieces();
        Assert.Equal(1, ActivePieces(torrent.FileTransferInternal).Count);
    }

    [Fact]
    public async Task StartingRestoresDeferredBlocksBeforeTransfersBegin()
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSharpDeferred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] data = new byte[32768];
            data.AsSpan(0, 16384).Fill(77);
            var state = new TorrentStateData { UnfinishedPieces = [new() { Index = 0, Blocks = [true, false], Data = data }] };
            var resume = new TorrentResumeData { Data = JsonSerializer.SerializeToUtf8Bytes(state, PeerSharpJsonContext.Default.TorrentStateData) };
            await using var torrent = TorrentTestUtility.CreateMinimal(Metadata(), root, resumeData: resume);
            torrent.Settings.Connection.EnableWebSeeds = false;
            torrent.Settings.Connection.EnableTcpIn = false;
            torrent.Settings.Connection.EnableTcpOut = false;
            torrent.Settings.Connection.EnableUtpIn = false;
            torrent.Settings.Connection.EnableUtpOut = false;

            await torrent.StartCoreAsync(TestContext.Current.CancellationToken);

            Assert.True(torrent.Started);
            Assert.Equal(1, ActivePieces(torrent.FileTransferInternal).Count);
            Assert.Equal(data, Assert.Single(torrent.FileTransferInternal.GetUnfinishedPiecesState()).Data);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RecheckCompletedPiecesAreNotRestoredAsUnfinished()
    {
        await using var torrent = TorrentTestUtility.CreateMinimal(Metadata());
        torrent.FileTransferInternal.DeferUnfinishedPiecesState([new() { Index = 0, Blocks = [true, false], Data = new byte[32768] }]);
        torrent.Pieces.AddPiece(0);
        torrent.FileTransferInternal.RestoreDeferredPieces();
        Assert.Empty(torrent.FileTransferInternal.GetUnfinishedPiecesState());
        Assert.Equal(0, torrent.FileTransferInternal.GetUnfinishedBytes());
    }

    private static TorrentFileMetadata Metadata()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Hash = InfoHash.CreateRandom();
        metadata.Info.PieceSize = 32768;
        metadata.Info.FullSize = 32768;
        metadata.Info.Pieces.Add(new byte[20]);
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "data.bin", Size = 32768 });
        return metadata;
    }

    private static PieceStateManager ActivePieces(FileTransfer transfer) => (PieceStateManager)typeof(FileTransfer)
        .GetField("_pieceStateManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transfer)!;
}
