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
        public const string Version = "1.1.2";

        // The gated view of the log sink: null = silent, and every `_logger?.LogX(...)` in the plugin is a
        // no-op.
        internal static ManualLogSource _logger;
        private static ManualLogSource _log;      // the real sink, handed out only when logging is on

        // The item's own entrance axis bone — used to find the item-scoped root to attach to
        // (the host character also has this bone, so we must stay inside the item subtree).
        // The WOMB's own axis bone, not hers. b901 renamed the item's skeleton to clo_* so it would
        // stop colliding with the character's bone names in ABMX; this const was missed in that pass
        // and kept naming HER bone. FindItemRoot climbs until a subtree contains this name, so with the
        // womb's copy gone the walk sailed past the womb and stopped at the CHARACTER - attaching
        // WombExpandEffect to chaF_001 itself. Everything that asks "is this transform inside a womb"
        // via GetComponentInParent<WombExpandEffect> then answered YES for her entire skeleton, and
        // FindTargetFemale (which excludes womb bones that way) could no longer find her at all.
        private static readonly string AxisBone = MainGameWomb.WombBone("cf_j_kokan");

        // --- Live config (BepInEx F1 menu). Read each frame by WombExpandEffect, so tweaks apply instantly.
        private static ConfigEntry<bool> _cfgEnabled;
        private static ConfigEntry<bool> _cfgDebugLog;
        private static ConfigEntry<bool> _cfgAutoBodyReveal;
        private static ConfigEntry<KeyboardShortcut> _cfgAutoBodyRevealKey;
        private static ConfigEntry<bool> _cfgBodyVeil;
        private static ConfigEntry<bool> _cfgLimbMask;
        private static ConfigEntry<bool> _cfgClothesReveal;
        private static ConfigEntry<bool>  _cfgReactColliders;
        private static ConfigEntry<float> _cfgHFillAmount;
        private static ConfigEntry<KeyboardShortcut> _cfgHToggleKey;
        private static ConfigEntry<bool>  _cfgHPullOutFlow;
        private static ConfigEntry<bool>  _cfgHAutoLength;
        private static ConfigEntry<float> _cfgHWombBack;
        private static ConfigEntry<string> _cfgHBPDanOptions;

        // false = CloXray's TWO BetterPenetration work-arounds are OFF, so the BP-source fixes can be
        // validated in isolation (otherwise the guards mask whether BP itself behaves).
        public const bool BPWorkaroundsEnabled = true;

        // Log output: true for a shipped build, false while testing.
        public const bool ReleaseSilent = true;

        // ⚠ TEST BUILD ONLY — SET BACK TO false BEFORE ANY RELEASE ⚠
        // Hands the log sink over and forces the diagnostic dumps on regardless of the F1 switch, so a
        // tester produces a full log without being talked through the menu. A shipped build must decide
        // this from the switch alone:
        public const bool ForceDiagnosticsForTester = false;

        public static bool Configured { get; private set; }
        public static bool  CfgEnabled          => _cfgEnabled == null || _cfgEnabled.Value;   // master F1 toggle; absent/not-bound -> on
        public static bool  CfgDebugLog         => ForceDiagnosticsForTester || (_cfgDebugLog != null && _cfgDebugLog.Value);
        public static bool  CfgBodyVeil         => _cfgBodyVeil == null || _cfgBodyVeil.Value;
        public static bool  CfgLimbMask         => _cfgLimbMask == null || _cfgLimbMask.Value;   // OFF = womb shows through hands/limbs (no torso mask)
        public static bool  CfgClothesReveal    => _cfgClothesReveal != null && _cfgClothesReveal.Value;
        public static bool  CfgReactColliders   => _cfgReactColliders != null && _cfgReactColliders.Value;
        public static float CfgHFillAmount      => _cfgHFillAmount != null ? _cfgHFillAmount.Value : 0f;
        public static bool  CfgHPullOutFlow     => _cfgHPullOutFlow == null || _cfgHPullOutFlow.Value;
        public static bool  CfgHAutoLength      => _cfgHAutoLength == null || _cfgHAutoLength.Value;
        public static float CfgHWombBack        => _cfgHWombBack != null ? _cfgHWombBack.Value : 2f;   // default mirrors the Bind above
        public static string CfgHBPDanOptions   => _cfgHBPDanOptions != null ? _cfgHBPDanOptions.Value : "";
        public const float CfgRingWeight       = 54f;
        public const float CfgEntranceWeight   = 52f;   // b741: was 58 — entrance ring slightly more closed
        public const float CfgCervixWeight     = 52f;
        public const float CfgDepthStart       = 0.10f;
        public const float CfgDepthEnd         = 0.97f;
        public const float CfgOpenWidth        = 0.18f;
        public const float CfgDepthSmoothing   = 12f;
        public const float CfgRefGirth         = 0.0213f;
        public const float CfgFullDepthIn      = 0.62f;
        public const float CfgStretchMax       = 20f;
        public const float CfgStretchStart     = 0.55f;
        public const float CfgStretchOvershoot = 110f;
        public const float CfgRefLength        = 0.10f;
        public const float CfgDirReact         = 25f;
        public const float CfgOpenLead         = 0.06f;
        public const float CfgCloseSmoothing   = 4f;
        public const float CfgEntranceOpenWidth  = 0.30f;
        public const float CfgEntranceCloseScale = 2f;
        public const float CfgOpenTime           = 0.2f;
        public const float CfgEntranceOpenScale  = 2f;
        public const float CfgMaxGirthScale      = 2.5f;
        public const float CfgVeilAlpha        = 0.9f;
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
        public const bool  CfgHPinEnable       = true;
        public const float CfgHStretchBoost    = 2f;
        public const string CfgHColliderName   = "cm_J_dan";
        public const float CfgHPressGain       = 80f;
        public const float CfgHFollowPct       = 60f;
        public const float CfgHForceStretch    = -1f;   // debug slider retired: -1 = off, permanently
        public const float CfgHStrokeShallow   = 30f;
        public const float CfgHStrokeDeep      = 72f;
        public const float CfgHMoundDown       = 50f;
        public const float CfgHContactPct      = 85f;
        public const float CfgHInStrokePct     = 100f;
        public const float CfgHBaseStretchPct  = 0f;
        public const float CfgHWombPush        = 1.15f;   // b674 DefaultWombPush — dome leads the tip slightly
        public const bool  CfgHPenisBottomWindow = true;
        // The collider filter for the CURRENT game: Studio settings are validated and locked.
        public static string CfgColliderNameForGame => MainGameWomb.IsStudio ? CfgColliderName : CfgHColliderName;

        // Silent unless the F1 diagnostic switch is on. Called once the switch is bound and again whenever
        // it changes, so turning it on mid-session starts logging without a restart.
        private static void ApplyLogVisibility()
        {
            _logger = (ReleaseSilent && !CfgDebugLog) ? null : _log;
        }


        // EVERY BUG REPORT SHOULD SAY WHAT THE SETTINGS WERE. A tester's config carries their own
        // history: BepInEx keeps a stored value even after we change a default, and people switch
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
        private void Awake()
        {
            // A shipped build runs SILENT: _logger stays null until ApplyLogVisibility below decides
            // otherwise, so nothing is logged during the binds either.
            _log = Logger;
            useGUILayout = false;

            _cfgEnabled = Config.Bind("General", "Enabled", true,
                "Master switch for the whole mod (ON by default). OFF = the plugin stops driving and stops reaching into the scene: wombs freeze (canal/liquid stop updating), no x-ray auto-apply on character load, and BetterPenetration + the penis FK are left exactly as you posed them. Takes effect live — no scene reload needed.");

            // A NEW key on purpose: 1.0 shipped a "WombExpand / Debug Log" that did nothing (the sink was
            // nulled unconditionally), so reusing it would switch the full log on for anyone whose old config happens to hold true.
            _cfgDebugLog = Config.Bind("General", "Diagnostic log (for bug reports)", false,
                "Writes what the plugin is doing to BepInEx/LogOutput.log: which penis it paired with which womb, the entry anchor and stencil pair it chose, the materials it stamped, and any warning that is otherwise silent. Event-driven, so it stays quiet until something happens — a scene load plus one hotkey press is a few hundred lines. OFF by default: a shipped build logs nothing at all. Turn it on, reproduce the problem, then send the log. Takes effect live.");
            ApplyLogVisibility();
            _cfgDebugLog.SettingChanged += (s, e) => ApplyLogVisibility();

            const string brSec = "AutoBodyReveal";
            _cfgAutoBodyReveal = Config.Bind(brSec, "Enable", true,
                "Auto-apply the CloXray BodyReveal (x-ray body stencil) via MaterialEditor when a character loads/swaps, IF a CloXray womb is near its vagina. The womb's organ stencil is matched automatically. Per-body: only characters with a womb nearby are touched.");
            _cfgAutoBodyRevealKey = Config.Bind(brSec, "Apply Now Hotkey", new KeyboardShortcut(KeyCode.X, KeyCode.LeftShift, KeyCode.LeftAlt),
                "Manually (re)apply BodyReveal to every character near a CloXray womb. Use after first placing a womb, or after dragging one onto a different character. (Shift+Alt+X. Rebind here if it ever collides with another plugin.)");

            _cfgReactColliders = Config.Bind("WombExpand", "React to colliders", true,
                "When NO BP penis is engaged, also react to the nearest DynamicBoneCollider (e.g. a BP collider you place and size with KKPE on a bottle/toy). The womb opens and displaces from the collider's deeper capsule end (treated as the tip) and its radius (girth), exactly like a penis. READ-ONLY: the collider itself is never modified. A real BP penis always takes priority.");

            _cfgClothesReveal = Config.Bind("AutoBodyReveal", "Also stamp worn clothes (x-ray through clothes)", true,
                "When applying BodyReveal (hotkey or character-change), ALSO stamp every WORN torso garment (top/bot/bra/shorts/panst; only clothes that are ON) with a BodyReveal material copy at the matching pair stencil — the womb x-rays through the clothes and the out-of-body bleed disappears at stamped pixels. ME persists the copies per scene/card. A freshly equipped garment needs another hotkey press. OFF = body only (clothes hide the shell; interior/cum may bleed unless the womb's OutBodySceneConfine is on).");


            _cfgHFillAmount = Config.Bind("Free-H", "Liquid fill amount", 0f,
                new ConfigDescription("MAIN GAME only: the womb's liquid fill level (Material Editor has no H-scene UI, so the fill is set here instead). 0 = empty (default) — raise it when you want cum. Live: drag mid-scene and the liquid follows. In Studio this does nothing (use Material Editor there).", new AcceptableValueRange<float>(0f, 1f)));


            _cfgHToggleKey = Config.Bind("Free-H", "Toggle womb hotkey", new KeyboardShortcut(KeyCode.W, KeyCode.LeftShift, KeyCode.LeftAlt),
                "MAIN GAME only: toggles the womb on the H female (spawn/remove + x-ray). Moved OFF Shift+Alt+X because another plugin's raw Alt+X check also fires on that combo (hiding the penis). Studio keeps its own hotkey.");


            _cfgHAutoLength = Config.Bind("Free-H", "Auto penis length (fit animation)", true,
                "MAIN GAME only: slowly fits the penis's natural length to the current animation (constant during play — the size NEVER pulses with the stroke). Too long for the animation (tip parked at the womb, only compressing) -> shortened until its own motion shows as a real stroke; too short (never reaches the womb) -> lengthened until the deepest push just reaches it. Adapts over a few seconds per animation/position change; restored on womb toggle-off. OFF = the legacy squish-sweep stroke.");

            // This value is KK's offset; KKS subtracts its own SeatForwardMM so each game keeps a
            // seat tuned by eye on its own body/uncensor meshes. Direction note: RAISING it moves
            // the entrance toward her belly - the kokan bone's forward axis points into the body,
            // so the shift runs opposite to what the name suggests.
            _cfgHWombBack = Config.Bind("Free-H", "Womb offset to back (mm)", 2f,
                new ConfigDescription("MAIN GAME only: shifts the whole womb along her front/back axis (pelvis frame — follows the pose). LOWER = toward her BACK, higher = toward her belly (the kokan bone's forward axis points into her body, so the shift runs the opposite way to what the name suggests — verified on screen 2026-08-07). Live.", new AcceptableValueRange<float>(-20f, 30f)));


            // all poked.


            _cfgHPullOutFlow = Config.Bind("Free-H", "Cum flows out on pull-out", true,
                "MAIN GAME only: when the penis withdraws with cum in the womb, the cum flows womb -> canal -> out the entrance over a few seconds (instead of just vanishing). The canal returns to empty afterward.");


            _cfgHBPDanOptions = Config.Bind("Free-H", "BP dan options override",
                "danLengthSquish=0.8; danGirthSquish=0.8; squishThreshold=0.40; danRadiusScale=1; danLengthScale=1; simplifyVaginal=True; simplifyOral=True; simplifyAnal=True; squishOralGirth=True; rotateTamaWithShaft=True; limitCorrection=False; maxCorrection=10",
                "MAIN GAME only: semicolon list of name=value pairs applied to the H male's BetterPenetration DanOptions when the womb is applied (BP has no per-male UI in Free-H). squishThreshold here is only the STARTING value — while the womb pin is active the plugin sweeps it per-frame between the 'Stroke shallow/deep point (mm)' settings (those are the depth control); danLengthSquish = how tightly the tip obeys the stroke points (0.8 = 80%, 1 = exact); danGirthSquish = how much the shaft thickens while compressed. To harvest values from a Studio male: press the apply hotkey in Studio and copy the BP-DANOPT log line. Empty = leave BP's defaults. simplifyVaginal is forced true afterwards while the pin is enabled.");


            _cfgBodyVeil = Config.Bind("AutoBodyReveal", "Also apply skin veil (BodyRevealExtra)", true,
                "When applying BodyReveal (hotkey or character-change), ALSO create the second body material copy with CloXray/BodyRevealExtra — the translucent-skin layer over the x-ray window with the 'XrayAlpha' strength slider (0=skin opaque, 1=raw x-ray). Two copies are required because one material has one render queue (stamp=2500, veil=3502); this just saves doing the second copy by hand. OFF = stamp only (the pre-veil look).");
            _cfgLimbMask = Config.Bind("AutoBodyReveal", "Hands and limbs block the x-ray", true,
                "ON (default): the torso mask stops the x-ray window at hands and limbs. OFF: the womb shows through everything, the old behavior. "
                + "Free-H has no MaterialEditor access, so this is the switch there; it re-applies live to the spawned womb's wearer.");
            _cfgLimbMask.SettingChanged += (s, e) => MainGameWomb.ReassertBodyRevealOnWearers();

            // (2026-06-10 cleanup) The IK-saga A/B toggles were removed once the fixes were verified:
            // skin-matrix cum scale, the post-IK onPreCull pose feed, and BakeMesh measurement are now
            // hard-wired (the OFF paths were the known-wrong legacy behaviors); the penis_target detach
            // toggle was diagnostic for a refuted theory.

#if CLOXRAY_RESEARCH
            _cfgDebugLog.Value = true;   // research builds: verbose diagnostics always on
#endif

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
            // early-out when no womb is present. A session that never uses a CloXray womb stays completely unpatched —
            // zero effect on BetterPenetration / the penis when our womb mod isn't in use.
            // No scan: the baked wobble component bootstraps WombExpandEffect on each item (EnsureWombExpand).
            // Persist hotkey-added wobble drivers (bottles etc.) with the scene, like ComponentUtil/MaterialEditor.
            try { KKAPI.Studio.SaveLoad.StudioSaveLoadApi.RegisterExtraBehaviour<WobbleSceneController>(Guid); }
            catch (System.Exception e) { _logger?.LogWarning("Wobble scene-persistence not registered (KKAPI/Studio unavailable?): " + e.Message); }
        }

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

        // b715 — "draw me a dot so I know when to switch character". On-screen readiness indicator,
        // shown ONLY while the research auto-sweep is running (no clutter in normal play). Colour by the
        // LEAST-covered animation of the CURRENT character: red = still thin, yellow = one more pass, green =
        // every reachable pose has >= target samples -> safe to stop and switch to the other girl.
