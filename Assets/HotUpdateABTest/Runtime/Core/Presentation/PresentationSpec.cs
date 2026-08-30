using System;

namespace HotUpdateABTest.Core.Presentation
{
    /// <summary>How the offer list is arranged.</summary>
    /// <remarks>
    /// Every value here must have an arrangement authored in the FairyGUI package. Accepting a value
    /// nothing was drawn for would let a patch produce a <i>valid</i> spec the screen cannot render, which
    /// is just validation passing the buck to the renderer.
    /// </remarks>
    public enum OfferLayout
    {
        /// <summary>One offer per row, full width. The baseline.</summary>
        List,

        /// <summary>Two offers per row.</summary>
        Grid
    }

    /// <summary>How a price is presented.</summary>
    /// <remarks>
    /// Presentation only. Lua never sets a price or a discount - see <see cref="PresentationSpec"/>.
    /// </remarks>
    public enum PriceStyle
    {
        /// <summary>The current price alone. The baseline.</summary>
        Plain,

        /// <summary>The catalogue's original price struck through, beside the current price.</summary>
        Discounted
    }

    /// <summary>
    /// The complete, validated description of how the shop screen should present itself.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the entire vocabulary a hot update has.</b> A Lua behavior returns a table, it is
    /// validated field by field against this closed set, and the result is applied. Lua chooses among
    /// presentations the screen can already render; it cannot invent UI, and there is deliberately no
    /// escape hatch - no free-form property bag, no passthrough, no "extra" table. That is the honest limit
    /// of what a patch can do, and this type is where it stops being a promise in a README.</para>
    ///
    /// <para><b>Lua returns data, not commands.</b> Nothing here is a callback, a handler or an object
    /// reference. A behavior cannot touch a <c>GObject</c>, so a bad patch produces a rejected spec rather
    /// than a corrupted UI tree, and a variant's behaviour can be tested headless with no UI at all.</para>
    ///
    /// <para><b>Lua sets no prices.</b> <see cref="PriceStyle.Discounted"/> means "present the catalogue's
    /// existing original price struck through", not "apply a discount". Whoever can push a patch can run
    /// code on every device, and letting that channel change what things cost would be an unforced error.
    /// The numbers stay in C#, with the offer catalogue.</para>
    ///
    /// <para><b>Fields are owned by layer.</b> A behavior may only set the fields belonging to its own
    /// layer's group - see <see cref="SpecFieldGroup"/>. Two experiments running concurrently in different
    /// layers therefore compose without either being able to overwrite the other, and a behavior reaching
    /// outside its group is a validation error rather than a race.</para>
    /// </remarks>
    public readonly struct PresentationSpec : IEquatable<PresentationSpec>
    {
        /// <summary>Longest badge text the screen has room for.</summary>
        public const int MaxBadgeLength = 16;

        /// <summary>Longest call-to-action text the button has room for.</summary>
        public const int MaxCtaLength = 24;

        /// <summary>What the screen renders with no experiment applied at all.</summary>
        /// <remarks>
        /// Also the fallback whenever a spec is rejected. Control is defined in C# rather than in Lua on
        /// purpose: the state the framework retreats to must not itself be hot-updatable, or a bad patch
        /// could take the fallback down with it.
        /// </remarks>
        public static readonly PresentationSpec Baseline =
            new PresentationSpec(OfferLayout.List, PriceStyle.Plain, null, "Buy");

        /// <summary>How the offer list is arranged. Group <see cref="SpecFieldGroup.Layout"/>.</summary>
        public OfferLayout Layout { get; }

        /// <summary>How prices are presented. Group <see cref="SpecFieldGroup.Pricing"/>.</summary>
        public PriceStyle PriceStyle { get; }

        /// <summary>
        /// Badge text, or null for no badge. Group <see cref="SpecFieldGroup.Pricing"/>.
        /// </summary>
        public string BadgeText { get; }

        /// <summary>Call-to-action text. Group <see cref="SpecFieldGroup.Pricing"/>.</summary>
        public string CtaText { get; }

        /// <summary>True when a badge should be shown.</summary>
        public bool HasBadge => !string.IsNullOrEmpty(BadgeText);

        /// <summary>Creates a spec.</summary>
        public PresentationSpec(OfferLayout layout, PriceStyle priceStyle, string badgeText, string ctaText)
        {
            Layout = layout;
            PriceStyle = priceStyle;
            BadgeText = string.IsNullOrEmpty(badgeText) ? null : badgeText;
            CtaText = ctaText;
        }

        /// <summary>This spec with <see cref="Layout"/> replaced.</summary>
        public PresentationSpec WithLayout(OfferLayout layout) =>
            new PresentationSpec(layout, PriceStyle, BadgeText, CtaText);

        /// <summary>This spec with <see cref="PriceStyle"/> replaced.</summary>
        public PresentationSpec WithPriceStyle(PriceStyle priceStyle) =>
            new PresentationSpec(Layout, priceStyle, BadgeText, CtaText);

        /// <summary>This spec with <see cref="BadgeText"/> replaced.</summary>
        public PresentationSpec WithBadgeText(string badgeText) =>
            new PresentationSpec(Layout, PriceStyle, badgeText, CtaText);

        /// <summary>This spec with <see cref="CtaText"/> replaced.</summary>
        public PresentationSpec WithCtaText(string ctaText) =>
            new PresentationSpec(Layout, PriceStyle, BadgeText, ctaText);

        /// <inheritdoc />
        public bool Equals(PresentationSpec other) =>
            Layout == other.Layout &&
            PriceStyle == other.PriceStyle &&
            string.Equals(BadgeText, other.BadgeText, StringComparison.Ordinal) &&
            string.Equals(CtaText, other.CtaText, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is PresentationSpec other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Layout;
                hash = (hash * 397) ^ (int)PriceStyle;
                hash = (hash * 397) ^ (BadgeText == null ? 0 : StringComparer.Ordinal.GetHashCode(BadgeText));
                hash = (hash * 397) ^ (CtaText == null ? 0 : StringComparer.Ordinal.GetHashCode(CtaText));
                return hash;
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            "layout=" + Layout.ToString().ToLowerInvariant() +
            " price=" + PriceStyle.ToString().ToLowerInvariant() +
            " badge=" + (BadgeText ?? "(none)") +
            " cta='" + CtaText + "'";
    }

    /// <summary>
    /// Which fields of the spec one layer's behaviors are allowed to set.
    /// </summary>
    /// <remarks>
    /// The demo runs two concurrent experiments in two layers, and the layer story is only honest if they
    /// genuinely cannot fight. Rather than resolving conflicts by precedence - which would mean one layer
    /// silently losing, and the loser's experiment measuring nothing - each layer owns a disjoint group of
    /// fields, and a behavior that writes outside its group has its whole spec rejected.
    ///
    /// Which layer drives which group is demo policy and lives with the screen, not here: the framework's
    /// rule is that the mapping exists and is enforced, not what it happens to be.
    /// </remarks>
    public enum SpecFieldGroup
    {
        /// <summary>Arrangement of the offer list. Field: <c>layout</c>.</summary>
        Layout,

        /// <summary>Price presentation and call to action. Fields: <c>priceStyle</c>, <c>badgeText</c>, <c>ctaText</c>.</summary>
        Pricing
    }

    /// <summary>The names of the spec's fields as Lua writes them, and which group each belongs to.</summary>
    public static class SpecFields
    {
        /// <summary>Key for <see cref="PresentationSpec.Layout"/>.</summary>
        public const string Layout = "layout";

        /// <summary>Key for <see cref="PresentationSpec.PriceStyle"/>.</summary>
        public const string PriceStyle = "priceStyle";

        /// <summary>Key for <see cref="PresentationSpec.BadgeText"/>.</summary>
        public const string BadgeText = "badgeText";

        /// <summary>Key for <see cref="PresentationSpec.CtaText"/>.</summary>
        public const string CtaText = "ctaText";

        private static readonly string[] LayoutGroup = { Layout };
        private static readonly string[] PricingGroup = { PriceStyle, BadgeText, CtaText };
        private static readonly string[] All = { Layout, PriceStyle, BadgeText, CtaText };

        /// <summary>Every field name the spec understands.</summary>
        public static string[] Names => All;

        /// <summary>The fields belonging to one group.</summary>
        public static string[] For(SpecFieldGroup group) =>
            group == SpecFieldGroup.Layout ? LayoutGroup : PricingGroup;

        /// <summary>The group a field belongs to, or null when the name is not a field at all.</summary>
        public static SpecFieldGroup? GroupOf(string field)
        {
            switch (field)
            {
                case Layout: return SpecFieldGroup.Layout;
                case PriceStyle:
                case BadgeText:
                case CtaText: return SpecFieldGroup.Pricing;
                default: return null;
            }
        }
    }
}
