---
--- A patch that is refused, on purpose.
---
--- `badgeText` is capped at 10 characters and `layout` accepts only 'list' or 'grid'. Both rules are
--- broken below. Load this to watch a bad patch fail the way it should.
---
--- Expected result: the screen renders control, and the spec strip carries a marker naming the reason -
--- `[FALLBACK: text too long]` here. The log carries the full sentence, naming the field, the actual
--- length and the limit. Uncomment the alternative at the bottom for `[FALLBACK: foreign field]`; the
--- unknown-enum case needs a patch that owns the layout group, which is `50-bad-layout-value.lua`.
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

local function too_long(ctx)
    return {
        badgeText = 'ELEVENCHARS',   -- 11 characters; the cap is 10  -> text too long
        ctaText = 'Buy',             -- valid, and discarded anyway
    }
end

-- Both arms, so the rejection is visible whichever one your id hashes into.
register('shop.pricing_cta.control', too_long)
register('shop.pricing_cta.urgency', too_long)

-- Uncomment this instead to see a different reason token. `layout` is a perfectly valid field with a
-- perfectly valid value - it just belongs to the other layer, and a pricing behaviour writing it would
-- mean one experiment silently overwriting another's.
--
-- register('shop.pricing_cta.urgency', function(ctx)
--     return { layout = 'grid' }          -- owned by the layout group -> foreign field
-- end)
