using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using PeerSharp.Internals;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// How a connection's reads end. Closing the stream ends them, as end of input; they are not cancelled,
/// because a cancelled read can only say so by throwing - from our streams into System.IO.Pipelines on
/// every disconnect, which stops the debugger there for anyone with Just My Code on.
/// </summary>
public class ReceiveLoopShutdownTests
{
    private static readonly byte[] RemotePeerId = [.. Enumerable.Range(0, 20).Select(i => (byte)(200 + i))];

    [Fact(Timeout = 30000)]
    public async Task Closing_EndsTheReceiveLoopWithoutCancellingItsRead()
    {
        var torrent = PlaintextTorrent();
        var stream = new ScriptedPeerStream(PeerHandshake.Create(torrent.InfoFile.Info, RemotePeerId), endsReadsOnDispose: true);
        var peer = new PeerCommunication(torrent, new SilentListener(), TimeProvider.System);

        peer.Start(stream);
        await TorrentTestUtility.WaitUntilAsync(() => ReceiveLoopTaskOrNull(peer) != null && stream.WaitingReads > 0, because: "the receive loop reading after the handshake");
        await peer.CloseAsync();
        await ReceiveLoop(peer);

        Assert.False(stream.AReadWasCancelled);
    }

    [Fact(Timeout = 30000)]
    public async Task AReadThatClosingDoesNotEnd_IsCancelledAfterAGrace()
    {
        // Every transport ends its reads when disposed, but a stream that did not would otherwise hold
        // the loop forever.
        var clock = new FakeTimeProvider();
        var torrent = PlaintextTorrent();
        var stream = new ScriptedPeerStream(PeerHandshake.Create(torrent.InfoFile.Info, RemotePeerId), endsReadsOnDispose: false);
        var peer = new PeerCommunication(torrent, new SilentListener(), clock);

        peer.Start(stream);
        await TorrentTestUtility.WaitUntilAsync(() => ReceiveLoopTaskOrNull(peer) != null && stream.WaitingReads > 0, because: "the receive loop reading after the handshake");
        await peer.CloseAsync();
        var loop = ReceiveLoopTask(peer);
        Assert.False(loop.IsCompleted);

        await TorrentTestUtility.AdvanceUntilAsync(clock, () => loop.IsCompleted, PeerCommunication.StuckReadGrace);

        await loop;
        Assert.True(stream.AReadWasCancelled);
    }

    [Fact(Timeout = 30000)]
    public async Task APeerThatSaysNothing_IsDroppedByClosingTheConnection_NotByCancellingTheRead()
    {
        // The handshake's deadline closes the connection. The encryption handshake waits five seconds for
        // its first bytes.
        var torrent = TorrentTestUtility.CreateMinimal();
        var stream = new ScriptedPeerStream([], endsReadsOnDispose: true);
        var peer = new PeerCommunication(torrent, new SilentListener(), TimeProvider.System);

        peer.Start(stream);
        await TorrentTestUtility.WaitUntilAsync(() => peer.Connected == 0, timeoutMs: 20000, because: "the handshake giving up");

        Assert.False(stream.AReadWasCancelled);
    }

    private static Torrent PlaintextTorrent()
    {
        var torrent = TorrentTestUtility.CreateMinimal();
        torrent.Settings.Connection.Encryption = Encryption.Refuse;
        return torrent;
    }

    private static Task ReceiveLoop(PeerCommunication peer) => ReceiveLoopTask(peer).WaitAsync(TimeSpan.FromSeconds(10));

    private static Task ReceiveLoopTask(PeerCommunication peer) => ReceiveLoopTaskOrNull(peer)!;

    private static Task? ReceiveLoopTaskOrNull(PeerCommunication peer) =>
        (Task?)typeof(PeerCommunication).GetField("_receiveLoopTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(peer);

    /// <summary>Serves a handshake, then waits on every read until shortly after disposal (or, if told not to, forever).</summary>
    private sealed class ScriptedPeerStream(byte[] handshake, bool endsReadsOnDispose) : Stream
    {
        private readonly Lock _lock = new();
        private int _served;
        private TaskCompletionSource<int>? _pending;
        private int _waitingReads;
        private int _cancelled;

        public int WaitingReads => Volatile.Read(ref _waitingReads);

        public bool AReadWasCancelled => Volatile.Read(ref _cancelled) != 0;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<int> pending;
            lock (_lock)
            {
                if (_served < handshake.Length)
                {
                    int count = Math.Min(buffer.Length, handshake.Length - _served);
                    handshake.AsMemory(_served, count).CopyTo(buffer);
                    _served += count;
                    return count;
                }

                pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = pending;
            }

            Interlocked.Increment(ref _waitingReads);
            await using var registration = cancellationToken.Register(() =>
            {
                if (pending.TrySetCanceled(cancellationToken))
                {
                    Interlocked.Exchange(ref _cancelled, 1);
                }
            });
            return await pending.Task;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && endsReadsOnDispose)
            {
                // A moment later, as a socket's aborted receive completes: cancelling the read straight
                // after closing the stream, as the loop used to, gets there first.
                TaskCompletionSource<int>? pending;
                lock (_lock)
                {
                    pending = _pending;
                }

                _ = Task.Delay(200).ContinueWith(_ => pending?.TrySetResult(0), TaskScheduler.Default);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class SilentListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<System.Net.IPEndPoint> added, List<byte> addedFlags, List<System.Net.IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, System.Net.IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}
