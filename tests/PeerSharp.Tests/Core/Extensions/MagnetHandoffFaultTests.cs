using System.Net;
using PeerSharp.BEncoding;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Extensions;

public sealed class MagnetHandoffFaultTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task AStopDuringMetadataHandoffPublishesSettledMetadataAndReleasesPeers(int seed)
        => ReplayableScenario.RunAsync(seed, RunAsync, TestContext.Current.CancellationToken);

    private static async Task RunAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("PeerSharpMagnetFault-");
        try
        {
            await RunInDirectoryAsync(root.FullName, seed, scenario, ct);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task RunInDirectoryAsync(string path, int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        bool isPrivate = (seed & 1) != 0;
        bool pauseSession = (seed & 2) != 0;
        const int pieceSize = ProtocolConstants.BlockSize;
        var payload = new byte[pieceSize];
        scenario.Random.NextBytes(payload);
        var file = new PeerSharp.Core.TorrentFileBuilder().WithName("magnet-fault").WithPieceLength(pieceSize)
            .WithPrivate(isPrivate).AddFile("a.bin", payload).AddFile("b.bin", payload).Build();
        byte[] info = file.Metadata.InfoBytes!;
        Assert.InRange(info.Length, 1, UtMetadata.PieceSize);
        await using var engine = ClientEngine.Create(new TorrentClientOptions
        {
            Settings = new Settings
            {
                Files = { DefaultDownloadPath = path },
                Connection = { TcpPort = 0, UdpPort = 0, EnableLsd = false, EnableUtpIn = false, EnableUtpOut = false,
                    UpnpPortMapping = false, NatPmpPortMapping = false },
                Dht = { Enabled = false },
                Session = { Enabled = false }
            }
        });
        await engine.InitializeAsync(ct);
        int excluded = scenario.Random.Next(2);
        var selections = new[] { new FileSelection(), new FileSelection() };
        selections[excluded] = new FileSelection(Selected: false);
        var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{file.InfoHash.ToHexString()}");
        var torrent = (Torrent)await engine.AddMagnetAsync(magnet,
            new AddTorrentOptions(path) { FileSelections = selections }, ct);
        Assert.True(torrent.Started);
        var peers = torrent.PeersInternal;
        var governor = torrent.Services.ConnectionGovernor;
        var source = AddPeer(peers, torrent, 11);
        var discovery = AddPeer(peers, torrent, 12);
        peers.AddPeers([discovery.RemoteEndPoint!], PeerSourceKind.Dht);
        peers.ImportConnectionPreferences([new SavedPeerPreference("203.0.113.12", 6881, false, true)]);
        Assert.Single(peers.ExportConnectionPreferences());
        Assert.Equal(2, governor.ActiveConnections);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int publications = 0;
        torrent.Events = new TorrentEventsBuilder()
            .OnStateChanged((_, transition) =>
            {
                if (transition.NewState == TorrentState.Stopping && entered.TrySetResult())
                {
                    // For public metadata the old manager has just detached the peers. For private
                    // metadata it still owns them and will close them in the stop immediately below.
                    release.Wait(ct);
                }
            })
            .OnMetadataReceived(_ => Interlocked.Increment(ref publications))
            .Build();
        Task waiter = WaitForSettledMetadataAsync(torrent, selections, ct);

        var header = new BDict();
        header.Dict["msg_type"] = new BNumber((int)UtMetadata.MessageType.Data);
        header.Dict["piece"] = new BNumber(0);
        header.Dict["total_size"] = new BNumber(info.Length);
        byte[] message = [.. BencodeWriter.Write(header), .. info];
        scenario.Record($"deliver valid {(isPrivate ? "private" : "public")} metadata through the peer message handler");
        Task delivery = Task.Run(() => peers.ExtendedMessageReceivedAsync(source, source.UtMetadata.LocalMessageId!.Value, message), ct);
        Task interrupt = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Timeout, ct);
            Assert.False(waiter.IsCompleted);
            Assert.Equal(isPrivate ? 2 : 0, peers.ConnectedCount);
            Assert.Equal(2, governor.ActiveConnections);
            Assert.Equal(0, Volatile.Read(ref publications));

            scenario.Record(pauseSession ? "request a session pause while the rebuild holds the state lock" :
                "request a torrent stop while the rebuild holds the state lock");
            long previousStops = torrent.StopRequests;
            interrupt = pauseSession ? engine.PauseAsync(ct) : torrent.StopAsync(ct);
            await TorrentTestUtility.WaitUntilAsync(() => torrent.StopRequests > previousStops, because: "stop request to reach the torrent");
            Assert.False(interrupt.IsCompleted);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(delivery, interrupt).WaitAsync(Timeout, ct);
        }

        await waiter.WaitAsync(Timeout, ct);
        // Repeat the last data message against the settled torrent to check publication is one-shot.
        await torrent.PeersInternal.ExtendedMessageReceivedAsync(source, source.UtMetadata.LocalMessageId!.Value, message);
        Assert.Equal(file.InfoHash, torrent.ExportTorrentFile().InfoHash);
        Assert.Equal(1, Volatile.Read(ref publications));
        Assert.Equal(TorrentState.Stopped, torrent.State);
        Assert.Equal(pauseSession, engine.IsPaused);
        Assert.Equal(0, source.Connected);
        Assert.Equal(0, discovery.Connected);
        Assert.Equal(0, peers.ConnectedCount);
        Assert.Equal(0, torrent.PeersInternal.ConnectedCount);
        Assert.Equal(0, governor.ActiveConnections);
        Assert.Equal(isPrivate ? 0 : 1, torrent.PeersInternal.ExportConnectionPreferences().Count);
    }

    private static PeerCommunication AddPeer(PeerManager peers, Torrent torrent, int address)
    {
        var peer = new PeerCommunication(torrent, peers, torrent.Services.TimeProvider)
        {
            Connected = 1,
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse($"203.0.113.{address}"), 6881),
            Stream = new MemoryStream()
        };
        peer.UtMetadata.SetLocalMessageId(1);
        Assert.True(torrent.Services.ConnectionGovernor.TryAcquireConnectionSlot());
        peers.AddConnectedPeerForTesting(peer);
        return peer;
    }

    private static async Task WaitForSettledMetadataAsync(Torrent torrent, FileSelection[] selections, CancellationToken ct)
    {
        await torrent.WaitForMetadataAsync(ct);
        Assert.Equal(selections, torrent.GetAllFileSelections());
        Assert.Equal(TorrentState.Stopped, torrent.State);
    }
}
