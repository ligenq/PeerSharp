using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// Falling back to plaintext after the encryption handshake fails.
///
/// <para>
/// In <see cref="Encryption.Allow"/> mode a peer that cannot or will not speak MSE should still be
/// reachable in plaintext. That only works if the fallback happens on a <em>fresh</em> connection: by
/// the time the encryption handshake fails we have already written MSE bytes to the socket, so it is
/// either dead or desynchronised, and reusing it guarantees failure.
/// </para>
///
/// <para>
/// Measured against a live swarm, this was losing roughly 3% of all connection attempts - peers that
/// would have connected in plaintext, discarded because an I/O error mid-handshake was classified as
/// "failed" rather than "connection gone".
/// </para>
/// </summary>
[Collection("Integration")]
public class EncryptionFallbackTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _path;

    public EncryptionFallbackTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        _path = Path.Combine(Path.GetTempPath(), "PeerSharpEncFallback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
    }

    [Fact(Timeout = 60000)]
    public async Task PeerThatAbortsDuringEncryption_IsRetriedOnAFreshConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accepted = new List<TcpClient>();
        var secondConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Accept connections and abort each one part way through the MSE handshake, the way a peer
        // behind a middlebox or one that has just gone away behaves.
        var acceptLoop = Task.Run(async () =>
        {
            while (accepted.Count < 2)
            {
                var client = await listener.AcceptTcpClientAsync();
                accepted.Add(client);

                if (accepted.Count == 2)
                {
                    secondConnection.TrySetResult(true);
                }

                // Read whatever the initiator sent, then reset rather than close cleanly, so the peer
                // sees an I/O error rather than a graceful zero-length read.
                var buffer = new byte[96];
                try { await client.GetStream().ReadAsync(buffer); } catch { /* Abort races the read. */ }

                client.Client.LingerState = new LingerOption(true, 0);
                client.Close();
            }
        });

        var torrent = TorrentTestUtility.CreateMinimal(downloadPath: _path);
        torrent.Settings.Connection.Encryption = Encryption.Allow;

        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);

        try
        {
            // Expected to fail overall - the stub never completes a handshake. What matters is that a
            // second connection was attempted at all.
            await peer.ConnectAsync(IPAddress.Loopback.ToString(), port, useUtp: false, timeoutMs: 5000);

            var reconnected = await Task.WhenAny(secondConnection.Task, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.True(
                reconnected == secondConnection.Task,
                "Only one connection was attempted. After the encryption handshake died the plaintext " +
                "fallback reused the broken socket instead of reconnecting, so the peer was lost.");
        }
        finally
        {
            await peer.DisposeAsync();
            listener.Stop();
            foreach (var client in accepted)
            {
                client.Dispose();
            }

            try { await acceptLoop; } catch { /* The listener is stopped out from under it. */ }
            await torrent.DisposeAsync();
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _loggerFactory.Dispose();
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

    private sealed class NullPeerListener : IPeerListener
    {
        public Task ConnectionClosedAsync(Internals.Extensions.IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(Internals.Extensions.IPeerCommunication peer, Internals.Extensions.ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(Internals.Extensions.IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task HandshakeFinishedAsync(Internals.Extensions.IPeerCommunication peer) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(Internals.Extensions.IPeerCommunication peer, Internals.Extensions.UtHolepunch.MsgId id, IPEndPoint endpoint, Internals.Extensions.UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task MessageReceivedAsync(Internals.Extensions.IPeerCommunication peer, Messages.PeerMessage msg) => Task.CompletedTask;
        public Task PexReceivedAsync(Internals.Extensions.IPeerCommunication peer, List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped) => Task.CompletedTask;
        public Task PortReceivedAsync(Internals.Extensions.IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}
