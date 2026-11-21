using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Peers;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Integration;

/// <summary>
/// Falling back to plaintext after the encryption handshake fails.
///
/// <para>
/// In <see cref="Encryption.Allow"/> mode a peer that cannot or will not speak MSE should still be
/// reachable in plaintext - but not by redialling it inside the same attempt. A peer that hangs up
/// mid-handshake has told us nothing about encryption: peers hang up because they are at their
/// connection limit, do not have the torrent, or already have us, and dialling straight back meets the
/// same reason again. Measured against a live swarm, that immediate retry failed 72 times out of 77,
/// one peer was redialled fifteen times, and a peer diagnosed as not supporting encryption completed an
/// encrypted handshake with us minutes later.
/// </para>
///
/// <para>
/// So the choice is made per peer instead of per attempt, following libtorrent: it flips a
/// <c>pe_support</c> flag on the peer before dialling and flips it back when a handshake completes, so
/// a peer that refuses one form is offered the other next time. Transmission is stricter still and does
/// not retry at all, marking a peer that sent nothing back as unconnectable. Neither reconnects within
/// an attempt.
/// </para>
///
/// <para>
/// An earlier version of this file asserted the opposite, on the strength of a narrower measurement
/// showing the fallback reusing an already-dead socket. That was a real bug and the fix was to
/// reconnect; the wider measurement showed the retry itself was the mistake. What survives of the
/// earlier fix is that a broken connection is reported as failed rather than quietly retried on a
/// socket that could never have worked.
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

    /// <summary>
    /// One connection per attempt: the peer is not redialled inside <c>ConnectAsync</c>.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PeerThatAbortsDuringEncryption_IsNotImmediatelyRedialled()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accepted = new List<TcpClient>();
        int connectionCount = 0;

        // Accept connections and abort each part way through the MSE handshake, the way a peer at its
        // connection limit or one that has just gone away behaves.
        var acceptLoop = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return; // Listener stopped.
                }

                lock (accepted)
                {
                    accepted.Add(client);
                }
                Interlocked.Increment(ref connectionCount);

                var buffer = new byte[96];
                try { _ = await client.GetStream().ReadAsync(buffer); } catch { /* Abort races the read. */ }

                client.Client.LingerState = new LingerOption(true, 0);
                client.Close();
            }
        });

        var torrent = TorrentTestUtility.CreateMinimal(downloadPath: _path);
        torrent.Settings.Connection.Encryption = Encryption.Allow;

        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);

        try
        {
            bool connected = await peer.ConnectAsync(IPAddress.Loopback.ToString(), port, useUtp: false, timeoutMs: 5000);

            Assert.False(connected, "The stub never completes a handshake, so the attempt must report failure.");

            // Give any stray reconnect time to arrive before concluding there was not one.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(
                Volatile.Read(ref connectionCount) == 1,
                $"The peer was dialled {Volatile.Read(ref connectionCount)} times for one connection attempt. " +
                "Retrying inside the attempt is what this design removed - the peer's history decides what to " +
                "offer on the next attempt instead.");
        }
        finally
        {
            await peer.DisposeAsync();
            listener.Stop();
            lock (accepted)
            {
                foreach (var client in accepted)
                {
                    client.Dispose();
                }
            }

            try { await acceptLoop; } catch { /* The listener is stopped out from under it. */ }
        }
    }

    /// <summary>
    /// Asking for plaintext skips the encryption handshake. Without this, alternating would be
    /// pointless because every attempt would still open with MSE.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task OfferingPlaintextSkipsTheEncryptionHandshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var opening = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var buffer = new byte[20];
                int read = await client.GetStream().ReadAsync(buffer);
                opening.TrySetResult(buffer[..read]);
            }
            catch
            {
                opening.TrySetCanceled();
            }
        });

        var torrent = TorrentTestUtility.CreateMinimal(downloadPath: _path);
        torrent.Settings.Connection.Encryption = Encryption.Allow;

        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);

        try
        {
            _ = peer.ConnectAsync(
                IPAddress.Loopback.ToString(), port, useUtp: false, timeoutMs: 5000, offerEncryption: false);

            var completed = await Task.WhenAny(opening.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(completed == opening.Task, "Nothing was sent to the listener.");

            // A plaintext handshake opens with the length-prefixed protocol name. An MSE handshake opens
            // with a random Diffie-Hellman key, which will not spell this out.
            var opened = await opening.Task;
            Assert.True(opened.Length >= 20, $"Only {opened.Length} bytes were sent.");
            Assert.Equal(19, opened[0]);
            Assert.Equal("BitTorrent protocol", System.Text.Encoding.ASCII.GetString(opened, 1, 19));
        }
        finally
        {
            await peer.DisposeAsync();
            listener.Stop();
            try { await acceptLoop; } catch { /* The listener is stopped out from under it. */ }
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ProxyAuthenticationFailureBeforePeerConnectionIsLoggedAsError()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sink = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(sink);
        });
        var torrent = TorrentTestUtility.CreateMinimal(downloadPath: _path);
        torrent.Settings.Proxy.Type = ProxyType.Http;
        torrent.Settings.Proxy.Host = IPAddress.Loopback.ToString();
        torrent.Settings.Proxy.Port = (ushort)port;
        torrent.Settings.Proxy.ProxyPeers = true;

        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System, loggerFactory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var rejectProxy = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation.Token))) { }
            await stream.WriteAsync(
                "HTTP/1.1 407 Proxy Authentication Required\r\n\r\n"u8.ToArray(),
                cancellation.Token);
        }, cancellation.Token);

        try
        {
            bool connected = await peer.ConnectAsync("192.0.2.1", 6881, useUtp: false, timeoutMs: 5000, ct: cancellation.Token);
            await rejectProxy;

            Assert.False(connected);
            Assert.Contains(sink.Entries, entry =>
                entry.Level == LogLevel.Error
                && entry.Exception is IOException ex
                && ex.Message.Contains("HTTP Proxy connection failed", StringComparison.Ordinal));
        }
        finally
        {
            await cancellation.CancelAsync();
            await peer.DisposeAsync();
        }
    }

    /// <summary>
    /// A peer that accepts a connection and never answers our MSE Pe1 times out inside the
    /// encryption handshake (which aborts and disposes the transport). ConnectAsync must not
    /// fall through to PerformPlaintextHandshakeAsync on that disposed stream and log an
    /// unexpected IOException error.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PeerThatIgnoresEncryptionHandshake_TimesOutWithoutUnexpectedErrorLog()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var stopPeer = new CancellationTokenSource();
        var accepted = new List<TcpClient>();
        int connectionCount = 0;

        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stopPeer.Token);
                lock (accepted)
                {
                    accepted.Add(client);
                }
                Interlocked.Increment(ref connectionCount);

                // Drain Pe1 and stay silent until the test ends.
                var buffer = new byte[256];
                try { _ = await client.GetStream().ReadAsync(buffer, stopPeer.Token); } catch { /* Test teardown. */ }
                try { await Task.Delay(Timeout.InfiniteTimeSpan, stopPeer.Token); } catch { /* Test teardown. */ }
            }
            catch
            {
                // Listener stopped.
            }
        });

        var sink = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(sink);
        });

        var torrent = TorrentTestUtility.CreateMinimal(downloadPath: _path);
        torrent.Settings.Connection.Encryption = Encryption.Allow;

        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System, loggerFactory);

        try
        {
            // Longer than the 5s first-read MSE timeout so ReadHandshakeBytesAsync aborts the
            // transport before the outer connect timeout fires.
            bool connected = await peer.ConnectAsync(IPAddress.Loopback.ToString(), port, useUtp: false, timeoutMs: 12000);

            Assert.False(connected);
            Assert.True(peer.OfferedAnEncryptionChoice);
            Assert.False(peer.HungUpDuringEncryptionHandshake);
            Assert.Equal(1, Volatile.Read(ref connectionCount));
            Assert.DoesNotContain(sink.Entries, entry => entry.Level >= LogLevel.Warning);
        }
        finally
        {
            await stopPeer.CancelAsync();
            await peer.DisposeAsync();
            listener.Stop();
            lock (accepted)
            {
                foreach (var client in accepted)
                {
                    client.Dispose();
                }
            }

            try { await acceptLoop; } catch { /* Listener stopped. */ }
        }
    }

    /// <summary>
    /// When the outer dial budget expires during an MSE handshake before the inner 5s read timer,
    /// the peer did not hang up - our own timeout aborted the transport. It must neither log an
    /// unexpected error nor claim HungUpDuringEncryptionHandshake.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PeerThatExceedsConnectTimeoutDuringEncryptionHandshake_DoesNotClaimHangUpOrLogError()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var stopPeer = new CancellationTokenSource();
        var accepted = new List<TcpClient>();

        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stopPeer.Token);
                lock (accepted)
                {
                    accepted.Add(client);
                }

                var buffer = new byte[256];
                try { _ = await client.GetStream().ReadAsync(buffer, stopPeer.Token); } catch { /* Test teardown. */ }
                try { await Task.Delay(Timeout.InfiniteTimeSpan, stopPeer.Token); } catch { /* Test teardown. */ }
            }
            catch
            {
                // Listener stopped.
            }
        });

        var sink = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(sink);
        });

        var torrent = TorrentTestUtility.CreateMinimal(downloadPath: _path);
        torrent.Settings.Connection.Encryption = Encryption.Allow;

        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System, loggerFactory);

        try
        {
            bool connected = await peer.ConnectAsync(IPAddress.Loopback.ToString(), port, useUtp: false, timeoutMs: 1000);

            Assert.False(connected);
            Assert.False(peer.HungUpDuringEncryptionHandshake);
            Assert.DoesNotContain(sink.Entries, entry => entry.Level >= LogLevel.Warning);
        }
        finally
        {
            await stopPeer.CancelAsync();
            await peer.DisposeAsync();
            listener.Stop();
            lock (accepted)
            {
                foreach (var client in accepted)
                {
                    client.Dispose();
                }
            }

            try { await acceptLoop; } catch { /* Listener stopped. */ }
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
