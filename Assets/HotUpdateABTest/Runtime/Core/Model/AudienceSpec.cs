using System;
using System.Collections.Generic;

namespace HotUpdateABTest.Core.Model
{
    /// <summary>
    /// An optional filter narrowing an experiment to part of the population.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audience is applied <i>after</i> layer allocation, not before. A user's bucket is a property of the
    /// user and does not move because they failed a predicate, so an experiment's effective traffic is its
    /// allocation width multiplied by the share of users who match. That has a consequence worth writing
    /// down: the sample-ratio check in the telemetry layer must compare observed assignment against
    /// audience-filtered expectations, not against the raw allocation, or a perfectly healthy targeted
    /// experiment will look like it is losing users.
    /// </para>
    /// <para>
    /// Every clause is optional and an absent clause matches everyone; the clauses that are present are
    /// combined with AND. This stays intentionally simple. Rich predicate logic belongs in Lua, where it
    /// can be changed without a rebuild, and that arrives with the variant behaviour seam. What is here is
    /// the small set of attributes worth gating on before any Lua has loaded.
    /// </para>
    /// </remarks>
    public sealed class AudienceSpec
    {
        /// <summary>Matches every user. Used when an experiment declares no audience at all.</summary>
        public static readonly AudienceSpec Everyone = new AudienceSpec(null, null, null);

        private readonly string[] _platforms;
        private readonly string[] _countries;

        /// <summary>Minimum account level, inclusive, or null for no lower bound.</summary>
        public int? MinAccountLevel { get; }

        /// <summary>Allowed platform tags, or null for any platform.</summary>
        public IReadOnlyList<string> Platforms => _platforms;

        /// <summary>Allowed country codes, or null for any country.</summary>
        public IReadOnlyList<string> Countries => _countries;

        /// <summary>True when this spec excludes nobody.</summary>
        public bool IsEveryone => MinAccountLevel == null && _platforms == null && _countries == null;

        /// <summary>Creates an audience spec. A null clause means "do not filter on this".</summary>
        public AudienceSpec(int? minAccountLevel, IEnumerable<string> platforms, IEnumerable<string> countries)
        {
            MinAccountLevel = minAccountLevel;
            _platforms = ToArrayOrNull(platforms);
            _countries = ToArrayOrNull(countries);
        }

        /// <summary>True when <paramref name="user"/> qualifies.</summary>
        public bool Matches(UserContext user)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));

            if (MinAccountLevel != null && user.AccountLevel < MinAccountLevel.Value) return false;
            if (_platforms != null && !ContainsIgnoreCase(_platforms, user.Platform)) return false;
            if (_countries != null && !ContainsIgnoreCase(_countries, user.Country)) return false;

            return true;
        }

        /// <summary>Why <paramref name="user"/> does not qualify, or null when they do.</summary>
        /// <remarks>
        /// Separate from <see cref="Matches"/> so the debug panel can say "excluded: account level 2 is
        /// below 3" rather than only that the user is not in the experiment. A guardrail nobody can read
        /// on screen is a guardrail that gets blamed on the bucketing.
        /// </remarks>
        public string ExplainMismatch(UserContext user)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));

            if (MinAccountLevel != null && user.AccountLevel < MinAccountLevel.Value)
            {
                return "account level " + user.AccountLevel + " is below the minimum of " + MinAccountLevel.Value;
            }

            if (_platforms != null && !ContainsIgnoreCase(_platforms, user.Platform))
            {
                return "platform '" + user.Platform + "' is not in [" + string.Join(", ", _platforms) + "]";
            }

            if (_countries != null && !ContainsIgnoreCase(_countries, user.Country))
            {
                return "country '" + (user.Country ?? "unknown") + "' is not in [" +
                       string.Join(", ", _countries) + "]";
            }

            return null;
        }

        private static bool ContainsIgnoreCase(string[] allowed, string value)
        {
            if (value == null) return false;
            for (int i = 0; i < allowed.Length; i++)
            {
                if (string.Equals(allowed[i], value, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static string[] ToArrayOrNull(IEnumerable<string> values)
        {
            if (values == null) return null;
            var list = new List<string>(values);
            return list.ToArray();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            if (IsEveryone) return "everyone";

            var parts = new List<string>();
            if (MinAccountLevel != null) parts.Add("level >= " + MinAccountLevel.Value);
            if (_platforms != null) parts.Add("platform in [" + string.Join(", ", _platforms) + "]");
            if (_countries != null) parts.Add("country in [" + string.Join(", ", _countries) + "]");
            return string.Join(" and ", parts.ToArray());
        }
    }
}
