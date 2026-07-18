using PeerSharp.BEncoding;
using System.Net;
using PeerSharp.Messages;

namespace PeerSharp.Internals.Extensions;

internal class UtPex : IUtPex, IDisposable
{
    public const string Name = "ut_pex";

    private readonly IPeerCommunication _peer;

    private readonly HashSet<IPEndPoint> _sentPeers = [];

    private AtomicDisposal _disposal = new();

    public UtPex(IPeerCommunication peer)
    {
        _peer = peer;
    }

    [Flags]
    internal enum Peer : byte
    {
        Encryption = 0x1,
        Seed = 0x2,
        Utp = 0x4,
        Holepunch = 0x8
    }

    public int? LocalMessageId { get; private set; }
    public int? RemoteMessageId { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async Task HandleMessageAsync(byte[] data)
    {
        // Data starts after ExtId
        BDict? dict;
        try
        {
            dict = BencodeParser.Parse(data) as BDict;
        }
        catch (FormatException)
        {
            // Malformed PEX payload from a remote peer - ignore
            return;
        }

        if (dict is null)
        {
            return;
        }

        var added = new List<IPEndPoint>();
        var addedFlags = new List<byte>();
        var dropped = new List<IPEndPoint>();

        ParsePeersWithFlags(dict.GetBytes("added"), dict.GetBytes("added.f"), false, added, addedFlags);
        ParsePeersWithFlags(dict.GetBytes("added6"), dict.GetBytes("added6.f"), true, added, addedFlags);
        ParsePeers(dict.GetBytes("dropped"), false, dropped);
        ParsePeers(dict.GetBytes("dropped6"), true, dropped);

        if (added.Count > 0 || dropped.Count > 0)
        {
            // Notify listener
            await _peer.Listener.PexReceivedAsync(_peer, added, addedFlags, dropped).ConfigureAwait(false);
        }
    }

    public void Init(ExtensionHandshake handshake)
    {
        if (handshake.MessageIds.TryGetValue(Name, out int id))
        {
            RemoteMessageId = id;
        }
    }

    public void SetLocalMessageId(int id)
    {
        LocalMessageId = id;
    }

    public void SendPex(List<IPEndPoint> added, List<byte> addedFlags, List<IPEndPoint> dropped)
    {
        if (!RemoteMessageId.HasValue)
        {
            return;
        }

        var dict = new BDict();

        if (added?.Count > 0)
        {
            var addedBytes = new List<byte>();
            var added6Bytes = new List<byte>();
            var flagsBytes = new List<byte>();
            var flags6Bytes = new List<byte>();

            for (int i = 0; i < added.Count; i++)
            {
                var ep = added[i];
                var flags = (i < addedFlags.Count) ? addedFlags[i] : (byte)0;

                if (ep.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    addedBytes.AddRange(ep.Address.GetAddressBytes());
                    addedBytes.Add((byte)(ep.Port >> 8));
                    addedBytes.Add((byte)(ep.Port & 0xFF));
                    flagsBytes.Add(flags);
                }
                else if (ep.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    added6Bytes.AddRange(ep.Address.GetAddressBytes());
                    added6Bytes.Add((byte)(ep.Port >> 8));
                    added6Bytes.Add((byte)(ep.Port & 0xFF));
                    flags6Bytes.Add(flags);
                }
            }

            if (addedBytes.Count > 0)
            {
                dict.Dict["added"] = new BString(addedBytes.ToArray());
                dict.Dict["added.f"] = new BString(flagsBytes.ToArray());
            }
            if (added6Bytes.Count > 0)
            {
                dict.Dict["added6"] = new BString(added6Bytes.ToArray());
                dict.Dict["added6.f"] = new BString(flags6Bytes.ToArray());
            }
        }

        if (dropped?.Count > 0)
        {
            var droppedBytes = new List<byte>();
            var dropped6Bytes = new List<byte>();

            foreach (var ep in dropped)
            {
                if (ep.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    droppedBytes.AddRange(ep.Address.GetAddressBytes());
                    droppedBytes.Add((byte)(ep.Port >> 8));
                    droppedBytes.Add((byte)(ep.Port & 0xFF));
                }
                else if (ep.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    dropped6Bytes.AddRange(ep.Address.GetAddressBytes());
                    dropped6Bytes.Add((byte)(ep.Port >> 8));
                    dropped6Bytes.Add((byte)(ep.Port & 0xFF));
                }
            }

            if (droppedBytes.Count > 0)
            {
                dict.Dict["dropped"] = new BString(droppedBytes.ToArray());
            }

            if (dropped6Bytes.Count > 0)
            {
                dict.Dict["dropped6"] = new BString(dropped6Bytes.ToArray());
            }
        }

        if (dict.Dict.Count == 0)
        {
            return;
        }

        using var result = BencodeWriter.WriteToResult(dict);

        var msg = new PeerMessage(MessageId.Extended)
        {
            Data = new byte[1 + result.Memory.Length]
        };
        msg.Data[0] = (byte)RemoteMessageId.Value;
        result.Memory.Span.CopyTo(msg.Data.AsSpan(1));

        _ = _peer.SendMessageAsync(msg);
    }

    public void Update(IEnumerable<(IPEndPoint Ep, byte Flags)> peers)
    {
        if (!RemoteMessageId.HasValue)
        {
            return;
        }

        var added = new List<IPEndPoint>();
        var addedFlags = new List<byte>();

        // Build current set and detect new peers in single pass
        var currentSet = new HashSet<IPEndPoint>();
        foreach (var p in peers)
        {
            currentSet.Add(p.Ep);
            if (_sentPeers.Add(p.Ep))
            {
                // Was newly added to _sentPeers, so it's a new peer
                added.Add(p.Ep);
                addedFlags.Add(p.Flags);
            }
        }

        // Find and remove dropped peers in single pass
        var dropped = new List<IPEndPoint>();
        _sentPeers.RemoveWhere(p =>
        {
            if (!currentSet.Contains(p))
            {
                dropped.Add(p);
                return true;
            }
            return false;
        });

        if (added.Count > 0 || dropped.Count > 0)
        {
            SendPex(added, addedFlags, dropped);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            // No disposable resources currently
        }
    }

    private static void ParsePeers(ReadOnlyMemory<byte>? data, bool ipv6, List<IPEndPoint> list)
    {
        if (data == null)
        {
            return;
        }

        int stride = ipv6 ? 18 : 6;
        int ipLen = ipv6 ? 16 : 4;

        var span = data.Value.Span;
        for (int i = 0; i <= span.Length - stride; i += stride)
        {
            var ipSpan = span.Slice(i, ipLen);
            var port = (span[i + ipLen] << 8) | span[i + ipLen + 1];
            list.Add(new IPEndPoint(new IPAddress(ipSpan), port));
        }
    }

    private static void ParsePeersWithFlags(ReadOnlyMemory<byte>? data, ReadOnlyMemory<byte>? flags, bool ipv6, List<IPEndPoint> list, List<byte> flagList)
    {
        if (data == null || flags == null)
        {
            return;
        }

        int stride = ipv6 ? 18 : 6;
        int ipLen = ipv6 ? 16 : 4;

        var span = data.Value.Span;
        var flagsSpan = flags.Value.Span;
        int flagIndex = 0;

        for (int i = 0; i <= span.Length - stride; i += stride)
        {
            var ipSpan = span.Slice(i, ipLen);
            var port = (span[i + ipLen] << 8) | span[i + ipLen + 1];
            list.Add(new IPEndPoint(new IPAddress(ipSpan), port));

            byte flag = 0;
            if (flagIndex < flagsSpan.Length)
            {
                flag = flagsSpan[flagIndex];
            }
            flagList.Add(flag);
            flagIndex++;
        }
    }
}
