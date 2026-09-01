using HotUpdateABTest.Core;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Telemetry;
using HotUpdateABTest.Demo;
using HotUpdateABTest.Transport;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Unity
{
    /// <summary>
    /// For every state a LiveOps control can put the demo into, asserts there is a control that takes it
    /// back out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried over from the red-dot repository, where two of the three bugs hand play-testing found were
    /// of the "nothing makes this false again" class - a toggle that could be set but never cleared. That
    /// is the failure mode manual testing reliably catches, so catching it first is worth the effort.
    /// </para>
    /// <para>
    /// The pairs are tabulated in <c>docs/STATUS.md</c>. One is deliberately asymmetric and is asserted as
    /// such rather than being quietly omitted: restoring the weights does not restore the arms of users who
    /// were already exposed, because that is the sticky policy working.
    /// </para>
    /// <para>
    /// Driven through <see cref="AbTestDemoController"/>, which has no FairyGUI dependency, so every LiveOps
    /// action is exercised without a screen.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class DemoActionPairTests
    {
        private ListLog _log;
        private LuaFixture _lua;
        private AbTestDemoController _demo;

        [SetUp]
        public void SetUp()
        {
            _log = new ListLog();
            _lua = new LuaFixture();

            // No HTTP: the pairs are about demo state, not about sockets, and LocalConfigServerTests
            // already covers the transport. Skipping the bind also keeps this fixture from fighting a
            // running demo for a port.
            _demo = new AbTestDemoController(_log, SystemClock.Instance, _lua.Host, preferHttp: false);
            _demo.Start();
        }

        [TearDown]
        public void TearDown()
        {
            _demo?.Dispose();
            _lua?.Dispose();
        }

        private long ExposedUsers()
        {
            long total = 0;
            foreach (var experiment in _demo.BuildReport().Experiments)
            {
                foreach (var variant in experiment.Variants) total += variant.UsersExposed;
            }

            return total;
        }

        // --- second play-test pass, findings 1 and 3: what a second Simulate press can and cannot do ---

        [Test]
        public void SimulatingTwiceCanStillMoveTheRatioVerdict()
        {
            // Break, simulate, fix, simulate, break, simulate. On camera the light went red, then green,
            // then stayed green no matter how much skew arrived afterwards.
            //
            // The aggregator is not at fault - MetricsAggregatorTests shows it moves the verdict happily
            // when each run brings fresh identities. The simulator reuses sim-0..4999 on every press, and
            // UsersExposed is a set of user ids, so after the first run the exposed population can never
            // grow again and no later run can change what the ratio check sees.
            _demo.OnButton("btnScenarioNormal");

            _demo.SetExposureSkipping(true);
            _demo.SimulateUsers(2000);
            Assert.That(Verdict(), Is.EqualTo(SrmState.Alarm), "the first broken run alarms");

            _demo.SetExposureSkipping(false);
            _demo.SimulateUsers(2000);

            _demo.SetExposureSkipping(true);
            _demo.SimulateUsers(2000);

            Assert.That(Verdict(), Is.EqualTo(SrmState.Alarm),
                "a third run with the fault back on must be visible, not masked by the first two");
        }

        [Test]
        public void EachSimulateRunAddsToTheExposedPopulation()
        {
            // The property underneath it. "Simulate 5000 users" that adds nobody the second time is a
            // button whose label is false, and every count downstream inherits that.
            _demo.OnButton("btnScenarioNormal");

            _demo.SimulateUsers(500);
            long afterFirst = ExposedUsers();

            _demo.SimulateUsers(500);
            long afterSecond = ExposedUsers();

            Assert.That(afterSecond, Is.GreaterThan(afterFirst),
                "the second run exposed " + (afterSecond - afterFirst) + " further people");
        }

        [Test]
        public void TheConversionRateStaysARateAcrossRepeatedRuns()
        {
            // Four presses took it to 20.6, 41.1, 61.7, 82.2 percent on screen.
            _demo.OnButton("btnScenarioNormal");

            for (int run = 0; run < 6; run++) _demo.SimulateUsers(1000);

            foreach (var experiment in _demo.BuildReport().Experiments)
            {
                foreach (var variant in experiment.Variants)
                {
                    Assert.That(variant.ConversionRate, Is.InRange(0.0, 1.0),
                        experiment.ExperimentId + "/" + variant.VariantId + " reads " +
                        variant.ConversionRate.ToString("P1"));
                }
            }
        }

        private SrmState Verdict()
        {
            foreach (var experiment in _demo.BuildReport().Experiments)
            {
                if (experiment.ExperimentId == "exp_pricing_cta") return experiment.Srm.State;
            }

            Assert.Fail("no pricing experiment in the report");
            return SrmState.Unknown;
        }

        // --- server ---------------------------------------------------------------------------------

        [Test]
        public void ScenarioMalformedThenNormalRecovers()
        {
            _demo.OnButton("btnScenarioNormal");
            string good = _demo.Snapshot.ConfigVersion;

            _demo.OnButton("btnScenarioMalformed");
            Assert.That(_demo.Snapshot.ConfigVersion, Is.EqualTo(good), "the good config must survive");

            _demo.OnButton("btnScenarioNormal");
            Assert.That(_demo.Snapshot.Source, Is.EqualTo(ConfigSourceKind.Live));
            Assert.That(_demo.Snapshot.ConfigVersion, Is.Not.EqualTo(good), "and a new one must be accepted");
        }

        [Test]
        public void ScenarioBadSchemaThenNormalRecovers()
        {
            _demo.OnButton("btnScenarioBadSchema");
            _demo.OnButton("btnScenarioNormal");

            Assert.That(_demo.Snapshot.Source, Is.EqualTo(ConfigSourceKind.Live));
            Assert.That(_demo.Snapshot.Config.Experiments.Count, Is.EqualTo(2));
        }

        [Test]
        public void OfflineThenNormalRecovers()
        {
            _demo.OnButton("btnScenarioOffline");
            _demo.OnButton("btnScenarioNormal");

            Assert.That(_demo.Snapshot.Source, Is.EqualTo(ConfigSourceKind.Live));
        }

        [Test]
        public void TheKillSwitchStopsEveryExperimentAndNormalStartsThemAgain()
        {
            _demo.OnButton("btnScenarioKill");

            foreach (var experiment in _demo.Snapshot.Config.Experiments)
            {
                Assert.That(experiment.IsRunning, Is.False, experiment.Id);
            }

            _demo.OnButton("btnScenarioNormal");

            foreach (var experiment in _demo.Snapshot.Config.Experiments)
            {
                Assert.That(experiment.IsRunning, Is.True, experiment.Id);
            }
        }

        [Test]
        public void PauseThenNormalRestoresOnlyThePausedExperiment()
        {
            _demo.OnButton("btnScenarioPause");

            Assert.That(_demo.Snapshot.Config.FindExperiment("exp_offer_layout").IsRunning, Is.False);
            Assert.That(_demo.Snapshot.Config.FindExperiment("exp_pricing_cta").IsRunning, Is.True,
                "the other layer must be untouched");

            _demo.OnButton("btnScenarioNormal");
            Assert.That(_demo.Snapshot.Config.FindExperiment("exp_offer_layout").IsRunning, Is.True);
        }

        // --- the deliberately asymmetric pair ------------------------------------------------------------

        [Test]
        public void RestoringTheWeightsDoesNotRestoreTheArmsOfUsersAlreadyExposed()
        {
            // Not a missing pair. The sticky policy exists precisely so that an exposed user does not move
            // when weights change, and this asserts the demo honours it rather than quietly re-bucketing.
            _demo.OnButton("btnScenarioNormal");
            _demo.MarkShopSeen();

            var before = _demo.RenderShop();

            _demo.OnButton("btnScenarioWeights");
            _demo.OnButton("btnScenarioNormal");

            Assert.That(_demo.RenderShop(), Is.EqualTo(before),
                "the exposed player must keep the arm they saw across a weight ramp and back");
        }

        // --- QA override -----------------------------------------------------------------------------------

        [Test]
        public void ForcingAVariantThenClearingItRestoresBucketing()
        {
            _demo.OnButton("btnScenarioNormal");
            var natural = _demo.RenderShop();

            _demo.OnButton("btnForceVariant");
            Assert.That(_demo.IsForced, Is.True);
            Assert.That(_demo.ForcedDescription, Does.Contain("excluded from all metrics"));

            _demo.OnButton("btnClearForce");

            Assert.That(_demo.IsForced, Is.False);
            Assert.That(_demo.ForcedDescription, Is.Null);
            Assert.That(_demo.RenderShop(), Is.EqualTo(natural));
        }

        [Test]
        public void CyclingTheOverridePastTheLastArmClearsIt()
        {
            // The force button cycles rather than latching, so pressing it repeatedly always returns to the
            // unforced state rather than getting stuck on the last variant.
            _demo.OnButton("btnScenarioNormal");

            bool everCleared = false;
            for (int i = 0; i < 6; i++)
            {
                _demo.OnButton("btnForceVariant");
                if (!_demo.IsForced) everCleared = true;
            }

            Assert.That(everCleared, Is.True, "cycling must pass back through the unforced state");
        }

        [Test]
        public void ForcingOneExperimentExcludesItsTrafficWithoutSilencingTheOtherLayer()
        {
            // The force button targets the pricing experiment. The offer layer is untouched, so its
            // exposure is ordinary traffic and must still be counted - forcing one experiment is not a
            // reason to stop measuring a different one.
            _demo.OnButton("btnScenarioNormal");
            _demo.OnButton("btnForceVariant");
            _demo.MarkShopSeen();

            var report = _demo.BuildReport();

            Assert.That(ExposedUsersIn(report, "exp_pricing_cta"), Is.Zero,
                "a forced session is a deliberate ratio violation and must not reach the metrics:\n" +
                report.Describe());

            Assert.That(ExposedUsersIn(report, "exp_offer_layout"), Is.EqualTo(1),
                "the layer that was not forced must keep measuring:\n" + report.Describe());
        }

        // --- the two breakages ---------------------------------------------------------------------------------

        [Test]
        public void SkipExposureBreaksTheRatioAndFixingItRecovers()
        {
            _demo.OnButton("btnScenarioNormal");
            _demo.OnButton("btnSkipExposure");
            Assert.That(_demo.SkipExposureBreakage, Is.True);

            _demo.SimulateUsers(2000);

            var broken = _demo.BuildReport();
            Assert.That(SrmOf(broken, "exp_pricing_cta"), Is.EqualTo(SrmState.Alarm),
                "measuring the ratio over exposures is what catches this:\n" + broken.Describe());

            _demo.OnButton("btnSkipExposure");
            Assert.That(_demo.SkipExposureBreakage, Is.False);

            _demo.OnButton("btnClearState");
            _demo.SimulateUsers(2000);

            Assert.That(SrmOf(_demo.BuildReport(), "exp_pricing_cta"), Is.EqualTo(SrmState.Healthy),
                "and the light must be able to go back to green");
        }

        [Test]
        public void SkipExposureLeavesTheAssignmentSplitIntact()
        {
            // The trap the exposure-based ratio check exists for. Assignments stay even while half the data
            // is destroyed, so an assignment-based light would stay green throughout.
            _demo.OnButton("btnScenarioNormal");
            _demo.OnButton("btnSkipExposure");
            _demo.SimulateUsers(2000);

            var pricing = ExperimentOf(_demo.BuildReport(), "exp_pricing_cta");

            long control = 0, urgency = 0;
            foreach (var variant in pricing.Variants)
            {
                if (variant.VariantId == "control") control = variant.UsersAssigned;
                if (variant.VariantId == "urgency") urgency = variant.UsersAssigned;
            }

            Assert.That(control, Is.EqualTo(urgency).Within(150),
                "the assignment split must be untouched, which is exactly why it cannot be the signal");
        }

        [Test]
        public void BucketingSkewBreaksTheRatioAndFixingItRecovers()
        {
            _demo.OnButton("btnScenarioNormal");
            _demo.OnButton("btnInjectSkew");
            Assert.That(_demo.BucketingSkewBreakage, Is.True);

            _demo.SimulateUsers(2000);
            Assert.That(SrmOf(_demo.BuildReport(), "exp_pricing_cta"), Is.EqualTo(SrmState.Alarm));

            _demo.OnButton("btnInjectSkew");
            _demo.OnButton("btnClearState");
            _demo.SimulateUsers(2000);

            Assert.That(SrmOf(_demo.BuildReport(), "exp_pricing_cta"), Is.EqualTo(SrmState.Healthy));
        }

        [Test]
        public void TheTwoBreakagesLookDifferentInTheFunnelRate()
        {
            // Both skew the exposed split. The funnel rate is what says which fault it is, and a reader who
            // cannot tell them apart cannot act on the light.
            _demo.OnButton("btnScenarioNormal");
            _demo.OnButton("btnSkipExposure");
            _demo.SimulateUsers(2000);

            double collapsed = FunnelOf(_demo.BuildReport(), "exp_pricing_cta", "urgency");

            _demo.OnButton("btnSkipExposure");
            _demo.OnButton("btnClearState");
            _demo.OnButton("btnInjectSkew");
            _demo.SimulateUsers(2000);

            double skewed = FunnelOf(_demo.BuildReport(), "exp_pricing_cta", "urgency");

            Assert.That(collapsed, Is.Zero, "suppressed logging collapses that arm's funnel");
            Assert.That(skewed, Is.EqualTo(1.0).Within(0.05), "skewed bucketing leaves every funnel healthy");
        }

        // --- reset ------------------------------------------------------------------------------------------------

        [Test]
        public void ResetUndoesEveryStateAtOnce()
        {
            _demo.OnButton("btnScenarioKill");
            _demo.OnButton("btnForceVariant");
            _demo.OnButton("btnInjectSkew");
            _demo.OnButton("btnSkipExposure");
            _demo.SimulateUsers(200);

            _demo.OnButton("btnClearState");

            Assert.That(_demo.IsForced, Is.False, "override");
            Assert.That(_demo.BucketingSkewBreakage, Is.False, "skew");
            Assert.That(_demo.SkipExposureBreakage, Is.False, "exposure skipping");
            Assert.That(_demo.Server.Scenario, Is.EqualTo(ServerScenario.Normal), "scenario");
            Assert.That(ExposedUsers(), Is.Zero, "metrics");

            foreach (var experiment in _demo.Snapshot.Config.Experiments)
            {
                Assert.That(experiment.IsRunning, Is.True, experiment.Id);
            }
        }

        [Test]
        public void ResetIsIdempotent()
        {
            _demo.OnButton("btnClearState");
            Assert.That(() => _demo.OnButton("btnClearState"), Throws.Nothing);
            Assert.That(ExposedUsers(), Is.Zero);
        }

        // --- the demo actually works ----------------------------------------------------------------------------------

        [Test]
        public void TheShopRendersFromBothLayersAtOnce()
        {
            _demo.OnButton("btnScenarioNormal");

            var spec = _demo.RenderShop();

            // Whatever the player bucketed into, the spec must be one the screen can render, and it must
            // carry a call to action from the pricing layer.
            Assert.That(spec.CtaText, Is.Not.Null.And.Not.Empty);
            Assert.That(System.Enum.IsDefined(typeof(HotUpdateABTest.Core.Presentation.OfferLayout), spec.Layout), Is.True);
        }

        [Test]
        public void SimulatingUsersProducesAHealthyPicture()
        {
            _demo.OnButton("btnScenarioNormal");
            _demo.SimulateUsers(3000);

            var report = _demo.BuildReport();

            Assert.That(ExposedUsers(), Is.GreaterThan(4000), "both layers expose each user");
            Assert.That(SrmOf(report, "exp_offer_layout"), Is.EqualTo(SrmState.Healthy), report.Describe());
            Assert.That(SrmOf(report, "exp_pricing_cta"), Is.EqualTo(SrmState.Healthy), report.Describe());

            TestContext.WriteLine("\n" + report.Describe());
        }

        [Test]
        public void EveryConsoleButtonIsHandled()
        {
            // A button the controller does not answer logs "unhandled" rather than doing nothing visible,
            // and this asserts none of them do.
            foreach (var spec in DemoUiFactory.Buttons)
            {
                _demo.OnButton(spec.Name);
            }

            Assert.That(_log.CountContaining("unhandled button"), Is.Zero, _log.All);
        }

        private static ExperimentMetrics ExperimentOf(MetricsReport report, string experimentId)
        {
            foreach (var experiment in report.Experiments)
            {
                if (experiment.ExperimentId == experimentId) return experiment;
            }

            Assert.Fail("no experiment '" + experimentId + "' in the report:\n" + report.Describe());
            return null;
        }

        private static long ExposedUsersIn(MetricsReport report, string experimentId)
        {
            long total = 0;
            foreach (var variant in ExperimentOf(report, experimentId).Variants) total += variant.UsersExposed;
            return total;
        }

        private static SrmState SrmOf(MetricsReport report, string experimentId) =>
            ExperimentOf(report, experimentId).Srm.State;

        private static double FunnelOf(MetricsReport report, string experimentId, string variantId)
        {
            foreach (var variant in ExperimentOf(report, experimentId).Variants)
            {
                if (variant.VariantId == variantId) return variant.ExposureRate;
            }

            Assert.Fail("no variant '" + variantId + "' in '" + experimentId + "'");
            return 0;
        }
    }
}
