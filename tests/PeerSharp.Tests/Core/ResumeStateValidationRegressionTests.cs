using System.Text;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

public class ResumeStateValidationRegressionTests
{
    [Theory(Timeout = 15000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NullUnfinishedPiecesMeansNoPartialDataAndDoesNotBreakStart(bool hasMetadata, bool immediateRestore)
    {
        string root = Path.Combine(Path.GetTempPath(), "PeerSharpNullResume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var metadata = new TorrentFileMetadata();
            metadata.Info.Name = "null-resume.bin";
            metadata.Info.Hash = InfoHash.CreateRandom();
            if (hasMetadata)
            {
                metadata.Info.FullSize = 1000;
                metadata.Info.PieceSize = 16384;
                metadata.Info.Pieces.Add(new byte[20]);
                metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "null-resume.bin", Size = 1000 });
            }
            var resume = new TorrentResumeData { Data = Encoding.UTF8.GetBytes("{\"Downloaded\":123,\"UnfinishedPieces\":null}") };
            await using var torrent = TorrentTestUtility.CreateMinimal(metadata, root, resumeData: resume, loadResumePiecesForStart: immediateRestore);
            Assert.Equal(123UL, torrent.LocalState.Downloaded);
            Assert.NotNull(torrent.LocalState.UnfinishedPieces);
            Assert.Empty(torrent.LocalState.UnfinishedPieces);
            if (immediateRestore) await torrent.DeferRestoredPiecesAsync(TestContext.Current.CancellationToken);
            await torrent.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(torrent.Started);
            await torrent.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("{\"Info\":null}")]
    [InlineData("{\"Pieces\":null}")]
    [InlineData("{\"Selection\":null}")]
    [InlineData("{\"Selection\":[null]}")]
    [InlineData("{\"RenamedFiles\":null}")]
    [InlineData("{\"RenamedFiles\":[null]}")]
    [InlineData("{\"AddedTime\":9223372036854775807}")]
    [InlineData("{\"SeedTimeSeconds\":9223372036854775807}")]
    [InlineData("{\"SeedTimeSeconds\":-1}")]
    [InlineData("{\"Pieces\":\"AAE=\"}")]
    [InlineData("{\"FileSnapshots\":[null]}")]
    [InlineData("{\"FileSnapshots\":[{\"Index\":-1}]}")]
    [InlineData("{\"FileSnapshots\":[{\"Index\":0},{\"Index\":0}]}")]
    [InlineData("{\"FileSnapshots\":[{\"Index\":0,\"LastWriteTimeUtcTicks\":9223372036854775807}]}")]
    public async Task MalformedResumeFieldsAreRejectedWithoutReplacingUsableState(string json)
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = 9L * ProtocolConstants.BlockSize;
        metadata.Info.Files.Add(new Internals.TorrentFileEntry { Path = "file.bin", Size = metadata.Info.FullSize });
        await using var torrent = TorrentTestUtility.CreateMinimal(metadata);
        var original = torrent.LocalState;
        torrent.ApplyResumeData(new TorrentResumeData { Data = Encoding.UTF8.GetBytes(json) });
        Assert.Same(original, torrent.LocalState);
    }
}
