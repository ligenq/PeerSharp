using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using static PeerSharp.Tests.Core.Peers.BandwidthTestDoubles;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// The rate limiting stream layer.
///
/// <para>
/// This logic previously lived inside <c>EncryptedStream</c>, so it only ran on connections that
/// negotiated encryption and a configured limit silently did nothing on plaintext peers. It now wraps
/// every peer connection, so these cover it directly rather than through an encryption test.
/// </para>
/// </summary>
public class RateLimitedStreamTests
{
    private static RateLimitedStream Create(Stream inner, TestBandwidthManager manager)
    {
        return new RateLimitedStream(
            inner,
            new TestBandwidthUser(),
            manager,
            [DownloadChannel],
            [UploadChannel],
            leaveInnerOpen: true);
    }

    [Fact]
    public async Task ReadAsync_PassesDataThroughUnchanged()
    {
        byte[] payload = new byte[100];
        Random.Shared.NextBytes(payload);

        await using var inner = new MemoryStream(payload);
        var manager = new TestBandwidthManager();
        await using var stream = Create(inner, manager);

        byte[] buffer = new byte[payload.Length];
        int read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task ReadAsync_ReturnsTheUnusedReservationOnDispose()
    {
        // Quota is reserved in batches, so whatever is left over when the connection ends has to go
        // back. Otherwise every short-lived peer permanently shrinks the budget.
        byte[] payload = new byte[100];

        await using var inner = new MemoryStream(payload);
        var manager = new TestBandwidthManager();
        var stream = Create(inner, manager);

        int read = await stream.ReadAsync(new byte[payload.Length], TestContext.Current.CancellationToken);
        stream.Dispose();

        Assert.Equal(ProtocolConstants.DownloadBatchSize - read, manager.ReturnedDownload);
        Assert.Equal(0, manager.ReturnedUpload);
    }

    [Fact]
    public async Task WriteAsync_PassesDataThroughUnchanged()
    {
        byte[] payload = new byte[100];
        Random.Shared.NextBytes(payload);

        await using var inner = new MemoryStream();
        var manager = new TestBandwidthManager();
        await using var stream = Create(inner, manager);

        await stream.WriteAsync(payload, TestContext.Current.CancellationToken);

        Assert.Equal(payload, inner.ToArray());
    }

    [Fact]
    public async Task WriteAsync_ReturnsTheUnusedReservationOnDispose()
    {
        byte[] payload = new byte[100];

        await using var inner = new MemoryStream();
        var manager = new TestBandwidthManager();
        var stream = Create(inner, manager);

        await stream.WriteAsync(payload, TestContext.Current.CancellationToken);
        stream.Dispose();

        Assert.Equal(ProtocolConstants.UploadBatchSize - payload.Length, manager.ReturnedUpload);
        Assert.Equal(0, manager.ReturnedDownload);
    }

    [Fact]
    public async Task ReadAsync_WhenNoQuotaIsGranted_ReadsNothing()
    {
        // A denial must not be mistaken for end of stream by reading anyway; returning 0 lets the peer
        // loop retry once quota is replenished.
        byte[] payload = new byte[100];

        await using var inner = new MemoryStream(payload);
        var manager = new TestBandwidthManager { GrantAmount = 0 };
        await using var stream = Create(inner, manager);

        int read = await stream.ReadAsync(new byte[payload.Length], TestContext.Current.CancellationToken);

        Assert.Equal(0, read);
        Assert.Equal(0, inner.Position);
    }

    [Fact]
    public async Task WriteAsync_WhenNoQuotaIsGranted_WritesNothing()
    {
        await using var inner = new MemoryStream();
        var manager = new TestBandwidthManager { GrantAmount = 0 };
        await using var stream = Create(inner, manager);

        await stream.WriteAsync(new byte[100], TestContext.Current.CancellationToken);

        Assert.Empty(inner.ToArray());
    }

    [Fact]
    public async Task ReadAsync_PartialGrant_ReadsOnlyWhatWasGranted()
    {
        // The throttle itself: a partial grant must cap the read rather than being rounded up.
        byte[] payload = new byte[100];
        Random.Shared.NextBytes(payload);

        await using var inner = new MemoryStream(payload);
        var manager = new TestBandwidthManager { GrantAmount = 10 };
        await using var stream = Create(inner, manager);

        int read = await stream.ReadAsync(new byte[payload.Length], TestContext.Current.CancellationToken);

        Assert.Equal(10, read);
    }

    [Fact]
    public async Task WriteAsync_PartialGrants_StillWriteEverything()
    {
        // Writes must not silently truncate: the loop keeps requesting until the buffer is drained.
        byte[] payload = new byte[100];
        Random.Shared.NextBytes(payload);

        await using var inner = new MemoryStream();
        var manager = new TestBandwidthManager { GrantAmount = 16 };
        await using var stream = Create(inner, manager);

        await stream.WriteAsync(payload, TestContext.Current.CancellationToken);

        Assert.Equal(payload, inner.ToArray());
    }

    [Fact]
    public void Dispose_LeaveInnerOpenFalse_DisposesInner()
    {
        var inner = new MemoryStream();
        var stream = new RateLimitedStream(
            inner,
            new TestBandwidthUser(),
            new TestBandwidthManager(),
            [DownloadChannel],
            [UploadChannel],
            leaveInnerOpen: false);

        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => inner.Position);
    }
}
