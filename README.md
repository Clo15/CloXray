# CloXray

An x-ray womb mod for Koikatsu and Koikatsu Sunshine: a see-through body revealing a transparent
uterus, ovaries and tubes, with a fillable liquid inside the womb (gravity, wobble, slosh) and a
vaginal canal that opens and reacts to penetration in real time. Works in CharaStudio scenes and,
as of 1.1, in the main game's H scenes (Free-H).

## Free-H support

Toggle the womb on the H partner with **Ctrl+Shift+W** - the same key as in Studio (rebindable in
the F1 menu, *Free-H → Toggle womb hotkey*) and the x-ray,
canal reaction and liquid all work during normal H play. The mod does not change the H animations themselves - it adapts to them:
it measures each animation and sizes the penis to fit the pair you are playing, so penetration
lines up even on odd-sized characters (very small or very large girls and males included). The
sizing tables for common animations ship with the mod; anything unknown is learned after a
single stroke and remembered.

### New in 1.1.6

- **Free-H costs about 2ms a frame less.** A scene-wide search ran every single frame to answer a
  question that only changes when the scene does; the plugin's own cost during H fell from 2.33ms to
  0.30ms a frame. Turn on *Diagnostic log* and the log reports that share every second.
- **`Ctrl+Shift+A` sets up a womb at her anus**, on the stencil pair she is already using.
  `Ctrl+Shift+W` handles her vaginal womb; press it with a womb selected and that womb alone is
  wired, taking the penis aimed nearest it - which is how a womb worn anywhere else gets set up.
- **New per-womb slider `BP_IndependentRings`.** At 0 the canal behaves as before: a ring opens when
  something reaches it and stays open while that thing is deeper. Above 0 each ring reacts only to
  what is beside it, so a toy opens one ring at a time as it travels, and the value sets how wide a
  band travels with it (about 20 grips a ball tightly). Colliders only - a BetterPenetration penis
  spans every ring behind its tip either way.
- **The penis sits in the middle of the opened canal** instead of against its front wall.
- **A womb away from the default spot reacts at its real depth**, and only the womb on her vagina
  takes BetterPenetration's entry link - an anal, second or free-standing womb is entered at its own
  canal mouth.
- **The one-key setup says so when the male's uncensor is not BetterPenetration**, instead of
  looking for a penis it cannot find.

### Fixed in 1.1.5

- **Toys** open the canal the way they did in 1.1.3 again.
- **`Ctrl+Shift+W`** ignores hidden characters, resets KKPE bends on the penis it claims, and re-arms
  BetterPenetration for couples that were already linked.
- **A penis slightly off the canal's centre** keeps the womb reacting (at least 30mm).

### New in 1.1.4

- **Tessellated skin no longer defeats the window.** KKUTS_Tess and its family displace the skin in
  front of the x-ray stamp, so the window failed its depth test up close and only appeared zoomed
  out. The stamp is now pulled 10mm toward the camera on tessellated materials only.
- **Clothes ghost over the window** instead of being cut through, and loading a scene no longer
  makes garments see-through that were not. Remove a garment's copy in Material Editor and it stays
  opaque.
- **New render-queue order, opt-in.** `Ctrl+Shift+W` moves the character it is pressed on below
  Unity's 2500 line, where a draw still receives scene shadows - so the penis seen through the
  window is shaded rather than flat. Existing scenes keep the legacy order.
- **Scaled wombs react correctly**, and the womb's stencil pair and render queues are now saved
  with the scene.
- **A saved scene loads exactly as you left it.** Hand-tuned render queues, a womb's stencil pair,
  garment ghosts and material setups carried from another scene were all being changed on load.
  A load now writes only where nothing had been chosen; the hotkey is what changes a scene.
- **A bystander is not mistaken for the partner.** A male has to be within 15cm of the womb's aim
  point to be claimed, and the on-screen message tells you the distance it decided on.
- **Your own aim targets are respected** - a penis aimed at your own sphere is never re-aimed, and
  such aims now survive a save and load.

Version 1.1 added the Free-H support above (per-animation penis sizing from shipped measurement
tables with live learning, separate weak/strong loop fits, per-male canal width, mid-penetration
womb spawning), a womb that can be placed anywhere on the character rather than only in the vagina
(vaginal, anal, or reacting on its own wherever you put it), and a large Studio performance pass.
See `plugin/CHANGELOG.md`.

**BetterPenetration 5.1 or newer is required.** The two work-arounds CloXray used to carry (a
duplicate-constraint guard and a penis-FK fix on scene load) are fixed in BP itself and have been
removed. On an older BP the penis bend stays kinked after a scene load, and the log says so.

The finished build is on the [Releases](../../releases) page. This repository holds the plugin
and shader source; the womb studio item ships prebuilt (see below).

## Components

| Folder | What it is | Builds to |
|---|---|---|
| `plugin/` | The BepInEx plugin (`LiquidWobbleMPB`) that drives the liquid and the reactive canal. | `LiquidWobbleMPB.dll` |
| `shaders/` | The Unity (5.6) shader sources - x-ray body, organs, liquid. | `[Clo]XrayShaders.zipmod` |
| `docs/` | User manual and reference. | - |

The womb studio item (`[Clo]XrayWomb1.zipmod`) ships prebuilt on the [Releases](../../releases)
page - its mesh is derived from GFanon's (see Credits) and is assembled by a separate pipeline
maintained outside this repository.

## Install (for users)

Grab the zip for your game from the latest [Release](../../releases) - `KK_CloXray.zip` for
Koikatsu, `KKS_CloXray.zip` for Koikatsu Sunshine - and extract it into
the game folder. It carries the two zipmods (`mods/Clo/`), the plugin (`BepInEx/plugins/Clo/`),
the manual, and an example scene (KK zip).

See [docs/CloXray_Manual.md](docs/CloXray_Manual.md) for usage. The womb requires the plugin
(the liquid is hidden without it, by design).

## Build (for developers)

Plugin - needs a .NET toolchain plus the game/BepInEx reference DLLs from your own Koikatsu install.
Point the build at your install with `KKDir` (Koikatsu) / `KKSDir` (Sunshine):

```bash
dotnet build plugin/LiquidShaderWobble.KK/LiquidShaderWobble.KK.csproj -c Release -p:KKDir="D:\Games\Koikatsu"
```

Or set them once in a `Directory.Build.props` next to the solution (kept out of the repo).

Shaders - needs Unity 5.6.2f1 plus xukmi's KKShadersPlus includes (the three organ shaders
`#include` them - see [shaders/Assets/Shaders/Includes/README.md](shaders/Assets/Shaders/Includes/README.md)).
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
(Animal42069, GPL-3.0 - the penis bones it reads), RSkoi (the wobble plugin it builds on),
[xukmi/KKShadersPlus](https://github.com/xukmi/KKShadersPlus) (MIT - the shader base the organ
shaders extend), Minionsart (liquid-shader technique).
