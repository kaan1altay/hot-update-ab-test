using System;
using System.Collections.Generic;
using HotUpdateABTest.Core;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Presentation;
using HotUpdateABTest.Core.Telemetry;
using HotUpdateABTest.Lua;
using HotUpdateABTest.Transport;

namespace HotUpdateABTest.Demo
{
    /// <summary>
    /// The demo's brain: owns every framework object, answers every button, and knows how to undo
    /// everything it can be told to do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately free of FairyGUI. The view layer reads state from here and calls into it; nothing here
    /// touches a <c>GObject</c>. That is what lets the PlayMode suite drive every LiveOps action without a
    /// screen, and what let this be written while the package was still being drawn.
    /// </para>
    /// <para>
    /// <b>Every state-entering action has a defined way out</b>, and <see cref="ResetDemo"/> performs all of
    /// them at once. That discipline is carried over from the red-dot repository, where two of the three
    /// bugs hand play-testing found were of the "nothing makes this false again" class. The pairs are
    /// tabulated in <c>docs/STATUS.md</c> and each is asserted in <c>DemoActionPairTests</c>.
    /// </para>
    /// </remarks>
    public sealed class AbTestDemoController : IDisposable
    {
        /// <summary>The two layers the demo runs experiments in.</summary>
        public const string OfferLayer = "offer_layout";

        /// <summary>The pricing and call-to-action layer.</summary>
        public const string PricingLayer = "pricing_cta";

        private const string GoalId = "purchase";
        private const int SimulatedUsers = 5000;

        private readonly IAbLog _log;
        private readonly IClock _clock;

        private readonly LocalConfigServer _server;
        private readonly ConfigService _config;
        private readonly InMemoryAssignmentStore _pins;
        private readonly ExperimentResolver _resolver;
        private readonly ExposureLedger _ledger;
        private readonly InMemoryAnalyticsSink _events;
        private readonly MetricsAggregator _metrics;
        private readonly ExposureTracker _exposures;
        private readonly ConversionTracker _conversions;
        private readonly SessionTracker _sessions;
        private readonly LuaVariantHost _lua;

        private int _simulationRun;
        private int _forcedIndex = -1;

        /// <summary>The user the on-screen shop renders for.</summary>
        public UserContext Player { get; private set; } =
            new UserContext("player-local", accountLevel: 7, platform: "editor", country: "TR");

        /// <summary>True while one arm is deliberately not logging exposures.</summary>
        public bool SkipExposureBreakage { get; private set; }

        /// <summary>True while bucketing is deliberately skewed.</summary>
        public bool BucketingSkewBreakage { get; private set; }

        /// <summary>The spec the shop screen should render.</summary>
        public PresentationSpec CurrentSpec { get; private set; } = PresentationSpec.Baseline;

        /// <summary>A short token naming why the last render fell back, or null when it did not.</summary>
        /// <remarks>
        /// Surfaced rather than only logged. A rejected spec renders the baseline, which is visually
        /// identical to a working control variant - the demo has to be able to say which it is looking at,
        /// in a still frame, without reading the log panel.
        /// </remarks>
        public string LastRejectionToken { get; private set; }

        /// <summary>The config in force.</summary>
        public ConfigSnapshot Snapshot => _config.CurrentSnapshot;

        /// <summary>The local server, for status display and scenario buttons.</summary>
        public LocalConfigServer Server => _server;

        /// <summary>The Lua host, for the patch folder path and the reload button.</summary>
        public LuaVariantHost Lua => _lua;

        /// <summary>True while a QA override is in force.</summary>
        public bool IsForced => _resolver.Overrides.Any;

        /// <summary>A one-line description of the active override, or null.</summary>
        public string ForcedDescription
        {
            get
            {
                if (!IsForced) return null;

                var parts = new List<string>();
                foreach (var pair in _resolver.Overrides.All) parts.Add(pair.Key + " = " + pair.Value);
                return "FORCED: " + string.Join(", ", parts.ToArray()) + " - excluded from all metrics";
            }
        }

        /// <summary>Creates the demo. Nothing is fetched until <see cref="Start"/>.</summary>
        public AbTestDemoController(
            IAbLog log,
            IClock clock,
            LuaVariantHost lua,
            string shippedDefaults = null,
            bool preferHttp = true)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _lua = lua;

            _server = new LocalConfigServer(_log);

            bool bound = preferHttp && _server.Start();

