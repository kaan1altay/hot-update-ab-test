# Status

Engineering log for `hot-update-ab-test`. Updated at the end of every slice, so the numbers here are
checkable rather than claimed.

**Slice 6 of 6 complete.** The authored `ShopScreen` and `OfferCard` bound, boot validation of the whole
binding, the demo script, and the README.

---

## Environment

| | |
| --- | --- |
| Unity | 6000.0.59f2 (URP 17.0.4, Input System 1.14.2, Test Framework 1.6.0) |
| xLua | v2.1.16 (MIT), vendored — see `Assets/XLua/VENDORED.md` |
| FairyGUI | 5.2.0 (MIT), runtime only, vendored — see `Assets/FairyGUI/VENDORED.md` |
| Newtonsoft.Json | `com.unity.nuget.newtonsoft-json` 3.2.2 (Newtonsoft.Json 13.0.3) |
| .NET (CI + local second compilation) | net9.0, C# LangVersion pinned to 9.0 |
| NUnit | Unity bundles **3.5.0** (`com.unity.ext.nunit`); the .NET project pins **3.14.0** |
| Platforms Lua runs on | Windows and Linux desktop x64 only — the vendored xLua native plugin covers no others |

## Assemblies

| Assembly | Location | Depends on | Notes |
| --- | --- | --- | --- |
| `HotUpdateABTest.Core` | `Assets/HotUpdateABTest/Runtime/Core/` | Newtonsoft.Json | `noEngineReferences: true`. The decision core. |
| `HotUpdateABTest.Runtime` | `Assets/HotUpdateABTest/Runtime/Unity/` | Core, XLua | The engine-facing half: file cache, file-backed pins, StreamingAssets. |
| `HotUpdateABTest.Transport` | `Assets/HotUpdateABTest/Transport/` | Core | The local HTTP server. Demo tooling. |
| `HotUpdateABTest.Demo` | `Assets/HotUpdateABTest/Demo/` | Core, Runtime, Transport, FairyGUI, XLua | The console and the shop screen. Demo tooling. |
| `HotUpdateABTest.Tests.EditMode` | `Assets/HotUpdateABTest/Tests/EditMode/` | all of the above, TestRunner | Editor-only, `UNITY_INCLUDE_TESTS`. |
| `HotUpdateABTest.Tests.PlayMode` | `Assets/HotUpdateABTest/Tests/PlayMode/` | all of the above, TestRunner | Needs a running stage. |
| `HotUpdateABTest.Core.Tests` | `dotnet/HotUpdateABTest.Core.Tests/` | Core (linked), NUnit | Not a Unity assembly. See below. |

Transport and Demo are **runtime** assemblies rather than Editor-only ones: a MonoBehaviour in an
Editor-only assembly cannot be added to a GameObject, so the demo simply would not run. What keeps them out
of a real build is folder membership — a game adopting this framework takes `Runtime/` and leaves
`Transport/` and `Demo/` behind — not an assembly definition platform filter. Said plainly rather than
implying a guarantee the build does not make.

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

**Do not chain the EditMode and PlayMode invocations in one shell command.** Launching the second
Unity process immediately after the first exits can have it run the *previous* compilation of a test
assembly: the first process is still writing `Library/ScriptAssemblies` as the second starts. This is
not theoretical - it cost an afternoon here. Two PlayMode tests reported failures whose assertion text
did not match any assertion in the source, because the source had been edited and the run was
executing the build from before the edit. The same source, run as its own invocation, was green
eleven times.

The tell is worth knowing because the failure is indistinguishable from a real one: a red suite that
is measuring code you no longer have. If a failure message does not correspond to the assertion you
can read on disk, suspect the build before you suspect the test. Run each platform as a separate
command and let the first fully exit.

## Test results

Last run 2026-08-31, all three suites green.

| Suite | Tests | Result |
| --- | --- | --- |
| `dotnet test` (engine-free core) | 234 | 234 passed, ~12 s |
| Unity EditMode batchmode | 315 | 315 passed, 0 skipped |
| Unity PlayMode batchmode | 23 | 23 passed |

### How the suites overlap

**338 distinct tests.** Verified by set arithmetic on fully-qualified test names from the three result
files, not by subtracting counts:

| | Count | |
| --- | --- | --- |
| Core tests run by `dotnet test` **and** again inside Unity | **234** | every one of them; none are CI-only |
| Unity-only EditMode tests (Lua VM, sockets, the package) | **81** | 234 + 81 = the 315 EditMode total |
| PlayMode tests | **23** | no overlap with EditMode |
| **Distinct** | **338** | |

