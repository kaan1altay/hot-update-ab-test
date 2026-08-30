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
declares its pages as `4,unknown,0,green,1,yellow,2,red`, so the page whose *id* is `4` sits at *index* `0`.
Anything indexing those positionally would silently pick the wrong colour.

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

Column headings, inserted as the first list item. Static; nothing is bound.

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

Controller `state`, pages `unknown` / `green` / `yellow` / `red`, plus a `title` text showing the
percentage. Used for the funnel rate: green above 90%, yellow above 50%, red below, `unknown` with no
assignments yet.

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

**Currently empty.** Its interior is authored against `docs/PRESENTATION_SPEC.md` and is not drawn yet, so
the demo builds the shop interior in code and adds it into this container. When the authored children
appear, `ShopScreenView` finds them by name and uses them instead — the code already looks first and falls
back second, so no change is needed to switch over.

Names the binder looks for, once they exist:

| Child | Type | Purpose |
| --- | --- | --- |
| controller `layout` | pages `list` / `grid` | `PresentationSpec.Layout` |
| `listOffers` | list | The offers |
| offer item: controller `priceStyle` | pages `plain` / `discounted` | `PresentationSpec.PriceStyle` |
| offer item: `txtPrice`, `txtOriginalPrice` | text | Current and struck-through original |
| offer item: `badge` + `txtBadge` | component + text | `PresentationSpec.BadgeText`, hidden when absent |
| `btnCta` | Button | `PresentationSpec.CtaText` |

---

## Fallback

The demo runs with **no package at all**. `DemoUiFactory` builds the entire console — top bar, device
frame, metrics table, buttons, log — from `GComponent`, `GTextField`, `GButton` and `GGraph` at the same
1600 × 900 layout and the same child names.

This is not a courtesy. It is what makes the demo testable headless: the PlayMode suite runs both paths and
asserts the same behaviour, so a broken binding shows up as a test failure rather than as an empty screen
somebody notices later. It is also what let this slice be built while the package was still being drawn.

`UsingFallbackUi` is public and shown in the log on startup, so it is never ambiguous which path is running.

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
