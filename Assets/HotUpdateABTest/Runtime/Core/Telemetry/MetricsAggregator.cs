using System;
using System.Collections.Generic;
using System.Text;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Telemetry
{
    /// <summary>One arm's numbers, computed over a stated population.</summary>
    public sealed class VariantMetrics
    {
        /// <summary>The arm.</summary>
        public string VariantId { get; internal set; }

        /// <summary>The weight the config gives it, or -1 when it is no longer in the config.</summary>
        public long ConfiguredWeight { get; internal set; }

        /// <summary>Times a user was resolved into this arm. The funnel denominator.</summary>
        public long Assignments { get; internal set; }

        /// <summary>Distinct users ever resolved into this arm.</summary>
        public long UsersAssigned { get; internal set; }

        /// <summary>Times a user actually saw this arm, deduplicated per session.</summary>
        public long Exposures { get; internal set; }

        /// <summary>Distinct users ever exposed to this arm. The unit the ratio check uses.</summary>
        public long UsersExposed { get; internal set; }

        /// <summary>Goals credited to this arm.</summary>
        public long Conversions { get; internal set; }

        /// <summary>Conversions per exposed user, or 0 when nobody has been exposed.</summary>
        /// <remarks>
        /// The denominator is exposed users, not assigned ones. A user who was never shown the treatment
        /// tells you nothing about whether the treatment works, and including them would drag every arm's
        /// rate toward zero in proportion to how often the screen went unopened.
        /// </remarks>
        public double ConversionRate => UsersExposed == 0 ? 0 : Conversions / (double)UsersExposed;

        /// <summary>
        /// Share of assigned users who went on to be exposed, or 0 when nobody was assigned.
        /// </summary>
        /// <remarks>
        /// The second health signal, and the one that separates the two ways an experiment can go wrong.
        /// A collapsed rate in one arm alongside a skewed exposure split means that arm is failing to
        /// render; a skewed split with healthy rates everywhere means the bucketing itself is off.
        /// </remarks>
        public double ExposureRate => UsersAssigned == 0 ? 0 : UsersExposed / (double)UsersAssigned;

        /// <summary>True when the config no longer declares this arm but events for it were recorded.</summary>
        public bool IsOrphaned => ConfiguredWeight < 0;
    }

    /// <summary>One experiment's numbers, plus its ratio verdict.</summary>
    public sealed class ExperimentMetrics
    {
        /// <summary>The experiment.</summary>
        public string ExperimentId { get; internal set; }

        /// <summary>The layer it belongs to, or null when it is no longer in the config.</summary>
        public string LayerId { get; internal set; }

        /// <summary>Its status in the current config, or null when it is gone.</summary>
        public string Status { get; internal set; }

        /// <summary>Its arms, in the order the config declares them.</summary>
        public IReadOnlyList<VariantMetrics> Variants { get; internal set; }

        /// <summary>The sample-ratio verdict, computed over exposed users.</summary>
        public SrmResult Srm { get; internal set; }

        /// <summary>Distinct users exposed across every arm.</summary>
        public long UsersExposed
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Variants.Count; i++) total += Variants[i].UsersExposed;
                return total;
            }
        }
    }

    /// <summary>Everything the metrics panel draws, computed over one population.</summary>
    public sealed class MetricsReport
    {
        /// <summary>Which events these numbers were computed over.</summary>
        public MetricsPopulation Population { get; internal set; }

        /// <summary>The config version the shape of this report came from.</summary>
        public string ConfigVersion { get; internal set; }

        /// <summary>One entry per experiment in the config, plus any orphans with recorded events.</summary>
        public IReadOnlyList<ExperimentMetrics> Experiments { get; internal set; }

        /// <summary>Conversions that could not be credited to any experiment.</summary>
        /// <remarks>
        /// Surfaced rather than merely recorded. A steady trickle is ordinary - people who never opened the
        /// shop - but a sudden rise is how a broken exposure call announces itself, and a number nobody
        /// renders is the same as a number nobody kept.
        /// </remarks>
        public long UnattributedConversions { get; internal set; }

        /// <summary>(user, experiment) pairs that have been exposed to more than one arm.</summary>
        public long ContaminatedUsers { get; internal set; }

        /// <summary>Renders the whole report as a plain-text table.</summary>
        public string Describe() => MetricsAggregator.Describe(this);

        /// <inheritdoc />
        public override string ToString() => Describe();
    }

    /// <summary>
    /// Maintains the running totals the metrics panel reads, updating them in constant time per event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implemented as a sink rather than as something that walks the event log, because the demo fires
    /// thousands of events per click and recomputing an aggregate per event would be quadratic. Counters
    /// are kept per arm and split across the four trait combinations, so any population is a sum over at
    /// most four buckets at read time and no filtering pass over history is ever needed.
    /// </para>
    /// <para>
    /// Distinct-user counts need membership tests rather than increments, so each arm carries a set of the
    /// users it has assigned and exposed. That is the one place memory grows with the population, which is
    /// the price of the ratio check being about people rather than events.
    /// </para>
    /// </remarks>
    public sealed class MetricsAggregator : IAnalyticsSink
    {
        private const int TraitBuckets = 4;

        private sealed class ArmCounters
        {
            public readonly long[] Assignments = new long[TraitBuckets];
            public readonly long[] Exposures = new long[TraitBuckets];
            public readonly long[] Conversions = new long[TraitBuckets];

            public readonly HashSet<string>[] UsersAssigned = NewSets();
            public readonly HashSet<string>[] UsersExposed = NewSets();

            private static HashSet<string>[] NewSets()
            {
                var sets = new HashSet<string>[TraitBuckets];
                for (int i = 0; i < TraitBuckets; i++) sets[i] = new HashSet<string>(StringComparer.Ordinal);
                return sets;
            }
        }

        private readonly Dictionary<string, Dictionary<string, ArmCounters>> _byExperiment =
            new Dictionary<string, Dictionary<string, ArmCounters>>(StringComparer.Ordinal);

        private readonly long[] _unattributedConversions = new long[TraitBuckets];

        /// <summary>How many events have been folded in.</summary>
        public long EventCount { get; private set; }

        /// <inheritdoc />
        public void Record(AnalyticsEvent analyticsEvent)
        {
            if (analyticsEvent == null) return;

            EventCount++;
            int bucket = BucketOf(analyticsEvent.Traits);

            if (analyticsEvent.ExperimentId == null)
            {
                if (analyticsEvent.Kind == AnalyticsEventKind.Conversion) _unattributedConversions[bucket]++;
                return;
            }

            var arm = ArmFor(analyticsEvent.ExperimentId, analyticsEvent.VariantId);

            switch (analyticsEvent.Kind)
            {
                case AnalyticsEventKind.Assignment:
                    arm.Assignments[bucket]++;
                    arm.UsersAssigned[bucket].Add(analyticsEvent.UserId);
                    break;

                case AnalyticsEventKind.Exposure:
                    arm.Exposures[bucket]++;
                    arm.UsersExposed[bucket].Add(analyticsEvent.UserId);
                    break;

                case AnalyticsEventKind.Conversion:
                    arm.Conversions[bucket]++;
                    break;
            }
        }

        /// <summary>Builds a report over <paramref name="population"/> against the current config.</summary>
        /// <param name="config">Shapes the report and supplies the weights the ratio check compares against.</param>
        /// <param name="population">Which events to count. Defaults to <see cref="MetricsPopulation.Analysis"/>.</param>
        /// <param name="ledger">Optional, only to report the contamination count.</param>
        public MetricsReport Build(
            ExperimentConfig config, MetricsPopulation population = null, ExposureLedger ledger = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            population = population ?? MetricsPopulation.Analysis;

            var experiments = new List<ExperimentMetrics>();
            var covered = new HashSet<string>(StringComparer.Ordinal);

            foreach (var experiment in config.Experiments)
            {
                covered.Add(experiment.Id);
                experiments.Add(BuildExperiment(experiment, population));
            }

            // Anything with recorded events that the config no longer declares. Hiding these would make a
            // deleted experiment's traffic vanish from the totals without explanation.
            foreach (var pair in _byExperiment)
            {
                if (covered.Contains(pair.Key)) continue;
                experiments.Add(BuildOrphan(pair.Key, pair.Value, population));
            }

            return new MetricsReport
            {
                Population = population,
                ConfigVersion = config.ConfigVersion,
                Experiments = experiments,
                UnattributedConversions = SumAccepted(_unattributedConversions, population),
                ContaminatedUsers = ledger?.ContaminatedCount ?? 0
            };
        }

        /// <summary>Forgets every counter.</summary>
        public void Clear()
        {
            _byExperiment.Clear();
            for (int i = 0; i < TraitBuckets; i++) _unattributedConversions[i] = 0;
            EventCount = 0;
        }

        /// <summary>Renders a report as a plain-text table.</summary>
        /// <remarks>
        /// Exists before any UI does, on purpose. It is what the panel will render in Slice 5, it is
        /// assertable from a test, and it means real numbers can be eyeballed in a batchmode log now rather
        /// than something odd being discovered two slices later.
        /// </remarks>
        public static string Describe(MetricsReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));

            var text = new StringBuilder();
            text.Append("population: ").Append(report.Population.Name)
                .Append("   config: ").Append(report.ConfigVersion).Append('\n');

            text.Append("experiment            variant       assigned  exposed  exp/asgn   conv   conv/user\n");
            text.Append("--------------------  ------------  --------  -------  --------  -----  ----------\n");

            foreach (var experiment in report.Experiments)
            {
                foreach (var variant in experiment.Variants)
                {
                    text.Append(Pad(experiment.ExperimentId, 20)).Append("  ")
                        .Append(Pad(variant.VariantId + (variant.IsOrphaned ? "*" : ""), 12)).Append("  ")
                        .Append(PadLeft(variant.Assignments.ToString(), 8)).Append("  ")
                        .Append(PadLeft(variant.Exposures.ToString(), 7)).Append("  ")
                        .Append(PadLeft(Percent(variant.ExposureRate), 8)).Append("  ")
                        .Append(PadLeft(variant.Conversions.ToString(), 5)).Append("  ")
                        .Append(PadLeft(Percent(variant.ConversionRate), 10)).Append('\n');
                }

                text.Append("  ").Append(Pad(experiment.ExperimentId, 20)).Append(" SRM [")
                    .Append(experiment.Srm.Label).Append("] ").Append(experiment.Srm.Explanation).Append('\n');
            }

            text.Append("--------------------------------------------------------------------------------\n");
            text.Append("unattributed conversions: ").Append(report.UnattributedConversions);
            text.Append("   contaminated (user, experiment) pairs: ").Append(report.ContaminatedUsers);
            text.Append("\n* the current config no longer declares this variant\n");

            return text.ToString();
        }

        private ExperimentMetrics BuildExperiment(ExperimentDef experiment, MetricsPopulation population)
        {
            _byExperiment.TryGetValue(experiment.Id, out var arms);

            var variants = new List<VariantMetrics>();
            var observations = new List<SrmObservation>();
            var declared = new HashSet<string>(StringComparer.Ordinal);

            foreach (var variant in experiment.Variants)
            {
                declared.Add(variant.Id);

                ArmCounters counters = null;
                arms?.TryGetValue(variant.Id, out counters);

                var metrics = Snapshot(variant.Id, counters, population);
                metrics.ConfiguredWeight = variant.Weight;

                variants.Add(metrics);
                observations.Add(new SrmObservation(variant.Id, metrics.UsersExposed, variant.Weight));
            }

            // Arms the config has dropped since events were recorded against them.
            if (arms != null)
            {
                foreach (var pair in arms)
                {
                    if (declared.Contains(pair.Key)) continue;

                    var metrics = Snapshot(pair.Key, pair.Value, population);
                    metrics.ConfiguredWeight = -1;
                    variants.Add(metrics);
                }
            }

            return new ExperimentMetrics
            {
                ExperimentId = experiment.Id,
                LayerId = experiment.LayerId,
                Status = experiment.Status.ToString().ToLowerInvariant(),
                Variants = variants,
                Srm = SrmCheck.Evaluate(observations)
            };
        }

        private static ExperimentMetrics BuildOrphan(
            string experimentId, Dictionary<string, ArmCounters> arms, MetricsPopulation population)
        {
            var variants = new List<VariantMetrics>();
            foreach (var pair in arms)
            {
                var metrics = Snapshot(pair.Key, pair.Value, population);
                metrics.ConfiguredWeight = -1;
                variants.Add(metrics);
            }

            return new ExperimentMetrics
            {
                ExperimentId = experimentId,
                LayerId = null,
                Status = "not in config",
                Variants = variants,
                Srm = new SrmResult(SrmState.Unknown, 0, 0, 0, 0,
                    "this experiment is no longer in the configuration, so there are no weights to check against")
            };
        }

        private static VariantMetrics Snapshot(
            string variantId, ArmCounters counters, MetricsPopulation population)
        {
            var metrics = new VariantMetrics { VariantId = variantId };
            if (counters == null) return metrics;

            metrics.Assignments = SumAccepted(counters.Assignments, population);
            metrics.Exposures = SumAccepted(counters.Exposures, population);
            metrics.Conversions = SumAccepted(counters.Conversions, population);
            metrics.UsersAssigned = CountDistinct(counters.UsersAssigned, population);
            metrics.UsersExposed = CountDistinct(counters.UsersExposed, population);

            return metrics;
        }

        private static long SumAccepted(long[] byBucket, MetricsPopulation population)
        {
            long total = 0;
            for (int bucket = 0; bucket < TraitBuckets; bucket++)
            {
                if (population.Accepts(TraitsOf(bucket))) total += byBucket[bucket];
            }

            return total;
        }

        private static long CountDistinct(HashSet<string>[] byBucket, MetricsPopulation population)
        {
            // A user can appear in more than one trait bucket - hand-testing during a simulation, say - so
            // the buckets are unioned rather than summed. Anything else would count them twice.
            HashSet<string> single = null;
            HashSet<string> union = null;

            for (int bucket = 0; bucket < TraitBuckets; bucket++)
            {
                if (!population.Accepts(TraitsOf(bucket))) continue;
                if (byBucket[bucket].Count == 0) continue;

                if (single == null)
                {
                    single = byBucket[bucket];
                    continue;
                }

                if (union == null) union = new HashSet<string>(single, StringComparer.Ordinal);
                union.UnionWith(byBucket[bucket]);
            }

            if (union != null) return union.Count;
            return single?.Count ?? 0;
        }

        private ArmCounters ArmFor(string experimentId, string variantId)
        {
            if (!_byExperiment.TryGetValue(experimentId, out var arms))
            {
                arms = new Dictionary<string, ArmCounters>(StringComparer.Ordinal);
                _byExperiment[experimentId] = arms;
            }

            if (!arms.TryGetValue(variantId ?? "(none)", out var counters))
            {
                counters = new ArmCounters();
                arms[variantId ?? "(none)"] = counters;
            }

            return counters;
        }

        private static int BucketOf(EventTraits traits) => (int)traits & 0x3;

        private static EventTraits TraitsOf(int bucket) => (EventTraits)bucket;

        private static string Percent(double value) => (value * 100.0).ToString("0.0") + "%";

        private static string Pad(string value, int width)
        {
            value = value ?? "";
            if (value.Length >= width) return value.Substring(0, width);
            return value + new string(' ', width - value.Length);
        }

        private static string PadLeft(string value, int width)
        {
            value = value ?? "";
            if (value.Length >= width) return value;
            return new string(' ', width - value.Length) + value;
        }
    }
}
