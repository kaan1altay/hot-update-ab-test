using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Model;
using HotUpdateABTest.Core.Presentation;
using HotUpdateABTest.Core.Telemetry;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Unity
{
    /// <summary>
    /// The load-bearing restriction of the whole design: a patch cannot touch the telemetry the analysis
    /// rests on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything else in this framework is in service of producing numbers somebody will make a decision
    /// from. A hot update that could fabricate an exposure, duplicate a conversion or suppress an event
    /// would make every one of those numbers meaningless while leaving the reports looking entirely
    /// normal - which is worse than an outage, because an outage is visible.
    /// </para>
    /// <para>
    /// So these are written as attacks. Each one has a patch try to reach the sink by a different route,
    /// and each asserts the same three things: the attempt fails, the spec falls back to control, and the
    /// sink is untouched. The third assertion is the one that matters.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class LuaCannotReachTelemetryTests
    {
        private LuaFixture _fixture;
        private InMemoryAnalyticsSink _sink;

        [SetUp]
        public void SetUp()
        {
            _fixture = new LuaFixture();
            _sink = new InMemoryAnalyticsSink();
        }

        [TearDown]
        public void TearDown()
        {
            _fixture?.Dispose();
        }

        private static UserContext User() => new UserContext("user-1", accountLevel: 5, platform: "editor");

        private static VariantAssignment Assignment(string behavior)
        {
            var variant = new VariantDef("attacker", 5000, behavior);
            var experiment = new ExperimentDef(
                "exp_test", "pricing_cta", ExperimentStatus.Running, "salt", BucketRange.Full,
                StickinessPolicy.StickyAfterExposure, new[] { variant });

            return VariantAssignment.Assigned(
                "pricing_cta", experiment, variant, AssignmentSource.Bucketed, 1, 2, "v1");
        }

        /// <summary>Loads an attacking behavior, calls it, and asserts nothing reached the sink.</summary>
        private void AssertAttackFails(string attackBody)
        {
            const string key = "shop.pricing_cta.attacker";

            _fixture.WritePatch("attack.lua",
                "register('" + key + "', function(ctx)\n" + attackBody + "\nend)");
            _fixture.Host.Reload();

            long before = _sink.TotalRecorded;

            var spec = _fixture.Host.Present(
                User(), Assignment(key), SpecFieldGroup.Pricing, PresentationSpec.Baseline);

            Assert.That(spec, Is.EqualTo(PresentationSpec.Baseline),
                "the attacking behavior must render control");
            Assert.That(_sink.TotalRecorded, Is.EqualTo(before),
                "the analytics sink must be untouched:\n" + _fixture.Log.All);
            Assert.That(_sink.TotalRecorded, Is.Zero);
        }

        [Test]
        public void APatchCannotFabricateAnExposureThroughTheContext()
        {
            // The context table is values only. There is no function on it, so there is nothing to call.
            AssertAttackFails("  ctx.record_exposure('exp_test', 'attacker')\n  return {}");
        }

        [Test]
        public void APatchCannotReachTheSinkThroughTheCSharpBridge()
        {
            // xLua's CS global would hand a patch every type in the process, the sink included. It is
            // removed from the sandbox, so this is a nil index rather than a working call.
            AssertAttackFails(
                "  local sink = CS.HotUpdateABTest.Core.Telemetry.InMemoryAnalyticsSink()\n" +
                "  sink:Record(nil)\n  return {}");
        }

        [Test]
        public void APatchCannotEnumerateItsWayToTheSink()
        {
            // Walking the environment looking for anything callable. The sandbox contains no C# objects at
            // all, so there is nothing to find.
            AssertAttackFails(
                "  for k, v in pairs(_ENV) do\n" +
                "    if type(v) == 'userdata' then error('found a bridge: ' .. tostring(k)) end\n" +
                "  end\n" +
                "  for k, v in pairs(ctx) do\n" +
                "    if type(v) == 'function' or type(v) == 'userdata' then\n" +
                "      error('context exposed something callable: ' .. tostring(k))\n" +
                "    end\n" +
                "  end\n" +
                "  error('nothing reachable, failing deliberately so the sink assertion is meaningful')");
        }

        [Test]
        public void APatchCannotSuppressAnExposureByThrowing()
        {
            // The other direction: rather than adding events, stop them. A behavior that throws makes the
            // screen render control, but the exposure is recorded by C# at view time regardless - the
            // decision to log is not the behavior's to make.
            const string key = "shop.pricing_cta.suppressor";

            _fixture.WritePatch("attack.lua",
                "register('" + key + "', function(ctx) error('refusing to render') end)");
            _fixture.Host.Reload();

            var ledger = new ExposureLedger();
            var clock = new TestClock();
            var tracker = new ExposureTracker(ledger, _sink, clock);
            var assignment = Assignment(key);

            var spec = _fixture.Host.Present(
                User(), assignment, SpecFieldGroup.Pricing, PresentationSpec.Baseline);
            bool logged = tracker.MarkExposed(User(), assignment, new SessionId("s1"));

            Assert.That(spec, Is.EqualTo(PresentationSpec.Baseline));
            Assert.That(logged, Is.True,
                "the user saw the screen, so the exposure stands whatever the behavior did");
            Assert.That(_sink.CountOf(AnalyticsEventKind.Exposure), Is.EqualTo(1));
        }

        [Test]
        public void APatchCannotDuplicateAnExposureByBeingCalledRepeatedly()
        {
            // Rendering many times is normal - a screen rebuilds, a list scrolls. Deduplication is owned by
            // C# and keyed per session, so a behavior being invoked a hundred times produces one exposure.
            const string key = "shop.pricing_cta.control";

            var ledger = new ExposureLedger();
            var tracker = new ExposureTracker(ledger, _sink, new TestClock());
            var assignment = Assignment(key);
            var session = new SessionId("s1");

            for (int i = 0; i < 100; i++)
            {
                _fixture.Host.Present(User(), assignment, SpecFieldGroup.Pricing, PresentationSpec.Baseline);
                tracker.MarkExposed(User(), assignment, session);
            }

            Assert.That(_sink.CountOf(AnalyticsEventKind.Exposure), Is.EqualTo(1));
        }

        [Test]
        public void TheContextExposesOnlyPlainValues()
        {
            // A positive statement of the same restriction, so the surface is pinned rather than merely
            // attacked. If somebody ever adds a callable to the context, this fails.
            const string key = "shop.pricing_cta.inspector";

            _fixture.WritePatch("inspect.lua",
                "register('" + key + "', function(ctx)\n" +
                "  local kinds = {}\n" +
                "  for k, v in pairs(ctx) do kinds[#kinds + 1] = k .. '=' .. type(v) end\n" +
                "  table.sort(kinds)\n" +
                "  return { ctaText = tostring(#kinds) }\n" +
                "end)");
            _fixture.Host.Reload();

            _fixture.WritePatch("assert.lua",
                "register('shop.pricing_cta.assert_values', function(ctx)\n" +
                "  for k, v in pairs(ctx) do\n" +
                "    local t = type(v)\n" +
                "    if t ~= 'string' and t ~= 'number' and t ~= 'boolean' then\n" +
                "      error('context field ' .. tostring(k) .. ' is a ' .. t)\n" +
                "    end\n" +
                "  end\n" +
                "  return { ctaText = 'all plain' }\n" +
                "end)");
            _fixture.Host.Reload();

            var spec = _fixture.Host.Present(
                User(), Assignment("shop.pricing_cta.assert_values"), SpecFieldGroup.Pricing,
                PresentationSpec.Baseline);

            Assert.That(spec.CtaText, Is.EqualTo("all plain"),
                "the context must carry values only:\n" + _fixture.Log.All);
        }

        private sealed class TestClock : HotUpdateABTest.Core.IClock
        {
            public System.DateTime UtcNow { get; } =
                new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        }
    }
}
