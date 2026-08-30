using System;
using System.Collections.Generic;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Telemetry
{
    /// <summary>What happened when a goal was reported.</summary>
    public sealed class ConversionResult
    {
        /// <summary>The experiments the conversion was credited to, and the arm in each.</summary>
        public IReadOnlyList<ExposureRecord> AttributedTo { get; }

        /// <summary>True when no exposure was on record and the conversion could not be attributed.</summary>
        public bool IsUnattributed => AttributedTo.Count == 0;

        /// <summary>Creates a result.</summary>
        public ConversionResult(IReadOnlyList<ExposureRecord> attributedTo)
        {
            AttributedTo = attributedTo ?? new ExposureRecord[0];
        }
    }

    /// <summary>
    /// Credits a goal to whatever the user was actually exposed to.
    /// </summary>
    /// <remarks>
    /// <para><b>Attribution reads the exposure record and never re-resolves.</b> This is the single rule
    /// that makes the numbers trustworthy. Asking the resolver "what arm is this user in" at conversion
    /// time returns the arm they are in <i>now</i>, and between the exposure and the purchase the config
    /// may well have moved: an operator ramped the weights, a kill switch fired, a pin was invalidated. Any
    /// of those would silently credit the outcome to an arm the user never saw, and the resulting report
    /// would look completely normal. Reading the ledger means attribution is a fact about history rather
    /// than a recomputation of a present that has changed.</para>
    ///
    /// <para><b>A conversion credits every experiment the user was exposed to.</b> With layers that is the
    /// only coherent answer: one purchase is evidence about the offer-layout experiment and about the
    /// pricing experiment at the same time, because they are separate questions asked of the same event.
    /// Crediting only one would leave the other blind to its own effect.</para>
    ///
    /// <para><b>An unattributed conversion is recorded, not dropped.</b> A goal reached by a user with no
    /// exposure on record is usually benign - they never opened the shop - but a sudden rise in them is how
    /// a broken exposure call announces itself. Dropping them would remove the only evidence that anything
    /// was wrong, so they are counted and surfaced in the aggregate rather than merely written somewhere.</para>
    /// </remarks>
    public sealed class ConversionTracker
    {
        private readonly ExposureLedger _ledger;
        private readonly IAnalyticsSink _sink;
        private readonly IClock _clock;

        /// <summary>How many conversions were credited to at least one experiment.</summary>
        public long AttributedCount { get; private set; }

        /// <summary>How many conversions had no exposure on record.</summary>
        public long UnattributedCount { get; private set; }

        /// <summary>Every conversion reported, attributed or not.</summary>
        public long TotalCount => AttributedCount + UnattributedCount;

        /// <summary>Creates a tracker reading from <paramref name="ledger"/>.</summary>
        public ConversionTracker(ExposureLedger ledger, IAnalyticsSink sink, IClock clock)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>Reports that <paramref name="user"/> reached <paramref name="goalId"/>.</summary>
        public ConversionResult Convert(
            UserContext user, SessionId session, string goalId, bool synthetic = false)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (goalId == null) throw new ArgumentNullException(nameof(goalId));

            var records = _ledger.RecordsFor(user.UserId);
            DateTime now = _clock.UtcNow;

            if (records.Count == 0)
            {
                UnattributedCount++;

                _sink.Record(new AnalyticsEvent(
                    AnalyticsEventKind.Conversion,
                    user.UserId,
                    session,
                    null,
                    null,
                    null,
                    goalId,
                    synthetic ? EventTraits.Synthetic : EventTraits.None,
                    null,
                    now));

                return new ConversionResult(new ExposureRecord[0]);
            }

            var attributed = new List<ExposureRecord>(records.Count);

            foreach (var record in records)
            {
                attributed.Add(record);

                // The traits come from the exposure, not from the conversion call. A conversion following a
                // forced exposure is itself tainted, and inheriting the flag is what keeps it out of the
                // headline numbers without the caller having to remember.
                var traits = record.Traits;
                if (synthetic) traits |= EventTraits.Synthetic;

                _sink.Record(new AnalyticsEvent(
                    AnalyticsEventKind.Conversion,
                    user.UserId,
                    session,
                    record.ExperimentId,
                    record.VariantId,
                    record.LayerId,
                    goalId,
                    traits,
                    record.ConfigVersion,
                    now));
            }

            AttributedCount++;
            return new ConversionResult(attributed);
        }
    }
}