The core suite is a strict subset of EditMode, because the same source files are compiled twice — once as a
plain .NET project, once by Unity. **Adding the three suite totals gives 572, which counts the core tests
twice. Do not quote it.**

The soak accounts for most of the core suite's twelve seconds; everything else is about one.

Swap `-testPlatform EditMode` for `-testPlatform PlayMode` in the command above to run the UI suite.

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
| `PresentationSpecReaderTests` | 21 | The closed field set, every authored value, unknown fields and values rejected, layer ownership, whole-table rejection, text limits, composition |
| `AudiencePredicateTests` | 8 | Predicate read from config, match and mismatch, fail-closed with no evaluator, clause-before-predicate ordering, narrowing only |
| `LuaSandboxTests` | 11 | Filesystem, process control, the C# bridge, `_G`, `require`, runtime compilation, `debug`, bytecode, nondeterminism sources, cross-patch leakage, and that the pure libraries still work |
| `LuaVariantHostTests` | 22 | Baseline behaviors, purity over 50 calls, one-broken-patch isolation, staged registration, reload idempotence, deletion reverting, a patch adding a variant, every spec rejection path, predicates failing closed, disposal |
| `LuaCannotReachTelemetryTests` | 6 | A patch attacking the sink through the context, the C# bridge and enumeration; suppression and duplication attempts; the context proven to carry values only |
| `LuaEnvironmentSmokeTests` | 5 | Native VM under batchmode, values both ways, `LuaFunction` handles, `LuaException`, custom `require` loader |
| `LocalConfigServerTests` | 12 | Every scenario producing the fault it advertises, version bumping, the socketless fallback; over a real socket: binding without elevation, port scanning, fetch, 503-as-unreachable, stop and restart |
| `PackageBindingTests` | 6 | The real published package has every component, child and controller page the code binds to |
| `DemoActionPairTests` | 17 | Every scenario recovering, the override cycling and clearing, both breakages breaking and recovering, the two being distinguishable, reset undoing everything, every button handled |
| `DemoPlayModeTests` | 7 | The fallback declaring the same names as the package, the demo starting on whichever UI exists, buttons moving what is on screen, the table filling, the forced banner appearing and clearing |
| `ShareAndSizingTests` | 10 | Observed and expected share, zero-weight arms excluded from the split, the text limits the card is drawn to hold, rejection tokens derived from issue codes |
| `ExposureAtViewTimeTests` | 3 | A shop screen built and rendered 20× with the sink empty, `MarkExposed` producing exactly one, the live demo repainting without manufacturing exposures |
| `StripWidthTests` | 5 | The bar title and the spec strip measured through FairyGUI's own text layout at their worst case, and the strip proven to be set to `Shrink` |
| `PlayTestRegressionTests` | 8 | The three defects the first hand play-test found, each reproduced against the authored package before it was fixed, plus the two observations that turned out not to be code defects |

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
- **`LuaCannotReachTelemetryTests`** — the whole fixture. Six patches attack the analytics sink by
  different routes and each asserts the sink is *untouched* afterwards. A patch that could forge an
  exposure would make every number downstream meaningless while leaving the reports looking normal, which
  is worse than an outage because an outage is visible.
- **`LuaVariantHostTests.APatchThatThrowsWhileRegisteringCommitsNothingItStaged`** — the half-applied patch.
  Registers one variant, throws, and neither registration survives.

---

## The Lua sandbox

A patch channel is a remote code execution channel. Whoever can publish a patch runs code on every device
that fetches it, so the environment is decided deliberately rather than inherited.

### What a patch can reach

| Available | Why |
| --- | --- |
| `string`, `table` | Pure, and what a copy-formatting behavior is actually made of. |
| `math` **minus `random` and `randomseed`** | Arithmetic is fine; a random source would break purity. |
| `assert`, `error`, `pcall`, `xpcall`, `select`, `type`, `tostring`, `tonumber` | Pure language built-ins. |
| `pairs`, `ipairs`, `next`, `rawget`, `rawset`, `rawlen`, `rawequal`, `setmetatable`, `getmetatable`, `unpack` | Table manipulation. |
| `print` | Routed into the framework log with a `[lua]` prefix; never stdout. |
| `register`, `register_audience` | The two things a patch exists to do. |

### What is removed, and why

