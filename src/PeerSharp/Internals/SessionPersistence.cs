using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace PeerSharp.Internals;

/// <summary>
/// File-based implementation of session persistence.
/// Stores torrent data in a directory structure:
/// {SessionPath}/torrents/{hash_hex}/
///   - torrent.torrent (raw .torrent file)
///   - magnet.txt (magnet link if applicable)
///   - resume.dat (resume data bytes)
///   - options.json (saved options)
/// </summary>
internal sealed class SessionPersistence : ISessionPersistence
{
    private const string MagnetFileName = "magnet.txt";
    private const string OptionsFileName = "options.json";
    private const string ResumeFileName = "resume.dat";
    private const string TorrentFileName = "torrent.torrent";
    private const string TorrentsFolder = "torrents";
    private const string DhtStateFileName = "dht.json";

    private readonly Lock _lock = new();

    /// <summary>
    /// Per-hash mutual exclusion for the multi-file entry directories, so a save never publishes
    /// half of one entry and half of another and never races a delete. Gates are reference
    /// counted and removed once idle: a long-lived session that churns torrents would otherwise
    /// accumulate one <see cref="SemaphoreSlim"/> per info hash it has ever seen.
    /// </summary>
    private readonly Lock _entryGateLock = new();
    private readonly Dictionary<InfoHash, EntryGate> _entryGates = [];

    private readonly ILogger<SessionPersistence> _logger;
    private readonly string _sessionPath;

    public SessionPersistence(string sessionPath, ILogger<SessionPersistence> logger)
    {
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            throw new ArgumentException("SessionPath must be specified when session persistence is enabled.", nameof(sessionPath));
        }

        _sessionPath = sessionPath;
        _logger = logger;

