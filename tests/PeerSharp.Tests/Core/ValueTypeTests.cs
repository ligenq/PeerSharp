using PeerSharp.Core;

namespace PeerSharp.Tests.Core;

public class AtomicDisposalTests
{
    [Fact]
    public void MarkDisposed_SucceedsExactlyOnce()
    {
        // The whole point: whoever gets true owns the teardown, and everyone else must not repeat
        // it. Two callers both freeing the same handles is what this exists to prevent.
        var disposal = new AtomicDisposal();

        Assert.False(disposal.IsDisposed);
        Assert.True(disposal.MarkDisposed());
        Assert.True(disposal.IsDisposed);
        Assert.False(disposal.MarkDisposed());
        Assert.False(disposal.MarkDisposed());
    }

    [Fact]
    public void MarkDisposed_HandsTheWinToOneCallerUnderContention()
    {
        var disposal = new AtomicDisposal();
        int winners = 0;

        Parallel.For(0, 256, _ =>
        {
            if (disposal.MarkDisposed())
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
    }

    [Fact]
    public void ThrowIfDisposed_IsSilentBeforeAndThrowsAfter()
    {
        var disposal = new AtomicDisposal();
        var owner = new object();

        disposal.ThrowIfDisposed(owner);

        disposal.MarkDisposed();
        Assert.Throws<ObjectDisposedException>(() => disposal.ThrowIfDisposed(owner));
    }
}

public class TorrentFileEntryTests
{
    [Fact]
    public void Bep47Flags_AreReadOutOfTheAttributeSet()
    {
        var plain = CreateEntry(TorrentFileAttributes.None);
        Assert.False(plain.IsExecutable);
        Assert.False(plain.IsHidden);
        Assert.False(plain.IsSymlink);

        var executable = CreateEntry(TorrentFileAttributes.Executable);
        Assert.True(executable.IsExecutable);
        Assert.False(executable.IsHidden);

        var hidden = CreateEntry(TorrentFileAttributes.Hidden);
        Assert.True(hidden.IsHidden);
        Assert.False(hidden.IsExecutable);

        var link = CreateEntry(TorrentFileAttributes.Symlink);
        Assert.True(link.IsSymlink);
    }

    [Fact]
    public void SeveralAttributesCanBeSetAtOnce()
    {
        // They are flags, so a file can be both, and testing each alone would not catch a
        // comparison written as equality.
        var entry = CreateEntry(TorrentFileAttributes.Executable | TorrentFileAttributes.Hidden);

        Assert.True(entry.IsExecutable);
        Assert.True(entry.IsHidden);
        Assert.False(entry.IsSymlink);
    }

    [Fact]
    public void ToString_NamesTheFileAndItsSize()
    {
        // Under a thousand so no digit grouping applies: the separator is the running culture's,
        // and this is a display string rather than anything that goes on the wire.
        var entry = CreateEntry(TorrentFileAttributes.None, size: 512);

        Assert.Equal("dir/file.bin (512 bytes)", entry.ToString());
    }

    [Fact]
    public void TheOptionalBep47FieldsAreAbsentUnlessSupplied()
    {
        var plain = CreateEntry(TorrentFileAttributes.None);
        Assert.Null(plain.SymlinkTarget);
        Assert.Null(plain.Sha1);

        var full = new TorrentFileEntry(
            "link", 0, 3, TorrentFileAttributes.Symlink, "target/path", new byte[20]);

        Assert.Equal("target/path", full.SymlinkTarget);
        Assert.Equal(20, full.Sha1!.Value.Length);
        Assert.Equal(3, full.Index);
    }

    private static TorrentFileEntry CreateEntry(TorrentFileAttributes attributes, long size = 100) =>
        new("dir/file.bin", size, 0, attributes);
}

public class DownloadProgressTests
{
    [Fact]
    public void RemainingBytes_IsWhatIsLeftOfTheTotal()
    {
        var progress = new DownloadProgress { TotalBytes = 1000, FinishedBytes = 400 };

        Assert.Equal(600, progress.RemainingBytes);
    }

    [Fact]
    public void RemainingBytes_IsZeroWhenEverythingIsDone()
    {
        var progress = new DownloadProgress { TotalBytes = 1000, FinishedBytes = 1000 };

        Assert.Equal(0, progress.RemainingBytes);
    }

    [Fact]
    public void RemainingBytes_StaysSignedSoAnOvershootIsVisibleRatherThanEnormous()
    {
        // Both operands are unsigned. Subtracting them as unsigned would turn a 1-byte overshoot
        // into 18 exabytes remaining, which is why the property casts to long first.
        var progress = new DownloadProgress { TotalBytes = 1000, FinishedBytes = 1001 };

        Assert.Equal(-1, progress.RemainingBytes);
    }

    [Fact]
    public void TheReportedFieldsAreCarriedThrough()
    {
        var progress = new DownloadProgress
        {
            CompletedPieces = 5,
            TotalPieces = 10,
            Progress = 0.5f,
            SelectionProgress = 0.75f,
            FinishedBytes = 512,
            TotalBytes = 1024
        };

        Assert.Equal(5, progress.CompletedPieces);
        Assert.Equal(10, progress.TotalPieces);
        Assert.Equal(0.5f, progress.Progress);
        Assert.Equal(0.75f, progress.SelectionProgress);
        Assert.Equal(512UL, progress.FinishedBytes);
        Assert.Equal(1024UL, progress.TotalBytes);
    }
}

public class PieceProgressTests
{
    [Theory]
    [InlineData(0, 10, 0f)]
    [InlineData(5, 10, 0.5f)]
    [InlineData(10, 10, 1f)]
    public void Progress_IsTheCompletedShareOfTheTotal(int completed, int total, float expected)
    {
        var progress = new PieceProgress { CompletedPieces = completed, TotalPieces = total };

        Assert.Equal(expected, progress.Progress);
    }

    [Fact]
    public void Progress_IsZeroRatherThanNaNBeforeTheMetadataIsKnown()
    {
        // A magnet has no piece count until the metadata arrives, and a NaN here would propagate
        // into whatever the caller renders.
        var progress = new PieceProgress { CompletedPieces = 0, TotalPieces = 0 };

        Assert.Equal(0f, progress.Progress);
        Assert.False(float.IsNaN(progress.Progress));
    }

    [Fact]
    public void ThePieceIndexIsCarriedThrough()
    {
        var progress = new PieceProgress { PieceIndex = 42, CompletedPieces = 1, TotalPieces = 100 };

        Assert.Equal(42, progress.PieceIndex);
    }
}
