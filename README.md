# CloXray

An x-ray womb mod for Koikatsu and Koikatsu Sunshine: a see-through body revealing a transparent
uterus, ovaries and tubes, with a fillable liquid inside the womb (gravity, wobble, slosh) and a
vaginal canal that opens and reacts to penetration in real time. Works in CharaStudio scenes and,
as of 1.1, in the main game's H scenes (Free-H).

## Free-H support (new in 1.1)

Toggle the womb on the H partner with **Shift+Alt+W** (rebindable in the F1 menu, *Free-H → Toggle
womb hotkey* — Studio keeps its own Shift+Alt+X) and the x-ray,
canal reaction and liquid all work during normal H play. The mod does not change the H animations themselves - it adapts to them:
it measures each animation and sizes the penis to fit the pair you are playing, so penetration
lines up even on odd-sized characters (very small or very large girls and males included). The
sizing tables for common animations ship with the mod; anything unknown is learned after a
single stroke and remembered.

Version 1.1 adds the Free-H support above (per-animation penis sizing from shipped measurement
tables with live learning, separate weak/strong loop fits, per-male canal width, mid-penetration
womb spawning), a womb that can be placed anywhere on the character rather than only in the vagina
(vaginal, anal, or reacting on its own wherever you put it), and a large Studio performance pass.
See `plugin/CHANGELOG.md`.

Note on BetterPenetration: the plugin ships two small BP work-arounds (a duplicate-constraint
guard and a penis-FK fix on scene load). Both are already fixed upstream in BetterPenetration
itself and will reach the HF Patch in a future update; once they do, the work-arounds will be
removed in a later CloXray version. Running the fixed BP alongside them causes no conflict.

The finished build is on the [Releases](../../releases) page. This repository holds the plugin
and shader source; the womb studio item ships prebuilt (see below).

## Components

| Folder | What it is | Builds to |
|---|---|---|
| `plugin/` | The BepInEx plugin (`LiquidWobbleMPB`) that drives the liquid and the reactive canal. | `LiquidWobbleMPB.dll` |
| `shaders/` | The Unity (5.6) shader sources — x-ray body, organs, liquid. | `[Clo]XrayShaders.zipmod` |
| `docs/` | User manual and reference. | — |

The womb studio item (`[Clo]XrayWomb1.zipmod`) ships prebuilt on the [Releases](../../releases)
page — its mesh is derived from GFanon's (see Credits) and is assembled by a separate pipeline
maintained outside this repository.

## Install (for users)

Grab the zip for your game from the latest [Release](../../releases) — `KK_CloXray.zip` for
Koikatsu, `KKS_CloXray.zip` for Koikatsu Sunshine — and extract it into
the game folder. It carries the two zipmods (`mods/Clo/`), the plugin (`BepInEx/plugins/Clo/`),
the manual, and an example scene (KK zip).

See [docs/CloXray_Manual.md](docs/CloXray_Manual.md) for usage. The womb requires the plugin
(the liquid is hidden without it, by design).

## Build (for developers)

Plugin — needs a .NET toolchain plus the game/BepInEx reference DLLs from your own Koikatsu install.
Point the build at your install with `KKDir` (Koikatsu) / `KKSDir` (Sunshine):

```bash
dotnet build plugin/LiquidShaderWobble.KK/LiquidShaderWobble.KK.csproj -c Release -p:KKDir="D:\Games\Koikatsu"
```

Or set them once in a `Directory.Build.props` next to the solution (kept out of the repo).

Shaders — needs Unity 5.6.2f1 plus xukmi's KKShadersPlus includes (the three organ shaders
`#include` them — see [shaders/Assets/Shaders/Includes/README.md](shaders/Assets/Shaders/Includes/README.md)).
Compile via the build entry point, then package the result into a Koikatsu sideloader zipmod with
the standard modding workflow:

```bash
Unity.exe -batchmode -nographics -quit -projectPath shaders -executeMethod BuildCloXray.Build
```

## Reporting a problem

Open an [issue](../../issues) with your game (KK or KKS) and what you did. If the womb does not
react or the x-ray does not appear, a log helps a lot: turn on **F1 -> General -> Diagnostic log
(for bug reports)**, reproduce the problem, then attach `BepInEx/LogOutput.log`. The switch is off
by default and takes effect live - a shipped build logs nothing until you turn it on.

## License

[GPL-3.0](LICENSE)

## Credits

The womb mesh is based on GFanon's [GF] womb mod.

Tooling and bases: [BetterPenetration](https://github.com/Animal42069/BetterPenetration)
(Animal42069, GPL-3.0 — the penis bones it reads), RSkoi (the wobble plugin it builds on),
[xukmi/KKShadersPlus](https://github.com/xukmi/KKShadersPlus) (MIT — the shader base the organ
shaders extend), Minionsart (liquid-shader technique).
