using PeerSharp.Internals;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Core;

/// <summary>
/// Represents a parsed and validated .torrent file.
/// Immutable type that ensures the torrent file is valid upon construction.
/// </summary>
public sealed class TorrentFile : IEquatable<TorrentFile>
{
    internal TorrentFile(TorrentFileMetadata metadata)
    {
        Metadata = metadata;
        RawData = ReadOnlyMemory<byte>.Empty;
    }

    private TorrentFile(TorrentFileMetadata metadata, ReadOnlyMemory<byte> rawData)
    {
        Metadata = metadata;
        RawData = rawData;
    }

    /// <summary>
    /// Gets the primary tracker URL.
    /// </summary>
    public string? Announce => string.IsNullOrEmpty(Metadata.Announce) ? null : Metadata.Announce;

    /// <summary>
    /// Gets the number of files in this torrent.
    /// </summary>
    public int FileCount => Metadata.Info.GetVisibleFileCount();

    /// <summary>
    /// Gets the V1 info hash (SHA-1, 20 bytes). May be empty for V2-only torrents.
    /// </summary>
    public InfoHash InfoHash => Metadata.Info.Hash;

    /// <summary>
    /// Gets the V2 info hash (SHA-256, 32 bytes). Empty for V1-only torrents.
    /// </summary>
    public InfoHash InfoHashV2 => Metadata.Info.HashV2;

    /// <summary>
    /// Gets whether this is a hybrid V1+V2 torrent (BEP 52).
    /// </summary>
    public bool IsHybrid => Metadata.Info.Version == TorrentVersion.Hybrid;

    /// <summary>
    /// Gets whether this is a Merkle hash torrent (BEP 30).
    /// </summary>
    public bool IsMerkle => Metadata.Info.IsMerkle;

    /// <summary>
    /// Gets whether this is a private torrent (BEP 27).
    /// </summary>
    public bool IsPrivate => Metadata.Info.IsPrivate;

    /// <summary>
    /// Gets whether this is a V1 torrent.
    /// </summary>
    public bool IsV1 => Metadata.Info.IsV1;

    /// <summary>
    /// Gets whether this is a V2 torrent (BEP 52).
    /// </summary>
    public bool IsV2 => Metadata.Info.IsV2;

    /// <summary>
    /// Gets the torrent name.
    /// </summary>
    public string Name => Metadata.Info.Name;

    /// <summary>
    /// Gets the number of pieces.
    /// </summary>
    public int PieceCount => Metadata.Info.Pieces.Count > 0
        ? Metadata.Info.Pieces.Count
        : Metadata.Info.MerklePieceCount;

    /// <summary>
    /// Gets the piece size in bytes.
    /// </summary>
    public uint PieceSize => Metadata.Info.PieceSize;

    /// <summary>
    /// Gets the raw torrent file bytes.
    /// </summary>
    public ReadOnlyMemory<byte> RawData { get; }

    /// <summary>
    /// Gets the total size of all files in bytes.
    /// </summary>
    public long TotalSize => Metadata.Info.FullSize;

    /// <summary>
    /// Gets the list of tracker URLs.
    /// </summary>
    public IReadOnlyList<string> Trackers => Metadata.AnnounceList;

    /// <summary>
    /// Gets the list of web seed URLs (BEP 19).
    /// </summary>
    public IReadOnlyList<string> WebSeeds => Metadata.WebSeedUrls;

    /// <summary>
    /// Gets the internal metadata. For internal use only.
    /// </summary>
    internal TorrentFileMetadata Metadata { get; }

