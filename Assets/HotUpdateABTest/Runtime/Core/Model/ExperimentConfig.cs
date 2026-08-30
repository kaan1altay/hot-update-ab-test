using System;
using System.Collections.Generic;

namespace HotUpdateABTest.Core.Model
{
    /// <summary>
    /// A whole server payload: every layer, every experiment, and the versions that identify it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A config is immutable and is swapped in whole or not at all. There is no path that applies half a
    /// payload, because a partially applied config is the one state in which the framework's invariants -
    /// at most one experiment per layer, never a variant absent from the current config - could be false.
    /// Validation therefore either accepts the payload entirely or rejects it entirely and leaves the last
    /// known good one in place.
    /// </para>
    /// <para>
    /// <see cref="SchemaVersion"/> is the client's compatibility gate and is checked before anything else
    /// is read; <see cref="ConfigVersion"/> is an opaque server-assigned label used to tell one payload
    /// from another in logs, caches and the metrics panel.
    /// </para>
    /// </remarks>
    public sealed class ExperimentConfig
    {
        /// <summary>The only schema version this build understands.</summary>
        public const int SupportedSchemaVersion = 1;

        private readonly LayerDef[] _layers;
        private readonly ExperimentDef[] _experiments;

        /// <summary>Payload schema version. Anything else is rejected wholesale.</summary>
        public int SchemaVersion { get; }

        /// <summary>Opaque server-assigned label identifying this payload.</summary>
        public string ConfigVersion { get; }

        /// <summary>Every declared layer.</summary>
        public IReadOnlyList<LayerDef> Layers => _layers;

        /// <summary>Every declared experiment, whatever its status.</summary>
        public IReadOnlyList<ExperimentDef> Experiments => _experiments;

        /// <summary>Creates a config.</summary>
        public ExperimentConfig(
            int schemaVersion,
            string configVersion,
            IEnumerable<LayerDef> layers,
            IEnumerable<ExperimentDef> experiments)
        {
            SchemaVersion = schemaVersion;
            ConfigVersion = configVersion ?? throw new ArgumentNullException(nameof(configVersion));

            if (layers == null) throw new ArgumentNullException(nameof(layers));
            if (experiments == null) throw new ArgumentNullException(nameof(experiments));

            _layers = new List<LayerDef>(layers).ToArray();
            _experiments = new List<ExperimentDef>(experiments).ToArray();
        }

        /// <summary>An empty config: no layers, no experiments, everybody sees control.</summary>
        /// <remarks>
        /// This is the floor of the fallback ladder. When the server is unreachable and nothing is cached,
        /// the framework runs on this rather than failing, so an outage degrades to the shipped experience
        /// instead of a crash or a half-applied variant.
        /// </remarks>
        public static ExperimentConfig Empty { get; } = new ExperimentConfig(
            SupportedSchemaVersion, "empty", new LayerDef[0], new ExperimentDef[0]);

        /// <summary>Finds a layer by id, or returns null.</summary>
        public LayerDef FindLayer(string layerId)
        {
            if (layerId == null) return null;
            for (int i = 0; i < _layers.Length; i++)
            {
                if (string.Equals(_layers[i].Id, layerId, StringComparison.Ordinal)) return _layers[i];
            }

            return null;
        }

        /// <summary>Finds an experiment by id, or returns null.</summary>
        public ExperimentDef FindExperiment(string experimentId)
        {
            if (experimentId == null) return null;
            for (int i = 0; i < _experiments.Length; i++)
            {
                if (string.Equals(_experiments[i].Id, experimentId, StringComparison.Ordinal))
                {
                    return _experiments[i];
                }
            }

            return null;
        }

        /// <summary>Every running experiment in <paramref name="layerId"/>, in declared order.</summary>
        public List<ExperimentDef> RunningIn(string layerId)
        {
            var result = new List<ExperimentDef>();
            if (layerId == null) return result;

            for (int i = 0; i < _experiments.Length; i++)
            {
                var experiment = _experiments[i];
                if (experiment.IsRunning && string.Equals(experiment.LayerId, layerId, StringComparison.Ordinal))
                {
                    result.Add(experiment);
                }
            }

            return result;
        }

        /// <inheritdoc />
        public override string ToString() =>
            "config " + ConfigVersion + " (schema " + SchemaVersion + ", " +
            _layers.Length + " layers, " + _experiments.Length + " experiments)";
    }
}
