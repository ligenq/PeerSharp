using System.Buffers.Binary;
using System.Net;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>
/// The BEP 9, BEP 10 and BEP 21 claims the synthetic peer makes, written once and run against more
/// than one engine.
///
/// <para>
/// The synthetic peer's weakness is that it is my reading of the specifications. If that reading is
/// wrong, PeerSharp is held to an invented standard, the tests pass, and the engine is still broken
/// against everything else - which is the same failure the synthetic peer was built to catch, moved up
/// one level. Nothing about writing the peer independently of PeerSharp fixes that; independence buys
/// a second opinion, not a correct one.
/// </para>
///
/// <para>
/// So these assertions live apart from the tests that call them, and libtorrent is put through exactly
/// the same ones. A reference implementation passing them is evidence that they describe conformant
/// behaviour. A reference implementation failing one is evidence that the expectation is wrong, and
/// the finding is then about this file rather than about PeerSharp.
/// </para>
/// </summary>
internal static class ExtensionProtocolConformance
{
    /// <summary>
    /// BEP 21: a peer that has no metadata yet cannot be upload-only, because it wants the metadata.
    /// Saying otherwise invites a conformant peer to disconnect as redundant.
    /// </summary>
    public static void AssertNoUploadOnlyBeforeMetadata(
        IReadOnlyDictionary<string, object> handshake, string engine, bool isReference)
    {
        long uploadOnly = SyntheticBencode.TryGetInteger(handshake, "upload_only") ?? 0;

        Assert.True(
            uploadOnly == 0,
            $"{engine} advertised upload_only in its extension handshake while it still had no metadata. " +
            "A peer honouring BEP 21 reads that as nothing to gain from the connection and drops it, taking " +
            "the metadata request with it. " +
            Consequence(isReference));
    }

    /// <summary>
    /// BEP 10: extension ids are chosen by the receiver, so a message must carry the id its recipient
    /// published. An id from the sender's own numbering means something else, or nothing.
    /// </summary>
    public static void AssertOnlyPublishedExtensionIdsAreAddressed(
        SyntheticConnection connection, IReadOnlyCollection<byte> published, string engine, bool isReference)
    {
        var misaddressed = connection.ExtendedFrames
            .Where(frame => !published.Contains(frame.ExtendedId))
            .ToArray();

        Assert.True(
            misaddressed.Length == 0,
            $"{engine} addressed an extension message to id(s) " +
            $"{string.Join(", ", misaddressed.Select(static frame => frame.ExtendedId).Distinct())}, which this " +
            $"peer never published. BEP 10 numbers extensions per receiver, so that id means something else " +
            $"here - or nothing at all. " +
            Consequence(isReference) +
            $" Traffic: {connection.Describe()}");
    }

    /// <summary>
    /// BEP 9: a metadata request is a bencoded dictionary containing request type zero and a piece
    /// index inside the 16 KiB metadata piece space. BEP 10 additionally requires every such request
    /// to use the id this receiver assigned to <c>ut_metadata</c>.
    /// </summary>
    public static void AssertValidMetadataRequests(
        SyntheticConnection connection,
        byte utMetadataId,
        int metadataSize,
        string engine,
        bool isReference)
    {
        int pieceCount = (metadataSize + 16 * 1024 - 1) / (16 * 1024);
        var requests = new List<Dictionary<string, object>>();

        foreach (WireFrame frame in connection.ExtendedFrames.Where(static frame => frame.ExtendedId != 0))
        {
            Dictionary<string, object> body;
            try
            {
                body = SyntheticBencode.DecodeDictionary(frame.ExtendedPayload, $"{engine}'s extended message");
            }
            catch (InvalidOperationException) when (frame.ExtendedId != utMetadataId)
            {
                // Other extensions need not use bencode. A frame addressed as ut_metadata does.
                continue;
            }

            bool looksLikeMetadata = body.ContainsKey("msg_type") || body.ContainsKey("piece");
            if (frame.ExtendedId != utMetadataId && !looksLikeMetadata)
            {
                continue;
            }

            Assert.True(
                frame.ExtendedId == utMetadataId,
                $"{engine} sent a metadata-shaped message under extension id {frame.ExtendedId}, but this " +
                $"peer assigned ut_metadata id {utMetadataId}. " + Consequence(isReference) +
                $" Traffic: {connection.Describe()}");

            requests.Add(body);
        }

        Assert.True(
            requests.Count > 0,
            $"{engine} never sent a metadata request under the peer's ut_metadata id {utMetadataId}. " +
            Consequence(isReference) + $" Traffic: {connection.Describe()}");

        foreach (Dictionary<string, object> body in requests)
        {
            long? messageType = SyntheticBencode.TryGetInteger(body, "msg_type");
            long? piece = SyntheticBencode.TryGetInteger(body, "piece");

            Assert.True(
                messageType == 0,
                $"{engine} sent ut_metadata msg_type {messageType?.ToString() ?? "(missing)"} while it had no " +
                $"metadata; BEP 9 requires a request (msg_type 0). " + Consequence(isReference));
            Assert.True(
                piece is >= 0 && piece < pieceCount,
                $"{engine} requested metadata piece {piece?.ToString() ?? "(missing)"}, outside the {pieceCount} " +
                $"piece(s) implied by metadata_size {metadataSize}. " + Consequence(isReference));
            Assert.True(
                body.Count == 2,
                $"{engine}'s BEP 9 request contained keys [{string.Join(", ", body.Keys)}]; a request contains " +
                $"only msg_type and piece. " + Consequence(isReference));
        }
    }