        EnsureDirectoryExists(_sessionPath);
        EnsureDirectoryExists(GetTorrentsPath());
    }

    public async Task DeleteAsync(InfoHash hash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var gate = RentEntryGate(hash);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var torrentDir = GetTorrentPath(hash);
                if (Directory.Exists(torrentDir))
                {
                    try
                    {
                        // Recursive directory deletion has no native async API and can be slow on
                        // large or network-backed sessions, so keep it off the caller's thread.
                        await Task.Run(() => Directory.Delete(torrentDir, recursive: true), cancellationToken).ConfigureAwait(false);
                        _logger.LogDebug("Deleted torrent entry {Hash}", hash);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to delete torrent entry {Hash}", hash);
                    }
                }
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            ReturnEntryGate(hash, gate);
        }
    }

    public async Task<DhtState?> LoadDhtStateAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_sessionPath, DhtStateFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize(json, PeerSharpJsonContext.Default.DhtStateDto);

            if (dto == null)
            {
                return null;
            }

            byte[]? nodeId = null;
            if (!string.IsNullOrEmpty(dto.NodeId))
            {
                try
                {
                    nodeId = Convert.FromHexString(dto.NodeId);
                }
                catch
                {
                    // Ignore invalid node ID
                }
            }

            var nodes = new List<DhtNode>();
            if (dto.Nodes != null)
            {
                foreach (var nodeDto in dto.Nodes)
                {
                    if (System.Net.IPAddress.TryParse(nodeDto.Ip, out var ip) &&
                        nodeDto.Port > 0 && nodeDto.Port <= 65535)
                    {
                        byte[] id;
                        try
                        {
                            id = Convert.FromHexString(nodeDto.Id);
                        }
                        catch
                        {
                            continue;
                        }

                        nodes.Add(new DhtNode(id, new System.Net.IPEndPoint(ip, nodeDto.Port)));
                    }
                }
            }

            return new DhtState(nodeId, nodes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load DHT state");
            return null;
        }
    }

    public async Task<IReadOnlyList<SavedTorrentEntry>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var torrentsPath = GetTorrentsPath();

        if (!Directory.Exists(torrentsPath))
        {
            return [];
        }

        var torrentDirs = await Task.Run(
            () => Directory.GetDirectories(torrentsPath),
            cancellationToken).ConfigureAwait(false);
        if (torrentDirs.Length == 0)
        {
            return [];
        }

        // Each entry lives in its own directory and reads a handful of small files, so the
        // load is I/O-bound and independent per directory. Read them with a bounded fan-out
        // instead of serially. A ConcurrentBag collects results since order is irrelevant.
        var bag = new ConcurrentBag<SavedTorrentEntry>();
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
        };

        await Parallel.ForEachAsync(torrentDirs, options, async (torrentDir, ct) =>
        {
            try
            {
                var entry = await LoadEntryAsync(torrentDir, ct).ConfigureAwait(false);
                if (entry != null)
                {
                    bag.Add(entry);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load torrent entry from {Path}", torrentDir);
            }
        }).ConfigureAwait(false);

        var entries = bag.ToArray();
        _logger.LogInformation("Loaded {Count} torrent entries from session", entries.Length);
        return entries;
    }

    public async Task SaveAllAsync(IEnumerable<SavedTorrentEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
        };
        await Parallel.ForEachAsync(entries, options, (entry, ct) => new ValueTask(SaveAsync(entry, ct))).ConfigureAwait(false);
    }

    public async Task SaveAsync(SavedTorrentEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var torrentDir = GetTorrentPath(entry.Hash);
        var gate = RentEntryGate(entry.Hash);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_lock)
                {
                    EnsureDirectoryExists(torrentDir);
                }

                var writes = new List<Task>(4);

                if (entry.TorrentFileData != null)
                {
                    writes.Add(WriteAllBytesAtomicAsync(
                        Path.Combine(torrentDir, TorrentFileName),
                        entry.TorrentFileData,
                        cancellationToken));
                }

                if (!string.IsNullOrEmpty(entry.MagnetLink))
                {
                    writes.Add(WriteAllTextAtomicAsync(
                        Path.Combine(torrentDir, MagnetFileName),
                        entry.MagnetLink,
                        cancellationToken));
                }

                if (entry.ResumeData != null)
                {
                    writes.Add(WriteAllBytesAtomicAsync(
                        Path.Combine(torrentDir, ResumeFileName),
                        entry.ResumeData.Data,
                        cancellationToken));
                }

                if (entry.Options != null)
                {
                    var optionsJson = JsonSerializer.Serialize(entry.Options, PeerSharpJsonContext.Default.SavedTorrentOptions);
                    writes.Add(WriteAllTextAtomicAsync(
                        Path.Combine(torrentDir, OptionsFileName),
                        optionsJson,
                        cancellationToken));
                }

                await Task.WhenAll(writes).ConfigureAwait(false);
                _logger.LogDebug("Saved torrent entry {Hash}", entry.Hash);
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            ReturnEntryGate(entry.Hash, gate);
        }
    }

    public async Task SaveDhtStateAsync(DhtState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var dto = new DhtStateDto
        {
            NodeId = state.NodeId != null ? Convert.ToHexString(state.NodeId) : null,
            Nodes = [.. state.Nodes.Select(n => new DhtNodeDto
            {
                Id = Convert.ToHexString(n.Id),
                Ip = n.EndPoint.Address.ToString(),
                Port = n.EndPoint.Port
            })]
        };

        var json = JsonSerializer.Serialize(dto, PeerSharpJsonContext.Default.DhtStateDto);
        var path = Path.Combine(_sessionPath, DhtStateFileName);

        await WriteAllTextAtomicAsync(path, json, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Saved DHT state with {Count} nodes", state.Nodes.Count);
    }

    internal sealed class DhtStateDto
    {
        public string? NodeId { get; set; }
        public List<DhtNodeDto> Nodes { get; set; } = [];
    }

    internal sealed class DhtNodeDto
    {
        public string Id { get; set; } = string.Empty;
        public string Ip { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    private static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    /// <summary>
    /// Writes to a temporary file, forces it to the physical device, then renames over the
    /// destination. The rename gives readers an all-or-nothing view; the flush is what makes the
    /// result survive a crash. Without it the rename can publish whatever the OS write cache
    /// happened to contain - i.e. truncated resume data replacing a perfectly good copy.
    /// </summary>
    private static async Task WriteAllBytesAtomicAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                PreallocationSize = data.Length
            };

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                // No async equivalent exists for the flush-to-disk barrier, and this path runs
                // once per entry per periodic save rather than per block.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static Task WriteAllTextAtomicAsync(string path, string text, CancellationToken cancellationToken)
    {
        // UTF-8 without a BOM, matching what File.WriteAllTextAsync produced before.
        return WriteAllBytesAtomicAsync(path, Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    /// <summary>
    /// Takes a reference on the gate for <paramref name="hash"/>, creating it if needed. Must be
    /// paired with <see cref="ReturnEntryGate"/>.
    /// </summary>
    private EntryGate RentEntryGate(InfoHash hash)
    {
        lock (_entryGateLock)
        {
            if (!_entryGates.TryGetValue(hash, out var gate))
            {
                gate = new EntryGate();
                _entryGates[hash] = gate;
            }

            gate.RefCount++;
            return gate;
        }
    }

    /// <summary>
    /// Drops a reference taken by <see cref="RentEntryGate"/> and evicts the gate once no
    /// operation holds it. Eviction and the reference check happen under the same lock, so a
    /// caller can never rent a gate that is about to be disposed.
    /// </summary>
    private void ReturnEntryGate(InfoHash hash, EntryGate gate)
    {
        lock (_entryGateLock)
        {
            if (--gate.RefCount > 0)
            {
                return;
            }

            if (_entryGates.TryGetValue(hash, out var current) && ReferenceEquals(current, gate))
            {
                _entryGates.Remove(hash);
            }

            gate.Semaphore.Dispose();
        }
    }

    private sealed class EntryGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Guarded by the owner's entry-gate lock.</summary>
        public int RefCount { get; set; }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort only: a failed temporary-file cleanup must not mask the original
            // write/cancellation exception, and the next session scan ignores *.tmp files.
        }
        catch (UnauthorizedAccessException)
        {
            // Same best-effort rule as IOException; preserving the write failure is more useful.
        }
    }

    private string GetTorrentPath(InfoHash hash)
    {
        return Path.Combine(GetTorrentsPath(), hash.ToHexStringUpper());
    }

    private string GetTorrentsPath()
    {
        return Path.Combine(_sessionPath, TorrentsFolder);
    }

    private async Task<SavedTorrentEntry?> LoadEntryAsync(string torrentDir, CancellationToken cancellationToken)
    {
        var dirName = Path.GetFileName(torrentDir);

        // Try to parse the directory name as an info hash
        if (!InfoHash.TryFromHex(dirName, out var hash))
        {
            _logger.LogWarning("Invalid torrent directory name: {Name}", dirName);
            return null;
        }

        // Load .torrent file if it exists
        byte[]? torrentFileData = null;
        var torrentFilePath = Path.Combine(torrentDir, TorrentFileName);
        if (File.Exists(torrentFilePath))
        {
            torrentFileData = await File.ReadAllBytesAsync(torrentFilePath, cancellationToken).ConfigureAwait(false);
        }

        // Load magnet link if it exists
        string? magnetLink = null;
        var magnetFilePath = Path.Combine(torrentDir, MagnetFileName);
        if (File.Exists(magnetFilePath))
        {
            magnetLink = await File.ReadAllTextAsync(magnetFilePath, cancellationToken).ConfigureAwait(false);
        }

        // Must have either torrent file or magnet link
        if (torrentFileData == null && string.IsNullOrEmpty(magnetLink))
        {
            _logger.LogWarning("Torrent entry {Hash} has no .torrent file or magnet link", hash);
            return null;
        }

        // Load resume data if it exists
        TorrentResumeData? resumeData = null;
        var resumeFilePath = Path.Combine(torrentDir, ResumeFileName);
        if (File.Exists(resumeFilePath))
        {
            var resumeBytes = await File.ReadAllBytesAsync(resumeFilePath, cancellationToken).ConfigureAwait(false);
            resumeData = new TorrentResumeData
            {
                Data = resumeBytes,
                Hash = hash,
                Timestamp = File.GetLastWriteTimeUtc(resumeFilePath)
            };
        }

        // Load options if they exist
        SavedTorrentOptions? options = null;
        var optionsFilePath = Path.Combine(torrentDir, OptionsFileName);
        if (File.Exists(optionsFilePath))
        {
            try
            {
                var optionsJson = await File.ReadAllTextAsync(optionsFilePath, cancellationToken).ConfigureAwait(false);
                options = JsonSerializer.Deserialize(optionsJson, PeerSharpJsonContext.Default.SavedTorrentOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid options.json for torrent {Hash}", hash);
            }
        }

        return new SavedTorrentEntry(
            hash,
            torrentFileData,
            magnetLink,
            resumeData,
            options);
    }
}
