using Microsoft.Extensions.Logging;
using PeerSharp.Exceptions;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;
using System.Security.Cryptography;
using System.Text.Json;
using ApiTorrentFileBuilder = PeerSharp.Core.TorrentFileBuilder;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// Moving a torrent's data, and storing one of its files under a different name.
///
/// <para>
/// Both are about bytes on disk, so they are checked against the disk rather than against a progress
/// counter. The failure worth catching is the quiet one: a torrent that reports itself complete while
/// its data sits somewhere the engine will not look on the next start, which surfaces only when the
/// user restarts and finds the download back at zero.
/// </para>
/// </summary>
[Collection("Integration")]
public class StorageRelocationTests : IDisposable
{
    private const int PieceLength = 32 * 1024;

    private readonly ILoggerFactory _loggerFactory;
    private readonly string _testRoot;

    public StorageRelocationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PeerSharpMove_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task MoveStorage_TakesTheDataWithIt()
    {
        var fixture = await CreateCompleteTorrentAsync("moved");
        await using var engine = fixture.Engine;

        string destination = Path.Combine(_testRoot, "destination");

        await fixture.Torrent.MoveStorageAsync(destination, TestContext.Current.CancellationToken);

        foreach (var (relativePath, data) in fixture.Payloads)
        {
            string moved = Path.Combine(destination, relativePath);

            Assert.True(File.Exists(moved), $"{relativePath} should exist under the new path");
            Assert.Equal(
                SHA256.HashData(data),
                SHA256.HashData(await File.ReadAllBytesAsync(moved, TestContext.Current.CancellationToken)));
        }

        // The old location must be empty, not merely still readable: a stale copy left behind is what
        // a later recheck of the same content would find and believe.
        foreach (var (relativePath, _) in fixture.Payloads)
        {
            Assert.False(
                File.Exists(Path.Combine(fixture.DownloadPath, relativePath)),
                $"{relativePath} should no longer be at the old path");
        }
    }

    [Fact]
    public async Task MoveStorage_KeepsTheTorrentComplete()
    {
        var fixture = await CreateCompleteTorrentAsync("still-complete");
        await using var engine = fixture.Engine;

        int piecesBefore = fixture.Torrent.PiecesReceived;

        // Without this the comparison below is satisfied by zero equalling zero, which is exactly
        // what a move that quietly lost the data would produce.
        Assert.True(piecesBefore > 0, "the fixture should start out with verified pieces");

        string destination = Path.Combine(_testRoot, "destination");

        await fixture.Torrent.MoveStorageAsync(destination, TestContext.Current.CancellationToken);

        // The recheck is the real question. It re-reads every byte from the new location, so it can
        // only pass if the data arrived intact and the engine is now looking in the right place.
        int verified = await fixture.Torrent.ForceRecheckAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(piecesBefore, verified);
    }

