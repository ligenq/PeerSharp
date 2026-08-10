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
    /// A Windows reserved name keeps all of its extension: CON.tar.gz is still a .tar.gz file, so
    /// the suffix goes on the device-name stem rather than the end. Other platforms keep names they
    /// can store unchanged.
    /// </summary>
    [Fact]
    public void ValidatePath_WindowsReservedName_IsSuffixedRatherThanDroppedOnWindows()
    {
        var withExtension = _validator.ValidatePath("CON.tar.gz");
        Assert.True(withExtension.IsValid);
        Assert.Equal(
            OperatingSystem.IsWindows() ? "CON_.tar.gz" : "CON.tar.gz",
            Path.GetFileName(withExtension.SanitizedPath));

        var bare = _validator.ValidatePath("folder/LPT1");
        Assert.True(bare.IsValid);
        Assert.Equal(
            OperatingSystem.IsWindows() ? "LPT1_" : "LPT1",
            Path.GetFileName(bare.SanitizedPath));

        var superscript = _validator.ValidatePath("COM¹.txt");
        Assert.True(superscript.IsValid);
        Assert.Equal(
            OperatingSystem.IsWindows() ? "COM¹_.txt" : "COM¹.txt",
            Path.GetFileName(superscript.SanitizedPath));
    }

    /// <summary>
    /// Windows resolves a trailing dot or space away, so "name." and "name" would address one file.
    /// Trimming makes that collision visible to the caller instead of silent on disk.
    /// </summary>
    [Fact]
    public void ValidatePath_TrailingDotsAndSpaces_FollowPlatformRules()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? "report.txt" : "report.txt. ",
            Path.GetFileName(_validator.ValidatePath("report.txt. ").SanitizedPath));
        Assert.Equal(
            OperatingSystem.IsWindows() ? "name" : "name.",
            Path.GetFileName(_validator.ValidatePath("name.").SanitizedPath));
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

        // Only Windows can trim a component away to nothing; "...  " is a storable directory name
        // everywhere else, so there the file keeps its parent. Asserted by shape rather than by the
        // exact string, which would be a claim about how the platform round-trips trailing spaces.
        string? directory = Path.GetDirectoryName(result.SanitizedPath);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(_rootPath, directory);
        }
        else
        {
            Assert.NotEqual(_rootPath, directory);
            Assert.StartsWith(_rootPath, directory, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A name is not an escape attempt for beginning with two dots. The containment check compared
    /// the relative path as a string, so "..foo" tripped it exactly as "../foo" does - a legal
    /// directory name on every platform, and one a torrent may perfectly well contain.
    /// </summary>
    [Theory]
    [InlineData("..foo/real.txt", "..foo")]
    [InlineData("..config/settings.ini", "..config")]
    [InlineData("...bar/real.txt", "...bar")]
    public void ValidatePath_ANameBeginningWithTwoDots_IsNotMistakenForTraversal(
        string relativePath,
        string expectedDirectory)
    {
        var result = _validator.ValidatePath(relativePath);

        Assert.True(result.IsValid, $"'{relativePath}' is a name, not a traversal.");
        Assert.Equal(
            Path.Combine(_rootPath, expectedDirectory),
            Path.GetDirectoryName(result.SanitizedPath));
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
        Assert.True(_validator.IsWindowsReservedName("COM¹"));
        Assert.True(_validator.IsWindowsReservedName("LPT9"));
        Assert.True(_validator.IsWindowsReservedName("LPT²"));

        // Documented by Microsoft alongside the rest, and cheaper to carry than to argue about: this
        // list only has to be a superset of what some Windows version refuses, since being on it
        // costs one underscore and being wrongly off it costs the file.
        Assert.True(_validator.IsWindowsReservedName("COM0"));
        Assert.True(_validator.IsWindowsReservedName("LPT0"));

        Assert.False(_validator.IsWindowsReservedName("normal"));
        Assert.False(_validator.IsWindowsReservedName(""));
    }
}





