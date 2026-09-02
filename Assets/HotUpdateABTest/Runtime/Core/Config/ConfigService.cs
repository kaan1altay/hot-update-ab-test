using System;
using System.Collections.Generic;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Model;

namespace HotUpdateABTest.Core.Config
{
    /// <summary>What happened to a payload handed to <see cref="ConfigService.Apply"/>.</summary>
    public enum ConfigApplyOutcome
    {
        /// <summary>Read, validated and published. The snapshot changed.</summary>
        Accepted,

        /// <summary>Identical to what is already in force. Nothing was parsed and nothing changed.</summary>
        Unchanged,

        /// <summary>
        /// A different payload arrived under a version label already in force. Ignored, because the
        /// version is the payload's identity and honouring the change would make the client and the server
        /// disagree about what version 7 means.
        /// </summary>
        ContentDriftIgnored,

        /// <summary>Rejected. The previous snapshot is untouched.</summary>
        Rejected
    }

    /// <summary>The outcome of one apply, with everything needed to explain it.</summary>
    public sealed class ConfigApplyResult
    {
        /// <summary>What happened.</summary>
        public ConfigApplyOutcome Outcome { get; }

        /// <summary>The snapshot in force after the call. Never null.</summary>
        public ConfigSnapshot Snapshot { get; }

        /// <summary>Findings from reading and validating, when the payload got that far.</summary>
        public ValidationResult Issues { get; }

        /// <summary>True when the configuration in force changed as a result of this call.</summary>
        public bool Changed => Outcome == ConfigApplyOutcome.Accepted;

        /// <summary>Creates a result.</summary>
        public ConfigApplyResult(ConfigApplyOutcome outcome, ConfigSnapshot snapshot, ValidationResult issues)
        {
            Outcome = outcome;
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Issues = issues ?? ValidationResult.Ok;
        }
    }

    /// <summary>Somewhere the last accepted payload is kept between sessions.</summary>
    public interface IConfigCache
    {
        /// <summary>The cached payload, or null when there is none. Must not throw.</summary>
        string Read();

        /// <summary>Stores <paramref name="payload"/> as the last known good. Must not throw.</summary>
        void Write(string payload);

        /// <summary>Forgets the cached payload. Must not throw.</summary>
        void Clear();
    }

    /// <summary>A cache that lives only as long as the process. Used by tests and by headless runs.</summary>
    public sealed class InMemoryConfigCache : IConfigCache
    {
        private string _payload;

        /// <summary>Creates a cache, optionally pre-seeded.</summary>
        public InMemoryConfigCache(string payload = null)
        {
            _payload = payload;
        }

        /// <inheritdoc />
        public string Read() => _payload;

        /// <inheritdoc />
        public void Write(string payload) => _payload = payload;

        /// <inheritdoc />
        public void Clear() => _payload = null;
    }

    /// <summary>Knobs for <see cref="ConfigService"/>.</summary>
    public sealed class ConfigServiceOptions
    {
        /// <summary>How long between automatic polls. Default two minutes.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// The payload that ships with the build, used when nothing has ever been cached. Should be a
        /// valid config in which every experiment is present but stopped.
        /// </summary>
        public string ShippedDefaultsPayload { get; set; }

        /// <summary>Where the last accepted payload is kept. Defaults to an in-memory cache.</summary>
        public IConfigCache Cache { get; set; }

        /// <summary>Pins to reconcile whenever a new config is accepted. Optional.</summary>
        public IAssignmentStore AssignmentStore { get; set; }
    }

