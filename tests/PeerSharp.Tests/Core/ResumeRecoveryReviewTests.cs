using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

public sealed class ResumeRecoveryReviewTests
{
    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeValidatesFileIdentityEvenWhenAnEditKeepsTheSameLength(bool editFirstFile)
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSharpResumeReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.Hash = InfoHash.CreateRandom();
            metadata.Info.PieceSize = 1000;
            metadata.Info.FullSize = 2000;
            metadata.Info.Pieces = [new byte[20], new byte[20]];
            metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "a.bin", Offset = 0, Size = 1000 });
            metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "b.bin", Offset = 1000, Size = 1000 });
            TorrentResumeData resume;
            await using (var original = TorrentTestUtility.CreateMinimal(metadata, root))
            {
                await original.FilesInternal.InitializeAsync([]);
                await original.FilesInternal.WriteAsync(0, new byte[2000], TestContext.Current.CancellationToken);
                Assert.True(await original.FilesInternal.FlushAsync(TestContext.Current.CancellationToken));
                original.Pieces.AddPiece(0);
                original.Pieces.AddPiece(1);
                resume = original.GetResumeData();
            }
            if (editFirstFile)
            {
                string path = Path.Combine(root, "a.bin");
                var previousTime = File.GetLastWriteTimeUtc(path);
                await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)0xFF, 1000).ToArray());
                File.SetLastWriteTimeUtc(path, previousTime.AddSeconds(5));
            }
            await using var restored = TorrentTestUtility.CreateMinimal(metadata, root, resumeData: resume);
            Assert.True(restored.Pieces.HasPiece(0));
            await restored.FilesInternal.InitializeAsync([]);
            Assert.Equal(!editFirstFile, restored.Pieces.HasPiece(0));
            Assert.True(restored.Pieces.HasPiece(1));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
