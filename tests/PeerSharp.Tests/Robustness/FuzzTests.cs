using PeerSharp.BEncoding;
using PeerSharp.Internals.Peers;
using System.Buffers;

namespace PeerSharp.Tests.Robustness;

public class FuzzTests
{
    private readonly ITestOutputHelper _output;

    public FuzzTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BencodeParser_Fuzz_RandomBytes_ShouldNotCrash()
    {
        var random = new Random(12345); // Fixed seed
        int iterations = 10000;
        int maxLen = 1024;
        byte[] buffer = new byte[maxLen];

        for (int i = 0; i < iterations; i++)
        {
            int len = random.Next(1, maxLen);
            random.NextBytes(buffer.AsSpan(0, len));
            var data = buffer.AsSpan(0, len).ToArray();

            try
            {
                BencodeParser.Parse(data);
            }
            catch (FormatException)
            {
                // Expected
            }
            catch (Exception ex)
            {
                _output.WriteLine($"BencodeParser crashed on iteration {i}: {ex}");
                Assert.Fail($"BencodeParser crashed with unexpected exception: {ex.GetType().Name}");
            }
        }
    }

    [Fact]
    public void PeerProtocol_Fuzz_RandomBytes_ShouldNotCrash()
    {
        var random = new Random(67890);
        int iterations = 10000;
        int maxLen = PeerProtocol.MaxMessageSize + 100; // Allow slightly larger than max to test boundary
        byte[] buffer = new byte[maxLen];

        for (int i = 0; i < iterations; i++)
        {
            int len = random.Next(1, 1024); // Most messages are small, but test occasional large ones
            if (random.NextDouble() < 0.01) len = random.Next(1, maxLen); // 1% large messages

            random.NextBytes(buffer.AsSpan(0, len));
            var sequence = new ReadOnlySequence<byte>(buffer.AsMemory(0, len));

            try
            {
                while (sequence.Length > 0)
                {
                    if (!PeerProtocol.TryDecodeMessage(ref sequence, out _, out _))
                    {
                        break;
                    }
                }
            }
            catch (InvalidDataException)
            {
                // Expected for malformed messages
            }
            catch (Exception ex)
            {
                 _output.WriteLine($"PeerProtocol crashed on iteration {i}: {ex}");
                 Assert.Fail($"PeerProtocol crashed with unexpected exception: {ex.GetType().Name}");
            }
        }
    }
}
