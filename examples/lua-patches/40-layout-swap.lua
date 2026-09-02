---
--- The other layer. Owns the layout group, and swaps the two arms over.
---
--- exp_offer_layout and exp_pricing_cta run concurrently in different layers, and each may write only its
--- own fields. This patch touches nothing but 'layout', so it composes with 10-flash-sale.lua rather than
--- fighting it: drop both in and the shop changes arrangement and copy at once, from two independent
--- experiments.
---
--- WHERE: the same abtest-patches folder.
---
--- WHAT YOU SHOULD SEE: the offer list changes arrangement - two cards per row becomes one per row, or
--- the reverse, depending which arm you are in. The debug strip's first value flips between list and
--- grid. Whichever you were seeing, you now see the other, which is what makes it obvious it applied.
---
--- Swapping rather than pinning is deliberate: pinning both arms to 'grid' looks identical to a patch
--- that did nothing at all if you were already on grid.
---

register('shop.offer_layout.control', function(ctx)
    return { layout = 'grid' }
end)

register('shop.offer_layout.grid_v2', function(ctx)
    return { layout = 'list' }
end)
