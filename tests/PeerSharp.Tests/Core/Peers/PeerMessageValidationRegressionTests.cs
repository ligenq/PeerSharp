using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Net;
using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Messages;
using TorrentFileEntry = PeerSharp.Internals.TorrentFileEntry;

namespace PeerSharp.Tests.Core.Peers;

public class PeerMessageValidationRegressionTests
{
    [Fact]
    public void AllowedFastSetMatchesTheBep6ExampleAndUsesTheSubnet()
    {
        byte[] hash = Enumerable.Repeat((byte)0xAA, 20).ToArray();
        var set = PeerManager.GenerateAllowedFastSet(IPAddress.Parse("80.4.4.200"), hash, 1313, 9);
        Assert.Equal(new[] { 1059, 431, 808, 1217, 287, 376, 1188, 353, 508 }, set);
        Assert.Equal(set, PeerManager.GenerateAllowedFastSet(IPAddress.Parse("80.4.4.1"), hash, 1313, 9));
        Assert.Equal(set, PeerManager.GenerateAllowedFastSet(IPAddress.Parse("::ffff:80.4.4.200"), hash, 1313, 9));
    }

    [Fact]
    public void SmallTorrentAllowedFastSetContainsEveryPieceOnce()
    {
        var set = PeerManager.GenerateAllowedFastSet(IPAddress.Loopback, new byte[20], 3, 10);
        Assert.Equal(new[] { 0, 1, 2 }, set.Order().ToArray());
        Assert.Empty(PeerManager.GenerateAllowedFastSet(IPAddress.IPv6Loopback, new byte[20], 3, 10));
    }

    [Fact]
    public async Task RemoteAllowedFastPermissionCannotAuthorizeAnUpload()
    {
        await using var torrent = CreateTorrent();
        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        typeof(PeerCommunication).GetProperty(nameof(peer.RemoteSupportsFastExtension))!.SetValue(peer, true);
        using var message = new PeerMessage(MessageId.AllowedFast) { PieceIndex = 0 };
        await Process(peer, message);
        Assert.True(peer.IsAllowedFast(0));
        Assert.False(peer.IsUploadAllowedFast(0));
        await peer.SendAllowedFastAsync(1);
        Assert.True(peer.IsUploadAllowedFast(1));
        Assert.False(peer.IsAllowedFast(1));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 6)]
    [InlineData(6, 14)]
    [InlineData(7, 9)]
    [InlineData(8, 14)]
    [InlineData(9, 4)]
    [InlineData(13, 6)]
    [InlineData(14, 2)]
    [InlineData(15, 2)]
    [InlineData(16, 14)]
    [InlineData(17, 6)]
    [InlineData(20, 1)]
    [InlineData(21, 50)]
    [InlineData(22, 50)]
    [InlineData(23, 50)]
    [InlineData(253, 1)]
    [InlineData(254, 1)]
    [InlineData(255, 1)]
    public void MalformedWireFramesAreRejectedBeforeAllocation(byte id, int length)
    {
        byte[] frame = new byte[length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, length);
        frame[4] = id;
        var buffer = new ReadOnlySequence<byte>(frame);
        Assert.Throws<InvalidDataException>(() => PeerProtocol.TryDecodeMessage(ref buffer, out _, out _));
        Assert.Equal(frame.Length, buffer.Length);
    }

    [Fact]
    public void DecoderHandlesEmptySequenceSegments()
    {
        var head = new Segment(new byte[] { 0, 0, 0, 5 });
        var empty = head.Append(ReadOnlyMemory<byte>.Empty);
        var tail = empty.Append(new byte[] { (byte)MessageId.Have, 0, 0, 0, 1 });
        var buffer = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        Assert.True(PeerProtocol.TryDecodeMessage(ref buffer, out var message, out int consumed));
        using (message)
        {
            Assert.Equal(MessageId.Have, message!.Id);
            Assert.Equal(1, message.HavePieceIndex);
            Assert.Equal(9, consumed);
            Assert.True(buffer.IsEmpty);
        }
    }

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public async Task FastMessagesRequireHandshakeNegotiation(byte id)
    {
        await using var torrent = CreateTorrent();
        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        using var message = new PeerMessage((MessageId)id);
        await Assert.ThrowsAsync<InvalidDataException>(() => Process(peer, message));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public async Task HaveCannotAdvertisePiecesOutsideTheTorrent(int index)
    {
        await using var torrent = CreateTorrent();
        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        using var message = new PeerMessage(MessageId.Have) { HavePieceIndex = index };
        await Assert.ThrowsAsync<InvalidDataException>(() => Process(peer, message));
        Assert.Equal(0, peer.PeerPieces.ReceivedCount);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 127)]
    public async Task BitfieldRequiresExactLengthAndZeroSpareBits(int length, byte lastByte)
    {
        await using var torrent = CreateTorrent();
        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        var bytes = new byte[length];
        bytes[^1] = lastByte;
        using var message = new PeerMessage(MessageId.Bitfield) { Data = bytes };
        await Assert.ThrowsAsync<InvalidDataException>(() => Process(peer, message));
        Assert.Equal(0, peer.PeerPieces.ReceivedCount);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    public async Task FastAvailabilityCannotReplaceAnEarlierAvailabilityReport(byte id)
    {
        await using var torrent = CreateTorrent();
        var peer = new PeerCommunication(torrent, new NullPeerListener(), TimeProvider.System);
        typeof(PeerCommunication).GetProperty(nameof(peer.RemoteSupportsFastExtension))!.SetValue(peer, true);
        using var bitfield = new PeerMessage(MessageId.Bitfield) { Data = [0x80, 0] };
        await Process(peer, bitfield);
        using var message = new PeerMessage((MessageId)id);
        await Assert.ThrowsAsync<InvalidDataException>(() => Process(peer, message));
        Assert.True(peer.PeerPieces.HasPiece(0));
        Assert.Equal(1, peer.PeerPieces.ReceivedCount);
    }

    internal static Task Process(PeerCommunication peer, PeerMessage message)
        => (Task)typeof(PeerCommunication).GetMethod("ProcessMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(peer, [message])!;

    private static Torrent CreateTorrent()
    {
        var metadata = new TorrentFileMetadata();
        metadata.Info.PieceSize = ProtocolConstants.BlockSize;
        metadata.Info.FullSize = 9L * ProtocolConstants.BlockSize;
        metadata.Info.Files.Add(new TorrentFileEntry { Path = "file.bin", Size = metadata.Info.FullSize });
        metadata.Info.Pieces.AddRange(Enumerable.Range(0, 9).Select(_ => new byte[20]));
        return TorrentTestUtility.CreateMinimal(metadata);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { Memory = memory, RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
