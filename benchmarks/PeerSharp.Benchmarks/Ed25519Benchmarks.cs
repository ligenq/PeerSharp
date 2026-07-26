using BenchmarkDotNet.Attributes;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Benchmarks;

/// <summary>
/// Ed25519, as used by BEP 44 mutable DHT items.
///
/// Verification cost is a security property here, not just a performance one. A BEP 44 storage
/// node verifies a signature for every incoming <c>put</c>, so this figure sets how much CPU an
/// attacker can burn per packet. Token validation gates <c>put</c> and is far cheaper, which
/// limits the exposure, but the ratio between the two is what decides whether that gate is
/// enough.
///
/// The implementation is BigInteger-based rather than a ref10 port, so these numbers are
/// expected to be well off libsodium's; the question is whether they are acceptable, not whether
/// they are competitive.
/// </summary>
[MemoryDiagnoser]
public class Ed25519Benchmarks
{
    private byte[] _seed = null!;
    private byte[] _publicKey = null!;
    private byte[] _message = null!;
    private byte[] _signature = null!;
    private byte[] _corruptSignature = null!;

    /// <summary>BEP 44 caps a value at 1000 bytes, so that is the realistic upper bound.</summary>
    [Params(64, 1000)]
    public int MessageSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _seed = Ed25519.GenerateSeed();
        _publicKey = Ed25519.PublicKeyFromSeed(_seed);
        _message = new byte[MessageSize];
        Random.Shared.NextBytes(_message);
        _signature = Ed25519.Sign(_message, _seed);

        // A signature that fails late, after the curve work - the expensive rejection path, and
        // the one an attacker would choose.
        _corruptSignature = (byte[])_signature.Clone();
        _corruptSignature[0] ^= 0x01;
    }

    [Benchmark(Baseline = true, Description = "Verify (valid)")]
    public bool VerifyValid() => Ed25519.Verify(_signature, _message, _publicKey);

    [Benchmark(Description = "Verify (invalid - attacker's path)")]
    public bool VerifyInvalid() => Ed25519.Verify(_corruptSignature, _message, _publicKey);

    [Benchmark(Description = "Sign")]
    public byte[] Sign() => Ed25519.Sign(_message, _seed);

    [Benchmark(Description = "PublicKeyFromSeed")]
    public byte[] DerivePublicKey() => Ed25519.PublicKeyFromSeed(_seed);
}
