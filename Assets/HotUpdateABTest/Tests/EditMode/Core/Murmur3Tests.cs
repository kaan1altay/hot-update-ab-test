using System.Text;
using HotUpdateABTest.Core.Hashing;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Pins the hash implementation. Every bucketing decision the framework makes is downstream of this, so
    /// a change here silently rebuckets every user in every experiment - exactly the failure these tests
    /// exist to make loud.
    /// </summary>
    [TestFixture]
    public sealed class Murmur3Tests
    {
        /// <summary>
        /// SMHasher's verification value for MurmurHash3 x86_32.
        /// </summary>
        /// <remarks>
        /// This is the authoritative self-test that ships with the reference implementation, and it is used
        /// here in preference to a handful of remembered string vectors. It hashes 256 keys of increasing
        /// length with decreasing seeds, then hashes the concatenated results, so a single wrong constant,
        /// a wrong rotation, a big-endian block read or a mishandled tail length all change it. Getting this
        /// one number right is strong evidence the whole implementation is right.
        /// </remarks>
        private const uint SmHasherVerificationValue = 0xB0F57EE3u;

        [Test]
        public void TheImplementationMatchesSmHashersVerificationValue()
        {
            const int hashBytes = 4;
            var key = new byte[256];
            var hashes = new byte[256 * hashBytes];

            for (int i = 0; i < 256; i++)
            {
                key[i] = (byte)i;

                // Key of length i, seeded with 256 - i, exactly as the reference harness does it.
                uint h = Murmur3.Hash32(key, 0, i, (uint)(256 - i));

                hashes[(i * 4) + 0] = (byte)h;
                hashes[(i * 4) + 1] = (byte)(h >> 8);
                hashes[(i * 4) + 2] = (byte)(h >> 16);
                hashes[(i * 4) + 3] = (byte)(h >> 24);
            }

            uint verification = Murmur3.Hash32(hashes, 0, hashes.Length, 0u);

            Assert.That(verification, Is.EqualTo(SmHasherVerificationValue),
                "MurmurHash3 x86_32 no longer matches the reference implementation. Every existing " +
                "bucket assignment would move. Expected 0x" + SmHasherVerificationValue.ToString("X8") +
                ", got 0x" + verification.ToString("X8") + ".");
        }

        [Test]
        public void TheEmptyStringWithSeedZeroHashesToZero()
        {
            Assert.That(Murmur3.Hash32(string.Empty), Is.EqualTo(0u));
        }

        [Test]
        public void KnownReferenceVectorsStillHold()
        {
            // Short inputs that exercise the tail path at lengths one and three.
            Assert.That(Murmur3.Hash32("a"), Is.EqualTo(0x3C2569B2u), "vector for \"a\"");
            Assert.That(Murmur3.Hash32("abc"), Is.EqualTo(0xB3DD93FAu), "vector for \"abc\"");
        }

        [Test]
        public void HashingIsRepeatableWithinAndAcrossCalls()
        {
            const string key = "user-4711:offer_layout.2026q3";
            uint first = Murmur3.Hash32(key);

            for (int i = 0; i < 100; i++)
            {
                Assert.That(Murmur3.Hash32(key), Is.EqualTo(first));
            }
        }

        [Test]
        public void TheStringOverloadHashesTheUtf8Encoding()
        {
            // The string overload must not hash UTF-16 code units: doing so would make the result depend on
            // the runtime's internal string representation rather than on the characters.
            const string key = "kullanici-çök";

            Assert.That(Murmur3.Hash32(key), Is.EqualTo(Murmur3.Hash32(Encoding.UTF8.GetBytes(key))));
        }

        [Test]
        public void DifferentSeedsProduceDifferentHashesForTheSameInput()
        {
            Assert.That(Murmur3.Hash32("user-1", 0u), Is.Not.EqualTo(Murmur3.Hash32("user-1", 1u)));
        }

        [Test]
        public void AOneByteChangeChangesRoughlyHalfTheOutputBits()
        {
            // The avalanche property is why MurmurHash3 was chosen over FNV-1a: layer salts differ by only a
            // few bytes, and weak diffusion there would leave layers correlated. Averaged over many pairs
            // the Hamming distance between hashes of adjacent inputs should sit near 16 of 32 bits.
            long totalBitsDiffering = 0;
            const int samples = 2000;

            for (int i = 0; i < samples; i++)
            {
                uint a = Murmur3.Hash32("user-" + i + ":layer.a");
                uint b = Murmur3.Hash32("user-" + i + ":layer.b");
                totalBitsDiffering += CountBits(a ^ b);
            }

            double meanBitsDiffering = totalBitsDiffering / (double)samples;

            Assert.That(meanBitsDiffering, Is.EqualTo(16.0).Within(0.5),
                "Mean Hamming distance between hashes of one-byte-apart keys was " + meanBitsDiffering +
                "; a value far from 16 means poor avalanche and correlated layers.");
        }

        [Test]
        public void HashingARangeMatchesHashingTheEquivalentStandaloneBuffer()
        {
            var padded = Encoding.UTF8.GetBytes("XXXXpayload-bytesYYYY");
            var exact = Encoding.UTF8.GetBytes("payload-bytes");

            Assert.That(Murmur3.Hash32(padded, 4, exact.Length, 0u), Is.EqualTo(Murmur3.Hash32(exact, 0u)));
        }

        [Test]
        public void EveryTailLengthIsHandled()
        {
            // Lengths 0..7 cover an empty input, all three tail widths, a whole block, and a block plus each
            // tail width. A mishandled `goto case` in the tail switch shows up here.
            var seen = new System.Collections.Generic.HashSet<uint>();

            for (int length = 0; length <= 7; length++)
            {
                var buffer = new byte[length];
                for (int i = 0; i < length; i++) buffer[i] = (byte)(i + 1);

                uint hash = Murmur3.Hash32(buffer, 0u);
                Assert.That(seen.Add(hash), Is.True, "length " + length + " collided with a shorter input");
            }
        }

        [Test]
        public void NullInputIsRejectedRatherThanTreatedAsEmpty()
        {
            Assert.That(() => Murmur3.Hash32((string)null), Throws.ArgumentNullException);
            Assert.That(() => Murmur3.Hash32((byte[])null), Throws.ArgumentNullException);
        }

        [Test]
        public void AReadPastTheEndOfTheBufferIsRejected()
        {
            var buffer = new byte[4];

            Assert.That(() => Murmur3.Hash32(buffer, 2, 4, 0u), Throws.ArgumentException);
            Assert.That(() => Murmur3.Hash32(buffer, -1, 1, 0u), Throws.InstanceOf<System.ArgumentOutOfRangeException>());
        }

        private static int CountBits(uint value)
        {
            int count = 0;
            while (value != 0)
            {
                value &= value - 1;
                count++;
            }

            return count;
        }
    }
}
