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
        public const string Version = "1.0.0";

        internal static ManualLogSource _logger;

        // The item's own entrance axis bone — used to find the item-scoped root to attach to
        // (the host character also has this bone, so we must stay inside the item subtree).
        private const string AxisBone = "cf_j_kokan";

        // --- Live config (BepInEx F1 menu). Read each frame by WombExpandEffect, so tweaks
        //     apply instantly. Per-object strength/dampening are NOT here — they're inert
        //     blendshapes (BP_Strength / BP_Dampening) so they're per-item and save with the scene. ---
        private static ConfigEntry<float> _cfgRingWeight;
        private static ConfigEntry<float> _cfgEntranceWeight;
        private static ConfigEntry<float> _cfgCervixWeight;
        private static ConfigEntry<float> _cfgDepthStart;
        private static ConfigEntry<float> _cfgDepthEnd;
        private static ConfigEntry<float> _cfgOpenWidth;
        private static ConfigEntry<float> _cfgDepthSmoothing;
        private static ConfigEntry<float> _cfgRefGirth;
        private static ConfigEntry<float> _cfgFullDepthIn;
        private static ConfigEntry<float> _cfgStretchMax;
        private static ConfigEntry<float> _cfgStretchStart;
        private static ConfigEntry<float> _cfgStretchOvershoot;
        private static ConfigEntry<float> _cfgRefLength;
        private static ConfigEntry<float> _cfgDirReact;
        private static ConfigEntry<float> _cfgOpenLead;
        private static ConfigEntry<float> _cfgCloseSmoothing;
        private static ConfigEntry<float> _cfgEntranceOpenWidth;
        private static ConfigEntry<float> _cfgEntranceCloseScale;
        private static ConfigEntry<float> _cfgOpenTime;
        private static ConfigEntry<float> _cfgEntranceOpenScale;
        private static ConfigEntry<float> _cfgMaxGirthScale;
        private static ConfigEntry<bool> _cfgEnabled;
        private static ConfigEntry<bool> _cfgDebugLog;
        private static ConfigEntry<bool> _cfgAutoBodyReveal;
        private static ConfigEntry<KeyboardShortcut> _cfgAutoBodyRevealKey;
        private static ConfigEntry<float> _cfgAutoBodyRevealRange;
        private static ConfigEntry<bool> _cfgBodyVeil;
        private static ConfigEntry<bool> _cfgClothesReveal;
        private static ConfigEntry<float> _cfgVeilAlpha;
        private static ConfigEntry<float> _cfgPairRange;
        private static ConfigEntry<bool>  _cfgReactColliders;
        private static ConfigEntry<float> _cfgColliderRange;
        private static ConfigEntry<float> _cfgColliderInCanal;
        private static ConfigEntry<float> _cfgTipDetach;
        private static ConfigEntry<float> _cfgEntryDetach;
        private static ConfigEntry<float> _cfgColliderMaxRadius;
        private static ConfigEntry<string> _cfgColliderName;

        public static bool Configured { get; private set; }
        public static bool  CfgEnabled          => _cfgEnabled == null || _cfgEnabled.Value;   // master F1 toggle; absent/not-bound -> on
        public static float CfgRingWeight       => _cfgRingWeight.Value;
        public static float CfgEntranceWeight   => _cfgEntranceWeight.Value;
        public static float CfgCervixWeight     => _cfgCervixWeight != null ? _cfgCervixWeight.Value : 25f;
        public static float CfgDepthStart       => _cfgDepthStart.Value;
        public static float CfgDepthEnd         => _cfgDepthEnd.Value;
        public static float CfgOpenWidth        => _cfgOpenWidth.Value;
        public static float CfgDepthSmoothing   => _cfgDepthSmoothing.Value;
        public static float CfgRefGirth         => _cfgRefGirth.Value;
        public static float CfgFullDepthIn      => _cfgFullDepthIn.Value;
        public static float CfgStretchMax       => _cfgStretchMax.Value;
        public static float CfgStretchStart     => _cfgStretchStart.Value;
        public static float CfgStretchOvershoot => _cfgStretchOvershoot.Value;
        public static float CfgRefLength        => _cfgRefLength != null ? _cfgRefLength.Value : 0.10f;
        public static float CfgDirReact         => _cfgDirReact != null ? _cfgDirReact.Value : 25f;
        public static float CfgOpenLead           => _cfgOpenLead != null ? _cfgOpenLead.Value : 0.06f;
        public static float CfgCloseSmoothing     => _cfgCloseSmoothing != null ? _cfgCloseSmoothing.Value : 4f;
        public static float CfgEntranceOpenWidth  => _cfgEntranceOpenWidth != null ? _cfgEntranceOpenWidth.Value : 0.30f;
        public static float CfgEntranceCloseScale => _cfgEntranceCloseScale != null ? _cfgEntranceCloseScale.Value : 2f;
        public static float CfgOpenTime           => _cfgOpenTime != null ? _cfgOpenTime.Value : 0.2f;
        public static float CfgEntranceOpenScale  => _cfgEntranceOpenScale != null ? _cfgEntranceOpenScale.Value : 2f;
        public static float CfgMaxGirthScale      => _cfgMaxGirthScale != null ? _cfgMaxGirthScale.Value : 2.5f;
        public static bool  CfgDebugLog         => _cfgDebugLog.Value;
        public static bool  CfgBodyVeil         => _cfgBodyVeil == null || _cfgBodyVeil.Value;
        public static bool  CfgClothesReveal    => _cfgClothesReveal != null && _cfgClothesReveal.Value;
        public static float CfgVeilAlpha        => _cfgVeilAlpha != null ? _cfgVeilAlpha.Value : 0.9f;
        public static float CfgPairRange        => _cfgPairRange != null ? _cfgPairRange.Value : 0.5f;
        public static float CfgAutoBodyRevealRange => _cfgAutoBodyRevealRange != null ? _cfgAutoBodyRevealRange.Value : 0.15f;
        public static float CfgTipDetach        => _cfgTipDetach != null ? _cfgTipDetach.Value : 0.20f;
        public static float CfgEntryDetach      => _cfgEntryDetach != null ? _cfgEntryDetach.Value : 0.05f;
        public static bool  CfgReactColliders   => _cfgReactColliders != null && _cfgReactColliders.Value;
        public static float CfgColliderRange    => _cfgColliderRange != null ? _cfgColliderRange.Value : 0.3f;
        public static float CfgColliderInCanal  => _cfgColliderInCanal != null ? _cfgColliderInCanal.Value : 0.045f;
        public static float CfgColliderMaxRadius => _cfgColliderMaxRadius != null ? _cfgColliderMaxRadius.Value : 0.06f;
        public static string CfgColliderName    => _cfgColliderName != null ? _cfgColliderName.Value : "";

        private void Awake()
        {
            // Logging is development-only. RELEASE ships with `_logger = null`, which makes every `_logger?.LogX(...)`
            // call across the plugin a no-op — no log-file output at all. To debug, COMMENT OUT the `_logger = null;`
            // line below and rebuild to restore the full diagnostic log, then re-add it for the release build.
            _logger = Logger;
            _logger = null;

            _cfgEnabled = Config.Bind("General", "Enabled", true,
                "Master switch for the whole mod (ON by default). OFF = the plugin stops driving and stops reaching into the scene: wombs freeze (canal/liquid stop updating), no x-ray auto-apply on character load, and BetterPenetration + the penis FK are left exactly as you posed them. Takes effect live — no scene reload needed.");

            const string sec = "WombExpand";
            _cfgRingWeight     = Config.Bind(sec, "Ring Weight", 66f,
                new ConfigDescription("Full-open weight of the tube-BODY rings (V2-V4). The canal blendshape was widened ~30% (100 = ~64mm dia max), so ~66 now = ~45mm lumen (snug on the ~43mm default penis); girthScale opens it toward the wider 100 for bigger/deeper objects.", new AcceptableValueRange<float>(0f, 100f)));
            _cfgEntranceWeight = Config.Bind(sec, "Entrance Weight", 66f,
                new ConfigDescription("Full-open weight of the ENTRANCE ring (Vagina_1_open) — the dedicated entrance opener at the very bottom (a radial push, not part of the smooth tube). ~66 matches the snug default fit after the ~30% canal widen.", new AcceptableValueRange<float>(0f, 100f)));
            _cfgCervixWeight   = Config.Bind(sec, "Cervix Weight", 62f,
                new ConfigDescription("Full-open weight of the deepest ring (Vagina_5_entrance_open) — a tube radial-target + rounded dome for the TOP/cervix. ~62 opens the top to the snug default after the ~30% widen; lower it to keep a cervix neck.", new AcceptableValueRange<float>(0f, 100f)));
            _cfgDepthStart     = Config.Bind(sec, "Depth Start", 0.10f,
                new ConfigDescription("Normalized depth at which the entrance ring (V1, very bottom) opens. Low so the entrance opens early, with BP entry.", new AcceptableValueRange<float>(0f, 1f)));
            _cfgDepthEnd       = Config.Bind(sec, "Depth End", 0.97f,
                new ConfigDescription("Normalized depth of the deepest ring, the cervix (its canal position). 0.97 so it opens only at near-full insertion.", new AcceptableValueRange<float>(0f, 1f)));
            _cfgOpenWidth      = Config.Bind(sec, "Open Width", 0.18f,
                new ConfigDescription("Leading-edge softness in normalized depth. Wider = more gradual opening (like BP); smaller = sharper sequential opening.", new AcceptableValueRange<float>(0.02f, 1f)));
            _cfgDepthSmoothing = Config.Bind(sec, "Depth Smoothing", 12f,
                new ConfigDescription("How quickly the measured depth follows the penis (higher = snappier).", new AcceptableValueRange<float>(1f, 30f)));
            _cfgRefGirth       = Config.Bind(sec, "Reference Girth", 0.0213f,
                "BP penis radius of the DEFAULT penis. Ring width scales by (actual girth / this), so bigger/deeper penises open wider automatically.");
            _cfgFullDepthIn    = Config.Bind(sec, "Full Depth In", 0.62f,
                new ConfigDescription("BP visual depth at which the tip sits at the womb mouth (= normalized 1.0). Calibrate so the deepest ring just opens at full insertion.", new AcceptableValueRange<float>(0.3f, 1f)));
            _cfgStretchMax     = Config.Bind(sec, "Stretch Max", 20f,
                new ConfigDescription("womb_displace weight reached as the tip arrives at the womb mouth.", new AcceptableValueRange<float>(0f, 100f)));
            _cfgStretchStart   = Config.Bind(sec, "Stretch Start", 0.55f,
                new ConfigDescription("Normalized depth where womb_displace starts (later = higher). 0.55 = the womb holds its shape until past half insertion, then gives way -- so the tip travels deeper before it starts pushing.", new AcceptableValueRange<float>(0f, 1f)));
            _cfgStretchOvershoot = Config.Bind(sec, "Stretch Overshoot", 110f,
                new ConfigDescription("Extra womb_displace weight per unit of overshoot past the womb mouth (so a deeper push displaces the womb hard to keep the tip inside).", new AcceptableValueRange<float>(0f, 300f)));
            _cfgRefLength      = Config.Bind(sec, "Reference Length (m)", 0.10f,
                new ConfigDescription("BP penis LENGTH (m_baseDanLength) of your DEFAULT penis. womb_displace scales by baseLen/this — a longer / deeper-set penis displaces the womb more (clamped 0.6..2). Set this to the baseLen shown in the WombExpand debug log.", new AcceptableValueRange<float>(0.02f, 0.5f)));
            _cfgDirReact       = Config.Bind(sec, "Direction Reaction", 25f,
                new ConfigDescription("How far the entrance/mouth leans toward the INCOMING PENIS DIRECTION (drives moundforward/moundback from BP's tip direction). 0 = off; ~25 = a little.", new AcceptableValueRange<float>(0f, 100f)));
            _cfgOpenLead       = Config.Bind(sec, "Open Lead", 0.06f,
                new ConfigDescription("ANTICIPATION: how far AHEAD (normalized depth) every ring reaches full before the penis arrives. 0 = opens exactly at the ring; higher = opens earlier (ahead of entry).", new AcceptableValueRange<float>(0f, 0.5f)));
            _cfgCloseSmoothing = Config.Bind(sec, "Close Smoothing", 4f,
                new ConfigDescription("FOLLOW-THROUGH: how fast the measured depth FALLS on withdrawal (vs Depth Smoothing going in). LOWER than Depth Smoothing makes the depth linger, so ALL rings close LATER. Equal = symmetric open/close.", new AcceptableValueRange<float>(1f, 30f)));
            _cfgEntranceOpenWidth = Config.Bind(sec, "Entrance Open Width", 0.30f,
                new ConfigDescription("Depth range over which the ENTRANCE ring (V1) eases open, anchored at the moment the tip enters (depth 0): closed at 0, full at this depth. Gives the entrance INTERMEDIATE states during shallow insertion instead of snapping fully open the instant the tip touches it. Smaller = opens faster (0.15 ~ full when the tip is 15% in); larger = more gradual.", new AcceptableValueRange<float>(0.02f, 1f)));
            _cfgEntranceCloseScale = Config.Bind(sec, "Entrance Close Scale", 2f,
                new ConfigDescription("The ENTRANCE ring (V1) closes this many times SLOWER than the other rings (its geometric move per weight unit is smaller, so it needs a slower weight-rate for the same apparent close speed). 1 = same as the others.", new AcceptableValueRange<float>(1f, 5f)));
            _cfgOpenTime       = Config.Bind(sec, "Open Time", 0.2f,
                new ConfigDescription("Seconds for a ring to open fully — rate-limits opening so it EASES IN instead of snapping. Smaller = snappier; ~0.01 = the old instant behavior.", new AcceptableValueRange<float>(0.01f, 1f)));
            _cfgEntranceOpenScale = Config.Bind(sec, "Entrance Open Scale", 2f,
                new ConfigDescription("The ENTRANCE ring (V1) opens this many times SLOWER than the others, so the bottom eases open instead of popping. 1 = same as the others.", new AcceptableValueRange<float>(1f, 5f)));
            _cfgMaxGirthScale  = Config.Bind(sec, "Max Girth Scale", 2.5f,
                new ConfigDescription("Upper clamp on girth->width scaling (actual girth / Reference Girth). Higher lets bigger objects open wider; pairs with Max Ring Weight.", new AcceptableValueRange<float>(1f, 4f)));
            _cfgDebugLog       = Config.Bind(sec, "Debug Log", false,
                "Log depth/girth/weights every 2s (diagnostics). On by default during tuning.");

            const string brSec = "AutoBodyReveal";
            _cfgAutoBodyReveal = Config.Bind(brSec, "Enable", true,
                "Auto-apply the CloXray BodyReveal (x-ray body stencil) via MaterialEditor when a character loads/swaps, IF a CloXray womb is near its vagina. The womb's organ stencil is matched automatically. Per-body: only characters with a womb nearby are touched.");
            _cfgAutoBodyRevealKey = Config.Bind(brSec, "Apply Now Hotkey", new KeyboardShortcut(KeyCode.X, KeyCode.LeftShift, KeyCode.LeftAlt),
                "Manually (re)apply BodyReveal to every character near a CloXray womb. Use after first placing a womb, or after dragging one onto a different character. (Shift+Alt+X. Rebind here if it ever collides with another plugin.)");
            _cfgAutoBodyRevealRange = Config.Bind(brSec, "Womb-in-vagina range (m)", 0.15f,
                new ConfigDescription("How close a womb's entrance must sit to a character's cf_J_Vagina_root to count as 'inside it' (so x-ray auto-applies on scene-load/character-change, while a separately-spawned character is excluded). Raise if your womb sits farther from the vagina; lower to be stricter.", new AcceptableValueRange<float>(0.02f, 1f)));

            _cfgPairRange = Config.Bind("WombExpand", "Penis pair range (m)", 0.5f,
                new ConfigDescription("A womb only reacts to a penis whose tip is within this distance of the womb (entrance for the rings/stretch, womb center for the slosh). Fixes every womb in a multi-womb scene reacting to one penis — each pairs with the nearest. Raise if engagement drops out on long strokes; lower if neighboring wombs still cross-react.", new AcceptableValueRange<float>(0.1f, 2f)));
            _cfgTipDetach = Config.Bind("WombExpand", "Penis tip detach distance (m)", 0.20f,
                new ConfigDescription("The womb treats the penis as WITHDRAWN (closes) once the tip marker k_f_dan_end is farther than this from the womb's entrance — even if BP still reports depth. This is what lets you pull the tip out via a constraint/sphere on k_f_dan_end and have the womb follow (BP's own depth doesn't fall when you move only the tip). When inserted the tip sits ~0.08-0.13m from the entrance; pulled out it's 0.3m+. Lower it if the womb stays open after you pull out; raise it if it closes while still inserted (very long penis / very off-seated overlay). Only applies when a k_f_dan_end exists.", new AcceptableValueRange<float>(0.05f, 0.6f)));
            _cfgEntryDetach = Config.Bind("WombExpand", "Penis entry off-axis limit (m)", 0.05f,
                new ConfigDescription("The womb treats the penis as WITHDRAWN (closes) when its entry/base marker k_f_dan_entry sits farther than this OFF the womb's canal axis (sideways) — even if BP still aims the tip up into the womb. This catches pulling the penis OUT for a penis whose entry is NOT NodesConstraint-pinned to this womb's vagina: the entry tracks the base, so it swings off-axis on withdrawal (when inserted it sits ~on the canal axis, a few mm off; pulled out it's 60mm+). Being BELOW the mouth ALONG the canal is fine (that's normal insertion) — only sideways distance counts. An NC-pinned entry stays at the vagina (on-axis) so this never closes it. Lower if the womb stays open after you pull out sideways; raise if it closes while still inserted at an angle.", new AcceptableValueRange<float>(0.02f, 0.3f)));
            _cfgReactColliders = Config.Bind("WombExpand", "React to colliders", true,
                "When NO BP penis is engaged, also react to the nearest DynamicBoneCollider (e.g. a BP collider you place and size with KKPE on a bottle/toy). The womb opens and displaces from the collider's deeper capsule end (treated as the tip) and its radius (girth), exactly like a penis. READ-ONLY: the collider itself is never modified. A real BP penis always takes priority.");
            _cfgColliderRange = Config.Bind("WombExpand", "Collider pair range (m)", 0.3f,
                new ConfigDescription("A womb only reacts to a collider whose center is within this distance of the womb entrance. Tighter than the penis range since you place the collider right at the womb. Raise if a large collider's center sits far from the entrance.", new AcceptableValueRange<float>(0.05f, 1.5f)));
            _cfgColliderInCanal = Config.Bind("WombExpand", "Collider in-canal width (m)", 0.045f,
                new ConfigDescription("A collider only counts as INSERTED when its tip is within this lateral distance of the womb's canal axis. This is what keeps the womb from reacting to the character's own body colliders or an object resting beside it (which otherwise pin the low rings open on spawn). Raise if your inserted object is ignored; lower if the womb still reacts to something merely nearby.", new AcceptableValueRange<float>(0.01f, 0.25f)));
            _cfgColliderMaxRadius = Config.Bind("WombExpand", "Collider max radius (m)", 0.06f,
                new ConfigDescription("AUTO mode only (used when 'Collider name filter' is empty): a collider FATTER than this is treated as a character BODY collider and ignored. Body colliders are ~0.08m+; a penis ~0.02m, a big toy ~0.04m. Ignored when a name filter is set.", new AcceptableValueRange<float>(0.01f, 0.3f)));
            _cfgColliderName = Config.Bind("WombExpand", "Collider name filter", "Collider",
                "Targets colliders whose GameObject name STARTS WITH this (case-insensitive). Default 'Collider' is the name KKPE gives colliders you add, so the womb reacts to YOUR KKPE collider and skips the character's body colliders (named 'KK_Colliders_...', which a PREFIX match correctly ignores -- a substring match would not) and the penis (handled separately via BP). The womb tracks the matched collider's TIP depth, so it's responsive. Set EMPTY to instead use the automatic size/position heuristic (any small in-canal collider). Turn on Debug Log to see the real names in the 'DynamicBoneColliders' log line -- the KKPE '[J694]' label is NOT the object name.");

            _cfgClothesReveal = Config.Bind("AutoBodyReveal", "Also stamp worn clothes (x-ray through clothes)", true,
                "When applying BodyReveal (hotkey or character-change), ALSO stamp every WORN torso garment (top/bot/bra/shorts/panst; only clothes that are ON) with a BodyReveal material copy at the matching pair stencil — the womb x-rays through the clothes and the out-of-body bleed disappears at stamped pixels. ME persists the copies per scene/card. A freshly equipped garment needs another hotkey press. OFF = body only (clothes hide the shell; interior/cum may bleed unless the womb's OutBodySceneConfine is on).");

            _cfgVeilAlpha = Config.Bind("AutoBodyReveal", "Veil X-ray strength on apply", 0.9f,
                new ConfigDescription("XrayAlpha set on the skin-veil copy when it is first CREATED (0=skin opaque/no x-ray, 1=raw x-ray). Re-pressing the hotkey never overwrites your per-scene slider tweaks.", new AcceptableValueRange<float>(0f, 1f)));

            _cfgBodyVeil = Config.Bind("AutoBodyReveal", "Also apply skin veil (BodyRevealExtra)", true,
                "When applying BodyReveal (hotkey or character-change), ALSO create the second body material copy with CloXray/BodyRevealExtra — the translucent-skin layer over the x-ray window with the 'XrayAlpha' strength slider (0=skin opaque, 1=raw x-ray). Two copies are required because one material has one render queue (stamp=2500, veil=3502); this just saves doing the second copy by hand. OFF = stamp only (the pre-veil look).");

            // (2026-06-10 cleanup) The IK-saga A/B toggles were removed once the fixes were verified:
            // skin-matrix cum scale, the post-IK onPreCull pose feed, and BakeMesh measurement are now
            // hard-wired (the OFF paths were the known-wrong legacy behaviors); the penis_target detach
            // toggle was diagnostic for a refuted theory.

            Configured = true;
            AutoBodyReveal.Enabled  = CfgEnabled && _cfgAutoBodyReveal.Value;
            AutoBodyReveal.Debug    = _cfgDebugLog.Value;
            AutoBodyReveal.MaxRange = _cfgAutoBodyRevealRange.Value;
            AutoBodyReveal.Init();   // subscribe to CharacterReloaded (KKAPI soft-dep ensures it loads first)
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

        private void Update()
        {
            // Hotkey + live config only — NO per-frame scanning/applying (that's event-driven).
            if (_cfgAutoBodyReveal != null)      AutoBodyReveal.Enabled  = CfgEnabled && _cfgAutoBodyReveal.Value;   // master OFF disables the auto-apply path too
            if (_cfgDebugLog != null)            AutoBodyReveal.Debug    = _cfgDebugLog.Value;
            if (_cfgAutoBodyRevealRange != null) AutoBodyReveal.MaxRange = _cfgAutoBodyRevealRange.Value;
            if (CfgEnabled && _cfgAutoBodyRevealKey != null && _cfgAutoBodyRevealKey.Value.IsDown())
                AutoBodyReveal.ApplyAll();
        }

        // Bootstrap entry point called by the baked wobble component (LiquidWobbleMPBEffect.Start)
        // on each womb item. Finds the item root and attaches WombExpandEffect once. No scan, no
        // freshly-baked MonoScript (which crashed Unity's native loader).
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
