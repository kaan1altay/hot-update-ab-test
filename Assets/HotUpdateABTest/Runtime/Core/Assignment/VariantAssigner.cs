using System;
using HotUpdateABTest.Core.Hashing;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Assignment
{
    /// <summary>
    /// Stage two of assignment: decides which arm of an experiment a user gets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user is hashed a second time, with the experiment's own id and salt rather than the layer's, and
    /// the resulting bucket is walked against the cumulative weights of the arms in declared order. Using a
    /// second, independent hash is the point: if the layer bucket were reused, a user's position inside the
    /// experiment's allocation range would decide their arm, so widening the range from twenty to fifty
    /// percent of the layer would reshuffle the arms of everyone already in it. Traffic share and variant
    /// split need to be knobs an operator can turn one at a time.
    /// </para>
    /// <para>
    /// Weights are shares rather than percentages and are compared in integer arithmetic. Scaling each
    /// cumulative weight into bucket space and rounding would leave rounding error at the seams, so the
    /// comparison is cross-multiplied instead: the user falls in the arm where
    /// <c>bucket / BucketCount &lt; cumulativeWeight / totalWeight</c>. Both sides are widened to
    /// <see cref="long"/> first, because with ten thousand buckets a total weight above about 214,748 would
    /// otherwise overflow a 32-bit multiply and silently mis-assign.
    /// </para>
    /// <para>
    /// Declared order is part of the contract, not an implementation detail. Reordering the arms in config
    /// moves the boundaries and rebuckets users, so the validator treats variant order as significant and
    /// the README says so.
    /// </para>
    /// </remarks>
    public static class VariantAssigner
    {
        /// <summary>The bucket <paramref name="userId"/> occupies within <paramref name="experiment"/>.</summary>
        public static int BucketOf(string userId, ExperimentDef experiment)
        {
            if (experiment == null) throw new ArgumentNullException(nameof(experiment));
            return BucketSpace.Of(BucketSpace.VariantKey(userId, experiment.Id, experiment.Salt));
        }

        /// <summary>
        /// The arm <paramref name="userId"/> is assigned to, or null when the experiment can assign nobody
        /// because every weight is zero.
        /// </summary>
        public static VariantDef Assign(ExperimentDef experiment, string userId)
        {
            if (experiment == null) throw new ArgumentNullException(nameof(experiment));
            if (userId == null) throw new ArgumentNullException(nameof(userId));

            return AssignAt(experiment, BucketOf(userId, experiment));
        }

        /// <summary>
        /// The arm claiming <paramref name="bucket"/>, or null when every weight is zero.
        /// </summary>
        /// <remarks>
        /// Exposed so tests can sweep every bucket in the space and assert the arms partition it exactly -
        /// no gap, no overlap - which is a stronger statement than sampling users and checking the split is
        /// close to the weights.
        /// </remarks>
        public static VariantDef AssignAt(ExperimentDef experiment, int bucket)
        {
            if (experiment == null) throw new ArgumentNullException(nameof(experiment));

            long total = experiment.TotalWeight;

            // Every arm has weight zero: the experiment is live but holds no traffic. Returning null rather
            // than picking the first arm keeps the caller honest - there is no assignment to make here, and
            // silently inventing one would put users in an arm the operator explicitly emptied.
            if (total <= 0) return null;

            var variants = experiment.Variants;
            long cumulative = 0;

            for (int i = 0; i < variants.Count; i++)
            {
                var variant = variants[i];
                if (variant.Weight <= 0) continue;

                cumulative += variant.Weight;

                // bucket / BucketCount < cumulative / total, cross-multiplied to stay in integers.
                if ((long)bucket * total < cumulative * BucketSpace.BucketCount) return variant;
            }

            // Unreachable while total > 0: the final cumulative equals total, and bucket is strictly less
            // than BucketCount, so the comparison above must have succeeded. Kept as a guard rather than an
            // exception because a live experiment is not the place to throw over an arithmetic invariant.
            for (int i = variants.Count - 1; i >= 0; i--)
            {
                if (variants[i].Weight > 0) return variants[i];
            }

            return null;
        }
    }
}
