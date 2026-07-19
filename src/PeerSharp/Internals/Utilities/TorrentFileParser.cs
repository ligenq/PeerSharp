using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.BEncoding;
using System.Security.Cryptography;

namespace PeerSharp.Internals.Utilities;

internal static class TorrentFileParser
{
    public static TorrentFileMetadata Parse(byte[] data, ILoggerFactory? loggerFactory = null)
    {
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(nameof(TorrentFileParser));
        if (BencodeParser.Parse(data) is not BDict root)
        {
            throw new FormatException("Invalid torrent file");
        }

        var metadata = new TorrentFileMetadata
        {
            // Parse Announce
            Announce = root.GetString("announce") ?? string.Empty
        };

        var announceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddAnnounce(string? url, List<string>? tier = null)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (announceSet.Add(url))
            {
                metadata.AnnounceList.Add(url);
            }

            tier?.Add(url);
        }

        // Parse Announce-List
        if (root.Get("announce-list") is BList announceList)
        {
            foreach (var tier in announceList.List)
            {
                if (tier is BList tierList)
                {
                    var tierUrls = new List<string>();
                    foreach (var url in tierList.List)
                    {
                        if (url is BString s)
                        {
                            AddAnnounce(s.Text, tierUrls);
                        }
                    }

                    if (tierUrls.Count > 0)
                    {
                        metadata.AnnounceTiers.Add(tierUrls);
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(metadata.Announce) && metadata.AnnounceList.Count > 0)
        {
            metadata.Announce = metadata.AnnounceList[0];
        }
        else if (!string.IsNullOrWhiteSpace(metadata.Announce))
        {
            AddAnnounce(metadata.Announce);
        }

        if (metadata.AnnounceTiers.Count == 0 && !string.IsNullOrWhiteSpace(metadata.Announce))
        {
            metadata.AnnounceTiers.Add([metadata.Announce]);
        }

        // BEP 19: Parse url-list for web seeds
        // url-list can be either a single string or a list of strings
        var urlListNode = root.Get("url-list");
        if (urlListNode is BList urlList)
        {
            foreach (var url in urlList.List)
            {
                if (url is BString s && !string.IsNullOrWhiteSpace(s.Text))
                {
                    metadata.WebSeedUrls.Add(s.Text);
                }
            }
        }
        else if (urlListNode is BString singleUrl && !string.IsNullOrWhiteSpace(singleUrl.Text))
        {
            // Single URL case
            metadata.WebSeedUrls.Add(singleUrl.Text);
        }

        if (root.Get("info") is not BDict info)
        {
            throw new FormatException("Missing info dictionary");
        }

        var infoBytes = BencodeWriter.Write(info);
        ParseInfoDictionary(info, metadata, infoBytes, root, logger);

        return metadata;
    }

    public static TorrentFileMetadata ParseInfoBytes(byte[] infoBytes, ILoggerFactory? loggerFactory = null)
    {
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(nameof(TorrentFileParser));
        if (BencodeParser.Parse(infoBytes) is not BDict info)
        {
            throw new FormatException("Invalid info dictionary");
        }

        var metadata = new TorrentFileMetadata
        {
            InfoBytes = infoBytes
        };

        ParseInfoDictionary(info, metadata, infoBytes, root: null, logger);

        return metadata;
    }

    /// <summary>
    /// BEP 52: Link piece layers from the torrent to their corresponding files.
    /// </summary>
    private static void LinkPieceLayersToFiles(TorrentFileMetadata metadata)
    {
        foreach (var file in metadata.Info.Files)
        {
            if (file.PiecesRoot != null && metadata.PieceLayers.TryGetValue(file.PiecesRoot, out var layerData))
            {
                // Parse layer data into individual 32-byte hashes
                file.PieceLayers = MerkleTree.ParsePieceLayer(layerData);
            }
        }
    }

    private static void ValidateV2Metadata(TorrentFileMetadata metadata, bool requirePieceLayers)
    {
        if (metadata.Info.PieceSize < MerkleTree.BlockSize ||
            metadata.Info.PieceSize % MerkleTree.BlockSize != 0 ||
            !IsPowerOfTwo(metadata.Info.PieceSize / MerkleTree.BlockSize))
        {
            throw new FormatException("Invalid BEP 52 piece length.");
        }

        foreach (var file in metadata.Info.Files)
        {
            if (file.Size < 0)
            {
                throw new FormatException("Invalid BEP 52 file length.");
            }

            if (file.Size == 0)
            {
                continue;
            }

            if (file.PiecesRoot == null)
            {
                throw new FormatException($"BEP 52 non-empty file '{file.Path}' is missing 'pieces root'.");
            }

            if (file.PiecesRoot.Length != MerkleTree.HashSize)
            {
                throw new FormatException($"BEP 52 non-empty file '{file.Path}' has invalid 'pieces root' length: {file.PiecesRoot.Length} (expected {MerkleTree.HashSize}).");
            }

            if (file.Size > metadata.Info.PieceSize)
            {
                if (file.PieceLayers == null || file.PieceLayers.Count != file.PieceCount)
                {
                    if (requirePieceLayers)
                    {
                        throw new FormatException("BEP 52 piece layers are missing or incomplete.");
                    }

                    continue;
                }

                if (!MerkleTree.VerifyPieceLayerAgainstRoot(file.PieceLayers, file.PiecesRoot, metadata.Info.PieceSize, file.Size))
                {
                    throw new FormatException("BEP 52 piece layers do not match pieces root.");
                }
            }
        }
    }

    private static bool IsPowerOfTwo(uint value)
    {
        return value != 0 && (value & (value - 1)) == 0;
    }

    private static long RequireNonNegativeLength(BDict dict, string key, string context)
    {
        long? value = dict.GetLong(key);
        if (!value.HasValue || value.Value < 0)
        {
            throw new FormatException($"Invalid {context} length.");
        }

        return value.Value;
    }

    private static uint RequireValidPieceLength(BDict info)
    {
        long? pieceLength = info.GetLong("piece length");
        if (!pieceLength.HasValue || pieceLength.Value <= 0 || pieceLength.Value > int.MaxValue)
        {
            throw new FormatException("Invalid torrent piece length.");
        }

        return (uint)pieceLength.Value;
    }

    private static void ValidateV1PieceHashes(TorrentFileMetadata metadata, ReadOnlyMemory<byte>? pieces)
    {
        if (metadata.Info.IsMerkle)
        {
            return;
        }

        if (pieces == null)
        {
            throw new FormatException("Missing V1 pieces list.");
        }

        var pMem = pieces.Value;
        if (pMem.Length % 20 != 0)
        {
            throw new FormatException("Invalid V1 pieces list length.");
        }

        int expectedPieceCount = metadata.Info.GetPieceCount();
        int actualPieceCount = pMem.Length / 20;
        if (actualPieceCount != expectedPieceCount)
        {
            throw new FormatException($"Invalid V1 pieces count: expected {expectedPieceCount}, got {actualPieceCount}.");
        }
    }

    /// <summary>
    /// BEP 47: Applies the "attr" flag characters ('p' padding, 'x' executable,
    /// 'h' hidden, 'l' symlink) and the "symlink path" and "sha1" keys of a file
    /// entry. Unknown flag characters are ignored, per the BEP.
    /// </summary>
    private static void ApplyBep47Attributes(BDict fileDict, TorrentFileEntry entry)
    {
        string? attr = fileDict.GetString("attr");
        if (attr != null)
        {
            entry.IsPadding |= attr.Contains('p');
            entry.IsExecutable = attr.Contains('x');
            entry.IsHidden = attr.Contains('h');
            entry.IsSymlink = attr.Contains('l');
        }

        if (fileDict.Get("symlink path") is BList symlinkPath && symlinkPath.List.Count > 0)
        {
            entry.SymlinkTarget = string.Join(
                Path.DirectorySeparatorChar,
                symlinkPath.List.OfType<BString>().Select(s => s.Text));
        }

        var sha1 = fileDict.GetBytes("sha1");
        if (sha1?.Length == 20)
        {
            entry.Sha1 = sha1.Value.ToArray();
        }
    }

    /// <summary>
    /// BEP 52: Parse the file tree structure recursively.
    /// File tree is a nested dictionary where keys are path components.
    /// </summary>
    private static long ParseFileTree(TorrentFileMetadata metadata, BDict tree, string pathPrefix, long currentOffset)
    {
        foreach (var kvp in tree.Dict)
        {
            string name = kvp.Key;
            string path = string.IsNullOrEmpty(pathPrefix) ? name : $"{pathPrefix}{Path.DirectorySeparatorChar}{name}";

            if (kvp.Value is BDict nodeDict)
            {
                // Check if this is a file (has empty string key with length) or directory
                var fileInfoNode = nodeDict.Get("");
                if (fileInfoNode is BDict fileInfo)
                {
                    // This is a file
                    long length = RequireNonNegativeLength(fileInfo, "length", "BEP 52 file");
                    var piecesRootBytes = fileInfo.GetBytes("pieces root");

                    var file = new TorrentFileEntry
                    {
                        Path = path,
                        Size = length,
                        Offset = currentOffset,
                        PiecesRoot = piecesRootBytes?.ToArray(),
                        IsPadding = PaddingFileHelper.IsPaddingPath(path)
                    };
                    ApplyBep47Attributes(fileInfo, file);

                    // BEP 52: Calculate first piece index (files are piece-aligned)
                    if (metadata.Info.PieceSize > 0)
                    {
                        file.FirstPieceIndex = (int)(currentOffset / metadata.Info.PieceSize);
                        file.PieceCount = length > 0 ? (int)((length + metadata.Info.PieceSize - 1) / metadata.Info.PieceSize) : 0;
                    }

                    metadata.Info.Files.Add(file);

                    // BEP 52: In v2, each file is piece-aligned
                    // Offset advances by file size rounded up to piece boundary
                    if (metadata.Info.PieceSize > 0 && length > 0)
                    {
                        currentOffset += (length + metadata.Info.PieceSize - 1) / metadata.Info.PieceSize * metadata.Info.PieceSize;
                    }
                }
                else
                {
                    // This is a directory - recurse
                    currentOffset = ParseFileTree(metadata, nodeDict, path, currentOffset);
                }
            }
        }
        return currentOffset;
    }

    private static void ParseInfoDictionary(BDict info, TorrentFileMetadata metadata, byte[] infoBytes, BDict? root, ILogger logger)
    {
        // BEP 52: Check meta version to determine torrent type
        long? metaVersion = info.GetLong("meta version");
        bool hasV1Pieces = info.Get("pieces") != null;
        bool hasV2FileTree = info.Get("file tree") != null;

        // Determine torrent version
        if (metaVersion.HasValue && metaVersion.Value != 2)
        {
            throw new FormatException($"Unsupported torrent meta version {metaVersion.Value}.");
        }

        if (hasV1Pieces && hasV2FileTree)
        {
            metadata.Info.Version = TorrentVersion.Hybrid;
        }
        else if (hasV2FileTree || metaVersion == 2)
        {
            metadata.Info.Version = TorrentVersion.V2;
        }
        else
        {
            metadata.Info.Version = TorrentVersion.V1;
        }

        metadata.InfoBytes = infoBytes;

        // V1 info hash (SHA-1) - always calculate for v1 and hybrid
        if (metadata.Info.IsV1)
        {
            metadata.Info.Hash = SHA1.HashData(infoBytes);
        }

        // BEP 52: V2 info hash (SHA-256)
        if (metadata.Info.IsV2)
        {
            metadata.Info.HashV2 = SHA256.HashData(infoBytes);
        }

        metadata.Info.Name = info.GetString("name") ?? "Unknown";
        metadata.Info.PieceSize = RequireValidPieceLength(info);

        // BEP 27: Parse the "private" flag from info dictionary
        long? privateFlag = info.GetLong("private");
        metadata.Info.IsPrivate = privateFlag == 1;

        // BEP 30: Check for Merkle root hash (alternative to pieces list)
        var rootHash = info.GetBytes("root hash");
        if (rootHash?.Length == 20)
        {
            metadata.Info.MerkleRootHash = rootHash.Value.ToArray();
            logger.LogInformation("BEP 30: Merkle hash torrent detected, root hash: {RootHash}", Convert.ToHexString(metadata.Info.MerkleRootHash));
        }

        var v1Pieces = metadata.Info.IsV1 && !metadata.Info.IsMerkle
            ? info.GetBytes("pieces")
            : null;

        // BEP 52: Parse piece layers dictionary (outside info dict)
        if (root?.Get("piece layers") is BDict pieceLayers)
        {
            foreach (var kvp in pieceLayers.Dict)
            {
                // In bencode, dictionary keys are strings (which may be binary data)
                // The key is the pieces root hash (32 bytes)
                // The value is the concatenated piece layer hashes
                if (kvp.Value is BString valueStr)
                {
                    // Convert the string key back to bytes
                    var piecesRootKey = System.Text.Encoding.Latin1.GetBytes(kvp.Key);
                    var layerData = valueStr.Value.ToArray();
                    if (piecesRootKey.Length == 32)
                    {
                        metadata.PieceLayers[piecesRootKey] = layerData;
                    }
                }
            }
        }

        // Parse files based on version
        if (metadata.Info.IsV2 && hasV2FileTree && info.Get("file tree") is BDict fileTree)
        {
            long endOffset = ParseFileTree(metadata, fileTree, "", 0);
            metadata.Info.FullSize = endOffset;
        }

        // Only parse V1 file structure if we haven't already populated the files from a V2 file tree.
        // For Hybrid torrents, we prefer the V2 file tree as it contains piece-layer information.
        if (metadata.Info.Files.Count == 0 && (metadata.Info.IsV1 || metadata.Info.Version == TorrentVersion.Hybrid))
        {
            // Parse V1 file structure
            if (info.Get("files") is BList files)
            {
                long offset = 0;
                foreach (var fNode in files.List)
                {
                    if (fNode is BDict fDict)
                    {
                        long len = RequireNonNegativeLength(fDict, "length", "V1 file");
                        string path = fDict.Get("path") is BList pathList
                            ? string.Join(Path.DirectorySeparatorChar, pathList.List.Select(n => n.ToString()))
                            : "Unknown";

                        var entry = new TorrentFileEntry
                        {
                            Path = path,
                            Size = len,
                            Offset = offset,
                            IsPadding = PaddingFileHelper.IsPaddingPath(path)
                        };
                        ApplyBep47Attributes(fDict, entry);
                        metadata.Info.Files.Add(entry);
                        try
                        {
                            checked
                            {
                                offset += len;
                            }
                        }
                        catch (OverflowException)
                        {
                            throw new FormatException("Torrent total size overflow.");
                        }
                    }
                }
                metadata.Info.FullSize = offset;
            }
            else if (metadata.Info.Files.Count == 0)
            {
                // Single file. BEP 47 keys live directly in the info dictionary here.
                long len = RequireNonNegativeLength(info, "length", "V1 file");
                var entry = new TorrentFileEntry
                {
                    Path = metadata.Info.Name,
                    Size = len,
                    Offset = 0,
                    IsPadding = PaddingFileHelper.IsPaddingPath(metadata.Info.Name)
                };
                ApplyBep47Attributes(info, entry);
                metadata.Info.Files.Add(entry);
                metadata.Info.FullSize = len;
            }
        }

        // Parse V1 piece hashes after file sizes are known so the count can be validated.
        if (metadata.Info.IsV1 && !metadata.Info.IsMerkle)
        {
            ValidateV1PieceHashes(metadata, v1Pieces);
            var pMem = v1Pieces!.Value;
            for (int i = 0; i < pMem.Length; i += 20)
            {
                metadata.Info.Pieces.Add(pMem.Slice(i, 20).ToArray());
            }
        }

        // BEP 52: Link piece layers to files
        if (metadata.Info.IsV2)
        {
            LinkPieceLayersToFiles(metadata);
            ValidateV2Metadata(metadata, requirePieceLayers: root != null);
        }
    }
}
