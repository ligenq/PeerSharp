using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Trackers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.Tests.Core.Trackers;

public class UdpTrackerTests
{
    private class MockUdpSocket : IUdpSocket
    {
        public List<byte[]> SentPackets { get; } = [];
        public bool Closed { get; private set; }
        private TaskCompletionSource<UdpReceiveResult>? _receiveTcs;
        private readonly Queue<UdpReceiveResult> _queuedResponses = new();
        private readonly List<TaskCompletionSource<byte[]>> _sentPacketWaiters = [];

        public Socket Client => throw new NotImplementedException();

        public void TriggerResponse(byte[] data)
        {
            var response = new UdpReceiveResult(data, new IPEndPoint(IPAddress.Loopback, 0));
            var tcs = Interlocked.Exchange(ref _receiveTcs, null);
            if (tcs != null)
            {
                tcs.TrySetResult(response);
                return;
            }

            lock (_queuedResponses)
            {
                _queuedResponses.Enqueue(response);
            }
        }

        public void TriggerTimeout()
        {
            var tcs = Interlocked.Exchange(ref _receiveTcs, null);
            tcs?.TrySetException(new TimeoutException());
        }

        public void TriggerSocketException(SocketError error = SocketError.ConnectionReset)
        {
            var tcs = Interlocked.Exchange(ref _receiveTcs, null);
            tcs?.TrySetException(new SocketException((int)error));
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
            lock (_queuedResponses)
            {
                if (_queuedResponses.TryDequeue(out var response))
                {
                    return Task.FromResult(response);
                }
            }

            var tcs = new TaskCompletionSource<UdpReceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _receiveTcs, tcs);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
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
        public MultiScrapeResponse? MultiScrapeResponse { get; private set; }
        public string? AnnounceErrorMessage { get; private set; }
        public void OnAnnounceResult(bool success, AnnounceResponse response, ITracker tracker, string? errorMessage = null)
        {
            Success = success;
            AnnounceResponse = response;
            AnnounceErrorMessage = errorMessage;
        }

