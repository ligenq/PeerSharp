using System.Net.Sockets;

namespace PeerSharp.Tests.Integration.Synthetic;

/// <summary>Checks the synthetic peer's observations against bytes chosen independently of the engine.</summary>
[Collection("Integration")]
public class SyntheticPeerHandshakeTests
{
    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEncryptedOpeningCanStartWithThePlaintextLength(bool hangUp)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions { HangUpDuringHandshake = hangUp });
        using var client = new TcpClient();
        await client.ConnectAsync(peer.EndPoint, ct);
        using var stream = client.GetStream();

        // A DH key can start with 19, and even with part of the protocol name. The final byte
        // deliberately differs so checking only the length or a shorter prefix cannot pass.
        byte[] opening = [19, .. "BitTorrent protocoX"u8.ToArray()];
        await stream.WriteAsync(opening, ct);
        if (!hangUp)
        {
            client.Client.Shutdown(SocketShutdown.Send);
        }

        var connection = await peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(5), ct);
        Assert.True(await SyntheticPeer.WaitForAsync(() => connection.IsClosed, TimeSpan.FromSeconds(5), ct));
        Assert.Equal(HandshakeOpening.Encrypted, connection.Opening);
        Assert.Null(connection.Reserved);
    }

    [Theory(Timeout = 30000)]
    [InlineData(1)]
    [InlineData(19)]
    public async Task AnIncompletePlaintextPrefix_RemainsUnclassified(int length)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions());
        using var client = new TcpClient();
        await client.ConnectAsync(peer.EndPoint, ct);
        using var stream = client.GetStream();
        byte[] prefix = [19, .. "BitTorrent protocol"u8.ToArray()];

        await stream.WriteAsync(prefix.AsMemory(0, length), ct);
        client.Client.Shutdown(SocketShutdown.Send);

        var connection = await peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(5), ct);
        Assert.True(await SyntheticPeer.WaitForAsync(() => connection.IsClosed, TimeSpan.FromSeconds(5), ct));
        Assert.Null(connection.Opening);
    }

    [Theory(Timeout = 30000)]
    [InlineData(0)]
    [InlineData(18)]
    [InlineData(20)]
    [InlineData(255)]
    public async Task ADifferentFirstByte_IsEnoughToIdentifyEncryption(byte first)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions { HangUpDuringHandshake = true });
        using var client = new TcpClient();
        await client.ConnectAsync(peer.EndPoint, ct);
        using var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { first }, ct);

        var connection = await peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(5), ct);
        Assert.True(await SyntheticPeer.WaitForAsync(() => connection.IsClosed, TimeSpan.FromSeconds(5), ct));
        Assert.Equal(HandshakeOpening.Encrypted, connection.Opening);
    }

    [Fact(Timeout = 30000)]
    public async Task AFragmentedPlaintextHandshake_PreservesReservedBytesAndInfoHash()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var peer = SyntheticPeer.Start(new SyntheticPeerOptions { AdvertiseExtensionProtocol = false });
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(peer.EndPoint, ct);
        using var stream = client.GetStream();
        byte[] handshake = Enumerable.Range(0, 68).Select(i => (byte)i).ToArray();
        handshake[0] = 19;
        "BitTorrent protocol"u8.CopyTo(handshake.AsSpan(1));

        await stream.WriteAsync(handshake.AsMemory(0, 1), ct);
        await stream.WriteAsync(handshake.AsMemory(1, 18), ct);
        await stream.WriteAsync(handshake.AsMemory(19, 1), ct);
        await stream.WriteAsync(handshake.AsMemory(20), ct);

        byte[] response = new byte[68];
        await stream.ReadExactlyAsync(response, ct);
        var connection = await peer.WaitForConnectionAsync(0, TimeSpan.FromSeconds(5), ct);
        await connection.Ready.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Equal(HandshakeOpening.Plaintext, connection.Opening);
        Assert.Equal(handshake[20..28], connection.Reserved);
        Assert.Equal(handshake[28..48], response[28..48]);
        Assert.Equal(handshake[..20], response[..20]);
    }
}
