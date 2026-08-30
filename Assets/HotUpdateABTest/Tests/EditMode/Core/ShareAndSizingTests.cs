using HotUpdateABTest.Core.Presentation;
using HotUpdateABTest.Core.Telemetry;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers the two numbers the share bar renders, and the text limits the card is drawn to hold.
    /// </summary>
    [TestFixture]
    public sealed class ShareAndSizingTests
    {
        private static VariantMetrics Arm(TelemetryHarness harness, string variantId)
        {
            return harness.Arm("exp_offer_layout", variantId);
        }

        [Test]
        public void ObservedShareIsTheArmsFractionOfTheExposedPopulation()
        {
            var harness = new TelemetryHarness();
            harness.SimulateUsers(4000);

            double control = Arm(harness, "control").ObservedShare;
            double treatment = Arm(harness, "treatment").ObservedShare;

            Assert.That(control + treatment, Is.EqualTo(1.0).Within(0.0001));
            Assert.That(control, Is.EqualTo(0.5).Within(0.05));
        }

        [Test]
        public void ExpectedShareComesFromTheConfiguredWeights()
        {
            var harness = new TelemetryHarness(ConfigJson.New("1")
                .Layer("offer_layout")
                .Layer("pricing_cta")
                .Experiment("exp_offer_layout", "offer_layout", variants: new[]
                {
                    ConfigJson.Variant("control", 9000),
                    ConfigJson.Variant("treatment", 1000)
                })
                .Experiment("exp_pricing_cta", "pricing_cta")
                .Build());

            harness.SimulateUsers(2000);

            Assert.That(Arm(harness, "control").ExpectedShare, Is.EqualTo(0.9).Within(0.0001));
            Assert.That(Arm(harness, "treatment").ExpectedShare, Is.EqualTo(0.1).Within(0.0001));
            Assert.That(Arm(harness, "control").ObservedShare, Is.EqualTo(0.9).Within(0.03),
                "a healthy run should observe roughly what it configured");
        }

        [Test]
        public void AZeroWeightArmIsNotPartOfTheExpectedSplit()
        {
            // An arm the operator emptied is not in the ratio, and dividing by a total that included it
            // would make every other arm look under-represented.
            var harness = new TelemetryHarness(ConfigJson.New("1")
                .Layer("offer_layout")
                .Layer("pricing_cta")
                .Experiment("exp_offer_layout", "offer_layout", variants: new[]
                {
                    ConfigJson.Variant("control", 5000),
                    ConfigJson.Variant("treatment", 5000),
                    ConfigJson.Variant("retired", 0)
                })
                .Experiment("exp_pricing_cta", "pricing_cta")
                .Build());

            harness.SimulateUsers(1000);

            Assert.That(Arm(harness, "control").ExpectedShare, Is.EqualTo(0.5).Within(0.0001));
            Assert.That(Arm(harness, "retired").ExpectedShare, Is.Zero);
        }

        [Test]
        public void SharesAreZeroBeforeAnybodyIsExposed()
        {
            var harness = new TelemetryHarness();

            Assert.That(Arm(harness, "control").ObservedShare, Is.Zero);
            Assert.That(Arm(harness, "control").ExpectedShare, Is.EqualTo(0.5).Within(0.0001),
                "expected share is a property of the config and is known before any traffic");
        }

        // --- text limits are load-bearing ---------------------------------------------------------------

        [Test]
        public void TheTextLimitsAreTheLengthsTheCardIsDrawnToHold()
        {
            // Because the reader rejects rather than truncates, whatever these constants say is guaranteed
            // to arrive on screen. They are therefore a statement about what the authored card can hold at
            // a legible size, not a safety margin. Sixteen was tried for the badge and does not fit beside
            // the offer name on a 335-wide card; ten does, and "BEST VALUE" is exactly ten.
            Assert.That(PresentationSpec.MaxBadgeLength, Is.EqualTo(10));
            Assert.That(PresentationSpec.MaxCtaLength, Is.EqualTo(24));
            Assert.That("BEST VALUE".Length, Is.EqualTo(PresentationSpec.MaxBadgeLength));
        }

        [Test]
        public void ABadgeLongerThanTheCardHoldsIsRejectedRatherThanClipped()
        {
            var table = new System.Collections.Generic.Dictionary<string, object>
            {
                { SpecFields.BadgeText, new string('x', PresentationSpec.MaxBadgeLength + 1) }
            };

            var result = PresentationSpecReader.Read(
                table, SpecFieldGroup.Pricing, PresentationSpec.Baseline);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Issues.Describe(), Does.Contain("the screen has room for 10"));
        }

        [Test]
        public void EveryShippedBehaviorProducesTextTheCardCanHold()
        {
            // The baseline Lua sets badge 'LIMITED' and cta 'Claim offer'. If either outgrew the card the
            // shipped variant itself would render control, which is a silly way to find out.
            Assert.That("LIMITED".Length, Is.LessThanOrEqualTo(PresentationSpec.MaxBadgeLength));
            Assert.That("Claim offer".Length, Is.LessThanOrEqualTo(PresentationSpec.MaxCtaLength));
            Assert.That("Buy".Length, Is.LessThanOrEqualTo(PresentationSpec.MaxCtaLength));
        }

        // --- rejection tokens -----------------------------------------------------------------------------

        [Test]
        public void EachRejectionClassGetsItsOwnShortToken()
        {
            // The strip shows one of these; the log keeps the sentence. A viewer of a recording needs the
            // class of failure, not the prose.
            Assert.That(SpecRejection.TokenFor("spec.unknownField"), Is.EqualTo("unknown field"));
            Assert.That(SpecRejection.TokenFor("spec.foreignField"), Is.EqualTo("foreign field"));
            Assert.That(SpecRejection.TokenFor("spec.ctaText.tooLong"), Is.EqualTo("text too long"));
            Assert.That(SpecRejection.TokenFor("spec.layout.unknown"), Is.EqualTo("bad enum value"));
            Assert.That(SpecRejection.TokenFor("spec.priceStyle.notAString"), Is.EqualTo("wrong type"));
            Assert.That(SpecRejection.TokenFor("spec.null"), Is.EqualTo("no table"));
        }

        [Test]
        public void TheTokenComesFromTheIssueCodeNotTheWording()
        {
            // Derived from the machine-readable code, so improving a message cannot silently change what
            // the strip says.
            var table = new System.Collections.Generic.Dictionary<string, object>
            {
                { "particleEffect", "confetti" }
            };

            var result = PresentationSpecReader.Read(
                table, SpecFieldGroup.Pricing, PresentationSpec.Baseline);

            Assert.That(SpecRejection.Token(result.Issues), Is.EqualTo("unknown field"));
        }

        [Test]
        public void AValidSpecHasNoToken()
        {
            Assert.That(SpecRejection.Token(HotUpdateABTest.Core.Config.ValidationResult.Ok), Is.Null);
        }
    }
}
