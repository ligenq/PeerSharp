using BenchmarkDotNet.Attributes;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;
using System.Net;
using System.Reflection;

namespace PeerSharp.Benchmarks;

/// <summary>
/// uTP receive path. <c>UtpStream</c> is the second-largest file in the library and sits on a
/// per-packet path, but had no performance coverage at all.
///
/// Packet processing is stateful - sequence numbers advance, the reorder buffer fills - so it
/// cannot be measured one call at a time the way a pure function can. Each benchmark therefore
/// processes a whole window of packets against a stream rebuilt by
/// <see cref="IterationSetup"/>, and the reported figure is the cost of the window, not of a
/// single packet.
///
/// In-order and reordered delivery are separated because they exercise different code: in-order
/// packets go straight to the receive buffer, while out-of-order ones land in the reorder buffer
/// and trigger a scan each time the gap closes.
/// </summary>
[MemoryDiagnoser]
public class UtpBenchmarks
{
    private const int WindowSize = 256;
    private const int PayloadSize = 1024;

    private static readonly FieldInfo StateField =
        typeof(UtpStream).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo SeqField =
        typeof(UtpStream).GetField("_seqNr", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo AckField =
        typeof(UtpStream).GetField("_ackNr", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly IPEndPoint _remote = new(IPAddress.Loopback, 12345);
    private UtpStream _stream = null!;
    private ushort _baseSeq;
    private byte[] _payload = null!;
    private byte[] _sackBitmask = null!;
    private byte[] _drainBuffer = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _payload = new byte[PayloadSize];
        Random.Shared.NextBytes(_payload);
        _drainBuffer = new byte[64 * 1024];

        // A 16-byte SACK bitmask with a realistic scattering of gaps - a solid block of set bits
        // would collapse into one range and skip most of the parser's work.
        _sackBitmask = new byte[16];
        for (int i = 0; i < _sackBitmask.Length; i++)
        {
            _sackBitmask[i] = (byte)(i % 3 == 0 ? 0b1010_1101 : 0b1111_0011);
        }
    }

    /// <summary>
    /// Rebuilds the stream so each measured window starts from the same state. Without this the
    /// first window would be in-order and every later one a flood of duplicates.
    /// </summary>
    [IterationSetup(Targets = [nameof(ConstructOnly), nameof(ProcessInOrderWindow), nameof(ProcessReorderedWindow)])]
    public void IterationSetup()
    {
        _stream = new UtpStream(new NullUtpManager(), _remote, 100, 101, TimeProvider.System);
        StateField.SetValue(_stream, UtpState.Connected);
        _baseSeq = (ushort)SeqField.GetValue(_stream)!;
        AckField.SetValue(_stream, (ushort)(_baseSeq - 1));
    }

    /// <summary>
    /// Processes nothing, so the construction that <see cref="IterationSetup"/> does can be
    /// subtracted from the rows below rather than guessed at.
    /// </summary>
    [Benchmark(Description = "Baseline: stream construction only, 0 packets")]
    public void ConstructOnly()
    {
    }

    [Benchmark(Description = "Process 256-packet window, in order")]
    public async Task ProcessInOrderWindow()
    {
        for (int i = 0; i < WindowSize; i++)
        {
            Deliver((ushort)(_baseSeq + i));
        }
        await DrainAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers each pair swapped, so every second packet arrives early and sits in the reorder
    /// buffer until its predecessor lands.
    /// </summary>
    [Benchmark(Description = "Process 256-packet window, pairwise reordered")]
    public async Task ProcessReorderedWindow()
    {
        for (int i = 0; i < WindowSize; i += 2)
        {
            Deliver((ushort)(_baseSeq + i + 1));
            Deliver((ushort)(_baseSeq + i));
        }
        await DrainAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the window back out. This is not incidental cleanup - it is the other half of the
    /// pipeline. The receive path rents a pooled buffer per packet and the reader pump returns it
    /// only after the data reaches the pipe, and the pipe stalls its writer past 64 KiB. Without
    /// a reader the pool is never replenished, and the benchmark reports fresh allocations for
    /// buffers that production recycles.
    /// </summary>
    private async Task DrainAsync()
    {
        int expected = WindowSize * PayloadSize;
        int total = 0;
        while (total < expected)
        {
            int read = await _stream.ReadAsync(_drainBuffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
    }

    [Benchmark(Description = "Parse 16-byte SACK bitmask")]
    public int ParseSack()
    {
        var ranges = UtpSackParser.Parse(_sackBitmask, 0, _sackBitmask.Length, 1000);
        return ranges?.Count ?? 0;
    }

    private void Deliver(ushort seqNr)
    {
        var header = new MessageHeader
        {
            TypeVer = (byte)(((byte)MessageType.ST_DATA << 4) | MessageHeader.CurrentVersion),
            ConnectionId = 100,
            SeqNr = seqNr,
            AckNr = _baseSeq,
            WndSize = 1024 * 1024,
            TimestampMicroseconds = Utils.TimestampMicro()
        };

        _stream.ProcessPacketWithSack(header, _payload, 0, null, null, _remote);
    }

    /// <summary>Manager stub: the benchmark drives the receive path and never needs a send.</summary>
    private sealed class NullUtpManager : IUtpManager
    {
        public Action<UtpStream>? OnNewConnection { get; set; }

        public void CloseStream(UtpStream stream) { }

        public UtpStream CreateStream(IPEndPoint remote) => throw new NotSupportedException();

        public Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct)
            => Task.CompletedTask;

        public void Start(IUdpListener listener) { }

        public void Stop() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
