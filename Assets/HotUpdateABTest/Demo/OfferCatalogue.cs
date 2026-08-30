using System.Collections.Generic;

namespace HotUpdateABTest.Demo
{
    /// <summary>One thing for sale.</summary>
    /// <remarks>
    /// Prices live here, in C#, and nothing in Lua can change them. A patch channel is a remote code
    /// execution channel; letting it set what things cost would be an unforced error. A variant chooses
    /// how a price is <i>presented</i> - see <c>PresentationSpec</c> - and that is all.
    /// </remarks>
    public sealed class Offer
    {
        /// <summary>Identifier, used in conversion goals.</summary>
        public string Id { get; }

        /// <summary>Display name.</summary>
        public string Title { get; }

        /// <summary>What it costs now, in minor units of the display currency.</summary>
        public int Price { get; }

        /// <summary>
        /// What it cost before, or 0 when there is no original price to strike through.
        /// </summary>
        /// <remarks>
        /// A variant asking for the discounted presentation on an offer with no original price would be
        /// asking the screen to strike through nothing. The behavior is told whether one exists through
        /// <c>ctx.has_original_price</c> so it can decide, and the renderer falls back to plain if it asks
        /// anyway.
        /// </remarks>
        public int OriginalPrice { get; }

        /// <summary>True when there is an original price to present struck through.</summary>
        public bool HasOriginalPrice => OriginalPrice > Price;

        /// <summary>Creates an offer.</summary>
        public Offer(string id, string title, int price, int originalPrice = 0)
        {
            Id = id;
            Title = title;
            Price = price;
            OriginalPrice = originalPrice;
        }

        /// <summary>The current price, formatted for display.</summary>
        public string PriceText => Format(Price);

        /// <summary>The original price, formatted for display.</summary>
        public string OriginalPriceText => Format(OriginalPrice);

        private static string Format(int minorUnits) =>
            "$" + (minorUnits / 100) + "." + (minorUnits % 100).ToString("00");
    }

    /// <summary>The offers the shop screen shows.</summary>
    /// <remarks>
    /// Fixed and small. The experiment is about presentation, so varying the catalogue as well would make
    /// it impossible to say which change moved the numbers - which is the mistake this whole framework
    /// exists to help somebody avoid.
    /// </remarks>
    public static class OfferCatalogue
    {
        /// <summary>Every offer, in display order.</summary>
        public static IReadOnlyList<Offer> All { get; } = new[]
        {
            new Offer("starter_pack", "Starter Pack", 199, 399),
            new Offer("gem_bundle", "Gem Bundle", 499, 999),
            new Offer("season_pass", "Season Pass", 999),
            new Offer("cosmetic_crate", "Cosmetic Crate", 299, 499)
        };

        /// <summary>True when at least one offer has an original price to strike through.</summary>
        public static bool AnyHasOriginalPrice
        {
            get
            {
                for (int i = 0; i < All.Count; i++)
                {
                    if (All[i].HasOriginalPrice) return true;
                }

                return false;
            }
        }
    }
}
