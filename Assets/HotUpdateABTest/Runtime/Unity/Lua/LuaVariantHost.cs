using System;
using System.Collections.Generic;
using HotUpdateABTest.Core;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Presentation;
using XLua;

namespace HotUpdateABTest.Lua
{
    /// <summary>What a reload did.</summary>
    public sealed class LuaReloadReport
    {
        /// <summary>Files that loaded and committed their registrations.</summary>
        public int FilesLoaded { get; internal set; }

        /// <summary>Files that failed and were skipped.</summary>
        public int FilesFailed { get; internal set; }

        /// <summary>Behaviors registered afterwards.</summary>
        public int BehaviorCount { get; internal set; }

        /// <summary>How many of the loaded files came from the patch root.</summary>
        public int PatchesLoaded { get; internal set; }

        /// <summary>The patch files that loaded, in the order they loaded.</summary>
        /// <remarks>
        /// Named rather than merely counted because later registrations win, so the order is the answer
        /// to "which patch is actually in force". A play-test pass spent a session unable to make any
        /// patch apply, and the cause was a deliberately-rejected example left in the folder under a name
        /// that sorted first: the count said "1 patch" and the screen said nothing at all.
        /// </remarks>
        public IReadOnlyList<string> PatchNames { get; internal set; } = new string[0];

        /// <summary>One line for the log panel.</summary>
        public string Describe() =>
            FilesLoaded + " file(s) loaded (" + PatchesLoaded + " patch), " + FilesFailed +
            " failed, " + BehaviorCount + " behaviors registered" +
            (PatchNames.Count == 0
                ? ", no patch files in the folder"
                : "; patches in load order, last wins: " + string.Join(", ", PatchNames));

        /// <inheritdoc />
        public override string ToString() => Describe();
    }

    /// <summary>
    /// Owns the Lua VM and is the only thing in the codebase that talks to it.
    /// </summary>
    /// <remarks>
    /// <para><b>The seam.</b> C# builds an immutable context table, calls a registered behavior, and gets a
    /// plain table back which is validated field by field before anything is applied. Nothing else crosses:
    /// no <c>GObject</c>, no delegates handed to Lua, no C# objects in the context. That is what makes a bad
    /// patch produce a rejected spec rather than a corrupted UI tree, and what makes a variant's behaviour
    /// testable with no UI at all.</para>
    ///
    /// <para><b>Lua cannot record telemetry.</b> The context table contains only values. There is no
    /// function on it, so there is nothing to call, and xLua's <c>CS</c> bridge - which would otherwise
    /// hand a patch the entire C# type system including the analytics sink - is removed from the sandbox.
    /// Telemetry integrity is the product here; a patch that could fabricate, duplicate or suppress events
    /// would make every number downstream meaningless.</para>
    ///
    /// <para><b>Reload rebuilds rather than diffs.</b> <see cref="Reload"/> resets the registry and reloads
    /// every file from scratch, which makes pressing the demo's reload button twice a no-op and makes
    /// deleting a patch file revert the variant it changed - both for free, rather than as two features
    /// that have to be got right separately.</para>
    ///
    /// <para><b>Disposal order matters.</b> Cached <see cref="LuaFunction"/> handles are released before
    /// the environment they came from. Disposing the environment first leaves the handles pointing into a
    /// freed VM, which fails at some later, unrelated moment.</para>
    /// </remarks>
    public sealed class LuaVariantHost : IAudiencePredicateEvaluator, IDisposable
    {
        private readonly LuaPatchLoader _loader;
        private readonly IAbLog _log;
        private readonly HashSet<string> _loggedOnce = new HashSet<string>(StringComparer.Ordinal);

        private LuaEnv _env;
        private LuaTable _module;
        private LuaFunction _loadChunk;
        private LuaFunction _invoke;
        private LuaFunction _evaluateAudience;
        private LuaFunction _reset;
        private LuaFunction _setLog;
        private LuaFunction _hasBehavior;

        private bool _disposed;

        /// <summary>True when the VM started and the bootstrap ran.</summary>
        public bool IsReady { get; private set; }

        /// <summary>What the last reload did.</summary>
        public LuaReloadReport LastReload { get; private set; } = new LuaReloadReport();

        /// <summary>Where hot updates are dropped. Shown in the debug panel.</summary>
        public string PatchRoot => _loader.PatchRoot;

        /// <summary>Creates a host and starts the VM.</summary>
        public LuaVariantHost(LuaPatchLoader loader, IAbLog log)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            Start();
        }

