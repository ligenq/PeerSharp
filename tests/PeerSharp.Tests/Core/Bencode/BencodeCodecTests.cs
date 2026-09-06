using PeerSharp.BEncoding;
using System.Buffers;
using System.Text;

namespace PeerSharp.Tests.Core.Bencode;

public class BencodeParserTests
{
    [Fact]
    public void Parse_ReadsEachOfTheFourTypes()
    {
        Assert.Equal(42, Assert.IsType<BNumber>(BencodeParser.Parse("i42e"u8.ToArray())).Value);
        Assert.Equal("spam", Assert.IsType<BString>(BencodeParser.Parse("4:spam"u8.ToArray())).Text);
        Assert.Equal(2, Assert.IsType<BList>(BencodeParser.Parse("li1ei2ee"u8.ToArray())).List.Count);
        Assert.Single(Assert.IsType<BDict>(BencodeParser.Parse("d3:cowi1ee"u8.ToArray())).Dict);
    }

    [Fact]
    public void Parse_RoundTripsANestedStructure()
    {
        var parsed = Assert.IsType<BDict>(
            BencodeParser.Parse("d4:infod4:name4:test6:lengthi1234eee"u8.ToArray()));

        var info = Assert.IsType<BDict>(parsed.Get("info"));
        Assert.Equal("test", info.GetString("name"));
        Assert.Equal(1234, info.GetLong("length"));
    }

    [Theory]
    [InlineData("i-42e", -42L)]
    [InlineData("i0e", 0L)]
    [InlineData("i9223372036854775807e", long.MaxValue)]
    public void Parse_ReadsNegativeAndExtremeIntegers(string input, long expected)
    {
        Assert.Equal(expected, Assert.IsType<BNumber>(BencodeParser.Parse(Encoding.ASCII.GetBytes(input))).Value);
    }

    [Fact]
    public void Parse_ReadsAnEmptyStringAndAnEmptyCollection()
    {
        Assert.Equal(0, Assert.IsType<BString>(BencodeParser.Parse("0:"u8.ToArray())).Value.Length);
        Assert.Empty(Assert.IsType<BList>(BencodeParser.Parse("le"u8.ToArray())).List);
        Assert.Empty(Assert.IsType<BDict>(BencodeParser.Parse("de"u8.ToArray())).Dict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("i42")]        // unterminated integer
    [InlineData("ie")]         // no digits
    [InlineData("4:ab")]       // string shorter than its length
    [InlineData("li1e")]       // unterminated list
    [InlineData("d3:cow")]     // dictionary key with no value
    [InlineData("x")]          // not a bencode type at all
    public void Parse_RejectsMalformedInputWithAnException(string input)
    {
        // Every one of these can arrive from a peer or a tracker. Rejecting is the contract; what
        // must not happen is a silently wrong value.
        Assert.ThrowsAny<Exception>(() => BencodeParser.Parse(Encoding.ASCII.GetBytes(input)));
    }

    [Fact]
    public void Parse_AllowsTrailingDataUnlessCanonicalFormIsRequired()
    {
        byte[] withTrailer = "i42eXXXX"u8.ToArray();

        Assert.Equal(42, Assert.IsType<BNumber>(BencodeParser.Parse(withTrailer)).Value);
        Assert.Throws<FormatException>(() => BencodeParser.Parse(withTrailer, requireCanonical: true));
    }

    [Fact]
    public void ParseWithConsumed_ReportsWhereTheValueEnded()
    {
        // How a stream of concatenated messages is read: the caller needs to know where the next
        // one starts, which the node alone cannot say.
        var (node, consumed) = BencodeParser.ParseWithConsumed("i42e5:extra"u8.ToArray());

        Assert.Equal(42, Assert.IsType<BNumber>(node).Value);
        Assert.Equal(4, consumed);
    }

    [Fact]
    public void ParseWithConsumed_ReportsTheWholeLengthWhenNothingFollows()
    {
        byte[] data = "d3:cowi1ee"u8.ToArray();
        var (_, consumed) = BencodeParser.ParseWithConsumed(data);

        Assert.Equal(data.Length, consumed);
    }

    [Fact]
    public void Parse_RefusesToRecurseWithoutBound()
    {
        // A nesting bomb is one line to write and would otherwise be a stack overflow, which no
        // catch block can save the process from.
        var deep = new StringBuilder();
        for (int i = 0; i < 5000; i++) deep.Append('l');
        for (int i = 0; i < 5000; i++) deep.Append('e');

        Assert.ThrowsAny<Exception>(() => BencodeParser.Parse(Encoding.ASCII.GetBytes(deep.ToString())));
    }

    [Fact]
    public void Parse_RefusesAStringLongerThanTheDataItArrivedIn()
    {
        // The declared length is attacker-controlled and a naive reader would allocate it first.
        Assert.ThrowsAny<Exception>(() => BencodeParser.Parse("999999999999:x"u8.ToArray()));
    }
}

public class BencodeWriterTests
{
    [Fact]
    public void Write_ProducesTheWireFormForEachType()
    {
        Assert.Equal("i42e"u8.ToArray(), BencodeWriter.Write(new BNumber(42)));
        Assert.Equal("4:spam"u8.ToArray(), BencodeWriter.Write(new BString("spam"u8.ToArray())));

        var list = new BList();
        list.List.Add(new BNumber(1));
        list.List.Add(new BString("a"u8.ToArray()));
        Assert.Equal("li1e1:ae"u8.ToArray(), BencodeWriter.Write(list));
    }

