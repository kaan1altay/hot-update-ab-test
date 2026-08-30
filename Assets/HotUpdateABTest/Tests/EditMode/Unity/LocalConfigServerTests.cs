using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Transport;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Unity
{
    /// <summary>
    /// Covers the dev-only HTTP transport, over a real socket where one can be bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every socket test skips rather than fails when no port can be bound.</b> A firewall prompt, a
    /// locked-down machine or a colleague already running the demo would otherwise turn a green suite red
    /// for reasons that have nothing to do with the code, and a suite that goes red for environmental
    /// reasons stops being read.
    /// </para>
    /// <para>
    /// Nothing else in the repository depends on a socket: every rule about validation, fallback and kill
    /// switches is tested against an in-memory source. This fixture only covers the transport itself.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class LocalConfigServerTests
    {
        private ListLog _log;
        private LocalConfigServer _server;

        [SetUp]
        public void SetUp()
        {
            _log = new ListLog();
            _server = new LocalConfigServer(_log);
        }

        [TearDown]
        public void TearDown()
        {
            _server?.Dispose();
        }

        private void RequireSocket()
        {
            if (_server.Start()) return;

            Assert.Ignore(
                "no port in " + LocalConfigServer.FirstPort + ".." +
                (LocalConfigServer.FirstPort + LocalConfigServer.PortsToTry - 1) +
                " could be bound (" + _server.LastError + "); skipping the socket tests");
        }

        // --- payloads, no socket needed ---------------------------------------------------------------

        [Test]
        public void TheNormalScenarioServesAValidConfig()
        {
            var read = ConfigReader.Read(LocalConfigServer.PayloadFor(ServerScenario.Normal, 1));

            Assert.That(read.IsValid, Is.True, read.Issues.Describe());
            Assert.That(ConfigValidator.Validate(read.Config).IsValid, Is.True);
            Assert.That(read.Config.Experiments.Count, Is.EqualTo(2));
        }

        [Test]
        public void EveryScenarioProducesWhatItClaimsTo()
        {
            // Each of these is a button. If a scenario stopped producing the fault it advertises, the demo
            // would quietly stop demonstrating the guardrail it exists to show.
            Assert.That(ConfigReader.Read(LocalConfigServer.PayloadFor(ServerScenario.MalformedJson, 1)).IsValid,
                Is.False, "malformed");

            Assert.That(ConfigReader.Read(LocalConfigServer.PayloadFor(ServerScenario.BadSchemaVersion, 1))
                .Issues.Describe(), Does.Contain("schema version 99"), "bad schema");

            var ramped = ConfigReader.Read(LocalConfigServer.PayloadFor(ServerScenario.WeightsRamped, 1)).Config;
            Assert.That(ramped.FindExperiment("exp_offer_layout").FindVariant("control").Weight,
                Is.EqualTo(9000), "weights");

            var paused = ConfigReader.Read(LocalConfigServer.PayloadFor(ServerScenario.ExperimentPaused, 1)).Config;
            Assert.That(paused.FindExperiment("exp_offer_layout").IsRunning, Is.False, "paused");
            Assert.That(paused.FindExperiment("exp_pricing_cta").IsRunning, Is.True,
                "pausing one experiment must leave the other layer alone");

            var killed = ConfigReader.Read(LocalConfigServer.PayloadFor(ServerScenario.KillSwitch, 1)).Config;
            foreach (var experiment in killed.Experiments)
            {
                Assert.That(experiment.IsRunning, Is.False, "kill switch: " + experiment.Id);
            }
        }

        [Test]
        public void ChangingScenarioBumpsTheConfigVersion()
        {
            // The client refuses content that changes under an unchanged version label, so a scenario that
            // did not bump would look broken for a completely correct reason.
            _server.SetScenario(ServerScenario.Normal);
            string first = _server.CurrentPayload();

            _server.SetScenario(ServerScenario.WeightsRamped);
            string second = _server.CurrentPayload();

            Assert.That(ConfigReader.Read(first).Config.ConfigVersion,
                Is.Not.EqualTo(ConfigReader.Read(second).Config.ConfigVersion));
        }

        [Test]
        public void TheDirectSourceServesTheSameScenariosWithoutASocket()
        {
            // The fallback when no port binds. Every button still works.
            var source = new DirectConfigSource(_server);

            _server.SetScenario(ServerScenario.KillSwitch);
            var fetched = source.Fetch();

            Assert.That(fetched.Outcome, Is.EqualTo(ConfigFetchOutcome.Fetched));
            Assert.That(ConfigReader.Read(fetched.Payload).Config.FindExperiment("exp_offer_layout").IsRunning,
                Is.False);

            _server.SetScenario(ServerScenario.Offline);
            Assert.That(source.Fetch().Outcome, Is.EqualTo(ConfigFetchOutcome.Unreachable));
        }

        // --- over a real socket -------------------------------------------------------------------------

        [Test]
        public void TheServerBindsALocalhostPortWithoutElevation()
        {
            RequireSocket();

            Assert.That(_server.IsRunning, Is.True);
            Assert.That(_server.Port, Is.InRange(
                LocalConfigServer.FirstPort, LocalConfigServer.FirstPort + LocalConfigServer.PortsToTry - 1));
            Assert.That(_server.Url, Does.StartWith("http://localhost:"));
        }

        [Test]
        public void ASecondServerTakesTheNextPortRatherThanFailing()
        {
            RequireSocket();

            using (var second = new LocalConfigServer(_log))
            {
                if (!second.Start()) Assert.Ignore("no second port available");

                Assert.That(second.Port, Is.Not.EqualTo(_server.Port),
                    "two demos on one machine must not fight over a hard-coded port");
            }
        }

        [Test]
        public void ConfigIsFetchedOverHttpAndParses()
        {
            RequireSocket();

            var source = new HttpConfigSource(() => _server.Url);
            var fetched = source.Fetch();

            Assert.That(fetched.Outcome, Is.EqualTo(ConfigFetchOutcome.Fetched), fetched.Error);

            var read = ConfigReader.Read(fetched.Payload);
            Assert.That(read.IsValid, Is.True, read.Issues.Describe());
        }

        [Test]
        public void TheOfflineScenarioReadsAsUnreachableRatherThanAsABadPayload()
        {
            // A different rung of the fallback ladder, and the demo's status chip shows which. Answering
            // with a 503 rather than a broken body is what keeps them distinguishable.
            RequireSocket();

            var source = new HttpConfigSource(() => _server.Url);
            _server.SetScenario(ServerScenario.Offline);

            var fetched = source.Fetch();

            Assert.That(fetched.Outcome, Is.EqualTo(ConfigFetchOutcome.Unreachable));
            Assert.That(fetched.Error, Does.Contain("503"));
        }

        [Test]
        public void AStoppedServerReadsAsUnreachable()
        {
            RequireSocket();

            var source = new HttpConfigSource(() => _server.Url);
            Assert.That(source.Fetch().Outcome, Is.EqualTo(ConfigFetchOutcome.Fetched));

            _server.Stop();

            Assert.That(source.Fetch().Outcome, Is.EqualTo(ConfigFetchOutcome.Unreachable));
        }

        [Test]
        public void TheServerCanBeStoppedAndStartedAgain()
        {
            // An action pair: the demo's server toggle is pressed repeatedly on camera, and a port that
            // stayed bound after Stop would make the second start fail.
            RequireSocket();

            int first = _server.Port;
            _server.Stop();
            Assert.That(_server.IsRunning, Is.False);

            Assert.That(_server.Start(), Is.True, "restart failed; the port was probably not released");
            Assert.That(_server.Port, Is.EqualTo(first), "and it should reclaim the same port");
        }

        [Test]
        public void AScenarioChangeIsVisibleToTheNextFetch()
        {
            RequireSocket();

            var source = new HttpConfigSource(() => _server.Url);

            _server.SetScenario(ServerScenario.WeightsRamped);
            var config = ConfigReader.Read(source.Fetch().Payload).Config;

            Assert.That(config.FindExperiment("exp_offer_layout").FindVariant("control").Weight,
                Is.EqualTo(9000));
        }
    }
}
