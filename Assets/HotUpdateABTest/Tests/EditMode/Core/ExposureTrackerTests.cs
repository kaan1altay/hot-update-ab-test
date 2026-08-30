using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Telemetry;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers the distinction the whole telemetry layer is built around: assignment is free and silent,
    /// exposure is the event.
    /// </summary>
    [TestFixture]
    public sealed class ExposureTrackerTests
    {
        private TelemetryHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new TelemetryHarness();
        }

        [Test]
        public void ResolvingManyTimesLogsNothing()
        {
            var user = new UserContext("user-1", platform: "editor");

            for (int i = 0; i < 50; i++) _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");

            Assert.That(_h.Events.CountOf(AnalyticsEventKind.Exposure), Is.Zero);
            Assert.That(_h.Events.CountOf(AnalyticsEventKind.Assignment), Is.Zero);
            Assert.That(_h.Pins.Count, Is.Zero, "resolving must not pin either");
        }

        [Test]
        public void SeeingTheSurfaceIsWhatLogsAnExposure()
        {
            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");

            Assert.That(_h.Exposures.MarkExposed(user, assignment, new SessionId("s1")), Is.True);
            Assert.That(_h.Events.CountOf(AnalyticsEventKind.Exposure), Is.EqualTo(1));
            Assert.That(_h.Pins.Count, Is.EqualTo(1), "the first exposure pins a sticky assignment");
        }

        // --- deduplication ---------------------------------------------------------------------------

        [Test]
        public void ReEnteringTheSurfaceInTheSameSessionLogsNothingFurther()
        {
            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            var session = new SessionId("s1");

            Assert.That(_h.Exposures.MarkExposed(user, assignment, session), Is.True);

            for (int i = 0; i < 20; i++)
            {
                Assert.That(_h.Exposures.MarkExposed(user, assignment, session), Is.False);
            }

            Assert.That(_h.Events.CountOf(AnalyticsEventKind.Exposure), Is.EqualTo(1));
            Assert.That(_h.Exposures.DeduplicatedCount, Is.EqualTo(20));
        }

        [Test]
        public void ANewSessionLogsAgain()
        {
            // Deduplicating forever would turn the exposure count into a first-seen count, and a user who
            // opened the shop every day for a week would be indistinguishable from one who opened it once.
            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");

            Assert.That(_h.Exposures.MarkExposed(user, assignment, new SessionId("s1")), Is.True);
            Assert.That(_h.Exposures.MarkExposed(user, assignment, new SessionId("s2")), Is.True);
            Assert.That(_h.Exposures.MarkExposed(user, assignment, new SessionId("s3")), Is.True);

            Assert.That(_h.Events.CountOf(AnalyticsEventKind.Exposure), Is.EqualTo(3));
        }

        [Test]
        public void ASecondSessionDoesNotCountAsASecondExposedUser()
        {
            // The ratio check counts people, not visits. If it counted visits, a handful of heavy users
            // could move the split on their own.
            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");

            for (int i = 0; i < 5; i++)
            {
                _h.Exposures.MarkExposed(user, assignment, new SessionId("s" + i));
            }

            var arm = _h.Arm("exp_offer_layout", assignment.VariantId, MetricsPopulation.Everything);

            Assert.That(arm.Exposures, Is.EqualTo(5));
            Assert.That(arm.UsersExposed, Is.EqualTo(1));
        }

        [Test]
        public void SimulatedUsersEachGetTheirOwnSession()
        {
            // Without distinct sessions "simulate N users" collapses into one visit and dedup eats it.
            _h.SimulateUsers(500);

            Assert.That(_h.Events.CountOf(AnalyticsEventKind.Exposure), Is.EqualTo(500));
            Assert.That(_h.Exposures.DeduplicatedCount, Is.Zero);
        }

        [Test]
        public void TheSessionTrackerRollsOverAfterIdleTime()
        {
            var sessions = _h.Sessions;
            var first = sessions.Current;

            _h.Clock.AdvanceSeconds(60);
            Assert.That(sessions.Touch(), Is.EqualTo(first), "still the same visit");

            _h.Clock.AdvanceSeconds(SessionTracker.DefaultIdleTimeout.TotalSeconds + 1);
            var second = sessions.Touch();

            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(sessions.SessionCount, Is.EqualTo(2));
        }

        [Test]
        public void AnExposureWithoutASessionIsRejectedRatherThanSilentlyUndeduplicated()
        {
            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");

            Assert.That(() => _h.Exposures.MarkExposed(user, assignment, SessionId.None),
                Throws.ArgumentException);
        }

        // --- non-assignments -------------------------------------------------------------------------

        [Test]
        public void AUserInNoExperimentProducesNoExposure()
        {
            _h.Serve(ConfigJson.New("2")
                .Layer("offer_layout")
                .Experiment("exp_offer_layout", "offer_layout", status: "stopped")
                .Build());

            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");

            Assert.That(assignment.IsAssigned, Is.False);
            Assert.That(_h.Exposures.MarkExposed(user, assignment, new SessionId("s1")), Is.False);
            Assert.That(_h.Events.CountOf(AnalyticsEventKind.Exposure), Is.Zero);
        }

        // --- contamination ---------------------------------------------------------------------------

        [Test]
        public void AUserExposedToTwoArmsIsFlaggedRatherThanSwallowed()
        {
            // Variant is in the dedup key on purpose: a second arm is not suppressed, it is surfaced.
            var user = new UserContext("user-1", platform: "editor");
            var session = new SessionId("s1");

            var control = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            _h.Exposures.MarkExposed(user, control, session);

            _h.Resolver.Overrides.Force("exp_offer_layout", OtherArm(control.VariantId));
            var other = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            _h.Exposures.MarkExposed(user, other, session);

            Assert.That(_h.Exposures.ContaminatingCount, Is.EqualTo(1));
            Assert.That(_h.Ledger.IsContaminated("user-1", "exp_offer_layout"), Is.True);
            Assert.That(_h.Ledger.DistinctArmsSeen("user-1", "exp_offer_layout"), Is.EqualTo(2));
            Assert.That(_h.Events.CountOf(AnalyticsEventKind.Exposure), Is.EqualTo(2),
                "both exposures must be on record, or the evidence is gone");
        }

        [Test]
        public void ContaminationDoesNotMoveTheAttributionTarget()
        {
            // The first arm a user saw is the one their outcomes belong to. A later exposure makes the
            // record suspect but does not get to claim the conversions.
            var user = new UserContext("user-1", platform: "editor");
            var session = new SessionId("s1");

            var first = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            _h.Exposures.MarkExposed(user, first, session);

            _h.Resolver.Overrides.Force("exp_offer_layout", OtherArm(first.VariantId));
            _h.Exposures.MarkExposed(user, _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout"), session);

            _h.Ledger.TryGetRecord("user-1", "exp_offer_layout", out var record);

            Assert.That(record.VariantId, Is.EqualTo(first.VariantId));
            Assert.That(record.IsContaminated, Is.True);
        }

        // --- forced and synthetic ----------------------------------------------------------------------

        [Test]
        public void AForcedExposureIsFlaggedAndNeverPins()
        {
            _h.Resolver.Overrides.Force("exp_offer_layout", "treatment");

            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            _h.Exposures.MarkExposed(user, assignment, new SessionId("s1"));

            var events = _h.Events.OfKind(AnalyticsEventKind.Exposure);

            Assert.That(events[0].IsForced, Is.True);
            Assert.That(_h.Pins.Count, Is.Zero, "a QA override must vanish when it is cleared");
        }

        [Test]
        public void SyntheticExposuresAreFlagged()
        {
            _h.Visit("user-1");

            Assert.That(_h.Events.OfKind(AnalyticsEventKind.Exposure)[0].IsSynthetic, Is.True);
        }

        // --- the funnel ---------------------------------------------------------------------------------

        [Test]
        public void AssignmentsAreCountedSeparatelyAsTheFunnelDenominator()
        {
            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");

            // Prepared five times, seen once - a screen built repeatedly but only ever viewed on one visit.
            for (int i = 0; i < 5; i++) _h.Exposures.RecordAssignment(user, assignment, new SessionId("s1"));
            _h.Exposures.MarkExposed(user, assignment, new SessionId("s1"));

            var arm = _h.Arm("exp_offer_layout", assignment.VariantId, MetricsPopulation.Everything);

            Assert.That(arm.Assignments, Is.EqualTo(5));
            Assert.That(arm.Exposures, Is.EqualTo(1));
            Assert.That(arm.UsersAssigned, Is.EqualTo(1));
            Assert.That(arm.ExposureRate, Is.EqualTo(1.0).Within(0.001), "one of one assigned user saw it");
        }

        [Test]
        public void ForgettingASessionReleasesDedupStateWithoutLosingAttribution()
        {
            var user = new UserContext("user-1", platform: "editor");
            var assignment = _h.Resolver.Resolve(_h.Snapshot, user, "offer_layout");
            var session = new SessionId("s1");

            _h.Exposures.MarkExposed(user, assignment, session);
            _h.Exposures.ForgetSession(session);

            Assert.That(_h.Ledger.TryGetRecord("user-1", "exp_offer_layout", out _), Is.True,
                "attribution outlives the visit");
            Assert.That(_h.Exposures.MarkExposed(user, assignment, session), Is.True,
                "the dedup key for a forgotten session is gone");
        }

        private static string OtherArm(string variantId) => variantId == "control" ? "treatment" : "control";
    }
}
