using System;
using System.Text;
using CardGames.Provable;
using Xunit;

namespace CardGames.Tests
{
    public class DeterministicRngTests
    {
        private static byte[] Seed(string s) => Encoding.UTF8.GetBytes(s);

        [Fact]
        public void SameSeed_ProducesSameSequence()
        {
            var a = new DeterministicRng(Seed("seed-A"));
            var b = new DeterministicRng(Seed("seed-A"));
            for (int i = 0; i < 1000; i++)
                Assert.Equal(a.NextUInt32(), b.NextUInt32());
        }

        [Fact]
        public void DifferentSeed_ProducesDifferentSequence()
        {
            var a = new DeterministicRng(Seed("seed-A"));
            var b = new DeterministicRng(Seed("seed-B"));

            bool diverged = false;
            for (int i = 0; i < 1000 && !diverged; i++)
                diverged = a.NextUInt32() != b.NextUInt32();

            Assert.True(diverged);
        }

        [Fact]
        public void NextIndex_AlwaysInRange()
        {
            var rng = new DeterministicRng(Seed("range"));
            for (int i = 0; i < 100_000; i++)
                Assert.InRange(rng.NextIndex(52), 0, 51);
        }

        [Fact]
        public void NextIndex_IsApproximatelyUniform()
        {
            // Seeded → deterministic, so this distribution check never flakes.
            const int buckets = 13;
            const int draws = 1_300_000;
            var counts = new int[buckets];
            var rng = new DeterministicRng(Seed("uniformity"));

            for (int i = 0; i < draws; i++)
                counts[rng.NextIndex(buckets)]++;

            int expected = draws / buckets;
            foreach (var c in counts)
            {
                double deviation = Math.Abs(c - expected) / (double)expected;
                Assert.True(deviation < 0.02, $"bucket off by {deviation:P2} (>2%)");
            }
        }

        [Fact]
        public void EmptyOrNullSeed_Throws()
        {
            Assert.Throws<ArgumentException>(() => new DeterministicRng(Array.Empty<byte>()));
            Assert.Throws<ArgumentException>(() => new DeterministicRng(null));
        }

        [Fact]
        public void NextIndex_RejectsNonPositiveBound()
        {
            var rng = new DeterministicRng(Seed("x"));
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextIndex(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextIndex(-3));
        }
    }
}