    [Fact]
    public async Task MoveStorage_RefusesWhileTheTorrentIsRunning()
    {
        var fixture = await CreateCompleteTorrentAsync("running");
        await using var engine = fixture.Engine;

        await fixture.Torrent.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Torrent.MoveStorageAsync(
                    Path.Combine(_testRoot, "nope"),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            await fixture.Torrent.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task MoveStorage_PutsEverythingBackWhenOneFileCannotMove()
    {
        var fixture = await CreateCompleteTorrentAsync("rollback", fileCount: 3);
        await using var engine = fixture.Engine;

        string destination = Path.Combine(_testRoot, "blocked");

        // Put a directory exactly where the last file needs to land, so its move is the one that
        // fails and the two before it have already been moved by then. That ordering is the point: a
        // rollback that only ever runs with nothing to undo proves nothing. A directory is used
        // rather than an open handle because the engine's own handle cache holds these files, and the
        // move closes those handles before it starts.
        Directory.CreateDirectory(Path.Combine(destination, fixture.Payloads[^1].Path));

        await Assert.ThrowsAsync<StorageException>(
            () => fixture.Torrent.MoveStorageAsync(destination, TestContext.Current.CancellationToken));

        foreach (var (relativePath, data) in fixture.Payloads)
        {
            string stillThere = Path.Combine(fixture.DownloadPath, relativePath);

            Assert.True(File.Exists(stillThere), $"{relativePath} should have been put back");
            Assert.Equal(
                SHA256.HashData(data),
                SHA256.HashData(await File.ReadAllBytesAsync(stillThere, TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task SetDownloadPath_LeavesTheDataBehind()
    {
        // Pinning the difference between the two, because it is the whole reason MoveStorageAsync
        // exists. SetDownloadPathAsync repoints and nothing more.
        var fixture = await CreateCompleteTorrentAsync("repointed");
        await using var engine = fixture.Engine;

        await fixture.Torrent.SetDownloadPathAsync(
            Path.Combine(_testRoot, "elsewhere"),
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(fixture.DownloadPath, fixture.Payloads[0].Path)));
    }

    [Fact]
    public async Task RenameFile_MovesTheDataToTheNewName()
    {
        var fixture = await CreateCompleteTorrentAsync("renamed");
        await using var engine = fixture.Engine;

        string oldPath = Path.Combine(fixture.DownloadPath, fixture.Payloads[0].Path);
        byte[] expected = await File.ReadAllBytesAsync(oldPath, TestContext.Current.CancellationToken);

        await fixture.Torrent.RenameFileAsync(0, "sub/dir/renamed.bin", TestContext.Current.CancellationToken);

        string newPath = Path.Combine(fixture.DownloadPath, "sub", "dir", "renamed.bin");

        Assert.True(File.Exists(newPath), "the file should be at its new name");
        Assert.False(File.Exists(oldPath), "the file should not still be at its old name");
        Assert.Equal(
            SHA256.HashData(expected),
            SHA256.HashData(await File.ReadAllBytesAsync(newPath, TestContext.Current.CancellationToken)));

        Assert.Equal("sub/dir/renamed.bin", fixture.Torrent.GetRenamedFiles()[0]);
    }

    [Fact]
    public async Task RenameFile_SurvivesInResumeData()
    {
        // The rename has to outlive the process. Rebuilding paths from the torrent's own metadata is
        // what storage does by default, and that would silently undo every rename on the next start,
        // so the resume data is where this is really decided.
        var fixture = await CreateCompleteTorrentAsync("persisted");
        await using var engine = fixture.Engine;

        await fixture.Torrent.RenameFileAsync(0, "kept.bin", TestContext.Current.CancellationToken);

        var resumeData = fixture.Torrent.GetResumeData();
        var state = JsonSerializer.Deserialize(resumeData.Data, PeerSharpJsonContext.Default.TorrentStateData);

        Assert.NotNull(state);
        Assert.Equal("kept.bin", Assert.Single(state.RenamedFiles).Path);
        Assert.True(File.Exists(Path.Combine(fixture.DownloadPath, "kept.bin")));
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("sub/../../escape.bin")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RenameFile_RefusesANameThatLeavesTheDownloadPath(string name)
    {
        var fixture = await CreateCompleteTorrentAsync("guarded");
        await using var engine = fixture.Engine;

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => fixture.Torrent.RenameFileAsync(0, name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenameFile_RefusesAnAbsolutePath()
    {
        var fixture = await CreateCompleteTorrentAsync("absolute");
        await using var engine = fixture.Engine;

        string absolute = Path.Combine(Path.GetTempPath(), "somewhere-else.bin");

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Torrent.RenameFileAsync(0, absolute, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _loggerFactory.Dispose();

        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A handle the OS has not released yet. The temp directory is not this test's subject.
        }
    }

    private sealed record Fixture(
        ClientEngine Engine,
        ITorrent Torrent,
        string DownloadPath,
        (string Path, byte[] Data)[] Payloads);

    /// <summary>
    /// Builds a torrent whose content is already on disk and verified, so these tests need no transfer.
    /// </summary>
    private async Task<Fixture> CreateCompleteTorrentAsync(string name, int fileCount = 2)
    {
        string downloadPath = Path.Combine(_testRoot, name + "-root");
        Directory.CreateDirectory(downloadPath);

        var payloads = new (string Path, byte[] Data)[fileCount];
        var builder = new ApiTorrentFileBuilder().WithName(name).WithPieceLength(PieceLength);

        for (int i = 0; i < fileCount; i++)
        {
            string relative = i == 0 ? "first.bin" : $"nested/file{i}.bin";
            byte[] data = RandomNumberGenerator.GetBytes(PieceLength + (i * 997) + 13);
            payloads[i] = (relative, data);

            string full = Path.Combine(downloadPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, data, TestContext.Current.CancellationToken);

            builder.AddFile(relative, data);
        }

        var torrentFile = builder.Build();

        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            LoggerFactory = _loggerFactory,
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = downloadPath },
                Connection =
                {
                    TcpPort = 0,
                    UdpPort = 0,
                    EnableLsd = false,
                    UpnpPortMapping = false,
                    NatPmpPortMapping = false
                },
                Dht = { Enabled = false }
            }
        });

        await engine.InitializeAsync(TestContext.Current.CancellationToken);

        var torrent = await engine.AddTorrentAsync(
            torrentFile,
            new AddTorrentOptions(downloadPath) { StartImmediately = false },
            TestContext.Current.CancellationToken);

        await torrent.ForceRecheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        return new Fixture(engine, torrent, downloadPath, payloads);
    }
}
