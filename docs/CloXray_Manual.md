# CloXray — Manual

An x-ray womb for Koikatsu CharaStudio: see a womb fill with liquid and react to penetration, *through* the character's body.

---

## TL;DR
Add the womb → drop it into the female's vagina (leave position & rotation at default so it sits inside her) → select it and press `Shift+Alt+X` → bam, x-ray ready.

That one keypress makes her body see-through over the womb, starts the liquid, and — automatically — x-rays any inserted penis and aims it straight down the canal. You don't need to select the male.

---

## Quick start
1. Install the mod + prerequisites (see [Requirements](#requirements)).
2. In CharaStudio, load your female character as usual.
3. **Add → Clo → Xray → "Xray Womb 1".**
4. Place it in her vagina and leave its **position and rotation at default** — it's built to sit correctly there.
5. Select the womb, press `Shift+Alt+X`. Her body turns see-through over the womb, and any penis inside is x-rayed and aimed down the centre of the canal — x-ray ready.
6. Tune it in **Material Editor → Liquid** (Fill Amount, colour, wobble…).

On a bottle / other item: apply the `CloXray/Liquid` shader to a liquid mesh copy, select it, press the hotkey to add wobble.

---

## Features

### X-ray see-through
- See the womb and the liquid inside it through the body.
- See a penis (or an item inside the womb) through the body too.
- One hotkey (`Shift+Alt+X`) sets it all up: the body goes see-through, the liquid wobble attaches to the selected item, and every inserted penis is x-rayed and aimed down the canal — automatically, no need to pick the male.
- Re-applies itself when you swap/replace the character — no need to redo it.
- Visual toggles: out-of-body visibility, behind-body visibility, x-ray outline, occlusion (props/clothes/walls hide it), see-up-the-canal window, see-through-clothes, skin transparency.

### X-ray machine plane
- Drop a plane in front of the character and turn it into an x-ray screen: a black backdrop with a white silhouette outline and the character's revealed organs showing *through* it — like a medical viewer or an airport scanner.
- Windowed reveal (advanced): show an extra organ (e.g. a stomach) *only* where the plane covers it, while the womb stays visible everywhere. Slide the plane over the belly and the stomach appears; move it away and it's hidden.
- The revealed organ can be made semi-transparent over the screen, and you can put cum inside it that only shows through the plane too.

### The liquid
- Fill amount from empty to full; stays level when you rotate or tilt.
- Fill level stays consistent at any womb / character scale — a half-full womb reads half-full whether you shrink or enlarge it.
- One or two chambers.
  - Connected (default): the womb bulb and the canal share a single liquid level — fill flows between them and settles like one vessel.
  - Closed: the bulb and the canal are independent — the canal can hold (or not hold) liquid separately from the bulb. Use this when you want cum sitting in the canal at a different level than the womb.
- Drain the canal from either end. In Closed mode, *Fill Amount Chamber 2* sets the canal's top level and *Fill Bottom Clear C2* empties it from the bottom up — so the cum can be a floating band, or trail a withdrawing penis from top to bottom.
- Look: colour & transparency, wet gloss, edge glow, rounded surface.

### Wobble & slosh
- Sloshes when you move it; sloshes with each thrust; subtle idle jiggle. Tunable strength/speed/settle.

### Shape sliders (KKPE)
- Extra control sliders live in KKPE / StudioBlendShapes (not Material Editor — that's only for shader/material look). They save with the scene.
- Pose the canal and womb by hand: open/close each canal ring (entrance → cervix), narrow or widen the whole canal, a serpentine *skew*, *stretch*, a cervix *poke*, and a *pregnant* swell.
- Hide / remove ovaries & tubes — a slider that shrinks the ovary-and-tube arms away if you don't want them on screen.
- Reaction tuning: *BP Strength* scales how hard the womb reacts to penetration — set it to 0 to switch the auto-reaction off entirely and pose the canal yourself; *BP Dampening* sets how fast the canal closes back up after a thrust.

### Womb reaction to penetration
- The canal opens with depth (entrance → cervix), widens with girth, stretches on deep thrusts, and the mouth leans toward the penis (forward/back and left/right).
- Reacts whenever a BetterPenetration penis is inserted in the woman the womb sits on — robust to a long or BP-squished penis and to a womb placed slightly off the exact canal line (it keys off insertion into *that woman's vagina*, not just the narrow tube; a penis that isn't in that woman won't open it). Also reacts to a collider you push in (a toy/bottle carrying a DynamicBone collider); with both present, it follows whichever is deeper.
- Works for a posed (static) penis, not just active thrusting. Withdraw the penis — or pull its tip away via a constraint/sphere on `k_f_dan_end` — and the womb closes (tunable: **F1 → WombExpand → Penis tip detach distance**).
- The deep dome (top of the womb, the `Vagina_6_entrance_open` shape) is manual — open it by hand with its KKPE slider; the auto reaction drives the entrance→cervix rings (V1–V5) only.
- Turn collider reaction off if you don't want it: globally via **BepInEx config (F1) → WombExpand → React to colliders**, or per-womb by setting the `BP_IgnoreColliders` shape (KKPE / StudioBlendShapes) above 50 — that one womb then ignores colliders and only reacts to a BP penis.

### Smooth penis bend (BetterPenetration fix)
- Fixes a long-standing BetterPenetration annoyance: on scene load the penis bent with a kink — a few segments stayed straight ("stuck") instead of following the curve — until you toggled the character's FK off then on by hand.
- CloXray now does that for you: when BP is driving a penis, the affected FK nodes are released to BP on load and kept released, so the penis bends smoothly from the first frame. No manual FK toggle.
- Only touches a BP-driven penis — one you've hand-posed with FK is left alone.

### No duplicate aim markers (BetterPenetration fix)
- Fixes a second BetterPenetration bug: BP re-adds a duplicate `k_f_dan_end` tip marker every scene load, so each reload stacks another — the markers multiply and anything bound to them breaks (the penis aiming, and your own constraint spheres attached to the tip).
- CloXray suppresses the duplicate, so there's always exactly one `k_f_dan_end` — your aiming and tip-spheres stay attached and keep working across reloads.

### Bottles & other items
- Put the liquid in any item (e.g. a bottle); a cap closes an open bottle mouth; wobble works there too. Saves with the scene.

### Controls
- **Material Editor** — look, fill, chamber mode, wobble setup. **KKPE / StudioBlendShapes** — the shape sliders above. **BepInEx config (F1)** — tuning, the hotkey, ranges. **Scene save/load** keeps wobble + constraints + slider values.

---

## Requirements
Just the latest HF patch, or:
**Required:** BepInEx + Sideloader (KK), KKAPI, MaterialEditor.
**Optional (per feature):**
- BetterPenetration — womb reaction to a penis + the penis bones (the body x-ray works without it).
- NodesConstraints (Joan6694) + KK_AdditionalFKNodes — for aiming the penis at the womb.

---

## Technical details
*(Quirks, internals, and the "why isn't X working" answers. Skip unless you're tuning or troubleshooting.)*

**The hotkey (`Shift+Alt+X`, rebindable under *AutoBodyReveal → Apply Now Hotkey*) does several things in one press — automatically, for every womb in the scene:**
1. **Body x-ray** — stamps the character each womb sits inside (within *AutoBodyReveal → Womb-in-vagina range*, default 0.15 m) so the womb shows through. Proximity-based, so it re-applies automatically on character swap. Works with or without BetterPenetration: it matches the female by the BP bone `cf_J_Vagina_root`, falling back to the vanilla `cf_j_kokan` crotch bone if no character has it (the womb item's own `cf_j_kokan` is excluded).
2. **Wobble** — attaches the liquid-wobble effect to the **selected** item's `CloXray/Liquid` renderer.
3. **Penis x-ray** — for each womb it finds the penetrating male automatically (the nearest `cm_m_dankon` penis that is **not** the womb's own female), and converts his `cm_m_dankon` material to `CloXray/OrgInside`, with the **stencil pair matched to that womb** so it shows through *that* body.
4. **Penis aiming** — two `NodesConstraints` position links: `k_f_dan_entry` → the female's `cf_J_Vagina_root`, and `k_f_dan_end` → the womb's `penis_target` bone.

**Automatic, not selection-based.** Both partners can carry a `cm_m_dankon` penis and the `k_f_dan` FK bones (KK_AdditionalFKNodes adds them to everyone), so the plugin pairs each womb with the nearest male that isn't its own female — you don't pick the male. A penis only claims a womb if its tip is within ~0.5 m of it, so in a multi-couple scene each womb wires to its own partner, and dan nodes you've already targeted by hand are left untouched.

**`penis_target` is a centred aim bone.** It's a dedicated, empty leaf bone baked onto the canal centreline (nothing is skinned to it), so the penis points straight down the middle of the tube — and because it's a leaf, aiming it can never drag the womb. The womb itself stays exactly where you placed it.

**Constraints only *drive* when the penis FK bones are active Studio GuideObjects** — i.e. the penis FK nodes must be enabled via KK_AdditionalFKNodes (same requirement as BetterPenetration). `AddConstraint` is idempotent and the plugin also dedups by the penis bone.

**Stencil pairs (multiple couples).** The x-ray separates up to 4 characters by stencil pair. Set a womb's `StencilBody` to put a couple on its own pair; the hotkey matches that character's body / penis / clothes to it:

| Pair | Body ref (`StencilBody` / `StencilRef`) | Organ ref (`StencilBody_Plus_1`) |
|------|------|------|
| A (default) | 4 | 5 |
| B | 8 | 9 |
| C | 12 | 13 |
| D | 16 | 17 |

**OrgInside sliders (Material Editor).** The hotkey converts a penis's `cm_m_dankon` material (and the womb interior wall uses this shader too) to `CloXray/OrgInside` — the shader for anything crossing the organ/body boundary. Most of its Material Editor sliders are standard skin/item look controls (textures, ramps, shadow/specular) — same as KK's skin shader; only the CloXray-category ones change x-ray behaviour:
- **Bottom Window** (`BottomWindow`, On/Off, default On) — opens the x-ray see-through window for rays looking up the open canal from below. This is the Features list's "see-up-the-canal window." When the canal is opened/penetrated, rays from underneath travel up the canal hitting pixels the womb exterior never covered, so its depth-clear never ran there — the body skin's depth would normally occlude that view. On wipes the body depth to FAR and promotes those pixels into the organ region (exactly what the exterior would have done), so you can see *up inside* the opened canal from underneath instead of the womb exterior occluding it. Off leaves those pixels as plain body, so the exterior/skin covers the view (the old behaviour). Pixels the exterior already covered are unaffected either way.
- **Alpha** (`Alpha`, CloXray category) — overall opacity of the inside-organ render; multiplies the final colour's alpha. 1 = solid, lower = more translucent.
- **StencilBody / StencilBody_Plus_1** — the stencil pair that ties this material to one couple's body, matching the Stencil pairs table above (`StencilBody` = the body ref, `StencilBody_Plus_1` = the organ ref; e.g. pair A = 4 / 5). The hotkey sets these to match the womb the penis is paired with; only change them by hand for multi-couple setups.
- **OutsideOfBodyAlpha** (`OutsideOfBodyAlpha`) — how the part of the object that is outside any body renders. 1 = fully visible (default), 0 = invisible. Colour only; doesn't change depth/stencil.
- **CullOption** (`CullOption`) — face culling (Off / Front / Back; default Back, inward-facing interior normals). The Bottom Window pass shares this cull so its coverage stays a subset of what the lit pass paints — leave it on Back for the womb interior.
- **AlphaOptionZWrite (ZWrite)** — whether this material writes depth. On by default; part of the render-order machinery, not a look control.

**X-ray machine plane (`CloXray/XrayMachine`) — the x-ray screen + windowed reveal.**
Apply `CloXray/XrayMachine` to a plane item. At its defaults (`StencilOrgan` = 0, `StencilOrganMask` = 3, render queue 3550) it paints a black backdrop + white silhouette outline over everything the plane covers, but skips revealed-organ pixels — so any organ that's already x-ray-revealed (the womb, a penis inside it) shows through the screen while the body reads as a flat silhouette. On its own that's just the "x-ray viewer" look; it doesn't change *what* is revealed, only draws the backdrop behind it.

**Windowed reveal — one plane reveals an extra organ, womb stays always-on.** To reveal a *second* organ (say a stomach) only under the plane while the default womb stays see-through everywhere, three materials cooperate through stencil bit 7 (value 128) — a free bit nothing else uses:

| Material | Shader | Settings |
|---|---|---|
| **Extra organ** (e.g. stomach mesh) | `CloXray/Organ` | `StencilBody` = **132**, `StencilBody_Plus_1` = **133**, `StencilReadMask` = **159** |
| **Bit-stamp plane** (big, covers the organ) | `CloXray/BodyReveal` | `StencilRef` = **128**, `StencilWriteMask` = **128**, `StampZTest` = **8** (Always), **Render Queue = 3490** |
| **Screen plane** | `CloXray/XrayMachine` | defaults (above) |

- The bit-stamp plane writes *only* bit 7 wherever it covers the screen. `StampZTest = 8` (Always) makes it stamp regardless of the plane's depth vs the body — otherwise a plane sitting behind the protruding belly wouldn't tag it. **It must face the camera** (`BodyReveal` culls back faces; a back-facing plane stamps nothing). Its **Render Queue must be 3490** — after the body's own reveal (2500) but before organs (3500); left at the default 2500 the body's whole-byte stamp wipes bit 7 and nothing reveals.
- The extra organ gates on region-4 and bit 7 (that's what `132` / `133` / `159` = `4|128` / `5|128` / `31|128` encode), so it reveals *only* where the plane stamped. The womb keeps its defaults (`4` / `5` / `31`), ignores bit 7, and stays revealed everywhere.
- Put the bit-stamp and the screen on two stacked planes at the same spot, or two material slots on one plane.

**Organ semi-transparency over the screen (`XrayAlphaToBlack`).** Lowering an organ's `Alpha` normally blends it toward the body skin behind it (the black screen draws *after* the organ), so it washes out to pale/white. Turn **`XrayAlphaToBlack` = 1** on the organ's `CloXray/Organ` material and `Alpha` instead fades the organ toward black — which over the black x-ray screen reads as true see-through. `Alpha = 1` looks unchanged; the toggle only bites as you lower Alpha. Leave it off for the normal in-body look, where blending with the skin is what you want.

**Cum inside a plane-gated organ (`CumStencilRef` / `CumStencilReadMask`).** Put a `CloXray/Liquid` cum mesh inside the extra organ (non-rest mode, like a bottle liquid). By default the cum draws wherever its mesh is; to make it appear only through the plane (hidden when the plane is off, matching the organ) set **`CumStencilRef` = 192** and **`CumStencilReadMask` = 128** on the cum material — it then marks itself only where the plane set bit 7. The womb's own cum stays at the defaults (`64` / `0` = always drawn) and is untouched.

**Multi-organ cutaway (several organs through one body — stomach, intestines, bladder…).** Apply `CloXray/Organ` at the default `StencilBody = 4` / `StencilBody_Plus_1 = 5` to *each* organ mesh. They all reveal through the body's existing region-4 BodyReveal — one body stamp reveals every organ sitting behind it, so a full reproductive-tract / abdominal cutaway is just several organ meshes placed inside her. This works cleanly for organs at **different heights that don't overlap on screen** (womb low, stomach high, etc.). Notes:
- **Overlap limit:** a body pixel carries only one region, and two organs sharing the default base both write region 5, so where they *overlap on screen* the second won't clear behind the first. For overlapping organs that must both show, put the extra one on its own always-on class — a free region base like `20/21`, `24/25`, or `28/29` (`StencilBody`/`_Plus_1`) with its own BodyReveal quad stamping that base over the same area — or gate one of them to a plane (below).
- **Windowed per-organ:** any organ in the set can be made plane-gated instead of always-on by using the bit7 windowing values (`132/133/159`) from the table above, so e.g. the womb stays always-on while the stomach only appears under the x-ray plane.
- **Interior/contents:** for things *inside* an organ (swallowed props, a fetus, food) use `CloXray/OrgInside` matched to that organ's pair — it reads through the belly the same way the canal wall does; or fill the organ with a `CloXray/Liquid` gated to its reveal bit.

**Chamber modes (`ChamberMode_0single_1connected_2closed`).** A single float on the `CloXray/Liquid` material picks how the womb bulb and the canal share liquid:
- **`1` Connected (default).** Two boxes, one world-horizontal level: `FillAmount` fills the lower chamber first and redistributes across both as you tilt — they behave as one connected vessel.
- **`2` Closed.** Two independent levels: chamber 1 (bulb) uses `FillAmount`, chamber 2 (canal) uses `FillAmount2`; no flow between them. Use this when the canal should hold liquid separately from the bulb.
- **`0` Single.** One box, one fill (`FillAmount`); the chamber split is ignored. (The womb ships in Connected mode; Single is mostly for non-womb items.)

**Canal cum band (Closed mode).** In Closed (2) the canal (chamber 2) has two edges: `FillAmount2` (the canal top level) and `FillBottom2` (*Fill Bottom Clear C2* — drains the canal from the floor up). Animate both to fill behind a withdrawing penis, then drip away — the cum can be a floating band or a trail. `FillBottom2 = 0` is identical to a normal fill from the bottom. Defaults: `FillAmount` / `FillAmount2` = 0.2, `FillBottom2` = 0.

**Fill level is scale-invariant.** The fill % doesn't change when you scale the womb item or the character — a half-full womb stays half-full at any scale. The cum is a CPU-skinned `SkinnedMeshRenderer`: its `unity_ObjectToWorld` is identity (all scale lives in the bones), so the shader can't read a Studio transform and runs all fill math in the mesh's REST space (rest positions baked into UV2, mapped to world by a single per-draw uniform). Rest-space math means rescaling the rig leaves the fill fraction untouched. The plugin supplies the live per-axis scale + world-up only to keep the surface horizontal and the cap crack-free — it does not feed the transform into the fill amount.

**Control-slider blendshapes (KKPE / StudioBlendShapes).** The womb exposes per-object control shapes through **KKPE / StudioBlendShapes** — *not* Material Editor (Material Editor only surfaces shader material properties). They're real blendshape channels on the `o_uterus` mesh, so they're per-womb and save with the scene. Deform shapes: `Vagina_1_open` … `Vagina_4_upper` (canal rings, entrance→upper), `Vagina_5_entrance_open` / `Vagina_5_entrance_closed` (cervical os), `Vagina_narrowall` (whole-canal radial collapse, default 0 = canal open), `Vagina_widenall`, `Vagina_skew` (serpentine bend), `poke`, `Vagina_stretch`, `pregnant`, `mounddown` (default 50), and an **ovary-shrink / remove-ovaries** shape that contracts the ovary+tube arms (default 10; exact channel name may change). Three inert control channels are read (not deformed) by the plugin: `BP_Strength` (weight ÷ 50 → reaction gain; default 50 = 1.0×, 0 hands the womb reaction off to manual/KKPE so it won't auto-expand), `BP_Dampening` (weight ÷ 100 → ring close time in seconds; default 15 = 0.15 s), and `BP_IgnoreColliders` (default 0; set it above 50 to make THIS womb ignore pushed-in colliders — it then only reacts to a BP penis). Defaults are baked into the SMR's blendshape weights.

**Smooth penis bend on load (the BetterPenetration FK fix).** BetterPenetration aims the whole penis (`cm_J_dan*`) chain every frame, but KK_AdditionalFKNodes only registers Studio FK nodes for a *subset* of the shaft bones — `cm_J_dan103/105/107` plus the foreskin `119`. On scene load Studio re-applies the scene's saved FK state, re-enabling FK on exactly those nodes, and FK (writing last) pins them straight while BP bends everything else — the kinked, "some segments stuck" bend. That's also *why* only some bones stick: only those bones *have* an FK node; the rest are always BP-driven. The manual workaround was toggling the character's FK off→on. CloXray automates the equivalent: it clears just those nodes' **per-bone FK enable** (`TargetInfo.enable`) and deactivates their guides, so BP owns the chain — without touching the rest of the body's FK (the penis nodes share the Body bone group, so a group-level toggle would disturb the whole skeleton; the fix is deliberately per-bone). A Harmony postfix on `OCIChar.ActiveFK` re-applies it every time FK is re-enabled (the load re-apply, a manual FK-panel toggle, KKPE's IK pass), so the bend can't get re-pinned. It acts only on a male with an active BP penis (`danTargetsValid`); a hand-FK-posed, non-BP penis is never touched.

**No duplicate `k_f_dan_end` (the BetterPenetration dan-readd fix).** BetterPenetration re-binds its dan target bones by name on every `CharacterReloaded`, *without* checking whether they already exist (it isn't Auto-Target gated), so it appends a second `k_f_dan_end` (and siblings) on each scene/character load — duplicates that pile up and break by-name lookups, the aiming constraints, and any sphere/constraint you bound to the marker. CloXray installs a Harmony prefix (`BPDanReaddGuard`) that skips the re-add when the bone is already present, so exactly one marker survives. It's installed lazily on `CharacterReloaded` (not at `Awake` — BP's Core assembly loads late, so an early hook would no-op) and is a no-op when BP isn't present.

**Reaction to colliders (toys / items).** Besides a BP penis, the womb reacts to a DynamicBone collider pushed into the canal — e.g. a toy or bottle you add a KKPE collider to. It runs alongside the penis path and the womb follows whichever reaches deeper, so a toy works even with a penis also present. Tuning lives under **BepInEx config (F1) → WombExpand**:
- **React to colliders** (default on) — the global switch; turn it off to disable collider reaction for every womb.
- **Collider name filter** (default `Collider`) — only colliders whose GameObject name *starts with* this drive the womb. `Collider` is the name KKPE gives a collider you add, so by default the womb reacts to your collider and ignores the character's body colliders (`KK_Colliders_…`) and the penis (handled separately). Set it empty to instead auto-detect any small in-canal collider by size/position. The KKPE `[J694]`-style label is not the object name — turn on Debug Log to see the real names (the `DynamicBoneColliders` / `COLLIDER-DIAG` lines).
- **Collider in-canal width** (default 0.045 m) — how close the collider's tip must be to the canal axis to count as inserted (lower = stricter). **Collider pair range** / **Collider max radius** bound which colliders are considered.
- To stop just one womb from reacting to colliders (while others still do), use the per-womb `BP_IgnoreColliders` blendshape (set > 50) instead of the global switch.

**Bottle liquid layering.** Make a liquid mesh copy, inset it slightly (~0.85–0.9) so it doesn't z-fight the glass, and set the liquid material's render queue just below the glass so the glass composites over it.

**Cum z-fighting fix (`DepthOffset`).** When a `CloXray/Liquid` cum sits inside a *coincident* surface — another Liquid item it fills, or an `Organ` you added it to as a second material — the two surfaces can land on the same depth and flicker / z-fight along their shared boundary. The `DepthOffset` slider (Material Editor → Liquid) applies a polygon depth-offset to the cum so it stops fighting: set the cum's `DepthOffset` to about −2 to pull it in front of the host surface (leave the host at `0`). If the cum hides *behind* instead, flip the sign to +2; if it still flickers, raise the magnitude (−4, −6). `0` = off (default). This is the shader-side alternative to the geometric inset trick above.

---

## Slider reference (Material Editor)

Every CloXray-specific slider, per shader. Sliders already covered in depth above (chamber modes, stencil pairs, the windowed reveal, `DepthOffset`, `BottomWindow`) get one line and a pointer. On `CloXray/Organ` and `CloXray/OrgInside`, the many sliders not listed here are the stock KK skin/item look controls (textures, ramps, specular…), unchanged. The shape sliders live in KKPE, not here — see "Control-slider blendshapes" above.

### CloXray/Liquid — look
- `Color` — master tint; the color's alpha is the liquid's transparency. The first slider to touch for a different cum look. Note the liquid paints once per pixel: you never see rear layers of the same liquid through the front, whatever the alpha.
- `MainTex` — optional pattern texture, multiplied with `Color` (default plain white). Strong patterns can smear on the flat top surface — it shares the wall UVs.
- `Matcap`, `MatcapAlpha` — the sphere image that supplies the wet-gloss highlight, and its strength. It adds on top of the base color, so past ~0.3 the goo washes toward white; the default 0.05 is a subtle sheen.
- `Sh` — sheen darkness: darkens the goo's body while the bright rim stays, so the liquid reads thick and deep. Coupled to fresnel — at `FresnelAlpha` 0 the full darkening applies everywhere.
- `FresnelPower` — how tightly the edge glow hugs the outline (higher = thinner rim).
- `FresnelScale` — rim brightness. Useful range is roughly 0–5 despite the slider maximum; large values just turn the whole surface uniformly milky.
- `FresnelBias` — a small uniform lift, independent of viewing angle. Near zero by default.
- `FresnelAlpha` — master strength for the whole fresnel effect; 0 turns the rim off — and removes its counterweight to `Sh`, so if the goo suddenly looks dark, check these two together.
- `EmissionColor`, `EmissionStrength` — self-glow added on top of the final color; strength 0 = off. This is the cum-glow feature.

### CloXray/Liquid — physics
- `FillAmount`, `ChamberMode_0single_1connected_2closed`, `FillAmount2`, `FillBottom2` — see "Chamber modes" and "Canal cum band" above.
- `VolumeConserve_0off_1cube_2ellipsoid` — how the fill % maps to a surface height. 0 and 1 are the same linear mapping (right for box-like vessels); 2 treats the vessel as rounded, so an animated pour looks volumetrically honest in a round flask or the womb bulb. Empty, half and full look identical in every mode — the difference is the in-between fills.
- `CumStencilRef`, `CumStencilReadMask` — see "Cum inside a plane-gated organ" above.
- `DepthOffset` — see "Cum z-fighting fix" above.

### CloXray/Liquid — wobble
The idle pair runs in the shader itself; the rest are setup values the plugin reads from the material about twice a second — they need the wobble driver attached (the womb gets it automatically, a bottle needs the hotkey) and take about half a second to react after you edit them.
- `IdleWobbleSpeed`, `IdleWobbleStrength` — automatic gentle sway of the surface, even in a perfectly still scene. Both must be above 0 to see anything. It never settles — set strength back to 0 for still renders. Works even without the wobble driver.
- `MaxWobble` — motion-slosh sensitivity and ceiling in one: every jolt is scaled by it and capped at it. 0 = motion slosh off.
- `WobbleSpeed` — how fast the surface rocks after a jolt: low = heavy syrup, high = water.
- `Recovery` — how quickly the slosh dies down. The floor is 0.05 on purpose — near-zero decay would let the sway build up without limit.
- `ThrustSlosh_0off_1global_2perChamber` — slosh with each BetterPenetration thrust, scaled by penetration speed. 1 jolts the liquid as one; 2 lets the bulb and the canal slosh independently, with the jolt going to whichever chamber the tip is near — the showpiece womb setting. When the tip pose isn't available, 2 quietly behaves like 1.
- `ThrustSloshGain` — thrust-to-slosh volume knob. Each jolt is still capped by `MaxWobble`, so past a point raise that instead.

### CloXray/Liquid — cap
- `EdgeRadius` — rounds the edge where the surface meets the wall into a meniscus-like roll; 0 = perfectly flat, sharp fill line. Applies in Closed mode and to the Connected bulb; the connected canal and Single-mode bottles stay flat by design.
- `CapDome` — shading-only doming of the top surface, so it catches light like a slight bulge. The geometry stays flat — no bulge in profile view.
- `CapTess` — how finely the strip around the fill surface is subdivided; higher = smoother rounded rim, 1 = off. Needs DX11.
- `CapForOpenMesh` — for open-mouthed vessels: builds the missing top surface as a disc so you don't look into a hollow shell. Leave off for closed meshes, and center the bounds box on the vessel first.

### CloXray/Liquid — physics box setup
For custom items only — the womb ships with baked values; don't move them there.
- `ShowSetupPhysicsBounds` — setup overlay: a yellow wireframe box per chamber, plus yellow lines where its planes slice the mesh. Fit the box to the liquid interior — bottom plane where fill 0 should sit, top plane at the full line, side planes at the inner walls (they set the tilt pivot and the ellipsoid proportions) — then switch it off. On the womb the box shown is live-measured, so the sliders won't move it; that's expected.
- `Bound1MinY_bottom` … `Bound6MaxZ_front` — chamber 1's invisible physics box: fill 0 = bottom plane, fill 1 = top plane, plus the wobble pivot and volume proportions. The bottom plane also doubles as the divider deciding which vertices belong to chamber 2.
- `C2Bound1MinY_bottom` … `C2Bound6MaxZ_front` — the same box for chamber 2 (its fill range, pivot, and volume share in Connected mode). Note chamber membership comes from chamber 1's bottom plane, not from this box.

### CloXray/Organ — the revealed organ
- `Alpha` — fades the organ into the skin painted behind it (it sinks under the skin and washes pale — it does not reveal the scene behind her). For the x-ray-screen look pair it with `XrayAlphaToBlack` (above). 0 = fully gone, no ghost masking left behind.
- `Brightness` — plain brightness dial, up to 3×. The knob to reach for when the organ reads too dim through the skin — it leaves transparency alone.
- `OutsideOfBodyAlpha` — visibility of the parts outside the body's outline. The shader default is 0, so a freshly applied Organ material is invisible in open air — if your custom organ "vanished", raise this to 1. The shipped womb is baked at 1.
- `XrayOutlineWidth` — a highlight rim around the organ's silhouette, painted on the surrounding skin. 0 = off; try 0.002–0.005.
- `XrayOutlineColor` — the rim's color; its alpha is the rim's opacity, kept exact even where mesh layers overlap.
- `XrayOutlineColorBlend` — 0 = pure outline color; 1 = the rim tinted by the organ's own color.
- `XrayOutlineCull` — 0 = solid full rim (default), 1 = the thinnest clean ring, 2 = rarely useful. Whole numbers only.
- `XrayOutlineExtrusionMode` — 0: width in meters, thins as the camera pulls back; 1: constant on-screen width, also closes gaps at sharp corners. Re-tune the width after switching — the number means different things.
- `StencilBody`, `StencilBody_Plus_1`, `StencilReadMask` — pair and window plumbing; see the stencil-pairs table and the windowed-reveal recipe above. Leave `StencilReadMask` at 31 unless following that recipe — other values silently hide the organ in-body.
- `XrayAlphaToBlack` — see "Organ semi-transparency over the screen" above.

### CloXray/OrgInside — extras
The main sliders are documented in "OrgInside sliders" above. Two more:
- `BehindBodyAlpha` — ghost-through-skin opacity for the parts inside the body but outside the x-ray window (the shaft below the womb). 0 = classic hidden; try 0.2–0.4 for a faint full-length ghost. High values paint over the skin rather than sort behind anything.
- `XrayOutlineWidth`, `XrayOutlineColor` — a flat colored overlay marking where the object hides behind the body; it reads as a solid scanner-style silhouette of the hidden length, not a thin ring. Width in meters, 0 = off. Translucent colors stack where the shell overlaps itself.

### CloXray/BodyReveal — the body stamp
- `StencilRef` — the character's x-ray ID; must equal the womb Organ's `StencilBody` (4/8/12/16 — see the pairs table). The hotkey sets and re-syncs it; scene load never overwrites a saved value.
- `StencilWriteMask` — 255 on a body. 128 only on a windowed-reveal stamp plane.
- `StampZTest` — 4 (LEqual) on a body; 8 (Always) only on a stamp plane — without it the window fails wherever the belly protrudes closer than the plane. Other values aren't useful.

### CloXray/BodyRevealExtra — the skin veil
The second body copy the hotkey applies (config "Also apply skin veil", default on). It re-draws the character's own lit skin over the whole x-ray window, after the womb stack — so one slider fades the entire in-body view like a master opacity.
- `XrayAlpha` — the master x-ray strength: 0 = the window is fully covered by opaque skin (looks exactly like "the mod stopped working" — the first thing to check), 1 = raw full-brightness x-ray. The plugin sets it only when the veil is first created; re-pressing the hotkey deliberately never resets your tweak, so a scene saved at 0 stays at 0 until you move it.
- `StencilComp` — 4 = the veil covers womb and penis alike, one uniform fade; 3 = the penis is skipped and stays crisp while the womb dims under skin. Use exactly 3 or 4 — in-between values are undefined, not a mix.
- `StencilBody_Plus_1` — pair plumbing: the womb Organ's `StencilBody_Plus_1` (5/9/13/17). A wrong value makes `XrayAlpha` appear dead; re-press the hotkey to auto-resync rather than hunting by hand.

### CloXray/AddXrayToMaterialCopy — see-through for any object
A manual tool, not applied by the plugin. In Material Editor: copy the target's material, set the copy's shader to this, and set the copy's render queue lower than the original's. The copy then hides a dithered share of the original's pixels, so whatever rendered before it shows through — e.g. peel a garment down to the body by putting the copy's queue after the body's but before the garment's.
- `Alpha` — the strength: 0 = no effect, 1 = the original fully hidden, in between = a fine checker dither (visible up close; expected). If nothing happens at all, the copy's queue isn't below the original's.

### CloXray/XrayMachine — the screen plane
- `BackgroundColor` — the screen's fill color (classic black). Its alpha does nothing — the screen is always opaque; only revealed organs show through it.
- `OutlineColor` — the silhouette lines' color; alpha likewise ignored. To hide the lines entirely, set it equal to the background color.
- `OutlineWidth` — line thickness, in centimeters of world size at `RefDepth` distance. It outlines everything that writes depth behind the plane — characters, props, floor edges — not just the character.
- `RefDepth` — the camera distance at which the width is true to size, plus a cap that stops the lines blowing up into smears in close-ups. Tuned as a pair with `OutlineWidth` — retune the width after changing it.
- `Threshold` — how big a depth jump (in meters) counts as an edge. Too low and slanted surfaces flood with line color; too high and a hand held in front of the chest stops getting a line. A fatter `OutlineWidth` makes the test more trigger-happy — raise this to compensate.
- `StencilOrgan`, `StencilOrganMask` — see the x-ray machine section above; the defaults show every pair's organs through the screen.

---

## Credits
Womb mesh is based on GFanon's [GF] womb mod. Tooling: BetterPenetration (Animal42069), RSkoi's wobble, xukmi's Vanilla Plus shaders, Minionsart's liquid technique. License: GPL-3.0.

**Diagnostics.** Turn on *WombExpand → Debug Log* (BepInEx config, F1) for verbose per-frame logging to `BepInEx\LogOutput.log` — including a `why={…}` line that states whether the womb engaged with the penis and, if not, why (no penis in range, tip off-axis / outside the womb volume, not deep enough, withdrawn, BP_Strength = 0, etc.). The loaded plugin version is shown at startup in BepInEx's `Loading [LiquidWobbleMPB …]` line. Material-Editor debug overlays (tip pose, physics bounds) are available as toggles.
