using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.BEncoding;
using PeerSharp.Core;
using PeerSharp.Internals;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utilities;
using System.Text;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Interoperability against the live Mainline DHT.
///
/// <para>
/// <b>These do not run in CI, by design.</b> They need outbound UDP, they talk to third-party
/// nodes we do not control, and they are slow and inherently flaky - a put may land on nodes that
/// drop it, and a get may be answered by a node running a broken implementation. A red result here
/// is a prompt to investigate, not necessarily a defect. Excluded via the
/// <c>PeerSharp.Tests.Interop</c> namespace filter, alongside the existing lane filters.
/// </para>
///
/// <para>
/// What they add over <c>Bep44SpecVectorTests</c>: that suite proves the encoding agrees with the
/// specification's own vectors, which is strong evidence but entirely offline. These prove that
/// real nodes, running other implementations, actually accept what we send and return something we
/// can read back. Only the network can answer that.
/// </para>
///
/// <para>
/// They are gated on the <c>PEERSHARP_INTEROP</c> environment variable rather than xUnit's Skip,
/// which cannot be overridden from the command line and would make them unrunnable without
/// editing code. Set it and select the namespace:
/// </para>
/// <code>
/// PEERSHARP_INTEROP=1 dotnet test --filter "FullyQualifiedName~PeerSharp.Tests.Interop"
/// </code>
/// </summary>
public class Bep44LiveDhtTests
{
    /// <summary>
    /// Long enough for bootstrap plus an iterative lookup against real latency. The DHT is not
    /// fast, and a tighter bound would fail for reasons that have nothing to do with correctness.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    /// <summary>Time allowed for the routing table to fill enough to run a lookup.</summary>
    private static readonly TimeSpan BootstrapWait = TimeSpan.FromSeconds(20);

    private static BString Text(string value) => new(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Skips unless interop testing was asked for. Dynamic rather than a Skip attribute so the
    /// tests remain runnable without a code change.
    /// </summary>
    private static void RequireInteropEnabled()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PEERSHARP_INTEROP")))
        {
            Assert.Skip("Set PEERSHARP_INTEROP=1 to run live DHT interoperability tests.");
        }
    }

