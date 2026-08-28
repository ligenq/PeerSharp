using CsCheck;
using PeerSharp.PieceWriter;

namespace PeerSharp.Tests.Core.IO;

/// <summary>
/// The one thing <see cref="PathValidator"/> must never do, checked against generated input rather
/// than remembered attacks.
/// </summary>
/// <remarks>
/// Every path here comes out of a torrent, which is to say from a stranger, and the file names in it
/// are chosen by whoever made it. The property that matters is not that any particular trick is
/// caught but that nothing the validator accepts can name a location outside the download directory.
/// Example tests can only cover the tricks someone thought of; an attacker is generating too, which
/// makes this the shape of test that fits the problem.
/// </remarks>
public class PathValidatorPropertyTests
{
    /// <summary>
    /// Path fragments worth combining: traversal, separators, roots and UNC prefixes, names Windows
    /// reserves or rewrites, characters no platform will store, and ordinary names that merely look
    /// suspicious and must still be accepted.
    /// </summary>
    private static readonly Gen<string> Component = Gen.OneOfConst(
        "..", ".", "...", "..foo", "foo..", ".hidden",
        "", " ", "  ", "\t",
        "/", "\\", "//", "\\\\", "/..", "..\\",
        "C:", "C:\\", "c:/windows", @"\\server\share", "//server/share",
        "CON", "NUL", "COM1", "LPT1", "CON.txt", "con.tar.gz", "NUL.",
        "name.", "name ", "name..", "name . ",
        "<", ">", "|", ":", "*", "?", "\"", "a<b>c", "British | SDH",
        "\u0000", "a\u0000b", "\u00e9\u4e2d\u6587", "\ud83d\ude00",
        "normal.txt", "sub", "a", new string('x', 300));

    private static readonly Gen<string> Path_ = Component.Array[0, 6]
        .SelectMany(parts => Gen.OneOfConst("/", "\\").Select(separator => string.Join(separator, parts)));

    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PeerSharpPathProps", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AnAcceptedPathNeverLeavesTheRoot()
    {
        var validator = new PathValidator(_root);
        string rootPrefix = System.IO.Path.GetFullPath(_root)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;

        Path_.Sample(path =>
        {
            var result = validator.ValidatePath(path);
            if (!result.IsValid)
            {
                return;
            }

            Assert.NotNull(result.SanitizedPath);

            // Checked by prefix against the fully resolved root, which is a different mechanism from
            // the relative-path comparison the validator itself uses - so a fault in that comparison
            // cannot hide behind the same fault here.
            Assert.StartsWith(rootPrefix, result.SanitizedPath, StringComparison.Ordinal);
            Assert.True(
                System.IO.Path.IsPathFullyQualified(result.SanitizedPath),
                $"accepted path was not fully qualified: {result.SanitizedPath}");
        }, iter: 20_000);
    }

    [Fact]
    public void AnAcceptedPathContainsNoTraversalComponent()
    {
        // Belt and braces for the property above: a ".." surviving into the result would be a
        // traversal that only the filesystem, or a symlink, would later resolve.
        var validator = new PathValidator(_root);

        Path_.Sample(path =>
        {
            var result = validator.ValidatePath(path);
            if (!result.IsValid)
            {
                return;
            }

            string[] components = result.SanitizedPath!.Split(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);

            Assert.DoesNotContain("..", components);
        }, iter: 20_000);
    }

    [Fact]
    public void ValidationNeverThrows()
    {
        // The caller is a torrent parser handling a stranger's file list. An exception here is a
        // malformed name taking down the torrent rather than being rejected as one.
        var validator = new PathValidator(_root);

        Path_.Sample(path =>
        {
            var result = validator.ValidatePath(path);
            Assert.Equal(result.IsValid, result.SanitizedPath != null);
            Assert.Equal(result.IsValid, result.Error == PathValidationError.None);
        }, iter: 20_000);
    }

    [Fact]
    public void ValidationIsDeterministic()
    {
        var validator = new PathValidator(_root);

        Path_.Sample(path =>
        {
            var first = validator.ValidatePath(path);
            var second = validator.ValidatePath(path);

            Assert.Equal(first, second);
        }, iter: 5_000);
    }

    [Fact]
    public void RewritingAComponentNeverIntroducesASeparator()
    {
        // MakeUsableFileName replaces characters a platform will not store. If a replacement could
        // ever produce a separator, one component would silently become two and the depth of the
        // written path would no longer be the depth the validator checked.
        Component.Sample(component =>
        {
            string usable = PathValidator.MakeUsableFileName(component);

            Assert.DoesNotContain(System.IO.Path.DirectorySeparatorChar, usable);
            Assert.DoesNotContain(System.IO.Path.AltDirectorySeparatorChar, usable);
            Assert.NotEqual("..", usable);
            Assert.NotEqual(".", usable);
        }, iter: 2_000);
    }
}
