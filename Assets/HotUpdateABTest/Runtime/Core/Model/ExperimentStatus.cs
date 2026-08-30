namespace HotUpdateABTest.Core.Model
{
    /// <summary>
    /// Where an experiment is in its lifecycle. Only <see cref="Running"/> assigns anyone; every other
    /// value means the operator has taken it off the table and users must see control.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Paused"/> and <see cref="Stopped"/> both return every user to control, and the framework
    /// treats them identically at assignment time. They are separate values because they mean different
    /// things to the operator who set them, and because that intent is worth preserving in the config and
    /// in the logs: paused is "hold, I am about to resume", stopped is "this is over".
    /// </para>
    /// <para>
    /// <see cref="Draft"/> exists so an experiment can be written, reviewed and shipped in config before it
    /// is switched on. A draft claims no traffic and its allocation range does not conflict with anyone.
    /// </para>
    /// </remarks>
    public enum ExperimentStatus
    {
        /// <summary>Authored but never switched on. Claims no traffic.</summary>
        Draft,

        /// <summary>Live. This is the only status that assigns users to variants.</summary>
        Running,

        /// <summary>Temporarily halted. Everyone sees control on the next config refresh.</summary>
        Paused,

        /// <summary>Permanently halted. Everyone sees control on the next config refresh.</summary>
        Stopped
    }
}
