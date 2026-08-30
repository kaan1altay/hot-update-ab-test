using System.Collections;
using FairyGUI;
using HotUpdateABTest.Core;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Presentation;
using HotUpdateABTest.Core.Telemetry;
using HotUpdateABTest.Demo;
using HotUpdateABTest.Lua;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HotUpdateABTest.Tests.PlayMode
{
    /// <summary>Collects log lines so a test can assert what was said.</summary>
    /// <remarks>
    /// A second copy of the EditMode fixture's helper rather than a shared one: the two test assemblies
    /// cannot reference each other, and a five-line recorder is not worth a third assembly to share.
    /// </remarks>
    internal sealed class ListLog : IAbLog
    {
        public System.Collections.Generic.List<string> Lines { get; } =
            new System.Collections.Generic.List<string>();

        public void Log(AbLogLevel level, string message) => Lines.Add(level + ": " + message);

        public string All => string.Join("\n", Lines.ToArray());
    }

    /// <summary>
    /// Proves the claim the whole repository rests on, at the one place it could quietly break: the render
    /// path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exposure is logged when a user sees the treated surface, not when a variant is resolved. Everything
    /// downstream depends on it - the ratio check, the conversion denominator, the funnel - and the layer
    /// most likely to break it is this one. A screen binder that logged an exposure while resolving a spec
    /// in order to decide what to draw would manufacture exposures from a prefetch, an off-screen build, or
    /// a resolve done purely to render a debug panel, and every number would drift low for reasons nobody
    /// could see.
    /// </para>
    /// <para>
    /// The EditMode suite already asserts that <c>Resolve</c> logs nothing. What is left, and what only a
    /// running stage can show, is that <i>building the actual screen</i> logs nothing either.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ExposureAtViewTimeTests
    {
        private GameObject _stage;
        private ListLog _log;
        private LuaVariantHost _lua;
        private InMemoryAnalyticsSink _sink;

        [SetUp]
        public void SetUp()
        {
            _stage = new GameObject("StageCamera");
            _stage.AddComponent<Camera>();
            _ = GRoot.inst;

            _log = new ListLog();
            _lua = new LuaVariantHost(LuaPatchLoader.Default(_log), _log);
            _sink = new InMemoryAnalyticsSink();
        }

        [TearDown]
        public void TearDown()
        {
            _lua?.Dispose();
            if (_stage != null) Object.DestroyImmediate(_stage);
        }

        private static UserContext Player() =>
            new UserContext("player-local", accountLevel: 7, platform: "editor", country: "TR");

        [Test]
        public void ResolvingAndBuildingTheWholeShopScreenLogsNothing()
        {
            // The screen is created, bound, and rendered from a real resolved spec - everything short of a
            // player actually seeing it. The sink must stay completely empty.
            var controller = new AbTestDemoController(_log, SystemClock.Instance, _lua, preferHttp: false);

            try
            {
                controller.Start();

                var screen = DemoUiFactory.CreateShopScreen();
                var view = new ShopScreenView(
                    screen, new FairyBinder(_log), DemoUiFactory.CreateOfferCard, _ => { });

                for (int i = 0; i < 20; i++)
                {
                    view.Apply(controller.RenderShop(), controller.LastRejectionToken);
                }

                Assert.That(_sink.TotalRecorded, Is.Zero,
                    "building and rendering the screen must not log anything");

                screen.Dispose();
            }
            finally
            {
                controller.Dispose();
            }
        }

        [Test]
        public void OnlyMarkingTheScreenSeenProducesAnExposure()
        {
            // The other half. Without this, a view layer that logged nothing at all would also pass.
            var ledger = new ExposureLedger();
            var resolver = new ExperimentResolver(new InMemoryAssignmentStore());
            var tracker = new ExposureTracker(ledger, _sink, SystemClock.Instance, resolver);

            var variant = new VariantDef("control", 5000, "shop.pricing_cta.control");
            var experiment = new ExperimentDef(
                "exp_pricing_cta", "pricing_cta", ExperimentStatus.Running, "salt",
                BucketRange.Full, StickinessPolicy.StickyAfterExposure,
                new[] { variant });

            var assignment = VariantAssignment.Assigned(
                "pricing_cta", experiment, variant, AssignmentSource.Bucketed, 1, 2, "v1");

            // Render as many times as you like: still nothing.
            var screen = DemoUiFactory.CreateShopScreen();
            var view = new ShopScreenView(
                screen, new FairyBinder(_log), DemoUiFactory.CreateOfferCard, _ => { });

            for (int i = 0; i < 10; i++) view.Apply(PresentationSpec.Baseline, null);
            Assert.That(_sink.TotalRecorded, Is.Zero);

            tracker.MarkExposed(Player(), assignment, new SessionId("s1"));

            Assert.That(_sink.CountOf(AnalyticsEventKind.Exposure), Is.EqualTo(1));
            screen.Dispose();
        }

        [UnityTest]
        public IEnumerator TheLiveDemoLogsOneExposurePerLayerNoMatterHowOftenItRepaints()
        {
            // The whole thing running: the demo marks the shop seen once at startup, then repaints on every
            // button press and every config change. Those repaints resolve, which must stay free.
            var host = new GameObject("AbTestDemo");
            var demo = host.AddComponent<AbTestDemoBehaviour>();

            yield return null;
            yield return null;

            var console = demo.ConsoleRoot;
            Assert.That(console, Is.Not.Null);

            long afterStartup = ExposedUsers(demo);

            // Force a lot of repaints without the player seeing anything new.
            for (int i = 0; i < 5; i++)
            {
                Press(console, "btnRefresh");
                yield return null;
                yield return null;
            }

            Assert.That(ExposedUsers(demo), Is.EqualTo(afterStartup),
                "repainting must not manufacture exposures");

            Object.DestroyImmediate(host);
            yield return null;
        }

        private static long ExposedUsers(AbTestDemoBehaviour demo)
        {
            long total = 0;
            foreach (var experiment in demo.Report.Experiments)
            {
                foreach (var variant in experiment.Variants) total += variant.UsersExposed;
            }

            return total;
        }

        private static void Press(GComponent console, string buttonName)
        {
            var button = UiValidator.Deep(console, buttonName);
            Assert.That(button, Is.Not.Null, "no button named '" + buttonName + "'");
            button.onClick.Call();
        }
    }
}
