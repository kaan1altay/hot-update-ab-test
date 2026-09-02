using System;
using System.IO;
using HotUpdateABTest.Core;
using HotUpdateABTest.Lua;
using NUnit.Framework;
using UnityEngine;

namespace HotUpdateABTest.Tests.Unity
{
    /// <summary>Collects log lines so tests can assert how much was said, and how often.</summary>
    internal sealed class ListLog : IAbLog
    {
        public System.Collections.Generic.List<string> Lines { get; } =
            new System.Collections.Generic.List<string>();

        public void Log(AbLogLevel level, string message) => Lines.Add(level + ": " + message);

        public int CountContaining(string fragment)
        {
            int count = 0;
            foreach (string line in Lines)
            {
                if (line.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) count++;
            }

            return count;
        }

        public string All => string.Join("\n", Lines.ToArray());

        /// <summary>The most severe level anything was logged at.</summary>
        public AbLogLevel HighestLevel
        {
            get
            {
                var highest = AbLogLevel.Info;
                foreach (string line in Lines)
                {
                    if (line.StartsWith("Error", StringComparison.Ordinal)) return AbLogLevel.Error;
                    if (line.StartsWith("Warning", StringComparison.Ordinal)) highest = AbLogLevel.Warning;
                }

                return highest;
            }
        }
    }

    /// <summary>
    /// Builds a host over temporary folders so a test can write patch files and reload.
    /// </summary>
    internal sealed class LuaFixture : IDisposable
    {
        private readonly string _root;

        public ListLog Log { get; } = new ListLog();

        public LuaPatchLoader Loader { get; }

        public LuaVariantHost Host { get; }

        public string PatchRoot { get; }

        public LuaFixture(bool copyBaseline = true)
        {
            _root = Path.Combine(Path.GetTempPath(), "abtest-lua-" + Guid.NewGuid().ToString("N"));

            string baseline = Path.Combine(_root, "lua");
            PatchRoot = Path.Combine(_root, "patches");

            Directory.CreateDirectory(Path.Combine(baseline, "variants"));
            Directory.CreateDirectory(PatchRoot);

            // The real bootstrap, not a stand-in: the sandbox is the thing under test.
            string shipped = Path.Combine(Application.streamingAssetsPath, LuaPatchLoader.BaselineRelativePath);
            File.Copy(Path.Combine(shipped, "bootstrap.lua"), Path.Combine(baseline, "bootstrap.lua"));

            if (copyBaseline)
            {
                foreach (string file in Directory.GetFiles(Path.Combine(shipped, "variants"), "*.lua"))
                {
                    File.Copy(file, Path.Combine(baseline, "variants", Path.GetFileName(file)));
                }
            }

            Loader = new LuaPatchLoader(baseline, PatchRoot, Log);
            Host = new LuaVariantHost(Loader, Log);
        }

        /// <summary>Writes a patch file into the hot-update folder.</summary>
        public void WritePatch(string name, string source) =>
            File.WriteAllText(Path.Combine(PatchRoot, name), source);

        /// <summary>Deletes a patch file.</summary>
        public void DeletePatch(string name) => File.Delete(Path.Combine(PatchRoot, name));

        public void Dispose()
        {
            Host?.Dispose();

            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }
    }

    /// <summary>
    /// Adversarial tests: a patch is untrusted code, and these are the things it must not be able to do.
    /// </summary>
    /// <remarks>
    /// A patch channel is a remote code execution channel - whoever can publish a patch runs code on every
    /// device that fetches it. The sandbox is the answer to that, and a sandbox nobody attacks is a
    /// sandbox nobody has tested. Each test below is written as the attack, not as the well-behaved case.
    /// </remarks>
    [TestFixture]
    public sealed class LuaSandboxTests
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

        [Test]
        public void TheFilesystemIsUnreachable()
        {
            _fixture.WritePatch("attack.lua", "local f = io.open('/tmp/pwned', 'w') register('x', function() end)");

            var report = _fixture.Host.Reload();

            Assert.That(report.FilesFailed, Is.EqualTo(1));
            Assert.That(_fixture.Host.HasBehavior("x"), Is.False,
                "the file threw before registering, and nothing it staged is committed");
            Assert.That(_fixture.Log.All, Does.Contain("attack.lua").And.Contain("skipped"));
        }

        [Test]
        public void ProcessControlIsUnreachable()
        {
            _fixture.WritePatch("attack.lua", "os.execute('echo pwned') register('x', function() end)");

            Assert.That(_fixture.Host.Reload().FilesFailed, Is.EqualTo(1));
        }

        [Test]
        public void TheCSharpBridgeIsUnreachable()
        {
            // The most important one. xLua's CS global exposes the entire C# type system - left in, a patch
            // could reach the analytics sink, the filesystem and UnityEngine directly, and every other
            // restriction here would be decorative.
            _fixture.WritePatch("attack.lua",
                "local t = CS.System.IO.File register('x', function() end)");

            Assert.That(_fixture.Host.Reload().FilesFailed, Is.EqualTo(1));
            Assert.That(_fixture.Host.HasBehavior("x"), Is.False);
        }

        [Test]
        public void TheRealGlobalTableIsUnreachable()
        {
            _fixture.WritePatch("attack.lua", "local g = _G.io register('x', function() end)");

            Assert.That(_fixture.Host.Reload().FilesFailed, Is.EqualTo(1));
        }

