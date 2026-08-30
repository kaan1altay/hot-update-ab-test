---
--- Baseline behaviors for the offer_layout layer.
---
--- A behavior receives the context table and returns a partial presentation spec. It may only set the
--- fields its layer owns - here, just `layout`. Setting anything else has the whole spec rejected, which
--- is what stops two concurrently running experiments overwriting each other.
---
--- Behaviors are pure. There is no clock and no random source in the sandbox, so the same context always
--- produces the same spec; an experiment whose treatment varies within a user measures noise.
---

register('shop.offer_layout.control', function(ctx)
    return { layout = 'list' }
end)

register('shop.offer_layout.grid_v2', function(ctx)
    return { layout = 'grid' }
end)
