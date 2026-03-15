using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Peers;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Internals.Network;

internal class PortListener : IPortListener
{
    private readonly ILogger<PortListener> _logger = TorrentLoggerFactory.CreateLogger<PortListener>();
    private readonly ITorrentResolver _resolver;
    private readonly ITcpListenerFactory _tcpFactory;
    private CancellationTokenSource? _acceptCts;
    private AtomicDisposal _disposal = new();
    private bool _running;
    private ITcpListener? _tcpListener;

    public PortListener(ITorrentResolver resolver)
        : this(resolver, new TcpListenerFactory())
    {
    }

    internal PortListener(ITorrentResolver resolver, ITcpListenerFactory tcpFactory)
    {
        _resolver = resolver;
        _tcpFactory = tcpFactory;
    }

    public int Port { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            Stop();
        }
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    public void Start(int port)
    {
        if (_running)
        {
            Stop();
        }

        Port = port;
        try
        {
            _tcpListener = _tcpFactory.Create(IPAddress.Any, port);
            _tcpListener.Start();
            var cts = new CancellationTokenSource();
            _acceptCts = cts;

            // Retrieve actual bound port (useful if port 0 was requested)
            if (_tcpListener.LocalEndpoint is IPEndPoint ep)
            {
                Port = ep.Port;
            }

            _running = true;
            _ = AcceptLoopAsync(cts);
            _logger.LogInformation("Listening on port {Port}", Port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start listener");
            _running = false;
        }
    }

    public void Stop()
    {
        _running = false;
        var cts = Interlocked.Exchange(ref _acceptCts, null);
        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore if cancellation source is already disposed.
            }
        }
        _tcpListener?.Stop();
        _tcpListener?.Dispose();
        _tcpListener = null;
    }

    private async Task AcceptLoopAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            while (_running && _tcpListener != null && !token.IsCancellationRequested)
            {
                try
                {
                    var client = await _tcpListener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    _ = HandleClientAsync(client);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Accept error");
                }
            }
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        bool ownershipTransferred = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stream = client.GetStream();

            // Peek first byte to determine protocol
            byte[] peekBuffer = new byte[1];
            int read = await stream.ReadAsync(peekBuffer, cts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                // Connection closed
                return;
            }

            if (peekBuffer[0] == 19)
            {
                // Plaintext Handshake
                // 1 (19) + 19 (Protocol) + 8 (Reserved) + 20 (InfoHash) + 20 (PeerId) = 68
                byte[] buffer = new byte[68];
                buffer[0] = 19;
                read = 1;

                while (read < 68)
                {
                    int r = await stream.ReadAsync(buffer.AsMemory(read, 68 - read), cts.Token).ConfigureAwait(false);
                    if (r == 0)
                    {
                        throw new InvalidDataException("Connection closed");
                    }

                    read += r;
                }

                // Extract InfoHash
                var infoHash = new InfoHash(buffer.AsSpan(28, 20));

                var torrent = _resolver.GetTorrent(infoHash);
                if (torrent is Torrent t)
                {
                    await t.PeersInternal.AddIncomingPeerAsync(client, buffer).ConfigureAwait(false);
                    ownershipTransferred = true;
                }
            }
            else
            {
                // Encrypted Handshake
                ownershipTransferred = await HandleEncryptedClientAsync(client, peekBuffer, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Handshake error");
        }
        finally
        {
            if (!ownershipTransferred)
            {
                client.Dispose();
            }
        }
    }

    private async Task<bool> HandleEncryptedClientAsync(TcpClient client, byte[] initialBytes, CancellationToken token)
    {
        var pe = new ProtocolEncryptionHandshake(_resolver);
        var stream = client.GetStream();

        try
        {
            // Feed initial bytes
            var response = pe.HandleIncoming(initialBytes);
            if (response.Length > 0)
            {
                await stream.WriteAsync(response, token).ConfigureAwait(false);
            }

            byte[] buffer = new byte[4096];
            while (!pe.IsComplete && !pe.IsError)
            {
                int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                var data = buffer.AsSpan(0, read).ToArray();
                response = pe.HandleIncoming(data);
                if (response.Length > 0)
                {
                    await stream.WriteAsync(response, token).ConfigureAwait(false);
                }
            }

            if (pe.IsComplete && pe.MatchedInfoHash != null && pe.Encryption != null)
            {
                var infoHash = new InfoHash(pe.MatchedInfoHash);
                var torrent = _resolver.GetTorrent(infoHash);
                if (torrent is Torrent t)
                {
                    await t.PeersInternal.AddIncomingPeerAsync(client, pe.ReceivedPayload ?? Array.Empty<byte>(), pe.Encryption).ConfigureAwait(false);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Encryption handshake error");
        }
        finally
        {
            pe.Dispose();
        }
        return false;
    }
}
