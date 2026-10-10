# CloXray - Manual

An x-ray womb for Koikatsu CharaStudio: see a womb fill with liquid and react to penetration, *through* the character's body.

---

## TL;DR
Select the female, press `Ctrl+Shift+W`. A womb is added inside her, the body turns see-through
over it, and any penis close by is x-rayed and aimed down the canal. That is the whole setup.

---

## Quick start

1. Add a female whose card uses a BP body uncensor.
2. Add a male whose card uses a BP penis uncensor ("BP SOS" or similar) and turn his penis on
   (Anim > State).
3. Move him so the penis is close to the female's vagina.
4. Select **the female** and press `Ctrl+Shift+W`.

A womb appears inside her, the body turns see-through over it, and his penis is x-rayed and aimed
down the canal - push in and the canal opens around him, with the liquid and everything else live.

Press `Ctrl+Shift+W` again to move that female to the next stencil pair; a second female in the
scene takes her own pair automatically (two sharing one would see each other's organs). After the
last pair the x-ray comes off, and the next press starts over.

For her **anus**, press `Ctrl+Shift+A` instead: it adds *Xray Womb 2* at her anus point with the
uterus collapsed (a plain canal), on the pair she is already using. Select a **womb** rather than a
character and `Ctrl+Shift+W` sets up that womb alone, taking the penis aimed nearest it - that is
how a womb worn somewhere else, or by nobody, gets wired.

The womb item itself is never deleted by the hotkey - delete it in the workspace when you want it
gone.

Tune the look in **Material Editor -> Liquid** (Fill Amount, colour, wobble...) and the shapes in
**KKPE / StudioBlendShapes**.

**In the main game (Free-H)** there is nothing to place - see [Free-H](#free-h-main-game).

---

## Manual womb and shader application

For placing the womb yourself, or for putting the liquid in something that is not a womb.

1. **Add -> Clo -> Xray -> "Xray Womb 1".**
2. Place it in the female's vagina and leave its **position and rotation at default** - it is built
   to sit correctly there. `Ctrl+Shift+W` places it 5mm forward (position Z `0.005`), which centres
   the opened canal on the penis; either is fine.
3. Select the womb and press `Shift+Alt+X`. The body turns see-through over the womb, and any penis
   inside is x-rayed and aimed down the centre of the canal.

`Shift+Alt+X` works on every womb in the scene at once and does not add or move anything - use it
when you have placed the wombs yourself, or to re-apply after editing a scene. It also leaves the
character on whichever render queue order she is already on: the classic 3500 one unless
`Ctrl+Shift+W` has moved her below 2500, in which case she stays there. Only `Ctrl+Shift+W` moves a
character between the two orders - see *Render queue order*.

On a bottle or other item: apply the `CloXray/Liquid` shader to a liquid mesh copy, select it, and
press `Shift+Alt+X` to attach the wobble.

---

## Features

### X-ray see-through
- See the womb and the liquid inside it through the body.
- See a penis (or an item inside the womb) through the body too.
- One hotkey (`Ctrl+Shift+W`) sets it all up: the body goes see-through, the liquid wobble attaches to the selected item, and every inserted penis is x-rayed and aimed down the canal - automatically, no need to pick the partner.
- Re-applies itself when you swap/replace the character - no need to redo it.
- Visual toggles: out-of-body visibility, behind-body visibility, x-ray outline, occlusion (props/clothes/walls hide it), see-up-the-canal window, see-through-clothes, skin transparency.

### X-ray machine plane
- Drop a plane in front of the character and turn it into an x-ray screen: a black backdrop with a white silhouette outline and the character's revealed organs showing *through* it - like a medical viewer or an airport scanner.
- Windowed reveal (advanced): show an extra organ (e.g. a stomach) *only* where the plane covers it, while the womb stays visible everywhere. Slide the plane over the belly and the stomach appears; move it away and it's hidden.
- The revealed organ can be made semi-transparent over the screen, and you can put cum inside it that only shows through the plane too.

### The liquid
- Fill amount from empty to full; stays level when you rotate or tilt.
- Fill level stays consistent at any womb / character scale - a half-full womb reads half-full whether you shrink or enlarge it.
- One or two chambers.
  - Connected (default): the womb bulb and the canal share a single liquid level - fill flows between them and settles like one vessel.
  - Closed: the bulb and the canal are independent - the canal can hold (or not hold) liquid separately from the bulb. Use this when you want cum sitting in the canal at a different level than the womb.
- Drain the canal from either end. In Closed mode, *Fill Amount Chamber 2* sets the canal's top level and *Fill Bottom Clear C2* empties it from the bottom up - so the cum can be a floating band, or trail a withdrawing penis from top to bottom.
- Look: colour & transparency, wet gloss, edge glow, rounded surface.

### Wobble & slosh
- Sloshes when you move it; sloshes with each thrust; subtle idle jiggle. Tunable strength/speed/settle.

### Shape sliders (KKPE)
- Extra control sliders live in KKPE / StudioBlendShapes, on the womb item. They save with the scene.
- They pose the canal and womb by hand and set how the automatic reaction behaves.
- The four that set *behaviour* rather than shape are explained under **Slider reference (KKPE
  blendshapes)** below.

### Placement, partners and swaps

- **The womb can sit anywhere on the female, not just in the vagina.** In the vagina,
  BetterPenetration drives it as a vaginal penetration; at the anus, as anal; anywhere else the
  penis entry is anchored at the womb's own canal mouth and the canal reacts to how far the tip has
  travelled up it. Aim the womb's canal at the penis - it is a narrow tube, and a penis that passes
  beside it will not open the rings.
- **Futa supported.** A female-bodied character with a penis drives a womb exactly like a male does.
- **Character replacement keeps the setup.** Swapping the female carries the body uncensor, the
  x-ray and both penis constraints over to the new card; swapping the man carries his BP penis
  uncensor and re-aims him at the womb. Neither needs the hotkey afterwards.
- **The man's own penis shader is kept.** The x-ray is added as an extra material, so custom penis
  shaders (KKUTS and similar) keep their look outside the body while the part inside the womb shows
  through.

### Womb reaction to penetration
- The canal opens with depth (entrance → cervix), widens with girth, stretches on deep thrusts, and the mouth leans toward the penis (forward/back and left/right).
- Reacts whenever a BetterPenetration penis is inserted in the female the womb sits on - robust to a long or BP-squished penis and to a womb placed slightly off the exact canal line (it keys off insertion into *that female's vagina*, not just the narrow tube; a penis that isn't in her won't open it). Also reacts to a collider you push in (a toy/bottle carrying a DynamicBone collider); with both present, it follows whichever is deeper.
- Works for a posed (static) penis, not just active thrusting. Withdraw the penis - or pull its tip away via a constraint/sphere on `k_f_dan_end` - and the womb closes. The canal decides: a tip below the canal mouth, or further off its centreline than the tube is wide, counts as out however the penis is aimed.
- The deep dome (top of the womb, the `Vagina_6_entrance_open` shape) is manual - open it by hand with its KKPE slider; the auto reaction drives the entrance→cervix rings (V1-V5) only.
- Turn collider reaction off if you don't want it: globally via **BepInEx config (F1) → WombExpand → React to colliders**, or per-womb by setting the `BP_IgnoreColliders` shape (KKPE / StudioBlendShapes) above 50 - that one womb then ignores colliders and only reacts to a BP penis.

### Bottles & other items
- Put the liquid in any item (e.g. a bottle); a cap closes an open bottle mouth; wobble works there too. Saves with the scene.

### Controls
- **Hotkeys** - Studio: `Ctrl+Shift+W` adds the womb and sets everything up; `Shift+Alt+X` applies to wombs you placed yourself. Main game (Free-H): `Ctrl+Shift+W` - the same key - toggles the womb on the H female. Both rebindable in the F1 menu, under *AutoBodyReveal → Apply Now Hotkey* and *Free-H → Toggle womb hotkey*.
- **Material Editor** - look, fill, chamber mode, wobble setup. **KKPE / StudioBlendShapes** - the shape sliders above. **BepInEx config (F1)** - tuning, the hotkey, ranges. **Scene save/load** keeps wobble + constraints + slider values.

---

### Hands and limbs block the x-ray

Female hands, arms and shins block the window - a hand over the belly hides the womb behind it.
Thighs stay see-through on purpose.

- **Bare limbs** work out of the box.
- **Clothed limbs** (a sleeve over the hand) are off by default - fabric needs a per-outfit width.
  Set `LimbBlockInflate` on the body's BodyReveal copy in Material Editor (start at `0.01`, up to
  `0.06` for a loose sleeve).
- Off for one character: `RegionMaskCutoff = 0` on that same copy. Off for everyone: the F1 switch
  **"Hands and limbs block the x-ray"**.

Values set in Material Editor are never overwritten by a re-apply. Scenes saved with older versions
pick all this up on the first hotkey press.

### X-ray machine scenes

Stamped-window setups - a plane stamping `StencilRef 128` with organ items on `StencilBody 132/133`
- need nothing set: stencil bit 7 is reserved for these windows, and female hands block them too.

### Render queue order

Two orders exist and both are correct. The shaders ship with the classic one.

`Ctrl+Shift+W` moves the character it is pressed on to the second order, below Unity's 2500
opaque/transparent line. A draw below that line still receives the scene's shadows, so the penis
seen through the window is shaded like the rest of him instead of looking flat and pale. Nothing
else changes.

`Shift+Alt+X` never moves a character between the two. It applies to whichever order she is
already on, so a scene stays on the one it was built with until you press `Ctrl+Shift+W`.

| Layer | Legacy default | New RQ, applied by `Ctrl+Shift+W` |
|---|---:|---:|
| garment stamps | 2499 | 2478 |
| body stamp | 2500 | 2479 |
| balls occluder | 2501 | 2480 |
| x-ray machine plane | 3490 | unchanged |
| uterus | 3500 | 2482 |
| ovary + penis carve | 3501 | 2483 |
| womb interior + penis redraw | 3502 | 2484 |
| cum | 3503 | 2485 |
| skin veil | 3504 | 2486 |
| garment ghost | 3505 | 2487 |

A queue you set yourself in Material Editor is never overwritten.

### Scenes saved before womb 7.4.1

The womb's internal bones carry a `clo_` prefix, to avoid an ABMX bug where the item could steal
the character's bones. For a scene saved before that rename:

- NodesConstraints links to womb bones are rewritten to the new names automatically - re-save the
  scene and it sticks.
- A link to one of the 13 bones that were *removed* can't be restored; it is reported in the log
  instead of silently dropped - re-make that constraint.
- KKPE edits made directly on womb bones do not migrate.
- Fill, canal and shape sliders load normally.

## Free-H (main game)

The main game, not Studio. Nothing needs placing by hand.

- **Press `Ctrl+Shift+W` during H.** A womb is spawned inside the H partner, the x-ray is applied,
  and the canal reaction and liquid run for the rest of the scene. Press again to remove it.
  Rebindable under *Free-H -> Toggle womb hotkey*; the Studio combo `Shift+Alt+X` also works.
- **The penis is sized to the animation.** Measurement tables ship with the mod, and anything not in
  them is learned after a single stroke and remembered, so penetration lines up on unusual body
  sizes too.
- **`Auto penis length: size bias (%)` moves the deepest push.** The fit aims it at the cervix by
  default. Above 100 the penis runs slightly long and pushes past; below 100 it stops just before.
  Every animation is still measured, so the stroke stays visible at any setting. Applies on the next
  pose change.
- **`Womb offset to back (mm)` seats the womb** along her body axis, for characters whose anatomy
  sits further forward or back than the default.
- **`Cum flows out on pull-out`** drains the pooled finish-cum when the penis leaves, synced to the
  game's own drip animation. *Liquid fill amount* is the level a fresh womb starts at.
- **No Material Editor here.** The look is fixed at the shipped values and F1 is the only tuning
  surface. The render queues are always the sub-2500 set (see *Render queue order*), since no scene
  file has to stay compatible.

## Requirements
Koikatsu (KK) and Koikatsu Sunshine (KKS) are both supported, in CharaStudio and in Free-H.

Just the latest HF patch, or:
**Required:** BepInEx + Sideloader (KK), KKAPI, MaterialEditor.
**Optional (per feature):**
- BetterPenetration **5.1 or newer** - womb reaction to a penis + the penis bones (the body x-ray
  works without it). 5.1 is the minimum, because the penis-bend and duplicate-marker fixes live in BP
  itself from that version on. On an older BP the penis bend stays kinked after a scene load, and
  the log says so.
- NodesConstraints (Joan6694) + KK_AdditionalFKNodes - for aiming the penis at the womb.

---

## Technical details
*(Quirks, internals, and the "why isn't X working" answers. Skip unless you're tuning or troubleshooting.)*

**`Ctrl+Shift+W` adds a womb to the selected female, attaches it at the crotch and then runs the same apply as `Shift+Alt+X`. `Shift+Alt+X` (rebindable under *AutoBodyReveal → Apply Now Hotkey*) does several things in one press - automatically, for every womb in the scene:**
1. **Body x-ray** - stamps the character each womb sits inside (within 15cm of the vagina) so the womb shows through. Proximity-based, so it re-applies automatically on character swap. Works with or without BetterPenetration: it matches the female by the BP bone `cf_J_Vagina_root`, falling back to the vanilla `cf_j_kokan` crotch bone if no character has it (the womb item's own `cf_j_kokan` is excluded).
2. **Wobble** - attaches the liquid-wobble effect to the **selected** item's `CloXray/Liquid` renderer.
3. **Penis x-ray** - for each womb it finds the penetrating male automatically (the nearest `cm_m_dankon` penis that is **not** the womb's own wearer), and converts his `cm_m_dankon` material to `CloXray/OrgInside`, with the **stencil pair matched to that womb** so it shows through *that* body.
4. **Penis aiming** - two `NodesConstraints` position links: `k_f_dan_end` → the womb's `penis_target` bone, and `k_f_dan_entry` → wherever that womb sits - `cf_J_Vagina_root` for a womb in the vagina, `cf_J_Ana_Root` for one at the anus (BetterPenetration then treats the penetration as vaginal or anal accordingly), or the womb's own `clo_canal_entry` mouth for a womb placed anywhere else.

**In the main game, `Ctrl+Shift+W` does the same job** (*Free-H → Toggle womb hotkey*) - one key for both places. It spawns or removes the womb and does the same wiring for you. It is a separate binding from the Studio one only so you can rebind them apart; it is deliberately not `Shift+Alt+X`, because another plugin's raw Alt+X check also fires on that combo here and hides the penis.

**Automatic, not selection-based.** Both partners can carry a `cm_m_dankon` penis and the `k_f_dan` FK bones (KK_AdditionalFKNodes adds them to everyone), so the plugin pairs each womb with the nearest character that actually has a visible penis and is not the one wearing that womb - you do not pick the partner. A female-bodied character with a penis counts. A penis only claims a womb if its tip is within 15cm of its aim point, so a man standing nearby is not mistaken for the partner and in a multi-couple scene each womb wires to its own. A dan node you have targeted by hand keeps your aim - the penis still gets its x-ray materials, but no competing link is made.

**`penis_target` is a centred aim bone.** It's a dedicated, empty leaf bone baked onto the canal centreline (nothing is skinned to it), so the penis points straight down the middle of the tube - and because it's a leaf, aiming it can never drag the womb. The womb itself stays exactly where you placed it.

**Constraints only *drive* when the penis FK bones are active Studio GuideObjects** - i.e. the penis FK nodes must be enabled via KK_AdditionalFKNodes (same requirement as BetterPenetration). `AddConstraint` is idempotent and the plugin also dedups by the penis bone.

**Stencil pairs (multiple couples).** The x-ray separates up to 4 characters by stencil pair; a fifth shares pair 1, and where two windows overlap on screen they can show through each other. Set a womb's `StencilBody` to put a couple on its own pair; the hotkey matches that character's body, penis and clothes to it:

| Pair | Body ref (`StencilBody` / `StencilRef`) | Organ ref (`StencilBody_Plus_1`) |
|------|------|------|
| A (default) | 4 | 5 |
| B | 8 | 9 |
| C | 12 | 13 |
| D | 16 | 17 |

**OrgInside sliders (Material Editor).** The hotkey converts a penis's `cm_m_dankon` material (and the womb interior wall uses this shader too) to `CloXray/OrgInside` - the shader for anything crossing the organ/body boundary. Most of its Material Editor sliders are standard skin/item look controls (textures, ramps, shadow/specular) - same as KK's skin shader; only the CloXray-category ones change x-ray behaviour:
- **Bottom Window** (`BottomWindow`, On/Off) - opens the x-ray see-through window for rays looking up the open canal from below. The womb's interior canal material ships with it On; the shader's own default is Off, so a material you put `CloXray/OrgInside` on yourself (an item, or the penis the hotkey converts) starts with it off. This is the Features list's "see-up-the-canal window." When the canal is opened/penetrated, rays from underneath travel up the canal hitting pixels the womb exterior never covered, so its depth-clear never ran there - the body skin's depth would normally occlude that view. On wipes the body depth to FAR and promotes those pixels into the organ region (exactly what the exterior would have done), so you can see *up inside* the opened canal from underneath instead of the womb exterior occluding it. Off leaves those pixels as plain body, so the exterior/skin covers the view (the old behaviour). Pixels the exterior already covered are unaffected either way.
- **Alpha** (`Alpha`, CloXray category) - overall opacity of the inside-organ render; multiplies the final colour's alpha. 1 = solid, lower = more translucent.
- **StencilBody / StencilBody_Plus_1** - the stencil pair that ties this material to one couple's body, matching the Stencil pairs table above (`StencilBody` = the body ref, `StencilBody_Plus_1` = the organ ref; e.g. pair A = 4 / 5). The hotkey sets these to match the womb the penis is paired with; only change them by hand for multi-couple setups.
- **OutsideOfBodyAlpha** (`OutsideOfBodyAlpha`) - how the part of the object that is outside any body renders. 1 = fully visible (default), 0 = invisible. Colour only; doesn't change depth/stencil.
- **CullOption** (`CullOption`) - face culling (Off / Front / Back; default Back, inward-facing interior normals). The Bottom Window pass shares this cull so its coverage stays a subset of what the lit pass paints - leave it on Back for the womb interior.
- **AlphaOptionZWrite (ZWrite)** - whether this material writes depth. On by default; part of the render-order machinery, not a look control.

**X-ray machine plane (`CloXray/XrayMachine`) - the x-ray screen + windowed reveal.**
Apply `CloXray/XrayMachine` to a plane item. At its defaults (`StencilOrgan` = 0, `StencilOrganMask` = 3, render queue 3550) it paints a black backdrop + white silhouette outline over everything the plane covers, but skips revealed-organ pixels - so any organ that's already x-ray-revealed (the womb, a penis inside it) shows through the screen while the body reads as a flat silhouette. On its own that's just the "x-ray viewer" look; it doesn't change *what* is revealed, only draws the backdrop behind it.

**Windowed reveal - one plane reveals an extra organ, womb stays always-on.** To reveal a *second* organ (say a stomach) only under the plane while the default womb stays see-through everywhere, three materials cooperate through stencil bit 7 (value 128) - a free bit nothing else uses:

| Material | Shader | Settings |
|---|---|---|
| **Extra organ** (e.g. stomach mesh) | `CloXray/Organ` | `StencilBody` = **132**, `StencilBody_Plus_1` = **133**, `StencilReadMask` = **159** |
| **Bit-stamp plane** (big, covers the organ) | `CloXray/BodyReveal` | `StencilRef` = **128**, `StencilWriteMask` = **128**, `StampZTest` = **8** (Always), **Render Queue = 3490** |
| **Screen plane** | `CloXray/XrayMachine` | defaults (above) |

- The bit-stamp plane writes *only* bit 7 wherever it covers the screen. `StampZTest = 8` (Always) makes it stamp regardless of the plane's depth vs the body - otherwise a plane sitting behind the protruding belly wouldn't tag it. **It must face the camera** (`BodyReveal` culls back faces; a back-facing plane stamps nothing). Its **Render Queue must be 3490** - after the body's own reveal (2500) but before organs (3500); left at the default 2500 the body's whole-byte stamp wipes bit 7 and nothing reveals.
- The extra organ gates on region-4 and bit 7 (that's what `132` / `133` / `159` = `4|128` / `5|128` / `31|128` encode), so it reveals *only* where the plane stamped. The womb keeps its defaults (`4` / `5` / `31`), ignores bit 7, and stays revealed everywhere.
- Put the bit-stamp and the screen on two stacked planes at the same spot, or two material slots on one plane.

**Organ semi-transparency over the screen (`XrayAlphaToBlack`).** Lowering an organ's `Alpha` normally blends it toward the body skin behind it (the black screen draws *after* the organ), so it washes out to pale/white. Turn **`XrayAlphaToBlack` = 1** on the organ's `CloXray/Organ` material and `Alpha` instead fades the organ toward black - which over the black x-ray screen reads as true see-through. `Alpha = 1` looks unchanged; the toggle only bites as you lower Alpha. Leave it off for the normal in-body look, where blending with the skin is what you want.

**Cum inside a plane-gated organ (`CumStencilRef` / `CumStencilReadMask`).** Put a `CloXray/Liquid` cum mesh inside the extra organ (non-rest mode, like a bottle liquid). By default the cum draws wherever its mesh is; to make it appear only through the plane (hidden when the plane is off, matching the organ) set **`CumStencilRef` = 192** and **`CumStencilReadMask` = 128** on the cum material - it then marks itself only where the plane set bit 7. The womb's own cum stays at the defaults (`64` / `0` = always drawn) and is untouched.

**Multi-organ cutaway (several organs through one body - stomach, intestines, bladder…).** Apply `CloXray/Organ` at the default `StencilBody = 4` / `StencilBody_Plus_1 = 5` to *each* organ mesh. They all reveal through the body's existing region-4 BodyReveal - one body stamp reveals every organ sitting behind it, so a full reproductive-tract / abdominal cutaway is just several organ meshes placed inside her. This works cleanly for organs at **different heights that don't overlap on screen** (womb low, stomach high, etc.). Notes:
- **Overlap limit:** a body pixel carries only one region, and two organs sharing the default base both write region 5, so where they *overlap on screen* the second won't clear behind the first. For overlapping organs that must both show, put the extra one on its own always-on class - a free region base like `20/21`, `24/25`, or `28/29` (`StencilBody`/`_Plus_1`) with its own BodyReveal quad stamping that base over the same area - or gate one of them to a plane (below).
- **Windowed per-organ:** any organ in the set can be made plane-gated instead of always-on by using the bit7 windowing values (`132/133/159`) from the table above, so e.g. the womb stays always-on while the stomach only appears under the x-ray plane.
- **Interior/contents:** for things *inside* an organ (swallowed props, a fetus, food) use `CloXray/OrgInside` matched to that organ's pair - it reads through the belly the same way the canal wall does; or fill the organ with a `CloXray/Liquid` gated to its reveal bit.

**Chamber modes (`ChamberMode_0single_1connected_2closed`).** A single float on the `CloXray/Liquid` material picks how the womb bulb and the canal share liquid:
- **`1` Connected (default).** Two boxes, one world-horizontal level: `FillAmount` fills the lower chamber first and redistributes across both as you tilt - they behave as one connected vessel.
- **`2` Closed.** Two independent levels: chamber 1 (bulb) uses `FillAmount`, chamber 2 (canal) uses `FillAmount2`; no flow between them. Use this when the canal should hold liquid separately from the bulb.
- **`0` Single.** One box, one fill (`FillAmount`); the chamber split is ignored. (The womb ships in Connected mode; Single is mostly for non-womb items.)

**Canal cum band (Closed mode).** In Closed (2) the canal (chamber 2) has two edges: `FillAmount2` (the canal top level) and `FillBottom2` (*Fill Bottom Clear C2* - drains the canal from the floor up). Animate both to fill behind a withdrawing penis, then drip away - the cum can be a floating band or a trail. `FillBottom2 = 0` is identical to a normal fill from the bottom. Defaults: `FillAmount` / `FillAmount2` = 0.2, `FillBottom2` = 0.

**Fill level is scale-invariant.** The fill % doesn't change when you scale the womb item or the character - a half-full womb stays half-full at any scale. The cum is a CPU-skinned `SkinnedMeshRenderer`: its `unity_ObjectToWorld` is identity (all scale lives in the bones), so the shader can't read a Studio transform and runs all fill math in the mesh's REST space (rest positions baked into UV2, mapped to world by a single per-draw uniform). Rest-space math means rescaling the rig leaves the fill fraction untouched. The plugin supplies the live per-axis scale + world-up only to keep the surface horizontal and the cap crack-free - it does not feed the transform into the fill amount.

**Control-slider blendshapes (KKPE / StudioBlendShapes).** The womb exposes its control shapes as real blendshape channels on the `o_uterus` mesh, so they are per-womb and save with the scene - which an F1 config value cannot do. The four that carry a *setting* rather than a shape are explained under **Slider reference (KKPE blendshapes)**; they declare no vertex movement. The plugin reads them as weight-over-50, so 50 = 1.0x and 0 = off: `BP_Strength` (reaction gain; 0 hands the womb to manual/KKPE and it will not auto-expand), `BP_Sensitivity` (how readily penetration is recognised; saturated at 50, so higher does nothing), `BP_IgnoreColliders` (above 50 this womb ignores pushed-in colliders and reacts only to a BP penis) and `BP_Dampening`, which is read as weight-over-100 in seconds instead - default 15 = 0.15 s of ring close time. Defaults are baked into the SMR's blendshape weights, so a freshly spawned womb already carries them.

**Reaction to colliders (toys / items).** Besides a BP penis, the womb reacts to a DynamicBone collider pushed into the canal - e.g. a toy or bottle you add a KKPE collider to. It runs alongside the penis path and the womb follows whichever reaches deeper, so a toy works even with a penis also present.
- **React to colliders** (default on) - the global switch; turn it off to disable collider reaction for every womb.
- **Which colliders count** is fixed, not a setting. In Studio the womb reacts to colliders whose
  object name starts with `Collider` - the name KKPE gives one you add - so your own collider drives
  it while the character's body colliders (`KK_Colliders_...`) and the penis are ignored. In the main
  game it matches `cm_J_dan` instead. A collider counts as inserted once its tip is within 45mm of
  the canal axis, and one larger than 60mm radius is never considered. The KKPE `[J694]`-style label
  is not the object name - turn on the diagnostic log to see the real names (the
  `DynamicBoneColliders` / `COLLIDER-DIAG` lines).
- To stop just one womb from reacting to colliders (while others still do), use the per-womb `BP_IgnoreColliders` blendshape (set > 50) instead of the global switch.

**Bottle liquid layering.** Make a liquid mesh copy, inset it slightly (~0.85-0.9) so it doesn't z-fight the glass, and set the liquid material's render queue just below the glass so the glass composites over it.

**Cum z-fighting fix (`DepthOffset`).** When a `CloXray/Liquid` cum sits inside a *coincident* surface - another Liquid item it fills, or an `Organ` you added it to as a second material - the two surfaces can land on the same depth and flicker / z-fight along their shared boundary. The `DepthOffset` slider (Material Editor → Liquid) applies a polygon depth-offset to the cum so it stops fighting: set the cum's `DepthOffset` to about −2 to pull it in front of the host surface (leave the host at `0`). If the cum hides *behind* instead, flip the sign to +2; if it still flickers, raise the magnitude (−4, −6). `0` = off (default). This is the shader-side alternative to the geometric inset trick above.

---

## Slider reference (KKPE blendshapes)

The womb item carries its shape channels in KKPE / StudioBlendShapes - the canal rings, the whole-
canal width, stretch and skew, the cervix and dome, pregnancy, and the ovary arms. They are named
for what they move and you can see each one work as you drag it, so they are not listed here. They
save with the scene, and weights run 0-100.

Four channels are different, and are the ones worth reading about: they move no vertices at all.
They exist so that a per-womb *setting* saves with the scene, which an F1 config value cannot do.

- `BP_Strength` - how hard the womb reacts to penetration. 50 = normal. **0 switches the automatic
  reaction off completely**, leaving the shape channels entirely under your control.
- `BP_Dampening` - how fast the canal closes again after a thrust. Higher = slower, softer return.
- `BP_Sensitivity` - how readily the womb decides a penis is in it. 50 = normal; 0 hands off
  entirely. Values above 50 do not increase it further - the gate is already fully open at 50.
- `BP_IgnoreColliders` - above 50, this womb ignores toys and bottles and reacts only to a
  BetterPenetration penis. The global equivalent is *F1 -> WombExpand -> React to colliders*.
- `BP_IndependentRings` - at 0 (the default) a ring stays open while anything is deeper than it, which
  is what a shaft does to a canal. Any value above 0 makes each ring react only to what is actually
  beside it, so a small ball opens the ring it is passing through and the rings behind it close again -
  one ring at a time as it travels. The value is how wide a band travels with the object: around 20 is
  a tight grip on a ball, 100 opens about a ring and a half either side. Studio only, and toys or other
  colliders only;
  a BetterPenetration penis is unaffected, since a shaft spans every ring behind its tip anyway.

## Slider reference (Material Editor)

The CloXray sliders that decide whether a setup works - modes, values that must match something
else, and the ones that make the mod look broken when they are wrong. Pure look dials are left out:
tint, texture, matcap, fresnel, emission and brightness do what their names say and any value is
valid. On `CloXray/Organ` and `CloXray/OrgInside` the many sliders not listed here are the stock KK
skin/item look controls, unchanged. Shape channels live in KKPE - see above.

### CloXray/Liquid - chambers and fill
- `FillAmount`, `ChamberMode_0single_1connected_2closed`, `FillAmount2`, `FillBottom2` - see "Chamber modes" and "Canal cum band" above.
- `VolumeConserve_0off_1cube_2ellipsoid` - how the fill % maps to a surface height. 0 and 1 are the same linear mapping (right for box-like vessels); 2 treats the vessel as rounded, so an animated pour looks volumetrically honest in a round flask or the womb bulb. Empty, half and full look identical in every mode - the difference is the in-between fills.
- `CumStencilRef`, `CumStencilReadMask` - see "Cum inside a plane-gated organ" above.
- `DepthOffset` - see "Cum z-fighting fix" above.

### CloXray/Liquid - wobble
These are setup values the plugin reads from the material about twice a second. They need the wobble
driver attached - the womb gets it automatically, a bottle needs the hotkey - and take about half a
second to react after you edit them.
- `IdleWobbleSpeed`, `IdleWobbleStrength` - gentle sway even in a still scene. Both must be above 0 to see anything, and it never settles: set strength to 0 for still renders. Works without the wobble driver.
- `MaxWobble` - motion-slosh sensitivity and ceiling in one: every jolt is scaled by it and capped at it. **0 = motion slosh off.**
- `ThrustSlosh_0off_1global_2perChamber` - slosh with each BetterPenetration thrust, scaled by penetration speed. 1 jolts the liquid as one; 2 lets the bulb and the canal slosh independently, with the jolt going to whichever chamber the tip is near - the showpiece womb setting. When the tip pose is not available, 2 quietly behaves like 1.
- `ThrustSloshGain` - thrust-to-slosh volume. Each jolt is still capped by `MaxWobble`, so past a point raise that instead.

### CloXray/Liquid - surface cap
- `EdgeRadius` - rounds the edge where the surface meets the wall; 0 = flat, sharp fill line. Applies in Closed mode and to the Connected bulb only; the connected canal and Single-mode bottles stay flat by design.
- `CapDome` - shading-only doming of the top surface. The geometry stays flat, so there is no bulge in profile view.
- `CapTess` - subdivision of the strip around the fill surface; 1 = off. **Needs DX11.**
- `CapForOpenMesh` - for open-mouthed vessels: builds the missing top surface as a disc so you do not look into a hollow shell. Leave off for closed meshes, and centre the bounds box on the vessel first.

### CloXray/Liquid - physics box setup
For custom items only - the womb ships with baked values; do not move them there.
- `ShowSetupPhysicsBounds` - setup overlay: a yellow wireframe box per chamber, plus yellow lines where its planes slice the mesh. Fit the box to the liquid interior - bottom plane where fill 0 should sit, top plane at the full line, side planes at the inner walls (they set the tilt pivot and the ellipsoid proportions) - then switch it off. On the womb the box shown is live-measured, so the sliders will not move it; that is expected.
- `Bound1MinY_bottom` ... `Bound6MaxZ_front` - chamber 1's invisible physics box: fill 0 = bottom plane, fill 1 = top plane, plus the wobble pivot and volume proportions. The bottom plane also doubles as the divider deciding which vertices belong to chamber 2.
- `C2Bound1MinY_bottom` ... `C2Bound6MaxZ_front` - the same box for chamber 2 (its fill range, pivot, and volume share in Connected mode). Chamber membership comes from chamber 1's bottom plane, not from this box.

### CloXray/Organ - the revealed organ
- `OutsideOfBodyAlpha` - visibility of the parts outside the body's outline. The shader default is 0, so a freshly applied Organ material is **invisible in open air** - if your custom organ "vanished", raise this to 1. The shipped womb is baked at 1.
- `Alpha` - fades the organ into the skin painted behind it. It sinks under the skin and washes pale; it does not reveal the scene behind her. For the x-ray-screen look pair it with `XrayAlphaToBlack`.
- `XrayOutlineWidth` - a highlight rim around the organ's silhouette, painted on the surrounding skin. 0 = off; try 0.002-0.005.
- `XrayOutlineExtrusionMode` - 0: width in meters, thins as the camera pulls back; 1: constant on-screen width, also closes gaps at sharp corners. **Re-tune the width after switching** - the number means different things.
- `XrayOutlineColor`, `XrayOutlineColorBlend`, `XrayOutlineCull` - the rim's colour (its alpha is the opacity, kept exact where mesh layers overlap), how far it is tinted by the organ's own colour (0 = pure outline colour, 1 = fully tinted), and its thickness style (0 = solid full rim, 1 = thinnest clean ring; whole numbers only).
- `StencilBody`, `StencilBody_Plus_1`, `StencilReadMask` - pair and window plumbing; see the stencil-pairs table and the windowed-reveal recipe above. Leave `StencilReadMask` at 31 unless following that recipe - other values silently hide the organ in-body.
- `XrayAlphaToBlack` - see "Organ semi-transparency over the screen" above.

### CloXray/OrgInside - extras
The main sliders are documented in "OrgInside sliders" above. Two more:
- `BehindBodyAlpha` - ghost-through-skin opacity for the parts inside the body but outside the x-ray window (the shaft below the womb). 0 = classic hidden; try 0.2-0.4 for a faint full-length ghost. High values paint over the skin rather than sort behind anything.
- `XrayOutlineWidth`, `XrayOutlineColor` - a flat coloured overlay marking where the object hides behind the body; it reads as a solid scanner-style silhouette of the hidden length, not a thin ring. Width in meters, 0 = off. Translucent colours stack where the shell overlaps itself.

### CloXray/BodyReveal - the body stamp
- `StencilRef` - the character's x-ray ID; must equal the womb Organ's `StencilBody` (4/8/12/16 - see the pairs table). The hotkey sets and re-syncs it; scene load never overwrites a saved value.
- `StencilWriteMask` - 255 on a body. 128 only on a windowed-reveal stamp plane.
- `StampZTest` - 4 (LEqual) on a body; 8 (Always) only on a stamp plane - without it the window fails wherever the belly protrudes closer than the plane. Other values are not useful.

### CloXray/BodyRevealExtra - the skin veil
The second body copy the hotkey applies (config "Also apply skin veil", default on). It re-draws the character's own lit skin over the whole x-ray window, after the womb stack - so one slider fades the entire in-body view like a master opacity.
- `XrayAlpha` - the master x-ray strength: 0 = the window is fully covered by opaque skin (**looks exactly like "the mod stopped working" - the first thing to check**), 1 = raw full-brightness x-ray. The plugin sets it only when the veil is first created; re-pressing the hotkey deliberately never resets your tweak, so a scene saved at 0 stays at 0 until you move it.
- `StencilComp` - 4 = the veil covers womb and penis alike, one uniform fade; 3 = the penis is skipped and stays crisp while the womb dims under skin. Use exactly 3 or 4 - in-between values are undefined, not a mix.
- `StencilBody_Plus_1` - pair plumbing: the womb Organ's `StencilBody_Plus_1` (5/9/13/17). A wrong value makes `XrayAlpha` appear dead; re-press the hotkey to auto-resync rather than hunting by hand.

### CloXray/AddXrayToMaterialCopy - see-through for any object
A manual tool, not applied by the plugin. In Material Editor: copy the target's material, set the copy's shader to this, and set the copy's render queue lower than the original's. The copy then hides a dithered share of the original's pixels, so whatever rendered before it shows through - e.g. peel a garment down to the body by putting the copy's queue after the body's but before the garment's.
- `Alpha` - the strength: 0 = no effect, 1 = the original fully hidden, in between = a fine checker dither (visible up close; expected). If nothing happens at all, the copy's queue is not below the original's.

### CloXray/XrayMachine - the screen plane
- `BackgroundColor`, `OutlineColor` - the screen fill and the silhouette lines. Alpha is ignored on both: the screen is always opaque and only revealed organs show through it. To hide the lines entirely, set the outline colour equal to the background.
- `OutlineWidth` - line thickness, in centimetres of world size at `RefDepth` distance. It outlines everything that writes depth behind the plane - characters, props, floor edges - not just the character.
- `RefDepth` - the camera distance at which the width is true to size, plus a cap that stops the lines blowing up into smears in close-ups. Tuned as a pair with `OutlineWidth` - retune the width after changing it.
- `Threshold` - how big a depth jump (in meters) counts as an edge. Too low and slanted surfaces flood with line colour; too high and a hand held in front of the chest stops getting a line. A fatter `OutlineWidth` makes the test more trigger-happy - raise this to compensate.
- `StencilOrgan`, `StencilOrganMask` - see the x-ray machine section above; the defaults show every pair's organs through the screen.

---

## Credits
Womb mesh is based on GFanon's [GF] womb mod. Tooling: BetterPenetration (Animal42069), RSkoi's wobble, xukmi's Vanilla Plus shaders, Minionsart's liquid technique. License: GPL-3.0.

**Diagnostics.** Turn on *General → Diagnostic log (for bug reports)* (BepInEx config, F1) to write a diagnostic log to `BepInEx\LogOutput.log`: which penis paired with which womb, the entry anchor and stencil pair chosen, the materials stamped, and any warning that is otherwise silent. It is event-driven rather than a per-frame dump, and takes effect live - turn it on, reproduce the problem, then send the log. The loaded plugin version is shown at startup in BepInEx's `Loading [LiquidWobbleMPB …]` line. Material-Editor debug overlays (tip pose, physics bounds) are available as toggles.
