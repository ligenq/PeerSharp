using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utp;

namespace PeerSharp.Tests.Core.Utp;

public class EncryptedUtpFaultTests
{
    [Theory(Timeout = 30000)]
    [InlineData(7, true)]
    [InlineData(13, true)]
    [InlineData(7, false)]
    [InlineData(13, false)]
    public async Task EncryptionAndBidirectionalPayloadSurviveLossDuplicationAndReordering(int lossInterval, bool encrypted)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var left = new FaultyListener(41001, lossInterval);
        var right = new FaultyListener(41002, lossInterval);
        await using var leftManager = new UtpManager(clock);
        await using var rightManager = new UtpManager(clock);
        leftManager.Start(left);
        rightManager.Start(right);
        var accepted = new TaskCompletionSource<UtpStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        rightManager.OnNewConnection = stream => accepted.TrySetResult(stream);
        using var client = leftManager.CreateStream(right.Endpoint);
        Task connect = client.ConnectAsync(cancellationToken: ct);
        await PumpUntilAsync(Task.WhenAll(connect, accepted.Task), clock, left, right, ct);
        using var server = await accepted.Task;

        byte[] hash = SHA1.HashData("encrypted uTP fault audit"u8);
        using var initiator = new ProtocolEncryptionHandshake(hash, true) { AllowPlaintextPayload = !encrypted };
        using var responder = new ProtocolEncryptionHandshake(hash, false) { AllowPlaintextPayload = !encrypted, PreferRc4 = encrypted };
        initiator.InitialPayload = "initial BitTorrent payload"u8.ToArray();
        Task handshake = Task.WhenAll(HandshakeAsync(client, initiator, true, ct), HandshakeAsync(server, responder, false, ct));
        await PumpUntilAsync(handshake, clock, left, right, ct);
        Assert.Equal(encrypted, initiator.EncryptPayload);
        Assert.Equal(encrypted, responder.EncryptPayload);
        Assert.Equal(initiator.InitialPayload, responder.ReceivedPayload);

        byte[] forward = new byte[128 * 1024];
        byte[] reverse = new byte[96 * 1024];
        new Random(1234).NextBytes(forward);
        new Random(4321).NextBytes(reverse);
        byte[] forwardEncrypted = forward.ToArray();
        byte[] reverseEncrypted = reverse.ToArray();
        if (encrypted)
        {
            initiator.Encryption!.Encrypt(forwardEncrypted, 0, forwardEncrypted.Length);
            responder.Encryption!.Encrypt(reverseEncrypted, 0, reverseEncrypted.Length);
        }
        byte[] forwardReceived = new byte[forward.Length];
        byte[] reverseReceived = new byte[reverse.Length];
        Task transfer = Task.WhenAll(
            client.WriteAsync(forwardEncrypted, ct).AsTask(),
            server.WriteAsync(reverseEncrypted, ct).AsTask(),
            server.ReadExactlyAsync(forwardReceived, ct).AsTask(),
            client.ReadExactlyAsync(reverseReceived, ct).AsTask());
        await PumpUntilAsync(transfer, clock, left, right, ct);
        if (encrypted)
        {
            responder.Encryption!.Decrypt(forwardReceived, 0, forwardReceived.Length);
            initiator.Encryption!.Decrypt(reverseReceived, 0, reverseReceived.Length);
        }

        Assert.Equal(forward, forwardReceived);
        Assert.Equal(reverse, reverseReceived);
        Assert.True(left.Dropped > 0 && right.Dropped > 0);
        Assert.True(left.Duplicated > 0 && right.Duplicated > 0);
        Assert.True(left.Reordered + right.Reordered > 0);
    }

    private static async Task HandshakeAsync(UtpStream stream, ProtocolEncryptionHandshake handshake, bool initiate, CancellationToken ct)
    {
        if (initiate)
        {
            await stream.WriteAsync(handshake.Initiate(), ct);
        }
        byte[] buffer = new byte[2048];
        while (!handshake.IsComplete)
        {
            int read = await stream.ReadAsync(buffer, ct);
            Assert.True(read > 0);
            byte[] response = handshake.HandleIncoming(buffer.AsSpan(0, read).ToArray());
            if (response.Length > 0)
            {
                await stream.WriteAsync(response, ct);
            }
        }
    }

    private static async Task PumpUntilAsync(Task completion, FakeTimeProvider clock, FaultyListener left, FaultyListener right, CancellationToken ct)
    {
        for (int i = 0; i < 15000 && !completion.IsCompleted; i++)
        {
            left.DeliverTo(right);
            right.DeliverTo(left);
            clock.Advance(TimeSpan.FromMilliseconds(10));
            // Allow the pipe readers/writers to run between network ticks.
            await Task.Delay(1, ct);
        }
        Assert.True(completion.IsCompleted, "The encrypted uTP operation stalled under controlled packet faults.");
        await completion;
    }

    private sealed class FaultyListener(int port, int lossInterval) : IUdpListener
    {
        private readonly ConcurrentQueue<byte[]> _packets = new();
        private readonly HashSet<ushort> _seen = [];
        private IUdpReceiver? _receiver;
        private int _uniqueData;
        public IPEndPoint Endpoint { get; } = new(IPAddress.Loopback, port);
        public int Port => port;
        public int Dropped { get; private set; }
        public int Duplicated { get; private set; }
        public int Reordered { get; private set; }

        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint endpoint, CancellationToken ct)
        {
            _packets.Enqueue(data.ToArray());
            return Task.CompletedTask;
        }

        public void DeliverTo(FaultyListener target)
        {
            var batch = new List<byte[]>();
            while (_packets.TryDequeue(out byte[]? packet))
            {
                batch.Add(packet);
            }
            if (batch.Count > 1)
            {
                batch.Reverse();
                Reordered++;
            }
            foreach (byte[] packet in batch)
            {
                bool data = packet[0] >> 4 == 0;
                ushort sequence = UtpManager.ReadUInt16BigEndian(packet, 16);
                if (data && _seen.Add(sequence) && ++_uniqueData % lossInterval == 1)
                {
                    Dropped++;
                    continue;
                }
                target._receiver!.Receive(packet, Endpoint);
                if (data && sequence % 5 == 0)
                {
                    target._receiver.Receive(packet, Endpoint);
                    Duplicated++;
                }
            }
        }

        public void RegisterReceiver(IUdpReceiver receiver) => _receiver = receiver;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
