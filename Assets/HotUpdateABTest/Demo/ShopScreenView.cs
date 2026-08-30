using System;
using System.Collections.Generic;
using FairyGUI;
using HotUpdateABTest.Core.Presentation;

namespace HotUpdateABTest.Demo
{
    /// <summary>
    /// Renders the shop screen from a <see cref="PresentationSpec"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binds the authored <c>ShopScreen</c> and its <c>OfferCard</c> items by name. Where the package is
    /// absent the same structure is built in code with the same names, so this class does not know or care
    /// which it is driving - which is what lets the PlayMode suite run both through one set of assertions.
    /// </para>
    /// <para>
    /// <b>Three orthogonal controllers, not eight arrangements.</b> <c>layout</c> is <c>list</c>/<c>grid</c>,
    /// <c>price</c> is <c>plain</c>/<c>discounted</c>, <c>badge</c> is <c>none</c>/<c>shown</c>. The first
    /// two use the spec's own strings as page names, so the enum maps onto a page with no translation table
    /// - one fewer place for the package and the code to drift apart.
    /// </para>
    /// <para>
    /// <b>The spec arrives already validated</b>, so there is no branch here for an unknown layout: a spec
    /// carrying one could not have got this far. Two things are still decided at render time, both because
    /// they depend on data the behavior cannot see - an offer with no original price cannot show a
    /// struck-through one, and a GList's layout type is not something a controller can gear.
    /// </para>
    /// </remarks>
    public sealed class ShopScreenView
    {
        private const int ScreenWidth = 375;
        private const int ListWidth = 335;
        private const int GridGap = 9;

        /// <summary>
        /// Divider between the spec strip's four values: a middle dot, U+00B7.
        /// </summary>
        /// <remarks>
        /// A dot rather than a slash, because the metrics table one panel over uses a slash between two
        /// numbers and reusing it here would read as a second fraction. Built from its code point rather
        /// than written as a literal so the file stays pure ASCII: this repository has already had one
        /// document corrupted by a tool that reinterpreted UTF-8, and source is not where that should be
        /// discovered.
        /// </remarks>
        private static readonly string Separator = char.ConvertFromUtf32(0x00B7);

        private static readonly int[] ListCardSize = { 335, 96 };
        private static readonly int[] GridCardSize = { 163, 190 };

        private readonly FairyBinder _binder;
        private readonly Action<string> _onCta;
        private readonly Func<GComponent> _cardFactory;
        private readonly List<GComponent> _cards = new List<GComponent>();

        private GList _list;
        private GObject _cta;
        private GObject _spec;

        /// <summary>The screen root, authored or built.</summary>
        public GComponent Screen { get; }

        /// <summary>The spec currently rendered.</summary>
        public PresentationSpec Current { get; private set; } = PresentationSpec.Baseline;

        /// <summary>One offer card, for boot validation to check against the contract.</summary>
        public GComponent SampleCard => _cards.Count > 0 ? _cards[0] : null;

        /// <summary>Creates a view over <paramref name="screen"/>.</summary>
        /// <param name="cardFactory">Makes one <c>OfferCard</c>: from the package, or from the fallback.</param>
        /// <param name="onCta">Called with the offer id when the call to action or a card is pressed.</param>
        public ShopScreenView(
            GComponent screen, FairyBinder binder, Func<GComponent> cardFactory, Action<string> onCta)
        {
            Screen = screen ?? throw new ArgumentNullException(nameof(screen));
            _binder = binder ?? throw new ArgumentNullException(nameof(binder));
            _cardFactory = cardFactory ?? throw new ArgumentNullException(nameof(cardFactory));
            _onCta = onCta;

            if (Screen.GetChild("listOffers") == null) DemoUiFactory.BuildShopScreenInterior(Screen);

            _list = _binder.Child<GList>(Screen, "listOffers", "ShopScreen");
            _cta = _binder.Child<GObject>(Screen, "btnCta", "ShopScreen");
            _spec = _binder.Child<GObject>(Screen, "txtSpec", "ShopScreen");

            // The compact form fits 335px at 11px in every ordinary case, but the absolute worst - grid,
            // discounted, a ten-character badge and a twenty-four-character call to action - lands within a
            // few pixels of the edge. Shrink costs nothing until then and guarantees the strip is never
            // clipped, which matters because a half-read strip is worse than none: it looks authoritative.
            if (_spec is GTextField strip) strip.autoSize = AutoSizeType.Shrink;

            if (_cta != null) _cta.onClick.Add(() => _onCta?.Invoke(OfferCatalogue.All[0].Id));

            BuildCards();
            Apply(PresentationSpec.Baseline, null);
        }

        /// <summary>Applies a spec.</summary>
        /// <param name="spec">The validated spec to render.</param>
        /// <param name="rejectionToken">
        /// A short token naming why the spec fell back to the baseline, or null when it did not.
        /// </param>
        public void Apply(PresentationSpec spec, string rejectionToken)
        {
            Current = spec;

            ApplyListLayout(spec.Layout);

            for (int i = 0; i < _cards.Count; i++) ApplyCard(_cards[i], OfferCatalogue.All[i], spec);

            SetCtaText(spec.CtaText);
            SetSpecStrip(spec, rejectionToken);
        }

        private void BuildCards()
        {
            if (_list == null) return;

            _list.RemoveChildren();
            _cards.Clear();

            foreach (var offer in OfferCatalogue.All)
            {
                var card = _cardFactory();
                if (card == null) return;

                string offerId = offer.Id;
                card.onClick.Add(() => _onCta?.Invoke(offerId));

                _cards.Add(card);
                _list.AddChild(card);
            }
        }

