using System;
using HotUpdateABTest.Core.Hashing;

namespace HotUpdateABTest.Core.Model
{
    /// <summary>
    /// A half-open range <c>[From, To)</c> of the layer's bucket space, claimed by one experiment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the mechanism behind "a user is in at most one experiment per layer". Rather than assigning
    /// a user to experiments and then checking afterwards whether two of them collided, each running
    /// experiment owns a disjoint slice of its layer, validated when the config is accepted. Mutual
    /// exclusion is then structural: there is no runtime decision left that could get it wrong, because a
    /// bucket falls inside at most one range by construction.
    /// </para>
    /// <para>
    /// The range is half-open so that adjacent experiments can be written as <c>[0, 5000)</c> and
    /// <c>[5000, 10000)</c> without an off-by-one argument at the seam.
    /// </para>
    /// </remarks>
    public readonly struct BucketRange : IEquatable<BucketRange>
    {
        /// <summary>A range claiming no traffic at all.</summary>
        public static readonly BucketRange Empty = new BucketRange(0, 0);

        /// <summary>A range claiming the whole layer.</summary>
        public static readonly BucketRange Full = new BucketRange(0, BucketSpace.BucketCount);

        /// <summary>Inclusive lower bound.</summary>
        public int From { get; }

        /// <summary>Exclusive upper bound.</summary>
        public int To { get; }

        /// <summary>How many buckets this range claims.</summary>
        public int Width => To - From;

        /// <summary>True when the range claims no traffic.</summary>
        public bool IsEmpty => Width <= 0;

        /// <summary>Creates a range. Bounds are not validated here; see <c>ConfigValidator</c>.</summary>
        public BucketRange(int from, int to)
        {
            From = from;
            To = to;
        }

        /// <summary>True when <paramref name="bucket"/> falls inside this range.</summary>
        public bool Contains(int bucket) => bucket >= From && bucket < To;

        /// <summary>True when the two ranges share at least one bucket.</summary>
        public bool Overlaps(BucketRange other)
        {
            if (IsEmpty || other.IsEmpty) return false;
            return From < other.To && other.From < To;
        }

        /// <inheritdoc />
        public bool Equals(BucketRange other) => From == other.From && To == other.To;

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is BucketRange other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (From * 397) ^ To;

        /// <inheritdoc />
        public override string ToString() => "[" + From + ", " + To + ")";
    }
}
