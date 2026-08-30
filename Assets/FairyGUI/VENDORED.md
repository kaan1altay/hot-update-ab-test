# Vendored dependency: FairyGUI

- Upstream: https://github.com/fairygui/FairyGUI-unity
- Version: **5.2.0** (tag `5.2.0`)
- License: MIT (see `LICENSE.txt` in this folder)

## What was vendored

Runtime only:

| Path | Purpose |
| --- | --- |
| `Assets/FairyGUI/Scripts/` | The runtime, shipped upstream with its own `FairyGUI.asmdef` |
| `Assets/FairyGUI/Resources/Shaders/` | Shaders the runtime resolves by name, so they must stay under a `Resources` folder |

Deliberately **not** vendored: `Assets/Editor` (the package/asset import tooling),
`Assets/Examples` (~30 MB of demo art), `LuaSupport` and `UIProject` (the FairyGUI
Editor source project). This repository renders one shop/offer screen plus a set of
LiveOps tooling panels from its own authored package; it does not need the upstream
demo content.

## Local modifications

None. The upstream `FairyGUI.asmdef` is used as-is; it references
`Unity.TextMeshPro`, which Unity 6 provides through `com.unity.ugui`.

## Note on the UI package

The `.fui` / `_fui.bytes` UI package the demo binds to is authored by hand in the
FairyGUI Editor (a separate desktop application) and is not part of this dependency.
`docs/PACKAGE_SPEC.md` is the authoring contract: exact component names, child names
and sizes. The demo also ships a programmatic fallback UI built in code, so it runs
and can be tested headless whether or not the authored package is present.