| Removed | Reason |
| --- | --- |
| **`CS`, `xlua`** | xLua's bridge to the entire C# type system. Left in, a patch reaches the analytics sink, the filesystem and UnityEngine directly — **every other omission here would be decorative**. |
| `io`, `os` | Filesystem and process control. `os` also carries `time`, `clock` and `date`, which would break purity even if the rest were harmless. |
| `require`, `package` | A patch may not pull in modules of its own choosing; C# decides what source runs. There is **no `require` at all** — C# reads files and hands source to a sandboxed `load`, which is stronger than the conventional filtered-`require`. |
| `load`, `loadstring`, `dofile`, `loadfile` | Compiling more code at runtime routes straight around the sandbox by supplying a different `_ENV`. |
| `debug` | `getupvalue`/`setupvalue` reach into other closures, including the registry's. |
| `coroutine` | No use case, and suspended state across calls would undermine purity. |
| `collectgarbage` | A patch has no business tuning the VM. |
| `_G` | The real global table. Exposing it makes the whole exercise decorative. |
| `math.random`, `math.randomseed` | The same user must resolve to the same presentation every time. |

**Text mode only.** Chunks load with `load(source, name, "t", sandbox)`. The Lua bytecode verifier is not
hardened and crafted bytecode can subvert the VM outright, so a channel accepting source alone is a
materially smaller attack surface.

**The bootstrap is not patchable.** It defines the sandbox and lives outside the patch root — a hot update
that could replace it could rewrite the rules it is supposed to obey.

### The context a behavior sees

Values only: `user_id`, `account_level`, `platform`, `country`, `layer_id`, `experiment_id`, `variant_id`,
`config_version`, `has_original_price`. No functions, no C# objects, no collections. Adding a field here is
a decision about what a hot update can reach, which is why the list is short and every entry is a plain
scalar. A test walks the context asserting every value is a string, number or boolean.

### Purity

Enforced by construction rather than convention: with no clock and no random source there is nothing to be
impure with. `CallingABehaviorTwiceWithTheSameContextGivesAnIdenticalSpec` calls fifty times and compares.

---

## The action-pair audit

For every control that puts the demo into a state, the control that takes it back out. Carried over from
`ui-reddot-system`, where two of the three bugs hand play-testing found were of the "nothing makes this
false again" class — a toggle that could be set but never cleared. That is the failure mode manual testing
reliably catches, so it is worth catching first.

| Enters state | Returns to prior state | Asserted by |
| --- | --- | --- |
| `btnScenarioMalformed` | `btnScenarioNormal` | `ScenarioMalformedThenNormalRecovers` |
| `btnScenarioBadSchema` | `btnScenarioNormal` | `ScenarioBadSchemaThenNormalRecovers` |
| `btnScenarioOffline` | `btnScenarioNormal` | `OfflineThenNormalRecovers` |
| `btnScenarioKill` | `btnScenarioNormal` | `TheKillSwitchStopsEveryExperimentAndNormalStartsThemAgain` |
| `btnScenarioPause` | `btnScenarioNormal` | `PauseThenNormalRestoresOnlyThePausedExperiment` |
| `btnScenarioWeights` | `btnScenarioNormal` — **asymmetric, see below** | `RestoringTheWeightsDoesNotRestoreTheArmsOfUsersAlreadyExposed` |
| `btnForceVariant` | `btnClearForce`, or cycling past the last arm | `ForcingAVariantThenClearingItRestoresBucketing`, `CyclingTheOverridePastTheLastArmClearsIt` |
| `btnInjectSkew` | press again | `BucketingSkewBreaksTheRatioAndFixingItRecovers` |
| `btnSkipExposure` | press again | `SkipExposureBreaksTheRatioAndFixingItRecovers` |
| `btnServerToggle` (stop) | press again — the port is released and reclaimed | `TheServerCanBeStoppedAndStartedAgain` |
| `btnSimulate` | `btnClearState` | `ResetUndoesEveryStateAtOnce` |
| Drop a Lua patch, `btnReloadPatches` | delete it, `btnReloadPatches` | `DeletingAPatchAndReloadingRevertsToTheBaseline` |
| Reload repeatedly | no-op; reload rebuilds rather than diffs, so it cannot double-register | `ReloadingTheSamePatchTwiceChangesNothing` |
| A broken patch is skipped | fix or remove it; the log-once key clears on a clean reload | `ABrokenPatchIsReportedOncePerReloadRatherThanOnEveryCall` |
| **anything at all** | `btnClearState` | `ResetUndoesEveryStateAtOnce`, `ResetIsIdempotent` |

