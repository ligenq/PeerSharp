using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.BEncoding;
using PeerSharp.Exceptions;
using PeerSharp.Internals;
using PeerSharp.Internals.Utilities;
using System.Security.Cryptography;
using System.Text;

namespace PeerSharp.Tests.Core;

public sealed class TorrentIdentityRegressionTests
{
    [Fact]
    public void TorrentFile_DifferentV2TorrentsRemainDistinctInCollections()
    {
        var first = CreateFile("first", TorrentVersion.V2);
        var second = CreateFile("second", TorrentVersion.V2);
        var copy = TorrentFile.Parse(first.RawData.ToArray());

        Assert.NotEqual(first.InfoHashV2, second.InfoHashV2);
        Assert.False(first.Equals(second));
        Assert.True(first != second);
        Assert.True(first == copy);
        Assert.Equal(first.GetHashCode(), copy.GetHashCode());
        Assert.Equal(2, new HashSet<TorrentFile> { first, second, copy }.Count);
    }

    [Theory]
    [InlineData((int)TorrentVersion.V1)]
    [InlineData((int)TorrentVersion.V2)]
    [InlineData((int)TorrentVersion.Hybrid)]
    public void TorrentFile_IdentityIgnoresOuterTrackerMetadata(int version)
    {
        var first = CreateFile("same", (TorrentVersion)version);
        var root = (BDict)BencodeParser.Parse(first.RawData.ToArray());
        root.Dict["announce"] = new BString("https://example.invalid/announce"u8.ToArray());
        var second = TorrentFile.Parse(BencodeWriter.Write(root));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Registry_RejectsHybridAndSingleHashMagnetInEitherOrder(bool useV2, bool hybridFirst)
    {
        var file = CreateFile("hybrid", TorrentVersion.Hybrid);
        await using var hybrid = TorrentTestUtility.CreateMinimal(file.Metadata);
        var magnetMetadata = new TorrentFileMetadata();
        magnetMetadata.Info.Hash = useV2 ? InfoHash.Empty : file.InfoHash;
        magnetMetadata.Info.HashV2 = useV2 ? file.InfoHashV2 : InfoHash.EmptyV2;
        await using var magnet = TorrentTestUtility.CreateMinimal(magnetMetadata);
        var registry = new TorrentRegistry();
        var existing = hybridFirst ? hybrid : magnet;
        var duplicate = hybridFirst ? magnet : hybrid;
        registry.Add(existing);

        Assert.True(existing.HasSameIdentity(duplicate));
        Assert.Throws<TorrentAlreadyExistsException>(() => registry.Add(duplicate));
        Assert.Same(existing, Assert.Single(registry.GetAll()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_RemovalAfterMetadataUpgradeAllowsOriginalMagnetAgain(bool removeByHash)
    {
        var file = CreateFile("hybrid", TorrentVersion.Hybrid);
        var metadata = new TorrentFileMetadata();
        metadata.Info.Hash = file.InfoHash;
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var registry = new TorrentRegistry();
        registry.Add(torrent);
        torrent.InfoFile.Info = file.Metadata.Info;

        Assert.True(registry.TryGet(file.InfoHashV2, out var found));
        Assert.Same(torrent, found);
        Assert.True(removeByHash ? registry.Remove(file.InfoHashV2, out _) : registry.Remove(torrent));
        Assert.Empty(registry.GetAll());

        var replacementMetadata = new TorrentFileMetadata();
        replacementMetadata.Info.Hash = file.InfoHash;
        await using var replacement = TorrentTestUtility.CreateMinimal(replacementMetadata);
        registry.Add(replacement);
        Assert.Same(replacement, Assert.Single(registry.GetAll()));
    }

    [Theory]
    [InlineData("d4:name1:a6:lengthi0e12:piece lengthi16384e6:pieces0:e")]
    [InlineData("d6:lengthi0e4:name1:a4:name1:b12:piece lengthi16384e6:pieces0:e")]
    [InlineData("d6:lengthi0e4:name1:a12:piece lengthi16384e6:pieces0:1:xd1:bi1e1:ai2eee")]
    public void Parsing_RejectsNoncanonicalInfoInFilesAndMetadataExchange(string encodedInfo)
    {
        var info = Encoding.ASCII.GetBytes(encodedInfo);
        var file = "d4:info"u8.ToArray().Concat(info).Append((byte)'e').ToArray();

        Assert.Throws<TorrentMetadataException>(() => TorrentFileParser.Parse(file));
        Assert.Throws<TorrentMetadataException>(() => TorrentFileParser.ParseInfoBytes(info));
    }

    [Fact]
    public void Parsing_RejectsTrailingDataInMetadataExchange()
    {
        var file = CreateFile("hybrid", TorrentVersion.Hybrid);
        var info = file.Metadata.InfoBytes!.Concat("junk"u8.ToArray()).ToArray();
        Assert.Throws<TorrentMetadataException>(() => TorrentFileParser.ParseInfoBytes(info));
    }

    [Fact]
    public void Parsing_CanonicalHybridHashesMatchOriginalBytesInBothPaths()
    {
        var file = CreateFile("hybrid", TorrentVersion.Hybrid);
        var info = file.Metadata.InfoBytes!;
        var metadata = TorrentFileParser.ParseInfoBytes(info);
        Assert.Equal(new InfoHash(SHA1.HashData(info)), file.InfoHash);
        Assert.Equal(new InfoHash(SHA256.HashData(info)), file.InfoHashV2);
        Assert.Equal(file.InfoHash, metadata.Info.Hash);
        Assert.Equal(file.InfoHashV2, metadata.Info.HashV2);
    }

    [Fact(Timeout = 30000)]
    public async Task Session_TwoV2TorrentsRoundTripAndCanBeRemovedIndependently()
    {
        var path = Path.Combine(Path.GetTempPath(), "PeerSharpIdentity_" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = CreateFile("first", TorrentVersion.V2);
            var second = CreateFile("second", TorrentVersion.V2);
            var options = new AddTorrentOptions { StartImmediately = false, DownloadPath = path };
            await using (var engine = ClientEngine.Create(new TorrentClientOptions { Settings = SettingsFor(path) }))
            {
                await engine.AddTorrentAsync(first, options);
                await engine.AddTorrentAsync(second, options);
                await engine.SaveSessionAsync();
            }

            var persistence = new SessionPersistence(path, NullLogger<SessionPersistence>.Instance);
            var saved = await persistence.LoadAllAsync();
            Assert.Equal(2, saved.Count);
            Assert.Contains(saved, entry => entry.Hash == first.InfoHashV2 && entry.TorrentFileData!.SequenceEqual(first.RawData.ToArray()));
            Assert.Contains(saved, entry => entry.Hash == second.InfoHashV2 && entry.TorrentFileData!.SequenceEqual(second.RawData.ToArray()));

            await using var restored = ClientEngine.Create(new TorrentClientOptions { Settings = SettingsFor(path) });
            await restored.InitializeAsync();
            Assert.NotNull(restored.GetTorrent(first.InfoHashV2));
            Assert.NotNull(restored.GetTorrent(second.InfoHashV2));
            await restored.RemoveTorrentAsync(first.InfoHashV2.TruncateToV1());
            Assert.Equal(second.InfoHashV2, Assert.Single(await persistence.LoadAllAsync()).Hash);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Session_RestoredHybridKeepsItsOriginalV2PersistenceKey()
    {
        var path = Path.Combine(Path.GetTempPath(), "PeerSharpIdentity_" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = CreateFile("hybrid", TorrentVersion.Hybrid);
            var persistence = new SessionPersistence(path, NullLogger<SessionPersistence>.Instance);
            await persistence.SaveAsync(new SavedTorrentEntry(file.InfoHashV2, file.RawData.ToArray()));
            await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = SettingsFor(path) });
            await engine.InitializeAsync();
            Assert.NotNull(engine.GetTorrent(file.InfoHashV2));
            await engine.SaveSessionAsync();
            Assert.Equal(file.InfoHashV2, Assert.Single(await persistence.LoadAllAsync()).Hash);
            await engine.RemoveTorrentAsync(file.InfoHash);
            Assert.Empty(await persistence.LoadAllAsync());
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task ResumeData_KeepsTheOriginalV2IdentityAfterMetadataUpgrade()
    {
        var file = CreateFile("hybrid", TorrentVersion.Hybrid);
        var metadata = new TorrentFileMetadata();
        metadata.Info.HashV2 = file.InfoHashV2;
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata);

        Assert.Equal(file.InfoHashV2, torrent.GetResumeData().Hash);
        torrent.InfoFile.Info = file.Metadata.Info;
        Assert.Equal(file.InfoHashV2, torrent.GetResumeData().Hash);
    }

    [Fact]
    public async Task Session_MetadataUpgradeKeepsTheOriginalHashAndCachedData()
    {
        var path = Path.Combine(Path.GetTempPath(), "PeerSharpIdentity_" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = CreateFile("hybrid", TorrentVersion.Hybrid);
            var metadata = new TorrentFileMetadata();
            metadata.Info.HashV2 = file.InfoHashV2;
            await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
            var persistence = new SessionPersistence(path, NullLogger<SessionPersistence>.Instance);
            await using var manager = new SessionManager(persistence, new TorrentRegistry(), TimeProvider.System, NullLogger<SessionManager>.Instance);
            manager.RegisterTorrentData(file.InfoHashV2, file.RawData.ToArray(), null);
            await manager.SaveTorrentEntryAsync(torrent);
            torrent.InfoFile.Info = file.Metadata.Info;
            await manager.SaveTorrentEntryAsync(torrent);

            var saved = Assert.Single(await persistence.LoadAllAsync());
            Assert.Equal(file.InfoHashV2, saved.Hash);
            Assert.Equal(file.RawData.ToArray(), saved.TorrentFileData);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static Settings SettingsFor(string path)
    {
        var settings = new Settings();
        settings.Files.DefaultDownloadPath = path;
        settings.Session.Enabled = true;
        settings.Session.SessionPath = path;
        settings.Session.AutoSaveIntervalSeconds = 0;
        settings.Dht.Enabled = false;
        settings.Connection.EnableTcpIn = false;
        settings.Connection.EnableUtpIn = false;
        settings.Connection.EnableUtpOut = false;
        settings.Connection.EnableLsd = false;
        return settings;
    }

    private static TorrentFile CreateFile(string name, TorrentVersion version)
    {
        var info = new BDict();
        info.Dict["name"] = new BString(Encoding.UTF8.GetBytes(name));
        info.Dict["piece length"] = new BNumber(16384);
        if (version != TorrentVersion.V2)
        {
            info.Dict["length"] = new BNumber(0);
            info.Dict["pieces"] = new BString(Array.Empty<byte>());
        }
        if (version != TorrentVersion.V1)
        {
            info.Dict["meta version"] = new BNumber(2);
            var properties = new BDict();
            properties.Dict["length"] = new BNumber(0);
            var file = new BDict();
            file.Dict[""] = properties;
            var tree = new BDict();
            tree.Dict[name] = file;
            info.Dict["file tree"] = tree;
        }
        var root = new BDict();
        root.Dict["info"] = info;
        return TorrentFile.Parse(BencodeWriter.Write(root));
    }
}

