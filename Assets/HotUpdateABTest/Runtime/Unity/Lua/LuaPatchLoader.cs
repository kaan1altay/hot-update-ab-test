using System;
using System.Collections.Generic;
using System.IO;
using HotUpdateABTest.Core;
using UnityEngine;

namespace HotUpdateABTest.Lua
{
    /// <summary>One Lua file found on disk, ready to be handed to the sandbox.</summary>
    public readonly struct LuaSourceFile
    {
        /// <summary>Absolute path. Also the chunk name, so stack traces point at a real file.</summary>
        public string Path { get; }

        /// <summary>The source text.</summary>
        public string Source { get; }

        /// <summary>True when this came from the hot-update patch root rather than the shipped baseline.</summary>
        public bool IsPatch { get; }

        /// <summary>Creates a source file.</summary>
        public LuaSourceFile(string path, string source, bool isPatch)
        {
            Path = path;
            Source = source;
            IsPatch = isPatch;
        }

        /// <summary>Short name for logs.</summary>
        public string Name => System.IO.Path.GetFileName(Path);
    }

    /// <summary>
    /// Finds the Lua that should be loaded, in the order it should be loaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two roots. The shipped baseline lives in <c>StreamingAssets/abtest/lua/variants</c> and defines
    /// every variant the build knows about. The patch root lives under
    /// <see cref="Application.persistentDataPath"/> and is where a hot update lands - dropping a file there
    /// and hitting reload is the whole delivery mechanism.
    /// </para>
    /// <para>
    /// Baseline first, patches second, and later registrations win. That ordering is what lets a patch both
    /// add a new variant and change what an existing one does, without needing two mechanisms.
    /// </para>
    /// <para>
    /// Files within a root load in sorted order, so a patch set behaves the same on every machine rather
    /// than depending on whatever order the filesystem happens to enumerate.
    /// </para>
    /// <para>
    /// Note there is no <c>require</c> anywhere in this design. C# reads the files and hands the source to a
    /// sandboxed <c>load</c>; patches cannot pull in modules of their own choosing. A filtered <c>require</c>
    /// would have been the conventional approach and a materially weaker one.
    /// </para>
    /// </remarks>
    public sealed class LuaPatchLoader
    {
        /// <summary>Where the shipped Lua lives, relative to StreamingAssets.</summary>
        public const string BaselineRelativePath = "abtest/lua";

        /// <summary>Where hot updates land, relative to persistentDataPath.</summary>
        public const string PatchFolderName = "abtest-patches";

        private readonly IAbLog _log;

        /// <summary>The folder holding the shipped baseline variants.</summary>
        public string BaselineRoot { get; }

        /// <summary>The folder hot updates are dropped into.</summary>
        public string PatchRoot { get; }

        /// <summary>The bootstrap file, loaded before anything else and never patchable.</summary>
        public string BootstrapPath { get; }

        /// <summary>Creates a loader over explicit roots. Tests point these at temporary folders.</summary>
        public LuaPatchLoader(string baselineRoot, string patchRoot, IAbLog log)
        {
            BaselineRoot = baselineRoot ?? throw new ArgumentNullException(nameof(baselineRoot));
            PatchRoot = patchRoot ?? throw new ArgumentNullException(nameof(patchRoot));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            BootstrapPath = Path.Combine(BaselineRoot, "bootstrap.lua");
        }

        /// <summary>Creates a loader over the standard Unity locations.</summary>
        public static LuaPatchLoader Default(IAbLog log) =>
            new LuaPatchLoader(
                Path.Combine(Application.streamingAssetsPath, BaselineRelativePath),
                Path.Combine(Application.persistentDataPath, PatchFolderName),
                log);

        /// <summary>Reads the bootstrap source, or returns null when it cannot be read.</summary>
        /// <remarks>
        /// The bootstrap defines the sandbox, so it is deliberately outside the patch root: a hot update
        /// that could replace it could rewrite the rules it is supposed to obey.
        /// </remarks>
        public string ReadBootstrap()
        {
            try
            {
                if (File.Exists(BootstrapPath)) return File.ReadAllText(BootstrapPath);

                _log.Log(AbLogLevel.Error,
                    "the Lua bootstrap is missing from the build at " + BootstrapPath +
                    "; no variant behavior can run");
                return null;
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Error, "could not read the Lua bootstrap at " + BootstrapPath + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Every behavior file to load, baseline first then patches, sorted within each root.</summary>
        public List<LuaSourceFile> Discover()
        {
            var files = new List<LuaSourceFile>();

            ReadFolder(Path.Combine(BaselineRoot, "variants"), isPatch: false, files);
            ReadFolder(PatchRoot, isPatch: true, files);

            return files;
        }

        /// <summary>Creates the patch folder if it is not there, so the demo can point a user at it.</summary>
        public void EnsurePatchRoot()
        {
            try
            {
                Directory.CreateDirectory(PatchRoot);
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Warning, "could not create the patch folder " + PatchRoot + ": " + e.Message);
            }
        }

        private void ReadFolder(string folder, bool isPatch, List<LuaSourceFile> into)
        {
            try
            {
                if (!Directory.Exists(folder)) return;

                var paths = Directory.GetFiles(folder, "*.lua", SearchOption.TopDirectoryOnly);
                Array.Sort(paths, StringComparer.Ordinal);

                for (int i = 0; i < paths.Length; i++)
                {
                    try
                    {
                        into.Add(new LuaSourceFile(paths[i], File.ReadAllText(paths[i]), isPatch));
                    }
                    catch (Exception e)
                    {
                        // One unreadable file must not stop the others being loaded, for the same reason a
                        // patch that throws must not take the registry down.
                        _log.Log(AbLogLevel.Warning,
                            "could not read " + paths[i] + ", skipping it: " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Warning, "could not list " + folder + ": " + e.Message);
            }
        }
    }
}
