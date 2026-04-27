using Microsoft.Extensions.Logging;
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

    public Task DeleteAsync(InfoHash hash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var torrentDir = GetTorrentPath(hash);

        if (Directory.Exists(torrentDir))
        {
            try
            {
                Directory.Delete(torrentDir, recursive: true);
                _logger.LogDebug("Deleted torrent entry {Hash}", hash);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete torrent entry {Hash}", hash);
            }
        }

        return Task.CompletedTask;
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
        var entries = new List<SavedTorrentEntry>();
        var torrentsPath = GetTorrentsPath();

        if (!Directory.Exists(torrentsPath))
        {
            return entries;
        }

        foreach (var torrentDir in Directory.GetDirectories(torrentsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var entry = await LoadEntryAsync(torrentDir, cancellationToken).ConfigureAwait(false);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load torrent entry from {Path}", torrentDir);
            }
        }

        _logger.LogInformation("Loaded {Count} torrent entries from session", entries.Count);
        return entries;
    }

    public async Task SaveAllAsync(IEnumerable<SavedTorrentEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(SavedTorrentEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var torrentDir = GetTorrentPath(entry.Hash);

        lock (_lock)
        {
            EnsureDirectoryExists(torrentDir);
        }

        // Save .torrent file
        if (entry.TorrentFileData != null)
        {
            var torrentFilePath = Path.Combine(torrentDir, TorrentFileName);
            await File.WriteAllBytesAsync(torrentFilePath, entry.TorrentFileData, cancellationToken).ConfigureAwait(false);
        }

        // Save magnet link
        if (!string.IsNullOrEmpty(entry.MagnetLink))
        {
            var magnetFilePath = Path.Combine(torrentDir, MagnetFileName);
            await File.WriteAllTextAsync(magnetFilePath, entry.MagnetLink, cancellationToken).ConfigureAwait(false);
        }

        // Save resume data
        if (entry.ResumeData != null)
        {
            var resumeFilePath = Path.Combine(torrentDir, ResumeFileName);
            await File.WriteAllBytesAsync(resumeFilePath, entry.ResumeData.Data, cancellationToken).ConfigureAwait(false);
        }

        // Save options
        if (entry.Options != null)
        {
            var optionsFilePath = Path.Combine(torrentDir, OptionsFileName);
            var optionsJson = JsonSerializer.Serialize(entry.Options, PeerSharpJsonContext.Default.SavedTorrentOptions);
            await File.WriteAllTextAsync(optionsFilePath, optionsJson, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Saved torrent entry {Hash}", entry.Hash);
    }

    public async Task SaveDhtStateAsync(DhtState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var dto = new DhtStateDto
        {
            NodeId = state.NodeId != null ? Convert.ToHexString(state.NodeId) : null,
            Nodes = state.Nodes.Select(n => new DhtNodeDto
            {
                Id = Convert.ToHexString(n.Id),
                Ip = n.EndPoint.Address.ToString(),
                Port = n.EndPoint.Port
            }).ToList()
        };

        var json = JsonSerializer.Serialize(dto, PeerSharpJsonContext.Default.DhtStateDto);
        var path = Path.Combine(_sessionPath, DhtStateFileName);

        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Saved DHT state with {Count} nodes", state.Nodes.Count);
    }

    internal sealed class DhtStateDto
    {
        public string? NodeId { get; set; }
        public List<DhtNodeDto> Nodes { get; set; } = new();
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