#if CLOXRAY_RESEARCH   // 1.1 release strip: research scaffolding compiles only in research builds (add CLOXRAY_RESEARCH to DefineConstants)
        private void OnGUI()
        {
            if (!MainGameWomb.AutoCollectActive) return;
            int canalMM, poses, ready, minC, passNo;
            MainGameWomb.ResearchDot(out canalMM, out poses, out ready, out minC, out passNo);
            int target = MainGameWomb.ResearchTargetPerPose;
            // b716: judge by the BULK, not the single worst pose — a few poses that rarely penetrate would
            // otherwise pin it red forever. GREEN once ~all reachable poses have the target; the laggard
            // tail is expected and shown as a count.
            int laggards = poses - ready;
            float frac = poses > 0 ? (float)ready / poses : 0f;
            // b747/b748 the dot shows TOTAL progress; GREEN = "we have looped twice and most
            // scenes are verified" — passNo>=3 means two FULL passes completed, and the 85% bulk floor
            // tolerates the barely-penetrating stragglers that can never verify on small characters
            // (mid-girl proof: 78/83 keys, the 5 stragglers all physically-thin poses). Counting is
            // still loop-keys-only, so this can no longer report green at 46% real verification.
            bool done = poses >= 4 && ((passNo >= 3 && frac >= 0.85f) || laggards == 0 && passNo >= 2);
            Color c = poses < 4 ? Color.gray
                    : done ? new Color(0.25f, 0.9f, 0.35f)
                    : frac >= 0.5f ? new Color(1f, 0.85f, 0.2f)
                    : new Color(1f, 0.35f, 0.35f);
            if (_dotTex == null) { _dotTex = new Texture2D(1, 1); _dotTex.SetPixel(0, 0, Color.white); _dotTex.Apply(); }
            if (_dotStyle == null) { _dotStyle = new GUIStyle(); _dotStyle.fontSize = 15; _dotStyle.fontStyle = FontStyle.Bold; _dotStyle.alignment = TextAnchor.MiddleLeft; }
            var oc = GUI.color; GUI.color = c;
            GUI.DrawTexture(new Rect(16f, 16f, 22f, 22f), _dotTex);
            GUI.color = oc;
            string msg = poses == 0
                ? "CloXray REC — waiting for the first pose…"
                : "CloXray REC  char " + canalMM + "mm   TOTAL " + Mathf.RoundToInt(frac * 100f) + "% (" + ready + "/" + poses + " keys x" + target + ")"
                  + (laggards > 0 ? "  " + laggards + " to go" : "") + "   pass " + passNo
                  + (done ? (laggards > 0 ? "   >>> FINISHED (" + laggards + " physically thin) — stop & switch" : "   >>> FINISHED — stop & switch character") : "   keep sweeping");
            var r = new Rect(46f, 15f, 1000f, 26f);
            _dotStyle.normal.textColor = Color.black; GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), msg, _dotStyle);
            _dotStyle.normal.textColor = Color.white; GUI.Label(r, msg, _dotStyle);
        }
