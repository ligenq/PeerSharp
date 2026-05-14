namespace PeerSharp.Tests.Core;

public class TorrentFileMetadataTests
{
    [Fact]
    public void GetPieceRangeForFile_CorrectIndices()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 100;
        info.FullSize = 300;
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 150 }); // Piece 0, 1
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 150, Size = 150 }); // Piece 1, 2
        info.Pieces.Add(new byte[20]);
        info.Pieces.Add(new byte[20]);
        info.Pieces.Add(new byte[20]);

        var range0 = info.GetPieceRangeForFile(0);
        Assert.Equal(0, range0.firstPiece);
        Assert.Equal(1, range0.lastPiece);

        var range1 = info.GetPieceRangeForFile(1);
        Assert.Equal(1, range1.firstPiece);
        Assert.Equal(2, range1.lastPiece);
    }

    [Fact]
    public void GetFilesForPiece_CorrectFiles()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 100;
        info.FullSize = 300;
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 150 });
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 150, Size = 150 });
        info.Pieces.Add(new byte[20]);
        info.Pieces.Add(new byte[20]);
        info.Pieces.Add(new byte[20]);

        var files0 = info.GetFilesForPiece(0);
        Assert.Single(files0);
        Assert.Equal(0, files0[0]);

        var files1 = info.GetFilesForPiece(1);
        Assert.Equal(2, files1.Count);
        Assert.Equal(0, files1[0]);
        Assert.Equal(1, files1[1]);

        var files2 = info.GetFilesForPiece(2);
        Assert.Single(files2);
        Assert.Equal(1, files2[0]);
    }

    [Fact]
    public void GetPieceRangeForFile_UsesDerivedPieceCount_WhenPiecesMissing()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 100;
        info.FullSize = 300;
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 60 });
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 100, Size = 120 });

        var range0 = info.GetPieceRangeForFile(0);
        Assert.Equal(0, range0.firstPiece);
        Assert.Equal(0, range0.lastPiece);

        var range1 = info.GetPieceRangeForFile(1);
        Assert.Equal(1, range1.firstPiece);
        Assert.Equal(2, range1.lastPiece);
    }

    [Fact]
    public void GetFilesForPiece_UsesDerivedPieceCount_WhenPiecesMissing()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 100;
        info.FullSize = 300;
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 60 });
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 100, Size = 120 });

        var files0 = info.GetFilesForPiece(0);
        Assert.Single(files0);
        Assert.Equal(0, files0[0]);

        var files1 = info.GetFilesForPiece(1);
        Assert.Single(files1);
        Assert.Equal(1, files1[0]);

        var files2 = info.GetFilesForPiece(2);
        Assert.Single(files2);
        Assert.Equal(1, files2[0]);
    }

    [Fact]
    public void IsPieceNeeded_RespectsSelection()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 100;
        info.FullSize = 200;
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 100 });
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 100, Size = 100 });
        info.Pieces.Add(new byte[20]);
        info.Pieces.Add(new byte[20]);

        var selection = new List<FileSelection>
        {
            new() { Selected = true },
            new() { Selected = false, Priority = Priority.DoNotDownload }
        };

        Assert.True(info.IsPieceNeeded(0, selection));
        Assert.False(info.IsPieceNeeded(1, selection));
    }

    [Fact]
    public void MerklePieceCount_CalculatesCorrectly()
    {
        var info = new Internals.TorrentFileInfo();
        info.PieceSize = 16384;
        info.FullSize = (16384 * 10) + 1; // 11 pieces

        Assert.Equal(11, info.MerklePieceCount);
    }

    [Fact]
    public void GetPieceSize_V2UsesFileLocalLastPiece()
    {
        var info = new Internals.TorrentFileInfo
        {
            PieceSize = 100,
            FullSize = 300,
            Version = Internals.TorrentVersion.V2
        };
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 0, Size = 150, FirstPieceIndex = 0, PieceCount = 2, PiecesRoot = new byte[32] });
        info.Files.Add(new Internals.TorrentFileEntry { Offset = 200, Size = 80, FirstPieceIndex = 2, PieceCount = 1, PiecesRoot = new byte[32] });

        Assert.Equal(100, info.GetPieceSize(0));
        Assert.Equal(50, info.GetPieceSize(1));
        Assert.Equal(80, info.GetPieceSize(2));
    }

    [Fact]
    public void GetTrackerInfoHash_V2UsesTruncatedV2Hash()
    {
        byte[] hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var info = new Internals.TorrentFileInfo
        {
            Version = Internals.TorrentVersion.V2,
            HashV2 = new InfoHash(hash)
        };

        Assert.Equal(hash.Take(20).ToArray(), info.GetTrackerInfoHash().ToArray());
    }

    [Fact]
    public void TryAddV2Hashes_FullPieceLayer_VerifiesAgainstRoot()
    {
        byte[] fileData = new byte[32 * 1024];
        Random.Shared.NextBytes(fileData);
        var leaves = Internals.Utilities.MerkleTree.ComputeLeaves(fileData);
        var piecesRoot = Internals.Utilities.MerkleTree.ComputeRoot(leaves);
        var pieceLayer = Internals.Utilities.MerkleTree.GetPieceLayer(leaves, 16_384);
        byte[] layerData = pieceLayer.SelectMany(hash => hash).ToArray();

        var info = new Internals.TorrentFileInfo
        {
            PieceSize = 16_384,
            FullSize = fileData.Length,
            Version = Internals.TorrentVersion.V2
        };
        info.Files.Add(new Internals.TorrentFileEntry
        {
            Offset = 0,
            Size = fileData.Length,
            FirstPieceIndex = 0,
            PieceCount = 2,
            PiecesRoot = piecesRoot
        });

        bool added = info.TryAddV2Hashes(
            piecesRoot,
            Internals.Utilities.MerkleTree.GetPieceLayerDepth(info.PieceSize),
            0,
            2,
            0,
            layerData);

        Assert.True(added);
        Assert.NotNull(info.GetV2ExpectedPieceHash(1));
        Assert.Equal(pieceLayer[1], info.GetV2ExpectedPieceHash(1));
    }

    [Fact]
    public void TryAddV2Hashes_RejectsInvalidPieceLayer()
    {
        byte[] fileData = new byte[32 * 1024];
        Random.Shared.NextBytes(fileData);
        var leaves = Internals.Utilities.MerkleTree.ComputeLeaves(fileData);
        var piecesRoot = Internals.Utilities.MerkleTree.ComputeRoot(leaves);
        byte[] badLayerData = new byte[64];

        var info = new Internals.TorrentFileInfo
        {
            PieceSize = 16_384,
            FullSize = fileData.Length,
            Version = Internals.TorrentVersion.V2
        };
        info.Files.Add(new Internals.TorrentFileEntry
        {
            Offset = 0,
            Size = fileData.Length,
            FirstPieceIndex = 0,
            PieceCount = 2,
            PiecesRoot = piecesRoot
        });

        bool added = info.TryAddV2Hashes(
            piecesRoot,
            Internals.Utilities.MerkleTree.GetPieceLayerDepth(info.PieceSize),
            0,
            2,
            0,
            badLayerData);

        Assert.False(added);
        Assert.Null(info.GetV2ExpectedPieceHash(1));
    }

    [Fact]
    public void V2Hashes_PartialPieceLayerWithProof_VerifiesAndCaches()
    {
        byte[] fileData = new byte[4 * 16_384];
        Random.Shared.NextBytes(fileData);
        var leaves = Internals.Utilities.MerkleTree.ComputeLeaves(fileData);
        var piecesRoot = Internals.Utilities.MerkleTree.ComputeRoot(leaves);
        var pieceLayer = Internals.Utilities.MerkleTree.GetPieceLayer(leaves, 16_384);

        var source = CreateV2Info(fileData.Length, piecesRoot);
        source.Files[0].PieceLayers = pieceLayer;

        var request = new Internals.V2HashRequest(
            piecesRoot,
            Internals.Utilities.MerkleTree.GetPieceLayerDepth(source.PieceSize),
            0,
            2,
            Internals.Utilities.MerkleTree.GetTotalLevels(fileData.Length) - Internals.Utilities.MerkleTree.GetPieceLayerDepth(source.PieceSize) - 1);

        byte[]? hashes = source.GetV2Hashes(
            request.PiecesRoot,
            request.BaseLayer,
            request.Index,
            request.Length,
            request.ProofLayers);

        Assert.NotNull(hashes);
        Assert.True(hashes!.Length > request.Length * 32);

        var target = CreateV2Info(fileData.Length, piecesRoot);
        bool added = target.TryAddV2Hashes(
            request.PiecesRoot,
            request.BaseLayer,
            request.Index,
            request.Length,
            request.ProofLayers,
            hashes);

        Assert.True(added);
        Assert.Equal(pieceLayer[1], target.GetV2ExpectedPieceHash(1));
    }

    [Fact]
    public void GetV2HashRequestForPiece_UsesBoundedChunks()
    {
        // libtorrent uses min(512, NextPowerOf2(remaining)) so the chunk forms a balanced
        // sub-tree the receiver can verify. For 600 pieces, the second chunk covers indices
        // 512..639 — i.e. 88 real pieces padded out to 128 with merkle pad hashes.
        int pieceCount = 600;
        var info = CreateV2Info(pieceCount * 16_384, new byte[32], pieceCount);

        var request = info.GetV2HashRequestForPiece(550);

        Assert.NotNull(request);
        Assert.Equal(512, request!.Index);
        Assert.Equal(128, request.Length);
        Assert.True(request.ProofLayers > 0);
    }

    [Fact]
    public void TryAddV2Hashes_LibtorrentStylePaddedFinalChunk_VerifiesAndCachesRealHashesOnly()
    {
        // Multi-chunk file (>512 pieces) where the final chunk uses NextPow2(remaining) padding.
        // pieceSize=16KB makes the math reproducible: pieceCount = blockCount.
        const int pieceCount = 600;
        long fileSize = pieceCount * 16_384L;
        byte[] fileData = new byte[fileSize];
        Random.Shared.NextBytes(fileData);

        var leaves = Internals.Utilities.MerkleTree.ComputeLeaves(fileData);
        var piecesRoot = Internals.Utilities.MerkleTree.ComputeRoot(leaves);
        var pieceLayer = Internals.Utilities.MerkleTree.GetPieceLayer(leaves, 16_384);
        Assert.Equal(pieceCount, pieceLayer.Count);

        var source = CreateV2Info((int)fileSize, piecesRoot, pieceCount);
        source.Files[0].PieceLayers = pieceLayer;

        int baseLayer = Internals.Utilities.MerkleTree.GetPieceLayerDepth(source.PieceSize);
        int proofLayers = Internals.Utilities.MerkleTree.GetTotalLevels(fileSize) - baseLayer - 1;

        // Final chunk: index=512, length=NextPow2(88)=128 (88 real + 40 pad entries).
        byte[]? hashes = source.GetV2Hashes(piecesRoot, baseLayer, 512, 128, proofLayers);
        Assert.NotNull(hashes);

        var target = CreateV2Info((int)fileSize, piecesRoot, pieceCount);
        bool added = target.TryAddV2Hashes(piecesRoot, baseLayer, 512, 128, proofLayers, hashes!);

        Assert.True(added);
        // Only real piece hashes should be cached, not the 40 pad hashes the sender padded with.
        Assert.Equal(88, target.Files[0].PieceLayerHashes.Count);
        Assert.Equal(pieceLayer[599], target.GetV2ExpectedPieceHash(599));
        Assert.False(target.Files[0].PieceLayerHashes.ContainsKey(600));
    }

    [Fact]
    public void TryAddV2Hashes_NonPowerOfTwoPieceCount_LargerThanBlock_VerifiesFullLayer()
    {
        // 96KB file, 32KB pieces => 3 pieces, blocks_per_piece = 2. This is the exact case
        // where the previous merkle padding bug surfaced: any chunk verification used zero hash
        // instead of merkle_pad(2, 1) = SHA256(zero || zero).
        long fileSize = 96 * 1024;
        const uint pieceSize = 32 * 1024;
        byte[] fileData = new byte[fileSize];
        Random.Shared.NextBytes(fileData);

        var leaves = Internals.Utilities.MerkleTree.ComputeLeaves(fileData);
        var piecesRoot = Internals.Utilities.MerkleTree.ComputeRoot(leaves);
        var pieceLayer = Internals.Utilities.MerkleTree.GetPieceLayer(leaves, pieceSize);

        var source = new Internals.TorrentFileInfo
        {
            PieceSize = pieceSize,
            FullSize = fileSize,
            Version = Internals.TorrentVersion.V2
        };
        source.Files.Add(new Internals.TorrentFileEntry
        {
            Offset = 0,
            Size = fileSize,
            FirstPieceIndex = 0,
            PieceCount = 3,
            PiecesRoot = piecesRoot,
            PieceLayers = pieceLayer
        });

        int baseLayer = Internals.Utilities.MerkleTree.GetPieceLayerDepth(pieceSize);
        int proofLayers = Internals.Utilities.MerkleTree.GetTotalLevels(fileSize) - baseLayer - 1;

        // 3 pieces => NextPow2(3) = 4. Sender pads with 1 piece-layer pad hash.
        byte[]? hashes = source.GetV2Hashes(piecesRoot, baseLayer, 0, 4, proofLayers);
        Assert.NotNull(hashes);

        var target = new Internals.TorrentFileInfo
        {
            PieceSize = pieceSize,
            FullSize = fileSize,
            Version = Internals.TorrentVersion.V2
        };
        target.Files.Add(new Internals.TorrentFileEntry
        {
            Offset = 0,
            Size = fileSize,
            FirstPieceIndex = 0,
            PieceCount = 3,
            PiecesRoot = piecesRoot
        });

        Assert.True(target.TryAddV2Hashes(piecesRoot, baseLayer, 0, 4, proofLayers, hashes!));
        Assert.Equal(pieceLayer[2], target.GetV2ExpectedPieceHash(2));
        // Promotion: full piece layer reconstructed from cache once all real pieces arrive.
        Assert.NotNull(target.Files[0].PieceLayers);
        Assert.Equal(3, target.Files[0].PieceLayers!.Count);
    }

    [Fact]
    public void GetV2HashRequestForPiece_HashAlreadyCached_ReturnsNull()
    {
        var info = CreateV2Info(4 * 16_384, new byte[32], 4);
        info.Files[0].PieceLayerHashes[2] = new byte[32];

        Assert.Null(info.GetV2HashRequestForPiece(2));
        // Other pieces in the same chunk still need a request.
        Assert.NotNull(info.GetV2HashRequestForPiece(0));
    }

    [Fact]
    public void PieceLayerHashes_ConcurrentWrites_AllPersisted()
    {
        // The previous Dictionary<int, byte[]> wasn't thread-safe; we now use ConcurrentDictionary
        // so concurrent peer responses (different chunks of the same file) don't corrupt state.
        var info = CreateV2Info(2048 * 16_384, new byte[32], 2048);
        var file = info.Files[0];

        Parallel.For(0, 1024, i =>
        {
            byte[] hash = new byte[32];
            BitConverter.TryWriteBytes(hash, i);
            file.PieceLayerHashes[i] = hash;
        });

        Assert.Equal(1024, file.PieceLayerHashes.Count);
        for (int i = 0; i < 1024; i++)
        {
            Assert.True(file.PieceLayerHashes.ContainsKey(i));
        }
    }

    private static Internals.TorrentFileInfo CreateV2Info(int fileSize, byte[] piecesRoot, int? pieceCount = null)
    {
        var info = new Internals.TorrentFileInfo
        {
            PieceSize = 16_384,
            FullSize = fileSize,
            Version = Internals.TorrentVersion.V2
        };
        info.Files.Add(new Internals.TorrentFileEntry
        {
            Offset = 0,
            Size = fileSize,
            FirstPieceIndex = 0,
            PieceCount = pieceCount ?? fileSize / 16_384,
            PiecesRoot = piecesRoot
        });
        return info;
    }
}





