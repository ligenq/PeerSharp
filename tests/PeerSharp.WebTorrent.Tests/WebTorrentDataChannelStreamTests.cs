using System.Threading.Channels;
using RtcForge;

namespace PeerSharp.WebTorrent.Tests;

public class WebTorrentDataChannelStreamTests
{
    [Fact(Timeout = 30000)]
    public async Task ReadAsync_ReturnsFramesAcrossMultipleReads()
    {
        var channel = new FakeWebRtcDataChannel("bittorrent");
        await using var stream = new WebTorrentDataChannelStream(channel);
        stream.Start();
        channel.EmitMessage(new byte[] { 1, 2, 3, 4, 5 });

        byte[] first = new byte[2];
        byte[] second = new byte[3];

        Assert.Equal(2, await stream.ReadAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal(3, await stream.ReadAsync(second, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 2 }, first);
        Assert.Equal(new byte[] { 3, 4, 5 }, second);
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_ReturnsZeroWhenChannelCompletes()
    {
        var channel = new FakeWebRtcDataChannel("bittorrent");
        await using var stream = new WebTorrentDataChannelStream(channel);
        stream.Start();
        await channel.DisposeAsync();

        byte[] buffer = new byte[4];
        int read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(0, read);
    }

    [Fact(Timeout = 30000)]
    public async Task WriteAsync_SendsPayloadToChannel()
    {
        var channel = new FakeWebRtcDataChannel("bittorrent");
        await using var stream = new WebTorrentDataChannelStream(channel);

        await stream.WriteAsync(new byte[] { 9, 8, 7 }, TestContext.Current.CancellationToken);
        await stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, 1, 2, TestContext.Current.CancellationToken);

        Assert.Collection(
            channel.SentPayloads,
            payload => Assert.Equal(new byte[] { 9, 8, 7 }, payload),
            payload => Assert.Equal(new byte[] { 2, 3 }, payload));
    }

    [Fact]
    public void StreamContract_ReportsCapabilitiesAndUnsupportedOperations()
    {
        using var stream = new WebTorrentDataChannelStream(new FakeWebRtcDataChannel("bittorrent"));

        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        stream.Flush();
        Assert.Same(Task.CompletedTask, stream.FlushAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OperationsAfterDispose_ThrowObjectDisposedException()
    {
        var channel = new FakeWebRtcDataChannel("bittorrent");
        var stream = new WebTorrentDataChannelStream(channel);
        stream.Dispose();

        Assert.False(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.True(channel.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await stream.ReadAsync(new byte[1], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await stream.WriteAsync(new byte[1], TestContext.Current.CancellationToken));
    }

    private sealed class FakeWebRtcDataChannel : IWebRtcDataChannel
    {
        private readonly Channel<ReadOnlyMemory<byte>> _messages = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly TaskCompletionSource _openTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeWebRtcDataChannel(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public bool Disposed { get; private set; }
        public RTCDataChannelState ReadyState => RTCDataChannelState.Open;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> Messages => _messages.Reader.ReadAllAsync();
        public List<byte[]> SentPayloads { get; } = new();

        public Task WaitUntilOpenAsync(CancellationToken cancellationToken = default) =>
            _openTcs.Task.WaitAsync(cancellationToken);

        public Stream AsStream() => throw new NotSupportedException();
        public void EmitMessage(byte[] payload) => _messages.Writer.TryWrite(payload);

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            SentPayloads.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
