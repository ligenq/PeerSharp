using PeerSharp.Exceptions;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Api;

/// <summary>
/// The distinctions a consumer is supposed to be able to make without reading message strings.
/// </summary>
/// <remarks>
/// <para>
/// This was the whole complaint the error model set out to fix: a malformed torrent, a tracker that
/// refused, and a disk that could not be written all arrived as framework exceptions from three
/// different namespaces, so telling them apart meant matching on <c>ex.Message</c>. These tests are
/// the contract that says they no longer do.
/// </para>
/// <para>
/// The other half is what did <em>not</em> move. A caller passing null still gets
/// <see cref="ArgumentNullException"/>, and cancellation still arrives as
/// <see cref="OperationCanceledException"/>, because those say the calling code has a bug or changed
/// its mind - neither is the library reporting a failure of its own.
/// </para>
/// </remarks>
public class ErrorModelTests
{
    [Fact]
    public void EverythingThisLibraryReportsSharesOneRoot()
    {
        // A consumer that only wants "did PeerSharp fail" needs exactly one catch.
        Assert.IsAssignableFrom<PeerSharpException>(new TorrentMetadataException("x"));
        Assert.IsAssignableFrom<PeerSharpException>(new StorageException("x"));
        Assert.IsAssignableFrom<PeerSharpException>(new TrackerException("x"));
        Assert.IsAssignableFrom<PeerSharpException>(new UdpTrackerException("x"));
        Assert.IsAssignableFrom<PeerSharpException>(new TorrentException("x"));
        Assert.IsAssignableFrom<PeerSharpException>(new TorrentNotFoundException(default));
    }

    [Fact]
    public void TheThreeFailuresTheEntryNamedAreNowDistinguishable()
    {
        // "tracker rejected the announce", "disk full", "malformed torrent file" - the three cases
        // the improvement entry said could only be told apart by message.
        Assert.IsNotType<StorageException>(new TorrentMetadataException("malformed"));
        Assert.IsNotType<TorrentMetadataException>(new StorageException("disk full"));
        Assert.IsNotType<TrackerException>(new StorageException("disk full"));

        Assert.False(new StorageException("full disk", null, isRecoverable: false).IsRecoverable);
        Assert.True(new TrackerException("timed out", isTransient: true).IsTransient);
    }

    [Fact]
    public void AMalformedTorrentIsReportedAsMetadata()
    {
        var thrown = Assert.Throws<TorrentMetadataException>(() => TorrentFile.Parse([]));

        Assert.IsAssignableFrom<PeerSharpException>(thrown);
    }

    [Fact]
    public void AMalformedTorrentFromTheParserCarriesWhatWentWrongUnderneath()
    {
        // The bencode failure is kept as the inner exception rather than discarded: the domain type
        // says which layer failed, the inner says what it saw.
        var thrown = Assert.Throws<TorrentMetadataException>(
            () => TorrentFileParser.Parse("d8:announce4:teste"u8.ToArray()));

        Assert.NotNull(thrown.InnerException);
    }

    [Fact]
    public void AMalformedMagnetLinkNamesTheLinkItCouldNotRead()
    {
        var thrown = Assert.Throws<TorrentMetadataException>(() => MagnetLink.Parse("not-a-magnet"));

        Assert.Equal("not-a-magnet", thrown.MetadataSource);
    }

    [Fact]
    public void CallerMistakesAreStillCallerMistakes()
    {
        // Not wrapped, deliberately. These say the calling code has a bug, and turning them into a
        // library failure type would hide that from whoever has to fix it.
        Assert.Throws<ArgumentNullException>(() => TorrentFile.Parse((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => MagnetLink.Parse(null!));
    }

    [Fact]
    public void TheTryPatternStillReportsFailureWithoutThrowing()
    {
        // Nothing above changes the cheap path: a caller that expects bad input asks rather than
        // catches, and pays no exception at all.
        Assert.False(TorrentFile.TryParse([], out _, out string? torrentError));
        Assert.False(MagnetLink.TryParse("not-a-magnet", out _, out string? magnetError));

        Assert.NotNull(torrentError);
        Assert.NotNull(magnetError);
    }
}
