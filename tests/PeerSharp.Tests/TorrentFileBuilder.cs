using System.Security.Cryptography;
using PeerSharp.Internals;
using PeerSharp.BEncoding;

namespace PeerSharp.Tests;

internal class TorrentFileBuilder
{
    private string _name = "test_torrent";
    private uint _pieceLength = 16384;
    private readonly List<(string Path, byte[] Data)> _files = new();

    public TorrentFileBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public TorrentFileBuilder WithPieceLength(uint length)
    {
        _pieceLength = length;
        return this;
    }

    public TorrentFileBuilder AddFile(string path, byte[] data)
    {
        _files.Add((path, data));
        return this;
    }

    public TorrentFile Build()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.Name = _name;
        metadata.Info.PieceSize = _pieceLength;

        long fullSize = 0;
        var info = new BDict();
        info.Dict["name"] = new BString(System.Text.Encoding.UTF8.GetBytes(_name));
        info.Dict["piece length"] = new BNumber(_pieceLength);

        if (_files.Count == 1)
        {
            var file = _files[0];
            info.Dict["length"] = new BNumber(file.Data.Length);
            fullSize = file.Data.Length;
            metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = file.Path, Size = file.Data.Length, Offset = 0 });
        }
        else
        {
            var bFiles = new BList();
            long offset = 0;
            foreach (var file in _files)
            {
                var fDict = new BDict();
                fDict.Dict["length"] = new BNumber(file.Data.Length);
                var pathList = new BList();
                foreach (var part in file.Path.Split('/', '\\'))
                {
                    pathList.List.Add(new BString(System.Text.Encoding.UTF8.GetBytes(part)));
                }
                fDict.Dict["path"] = pathList;
                bFiles.List.Add(fDict);
                metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = file.Path, Size = file.Data.Length, Offset = offset });
                offset += file.Data.Length;
            }
            info.Dict["files"] = bFiles;
            fullSize = offset;
        }

        metadata.Info.FullSize = fullSize;

        // Concatenate all data to calculate pieces
        byte[] allData = new byte[fullSize];
        int currentOffset = 0;
        foreach (var file in _files)
        {
            Buffer.BlockCopy(file.Data, 0, allData, currentOffset, file.Data.Length);
            currentOffset += file.Data.Length;
        }

        int pieceCount = (int)((fullSize + _pieceLength - 1) / _pieceLength);
        byte[] piecesHashes = new byte[pieceCount * 20];
        for (int i = 0; i < pieceCount; i++)
        {
            int start = (int)(i * _pieceLength);
            int end = (int)Math.Min(start + _pieceLength, fullSize);
            byte[] hash = SHA1.HashData(allData.AsSpan(start, end - start));
            Buffer.BlockCopy(hash, 0, piecesHashes, i * 20, 20);
            metadata.Info.Pieces.Add(hash);
        }
        info.Dict["pieces"] = new BString(piecesHashes);

        // Build a proper .torrent file with a root dict containing the info dict
        var root = new BDict();
        root.Dict["info"] = info;
        var rawBytes = BencodeWriter.Write(root);

        return TorrentFile.Parse(rawBytes);
    }
}