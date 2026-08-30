using System.Collections.Generic;
using System.Text;
using FairyGUI;

namespace HotUpdateABTest.Demo
{
    /// <summary>What a boot-time validation found.</summary>
    public sealed class UiValidationReport
    {
        private readonly List<UiExpectation> _missing = new List<UiExpectation>();

        /// <summary>How many expectations were checked.</summary>
        public int Checked { get; internal set; }

        /// <summary>Everything that was not found.</summary>
        public IReadOnlyList<UiExpectation> Missing => _missing;

        /// <summary>True when every expected name resolved.</summary>
        public bool IsComplete => _missing.Count == 0;

        internal void Add(UiExpectation expectation) => _missing.Add(expectation);

        /// <summary>One message listing everything that is missing.</summary>
        public string Describe()
        {
            if (IsComplete) return "UI binding validated: " + Checked + " names, all present.";

            var text = new StringBuilder();
            text.Append("UI binding is incomplete: ").Append(_missing.Count).Append(" of ").Append(Checked)
                .Append(" expected names could not be found.\n");

            string owner = null;
            for (int i = 0; i < _missing.Count; i++)
            {
                var expectation = _missing[i];
                if (expectation.Owner != owner)
                {
                    owner = expectation.Owner;
                    text.Append("  ").Append(owner).Append(":\n");
                }

                text.Append("    - ").Append(expectation.Kind).Append(" '").Append(expectation.Name).Append('\'');
                if (expectation.Controller != null) text.Append(" on controller '").Append(expectation.Controller).Append('\'');
                text.Append('\n');
            }

            text.Append("Either a name was mistyped, the FairyGUI package was not republished, or ")
                .Append("docs/PACKAGE_SPEC.md is out of date.");

            return text.ToString();
        }

        /// <inheritdoc />
        public override string ToString() => Describe();
    }

    /// <summary>
    /// Walks the whole UI at boot and reports every name that does not resolve, in one message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runtime binding degrades gracefully - a missing child logs once and leaves that part of the screen
    /// static. That is the right behaviour for a player and exactly the wrong thing to discover on camera.
    /// Every <c>GetChild</c> returning null is a name mistyped or a publish forgotten, and the symptom is a
    /// dead button that looks like a working one.
    /// </para>
    /// <para>
    /// So the whole tree is checked once at startup, and every failure is collected before anything is
    /// reported. Stopping at the first missing name would mean finding one typo per run; a null check at
    /// each use site would mean finding them one interaction at a time. One message listing all of them is
    /// the only version that gets fixed in a single pass.
    /// </para>
    /// </remarks>
    public static class UiValidator
    {
        /// <summary>Validates the console tree, the shop screen, and one instance of each list item.</summary>
        /// <param name="console">The console root.</param>
        /// <param name="shop">The shop screen, or null when it was not built.</param>
        /// <param name="metricsRow">A sample metrics row, or null.</param>
        /// <param name="logRow">A sample log row, or null.</param>
        /// <param name="offerCard">A sample offer card, or null.</param>
        public static UiValidationReport Validate(
            GComponent console,
            GComponent shop = null,
            GComponent metricsRow = null,
            GComponent logRow = null,
            GComponent offerCard = null)
        {
            var report = new UiValidationReport();

            Check(report, console, UiContract.Console);
            Check(report, shop, UiContract.ShopScreen);
            Check(report, metricsRow, UiContract.MetricsRow);
            Check(report, logRow, UiContract.LogRow);
            Check(report, offerCard, UiContract.OfferCard);

            return report;
        }

        /// <summary>Checks one component against one contract.</summary>
        /// <remarks>
        /// Public so the package tests run the same code the boot check does. A test with its own copy of
        /// the matching logic can pass on a laxer rule than the thing it is supposed to be guarding.
        /// </remarks>
        public static UiValidationReport ValidateAgainst(
            GComponent root, IReadOnlyList<UiExpectation> expectations)
        {
            var report = new UiValidationReport();
            Check(report, root, expectations);
            return report;
        }

        private static void Check(
            UiValidationReport report, GComponent root, IReadOnlyList<UiExpectation> expectations)
        {
            // A tree that was never built is not a binding failure - the demo may legitimately be running
            // without a shop screen, for instance - so it is skipped rather than reported as sixteen
            // missing names.
            if (root == null) return;

            for (int i = 0; i < expectations.Count; i++)
            {
                var expectation = expectations[i];
                report.Checked++;

                if (!Resolves(root, expectation)) report.Add(expectation);
            }
        }

        private static bool Resolves(GComponent root, UiExpectation expectation)
        {
            // The owner is either the root itself or a child of it, so that a contract can talk about
            // "srmLight.state:healthy" without the caller having to hand over every sub-component.
            var owner = expectation.Owner == root.name ? root : Deep(root, expectation.Owner) as GComponent;
            owner = owner ?? root;

            switch (expectation.Kind)
            {
                case "child":
                    return Deep(owner, expectation.Name) != null;

                case "controller":
                    return owner.GetController(expectation.Name) != null;

                case "page":
                    var controller = owner.GetController(expectation.Controller);
                    return controller != null && controller.GetPageIdByName(expectation.Name) != null;

                default:
                    return true;
            }
        }

        /// <summary>Finds a child on the component or one level inside its child components.</summary>
        /// <remarks>
        /// The authored package nests buttons in groups and the fallback nests them in components; one
        /// level of descent covers both without the contract having to know which.
        /// </remarks>
        public static GObject Deep(GComponent parent, string name)
        {
            if (parent == null) return null;

            var direct = parent.GetChild(name);
            if (direct != null) return direct;

            for (int i = 0; i < parent.numChildren; i++)
            {
                if (parent.GetChildAt(i) is GComponent child)
                {
                    var found = child.GetChild(name);
                    if (found != null) return found;
                }
            }

            return null;
        }
    }
}
