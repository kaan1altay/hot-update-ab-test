using System;
using System.Collections.Generic;

namespace HotUpdateABTest.Core.Telemetry
{
    /// <summary>What kind of thing happened.</summary>
    public enum AnalyticsEventKind
    {
        /// <summary>
        /// A user was resolved into an arm. Free and silent as far as resolution is concerned - this is
        /// recorded only because it is the denominator of the assignment-to-exposure funnel.
        /// </summary>
        Assignment,

        /// <summary>A user actually saw the treated surface. The event analysis rests on.</summary>
        Exposure,

        /// <summary>A goal was reached, attributed to whatever the user was exposed to.</summary>
        Conversion
    }

    /// <summary>
    /// Properties of an event that decide which populations it belongs to.
    /// </summary>
    /// <remarks>
    /// Flags rather than booleans scattered through the aggregator, so that every metric can name the
    /// population it was computed over instead of applying whatever conditions its author happened to
    /// remember. See <see cref="MetricsPopulation"/>.
    /// </remarks>
    [Flags]
    public enum EventTraits
    {
        /// <summary>Ordinary traffic from a real user with a bucketed or pinned assignment.</summary>
        None = 0,

        /// <summary>
        /// Produced while a QA override was in force. A forced session is a deliberate violation of the
        /// assignment ratio, so counting it would manufacture the very alarm the ratio check exists to
        /// raise.
        /// </summary>
        Forced = 1 << 0,

        /// <summary>
        /// Produced by the demo's user simulator rather than by somebody playing. Kept in the headline
        /// numbers, because it is the only traffic this demo has and a real deployment would never generate
        /// it - but flagged, so a reader can tell what they are looking at.
        /// </summary>
        Synthetic = 1 << 1
    }

    /// <summary>One recorded event.</summary>
    /// <remarks>
    /// Immutable, and carries everything needed to interpret it later without asking anything else. In
    /// particular it carries the config version and the assignment source: a row that cannot say which
    /// configuration produced it is not much use when the question is whether a config change broke
    /// something.
    /// </remarks>
    public sealed class AnalyticsEvent
    {
        /// <summary>What happened.</summary>
        public AnalyticsEventKind Kind { get; }

        /// <summary>Who it happened to.</summary>
        public string UserId { get; }

        /// <summary>Which visit it happened in.</summary>
        public SessionId Session { get; }

        /// <summary>The experiment, or null on an unattributed conversion.</summary>
        public string ExperimentId { get; }

        /// <summary>The arm, or null on an unattributed conversion.</summary>
        public string VariantId { get; }

        /// <summary>The layer, or null on an unattributed conversion.</summary>
        public string LayerId { get; }

        /// <summary>The goal that was reached, on a conversion. Null otherwise.</summary>
        public string GoalId { get; }

        /// <summary>Which populations this event belongs to.</summary>
        public EventTraits Traits { get; }

        /// <summary>The config version in force when this was recorded.</summary>
        public string ConfigVersion { get; }

        /// <summary>When it happened.</summary>
        public DateTime TimestampUtc { get; }

        /// <summary>True when this is a conversion that could not be attributed to any experiment.</summary>
        public bool IsUnattributed => Kind == AnalyticsEventKind.Conversion && ExperimentId == null;

        /// <summary>True when a QA override produced this.</summary>
        public bool IsForced => (Traits & EventTraits.Forced) != 0;

        /// <summary>True when the simulator produced this.</summary>
        public bool IsSynthetic => (Traits & EventTraits.Synthetic) != 0;

        /// <summary>Creates an event.</summary>
        public AnalyticsEvent(
            AnalyticsEventKind kind,
            string userId,
            SessionId session,
            string experimentId,
            string variantId,
            string layerId,
            string goalId,
            EventTraits traits,
            string configVersion,
            DateTime timestampUtc)
        {
            Kind = kind;
            UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            Session = session;
            ExperimentId = experimentId;
            VariantId = variantId;
            LayerId = layerId;
            GoalId = goalId;
            Traits = traits;
            ConfigVersion = configVersion;
            TimestampUtc = timestampUtc;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            string where = ExperimentId == null ? "(unattributed)" : ExperimentId + "/" + VariantId;
            string flags = Traits == EventTraits.None ? "" : " [" + Traits.ToString().ToLowerInvariant() + "]";
            string goal = GoalId == null ? "" : " goal=" + GoalId;
            return Kind.ToString().ToLowerInvariant() + " " + UserId + " " + where + goal + flags;
        }
    }