    /// <summary>
    /// Owns the configuration in force: fetches it, decides whether to trust it, publishes it atomically,
    /// and falls back down a documented ladder when it cannot.
    /// </summary>
    /// <remarks>
    /// <para><b>The ladder.</b> A payload accepted this session beats the last one accepted in a previous
    /// session, which beats the config that shipped with the build, which beats nothing at all. The rung
    /// currently in force is public, because an operator looking at a screen full of control needs to tell
    /// "the server said so" from "we cannot reach the server", and a guardrail nobody can see gets blamed
    /// on the bucketing.</para>
    ///
    /// <para><b>Atomic swap.</b> The snapshot is a single immutable object published with one reference
    /// assignment. A resolve already in flight finishes against the snapshot it started with; a resolve
    /// that begins afterwards sees the new one. There is no interval during which half a config is
    /// visible, which is what makes "at most one experiment per layer" and "never a variant absent from
    /// the current config" true at every instant rather than usually.</para>
    ///
    /// <para><b>Threading contract.</b> <see cref="CurrentSnapshot"/> is a lock-free volatile read and is
    /// safe from any thread at any time. <see cref="Apply"/>, <see cref="Refresh"/> and
    /// <see cref="PollIfDue"/> are serialised against each other by an internal lock, so they are also
    /// safe from any thread, but they are <i>not</i> where blocking I/O belongs: fetch off the main thread
    /// via <see cref="IConfigSource.Fetch"/>, then hand the inert payload to <see cref="Apply"/>. Events
    /// are raised synchronously on the thread that caused the change, outside the lock, so a Unity caller
    /// that wants them on the main thread must apply on the main thread. Slice 5's HTTP transport is built
    /// to that rule: it fetches on a worker and applies on the player loop.</para>
    ///
    /// <para><b>Rejection is not sticky.</b> A rejected payload leaves no residue beyond a log line. The
    /// next payload is read from scratch, so a server that serves rubbish once and good config afterwards
    /// recovers on the very next poll. There is deliberately no error latch, no backoff that has to be
    /// cleared and no "unhealthy" flag that outlives the condition that set it.</para>
    /// </remarks>
    public sealed class ConfigService
    {
        private readonly IConfigSource _source;
        private readonly IClock _clock;
        private readonly IAbLog _log;
        private readonly IConfigCache _cache;
        private readonly IAssignmentStore _assignmentStore;
        private readonly TimeSpan _pollInterval;
        private readonly string _shippedDefaults;

        private readonly object _gate = new object();
        private readonly HashSet<string> _alreadyLogged = new HashSet<string>(StringComparer.Ordinal);

        // The published snapshot. Volatile so a reader on any thread sees the most recent publish without
        // taking the lock, and so the compiler cannot hoist the read out of a loop.
        private volatile ConfigSnapshot _snapshot;

        private string _lastAcceptedRaw;
        private DateTime _lastPollUtc;
        private bool _polledAtLeastOnce;

        /// <summary>Raised when the configuration in force actually changed. Re-resolve on this.</summary>
        /// <remarks>
        /// Deliberately not raised when only the ladder rung changed. A payload identical to the one in
        /// force does not move a single user, so re-resolving every screen because the server came back up
        /// would be work with no output - the same "many signals, at most one evaluation" discipline the
        /// rest of the framework follows.
        /// </remarks>
        public event Action<ConfigSnapshot> ConfigChanged;

        /// <summary>
        /// Raised whenever the published snapshot is replaced, including when only the rung changed.
        /// Status displays bind to this.
        /// </summary>
        public event Action<ConfigSnapshot> StatusChanged;

        /// <summary>The configuration in force. Safe to read from any thread.</summary>
        public ConfigSnapshot CurrentSnapshot => _snapshot;

        /// <summary>Shortcut for <c>CurrentSnapshot.Config</c>.</summary>
        public ExperimentConfig Current => _snapshot.Config;

        /// <summary>How many payloads have been rejected since the last accepted one.</summary>
        public int ConsecutiveFailures { get; private set; }

        /// <summary>The most recent failure, or null when the last attempt succeeded.</summary>
        public string LastFailureReason { get; private set; }

        /// <summary>Creates a service. Nothing is loaded until <see cref="Initialize"/> is called.</summary>
        public ConfigService(IConfigSource source, IClock clock, IAbLog log, ConfigServiceOptions options = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            options = options ?? new ConfigServiceOptions();
            _cache = options.Cache ?? new InMemoryConfigCache();
            _assignmentStore = options.AssignmentStore;
            _pollInterval = options.PollInterval;
            _shippedDefaults = options.ShippedDefaultsPayload;

            _snapshot = ConfigSnapshot.Nothing(_clock.UtcNow);
        }

