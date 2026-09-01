---
--- Changes what an existing variant presents.
---
--- `shop.pricing_cta.urgency` already exists in the build and the config already assigns traffic to it,
--- which is why this patch changes the screen the moment it is loaded. Baseline files load first and
--- patches second, and the later registration wins, so re-registering a name replaces it.
---
--- Expected result: the spec strip reads `list · discounted · FLASH · Grab it now`, the badge on each
--- discounted card reads FLASH, and the button reads "Grab it now".
---
--- Delete this file, press Reload Lua patches, and the strip returns to `LIMITED` / `Claim offer`.
---

register('shop.pricing_cta.urgency', function(ctx)
    -- Same guard the shipped baseline uses. `discounted` presents an original price that the C# offer
    -- catalogue already carries; asking for it on an offer that has none produces a spec the screen has
    -- to walk back, so the decision belongs here where the reason is readable.
    if not ctx.has_original_price then
        return {
            priceStyle = 'plain',
            ctaText = 'Grab it now',
        }
    end

    return {
        priceStyle = 'discounted',
        badgeText = 'FLASH',
        ctaText = 'Grab it now',
    }
end)
