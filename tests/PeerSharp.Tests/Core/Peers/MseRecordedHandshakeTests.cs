using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using System.Text;
using System.Text.Json;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Replays an MSE handshake recorded against qBittorrent.
/// </summary>
/// <remarks>
/// <para>
/// Every other test of this encryption drives our initiator against our own responder. That shows
/// the two halves agree with each other, which they would continue to do if both drifted the same
/// way; it cannot show that either agrees with anything else. This repository has already paid for
/// that distinction more than once - a late bitfield, a plaintext fallback reusing a dead socket, an
/// inbound uTP path rejecting every encrypted peer - all invisible to testing against ourselves,
/// because our own parser was tolerant of exactly what our own writer produced.
/// </para>
/// <para>
/// So these bytes were produced by qBittorrent, not by us. Replaying them needs the private key from
/// the recording, since the shared secret and every key derived from it follow from that and the
/// far side's recorded public key - hence the fixed key in the fixture. The proof that the whole
/// derivation is right is at the end: qBittorrent's BitTorrent handshake comes out of the decrypted
/// stream intact, which cannot happen unless the shared secret, both RC4 keys, the discard of the
/// first 1024 bytes and the stream position all match what qBittorrent did.
/// </para>
/// <para>
/// Regenerate with <c>PEERSHARP_MSE_CAPTURE=1</c> against the capture test in the Interop directory.
/// </para>
/// </remarks>
public class MseRecordedHandshakeTests
{
    [Fact]
    public void AHandshakeRecordedFromQBittorrentStillCompletes()
    {
        var recording = Recording.Load();

        using var handshake = new ProtocolEncryptionHandshake(
            recording.InfoHash,
            initiator: true,
            new DiffieHellman(recording.PrivateKey))
        {
            InitialPayload = BuildBitTorrentHandshake(recording.InfoHash, recording.OurPeerId)
        };

        // Sent, but not compared against the recording: the padding lengths and contents are random
        // on every run, so our outgoing bytes are not reproducible and are not what is under test.
        // What the far side replied to was our public key, which the fixed private key reproduces.
        byte[] initiate = handshake.Initiate();
        Assert.True(initiate.Length >= 96);

        var plaintext = new List<byte>();
        foreach (byte[] chunk in recording.Received)
        {
            byte[] copy = chunk.ToArray();

            if (!handshake.IsComplete)
            {
                handshake.HandleIncoming(copy);

                // Whatever the handshake did not consume is already stream data.
                if (handshake.IsComplete)
                {
                    byte[] trailing = handshake.TrailingData;
                    if (trailing.Length > 0)
                    {
                        handshake.Encryption!.RC4In.Decrypt(trailing);
                        plaintext.AddRange(trailing);
                    }
                }

                continue;
            }

            handshake.Encryption!.RC4In.Decrypt(copy);
            plaintext.AddRange(copy);
        }

        Assert.False(handshake.IsError, "the recorded qBittorrent handshake was rejected");
        Assert.True(handshake.IsComplete, "the recorded qBittorrent handshake did not complete");
        Assert.NotNull(handshake.Encryption);

        // The decrypted stream has to start with qBittorrent's BitTorrent handshake, byte for byte
        // as recorded. Any error in the shared secret, either RC4 key, the 1024-byte discard or the
        // stream position turns this into noise rather than into a different valid message.
        Assert.True(plaintext.Count >= 68, $"only {plaintext.Count} bytes decrypted");
        Assert.Equal(recording.TheirHandshake, plaintext.Take(68));
    }

    [Fact]
    public void TheRecordedPeerIsQBittorrentAndAgreesOnTheTorrent()
    {
        // Reading the fixture as data rather than as bytes: if a future recording were made against
        // the wrong client, or for the wrong torrent, the replay above would still pass.
        var recording = Recording.Load();
        byte[] theirs = recording.TheirHandshake;

        Assert.Equal(19, theirs[0]);
        Assert.Equal("BitTorrent protocol", Encoding.ASCII.GetString(theirs, 1, 19));
        Assert.Equal(recording.InfoHash, theirs[28..48]);
        Assert.StartsWith("-qB", Encoding.ASCII.GetString(theirs, 48, 20), StringComparison.Ordinal);

        // qBittorrent advertises the extension protocol in the reserved bytes; losing that would
        // mean the recording no longer covers a peer worth talking to.
        Assert.Equal(0x10, theirs[25] & 0x10);
    }

    private static byte[] BuildBitTorrentHandshake(byte[] infoHash, byte[] peerId)
    {
        byte[] message = new byte[68];
        message[0] = 19;
        "BitTorrent protocol"u8.CopyTo(message.AsSpan(1));
        message[25] = 0x10;
        infoHash.CopyTo(message.AsSpan(28));
        peerId.CopyTo(message.AsSpan(48));
        return message;
    }

    private sealed record Recording(
        byte[] PrivateKey,
        byte[] InfoHash,
        byte[] OurPeerId,
        byte[][] Received,
        byte[] TheirHandshake)
    {
        public static Recording Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Core", "Peers", "Fixtures", "qbittorrent-mse-handshake.json");
            Assert.True(File.Exists(path), $"recording not found at {path}");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            return new Recording(
                Convert.FromHexString(root.GetProperty("PrivateKey").GetString()!),
                Convert.FromHexString(root.GetProperty("InfoHash").GetString()!),
                Convert.FromHexString(root.GetProperty("OurPeerId").GetString()!),
                [.. root.GetProperty("Received").EnumerateArray().Select(e => Convert.FromHexString(e.GetString()!))],
                Convert.FromHexString(root.GetProperty("TheirHandshake").GetString()!));
        }
    }
}