        /// <summary>
        /// Sets the list's arrangement. Code rather than a gear, because a GList's layout type is not
        /// something a controller can drive.
        /// </summary>
        /// <remarks>
        /// The grid gap is chosen so two cards fill the same width as one list card: 163 + 9 + 163 = 335.
        /// Without that the two arrangements would sit on different margins and the layout change would
        /// read as a mistake rather than a variant.
        /// </remarks>
        private void ApplyListLayout(OfferLayout layout)
        {
            if (_list == null) return;

            if (layout == OfferLayout.Grid)
            {
                _list.layout = ListLayoutType.FlowHorizontal;
                _list.columnGap = GridGap;
                _list.lineGap = GridGap;
            }
            else
            {
                _list.layout = ListLayoutType.SingleColumn;
                _list.lineGap = GridGap;
            }
        }

        private void ApplyCard(GComponent card, Offer offer, PresentationSpec spec)
        {
            bool grid = spec.Layout == OfferLayout.Grid;
            int[] size = grid ? GridCardSize : ListCardSize;

            // The card's own size is set here rather than geared: FairyGUI gears children, not the root.
            card.SetSize(size[0], size[1]);
            _binder.SelectPage(card.GetController("layout"), grid ? "grid" : "list", "OfferCard");

            SetChildText(card, "txtName", offer.Title);
            SetChildText(card, "txtPrice", offer.PriceText);

            // A variant may ask for the discounted presentation on an offer with no original price to
            // strike through. Rendering a struck-through blank would be worse than presenting it plainly,
            // and the behavior is handed `has_original_price` precisely so it can avoid asking.
            bool discounted = spec.PriceStyle == PriceStyle.Discounted && offer.HasOriginalPrice;
            _binder.SelectPage(card.GetController("price"), discounted ? "discounted" : "plain", "OfferCard");

            SetChildText(card, "txtOriginal", offer.OriginalPriceText);
            if (discounted) SizeStrikeThrough(card);

            _binder.SelectPage(card.GetController("badge"), spec.HasBadge ? "shown" : "none", "OfferCard");
            if (spec.HasBadge) SetChildText(card, "txtBadge", spec.BadgeText);
        }

        /// <summary>
        /// Stretches the strike-through line across the original price.
        /// </summary>
        /// <remarks>
        /// Done in code because the width depends on the text, which depends on the offer. The text is
        /// written first so its auto-size has resolved before the line is measured against it; reading the
        /// width before the assignment gives the previous offer's price.
        /// </remarks>
        private static void SizeStrikeThrough(GComponent card)
        {
            var original = card.GetChild("txtOriginal");
            var strike = card.GetChild("graphStrike");
            if (original == null || strike == null) return;

            strike.width = original.width;
            strike.x = original.x;
            strike.y = original.y + (original.height / 2f);
        }

        private void SetCtaText(string text)
        {
            if (_cta == null) return;

            // A GButton exposes its label as .text; a plain component holds a child called title. Both
            // shapes appear - the authored button is the first, the fallback the second.
            if (_cta is GButton button) button.title = text;
            else if (_cta is GComponent component && component.GetChild("title") != null)
            {
                component.GetChild("title").text = text;
            }
            else _cta.text = text;
        }

        /// <summary>
        /// Writes the debug strip that says what is currently applied.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It exists so every recorded beat is readable in a still frame. Without it a viewer sees the shop
        /// change and has to guess which of the two experiments moved.
        /// </para>
        /// <para>
        /// It also names a rejection. Showing only the baseline would make a rejected spec look exactly
        /// like a working control variant, which is the one confusion this demo cannot afford - and the
        /// log line, being off in the log panel, does not disambiguate the shop screen in a still.
        /// </para>
        /// </remarks>
        private void SetSpecStrip(PresentationSpec spec, string rejectionToken)
        {
            if (_spec == null) return;

            string text = Compact(spec);
            _spec.text = rejectionToken == null ? text : text + "  [FALLBACK: " + rejectionToken + "]";
        }

        /// <summary>The spec in the shortest form that still says everything.</summary>
        /// <remarks>
        /// <para>
        /// Field names dropped, values kept, in the fixed order layout, price, badge, call to action. The
        /// strip is 335 wide at 11px, which holds roughly 57 characters;
        /// <c>PresentationSpec.ToString()</c> spends about half its length on labels the reader does not
        /// need twice, and the worst case with them ran well past the edge and clipped.
        /// </para>
        /// <para>
        /// The verbose form is still what goes to the log and what tests assert on. Compact on screen where
        /// space is the constraint, verbose in the log where it is not - the same split as the rejection
        /// token and its sentence.
        /// </para>
        /// <para>
        /// Note the rejected case is always the <i>shortest</i> possible strip, because a rejection renders
        /// the baseline: no badge, and "Buy" for the call to action. So the marker never competes for space
        /// with a rich spec.
        /// </para>
        /// </remarks>
        private static string Compact(PresentationSpec spec) =>
            spec.Layout.ToString().ToLowerInvariant() + " " + Separator + " " +
            spec.PriceStyle.ToString().ToLowerInvariant() + " " + Separator + " " +
            (spec.BadgeText ?? "no badge") + " " + Separator + " " +
            spec.CtaText;

        private static void SetChildText(GComponent card, string name, string text)
        {
            var child = card.GetChild(name);
            if (child != null) child.text = text ?? "";
        }
    }
}