        public void OnMultiScrapeResult(bool success, MultiScrapeResponse response, ITracker tracker)
        {
            Success = success;
            MultiScrapeResponse = response;
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
        await Task.Delay(50, TestContext.Current.CancellationToken); // Ensure task reached await
        _socketFactory.LastSocket.TriggerTimeout();

        // 2. Wait for retry delay (1s)
        await Task.Delay(50, TestContext.Current.CancellationToken);
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

        var scrapeTask = tracker.ScrapeMultipleAsync([hash1, hash2], CancellationToken.None);

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

        byte[] res2 = new byte[8 + (12 * 2)];
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

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_TrackerErrorResponse_SurfacesErrorMessage()
    {
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

        // 2. Respond to Announce with an error (action=3) carrying a short ASCII message.
        // BEP 15: Error response is shorter than a 20-byte announce response; the tracker
        // error path must accept it rather than raising "Response too short".
        var req2 = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int transId2 = BinaryPrimitives.ReadInt32BigEndian(req2.AsSpan(12));

        const string errorText = "torrent not registered";
        byte[] errorBytes = Encoding.ASCII.GetBytes(errorText);
        byte[] res2 = new byte[8 + errorBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(0), 3); // Action Error
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(4), transId2);
        errorBytes.CopyTo(res2.AsSpan(8));
        _socketFactory.LastSocket.TriggerResponse(res2);

        await announceTask;

        Assert.False(_callback.Success);
        Assert.NotNull(_callback.AnnounceErrorMessage);
        Assert.Contains(errorText, _callback.AnnounceErrorMessage);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_TrackerErrorWithNoBody_SurfacesPlaceholder()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        var req1 = await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        int transId1 = BinaryPrimitives.ReadInt32BigEndian(req1.AsSpan(12));
        byte[] res1 = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(res1.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(res1.AsSpan(4), transId1);
        BinaryPrimitives.WriteInt64BigEndian(res1.AsSpan(8), 0x12345678);
        _socketFactory.LastSocket.TriggerResponse(res1);

        var req2 = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int transId2 = BinaryPrimitives.ReadInt32BigEndian(req2.AsSpan(12));

        // 8-byte header only - no error body
        byte[] res2 = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(0), 3);
        BinaryPrimitives.WriteInt32BigEndian(res2.AsSpan(4), transId2);
        _socketFactory.LastSocket.TriggerResponse(res2);

        await announceTask;

        Assert.False(_callback.Success);
        Assert.NotNull(_callback.AnnounceErrorMessage);
        Assert.Contains("no error message", _callback.AnnounceErrorMessage);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_IgnoresStaleTransactionResponseAndAcceptsMatchingResponse()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        var connectRequest = await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        int connectTransactionId = BinaryPrimitives.ReadInt32BigEndian(connectRequest.AsSpan(12));
        byte[] connectResponse = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(connectResponse.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(connectResponse.AsSpan(4), connectTransactionId);
        BinaryPrimitives.WriteInt64BigEndian(connectResponse.AsSpan(8), 0x12345678);
        _socketFactory.LastSocket.TriggerResponse(connectResponse);

        var announceRequest = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int announceTransactionId = BinaryPrimitives.ReadInt32BigEndian(announceRequest.AsSpan(12));

        byte[] staleResponse = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(staleResponse.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32BigEndian(staleResponse.AsSpan(4), announceTransactionId + 1);
        _socketFactory.LastSocket.TriggerResponse(staleResponse);

        byte[] validResponse = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(validResponse.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32BigEndian(validResponse.AsSpan(4), announceTransactionId);
        BinaryPrimitives.WriteInt32BigEndian(validResponse.AsSpan(8), 1234);
        BinaryPrimitives.WriteInt32BigEndian(validResponse.AsSpan(12), 2);
        BinaryPrimitives.WriteInt32BigEndian(validResponse.AsSpan(16), 5);
        _socketFactory.LastSocket.TriggerResponse(validResponse);

        await announceTask;

        Assert.True(_callback.Success);
        Assert.Equal(1234u, _callback.AnnounceResponse?.Interval);
        Assert.Equal(2u, _callback.AnnounceResponse?.LeechCount);
        Assert.Equal(5u, _callback.AnnounceResponse?.SeedCount);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_InvalidAnnounceAction_FailsCallback()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        var connectRequest = await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        int connectTransactionId = BinaryPrimitives.ReadInt32BigEndian(connectRequest.AsSpan(12));
        byte[] connectResponse = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(connectResponse.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(connectResponse.AsSpan(4), connectTransactionId);
        BinaryPrimitives.WriteInt64BigEndian(connectResponse.AsSpan(8), 0x12345678);
        _socketFactory.LastSocket.TriggerResponse(connectResponse);

        var announceRequest = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int announceTransactionId = BinaryPrimitives.ReadInt32BigEndian(announceRequest.AsSpan(12));

        byte[] invalidResponse = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(invalidResponse.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32BigEndian(invalidResponse.AsSpan(4), announceTransactionId);
        _socketFactory.LastSocket.TriggerResponse(invalidResponse);

        await announceTask;

        Assert.False(_callback.Success);
        Assert.Contains("Invalid announce action", _callback.AnnounceErrorMessage);
    }

    [Fact(Timeout = 30000)]
    public async Task ScrapeMultipleAsync_LimitsRequestToUdpTrackerMaximumHashCount()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);
        var hashes = Enumerable.Range(0, 80)
            .Select(_ => InfoHash.CreateRandom().ToArray())
            .ToList();

        var scrapeTask = tracker.ScrapeMultipleAsync(hashes, CancellationToken.None);

        var connectRequest = await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        int connectTransactionId = BinaryPrimitives.ReadInt32BigEndian(connectRequest.AsSpan(12));
        byte[] connectResponse = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(connectResponse.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(connectResponse.AsSpan(4), connectTransactionId);
        BinaryPrimitives.WriteInt64BigEndian(connectResponse.AsSpan(8), 0x12345678);
        _socketFactory.LastSocket.TriggerResponse(connectResponse);

        var scrapeRequest = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int scrapeTransactionId = BinaryPrimitives.ReadInt32BigEndian(scrapeRequest.AsSpan(12));
        const int maxUdpScrapeHashes = 74;
        Assert.Equal(16 + (maxUdpScrapeHashes * 20), scrapeRequest.Length);

        byte[] scrapeResponse = new byte[8 + (maxUdpScrapeHashes * 12)];
        BinaryPrimitives.WriteInt32BigEndian(scrapeResponse.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32BigEndian(scrapeResponse.AsSpan(4), scrapeTransactionId);
        for (int i = 0; i < maxUdpScrapeHashes; i++)
        {
            int offset = 8 + (i * 12);
            BinaryPrimitives.WriteInt32BigEndian(scrapeResponse.AsSpan(offset), i + 1);
            BinaryPrimitives.WriteInt32BigEndian(scrapeResponse.AsSpan(offset + 4), i + 2);
            BinaryPrimitives.WriteInt32BigEndian(scrapeResponse.AsSpan(offset + 8), i + 3);
        }
        _socketFactory.LastSocket.TriggerResponse(scrapeResponse);

        var response = await scrapeTask;

        Assert.Equal(maxUdpScrapeHashes, response.Results.Count);
        Assert.False(response.Results.ContainsKey(Convert.ToHexString(hashes[74])));
        Assert.Equal(1u, response.Results[Convert.ToHexString(hashes[0])].SeedCount);
        Assert.Equal(76u, response.Results[Convert.ToHexString(hashes[73])].LeechCount);
    }

    [Fact]
    public async Task ScrapeMultipleAsync_EmptyInput_ReturnsEmptyWithoutSendingPacket()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var response = await tracker.ScrapeMultipleAsync([], CancellationToken.None);

        Assert.Empty(response.Results);
        Assert.Empty(_socketFactory.LastSocket.SentPackets);
    }

    [Fact]
    public async Task MultiScrapeAsync_V2OnlyHashes_ReturnsWithoutSendingPacket()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        await tracker.MultiScrapeAsync([InfoHash.CreateRandomV2()], CancellationToken.None);

        Assert.Null(_callback.MultiScrapeResponse);
        Assert.Empty(_socketFactory.LastSocket.SentPackets);
    }

    [Fact]
    public void ParseTrackerErrorMessage_ExtractsAsciiMessage()
    {
        const string errorText = "torrent not registered";
        byte[] errorBytes = Encoding.ASCII.GetBytes(errorText);
        byte[] buffer = new byte[8 + errorBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0), 3);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4), unchecked((int)0xDEADBEEF));
        errorBytes.CopyTo(buffer.AsSpan(8));

        var result = UdpTracker.ParseTrackerErrorMessage(buffer);

        Assert.Equal(errorText, result);
    }

