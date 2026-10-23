using System.Text.Json;
using PeerSharp.Internals;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core;

public sealed class ResumePieceJsonConverterTests
{
    [Fact]
    public void CancellationStopsAChunkedWriteBeforeEncodingTheRemainingData()
    {
        using var cts = new CancellationTokenSource();
        var piece = new TorrentStateData.UnfinishedPieceData
        {
            Index = 0,
            Blocks = [true],
            Data = new byte[1024 * 1024],
            SerializationCancellationToken = cts.Token
        };
        using var output = new CountingStream { CancelOnWrite = cts };
        using var writer = new Utf8JsonWriter(output);
        Assert.Throws<OperationCanceledException>(() => new ResumePieceJsonConverter().Write(writer, piece, PeerSharpJsonContext.Default.Options));
        Assert.True(output.Length < 85000);
    }

    [Fact]
    public void ConverterKeepsLegacyFieldsAndSkipsUnknownExtensions()
    {
        var reader = new Utf8JsonReader("{\"Extra\":{\"Nested\":[1,2]},\"Data\":\"AQID\",\"Index\":2,\"Blocks\":[true,false]}"u8);
        Assert.True(reader.Read());
        var converter = new ResumePieceJsonConverter();
        var piece = converter.Read(ref reader, typeof(TorrentStateData.UnfinishedPieceData), PeerSharpJsonContext.Default.Options);
        Assert.Equal(2, piece.Index);
        Assert.Equal(new byte[] { 1, 2, 3 }, piece.Data);
        Assert.Equal(new[] { true, false }, piece.Blocks);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) converter.Write(writer, piece, PeerSharpJsonContext.Default.Options);
        using var json = JsonDocument.Parse(output.ToArray());
        Assert.Equal(piece.Data, json.RootElement.GetProperty("Data").GetBytesFromBase64());
        Assert.Equal(2, json.RootElement.GetProperty("Index").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(12287)]
    [InlineData(12288)]
    [InlineData(12289)]
    [InlineData(4194305)]
    public void Base64ChunksRoundTripWithoutLargeWrites(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        var state = new TorrentStateData
        {
            UnfinishedPieces = [new() { Index = 17, Blocks = [true, false, true], Data = data }]
        };
        using var output = new CountingStream();
        using (var writer = new Utf8JsonWriter(output))
            JsonSerializer.Serialize(writer, state, PeerSharpJsonContext.Default.TorrentStateData);

        // Read with the uncustomized JSON DOM as well: the on-disk representation stays compatible.
        using var document = JsonDocument.Parse(output.ToArray());
        Assert.Equal(data, document.RootElement.GetProperty("UnfinishedPieces")[0].GetProperty("Data").GetBytesFromBase64());
        var restored = JsonSerializer.Deserialize(output.ToArray(), PeerSharpJsonContext.Default.TorrentStateData)!;
        var piece = Assert.Single(restored.UnfinishedPieces);
        Assert.Equal(17, piece.Index);
        Assert.Equal(new[] { true, false, true }, piece.Blocks);
        Assert.Equal(data, piece.Data);
        Assert.True(output.LargestWrite < 85000, $"A contiguous {output.LargestWrite}-byte write would require a large intermediate buffer.");
    }

    private sealed class CountingStream : MemoryStream
    {
        internal int LargestWrite { get; private set; }
        internal CancellationTokenSource? CancelOnWrite { get; init; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            base.Write(buffer);
            CancelOnWrite?.Cancel();
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            LargestWrite = Math.Max(LargestWrite, count);
            base.Write(buffer, offset, count);
            CancelOnWrite?.Cancel();
        }
    }
}