    [Fact]
    public void Write_OrdersDictionaryKeysAsBep3Requires()
    {
        // Not cosmetic: the info dictionary's infohash is the SHA-1 of these bytes, so a writer that
        // emitted insertion order would compute a hash no other client agrees with.
        var dict = new BDict();
        dict.Dict["zebra"] = new BNumber(1);
        dict.Dict["apple"] = new BNumber(2);
        dict.Dict["Banana"] = new BNumber(3);

        // Ordinal, so uppercase sorts before lowercase.
        Assert.Equal("d6:Bananai3e5:applei2e5:zebrai1ee"u8.ToArray(), BencodeWriter.Write(dict));
    }

    [Fact]
    public void Write_RoundTripsThroughTheParser()
    {
        var original = new BDict();
        original.Dict["name"] = new BString("test"u8.ToArray());
        original.Dict["length"] = new BNumber(1234);
        var nested = new BList();
        nested.List.Add(new BString("a"u8.ToArray()));
        original.Dict["files"] = nested;

        var reparsed = Assert.IsType<BDict>(BencodeParser.Parse(BencodeWriter.Write(original)));

        Assert.Equal("test", reparsed.GetString("name"));
        Assert.Equal(1234, reparsed.GetLong("length"));
        Assert.Single(Assert.IsType<BList>(reparsed.Get("files")).List);
    }

    [Fact]
    public void Write_KeepsKeyBytesThatAreNotValidUtf8()
    {
        // Keys are arbitrary byte strings, which is why the writer uses Latin1: every byte value
        // survives the round trip, where UTF-8 would replace the ones it cannot decode.
        var key = Encoding.Latin1.GetString([0xFF, 0xFE]);
        var dict = new BDict();
        dict.Dict[key] = new BNumber(1);

        var reparsed = Assert.IsType<BDict>(BencodeParser.Parse(BencodeWriter.Write(dict)));

        Assert.Equal(1, reparsed.GetLong(key));
    }

    [Fact]
    public void Write_HandlesBinaryStringValues()
    {
        byte[] binary = [0x00, 0xFF, 0x0D, 0x0A];
        var reparsed = Assert.IsType<BString>(BencodeParser.Parse(BencodeWriter.Write(new BString(binary))));

        Assert.Equal(binary, reparsed.Value.ToArray());
    }

