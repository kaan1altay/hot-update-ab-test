using System.Collections.Generic;

namespace HotUpdateABTest.Demo
{
    /// <summary>One thing the code expects to find in the UI.</summary>
    public readonly struct UiExpectation
    {
        /// <summary>The component the child or controller is expected on.</summary>
        public string Owner { get; }

        /// <summary>What kind of thing: <c>child</c>, <c>controller</c> or <c>page</c>.</summary>
        public string Kind { get; }

        /// <summary>The name looked up.</summary>
        public string Name { get; }

        /// <summary>For a page, the controller it belongs to. Null otherwise.</summary>
        public string Controller { get; }

        /// <summary>Creates an expectation.</summary>
        public UiExpectation(string owner, string kind, string name, string controller = null)
        {
            Owner = owner;
            Kind = kind;
            Name = name;
            Controller = controller;
        }

        /// <inheritdoc />
        public override string ToString() =>
            Controller == null
                ? Owner + "." + Name + " (" + Kind + ")"
                : Owner + "." + Controller + ":" + Name + " (page)";
    }

    /// <summary>
    /// Every name the demo binds to, in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One list, three consumers: the boot validation that fails loudly when something is missing, the
    /// EditMode test that checks the published package against it, and the programmatic fallback that has
    /// to declare the same vocabulary. Keeping them on one list is what stops the three drifting apart -
    /// and drift between them is precisely the bug none of them would catch alone.
    /// </para>
    /// <para>
    /// Pages are listed by name rather than index on purpose. <c>barShare</c> declares its pages as
    /// <c>4,unknown,0,healthy,1,warn,2,alarm</c>, so the page whose id is 4 sits at index 0; anything
    /// positional silently picks the wrong colour. It is a package-wide rule, not a quirk of one component
    /// - and the id-to-name mapping has already been re-authored once, which is exactly the change that
    /// would have gone unnoticed had anything indexed positionally.
    /// </para>
    /// </remarks>
    public static class UiContract
    {
        /// <summary>Everything <c>ConsoleMain</c> must provide.</summary>
        public static IReadOnlyList<UiExpectation> Console
        {
            get
            {
                var expectations = new List<UiExpectation>
                {
                    new UiExpectation("ConsoleMain", "child", "chipSource"),
                    new UiExpectation("ConsoleMain", "child", "txtConfigVersion"),
                    new UiExpectation("ConsoleMain", "child", "txtServer"),
                    new UiExpectation("ConsoleMain", "child", "txtScenario"),
                    new UiExpectation("ConsoleMain", "child", "containerDevice"),
                    new UiExpectation("ConsoleMain", "child", "bannerForced"),
                    new UiExpectation("ConsoleMain", "child", "listMetrics"),
                    new UiExpectation("ConsoleMain", "child", "listLog"),

                    new UiExpectation("chipSource", "controller", "state"),
                    new UiExpectation("chipSource", "page", "live", "state"),
                    new UiExpectation("chipSource", "page", "lkg", "state"),
                    new UiExpectation("chipSource", "page", "defaults", "state"),
                    new UiExpectation("chipSource", "page", "none", "state"),

                    new UiExpectation("bannerForced", "controller", "state"),
                    new UiExpectation("bannerForced", "page", "hidden", "state"),
                    new UiExpectation("bannerForced", "page", "shown", "state")
                };

                foreach (var button in DemoUiFactory.Buttons)
                {
                    expectations.Add(new UiExpectation("ConsoleMain", "child", button.Name));
                }

                return expectations;
            }
        }

        /// <summary>Everything one <c>MetricsRow</c> must provide.</summary>
        public static IReadOnlyList<UiExpectation> MetricsRow { get; } = new[]
        {
            new UiExpectation("MetricsRow", "child", "txtExperiment"),
            new UiExpectation("MetricsRow", "child", "txtVariant"),
            new UiExpectation("MetricsRow", "child", "txtAssignments"),
            new UiExpectation("MetricsRow", "child", "txtExposures"),
            new UiExpectation("MetricsRow", "child", "txtConversions"),
            new UiExpectation("MetricsRow", "child", "txtRate"),
            new UiExpectation("MetricsRow", "child", "barShare"),
            new UiExpectation("MetricsRow", "child", "srmLight"),

            new UiExpectation("barShare", "controller", "state"),
            new UiExpectation("barShare", "page", "unknown", "state"),
            new UiExpectation("barShare", "page", "healthy", "state"),
            new UiExpectation("barShare", "page", "warn", "state"),
            new UiExpectation("barShare", "page", "alarm", "state"),

            new UiExpectation("srmLight", "controller", "state"),
            new UiExpectation("srmLight", "page", "unknown", "state"),
            new UiExpectation("srmLight", "page", "healthy", "state"),
            new UiExpectation("srmLight", "page", "alarm", "state")
        };

        /// <summary>Everything one <c>LogRow</c> must provide.</summary>
        public static IReadOnlyList<UiExpectation> LogRow { get; } = new[]
        {
            new UiExpectation("LogRow", "child", "title"),
            new UiExpectation("LogRow", "controller", "type"),
            new UiExpectation("LogRow", "page", "log", "type"),
            new UiExpectation("LogRow", "page", "warn", "type"),
            new UiExpectation("LogRow", "page", "err", "type")
        };

        /// <summary>Everything <c>ShopScreen</c> must provide.</summary>
        public static IReadOnlyList<UiExpectation> ShopScreen { get; } = new[]
        {
            new UiExpectation("ShopScreen", "child", "txtShopTitle"),
            new UiExpectation("ShopScreen", "child", "listOffers"),
            new UiExpectation("ShopScreen", "child", "btnCta"),
            new UiExpectation("ShopScreen", "child", "txtSpec")
        };

        /// <summary>Everything one <c>OfferCard</c> must provide.</summary>
        /// <remarks>
        /// Three orthogonal controllers rather than eight drawn arrangements. The first two use the
        /// presentation spec's own strings as page names, so <c>OfferLayout</c> and <c>PriceStyle</c> map
        /// straight onto pages with no translation table in between - one fewer place for the two to drift.
        /// </remarks>
        public static IReadOnlyList<UiExpectation> OfferCard { get; } = new[]
        {
            new UiExpectation("OfferCard", "child", "imgIcon"),
            new UiExpectation("OfferCard", "child", "txtName"),
            new UiExpectation("OfferCard", "child", "txtPrice"),
            new UiExpectation("OfferCard", "child", "txtOriginal"),
            new UiExpectation("OfferCard", "child", "graphStrike"),
            new UiExpectation("OfferCard", "child", "imgBadgeBg"),
            new UiExpectation("OfferCard", "child", "txtBadge"),

            new UiExpectation("OfferCard", "controller", "layout"),
            new UiExpectation("OfferCard", "page", "list", "layout"),
            new UiExpectation("OfferCard", "page", "grid", "layout"),

            new UiExpectation("OfferCard", "controller", "price"),
            new UiExpectation("OfferCard", "page", "plain", "price"),
            new UiExpectation("OfferCard", "page", "discounted", "price"),

            new UiExpectation("OfferCard", "controller", "badge"),
            new UiExpectation("OfferCard", "page", "none", "badge"),
            new UiExpectation("OfferCard", "page", "shown", "badge")
        };
    }
}