    /// <summary>Somewhere events go.</summary>
    /// <remarks>
    /// The demo's sink is local and in memory - there is no network anywhere in this repository. A real
    /// game would put its own analytics client behind this interface, which is the point of it being one.
    /// </remarks>
    public interface IAnalyticsSink
    {
        /// <summary>Records one event. Must not throw.</summary>
        void Record(AnalyticsEvent analyticsEvent);
    }

    /// <summary>Fans one event out to several sinks.</summary>
    /// <remarks>
    /// Used to send every event both to the raw event log, which the log panel reads, and to the
    /// incremental aggregator, which the metrics panel reads. Keeping those separate means the aggregate is
    /// maintained in constant time per event while the raw log stays a raw log.
    /// </remarks>
    public sealed class CompositeAnalyticsSink : IAnalyticsSink
    {
        private readonly IAnalyticsSink[] _sinks;

        /// <summary>Creates a fan-out over the given sinks.</summary>
        public CompositeAnalyticsSink(params IAnalyticsSink[] sinks)
        {
            _sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));
        }

        /// <inheritdoc />
        public void Record(AnalyticsEvent analyticsEvent)
        {
            for (int i = 0; i < _sinks.Length; i++) _sinks[i]?.Record(analyticsEvent);
        }
    }

    /// <summary>Keeps the most recent events in memory.</summary>
    /// <remarks>
    /// <para>
    /// Bounded on purpose. The demo can fire thousands of events per click, and an unbounded list would
    /// grow without limit across a long session on camera. Oldest events are dropped and counted, so the
    /// log panel can say so rather than quietly showing a truncated history.
    /// </para>
    /// <para>
    /// Dropping raw events does not disturb the metrics: <see cref="MetricsAggregator"/> keeps its own
    /// counters and never reads this back.
    /// </para>
    /// </remarks>
    public sealed class InMemoryAnalyticsSink : IAnalyticsSink
    {
        /// <summary>How many events are kept by default.</summary>
        public const int DefaultCapacity = 50000;

        private readonly Queue<AnalyticsEvent> _events;
        private readonly int _capacity;

        /// <summary>How many events were dropped to stay within capacity.</summary>
        public long DroppedCount { get; private set; }

        /// <summary>How many events are currently held.</summary>
        public int Count => _events.Count;

        /// <summary>How many events have ever been recorded, including dropped ones.</summary>
        public long TotalRecorded { get; private set; }

        private readonly long[] _recordedByKind = new long[3];

        /// <summary>How many events of one kind have ever been recorded, including dropped ones.</summary>
        /// <remarks>
        /// Maintained incrementally rather than counted on demand. Callers that ask often - the log panel
        /// every frame, the soak test after every operation - would otherwise turn a constant-time question
        /// into a scan of the whole history, which is quadratic over a long run.
        /// </remarks>
        public long TotalRecordedOf(AnalyticsEventKind kind) => _recordedByKind[(int)kind];

        /// <summary>Creates a sink.</summary>
        public InMemoryAnalyticsSink(int capacity = DefaultCapacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _events = new Queue<AnalyticsEvent>();
        }

        /// <inheritdoc />
        public void Record(AnalyticsEvent analyticsEvent)
        {
            if (analyticsEvent == null) return;

            _events.Enqueue(analyticsEvent);
            TotalRecorded++;
            _recordedByKind[(int)analyticsEvent.Kind]++;

            while (_events.Count > _capacity)
            {
                _events.Dequeue();
                DroppedCount++;
            }
        }

        /// <summary>Every event still held, oldest first.</summary>
        public IReadOnlyList<AnalyticsEvent> Events => new List<AnalyticsEvent>(_events);

        /// <summary>Every held event of one kind, oldest first.</summary>
        public List<AnalyticsEvent> OfKind(AnalyticsEventKind kind)
        {
            var result = new List<AnalyticsEvent>();
            foreach (var e in _events)
            {
                if (e.Kind == kind) result.Add(e);
            }

            return result;
        }

        /// <summary>How many held events are of one kind.</summary>
        public int CountOf(AnalyticsEventKind kind)
        {
            int count = 0;
            foreach (var e in _events)
            {
                if (e.Kind == kind) count++;
            }

            return count;
        }

        /// <summary>Forgets everything.</summary>
        public void Clear()
        {
            _events.Clear();
            DroppedCount = 0;
            TotalRecorded = 0;
            for (int i = 0; i < _recordedByKind.Length; i++) _recordedByKind[i] = 0;
        }
    }
}
