using NUnit.Framework;
using XLua;

namespace HotUpdateABTest.Tests.Unity
{
    /// <summary>
    /// Proves the Lua VM actually loads and runs in this project, in this Editor, under batchmode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in the framework needs Lua yet - variant behavior arrives later. This fixture exists now on
    /// purpose. xLua is backed by a native library that is vendored for desktop x64 only, and if it fails
    /// to load under <c>-batchmode -nographics</c> then a large part of the planned test strategy is built
    /// on sand. Finding that out from a five-line test today is very much cheaper than finding it out from
    /// a half-finished Lua bridge later.
    /// </para>
    /// <para>
    /// The fixture also fixes the disposal discipline the real bridge will follow: cached
    /// <see cref="LuaFunction"/> handles are released before the environment they came from, and
    /// <see cref="LuaEnv.Dispose"/> runs in tear-down whatever the test did, because a leaked
    /// <see cref="LuaEnv"/> survives into the next test and turns one failure into a cascade.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class LuaEnvironmentSmokeTests
    {
        private LuaEnv _lua;

        [SetUp]
        public void SetUp()
        {
            _lua = new LuaEnv();
        }

        [TearDown]
        public void TearDown()
        {
            if (_lua == null) return;

            _lua.Dispose();
            _lua = null;
        }

        [Test]
        public void TheNativeVirtualMachineLoadsAndEvaluatesAnExpression()
        {
            object[] result = _lua.DoString("return 1 + 1");

            Assert.That(result, Is.Not.Null.And.Length.EqualTo(1));
            Assert.That(System.Convert.ToInt32(result[0]), Is.EqualTo(2));
        }

        [Test]
        public void ValuesCrossFromCSharpIntoLuaAndBack()
        {
            _lua.Global.Set("bucket_count", 10000);

            object[] result = _lua.DoString("return bucket_count / 2");

            Assert.That(System.Convert.ToInt32(result[0]), Is.EqualTo(5000));
        }

        [Test]
        public void AFunctionDefinedInLuaCanBeCalledFromCSharp()
        {
            // The shape the variant behavior seam will use: Lua defines a function, C# holds a handle to it
            // and calls it. Holding the handle is the part that has to be disposed in the right order.
            _lua.DoString("function pick_label(discounted) return discounted and 'SALE' or 'BUY' end");

            var pick = _lua.Global.Get<LuaFunction>("pick_label");
            try
            {
                Assert.That(pick, Is.Not.Null);
                Assert.That(pick.Call(true)[0], Is.EqualTo("SALE"));
                Assert.That(pick.Call(false)[0], Is.EqualTo("BUY"));
            }
            finally
            {
                pick?.Dispose();
            }
        }

        [Test]
        public void ALuaErrorSurfacesAsAManagedExceptionRatherThanKillingTheProcess()
        {
            // Every call into a hot-updated patch is wrapped, because a patch is exactly the code most
            // likely to be wrong. This confirms the failure arrives as something catchable.
            Assert.That(() => _lua.DoString("error('patch blew up')"), Throws.InstanceOf<LuaException>());
        }

        [Test]
        public void ACustomLoaderIsConsultedForRequire()
        {
            // The hot-update channel works by putting a loader in front of xLua's own, so a patch file on
            // disk can shadow a shipped module. This asserts the hook fires and its return value is used.
            bool loaderCalled = false;

            _lua.AddLoader((ref string chunkName) =>
            {
                if (chunkName != "abtest.probe") return null;

                loaderCalled = true;
                return System.Text.Encoding.UTF8.GetBytes("return { answer = 42 }");
            });

            object[] result = _lua.DoString("local m = require('abtest.probe') return m.answer");

            Assert.That(loaderCalled, Is.True, "the custom loader was never consulted");
            Assert.That(System.Convert.ToInt32(result[0]), Is.EqualTo(42));
        }
    }
}
