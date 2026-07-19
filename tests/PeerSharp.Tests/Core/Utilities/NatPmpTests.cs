using PeerSharp.Internals.Utilities;
using System.Net;
using System.Net.Sockets;

namespace PeerSharp.Tests.Core.Utilities;

public sealed class NatPmpTests
{
    [Fact]
    public async Task MapPortAsync_SucceedsWithValidResponse()
    {
        await using var server = new NatPmpTestServer(success: true, externalPort: 5555);
        var mapper = new NatPmpPortMapping(() => [IPAddress.Loopback], server.Port);

        await mapper.StartAsync(CancellationToken.None);

        bool result = await mapper.MapPortAsync(1234, "UDP", "test", CancellationToken.None);
        Assert.True(result);

        var status = mapper.GetStatus();
        Assert.Single(status);
        Assert.Equal(PortMappingResult.Success, status[0].Result);
        Assert.Equal(5555, status[0].ExternalPort);
    }

    [Fact]
    public async Task MapPortAsync_FailsOnErrorResponse()
    {
        await using var server = new NatPmpTestServer(success: false, externalPort: null);
        var mapper = new NatPmpPortMapping(() => [IPAddress.Loopback], server.Port);

        await mapper.StartAsync(CancellationToken.None);

        bool result = await mapper.MapPortAsync(1234, "TCP", "test", CancellationToken.None);
        Assert.False(result);

        var status = mapper.GetStatus();
        Assert.Single(status);
        Assert.Equal(PortMappingResult.Failed, status[0].Result);
    }

    [Fact]
    public async Task GetStatus_NoGateways_ReturnsFailed()
    {
        var mapper = new NatPmpPortMapping(() => [], 5351);
        await mapper.StartAsync(CancellationToken.None);

        var status = mapper.GetStatus();
        Assert.Single(status);
        Assert.Equal(PortMappingResult.Failed, status[0].Result);
        Assert.Equal("No gateways discovered", status[0].ErrorMessage);
    }

    [Fact]
    public async Task MapPortAsync_TcpProtocol_SendsCorrectOpCode()
    {
        var received = new List<byte[]>();
        await using var server = new NatPmpTestServer(success: true, externalPort: 7777, captureRequests: received);
        var mapper = new NatPmpPortMapping(() => [IPAddress.Loopback], server.Port);

        await mapper.StartAsync(CancellationToken.None);
        bool result = await mapper.MapPortAsync(9000, "TCP", "test", CancellationToken.None);

        Assert.True(result);
        Assert.NotEmpty(received);
        Assert.Equal(2, received[0][1]); // TCP opCode = 2
    }

    [Fact(Timeout = 10000)]
    public async Task UnmapAllAsync_AfterMapping_SendsUnmapPackets()
    {
        var received = new List<byte[]>();
        await using var server = new NatPmpTestServer(success: true, externalPort: 4444, captureRequests: received);
        var mapper = new NatPmpPortMapping(() => [IPAddress.Loopback], server.Port);

        await mapper.StartAsync(CancellationToken.None);
        await mapper.MapPortAsync(1234, "UDP", "test", CancellationToken.None);

        int packetsBefore = received.Count;
        await mapper.UnmapAllAsync(CancellationToken.None);

        // Wait for the server to receive the unmap packet (fire-and-forget send).
        await server.WaitForPacketAsync(TimeSpan.FromSeconds(5));

        Assert.True(received.Count > packetsBefore);
        var unmapPacket = received[packetsBefore];
        // Unmap lifetime bytes 8-11 must all be zero.
        Assert.Equal(0, unmapPacket[8]);
        Assert.Equal(0, unmapPacket[9]);
        Assert.Equal(0, unmapPacket[10]);
        Assert.Equal(0, unmapPacket[11]);
    }

    [Fact]
    public async Task UnmapAllAsync_NoMappings_CompletesWithoutSendingPackets()
    {
        await using var server = new NatPmpTestServer(success: true, externalPort: 4444);
        var mapper = new NatPmpPortMapping(() => [IPAddress.Loopback], server.Port);
        await mapper.StartAsync(CancellationToken.None);

        // No mappings registered — should complete silently.
        await mapper.UnmapAllAsync(CancellationToken.None);
    }

    [Fact(Timeout = 10000)]
    public async Task UnmapAllAsync_ClearsInternalMappings_SubsequentCallSendsNothing()
    {
        var received = new List<byte[]>();
        await using var server = new NatPmpTestServer(success: true, externalPort: 5000, captureRequests: received);
        var mapper = new NatPmpPortMapping(() => [IPAddress.Loopback], server.Port);

        await mapper.StartAsync(CancellationToken.None);
        await mapper.MapPortAsync(1234, "UDP", "test", CancellationToken.None);
        int packetsBeforeUnmap = server.CapturedCount;
        await mapper.UnmapAllAsync(CancellationToken.None);
        await server.WaitForPacketCountAsync(packetsBeforeUnmap + 1, TimeSpan.FromSeconds(5));

        int countAfterFirst = server.CapturedCount;
        await mapper.UnmapAllAsync(CancellationToken.None);

        // Brief wait to confirm nothing arrived.
        await Task.Delay(100);
        Assert.Equal(countAfterFirst, server.CapturedCount);
    }

    private sealed class NatPmpTestServer : IAsyncDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopTask;
        private readonly bool _success;
        private readonly int? _externalPort;
        private readonly List<byte[]>? _captureRequests;
        private readonly Lock _captureLock = new();
        private readonly SemaphoreSlim _packetSignal = new(0);

        public NatPmpTestServer(bool success, int? externalPort, List<byte[]>? captureRequests = null)
        {
            _success = success;
            _externalPort = externalPort;
            _captureRequests = captureRequests;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _loopTask = Task.Run(ReceiveLoopAsync);
        }

        public int Port { get; }

        public int CapturedCount
        {
            get
            {
                lock (_captureLock)
                {
                    return _captureRequests?.Count ?? 0;
                }
            }
        }

        public Task WaitForPacketAsync(TimeSpan timeout) =>
            _packetSignal.WaitAsync(timeout).ContinueWith(_ => { });

        public async Task WaitForPacketCountAsync(int count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (CapturedCount < count)
            {
                await _packetSignal.WaitAsync(cts.Token).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _udp.Dispose();
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch
            {
                // Best-effort shutdown.
            }
            _cts.Dispose();
            _packetSignal.Dispose();
        }

        private async Task ReceiveLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                lock (_captureLock)
                {
                    _captureRequests?.Add(result.Buffer);
                }
                _packetSignal.Release();

                // Mapping requests need a response; unmap packets (lifetime=0) don't wait for one.
                bool isUnmap = result.Buffer.Length >= 12 &&
                               result.Buffer[8] == 0 && result.Buffer[9] == 0 &&
                               result.Buffer[10] == 0 && result.Buffer[11] == 0;
                if (!isUnmap)
                {
                    var response = BuildResponse(result.Buffer);
                    await _udp.SendAsync(response, result.RemoteEndPoint).ConfigureAwait(false);
                }
            }
        }

        private byte[] BuildResponse(byte[] request)
        {
            byte opCode = request.Length > 1 ? request[1] : (byte)0x01;
            var response = new byte[12];
            response[0] = 0;
            response[1] = (byte)(128 + opCode);
            if (_success)
            {
                response[2] = 0;
                response[3] = 0;
                int port = _externalPort ?? 0;
                response[8] = (byte)(port >> 8);
                response[9] = (byte)(port & 0xFF);
            }
            else
            {
                response[2] = 0;
                response[3] = 1;
            }
            return response;
        }
    }
}