        /// <summary>
        /// Establishes a starting configuration from the cache, or failing that from the shipped defaults.
        /// Does not touch the network.
        /// </summary>
        /// <remarks>
        /// Called before the first frame so the game always has something coherent to render, even offline
        /// on a fresh install. The shipped defaults are a real config in which every experiment is present
        /// but stopped, so this path produces the control experience rather than an absent one - screens
        /// that ask about an experiment get a definite "not running" instead of a blank.
        /// </remarks>
        public ConfigSnapshot Initialize()
        {
            lock (_gate)
            {
                string cached = SafeReadCache();
                if (cached != null && TrySeed(cached, ConfigSourceKind.LastKnownGood,
                        "restored from the on-disk cache; no payload has been fetched yet", out var restored))
                {
                    return Publish(restored, contentChanged: true);
                }

                if (cached != null)
                {
                    // A cache we wrote ourselves should always be valid. If it is not, the file is corrupt
                    // or was written by an older build; drop it rather than retrying it every launch.
                    LogOnce(AbLogLevel.Warning, "cache.invalid", "cache",
                        "the cached configuration could not be read back and has been discarded");
                    SafeClearCache();
                }

                if (_shippedDefaults != null && TrySeed(_shippedDefaults, ConfigSourceKind.ShippedDefaults,
                        "no cached configuration; running on the defaults that shipped with the build",
                        out var defaults))
                {
                    return Publish(defaults, contentChanged: true);
                }

                if (_shippedDefaults != null)
                {
                    // This is a build-time mistake, not a runtime condition, so it is an error rather than
                    // a warning: the artifact that is supposed to be the floor of the ladder is broken.
                    _log.Log(AbLogLevel.Error,
                        "the shipped default configuration is not valid; falling back to an empty config");
                }

                return _snapshot;
            }
        }

        /// <summary>Fetches from the source and applies whatever comes back.</summary>
        public ConfigApplyResult Refresh()
        {
            var fetch = SafeFetch();

            if (fetch.Outcome == ConfigFetchOutcome.NotModified)
            {
                lock (_gate) { NoteHealthy(); }
                return new ConfigApplyResult(ConfigApplyOutcome.Unchanged, _snapshot, ValidationResult.Ok);
            }

            if (fetch.Outcome == ConfigFetchOutcome.Unreachable)
            {
                return HandleUnreachable(fetch.Error);
            }

            return Apply(fetch.Payload);
        }

        /// <summary>Fetches only if the poll interval has elapsed. Returns null when it has not.</summary>
        /// <remarks>
        /// Driven by <see cref="IClock"/> rather than by a coroutine or a timer thread, so a test can move
        /// time forward and assert exactly how many fetches happened. A test that waits real seconds to
        /// prove a poll fired is a test that eventually gets deleted for being slow.
        /// </remarks>
        public ConfigApplyResult PollIfDue()
        {
            DateTime now = _clock.UtcNow;

            lock (_gate)
            {
                if (_polledAtLeastOnce && now - _lastPollUtc < _pollInterval) return null;
                _lastPollUtc = now;
                _polledAtLeastOnce = true;
            }

            return Refresh();
        }

        /// <summary>
        /// Reads, validates and - if it survives both - publishes <paramref name="raw"/>.
        /// </summary>
        public ConfigApplyResult Apply(string raw)
        {
            ConfigApplyResult result;
            ConfigSnapshot toAnnounce = null;
            bool contentChanged = false;

            lock (_gate)
            {
                result = ApplyLocked(raw, out toAnnounce, out contentChanged);
            }

            // Raised outside the lock: a handler that re-enters the service, or that blocks, must not be
            // able to deadlock the next fetch.
            if (toAnnounce != null) Announce(toAnnounce, contentChanged);
            return result;
        }

        /// <summary>Throws away the cached payload. The demo's "clear last known good" control.</summary>
        public void ClearCache()
        {
            lock (_gate)
            {
                SafeClearCache();
                _lastAcceptedRaw = null;
            }
        }

