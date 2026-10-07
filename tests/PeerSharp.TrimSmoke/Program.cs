using PeerSharp.Clients;
using PeerSharp.Config;
using PeerSharp.Core;
using System.Net;

namespace PeerSharp.TrimSmoke;

/// <summary>
/// Exercises the public API through a trimmed executable, including engine initialization,
/// hybrid torrent hashing, file verification and streaming. Trim warnings fail the publish;
/// runtime assertions also catch dependencies that survive analysis but fail after trimming.
/// </summary>
public static class Program
{
    public static async Task<int> Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PeerSharp.TrimSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string payloadPath = Path.Combine(directory, "data.bin");
        try
        {
            var settings = new Settings
            {
                Files = { DefaultDownloadPath = directory },
                Connection =
                {
                    BindAddress = IPAddress.Loopback,
                    TcpPort = 0,
                    UdpPort = 0,
                    EnableUtpIn = false,
                    EnableUtpOut = false,
                    EnableLsd = false,
                    UpnpPortMapping = false,
                    NatPmpPortMapping = false
                },
                Dht = { Enabled = false }
            };
            await using var engine = ClientEngineFactory.Create(new TorrentClientOptions { Settings = settings });
            await engine.InitializeAsync();

            byte[] payload = new byte[64 * 1024];
            Random.Shared.NextBytes(payload);
            await File.WriteAllBytesAsync(payloadPath, payload);
            var created = await new TorrentFileBuilder()
                .WithName("data.bin")
                .WithVersion(TorrentFileVersion.Hybrid)
                .WithPieceLength(16 * 1024)
                .AddFile("data.bin", payload)
                .BuildAsync();
            var reparsed = TorrentFile.Parse(created.RawData.ToArray());
            if (reparsed.InfoHash != created.InfoHash) throw new InvalidDataException("Torrent hash changed during round-trip.");
            var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{created.InfoHash}");

            var torrent = await engine.AddTorrentAsync(reparsed, new AddTorrentOptions { StartImmediately = false });
            if (await torrent.ForceRecheckAsync() != created.PieceCount) throw new InvalidDataException("File verification failed.");
            await torrent.StartAsync();
            await using (var stream = await torrent.OpenStreamAsync(0))
            {
                byte[] received = new byte[payload.Length];
                await stream.ReadExactlyAsync(received);
                if (!received.AsSpan().SequenceEqual(payload)) throw new InvalidDataException("Streaming bytes differ from the payload.");
                stream.Seek(123, SeekOrigin.Begin);
                byte[] range = new byte[37];
                await stream.ReadExactlyAsync(range);
                if (!range.AsSpan().SequenceEqual(payload.AsSpan(123, range.Length))) throw new InvalidDataException("Seek returned incorrect bytes.");
            }
            await torrent.StopAsync();
            Console.WriteLine($"Trim smoke OK: {reparsed.PieceCount} verified pieces, full stream and seek; magnet={magnet.InfoHash}");
            return 0;
        }
        finally
        {
            File.Delete(payloadPath);
            // The smoke writes one known file. Non-recursive deletion will expose unexpected output.
            Directory.Delete(directory);
        }
    }
}
