using System.IO.Pipelines;
using System.Security.Cryptography;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Interop;

/// <summary>
/// Isolates the encrypted stream from the network.
///
/// <para>
/// A live run against Transmission decrypts several MiB correctly and then produces bytes that were
/// never plaintext, with reads that are provably single-threaded, uncancelled, contiguous and
/// uniform. Either the pipeline is chunk-sensitive in a way that only one peer's write pattern
/// exposes, or it is fine and the fault is elsewhere. These run it with no socket involved.
/// </para>
/// </summary>
public sealed class EncryptedStreamRoundTripTests
{
    /// <summary>
    /// RC4 is a stream cipher: the split between calls must not change the output. This is the
    /// property the live failure would need to violate.
    /// </summary>
    [Fact]
    public void Rc4_IsIndependentOfHowTheStreamIsSplit()
    {
        var key = RandomNumberGenerator.GetBytes(20);
        var plaintext = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);

        var whole = plaintext.ToArray();
        var oneCall = new RC4();
        oneCall.Init(key);
        oneCall.Encrypt(whole);

        var chunked = plaintext.ToArray();
        var manyCalls = new RC4();
        manyCalls.Init(key);
        var random = new Random(20260802);
        int offset = 0;
        while (offset < chunked.Length)
        {
            int take = Math.Min(random.Next(1, 20_000), chunked.Length - offset);
            manyCalls.Encrypt(chunked, offset, take);
            offset += take;
        }

        Assert.Equal(whole, chunked);
    }

    /// <summary>
    /// The production path: one EncryptedStream writing, another reading, with the reader taking
    /// different sized bites than the writer produced - which is what a socket does.
    /// </summary>
    [Fact]
    public async Task EncryptedStream_RoundTripsMegabytesAcrossMismatchedChunks()
    {
        var key = RandomNumberGenerator.GetBytes(20);
        var payload = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);

        var sender = new ProtocolEncryption();
        sender.RC4Out.Init(key);
        var receiver = new ProtocolEncryption();
        receiver.RC4In.Init(key);

        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19));
        await using var writeSide = new EncryptedStream(pipe.Writer.AsStream(), sender, leaveInnerOpen: true);
        await using var readSide = new EncryptedStream(pipe.Reader.AsStream(), receiver, leaveInnerOpen: true);

        var writer = Task.Run(async () =>
        {
            var random = new Random(1);
            int offset = 0;
            while (offset < payload.Length)
            {
                int take = Math.Min(random.Next(1, 40_000), payload.Length - offset);
                await writeSide.WriteAsync(payload.AsMemory(offset, take));
                offset += take;
            }

            await writeSide.FlushAsync();
            pipe.Writer.Complete();
        });

        var received = new byte[payload.Length];
        int total = 0;
        var readRandom = new Random(2);
        while (total < received.Length)
        {
            int want = Math.Min(readRandom.Next(1, 9_000), received.Length - total);
            int read = await readSide.ReadAsync(received.AsMemory(total, want));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        await writer;

        Assert.Equal(payload.Length, total);
        Assert.True(payload.AsSpan().SequenceEqual(received), "The decrypted stream does not match what was sent.");
    }
}
