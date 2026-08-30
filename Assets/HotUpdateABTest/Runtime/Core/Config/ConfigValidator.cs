using System;
using System.Collections.Generic;
using HotUpdateABTest.Core.Hashing;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Config
{
    /// <summary>
    /// Checks the rules that a well-formed payload can still break: references that do not resolve,
    /// duplicate identifiers, and above all overlapping traffic within a layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ConfigReader"/> answers "is this the right shape"; this answers "does it make sense".
    /// The split matters because the two fail for different reasons and deserve different messages: a
    /// shape error is usually a serialization bug on the server, a semantic error is usually an operator
    /// setting up two experiments that fight.
    /// </para>
    /// <para>
    /// The overlap rule is the load-bearing one. Mutual exclusion within a layer is structural in this
    /// framework - the allocator simply asks which range contains the user's bucket - which is only safe
    /// because a config where two running experiments claim the same bucket never gets accepted. This
    /// class is where that guarantee is actually made, and everything downstream depends on it holding.
    /// </para>
    /// <para>
    /// Non-running experiments are exempt from the overlap rule on purpose. An operator preparing a
    /// replacement writes it against the same traffic the current experiment holds, leaves it
    /// <c>draft</c>, and flips the pair over in one payload. Forbidding the overlap while it is still
    /// draft would make that ordinary manoeuvre impossible.
    /// </para>
    /// </remarks>
    public static class ConfigValidator
    {
        /// <summary>Checks <paramref name="config"/> and reports everything wrong with it.</summary>
        public static ValidationResult Validate(ExperimentConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var issues = new ValidationBuilder();

            ValidateLayers(config, issues);
            ValidateExperiments(config, issues);
            ValidateLayerTraffic(config, issues);

            return issues.Build();
        }

        private static void ValidateLayers(ExperimentConfig config, ValidationBuilder issues)
        {
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenSalts = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var layer in config.Layers)
            {
                string entity = "layer '" + layer.Id + "'";

                if (!seenIds.Add(layer.Id))
                {
                    issues.Error("layer.duplicateId", entity, "declared more than once");
                }

                // Two layers sharing a salt are perfectly confounded: every user lands on the same bucket
                // in both, so their experiments hold identical populations and an interaction between them
                // cannot be told apart from a main effect of either. This is the single most damaging
                // mistake available in this config format and it is completely invisible at runtime, so it
                // is rejected rather than warned about.
                if (seenSalts.TryGetValue(layer.Salt, out string firstOwner))
                {
                    issues.Error("layer.duplicateSalt", entity,
                        "uses the same salt as layer '" + firstOwner + "'; layers sharing a salt bucket " +
                        "every user identically, which makes their experiments perfectly confounded rather " +
                        "than independent");
                }
                else
                {
                    seenSalts[layer.Salt] = layer.Id;
                }
            }
        }

        private static void ValidateExperiments(ExperimentConfig config, ValidationBuilder issues)
        {
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var experiment in config.Experiments)
            {
                string entity = "experiment '" + experiment.Id + "'";

                if (!seenIds.Add(experiment.Id))
                {
                    issues.Error("experiment.duplicateId", entity, "declared more than once");
                }

                if (config.FindLayer(experiment.LayerId) == null)
                {
                    issues.Error("experiment.unknownLayer", entity,
                        "references unknown layer '" + experiment.LayerId + "'");
                }

                ValidateAllocation(experiment, entity, issues);
                ValidateVariants(experiment, entity, issues);
            }
        }

        private static void ValidateAllocation(ExperimentDef experiment, string entity, ValidationBuilder issues)
        {
            var allocation = experiment.Allocation;

            if (allocation.From < 0 || allocation.To > BucketSpace.BucketCount)
            {
                issues.Error("experiment.allocation.outOfRange", entity,
                    "allocation " + allocation + " falls outside the bucket space [0, " +
                    BucketSpace.BucketCount + ")");
            }

            if (allocation.To < allocation.From)
            {
                issues.Error("experiment.allocation.inverted", entity,
                    "allocation " + allocation + " ends before it starts");
            }
            else if (allocation.IsEmpty && experiment.IsRunning)
            {
                // Legal, and occasionally deliberate while an operator stages a ramp, but almost always a
                // mistake. A warning keeps it visible without blocking the payload.
                issues.Warning("experiment.allocation.empty", entity,
                    "is running but its allocation " + allocation + " claims no traffic, so it will never " +
                    "assign anyone");
            }
        }

        private static void ValidateVariants(ExperimentDef experiment, string entity, ValidationBuilder issues)
        {
            if (experiment.Variants.Count == 0)
            {
                issues.Error("experiment.noVariants", entity, "declares no variants");
                return;
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variant in experiment.Variants)
            {
                if (!seenIds.Add(variant.Id))
                {
                    issues.Error("variant.duplicateId", entity + " > variant '" + variant.Id + "'",
                        "declared more than once; variant order and identity decide bucketing, so a " +
                        "duplicate is ambiguous rather than harmless");
                }
            }

            // Control must be explicit. An experiment without a named control has no baseline to compare
            // against, and more practically the kill switch and every fallback path need an arm to return
            // users to.
            if (experiment.Control == null)
            {
                issues.Error("experiment.noControl", entity,
                    "declares no variant with id '" + VariantDef.ControlId + "'; control must be explicit, " +
                    "because it is the arm the kill switch and every fallback return users to");
            }

            long totalWeight = experiment.TotalWeight;
            if (totalWeight <= 0)
            {
                if (experiment.IsRunning)
                {
                    issues.Error("experiment.zeroWeight", entity, "variant weights sum to 0");
                }
                else
                {
                    issues.Warning("experiment.zeroWeight", entity,
                        "variant weights sum to 0; harmless while " + Describe(experiment.Status) +
                        ", but it cannot be started as it stands");
                }
            }
        }

        private static void ValidateLayerTraffic(ExperimentConfig config, ValidationBuilder issues)
        {
            var byLayer = new Dictionary<string, List<ExperimentDef>>(StringComparer.Ordinal);

            foreach (var experiment in config.Experiments)
            {
                if (!experiment.IsRunning) continue;

                if (!byLayer.TryGetValue(experiment.LayerId, out var list))
                {
                    list = new List<ExperimentDef>();
                    byLayer[experiment.LayerId] = list;
                }

                list.Add(experiment);
            }

            foreach (var pair in byLayer)
            {
                var running = pair.Value;
                string entity = "layer '" + pair.Key + "'";

                for (int i = 0; i < running.Count; i++)
                {
                    for (int j = i + 1; j < running.Count; j++)
                    {
                        var a = running[i];
                        var b = running[j];
                        if (!a.Allocation.Overlaps(b.Allocation)) continue;

                        issues.Error("layer.overlappingAllocations", entity,
                            "running experiments '" + a.Id + "' " + a.Allocation + " and '" + b.Id + "' " +
                            b.Allocation + " claim overlapping traffic; experiments in one layer must be " +
                            "mutually exclusive");
                    }
                }
            }
        }

        private static string Describe(ExperimentStatus status)
        {
            switch (status)
            {
                case ExperimentStatus.Draft: return "a draft";
                case ExperimentStatus.Paused: return "paused";
                case ExperimentStatus.Stopped: return "stopped";
                default: return "running";
            }
        }
    }
}
