# FairyGUI package: `AbTestDemo`

**This documents the package as authored.** It was drawn first and the binding code was written against it,
so this is a description rather than a specification. Where the code makes a demand of the package, it says
so and names the test that fails if the demand stops being met.

`docs/PRESENTATION_SPEC.md` is the other half: what a Lua patch may ask the shop screen to present.

| | |
| --- | --- |
| Package name | `AbTestDemo` |
| Package id | `pupdaecj` |
| Design resolution | 1600 × 900 |
| Source project | `FGUIProject/assets/AbTestDemo/` |
| Published output | `Assets/FairyGUI-Packages/AbTestDemo_fui.bytes` (binary, single file) |
| Code generation | **off** — binding is by name at runtime, see below |

## How the code binds

By **child name**, resolved at runtime, never by generated class. Three reasons: the package can be
re-authored without regenerating anything, a renamed or deleted child degrades to one specific warning
instead of a compile error, and the demo can run with no package at all (see *Fallback*).

Every lookup goes through `FairyBinder`, which logs one line naming the component and the missing child,
then returns null. Nothing throws. A screen missing half its children renders the half it has.

**Controllers are selected by page name, never by index.** This matters more than it looks: `barShare`
declares its pages as `4,unknown,0,healthy,1,warn,2,alarm`, so the page whose *id* is `4` sits at *index*
`0`. Anything indexing those positionally would silently pick the wrong colour — and that mapping has
already been re-authored once during this slice, which is exactly the change positional indexing would
have swallowed.

---

## Components

### `ConsoleMain` — 1600 × 900, exported

The root. Everything else lives inside it.

| Child | Type | Bound to |
| --- | --- | --- |
| `txtTitle` | text | Static title |
| `chipSource` | `StatusChip` | Which rung of the config fallback ladder is in force |
| `txtConfigVersion` | text | `ConfigSnapshot.ConfigVersion` |
| `txtServer` | text | Local server state and port |
| `txtScenario` | text | The scenario the server is currently serving |
| `containerDevice` | `containerDevice` | Holds the shop screen |
| `bannerForced` | `ForcedBanner` | Shown while a QA override is active |
| `listMetrics` | list | One `MetricsHeader` then one `MetricsRow` per arm |
| `listLog` | list | `LogRow` per line |
| `groupTopBar`, `groupButtons` | groups | Layout only; not bound |

**Buttons** (all in `groupButtons`):

| Child | Type | Action |
| --- | --- | --- |
| `btnServerToggle` | `ToggleButton` | Start / stop the local HTTP server |
| `btnRefresh` | `ActionButton` | Fetch config now |
| `btnScenarioNormal` | `ActionButton` | Serve the healthy config |
| `btnScenarioWeights` | `ActionButton` | Serve 90/10 weights |
| `btnScenarioPause` | `ActionButton` | Pause the offer-layout experiment |
| `btnScenarioKill` | `ActionButton` | Stop both experiments — the kill switch |
| `btnScenarioMalformed` | `ActionButton` | Serve broken JSON |
| `btnScenarioBadSchema` | `ActionButton` | Serve an unsupported `schemaVersion` |
| `btnScenarioOffline` | `ActionButton` | Refuse every request |
| `btnSimulate` | `ActionButton` | Run 5000 synthetic users |
| `btnForceVariant` | `ActionButton` | Cycle the QA override |
| `btnClearForce` | `ActionButton` | Clear the override |
| `btnInjectSkew` | `ToggleButton` | Break / fix bucketing skew |
| `btnSkipExposure` | `ToggleButton` | Break / fix exposure logging for one arm |
| `btnReloadPatches` | `ActionButton` | Rebuild the Lua registry from disk |
| `btnDumpState` | `ActionButton` | Print the metrics table and Lua registry to the log |
| `btnClearState` | `ActionButton` | Clear pins, cache, sink and overrides |