        /// <summary>Discards every registration and loads all Lua again from disk.</summary>
        public LuaReloadReport Reload()
        {
            ThrowIfDisposed();

            var report = new LuaReloadReport();
            if (!IsReady) return LastReload = report;

            var patchNames = new List<string>();

            _reset.Call();

            foreach (var file in _loader.Discover())
            {
                object[] result = _loadChunk.Call(file.Source, file.Path);

                bool ok = result.Length > 0 && result[0] is bool flag && flag;
                if (!ok)
                {
                    report.FilesFailed++;

                    string reason = Describe(result, 1);

                    // Keyed by file *and* reason. Keying on the path alone was the bug: an author editing
                    // one file until it works hits the same key every time, so the first failure was
                    // reported and every later one - a different error, in a file they had just changed -
                    // was swallowed. The comment claimed a newly broken file is never hidden by an
                    // earlier line; keying on the path could not deliver that.
                    //
                    // Reported at Error, not Warning. A file that cannot be parsed or cannot run is not a
                    // caution about something that might matter later, it is a patch that is not running.
                    // It was also the reason the log panel's error page was unreachable: nothing in the
                    // demo ever emitted one.
                    LogOnce(AbLogLevel.Error, "patch.failed." + file.Path + "." + reason,
                        (file.IsPatch ? "patch" : "baseline") + " file '" + file.Name +
                        "' was skipped: " + reason +
                        ". Everything registered before it is unaffected.");
                    continue;
                }

                report.FilesLoaded++;
                if (file.IsPatch)
                {
                    report.PatchesLoaded++;
                    patchNames.Add(file.Name);
                }
            }

            report.PatchNames = patchNames;
            report.BehaviorCount = BehaviorCount();

            if (report.FilesFailed == 0) _loggedOnce.Clear();

            _log.Log(AbLogLevel.Info, "Lua reloaded: " + report.Describe());
            return LastReload = report;
        }

        /// <summary>True when a behavior is registered under this key.</summary>
        public bool HasBehavior(string behaviorKey)
        {
            if (!IsReady || behaviorKey == null) return false;

            object[] result = _hasBehavior.Call(behaviorKey);
            return result.Length > 0 && result[0] is bool flag && flag;
        }

        /// <summary>
        /// Runs the behavior for <paramref name="assignment"/> and returns the validated spec merged onto
        /// <paramref name="baseline"/>.
        /// </summary>
        /// <remarks>
        /// Every failure - no VM, no registered behavior, a Lua error, a non-table return, a spec that fails
        /// validation - produces the same outcome: the baseline is returned and the reason is logged once.
        /// The caller does not branch on why, because there is nothing different to do.
        /// </remarks>
        public PresentationSpec Present(
            UserContext user,
            VariantAssignment assignment,
            SpecFieldGroup group,
            PresentationSpec baseline,
            bool hasOriginalPrice = true)
        {
            return Present(user, assignment, group, baseline, hasOriginalPrice, out _);
        }

