using System.Text;
using PeerSharp.BEncoding;

namespace PeerSharp.Tests.Core.Utilities.Bencode;

/// <summary>
/// Comprehensive tests for Bencode parsing and writing.
/// Based on the Bencode specification used in BitTorrent.
/// Reference: https://wiki.theory.org/BitTorrentSpecification#Bencoding
///
/// Key rules:
/// - Integers: i[number]e (e.g., i42e, i-3e, i0e)
/// - Strings: [length]:[content] (e.g., 4:test)
/// - Lists: l[items]e (e.g., l4:testi42ee)
/// - Dictionaries: d[key][value]...e - keys must be strings and sorted
/// </summary>
public class BencodeComprehensiveTests
{
    #region Integer Parsing Tests

    [Fact]
    public void Parse_PositiveInteger_ReturnsCorrectValue()
    {
        var data = Encoding.ASCII.GetBytes("i42e");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BNumber>(node);
        Assert.Equal(42, ((BNumber)node).Value);
    }

    [Fact]
    public void Parse_NegativeInteger_ReturnsCorrectValue()
    {
        var data = Encoding.ASCII.GetBytes("i-42e");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BNumber>(node);
        Assert.Equal(-42, ((BNumber)node).Value);
    }

    [Fact]
    public void Parse_Zero_ReturnsCorrectValue()
    {
        var data = Encoding.ASCII.GetBytes("i0e");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BNumber>(node);
        Assert.Equal(0, ((BNumber)node).Value);
    }

    [Fact]
    public void Parse_LargePositiveInteger_ReturnsCorrectValue()
    {
        var data = Encoding.ASCII.GetBytes("i9223372036854775807e"); // long.MaxValue
        var node = BencodeParser.Parse(data);

        Assert.IsType<BNumber>(node);
        Assert.Equal(long.MaxValue, ((BNumber)node).Value);
    }

    [Fact]
    public void Parse_LargeNegativeInteger_ReturnsCorrectValue()
    {
        // Test with a large negative number that doesn't cause overflow
        var data = Encoding.ASCII.GetBytes("i-1234567890123456789e");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BNumber>(node);
        Assert.Equal(-1234567890123456789L, ((BNumber)node).Value);
    }

    [Fact]
    public void Parse_LongMinValue_ReturnsCorrectValue()
    {
        // long.MinValue (-9223372036854775808) is valid per Bencode spec
        var data = Encoding.ASCII.GetBytes("i-9223372036854775808e"); // long.MinValue
        var node = BencodeParser.Parse(data);

        Assert.IsType<BNumber>(node);
        Assert.Equal(long.MinValue, ((BNumber)node).Value);
    }

