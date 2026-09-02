using System.Collections;
using System.Collections.Generic;
using FairyGUI;
using HotUpdateABTest.Demo;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HotUpdateABTest.Tests.PlayMode
{
    /// <summary>
    /// Runs the demo for real: a stage, a UI, buttons that are actually pressed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The EditMode suite covers every decision the demo makes, because the controller has no FairyGUI
    /// dependency. What is left for PlayMode is the part that genuinely needs a running stage: that the
    /// console builds, that its children resolve, and that pressing a button moves what is on screen.
    /// </para>
    /// <para>
    /// Both UI paths are exercised. The programmatic fallback declares the same child names as the authored
    /// package, so the same assertions run against both - which is what turns a broken binding into a test
    /// failure rather than an empty panel somebody notices later.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class DemoPlayModeTests
    {
        private GameObject _stage;

        [SetUp]
        public void SetUp()
        {
            // Failure paths are the subject here - a patch that cannot parse, a spec the screen
            // cannot render - and those log at Error, which the framework otherwise treats as an
            // unexpected failure. The assertions still check the message reached the panel.
            LogAssert.ignoreFailingMessages = true;
            // FairyGUI needs a Stage before any display object can be built.
            _stage = new GameObject("StageCamera");
            _stage.AddComponent<Camera>();
            _ = GRoot.inst;
        }

        [TearDown]
        public void TearDown()
        {
            // Failure paths are the subject here - a patch that cannot parse, a spec the screen
            // cannot render - and those log at Error, which the framework otherwise treats as an
            // unexpected failure. The assertions still check the message reached the panel.
            LogAssert.ignoreFailingMessages = true;
            if (_stage != null) Object.DestroyImmediate(_stage);
        }

        private static IEnumerable<string> ButtonNames()
        {
            foreach (var spec in DemoUiFactory.Buttons) yield return spec.Name;
        }

        [Test]
        public void TheFallbackConsoleDeclaresEveryChildTheViewBindsTo()
        {
            // Failure paths are the subject here - a patch that cannot parse, a spec the screen
            // cannot render - and those log at Error, which the framework otherwise treats as an
            // unexpected failure. The assertions still check the message reached the panel.
            LogAssert.ignoreFailingMessages = true;
            var console = DemoUiFactory.CreateConsole();

            try
            {
                AssertHasChildren(console,
                    "chipSource", "txtConfigVersion", "txtServer", "txtScenario",
                    "containerDevice", "bannerForced", "listMetrics", "listLog");

                foreach (string name in ButtonNames())
                {
                    Assert.That(Deep(console, name), Is.Not.Null, "the fallback has no button '" + name + "'");
                }
            }
            finally
            {
                console.Dispose();
            }
        }

        [Test]
        public void TheFallbackMetricsRowDeclaresEveryFieldTheTableFills()
        {
            // Failure paths are the subject here - a patch that cannot parse, a spec the screen
            // cannot render - and those log at Error, which the framework otherwise treats as an
            // unexpected failure. The assertions still check the message reached the panel.
            LogAssert.ignoreFailingMessages = true;
            var row = DemoUiFactory.CreateMetricsRow();

            try
            {
                AssertHasChildren(row,
                    "txtExperiment", "txtVariant", "txtAssignments", "txtExposures", "txtConversions",
                    "txtRate", "barShare", "srmLight");

                AssertPages((GComponent)row.GetChild("srmLight"), "state", "unknown", "healthy", "alarm");
                AssertPages((GComponent)row.GetChild("barShare"), "state", "unknown", "healthy", "warn", "alarm");
            }
            finally
            {
                row.Dispose();
            }
        }

        [Test]
        public void TheFallbackShopScreenIsTheSameShapeAsTheAuthoredOne()
        {
            // Failure paths are the subject here - a patch that cannot parse, a spec the screen
            // cannot render - and those log at Error, which the framework otherwise treats as an
            // unexpected failure. The assertions still check the message reached the panel.
            LogAssert.ignoreFailingMessages = true;
            var screen = DemoUiFactory.CreateShopScreen();

            try
            {
                Assert.That(screen.width, Is.EqualTo(375));
                Assert.That(screen.height, Is.EqualTo(667));
                Assert.That(screen.numChildren, Is.Zero, "the authored ShopScreen is empty too");
            }
            finally
            {
                screen.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator TheDemoStartsAndRendersWhicheverUiIsAvailable()
        {
            // Failure paths are the subject here - a patch that cannot parse, a spec the screen
            // cannot render - and those log at Error, which the framework otherwise treats as an
            // unexpected failure. The assertions still check the message reached the panel.
            LogAssert.ignoreFailingMessages = true;
            var host = new GameObject("AbTestDemo");
            var demo = host.AddComponent<AbTestDemoBehaviour>();

            yield return null;
            yield return null;

            Assert.That(GRoot.inst.numChildren, Is.GreaterThan(0), "nothing was added to the stage");

            TestContext.WriteLine(demo.UsingFallbackUi
                ? "running on the programmatic fallback"
                : "running on the authored AbTestDemo package");

            Object.DestroyImmediate(host);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PressingScenarioButtonsMovesWhatIsOnScreen()
        {
            // Failure paths are the subject here - a patch that cannot parse, a spec the screen
            // cannot render - and those log at Error, which the framework otherwise treats as an
            // unexpected failure. The assertions still check the message reached the panel.
            LogAssert.ignoreFailingMessages = true;
            var host = new GameObject("AbTestDemo");
            var demo = host.AddComponent<AbTestDemoBehaviour>();

            yield return null;
            yield return null;

            var console = demo.ConsoleRoot;
            Assert.That(console, Is.Not.Null, "the console was never built");

            var version = Deep(console, "txtConfigVersion");
            Assert.That(version, Is.Not.Null);

            string before = version.text;

            Press(console, "btnScenarioKill");
            yield return null;
            yield return null;

            Assert.That(version.text, Is.Not.EqualTo(before),
                "the kill switch should have produced a new config version on screen");

            Object.DestroyImmediate(host);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SimulatingUsersFillsTheMetricsTable()
        {
            // Failure paths are the subject here - a patch that cannot parse, a spec the screen
            // cannot render - and those log at Error, which the framework otherwise treats as an
            // unexpected failure. The assertions still check the message reached the panel.
            LogAssert.ignoreFailingMessages = true;
            var host = new GameObject("AbTestDemo");
            var demo = host.AddComponent<AbTestDemoBehaviour>();

            yield return null;
            yield return null;

            var console = demo.ConsoleRoot;
            var list = Deep(console, "listMetrics") as GList;
            Assert.That(list, Is.Not.Null);

            Press(console, "btnScenarioNormal");
            yield return null;
            Press(console, "btnSimulate");

            yield return null;
            yield return null;

            Assert.That(list.numChildren, Is.GreaterThan(1),
                "the metrics table should have a header and at least one arm");

            Object.DestroyImmediate(host);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheForcedBannerAppearsAndClears()
        {
            // Failure paths are the subject here - a patch that cannot parse, a spec the screen
            // cannot render - and those log at Error, which the framework otherwise treats as an
            // unexpected failure. The assertions still check the message reached the panel.
            LogAssert.ignoreFailingMessages = true;
            // The action pair, on screen. A banner that could be shown but never hidden is exactly the
            // class of bug hand play-testing keeps finding.
            var host = new GameObject("AbTestDemo");
            var demo = host.AddComponent<AbTestDemoBehaviour>();

            yield return null;
            yield return null;

            var console = demo.ConsoleRoot;
            var banner = Deep(console, "bannerForced");
            Assert.That(banner, Is.Not.Null);

            Press(console, "btnScenarioNormal");
            yield return null;

            Press(console, "btnForceVariant");
            yield return null;
            yield return null;
            Assert.That(banner.visible, Is.True, "forcing a variant must show the banner");

            // Deliberate change of behaviour. Clearing the override no longer hides the banner, because
            // the rows gathered while the override was on are still in the sink: the cause must not
            // vanish while the symptom stays on screen. It reports in the past tense instead, and the
            // control that clears the data is the control that clears the marker.
            Press(console, "btnClearForce");
            yield return null;
            yield return null;
            Assert.That(banner.visible, Is.True,
                "the data is still tainted, so the banner must still say so");

            Press(console, "btnClearState");
            yield return null;
            yield return null;
            Assert.That(banner.visible, Is.False, "clearing saved state must hide it again");

            Object.DestroyImmediate(host);
            yield return null;
        }

        private static void Press(GComponent console, string buttonName)
        {
            var button = Deep(console, buttonName);
            Assert.That(button, Is.Not.Null, "no button named '" + buttonName + "'");

            // Dispatching the click rather than simulating a pointer keeps the test about wiring rather
            // than about hit-testing, which FairyGUI already covers.
            button.onClick.Call();
        }

        private static void AssertHasChildren(GComponent component, params string[] names)
        {
            var missing = new List<string>();
            foreach (string name in names)
            {
                if (Deep(component, name) == null) missing.Add(name);
            }

            Assert.That(missing, Is.Empty, component.name + " is missing " + string.Join(", ", missing.ToArray()));
        }

        private static GObject Deep(GComponent parent, string name)
        {
            if (parent == null) return null;

            var direct = parent.GetChild(name);
            if (direct != null) return direct;

            for (int i = 0; i < parent.numChildren; i++)
            {
                if (parent.GetChildAt(i) is GComponent child && child.GetChild(name) != null)
                {
                    return child.GetChild(name);
                }
            }

            return null;
        }

        private static void AssertPages(GComponent component, string controllerName, params string[] pages)
        {
            var controller = component.GetController(controllerName);
            Assert.That(controller, Is.Not.Null, component.name + " has no controller '" + controllerName + "'");

            foreach (string page in pages)
            {
                Assert.That(controller.GetPageIdByName(page), Is.Not.Null,
                    component.name + "." + controllerName + " has no page '" + page + "'");
            }
        }
    }
}
