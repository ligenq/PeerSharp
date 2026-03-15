using PeerSharp.Internals.Bandwidth;

namespace PeerSharp.PieceWriter;

internal sealed class DiskBandwidthLimiter : IBandwidthUser
{
    private const int DefaultChunkSize = 256 * 1024;
    private readonly IBandwidthManager _bandwidth;
    private readonly string[] _readChannels;
    private readonly string[] _writeChannels;
    private readonly string _torrentReadChannel;
    private readonly string _torrentWriteChannel;

    public DiskBandwidthLimiter(IBandwidthManager bandwidth, string torrentHash)
    {
        _bandwidth = bandwidth;
        _torrentReadChannel = $"{torrentHash}_DR";
        _torrentWriteChannel = $"{torrentHash}_DW";
        _readChannels = new[] { BandwidthManager.GlobalDiskRead, _torrentReadChannel };
        _writeChannels = new[] { BandwidthManager.GlobalDiskWrite, _torrentWriteChannel };
    }

    public string Name => "DiskBandwidthLimiter";

    public static int MaxChunkBytes => DefaultChunkSize;

    public void AssignBandwidth(int amount)
    {
        // Not used by BandwidthManager; required by IBandwidthUser.
    }

    public Task<int> RequestReadAsync(int amount, CancellationToken ct)
    {
        if (!IsReadLimited())
        {
            return Task.FromResult(amount);
        }
        return _bandwidth.RequestBandwidthAsync(this, amount, priority: 0, _readChannels, ct);
    }

    public Task<int> RequestWriteAsync(int amount, CancellationToken ct)
    {
        if (!IsWriteLimited())
        {
            return Task.FromResult(amount);
        }
        return _bandwidth.RequestBandwidthAsync(this, amount, priority: 0, _writeChannels, ct);
    }

    public void ReturnRead(int amount)
    {
        _bandwidth.ReturnBandwidth(amount, _readChannels);
    }

    public void ReturnWrite(int amount)
    {
        _bandwidth.ReturnBandwidth(amount, _writeChannels);
    }

    private bool IsReadLimited()
    {
        return _bandwidth.GetChannel(BandwidthManager.GlobalDiskRead).GetLimit() > 0 ||
               _bandwidth.GetChannel(_torrentReadChannel).GetLimit() > 0;
    }

    private bool IsWriteLimited()
    {
        return _bandwidth.GetChannel(BandwidthManager.GlobalDiskWrite).GetLimit() > 0 ||
               _bandwidth.GetChannel(_torrentWriteChannel).GetLimit() > 0;
    }
}
