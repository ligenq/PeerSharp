using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

public class SeedingChokerTests
{
    private static readonly DateTimeOffset Epoch = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long PieceLength = 524_288; // 512 KiB
    private const int Quota = SeedingChoker.DefaultPieceQuota;

    // A helper that returns negative when x should be unchoked before y
    private static int Cmp(SeedingRankEntry x, SeedingRankEntry y, DateTimeOffset? now = null)
        => SeedingChoker.Compare(x, y, PieceLength, Quota, now ?? Epoch.AddHours(1));

    private static SeedingRankEntry Active(int uploadSpeed, long uploadedSinceUnchoked, DateTimeOffset lastUnchokedAt)
        => new() { UploadSpeed = uploadSpeed, IsChoked = false, UploadedSinceUnchoked = uploadedSinceUnchoked, LastUnchokedAt = lastUnchokedAt };

    // ── Quota de-prioritization ─────────────────────────────────────────────

    [Fact]
    public void Compare_NotQuotaComplete_BeatsQuotaComplete()
    {
        // within-quota peer unchoked for 90s, exceeded bytes, NOT past quota (no: bytes ok but time ok)
        // Actually: quota-complete = !IsChoked && bytes > quota && time >= 60s
        var withinQuota = Active(uploadSpeed: 100, uploadedSinceUnchoked: 100, lastUnchokedAt: Epoch);
        var pastQuota = Active(
            uploadSpeed: 1_000_000,
            uploadedSinceUnchoked: (PieceLength * Quota) + 1,
            lastUnchokedAt: Epoch); // unchoked at Epoch, now = Epoch+1h → time ≥ 60s → quota-complete

        int result = Cmp(withinQuota, pastQuota);
        Assert.True(result < 0, "Peer within quota should rank before quota-complete peer");
    }

    [Fact]
    public void Compare_BothWithinQuota_FasterUploadWins()
    {
        var fast = Active(uploadSpeed: 1000, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch);
        var slow = Active(uploadSpeed: 100, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch);

        Assert.True(Cmp(fast, slow) < 0, "Faster peer should rank before slower peer");
        Assert.True(Cmp(slow, fast) > 0, "Slower peer should rank after faster peer");
    }

    [Fact]
    public void Compare_BothPastQuota_FasterUploadWins()
    {
        const long bigUpload = (PieceLength * Quota) + 1;
        DateTimeOffset longAgo = Epoch.AddHours(-2); // unchoked >60s ago

        var fast = Active(uploadSpeed: 1000, uploadedSinceUnchoked: bigUpload, lastUnchokedAt: longAgo);
        var slow = Active(uploadSpeed: 100, uploadedSinceUnchoked: bigUpload, lastUnchokedAt: longAgo);

        Assert.True(Cmp(fast, slow) < 0, "Among quota-complete peers, faster upload should rank first");
    }

    // ── Quota conditions must both be satisfied ──────────────────────────────

    [Fact]
    public void Compare_BytesExceededButNotEnoughTime_NotQuotaComplete()
    {
        var now = Epoch.AddSeconds(30); // only 30 seconds since unchoke — not yet eligible

        // bytes > quota but unchoked only 30s → NOT quota-complete
        var heavyButNew = Active(
            uploadSpeed: 100,
            uploadedSinceUnchoked: (PieceLength * Quota) + 1,
            lastUnchokedAt: Epoch);

        var lightAndNew = Active(uploadSpeed: 200, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch);

        // heavyButNew is NOT quota-complete (time < 60s), so both are in same group
        // faster upload (lightAndNew) should win
        Assert.True(Cmp(lightAndNew, heavyButNew, now) < 0);
    }

    [Fact]
    public void Compare_TimeElapsedButBytesNotExceeded_NotQuotaComplete()
    {
        // unchoked 90s ago, but bytes uploaded well below quota → NOT quota-complete
        var timeElapsedLowBytes = Active(
            uploadSpeed: 100,
            uploadedSinceUnchoked: 1,
            lastUnchokedAt: Epoch);

        var fastNew = Active(uploadSpeed: 1000, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch);

        // Neither is quota-complete (bytes condition not met for first peer)
        // So sort by upload rate — fastNew should win
        Assert.True(Cmp(fastNew, timeElapsedLowBytes) < 0);
    }

