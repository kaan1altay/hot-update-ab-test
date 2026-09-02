using System.Collections.Generic;
using System.Collections;
using System.IO;
using FairyGUI;
using HotUpdateABTest.Core.Config;
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
    /// <summary>
    /// Reproduces the defects the first hand play-test found, against the real authored package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of these needs the published <c>AbTestDemo</c> package rather than the programmatic
    /// fallback, because the fallback has no gears, no <c>GProgressBar</c> and no groups - which is exactly
    /// why the existing suite was green while the screen was wrong. A fallback that is simpler than the
    /// thing it stands in for cannot reproduce the thing it stands in for.
    /// </para>
    /// <para>
    /// That is the lesson worth recording: the two paths were asserted to have the same <i>names</i>, and
    /// nothing asserted they behave the same. These tests close that gap for the four behaviours that broke.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class PlayTestRegressionTests
    {
        private const string PackagePath = "Assets/FairyGUI-Packages/AbTestDemo";
        private const string PackageName = "AbTestDemo";

        private GameObject _stage;
        private bool _loadedHere;

        [SetUp]
        public void SetUp()
        {
            _stage = new GameObject("StageCamera");
            _stage.AddComponent<Camera>();
            _ = GRoot.inst;

            if (UIPackage.GetByName(PackageName) == null && File.Exists(PackagePath + "_fui.bytes"))
            {
                UIPackage.AddPackage(PackagePath);
                _loadedHere = true;
            }

            if (UIPackage.GetByName(PackageName) == null)
            {
                Assert.Ignore("the AbTestDemo package is not published; these need the authored components");
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (_loadedHere) UIPackage.RemovePackage(PackageName);
            _loadedHere = false;

            if (_stage != null) Object.DestroyImmediate(_stage);
        }

        private static GComponent Authored(string component) =>
            UIPackage.CreateObject(PackageName, component) as GComponent;

        // --- Finding 1: children positioned against the wrong card size ---------------------------------

        [Test]
        public void OfferCardChildrenStayInsideTheCardInBothLayouts()
        {
            // The authored gears use positionsInPercent, so a child's position is (percent x current parent
            // size) at the moment the page is applied. The percentages were computed against the card's
            // base 335x96, so applying the grid page while the card is already 163x190 multiplies every
            // offset: txtPrice's 1.479 lands at 281 in a 190-tall card instead of 142.
            var screen = Authored("ShopScreen");
            var view = new ShopScreenView(screen, new FairyBinder(new ListLog()), () => Authored("OfferCard"), _ => { });

            foreach (var layout in new[] { OfferLayout.List, OfferLayout.Grid })
            {
                view.Apply(new PresentationSpec(layout, PriceStyle.Discounted, "SALE", "Buy"), null);

                var card = view.SampleCard;
                Assert.That(card, Is.Not.Null);

                foreach (string childName in new[] { "txtName", "txtPrice", "txtOriginal", "imgIcon" })
                {
                    var child = card.GetChild(childName);
                    Assert.That(child, Is.Not.Null, childName);

                    Assert.That(child.y, Is.InRange(-4f, card.height),
                        layout + ": '" + childName + "' sits at y=" + child.y + " in a card " +
                        card.height + " tall, so it renders outside its own card");

                    Assert.That(child.x, Is.InRange(-4f, card.width),
                        layout + ": '" + childName + "' sits at x=" + child.x + " in a card " +
                        card.width + " wide");
                }
            }

            screen.Dispose();
        }

        [Test]
        public void EveryOfferCardShowsItsOwnNameAndPrice()
        {
            // The screenshot showed names on the bottom row only and no prices at all. Whatever the
            // mechanism, each of the four cards must carry the text for its own offer.
            var screen = Authored("ShopScreen");
            var view = new ShopScreenView(screen, new FairyBinder(new ListLog()), () => Authored("OfferCard"), _ => { });

            view.Apply(new PresentationSpec(OfferLayout.Grid, PriceStyle.Discounted, "SALE", "Buy"), null);

            var list = screen.GetChild("listOffers") as GList;
            Assert.That(list, Is.Not.Null);
            Assert.That(list.numChildren, Is.EqualTo(OfferCatalogue.All.Count),
                "one card per offer, no duplication");

            for (int i = 0; i < list.numChildren; i++)
            {
                var card = list.GetChildAt(i) as GComponent;
                var offer = OfferCatalogue.All[i];

                Assert.That(card.GetChild("txtName").text, Is.EqualTo(offer.Title), "card " + i + " name");
                Assert.That(card.GetChild("txtPrice").text, Is.EqualTo(offer.PriceText), "card " + i + " price");
            }

            screen.Dispose();
        }

        // --- Second pass, finding 5: list layout offsets every second card ------------------------------

        [Test]
        public void ListLayoutPutsEveryCardOnTheSameLeftEdge()
        {
            // The play-test saw cards alternating: one flush left, the next shifted right. list is the
            // baseline layout - what renders with no experiment applied and after every rejected spec -
            // so this is the screen the kill-switch and fallback shots are made of.
            //
            // Reached the way the demo reaches it. Play mode opens in grid, so list is only ever arrived
            // at by transitioning out of grid, and a fresh-built list was never what anyone looked at.
            var screen = Authored("ShopScreen");
            var view = new ShopScreenView(screen, new FairyBinder(new ListLog()), () => Authored("OfferCard"), _ => { });

            var list = screen.GetChild("listOffers") as GList;
            Assert.That(list, Is.Not.Null);

            // Flush between the two, or the grid pass never runs and there is no stale x to inherit -
            // which is exactly why the first version of this test passed while the demo was broken.
            view.Apply(new PresentationSpec(OfferLayout.Grid, PriceStyle.Plain, null, "Buy"), null);
            list.EnsureBoundsCorrect();

            view.Apply(new PresentationSpec(OfferLayout.List, PriceStyle.Plain, null, "Buy"), null);
            list.EnsureBoundsCorrect();

            Assert.That(list.numChildren, Is.GreaterThan(1), "need several cards to see an alternation");

            var first = list.GetChildAt(0);
            for (int i = 1; i < list.numChildren; i++)
            {
                var card = list.GetChildAt(i);
                Assert.That(card.x, Is.EqualTo(first.x).Within(0.5f),
                    "card " + i + " sits at x=" + card.x + " while card 0 sits at x=" + first.x +
                    "; in a single-column list every card shares one left edge. " + Columns(list));
            }
        }

        [Test]
        public void ListLayoutGivesEveryCardTheFullWidthOfTheList()
        {
            // The width is what the centring acts on: a card left at its grid width in a centre-aligned
            // list is offset by half the difference. Asserting the width separately says which of the two
            // is wrong when this fails.
            var screen = Authored("ShopScreen");
            var view = new ShopScreenView(screen, new FairyBinder(new ListLog()), () => Authored("OfferCard"), _ => { });

            var list = screen.GetChild("listOffers") as GList;

            view.Apply(new PresentationSpec(OfferLayout.Grid, PriceStyle.Plain, null, "Buy"), null);
            list.EnsureBoundsCorrect();

            view.Apply(new PresentationSpec(OfferLayout.List, PriceStyle.Plain, null, "Buy"), null);
            list.EnsureBoundsCorrect();

            for (int i = 0; i < list.numChildren; i++)
            {
                Assert.That(list.GetChildAt(i).width, Is.EqualTo(335f).Within(0.5f),
                    "card " + i + " is " + list.GetChildAt(i).width + " wide. " + Columns(list));
            }
        }

        /// <summary>Names where every card actually sits, so a failure says the shape of the wrongness.</summary>
        private static string Columns(GList list)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("layout=").Append(list.layout)
              .Append(" align=").Append(list.align)
              .Append(" listWidth=").Append(list.width)
              .Append(" columnGap=").Append(list.columnGap)
              .Append(" cards:");

            for (int i = 0; i < list.numChildren; i++)
            {
                var c = list.GetChildAt(i);
                sb.Append(" [").Append(i).Append("] x=").Append(c.x).Append(" y=").Append(c.y)
                  .Append(" w=").Append(c.width).Append(" h=").Append(c.height);
            }

            return sb.ToString();
        }

        [UnityTest]
        public IEnumerator ListLayoutInTheRunningDemoPutsEveryCardOnTheSameLeftEdge()
        {
            // The standalone view passes this; the play-test says the running demo does not. The demo
            // nests the shop screen inside the console's device container, so the list is laid out at
            // whatever size that container gives it rather than at its authored 335. Reproduce it there.
            var host = new GameObject("AbTestDemo");
            var demo = host.AddComponent<AbTestDemoBehaviour>();

            yield return null;
            yield return null;

            // Play mode opens in grid. The kill switch is how the demo reaches its own baseline.
            Press(demo.ConsoleRoot, "btnScenarioKill");
            for (int i = 0; i < 5; i++) yield return null;
            Press(demo.ConsoleRoot, "btnRefresh");
            for (int i = 0; i < 5; i++) yield return null;

            // ConsoleMain > containerDevice > ShopScreen > listOffers is two levels down, and Deep only
            // descends one, so walk to the screen first.
            var device = UiValidator.Deep(demo.ConsoleRoot, "containerDevice") as GComponent;
            Assert.That(device, Is.Not.Null, "no device container");
            var shop = device.GetChild("ShopScreen") as GComponent;
            Assert.That(shop, Is.Not.Null, "no shop screen in the device container");

            var list = shop.GetChild("listOffers") as GList;
            Assert.That(list, Is.Not.Null, "no offer list in the running demo");
            list.EnsureBoundsCorrect();

            Assert.That(list.numChildren, Is.GreaterThan(1));

            var first = list.GetChildAt(0);
            for (int i = 1; i < list.numChildren; i++)
            {
                Assert.That(list.GetChildAt(i).x, Is.EqualTo(first.x).Within(0.5f),
                    "card " + i + " is offset from card 0. " + Columns(list));
            }

            Object.DestroyImmediate(host);
            yield return null;
        }

        // --- Finding 11: does a patch in the real folder change the real screen? ------------------------

        [UnityTest]
        public IEnumerator APatchInTheRealPatchFolderChangesTheRunningShop()
        {
            // The play-test could never get a patch to visibly apply, so the repository's headline claim
            // was the one thing nobody had confirmed by hand. Everything else that exercises Lua uses a
            // temporary baseline and a temporary patch root; this writes into the folder the demo
            // actually reads, boots the authored package, and reads the text off the button.
            //
            // Both pricing arms are registered, so the assertion does not depend on which arm the local
            // player's hash lands in.
            string root = Path.Combine(Application.persistentDataPath, LuaPatchLoader.PatchFolderName);
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "zz-probe-" + System.Guid.NewGuid().ToString("N") + ".lua");
            File.WriteAllText(file,
                "register('shop.pricing_cta.control', function(ctx) return { ctaText = 'PATCHED' } end)\n" +
                "register('shop.pricing_cta.urgency', function(ctx) return { ctaText = 'PATCHED' } end)\n");

            var host = new GameObject("AbTestDemo");
            try
            {
                var demo = host.AddComponent<AbTestDemoBehaviour>();
                yield return null;
                yield return null;

                var console = demo.ConsoleRoot;
                var device = UiValidator.Deep(console, "containerDevice") as GComponent;
                var shop = device?.GetChild("ShopScreen") as GComponent;
                Assert.That(shop, Is.Not.Null, "no shop screen");

                var cta = shop.GetChild("btnCta");
                Assert.That(cta, Is.Not.Null, "no btnCta on the authored shop screen");
                string before = CtaText(cta);

                Press(console, "btnReloadPatches");
                for (int i = 0; i < 5; i++) yield return null;

                Assert.That(CtaText(cta), Is.EqualTo("PATCHED"),
                    "the button read '" + before + "' before the reload and '" + CtaText(cta) +
                    "' after it; a patch in " + root + " did not reach the screen");
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (File.Exists(file)) File.Delete(file);
            }
        }

        [UnityTest]
        public IEnumerator DeletingThePatchAndReloadingPutsTheShippedTextBack()
        {
            // Test 20 of the play-test, which could not run until a patch applied at all. Reload rebuilds
            // the registry from the baseline up rather than applying a delta, so removing a file removes
            // its effect - the half of hot update that is easy to skip demonstrating.
            string root = Path.Combine(Application.persistentDataPath, LuaPatchLoader.PatchFolderName);
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "zz-probe-" + System.Guid.NewGuid().ToString("N") + ".lua");
            File.WriteAllText(file,
                "register('shop.pricing_cta.control', function(ctx) return { ctaText = 'PATCHED' } end)\n" +
                "register('shop.pricing_cta.urgency', function(ctx) return { ctaText = 'PATCHED' } end)\n");

            var host = new GameObject("AbTestDemo");
            try
            {
                var demo = host.AddComponent<AbTestDemoBehaviour>();
                yield return null;
                yield return null;

                var console = demo.ConsoleRoot;
                var device = UiValidator.Deep(console, "containerDevice") as GComponent;
                var shop = device.GetChild("ShopScreen") as GComponent;
                var cta = shop.GetChild("btnCta");

                Press(console, "btnReloadPatches");
                for (int i = 0; i < 5; i++) yield return null;
                Assert.That(CtaText(cta), Is.EqualTo("PATCHED"), "the patch must apply before it can revert");

                File.Delete(file);
                Press(console, "btnReloadPatches");
                for (int i = 0; i < 5; i++) yield return null;

                Assert.That(CtaText(cta), Is.Not.EqualTo("PATCHED"),
                    "removing the file and reloading must put the shipped behaviour back");
                // Is.AnyOf is not in Unity's bundled NUnit 3.5 - see the parity note in STATUS.md.
                string restored = CtaText(cta);
                Assert.That(restored == "Buy" || restored == "Claim offer", Is.True,
                    "and it must be one of the two shipped labels, not empty; it reads '" + restored + "'");
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (File.Exists(file)) File.Delete(file);
            }
        }

        private static string CtaText(GObject cta)
        {
            if (cta is GButton button) return button.title;
            if (cta is GComponent component && component.GetChild("title") != null)
                return component.GetChild("title").text;
            return cta.text;
        }

        /// <summary>The examples folder, beside Assets/ rather than inside it.</summary>
        private static string ExamplesRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "examples", "lua-patches"));

        /// <summary>The folder the running demo actually reads.</summary>
        private static string LivePatchRoot =>
            Path.Combine(Application.persistentDataPath, LuaPatchLoader.PatchFolderName);

        /// <summary>Copies one example into the live patch folder and returns the path written.</summary>
        private static string InstallExample(string fileName)
        {
            string source = Path.Combine(ExamplesRoot, fileName);
            Assert.That(File.Exists(source), Is.True, "the example is missing: " + source);

            Directory.CreateDirectory(LivePatchRoot);
            string target = Path.Combine(LivePatchRoot, "zz-e2e-" + fileName);
            File.Copy(source, target, true);
            return target;
        }

        /// <summary>
        /// Moves whatever patches a human left in the live folder out of the way, and puts them back.
        /// </summary>
        /// <remarks>
        /// These tests read the folder a person hand-tests in, which is the point - a temporary root
        /// would not prove the demo reads the folder the startup log names. But it means someone else's
        /// leftovers decide the result. That is not hypothetical: a play-test pass could never make a
        /// patch apply, and the cause was a deliberately-rejected example still sitting in this folder
        /// under a name that sorted first. Borrowing the folder and giving it back keeps both properties.
        /// </remarks>
        private sealed class BorrowedPatchFolder : System.IDisposable
        {
            private readonly List<KeyValuePair<string, string>> _moved =
                new List<KeyValuePair<string, string>>();

            public BorrowedPatchFolder()
            {
                Directory.CreateDirectory(LivePatchRoot);
                string parked = Path.Combine(LivePatchRoot, "parked-by-tests");
                Directory.CreateDirectory(parked);

                foreach (string path in Directory.GetFiles(LivePatchRoot, "*.lua"))
                {
                    string to = Path.Combine(parked, Path.GetFileName(path));
                    File.Copy(path, to, true);
                    File.Delete(path);
                    _moved.Add(new KeyValuePair<string, string>(path, to));
                }
            }

            public void Dispose()
            {
                foreach (var pair in _moved)
                {
                    try
                    {
                        File.Copy(pair.Value, pair.Key, true);
                        File.Delete(pair.Value);
                    }
                    catch (System.Exception)
                    {
                        // Leaving a copy in parked-by-tests is recoverable; throwing here would hide the
                        // real assertion failure behind a cleanup one.
                    }
                }

                try
                {
                    string parked = Path.Combine(LivePatchRoot, "parked-by-tests");
                    if (Directory.Exists(parked) && Directory.GetFiles(parked).Length == 0)
                        Directory.Delete(parked);
                }
                catch (System.Exception)
                {
                }
            }
        }

        [UnityTest]
        public IEnumerator TheFlashSaleExampleChangesTheAuthoredShopScreen()
        {
            // Exactly what a reader does: copy the file the repository ships into the folder the demo
            // reads, press the button, look at the screen. Against the authored package, not the
            // fallback - the fallback's button is a different object and would prove nothing about the
            // thing on camera.
            using (new BorrowedPatchFolder())
            {
            // Boot with the folder empty. The demo reloads patches at startup, so a file
            // installed first is already in force before the button is ever pressed, and the
            // "before" reading would be the patched one.
            string installed = null;
            var host = new GameObject("AbTestDemo");
            try
            {
                var demo = host.AddComponent<AbTestDemoBehaviour>();
                yield return null;
                yield return null;

                Assert.That(demo.UsingFallbackUi, Is.False, "this must run against the authored package");

                var shop = ShopOf(demo);
                var cta = shop.GetChild("btnCta");

                installed = InstallExample("10-flash-sale.lua");
                Press(demo.ConsoleRoot, "btnReloadPatches");
                for (int i = 0; i < 5; i++) yield return null;

                Assert.That(CtaText(cta), Is.EqualTo("Grab it now"),
                    "the call to action reads '" + CtaText(cta) + "'");
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (File.Exists(installed)) File.Delete(installed);
            }
            }
        }

        [UnityTest]
        public IEnumerator TheLayoutSwapExampleChangesTheArrangement()
        {
            // The other layer, and the proof the two compose rather than collide: this one writes only
            // 'layout' and leaves the pricing copy alone.
            using (new BorrowedPatchFolder())
            {
            // Boot with the folder empty. The demo reloads patches at startup, so a file
            // installed first is already in force before the button is ever pressed, and the
            // "before" reading would be the patched one.
            string installed = null;
            var host = new GameObject("AbTestDemo");
            try
            {
                var demo = host.AddComponent<AbTestDemoBehaviour>();
                yield return null;
                yield return null;

                // Pin the config first. Without a scenario the boot config decides whether the layout
                // experiment is running at all, and an unassigned layer never calls Lua - so the patch
                // would look broken when it is simply not being consulted.
                Press(demo.ConsoleRoot, "btnScenarioNormal");
                for (int i = 0; i < 3; i++) yield return null;
                Press(demo.ConsoleRoot, "btnRefresh");
                for (int i = 0; i < 5; i++) yield return null;

                // Read the debug strip rather than an internal - it is the artefact on camera, and its
                // first value is the arrangement.
                var strip = ShopOf(demo).GetChild("txtSpec");
                Assert.That(strip, Is.Not.Null, "no txtSpec strip");
                string before = strip.text;

                installed = InstallExample("40-layout-swap.lua");
                Press(demo.ConsoleRoot, "btnReloadPatches");
                for (int i = 0; i < 5; i++) yield return null;

                Assert.That(strip.text, Is.Not.EqualTo(before),
                    "the arrangement must flip; the strip still reads '" + before + "'. " +
                    LogTail(demo.ConsoleRoot));
                Assert.That(Arrangement(strip.text), Is.Not.EqualTo(Arrangement(before)),
                    "'" + before + "' became '" + strip.text + "'");
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (File.Exists(installed)) File.Delete(installed);
            }
            }
        }

        [UnityTest]
        public IEnumerator TheBadLayoutValueExampleReachesTheUnknownEnumCheck()
        {
            // The whole reason this example exists. Owning the layout group is what gets past the field
            // ownership rule, which is what the play-test's 'carousel' attempt kept tripping first.
            using (new BorrowedPatchFolder())
            {
            // Boot with the folder empty. The demo reloads patches at startup, so a file
            // installed first is already in force before the button is ever pressed, and the
            // "before" reading would be the patched one.
            string installed = null;
            var host = new GameObject("AbTestDemo");
            try
            {
                var demo = host.AddComponent<AbTestDemoBehaviour>();
                yield return null;
                yield return null;

                installed = InstallExample("50-bad-layout-value.lua");
                Press(demo.ConsoleRoot, "btnReloadPatches");
                for (int i = 0; i < 5; i++) yield return null;

                var strip = ShopOf(demo).GetChild("txtSpec");
                Assert.That(strip, Is.Not.Null, "no txtSpec strip");

                Assert.That(strip.text, Does.Contain("bad enum value"),
                    "the strip should carry the enum rejection, not the ownership one; it reads '" +
                    strip.text + "'");
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (File.Exists(installed)) File.Delete(installed);
            }
            }
        }

        /// <summary>The first value on the debug strip, which is the arrangement.</summary>
        private static string Arrangement(string strip) =>
            string.IsNullOrEmpty(strip) ? "" : strip.Split(' ')[0];

        /// <summary>The tail of the on-screen log, so a failure says what the demo thought it did.</summary>
        private static string LogTail(GComponent console, int lines = 12)
        {
            var list = UiValidator.Deep(console, "listLog") as GList;
            if (list == null) return "(no log panel)";

            var sb = new System.Text.StringBuilder("log tail:");
            int from = list.numChildren > lines ? list.numChildren - lines : 0;
            for (int i = from; i < list.numChildren; i++)
            {
                if (!(list.GetChildAt(i) is GComponent row)) continue;
                var title = row.GetChild("title");
                if (title != null) sb.AppendLine().Append("  ").Append(title.text);
            }

            return sb.ToString();
        }

        private static GComponent ShopOf(AbTestDemoBehaviour demo)
        {
            var device = UiValidator.Deep(demo.ConsoleRoot, "containerDevice") as GComponent;
            var shop = device?.GetChild("ShopScreen") as GComponent;
            Assert.That(shop, Is.Not.Null, "no shop screen in the device container");
            return shop;
        }

        // --- Finding 10: the bar and the light must not contradict each other --------------------------

        [UnityTest]
        public IEnumerator BelowTheFloorTheBarSaysUnmeasuredJustAsTheLightDoes()
        {
            // One user in the system. The light reads grey - unknown, correct, far below the chi-squared
            // floor. The bar beside it read "100.0% / 50.0%" and drew itself full, which is a measured
            // extreme. Two indicators, same state, opposite stories, and the index-aligned SrmState was
            // supposed to make that impossible.
            //
            // The dash was gated on nobody at all being exposed, not on the measurement being below the
            // floor, so with one exposed user it never appeared.
            var console = Authored("ConsoleMain");
            GRoot.inst.AddChild(console);
            var view = new ConsoleView(console, new FairyBinder(new ListLog()), usingFallback: false);

            view.SetMetrics(OneUserReport());
            yield return null;

            var list = console.GetChild("listMetrics") as GList;
            list.EnsureBoundsCorrect();

            int checkedBars = 0;
            for (int i = 0; i < list.numChildren; i++)
            {
                if (!(list.GetChildAt(i) is GComponent row)) continue;
                if (!(row.GetChild("barShare") is GComponent bar)) continue;

                var caption = bar.GetChild("txtShare") ?? bar.GetChild("title");
                if (caption == null) continue;

                checkedBars++;

                Assert.That(caption.text, Is.EqualTo("-"),
                    "the light says unmeasured and the bar says '" + caption.text + "'");

                if (bar is GProgressBar progress)
                {
                    Assert.That(progress.value, Is.EqualTo(0.0).Within(0.001),
                        "an unmeasured bar must not draw a fill; it drew " + progress.value + "%");
                }
            }

            Assert.That(checkedBars, Is.GreaterThan(1), "not enough bars to be meaningful");

            GRoot.inst.RemoveChild(console);
            console.Dispose();
        }

        /// <summary>One exposed user - far below the ratio check's floor, so the verdict is unknown.</summary>
        private static MetricsReport OneUserReport()
        {
            var aggregator = new MetricsAggregator();
            var read = ConfigReader.Read(Transport.LocalConfigServer.PayloadFor(
                Transport.ServerScenario.Normal, 1));

            foreach (var kind in new[] { AnalyticsEventKind.Assignment, AnalyticsEventKind.Exposure })
            {
                aggregator.Record(new AnalyticsEvent(
                    kind, "solo", new SessionId("s"), "exp_pricing_cta", "urgency", "pricing_cta", null,
                    EventTraits.None, "1", new FixedClock().UtcNow));
            }

            return aggregator.Build(read.Config, MetricsPopulation.Analysis);
        }

        // --- Finding 2: is the badge written, or is it a placeholder? -------------------------------------

        [Test]
        public void EveryCardsBadgeIsWrittenFromTheSpecNotLeftAsThePlaceholder()
        {
            // The authored placeholder is "Badge". A distinctive value here proves the per-card write
            // happens on all four rather than on one cached reference.
            var screen = Authored("ShopScreen");
            var view = new ShopScreenView(screen, new FairyBinder(new ListLog()), () => Authored("OfferCard"), _ => { });

            view.Apply(new PresentationSpec(OfferLayout.List, PriceStyle.Plain, "ZZTOP", "Buy"), null);

            var list = screen.GetChild("listOffers") as GList;
            for (int i = 0; i < list.numChildren; i++)
            {
                var card = list.GetChildAt(i) as GComponent;
                Assert.That(card.GetChild("txtBadge").text, Is.EqualTo("ZZTOP"), "card " + i);
            }

            screen.Dispose();
        }

        // --- Finding 3: the progress bar overwrites the title --------------------------------------------

        [Test]
        public void TheShareCaptionSurvivesAResizeOrIsKnownNotTo()
        {
            // GProgressBar adopts a child named literally "title" as its own title object and rewrites it
            // from titleType inside HandleSizeChanged. So the caption's *name* decides whether a resize can
            // clobber it, and this asserts whichever is true of the package as authored today.
            //
            // Under "title": the trap applies, and ConsoleView's layout flush is what keeps the demo
            // correct - ordering, not structure.
            // Under any other name: the trap cannot apply at all, and the flush is belt to that braces.
            //
            // Keeping both branches means the knowledge survives the rename instead of being deleted with
            // the test, and the day the package changes the assertion follows it rather than going red.
            var row = Authored("MetricsRow");
            var bar = row.GetChild("barShare") as GComponent;
            Assert.That(bar, Is.Not.Null);

            var caption = bar.GetChild("txtShare") ?? bar.GetChild("title");
            Assert.That(caption, Is.Not.Null, "barShare has no caption under either name");

            bool adoptedByTheProgressBar = bar.GetChild("txtShare") == null;

            if (bar is GProgressBar progress) progress.value = 49.9;
            caption.text = "49.9% / 50.0%";
            bar.SetSize(bar.width + 1, bar.height);

            if (adoptedByTheProgressBar)
            {
                Assert.That(caption.text, Is.Not.EqualTo("49.9% / 50.0%"),
                    "the caption is named 'title' but survived a resize, so GProgressBar no longer adopts " +
                    "it; ConsoleView's EnsureBoundsCorrect flush exists only for this and can go");
            }
            else
            {
                Assert.That(caption.text, Is.EqualTo("49.9% / 50.0%"),
                    "the caption is named 'txtShare', so nothing should rewrite it - if this fails, " +
                    "something other than GProgressBar's title handling is touching it");
            }

            TestContext.WriteLine(adoptedByTheProgressBar
                ? "barShare's caption is named 'title': the recompute trap applies, mitigated by ordering"
                : "barShare's caption is named 'txtShare': the recompute trap cannot apply");

            row.Dispose();
        }

        [UnityTest]
        public IEnumerator TheShareBarShowsOurTitleInTheRunningDemo()
        {
            // The end-to-end guard for what the play-test actually saw: "0%" and "100%" where
            // "49.9% / 50.0%" belonged. Runs the whole demo, simulates a population, and lets several
            // frames pass so any deferred layout has every chance to clobber the title.
            var host = new GameObject("AbTestDemo");
            var demo = host.AddComponent<AbTestDemoBehaviour>();

            yield return null;
            yield return null;

            var console = demo.ConsoleRoot;
            Press(console, "btnScenarioNormal");
            yield return null;
            Press(console, "btnSimulate");

            for (int i = 0; i < 5; i++) yield return null;

            var list = UiValidator.Deep(console, "listMetrics") as GList;
            Assert.That(list, Is.Not.Null);

            // Flush any queued layout instead of trusting the frames above to have covered it.
            // Waiting a fixed number of frames makes the assertion depend on frame cadence, which
            // differs between a batchmode run and the editor; this makes the read deterministic.
            list.EnsureBoundsCorrect();

            int checkedBars = 0;
            for (int i = 0; i < list.numChildren; i++)
            {
                if (!(list.GetChildAt(i) is GComponent row)) continue;
                if (!(row.GetChild("barShare") is GComponent bar)) continue;

                var title = bar.GetChild("txtShare") ?? bar.GetChild("title");
                if (title == null) continue;

                checkedBars++;
                Assert.That(title.text, Does.Contain("/"),
                    "bar " + checkedBars + " reads '" + title.text +
                    "', which is FairyGUI's auto-percent rather than the share against expected");
            }

            Assert.That(checkedBars, Is.GreaterThan(1),
                "not enough bars were rendered to be meaningful. " + Describe(list));

            Object.DestroyImmediate(host);
            yield return null;
        }

        /// <summary>Names what a metrics list actually holds, so a miss says why rather than only that.</summary>
        private static string Describe(GList list)
        {
            if (list == null) return "the list itself is null";

            var sb = new System.Text.StringBuilder();
            sb.Append("list has ").Append(list.numChildren).Append(" children:");

            for (int i = 0; i < list.numChildren; i++)
            {
                var child = list.GetChildAt(i);
                sb.AppendLine();
                sb.Append("  [").Append(i).Append("] ").Append(child.GetType().Name)
                  .Append(" visible=").Append(child.visible);

                if (!(child is GComponent row)) continue;

                var bar = row.GetChild("barShare");
                sb.Append(" barShare=").Append(bar == null ? "MISSING" : bar.GetType().Name);

                if (bar is GComponent barComponent)
                {
                    var caption = barComponent.GetChild("txtShare") ?? barComponent.GetChild("title");
                    sb.Append(" caption=").Append(caption == null ? "MISSING" : "'" + caption.text + "'");
                }
            }

            return sb.ToString();
        }

        private static void Press(GComponent console, string buttonName)
        {
            var button = UiValidator.Deep(console, buttonName);
            Assert.That(button, Is.Not.Null, "no button named '" + buttonName + "'");
            button.onClick.Call();
        }

        [UnityTest]
        public IEnumerator AnUnmeasuredShareCellReadsADashRatherThanZero()
        {
            // "0%" on an unmeasured cell reads as a measured zero, which is the specific misreading the
            // dash exists to prevent. Driven on the stage with frames yielded, because the list lays out
            // deferred: a synthetic call that never renders cannot catch a clobber that happens later.
            var console = Authored("ConsoleMain");
            GRoot.inst.AddChild(console);
            var view = new ConsoleView(console, new FairyBinder(new ListLog()), usingFallback: false);

            view.SetMetrics(EmptyReport());
            yield return null;
            yield return null;

            var list = console.GetChild("listMetrics") as GList;
            Assert.That(list, Is.Not.Null);

            list.EnsureBoundsCorrect();

            bool checkedAny = false;
            for (int i = 0; i < list.numChildren; i++)
            {
                if (!(list.GetChildAt(i) is GComponent row)) continue;
                if (!(row.GetChild("barShare") is GComponent bar)) continue;

                var title = bar.GetChild("txtShare") ?? bar.GetChild("title");
                if (title == null) continue;

                checkedAny = true;
                Assert.That(title.text, Is.EqualTo("-"),
                    "an experiment with nobody exposed shows '" + title.text + "' where a dash belongs");
            }

            Assert.That(checkedAny, Is.True, "no bar was found to check. " + Describe(list));

            GRoot.inst.RemoveChild(console);
            console.Dispose();
        }

        // --- Finding 4: the debug strip ------------------------------------------------------------------

        [Test]
        public void TheSpecStripIsWrittenAndInsideTheScreen()
        {
            var screen = Authored("ShopScreen");
            var view = new ShopScreenView(screen, new FairyBinder(new ListLog()), () => Authored("OfferCard"), _ => { });

            view.Apply(new PresentationSpec(OfferLayout.Grid, PriceStyle.Discounted, "SALE", "Claim offer"), null);

            var strip = screen.GetChild("txtSpec");
            Assert.That(strip, Is.Not.Null);

            Assert.That(strip.text, Is.Not.Null.And.Not.Empty, "the strip carries no text");
            Assert.That(strip.text, Does.Contain("grid").And.Contain("discounted").And.Contain("SALE"));

            Assert.That(strip.visible, Is.True, "the strip is hidden");
            Assert.That(strip.y + strip.height, Is.LessThanOrEqualTo(screen.height),
                "the strip at y=" + strip.y + " falls outside a " + screen.height + "-tall screen");

            // Invisible-because-black is the failure this catches: the strip exists, is populated and is in
            // bounds, and still cannot be read on a dark device frame.
            Assert.That(strip.alpha, Is.GreaterThan(0f));

            screen.Dispose();
        }

        // --- Finding 5: row alignment --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator EveryMetricsRowAlignsItsColumnsIdentically()
        {
            // groupRow uses a horizontal layout with excludeInvisibles and is centred in the row, so hiding
            // the ratio light on continuation rows makes the group narrower and re-centres everything left
            // of it. The light must be hidden without leaving the layout.
            var console = Authored("ConsoleMain");
            GRoot.inst.AddChild(console);
            var view = new ConsoleView(console, new FairyBinder(new ListLog()), usingFallback: false);

            view.SetMetrics(TwoArmReport());
            yield return null;
            yield return null;

            var list = console.GetChild("listMetrics") as GList;
            float firstRowX = float.NaN;
            int compared = 0;

            for (int i = 0; i < list.numChildren; i++)
            {
                if (!(list.GetChildAt(i) is GComponent row)) continue;

                var cell = row.GetChild("txtVariant");
                if (cell == null) continue;

                if (float.IsNaN(firstRowX))
                {
                    firstRowX = cell.x;
                    continue;
                }

                compared++;
                Assert.That(cell.x, Is.EqualTo(firstRowX).Within(0.5f),
                    "row " + i + " puts its variant column at x=" + cell.x + " while the first row uses " +
                    firstRowX + "; the columns do not line up");
            }

            Assert.That(compared, Is.GreaterThan(0), "only one row was rendered, so nothing was compared");

            GRoot.inst.RemoveChild(console);
            console.Dispose();
        }

        // --- report builders ---------------------------------------------------------------------------------

        private static MetricsReport EmptyReport() => ReportFor(exposedControl: 0, exposedTreatment: 0);

        private static MetricsReport TwoArmReport() => ReportFor(exposedControl: 500, exposedTreatment: 500);

        private static MetricsReport ReportFor(long exposedControl, long exposedTreatment)
        {
            var aggregator = new MetricsAggregator();
            var clock = new FixedClock();

            var read = ConfigReader.Read(Transport.LocalConfigServer.PayloadFor(
                Transport.ServerScenario.Normal, 1));

            for (long i = 0; i < exposedControl; i++) Feed(aggregator, clock, "control", "c" + i);
            for (long i = 0; i < exposedTreatment; i++) Feed(aggregator, clock, "urgency", "t" + i);

            return aggregator.Build(read.Config, MetricsPopulation.Analysis);
        }

        private static void Feed(MetricsAggregator aggregator, FixedClock clock, string variant, string user)
        {
            foreach (var kind in new[] { AnalyticsEventKind.Assignment, AnalyticsEventKind.Exposure })
            {
                aggregator.Record(new AnalyticsEvent(
                    kind, user, new SessionId("s"), "exp_pricing_cta", variant, "pricing_cta", null,
                    EventTraits.None, "1", clock.UtcNow));
            }
        }

        private sealed class FixedClock : Core.IClock
        {
            public System.DateTime UtcNow { get; } =
                new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        }
    }
}
