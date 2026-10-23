using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Interop;

/// <summary>A bounded opt-in download coordinated with an external, filtered packet capture.</summary>
[Collection("LiveEngine")]
public sealed class RealSwarmPacketAuditTests
{
    [Fact(Timeout = 300_000)]
    public async Task BoundedDownloadWithPacketCapture()
    {
        string? root = Environment.GetEnvironmentVariable("PEERSHARP_PACKET_AUDIT_DIR");
        string? source = Environment.GetEnvironmentVariable("PEERSHARP_SOAK_TORRENT");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(source))
            Assert.Skip("Use Invoke-LiveTrafficAudit.ps1 with an operator-supplied torrent and Npcap.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(4));
        var ct = budget.Token;
        bool useUtp = string.Equals(Environment.GetEnvironmentVariable("PEERSHARP_PACKET_AUDIT_TRANSPORT"), "Utp", StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(root);
        using var log = new TimestampedFileLoggerProvider(Path.Combine(root, "engine.log"));
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        var settings = new Settings
        {
            Files = { DefaultDownloadPath = Path.Combine(root, "download") },
            Session = { Enabled = false },
            Connection =
            {
                EnableUtpIn = useUtp, EnableUtpOut = useUtp, PreferUtp = useUtp,
                EnableTcpIn = false, EnableTcpOut = !useUtp, Encryption = Encryption.Refuse, EnableWebSeeds = false,
                EnableLsd = false, UpnpPortMapping = false, NatPmpPortMapping = false,
                MaxPeersPerTorrent = 12, MaxConnections = 12
            },
            Transfer = { MaxDownloadSpeed = 2 * 1024 * 1024, MaxUploadSpeed = 128 * 1024 }
        };
        await using var engine = ClientEngine.Create(new TorrentClientOptions { Settings = settings, LoggerFactory = loggerFactory });
        await engine.InitializeAsync(ct);
        var file = TorrentFile.Parse(await File.ReadAllBytesAsync(source, ct));
        var torrent = (Torrent)await engine.AddTorrentAsync(file, new AddTorrentOptions(), ct);
        var discovery = Stopwatch.StartNew();
        while (discovery.Elapsed < TimeSpan.FromSeconds(30))
        {
            var peers = torrent.Peers.GetConnectedPeers();
            if (peers.Count(p => p.Downloaded > 0) >= 3 || torrent.FinishedBytes >= 16 * 1024 * 1024) break;
            await Task.Delay(500, ct);
        }
        var selected = torrent.Peers.GetConnectedPeers()
            .Where(p => p.EndPoint.AddressFamily == AddressFamily.InterNetwork)
            .OrderByDescending(p => p.Downloaded).Take(8).Select(p => p.EndPoint).ToArray();
        Assert.NotEmpty(selected);
        ulong discoveryFinished = torrent.FinishedBytes;
        await torrent.StopAsync(ct);
        // Include TCP in uTP captures as evidence that fallback really remained disabled.
        string filter = (useUtp ? "(tcp or udp)" : "tcp") + " and (" + string.Join(" or ", selected.Select(p => $"(host {p.Address} and port {p.Port})")) + ")";
        await File.WriteAllTextAsync(Path.Combine(root, "capture-filter.txt"), filter, ct);
        await File.WriteAllTextAsync(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(new
        {
            name = torrent.Name,
            transport = useUtp ? "Utp" : "Tcp",
            totalSize = torrent.TotalSize,
            pieces = torrent.PieceCount,
            pieceSize = torrent.InfoFile.Info.PieceSize,
            infoHash = torrent.Hash.ToString(),
            discoveryFinished,
            peers = selected.Select(p => new { address = p.Address.ToString(), port = p.Port })
        }), ct);
        var readyWait = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "capture-ready")))
        {
            Assert.True(readyWait.Elapsed < TimeSpan.FromSeconds(30), "Capture coordinator did not become ready.");
            await Task.Delay(100, ct);
        }
        await torrent.StartAsync(ct);
        engine.OnPeersFound(torrent.Hash, [.. selected]);
        using var samples = new StreamWriter(Path.Combine(root, "samples.jsonl")) { AutoFlush = true };
        var observer = new RealSwarmObserver();
        var clock = Stopwatch.StartNew();
        long received = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(90) && received < 64L * 1024 * 1024)
        {
            var peers = torrent.Peers.GetConnectedPeers();
            Assert.All(peers, peer => Assert.Equal(useUtp, peer.IsUtp));
            observer.Sample(peers);
            received = ((ITorrent)torrent).GetTransferStats().Downloaded;
            await samples.WriteLineAsync(JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow,
                seconds = clock.Elapsed.TotalSeconds,
                received,
                verified = torrent.FinishedBytes,
                peers = peers.Select(p => new
                {
                    endpoint = p.EndPoint.ToString(),
                    p.ClientName,
                    p.Downloaded,
                    p.Uploaded,
                    p.DownloadSpeed,
                    p.PeerChoking,
                    p.AmInterested,
                    p.IsEncrypted,
                    p.IsUtp
                })
            }));
            await Task.Delay(500, ct);
        }
        long payloadReceived = ((ITorrent)torrent).GetTransferStats().Downloaded;
        ulong verifiedBefore = torrent.FinishedBytes;
        await torrent.StopAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(root, "download-stopped"), DateTimeOffset.UtcNow.ToString("O"), ct);
        int rechecked = await torrent.ForceRecheckAsync(cancellationToken: ct);
        ulong verifiedAfter = torrent.FinishedBytes;
        await File.WriteAllTextAsync(Path.Combine(root, "download-summary.json"), JsonSerializer.Serialize(new
        {
            elapsedSeconds = clock.Elapsed.TotalSeconds,
            payloadReceived,
            transport = useUtp ? "Utp" : "Tcp",
            discoveryFinished,
            verifiedBefore,
            verifiedAfter,
            rechecked,
            observer.PeakConcurrentPeers,
            peersMet = observer.Peers.Count,
            recheckLostVerifiedData = verifiedAfter < verifiedBefore
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        await File.WriteAllTextAsync(Path.Combine(root, "peers.txt"), observer.BuildReport("Captured real download"), ct);
        Assert.True(payloadReceived > 0, "No piece payload was received during capture.");
        Assert.True(verifiedBefore > discoveryFinished, "No additional pieces completed during capture.");
        Assert.True(verifiedAfter >= verifiedBefore, "Disk recheck lost previously verified data.");
        TestContext.Current.TestOutputHelper!.WriteLine($"Real-download evidence: {root}");
    }
}
