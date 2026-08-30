using System;
using System.Collections.Generic;

namespace HotUpdateABTest.Core.Assignment
{
    /// <summary>A variant a user has been locked to, and when.</summary>
    /// <remarks>
    /// A pin is written the moment a user is <i>exposed</i> to a treatment, never merely when they are
    /// assigned one. That timing is the whole design: assignment is a free, stateless, speculative
    /// calculation, while exposure is the event the analysis rests on, and only the latter creates an
    /// obligation not to move the user afterwards.
    /// </remarks>
    public readonly struct AssignmentPin
    {
        /// <summary>The experiment this pin belongs to.</summary>
        public string ExperimentId { get; }

        /// <summary>The arm the user is locked to.</summary>
        public string VariantId { get; }

        /// <summary>When the pin was written, which is when the user was first exposed.</summary>
        public DateTime PinnedUtc { get; }

        /// <summary>The config version in force at the moment of exposure. Diagnostic only.</summary>
        public string ConfigVersion { get; }

        /// <summary>Creates a pin.</summary>
        public AssignmentPin(string experimentId, string variantId, DateTime pinnedUtc, string configVersion)
        {
            ExperimentId = experimentId;
            VariantId = variantId;
            PinnedUtc = pinnedUtc;
            ConfigVersion = configVersion;
        }

        /// <summary>True when this is a real pin rather than a default struct.</summary>
        public bool IsValid => !string.IsNullOrEmpty(ExperimentId) && !string.IsNullOrEmpty(VariantId);

        /// <inheritdoc />
        public override string ToString() => ExperimentId + " -> " + VariantId;
    }

    /// <summary>Where pins live between resolves, and between sessions.</summary>
    public interface IAssignmentStore
    {
        /// <summary>Looks up the pin for a user and experiment.</summary>
        bool TryGet(string userId, string experimentId, out AssignmentPin pin);

        /// <summary>Writes or replaces a pin.</summary>
        void Set(string userId, AssignmentPin pin);

        /// <summary>Removes one pin. Returns true when there was one to remove.</summary>
        bool Remove(string userId, string experimentId);

        /// <summary>Removes every pin for an experiment, across all users. Returns how many went.</summary>
        /// <remarks>
        /// This is what the kill switch uses. When an experiment stops, its cached assignments must go with
        /// it, or a user would keep being handed an arm of an experiment that is no longer running.
        /// </remarks>
        int RemoveExperiment(string experimentId);

        /// <summary>Every pin held for one experiment, keyed by user id.</summary>
        /// <remarks>Reconciliation needs this to drop pins naming an arm the server has deleted.</remarks>
        IEnumerable<KeyValuePair<string, AssignmentPin>> PinsFor(string experimentId);

        /// <summary>Every experiment id that has at least one pin. Used by reconciliation.</summary>
        IReadOnlyCollection<string> PinnedExperimentIds { get; }

        /// <summary>Total number of pins held. Diagnostic, for the debug panel.</summary>
        int Count { get; }

        /// <summary>Removes everything.</summary>
        void Clear();
    }

    /// <summary>A store that lives only as long as the process.</summary>
    /// <remarks>
    /// The default in tests and in headless runs. The Unity build swaps in a file-backed store so pins
    /// survive a restart, which is what makes the sticky policy mean anything across sessions.
    /// </remarks>
    public sealed class InMemoryAssignmentStore : IAssignmentStore
    {
        private readonly Dictionary<string, Dictionary<string, AssignmentPin>> _byExperiment =
            new Dictionary<string, Dictionary<string, AssignmentPin>>(StringComparer.Ordinal);

        /// <inheritdoc />
        public IReadOnlyCollection<string> PinnedExperimentIds => new List<string>(_byExperiment.Keys);

        /// <inheritdoc />
        public int Count
        {
            get
            {
                int total = 0;
                foreach (var pair in _byExperiment) total += pair.Value.Count;
                return total;
            }
        }

        /// <inheritdoc />
        public IEnumerable<KeyValuePair<string, AssignmentPin>> PinsFor(string experimentId)
        {
            if (experimentId == null || !_byExperiment.TryGetValue(experimentId, out var users))
            {
                return new KeyValuePair<string, AssignmentPin>[0];
            }

            // Copied on purpose: reconciliation removes pins while walking this, and mutating the live
            // dictionary mid-enumeration would throw.
            return new List<KeyValuePair<string, AssignmentPin>>(users);
        }

        /// <inheritdoc />
        public bool TryGet(string userId, string experimentId, out AssignmentPin pin)
        {
            pin = default;
            if (userId == null || experimentId == null) return false;

            return _byExperiment.TryGetValue(experimentId, out var users) && users.TryGetValue(userId, out pin);
        }

        /// <inheritdoc />
        public void Set(string userId, AssignmentPin pin)
        {
            if (userId == null) throw new ArgumentNullException(nameof(userId));
            if (!pin.IsValid) throw new ArgumentException("The pin is empty.", nameof(pin));

            if (!_byExperiment.TryGetValue(pin.ExperimentId, out var users))
            {
                users = new Dictionary<string, AssignmentPin>(StringComparer.Ordinal);
                _byExperiment[pin.ExperimentId] = users;
            }

            users[userId] = pin;
        }

        /// <inheritdoc />
        public bool Remove(string userId, string experimentId)
        {
            if (userId == null || experimentId == null) return false;
            if (!_byExperiment.TryGetValue(experimentId, out var users)) return false;

            bool removed = users.Remove(userId);
            if (users.Count == 0) _byExperiment.Remove(experimentId);
            return removed;
        }

        /// <inheritdoc />
        public int RemoveExperiment(string experimentId)
        {
            if (experimentId == null) return 0;
            if (!_byExperiment.TryGetValue(experimentId, out var users)) return 0;

            int count = users.Count;
            _byExperiment.Remove(experimentId);
            return count;
        }

        /// <inheritdoc />
        public void Clear() => _byExperiment.Clear();
    }
}
