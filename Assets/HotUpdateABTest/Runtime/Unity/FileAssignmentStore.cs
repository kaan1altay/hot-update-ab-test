using System;
using System.Collections.Generic;
using System.IO;
using HotUpdateABTest.Core;
using HotUpdateABTest.Core.Assignment;
using Newtonsoft.Json;
using UnityEngine;

namespace HotUpdateABTest
{
    /// <summary>
    /// An assignment store that survives a restart, so a user who was exposed yesterday still sees the
    /// same arm today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without persistence the sticky-after-exposure policy would only hold within a session, which is
    /// most of the way to not holding at all: the reshuffle it exists to prevent would simply happen on the
    /// next launch instead of on the next weight change.
    /// </para>
    /// <para>
    /// Everything is held in memory and the file is written behind it, because resolution happens on the
    /// hot path and must not touch the disk. Writes are debounced by an explicit <see cref="Flush"/> rather
    /// than happening per pin: exposures arrive in bursts when a screen opens, and a file write per
    /// exposure would be the most expensive thing in the frame. The caller flushes at a natural boundary -
    /// screen close, application pause, quit.
    /// </para>
    /// <para>
    /// A corrupt or unreadable file is discarded rather than repaired. Losing pins costs some users a
    /// rebucket, which is regrettable; running on half-parsed pins would mean applying arms that may not
    /// exist in the current config, which is worse.
    /// </para>
    /// </remarks>
    public sealed class FileAssignmentStore : IAssignmentStore
    {
        [Serializable]
        private sealed class PersistedPin
        {
            public string user;
            public string experiment;
            public string variant;
            public long pinnedUtcTicks;
            public string configVersion;
        }

        private readonly InMemoryAssignmentStore _memory = new InMemoryAssignmentStore();
        private readonly string _path;
        private readonly IAbLog _log;

        private bool _dirty;

        /// <summary>Where the pins are kept.</summary>
        public string Path => _path;

        /// <summary>True when there are unsaved changes.</summary>
        public bool HasUnsavedChanges => _dirty;

        /// <summary>Creates a store at <paramref name="path"/> and loads whatever is there.</summary>
        public FileAssignmentStore(string path, IAbLog log)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            Load();
        }

        /// <summary>Creates a store under <see cref="Application.persistentDataPath"/>.</summary>
        public static FileAssignmentStore Default(IAbLog log) =>
            new FileAssignmentStore(
                System.IO.Path.Combine(Application.persistentDataPath, "abtest", "assignments.json"), log);

        /// <inheritdoc />
        public IReadOnlyCollection<string> PinnedExperimentIds => _memory.PinnedExperimentIds;

        /// <inheritdoc />
        public int Count => _memory.Count;

        /// <inheritdoc />
        public bool TryGet(string userId, string experimentId, out AssignmentPin pin) =>
            _memory.TryGet(userId, experimentId, out pin);

        /// <inheritdoc />
        public IEnumerable<KeyValuePair<string, AssignmentPin>> PinsFor(string experimentId) =>
            _memory.PinsFor(experimentId);

        /// <inheritdoc />
        public void Set(string userId, AssignmentPin pin)
        {
            _memory.Set(userId, pin);
            _dirty = true;
        }

        /// <inheritdoc />
        public bool Remove(string userId, string experimentId)
        {
            bool removed = _memory.Remove(userId, experimentId);
            _dirty |= removed;
            return removed;
        }

        /// <inheritdoc />
        public int RemoveExperiment(string experimentId)
        {
            int removed = _memory.RemoveExperiment(experimentId);
            _dirty |= removed > 0;
            return removed;
        }

        /// <inheritdoc />
        public void Clear()
        {
            _memory.Clear();
            _dirty = true;
        }

        /// <summary>Writes pending changes to disk. Does nothing when there are none.</summary>
        public void Flush()
        {
            if (!_dirty) return;

            try
            {
                var rows = new List<PersistedPin>();
                foreach (string experimentId in _memory.PinnedExperimentIds)
                {
                    foreach (var pair in _memory.PinsFor(experimentId))
                    {
                        rows.Add(new PersistedPin
                        {
                            user = pair.Key,
                            experiment = pair.Value.ExperimentId,
                            variant = pair.Value.VariantId,
                            pinnedUtcTicks = pair.Value.PinnedUtc.Ticks,
                            configVersion = pair.Value.ConfigVersion
                        });
                    }
                }

                string directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(rows));

                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);

                _dirty = false;
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Warning, "could not save assignments to " + _path + ": " + e.Message);
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;

                var rows = JsonConvert.DeserializeObject<List<PersistedPin>>(File.ReadAllText(_path));
                if (rows == null) return;

                foreach (var row in rows)
                {
                    if (string.IsNullOrEmpty(row?.user) ||
                        string.IsNullOrEmpty(row.experiment) ||
                        string.IsNullOrEmpty(row.variant))
                    {
                        continue;
                    }

                    _memory.Set(row.user, new AssignmentPin(
                        row.experiment,
                        row.variant,
                        new DateTime(row.pinnedUtcTicks, DateTimeKind.Utc),
                        row.configVersion));
                }
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Warning,
                    "the saved assignments at " + _path + " could not be read and have been discarded (" +
                    e.Message + "); affected users will be re-bucketed");
                _memory.Clear();
            }
        }
    }
}
