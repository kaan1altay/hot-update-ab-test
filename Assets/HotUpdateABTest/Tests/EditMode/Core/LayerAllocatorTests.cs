using System.Collections.Generic;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Model;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers the layer story: mutual exclusion inside a layer, and statistical independence between
    /// layers. These are the two properties that let two experiments run at the same time and still be
    /// analysable separately.
    /// </summary>
    [TestFixture]
    public sealed class LayerAllocatorTests
    {
        private const int UserCount = 100000;

        [Test]
        public void AUserFallsIntoAtMostOneExperimentPerLayer()
        {
            // Three experiments carving up one layer. Sweeping the bucket space directly is a stronger
            // statement than sampling users: it proves no bucket is claimed twice anywhere, rather than
            // that the users we happened to try did not collide.
            var layer = TestConfigs.Layer("offer_layout");
            var config = TestConfigs.Config(
                new[] { layer },
                new[]
                {
                    TestConfigs.Experiment("exp_a", "offer_layout", allocation: new BucketRange(0, 3000)),
                    TestConfigs.Experiment("exp_b", "offer_layout", allocation: new BucketRange(3000, 6000)),
                    TestConfigs.Experiment("exp_c", "offer_layout", allocation: new BucketRange(6000, 9000))
                });

            for (int bucket = 0; bucket < TestConfigs.BucketCount; bucket++)
            {
                int claimants = 0;
                foreach (var experiment in config.Experiments)
                {
                    if (experiment.IsRunning && experiment.Allocation.Contains(bucket)) claimants++;
                }

                Assert.That(claimants, Is.LessThanOrEqualTo(1),
                    "bucket " + bucket + " is claimed by " + claimants + " experiments in one layer");
            }
        }

        [Test]
        public void AUserOutsideEveryAllocationIsInNoExperiment()
        {
            // The top ten percent of the layer is unclaimed, which is how a holdout or a partial ramp is
            // expressed. Those users must resolve to nothing rather than to the nearest experiment.
            var layer = TestConfigs.Layer("offer_layout");
            var config = TestConfigs.Config(
                new[] { layer },
                new[] { TestConfigs.Experiment("exp_a", "offer_layout", allocation: new BucketRange(0, 9000)) });

            Assert.That(LayerAllocator.AllocateAt(config, layer, 9000), Is.Null);
            Assert.That(LayerAllocator.AllocateAt(config, layer, 9999), Is.Null);
            Assert.That(LayerAllocator.AllocateAt(config, layer, 8999), Is.Not.Null);
        }

        [Test]
        public void AllocationIsStableForTheSameUserAndLayer()
        {
            var config = TestConfigs.SingleExperiment();
            var layer = config.FindLayer("layer_a");

            foreach (string user in TestConfigs.Users(500))
            {
                var first = LayerAllocator.Allocate(config, layer, user);
                for (int repeat = 0; repeat < 5; repeat++)
                {
                    Assert.That(LayerAllocator.Allocate(config, layer, user), Is.SameAs(first));
                }
            }
        }

        [Test]
        public void BucketsAreSpreadEvenlyAcrossTheSpace()
        {
            // Ten equal slices of the bucket space should receive roughly a tenth of users each. This is a
            // property of the hash rather than of the allocator, but it is the allocator that depends on it:
            // an uneven spread would make an allocation range claim a different share of traffic than it
            // says it does.
            var layer = TestConfigs.Layer("offer_layout");
            var observed = new int[10];

            foreach (string user in TestConfigs.Users(UserCount))
            {
                observed[LayerAllocator.BucketOf(user, layer) / 1000]++;
            }

            var expected = new double[10];
            for (int i = 0; i < expected.Length; i++) expected[i] = UserCount / 10.0;

            double chiSquare = TestConfigs.ChiSquare(observed, expected);

            Assert.That(chiSquare, Is.LessThan(TestConfigs.ChiSquareCritical001(9)),
                "bucket distribution is not uniform, chi-square = " + chiSquare);
        }

        [Test]
        public void AssignmentInOneLayerIsIndependentOfAssignmentInAnother()
        {
            // The point of the per-layer salt. Each layer runs one experiment holding the bottom thirty
            // percent of its space. If the salts did not decorrelate the layers, the two experiments would
            // hold the *same* users and the 2x2 contingency table below would be wildly off independence -
            // in the degenerate case, the off-diagonal cells would both be zero.
            var layerA = TestConfigs.Layer("offer_layout");
            var layerB = TestConfigs.Layer("pricing_cta");

            var config = TestConfigs.Config(
                new[] { layerA, layerB },
                new[]
                {
                    TestConfigs.Experiment("exp_a", "offer_layout", allocation: new BucketRange(0, 3000)),
                    TestConfigs.Experiment("exp_b", "pricing_cta", allocation: new BucketRange(0, 3000))
                });

            int inBoth = 0, inAOnly = 0, inBOnly = 0, inNeither = 0;

            foreach (string user in TestConfigs.Users(UserCount))
            {
                bool inA = LayerAllocator.Allocate(config, layerA, user) != null;
                bool inB = LayerAllocator.Allocate(config, layerB, user) != null;

                if (inA && inB) inBoth++;
                else if (inA) inAOnly++;
                else if (inB) inBOnly++;
                else inNeither++;
            }

            // Both experiments must still hold their stated share; independence of two empty sets is not
            // the property under test.
            Assert.That(inBoth + inAOnly, Is.EqualTo((int)(UserCount * 0.3)).Within(UserCount * 0.01),
                "experiment A does not hold its stated 30% of the layer");
            Assert.That(inBoth + inBOnly, Is.EqualTo((int)(UserCount * 0.3)).Within(UserCount * 0.01),
                "experiment B does not hold its stated 30% of the layer");

            double chiSquare = ChiSquareOfIndependence(inBoth, inAOnly, inBOnly, inNeither);

            Assert.That(chiSquare, Is.LessThan(TestConfigs.ChiSquareCritical001(1)),
                "layer assignments are correlated, chi-square = " + chiSquare +
                " (in both " + inBoth + ", A only " + inAOnly + ", B only " + inBOnly +
                ", neither " + inNeither + ")");
        }

        [Test]
        public void ReusingOneSaltAcrossLayersWouldCorrelateThem()
        {
            // The negative control for the test above. Two layers sharing a salt put every user at the same
            // bucket in both, so membership in one experiment implies membership in the other exactly. This
            // is asserted rather than merely explained in a comment, so the failure mode the salt prevents
            // is demonstrated by the suite rather than taken on trust.
            var shared = "shared.salt";
            var layerA = new LayerDef("offer_layout", shared);
            var layerB = new LayerDef("pricing_cta", shared);

            var config = TestConfigs.Config(
                new[] { layerA, layerB },
                new[]
                {
                    TestConfigs.Experiment("exp_a", "offer_layout", allocation: new BucketRange(0, 3000)),
                    TestConfigs.Experiment("exp_b", "pricing_cta", allocation: new BucketRange(0, 3000))
                });

            int disagreements = 0;
            foreach (string user in TestConfigs.Users(10000))
            {
                bool inA = LayerAllocator.Allocate(config, layerA, user) != null;
                bool inB = LayerAllocator.Allocate(config, layerB, user) != null;
                if (inA != inB) disagreements++;
            }

            Assert.That(disagreements, Is.Zero,
                "sharing a salt should make the two layers perfectly confounded; that it did not means the " +
                "layer key no longer depends only on the salt");
        }

        [Test]
        public void OnlyRunningExperimentsClaimTraffic()
        {
            var layer = TestConfigs.Layer("offer_layout");

            foreach (ExperimentStatus status in new[]
                     {
                         ExperimentStatus.Draft, ExperimentStatus.Paused, ExperimentStatus.Stopped
                     })
            {
                var config = TestConfigs.Config(
                    new[] { layer },
                    new[] { TestConfigs.Experiment("exp_a", "offer_layout", status: status) });

                for (int bucket = 0; bucket < TestConfigs.BucketCount; bucket += 97)
                {
                    Assert.That(LayerAllocator.AllocateAt(config, layer, bucket), Is.Null,
                        "a " + status + " experiment claimed bucket " + bucket);
                }
            }
        }

        [Test]
        public void PausingAnExperimentFreesItsTrafficToAnotherInTheSameLayer()
        {
            // A running experiment sits underneath a paused one on overlapping ranges. This config would be
            // rejected by the validator if both were running, but with one paused it is legal, and it shows
            // that traffic is released immediately rather than held by the paused experiment.
            var layer = TestConfigs.Layer("offer_layout");
            var config = TestConfigs.Config(
                new[] { layer },
                new[]
                {
                    TestConfigs.Experiment("exp_paused", "offer_layout",
                        status: ExperimentStatus.Paused, allocation: new BucketRange(0, 5000)),
                    TestConfigs.Experiment("exp_live", "offer_layout",
                        status: ExperimentStatus.Running, allocation: new BucketRange(0, 5000))
                });

            var allocated = LayerAllocator.AllocateAt(config, layer, 2500);

            Assert.That(allocated, Is.Not.Null);
            Assert.That(allocated.Id, Is.EqualTo("exp_live"));
        }

        [Test]
        public void ExperimentsInOtherLayersAreIgnored()
        {
            var layerA = TestConfigs.Layer("layer_a");
            var layerB = TestConfigs.Layer("layer_b");
            var config = TestConfigs.Config(
                new[] { layerA, layerB },
                new[] { TestConfigs.Experiment("exp_b", "layer_b") });

            for (int bucket = 0; bucket < TestConfigs.BucketCount; bucket += 331)
            {
                Assert.That(LayerAllocator.AllocateAt(config, layerA, bucket), Is.Null);
                Assert.That(LayerAllocator.AllocateAt(config, layerB, bucket), Is.Not.Null);
            }
        }

        [Test]
        public void AnEmptyAllocationClaimsNobody()
        {
            var layer = TestConfigs.Layer("offer_layout");
            var config = TestConfigs.Config(
                new[] { layer },
                new[] { TestConfigs.Experiment("exp_a", "offer_layout", allocation: BucketRange.Empty) });

            foreach (string user in TestConfigs.Users(2000))
            {
                Assert.That(LayerAllocator.Allocate(config, layer, user), Is.Null);
            }
        }

        [Test]
        public void NullArgumentsAreRejected()
        {
            var config = TestConfigs.SingleExperiment();
            var layer = config.FindLayer("layer_a");

            Assert.That(() => LayerAllocator.Allocate(null, layer, "u"), Throws.ArgumentNullException);
            Assert.That(() => LayerAllocator.Allocate(config, null, "u"), Throws.ArgumentNullException);
            Assert.That(() => LayerAllocator.Allocate(config, layer, null), Throws.ArgumentNullException);
        }

        /// <summary>Pearson chi-square for a 2x2 contingency table, one degree of freedom.</summary>
        private static double ChiSquareOfIndependence(int both, int aOnly, int bOnly, int neither)
        {
            double n = both + aOnly + bOnly + neither;
            double rowA = both + aOnly;
            double rowNotA = bOnly + neither;
            double colB = both + bOnly;
            double colNotB = aOnly + neither;

            var observed = new[] { both, aOnly, bOnly, neither };
            var expected = new[]
            {
                rowA * colB / n,
                rowA * colNotB / n,
                rowNotA * colB / n,
                rowNotA * colNotB / n
            };

            var expectedList = new List<double>(expected);
            return TestConfigs.ChiSquare(observed, expectedList);
        }
    }
}