    private sealed record LiveNode(DhtManager Dht, UdpListener Listener) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Dht.DisposeAsync();
            await Listener.DisposeAsync();
        }
    }

    /// <summary>
    /// Starts a real DHT node against the public bootstrap servers and waits for it to find peers.
    /// </summary>
    private static async Task<LiveNode> StartLiveNodeAsync(CancellationToken cancellationToken)
    {
        var settings = new Settings();
        // Port 0: let the OS pick, so concurrent runs do not collide.
        var listener = new UdpListener(0, new UdpSocketFactory(), settings, NullLoggerFactory.Instance, TimeProvider.System);
        await listener.StartAsync(cancellationToken);

        var dht = DhtManager.CreateSecure(listener, settings);
        await dht.StartAsync(cancellationToken);

        // Bootstrap is asynchronous; give the routing table time to populate before a lookup.
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(BootstrapWait);
        try
        {
            await Task.Delay(BootstrapWait, wait.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Expected: the wait elapsed.
        }

        return new LiveNode(dht, listener);
    }

    /// <summary>
    /// Publishes an immutable item to the real DHT and reads it back. Immutable first because it
    /// removes signatures from the picture: if this fails, the problem is the get/put plumbing or
    /// the target derivation, not the crypto.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ImmutableItem_RoundTripsThroughTheRealDht()
    {
        RequireInteropEnabled();

        using var cts = new CancellationTokenSource(Budget);
        await using var node = await StartLiveNodeAsync(cts.Token);

        // Unique payload, so a hit cannot be someone else's item at a colliding address.
        var item = new DhtImmutableItem { Value = Text($"PeerSharp interop {Guid.NewGuid()}") };

        int accepted = await node.Dht.PutItemAsync(item, cancellationToken: cts.Token);
        Assert.True(accepted > 0, "No live node accepted the put; the DHT may be unreachable from here.");

        var fetched = await node.Dht.GetItemAsync(item.Target, cancellationToken: cts.Token);

        Assert.NotNull(fetched);
        Assert.Equal(
            ((BString)item.Value).Text,
            ((BString)fetched.Value).Text);
    }

    /// <summary>
    /// The signature path against foreign verifiers: real nodes must accept our Ed25519 signature,
    /// which they will only do if the signature buffer matches what their implementation builds.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task MutableItem_RoundTripsThroughTheRealDht()
    {
        RequireInteropEnabled();

        using var cts = new CancellationTokenSource(Budget);
        await using var node = await StartLiveNodeAsync(cts.Token);

        var seed = Ed25519.GenerateSeed();
        var item = DhtItemCodec.CreateSigned(seed, [], 1, Text($"PeerSharp mutable {Guid.NewGuid()}"));

        int accepted = await node.Dht.PutItemAsync(item, cancellationToken: cts.Token);
        Assert.True(accepted > 0, "No live node accepted the signed put; foreign verifiers rejected our signature.");

        var fetched = await node.Dht.GetItemAsync(item.Target, cancellationToken: cts.Token);

        var mutable = Assert.IsType<DhtMutableItem>(fetched);
        Assert.Equal(1, mutable.SequenceNumber);
        Assert.True(mutable.VerifySignature());
    }

    /// <summary>
    /// A salted item, which exercises the target derivation that differs most between
    /// implementations - the salt has to be appended to the key before hashing, and folded into the
    /// signature buffer, or the record lands somewhere nobody else looks.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SaltedMutableItem_RoundTripsThroughTheRealDht()
    {
        RequireInteropEnabled();

        using var cts = new CancellationTokenSource(Budget);
        await using var node = await StartLiveNodeAsync(cts.Token);

        var seed = Ed25519.GenerateSeed();
        var salt = "peersharp"u8.ToArray();
        var item = DhtItemCodec.CreateSigned(seed, salt, 1, Text($"PeerSharp salted {Guid.NewGuid()}"));

        Assert.True(await node.Dht.PutItemAsync(item, cancellationToken: cts.Token) > 0);

        var mutable = Assert.IsType<DhtMutableItem>(
            await node.Dht.GetItemAsync(item.Target, salt, cts.Token));

        Assert.Equal(salt, mutable.Salt);
    }

    /// <summary>
    /// The whole BEP 46 flow against the real network: publish a record naming an info-hash,
    /// resolve the key back to it, then publish a second version and see the resolution move.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Bep46_PublishAndResolveThroughTheRealDht()
    {
        RequireInteropEnabled();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var node = await StartLiveNodeAsync(cts.Token);

        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var resolver = new Bep46Resolver(node.Dht);

        var first = InfoHash.CreateRandom();
        Assert.True(await resolver.PublishAsync(seed, first, 0, cancellationToken: cts.Token) > 0);

        var resolved = await resolver.ResolveAsync(publicKey, cancellationToken: cts.Token);
        Assert.NotNull(resolved);
        Assert.Equal(first, resolved.Value.InfoHash);

        var second = InfoHash.CreateRandom();
        Assert.True(await resolver.PublishAsync(seed, second, 1, cancellationToken: cts.Token) > 0);

        var updated = await resolver.ResolveAsync(publicKey, cancellationToken: cts.Token);
        Assert.NotNull(updated);
        Assert.Equal(second, updated.Value.InfoHash);
        Assert.Equal(1, updated.Value.SequenceNumber);
    }

    /// <summary>
    /// Reads the specification's own immutable test vector out of the live DHT if any node happens
    /// to hold it. Informational rather than an assertion of our behaviour: it tells us whether
    /// our target derivation finds what other implementations stored, but a miss only means nobody
    /// currently holds that item.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SpecVectorTarget_IsQueryableOnTheRealDht()
    {
        RequireInteropEnabled();

        using var cts = new CancellationTokenSource(Budget);
        await using var node = await StartLiveNodeAsync(cts.Token);

        // BEP 44 test 3: the immutable item holding "Hello World!".
        var target = DhtTarget.FromHex("e5f96f6f38320f0f33959cb4d3d656452117aadb");

        var fetched = await node.Dht.GetItemAsync(target, cancellationToken: cts.Token);

        // Not asserted either way: the lookup completing without error is the useful signal.
        if (fetched is not null)
        {
            Assert.Equal("Hello World!", ((BString)fetched.Value).Text);
        }
    }
}