        /// <summary>
        /// As <see cref="Present(UserContext, VariantAssignment, SpecFieldGroup, PresentationSpec, bool)"/>,
        /// but also reports a short token naming why the baseline was returned.
        /// </summary>
        /// <remarks>
        /// The demo needs the distinction on screen: a rejected spec renders the baseline, which looks
        /// exactly like a working control variant unless something says otherwise, and the log alone does
        /// not disambiguate a still frame. A token rather than the sentence, because a viewer watching a
        /// recording needs the class of failure and has no time to read prose off a still - the full
        /// message is in the log where there is room for it.
        /// </remarks>
        public PresentationSpec Present(
            UserContext user,
            VariantAssignment assignment,
            SpecFieldGroup group,
            PresentationSpec baseline,
            bool hasOriginalPrice,
            out string rejectionToken)
        {
            rejectionToken = null;
            ThrowIfDisposed();

            if (user == null) throw new ArgumentNullException(nameof(user));
            if (assignment == null) throw new ArgumentNullException(nameof(assignment));
            if (!assignment.IsAssigned) return baseline;

            string behaviorKey = assignment.Variant.Behavior;

            if (!IsReady)
            {
                rejectionToken = SpecRejection.NoLua;
                LogOnce("lua.notReady", "the Lua environment is not running; every variant renders control");
                return baseline;
            }

            LuaTable context = null;
            try
            {
                context = BuildContext(user, assignment, hasOriginalPrice);
                object[] result = _invoke.Call(behaviorKey, context);

                bool ok = result.Length > 0 && result[0] is bool flag && flag;
                if (!ok)
                {
                    string detail = Describe(result, 1);
                    rejectionToken = detail.Contains("no behavior is registered")
                        ? SpecRejection.NoBehavior
                        : SpecRejection.LuaError;
                    LogOnce("behavior.failed." + behaviorKey,
                        "variant '" + assignment.VariantId + "' of '" + assignment.ExperimentId +
                        "' renders control: " + detail);
                    return baseline;
                }

                if (!(result[1] is LuaTable table))
                {
                    rejectionToken = SpecRejection.NotATable;
                    LogOnce("behavior.notATable." + behaviorKey,
                        "variant '" + assignment.VariantId + "' returned something that is not a table");
                    return baseline;
                }

                try
                {
                    var fields = ToDictionary(table);
                    var read = PresentationSpecReader.Read(fields, group, baseline,
                        "variant '" + assignment.VariantId + "' (" + behaviorKey + ")");

                    if (!read.IsValid)
                    {
                        rejectionToken = SpecRejection.Token(read.Issues);
                        LogOnce("spec.invalid." + behaviorKey,
                            "the spec from '" + behaviorKey + "' was rejected and renders control: " +
                            read.Issues.Describe());
                    }

                    return read.Spec;
                }
                finally
                {
                    table.Dispose();
                }
            }
            catch (Exception e)
            {
                // The sandbox and the Lua-side pcall should make this unreachable. It is here because a
                // variant rendering the wrong thing is survivable and an exception escaping into a UI
                // callback is not.
                rejectionToken = SpecRejection.LuaError;
                LogOnce("behavior.threw." + behaviorKey,
                    "calling '" + behaviorKey + "' threw and renders control: " + e.Message);
                return baseline;
            }
            finally
            {
                context?.Dispose();
            }
        }

