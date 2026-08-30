# Vendored dependency: xLua

- Upstream: https://github.com/Tencent/xLua
- Version: **v2.1.16** (tag `v2.1.16`)
- License: MIT (Tencent / THL A29 Limited)

## What was vendored

Only the parts needed to run Lua in the Unity Editor and in a Windows x64 player:

| Path | Purpose |
| --- | --- |
| `Assets/XLua/Src/` | Runtime + the code generator under `Src/Editor/` |
| `Assets/XLua/Resources/` | `util.lua.txt` and the profiler/tdr helper scripts xLua loads at runtime |
| `Assets/Plugins/x86_64/` | Native Lua VM for 64-bit desktop (`xlua.dll`, `libxlua.so`) |

Deliberately **not** vendored, to keep the repository small: `Assets/XLua/Doc/`,
`Assets/XLua/Examples/`, `Assets/XLua/Tutorial/`, `Assets/XLua/Editor/ExampleConfig.cs`
(config for the dropped examples), and the Android / iOS / WSA / WebGL / macOS-bundle
native plugins. Add the platform plugin you need from the upstream release when you
target that platform.

## Local modifications

Two assembly definitions were added so that this repository's own code can live in
assembly definitions and still reference xLua (an `asmdef` assembly cannot reference
the default `Assembly-CSharp`):

- `Assets/XLua/Src/XLua.asmdef` — assembly `XLua`
- `Assets/XLua/Src/Editor/XLua.Editor.asmdef` — assembly `XLua.Editor`

The `Editor` folder is nested inside the `Src` assembly definition's folder, and an
assembly definition overrides Unity's special-folder rules, so the second asmdef is
what keeps the generator out of player builds.

No xLua source file was edited.
