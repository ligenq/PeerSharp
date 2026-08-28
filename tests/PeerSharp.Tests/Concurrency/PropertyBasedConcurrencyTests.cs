using CsCheck;
using PeerSharp.Internals;
using PeerSharp.Internals.Bandwidth;

namespace PeerSharp.Tests.Concurrency;

[Collection("Concurrency")]
public class PropertyBasedConcurrencyTests
{
    [Fact]
    public void BandwidthChannel_ConcurrentOperations_AreLinearizable()
    {
        Gen.Int[1, 100_000]
            .Select(limit =>
            {
                var channel = new BandwidthChannel(TimeProvider.System);
                channel.SetLimit(limit);
                return (new BandwidthActual(channel), new BandwidthModel(limit));
            })
            .SampleParallel(
                Gen.Int[1, 5_000].Operation<BandwidthActual, BandwidthModel>(
                    amount => $"UseQuota({amount})",
                    (actual, amount) => actual.Channel.UseQuota(amount),
                    (model, amount) => model.UseQuota(amount)),
                Gen.Int[1, 5_000].Operation<BandwidthActual, BandwidthModel>(
                    amount => $"ReturnQuota({amount})",
                    (actual, amount) => actual.Channel.ReturnQuota(amount),
                    (model, amount) => model.ReturnQuota(amount)),
                Gen.Int[1, 2_000].Operation<BandwidthActual, BandwidthModel>(
                    milliseconds => $"UpdateQuota({milliseconds})",
                    (actual, milliseconds) => actual.Channel.UpdateQuota(milliseconds),
                    (model, milliseconds) => model.UpdateQuota(milliseconds)),
                equal: (actual, model) => actual.Channel.AvailableQuota == model.Quota,
                maxSequentialOperations: 12,
                maxParallelOperations: 8,
                iter: 5_000,
                threads: 4,
                replay: 20,
                writeLine: Console.WriteLine);
    }

    [Fact]
    public void PieceState_GeneratedConcurrentBlocks_MatchSetSemantics()
    {
        Gen.Select(Gen.Int[1, 16], Gen.Int[-2, 17].Array[0, 64])
            .Sample((blocksCount, blockIndexes) =>
            {
                using var piece = new PieceState(index: 0, blocksCount);
                var accepted = new bool[blockIndexes.Length];

                Parallel.For(0, blockIndexes.Length, i =>
                {
                    var block = new Block(length: 1);
                    accepted[i] = piece.TryAddBlockFromWebSeed(blockIndexes[i], block);
                    if (!accepted[i])
                    {
                        block.Dispose();
                    }
                });

                var expected = blockIndexes
                    .Where(index => index >= 0 && index < blocksCount)
                    .Distinct()
                    .Order()
                    .ToArray();

                Assert.Equal(expected.Length, accepted.Count(value => value));
                Assert.Equal(expected.Length, piece.ReceivedCount);
                Assert.Equal(expected, piece.Blocks
                    .Select((received, index) => (received, index))
                    .Where(item => item.received)
                    .Select(item => item.index));
                Assert.Equal(expected.Length == blocksCount, piece.TryCompleteAndSetWriting());
                Assert.Equal(expected.Length == blocksCount, piece.IsWriting);
                Assert.False(piece.TryCompleteAndSetWriting());
            }, iter: 2_000, threads: 1);
    }

    private sealed record BandwidthActual(BandwidthChannel Channel)
    {
        public override string ToString() => $"Quota={Channel.AvailableQuota}, Limit={Channel.GetLimit()}";
    }

    private sealed class BandwidthModel(long limit)
    {
        private long _subQuota;

        public long Quota { get; private set; }

        public void ReturnQuota(int amount)
        {
            Quota = Math.Min(3 * limit, Quota + amount);
        }

        public void UpdateQuota(int milliseconds)
        {
            long generated = limit * milliseconds;
            long delta = generated / 1_000;
            _subQuota += generated % 1_000;

            if (_subQuota >= 1_000)
            {
                delta += _subQuota / 1_000;
                _subQuota %= 1_000;
            }

            Quota = Math.Min(3 * limit, Quota + delta);
        }

        public void UseQuota(int amount)
        {
            Quota = Math.Max(-3 * limit, Quota - amount);
        }

        public override string ToString() => $"Quota={Quota}, SubQuota={_subQuota}, Limit={limit}";
    }
}
