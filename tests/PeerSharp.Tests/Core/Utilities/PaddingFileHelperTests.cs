using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core.Utilities;

public class PaddingFileHelperTests
{
    [Theory]
    [InlineData(".pad/16384-0", true)]
    [InlineData(".pad\\16384-0", true)]
    [InlineData("dir/.pad/file", true)]
    [InlineData("file.txt", false)]
    [InlineData("pad/123", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsPaddingPath_IdentifiesCorrectPaths(string? path, bool expected)
    {
#pragma warning disable CS8604 // Possible null reference argument.
        Assert.Equal(expected, PaddingFileHelper.IsPaddingPath(path));
#pragma warning restore CS8604 // Possible null reference argument.
    }

    [Fact]
    public void BuildPaddingPath_ReturnsExpectedFormat()
    {
        string path = PaddingFileHelper.BuildPaddingPath(16384, 5);
        Assert.Equal(".pad/16384-5", path);
    }
}
