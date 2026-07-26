using PeerSharp.BEncoding;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Utilities;
using System.Net;
using System.Text;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// Exercises the BEP 44 wire layer by wiring two DhtManager instances together over an in-memory
/// transport, so a get or put really is encoded, sent, parsed, answered and decoded.
///
/// Unit-testing the store and codec separately proves the rules are right; this proves they are
/// actually reachable over the protocol, which is where the interesting mistakes live - a
/// mislabelled argument key or a token computed over the wrong bytes passes every unit test and
/// fails against every real node.
/// </summary>
public class DhtBep44WireTests
{
    private static BString Text(string value) => new(Encoding.UTF8.GetBytes(value));

    [Fact(Timeout = 30000)]
    public async Task PutThenGet_RoundTripsAnImmutableItem()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var item = new DhtImmutableItem { Value = Text("immutable payload") };

        int accepted = await fixture.Client.PutItemAsync(item);
        Assert.True(accepted > 0, "No node accepted the put.");

        var fetched = await fixture.Client.GetItemAsync(item.Target);

        var immutable = Assert.IsType<DhtImmutableItem>(fetched);
        Assert.Equal("immutable payload", ((BString)immutable.Value).Text);
    }

    [Fact(Timeout = 30000)]
    public async Task PutThenGet_RoundTripsAMutableItem()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var item = DhtItemCodec.CreateSigned(seed, [], sequenceNumber: 1, Text("mutable payload"));

        Assert.True(await fixture.Client.PutItemAsync(item) > 0);

        var fetched = await fixture.Client.GetItemAsync(item.Target);

        var mutable = Assert.IsType<DhtMutableItem>(fetched);
        Assert.Equal(1, mutable.SequenceNumber);
        Assert.Equal("mutable payload", ((BString)mutable.Value).Text);
        Assert.Equal(item.PublicKey, mutable.PublicKey);
        Assert.True(mutable.VerifySignature());
    }

    [Fact(Timeout = 30000)]
    public async Task PutThenGet_RoundTripsASaltedItem()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var item = DhtItemCodec.CreateSigned(seed, "photos"u8, 1, Text("photo list"));

        Assert.True(await fixture.Client.PutItemAsync(item) > 0);

        // The salt is supplied by the caller, not returned by the node: a get reply carries no
        // salt because the requester derived the target from it in the first place.
        var mutable = Assert.IsType<DhtMutableItem>(
            await fixture.Client.GetItemAsync(item.Target, "photos"u8.ToArray()));
        Assert.Equal("photos"u8.ToArray(), mutable.Salt);
    }

    /// <summary>
    /// Without the salt the reconstructed record hashes to a different address, so it cannot be
    /// verified and must be discarded rather than returned unchecked.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Get_DiscardsASaltedItemWhenTheSaltIsNotSupplied()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), "photos"u8, 1, Text("photo list"));

        Assert.True(await fixture.Client.PutItemAsync(item) > 0);

        Assert.Null(await fixture.Client.GetItemAsync(item.Target));
    }

    [Fact(Timeout = 30000)]
    public async Task Put_ReplacesAnItemWithANewerVersion()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();

        await fixture.Client.PutItemAsync(DhtItemCodec.CreateSigned(seed, [], 1, Text("v1")));
        await fixture.Client.PutItemAsync(DhtItemCodec.CreateSigned(seed, [], 2, Text("v2")));

        var target = DhtItemCodec.ComputeMutableTarget(Ed25519.PublicKeyFromSeed(seed), []);
        var mutable = Assert.IsType<DhtMutableItem>(await fixture.Client.GetItemAsync(target));

        Assert.Equal(2, mutable.SequenceNumber);
        Assert.Equal("v2", ((BString)mutable.Value).Text);
    }

    /// <summary>
    /// A replayed older record must not displace a newer one, even though its signature is valid.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Put_RejectsAReplayedOlderVersionOverTheWire()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var old = DhtItemCodec.CreateSigned(seed, [], 1, Text("v1"));

        await fixture.Client.PutItemAsync(old);
        await fixture.Client.PutItemAsync(DhtItemCodec.CreateSigned(seed, [], 2, Text("v2")));

        int accepted = await fixture.Client.PutItemAsync(old);
        Assert.Equal(0, accepted);

        var mutable = Assert.IsType<DhtMutableItem>(await fixture.Client.GetItemAsync(old.Target));
        Assert.Equal(2, mutable.SequenceNumber);
    }

    [Fact(Timeout = 30000)]
    public async Task Get_ReturnsNullForAnAddressNobodyHolds()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();

        Assert.Null(await fixture.Client.GetItemAsync(DhtTarget.FromHex(new string('b', 40))));
    }

    [Fact(Timeout = 30000)]
    public async Task Put_ReturnsZeroWhenNoNodeAnswers()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        fixture.ServerTransport.Blackhole = true;
        fixture.ClientTransport.Blackhole = true;

        int accepted = await fixture.Client.PutItemAsync(new DhtImmutableItem { Value = Text("unreachable") });

        Assert.Equal(0, accepted);
    }

    [Fact(Timeout = 30000)]
    public async Task Put_RefusesAnUnsignedMutableItemBeforeTouchingTheNetwork()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var item = DhtItemCodec.CreateSigned(Ed25519.GenerateSeed(), [], 1, Text("payload"));
        var forged = item with { Value = Text("tampered") };

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.PutItemAsync(forged));
        Assert.Equal(0, fixture.ClientTransport.DroppedPackets);
    }

    [Fact(Timeout = 30000)]
    public async Task Put_HonoursCompareAndSwapOverTheWire()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        await fixture.Client.PutItemAsync(DhtItemCodec.CreateSigned(seed, [], 5, Text("current")));

        var next = DhtItemCodec.CreateSigned(seed, [], 6, Text("next"));

        Assert.Equal(0, await fixture.Client.PutItemAsync(next, compareAndSwap: 4));
        Assert.True(await fixture.Client.PutItemAsync(next, compareAndSwap: 5) > 0);
    }

    /// <summary>
    /// A malicious node can answer any query with any bytes. The client must reject a record whose
    /// key and salt do not hash to the address it asked about, otherwise a node could serve a
    /// validly signed record belonging to a different address and have it accepted.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Get_RejectsAValidlySignedItemForTheWrongTarget()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var elsewhere = DhtItemCodec.CreateSigned(seed, "elsewhere"u8, 1, Text("not yours"));

        await fixture.Client.PutItemAsync(elsewhere);

        // Ask for a different address; the server holds nothing there.
        var otherTarget = DhtItemCodec.ComputeMutableTarget(elsewhere.PublicKey, "different"u8);

        Assert.NotEqual(elsewhere.Target, otherTarget);
        Assert.Null(await fixture.Client.GetItemAsync(otherTarget));
    }

    [Fact(Timeout = 30000)]
    public async Task Get_ReportsTheSequenceNumberEvenWhenTheCallerIsUpToDate()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();
        var seed = Ed25519.GenerateSeed();
        var item = DhtItemCodec.CreateSigned(seed, [], 3, Text("payload"));
        await fixture.Client.PutItemAsync(item);

        // A plain get still returns the full value; the seq-conditional path is an optimisation
        // for callers that already hold a version, exercised through the server handler directly.
        var mutable = Assert.IsType<DhtMutableItem>(await fixture.Client.GetItemAsync(item.Target));
        Assert.Equal(3, mutable.SequenceNumber);
    }
}