    [Fact]
    public void ParseTrackerErrorMessage_TrimsTrailingNulls()
    {
        byte[] buffer = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0), 3);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4), 1);
        Encoding.ASCII.GetBytes("BAD").CopyTo(buffer.AsSpan(8));
        // bytes 11..15 remain zero

        var result = UdpTracker.ParseTrackerErrorMessage(buffer);

        Assert.Equal("BAD", result);
    }

    [Fact]
    public void ParseTrackerErrorMessage_HeaderOnly_ReturnsPlaceholder()
    {
        byte[] buffer = new byte[8];

        var result = UdpTracker.ParseTrackerErrorMessage(buffer);

        Assert.Equal("(no error message)", result);
    }

    [Fact]
    public void ParseTrackerErrorMessage_EmptyBody_ReturnsPlaceholder()
    {
        byte[] buffer = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0), 3);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4), 1);

        var result = UdpTracker.ParseTrackerErrorMessage(buffer);

        Assert.Equal("(empty error message)", result);
    }

    // ---------- Lifecycle ----------

    [Fact]
    public void SetRequestTimeout_OverridesDefault()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        tracker.SetRequestTimeout(TimeSpan.FromMilliseconds(250));

        Assert.Equal(TimeSpan.FromMilliseconds(250), tracker._requestTimeout);
    }

    [Fact(Timeout = 30000)]
    public async Task Deinit_ResetsConnectionStateSoNextAnnounceReconnects()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var first = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0xCAFE);
        await CompleteAnnounceAsync(packetIndex: 1);
        await first;

        tracker.Deinit();
        Assert.True(_socketFactory.LastSocket.Closed);

        var second = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        // After Deinit, the next announce must do a fresh Connect (not reuse the cached id),
        // so we expect packet index 2 to be a Connect (action=0), not an Announce (action=1).
        var nextPacket = await _socketFactory.LastSocket.WaitForPacketAsync(2, TimeSpan.FromSeconds(2));
        int action = BinaryPrimitives.ReadInt32BigEndian(nextPacket.AsSpan(8));
        Assert.Equal(0, action);

        await CompleteConnectAtIndexAsync(packetIndex: 2, connectionId: 0xBEEF);
        await CompleteAnnounceAsync(packetIndex: 3);
        await second;
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        tracker.Dispose();
        tracker.Dispose();
    }

    // ---------- Connection ID caching (BEP 15: 60s lifetime) ----------

    [Fact(Timeout = 30000)]
    public async Task AnnounceTwice_WithinConnectionIdLifetime_SkipsSecondConnect()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var first = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0x1111_2222_3333_4444);
        await CompleteAnnounceAsync(packetIndex: 1);
        await first;

        // Stay well inside the 60s lifetime
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        var second = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        var pkt = await _socketFactory.LastSocket.WaitForPacketAsync(2, TimeSpan.FromSeconds(2));
        // Expect another Announce, not a Connect
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(8)));
        // And the connection id from the cache
        Assert.Equal(0x1111_2222_3333_4444, BinaryPrimitives.ReadInt64BigEndian(pkt.AsSpan(0)));
        await CompleteAnnounceAtIndexAsync(packetIndex: 2);
        await second;

        Assert.Equal(3, _socketFactory.LastSocket.SentPackets.Count);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceTwice_AfterConnectionIdExpiry_ReconnectsAndGetsFreshId()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var first = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0xAAAA);
        await CompleteAnnounceAsync(packetIndex: 1);
        await first;

        // Push past the 60s connection-id lifetime
        _timeProvider.Advance(TimeSpan.FromSeconds(61));

        var second = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        var pkt = await _socketFactory.LastSocket.WaitForPacketAsync(2, TimeSpan.FromSeconds(2));
        // Must be a fresh Connect
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(8)));

        await CompleteConnectAtIndexAsync(packetIndex: 2, connectionId: 0xBBBB);
        await CompleteAnnounceAsync(packetIndex: 3);
        await second;
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceThenScrape_ReusesConnectionId()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announce = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0xDEAD_BEEF);
        await CompleteAnnounceAsync(packetIndex: 1);
        await announce;

        var scrape = tracker.ScrapeAsync(CancellationToken.None);
        var pkt = await _socketFactory.LastSocket.WaitForPacketAsync(2, TimeSpan.FromSeconds(2));
        // Scrape uses action=2; connect would be action=0
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(8)));
        Assert.Equal(0xDEAD_BEEF, BinaryPrimitives.ReadInt64BigEndian(pkt.AsSpan(0)));

        int transId = BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(12));
        byte[] response = new byte[8 + 12];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        _socketFactory.LastSocket.TriggerResponse(response);
        await scrape;

        Assert.True(_callback.Success);
    }

    // ---------- Announce request format (BEP 15) ----------

    [Theory]
    [InlineData((int)TrackerEvent.None, 0)]
    [InlineData((int)TrackerEvent.Completed, 1)]
    [InlineData((int)TrackerEvent.Started, 2)]
    [InlineData((int)TrackerEvent.Stopped, 3)]
    public async Task AnnounceAsync_TrackerEvent_EncodesCorrectEventId(int evt, int expectedEventId)
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync((TrackerEvent)evt, CancellationToken.None);
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0x1234);

        var announceRequest = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        Assert.Equal(98, announceRequest.Length);
        Assert.Equal(expectedEventId, BinaryPrimitives.ReadInt32BigEndian(announceRequest.AsSpan(80)));

        await CompleteAnnounceAtIndexAsync(packetIndex: 1);
        await announceTask;
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_PacketLayoutMatchesBep15()
    {
        // Configure non-default values so the assertions actually exercise the encoder.
        var peerId = new byte[20];
        for (int i = 0; i < peerId.Length; i++)
        {
            peerId[i] = (byte)(0x40 + i);
        }

        _torrent.Settings.PeerId = peerId;
        _torrent.Settings.Connection.TcpPort = 12345;

        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.Started, CancellationToken.None);
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0x0102_0304_0506_0708);

        var pkt = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));

        Assert.Equal(98, pkt.Length);
        Assert.Equal(0x0102_0304_0506_0708, BinaryPrimitives.ReadInt64BigEndian(pkt.AsSpan(0)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(8))); // action = announce
        // bytes 12..15 = transaction id (random, just check non-zero across multiple bytes)
        Assert.Equal(InfoHash.Empty.Span.ToArray(), pkt.AsSpan(16, 20).ToArray());
        Assert.Equal(peerId, pkt.AsSpan(36, 20).ToArray());
        Assert.Equal(_torrent.FileTransfer.Downloaded, BinaryPrimitives.ReadInt64BigEndian(pkt.AsSpan(56)));
        Assert.Equal(_torrent.DataLeft, BinaryPrimitives.ReadInt64BigEndian(pkt.AsSpan(64)));
        Assert.Equal(_torrent.FileTransfer.Uploaded, BinaryPrimitives.ReadInt64BigEndian(pkt.AsSpan(72)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(80))); // event = started
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(84))); // ip default
        Assert.Equal(12345, BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(96)));

        await CompleteAnnounceAtIndexAsync(packetIndex: 1);
        await announceTask;
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_NumWantZero_EncodesNegativeOne()
    {
        _torrent.Settings.MaxPeersPerTrackerRequest = 0;

        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0x1234);

        var pkt = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32BigEndian(pkt.AsSpan(92))); // numwant

        await CompleteAnnounceAtIndexAsync(packetIndex: 1);
        await announceTask;
    }

    // ---------- Announce response edge cases ----------

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_ParsesIPv4Peers()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0x1234);

        var req = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));

        // 2 IPv4 peers: 1.2.3.4:5678 and 9.10.11.12:6789
        byte[] response = new byte[20 + 12];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(8), 1800); // interval
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(12), 3);   // leechers
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(16), 7);   // seeders
        // Peer 1
        response.AsSpan(20)[0] = 1; response.AsSpan(20)[1] = 2; response.AsSpan(20)[2] = 3; response.AsSpan(20)[3] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(24), 5678);
        // Peer 2
        response.AsSpan(26)[0] = 9; response.AsSpan(26)[1] = 10; response.AsSpan(26)[2] = 11; response.AsSpan(26)[3] = 12;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(30), 6789);
        _socketFactory.LastSocket.TriggerResponse(response);

        await announceTask;

        Assert.True(_callback.Success);
        Assert.Equal(2, _callback.AnnounceResponse?.Peers.Count);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 5678), _callback.AnnounceResponse?.Peers[0]);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("9.10.11.12"), 6789), _callback.AnnounceResponse?.Peers[1]);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_PartialPeerEntry_IgnoredCleanly()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0x1234);

        var req = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));

        // Header (20) + 1 full IPv4 peer (6) + a trailing 4-byte stub
        byte[] response = new byte[20 + 6 + 4];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        response.AsSpan(20)[0] = 10; response.AsSpan(20)[1] = 0; response.AsSpan(20)[2] = 0; response.AsSpan(20)[3] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(24), 9001);
        _socketFactory.LastSocket.TriggerResponse(response);

        await announceTask;

        Assert.True(_callback.Success);
        Assert.Single(_callback.AnnounceResponse!.Peers);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_AnnounceResponseTooShort_FailsCallback()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0x1234);

        var req = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));

        // 12-byte announce response: action=1 (announce), trans id matches, but body too short.
        // ReceiveSpecificTransactionAsync requires minSize=20 for action!=3, so this triggers
        // the "Response too short" InvalidDataException path.
        byte[] response = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        _socketFactory.LastSocket.TriggerResponse(response);

        await announceTask;

        Assert.False(_callback.Success);
        Assert.NotNull(_callback.AnnounceErrorMessage);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_SocketExceptionDuringConnect_RetriesAndSucceeds()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        // attempt 0: connect packet sent, then receive errors out with SocketException
        await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _socketFactory.LastSocket.TriggerSocketException(SocketError.ConnectionReset);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));

        // attempt 1: succeed
        await CompleteConnectAsync(packetIndex: 1, connectionId: 0xFEED);
        await CompleteAnnounceAsync(packetIndex: 2);

        await announceTask;
        Assert.True(_callback.Success);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_AllRetriesExhausted_RaisesFailureCallback()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        int[] retryDelaysMs = [1000, 2000, 4000];

        // 4 attempts (initial + 3 retries), all timing out on the Connect packet.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await _socketFactory.LastSocket.WaitForPacketAsync(attempt, TimeSpan.FromSeconds(5));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            _socketFactory.LastSocket.TriggerTimeout();

            if (attempt < 3)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                _timeProvider.Advance(TimeSpan.FromMilliseconds(retryDelaysMs[attempt]));
            }
        }

        await announceTask;

        Assert.False(_callback.Success);
        Assert.NotNull(_callback.AnnounceErrorMessage);
        Assert.Equal(4, _socketFactory.LastSocket.SentPackets.Count);
    }

    // ---------- Connect-phase errors ----------

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_ConnectErrorAction_FailsImmediatelyWithMessage()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        var req = await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));

        const string errText = "tracker is down";
        byte[] errBytes = Encoding.ASCII.GetBytes(errText);
        byte[] response = new byte[8 + errBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 3); // action = error
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        errBytes.CopyTo(response.AsSpan(8));
        _socketFactory.LastSocket.TriggerResponse(response);

        await announceTask;

        Assert.False(_callback.Success);
        Assert.Contains(errText, _callback.AnnounceErrorMessage);
        // Non-transient => no retry
        Assert.Single(_socketFactory.LastSocket.SentPackets);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_ConnectInvalidAction_FailsCallback()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        var req = await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));

        // action=1 (announce) is invalid for a connect response
        byte[] response = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        BinaryPrimitives.WriteInt64BigEndian(response.AsSpan(8), 0xCAFE);
        _socketFactory.LastSocket.TriggerResponse(response);

        await announceTask;

        Assert.False(_callback.Success);
        Assert.Contains("expected action 0", _callback.AnnounceErrorMessage);
    }

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_ConnectStalePacketThenValid_Succeeds()
    {
        // The receive layer is shared between Connect and Announce; ensure a stale packet
        // with a different transaction id during Connect is ignored and a subsequent valid
        // packet is accepted.
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, CancellationToken.None);

        var req = await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));

        byte[] stale = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(stale.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(stale.AsSpan(4), transId + 999);
        BinaryPrimitives.WriteInt64BigEndian(stale.AsSpan(8), 0x1111);
        _socketFactory.LastSocket.TriggerResponse(stale);

        byte[] valid = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(valid.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(valid.AsSpan(4), transId);
        BinaryPrimitives.WriteInt64BigEndian(valid.AsSpan(8), 0x2222);
        _socketFactory.LastSocket.TriggerResponse(valid);

        await CompleteAnnounceAtIndexAsync(packetIndex: 1);
        await announceTask;

        Assert.True(_callback.Success);
    }

    // ---------- Scrape ----------

    [Fact(Timeout = 30000)]
    public async Task ScrapeAsync_TransientRetry_Succeeds()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var scrapeTask = tracker.ScrapeAsync(CancellationToken.None);

        // attempt 0: connect succeeds, then scrape times out
        await CompleteConnectAsync(packetIndex: 0, connectionId: 0x1234);
        await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _socketFactory.LastSocket.TriggerTimeout();

        await Task.Delay(50, TestContext.Current.CancellationToken);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));

        // attempt 1: fresh connect + scrape success
        await CompleteConnectAsync(packetIndex: 2, connectionId: 0x5678);

        var req = await _socketFactory.LastSocket.WaitForPacketAsync(3, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));
        byte[] response = new byte[8 + 12];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(8), 11);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(12), 22);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(16), 33);
        _socketFactory.LastSocket.TriggerResponse(response);

        await scrapeTask;

        Assert.True(_callback.Success);
        Assert.Equal(11u, _callback.ScrapeResponse?.SeedCount);
    }

    [Fact(Timeout = 30000)]
    public async Task ScrapeAsync_AllRetriesExhausted_RaisesFailureCallback()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var scrapeTask = tracker.ScrapeAsync(CancellationToken.None);

        int[] retryDelaysMs = [1000, 2000, 4000];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await _socketFactory.LastSocket.WaitForPacketAsync(attempt, TimeSpan.FromSeconds(5));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            _socketFactory.LastSocket.TriggerTimeout();

            if (attempt < 3)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                _timeProvider.Advance(TimeSpan.FromMilliseconds(retryDelaysMs[attempt]));
            }
        }

        await scrapeTask;

        Assert.False(_callback.Success);
        Assert.NotNull(_callback.ScrapeResponse);
    }

    // ---------- ScrapeMultipleAsync (public, returns response) ----------

    [Fact(Timeout = 30000)]
    public async Task ScrapeMultipleAsync_TransientRetry_Succeeds()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        byte[] hash = InfoHash.CreateRandom().ToArray();
        var scrapeTask = tracker.ScrapeMultipleAsync([hash], CancellationToken.None);

        // attempt 0: timeout on connect
        await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _socketFactory.LastSocket.TriggerTimeout();

        await Task.Delay(50, TestContext.Current.CancellationToken);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));

        // attempt 1: connect + scrape success
        await CompleteConnectAsync(packetIndex: 1, connectionId: 0x9999);

        var req = await _socketFactory.LastSocket.WaitForPacketAsync(2, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));
        byte[] response = new byte[8 + 12];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(8), 7);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(12), 8);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(16), 9);
        _socketFactory.LastSocket.TriggerResponse(response);

        var result = await scrapeTask;

        Assert.Equal(7u, result.Results[Convert.ToHexString(hash)].SeedCount);
    }

    [Fact(Timeout = 30000)]
    public async Task ScrapeMultipleAsync_AllRetriesExhausted_ReturnsEmpty()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        byte[] hash = InfoHash.CreateRandom().ToArray();
        var scrapeTask = tracker.ScrapeMultipleAsync([hash], CancellationToken.None);

        int[] retryDelaysMs = [1000, 2000, 4000];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await _socketFactory.LastSocket.WaitForPacketAsync(attempt, TimeSpan.FromSeconds(5));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            _socketFactory.LastSocket.TriggerTimeout();
            if (attempt < 3)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                _timeProvider.Advance(TimeSpan.FromMilliseconds(retryDelaysMs[attempt]));
            }
        }

        var result = await scrapeTask;

        Assert.Empty(result.Results);
    }

    // ---------- MultiScrapeAsync (callback-based override) ----------

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_FullCycle_RaisesCallbackWithResults()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var hashes = new[]
        {
            InfoHash.CreateRandom(),
            InfoHash.CreateRandom()
        };
        var scrapeTask = tracker.MultiScrapeAsync(hashes, CancellationToken.None);

        await CompleteConnectAsync(packetIndex: 0, connectionId: 0xABCD);

        var req = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));
        byte[] response = new byte[8 + (12 * 2)];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(8), 4);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(12), 5);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(16), 6);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(20), 40);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(24), 50);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(28), 60);
        _socketFactory.LastSocket.TriggerResponse(response);

        await scrapeTask;

        Assert.True(_callback.Success);
        Assert.NotNull(_callback.MultiScrapeResponse);
        Assert.Equal(2, _callback.MultiScrapeResponse!.Results.Count);
        var key0 = Convert.ToHexString(hashes[0].Span.ToArray());
        Assert.Equal(4u, _callback.MultiScrapeResponse.Results[key0].SeedCount);
    }

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_AllRetriesExhausted_RaisesFailureCallback()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var hashes = new[] { InfoHash.CreateRandom() };
        var scrapeTask = tracker.MultiScrapeAsync(hashes, CancellationToken.None);

        int[] retryDelaysMs = [1000, 2000, 4000];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await _socketFactory.LastSocket.WaitForPacketAsync(attempt, TimeSpan.FromSeconds(5));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            _socketFactory.LastSocket.TriggerTimeout();
            if (attempt < 3)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                _timeProvider.Advance(TimeSpan.FromMilliseconds(retryDelaysMs[attempt]));
            }
        }

        await scrapeTask;

        Assert.False(_callback.Success);
        Assert.NotNull(_callback.MultiScrapeResponse);
        Assert.Empty(_callback.MultiScrapeResponse!.Results);
    }

    [Fact(Timeout = 30000)]
    public async Task MultiScrapeAsync_TrackerErrorAction_RaisesFailureCallback()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        var hashes = new[] { InfoHash.CreateRandom() };
        var scrapeTask = tracker.MultiScrapeAsync(hashes, CancellationToken.None);

        await CompleteConnectAsync(packetIndex: 0, connectionId: 0x1234);

        var req = await _socketFactory.LastSocket.WaitForPacketAsync(1, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));

        byte[] errBytes = Encoding.ASCII.GetBytes("rejected");
        byte[] response = new byte[8 + errBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 3);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        errBytes.CopyTo(response.AsSpan(8));
        _socketFactory.LastSocket.TriggerResponse(response);

        await scrapeTask;

        Assert.False(_callback.Success);
        Assert.NotNull(_callback.MultiScrapeResponse);
        // Non-transient error => no retry loop
        Assert.Equal(2, _socketFactory.LastSocket.SentPackets.Count);
    }

    [Fact]
    public async Task MultiScrapeAsync_NoV1Hashes_NoOp()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        // Empty list short-circuits before the lock; this hits the early-return path.
        await tracker.MultiScrapeAsync(Array.Empty<InfoHash>(), CancellationToken.None);

        Assert.Empty(_socketFactory.LastSocket.SentPackets);
        Assert.Null(_callback.MultiScrapeResponse);
    }

    // ---------- Cancellation ----------

    [Fact(Timeout = 30000)]
    public async Task AnnounceAsync_CancellationToken_PropagatesCancellation()
    {
        var tracker = new UdpTracker(_timeProvider, _socketFactory);
        tracker.Init("udp://127.0.0.1:80/announce", _torrent, _callback);

        using var cts = new CancellationTokenSource();
        var announceTask = tracker.AnnounceAsync(TrackerEvent.None, cts.Token);

        // Wait until the connect packet is in flight, then cancel.
        await _socketFactory.LastSocket.WaitForPacketAsync(0, TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => announceTask);
    }

    // ---------- Helpers ----------

    private async Task CompleteConnectAsync(int packetIndex, long connectionId)
    {
        var req = await _socketFactory.LastSocket.WaitForPacketAsync(packetIndex, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));

        byte[] response = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        BinaryPrimitives.WriteInt64BigEndian(response.AsSpan(8), connectionId);
        _socketFactory.LastSocket.TriggerResponse(response);
    }

    private Task CompleteConnectAtIndexAsync(int packetIndex, long connectionId)
        => CompleteConnectAsync(packetIndex, connectionId);

    private async Task CompleteAnnounceAsync(int packetIndex)
    {
        var req = await _socketFactory.LastSocket.WaitForPacketAsync(packetIndex, TimeSpan.FromSeconds(2));
        int transId = BinaryPrimitives.ReadInt32BigEndian(req.AsSpan(12));

        byte[] response = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), transId);
        _socketFactory.LastSocket.TriggerResponse(response);
    }

    private Task CompleteAnnounceAtIndexAsync(int packetIndex) => CompleteAnnounceAsync(packetIndex);
}






