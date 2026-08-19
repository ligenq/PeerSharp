using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using PeerSharp.Tests.Core.Peers;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// A peer that sends its handshake and the messages after it in one go.
///
/// <para>
/// This is the normal case, not an edge case: real clients write the handshake, bitfield and extended
/// handshake back to back, and they arrive in a single TCP segment. Reading a fixed 68 bytes for the
/// handshake therefore pulls the start of the next message off the socket with it, and those bytes
/// cannot be put back - whatever reads the message stream next has to be shown them first.
/// </para>
///
/// <para>
/// They were being dropped. The leftover was stashed in the same field that holds the handshake itself,
/// so the call that recorded the handshake overwrote it moments later. The message stream then began
/// part way through a message, and the first length prefix decoded as nonsense. Against a live swarm
/// this closed ten connections in ninety seconds, every one of them after a handshake that had
/// succeeded - visible only as "Invalid negative message length: -1".
/// </para>
/// </summary>
[Collection("Integration")]
public class PipelinedHandshakeTests : IDisposable
{
    private readonly string _path;

    public PipelinedHandshakeTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpPipelined_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
    }

    /// <summary>
    /// Drives a real MSE handshake and then sends the BitTorrent handshake and an Unchoke as one
    /// encrypted write, which is what puts both into the buffer left over from the encryption handshake.
    /// A plaintext connection cannot reproduce this: there the handshake read takes exactly 68 bytes off
    /// the socket and the rest stays in the kernel buffer for the message reader to find.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task MessagesArrivingWithTheEncryptedHandshakeAreNotLost()
    {
        var torrent = TorrentTestUtility.CreateMinimal(downloadPath: _path);
        torrent.Settings.Connection.Encryption = Encryption.Require;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var infoHash = torrent.InfoFile.Info.Hash.ToArray();
        var peerId = new byte[20];
        Random.Shared.NextBytes(peerId);

        var responder = new MseResponder(infoHash);
        var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();

                // Handshake and first message carried in the same write as the end of the MSE
                // handshake, the way a real client sends handshake + bitfield + extended handshake
                // together. This is what lands them both in the leftover buffer.
                var payload = new List<byte> { 19 };
                payload.AddRange("BitTorrent protocol"u8.ToArray());
                payload.AddRange(new byte[8]);
                payload.AddRange(infoHash);
                payload.AddRange(peerId);
                payload.AddRange([0, 0, 0, 1, (byte)MessageId.Unchoke]);

                await responder.AcceptAsync(stream, trailingPayload: payload.ToArray());
                sent.TrySetResult(true);

                // Hold it open so a lost Unchoke shows as a stalled state rather than a disconnect.
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken: TestContext.Current.CancellationToken);
            }
            catch (Exception ex)
            {
                failure.TrySetResult(ex.Message);
                sent.TrySetResult(false);
            }
        });

        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);

        try
        {
            bool connected = await peer.ConnectAsync(
                IPAddress.Loopback.ToString(), port, useUtp: false, timeoutMs: 15000);

            if (failure.Task.IsCompleted)
            {
                Assert.Fail($"The MSE responder failed: {await failure.Task}");
            }

            Assert.True(connected, "The encrypted handshake itself should succeed; this is about what follows it.");
            Assert.True(await sent.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            // The Unchoke shared an encrypted write with the handshake, so it landed in the leftover
            // buffer. If that buffer is dropped, this never arrives and the message stream is meanwhile
            // decoding from the wrong offset.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (peer.PeerChoking && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken: TestContext.Current.CancellationToken);
            }

            Assert.False(
                peer.PeerChoking,
                "The Unchoke that arrived in the same encrypted write as the handshake was never seen. " +
                "The bytes read off the socket alongside the handshake were discarded, so the message " +
                "stream began part way through a message.");
        }
        finally
        {
            await peer.DisposeAsync();
            listener.Stop();
            try { await server; } catch { /* Torn down out from under it. */ }
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }
    }
}
