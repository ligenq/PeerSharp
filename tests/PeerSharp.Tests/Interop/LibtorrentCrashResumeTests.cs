using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals;
using PeerSharp.Tests.Integration.Synthetic;

namespace PeerSharp.Tests.Interop;

[Collection("Integration")]
public sealed class LibtorrentCrashResumeTests
{
    private const int PieceSize = ProtocolConstants.BlockSize;
    private const int PieceCount = 4;
    private const string TorrentName = "payload";
    private const string WorkerDirectory = "PEERSHARP_CRASH_RESUME_DIRECTORY";
    private const string WorkerSource = "PEERSHARP_CRASH_RESUME_SOURCE";
    private const string WorkerPhase = "PEERSHARP_CRASH_RESUME_PHASE";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Theory(Timeout = 120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARestartServesExactlyThePublishedPieces(bool killAfterPublication)
    {
        if (Environment.GetEnvironmentVariable("PEERSHARP_INTEROP") != "1")
        {
            Assert.Skip("Set PEERSHARP_INTEROP=1.");
        }
        string? executable = LibtorrentOracleTests.FindClientTest();
        if (executable == null) Assert.Skip("Build libtorrent client_test first.");

        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("PeerSharpLibtorrentCrash-");
        try
        {
            await RunInDirectoryAsync(root.FullName, executable, killAfterPublication, ct);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task RunInDirectoryAsync(string root, string executable, bool killAfterPublication, CancellationToken ct)
    {
        var payload = new byte[PieceSize * PieceCount];
        new Random(314159).NextBytes(payload);
        // The baseline piece occupies a separate file. The new piece crosses b.bin and c.bin
        // without changing the timestamp of the file verified by the baseline.
        byte[][] files = [payload[..PieceSize], payload[PieceSize..(PieceSize + 997)], payload[(PieceSize + 997)..]];
        string[] names = ["a.bin", "b.bin", "c.bin"];
        var file = new PeerSharp.Core.TorrentFileBuilder().WithName(TorrentName).WithPieceLength(PieceSize)
            .AddFile(names[0], files[0]).AddFile(names[1], files[1]).AddFile(names[2], files[2]).Build();
        Assert.Equal(PieceCount, file.PieceCount);
        string torrentPath = Path.Combine(root, "test.torrent");
        await File.WriteAllBytesAsync(torrentPath, file.RawData.ToArray(), ct);

        await CrashDownloaderAsync(root, payload, killAfterPublication, ct);
        int publishedPieces = killAfterPublication ? 2 : 1;
        await using var engine = CreateEngine(root);
        await engine.InitializeAsync(ct);
        var torrent = Assert.Single(engine.GetTorrents());
        Assert.Equal(file.InfoHash, torrent.Hash);
        Assert.True(torrent.Started);
        Assert.Equal(publishedPieces, torrent.PiecesReceived);
        Assert.False(torrent.Finished);
        await AssertAdvertisedPiecesAsync(engine, torrent.Hash, publishedPieces, ct);

        // PeerSharp is the only source while libtorrent downloads the restored pieces.
        await AssertLibtorrentDownloadAsync(executable, torrentPath, Path.Combine(root, "libtorrent-partial"),
            engine.BoundTcpPort, names, files, publishedPieces * PieceSize, ct);
        Assert.Equal(publishedPieces, torrent.PiecesReceived);

        await FinishDownloadAsync(engine, torrent, payload, publishedPieces, ct);
        // The first observer can disconnect when neither peer has anything the other needs.
        await AssertLibtorrentDownloadAsync(executable, torrentPath, Path.Combine(root, "libtorrent-complete"),
            engine.BoundTcpPort, names, files, payload.Length, ct);
        await torrent.StopAsync(ct);
        for (int index = 0; index < names.Length; index++)
        {
            Assert.Equal(files[index], await File.ReadAllBytesAsync(
                Path.Combine(DownloadPath(root), torrent.GetFileInfo(index).Path), ct));
        }
    }

    private static async Task CrashDownloaderAsync(string root, byte[] payload, bool killAfterPublication, CancellationToken ct)
    {
        await using var source = StartScriptedPeer();
        await using var child = StartDownloader(root, source.Port, killAfterPublication);
        try
        {
            var connection = await source.WaitForConnectionAsync(0, Deadline, ct);
            await connection.Ready.WaitAsync(Deadline, ct);
            await connection.SendFrameAsync(5, new byte[] { 0x80 }, ct);
            await connection.SendFrameAsync(1, ReadOnlyMemory<byte>.Empty, ct);
            await WaitForRequestAndSendAsync(connection, 0, payload, ct);
            await WaitForMarkerAsync(child, Path.Combine(root, "baseline"), ct);

            var announcedPiece = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(announcedPiece, 1);
            await connection.SendFrameAsync(4, announcedPiece, ct);
            await WaitForRequestAndSendAsync(connection, 1, payload, ct);
            await WaitForMarkerAsync(child, Path.Combine(root, "publication"), ct);
            Assert.False(child.HasExited);
            await child.StopAsync();
            Assert.NotEqual(0, child.ExitCode);
        }
        catch
        {
            await child.StopAsync();
            await child.ReportOutputAsync();
            throw;
        }
    }

    private static async Task AssertAdvertisedPiecesAsync(ClientEngine engine, InfoHash hash, int publishedPieces, CancellationToken ct)
    {
        await using var witness = StartScriptedPeer();
        engine.OnPeersFound(hash, [witness.EndPoint]);
        var connection = await witness.WaitForConnectionAsync(0, Deadline, ct);
        await connection.Ready.WaitAsync(Deadline, ct);
        Assert.True(await connection.WaitForFrameAsync(frame => frame.Id == 5, Deadline, ct), "The restart sent no initial bitfield.");
        byte expected = publishedPieces == 1 ? (byte)0x80 : (byte)0xc0;
        Assert.Equal(new byte[] { expected }, Assert.Single(connection.Frames, frame => frame.Id == 5).Payload);
    }

    private static async Task FinishDownloadAsync(ClientEngine engine, ITorrent torrent, byte[] payload, int publishedPieces,
        CancellationToken ct)
    {
        await using var source = StartScriptedPeer();
        engine.OnPeersFound(torrent.Hash, [source.EndPoint]);
        var connection = await source.WaitForConnectionAsync(0, Deadline, ct);
        await connection.Ready.WaitAsync(Deadline, ct);
        await connection.SendFrameAsync(5, new byte[] { 0xf0 }, ct);
        await connection.SendFrameAsync(1, ReadOnlyMemory<byte>.Empty, ct);

        var sent = new HashSet<int>();
        var deadline = Stopwatch.StartNew();
        while (!torrent.Finished && deadline.Elapsed < Deadline)
        {
            foreach (var frame in connection.Frames.Where(frame => frame.Id == 6))
            {
                int piece = ReadRequest(frame);
                Assert.InRange(piece, publishedPieces, PieceCount - 1);
                if (sent.Add(piece))
                {
                    await SendPieceAsync(connection, piece, payload, ct);
                }
            }
            if (!torrent.Finished) await Task.Delay(20, ct);
        }
        Assert.True(torrent.Finished, $"The restarted torrent stalled after requesting pieces [{string.Join(", ", sent)}].");
        Assert.Equal(PieceCount, torrent.PiecesReceived);
    }

    private static async Task WaitForRequestAndSendAsync(SyntheticConnection connection, int piece, byte[] payload, CancellationToken ct)
    {
        Assert.True(await connection.WaitForFrameAsync(frame => frame.Id == 6 && ReadRequest(frame) == piece, Deadline, ct),
            $"The crash downloader never requested piece {piece}.");
        await SendPieceAsync(connection, piece, payload, ct);
    }

    private static int ReadRequest(WireFrame frame)
    {
        Assert.Equal(12, frame.Payload.Length);
        int piece = BinaryPrimitives.ReadInt32BigEndian(frame.Payload);
        Assert.InRange(piece, 0, PieceCount - 1);
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4)));
        Assert.Equal(PieceSize, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(8)));
        return piece;
    }

    private static Task SendPieceAsync(SyntheticConnection connection, int piece, byte[] payload, CancellationToken ct)
    {
        var message = new byte[8 + PieceSize];
        BinaryPrimitives.WriteInt32BigEndian(message, piece);
        payload.AsSpan(piece * PieceSize, PieceSize).CopyTo(message.AsSpan(8));
        return connection.SendFrameAsync(7, message, ct);
    }

    private static async Task AssertLibtorrentDownloadAsync(string executable, string torrentPath, string path, int peerPort,
        string[] names, byte[][] expected, int length, CancellationToken ct)
    {
        Directory.CreateDirectory(path);
        await using var observer = StartLibtorrent(executable, torrentPath, path, peerPort);
        try
        {
            string destination = Path.Combine(path, TorrentName);
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < Deadline && !observer.HasExited)
            {
                if (await PayloadMatchesAsync(destination, names, expected, length, ct)) return;
                await Task.Delay(40, ct);
            }
            Assert.Fail($"libtorrent did not read the expected first {length} bytes. Exited={observer.HasExited}.");
        }
        catch
        {
            await observer.StopAsync();
            await observer.ReportOutputAsync();
            throw;
        }
    }

    private static async Task<bool> PayloadMatchesAsync(string root, string[] names, byte[][] expected, int length, CancellationToken ct)
    {
        for (int index = 0; index < names.Length && length > 0; index++)
        {
            int count = Math.Min(length, expected[index].Length);
            try
            {
                await using var stream = new FileStream(Path.Combine(root, names[index]), FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
                if (stream.Length < count) return false;
                var bytes = new byte[count];
                await stream.ReadExactlyAsync(bytes, ct);
                if (!expected[index].AsSpan(0, count).SequenceEqual(bytes)) return false;
            }
            catch (IOException)
            {
                return false;
            }
            length -= count;
        }
        return length == 0;
    }

    [Fact(Explicit = true, Timeout = 90000)]
    public async Task CrashDownloaderWorker()
    {
        string? root = Environment.GetEnvironmentVariable(WorkerDirectory);
        if (root == null) return;

        var ct = TestContext.Current.CancellationToken;
        int sourcePort = int.Parse(Environment.GetEnvironmentVariable(WorkerSource)!, CultureInfo.InvariantCulture);
        bool afterPublication = Environment.GetEnvironmentVariable(WorkerPhase) == "after";
        bool armed = false;
        var persistence = new SessionPersistence(SessionPath(root), NullLogger<SessionPersistence>.Instance)
        {
            OnSnapshotPublication = async (published, token) =>
            {
                if (!armed || published != afterPublication) return;
                using (File.Create(Path.Combine(root, "publication"))) { }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        await using var engine = CreateEngine(root, persistence);
        await engine.InitializeAsync(ct);
        var file = TorrentFile.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "test.torrent"), ct));
        var torrent = await engine.AddTorrentAsync(file, cancellationToken: ct);
        engine.OnPeersFound(file.InfoHash, [new IPEndPoint(IPAddress.Loopback, sourcePort)]);
        await TorrentTestUtility.WaitUntilAsync(() => torrent.PiecesReceived == 1, (int)Deadline.TotalMilliseconds, "the baseline piece");
        await engine.SaveSessionAsync(ct);
        using (File.Create(Path.Combine(root, "baseline"))) { }

        await TorrentTestUtility.WaitUntilAsync(() => torrent.PiecesReceived == 2, (int)Deadline.TotalMilliseconds, "the cross-file piece");
        Assert.False(torrent.Finished);
        armed = true;
        await engine.SaveSessionAsync(ct);
        Assert.Fail("The writer should be killed at the armed publication barrier.");
    }

    private static SyntheticPeer StartScriptedPeer() => SyntheticPeer.Start(new SyntheticPeerOptions
    {
        AdvertiseExtensionProtocol = false,
        AdvertiseFastExtension = false
    });

    private static string DownloadPath(string root) => Path.Combine(root, "peersharp");
    private static string SessionPath(string root) => Path.Combine(root, "session");

    private static ClientEngine CreateEngine(string root, ISessionPersistence? persistence = null)
        => ClientEngine.Create(new TorrentClientOptions
        {
            SessionPersistence = persistence,
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = Directory.CreateDirectory(DownloadPath(root)).FullName },
                Dht = { Enabled = false },
                Session = { Enabled = true, SessionPath = SessionPath(root), AutoSaveIntervalSeconds = 0 },
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
                    Encryption = Encryption.Refuse
                }
            }
        });

    private static ChildProcess StartDownloader(string root, int sourcePort, bool afterPublication)
    {
        var start = ProcessOptions(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", root);
        foreach (string argument in new[]
        {
            typeof(LibtorrentCrashResumeTests).Assembly.Location, "-noLogo", "-method",
            typeof(LibtorrentCrashResumeTests).FullName + "." + nameof(CrashDownloaderWorker), "-explicit", "on"
        })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[WorkerDirectory] = root;
        start.Environment[WorkerSource] = sourcePort.ToString(CultureInfo.InvariantCulture);
        start.Environment[WorkerPhase] = afterPublication ? "after" : "before";
        return new ChildProcess(start);
    }

    private static ChildProcess StartLibtorrent(string executable, string torrentPath, string path, int peerPort)
    {
        Assert.InRange(peerPort, 1, ushort.MaxValue);
        var start = ProcessOptions(executable, path);
        start.RedirectStandardInput = true;
        foreach (string argument in new[]
        {
            torrentPath, "-k", "-s", path, "-r", $"127.0.0.1:{peerPort}", "-F", "100",
            "--listen_interfaces=127.0.0.1:0", "--enable_dht=0", "--enable_lsd=0",
            "--enable_upnp=0", "--enable_natpmp=0", "--enable_outgoing_utp=0", "--out_enc_policy=2"
        })
        {
            start.ArgumentList.Add(argument);
        }
        return new ChildProcess(start);
    }

    private static ProcessStartInfo ProcessOptions(string fileName, string root) => new(fileName)
    {
        WorkingDirectory = root,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
    };

    private static async Task WaitForMarkerAsync(ChildProcess child, string marker, CancellationToken ct)
    {
        await TorrentTestUtility.WaitUntilAsync(() => File.Exists(marker) || child.HasExited,
            (int)Deadline.TotalMilliseconds, Path.GetFileName(marker));
        ct.ThrowIfCancellationRequested();
        Assert.False(child.HasExited, "The downloader exited early.");
        Assert.True(File.Exists(marker));
    }

    private sealed class ChildProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _output;
        private readonly Task<string> _error;

        public ChildProcess(ProcessStartInfo start)
        {
            _process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
            _output = _process.StandardOutput.ReadToEndAsync();
            _error = _process.StandardError.ReadToEndAsync();
        }

        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;

        public async Task StopAsync()
        {
            try
            {
                if (!HasExited) _process.Kill();
            }
            catch (InvalidOperationException) when (HasExited) { }
            await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(Deadline);
        }

        public async Task ReportOutputAsync()
        {
            string output = await _output + await _error;
            TestContext.Current.TestOutputHelper!.WriteLine(output[Math.Max(0, output.Length - 4000)..]);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
                await Task.WhenAll(_output, _error);
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