    /// <summary>
    /// Loads a .torrent file from disk.
    /// </summary>
    /// <param name="path">Path to the .torrent file.</param>
    /// <returns>A parsed TorrentFile instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="FormatException">Thrown when the torrent file is invalid.</exception>
    public static TorrentFile Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Torrent file not found.", path);
        }

        var data = File.ReadAllBytes(path);
        return Parse(data);
    }

    /// <summary>
    /// Loads a .torrent file from disk asynchronously.
    /// </summary>
    /// <param name="path">Path to the .torrent file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A parsed TorrentFile instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="FormatException">Thrown when the torrent file is invalid.</exception>
    public static async Task<TorrentFile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Torrent file not found.", path);
        }

        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(data);
    }

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(TorrentFile? left, TorrentFile? right) => !(left == right);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(TorrentFile? left, TorrentFile? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>
    /// Parses a .torrent file from raw bytes.
    /// </summary>
    /// <param name="data">The raw .torrent file bytes.</param>
    /// <returns>A parsed TorrentFile instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when data is null.</exception>
    /// <exception cref="FormatException">Thrown when the torrent file is invalid.</exception>
    public static TorrentFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
        {
            throw new FormatException("Torrent file data cannot be empty.");
        }

        var metadata = TorrentFileParser.Parse(data);
        return new TorrentFile(metadata, data);
    }

    /// <summary>
    /// Parses a .torrent file from raw bytes.
    /// </summary>
    /// <param name="data">The raw .torrent file bytes.</param>
    /// <returns>A parsed TorrentFile instance.</returns>
    /// <exception cref="ArgumentException">Thrown when data is empty.</exception>
    /// <exception cref="FormatException">Thrown when the torrent file is invalid.</exception>
    public static TorrentFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new FormatException("Torrent file data cannot be empty.");
        }

        return Parse(data.ToArray());
    }

    /// <summary>
    /// Attempts to parse a .torrent file from raw bytes.
    /// </summary>
    /// <param name="data">The raw .torrent file bytes.</param>
    /// <param name="result">The parsed TorrentFile if successful.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryParse(byte[]? data, out TorrentFile? result)
    {
        return TryParse(data, out result, out _);
    }

    /// <summary>
    /// Attempts to parse a .torrent file from raw bytes with error details.
    /// </summary>
    /// <param name="data">The raw .torrent file bytes.</param>
    /// <param name="result">The parsed TorrentFile if successful.</param>
    /// <param name="error">Error message if parsing failed.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryParse(byte[]? data, out TorrentFile? result, out string? error)
    {
        result = null;
        error = null;

        if (data == null || data.Length == 0)
        {
            error = "Torrent file data cannot be null or empty.";
            return false;
        }

        try
        {
            result = Parse(data);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <inheritdoc />
    public bool Equals(TorrentFile? other)
    {
        return other is not null && InfoHash.Equals(other.InfoHash);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as TorrentFile);
    }

    /// <summary>
    /// Gets information about a specific file by index.
    /// </summary>
    /// <param name="fileIndex">Zero-based index of the file.</param>
    /// <returns>File information.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when fileIndex is out of range.</exception>
    public TorrentFileEntry GetFile(int fileIndex)
    {
        int internalIndex = Metadata.Info.MapVisibleIndexToInternal(fileIndex);
        var file = Metadata.Info.Files[internalIndex];
        return CreateFileEntry(file, fileIndex);
    }

    /// <summary>
    /// Gets information about all files in the torrent.
    /// </summary>
    public IReadOnlyList<TorrentFileEntry> GetFiles()
    {
        int visibleIndex = 0;
        var results = new List<TorrentFileEntry>();
        foreach (var internalIndex in Metadata.Info.GetVisibleFileIndices())
        {
            var file = Metadata.Info.Files[internalIndex];
            results.Add(CreateFileEntry(file, visibleIndex));
            visibleIndex++;
        }
        return results;
    }

    private static TorrentFileEntry CreateFileEntry(Internals.TorrentFileEntry file, int visibleIndex)
    {
        var attributes = TorrentFileAttributes.None;
        if (file.IsExecutable)
        {
            attributes |= TorrentFileAttributes.Executable;
        }
        if (file.IsHidden)
        {
            attributes |= TorrentFileAttributes.Hidden;
        }
        if (file.IsSymlink)
        {
            attributes |= TorrentFileAttributes.Symlink;
        }
        if (file.IsPadding)
        {
            attributes |= TorrentFileAttributes.Padding;
        }

        ReadOnlyMemory<byte>? sha1 = file.Sha1 is null ? null : new ReadOnlyMemory<byte>(file.Sha1);
        return new TorrentFileEntry(file.Path, file.Size, visibleIndex, attributes, file.SymlinkTarget, sha1);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return InfoHash.GetHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Name} ({FileCount} files, {TotalSize:N0} bytes)";
    }
}

/// <summary>
/// Represents a file entry within a torrent.
/// </summary>
public readonly struct TorrentFileEntry
{
    internal TorrentFileEntry(string path, long size, int index, TorrentFileAttributes attributes = TorrentFileAttributes.None, string? symlinkTarget = null, ReadOnlyMemory<byte>? sha1 = null)
    {
        Path = path;
        Size = size;
        Index = index;
        Attributes = attributes;
        SymlinkTarget = symlinkTarget;
        Sha1 = sha1;
    }

    /// <summary>
    /// Gets the file index within the torrent.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the file path (relative to torrent root).
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the file size in bytes.
    /// </summary>
    public long Size { get; }

    /// <summary>
    /// Gets the BEP 47 attribute flags of this file.
    /// </summary>
    public TorrentFileAttributes Attributes { get; }

    /// <summary>
    /// Gets whether the file carries the BEP 47 executable attribute.
    /// </summary>
    public bool IsExecutable => (Attributes & TorrentFileAttributes.Executable) != 0;

    /// <summary>
    /// Gets whether the file carries the BEP 47 hidden attribute.
    /// </summary>
    public bool IsHidden => (Attributes & TorrentFileAttributes.Hidden) != 0;

    /// <summary>
    /// Gets whether the entry is a BEP 47 symbolic link.
    /// </summary>
    public bool IsSymlink => (Attributes & TorrentFileAttributes.Symlink) != 0;

    /// <summary>
    /// Gets the symlink target path (relative to the torrent root), if the entry is a symlink.
    /// </summary>
    public string? SymlinkTarget { get; }

    /// <summary>
    /// Gets the BEP 47 per-file SHA-1 digest (20 bytes), if present. This is a
    /// deduplication hint, not an integrity guarantee.
    /// </summary>
    public ReadOnlyMemory<byte>? Sha1 { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Path} ({Size:N0} bytes)";
    }
}

