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




