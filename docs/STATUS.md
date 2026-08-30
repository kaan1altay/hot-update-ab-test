# Status

Engineering log for `hot-update-ab-test`. Updated at the end of every slice, so the numbers here are
checkable rather than claimed.

**Slice 3 of 6 complete.** Exposure at view time, conversion attribution, the sample-ratio guardrail, and a
randomised soak over the whole framework.

---

## Environment

| | |
| --- | --- |
| Unity | 6000.0.59f2 (URP 17.0.4, Input System 1.14.2, Test Framework 1.6.0) |
| xLua | v2.1.16 (MIT), vendored — see `Assets/XLua/VENDORED.md` |
| FairyGUI | 5.2.0 (MIT), runtime only, vendored — see `Assets/FairyGUI/VENDORED.md` |
| Newtonsoft.Json | `com.unity.nuget.newtonsoft-json` 3.2.1 (Newtonsoft.Json 13.0.3) |
| .NET (CI + local second compilation) | net9.0, C# LangVersion pinned to 9.0 |
| NUnit | Unity bundles **3.5.0** (`com.unity.ext.nunit`); the .NET project pins **3.14.0** |
| Platforms Lua runs on | Windows and Linux desktop x64 only — the vendored xLua native plugin covers no others |

## Assemblies

| Assembly | Location | Depends on | Notes |
| --- | --- | --- | --- |
| `HotUpdateABTest.Core` | `Assets/HotUpdateABTest/Runtime/Core/` | Newtonsoft.Json | `noEngineReferences: true`. The decision core. |
| `HotUpdateABTest.Runtime` | `Assets/HotUpdateABTest/Runtime/Unity/` | Core, XLua | The engine-facing half: file cache, file-backed pins, StreamingAssets. |
| `HotUpdateABTest.Tests.EditMode` | `Assets/HotUpdateABTest/Tests/EditMode/` | Core, Runtime, XLua, TestRunner | Editor-only, `UNITY_INCLUDE_TESTS`. |
| `HotUpdateABTest.Core.Tests` | `dotnet/HotUpdateABTest.Core.Tests/` | Core (linked), NUnit | Not a Unity assembly. See below. |

### Why the core is compiled twice

Everything under `Runtime/Core/` is written without touching `UnityEngine`. `dotnet/` links those files
into a plain NUnit project — links, not copies — and GitHub Actions runs them on every push. **Unity is
never run in CI**: it needs a licence secret, and the vendored xLua plugin is desktop x64 only, so a badge
would eventually go red for reasons unrelated to the code.

**What the second compilation does and does not guarantee.** It pins the *language level* (`LangVersion
9.0`) and the *core library surface*. It does **not** guarantee assertion-API parity: Unity bundles NUnit
3.5.0 while the lowest version workable on `net9.0` with a modern adapter is 3.14.0. Slice 2 hit that gap
twice. **The Unity batchmode run is the authority, and nothing is pushed until both suites are green.**

---

## Running the tests

**The Unity Editor must be closed.** Unity refuses `-batchmode` while the Editor holds the project lock.

```powershell
# Engine-free core - fast, no Unity, also what CI runs.
dotnet test dotnet/HotUpdateABTest.sln

# Everything, including the Lua bridge. Requires the Editor to be closed.
& "C:\Program Files\Unity\Hub\Editor\6000.0.59f2\Editor\Unity.exe" `
    -batchmode -nographics `
    -projectPath "C:\SampleProjects\hot-update-ab-test" `
    -runTests -testPlatform EditMode `
    -testResults "C:\SampleProjects\hot-update-ab-test\TestResults\editmode.xml" `
    -logFile "C:\SampleProjects\hot-update-ab-test\TestResults\editmode.log"
