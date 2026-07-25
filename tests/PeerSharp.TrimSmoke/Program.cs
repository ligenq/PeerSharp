using PeerSharp.Clients;
using PeerSharp.Config;
using PeerSharp.Core;

namespace PeerSharp.TrimSmoke;

/// <summary>
/// Exercises a representative slice of the public API so the trimmer has real roots to analyse.
///
/// The point is the publish, not the run: <c>PublishTrimmed</c> with warnings as errors fails the
/// build if anything reachable from here uses reflection the trimmer cannot follow. Touching the
/// engine, torrent parsing, torrent creation and configuration keeps the roots wide enough that
/// the check is meaningful - trimming an unreferenced library proves nothing.
///
/// It does run in CI as well, as a cheap check that the trimmed binary is not merely well-formed
/// but actually starts.
/// </summary>
public static class Program
{
    public static async Task<int> Main()
    {
        // Configuration and engine construction.
        var settings = new Settings();
        settings.Connection.MaxConnections = 50;
        var options = new TorrentClientOptions { Settings = settings };
        await using var engine = ClientEngineFactory.Create(options);

        // Torrent creation, which pulls in hashing, the Merkle tree and the bencode writer.
        byte[] payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        var created = await new TorrentFileBuilder()
            .WithName("trim-smoke")
            .WithVersion(TorrentFileVersion.Hybrid)
            .WithPieceLength(16 * 1024)
            .AddTracker("https://tracker.invalid/announce")
            .AddFile("trim-smoke/data.bin", payload)
            .BuildAsync();

        // Parsing back, which pulls in the bencode parser and metadata validation.
        var reparsed = TorrentFile.Parse(created.RawData.ToArray());
        if (reparsed.InfoHash != created.InfoHash)
        {
            Console.Error.WriteLine("Round-trip produced a different info hash.");
            return 1;
        }

        var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{created.InfoHash}");

        Console.WriteLine(
            $"Trim smoke OK: {reparsed.FileCount} file(s), {reparsed.PieceCount} piece(s), " +
            $"magnet={magnet.InfoHash}, maxConnections={settings.Connection.MaxConnections}");
        return 0;
    }
}
