using CsCheck;
using PeerSharp.BEncoding;
using System.Text;

namespace PeerSharp.Tests.Core;

/// <summary>
/// Bencode's round-trip and canonical-form properties.
/// </summary>
/// <remarks>
/// <para>
/// The fuzz harness covers the other half of this: it asks whether a hostile input can make the
/// parser do something other than fail cleanly. It cannot ask whether the parser is <em>right</em>,
/// because it has no idea what the input was supposed to mean. That is what these properties are
/// for.
/// </para>
/// <para>
/// Canonical form is not a stylistic matter here. An info hash is the hash of the encoded info
/// dictionary, so an encoder that orders keys differently from every other client's produces a
/// different hash for the same torrent, and the engine then never meets a single peer for it.
/// </para>
/// </remarks>
public class BencodePropertyTests
{
    /// <summary>
    /// Keys are arbitrary byte strings, which the parser hands back as Latin1 - one char per byte.
    /// Generating them from raw bytes covers the whole key space including bytes above 0x7F, which
    /// is where an ordering that sorts text rather than bytes begins to diverge.
    /// </summary>
    private static readonly Gen<string> Key = Gen.Byte.Array[1, 8].Select(Encoding.Latin1.GetString);

    private static readonly Gen<IBNode> Leaf = Gen.OneOf(
        Gen.Long.Select(value => (IBNode)new BNumber(value)),
        Gen.Byte.Array[0, 24].Select(bytes => (IBNode)new BString(bytes)));

    [Fact]
    public void ParsingWhatWasWrittenGivesBackTheSameTree()
    {
        Node(depth: 3).Sample(node =>
        {
            byte[] encoded = BencodeWriter.Write(node);
            IBNode parsed = BencodeParser.Parse(encoded);

            Assert.True(NodesEqual(node, parsed), "round-trip changed the tree");
        }, iter: 10_000);
    }

    [Fact]
    public void EncodingIsCanonical()
    {
        // Re-encoding a parsed tree must reproduce the bytes exactly. Anything else means a tree has
        // more than one encoding, and an info hash computed from one of them is not reproducible.
        Node(depth: 3).Sample(node =>
        {
            byte[] once = BencodeWriter.Write(node);
            byte[] twice = BencodeWriter.Write(BencodeParser.Parse(once));

            Assert.Equal(once, twice);
        }, iter: 10_000);
    }

    [Fact]
    public void DictionaryKeysAreWrittenInAscendingByteOrder()
    {
        // The rule bencode actually states, checked as bytes rather than as text. Sorting by UTF-16
        // code unit agrees with this while keys are Latin1 and stops agreeing the moment someone
        // "fixes" the key encoding to UTF-8 - which is exactly the change that looks harmless.
        Gen.Select(Key, Leaf).Array[2, 8].Sample(pairs =>
        {
            var dictionary = new BDict();
            foreach (var (key, value) in pairs)
            {
                dictionary.Dict[key] = value;
            }

            var written = KeysInWrittenOrder(BencodeWriter.Write(dictionary));

            Assert.Equal(dictionary.Dict.Count, written.Count);
            for (int i = 1; i < written.Count; i++)
            {
                Assert.True(
                    written[i - 1].AsSpan().SequenceCompareTo(written[i]) < 0,
                    "keys were not in ascending byte order");
            }
        }, iter: 5_000);
    }

    [Fact]
    public void ParsingConsumesExactlyTheEncoding()
    {
        Node(depth: 3).Sample(node =>
        {
            byte[] encoded = BencodeWriter.Write(node);
            var (_, consumed) = BencodeParser.ParseWithConsumed(encoded);

            Assert.Equal(encoded.Length, consumed);
        }, iter: 10_000);
    }

    [Fact]
    public void TrailingBytesAreLeftUnconsumed()
    {
        // How a length confusion shows up in practice: a torrent file with junk appended, or a
        // metadata piece followed by the next message. The count is what tells the caller where the
        // value ended, so it has to be the end of the value and not the end of the buffer.
        Gen.Select(Node(depth: 2), Gen.Byte.Array[1, 8]).Sample((node, trailing) =>
        {
            byte[] encoded = BencodeWriter.Write(node);
            var (_, consumed) = BencodeParser.ParseWithConsumed([.. encoded, .. trailing]);

            Assert.Equal(encoded.Length, consumed);
        }, iter: 5_000);
    }

    /// <summary>
    /// Trees bounded well inside the parser's own depth and item caps, so a failure here is about
    /// the round trip rather than about a security limit firing.
    /// </summary>
    private static Gen<IBNode> Node(int depth)
    {
        if (depth == 0)
        {
            return Leaf;
        }

        var child = Node(depth - 1);

        return Gen.OneOf(
            Leaf,
            child.Array[0, 4].Select(items =>
            {
                var list = new BList();
                list.List.AddRange(items);
                return (IBNode)list;
            }),
            Gen.Select(Key, child).Array[0, 4].Select(pairs =>
            {
                var dictionary = new BDict();
                foreach (var (key, value) in pairs)
                {
                    dictionary.Dict[key] = value;
                }

                return (IBNode)dictionary;
            }));
    }

    /// <summary>
    /// Reads the keys back out of an encoded dictionary directly, without going through the parser,
    /// so a parser that reordered keys could not conceal a writer that had done the same.
    /// </summary>
    private static List<byte[]> KeysInWrittenOrder(byte[] encoded)
    {
        var keys = new List<byte[]>();
        int position = 1; // past the opening 'd'

        while (position < encoded.Length && encoded[position] != (byte)'e')
        {
            int colon = Array.IndexOf(encoded, (byte)':', position);
            int length = int.Parse(Encoding.ASCII.GetString(encoded, position, colon - position));

            keys.Add(encoded[(colon + 1)..(colon + 1 + length)]);
            position = SkipValue(encoded, colon + 1 + length);
        }

        return keys;
    }

    /// <summary>
    /// Steps over one encoded value. Only the shapes this test generates need handling.
    /// </summary>
    private static int SkipValue(byte[] encoded, int position)
    {
        switch ((char)encoded[position])
        {
            case 'i':
                return Array.IndexOf(encoded, (byte)'e', position) + 1;

            case 'l':
                position++;
                while (encoded[position] != (byte)'e')
                {
                    position = SkipValue(encoded, position);
                }

                return position + 1;

            case 'd':
                position++;
                while (encoded[position] != (byte)'e')
                {
                    position = SkipValue(encoded, SkipValue(encoded, position));
                }

                return position + 1;

            default:
                int colon = Array.IndexOf(encoded, (byte)':', position);
                int length = int.Parse(Encoding.ASCII.GetString(encoded, position, colon - position));
                return colon + 1 + length;
        }
    }

    private static bool NodesEqual(IBNode left, IBNode right)
    {
        if (left.Type != right.Type)
        {
            return false;
        }

        switch (left)
        {
            case BNumber number:
                return number.Value == ((BNumber)right).Value;

            case BString text:
                return text.Value.Span.SequenceEqual(((BString)right).Value.Span);

            case BList list:
                var otherList = (BList)right;
                return list.List.Count == otherList.List.Count
                    && list.List.Zip(otherList.List).All(pair => NodesEqual(pair.First, pair.Second));

            case BDict dictionary:
                var otherDictionary = (BDict)right;
                return dictionary.Dict.Count == otherDictionary.Dict.Count
                    && dictionary.Dict.All(entry =>
                        otherDictionary.Dict.TryGetValue(entry.Key, out var value) && NodesEqual(entry.Value, value));

            default:
                return false;
        }
    }
}
