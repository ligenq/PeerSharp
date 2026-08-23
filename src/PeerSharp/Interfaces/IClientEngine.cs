namespace PeerSharp.Interfaces;

/// <summary>
/// Core BitTorrent client interface. Provides methods for managing torrents,
/// initializing the client, and controlling the overall application lifecycle.
/// </summary>
public interface IClientEngine : IAsyncDisposable
{
    /// <summary>
    /// Gets the alerts manager for the client.
    /// </summary>
    IAlerts Alerts { get; }

    /// <summary>
    /// Gets the bandwidth manager for the client.
    /// </summary>
    IBandwidth Bandwidth { get; }

    /// <summary>
    /// Gets or sets whether the IP blocklist is enabled.
    /// Defaults to true after <see cref="LoadBlocklist"/> is called.
    /// </summary>
    bool BlocklistEnabled { get; set; }

    /// <summary>
    /// Gets the actual TCP port the listener is bound to.
    /// </summary>
    int BoundTcpPort { get; }

    /// <summary>
    /// Gets the actual UDP port the listener is bound to.
    /// </summary>
    int BoundUdpPort { get; }

    /// <summary>
    /// Gets or sets whether GeoIP lookups are enabled.
    /// Defaults to true after <see cref="LoadGeoIp"/> is called.
    /// </summary>
    bool GeoIpEnabled { get; set; }

    /// <summary>
    /// Gets the settings instance for the client.
    /// </summary>
    Settings Settings { get; }

