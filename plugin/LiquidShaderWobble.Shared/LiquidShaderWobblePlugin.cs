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
        public const string Version = "1.1.0";

        // The gated view of the log sink: null = silent, and every `_logger?.LogX(...)` in the plugin is a
        // no-op.
        internal static ManualLogSource _logger;
        private static ManualLogSource _log;   // the real sink, handed out only while logging is on.

        private const string AxisBone = "cf_j_kokan";

        // --- Live config (BepInEx F1 menu). Read each frame by WombExpandEffect, so tweaks apply instantly.
        private static ConfigEntry<bool> _cfgEnabled;
        private static ConfigEntry<bool> _cfgDebugLog;
        private static ConfigEntry<bool> _cfgAutoBodyReveal;
        private static ConfigEntry<KeyboardShortcut> _cfgAutoBodyRevealKey;
        private static ConfigEntry<bool> _cfgBodyVeil;
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

        public static bool Configured { get; private set; }
        public static bool  CfgEnabled          => _cfgEnabled == null || _cfgEnabled.Value;   // master F1 toggle; absent/not-bound ->.
        public static bool  CfgDebugLog         => _cfgDebugLog != null && _cfgDebugLog.Value;
        public static bool  CfgBodyVeil         => _cfgBodyVeil == null || _cfgBodyVeil.Value;
        public static bool  CfgClothesReveal    => _cfgClothesReveal != null && _cfgClothesReveal.Value;
        public static bool  CfgReactColliders   => _cfgReactColliders != null && _cfgReactColliders.Value;
        public static float CfgHFillAmount      => _cfgHFillAmount != null ? _cfgHFillAmount.Value : 0f;
        public static bool  CfgHPullOutFlow     => _cfgHPullOutFlow == null || _cfgHPullOutFlow.Value;
        public static bool  CfgHAutoLength      => _cfgHAutoLength == null || _cfgHAutoLength.Value;
        public static float CfgHWombBack        => _cfgHWombBack != null ? _cfgHWombBack.Value : 5f;
        public static string CfgHBPDanOptions   => _cfgHBPDanOptions != null ? _cfgHBPDanOptions.Value : "";
        public const float CfgRingWeight       = 54f;
        public const float CfgEntranceWeight   = 52f;
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
        public const float CfgHForceStretch    = -1f;   // debug slider retired: -1 = off, permanently.
        public const float CfgHStrokeShallow   = 30f;
        public const float CfgHStrokeDeep      = 72f;
        public const float CfgHMoundDown       = 50f;
        public const float CfgHContactPct      = 85f;
        public const float CfgHInStrokePct     = 100f;
        public const float CfgHBaseStretchPct  = 0f;
        public const float CfgHWombPush        = 1.15f;
        public const bool  CfgHPenisBottomWindow = true;
        // The collider filter for the CURRENT game: Studio settings are validated and locked.
        public static string CfgColliderNameForGame => MainGameWomb.IsStudio ? CfgColliderName : CfgHColliderName;

        // Silent unless the F1 diagnostic switch is on. Called once the switch is bound and again whenever
        // it changes, so turning it on mid-session starts logging without a restart.
        private static void ApplyLogVisibility()
        {
            _logger = (ReleaseSilent && !CfgDebugLog) ? null : _log;
        }

        private void Awake()
        {
            // A shipped build runs SILENT: _logger stays null until ApplyLogVisibility below decides
            // otherwise, so nothing is logged during the binds either.
            _log = Logger;
            useGUILayout = false;

            _cfgEnabled = Config.Bind("General", "Enabled", true,
                "Master switch for the whole mod (ON by default). OFF = the plugin stops driving and stops reaching into the scene: wombs freeze (canal/liquid stop updating), no x-ray auto-apply on character load, and BetterPenetration + the penis FK are left exactly as you posed them. Takes effect live — no scene reload needed.");

            // Deliberately not the old "WombExpand / Debug Log" key: an existing config holding true for
            // that dead setting must not switch this one on.
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

            _cfgHWombBack = Config.Bind("Free-H", "Womb offset to back (mm)", 5f,
                new ConfigDescription("MAIN GAME only: shifts the whole womb along the female's backward direction (pelvis frame — follows the pose). Positive = toward her back. Use when the penis visibly hugs the canal's BACK wall (the mesh seat sits too far toward the belly on some bodies). Live.", new AcceptableValueRange<float>(-20f, 30f)));


            // all poked.


            _cfgHPullOutFlow = Config.Bind("Free-H", "Cum flows out on pull-out", true,
                "MAIN GAME only: when the penis withdraws with cum in the womb, the cum flows womb -> canal -> out the entrance over a few seconds (instead of just vanishing). The canal returns to empty afterward.");


            _cfgHBPDanOptions = Config.Bind("Free-H", "BP dan options override",
                "danLengthSquish=0.8; danGirthSquish=0.8; squishThreshold=0.40; danRadiusScale=1; danLengthScale=1; simplifyVaginal=True; simplifyOral=True; simplifyAnal=True; squishOralGirth=True; rotateTamaWithShaft=True; limitCorrection=False; maxCorrection=10",
                "MAIN GAME only: semicolon list of name=value pairs applied to the H male's BetterPenetration DanOptions when the womb is applied (BP has no per-male UI in Free-H). squishThreshold here is only the STARTING value — while the womb pin is active the plugin sweeps it per-frame between the 'Stroke shallow/deep point (mm)' settings (those are the depth control); danLengthSquish = how tightly the tip obeys the stroke points (0.8 = 80%, 1 = exact); danGirthSquish = how much the shaft thickens while compressed. To harvest values from a Studio male: press the apply hotkey in Studio and copy the BP-DANOPT log line. Empty = leave BP's defaults. simplifyVaginal is forced true afterwards while the pin is enabled.");


            _cfgBodyVeil = Config.Bind("AutoBodyReveal", "Also apply skin veil (BodyRevealExtra)", true,
                "When applying BodyReveal (hotkey or character-change), ALSO create the second body material copy with CloXray/BodyRevealExtra — the translucent-skin layer over the x-ray window with the 'XrayAlpha' strength slider (0=skin opaque, 1=raw x-ray). Two copies are required because one material has one render queue (stamp=2500, veil=3502); this just saves doing the second copy by hand. OFF = stamp only (the pre-veil look).");


            Configured = true;
            AutoBodyReveal.Enabled  = CfgEnabled && _cfgAutoBodyReveal.Value;
            AutoBodyReveal.Debug    = _cfgDebugLog.Value;
            AutoBodyReveal.MaxRange = CfgAutoBodyRevealRange;
            AutoBodyReveal.Init();   // subscribe to CharacterReloaded (KKAPI soft-dep ensures it loads first).
            // The BP-interop Harmony patches (dan-dup guard + penis-FK enforcer) are NOT installed here.
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
                    default: if (!Input.GetKey(mod)) return false; break;   // exotic modifier: exact key.
                }
            }
            bool shift = Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift);
            bool alt   = Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt);
            bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            return shift == needShift && alt == needAlt && ctrl == needCtrl;
        }


        private void Update()
        {
            // Hotkey + live config only - NO per-frame scanning/applying (that's event-driven).
            if (_cfgAutoBodyReveal != null)      AutoBodyReveal.Enabled  = CfgEnabled && _cfgAutoBodyReveal.Value;   // master OFF disables the auto-apply path too.
            if (_cfgDebugLog != null)            AutoBodyReveal.Debug    = _cfgDebugLog.Value;
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


            }
        }

        // Bootstrap entry point called by the baked wobble component (LiquidWobbleMPBEffect.Start) on each
        // womb item.
        public static WombExpandEffect EnsureWombExpand(Transform from)
        {
            Transform root = FindItemRoot(from, AxisBone);
            if (root == null)
                return null;
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
                var ts = c.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < ts.Length; i++)
                    if (ts[i] != null && ts[i].name == axisBone)
                        return c;
            }
            return null;
        }
    }
}