        /// <summary>
        /// Evaluates a named audience predicate. Anything other than a clean <c>true</c> means "does not
        /// match".
        /// </summary>
        /// <remarks>
        /// Fails closed, deliberately. A predicate that errors, returns a non-boolean, or is not registered
        /// at all excludes the user. Failing open would sweep users into a treatment nobody validated on
        /// the strength of a bug, and the experiment would then be measuring the bug rather than the
        /// treatment. Excluding them costs sample size, which is the cheaper mistake by a wide margin.
        /// </remarks>
        public bool EvaluateAudience(string predicateKey, UserContext user)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(predicateKey)) return true;
            if (user == null) throw new ArgumentNullException(nameof(user));

            if (!IsReady)
            {
                LogOnce("lua.notReady.audience",
                    "the Lua environment is not running, so audience predicate '" + predicateKey +
                    "' cannot be evaluated and excludes everyone");
                return false;
            }

            LuaTable context = null;
            try
            {
                context = BuildContext(user, null, true);
                object[] result = _evaluateAudience.Call(predicateKey, context);

                bool ok = result.Length > 0 && result[0] is bool okFlag && okFlag;
                if (!ok)
                {
                    LogOnce("audience.failed." + predicateKey,
                        "audience predicate '" + predicateKey + "' failed and excludes the user: " +
                        Describe(result, 2));
                    return false;
                }

                return result.Length > 1 && result[1] is bool matched && matched;
            }
            catch (Exception e)
            {
                LogOnce("audience.threw." + predicateKey,
                    "audience predicate '" + predicateKey + "' threw and excludes the user: " + e.Message);
                return false;
            }
            finally
            {
                context?.Dispose();
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// The resolver talks to this through <see cref="IAudiencePredicateEvaluator"/> so the decision core
        /// stays free of Lua, exactly as it stays free of UnityEngine.
        /// </remarks>
        public bool Matches(string predicateKey, UserContext user) => EvaluateAudience(predicateKey, user);

        /// <summary>How many behaviors are registered.</summary>
        public int BehaviorCount()
        {
            if (!IsReady) return 0;

            var count = _module.Get<LuaFunction>("behavior_count");
            try
            {
                object[] result = count.Call();
                return result.Length > 0 ? Convert.ToInt32(result[0]) : 0;
            }
            finally
            {
                count?.Dispose();
            }
        }

        /// <summary>Every registered behavior key, for the debug panel.</summary>
        public List<string> BehaviorKeys()
        {
            var keys = new List<string>();
            if (!IsReady) return keys;

            var fn = _module.Get<LuaFunction>("behavior_keys");
            try
            {
                object[] result = fn.Call();
                string joined = result.Length > 0 ? result[0] as string : null;
                if (string.IsNullOrEmpty(joined)) return keys;

                keys.AddRange(joined.Split('\n'));
                return keys;
            }
            finally
            {
                fn?.Dispose();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsReady = false;

            // Handles first, then the environment they point into.
            _loadChunk?.Dispose();
            _invoke?.Dispose();
            _evaluateAudience?.Dispose();
            _reset?.Dispose();
            _setLog?.Dispose();
            _hasBehavior?.Dispose();
            _module?.Dispose();

            _loadChunk = null;
            _invoke = null;
            _evaluateAudience = null;
            _reset = null;
            _setLog = null;
            _hasBehavior = null;
            _module = null;

            if (_env != null)
            {
                try
                {
                    _env.Dispose();
                }
                catch (Exception e)
                {
                    _log.Log(AbLogLevel.Warning, "the Lua environment did not shut down cleanly: " + e.Message);
                }

                _env = null;
            }
        }

        private void Start()
        {
            string bootstrap = _loader.ReadBootstrap();
            if (bootstrap == null) return;

            try
            {
                _env = new LuaEnv();

                object[] loaded = _env.DoString(bootstrap, "@" + _loader.BootstrapPath);
                if (loaded.Length == 0 || !(loaded[0] is LuaTable module))
                {
                    _log.Log(AbLogLevel.Error, "the Lua bootstrap did not return its module table");
                    return;
                }

                _module = module;
                _loadChunk = _module.Get<LuaFunction>("load_chunk");
                _invoke = _module.Get<LuaFunction>("invoke");
                _evaluateAudience = _module.Get<LuaFunction>("evaluate_audience");
                _reset = _module.Get<LuaFunction>("reset");
                _setLog = _module.Get<LuaFunction>("set_log");
                _hasBehavior = _module.Get<LuaFunction>("has_behavior");

                if (_loadChunk == null || _invoke == null || _evaluateAudience == null ||
                    _reset == null || _setLog == null || _hasBehavior == null)
                {
                    _log.Log(AbLogLevel.Error, "the Lua bootstrap is missing one of its entry points");
                    return;
                }

                _setLog.Call(new Action<string>(line => _log.Log(AbLogLevel.Info, "[lua] " + line)));

                IsReady = true;
                Reload();
            }
            catch (Exception e)
            {
                _log.Log(AbLogLevel.Error,
                    "the Lua environment could not be started; every variant will render control: " + e.Message);
                IsReady = false;
            }
        }

        /// <summary>
        /// Builds the context table. This is the entire surface a behavior can see.
        /// </summary>
        /// <remarks>
        /// Values only - no functions, no C# objects, no collections. Adding a field here is a decision
        /// about what a hot update can reach, which is why the list is short and every entry is a plain
        /// scalar a behavior could plausibly branch on.
        /// </remarks>
        private LuaTable BuildContext(UserContext user, VariantAssignment assignment, bool hasOriginalPrice)
        {
            var context = _env.NewTable();

            context.Set("user_id", user.UserId);
            context.Set("account_level", user.AccountLevel);
            context.Set("platform", user.Platform);
            context.Set("country", user.Country ?? "");

            context.Set("layer_id", assignment?.LayerId ?? "");
            context.Set("experiment_id", assignment?.ExperimentId ?? "");
            context.Set("variant_id", assignment?.VariantId ?? "");
            context.Set("config_version", assignment?.ConfigVersion ?? "");

            context.Set("has_original_price", hasOriginalPrice);

            return context;
        }

        /// <summary>Flattens a returned Lua table into the plain dictionary the reader validates.</summary>
        /// <remarks>
        /// Only string keys are collected. A behavior returning an array or a mixed table is producing
        /// something the spec has no shape for, and the unknown-field rule will reject it.
        /// </remarks>
        private static Dictionary<string, object> ToDictionary(LuaTable table)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);

            table.ForEach<object, object>((key, value) =>
            {
                if (key is string name) fields[name] = value;
            });

            return fields;
        }

        private static string Describe(object[] result, int index)
        {
            if (result == null || result.Length <= index) return "no detail";
            return result[index] as string ?? "no detail";
        }

        private void LogOnce(string key, string message) => LogOnce(AbLogLevel.Warning, key, message);

        private void LogOnce(AbLogLevel level, string key, string message)
        {
            if (!_loggedOnce.Add(key)) return;
            _log.Log(level, message);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LuaVariantHost));
        }
    }
}
