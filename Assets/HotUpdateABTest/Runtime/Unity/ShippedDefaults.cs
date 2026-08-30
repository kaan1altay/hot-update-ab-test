using System;
using System.IO;
using HotUpdateABTest.Core;
using UnityEngine;

namespace HotUpdateABTest
{
    /// <summary>
    /// Loads the configuration that ships inside the build, the floor of the fallback ladder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The artifact is a real config in which every experiment is declared but <c>stopped</c>, not an empty
    /// document. That distinction is the point: a screen that asks about an experiment on a first offline
    /// launch gets a definite "not running" and renders the control experience, rather than getting nothing
    /// and having to invent a policy locally. It also means the metrics panel has rows to draw before the
    /// first successful fetch.
    /// </para>
    /// <para>
    /// StreamingAssets is read differently per platform - on Android it lives inside the APK and needs
    /// UnityWebRequest, everywhere this project targets it is a plain file. The read is synchronous because
    /// it happens once at startup, before the first frame, and the file is a couple of kilobytes.
    /// </para>
    /// </remarks>
    public static class ShippedDefaults
    {
        /// <summary>Path relative to <see cref="Application.streamingAssetsPath"/>.</summary>
        public const string RelativePath = "abtest/default_config.json";

        /// <summary>Reads the shipped payload, or returns null when it cannot be read.</summary>
        /// <remarks>
        /// A null here is a packaging fault rather than a runtime condition, so it is logged as an error:
        /// the artifact that is supposed to be the last line of defence is missing from the build.
        /// </remarks>
        public static string Load(IAbLog log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            string path = Path.Combine(Application.streamingAssetsPath, RelativePath);

            try
            {
                if (File.Exists(path)) return File.ReadAllText(path);

                log.Log(AbLogLevel.Error,
                    "the shipped default configuration is missing from the build at " + path +
                    "; with no cache and no server the framework will run on an empty config");
                return null;
            }
            catch (Exception e)
            {
                log.Log(AbLogLevel.Error,
                    "could not read the shipped default configuration at " + path + ": " + e.Message);
                return null;
            }
        }
    }
}
