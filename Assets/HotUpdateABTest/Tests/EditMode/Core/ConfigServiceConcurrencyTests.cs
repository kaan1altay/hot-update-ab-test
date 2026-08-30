using System;
using System.Collections.Generic;
using System.Threading;
using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Exercises the threading contract: readers on many threads while the configuration is swapped
    /// underneath them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The contract is written down on <see cref="ConfigService"/>, but a documented contract that is never
    /// exercised is a comment. These tests exist because the HTTP transport in the next slice fetches off
    /// the main thread, and the decision about what that is allowed to do is made here rather than there.
    /// </para>
    /// <para>
    /// They are bounded by iteration count rather than by wall time, so they take the same work on a fast
    /// machine and a slow one and cannot flake into passing by simply not racing.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class ConfigServiceConcurrencyTests
    {
        private const int ReaderThreads = 4;
        private const int ReadsPerThread = 20000;
        private const int Swaps = 200;

        [Test]
        public void AReaderNeverObservesAHalfAppliedConfiguration()
        {
            // Each published config is internally consistent but different from its neighbours: the two
            // experiments swap which half of the layer they own, and the variant weights invert. A reader
            // that could see a torn snapshot would find a variant that does not belong to its experiment,
            // or two experiments claiming its bucket.
            var payloads = new[] { SplitLayerPayload("1", 5000), SplitLayerPayload("2", 3000) };

            var source = new InMemoryConfigSource(payloads[0]);
            var service = new ConfigService(source, new ManualTestClock(), new RecordingLog());
            service.Refresh();

            var failures = new List<string>();
            var stop = new ManualResetEventSlim(false);
            var readersReady = new CountdownEvent(ReaderThreads);

            var readers = new Thread[ReaderThreads];
            for (int t = 0; t < ReaderThreads; t++)
            {
                int threadIndex = t;
                readers[t] = new Thread(() =>
                {
                    var resolver = new ExperimentResolver();
                    readersReady.Signal();

                    for (int i = 0; i < ReadsPerThread; i++)
                    {
                        // One volatile read, then everything else against that same immutable object. This
                        // is the pattern the contract promises is safe.
                        var snapshot = service.CurrentSnapshot;
                        var user = new UserContext("user-" + threadIndex + "-" + i);

                        var assignment = resolver.Resolve(snapshot, user, "offer_layout");
                        string problem = Inspect(snapshot, assignment);

                        if (problem != null)
                        {
                            lock (failures) { failures.Add(problem); }
                            return;
                        }
                    }
                })
                { IsBackground = true, Name = "reader-" + t };
            }

            var writer = new Thread(() =>
            {
                readersReady.Wait();
                for (int i = 0; i < Swaps && !stop.IsSet; i++)
                {
                    service.Apply(payloads[i % payloads.Length]);
                }
            })
            { IsBackground = true, Name = "writer" };

            foreach (var reader in readers) reader.Start();
            writer.Start();

            foreach (var reader in readers)
            {
                Assert.That(reader.Join(TimeSpan.FromSeconds(60)), Is.True, "reader thread did not finish");
            }

            stop.Set();
            writer.Join(TimeSpan.FromSeconds(60));

            Assert.That(failures, Is.Empty, failures.Count == 0 ? "" : failures[0]);
        }

        [Test]
        public void ConcurrentAppliesLeaveOneCoherentConfigurationInForce()
        {
            // Several transports racing to apply. The lock serialises them, so the result must be exactly
            // one of the payloads offered, never a blend.
            var payloads = new List<string>();
            for (int i = 0; i < 8; i++) payloads.Add(SplitLayerPayload("v" + i, 1000 + (i * 500)));

            var service = new ConfigService(
                new InMemoryConfigSource(payloads[0]), new ManualTestClock(), new RecordingLog());

            var threads = new Thread[payloads.Count];
            for (int t = 0; t < payloads.Count; t++)
            {
                string payload = payloads[t];
                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < 200; i++) service.Apply(payload);
                })
                { IsBackground = true };
            }

            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads)
            {
                Assert.That(thread.Join(TimeSpan.FromSeconds(60)), Is.True);
            }

            var final = service.CurrentSnapshot;
            var expectedVersions = new List<string>();
            for (int i = 0; i < payloads.Count; i++) expectedVersions.Add("v" + i);

            Assert.That(expectedVersions, Does.Contain(final.ConfigVersion));
            Assert.That(ConfigValidator.Validate(final.Config).IsValid, Is.True);
            Assert.That(final.Config.Experiments.Count, Is.EqualTo(2));
        }

        [Test]
        public void ASnapshotCapturedBeforeASwapStaysUsableAfterIt()
        {
            var service = new ConfigService(
                new InMemoryConfigSource(SplitLayerPayload("1", 5000)), new ManualTestClock(), new RecordingLog());
            service.Refresh();

            var held = service.CurrentSnapshot;
            var resolver = new ExperimentResolver();
            var user = new UserContext("user-1");
            var before = resolver.Resolve(held, user, "offer_layout");

            for (int i = 2; i < 20; i++) service.Apply(SplitLayerPayload(i.ToString(), 1000 + (i * 100)));

            var after = resolver.Resolve(held, user, "offer_layout");

            Assert.That(after.ExperimentId, Is.EqualTo(before.ExperimentId));
            Assert.That(after.VariantId, Is.EqualTo(before.VariantId));
            Assert.That(held.ConfigVersion, Is.EqualTo("1"));
        }

        /// <summary>Returns a description of what is wrong with an assignment, or null when it is coherent.</summary>
        private static string Inspect(ConfigSnapshot snapshot, VariantAssignment assignment)
        {
            var config = snapshot.Config;

            if (assignment.ConfigVersion != config.ConfigVersion)
            {
                return "assignment claims version '" + assignment.ConfigVersion + "' but the snapshot is '" +
                       config.ConfigVersion + "'";
            }

            if (!assignment.IsAssigned) return null;

            var experiment = config.FindExperiment(assignment.ExperimentId);
            if (experiment == null)
            {
                return "assigned to experiment '" + assignment.ExperimentId + "' which is not in the snapshot";
            }

            if (experiment.FindVariant(assignment.VariantId) == null)
            {
                return "assigned to variant '" + assignment.VariantId + "' which is not in experiment '" +
                       experiment.Id + "'";
            }

            if (!experiment.IsRunning) return "assigned to experiment '" + experiment.Id + "' which is not running";

            // The invariant that matters most: within this snapshot, exactly one running experiment in the
            // layer may claim the user's bucket.
            int claimants = 0;
            foreach (var candidate in config.Experiments)
            {
                if (!candidate.IsRunning) continue;
                if (candidate.LayerId != experiment.LayerId) continue;
                if (candidate.Allocation.Contains(assignment.LayerBucket)) claimants++;
            }

            if (claimants != 1)
            {
                return "bucket " + assignment.LayerBucket + " is claimed by " + claimants +
                       " running experiments in layer '" + experiment.LayerId + "'";
            }

            return null;
        }

        /// <summary>Two experiments splitting one layer at <paramref name="boundary"/>.</summary>
        private static string SplitLayerPayload(string version, int boundary)
        {
            return ConfigJson.New(version)
                .Layer("offer_layout")
                .Experiment("exp_low", "offer_layout", from: 0, to: boundary, variants: new[]
                {
                    ConfigJson.Variant("control", boundary),
                    ConfigJson.Variant("treatment", 10000 - boundary)
                })
                .Experiment("exp_high", "offer_layout", from: boundary, to: 10000, variants: new[]
                {
                    ConfigJson.Variant("control", 10000 - boundary),
                    ConfigJson.Variant("treatment", boundary)
                })
                .Build();
        }
    }
}
