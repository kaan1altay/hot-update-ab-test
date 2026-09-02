# Example Lua patches

Five patches you can drop into a running demo. They are the delivery mechanism this repository is about,
so they are here as files you can copy rather than as snippets you have to retype.

| File | Group | What it demonstrates |
| --- | --- | --- |
| `10-flash-sale.lua` | pricing | Changing what an arm presents. **Start here** — visible immediately. |
| `20-rejected-spec.lua` | pricing | A patch that breaks the badge cap and is refused, with the reason on screen. |
| `30-new-variant.lua` | pricing | Registering a behaviour the build has never heard of, and why that alone is inert. |
| `40-layout-swap.lua` | layout | The other layer. Swaps the two arms, so it flips whichever one you are in. |
| `50-bad-layout-value.lua` | layout | A value nothing was drawn for. The only way to reach the unknown-enum check. |

Every one of them registers **both arms of its experiment**. A patch that targets one arm does nothing
visible if your user id hashes into the other, and that is indistinguishable from a patch channel that is
broken — which is exactly how one play-test pass lost a session.

## Where they go

The patch root is created at startup and its full path is printed in the demo's log panel on the first
line. On Windows it is:

```
%USERPROFILE%\AppData\LocalLow\DefaultCompany\hot-update-ab-test\abtest-patches\
```

Copy a file in, press **Reload Lua patches** in the LiveOps panel, and read the log line. It names every
patch file it loaded, in load order:

```
Lua reloaded: 3 file(s) loaded (1 patch), 0 failed, 4 behaviors registered;
patches in load order, last wins: 10-flash-sale.lua
```

**Read that line before concluding a patch does not work.** Files load in sorted order and later
registrations win, so a file left over from an earlier experiment can quietly outrank the one you just
added — and if the leftover is `20-rejected-spec.lua`, every reload renders a rejection no matter what
else is in the folder. If the line says *no patch files in the folder*, the file is not where the demo is
reading; the startup log names that folder.

Delete the file and reload again to revert.
Reverting works because reload rebuilds the registry from the baseline up rather than applying a delta, so
removing a patch removes its effect — the half of hot update that is easy to skip demonstrating.

## Why these files are not under `Assets/`

Deliberately. `Assets/StreamingAssets/abtest/lua/variants/` is the **shipped baseline**, and everything in
it loads automatically at startup. An example dropped there would stop being an example and quietly become
the demo's default behaviour. Keeping them outside `Assets/` also keeps Unity from importing them and
generating `.meta` files for content that is meant to be copied, not built.

## Load order

Baseline first, patches second; within each root, files load in sorted order, so a patch set behaves the
same on every machine instead of depending on how the filesystem enumerates. **Later registrations win** —
that single rule is why adding a variant and changing an existing one need no separate mechanisms. The
numeric filename prefixes are a convention that makes the order visible; nothing enforces them.

`bootstrap.lua` sits in the baseline root and is not patchable. It defines the sandbox, and a patch that
could replace it could rewrite the rules it is supposed to obey.

## What a patch may do

Two registration functions, and that is the whole surface:

```lua
register('behaviour.name', function(ctx) return { ... } end)          -- returns a presentation spec
register_audience('predicate.name', function(ctx) return true end)    -- returns a boolean
```

A behaviour returns a table with up to four fields — `layout`, `priceStyle`, `badgeText`, `ctaText` — and
omitted fields keep their baseline value. Each layer may write only its own fields. The full vocabulary,
with allowed values and the reasoning, is `docs/PRESENTATION_SPEC.md`.

`ctx` carries `user_id`, `account_level`, `platform`, `layer_id`, `experiment_id`, `variant_id` and
`has_original_price`. Nothing else, and nothing writable.

## What a patch may not do

No filesystem, no process control, no `require`, no runtime compilation, no `debug` library, and no `CS`
bridge — that last omission is the load-bearing one, since it would otherwise hand a patch the whole C#
type system including the analytics sink. No prices: money stays in the C# offer catalogue. No telemetry:
a behaviour has no way to log, duplicate or suppress an exposure, because telemetry integrity is the
product here. No `math.random` and no clock, so the same user sees the same treatment every frame.

Chunks load in text mode only — `load(source, name, "t", sandbox)` — so precompiled bytecode is refused.
The Lua bytecode verifier is not hardened and crafted bytecode can subvert the VM outright.

The honest gap, also stated in the root `README.md`: this is a capability restriction, not a resource
limit. Nothing stops a patch spinning in `while true do end` and hanging the frame. Bounding execution
needs a debug hook with an instruction-count budget, and that belongs in place before a channel like this
ships to devices.

## When a patch is wrong

One bad file does not take the registry down: the others still load. A syntax error is trapped and logged,
and the behaviours that did register keep working. A spec that fails validation renders control and puts a
marker on the debug strip naming the reason — `text too long`, `bad enum value`, `unknown field`,
`foreign field`, `wrong type`, `empty value`, `no table`. The whole table is rejected rather than the
offending field, because a half-applied screen is harder to diagnose than a control screen.

`20-rejected-spec.lua` is there to be loaded, not only read.
