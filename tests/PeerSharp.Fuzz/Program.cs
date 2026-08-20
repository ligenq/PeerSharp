using System.Buffers;
using System.Text;
using PeerSharp.BEncoding;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using PeerSharp.Messages;
using SharpFuzz;

const string BencodeTarget = "bencode";
const string PeerMessageTarget = "peer-message";
const string TorrentMetadataTarget = "torrent-metadata";

if (args is ["--self-test"])
{
    ReplaySeedCorpus(BencodeTarget, FuzzBencode);
    ReplaySeedCorpus(PeerMessageTarget, FuzzPeerMessages);
    ReplaySeedCorpus(TorrentMetadataTarget, FuzzTorrentMetadata);
    Console.WriteLine("Replayed all SharpFuzz seed inputs successfully.");
    return;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: PeerSharp.Fuzz <bencode|peer-message|torrent-metadata> | --self-test");
    Environment.ExitCode = 2;
    return;
}

Action<Stream> target = args[0] switch
{
    BencodeTarget => FuzzBencode,
    PeerMessageTarget => FuzzPeerMessages,
    TorrentMetadataTarget => FuzzTorrentMetadata,
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