    /// <summary>
    /// Downloads only the metadata for a magnet link and returns it as a
    /// <see cref="TorrentFile"/>, without starting (or keeping) a download. Use the result to
    /// preview the file list, then add it via AddTorrentAsync with file selections; persist
    /// <see cref="TorrentFile.RawData"/> to skip the metadata download for the same magnet in
    /// the future.
    ///
    /// <para>
    /// A torrent is added internally to do the fetch, but it is invisible to the caller and stays
    /// that way. It does not appear in <see cref="GetTorrents"/> or <see cref="GetTorrent"/>, emits
    /// no alerts, takes no session entry, and does not participate in the queue. It also makes no
    /// claim on the info hash: fetching metadata for a hash already added is allowed, and adding
    /// that hash while a fetch is in flight is not blocked by it.
    /// </para>
    ///
    /// <para>
    /// The fetch emits no engine-wide <see cref="AlertId.MetadataProgressChanged"/> alert. Use
    /// <see cref="GetMagnetMetadataWithProgressAsync"/> when this operation needs a scoped progress
    /// indicator.
    /// </para>
    /// </summary>
    /// <param name="magnetLink">The magnet link to resolve.</param>
    /// <param name="cancellationToken">Cancellation token; use a timeout-based token to bound the fetch.</param>
    /// <exception cref="ArgumentNullException">Thrown when magnetLink is null.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the token is cancelled before metadata arrives.</exception>
    Task<TorrentFile> GetMagnetMetadataAsync(MagnetLink magnetLink, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads only a magnet's metadata like <see cref="GetMagnetMetadataAsync"/>, while reporting
    /// progress specifically for this operation. The transient torrent remains absent from the engine
    /// and its global alert stream.
    /// </summary>
    /// <param name="magnetLink">The magnet link to resolve.</param>
    /// <param name="progress">Receives metadata piece progress from 0.0 to 1.0.</param>
    /// <param name="cancellationToken">Cancellation token; use a timeout-based token to bound the fetch.</param>
    Task<TorrentFile> GetMagnetMetadataWithProgressAsync(
        MagnetLink magnetLink,
        IProgress<MetadataProgress> progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a torrent from a magnet link. Metadata is fetched from the swarm in the background,
    /// so the returned torrent may not know its file list yet - await
    /// <see cref="ITorrent.WaitForMetadataAsync"/>, or set
    /// <see cref="AddTorrentOptions.StopAfterMetadata"/> to adjust file selection before the
    /// download starts.
    /// </summary>
    /// <param name="magnetLink">The magnet link to add.</param>
    /// <param name="options">Download path and other per-torrent options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when magnetLink is null.</exception>
    /// <exception cref="TorrentAlreadyExistsException">Thrown when a torrent with the same hash is already added.</exception>
    Task<ITorrent> AddMagnetAsync(
        MagnetLink magnetLink,
        AddTorrentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// BEP 46: resolves a self-updating magnet link to the torrent it currently names.
    ///
    /// A link carrying <c>xs=urn:btpk:</c> has no info-hash of its own - the publisher stores the
    /// current one in a signed DHT record, so the same link keeps working across releases. Resolve
    /// it, then add the resulting info-hash with the ordinary magnet or torrent APIs.
    /// </summary>
    /// <param name="magnetLink">A link with <see cref="Core.MagnetLink.IsSelfUpdating"/> set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The current info-hash and version, or null when no node holds a usable record. Records that
    /// fail verification read as absent, since the DHT is untrusted input.
    /// </returns>
    /// <exception cref="ArgumentException">The link carries no public key.</exception>
    /// <exception cref="InvalidOperationException">The DHT is not available.</exception>
    Task<SelfUpdatingTorrentInfo?> ResolveSelfUpdatingMagnetAsync(
        MagnetLink magnetLink,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// BEP 46: resolves a publisher identity to the torrent it currently names.
    /// </summary>
    /// <param name="publisher">The publisher to follow; only the public key is used.</param>
    /// <param name="salt">Optional salt selecting which of the publisher's records to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SelfUpdatingTorrentInfo?> ResolveSelfUpdatingTorrentAsync(
        TorrentPublisherKey publisher,
        ReadOnlyMemory<byte> salt = default,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// BEP 46: publishes the info-hash that subscribers following this identity should now fetch.
    ///
    /// The version number is chosen automatically from whatever is currently published, and the
    /// write is compare-and-swapped against it so a concurrent publish from another instance of
    /// the same identity fails rather than silently overwriting. The record is then kept alive in
    /// the DHT for as long as this engine runs.
    /// </summary>
    /// <param name="publisher">The publishing identity; must hold private key material.</param>
    /// <param name="infoHash">The info-hash of the new version. Must be a v1 (20-byte) hash.</param>
    /// <param name="salt">Optional salt, letting one identity publish several torrents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many DHT nodes accepted the record, and the version number published.</returns>
    /// <exception cref="InvalidOperationException">
    /// The identity cannot publish, or the DHT is not available.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The DHT routing table did not become usable within two minutes.
    /// </exception>
    Task<(int AcceptedByNodes, long Version)> PublishSelfUpdatingTorrentAsync(
        TorrentPublisherKey publisher,
        InfoHash infoHash,
        ReadOnlyMemory<byte> salt = default,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// BEP 46: stops keeping a published record alive. The record remains in the DHT until it
    /// expires of its own accord, which takes a couple of hours.
    /// </summary>
    /// <returns>True if the record was being maintained by this engine.</returns>
    bool StopMaintainingSelfUpdatingTorrent(TorrentPublisherKey publisher, ReadOnlyMemory<byte> salt = default);

    /// <summary>
    /// BEP 51: crawls the DHT and streams the distinct info-hashes it reports, for building a search
    /// index or surveying what is being shared.
    ///
    /// <para>
    /// The crawl walks outward from the routing table, asking each node for a sample of what it holds
    /// and for more nodes, and honours the requery interval every node asks for. It has no natural
    /// end: it runs until <see cref="DhtIndexerOptions.MaxInfoHashes"/> is reached, the enumeration is
    /// abandoned, or the token is cancelled - which throws <see cref="OperationCanceledException"/>,
    /// as cancelling an <see cref="IAsyncEnumerable{T}"/> does. Leaving the loop with <c>break</c>
    /// stops the crawl without an exception. While every known node is inside the interval it asked
    /// for, the crawl waits rather than finishing.
    /// </para>
    ///
    /// <para>
    /// A result is one node's claim to hold peers for a hash - nothing more. To learn what a hash
    /// actually is, fetch its metadata with <see cref="GetMagnetMetadataAsync"/>:
    /// <code>
    /// await foreach (var found in engine.DiscoverInfoHashesAsync(cancellationToken: token))
    /// {
    ///     var link = MagnetLink.Parse($"magnet:?xt=urn:btih:{found.InfoHash}");
    ///     var metadata = await engine.GetMagnetMetadataAsync(link, token);
    /// }
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="options">Crawl limits and politeness settings. Defaults are used when null.</param>
    /// <param name="cancellationToken">Cancellation token; ending the crawl is the normal way to stop.</param>
    /// <returns>Distinct info-hashes, streamed as they are discovered.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An option is outside its valid range; the message names which one.
    /// </exception>
    /// <exception cref="InvalidOperationException">The DHT is not available.</exception>
    /// <exception cref="TimeoutException">
    /// The DHT routing table was still empty after two minutes, leaving the crawl nowhere to start.
    /// </exception>
    IAsyncEnumerable<DiscoveredInfoHash> DiscoverInfoHashesAsync(
        DhtIndexerOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a torrent from a TorrentFile.
    /// </summary>
    /// <param name="torrentFile">The parsed torrent file.</param>
    /// <param name="options">Options for adding the torrent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The added torrent.</returns>
    /// <exception cref="ArgumentNullException">Thrown when torrentFile is null.</exception>
    /// <exception cref="TorrentAlreadyExistsException">Thrown when a torrent with the same hash already exists.</exception>
    /// <exception cref="TorrentException">Thrown when the operation fails.</exception>
    Task<ITorrent> AddTorrentAsync(
        TorrentFile torrentFile,
        AddTorrentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the IP blocklist and disables blocklist filtering.
    /// </summary>
    void ClearBlocklist();

    /// <summary>
    /// Clears the GeoIP database and disables GeoIP lookups.
    /// </summary>
    void ClearGeoIp();

    /// <summary>
    /// Gets the status of automatic port mapping (UPnP, NAT-PMP).
    /// </summary>
    /// <returns>A list of port mapping statuses.</returns>
    IReadOnlyList<PortMappingStatus> GetPortMappingStatus();

    /// <summary>
    /// Gets a snapshot of the current engine-wide statistics.
    /// </summary>
    /// <returns>Aggregate statistics for all torrents.</returns>
    EngineStats GetStats();

    /// <summary>
    /// Gets a torrent by its info hash.
    /// </summary>
    /// <param name="hash">The info hash.</param>
    /// <returns>The torrent if found, null otherwise.</returns>
    ITorrent? GetTorrent(InfoHash hash);

    /// <summary>
    /// Gets a snapshot of all torrents currently managed by the client.
    /// </summary>
    /// <returns>Read-only list of all torrents.</returns>
    IReadOnlyList<ITorrent> GetTorrents();

    /// <summary>
    /// Whether the session is paused. See <see cref="PauseAsync(CancellationToken)"/>.
    /// </summary>
    bool IsPaused { get; }

    /// <summary>
    /// Stops every running torrent at once, remembering which were running.
    /// </summary>
    /// <param name="cancellationToken">Cancels partway through; torrents already stopped stay stopped.</param>
    /// <remarks>
    /// <para>
    /// For "stop everything now" - the machine is going to sleep, the user pulled the network, a
    /// metered connection came up. <see cref="ResumeAsync(CancellationToken)"/> starts again exactly
    /// the torrents this stopped, leaving alone any the user had already stopped by hand.
    /// </para>
    /// <para>
    /// A torrent added while paused waits for the resume rather than starting on its own, whatever
    /// its <see cref="Config.AddTorrentOptions.StartImmediately"/> says. Removing a torrent while
    /// paused is fine: the resume skips what is no longer there.
    /// </para>
    /// <para>
    /// The engine keeps running - the DHT node, the listeners and the port mappings all stay up, so
    /// this is not a substitute for disposing the engine.
    /// </para>
    /// </remarks>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the torrents that <see cref="PauseAsync(CancellationToken)"/> stopped.
    /// </summary>
    /// <param name="cancellationToken">Cancels partway through; torrents already started stay started.</param>
    /// <remarks>Does nothing if the session is not paused.</remarks>
    Task ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes the BitTorrent client asynchronously.
    /// Sets up listeners, DHT, UPnP, and loads previously added torrents.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if already initialized.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the token is cancelled during initialization.</exception>
    /// <remarks>
    /// If initialization fails or is cancelled the engine may be partially started (listeners
    /// bound, some persisted torrents restored). It is left in a retryable state - calling
    /// <see cref="InitializeAsync"/> again is allowed - but a caller that gives up must still
    /// dispose the engine to release those resources.
    /// </remarks>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the IP blocklist from a stream and enables blocklist filtering.
    /// Supports P2P format (Description:StartIP-EndIP), CIDR notation, and single IPs.
    /// </summary>
    /// <param name="stream">Stream containing blocklist data.</param>
    /// <remarks>
    /// The stream is read to completion but is not disposed by this method.
    /// The caller is responsible for disposing the stream after this method returns.
    /// </remarks>
    void LoadBlocklist(Stream stream);

    /// <summary>
    /// Loads the IP blocklist from a stream asynchronously and enables blocklist filtering.
    /// Supports P2P format (Description:StartIP-EndIP), CIDR notation, and single IPs.
    /// </summary>
    /// <param name="stream">Stream containing blocklist data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The stream is read to completion but is not disposed by this method.
    /// The caller is responsible for disposing the stream after this method returns.
    /// </remarks>
    Task LoadBlocklistAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the GeoIP database from a stream and enables GeoIP lookups.
    /// </summary>
    /// <param name="stream">Stream containing GeoIP data.</param>
    /// <remarks>
    /// The stream is read to completion but is not disposed by this method.
    /// The caller is responsible for disposing the stream after this method returns.
    /// </remarks>
    void LoadGeoIp(Stream stream);

    /// <summary>
    /// Loads the GeoIP database from a stream asynchronously and enables GeoIP lookups.
    /// </summary>
    /// <param name="stream">Stream containing GeoIP data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The stream is read to completion but is not disposed by this method.
    /// The caller is responsible for disposing the stream after this method returns.
    /// </remarks>
    Task LoadGeoIpAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves session data for all torrents immediately.
    /// Call this after state changes (start, stop) to ensure persistence.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a torrent by its info hash.
    /// </summary>
    /// <param name="hash">The info hash of the torrent to remove.</param>
    /// <param name="options">Options specifying what to delete.</param>
    /// <param name="cancellationToken">
    /// Checked before the removal begins. Once the torrent has been deregistered the removal
    /// runs to completion, so it can never be left half-removed.
    /// </param>
    /// <exception cref="TorrentNotFoundException">Thrown when the torrent is not found.</exception>
    Task RemoveTorrentAsync(
        InfoHash hash,
        RemoveOptions options = RemoveOptions.None,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a torrent.
    /// </summary>
    /// <param name="torrent">The torrent to remove.</param>
    /// <param name="options">Options specifying what to delete.</param>
    /// <param name="cancellationToken">
    /// Checked before the removal begins. Once the torrent has been deregistered the removal
    /// runs to completion, so it can never be left half-removed.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when torrent is null.</exception>
    /// <exception cref="TorrentNotFoundException">Thrown when the torrent is not found.</exception>
    Task RemoveTorrentAsync(
        ITorrent torrent,
        RemoveOptions options = RemoveOptions.None,
        CancellationToken cancellationToken = default);
}