### `MetricsRow` — 1090 × 40, exported

One arm of one experiment. `listMetrics`'s default item.

| Child | Type | Bound to |
| --- | --- | --- |
| `txtExperiment` | text | Experiment id, blank on continuation rows |
| `txtVariant` | text | Variant id, suffixed `*` when the config no longer declares it |
| `txtAssignments` | text | Distinct users assigned |
| `txtExposures` | text | Distinct users exposed |
| `txtConversions` | text | Conversions credited |
| `txtRate` | text | Conversions per exposed user |
| `barShare` | `barShare` | Assignment-to-exposure funnel rate |
| `srmLight` | `SrmLight` | The experiment's ratio verdict, on the first row only |

### `MetricsHeader` — 1090 × 40, exported

Column headings, inserted as the first list item. **Every label is static — the code never writes to any of
them**, which is why none appear in `UiContract`: that list is the names the code binds, and putting
unbound names in it would make the boot check assert things nothing depends on.

Labels: `txtExperiment`, `txtVariant`, `txtAssignments`, `txtExposures`, `txtConversions`, `txtRate`,
`txtBarRate`, `txtSRMLight`.

Two of those carry meaning worth recording:

- **`txtBarRate` reads `share / expected`.** The unit lives in the header rather than being repeated in
  every cell, which is what let the bar's own title shrink to `49.9% / 50.0%` — see `barShare` below.
- **`txtSRMLight` labels the ratio light**, which has no title of its own.

### `SrmLight` — 28 × 28

Controller `state`, pages `unknown` / `healthy` / `warn` / `alarm`.

> **`warn` is authored but never selected.** A warning band between healthy and alarm was considered and
> dropped: production sample-ratio checks are binary, an intermediate state invites "it is probably fine",
> and `unknown` already covers the honest third case of not having enough data yet. The page is harmless
> and is left in case that decision is ever revisited. `PackageBindingTests` asserts the three pages the
> code *does* select all exist.

### `StatusChip` — 200 × 34

Controller `state`, pages `live` / `lkg` / `defaults` / `none`, plus a `title` text. Maps one-to-one onto
`ConfigSourceKind`, which is the whole point of it being visible: an operator staring at a screen full of
control needs to tell "the server said so" from "we cannot reach the server".

### `barShare` — 50 × 10, ProgressBar

Controller `state`, pages `unknown` / `healthy` / `warn` / `alarm`, plus a caption text named
`txtShare`.

**Shows observed share against expected**, so it sits beside the ratio light and explains it: the light says
the split is not plausible, the bars say which arm is over- or under-represented and by how much. The page
is chosen from the arm's *relative* deviation — under 5% `healthy`, under 20% `warn`, beyond that `alarm` —
gated on the experiment's own verdict, so it reads `unknown` rather than alarming on four users.

**Title format: `49.9% / 50.0%`.** The word `exp` moved into `MetricsHeader.txtBarRate`, which now reads
`share / expected`. The previous form, `49.9% (exp 50.0%)`, measured 129px in a 130px cell — it fitted by
one pixel, which is not the same as fitting, since any font fallback or rounding difference would have
clipped it. The current form measures 114px at its widest possible value, and `StripWidthTests` asserts it
keeps at least ten pixels of slack. A unit belongs in a column header rather than repeated in every row
anyway.

**The caption is named `txtShare`, not `title`, and that is load-bearing.** `GProgressBar` adopts a child
named literally `title` as its own title object and rewrites it from `titleType` inside
`HandleSizeChanged` — so under the old name any layout pass, not merely a `value` write, could replace
`49.9% / 50.0%` with a bare percentage. Sequencing the writes around it worked, but it left the trap armed
for whoever next laid out a row. Renaming the child disarms it structurally: there is no ordering left to
get wrong. `ConsoleView` still resolves `txtShare` and then `title`, so an older package still binds, and
`TheShareCaptionSurvivesAResizeOrIsKnownNotTo` asserts whichever of the two is true of the package on
disk rather than going red when it changes. No `TweenValue`; `unknown` renders a dash rather than a
number.

