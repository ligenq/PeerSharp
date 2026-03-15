using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Network;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace PeerSharp.Internals.Utp;

internal class UtpManager : IUdpReceiver, IUtpManager
{
    // SACK extension type per BEP-29
    private const byte ExtensionSack = 1;

    private readonly ILogger<UtpManager> _logger = TorrentLoggerFactory.CreateLogger<UtpManager>();
    private readonly ConcurrentDictionary<UtpSocketKey, UtpStream> _streamsByRecvId = new();
    private readonly ConcurrentDictionary<UtpSocketKey, UtpStream> _streamsBySendId = new();
    private readonly TimeProvider _timeProvider;
    private AtomicDisposal _disposal = new();
    private bool _running;
    private ITimer? _timer;

    public UtpManager(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public IUdpListener? Listener { get; private set; }
    public Action<UtpStream>? OnNewConnection { get; set; }

    public static ushort ReadUInt16BigEndian(byte[] data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    public static uint ReadUInt32BigEndian(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }

    public static void WriteUInt16BigEndian(byte[] data, int offset, ushort val)
    {
        data[offset] = (byte)(val >> 8);
        data[offset + 1] = (byte)val;
    }

    public static void WriteUInt32BigEndian(byte[] data, int offset, uint val)
    {
        data[offset] = (byte)(val >> 24);
        data[offset + 1] = (byte)(val >> 16);
        data[offset + 2] = (byte)(val >> 8);
        data[offset + 3] = (byte)val;
    }

    public void CloseStream(UtpStream stream)
    {
        var recvKey = new UtpSocketKey(stream.RemoteEndPoint, stream.ConnectionIdRecv);
        var sendKey = new UtpSocketKey(stream.RemoteEndPoint, stream.ConnectionIdSend);
        _streamsByRecvId.TryRemove(recvKey, out _);
        _streamsBySendId.TryRemove(sendKey, out _);
    }

    public UtpStream CreateStream(IPEndPoint remote)
    {
        const int maxRetries = 100;
        for (int i = 0; i < maxRetries; i++)
        {
            var id = (ushort)Random.Shared.Next(0, 65535);
            var sendId = (ushort)(id + 1);

            // Check both IDs to avoid collision with existing streams
            // (incoming connections use id+1 as their recvId)
            if (_streamsByRecvId.ContainsKey(new UtpSocketKey(remote, id)) ||
                _streamsByRecvId.ContainsKey(new UtpSocketKey(remote, sendId)) ||
                _streamsBySendId.ContainsKey(new UtpSocketKey(remote, id)) ||
                _streamsBySendId.ContainsKey(new UtpSocketKey(remote, sendId)))
            {
                continue;
            }

            var stream = new UtpStream(this, remote, id, sendId, _timeProvider);
            if (AddStream(remote, id, sendId, stream))
            {
                return stream;
            }
            // TryAdd failed due to race condition, retry with new ID
        }

        throw new InvalidOperationException("Failed to allocate unique connection ID after maximum retries");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposal.MarkDisposed())
        {
            Stop();
        }
        await ValueTask.CompletedTask.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void Receive(byte[] data, IPEndPoint remote)
    {
        if (!_running || data.Length < 20)
        {
            return;
        }

        // Normalize IPv6-mapped IPv4 addresses (e.g. ::ffff:127.0.0.1 -> 127.0.0.1)
        // so that lookups match streams created with plain IPv4 endpoints.
        if (remote.Address.IsIPv4MappedToIPv6)
        {
            remote = new IPEndPoint(remote.Address.MapToIPv4(), remote.Port);
        }

        byte ver = (byte)(data[0] & 0x0F);
        if (ver != 1)
        {
            return;
        }

        var header = ParseHeader(data);
        if (!TryParseExtensions(data, header, remote, out int headerSize, out var sackRanges, out var extensionBits))
        {
            return;
        }

        var key = new UtpSocketKey(remote, header.ConnectionId);
        if (TryGetStream(header.Type, key, out var stream))
        {
            stream.ProcessPacketWithSack(header, data, headerSize, sackRanges, extensionBits, remote);
        }
        else if (header.Type == MessageType.ST_SYN)
        {
            ushort sendId = header.ConnectionId;
            ushort recvId = (ushort)(header.ConnectionId + 1);

            var newStream = new UtpStream(this, remote, recvId, sendId, _timeProvider);
            if (AddStream(remote, recvId, sendId, newStream))
            {
                newStream.ProcessPacketWithSack(header, data, headerSize, sackRanges, extensionBits, remote);
                OnNewConnection?.Invoke(newStream);
            }
            // else: stream already exists (race condition or duplicate SYN), ignore
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint remote, CancellationToken ct)
    {
        if (Listener != null)
        {
            await Listener.SendAsync(packet, remote, ct).ConfigureAwait(false);
        }
    }

    public void Start(IUdpListener listener)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        Listener = listener;
        Listener.RegisterReceiver(this);

        _timer = _timeProvider.CreateTimer(CheckTimeouts, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;

        foreach (var stream in _streamsByRecvId.Values)
        {
            stream.Close();
        }
        _streamsByRecvId.Clear();
        _streamsBySendId.Clear();
    }

    private static MessageHeader ParseHeader(byte[] data)
    {
        // Manual parsing for big-endian fields
        var h = new MessageHeader
        {
            TypeVer = data[0],
            Extension = data[1],
            ConnectionId = ReadUInt16BigEndian(data, 2),
            TimestampMicroseconds = ReadUInt32BigEndian(data, 4),
            TimestampDifferenceMicroseconds = ReadUInt32BigEndian(data, 8),
            WndSize = ReadUInt32BigEndian(data, 12),
            SeqNr = ReadUInt16BigEndian(data, 16),
            AckNr = ReadUInt16BigEndian(data, 18)
        };
        return h;
    }

    private bool AddStream(IPEndPoint remote, ushort recvId, ushort sendId, UtpStream stream)
    {
        var recvKey = new UtpSocketKey(remote, recvId);
        if (!_streamsByRecvId.TryAdd(recvKey, stream))
        {
            return false;
        }

        var sendKey = new UtpSocketKey(remote, sendId);
        if (!_streamsBySendId.TryAdd(sendKey, stream))
        {
            _streamsByRecvId.TryRemove(recvKey, out _);
            return false;
        }

        return true;
    }

    private void CheckTimeouts(object? state)
    {
        foreach (var stream in _streamsByRecvId.Values)
        {
            try
            {
                stream.CheckTimeout();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking timeout for stream");
            }
        }
    }

    /// <summary>
    /// SACK SUPPORT: Parse SACK bitmask extension into sequence number ranges.
    /// The SACK bitmask indicates which packets after ack_nr have been received.
    /// Bit 0 = ack_nr + 2, bit 1 = ack_nr + 3, etc.
    /// </summary>
    private List<(ushort Start, ushort End)>? ParseSackExtension(byte[] data, int offset, int len, ushort ackNr)
    {
        var ranges = new List<(ushort Start, ushort End)>();
        ushort? rangeStart = null;
        ushort lastSeq = 0;

        for (int i = 0; i < len; i++)
        {
            byte b = data[offset + i];
            for (int bit = 0; bit < 8; bit++)
            {
                int seqOffset = (i * 8) + bit;
                ushort seq = (ushort)(ackNr + 2 + seqOffset);

                if ((b & (1 << bit)) != 0)
                {
                    // Packet received
                    if (rangeStart == null)
                    {
                        rangeStart = seq;
                    }
                    lastSeq = seq;
                }
                else
                {
                    // Packet not received - close current range if open
                    if (rangeStart != null)
                    {
                        ranges.Add((rangeStart.Value, lastSeq));
                        rangeStart = null;
                    }
                }
            }
        }

        // Close final range if still open
        if (rangeStart != null)
        {
            ranges.Add((rangeStart.Value, lastSeq));
        }

        if (ranges.Count > 0)
        {
            _logger.LogDebug("SACK: Parsed {Count} ranges from {Len} bytes", ranges.Count, len);
        }

        return ranges.Count > 0 ? ranges : null;
    }

    private bool TryGetStream(MessageType type, UtpSocketKey key, [NotNullWhen(true)] out UtpStream? stream)
    {
        if (type == MessageType.ST_RESET)
        {
            if (_streamsBySendId.TryGetValue(key, out stream))
            {
                return true;
            }

            stream = null;
            return false;
        }

        return _streamsByRecvId.TryGetValue(key, out stream);
    }

    private bool TryParseExtensions(
            byte[] data,
        MessageHeader header,
        IPEndPoint remote,
        out int headerSize,
        out List<(ushort Start, ushort End)>? sackRanges,
        out byte[]? extensionBits)
    {
        headerSize = 20;
        sackRanges = null;
        extensionBits = null;
        byte extension = header.Extension;
        int extensionCount = 0;

        while (extension != 0)
        {
            // Security: Limit number of extensions to prevent CPU/malformed chain abuse
            if (++extensionCount > 16)
            {
                _logger.LogWarning("uTP {Remote}: Too many extensions ({Count}), dropping packet", remote, extensionCount);
                return false;
            }

            if (headerSize + 2 > data.Length)
            {
                _logger.LogDebug("uTP {Remote}: Malformed extension chain (too short for header)", remote);
                return false;
            }

            byte nextExtension = data[headerSize];
            int len = data[headerSize + 1];

            if (headerSize + 2 + len > data.Length)
            {
                _logger.LogDebug("uTP {Remote}: Malformed extension chain (len {Len} goes past packet end)", remote, len);
                return false;
            }

            switch (extension)
            {
                case ExtensionSack:
                    // Per BEP-29: SACK is a bitmask, each byte = 8 packets, valid range 1-32 bytes
                    if (len > 0 && len <= 32)
                    {
                        var newRanges = ParseSackExtension(data, headerSize + 2, len, header.AckNr);
                        if (newRanges != null)
                        {
                            if (sackRanges == null)
                            {
                                sackRanges = newRanges;
                            }
                            else
                            {
                                sackRanges.AddRange(newRanges);
                            }
                        }
                    }
                    else if (len > 32)
                    {
                        _logger.LogDebug("uTP {Remote}: SACK extension too long {Len}", remote, len);
                    }
                    break;

                case 2:
                    // Extension bits per libutp (8 bytes)
                    if (len != 8)
                    {
                        _logger.LogDebug("uTP {Remote}: Invalid extension bits len {Len}", remote, len);
                        return false;
                    }
                    extensionBits = new byte[8];
                    Buffer.BlockCopy(data, headerSize + 2, extensionBits, 0, 8);
                    break;
            }

            headerSize += 2 + len;
            extension = nextExtension;
        }

        return true;
    }

    private readonly struct UtpSocketKey : IEquatable<UtpSocketKey>
    {
        public readonly IPAddress Address;
        public readonly ushort ConnectionId;
        public readonly int Port;

        public UtpSocketKey(IPEndPoint endpoint, ushort connectionId)
        {
            Address = endpoint.Address;
            Port = endpoint.Port;
            ConnectionId = connectionId;
        }

        public bool Equals(UtpSocketKey other)
        {
            return Port == other.Port && ConnectionId == other.ConnectionId && Address.Equals(other.Address);
        }

        public override bool Equals(object? obj)
        {
            return obj is UtpSocketKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Address, Port, ConnectionId);
        }
    }
}
