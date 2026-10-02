using System.Text;
using PeerSharp.Internals;

namespace PeerSharp.Tests.Core;

public class ResumeStateValidationRegressionTests
{
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
