using System;
using System.IO;
using HotUpdateABTest.Core;
using HotUpdateABTest.Core.Config;
using UnityEngine;

namespace HotUpdateABTest
{
    /// <summary>
    /// Keeps the last accepted payload on disk so a cold start with no network still gets real
    /// configuration rather than the shipped defaults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writes go through a temporary file and a replace, so a process killed mid-write leaves either the
    /// previous cache or the new one, never half of either. A truncated cache would be read back as a
    /// malformed payload on the next launch - survivable, since it is discarded and the ladder falls to the
    /// shipped defaults, but it would throw away a perfectly good configuration for no reason.
    /// </para>
    /// <para>
    /// Every operation swallows its exceptions and reports through <see cref="IAbLog"/>. Persisting the
    /// last known good is a convenience: failing to write it costs the next cold start, and failing to read
    /// it costs this one, but neither is worth failing a session over.
    /// </para>
    /// </remarks>
    public sealed class FileConfigCache : IConfigCache
    {
        private readonly string _path;
        private readonly IAbLog _log;

        /// <summary>Where the cache file lives.</summary>
        public string Path => _path;

        /// <summary>Creates a cache at <paramref name="path"/>.</summary>
        public FileConfigCache(string path, IAbLog log)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Creates a cache under <see cref="Application.persistentDataPath"/>.</summary>
        public static FileConfigCache Default(IAbLog log) =>
            new FileConfigCache(
                System.IO.Path.Combine(Application.persistentDataPath, "abtest", "last-known-good.json"),
                log);

        /// <inheritdoc />
        public string Read()
        {
            try
            {
                return File.Exists(_path) ? File.ReadAllText(_path) : null;
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Warning, "could not read the config cache at " + _path + ": " + e.Message);
                return null;
            }
        }

        /// <inheritdoc />
        public void Write(string payload)
        {
            if (payload == null) return;

            try
            {
                string directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, payload);

                // File.Replace needs the destination to exist; on a first write there is nothing to
                // replace, so a plain move is both correct and equally atomic.
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Warning, "could not write the config cache at " + _path + ": " + e.Message);
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Warning, "could not clear the config cache at " + _path + ": " + e.Message);
            }
        }
    }
}