            // When no port binds - a firewall prompt, a locked-down machine - the demo degrades to reading
            // the same scenarios in-process rather than ending. Every button still works.
            IConfigSource source = bound
                ? (IConfigSource)new HttpConfigSource(() => _server.Url)
                : new DirectConfigSource(_server);

            if (!bound)
            {
                _log.Log(AbLogLevel.Warning,
                    "running without HTTP: config is read in-process. Every scenario button still works.");
            }

            _pins = new InMemoryAssignmentStore();
            _config = new ConfigService(source, _clock, _log, new ConfigServiceOptions
            {
                Cache = new InMemoryConfigCache(),
                AssignmentStore = _pins,
                ShippedDefaultsPayload = shippedDefaults,
                PollInterval = TimeSpan.FromSeconds(5)
            });

            _resolver = new ExperimentResolver(_pins, null, _lua);
            _ledger = new ExposureLedger();
            _events = new InMemoryAnalyticsSink();
            _metrics = new MetricsAggregator();

            var sink = new CompositeAnalyticsSink(_events, _metrics);
            _exposures = new ExposureTracker(_ledger, sink, _clock, _resolver);
            _conversions = new ConversionTracker(_ledger, sink, _clock);
            _sessions = new SessionTracker(_clock);
        }

        /// <summary>Raised whenever anything the screen shows may have changed.</summary>
        public event Action Changed;

        /// <summary>Establishes a starting config from the cache or the shipped defaults, then fetches.</summary>
        /// <remarks>
        /// Initialize before Refresh, deliberately. It means the very first frame renders something
        /// coherent - the control experience, from the defaults that shipped in the build - rather than an
        /// empty screen that fills in a moment later if the network happens to be there.
        /// </remarks>
        public void Start()
        {
            _config.StatusChanged += _ => Refresh();
            _config.Initialize();
            _config.Refresh();
            Refresh();
        }

        /// <summary>Polls the config source if the interval has elapsed.</summary>
        public void Tick() => _config.PollIfDue();

        /// <summary>Handles a console button by its child name.</summary>
        public void OnButton(string name)
        {
            switch (name)
            {
                case "btnServerToggle": ToggleServer(); break;
                case "btnRefresh": _config.Refresh(); break;
                case "btnScenarioNormal": SetScenario(ServerScenario.Normal); break;
                case "btnScenarioWeights": SetScenario(ServerScenario.WeightsRamped); break;
                case "btnScenarioPause": SetScenario(ServerScenario.ExperimentPaused); break;
                case "btnScenarioKill": SetScenario(ServerScenario.KillSwitch); break;
                case "btnScenarioMalformed": SetScenario(ServerScenario.MalformedJson); break;
                case "btnScenarioBadSchema": SetScenario(ServerScenario.BadSchemaVersion); break;
                case "btnScenarioOffline": SetScenario(ServerScenario.Offline); break;
                case "btnSimulate": SimulateUsers(SimulatedUsers); break;
                case "btnForceVariant": CycleForcedVariant(); break;
                case "btnClearForce": ClearForcedVariant(); break;
                case "btnInjectSkew": SetBucketingSkew(!BucketingSkewBreakage); break;
                case "btnSkipExposure": SetExposureSkipping(!SkipExposureBreakage); break;
                case "btnReloadPatches": ReloadPatches(); break;
                case "btnDumpState": DumpState(); break;
                case "btnClearState": ResetDemo(); break;
                default: _log.Log(AbLogLevel.Warning, "unhandled button '" + name + "'"); break;
            }

            Refresh();
        }

        /// <summary>Starts or stops the local server.</summary>
        public void ToggleServer()
        {
            if (_server.IsRunning) _server.Stop();
            else _server.Start();
        }

        /// <summary>Changes what the server serves and fetches immediately.</summary>
        public void SetScenario(ServerScenario scenario)
        {
            _server.SetScenario(scenario);
            _config.Refresh();
        }

        /// <summary>Resolves and renders the shop screen for the local player.</summary>
        /// <remarks>
        /// Two layers compose: the offer layer's behavior may set the layout, the pricing layer's may set
        /// the price presentation, badge and call to action. Neither can write the other's fields - a
        /// behavior that tries has its whole spec rejected - so the two experiments genuinely run side by
        /// side rather than one quietly winning.
        /// </remarks>
        public PresentationSpec RenderShop()
        {
            var spec = PresentationSpec.Baseline;
            string rejection = null;

            spec = ApplyLayer(OfferLayer, SpecFieldGroup.Layout, spec, ref rejection);
            spec = ApplyLayer(PricingLayer, SpecFieldGroup.Pricing, spec, ref rejection);

            CurrentSpec = spec;
            LastRejectionToken = rejection;
            return spec;
        }

