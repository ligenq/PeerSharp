using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Peers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.Internals.Network;

/// <summary>
/// Implements Local Peer Discovery (LSD/BEP 14).
/// Uses UDP multicast to find peers on the same local network.
/// </summary>
internal class LsdManager : ILsdManager
{
    private const int LsdPort = 6771;
    private const string MulticastIpV4 = "239.192.152.143";
    private const string MulticastIpV6 = "ff15::efc0:988f";
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromMinutes(5);
    private static readonly string[] LineSeparators = ["\r\n", "\n"];
    private readonly string _cookie;
    private readonly ILogger<LsdManager> _logger;
    private readonly ITorrentResolver _resolver;
    private readonly Settings _settings;
    private readonly IUdpSocketFactory _socketFactory;
    private readonly TimeProvider _timeProvider;
    private ITimer? _announceTimer;
    private CancellationTokenSource? _cts;
    private AtomicDisposal _disposal = new();
    private IUdpSocket? _ipv4Client;
    private IUdpSocket? _ipv6Client;
    private bool _running;

    public LsdManager(Settings settings, ITorrentResolver resolver, TimeProvider timeProvider)
        : this(settings, resolver, timeProvider, new UdpSocketFactory(), NullLoggerFactory.Instance)
    {
    }

    public LsdManager(Settings settings, ITorrentResolver resolver, TimeProvider timeProvider, ILoggerFactory loggerFactory)
        : this(settings, resolver, timeProvider, new UdpSocketFactory(), loggerFactory)
    {
    }

    internal LsdManager(Settings settings, ITorrentResolver resolver, TimeProvider timeProvider, IUdpSocketFactory socketFactory)
        : this(settings, resolver, timeProvider, socketFactory, NullLoggerFactory.Instance)
    {
    }

    internal LsdManager(Settings settings, ITorrentResolver resolver, TimeProvider timeProvider, IUdpSocketFactory socketFactory, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _resolver = resolver;
        _timeProvider = timeProvider;
        _socketFactory = socketFactory;
        _logger = loggerFactory.CreateLogger<LsdManager>();
        _cookie = Guid.NewGuid().ToString("N")[..8]; // Opaque string to identify ourselves
    }

    public async Task AnnounceAsync(InfoHash infoHash, CancellationToken token = default)
    {
        if (!_running)
        {
            return;
        }

        try
        {
            if (_ipv4Client != null)
            {
                var message = BuildAnnounceMessage(infoHash, MulticastIpV4);
                var data = Encoding.ASCII.GetBytes(message);
                var endpoint = new IPEndPoint(IPAddress.Parse(MulticastIpV4), LsdPort);
                await _ipv4Client.SendAsync(data, endpoint, token).ConfigureAwait(false);
            }

            if (_ipv6Client != null)
            {
                var message = BuildAnnounceMessage(infoHash, $"[{MulticastIpV6}]");
                var data = Encoding.ASCII.GetBytes(message);
                var endpoint = new IPEndPoint(IPAddress.Parse(MulticastIpV6), LsdPort);
                await _ipv6Client.SendAsync(data, endpoint, token).ConfigureAwait(false);
            }

            _logger.LogTrace("Sent LSD announce for {Hash}", infoHash);
        }
        catch (OperationCanceledException) { /* Expected */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSD announce failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            Stop();
        }
        await ValueTask.CompletedTask.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void Start()
    {
        if (!_settings.Connection.EnableLsd || _running)
        {
            return;
        }

        try
        {
            _running = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            // Setup IPv4 Multicast Socket
            try
            {
                _ipv4Client = _socketFactory.Create(AddressFamily.InterNetwork);
                _ipv4Client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _ipv4Client.Client.Bind(new IPEndPoint(IPAddress.Any, LsdPort));
                _ipv4Client.JoinMulticastGroup(IPAddress.Parse(MulticastIpV4));
                _ipv4Client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

                _ = ReceiveLoopAsync(_ipv4Client, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to setup IPv4 LSD");
            }

            // Setup IPv6 Multicast Socket
            try
            {
                _ipv6Client = _socketFactory.Create(AddressFamily.InterNetworkV6);
                _ipv6Client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _ipv6Client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                _ipv6Client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, LsdPort));
                _ipv6Client.JoinMulticastGroup(IPAddress.Parse(MulticastIpV6));
                _ipv6Client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);

                _ = ReceiveLoopAsync(_ipv6Client, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to setup IPv6 LSD");
            }

            if (_ipv4Client == null && _ipv6Client == null)
            {
                throw new IOException("Failed to bind both IPv4 and IPv6 LSD sockets");
            }

            _announceTimer = _timeProvider.CreateTimer(OnAnnounceTick, null, TimeSpan.FromSeconds(5), AnnounceInterval);

            _logger.LogInformation("LSD Manager started on port {Port}, cookie {Cookie}", LsdPort, _cookie);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start LSD Manager");
            Stop();
        }
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _announceTimer?.Dispose();

        _ipv4Client?.Close();
        _ipv4Client?.Dispose();
        _ipv4Client = null;

        _ipv6Client?.Close();
        _ipv6Client?.Dispose();
        _ipv6Client = null;

        _cts?.Dispose();
        _cts = null;
    }

