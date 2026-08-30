using System;
using System.Collections.Generic;

namespace HotUpdateABTest.Core.Telemetry
{
    /// <summary>What a user was exposed to in one experiment, and when.</summary>
    public readonly struct ExposureRecord
    {
        /// <summary>The experiment.</summary>
        public string ExperimentId { get; }

        /// <summary>The arm the user actually saw.</summary>
        public string VariantId { get; }

        /// <summary>The layer the experiment belongs to.</summary>
        public string LayerId { get; }

        /// <summary>The traits of the exposure, carried forward onto anything attributed to it.</summary>
        public EventTraits Traits { get; }

        /// <summary>The config version in force at exposure.</summary>
        public string ConfigVersion { get; }

        /// <summary>When the user was first exposed to this arm.</summary>
        public DateTime FirstExposedUtc { get; }

        /// <summary>The session the first exposure happened in.</summary>
        public SessionId FirstSession { get; }

        /// <summary>
        /// True when this user has been exposed to more than one arm of this experiment. Their outcome
        /// cannot be cleanly attributed and their data is suspect.
        /// </summary>
        public bool IsContaminated { get; }

        /// <summary>Creates a record.</summary>
        public ExposureRecord(
            string experimentId,
            string variantId,
            string layerId,
            EventTraits traits,
            string configVersion,
            DateTime firstExposedUtc,
            SessionId firstSession,
            bool isContaminated)
        {
            ExperimentId = experimentId;
            VariantId = variantId;
            LayerId = layerId;
            Traits = traits;
            ConfigVersion = configVersion;
            FirstExposedUtc = firstExposedUtc;
            FirstSession = firstSession;
            IsContaminated = isContaminated;
        }

        /// <summary>True when this is a real record rather than a default struct.</summary>
        public bool IsValid => !string.IsNullOrEmpty(ExperimentId);

        internal ExposureRecord AsContaminated() => new ExposureRecord(
            ExperimentId, VariantId, LayerId, Traits, ConfigVersion, FirstExposedUtc, FirstSession, true);
    }

