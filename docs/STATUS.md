# Status

Engineering log for `hot-update-ab-test`. Updated at the end of every slice, so the numbers here are
checkable rather than claimed.

**Slice 2 of 6 complete.** Configuration pipeline, strict validation, the fallback ladder, and the kill
switch.

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
into a plain NUnit project — links, not copies, so there is exactly one source of truth — and GitHub
Actions runs them on every push. **Unity is never run in CI**: it needs a licence secret, and the vendored
xLua plugin is desktop x64 only, so a badge would eventually go red for reasons unrelated to the code.

Newtonsoft is referenced from the Core assembly through `precompiledReferences`, which works alongside
`noEngineReferences: true` — verified, since that combination was the main risk in adopting it.

**What the second compilation does and does not guarantee.** It pins the *language level* (`LangVersion
9.0`, so CI rejects C# 10+ before the Editor does) and the *core library surface*. It does **not**
guarantee assertion-API parity: Unity bundles NUnit 3.5.0 while the lowest version workable on `net9.0`
with a modern test adapter is 3.14.0. That gap is real and this slice hit it twice — `Is.AnyOf` does not
exist in 3.5, and 3.5's `Has.Count` cannot resolve the property through `IReadOnlyList<T>`. Both passed
under `dotnet test` and failed the Unity run. The rule that follows: **the Unity batchmode run is the
authority, and nothing is pushed until both suites are green.** Core tests should stick to NUnit
3.5-era assertions.

---

## Running the tests

**The Unity Editor must be closed.** Unity refuses `-batchmode` while the Editor holds the project lock,
and the run fails with a lock error rather than waiting.

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
| `dotnet test` (engine-free core) | 141 | 141 passed, ~1.0 s |
| Unity EditMode batchmode | 146 | 146 passed |

The Unity run is a superset: the same 141 core tests plus 5 that need the engine.

| Area | Tests | What is covered |
| --- | --- | --- |
| `Murmur3Tests` | 12 | SMHasher verification value, reference vectors, UTF-8 encoding, avalanche, every tail length, buffer-range guards |
| `LayerAllocatorTests` | 11 | Mutual exclusion by bucket sweep, holdout traffic, stability, uniformity, cross-layer independence, the shared-salt negative control, status gating |
| `VariantAssignerTests` | 12 | Determinism across rebuilt configs, exact bucket-space partition, weighted split, independence from layer position, boundary shift on weight change, arm order, zero weights, 64-bit overflow guard |
| `ConfigReaderTests` | 22 | Absent-vs-zero weight, schema gate and its short-circuit, malformed/empty/wrong-root payloads, all findings collected, message wording, optional-field defaults, unknown fields ignored |
| `ConfigValidatorTests` | 15 | Overlapping traffic, adjacent ranges, draft exemption, shared layer salts, unknown layer refs, duplicate ids, missing control, zero weights, allocation bounds |
| `ConfigServiceTests` | 21 | The full ladder, cache preference, corrupt cache, every failure mode preserving what is in force, recovery after rejection, log-once behaviour, skip-when-unchanged, content drift, poll interval, kill-switch pin discard |
| `ConfigServiceConcurrencyTests` | 3 | 4 threads × 20,000 resolves against 200 swaps with per-result coherence inspection; concurrent applies; a snapshot held across a swap |
| `ExperimentResolverTests` | 22 | Resolution order, audience-after-allocation, pin precedence, sticky vs stateless, forced override and its limits, per-layer independence, explanations for every non-assignment |
| `PinReconcilerTests` | 11 | All four invalidation reasons, per-user variant removal, the stickiness-flip decision and its round trip, idempotence |
| `ShippedDefaultsTests` | 6 | The real file parses and validates, every experiment stopped, cold-start-offline end to end, startable by flipping status alone |
| `LuaEnvironmentSmokeTests` | 5 | Native VM loads under batchmode, values cross both ways, `LuaFunction` handles, errors surface as `LuaException`, custom `require` loader is consulted |

### Tests that demonstrate the failure mode rather than the success path

- **`Murmur3Tests.TheImplementationMatchesSmHashersVerificationValue`** — the reference self-test, asserted
  at `0xB0F57EE3`. One number pins the whole implementation.
- **`LayerAllocatorTests.ReusingOneSaltAcrossLayersWouldCorrelateThem`** — builds two layers sharing a salt
  and asserts they are *perfectly* confounded, so the damage per-layer salting prevents is demonstrated.
- **`ExperimentResolverTests.AnUnexposedUserIsRebucketedFreelyWhenTheWeightsChange`** — the flip side of
  stickiness. Without it, "exposed users keep their arm" could be satisfied by pinning everybody, which
  would make ramping impossible.
- **`PinReconcilerTests.FlippingToStatelessAndBackRestoresTheOriginalAssignments`** — proves the policy
  toggle is lossless, which is the whole reason a stickiness flip does not delete pins.
- **`ConfigServiceConcurrencyTests.AReaderNeverObservesAHalfAppliedConfiguration`** — inspects every one of
  80,000 concurrent resolves for a variant that does not belong to its experiment, or two experiments
  claiming one bucket.

---

## Decisions made in this slice

**Snapshots, and the threading contract.** A config is immutable and published with one reference
assignment. `CurrentSnapshot` is a lock-free volatile read, safe from any thread; `Apply`/`Refresh`/
`PollIfDue` are serialised by an internal lock; events fire outside that lock on the thread that caused
the change. The intended usage — decided here rather than in Slice 5 — is **fetch on a worker, apply on
the player loop**. Holding a snapshot across a swap is legitimate: a screen that resolved against version 7
can keep rendering version 7 until it re-reads, rather than changing under the player mid-frame.

**Rejection is not sticky.** No error latch, no backoff to clear, no unhealthy flag outliving its cause.
The next payload is read from scratch.

**Two events, not one.** `ConfigChanged` fires only when the configuration actually changed and is what
consumers re-resolve on. `StatusChanged` fires whenever the snapshot reference is replaced, including a
rung upgrade with identical content. Coming back online with the same payload updates the ladder display
without re-resolving a single user — the same "many signals, at most one evaluation" discipline the rest
of the framework follows.

**Same version, different bytes → refused.** The version label is a payload's identity. Honouring a silent
content change would leave the client running something the analysis pipeline attributes to a different
version 7. Reported once, so the server bug is findable.

**Log-once resets on genuine health, not on any successful fetch.** Content drift clears the failure
counters (the transport worked) but not the dedup set (the anomaly persists). Getting this wrong made the
drift warning repeat on every poll; its own test caught it.

**A stickiness flip does not delete pins.** Flipping to `stateless` stops pins being honoured but keeps
them, so flipping back restores the users who were already treated instead of re-bucketing them. Deleting
would make the toggle irreversible and destroy the record of who had been treated.

**Audience applies after allocation.** A user's bucket does not move because they failed a predicate, so a
targeted experiment holds its allocation width × match rate. Slice 3's sample-ratio check must compare
against audience-filtered expectations or a healthy targeted experiment will look broken.

**A pin outranks a narrowed audience, but not the kill switch.**

**Unknown JSON fields are ignored.** Refusing them would turn every additive server change into a forced
app update. Strictness is spent on declared fields, where it buys the absent-versus-zero distinction.

---

## What is deliberately not here yet

| Slice | Contents |
| --- | --- |
| 3 | Exposure tracking at view time, conversion attribution, analytics sink, SRM guardrail, fuzz/soak invariants |
| 4 | Lua host, patch loader, variant behavior registry, presentation-spec contract |
| 5 | Local `HttpListener` config server, programmatic shop screen, metrics and LiveOps panels, `docs/PACKAGE_SPEC.md`, the action-pair audit table |
| 6 | FairyGUI package binding, README, media |

`FileAssignmentStore` and `FileConfigCache` exist and are used by the demo wiring, but have no dedicated
tests yet: both are thin adapters over an in-memory implementation that is thoroughly covered, and the
behaviour worth testing in them — surviving a restart — belongs with the PlayMode suite in Slice 5.
