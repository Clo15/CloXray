# Changelog

## 1.1.5

Bug-fix release. Scenes made with earlier versions look the way they did again.

- Toys open the canal the way they did in 1.1.3.
- `Ctrl+Shift+W` ignores hidden characters and clears KKPE bends that kept the penis from pointing
  into the womb.
- A penis slightly off the canal's centre keeps the womb reacting.

## 1.1.4

**Loading a scene no longer changes it.** A saved scene came back subtly different from how it was
left: hand-tuned render queues were flattened, a womb's stencil pair could be overwritten, garment
ghosts were created on scenes that never had them, and a material setup could carry over from a
scene loaded earlier in the same session. Six separate places were doing it. A load now writes only
where a material is still on its shipped default - the case where nothing was ever chosen - and
leaves everything else exactly as saved. Changing a scene is the hotkey's job.

**BetterPenetration 5.1 is now required.** Its own fixes replace around 500 lines of work-around.
An older version is reported once as an error rather than silently limped along.

**Clothes over the window ghost translucently** instead of being cut through, at an `XrayAlpha` you
can set per garment in Material Editor. Scenes saved before this keep the look they were made with;
press the apply hotkey to give them the ghost.

**The window opens up close on tessellated bodies.** KKUTS_Tess pushes skin in front of the flat
stamp, so the window only appeared zoomed out.

**The skin veil covers the whole body.** It inherited a clothing alpha clip that left a
torso-shaped hole on a naked character.

**Scaled wombs react in proportion.** A smaller womb opens and displaces proportionally more for
the same penis, a larger one less. Rest width and displace travel are measured once per womb
instead of assumed, and the displace releases three times faster than the rings on withdrawal.

**The womb's stencil pair and render queues are saved with the scene.** They used to be written
onto live materials and never stored, so every load reverted the item to its shipped default -
invisible for a couple on the second pair. Older scenes are matched to the body stamp on load, and
only when the womb really did come back on the default.

**Optional sub-2500 render queue stack.** Below Unity's 2500 line the in-window penis receives the
screen-space shadow buffer and is shaded rather than flat. Opt in per character with `Ctrl+Shift+W`;
shipped defaults stay on the classic queues and saved scenes are untouched.

**Better partner matching.** A male merely standing near a girl is no longer claimed as her partner
- the range for that is 15cm, not half a metre - and the on-screen message says the distance the
decision was made on. A penis whose tip you have aimed yourself still gets its x-ray materials, and
your aim is left alone.

**Aim takeover.** A `k_f_dan_end` held by one of *her bones* is an automatic aim and the womb claims
it; one held by an *item* is yours and is never touched.

**BetterPenetration is re-armed after a scene load.** In Studio it learns "this is vaginal" only
while constraints are being added, which on load happens before its own agent exists - so it
reported no depth for the rest of the session.

**Free-H womb toggle is now `Ctrl+Shift+W`,** the same key as Studio.

- The first press adopts the stencil pair the scene already gave a character; a fifth character
  shares pair 1 rather than being refused.
- Clothes are no longer stamped without asking: the automatic pass corrects the garment copies a
  scene already has and creates none.
- Womb item 7.5.2, and the shader zipmod at 7.1.0. The womb carries its own copies of the
  CloXray shaders, so the two are always released together.

## 1.1.3

- **New Studio hotkey: `Ctrl+Shift+W` - one-key womb setup.** Select a girl and press: if she has
  no womb, one is added, attached at her crotch and set up - no finding the item and placing it by
  hand. Press again to move her to the next stencil pair; a second girl in the scene takes her own
  pair automatically. After the last pair the x-ray comes off her again. The womb item is never
  deleted by the hotkey. `Shift+Alt+X` is unchanged.
- The studio items now show icons in the item list.
- Womb item 7.4.3 - identical geometry; the embedded shader copies are refreshed in step with the
  shader zipmod.

## 1.1.2

- **The balls never draw through her body or clothes any more.** In the open they render normally
  and block the x-ray window like a hand. The white and wrong-colour sack ghosts that could show
  through clothing are gone with them.
- **Hands and limbs block the x-ray.** Bare limbs out of the box; a sleeved hand blocks after
  setting `LimbBlockInflate` on the body copy (per outfit - see the manual). Thighs stay
  see-through. An F1 switch turns the whole feature off. Older scenes pick it up on the first
  hotkey press.
- **Fixed a male torso turning permanently invisible** after pressing the hotkey and then
  undressing him. The game was writing its clothing mask to a material that was no longer the one
  on screen, which even removing the plugin could not undo. Heals automatically on apply now.
- **Fixed the white cap and sliver at the penis tip and base inside the window** - skin textures
  clip and fade in those zones; the window now paints them solid.
- The window's edge fades out instead of cutting hard.
- Stamped-window ("x-ray machine") scenes keep working, and her hands block those windows too.
- Old Studio scenes: constraint links to womb bones renamed in 7.4.0 migrate automatically on
  load; links to deleted bones are reported instead of silently dropped.
- Womb item 7.4.1 - identical shape, and its Studio item-FK bone list works again.
- Removed a debug overlay that could draw a blue ring on the penis while the diagnostic log was on.
- **Free-H with *Auto penis length* OFF works properly again** - the canal expansion follows the
  penis on that path too (it could stay clamped after toggling the setting off mid-play), a penis
  that never reaches the womb is no longer squished, and the womb reacts more calmly to the stroke.
