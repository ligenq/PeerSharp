using PeerSharp.BEncoding;
using PeerSharp.Core;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using PeerSharp.Internals.Utilities;
using System.Net;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// BEP 46 self-updating torrents, end to end over the loopback DHT fixture: a publisher signs a
/// record naming an info-hash, a subscriber resolves the public key and gets that info-hash back,
/// and a later release supersedes the earlier one.
///
/// The rollback case is the one that matters most. An old record's signature stays valid forever,
/// so nothing but the sequence rule stops an attacker who captured version 1 from serving it to
/// someone already on version 5.
/// </summary>
public class Bep46Tests
{
    private sealed class EngineNetworkManager(DhtManager dht) : INetworkManager
    {
        public IpBlocklist Blocklist { get; } = new();
        public int BoundTcpPort => 0;
        public int BoundUdpPort => 0;
        public IDhtManager Dht { get; } = dht;
        public ILsdManager Lsd { get; } = null!;
        public IPortListener PortListener { get; } = null!;
        public IUtpManager Utp { get; } = null!;

        public IReadOnlyList<PortMappingStatus> GetPortMappingStatus() => [];
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact(Timeout = 30000)]
    public async Task Publish_ThenResolve_ReturnsTheInfoHash()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var infoHash = InfoHash.CreateRandom();

        var publisher = new Bep46Resolver(fixture.Client);
        Assert.True(await publisher.PublishAsync(seed, infoHash, sequenceNumber: 0) > 0);

        var resolved = await publisher.ResolveAsync(publicKey);

