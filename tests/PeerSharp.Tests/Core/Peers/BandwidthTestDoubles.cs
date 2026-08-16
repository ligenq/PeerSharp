using PeerSharp.Internals.Bandwidth;

namespace PeerSharp.Tests.Core.Peers;

/// <summary>
/// Bandwidth test doubles shared by the encryption and rate limiting stream tests.
/// </summary>
internal static class BandwidthTestDoubles
{
    public const string DownloadChannel = "download";
    public const string UploadChannel = "upload";

    internal sealed class TestBandwidthUser : IBandwidthUser
    {
        public string Name => "test";

        public void AssignBandwidth(int amount)
        {
        }
    }

    internal sealed class TestBandwidthManager : IBandwidthManager
    {
        public int ReturnedDownload { get; private set; }
        public int ReturnedUpload { get; private set; }

        /// <summary>Bytes granted per request, or null to grant whatever was asked for.</summary>
        public int? GrantAmount { get; set; }

        public void Configure(int updateIntervalMs)
        {
        }

        public BandwidthChannel GetChannel(string name)
        {
            return new BandwidthChannel(TimeProvider.System);
        }

        public (long DownloadLimit, long UploadLimit) GetTorrentLimits(ITorrent torrent)
        {
            return (0, 0);
        }

        public (int ReadLimit, int WriteLimit) GetTorrentDiskLimits(ITorrent torrent)
        {
            return (0, 0);
        }

        public Task<int> RequestBandwidthAsync(IBandwidthUser user, int amount, int priority, string[] channelNames, CancellationToken ct = default)
        {
            return Task.FromResult(GrantAmount ?? amount);
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

        public void SetGlobalLimits(long downloadLimit, long uploadLimit)
        {
        }

        public void SetGlobalDiskLimits(int readLimit, int writeLimit)
        {
        }

        public void SetTorrentLimits(ITorrent torrent, long downloadLimit, long uploadLimit)
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
