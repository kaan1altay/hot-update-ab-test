using System;
using System.IO;
using FairyGUI;
using HotUpdateABTest.Core;
using HotUpdateABTest.Lua;
using HotUpdateABTest.Transport;
using UnityEngine;

namespace HotUpdateABTest.Demo
{
    /// <summary>
    /// The scene component: builds the UI, wires it to the controller, and pumps the poll.
    /// </summary>
    /// <remarks>
    /// Thin on purpose. Everything with a decision in it lives in <see cref="AbTestDemoController"/>, which
    /// has no FairyGUI dependency and can therefore be driven by tests without a screen. What is left here
    /// is Unity lifecycle and view updates.
    /// </remarks>
    public sealed class AbTestDemoBehaviour : MonoBehaviour
    {
        /// <summary>Name of the FairyGUI package the demo binds to.</summary>
        public const string PackageName = "AbTestDemo";

        private UnityAbLog _log;
        private AbTestDemoController _controller;
        private LuaVariantHost _lua;
        private ConsoleView _console;
        private ShopScreenView _shop;
        private GComponent _root;
        private FairyBinder _binder;

        private bool _dirty = true;
        private bool _fallbackUi;
        private bool _shutDown;

        /// <summary>True when the console was built in code because no package could be loaded.</summary>
        public bool UsingFallbackUi { get; private set; }

        /// <summary>The console root, exposed so PlayMode tests can drive it without searching the stage.</summary>
        public GComponent ConsoleRoot => _root;

        /// <summary>The current metrics, exposed for the same reason.</summary>
        public Core.Telemetry.MetricsReport Report => _controller.BuildReport();

        private void Start()
        {
            _log = new UnityAbLog("ABTest", (level, message) => _console?.AppendLog(level, message));
            _binder = new FairyBinder(_log);

            _lua = new LuaVariantHost(LuaPatchLoader.Default(_log), _log);

            _controller = new AbTestDemoController(_log, SystemClock.Instance, _lua, ReadShippedDefaults());
            _controller.Changed += () => _dirty = true;

            BuildUi();

            _controller.Start();
            _controller.MarkShopSeen();

            _log.Log(AbLogLevel.Info,
                UsingFallbackUi
                    ? "no FairyGUI package found; the console is the programmatic fallback"
                    : "bound to FairyGUI package '" + PackageName + "'");
            _log.Log(AbLogLevel.Info, "drop Lua patches into: " + _lua.PatchRoot);
        }

        private void Update()
        {
            _controller?.Tick();

            if (!_dirty) return;
            _dirty = false;
            Repaint();
        }

        private void OnApplicationQuit() => Shutdown();

        private void OnDestroy() => Shutdown();

        /// <summary>Releases the socket, the Lua VM and the UI, once, whatever caused the teardown.</summary>
        /// <remarks>
        /// OnDestroy covers leaving play mode and an Editor domain reload; OnApplicationQuit covers a
        /// player closing. Both route here and the whole thing is idempotent, because an orphaned
        /// HttpListener still holding :8757 would make the next run silently unable to start its server -
        /// which is exactly the kind of thing that surfaces halfway through recording.
        /// </remarks>
        private void Shutdown()
        {
            if (_shutDown) return;
            _shutDown = true;

            _controller?.Dispose();
            _lua?.Dispose();

            if (_root != null)
            {
                GRoot.inst.RemoveChild(_root);
                _root.Dispose();
                _root = null;
            }
        }

        private void BuildUi()
        {
            GRoot.inst.SetContentScaleFactor(
                DemoUiFactory.Width, DemoUiFactory.Height, UIContentScaler.ScreenMatchMode.MatchWidthOrHeight);

            _root = TryCreateFromPackage("ConsoleMain");
            UsingFallbackUi = _root == null;
            _fallbackUi = UsingFallbackUi;
            if (UsingFallbackUi) _root = DemoUiFactory.CreateConsole();

            // UIPackage.CreateObject does not name what it creates, and the binder quotes the name in every
            // message it logs. Naming it here keeps those messages meaningful on both paths.
            _root.name = "ConsoleMain";

            GRoot.inst.AddChild(_root);

            _console = new ConsoleView(_root, _binder, UsingFallbackUi);
            _console.ButtonPressed += OnButton;

            var screen = TryCreateFromPackage("ShopScreen") ?? DemoUiFactory.CreateShopScreen();
            screen.name = "ShopScreen";
            _console.DeviceContainer?.AddChild(screen);

            _shop = new ShopScreenView(screen, _binder, CreateOfferCard, OnOfferPressed);

            ValidateBinding(screen);
        }

