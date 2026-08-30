using System;
using System.Collections.Generic;

namespace HotUpdateABTest.Core.Telemetry
{
    /// <summary>The verdict of a sample-ratio check.</summary>
    public enum SrmState
    {
        /// <summary>Not enough data to say anything. Not the same as healthy.</summary>
        Unknown,

        /// <summary>The observed split is consistent with the configured weights.</summary>
        Healthy,

        /// <summary>The observed split is not plausibly the configured one. The results are suspect.</summary>
        Alarm
    }

    /// <summary>The full result of a sample-ratio check, including why it says what it says.</summary>
    public sealed class SrmResult
    {
        /// <summary>The verdict.</summary>
        public SrmState State { get; }

        /// <summary>The chi-square statistic, or 0 when the check did not run.</summary>
        public double ChiSquare { get; }

        /// <summary>Degrees of freedom, one less than the number of arms holding traffic.</summary>
        public int DegreesOfFreedom { get; }

        /// <summary>The critical value the statistic was compared against.</summary>
        public double CriticalValue { get; }

        /// <summary>Total observations the check ran over.</summary>
        public long Total { get; }

        /// <summary>Why the state is what it is, in one line.</summary>
        public string Explanation { get; }

        /// <summary>Creates a result.</summary>
        public SrmResult(
            SrmState state, double chiSquare, int degreesOfFreedom, double criticalValue, long total,
            string explanation)
        {
            State = state;
            ChiSquare = chiSquare;
            DegreesOfFreedom = degreesOfFreedom;
            CriticalValue = criticalValue;
            Total = total;
            Explanation = explanation;
        }

        /// <summary>A short tag for a status light.</summary>
        public string Label
        {
            get
            {
                switch (State)
                {
                    case SrmState.Healthy: return "OK";
                    case SrmState.Alarm: return "SRM";
                    default: return "--";
                }
            }
        }

        /// <inheritdoc />
        public override string ToString() => State + ": " + Explanation;
    }

    /// <summary>One arm's share of the check.</summary>
    public readonly struct SrmObservation
    {
        /// <summary>The arm.</summary>
        public string VariantId { get; }

        /// <summary>How many users were observed in it.</summary>
        public long Observed { get; }

        /// <summary>The configured weight it should have received.</summary>
        public long Weight { get; }

        /// <summary>Creates an observation.</summary>
        public SrmObservation(string variantId, long observed, long weight)
        {
            VariantId = variantId;
            Observed = observed;
            Weight = weight;
        }
    }

    /// <summary>
    /// Tests whether the split of users across an experiment's arms is plausibly the split that was
    /// configured.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured over the exposed population, not over assignments.</b> This is the most important
    /// decision in the telemetry layer and it is easy to get backwards. The population an analysis draws
    /// conclusions from is the set of users who actually saw the treatment, so that is the population whose
    /// ratio has to be checked. Checking assignments instead would leave the light green through the exact
    /// failure it exists to catch: if one arm stops logging exposures - a rendering bug, a missing call, a
    /// bad Lua patch - the assignment split stays a perfect 50/50 while the data being collected is
    /// quietly destroyed. The demo has a button that does precisely this, and an assignment-based check
    /// would sail through it.</para>
    ///
    /// <para>Read alongside the assignment-to-exposure funnel rate, the two signals point in different
    /// directions and between them identify the fault. Skewed bucketing shows up as a skewed exposure split
    /// with healthy funnel rates in every arm. Suppressed exposure logging shows up as a skewed exposure
    /// split with one arm's funnel rate collapsed.</para>
    ///
    /// <para><b>Counted in distinct users, not events.</b> A user who returns in a second session is
    /// exposed again, and counting both would let heavy users move the ratio. The question is how the
    /// population divided, so the unit is the person.</para>
    ///
    /// <para><b>Two floors, because a check that cries wolf gets ignored.</b> Chi-square needs an expected
    /// count of at least five per cell to be valid at all, and separately the demo needs a practical floor
    /// so the light does not flash red on the first few clicks - three users against one is not evidence of
    /// anything, but a naive statistic will happily call it significant. Below either floor the state is
    /// <see cref="SrmState.Unknown"/>, which is deliberately not <see cref="SrmState.Healthy"/>: "we cannot
    /// tell yet" and "we checked and it is fine" are different claims and a status light should not conflate
    /// them.</para>
    ///
    /// <para><b>Alarm at p &lt; 0.0005, not 0.05.</b> Sample-ratio mismatch is checked continuously over
    /// large populations, where a trivial imbalance becomes "significant" almost immediately. Production
    /// experimentation platforms use a threshold in this region for exactly that reason: at 0.05 a healthy
    /// experiment would raise an alarm one time in twenty, every time anybody looked, and the light would be
    /// ignored within a week.</para>
    ///
    /// <para>The statistic is compared against a tabulated critical value rather than converted into a
    /// p-value. Reporting an exact p would mean implementing the regularised incomplete gamma function to
    /// display a number nobody acts on differently, so the result carries the statistic, the degrees of
    /// freedom and the threshold instead, which is enough to explain any verdict it gives.</para>
    /// </remarks>
    public static class SrmCheck
    {
        /// <summary>Minimum expected count per arm for chi-square to be valid.</summary>
        public const double MinimumExpectedPerCell = 5.0;

