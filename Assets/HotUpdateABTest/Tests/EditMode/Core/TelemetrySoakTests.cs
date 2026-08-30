using System.Collections.Generic;
using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Telemetry;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Drives the whole framework through a long randomised sequence of realistic operations and checks its
    /// invariants throughout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unit tests each pin one behaviour in a situation built to expose it. This one exists for the
    /// situations nobody thought to build: a kill switch firing while a user is mid-session, a variant
    /// deleted between an exposure and a conversion, a QA override left on across a config swap. Those
    /// interactions are where an experiment framework actually breaks, and they are combinatorial enough
    /// that enumerating them by hand is hopeless.
    /// </para>
    /// <para>
    /// Randomness comes from a seeded generator written into the test rather than from
    /// <see cref="System.Random"/>, so a failure reproduces exactly from the seed printed in its message. A
    /// soak test that cannot be replayed is a soak test that gets marked flaky and ignored.
    /// </para>
    /// <para>
    /// The contamination invariant is tracked per <i>(user, experiment)</i> rather than per experiment. The
    /// coarser version is nearly vacuous: one pin wipe anywhere licenses every user to flip arms forever
    /// after, so the assertion stops constraining anything. Tracking which specific pins were disturbed
    /// keeps it sharp - a user whose pin was never touched must never be seen in a second arm.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class TelemetrySoakTests
    {
        private const int Operations = 20000;
        private const int UserPool = 400;

        /// <summary>How often the full cross-population sweep runs.</summary>
        /// <remarks>
        /// The cheap invariants - event counters, conversion totals - are checked after every operation.
        /// The sweep over every user is checked periodically instead, because it is O(population) and doing
        /// it twenty thousand times is quadratic for no gain: every property it checks is monotonic, so a
        /// violation cannot appear and then repair itself before the next sweep sees it.
        /// </remarks>
        private const int SweepEvery = 500;

        /// <summary>A tiny linear congruential generator, so runs are reproducible from the seed.</summary>
        private sealed class Rng
        {
            private uint _state;

            public Rng(uint seed)
            {
                _state = seed == 0 ? 1u : seed;
            }

            public int Next(int exclusiveMax)
            {
                unchecked
                {
                    _state = (1664525u * _state) + 1013904223u;
                    return (int)((_state >> 8) % (uint)exclusiveMax);
                }
            }

            public bool Chance(int percent) => Next(100) < percent;
        }

        /// <summary>Everything the soak has to remember to judge the framework's behaviour.</summary>
        private sealed class SoakState
        {
            public readonly TelemetryHarness Harness = new TelemetryHarness();
            public readonly Rng Random;

            /// <summary>Arms each user has been exposed to, tracked independently of the ledger.</summary>
            public readonly Dictionary<string, Dictionary<string, HashSet<string>>> ArmsSeen =
                new Dictionary<string, Dictionary<string, HashSet<string>>>();

            /// <summary>"user|experiment" pairs whose pin was removed, ignored, or overridden.</summary>
            public readonly HashSet<string> Disturbed = new HashSet<string>();

            public long ExposureEvents;
            public long AssignmentEvents;

            public SoakState(uint seed)
            {
                Random = new Rng(seed);
            }

            public static string Pair(string userId, string experimentId) => userId + "|" + experimentId;
        }

        [Test]
        public void TheInvariantsHoldAcrossALongRandomisedRun()
        {
            RunSoak(seed: 20260830);
        }

        [Test]
        public void TheInvariantsHoldFromASecondSeed()
        {
            RunSoak(seed: 991);
        }

        private static void RunSoak(uint seed)
        {
            var state = new SoakState(seed);

            for (int step = 0; step < Operations; step++)
            {
                string where = "seed " + seed + ", step " + step;

                int roll = state.Random.Next(100);
                if (roll < 58) Visit(state);
                else if (roll < 74) Convert(state);
                else if (roll < 88) SwapConfig(state, step);
                else if (roll < 93) ForceOrClear(state);
                else if (roll < 98) state.Harness.Sessions.Roll();
                else WipePins(state);

                AssertCheapInvariants(state, where);
                if (step % SweepEvery == 0) AssertFullSweep(state, where);
            }

            AssertFullSweep(state, "seed " + seed + ", final");

            // The run has to have actually exercised something, or the invariants held vacuously.
            Assert.That(state.ExposureEvents, Is.GreaterThan(1000), "the soak barely exposed anybody");
            Assert.That(state.Harness.Conversions.TotalCount, Is.GreaterThan(100), "the soak barely converted");
            Assert.That(CountUsersSeenInTwoArms(state), Is.GreaterThan(0),
                "no user was ever contaminated, so the contamination invariant proved nothing");

            var harness = state.Harness;
            TestContext.WriteLine(
                "seed " + seed + ": " + state.ExposureEvents + " exposures, " +
                harness.Conversions.TotalCount + " conversions (" + harness.Conversions.UnattributedCount +
                " unattributed), " + harness.Ledger.ContaminatedCount + " contaminated pairs, " +
                CountUsersSeenInTwoArms(state) + " users seen in more than one arm");
            TestContext.WriteLine("\n" + harness.Report().Describe());
        }

        // --- operations -------------------------------------------------------------------------------

        private static void Visit(SoakState state)
        {
            var harness = state.Harness;
            string userId = "user-" + state.Random.Next(UserPool);
            var user = new UserContext(userId, platform: "editor");
            string layerId = state.Random.Chance(50) ? "offer_layout" : "pricing_cta";
            var session = harness.Sessions.Current;

            var assignment = harness.Resolver.Resolve(harness.Snapshot, user, layerId);
            if (!assignment.IsAssigned) return;

            // A forced assignment bypasses the pin, so this user may legitimately end up in a second arm.
            if (assignment.IsForced) state.Disturbed.Add(SoakState.Pair(userId, assignment.ExperimentId));

            harness.Exposures.RecordAssignment(user, assignment, session, synthetic: true);
            state.AssignmentEvents++;

            if (!harness.Exposures.MarkExposed(user, assignment, session, synthetic: true)) return;

            state.ExposureEvents++;
            Track(state.ArmsSeen, userId, assignment.ExperimentId, assignment.VariantId);
        }

        private static void Convert(SoakState state)
        {
            string userId = "user-" + state.Random.Next(UserPool);
            state.Harness.Conversions.Convert(
                new UserContext(userId, platform: "editor"), state.Harness.Sessions.Current, "purchase",
                synthetic: true);
        }

        private static void SwapConfig(SoakState state, int step)
        {
            var harness = state.Harness;
            var rng = state.Random;

            int offerControlWeight = 1000 + (rng.Next(9) * 1000);
            bool dropTreatment = rng.Chance(8);

            var pinsBefore = SnapshotPins(state);

            harness.Serve(ConfigJson.New("v" + step)
                .Layer("offer_layout")
                .Layer("pricing_cta")
                .Experiment("exp_offer_layout", "offer_layout", status: PickStatus(rng), variants: dropTreatment
                    ? new[] { ConfigJson.Variant("control", 10000) }
                    : new[]
                    {
                        ConfigJson.Variant("control", offerControlWeight),
                        ConfigJson.Variant("treatment", 10000 - offerControlWeight)
                    })
                .Experiment("exp_pricing_cta", "pricing_cta", status: PickStatus(rng))
                .Build());

            NotePinsLost(state, pinsBefore);
        }

        private static void ForceOrClear(SoakState state)
        {
            if (state.Random.Chance(60))
            {
                state.Harness.Resolver.Overrides.ClearAll();
                return;
            }

            string experimentId = state.Random.Chance(50) ? "exp_offer_layout" : "exp_pricing_cta";
            state.Harness.Resolver.Overrides.Force(
                experimentId, state.Random.Chance(50) ? "control" : "treatment");
        }

        private static void WipePins(SoakState state)
        {
            var pinsBefore = SnapshotPins(state);
            state.Harness.Pins.Clear();
            NotePinsLost(state, pinsBefore);
        }

        private static string PickStatus(Rng rng)
        {
            switch (rng.Next(12))
            {
                case 0: return "paused";
                case 1: return "stopped";
                case 2: return "draft";
                default: return "running";
            }
        }

        // --- bookkeeping ------------------------------------------------------------------------------

        private static List<string> SnapshotPins(SoakState state)
        {
            var held = new List<string>();
            foreach (string experimentId in state.Harness.Pins.PinnedExperimentIds)
            {
                foreach (var pin in state.Harness.Pins.PinsFor(experimentId))
                {
                    held.Add(SoakState.Pair(pin.Key, experimentId));
                }
            }

            return held;
        }

        private static void NotePinsLost(SoakState state, List<string> pinsBefore)
        {
            var stillHeld = new HashSet<string>(SnapshotPins(state));
            for (int i = 0; i < pinsBefore.Count; i++)
            {
                if (!stillHeld.Contains(pinsBefore[i])) state.Disturbed.Add(pinsBefore[i]);
            }
        }

        // --- invariants -------------------------------------------------------------------------------

        private static void AssertCheapInvariants(SoakState state, string where)
        {
            var harness = state.Harness;

            // The sink and the tracker must agree about what was logged.
            Assert.That(harness.Exposures.LoggedCount, Is.EqualTo(state.ExposureEvents), where);
            Assert.That(harness.Events.TotalRecordedOf(AnalyticsEventKind.Exposure),
                Is.EqualTo(state.ExposureEvents), where + ": sink and tracker disagree on exposures");
            Assert.That(harness.Events.TotalRecordedOf(AnalyticsEventKind.Assignment),
                Is.EqualTo(state.AssignmentEvents), where + ": sink and tracker disagree on assignments");

            // Attributed plus unattributed always equals the total reported.
            Assert.That(harness.Conversions.AttributedCount + harness.Conversions.UnattributedCount,
                Is.EqualTo(harness.Conversions.TotalCount), where);
        }

        private static void AssertFullSweep(SoakState state, string where)
        {
            var harness = state.Harness;
            var config = harness.Snapshot.Config;

            // Never two experiments applied in one layer, and never a variant absent from the current
            // config.
            foreach (var layer in config.Layers)
            {
                for (int i = 0; i < UserPool; i += 7)
                {
                    var user = new UserContext("user-" + i, platform: "editor");
                    var assignment = harness.Resolver.Resolve(harness.Snapshot, user, layer.Id);
                    if (!assignment.IsAssigned) continue;

                    var experiment = config.FindExperiment(assignment.ExperimentId);
                    Assert.That(experiment, Is.Not.Null,
                        where + ": assigned to an experiment absent from the config");
                    Assert.That(experiment.FindVariant(assignment.VariantId), Is.Not.Null,
                        where + ": assigned to variant '" + assignment.VariantId +
                        "' which is absent from the current config");

                    if (assignment.IsForced) continue;

                    Assert.That(experiment.LayerId, Is.EqualTo(layer.Id), where);

                    int claimants = 0;
                    foreach (var candidate in config.Experiments)
                    {
                        if (!candidate.IsRunning) continue;
                        if (candidate.LayerId != layer.Id) continue;
                        if (candidate.Allocation.Contains(assignment.LayerBucket)) claimants++;
                    }

                    Assert.That(claimants, Is.LessThanOrEqualTo(1),
                        where + ": " + claimants + " experiments claim one bucket in layer " + layer.Id);
                }
            }

            // A user sees exactly one arm of an experiment unless their own pin was disturbed, and when
            // they see more than one the ledger says so.
            foreach (var userPair in state.ArmsSeen)
            {
                foreach (var experimentPair in userPair.Value)
                {
                    int distinct = experimentPair.Value.Count;
                    if (distinct <= 1) continue;

                    string pair = SoakState.Pair(userPair.Key, experimentPair.Key);

                    Assert.That(state.Disturbed, Does.Contain(pair),
                        where + ": " + userPair.Key + " saw " + distinct + " arms of " +
                        experimentPair.Key + " with nothing having disturbed that user's pin");

                    Assert.That(harness.Ledger.IsContaminated(userPair.Key, experimentPair.Key), Is.True,
                        where + ": " + pair + " saw " + distinct + " arms but is not flagged contaminated");

                    Assert.That(harness.Ledger.DistinctArmsSeen(userPair.Key, experimentPair.Key),
                        Is.EqualTo(distinct), where + ": the ledger's arm count disagrees with the test's");
                }
            }
        }

        private static int CountUsersSeenInTwoArms(SoakState state)
        {
            int count = 0;
            foreach (var userPair in state.ArmsSeen)
            {
                foreach (var experimentPair in userPair.Value)
                {
                    if (experimentPair.Value.Count > 1) count++;
                }
            }

            return count;
        }

        private static void Track(
            Dictionary<string, Dictionary<string, HashSet<string>>> armsSeen,
            string userId, string experimentId, string variantId)
        {
            if (!armsSeen.TryGetValue(userId, out var byExperiment))
            {
                byExperiment = new Dictionary<string, HashSet<string>>();
                armsSeen[userId] = byExperiment;
            }

            if (!byExperiment.TryGetValue(experimentId, out var arms))
            {
                arms = new HashSet<string>();
                byExperiment[experimentId] = arms;
            }

            arms.Add(variantId);
        }
    }
}
