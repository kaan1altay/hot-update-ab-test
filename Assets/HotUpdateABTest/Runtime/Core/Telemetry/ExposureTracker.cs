using System;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Telemetry
{
    /// <summary>
    /// Records the funnel: that a user was assigned an arm, and separately that they actually saw it.
    /// </summary>
    /// <remarks>
    /// <para><b>Assignment is not exposure, and the gap between them is the point.</b> Resolution is a pure
    /// function that may be called speculatively - to warm a screen, to render a diagnostic, to simulate a
    /// population - and it logs nothing on its own. An exposure is recorded only when the treated surface
    /// is actually in front of the user. Logging at assignment time instead would put users who never
    /// reached the shop into both arms' denominators, diluting measured lift toward zero and, worse,
    /// destroying the sample-ratio check as a diagnostic, because the ratio would then always match by
    /// construction.</para>
    ///
    /// <para><b>Assignments are still counted</b>, through <see cref="RecordAssignment"/>, but only as the
    /// denominator of the assignment-to-exposure funnel. That funnel is a health signal in its own right:
    /// a variant whose exposures collapse while its assignments hold steady is a variant that is failing to
    /// render, which is a bug that no amount of staring at conversion rates will reveal.</para>
    ///
    /// <para><b>Deduplication is per (user, experiment, variant, session).</b> Variant is in the key
    /// deliberately. If a user somehow flips arms mid-experiment, the key does not suppress the second
    /// exposure - it lets it through, flags it, and counts it as contamination. Swallowing the evidence
    /// would make the framework look tidier and the data quietly wrong.</para>
    ///
    /// <para>Exposure is also the moment a sticky assignment is pinned, which is why the tracker holds the
    /// resolver: the obligation not to move a user is created by their having seen something, not by an
    /// arithmetic result nobody looked at.</para>
    /// </remarks>
    public sealed class ExposureTracker
    {
        private readonly ExposureLedger _ledger;
        private readonly IAnalyticsSink _sink;
        private readonly IClock _clock;
        private readonly ExperimentResolver _resolver;

        /// <summary>How many exposures were logged.</summary>
        public long LoggedCount { get; private set; }

        /// <summary>How many exposures were suppressed because the session had already seen them.</summary>
        public long DeduplicatedCount { get; private set; }

        /// <summary>How many exposures put a user into a second arm of the same experiment.</summary>
        public long ContaminatingCount { get; private set; }

        /// <summary>The ledger this tracker writes to. Conversion attribution reads the same one.</summary>
        public ExposureLedger Ledger => _ledger;

        /// <summary>Creates a tracker.</summary>
        /// <param name="resolver">Optional. When supplied, a first exposure pins a sticky assignment.</param>
        public ExposureTracker(
            ExposureLedger ledger, IAnalyticsSink sink, IClock clock, ExperimentResolver resolver = null)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _resolver = resolver;
        }

        /// <summary>
        /// Records that a user was resolved into an arm. Not an exposure, and never deduplicated: this is
        /// the funnel denominator and it counts every time the surface was prepared.
        /// </summary>
        public void RecordAssignment(
            UserContext user, VariantAssignment assignment, SessionId session, bool synthetic = false)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (assignment == null) throw new ArgumentNullException(nameof(assignment));
            if (!assignment.IsAssigned) return;

            _sink.Record(new AnalyticsEvent(
                AnalyticsEventKind.Assignment,
                user.UserId,
                session,
                assignment.ExperimentId,
                assignment.VariantId,
                assignment.LayerId,
                null,
                TraitsOf(assignment, synthetic),
                assignment.ConfigVersion,
                _clock.UtcNow));
        }

        /// <summary>
        /// Records that the user has actually seen the treated surface. Returns true when an exposure was
        /// logged, false when this session had already seen this arm.
        /// </summary>
        public bool MarkExposed(
            UserContext user, VariantAssignment assignment, SessionId session, bool synthetic = false)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (assignment == null) throw new ArgumentNullException(nameof(assignment));

            // Nothing to be exposed to. A user in no experiment saw the untreated surface, which is not an
            // event - it is the absence of one.
            if (!assignment.IsAssigned) return false;

            if (!session.IsValid)
            {
                throw new ArgumentException(
                    "An exposure needs a session: deduplication is scoped to one, and an exposure without " +
                    "a session cannot be deduplicated at all.", nameof(session));
            }

            var traits = TraitsOf(assignment, synthetic);
            DateTime now = _clock.UtcNow;

            var outcome = _ledger.Offer(
                user.UserId, assignment.ExperimentId, assignment.VariantId, assignment.LayerId,
                session, traits, assignment.ConfigVersion, now);

            if (outcome == ExposureLedger.Outcome.Duplicate)
            {
                DeduplicatedCount++;
                return false;
            }

            if (outcome == ExposureLedger.Outcome.Contaminating) ContaminatingCount++;

            LoggedCount++;

            _sink.Record(new AnalyticsEvent(
                AnalyticsEventKind.Exposure,
                user.UserId,
                session,
                assignment.ExperimentId,
                assignment.VariantId,
                assignment.LayerId,
                null,
                traits,
                assignment.ConfigVersion,
                now));

            // Seeing it is what creates the obligation not to move them. A forced assignment is excluded
            // inside NotifyExposed, so a QA override never writes itself into the store.
            _resolver?.NotifyExposed(user, assignment, now);

            return true;
        }

        /// <summary>Releases per-session dedup state for a finished visit.</summary>
        public void ForgetSession(SessionId session) => _ledger.ForgetSession(session);

        private static EventTraits TraitsOf(VariantAssignment assignment, bool synthetic)
        {
            var traits = EventTraits.None;
            if (assignment.IsForced) traits |= EventTraits.Forced;
            if (synthetic) traits |= EventTraits.Synthetic;
            return traits;
        }
    }
}
