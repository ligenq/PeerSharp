using PeerSharp.Internals.Utilities;
using PeerSharp.BEncoding;
using System.Security.Cryptography;
using System.Text;

namespace PeerSharp.Core;

/// <summary>
/// Builds a V1 .torrent file from in-memory or on-disk files.
/// </summary>
public sealed class TorrentFileBuilder
{
    private const uint DefaultPieceLength = 256 * 1024;
    private readonly List<List<string>> _announceTiers = [];
    private readonly List<FileSource> _files = [];
    private readonly List<string> _webSeeds = [];
    private string? _announce;
    private bool _isPrivate;
    private string? _name;
    private uint _pieceLength = DefaultPieceLength;
    private bool _usePadding;
    private TorrentFileVersion _version = TorrentFileVersion.V1;
    private bool _useAsyncFileIO = true;

    /// <summary>
    /// Adds an in-memory file to the torrent.
    /// </summary>
    public TorrentFileBuilder AddFile(string torrentPath, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        AddFileInternal(
            torrentPath,
            data.LongLength,
            () => new MemoryStream(data, writable: false),
            () => new MemoryStream(data, writable: false));
        return this;
    }

    /// <summary>
    /// Adds a file from disk to the torrent.
    /// </summary>
    public TorrentFileBuilder AddFileFromPath(string filePath, string? torrentPath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or whitespace.", nameof(filePath));
        }

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("File not found.", filePath);
        }

        string relativePath = string.IsNullOrWhiteSpace(torrentPath) ? fileInfo.Name : torrentPath;
        AddFileInternal(
            relativePath,
            fileInfo.Length,
            () => OpenFileRead(fileInfo.FullName),
            () => OpenFileReadWithAsyncIO(fileInfo.FullName));
        return this;
    }

    /// <summary>
    /// Adds a tracker URL to the primary tier.
    /// </summary>
    public TorrentFileBuilder AddTracker(string trackerUrl)
    {
        if (string.IsNullOrWhiteSpace(trackerUrl))
        {
            throw new ArgumentException("Tracker URL cannot be null or whitespace.", nameof(trackerUrl));
        }

        if (_announceTiers.Count == 0)
        {
            _announceTiers.Add([]);
        }

        _announceTiers[0].Add(trackerUrl);
        return this;
    }

    /// <summary>
    /// Adds a tracker tier (list of URLs) to the announce list.
    /// </summary>
    public TorrentFileBuilder AddTrackerTier(IEnumerable<string> trackerUrls)
    {
        ArgumentNullException.ThrowIfNull(trackerUrls);

        var tier = trackerUrls.Where(url => !string.IsNullOrWhiteSpace(url)).ToList();
        if (tier.Count == 0)
        {
            throw new ArgumentException("Tracker tier must contain at least one URL.", nameof(trackerUrls));
        }

        _announceTiers.Add(tier);
        return this;
    }

    /// <summary>
    /// Adds a web seed URL (BEP 19).
    /// </summary>
    public TorrentFileBuilder AddWebSeed(string webSeedUrl)
    {
        if (string.IsNullOrWhiteSpace(webSeedUrl))
        {
            throw new ArgumentException("Web seed URL cannot be null or whitespace.", nameof(webSeedUrl));
        }

        _webSeeds.Add(webSeedUrl);
        return this;
    }

    /// <summary>
    /// Builds the torrent file synchronously.
    /// </summary>
    public TorrentFile Build()
    {
        var data = BuildRawTorrentBytes();
        return TorrentFile.Parse(data);
    }

    /// <summary>
    /// Builds the torrent file asynchronously.
    /// </summary>
    public async Task<TorrentFile> BuildAsync(CancellationToken cancellationToken = default)
    {
        var data = await BuildRawTorrentBytesAsync(cancellationToken).ConfigureAwait(false);
        return TorrentFile.Parse(data);
    }

    /// <summary>
    /// Sets the primary announce URL.
    /// </summary>
    public TorrentFileBuilder WithAnnounce(string announceUrl)
    {
        if (string.IsNullOrWhiteSpace(announceUrl))
        {
            throw new ArgumentException("Announce URL cannot be null or whitespace.", nameof(announceUrl));
        }

        _announce = announceUrl;
        return this;
    }

    /// <summary>
    /// Sets the name used in the torrent metadata.
    /// </summary>
    public TorrentFileBuilder WithName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name cannot be null or whitespace.", nameof(name));
        }

        _name = name;
        return this;
    }

    /// <summary>
    /// Sets the piece length in bytes.
    /// </summary>
    public TorrentFileBuilder WithPieceLength(uint length)
    {
        if (length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Piece length must be greater than zero.");
        }

        _pieceLength = length;
        return this;
    }

    /// <summary>
    /// Marks the torrent as private (BEP 17).
    /// </summary>
    public TorrentFileBuilder WithPrivate(bool isPrivate = true)
    {
        _isPrivate = isPrivate;
        return this;
    }

    /// <summary>
    /// Sets the torrent metadata version to emit.
    /// </summary>
    public TorrentFileBuilder WithVersion(TorrentFileVersion version)
    {
        _version = version;
        return this;
    }

    /// <summary>
    /// Enables BEP 47 padding files for V1 torrents.
    /// </summary>
    public TorrentFileBuilder WithPaddingFiles(bool enabled = true)
    {
        _usePadding = enabled;
        return this;
    }

    /// <summary>
    /// Controls whether async build operations open files with async I/O.
    /// Defaults to true.
    /// </summary>
    public TorrentFileBuilder WithAsyncFileIO(bool enabled = true)
    {
        _useAsyncFileIO = enabled;
        return this;
    }

    private static BDict BuildFileTree(IReadOnlyList<FileMerkleInfo> merkleFiles)
    {
        var root = new BDict();

        foreach (var file in merkleFiles)
        {
            var parts = SplitPath(file.Path);
            if (parts.Length == 0)
            {
                throw new InvalidOperationException("File path must contain at least one segment.");
            }

            var current = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string part = parts[i];
                if (!current.Dict.TryGetValue(part, out var node) || node is not BDict next)
                {
                    next = new BDict();
                    current.Dict[part] = next;
                }
                current = next;
            }

            string fileName = parts[^1];
            var fileNode = new BDict();
            var fileInfo = new BDict();
            fileInfo.Dict["length"] = new BNumber(file.Length);
            fileInfo.Dict["pieces root"] = new BString(file.PiecesRoot);
            fileNode.Dict[string.Empty] = fileInfo;
            current.Dict[fileName] = fileNode;
        }

        return root;
    }

    private BDict? BuildPieceLayersDictionary(IReadOnlyList<FileMerkleInfo> merkleFiles)
    {
        var dict = new BDict();
        foreach (var file in merkleFiles)
        {
            if (file.Length <= _pieceLength || file.PieceLayer.Count == 0)
            {
                continue;
            }

            byte[] layerData = new byte[file.PieceLayer.Count * MerkleTree.HashSize];
            for (int i = 0; i < file.PieceLayer.Count; i++)
            {
                Buffer.BlockCopy(file.PieceLayer[i], 0, layerData, i * MerkleTree.HashSize, MerkleTree.HashSize);
            }

            string key = Encoding.Latin1.GetString(file.PiecesRoot);
            dict.Dict[key] = new BString(layerData);
        }

        return dict.Dict.Count == 0 ? null : dict;
    }

    private static byte[] HashMerkleBlock(byte[] buffer, int length)
    {
        if (length < MerkleTree.BlockSize)
        {
            // BEP 52: "If the file size is not a multiple of 16KiB, the last leaf is the SHA-256 hash of the remaining data, zero-padded to 16KiB."
            Array.Clear(buffer, length, MerkleTree.BlockSize - length);
            return MerkleTree.HashBlock(buffer);
        }
        return MerkleTree.HashBlock(buffer.AsSpan(0, length));
    }

    private static bool IsPowerOfTwo(uint value)
    {
        return value != 0 && (value & (value - 1)) == 0;
    }

    private static string[] SplitPath(string path)
    {
        return path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static void ValidateTorrentPath(string torrentPath)
    {
        if (string.IsNullOrWhiteSpace(torrentPath))
        {
            throw new ArgumentException("Torrent path cannot be null or whitespace.", nameof(torrentPath));
        }

        if (Path.IsPathRooted(torrentPath))
        {
            throw new ArgumentException("Torrent paths must be relative.", nameof(torrentPath));
        }

        var parts = SplitPath(torrentPath);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Torrent path must contain at least one file name.", nameof(torrentPath));
        }

        foreach (string part in parts)
        {
            if (part == "." || part == "..")
            {
                throw new ArgumentException("Torrent paths must not contain current or parent directory segments.", nameof(torrentPath));
            }
        }
    }

    private void AddFileInternal(string torrentPath, long length, Func<Stream> openRead, Func<Stream> openReadAsync)
    {
        ValidateTorrentPath(torrentPath);

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "File length cannot be negative.");
        }

        _files.Add(new FileSource(torrentPath, length, openRead, openReadAsync));
    }

    private List<FileSource> GetV1FilesWithPadding()
    {
        bool shouldPad = _usePadding || _version == TorrentFileVersion.Hybrid;
        if (!shouldPad || _files.Count <= 1)
        {
            return _files;
        }

        var result = new List<FileSource>();
        long offset = 0;
        int padIndex = 0;

        for (int i = 0; i < _files.Count; i++)
        {
            var file = _files[i];
            result.Add(file);
            offset += file.Length;

            if (i == _files.Count - 1)
            {
                continue;
            }

            long remainder = offset % _pieceLength;
            if (remainder == 0)
            {
                continue;
            }

            long padLength = _pieceLength - remainder;
            if (padLength <= 0)
            {
                continue;
            }

            string padPath = PaddingFileHelper.BuildPaddingPath(padLength, padIndex++);
            result.Add(new FileSource(
                padPath,
                padLength,
                () => new ZeroStream(padLength),
                () => new ZeroStream(padLength)));
            offset += padLength;
        }

        return result;
    }

    private void ApplyHybridV1Fields(BDict info)
    {
        if (_files.Count == 1)
        {
            var file = _files[0];
            info.Dict["length"] = new BNumber(file.Length);
        }
        else
        {
            info.Dict["files"] = BuildV1FilesList();
        }
    }

    private byte[] BuildHybridTorrentBytes()
    {
        var metadata = BuildV2Metadata();
        ApplyHybridV1Fields(metadata.Info);
        metadata.Info.Dict["pieces"] = new BString(BuildPieceHashes());
        return BuildRootDictionary(metadata.Info, metadata.PieceLayers);
    }

    private async Task<byte[]> BuildHybridTorrentBytesAsync(CancellationToken cancellationToken)
    {
        var metadata = await BuildV2MetadataAsync(cancellationToken).ConfigureAwait(false);
        ApplyHybridV1Fields(metadata.Info);
        metadata.Info.Dict["pieces"] = new BString(await BuildPieceHashesAsync(cancellationToken).ConfigureAwait(false));
        return BuildRootDictionary(metadata.Info, metadata.PieceLayers);
    }

    private List<FileMerkleInfo> BuildMerkleFiles()
    {
        var results = new List<FileMerkleInfo>(_files.Count);
        foreach (var file in _files)
        {
            using var stream = file.OpenRead();
            results.Add(BuildMerkleInfo(file, stream));
        }
        return results;
    }

    private async Task<List<FileMerkleInfo>> BuildMerkleFilesAsync(CancellationToken cancellationToken)
    {
        var results = new List<FileMerkleInfo>(_files.Count);
        foreach (var file in _files)
        {
            await using var stream = file.OpenReadWithAsyncIO();
            results.Add(await BuildMerkleInfoAsync(file, stream, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    private FileMerkleInfo BuildMerkleInfo(FileSource file, Stream stream)
    {
        var leaves = new List<byte[]>();
        var buffer = new byte[MerkleTree.BlockSize];
        int bufferFill = 0;
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, bufferFill, buffer.Length - bufferFill)) > 0)
        {
            bufferFill += bytesRead;
            if (bufferFill == buffer.Length)
            {
                leaves.Add(MerkleTree.HashBlock(buffer));
                bufferFill = 0;
            }
        }

        if (bufferFill > 0)
        {
            leaves.Add(HashMerkleBlock(buffer, bufferFill));
        }

        var piecesRoot = MerkleTree.ComputeRoot(leaves);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, _pieceLength);
        return new FileMerkleInfo(file.Path, file.Length, piecesRoot, pieceLayer);
    }

    private async Task<FileMerkleInfo> BuildMerkleInfoAsync(FileSource file, Stream stream, CancellationToken cancellationToken)
    {
        var leaves = new List<byte[]>();
        var buffer = new byte[MerkleTree.BlockSize];
        int bufferFill = 0;
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(bufferFill, buffer.Length - bufferFill), cancellationToken).ConfigureAwait(false)) > 0)
        {
            bufferFill += bytesRead;
            if (bufferFill == buffer.Length)
            {
                leaves.Add(MerkleTree.HashBlock(buffer));
                bufferFill = 0;
            }
        }

        if (bufferFill > 0)
        {
            leaves.Add(HashMerkleBlock(buffer, bufferFill));
        }

        var piecesRoot = MerkleTree.ComputeRoot(leaves);
        var pieceLayer = MerkleTree.GetPieceLayer(leaves, _pieceLength);
        return new FileMerkleInfo(file.Path, file.Length, piecesRoot, pieceLayer);
    }

    private byte[] BuildPieceHashes()
    {
        int pieceLength = (int)_pieceLength;
        var pieceBuffer = new byte[pieceLength];
        int bufferFill = 0;
        using var piecesStream = new MemoryStream();

        foreach (var file in GetV1FilesWithPadding())
        {
            using var stream = file.OpenRead();
            int bytesRead;
            while ((bytesRead = stream.Read(pieceBuffer, bufferFill, pieceLength - bufferFill)) > 0)
            {
                bufferFill += bytesRead;
                if (bufferFill == pieceLength)
                {
                    byte[] hash = SHA1.HashData(pieceBuffer.AsSpan(0, bufferFill));
                    piecesStream.Write(hash);
                    bufferFill = 0;
                }
            }
        }

        if (bufferFill > 0)
        {
            byte[] hash = SHA1.HashData(pieceBuffer.AsSpan(0, bufferFill));
            piecesStream.Write(hash);
        }

        return piecesStream.ToArray();
    }

    private async Task<byte[]> BuildPieceHashesAsync(CancellationToken cancellationToken)
    {
        int pieceLength = (int)_pieceLength;
        var pieceBuffer = new byte[pieceLength];
        int bufferFill = 0;
        await using var piecesStream = new MemoryStream();

        foreach (var file in GetV1FilesWithPadding())
        {
            await using var stream = file.OpenReadWithAsyncIO();
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(pieceBuffer.AsMemory(bufferFill, pieceLength - bufferFill), cancellationToken).ConfigureAwait(false)) > 0)
            {
                bufferFill += bytesRead;
                if (bufferFill == pieceLength)
                {
                    byte[] hash = SHA1.HashData(pieceBuffer.AsSpan(0, bufferFill));
                    await piecesStream.WriteAsync(hash, cancellationToken).ConfigureAwait(false);
                    bufferFill = 0;
                }
            }
        }

        if (bufferFill > 0)
        {
            byte[] hash = SHA1.HashData(pieceBuffer.AsSpan(0, bufferFill));
            await piecesStream.WriteAsync(hash, cancellationToken).ConfigureAwait(false);
        }

        return piecesStream.ToArray();
    }

    private byte[] BuildRawTorrentBytes()
    {
        ValidateInputs();
        return _version switch
        {
            TorrentFileVersion.V1 => BuildV1TorrentBytes(),
            TorrentFileVersion.V2 => BuildV2TorrentBytes(),
            TorrentFileVersion.Hybrid => BuildHybridTorrentBytes(),
            _ => throw new InvalidOperationException($"Unsupported torrent version {_version}.")
        };
    }

    private async Task<byte[]> BuildRawTorrentBytesAsync(CancellationToken cancellationToken)
    {
        ValidateInputs();
        return _version switch
        {
            TorrentFileVersion.V1 => await BuildV1TorrentBytesAsync(cancellationToken).ConfigureAwait(false),
            TorrentFileVersion.V2 => await BuildV2TorrentBytesAsync(cancellationToken).ConfigureAwait(false),
            TorrentFileVersion.Hybrid => await BuildHybridTorrentBytesAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported torrent version {_version}.")
        };
    }

    private byte[] BuildRootDictionary(BDict info, BDict? pieceLayers)
    {
        var root = new BDict();

        string? announce = _announce;
        var announceTiers = _announceTiers.ConvertAll(tier => new List<string>(tier));
        if (string.IsNullOrWhiteSpace(announce) && announceTiers.Count > 0 && announceTiers[0].Count > 0)
        {
            announce = announceTiers[0][0];
        }

        if (!string.IsNullOrWhiteSpace(announce))
        {
            if (announceTiers.Count == 0)
            {
                announceTiers.Add([announce]);
            }
            else if (!announceTiers[0].Any(url => string.Equals(url, announce, StringComparison.Ordinal)))
            {
                announceTiers[0].Insert(0, announce);
            }
        }

        if (!string.IsNullOrWhiteSpace(announce))
        {
            root.Dict["announce"] = new BString(Encoding.UTF8.GetBytes(announce));
        }

        if (announceTiers.Count > 0)
        {
            var announceList = new BList();
            foreach (var tier in announceTiers)
            {
                var tierList = new BList();
                foreach (var url in tier)
                {
                    tierList.List.Add(new BString(Encoding.UTF8.GetBytes(url)));
                }
                announceList.List.Add(tierList);
            }
            root.Dict["announce-list"] = announceList;
        }

        if (_webSeeds.Count == 1)
        {
            root.Dict["url-list"] = new BString(Encoding.UTF8.GetBytes(_webSeeds[0]));
        }
        else if (_webSeeds.Count > 1)
        {
            var urlList = new BList();
            foreach (var url in _webSeeds)
            {
                urlList.List.Add(new BString(Encoding.UTF8.GetBytes(url)));
            }
            root.Dict["url-list"] = urlList;
        }

        if (pieceLayers != null)
        {
            root.Dict["piece layers"] = pieceLayers;
        }

        root.Dict["info"] = info;

        return BencodeWriter.Write(root);
    }

    private BList BuildV1FilesList()
    {
        var bFiles = new BList();
        foreach (var file in GetV1FilesWithPadding())
        {
            var fDict = new BDict();
            fDict.Dict["length"] = new BNumber(file.Length);
            var pathList = new BList();
            foreach (var part in SplitPath(file.Path))
            {
                pathList.List.Add(new BString(Encoding.UTF8.GetBytes(part)));
            }
            fDict.Dict["path"] = pathList;
            bFiles.List.Add(fDict);
        }
        return bFiles;
    }

    private BDict BuildV1InfoDictionary()
    {
        string name = _name ?? InferName();

        var info = new BDict();
        info.Dict["name"] = new BString(Encoding.UTF8.GetBytes(name));
        info.Dict["piece length"] = new BNumber(_pieceLength);

        if (_isPrivate)
        {
            info.Dict["private"] = new BNumber(1);
        }

        if (_files.Count == 1)
        {
            var file = _files[0];
            info.Dict["length"] = new BNumber(file.Length);
        }
        else
        {
            info.Dict["files"] = BuildV1FilesList();
        }

        return info;
    }

    private byte[] BuildV1TorrentBytes()
    {
        var info = BuildV1InfoDictionary();
        info.Dict["pieces"] = new BString(BuildPieceHashes());
        return BuildRootDictionary(info, pieceLayers: null);
    }

    private async Task<byte[]> BuildV1TorrentBytesAsync(CancellationToken cancellationToken)
    {
        var info = BuildV1InfoDictionary();
        info.Dict["pieces"] = new BString(await BuildPieceHashesAsync(cancellationToken).ConfigureAwait(false));
        return BuildRootDictionary(info, pieceLayers: null);
    }

    private BDict BuildV2InfoDictionary(IReadOnlyList<FileMerkleInfo> merkleFiles)
    {
        string name = _name ?? InferName();

        var info = new BDict();
        info.Dict["name"] = new BString(Encoding.UTF8.GetBytes(name));
        info.Dict["piece length"] = new BNumber(_pieceLength);
        info.Dict["meta version"] = new BNumber(2);
        info.Dict["file tree"] = BuildFileTree(merkleFiles);

        if (_isPrivate)
        {
            info.Dict["private"] = new BNumber(1);
        }

        return info;
    }

    private V2Metadata BuildV2Metadata()
    {
        var merkleFiles = BuildMerkleFiles();
        var info = BuildV2InfoDictionary(merkleFiles);
        var pieceLayers = BuildPieceLayersDictionary(merkleFiles);
        return new V2Metadata(info, pieceLayers);
    }

    private async Task<V2Metadata> BuildV2MetadataAsync(CancellationToken cancellationToken)
    {
        var merkleFiles = await BuildMerkleFilesAsync(cancellationToken).ConfigureAwait(false);
        var info = BuildV2InfoDictionary(merkleFiles);
        var pieceLayers = BuildPieceLayersDictionary(merkleFiles);
        return new V2Metadata(info, pieceLayers);
    }

    private byte[] BuildV2TorrentBytes()
    {
        var metadata = BuildV2Metadata();
        return BuildRootDictionary(metadata.Info, metadata.PieceLayers);
    }

    private async Task<byte[]> BuildV2TorrentBytesAsync(CancellationToken cancellationToken)
    {
        var metadata = await BuildV2MetadataAsync(cancellationToken).ConfigureAwait(false);
        return BuildRootDictionary(metadata.Info, metadata.PieceLayers);
    }

    private string InferName()
    {
        if (_files.Count == 1)
        {
            return Path.GetFileName(_files[0].Path);
        }

        string? commonRoot = null;
        foreach (var file in _files)
        {
            var parts = SplitPath(file.Path);
            if (parts.Length == 0)
            {
                continue;
            }

            if (commonRoot == null)
            {
                commonRoot = parts[0];
                continue;
            }

            if (!string.Equals(commonRoot, parts[0], StringComparison.Ordinal))
            {
                commonRoot = null;
                break;
            }
        }

        return string.IsNullOrWhiteSpace(commonRoot) ? "torrent" : commonRoot;
    }

    private void ValidateInputs()
    {
        if (_files.Count == 0)
        {
            throw new InvalidOperationException("At least one file is required to build a torrent.");
        }

        if (_pieceLength == 0)
        {
            throw new InvalidOperationException("Piece length must be greater than zero.");
        }

        if (_pieceLength > int.MaxValue)
        {
            throw new InvalidOperationException("Piece length must fit within a 32-bit signed integer.");
        }

        if (_usePadding && _version == TorrentFileVersion.V2)
        {
            throw new InvalidOperationException("Padding files are only supported for V1 and hybrid torrents.");
        }

        if (_version != TorrentFileVersion.V1)
        {
            if (_pieceLength < MerkleTree.BlockSize)
            {
                throw new InvalidOperationException($"V2 piece length must be at least {MerkleTree.BlockSize} bytes.");
            }

            if (_pieceLength % MerkleTree.BlockSize != 0)
            {
                throw new InvalidOperationException("V2 piece length must be a multiple of 16 KiB.");
            }

            uint blocksPerPiece = _pieceLength / MerkleTree.BlockSize;
            if (!IsPowerOfTwo(blocksPerPiece))
            {
                throw new InvalidOperationException("V2 piece length must be a power-of-two multiple of 16 KiB.");
            }
        }
    }

    private static Stream OpenFileRead(string fullPath)
    {
        return new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
    }

    private Stream OpenFileReadWithAsyncIO(string fullPath)
    {
        if (!_useAsyncFileIO)
        {
            return OpenFileRead(fullPath);
        }

        return new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        private AtomicDisposal _disposal = new();

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= length)
            {
                return 0;
            }

            int toRead = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (target < 0 || target > length)
            {
                throw new IOException("Attempted to seek outside the stream bounds.");
            }

            _position = target;
            return _position;
        }

        public override void Flush()
        {
        }

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            _disposal.MarkDisposed();
            base.Dispose(disposing);
        }
    }

    private sealed record FileSource(string Path, long Length, Func<Stream> OpenReadFactory, Func<Stream> OpenReadWithAsyncIOFactory)
    {
        public Stream OpenRead() => OpenReadFactory();
        public Stream OpenReadWithAsyncIO() => OpenReadWithAsyncIOFactory();
    }
    private sealed record FileMerkleInfo(string Path, long Length, byte[] PiecesRoot, List<byte[]> PieceLayer);
    private sealed record V2Metadata(BDict Info, BDict? PieceLayers);
}
