namespace PeerSharp.Core;

/// <summary>
/// Controls a BEP 51 crawl of the DHT.
/// </summary>
public sealed class DhtIndexerOptions
{
    /// <summary>
    /// Whether to emit a hash again when a different sampled node reports it. Default is
    /// <see langword="false"/>, preserving the distinct stream. Repeat sightings are an untrusted
    /// ranking hint, not proof of popularity, availability, or safety.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxInfoHashes"/> continues to count unique hashes, not emitted sightings.
    /// </remarks>
    public bool ReturnDuplicateSightings { get; set; }

    /// <summary>
    /// How many nodes are queried at once. The crawl is entirely network-bound, so this is the main
    /// throughput knob - and the main politeness one, since it caps how much traffic the swarm sees
    /// from this process.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    /// Stops the crawl after this many distinct info-hashes, or null to run until cancelled.
    ///
    /// <para>
    /// This also bounds memory: duplicate suppression has to remember every hash already returned,
    /// so an unbounded crawl grows without limit for as long as it runs. Prefer a limit, or consume
    /// the stream and cancel when you have enough.
    /// </para>
    /// </summary>
    public int? MaxInfoHashes { get; set; } = 100_000;

    /// <summary>
    /// The shortest time to wait before asking the same node again, applied when a node reports an
    /// interval below it (including zero).
    ///
    /// <para>
    /// BEP 51 says an indexer should honour the interval a node returns; this is the floor for nodes
    /// that ask for nothing, so a crawl cannot be talked into hammering one node.
    /// </para>
    /// </summary>
    public TimeSpan MinNodeRequeryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many known nodes the crawl frontier holds. Once reached, newly discovered nodes are
    /// dropped and the crawl continues with the nodes it already has.
    /// </summary>
    public int MaxTrackedNodes { get; set; } = 10_000;

    /// <summary>
    /// Throws if the options are unusable. The offending property is named in the message; the
    /// parameter name is the caller's own, since from their side it is the options argument that is
    /// wrong.
    /// </summary>
    /// <param name="paramName">The parameter these options arrived as.</param>
    internal void Validate(string paramName)
    {
        if (MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(paramName, MaxConcurrency, $"{nameof(MaxConcurrency)} must be at least 1.");
        }

        if (MaxInfoHashes is { } max && max < 1)
        {
            throw new ArgumentOutOfRangeException(paramName, max, $"{nameof(MaxInfoHashes)} must be at least 1 when set.");
        }

        if (MinNodeRequeryInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, MinNodeRequeryInterval, $"{nameof(MinNodeRequeryInterval)} cannot be negative.");
        }

        if (MaxTrackedNodes < 1)
        {
            throw new ArgumentOutOfRangeException(paramName, MaxTrackedNodes, $"{nameof(MaxTrackedNodes)} must hold at least 1 node.");
        }
    }
}
