using System;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Config
{
    /// <summary>Where the configuration currently in force came from.</summary>
    /// <remarks>
    /// This is the rung of the fallback ladder the framework is standing on. It is public and observable
    /// because a guardrail nobody can see is a guardrail that gets blamed on something else: when the
    /// shop screen shows control for everyone, the operator needs to be able to tell "the server said so"
    /// from "we could not reach the server" without reading a log file.
    /// </remarks>
    public enum ConfigSourceKind
    {
        /// <summary>Nothing has been loaded yet. Resolves as if every experiment were stopped.</summary>
        None,

        /// <summary>A payload fetched and accepted this session.</summary>
        Live,

        /// <summary>The last payload that was accepted, restored from the on-disk cache.</summary>
        LastKnownGood,

        /// <summary>The config that shipped with the build. The floor of the ladder.</summary>
        ShippedDefaults
    }

    /// <summary>
    /// An immutable view of the configuration in force, together with how it got there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Snapshots are the unit of atomic swap. <see cref="ConfigService"/> publishes a new one with a single
    /// reference assignment, so a resolve already in flight finishes against the snapshot it started with
    /// and a resolve that starts afterwards sees the new one. There is no window in which a caller can
    /// observe half a config, which is what keeps the framework's invariants - at most one experiment per
    /// layer, never a variant absent from the current config - true at every instant rather than merely
    /// most of the time.
    /// </para>
    /// <para>
    /// Everything reachable from here is immutable, so holding a snapshot across a swap is not just safe
    /// but occasionally the right thing to do: a screen that resolved against version 7 can keep rendering
    /// version 7 until it next re-reads, rather than changing under the player mid-frame.
    /// </para>
    /// </remarks>
    public sealed class ConfigSnapshot
    {
        /// <summary>The configuration itself. Never null.</summary>
        public ExperimentConfig Config { get; }

        /// <summary>Which rung of the fallback ladder this came from.</summary>
        public ConfigSourceKind Source { get; }

        /// <summary>
        /// Why the framework is on this rung rather than a higher one, or null when it is on
        /// <see cref="ConfigSourceKind.Live"/> and nothing went wrong.
        /// </summary>
        public string Reason { get; }

        /// <summary>When this snapshot was published.</summary>
        public DateTime AcquiredUtc { get; }

        /// <summary>The payload's version label, shortcut for <c>Config.ConfigVersion</c>.</summary>
        public string ConfigVersion => Config.ConfigVersion;

        /// <summary>True when the framework is not running on a payload fetched this session.</summary>
        public bool IsDegraded => Source != ConfigSourceKind.Live;

        /// <summary>Creates a snapshot.</summary>
        public ConfigSnapshot(ExperimentConfig config, ConfigSourceKind source, DateTime acquiredUtc, string reason = null)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Source = source;
            AcquiredUtc = acquiredUtc;
            Reason = reason;
        }

        /// <summary>The starting snapshot, before anything has been fetched or restored.</summary>
        public static ConfigSnapshot Nothing(DateTime nowUtc) =>
            new ConfigSnapshot(ExperimentConfig.Empty, ConfigSourceKind.None, nowUtc,
                "no configuration has been loaded yet");

        /// <summary>One line for a log or a status panel.</summary>
        public string Describe()
        {
            string head = DescribeSource() + " (version '" + ConfigVersion + "', " +
                          Config.Experiments.Count + " experiments)";
            return Reason == null ? head : head + " - " + Reason;
        }

        private string DescribeSource()
        {
            switch (Source)
            {
                case ConfigSourceKind.Live: return "live";
                case ConfigSourceKind.LastKnownGood: return "last known good";
                case ConfigSourceKind.ShippedDefaults: return "shipped defaults";
                default: return "nothing loaded";
            }
        }

        /// <inheritdoc />
        public override string ToString() => Describe();
    }
}
