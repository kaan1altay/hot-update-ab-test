using System;
using HotUpdateABTest.Core.Hashing;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Assignment
{
    /// <summary>
    /// Stage one of assignment: decides which experiment in a layer, if any, a user belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The user is hashed once per layer into <c>[0, 10000)</c> using the layer's salt, and the running
    /// experiment whose allocation range contains that bucket wins. Because the validator guarantees the
    /// ranges are disjoint, at most one can contain it - so "a user is in at most one experiment per layer"
    /// is a property of the data, not a rule this code has to enforce and could forget to. If no range
    /// contains the bucket the user is in no experiment in that layer, which is how a holdout or a partial
    /// traffic ramp is expressed.
    /// </para>
    /// <para>
    /// This class deliberately does not look at variants, audiences, pins or overrides. It answers one
    /// question, and keeping it that narrow is what makes cross-layer independence testable in isolation.
    /// </para>
    /// </remarks>
    public static class LayerAllocator
    {
        /// <summary>
        /// The bucket <paramref name="userId"/> occupies in <paramref name="layer"/>. Stable for the life
        /// of the layer's salt, and independent of every other layer's bucket for the same user.
        /// </summary>
        public static int BucketOf(string userId, LayerDef layer)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            return BucketSpace.Of(BucketSpace.LayerKey(userId, layer.Salt));
        }

        /// <summary>
        /// The running experiment in <paramref name="layer"/> that <paramref name="userId"/> falls into, or
        /// null when the user falls outside every running experiment's allocation.
        /// </summary>
        /// <remarks>
        /// Non-running experiments are skipped before their ranges are consulted, so pausing an experiment
        /// frees its traffic immediately: the users who were in it fall through to whichever other running
        /// experiment claims their bucket, or to no experiment at all.
        /// </remarks>
        public static ExperimentDef Allocate(ExperimentConfig config, LayerDef layer, string userId)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (userId == null) throw new ArgumentNullException(nameof(userId));

            int bucket = BucketOf(userId, layer);
            return AllocateAt(config, layer, bucket);
        }

        /// <summary>
        /// The running experiment in <paramref name="layer"/> claiming <paramref name="bucket"/>, or null.
        /// </summary>
        /// <remarks>
        /// Split out from <see cref="Allocate"/> so tests can sweep the whole bucket space directly and
        /// assert that no bucket is ever claimed twice, without having to find user ids that land on it.
        /// </remarks>
        public static ExperimentDef AllocateAt(ExperimentConfig config, LayerDef layer, int bucket)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (layer == null) throw new ArgumentNullException(nameof(layer));

            var experiments = config.Experiments;
            for (int i = 0; i < experiments.Count; i++)
            {
                var experiment = experiments[i];
                if (!experiment.IsRunning) continue;
                if (!string.Equals(experiment.LayerId, layer.Id, StringComparison.Ordinal)) continue;
                if (experiment.Allocation.Contains(bucket)) return experiment;
            }

            return null;
        }
    }
}
