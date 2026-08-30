using System;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers every way a cached assignment can stop being justified by the current configuration.
    /// </summary>
    /// <remarks>
    /// The list is meant to be exhaustive rather than representative. A pin that outlives its
    /// justification is the mechanism by which a killed experiment keeps running for the users who had
    /// already seen it - the exact failure the kill switch is supposed to prevent - so "mostly works" is
    /// not a useful standard here.
    /// </remarks>
    [TestFixture]
    public sealed class PinReconcilerTests
    {
        private static readonly DateTime When = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static ExperimentConfig Parse(ConfigJson payload)
        {
            var read = ConfigReader.Read(payload.Build());
            Assert.That(read.IsValid, Is.True, read.Issues.Describe());
            return read.Config;
        }

        private static InMemoryAssignmentStore StoreWith(params string[] users)
        {
            var store = new InMemoryAssignmentStore();
            foreach (string user in users)
            {
                store.Set(user, new AssignmentPin("exp_x", "treatment", When, "1"));
            }

            return store;
        }

        [Test]
        public void APinForARunningExperimentSurvives()
        {
            var store = StoreWith("user-1", "user-2");
            var config = Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l"));

            var report = PinReconciler.Reconcile(config, store);

            Assert.That(report.RemovedCount, Is.Zero);
            Assert.That(store.Count, Is.EqualTo(2));
        }

        [Test]
        public void PinsGoWhenTheExperimentStopsRunning()
        {
            // The kill switch. Leaving these behind would keep handing users an arm of an experiment
            // nobody is running.
            foreach (string status in new[] { "paused", "stopped", "draft" })
            {
                var store = StoreWith("user-1", "user-2");
                var config = Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l", status: status));

                var report = PinReconciler.Reconcile(config, store);

                Assert.That(store.Count, Is.Zero, status);
                Assert.That(report.CountFor(PinDiscardReason.ExperimentNotRunning), Is.EqualTo(2), status);
            }
        }

        [Test]
        public void PinsGoWhenTheExperimentDisappearsFromTheConfig()
        {
            var store = StoreWith("user-1");
            var config = Parse(ConfigJson.New().Layer("l"));

            var report = PinReconciler.Reconcile(config, store);

            Assert.That(store.Count, Is.Zero);
            Assert.That(report.CountFor(PinDiscardReason.ExperimentGone), Is.EqualTo(1));
        }

        [Test]
        public void OnlyThePinsNamingADeletedVariantGo()
        {
            // Per-user, not per-experiment: the users still on a surviving arm keep their pins.
            var store = new InMemoryAssignmentStore();
            store.Set("stays", new AssignmentPin("exp_x", "control", When, "1"));
            store.Set("goes", new AssignmentPin("exp_x", "treatment", When, "1"));

            var config = Parse(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", variants: new[] { ConfigJson.Variant("control", 10000) }));

            var report = PinReconciler.Reconcile(config, store);

            Assert.That(report.CountFor(PinDiscardReason.VariantGone), Is.EqualTo(1));
            Assert.That(store.TryGet("stays", "exp_x", out _), Is.True);
            Assert.That(store.TryGet("goes", "exp_x", out _), Is.False);
        }

        [Test]
        public void PinsGoWhenTheLayerDisappears()
        {
            // Unreachable through an accepted payload, because the validator rejects an experiment naming
            // an undeclared layer. Reconciliation also runs against configs built in code, and a pin for an
            // experiment that can never be allocated would otherwise sit there forever.
            var store = StoreWith("user-1");
            var config = new ExperimentConfig(1, "1", new LayerDef[0], new[]
            {
                new ExperimentDef("exp_x", "gone", ExperimentStatus.Running, "s", BucketRange.Full,
                    StickinessPolicy.StickyAfterExposure, new[] { new VariantDef("treatment", 1, "b") })
            });

            var report = PinReconciler.Reconcile(config, store);

            Assert.That(store.Count, Is.Zero);
            Assert.That(report.CountFor(PinDiscardReason.LayerGone), Is.EqualTo(1));
        }

        // --- the stickiness flip -------------------------------------------------------------------------

        [Test]
        public void FlippingToStatelessKeepsThePinsDormantRatherThanDeletingThem()
        {
            // The decision worth defending. Deleting pins on a policy flip would make the flip
            // irreversible: flipping back would have lost the record of who was already treated, and those
            // users would be re-bucketed - exactly the contamination the sticky policy exists to prevent.
            // A dormant pin costs a dictionary entry; a lost one costs the experiment's validity.
            var store = StoreWith("user-1", "user-2");
            var config = Parse(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", stickiness: "stateless"));

            var report = PinReconciler.Reconcile(config, store);

            Assert.That(report.RemovedCount, Is.Zero, "a policy change is not a reason to forget history");
            Assert.That(store.Count, Is.EqualTo(2));
        }

        [Test]
        public void FlippingToStatelessAndBackRestoresTheOriginalAssignments()
        {
            var store = StoreWith("user-1");
            var resolver = new ExperimentResolver(store);
            var user = new UserContext("user-1");

            var sticky = Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l"));
            var stateless = Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l", stickiness: "stateless"));

            Assert.That(resolver.Resolve(sticky, user, "l").Source, Is.EqualTo(AssignmentSource.Pinned));

            PinReconciler.Reconcile(stateless, store);
            Assert.That(resolver.Resolve(stateless, user, "l").Source, Is.EqualTo(AssignmentSource.Bucketed),
                "a stateless experiment ignores pins");

            PinReconciler.Reconcile(sticky, store);
            var restored = resolver.Resolve(sticky, user, "l");

            Assert.That(restored.Source, Is.EqualTo(AssignmentSource.Pinned));
            Assert.That(restored.VariantId, Is.EqualTo("treatment"),
                "the arm the user was originally exposed to must come back");
        }

        [Test]
        public void PinsForAStatelessExperimentStillGoWhenItStops()
        {
            // Dormant is not exempt. The pins go when the experiment ends, like everyone else's.
            var store = StoreWith("user-1");
            var config = Parse(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", status: "stopped", stickiness: "stateless"));

            PinReconciler.Reconcile(config, store);

            Assert.That(store.Count, Is.Zero);
        }

        // --- reporting -----------------------------------------------------------------------------------

        [Test]
        public void TheReportSaysWhatWentAndWhy()
        {
            var store = new InMemoryAssignmentStore();
            store.Set("u1", new AssignmentPin("exp_stopped", "treatment", When, "1"));
            store.Set("u2", new AssignmentPin("exp_gone", "treatment", When, "1"));

            var config = Parse(ConfigJson.New().Layer("l").Experiment("exp_stopped", "l", status: "stopped"));

            var report = PinReconciler.Reconcile(config, store);

            Assert.That(report.RemovedCount, Is.EqualTo(2));
            Assert.That(report.Describe(),
                Does.Contain("no longer running").And.Contain("no longer in the config"));
        }

        [Test]
        public void ReconcilingAnEmptyStoreIsHarmless()
        {
            var report = PinReconciler.Reconcile(Parse(ConfigJson.Demo()), new InMemoryAssignmentStore());

            Assert.That(report.RemovedCount, Is.Zero);
            Assert.That(report.Describe(), Is.EqualTo("nothing to discard"));
        }

        [Test]
        public void ReconcilingIsIdempotent()
        {
            var store = StoreWith("user-1");
            var config = Parse(ConfigJson.New().Layer("l").Experiment("exp_x", "l", status: "stopped"));

            Assert.That(PinReconciler.Reconcile(config, store).RemovedCount, Is.EqualTo(1));
            Assert.That(PinReconciler.Reconcile(config, store).RemovedCount, Is.Zero);
        }
    }
}
