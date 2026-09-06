using PeerSharp.BEncoding;
using System.Text;

namespace PeerSharp.Tests.Core.Bencode;

public class BDictTests
{
    [Fact]
    public void Get_ReturnsTheNodeOrNullRatherThanThrowing()
    {
        var dict = new BDict();
        dict.Dict["present"] = new BNumber(7);

        Assert.IsType<BNumber>(dict.Get("present"));
        Assert.Null(dict.Get("absent"));
    }

    [Fact]
    public void TypedGetters_ReturnNullWhenTheKeyHoldsADifferentType()
    {
        // Bencode is untyped on the wire, so a peer can send a number where a string belongs. Every
        // accessor here answers "not that" rather than throwing, because a malformed message from
        // one peer must not take the connection down.
        var dict = new BDict();
        dict.Dict["number"] = new BNumber(42);
        dict.Dict["text"] = new BString("hello"u8.ToArray());

        Assert.Equal(42, dict.GetLong("number"));
        Assert.Null(dict.GetLong("text"));

        Assert.Equal("hello", dict.GetString("text"));
        Assert.Null(dict.GetString("number"));

        Assert.Equal("hello"u8.ToArray(), dict.GetBytes("text")!.Value.ToArray());
        Assert.Null(dict.GetBytes("number"));
    }

    [Fact]
    public void GetBytes_DistinguishesAMissingKeyFromAnEmptyString()
    {
        // The nullable cast in GetBytes exists for this: a bare null would convert through
        // byte[] to ReadOnlyMemory<byte> and come back as an empty value that is not null.
        var dict = new BDict();
        dict.Dict["empty"] = new BString([]);

        var empty = dict.GetBytes("empty");
        Assert.True(empty.HasValue);
        Assert.Equal(0, empty!.Value.Length);

        Assert.False(dict.GetBytes("absent").HasValue);
    }

    [Fact]
    public void TypedGetters_ReturnNullForAnAbsentKey()
    {
        var dict = new BDict();

        Assert.Null(dict.GetLong("absent"));
        Assert.Null(dict.GetString("absent"));
        Assert.Null(dict.GetBytes("absent"));
    }

    [Fact]
    public void ToString_ReportsTheEntryCount()
    {
        var dict = new BDict();
        Assert.Equal("Dict[0]", dict.ToString());

        dict.Dict["a"] = new BNumber(1);
        dict.Dict["b"] = new BNumber(2);
        Assert.Equal("Dict[2]", dict.ToString());
    }
}

public class BListTests
{
    [Fact]
    public void AListIsAListOfNodesAndReportsItsLength()
    {
        var list = new BList();
        Assert.Equal(BencodeType.List, list.Type);
        Assert.Equal("List[0]", list.ToString());

        list.List.Add(new BNumber(1));
        list.List.Add(new BString("x"u8.ToArray()));

        Assert.Equal("List[2]", list.ToString());
        Assert.Equal(BencodeType.Integer, list.List[0].Type);
        Assert.Equal(BencodeType.String, list.List[1].Type);
    }
}

public class BNumberTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(42L, "42")]
    [InlineData(-1L, "-1")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void ToString_IsCultureInvariantAndCoversTheWholeRange(long value, string expected)
    {
        // Invariant on purpose: the text goes on the wire, where a locale's separators would be a
        // protocol error rather than a formatting preference.
        Assert.Equal(expected, new BNumber(value).ToString());
    }

    [Fact]
    public void ANumberCarriesItsValueAndType()
    {
        var number = new BNumber(7);
        Assert.Equal(7, number.Value);
        Assert.Equal(BencodeType.Integer, number.Type);

        number.Value = 9;
        Assert.Equal(9, number.Value);
    }
}

public class BStringTests
{
    [Fact]
    public void Text_DecodesTheBytesAsUtf8()
    {
        var value = Encoding.UTF8.GetBytes("naïve ☃");
        var node = new BString(value);

        Assert.Equal("naïve ☃", node.Text);
        Assert.Equal("naïve ☃", node.ToString());
        Assert.Equal(BencodeType.String, node.Type);
    }

    [Fact]
    public void Text_IsRecomputedWhenTheBytesAreReplaced()
    {
        // Text is cached, so replacing Value has to clear it. Without that the node would keep
        // reporting whatever it decoded first, which is the sort of thing that survives every test
        // that only ever reads once.
        var node = new BString("first"u8.ToArray());
        Assert.Equal("first", node.Text);

        node.Value = "second"u8.ToArray();
        Assert.Equal("second", node.Text);
    }

    [Fact]
    public void ByteArrayAndMemoryConstructors_AgreeOnTheContent()
    {
        byte[] bytes = [1, 2, 3];

        Assert.Equal(bytes, new BString(bytes).Value.ToArray());
        Assert.Equal(bytes, new BString(new ReadOnlyMemory<byte>(bytes)).Value.ToArray());
    }

    [Fact]
    public void Text_HandlesBytesThatAreNotValidUtf8WithoutThrowing()
    {
        // A peer can put arbitrary bytes in a string. Text is a convenience; it must not be the
        // thing that throws on a hostile message.
        var node = new BString([0xFF, 0xFE, 0x00]);

        Assert.NotNull(node.Text);
        Assert.Equal(3, node.Value.Length);
    }
}
