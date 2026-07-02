# LiquidWobbleMPB

A BepInEx plugin for **Koikatsu (KK)** and **Koikatsu Sunshine (KKS)** Studio that drives
[RSkoi/LiquidShader](https://github.com/RSkoi/LiquidShader)-style liquid materials from an
object's own **motion, scale, and orientation** — so liquid sways, tracks Studio scaling, and
stays level to gravity. Everything is fed to the shader through a single renderer-wide
`MaterialPropertyBlock`, so it works in **any material slot** (or several at once) with **no
performance cost**.

Requires [ComponentUtil](https://github.com/RSkoi/ComponentUtil) to add the component in Studio.

## What it does

Each frame (`LateUpdate`) the component writes, on one shared `MaterialPropertyBlock`:

- **Wobble** — `_RotationX` / `_RotationZ`: a damped, oscillating sway derived from how the
  object moved this frame. The liquid jiggles when you drag, swing, or animate the object and
  settles smoothly back to rest.
- **Per-axis scale** — `_ObjectScaleVec` (float4): the live rest→world scale, so the liquid's
  fill/cap snap tracks Studio scaling and stays crack-free under blendshapes. Supports
  **non-uniform (stretched) scale**.
- **World-up** — `_RestWorldUp` (float3): world-up expressed in the mesh's rest frame, so the
  shader can keep the liquid surface **horizontal to gravity** even when the object is rotated
  or tilted, while still doing its fill math in rest space (crack-free).

## Features

- **Any material slot** — uses a renderer-wide `MaterialPropertyBlock`, so the liquid material
  is driven no matter which slot it sits in (slot 0, slot 2, several at once). Not limited to
  slot 0 like a `material.SetFloat` approach.
- **No performance harm** — the property block is created once and reused; the work per frame is
  one `GetPropertyBlock` → set → `SetPropertyBlock` on a cached renderer. It does **not**
  instantiate materials (so it doesn't break batching or duplicate material assets) and produces
  **no per-frame garbage**. The override is renderer-wide but each material only reads the
  uniforms it declares, so non-liquid slots (organ/glass/interior) simply ignore it.
- **Smart renderer resolution (done once, at load)** — attach the component directly to the
  renderer *or* to a parent (e.g. an asset root). At `Start` it finds a renderer on itself, and
  if there isn't one it searches children and picks the renderer whose material actually declares
  the wobble property — so it targets the liquid mesh and not a sibling mesh. This is a one-time
  cost; the per-frame path never searches.
- **Shader-agnostic** — every shader property name is a configurable field, so it drives any
  liquid shader that exposes the relevant uniforms, not a single hard-coded one.
- **Safe defaults / graceful** — skips a write when scale is degenerate (mid-load, disabled,
  reparenting) so it never feeds the `0 = auto` sentinel; no-ops cleanly if no renderer is found.
- **Coexists with everything** — distinct plugin GUID, name, namespace, and class. It runs
  alongside RSkoi's original wobble plugin (and any other plugin) without conflict. Because it
  writes via an MPB, it overrides `material.SetFloat`-based drivers automatically.
- **Cross-game, one bundle** — KK and KKS builds share a single assembly name
  (`LiquidWobbleMPB`), so one baked mod bundle resolves in both games (install the matching DLL
  per game).

## Compatibility

- Works with any **RSkoi/LiquidShader**-style liquid shader.
- Works with the **CloLiquid** shader (`CloXray/Liquid`) and the **Clo Xray Womb** mod — drives
  the womb's liquid wobble, per-axis scale snap, and gravity-leveled surface. The same component
  works on free-standing items too (bottles, glasses, pools): give the item's material the liquid
  shader, then add the component.
- KK (net35) and KKS (net46). Install the matching `LiquidWobbleMPB.dll` for your game into
  `BepInEx/plugins`.

## How to use (in Studio)

0. Add an object / item to the workspace.
1. Set the object's material to a liquid shader (e.g. `CloXray/Liquid` or RSkoi/LiquidShader) in
   MaterialEditor.
2. Open **ComponentAdder** (ComponentUtil) on the object and add **`LiquidWobbleMPBEffect`**.
   (Ignore the `...Plugin` entries — those are BepInEx bootstrap classes, not components.)
3. Tune the properties to taste — most relevant are `MaxWobble`, `WobbleSpeed`, and `Recovery`.

### Properties

| Property | Default | Purpose |
|---|---|---|
| `ShaderRotXPropName` | `_RotationX` | Wobble X uniform name |
| `ShaderRotZPropName` | `_RotationZ` | Wobble Z uniform name |
| `MaxWobble` | `0.03` | Maximum sway amplitude |
| `WobbleSpeed` | `1` | Oscillation speed |
| `Recovery` | `1` | How fast the sway settles back to rest |
| `ShaderScalePropName` | `_ObjectScaleVec` | Per-axis scale uniform name |
| `ScaleMultiplier` | `1` | Calibrates rigs that bake a non-1 base scale |
| `ShaderRestWorldUpPropName` | `_RestWorldUp` | World-up (rest frame) uniform name |

Leave any shader-property name blank to disable that particular feed. If the shader doesn't
declare a uniform, the write is harmless (the shader just doesn't read it).

## Credits

Inspired by **RSkoi**'s work:

- [LiquidShaderWobble](https://github.com/RSkoi/LiquidShaderWobble) — the original wobble plugin that inspired this one.
- [LiquidShader](https://github.com/RSkoi/LiquidShader) — the liquid shader family this plugin drives.
- [ComponentUtil](https://github.com/RSkoi/ComponentUtil) — required to add the component in Studio.