        private ConfigApplyResult ApplyLocked(string raw, out ConfigSnapshot toAnnounce, out bool contentChanged)
        {
            toAnnounce = null;
            contentChanged = false;

            // Cheapest possible skip: byte-identical to what we already accepted. No parse, no validation,
            // no allocation, no event. This is the common case when polling a server that rarely changes.
            if (raw != null && string.Equals(raw, _lastAcceptedRaw, StringComparison.Ordinal))
            {
                NoteHealthy();

                // The content has not changed, but the rung may have: if we were running on the cache and
                // the server is now answering with the same payload, we are demonstrably live again and the
                // status display should say so. The snapshot is replaced, but ConfigChanged is not raised,
                // because not one user's assignment moves.
                if (_snapshot.Source != ConfigSourceKind.Live)
                {
                    toAnnounce = new ConfigSnapshot(_snapshot.Config, ConfigSourceKind.Live, _clock.UtcNow);
                    _snapshot = toAnnounce;
                }

                return new ConfigApplyResult(ConfigApplyOutcome.Unchanged, _snapshot, ValidationResult.Ok);
            }

            var read = ConfigReader.Read(raw);
            if (!read.IsValid)
            {
                return Reject(read.Issues, "payload.read");
            }

            var validation = ConfigValidator.Validate(read.Config);
            if (!validation.IsValid)
            {
                return Reject(validation, "payload.validate");
            }

            foreach (var issue in validation.Issues)
            {
                LogOnce(AbLogLevel.Warning, "warn." + issue.Code + "." + read.Config.ConfigVersion,
                    issue.Entity, issue.Detail);
            }

            // Same version label, different bytes. The version is the payload's identity; honouring the
            // change would leave the client running something the server believes is version 7 while the
            // analysis pipeline attributes the results to a different version 7. Refusing is the safe
            // reading, and saying so once makes the server bug findable.
            if (_snapshot.Source == ConfigSourceKind.Live &&
                string.Equals(read.Config.ConfigVersion, _snapshot.ConfigVersion, StringComparison.Ordinal))
            {
                NoteTransportWorked();
                LogOnce(AbLogLevel.Warning, "payload.contentDrift." + read.Config.ConfigVersion, "payload",
                    "version '" + read.Config.ConfigVersion + "' was served with different content than the " +
                    "copy already in force; ignoring it, because the version label is what identifies a " +
                    "payload - bump the version to publish a change");
                return new ConfigApplyResult(ConfigApplyOutcome.ContentDriftIgnored, _snapshot, validation);
            }

            _lastAcceptedRaw = raw;
            SafeWriteCache(raw);
            NoteHealthy();

            var snapshot = new ConfigSnapshot(read.Config, ConfigSourceKind.Live, _clock.UtcNow);
            _snapshot = snapshot;

            ReconcilePins(read.Config);

            toAnnounce = snapshot;
            contentChanged = true;
            return new ConfigApplyResult(ConfigApplyOutcome.Accepted, snapshot, validation);
        }

        private ConfigApplyResult Reject(ValidationResult issues, string stage)
        {
            ConsecutiveFailures++;
            LastFailureReason = issues.FirstError;

            // Keyed by the specific finding so a server stuck serving the same broken payload says it once,
            // while a server that breaks in a new way is never swallowed.
            LogOnce(AbLogLevel.Warning, stage + "." + FirstErrorCode(issues) + "." + Fingerprint(issues),
                null,
                "configuration payload rejected, keeping " + DescribeCurrentRung() + " - " + issues.Describe());

            return new ConfigApplyResult(ConfigApplyOutcome.Rejected, _snapshot, issues);
        }

        private ConfigApplyResult HandleUnreachable(string error)
        {
            lock (_gate)
            {
                ConsecutiveFailures++;
                LastFailureReason = error;

                // The configuration in force does not change - that is the whole point of the ladder -
                // but the rung does. It is no longer live-confirmed, because the server that supplied it
                // cannot be reached, and a status display that goes on reading LIVE is reporting
                // something untrue in every frame of every recording. Demoting here is the missing half
                // of a pair: the climb back already exists, where an unchanged payload from a reachable
                // server restores Live without raising ConfigChanged.
                //
                // No event is raised, because not one user's assignment moves.
                if (_snapshot.Source == ConfigSourceKind.Live)
                {
                    _snapshot = new ConfigSnapshot(
                        _snapshot.Config, ConfigSourceKind.LastKnownGood, _clock.UtcNow);
                }

                LogOnce(AbLogLevel.Warning, "source.unreachable." + error, "config source",
                    _source.Description + " could not be reached (" + error + "), keeping " +
                    DescribeCurrentRung());

                return new ConfigApplyResult(ConfigApplyOutcome.Rejected, _snapshot,
                    ValidationResult.Error("source.unreachable", "config source", error));
            }
        }

        private bool TrySeed(string raw, ConfigSourceKind kind, string reason, out ConfigSnapshot snapshot)
        {
            snapshot = null;

            var read = ConfigReader.Read(raw);
            if (!read.IsValid) return false;
            if (!ConfigValidator.Validate(read.Config).IsValid) return false;

            // Seeding records the raw text as accepted, so that a later fetch of the very same payload is
            // recognised as unchanged and skips straight to the rung upgrade.
            _lastAcceptedRaw = raw;
            snapshot = new ConfigSnapshot(read.Config, kind, _clock.UtcNow, reason);
            _snapshot = snapshot;

            ReconcilePins(read.Config);
            return true;
        }

