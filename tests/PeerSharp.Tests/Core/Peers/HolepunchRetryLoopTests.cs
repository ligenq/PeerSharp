using Microsoft.Extensions.Logging;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using System.Net;
using System.Reflection;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// A failed holepunch must not ask for another one.
///
/// <para>
/// When a connection fails and the peer was introduced by someone who supports ut_holepunch, we ask
/// that introducer to arrange a NAT traversal. The relay answers by telling us to connect, and that
/// dial deliberately skips the per-peer backoff, because both ends have to fire at the same moment for
/// the hole to open.
/// </para>
///
/// <para>
/// Which closes a loop if the holepunch dial is itself allowed to ask for a rendezvous when it fails:
/// fail, ask, dial, fail, with the backoff that would normally slow this down explicitly bypassed. A
/// live run dialled one unreachable endpoint 29 times in eight minutes this way, and the holepunch
/// budget it burned is shared with every other peer. libtorrent guards the same call with
/// <c>!m_holepunch_mode</c>.
/// </para>
/// </summary>
public class HolepunchRetryLoopTests
{
    /// <summary>An address that will not answer, so every dial fails the same way.</summary>
    private static IPEndPoint Unreachable => new(IPAddress.Parse("192.0.2.1"), 6881); // TEST-NET-1

    [Fact(Timeout = 120000)]
    public async Task AFailedHolepunchDialDoesNotRequestAnotherRendezvous()
    {
        var capture = new Interop.CapturingLoggerProvider(LogLevel.Debug);
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));

        var torrent = TorrentTestUtility.CreateMinimal();
        var connection = torrent.Settings.Connection;
        connection.InitialConnectionTimeoutMs = 1000;   // Fail fast; the address never answers.
        connection.MinConnectionTimeoutMs = 500;
        connection.UtpFallbackTimeoutMs = 500;
        connection.EnableTcpOut = true;
        connection.EnableUtpOut = true;
        connection.UtpWarmupSeconds = 0;                // Warmup would refuse the forced-uTP plan outright.

        // A holepunch dial is forced onto uTP, and a plan that forces a transport the torrent cannot
        // speak comes back empty - so without this the dial under test never happens and the assertion
        // passes for the wrong reason.
        var utpManager = new PeerSharp.Internals.Utp.UtpManager(TimeProvider.System);
        torrent.UtpManager = utpManager;

        var manager = new PeerManager(
            torrent,
            new TorrentTestUtility.MockGeoIpService(),
            new PeerCommunicationFactory(),
            TimeProvider.System,
            new TorrentTestUtility.MockConnectionGovernor(),
            loggerFactory.CreateLogger<PeerManager>());

        await manager.StartAsync();

        // An introducer that claims ut_holepunch support, so the failure path has somewhere to ask.
        var introducer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        GiveHolepunchSupport(introducer);

        try
        {
            manager.AddPeers([Unreachable], PeerSourceKind.Pex, introducer);

            // An ordinary dial that fails should ask the introducer for a rendezvous.
            manager.ConnectTo(Unreachable.Address.ToString(), Unreachable.Port);

            await TorrentTestUtility.WaitUntilAsync(
                () => RendezvousRequests(capture) >= 1,
                timeoutMs: 30000,
                because: "an ordinary failed connection to ask for a holepunch");

            int afterOrdinary = RendezvousRequests(capture);

            // Now the dial a relay asks us to make. Its failure must not produce another request.
            await manager.HolepunchMessageReceivedAsync(
                introducer, UtHolepunch.MsgId.Connect, Unreachable, UtHolepunch.ErrorCode.None);

            // Long enough for the dial to be queued, attempted and to fail.
            await Task.Delay(TimeSpan.FromSeconds(8));

            int afterHolepunch = RendezvousRequests(capture);

            Assert.True(
                afterHolepunch == afterOrdinary,
                $"A failed holepunch dial asked for {afterHolepunch - afterOrdinary} further rendezvous. " +
                "That closes a retry loop with nothing to break it, because the holepunch dial skips the " +
                "per-peer backoff by design.");
        }
        finally
        {
            await manager.StopAsync();
            await introducer.DisposeAsync();
            await utpManager.DisposeAsync();
            await torrent.DisposeAsync();
            loggerFactory.Dispose();
        }
    }

    private static int RendezvousRequests(Interop.CapturingLoggerProvider capture)
        => capture.CountMatching("attempting holepunch via");

    /// <summary>
    /// Marks the peer as supporting ut_holepunch. The property is set from a real extended handshake,
    /// which needs a live connection, so the test writes it directly - the existing PeerCommunication
    /// tests reach private state the same way.
    /// </summary>
    private static void GiveHolepunchSupport(PeerCommunication peer)
    {
        var handshake = new ExtensionHandshake();
        handshake.MessageIds[UtHolepunch.Name] = 4;

        typeof(PeerCommunication)
            .GetProperty(nameof(PeerCommunication.RemoteExtensions), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(peer, handshake);

        peer.UtHolepunch.SetLocalMessageId(4);
        peer.UtHolepunch.Init(handshake);
    }
}
