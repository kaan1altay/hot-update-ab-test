# Presentation spec — what a hot update can change

**This is the authoring contract for `ShopScreen`.** It is the complete vocabulary a Lua patch has: four
fields, fixed values, nothing else. Everything listed here must be renderable in the FairyGUI package,
because validation accepts exactly this set and no more.

Pinned by `PresentationSpecReaderTests` (21 tests). If anything below changes, that suite changes with it.

> This document says **what must exist**. `docs/PACKAGE_SPEC.md`, written in Slice 5 once you hand over the
> real component and child names, will say **what does exist** and how the code binds to it. Name things
> however you like — the names below are only a suggestion.

---

## The four fields

| Field | Type | Allowed values | Layer that owns it | Baseline |
| --- | --- | --- | --- | --- |
| `layout` | string | `"list"`, `"grid"` | `offer_layout` | `list` |
| `priceStyle` | string | `"plain"`, `"discounted"` | `pricing_cta` | `plain` |
| `badgeText` | string or nil | any text, **≤ 10 chars**; nil or `""` means no badge | `pricing_cta` | none |
| `ctaText` | string | any text, **1–24 chars**, may not be empty | `pricing_cta` | `"Buy"` |

A behavior returns only the fields it wants to change. Anything it omits keeps its baseline value.

---

## What has to be drawable

### `layout` — two arrangements of the offer list

| Value | Must render as |
| --- | --- |
| `list` | One offer per row, full width of the 375-wide screen. |
| `grid` | Two offers per row. |

Both arrangements hold the **same offer items** — only the arrangement differs. Suggested: a controller
named `layout` on `ShopScreen` with pages `list` and `grid`.

### `priceStyle` — two price presentations, on each offer item

| Value | Must render as |
| --- | --- |
| `plain` | The current price only. |
| `discounted` | The **original price struck through** beside the current price. |

So each offer item needs two price text fields — current, and a struck-through original that is only
visible on the discounted presentation. Suggested: a controller `priceStyle` on the offer item with pages
`plain` and `discounted`.

**Important:** `discounted` does **not** apply a discount. It presents the original price the C# offer
catalogue already carries. Lua cannot set or change any price — see below. If an offer has no original
price, the screen renders `plain` and logs; you do not need to author a third state for that.

### `badgeText` — one badge, present or absent

A small badge on the offer item, showing up to **10 characters** (`"SALE"`, `"-40%"`, `"BEST VALUE"` — which
is exactly ten). It must be hideable, because most variants will not set one. Authored as a controller
`badge` with pages `none` and `shown`.

There is **one** badge appearance, not a set of badge types. The text varies; the look does not.

> **The limit was 16 and came down to 10 during authoring**, because sixteen characters cannot sit beside
> the offer name on a 335-wide card at a legible size. That is the reject-rather-than-truncate rule working
> as intended: whatever the constant says is *guaranteed* to arrive on screen, so it has to be a length the
> card can hold. The fix is to lower the constant, never to clip at render time — clipping would produce a
> screen that looks deliberate and reads as nonsense, and the patch author would never find out.

### `ctaText` — the button label

The call-to-action button's title, up to 24 characters. No style variation — only the words change. Any
button that can hold 24 characters at your chosen font size works.

---

## Every combination you have to draw

**2 layouts × 2 price presentations × badge present/absent = 8 arrangements.** A test asserts this stays at
8 or fewer; if the vocabulary ever grows past what can be authored exhaustively, something gets cut instead.

|  | `plain`, no badge | `plain`, badge | `discounted`, no badge | `discounted`, badge |
| --- | --- | --- | --- | --- |
| **`list`** | ✓ | ✓ | ✓ | ✓ |
| **`grid`** | ✓ | ✓ | ✓ | ✓ |

If controllers do the work, this is two controllers and a visibility toggle rather than eight layouts.

---

## How the two layers compose on one screen

A user can be in one experiment per layer at once, and both apply to the same screen:

```
baseline  ──►  offer_layout variant   ──►  pricing_cta variant   ──►  rendered
               may set: layout             may set: priceStyle,
                                                    badgeText,
                                                    ctaText
```

Each layer may only write its own fields. A pricing behavior that tries to set `layout` has its **whole
spec rejected** and falls back to control. That is what stops two concurrent experiments overwriting each
other — resolving by precedence instead would mean one layer silently losing, and its experiment measuring
nothing.

A rejection in one layer does not take the other down: if the pricing behavior is broken, the layout
variant still applies and only pricing falls back.

---

## What Lua cannot do

The limit is real and worth stating plainly, because whoever can push a patch can run code on every device.

- **Cannot invent UI.** Unknown field → rejected. Unknown value → rejected. There is no free-form property
  bag, no passthrough, no `extra` table. `layout = "carousel"` is an error, not a request.
- **Cannot set prices or discounts.** Money stays in C#, with the offer catalogue.
- **Cannot touch a `GObject`.** Behaviors return data; C# does the rendering. A bad patch produces a
  rejected spec, never a corrupted UI tree.
- **Cannot record telemetry.** The context handed to a behavior has no way to log an exposure or a
  conversion. Telemetry integrity is the product here, so a patch must not be able to fabricate,
  duplicate or suppress the events the analysis rests on.
- **Cannot be nondeterministic.** No `math.random`, no clock. The same user must see the same treatment
  every frame, or the experiment measures noise.

**Anything rejected falls back to control and is logged once.** Never a half-applied screen: one bad field
rejects the whole table, including the fields that were fine.

---

## Adding a value later

Adding `layout = "carousel"` is a **C# change and a rebuild**, plus drawing a carousel. That is the honest
boundary: a patch can change *which* presentation a variant chooses and can add a whole new variant to a
running experiment, but it cannot add a new *kind* of surface.

Values are enumerated against what the package actually contains, deliberately. Accepting a value nothing
was drawn for would let a patch produce a valid spec the screen cannot render — validation passing the buck
to the renderer.