        /// <summary>Records that the player has actually seen the shop screen.</summary>
        /// <remarks>
        /// Separate from <see cref="RenderShop"/> on purpose. Resolving is free and silent; this is the
        /// moment the exposure exists, and it is the caller's job to call it only when the surface is
        /// genuinely in front of somebody.
        /// </remarks>
        public void MarkShopSeen()
        {
            foreach (string layer in new[] { OfferLayer, PricingLayer })
            {
                var assignment = Resolve(Player, layer);
                if (!assignment.IsAssigned) continue;

                _exposures.RecordAssignment(Player, assignment, _sessions.Current);
                if (IsExposureSuppressed(assignment)) continue;

                _exposures.MarkExposed(Player, assignment, _sessions.Current);
            }

            Refresh();
        }

        /// <summary>Records a conversion for the local player.</summary>
        public void Convert(string offerId)
        {
            _conversions.Convert(Player, _sessions.Current, GoalId);
            _log.Log(AbLogLevel.Info, "converted on " + offerId);
            Refresh();
        }

        /// <summary>Runs a synthetic population through both layers.</summary>
        public void SimulateUsers(int count)
        {
            _simulationRun++;

            for (int i = 0; i < count; i++)
            {
                string userId = "sim-" + i;
                var user = new UserContext(userId, accountLevel: 3 + (i % 5), platform: "editor", country: "TR");
                var session = SessionId.ForSimulatedUser(userId, _simulationRun);

                foreach (string layer in new[] { OfferLayer, PricingLayer })
                {
                    var assignment = Resolve(user, layer);
                    if (!assignment.IsAssigned) continue;

                    _exposures.RecordAssignment(user, assignment, session, synthetic: true);
                    if (IsExposureSuppressed(assignment)) continue;

                    _exposures.MarkExposed(user, assignment, session, synthetic: true);
                }

                // A fixed conversion rate, deterministic rather than random, so two runs of the demo
                // produce the same picture and a change on screen means something changed.
                if (i % 5 == 0) _conversions.Convert(user, session, GoalId, synthetic: true);

                _exposures.ForgetSession(session);
            }

            _log.Log(AbLogLevel.Info, "simulated " + count + " users (run " + _simulationRun + ")");
        }

        /// <summary>Cycles the QA override through the arms of the pricing experiment.</summary>
        public void CycleForcedVariant()
        {
            var experiment = Snapshot.Config.FindExperiment("exp_pricing_cta");
            if (experiment == null || experiment.Variants.Count == 0)
            {
                _log.Log(AbLogLevel.Warning, "no pricing experiment in the current config to force");
                return;
            }

            _forcedIndex++;
            if (_forcedIndex >= experiment.Variants.Count)
            {
                ClearForcedVariant();
                return;
            }

            string variantId = experiment.Variants[_forcedIndex].Id;
            _resolver.Overrides.Force(experiment.Id, variantId);
            _log.Log(AbLogLevel.Warning,
                "FORCED " + experiment.Id + " = " + variantId + "; these exposures are flagged and " +
                "excluded from every metric");
        }

        /// <summary>Clears every QA override.</summary>
        public void ClearForcedVariant()
        {
            _forcedIndex = -1;
            if (!_resolver.Overrides.Any) return;

            _resolver.Overrides.ClearAll();
            _log.Log(AbLogLevel.Info, "QA override cleared; bucketing decides again");
        }

        /// <summary>
        /// Breaks or fixes bucketing, so the ratio light can be watched going red.
        /// </summary>
        /// <remarks>
        /// Implemented by resolving simulated users under a skewed identity rather than by tampering with
        /// the hash: the hash is the thing under test everywhere else in this repository and giving it a
        /// mutable back door for a demo would be a poor trade.
        /// </remarks>
        public void SetBucketingSkew(bool broken)
        {
            BucketingSkewBreakage = broken;
            _log.Log(broken ? AbLogLevel.Warning : AbLogLevel.Info,
                broken
                    ? "BREAKAGE: bucketing skewed. The exposed split will drift while every funnel rate " +
                      "stays healthy - that combination means the split itself is wrong."
                    : "bucketing skew cleared; simulate again to see the light recover");
        }

