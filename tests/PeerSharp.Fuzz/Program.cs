using System.Buffers;
using System.Text;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Dht;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using PeerSharp.Messages;
using SharpFuzz;

const string BencodeTarget = "bencode";
const string PeerMessageTarget = "peer-message";
const string TorrentMetadataTarget = "torrent-metadata";
const string DhtCompactTarget = "dht-compact";

if (args is ["--self-test"])
{
    ReplaySeedCorpus(BencodeTarget, FuzzBencode);
    ReplaySeedCorpus(PeerMessageTarget, FuzzPeerMessages);
    ReplaySeedCorpus(TorrentMetadataTarget, FuzzTorrentMetadata);
    ReplaySeedCorpus(DhtCompactTarget, FuzzDhtCompact);
    Console.WriteLine("Replayed all SharpFuzz seed inputs successfully.");
    return;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: PeerSharp.Fuzz <bencode|peer-message|torrent-metadata|dht-compact> | --self-test");
    Environment.ExitCode = 2;
    return;
}

Action<Stream> target = args[0] switch
{
    BencodeTarget => FuzzBencode,
    PeerMessageTarget => FuzzPeerMessages,
    TorrentMetadataTarget => FuzzTorrentMetadata,
    DhtCompactTarget => FuzzDhtCompact,
    _ => throw new ArgumentException($"Unknown fuzz target '{args[0]}'.")
};

Fuzzer.OutOfProcess.Run(target);

static void FuzzBencode(Stream stream)
{
    byte[] input = ReadAll(stream);

    try
    {
        BencodeParser.ParseWithConsumed(input);
    }
    catch (FormatException)
    {
        // Malformed bencode is ordinary untrusted input. Other exception types are findings.
    }
}

static void FuzzPeerMessages(Stream stream)
{
    var sequence = new ReadOnlySequence<byte>(ReadAll(stream));

    try
    {
        while (!sequence.IsEmpty)
        {
            PeerMessage? message = null;
            try
            {
                if (!PeerProtocol.TryDecodeMessage(ref sequence, out message, out int consumed))
                {
                    return;
                }

                if (consumed <= 0)
                {
                    throw new InvalidOperationException("A decoded peer message consumed no input.");
                }
            }
            finally
            {
                message?.Dispose();
            }
        }
    }
    catch (InvalidDataException)
    {
        // Invalid framing and payload lengths are expected. Other exception types are findings.
    }
}

static void FuzzTorrentMetadata(Stream stream)
{
    byte[] input = ReadAll(stream);

    try
    {
        TorrentFileParser.ParseInfoBytes(input);
    }
    catch (FormatException)
    {
        // The contract this target exists to check. MetadataDownload parses a peer's ut_metadata
        // response before it can verify the hash - it has to, because computing the hash is what
        // parsing produces - and catches FormatException and nothing else. Any other exception type
        // escapes into the peer loop from bytes a stranger chose, so any other exception type
        // reaching here is a finding rather than a malformed torrent.
    }
}

static void FuzzDhtCompact(Stream stream)
{
    byte[] input = ReadAll(stream);

    // No catch, deliberately. These decode a byte string lifted straight out of a UDP datagram from
    // an unknown source, and both walk it in fixed-size records, so the contract is total: every
    // input yields a list, a short or ragged tail is ignored, and no input throws. Any exception at
    // all is the finding.
    DhtCompactNodeCodec.Parse(input, ipv6: false);
    DhtCompactNodeCodec.Parse(input, ipv6: true);

    // Compact peers arrive as a bencoded list of strings rather than one blob, and the codec keys
    // off each string's length. Slicing the input gives the fuzzer control of those lengths, which
    // is the part worth exploring.
    DhtCompactPeerCodec.Parse(SliceIntoValues(input), ipv6: false);
    DhtCompactPeerCodec.Parse(SliceIntoValues(input), ipv6: true);
}

/// <summary>
/// Cuts the input into "values" of a width the input itself chooses.
/// </summary>
static List<ReadOnlyMemory<byte>> SliceIntoValues(byte[] input)
{
    var values = new List<ReadOnlyMemory<byte>>();
    if (input.Length == 0)
    {
        return values;
    }

    int width = 1 + (input[0] % 24);
    for (int offset = 0; offset < input.Length; offset += width)
    {
        values.Add(input.AsMemory(offset, Math.Min(width, input.Length - offset)));
    }

    return values;
}

static byte[] ReadAll(Stream stream)
{
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
}

static void ReplaySeedCorpus(string target, Action<Stream> fuzzTarget)
{
    string corpusDirectory = Path.Combine(AppContext.BaseDirectory, "corpus", target);
    foreach (string path in Directory.EnumerateFiles(corpusDirectory).Order())
    {
        byte[] input = target == PeerMessageTarget
            ? Convert.FromHexString(RemoveWhitespace(File.ReadAllText(path)))
            : Encoding.ASCII.GetBytes(File.ReadAllText(path).TrimEnd('\r', '\n'));

        using var stream = new MemoryStream(input, writable: false);
        fuzzTarget(stream);
    }
}

static string RemoveWhitespace(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
