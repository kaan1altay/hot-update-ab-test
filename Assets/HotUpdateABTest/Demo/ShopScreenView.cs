using System;
using System.Collections.Generic;
using FairyGUI;
using HotUpdateABTest.Core.Presentation;
using UnityEngine;

namespace HotUpdateABTest.Demo
{
    /// <summary>
    /// Renders the shop screen from a <see cref="PresentationSpec"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The authored <c>ShopScreen</c> component is currently an empty 375x667 container - its interior is
    /// being drawn against <c>docs/PRESENTATION_SPEC.md</c> and does not exist yet. So this builds the
    /// interior in code, inside whichever container it is given.
    /// </para>
    /// <para>
    /// It looks for the authored children first and only builds its own when they are absent, so switching
    /// over when the interior is drawn needs no code change: <c>listOffers</c>, a <c>layout</c> controller
    /// and a <c>btnCta</c> appearing in the package is all it takes.
    /// </para>
    /// <para>
    /// <b>The spec arrives already validated.</b> Everything here is a straight application of an
    /// enumerated value - there is no branch for "unknown layout", because a spec that reached this point
    /// cannot carry one. The one exception is a variant asking for the discounted presentation on an offer
    /// with no original price to strike through, which the renderer walks back to plain rather than
    /// rendering a struck-through blank.
    /// </para>
    /// </remarks>
    public sealed class ShopScreenView
    {
        private const int ScreenWidth = 375;
        private const int Margin = 12;

        private readonly GComponent _screen;
        private readonly FairyBinder _binder;
        private readonly Action<string> _onCta;

        private readonly List<GComponent> _offerCards = new List<GComponent>();

        private GComponent _authoredOffers;
        private Controller _authoredLayout;
        private GObject _authoredCta;

        private GComponent _builtRoot;
        private GTextField _builtCta;

        /// <summary>The spec currently rendered.</summary>
        public PresentationSpec Current { get; private set; } = PresentationSpec.Baseline;

        /// <summary>True when the screen's interior was built in code rather than found in the package.</summary>
        public bool UsingBuiltInterior => _authoredOffers == null;

        /// <summary>Creates a view over <paramref name="screen"/>.</summary>
        /// <param name="onCta">Called with the offer id when a call to action is pressed.</param>
        public ShopScreenView(GComponent screen, FairyBinder binder, Action<string> onCta)
        {
            _screen = screen ?? throw new ArgumentNullException(nameof(screen));
            _binder = binder ?? throw new ArgumentNullException(nameof(binder));
            _onCta = onCta;

            // Silent probes: the authored interior is expected to be absent today, so a missing child is
            // not worth a warning yet. Once it exists, the binder's reporting takes over.
            _authoredOffers = _screen.GetChild("listOffers") as GComponent;
            _authoredLayout = _screen.GetController("layout");
            _authoredCta = _screen.GetChild("btnCta");

            if (UsingBuiltInterior) BuildInterior();
        }

        /// <summary>Applies a spec, rebuilding only what changed.</summary>
        public void Apply(PresentationSpec spec)
        {
            Current = spec;

            if (_authoredLayout != null) _binder.SelectPage(_authoredLayout, PageFor(spec.Layout), "ShopScreen");
            if (_authoredCta != null) _authoredCta.text = spec.CtaText;

            if (UsingBuiltInterior) ApplyToBuiltInterior(spec);
        }

        private static string PageFor(OfferLayout layout) =>
            layout == OfferLayout.Grid ? "grid" : "list";

        private void BuildInterior()
        {
            _builtRoot = new GComponent { name = "shopInterior" };
            _builtRoot.SetSize(ScreenWidth, 667);
            _screen.AddChild(_builtRoot);

            _builtRoot.AddChild(Graph(ScreenWidth, 667, new Color32(0x12, 0x10, 0x0E, 0xFF), "bg"));
            _builtRoot.AddChild(Label("txtShopTitle", "SHOP", 0, 12, ScreenWidth, 34, 20, Color.white, true));

            foreach (var offer in OfferCatalogue.All) _offerCards.Add(BuildOfferCard(offer));

            _builtCta = Label("btnCta", "Buy", Margin, 600, ScreenWidth - (Margin * 2), 44, 18,
                new Color32(0xFF, 0xCC, 0x00, 0xFF), true);
            _builtRoot.AddChild(Graph(ScreenWidth - (Margin * 2), 44,
                new Color32(0x66, 0x33, 0x00, 0xFF), "ctaBg", Margin, 600));
            _builtRoot.AddChild(_builtCta);

            ApplyToBuiltInterior(PresentationSpec.Baseline);
        }