    [Fact]
    public void Write_ToAnExternalBufferWriterProducesTheSameBytes()
    {
        var node = new BNumber(1234567890123);

        var buffer = new ArrayBufferWriter<byte>();
        BencodeWriter.Write(node, buffer);

        Assert.Equal(BencodeWriter.Write(node), buffer.WrittenSpan.ToArray());
    }

    [Fact]
    public void WriteToResult_ExposesTheSameBytesAsMemoryAndSpan()
    {
        var dict = new BDict();
        dict.Dict["a"] = new BNumber(1);

        using var result = BencodeWriter.WriteToResult(dict);

        Assert.Equal("d1:ai1ee"u8.ToArray(), result.Span.ToArray());
        Assert.Equal(result.Span.ToArray(), result.Memory.ToArray());
    }
}

public class BencodeResultTests
{
    [Fact]
    public void MemoryAndSpan_AgreeAndCoverOnlyWhatWasWritten()
    {
        var writer = new PooledBufferWriter(4);
        BencodeWriter.Write(new BString("abcdef"u8.ToArray()), writer);
        using var result = new BencodeResult(writer);

        Assert.Equal("6:abcdef"u8.ToArray(), result.Span.ToArray());
        Assert.Equal(result.Span.Length, result.Memory.Length);
    }

    [Fact]
    public void Dispose_ReturnsTheBufferAndIsSafeToRepeat()
    {
        var result = BencodeWriter.WriteToResult(new BNumber(1));

        result.Dispose();
        result.Dispose();
    }
}

public class PooledBufferWriterTests
{
    [Fact]
    public void GetSpanAndAdvance_AccumulateIntoWrittenSpan()
    {
        using var writer = new PooledBufferWriter(16);

        "abc"u8.CopyTo(writer.GetSpan(3));
        writer.Advance(3);
        "de"u8.CopyTo(writer.GetSpan(2));
        writer.Advance(2);

        Assert.Equal("abcde"u8.ToArray(), writer.WrittenSpan.ToArray());
        Assert.Equal("abcde"u8.ToArray(), writer.WrittenMemory.ToArray());
    }

    [Fact]
    public void GetMemory_WritesIntoTheSameBufferAsGetSpan()
    {
        using var writer = new PooledBufferWriter(16);

        "ab"u8.CopyTo(writer.GetSpan(2));
        writer.Advance(2);
        "cd"u8.CopyTo(writer.GetMemory(2).Span);
        writer.Advance(2);

        Assert.Equal("abcd"u8.ToArray(), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void TheBufferGrowsAndKeepsWhatWasAlreadyWritten()
    {
        // Growth rents a new array and copies. Losing the earlier bytes here would corrupt exactly
        // the large messages - a torrent's piece hashes - that are least likely to be eyeballed.
        using var writer = new PooledBufferWriter(initialCapacity: 1);

        var expected = new List<byte>();
        for (int i = 0; i < 1000; i++)
        {
            byte value = (byte)(i % 251);
            writer.GetSpan(1)[0] = value;
            writer.Advance(1);
            expected.Add(value);
        }

        Assert.Equal(expected, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void ASizeHintLargerThanTheBufferIsHonoured()
    {
        using var writer = new PooledBufferWriter(initialCapacity: 4);

        Assert.True(writer.GetSpan(5000).Length >= 5000);
        Assert.True(writer.GetMemory(9000).Length >= 9000);
    }

    [Fact]
    public void GetSpan_AlwaysOffersRoomForAtLeastOneByte()
    {
        // BencodeWriter asks for a span with no hint when writing a single delimiter, so a zero
        // hint at an exactly-full buffer must still grow rather than hand back nothing.
        using var writer = new PooledBufferWriter(initialCapacity: 1);
        writer.GetSpan(1)[0] = (byte)'x';
        writer.Advance(1);

        Assert.True(writer.GetSpan().Length >= 1);
    }

    [Fact]
    public void Dispose_ReturnsTheBufferAndIsSafeToRepeat()
    {
        var writer = new PooledBufferWriter();
        writer.GetSpan(1)[0] = 1;
        writer.Advance(1);

        writer.Dispose();
        writer.Dispose();
    }
}
