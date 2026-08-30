using System;

namespace HotUpdateABTest.Core.Model
{
    /// <summary>
    /// A layer: one surface of the product that experiments compete for, and the salt that keeps it
    /// statistically independent of every other layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layers are how two experiments can run at once without fighting. Experiments in the same layer are
    /// mutually exclusive because they claim disjoint slices of it; experiments in different layers apply
    /// to the same user simultaneously and independently.
    /// </para>
    /// <para>
    /// <see cref="Salt"/> is the load-bearing field and the easiest one to get wrong. Without it every
    /// layer would map user <c>U</c> to the same bucket, so an experiment holding <c>[0, 1000)</c> in one
    /// layer and an experiment holding <c>[0, 1000)</c> in another would have <i>identical</i> populations.
    /// The two would be perfectly confounded: you could no longer tell an interaction between them from a
    /// main effect of either, and running them concurrently - the whole reason layers exist - would stop
    /// meaning anything. Changing a layer's salt reshuffles everyone in it, so it is a deliberate act, not
    /// a cosmetic edit.
    /// </para>
    /// </remarks>
    public sealed class LayerDef
    {
        /// <summary>Identifier, unique within the config.</summary>
        public string Id { get; }

        /// <summary>Decorrelates this layer's bucketing from every other layer's.</summary>
        public string Salt { get; }

        /// <summary>Creates a layer definition.</summary>
        public LayerDef(string id, string salt)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Salt = salt ?? throw new ArgumentNullException(nameof(salt));
        }

        /// <inheritdoc />
        public override string ToString() => Id;
    }
}
