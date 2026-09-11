using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace LiquidWobbleMPB
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("marco.kkapi", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.deathweasel.bepinex.materialeditor", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.animal42069.kkstudiobetterpenetration", BepInDependency.DependencyFlags.SoftDependency)]
    public class LiquidWobbleMPBPlugin : BaseUnityPlugin
    {
        public const string Guid = "Clo.LiquidWobbleMPB";
        public const string Name = "LiquidWobbleMPB";
        public const string Version = "1.1.5";

        // The gated view of the log sink: null = silent, and every `_logger?.LogX(...)` in the plugin
        // is a no-op. Never assign it directly - ApplyLogVisibility() owns it.
        internal static ManualLogSource _logger;
        // The real sink. _logger is the DIAGNOSTIC handle and is null in a shipped build; anything the
        // on-screen text (LogLevel.Message shows it on screen) must go through this one.
        internal static ManualLogSource _log;

        // The item's own entrance axis bone - used to find the item-scoped root to attach to
        // (the host character also has this bone, so the search must stay inside the item subtree).
        // The WOMB's own axis bone, not hers. The item's skeleton was renamed to clo_* so it would
        // stop colliding with the character's bone names in ABMX; this const was missed in that pass
        // and kept naming HER bone. FindItemRoot climbs until a subtree contains this name, so with the
        // womb's copy gone the walk sailed past the womb and stopped at the CHARACTER - attaching
        // WombExpandEffect to chaF_001 itself. Everything that asks "is this transform inside a womb"
        // via GetComponentInParent<WombExpandEffect> then answered YES for her entire skeleton, and
        // FindTargetFemale (which excludes womb bones that way) could no longer find her at all.
        private static readonly string AxisBone = MainGameWomb.WombBone("cf_j_kokan");

        // --- Live config (BepInEx F1 menu). Read each frame by WombExpandEffect, so tweaks
        //     apply instantly. Per-object strength/dampening are NOT here - they're inert
        //     blendshapes (BP_Strength / BP_Dampening) so they're per-item and save with the scene. ---
        // F1 is not a tuning surface - the best value is selected and hard-wired, not offered:
        // 48 calibration values are now compiled constants below (frozen at the tuned numbers the
        // F1 defaults held); only genuine switches, hotkeys and content choices keep an entry.
        private static ConfigEntry<bool> _cfgEnabled;
        private static ConfigEntry<bool> _cfgDebugLog;
        private static ConfigEntry<bool> _cfgAutoBodyReveal;
        private static ConfigEntry<KeyboardShortcut> _cfgAutoBodyRevealKey;
        private static ConfigEntry<KeyboardShortcut> _cfgStudioPairKey;
        private static ConfigEntry<bool> _cfgBodyVeil;
        private static ConfigEntry<bool> _cfgLimbMask;
        private static ConfigEntry<bool> _cfgClothesReveal;
        private static ConfigEntry<bool>  _cfgReactColliders;
        private static ConfigEntry<float> _cfgHFillAmount;
        private static ConfigEntry<KeyboardShortcut> _cfgHToggleKey;
        private static ConfigEntry<bool>  _cfgHPullOutFlow;
        private static ConfigEntry<bool>  _cfgHAutoLength;
        private static ConfigEntry<float> _cfgHWombBack;
        private static ConfigEntry<float> _cfgHInStroke;
        private static ConfigEntry<string> _cfgHBPDanOptions;
        // Log output: true for a shipped build, false while testing.
        public const bool ReleaseSilent = true;

        // ⚠ TEST BUILD ONLY - SET BACK TO false BEFORE ANY RELEASE ⚠
        // Hands the log sink over and forces the diagnostic dumps on regardless of the F1 switch, so a
        // a full log is produced without being talked through the menu. A shipped build must decide
        // this from the switch alone; the shipped config file must equal these defaults.

        public static bool Configured { get; private set; }
        public static bool  CfgEnabled          => _cfgEnabled == null || _cfgEnabled.Value;   // master F1 toggle; absent/not-bound -> on
        public static bool  CfgDebugLog         => _cfgDebugLog != null && _cfgDebugLog.Value;
        public static bool  CfgBodyVeil         => _cfgBodyVeil == null || _cfgBodyVeil.Value;
        public static bool  CfgLimbMask         => _cfgLimbMask == null || _cfgLimbMask.Value;   // OFF = womb shows through hands/limbs (no torso mask)
        public static bool  CfgClothesReveal    => _cfgClothesReveal != null && _cfgClothesReveal.Value;
        public static bool  CfgReactColliders   => _cfgReactColliders != null && _cfgReactColliders.Value;
        public static float CfgHFillAmount      => _cfgHFillAmount != null ? _cfgHFillAmount.Value : 0f;
        public static bool  CfgHPullOutFlow     => _cfgHPullOutFlow == null || _cfgHPullOutFlow.Value;
        public static bool  CfgHAutoLength      => _cfgHAutoLength == null || _cfgHAutoLength.Value;
        public static float CfgHWombBack        => _cfgHWombBack != null ? _cfgHWombBack.Value : 2f;   // default mirrors the Bind above
        public static string CfgHBPDanOptions   => _cfgHBPDanOptions != null ? _cfgHBPDanOptions.Value : "";
        // ── Hard-wired calibration (was F1; values = the tuned defaults) ───────────────────────────
        public const float CfgRingWeight       = 54f;
        public const float CfgEntranceWeight   = 52f;   // was 58 - entrance ring slightly more closed
        public const float CfgCervixWeight     = 52f;
        public const float CfgDepthStart       = 0.10f;
        public const float CfgDepthEnd         = 0.97f;
        public const float CfgOpenWidth        = 0.18f;
        public const float CfgDepthSmoothing   = 12f;
        public const float CfgRefGirth         = 0.0213f;
        public const float CfgFullDepthIn      = 0.62f;
        // Calibrated from the measured canal geometry rather than by eye. With the mouth-to-dome
        // distance at 77.3mm and the displace band travelling 0.705 of it, the weight that holds the
        // cervix level with the tip is 41.5 + (depth-1)*202.6. Below that the dome trails and the tip
        // pokes through from depth 0.86; at it the dome tracks until the band runs out of travel at
        // depth 1.29. Deeper still, these and the previous values both sit at the 100 cap, so the
        // full-stretch appearance is unchanged - only the approach differs.
        public const float CfgStretchMax       = 42f;
        // Contact, not a feel value: the tip reaches the undisplaced dome at depth 0.795, so a start
        // of 0.55 had the band moving while the tip was still 27mm short of it. 0.76 leaves 1.3mm of
        // lead-in, which the SmoothStep ease needs to meet the straight-line requirement without
        // letting the tip through.
        public const float CfgStretchStart     = 0.76f;
        public const float CfgStretchOvershoot = 203f;
        public const float CfgDirReact         = 25f;
        public const float CfgOpenLead         = 0.06f;
        public const float CfgCloseSmoothing   = 4f;
        public const float CfgEntranceOpenWidth  = 0.30f;
        // Ramp LENGTH for the body rings (V2..V5). OpenWidth stays the LEAD-IN: where opening starts
        // relative to the ring. 0.24 puts full open exactly at the ring's own position - at 0.18 the
        // ramp finished 6.6mm short of it and the ring read as ahead of the shaft.
        public const float CfgRingRampWidth      = 0.24f;
        public const float CfgEntranceCloseScale = 2f;
        public const float CfgOpenTime           = 0.2f;
        public const float CfgEntranceOpenScale  = 2f;
        public const float CfgMaxGirthScale      = 2.5f;
        public const float CfgVeilAlpha        = 0.9f;
        // Garment ghost: fabric reads better a touch MORE present than skin - 0.77 leaves ~23% of the
        // cloth visible over the window, against the skin veil's ~10%. Tuned by eye on screen (0.70 ->
        // 0.77, 2026-08-27). Written only when the ghost copy is created; the per-garment ME
        // slider owns it afterwards, so scenes that already have a ghost keep whatever they saved.
        public const float CfgGarmentVeilAlpha = 0.77f;
        public const float CfgPairRange        = 0.5f;
        public const float CfgAutoBodyRevealRange = 0.15f;
        public const float CfgTipDetach        = 0.20f;
        public const float CfgEntryDetach      = 0.05f;
        public const float CfgColliderRange    = 0.3f;
        public const float CfgColliderInCanal  = 0.045f;
        public const float CfgColliderMaxRadius = 0.06f;
        public const string CfgColliderName    = "Collider";
        public const float CfgHWombScale       = 1.15f;
        public const float CfgHPenisOutside    = 1f;
        public const float CfgHPinOffset       = 10f;
        public const float CfgHPinDepth        = 0f;
        public const string CfgHColliderName   = "cm_J_dan";
        public const float CfgHPressGain       = 80f;
        public const float CfgHFollowPct       = 60f;
        public const float CfgHForceStretch    = -1f;   // debug slider retired: -1 = off, permanently
        public const float CfgHStrokeShallow   = 30f;
        public const float CfgHStrokeDeep      = 72f;
        public const float CfgHMoundDown       = 50f;
        public const float CfgHContactPct      = 85f;
        // un-frozen back into F1 by request - the one honest "penis slightly longer/shorter
        // vs the auto fit" knob. It scales the fit TARGET (where the deepest push should land
        // relative to the cervix), so the fit still measures every animation and the stroke stays
        // visible at any setting - the caps (0.6x..1.25x natural) still apply.
        public static float CfgHInStrokePct     => _cfgHInStroke != null ? _cfgHInStroke.Value : 100f;
        public const float CfgHBaseStretchPct  = 0f;
        public const float CfgHWombPush        = 1.15f;   // DefaultWombPush - dome leads the tip slightly
        public const bool  CfgHPenisBottomWindow = true;
        // The collider filter for the CURRENT game: Studio settings are validated and locked - Free-H
        // never changes them, it has its own (hard rule 2026-07-03).
        public static string CfgColliderNameForGame => MainGameWomb.IsStudio ? CfgColliderName : CfgHColliderName;

        // Silent unless the F1 diagnostic switch is on. Called once the switch is bound and again
        // whenever it changes, so turning it on mid-session starts logging without a restart.
        private static void ApplyLogVisibility()
        {
            _logger = (ReleaseSilent && !CfgDebugLog) ? null : _log;
        }


        // EVERY BUG REPORT SHOULD SAY WHAT THE SETTINGS WERE. A config carries its own
        // history: BepInEx keeps a stored value even after a default changes, and people switch
        // features off to work around a fault and then report from that state. Without this line the
        // question "could his options explain it?" is unanswerable from a log, which has already cost a
        // round trip. Non-defaults only, so a stock setup prints one short line and says so.
        private static bool _settingsLogged;
        private void LogSettingsOnce()
        {
            if (_settingsLogged) return;
            _settingsLogged = true;
            try
            {
                var diff = new System.Collections.Generic.List<string>();
                foreach (var e in Config.GetConfigEntries())
                {
                    var ce = e as ConfigEntryBase;
                    if (ce == null) continue;
                    object cur = ce.BoxedValue, def = ce.DefaultValue;
                    string a = cur == null ? "null" : cur.ToString();
                    string b = def == null ? "null" : def.ToString();
                    if (a != b) diff.Add(ce.Definition.Section + "/" + ce.Definition.Key + " = " + a + "  (default " + b + ")");
                }
                if (diff.Count == 0)
                    _logger?.LogInfo("CloXray: all settings are at their defaults.");
                else
                    _logger?.LogWarning("CloXray: " + diff.Count + " setting(s) differ from the shipped defaults — "
                        + string.Join(" | ", diff.ToArray()));
            }
            catch (System.Exception e) { _logger?.LogWarning("CloXray: could not read the settings for the log: " + e.Message); }
        }
        // The plugin object is the one MonoBehaviour guaranteed to exist in BOTH Studio and the main
        // game, so it hosts the few checks that must run a frame late. WobbleSceneController cannot:
        // it is a Studio scene controller and does not exist in Free-H.
        internal static LiquidWobbleMPBPlugin Host;

        /// Verify a MaterialEditor write LANDED, by looking the material up AGAIN each time instead of
        /// holding a reference to it. Holding one is what made this check lie: ME re-assigns the
        /// renderer's material array, so the captured reference becomes an orphan nothing draws, and
        /// reading it reported 0.500 while Material Editor's own panel showed the 0.770 that had in fact
        /// been written. A name+shader lookup always finds whichever instance is live now.
        internal static void VerifyFloatSoon(Renderer r, string matBaseName, string shaderName,
                                             string prop, float want, string what)
        {
            if (Host == null || r == null || string.IsNullOrEmpty(matBaseName) || string.IsNullOrEmpty(prop)) return;
            Host.StartCoroutine(VerifyFloatRoutine(r, matBaseName, shaderName, prop, want, what));
        }

        private static Material FindLive(Renderer r, string matBaseName, string shaderName)
        {
            if (r == null || r.sharedMaterials == null) return null;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || m.shader == null) continue;
                if (!string.IsNullOrEmpty(shaderName) && m.shader.name != shaderName) continue;
                if (MEBridge.BaseName(m) == matBaseName) return m;
            }
            return null;
        }

        private static System.Collections.IEnumerator VerifyFloatRoutine(Renderer r, string matBaseName,
                                                                        string shaderName, string prop,
                                                                        float want, string what)
        {
            const float Window = 3f;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < Window)
            {
                yield return null;
                if (r == null) yield break;                       // renderer died with its character
                var m = FindLive(r, matBaseName, shaderName);
                if (m == null || !m.HasProperty(prop)) continue;  // not re-created yet - keep looking
                if (Mathf.Abs(m.GetFloat(prop) - want) <= 0.01f) yield break;   // landed; say nothing
            }
            var last = FindLive(r, matBaseName, shaderName);
            if (last == null || !last.HasProperty(prop)) yield break;
            _logger?.LogError("CloXray: " + what + " - " + prop + " is still " + last.GetFloat(prop).ToString("F3")
                + " " + Window.ToString("F0") + "s after a write of " + want.ToString("F3")
                + ", on the material that is actually being drawn. MaterialEditor did not take the write.");
        }

        private void Awake()
        {
            Host = this;
            // A shipped build runs SILENT: _logger stays null until ApplyLogVisibility() below decides
            // otherwise, so nothing is logged during the binds either. The F1 diagnostic switch is what
            // hands the sink over, which is how a full log is produced for a bug report without a new
            // build. ReleaseSilent = false logs unconditionally (development).
            _log = Logger;
            // Profiling probe: this OnGUI appeared in 31/40 spike frames. This component draws
            // only explicit-Rect GUI (the research dot) - disabling the GUILayout pass removes Unity's
            // per-frame IMGUI layout event + its allocations for this always-present behaviour.
            useGUILayout = false;

            _cfgEnabled = Config.Bind("General", "Enabled", true,
                "Master switch for the whole mod (ON by default). OFF = the plugin stops driving and stops reaching into the scene: wombs freeze (canal/liquid stop updating), no x-ray auto-apply on character load, and BetterPenetration + the penis FK are left exactly as you posed them. Takes effect live — no scene reload needed.");

            // A NEW key on purpose: 1.0 shipped a "WombExpand / Debug Log" that did nothing (the sink was
            // nulled unconditionally), so reusing it would switch the full log on for anyone whose old
            // config happens to hold true. That line is simply ignored now.
            _cfgDebugLog = Config.Bind("General", "Diagnostic log (for bug reports)", false,
                "Writes what the plugin is doing to BepInEx/LogOutput.log: which penis it paired with which womb, the entry anchor and stencil pair it chose, the materials it stamped, and any warning that is otherwise silent. Event-driven, so it stays quiet until something happens — a scene load plus one hotkey press is a few hundred lines. OFF by default: a shipped build logs nothing at all. Turn it on, reproduce the problem, then send the log. Takes effect live.");
            ApplyLogVisibility();
            _cfgDebugLog.SettingChanged += (s, e) => ApplyLogVisibility();

            const string brSec = "AutoBodyReveal";
            _cfgAutoBodyReveal = Config.Bind(brSec, "Enable", true,
                "Auto-apply the CloXray BodyReveal (x-ray body stencil) via MaterialEditor when a character loads/swaps, IF a CloXray womb is near its vagina. The womb's organ stencil is matched automatically. Per-body: only characters with a womb nearby are touched.");
            _cfgAutoBodyRevealKey = Config.Bind(brSec, "Apply Now Hotkey", new KeyboardShortcut(KeyCode.X, KeyCode.LeftShift, KeyCode.LeftAlt),
                "Manually (re)apply BodyReveal to every character near a CloXray womb. Use after first placing a womb, or after dragging one onto a different character. (Shift+Alt+X. Rebind here if it ever collides with another plugin.)");

            _cfgStudioPairKey = Config.Bind(brSec, "Studio: womb + pair hotkey", new KeyboardShortcut(KeyCode.W, KeyCode.LeftControl, KeyCode.LeftShift),
                "STUDIO only. Select a girl and press: if she has no womb one is added for her, parented to her and placed, then the whole x-ray is applied - no need to find the item and drop it in. Press again to move her to the next stencil pair, which is how a second or third girl in one scene gets her own (two girls sharing a pair see each other's organs). After the last pair it removes her x-ray and womb, and the next press starts over. (Ctrl+Shift+W.)");

            _cfgReactColliders = Config.Bind("WombExpand", "React to colliders", true,
                "When NO BP penis is engaged, also react to the nearest DynamicBoneCollider (e.g. a BP collider you place and size with KKPE on a bottle/toy). The womb opens and displaces from the collider's deeper capsule end (treated as the tip) and its radius (girth), exactly like a penis. READ-ONLY: the collider itself is never modified. A real BP penis always takes priority.");

            _cfgClothesReveal = Config.Bind("AutoBodyReveal", "Also stamp worn clothes (x-ray through clothes)", true,
                "When applying BodyReveal (hotkey or character-change), ALSO stamp every WORN torso garment (top/bot/bra/shorts/panst; only clothes that are ON) with a BodyReveal material copy at the matching pair stencil — the womb x-rays through the clothes and the out-of-body bleed disappears at stamped pixels. ME persists the copies per scene/card. A freshly equipped garment needs another hotkey press. OFF = body only (clothes hide the shell; interior/cum may bleed unless the womb's OutBodySceneConfine is on).");


            _cfgHFillAmount = Config.Bind("Free-H", "Liquid fill amount", 0f,
                new ConfigDescription("MAIN GAME only: the womb's liquid fill level (Material Editor has no H-scene UI, so the fill is set here instead). 0 = empty (default) — raise it when you want cum. Live: drag mid-scene and the liquid follows. In Studio this does nothing (use Material Editor there).", new AcceptableValueRange<float>(0f, 1f)));




            _cfgHToggleKey = Config.Bind("Free-H", "Toggle womb hotkey", new KeyboardShortcut(KeyCode.W, KeyCode.LeftControl, KeyCode.LeftShift),
                "MAIN GAME only: toggles the womb on the H female (spawn/remove + x-ray). Same combo as the Studio one-key setup, so there is one key to remember for both. NOT Shift+Alt+X: another plugin's raw Alt+X check also fires on that combo and hides the penis.");








            _cfgHAutoLength = Config.Bind("Free-H", "Auto penis length (fit animation)", true,
                "MAIN GAME only: slowly fits the penis's natural length to the current animation (constant during play — the size NEVER pulses with the stroke). Too long for the animation (tip parked at the womb, only compressing) -> shortened until its own motion shows as a real stroke; too short (never reaches the womb) -> lengthened until the deepest push just reaches it. Adapts over a few seconds per animation/position change; restored on womb toggle-off. OFF = the legacy squish-sweep stroke.");

            // 5 -> 6 (2026-08-07, by eye in KK Free-H: move the entrance point 1mm toward her back).
            // This value IS KK's offset (SeatForwardMM 0 = the reference); KKS subtracts its own
            // constant, moved in lockstep so KKS keeps the approved seat. DIRECTION: raising
            // this moves the entrance toward her BELLY, lowering it moves it toward her BACK - the
            // opposite of the old wording, corrected after watching 6 -> 8 move it the wrong
            // way. KK settled at 2 (2026-08-07). The games need different numbers because the womb
            // is placed off her KOKAN BONE while the look is judged against the VULVA SURFACE, and the
            // two games ship different body + uncensor meshes - the vulva probe measured the
            // entrance-vs-vulvaRoot delta as KK (0, +6.1, +2.2)mm vs KKS (-0.2, +4.8, +3.8)mm.
            _cfgHWombBack = Config.Bind("Free-H", "Womb offset to back (mm)", 2f,
                new ConfigDescription("MAIN GAME only: shifts the whole womb along her front/back axis (pelvis frame — follows the pose). LOWER = toward her BACK, higher = toward her belly (the kokan bone's forward axis points into her body, so the shift runs the opposite way to what the name suggests — verified on screen 2026-08-07). Live.", new AcceptableValueRange<float>(-20f, 30f)));

            _cfgHInStroke = Config.Bind("Free-H", "Auto penis length: size bias (%)", 100f,
                new ConfigDescription("MAIN GAME only, with 'Auto penis length' ON: where the auto fit aims the deepest push, relative to the womb's cervix. 100 = at the cervix (the tuned default). Above 100 = a slightly LONGER penis (the deepest push lands past the cervix, more dome push); below 100 = slightly shorter (stops just before it). The fit still measures every animation, so the stroke stays visible at any setting. Takes effect on the next pose/animation change.", new AcceptableValueRange<float>(50f, 140f)));

            // Game-specific default: KKS's longer canal + cleaner collider signal read 1.5 as a
            // firm, liked push; KK's shorter canal (85 vs 108mm) + jumpier signal made the SAME 1.5x
            // spring the dome far past the tip, so it reads as bouncing off the penis. KK -> 1.0 = the dome
            // only keeps up with the tip (no overshoot). KKS stays 1.5.
            // the PENIS is game/BP-driven and its stop point is NOT
            // controlled here, so it strokes to its raw depth regardless of the dome. The dome must
            // therefore move AT LEAST as far as the tip's overshoot to avoid a poke: weight = overMM/travel,
            // so WombPush=1.0 = dome moves EXACTLY the overshoot (tip at the surface), <1 = tip pokes through
            // (0.90 fell 10% short - the dome trailed the tip), >1 = dome LEADS the tip (tip enclosed, still in
            // contact from inside). 1.15 keeps the tip enclosed with margin for the smoother's stroke lag.

            // on the default char almost all poses pushed the dome. The wanted MIX is: shallow poses fall
            // short, deep poses still poke). At 110 the fit landed every IN-RANGE pose's deepest push 10%
            // past the cervix, so nearly all poked. 100 lands the in-range poses AT the cervix (0 overshoot
            // = no dome push), while genuinely deep-reach poses (whose fit clamps at the 0.6× floor) still
            // overshoot and poke - natural variety, no per-pose logic. Live-tunable
            // (90-95 for clearer clearance on shallow poses, 110+ back to always-poke).


            _cfgHPullOutFlow = Config.Bind("Free-H", "Cum flows out on pull-out", true,
                "MAIN GAME only: when the penis withdraws with cum in the womb, the cum flows womb -> canal -> out the entrance over a few seconds (instead of just vanishing). The canal returns to empty afterward.");





            _cfgHBPDanOptions = Config.Bind("Free-H", "BP dan options override",
                "danLengthSquish=0.8; danGirthSquish=0.8; squishThreshold=0.40; danRadiusScale=1; danLengthScale=1; simplifyVaginal=True; simplifyOral=True; simplifyAnal=True; squishOralGirth=True; rotateTamaWithShaft=True; limitCorrection=False; maxCorrection=10",
                "MAIN GAME only: semicolon list of name=value pairs applied to the H male's BetterPenetration DanOptions when the womb is applied (BP has no per-male UI in Free-H). squishThreshold here is only the STARTING value — while the womb pin is active the plugin sweeps it per-frame between the 'Stroke shallow/deep point (mm)' settings (those are the depth control); danLengthSquish = how tightly the tip obeys the stroke points (0.8 = 80%, 1 = exact); danGirthSquish = how much the shaft thickens while compressed. To harvest values from a Studio male: press the apply hotkey in Studio and copy the BP-DANOPT log line. Empty = leave BP's defaults. simplifyVaginal is forced true afterwards while the pin is enabled.");





            _cfgBodyVeil = Config.Bind("AutoBodyReveal", "Also apply skin veil (BodyRevealExtra)", true,
                "When applying BodyReveal (hotkey or character-change), ALSO create the second body material copy with CloXray/BodyRevealExtra — the translucent-skin layer over the x-ray window with the 'XrayAlpha' strength slider (0=skin opaque, 1=raw x-ray). Two copies are required because one material has one render queue - the stamp draws before the organ, the veil after it; this just saves doing the second copy by hand. OFF = stamp only (the pre-veil look).");
            _cfgLimbMask = Config.Bind("AutoBodyReveal", "Hands and limbs block the x-ray", true,
                "ON (default): the torso mask stops the x-ray window at hands and limbs. OFF: the womb shows through everything, the old behavior. "
                + "Free-H has no MaterialEditor access, so this is the switch there; it re-applies live to the spawned womb's wearer.");
            _cfgLimbMask.SettingChanged += (s, e) => MainGameWomb.ReassertBodyRevealOnWearers();

            // (2026-06-10 cleanup) The IK-saga A/B toggles were removed once the fixes were verified:
            // skin-matrix cum scale, the post-IK onPreCull pose feed, and BakeMesh measurement are now
            // hard-wired (the OFF paths were the known-wrong legacy behaviors); the penis_target detach
            // toggle was diagnostic for a refuted theory.


            Configured = true;
            LogSettingsOnce();
            AutoBodyReveal.Enabled  = CfgEnabled && _cfgAutoBodyReveal.Value;
            AutoBodyReveal.Debug    = CfgDebugLog;   // honours ForceDiagnosticsForTester
            AutoBodyReveal.MaxRange = CfgAutoBodyRevealRange;
            AutoBodyReveal.Init();   // subscribe to CharacterReloaded (KKAPI soft-dep ensures it loads first)
            // Repair NC constraint paths saved against the pre-7.4.0 womb bone names. Must be armed
            // before any scene loads: NC drops an unresolvable constraint silently, so once the load
            // has run there is nothing left to fix. Inert without NodesConstraints installed.
            // The BP-interop Harmony patches (dan-dup guard + penis-FK enforcer) are NOT installed here. They are
            // CloXray-womb-scoped: installed lazily only once a womb is actually in the scene (WombExpandEffect.OnEnable,
            // the scene-load penis-bend coroutine, or a womb-gated CharacterReloaded), and their prefix/postfix also
            // early-out when no womb is present. A session that never uses a CloXray womb stays completely unpatched -
            // zero effect on BetterPenetration / the penis when the womb mod isn't in use.
            // No scan: the baked wobble component bootstraps WombExpandEffect on each item (EnsureWombExpand).
            // Persist hotkey-added wobble drivers (bottles etc.) with the scene, like ComponentUtil/MaterialEditor.
            try { KKAPI.Studio.SaveLoad.StudioSaveLoadApi.RegisterExtraBehaviour<WobbleSceneController>(Guid); }
            catch (System.Exception e) { _logger?.LogWarning("Wobble scene-persistence not registered (KKAPI/Studio unavailable?): " + e.Message); }
        }

        // BepInEx KeyboardShortcut.IsDown() has two accidental-miss traps - the RIGHT-side
        // Shift/Alt never matches a Left-key bind, and ANY unrelated held key (a camera/WASD key)
        // silently eats the press; that happened in practice (log showed zero ApplyAll dispatches).
        // This check fixes both while KEEPING stock's overlap courtesy: the held Shift/Alt/Ctrl
        // CLASSES must equal the bind's exactly (either physical side counts), so a press carrying an
        // extra modifier - likely another plugin's longer combo - still does NOT fire ours. The F1
        // ConfigurationManager rebind UI is untouched (same ConfigEntry<KeyboardShortcut>).
        private static bool ComboDown(KeyboardShortcut sc)
        {
            if (!Input.GetKeyDown(sc.MainKey)) return false;
            bool needShift = false, needAlt = false, needCtrl = false;
            foreach (var mod in sc.Modifiers)
            {
                switch (mod)
                {
                    case KeyCode.LeftShift:
                    case KeyCode.RightShift:   needShift = true; break;
                    case KeyCode.LeftAlt:
                    case KeyCode.RightAlt:     needAlt = true; break;
                    case KeyCode.LeftControl:
                    case KeyCode.RightControl: needCtrl = true; break;
                    default: if (!Input.GetKey(mod)) return false; break;   // exotic modifier: exact key
                }
            }
            bool shift = Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift);
            bool alt   = Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt);
            bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            return shift == needShift && alt == needAlt && ctrl == needCtrl;
        }

        // an on-screen readiness indicator, so the moment to switch character is visible.
        // shown ONLY while the research auto-sweep is running (no clutter in normal play). Colour by the
        // LEAST-covered animation of the CURRENT character: red = still thin, yellow = one more pass, green =
        // every reachable pose has >= target samples -> safe to stop and switch to the other girl.

        private void Update()
        {
            // Hotkey + live config only - NO per-frame scanning/applying (that's event-driven).
            // (PumpRegionMasks is a bounded queue, empty in steady state - it exists because ME's
            // shader flip is deferred and the torso mask must be set AFTER it lands.)
            MEBridge.PumpRegionMasks();
            MEBridge.PumpMaskWatch();       // masks cannot persist in ME records - re-assert after body rebuilds
            if (_cfgAutoBodyReveal != null)      AutoBodyReveal.Enabled  = CfgEnabled && _cfgAutoBodyReveal.Value;   // master OFF disables the auto-apply path too
            if (_cfgDebugLog != null)            AutoBodyReveal.Debug    = CfgDebugLog;   // honours ForceDiagnosticsForTester
            // One-shot respawn after the forced uncensor body reload (see MainGameWomb.RespawnAt):
            // the womb outlives the reload but stays bound to the old body, which read as "the first
            // hotkey press did nothing".
            if (MainGameWomb.DeferredSpawnAt > 0f && Time.unscaledTime >= MainGameWomb.DeferredSpawnAt)
            {
                bool ready = MainGameWomb.BpBodyReady(MainGameWomb.DeferredSpawnFemale);
                bool giveUp = MainGameWomb.DeferredSpawnDeadline > 0f && Time.unscaledTime >= MainGameWomb.DeferredSpawnDeadline;
                if (!ready && !giveUp) { MainGameWomb.DeferredSpawnAt = Time.unscaledTime + 0.1f; }   // keep polling
                else
                {
                MainGameWomb.DeferredSpawnAt = 0f;
                MainGameWomb.DeferredSpawnDeadline = giveUp ? 1f : 0f;
                if (CfgEnabled && !MainGameWomb.AnySpawned())
                {
                    if (MainGameWomb.DeferredSpawnDeadline > 0f)
                        _logger?.LogError("CloXray: the forced uncensor reload never reported completion — spawning the womb anyway; it may need one more hotkey press.");
                    else
                        _logger?.LogInfo("CloXray: body reload finished — spawning the womb now (one hotkey press).");
                    MainGameWomb.ToggleWhy = "deferred-spawn";
                    MainGameWomb.DeferredSpawnDeadline = 0f;
                    MainGameWomb.Toggle(this);
                }
                }
            }
            if (MainGameWomb.ChainDumpAt > 0f && Time.unscaledTime >= MainGameWomb.ChainDumpAt)
            {
                MainGameWomb.ChainDumpAt = 0f;
                if (CfgDebugLog && MainGameWomb.SMale != null)
                    MEBridge.DumpXrayChain(MainGameWomb.SMale, MainGameWomb.SFemale, MainGameWomb.SWomb);
            }
            // Free-H UI survey: one read-only dump per H scene so the x-ray button can be built
            // against the real hierarchy. Diagnostic-log gated, so release does nothing here.
            HSceneUIProbe.Pump();
            if (MainGameWomb.MeRefreshChara != null)
            {
                if (UncBodyReloadWatch.Done(MainGameWomb.MeRefreshChara))
                {
                    var who = MainGameWomb.MeRefreshChara;
                    MainGameWomb.MeRefreshChara = null; MainGameWomb.MeRefreshDeadline = 0f;
                    MEBridge.RefreshBodyEdits(who);
                }
                else if (Time.unscaledTime >= MainGameWomb.MeRefreshDeadline)
                {
                    _logger?.LogError("CloXray: the forced body reload on '" + MainGameWomb.MeRefreshChara.name
                        + "' never reported finishing, so their own body material edits were NOT restored. "
                        + "If their skin shader looks wrong, re-pick it in the MaterialEditor menu.");
                    MainGameWomb.MeRefreshChara = null; MainGameWomb.MeRefreshDeadline = 0f;
                }
            }
            if (MainGameWomb.RespawnAt > 0f && Time.unscaledTime >= MainGameWomb.RespawnAt)
            {
                MainGameWomb.RespawnAt = 0f;
                if (CfgEnabled && MainGameWomb.AnySpawned())
                {
                    _logger?.LogInfo("CloXray: respawning the H womb — " + MainGameWomb.RespawnWhy + ".");
                    MainGameWomb.ToggleWhy = "reload-respawn"; MainGameWomb.Toggle(this); MainGameWomb.ToggleWhy = "reload-respawn"; MainGameWomb.Toggle(this);
                }
            }
            // Armed from Update, NOT Awake: BepInEx loads NodesConstraints AFTER this plugin (log lines 158
            // vs 170), so at Awake its type does not exist. Cheap once installed (one bool), and
            // this runs many frames before a scene can be opened.
            if (MainGameWomb.IsStudio) NodeConstraintBridge.InstallScenePathMigration();
            AutoBodyReveal.MaxRange = CfgAutoBodyRevealRange;
            if (MainGameWomb.IsStudio)
            {
                if (CfgEnabled && _cfgAutoBodyRevealKey != null && ComboDown(_cfgAutoBodyRevealKey.Value))
                    AutoBodyReveal.ApplyAll();
                if (CfgEnabled && _cfgStudioPairKey != null && ComboDown(_cfgStudioPairKey.Value))
                    StudioPairWomb.Trigger();
                StudioPairWomb.Tick();   // finishes an add once the new item's own component is up

            }
            else
            {
                // MAIN GAME: BOTH hotkeys toggle the womb - the dedicated one (default Ctrl+Shift+W) and
                // the familiar Studio combo (default Shift+Alt+X; note another plugin's raw Alt+X check
                // also fires on it - accepted).
                bool down = (_cfgHToggleKey != null && ComboDown(_cfgHToggleKey.Value)) ||
                            (_cfgAutoBodyRevealKey != null && ComboDown(_cfgAutoBodyRevealKey.Value));
                if (CfgEnabled && down)
                    MainGameWomb.Toggle(this);


            }
        }

        // Bootstrap entry point called by the baked wobble component (LiquidWobbleMPBEffect.Start)
        // on each womb item. Finds the item root and attaches WombExpandEffect once. No scan, no
        // freshly-baked MonoScript (which crashed Unity's native loader).
        public static WombExpandEffect EnsureWombExpand(Transform from)
        {
            Transform root = FindItemRoot(from, AxisBone);
            if (root == null)
            {
                _logger?.LogError($"{Name}: cannot attach WombExpandEffect from '{(from ? from.name : "?")}' — no ancestor "
                    + $"contains the womb axis bone '{AxisBone}'. The zipmod does not match this DLL (womb 7.4.0+ renames "
                    + "its skeleton to clo_*). Nothing attached; the womb will not expand.");
                return null;
            }
            var fx = root.GetComponent<WombExpandEffect>();
            if (fx == null)
            {
                fx = root.gameObject.AddComponent<WombExpandEffect>();
                _logger?.LogInfo($"{Name}: WombExpandEffect attached to '{root.name}'.");
            }
            return fx;
        }

        public static Transform FindItemRoot(Transform from, string axisBone)
        {
            for (var c = from; c != null; c = c.parent)
            {
                // NEVER climb into a character. The womb is instantiated as a CHILD of the wearer, so an
                // unbounded walk that misses the item root lands on her ChaControl object and quietly makes
                // the whole character look like a womb item to every ancestor test. Stop here and
                // let the caller report it rather than attaching to the wrong object.
                if (c.GetComponent("ChaControl") != null) return null;
                var ts = c.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < ts.Length; i++)
                    if (ts[i] != null && ts[i].name == axisBone)
                        return c;
            }
            return null;
        }
    }
}
