using System.Collections;
using System.IO;
using FairyGUI;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Presentation;
using HotUpdateABTest.Core.Telemetry;
using HotUpdateABTest.Demo;
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
        public void ResizingAShareBarStillRewritesItsTitle()
        {
            // Documents the trap rather than asserting it away, because it is FairyGUI's behaviour and not
            // something this code can change: barShare's text child is named "title", so GProgressBar
            // adopts it as its title object and rewrites it from titleType inside HandleSizeChanged.
            //
            // The mitigation is ordering - the list's layout is flushed before any cell is written, so
            // nothing resizes after the write. That mitigation is only sound while this remains true, so if
            // this test ever fails, the flush is no longer needed and should go.
            var row = Authored("MetricsRow");
            var bar = row.GetChild("barShare") as GComponent;
            var title = bar.GetChild("title");

            if (bar is GProgressBar progress) progress.value = 49.9;
            title.text = "49.9% / 50.0%";

            bar.SetSize(bar.width + 1, bar.height);

            Assert.That(title.text, Is.Not.EqualTo("49.9% / 50.0%"),
                "GProgressBar no longer rewrites its title on resize; ConsoleView's EnsureBoundsCorrect " +
                "flush was added only to work around that and can now be removed");

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

            int checkedBars = 0;
            for (int i = 0; i < list.numChildren; i++)
            {
                if (!(list.GetChildAt(i) is GComponent row)) continue;
                if (!(row.GetChild("barShare") is GComponent bar)) continue;

                var title = bar.GetChild("title");
                if (title == null) continue;

                checkedBars++;
                Assert.That(title.text, Does.Contain("/"),
                    "bar " + checkedBars + " reads '" + title.text +
                    "', which is FairyGUI's auto-percent rather than the share against expected");
            }

            Assert.That(checkedBars, Is.GreaterThan(1), "not enough bars were rendered to be meaningful");

            Object.DestroyImmediate(host);
            yield return null;
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

            bool checkedAny = false;
            for (int i = 0; i < list.numChildren; i++)
            {
                if (!(list.GetChildAt(i) is GComponent row)) continue;
                if (!(row.GetChild("barShare") is GComponent bar)) continue;

                var title = bar.GetChild("title");
                if (title == null) continue;

                checkedAny = true;
                Assert.That(title.text, Is.EqualTo("-"),
                    "an experiment with nobody exposed shows '" + title.text + "' where a dash belongs");
            }

            Assert.That(checkedAny, Is.True, "no bar was found to check");

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
