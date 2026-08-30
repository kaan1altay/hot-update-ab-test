using System.Collections.Generic;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Model;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers stage two of assignment: splitting the users inside an experiment across its arms.
    /// </summary>
    [TestFixture]
    public sealed class VariantAssignerTests
    {
        private const int UserCount = 100000;

        [Test]
        public void TheSameUserAlwaysGetsTheSameVariant()
        {
            var experiment = TestConfigs.Experiment("exp_a", "layer_a");

            foreach (string user in TestConfigs.Users(1000))
            {
                var first = VariantAssigner.Assign(experiment, user);
                for (int repeat = 0; repeat < 5; repeat++)
                {
                    Assert.That(VariantAssigner.Assign(experiment, user), Is.SameAs(first));
                }
            }
        }

        [Test]
        public void ARebuiltEquivalentExperimentAssignsIdentically()
        {
            // Assignment must survive the config being re-fetched and re-parsed into new object instances.
            // If it depended on anything but id, salt, weights and order, this would drift.
            var first = TestConfigs.Experiment("exp_a", "layer_a");
            var second = TestConfigs.Experiment("exp_a", "layer_a");

            foreach (string user in TestConfigs.Users(2000))
            {
                Assert.That(VariantAssigner.Assign(second, user).Id,
                    Is.EqualTo(VariantAssigner.Assign(first, user).Id));
            }
        }

        [Test]
        public void TheArmsPartitionTheBucketSpaceExactly()
        {
            // Every bucket belongs to exactly one arm, with no gap and no overlap. Sweeping the space is a
            // complete proof of the split rather than a sample of it.
            var experiment = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant(VariantDef.ControlId, 2000),
                TestConfigs.Variant("b", 3000),
                TestConfigs.Variant("c", 5000)
            });

            var widths = new Dictionary<string, int>();

            for (int bucket = 0; bucket < TestConfigs.BucketCount; bucket++)
            {
                var variant = VariantAssigner.AssignAt(experiment, bucket);
                Assert.That(variant, Is.Not.Null, "bucket " + bucket + " belongs to no arm");

                widths.TryGetValue(variant.Id, out int seen);
                widths[variant.Id] = seen + 1;
            }

            Assert.That(widths[VariantDef.ControlId], Is.EqualTo(2000));
            Assert.That(widths["b"], Is.EqualTo(3000));
            Assert.That(widths["c"], Is.EqualTo(5000));
        }

        [Test]
        public void UsersAreSplitInProportionToTheWeights()
        {
            var experiment = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant(VariantDef.ControlId, 7000),
                TestConfigs.Variant("treatment", 3000)
            });

            var counts = CountAssignments(experiment, UserCount);

            var observed = new[] { counts[VariantDef.ControlId], counts["treatment"] };
            var expected = new[] { UserCount * 0.7, UserCount * 0.3 };
            double chiSquare = TestConfigs.ChiSquare(observed, expected);

            Assert.That(chiSquare, Is.LessThan(TestConfigs.ChiSquareCritical001(1)),
                "70/30 split came out " + observed[0] + "/" + observed[1] + ", chi-square = " + chiSquare);
        }

        [Test]
        public void UnevenWeightsThatDoNotSumToTheBucketCountStillSplitProportionally()
        {
            // Weights are shares, not percentages: an operator writing 1 and 3 means a quarter/three
            // quarters split and should not have to make the column add up to a round number.
            var experiment = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant(VariantDef.ControlId, 1),
                TestConfigs.Variant("treatment", 3)
            });

            var counts = CountAssignments(experiment, UserCount);

            var observed = new[] { counts[VariantDef.ControlId], counts["treatment"] };
            var expected = new[] { UserCount * 0.25, UserCount * 0.75 };
            double chiSquare = TestConfigs.ChiSquare(observed, expected);

            Assert.That(chiSquare, Is.LessThan(TestConfigs.ChiSquareCritical001(1)),
                "1:3 split came out " + observed[0] + "/" + observed[1] + ", chi-square = " + chiSquare);
        }

        [Test]
        public void VariantAssignmentIsIndependentOfPositionInTheLayer()
        {
            // Why the variant hash is salted separately from the layer hash. Users are partitioned by their
            // layer bucket into the bottom and top halves of the layer; both halves must still split evenly
            // across the arms. If the two hashes were the same, one half would be all control and the other
            // all treatment.
            var layer = TestConfigs.Layer("layer_a");
            var experiment = TestConfigs.Experiment("exp_a", "layer_a");

            int lowHalfTreatment = 0, lowHalfTotal = 0;
            int highHalfTreatment = 0, highHalfTotal = 0;

            foreach (string user in TestConfigs.Users(UserCount))
            {
                bool lowHalf = LayerAllocator.BucketOf(user, layer) < TestConfigs.BucketCount / 2;
                bool treatment = VariantAssigner.Assign(experiment, user).Id == "treatment";

                if (lowHalf)
                {
                    lowHalfTotal++;
                    if (treatment) lowHalfTreatment++;
                }
                else
                {
                    highHalfTotal++;
                    if (treatment) highHalfTreatment++;
                }
            }

            double lowRate = lowHalfTreatment / (double)lowHalfTotal;
            double highRate = highHalfTreatment / (double)highHalfTotal;

            Assert.That(lowRate, Is.EqualTo(0.5).Within(0.01),
                "treatment rate in the bottom half of the layer was " + lowRate);
            Assert.That(highRate, Is.EqualTo(0.5).Within(0.01),
                "treatment rate in the top half of the layer was " + highRate);
        }

        [Test]
        public void ChangingTheWeightsMovesOnlyTheUsersNearTheBoundary()
        {
            // The stateless reshuffle that the sticky-after-exposure policy exists to contain. Moving the
            // boundary from 50/50 to 60/40 must move about a tenth of users and leave the other ninety
            // percent where they were - it is a boundary shift, not a rehash.
            var before = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant(VariantDef.ControlId, 5000),
                TestConfigs.Variant("treatment", 5000)
            });

            var after = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant(VariantDef.ControlId, 6000),
                TestConfigs.Variant("treatment", 4000)
            });

            int moved = 0;
            foreach (string user in TestConfigs.Users(UserCount))
            {
                if (VariantAssigner.Assign(before, user).Id != VariantAssigner.Assign(after, user).Id) moved++;
            }

            double movedFraction = moved / (double)UserCount;

            Assert.That(movedFraction, Is.EqualTo(0.10).Within(0.01),
                "expected about 10% of users to cross the boundary, saw " + movedFraction);
        }

        [Test]
        public void ReorderingTheArmsRebucketsUsers()
        {
            // Declared order is part of the bucketing contract, not an implementation detail. This test
            // exists so the fact is recorded rather than discovered later by an operator who tidied a config
            // file alphabetically and reshuffled a live experiment.
            var declared = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant(VariantDef.ControlId, 5000),
                TestConfigs.Variant("treatment", 5000)
            });

            var reordered = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant("treatment", 5000),
                TestConfigs.Variant(VariantDef.ControlId, 5000)
            });

            int moved = 0;
            foreach (string user in TestConfigs.Users(1000))
            {
                if (VariantAssigner.Assign(declared, user).Id != VariantAssigner.Assign(reordered, user).Id) moved++;
            }

            Assert.That(moved, Is.EqualTo(1000),
                "swapping two equally weighted arms should swap every user; order is contractual");
        }

        [Test]
        public void AZeroWeightArmIsNeverAssigned()
        {
            var experiment = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant(VariantDef.ControlId, 5000),
                TestConfigs.Variant("retired", 0),
                TestConfigs.Variant("treatment", 5000)
            });

            for (int bucket = 0; bucket < TestConfigs.BucketCount; bucket++)
            {
                Assert.That(VariantAssigner.AssignAt(experiment, bucket).Id, Is.Not.EqualTo("retired"));
            }
        }

        [Test]
        public void AnExperimentWhereEveryWeightIsZeroAssignsNobody()
        {
            // A live experiment holding no traffic. Returning null rather than defaulting to the first arm
            // keeps the caller from putting users in an arm the operator explicitly emptied.
            var experiment = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant(VariantDef.ControlId, 0),
                TestConfigs.Variant("treatment", 0)
            });

            Assert.That(VariantAssigner.Assign(experiment, "user-1"), Is.Null);
            Assert.That(VariantAssigner.AssignAt(experiment, 0), Is.Null);
            Assert.That(VariantAssigner.AssignAt(experiment, TestConfigs.BucketCount - 1), Is.Null);
        }

        [Test]
        public void ASingleArmTakesEveryUser()
        {
            var experiment = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant(VariantDef.ControlId, 1)
            });

            for (int bucket = 0; bucket < TestConfigs.BucketCount; bucket++)
            {
                Assert.That(VariantAssigner.AssignAt(experiment, bucket).Id, Is.EqualTo(VariantDef.ControlId));
            }
        }

        [Test]
        public void WeightsLargeEnoughToOverflowA32BitMultiplyStillSplitCorrectly()
        {
            // bucket * totalWeight exceeds int.MaxValue once the total passes about 214,748. The comparison
            // is done in 64-bit arithmetic for exactly this reason; without it the split silently inverts.
            var experiment = TestConfigs.Experiment("exp_a", "layer_a", new[]
            {
                TestConfigs.Variant(VariantDef.ControlId, 1000000000),
                TestConfigs.Variant("treatment", 1000000000)
            });

            int control = 0;
            for (int bucket = 0; bucket < TestConfigs.BucketCount; bucket++)
            {
                if (VariantAssigner.AssignAt(experiment, bucket).IsControl) control++;
            }

            Assert.That(control, Is.EqualTo(TestConfigs.BucketCount / 2));
        }

        [Test]
        public void NullArgumentsAreRejected()
        {
            var experiment = TestConfigs.Experiment("exp_a", "layer_a");

            Assert.That(() => VariantAssigner.Assign(null, "u"), Throws.ArgumentNullException);
            Assert.That(() => VariantAssigner.Assign(experiment, null), Throws.ArgumentNullException);
        }

        private static Dictionary<string, int> CountAssignments(ExperimentDef experiment, int userCount)
        {
            var counts = new Dictionary<string, int>();
            foreach (var variant in experiment.Variants) counts[variant.Id] = 0;

            foreach (string user in TestConfigs.Users(userCount))
            {
                counts[VariantAssigner.Assign(experiment, user).Id]++;
            }

            return counts;
        }
    }
}
