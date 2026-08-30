using System;
using System.Collections.Generic;

namespace HotUpdateABTest.Core.Model
{
    /// <summary>One experiment, as declared by the server.</summary>
    /// <remarks>
    /// <para>
    /// An experiment owns a slice of exactly one layer (<see cref="Allocation"/>), splits the users who
    /// fall in that slice across its <see cref="Variants"/>, and is switched on or off by
    /// <see cref="Status"/>.
    /// </para>
    /// <para>
    /// <see cref="Salt"/> is separate from the layer's salt on purpose. The layer salt decides <i>which</i>
    /// experiment a user gets; this one decides <i>which arm</i> of it. Deriving both from one hash would
    /// couple them, so that widening an experiment's traffic share would simultaneously reshuffle the arms
    /// of everyone already in it - two operator actions welded into one. Keeping them apart costs a second
    /// hash per resolve and makes traffic ramp and variant split independent knobs.
    /// </para>
    /// </remarks>
    public sealed class ExperimentDef
    {
        private readonly VariantDef[] _variants;

        /// <summary>Identifier, unique within the config.</summary>
        public string Id { get; }

        /// <summary>Id of the layer this experiment competes in.</summary>
        public string LayerId { get; }

        /// <summary>Lifecycle state. Only <see cref="ExperimentStatus.Running"/> assigns anyone.</summary>
        public ExperimentStatus Status { get; }

        /// <summary>Decorrelates this experiment's variant split from its position in the layer.</summary>
        public string Salt { get; }

        /// <summary>The slice of the layer's bucket space this experiment claims.</summary>
        public BucketRange Allocation { get; }

        /// <summary>What happens to an assigned user when the weights change.</summary>
        public StickinessPolicy Stickiness { get; }

        /// <summary>The arms, in declared order. Order is part of the bucketing contract.</summary>
        public IReadOnlyList<VariantDef> Variants => _variants;

        /// <summary>True when this experiment is live and may assign users.</summary>
        public bool IsRunning => Status == ExperimentStatus.Running;

        /// <summary>Creates an experiment definition.</summary>
        public ExperimentDef(
            string id,
            string layerId,
            ExperimentStatus status,
            string salt,
            BucketRange allocation,
            StickinessPolicy stickiness,
            IEnumerable<VariantDef> variants)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            LayerId = layerId ?? throw new ArgumentNullException(nameof(layerId));
            Salt = salt ?? throw new ArgumentNullException(nameof(salt));
            Status = status;
            Allocation = allocation;
            Stickiness = stickiness;

            if (variants == null) throw new ArgumentNullException(nameof(variants));
            _variants = new List<VariantDef>(variants).ToArray();
        }

        /// <summary>Finds a variant by id, or returns null when this experiment has no such arm.</summary>
        /// <remarks>
        /// Used to decide whether a pinned or forced variant still exists in the current config. A pin
        /// naming an arm the server has since deleted must be discarded rather than honoured, or the
        /// framework would apply a variant that is not in the config it is running.
        /// </remarks>
        public VariantDef FindVariant(string variantId)
        {
            if (variantId == null) return null;
            for (int i = 0; i < _variants.Length; i++)
            {
                if (string.Equals(_variants[i].Id, variantId, StringComparison.Ordinal)) return _variants[i];
            }

            return null;
        }

        /// <summary>The control arm, or null when the experiment declares none.</summary>
        public VariantDef Control => FindVariant(VariantDef.ControlId);

        /// <summary>Sum of every arm's weight. Zero means the experiment can assign nobody.</summary>
        public long TotalWeight
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _variants.Length; i++) total += _variants[i].Weight;
                return total;
            }
        }

        /// <inheritdoc />
        public override string ToString() => Id + " [" + LayerId + ", " + Status + ", " + Allocation + "]";
    }
}