    internal void ProcessMessage(string message, IPEndPoint sender)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // Basic verification
        if (!message.StartsWith("BT-SEARCH"))
        {
            return;
        }

        var lines = message.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
        int port = 0;
        string? hashStr = null;
        string? cookie = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("Port:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(line[5..].Trim(), out port);
            }
            else if (line.StartsWith("Infohash:", StringComparison.OrdinalIgnoreCase))
            {
                hashStr = line[9..].Trim();
            }
            else if (line.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            {
                cookie = line[7..].Trim();
            }
        }

        // Ignore our own announcements
        if (cookie == _cookie)
        {
            return;
        }

        if (port > 0 && !string.IsNullOrEmpty(hashStr) && InfoHash.TryFromHex(hashStr, out var hash))
        {
            var torrent = _resolver.GetTorrent(hash);
            if (torrent is Torrent t)
            {
                var peerEp = new IPEndPoint(sender.Address, port);
                _logger.LogInformation("LSD found peer {Peer} for {Torrent}", peerEp, t.Name);
                t.PeersInternal.AddPeers([peerEp], PeerSourceKind.Lpd, null);
            }
        }
    }

    private string BuildAnnounceMessage(InfoHash infoHash, string hostIp)
    {
        var sb = new StringBuilder();
        sb.Append("BT-SEARCH * HTTP/1.1\r\n");
        sb.Append($"Host: {hostIp}:{LsdPort}\r\n");
        sb.Append($"Port: {_settings.Connection.TcpPort}\r\n");
        sb.Append($"Infohash: {infoHash.ToHexString()}\r\n");
        sb.Append($"cookie: {_cookie}\r\n");
        sb.Append("\r\n\r\n");
        return sb.ToString();
    }

    private void OnAnnounceTick(object? state)
    {
        var torrents = _resolver is ClientEngine engine ? engine.GetTorrents() : [];
        // Use manager lifetime token when running; otherwise do not cancel.
        var token = _cts?.Token ?? CancellationToken.None;

        var announceTasks = new List<Task>();
        foreach (var torrent in torrents)
        {
            if (torrent.State == TorrentState.Active)
            {
                announceTasks.Add(AnnounceAsync(torrent.Hash, token));
            }
        }

        if (announceTasks.Count > 0)
        {
            _ = Task.WhenAll(announceTasks).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    _logger.LogDebug(t.Exception, "One or more LSD announces failed during periodic tick");
                }
            }, TaskScheduler.Default);
        }
    }

    private async Task ReceiveLoopAsync(IUdpSocket client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(token).ConfigureAwait(false);
                var message = Encoding.ASCII.GetString(result.Buffer);
                ProcessMessage(message, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (_running)
                {
                    _logger.LogDebug(ex, "LSD receive error");
                }
            }
        }
    }
}