- **New F1 slider: "Auto penis length: size bias (%)"** - with auto length ON, aims the fit
  slightly longer (above 100) or shorter (below 100) relative to the womb's cervix; every
  animation keeps a visible stroke at any setting.
- The Free-H womb entrance point is re-tuned per game - KK and KKS each keep their own seat.

## 1.1.1

- **The womb never turned see-through on some setups** - its shaders were not loading correctly there,
  so the x-ray window never formed, the liquid misbehaved and the wobble sliders were inactive. Fixed
  in the womb item. If you had to re-pick the shader in Material Editor to make a womb work, you do
  not any more.
- **BetterPenetration threw an exception every frame after removing a womb in H**, which also left the
  penis mis-positioned and got worse with each toggle. The plugin was handing BP a reference to an
  object it had just destroyed; BP now gets its own target back and the penis with it.
- Removing the womb in H removes the x-ray it applied - body, skin veil, clothes and the penis copy.
- The womb needed two hotkey presses in H on a character without a BP uncensor; one is enough now.
- The male's balls no longer turn white on the hotkey (the forced uncensor swap skipped the skin pass),
  and the shaft/balls junction no longer shows as a white sliver through the body.
- Cum survives toggling the womb off and on, and any internal respawn. Only a real pull-out drains it.
- Fixed the canal not opening at all on some poses: a calibration acted on an impossible measurement.
- The womb's canal geometry now comes only from the mesh's own marker bone.

## 1.1.0

- Free-H support in KK and KKS. `Shift+Alt+W` toggles the womb on the H female (rebindable in the
  F1 menu, *Free-H → Toggle womb hotkey*); the x-ray, the canal reaction and the liquid all work
  during normal H play. It is a separate key from the Studio hotkey because another plugin's raw
  Alt+X check also fires on Shift+Alt+X in the main game.
- The womb can sit anywhere on the character, not just in the vagina. In the vagina,
  BetterPenetration drives it as a vaginal penetration; at the anus, as anal; anywhere else the
  penis entry is anchored at the womb's own canal mouth and the canal opens with how far the tip
  has travelled up it. The canal is a narrow tube, so aim it at the penis.
- Per-animation penis sizing. The plugin ships measurement tables (six female sizes and three
  male sizes per game, weak and strong loops measured separately) and interpolates between them,
  so the penis is the right length before the first stroke. It keeps learning from your own
  characters on top; new sizes converge after one stroke and are instant afterwards.
- Weak and strong loops keep separate fits. Flipping the motion pattern re-fits once (the two
  patterns stroke about 30mm apart, one shared fit was always wrong for one of them).
- Spawning the womb mid-penetration is fully supported. The canal calibrates itself within a
  couple of animation loops regardless of spawn timing.
- Canal width follows the actual male: girth is latched per male (swapping males can no longer
  inherit the previous male's width), and length squish/stretch feeds back into the opening.
- Big Studio performance pass for multi-womb scenes: the collider scan was rebuilt (it was the
  single largest per-frame cost), the per-frame allocation storm is gone, parked and off-screen
  wombs now cost close to nothing.
- Futa is supported. A female-bodied character with a penis can now drive a womb: pairing looks for a
  real, visible penis mesh instead of assuming the penetrator is male, so her canal reacts, fills and
  x-rays exactly as it does for a man. A womb also never pairs with the character wearing it.
- Fixed a womb that stayed shut when the character was resized by another mod. The canal measurement
  was checked against fixed limits, so a scaled character had it rejected outright and nothing reacted.
- The man's own penis shader is kept. The x-ray is added as an extra material copy instead of
  converting his material, so custom penis shaders (KKUTS and the like) keep rendering the outside
  of the penis with their own look while the part inside the womb shows through the x-ray window.
  Copies you made yourself are never touched.
- Studio character replacement now keeps the whole setup. Swapping the girl gives the incoming card
  the same body uncensor the scene was built on, re-stamps the x-ray, and re-creates both penis
  constraints; her vagina reacts again without touching the uncensor menu. Swapping the man carries
  his BP penis uncensor across and re-aims the penis at the womb. Neither needs the apply hotkey.
- Fixed a family of replacement bugs along the way: a penis constraint that was never re-created,
  constraints doubling every time a scene was loaded, the man's penis coming back as the mosaic
  version, and a saved scene loading with the girl's body fully invisible or carrying a pile of
  duplicated material copies.
- Works with both BetterPenetration 5.0.1.5 and 5.1. The two BP fixes this plugin used to carry are
  merged upstream in 5.1, so on that version it stands down and lets BP do the work.
- New F1 switch "Diagnostic log (for bug reports)", off by default: a shipped build logs nothing at
  all, and turning it on (live, no restart) records what the plugin is doing so a log can be
  attached to a bug report.
- F1 menu reduced to 13 settings. Everything that was a calibration value rather than a real
  choice is baked in at its tuned value.
- KKS: womb placement recalibrated for the KKS rig, and a BetterPenetration-compatible body
  uncensor is applied automatically when the card lacks one.
- Fixes: harmless shader warning in the log on load, penis length jumping on pose change,
  first pose of a session not being predicted, penis FK sticking after a scene load.

## 1.0.0

- Initial release.
