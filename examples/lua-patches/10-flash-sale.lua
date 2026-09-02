---
--- Changes what the pricing arms present. This is the one to try first.
---
--- Both arms of exp_pricing_cta are registered, deliberately. A patch that targets only 'urgency' does
--- nothing visible if your user id happens to hash into 'control', and you cannot tell that apart from a
--- patch channel that is broken. Registering both means it applies whichever arm you are in.
---
--- WHERE: copy to %USERPROFILE%\AppData\LocalLow\DefaultCompany\hot-update-ab-test\abtest-patches\
--- THEN:  press Reload Lua patches
---
--- WHAT YOU SHOULD SEE, on the shop screen:
---   * every call-to-action button reads      Grab it now
---   * the debug strip under the screen reads  ... · FLASH · Grab it now
---   * on the urgency arm, each discounted card carries a FLASH badge
---
--- The log line from the reload says how many files loaded and how many behaviours are registered. If it
--- says 0 patches, the file is not in the folder the demo is reading - the startup log names that folder.
---
--- Delete the file, press Reload Lua patches again, and the labels go back to Buy / Claim offer. Reload
--- rebuilds the registry from the shipped baseline up rather than applying a delta, so removing a patch
--- removes its effect.
---

local function flash_sale(ctx)
    -- Same guard the shipped baseline uses. 'discounted' presents an original price that the C# offer
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
end

register('shop.pricing_cta.control', flash_sale)
register('shop.pricing_cta.urgency', flash_sale)
