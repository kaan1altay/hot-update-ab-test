# Status

Engineering log for `hot-update-ab-test`. Updated at the end of every slice, so the numbers here are
checkable rather than claimed.

**Slice 1 of 6 complete.** Deterministic bucketing and two-stage assignment, tested in both compilations.

---

## Environment

| | |
| --- | --- |
| Unity | 6000.0.59f2 (URP 17.0.4, Input System 1.14.2, Test Framework 1.6.0) |
| xLua | v2.1.16 (MIT), vendored — see `Assets/XLua/VENDORED.md` |
| FairyGUI | 5.2.0 (MIT), runtime only, vendored — see `Assets/FairyGUI/VENDORED.md` |
| Newtonsoft.Json | `com.unity.nuget.newtonsoft-json` 3.2.1 (Newtonsoft.Json 13.0.3) |
| .NET (CI + local second compilation) | net9.0, C# LangVersion pinned to 9.0 |
| Platforms Lua runs on | Windows and Linux desktop x64 only — the vendored xLua native plugin covers no others |

## Assemblies

| Assembly | Location | Depends on | Notes |
| --- | --- | --- | --- |
| `HotUpdateABTest.Core` | `Assets/HotUpdateABTest/Runtime/Core/` | nothing | `noEngineReferences: true`. The decision core. |
| `HotUpdateABTest.Runtime` | `Assets/HotUpdateABTest/Runtime/Unity/` | Core, XLua | The engine-facing half. |
| `HotUpdateABTest.Tests.EditMode` | `Assets/HotUpdateABTest/Tests/EditMode/` | Core, Runtime, XLua, TestRunner | Editor-only, `UNITY_INCLUDE_TESTS`. |
| `HotUpdateABTest.Core.Tests` | `dotnet/HotUpdateABTest.Core.Tests/` | Core (linked), NUnit | Not a Unity assembly. See below. |

### Why the core is compiled twice

Everything under `Runtime/Core/` is written without touching `UnityEngine`. `dotnet/` links those files
into a plain NUnit project — links, not copies, so there is exactly one source of truth — and GitHub
Actions runs them on every push. This is the whole CI story: **Unity is never run in CI.** It needs a
licence secret, and the vendored xLua plugin is desktop x64 only, so a badge would eventually go red for
reasons unrelated to the code. Editor-only suites run locally instead, with the command below.

The second compilation earns its place three ways beyond the badge:

- The engine-free claim is **enforced**, not asserted. The Core `asmdef` sets `noEngineReferences: true`,
  and the workflow greps for a `using UnityEngine` before it builds. Both compilations reject it.
- `LangVersion 9.0` matches what Unity 6 accepts, so CI rejects newer C# before the Editor does.
- The core suite runs in under a second, against a multi-minute Unity boot.

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
| `dotnet test` (engine-free core) | 35 | 35 passed |
| Unity EditMode batchmode | 40 | 40 passed |

The Unity run is a superset: the same 35 core tests plus 5 that need the engine.

| Area | Tests | What is covered |
| --- | --- | --- |
| `Murmur3Tests` | 12 | SMHasher verification value, reference vectors, UTF-8 encoding, avalanche, every tail length, buffer-range guards |
| `LayerAllocatorTests` | 11 | Mutual exclusion by bucket sweep, holdout traffic, stability, uniformity, cross-layer independence, the shared-salt negative control, status gating |
| `VariantAssignerTests` | 12 | Determinism across rebuilt configs, exact bucket-space partition, weighted split, independence from layer position, boundary-shift on weight change, arm order, zero weights, 64-bit overflow guard |
| `LuaEnvironmentSmokeTests` | 5 | Native VM loads under batchmode, values cross both ways, `LuaFunction` handles, errors surface as `LuaException`, custom `require` loader is consulted |

### Two tests worth pointing at

**`Murmur3Tests.TheImplementationMatchesSmHashersVerificationValue`** runs the verification procedure that
ships with the reference implementation — 256 keys of increasing length with decreasing seeds, then a hash
of the concatenated results — and asserts `0xB0F57EE3`. A wrong constant, a wrong rotation, a big-endian
block read or a mishandled tail length all change that number. It is one assertion that pins the whole
implementation, and it beats trusting a handful of remembered string vectors.

**`LayerAllocatorTests.ReusingOneSaltAcrossLayersWouldCorrelateThem`** is a negative control. It builds two
layers that share a salt and asserts they are *perfectly* confounded — every user in one experiment is in
the other. The failure mode that per-layer salting prevents is therefore demonstrated by the suite rather
than explained in a comment.

### Risk retired early

`LuaEnvironmentSmokeTests` exists before anything needs Lua, on purpose. xLua is backed by a vendored
native library, and a large part of the planned test strategy assumes it runs under `-batchmode
-nographics`. It does, so Slice 4 can be built on that assumption instead of discovering otherwise
halfway through a Lua bridge.

---

## What is deliberately not here yet

Slice 1 is the decision core and nothing else. Not yet built, in planned order:

| Slice | Contents |
| --- | --- |
| 2 | Config model reader, strict validator, `IConfigSource`, `ConfigService`, last-known-good ladder, kill switch, assignment pinning, QA override |
| 3 | Exposure tracking at view time, conversion attribution, analytics sink, SRM guardrail, fuzz/soak invariants |
| 4 | Lua host, patch loader, variant behavior registry, presentation-spec contract |
| 5 | Local `HttpListener` config server, programmatic shop screen, metrics and LiveOps panels, `docs/PACKAGE_SPEC.md` |
| 6 | FairyGUI package binding, README, media |

`ExperimentResolver` — the piece that composes the two assignment stages with audience predicates, pins
and the QA override — was planned for Slice 1 but moved to Slice 2. All three of its inputs land there,
and building it now would have meant writing it against placeholders and rewriting it immediately. The two
stages it composes are complete and fully tested.
