---
--- Baseline behaviors for the pricing_cta layer.
---
--- This layer owns priceStyle, badgeText and ctaText. Note what it cannot do: it selects between two
--- authored price presentations, but it does not set a price or a discount. Money stays in C# with the
--- offer catalogue, because a channel that can run code on every device should not also be able to change
--- what things cost.
---

register('shop.pricing_cta.control', function(ctx)
    return {
        priceStyle = 'plain',
        ctaText = 'Buy',
    }
end)

register('shop.pricing_cta.urgency', function(ctx)
    -- ctx.has_original_price tells the behavior whether a struck-through original exists to show. Asking
    -- for the discounted presentation without one would be a spec the screen has to walk back, so the
    -- decision is made here where the reason is visible.
    local style = ctx.has_original_price and 'discounted' or 'plain'

    return {
        priceStyle = style,
        badgeText = ctx.has_original_price and 'LIMITED' or nil,
        ctaText = 'Claim offer',
    }
end)

---
--- An audience predicate. Returns a boolean; anything else, or an error, is treated as "does not match".
---
register_audience('shop.audience.established_player', function(ctx)
    return ctx.account_level >= 3
end)
