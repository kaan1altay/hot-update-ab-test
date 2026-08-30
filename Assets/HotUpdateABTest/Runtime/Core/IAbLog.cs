namespace HotUpdateABTest.Core
{
    /// <summary>Severity of a message written to <see cref="IAbLog"/>.</summary>
    public enum AbLogLevel
    {
        /// <summary>Routine progress: a config accepted, a patch loaded.</summary>
        Info,

        /// <summary>Something was rejected or fell back, and the framework carried on correctly.</summary>
        Warning,

        /// <summary>A failure the framework could not absorb.</summary>
        Error
    }

    /// <summary>
    /// The framework's only way to say something. Injected rather than called statically so the core stays
    /// free of <c>UnityEngine.Debug</c>.
    /// </summary>
    /// <remarks>
    /// The core is compiled twice: once by Unity, and once as a plain .NET library so its tests can run in
    /// CI without a Unity licence. That second compilation is what keeps the "the decision core has no
    /// engine dependency" claim honest, and it only works if nothing under <c>Core/</c> reaches for
    /// <c>UnityEngine</c>. Logging is the most tempting place to break that rule, so it is an interface.
    /// </remarks>
    public interface IAbLog
    {
        /// <summary>Writes one message.</summary>
        void Log(AbLogLevel level, string message);
    }
}