    /// <summary>
    /// The record of who has been exposed to what. Deduplication and conversion attribution both read from
    /// here, and neither ever re-resolves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single most important object in the telemetry layer, because it is what makes
    /// attribution honest. If a conversion were attributed by asking the resolver what arm the user is in
    /// <i>now</i>, then any config change between exposure and conversion - a weight ramp, a kill switch, a
    /// pin invalidation - would silently move the outcome onto the wrong arm. The ledger holds what the user
    /// actually saw, so attribution reads history rather than recomputing a present that has moved on.
    /// </para>
    /// <para>
    /// It keeps two things per user and experiment: the arm they were first exposed to, which is what
    /// conversions attribute to, and the set of arms they have been exposed to at all, which is how
    /// contamination is detected. A user appearing in two arms is not silently tolerated and not silently
    /// dropped; it is recorded, flagged, and counted.
    /// </para>
    /// <para>
    /// Session-scoped dedup state is kept separately from the lifetime attribution record, because they
    /// have different lifetimes: the first is only interesting while a visit is open and can be released
    /// with <see cref="ForgetSession"/>, while the second has to outlive every session the user has.
    /// </para>
    /// </remarks>
    public sealed class ExposureLedger
    {
        private readonly struct SessionExposureKey : IEquatable<SessionExposureKey>
        {
            private readonly string _userId;
            private readonly string _experimentId;
            private readonly string _variantId;
            private readonly SessionId _session;

            public SessionExposureKey(string userId, string experimentId, string variantId, SessionId session)
            {
                _userId = userId;
                _experimentId = experimentId;
                _variantId = variantId;
                _session = session;
            }

            public SessionId Session => _session;

            public bool Equals(SessionExposureKey other) =>
                string.Equals(_userId, other._userId, StringComparison.Ordinal) &&
                string.Equals(_experimentId, other._experimentId, StringComparison.Ordinal) &&
                string.Equals(_variantId, other._variantId, StringComparison.Ordinal) &&
                _session.Equals(other._session);

            public override bool Equals(object obj) => obj is SessionExposureKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _userId == null ? 0 : StringComparer.Ordinal.GetHashCode(_userId);
                    hash = (hash * 397) ^ (_experimentId == null ? 0 : StringComparer.Ordinal.GetHashCode(_experimentId));
                    hash = (hash * 397) ^ (_variantId == null ? 0 : StringComparer.Ordinal.GetHashCode(_variantId));
                    hash = (hash * 397) ^ _session.GetHashCode();
                    return hash;
                }
            }
        }

        // Dedup: has this exact (user, experiment, variant) already been logged in this session?
        private readonly HashSet<SessionExposureKey> _loggedThisSession = new HashSet<SessionExposureKey>();

        // Attribution: user -> experiment -> what they were first exposed to. Outlives sessions.
        private readonly Dictionary<string, Dictionary<string, ExposureRecord>> _records =
            new Dictionary<string, Dictionary<string, ExposureRecord>>(StringComparer.Ordinal);

        // Contamination detection: user -> experiment -> every arm ever seen.
        private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _armsSeen =
            new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);

        /// <summary>How many users have at least one exposure recorded.</summary>
        public int UserCount => _records.Count;

        /// <summary>How many (user, experiment) pairs are contaminated.</summary>
        public int ContaminatedCount { get; private set; }

        /// <summary>What the outcome of offering an exposure to the ledger was.</summary>
        public enum Outcome
        {
            /// <summary>First time this user has seen this arm in this session. Log it.</summary>
            New,

            /// <summary>Already logged in this session. Do not log again.</summary>
            Duplicate,

            /// <summary>
            /// New in this session, but the user has previously been exposed to a different arm of the same
            /// experiment. Logged, and both the event and the record are flagged.
            /// </summary>
            Contaminating
        }

        /// <summary>
        /// Offers an exposure. Returns whether it should be logged, and records it when it should.
        /// </summary>
        public Outcome Offer(
            string userId,
            string experimentId,
            string variantId,
            string layerId,
            SessionId session,
            EventTraits traits,
            string configVersion,
            DateTime nowUtc)
        {
            if (userId == null) throw new ArgumentNullException(nameof(userId));
            if (experimentId == null) throw new ArgumentNullException(nameof(experimentId));
            if (variantId == null) throw new ArgumentNullException(nameof(variantId));

            var key = new SessionExposureKey(userId, experimentId, variantId, session);
            if (!_loggedThisSession.Add(key)) return Outcome.Duplicate;

            bool contaminating = NoteArm(userId, experimentId, variantId);

            if (!_records.TryGetValue(userId, out var byExperiment))
            {
                byExperiment = new Dictionary<string, ExposureRecord>(StringComparer.Ordinal);
                _records[userId] = byExperiment;
            }

            if (byExperiment.TryGetValue(experimentId, out var existing))
            {
                // The attribution target never moves once set. The first arm a user saw is the one their
                // outcomes belong to; a later exposure to a second arm makes the record suspect, but it does
                // not get to claim the conversions.
                if (contaminating)
                {
                    byExperiment[experimentId] = existing.AsContaminated();
                    ContaminatedCount++;
                }
            }
            else
            {
                byExperiment[experimentId] = new ExposureRecord(
                    experimentId, variantId, layerId, traits, configVersion, nowUtc, session, false);
            }

            return contaminating ? Outcome.Contaminating : Outcome.New;
        }

        /// <summary>The arm a user's outcomes in one experiment attribute to, if any.</summary>
        public bool TryGetRecord(string userId, string experimentId, out ExposureRecord record)
        {
            record = default;
            if (userId == null || experimentId == null) return false;

            return _records.TryGetValue(userId, out var byExperiment) &&
                   byExperiment.TryGetValue(experimentId, out record);
        }

        /// <summary>Every experiment this user has been exposed to.</summary>
        /// <remarks>
        /// A conversion attributes to all of them. With layers, one purchase is evidence about the offer
        /// layout experiment and about the pricing experiment simultaneously, and counting it once for each
        /// is exactly right - they are separate questions asked of the same event.
        /// </remarks>
        public IReadOnlyCollection<ExposureRecord> RecordsFor(string userId)
        {
            if (userId == null || !_records.TryGetValue(userId, out var byExperiment))
            {
                return new ExposureRecord[0];
            }

            return new List<ExposureRecord>(byExperiment.Values);
        }

        /// <summary>True when the user has been exposed to more than one arm of this experiment.</summary>
        public bool IsContaminated(string userId, string experimentId) =>
            TryGetRecord(userId, experimentId, out var record) && record.IsContaminated;

        /// <summary>How many distinct arms of an experiment a user has been exposed to.</summary>
        /// <remarks>Used by the soak test, which asserts this is one unless a pin was invalidated.</remarks>
        public int DistinctArmsSeen(string userId, string experimentId)
        {
            if (userId == null || experimentId == null) return 0;
            if (!_armsSeen.TryGetValue(userId, out var byExperiment)) return 0;
            return byExperiment.TryGetValue(experimentId, out var arms) ? arms.Count : 0;
        }

        /// <summary>
        /// Releases the per-session deduplication state for a finished session. The attribution records are
        /// untouched.
        /// </summary>
        /// <remarks>
        /// The simulator calls this after each simulated user, so that simulating a hundred thousand users
        /// does not accumulate a hundred thousand sessions worth of dedup keys for visits that are over.
        /// </remarks>
        public int ForgetSession(SessionId session)
        {
            var doomed = new List<SessionExposureKey>();
            foreach (var key in _loggedThisSession)
            {
                if (key.Session.Equals(session)) doomed.Add(key);
            }

            for (int i = 0; i < doomed.Count; i++) _loggedThisSession.Remove(doomed[i]);
            return doomed.Count;
        }

        /// <summary>Forgets everything, including attribution.</summary>
        public void Clear()
        {
            _loggedThisSession.Clear();
            _records.Clear();
            _armsSeen.Clear();
            ContaminatedCount = 0;
        }

        /// <summary>Records the arm and reports whether it is a second distinct one.</summary>
        private bool NoteArm(string userId, string experimentId, string variantId)
        {
            if (!_armsSeen.TryGetValue(userId, out var byExperiment))
            {
                byExperiment = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                _armsSeen[userId] = byExperiment;
            }

            if (!byExperiment.TryGetValue(experimentId, out var arms))
            {
                arms = new HashSet<string>(StringComparer.Ordinal);
                byExperiment[experimentId] = arms;
            }

            bool isNewArm = arms.Add(variantId);
            return isNewArm && arms.Count > 1;
        }
    }
}
