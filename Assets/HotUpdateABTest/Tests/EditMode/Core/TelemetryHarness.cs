using System;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Telemetry;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Wires the whole telemetry pipeline together the way the demo will, so the tests exercise the
    /// composition rather than each part in isolation.
    /// </summary>
    /// <remarks>
    /// Several of the properties worth asserting - that attribution survives a config change, that the
    /// ratio check reacts to suppressed exposure logging - only exist once resolution, exposure, conversion
    /// and aggregation are joined up. Testing them against a hand-built pile of events would prove
    /// something about the pile.
    /// </remarks>
    internal sealed class TelemetryHarness
    {
        public ManualTestClock Clock { get; }

        public RecordingLog Log { get; }

        public InMemoryConfigSource Source { get; }

        public ConfigService Config { get; }

        public InMemoryAssignmentStore Pins { get; }

        public ExperimentResolver Resolver { get; }

        public ExposureLedger Ledger { get; }

        public InMemoryAnalyticsSink Events { get; }

        public MetricsAggregator Metrics { get; }

        public ExposureTracker Exposures { get; }

        public ConversionTracker Conversions { get; }

        public SessionTracker Sessions { get; }

        /// <summary>Set to make exposures for this arm silently not be logged.</summary>
        /// <remarks>
        /// Stands in for the demo's "make one variant skip exposure logging" button, which exists to prove
        /// the ratio light can actually be made to go red. Modelling the breakage here rather than in the
        /// UI means the guardrail is verified in the suite instead of only on camera.
        /// </remarks>
        public string SuppressExposureForVariant { get; set; }

        public TelemetryHarness(string payload = null)
        {
            Clock = new ManualTestClock();
            Log = new RecordingLog();
            Source = new InMemoryConfigSource(payload ?? ConfigJson.Demo("1").Build());
            Pins = new InMemoryAssignmentStore();

            Config = new ConfigService(Source, Clock, Log, new ConfigServiceOptions
            {
                Cache = new InMemoryConfigCache(),
                AssignmentStore = Pins
            });

            Resolver = new ExperimentResolver(Pins);
            Ledger = new ExposureLedger();
            Events = new InMemoryAnalyticsSink();
            Metrics = new MetricsAggregator();

            var sink = new CompositeAnalyticsSink(Events, Metrics);
            Exposures = new ExposureTracker(Ledger, sink, Clock, Resolver);
            Conversions = new ConversionTracker(Ledger, sink, Clock);
            Sessions = new SessionTracker(Clock);

            Config.Refresh();
        }

        public ConfigSnapshot Snapshot => Config.CurrentSnapshot;

        public void Serve(string payload)
        {
            Source.Serve(payload);
            Config.Refresh();
        }

        public VariantAssignment Resolve(string userId, string layerId = "offer_layout") =>
            Resolver.Resolve(Snapshot, new UserContext(userId, platform: "editor"), layerId);

        /// <summary>
        /// One simulated visit: resolve, record the assignment, view the surface, and optionally convert.
        /// </summary>
        public VariantAssignment Visit(
            string userId,
            string layerId = "offer_layout",
            bool convert = false,
            SessionId? session = null,
            bool synthetic = true,
            int views = 1)
        {
            var user = new UserContext(userId, platform: "editor");
            var visitSession = session ?? SessionId.ForSimulatedUser(userId, 0);
            var assignment = Resolver.Resolve(Snapshot, user, layerId);

            Exposures.RecordAssignment(user, assignment, visitSession, synthetic);

            for (int i = 0; i < views; i++)
            {
                bool suppressed = assignment.IsAssigned &&
                                  string.Equals(assignment.VariantId, SuppressExposureForVariant,
                                      StringComparison.Ordinal);

                if (!suppressed) Exposures.MarkExposed(user, assignment, visitSession, synthetic);
            }

            if (convert) Conversions.Convert(user, visitSession, "purchase", synthetic);

            return assignment;
        }

        /// <summary>Runs a whole population through one visit each.</summary>
        public void SimulateUsers(int count, string layerId = "offer_layout", double conversionRate = 0)
        {
            for (int i = 0; i < count; i++)
            {
                string userId = "user-" + i;

                // Deterministic rather than random: the same run produces the same numbers, so a
                // distribution assertion cannot flake.
                bool convert = conversionRate > 0 && (i % 100) < (int)Math.Round(conversionRate * 100);
                Visit(userId, layerId, convert);
            }
        }

        public MetricsReport Report(MetricsPopulation population = null) =>
            Metrics.Build(Snapshot.Config, population ?? MetricsPopulation.Analysis, Ledger);

        /// <summary>Finds one arm's numbers, failing the test when the report has no such row.</summary>
        public VariantMetrics Arm(string experimentId, string variantId, MetricsPopulation population = null)
        {
            var report = Report(population);
            foreach (var experiment in report.Experiments)
            {
                if (experiment.ExperimentId != experimentId) continue;
                foreach (var variant in experiment.Variants)
                {
                    if (variant.VariantId == variantId) return variant;
                }
            }

            Assert.Fail("no row for " + experimentId + "/" + variantId + " in:\n" + report.Describe());
            return null;
        }

        public ExperimentMetrics Experiment(string experimentId, MetricsPopulation population = null)
        {
            var report = Report(population);
            foreach (var experiment in report.Experiments)
            {
                if (experiment.ExperimentId == experimentId) return experiment;
            }

            Assert.Fail("no row for " + experimentId + " in:\n" + report.Describe());
            return null;
        }
    }
}
