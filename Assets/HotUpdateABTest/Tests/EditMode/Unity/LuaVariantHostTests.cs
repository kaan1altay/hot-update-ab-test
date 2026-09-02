using HotUpdateABTest.Core;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Presentation;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Unity
{
    /// <summary>
    /// Drives real Lua through the real host: purity, isolation, reload behaviour, and the headline
    /// property that a patch can add a working variant to a running experiment.
    /// </summary>
    [TestFixture]
    public sealed class LuaVariantHostTests
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

        private static UserContext User(int level = 5) =>
            new UserContext("user-1", accountLevel: level, platform: "editor");

        private static VariantAssignment Assignment(string variantId, string behavior, string layerId = "pricing_cta")
        {
            var variant = new VariantDef(variantId, 5000, behavior);
            var experiment = new ExperimentDef(
                "exp_test", layerId, ExperimentStatus.Running, "salt", BucketRange.Full,
                StickinessPolicy.StickyAfterExposure, new[] { variant });

            return VariantAssignment.Assigned(layerId, experiment, variant, AssignmentSource.Bucketed, 1, 2, "v1");
        }

        private PresentationSpec Present(
            string variantId, string behavior, SpecFieldGroup group = SpecFieldGroup.Pricing,
            UserContext user = null, bool hasOriginalPrice = true)
        {
            return _fixture.Host.Present(
                user ?? User(), Assignment(variantId, behavior), group, PresentationSpec.Baseline,
                hasOriginalPrice);
        }

        // --- the baseline behaviors work -------------------------------------------------------------

        [Test]
        public void TheShippedBaselineBehaviorsProduceTheirSpecs()
        {
            Assert.That(Present("control", "shop.offer_layout.control", SpecFieldGroup.Layout).Layout,
                Is.EqualTo(OfferLayout.List));
            Assert.That(Present("grid_v2", "shop.offer_layout.grid_v2", SpecFieldGroup.Layout).Layout,
                Is.EqualTo(OfferLayout.Grid));

            var urgency = Present("urgency", "shop.pricing_cta.urgency");

            Assert.That(urgency.PriceStyle, Is.EqualTo(PriceStyle.Discounted));
            Assert.That(urgency.BadgeText, Is.EqualTo("LIMITED"));
            Assert.That(urgency.CtaText, Is.EqualTo("Claim offer"));
        }

        [Test]
        public void ABehaviorCanBranchOnTheContextItIsGiven()
        {
            // The urgency behavior asks for the discounted presentation only when there is an original
            // price to strike through.
            var withOriginal = Present("urgency", "shop.pricing_cta.urgency", hasOriginalPrice: true);
            var without = Present("urgency", "shop.pricing_cta.urgency", hasOriginalPrice: false);

            Assert.That(withOriginal.PriceStyle, Is.EqualTo(PriceStyle.Discounted));
            Assert.That(without.PriceStyle, Is.EqualTo(PriceStyle.Plain));
            Assert.That(without.HasBadge, Is.False);
        }

        // --- purity --------------------------------------------------------------------------------------

        [Test]
        public void CallingABehaviorTwiceWithTheSameContextGivesAnIdenticalSpec()
        {
            // The property an A/B framework cannot do without. A behavior that varied within a user would
            // make the experiment measure noise.
            var first = Present("urgency", "shop.pricing_cta.urgency");

            for (int i = 0; i < 50; i++)
            {
                Assert.That(Present("urgency", "shop.pricing_cta.urgency"), Is.EqualTo(first));
            }
        }

        [Test]
        public void ABehaviorReachingForARandomSourceRendersControl()
        {
            _fixture.WritePatch("rand.lua",
                "register('shop.pricing_cta.rand', function(ctx)\n" +
                "  return { ctaText = math.random() > 0.5 and 'A' or 'B' }\n" +
                "end)");
            _fixture.Host.Reload();

            var spec = Present("rand", "shop.pricing_cta.rand");

            Assert.That(spec, Is.EqualTo(PresentationSpec.Baseline));
            Assert.That(_fixture.Log.All, Does.Contain("renders control"));
        }

        // --- isolation ------------------------------------------------------------------------------------

        [Test]
        public void OneBrokenPatchDoesNotTakeTheRegistryDown()
        {
            _fixture.WritePatch("a_good.lua",
                "register('shop.pricing_cta.good', function(ctx) return { ctaText = 'Still here' } end)");
            _fixture.WritePatch("b_broken.lua", "register('shop.pricing_cta.bad', function( -- syntax error");

            var report = _fixture.Host.Reload();

            Assert.That(report.FilesFailed, Is.EqualTo(1));
            Assert.That(report.FilesLoaded, Is.GreaterThanOrEqualTo(3), "everything else still loaded");

            Assert.That(Present("good", "shop.pricing_cta.good").CtaText, Is.EqualTo("Still here"));
            Assert.That(_fixture.Host.HasBehavior("shop.pricing_cta.control"), Is.True,
                "the shipped baseline is untouched");
        }

        [Test]
        public void APatchThatThrowsWhileRegisteringCommitsNothingItStaged()
        {
            // Half-applying a patch file is the same class of problem as half-applying a config: the file
            // registers one variant, then throws, and neither should be left behind.
            _fixture.WritePatch("partial.lua",
                "register('shop.pricing_cta.first', function() return {} end)\n" +
                "error('exploded halfway through')\n" +
                "register('shop.pricing_cta.second', function() return {} end)");

            var report = _fixture.Host.Reload();

            Assert.That(report.FilesFailed, Is.EqualTo(1));
            Assert.That(_fixture.Host.HasBehavior("shop.pricing_cta.first"), Is.False,
                "the registration made before the throw must be discarded with the rest of the file");
            Assert.That(_fixture.Host.HasBehavior("shop.pricing_cta.second"), Is.False);
        }

        [Test]
        public void APatchThatThrowsAtRegistrationSaysSoInTheLog()
        {
            // Finding 7. "One bad patch must not take the registry down" is only half the claim: if the
            // bad patch also vanishes without a trace, the author has no way to learn their file is dead.
            // The syntax-error path reports correctly; this is the one that did not, and the test that
            // was supposed to cover it only asserted that nothing was committed.
            _fixture.WritePatch("boom.lua", "error('boom')");

            var report = _fixture.Host.Reload();

            Assert.That(report.FilesFailed, Is.EqualTo(1), "the file must be counted as failed");
            Assert.That(_fixture.Log.All, Does.Contain("boom.lua"),
                "the log must name the file that died. It says:\n" + _fixture.Log.All);
            Assert.That(_fixture.Log.All, Does.Contain("boom"),
                "and must carry the message the patch threw");
        }

        [Test]
        public void APatchThatThrowsAtRegistrationIsReportedAtErrorSeverity()
        {
            // Findings 8 and 9. A file that cannot run is an error, not a warning, and the log panel's
            // error page was unreachable because nothing ever emitted one.
            _fixture.WritePatch("boom.lua", "error('boom')");
            _fixture.Host.Reload();

            Assert.That(_fixture.Log.HighestLevel, Is.EqualTo(AbLogLevel.Error),
                "a patch that cannot run is an error; the log says:\n" + _fixture.Log.All);
        }

        [Test]
        public void APatchThatCannotBeParsedIsReportedAtErrorSeverity()
        {
            // Finding 8. This one was isolated cleanly and reported at warn. A file that cannot be parsed
            // is not a warning about something that might matter later.
            _fixture.WritePatch("bad.lua", "register('x', function( -- syntax error");
            _fixture.Host.Reload();

            Assert.That(_fixture.Log.HighestLevel, Is.EqualTo(AbLogLevel.Error),
                "the log says:\n" + _fixture.Log.All);
        }

        [Test]
        public void EachNewFailureInTheSameFileIsReported()
        {
            // The mechanism behind finding 7. An author edits one file until it works, so every attempt
            // lands at the same path. Keyed on the path alone, the first failure was reported and every
            // later one - a different error, in a file they had just changed - was silent, which reads
            // exactly like a patch channel that has stopped listening.
            _fixture.WritePatch("wip.lua", "error('first mistake')");
            _fixture.Host.Reload();

            _fixture.WritePatch("wip.lua", "error('second mistake')");
            _fixture.Host.Reload();

            Assert.That(_fixture.Log.All, Does.Contain("first mistake"));
            Assert.That(_fixture.Log.All, Does.Contain("second mistake"),
                "the second failure is a different error in a file that just changed, and it was " +
                "swallowed by the first one's key. The log says:\n" + _fixture.Log.All);
        }

        [Test]
        public void TheSameFailureRepeatedIsStillOnlyReportedOnce()
        {
            // The property the key exists for, which the fix must not lose: a permanently broken patch
            // says so once rather than on every reload.
            _fixture.WritePatch("stuck.lua", "error('same every time')");

            _fixture.Host.Reload();
            _fixture.Host.Reload();
            _fixture.Host.Reload();

            Assert.That(_fixture.Log.CountContaining("same every time"), Is.EqualTo(1),
                "the log says:\n" + _fixture.Log.All);
        }

        [Test]
        public void ABrokenPatchIsReportedOncePerReloadRatherThanOnEveryCall()
        {
            _fixture.WritePatch("broken.lua", "this is not lua");

            for (int i = 0; i < 5; i++) _fixture.Host.Reload();

            Assert.That(_fixture.Log.CountContaining("broken.lua"), Is.EqualTo(1), _fixture.Log.All);
        }

        // --- reload is idempotent, removal reverts ------------------------------------------------------------

        [Test]
        public void ReloadingTheSamePatchTwiceChangesNothing()
        {
            // The demo has a reload button that gets pressed repeatedly on camera.
            _fixture.WritePatch("p.lua",
                "register('shop.pricing_cta.control', function(ctx) return { ctaText = 'Patched' } end)");

            var first = _fixture.Host.Reload();
            var firstSpec = Present("control", "shop.pricing_cta.control");

            for (int i = 0; i < 5; i++)
            {
                var again = _fixture.Host.Reload();
                Assert.That(again.BehaviorCount, Is.EqualTo(first.BehaviorCount));
                Assert.That(Present("control", "shop.pricing_cta.control"), Is.EqualTo(firstSpec));
            }
        }

        [Test]
        public void DeletingAPatchAndReloadingRevertsToTheBaseline()
        {
            // The other half of the action pair. For every state a patch can put the system into, there is
            // a defined way out.
            Assert.That(Present("control", "shop.pricing_cta.control").CtaText, Is.EqualTo("Buy"));

            _fixture.WritePatch("p.lua",
                "register('shop.pricing_cta.control', function(ctx) return { ctaText = 'Patched' } end)");
            _fixture.Host.Reload();
            Assert.That(Present("control", "shop.pricing_cta.control").CtaText, Is.EqualTo("Patched"));

            _fixture.DeletePatch("p.lua");
            _fixture.Host.Reload();

            Assert.That(Present("control", "shop.pricing_cta.control").CtaText, Is.EqualTo("Buy"),
                "removing a patch must return the variant to what the build shipped");
        }

        [Test]
        public void APatchOverridesTheBaselineBecausePatchesLoadLast()
        {
            _fixture.WritePatch("p.lua",
                "register('shop.offer_layout.control', function(ctx) return { layout = 'grid' } end)");
            _fixture.Host.Reload();

            Assert.That(Present("control", "shop.offer_layout.control", SpecFieldGroup.Layout).Layout,
                Is.EqualTo(OfferLayout.Grid));
        }

        // --- the headline: a patch adds a variant to a running experiment ----------------------------------------

        [Test]
        public void APatchAddsAWorkingNewVariantWithNoCSharpChangeAndNoRebuild()
        {
            const string newVariant = "shop.pricing_cta.flash_sale";

            Assert.That(_fixture.Host.HasBehavior(newVariant), Is.False, "it does not exist in the build");

            _fixture.WritePatch("flash_sale.lua",
                "register('" + newVariant + "', function(ctx)\n" +
                "  return {\n" +
                "    priceStyle = 'discounted',\n" +
                "    badgeText = 'FLASH',\n" +
                "    ctaText = 'Grab it now',\n" +
                "  }\n" +
                "end)");

            var report = _fixture.Host.Reload();

            Assert.That(report.PatchesLoaded, Is.EqualTo(1));
            Assert.That(_fixture.Host.HasBehavior(newVariant), Is.True);

            var spec = Present("flash_sale", newVariant);

            Assert.That(spec.PriceStyle, Is.EqualTo(PriceStyle.Discounted));
            Assert.That(spec.BadgeText, Is.EqualTo("FLASH"));
            Assert.That(spec.CtaText, Is.EqualTo("Grab it now"));
        }

        // --- spec validation through the real bridge -----------------------------------------------------------------

        [Test]
        public void AVariantWithNoRegisteredBehaviorRendersControl()
        {
            var spec = Present("ghost", "shop.pricing_cta.does_not_exist");

            Assert.That(spec, Is.EqualTo(PresentationSpec.Baseline));
            Assert.That(_fixture.Log.All, Does.Contain("no behavior is registered"));
        }

        [Test]
        public void ABehaviorReturningSomethingOtherThanATableRendersControl()
        {
            _fixture.WritePatch("p.lua", "register('shop.pricing_cta.odd', function(ctx) return 42 end)");
            _fixture.Host.Reload();

            Assert.That(Present("odd", "shop.pricing_cta.odd"), Is.EqualTo(PresentationSpec.Baseline));
            Assert.That(_fixture.Log.All, Does.Contain("rather than a table"));
        }

        [Test]
        public void ABehaviorAskingForAPresentationNobodyDrewRendersControl()
        {
            _fixture.WritePatch("p.lua",
                "register('shop.offer_layout.wild', function(ctx) return { layout = 'carousel' } end)");
            _fixture.Host.Reload();

            var spec = Present("wild", "shop.offer_layout.wild", SpecFieldGroup.Layout);

            Assert.That(spec, Is.EqualTo(PresentationSpec.Baseline));
            Assert.That(_fixture.Log.All, Does.Contain("not one the screen can render"));
        }

        [Test]
        public void ABehaviorWritingAnotherLayersFieldIsRejected()
        {
            _fixture.WritePatch("p.lua",
                "register('shop.pricing_cta.greedy', function(ctx)\n" +
                "  return { ctaText = 'Fine', layout = 'grid' }\n" +
                "end)");
            _fixture.Host.Reload();

            var spec = Present("greedy", "shop.pricing_cta.greedy");

            Assert.That(spec, Is.EqualTo(PresentationSpec.Baseline), "including the field that was legal");
            Assert.That(_fixture.Log.All, Does.Contain("layers must not be able to overwrite each other"));
        }

        [Test]
        public void ABehaviorThatThrowsAtCallTimeRendersControlWithoutEscaping()
        {
            _fixture.WritePatch("p.lua",
                "register('shop.pricing_cta.boom', function(ctx) error('kaboom') end)");
            _fixture.Host.Reload();

            PresentationSpec spec = PresentationSpec.Baseline;

            Assert.That(() => spec = Present("boom", "shop.pricing_cta.boom"), Throws.Nothing);
            Assert.That(spec, Is.EqualTo(PresentationSpec.Baseline));
        }

        // --- audience predicates fail closed ---------------------------------------------------------------------------

        [Test]
        public void AWorkingPredicateDecidesMembership()
        {
            Assert.That(_fixture.Host.EvaluateAudience("shop.audience.established_player", User(level: 5)),
                Is.True);
            Assert.That(_fixture.Host.EvaluateAudience("shop.audience.established_player", User(level: 1)),
                Is.False);
        }

        [Test]
        public void APredicateThatErrorsExcludesTheUser()
        {
            // Failing open would sweep users into a treatment nobody validated on the strength of a bug,
            // and the experiment would then be measuring the bug.
            _fixture.WritePatch("p.lua",
                "register_audience('broken', function(ctx) return ctx.nothing.here end)");
            _fixture.Host.Reload();

            Assert.That(_fixture.Host.EvaluateAudience("broken", User()), Is.False);
            Assert.That(_fixture.Log.All, Does.Contain("excludes the user"));
        }

        [Test]
        public void APredicateReturningANonBooleanExcludesTheUser()
        {
            _fixture.WritePatch("p.lua", "register_audience('truthy', function(ctx) return 'yes' end)");
            _fixture.Host.Reload();

            Assert.That(_fixture.Host.EvaluateAudience("truthy", User()), Is.False,
                "a truthy string is not a boolean, and guessing what the author meant is how bugs ship");
        }

        [Test]
        public void AnUnregisteredPredicateExcludesTheUser()
        {
            Assert.That(_fixture.Host.EvaluateAudience("never.registered", User()), Is.False);
        }

        [Test]
        public void NoPredicateAtAllMeansEveryoneQualifies()
        {
            // Absent is not the same as broken: an experiment that declares no predicate is not targeted.
            Assert.That(_fixture.Host.EvaluateAudience(null, User()), Is.True);
            Assert.That(_fixture.Host.EvaluateAudience("", User()), Is.True);
        }

        // --- lifecycle -----------------------------------------------------------------------------------------------------

        [Test]
        public void DisposingTwiceIsHarmless()
        {
            _fixture.Host.Dispose();
            Assert.That(() => _fixture.Host.Dispose(), Throws.Nothing);
        }

        [Test]
        public void UsingADisposedHostFailsLoudlyRatherThanQuietly()
        {
            _fixture.Host.Dispose();

            Assert.That(() => _fixture.Host.Present(User(), Assignment("c", "x"), SpecFieldGroup.Pricing,
                PresentationSpec.Baseline), Throws.InstanceOf<System.ObjectDisposedException>());
        }
    }
}
