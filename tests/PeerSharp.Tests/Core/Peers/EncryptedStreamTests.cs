using PeerSharp.Internals;
using PeerSharp.Internals.Bandwidth;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Tests.Core.Peers;

public class EncryptedStreamTests
{
    private const string DownloadChannel = "download";
    private const string UploadChannel = "upload";

    [Fact]
    public async Task ReadAsync_DecryptsAndReturnsReservedBandwidth()
    {
        byte[] key = { 1, 2, 3, 4, 5, 6, 7, 8 };
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

        var manager = new TestBandwidthManager();
        var user = new TestBandwidthUser();

        await using var stream = new EncryptedStream(
            inner,
            pe,
            user,
            manager,
            new[] { DownloadChannel },
            new[] { UploadChannel },
            leaveInnerOpen: true);

        byte[] buffer = new byte[plain.Length];
        int read = await stream.ReadAsync(buffer, CancellationToken.None);

        Assert.Equal(plain.Length, read);
        Assert.Equal(plain, buffer);

        stream.Dispose();

        int expectedReturned = ProtocolConstants.DownloadBatchSize - read;
        Assert.Equal(expectedReturned, manager.ReturnedDownload);
        Assert.Equal(0, manager.ReturnedUpload);
    }

    [Fact]
    public async Task WriteAsync_EncryptsAndReturnsReservedBandwidth()
    {
        byte[] key = { 9, 8, 7, 6, 5, 4, 3, 2 };
        byte[] plain = new byte[100];
        Random.Shared.NextBytes(plain);

        await using var inner = new MemoryStream();
        var pe = new ProtocolEncryption();
        pe.RC4Out.Init(key);

        var manager = new TestBandwidthManager();
        var user = new TestBandwidthUser();

        await using var stream = new EncryptedStream(
            inner,
            pe,
            user,
            manager,
            new[] { DownloadChannel },
            new[] { UploadChannel },
            leaveInnerOpen: true);

        await stream.WriteAsync(plain, 0, plain.Length, CancellationToken.None);

        byte[] cipher = inner.ToArray();
        Assert.False(plain.SequenceEqual(cipher));

        var decryptor = new RC4();
        decryptor.Init(key);
        decryptor.Decrypt(cipher);

        Assert.Equal(plain, cipher);

        stream.Dispose();

        int expectedReturned = ProtocolConstants.UploadBatchSize - plain.Length;
        Assert.Equal(expectedReturned, manager.ReturnedUpload);
        Assert.Equal(0, manager.ReturnedDownload);
    }

    [Fact]
    public async Task ReadAsync_ByteArrayOverload_DecryptsData()
    {
        byte[] key = { 3, 1, 4, 1, 5, 9, 2, 6 };
        byte[] plain = new byte[32];
        Random.Shared.NextBytes(plain);

        byte[] cipher = plain.ToArray();
        var encryptor = new RC4();
        encryptor.Init(key);
        encryptor.Encrypt(cipher);

        await using var inner = new MemoryStream(cipher);
        var pe = new ProtocolEncryption();
        pe.RC4In.Init(key);

        var manager = new TestBandwidthManager();
        var user = new TestBandwidthUser();

        await using var stream = new EncryptedStream(
            inner, pe, user, manager,
            new[] { DownloadChannel }, new[] { UploadChannel },
            leaveInnerOpen: true);

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
        var manager = new TestBandwidthManager();
        var user = new TestBandwidthUser();

        var stream = new EncryptedStream(
            inner, pe, user, manager,
            new[] { DownloadChannel }, new[] { UploadChannel },
            leaveInnerOpen: false);

        stream.Dispose();

        // Inner was closed: reading after dispose should throw
        Assert.Throws<ObjectDisposedException>(() => inner.ReadByte());
    }

    [Fact]
    public void Properties_ReturnExpectedValues()
    {
        var inner = new MemoryStream(new byte[64], writable: true);
        var pe = new ProtocolEncryption();
        var manager = new TestBandwidthManager();
        var user = new TestBandwidthUser();

        using var stream = new EncryptedStream(
            inner, pe, user, manager,
            new[] { DownloadChannel }, new[] { UploadChannel },
            leaveInnerOpen: true);

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
        var manager = new TestBandwidthManager();
        var user = new TestBandwidthUser();

        using var stream = new EncryptedStream(
            inner, pe, user, manager,
            new[] { DownloadChannel }, new[] { UploadChannel },
            leaveInnerOpen: true);

        Assert.Throws<NotSupportedException>(() => { stream.Position = 0; });
    }

