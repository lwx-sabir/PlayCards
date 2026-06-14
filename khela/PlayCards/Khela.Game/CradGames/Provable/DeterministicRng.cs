using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CardGames.Provable
{
    /// <summary>
    /// Deterministic, cryptographically-derived RNG: the same seed always yields the same stream.
    /// Bytes are an HMAC-SHA256 keystream — block k = HMAC-SHA256(seed, k as 8-byte big-endian),
    /// for k = 0, 1, 2, … — read 4 bytes (big-endian) per draw. Unpredictable without the seed,
    /// yet fully reproducible with it: exactly the property a provably-fair shuffle needs.
    ///
    /// A third party can reimplement this from the one-line spec above to verify any shuffle.
    /// </summary>
    public sealed class DeterministicRng
    {
        private const int BlockSize = 32; // SHA-256 output; divisible by 4, so draws never straddle blocks

        private readonly byte[] _seed;
        private long _block;
        private byte[] _buffer = Array.Empty<byte>();
        private int _pos;

        public DeterministicRng(byte[] seed)
        {
            if (seed == null || seed.Length == 0)
                throw new ArgumentException("Seed must be non-empty.", nameof(seed));
            _seed = (byte[])seed.Clone();
        }

        private void Refill()
        {
            Span<byte> counter = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(counter, _block++);
            using var hmac = new HMACSHA256(_seed);
            _buffer = hmac.ComputeHash(counter.ToArray()); // BlockSize bytes
            _pos = 0;
        }

        /// <summary>Next 32 bits of the keystream (big-endian).</summary>
        public uint NextUInt32()
        {
            if (_buffer.Length - _pos < 4) Refill();
            uint v = (uint)((_buffer[_pos] << 24)
                          | (_buffer[_pos + 1] << 16)
                          | (_buffer[_pos + 2] << 8)
                          |  _buffer[_pos + 3]);
            _pos += 4;
            return v;
        }

        /// <summary>
        /// Uniform integer in [0, exclusiveMax) with NO modulo bias. Values are drawn and any that
        /// fall in the final partial block of the 2^32 range are rejected, so every outcome is
        /// equally likely — no card position is ever favoured.
        /// </summary>
        public int NextIndex(int exclusiveMax)
        {
            if (exclusiveMax <= 0)
                throw new ArgumentOutOfRangeException(nameof(exclusiveMax), "Must be positive.");

            ulong n = (ulong)exclusiveMax;
            ulong limit = (0x1_0000_0000UL / n) * n; // largest multiple of n that fits in 2^32
            ulong x;
            do { x = NextUInt32(); } while (x >= limit);
            return (int)(x % n);
        }
    }
}