**The one deliberate asymmetry.** Ramping the weights and putting them back does *not* return an
already-exposed user to a different arm. That is not a missing pair — it is the sticky-after-exposure policy
working, and a user who has seen a treatment must not switch arms. It is asserted explicitly rather than
quietly omitted, so nobody later reads the gap as an oversight and "fixes" it.

**On-screen pairs** are checked in PlayMode too: `TheForcedBannerAppearsAndClears` presses the buttons and
asserts the banner's visibility both ways, because a banner that can be shown but never hidden is exactly
the bug this table exists to prevent.

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

## Decisions made in the final slice

**The whole binding is validated once at boot, not per use site.** Every `GetChild` returning null is a name
mistyped or a publish forgotten, and the symptom is a dead control that looks like a working one. Stopping
at the first failure finds one typo per run; checking at each use site finds them one interaction at a time.
`UiValidator` collects all of them and reports one message, at error level so a stale package fails the
PlayMode suite rather than shipping. `UiContract` is the single list behind the boot check, the package
tests and the fallback — and the package tests call the same validator, because a test with its own copy of
the matching logic can pass on a laxer rule than the thing it guards.

**`barShare` shows observed share against expected**, captioned `49.9% / 50.0%`, not the funnel rate. It
sits beside the ratio light and explains it: the light says the split is not plausible, the bars say which
arm is over-represented and by how much. The funnel signal is not lost — it is read from the `assigned` and
`exposed` columns side by side, which is also what distinguishes the two breakage modes, and the test that
asserts that distinction reads the data directly.

**The caption child is named `txtShare`, not `title`.** `GProgressBar` adopts a child named literally
`title` as its own title object and rewrites it from `titleType` inside `HandleSizeChanged`, so under that
name any layout pass — not merely a `value` write — could replace the caption with a bare percentage. The
first fix was to sequence the writes so the caption was set last. That worked, and it left the trap armed
for whoever next laid out a row. Renaming the child removes the trap instead of stepping around it: there
is no ordering left to get wrong. `ConsoleView` resolves `txtShare` and then `title`, so an older package
still binds, and `TheShareCaptionSurvivesAResizeOrIsKnownNotTo` asserts whichever of the two the package on
disk actually uses rather than going red when it changes.

**`MaxBadgeLength` is 10, lowered from 16.** Because the reader rejects rather than truncates, whatever the
constant says is guaranteed to arrive on screen, so it has to be a length the authored card can hold at a
legible size. Sixteen does not fit beside the offer name on a 335-wide card. Lowering the constant is the
honest fix; clipping at render time would be the dishonest one, and would quietly break the guarantee that
makes the reject-rather-than-truncate rule worth having.

**The rejection marker carries a class, not a sentence.** `[FALLBACK: unknown field]` on the spec strip,
the full validation message in the log. A viewer of a recording needs to know which kind of thing went
wrong and has no time to read prose off a still frame. The token comes from the machine-readable issue code
rather than by matching message text, so improving a message cannot silently change what the strip says.

**Prices stay in C#.** A patch channel is a remote code execution channel; letting it set what things cost
would be an unforced error. `priceStyle = "discounted"` presents the catalogue's existing original price
struck through, and a variant asking for it on an offer that has none gets `plain` rather than a
struck-through blank.

---

## Decisions carried from Slice 5

**The presentation spec is closed and finite.** Four fields, enumerated values, unknown keys rejected. The
value sets are enumerated against what the FairyGUI package actually contains: accepting `layout =
"carousel"` when no carousel was drawn would let a patch produce a *valid* spec the screen cannot render,
which is validation passing the buck to the renderer. An unrecognised value is treated exactly like a
malformed spec — fall back to control, log once. A test asserts the enum members are the authored ones, and
another asserts the combination count stays at or under eight so the package remains authorable
exhaustively. Full authoring contract in `docs/PRESENTATION_SPEC.md`.

**Spec fields are owned by layer.** A pricing behavior that sets `layout` has its whole spec rejected rather
than winning or losing a precedence fight. Resolving by precedence would mean one layer silently losing, and
the loser's experiment would then be measuring nothing.

**Lua sets no prices.** `priceStyle = "discounted"` presents the catalogue's existing original price struck
through; it does not apply a discount. A channel that can run code on every device should not also be able
to change what things cost.

**Rejection is whole-table.** One bad field discards the good ones too, for the same reason config rejection
is whole-payload. Text longer than the screen can hold is rejected rather than truncated — silent clipping
produces a screen that looks deliberate and reads as nonsense, and the patch author never finds out.

