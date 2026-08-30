using HotUpdateABTest.Core.Config;

namespace HotUpdateABTest.Core.Presentation
{
    /// <summary>
    /// Short, screen-sized names for the ways a presentation spec can be refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The full validation message goes to the log, where there is room for it and somebody can read a
    /// sentence. The debug strip on the shop screen gets one of these instead, because a viewer watching a
    /// recording needs to know <i>which class of thing</i> went wrong and has no time to read a sentence
    /// off a still frame.
    /// </para>
    /// <para>
    /// Derived from the machine-readable issue code rather than by matching on message text, so improving
    /// the wording of a validation message cannot silently change what the strip says.
    /// </para>
    /// </remarks>
    public static class SpecRejection
    {
        /// <summary>The behavior errored, or the environment could not run it.</summary>
        public const string LuaError = "lua error";

        /// <summary>No behavior is registered under the variant's key.</summary>
        public const string NoBehavior = "no behavior";

        /// <summary>The behavior returned something that is not a table.</summary>
        public const string NotATable = "not a table";

        /// <summary>The Lua environment is not running at all.</summary>
        public const string NoLua = "lua unavailable";

        /// <summary>A short token naming the first error in <paramref name="result"/>, or null.</summary>
        public static string Token(ValidationResult result)
        {
            if (result == null || result.IsValid) return null;

            foreach (var issue in result.Issues)
            {
                if (issue.Level != ValidationLevel.Error) continue;
                return TokenFor(issue.Code);
            }

            return "rejected";
        }

        /// <summary>Maps one issue code onto its token.</summary>
        public static string TokenFor(string code)
        {
            if (code == null) return "rejected";

            if (code == "spec.unknownField") return "unknown field";
            if (code == "spec.foreignField") return "foreign field";
            if (code == "spec.null") return "no table";

            if (code.EndsWith(".tooLong")) return "text too long";
            if (code.EndsWith(".unknown")) return "bad enum value";
            if (code.EndsWith(".notAString")) return "wrong type";
            if (code.EndsWith(".empty") || code.EndsWith(".null")) return "empty value";

            return "rejected";
        }
    }
}
