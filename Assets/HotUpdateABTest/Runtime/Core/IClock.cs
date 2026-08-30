using System;

namespace HotUpdateABTest.Core
{
    /// <summary>
    /// The framework's only source of time, injected for the same reason as <see cref="IAbLog"/>: the core
    /// may not reach for <c>UnityEngine.Time</c>.
    /// </summary>
    /// <remarks>
    /// Config polling intervals, exposure timestamps and session boundaries all depend on time, and all of
    /// them need to be driven deterministically from tests. A test that has to wait a real second to prove
    /// a poll happened is a test that will eventually be deleted for being slow.
    /// </remarks>
    public interface IClock
    {
        /// <summary>The current instant, in UTC.</summary>
        DateTime UtcNow { get; }
    }
}
