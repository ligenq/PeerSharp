using System.Text;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>Options a test sets before <see cref="SyntheticPeer.Start"/>.</summary>
internal sealed class SyntheticPeerOptions
{
    /// <summary>
    /// Reset the connection part-way through the handshake, before answering anything. This is the
    /// peer that is at its connection limit or has just gone away - it says nothing about whether it
    /// supports encryption, which is precisely why guessing from it was wrong.
    /// </summary>
    public bool HangUpDuringHandshake { get; set; }

    /// <summary>Whether to claim BEP 10 in the reserved bytes and send an extension handshake.</summary>
    public bool AdvertiseExtensionProtocol { get; set; } = true;

    /// <summary>
    /// The extension ids this peer assigns, as sent in its <c>m</c> dictionary. BEP 10 numbers
    /// extensions per receiver, so these are the ids PeerSharp must address us by, and they are
    /// deliberately not the ones PeerSharp would pick for itself.
    /// </summary>
    public Dictionary<string, long> Extensions { get; } = new(StringComparer.Ordinal);

    /// <summary>The <c>metadata_size</c> to advertise, when this peer claims to hold metadata.</summary>
    public long? MetadataSize { get; set; }
}

/// <summary>One frame as it arrived, kept as bytes rather than as an interpretation of them.</summary>
internal readonly record struct WireFrame(byte Id, byte[] Payload)
{
    /// <summary>Not a real message id; a keep-alive is a zero-length frame with no id at all.</summary>
    public const byte KeepAlive = 0xFF;

    public const byte Extended = 20;

    public bool IsExtended => Id == Extended && Payload.Length >= 1;

    /// <summary>The extension id this frame was addressed to, which is the receiver's own numbering.</summary>
    public byte ExtendedId => IsExtended
        ? Payload[0]
        : throw new InvalidOperationException($"Frame {Id} is not an extended message.");

    public ReadOnlySpan<byte> ExtendedPayload => IsExtended
        ? Payload.AsSpan(1)
        : throw new InvalidOperationException($"Frame {Id} is not an extended message.");

    public override string ToString() => IsExtended
        ? $"extended(id {ExtendedId}, {Payload.Length - 1} bytes)"
        : Id == KeepAlive ? "keep-alive" : $"message {Id} ({Payload.Length} bytes)";
}

/// <summary>
/// One dialled connection and everything that crossed it.
/// </summary>
internal sealed class SyntheticConnection(int ordinal)
{
    private readonly List<WireFrame> _frames = [];
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Zero-based order of arrival.</summary>
    public int Ordinal { get; } = ordinal;

    /// <summary>
    /// Whether this attempt opened with a plaintext BitTorrent handshake rather than the MSE key
    /// exchange. This is read from the first byte on the socket, so it is what was actually offered.
    /// </summary>
    public bool StartedWithPlaintextHandshake { get; private set; }

    /// <summary>The eight reserved bytes from their handshake, when there was one.</summary>
    public byte[]? Reserved { get; private set; }

    /// <summary>Whether their reserved bytes claimed BEP 10.</summary>
    public bool ClaimsExtensionProtocol => Reserved is not null && (Reserved[5] & 0x10) != 0;

    /// <summary>A snapshot of every frame received, in order.</summary>
    public IReadOnlyList<WireFrame> Frames
    {
        get
        {
            lock (_frames)
            {
                return [.. _frames];
            }
        }
    }

    /// <summary>Every extended message, including the handshake at extension id zero.</summary>
    public IReadOnlyList<WireFrame> ExtendedFrames => [.. Frames.Where(static frame => frame.IsExtended)];

    internal void RecordOpening(bool plaintext) => StartedWithPlaintextHandshake = plaintext;

    internal void RecordHandshake(byte[] remainderOfHandshake)
    {
        // The 67 bytes after the leading 19: 18 of protocol string, 8 reserved, 20 info hash, 20 peer id.
        Reserved = remainderOfHandshake.AsSpan(18, 8).ToArray();
    }

    internal void Record(WireFrame frame)
    {
        lock (_frames)
        {
            _frames.Add(frame);
        }
    }

    internal void Complete() => _finished.TrySetResult();

    /// <summary>Waits for their BEP 10 handshake and returns it decoded by the synthetic peer's own parser.</summary>
    public async Task<Dictionary<string, object>> WaitForExtensionHandshakeAsync(
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        WireFrame? handshake = null;
        bool arrived = await SyntheticPeer.WaitForAsync(
            () =>
            {
                handshake = ExtendedFrames.Cast<WireFrame?>()
                    .FirstOrDefault(static frame => frame!.Value.ExtendedId == 0);
                return handshake is not null;
            },
            timeout,
            cancellationToken).ConfigureAwait(false);

        if (!arrived)
        {
            throw new TimeoutException(
                $"No BEP 10 handshake arrived within {timeout.TotalSeconds:0.#}s. Received: {Describe()}");
        }

        return SyntheticBencode.DecodeDictionary(handshake!.Value.ExtendedPayload, "The BEP 10 handshake");
    }

    /// <summary>Waits until at least one frame satisfies <paramref name="predicate"/>.</summary>
    public Task<bool> WaitForFrameAsync(
        Func<WireFrame, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return SyntheticPeer.WaitForAsync(() => Frames.Any(predicate), timeout, cancellationToken);
    }

    /// <summary>A readable account of the traffic, for an assertion message worth reading.</summary>
    public string Describe()
    {
        var frames = Frames;
        if (frames.Count == 0)
        {
            return "(nothing)";
        }

        var text = new StringBuilder();
        foreach (var frame in frames)
        {
            if (text.Length > 0)
            {
                text.Append(" -> ");
            }

            text.Append(frame.ToString());
        }

        return text.ToString();
    }
}
