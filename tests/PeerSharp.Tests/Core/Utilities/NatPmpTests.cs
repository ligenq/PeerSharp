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
        var mapper = new NatPmpPortMapping(() => new[] { IPAddress.Loopback }, server.Port);

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
        var mapper = new NatPmpPortMapping(() => new[] { IPAddress.Loopback }, server.Port);

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
        var mapper = new NatPmpPortMapping(() => Enumerable.Empty<IPAddress>(), 5351);
        await mapper.StartAsync(CancellationToken.None);

        var status = mapper.GetStatus();
        Assert.Single(status);
        Assert.Equal(PortMappingResult.Failed, status[0].Result);
        Assert.Equal("No gateways discovered", status[0].ErrorMessage);
    }

    private sealed class NatPmpTestServer : IAsyncDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopTask;
        private readonly bool _success;
        private readonly int? _externalPort;

        public NatPmpTestServer(bool success, int? externalPort)
        {
            _success = success;
            _externalPort = externalPort;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _loopTask = Task.Run(ReceiveLoopAsync);
        }

        public int Port { get; }

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

                var response = BuildResponse(result.Buffer);
                await _udp.SendAsync(response, result.RemoteEndPoint).ConfigureAwait(false);
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




