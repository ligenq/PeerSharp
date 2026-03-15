using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Trackers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Trackers;

public class UdpTrackerTests
{
    private class MockUdpSocket : IUdpSocket
    {
        public List<byte[]> SentPackets { get; } = new();
        public bool Closed { get; private set; }
        private TaskCompletionSource<UdpReceiveResult>? _receiveTcs;
        private readonly List<TaskCompletionSource<byte[]>> _sentPacketWaiters = new();

        public Socket Client => throw new NotImplementedException();

        public void TriggerResponse(byte[] data)
        {
            var tcs = Interlocked.Exchange(ref _receiveTcs, null);
            tcs?.TrySetResult(new UdpReceiveResult(data, new IPEndPoint(IPAddress.Loopback, 0)));
        }

        public void TriggerTimeout()
        {
            var tcs = Interlocked.Exchange(ref _receiveTcs, null);
            tcs?.TrySetException(new TimeoutException());
        }

        public async Task<byte[]> WaitForPacketAsync(int index, TimeSpan timeout)
        {
            TaskCompletionSource<byte[]> tcs;
            lock (_sentPacketWaiters)
            {
                if (SentPackets.Count > index)
                {
                    return SentPackets[index];
                }

                while (_sentPacketWaiters.Count <= index)
                {
                    _sentPacketWaiters.Add(new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                tcs = _sentPacketWaiters[index];
            }
            return await tcs.Task.WaitAsync(timeout);
        }

        public void Close()
        {
            Closed = true;
        }

        public void Dispose() { }

        public void JoinMulticastGroup(IPAddress multicastAddr) { }

        public Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<UdpReceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _receiveTcs, tcs);
            return tcs.Task;
        }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint endPoint, CancellationToken ct)
        {
            var packet = datagram.ToArray();
            lock (_sentPacketWaiters)
            {
                SentPackets.Add(packet);
                if (SentPackets.Count <= _sentPacketWaiters.Count)
                {
                    _sentPacketWaiters[SentPackets.Count - 1].TrySetResult(packet);
                }
            }
            return new ValueTask<int>(datagram.Length);
        }
    }

    private class MockUdpSocketFactory : IUdpSocketFactory
    {
        public MockUdpSocket LastSocket { get; } = new();
        public IUdpSocket Create(int port)
        {
            return LastSocket;
        }

        public IUdpSocket Create(AddressFamily family)
        {
            return LastSocket;
        }
    }

    private class MockCallback : ITrackerCallback
    {
        public bool Success { get; private set; }
        public AnnounceResponse? AnnounceResponse { get; private set; }
        public ScrapeResponse? ScrapeResponse { get; private set; }
        public void OnAnnounceResult(bool success, AnnounceResponse response, ITracker tracker, string? errorMessage = null)
        {
            Success = success;
            AnnounceResponse = response;
        }

        public void OnMultiScrapeResult(bool success, MultiScrapeResponse response, ITracker tracker)
        {
            throw new NotImplementedException();
        }

        public void OnScrapeResult(bool success, ScrapeResponse response, ITracker tracker)
        {
            Success = success;
            ScrapeResponse = response;
        }
    }

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly MockUdpSocketFactory _socketFactory = new();
    private readonly Torrent _torrent;
    private readonly MockCallback _callback = new();

    public UdpTrackerTests()
    {
        _torrent = TorrentTestUtility.CreateMinimal();
    }

                [Fact(Timeout = 30000)]

                public async Task AnnounceAsync_FullCycle_Succeeds()

                {

                    // Arrange

                    var tracker = new UdpTracker(_timeProvider, _socketFactory);

                    tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);


                    var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);


                    // 1. Respond to Connect

                    var req1 = await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));

                    int transId1 = BinaryPrimitives.ReadInt32BigEndian(req1.AsSpan(12));


                    byte[] res1 = new byte[16];

                    BinaryPrimitives.WriteInt32BigEndian(res1.AsSpan(0), 0);

                    BinaryPrimitives.WriteInt32BigEndian(res1.AsSpan(4), transId1);

                    BinaryPrimitives.WriteInt64BigEndian(res1.AsSpan(8), 0x12345678);

                    _socketFactory.LastSocket.TriggerResponse(res1);


                    // 2. Respond to Announce

                    var req2 = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));

                    int transId2 = BinaryPrimitives.ReadInt32BigEndian(req2.AsSpan(12));


                    byte[] res2 = new byte[20 + 6];

                    BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(0), 1);

                    BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(4), transId2);

                    BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(8), 1800);

                    _socketFactory.LastSocket.TriggerResponse(res2);


                    await announceTask;


                    // Assert

                    Assert.True(_callback.Success);

                    Assert.Equal(1800u, _callback.AnnounceResponse?.Interval);

                }


    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_TransientFailure_RetriesAndSucceeds()
    {
        // Arrange
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        // 1. First Connect - Trigger timeout
        await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(5));
        await Task.Delay(50); // Ensure task reached await
        _socketFactory.LastSocket.TriggerTimeout();

        // 2. Wait for retry delay (1s)
        await Task.Delay(50);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));

        // 3. Second Connect attempt
        var req2 = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(5));
        int transId2 = BinaryPrimitives.ReadInt32BigEndian(req2.AsSpan(12));
        byte[] res2 = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(4), transId2);
        BinaryPrimitives.WriteInt64BigEndian(res2.AsSpan(8), 0x12345678);
        _socketFactory.LastSocket.TriggerResponse(res2);

        // 4. Respond to Announce
        var req3 = await _socketFactory.LastSocket.WaitForPacketAsync(2, TimeSpan.FromSeconds(5));
        int transId3 = BinaryPrimitives.ReadInt32BigEndian(req3.AsSpan(12));
        byte[] res3 = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(res3.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32BigEndian(res3.AsSpan(4), transId3);
        _socketFactory.LastSocket.TriggerResponse(res3);

        await announceTask;

        // Assert
        Assert.True(_callback.Success);
        Assert.True(_socketFactory.LastSocket.SentPackets.Count >= 2);
    }


                [Fact(Timeout = 30000)]

    public async Task ScrapeAsync_Succeeds()

    {

                    // Arrange

                    var tracker = new UdpTracker(_timeProvider, _socketFactory);

                    tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);


                    var scrapeTask = tracker.ScrapeAsync(CancellationToken.None);


                    // 1. Respond to Connect

                    var req1 = await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));


                    int transId1 = BinaryPrimitives.ReadInt32BigEndian(req1.AsSpan(12));


                    byte[] res1 = new byte[16];


                    BinaryPrimitives.WriteInt32BigEndian(res1.AsSpan(0), 0);


                    BinaryPrimitives.WriteInt32BigEndian(res1.AsSpan(4), transId1);


                    BinaryPrimitives.WriteInt64BigEndian(res1.AsSpan(8), 0x12345678);


                    _socketFactory.LastSocket.TriggerResponse(res1);


                    // 2. Respond to Scrape

                    var req2 = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));

                    int transId2 = BinaryPrimitives.ReadInt32BigEndian(req2.AsSpan(12));


                    byte[] res2 = new byte[8 + 12];

                    BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(0), 2); // Action Scrape

                    BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(4), transId2);

                    BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(8), 10); // Seeders

                    BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(12), 50); // Completed

                    BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(16), 2); // Leechers

                    _socketFactory.LastSocket.TriggerResponse(res2);


                    await scrapeTask;


                    // Assert

                    Assert.True(_callback.Success);

                    Assert.NotNull(_callback.ScrapeResponse);

                    Assert.Equal(10u, _callback.ScrapeResponse.SeedCount);

                    Assert.Equal(50u, _callback.ScrapeResponse.Downloaded);

                Assert.Equal(2u, _callback.ScrapeResponse.LeechCount);

                }

    [Fact(Timeout = 30000)]
    public async Task ScrapeMultipleAsync_ReturnsAllResults()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        byte[] hash1 = InfoHash.CreateRandom().ToArray();
        byte[] hash2 = InfoHash.CreateRandom().ToArray();

        var scrapeTask = tracker.ScrapeMultipleAsync(new List<byte[]> { hash1, hash2 }, CancellationToken.None);

        // Connect response
        var req1 = await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        int transId1 = BinaryPrimitives.ReadInt32BigEndian(req1.AsSpan(12));

        byte[] res1 = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(res1.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(res1.AsSpan(4), transId1);
        BinaryPrimitives.WriteInt64BigEndian(res1.AsSpan(8), 0x12345678);
        _socketFactory.LastSocket.TriggerResponse(res1);

        // Scrape response for two hashes
        var req2 = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int transId2 = BinaryPrimitives.ReadInt32BigEndian(req2.AsSpan(12));

        byte[] res2 = new byte[8 + 12 * 2];
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(0), 2); // Action Scrape
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(4), transId2);

        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(8), 10);
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(12), 100);
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(16), 5);

        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(20), 3);
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(24), 30);
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(28), 7);

        _socketFactory.LastSocket.TriggerResponse(res2);

        var result = await scrapeTask;

        string key1 = Convert.ToHexString(hash1);
        string key2 = Convert.ToHexString(hash2);

        Assert.True(result.Results.ContainsKey(key1));
        Assert.True(result.Results.ContainsKey(key2));
        Assert.Equal(10u, result.Results[key1].SeedCount);
        Assert.Equal(100u, result.Results[key1].Downloaded);
        Assert.Equal(5u, result.Results[key1].LeechCount);
        Assert.Equal(3u, result.Results[key2].SeedCount);
        Assert.Equal(30u, result.Results[key2].Downloaded);
        Assert.Equal(7u, result.Results[key2].LeechCount);
    }

}






