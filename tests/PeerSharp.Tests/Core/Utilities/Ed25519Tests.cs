using PeerSharp.Internals.Utilities;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core.Utilities;

/// <summary>
/// Validates the vendored Ed25519 against the RFC 8032 section 7.1 vectors, plus the negative
/// cases that a naive implementation passes the positive vectors while still getting wrong:
/// malleable signatures, non-canonical encodings, and wrong-key acceptance.
///
/// These tests are the entire justification for vendoring the algorithm rather than taking a
/// dependency, so they are deliberately exhaustive about failure modes rather than just
/// round-tripping.
/// </summary>
public class Ed25519Tests
{
    /// <summary>RFC 8032 section 7.1 test vectors: seed, public key, message, signature.</summary>
    public static TheoryData<string, string, string, string> Rfc8032Vectors => new()
    {
        // TEST 1: empty message
        {
            "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
        },
        // TEST 2: single byte
        {
            "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"
        },
        // TEST 3: two bytes
        {
            "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "af82",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"
        },
        // TEST 1024: a long message, which exercises the hash chaining rather than just the curve
        {
            "f5e5767cf153319517630f226876b86c8160cc583bc013744c6bf255f5cc0ee5",
            "278117fc144c72340f67d0f2316e8386ceffbf2b2428c9c51fef7c597f1d426e",
            "08b8b2b733424243760fe426a4b54908632110a66c2f6591eabd3345e3e4eb98fa6e264bf09efe12ee50f8f54e9f77b1e355f6c50544e23fb1433ddf73be84d8" +
            "79de7c0046dc4996d9e773f4bc9efe5738829adb26c81b37c93a1b270b20329d658675fc6ea534e0810a4432826bf58c941efb65d57a338bbd2e26640f89ffbc" +
            "1a858efcb8550ee3a5e1998bd177e93a7363c344fe6b199ee5d02e82d522c4feba15452f80288a821a579116ec6dad2b3b310da903401aa62100ab5d1a36553e" +
            "06203b33890cc9b832f79ef80560ccb9a39ce767967ed628c6ad573cb116dbefefd75499da96bd68a8a97b928a8bbc103b6621fcde2beca1231d206be6cd9ec7" +
            "aff6f6c94fcd7204ed3455c68c83f4a41da4af2b74ef5c53f1d8ac70bdcb7ed185ce81bd84359d44254d95629e9855a94a7c1958d1f8ada5d0532ed8a5aa3fb2" +
            "d17ba70eb6248e594e1a2297acbbb39d502f1a8c6eb6f1ce22b3de1a1f40cc24554119a831a9aad6079cad88425de6bde1a9187ebb6092cf67bf2b13fd65f270" +
            "88d78b7e883c8759d2c4f5c65adb7553878ad575f9fad878e80a0c9ba63bcbcc2732e69485bbc9c90bfbd62481d9089beccf80cfe2df16a2cf65bd92dd597b07" +
            "07e0917af48bbb75fed413d238f5555a7a569d80c3414a8d0859dc65a46128bab27af87a71314f318c782b23ebfe808b82b0ce26401d2e22f04d83d1255dc51a" +
            "ddd3b75a2b1ae0784504df543af8969be3ea7082ff7fc9888c144da2af58429ec96031dbcad3dad9af0dcbaaaf268cb8fcffead94f3c7ca495e056a9b47acdb7" +
            "51fb73e666c6c655ade8297297d07ad1ba5e43f1bca32301651339e22904cc8c42f58c30c04aafdb038dda0847dd988dcda6f3bfd15c4b4c4525004aa06eeff8" +
            "ca61783aacec57fb3d1f92b0fe2fd1a85f6724517b65e614ad6808d6f6ee34dff7310fdc82aebfd904b01e1dc54b2927094b2db68d6f903b68401adebf5a7e08" +
            "d78ff4ef5d63653a65040cf9bfd4aca7984a74d37145986780fc0b16ac451649de6188a7dbdf191f64b5fc5e2ab47b57f7f7276cd419c17a3ca8e1b939ae49e4" +
            "88acba6b965610b5480109c8b17b80e1b7b750dfc7598d5d5011fd2dcc5600a32ef5b52a1ecc820e308aa342721aac0943bf6686b64b2579376504ccc493d97e" +
            "6aed3fb0f9cd71a43dd497f01f17c0e2cb3797aa2a2f256656168e6c496afc5fb93246f6b1116398a346f1a641f3b041e989f7914f90cc2c7fff357876e506b5" +
            "0d334ba77c225bc307ba537152f3f1610e4eafe595f6d9d90d11faa933a15ef1369546868a7f3a45a96768d40fd9d03412c091c6315cf4fde7cb68606937380d" +
            "b2eaaa707b4c4185c32eddcdd306705e4dc1ffc872eeee475a64dfac86aba41c0618983f8741c5ef68d3a101e8a3b8cac60c905c15fc910840b94c00a0b9d0",
            "0aab4c900501b3e24d7cdf4663326a3a87df5e4843b2cbdb67cbf6e460fec350aa5371b1508f9f4528ecea23c436d94b5e8fcd4f681e30a6ac00a9704a188a03"
        },
        // SHA(abc): a 64-byte message, matching the hash block size exactly
        {
            "833fe62409237b9d62ec77587520911e9a759cec1d19755b7da901b96dca3d42",
            "ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
            "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
            "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b58909351fc9ac90b3ecfdfbc7c66431e0303dca179c138ac17ad9bef1177331a704"
        },
    };

