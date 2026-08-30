using System;
using HotUpdateABTest.Core;
using UnityEngine;

namespace HotUpdateABTest
{
    /// <summary>
    /// Routes framework messages to the Unity console, and optionally to a second listener such as the
    /// demo's on-screen log panel.
    /// </summary>
    /// <remarks>
    /// This is the Unity half of <see cref="IAbLog"/>. It lives outside <c>Runtime/Core</c> because it
    /// touches <see cref="Debug"/>, and the core is compiled a second time as a plain .NET library where
    /// that type does not exist.
    /// </remarks>
    public sealed class UnityAbLog : IAbLog
    {
        private readonly string _prefix;
        private readonly Action<AbLogLevel, string> _listener;

        /// <summary>Creates a log writer.</summary>
        /// <param name="prefix">Bracketed tag put in front of every message, for console filtering.</param>
        /// <param name="listener">Optional second destination, called after the console write.</param>
        public UnityAbLog(string prefix = "ABTest", Action<AbLogLevel, string> listener = null)
        {
            _prefix = "[" + (prefix ?? "ABTest") + "] ";
            _listener = listener;
        }

        /// <inheritdoc />
        public void Log(AbLogLevel level, string message)
        {
            string line = _prefix + message;

            switch (level)
            {
                case AbLogLevel.Error:
                    Debug.LogError(line);
                    break;
                case AbLogLevel.Warning:
                    Debug.LogWarning(line);
                    break;
                default:
                    Debug.Log(line);
                    break;
            }

            // A listener that throws must not take the caller down with it: logging is on the failure path
            // of nearly everything here, and a log write is never the operation the caller cared about.
            if (_listener == null) return;
            try
            {
                _listener(level, message);
            }
            catch (Exception e)
            {
                Debug.LogError(_prefix + "log listener threw and was ignored: " + e.Message);
            }
        }
    }
}
