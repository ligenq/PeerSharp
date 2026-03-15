using PeerSharp.Internals;

namespace PeerSharp.Tests.Integration;

public class ClientEnginePersistenceTests
{
    private readonly ITestOutputHelper _output;

    public ClientEnginePersistenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Session_PersistsAndRestoresTorrents()
    {
        string sessionPath = Path.Combine(Path.GetTempPath(), "PeerSharpTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(sessionPath);

        try
        {
            var settings = new Settings();
            settings.Session.Enabled = true;
            settings.Session.SessionPath = sessionPath;
            settings.Session.AutoSaveIntervalSeconds = 3600;
            settings.Files.DefaultDownloadPath = sessionPath;

            // Build a proper torrent file with raw bytes so it can be persisted
            var torrentFile = new TorrentFileBuilder()
                .WithName("PersistedTorrent")
                .WithPieceLength(16384)
                .AddFile("test.dat", new byte[16384])
                .Build();

            _output.WriteLine($"Built torrent hash: {torrentFile.InfoHash.ToHexString()}");
            _output.WriteLine($"RawData length: {torrentFile.RawData.Length}");

            // 1. Start Engine 1
            var engine1 = ClientEngine.Create(new TorrentClientOptions { Settings = settings });
            await engine1.InitializeAsync();

            var torrent = await engine1.AddTorrentAsync(torrentFile, new AddTorrentOptions { StartImmediately = false });
            _output.WriteLine($"Added torrent: {torrent.Name}, hash: {torrent.Hash.ToHexString()}");
            _output.WriteLine($"Torrents in engine1: {engine1.GetTorrents().Count}");

            // 2. Stop Engine 1 (Trigger Save)
            await engine1.StopAsync();
            await engine1.DisposeAsync();

            // Debug: Check what was saved
            var torrentsDir = Path.Combine(sessionPath, "torrents");
            if (Directory.Exists(torrentsDir))
            {
                foreach (var dir in Directory.GetDirectories(torrentsDir))
                {
                    _output.WriteLine($"Saved dir: {dir}");
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var fi = new FileInfo(f);
                        _output.WriteLine($"  File: {fi.Name} ({fi.Length} bytes)");
                    }
                }
            }
            else
            {
                _output.WriteLine("No torrents directory exists!");
            }

            // 3. Start Engine 2
            var engine2 = ClientEngine.Create(new TorrentClientOptions { Settings = settings });
            await engine2.InitializeAsync(); // Should load

            _output.WriteLine($"Torrents in engine2: {engine2.GetTorrents().Count}");

            // 4. Verify
            Assert.Single(engine2.GetTorrents());
            var loaded = engine2.GetTorrents()[0];
            Assert.Equal("PersistedTorrent", loaded.Name);
            Assert.Equal(torrentFile.InfoHash, loaded.Hash);

            await engine2.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(sessionPath))
            {
                Directory.Delete(sessionPath, true);
            }
        }
    }
}
