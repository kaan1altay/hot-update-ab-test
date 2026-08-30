using System;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Assignment
{
    /// <summary>How a user came to be in the arm they are in.</summary>
    public enum AssignmentSource
    {
        /// <summary>Computed from the hash. The normal path.</summary>
        Bucketed,

        /// <summary>Restored from a pin written when the user was first exposed.</summary>
        Pinned,

        /// <summary>Set by hand in the debug panel. Never counts as evidence.</summary>
        Forced
    }

    /// <summary>Why a user is in no experiment in a layer.</summary>
    /// <remarks>
    /// Enumerated rather than folded into a single "not assigned" because the debug panel needs to
    /// explain it. "You are not in the experiment" is a support ticket; "your bucket 7412 falls outside
    /// the experiment's allocation [0, 3000)" is an answer.
    /// </remarks>
    public enum NoAssignmentReason
    {
        /// <summary>The user is assigned. Not a reason at all.</summary>
        None,

        /// <summary>The config declares no such layer.</summary>
        UnknownLayer,

        /// <summary>No running experiment in the layer claims the user's bucket.</summary>
        OutsideAllocation,

        /// <summary>An experiment claimed the bucket, but the user does not match its audience.</summary>
        AudienceExcluded,

        /// <summary>An experiment claimed the user, but every one of its arms has weight zero.</summary>
        NoTrafficInVariants
    }

    /// <summary>The answer to "what should this user see on this surface".</summary>
    /// <remarks>
    /// <para>
    /// Deliberately carries the reasoning, not just the verdict. The bucket values, the source, and the
    /// reason for a non-assignment all end up on the debug panel and in the exposure record, and having
    /// them here means nothing downstream has to recompute a hash to explain itself.
    /// </para>
    /// <para>
    /// An <see cref="Assignment"/> is not an exposure. Producing one is free and silent, and calling code
    /// may do it speculatively - to warm a screen, to render a diagnostic, to simulate a population.
    /// Nothing is logged until somebody actually sees the treated surface.
    /// </para>
    /// </remarks>
    public sealed class Assignment
    {
        /// <summary>The layer this answer is about. Always set.</summary>
        public string LayerId { get; }

        /// <summary>The experiment the user is in, or null when they are in none.</summary>
        public ExperimentDef Experiment { get; }

        /// <summary>The arm the user is in, or null when they are in none.</summary>
        public VariantDef Variant { get; }

        /// <summary>How the arm was chosen.</summary>
        public AssignmentSource Source { get; }

        /// <summary>Why there is no arm, when there is not.</summary>
        public NoAssignmentReason Reason { get; }

        /// <summary>Human-readable detail behind <see cref="Reason"/>, for the debug panel.</summary>
        public string Explanation { get; }

        /// <summary>The user's bucket within the layer.</summary>
        public int LayerBucket { get; }

        /// <summary>The user's bucket within the experiment, or -1 when they are in none.</summary>
        public int VariantBucket { get; }

        /// <summary>The config version this answer was computed against.</summary>
        public string ConfigVersion { get; }

        /// <summary>True when the user is in an experiment and an arm of it.</summary>
        public bool IsAssigned => Variant != null;

        /// <summary>True when this came from the debug panel rather than from the hash.</summary>
        /// <remarks>
        /// Everything downstream keys off this. A forced session's exposures are flagged so they can be
        /// excluded from analysis - a QA override that quietly polluted the results would be worse than no
        /// override at all.
        /// </remarks>
        public bool IsForced => Source == AssignmentSource.Forced;

        /// <summary>Shortcut for the experiment id, or null.</summary>
        public string ExperimentId => Experiment?.Id;

        /// <summary>Shortcut for the variant id, or null.</summary>
        public string VariantId => Variant?.Id;

        private Assignment(
            string layerId,
            ExperimentDef experiment,
            VariantDef variant,
            AssignmentSource source,
            NoAssignmentReason reason,
            string explanation,
            int layerBucket,
            int variantBucket,
            string configVersion)
        {
            LayerId = layerId;
            Experiment = experiment;
            Variant = variant;
            Source = source;
            Reason = reason;
            Explanation = explanation;
            LayerBucket = layerBucket;
            VariantBucket = variantBucket;
            ConfigVersion = configVersion;
        }

        /// <summary>The user is in an arm.</summary>
        public static Assignment Assigned(
            string layerId,
            ExperimentDef experiment,
            VariantDef variant,
            AssignmentSource source,
            int layerBucket,
            int variantBucket,
            string configVersion)
        {
            return new Assignment(layerId, experiment, variant, source, NoAssignmentReason.None, null,
                layerBucket, variantBucket, configVersion);
        }

        /// <summary>The user is in no experiment in this layer.</summary>
        public static Assignment NotAssigned(
            string layerId,
            NoAssignmentReason reason,
            string explanation,
            int layerBucket,
            string configVersion)
        {
            return new Assignment(layerId, null, null, AssignmentSource.Bucketed, reason, explanation,
                layerBucket, -1, configVersion);
        }

        /// <summary>One line for the debug panel.</summary>
        public string Describe()
        {
            if (IsAssigned)
            {
                return LayerId + ": " + Experiment.Id + " / " + Variant.Id + " (" +
                       Source.ToString().ToLowerInvariant() + ", layer bucket " + LayerBucket +
                       ", variant bucket " + VariantBucket + ")";
            }

            return LayerId + ": no experiment (" + Explanation + ", layer bucket " + LayerBucket + ")";
        }

        /// <inheritdoc />
        public override string ToString() => Describe();
    }
}
