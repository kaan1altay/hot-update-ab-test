---
--- Registers a variant the build has never heard of.
---
--- This is the capability the repository claims: a name that exists in no C# file becomes a working
--- behaviour with no recompile. `APatchAddsAWorkingNewVariantWithNoCSharpChangeAndNoRebuild` asserts it.
---
--- Read this next part before you drop the file in, because otherwise the demo will look broken.
---
--- Loading this on its own changes nothing on screen, and that is correct. Registering a behaviour does
--- not assign anyone to it. The resolver picks variants from the *config*, so until a config declares a
--- variant whose `behavior` is the name below, nobody is ever bucketed into it and the function is never
--- called. A registered behaviour with no matching config entry is inert by design - the alternative
--- would be a patch channel that can enrol users into an experiment nobody configured, which is exactly
--- the authority a patch must not have.
---
--- So this file is half of a two-part change, and the other half is a config edit:
---
---     { "id": "flash_sale", "weight": 3400, "behavior": "shop.pricing_cta.flash_sale" }
---
--- added to `exp_pricing_cta`, with the existing weights lowered to keep the split intentional. The
--- bundled local server does not serve a scenario with a third arm, so seeing this one live means editing
--- `LocalConfigServer.PayloadFor` and rebuilding - which is the honest boundary. The behaviour ships hot;
--- the decision to run an arm does not.
---
--- To watch the registration itself succeed without any of that, drop the file in, press Reload Lua
--- patches, and read the log line: the behaviour count goes up by one.
---

register('shop.pricing_cta.flash_sale', function(ctx)
    return {
        priceStyle = 'discounted',
        badgeText = 'FLASH',
        ctaText = 'Grab it now',
    }
end)

---
--- Audience predicates register through a second function and return a boolean. Anything else, and any
--- error, counts as "does not match" - predicates fail closed, so a broken one shrinks an experiment's
--- audience rather than silently widening it.
---
register_audience('shop.audience.flash_sale_eligible', function(ctx)
    return ctx.account_level >= 5
end)
