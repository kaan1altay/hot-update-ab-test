using System;

namespace HotUpdateABTest.Core.Model
{
    /// <summary>One arm of an experiment, as declared by the server.</summary>
    /// <remarks>
    /// <para>
    /// A variant is a name, a share of the experiment's traffic, and the key of the Lua behavior that
    /// decides what it presents. The definition is server-owned and the behavior is hot-updatable, which is
    /// the seam that lets a Lua patch add a working new arm to a running experiment: the server declares
    /// <c>{ id, weight, behavior }</c> and the patch supplies the function that <c>behavior</c> names.
    /// </para>
    /// <para>
    /// <see cref="Weight"/> is a share, not a percentage. Weights are summed and each variant gets its
    /// proportion of the total, so <c>1/1</c> and <c>5000/5000</c> both mean an even split and an operator
    /// never has to make a column add up to a round number.
    /// </para>
    /// </remarks>
    public sealed class VariantDef
    {
        /// <summary>The conventional id of the control arm. Every experiment must declare one.</summary>
        public const string ControlId = "control";

        /// <summary>Identifier, unique within its experiment.</summary>
        public string Id { get; }

        /// <summary>This arm's share of the experiment's traffic. Never negative.</summary>
        public int Weight { get; }

        /// <summary>Key of the Lua behavior that decides what this arm presents.</summary>
        public string Behavior { get; }

        /// <summary>True when this is the control arm.</summary>
        public bool IsControl => string.Equals(Id, ControlId, StringComparison.Ordinal);

        /// <summary>Creates a variant definition.</summary>
        public VariantDef(string id, int weight, string behavior)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Weight = weight;
            Behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        }

        /// <inheritdoc />
        public override string ToString() => Id + " (w=" + Weight + ", behavior=" + Behavior + ")";
    }
}
