using PeerSharp.Clients;
using PeerSharp.Config;
using PeerSharp.Internals;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace PeerSharp.Benchmarks;

/// <summary>Hash-verified, single-process TCP/uTP whole-transfer comparison, not a microbenchmark.</summary>
internal static class LoopbackTransfer
{
    public static async Task RunAsync(string[] args)
    {
        int sizeMiB = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 256;
        int iterations = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;
        string transport = args.Length > 2 ? args[2] : "both";
        if (args.Length > 3 || sizeMiB is < 1 or > 1024 || iterations is < 1 or > 100 ||
            transport is not ("both" or "tcp" or "utp"))
        {
            throw new ArgumentException("Usage: --loopback [MiB:1..1024] [iterations:1..100] [both|tcp|utp]");
        }

        string root = Path.GetFullPath(Path.Combine("artifacts", "transport-loopback", Guid.NewGuid().ToString("N")));
        string seedPath = Path.Combine(root, "seed");
        Directory.CreateDirectory(seedPath);
        Console.WriteLine($"Results and verified payloads: {root}");
        byte[] payload = new byte[sizeMiB * 1024 * 1024];
        new Random(1741).NextBytes(payload);
        byte[] expectedHash = SHA256.HashData(payload);
        await File.WriteAllBytesAsync(Path.Combine(seedPath, "payload.bin"), payload).ConfigureAwait(false);
        var torrent = new Core.TorrentFileBuilder().WithName("payload.bin").WithPieceLength(256 * 1024)
            .AddFile("payload.bin", payload).Build();
        payload = null!;

        // One full-size warmup per transport, then alternate order to reduce ordering bias.
        for (int iteration = 0; iteration <= iterations; iteration++)
        {
            string[] transports = transport == "both"
                ? (iteration % 2 == 0 ? ["utp", "tcp"] : ["tcp", "utp"])
                : [transport];
            foreach (string protocol in transports)
            {
                string downPath = Path.Combine(root, $"{iteration}-{protocol}");
                Directory.CreateDirectory(downPath);
                await using var seed = await CreateEngineAsync(seedPath, protocol).ConfigureAwait(false);
                await using var down = await CreateEngineAsync(downPath, protocol).ConfigureAwait(false);
                var seeding = await seed.AddTorrentAsync(torrent, new AddTorrentOptions { StartImmediately = false }).ConfigureAwait(false);
                await seeding.ForceRecheckAsync().ConfigureAwait(false);
                if (seeding.FinishedBytes != (ulong)sizeMiB * 1024 * 1024)
                {
                    throw new InvalidOperationException("Seed recheck failed.");
                }
                await seeding.StartAsync().ConfigureAwait(false);
                var downloading = await down.AddTorrentAsync(torrent, new AddTorrentOptions { StartImmediately = true }).ConfigureAwait(false);

                using var process = Process.GetCurrentProcess();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long allocation = GC.GetTotalAllocatedBytes(true);
                TimeSpan cpu = process.TotalProcessorTime;
                int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
                var clock = Stopwatch.StartNew();
                int port = protocol == "utp" ? seed.BoundUdpPort : seed.BoundTcpPort;
                down.OnPeersFound(torrent.InfoHash, [new IPEndPoint(IPAddress.Loopback, port)]);
                while (!downloading.Finished && clock.Elapsed.TotalSeconds < 120)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }
                clock.Stop();
                process.Refresh();
                double cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
                double allocatedMiB = (GC.GetTotalAllocatedBytes(true) - allocation) / 1048576.0;
                int[] gcCounts = [GC.CollectionCount(0) - collections[0], GC.CollectionCount(1) - collections[1], GC.CollectionCount(2) - collections[2]];

                // Verification is outside the timing, but no successful result is emitted without it.
                bool verified = false;
                if (downloading.Finished)
                {
                    await using var file = new FileStream(Path.Combine(downPath, "payload.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    verified = (await SHA256.HashDataAsync(file).ConfigureAwait(false)).AsSpan().SequenceEqual(expectedHash);
                }
                string result = JsonSerializer.Serialize(new
                {
                    protocol,
                    iteration,
                    warmup = iteration == 0,
                    sizeMiB,
                    finished = downloading.Finished,
                    verified,
                    seconds = clock.Elapsed.TotalSeconds,
                    mibPerSecond = downloading.FinishedBytes / 1048576.0 / clock.Elapsed.TotalSeconds,
                    cpuSeconds,
                    allocatedMiB,
                    gcCounts
                });
                Console.WriteLine(result);
                await File.AppendAllTextAsync(Path.Combine(root, "results.jsonl"), result + Environment.NewLine).ConfigureAwait(false);
                if (!verified)
                {
                    throw new InvalidOperationException("Transfer timed out or payload verification failed; see results.jsonl.");
                }
            }
        }
    }

    private static async Task<ClientEngine> CreateEngineAsync(string path, string protocol)
    {
        var engine = ClientEngine.Create(new TorrentClientOptions
        {
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = path },
                Connection =
                {
                    BindAddress = IPAddress.Loopback, TcpPort = 0, UdpPort = 0, EnableLsd = false,
                    EnableTcpIn = protocol == "tcp", EnableTcpOut = protocol == "tcp",
                    EnableUtpIn = protocol == "utp", EnableUtpOut = protocol == "utp",
                    UpnpPortMapping = false, NatPmpPortMapping = false, Encryption = Encryption.Refuse
                },
                Dht = { Enabled = false },
                Session = { Enabled = false }
            }
        });
        try
        {
            await engine.InitializeAsync().ConfigureAwait(false);
            return engine;
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
