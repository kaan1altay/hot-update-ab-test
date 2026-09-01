using System.IO;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace HotUpdateABTest.Tests.Unity
{
    /// <summary>
    /// Runs the patches in <c>examples/lua-patches/</c> through the real sandbox.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An example that has quietly stopped working is worse than no example, because the reader trusts it
    /// and then blames their own setup. These are the files a reader is invited to copy into a running
    /// demo, so they are loaded here exactly as the demo would load them, from the same folder, and each
    /// one is asserted to do what its comment header says it does.
    /// </para>
    /// <para>
    /// This also pins the examples to the constants they depend on. <c>20-rejected-spec.lua</c> is only a
    /// useful demonstration while its badge text is longer than <c>MaxBadgeLength</c>; the day that
    /// constant moves, this fails rather than the example silently becoming a patch that works.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ExampleLuaPatchTests
    {
        private LuaFixture _fixture;

        [SetUp]
        public void SetUp()
        {
            _fixture = new LuaFixture();
        }

        [TearDown]
        public void TearDown()
        {
            _fixture?.Dispose();
        }

        /// <summary>The examples folder, which sits beside Assets/ rather than inside it.</summary>
        private static string ExamplesRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "examples", "lua-patches"));

        /// <summary>Copies one example into the patch root, as a reader would, and reloads.</summary>
        private void Install(string fileName)
        {
            string source = Path.Combine(ExamplesRoot, fileName);
            Assert.That(File.Exists(source), Is.True, "the example is missing: " + source);

            _fixture.WritePatch(fileName, File.ReadAllText(source));
            _fixture.Host.Reload();
        }

        private static UserContext User(int level = 5) =>
            new UserContext("user-1", accountLevel: level, platform: "editor");

        private static VariantAssignment Assignment(string variantId, string behavior)
        {
            var variant = new VariantDef(variantId, 5000, behavior);
            var experiment = new ExperimentDef(
                "exp_test", "pricing_cta", ExperimentStatus.Running, "salt", BucketRange.Full,
                StickinessPolicy.StickyAfterExposure, new[] { variant });

            return VariantAssignment.Assigned(
                "pricing_cta", experiment, variant, AssignmentSource.Bucketed, 1, 2, "v1");
        }

        private PresentationSpec Present(string variantId, string behavior, bool hasOriginalPrice = true) =>
            _fixture.Host.Present(
                User(), Assignment(variantId, behavior), SpecFieldGroup.Pricing, PresentationSpec.Baseline,
                hasOriginalPrice);

        [Test]
        public void EveryExampleIsSyntacticallyValidAndLoads()
        {
            var examples = Directory.GetFiles(ExamplesRoot, "*.lua");
            Assert.That(examples.Length, Is.GreaterThan(0), "no examples found in " + ExamplesRoot);

            for (int i = 0; i < examples.Length; i++)
            {
                _fixture.WritePatch(Path.GetFileName(examples[i]), File.ReadAllText(examples[i]));
            }

            var report = _fixture.Host.Reload();

            Assert.That(report.PatchesLoaded, Is.EqualTo(examples.Length),
                "every example should load; the log says which did not: " + _fixture.Log.All);
        }

        [Test]
        public void TheFlashSaleExampleReplacesTheUrgencyBehaviour()
        {
            Install("10-flash-sale.lua");

            var spec = Present("urgency", "shop.pricing_cta.urgency");

            Assert.That(spec.PriceStyle, Is.EqualTo(PriceStyle.Discounted));
            Assert.That(spec.BadgeText, Is.EqualTo("FLASH"));
            Assert.That(spec.CtaText, Is.EqualTo("Grab it now"));
        }

        [Test]
        public void TheFlashSaleExampleStillGuardsTheDiscountedPresentation()
        {
            // The header claims it keeps the baseline's guard. An offer with no original price to strike
            // through must not be asked to render the discounted presentation.
            Install("10-flash-sale.lua");

            var spec = Present("urgency", "shop.pricing_cta.urgency", hasOriginalPrice: false);

            Assert.That(spec.PriceStyle, Is.EqualTo(PriceStyle.Plain));
            Assert.That(spec.HasBadge, Is.False);
            Assert.That(spec.CtaText, Is.EqualTo("Grab it now"));
        }

        [Test]
        public void RemovingTheFlashSaleExampleRestoresTheShippedBehaviour()
        {
            // The half of hot update that is easy to skip demonstrating, and the reason the README claims
            // reload rebuilds from the baseline up rather than applying a delta.
            Install("10-flash-sale.lua");
            Assert.That(Present("urgency", "shop.pricing_cta.urgency").BadgeText, Is.EqualTo("FLASH"));

            _fixture.DeletePatch("10-flash-sale.lua");
            _fixture.Host.Reload();

            var spec = Present("urgency", "shop.pricing_cta.urgency");

            Assert.That(spec.BadgeText, Is.EqualTo("LIMITED"));
            Assert.That(spec.CtaText, Is.EqualTo("Claim offer"));
        }

        [Test]
        public void TheRejectedExampleIsRejectedForTheReasonItAdvertises()
        {
            Install("20-rejected-spec.lua");

            var spec = Present("urgency", "shop.pricing_cta.urgency");

            Assert.That(spec, Is.EqualTo(PresentationSpec.Baseline), "a rejected spec renders control");
            Assert.That(SpecRejection.TokenFor("badgeText.tooLong"), Is.EqualTo("text too long"),
                "the marker the example's header promises");
        }

        [Test]
        public void TheRejectedExampleDiscardsItsValidFieldToo()
        {
            // The header says the whole table is rejected rather than the offending field. ctaText is
            // valid in that file, so if it survived, the example would be teaching the wrong rule.
            Install("20-rejected-spec.lua");

            Assert.That(Present("urgency", "shop.pricing_cta.urgency").CtaText,
                Is.EqualTo(PresentationSpec.Baseline.CtaText));
        }

        [Test]
        public void TheNewVariantExampleRegistersANameTheBuildDoesNotKnow()
        {
            const string added = "shop.pricing_cta.flash_sale";

            Assert.That(_fixture.Host.HasBehavior(added), Is.False, "it does not exist in the build");

            Install("30-new-variant.lua");

            Assert.That(_fixture.Host.HasBehavior(added), Is.True);

            var spec = Present("flash_sale", added);

            Assert.That(spec.PriceStyle, Is.EqualTo(PriceStyle.Discounted));
            Assert.That(spec.BadgeText, Is.EqualTo("FLASH"));
            Assert.That(spec.CtaText, Is.EqualTo("Grab it now"));
        }

        [Test]
        public void TheNewVariantExampleChangesNothingUntilAConfigDeclaresIt()
        {
            // The claim the example's header spends a paragraph on: registering a behaviour does not
            // enrol anyone in it. A user assigned to the variants that *are* configured must be
            // unaffected by the presence of the new registration.
            Install("30-new-variant.lua");

            var urgency = Present("urgency", "shop.pricing_cta.urgency");

            Assert.That(urgency.BadgeText, Is.EqualTo("LIMITED"));
            Assert.That(urgency.CtaText, Is.EqualTo("Claim offer"));
        }
    }
}
