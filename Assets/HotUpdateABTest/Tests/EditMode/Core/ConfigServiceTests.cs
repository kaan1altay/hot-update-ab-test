using System;
using System.Collections.Generic;
using HotUpdateABTest.Core;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>Collects everything written to the log so tests can assert on how much was said.</summary>
    internal sealed class RecordingLog : IAbLog
    {
        public List<string> Lines { get; } = new List<string>();

        public void Log(AbLogLevel level, string message) => Lines.Add(level + ": " + message);

        public int CountContaining(string fragment)
        {
            int count = 0;
            foreach (string line in Lines)
            {
                if (line.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) count++;
            }

            return count;
        }

        public string All => string.Join("\n", Lines.ToArray());
    }

    /// <summary>
    /// Covers the configuration lifecycle: the fallback ladder, recovery after a rejection, how much gets
    /// said about a failure that repeats, and the work skipped when nothing has changed.
    /// </summary>
    [TestFixture]
    public sealed class ConfigServiceTests
    {
        private RecordingLog _log;
        private ManualTestClock _clock;

        [SetUp]
        public void SetUp()
        {
            _log = new RecordingLog();
            _clock = new ManualTestClock();
        }

        private ConfigService Service(
            InMemoryConfigSource source,
            IConfigCache cache = null,
            string shippedDefaults = null,
            IAssignmentStore store = null,
            TimeSpan? pollInterval = null)
        {
            return new ConfigService(source, _clock, _log, new ConfigServiceOptions
            {
                Cache = cache ?? new InMemoryConfigCache(),
                ShippedDefaultsPayload = shippedDefaults,
                AssignmentStore = store,
                PollInterval = pollInterval ?? TimeSpan.FromMinutes(2)
            });
        }

        // --- the ladder ------------------------------------------------------------------------------

        [Test]
        public void AFreshInstallWithNoNetworkFallsAllTheWayToTheShippedDefaults()
        {
            var source = new InMemoryConfigSource();
            source.GoOffline();

            var service = Service(source, shippedDefaults: ConfigJson.Demo("shipped").Build());
            service.Initialize();
            service.Refresh();

            Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.ShippedDefaults));
            Assert.That(service.CurrentSnapshot.ConfigVersion, Is.EqualTo("shipped"));
            Assert.That(service.Current.Experiments, Is.Not.Empty,
                "the shipped defaults must declare experiments, not be an empty document");
        }

        [Test]
        public void AFreshInstallWithNoNetworkAndNoDefaultsStillResolvesRatherThanThrowing()
        {
            var source = new InMemoryConfigSource();
            source.GoOffline();

            var service = Service(source);
            service.Initialize();

            Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.None));
            Assert.That(service.Current.Experiments, Is.Empty);
            Assert.That(() => service.Refresh(), Throws.Nothing);
        }

        [Test]
        public void ACachedPayloadIsPreferredOverTheShippedDefaults()
        {
            var cache = new InMemoryConfigCache(ConfigJson.Demo("cached").Build());
            var source = new InMemoryConfigSource();
            source.GoOffline();

            var service = Service(source, cache, ConfigJson.Demo("shipped").Build());
            service.Initialize();

            Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.LastKnownGood));
            Assert.That(service.CurrentSnapshot.ConfigVersion, Is.EqualTo("cached"));
        }

        [Test]
        public void ALivePayloadIsPreferredOverEverything()
        {
            var cache = new InMemoryConfigCache(ConfigJson.Demo("cached").Build());
            var source = new InMemoryConfigSource(ConfigJson.Demo("live").Build());

            var service = Service(source, cache, ConfigJson.Demo("shipped").Build());
            service.Initialize();
            service.Refresh();

            Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.Live));
            Assert.That(service.CurrentSnapshot.ConfigVersion, Is.EqualTo("live"));
        }

        [Test]
        public void AnAcceptedPayloadIsWrittenToTheCacheForTheNextColdStart()
        {
            var cache = new InMemoryConfigCache();
            var source = new InMemoryConfigSource(ConfigJson.Demo("7").Build());

            Service(source, cache).Refresh();

            Assert.That(cache.Read(), Is.Not.Null);
            Assert.That(ConfigReader.Read(cache.Read()).Config.ConfigVersion, Is.EqualTo("7"));
        }

        [Test]
        public void ACorruptCacheIsDiscardedRatherThanRetriedEveryLaunch()
        {
            var cache = new InMemoryConfigCache("{ this is not json");
            var source = new InMemoryConfigSource();
            source.GoOffline();

            var service = Service(source, cache, ConfigJson.Demo("shipped").Build());
            service.Initialize();

            Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.ShippedDefaults));
            Assert.That(cache.Read(), Is.Null, "the unusable cache should have been cleared");
        }

        [Test]
        public void TheLadderRungAndTheReasonAreObservable()
        {
            var source = new InMemoryConfigSource();
            source.GoOffline("connection refused");

            var service = Service(source, shippedDefaults: ConfigJson.Demo("shipped").Build());
            service.Initialize();
            service.Refresh();

            var snapshot = service.CurrentSnapshot;

            Assert.That(snapshot.IsDegraded, Is.True);
            Assert.That(snapshot.Describe(), Does.Contain("shipped defaults").And.Contain("shipped"));
            Assert.That(service.LastFailureReason, Does.Contain("connection refused"));
            Assert.That(service.ConsecutiveFailures, Is.EqualTo(1));
        }

        // --- rejection must not poison the pipeline ------------------------------------------------------

        [Test]
        public void AGoodPayloadAfterABadOneIsAcceptedNormally()
        {
            var source = new InMemoryConfigSource(ConfigJson.Demo("1").Build());
            var service = Service(source);

            Assert.That(service.Refresh().Outcome, Is.EqualTo(ConfigApplyOutcome.Accepted));

            source.Serve("{ not json at all");
            Assert.That(service.Refresh().Outcome, Is.EqualTo(ConfigApplyOutcome.Rejected));
            Assert.That(service.CurrentSnapshot.ConfigVersion, Is.EqualTo("1"), "the good config must survive");

            source.Serve(ConfigJson.Demo("2").Build());
            var recovery = service.Refresh();

            Assert.That(recovery.Outcome, Is.EqualTo(ConfigApplyOutcome.Accepted),
                "a rejection must leave no latch behind");
            Assert.That(service.CurrentSnapshot.ConfigVersion, Is.EqualTo("2"));
            Assert.That(service.ConsecutiveFailures, Is.Zero);
            Assert.That(service.LastFailureReason, Is.Null);
        }

        [Test]
        public void ABadFirstEverPayloadDoesNotPreventTheNextOneFromBeingAccepted()
        {
            var source = new InMemoryConfigSource("{ not json at all");
            var service = Service(source);
            service.Initialize();

            Assert.That(service.Refresh().Outcome, Is.EqualTo(ConfigApplyOutcome.Rejected));

            source.Serve(ConfigJson.Demo("1").Build());

            Assert.That(service.Refresh().Outcome, Is.EqualTo(ConfigApplyOutcome.Accepted));
            Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.Live));
        }

        [Test]
        public void EveryFailureModeKeepsTheConfigurationAlreadyInForce()
        {
            var cases = new Dictionary<string, string>
            {
                { "malformed", "{ not json" },
                { "empty", "" },
                { "bad schema", "{\"schemaVersion\":99,\"configVersion\":\"x\",\"layers\":[],\"experiments\":[]}" },
                { "semantically invalid", ConfigJson.New()
                    .Layer("l")
                    .Experiment("a", "l", from: 0, to: 6000)
                    .Experiment("b", "l", from: 4000, to: 10000)
                    .Build() },
                { "json array", "[]" }
            };

            foreach (var pair in cases)
            {
                var source = new InMemoryConfigSource(ConfigJson.Demo("good").Build());
                var service = Service(source);
                service.Refresh();

                source.Serve(pair.Value);
                var result = service.Refresh();

                Assert.That(result.Outcome, Is.EqualTo(ConfigApplyOutcome.Rejected), pair.Key);
                Assert.That(service.CurrentSnapshot.ConfigVersion, Is.EqualTo("good"), pair.Key);
                Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.Live), pair.Key);
            }
        }

        [Test]
        public void ASourceThatThrowsIsTreatedAsUnreachableRatherThanTakingTheGameDown()
        {
            var service = new ConfigService(new ThrowingConfigSource(), _clock, _log);
            service.Initialize();

            Assert.That(() => service.Refresh(), Throws.Nothing);
            Assert.That(service.LastFailureReason, Does.Contain("InvalidOperationException"));
        }

        // --- log once, but not too once ------------------------------------------------------------------

        [Test]
        public void AServerFailingEveryPollForFiveMinutesSaysSoOnce()
        {
            var source = new InMemoryConfigSource(ConfigJson.Demo("1").Build());
            var service = Service(source, pollInterval: TimeSpan.FromSeconds(10));
            service.Refresh();

            source.GoOffline("connection refused");

            for (int i = 0; i < 30; i++)
            {
                _clock.AdvanceSeconds(10);
                service.PollIfDue();
            }

            Assert.That(source.FetchCount, Is.GreaterThan(20), "the polls must actually have happened");
            Assert.That(_log.CountContaining("could not be reached"), Is.EqualTo(1), _log.All);
        }

        [Test]
        public void ADifferentFailureReasonIsNotSwallowedByTheFirstOne()
        {
            var source = new InMemoryConfigSource(ConfigJson.Demo("1").Build());
            var service = Service(source);
            service.Refresh();

            source.GoOffline("connection refused");
            service.Refresh();
            service.Refresh();

            source.GoOffline("dns failure");
            service.Refresh();
            service.Refresh();

            Assert.That(_log.CountContaining("connection refused"), Is.EqualTo(1), _log.All);
            Assert.That(_log.CountContaining("dns failure"), Is.EqualTo(1), _log.All);
        }

        [Test]
        public void TwoDifferentBrokenPayloadsAreBothReported()
        {
            var source = new InMemoryConfigSource(ConfigJson.Demo("1").Build());
            var service = Service(source);
            service.Refresh();

            source.Serve("{ not json");
            service.Refresh();
            service.Refresh();

            source.Serve(ConfigJson.New()
                .Layer("l")
                .Experiment("a", "l", from: 0, to: 6000)
                .Experiment("b", "l", from: 4000, to: 10000)
                .Build());
            service.Refresh();
            service.Refresh();

            Assert.That(_log.CountContaining("not valid JSON"), Is.EqualTo(1), _log.All);
            Assert.That(_log.CountContaining("overlapping traffic"), Is.EqualTo(1), _log.All);
        }

        [Test]
        public void ARecoveryClosesTheIncidentSoARecurrenceIsReportedAgain()
        {
            // A failure that comes back after the server recovered is a new incident, not a continuation
            // of the old one, and an operator watching a flapping server needs to see each episode.
            var source = new InMemoryConfigSource(ConfigJson.Demo("1").Build());
            var service = Service(source);
            service.Refresh();

            source.GoOffline("connection refused");
            service.Refresh();
            service.Refresh();

            source.Serve(ConfigJson.Demo("2").Build());
            service.Refresh();

            source.GoOffline("connection refused");
            service.Refresh();

            Assert.That(_log.CountContaining("could not be reached"), Is.EqualTo(2), _log.All);
        }

        // --- skip work when nothing changed ---------------------------------------------------------------

        [Test]
        public void AnIdenticalPayloadIsNotReparsedAndRaisesNoChange()
        {
            var source = new InMemoryConfigSource(ConfigJson.Demo("7").Build());
            var service = Service(source);

            int configChanged = 0;
            service.ConfigChanged += _ => configChanged++;

            service.Refresh();
            Assert.That(configChanged, Is.EqualTo(1));

            for (int i = 0; i < 5; i++)
            {
                Assert.That(service.Refresh().Outcome, Is.EqualTo(ConfigApplyOutcome.Unchanged));
            }

            Assert.That(configChanged, Is.EqualTo(1), "an unchanged payload must not trigger re-resolution");
        }

        [Test]
        public void ASourceReportingNotModifiedDoesNoWorkAtAll()
        {
            var source = new InMemoryConfigSource(ConfigJson.Demo("7").Build());
            var service = Service(source);
            service.Refresh();

            int configChanged = 0;
            service.ConfigChanged += _ => configChanged++;
            source.ServeNotModified();

            Assert.That(service.Refresh().Outcome, Is.EqualTo(ConfigApplyOutcome.Unchanged));
            Assert.That(configChanged, Is.Zero);
        }

        [Test]
        public void ComingBackOnlineWithTheSamePayloadUpgradesTheRungWithoutReResolving()
        {
            // The status display must stop saying "last known good" once the server answers again, but not
            // one user's assignment has moved, so nothing should re-resolve.
            string payload = ConfigJson.Demo("7").Build();
            var cache = new InMemoryConfigCache(payload);
            var source = new InMemoryConfigSource();
            source.GoOffline();

            var service = Service(source, cache);
            service.Initialize();
            Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.LastKnownGood));

            int configChanged = 0, statusChanged = 0;
            service.ConfigChanged += _ => configChanged++;
            service.StatusChanged += _ => statusChanged++;

            source.Serve(payload);
            var result = service.Refresh();

            Assert.That(result.Outcome, Is.EqualTo(ConfigApplyOutcome.Unchanged));
            Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.Live));
            Assert.That(statusChanged, Is.EqualTo(1), "the rung change must be observable");
            Assert.That(configChanged, Is.Zero, "no user moved, so nothing should re-resolve");
        }

        [Test]
        public void TheSameVersionServedWithDifferentContentIsIgnoredAndReportedOnce()
        {
            var source = new InMemoryConfigSource(ConfigJson.Demo("7").Build());
            var service = Service(source);
            service.Refresh();

            // Same version label, one experiment now stopped. Honouring it would leave the client running
            // something the analysis pipeline attributes to a different version 7.
            source.Serve(ConfigJson.New("7")
                .Layer("offer_layout")
                .Layer("pricing_cta")
                .Experiment("exp_offer_layout", "offer_layout", status: "stopped")
                .Experiment("exp_pricing_cta", "pricing_cta")
                .Build());

            Assert.That(service.Refresh().Outcome, Is.EqualTo(ConfigApplyOutcome.ContentDriftIgnored));
            Assert.That(service.Current.FindExperiment("exp_offer_layout").IsRunning, Is.True);

            service.Refresh();
            Assert.That(_log.CountContaining("bump the version"), Is.EqualTo(1), _log.All);
        }

        [Test]
        public void PollingRespectsTheInterval()
        {
            var source = new InMemoryConfigSource(ConfigJson.Demo("1").Build());
            var service = Service(source, pollInterval: TimeSpan.FromSeconds(30));

            Assert.That(service.PollIfDue(), Is.Not.Null, "the first poll is always due");
            Assert.That(service.PollIfDue(), Is.Null);

            _clock.AdvanceSeconds(29);
            Assert.That(service.PollIfDue(), Is.Null);

            _clock.AdvanceSeconds(2);
            Assert.That(service.PollIfDue(), Is.Not.Null);
            Assert.That(source.FetchCount, Is.EqualTo(2));
        }

        // --- kill switch ----------------------------------------------------------------------------------

        [Test]
        public void StoppingAnExperimentDiscardsItsCachedAssignments()
        {
            var store = new InMemoryAssignmentStore();
            store.Set("user-1", new AssignmentPin("exp_offer_layout", "treatment", _clock.UtcNow, "1"));
            store.Set("user-2", new AssignmentPin("exp_offer_layout", "control", _clock.UtcNow, "1"));
            store.Set("user-1", new AssignmentPin("exp_pricing_cta", "treatment", _clock.UtcNow, "1"));

            var source = new InMemoryConfigSource(ConfigJson.Demo("1").Build());
            var service = Service(source, store: store);
            service.Refresh();
            Assert.That(store.Count, Is.EqualTo(3), "a running experiment keeps its pins");

            source.Serve(ConfigJson.New("2")
                .Layer("offer_layout")
                .Layer("pricing_cta")
                .Experiment("exp_offer_layout", "offer_layout", status: "stopped")
                .Experiment("exp_pricing_cta", "pricing_cta")
                .Build());
            service.Refresh();

            Assert.That(store.TryGet("user-1", "exp_offer_layout", out _), Is.False);
            Assert.That(store.TryGet("user-2", "exp_offer_layout", out _), Is.False);
            Assert.That(store.TryGet("user-1", "exp_pricing_cta", out _), Is.True,
                "the other layer's experiment is untouched");
            Assert.That(_log.CountContaining("discarded 2 cached assignment"), Is.EqualTo(1), _log.All);
        }

        // --- snapshots ------------------------------------------------------------------------------------

        [Test]
        public void ASnapshotHeldAcrossASwapKeepsShowingTheConfigItWasTakenFrom()
        {
            // The atomic-swap contract. A screen that resolved against version 1 can keep rendering
            // version 1 until it re-reads, rather than changing under the player mid-frame.
            var source = new InMemoryConfigSource(ConfigJson.Demo("1").Build());
            var service = Service(source);
            service.Refresh();

            var held = service.CurrentSnapshot;

            source.Serve(ConfigJson.Demo("2").Build());
            service.Refresh();

            Assert.That(held.ConfigVersion, Is.EqualTo("1"));
            Assert.That(service.CurrentSnapshot.ConfigVersion, Is.EqualTo("2"));
            Assert.That(held.Config, Is.Not.SameAs(service.Current));
        }

        [Test]
        public void WarningsOnAnAcceptedPayloadAreReportedOnceRatherThanEveryPoll()
        {
            var source = new InMemoryConfigSource(ConfigJson.New("1")
                .Layer("l")
                .Experiment("exp_x", "l", from: 0, to: 0)
                .Build());

            var service = Service(source);
            for (int i = 0; i < 5; i++) service.Refresh();

            Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.Live));
            Assert.That(_log.CountContaining("claims no traffic"), Is.EqualTo(1), _log.All);
        }

        private sealed class ThrowingConfigSource : IConfigSource
        {
            public string Description => "throwing";

            public ConfigFetchResult Fetch() => throw new InvalidOperationException("transport is broken");
        }
    }

    /// <summary>A clock the tests advance by hand.</summary>
    /// <remarks>
    /// Duplicated from the Unity-side <c>ManualClock</c> rather than shared, because that type lives in the
    /// engine-facing assembly and these tests are compiled without it in CI.
    /// </remarks>
    internal sealed class ManualTestClock : IClock
    {
        public DateTime UtcNow { get; private set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public void AdvanceSeconds(double seconds) => UtcNow += TimeSpan.FromSeconds(seconds);
    }
}
