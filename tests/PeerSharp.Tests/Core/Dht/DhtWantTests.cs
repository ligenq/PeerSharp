using System.Net;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Dht;

namespace PeerSharp.Tests.Core.Dht;

/// <summary>
/// BEP 32 <c>want</c>, over the wire.
///
/// <para>
/// A node answering a query may return only the address family the query arrived over unless the
/// querier asks for more. Both halves of that matter: without asking, a client on a v4 socket can
/// simply never be handed IPv6 nodes and its v6 routing table never fills; without honouring it, we
/// send families the other end did not ask for and will discard.
/// </para>
/// </summary>
public class DhtWantTests
{
    [Fact]
    public async Task OutgoingQueries_AskForBothFamilies()
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();

        // Capture what the client puts on the wire rather than what the server makes of it.
        var sent = new List<BDict>();
        fixture.ClientTransport.OnSend = data => sent.Add(Decode(data));

        await fixture.Client.FindNodeAsync(
            fixture.ServerEndPoint,
            new DhtTarget(InfoHash.CreateRandom().Span));

        var query = Assert.Single(sent, d => Query(d) == "find_node");
        var want = Assert.IsType<BList>(Args(query).Get("want"));
        var families = want.List.OfType<BString>()
            .Select(v => System.Text.Encoding.ASCII.GetString(v.Value.Span))
            .ToList();

        Assert.Contains("n4", families);
    }

    [Theory]
    [InlineData("n4", true, false)]
    [InlineData("n6", false, true)]
    public async Task AResponseCarriesOnlyTheFamiliesAsked(string family, bool expectNodes, bool expectNodes6)
    {
        await using var fixture = await DhtLoopbackFixture.CreateAsync();

        var responses = new List<BDict>();
        fixture.ServerTransport.OnSend = data => responses.Add(Decode(data));

        var want = new BList();
        want.List.Add(new BString(System.Text.Encoding.ASCII.GetBytes(family)));

        var args = new BDict();
        args.Dict["id"] = new BString(InfoHash.CreateRandom().Span.ToArray());
        args.Dict["target"] = new BString(InfoHash.CreateRandom().Span.ToArray());
        args.Dict["want"] = want;

        var query = new BDict();
        query.Dict["t"] = new BString("aa"u8.ToArray());
        query.Dict["y"] = new BString("q"u8.ToArray());
        query.Dict["q"] = new BString("find_node"u8.ToArray());
        query.Dict["a"] = args;

        await fixture.ClientTransport.SendAsync(BencodeWriter.Write(query), fixture.ServerEndPoint);

        var reply = Assert.Single(responses);
        var r = Assert.IsType<BDict>(reply.Get("r"));

        // The server may legitimately have no nodes of a family to offer, so the assertion that
        // carries weight is the negative one: a family that was not asked for must never appear.
        if (!expectNodes)
        {
            Assert.Null(r.Get("nodes"));
        }

        if (!expectNodes6)
        {
            Assert.Null(r.Get("nodes6"));
        }
    }

    private static BDict Decode(ReadOnlyMemory<byte> data)
        => Assert.IsType<BDict>(BencodeParser.Parse(data.ToArray()));

    private static string Query(BDict dict)
        => dict.Get("q") is BString q ? System.Text.Encoding.ASCII.GetString(q.Value.Span) : string.Empty;

    private static BDict Args(BDict dict)
        => Assert.IsType<BDict>(dict.Get("a"));
}
