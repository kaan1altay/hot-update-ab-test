namespace HotUpdateABTest.Core.Model
{
    /// <summary>
    /// Evaluates a named audience predicate, which in this framework means running a Lua function.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An interface because the decision core may not reach for Lua any more than it may reach for
    /// UnityEngine: the predicate host lives in the engine-facing assembly, and resolution stays testable
    /// in the one-second CI suite without a native VM.
    /// </para>
    /// <para>
    /// <b>Implementations must fail closed.</b> A predicate that errors, returns a non-boolean, or is not
    /// registered at all returns false. Failing open would sweep users into a treatment nobody validated on
    /// the strength of a bug, and the experiment would then be measuring the bug rather than the treatment.
    /// Excluding them costs sample size, which is by a wide margin the cheaper mistake.
    /// </para>
    /// </remarks>
    public interface IAudiencePredicateEvaluator
    {
        /// <summary>True only when the predicate ran cleanly and returned true.</summary>
        bool Matches(string predicateKey, UserContext user);
    }
}
