# Demo script

A recording order for the LiveOps console, and — for each beat — **the tell**: what a viewer must be able
to read in a single still frame. A beat whose point only lands if you narrate it is a beat that will not
survive being turned into a GIF.

Open `Assets/Scenes/AbTestDemo.unity` and press play. The server starts on **:8757** (or the next free port
up to 8766; the top bar shows which). The Lua patch folder is printed in the log on the first frame.

The console is 1600 × 900. The phone-shaped frame on the left is the game; everything on the right is
tooling.

---

## 0. Cold open — what you are looking at

Nothing pressed yet.

**The tell:** the status chip reads **LIVE**, the config version is shown next to it, and the shop screen
inside the phone frame is rendering. The strip under the shop reads the applied spec, e.g.
`layout=list price=plain badge=(none) cta='Buy'` — so a viewer knows what "before" was without being told.

---

## 1. Simulate a population

Press **Simulate 5000 users**.

**The tell:** the metrics table fills. Per arm: assigned, exposed, conversions, rate, a share bar reading
something like `49.9% (exp 50.0%)`, and a green **SRM** light on each experiment's first row. Two
experiments are listed, in two different layers, both healthy — that is the layer story, visible without
commentary.

Worth pausing on: **assigned and exposed are equal** in this beat. That is the healthy funnel, and it is
the thing beats 4 and 5 will break in two different ways.

---

## 2. The two layers do different things

Press **Force variant** a few times, watching the shop screen.

**The tell:** the spec strip changes and the shop changes with it — `price=discounted` puts a struck-through
original beside the price, a badge appears, the CTA copy changes. Meanwhile the **red FORCED banner** sits
under the phone saying the numbers are excluded.

Then **Clear override**: the banner disappears and the strip returns to the bucketed spec. Both directions
in one beat, which is the point.

---

## 3. Kill switch

Press **Scenario: kill switch**, then **Refresh config** if you do not want to wait for the five-second
poll.

**The tell — three things move at once:**
- the config version in the top bar increments,
- the scenario text reads `kill switch (all stopped)`,
- the shop screen returns to the baseline spec — `layout=list price=plain badge=(none) cta='Buy'`.

The strip is what makes this readable. Without it a viewer sees the shop change and has to guess whether an
experiment stopped or a variant merely lost a coin flip.

Press **Scenario: normal** to bring it back.

---

## 4. Breakage one — suppressed exposure logging

Press **Break: skip exposure**, then **Simulate 5000 users**.

**The tell — the trap and the catch in one frame:**
- the **assigned** column is still an even split, roughly 2500 / 2500 — bucketing is working perfectly,
- the **exposed** column is not: one arm has thousands, the other has zero,
- the **SRM light goes red**,
- that arm's share bar reads `0.0% (exp 50.0%)`.

This is the beat the whole telemetry design exists for. An assignment-based ratio check would have stayed
green through it, because the assignment split never moved — which is exactly why the check is run over the
exposed population instead.

Press **Break: skip exposure** again to fix it, then **Clear saved state**, then **Simulate 5000 users** —
the light goes back to green.

---

## 5. Breakage two — skewed bucketing

Press **Break: bucketing skew**, then **Simulate 5000 users**.

**The tell — same symptom, different cause, and the columns say which:**
- the **exposed** column is skewed, and the **SRM light is red** — same as beat 4 so far,
- but the **assigned** column is skewed *by the same ratio*, and each arm's exposed still equals its
  assigned.

Put beside beat 4, this is the payoff: a red light on its own tells you something is wrong, and the
assigned-versus-exposed columns tell you *which* of the two things it is. A collapsed funnel in one arm is
a rendering fault; a skew that runs through both columns equally is a bucketing fault.

Fix, clear state, simulate, green again.

---

## 6. Malformed config — the guardrail that looks like nothing happening

Press **Scenario: malformed JSON**.

**The tell:** this beat is deliberately *undramatic*, and the tells are what prove it.
- the config version in the top bar **does not change**,
- the status chip **stays LIVE** on the old version,
- the shop screen **does not flicker** — the spec strip is unchanged,
- exactly **one warning** appears in the log, naming the parse failure.

Press it again a few times: still one warning. That is the log-once behaviour, and a viewer can see the
count not climbing.

Then **Scenario: bad schema** — same shape of result, a different single line. Then **Scenario: normal**:
the version increments and the chip stays green, proving the rejection left no latch behind.

---

## 7. Offline — a different rung of the same ladder

Press **Scenario: offline**.

**The tell:** the status chip changes colour and reads **LAST KNOWN GOOD**, and the version shown is the one
that was in force before. That is the distinction the chip exists for: "the server said so" and "we cannot
reach the server" are different states and look different on screen.

Press **Stop server** for the same effect by a different route. Press **Start server** and **Scenario:
normal** to recover — the chip returns to **LIVE**.

---

## 8. A Lua patch adds a variant to a running experiment

The patch folder path is in the log from startup — under `AppData/LocalLow/.../abtest-patches/`. Drop this
in as `flash_sale.lua`:

```lua
register('shop.pricing_cta.urgency', function(ctx)
    return {
        priceStyle = 'discounted',
        badgeText = 'FLASH',
        ctaText = 'Grab it now',
    }
end)
```

Press **Reload Lua patches**.

**The tell:** the spec strip changes to `badge=FLASH cta='Grab it now'` and the shop screen follows — new
badge text, new button copy — **with no recompile and without leaving play mode**. The log line says how
many files loaded and how many behaviors are registered.

Delete the file and press **Reload Lua patches** again: the strip returns to `LIMITED` / `Claim offer`. The
patch channel has a way back, which is the half of hot update people forget to demonstrate.

### 8b. A bad patch

Change `badgeText` to something eleven characters long, or `layout = 'carousel'`, and reload.

**The tell:** the spec strip reads the baseline **followed by a marker** — `[FALLBACK: text too long]` or
`[FALLBACK: bad enum value]`. That marker is the entire reason the strip exists: a rejected spec renders
control, and control looks exactly like a working control variant unless something on screen says
otherwise. The log carries the full sentence.

---

## 9. Reset

Press **Clear saved state**.

**The tell:** the metrics table empties, both breakage toggles return to their `Break:` labels, the forced
banner is gone, and the scenario reads `normal`. One control undoes everything the previous eight beats
did — the full pair table is in `docs/STATUS.md`.

---

## Recording notes

- **1600 × 900 native.** Record at that size; the tooling text is sized for it and does not survive being
  scaled down much past a half.
- **The spec strip is the narration.** If a beat is unreadable, it is usually because the strip is out of
  frame — keep the phone frame and the strip in shot for every shop-screen beat.
- **Beats 4 and 5 belong together.** Neither is interesting alone; the pair is the argument.
- **Beat 6 needs the log panel in frame**, since "one warning, not a flood" is the whole tell.
- Numbers are deterministic: the simulator uses fixed ids and a fixed 20% conversion rate, so a re-record
  produces the same table. Any change you see between takes is a real change.