**Registrations are staged per patch file and committed only on success.** A file that registers two
variants and then throws leaves neither behind.

**Reload rebuilds rather than diffs.** Idempotent reload and reverting deletion both fall out for free,
rather than being two features to get right separately.

**Audience predicates AND with the declarative clauses.** A patch can only ever narrow an audience, never
widen one past the bounds the config declared. Clauses are checked first, so an excluded user never pays for
a Lua call.

**Everything about predicates fails closed** — error, non-boolean, unregistered, or no evaluator wired up at
all. The last case is worth stating separately: a config asking for a narrowing this build cannot perform is
not the same as a config asking for no narrowing.

**The spec reader is engine-free and Lua-free.** It validates a plain dictionary, so all 21 of its rules run
in the one-second CI suite rather than only in a run that needs the native VM.

---

## Decisions carried from Slice 3

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

## What is deliberately not here

All six slices are complete. What follows is deliberately absent, with reasons — nothing here is an
oversight, and each one is a decision somebody could reasonably have made differently.

**Not yet recorded.** `docs/DEMO_SCRIPT.md` is the shooting order with a still-frame tell for every beat;
the GIFs themselves are not in the repository.

Deferred with reasons:

- **`FileAssignmentStore` and `FileConfigCache` have no dedicated tests.** Thin adapters over in-memory
  implementations that are thoroughly covered, and the demo wires the in-memory ones. The behaviour worth
  testing in them — surviving a restart — needs a test that restarts, which the PlayMode suite cannot do.
  Genuinely untested; said plainly rather than counted as covered.
- **`ExposureLedger.ForgetSession` is O(live dedup keys).** Fine at demo scale and called once per simulated
  user, but it scans rather than indexing by session. Noted rather than built.
- **The soak's `SweepEvery` is 500.** Cheap invariants run after every operation; the O(population) sweep
  runs periodically, because every property it checks is monotonic. Took the soak from 3m08s to 13s.
- **The sandbox is a capability restriction, not a resource limit.** A patch cannot reach the filesystem or
  the C# bridge, but nothing stops it spinning in a `while true` loop and hanging the frame. Lua has no
  preemption, so bounding that needs a debug hook with an instruction-count budget. Worth doing before this
  shipped to real devices; out of scope for a demo where the only patch author is the person running it.
  **This is the one honest gap in the sandbox and it is deliberate.**
- **Behaviors are re-invoked per render rather than memoised.** Purity means the result is cacheable by
  `(behaviorKey, context)`, which would matter for a screen rebuilding every frame. The demo rebuilds on
  config change and on click, so it is not worth the invalidation surface yet.
- **The offer catalogue is four fixed offers.** Varying the catalogue as well as the presentation would
  make it impossible to say which change moved the numbers — which is the mistake the whole framework
  exists to help somebody avoid.
- **The config fetch runs on the main thread.** `ConfigService` documents an off-thread contract and the
  concurrency suite proves it holds, but the demo polls a localhost server every five seconds with a
  two-second timeout, so moving it to a worker would add a marshalling path for no observable gain. The
  contract exists for a real transport; this one does not need it.
- **The simulated conversion rate is fixed at 20% and identical across arms**, so the conversion column
  shows the plumbing rather than a lift. Fabricating a difference would make the panel look better and mean
  nothing.

### The FairyGUI package

Authored as `AbTestDemo` at 1600×900 and bound by name at runtime, with code generation off.

Two documents, deliberately different in kind. `docs/PRESENTATION_SPEC.md` says what *must* exist — the
closed vocabulary a Lua patch has, handed over mid-Slice 4 as soon as tests pinned it, so the interior could
be drawn in parallel. `docs/PACKAGE_SPEC.md` says what *does* exist — the real component and child names,
written after the package was authored, describing rather than specifying.

`PackageBindingTests` loads the real published package and asserts every component, child and controller
page the code touches is present. The binder degrades gracefully at runtime, which is right for a player and
exactly wrong to rely on for correctness: without those tests a rename would surface as a quietly blank
panel days later rather than a named failure at republish time.

One authored page is never selected: `SrmLight.state` has a `warn` page, and the framework only ever picks
`unknown`, `healthy` or `alarm`. The warning band was considered and dropped — production sample-ratio
checks are binary and an intermediate state invites "it is probably fine". The page is harmless and left in
case that is ever revisited.
