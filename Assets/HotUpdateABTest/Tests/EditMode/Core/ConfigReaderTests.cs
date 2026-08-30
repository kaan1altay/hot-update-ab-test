using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers reading a payload, and above all the quality of the complaint when it cannot be read.
    /// </summary>
    /// <remarks>
    /// The messages are treated as a deliverable rather than as debugging output. Several tests below
    /// assert on wording, not just on the fact that something was rejected, because the reason the project
    /// pays for Newtonsoft plus a hand-written reader - instead of a serializer that maps fields
    /// automatically - is precise diagnosis. If the messages are allowed to rot, that cost bought nothing.
    /// </remarks>
    [TestFixture]
    public sealed class ConfigReaderTests
    {
        [Test]
        public void AWellFormedPayloadIsRead()
        {
            var result = ConfigReader.Read(ConfigJson.Demo("7").Build());

            Assert.That(result.IsValid, Is.True, result.Issues.Describe());
            Assert.That(result.Config.ConfigVersion, Is.EqualTo("7"));
            Assert.That(result.Config.Layers.Count, Is.EqualTo(2));
            Assert.That(result.Config.Experiments.Count, Is.EqualTo(2));

            var experiment = result.Config.FindExperiment("exp_offer_layout");
            Assert.That(experiment.Status, Is.EqualTo(ExperimentStatus.Running));
            Assert.That(experiment.Allocation, Is.EqualTo(BucketRange.Full));
            Assert.That(experiment.Stickiness, Is.EqualTo(StickinessPolicy.StickyAfterExposure));
            Assert.That(experiment.Variants.Count, Is.EqualTo(2));
        }

        // --- the distinction the whole reader exists for -------------------------------------------

        [Test]
        public void AMissingWeightIsRejectedAndSaysSoInThoseWords()
        {
            string json = ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", variants: new[]
                {
                    ConfigJson.Variant("control", 5000),
                    ConfigJson.VariantWithoutWeight("treatment")
                })
                .Build();

            var result = ConfigReader.Read(json);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Issues.Describe(),
                Does.Contain("variant 'treatment'").And.Contain("'weight' is missing")
                    .And.Contain("absent is not the same as 0"));
        }

        [Test]
        public void AWeightOfZeroIsAcceptedBecauseItMeansARetiredArm()
        {
            string json = ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", variants: new[]
                {
                    ConfigJson.Variant("control", 5000),
                    ConfigJson.Variant("retired", 0),
                    ConfigJson.Variant("treatment", 5000)
                })
                .Build();

            var result = ConfigReader.Read(json);

            Assert.That(result.IsValid, Is.True, result.Issues.Describe());
            Assert.That(result.Config.FindExperiment("exp_x").FindVariant("retired").Weight, Is.Zero);
        }

        [Test]
        public void ANegativeWeightIsRejected()
        {
            string json = ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", variants: new[] { ConfigJson.Variant("control", -1) })
                .Build();

            Assert.That(ConfigReader.Read(json).Issues.Describe(),
                Does.Contain("is negative").And.Contain("use 0 to retire an arm"));
        }

        // --- the schema gate ------------------------------------------------------------------------

        [Test]
        public void AnUnknownSchemaVersionIsRejectedWholesale()
        {
            var payload = ConfigJson.Demo();
            payload.SchemaVersion = 99;

            var result = ConfigReader.Read(payload.Build());

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Issues.Describe(), Does.Contain("schema version 99 is not supported"));
        }

        [Test]
        public void AnUnknownSchemaVersionShortCircuitsEverythingElse()
        {
            // The payload is broken in several other ways too. None of them should be reported: the
            // contract they would be judged against is not the contract this payload claims to follow, and
            // a wall of consequential errors reads as though the operator's config were at fault when in
            // fact the client is old.
            string json = "{\"schemaVersion\":99,\"layers\":\"not-an-array\"}";

            var result = ConfigReader.Read(json);

            Assert.That(result.Issues.ErrorCount, Is.EqualTo(1));
            Assert.That(result.Issues.Describe(), Does.Contain("schema version"));
        }

        [Test]
        public void AMissingSchemaVersionIsRejected()
        {
            Assert.That(ConfigReader.Read("{\"configVersion\":\"1\"}").IsValid, Is.False);
        }

        // --- malformed and empty --------------------------------------------------------------------

        [Test]
        public void MalformedJsonIsRejectedWithoutThrowing()
        {
            var result = ConfigReader.Read("{\"schemaVersion\": 1, \"layers\": [");

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Issues.Describe(), Does.Contain("not valid JSON"));
        }

        [Test]
        public void AnEmptyOrWhitespacePayloadIsRejected()
        {
            Assert.That(ConfigReader.Read("").Issues.Describe(), Does.Contain("empty"));
            Assert.That(ConfigReader.Read("   ").Issues.Describe(), Does.Contain("empty"));
            Assert.That(ConfigReader.Read(null).Issues.Describe(), Does.Contain("empty"));
        }

        [Test]
        public void AJsonArrayAtTheRootIsRejectedRatherThanCrashing()
        {
            Assert.That(ConfigReader.Read("[1,2,3]").IsValid, Is.False);
        }

        // --- collected, not first-only ---------------------------------------------------------------

        [Test]
        public void EveryStructuralProblemIsReportedFromOneRead()
        {
            // Three separate faults. An operator who has broken three things should learn all three from
            // one refresh rather than one deploy at a time.
            string json = ConfigJson.New()
                .RawLayer("{\"id\":\"l\"}")
                .Experiment("exp_a", "l", status: "sideways")
                .Experiment("exp_b", "l", variants: new[] { ConfigJson.VariantWithoutWeight("control") })
                .Build();

            var issues = ConfigReader.Read(json).Issues;

            Assert.That(issues.ErrorCount, Is.GreaterThanOrEqualTo(3), issues.Describe());
            Assert.That(issues.Describe(), Does.Contain("'salt' is missing"));
            Assert.That(issues.Describe(), Does.Contain("status 'sideways' is not recognised"));
            Assert.That(issues.Describe(), Does.Contain("'weight' is missing"));
        }

        // --- messages name the offender ---------------------------------------------------------------

        [Test]
        public void AMessageNamesTheEntityAndTheRule()
        {
            string json = ConfigJson.New()
                .Layer("l")
                .RawExperiment("{\"id\":\"exp_x\",\"layer\":\"l\",\"status\":\"running\",\"salt\":\"s\"," +
                               "\"variants\":[" + ConfigJson.Variant("control", 1) + "]}")
                .Build();

            Assert.That(ConfigReader.Read(json).Issues.Describe(),
                Does.Contain("experiment 'exp_x': field 'allocation' is missing"));
        }

        [Test]
        public void AWrongTypeSaysWhatWasActuallyFound()
        {
            string json = ConfigJson.New()
                .Layer("l")
                .RawExperiment("{\"id\":\"exp_x\",\"layer\":\"l\",\"status\":7,\"salt\":\"s\"," +
                               "\"allocation\":{\"from\":0,\"to\":10000}," +
                               "\"variants\":[" + ConfigJson.Variant("control", 1) + "]}")
                .Build();

            Assert.That(ConfigReader.Read(json).Issues.Describe(),
                Does.Contain("expected a string, found the integer 7"));
        }

        [Test]
        public void AnEntityWithoutAnIdIsStillLocatableByPosition()
        {
            string json = ConfigJson.New().RawLayer("{\"salt\":\"s\"}").Build();

            Assert.That(ConfigReader.Read(json).Issues.Describe(), Does.Contain("layer #0"));
        }

        // --- optional fields and defaults ---------------------------------------------------------------

        [Test]
        public void AnAbsentStickinessDefaultsToTheSafeOption()
        {
            // Sticky is the policy that protects the analysis, so forgetting the field yields protection
            // rather than a reshuffle.
            string json = ConfigJson.New().Layer("l").Experiment("exp_x", "l", stickiness: null).Build();

            Assert.That(ConfigReader.Read(json).Config.FindExperiment("exp_x").Stickiness,
                Is.EqualTo(StickinessPolicy.StickyAfterExposure));
        }

        [Test]
        public void AnAbsentStatusIsRejectedRatherThanDefaulted()
        {
            // Unlike stickiness, there is no safe default: defaulting to running silently starts an
            // experiment, defaulting to stopped silently stops one.
            string json = ConfigJson.New()
                .Layer("l")
                .RawExperiment("{\"id\":\"exp_x\",\"layer\":\"l\",\"salt\":\"s\"," +
                               "\"allocation\":{\"from\":0,\"to\":100}," +
                               "\"variants\":[" + ConfigJson.Variant("control", 1) + "]}")
                .Build();

            Assert.That(ConfigReader.Read(json).Issues.Describe(),
                Does.Contain("'status' is missing").And.Contain("silently starts or stops"));
        }

        [Test]
        public void AnAbsentAudienceMatchesEveryone()
        {
            string json = ConfigJson.New().Layer("l").Experiment("exp_x", "l").Build();

            Assert.That(ConfigReader.Read(json).Config.FindExperiment("exp_x").Audience.IsEveryone, Is.True);
        }

        [Test]
        public void AnAudienceIsRead()
        {
            string json = ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l",
                    audience: "{\"minAccountLevel\":3,\"platforms\":[\"editor\",\"windows\"]}")
                .Build();

            var audience = ConfigReader.Read(json).Config.FindExperiment("exp_x").Audience;

            Assert.That(audience.MinAccountLevel, Is.EqualTo(3));
            Assert.That(audience.Platforms, Is.EquivalentTo(new[] { "editor", "windows" }));
            Assert.That(audience.Countries, Is.Null, "an absent clause must not become an empty one");
        }

        [Test]
        public void AnEmptyAudienceListWarnsBecauseItExcludesEverybody()
        {
            // Absent means "do not filter"; empty means "filter, and allow nothing". The second is almost
            // always a mistake, and it is invisible at runtime - the experiment simply never assigns.
            string json = ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", audience: "{\"platforms\":[]}")
                .Build();

            var result = ConfigReader.Read(json);

            Assert.That(result.IsValid, Is.True, "an empty list is legal, just suspicious");
            Assert.That(result.Issues.Describe(),
                Does.Contain("excludes every user").And.Contain("omit the field"));
        }

        [Test]
        public void UnknownFieldsAreIgnoredSoTheServerCanAddThemWithoutBreakingOldClients()
        {
            string json = "{\"schemaVersion\":1,\"configVersion\":\"1\"," +
                          "\"_comment\":\"notes for humans\",\"somethingNewer\":{\"a\":1}," +
                          "\"layers\":[{\"id\":\"l\",\"salt\":\"s\",\"futureField\":true}]," +
                          "\"experiments\":[]}";

            Assert.That(ConfigReader.Read(json).IsValid, Is.True);
        }

        [Test]
        public void ABlankIdentifierIsRejected()
        {
            string json = ConfigJson.New().RawLayer("{\"id\":\"  \",\"salt\":\"s\"}").Build();

            Assert.That(ConfigReader.Read(json).Issues.Describe(), Does.Contain("is blank"));
        }

        [Test]
        public void AFractionalWeightIsRejectedAndSaysWhy()
        {
            string json = ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", variants: new[]
                {
                    "{\"id\":\"control\",\"weight\":50.5,\"behavior\":\"b\"}"
                })
                .Build();

            Assert.That(ConfigReader.Read(json).Issues.Describe(),
                Does.Contain("an integer was expected, not a fraction"));
        }
    }
}