    [Fact]
    public void Parse_IntegerOverflow_ThrowsFormatException()
    {
        // long.MaxValue + 1
        var data = Encoding.ASCII.GetBytes("i9223372036854775808e");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_LeadingZero_ThrowsFormatException()
    {
        var data = Encoding.ASCII.GetBytes("i03e");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_NegativeZero_ThrowsFormatException()
    {
        var data = Encoding.ASCII.GetBytes("i-0e");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_IntegerMissingTerminator_ThrowsFormatException()
    {
        var data = Encoding.ASCII.GetBytes("i42");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_IntegerNonDigitCharacter_ThrowsFormatException()
    {
        var data = Encoding.ASCII.GetBytes("i4x2e");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_EmptyInteger_ThrowsFormatException()
    {
        // Per Bencode spec, "ie" (empty integer) is invalid
        var data = Encoding.ASCII.GetBytes("ie");

        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    #endregion

    #region String Parsing Tests

    [Fact]
    public void Parse_SimpleString_ReturnsCorrectValue()
    {
        var data = Encoding.ASCII.GetBytes("4:test");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BString>(node);
        Assert.Equal("test", ((BString)node).Text);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyValue()
    {
        var data = Encoding.ASCII.GetBytes("0:");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BString>(node);
        Assert.Equal("", ((BString)node).Text);
    }

    [Fact]
    public void Parse_StringWithSpaces_ReturnsCorrectValue()
    {
        var data = Encoding.ASCII.GetBytes("11:hello world");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BString>(node);
        Assert.Equal("hello world", ((BString)node).Text);
    }

    [Fact]
    public void Parse_StringWithColons_ReturnsCorrectValue()
    {
        var data = Encoding.ASCII.GetBytes("5:a:b:c");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BString>(node);
        Assert.Equal("a:b:c", ((BString)node).Text);
    }

    [Fact]
    public void Parse_BinaryString_ReturnsCorrectBytes()
    {
        // Binary data (like piece hashes) - includes null bytes
        var binaryData = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var prefix = Encoding.ASCII.GetBytes("5:");
        var data = new byte[prefix.Length + binaryData.Length];
        Array.Copy(prefix, 0, data, 0, prefix.Length);
        Array.Copy(binaryData, 0, data, prefix.Length, binaryData.Length);

        var node = BencodeParser.Parse(data);

        Assert.IsType<BString>(node);
        Assert.Equal(binaryData, ((BString)node).Value.ToArray());
    }

    [Fact]
    public void Parse_20ByteHash_ReturnsCorrectBytes()
    {
        // SHA-1 hash (20 bytes) - common in torrents
        var hash = new byte[20];
        for (int i = 0; i < 20; i++)
        {
            hash[i] = (byte)i;
        }

        var prefix = Encoding.ASCII.GetBytes("20:");
        var data = new byte[prefix.Length + hash.Length];
        Array.Copy(prefix, 0, data, 0, prefix.Length);
        Array.Copy(hash, 0, data, prefix.Length, hash.Length);

        var node = BencodeParser.Parse(data);

        Assert.IsType<BString>(node);
        Assert.Equal(hash, ((BString)node).Value.ToArray());
    }

    [Fact]
    public void Parse_StringTooShort_ThrowsFormatException()
    {
        // Claims 10 bytes but only has 4
        var data = Encoding.ASCII.GetBytes("10:test");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_StringLengthOverflow_ThrowsFormatException()
    {
        // Length exceeds int.MaxValue
        var data = Encoding.ASCII.GetBytes("2147483648:a");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_StringLeadingZeroInLength_ThrowsFormatException()
    {
        var data = Encoding.ASCII.GetBytes("04:test");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_StringMissingColon_ThrowsFormatException()
    {
        var data = Encoding.ASCII.GetBytes("4test");
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    #endregion

    #region List Parsing Tests

    [Fact]
    public void Parse_EmptyList_ReturnsEmptyList()
    {
        var data = Encoding.ASCII.GetBytes("le");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BList>(node);
        Assert.Empty(((BList)node).List);
    }

    [Fact]
    public void Parse_ListWithOneInteger_ReturnsCorrectList()
    {
        var data = Encoding.ASCII.GetBytes("li42ee");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BList>(node);
        var list = (BList)node;
        Assert.Single(list.List);
        Assert.Equal(42, ((BNumber)list.List[0]).Value);
    }

    [Fact]
    public void Parse_ListWithOneString_ReturnsCorrectList()
    {
        var data = Encoding.ASCII.GetBytes("l4:teste");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BList>(node);
        var list = (BList)node;
        Assert.Single(list.List);
        Assert.Equal("test", ((BString)list.List[0]).Text);
    }

    [Fact]
    public void Parse_ListWithMixedTypes_ReturnsCorrectList()
    {
        var data = Encoding.ASCII.GetBytes("l4:testi42e5:helloe");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BList>(node);
        var list = (BList)node;
        Assert.Equal(3, list.List.Count);
        Assert.Equal("test", ((BString)list.List[0]).Text);
        Assert.Equal(42, ((BNumber)list.List[1]).Value);
        Assert.Equal("hello", ((BString)list.List[2]).Text);
    }

    [Fact]
    public void Parse_NestedLists_ReturnsCorrectStructure()
    {
        var data = Encoding.ASCII.GetBytes("ll4:testee"); // List containing a list
        var node = BencodeParser.Parse(data);

        Assert.IsType<BList>(node);
        var outerList = (BList)node;
        Assert.Single(outerList.List);

        Assert.IsType<BList>(outerList.List[0]);
        var innerList = (BList)outerList.List[0];
        Assert.Single(innerList.List);
        Assert.Equal("test", ((BString)innerList.List[0]).Text);
    }

    [Fact]
    public void Parse_ListWithDict_ReturnsCorrectStructure()
    {
        var data = Encoding.ASCII.GetBytes("ld3:key5:valueee");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BList>(node);
        var list = (BList)node;
        Assert.Single(list.List);
        Assert.IsType<BDict>(list.List[0]);
    }

    #endregion

    #region Dictionary Parsing Tests

    [Fact]
    public void Parse_EmptyDict_ReturnsEmptyDict()
    {
        var data = Encoding.ASCII.GetBytes("de");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BDict>(node);
        Assert.Empty(((BDict)node).Dict);
    }

    [Fact]
    public void Parse_DictWithOneEntry_ReturnsCorrectDict()
    {
        var data = Encoding.ASCII.GetBytes("d3:key5:valuee");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BDict>(node);
        var dict = (BDict)node;
        Assert.Single(dict.Dict);
        Assert.Equal("value", dict.GetString("key"));
    }

    [Fact]
    public void Parse_DictWithIntegerValue_ReturnsCorrectDict()
    {
        var data = Encoding.ASCII.GetBytes("d3:numi42ee");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BDict>(node);
        var dict = (BDict)node;
        Assert.Equal(42, dict.GetLong("num"));
    }

    [Fact]
    public void Parse_DictWithListValue_ReturnsCorrectDict()
    {
        var data = Encoding.ASCII.GetBytes("d4:listl4:testi42eee");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BDict>(node);
        var dict = (BDict)node;
        var list = dict.Get("list") as BList;
        Assert.NotNull(list);
        Assert.Equal(2, list.List.Count);
    }

    [Fact]
    public void Parse_NestedDicts_ReturnsCorrectStructure()
    {
        var data = Encoding.ASCII.GetBytes("d5:outerd5:inner5:valueee");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BDict>(node);
        var outerDict = (BDict)node;
        var innerDict = outerDict.Get("outer") as BDict;
        Assert.NotNull(innerDict);
        Assert.Equal("value", innerDict.GetString("inner"));
    }

    [Fact]
    public void Parse_DictMultipleEntries_ReturnsAllEntries()
    {
        var data = Encoding.ASCII.GetBytes("d1:ai1e1:bi2e1:ci3ee");
        var node = BencodeParser.Parse(data);

        Assert.IsType<BDict>(node);
        var dict = (BDict)node;
        Assert.Equal(3, dict.Dict.Count);
        Assert.Equal(1, dict.GetLong("a"));
        Assert.Equal(2, dict.GetLong("b"));
        Assert.Equal(3, dict.GetLong("c"));
    }

    #endregion

    #region BDict Helper Method Tests

    [Fact]
    public void BDict_GetString_ReturnsNullForMissingKey()
    {
        var data = Encoding.ASCII.GetBytes("d3:key5:valuee");
        var dict = BencodeParser.Parse(data) as BDict;

        Assert.Null(dict!.GetString("missing"));
    }

    [Fact]
    public void BDict_GetLong_ReturnsNullForMissingKey()
    {
        var data = Encoding.ASCII.GetBytes("d3:numi42ee");
        var dict = BencodeParser.Parse(data) as BDict;

        Assert.Null(dict!.GetLong("missing"));
    }

    [Fact]
    public void BDict_GetLong_ReturnsNullForStringValue()
    {
        var data = Encoding.ASCII.GetBytes("d3:key5:valuee");
        var dict = BencodeParser.Parse(data) as BDict;

        Assert.Null(dict!.GetLong("key")); // Value is string, not number
    }

    [Fact]
    public void BDict_GetString_ReturnsNullForIntegerValue()
    {
        var data = Encoding.ASCII.GetBytes("d3:numi42ee");
        var dict = BencodeParser.Parse(data) as BDict;

        Assert.Null(dict!.GetString("num")); // Value is number, not string
    }

    [Fact]
    public void BDict_GetBytes_ReturnsBinaryData()
    {
        var binaryData = new byte[] { 0x00, 0x01, 0x02 };
        var prefix = Encoding.ASCII.GetBytes("d4:data3:");
        var suffix = Encoding.ASCII.GetBytes("e");
        var data = new byte[prefix.Length + binaryData.Length + suffix.Length];
        Array.Copy(prefix, 0, data, 0, prefix.Length);
        Array.Copy(binaryData, 0, data, prefix.Length, binaryData.Length);
        Array.Copy(suffix, 0, data, prefix.Length + binaryData.Length, suffix.Length);

        var dict = BencodeParser.Parse(data) as BDict;
        var bytes = dict!.GetBytes("data");

        Assert.NotNull(bytes);
        Assert.Equal(binaryData, bytes.Value.ToArray());
    }

    [Fact]
    public void BDict_Get_ReturnsCorrectNodeType()
    {
        var data = Encoding.ASCII.GetBytes("d3:numi42e3:str4:test4:listlee");
        var dict = BencodeParser.Parse(data) as BDict;

        Assert.IsType<BNumber>(dict!.Get("num"));
        Assert.IsType<BString>(dict.Get("str"));
        Assert.IsType<BList>(dict.Get("list"));
        Assert.Null(dict.Get("missing"));
    }

    #endregion

    #region BencodeWriter Tests

    [Fact]
    public void Write_Integer_ProducesCorrectOutput()
    {
        var node = new BNumber(42);

        var result = BencodeWriter.Write(node);

        Assert.Equal("i42e", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Write_NegativeInteger_ProducesCorrectOutput()
    {
        var node = new BNumber(-42);

        var result = BencodeWriter.Write(node);

        Assert.Equal("i-42e", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Write_Zero_ProducesCorrectOutput()
    {
        var node = new BNumber(0);

        var result = BencodeWriter.Write(node);

        Assert.Equal("i0e", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Write_String_ProducesCorrectOutput()
    {
        var node = new BString(Encoding.UTF8.GetBytes("test"));

        var result = BencodeWriter.Write(node);

        Assert.Equal("4:test", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Write_EmptyString_ProducesCorrectOutput()
    {
        var node = new BString([]);

        var result = BencodeWriter.Write(node);

        Assert.Equal("0:", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Write_BinaryString_PreservesBytes()
    {
        var binaryData = new byte[] { 0x00, 0x01, 0xFF };
        var node = new BString(binaryData);

        var result = BencodeWriter.Write(node);

        // Should be "3:" followed by the binary bytes
        Assert.Equal(5, result.Length);
        Assert.Equal((byte)'3', result[0]);
        Assert.Equal((byte)':', result[1]);
        Assert.Equal(binaryData, result[2..]);
    }

    [Fact]
    public void Write_EmptyList_ProducesCorrectOutput()
    {
        var node = new BList();

        var result = BencodeWriter.Write(node);

        Assert.Equal("le", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Write_ListWithItems_ProducesCorrectOutput()
    {
        var node = new BList();
        node.List.Add(new BString(Encoding.UTF8.GetBytes("test")));
        node.List.Add(new BNumber(42));

        var result = BencodeWriter.Write(node);

        Assert.Equal("l4:testi42ee", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Write_EmptyDict_ProducesCorrectOutput()
    {
        var node = new BDict();

        var result = BencodeWriter.Write(node);

        Assert.Equal("de", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Write_DictWithItems_ProducesCorrectOutput()
    {
        var node = new BDict();
        node.Dict["key"] = new BString(Encoding.UTF8.GetBytes("value"));

        var result = BencodeWriter.Write(node);

        Assert.Equal("d3:key5:valuee", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Write_DictKeysSortedCorrectly()
    {
        // Per Bencode spec, dictionary keys must be sorted
        var node = new BDict();
        node.Dict["z"] = new BNumber(3);
        node.Dict["a"] = new BNumber(1);
        node.Dict["m"] = new BNumber(2);

        var result = BencodeWriter.Write(node);

        // Keys should be in order: a, m, z
        Assert.Equal("d1:ai1e1:mi2e1:zi3ee", Encoding.ASCII.GetString(result));
    }

    #endregion

    #region Round-Trip Tests

    [Theory]
    [InlineData("i0e")]
    [InlineData("i42e")]
    [InlineData("i-42e")]
    [InlineData("i9223372036854775807e")]
    [InlineData("i-9223372036854775808e")] // long.MinValue
    [InlineData("i-1234567890123456789e")]
    public void RoundTrip_Integers_PreservesValue(string bencode)
    {
        var data = Encoding.ASCII.GetBytes(bencode);
        var node = BencodeParser.Parse(data);
        var result = BencodeWriter.Write(node!);

        Assert.Equal(bencode, Encoding.ASCII.GetString(result));
    }

    [Theory]
    [InlineData("0:")]
    [InlineData("4:test")]
    [InlineData("11:hello world")]
    public void RoundTrip_Strings_PreservesValue(string bencode)
    {
        var data = Encoding.ASCII.GetBytes(bencode);
        var node = BencodeParser.Parse(data);
        var result = BencodeWriter.Write(node!);

        Assert.Equal(bencode, Encoding.ASCII.GetString(result));
    }

    [Theory]
    [InlineData("le")]
    [InlineData("li42ee")]
    [InlineData("l4:testi42ee")]
    [InlineData("ll4:testee")]
    public void RoundTrip_Lists_PreservesValue(string bencode)
    {
        var data = Encoding.ASCII.GetBytes(bencode);
        var node = BencodeParser.Parse(data);
        var result = BencodeWriter.Write(node!);

        Assert.Equal(bencode, Encoding.ASCII.GetString(result));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("d3:key5:valuee")]
    [InlineData("d1:ai1e1:bi2ee")]
    public void RoundTrip_Dicts_PreservesValue(string bencode)
    {
        var data = Encoding.ASCII.GetBytes(bencode);
        var node = BencodeParser.Parse(data);
        var result = BencodeWriter.Write(node!);

        Assert.Equal(bencode, Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void RoundTrip_BinaryData_PreservesBytes()
    {
        var binaryData = new byte[20];
        for (int i = 0; i < 20; i++)
        {
            binaryData[i] = (byte)i;
        }

        var prefix = Encoding.ASCII.GetBytes("20:");
        var originalData = new byte[prefix.Length + binaryData.Length];
        Array.Copy(prefix, 0, originalData, 0, prefix.Length);
        Array.Copy(binaryData, 0, originalData, prefix.Length, binaryData.Length);

        var node = BencodeParser.Parse(originalData);
        var result = BencodeWriter.Write(node!);

        Assert.Equal(originalData, result);
    }

    [Fact]
    public void RoundTrip_ComplexStructure_PreservesValue()
    {
        // Structure similar to a torrent info dict
        const string bencode = "d4:infod4:name4:test12:piece lengthi16384e6:pieces20:01234567890123456789ee";
        var data = Encoding.ASCII.GetBytes(bencode);
        var node = BencodeParser.Parse(data);
        var result = BencodeWriter.Write(node!);

        Assert.Equal(bencode, Encoding.ASCII.GetString(result));
    }

    #endregion

    #region Security Tests

    [Fact]
    public void Parse_DeeplyNestedLists_ThrowsFormatException()
    {
        // Create deeply nested list structure (> 100 levels)
        var sb = new StringBuilder();
        for (int i = 0; i < 150; i++)
        {
            sb.Append('l');
        }

        for (int i = 0; i < 150; i++)
        {
            sb.Append('e');
        }

        var data = Encoding.ASCII.GetBytes(sb.ToString());

        var ex = Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
        Assert.Contains("recursion", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void Parse_DeeplyNestedDicts_ThrowsFormatException()
    {
        // Create deeply nested dictionary structure
        var sb = new StringBuilder();
        for (int i = 0; i < 150; i++)
        {
            sb.Append("d1:a");
        }

        sb.Append("i1e");
        for (int i = 0; i < 150; i++)
        {
            sb.Append('e');
        }

        var data = Encoding.ASCII.GetBytes(sb.ToString());

        var ex = Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
        Assert.Contains("recursion", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void Parse_ExtremelyLongString_ThrowsFormatException()
    {
        // Request a string longer than MaxStringLength (10MB)
        var data = Encoding.ASCII.GetBytes("10485761:a"); // 10MB + 1

        var ex = Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
        Assert.Contains("string length", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void Parse_ValidLengthString_DoesNotThrow()
    {
        // 1000 bytes is well under limit
        var content = new string('a', 1000);
        var data = Encoding.ASCII.GetBytes($"1000:{content}");

        var node = BencodeParser.Parse(data);

        Assert.IsType<BString>(node);
        Assert.Equal(1000, ((BString)node).Value.Length);
    }

    #endregion

    #region ParseWithConsumed Tests

    [Fact]
    public void ParseWithConsumed_ReturnsCorrectByteCount()
    {
        var data = Encoding.ASCII.GetBytes("i42e");

        var (node, consumed) = BencodeParser.ParseWithConsumed(data);

        Assert.NotNull(node);
        Assert.Equal(4, consumed);
    }

    [Fact]
    public void ParseWithConsumed_StringWithTrailingData_ReturnsCorrectByteCount()
    {
        var data = Encoding.ASCII.GetBytes("4:testextradata");

        var (node, consumed) = BencodeParser.ParseWithConsumed(data);

        Assert.NotNull(node);
        Assert.Equal(6, consumed); // "4:test" = 6 bytes
        Assert.Equal("test", ((BString)node).Text);
    }

    [Fact]
    public void ParseWithConsumed_ComplexStructure_ReturnsCorrectByteCount()
    {
        const string bencode = "d3:key5:valuee";
        var data = Encoding.ASCII.GetBytes(bencode + "trailing");

        var (node, consumed) = BencodeParser.ParseWithConsumed(data);

        Assert.NotNull(node);
        Assert.Equal(bencode.Length, consumed);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Parse_EmptyData_ThrowsException()
    {
        var data = Array.Empty<byte>();
        Assert.Throws<FormatException>(() => BencodeParser.Parse(data));
    }

    [Fact]
    public void Parse_UnknownType_ReturnsNull()
    {
        var data = Encoding.ASCII.GetBytes("x123");
        var node = BencodeParser.Parse(data);

        Assert.Null(node);
    }

    [Fact]
    public void Parse_JustTerminator_ReturnsNull()
    {
        var data = Encoding.ASCII.GetBytes("e");
        var node = BencodeParser.Parse(data);

        Assert.Null(node);
    }

    [Fact]
    public void Parse_UnicodeInString_PreservesBytes()
    {
        // UTF-8 encoded "hello" in Japanese
        var utf8Bytes = Encoding.UTF8.GetBytes("こんにちは");
        var prefix = Encoding.ASCII.GetBytes($"{utf8Bytes.Length}:");
        var data = new byte[prefix.Length + utf8Bytes.Length];
        Array.Copy(prefix, 0, data, 0, prefix.Length);
        Array.Copy(utf8Bytes, 0, data, prefix.Length, utf8Bytes.Length);

        var node = BencodeParser.Parse(data);

        Assert.IsType<BString>(node);
        Assert.Equal(utf8Bytes, ((BString)node).Value.ToArray());
        Assert.Equal("こんにちは", ((BString)node).Text);
    }

    [Fact]
    public void BString_ToString_ReturnsText()
    {
        var data = Encoding.ASCII.GetBytes("4:test");
        var node = BencodeParser.Parse(data) as BString;

        Assert.Equal("test", node!.ToString());
    }

    [Fact]
    public void BNumber_ToString_ReturnsValue()
    {
        var data = Encoding.ASCII.GetBytes("i42e");
        var node = BencodeParser.Parse(data) as BNumber;

        Assert.Equal("42", node!.ToString());
    }

    [Fact]
    public void BList_ToString_ReturnsCount()
    {
        var data = Encoding.ASCII.GetBytes("l4:testi42ee");
        var node = BencodeParser.Parse(data) as BList;

        Assert.Equal("List[2]", node!.ToString());
    }

    [Fact]
    public void BDict_ToString_ReturnsCount()
    {
        var data = Encoding.ASCII.GetBytes("d3:key5:value3:numi42ee");
        var node = BencodeParser.Parse(data) as BDict;

        Assert.Equal("Dict[2]", node!.ToString());
    }

    #endregion

    #region Torrent-Specific Tests

    [Fact]
    public void Parse_TorrentLikeInfoDict_ParsesCorrectly()
    {
        // Simplified torrent info dictionary structure
        const string bencode = "d" +
            "4:name11:example.txt" +
            "12:piece lengthi16384e" +
            "6:pieces20:01234567890123456789" +
            "6:lengthi1048576e" +
            "e";

        var data = Encoding.ASCII.GetBytes(bencode);
        var node = BencodeParser.Parse(data) as BDict;

        Assert.NotNull(node);
        Assert.Equal("example.txt", node.GetString("name"));
        Assert.Equal(16384, node.GetLong("piece length"));
        Assert.Equal(1048576, node.GetLong("length"));

        var pieces = node.GetBytes("pieces");
        Assert.NotNull(pieces);
        Assert.Equal(20, pieces.Value.Length);
    }

    [Fact]
    public void Parse_AnnounceList_ParsesCorrectly()
    {
        // Tiered announce list structure
        // Each URL is 41 characters: http://tracker1.example.com:6969/announce
        const string bencode = "d13:announce-listll41:http://tracker1.example.com:6969/announceel41:http://tracker2.example.com:6969/announceeee";

        var data = Encoding.ASCII.GetBytes(bencode);
        var node = BencodeParser.Parse(data) as BDict;

        Assert.NotNull(node);
        var announceList = node.Get("announce-list") as BList;
        Assert.NotNull(announceList);
        Assert.Equal(2, announceList.List.Count); // 2 tiers
    }

    [Fact]
    public void Parse_MultiFileInfo_ParsesCorrectly()
    {
        // Multi-file torrent structure
        const string bencode = "d" +
            "4:name7:myfiles" +
            "5:filesl" +
                "d6:lengthi1000e4:pathl3:dir8:file.txte" + "e" +
                "d6:lengthi2000e4:pathl9:file2.txte" + "e" +
            "ee";

        var data = Encoding.ASCII.GetBytes(bencode);
        var node = BencodeParser.Parse(data) as BDict;

        Assert.NotNull(node);
        Assert.Equal("myfiles", node.GetString("name"));

        var files = node.Get("files") as BList;
        Assert.NotNull(files);
        Assert.Equal(2, files.List.Count);

        var file1 = files.List[0] as BDict;
        Assert.NotNull(file1);
        Assert.Equal(1000, file1.GetLong("length"));
    }

    #endregion
}





