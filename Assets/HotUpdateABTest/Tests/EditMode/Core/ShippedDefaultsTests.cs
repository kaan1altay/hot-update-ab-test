using System.IO;
using HotUpdateABTest.Core;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Checks the artifact that ships inside the build, by reading the actual file rather than a copy of
    /// it pasted into a string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shipped defaults are the floor of the fallback ladder, which means they are the one config that
    /// is never exercised during normal development - it only matters on a first launch with no network,
    /// the situation nobody tests by hand. A typo in it would sit there undetected until a real user hit
    /// exactly that case.
    /// </para>
    /// <para>
    /// Reading the file from disk is deliberate. Asserting against an inline copy would prove that the
    /// copy is valid, which is not the question.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ShippedDefaultsTests
    {
        private const string RelativePath = "Assets/StreamingAssets/abtest/default_config.json";

        private static string LoadPayload()
        {
            string path = RepoPaths.Resolve(RelativePath);

            Assert.That(path, Is.Not.Null,
                "could not locate the repository root from " + System.AppContext.BaseDirectory);
            Assert.That(File.Exists(path), Is.True, "the shipped default config is missing from " + path);

            return File.ReadAllText(path);
        }

        [Test]
        public void TheShippedConfigParsesAndValidates()
        {
            var read = ConfigReader.Read(LoadPayload());

            Assert.That(read.IsValid, Is.True, read.Issues.Describe());
            Assert.That(ConfigValidator.Validate(read.Config).IsValid, Is.True,
                ConfigValidator.Validate(read.Config).Describe());
        }

        [Test]
        public void EveryShippedExperimentIsPresentButStopped()
        {
            // The distinction that makes this artifact worth having. An empty document would leave a screen
            // asking about an experiment with nothing to answer; this way it gets a definite "not running"
            // and renders control, and the metrics panel has rows to draw before the first fetch.
            var config = ConfigReader.Read(LoadPayload()).Config;

            Assert.That(config.Experiments, Is.Not.Empty,
                "shipped defaults must declare the experiments, not be an empty document");

            foreach (var experiment in config.Experiments)
            {
                Assert.That(experiment.Status, Is.EqualTo(ExperimentStatus.Stopped),
                    "experiment '" + experiment.Id + "' ships enabled; the defaults must be inert");
            }
        }

        [Test]
        public void EveryShippedExperimentDeclaresAControlArm()
        {
            foreach (var experiment in ConfigReader.Read(LoadPayload()).Config.Experiments)
            {
                Assert.That(experiment.Control, Is.Not.Null, experiment.Id);
            }
        }

        [Test]
        public void AColdStartWithNoCacheAndNoServerResolvesCleanlyToControl()
        {
            // The scenario this artifact exists for, end to end: fresh install, aeroplane mode, first
            // launch. Nothing may throw, and every layer must resolve to a definite non-assignment.
            var log = new RecordingLog();
            var clock = new ManualTestClock();

            var source = new InMemoryConfigSource();
            source.GoOffline("no network on first launch");

            var service = new ConfigService(source, clock, log, new ConfigServiceOptions
            {
                Cache = new InMemoryConfigCache(),
                ShippedDefaultsPayload = LoadPayload()
            });

            service.Initialize();
            service.Refresh();

            Assert.That(service.CurrentSnapshot.Source, Is.EqualTo(ConfigSourceKind.ShippedDefaults));

            var resolver = new ExperimentResolver(new InMemoryAssignmentStore());
            var snapshot = service.CurrentSnapshot;

            foreach (string userId in TestConfigs.Users(500))
            {
                var results = resolver.ResolveAll(snapshot, new UserContext(userId, platform: "editor"));

                Assert.That(results.Count, Is.EqualTo(snapshot.Config.Layers.Count));
                foreach (var assignment in results)
                {
                    Assert.That(assignment.IsAssigned, Is.False,
                        "a stopped experiment must assign nobody, but " + userId + " got " +
                        assignment.Describe());
                }
            }

            Assert.That(log.CountContaining("Error"), Is.Zero, log.All);
        }

        [Test]
        public void TheShippedLayersMatchTheOnesTheDemoUses()
        {
            // Keeps the artifact honest as the demo grows: shipping defaults for layers that no longer
            // exist would quietly stop covering the layers that do.
            var config = ConfigReader.Read(LoadPayload()).Config;

            Assert.That(config.FindLayer("offer_layout"), Is.Not.Null);
            Assert.That(config.FindLayer("pricing_cta"), Is.Not.Null);
        }

        [Test]
        public void TheShippedDefaultsCanBeStartedByFlippingStatusAlone()
        {
            // If the only thing standing between the defaults and a working experiment is the status field,
            // then the artifact is a faithful description of the real experiments rather than a stub. The
            // validator's running-experiment rules - a control arm, non-zero weights, no overlap - all have
            // to hold once it is switched on.
            string running = LoadPayload().Replace("\"status\": \"stopped\"", "\"status\": \"running\"");
            var read = ConfigReader.Read(running);

            Assert.That(read.IsValid, Is.True, read.Issues.Describe());

            var validation = ConfigValidator.Validate(read.Config);
            Assert.That(validation.IsValid, Is.True, validation.Describe());

            foreach (var experiment in read.Config.Experiments)
            {
                Assert.That(experiment.IsRunning, Is.True, "the replace should have flipped " + experiment.Id);
            }
        }
    }
}