    [Theory]
    [MemberData(nameof(Rfc8032Vectors))]
    public void PublicKeyFromSeed_MatchesRfc8032(string seedHex, string publicKeyHex, string messageHex, string signatureHex)
    {
        _ = messageHex;
        _ = signatureHex;

        var derived = Ed25519.PublicKeyFromSeed(Convert.FromHexString(seedHex));

        Assert.Equal(publicKeyHex, Convert.ToHexStringLower(derived));
    }

    [Theory]
    [MemberData(nameof(Rfc8032Vectors))]
    public void Sign_MatchesRfc8032(string seedHex, string publicKeyHex, string messageHex, string signatureHex)
    {
        _ = publicKeyHex;

        var signature = Ed25519.Sign(Convert.FromHexString(messageHex), Convert.FromHexString(seedHex));

        // Ed25519 is deterministic, so the signature must match byte for byte - not merely verify.
        Assert.Equal(signatureHex, Convert.ToHexStringLower(signature));
    }

    [Theory]
    [MemberData(nameof(Rfc8032Vectors))]
    public void Verify_AcceptsRfc8032(string seedHex, string publicKeyHex, string messageHex, string signatureHex)
    {
        _ = seedHex;

        Assert.True(Ed25519.Verify(
            Convert.FromHexString(signatureHex),
            Convert.FromHexString(messageHex),
            Convert.FromHexString(publicKeyHex)));
    }

    [Fact]
    public void Verify_RejectsTamperedMessage()
    {
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var message = "the original message"u8.ToArray();
        var signature = Ed25519.Sign(message, seed);

        var tampered = (byte[])message.Clone();
        tampered[0] ^= 0x01;

        Assert.True(Ed25519.Verify(signature, message, publicKey));
        Assert.False(Ed25519.Verify(signature, tampered, publicKey));
    }

    [Fact]
    public void Verify_RejectsWrongPublicKey()
    {
        var message = "signed by someone else"u8.ToArray();
        var signature = Ed25519.Sign(message, Ed25519.GenerateSeed());
        var otherKey = Ed25519.PublicKeyFromSeed(Ed25519.GenerateSeed());

        Assert.False(Ed25519.Verify(signature, message, otherKey));
    }

