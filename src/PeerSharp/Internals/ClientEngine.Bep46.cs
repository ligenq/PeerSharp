using Microsoft.Extensions.Logging;
using PeerSharp.Core;
using PeerSharp.Internals.Dht;

namespace PeerSharp.Internals;

/// <summary>
/// BEP 46 surface on the engine: resolving a publisher key to the torrent it currently names, and
/// publishing updates under a key you own.
///
/// <para>
/// Records expire from the DHT after a couple of hours, and BEP 46 states that "both publisher and
/// consumer should periodically put the mutable items they have active to keep them alive". The
/// engine therefore re-publishes everything it has published, on a timer, for as long as it runs.
/// </para>
/// </summary>
internal sealed partial class ClientEngine
{
    /// <summary>
    /// How often published records are re-put. Comfortably inside the two-hour expiry so a single
    /// missed round is not fatal.
    /// </summary>
    private static readonly TimeSpan RepublishInterval = TimeSpan.FromMinutes(30);

    private readonly Lock _publishedRecordsLock = new();

    /// <summary>Signed records this engine has published, keyed by DHT address.</summary>
    private readonly Dictionary<DhtTarget, DhtMutableItem> _publishedRecords = [];

    private Task? _republishTask;

    /// <summary>
    /// Starts the keep-alive loop. Runs unconditionally rather than on first publish: the loop is
    /// idle while nothing has been published, and starting it here keeps its lifetime tied to
    /// initialisation like every other background loop.
    /// </summary>
    private void StartRepublishLoop()
    {
        _republishCts?.Dispose();
        _republishCts = new CancellationTokenSource();
        _republishTask = RunRepublishLoopAsync(_republishCts.Token);
    }

    /// <summary>
    /// Resolves a self-updating magnet link to the torrent it currently names.
    /// </summary>
    /// <param name="magnetLink">A link carrying <c>xs=urn:btpk:</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The current info-hash and version, or null when no node holds a usable record. Unverifiable
    /// records read as absent: the DHT is untrusted input.
    /// </returns>
    /// <exception cref="ArgumentException">The link is not self-updating.</exception>
    /// <exception cref="InvalidOperationException">DHT is disabled.</exception>
    public async Task<SelfUpdatingTorrentInfo?> ResolveSelfUpdatingMagnetAsync(
        MagnetLink magnetLink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(magnetLink);

        if (!magnetLink.IsSelfUpdating)
        {
            throw new ArgumentException(
                "This magnet link carries no BEP 46 public key. Check MagnetLink.IsSelfUpdating first.",
                nameof(magnetLink));
        }

        var resolver = RequireBep46Resolver();
        var resolution = await resolver.ResolveAsync(
            magnetLink.PublicKey.ToArray(),
            magnetLink.Salt.IsEmpty ? null : magnetLink.Salt.ToArray(),
            cancellationToken).ConfigureAwait(false);

        return resolution is null
            ? null
            : new SelfUpdatingTorrentInfo(resolution.Value.InfoHash, resolution.Value.SequenceNumber);
    }

    /// <summary>
    /// Resolves a publisher key to the torrent it currently names.
    /// </summary>
    /// <param name="publisher">The publisher to follow. Only the public key is used.</param>
    /// <param name="salt">Optional salt selecting which of the publisher's records to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SelfUpdatingTorrentInfo?> ResolveSelfUpdatingTorrentAsync(
        TorrentPublisherKey publisher,
        ReadOnlyMemory<byte> salt = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        var resolver = RequireBep46Resolver();
        var resolution = await resolver.ResolveAsync(
            publisher.PublicKey.ToArray(),
            salt.IsEmpty ? null : salt.ToArray(),
            cancellationToken).ConfigureAwait(false);

