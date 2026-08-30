using System;
using HotUpdateABTest.Core;

namespace HotUpdateABTest
{
    /// <summary>The real clock. Everything outside tests uses this one.</summary>
    /// <remarks>
    /// Deliberately backed by <see cref="DateTime.UtcNow"/> rather than <c>UnityEngine.Time</c>. Config
    /// polling and exposure timestamps are wall-clock concerns: they must keep running while the game is
    /// paused, must not be affected by <c>Time.timeScale</c>, and must survive being compared against a
    /// timestamp written by a previous session.
    /// </remarks>
    public sealed class SystemClock : IClock
    {
        /// <summary>A shared instance; the type holds no state.</summary>
        public static readonly SystemClock Instance = new SystemClock();

        /// <inheritdoc />
        public DateTime UtcNow => DateTime.UtcNow;
    }

    /// <summary>A clock the caller advances by hand. Used by tests and by the demo's time controls.</summary>
    /// <remarks>
    /// Lives beside the real clock rather than in the test assembly because the demo also needs it: the
    /// LiveOps panel can jump the poll interval forward without anybody waiting for it.
    /// </remarks>
    public sealed class ManualClock : IClock
    {
        /// <summary>Creates a clock reading the given instant.</summary>
        public ManualClock(DateTime startUtc)
        {
            UtcNow = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        }

        /// <summary>Creates a clock reading a fixed, arbitrary instant.</summary>
        public ManualClock() : this(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        {
        }

        /// <inheritdoc />
        public DateTime UtcNow { get; private set; }

        /// <summary>Moves the clock forward.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="delta"/> is negative.</exception>
        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(delta), "A clock does not run backwards.");
            }

            UtcNow += delta;
        }

        /// <summary>Moves the clock forward by a number of seconds.</summary>
        public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
    }
}