        private void OnButton(string name)
        {
            _controller.OnButton(name);

            // Every button can change what the shop shows - a config swap, a forced variant, a patch
            // reload - so the screen is re-resolved rather than each handler remembering to.
            _dirty = true;
        }

        private void OnOfferPressed(string offerId) => _controller.Convert(offerId);

        private static GComponent CreateOfferCard() =>
            TryCreateFromPackage("OfferCard") ?? DemoUiFactory.CreateOfferCard();

        /// <summary>
        /// Checks the whole bound tree once and reports every missing name in a single message.
        /// </summary>
        /// <remarks>
        /// Every failed lookup is a name mistyped or a publish forgotten, and the symptom is a dead button
        /// that looks like a working one. Reporting the first failure would mean finding one typo per run;
        /// checking at each use site would mean finding them one interaction at a time. One message with
        /// all of them is the only version that gets fixed in a single pass.
        /// </remarks>
        private void ValidateBinding(GComponent screen)
        {
            var report = UiValidator.Validate(
                _root,
                screen,
                _fallbackUi ? DemoUiFactory.CreateMetricsRow() : TryCreateFromPackage("MetricsRow"),
                _fallbackUi ? DemoUiFactory.CreateLogRow() : TryCreateFromPackage("LogRow"),
                _shop?.SampleCard);

            _log.Log(report.IsComplete ? AbLogLevel.Info : AbLogLevel.Error, report.Describe());
        }

        private void Repaint()
        {
            if (_controller == null || _console == null) return;

            var spec = _controller.RenderShop();
            _shop?.Apply(spec, _controller.LastRejectionReason);

            _console.SetStatus(_controller.Snapshot, DescribeServer(), DescribeScenario());
            _console.SetForcedBanner(_controller.IsForced, _controller.ForcedDescription ?? "");
            _console.SetMetrics(_controller.BuildReport());

            _console.SetToggle("btnServerToggle", _controller.Server.IsRunning);
            _console.SetToggle("btnInjectSkew", _controller.BucketingSkewBreakage);
            _console.SetToggle("btnSkipExposure", _controller.SkipExposureBreakage);
        }

        private string DescribeServer()
        {
            var server = _controller.Server;
            return server.IsRunning ? "server :" + server.Port : "server stopped";
        }

        private string DescribeScenario() =>
            "scenario " + LocalConfigServer.Describe(_controller.Server.Scenario);

        /// <summary>
        /// Creates a component from the package, loading the package on first use.
        /// </summary>
        /// <remarks>
        /// <c>UIPackage.AddPackage</c> throws rather than returning null when a package is missing, so each
        /// candidate path is pre-checked for its <c>_fui.bytes</c> before it is attempted. Returning null
        /// lets the caller fall back rather than making a missing package fatal.
        /// </remarks>
        private static GComponent TryCreateFromPackage(string componentName)
        {
            try
            {
                if (UIPackage.GetByName(PackageName) == null && !LoadPackage()) return null;

                // Ask whether the component exists before asking for it. CreateObject logs a Unity error
                // for a missing resource rather than returning null quietly, which turns "this component
                // is not drawn yet" - a legitimate state while the package is being authored - into red
                // console output and a failed PlayMode test. Same pre-check pattern as the package load.
                var package = UIPackage.GetByName(PackageName);
                if (package == null || package.GetItemByName(componentName) == null) return null;

                return UIPackage.CreateObject(PackageName, componentName) as GComponent;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool LoadPackage()
        {
            string[] candidates =
            {
                "Assets/FairyGUI-Packages/" + PackageName,
                PackageName,
                "UI/" + PackageName
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];

                try
                {
                    if (candidate.StartsWith("Assets/", StringComparison.Ordinal))
                    {
#if UNITY_EDITOR
                        if (!File.Exists(candidate + "_fui.bytes")) continue;
                        UIPackage.AddPackage(candidate);
                        return true;
#else
                        continue;
#endif
                    }

                    if (Resources.Load<TextAsset>(candidate + "_fui") == null) continue;

                    UIPackage.AddPackage(candidate);
                    return true;
                }
                catch (Exception)
                {
                    // Try the next candidate. A package that exists but fails to parse is the same problem
                    // as one that is absent, as far as the fallback is concerned.
                }
            }

            return false;
        }

        private static string ReadShippedDefaults() => ShippedDefaults.Load(new UnityAbLog("ABTest"));
    }
}