```

`TestResults/` is gitignored.

## Test results

Last run 2026-08-30, both suites green.

| Suite | Tests | Result |
| --- | --- | --- |
| `dotnet test` (engine-free core) | 195 | 195 passed, ~13 s |
| Unity EditMode batchmode | 200 | 200 passed |

The Unity run is a superset: the same 195 core tests plus 5 that need the engine. The soak accounts for
almost all of the 13 seconds; the rest of the suite is about one second.

| Area | Tests | What is covered |
| --- | --- | --- |
| `Murmur3Tests` | 12 | SMHasher verification value, reference vectors, UTF-8 encoding, avalanche, every tail length, buffer-range guards |
| `LayerAllocatorTests` | 11 | Mutual exclusion by bucket sweep, holdout traffic, uniformity, cross-layer independence, the shared-salt negative control, status gating |
| `VariantAssignerTests` | 12 | Determinism, exact bucket-space partition, weighted split, independence from layer position, boundary shift, arm order, zero weights, 64-bit overflow guard |
| `ConfigReaderTests` | 22 | Absent-vs-zero weight, schema gate and short-circuit, malformed input, all findings collected, message wording, unknown fields ignored |
| `ConfigValidatorTests` | 15 | Overlapping traffic, adjacent ranges, draft exemption, shared layer salts, unknown refs, duplicates, missing control, allocation bounds |
| `ConfigServiceTests` | 21 | The full ladder, every failure mode preserving what is in force, recovery after rejection, log-once, skip-when-unchanged, content drift, kill-switch pin discard |
| `ConfigServiceConcurrencyTests` | 3 | 4 threads × 20,000 resolves against 200 swaps with per-result coherence inspection |
| `ExperimentResolverTests` | 22 | Resolution order, audience-after-allocation, pin precedence, sticky vs stateless, forced override and its limits, explanations for every non-assignment |
| `PinReconcilerTests` | 11 | All four invalidation reasons, per-user variant removal, the stickiness-flip round trip, idempotence |
| `ShippedDefaultsTests` | 6 | The real file parses and validates, every experiment stopped, cold-start-offline end to end |
| `ExposureTrackerTests` | 15 | Resolution logs nothing, dedup within a session, a new session logging again, per-user counting, session rollover, contamination flagging, forced and synthetic traits, the funnel denominator |
| `ConversionTrackerTests` | 10 | Attribution from the record across a weight ramp and across the kill switch, multi-layer credit, unattributed visibility, trait inheritance, rate per exposed user |
| `SrmCheckTests` | 13 | Healthy and skewed splits, sampling noise not alarming, both floors, zero-weight arms, single-arm and empty cases, the many-arm approximation |
| `MetricsAggregatorTests` | 12 | Both breakage modes and the negative control, population filtering, orphaned arms, the printed table, aggregation cost |
| `TelemetrySoakTests` | 2 | 20,000 randomised operations per seed, two seeds, invariants throughout |
| `LuaEnvironmentSmokeTests` | 5 | Native VM under batchmode, values both ways, `LuaFunction` handles, `LuaException`, custom `require` loader |

### Tests that demonstrate the failure mode rather than the success path

- **`Murmur3Tests.TheImplementationMatchesSmHashersVerificationValue`** — the reference self-test at
  `0xB0F57EE3`. One number pins the whole implementation.
- **`LayerAllocatorTests.ReusingOneSaltAcrossLayersWouldCorrelateThem`** — two layers sharing a salt,
  asserted *perfectly* confounded.
- **`MetricsAggregatorTests.AnSrmCheckOverAssignmentsWouldHaveMissedIt`** — the new one, and the most
  important in this slice. It runs the suppressed-exposure breakage, feeds the same run's *assignment*
  counts to the same checker, and asserts they come back **healthy** while the exposure-based check alarms.
  The reason SRM is measured over exposures is therefore demonstrated by the suite, not argued in a comment.
- **`PinReconcilerTests.FlippingToStatelessAndBackRestoresTheOriginalAssignments`** — proves the policy
  toggle is lossless.
- **`SrmCheckTests.ThreeAgainstOneIsNotEvidenceOfAnything`** — the floor. Without it the light flashes red
  on the demo's first click and is ignored by the third.

---

## Why the sample-ratio check is measured on exposures

This is one of the two or three things in this repository most worth being able to explain.

**The plan originally said to run the chi-square over the assignment split. That is wrong here.** The demo
ships a deliberate-breakage button that makes one variant skip its exposure logging. Under that fault the
*assignment* split stays a flawless 50/50 — bucketing is working perfectly — while half the data being
collected is silently destroyed. An assignment-based ratio light sails straight through the exact failure it
exists to catch.

The population an analysis draws conclusions from is the set of users who **actually saw the treatment**. So
that is the population whose ratio has to be tested. `SrmCheck` runs over distinct exposed users per arm,
compared against the configured weights.

**Two signals, because two faults produce the same symptom.** A skewed exposed split says something is
wrong; the assignment-to-exposure funnel rate says *what*:

| Fault | Exposed split | Funnel rate per arm |
| --- | --- | --- |
| Suppressed exposure logging in one arm | skewed | **collapsed in that arm** |
| Skewed bucketing | skewed | healthy everywhere |

Both are asserted in `MetricsAggregatorTests`.

**Counted in distinct users, not events.** A user returning in a second session is exposed again, and
counting events would let a handful of heavy users move the ratio. The question is how the population
divided, so the unit is the person.

### Thresholds, and why they are where they are

| Setting | Value | Reasoning |
| --- | --- | --- |
| Minimum expected count per arm | 5 | The standard validity condition for chi-square. Below it the statistic is not meaningful regardless of how much total traffic there is — a 0.02% canary arm suppresses the verdict on its own. |
| Minimum total exposed users | 100 | A practical floor above the statistical one. The cell rule is satisfied at ten users on an even split, which is far too few for a light somebody is watching. Three against one is not evidence of anything. |
| Alarm significance | **p < 0.0005** | Not 0.05. SRM is checked continuously over large populations where trivial imbalances become "significant" almost immediately; at 0.05 a healthy experiment alarms one time in twenty, every time anybody looks, and the light is furniture within a week. This is the region production platforms use. |
| Degrees of freedom | k − 1 over arms **with non-zero weight** | A zero-weight arm is not part of the split. Users found in one are a hard alarm rather than a statistical question — they are in an arm the operator emptied. |
| Below either floor | `Unknown`, never `Healthy` | "We cannot tell yet" and "we checked and it is fine" are different claims and a status light must not conflate them. |

The statistic is compared against a tabulated critical value rather than converted into a p-value:
reporting an exact p would mean implementing the regularised incomplete gamma function to display a number
nobody acts on differently. Beyond fifteen degrees of freedom the critical value falls back to a
Wilson–Hilferty approximation so a many-armed experiment still gets a verdict.

A warning band between healthy and alarm was considered and dropped. Production SRM is binary, an
intermediate state invites "it is probably fine", and `Unknown` already covers the honest third case.

---

## Other decisions made in this slice

**A session is a real value with a defined lifetime.** Starts at launch, and again on the first activity
after thirty idle minutes — the convention every mobile analytics product uses, so these counts mean the
same thing as the ones in whatever a studio already runs. Dedup is per session rather than per lifetime:
forever would turn the exposure count into a first-seen count, never would let one user reopening a screen
dominate an arm. Simulated users each get their own session, without which "simulate 5000 users" collapses
into one visit.

**Populations are explicit values, not scattered conditions.** Forced is excluded from every metric and from
SRM; synthetic is included; every report prints the population it was computed over. A user appearing in two
trait buckets is unioned rather than summed, so hand-testing during a simulation cannot inflate the count.

**Attribution reads the ledger and never re-resolves.** Tested across a weight ramp and across the kill
switch.

**A conversion credits every experiment the user was exposed to**, because with layers one purchase is
evidence in both at once. `AttributedCount` still counts one conversion.

**Unattributed conversions are surfaced, not just recorded** — they have a line in the printed table.

**Contamination is flagged rather than swallowed.** Variant is in the dedup key so a user who flips arms
produces two rows and a flag; the attribution target does not move to the second arm.

**The aggregator is a sink, not a scan.** Constant time per event, counters split across four trait
buckets so any population is a four-bucket sum at read time. A test asserts the growth is not super-linear.

---

## What is deliberately not here yet

| Slice | Contents |
| --- | --- |
| 4 | Lua host, patch loader, variant behavior registry, `PresentationSpec` contract |
| 5 | Local `HttpListener` config server, shop screen, metrics and LiveOps panels, `docs/PACKAGE_SPEC.md`, the action-pair audit table |
| 6 | FairyGUI package binding, README, media |

Deferred with reasons:

- **`FileAssignmentStore` and `FileConfigCache` still have no dedicated tests.** Thin adapters over covered
  in-memory implementations; the behaviour worth testing — surviving a restart — belongs with the PlayMode
  suite in Slice 5.
- **`ExposureLedger.ForgetSession` is O(live dedup keys).** Fine at demo scale and called once per simulated
  user, but it scans rather than indexing by session. If the simulator ever runs six figures of users in one
  press it wants a session-keyed index; noted rather than built.
- **The soak's `SweepEvery` is 500.** The cheap invariants run after every operation; the O(population)
  sweep runs periodically, because every property it checks is monotonic and a violation cannot repair
  itself before the next sweep. This took the soak from 3m08s to 13s.

### A note on the FairyGUI package

The package is being authored in parallel as `AbTestDemo` at 1600×900 with a phone-shaped `containerDevice`.
Nothing in this repository binds to it yet. `docs/PACKAGE_SPEC.md` will be written in Slice 5 to **document
the component and child names that exist**, not to specify them in advance. `ShopScreen` stays an empty
375×667 container until `PresentationSpec` is fixed in Slice 4.
