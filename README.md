# CloXray

An x-ray womb mod for Koikatsu CharaStudio: a see-through body revealing a transparent uterus,
ovaries and tubes, with a fillable liquid inside the womb (gravity, wobble, slosh) and a vaginal
canal that opens and reacts to penetration in real time.

Koikatsu (KK) is the primary version. The Koikatsu Sunshine (KKS) build is still in testing —
the zipmods are shared; KKS uses its own plugin build. Report KKS issues if you hit any.

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
Koikatsu (primary), `KKS_CloXray.zip` for Koikatsu Sunshine (in testing) — and extract it into
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

## License

[GPL-3.0](LICENSE)

## Credits

The womb mesh is based on GFanon's [GF] womb mod.

Tooling and bases: [BetterPenetration](https://github.com/Animal42069/BetterPenetration)
(Animal42069, GPL-3.0 — the penis bones it reads), RSkoi (the wobble plugin it builds on),
[xukmi/KKShadersPlus](https://github.com/xukmi/KKShadersPlus) (MIT — the shader base the organ
shaders extend), Minionsart (liquid-shader technique).
