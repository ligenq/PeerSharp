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

    /// <summary>
    /// A name the platform will not accept is rewritten, not discarded. Dropping it lost the file
    /// while the torrent still reported complete: "Subs\British | SDH.eng.HI.srt" is a real subtitle
    /// name from a real release, and it went missing on Windows and arrived on Linux.
    /// </summary>
    [Fact]
    public void ValidatePath_CharactersThePlatformRejects_AreRewrittenAndTheFileKept()
    {
        var result = _validator.ValidatePath("Subs/British | SDH.eng.HI.srt");

        Assert.True(result.IsValid);
        Assert.Equal(PathValidationError.None, result.Error);
        Assert.NotNull(result.SanitizedPath);

        // Path.GetInvalidFileNameChars() is platform-specific, so the expected name is too: a pipe is
        // illegal on Windows and ordinary everywhere else. Renaming it on Linux would be the bug.
        string expected = OperatingSystem.IsWindows() ? "British _ SDH.eng.HI.srt" : "British | SDH.eng.HI.srt";
        Assert.Equal(expected, Path.GetFileName(result.SanitizedPath));
        Assert.Equal("Subs", Path.GetFileName(Path.GetDirectoryName(result.SanitizedPath)));
    }

    [Fact]
    public void ValidatePath_TheNullCharacter_IsRewrittenOnEveryPlatform()
    {
        var result = _validator.ValidatePath("test\0.txt");

        Assert.True(result.IsValid);
        Assert.Equal("test_.txt", Path.GetFileName(result.SanitizedPath));
    }

    /// <summary>
    /// A reserved name keeps its extension: CON.txt is still a .txt file, so the suffix goes on the
    /// stem rather than the end.
    /// </summary>
    [Fact]
    public void ValidatePath_WindowsReservedName_IsSuffixedRatherThanDropped()
    {
        var withExtension = _validator.ValidatePath("CON.txt");
        Assert.True(withExtension.IsValid);
        Assert.Equal("CON_.txt", Path.GetFileName(withExtension.SanitizedPath));

        var bare = _validator.ValidatePath("folder/LPT1");
        Assert.True(bare.IsValid);
        Assert.Equal("LPT1_", Path.GetFileName(bare.SanitizedPath));
    }

    /// <summary>
    /// Windows resolves a trailing dot or space away, so "name." and "name" would address one file.
    /// Trimming makes that collision visible to the caller instead of silent on disk.
    /// </summary>
    [Fact]
    public void ValidatePath_TrailingDotsAndSpaces_AreTrimmed()
    {
        Assert.Equal("report.txt", Path.GetFileName(_validator.ValidatePath("report.txt. ").SanitizedPath));
        Assert.Equal("name", Path.GetFileName(_validator.ValidatePath("name.").SanitizedPath));
    }

    /// <summary>
    /// A component that is nothing but unusable characters contributes no directory level, but the
    /// file itself still arrives - rewriting must not turn into rejection by another route.
    /// </summary>
    [Fact]
    public void ValidatePath_AComponentThatSanitizesAway_DoesNotTakeTheFileWithIt()
    {
        var result = _validator.ValidatePath("...  /real.txt");

        Assert.True(result.IsValid);
        Assert.Equal("real.txt", Path.GetFileName(result.SanitizedPath));
    }

    /// <summary>
    /// Traversal is the actual security concern and stays a hard refusal - rewriting applies to names
    /// the platform cannot store, never to a path trying to leave the download directory.
    /// </summary>
    [Fact]
    public void ValidatePath_Traversal_IsStillRefusedOutright()
    {
        Assert.False(_validator.ValidatePath("../../etc/passwd").IsValid);
        Assert.False(_validator.ValidatePath("folder/../../x.txt").IsValid);
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





