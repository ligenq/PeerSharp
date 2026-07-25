using BenchmarkDotNet.Attributes;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using System.Buffers;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Wire framing. Every block that arrives costs one decode and every request costs one encode,
/// so on a saturated swarm these run at the same rate as <see cref="StorageBenchmarks"/>.
///
/// Piece messages are measured separately from the small control messages: they carry a 16 KiB
/// payload, so their cost is dominated by how the payload is handed off (pooled vs copied) rather
/// than by header parsing, and a regression there looks very different from one in Have/Request.
/// </summary>
[MemoryDiagnoser]
public class PeerProtocolBenchmarks
{
    private const int BlockSize = 16 * 1024;

    private byte[] _scratch = null!;
    private PeerMessage _have = null!;
    private PeerMessage _request = null!;
    private PeerMessage _piece = null!;
    private byte[] _encodedHave = null!;
    private byte[] _encodedRequest = null!;
    private byte[] _encodedPiece = null!;

    [GlobalSetup]
    public void Setup()
    {
        _scratch = new byte[BlockSize + 64];

        _have = new PeerMessage(MessageId.Have) { HavePieceIndex = 1234 };
        _request = new PeerMessage(MessageId.Request)
        {
            PieceIndex = 1234,
            BlockOffset = 32 * 1024,
            BlockLength = BlockSize
        };

        var payload = new byte[BlockSize];
        Random.Shared.NextBytes(payload);
        _piece = new PeerMessage(MessageId.Piece)
        {
            PieceIndex = 1234,
            BlockOffset = 32 * 1024,
            Payload = payload
        };

        _encodedHave = Encode(_have);
        _encodedRequest = Encode(_request);
        _encodedPiece = Encode(_piece);
    }

    private static byte[] Encode(PeerMessage message)
    {
        var buffer = new byte[PeerProtocol.GetMessageLength(message)];
        PeerProtocol.WriteMessage(message, buffer);
        return buffer;
    }

    [Benchmark(Description = "Encode Have")]
    public int EncodeHave() => PeerProtocol.WriteMessage(_have, _scratch);

    [Benchmark(Description = "Encode Request")]
    public int EncodeRequest() => PeerProtocol.WriteMessage(_request, _scratch);

    [Benchmark(Description = "Encode Piece (16 KiB)")]
    public int EncodePiece() => PeerProtocol.WriteMessage(_piece, _scratch);

    [Benchmark(Description = "Decode Have")]
    public bool DecodeHave() => Decode(_encodedHave);

    [Benchmark(Description = "Decode Request")]
    public bool DecodeRequest() => Decode(_encodedRequest);

    [Benchmark(Description = "Decode Piece (16 KiB)")]
    public bool DecodePiece() => Decode(_encodedPiece);

    private static bool Decode(byte[] encoded)
    {
        var sequence = new ReadOnlySequence<byte>(encoded);
        bool decoded = PeerProtocol.TryDecodeMessage(ref sequence, out var message, out _);
        // Piece messages hand back pooled memory; not returning it would turn a decode
        // benchmark into a slow leak and distort the allocation column.
        message?.Dispose();
        return decoded;
    }
}
