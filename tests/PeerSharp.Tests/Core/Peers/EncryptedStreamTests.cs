using PeerSharp.Internals;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core.Peers;

public class EncryptedStreamTests
{
    [Fact]
    public async Task ReadAsync_DecryptsData()
    {
        byte[] key = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] plain = new byte[100];
        for (int i = 0; i < plain.Length; i++)
        {
            plain[i] = (byte)i;
        }

        byte[] cipher = plain.ToArray();
        var encryptor = new RC4();
        encryptor.Init(key);
        encryptor.Encrypt(cipher);

        await using var inner = new MemoryStream(cipher);
        var pe = new ProtocolEncryption();
        pe.RC4In.Init(key);

        await using var stream = new EncryptedStream(inner, pe, leaveInnerOpen: true);

        byte[] buffer = new byte[plain.Length];
        int read = await stream.ReadAsync(buffer, CancellationToken.None);

        Assert.Equal(plain.Length, read);
        Assert.Equal(plain, buffer);

    }

    [Fact]
    public async Task WriteAsync_EncryptsData()
    {
        byte[] key = [9, 8, 7, 6, 5, 4, 3, 2];
        byte[] plain = new byte[100];
        Random.Shared.NextBytes(plain);

        await using var inner = new MemoryStream();
        var pe = new ProtocolEncryption();
        pe.RC4Out.Init(key);

        await using var stream = new EncryptedStream(inner, pe, leaveInnerOpen: true);

        await stream.WriteAsync(plain, 0, plain.Length, CancellationToken.None);

        byte[] cipher = inner.ToArray();
        Assert.False(plain.SequenceEqual(cipher));

        var decryptor = new RC4();
        decryptor.Init(key);
        decryptor.Decrypt(cipher);

        Assert.Equal(plain, cipher);

    }

    [Fact]
    public async Task ReadAsync_ByteArrayOverload_DecryptsData()
    {
        byte[] key = [3, 1, 4, 1, 5, 9, 2, 6];
        byte[] plain = new byte[32];
        Random.Shared.NextBytes(plain);

        byte[] cipher = plain.ToArray();
        var encryptor = new RC4();
        encryptor.Init(key);
        encryptor.Encrypt(cipher);

        await using var inner = new MemoryStream(cipher);
        var pe = new ProtocolEncryption();
        pe.RC4In.Init(key);

        await using var stream = new EncryptedStream(inner, pe, leaveInnerOpen: true);

        byte[] buffer = new byte[plain.Length];
        int read = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);

        Assert.Equal(plain.Length, read);
        Assert.Equal(plain, buffer);
    }

    [Fact]
    public void Dispose_LeaveInnerOpenFalse_DisposesInner()
    {
        var inner = new MemoryStream(new byte[16]);
        var pe = new ProtocolEncryption();
        var stream = new EncryptedStream(inner, pe, leaveInnerOpen: false);

        stream.Dispose();

        // Inner was closed: reading after dispose should throw
        Assert.Throws<ObjectDisposedException>(() => inner.ReadByte());
    }

    [Fact]
    public void Properties_ReturnExpectedValues()
    {
        var inner = new MemoryStream(new byte[64], writable: true);
        var pe = new ProtocolEncryption();
        using var stream = new EncryptedStream(inner, pe, leaveInnerOpen: true);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.True(stream.CanWrite);
        Assert.Equal(64, stream.Length);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Position_Setter_ThrowsNotSupported()
    {
        var inner = new MemoryStream(new byte[16]);
        var pe = new ProtocolEncryption();
        using var stream = new EncryptedStream(inner, pe, leaveInnerOpen: true);

        Assert.Throws<NotSupportedException>(() => { stream.Position = 0; });
    }

    [Fact]
    public void Flush_DoesNotThrow()
    {
        var inner = new MemoryStream();
        var pe = new ProtocolEncryption();
        using var stream = new EncryptedStream(inner, pe, leaveInnerOpen: true);

        stream.Flush(); // must not throw
    }

    [Fact]
    public void Read_Sync_DecryptsData()
    {
        byte[] key = [5, 6, 7, 8, 1, 2, 3, 4];
        byte[] plain = new byte[64];
        Random.Shared.NextBytes(plain);

        byte[] cipher = plain.ToArray();
        var encryptor = new RC4();
        encryptor.Init(key);
        encryptor.Encrypt(cipher);

        var inner = new MemoryStream(cipher);
        var pe = new ProtocolEncryption();
        pe.RC4In.Init(key);

        using var stream = new EncryptedStream(inner, pe, leaveInnerOpen: true);

        byte[] buffer = new byte[plain.Length];
        int read = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(plain.Length, read);
        Assert.Equal(plain, buffer);
    }

    [Fact]
    public void Write_Sync_EncryptsData()
    {
        byte[] key = [11, 22, 33, 44, 55, 66, 77, 88];
        byte[] plain = new byte[64];
        Random.Shared.NextBytes(plain);

        var inner = new MemoryStream();
        var pe = new ProtocolEncryption();
        pe.RC4Out.Init(key);

        using var stream = new EncryptedStream(inner, pe, leaveInnerOpen: true);

        stream.Write(plain, 0, plain.Length);

        byte[] cipher = inner.ToArray();
        Assert.False(plain.SequenceEqual(cipher));

        var decryptor = new RC4();
        decryptor.Init(key);
        decryptor.Decrypt(cipher);

        Assert.Equal(plain, cipher);
    }
}
