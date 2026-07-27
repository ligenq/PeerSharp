using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using System.Net;

namespace PeerSharp.Tests.Core;

/// <summary>
/// Work that is already in flight when the engine shuts down.
///
/// <para>
/// Listeners, DHT callbacks and half-finished handshakes all keep arriving for a moment after
/// disposal begins - a packet already sitting in a socket buffer has no idea the engine is going away.
/// Those paths should conclude that there is no torrent, which is true, rather than throw on a
/// background thread where nobody is waiting to catch it.
/// </para>
/// </summary>
public class ShutdownRaceTests
{
    [Fact(Timeout = 30000)]
    public async Task BackgroundLookupsAfterDisposalReturnNothing()
    {
        var engine = CreateEngine(out string path);
        try
        {
            await engine.InitializeAsync();
            await engine.DisposeAsync();

            // Background components hold the engine as ITorrentResolver.
            ITorrentResolver resolver = engine;

            Assert.Null(resolver.GetTorrent(InfoHash.CreateRandom()));
            Assert.Empty(resolver.GetTorrents());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task DhtCallbacksAfterDisposalDoNotThrow()
    {
        // A DHT reply in flight when shutdown began still lands on the callback.
        var engine = CreateEngine(out string path);
        try
        {
            await engine.InitializeAsync();
            await engine.DisposeAsync();

            engine.OnPeersFound(InfoHash.CreateRandom(), [new IPEndPoint(IPAddress.Loopback, 6881)]);
            engine.OnScrapeResult(InfoHash.CreateRandom(), 5, 5);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task DirectCallsAfterDisposalStillThrow()
    {
        // The control. A consumer calling into a disposed engine is a programming error and should
        // hear about it; only background resolution is made tolerant.
        var engine = CreateEngine(out string path);
        try
        {
            await engine.InitializeAsync();
            await engine.DisposeAsync();

            Assert.Throws<ObjectDisposedException>(() => engine.GetTorrent(InfoHash.CreateRandom()));
            Assert.Throws<ObjectDisposedException>(() => engine.GetTorrents());
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static ClientEngine CreateEngine(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "PeerSharpShutdown_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        var settings = new Settings
        {
            Files = { DefaultDownloadPath = path },
            Connection =
            {
                TcpPort = 0,
                UdpPort = 0,
                EnableLsd = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false
            },
            Dht = { Enabled = false },
            Session = { Enabled = false }
        };

        return ClientEngine.Create(new TorrentClientOptions { Settings = settings });
    }

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { /* Best effort. */ }
        catch (UnauthorizedAccessException) { /* Best effort. */ }
    }
}
