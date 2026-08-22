using CsCheck;
using PeerSharp.Internals.Peers;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// The stream that puts already-read bytes back in front of a connection.
/// </summary>
/// <remarks>
/// <para>
/// A peer usually sends its handshake and the first messages in one segment, so reading the handshake
/// pulls part of the next message along with it. Those bytes have left the socket and cannot be put
/// back, so this serves them before continuing from the connection. Lose or duplicate one and the
/// message reader starts mid-message, which surfaces much later as a nonsense length prefix rather
/// than as anything pointing here.
/// </para>
/// <para>
/// The property is simply that a full drain yields the prefix followed by the stream, whatever sizes
/// the caller happens to read in - including the read that lands exactly on the boundary, and the one
/// that asks for more than the prefix holds.
/// </para>
/// </remarks>
public class PrefixedStreamPropertyTests
{
    private static readonly Gen<(byte[] Prefix, byte[] Inner, int[] Reads)> Case =
        Gen.Select(Gen.Byte.Array[0, 40], Gen.Byte.Array[0, 40], Gen.Int[1, 24].Array[1, 40]);

    [Fact]
    public async Task DrainingYieldsThePrefixThenTheStream()
    {
        await Case.SampleAsync(async test =>
        {
            using var inner = new MemoryStream(test.Inner, writable: false);
            await using var stream = new PrefixedStream(test.Prefix, inner, leaveInnerOpen: true);

            var drained = new List<byte>();
            int read = 0;
            while (true)
            {
                byte[] buffer = new byte[test.Reads[read++ % test.Reads.Length]];
                int count = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
                if (count == 0)
                {
                    break;
                }

                Assert.InRange(count, 1, buffer.Length);
                drained.AddRange(buffer[..count]);
            }

            Assert.Equal([.. test.Prefix, .. test.Inner], drained);
        }, iter: 5_000);
    }

    [Fact]
    public void TheSynchronousPathReadsTheSameBytes()
    {
        // Both overloads carry their own copy of the prefix bookkeeping, so both are worth draining.
        Case.Sample(test =>
        {
            using var inner = new MemoryStream(test.Inner, writable: false);
            using var stream = new PrefixedStream(test.Prefix, inner, leaveInnerOpen: true);

            var drained = new List<byte>();
            int read = 0;
            while (true)
            {
                byte[] buffer = new byte[test.Reads[read++ % test.Reads.Length]];
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break;
                }

                drained.AddRange(buffer[..count]);
            }

            Assert.Equal([.. test.Prefix, .. test.Inner], drained);
        }, iter: 5_000);
    }

    [Fact]
    public async Task ReadingIntoAnOffsetLeavesTheRestOfTheBufferAlone()
    {
        // The byte[]/offset/count overload is the one that writes somewhere other than the start, so
        // it is the one that can scribble outside its window.
        await Gen.Select(Case, Gen.Int[1, 8]).SampleAsync(async (test, offset) =>
        {
            using var inner = new MemoryStream(test.Inner, writable: false);
            await using var stream = new PrefixedStream(test.Prefix, inner, leaveInnerOpen: true);

            byte[] buffer = new byte[offset + 16];
            Array.Fill(buffer, (byte)0xEE);

            int count = await stream.ReadAsync(buffer, offset, 16, TestContext.Current.CancellationToken);

            Assert.All(buffer[..offset], b => Assert.Equal(0xEE, b));
            Assert.All(buffer[(offset + count)..], b => Assert.Equal(0xEE, b));
        }, iter: 2_000);
    }

    [Fact]
    public async Task AnEmptyPrefixIsJustTheStream()
    {
        await Gen.Byte.Array[0, 40].SampleAsync(async content =>
        {
            using var inner = new MemoryStream(content, writable: false);
            await using var stream = new PrefixedStream(ReadOnlyMemory<byte>.Empty, inner, leaveInnerOpen: true);

            var drained = new MemoryStream();
            await stream.CopyToAsync(drained, TestContext.Current.CancellationToken);

            Assert.Equal(content, drained.ToArray());
        }, iter: 1_000);
    }

    [Fact]
    public async Task TheWrappedStreamIsDisposedOnlyWhenItIsOwned()
    {
        // PeerCommunication owns the connection and closes it itself, so this wrapper must not; a
        // wrapper that closed it early would drop a live peer connection.
        var kept = new MemoryStream([1, 2, 3]);
        await using (var stream = new PrefixedStream(new byte[] { 9 }, kept, leaveInnerOpen: true))
        {
        }

        Assert.True(kept.CanRead, "the wrapped stream was disposed despite leaveInnerOpen");

        var owned = new MemoryStream([1, 2, 3]);
        await using (var stream = new PrefixedStream(new byte[] { 9 }, owned, leaveInnerOpen: false))
        {
        }

        Assert.False(owned.CanRead, "the wrapped stream outlived the wrapper that owned it");
    }
}
