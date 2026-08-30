using System.Collections.Generic;
using HotUpdateABTest.Core.Hashing;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Terse builders for the config shapes the assignment tests need, so a test reads as the scenario it
    /// describes rather than as six lines of constructor arguments.
    /// </summary>
    internal static class TestConfigs
    {
        /// <summary>A variant with the given id and weight, behaving as itself.</summary>
        public static VariantDef Variant(string id, int weight) =>
            new VariantDef(id, weight, "test.behavior." + id);

        /// <summary>An even two-arm control/treatment split.</summary>
        public static VariantDef[] EvenSplit() => new[]
        {
            Variant(VariantDef.ControlId, 5000),
            Variant("treatment", 5000)
        };

        /// <summary>A running experiment claiming the whole of its layer.</summary>
        public static ExperimentDef Experiment(
            string id,
            string layerId,
            IEnumerable<VariantDef> variants = null,
            ExperimentStatus status = ExperimentStatus.Running,
            BucketRange? allocation = null,
            StickinessPolicy stickiness = StickinessPolicy.StickyAfterExposure,
            string salt = null)
        {
            return new ExperimentDef(
                id,
                layerId,
                status,
                salt ?? (id + ".salt"),
                allocation ?? BucketRange.Full,
                stickiness,
                variants ?? EvenSplit());
        }

        /// <summary>A layer whose salt is derived from its id.</summary>
        public static LayerDef Layer(string id) => new LayerDef(id, id + ".salt.v1");

        /// <summary>A config over the given layers and experiments.</summary>
        public static ExperimentConfig Config(IEnumerable<LayerDef> layers, IEnumerable<ExperimentDef> experiments) =>
            new ExperimentConfig(ExperimentConfig.SupportedSchemaVersion, "test", layers, experiments);

        /// <summary>A config holding one running experiment that owns its whole layer.</summary>
        public static ExperimentConfig SingleExperiment(
            string layerId = "layer_a",
            string experimentId = "exp_a",
            IEnumerable<VariantDef> variants = null,
            ExperimentStatus status = ExperimentStatus.Running,
            StickinessPolicy stickiness = StickinessPolicy.StickyAfterExposure)
        {
            var layer = Layer(layerId);
            var experiment = Experiment(experimentId, layerId, variants, status, BucketRange.Full, stickiness);
            return Config(new[] { layer }, new[] { experiment });
        }

        /// <summary>A deterministic, stable set of synthetic user ids.</summary>
        /// <remarks>
        /// Assignment is a pure function of the id, so generating ids by index rather than at random keeps
        /// every statistical assertion in this suite reproducible. A distribution test that can flake is a
        /// distribution test that eventually gets a wider tolerance instead of a fix.
        /// </remarks>
        public static IEnumerable<string> Users(int count, string prefix = "user-")
        {
            for (int i = 0; i < count; i++) yield return prefix + i;
        }

        /// <summary>Chi-square statistic of observed counts against expected counts.</summary>
        public static double ChiSquare(IReadOnlyList<int> observed, IReadOnlyList<double> expected)
        {
            double total = 0;
            for (int i = 0; i < observed.Count; i++)
            {
                double e = expected[i];
                if (e <= 0) continue;
                double d = observed[i] - e;
                total += (d * d) / e;
            }

            return total;
        }

        /// <summary>Chi-square critical value at p = 0.001 for the first few degrees of freedom.</summary>
        /// <remarks>
        /// Assignment is deterministic here, so these tests cannot flake; a strict threshold is free. The
        /// table is short because no test in this suite needs more than three degrees of freedom.
        /// </remarks>
        public static double ChiSquareCritical001(int degreesOfFreedom)
        {
            switch (degreesOfFreedom)
            {
                case 1: return 10.828;
                case 2: return 13.816;
                case 3: return 16.266;
                default: return 20.515; // df = 5, comfortably conservative for anything larger used here.
            }
        }

        /// <summary>Total number of buckets in the space, re-exported so tests read cleanly.</summary>
        public const int BucketCount = BucketSpace.BucketCount;
    }
}