        Assert.NotNull(resolved);
        Assert.Equal(infoHash, resolved.Value.InfoHash);
        Assert.Equal(0, resolved.Value.SequenceNumber);
    }

    [Fact(Timeout = 30000)]
    public async Task Resolve_ReturnsNullForAKeyThatHasNeverPublished()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var resolver = new Bep46Resolver(fixture.Client);

        Assert.Null(await resolver.ResolveAsync(Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed())));
    }

    /// <summary>The whole point: the same key later names different content.</summary>
    [Fact(Timeout = 30000)]
    public async Task Publish_ANewVersion_SupersedesTheOld()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var resolver = new Bep46Resolver(fixture.Client);

        var first = InfoHash.CreateRandom();
        var second = InfoHash.CreateRandom();

        await resolver.PublishAsync(seed, first, 0);
        Assert.Equal(first, (await resolver.ResolveAsync(publicKey))!.Value.InfoHash);

        await resolver.PublishAsync(seed, second, 1);

        var resolved = await resolver.ResolveAsync(publicKey);
        Assert.Equal(second, resolved!.Value.InfoHash);
        Assert.Equal(1, resolved.Value.SequenceNumber);
    }

    /// <summary>
    /// The rollback attack. Version 1's signature never expires, so replaying it must be refused
    /// on sequence grounds alone.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Publish_CannotRollBackToAnEarlierVersion()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var resolver = new Bep46Resolver(fixture.Client);

        var original = InfoHash.CreateRandom();
        var current = InfoHash.CreateRandom();

        await resolver.PublishAsync(seed, original, 0);
        await resolver.PublishAsync(seed, current, 1);

        // Re-publishing the original at its original sequence number is exactly what a captured
        // record replay looks like.
        Assert.Equal(0, await resolver.PublishAsync(seed, original, 0));

        Assert.Equal(current, (await resolver.ResolveAsync(publicKey))!.Value.InfoHash);
    }

    [Fact(Timeout = 30000)]
    public async Task PublishNext_TracksTheSequenceNumberItself()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var resolver = new Bep46Resolver(fixture.Client);

        var (accepted, first) = await resolver.PublishNextAsync(seed, InfoHash.CreateRandom());
        Assert.True(accepted > 0);
        Assert.Equal(0, first);

        var latest = InfoHash.CreateRandom();
        var (_, second) = await resolver.PublishNextAsync(seed, latest);
        Assert.Equal(1, second);

        var resolved = await resolver.ResolveAsync(publicKey);
        Assert.Equal(latest, resolved!.Value.InfoHash);
        Assert.Equal(1, resolved.Value.SequenceNumber);
    }

    [Fact(Timeout = 30000)]
    public async Task ClientEngine_PublishesResolvesAndStopsMaintainingARecord()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync(settings =>
        {
            settings.Dht.InitialState = new DhtState(
                InfoHash.CreateRandom().ToArray(),
                Enumerable.Range(1, 6)
                    .Select(index => new DhtNode(
                        NodeId(index),
                        new IPEndPoint(IPAddress.Parse($"192.0.2.{index + 10}"), 7000 + index)))
                    .ToArray());
        });
        await using var engine = ClientEngine.Create(
            new Settings(),
            networkManager: new EngineNetworkManager(fixture.Client),
            takeOwnership: false);
        await engine.InitializeAsync();

        var publisher = TorrentPublisherKey.Create();
        var infoHash = InfoHash.CreateRandom();

        var published = await engine.PublishSelfUpdatingTorrentAsync(publisher, infoHash);
        var resolved = await engine.ResolveSelfUpdatingTorrentAsync(
            TorrentPublisherKey.FromPublicKey(publisher.PublicKey.Span));
        var magnet = MagnetLink.Parse(
            $"magnet:?xs=urn:btpk:{Convert.ToHexStringLower(publisher.PublicKey.Span)}");
        var magnetResolved = await engine.ResolveSelfUpdatingMagnetAsync(magnet);

        Assert.True(published.AcceptedByNodes > 0);
        Assert.Equal(0, published.Version);
        Assert.Equal(new SelfUpdatingTorrentInfo(infoHash, 0), resolved);
        Assert.Equal(resolved, magnetResolved);
        Assert.True(engine.StopMaintainingSelfUpdatingTorrent(publisher));
        Assert.False(engine.StopMaintainingSelfUpdatingTorrent(publisher));
    }

    /// <summary>
    /// One key, several torrents. Without salt separation a publisher would need a fresh identity
    /// per feed.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Publish_KeepsSaltedFeedsIndependent()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var resolver = new Bep46Resolver(fixture.Client);

        var nightly = InfoHash.CreateRandom();
        var stable = InfoHash.CreateRandom();

        await resolver.PublishAsync(seed, nightly, 0, "nightly"u8.ToArray());
        await resolver.PublishAsync(seed, stable, 0, "stable"u8.ToArray());

        Assert.Equal(nightly, (await resolver.ResolveAsync(publicKey, "nightly"u8.ToArray()))!.Value.InfoHash);
        Assert.Equal(stable, (await resolver.ResolveAsync(publicKey, "stable"u8.ToArray()))!.Value.InfoHash);

        // And the unsalted address is a third, empty one.
        Assert.Null(await resolver.ResolveAsync(publicKey));
    }

    [Fact(Timeout = 30000)]
    public async Task Resolve_RejectsARecordWhoseValueIsNotAnInfoHash()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);

        // Correctly signed, but the payload is not a BEP 46 record. A malformed record must read
        // as absent rather than throwing at the caller.
        var junk = new BDict();
        junk.Dict["ih"] = new BString(new byte[5]);
        await fixture.Client.PutItemAsync(DhtItemCodec.CreateSigned(seed, [], 0, junk));

        Assert.Null(await new Bep46Resolver(fixture.Client).ResolveAsync(publicKey));
    }

    [Fact]
    public async Task Publish_RefusesAV2InfoHash()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var resolver = new Bep46Resolver(fixture.Client);

        await Assert.ThrowsAsync<ArgumentException>(
            () => resolver.PublishAsync(Ed25519.GenerateSeed(), InfoHash.CreateRandomV2(), 0));
    }

    [Fact]
    public void ComputeTarget_MatchesTheBep44Derivation()
    {
        var publicKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed());

        Assert.Equal(
            DhtItemCodec.ComputeMutableTarget(publicKey, "salt"u8),
            Bep46Resolver.ComputeTarget(publicKey, "salt"u8));
    }

    private static byte[] NodeId(int suffix)
    {
        var id = new byte[DhtTarget.Length];
        id[^1] = (byte)suffix;
        return id;
    }

    [Fact]
    public async Task Resolve_RejectsAMalformedPublicKey()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var resolver = new Bep46Resolver(fixture.Client);

        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(new byte[31]));
    }

    // ---- Magnet link parsing -----------------------------------------------------------------

    [Fact]
    public void MagnetLink_ParsesABtpkPublicKey()
    {
        var publicKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed());
        var uri = $"magnet:?xs=urn:btpk:{Convert.ToHexStringLower(publicKey)}";

        var magnet = MagnetLink.Parse(uri);

        Assert.True(magnet.IsSelfUpdating);
        Assert.Equal(publicKey, magnet.PublicKey.ToArray());
        Assert.True(magnet.Salt.IsEmpty);
    }

    [Fact]
    public void MagnetLink_ParsesABtpkPublicKeyWithSalt()
    {
        var publicKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed());
        // BEP 46: "magnet:?xs=urn:btpk:[Public Key (Hex)]&s=[Salt (Hex)]" - the salt is hex.
        var salt = "nightly"u8.ToArray();
        var uri = $"magnet:?xs=urn:btpk:{Convert.ToHexStringLower(publicKey)}&s={Convert.ToHexStringLower(salt)}";

        var magnet = MagnetLink.Parse(uri);

        Assert.True(magnet.IsSelfUpdating);
        Assert.Equal(publicKey, magnet.PublicKey.ToArray());
        Assert.Equal(salt, magnet.Salt.ToArray());
    }

    [Fact]
    public void MagnetLink_WithoutBtpk_IsNotSelfUpdating()
    {
        var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{new string('a', 40)}");

        Assert.False(magnet.IsSelfUpdating);
        Assert.True(magnet.PublicKey.IsEmpty);
    }

    /// <summary>
    /// A self-updating link has no info-hash until it is resolved, which is the whole point - the
    /// info-hash is not knowable from the link.
    /// </summary>
    [Fact]
    public void MagnetLink_SelfUpdatingLinkNeedNotCarryAnInfoHash()
    {
        var publicKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed());

        Assert.True(MagnetLink.TryParse($"magnet:?xs=urn:btpk:{Convert.ToHexStringLower(publicKey)}", out var magnet));
        Assert.NotNull(magnet);
        Assert.True(magnet.IsSelfUpdating);
    }

    [Theory]
    [InlineData("magnet:?xs=urn:btpk:notvalidhex")]
    [InlineData("magnet:?xs=urn:btpk:aabb")]
    [InlineData("magnet:?xs=urn:btpk:")]
    public void MagnetLink_IgnoresAMalformedBtpkSource(string uri)
    {
        // Ignored rather than fatal: the rest of the link may still be usable.
        Assert.True(MagnetLink.TryParse($"{uri}&xt=urn:btih:{new string('c', 40)}", out var magnet));
        Assert.NotNull(magnet);
        Assert.False(magnet.IsSelfUpdating);
    }

    /// <summary>
    /// A salt that is not hex would address a different record than the link names, so the link is
    /// treated as not self-updating rather than resolved against the wrong target.
    /// </summary>
    [Fact]
    public void MagnetLink_RejectsANonHexSalt()
    {
        var publicKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed());
        var uri = $"magnet:?xs=urn:btpk:{Convert.ToHexStringLower(publicKey)}&s=nightly"
            + $"&xt=urn:btih:{new string('d', 40)}";

        var magnet = MagnetLink.Parse(uri);

        Assert.False(magnet.IsSelfUpdating);
    }

    [Fact]
    public void MagnetLink_KeepsTheBtpkSourceInExactSources()
    {
        var publicKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed());
        var source = $"urn:btpk:{Convert.ToHexStringLower(publicKey)}";

        var magnet = MagnetLink.Parse($"magnet:?xs={Uri.EscapeDataString(source)}");

        Assert.Contains(source, magnet.ExactSources);
    }

    /// <summary>
    /// The complete flow a client performs for a self-updating link: parse, resolve, download the
    /// info-hash that comes back.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task EndToEnd_MagnetLinkResolvesToThePublishedInfoHash()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var published = InfoHash.CreateRandom();

        var salt = "nightly"u8.ToArray();
        var resolver = new Bep46Resolver(fixture.Client);
        await resolver.PublishAsync(seed, published, 0, salt);

        var magnet = MagnetLink.Parse(
            $"magnet:?xs=urn:btpk:{Convert.ToHexStringLower(publicKey)}&s={Convert.ToHexStringLower(salt)}");

        Assert.True(magnet.IsSelfUpdating);
        var resolved = await resolver.ResolveAsync(magnet.PublicKey.ToArray(), magnet.Salt.ToArray());

        Assert.Equal(published, resolved!.Value.InfoHash);
    }
}