    /// <summary>BEP 10: id zero disables an extension and must not produce BEP 9 requests.</summary>
    public static void AssertNoMetadataRequests(
        SyntheticConnection connection, string engine, bool isReference)
    {
        var requests = new List<WireFrame>();
        foreach (WireFrame frame in connection.ExtendedFrames)
        {
            try
            {
                Dictionary<string, object> body = SyntheticBencode.DecodeDictionary(
                    frame.ExtendedPayload, $"{engine}'s extended message");
                if (body.ContainsKey("msg_type") || body.ContainsKey("piece"))
                {
                    requests.Add(frame);
                }
            }
            catch (InvalidOperationException)
            {
                // A non-bencoded extension cannot be a BEP 9 request.
            }
        }

        Assert.True(
            requests.Count == 0,
            $"{engine} sent {requests.Count} metadata-shaped message(s) after this peer disabled ut_metadata " +
            $"with id zero. BEP 10 reserves zero for the extension handshake. " + Consequence(isReference) +
            $" Traffic: {connection.Describe()}");
    }

    /// <summary>
    /// BEP 11: an IPv4 PEX announcement is a six-byte compact endpoint in <c>added</c>, with one
    /// corresponding flag byte, sent under the ut_pex id chosen by its receiver.
    /// </summary>
    public static void AssertPexIntroduces(
        SyntheticConnection connection,
        byte utPexId,
        IPEndPoint expected,
        string engine,
        bool isReference)
    {
        var pexMessages = new List<(WireFrame Frame, Dictionary<string, object> Body)>();
        foreach (WireFrame frame in connection.ExtendedFrames.Where(static frame => frame.ExtendedId != 0))
        {
            Dictionary<string, object> body;
            try
            {
                body = SyntheticBencode.DecodeDictionary(frame.ExtendedPayload, $"{engine}'s extended message");
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (body.ContainsKey("added") || body.ContainsKey("added6") || body.ContainsKey("dropped"))
            {
                pexMessages.Add((frame, body));
            }
        }

        Assert.True(
            pexMessages.Count > 0,
            $"{engine} never sent a PEX announcement. " + Consequence(isReference) +
            $" Traffic: {connection.Describe()}");
        Assert.All(pexMessages, message => Assert.True(
            message.Frame.ExtendedId == utPexId,
            $"{engine} sent PEX under extension id {message.Frame.ExtendedId}, but this peer assigned " +
            $"ut_pex id {utPexId}. " + Consequence(isReference)));

        byte[] expectedAddress = expected.Address.MapToIPv4().GetAddressBytes();
        bool introduced = false;
        foreach ((_, Dictionary<string, object> body) in pexMessages)
        {
            if (!body.TryGetValue("added", out object? value) || value is not byte[] added)
            {
                continue;
            }

            Assert.True(
                added.Length % 6 == 0,
                $"{engine}'s BEP 11 added string was {added.Length} bytes, not a sequence of six-byte IPv4 endpoints. " +
                Consequence(isReference));

            if (body.TryGetValue("added.f", out object? flagsValue))
            {
                Assert.True(
                    flagsValue is byte[] flags && flags.Length == added.Length / 6,
                    $"{engine}'s BEP 11 added.f flags did not contain one byte per added endpoint. " +
                    Consequence(isReference));
            }

            for (int offset = 0; offset < added.Length; offset += 6)
            {
                if (added.AsSpan(offset, 4).SequenceEqual(expectedAddress) &&
                    BinaryPrimitives.ReadUInt16BigEndian(added.AsSpan(offset + 4, 2)) == expected.Port)
                {
                    introduced = true;
                }
            }
        }

        Assert.True(
            introduced,
            $"{engine}'s PEX messages never introduced {expected}. " + Consequence(isReference) +
            $" Traffic: {connection.Describe()}");
    }

    /// <summary>
    /// Says what a failure implies, which is the opposite thing depending on who was being measured.
    /// </summary>
    private static string Consequence(bool isReference) => isReference
        ? "This is the reference implementation, so a failure here means the expectation encoded in this " +
          "file is wrong and the matching PeerSharp test is enforcing an invention."
        : "";
}