        return resolution is null
            ? null
            : new SelfUpdatingTorrentInfo(resolution.Value.InfoHash, resolution.Value.SequenceNumber);
    }

    /// <summary>
    /// Publishes the current version of a self-updating torrent.
    ///
    /// Reads the existing record first to pick the next version number and to compare-and-swap
    /// against it, so a concurrent publish from another instance of the same identity fails rather
    /// than one silently overwriting the other. The record is then kept alive automatically for as
    /// long as this engine runs.
    /// </summary>
    /// <param name="publisher">The publishing identity; must hold private key material.</param>
    /// <param name="infoHash">The info-hash subscribers should now fetch. Must be a v1 hash.</param>
    /// <param name="salt">Optional salt, letting one identity publish several torrents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of DHT nodes that accepted the record, and the version published.</returns>
    /// <exception cref="InvalidOperationException">
    /// The identity holds no private key, or DHT is disabled.
    /// </exception>
    public async Task<(int AcceptedByNodes, long Version)> PublishSelfUpdatingTorrentAsync(
        TorrentPublisherKey publisher,
        InfoHash infoHash,
        ReadOnlyMemory<byte> salt = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        if (!publisher.CanPublish)
        {
            throw new InvalidOperationException(
                "This identity holds no private key, so it cannot publish. Use TorrentPublisherKey.Create, FromSeed or FromExpandedKey.");
        }

        var resolver = RequireBep46Resolver();
        byte[]? saltBytes = salt.IsEmpty ? null : salt.ToArray();

        var current = await resolver.ResolveAsync(publisher.PublicKey.ToArray(), saltBytes, cancellationToken)
            .ConfigureAwait(false);

        long next = (current?.SequenceNumber ?? -1) + 1;

        var item = DhtItemCodec.CreateSigned(publisher, salt.Span, next, Bep46Resolver.BuildRecord(infoHash));

        int accepted = await resolver
            .PublishAsync(publisher, infoHash, next, saltBytes, current?.SequenceNumber, cancellationToken)
            .ConfigureAwait(false);

        // Remembered regardless of how many nodes took it: the republish loop is also the retry
        // path when a publish landed on too few nodes.
        lock (_publishedRecordsLock)
        {
            _publishedRecords[item.Target] = item;
        }

        _logger.LogInformation(
            "Published self-updating torrent version {Version} pointing at {InfoHash}; accepted by {Accepted} node(s)",
            next,
            infoHash,
            accepted);

        return (accepted, next);
    }

    /// <summary>
    /// Stops keeping a published record alive. The record stays in the DHT until it expires.
    /// </summary>
    /// <returns>True if the record was being maintained.</returns>
    public bool StopMaintainingSelfUpdatingTorrent(TorrentPublisherKey publisher, ReadOnlyMemory<byte> salt = default)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        var target = Bep46Resolver.ComputeTarget(publisher.PublicKey.Span, salt.Span);
        lock (_publishedRecordsLock)
        {
            return _publishedRecords.Remove(target);
        }
    }

    private Bep46Resolver RequireBep46Resolver()
    {
        if (Dht is not DhtManager dht)
        {
            throw new InvalidOperationException(
                "BEP 46 requires the DHT. Enable it via Settings.Dht and start the engine first.");
        }

        return new Bep46Resolver(dht, _loggerFactory);
    }

    /// <summary>
    /// Re-publishes every record this engine owns, forever, so they do not expire out of the DHT.
    /// Failures are logged and retried on the next round rather than ending the loop - a transient
    /// network problem should not silently stop a publisher's records being maintained.
    /// </summary>
    private async Task RunRepublishLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RepublishInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            DhtMutableItem[] records;
            lock (_publishedRecordsLock)
            {
                records = [.. _publishedRecords.Values];
            }

            if (records.Length == 0 || Dht is not DhtManager dht)
            {
                continue;
            }

            var resolver = new Bep46Resolver(dht, _loggerFactory);
            foreach (var record in records)
            {
                try
                {
                    int accepted = await resolver.RefreshAsync(record, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug(
                        "Refreshed self-updating record {Target}; accepted by {Accepted} node(s)",
                        record.Target,
                        accepted);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refresh self-updating record {Target}", record.Target);
                }
            }
        }
    }
}
