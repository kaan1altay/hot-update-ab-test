using System;
using System.Text;

namespace HotUpdateABTest.Core.Hashing
{
    /// <summary>
    /// MurmurHash3 x86_32, the hash every bucketing decision in this framework is built on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bucketing needs a hash that is <i>specified</i>, not merely available. The obvious candidate,
    /// <see cref="object.GetHashCode"/> on a string, is disqualified because it is not a contract: its
    /// documented guarantee is only that equal strings hash equally within a single execution of a single
    /// application. In this stack that abstraction leaks three ways. Mono and IL2CPP do not compute it
    /// identically, so the Editor and the shipped player would put the same user in different variants.
    /// The implementation may change between engine or runtime versions, silently rebucketing an entire
    /// population on an upgrade. And on modern .NET the seed is randomized per process, so it is not stable
    /// even across two launches of the same binary. Any of those quietly invalidates a running experiment.
    /// </para>
    /// <para>
    /// Among specified hashes the choice came down to FNV-1a and MurmurHash3, and it turns on avalanche.
    /// Layer keys here differ only by a short salt suffix, and FNV-1a diffuses a single differing byte
    /// weakly, so <c>hash(user + ":" + saltA)</c> and <c>hash(user + ":" + saltB)</c> stay correlated. That
    /// would defeat the entire point of layering: two layers would assign the same users to the same
    /// positions and their experiments would be confounded rather than independent. MurmurHash3 avalanches
    /// strongly, is roughly forty lines against a canonical reference implementation, and is what several
    /// production experimentation platforms bucket with.
    /// </para>
    /// <para>
    /// Determinism across platforms comes from hashing UTF-8 <i>bytes</i> rather than UTF-16 chars, reading
    /// blocks little-endian explicitly rather than reinterpreting memory, and wrapping every arithmetic
    /// operation in <c>unchecked</c>. The implementation is pinned by SMHasher's own verification procedure
    /// in the tests, so a well-meaning refactor that changes a single constant fails loudly.
    /// </para>
    /// </remarks>
    public static class Murmur3
    {
        private const uint C1 = 0xcc9e2d51u;
        private const uint C2 = 0x1b873593u;

        /// <summary>Hashes the UTF-8 encoding of <paramref name="key"/> with the given seed.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
        public static uint Hash32(string key, uint seed = 0u)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            return Hash32(Encoding.UTF8.GetBytes(key), seed);
        }

        /// <summary>Hashes <paramref name="data"/> with the given seed.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        public static uint Hash32(byte[] data, uint seed = 0u)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return Hash32(data, 0, data.Length, seed);
        }

        /// <summary>Hashes <paramref name="count"/> bytes of <paramref name="data"/> from <paramref name="offset"/>.</summary>
        public static uint Hash32(byte[] data, int offset, int count, uint seed)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (offset + count > data.Length)
                throw new ArgumentException("offset + count runs past the end of the buffer.", nameof(count));

            unchecked
            {
                uint h1 = seed;
                int blocks = count >> 2;

                // Body: whole 4-byte blocks, read little-endian on every platform.
                for (int i = 0; i < blocks; i++)
                {
                    int j = offset + (i << 2);
                    uint k1 = (uint)(data[j] | (data[j + 1] << 8) | (data[j + 2] << 16) | (data[j + 3] << 24));

                    k1 *= C1;
                    k1 = RotateLeft(k1, 15);
                    k1 *= C2;

                    h1 ^= k1;
                    h1 = RotateLeft(h1, 13);
                    h1 = (h1 * 5u) + 0xe6546b64u;
                }

                // Tail: the 0-3 bytes that did not fill a block.
                int tail = offset + (blocks << 2);
                uint t = 0u;
                switch (count & 3)
                {
                    case 3:
                        t ^= (uint)data[tail + 2] << 16;
                        goto case 2;
                    case 2:
                        t ^= (uint)data[tail + 1] << 8;
                        goto case 1;
                    case 1:
                        t ^= data[tail];
                        t *= C1;
                        t = RotateLeft(t, 15);
                        t *= C2;
                        h1 ^= t;
                        break;
                }

                h1 ^= (uint)count;
                return FMix32(h1);
            }
        }

        private static uint RotateLeft(uint x, int r)
        {
            unchecked { return (x << r) | (x >> (32 - r)); }
        }

        /// <summary>The finalization mix that gives the hash its avalanche.</summary>
        private static uint FMix32(uint h)
        {
            unchecked
            {
                h ^= h >> 16;
                h *= 0x85ebca6bu;
                h ^= h >> 13;
                h *= 0xc2b2ae35u;
                h ^= h >> 16;
                return h;
            }
        }
    }
}
