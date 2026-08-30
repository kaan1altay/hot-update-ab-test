using System;

namespace HotUpdateABTest.Core.Telemetry
{
    /// <summary>
    /// Which events a metric was computed over. Every headline number names one of these.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An explicit value rather than a condition written out wherever somebody needed one. Three flags are
    /// in play - forced, synthetic, and whether the user was exposed at all - and a codebase that filters
    /// them ad hoc ends up with two numbers on the same screen computed over different populations and no
    /// way to tell. Making the population a parameter means a metric cannot be produced without saying what
    /// it counted.
    /// </para>
    /// <para>
    /// <b>Forced is excluded from everything.</b> A QA override is a deliberate violation of the assignment
    /// ratio: somebody pinned themselves to one arm to look at it. Counting that would move the split, and
    /// the sample-ratio check would then raise an alarm about a person doing their job. It is excluded from
    /// the conversion numbers for the same reason - a tester clicking buy is not evidence.
    /// </para>
    /// <para>
    /// <b>Synthetic is included.</b> It is the only traffic this demo has, and excluding it would leave
    /// every panel at zero. A real deployment never produces it, so there is nothing to protect against;
    /// the flag exists so a reader can tell what they are looking at, and so
    /// <see cref="RealTrafficOnly"/> can answer the other question when somebody is hand-testing alongside
    /// a simulation.
    /// </para>
    /// </remarks>
    public sealed class MetricsPopulation
    {
        /// <summary>
        /// The default: everything except forced sessions. Simulated traffic counts, QA overrides do not.
        /// </summary>
        public static readonly MetricsPopulation Analysis =
            new MetricsPopulation("analysis", includeForced: false, includeSynthetic: true);

        /// <summary>Only what a real player did. Excludes both the simulator and QA overrides.</summary>
        public static readonly MetricsPopulation RealTrafficOnly =
            new MetricsPopulation("real traffic only", includeForced: false, includeSynthetic: false);

        /// <summary>Everything recorded, including forced sessions. Diagnostic; never a headline number.</summary>
        public static readonly MetricsPopulation Everything =
            new MetricsPopulation("everything, including forced", includeForced: true, includeSynthetic: true);

        /// <summary>Only forced sessions, so the debug panel can show what a QA override produced.</summary>
        public static readonly MetricsPopulation ForcedOnly =
            new MetricsPopulation("forced sessions only", includeForced: true, includeSynthetic: true,
                requireForced: true);

        /// <summary>A short name to print next to any number computed over this.</summary>
        public string Name { get; }

        /// <summary>Whether events from a QA override count.</summary>
        public bool IncludeForced { get; }

        /// <summary>Whether events from the simulator count.</summary>
        public bool IncludeSynthetic { get; }

        /// <summary>Whether only forced events count.</summary>
        public bool RequireForced { get; }

        private MetricsPopulation(
            string name, bool includeForced, bool includeSynthetic, bool requireForced = false)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            IncludeForced = includeForced;
            IncludeSynthetic = includeSynthetic;
            RequireForced = requireForced;
        }

        /// <summary>True when an event with these traits belongs to this population.</summary>
        public bool Accepts(EventTraits traits)
        {
            bool forced = (traits & EventTraits.Forced) != 0;
            bool synthetic = (traits & EventTraits.Synthetic) != 0;

            if (RequireForced && !forced) return false;
            if (forced && !IncludeForced) return false;
            if (synthetic && !IncludeSynthetic) return false;

            return true;
        }

        /// <inheritdoc />
        public override string ToString() => Name;
    }
}
