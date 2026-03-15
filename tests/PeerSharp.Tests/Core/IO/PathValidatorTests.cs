using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

public class PathValidatorTests
{
    private readonly string _rootPath;
    private readonly PathValidator _validator;

    public PathValidatorTests()
    {
        // Use a fixed root path for testing
        _rootPath = Path.Combine(Path.GetTempPath(), "MtTorrentTests", Guid.NewGuid().ToString());
        _validator = new PathValidator(_rootPath);
    }

    [Fact]
    public void ValidatePath_SimpleValidPath_ReturnsValid()
    {
        var result = _validator.ValidatePath("test.txt");
        Assert.True(result.IsValid);
        Assert.Equal(Path.Combine(_rootPath, "test.txt"), result.SanitizedPath);
        Assert.Equal(PathValidationError.None, result.Error);
    }

    [Fact]
    public void ValidatePath_NestedValidPath_ReturnsValid()
    {
        var result = _validator.ValidatePath("folder/sub/test.txt");
        Assert.True(result.IsValid);
        Assert.Equal(Path.Combine(_rootPath, "folder", "sub", "test.txt"), result.SanitizedPath);
    }

    [Fact]
    public void ValidatePath_EmptyOrWhitespace_ReturnsError()
    {
        Assert.Equal(PathValidationError.EmptyOrWhitespace, _validator.ValidatePath("").Error);
        Assert.Equal(PathValidationError.EmptyOrWhitespace, _validator.ValidatePath("   ").Error);
    }

    [Fact]
    public void ValidatePath_PathTraversal_ReturnsError()
    {
        Assert.Equal(PathValidationError.PathTraversalAttempt, _validator.ValidatePath("../test.txt").Error);
        Assert.Equal(PathValidationError.PathTraversalAttempt, _validator.ValidatePath("folder/../../test.txt").Error);
    }

    [Fact]
    public void ValidatePath_InvalidCharacters_ReturnsError()
    {
        // Characters like < > : " | ? * are invalid on Windows
        Assert.Equal(PathValidationError.InvalidCharacters, _validator.ValidatePath("test<abc>.txt").Error);
    }

    [Fact]
    public void ValidatePath_WindowsReservedName_ReturnsError()
    {
        Assert.Equal(PathValidationError.WindowsReservedName, _validator.ValidatePath("CON.txt").Error);
        Assert.Equal(PathValidationError.WindowsReservedName, _validator.ValidatePath("folder/LPT1").Error);
    }

    [Fact]
    public void ValidatePath_EscapesRoot_ReturnsError()
    {
        // Absolute paths or paths that go above root
        var result = _validator.ValidatePath("../../../etc/passwd");
        Assert.False(result.IsValid);
        Assert.Equal(PathValidationError.PathTraversalAttempt, result.Error);
    }

    [Fact]
    public void IsWindowsReservedName_CorrectlyIdentifies()
    {
        Assert.True(_validator.IsWindowsReservedName("CON"));
        Assert.True(_validator.IsWindowsReservedName("con"));
        Assert.True(_validator.IsWindowsReservedName("AUX"));
        Assert.True(_validator.IsWindowsReservedName("COM1"));
        Assert.True(_validator.IsWindowsReservedName("LPT9"));

        Assert.False(_validator.IsWindowsReservedName("normal"));
        Assert.False(_validator.IsWindowsReservedName(""));
    }
}





