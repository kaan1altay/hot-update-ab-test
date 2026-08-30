using System;

namespace HotUpdateABTest.Core.Telemetry
{
    /// <summary>
    /// Identifies one session. Exposure deduplication is scoped to this, so it has to be a real value with
    /// a defined lifetime rather than an implicit "since the app started".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session begins when the app starts, and again whenever the app returns to the foreground after
    /// being away longer than <see cref="SessionTracker.DefaultIdleTimeout"/>. It ends when the app quits or
    /// when that timeout elapses in the background. Thirty minutes is the convention every mobile analytics
    /// product uses, and matching it means the exposure counts here mean the same thing as the ones in
    /// whatever the studio already runs.
    /// </para>
    /// <para>
    /// Why exposure dedup is per session rather than per lifetime: the question an experiment asks is "how
    /// many users saw the treatment", and the natural unit for that is the visit. Deduplicating forever
    /// would make a user who opened the shop every day for a week indistinguishable from one who opened it
    /// once, and would quietly turn the exposure count into a first-seen count. Not deduplicating at all
    /// would let a user who reopens the same screen twenty times in one visit dominate the arm.
    /// </para>
    /// <para>
    /// Simulated users each get their own session. Without that, "simulate 5000 users" would collapse into
    /// one session, dedup would treat it as a single visit, and the funnel would be nonsense.
    /// </para>
    /// </remarks>
    public readonly struct SessionId : IEquatable<SessionId>
    {
        /// <summary>The session that is not a session. Events carrying it are a programming error.</summary>
        public static readonly SessionId None = default;

        /// <summary>The opaque identifier.</summary>
        public string Value { get; }

        /// <summary>True when this identifies an actual session.</summary>
        public bool IsValid => !string.IsNullOrEmpty(Value);

        /// <summary>Wraps an existing identifier.</summary>
        public SessionId(string value)
        {
            Value = value;
        }

        /// <summary>
        /// A session for a real player, identified by when it started and a per-process sequence number.
        /// </summary>
        /// <remarks>
        /// Deliberately not a GUID. The core has no random source - it may not touch
        /// <c>UnityEngine.Random</c>, and <c>Guid.NewGuid</c> would make every test run produce different
        /// event data for no benefit. A timestamp plus a counter is unique within a process, which is all
        /// the scope dedup needs, and it reads sensibly in a log.
        /// </remarks>
        public static SessionId ForPlayer(DateTime startedUtc, int sequence) =>
            new SessionId("s" + startedUtc.Ticks.ToString("x") + "-" + sequence.ToString("x"));

        /// <summary>A session belonging to one simulated user.</summary>
        /// <remarks>
        /// Distinct per simulated user, and distinct per simulation run, so pressing "simulate 5000 users"
        /// twice produces ten thousand visits rather than five thousand deduplicated ones.
        /// </remarks>
        public static SessionId ForSimulatedUser(string userId, int run) =>
            new SessionId("sim" + run.ToString("x") + "-" + userId);

        /// <inheritdoc />
        public bool Equals(SessionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is SessionId other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);

        /// <inheritdoc />
        public override string ToString() => Value ?? "(none)";
    }

    /// <summary>
    /// Owns the current session: starts one, ends it after enough idle time, and starts a new one on the
    /// next activity.
    /// </summary>
    /// <remarks>
    /// Driven by <see cref="IClock"/> rather than by a coroutine, so a test can move time forward and
    /// assert that the session rolled over without waiting thirty real minutes.
    /// </remarks>
    public sealed class SessionTracker
    {
        /// <summary>How long the app may be idle before the next activity starts a new session.</summary>
        public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

        private readonly IClock _clock;
        private readonly TimeSpan _idleTimeout;

        private DateTime _lastActivityUtc;
        private int _sequence;

        /// <summary>The session in force.</summary>
        public SessionId Current { get; private set; }

        /// <summary>When the current session began.</summary>
        public DateTime StartedUtc { get; private set; }

        /// <summary>How many sessions have begun in this process.</summary>
        public int SessionCount => _sequence;

        /// <summary>Creates a tracker and starts its first session immediately.</summary>
        public SessionTracker(IClock clock, TimeSpan? idleTimeout = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
            Begin();
        }

        /// <summary>Raised when a new session begins, with the session that just ended.</summary>
        public event Action<SessionId, SessionId> SessionRolled;

        /// <summary>
        /// Records that the app is active, rolling the session over first if it has been idle too long.
        /// Returns the session now in force.
        /// </summary>
        public SessionId Touch()
        {
            DateTime now = _clock.UtcNow;

            if (now - _lastActivityUtc >= _idleTimeout)
            {
                var previous = Current;
                Begin();
                SessionRolled?.Invoke(previous, Current);
            }
            else
            {
                _lastActivityUtc = now;
            }

            return Current;
        }

        /// <summary>Ends the current session and begins a new one, whatever the idle time.</summary>
        /// <remarks>The demo's "new session" button, and what a real app would call on resume from a cold
        /// background.</remarks>
        public SessionId Roll()
        {
            var previous = Current;
            Begin();
            SessionRolled?.Invoke(previous, Current);
            return Current;
        }

        private void Begin()
        {
            _sequence++;
            StartedUtc = _clock.UtcNow;
            _lastActivityUtc = StartedUtc;
            Current = SessionId.ForPlayer(StartedUtc, _sequence);
        }
    }
}