        private ConfigSnapshot Publish(ConfigSnapshot snapshot, bool contentChanged)
        {
            Announce(snapshot, contentChanged);
            return snapshot;
        }

        private void Announce(ConfigSnapshot snapshot, bool contentChanged)
        {
            StatusChanged?.Invoke(snapshot);
            if (contentChanged) ConfigChanged?.Invoke(snapshot);
        }

        private void ReconcilePins(ExperimentConfig config)
        {
            if (_assignmentStore == null) return;

            var report = PinReconciler.Reconcile(config, _assignmentStore);
            if (report.RemovedCount == 0) return;

            _log.Log(AbLogLevel.Info,
                "discarded " + report.RemovedCount + " cached assignment(s) after the configuration " +
                "changed: " + report.Describe());
        }

        /// <summary>The service is fully healthy: the transport worked and the payload was usable.</summary>
        /// <remarks>
        /// Clearing the log-once set here is what closes an incident. A failure that recurs after a
        /// genuine recovery is a new episode and deserves a new line - an operator watching a flapping
        /// server needs to see each one, not a single line from an hour ago.
        /// </remarks>
        private void NoteHealthy()
        {
            NoteTransportWorked();
            _alreadyLogged.Clear();
        }

        /// <summary>
        /// The transport worked, but the payload was not usable as-is - content drift, for instance.
        /// </summary>
        /// <remarks>
        /// The failure counters are about reachability, so they reset. The log-once set deliberately does
        /// not: the anomaly is still present, and clearing it here would make the warning repeat on every
        /// single poll for as long as the server kept serving the same thing.
        /// </remarks>
        private void NoteTransportWorked()
        {
            ConsecutiveFailures = 0;
            LastFailureReason = null;
        }

        private void LogOnce(AbLogLevel level, string key, string entity, string detail)
        {
            if (!_alreadyLogged.Add(key)) return;
            _log.Log(level, entity == null ? detail : entity + ": " + detail);
        }

        private string DescribeCurrentRung()
        {
            switch (_snapshot.Source)
            {
                case ConfigSourceKind.Live: return "the configuration already in force (version '" +
                                                   _snapshot.ConfigVersion + "')";
                case ConfigSourceKind.LastKnownGood: return "the last known good configuration (version '" +
                                                            _snapshot.ConfigVersion + "')";
                case ConfigSourceKind.ShippedDefaults: return "the shipped default configuration";
                default: return "an empty configuration";
            }
        }

        private static string FirstErrorCode(ValidationResult issues)
        {
            foreach (var issue in issues.Issues)
            {
                if (issue.Level == ValidationLevel.Error) return issue.Code;
            }

            return "unknown";
        }

        /// <summary>
        /// Discriminates one broken payload from another under the same error code, so a server that
        /// breaks in a new way is not silenced by the earlier line.
        /// </summary>
        private static string Fingerprint(ValidationResult issues)
        {
            string first = issues.FirstError;
            return first == null ? "none" : Hashing.Murmur3.Hash32(first).ToString("x8");
        }

        private ConfigFetchResult SafeFetch()
        {
            try
            {
                return _source.Fetch() ?? ConfigFetchResult.Unreachable("the source returned nothing");
            }
            catch (Exception e)
            {
                // IConfigSource.Fetch is documented as never throwing. Transports are written by people, so
                // this catches the case where one does anyway - a throwing transport must degrade to the
                // fallback ladder, not take the game down.
                return ConfigFetchResult.Unreachable(e.GetType().Name + ": " + e.Message);
            }
        }

        private string SafeReadCache()
        {
            try
            {
                return _cache.Read();
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Warning, "could not read the configuration cache: " + e.Message);
                return null;
            }
        }

        private void SafeWriteCache(string payload)
        {
            try
            {
                _cache.Write(payload);
            }
            catch (Exception e)
            {
                // Failing to persist last-known-good costs us the next cold start, not this session.
                _log.Log(AbLogLevel.Warning, "could not write the configuration cache: " + e.Message);
            }
        }

        private void SafeClearCache()
        {
            try
            {
                _cache.Clear();
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Warning, "could not clear the configuration cache: " + e.Message);
            }
        }
    }
}
