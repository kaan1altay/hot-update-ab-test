using System;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers the composition: bucketing plus pins plus audience plus the QA override, and the order they
    /// are applied in.
    /// </summary>
    [TestFixture]
    public sealed class ExperimentResolverTests
    {
        private static readonly DateTime When = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static ExperimentConfig Parse(ConfigJson payload)
        {
            var read = ConfigReader.Read(payload.Build());
            Assert.That(read.IsValid, Is.True, read.Issues.Describe());
            return read.Config;
        }

        private static ExperimentConfig Simple(string status = "running", string stickiness = "sticky_after_exposure",
            string audience = null)
        {
            return Parse(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", status: status, stickiness: stickiness, audience: audience));
        }

        // --- the ordinary path -----------------------------------------------------------------------

        [Test]
        public void AUserIsResolvedToAnArmAndTheReasoningIsCarriedWithIt()
        {
            var resolver = new ExperimentResolver();
            var assignment = resolver.Resolve(Simple(), new UserContext("user-1"), "l");

            Assert.That(assignment.IsAssigned, Is.True);
            Assert.That(assignment.ExperimentId, Is.EqualTo("exp_x"));
            Assert.That(assignment.Source, Is.EqualTo(AssignmentSource.Bucketed));
            Assert.That(assignment.LayerBucket, Is.InRange(0, 9999));
            Assert.That(assignment.VariantBucket, Is.InRange(0, 9999));
            Assert.That(assignment.IsForced, Is.False);
        }

        [Test]
        public void AStoppedExperimentReturnsEveryoneToControl()
        {
            var resolver = new ExperimentResolver();

            foreach (string status in new[] { "paused", "stopped", "draft" })
            {
                var config = Simple(status);
                foreach (string user in TestConfigs.Users(200))
                {
                    var assignment = resolver.Resolve(config, new UserContext(user), "l");
                    Assert.That(assignment.IsAssigned, Is.False, status);
                    Assert.That(assignment.Reason, Is.EqualTo(NoAssignmentReason.OutsideAllocation), status);
                }
            }
        }

        [Test]
        public void AnUnknownLayerIsReportedRatherThanThrowing()
        {
            var assignment = new ExperimentResolver().Resolve(Simple(), new UserContext("u"), "nope");

            Assert.That(assignment.IsAssigned, Is.False);
            Assert.That(assignment.Reason, Is.EqualTo(NoAssignmentReason.UnknownLayer));
            Assert.That(assignment.Explanation, Does.Contain("no layer 'nope'"));
        }

        [Test]
        public void EveryNonAssignmentExplainsItself()
        {
            // The debug panel has to be able to answer "why am I not in this experiment", and "you are not
            // in it" is a support ticket rather than an answer.
            var resolver = new ExperimentResolver();

            var outside = resolver.Resolve(
                Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l", from: 0, to: 1)),
                new UserContext("user-with-a-high-bucket"), "l");

            var excluded = resolver.Resolve(
                Simple(audience: "{\"minAccountLevel\":50}"), new UserContext("u", accountLevel: 3), "l");

            var noTraffic = resolver.Resolve(
                Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l", variants: new[]
                {
                    ConfigJson.Variant("control", 0)
                }, status: "running", from: 0, to: 10000)),
                new UserContext("u"), "l");

            Assert.That(outside.Explanation, Does.Contain("not claimed by any running experiment"));
            Assert.That(excluded.Explanation, Does.Contain("account level 3 is below the minimum of 50"));
            Assert.That(noTraffic.Explanation, Does.Contain("every variant has weight 0"));
        }

        // --- audience --------------------------------------------------------------------------------

        [Test]
        public void AudienceIsAppliedAfterAllocationSoTheBucketDoesNotMove()
        {
            // A user who fails the predicate keeps the bucket they always had; they are simply not in the
            // experiment. Filtering before allocation would re-pack the layer and let two targeted
            // experiments overlap.
            var config = Simple(audience: "{\"minAccountLevel\":10}");
            var resolver = new ExperimentResolver();

            var low = resolver.Resolve(config, new UserContext("user-1", accountLevel: 1), "l");
            var high = resolver.Resolve(config, new UserContext("user-1", accountLevel: 99), "l");

            Assert.That(low.IsAssigned, Is.False);
            Assert.That(high.IsAssigned, Is.True);
            Assert.That(low.LayerBucket, Is.EqualTo(high.LayerBucket),
                "the audience must not change where a user sits in the layer");
        }

        [Test]
        public void ATargetedExperimentHoldsItsAllocationTimesTheMatchRate()
        {
            // Worth pinning because it is the fact the sample-ratio check has to be told about: a healthy
            // targeted experiment holds fewer users than its allocation width suggests.
            var config = Simple(audience: "{\"platforms\":[\"editor\"]}");
            var resolver = new ExperimentResolver();

            int assigned = 0;
            const int total = 10000;

            for (int i = 0; i < total; i++)
            {
                string platform = i % 4 == 0 ? "editor" : "windows";
                if (resolver.Resolve(config, new UserContext("user-" + i, platform: platform), "l").IsAssigned)
                {
                    assigned++;
                }
            }

            Assert.That(assigned / (double)total, Is.EqualTo(0.25).Within(0.01));
        }

        // --- pins -------------------------------------------------------------------------------------

        [Test]
        public void AnExposedUserKeepsTheirArmWhenTheWeightsChange()
        {
            // The whole point of the sticky policy.
            var store = new InMemoryAssignmentStore();
            var resolver = new ExperimentResolver(store);
            var user = new UserContext("user-1");

            var before = Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l", variants: new[]
            {
                ConfigJson.Variant("control", 5000),
                ConfigJson.Variant("treatment", 5000)
            }));

            var assignment = resolver.Resolve(before, user, "l");
            resolver.NotifyExposed(user, assignment, When);
            string armSeen = assignment.VariantId;

            // Now flip the weights hard the other way.
            var after = Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l", variants: new[]
            {
                ConfigJson.Variant("control", 9900),
                ConfigJson.Variant("treatment", 100)
            }));

            var later = resolver.Resolve(after, user, "l");

            Assert.That(later.VariantId, Is.EqualTo(armSeen));
            Assert.That(later.Source, Is.EqualTo(AssignmentSource.Pinned));
        }

        [Test]
        public void AnUnexposedUserIsRebucketedFreelyWhenTheWeightsChange()
        {
            // The other half of the policy: users who have contributed nothing to the analysis can move, so
            // an operator can actually ramp traffic.
            var store = new InMemoryAssignmentStore();
            var resolver = new ExperimentResolver(store);

            var before = Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l", variants: new[]
            {
                ConfigJson.Variant("control", 5000),
                ConfigJson.Variant("treatment", 5000)
            }));

            var after = Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l", variants: new[]
            {
                ConfigJson.Variant("control", 9000),
                ConfigJson.Variant("treatment", 1000)
            }));

            int moved = 0, total = 0;
            foreach (string userId in TestConfigs.Users(5000))
            {
                var user = new UserContext(userId);
                if (resolver.Resolve(before, user, "l").VariantId != resolver.Resolve(after, user, "l").VariantId)
                {
                    moved++;
                }

                total++;
            }

            Assert.That(moved / (double)total, Is.EqualTo(0.40).Within(0.02),
                "with nobody exposed, the arm boundary should move as freely as pure bucketing");
        }

        [Test]
        public void ExposureIsWhatWritesThePinNotAssignment()
        {
            var store = new InMemoryAssignmentStore();
            var resolver = new ExperimentResolver(store);
            var user = new UserContext("user-1");

            for (int i = 0; i < 10; i++) resolver.Resolve(Simple(), user, "l");
            Assert.That(store.Count, Is.Zero, "resolving is free and silent");

            resolver.NotifyExposed(user, resolver.Resolve(Simple(), user, "l"), When);
            Assert.That(store.Count, Is.EqualTo(1));
        }

        [Test]
        public void AStatelessExperimentIgnoresPinsWithoutDeletingThem()
        {
            var store = new InMemoryAssignmentStore();
            store.Set("user-1", new AssignmentPin("exp_x", "treatment", When, "1"));

            var resolver = new ExperimentResolver(store);
            var assignment = resolver.Resolve(Simple(stickiness: "stateless"), new UserContext("user-1"), "l");

            Assert.That(assignment.Source, Is.EqualTo(AssignmentSource.Bucketed));
            Assert.That(store.Count, Is.EqualTo(1), "ignored, not deleted");
        }

        [Test]
        public void AStatelessExperimentNeverWritesAPin()
        {
            var store = new InMemoryAssignmentStore();
            var resolver = new ExperimentResolver(store);
            var user = new UserContext("user-1");
            var config = Simple(stickiness: "stateless");

            resolver.NotifyExposed(user, resolver.Resolve(config, user, "l"), When);

            Assert.That(store.Count, Is.Zero);
        }

        [Test]
        public void APinOutranksAnAudienceTheUserNoLongerMatches()
        {
            // They have already been treated. Pulling them out now would change the product under someone
            // mid-experiment and split one person across two arms of the analysis.
            var store = new InMemoryAssignmentStore();
            var resolver = new ExperimentResolver(store);
            var user = new UserContext("user-1", accountLevel: 50);

            var open = Simple();
            resolver.NotifyExposed(user, resolver.Resolve(open, user, "l"), When);

            var narrowed = Simple(audience: "{\"minAccountLevel\":99}");
            var later = resolver.Resolve(narrowed, user, "l");

            Assert.That(later.IsAssigned, Is.True);
            Assert.That(later.Source, Is.EqualTo(AssignmentSource.Pinned));
        }

        [Test]
        public void APinDoesNotOutrankTheKillSwitch()
        {
            var store = new InMemoryAssignmentStore();
            var resolver = new ExperimentResolver(store);
            var user = new UserContext("user-1");

            resolver.NotifyExposed(user, resolver.Resolve(Simple(), user, "l"), When);

            var stopped = Simple("stopped");
            PinReconciler.Reconcile(stopped, store);

            Assert.That(resolver.Resolve(stopped, user, "l").IsAssigned, Is.False);
            Assert.That(store.Count, Is.Zero);
        }

        [Test]
        public void ReExposingToTheSameArmDoesNotMoveTheOriginalTimestamp()
        {
            var store = new InMemoryAssignmentStore();
            var resolver = new ExperimentResolver(store);
            var user = new UserContext("user-1");
            var config = Simple();

            resolver.NotifyExposed(user, resolver.Resolve(config, user, "l"), When);
            bool wroteAgain = resolver.NotifyExposed(user, resolver.Resolve(config, user, "l"), When.AddDays(3));

            store.TryGet("user-1", "exp_x", out var pin);

            Assert.That(wroteAgain, Is.False);
            Assert.That(pin.PinnedUtc, Is.EqualTo(When), "the record of first treatment must not drift");
        }

        [Test]
        public void APinNamingADeletedArmFallsBackToBucketingRatherThanApplyingIt()
        {
            // Reconciliation removes these on a config swap; this covers the case where the store was
            // mutated by something else. The invariant is that a variant absent from the current config is
            // never applied.
            var store = new InMemoryAssignmentStore();
            store.Set("user-1", new AssignmentPin("exp_x", "deleted_arm", When, "0"));

            var resolver = new ExperimentResolver(store);
            var assignment = resolver.Resolve(Simple(), new UserContext("user-1"), "l");

            Assert.That(assignment.IsAssigned, Is.True);
            Assert.That(assignment.VariantId, Is.Not.EqualTo("deleted_arm"));
            Assert.That(assignment.Source, Is.EqualTo(AssignmentSource.Bucketed));
        }

        // --- the QA override ------------------------------------------------------------------------

        [Test]
        public void AForcedVariantBypassesBucketingAndIsFlagged()
        {
            var resolver = new ExperimentResolver();
            resolver.Overrides.Force("exp_x", "treatment");

            var config = Simple();

            foreach (string userId in TestConfigs.Users(50))
            {
                var assignment = resolver.Resolve(config, new UserContext(userId), "l");
                Assert.That(assignment.VariantId, Is.EqualTo("treatment"));
                Assert.That(assignment.Source, Is.EqualTo(AssignmentSource.Forced));
                Assert.That(assignment.IsForced, Is.True);
            }
        }

        [Test]
        public void AForcedVariantCanPreviewAnExperimentThatIsNotLiveYet()
        {
            var resolver = new ExperimentResolver();
            resolver.Overrides.Force("exp_x", "treatment");

            var assignment = resolver.Resolve(Simple("draft"), new UserContext("user-1"), "l");

            Assert.That(assignment.IsAssigned, Is.True);
            Assert.That(assignment.IsForced, Is.True);
        }

        [Test]
        public void AForcedVariantThatNoLongerExistsIsIgnoredRatherThanInvented()
        {
            // Stale tooling state is not a licence to apply an arm the config does not declare.
            var resolver = new ExperimentResolver();
            resolver.Overrides.Force("exp_x", "arm_that_was_deleted");

            var assignment = resolver.Resolve(Simple(), new UserContext("user-1"), "l");

            Assert.That(assignment.IsForced, Is.False);
            // Is.EqualTo(..).Or rather than Is.AnyOf: Unity bundles NUnit 3.5, which predates AnyOf.
            Assert.That(assignment.VariantId, Is.EqualTo("control").Or.EqualTo("treatment"));
        }

        [Test]
        public void AForcedAssignmentNeverWritesAPin()
        {
            // The override must vanish when it is cleared. Writing it into the store would leave a tester
            // wondering why the app still shows an arm they turned off.
            var store = new InMemoryAssignmentStore();
            var resolver = new ExperimentResolver(store);
            resolver.Overrides.Force("exp_x", "treatment");

            var user = new UserContext("user-1");
            resolver.NotifyExposed(user, resolver.Resolve(Simple(), user, "l"), When);

            Assert.That(store.Count, Is.Zero);
        }

        [Test]
        public void ClearingTheOverrideRestoresNormalBucketing()
        {
            var resolver = new ExperimentResolver();
            var config = Simple();
            var user = new UserContext("user-1");

            var natural = resolver.Resolve(config, user, "l").VariantId;
            string other = natural == "control" ? "treatment" : "control";

            resolver.Overrides.Force("exp_x", other);
            Assert.That(resolver.Resolve(config, user, "l").VariantId, Is.EqualTo(other));

            resolver.Overrides.Clear("exp_x");
            var restored = resolver.Resolve(config, user, "l");

            Assert.That(restored.VariantId, Is.EqualTo(natural));
            Assert.That(restored.IsForced, Is.False);
            Assert.That(resolver.Overrides.Any, Is.False);
        }

        // --- layers together -------------------------------------------------------------------------

        [Test]
        public void ResolvingEveryLayerGivesOneAnswerPerLayer()
        {
            var config = Parse(ConfigJson.Demo());
            var results = new ExperimentResolver().ResolveAll(
                new ConfigSnapshot(config, ConfigSourceKind.Live, When), new UserContext("user-1"));

            Assert.That(results.Count, Is.EqualTo(2));
            Assert.That(results[0].LayerId, Is.EqualTo("offer_layout"));
            Assert.That(results[1].LayerId, Is.EqualTo("pricing_cta"));
        }

        [Test]
        public void AUserCanBeInOneExperimentPerLayerSimultaneously()
        {
            var config = Parse(ConfigJson.Demo());
            var resolver = new ExperimentResolver();

            int bothAssigned = 0;
            foreach (string userId in TestConfigs.Users(1000))
            {
                var results = resolver.ResolveAll(
                    new ConfigSnapshot(config, ConfigSourceKind.Live, When), new UserContext(userId));

                if (results[0].IsAssigned && results[1].IsAssigned) bothAssigned++;
            }

            Assert.That(bothAssigned, Is.EqualTo(1000),
                "both experiments claim their whole layer, so every user should be in both");
        }

        [Test]
        public void ResolvingAgainstTheEmptyConfigAssignsNobodyAndDoesNotThrow()
        {
            var snapshot = ConfigSnapshot.Nothing(When);
            var results = new ExperimentResolver().ResolveAll(snapshot, new UserContext("user-1"));

            Assert.That(results, Is.Empty);
            Assert.That(new ExperimentResolver().Resolve(snapshot, new UserContext("u"), "l").IsAssigned,
                Is.False);
        }

        [Test]
        public void NullArgumentsAreRejected()
        {
            var resolver = new ExperimentResolver();
            var config = Simple();

            Assert.That(() => resolver.Resolve((ExperimentConfig)null, new UserContext("u"), "l"),
                Throws.ArgumentNullException);
            Assert.That(() => resolver.Resolve(config, null, "l"), Throws.ArgumentNullException);
            Assert.That(() => resolver.Resolve(config, new UserContext("u"), null), Throws.ArgumentNullException);
        }
    }
}