    [Fact]
    public void Flush_DoesNotThrow()
    {
        var inner = new MemoryStream();
        var pe = new ProtocolEncryption();
        var manager = new TestBandwidthManager();
        var user = new TestBandwidthUser();

        using var stream = new EncryptedStream(
            inner, pe, user, manager,
            new[] { DownloadChannel }, new[] { UploadChannel },
            leaveInnerOpen: true);

        stream.Flush(); // must not throw
    }

    [Fact]
    public void Read_Sync_DecryptsData()
    {
        byte[] key = { 5, 6, 7, 8, 1, 2, 3, 4 };
        byte[] plain = new byte[64];
        Random.Shared.NextBytes(plain);

        byte[] cipher = plain.ToArray();
        var encryptor = new RC4();
        encryptor.Init(key);
        encryptor.Encrypt(cipher);

        var inner = new MemoryStream(cipher);
        var pe = new ProtocolEncryption();
        pe.RC4In.Init(key);

        var manager = new TestBandwidthManager();
        var user = new TestBandwidthUser();

        using var stream = new EncryptedStream(
            inner, pe, user, manager,
            new[] { DownloadChannel }, new[] { UploadChannel },
            leaveInnerOpen: true);

        byte[] buffer = new byte[plain.Length];
        int read = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(plain.Length, read);
        Assert.Equal(plain, buffer);
    }

    [Fact]
    public void Write_Sync_EncryptsData()
    {
        byte[] key = { 11, 22, 33, 44, 55, 66, 77, 88 };
        byte[] plain = new byte[64];
        Random.Shared.NextBytes(plain);

        var inner = new MemoryStream();
        var pe = new ProtocolEncryption();
        pe.RC4Out.Init(key);

        var manager = new TestBandwidthManager();
        var user = new TestBandwidthUser();

        using var stream = new EncryptedStream(
            inner, pe, user, manager,
            new[] { DownloadChannel }, new[] { UploadChannel },
            leaveInnerOpen: true);

        stream.Write(plain, 0, plain.Length);

        byte[] cipher = inner.ToArray();
        Assert.False(plain.SequenceEqual(cipher));

        var decryptor = new RC4();
        decryptor.Init(key);
        decryptor.Decrypt(cipher);

        Assert.Equal(plain, cipher);
    }

    private sealed class TestBandwidthUser : IBandwidthUser
    {
        public string Name => "test";

        public void AssignBandwidth(int amount)
        {
        }
    }

    private sealed class TestBandwidthManager : IBandwidthManager
    {
        public int ReturnedDownload { get; private set; }
        public int ReturnedUpload { get; private set; }

        public void Configure(int updateIntervalMs)
        {
        }

        public BandwidthChannel GetChannel(string name)
        {
            return new BandwidthChannel(TimeProvider.System);
        }

        public (int DownloadLimit, int UploadLimit) GetTorrentLimits(ITorrent torrent)
        {
            return (0, 0);
        }

        public (int ReadLimit, int WriteLimit) GetTorrentDiskLimits(ITorrent torrent)
        {
            return (0, 0);
        }

        public Task<int> RequestBandwidthAsync(IBandwidthUser user, int amount, int priority, string[] channelNames, CancellationToken ct = default)
        {
            return Task.FromResult(amount);
        }

        public void ReturnBandwidth(int amount, string[] channelNames)
        {
            if (Array.Exists(channelNames, name => name == DownloadChannel))
            {
                ReturnedDownload += amount;
            }
            if (Array.Exists(channelNames, name => name == UploadChannel))
            {
                ReturnedUpload += amount;
            }
        }

        public void RemoveTorrentChannels(ITorrent torrent) { }

        public void SetGlobalLimits(int downloadLimit, int uploadLimit)
        {
        }

        public void SetGlobalDiskLimits(int readLimit, int writeLimit)
        {
        }

        public void SetTorrentLimits(ITorrent torrent, int downloadLimit, int uploadLimit)
        {
        }

        public void SetTorrentDiskLimits(ITorrent torrent, int readLimit, int writeLimit)
        {
        }

        public void Start()
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}




