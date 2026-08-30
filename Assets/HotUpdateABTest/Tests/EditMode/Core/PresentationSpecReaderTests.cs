using System.Collections.Generic;
using HotUpdateABTest.Core.Presentation;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Pins the vocabulary a hot update has: a closed field set, closed value sets, and whole-table
    /// rejection.
    /// </summary>
    /// <remarks>
    /// These tests are the contract the FairyGUI package is authored against. If an assertion here changes,
    /// somebody has to draw something new, so they are deliberately explicit about which values exist.
    /// </remarks>
    [TestFixture]
    public sealed class PresentationSpecReaderTests
    {
        private static Dictionary<string, object> Table(params object[] pairs)
        {
            var table = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) table[(string)pairs[i]] = pairs[i + 1];
            return table;
        }

        private static SpecReadResult ReadPricing(Dictionary<string, object> table) =>
            PresentationSpecReader.Read(table, SpecFieldGroup.Pricing, PresentationSpec.Baseline);

        private static SpecReadResult ReadLayout(Dictionary<string, object> table) =>
            PresentationSpecReader.Read(table, SpecFieldGroup.Layout, PresentationSpec.Baseline);

        // --- the baseline ------------------------------------------------------------------------------

        [Test]
        public void TheBaselineIsWhatTheScreenRendersWithNoExperiment()
        {
            var baseline = PresentationSpec.Baseline;

            Assert.That(baseline.Layout, Is.EqualTo(OfferLayout.List));
            Assert.That(baseline.PriceStyle, Is.EqualTo(PriceStyle.Plain));
            Assert.That(baseline.HasBadge, Is.False);
            Assert.That(baseline.CtaText, Is.EqualTo("Buy"));
        }

        [Test]
        public void AnEmptyTableLeavesTheBaselineAlone()
        {
            var result = ReadPricing(Table());

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Spec, Is.EqualTo(PresentationSpec.Baseline));
        }

        [Test]
        public void FieldsTheBehaviorDoesNotMentionKeepTheirBaselineValue()
        {
            var result = ReadPricing(Table(SpecFields.CtaText, "Grab it"));

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Spec.CtaText, Is.EqualTo("Grab it"));
            Assert.That(result.Spec.PriceStyle, Is.EqualTo(PriceStyle.Plain));
            Assert.That(result.Spec.HasBadge, Is.False);
        }

        // --- every value that exists ----------------------------------------------------------------------

        [Test]
        public void EveryAuthoredLayoutIsAccepted()
        {
            Assert.That(ReadLayout(Table(SpecFields.Layout, "list")).Spec.Layout, Is.EqualTo(OfferLayout.List));
            Assert.That(ReadLayout(Table(SpecFields.Layout, "grid")).Spec.Layout, Is.EqualTo(OfferLayout.Grid));
        }

        [Test]
        public void EveryAuthoredPriceStyleIsAccepted()
        {
            Assert.That(ReadPricing(Table(SpecFields.PriceStyle, "plain")).Spec.PriceStyle,
                Is.EqualTo(PriceStyle.Plain));
            Assert.That(ReadPricing(Table(SpecFields.PriceStyle, "discounted")).Spec.PriceStyle,
                Is.EqualTo(PriceStyle.Discounted));
        }

        [Test]
        public void TheEnumeratedValuesAreExactlyWhatIsAuthored()
        {
            // This test exists to fail if somebody adds a value without drawing it. The set of accepted
            // values must equal the set of things that exist in the package, or validation is passing the
            // buck to the renderer.
            Assert.That(System.Enum.GetNames(typeof(OfferLayout)),
                Is.EquivalentTo(new[] { "List", "Grid" }));
            Assert.That(System.Enum.GetNames(typeof(PriceStyle)),
                Is.EquivalentTo(new[] { "Plain", "Discounted" }));
            Assert.That(SpecFields.Names,
                Is.EquivalentTo(new[] { "layout", "priceStyle", "badgeText", "ctaText" }));
        }

        [Test]
        public void ABadgeIsOptional()
        {
            Assert.That(ReadPricing(Table(SpecFields.BadgeText, "SALE")).Spec.BadgeText, Is.EqualTo("SALE"));
            Assert.That(ReadPricing(Table(SpecFields.BadgeText, null)).Spec.HasBadge, Is.False);
            Assert.That(ReadPricing(Table(SpecFields.BadgeText, "")).Spec.HasBadge, Is.False);
        }

        // --- the closed vocabulary ------------------------------------------------------------------------

        [Test]
        public void AnUnknownFieldIsRejectedRatherThanIgnored()
        {
            // Unlike the config reader, which ignores unknown fields so the server can add them. The
            // opposite rule applies here: a behavior asking for something the screen has never heard of is
            // a patch that will not render, and silently dropping the key would ship it anyway.
            var result = ReadPricing(Table(SpecFields.CtaText, "Buy", "particleEffect", "confetti"));

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Issues.Describe(),
                Does.Contain("'particleEffect' is not part of the presentation spec")
                    .And.Contain("closed set"));
        }

        [Test]
        public void AnUnknownEnumValueIsRejectedRatherThanDefaulted()
        {
            // The failure this rule prevents: a patch asks for a carousel nobody drew, validation quietly
            // renders a list, and the patch looks like it worked.
            var result = ReadLayout(Table(SpecFields.Layout, "carousel"));

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Spec, Is.EqualTo(PresentationSpec.Baseline), "and it falls back to control");
            Assert.That(result.Issues.Describe(),
                Does.Contain("'carousel' is not one the screen can render")
                    .And.Contain("'list' and 'grid'"));
        }

        [Test]
        public void WrongTypesAreRejectedAndSayWhatWasFound()
        {
            Assert.That(ReadLayout(Table(SpecFields.Layout, 7L)).Issues.Describe(),
                Does.Contain("expected a string, found the number 7"));
            Assert.That(ReadPricing(Table(SpecFields.CtaText, true)).Issues.Describe(),
                Does.Contain("expected a string, found the boolean true"));
        }

        [Test]
        public void ABehaviorReturningNothingIsRejected()
        {
            var result = PresentationSpecReader.Read(null, SpecFieldGroup.Pricing, PresentationSpec.Baseline);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Spec, Is.EqualTo(PresentationSpec.Baseline));
            Assert.That(result.Issues.Describe(), Does.Contain("must return a table"));
        }

        // --- layer ownership ---------------------------------------------------------------------------------

        [Test]
        public void ABehaviorMayNotSetAFieldBelongingToAnotherLayer()
        {
            // Two experiments running concurrently must not be able to overwrite each other. Resolving by
            // precedence instead would mean one layer silently losing, and its experiment measuring nothing.
            var result = ReadPricing(Table(SpecFields.Layout, "grid"));

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Issues.Describe(),
                Does.Contain("belongs to the layout group").And.Contain("owns the pricing group"));
        }

        [Test]
        public void TheGroupsAreDisjointAndCoverEveryField()
        {
            var layout = new List<string>(SpecFields.For(SpecFieldGroup.Layout));
            var pricing = new List<string>(SpecFields.For(SpecFieldGroup.Pricing));

            foreach (string field in layout) Assert.That(pricing, Does.Not.Contain(field));

            var union = new List<string>(layout);
            union.AddRange(pricing);
            Assert.That(union, Is.EquivalentTo(SpecFields.Names));
        }

        // --- whole-table rejection ------------------------------------------------------------------------------

        [Test]
        public void OneBadFieldRejectsTheWholeTable()
        {
            // A half-applied presentation is the visual equivalent of a half-applied config.
            var result = ReadPricing(Table(
                SpecFields.CtaText, "Grab it",
                SpecFields.PriceStyle, "discounted",
                SpecFields.BadgeText, "sideways".PadRight(40, '!')));

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Spec, Is.EqualTo(PresentationSpec.Baseline),
                "the good fields must not be applied either");
        }

        [Test]
        public void EveryProblemInATableIsReported()
        {
            var result = ReadPricing(Table(
                SpecFields.PriceStyle, "shiny",
                "unknownThing", 1L,
                SpecFields.CtaText, ""));

            Assert.That(result.Issues.ErrorCount, Is.EqualTo(3), result.Issues.Describe());
        }

        // --- text limits ---------------------------------------------------------------------------------------

        [Test]
        public void TextTooLongForTheScreenIsRejectedRatherThanTruncated()
        {
            // Silently clipping copy produces a screen that looks deliberate and reads as nonsense, and the
            // patch author never finds out.
            var result = ReadPricing(Table(SpecFields.CtaText, new string('x', PresentationSpec.MaxCtaLength + 1)));

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Issues.Describe(), Does.Contain("the screen has room for 24"));
        }

        [Test]
        public void TextExactlyAtTheLimitIsAccepted()
        {
            string cta = new string('x', PresentationSpec.MaxCtaLength);
            string badge = new string('y', PresentationSpec.MaxBadgeLength);

            var result = ReadPricing(Table(SpecFields.CtaText, cta, SpecFields.BadgeText, badge));

            Assert.That(result.IsValid, Is.True, result.Issues.Describe());
            Assert.That(result.Spec.CtaText, Is.EqualTo(cta));
        }

        [Test]
        public void TheCallToActionMayNotBeEmptyOrMissing()
        {
            // A badge can be absent; a button with no label cannot.
            Assert.That(ReadPricing(Table(SpecFields.CtaText, "")).IsValid, Is.False);
            Assert.That(ReadPricing(Table(SpecFields.CtaText, null)).IsValid, Is.False);
        }

        // --- composition ------------------------------------------------------------------------------------------

        [Test]
        public void TwoLayersComposeIntoOneScreen()
        {
            // How the demo actually renders: the layout layer's spec merged, then the pricing layer's.
            var afterLayout = PresentationSpecReader.Read(
                Table(SpecFields.Layout, "grid"), SpecFieldGroup.Layout, PresentationSpec.Baseline);

            var afterPricing = PresentationSpecReader.Read(
                Table(SpecFields.PriceStyle, "discounted", SpecFields.BadgeText, "-40%",
                    SpecFields.CtaText, "Claim offer"),
                SpecFieldGroup.Pricing, afterLayout.Spec);

            var final = afterPricing.Spec;

            Assert.That(final.Layout, Is.EqualTo(OfferLayout.Grid));
            Assert.That(final.PriceStyle, Is.EqualTo(PriceStyle.Discounted));
            Assert.That(final.BadgeText, Is.EqualTo("-40%"));
            Assert.That(final.CtaText, Is.EqualTo("Claim offer"));
        }

        [Test]
        public void ARejectedLayerDoesNotTakeTheOtherLayerDownWithIt()
        {
            var afterLayout = PresentationSpecReader.Read(
                Table(SpecFields.Layout, "grid"), SpecFieldGroup.Layout, PresentationSpec.Baseline);

            var afterPricing = PresentationSpecReader.Read(
                Table(SpecFields.PriceStyle, "nonsense"), SpecFieldGroup.Pricing, afterLayout.Spec);

            Assert.That(afterPricing.IsValid, Is.False);
            Assert.That(afterPricing.Spec.Layout, Is.EqualTo(OfferLayout.Grid),
                "the pricing layer falls back to the baseline it was given, which still carries the layout");
            Assert.That(afterPricing.Spec.PriceStyle, Is.EqualTo(PriceStyle.Plain));
        }

        [Test]
        public void EveryCombinationTheScreenMustRenderIsSmallEnoughToAuthor()
        {
            // 2 layouts x 2 price styles x badge present or absent = 8 arrangements, which is a morning's
            // work to draw. If this number ever climbs, the field list has outgrown what the demo needs to
            // make its point and something should be cut.
            int combinations = System.Enum.GetValues(typeof(OfferLayout)).Length *
                               System.Enum.GetValues(typeof(PriceStyle)).Length *
                               2;

            Assert.That(combinations, Is.LessThanOrEqualTo(8),
                "the presentation vocabulary has grown beyond what can be authored exhaustively");
        }
    }
}
