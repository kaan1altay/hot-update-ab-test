using System.Collections.Generic;
using HotUpdateABTest.Core.Telemetry;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers the sample-ratio check in isolation: its floors, its threshold, and its arithmetic.
    /// </summary>
    [TestFixture]
    public sealed class SrmCheckTests
    {
        private static SrmResult Evaluate(params (string variant, long observed, long weight)[] arms)
        {
            var observations = new List<SrmObservation>();
            foreach (var arm in arms) observations.Add(new SrmObservation(arm.variant, arm.observed, arm.weight));
            return SrmCheck.Evaluate(observations);
        }

        [Test]
        public void AnEvenSplitOverAnEvenConfigurationIsHealthy()
        {
            var result = Evaluate(("control", 5000, 5000), ("treatment", 5000, 5000));

            Assert.That(result.State, Is.EqualTo(SrmState.Healthy));
            Assert.That(result.ChiSquare, Is.EqualTo(0).Within(0.0001));
            Assert.That(result.DegreesOfFreedom, Is.EqualTo(1));
        }

        [Test]
        public void ASplitMatchingUnevenWeightsIsHealthy()
        {
            Assert.That(Evaluate(("control", 7000, 70), ("treatment", 3000, 30)).State,
                Is.EqualTo(SrmState.Healthy));
        }

        [Test]
        public void NormalSamplingNoiseDoesNotRaiseAnAlarm()
        {
            // 50.7/49.3 over ten thousand users is an utterly ordinary sample. At the p < 0.05 threshold a
            // naive check would fire on this sort of thing constantly, which is exactly why the alarm sits
            // at 0.0005.
            Assert.That(Evaluate(("control", 5070, 5000), ("treatment", 4930, 5000)).State,
                Is.EqualTo(SrmState.Healthy));
        }

        [Test]
        public void ABadlySkewedSplitRaisesAnAlarm()
        {
            var result = Evaluate(("control", 6000, 5000), ("treatment", 4000, 5000));

            Assert.That(result.State, Is.EqualTo(SrmState.Alarm));
            Assert.That(result.ChiSquare, Is.GreaterThan(result.CriticalValue));
            Assert.That(result.Explanation, Does.Contain("not plausibly the configured one"));
        }

        [Test]
        public void ThreeArmsUseTwoDegreesOfFreedom()
        {
            var result = Evaluate(("control", 3400, 1), ("b", 3300, 1), ("c", 3300, 1));

            Assert.That(result.DegreesOfFreedom, Is.EqualTo(2));
            Assert.That(result.CriticalValue, Is.EqualTo(15.202).Within(0.001));
            Assert.That(result.State, Is.EqualTo(SrmState.Healthy));
        }

        // --- the floors ------------------------------------------------------------------------------

        [Test]
        public void ThreeAgainstOneIsNotEvidenceOfAnything()
        {
            // The failure this floor exists to prevent: a naive chi-square on four users produces a
            // statistic of 1.0, and on a handful more it starts producing small p-values. A status light
            // that flashes red on the demo's first click gets ignored by the third.
            var result = Evaluate(("control", 3, 5000), ("treatment", 1, 5000));

            Assert.That(result.State, Is.EqualTo(SrmState.Unknown));
            Assert.That(result.State, Is.Not.EqualTo(SrmState.Healthy),
                "'we cannot tell yet' and 'we checked and it is fine' are different claims");
            Assert.That(result.Explanation, Does.Contain("at least 100"));
        }

        [Test]
        public void TheStateBelowTheFloorIsUnknownEvenWhenTheSplitIsPerfect()
        {
            Assert.That(Evaluate(("control", 20, 5000), ("treatment", 20, 5000)).State,
                Is.EqualTo(SrmState.Unknown));
        }

        [Test]
        public void ATinyArmSuppressesTheVerdictUntilItsExpectedCountIsUsable()
        {
            // Total is well over the practical floor, but the 0.02% arm expects under five users, and
            // chi-square is not valid on a cell that small.
            var result = Evaluate(("control", 9998, 9998), ("canary", 2, 2));

            Assert.That(result.State, Is.EqualTo(SrmState.Unknown));
            Assert.That(result.Explanation, Does.Contain("at least 5"));
        }

        [Test]
        public void ExactlyAtTheTotalFloorTheCheckRuns()
        {
            Assert.That(Evaluate(("control", 50, 5000), ("treatment", 50, 5000)).State,
                Is.EqualTo(SrmState.Healthy));
        }

        // --- degenerate configurations ---------------------------------------------------------------------

        [Test]
        public void OneArmHoldingAllTheTrafficHasNoRatioToCheck()
        {
            var result = Evaluate(("control", 10000, 10000));

            Assert.That(result.State, Is.EqualTo(SrmState.Unknown));
            Assert.That(result.Explanation, Does.Contain("no ratio to check"));
        }

        [Test]
        public void UsersFoundInAZeroWeightArmAreAHardAlarm()
        {
            // Not a statistical question. Somebody is in an arm the operator explicitly emptied.
            var result = Evaluate(("control", 5000, 5000), ("treatment", 5000, 5000), ("retired", 3, 0));

            Assert.That(result.State, Is.EqualTo(SrmState.Alarm));
            Assert.That(result.Explanation, Does.Contain("no traffic at all"));
        }

        [Test]
        public void AZeroWeightArmWithNobodyInItIsIgnored()
        {
            Assert.That(Evaluate(("control", 5000, 5000), ("treatment", 5000, 5000), ("retired", 0, 0)).State,
                Is.EqualTo(SrmState.Healthy));
        }

        [Test]
        public void NoObservationsAtAllIsUnknown()
        {
            Assert.That(Evaluate(("control", 0, 5000), ("treatment", 0, 5000)).State,
                Is.EqualTo(SrmState.Unknown));
        }

        [Test]
        public void ManyArmsFallBackToAnApproximationRatherThanRefusingToJudge()
        {
            // Beyond the tabulated range the critical value is approximated instead of the check giving up.
            var observations = new List<SrmObservation>();
            for (int i = 0; i < 20; i++) observations.Add(new SrmObservation("v" + i, 500, 1));

            var result = SrmCheck.Evaluate(observations);

            Assert.That(result.DegreesOfFreedom, Is.EqualTo(19));
            Assert.That(result.CriticalValue, Is.GreaterThan(40).And.LessThan(60));
            Assert.That(result.State, Is.EqualTo(SrmState.Healthy));
        }
    }
}
