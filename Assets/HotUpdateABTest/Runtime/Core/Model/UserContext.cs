using System;
using System.Collections.Generic;

namespace HotUpdateABTest.Core.Model
{
    /// <summary>
    /// Everything the framework knows about the user it is resolving for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately small and deliberately flat. This is the value that will later be handed across the
    /// C#/Lua boundary as an immutable context table, so every field here is one a variant behaviour or an
    /// audience predicate is allowed to see. Adding a field is therefore a decision about what a hot
    /// update can reach, not just a convenience - which is why there is a <see cref="Custom"/> bag for
    /// game-specific attributes rather than a growing list of properties.
    /// </para>
    /// <para>
    /// <see cref="UserId"/> is the only field that affects bucketing. The rest affect audience matching
    /// only, which means a user whose level or country changes keeps their variant: their bucket did not
    /// move. Whether they still <i>qualify</i> for the experiment can change, and that asymmetry is worth
    /// knowing about - see <c>ExperimentResolver</c>.
    /// </para>
    /// </remarks>
    public sealed class UserContext
    {
        private static readonly Dictionary<string, string> NoCustom = new Dictionary<string, string>();

        /// <summary>Stable identifier. The only input to bucketing.</summary>
        public string UserId { get; }

        /// <summary>Account level, or zero when the game does not have the concept.</summary>
        public int AccountLevel { get; }

        /// <summary>Platform tag, lower case, for example <c>editor</c>, <c>windows</c>, <c>ios</c>.</summary>
        public string Platform { get; }

        /// <summary>Two-letter country code, upper case, or null when unknown.</summary>
        public string Country { get; }

        /// <summary>Game-specific attributes an audience predicate may read.</summary>
        public IReadOnlyDictionary<string, string> Custom { get; }

        /// <summary>Creates a user context.</summary>
        public UserContext(
            string userId,
            int accountLevel = 0,
            string platform = "unknown",
            string country = null,
            IReadOnlyDictionary<string, string> custom = null)
        {
            UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            AccountLevel = accountLevel;
            Platform = platform ?? "unknown";
            Country = country;
            Custom = custom ?? NoCustom;
        }

        /// <inheritdoc />
        public override string ToString() =>
            UserId + " (level " + AccountLevel + ", " + Platform + (Country == null ? "" : ", " + Country) + ")";
    }
}
