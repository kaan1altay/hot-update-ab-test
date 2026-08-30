using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Telemetry;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers attribution, and specifically that it reads the exposure record rather than re-resolving.
    /// </summary>
    [TestFixture]
    public sealed class ConversionTrackerTests
    {
        private TelemetryHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new TelemetryHarness();
        }

        [Test]
        public void AConversionIsCreditedToTheArmTheUserSaw()
        {
            var assignment = _h.Visit("user-1", convert: true);
            var arm = _h.Arm("exp_offer_layout", assignment.VariantId);

            Assert.That(arm.Conversions, Is.EqualTo(1));
            Assert.That(_h.Conversions.AttributedCount, Is.EqualTo(1));
        }

        // --- the rule that makes the numbers trustworthy -------------------------------------------------

        [Test]
        public void AConversionAfterAWeightRampStillCreditsTheOriginalArm()
        {
            // Re-resolving at conversion time would return the arm the user is in *now*. If the config has
            // moved in between, that silently credits the outcome to an arm they never saw, and the report
            // looks completely normal.
            var user = new UserContext("user-1", platform: "editor");
            var session = new SessionId("s1");

            var atExposure = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            _h.Exposures.MarkExposed(user, atExposure, session);

            // Ramp hard the other way, and drop the pin so the resolver genuinely would answer differently.
            _h.Pins.Clear();
            _h.Serve(ConfigJson.New("2")
                .Layer("offer_layout")
                .Layer("pricing_cta")
                .Experiment("exp_offer_layout", "offer_layout", variants: new[]
                {
                    ConfigJson.Variant("control", atExposure.VariantId == "control" ? 1 : 9999),
                    ConfigJson.Variant("treatment", atExposure.VariantId == "control" ? 9999 : 1)
                })
                .Experiment("exp_pricing_cta", "pricing_cta")
                .Build());

            var nowResolvesTo = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            Assert.That(nowResolvesTo.VariantId, Is.Not.EqualTo(atExposure.VariantId),
                "the setup must actually make the resolver disagree, or this test proves nothing");

            var result = _h.Conversions.Convert(user, session, "purchase");

            Assert.That(result.AttributedTo[0].VariantId, Is.EqualTo(atExposure.VariantId));
            Assert.That(_h.Arm("exp_offer_layout", atExposure.VariantId).Conversions, Is.EqualTo(1));
            Assert.That(_h.Arm("exp_offer_layout", nowResolvesTo.VariantId).Conversions, Is.Zero);
        }

        [Test]
        public void AConversionAfterTheKillSwitchStillCreditsTheArmTheUserSaw()
        {
            // The experiment is gone by the time the purchase lands. The outcome still happened, and it
            // still belongs to the arm that produced it.
            var user = new UserContext("user-1", platform: "editor");
            var session = new SessionId("s1");

            var atExposure = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            _h.Exposures.MarkExposed(user, atExposure, session);

            _h.Serve(ConfigJson.New("2")
                .Layer("offer_layout")
                .Layer("pricing_cta")
                .Experiment("exp_offer_layout", "offer_layout", status: "stopped")
                .Experiment("exp_pricing_cta", "pricing_cta")
                .Build());

            Assert.That(_h.Resolver.Resolve(_h.Snapshot, user, "offer_layout").IsAssigned, Is.False);
            Assert.That(_h.Pins.Count, Is.Zero, "the kill switch discarded the pin");

            var result = _h.Conversions.Convert(user, session, "purchase");

            Assert.That(result.IsUnattributed, Is.False);
            Assert.That(result.AttributedTo[0].VariantId, Is.EqualTo(atExposure.VariantId));
        }

        // --- multiple layers ------------------------------------------------------------------------------

        [Test]
        public void OneConversionCreditsEveryExperimentTheUserWasExposedTo()
        {
            // One purchase is evidence about the offer layout and about the pricing at the same time.
            var user = new UserContext("user-1", platform: "editor");
            var session = new SessionId("s1");

            var offer = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            var pricing = _h.Resolver.Resolve(_h.Snapshot, user, "pricing_cta");
            _h.Exposures.MarkExposed(user, offer, session);
            _h.Exposures.MarkExposed(user, pricing, session);

            var result = _h.Conversions.Convert(user, session, "purchase");

            Assert.That(result.AttributedTo.Count, Is.EqualTo(2));
            Assert.That(_h.Arm("exp_offer_layout", offer.VariantId).Conversions, Is.EqualTo(1));
            Assert.That(_h.Arm("exp_pricing_cta", pricing.VariantId).Conversions, Is.EqualTo(1));
            Assert.That(_h.Conversions.AttributedCount, Is.EqualTo(1),
                "one conversion happened, even though it is evidence in two experiments");
        }

        [Test]
        public void OnlyTheExperimentsTheUserWasActuallyExposedToAreCredited()
        {
            var user = new UserContext("user-1", platform: "editor");
            var session = new SessionId("s1");

            _h.Exposures.MarkExposed(user, _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout"), session);
            var result = _h.Conversions.Convert(user, session, "purchase");

            Assert.That(result.AttributedTo.Count, Is.EqualTo(1));
            Assert.That(result.AttributedTo[0].ExperimentId, Is.EqualTo("exp_offer_layout"));
        }

        // --- unattributed ----------------------------------------------------------------------------------

        [Test]
        public void AConversionWithNoExposureIsRecordedAndVisibleRatherThanDropped()
        {
            _h.Conversions.Convert(new UserContext("never-saw-it"), new SessionId("s1"), "purchase");

            Assert.That(_h.Conversions.UnattributedCount, Is.EqualTo(1));
            Assert.That(_h.Events.CountOf(AnalyticsEventKind.Conversion), Is.EqualTo(1));
            Assert.That(_h.Report().UnattributedConversions, Is.EqualTo(1),
                "recorded somewhere nobody renders is the same as dropped");
            Assert.That(_h.Report().Describe(), Does.Contain("unattributed conversions: 1"));
        }

        [Test]
        public void AttributedAndUnattributedAlwaysSumToTheTotal()
        {
            for (int i = 0; i < 50; i++) _h.Visit("seen-" + i, convert: true);
            for (int i = 0; i < 20; i++)
            {
                _h.Conversions.Convert(new UserContext("unseen-" + i), new SessionId("u" + i), "purchase");
            }

            Assert.That(_h.Conversions.AttributedCount, Is.EqualTo(50));
            Assert.That(_h.Conversions.UnattributedCount, Is.EqualTo(20));
            Assert.That(_h.Conversions.TotalCount, Is.EqualTo(70));
        }

        // --- traits carry forward ---------------------------------------------------------------------------

        [Test]
        public void AConversionInheritsTheTaintOfTheExposureItFollows()
        {
            // A tester clicking buy after forcing themselves into an arm is not evidence, and the caller
            // should not have to remember that.
            _h.Resolver.Overrides.Force("exp_offer_layout", "treatment");

            var user = new UserContext("qa-1", platform: "editor");
            var session = new SessionId("s1");
            _h.Exposures.MarkExposed(user, _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout"), session);
            _h.Conversions.Convert(user, session, "purchase");

            Assert.That(_h.Arm("exp_offer_layout", "treatment").Conversions, Is.Zero,
                "a forced conversion must not reach the headline numbers");
            Assert.That(_h.Arm("exp_offer_layout", "treatment", MetricsPopulation.ForcedOnly).Conversions,
                Is.EqualTo(1), "but it must still be inspectable");
        }

        [Test]
        public void ConversionRateIsPerExposedUserNotPerAssignedUser()
        {
            // A user never shown the treatment tells you nothing about whether it works. Including them
            // would drag every arm's rate toward zero in proportion to how often the screen went unopened.
            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            var session = new SessionId("s1");

            for (int i = 0; i < 9; i++)
            {
                var other = new UserContext("prepared-" + i, platform: "editor");
                _h.Exposures.RecordAssignment(
                    other, _h.Resolver.Resolve(_h.Snapshot, other, "offer_layout"), new SessionId("p" + i));
            }

            _h.Exposures.RecordAssignment(user, assignment, session);
            _h.Exposures.MarkExposed(user, assignment, session);
            _h.Conversions.Convert(user, session, "purchase");

            var arm = _h.Arm("exp_offer_layout", assignment.VariantId, MetricsPopulation.Everything);

            Assert.That(arm.UsersExposed, Is.EqualTo(1));
            Assert.That(arm.ConversionRate, Is.EqualTo(1.0).Within(0.001));
        }

        [Test]
        public void NullArgumentsAreRejected()
        {
            Assert.That(() => _h.Conversions.Convert(null, new SessionId("s"), "g"),
                Throws.ArgumentNullException);
            Assert.That(() => _h.Conversions.Convert(new UserContext("u"), new SessionId("s"), null),
                Throws.ArgumentNullException);
        }
    }
}
