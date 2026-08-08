# Changelog

## 1.1.2

- **The balls never draw through her body or clothes any more.** In the open they render normally
  and block the x-ray window like a hand. The white and wrong-colour sack ghosts that could show
  through clothing are gone with them.
- **Hands and limbs block the x-ray.** Bare limbs out of the box; a sleeved hand blocks after
  setting `LimbBlockInflate` on the body copy (per outfit — see the manual). Thighs stay
  see-through. An F1 switch turns the whole feature off. Older scenes pick it up on the first
  hotkey press.
- **Fixed a male torso turning permanently invisible** after pressing the hotkey and then
  undressing him. The game was writing its clothing mask to a material that was no longer the one
  on screen, which even removing the plugin could not undo. Heals automatically on apply now.
- **Fixed the white cap and sliver at the penis tip and base inside the window** — skin textures
  clip and fade in those zones; the window now paints them solid.
- The window's edge fades out instead of cutting hard.
- Stamped-window ("x-ray machine") scenes keep working, and her hands block those windows too.
- Old Studio scenes: constraint links to womb bones renamed in 7.4.0 migrate automatically on
  load; links to deleted bones are reported instead of silently dropped.
- Womb item 7.4.1 — identical shape, and its Studio item-FK bone list works again.
- Removed a debug overlay that could draw a blue ring on the penis while the diagnostic log was on.
- **Free-H with *Auto penis length* OFF works properly again** — the canal expansion follows the
  penis on that path too (it could stay clamped after toggling the setting off mid-play), a penis
  that never reaches the womb is no longer squished, and the womb reacts more calmly to the stroke.
- **New F1 slider: "Auto penis length: size bias (%)"** — with auto length ON, aims the fit
  slightly longer (above 100) or shorter (below 100) relative to the womb's cervix; every
  animation keeps a visible stroke at any setting.
- The Free-H womb entrance point is re-tuned per game — KK and KKS each keep their own seat.

## 1.1.1

- **The womb never turned see-through on some setups** — its shaders were not loading correctly there,
  so the x-ray window never formed, the liquid misbehaved and the wobble sliders were inactive. Fixed
  in the womb item. If you had to re-pick the shader in Material Editor to make a womb work, you do
  not any more.
- **BetterPenetration threw an exception every frame after removing a womb in H**, which also left the
  penis mis-positioned and got worse with each toggle. The plugin was handing BP a reference to an
  object it had just destroyed; BP now gets its own target back and the penis with it.
- Removing the womb in H removes the x-ray it applied — body, skin veil, clothes and the penis copy.
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
