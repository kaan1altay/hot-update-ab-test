using System;
using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Telemetry;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers the aggregate the metrics panel reads: stated populations, the two health signals, and the
    /// printable table.
    /// </summary>
    [TestFixture]
    public sealed class MetricsAggregatorTests
    {
        private TelemetryHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new TelemetryHarness();
        }

        // --- the two breakage buttons, and why each needs a different signal --------------------------

        [Test]
        public void SuppressedExposureLoggingIsCaughtByMeasuringSrmOverExposures()
        {
            // The demo's "make one variant skip exposure logging" button. Assignments stay a perfect
            // 50/50 while half the data is silently destroyed.
            _h.SuppressExposureForVariant = "treatment";
            _h.SimulateUsers(4000);

            var experiment = _h.Experiment("exp_offer_layout");
            var control = _h.Arm("exp_offer_layout", "control");
            var treatment = _h.Arm("exp_offer_layout", "treatment");

            Assert.That(control.UsersAssigned, Is.EqualTo(treatment.UsersAssigned).Within(200),
                "the assignment split is untouched, which is the whole trap");

            Assert.That(experiment.Srm.State, Is.EqualTo(SrmState.Alarm),
                "measuring over exposures catches it:\n" + _h.Report().Describe());

            // And the funnel rate is what says *which* of the two faults it is.
            Assert.That(control.ExposureRate, Is.EqualTo(1.0).Within(0.01));
            Assert.That(treatment.ExposureRate, Is.Zero, "the collapsed arm names itself");
        }

        [Test]
        public void AnSrmCheckOverAssignmentsWouldHaveMissedIt()
        {
            // The negative control for the decision above. Feeding the same run's *assignment* counts to
            // the same checker returns healthy, so the light would have stayed green while the data was
            // being destroyed. This is why SRM is measured over the exposed population.
            _h.SuppressExposureForVariant = "treatment";
            _h.SimulateUsers(4000);

            var control = _h.Arm("exp_offer_layout", "control");
            var treatment = _h.Arm("exp_offer_layout", "treatment");

            var overAssignments = SrmCheck.Evaluate(new[]
            {
                new SrmObservation("control", control.UsersAssigned, 5000),
                new SrmObservation("treatment", treatment.UsersAssigned, 5000)
            });

            Assert.That(overAssignments.State, Is.EqualTo(SrmState.Healthy),
                "if this ever alarms, the negative control has stopped demonstrating anything");
            Assert.That(_h.Experiment("exp_offer_layout").Srm.State, Is.EqualTo(SrmState.Alarm));
        }

        [Test]
        public void SkewedBucketingShowsUpAsASkewedSplitWithHealthyFunnelRates()
        {
            // The other breakage button. Both faults skew the exposed split; the funnel rate is what tells
            // them apart, so a reader can act on the light rather than just noticing it.
            _h.Serve(ConfigJson.New("2")
                .Layer("offer_layout")
                .Layer("pricing_cta")
                .Experiment("exp_offer_layout", "offer_layout", variants: new[]
                {
                    ConfigJson.Variant("control", 8000),
                    ConfigJson.Variant("treatment", 2000)
                }, salt: "skewed")
                .Experiment("exp_pricing_cta", "pricing_cta")
                .Build());

            // The config the *check* compares against still says 50/50, standing in for bucketing that has
            // drifted away from what the operator configured.
            _h.SimulateUsers(4000);

            var experiment = _h.Experiment("exp_offer_layout");
            var control = _h.Arm("exp_offer_layout", "control");
            var treatment = _h.Arm("exp_offer_layout", "treatment");

            Assert.That(control.UsersExposed, Is.GreaterThan(treatment.UsersExposed * 3));
            Assert.That(control.ExposureRate, Is.EqualTo(1.0).Within(0.01));
            Assert.That(treatment.ExposureRate, Is.EqualTo(1.0).Within(0.01),
                "both arms render fine; it is the split itself that is wrong");
            Assert.That(experiment.Srm.State, Is.EqualTo(SrmState.Healthy),
                "the 80/20 config matches the 80/20 observation, so this run is internally consistent");
        }

        [Test]
        public void AHealthyRunIsHealthyOnBothSignals()
        {
            _h.SimulateUsers(4000);

            var experiment = _h.Experiment("exp_offer_layout");

            Assert.That(experiment.Srm.State, Is.EqualTo(SrmState.Healthy), _h.Report().Describe());
            foreach (var variant in experiment.Variants)
            {
                Assert.That(variant.ExposureRate, Is.EqualTo(1.0).Within(0.01), variant.VariantId);
            }
        }

        // --- populations ------------------------------------------------------------------------------

        [Test]
        public void ForcedTrafficIsExcludedFromTheHeadlineNumbersAndFromSrm()
        {
            // A QA override is a deliberate ratio violation. Counting it would make the check alarm about
            // somebody doing their job.
            _h.SimulateUsers(2000);
            _h.Resolver.Overrides.Force("exp_offer_layout", "treatment");
            for (int i = 0; i < 800; i++) _h.Visit("qa-" + i);

            var analysis = _h.Experiment("exp_offer_layout");
            var everything = _h.Experiment("exp_offer_layout", MetricsPopulation.Everything);

            Assert.That(analysis.Srm.State, Is.EqualTo(SrmState.Healthy), _h.Report().Describe());
            Assert.That(everything.Srm.State, Is.EqualTo(SrmState.Alarm),
                "including forced traffic would manufacture exactly the false alarm we avoid");
        }

        [Test]
        public void SyntheticTrafficIsIncludedByDefaultButSeparable()
        {
            _h.SimulateUsers(200);

            Assert.That(_h.Arm("exp_offer_layout", "control").UsersExposed, Is.GreaterThan(0));
            Assert.That(_h.Arm("exp_offer_layout", "control", MetricsPopulation.RealTrafficOnly).UsersExposed,
                Is.Zero, "the simulator's traffic must be identifiable as such");
        }

        [Test]
        public void EveryReportNamesThePopulationItWasComputedOver()
        {
            Assert.That(_h.Report().Describe(), Does.Contain("population: analysis"));
            Assert.That(_h.Report(MetricsPopulation.Everything).Describe(),
                Does.Contain("population: everything, including forced"));
        }

        [Test]
        public void AUserCountedInTwoTraitBucketsIsNotCountedTwice()
        {
            // Someone hand-testing while a simulation runs produces both synthetic and plain events for the
            // same user. Summing the buckets rather than unioning them would inflate the population.
            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");

            _h.Exposures.MarkExposed(user, assignment, new SessionId("s1"), synthetic: true);
            _h.Exposures.MarkExposed(user, assignment, new SessionId("s2"), synthetic: false);

            var arm = _h.Arm("exp_offer_layout", assignment.VariantId);

            Assert.That(arm.Exposures, Is.EqualTo(2));
            Assert.That(arm.UsersExposed, Is.EqualTo(1));
        }

        // --- config changes -----------------------------------------------------------------------------

        [Test]
        public void ADeletedArmStillShowsItsRecordedTrafficAsAnOrphan()
        {
            // Hiding it would make a deleted experiment's traffic vanish from the totals with no
            // explanation, which looks exactly like data loss.
            _h.SimulateUsers(400);

            _h.Serve(ConfigJson.New("2")
                .Layer("offer_layout")
                .Layer("pricing_cta")
                .Experiment("exp_offer_layout", "offer_layout", variants: new[]
                {
                    ConfigJson.Variant("control", 10000)
                })
                .Experiment("exp_pricing_cta", "pricing_cta")
                .Build());

            var orphan = _h.Arm("exp_offer_layout", "treatment");

            Assert.That(orphan.IsOrphaned, Is.True);
            Assert.That(orphan.UsersExposed, Is.GreaterThan(0));
            Assert.That(_h.Report().Describe(), Does.Contain("treatment*"));
        }

        [Test]
        public void AnExperimentRemovedFromTheConfigStillAppears()
        {
            _h.SimulateUsers(200);
            _h.Serve(ConfigJson.New("2").Layer("pricing_cta").Experiment("exp_pricing_cta", "pricing_cta").Build());

            var gone = _h.Experiment("exp_offer_layout");

            Assert.That(gone.Status, Is.EqualTo("not in config"));
            Assert.That(gone.Srm.State, Is.EqualTo(SrmState.Unknown));
        }

        // --- the printable table --------------------------------------------------------------------------

        [Test]
        public void TheReportPrintsAsATable()
        {
            _h.SimulateUsers(3000, conversionRate: 0.12);

            string table = _h.Report().Describe();

            Assert.That(table, Does.Contain("experiment").And.Contain("variant").And.Contain("assigned"));
            Assert.That(table, Does.Contain("exp_offer_layout"));
            Assert.That(table, Does.Contain("SRM ["));
            Assert.That(table, Does.Contain("unattributed conversions:"));

            // Printed so a real table can be eyeballed in the batchmode log rather than something odd
            // being discovered two slices later.
            TestContext.WriteLine("\n" + table);
        }

        [Test]
        public void ASimulatedPopulationProducesPlausibleNumbers()
        {
            _h.SimulateUsers(5000, conversionRate: 0.20);

            var control = _h.Arm("exp_offer_layout", "control");
            var treatment = _h.Arm("exp_offer_layout", "treatment");

            Assert.That(control.UsersExposed + treatment.UsersExposed, Is.EqualTo(5000));
            Assert.That(control.UsersExposed, Is.EqualTo(2500).Within(150));
            Assert.That(control.ConversionRate, Is.EqualTo(0.20).Within(0.03));
            Assert.That(treatment.ConversionRate, Is.EqualTo(0.20).Within(0.03));
            Assert.That(_h.Experiment("exp_offer_layout").Srm.State, Is.EqualTo(SrmState.Healthy));

            TestContext.WriteLine("\n" + _h.Report().Describe());
        }

        // --- cost --------------------------------------------------------------------------------------------

        [Test]
        public void AggregationCostDoesNotGrowWithHistory()
        {
            // Recomputing the aggregate per event would be quadratic and would visibly stall the demo on
            // "simulate 5000 users". Timing is a blunt instrument, so this asserts on the shape of the
            // growth rather than on absolute milliseconds: ten times the events should not cost anything
            // like a hundred times the work.
            var first = Time(() => new TelemetryHarness().SimulateUsers(500));
            var tenfold = Time(() => new TelemetryHarness().SimulateUsers(5000));

            Assert.That(tenfold, Is.LessThan(first * 30 + TimeSpan.FromSeconds(2).TotalMilliseconds),
                "aggregation appears to be super-linear: 500 users took " + first + " ms, 5000 took " +
                tenfold + " ms");
        }

        private static double Time(Action action)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }
    }
}