        private GComponent BuildOfferCard(Offer offer)
        {
            var card = new GComponent { name = "offer_" + offer.Id };
            card.AddChild(Graph(1, 1, new Color32(0x1E, 0x1A, 0x16, 0xFF), "cardBg"));
            card.AddChild(Label("txtTitle", offer.Title, 8, 6, 100, 22, 14, Color.white));
            card.AddChild(Label("txtOriginalPrice", offer.OriginalPriceText, 8, 30, 80, 20, 12,
                new Color32(0x88, 0x88, 0x88, 0xFF)));
            card.AddChild(Label("txtPrice", offer.PriceText, 8, 30, 80, 20, 14,
                new Color32(0x8F, 0xD6, 0x00, 0xFF)));

            var badge = new GComponent { name = "badge" };
            badge.SetSize(80, 20);
            badge.AddChild(Graph(80, 20, new Color32(0xB2, 0x00, 0x00, 0xFF), "badgeBg"));
            badge.AddChild(Label("txtBadge", "", 0, 0, 80, 20, 11, Color.white, true));
            card.AddChild(badge);

            _builtRoot.AddChild(card);

            // A press anywhere on a card is a conversion for that offer. The call to action is the
            // headline, but a demo where only one button converts makes the funnel dull to watch.
            card.onClick.Add(() => _onCta?.Invoke(offer.Id));
            return card;
        }

        private void ApplyToBuiltInterior(PresentationSpec spec)
        {
            bool grid = spec.Layout == OfferLayout.Grid;

            int columns = grid ? 2 : 1;
            int cardWidth = grid
                ? (ScreenWidth - (Margin * 3)) / 2
                : ScreenWidth - (Margin * 2);
            int cardHeight = grid ? 110 : 76;

            for (int i = 0; i < _offerCards.Count; i++)
            {
                var card = _offerCards[i];
                var offer = OfferCatalogue.All[i];

                int column = i % columns;
                int row = i / columns;

                card.SetSize(cardWidth, cardHeight);
                card.SetXY(
                    Margin + (column * (cardWidth + Margin)),
                    60 + (row * (cardHeight + Margin)));

                Resize(card, "cardBg", cardWidth, cardHeight);
                ApplyPricing(card, offer, spec, cardWidth);
            }

            if (_builtCta != null) _builtCta.text = spec.CtaText;
        }

        private static void ApplyPricing(GComponent card, Offer offer, PresentationSpec spec, int cardWidth)
        {
            // A variant may ask for the discounted presentation on an offer that has no original price.
            // Rendering a struck-through blank would be worse than quietly presenting it plainly, and the
            // behavior is given `has_original_price` precisely so it can avoid asking.
            bool discounted = spec.PriceStyle == PriceStyle.Discounted && offer.HasOriginalPrice;

            var original = card.GetChild("txtOriginalPrice");
            var price = card.GetChild("txtPrice");

            if (original != null)
            {
                original.visible = discounted;

                // FairyGUI has no strikethrough on a plain text field, so the original price is marked with
                // surrounding dashes. Crude, and honest: the authored package will do this properly.
                original.text = discounted ? "-" + offer.OriginalPriceText + "-" : offer.OriginalPriceText;
            }

            if (price != null) price.SetXY(discounted ? 92 : 8, 30);

            var badge = card.GetChild("badge") as GComponent;
            if (badge == null) return;

            badge.visible = spec.HasBadge;
            badge.SetXY(cardWidth - 88, 6);

            var badgeText = badge.GetChild("txtBadge");
            if (badgeText != null) badgeText.text = spec.BadgeText ?? "";
        }

        private static void Resize(GComponent card, string childName, int width, int height)
        {
            if (card.GetChild(childName) is GGraph graph)
            {
                graph.SetSize(width, height);
                graph.DrawRect(width, height, 0, Color.clear, new Color32(0x1E, 0x1A, 0x16, 0xFF));
            }
        }

        private static GGraph Graph(int width, int height, Color color, string name, int x = 0, int y = 0)
        {
            var graph = new GGraph { name = name };
            graph.SetSize(width, height);
            graph.SetXY(x, y);
            graph.DrawRect(width, height, 0, Color.clear, color);
            return graph;
        }

        private static GTextField Label(
            string name, string text, int x, int y, int width, int height, int size, Color color,
            bool centred = false)
        {
            var field = new GTextField { name = name };
            field.SetSize(width, height);
            field.SetXY(x, y);
            field.textFormat = new TextFormat
            {
                size = size,
                color = color,
                align = centred ? AlignType.Center : AlignType.Left
            };
            field.verticalAlign = VertAlignType.Middle;
            field.text = text;
            return field;
        }
    }
}