The four page names deliberately match `SrmLight`, so a reader scanning a row does not learn two colour
languages. One asymmetry follows: `warn` is reachable on the bar and never on the light. An arm can be
somewhat off; a sample ratio is either plausible or it is not.

### `ForcedBanner` — 420 × 34, Label

Controller `state`, pages `hidden` / `shown`, plus a `title` text. Shown whenever a QA override is active,
because a forced session's numbers are excluded from every metric and a viewer needs to know why the screen
disagrees with the panel.

### `LogRow` — 1500 × 34, Label

Controller `type`, pages `log` / `warn` / `err`, plus a `title` text. Maps onto `AbLogLevel`.

### `ActionButton` — 200 × 50, Button

A `title`. Nothing else bound.

### `ToggleButton` — 200 × 50, Button

Controller `state` (`off` / `on`) drives which of `titleOn` / `titleOff` shows; controller `color`
(`grey/green` / `green/red`) picks the palette. The demo sets `state` and reads it back; `color` is authored
per instance and the code does not touch it.

### `containerDevice` — 375 × 667

A framed rectangle standing in for a phone. `ShopScreen` is added as its child at runtime.

### `ShopScreen` — 375 × 667, exported

The game surface, inside `containerDevice`.

| Child | Type | Bound to |
| --- | --- | --- |
| `txtShopTitle` | text | Static |
| `listOffers` | list, non-virtual, scroll | One `OfferCard` per offer in the catalogue |
| `btnCta` | Button | `PresentationSpec.CtaText` |
| `txtSpec` | text | The applied spec, plus a rejection marker — see below |

**`txtSpec` is a debug strip, and it is load-bearing for the recordings.** Without it a viewer sees the shop
change and has to guess which of the two experiments moved.

It shows the four spec values separated by middle dots — `grid · discounted · BEST VALUE · Claim offer` —
rather than `PresentationSpec.ToString()`, whose field labels spend about half the width saying things the
reader does not need twice. At 335px and 11px the strip holds roughly 57 characters; the verbose form ran
past that and clipped. The verbose form is still what goes to the log and what tests assert on.

When a spec is rejected it also carries a short token — `[FALLBACK: unknown field]`, `[FALLBACK: text too
long]`, `[FALLBACK: bad enum value]` — because a rejected spec renders the baseline, and the baseline is
visually identical to a working control variant. The full validation sentence goes to the log. Note the
rejected strip is always the *shortest* case, since a rejection renders the baseline: no badge, and `Buy`.

The binder sets `autoSize = Shrink` on it regardless of how it was authored. The worst *realistic* copy fits
at full size, but the worst *permitted* copy — ten and twenty-four characters of the font's widest glyph —
measures 429px and would scale to about 8.6px. Shrink costs nothing until then and guarantees the strip
never clips, which matters because a half-read strip still looks authoritative.

### `OfferCard` — 335 × 96 (list) / 163 × 190 (grid), exported

`listOffers`'s default item.

| Child | Type | Bound to |
| --- | --- | --- |
| `imgIcon` | loader | Static art |
| `txtName` | text | `Offer.Title` |
| `txtPrice` | text | `Offer.PriceText` |
| `txtOriginal` | text, **auto-sizing** | `Offer.OriginalPriceText` |
| `graphStrike` | graph | The strike-through line, **sized in code** |
| `imgBadgeBg`, `txtBadge` | graph + text | `PresentationSpec.BadgeText` |
| `graphBg` | graph | **Decoration, not bound** — see below |

Three controllers, deliberately orthogonal: 2 + 2 + 2 pages rather than eight drawn arrangements.