        /// <summary>
        /// Breaks or fixes exposure logging for one arm.
        /// </summary>
        /// <remarks>
        /// The fault an assignment-based ratio check would sail straight through: assignments stay a
        /// perfect 50/50 while half the data is destroyed. Measuring the ratio over exposures is what
        /// catches it, and the collapsed funnel rate in one arm is what names it.
        /// </remarks>
        public void SetExposureSkipping(bool broken)
        {
            SkipExposureBreakage = broken;
            _log.Log(broken ? AbLogLevel.Warning : AbLogLevel.Info,
                broken
                    ? "BREAKAGE: variant 'urgency' no longer logs exposures. Assignments stay 50/50; the " +
                      "SRM light should go red and that arm's funnel rate should collapse."
                    : "exposure logging restored; simulate again to see the light recover");
        }

        /// <summary>Rebuilds the Lua registry from disk.</summary>
        public void ReloadPatches()
        {
            if (_lua == null)
            {
                _log.Log(AbLogLevel.Warning, "no Lua host; variant behavior is unavailable");
                return;
            }

            _lua.Reload();
        }

        /// <summary>Prints the metrics table and the Lua registry to the log.</summary>
        public void DumpState()
        {
            _log.Log(AbLogLevel.Info, "\n" + BuildReport().Describe());

            if (_lua == null) return;

            _log.Log(AbLogLevel.Info,
                "Lua: " + _lua.LastReload.Describe() + "\npatch folder: " + _lua.PatchRoot);
        }

        /// <summary>
        /// Undoes every state the demo can be put into, in one action.
        /// </summary>
        /// <remarks>
        /// The other half of every action pair at once. Pins, the config cache, the analytics sink, the
        /// aggregate, the exposure ledger, both breakages and the QA override all go, and the server
        /// returns to the normal scenario.
        /// </remarks>
        public void ResetDemo()
        {
            ClearForcedVariant();
            SetBucketingSkew(false);
            SetExposureSkipping(false);

            _pins.Clear();
            _ledger.Clear();
            _events.Clear();
            _metrics.Clear();
            _config.ClearCache();

            _server.SetScenario(ServerScenario.Normal);
            _config.Refresh();

            _log.Log(AbLogLevel.Info,
                "demo reset: pins, cache, events, metrics, overrides and both breakages cleared");
        }

        /// <summary>Builds the metrics report the panel renders.</summary>
        public MetricsReport BuildReport() =>
            _metrics.Build(Snapshot.Config, MetricsPopulation.Analysis, _ledger);

        /// <inheritdoc />
        public void Dispose()
        {
            _server?.Dispose();
        }

        private PresentationSpec ApplyLayer(
            string layerId, SpecFieldGroup group, PresentationSpec baseline, ref string rejection)
        {
            var assignment = Resolve(Player, layerId);
            if (!assignment.IsAssigned) return baseline;
            if (_lua == null) return baseline;

            var spec = _lua.Present(
                Player, assignment, group, baseline, OfferCatalogue.AnyHasOriginalPrice, out string token);

            // The first rejection wins the strip. Two rejections at once is possible but says nothing more
            // than one does, and the log carries both.
            if (token != null && rejection == null) rejection = token;

            return spec;
        }

        private VariantAssignment Resolve(UserContext user, string layerId) =>
            _resolver.Resolve(Snapshot, SkewIfBroken(user), layerId);

        /// <summary>
        /// Returns the identity to bucket under, skewed when the breakage is on.
        /// </summary>
        /// <remarks>
        /// Prefixing the id pushes users onto different buckets in a way that correlates across the
        /// population, which is exactly what a bucketing fault looks like from the outside. The hash itself
        /// is untouched.
        /// </remarks>
        private UserContext SkewIfBroken(UserContext user)
        {
            if (!BucketingSkewBreakage) return user;

            // Every fourth user keeps their identity; the rest are forced onto a small set of ids that
            // bucket low. Murmur3 rather than GetHashCode even here - the whole repository argues that
            // GetHashCode is not a contract, and a demo helper quietly relying on it would undercut that.
            uint bucket = Core.Hashing.Murmur3.Hash32(user.UserId);
            if (bucket % 4 == 0) return user;

            return new UserContext(
                "skew-" + (bucket & 0x7), user.AccountLevel, user.Platform, user.Country);
        }

        private bool IsExposureSuppressed(VariantAssignment assignment) =>
            SkipExposureBreakage && assignment.VariantId == "urgency";

        private void Refresh() => Changed?.Invoke();
    }
}
