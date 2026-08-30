using System;

namespace HotUpdateABTest.Core.Hashing
{
    /// <summary>
    /// Maps a bucketing key onto the fixed <c>[0, 10000)</c> space that layer allocation and variant
    /// assignment both draw from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ten thousand buckets means traffic can be expressed to one hundredth of a percent, which is finer
    /// than any experiment here needs and coarse enough that a range stays readable in a config file.
    /// </para>
    /// <para>
    /// Reducing a 32-bit hash modulo 10000 introduces bias, because 2^32 is not a multiple of 10000: the
    /// first 4096 buckets are reachable by one more hash value than the rest. The relative excess is about
    /// 2.3 x 10^-5, or roughly one extra user in forty thousand at the boundary. That is orders of magnitude
    /// below the noise in any real conversion metric, so rejection sampling to remove it would buy nothing
    /// and would cost the property that matters far more: a single hash call per decision, with no loop
    /// whose iteration count depends on the input.
    /// </para>
    /// </remarks>
    public static class BucketSpace
    {
        /// <summary>The number of buckets. Traffic ranges are expressed in these units.</summary>
        public const int BucketCount = 10000;

        /// <summary>Hashes <paramref name="key"/> into <c>[0, <see cref="BucketCount"/>)</c>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
        public static int Of(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            return (int)(Murmur3.Hash32(key) % (uint)BucketCount);
        }

        /// <summary>
        /// Builds the layer bucketing key. The layer's salt is what makes assignment in one layer
        /// statistically independent of every other layer; see <c>LayerAllocator</c> for why that matters.
        /// </summary>
        public static string LayerKey(string userId, string layerSalt)
        {
            if (userId == null) throw new ArgumentNullException(nameof(userId));
            if (layerSalt == null) throw new ArgumentNullException(nameof(layerSalt));
            return userId + ":" + layerSalt;
        }

        /// <summary>
        /// Builds the variant bucketing key. This is deliberately a different key from
        /// <see cref="LayerKey"/> so that an experiment's traffic share and its variant split are
        /// independent knobs; see <c>VariantAssigner</c>.
        /// </summary>
        public static string VariantKey(string userId, string experimentId, string experimentSalt)
        {
            if (userId == null) throw new ArgumentNullException(nameof(userId));
            if (experimentId == null) throw new ArgumentNullException(nameof(experimentId));
            if (experimentSalt == null) throw new ArgumentNullException(nameof(experimentSalt));
            return userId + ":" + experimentId + ":" + experimentSalt;
        }
    }
}