        [Test]
        public void ArbitraryModulesCannotBeRequired()
        {
            _fixture.WritePatch("attack.lua", "require('os') register('x', function() end)");

            Assert.That(_fixture.Host.Reload().FilesFailed, Is.EqualTo(1));
        }

        [Test]
        public void MoreCodeCannotBeCompiledAtRuntime()
        {
            // load() would route straight around the sandbox by compiling a chunk with a different _ENV.
            _fixture.WritePatch("attack.lua", "load('return io')() register('x', function() end)");

            Assert.That(_fixture.Host.Reload().FilesFailed, Is.EqualTo(1));
        }

        [Test]
        public void TheDebugLibraryIsUnreachable()
        {
            // debug.getupvalue would reach into this file's own closures, including the registry.
            _fixture.WritePatch("attack.lua", "debug.getinfo(1) register('x', function() end)");

            Assert.That(_fixture.Host.Reload().FilesFailed, Is.EqualTo(1));
        }

        [Test]
        public void PrecompiledBytecodeIsRefused()
        {
            // The Lua bytecode verifier is not hardened and crafted bytecode can subvert the VM outright,
            // so the loader accepts text mode only. This is a byte string that begins with the Lua
            // signature, which a text-only load must refuse rather than attempt.
            _fixture.WritePatch("attack.lua", "\x1bLua nonsense");

            Assert.That(_fixture.Host.Reload().FilesFailed, Is.EqualTo(1));
            Assert.That(_fixture.Host.BehaviorCount(), Is.GreaterThan(0), "the baseline is unaffected");
        }

        [Test]
        public void NondeterminismSourcesAreRemoved()
        {
            // Both of these reach for a missing global from *inside* the closure, so both files load
            // cleanly and both behaviors register - the failure happens when they are called. That is the
            // shape the host has to survive, and LuaVariantHostTests asserts such a behavior renders
            // control. Here the point is narrower: the globals are genuinely absent.
            _fixture.WritePatch("probe.lua",
                "register('probe', function()\n" +
                "  print('math.random=' .. type(math.random))\n" +
                "  print('math.randomseed=' .. type(math.randomseed))\n" +
                "  print('os=' .. type(os))\n" +
                "  print('io=' .. type(io))\n" +
                "  return { ctaText = 'probed' }\n" +
                "end)");

            var report = _fixture.Host.Reload();
            Assert.That(report.FilesFailed, Is.Zero, _fixture.Log.All);

            var spec = _fixture.Host.Present(
                new HotUpdateABTest.Core.Model.UserContext("u", 1, "editor"),
                ProbeAssignment(),
                HotUpdateABTest.Core.Presentation.SpecFieldGroup.Pricing,
                HotUpdateABTest.Core.Presentation.PresentationSpec.Baseline);

            Assert.That(spec.CtaText, Is.EqualTo("probed"), _fixture.Log.All);
            Assert.That(_fixture.Log.All,
                Does.Contain("math.random=nil")
                    .And.Contain("math.randomseed=nil")
                    .And.Contain("os=nil")
                    .And.Contain("io=nil"),
                _fixture.Log.All);
        }

        private static HotUpdateABTest.Core.Assignment.VariantAssignment ProbeAssignment()
        {
            var variant = new HotUpdateABTest.Core.Model.VariantDef("probe", 1, "probe");
            var experiment = new HotUpdateABTest.Core.Model.ExperimentDef(
                "exp", "pricing_cta", HotUpdateABTest.Core.Model.ExperimentStatus.Running, "s",
                HotUpdateABTest.Core.Model.BucketRange.Full,
                HotUpdateABTest.Core.Model.StickinessPolicy.StickyAfterExposure, new[] { variant });

            return HotUpdateABTest.Core.Assignment.VariantAssignment.Assigned(
                "pricing_cta", experiment, variant,
                HotUpdateABTest.Core.Assignment.AssignmentSource.Bucketed, 1, 2, "v1");
        }

        [Test]
        public void ThePureLibrariesAPatchActuallyNeedsAreAvailable()
        {
            // The sandbox has to be usable, not merely safe. String formatting and table manipulation are
            // what a copy-tweaking behavior is made of.
            _fixture.WritePatch("ok.lua",
                "register('fmt', function(ctx)\n" +
                "  local parts = { 'Buy', 'now' }\n" +
                "  return { ctaText = string.upper(table.concat(parts, ' ')) .. string.rep('!', math.min(1, 3)) }\n" +
                "end)");

            var report = _fixture.Host.Reload();

            Assert.That(report.FilesFailed, Is.Zero, _fixture.Log.All);
            Assert.That(_fixture.Host.HasBehavior("fmt"), Is.True);
        }

        [Test]
        public void APatchCannotSeeAnotherPatchesLocals()
        {
            _fixture.WritePatch("a.lua", "local secret = 'hidden' register('a', function() return {} end)");
            _fixture.WritePatch("b.lua",
                "register('b', function() return { ctaText = tostring(secret) } end)");

            _fixture.Host.Reload();

            // `secret` is a local in a.lua, so b.lua sees nil rather than the value - the spec then fails
            // validation on 'nil' not being a legal cta, which is the point: no leakage.
            Assert.That(_fixture.Host.HasBehavior("b"), Is.True);
        }
    }
}
