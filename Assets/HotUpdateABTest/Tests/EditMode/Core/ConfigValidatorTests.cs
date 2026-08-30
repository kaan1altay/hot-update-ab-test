using HotUpdateABTest.Core.Config;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers the rules a well-formed payload can still break, and the one rule everything else in the
    /// framework leans on: no two running experiments in a layer may claim the same traffic.
    /// </summary>
    [TestFixture]
    public sealed class ConfigValidatorTests
    {
        private static ValidationResult Validate(ConfigJson payload)
        {
            var read = ConfigReader.Read(payload.Build());
            Assert.That(read.IsValid, Is.True, "payload should be structurally sound: " + read.Issues.Describe());
            return ConfigValidator.Validate(read.Config);
        }

        [Test]
        public void TheDemoConfigIsValid()
        {
            Assert.That(Validate(ConfigJson.Demo()).IsValid, Is.True);
        }

        // --- the load-bearing rule ---------------------------------------------------------------------

        [Test]
        public void TwoRunningExperimentsClaimingTheSameTrafficAreRejected()
        {
            var result = Validate(ConfigJson.New()
                .Layer("offer_layout")
                .Experiment("exp_a", "offer_layout", from: 0, to: 6000)
                .Experiment("exp_b", "offer_layout", from: 4000, to: 10000));

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Describe(),
                Does.Contain("layer 'offer_layout'")
                    .And.Contain("'exp_a' [0, 6000)")
                    .And.Contain("'exp_b' [4000, 10000)")
                    .And.Contain("mutually exclusive"));
        }

        [Test]
        public void AdjacentAllocationsDoNotOverlap()
        {
            // The ranges are half-open precisely so this is unambiguous rather than an off-by-one argument.
            Assert.That(Validate(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_a", "l", from: 0, to: 5000)
                .Experiment("exp_b", "l", from: 5000, to: 10000)).IsValid, Is.True);
        }

        [Test]
        public void ANonRunningExperimentMayOverlapARunningOne()
        {
            // The ordinary way to stage a replacement: write it against the same traffic, leave it draft,
            // and flip the pair over in one payload. Forbidding this would make that manoeuvre impossible.
            Assert.That(Validate(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_live", "l", status: "running", from: 0, to: 10000)
                .Experiment("exp_next", "l", status: "draft", from: 0, to: 10000)).IsValid, Is.True);
        }

        [Test]
        public void ExperimentsInDifferentLayersMayClaimTheSameBucketRange()
        {
            Assert.That(Validate(ConfigJson.New()
                .Layer("layer_a")
                .Layer("layer_b")
                .Experiment("exp_a", "layer_a", from: 0, to: 3000)
                .Experiment("exp_b", "layer_b", from: 0, to: 3000)).IsValid, Is.True);
        }

        // --- layer salts -------------------------------------------------------------------------------

        [Test]
        public void TwoLayersSharingASaltAreRejected()
        {
            // The most damaging mistake this config format allows, and completely invisible at runtime:
            // the layers would bucket every user identically and their experiments would be confounded.
            var result = Validate(ConfigJson.New()
                .Layer("offer_layout", "shared.salt")
                .Layer("pricing_cta", "shared.salt"));

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Describe(),
                Does.Contain("same salt").And.Contain("perfectly confounded"));
        }

        // --- references and duplicates -------------------------------------------------------------------

        [Test]
        public void AnExperimentReferencingAnUnknownLayerIsRejected()
        {
            var result = Validate(ConfigJson.New().Layer("offer_layout").Experiment("exp_x", "pricing"));

            Assert.That(result.Describe(),
                Does.Contain("experiment 'exp_x': references unknown layer 'pricing'"));
        }

        [Test]
        public void DuplicateIdentifiersAreRejected()
        {
            Assert.That(Validate(ConfigJson.New().Layer("l").Layer("l", "other.salt")).Describe(),
                Does.Contain("layer 'l': declared more than once"));

            Assert.That(Validate(ConfigJson.New()
                    .Layer("l")
                    .Experiment("exp_x", "l", from: 0, to: 5000)
                    .Experiment("exp_x", "l", from: 5000, to: 10000)).Describe(),
                Does.Contain("experiment 'exp_x': declared more than once"));
        }

        [Test]
        public void DuplicateVariantIdentifiersAreRejected()
        {
            var result = Validate(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", variants: new[]
                {
                    ConfigJson.Variant("control", 5000),
                    ConfigJson.Variant("control", 5000)
                }));

            Assert.That(result.Describe(),
                Does.Contain("variant 'control'").And.Contain("declared more than once"));
        }

        // --- variants -------------------------------------------------------------------------------------

        [Test]
        public void AnExperimentWithoutAControlVariantIsRejected()
        {
            var result = Validate(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", variants: new[]
                {
                    ConfigJson.Variant("a", 5000),
                    ConfigJson.Variant("b", 5000)
                }));

            Assert.That(result.Describe(),
                Does.Contain("declares no variant with id 'control'")
                    .And.Contain("the kill switch and every fallback return users to"));
        }

        [Test]
        public void ARunningExperimentWhoseWeightsSumToZeroIsRejected()
        {
            var result = Validate(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_offer_grid", "l", variants: new[]
                {
                    ConfigJson.Variant("control", 0),
                    ConfigJson.Variant("treatment", 0)
                }));

            Assert.That(result.Describe(), Does.Contain("experiment 'exp_offer_grid': variant weights sum to 0"));
        }

        [Test]
        public void AStoppedExperimentWhoseWeightsSumToZeroIsOnlyAWarning()
        {
            // It cannot assign anyone, but it is not running, so it harms nothing. Rejecting the payload
            // over it would block an operator from parking an experiment at zero while they think.
            var result = Validate(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", status: "stopped", variants: new[]
                {
                    ConfigJson.Variant("control", 0)
                }));

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Describe(), Does.StartWith("warning:").And.Contain("cannot be started"));
        }

        [Test]
        public void AnExperimentWithNoVariantsIsRejected()
        {
            Assert.That(Validate(ConfigJson.New()
                    .Layer("l")
                    .Experiment("exp_x", "l", variants: new string[0])).Describe(),
                Does.Contain("declares no variants"));
        }

        // --- allocation bounds ---------------------------------------------------------------------------

        [Test]
        public void AnAllocationOutsideTheBucketSpaceIsRejected()
        {
            Assert.That(Validate(ConfigJson.New()
                    .Layer("l")
                    .Experiment("exp_x", "l", from: 0, to: 10001)).Describe(),
                Does.Contain("falls outside the bucket space"));

            Assert.That(Validate(ConfigJson.New()
                    .Layer("l")
                    .Experiment("exp_y", "l", from: -1, to: 100)).Describe(),
                Does.Contain("falls outside the bucket space"));
        }

        [Test]
        public void AnInvertedAllocationIsRejected()
        {
            Assert.That(Validate(ConfigJson.New()
                    .Layer("l")
                    .Experiment("exp_x", "l", from: 8000, to: 2000)).Describe(),
                Does.Contain("ends before it starts"));
        }

        [Test]
        public void ARunningExperimentClaimingNoTrafficIsOnlyAWarning()
        {
            var result = Validate(ConfigJson.New().Layer("l").Experiment("exp_x", "l", from: 0, to: 0));

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Describe(), Does.Contain("claims no traffic"));
        }

        // --- collection -----------------------------------------------------------------------------------

        [Test]
        public void EverySemanticProblemIsReportedFromOneValidation()
        {
            var result = Validate(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_a", "unknown_layer", variants: new[] { ConfigJson.Variant("a", 1) })
                .Experiment("exp_b", "l", from: 0, to: 6000)
                .Experiment("exp_c", "l", from: 4000, to: 10000));

            Assert.That(result.ErrorCount, Is.GreaterThanOrEqualTo(3), result.Describe());
            Assert.That(result.Describe(), Does.Contain("unknown layer"));
            Assert.That(result.Describe(), Does.Contain("no variant with id 'control'"));
            Assert.That(result.Describe(), Does.Contain("overlapping traffic"));
        }
    }
}
