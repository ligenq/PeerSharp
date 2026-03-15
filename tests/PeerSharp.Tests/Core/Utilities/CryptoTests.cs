using PeerSharp.Internals.Utilities;
using System.Security.Cryptography;

namespace PeerSharp.Tests.Core.Utilities;

public class CryptoTests
{
    [Fact]
    public void DiffieHellman_KeyExchange_Matches()
    {
        var alice = new DiffieHellman();
        var bob = new DiffieHellman();

        alice.ComputeSharedSecret(bob.GetPublicKeyBytes());
        bob.ComputeSharedSecret(alice.GetPublicKeyBytes());

        Assert.Equal(alice.GetSharedSecretBytes(), bob.GetSharedSecretBytes());
    }

    [Fact]
    public void RC4_EncryptDecrypt_Matches()
    {
        byte[] key = "secret-key"u8.ToArray();
        byte[] data = "Hello World! This is a test message."u8.ToArray();
        byte[] original = data.ToArray();

        var rc4 = new RC4();
        rc4.Init(key);
        rc4.Encrypt(data);

        Assert.NotEqual(original, data);

        rc4.Init(key);
        rc4.Decrypt(data);

        Assert.Equal(original, data);
    }

    [Fact]
    public void RC4_Skip_Works()
    {
        byte[] key = new byte[16];
        RandomNumberGenerator.Fill(key);

        var rc4_1 = new RC4();
        rc4_1.Init(key);
        rc4_1.Skip(1024);

        var rc4_2 = new RC4();
        rc4_2.Init(key);
        byte[] dummy = new byte[1024];
        rc4_2.Encrypt(dummy);

        byte[] data1 = new byte[100];
        byte[] data2 = new byte[100];
        RandomNumberGenerator.Fill(data1);
        data1.CopyTo(data2, 0);

        rc4_1.Encrypt(data1);
        rc4_2.Encrypt(data2);

        Assert.Equal(data1, data2);
    }

    [Fact]
    public void RC4_CloneRestore_Works()
    {
        byte[] key = new byte[16];
        RandomNumberGenerator.Fill(key);

        var rc4 = new RC4();
        rc4.Init(key);
        rc4.Skip(100);

        var clone = rc4.Clone();

        byte[] data1 = new byte[50];
        byte[] data2 = new byte[50];
        RandomNumberGenerator.Fill(data1);
        data1.CopyTo(data2, 0);

        rc4.Encrypt(data1);
        clone.Encrypt(data2);

        Assert.Equal(data1, data2);

        // Modify clone and restore original
        clone.Skip(100);
        clone.Restore(rc4);

        byte[] data3 = new byte[50];
        byte[] data4 = new byte[50];
        RandomNumberGenerator.Fill(data3);
        data3.CopyTo(data4, 0);

        rc4.Encrypt(data3);
        clone.Encrypt(data4);

        Assert.Equal(data3, data4);
    }
}





