using HotUpdateABTest.Core.Assignment;
using HotUpdateABTest.Core.Config;
using HotUpdateABTest.Core.Model;
using NUnit.Framework;

namespace HotUpdateABTest.Tests.Core
{
    /// <summary>
    /// Covers the hot-updatable half of targeting: a named Lua predicate, ANDed with the declarative
    /// clauses, failing closed at every step.
    /// </summary>
    /// <remarks>
    /// The Lua side of this is tested for real in the EditMode suite. These tests use a stub evaluator so
    /// the resolver's own rules - when a predicate is consulted, what happens when it says no, what
    /// happens when there is nothing to consult - are covered by the fast CI suite.
    /// </remarks>
    [TestFixture]
    public sealed class AudiencePredicateTests
    {
        private sealed class StubEvaluator : IAudiencePredicateEvaluator
        {
            private readonly bool _result;

            public int Calls { get; private set; }

            public StubEvaluator(bool result)
            {
                _result = result;
            }

            public bool Matches(string predicateKey, UserContext user)
            {
                Calls++;
                return _result;
            }
        }

        private static ExperimentConfig WithPredicate(string audienceJson)
        {
            var read = ConfigReader.Read(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", audience: audienceJson)
                .Build());

            Assert.That(read.IsValid, Is.True, read.Issues.Describe());
            return read.Config;
        }

        [Test]
        public void APredicateIsReadFromTheConfig()
        {
            var config = WithPredicate("{\"predicate\":\"shop.audience.whales\"}");

            Assert.That(config.FindExperiment("exp_x").Audience.PredicateKey,
                Is.EqualTo("shop.audience.whales"));
            Assert.That(config.FindExperiment("exp_x").Audience.IsEveryone, Is.False);
        }

        [Test]
        public void APredicateThatMatchesAdmitsTheUser()
        {
            var evaluator = new StubEvaluator(true);
            var resolver = new ExperimentResolver(null, null, evaluator);

            var assignment = resolver.Resolve(
                WithPredicate("{\"predicate\":\"p\"}"), new UserContext("user-1"), "l");

            Assert.That(assignment.IsAssigned, Is.True);
            Assert.That(evaluator.Calls, Is.EqualTo(1));
        }

        [Test]
        public void APredicateThatDoesNotMatchExcludesTheUser()
        {
            var resolver = new ExperimentResolver(null, null, new StubEvaluator(false));

            var assignment = resolver.Resolve(
                WithPredicate("{\"predicate\":\"p\"}"), new UserContext("user-1"), "l");

            Assert.That(assignment.IsAssigned, Is.False);
            Assert.That(assignment.Reason, Is.EqualTo(NoAssignmentReason.AudienceExcluded));
            Assert.That(assignment.Explanation, Does.Contain("did not match"));
        }

        [Test]
        public void ANamedPredicateWithNoEvaluatorExcludesRatherThanAdmits()
        {
            // Fail closed. A config asking for a narrowing this build cannot perform is not the same as a
            // config asking for no narrowing, and admitting the user would apply a treatment to a
            // population nobody scoped.
            var resolver = new ExperimentResolver();

            var assignment = resolver.Resolve(
                WithPredicate("{\"predicate\":\"p\"}"), new UserContext("user-1"), "l");

            Assert.That(assignment.IsAssigned, Is.False);
            Assert.That(assignment.Explanation, Does.Contain("excludes rather than admits"));
        }

        [Test]
        public void NoPredicateMeansTheEvaluatorIsNeverConsulted()
        {
            var evaluator = new StubEvaluator(false);
            var resolver = new ExperimentResolver(null, null, evaluator);

            var assignment = resolver.Resolve(
                WithPredicate("{\"minAccountLevel\":1}"), new UserContext("user-1", accountLevel: 5), "l");

            Assert.That(assignment.IsAssigned, Is.True);
            Assert.That(evaluator.Calls, Is.Zero);
        }

        [Test]
        public void TheDeclarativeClausesAreCheckedBeforeThePredicate()
        {
            // Cheaper, and it means an obviously-excluded user never pays for a Lua call.
            var evaluator = new StubEvaluator(true);
            var resolver = new ExperimentResolver(null, null, evaluator);

            var assignment = resolver.Resolve(
                WithPredicate("{\"minAccountLevel\":50,\"predicate\":\"p\"}"),
                new UserContext("user-1", accountLevel: 1), "l");

            Assert.That(assignment.IsAssigned, Is.False);
            Assert.That(assignment.Explanation, Does.Contain("account level 1 is below"));
            Assert.That(evaluator.Calls, Is.Zero);
        }

        [Test]
        public void APredicateCanOnlyNarrowNeverWiden()
        {
            // A patch must not be able to widen an experiment past the bounds the config declared, so the
            // predicate is ANDed with the clauses rather than replacing them.
            var resolver = new ExperimentResolver(null, null, new StubEvaluator(true));

            var assignment = resolver.Resolve(
                WithPredicate("{\"minAccountLevel\":50,\"predicate\":\"p\"}"),
                new UserContext("user-1", accountLevel: 3), "l");

            Assert.That(assignment.IsAssigned, Is.False,
                "a matching predicate must not override a declarative clause the user fails");
        }

        [Test]
        public void APredicateThatIsNotAStringIsRejectedByTheReader()
        {
            var read = ConfigReader.Read(ConfigJson.New()
                .Layer("l")
                .Experiment("exp_x", "l", audience: "{\"predicate\":7}")
                .Build());

            Assert.That(read.IsValid, Is.False);
            Assert.That(read.Issues.Describe(), Does.Contain("naming a Lua predicate"));
        }
    }
}
