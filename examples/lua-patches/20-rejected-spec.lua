---
--- A patch that is refused, on purpose.
---
--- `badgeText` is capped at 10 characters and `layout` accepts only 'list' or 'grid'. Both rules are
--- broken below. Load this to watch a bad patch fail the way it should.
---
--- Expected result: the screen renders control, and the spec strip carries a marker naming the reason -
--- `[FALLBACK: text too long]` here. Swap which return is commented out to see `[FALLBACK: bad enum
--- value]` or `[FALLBACK: foreign field]` instead. The log carries the full sentence.
---
--- Three things are worth watching:
---
---   * The whole table is rejected, not the offending field. `ctaText` below is perfectly valid and is
---     still discarded. A half-applied screen is worse than a control screen, because nobody can tell
---     what they are looking at.
---   * The other layer keeps working. `offer_layout` still applies; only pricing falls back.
---   * The marker is the point. A rejected spec renders control, and control looks exactly like a
---     working control variant unless something on screen says otherwise.
---

register('shop.pricing_cta.urgency', function(ctx)
    return {
        badgeText = 'ELEVENCHARS',   -- 11 characters; the cap is 10  -> text too long
        ctaText = 'Buy',             -- valid, and discarded anyway
    }
end)

-- Uncomment either of these instead to see a different reason token.
--
-- register('shop.pricing_cta.urgency', function(ctx)
--     return { layout = 'carousel' }      -- not an authored page      -> bad enum value
-- end)
--
-- register('shop.pricing_cta.urgency', function(ctx)
--     return { layout = 'grid' }          -- owned by the other layer  -> foreign field
-- end)
