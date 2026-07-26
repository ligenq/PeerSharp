using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utilities;
using System.Net;
using System.Text;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// The BEP 44 storage node accepts data from strangers, so most of these tests are about what it
/// refuses: oversized values, forged signatures, replayed sequence numbers, and floods.
/// </summary>
public class DhtItemStoreTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private static BString Text(string value) => new(Encoding.UTF8.GetBytes(value));

    private static readonly IPAddress Source = IPAddress.Parse("198.51.100.7");

    private DhtItemStore CreateStore(int capacity = 1000, int putsPerAddress = 1000)
    {
        return new DhtItemStore(_time, NullLoggerFactory.Instance, capacity, DhtItemStore.DefaultExpiry, putsPerAddress);
    }

    [Fact]
    public void Store_AcceptsAndReturnsAnImmutableItem()
    {
        var store = CreateStore();
        var item = new DhtImmutableItem { Value = Text("hello") };

        Assert.Equal(DhtPutError.None, store.Store(item));
        Assert.Same(item, store.TryGet(item.Target));
    }

    [Fact]
    public void Store_AcceptsAndReturnsAMutableItem()
    {
        var store = CreateStore();
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), [], 1, Text("value"));

        Assert.Equal(DhtPutError.None, store.Store(item));
        Assert.Same(item, store.TryGet(item.Target));
    }

    [Fact]
    public void TryGet_ReturnsNullForAnUnknownTarget()
    {
        Assert.Null(CreateStore().TryGet(DhtTarget.FromHex(new string('a', 40))));
    }

    [Fact]
    public void Store_RejectsAForgedSignature()
    {
        var store = CreateStore();
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), [], 1, Text("value"));
        var forged = item with { Value = Text("tampered") };

        Assert.Equal(DhtPutError.InvalidSignature, store.Store(forged));
        Assert.Null(store.TryGet(forged.Target));
    }

    [Fact]
    public void Store_AcceptsANewerVersionAndServesIt()
    {
        var store = CreateStore();
        var seed = Ed25519.GenerateSeed();
        var first = DhtItemCodec.CreateSigned(seed, [], 1, Text("v1"));
        var second = DhtItemCodec.CreateSigned(seed, [], 2, Text("v2"));

        Assert.Equal(DhtPutError.None, store.Store(first));
        Assert.Equal(DhtPutError.None, store.Store(second));

        var stored = Assert.IsType<DhtMutableItem>(store.TryGet(first.Target));
        Assert.Equal(2, stored.SequenceNumber);
    }

    /// <summary>
    /// The rollback attack: replaying a captured older record over a newer one. The signature on
    /// the old record is perfectly valid, so only the sequence rule stops it.
    /// </summary>
    [Fact]
    public void Store_RejectsAReplayOfAnOlderVersion()
    {
        var store = CreateStore();
        var seed = Ed25519.GenerateSeed();
        var old = DhtItemCodec.CreateSigned(seed, [], 1, Text("v1"));
        var current = DhtItemCodec.CreateSigned(seed, [], 2, Text("v2"));

        store.Store(old);
        store.Store(current);

        Assert.True(old.VerifySignature());
        Assert.Equal(DhtPutError.SequenceNumberTooLow, store.Store(old));

        var stored = Assert.IsType<DhtMutableItem>(store.TryGet(current.Target));
        Assert.Equal(2, stored.SequenceNumber);
    }

    [Fact]
    public void Store_HonoursCompareAndSwap()
    {
        var store = CreateStore();
        var seed = Ed25519.GenerateSeed();
        store.Store(DhtItemCodec.CreateSigned(seed, [], 5, Text("current")));

        var next = DhtItemCodec.CreateSigned(seed, [], 6, Text("next"));

        Assert.Equal(DhtPutError.CasMismatch, store.Store(next, compareAndSwap: 4));
        Assert.Equal(DhtPutError.None, store.Store(next, compareAndSwap: 5));
    }

    [Fact]
    public void Store_RejectsCompareAndSwapOnAnImmutableItem()
    {
        var store = CreateStore();

        Assert.Equal(DhtPutError.Protocol, store.Store(new DhtImmutableItem { Value = Text("x") }, compareAndSwap: 1));
    }

    [Fact]
    public void Store_SeparatesItemsBySalt()
    {
        var store = CreateStore();
        var seed = Ed25519.GenerateSeed();
        var photos = DhtItemCodec.CreateSigned(seed, "photos"u8, 1, Text("photo list"));
        var profile = DhtItemCodec.CreateSigned(seed, "profile"u8, 1, Text("profile"));

        Assert.Equal(DhtPutError.None, store.Store(photos));
        Assert.Equal(DhtPutError.None, store.Store(profile));

        Assert.NotEqual(photos.Target, profile.Target);
        Assert.Equal(2, store.Count);
    }

    // ---- Expiry ------------------------------------------------------------------------------

    [Fact]
    public void TryGet_DropsAnExpiredItem()
    {
        var store = CreateStore();
        var item = new DhtImmutableItem { Value = Text("perishable") };
        store.Store(item);

        _time.Advance(DhtItemStore.DefaultExpiry - TimeSpan.FromSeconds(1));
        Assert.NotNull(store.TryGet(item.Target));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(store.TryGet(item.Target));
    }

    [Fact]
    public void Store_RefreshesTheExpiryOfARepublishedItem()
    {
        var store = CreateStore();
        var item = new DhtImmutableItem { Value = Text("kept alive") };
        store.Store(item);

        _time.Advance(DhtItemStore.DefaultExpiry - TimeSpan.FromMinutes(1));
        Assert.Equal(DhtPutError.None, store.Store(item));

        // Past the original deadline, but the republish reset the clock.
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(store.TryGet(item.Target));
    }

    [Fact]
    public void Prune_RemovesExpiredItems()
    {
        var store = CreateStore();
        store.Store(new DhtImmutableItem { Value = Text("a") });
        store.Store(new DhtImmutableItem { Value = Text("b") });
        Assert.Equal(2, store.Count);

        _time.Advance(DhtItemStore.DefaultExpiry + TimeSpan.FromSeconds(1));
        store.Prune();

        Assert.Equal(0, store.Count);
    }

    // ---- Capacity ----------------------------------------------------------------------------

    [Fact]
    public void Store_EvictsTheOldestWhenFull()
    {
        var store = CreateStore(capacity: 3);

        var first = new DhtImmutableItem { Value = Text("first") };
        store.Store(first);
        _time.Advance(TimeSpan.FromSeconds(1));
        store.Store(new DhtImmutableItem { Value = Text("second") });
        _time.Advance(TimeSpan.FromSeconds(1));
        store.Store(new DhtImmutableItem { Value = Text("third") });
        _time.Advance(TimeSpan.FromSeconds(1));

        var fourth = new DhtImmutableItem { Value = Text("fourth") };
        Assert.Equal(DhtPutError.None, store.Store(fourth));

        Assert.Equal(3, store.Count);
        Assert.Null(store.TryGet(first.Target));
        Assert.NotNull(store.TryGet(fourth.Target));
    }

    [Fact]
    public void Store_UpdatingAnExistingItemDoesNotConsumeCapacity()
    {
        var store = CreateStore(capacity: 2);
        var seed = Ed25519.GenerateSeed();
        var other = new DhtImmutableItem { Value = Text("other") };

        store.Store(DhtItemCodec.CreateSigned(seed, [], 1, Text("v1")));
        store.Store(other);
        Assert.Equal(2, store.Count);

        Assert.Equal(DhtPutError.None, store.Store(DhtItemCodec.CreateSigned(seed, [], 2, Text("v2"))));
        Assert.Equal(2, store.Count);
        Assert.NotNull(store.TryGet(other.Target));
    }

    // ---- Rate limiting -----------------------------------------------------------------------

    /// <summary>
    /// The limit exists because verification costs about 270 microseconds, so an unbounded put
    /// rate is a CPU exhaustion primitive. It has to be checkable before that cost is paid.
    /// </summary>
    [Fact]
    public void IsPutAllowed_StopsAFloodFromOneAddress()
    {
        var store = CreateStore(putsPerAddress: 5);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(store.IsPutAllowed(Source), $"Put {i} should have been allowed.");
        }

        Assert.False(store.IsPutAllowed(Source));
    }

    [Fact]
    public void IsPutAllowed_BudgetsPerAddress()
    {
        var store = CreateStore(putsPerAddress: 2);
        var other = IPAddress.Parse("203.0.113.9");

        Assert.True(store.IsPutAllowed(Source));
        Assert.True(store.IsPutAllowed(Source));
        Assert.False(store.IsPutAllowed(Source));

        // A different peer is unaffected by the first one's flood.
        Assert.True(store.IsPutAllowed(other));
        Assert.True(store.IsPutAllowed(other));
    }

    [Fact]
    public void IsPutAllowed_RecoversAfterTheWindow()
    {
        var store = CreateStore(putsPerAddress: 2);

        Assert.True(store.IsPutAllowed(Source));
        Assert.True(store.IsPutAllowed(Source));
        Assert.False(store.IsPutAllowed(Source));

        _time.Advance(DhtItemStore.RateLimitWindow + TimeSpan.FromSeconds(1));

        Assert.True(store.IsPutAllowed(Source));
    }

    [Fact]
    public void Prune_ForgetsStaleRateCounters()
    {
        var store = CreateStore(putsPerAddress: 1);
        Assert.True(store.IsPutAllowed(Source));
        Assert.False(store.IsPutAllowed(Source));

        _time.Advance(DhtItemStore.RateLimitWindow + TimeSpan.FromSeconds(1));
        store.Prune();

        Assert.True(store.IsPutAllowed(Source));
    }

    // ---- Size limits -------------------------------------------------------------------------

    [Fact]
    public void Store_RejectsAnOversizedValue()
    {
        var store = CreateStore();
        var seed = Ed25519.GenerateSeed();
        var oversized = new BString(new byte[1000]);

        var item = new DhtMutableItem
        {
            Value = oversized,
            PublicKey = Ed25519.PublicKeyFromSeed(seed),
            SequenceNumber = 0,
            Signature = Ed25519.Sign(DhtItemCodec.BuildSignatureBuffer([], 0, oversized), seed),
        };

        Assert.Equal(DhtPutError.ValueTooBig, store.Store(item));
    }

    [Fact]
    public void Store_RejectsAnOversizedSalt()
    {
        var store = CreateStore();
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), new byte[64], 0, Text("x"));

        Assert.Equal(DhtPutError.SaltTooBig, store.Store(item with { Salt = new byte[65] }));
    }
}
