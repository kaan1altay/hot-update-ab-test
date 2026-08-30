using System;
using System.Collections.Generic;
using System.Text;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Assignment
{
    /// <summary>Why a pin was thrown away.</summary>
    public enum PinDiscardReason
    {
        /// <summary>The experiment is no longer in the config at all.</summary>
        ExperimentGone,

        /// <summary>The experiment is still declared but is no longer running. This is the kill switch.</summary>
        ExperimentNotRunning,

        /// <summary>The arm the pin names has been deleted from the experiment.</summary>
        VariantGone,

        /// <summary>The experiment's layer is no longer declared, so it can never be allocated.</summary>
        LayerGone
    }

    /// <summary>What reconciliation did, in enough detail to log and to assert on.</summary>
    public sealed class PinReconcileReport
    {
        private readonly Dictionary<PinDiscardReason, int> _counts = new Dictionary<PinDiscardReason, int>();

        /// <summary>Total pins removed.</summary>
        public int RemovedCount { get; private set; }

        /// <summary>How many were removed for a given reason.</summary>
        public int CountFor(PinDiscardReason reason) => _counts.TryGetValue(reason, out int n) ? n : 0;

        internal void Add(PinDiscardReason reason, int count)
        {
            if (count <= 0) return;
            _counts.TryGetValue(reason, out int existing);
            _counts[reason] = existing + count;
            RemovedCount += count;
        }

        /// <summary>One line naming what went and why.</summary>
        public string Describe()
        {
            if (RemovedCount == 0) return "nothing to discard";

            var text = new StringBuilder();
            foreach (var pair in _counts)
            {
                if (text.Length > 0) text.Append(", ");
                text.Append(pair.Value).Append(' ').Append(Describe(pair.Key));
            }

            return text.ToString();
        }

        private static string Describe(PinDiscardReason reason)
        {
            switch (reason)
            {
                case PinDiscardReason.ExperimentGone: return "for experiments no longer in the config";
                case PinDiscardReason.ExperimentNotRunning: return "for experiments that are no longer running";
                case PinDiscardReason.VariantGone: return "naming a variant that has been deleted";
                default: return "for experiments whose layer has been deleted";
            }
        }

        /// <inheritdoc />
        public override string ToString() => Describe();
    }

    /// <summary>
    /// Throws away cached assignments that the current configuration no longer justifies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on every accepted config swap. A pin is a promise not to move a user who has already been
    /// treated, but the promise is only meaningful while the thing they were treated with still exists.
    /// The four ways it can stop existing are enumerated in <see cref="PinDiscardReason"/> and each is
    /// tested, because "the kill switch mostly works" is not a property worth having.
    /// </para>
    /// <para><b>The kill switch.</b> <see cref="PinDiscardReason.ExperimentNotRunning"/> is the important
    /// one. Setting an experiment to <c>paused</c> or <c>stopped</c> server-side must return every user to
    /// control on the next refresh, and leaving pins behind would defeat that: the user would keep being
    /// handed an arm of an experiment nobody is running any more. Discarding them here is what makes the
    /// kill switch actually kill.</para>
    ///
    /// <para><b>A stickiness flip is deliberately not a reason to discard.</b> When an experiment changes
    /// from <c>sticky_after_exposure</c> to <c>stateless</c>, its pins stop being <i>honoured</i> - see
    /// <c>ExperimentResolver</c> - but they are kept. Deleting them would make the change irreversible:
    /// flipping back to sticky would have lost the record of who had already been treated, and those users
    /// would be re-bucketed, which is exactly the contamination the sticky policy exists to prevent. A
    /// dormant pin costs a dictionary entry; a lost one costs the experiment's validity. The pins go when
    /// the experiment stops, like everyone else's.</para>
    /// </remarks>
    public static class PinReconciler
    {
        /// <summary>Removes every pin the current config no longer justifies.</summary>
        public static PinReconcileReport Reconcile(ExperimentConfig config, IAssignmentStore store)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (store == null) throw new ArgumentNullException(nameof(store));

            var report = new PinReconcileReport();

            // Snapshotted before mutating: the store's own collection would be invalidated underneath us.
            var pinnedExperiments = new List<string>(store.PinnedExperimentIds);

            foreach (string experimentId in pinnedExperiments)
            {
                var experiment = config.FindExperiment(experimentId);

                if (experiment == null)
                {
                    report.Add(PinDiscardReason.ExperimentGone, store.RemoveExperiment(experimentId));
                    continue;
                }

                if (!experiment.IsRunning)
                {
                    report.Add(PinDiscardReason.ExperimentNotRunning, store.RemoveExperiment(experimentId));
                    continue;
                }

                // Defence in depth. The validator rejects a payload whose experiment names a layer that is
                // not declared, so an accepted config cannot reach this - but reconciliation is also run
                // against configs built in code, and a pin for an unallocatable experiment would sit there
                // forever.
                if (config.FindLayer(experiment.LayerId) == null)
                {
                    report.Add(PinDiscardReason.LayerGone, store.RemoveExperiment(experimentId));
                    continue;
                }

                int variantGone = 0;
                foreach (var pair in store.PinsFor(experimentId))
                {
                    if (experiment.FindVariant(pair.Value.VariantId) != null) continue;
                    if (store.Remove(pair.Key, experimentId)) variantGone++;
                }

                report.Add(PinDiscardReason.VariantGone, variantGone);
            }

            return report;
        }
    }
}