        /// <summary>Minimum total observations before a verdict is offered at all.</summary>
        /// <remarks>
        /// Above the statistical floor on purpose. With two even arms the cell rule is satisfied at ten
        /// users, which is far too few for a status light somebody is watching to be worth trusting.
        /// </remarks>
        public const long MinimumTotal = 100;

        /// <summary>The significance level an alarm is raised at.</summary>
        public const double AlarmSignificance = 0.0005;

        /// <summary>Critical values of the chi-square distribution at p = 0.0005, indexed by df - 1.</summary>
        private static readonly double[] CriticalValues =
        {
            12.116, 15.202, 17.730, 19.997, 22.105, 24.103, 26.018, 27.868, 29.666, 31.420,
            33.137, 34.821, 36.478, 38.109, 39.719
        };

        /// <summary>Runs the check over one experiment's arms.</summary>
        public static SrmResult Evaluate(IReadOnlyList<SrmObservation> observations)
        {
            if (observations == null) throw new ArgumentNullException(nameof(observations));

            long totalObserved = 0;
            long totalWeight = 0;
            int armsWithTraffic = 0;

            for (int i = 0; i < observations.Count; i++)
            {
                totalObserved += observations[i].Observed;

                // An arm configured at zero weight is not part of the split. It should hold nobody, and
                // including it as an expected-zero cell would make the statistic infinite rather than
                // informative. Users found in one are reported separately below.
                if (observations[i].Weight <= 0) continue;

                totalWeight += observations[i].Weight;
                armsWithTraffic++;
            }

            if (armsWithTraffic < 2)
            {
                return new SrmResult(SrmState.Unknown, 0, 0, 0, totalObserved,
                    "only " + armsWithTraffic + " arm holds traffic, so there is no ratio to check");
            }

            // Anyone sitting in a zero-weight arm is a hard error rather than a statistical question: they
            // are in an arm the operator emptied.
            for (int i = 0; i < observations.Count; i++)
            {
                if (observations[i].Weight <= 0 && observations[i].Observed > 0)
                {
                    return new SrmResult(SrmState.Alarm, double.PositiveInfinity, armsWithTraffic - 1, 0,
                        totalObserved,
                        observations[i].Observed + " user(s) are in arm '" + observations[i].VariantId +
                        "', which is configured to receive no traffic at all");
                }
            }

            if (totalObserved < MinimumTotal)
            {
                return new SrmResult(SrmState.Unknown, 0, armsWithTraffic - 1, 0, totalObserved,
                    "only " + totalObserved + " exposed user(s); at least " + MinimumTotal +
                    " are needed before a ratio check means anything");
            }

            double chiSquare = 0;
            double smallestExpected = double.MaxValue;

            for (int i = 0; i < observations.Count; i++)
            {
                var observation = observations[i];
                if (observation.Weight <= 0) continue;

                double expected = totalObserved * (observation.Weight / (double)totalWeight);
                if (expected < smallestExpected) smallestExpected = expected;

                double delta = observation.Observed - expected;
                chiSquare += (delta * delta) / expected;
            }

            if (smallestExpected < MinimumExpectedPerCell)
            {
                return new SrmResult(SrmState.Unknown, chiSquare, armsWithTraffic - 1, 0, totalObserved,
                    "the smallest arm expects only " + smallestExpected.ToString("0.0") +
                    " user(s); chi-square needs at least " + MinimumExpectedPerCell + " per arm to be valid");
            }

            int degreesOfFreedom = armsWithTraffic - 1;
            double critical = CriticalValueFor(degreesOfFreedom);

            if (chiSquare > critical)
            {
                return new SrmResult(SrmState.Alarm, chiSquare, degreesOfFreedom, critical, totalObserved,
                    "the exposed split is not plausibly the configured one (chi-square " +
                    chiSquare.ToString("0.00") + " over " + degreesOfFreedom + " df exceeds " +
                    critical.ToString("0.00") + " at p < " + AlarmSignificance + ")");
            }

            return new SrmResult(SrmState.Healthy, chiSquare, degreesOfFreedom, critical, totalObserved,
                "the exposed split is consistent with the configured weights (chi-square " +
                chiSquare.ToString("0.00") + " over " + degreesOfFreedom + " df, under " +
                critical.ToString("0.00") + ")");
        }

        /// <summary>The chi-square critical value at <see cref="AlarmSignificance"/>.</summary>
        private static double CriticalValueFor(int degreesOfFreedom)
        {
            if (degreesOfFreedom <= 0) return double.PositiveInfinity;
            if (degreesOfFreedom <= CriticalValues.Length) return CriticalValues[degreesOfFreedom - 1];

            return WilsonHilferty(degreesOfFreedom);
        }

        /// <summary>
        /// Wilson-Hilferty approximation, used beyond the tabulated range so an experiment with many arms
        /// still gets a verdict rather than a shrug.
        /// </summary>
        /// <remarks>
        /// The cube root of a chi-square variate divided by its degrees of freedom is very nearly normal.
        /// The approximation is good to a fraction of a percent for df above about ten, and everything
        /// below that is in the table.
        /// </remarks>
        private static double WilsonHilferty(int degreesOfFreedom)
        {
            // Standard normal deviate for a one-sided tail of 0.0005.
            const double z = 3.2905267314919255;

            double d = degreesOfFreedom;
            double term = 1.0 - (2.0 / (9.0 * d)) + (z * Math.Sqrt(2.0 / (9.0 * d)));
            return d * term * term * term;
        }
    }
}