    [Fact]
    public void Verify_RejectsEverySingleBitFlipInTheSignature()
    {
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var message = "bit flip sweep"u8.ToArray();
        var signature = Ed25519.Sign(message, seed);

        for (int byteIndex = 0; byteIndex < signature.Length; byteIndex++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                var mutated = (byte[])signature.Clone();
                mutated[byteIndex] ^= (byte)(1 << bit);

                Assert.False(
                    Ed25519.Verify(mutated, message, publicKey),
                    $"Accepted a signature with bit {bit} of byte {byteIndex} flipped.");
            }
        }
    }

    /// <summary>
    /// S must be reduced mod L. Adding L to a valid S yields a different byte sequence that a
    /// naive verifier still accepts, which would let an attacker produce a second valid signature
    /// for a message they did not sign - fatal for anything using the signature as an identity.
    /// </summary>
    [Fact]
    public void Verify_RejectsMalleableSignatureWithUnreducedS()
    {
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var message = "malleability check"u8.ToArray();
        var signature = Ed25519.Sign(message, seed);

        var order = System.Numerics.BigInteger.Pow(2, 252) +
            System.Numerics.BigInteger.Parse("27742317777372353535851937790883648493");
        var s = new System.Numerics.BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: false);

        var mauled = (byte[])signature.Clone();
        var raised = s + order;
        Assert.True(raised.GetByteCount(isUnsigned: true) <= 32, "Test vector unsuitable: S + L overflows 32 bytes.");
        mauled.AsSpan(32).Clear();
        raised.TryWriteBytes(mauled.AsSpan(32), out _, isUnsigned: true, isBigEndian: false);

        Assert.True(Ed25519.Verify(signature, message, publicKey));
        Assert.False(Ed25519.Verify(mauled, message, publicKey));
    }

    /// <summary>
    /// y must be canonical, i.e. strictly less than p. Values in [p, 2^255) encode the same curve
    /// point and must be rejected so a public key has exactly one representation.
    /// </summary>
    [Fact]
    public void Verify_RejectsNonCanonicalPublicKeyEncoding()
    {
        var message = "canonical encoding"u8.ToArray();
        var signature = Ed25519.Sign(message, Ed25519.GenerateSeed());

        // p = 2^255 - 19, so y = p is the smallest non-canonical encoding.
        var nonCanonical = new byte[32];
        var p = System.Numerics.BigInteger.Pow(2, 255) - 19;
        p.TryWriteBytes(nonCanonical, out _, isUnsigned: true, isBigEndian: false);

        Assert.False(Ed25519.Verify(signature, message, nonCanonical));
    }

    [Fact]
    public void Verify_RejectsMalformedLengths()
    {
        var seed = Ed25519.GenerateSeed();
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var message = "length checks"u8.ToArray();
        var signature = Ed25519.Sign(message, seed);

        Assert.False(Ed25519.Verify(signature.AsSpan(0, 63), message, publicKey));
        Assert.False(Ed25519.Verify(signature, message, publicKey.AsSpan(0, 31)));
        Assert.False(Ed25519.Verify([], message, publicKey));
    }

    [Fact]
    public void Sign_IsDeterministic()
    {
        var seed = Ed25519.GenerateSeed();
        var message = "determinism"u8.ToArray();

        Assert.Equal(Ed25519.Sign(message, seed), Ed25519.Sign(message, seed));
    }

    [Fact]
    public void RoundTrip_AcrossManyRandomKeysAndMessageSizes()
    {
        // Sizes chosen around the SHA-512 block boundary, where padding bugs hide.
        foreach (int size in new[] { 0, 1, 31, 32, 63, 64, 65, 127, 128, 1000 })
        {
            var seed = Ed25519.GenerateSeed();
            var publicKey = Ed25519.PublicKeyFromSeed(seed);
            var message = RandomNumberGenerator.GetBytes(size);

            var signature = Ed25519.Sign(message, seed);

            Assert.True(Ed25519.Verify(signature, message, publicKey), $"Round-trip failed at message size {size}.");
        }
    }

    [Fact]
    public void PublicKeyFromSeed_RejectsWrongSeedLength()
    {
        Assert.Throws<ArgumentException>(() => Ed25519.PublicKeyFromSeed(new byte[31]));
        Assert.Throws<ArgumentException>(() => Ed25519.Sign([], new byte[33]));
    }
}
