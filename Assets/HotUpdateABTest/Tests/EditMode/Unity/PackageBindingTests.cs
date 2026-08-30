using System.Collections.Generic;
using System.IO;
using FairyGUI;
using HotUpdateABTest.Demo;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Unity
{
    /// <summary>
    /// Loads the real published FairyGUI package and asserts everything the code binds to is there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The binder degrades gracefully - a missing child produces one warning and a part of the screen that
    /// does not update. That is the right runtime behaviour and exactly the wrong thing to rely on for
    /// correctness, because a rename would show up as a quietly blank panel that somebody notices days
    /// later. These tests turn that into a named failure at the moment the package is republished.
    /// </para>
    /// <para>
    /// Skips rather than fails when the package is absent, so a fresh clone that has not published the
    /// FairyGUI project yet still gets a green suite.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class PackageBindingTests
    {
        private const string PackagePath = "Assets/FairyGUI-Packages/AbTestDemo";
        private const string PackageName = "AbTestDemo";

        private bool _loadedHere;

        [OneTimeSetUp]
        public void LoadPackage()
        {
            if (UIPackage.GetByName(PackageName) != null) return;
            if (!File.Exists(PackagePath + "_fui.bytes")) return;

            UIPackage.AddPackage(PackagePath);
            _loadedHere = true;
        }

        [OneTimeTearDown]
        public void UnloadPackage()
        {
            if (_loadedHere) UIPackage.RemovePackage(PackageName);
        }

        private static GComponent Create(string componentName)
        {
            if (UIPackage.GetByName(PackageName) == null)
            {
                Assert.Ignore("the AbTestDemo package is not published; skipping the binding checks");
            }

            var component = UIPackage.CreateObject(PackageName, componentName) as GComponent;
            Assert.That(component, Is.Not.Null, "the package has no component named '" + componentName + "'");
            return component;
        }

        private static void AssertChildren(GComponent component, string owner, params string[] names)
        {
            var missing = new List<string>();
            foreach (string name in names)
            {
                if (Deep(component, name) == null) missing.Add(name);
            }

            Assert.That(missing, Is.Empty,
                owner + " is missing " + string.Join(", ", missing.ToArray()) +
                ". Either the package was renamed or docs/PACKAGE_SPEC.md is out of date.");
        }

        private static GObject Deep(GComponent parent, string name)
        {
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
            Assert.That(controller, Is.Not.Null,
                component.name + " has no controller named '" + controllerName + "'");

            foreach (string page in pages)
            {
                Assert.That(controller.GetPageIdByName(page), Is.Not.Null,
                    component.name + "." + controllerName + " has no page named '" + page + "'");
            }
        }

        [Test]
        public void ConsoleMainHasEveryChildTheViewBindsTo()
        {
            var console = Create("ConsoleMain");

            AssertChildren(console, "ConsoleMain",
                "txtTitle", "chipSource", "txtConfigVersion", "txtServer", "txtScenario",
                "containerDevice", "bannerForced", "listMetrics", "listLog");
        }

        [Test]
        public void ConsoleMainHasEveryButtonTheControllerHandles()
        {
            // Driven by the same list the fallback builds from, so the two cannot drift apart.
            var console = Create("ConsoleMain");

            var names = new List<string>();
            foreach (var spec in DemoUiFactory.Buttons) names.Add(spec.Name);

            AssertChildren(console, "ConsoleMain", names.ToArray());
        }

        [Test]
        public void MetricsRowHasEveryFieldTheTableFills()
        {
            var row = Create("MetricsRow");

            AssertChildren(row, "MetricsRow",
                "txtExperiment", "txtVariant", "txtAssignments", "txtExposures", "txtConversions",
                "txtRate", "barShare", "srmLight");
        }

        [Test]
        public void TheControllerPagesTheCodeSelectsAllExist()
        {
            // Selected by name, never by index: barShare declares its pages as 4,unknown,0,green,... so the
            // page whose id is 4 sits at index 0, and anything positional picks the wrong colour.
            var row = Create("MetricsRow");

            AssertPages((GComponent)row.GetChild("srmLight"), "state", "unknown", "healthy", "alarm");
            AssertPages((GComponent)row.GetChild("barShare"), "state", "unknown", "green", "yellow", "red");

            var console = Create("ConsoleMain");
            AssertPages((GComponent)Deep(console, "chipSource"), "state", "live", "lkg", "defaults", "none");
            AssertPages((GComponent)Deep(console, "bannerForced"), "state", "hidden", "shown");

            AssertPages(Create("LogRow"), "type", "log", "warn", "err");
            AssertPages(Create("ToggleButton"), "state", "off", "on");
        }

        [Test]
        public void ShopScreenIsTheExpectedSize()
        {
            // Its interior is authored later; the container itself is what the device frame holds.
            var screen = Create("ShopScreen");

            Assert.That(screen.width, Is.EqualTo(375));
            Assert.That(screen.height, Is.EqualTo(667));
        }

        [Test]
        public void TheDeviceContainerIsPhoneShaped()
        {
            var device = Deep(Create("ConsoleMain"), "containerDevice");

            Assert.That(device, Is.Not.Null);
            Assert.That(device.width, Is.EqualTo(375));
            Assert.That(device.height, Is.EqualTo(667));
        }

        // The matching assertions for the programmatic fallback live in the PlayMode suite: building
        // FairyGUI display objects needs a stage, which EditMode does not have.
    }
}
