using BenchmarkDotNet.Attributes;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Benchmarks;

/// <summary>
/// MSE stream encryption. RC4 touches every byte on an encrypted connection, and encryption is
/// the norm rather than the exception in public swarms, so after hashing this is the largest
/// per-byte CPU cost in the engine.
///
/// Two layers are measured deliberately. <see cref="RawRc4Encrypt"/> is the cipher alone;
/// <see cref="WrappedEncrypt"/> goes through <see cref="ProtocolEncryption"/>, which takes a lock
/// because RC4 keystream state is not thread-safe. The gap between them prices that lock.
///
/// <see cref="ParallelPeersEncrypt"/> models the shape the engine actually produces: one
/// <see cref="ProtocolEncryption"/> per peer, several peers encrypting at once. The locks are
/// per-instance and separate for the send and receive directions, so peers should not serialise
/// against each other - this benchmark is what would catch it if that ever stopped being true.
/// </summary>
[MemoryDiagnoser]
public class ProtocolEncryptionBenchmarks
{
    private const int PeerCount = 8;
    private const int OpsPerPeer = 256;

    private RC4 _rc4 = null!;
    private ProtocolEncryption _encryption = null!;
    private byte[] _buffer = null!;
    private ProtocolEncryption[] _peerCiphers = null!;
    private byte[][] _peerBuffers = null!;

    /// <summary>Payload size. 16 KiB is a block; 68 bytes is roughly a handshake.</summary>
    [Params(68, 16 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var key = new byte[20];
        Random.Shared.NextBytes(key);

        _rc4 = new RC4();
        _rc4.Init(key);

        _encryption = new ProtocolEncryption();
        _encryption.RC4Out.Init(key);
        _encryption.RC4In.Init(key);

        _buffer = new byte[PayloadSize];
        Random.Shared.NextBytes(_buffer);

        // One cipher and one buffer per simulated peer. Sharing either would measure something
        // the engine never does - cache-line ping-pong or lock contention that cannot arise.
        _peerCiphers = new ProtocolEncryption[PeerCount];
        _peerBuffers = new byte[PeerCount][];
        for (int i = 0; i < PeerCount; i++)
        {
            _peerCiphers[i] = new ProtocolEncryption();
            _peerCiphers[i].RC4Out.Init(key);
            _peerCiphers[i].RC4In.Init(key);

            _peerBuffers[i] = new byte[PayloadSize];
            Random.Shared.NextBytes(_peerBuffers[i]);
        }
    }

    [Benchmark(Baseline = true, Description = "RC4 only")]
    public void RawRc4Encrypt() => _rc4.Encrypt(_buffer.AsSpan());

    [Benchmark(Description = "ProtocolEncryption.Encrypt (locked)")]
    public void WrappedEncrypt() => _encryption.Encrypt(_buffer.AsSpan());

    [Benchmark(Description = "ProtocolEncryption.Decrypt (locked)")]
    public void WrappedDecrypt() => _encryption.Decrypt(_buffer.AsSpan());

    /// <summary>
    /// Aggregate cost of <see cref="PeerCount"/> x <see cref="OpsPerPeer"/> encryptions issued
    /// concurrently, one cipher per peer. This is batch throughput, not single-operation latency:
    /// divide by the op count before comparing against the rows above. If peers scale, the result
    /// should approach the single-threaded cost times ops divided by core count.
    /// </summary>
    [Benchmark(Description = "Encrypt, 8 peers in parallel (own ciphers)")]
    public void ParallelPeersEncrypt()
    {
        var threads = new Thread[PeerCount];
        for (int i = 0; i < PeerCount; i++)
        {
            int index = i;
            threads[i] = new Thread(() =>
            {
                var cipher = _peerCiphers[index];
                var buffer = _peerBuffers[index];
                for (int op = 0; op < OpsPerPeer; op++)
                {
                    cipher.Encrypt(buffer.AsSpan());
                }
            });
            threads[i].Start();
        }

        for (int i = 0; i < PeerCount; i++)
        {
            threads[i].Join();
        }
    }
}
