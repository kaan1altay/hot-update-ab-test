---
--- A value the screen was never drawn for. This is the unknown-enum rejection, and it needs a patch that
--- owns the layout group to reach at all.
---
--- The obvious way to try this - putting layout = 'carousel' in a pricing behaviour - never reaches the
--- enum check. Field ownership is validated first, so the spec is refused for belonging to the wrong
--- layer and the value is never looked at:
---
---     field 'layout' belongs to the layout group, but this behavior owns the pricing group
---
--- That is correct, and it is also a validation-ordering fact nobody recovers by reading the code. Owning
--- the layout group is what gets past it.
---
--- WHAT YOU SHOULD SEE: the shop renders its baseline arrangement, and the debug strip carries
--- [FALLBACK: bad enum value]. The log carries the full sentence naming the field and the value.
---
--- 'carousel' is refused because nothing was authored to draw one. Accepting it would let a patch produce
--- a valid spec the screen cannot render, which is validation passing the buck to the renderer. Adding
--- the value is a C# change and a rebuild, plus drawing a carousel - the honest boundary.
---

register('shop.offer_layout.control', function(ctx)
    return { layout = 'carousel' }
end)

register('shop.offer_layout.grid_v2', function(ctx)
    return { layout = 'carousel' }
end)
