using System;
using System.Collections.Generic;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Assignment
{
    /// <summary>
    /// Forced variant selections made from the debug panel, bypassing bucketing entirely.
    /// </summary>
    /// <remarks>
    /// Kept as its own object rather than as a field on the resolver so that it can be inspected, cleared
    /// and asserted on independently, and so that "is anything forced right now" is one call for the
    /// banner the demo shows while an override is active.
    /// </remarks>
    public sealed class QaOverrides
    {
        private readonly Dictionary<string, string> _byExperiment = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>True when at least one override is active.</summary>
        public bool Any => _byExperiment.Count > 0;

        /// <summary>How many overrides are active.</summary>
        public int Count => _byExperiment.Count;

        /// <summary>Every active override, experiment id to variant id.</summary>
        public IReadOnlyDictionary<string, string> All => _byExperiment;

        /// <summary>Forces <paramref name="experimentId"/> to <paramref name="variantId"/>.</summary>
        public void Force(string experimentId, string variantId)
        {
            if (experimentId == null) throw new ArgumentNullException(nameof(experimentId));
            if (variantId == null) throw new ArgumentNullException(nameof(variantId));
            _byExperiment[experimentId] = variantId;
        }

        /// <summary>Removes the override for one experiment.</summary>
        public bool Clear(string experimentId) =>
            experimentId != null && _byExperiment.Remove(experimentId);

        /// <summary>Removes every override.</summary>
        public void ClearAll() => _byExperiment.Clear();

        /// <summary>The forced variant for an experiment, or null.</summary>
        public string For(string experimentId)
        {
            if (experimentId == null) return null;
            return _byExperiment.TryGetValue(experimentId, out string variantId) ? variantId : null;
        }
    }

    /// <summary>
    /// Turns a user and a layer into the arm they should see, composing bucketing with pins, audience and
    /// the QA override.
    /// </summary>
    /// <remarks>
    /// <para>The order of the five steps is the design, and each one is where a naive implementation goes
    /// wrong:</para>
    /// <list type="number">
    /// <item><description><b>Forced override.</b> Checked first and bypasses everything, because the point
    /// of a QA override is to reach states bucketing will not give you. The resulting assignment is marked
    /// <see cref="AssignmentSource.Forced"/> so nothing downstream mistakes it for evidence.</description></item>
    /// <item><description><b>Layer allocation.</b> One hash, salted per layer, picks the running experiment
    /// whose range holds the user - or none.</description></item>
    /// <item><description><b>Audience.</b> Applied <i>after</i> allocation, never before. A user's bucket is
    /// a property of the user and does not move because they failed a predicate, so a targeted experiment
    /// holds its allocation width multiplied by the match rate. Filtering first would silently re-pack the
    /// layer and make two targeted experiments overlap.</description></item>
    /// <item><description><b>Pin.</b> Honoured only for a sticky experiment, and only when the arm it names
    /// still exists. This is what stops an already-exposed user moving when weights change.</description></item>
    /// <item><description><b>Bucketing.</b> A second hash, salted per experiment, picks the arm.</description></item>
    /// </list>
    ///
    /// <para><b>A pin outranks the audience it was written under, but not the kill switch.</b> If a user
    /// was exposed and later stops matching the audience - they changed country, or the operator narrowed
    /// the targeting - the pin still applies. They have already been treated; pretending otherwise would
    /// change the product under someone mid-experiment and split one person across two arms of the
    /// analysis. What does remove them is the experiment ceasing to run, which discards the pin outright.</para>
    ///
    /// <para>Resolution is a pure read against one immutable snapshot. It takes no locks, allocates one
    /// result object, and is safe to call from any thread and as often as the caller likes - including
    /// speculatively, since nothing is logged here.</para>
    /// </remarks>
    public sealed class ExperimentResolver
    {
        private readonly IAssignmentStore _store;
        private readonly QaOverrides _overrides;
        private readonly IAudiencePredicateEvaluator _predicates;

        /// <summary>The forced selections in effect. Never null.</summary>
        public QaOverrides Overrides => _overrides;

        /// <summary>Creates a resolver.</summary>
        /// <param name="store">Where pins live. Optional; without one, every resolve is stateless.</param>
        /// <param name="overrides">Forced selections. Optional; a new empty set is created if omitted.</param>
        /// <param name="predicates">
        /// Runs Lua audience predicates. Optional; when an experiment names a predicate and there is no
        /// evaluator, the user is excluded rather than admitted - see the fail-closed note below.
        /// </param>
        public ExperimentResolver(
            IAssignmentStore store = null,
            QaOverrides overrides = null,
            IAudiencePredicateEvaluator predicates = null)
        {
            _store = store;
            _overrides = overrides ?? new QaOverrides();
            _predicates = predicates;
        }

        /// <summary>Resolves one layer for one user.</summary>
        public VariantAssignment Resolve(ConfigSnapshot snapshot, UserContext user, string layerId)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return Resolve(snapshot.Config, user, layerId);
        }

        /// <summary>Resolves one layer for one user against a specific config.</summary>
        public VariantAssignment Resolve(ExperimentConfig config, UserContext user, string layerId)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (layerId == null) throw new ArgumentNullException(nameof(layerId));

            var layer = config.FindLayer(layerId);
            if (layer == null)
            {
                return VariantAssignment.NotAssigned(layerId, NoAssignmentReason.UnknownLayer,
                    "the configuration declares no layer '" + layerId + "'", -1, config.ConfigVersion);
            }

            int layerBucket = LayerAllocator.BucketOf(user.UserId, layer);

            // 1. Forced override. Deliberately ahead of everything, including the running check: QA needs
            //    to be able to preview an arm of an experiment that is not live yet. It cannot conjure an
            //    experiment or an arm that the config does not declare, though - a forced variant that no
            //    longer exists is stale tooling state, not a licence to invent one.
            var forced = ResolveForced(config, layer, layerBucket);
            if (forced != null) return forced;

            // 2. Layer allocation.
            var experiment = LayerAllocator.AllocateAt(config, layer, layerBucket);
            if (experiment == null)
            {
                return VariantAssignment.NotAssigned(layerId, NoAssignmentReason.OutsideAllocation,
                    "bucket " + layerBucket + " is not claimed by any running experiment in this layer",
                    layerBucket, config.ConfigVersion);
            }

            // 4. Pin, before the audience check, so an already-exposed user keeps their arm even if they no
            //    longer qualify. Checked here rather than after bucketing so the pin genuinely short
            //    circuits the hash.
            var pinned = ResolvePinned(config, experiment, user, layerBucket);
            if (pinned != null) return pinned;

            // 3. Audience: the declarative clauses first, then the Lua predicate if one is named.
            string mismatch = experiment.Audience.ExplainMismatch(user) ?? ExplainPredicateMismatch(experiment, user);
            if (mismatch != null)
            {
                return VariantAssignment.NotAssigned(layerId, NoAssignmentReason.AudienceExcluded,
                    "experiment '" + experiment.Id + "' excludes this user: " + mismatch,
                    layerBucket, config.ConfigVersion);
            }

            // 5. Bucketing.
            int variantBucket = VariantAssigner.BucketOf(user.UserId, experiment);
            var variant = VariantAssigner.AssignAt(experiment, variantBucket);

            if (variant == null)
            {
                return VariantAssignment.NotAssigned(layerId, NoAssignmentReason.NoTrafficInVariants,
                    "experiment '" + experiment.Id + "' is running but every variant has weight 0",
                    layerBucket, config.ConfigVersion);
            }

            return VariantAssignment.Assigned(layerId, experiment, variant, AssignmentSource.Bucketed,
                layerBucket, variantBucket, config.ConfigVersion);
        }

        /// <summary>Resolves every declared layer for one user.</summary>
        public IReadOnlyList<VariantAssignment> ResolveAll(ConfigSnapshot snapshot, UserContext user)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var config = snapshot.Config;
            var results = new List<VariantAssignment>(config.Layers.Count);

            foreach (var layer in config.Layers) results.Add(Resolve(config, user, layer.Id));
            return results;
        }

        /// <summary>
        /// Records that <paramref name="user"/> has been exposed to the arm in <paramref name="assignment"/>,
        /// pinning it if the experiment's policy says to.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called by the exposure tracker, never by resolution. This is the single place a pin is written,
        /// and it is written at exposure time on purpose: assignment is speculative and free, exposure is
        /// the event that creates an obligation not to move the user afterwards.
        /// </para>
        /// <para>
        /// A forced assignment never pins. The override is a tooling state that must vanish when it is
        /// cleared, and writing it into the store would leave the tester wondering why the app is still
        /// showing an arm they turned off.
        /// </para>
        /// </remarks>
        public bool NotifyExposed(UserContext user, VariantAssignment assignment, DateTime nowUtc)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (assignment == null) throw new ArgumentNullException(nameof(assignment));

            if (_store == null) return false;
            if (!assignment.IsAssigned) return false;
            if (assignment.IsForced) return false;
            if (assignment.Experiment.Stickiness != StickinessPolicy.StickyAfterExposure) return false;

            // Already pinned, and to the same arm: nothing to do. Rewriting would move PinnedUtc and lose
            // the record of when the user was actually first treated.
            if (_store.TryGet(user.UserId, assignment.ExperimentId, out var existing) &&
                string.Equals(existing.VariantId, assignment.VariantId, StringComparison.Ordinal))
            {
                return false;
            }

            _store.Set(user.UserId, new AssignmentPin(
                assignment.ExperimentId, assignment.VariantId, nowUtc, assignment.ConfigVersion));
            return true;
        }

        /// <summary>
        /// Runs the experiment's Lua audience predicate, if it names one, and explains a failure.
        /// </summary>
        /// <remarks>
        /// Fails closed at every step, including the case where no evaluator was supplied at all. A named
        /// predicate that cannot be run is not the same as no predicate: the config asked for a narrowing
        /// that this build cannot perform, and admitting the user anyway would apply a treatment to a
        /// population nobody scoped. Excluding them costs sample size, which is the cheaper mistake.
        /// </remarks>
        private string ExplainPredicateMismatch(ExperimentDef experiment, UserContext user)
        {
            string key = experiment.Audience.PredicateKey;
            if (key == null) return null;

            if (_predicates == null)
            {
                return "audience predicate '" + key + "' cannot be evaluated because no predicate " +
                       "evaluator is wired up, and an unevaluable predicate excludes rather than admits";
            }

            return _predicates.Matches(key, user)
                ? null
                : "audience predicate '" + key + "' did not match";
        }

        private VariantAssignment ResolveForced(ExperimentConfig config, LayerDef layer, int layerBucket)
        {
            if (!_overrides.Any) return null;

            foreach (var experiment in config.Experiments)
            {
                if (!string.Equals(experiment.LayerId, layer.Id, StringComparison.Ordinal)) continue;

                string variantId = _overrides.For(experiment.Id);
                if (variantId == null) continue;

                var variant = experiment.FindVariant(variantId);
                if (variant == null)
                {
                    // The override names an arm the config no longer has. Ignoring it and falling through
                    // to normal resolution is the only safe reading: the invariant that the framework never
                    // applies a variant absent from the current config holds for forced sessions too.
                    continue;
                }

                return VariantAssignment.Assigned(layer.Id, experiment, variant, AssignmentSource.Forced,
                    layerBucket, -1, config.ConfigVersion);
            }

            return null;
        }

        private VariantAssignment ResolvePinned(
            ExperimentConfig config, ExperimentDef experiment, UserContext user, int layerBucket)
        {
            if (_store == null) return null;

            // A stateless experiment ignores pins without deleting them, so flipping the policy back to
            // sticky restores the users who were already treated instead of re-bucketing them.
            if (experiment.Stickiness != StickinessPolicy.StickyAfterExposure) return null;

            if (!_store.TryGet(user.UserId, experiment.Id, out var pin)) return null;

            var variant = experiment.FindVariant(pin.VariantId);
            if (variant == null)
            {
                // Reconciliation removes these on a config swap, so reaching this means the store was
                // mutated by something else. Falling through to bucketing keeps the invariant.
                return null;
            }

            return VariantAssignment.Assigned(experiment.LayerId, experiment, variant, AssignmentSource.Pinned,
                layerBucket, -1, config.ConfigVersion);
        }
    }
}
