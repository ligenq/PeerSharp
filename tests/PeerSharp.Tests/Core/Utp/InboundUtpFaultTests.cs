using System.Collections;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;

namespace PeerSharp.Tests.Core.Utp;

public sealed class InboundUtpFaultTests
{
    private static readonly FieldInfo PendingField = typeof(ClientEngine).GetField("_pendingUtpHandshakes",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ReceiveStreamsField = typeof(UtpManager).GetField("_streamsByRecvId",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo SendStreamsField = typeof(UtpManager).GetField("_streamsBySendId",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PipeWriterField = typeof(UtpStream).GetField("_pipeWriteTask",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ReorderBytesField = typeof(UtpStream).GetField("_reorderBufferBytes",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory(Timeout = 30000)]
    [Trait("Suite", "ReplayableFaults")]
    [MemberData(nameof(ReplayableScenario.Seeds), MemberType = typeof(ReplayableScenario))]
    public Task AdmissionDeadlinesAndShutdownKeepStreamAndSlotCountsInSync(int seed)
        => ReplayableScenario.RunAsync(seed, RunScenarioAsync, TestContext.Current.CancellationToken);

    [Fact(Timeout = 10000)]
    public async Task AReceiveAlreadyDispatchedBeforeShutdownCannotBufferOnTheClosedStream()
    {
        var clock = new FakeTimeProvider();
        using var listener = new MockUdpListener();
        await using var manager = new UtpManager(clock, NullLoggerFactory.Instance);
        manager.Start(listener);
        UtpStream? accepted = null;
        manager.OnNewConnection = stream => accepted = stream;
        var remote = new IPEndPoint(IPAddress.Loopback, 18000);
        listener.SimulateReceive(Packet(MessageType.ST_SYN, 42), remote);
        var stream = Assert.IsType<UtpStream>(accepted);
        var ct = TestContext.Current.CancellationToken;
        var read = stream.ReadAsync(new byte[1], ct);

        // Capture the stream as the UDP receive path does before acquiring its packet lock.
        // Stop wins that lock, then the in-flight receive resumes with an out-of-order DATA packet.
        manager.Stop();
        byte[] data = Packet(MessageType.ST_DATA, stream.ConnectionIdRecv, seq: 102, payload: [1]);
        stream.ProcessPacketWithSack(new MessageHeader
        {
            TypeVer = data[0],
            ConnectionId = stream.ConnectionIdRecv,
            SeqNr = 102
        }, data, 20, null, null, remote);

        Assert.Equal(0, await read.AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct));
        await AssertClosedAsync([stream], ct);
        Assert.Equal(0, (int)ReorderBytesField.GetValue(stream)!);
    }

    private static async Task RunScenarioAsync(int seed, ReplayableScenario scenario, CancellationToken ct)
    {
        var clock = new FakeTimeProvider();
        var settings = new Settings
        {
            Connection =
            {
                BindAddress = IPAddress.Loopback,
                UdpPort = 0,
                EnableUtpIn = true,
                EnableUtpOut = false,
                EnableTcpIn = false,
                EnableLsd = false,
                UpnpPortMapping = false,
                NatPmpPortMapping = false,
                UtpMaxSynRetries = 10
            },
            Dht = { Enabled = false },
            Queue = { Enabled = false, EnforceAutoStop = false }
        };
        await using var engine = ClientEngine.Create(settings, timeProvider: clock);
        await engine.InitializeAsync(ct);
        var manager = Assert.IsType<UtpManager>(engine.Utp);
        using var listener = new MockUdpListener();
        manager.Stop();
        manager.Start(listener);
        var limiter = (PendingHandshakeLimiter)PendingField.GetValue(engine)!;
        var receiveStreams = (IDictionary)ReceiveStreamsField.GetValue(manager)!;
        var sendStreams = (IDictionary)SendStreamsField.GetValue(manager)!;
        var accept = manager.OnNewConnection!;
        var pending = new Dictionary<(IPEndPoint Endpoint, ushort Id), UtpStream>();
        manager.OnNewConnection = stream =>
        {
            pending.Add((stream.RemoteEndPoint, stream.ConnectionIdSend), stream);
            accept(stream);
        };

        void SendSyn(IPEndPoint endpoint, ushort id)
        {
            listener.SimulateReceive(Packet(MessageType.ST_SYN, id), endpoint);
        }

        for (int phase = 0; phase < 3; phase++)
        {
            // Fill the cap from one address, then exceed the global cap with other addresses.
            var shared = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 18000 + phase);
            var attempts = new List<(IPEndPoint Endpoint, ushort Id)>();
            for (int i = 0; i < PendingHandshakeLimiter.DefaultMaxPerAddress + 3; i++)
            {
                attempts.Add((shared, (ushort)(phase == 0 && i == 0 ? ushort.MaxValue : i)));
            }
            for (int i = 0; i < 2 * PendingHandshakeLimiter.DefaultMaxTotal; i++)
            {
                attempts.Add((new IPEndPoint(new IPAddress([127, 0, (byte)(1 + (i / 256)), (byte)i]), 18100 + phase), (ushort)i));
            }

            scenario.Record($"phase {phase}: pressure, packet loss, reordered SYNs and retries");
            var byAddress = new Dictionary<IPAddress, int>();
            var expected = new HashSet<(IPEndPoint Endpoint, ushort Id)>();
            // Keep the per-address pressure first. Shuffle the remaining requests so the identity
            // of the peers admitted under the global cap varies by seed.
            int sharedCount = PendingHandshakeLimiter.DefaultMaxPerAddress + 3;
            var otherAddresses = attempts.Skip(sharedCount).ToArray();
            scenario.Random.Shuffle(otherAddresses);
            foreach (var attempt in attempts.Take(sharedCount).Concat(otherAddresses))
            {
                ct.ThrowIfCancellationRequested();
                if (attempt.Endpoint != shared && scenario.Random.Next(8) == 0)
                {
                    scenario.Record($"  dropped SYN {attempt.Endpoint}/{attempt.Id}");
                    continue;
                }
                var address = attempt.Endpoint.Address;
                int count = byAddress.GetValueOrDefault(address);
                if (count < PendingHandshakeLimiter.DefaultMaxPerAddress &&
                    expected.Count < PendingHandshakeLimiter.DefaultMaxTotal)
                {
                    expected.Add(attempt);
                    byAddress[address] = count + 1;
                }

                scenario.Record($"  SYN {attempt.Endpoint}/{attempt.Id}, expected admission={expected.Contains(attempt)}");
                SendSyn(attempt.Endpoint, attempt.Id);
                Assert.Equal(expected.Count, receiveStreams.Count);
                Assert.Equal(expected.Count, sendStreams.Count);
                Assert.Equal(expected.Count, limiter.Pending);
            }
            Assert.Equal(PendingHandshakeLimiter.DefaultMaxTotal, expected.Count);
            var retries = pending.Keys.ToArray();
            scenario.Random.Shuffle(retries);
            foreach (var retry in retries.Take(12))
            {
                scenario.Record($"  retry {retry.Endpoint}/{retry.Id}");
                SendSyn(retry.Endpoint, retry.Id);
                Assert.Equal(expected.Count, limiter.Pending);
                Assert.Equal(expected.Count, receiveStreams.Count);
                Assert.Equal(expected.Count, sendStreams.Count);
            }
            Assert.True(expected.SetEquals(pending.Keys));

            var ended = retries.Take(3).Select(key => pending[key]).ToArray();
            foreach (var stream in ended)
            {
                scenario.Record($"  reset {stream.RemoteEndPoint}/{stream.ConnectionIdSend}");
                listener.SimulateReceive(Packet(MessageType.ST_RESET, stream.ConnectionIdSend), stream.RemoteEndPoint);
            }
            int remaining = expected.Count - ended.Length;
            await TorrentTestUtility.WaitUntilAsync(() => limiter.Pending == remaining, because: "reset handshakes release their slots");
            Assert.Equal(remaining, receiveStreams.Count);
            Assert.Equal(remaining, sendStreams.Count);
            await AssertClosedAsync(ended, ct);

            if (phase < 2)
            {
                scenario.Record("  advance through the BitTorrent handshake deadline");
                clock.Advance(TimeSpan.FromSeconds(11));
            }
            else if ((seed & 1) == 0)
            {
                scenario.Record("  stop the engine with handshakes pending");
                await engine.StopAsync(ct);
            }
            else
            {
                scenario.Record("  dispose the engine with handshakes pending");
                await engine.DisposeAsync();
            }
            await TorrentTestUtility.WaitUntilAsync(() => limiter.Pending == 0, because: "all handshake slots to be released");
            await AssertClosedAsync(pending.Values, ct);
            Assert.Empty(receiveStreams);
            Assert.Empty(sendStreams);
            pending.Clear();
        }

        scenario.Record("late SYN after engine shutdown");
        SendSyn(new IPEndPoint(IPAddress.Loopback, 18004), 42);
        Assert.Empty(pending);
        Assert.Empty(receiveStreams);
        Assert.Empty(sendStreams);
        Assert.Equal(0, limiter.Pending);
    }

    private static async Task AssertClosedAsync(IEnumerable<UtpStream> streams, CancellationToken ct)
    {
        foreach (var stream in streams)
        {
            Assert.Equal(UtpState.Closed, stream.State);
            await ((Task)PipeWriterField.GetValue(stream)!).WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
    }

    private static byte[] Packet(MessageType type, ushort id, ushort seq = 100, byte[]? payload = null)
    {
        byte[] packet = new byte[20 + (payload?.Length ?? 0)];
        packet[0] = (byte)(((byte)type << 4) | MessageHeader.CurrentVersion);
        UtpManager.WriteUInt16BigEndian(packet, 2, id);
        UtpManager.WriteUInt16BigEndian(packet, 16, seq);
        payload?.CopyTo(packet, 20);
        return packet;
    }
}
