using FairyGUI;
using HotUpdateABTest.Core.Presentation;
using HotUpdateABTest.Demo;
using NUnit.Framework;
using UnityEngine;

namespace HotUpdateABTest.Tests.PlayMode
{
    /// <summary>
    /// Pins the two strings that have to fit in a fixed-width cell, at their worst case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both exist to be read off a still frame, which makes overflow a correctness problem rather than a
    /// cosmetic one: a clipped strip still looks authoritative, so a viewer trusts a half-sentence. Both
    /// overflowed once already, which is why they are measured rather than eyeballed.
    /// </para>
    /// <para>
    /// Measured through FairyGUI's own text layout rather than by counting characters, so the assertion is
    /// about what will actually be drawn at the authored size.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class StripWidthTests
    {
        /// <summary>Width of the bar's title cell inside a MetricsRow, in pixels.</summary>
        private const int BarTitleWidth = 130;

        /// <summary>Width of the shop screen's debug strip, in pixels.</summary>
        private const int SpecStripWidth = 335;

        private GameObject _stage;

        [SetUp]
        public void SetUp()
        {
            _stage = new GameObject("StageCamera");
            _stage.AddComponent<Camera>();
            _ = GRoot.inst;
        }

        [TearDown]
        public void TearDown()
        {
            if (_stage != null) Object.DestroyImmediate(_stage);
        }

        private static float Measure(string text, int fontSize, bool bold)
        {
            var field = new GTextField();
            field.textFormat = new TextFormat { size = fontSize, bold = bold };
            field.autoSize = AutoSizeType.Both;
            field.text = text;

            float width = field.width;
            field.Dispose();
            return width;
        }

        [Test]
        public void TheBarTitleHasRealHeadroomInItsCell()
        {
            // The widest this can ever be is two three-digit percentages.
            float shortened = Measure("100.0% / 100.0%", 16, bold: true);
            float previous = Measure("49.9% (exp 50.0%)", 16, bold: true);

            // Ten pixels of slack, not a percentage pulled out of the air: enough that a font fallback or a
            // rounding difference cannot push it over, and concrete enough to argue about.
            Assert.That(shortened, Is.LessThan(BarTitleWidth - 10),
                "the bar title is " + shortened + "px in a " + BarTitleWidth + "px cell and should have " +
                "room to spare, not merely fit");

            // The form this replaced measured 129px in that 130px cell. It fitted - by one pixel - which is
            // not the same as fitting: any font fallback, locale or rounding difference would have clipped
            // it, and a clipped cell still looks authoritative. Recorded rather than asserted, because the
            // point is the size of the margin, not a threshold either side of it.
            TestContext.WriteLine(
                "bar title: " + shortened + "px now, " + previous + "px before, cell is " + BarTitleWidth + "px");
        }

        [Test]
        public void TheSpecStripFitsAtItsWorstRealisticCase()
        {
            // Longest a valid spec can be with copy somebody would actually write: grid, discounted, a
            // badge at MaxBadgeLength, a call to action at MaxCtaLength.
            var worst = new PresentationSpec(
                OfferLayout.Grid, PriceStyle.Discounted, "BEST VALUE", "Claim your offer today!!");

            Assert.That(worst.BadgeText.Length, Is.EqualTo(PresentationSpec.MaxBadgeLength));
            Assert.That(worst.CtaText.Length, Is.EqualTo(PresentationSpec.MaxCtaLength));

            string text = CompactOf(worst);
            float width = Measure(text, 11, bold: true);

            Assert.That(width, Is.LessThan(SpecStripWidth),
                "the spec strip is " + width + "px of " + SpecStripWidth + "px at its worst: '" + text + "'");
        }

        [Test]
        public void TheStripOnlyNeedsShrinkForCopyNobodyWouldWrite()
        {
            // The pathological bound: every character the widest glyph in the font. It does not fit, and it
            // is not meant to - Shrink exists for exactly this, and the previous test is the design target.
            // Measured rather than assumed so the gap between "worst realistic" and "worst permitted" is a
            // number somebody can look at before changing either constant.
            var pathological = new PresentationSpec(
                OfferLayout.Grid,
                PriceStyle.Discounted,
                new string('W', PresentationSpec.MaxBadgeLength),
                new string('W', PresentationSpec.MaxCtaLength));

            float width = Measure(CompactOf(pathological), 11, bold: true);

            TestContext.WriteLine(
                "all-W worst case: " + width + "px of " + SpecStripWidth + "px, so Shrink would scale to " +
                (11f * SpecStripWidth / width).ToString("0.0") + "px");

            Assert.That(11f * SpecStripWidth / width, Is.GreaterThan(7f),
                "even the pathological case must stay legible after Shrink; below about 7px it does not, " +
                "and the compact form would need cutting further");
        }

        [Test]
        public void TheSpecStripFitsWhenARejectionMarkerIsAppended()
        {
            // A rejection always renders the baseline - no badge, "Buy" - so the marker never competes for
            // space with a rich spec. That is why the worst case here is shorter than the one above.
            string text = CompactOf(PresentationSpec.Baseline) +
                          "  [FALLBACK: " + SpecRejection.NoLua + "]";

            float width = Measure(text, 11, bold: true);

            Assert.That(width, Is.LessThan(SpecStripWidth),
                "the rejected strip is " + width + "px of " + SpecStripWidth + "px: '" + text + "'");
        }

        [Test]
        public void TheStripIsSetToShrinkSoItCanNeverClip()
        {
            var screen = DemoUiFactory.CreateShopScreen();

            try
            {
                var view = new ShopScreenView(
                    screen, new FairyBinder(new ListLog()), DemoUiFactory.CreateOfferCard, _ => { });

                view.Apply(PresentationSpec.Baseline, null);

                var strip = screen.GetChild("txtSpec") as GTextField;
                Assert.That(strip, Is.Not.Null);
                Assert.That(strip.autoSize, Is.EqualTo(AutoSizeType.Shrink),
                    "a clipped strip still looks authoritative, which is worse than a slightly smaller one");
            }
            finally
            {
                screen.Dispose();
            }
        }

        /// <summary>Mirrors ShopScreenView's compact form, which is private.</summary>
        private static string CompactOf(PresentationSpec spec)
        {
            string dot = char.ConvertFromUtf32(0x00B7);

            return spec.Layout.ToString().ToLowerInvariant() + " " + dot + " " +
                   spec.PriceStyle.ToString().ToLowerInvariant() + " " + dot + " " +
                   (spec.BadgeText ?? "no badge") + " " + dot + " " +
                   spec.CtaText;
        }
    }
}