    // ── Choked peer handling ─────────────────────────────────────────────────

    [Fact]
    public void Compare_ChokedPeerHasRateZero_EvenIfHistoricallyFast()
    {
        // An unchoked peer uploading at 100 B/s beats a choked peer, because
        // choked peers are treated as rate=0 regardless of their UploadSpeed field.
        var unchoked = Active(uploadSpeed: 100, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch);
        var chokedFast = new SeedingRankEntry
        {
            UploadSpeed = 1_000_000, // high speed, but choked
            IsChoked = true,
            UploadedSinceUnchoked = 0,
            LastUnchokedAt = Epoch.AddHours(-1), // waited longer
        };

        Assert.True(Cmp(unchoked, chokedFast) < 0, "Unchoked peer at any rate should beat choked peer");
    }

    [Fact]
    public void Compare_ChokedPeerIsNeverQuotaComplete()
    {
        // A choked peer with huge bytes and old unchoke time should not be quota-complete
        // because IsQuotaComplete checks !IsChoked first.
        var choked = new SeedingRankEntry
        {
            UploadSpeed = 0,
            IsChoked = true,
            UploadedSinceUnchoked = PieceLength * Quota * 100,
            LastUnchokedAt = Epoch.AddYears(-1),
        };
        var active = Active(uploadSpeed: 1, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch);

        // choked is NOT quota-complete, active is NOT quota-complete → both in same group
        // active has rate=1 > choked's rate=0 → active wins
        Assert.True(Cmp(active, choked) < 0);
    }

    // ── Tiebreak by wait time ────────────────────────────────────────────────

    [Fact]
    public void Compare_SameRate_LongestWaitingWins()
    {
        var waitedLonger = Active(uploadSpeed: 100, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch);
        var waitedLess = Active(uploadSpeed: 100, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch.AddMinutes(5));

        Assert.True(Cmp(waitedLonger, waitedLess) < 0, "Peer waiting longer (earlier LastUnchokedAt) should rank first");
    }

    [Fact]
    public void Compare_SameRate_SameWait_IsSymmetric()
    {
        var a = Active(uploadSpeed: 500, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch);
        var b = Active(uploadSpeed: 500, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch);

        Assert.Equal(0, Cmp(a, b));
        Assert.Equal(0, Cmp(b, a));
    }

    // ── Quota-complete peers rotated by wait time ────────────────────────────

    [Fact]
    public void Compare_QuotaComplete_LongestWaitingRotatesInFirst()
    {
        const long bigUpload = (PieceLength * Quota) + 1;

        // Both quota-complete; the one that's been waiting longer for their NEXT slot wins
        var waitedLonger = Active(
            uploadSpeed: 100,
            uploadedSinceUnchoked: bigUpload,
            lastUnchokedAt: Epoch.AddHours(-3));

        var waitedLess = Active(
            uploadSpeed: 2000,
            uploadedSinceUnchoked: bigUpload,
            lastUnchokedAt: Epoch.AddHours(-1));

        // Among quota-complete, upload rate sorts first, then wait time
        // waitedLess has higher upload speed → waitedLess wins
        Assert.True(Cmp(waitedLess, waitedLonger) < 0);
    }

    // ── Transitivity (sorting stability check) ───────────────────────────────

    [Fact]
    public void Compare_Transitivity_HoldsAcrossThreePeers()
    {
        const long bigUpload = (PieceLength * Quota) + 1;

        var A = Active(uploadSpeed: 1000, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch); // within quota, fast
        var B = Active(uploadSpeed: 500, uploadedSinceUnchoked: 0, lastUnchokedAt: Epoch);  // within quota, slower
        var C = Active(uploadSpeed: 2000, uploadedSinceUnchoked: bigUpload, lastUnchokedAt: Epoch); // quota-complete

        // A < B (A faster) and A < C (A within quota beats quota-complete C)
        Assert.True(Cmp(A, B) < 0);
        Assert.True(Cmp(A, C) < 0);
        // B < C (B within quota beats quota-complete C)
        Assert.True(Cmp(B, C) < 0);
    }
}
