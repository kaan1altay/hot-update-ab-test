using System;
using System.Collections.Generic;
using System.Text;

namespace HotUpdateABTest.Core.Config
{
    /// <summary>How badly a validation finding affects the payload.</summary>
    public enum ValidationLevel
    {
        /// <summary>Worth saying, but the payload is still usable.</summary>
        Warning,

        /// <summary>The payload is rejected. Any single error rejects the whole thing.</summary>
        Error
    }

    /// <summary>One thing wrong with a config payload, said precisely enough to act on.</summary>
    /// <remarks>
    /// <para>
    /// Reading JSON with Newtonsoft and a hand-written validator, rather than with a serializer that maps
    /// fields automatically, is a cost. Precise diagnosis is what that cost buys, so these messages are a
    /// deliverable and not a debugging aid. Every one of them names the offending entity and the rule it
    /// broke, in that order, so that a line read on its own - out of a log, off a screenshot, from a
    /// support ticket - is enough to find and fix the config.
    /// </para>
    /// <para>
    /// The shape is <c>{entity path}: {what is wrong}</c>, for example
    /// <c>experiment 'exp_offer_grid': variant weights sum to 0</c>. Where a value is involved it is
    /// quoted; where a distinction is easy to misread - absent versus zero, overlapping versus adjacent -
    /// the message says so rather than assuming the reader knows.
    /// </para>
    /// </remarks>
    public sealed class ValidationIssue
    {
        /// <summary>Severity.</summary>
        public ValidationLevel Level { get; }

        /// <summary>
        /// Stable machine-readable code, for log-once keys and tests that should not break when the
        /// wording is improved.
        /// </summary>
        public string Code { get; }

        /// <summary>The entity at fault, for example <c>experiment 'exp_x' &gt; variant 'grid_v2'</c>.</summary>
        public string Entity { get; }

        /// <summary>What is wrong with it.</summary>
        public string Detail { get; }

        /// <summary>Creates an issue.</summary>
        public ValidationIssue(ValidationLevel level, string code, string entity, string detail)
        {
            Level = level;
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Entity = entity;
            Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        }

        /// <summary>The full message, without the severity prefix.</summary>
        public string Message =>
            string.IsNullOrEmpty(Entity) ? Detail : Entity + ": " + Detail;

        /// <summary>The message as it appears in a log: <c>error: experiment 'x': ...</c>.</summary>
        public override string ToString() =>
            (Level == ValidationLevel.Error ? "error: " : "warning: ") + Message;
    }

    /// <summary>
    /// Everything wrong with one payload, collected rather than thrown at the first problem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validation deliberately does not stop at the first error. An operator who has broken three things
    /// in a config should learn all three from one refresh, not discover them one deploy at a time. The
    /// only exception is the schema version gate, which short-circuits: if the payload is a shape this
    /// build does not understand, every subsequent complaint would be noise about a contract that does not
    /// apply.
    /// </para>
    /// <para>
    /// A payload with any error is rejected in full. There is no partial application, because a half
    /// applied config is the one state in which the framework's invariants could be false.
    /// </para>
    /// </remarks>
    public sealed class ValidationResult
    {
        private static readonly ValidationIssue[] NoIssues = new ValidationIssue[0];

        private readonly ValidationIssue[] _issues;

        /// <summary>A result with nothing wrong.</summary>
        public static ValidationResult Ok { get; } = new ValidationResult(NoIssues);

        /// <summary>Every finding, in the order they were discovered.</summary>
        public IReadOnlyList<ValidationIssue> Issues => _issues;

        /// <summary>True when nothing was rejected. Warnings do not make this false.</summary>
        public bool IsValid
        {
            get
            {
                for (int i = 0; i < _issues.Length; i++)
                {
                    if (_issues[i].Level == ValidationLevel.Error) return false;
                }

                return true;
            }
        }

        /// <summary>How many findings are errors.</summary>
        public int ErrorCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _issues.Length; i++)
                {
                    if (_issues[i].Level == ValidationLevel.Error) count++;
                }

                return count;
            }
        }

        /// <summary>Creates a result over the given findings.</summary>
        public ValidationResult(IEnumerable<ValidationIssue> issues)
        {
            if (issues == null) throw new ArgumentNullException(nameof(issues));
            _issues = new List<ValidationIssue>(issues).ToArray();
        }

        /// <summary>A result carrying one error.</summary>
        public static ValidationResult Error(string code, string entity, string detail) =>
            new ValidationResult(new[] { new ValidationIssue(ValidationLevel.Error, code, entity, detail) });

        /// <summary>
        /// The first error's message, or null when there is none. Used as the human-readable reason on a
        /// degraded config snapshot, where one line has to stand for the whole rejection.
        /// </summary>
        public string FirstError
        {
            get
            {
                for (int i = 0; i < _issues.Length; i++)
                {
                    if (_issues[i].Level == ValidationLevel.Error) return _issues[i].Message;
                }

                return null;
            }
        }

        /// <summary>Every finding on its own line, ready for a log or a panel.</summary>
        public string Describe()
        {
            if (_issues.Length == 0) return "valid";

            var text = new StringBuilder();
            for (int i = 0; i < _issues.Length; i++)
            {
                if (i > 0) text.Append('\n');
                text.Append(_issues[i]);
            }

            return text.ToString();
        }

        /// <inheritdoc />
        public override string ToString() => Describe();
    }

    /// <summary>Accumulates findings while a payload is read and checked.</summary>
    internal sealed class ValidationBuilder
    {
        private readonly List<ValidationIssue> _issues = new List<ValidationIssue>();

        public bool HasErrors { get; private set; }

        public void Error(string code, string entity, string detail)
        {
            _issues.Add(new ValidationIssue(ValidationLevel.Error, code, entity, detail));
            HasErrors = true;
        }

        public void Warning(string code, string entity, string detail)
        {
            _issues.Add(new ValidationIssue(ValidationLevel.Warning, code, entity, detail));
        }

        public ValidationResult Build() => _issues.Count == 0 ? ValidationResult.Ok : new ValidationResult(_issues);
    }
}