| Controller | Pages | Driven by |
| --- | --- | --- |
| `layout` | `list` / `grid` | `PresentationSpec.Layout` |
| `price` | `plain` / `discounted` | `PresentationSpec.PriceStyle`, **and** whether the offer has an original price |
| `badge` | `none` / `shown` | `PresentationSpec.HasBadge`, so empty string and null both select `none` |

The first two use the spec's own strings as page names, so `OfferLayout` and `PriceStyle` map onto pages
with no translation table — one fewer place for the package and the code to drift.

**`graphBg` is decoration and nothing binds to it.** FairyGUI cannot gear a component root's size, so the
card gets a visible background at both sizes by having a child graphic geared across the layout pages
instead. The code still sets the root's size itself, for the same underlying reason. It is listed here so it
does not read as a stray.

**Three things the code does that a controller cannot.** The card's own size, since gears apply to children
and not the root. The list's arrangement — `SingleColumn` for `list`, `FlowHorizontal` with a 9px gap for
`grid`, chosen so `163 + 9 + 163 = 335` and both arrangements fill the same width. And `graphStrike`'s
geometry: `txtOriginal.text` is written first, then the line takes its width and x and sits at the text's
vertical middle. `txtOriginal` is the one auto-sizing text in the card for exactly that reason — reading its
width before the assignment would measure the previous offer's price.

**Text limits are load-bearing.** `MaxCtaLength` is 24 and `MaxBadgeLength` is 10. Because the reader
rejects rather than truncates, text at exactly those lengths is *guaranteed* to arrive, so the constants are
a statement about what the card can hold at a legible size. Sixteen was tried for the badge and does not fit
beside the offer name on a 335-wide card; `"BEST VALUE"` is exactly ten.

---

## Fallback

The demo runs with **no package at all**. `DemoUiFactory` builds the entire console — top bar, device
frame, metrics table, buttons, log, shop screen and offer cards — from `GComponent`, `GTextField`,
`GButton` and `GGraph` at the same 1600 × 900 layout and the same child names, declaring the same
controllers and the same page names.

The fallback offer card has no gears, so it listens to its own `layout` controller and repositions its
children in response. That keeps `ShopScreenView` identical for both paths: it selects a page and nothing
else.

This is not a courtesy. It is what makes the demo testable headless: the PlayMode suite runs both paths and
asserts the same behaviour, so a broken binding shows up as a test failure rather than as an empty screen
somebody notices later. It is also what let this slice be built while the package was still being drawn.

`UsingFallbackUi` is public and shown in the log on startup, so it is never ambiguous which path is running.

## Boot validation

`UiValidator` walks the whole bound tree once at startup, checks it against `UiContract` — the same list the
package tests use and the fallback is built against — and reports **every** missing name in one message
grouped by component. On a healthy run the log reads `UI binding validated: 75 names, all present.`

Reported at error level, deliberately: a missing name means a dead control that looks like a working one,
and logging it as a warning would let a stale package ship. It also means the PlayMode suite fails when the
package and the code disagree, which is how the one real drift during development was caught — a
`.bytes` published six minutes before the `.xml` that changed it.

**Loading is a candidate list, first hit wins:** `Assets/FairyGUI-Packages/AbTestDemo` (Editor, via
`AssetDatabase`), then `AbTestDemo` and `UI/AbTestDemo` under `Resources`. `UIPackage.AddPackage` throws
rather than returning null when a package is missing, so each candidate is pre-checked with `File.Exists`
on `<candidate>_fui.bytes` before it is attempted.

---

## Republishing

Publish settings live in `FGUIProject/settings/Publish.json`: binary format, single `.bytes` file, output to
`Assets/FairyGUI-Packages/`, code generation off. Keep code generation off — the binder resolves names at
runtime and generated classes would go stale silently.

After republishing, run the EditMode suite. `PackageBindingTests` loads the real published package and
asserts every component, child and controller page this document names is actually there, so a rename
surfaces as a named failure rather than as a blank panel in play mode.
