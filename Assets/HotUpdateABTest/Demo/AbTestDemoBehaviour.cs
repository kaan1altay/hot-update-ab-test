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

        /// <summary>True when the console was built in code because no package could be loaded.</summary>
        public bool UsingFallbackUi { get; private set; }

        /// <summary>The console root, exposed so PlayMode tests can drive it without searching the stage.</summary>
        public GComponent ConsoleRoot => _root;

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

        private void OnDestroy()
        {
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
            if (UsingFallbackUi) _root = DemoUiFactory.CreateConsole();

            // UIPackage.CreateObject does not name what it creates, and the binder quotes the name in every
            // message it logs. Naming it here keeps those messages meaningful on both paths.
            _root.name = "ConsoleMain";

            GRoot.inst.AddChild(_root);

            _console = new ConsoleView(_root, _binder, UsingFallbackUi);
            _console.ButtonPressed += OnButton;

            var screen = TryCreateFromPackage("ShopScreen") ?? DemoUiFactory.CreateShopScreen();
            _console.DeviceContainer?.AddChild(screen);

            _shop = new ShopScreenView(screen, _binder, OnOfferPressed);
        }

        private void OnButton(string name)
        {
            _controller.OnButton(name);

            // Every button can change what the shop shows - a config swap, a forced variant, a patch
            // reload - so the screen is re-resolved rather than each handler remembering to.
            _dirty = true;
        }

        private void OnOfferPressed(string offerId) => _controller.Convert(offerId);

        private void Repaint()
        {
            if (_controller == null || _console == null) return;

            _shop?.Apply(_controller.RenderShop());

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