#endif   // CLOXRAY_RESEARCH

        private void Update()
        {
            // Hotkey + live config only — NO per-frame scanning/applying (that's event-driven).
            // (PumpRegionMasks is a bounded queue, empty in steady state — it exists because ME's
            // shader flip is deferred and the torso mask must be set AFTER it lands, see b921.)
            MEBridge.PumpRegionMasks();
            MEBridge.PumpMaskWatch();       // masks cannot persist in ME records - re-assert after body rebuilds (b942)
            if (_cfgAutoBodyReveal != null)      AutoBodyReveal.Enabled  = CfgEnabled && _cfgAutoBodyReveal.Value;   // master OFF disables the auto-apply path too
            if (_cfgDebugLog != null)            AutoBodyReveal.Debug    = CfgDebugLog;   // honours ForceDiagnosticsForTester
            // One-shot respawn after our own forced uncensor body reload (see MainGameWomb.RespawnAt):
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
            // Armed from Update, NOT Awake: BepInEx loads NodesConstraints AFTER us (log lines 158
            // vs 170), so at Awake its type does not exist. Cheap once installed (one bool), and
            // this runs many frames before a user can open a scene.
            if (MainGameWomb.IsStudio) NodeConstraintBridge.InstallScenePathMigration();
            AutoBodyReveal.MaxRange = CfgAutoBodyRevealRange;
            if (MainGameWomb.IsStudio)
            {
                if (CfgEnabled && _cfgAutoBodyRevealKey != null && ComboDown(_cfgAutoBodyRevealKey.Value))
                    AutoBodyReveal.ApplyAll();
            }
            else
            {
                // MAIN GAME: both hotkeys toggle the womb - the dedicated one (default Shift+Alt+W) and the
                // familiar Studio combo.
                bool down = (_cfgHToggleKey != null && ComboDown(_cfgHToggleKey.Value)) ||
                            (_cfgAutoBodyRevealKey != null && ComboDown(_cfgAutoBodyRevealKey.Value));
                if (CfgEnabled && down)
                    MainGameWomb.Toggle(this);

#if CLOXRAY_RESEARCH   // 1.1 release strip: research scaffolding compiles only in research builds (add CLOXRAY_RESEARCH to DefineConstants)
                // ROUTE-B PROTOTYPE hotkey (TEMPORARY — strip once a winner is picked, like the old pin
                // toggle). Shift+Alt+B flips womb placement between MIRROR (per-frame copy, shipped) and
                // REBIND (native skinning off her bones) and respawns any live womb so the two can be
                // compared back-to-back in one scene. Deliberately a hotkey, not an F1 setting, so it
                // carries no shipped-defaults obligation.
                if (CfgEnabled && Input.GetKeyDown(KeyCode.B)
                    && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
                {
                    MainGameWomb.UseRebind = !MainGameWomb.UseRebind;
                    MainGameWomb.ManualBake = false;   // b621: mode toggle re-arms the auto-parity loop
                    bool had = MainGameWomb.AnySpawned();
                    _logger?.LogWarning("CloXray: womb placement -> " + (MainGameWomb.UseRebind
                        ? "REBIND (route B — Unity skins the womb from her bones; no per-frame mirror)"
                        : "MIRROR (route A — per-frame pose copy; the shipped path)")
                        + (had ? " — respawning the live womb." : " — toggle a womb on to see it."));
                    if (had) { MainGameWomb.Toggle(this); MainGameWomb.Toggle(this); }
                }
#endif   // CLOXRAY_RESEARCH

#if CLOXRAY_RESEARCH   // 1.1 release strip: research scaffolding compiles only in research builds (add CLOXRAY_RESEARCH to DefineConstants)
                // b609 CONTROL-AUTHORITY hotkeys (TEMPORARY, prototype only). Command a KNOWN delta and
                // check the womb moves exactly that much — proves placement/scale authority before we
                // argue about what the right target is. All in HER pelvis frame. Each press re-bakes.
                // b611: LETTER keys only. Arrows were eaten by the game (b609) and so were
                // Insert/Delete/End/Backspace (b610) — but Shift+Alt+B (the mode toggle) has always
                // worked, so letters demonstrably reach the plugin. Step 10mm to be obvious on screen.
                // Shift+Alt+M / N : +/- 10mm ALONG THE CANAL (her up)
                // Shift+Alt+K / J : +/- 10mm along her FORWARD axis
                // Shift+Alt+O / L : scale x1.05 / /1.05
                // Shift+Alt+G : reset nudge + scale
                // The old PageUp/PageDown/Home are kept as alternates (they did work in b609).
                if (CfgEnabled
                    && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
                {
                    // b692 (research, temporary): Shift+Alt+P = auto-collect — walk every H pose and
                    // record one settled research sample each, so the campaign doesn't need manual clicking.
                    if (Input.GetKeyDown(KeyCode.P)) MainGameWomb.ToggleAutoCollect(this);
                    bool changed = false;
                    if (Input.GetKeyDown(KeyCode.M)) { MainGameWomb.RebindNudgeMM += new Vector3(0f, 10f, 0f); changed = true; }
                    if (Input.GetKeyDown(KeyCode.N)) { MainGameWomb.RebindNudgeMM -= new Vector3(0f, 10f, 0f); changed = true; }
                    // b620: X axis (her right/left) — the ONE axis the authority test never exercised, and
                    // the auto-parity residuals suggest it does not respond. H = +10mm right, F = -10mm.
                    if (Input.GetKeyDown(KeyCode.H)) { MainGameWomb.RebindNudgeMM += new Vector3(10f, 0f, 0f); changed = true; }
                    if (Input.GetKeyDown(KeyCode.F)) { MainGameWomb.RebindNudgeMM -= new Vector3(10f, 0f, 0f); changed = true; }
                    if (Input.GetKeyDown(KeyCode.K)) { MainGameWomb.RebindNudgeMM += new Vector3(0f, 0f, 10f); changed = true; }
                    if (Input.GetKeyDown(KeyCode.J)) { MainGameWomb.RebindNudgeMM -= new Vector3(0f, 0f, 10f); changed = true; }
                    // b639: dial how much of the dome's narrow tip/neck the FILL VOLUME ignores.
                    // T = include more (looser clip), R = ignore more (tighter clip). Re-measures live.
                    if (Input.GetKeyDown(KeyCode.T) || Input.GetKeyDown(KeyCode.R))
                    {
                        LiquidWobbleMPBEffect.CoreFrac = Mathf.Clamp(
                            LiquidWobbleMPBEffect.CoreFrac + (Input.GetKeyDown(KeyCode.T) ? -0.05f : 0.05f), 0.30f, 0.98f);
                        LiquidWobbleMPBEffect.InvalidateCoreBand();
                        _logger?.LogWarning("CloXray: CORE-FRAC -> " + LiquidWobbleMPBEffect.CoreFrac.ToString("F2")
                            + " (T = include more of the tip, R = ignore more). Watch the fill and rotate.");
                    }
                    if (Input.GetKeyDown(KeyCode.O)) { MainGameWomb.RebindScaleMul *= 1.05f; changed = true; }
                    if (Input.GetKeyDown(KeyCode.L)) { MainGameWomb.RebindScaleMul /= 1.05f; changed = true; }
                    bool resetKey = false;
                    if (Input.GetKeyDown(KeyCode.G)) { MainGameWomb.RebindNudgeMM = Vector3.zero; MainGameWomb.RebindScaleMul = 1f; changed = true; resetKey = true; }
                    // alternates that were confirmed working in b609
                    if (Input.GetKeyDown(KeyCode.PageUp))   { MainGameWomb.RebindScaleMul *= 1.05f; changed = true; }
                    if (Input.GetKeyDown(KeyCode.PageDown)) { MainGameWomb.RebindScaleMul /= 1.05f; changed = true; }
                    if (Input.GetKeyDown(KeyCode.Home))     { MainGameWomb.RebindNudgeMM = Vector3.zero; MainGameWomb.RebindScaleMul = 1f; changed = true; resetKey = true; }
                    if (changed)
                    {
                        // b621: a manual command DISARMS the auto loop (it re-ran on every respawn and
                        // instantly overrode the commanded values — the user's "jumped then re-pinned").
                        // G/Home re-arm it.
                        MainGameWomb.ManualBake = !resetKey;
                        MainGameWomb.NudgeVerified = false;   // b626: any manual change (or G re-learn) invalidates the cached correction
                        _logger?.LogWarning("CloXray: COMMANDED nudge=" + MainGameWomb.RebindNudgeMM.ToString("F1")
                            + "mm scaleMul=" + MainGameWomb.RebindScaleMul.ToString("F3")
                            + (MainGameWomb.ManualBake ? " [MANUAL — auto-parity disarmed; Shift+Alt+G to re-arm]" : " [auto-parity re-armed]")
                            + (MainGameWomb.UseRebind ? " — re-baking." : " — (REBIND mode is OFF; press Shift+Alt+B to see any effect)"));
                        if (MainGameWomb.AnySpawned()) { MainGameWomb.Toggle(this); MainGameWomb.Toggle(this); }
                    }
                }
#endif   // CLOXRAY_RESEARCH
            }
        }

        // Bootstrap entry point called by the baked wobble component (LiquidWobbleMPBEffect.Start) on each
        // womb item.
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
                // the whole character look like a womb item to every ancestor test we run. Stop here and
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
