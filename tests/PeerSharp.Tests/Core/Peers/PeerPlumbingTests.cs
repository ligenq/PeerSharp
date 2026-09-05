using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Internals.Extensions;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;
using PeerSharp.Messages;
using System.Text;
using System.Threading.Channels;

namespace PeerSharp.Tests.Core.Peers;

public class MessageQueueTests
{
    [Fact]
    public void MessagesComeBackInTheOrderTheyWentIn()
    {
        var queue = new MessageQueue(8);

        Assert.True(queue.TryEnqueue(new PeerMessage(MessageId.Choke)));
        Assert.True(queue.TryEnqueue(new PeerMessage(MessageId.Unchoke)));
        Assert.Equal(2, queue.Count);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(MessageId.Choke, first.Id);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(MessageId.Unchoke, second.Id);

        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void TryEnqueue_RefusesRatherThanBlockingWhenTheQueueIsFull()
    {
        var queue = new MessageQueue(1);

        Assert.True(queue.TryEnqueue(new PeerMessage(MessageId.Choke)));
        Assert.False(queue.TryEnqueue(new PeerMessage(MessageId.Unchoke)));
    }

    [Fact]
    public void IsCompleted_TellsAClosedQueueApartFromAFullOne()
    {
        // TryEnqueue returns false for both, so without this flag the only way to distinguish them
        // was to call EnqueueAsync and let it throw - one exception per message still in flight
        // when a peer disconnects, which is the ordinary case rather than an exceptional one.
        var full = new MessageQueue(1);
        full.TryEnqueue(new PeerMessage(MessageId.Choke));
        Assert.False(full.TryEnqueue(new PeerMessage(MessageId.Unchoke)));
        Assert.False(full.IsCompleted);

        var closed = new MessageQueue(8);
        closed.TryComplete();
        Assert.False(closed.TryEnqueue(new PeerMessage(MessageId.Choke)));
        Assert.True(closed.IsCompleted);
    }

    [Fact]
    public async Task EnqueueAsync_WaitsForRoomAndThenSucceeds()
    {
        var queue = new MessageQueue(1);
        queue.TryEnqueue(new PeerMessage(MessageId.Choke));

        var pending = queue.EnqueueAsync(new PeerMessage(MessageId.Unchoke), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);

        Assert.True(queue.TryDequeue(out _));
        await pending;

        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task EnqueueAsync_IsCancellable()
    {
        var queue = new MessageQueue(1);
        queue.TryEnqueue(new PeerMessage(MessageId.Choke));
        using var cts = new CancellationTokenSource();

        var pending = queue.EnqueueAsync(new PeerMessage(MessageId.Unchoke), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task WaitToReadAsync_CompletesWhenAMessageArrivesAndReportsFalseOnceClosedAndDrained()
    {
        var queue = new MessageQueue(8);

        var waiting = queue.WaitToReadAsync(TestContext.Current.CancellationToken);
        queue.TryEnqueue(new PeerMessage(MessageId.Choke));
        Assert.True(await waiting);

        Assert.True(queue.TryDequeue(out _));
        queue.TryComplete();

        Assert.False(await queue.WaitToReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClosingTheQueueStillLetsWhatIsAlreadyInItBeRead()
    {
        // The send loop drains after completion, so messages queued before the close must survive
        // it rather than being discarded with the writer.
        var queue = new MessageQueue(8);
        queue.TryEnqueue(new PeerMessage(MessageId.Choke));

        queue.TryComplete();

        Assert.True(await queue.WaitToReadAsync(TestContext.Current.CancellationToken));
        Assert.True(queue.TryDequeue(out var message));
        Assert.Equal(MessageId.Choke, message.Id);
    }

    [Fact]
    public void TryComplete_IsIdempotent()
    {
        var queue = new MessageQueue(4);

        queue.TryComplete();
        queue.TryComplete();

        Assert.True(queue.IsCompleted);
    }

    [Fact]
    public async Task EnqueueAsync_ThrowsOnceTheQueueIsClosed()
    {
        var queue = new MessageQueue(4);
        queue.TryComplete();

        await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await queue.EnqueueAsync(new PeerMessage(MessageId.Choke), TestContext.Current.CancellationToken));
    }
}

public class PrefixedStreamTests
{
    [Fact]
    public async Task ThePrefixIsServedBeforeAnythingFromTheInnerStream()
    {
        // The bytes that came along with the handshake have already left the socket and cannot be
        // put back, so the message reader has to see them first or it starts mid-message.
        using var inner = new MemoryStream("world"u8.ToArray());
        await using var stream = new PrefixedStream("hello "u8.ToArray(), inner);

        byte[] all = new byte[11];
        int read = 0;
        while (read < all.Length)
        {
            int n = await stream.ReadAsync(all.AsMemory(read), TestContext.Current.CancellationToken);
            if (n == 0) break;
            read += n;
        }

        Assert.Equal("hello world", Encoding.ASCII.GetString(all, 0, read));
    }

    [Fact]
    public async Task ThePrefixIsServedOnItsOwnRatherThanToppedUpFromTheSocket()
    {
        // A short read is legitimate. Waiting to fill the buffer would stall a peer that is itself
        // waiting on us, which is a deadlock rather than a slow read.
        using var inner = new MemoryStream("world"u8.ToArray());
        await using var stream = new PrefixedStream("hi"u8.ToArray(), inner);

        byte[] buffer = new byte[100];
        int read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(2, read);
        Assert.Equal("hi", Encoding.ASCII.GetString(buffer, 0, 2));
    }

    [Fact]
    public async Task ThePrefixIsConsumedAcrossSeveralSmallReads()
    {
        using var inner = new MemoryStream("Z"u8.ToArray());
        await using var stream = new PrefixedStream("abc"u8.ToArray(), inner);

        byte[] one = new byte[1];
        var seen = new StringBuilder();
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(1, await stream.ReadAsync(one, TestContext.Current.CancellationToken));
            seen.Append((char)one[0]);
        }

        Assert.Equal("abcZ", seen.ToString());
    }

    [Fact]
    public void TheSynchronousReadPathFollowsTheSamePrefixThenInnerOrder()
    {
        using var inner = new MemoryStream("world"u8.ToArray());
        using var stream = new PrefixedStream("hi "u8.ToArray(), inner);

        byte[] buffer = new byte[16];
        int first = stream.Read(buffer, 0, buffer.Length);
        int second = stream.Read(buffer, first, buffer.Length - first);

        Assert.Equal("hi world", Encoding.ASCII.GetString(buffer, 0, first + second));
    }

    [Fact]
    public async Task AnEmptyPrefixReadsStraightThrough()
    {
        using var inner = new MemoryStream("data"u8.ToArray());
        await using var stream = new PrefixedStream(ReadOnlyMemory<byte>.Empty, inner);

        byte[] buffer = new byte[16];
        int read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal("data", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task WritesFlushesAndCapabilitiesGoToTheInnerStream()
    {
        using var inner = new MemoryStream();
        await using var stream = new PrefixedStream("prefix"u8.ToArray(), inner, leaveInnerOpen: true);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.True(stream.CanWrite);

        stream.Write("sync"u8.ToArray(), 0, 4);
        await stream.WriteAsync("async"u8.ToArray(), TestContext.Current.CancellationToken);
        stream.Flush();
        await stream.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal("syncasync", Encoding.ASCII.GetString(inner.ToArray()));
    }

    [Fact]
    public void TheUnsupportedStreamOperationsSaySoRatherThanMisbehaving()
    {
        using var inner = new MemoryStream();
        using var stream = new PrefixedStream("x"u8.ToArray(), inner, leaveInnerOpen: true);

        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact]
    public void DisposeClosesTheInnerStreamOnlyWhenItOwnsIt()
    {
        // PeerCommunication owns the connection stream and closes it to end the connection, so a
        // wrapper disposing it too would close a socket its owner still expects to hold.
        var owned = new MemoryStream();
        new PrefixedStream(ReadOnlyMemory<byte>.Empty, owned).Dispose();
        Assert.Throws<ObjectDisposedException>(() => owned.ReadByte());

        var borrowed = new MemoryStream("x"u8.ToArray());
        new PrefixedStream(ReadOnlyMemory<byte>.Empty, borrowed, leaveInnerOpen: true).Dispose();
        Assert.Equal((byte)'x', borrowed.ReadByte());
    }

    [Fact]
    public async Task DisposeAsyncFollowsTheSameOwnershipRuleAndIsRepeatable()
    {
        var owned = new MemoryStream();
        var stream = new PrefixedStream(ReadOnlyMemory<byte>.Empty, owned);

        await stream.DisposeAsync();
        await stream.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => owned.ReadByte());
    }

    [Fact]
    public async Task ReadAsyncOnTheByteArrayOverloadHonoursOffsetAndCount()
    {
        using var inner = new MemoryStream("456"u8.ToArray());
        await using var stream = new PrefixedStream("123"u8.ToArray(), inner);

        byte[] buffer = new byte[10];
        int read = await stream.ReadAsync(buffer, 2, 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, read);
        Assert.Equal("123", Encoding.ASCII.GetString(buffer, 2, 3));
        Assert.Equal(0, buffer[0]);
    }
}

public class PeerCommunicationFactoryTests
{
    [Fact]
    public void CreateProducesAFreshPeerBoundToTheTorrentAndListenerItWasGiven()
    {
        // Reference equality matters downstream: PeerManager registers the instance it gets back
        // and relies on a new one per dial to key its connecting and connected sets.
        var torrent = TorrentTestUtility.CreateMinimal();
        var listener = new NullPeerListener();
        var factory = new PeerCommunicationFactory(NullLoggerFactory.Instance);

        var first = factory.Create(torrent, listener, TimeProvider.System);
        var second = factory.Create(torrent, listener, TimeProvider.System);

        Assert.NotNull(first);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void TheParameterlessConstructorLogsNowhereRatherThanThrowing()
    {
        var torrent = TorrentTestUtility.CreateMinimal();

        Assert.NotNull(new PeerCommunicationFactory().Create(torrent, new NullPeerListener(), TimeProvider.System));
    }

    private sealed class NullPeerListener : IPeerListener
    {
        public Task HandshakeFinishedAsync(IPeerCommunication peer) => Task.CompletedTask;
        public Task ConnectionClosedAsync(IPeerCommunication peer, int code) => Task.CompletedTask;
        public Task MessageReceivedAsync(IPeerCommunication peer, PeerMessage msg) => Task.CompletedTask;
        public Task ExtendedHandshakeFinishedAsync(IPeerCommunication peer, ExtensionHandshake handshake) => Task.CompletedTask;
        public Task ExtendedMessageReceivedAsync(IPeerCommunication peer, int type, byte[] data) => Task.CompletedTask;
        public Task PexReceivedAsync(IPeerCommunication peer, List<System.Net.IPEndPoint> added, List<byte> addedFlags, List<System.Net.IPEndPoint> dropped) => Task.CompletedTask;
        public Task HolepunchMessageReceivedAsync(IPeerCommunication peer, UtHolepunch.MsgId id, System.Net.IPEndPoint endpoint, UtHolepunch.ErrorCode error) => Task.CompletedTask;
        public Task PortReceivedAsync(IPeerCommunication peer, ushort dhtPort) => Task.CompletedTask;
    }
}

public class RC4Tests
{
    private static readonly byte[] Key = "PeerSharp test key"u8.ToArray();

    [Fact]
    public void EncryptingThenDecryptingWithTheSameKeystreamReturnsThePlaintext()
    {
        byte[] plaintext = "the quick brown fox"u8.ToArray();
        byte[] buffer = (byte[])plaintext.Clone();

        var encryptor = new RC4();
        encryptor.Init(Key);
        encryptor.Encrypt(buffer);
        Assert.NotEqual(plaintext, buffer);

        var decryptor = new RC4();
        decryptor.Init(Key);
        decryptor.Decrypt(buffer);
        Assert.Equal(plaintext, buffer);
    }

    [Fact]
    public void EncryptAndDecryptAreTheSameOperation()
    {
        // RC4 is a stream cipher: both directions XOR against the keystream, and the two method
        // names exist only to read correctly at the call site.
        byte[] a = "payload"u8.ToArray();
        byte[] b = "payload"u8.ToArray();

        var first = new RC4();
        first.Init(Key);
        first.Encrypt(a);

        var second = new RC4();
        second.Init(Key);
        second.Decrypt(b);

        Assert.Equal(a, b);
    }

    [Fact]
    public void TheKeystreamAdvancesSoRepeatedBlocksDoNotEncryptAlike()
    {
        byte[] buffer = new byte[32];
        var cipher = new RC4();
        cipher.Init(Key);
        cipher.Encrypt(buffer);

        Assert.NotEqual(buffer[..16], buffer[16..]);
    }

    [Fact]
    public void TheOffsetAndCountOverloadTouchesOnlyItsSlice()
    {
        byte[] buffer = new byte[8];
        var cipher = new RC4();
        cipher.Init(Key);

        cipher.Encrypt(buffer, 2, 4);

        Assert.Equal(0, buffer[0]);
        Assert.Equal(0, buffer[1]);
        Assert.Equal(0, buffer[6]);
        Assert.Equal(0, buffer[7]);
        Assert.Contains(buffer[2..6], b => b != 0);
    }

    [Fact]
    public void Clone_CopiesTheKeystreamPositionSoBothContinueIdentically()
    {
        // The MSE handshake clones a keyed cipher after discarding its first bytes, so a clone that
        // restarted the keystream would decrypt everything after the handshake to noise.
        var original = new RC4();
        original.Init(Key);
        original.Encrypt(new byte[100]); // advance well past the start

        var clone = original.Clone();

        byte[] fromOriginal = new byte[16];
        byte[] fromClone = new byte[16];
        original.Encrypt(fromOriginal);
        clone.Encrypt(fromClone);

        Assert.Equal(fromOriginal, fromClone);
    }

    [Fact]
    public void Clone_IsIndependentOfTheOriginalAfterwards()
    {
        var original = new RC4();
        original.Init(Key);
        var clone = original.Clone();

        original.Encrypt(new byte[64]);

        byte[] fresh = new byte[16];
        byte[] cloned = new byte[16];
        var reference = new RC4();
        reference.Init(Key);
        reference.Encrypt(fresh);
        clone.Encrypt(cloned);

        Assert.Equal(fresh, cloned);
    }

    [Fact]
    public void MatchesTheRc4TestVectorForKeyAndPlaintextBothSpellingKey()
    {
        // From the RC4 test vectors: key "Key", plaintext "Plaintext" gives BBF316E8D940AF0AD3.
        var cipher = new RC4();
        cipher.Init("Key"u8);
        byte[] buffer = "Plaintext"u8.ToArray();
        cipher.Encrypt(buffer);

        Assert.Equal("BBF316E8D940AF0AD3", Convert.ToHexString(buffer));
    }
}

public class DiffieHellmanTests
{
    [Fact]
    public void TwoPartiesArriveAtTheSameSharedSecret()
    {
        // The property MSE rests on: neither side transmits the secret, and both compute the same
        // one from the other's public key.
        var alice = new DiffieHellman();
        var bob = new DiffieHellman();

        alice.ComputeSharedSecret(bob.GetPublicKeyBytes());
        bob.ComputeSharedSecret(alice.GetPublicKeyBytes());

        Assert.Equal(alice.GetSharedSecretBytes(), bob.GetSharedSecretBytes());
        Assert.Equal(alice.SharedSecret, bob.SharedSecret);
    }

    [Fact]
    public void ThePublicKeyIsTheHundredAndNinetySixByteValueMseSpecifies()
    {
        var exchange = new DiffieHellman();

        Assert.Equal(96, exchange.GetPublicKeyBytes().Length);
        Assert.NotEqual(System.Numerics.BigInteger.Zero, exchange.PublicKey);
    }

    [Fact]
    public void EachExchangeUsesItsOwnPrivateKey()
    {
        // A fixed private key would make every connection's keystream identical and the encryption
        // pointless, so this is worth asserting rather than assuming.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 10; i++)
        {
            keys.Add(Convert.ToHexString(new DiffieHellman().GetPublicKeyBytes()));
        }

        Assert.Equal(10, keys.Count);
    }

    [Fact]
    public void ASharedSecretIsNinetySixBytesAndDiffersBetweenPairings()
    {
        var alice = new DiffieHellman();
        var first = new DiffieHellman();
        var second = new DiffieHellman();

        alice.ComputeSharedSecret(first.GetPublicKeyBytes());
        byte[] withFirst = alice.GetSharedSecretBytes();

        alice.ComputeSharedSecret(second.GetPublicKeyBytes());
        byte[] withSecond = alice.GetSharedSecretBytes();

        Assert.Equal(96, withFirst.Length);
        Assert.Equal(96, withSecond.Length);
        Assert.NotEqual(withFirst, withSecond);
    }
}

public class ProtocolEncryptionHandshakeTests
{
    private static readonly byte[] InfoHash = [.. Enumerable.Range(0, 20).Select(i => (byte)i)];

    [Fact]
    public void AnInitiatorProducesAnOpeningMessageThatIsNotTheRawKey()
    {
        // MSE pads the public key with a random amount so the opening bytes have no fixed length or
        // signature for a middlebox to match on. A bare 96-byte key would defeat the point.
        using var handshake = new ProtocolEncryptionHandshake(InfoHash, initiator: true);

        byte[] opening = handshake.Initiate();

        Assert.True(opening.Length >= 96);
        Assert.False(handshake.IsComplete);
        Assert.False(handshake.IsError);
    }

    [Fact]
    public void TwoEndsThatKnowTheSameInfoHashReachAnEstablishedEncryption()
    {
        using var initiator = new ProtocolEncryptionHandshake(InfoHash, initiator: true);
        using var receiver = new ProtocolEncryptionHandshake(InfoHash, initiator: false);

        initiator.InitialPayload = "BT handshake"u8.ToArray();

        byte[] toReceiver = initiator.Initiate();
        byte[] toInitiator = receiver.HandleIncoming(toReceiver);
        while (!initiator.IsComplete && !initiator.IsError && toInitiator.Length > 0)
        {
            toReceiver = initiator.HandleIncoming(toInitiator);
            if (initiator.IsComplete || receiver.IsComplete || toReceiver.Length == 0) break;
            toInitiator = receiver.HandleIncoming(toReceiver);
        }

        Assert.False(initiator.IsError);
        Assert.False(receiver.IsError);
        Assert.NotNull(receiver.MatchedInfoHash);
        Assert.Equal(InfoHash, receiver.MatchedInfoHash);
    }

    [Fact]
    public void GarbageInsteadOfAHandshakeIsRejectedRatherThanAccepted()
    {
        // A connection that is not MSE at all reaches this code, so failing closed is the contract.
        using var handshake = new ProtocolEncryptionHandshake(InfoHash, initiator: false);

        handshake.HandleIncoming(new byte[600]);

        Assert.False(handshake.IsComplete);
    }

    [Fact]
    public void TrailingDataIsEmptyBeforeAnythingHasArrived()
    {
        using var handshake = new ProtocolEncryptionHandshake(InfoHash, initiator: true);

        Assert.Empty(handshake.TrailingData);
        Assert.Null(handshake.Encryption);
        Assert.Null(handshake.ReceivedPayload);
    }

    [Fact]
    public void UsingADisposedHandshakeSaysSoRatherThanReadingAFreedBuffer()
    {
        var handshake = new ProtocolEncryptionHandshake(InfoHash, initiator: true);
        handshake.Dispose();
        handshake.Dispose();

        Assert.Throws<ObjectDisposedException>(() => handshake.HandleIncoming([1, 2, 3]));
    }
}
