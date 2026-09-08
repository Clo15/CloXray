using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LiquidWobbleMPB
{
    // ── Free-H / main-game womb (build 439) ─────────────────────────────────────────────
    // In the MAIN GAME (not Studio) the hotkey TOGGLES a womb on the female instead of the
    // Studio apply: spawn the item prefab from the CloXray zipmod bundle, mirror its skeleton to
    // the wearer's live bones every frame, and run the same x-ray apply as Studio.
    //
    // Why mirroring instead of SMR bone-rebind: the womb prefab carries its own copy of the
    // 8 body bones PLUS item-only leaf bones (clo_canal_entry, penis_target) that the plugin
    // reads relative to that skeleton. Driving the item's bones from the character's keeps
    // every existing assumption intact - mesh, markers, measurement - while the womb follows
    // animation, body sliders and scale exactly like a worn accessory.
    //
    // Zero-effect rule holds: nothing runs in the main game until a womb is toggled on.
    internal static class MainGameWomb
    {
        private const string Bundle = "studio/clo/clo_xraywomb.unity3d";
        private const string Prefab = "clo_xraywomb";

        private static bool? _isStudio;
        public static bool IsStudio
        {
            get
            {
                if (_isStudio == null)
                {
                    // The MAIN GAME's Assembly-CSharp also CONTAINS the Studio.Studio type (shared
                    // codebase), so type-existence is not a discriminator - the process name is.
                    string pn = null;
                    try { pn = BepInEx.Paths.ProcessName; } catch { }
                    _isStudio = pn != null && pn.IndexOf("CharaStudio", StringComparison.OrdinalIgnoreCase) >= 0;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: process '" + pn + "' -> " + (_isStudio.Value ? "STUDIO" : "MAIN GAME") + " mode.");
                }
                return _isStudio.Value;
            }
        }

        // one womb per female (keyed by her ChaControl component)
        private static readonly Dictionary<Component, GameObject> _spawned = new Dictionary<Component, GameObject>();

        // POSE-CHANGE hook: bumped by a Harmony postfix on HSceneProc.ChangeAnimator - the SAME place
        // BP catches animation changes. The penis fit re-measures ONCE per bump, then stays locked
        // (when the pose changes, nothing else changes until the next pose).
        internal static string RespawnWhy = "forced uncensor body reload";
        internal static bool BpRefTargetMissing;   // BP DanAgent.m_referenceTarget is null - published where it is read
        internal static float RespawnAt;   // >0: respawn the H womb at this time (forced-uncensor body reload)
        internal static float ReloadPendingUntil;   // a forced uncensor reload is settling until this time
        internal static float DeferredSpawnAt;      // >0: spawn the H womb at this time (waiting on that reload)
        internal static float DeferredSpawnDeadline; // loud giving-up point if the reload never reports back

        // KKAPI's CharacterReloaded does NOT fire for UncensorSelector's ReloadCharacterBody() - it is a
        // partial reload, not a character reload (waiting on that event always timed out). So the
        // deferred spawn polls for the thing actually needed instead: her BP vagina bones existing. That
        // IS "the body finished rebuilding as a BP body", and it is what the womb binds to.
        internal static Component DeferredSpawnFemale;

        // A forced body uncensor drops MaterialEditor's edits on the old body mesh (its re-apply hooks are
        // KKAPI-level and none of them fire for UncensorSelector's partial reload). ME exposes
        // RefreshBodyEdits() - body/face only - but nothing calls it on KK/KKS. Run it once the reload has
        // settled so the character's own skin shader comes back.
        // EVENT-DRIVEN, not a timer: UncBodyReloadWatch is a Harmony postfix on the very method that
        // rebuilds the body, so Done() marks the exact frame the swap finished. A guessed delay would be
        // wrong on a slow load and wasteful on a fast one. The deadline is a LOUD giving-up point, not a
        // second path: if the reload never reports, say so and repair nothing.
        internal static Component MeRefreshChara;
        internal static float MeRefreshDeadline;

        // The pose version the BP pin was last established for. BP resets m_innerTarget to its own
        // default on every pose change, so danEnd is not pinned until it is re-pinned - see PinAgent.
        internal static int AimedForPose = -1;

        // >0: dump the x-ray material chain at this time (armed by a pose change, see BumpPose)
        internal static float ChainDumpAt;
        internal static Component SMale { get { return _sMale; } }

        internal static Component SFemale { get { return _sFemale; } }
        internal static WombExpandEffect SWomb { get { return _sWomb; } }

        internal static bool BpBodyReady(Component female)
        {
            if (female == null) return false;
            foreach (var tr in female.GetComponentsInChildren<Transform>(true))
                if (tr != null && tr.name.StartsWith("cf_J_Vagina", System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private static int _poseVersion;
        public static int PoseVersion { get { return _poseVersion; } }
        private static bool _poseHookTried;

        public static void InstallPoseHook()
        {
            if (_poseHookTried || IsStudio) return;
            _poseHookTried = true;
            try
            {
                var t = Type.GetType("HSceneProc, Assembly-CSharp");
                var m = t != null ? t.GetMethod("ChangeAnimator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
                if (m == null) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: HSceneProc.ChangeAnimator not found — pose-change re-measure hook NOT installed (fit stays from the first pose)."); return; }
                var h = new HarmonyLib.Harmony("cloxray.hposechange");
                h.Patch(m, null, new HarmonyLib.HarmonyMethod(typeof(MainGameWomb).GetMethod(nameof(OnPoseChanged), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: pose-change hook installed (HSceneProc.ChangeAnimator — same trigger BP uses).");
                var tSpr = t.Assembly.GetType("HSprite");
                var mSel = tSpr != null ? tSpr.GetMethod("OnChangePlaySelect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new Type[] { typeof(GameObject) }, null) : null;
                if (mSel == null) LiquidWobbleMPBPlugin._logger?.LogError("CloXray: HSprite.OnChangePlaySelect(GameObject) not found — manual pose clicks stay name-less (predictions can only match sweep-collected keys).");
                else h.Patch(mSel, new HarmonyLib.HarmonyMethod(typeof(MainGameWomb).GetMethod(nameof(OnPlaySelectClicked), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)), null);
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: pose-change hook install failed: " + e.Message); }
        }

        // HSceneProc exposes NO "current pose" member (metadata dump: no nowAnimationInfo field and no
        // AnimationListInfo property - only the lstAnimInfo/lstUseAnimInfo arrays). But there is already a postfix
        // ChangeAnimator, so capture its _nextAinmInfo argument (__0, positionally) - that IS the pose the
        // game just switched to, including a manual selection. This is what lets the sweep
        // start from where they are instead of pose 1.
        public static object CurrentAnimInfo;
        // several doggy/cowgirl poses carry different names, and that was NOT
        // accounted for, which invalidated the whole "staging drifts" conclusion. The menu dump shows
        // 67 sonyu entries of which ~20 are all `id=2 'Doggy'` and 5 are `id=4 'Cowgirl'` - modded animations
        // reuse the base id AND nameAnimation. Keying research rows by id+name therefore merged ~20 DIFFERENT
        // animations into one bucket, and the resulting MIN 23..110mm spread was simply different poses, not
        // one pose drifting. AnimationListInfo.pathMaleBase is a PathName{assetpath,file} - the actual
        // animation asset - so THAT is the unique identity.
        public static string CurrentAnimKey = "?";
        // variant identity is decided in OnPoseChanged by CLICK PROVENANCE. Every real menu
        // click arrives with a PendingBtnTag (sweep sets it; the OnChangePlaySelect prefix tags
        // manual clicks too) -> always a NEW pose. A CLICKLESS ChangeAnimator with the same
        // field-identity as the current bank is the ALTERNATE control (the right-click box) ->
        // walk the object registry for a stable ~v index. A poller-side registry was WRONG: it
        // compared field-descriptions, and the modded pack shares those across DIFFERENT poses, so the
        // base sweep minted fake ~v1..~v9 for consecutive poses (584-row CSV audit, 2026-07-26).
        public static string VariantSuffix = "";
        private static readonly System.Collections.Generic.List<object> _varReg = new System.Collections.Generic.List<object>();
        private static string _varRegKey;   // field-identity of the bank _varReg belongs to
        public static string CurrentAnimKeyV { get { return (CurrentAnimKey ?? "?") + VariantSuffix; } }
        // ~56 of the 67 menu entries are modded variants sharing id+name, and NOTHING in
        // AnimationListInfo separates them (both asset paths are shared base rigs; posture/kindHoushi/
        // numCtrl/sysTaii identical) - so 67 entries collapsed to 11 labels and only 4 survived as usable
        // pairs. The MENU BUTTON is the one thing that IS distinct per entry, and the sweep already knows
        // exactly which one it clicked; it was simply never recorded. Set as a ONE-SHOT before a sweep click
        // and consumed by the ChangeAnimator postfix, so sweep-driven changes get tagged while a manual
        // own manual switches stay untagged (no stale tag leaking onto them).
        public static string PendingBtnTag;
        public static string BtnTag(GameObject go)
        {
            if (go == null) return null;
            // where every pose has a unique VISIBLE name, detect those. The on-screen
            // label is a far better identity than the sibling index - reorder-STABLE (adding/removing an
            // anim mod shifts every index after it; the name survives), human-readable in the shipped
            // reference file, and partially PORTABLE (popular animations keep the same display name across
            // modpacks, so name-keyed baseline rows can match on another install where index keys never
            // could). Read via reflection - the plugin does not reference UnityEngine.UI/TMP assemblies.
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var ty = comp.GetType();
                if (ty.Name != "Text" && ty.Name != "TextMeshProUGUI" && ty.Name != "TMP_Text") continue;
                var pi = ty.GetProperty("text");
                string s = pi != null ? pi.GetValue(comp, null) as string : null;
                if (string.IsNullOrEmpty(s)) continue;
                s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();   // keep the key single-line
                if (s.Length > 0) return "name:" + s;
            }
            // Button has no visible text (blank label): fall back to the positional identity - still a
            // distinct, in-session-stable key, just not portable. Logged so blanks are visible in the data.
            var tr = go.transform;
            string p = go.name + "[" + tr.GetSiblingIndex() + "]";
            if (tr.parent != null) p = tr.parent.name + "/" + p;
            return p;
        }
        public static string AnimUniqueKey(object info)
        {
            if (info == null) return "?";
            var ty = info.GetType();
            string id = "?", nm = "?", file = "";
            try { id = Convert.ToInt32(ty.GetField("id", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(info)).ToString(); } catch { }
            try { nm = (ty.GetField("nameAnimation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(info) ?? "?").ToString(); } catch { }
            try
            {
                // pathMaleBase is the shared male BASE RIG - every pose reported
                // "@02_00_00.unity3d/khs_m_base", so it discriminated nothing and ~12 different Doggy
                // animations still merged (n=36 with 96.8mm deepY spread, vs 0.0mm for genuinely single
                // animations). Use the FEMALE base instead - her animation is what differs between the
                // variants - and fall back to the male one only if it is missing.
                object pn = ty.GetField("pathFemaleBase", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(info)
                         ?? ty.GetField("pathMaleBase", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(info);
                if (pn != null)
                {
                    var pt = pn.GetType();
                    string ap = (pt.GetField("assetpath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(pn) ?? "").ToString();
                    string fl = (pt.GetField("file", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(pn) ?? "").ToString();
                    int cut = ap.LastIndexOf('/'); if (cut >= 0 && cut + 1 < ap.Length) ap = ap.Substring(cut + 1);
                    file = ap + "/" + fl;
                }
                // ALSO fold in the per-entry discriminators that exist on AnimationListInfo itself.
                // If the asset paths still collide, these must differ - merging is worse than splitting,
                // because merged rows silently average different animations while split rows can be
                // recombined later once they are known to be identical.
                try { file += "|p" + Convert.ToInt32(ty.GetField("posture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(info)); } catch { }
                try { file += "|k" + Convert.ToInt32(ty.GetField("kindHoushi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(info)); } catch { }
                try { file += "|n" + Convert.ToInt32(ty.GetField("numCtrl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(info)); } catch { }
                try { file += "|s" + Convert.ToInt32(ty.GetField("sysTaii", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).GetValue(info)); } catch { }
            }
            catch { }
            return "id=" + id + " '" + nm + "' @" + file;
        }
        private static void OnPoseChanged(object __0)
        {
            if (__0 != null)
            {
                bool sameObj = ReferenceEquals(__0, CurrentAnimInfo);
                CurrentAnimInfo = __0;
                string tag = PendingBtnTag; PendingBtnTag = null;   // one-shot, consumed here
                string fkey = AnimUniqueKey(__0);
                if (!string.IsNullOrEmpty(tag))
                {
                    // A real menu-button click = a fresh pose selection, ALWAYS (a re-click of the same
                    // button just restarts it: AnimationLoader's OnChangePlaySelect transpiler NOPs the
                    // same-id early-return, which is exactly the observed double-play). Seed the registry.
                    CurrentAnimKey = fkey + " #" + tag;
                    _varReg.Clear(); _varReg.Add(__0); _varRegKey = fkey; VariantSuffix = "";
                }
                else if (_varReg.Count > 0 && fkey == _varRegKey)
                {
                    // CLICKLESS swap inside the same field-identity bank = the ALTERNATE control.
                    // CurrentAnimKey (the clicked button's name) stays - only the ~v index moves.
                    int vi = _varReg.IndexOf(__0);
                    if (vi < 0) { _varReg.Add(__0); vi = _varReg.Count - 1; }
                    VariantSuffix = vi > 0 ? (" ~v" + vi) : "";
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: CLICKLESS anim change -> variant index " + vi
                        + " (RMB=" + (UnityEngine.Input.GetMouseButton(1) ? 1 : 0)
                        + ", obj#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(__0).ToString("X8")
                        + ") key='" + CurrentAnimKey + VariantSuffix + "'");
                }
                else if (!sameObj)
                {
                    // Clickless AND a different field identity = game-internal change (H start, mode
                    // transition). A field-only key can NEVER match the #name-keyed baseline, so
                    // the FIRST pose of every H session always missed prediction (log proof: 4 poses
                    // primed at the fallback 164mm, then a 22mm one-stroke correction - the "penis
                    // readjusting on screen). Reverse-look-up the MENU button carrying this same
                    // AnimationListInfo and adopt its visible name; the menu not being built yet is the
                    // one honest miss left (logged, field key as before - that IS the old behavior).
                    string mtag = TagFromMenu(__0);
                    CurrentAnimKey = mtag != null ? (fkey + " #" + mtag) : fkey;
                    _varReg.Clear(); _varReg.Add(__0); _varRegKey = fkey; VariantSuffix = "";
                    if (LiquidWobbleMPBPlugin.CfgDebugLog)
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: CLICKLESS anim change -> game-internal, key='" + CurrentAnimKey + "'" + (mtag == null ? " (no menu button matched — field key only)" : " (name recovered from the menu)"));
                }
            }
            BumpPose("HSceneProc.ChangeAnimator");
        }
        // find the H menu button whose AnimationInfoComponent carries THIS AnimationListInfo
        // (by reference) and return its visible label - gives game-internal pose selections (H start,
        // mode transitions) the same #name identity as clicked ones, so prediction hits from pose one.
        private static string TagFromMenu(object animInfo)
        {
            try
            {
                var tHS = Type.GetType("HSceneProc, Assembly-CSharp");
                var hs = tHS != null ? UnityEngine.Object.FindObjectOfType(tHS) : null;
                var fSprite = tHS != null ? tHS.GetField("sprite", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
                var sprite = hs != null && fSprite != null ? fSprite.GetValue(hs) as Component : null;
                if (sprite == null) return null;
                var tAIC = sprite.GetType().GetNestedType("AnimationInfoComponent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var fInfo = tAIC != null ? tAIC.GetField("info", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
                if (fInfo == null) return null;
                foreach (var comp in sprite.GetComponentsInChildren(tAIC, true))
                {
                    if (comp == null) continue;
                    if (ReferenceEquals(fInfo.GetValue(comp), animInfo)) return BtnTag((comp as Component).gameObject);
                }
            }
            catch { }
            return null;
        }
        // tag EVERY OnChangePlaySelect click (the game's real anim-select entry point) with the
        // clicked button's visible label. Before this only SWEEP clicks were tagged - a manual
        // manual pose picks produced name-less field keys (which the modded pack shares across ~20
        // poses), so real-play prediction lookups could never match the shipped #name-keyed baseline.
        private static void OnPlaySelectClicked(object __0)
        {
            try
            {
                var go = __0 as GameObject;
                if (go == null) return;
                string tag = BtnTag(go);
                if (!string.IsNullOrEmpty(tag)) PendingBtnTag = tag;
                if (LiquidWobbleMPBPlugin.CfgDebugLog)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: OnChangePlaySelect click btn='" + (tag ?? "<no label>")
                        + "' RMB=" + (UnityEngine.Input.GetMouseButton(1) ? 1 : 0));
            }
            catch { }
        }


        // KKS RIG MATCH (hard-wired): copy the scaling from KK so the visual appearance
        // is the same", no sliders): the KK female pelvis chain carries ~0.828 bone scale (measured
        // kokanScale=0.828 on the reference body) which the KKS rig does NOT (1.0) - the same womb
        // rendered ~21% bigger vs the girl in KKS (canal 94mm vs KK ~74-78: weaker dome push, wider
        // look, larger canal-vs-shaft divergence arm). ONE source of truth: every H womb-scale
        // consumer (the bone mirror AND the canal-length math) reads this - never CfgHWombScale
        // directly - so the mesh and the math can never disagree.
        // rig-match corrected by MEASUREMENT - the KKS rig is NOT scale-1.0: her pelvis reads
        // 0.917 (log) vs KK's 0.828, so the conversion is 0.828/0.917 = 0.903 (0.828 over-
        // shrank ~9%). MIRROR-ONLY: the canal-length formula keeps multiplying the plain config value
        // (its 1.15 double-count exists identically in KK and every KK-approved calibration absorbed
        // it - the rig-match reaches the canal number through the mirrored bones' lossyScale, giving
        // numeric parity with KK: same visual womb -> same canal number -> same normalized math).
        // the KKS womb now SCALES WITH THE FEMALE'S BODY, matching KK's behaviour. KK mirrors
        // HER bone scales, so its womb inherits her pelvis size (canal-bone lossy = her kokan 0.888 in
        // the log) - a bigger female automatically gets a bigger womb. An experiment replaced her scales with
        // fixed AUTHORED scales for bind-consistent skinning, which broke that female-tracking (KKS
        // canal-bone lossy was a fixed 1.150). This restores it WITHOUT losing the skinning fix: the
        // root scale injection is now CfgHWombScale × (her kokan lossy / KokRef), so any female sizes
        // her womb from her own pelvis. KokRef = 1.019 makes this female (kokan 0.917) reproduce the
        // approved ~86mm (= the old ×0.90). KK keeps its implicit her-scale path.
        // Sub-linear womb scaling: small females get a proportionately BIGGER womb -
        // organs don't scale 1:1 with body size, and a fixed-size penis fits a too-tiny womb badly).
        // effKok pulls the female's pelvis scale toward the reference: Blend 1 = womb scales 1:1 with
        // her, 0 = fixed size, 0.6 = womb tracks 60% of her size change (petite -> womb
        // shrinks only 60% as much). Applied in BOTH games so they behave identically.
        public const float KokStd = 0.888f;       // reference female pelvis (default KK) where the womb size is calibrated
        public const float KokRefKKS = 1.006f;    // KKS authored-scale baseline (gives the approved ~86mm at the KKS ref female)
        // the womb on a small char looked too big and was reduced. 0.6 gave a small female
        // (kokan 0.598) a +19% womb boost (effKok/kok=1.19). 0.75 trims that to +12% - the womb is ~6%
        // smaller on petite chars while the reference (0.888) is unchanged by construction and larger
        // females barely move. Ovaries ride the same bake scale so they shrink with it (plus the extra
        // ovary_shrink baked below for their disproportionate size).
        public static float WombScaleBlend = 0.75f;

        public static float HMirrorWombScaleFor(Transform pivot)
        {
            float kok = pivot != null ? pivot.lossyScale.y : KokStd;
            float effKok = Mathf.Lerp(KokStd, kok, WombScaleBlend);   // sub-linear pull toward the reference size
            // UNIFIED both games - the mirror uses HER local scales (KKS no longer uses authored
            // scales, reverted for the KKS-follow experiment), so the womb scales with kok and
            // counter with effKok/kok for the sub-linear target. At kok == KokStd this is exactly 1.0.
            return LiquidWobbleMPBPlugin.CfgHWombScale * (effKok / kok);
        }

        // ── KKS IDLE FIX: BP classifies penetration from the GAME's Lookat_dan.strPlayMotion
        // (`m_danPenetration = (topStick && ref) || motion.Contains("IN")`; false -> AdjustDanToTargetNull
        // = the dan RESETS and the pin is ignored). KKS under-feeds that input at idle-after-insert
        // (KK doesn't) - the penis visibly left the canal line at pause and snapped back on motion.
        // Fix at BP's own entry: prefix CoreGame.LookAtDanUpdate and rewrite the motion argument to
        // "Insert" ONLY while the game's own HFlag says PENETRATED (the positive-list) - the game's
        // authoritative flag overrides its own under-fed lookat string. Gated: master toggle + a womb
        // attached + flag known; motions already containing "IN" pass through untouched (KK path).
        private static bool _danMotionHookTried;
        private static string _motionPatchLogged;

        public static void InstallDanMotionHook()
        {
            if (_danMotionHookTried || IsStudio) return;
            _danMotionHookTried = true;
            try
            {
                // Patch EVERY LookAtDanUpdate found: BP's Core can be ILMerged into more than
                // one loaded BP assembly (main + studio variants) - first-match-only risks silently
                // patching the copy the game never calls. Multiple patches are idempotent and the
                // prefix gates itself.
                // Patch BOTH LookAtDanUpdate AND LookAtDanSetup: the finish anims deliver the
                // NULL look-at through the SETUP call (fired at anim change; the per-frame Update path
                // reuses the STORED reference) - patching Update alone substituted a value nobody read
                // (log: substitution fired, refTarget stayed '-').
                var targets = new List<System.Reflection.MethodInfo>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name.IndexOf("BetterPenetration", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            foreach (var nm in new[] { "LookAtDanUpdate", "LookAtDanSetup" })
                            {
                                var mi = t.GetMethod(nm, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                if (mi != null) targets.Add(mi);
                            }
                        }
                    }
                    catch { }
                }
                if (targets.Count == 0) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BP CoreGame.LookAtDanUpdate/Setup not found — the KKS idle penetration-drop fix is NOT installed (pin may be ignored at idle)."); return; }
                var h = new HarmonyLib.Harmony("cloxray.danmotion");
                foreach (var mi in targets)
                    h.Patch(mi, new HarmonyLib.HarmonyMethod(typeof(MainGameWomb).GetMethod(nameof(LookAtDanMotionPrefix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)), null);
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP motion-input hook installed on " + targets.Count + " LookAtDanUpdate method(s) (HFlag-penetrated overrides an under-fed lookat motion string; the KKS idle fix).");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BP motion-input hook install failed: " + e.Message); }
        }

        private static void LookAtDanMotionPrefix(ref Transform lookAtTransform, ref string currentMotion)
        {
            try
            {
                if (!LiquidWobbleMPBPlugin.CfgEnabled || _spawned.Count == 0) return;
                if (!HMotionKnown || !HPenetrated) return;
                // NULL LOOK-AT FIX: during the FINISH anims (WS_IN_* etc.) KKS nulls the
                // game's dan look-at while still penetrated -> BP's reference target stays null ->
                // AdjustDanToTargetNull = dan reset, pin dropped ("after cumming the penis stopped
                // following the canal"). Substitute the LAST NON-NULL look-at the game itself passed
                // (by definition the LIVE bone; a name-lookup of k_f_kokan_00 risked the
                // un-animated PROXY skeleton's bone, the same phantom trap as the seat).
                if (lookAtTransform != null) _lastGoodLookAt = lookAtTransform;
                else if (_lastGoodLookAt != null)
                {
                    lookAtTransform = _lastGoodLookAt;
                    if (_lookAtPatchLogged != HMotion)
                    {
                        _lookAtPatchLogged = HMotion;
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP look-at input is NULL during '" + HMotion + "' while HFlag says PENETRATED — substituting the game's last look-at '" + _lastGoodLookAt.name + "' so the pin stays honored (finish-state hold).");
                    }
                }
                if (currentMotion != null && currentMotion.Contains("IN")) return;   // BP classifies penetrated on its own
                if (_motionPatchLogged != currentMotion)
                {
                    _motionPatchLogged = currentMotion;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP motion input '" + (currentMotion ?? "null") + "' would DROP the vaginal path while HFlag says PENETRATED (the KKS idle case) — passing 'Insert' so the pin stays honored.");
                }
                currentMotion = "Insert";
            }
            catch { }
        }
        private static Transform _lastGoodLookAt;  // the game's last non-null dan look-at (live by definition; finish-state substitute)
        private static string _lookAtPatchLogged;

        // Central pose bump: fired by the ChangeAnimator postfix AND by the nowAnimationInfo
        // watch (the ALTERNATIVE-variant button). Debounced - a normal pose change fires both within
        // the same instant and must re-measure ONCE, not twice (a double bump left _seenPose one
        // behind and burned a wasted re-measure right after every lock).
        private static float _lastPoseBump = -10f;
        public static void BumpPose(string why)
        {
            if (Time.unscaledTime - _lastPoseBump < 0.5f) return;
            _lastPoseBump = Time.unscaledTime;
            _poseVersion++;
            // a reporter has the penis rendering correctly on apply and going wrong on a pose
            // change, and nothing re-checks the x-ray here. Dump the material chain once the pose has
            // settled so the log holds the last good chain and the first bad one.
            ChainDumpAt = Time.unscaledTime + 2f;
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: H animation changed via " + why + " (pose v" + _poseVersion + ") — penis fit will re-measure one stroke, then lock.");
        }

        // Current animation label "id=.. name='..'" (from HFlag.nowAnimationInfo) - stamped into
        // the fit LOCKED log so main-vs-ALTERNATE depth can be compared empirically across poses
        // (hypothesis: alternates are the deeper variants by design).
        public static string HAnimLabel = "?";

        // Live H motion state (fed by WombBoneMirror.FeedPenetrationState from HFlag): the fit samples
        // ONLY while penetrated + the state is stable - the game itself signals when the transition
        // is over, no blind settle timers.
        public static string HMotion = "";

        // KKS Free-H only, hard-wired per the no-extra-sliders rule: extend the canal 6mm DOWN so
        // its mouth reaches the visible entrance (mounddown 50 shapes the mound but the tube itself
        // stopped short). Two halves that only work TOGETHER: the seat servo targets the entry
        // 6mm below BP's reference (whole womb down), and the resting elongation (+6mm via the existing
        // womb_displace base-stretch math) raises the top back - net: mouth −6mm, top unchanged, canal
        // +6mm, depth math coherent through the (1+f) formula. 0 in KK: it is the approved reference.
        // KKS Free-H only, hard-wired: the penis rode ~2mm toward the FRONT wall through every
        // animation - the tube sits slightly too far back relative to BP's penis line. Seat the womb
        // 2mm FORWARD (subtracted from the 'Womb offset to back' in both the mirror seat and the servo
        // target, so pre-insert hold and penetrated servo agree).
        // canal extend-down applies to BOTH games' Free-H now (approved in KKS, wanted in
        // same in KK). Seat-forward stays KKS-only - that corrects the KKS-specific rig seat offset;
        // KK's reference seat needs no forward nudge.
        public const float CanalExtendDownMM = 6f;
#if KKS
        // KKS SEAT RECALIBRATION (feel + the vulva probe, on the FORCED BP body - the old
        // value 4 was tuned on the pre-BP body mesh). Probe: KKS delta(ent−vulvaRoot)=(−0.2,+4.8,+3.8)mm vs
        // KK's approved (0,+6.1,+2.2). Asked for slightly down and 2mm to back - the 2mm back lands z at +1.8
        // ≈ KK's +2.2 (their eye re-derived the KK relation); expected post-fix probe ≈ (0, ~3.8, +1.8).
        // Absorbs the KK<->KKS difference so the shared 'Womb offset to back' can read as KK's value.
        // -1 -> -2. With the shared setting at KK's 2, KKS computes 2 - (-2) = 4 - the
        // tested KKS on the shipped 2 and preferred one step further ("default is 2 but i like 3
        // more", i.e. the F1 value 3 on their install, which this constant now supplies so their cfg
        // can stay equal to the default). KKS therefore sits 2mm toward her belly of KK's seat.
        // Keep these two in step: change the setting by N and this constant by N, or KKS moves too.
        public const float SeatForwardMM = -2f;
        public const float SeatAlongCanalMM = -1f;      // NEW: 1mm DOWN along the canal (KKS body sits the vulva differently)
#else
        public const float SeatForwardMM = 0f;
        public const float SeatAlongCanalMM = 0f;       // KK = the reference; no along-canal seat correction
#endif
        public static bool HPenetrated;
        public static bool HMotionKnown;
        public static float HMotionChangedAt;

        // ── ROUTE-B PROTOTYPE (temporary - strip once a winner is picked) ──────────────────────────
        // Instead of copying her bone poses onto the item's own skeleton every frame (the mirror),
        // re-point the womb's SkinnedMeshRenderers at HER bones so Unity skins the mesh natively.
        // The womb is then deformed in the SAME pass as her body => epoch-correct BY CONSTRUCTION,
        // which structurally removes the flying-womb / epoch-split / seat-servo class of bug.
        // PHASE 1 is a raw rebind: NO scale and NO seat offset yet, so expect correct SHAPE and
        // MOTION but possibly wrong SIZE/SEAT. Both are bake-once-at-spawn steps (phase 2).
        // REBIND is the DEFAULT - one Ctrl+Shift+W spawns a route-B womb directly
        // (no per-session mode toggle). MIRROR stays available in research builds for comparison.
        public static bool UseRebind = true;

        // -- PLACEMENT COMMAND ------------------------------------------------------------------
        // The correction the rebind bake applies, in HER PELVIS frame, millimetres: x = her right,
        // y = along the canal (up), z = her forward. Bone-LOCAL axes, so it stays pose-independent
        // like the rest of the bake. Commanded == measured 1:1, proven, which is what makes
        // hitting a target pure arithmetic; the analytic predictor supplies the number for
        // whatever character the womb spawns on. No per-character constants anywhere.
        public static Vector3 RebindNudgeMM = Vector3.zero;
        public static bool CurrentlyRebound;          // ACTUAL live state; UseRebind is the requested intent
        private static Component _nudgeFemale;        // nudge/target belong to THIS female; reset on change
        // SNAP: once a girl has been placed, later REBIND toggles for the SAME girl bake
        // instantly with her cached nudge - no second predict, no blink. Cleared only by the womb
        // spawning on a different girl. Session-only; card-persistent caching is later engineering.
        public static bool NudgePlaced;
        // the analytic predictor's one-shot nudge (her-frame world mm). Set by
        // WombExpandEffect.PredictParity, consumed by AutoPlacePredicted to place in ONE respawn.
        // Validated when it went in against the measured feedback loop it replaced (2.1mm y, 1.7mm z);
        // That loop was removed, so this is now the only thing that places the womb.
        public static Vector3 PredictedNudgeMM; public static bool PredictedValid;
        // Dual-chain predictor inputs (stashed at bake; consumed by WombExpandEffect.PredictParity)
        public static Matrix4x4[] RebindOrigBindposes;
        public static Matrix4x4 RebindM0;
        public static Transform RebindPivotBone;
        public static float RebindS = 1f, RebindSeatBackMM;

        public static bool AnySpawned()
        {
            foreach (var kv in _spawned) if (kv.Key != null && kv.Value != null) return true;
            return false;
        }

        private static bool RebindWombToWearer(GameObject womb, Transform wearer)
        {
            var hers = new Dictionary<string, Transform>();
            foreach (var t in wearer.GetComponentsInChildren<Transform>(true))
                if (t != null && !hers.ContainsKey(t.name)) hers[t.name] = t;   // first match wins (same rule as Bind)

            Transform herKokan;
            if (!hers.TryGetValue("cf_j_kokan", out herKokan) || herKokan == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: REBIND FAILED — the wearer has no cf_j_kokan. Applying NOTHING (no fallback) — hotkey back to MIRROR mode.");
                return false;
            }

            // PHASE 2 - reproduce the mirror's SCALE and SEAT OFFSET. Native skinning gives no per-bone
            // hook, so bake them into the mesh instead. Multiplying the transform into the BINDPOSES
            // applies it to the rest vertices AND to every blendshape delta consistently (deltas are
            // added to rest BEFORE skinning), so the rings still open by the correct absolute amount at
            // any womb size. Bake-once at spawn - zero per-frame cost.
            // Mirror parity: it scales bone positions about HER KOKAN by s and translates every bone by
            // -herKokan.forward * back, so exactly the same pivot, scalar and offset are used.
            // MEASURED: the un-baked rebind womb is NOT uniformly mis-sized. Skinning from her bones
            // compresses it ALONG THE CANAL (~0.75x on a small female, kokanLossy 0.598) while leaving the
            // GIRTH already correct (~1.00x) - verified identical at standing and cowgirl, so it is a fixed
            // anisotropy, not a pose-dependent bindpose mismatch. A uniform s therefore fixed the length
            // (0.75 x 1.374 = 1.03) and over-inflated the girth (1.00 x 1.374 = 1.37), which also skewed
            // openEff and hence the ring drive. So scale by s ONLY along the canal axis, girth untouched.
            float s = HMirrorWombScaleFor(herKokan);
            float backMM = LiquidWobbleMPBPlugin.CfgHWombBack - SeatForwardMM;
            Vector3 pivotW = herKokan.position;
            Vector3 offsetW = Mathf.Abs(backMM) > 0.01f ? -herKokan.forward * (backMM * 0.001f) : Vector3.zero;
            var canalBone = FindDeep(womb.transform, "clo_canal_entry");
            if (canalBone == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: REBIND FAILED — no clo_canal_entry on the womb. Applying NOTHING (no fallback).");
                return false;
            }
            // ROOT CAUSE: the bake must be POSE-INDEPENDENT. Bindposes are pose-invariant by
            // definition, so deriving M from her LIVE world matrices baked whatever phase of her idle
            // animation she happened to be in at spawn straight into the mesh - measured as ~15mm / ~6deg
            // of scatter between identical respawns (MIRROR is stable to ~5mm / ~1deg). That variance is
            // what the earlier (s-1) and (s-1)/s "laws" were actually fitting.
            // Everything is therefore taken in the canal bone's AUTHORED LOCAL frame under the womb's own
            // cf_j_kokan - captured BEFORE the reparent, so it is the bind-time relationship that the
            // bindposes already encode, and it cannot vary with animation.
            Vector3 canalLocalPos = canalBone.localPosition;
            Quaternion canalLocalRot = canalBone.localRotation;
            float herLossy = Mathf.Max(1e-4f, herKokan.lossyScale.y);

            int meshes = 0, remapped = 0, kept = 0, baked = 0;
            foreach (var smr in womb.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = smr.bones;
                if (bones == null || bones.Length == 0) continue;
                meshes++;
                var nb = new Transform[bones.Length];
                int kIdx = -1;
                // LEG + THIGH bones redirect to kokan-rigid - LIFTING swung cf_s_leg_R
                // 167mm LOCALLY and the natively-skinned womb skewed off the aim target while the
                // measurement basis exploded (RIGIDITY VIOLATED, spread 550mm, tipEst=0). Everything
                // ELSE matched re-points 1:1: waist01/spine01 and the DOME are also frozen (weighted
                // to them) compressed vs the approved mirror - measured mid→top 62mm REBIND vs 86mm
                // MIRROR on the small char (-28%), ~-4mm on a normal one. The torso bones are what
                // give the dome its her-belly follow; their excursions are moderate and mirror showed
                // the same follow in lifting without issue. (kIdx = the true cf_j_kokan slot by NAME -
                // redirected slots also reference herKokan but carry the wrong authored bindpose.)
                var redir = new List<int>();
                for (int i = 0; i < bones.Length; i++)
                {
                    Transform h = null;
                    string bn = bones[i] != null ? bones[i].name : null;
                    // bn is a WOMB bone (clo_-prefixed since womb 7.4.0); hers are plain-named.
                    string role = HerNameFor(bn);
                    if (role != null) hers.TryGetValue(role, out h);
                    if (h != null)
                    {
                        // (cf_s_siri dropped from this test: all 9 siri bones were PRUNED from the
                        // womb in 7.4.0, so no womb bone can map to that role any more.)
                        bool unstable = role.StartsWith("cf_s_leg") || role.StartsWith("cf_s_thigh")
                                     || role.StartsWith("cf_j_thigh");
                        if (!unstable) { nb[i] = h; if (role == "cf_j_kokan") kIdx = i; }
                        else { nb[i] = herKokan; redir.Add(i); }
                        remapped++;
                    }
                    else { nb[i] = bones[i]; kept++; }   // item-only bone: leave it on the item's own skeleton
                }
                smr.bones = nb;
                // The ONE lookup the rename missed: the rootBone is a WOMB bone (clo_cf_j_root since 7.4.0)
                // and must be remapped via its ROLE like the bones[] loop above. The raw name is worse
                // than a miss - `hers` is built from the wearer's whole subtree, womb included, so the
                // raw clo_ name matched the womb's OWN bone and self-assigned: a perfect silent no-op.
                // Under REBIND the item skeleton is frozen, so the un-remapped rootBone fed
                // ResolveRestSource a stale spawn-pose frame instead of her live pelvis.
                Transform hr = null;
                string rootRole = smr.rootBone != null ? HerNameFor(smr.rootBone.name) : null;
                if (rootRole != null && hers.TryGetValue(rootRole, out hr) && hr != null) smr.rootBone = hr;
                else if (smr.rootBone != null)
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: REBIND could not remap rootBone '"
                        + smr.rootBone.name + "' (role '" + rootRole + "') onto the wearer - the rest frame "
                        + "will read the frozen item skeleton. Send this log.");

                var mesh = smr.sharedMesh;
                if (mesh == null || kIdx < 0)
                {
                    // NO SILENT SKIP: if the bake can't be applied the womb renders at the wrong size,
                    // which is exactly the symptom that would then be chased blind.
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: REBIND BAKE SKIPPED on '" + smr.name
                        + "' — mesh=" + (mesh != null) + " kokanIdxInBones=" + kIdx
                        + " (cf_j_kokan is not one of this renderer's bones, so scale/offset could NOT be baked).");
                    continue;
                }
                var bp = mesh.bindposes;
                if (bp == null || kIdx >= bp.Length)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: REBIND BAKE SKIPPED on '" + smr.name
                        + "' — bindposes=" + (bp == null ? "null" : bp.Length.ToString()) + " kokanIdx=" + kIdx
                        + " (cannot bake scale/offset).");
                    continue;
                }

                // bindpose maps mesh -> kokan-LOCAL at bind time, so its inverse maps kokan-local -> mesh.
                // Deriving everything through THIS (instead of her live localToWorldMatrix) is what makes
                // the bake pose-independent and therefore deterministic across respawns.
                Matrix4x4 boneToMesh = bp[kIdx].inverse;
                // pivot = her kokan origin, which is the bone-local origin
                Vector3 pivotM = boneToMesh.MultiplyPoint3x4(Vector3.zero);
                // seat offset: -forward in bone-local (Transform.forward is local +Z), converted from a
                // world millimetre distance into bone-local units via her (pose-invariant) lossy scale.
                // seat offset + the commanded nudge, both in bone-LOCAL axes (pose-independent):
                // local +X = her right, +Y = along the canal, +Z = her forward.
                Vector3 nudgeLocal = RebindNudgeMM * (0.001f / herLossy);
                // + the KKS along-canal seat (local +Y = along the canal; 0 in KK) - same
                // pose-independent bone-local frame as the back seat and the commanded nudge.
                Vector3 offsetM = boneToMesh.MultiplyVector(Vector3.back * (backMM * 0.001f / herLossy)
                                                          + Vector3.up * (SeatAlongCanalMM * 0.001f / herLossy)
                                                          + nudgeLocal);
                // canal axis = the bone's authored local +Y, expressed in the parent (kokan) frame
                Vector3 axisM = boneToMesh.MultiplyVector(canalLocalRot * Vector3.up).normalized;   // kept for the parity term
                // ARCHITECTURAL: NO matrix-space STRETCH - ever. Deformation belongs to the
                // AUTHORED blendshapes only (womb_displace = elongation, rings = opening); an
                // anisotropic matrix stretch distorts everything indiscriminately (the "stretched
                // ovaries"). What the bake DOES carry, besides seat+nudge placement, is the UNIFORM SIZE
                // scale s (womb-scale config × sub-linear female term): proportional, distortion-free -
                // the rebind equivalent of base-bone scaling (her bones are shared with her body and
                // cannot be scaled). Canal opening self-adapts regardless of size (girthScale =
                // penisDia × margin / measured openEff), and the depth math self-measures the real mesh
                // (bakedCanal) - so uniform size is consistent by construction. Mirror-length-parity as
                // a goal is retired with the anisotropy.
                Matrix4x4 S = Matrix4x4.identity;
                S.m00 = s; S.m11 = s; S.m22 = s;
                // POSITION PARITY (measured: entrance sat 16mm low on kok 0.598, 6mm on kok 0.888 -
                // the offsets scale as (s-1), so this is the scale PIVOT, not a seat error).
                // MIRROR scales the womb UNIFORMLY about her kokan, so the entrance is displaced away from
                // the pivot in EVERY direction by s. The anisotropic S only stretches ALONG the canal axis,
                // so the entrance never receives its PERPENDICULAR displacement. The shortfall is exactly:
                //   uniform - aniso = (s-1)*r - (s-1)*(a.r)a = (s-1) * r_perp
                // Add it as a rigid translation: keeps the corrected (anisotropic) SHAPE while landing the
                // womb where the approved mirror puts it. Derived, not fitted.
                // the parity term ((s-1)*r_perp) is REMOVED - it existed solely to make the
                // anisotropic stretch reproduce a uniform scale's entrance placement. With S uniform again
                // that is true by construction, so keeping it would double-correct.
                // v' = pivot + S*(v - pivot) + offset
                Vector3 tM = pivotM - S.MultiplyPoint3x4(pivotM) + offsetM;
                Matrix4x4 M = S;
                M.m03 = tM.x; M.m13 = tM.y; M.m23 = tM.z;

                // PREDICTION SUPPORT: stash everything the analytic dual-chain predictor needs -
                // the ORIGINAL bindposes (pre-M) and the nudge-FREE bake matrix M0. The predictor
                // (WombExpandEffect.PredictParity) then computes mirror-chain vs rebind-chain entrance
                // positions from the same live bones and derives the nudge as their difference - the
                // "formula for any character" (mechanical simulation, nothing fitted).
                RebindOrigBindposes = bp;   // pre-instance copy (mesh.bindposes returned a fresh array)
                {
                    Vector3 tM0 = pivotM - S.MultiplyPoint3x4(pivotM) + boneToMesh.MultiplyVector(Vector3.back * (backMM * 0.001f / herLossy));
                    Matrix4x4 M0 = S;
                    M0.m03 = tM0.x; M0.m13 = tM0.y; M0.m23 = tM0.z;
                    RebindM0 = M0;
                }
                RebindPivotBone = herKokan; RebindS = s; RebindSeatBackMM = backMM;
                // INSTANCE the mesh first - bindposes live on the asset, and the shared one is used by
                // every other womb and by Studio. Never edit the shared asset.
                var inst = UnityEngine.Object.Instantiate(mesh);
                inst.name = mesh.name + "_CloXrayRebind";
                var nbp = inst.bindposes;
                // redirected bones take the KOKAN bindpose (their verts become extra kokan
                // weights - pelvis-rigid), then the bake multiplies every slot uniformly as before.
                for (int ri = 0; ri < redir.Count; ri++) nbp[redir[ri]] = bp[kIdx];
                for (int i = 0; i < nbp.Length; i++) nbp[i] = nbp[i] * M;
                inst.bindposes = nbp;
                smr.sharedMesh = inst;
                // ovaries read too big, so shrink them on SMALL chars only, ~15%. Drive ovary_shrink
                // scaled by HER pelvis size (petite -> more shrink; reference/large stay at the baked 10).
                // Plugin-side + H-only (this IS the rebind path) so it's character-scaled with NO global mesh
                // change. Set on the INSTANCED mesh (never the shared asset). ovary_shrink's full effect is a
                // ~40% ovary contraction, so weight 40 ≈ 16% smaller at the petite anchor (kokan 0.598).
                for (int bi = 0; bi < inst.blendShapeCount; bi++)
                {
                    if (!inst.GetBlendShapeName(bi).EndsWith("ovary_shrink")) continue;
                    float kok = herKokan != null ? herKokan.lossyScale.y : KokStd;
                    float ovW = Mathf.Lerp(40f, 10f, Mathf.Clamp01((kok - 0.598f) / (KokStd - 0.598f)));
                    smr.SetBlendShapeWeight(bi, ovW);
                    break;
                }
                baked++;
            }
            if (meshes == 0 || remapped == 0)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: REBIND FAILED — no skinned mesh bone matched the wearer (meshes=" + meshes + ", remapped=" + remapped + "). Applying NOTHING (no fallback).");
                return false;
            }

            // The measurement leaves ride the CANAL frame: clo_canal_entry is the anchor and
            // penis_target2 is authored as its CHILD, so reparent ONLY the anchor - PT2 rides
            // along and that relationship is preserved. World pose is kept, and the caller synced the
            // womb skeleton to her pose once immediately before this, so the anchor lands correctly;
            // CalibrateCanalToSkinnedMesh then refines it onto the truly-skinned canal.
            canalBone.SetParent(herKokan, true);

            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: REBIND (route B) OK — " + meshes + " mesh(es), " + remapped
                + " bones re-pointed at the wearer (non-core -> kokan-rigid), " + kept + " item-only bones kept, " + baked + " mesh(es) baked. "
                + "clo_canal_entry (+penis_target2) reparented under her cf_j_kokan. Per-frame mirror is OFF for this womb. "
                + "PHASE 2 bake: scale s=" + s.ToString("F3") + " about her kokan, seat back=" + backMM.ToString("F1") + "mm"
                + " | COMMANDED nudge=" + RebindNudgeMM.ToString("F1") + "mm (her frame: x=right y=alongCanal z=forward).");
            return true;
        }

        // place it immediately, the way mirror mode does - ONE-SHOT analytic placement. The fresh
        // rebind (nudge 0) already ran; wait only for the dual-chain predictor to fire (it needs the
        // canal BAND = canal DETECTED, which is fast - NOT the slow phase-locked calibration), then
        // apply its nudge in a single respawn via the snap path. It landed inside the accept
        // tolerance of the measured feedback loop it replaced (2.1mm y, 1.7mm z on the small
        // char), which is why that loop could be retired outright. MIRROR stays available in research builds
        // if a character ever proves the analytic model wrong.
        private static void ShowWomb(GameObject womb)
        {
            if (womb == null) return;
            foreach (var r in womb.GetComponentsInChildren<Renderer>(true)) if (r != null) r.enabled = true;
        }

        private static IEnumerator AutoPlacePredicted(MonoBehaviour host, Component female, GameObject womb)
        {
            PredictedValid = false;
            float deadline = Time.unscaledTime + 30f;
            while (Time.unscaledTime < deadline)
            {
                if (womb == null || female == null) yield break;
                if (PredictedValid) break;
                yield return null;
            }
            if (!PredictedValid)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: analytic placement predictor never fired within 30s (canal never detected?) — showing the naive rebind; Shift+Alt+B for MIRROR. No fallback loop.");
                ShowWomb(womb);   // never leave the hidden first womb invisible
                yield break;
            }
            Vector3 nudge = new Vector3(0f, PredictedNudgeMM.y, PredictedNudgeMM.z);   // never command x
            if (nudge.sqrMagnitude > 60f * 60f)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: predicted nudge implausible (" + nudge.ToString("F0") + "mm > 60mm) — showing the naive rebind; Shift+Alt+B for MIRROR.");
                ShowWomb(womb);
                yield break;
            }
            if (womb == null || female == null) { ShowWomb(womb); yield break; }
            RebindNudgeMM = nudge; NudgePlaced = true; _nudgeFemale = female;   // cache -> repeat spawns snap
            ToggleWhy = "nudge-bake"; Toggle(host); ToggleWhy = "nudge-bake"; Toggle(host);   // one respawn -> the snap path bakes the predicted nudge
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: PLACED via analytic prediction (one-shot, no loop) — nudge " + RebindNudgeMM.ToString("F1") + "mm, cached for this girl.");
        }

        /// <summary>Re-run the body reveal on every spawned womb's wearer - used by the F1
        /// "hands and limbs block the x-ray" toggle so it applies live, since Free-H has no
        /// MaterialEditor access. The existing-copy path re-asserts the mask cutoff per the flag.</summary>
        public static void ReassertBodyRevealOnWearers()
        {
            try
            {
                foreach (var kv in _spawned)
                {
                    if (kv.Key == null || kv.Value == null) continue;
                    var w = kv.Value.GetComponentInChildren<WombExpandEffect>(true);
                    if (w == null) continue;
                    MEBridge.EnsureBodyReveal(kv.Key, w.OrganStencil(), AutoBodyReveal.Debug, false);
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: limb-mask re-apply failed: " + e.Message); }
        }

        internal static string ToggleWhy = "hotkey";   // set by the caller so the log says who toggled
        public static void Toggle(MonoBehaviour host)
        {
            string why = ToggleWhy; ToggleWhy = "hotkey";
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: Toggle(" + why + ") — womb currently "
                + (AnySpawned() ? "SPAWNED" : "absent") + ".");
            // prune stale entries (H scene ended, character destroyed)
            var dead = new List<Component>();
            foreach (var kv in _spawned) if (kv.Key == null || kv.Value == null) dead.Add(kv.Key);
            foreach (var k in dead) _spawned.Remove(k);

            Component female = FindTargetFemale();
            if (female == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogMessage("CloXray: no female character found to toggle the womb on."
                    + "\n" + ScanCharactersForLog());
                return;
            }

            GameObject existing;
            if (_spawned.TryGetValue(female, out existing) && existing != null)
            {
                UnityEngine.Object.Destroy(existing);
                _spawned.Remove(female);
                // AFTER the map entry is gone: RemoveForWomb asks AnySpawned() whether any womb is
                // still set up before it touches the male, and this one must not count itself.
                // Cleanup is best-effort by design: the toggle (and the coroutine that may be driving
                // it) must complete even if MaterialEditor refuses, or a half-toggle leaves no womb.
                try { AutoBodyReveal.RemoveForWomb(female); }
                catch (Exception rex) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: x-ray cleanup threw on womb removal — continuing: " + rex.Message); }
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: womb removed from '" + female.name + "'.");
                return;
            }

            // BP UNCENSOR FIRST. Forcing it AFTER the womb exists reloads her body underneath the womb,
            // which kills it: the x-ray copies are re-applied but the womb stays bound to the body that
            // is gone, so the press looks ignored and gets pressed again. Force before spawning and
            // let the reload finish; the deferred spawn below then lands on the finished body. Both
            // forces are once-per-character-per-session, so this costs nothing after the first press.
            if (!IsStudio)
            {
                ReloadPendingUntil = 0f;
                // HER BODY FIRST, ALONE. Swapping his penis/balls while her body reload is still in
                // flight produced WHITE balls: the swap lands mid-reload and the mesh comes up untextured
                // (both BP balls variants render correctly when set by hand, so it is the moment, not the
                // choice). The deferred pass below re-enters here once her body is ready and does the
                // male then - her force is a no-op by that point, so the order is body -> settle -> male.
                TryForceBPUncensor(female, 2);
                if (ReloadPendingUntil <= Time.unscaledTime)
                {
                    Component male0 = FindNearestMaleWithPenis(female.transform.position, 2f, female);
                    if (male0 != null) { TryForceBPUncensor(male0, 0); TryForceBPUncensor(male0, 1); }
                }
                if (ReloadPendingUntil > Time.unscaledTime)
                {
                    // The poll (LiquidShaderWobblePlugin.Update) re-arms every 0.1s until BpBodyReady
                    // says her BP vagina bones exist; the deadline is the loud giving-up point.
                    DeferredSpawnFemale = female;
                    DeferredSpawnAt = Time.unscaledTime + 0.1f;      // first poll almost immediately
                    DeferredSpawnDeadline = Time.unscaledTime + 8f;  // loud giving-up point
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP uncensor forced first — spawning the womb once the body reload finishes.");
                    return;
                }
            }

            GameObject prefab = null;
            try { prefab = CommonLib.LoadAsset<GameObject>(Bundle, Prefab, false, ""); }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb bundle load threw: " + e.Message); }
            if (prefab == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: could not load '" + Prefab + "' from '" + Bundle + "' — is [Clo]XrayWomb1.zipmod installed?");
                return;
            }

            InstallPoseHook();   // once: re-measure the penis fit on every animation change
            InstallDanMotionHook();   // once: HFlag-penetrated overrides BP's under-fed motion input (KKS idle fix)
            var go = UnityEngine.Object.Instantiate(prefab, female.transform, false);
            go.name = "CloXray_Womb_H";
            // PT-FOLLOWS-CANAL (marker-verified in-game): `penis_target` is AUTHORED
            // under cf_j_waist01; in H the bone mirror drives that from the girl's LIVE waist, which
            // bends independently of the canal - the aim swung off the canal top in bent poses. The
            // zipmod now ships `penis_target2` - the CANAL-FRAME twin, authored under clo_canal_entry
            // at the same rest world position (old scenes keep the old bone: NodesConstraints persists
            // constraints as TRANSFORM PATHS, so the old bone must never move in the hierarchy).
            // Hard rule: the zipmod and the DLL always ship together - an absent penis_target2 is
            // a mismatch, not a supported configuration. Loud error, nothing applied, no legacy handling.
            if (FindDeep(go.transform, "penis_target2") == null)
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb has NO penis_target2 — [Clo]XrayWomb1.zipmod does not match this DLL. Update the zipmod; the penis aim will NOT be steered.");
            var mirror = go.AddComponent<WombBoneMirror>();
            mirror.Bind(female.transform);
            mirror.SyncNow();          // position the skeleton BEFORE anything measures it
            // CONTROL: force updateWhenOffscreen on for BOTH placement paths. With Unity's default
            // (false) a renderer that is off-screen skips its skinning/bounds update, so BakeMesh-derived
            // measurements - and the canal calibration that MOVES/ROTATES the canal bone from them - can
            // read stale data depending on the camera view. This was set only in the rebind path, which
            // made the two modes measure under different conditions; that asymmetry is now removed.
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r != null) r.updateWhenOffscreen = true;
            // ROUTE-B: hand placement over to Unity's skinning. A fresh girl rebinds at nudge 0 and
            // AutoPlacePredicted bakes the analytic correction in ONE respawn; every later toggle for
            // her snaps straight to the cached value.
            CurrentlyRebound = false;
            if (UseRebind)
            {
                if (!ReferenceEquals(female, _nudgeFemale))
                {   // different girl: her correction is not this girl's - place from scratch
                    RebindNudgeMM = Vector3.zero;
                    NudgePlaced = false;
                    _nudgeFemale = female;
                }
                if (NudgePlaced)
                {   // SNAP: this girl is already placed - bake her cached nudge instantly
                    if (RebindWombToWearer(go, female.transform))
                    {
                        mirror.Rebound = true; CurrentlyRebound = true;
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: REBIND snap — using this character's cached placement ("
                            + RebindNudgeMM.ToString("F1") + "mm world).");
                    }
                    else LiquidWobbleMPBPlugin._logger?.LogError("CloXray: REBIND snap rebind FAILED — womb is NOT driven.");
                }
                else
                {   // fresh girl - rebind IMMEDIATELY (nudge 0), then let the analytic predictor
                    // place it in ONE respawn (no mirror-first, no respawn churn).
                    if (RebindWombToWearer(go, female.transform))
                    {
                        mirror.Rebound = true; CurrentlyRebound = true;
                        // hiding the renderer broke placement - a disabled SkinnedMeshRenderer
                        // does NOT skin, so the canal calibration read the un-posed rest mesh and pinned
                        // the canal ~416mm up (womb far from the penis -> no reaction). Reverted: the womb
                        // is visible during the ~1s predict (the small nudge-0->predicted shift returns),
                        // which is the lesser evil vs a broken canal. The jump is first-spawn-per-char only.
                        host.StartCoroutine(AutoPlacePredicted(host, female, go));
                    }
                    else LiquidWobbleMPBPlugin._logger?.LogError("CloXray: fresh rebind FAILED — womb is NOT driven. Toggle to MIRROR (Shift+Alt+B).");
                }
            }
            _spawned[female] = go;
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: womb spawned on '" + female.name + "'.");

            // The baked bootstrap component attaches the wobble + womb effects in its Start;
            // give it a couple of frames, then apply the x-ray stack DIRECTLY to the known wearer
            // (no proximity search - H characters carry a second un-animated proxy skeleton that
            // fools bone-distance matching).
            host.StartCoroutine(ApplySoon(go, female));
        }

        private static IEnumerator ApplySoon(GameObject womb, Component female)
        {
            yield return null;
            yield return null;
            yield return null;
            if (womb == null || female == null) yield break;
            var fx = womb.GetComponentInChildren<WombExpandEffect>(true);
            if (fx == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: WombExpandEffect not attached after spawn — bootstrap missing? No x-ray applied.");
                yield break;
            }
            AutoBodyReveal.ApplyForWomb(fx, female);
        }

        // The female to toggle: nearest female ChaControl to the camera (in H that is the
        // heroine on screen). Bone-walk pattern mirrors AutoBodyReveal: find a cf_j_kokan
        // that is NOT part of a womb item, walk up to the ChaControl, gate on sex==female.

        // "No female found" is a dead end on its own: the womb toggle needs a transform named cf_j_kokan
        // under a ChaControl with sex==1, and the message named none of the three things that can be
        // missing. So when the search fails, say what WAS there. The low-poly body genuinely ships
        // without cf_j_kokan, so "character present, sex right, no kokan" is a real outcome and has to be
        // distinguishable from "no characters at all" and from "only males here". Failure path only - the
        // per-transform GetComponent sweep is far too heavy to run on a successful toggle.

        // Full hierarchy path, for diagnostics that have to distinguish two same-named transforms.
        internal static string PathOf(Transform t)
        {
            if (t == null) return "(null)";
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        internal static string ScanCharactersForLog()
        {
            try
            {
                var seen = new List<Component>();
                var sb = new System.Text.StringBuilder();
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                {
                    if (t == null) continue;
                    var cc = t.GetComponent("ChaControl");
                    if (cc == null || seen.Contains(cc)) continue;
                    seen.Add(cc);
                }
                if (seen.Count == 0) return "  scan: NO ChaControl in the scene at all (not in a game/Studio scene with characters loaded?).";
                sb.Append("  scan: ").Append(seen.Count).Append(" character(s) present -");
                foreach (var cc in seen)
                {
                    string sex = "?";
                    try
                    {
                        var pr = cc.GetType().GetProperty("sex");
                        if (pr != null) sex = Convert.ToInt32(pr.GetValue(cc, null)).ToString();
                        else { var fl = cc.GetType().GetField("sex"); if (fl != null) sex = Convert.ToInt32(fl.GetValue(cc)).ToString(); }
                    }
                    catch { }
                    // Report the EVIDENCE, not a verdict. "cf_j_kokan=NO" was printed against a
                    // character the same log shows the womb placing on, so the naive test misjudged
                    // something and a guess ("low-poly?") only sent the reader down the wrong path.
                    // hiPoly is the flag that actually selects the bone-poor body, the transform count
                    // separates 580-node from 338-node rigs, and every kokan-ish transform is listed with
                    // its owner so a womb copy, a prefixed name or a mid-reload skeleton is visible on sight.
                    string hiPoly = "?";
                    try
                    {
                        var pf = cc.GetType().GetField("hiPoly");
                        if (pf != null) hiPoly = pf.GetValue(cc).ToString();
                        else { var pp = cc.GetType().GetProperty("hiPoly"); if (pp != null) hiPoly = pp.GetValue(cc, null).ToString(); }
                    }
                    catch { }
                    var all = cc.GetComponentsInChildren<Transform>(true);
                    bool kokan = false; var kokanLines = new System.Text.StringBuilder();
                    foreach (var d in all)
                    {
                        if (d == null || d.name.IndexOf("kokan", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        // The search itself no longer excludes womb subtrees (the womb has no
                        // cf_j_kokan since 7.4.0), so the diag models it 1:1: any name match counts.
                        // The ownership column stays as EVIDENCE - it is what caught the earlier
                        // misattachment (a WombExpandEffect sitting on chaF_001 itself).
                        var owner = d.GetComponentInParent<WombExpandEffect>();
                        bool ours = owner != null;
                        if (d.name == "cf_j_kokan") kokan = true;
                        kokanLines.Append("\n").Append("        '").Append(d.name).Append("' ")
                                  .Append(ours ? "inside WombExpandEffect subtree of '" + PathOf(owner.transform) + "'" : "(hers)")
                                  .Append(" active=").Append(d.gameObject.activeInHierarchy)
                                  .Append(" path=").Append(PathOf(d));
                    }
                    string siblings = "";
                    foreach (var probe in new[] { "cf_d_kokan", "cf_s_leg_L", "cf_s_thigh01_L" })
                    {
                        bool has = false;
                        foreach (var d in all) if (d != null && d.name == probe) { has = true; break; }
                        siblings += " " + probe + "=" + (has ? "yes" : "NO");
                    }
                    sb.Append("\n").Append("    '").Append(cc.name).Append("' sex=").Append(sex)
                      .Append(sex == "1" ? " (female)" : sex == "0" ? " (MALE - the womb only targets females)" : "")
                      .Append(" hiPoly=").Append(hiPoly).Append(" transforms=").Append(all.Length)
                      .Append(" | usable cf_j_kokan=").Append(kokan ? "yes" : "NO")
                      .Append(" | low-poly probes:").Append(siblings)
                      .Append(kokanLines.Length > 0 ? kokanLines.ToString() : "\n" + "        (no transform with 'kokan' in the name at all)");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "  scan failed: " + e.Message; }
        }

        internal static Component FindTargetFemale()
        {
            Vector3 eye = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            Component best = null; float bestSq = float.MaxValue;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.name != "cf_j_kokan") continue;
                // No womb-subtree exclusion: since 7.4.0 the womb's copy is clo_cf_j_kokan and cannot
                // match this name. The old guard was worse than dead - a WombExpandEffect wrongly
                // sitting on the character made it exclude HER whole skeleton.
                Component cc = null;
                for (var c = t; c != null; c = c.parent) { cc = c.GetComponent("ChaControl"); if (cc != null) break; }
                if (cc == null || !IsFemale(cc)) continue;
                float d = (t.position - eye).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = cc; }
            }
            return best;
        }

        // Nearest MALE character carrying a penis material, within maxRange of a world point.
        // Same bone-walk pattern as the female finder (males have cf_j_kokan too; the proxy
        // skeleton duplicate resolves to the same ChaControl, so it can't mislead here).
        public static Component FindNearestMaleWithPenis(Vector3 pos, float maxRange, Component exclude)
        {
            Component best = null; float bestSq = maxRange * maxRange;
            var seen = new List<Component>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.name != "cf_j_kokan") continue;
                Component cc = null;
                for (var c = t; c != null; c = c.parent) { cc = c.GetComponent("ChaControl"); if (cc != null) break; }
                if (cc == null || cc == exclude || seen.Contains(cc)) continue;
                seen.Add(cc);
                if (IsFemale(cc)) continue;
                if (!MEBridge.HasPenisMaterial(cc)) continue;
                float d = (t.position - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = cc; }
            }
            return best;
        }

        // MAIN GAME aiming. The k_f_dan markers are declared but NEVER READ by main-game BP (verified
        // in its source) - the real lever is the female CollisionAgent's m_innerTarget: BP constrains
        // the penis END to it (default cf_j_waist01, far past the dome - hence the overshoot). Swap
        // that reference to the womb's penis_target and flip the male's simplifyVaginal so the constraint
        // pins the tip exactly there - the Studio look through BP's own pipeline. All reflection,
        // fail-loud (BP 5.x field names may drift from the reference source).
        // BP's OWN "Simplify Penetration Calculation" config entries (Male 1 + Male 2), forced ON while
        // a womb is attached and RESTORED on removal. Setting the ConfigEntry fires BP's SettingChanged
        // -> BP itself re-applies simplify to its agents through its own pipeline - so every future
        // config-rebuild of DanOptions produces simplify=true AT THE SOURCE (nothing to poll/catch).
        private static object[] _bpSimpEntries;
        private static bool[] _bpSimpSaved;
        private static bool _bpSimpForced;

        // AGENT WATCHDOG: BP re-creates its CollisionAgent/DanAgent objects on H init/reload -
        // then every cached reflection handle points at a DEAD object: the pin writes, size writes
        // and penis-line reads all silently no-op while the LIVE agents run BP defaults (inner limit
        // cf_j_waist01 = the tip aimed into the FRONT WALL; a whole session ran like this: ref=- in
        // every H-STATE line, 100mm pin offsets doing nothing). The pin's 2s tick verifies its cached
        // objects are still IN BP's live lists and re-runs the attach when they are not.
        private static System.Reflection.FieldInfo _sfAgents, _sfDans;
        private static WombExpandEffect _sWomb; private static Component _sMale, _sFemale;

        public static bool BPAgentsAlive(object colAgent, object danAgent)
        {
            try
            {
                if (_sfAgents == null || _sfDans == null) return true;
                var ca = _sfAgents.GetValue(null) as System.Collections.IEnumerable;
                var da = _sfDans.GetValue(null) as System.Collections.IEnumerable;
                if (ca == null || da == null) return true;
                bool anyC = false, anyD = false, hasC = false, hasD = false;
                foreach (var a in ca) { if (a == null) continue; anyC = true; if (ReferenceEquals(a, colAgent)) hasC = true; }
                foreach (var d in da) { if (d == null) continue; anyD = true; if (ReferenceEquals(d, danAgent)) hasD = true; }
                // Empty lists = H tearing down / not yet initialized: report alive so the watchdog
                // doesn't thrash re-attaches against a scene that has no agents to bind to.
                if (!anyC || !anyD) return true;
                return hasC && hasD;
            }
            catch { return true; }
        }

        public static void ReattachPenisAim()
        {
            if (_sWomb != null && _sFemale != null) AttachPenisAim(_sWomb, _sMale, _sFemale);
            // A body reload (BP5 uncensor force / an uncensor swap) recreates the penis renderer and
            // WIPES its instanced x-ray .MECopy materials - re-apply them, or BP drives a
            // correctly-posed but INVISIBLE penis in the womb (the KKS symptom: its default penis
            // uncensor isn't BP, so the force always reloads).
            // BP LOST HER. m_referenceTarget null while the game says PENETRATED means BP's dan agent is
            // not bound to any character: it resets the dan (penis leaves the womb) and feeds no depth
            // (canal stops reacting), and respawning the womb cannot help because the womb is not the
            // problem. Studio already re-binds through WobbleSceneController; the main game never did.
            // Condition = the field the fault actually showed (refTarget='-'). CollisionCharacterOf()
            // reports null even on a correctly bound scene, so keying off it re-bound a healthy couple
            // every watchdog tick and logged 23 FAILEDs in a session with no actual fault.
            if (!IsStudio && HPenetrated && BpRefTargetMissing && _sMale != null && _sFemale != null)
            {
                bool ok = BPBridge.RebindCollisionAgent(_sMale, _sFemale, true, false);
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP had NO collision target while penetrated — re-bound '"
                    + _sMale.name + "' to '" + _sFemale.name + "': " + (ok ? "OK" : "FAILED"));
            }
            if (_sWomb != null && _sMale != null) AutoBodyReveal.ReapplyMainGamePenisXray(_sWomb, _sMale);
            // HER body reload wipes the body/veil/clothes copies the same way - without this the womb
            // is invisible until the hotkey is pressed a second time.
            if (_sWomb != null && _sFemale != null) AutoBodyReveal.ReapplyMainGameBodyXray(_sWomb, _sFemale);
        }

        // ── BP5 UNCENSOR DEFAULT FORCE (by design - the same way BP's simplify is forced):
        // BP only drives meshes authored with its bones (uncensors with "BP5" in the name). Instead of
        // touching per-character data, force UncensorSelector's F1 DEFAULT entries (`Default Male
        // Penis` / `Default Female Body`, which store DISPLAY NAMES) to an installed BP5 uncensor -
        // then UncensorSelector's own resolution does exactly what was specified: a card WITH an
        // explicit selection uses it (never overridden; the log says what to switch); a card WITHOUT one
        // resolves through the (now BP5) default. Originals saved + RESTORED on womb removal, same as
        // ForceBPSimplifyConfig. Already-loaded default-resolving characters are re-resolved via the
        // controller's own public UpdateUncensor(); the body reload re-creates BP's agents and the
        // The agent watchdog re-attaches + re-checks health automatically.
        // kind: 0 = male penis, 1 = male balls, 2 = female body. The penis default should be
        // the "BP SOS" uncensor, balls the BP balls; and in Free-H the hotkey force OVERRIDES even a
        // card-explicit selection (pressing the womb hotkey IS the consent) - session-only, H never
        // saves cards.
        private static readonly HashSet<string> _uncForced = new HashSet<string>();
        // per-character caches for the spawn-time BP-uncensor bone check (see AttachPenisAim) -
        // _vagBonesOk = satisfied, stop scanning; _vagWarned = error logged once, not once per pose change.
        private static readonly HashSet<int> _vagBonesOk = new HashSet<int>();
        private static readonly HashSet<int> _vagWarned = new HashSet<int>();
        private static readonly string[] _uncDictName  = { "PenisDictionary", "BallsDictionary", "BodyDictionary" };
        private static readonly string[] _uncGuidProp  = { "PenisGUID", "BallsGUID", "BodyGUID" };
        // The resolved-data property beside each GUID: when a card saved no explicit pick, this is what
        // says which uncensor is ACTUALLY in use (UncensorSelector's own Studio menu reads it the same way).
        private static readonly string[] _uncDataProp  = { "PenisData", "BallsData", "BodyData" };
        private static readonly string[] _uncPartLabel = { "penis", "balls", "body" };
        // the per-part MESH RELOAD (separate from UpdateUncensor, which only re-resolves the selection).
        private static readonly object[] _uncEntry = new object[3];
        private static readonly string[] _uncSaved = new string[3];
        private static readonly bool[] _uncCfgForced = new bool[3];

        // A female body uncensor on a male reloads his body as a female one and his penis comes back as
        // the vanilla mosaic - and if the scene is saved in that state, it stays that way. The womb-
        // proximity search can reach a male standing against the wearer, so this guard is what keeps a
        // body-slot force strictly on the character it was meant for.
        // ── WOMB BONE NAMES ────────────────────────────────────────────────────────────────────────
        // The womb item's own bones carry a `clo_` prefix (womb build 7.4.0+). They were once exact
        // copies of HER bone names, and ABMX indexes bones by BARE NAME with first-match-wins while
        // excluding only accessories and other characters - so a womb PARENTED under a character in
        // Studio could win her names, and her ABMX modifiers were then silently never applied. Her
        // bones just sat at default.
        //
        // Two rules for every bone lookup in this plugin, because the same literal used to mean both:
        //   - a bone of HERS  -> plain name, no prefix (wearer/hers/theirs lookups)
        //   - a bone of the WOMB -> WombBone(...) or one of the constants below
        internal const string WombBonePrefix = "clo_";
        internal static string WombBone(string role) { return WombBonePrefix + role; }

        // Strip the prefix to get the name of HER equivalent bone. The mirror and the route-B rebind
        // match womb bone -> her bone by name, which only worked before because the names were
        // identical; now the mapping is explicit. Returns an unprefixed name unchanged, so this is one
        // path that reads both PREFIXED womb bones and her PLAIN names after a REBIND swaps
        // _smr.bones onto her skeleton - a name normalisation,
        // not a fallback to a second implementation.
        internal static string HerNameFor(string wombBoneName)
        {
            return (wombBoneName != null && wombBoneName.StartsWith(WombBonePrefix))
                 ? wombBoneName.Substring(WombBonePrefix.Length) : wombBoneName;
        }

        internal static bool IsMaleChara(Component cha)
        {
            try
            {
                var t = cha.GetType();
                var f = t.GetProperty("sex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (f != null) return Convert.ToInt32(f.GetValue(cha, null)) == 0;
                var fi = t.GetField("sex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (fi != null) return Convert.ToInt32(fi.GetValue(cha)) == 0;
            }
            catch { }
            return false;
        }
        private static void TryForceBPUncensor(Component cha, int kind)
        {
            if (cha == null) return;
            if (kind == 2 && IsMaleChara(cha))
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: refusing to set a body uncensor on the male '" + cha.name + "' - body uncensors are female-side."); return; }
            string key = cha.GetInstanceID() + ":" + kind;
            if (_uncForced.Contains(key)) return;
            _uncForced.Add(key);
            string part = _uncPartLabel[kind];
            try
            {
                const System.Reflection.BindingFlags BI = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                const System.Reflection.BindingFlags BS = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                Component ctl = null;
                foreach (var c in cha.GetComponents<Component>())
                    if (c != null && c.GetType().Name == "UncensorSelectorController") { ctl = c; break; }
                if (ctl == null) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector controller not found on '" + cha.name + "' — cannot force a BP " + part + " uncensor (is UncensorSelector installed?)."); return; }
                var tc = ctl.GetType();

                // installed uncensors: the plugin's static dictionary (key = GUID, value has DisplayName)
                object dict = null;
                foreach (var t in tc.Assembly.GetTypes())
                {
                    var pd = t.GetProperty(_uncDictName[kind], BS);
                    if (pd != null) { dict = pd.GetValue(null, null); }
                    else { var fd = t.GetField(_uncDictName[kind], BS); if (fd != null) dict = fd.GetValue(null); }
                    if (dict != null) break;
                }
                var entries = dict as System.Collections.IDictionary;
                if (entries == null) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector " + _uncDictName[kind] + " not found — cannot enumerate installed uncensors."); return; }

                Func<object, string> display = v => { if (v == null) return ""; var t = v.GetType(); var p = t.GetProperty("DisplayName", BI); if (p != null) return p.GetValue(v, null) as string ?? ""; var f = t.GetField("DisplayName", BI); return f != null ? f.GetValue(v) as string ?? "" : ""; };
                Func<object, int> sexOf = v => { try { var t = v.GetType(); var p = t.GetProperty("Sex", BI); if (p != null) return Convert.ToInt32(p.GetValue(v, null)); var f = t.GetField("Sex", BI); if (f != null) return Convert.ToInt32(f.GetValue(v)); } catch { } return -1; };

                // Candidates = display names containing "BP" (BP's uncensors: "BP SOS", "BP balls",
                // BP5-tagged bodies...). The penis slot PREFERS a name also containing "SOS" (as
                // default choice); alphabetical among equals for determinism.
                string bestName = null, bestGuid = null; bool bestPreferred = false; var all = new List<string>();
                foreach (System.Collections.DictionaryEntry e in entries)
                {
                    string dn = display(e.Value);
                    all.Add(dn);
                    if (dn.IndexOf("BP", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (kind == 2 && sexOf(e.Value) == 0) continue;   // female body: skip male-sex bodies
                    // kind 1 (balls): prefer a COLOR MATCH variant. The plain "BP Balls" carries no skin
                    // tint, so on a forced swap the balls render WHITE against her/his skin; the color
                    // match ones take the character's own tone. (kind 0 keeps the SoS preference.)
                    bool pref = (kind == 0 && dn.IndexOf("SOS", StringComparison.OrdinalIgnoreCase) >= 0)
                             || (kind == 1 && dn.IndexOf("COLOR MATCH", StringComparison.OrdinalIgnoreCase) >= 0);
                    bool better = bestName == null
                        || (pref && !bestPreferred)
                        || (pref == bestPreferred && string.CompareOrdinal(dn, bestName) < 0);
                    if (better) { bestName = dn; bestGuid = e.Key as string; bestPreferred = pref; }
                }
                // Log every candidate: the zipmod FILE names ("[KKS][Balls][BP] Color Match.zipmod") are
                // not what UncensorSelector reports as the display name, so picking by guessed substrings
                // silently lands on the wrong variant (the white-balls report).
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: " + part + " uncensor candidates: "
                    + string.Join(" | ", all.ToArray()) + "  -> picked '" + (bestName ?? "none") + "'.");
                if (bestName == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no installed " + part + " uncensor with 'BP' in its name — install BetterPenetration's uncensor zipmods. Installed: " + string.Join(" | ", all.ToArray()));
                    return;
                }

                // The GLOBAL UncensorSelector defaults are NOT touched. They live in that plugin's
                // config FILE, so writing them leaks out of the session and out of this game: every
                // character with no explicit pick silently resolves through this value, including males
                // whose penis then loads as the vanilla mosaic. The per-character GUID set below is
                // always written (see the note there), which covers the card that needs it and nobody
                // else. RestoreBPUncensorDefaults stays for sessions that ran the old path.
                // 2) re-resolve THIS character now. Card WITHOUT a selection: reload resolves through
                // the new default. Card WITH one (hard rule): the Free-H hotkey force
                //    OVERRIDES it - pressing the womb hotkey is the consent; session-only, H never
                //    saves cards (the card file is untouched).
                // KKS: the force logged but the BP body never applied - no cf_J_Vagina* bones afterwards.
                // TWO defects found by dumping KKS_UncensorSelector's real members:
                // (a) the per-character GUID is set ONLY when one was already set. A card with NO explicit
                // selection (default was 'Random') left `cur` empty, so it was skipped
                //      the GUID entirely and relied on the changed default alone. Now ALWAYS set it: an
                // explicit GUID is the deterministic path, and womb spawn is the consent.
                //  (b) `UpdateUncensor()` does NOT reload the body mesh - `ReloadCharacterBody()` /
                //      `ReloadCharacterPenis()` / `ReloadCharacterBalls()` are SEPARATE members and are what
                //      actually swap the mesh (hence the bones never appeared). Call the per-kind reload too.
                var pGuid = tc.GetProperty(_uncGuidProp[kind], BI);
                string cur = pGuid != null ? pGuid.GetValue(ctl, null) as string : null;

                // ALREADY BP -> DO NOTHING (hard rule 2026-08-01: if F already has BP, no need).
                // The reload this used to run is never free: UncensorSelector's body/penis/balls reload
                // is a PARTIAL reload, and MaterialEditor has no handler for it (its re-apply hooks are
                // KKAPI-level - ClothesStateChange / CoordinateChanged / ChangeCustomClothes / ChangeHair /
                // AccessoryTransferred - and the patches calling RefreshBodyEdits are #if PH). So every
                // swap silently drops the character's own material edits: a KKUTS skin comes back as
                // vanilla Main_Skin. Doing that to a character who ALREADY has a BP uncensor buys nothing,
                // and picking a different BP variant over the chosen one is worse than leaving it alone.
                // Read the EFFECTIVE selection, not just the explicit GUID: a card with no explicit pick
                // resolves through the default, and that resolved value is what actually loaded.
                string curName = null;
                if (!string.IsNullOrEmpty(cur) && entries.Contains(cur)) curName = display(entries[cur]);
                if (string.IsNullOrEmpty(curName))
                {
                    var pData = tc.GetProperty(_uncDataProp[kind], BI);
                    object dat = pData != null ? pData.GetValue(ctl, null) : null;
                    if (dat != null) curName = display(dat);
                }
                if (!string.IsNullOrEmpty(curName) && curName.IndexOf("BP", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: '" + cha.name + "' " + part
                        + " uncensor is already BP-driven ('" + curName + "') - leaving it alone (no reload, their own material edits stay).");
                    return;
                }
                // Belt and braces for her body: the BP vagina bones ARE the requirement, so if they are
                // already on the mesh the body is BP-capable whatever the selection is called.
                if (kind == 2 && BpBodyReady(cha))
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: '" + cha.name
                        + "' already has BP vagina bones - no body uncensor force needed.");
                    return;
                }

                if (pGuid == null)
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector controller has no '" + _uncGuidProp[kind] + "' property — cannot select the BP " + part + " uncensor per character.");
                else if (cur != bestGuid)
                {
                    if (string.IsNullOrEmpty(curName)) curName = "(none — resolving through the default)";
                    pGuid.SetValue(ctl, bestGuid, null);
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: '" + cha.name + "' " + part + " uncensor was " + curName + " (not BP-driven) — SET to '" + bestName + "' for this H session (womb spawn = consent; the card file is untouched).");
                }
                // ONE public call. UncensorSelector's own UI path is `GUID = x; UpdateUncensor();` and
                // that method starts ReloadCharacterUncensor(), whose coroutine does:
                //     ReloadCharacterBody(); ReloadCharacterPenis(); ReloadCharacterBalls(); UpdateSkin();
                // The ReloadCharacter* methods are PRIVATE and only swap meshes - calling them directly
                // (as this did) skipped UpdateSkin(), so a rebuilt mesh came back with no texture: the
                // balls rendered WHITE (o_dan_f / cm_m_dan_f, tex=NULL). It is also asynchronous, which is
                // why an immediate check after it looked like "the body did not reload" and led to the
                // private calls in the first place. Let the coroutine do the whole job; the spawn already
                // waits on BpBodyReady() for it to finish.
                // RELOAD ONLY THE CHANGED SLOT.
                // UncensorSelector source: UpdateUncensor() => StartCoroutine(ReloadCharacterUncensor()),
                // and that coroutine runs
                //     if (ExType == 0) ReloadCharacterBody(); ReloadCharacterPenis(); ReloadCharacterBalls(); UpdateSkin();
                // back-to-back with NO yields between them - all four are plain private void methods. So
                // the male path below is exactly what the coroutine does minus the body reload, which he
                // never needed: only his penis/balls GUID is ever set (a body uncensor is refused above),
                // yet the one public call rebuilt his body too and took his own skin shader with it.
                // UpdateSkin() is REQUIRED with them, never optional - the private reloads swap meshes and
                // skip the skin pass, which is what made the balls come back white.
                bool maleSlot = kind != 2;
                try
                {
                    if (maleSlot)
                    {
                        string reloadName = kind == 0 ? "ReloadCharacterPenis" : "ReloadCharacterBalls";
                        var mPart = tc.GetMethod(reloadName, BI);
                        var mSkin = tc.GetMethod("UpdateSkin", BI);
                        if (mPart == null || mSkin == null)
                        {
                            LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector controller is missing "
                                + (mPart == null ? reloadName + "()" : "UpdateSkin()")
                                + " — cannot apply the BP " + part + " uncensor without also rebuilding his body, so nothing was applied. Pick a BP "
                                + part + " uncensor by hand in the UncensorSelector menu.");
                            return;
                        }
                        mPart.Invoke(ctl, null);
                        mSkin.Invoke(ctl, null);
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: " + reloadName + "() + UpdateSkin() on '" + cha.name
                            + "' — " + part + " swapped; his body was NOT reloaded, so his own material edits are untouched.");
                    }
                    else
                    {
                        var mUpd = tc.GetMethod("UpdateUncensor", BI);
                        if (mUpd == null)
                        {
                            LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector controller has no public UpdateUncensor() — cannot apply the BP " + part + " uncensor.");
                            return;
                        }
                        // ARM BEFORE INVOKING. Unity runs a coroutine up to its first yield synchronously
                        // inside StartCoroutine, so the body reload can be finished by the time Invoke
                        // returns - and Arm() clears the done flag, which would swallow the very event
                        // are waiting for. Her body IS being replaced (a BP vagina is a body uncensor), so
                        // MaterialEditor's edits go with the old mesh and nothing on KK/KKS re-applies them.
                        UncBodyReloadWatch.Arm(ctl, cha);
                        MeRefreshChara = cha;
                        MeRefreshDeadline = Time.unscaledTime + 8f;
                        mUpd.Invoke(ctl, null);
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: UpdateUncensor() invoked on '" + cha.name + "' — " + part
                            + " mesh + skin pass reloading (async).");
                        if (!IsStudio) ReloadPendingUntil = Time.unscaledTime + 1.5f;
                        // Already-spawned womb (a later force, e.g. after a character swap): respawn it onto
                        // the rebuilt body once the coroutine has finished.
                        if (!IsStudio && AnySpawned()) RespawnAt = Time.unscaledTime + 1.5f;
                    }
                }
                catch (Exception re) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: applying the BP " + part + " uncensor failed on '" + cha.name + "': " + re.Message); }
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: '" + cha.name + "' " + part + " re-resolving as '" + bestName + "'. Body reloads; BP re-inits; the agent watchdog re-binds within 2s.");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BP " + part + " uncensor force failed: " + e.GetType().Name + ": " + e.Message); }
        }

        // Gives a swapped-in card the scene's body uncensor the way UncensorSelector itself expects:
        // written into the CARD's own uncensor data before its controllers read it. UncensorSelector's
        // OnReload nulls its GUIDs, re-reads them from that data and calls UpdateUncensor() on its own,
        // so the incoming character comes up on the right body the first time. Setting the GUID on the
        // live controller instead means racing that reload, which discards whatever was set.
        internal static class UncensorInject
        {
            private const string UncensorGuid = "com.deathweasel.bepinex.uncensorselector";
            private const float ArmWindow = 8f;   // the card load follows the replace call immediately
            private static string _pending;
            private static float _armedAt;
            private static bool _subscribed;

            internal static void Arm(string bodyGuid)
            {
                if (string.IsNullOrEmpty(bodyGuid) || !IsStudio) return;
                if (!_subscribed)
                {
                    _subscribed = true;
                    ExtensibleSaveFormat.ExtendedSave.CardBeingLoaded += OnCardBeingLoaded;
                }
                _pending = bodyGuid;
                _armedAt = UnityEngine.Time.realtimeSinceStartup;
                LiquidWobbleMPBPlugin._logger?.LogInfo("UncensorInject: the next card loaded will carry this scene's body uncensor (" + bodyGuid + ").");
            }

            private static void OnCardBeingLoaded(ChaFile file)
            {
                if (_pending == null) return;
                if (UnityEngine.Time.realtimeSinceStartup - _armedAt > ArmWindow) { _pending = null; return; }
                if (file == null || file.parameter == null || file.parameter.sex != 1) return;   // female cards only
                string guid = _pending;
                _pending = null;
                try
                {
                    var data = ExtensibleSaveFormat.ExtendedSave.GetExtendedDataById(file, UncensorGuid);
                    if (data == null) data = new ExtensibleSaveFormat.PluginData();
                    if (data.data == null) data.data = new System.Collections.Generic.Dictionary<string, object>();
                    data.version = 2;
                    data.data["BodyGUID"] = guid;   // her own penis/balls/display entries are left alone
                    ExtensibleSaveFormat.ExtendedSave.SetExtendedDataById(file, UncensorGuid, data);
                    LiquidWobbleMPBPlugin._logger?.LogWarning("UncensorInject: '" + file.parameter.fullname + "' will load on this scene's body uncensor (" + guid + ") - written into her card's uncensor data before UncensorSelector reads it.");
                }
                catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: could not write the body uncensor into the incoming card (" + e.Message + ") - she will load on her own body and CloXray will restore the uncensor afterwards instead."); }
            }
        }

        // Reads a character's current body-uncensor GUID (empty = resolving through the default).
        // Reads a character's uncensor GUID. kind: 0 = penis, 1 = balls, 2 = body. Falls back to the
        // RESOLVED data when the card saved no explicit pick, so the answer is the uncensor actually in
        // use rather than null.
        internal static string GetUncensorGuid(Component cha, int kind)
        {
            if (cha == null || kind < 0 || kind > 2) return null;
            try
            {
                const System.Reflection.BindingFlags BI = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                foreach (var c in cha.GetComponents<Component>())
                    if (c != null && c.GetType().Name == "UncensorSelectorController")
                    {
                        var tc = c.GetType();
                        var pg = tc.GetProperty(_uncGuidProp[kind], BI);
                        string g = pg != null ? pg.GetValue(c, null) as string : null;
                        if (!string.IsNullOrEmpty(g)) return g;
                        var pd = tc.GetProperty(_uncDataProp[kind], BI);
                        object dat = pd != null ? pd.GetValue(c, null) : null;
                        var fg = dat != null ? dat.GetType().GetField(_uncGuidProp[kind], BI) : null;
                        return fg != null ? fg.GetValue(dat) as string : null;
                    }
            }
            catch { }
            return null;
        }
        internal static string GetBodyUncensorGuid(Component cha) { return GetUncensorGuid(cha, 2); }

        // Puts the scene's BP penis (and balls) back on a replacement MALE, exactly the way
        // UncensorSelector's own Studio dropdown does it: set the GUID(s), then UpdateUncensor(). This is
        // the male counterpart of the body carry-over - his penis uncensor is what provides the dan bone
        // chain BetterPenetration drives, so without it a swapped-in card cannot be penetrated at all.
        // The BODY slot is never touched here: a female body forced onto a male is what produced the
        // mosaic penis, and that guard stays.
        internal static bool SetPenisUncensorGuid(Component cha, string penisGuid, string ballsGuid)
        {
            if (cha == null || string.IsNullOrEmpty(penisGuid)) return false;
            if (!IsMaleChara(cha))
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: refusing to set a penis uncensor on '" + cha.name + "' - not a male."); return false; }
            try
            {
                const System.Reflection.BindingFlags BI = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                Component ctl = null;
                foreach (var c in cha.GetComponents<Component>())
                    if (c != null && c.GetType().Name == "UncensorSelectorController") { ctl = c; break; }
                if (ctl == null) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector controller not found on '" + cha.name + "' - cannot carry his penis uncensor over."); return false; }
                var tc = ctl.GetType();
                var pPenis = tc.GetProperty(_uncGuidProp[0], BI);
                var pBalls = tc.GetProperty(_uncGuidProp[1], BI);
                var mUpd   = tc.GetMethod("UpdateUncensor", BI);
                if (pPenis == null || mUpd == null)
                { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector is missing " + _uncGuidProp[0] + "/UpdateUncensor - cannot carry his penis uncensor over."); return false; }
                pPenis.SetValue(ctl, penisGuid, null);
                if (pBalls != null && !string.IsNullOrEmpty(ballsGuid)) pBalls.SetValue(ctl, ballsGuid, null);
                UncBodyReloadWatch.Arm(ctl, cha);   // UpdateUncensor raises no event of its own
                mUpd.Invoke(ctl, null);
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: '" + cha.name + "' penis uncensor set to the one this scene was built on (" + penisGuid
                    + (string.IsNullOrEmpty(ballsGuid) ? "" : ", balls " + ballsGuid) + "); his body is reloading.");
                return true;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: penis-uncensor carry-over failed on '" + cha.name + "': " + e.Message); return false; }
        }

        // Applies a specific body uncensor to ONE character and reloads that mesh. Used to carry the
        // uncensor across a Studio character replacement - the swapped-in card keeps the body the scene
        // was built on, which is also what gives it the cf_J_Vagina bones the penis anchors to.
        internal static bool SetBodyUncensorGuid(Component cha, string guid)
        {
            if (cha == null || string.IsNullOrEmpty(guid)) return false;
            if (IsMaleChara(cha))
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: refusing to set a body uncensor on the male '" + cha.name + "' - body uncensors are female-side."); return false; }
            try
            {
                const System.Reflection.BindingFlags BI = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                Component ctl = null;
                foreach (var c in cha.GetComponents<Component>())
                    if (c != null && c.GetType().Name == "UncensorSelectorController") { ctl = c; break; }
                if (ctl == null) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector controller not found on '" + cha.name + "' - cannot carry the body uncensor over."); return false; }
                var tc = ctl.GetType();
                var pg = tc.GetProperty(_uncGuidProp[2], BI);
                var mUpd = tc.GetMethod("UpdateUncensor", BI);
                if (pg == null || mUpd == null)
                { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector is missing " + _uncGuidProp[2] + "/UpdateUncensor - cannot carry the body uncensor over."); return false; }
                if ((pg.GetValue(ctl, null) as string) == guid) return true;
                // Exactly UncensorSelector's own Studio-menu apply (BodyDropdownChangedStudio): set the
                // GUID, call UpdateUncensor(), nothing else. UpdateUncensor starts the plugin's full
                // ReloadCharacterUncensor coroutine; invoking ReloadCharacterBody directly on top of it
                // races that coroutine with a second rebuild.
                UncBodyReloadWatch.Arm(ctl, cha);   // completion event - the post-reload re-apply keys off it
                pg.SetValue(ctl, guid, null);
                mUpd.Invoke(ctl, null);
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: '" + cha.name + "' body uncensor set to the one this scene was built on (" + guid + "); the body is reloading.");
                return true;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: body-uncensor carry-over failed on '" + cha.name + "': " + e.Message); return false; }
        }

        public static void RestoreBPUncensorDefaults()
        {
            try
            {
                bool any = false;
                for (int k = 0; k < 3; k++)
                {
                    if (!_uncCfgForced[k] || _uncEntry[k] == null) continue;
                    _uncEntry[k].GetType().GetProperty("Value").SetValue(_uncEntry[k], _uncSaved[k], null);
                    _uncCfgForced[k] = false; _uncEntry[k] = null; any = true;
                }
                if (any) LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: UncensorSelector defaults restored to the user's original values.");
            }
            catch { }
        }

        private static void ForceBPSimplifyConfig(System.Reflection.Assembly bpAsm)
        {
            if (_bpSimpForced) return;
            try
            {
                const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                System.Reflection.FieldInfo fi = null;
                foreach (var t in bpAsm.GetTypes()) { fi = t.GetField("_simplifyVaginal", BF); if (fi != null) break; }
                var arr = fi != null ? fi.GetValue(null) as System.Array : null;
                if (arr == null) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BP '_simplifyVaginal' config entries not found — cannot force Simplify ON at the source (BP version drift?)."); return; }
                _bpSimpEntries = new object[arr.Length];
                _bpSimpSaved = new bool[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    var entry = arr.GetValue(i);
                    if (entry == null) continue;
                    var pVal = entry.GetType().GetProperty("Value");
                    _bpSimpEntries[i] = entry;
                    _bpSimpSaved[i] = (bool)pVal.GetValue(entry, null);
                    if (!_bpSimpSaved[i]) pVal.SetValue(entry, true, null);
                }
                _bpSimpForced = true;
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP config 'Simplify Penetration Calculation' forced ON at the source (was " + string.Join(",", System.Array.ConvertAll(_bpSimpSaved, b => b.ToString())) + ") — BP's own SettingChanged pipeline re-applies it, so DanOptions rebuilds keep the simplify path. Restored when the womb is removed.");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: forcing BP Simplify config failed: " + e.Message); }
        }

        public static void RestoreBPSimplifyConfig()
        {
            if (!_bpSimpForced || _bpSimpEntries == null) return;
            try
            {
                for (int i = 0; i < _bpSimpEntries.Length; i++)
                {
                    var entry = _bpSimpEntries[i];
                    if (entry == null) continue;
                    entry.GetType().GetProperty("Value").SetValue(entry, _bpSimpSaved[i], null);
                }
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP 'Simplify Penetration Calculation' restored to the user's original values.");
            }
            catch { }
            _bpSimpForced = false; _bpSimpEntries = null;
        }

        public static void AttachPenisAim(WombExpandEffect w, Component male, Component female)
        {
            if (w == null || female == null) return;
            // with no BP uncensor selected it has to be forced on womb spawn.
            // check the BONES directly, at spawn, BEFORE anything depends on BP's state. BP's female side
            // needs the uncensor's vagina chain (`cf_J_Vagina_root` / `_Inner` - same names in KK and KKS,
            // verified in both BP assemblies); a non-BP body has NONE of them, so BP degrades to kokan-only
            // aiming and her BP colliders are absent. The existing force (TryForceBPUncensor(female,2)) was
            // gated deep inside the BP-agent check on `m_bpKokanTarget == null`, which never fired on the
            // a KKS session (probe: cf_J_Vagina_root NOT FOUND, yet no force logged) - so gate it
            // on the bones that are directly visible. Idempotent per character via _uncForced.
            // AUDIT FIX: AttachPenisAim runs on EVERY POSE CHANGE (the event-driven
            // retarget), not just at spawn - so an unconditional hierarchy scan cost a ~600-transform
            // walk + array alloc per pose change (268 of them in a 4-pass research sweep), and re-logged the
            // same error each time whenever the force couldn't satisfy it. Cache the SATISFIED state per
            // character: once she has the bones there is no further scan, and the "still missing" error is logged
            // once per character instead of once per pose change.
            try
            {
                int fid = female.GetInstanceID();
                if (!_vagBonesOk.Contains(fid))
                {
                    bool hasVag = false;
                    foreach (var t721 in female.GetComponentsInChildren<Transform>(true))
                        if (t721 != null && t721.name.StartsWith("cf_J_Vagina", StringComparison.OrdinalIgnoreCase)) { hasVag = true; break; }
                    if (hasVag) _vagBonesOk.Add(fid);   // satisfied - never scan this character again
                    else if (_vagWarned.Add(fid))
                    {
                        LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + female.name + "' has NO cf_J_Vagina* bones — her body uncensor is not BP-compatible (BP can only aim at the game's kokan; no BP vagina colliders, and the womb entrance cannot be verified against the vulva). Forcing a BP body uncensor now (womb spawn = consent).");
                        TryForceBPUncensor(female, 2);
                    }
                }
            }
            catch (Exception e721) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP-uncensor bone check failed: " + e721.Message); }
            // The CANAL-FRAME aim bone (penis_target2) - the only aim source, no legacy path.
            // REBIND FIX: the rebind reparents clo_canal_entry (+PT2 child) under HER cf_j_kokan -
            // OUTSIDE the womb subtree - so a womb-only search came back null and the fallback grabbed
            // the OLD penis_target on the womb's FROZEN skeleton (mirror inert under rebind) = a stale
            // aim at the spawn pose. MEASURED symptom: after a pose change in B the penis missed the
            // canal ("targeter too far to the womb's front") until a re-toggle re-ran this search
            // pre-reparent. Search HER subtree too (PT2/clo_canal_entry are unique names - she has no
            // native bones by these names, so the wider search cannot mis-bind).
            Transform target = FindDeep(w.transform, "penis_target2");
            if (target == null && w.transform.parent != null) target = FindDeep(w.transform.parent, "penis_target2");
            if (target == null)
            {
                // zipmod+DLL ship together - no legacy old-bone handling. Loud error, nothing applied.
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: AIM BONE NOT FOUND — no penis_target2 in the womb or wearer subtree. BP aim NOT steered; the penis will not snap to the canal. Zipmod/DLL mismatch or a search bug — fix the cause (no fallback).");
                return;
            }
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: H aim bone = '" + target.name + "' (parent '" + (target.parent != null ? target.parent.name : "-") + "').");
            Transform entrance = FindDeep(w.transform, "clo_canal_entry");
            if (entrance == null && w.transform.parent != null) entrance = FindDeep(w.transform.parent, "clo_canal_entry");

            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                                                      System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            try
            {
                // Locate the BP static holder via its 'collisionAgents' list.
                System.Reflection.FieldInfo fAgents = null, fDans = null;
                System.Reflection.Assembly bpAsm = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string an = asm.GetName().Name;
                    if (an.IndexOf("BetterPenetration", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (var t in asm.GetTypes())
                    {
                        var fi = t.GetField("collisionAgents", BF);
                        if (fi != null) { fAgents = fi; fDans = t.GetField("danAgents", BF); bpAsm = asm; break; }
                    }
                    if (fAgents != null) break;
                }
                if (bpAsm != null) ForceBPSimplifyConfig(bpAsm);   // fix Simplify at BP's SOURCE (a shipped cfg can have it Disabled)
                if (fAgents == null) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BetterPenetration not found (no 'collisionAgents') — the Free-H womb HARD-DEPENDS on BP. Install + enable BetterPenetration."); return; }
                // Remember the list fields + attach context for the pin's 2s AGENT WATCHDOG:
                // BP re-creates its agent objects on H init/reload and every cached handle goes DEAD.
                _sfAgents = fAgents; _sfDans = fDans; _sWomb = w; _sMale = male; _sFemale = female;
                // (the look-at substitute is the game's own last non-null look-at - no name lookup)

                // Female agent -> pin m_innerTarget to penis_target (component keeps it pinned + restores).
                BPInnerTargetPin pin = null;
                var agents = fAgents.GetValue(null) as System.Collections.IEnumerable;
                int agentCount = 0; if (agents != null) foreach (var a in agents) if (a != null) agentCount++;
                if (agentCount == 0)
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BP is loaded but has NO collision agents (not initialized for this H scene). BP initializes its agents when the H scene starts — if this persists, BP may be disabled or the scene isn't a supported H mode. The womb won't react until BP is active.");
                object agent = null; System.Reflection.FieldInfo fInner = null;
                if (agents != null)
                    foreach (var a in agents)
                    {
                        if (a == null) continue;
                        var fCha = a.GetType().GetField("m_collisionCharacter", BF);
                        var cha = fCha != null ? fCha.GetValue(a) as Component : null;
                        if (cha != null && ReferenceEquals(cha, female)) { agent = a; fInner = a.GetType().GetField("m_innerTarget", BF); break; }
                    }
                if (agent == null || fInner == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: no BP collision agent for '" + female.name + "' (agent=" + (agent != null) + ", m_innerTarget=" + (fInner != null) + ") — penis aim not steered.");
                }
                else
                {
                    // Back axis: the WOMB's mirrored kokan (always the animated pelvis) - position-
                    // independent, unlike the character root which is the scene anchor in H and does
                    // NOT turn with reversed positions (the reverse-cowgirl mis-aim).
                    Transform wombKokan = FindDeep(w.transform, WombBone("cf_j_kokan"));
                    // Press measurement: the male's dan chain natural length + base bone, so the pin
                    // can convert squish (animation pushes, pin holds) into womb displace.
                    Transform danBase = null; float danLen = 0f;
                    if (male != null)
                    {
                        var danBones = new List<Transform>();
                        foreach (var t in male.GetComponentsInChildren<Transform>(true))
                            if (t != null && t.name.StartsWith("cm_J_dan1") && t.name.EndsWith("_00")) danBones.Add(t);
                        danBones.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                        for (int i = 1; i < danBones.Count; i++) danLen += Vector3.Distance(danBones[i - 1].position, danBones[i].position);
                        if (danBones.Count > 0) danBase = danBones[0];
                    }
                    pin = w.gameObject.GetComponent<BPInnerTargetPin>();
                    if (pin == null) pin = w.gameObject.AddComponent<BPInnerTargetPin>();
                    pin.Set(agent, fInner, target, wombKokan != null ? wombKokan : female.transform, entrance, danBase, danLen, w);
                }

                // Male dan options -> simplifyVaginal, so the constraint PINS at the inner limit instead
                // of the body-collider walk (which ignores the inner target entirely).
                if (fDans != null && male != null)
                {
                    var dans = fDans.GetValue(null) as System.Collections.IEnumerable;
                    if (dans != null)
                        foreach (var d in dans)
                        {
                            if (d == null) continue;
                            var td = d.GetType();
                            var fChar = td.GetField("m_danCharacter", BF) ?? td.GetField("m_character", BF);
                            var cha = fChar != null ? fChar.GetValue(d) as Component : null;
                            if (fChar != null && (cha == null || !ReferenceEquals(cha, male))) continue;   // unmatchable field -> fall through to first agent
                            var fOpt = td.GetField("m_danOptions", BF);
                            var opt = fOpt != null ? fOpt.GetValue(d) : null;
                            // Optional per-male tuning copied from a Studio male (no BP UI in Free-H).
                            string ovr = LiquidWobbleMPBPlugin.CfgHBPDanOptions;
                            if (opt != null && !string.IsNullOrEmpty(ovr)) ApplyOptions(opt, ovr);
                            var fSimp = opt != null ? opt.GetType().GetField("simplifyVaginal", BF) : null;
                            if (fSimp != null)
                            {
                                fSimp.SetValue(opt, true);
                                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP simplifyVaginal -> true on '" + male.name + "' (pin-at-womb mode).");
                            }
                            // NATURAL penis length from BP (the bone-sum at apply time measures the
                            // already-squished chain -> press would never go positive -> zero stretch).
                            var fLen = td.GetField("m_baseDanLength", BF);
                            if (fLen != null && pin != null)
                            {
                                float bl = 0f;
                                try { bl = (float)fLen.GetValue(d); } catch { }
                                if (bl > 0.02f) { pin.SetNaturalLength(bl); pin.SetLengthControl(d, fLen); }
                            }
                            else LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP danOptions/simplifyVaginal not found (danOptions=" + (opt != null) + ") — pin may be ignored on the body-collider path.");
                            // Stroke drive: the pin modulates this DanOptions' squishThreshold per frame
                            // so the visual tip actually travels with the thrust (see BPInnerTargetPin).
                            if (opt != null && pin != null) pin.SetStrokeControl(opt);
                            // BP HEALTH: BP.SetDanTarget only DRIVES the penis if m_danPointsFound (the male
                            // has a BP-compatible uncensor) AND the female's m_collisionPointsFound. Hard-
                            // depend on that; verify it and fail LOUD (no fallback) so a non-BP penis is
                            // obvious instead of a silently wrong womb.
                            bool danFound = false, colFound = false, bpFound = false;
                            try { var f = td.GetField("m_danPointsFound", BF); if (f != null) danFound = (bool)f.GetValue(d); } catch { }
                            try { var f = td.GetField("m_bpDanPointsFound", BF); if (f != null) bpFound = (bool)f.GetValue(d); } catch { }
                            try { var f = agent.GetType().GetField("m_collisionPointsFound", BF); if (f != null) colFound = (bool)f.GetValue(agent); } catch { }
                            // m_bpDanPointsFound (>2 dan points = a FULL BP uncensor) is the REAL gate: ConstrainDan
                            // only honours the innerTarget (puts the penis END where it is aimed) on the simplify path,
                            // which requires bpDanPointsFound. If it's false, BP body-drives the end (looks "random")
                            // and the target is IGNORED - the male's penis uncensor is not BP-compatible.
                            if (!danFound || !colFound)
                            {
                                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BP is NOT driving this penis (m_danPointsFound=" + danFound + " m_collisionPointsFound=" + colFound + "). Forcing the BP uncensors (womb hotkey = consent).");
                                TryForceBPUncensor(male, 0); TryForceBPUncensor(male, 1);
                            }
                            else if (!bpFound)
                            {
                                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BP drives the penis but m_bpDanPointsFound=FALSE — the male's uncensor is NOT a full BP uncensor, so BP IGNORES our aim target and body-drives the endpoint. Forcing the BP uncensors (womb hotkey = consent). (danPointsFound=" + danFound + " collisionPointsFound=" + colFound + ")");
                                TryForceBPUncensor(male, 0); TryForceBPUncensor(male, 1);
                            }
                            else
                                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP FULLY driving the penis — danPointsFound + bpDanPointsFound + collisionPointsFound all OK (our aim target is respected).");
                            // FEMALE side: no m_bpKokanTarget = her body uncensor lacks BP's kokan bones
                            // (BP then aims at the game's k_f_kokan_00 and her BP colliders are absent).
                            try
                            {
                                var fk = agent.GetType().GetField("m_bpKokanTarget", BF);
                                if (fk != null && fk.GetValue(agent) as Transform == null)
                                {
                                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: female has NO m_bpKokanTarget — her body uncensor lacks BP's points. Forcing the BP body uncensor (womb hotkey = consent).");
                                    TryForceBPUncensor(female, 2);
                                }
                            }
                            catch { }
                            break;
                        }
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP aim steering failed: " + e.GetType().Name + ": " + e.Message); }
        }

        // ── BP DanOptions harvest/apply ──────────────────────────────────────────────
        // Studio and main-game BP share an identical DanOptions layout (12 primitive fields), so a
        // name=value string round-trips them: DUMP a Studio male's tuned options from the shipped
        // scene, store the string in config, APPLY to the H male (who has no BP UI in Free-H).
        private const System.Reflection.BindingFlags AnyBF = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        public static void DumpBPDanOptions(Component cc)
        {
            try
            {
                object opts = FindDanOptions(cc);
                if (opts == null) { LiquidWobbleMPBPlugin._logger?.LogInfo("BP-DANOPT '" + cc.name + "': no BP DanOptions found."); return; }
                LiquidWobbleMPBPlugin._logger?.LogInfo("BP-DANOPT '" + cc.name + "': " + DumpOptions(opts));
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("BP-DANOPT dump failed on '" + (cc ? cc.name : "?") + "': " + e.Message); }
        }

        private static object FindDanOptions(Component cc)
        {
            foreach (var comp in cc.GetComponents<Component>())
            {
                if (comp == null) continue;
                string tn = comp.GetType().FullName ?? "";
                if (tn.IndexOf("BetterPenetration", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var f in comp.GetType().GetFields(AnyBF))
                {
                    if (f.FieldType.Name == "DanAgent")
                    {
                        var agent = f.GetValue(comp);
                        if (agent == null) continue;
                        var fo = agent.GetType().GetField("m_danOptions", AnyBF);
                        var o = fo != null ? fo.GetValue(agent) : null;
                        if (o != null) return o;
                    }
                    if (f.FieldType.Name == "DanOptions")
                    {
                        var o = f.GetValue(comp);
                        if (o != null) return o;
                    }
                }
            }
            return null;
        }

        public static string DumpOptions(object opts)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in opts.GetType().GetFields(AnyBF))
            {
                if (f.FieldType != typeof(float) && f.FieldType != typeof(bool)) continue;
                if (sb.Length > 0) sb.Append("; ");
                object v = f.GetValue(opts);
                sb.Append(f.Name).Append("=").Append(v is float ? ((float)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture) : v.ToString());
            }
            return sb.ToString();
        }

        public static void ApplyOptions(object opts, string s)
        {
            int applied = 0;
            foreach (var part in s.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string name = part.Substring(0, eq).Trim();
                string val = part.Substring(eq + 1).Trim();
                var f = opts.GetType().GetField(name, AnyBF);
                if (f == null) continue;
                try
                {
                    if (f.FieldType == typeof(float)) { f.SetValue(opts, float.Parse(val, System.Globalization.CultureInfo.InvariantCulture)); applied++; }
                    else if (f.FieldType == typeof(bool)) { f.SetValue(opts, bool.Parse(val)); applied++; }
                }
                catch { }
            }
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP DanOptions override applied (" + applied + " fields).");
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == name) return t;
            return null;
        }

        private static bool IsFemale(Component cc)
        {
            try
            {
                // ChaControl.sex is a field in some game builds and a property in others - accept either.
                var p = cc.GetType().GetProperty("sex");
                if (p != null) return Convert.ToInt32(p.GetValue(cc, null)) == 1;
                var f = cc.GetType().GetField("sex");
                if (f != null) return Convert.ToInt32(f.GetValue(cc)) == 1;
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: ChaControl has neither a 'sex' property nor field — cannot gate by sex.");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: sex check failed: " + e.Message); }
            return false;   // can't tell -> don't claim
        }
    }

    // Keeps the female BP agent's m_innerTarget pinned to the womb's penis_target: re-asserts every
    // 2s (BP re-initializes agents on H position changes) and RESTORES the original transform when
    // the womb is removed (component dies with the womb GameObject).
    internal class BPInnerTargetPin : MonoBehaviour
    {
        private object _agent;
        private System.Reflection.FieldInfo _fInner;
        private Transform _target, _original, _backRef, _proxy, _entrance, _danBase;
        private const string ProxyName = "CloXray_PinProxy";
        private float _danLen;
        private float _follow;   // displace-ride (m): how far the pin follows the yielding dome
        private float _minD = float.MaxValue, _maxD;   // thrust envelope: running base->pin distance extremes
        private float _smMin = float.MaxValue, _smMax; // 2nd envelope on the SMOOTHED push -> full-range stroke
        // One-time pose fit state: 0 = prime pending, 1 = measuring one stroke, 2 = locked.
        private int _fitState;
        private float _measT, _measMin = float.MaxValue, _measMax;
        private Vector3 _deepVec;   // research: base->canal vector in HIS base frame at the deepest sample
        // (the focus-event discard is RETIRED: measured 2026-07-26 - this setup runs in background, the
        // game does NOT pause on alt-tab, so focus changes were discarding VALID windows. Frame progress
        // is the assumption-free test: a genuinely paused/stalled window shows ~0 fps and is discarded;
        // an alt-tabbed-but-running one passes.)
        // how much of _danLen is the 0.65x-natural FLOOR's addition over the anatomical fit.
        // On a SMALL character the floor forces a penis longer than pose+canal can absorb (log: fit
        // wanted ~68mm on a 59mm canal, floor held 115mm) - that surplus is NOT compression the womb
        // should feel; it pinned stretchW at ~100 while the penis was visibly only half-in.
        private float _fitSurplus;
        private int _seenPose = -1;
        private string _measMotion = "";   // the motion state the current measure window belongs to
        private float _dbgNext;  // diagnostics timer
        // Worst tip-vs-dome margin seen this pose, and the state at that instant. Per FRAME, because
        // the 2s PIN-THRUST sampler cannot see a stroke peak.
        // the penis reduces its size after a stroke, so predict it BEFORE as a small correction. The pin
        // is recreated per pairing, so _fitState resets and every pose PRIMEs at natural 178 -> the ~38mm
        // settle jump. But the locked size clusters tightly PER CHARACTER (~140mm here across most poses), so
        // the last fit predicts the next. STATIC caches survive pin recreation: an exact per-pose+canal answer
        // (repeat pose = zero jump), else the last MOTION-locked length (same-char cluster = small correction).
        private static float s_lastFitLen;
        private static readonly System.Collections.Generic.Dictionary<string, float> s_poseFit
            = new System.Collections.Generic.Dictionary<string, float>();
        // the LOOP state joins every prediction key. PROOF (fKok-0.858 girl, 31 poses sampled in
        // BOTH loops): SLoop strokes a median 30.3mm DEEPER than WLoop with 22mm less range - one fit/
        // profile per pose silently averages two incompatible conditions. s_lastLoop remembers the last
        // *Loop* motion state seen (the gauge level persists across pose changes, so it is also the best
        // guess for the NEXT pose's loop at prime time; fresh session defaults to WLoop = entry loop).
        private static string s_lastLoop = "WLoop";
        private static string FitKey(float canalM)
            => MainGameWomb.CurrentAnimKeyV + "|" + s_lastLoop + "@" + Mathf.RoundToInt(canalM * 1000f).ToString()
             + "m" + Mathf.RoundToInt(NaturalDanLen * 200f).ToString();  // + male identity (5mm natural-length buckets) - a male swap must never reuse the previous male's locked fit

        // find the animations and calculate stroke length beforehand instead of measuring each
        // time"). The H clips ARE readable (abdata/h/anim/male/*.unity3d, ~1095 clips, names like M_WLoop2)
        // BUT they are HUMANOID/Mecanim: PositionCurves/RotationCurves are EMPTY and all motion sits in the
        // compressed m_MuscleClip. Getting a thrust amplitude out of that offline means replicating Unity's
        // muscle->bone retargeting, and the result would STILL depend on each character's proportions - the
        // very thing that varies. So it is LEARNED instead: store the measured stroke NORMALIZED by canal
        // length (character-independent) per ANIMATION, PERSIST it, and predict the first encounter on any
        // character. Modded animations are covered automatically - no asset decoding, no manual data run.
        // the animation mainly depends on CHARACTER SIZE, so that is the primary parameter; the shipped
        // txt stays abstract - pose name + char size -> the variables. Normalizing by CANAL
        // length is the wrong basis: canal is a config value (womb scale, base stretch), so that table would silently
        // go stale whenever the womb is retuned and means nothing on someone else's settings. The model is now
        // keyed by POSE and parameterized by HER PELVIS SCALE (a pure character property):
        //     "<pose label>|<charScale>,<deepestBaseToCanal m>,<strokeRange m>"   (one line per size sample)
        // With 2+ samples at different sizes, fit a LINE in char scale (the male's fixed geometry is the
        // intercept, her size the slope); with one, scale proportionally. Canal is applied AFTERWARDS by the
        // fit math, so re-tuning the womb never invalidates the table -> it is shippable as gathered knowledge.
        // whether pelvis scale is the determining factor, or another bone or height, is settled by
        // gathering across many character types and poses and selecting on deploy. Which one it is
        // character measure drives the stroke - plausibly HIS size matters more than hers, since the thrust is
        // his animation. So every sample records ALL candidate predictors next to the measured outcome; the
        // winner is chosen later from real data (offline correlation), and only then does the predictor switch.
        // FKok is merely the CURRENT choice, not a claim.
        private struct AnimSample
        {
            public float FKok;    // HER pelvis (cf_j_kokan) lossyScale.y      <- current predictor
            public float FStat;   // HER stature: leg length from LOCAL bone offsets (pose-independent)
            public float MScale;  // HIS body scale at the dan base
            public float MDan;    // HIS natural dan length
            public float Min;     // MEASURED deepest base->canal distance (m)
            public float Range;   // MEASURED stroke range (m)
        }
        private static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<AnimSample>> s_animProf;
        private static string AnimProfFile()
        {
            return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "Clo.CloXray.animfit.txt");
        }
        internal static float HerScale()
        {
            Transform k = MainGameWomb.RebindPivotBone;
            return (k != null && k.lossyScale.y > 1e-4f) ? k.lossyScale.y : MainGameWomb.KokStd;
        }
        // fHipH is world units, so measure locally. Correct: a WORLD pelvis height swings
        // with her whole stance (kneeling vs standing vs lying), so it encoded the POSE, not the character -
        // useless, even harmful, as a stature feature for the predictor study. Measure LOCALLY instead: each
        // leg bone's localPosition IS its authored bone length scaled by her body sliders, and joint ROTATION
        // never changes it, so summing the chain gives a pose-INDEPENDENT leg length. Converted to world units
        // via lossyScale (also pose-independent). Cheap: runs once per pose fit, not per frame.
        // Hypothesis: SOME bones may scale with pose rather than the whole char.
        // Very plausible - KK H scenes adjust specific joints for position matching. lossyScale is ACCUMULATED,
        // so a scaled PARENT moves every descendant: if only the pelvis region is touched, a bone OUTSIDE it
        // stays a true character constant and rescues the size model. Probe several levels to find out WHICH.
        private static float BoneScaleY(string exactName)
        {
            Transform k = MainGameWomb.RebindPivotBone;
            if (k == null || k.root == null) return 0f;
            foreach (var t in k.root.GetComponentsInChildren<Transform>(true))
                if (t.name == exactName) return t.lossyScale.y;
            return 0f;
        }

        internal static float HerStature()
        {
            Transform k = MainGameWomb.RebindPivotBone;
            if (k == null || k.root == null) return 0f;
            Transform thigh = null;
            foreach (var t in k.root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("cf_j_thigh00_L")) { thigh = t; break; }
            if (thigh == null) return 0f;
            float len = 0f; Transform cur = thigh;
            for (int step = 0; step < 4 && cur != null; step++)
            {
                Transform next = null;
                for (int c = 0; c < cur.childCount; c++)
                {
                    Transform ch = cur.GetChild(c);
                    if (ch.name.StartsWith("cf_j_leg") || ch.name.StartsWith("cf_j_foot")) { next = ch; break; }
                }
                if (next == null) break;
                len += next.localPosition.magnitude * cur.lossyScale.y;
                cur = next;
            }
            return len;
        }
        // SHIPPED read-only baseline, next to the DLL in plugins/Clo. Clean settled-sampler medians
        // (3 characters) that seed the predictor so a FRESH install predicts common poses on first encounter.
        private static string AnimBaselineFile()
        {
            try { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(MainGameWomb).Assembly.Location), "Clo.CloXray.animfit.baseline.txt"); }
            catch { return null; }
        }
        // Parse a profile file into s_animProf. replaceExisting=true (the USER's live-learned file) OVERWRITES a
        // baseline sample at the same size bucket - a live character measurement always wins; the
        // baseline only fills gaps. replaceExisting=false (the baseline) never overrides what's already there.
        private static int MergeProfFile(string path, bool replaceExisting)
        {
            if (path == null || !System.IO.File.Exists(path)) return 0;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            int n = 0;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                int i = line.LastIndexOf('|');
                if (i <= 0) continue;
                var parts = line.Substring(i + 1).Split(',');
                var smp = new AnimSample();
                if (parts.Length == 6)
                {   // full candidate row
                    if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out smp.FKok)) continue;
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out smp.FStat);
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out smp.MScale);
                    float.TryParse(parts[3], System.Globalization.NumberStyles.Float, inv, out smp.MDan);
                    if (!float.TryParse(parts[4], System.Globalization.NumberStyles.Float, inv, out smp.Min)) continue;
                    if (!float.TryParse(parts[5], System.Globalization.NumberStyles.Float, inv, out smp.Range)) continue;
                }
                else if (parts.Length == 3)
                {   // legacy row (scale,min,range)
                    if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out smp.FKok)) continue;
                    if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out smp.Min)) continue;
                    if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out smp.Range)) continue;
                }
                else continue;
                string pose = line.Substring(0, i);
                // only loop-keyed lines are valid - a motion-less line averages WLoop and SLoop,
                // which stroke ~30mm apart. Old-format lines are dropped here and disappear from the
                // live file on its next save; baseline v3 ships loop-keyed.
                int lp = pose.LastIndexOf('|');
                if (lp < 0 || pose.IndexOf("Loop", lp, StringComparison.Ordinal) < 0) continue;
                System.Collections.Generic.List<AnimSample> lst;
                if (!s_animProf.TryGetValue(pose, out lst)) { lst = new System.Collections.Generic.List<AnimSample>(); s_animProf[pose] = lst; }
                int at = -1;
                // the dedup bucket is JOINT (her fKok 0.02 x his mScale 0.05) - with male-varied
                // baselines, two lines at the same her-size but different males are BOTH real samples;
                // the old her-size-only bucket silently dropped the second male at load.
                for (int k = 0; k < lst.Count; k++)
                    if (Mathf.Abs(lst[k].FKok - smp.FKok) < 0.02f && Mathf.Abs(lst[k].MScale - smp.MScale) < 0.05f) { at = k; break; }
                if (at >= 0) { if (replaceExisting) lst[at] = smp; }   // same size bucket: live wins, baseline defers
                else lst.Add(smp);
                n++;
            }
            return n;
        }
        private static void LoadAnimProf()
        {
            if (s_animProf != null) return;
            s_animProf = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<AnimSample>>();
            try
            {
                int b = MergeProfFile(AnimBaselineFile(), false);   // shipped seed first (never overrides the live file)
                int u = MergeProfFile(AnimProfFile(), true);        // live-learned samples win at their own sizes
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: anim profiles loaded — " + b + " baseline + " + u + " user samples over " + s_animProf.Count + " animations.");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: anim-profile load failed: " + e.Message); }
        }
        private static void SaveAnimProf()
        {
            if (s_animProf == null) return;
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder();
                sb.Append("# CloXray learned H-stroke profiles. One line per (animation, character) sample.\n");
                sb.Append("# <pose label>|fKok,fHipH,mScale,mDan,MIN,RANGE\n");
                sb.Append("#   fKok   = HER pelvis (cf_j_kokan) lossyScale.y   [current predictor]\n");
                sb.Append("#   fHipH  = HER pelvis height above her char root  (leg-length / stature proxy)\n");
                sb.Append("#   mScale = HIS body scale at the dan base\n");
                sb.Append("#   mDan   = HIS natural dan length (m)\n");
                sb.Append("#   MIN    = MEASURED deepest base->canal distance (m)   <- what we predict\n");
                sb.Append("#   RANGE  = MEASURED stroke range (m)                    <- what we predict\n");
                sb.Append("# Candidates are recorded so the BEST predictor can be chosen from data later.\n");
                sb.Append("# Character-parameterised and womb-config independent: safe to ship/share.\n");
                foreach (var kv in s_animProf)
                    for (int i = 0; i < kv.Value.Count; i++)
                        sb.Append(kv.Key).Append('|')
                          .Append(kv.Value[i].FKok.ToString("F4", inv)).Append(',')
                          .Append(kv.Value[i].FStat.ToString("F4", inv)).Append(',')
                          .Append(kv.Value[i].MScale.ToString("F4", inv)).Append(',')
                          .Append(kv.Value[i].MDan.ToString("F4", inv)).Append(',')
                          .Append(kv.Value[i].Min.ToString("F5", inv)).Append(',')
                          .Append(kv.Value[i].Range.ToString("F5", inv)).Append('\n');
                System.IO.File.WriteAllText(AnimProfFile(), sb.ToString());
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: anim-profile save failed: " + e.Message); }
        }

        // pose + her scale -> predicted (deepest base->canal, stroke range). Line-fit across sizes where
        // have 2+ samples (male geometry = intercept, her size = slope); proportional from a single sample.
        // MALE-AWARE PREDICTION, to perfect it for release. The paired campaign
        // proved the male term is +50mm-class and POSE-DEPENDENT (small male IQR +17..+89mm vs ref on
        // the same girl+pose+loop), so the first-order proportional correction is retired. Model:
        // samples group into MALE BUCKETS (mScale, 0.05 wide). Inside a bucket the her-size
        // piecewise-linear curve applies unchanged; when two buckets BRACKET his size, the curve runs
        // in each and the results lerp by mScale (bilinear over the grid actually sampled); outside
        // the sampled male range it CLAMPS to the nearest bucket - the same saturation rule as the
        // her-size axis (extrapolating the male slope would invent geometry, cf. alignment-optimum).
        private static bool PredictInBucket(System.Collections.Generic.List<AnimSample> lst, float scale, float hisScale, out float minOut, out float rangeOut)
        {
            minOut = 0f; rangeOut = 0f;
            if (lst.Count == 0) return false;
            if (lst.Count == 1)
            {
                // MIN and RANGE have DIFFERENT drivers. RANGE is HIS thrust animation -> scale by
                // HIS size, never hers; MIN is the his-base->her-canal geometry -> scale by hers. Within
                // a 0.05 bucket the male ratio is a fine correction (<~5%).
                AnimSample s0 = lst[0];
                float fHer = s0.FKok > 1e-4f ? scale / s0.FKok : 1f;
                float fHim = (s0.MScale > 1e-4f && hisScale > 1e-4f) ? hisScale / s0.MScale : 1f;
                minOut = s0.Min * fHer;
                rangeOut = s0.Range * fHim;
                return true;
            }
            // Alignment-optimum: piecewise-linear across ALL samples sorted by her size; CLAMP at
            // the endpoints (the effect saturates; a two-point line through a curved response is wrong).
            lst.Sort(delegate (AnimSample x, AnimSample y) { return x.FKok.CompareTo(y.FKok); });
            AnimSample lo0 = lst[0], hi0 = lst[lst.Count - 1];
            if (scale <= lo0.FKok) { minOut = lo0.Min; rangeOut = lo0.Range; return true; }
            if (scale >= hi0.FKok) { minOut = hi0.Min; rangeOut = hi0.Range; return true; }
            for (int i = 0; i < lst.Count - 1; i++)
            {
                AnimSample lo = lst[i], hi = lst[i + 1];
                if (scale < lo.FKok || scale > hi.FKok) continue;
                float ds = hi.FKok - lo.FKok;
                float tt = ds > 1e-4f ? (scale - lo.FKok) / ds : 0f;
                minOut = lo.Min + tt * (hi.Min - lo.Min);
                rangeOut = lo.Range + tt * (hi.Range - lo.Range);
                return true;
            }
            minOut = lo0.Min; rangeOut = lo0.Range; return true;   // (unreachable safety)
        }
        private static bool PredictStroke(string pose, float scale, float hisScale, out float minOut, out float rangeOut)
        {
            minOut = 0f; rangeOut = 0f;
            LoadAnimProf();
            System.Collections.Generic.List<AnimSample> all;
            if (pose == null || !s_animProf.TryGetValue(pose, out all) || all.Count == 0) return false;
            if (hisScale <= 1e-4f)   // male unknown (never expected in play): the whole set as one bucket
                return PredictInBucket(new System.Collections.Generic.List<AnimSample>(all), scale, hisScale, out minOut, out rangeOut);
            // group into male buckets keyed by round(mScale/0.05); track each bucket's mean mScale
            var buckets = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<AnimSample>>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].MScale <= 1e-4f) continue;
                int bk = Mathf.RoundToInt(all[i].MScale / 0.05f);
                System.Collections.Generic.List<AnimSample> bl;
                if (!buckets.TryGetValue(bk, out bl)) { bl = new System.Collections.Generic.List<AnimSample>(); buckets[bk] = bl; }
                bl.Add(all[i]);
            }
            if (buckets.Count == 0) return false;
            // nearest bucket below and above HIS size (by bucket mean mScale)
            System.Collections.Generic.List<AnimSample> loB = null, hiB = null;
            float loC = float.MinValue, hiC = float.MaxValue;
            foreach (var kv in buckets)
            {
                float c = 0f; for (int i = 0; i < kv.Value.Count; i++) c += kv.Value[i].MScale;
                c /= kv.Value.Count;
                if (c <= hisScale && c > loC) { loC = c; loB = kv.Value; }
                if (c >= hisScale && c < hiC) { hiC = c; hiB = kv.Value; }
            }
            if (loB == null && hiB == null) return false;
            if (loB == null || ReferenceEquals(loB, hiB))   // below the range, or exact bucket hit
                return PredictInBucket(hiB, scale, hisScale, out minOut, out rangeOut);
            if (hiB == null)                                 // above the range -> clamp to the biggest male
                return PredictInBucket(loB, scale, hisScale, out minOut, out rangeOut);
            float mLo, rLo, mHi, rHi;
            if (!PredictInBucket(loB, scale, hisScale, out mLo, out rLo)) return false;
            if (!PredictInBucket(hiB, scale, hisScale, out mHi, out rHi)) return false;
            float tm = (hiC - loC) > 1e-4f ? Mathf.Clamp01((hisScale - loC) / (hiC - loC)) : 0f;
            minOut = mLo + tm * (mHi - mLo);
            rangeOut = rLo + tm * (rHi - rLo);
            return true;
        }
        // SINGLE SOURCE OF TRUTH: the ONE definition of "what penis length does this stroke imply" -
        // deep-end poke + shallow-end keep-inside. The LOCK calls this too, so the predictor can no longer
        // silently drift from the real fit when the poke math is retuned. It nearly did once (poke onset
        // 30->40mm): the two copies were kept in step only by a "keep in sync" comment, which is not a
        // mechanism. Returns the UNCLAMPED length so the lock can still derive _fitSurplus from it.
        private static float FitFromStrokeRaw(float pMin, float pRange, float canal)
        {
            float keepOver = Mathf.Clamp01((pRange - 0.040f) / 0.040f);
            float deepL = pMin + canal + Mathf.Lerp(-0.04f * canal, 0.025f, keepOver);
            float keepIn = (pMin + pRange) + 0.20f * canal;
            return Mathf.Max(deepL, keepIn);
        }
        private static float FitFromStroke(float pMin, float pRange, float canal, float natural, float lcap)
        {
            return Mathf.Clamp(FitFromStrokeRaw(pMin, pRange, canal), natural * 0.65f, natural * lcap);
        }

        public void SetNaturalLength(float len)
        {
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: pin natural dan length " + (_danLen * 1000f).ToString("F0") + "mm (bone-sum) -> " + (len * 1000f).ToString("F0") + "mm (BP m_baseDanLength).");
            _danLen = len;
        }

        // STROKE CONTROL: BP lays the dan chain out at FIXED length, so the tip lands at
        // (squished length − base→kokan). A constant squish flattens the animation's stroke to a few
        // mm - the tip sat visually still. The pin therefore drives DanOptions.squishThreshold PER
        // FRAME from the smoothed thrust: threshold = tip stop depth / natural length, swept between
        // the F1 shallow/deep stroke points - the tip travels the full configured range every stroke.
        private object _danOpts;
        private System.Reflection.FieldInfo _fSquishThr, _fLenSquish;
        private float _lastThr;   // last stroke-drive squishThreshold written this frame (guard re-asserts it)
        private bool _lineMissingLogged;   // one-shot: penis line unreadable while the game says penetrated
        // The unreadable-line error used to fire the instant HFlag said PENETRATED, but BP sets
        // m_referenceTarget on its OWN first vaginal SetDanTarget, which lands a frame or two later. So the
        // error was announcing a race that is always won: measured twice in one session with no visible fault,
        // and the 2s watchdog never fired because the flag had already cleared. That is worse than noise -
        // it trains the reader to ignore the one message that would announce the real fault, where the target never
        // arrives at all (BP agents recreated, or a male with no BP uncensor) and the womb genuinely gets
        // no depth feed. So: latch when it goes missing, and only escalate if it is STILL missing after a
        // grace period. Transient stays silent; persistent reports how long, and what changed.
        private float _refMissingSince = -1f;      // unscaled time the line first went unreadable, -1 = readable
        private object _refMissingAgent;           // the DanAgent instance at that moment (identity change = BP rebuilt it)
        // 0.5 -> 1.2s on evidence. A KK Free-H run produced one dropout that recovered on its own
        // after 0.6s, so 0.5s still let a benign transient print an error asserting "this is the real
        // fault ... suspect the male's BP uncensor" for a male whose penis was BP SoS. Still well inside
        // the 2s watchdog, so a genuinely dead line is announced before anything else reacts.
        private const float RefMissingGraceSec = 1.2f;
        private string _mdState; private float _mdMin = float.MaxValue, _mdMax, _mdT0;   // MOTION-DEPTH research window (debug)
        private bool _lockedNoMotion; private int _noMotionReopens; private string _loopCheckedState;   // no-motion re-open
        private string _lockLoop;   // loop the current lock was measured in (null = no lock yet)
        private float _pushSm;   // smoothed thrust (the raw envelope is near-binary on short strokes)
        // AUTO-LENGTH: fit m_baseDanLength ONCE per animation (slow drift, constant during
        // play - per-stroke size change was rejected as unnatural). The DanAgent + field to write:
        private object _danAgent;
        private System.Reflection.FieldInfo _fBaseLen;
        private float _danLen0;        // BP's ORIGINAL natural length - restored on remove
        // fitted/natural, <1 = the fit length-squished the penis, whose visual shaft then runs
        // FATTER than the tip-collider latch (BP danGirthSquish volume conservation); >1 = stretched,
        // shaft runs THINNER (proven both ways: big male 209 squished to ~180 read too tight, small
        // male 117 stretched to the 146 cap read too wide). Read by the canal-width solver.
        public static float FitSquishRatio = 1f;
        // the male IDENTITY for the girth latches. The latch reset keyed on names ("another male
        // appears") never fired on a card swap - dan names collide across cards, and the collider latch
        // key is a constant string - so the small male inherited the big male's 44.2mm latch (measured:
        // bpLatch 44.2 vs live tip 24.8). Natural length is per-male stable and always distinct.
        public static float NaturalDanLen;
        private float _lenSquishVal = 0.8f;   // the danLengthSquish string value (for the tip estimate)

        // BP's own penis-line inputs (read from the DanAgent each frame): m_referenceTarget = the VAGINA
        // bone BP aims the penis at; m_danPoints.GetDanStartPosition() = the penis base. The H penis is a
        // STRAIGHT line base→referenceTarget (verified in BP source AdjustDanToTargetKokan) - this is THE
        // canal axis, read from BP directly instead of guessed.
        private System.Reflection.FieldInfo _fRefTarget, _fDanPoints, _fDanEnd;
        private System.Reflection.MethodInfo _miGetDanStart;
        private object _danPointsObj;

        private System.Reflection.FieldInfo _fOptOnAgent;   // DanAgent.m_danOptions - re-read each tick (BP can swap/rewrite it)

        public void SetLengthControl(object danAgent, System.Reflection.FieldInfo fBaseLen)
        {
            _danAgent = danAgent; _fBaseLen = fBaseLen;
            _fOptOnAgent = danAgent.GetType().GetField("m_danOptions",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (_danLen0 <= 0f) _danLen0 = _danLen;
            if (_fLenSquish != null && _danOpts != null)
                try { _lenSquishVal = Mathf.Clamp01((float)_fLenSquish.GetValue(_danOpts)); } catch { }
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var t = danAgent.GetType();
            _fRefTarget = t.GetField("m_referenceTarget", F);
            _fDanPoints = t.GetField("m_danPoints", F);
            _danPointsObj = _fDanPoints != null ? _fDanPoints.GetValue(danAgent) : null;
            _miGetDanStart = _danPointsObj != null ? _danPointsObj.GetType().GetMethod("GetDanStartPosition", F) : null;
            _fDanEnd = _danPointsObj != null ? _danPointsObj.GetType().GetField("danEnd", F) : null;   // the REAL penis endpoint transform
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP penis-line taps — refTarget=" + (_fRefTarget != null) + " danStart=" + (_miGetDanStart != null) + " danEnd=" + (_fDanEnd != null) + ".");
        }

        // BP's live penis line: base (danStart), reference-target (vagina), and the REAL end (danEnd,
        // where BP actually puts the tip after ConstrainDan/bend). Read via reflection each frame.
        public bool TryReadPenisLine(out Vector3 baseW, out Vector3 refW, out Vector3 endW, out bool haveEnd)
        {
            baseW = refW = endW = Vector3.zero; haveEnd = false;
            try
            {
                var rt = _fRefTarget != null ? _fRefTarget.GetValue(_danAgent) as Transform : null;
                MainGameWomb.BpRefTargetMissing = (rt == null);   // the signal the real failure showed: refTarget='-'
                if (rt == null) return false;
                refW = rt.position;
                if (_miGetDanStart != null && _danPointsObj != null) baseW = (Vector3)_miGetDanStart.Invoke(_danPointsObj, null);
                else if (_danBase != null) baseW = _danBase.position;
                else return false;
                var de = _fDanEnd != null && _danPointsObj != null ? _fDanEnd.GetValue(_danPointsObj) as Transform : null;
                if (de != null) { endW = de.position; haveEnd = true; }
                return true;
            }
            catch { return false; }
        }

        public void SetStrokeControl(object danOptions)
        {
            _danOpts = danOptions;
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            _fSquishThr = danOptions != null ? danOptions.GetType().GetField("squishThreshold", F) : null;
            _fLenSquish = danOptions != null ? danOptions.GetType().GetField("danLengthSquish", F) : null;
            if (_fLenSquish != null)
                try { _lenSquishVal = Mathf.Clamp01((float)_fLenSquish.GetValue(danOptions)); } catch { }
            if (_fSquishThr == null)
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: DanOptions.squishThreshold not found — stroke drive disabled (tip depth stays static).");
            else
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: stroke drive armed — squishThreshold swept " + LiquidWobbleMPBPlugin.CfgHStrokeShallow.ToString("F0") + ".." + LiquidWobbleMPBPlugin.CfgHStrokeDeep.ToString("F0") + "mm with the thrust.");
        }
        private WombExpandEffect _womb;
        private float _next;
        private int _watchPose = -1;   // pose version at the last watchdog run (event-driven re-bind)
        // BP's kokan push/pull writes the girl's cf_j_kokan* bone positions along the penis direction.
        // The bone mirror copies those bones (the kokan is the womb's PIVOT), so the whole womb rides
        // the pull - and in reverse cowgirl the pull direction flips sign and the womb visibly JUMPS
        // off the body. Disabled while the womb is attached (the womb's own mound-lean blendshapes
        // carry the vaginal reaction); BP smoothly returns the bones (ReturnKokanBones) and they are restored
        // the flag on toggle-off.
        private object _colOpts;
        private System.Reflection.FieldInfo _fKokanPush;
        private bool _hadKokanPush;

        public void Set(object agent, System.Reflection.FieldInfo fInner, Transform target, Transform backRef, Transform entrance,
                        Transform danBase, float danLen, WombExpandEffect womb)
        {
            _agent = agent; _fInner = fInner; _target = target; _backRef = backRef; _entrance = entrance;
            _danBase = danBase; _danLen = danLen; _womb = womb;
            if (_proxy == null)
            {
                // The actual pin point: penis_target shifted along the female's backward direction by the
                // live config offset - a child of the womb so it dies with it.
                var go = new GameObject(ProxyName);
                _proxy = go.transform;
                _proxy.SetParent(transform, false);
            }
            UpdateProxy();
            // NEVER capture the CloXray proxy as "the original": a re-attach (agent watchdog, respawn)
            // finds that proxy already installed, and saving it means the restore writes a DESTROYED
            // transform back into BP -> Transform.get_position throws in AdjustDanToTargetKokan every
            // LateUpdate for the rest of the session (reported: 7325 exceptions).
            var cur = _fInner.GetValue(_agent) as Transform;
            if (cur == null || cur.name != ProxyName) _original = cur;
            _fInner.SetValue(_agent, _proxy);
            // BP's endpoint is only OURS from here. Before this, on every pose change, BP has rebuilt its
            // agent and reset m_innerTarget to its own default (cf_j_waist01) - so danEnd is aimed at her
            // WAIST, and projecting it onto the canal produced the nonsense readings behind a long wrong chase
            // a mistarget that was never visible. Mark the pose this pin belongs to; the depth feed
            // refuses to measure until they match.
            MainGameWomb.AimedForPose = MainGameWomb.PoseVersion;
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP m_innerTarget pinned: '" + (_original != null ? _original.name : "null") + "' -> penis_target + back-offset (penis end constrained at the womb).");
            try
            {
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var fOpts = _agent.GetType().GetField("m_collisionOptions", F);
                _colOpts = fOpts != null ? fOpts.GetValue(_agent) : null;
                _fKokanPush = _colOpts != null ? _colOpts.GetType().GetField("enableKokanPush", F) : null;
                if (_fKokanPush != null)
                {
                    _hadKokanPush = (bool)_fKokanPush.GetValue(_colOpts);
                    _fKokanPush.SetValue(_colOpts, false);
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP kokan push/pull disabled while the womb is attached (was " + _hadKokanPush + ") — the mirrored kokan is the womb pivot, BP's pull made it jump.");
                }
                // Diagnostic: BP aims the penis at ref + kokanOffset x up (NOT the bare bone) - a
                // nonzero KKS default here would put a CONSTANT gap between the drawn base->ref line /
                // the ref-anchored entrance and the actual shaft (parallel lines 3cm apart).
                if (_colOpts != null)
                {
                    try
                    {
                        var fko = _colOpts.GetType().GetField("kokanOffset", F);
                        var fio = _colOpts.GetType().GetField("innerKokanOffset", F);
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP offsets — kokanOffset=" + (fko != null ? ((float)fko.GetValue(_colOpts) * 1000f).ToString("F0") + "mm" : "?")
                            + " innerKokanOffset=" + (fio != null ? ((float)fio.GetValue(_colOpts) * 1000f).ToString("F0") + "mm" : "?") + ".");
                    }
                    catch { }
                }
                else
                {
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP CollisionOptions.enableKokanPush not found — kokan pull stays active (womb may jump with the pull in reverse poses).");
                }
            }
            catch (System.Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: kokan-push disable failed: " + e.Message); }
        }

        private void UpdateProxy()
        {
            if (_proxy == null || _target == null) return;
            // Back = the PELVIS frame's backward (the womb's mirrored kokan) - turns with the pose,
            // so cowgirl and reverse-cowgirl both aim anatomically (the character root does not turn).
            Vector3 back = _backRef != null ? -_backRef.forward : Vector3.zero;
            // Deeper along the canal axis (entrance -> aim bone): a positive depth offset makes the
            // pinned penis press INTO the dome, feeding the stretch/displace reaction.
            Vector3 axis = Vector3.zero;
            if (_entrance != null)
            {
                Vector3 d = _target.position - _entrance.position;
                if (d.sqrMagnitude > 1e-8f) axis = d.normalized;
            }
            // FOLLOW = ride the womb's displace weight, scaled by the follow-% config: 100 = the tip
            // is glued to the stretching dome (pure stretch look), lower = the dome stretches AWAY
            // from the tip while BP squishes the shaft (the stretch+squish combination look).
            const float MaxFollow = 0.055f;   // womb_displace travel at weight 100 (mesh audit)
            _follow = _womb != null
                ? Mathf.Clamp01(_womb.CurrentStretchWeight * 0.01f) * MaxFollow * Mathf.Clamp01(LiquidWobbleMPBPlugin.CfgHFollowPct * 0.01f)
                : 0f;
            // rawPin = the pin point WITHOUT the follow - the measurement reference. Measuring to the
            // followed pin would feed the displace back into the thrust signal.
            // AUTO-LENGTH mode: the pin is a FIXED aim point deep on the canal axis (anatomically
            // correct direction in every pose); the tip depth comes from the fitted natural length +
            // the animation's own motion - the penis NEVER changes size during play.
            bool autoLen = LiquidWobbleMPBPlugin.CfgHAutoLength && _womb != null && _womb.HasCanal && _womb.CanalLenWorld > 1e-4f;
            // END TARGET = the womb's authored `penis_target` BONE, pure (HARD RULE - the literal
            // Studio pairing dan_end -> penis_target; if the bone is misplaced the USER re-authors the
            // bone, never a computed substitute). The axis-point pin is reverted: the
            // front-wall session was caused by DEAD agent handles (BP fell back to its default
            // cf_j_waist01), NOT proven to be this bone. PT-vs-canal divergence measured 12-95mm across
            // poses but is UNVERIFIED by screenshot - the BLUE PT marker + ORANGE entrance->PT debug
            // line exist exactly for that verification (hard rule: a measurement claiming something is
            // wrong must be confirmed visually before conclusions).
            Vector3 rawPin = autoLen
                ? _target.position
                : _target.position
                  + back * (LiquidWobbleMPBPlugin.CfgHPinOffset * 0.001f)
                  + axis * (LiquidWobbleMPBPlugin.CfgHPinDepth * 0.001f);
            _proxy.position = autoLen ? rawPin : rawPin + axis * _follow;
            // THRUST ENVELOPE (the stretch driver). Diagnostics proved the Studio-style
            // absolute signals cannot work in H: natural dan length (~178mm) never reaches the pin
            // (base->pin 276-313mm) - BP just STRETCHES the visual penis to the constrained end, so
            // "pushed past natural length" never happens and any canal-normalized depth saturates >1.6
            // (stretch pegged at 100 = zero visible motion). The one honest dynamic signal is the
            // ANIMATION itself: base->pin distance oscillates with the stroke. Track its running
            // min/max (expand instantly, forget at RangeDecay so it re-calibrates on position change)
            // and map full-push -> 1, full-pull -> 0. Feeds ExternalPress -> weight via the gain cfg.
            // Feed the womb BP's EXACT penis line (base + reference vagina). BP drives the penis STRAIGHT
            // base->referenceTarget every frame (source-verified), so the canal axis = normalize(ref-base)
            // is BP's actual penis direction - not a guess. Fallback to _danBase if the taps aren't ready.
            if (_womb != null)
            {
                Vector3 bW, rW, eW; bool hasEnd;
                if (TryReadPenisLine(out bW, out rW, out eW, out hasEnd))
                {
                    _womb.ExternalPenisBase = bW; _womb.ExternalPenisRef = rW; _womb.HasPenisRef = true; _womb.HasPenisBase = true;
                    _womb.ExternalPenisEnd = eW; _womb.HasPenisEnd = hasEnd;
                    if (_lineMissingLogged)
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP penis line RECOVERED after "
                            + (Time.unscaledTime - _refMissingSince).ToString("F1") + "s — depth feed is live again"
                            + (!ReferenceEquals(_refMissingAgent, _danAgent) ? " (BP had replaced its DanAgent: that was the cause)" : " (same DanAgent throughout)") + ".");
                    _lineMissingLogged = false;
                    _refMissingSince = -1f; _refMissingAgent = null;
                }
                else
                {
                    if (_danBase != null) { _womb.ExternalPenisBase = _danBase.position; _womb.HasPenisBase = true; }
                    _womb.HasPenisRef = false; _womb.HasPenisEnd = false;
                    // Unreadable is NORMAL before the first insertion, and ALSO for the first frames after
                    // the game flips to PENETRATED - BP sets m_referenceTarget on its own first vaginal
                    // SetDanTarget, slightly after HFlag changes. Only a line that stays unreadable past
                    // the grace period is the real fault (dead handle / non-BP penis / BP rebuilt its
                    // agents), and only that one gets to be an error. Feed nothing either way.
                    if (MainGameWomb.HMotionKnown && MainGameWomb.HPenetrated)
                    {
                        if (_refMissingSince < 0f) { _refMissingSince = Time.unscaledTime; _refMissingAgent = _danAgent; }
                        float miss = Time.unscaledTime - _refMissingSince;
                        if (miss >= RefMissingGraceSec && !_lineMissingLogged)
                        {
                            _lineMissingLogged = true;
                            bool agentSwapped = !ReferenceEquals(_refMissingAgent, _danAgent);
                            LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BP penis line UNREADABLE (m_referenceTarget null) for "
                                + miss.ToString("F1") + "s while the game says PENETRATED — this is PAST the transition race, so it is the real fault: "
                                + "the womb gets NO depth feed (no fallback) and the penis is not aimed at the canal. "
                                + "DanAgent " + (agentSwapped ? "WAS REPLACED since the line dropped -> BP rebuilt its agents; the 2s watchdog should re-bind."
                                                             : "is the SAME instance -> BP did not rebuild, so suspect the male's BP uncensor (a non-BP penis never gets a reference target).")
                                + " danAgent=" + (_danAgent != null ? _danAgent.GetType().Name : "<null>")
                                + " danBase=" + (_danBase != null ? _danBase.name : "<null>") + ".");
                        }
                    }
                    else { _refMissingSince = -1f; _refMissingAgent = null; }
                }
            }

            if (_womb != null && _danBase != null)
            {
                // AUTO-LENGTH measures the FIRST LEG of BP's layout path directly: base -> canal
                // ENTRANCE. The old straight base->pin distance under-counted the first leg whenever
                // the approach is bent (reverse cowgirl): the estimate claimed cervix depth while the
                // real tip starved at the entrance, and the fit wrongly SHORTENED the penis. Penetration
                // = L − thisLeg exactly (BP lays base->kokan straight, then bends into the canal).
                float d2 = autoLen ? Vector3.Distance(_danBase.position, _womb.CanalEntranceWorld)
                                   : Vector3.Distance(_danBase.position, rawPin);
                // MOTION-DEPTH research windows (DebugLog only): the run showed most
                // "alternative" presses are WLoop<->SLoop MOTION-STATE switches inside the position
                // (only Cowgirl's alt was a real anim swap) - those never re-fit, so their depth was
                // invisible. Log the raw d2 window per motion state on every state exit: WLoop vs
                // SLoop rows per position answer "are alternates deeper by design" across the board.
                if (LiquidWobbleMPBPlugin.CfgDebugLog)
                {
                    if (_mdState != MainGameWomb.HMotion)
                    {
                        if (!string.IsNullOrEmpty(_mdState) && _mdMax >= _mdMin && _mdMin != float.MaxValue)
                            LiquidWobbleMPBPlugin._logger?.LogInfo("  MOTION-DEPTH '" + _mdState + "' [" + MainGameWomb.HAnimLabel + "]: d2 [" + (_mdMin * 1000f).ToString("F0") + ".." + (_mdMax * 1000f).ToString("F0") + "]mm over " + (Time.unscaledTime - _mdT0).ToString("F1") + "s (smaller min = deeper reach).");
                        _mdState = MainGameWomb.HMotion; _mdMin = float.MaxValue; _mdMax = 0f; _mdT0 = Time.unscaledTime;
                    }
                    if (MainGameWomb.HPenetrated) { _mdMin = Mathf.Min(_mdMin, d2); _mdMax = Mathf.Max(_mdMax, d2); }
                }
                const float RangeDecay = 0.010f;   // m/s envelope forgetting (~3-6s memory at 35mm strokes)
                // the envelope samples ONLY while the game says PENETRATED. It used to eat every
                // d2 - including the male standing across the room at scene start (log: range [9..276]mm
                // on a 59mm canal) - and at 10mm/s the spike took ~25s to forget, holding push≈0.93 the
                // whole time. Until the first penetrated frame the envelope stays uninitialized (its
                // negative range makes push read 0); while pulled out it freezes and resumes on re-entry.
                bool envSample = !MainGameWomb.HMotionKnown || MainGameWomb.HPenetrated;
                if (envSample)
                {
                    if (_minD > _maxD) { _minD = _maxD = d2; }   // first penetrated frame
                    _minD = Mathf.Min(_minD + RangeDecay * Time.deltaTime, d2);
                    _maxD = Mathf.Max(_maxD - RangeDecay * Time.deltaTime, d2);
                }
                float range = _maxD - _minD;
                float push = range > 0.006f ? Mathf.Clamp01((_maxD - d2) / range) : 0f;   // guard: idle/no strokes yet (6mm - close-body anims stroke only ~10-14mm)
                // SMOOTH the push: on short strokes (12-19mm ranges) the raw envelope is near-binary
                // (d touches the extremes) and slammed the stretch 0<->max - the "sudden stretch that
                // reads as a jump". Rise faster than fall so the push still leads the thrust.
                float kp = push > _pushSm ? 6f : 3f;
                _pushSm = Mathf.Lerp(_pushSm, push, 1f - Mathf.Exp(-kp * Time.deltaTime));
                _womb.ExternalPress = _pushSm;
                // The smoothing compresses a fast stroke's range (log: raw push 0..1 but sm 0.48..0.86),
                // so the commanded tip only used the middle of the canal. RE-NORMALIZE sm to its own
                // running min/max (slow decay) -> strokeN spans a true 0..1 every stroke regardless of
                // stroke speed. This is the signal the tip command uses.
                if (_smMin > _smMax) { _smMin = _smMax = _pushSm; }
                _smMax = Mathf.Max(_pushSm, _smMax - 0.12f * Time.deltaTime);
                _smMin = Mathf.Min(_pushSm, _smMin + 0.12f * Time.deltaTime);
                float strokeN = (_smMax - _smMin > 0.05f) ? Mathf.Clamp01((_pushSm - _smMin) / (_smMax - _smMin)) : _pushSm;

                // ===== AUTO PENIS LENGTH =====
                // Per-stroke size modulation was rejected (rubber penis). Instead FIT the penis's
                // natural length ONCE per animation (slow 15mm/s drift, imperceptible; constant during
                // play): the deepest point of the animation should put the tip just at/past the cervix
                // (In-stroke %). Too long (tip parked at the womb, BP squish eating the whole stroke)
                // -> shortens until the animation's own motion shows; too short (never reaches) ->
                // lengthens. The STROKE itself is 100% the animation's own base motion.
                // Colinear estimate: path base->entrance ≈ d2 − Dpin, so tip depth = L − (d2 − Dpin).
                // LEGACY pin (auto-length OFF): no fit state exists, but the canal solvers still
                // read the fit fields - starving them was why the canal stopped matching the penis on
                // this path (reported):
                //  - ExternalFitLocked is written only by the fit. Toggling auto-length off mid-measure
                //    left it FALSE forever: the girth latch refused to re-measure on any later pose
                //    (early-return) and the stretch stayed clamped to base.
                //  - FitSquishRatio / NaturalDanLen are static and only the fit wrote them - stale from
                //    a previous male or session, or 1.0 while BP is actually STRETCHING the visual shaft
                //    to the constrained pin and the stroke drive sweeps squish per frame. That is the
                // exact physics the girth corrector exists for; feed it the legacy inputs:
                //    visual length = base->pin (dan_end is constrained there), natural = BP's own
                //    m_baseDanLength captured at wiring.
                if (!autoLen && _womb != null)
                {
                    _womb.ExternalFitLocked = true;
                    if (_danLen0 > 1e-3f)
                    {
                        NaturalDanLen = _danLen0;
                        float dLegacy = _danBase != null ? Vector3.Distance(_danBase.position, rawPin) : 0f;
                        if (dLegacy > 1e-3f) FitSquishRatio = Mathf.Clamp(dLegacy / _danLen0, 0.5f, 1.25f);
                    }
                }
                if (autoLen)
                {
                    float canal = _womb.CanalLenWorld;
                    float wantTip = Mathf.Clamp(LiquidWobbleMPBPlugin.CfgHInStrokePct * 0.01f, 0.5f, 1.4f) * canal;
                    // publish the fit state - the womb suppresses its STRETCH reaction until the
                    // penis is fitted (the spawn-time uncut penis read stroke 112mm vs a 102mm canal ->
                    // stretch=100 instantly, then decayed over seconds as the fit cut in: the
                    // "womb spawns stretched then shrinks". Pre-lock over-depth is a fitting artifact,
                    // not an anatomical event - the womb shouldn't react to it.)
                    // a NO-MOTION lock does NOT enable the stretch. A first lock with no stroke
                    // over-estimates the penis (lifting/駅弁 locked "[no motion]" at 222mm/125%), which
                    // over-drove the stretch until the real stroke re-measured it down ("over-stretched
                    // on first insertion, later corrected). Keep the stretch suppressed (base only)
                    // until a MOTION-based lock exists.
                    // the penis pushed too far before the womb started to displace. The
                    // contact stretch is no longer dead for the whole ~2.2s measuring window - while
                    // MEASURING (a primed length exists) it is live whenever the game says PENETRATED
                    // and the motion state has settled (the same gate the measurement sampling uses),
                    // so the dome reacts from the first stable stroke of a new pose. Suppression
                    // remains for transition frames and for no-motion locks (those over-
                    // estimate the penis and have no strokes to react to anyway).
                    bool contactStable = MainGameWomb.HMotionKnown
                        ? (MainGameWomb.HPenetrated && Time.unscaledTime - MainGameWomb.HMotionChangedAt > 0.4f)
                        : _measT >= 1.2f;
                    _womb.ExternalFitLocked = (_fitState == 2 && !_lockedNoMotion) || (_fitState == 1 && contactStable);
                    // ===== ONE-TIME POSE FIT: PRIME -> measure ONE stroke -> LOCK. =====
                    // Anims are loops - once measured, nothing changes until the next pose (hard rule).
                    // Re-measure fires ONLY on MainGameWomb.PoseVersion (HSceneProc.ChangeAnimator hook,
                    // the same trigger BP uses). Length cap 1.25×natural: the penis must never visibly
                    // stretch beyond that under ANY circumstances (hard rule; was 1.8).
                    const float LCap = 1.25f;
                    { var hmLoop = MainGameWomb.HMotion; if (hmLoop != null && hmLoop.IndexOf("Loop", StringComparison.Ordinal) >= 0) s_lastLoop = hmLoop; }
                    FitSquishRatio = (_danLen0 > 1e-3f && _danLen > 1e-3f) ? Mathf.Clamp(_danLen / _danLen0, 0.5f, 1.25f) : 1f;   // live squish for the canal-width solver
                    NaturalDanLen = _danLen0;   // male identity for the girth latches
                    if (_seenPose != MainGameWomb.PoseVersion && _fitState == 2)
                    {
                        _seenPose = MainGameWomb.PoseVersion;
                        _fitState = 1; _measT = 0f; _measMin = float.MaxValue; _measMax = 0f;
                        _lockedNoMotion = false; _noMotionReopens = 0; _lockLoop = null;
                        // the thrust envelopes are PER-POSE state - the new pose's geometry makes
                        // the old extremes meaningless (and a transition spike would linger for ~25s).
                        _minD = float.MaxValue; _maxD = 0f; _smMin = float.MaxValue; _smMax = 0f;
                        // if THIS pose+char was fit before, SNAP to its answer now (repeat pose = no
                        // re-settle jump); else keep the current length (≈ the running estimate from the
                        // previous pose) and let the one-stroke measure refine it - either way, small/no jump.
                        float reEst;
                        if (s_poseFit.TryGetValue(FitKey(canal), out reEst))
                        {
                            _danLen = Mathf.Clamp(reEst, _danLen0 * 0.65f, _danLen0 * LCap);
                            try { _fBaseLen.SetValue(_danAgent, _danLen); } catch { }
                        }
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: fit RE-MEASURING (pose change) — one stroke (~1.5s); size stays locked meanwhile.");
                    }
                    // a NO-MOTION lock taken during the pose's OPENING IDLE mis-sizes everything
                    // (立位 locked "[no motion]" at 214mm/120% while its WLoop later strokes 55mm: the
                    // tip parked past the cervix, squish flattened the real strokes - "penis barely
                    // moved" - and the compression pulsed against the static tip - "dome out of sync").
                    // When a LOOP state starts and the current lock said no-motion, RE-OPEN the
                    // measurement (max 2 per pose; genuine poke poses simply re-lock the same).
                    if (_fitState == 2 && _lockedNoMotion && _noMotionReopens < 2 && MainGameWomb.HMotion != _loopCheckedState)
                    {
                        _loopCheckedState = MainGameWomb.HMotion;
                        if (_loopCheckedState != null && _loopCheckedState.Contains("Loop"))
                        {
                            _noMotionReopens++;
                            _fitState = 1; _measT = 0f; _measMin = float.MaxValue; _measMax = 0f;
                            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: no-motion lock RE-OPENED — loop state '" + _loopCheckedState + "' started (the pose may stroke after its opening idle); re-measuring.");
                        }
                    }
                    // a LOCKED fit belongs to the loop it was measured in - when the loop changes
                    // (WLoop<->SLoop via the gauge), re-prime from the NEW loop's cached/learned answer and
                    // re-measure one stroke. Toggling back later snaps instantly from s_poseFit (no jump).
                    if (_fitState == 2 && _lockLoop != null && s_lastLoop != _lockLoop)
                    {
                        _lockLoop = s_lastLoop;
                        float loopEst;
                        if (s_poseFit.TryGetValue(FitKey(canal), out loopEst))
                        {
                            _danLen = Mathf.Clamp(loopEst, _danLen0 * 0.65f, _danLen0 * LCap);
                            try { _fBaseLen.SetValue(_danAgent, _danLen); } catch { }
                        }
                        _fitState = 1; _measT = 0f; _measMin = float.MaxValue; _measMax = 0f;
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: fit RE-MEASURING (loop changed to '" + s_lastLoop + "') — strong/weak loops stroke ~30mm apart.");
                    }
                    if (_fitState == 0 && _fBaseLen != null && _danAgent != null && _danLen0 > 1e-3f)
                    {
                        // PRIME instantly from the CURRENT frame so the penis never STARTS at max
                        // stretch (the old drift began at natural 178mm and shrank over ~10s - the
                        // "womb+penis start at max stretch" report).
                        _seenPose = MainGameWomb.PoseVersion;
                        // PRIME from the prediction cache when there is one (kills the settle jump). The
                        // The fallbacks are FIRST-EVER only: NATURAL when already penetrated at spawn (the
                        // current mid-stroke d2 can't be trusted as a shallow reference -> "reached only the
                        // middle" if d2+wantTip were allowed to clamp to the floor), else d2+wantTip pre-insertion.
                        float primeEst, pMin, pRange;
                        if (s_poseFit.TryGetValue(FitKey(canal), out primeEst)) { }              // this exact pose+char before -> its answer
                        // LEARNED profile for this ANIMATION at HER size (pose + character scale),
                        // config-independent and persisted -> predicts a character/session never fitted before.
                        else if (canal > 1e-4f && PredictStroke(MainGameWomb.CurrentAnimKeyV + "|" + s_lastLoop, HerScale(),
                                                _danBase != null ? _danBase.lossyScale.y : 0f, out pMin, out pRange))
                            primeEst = FitFromStroke(pMin, pRange, canal, _danLen0, LCap);
                        else if (s_lastFitLen > 1e-3f) primeEst = s_lastFitLen;                  // running estimate (same-char cluster)
                        else primeEst = MainGameWomb.HPenetrated ? _danLen0 : (d2 + wantTip);    // first ever
                        _danLen = Mathf.Clamp(primeEst, _danLen0 * 0.65f, _danLen0 * LCap);
                        _fitSurplus = Mathf.Max(0f, _danLen - Mathf.Min(d2 + wantTip, _danLen0 * LCap));
                        try { _fBaseLen.SetValue(_danAgent, _danLen); } catch { }
                        _fitState = 1; _measT = 0f; _measMin = float.MaxValue; _measMax = 0f;
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: penis size PRIMED at " + (_danLen * 1000f).ToString("F0") + "mm — settling, then measuring one stroke to lock.");
                    }
                    else if (_fitState == 1 && _fBaseLen != null && _danAgent != null && _danLen0 > 1e-3f)
                    {
                        // GAME-STATE-DRIVEN sampling - same source as the penetration
                        // gate): sample ONLY while the game says PENETRATED and the motion state has
                        // been STABLE >0.4s (animator crossfade); any state change (Insert->WLoop, a
                        // position sub-change...) RESTARTS the window. The game itself signals when
                        // the transition is over - replaces the blind 1.2s settle, which still caught
                        // transitions (a "stroke [49..391]mm" reading blew the length to the 125% cap).
                        bool stable = MainGameWomb.HMotionKnown
                            ? (MainGameWomb.HPenetrated && Time.unscaledTime - MainGameWomb.HMotionChangedAt > 0.4f)
                            : _measT >= 1.2f;   // flag unavailable (already LogError'd loudly): time-settle
                        if (_measMotion != MainGameWomb.HMotion)
                        {
                            _measMotion = MainGameWomb.HMotion;
                            if (_measMin != float.MaxValue)
                                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: fit window restarted (motion state changed to '" + _measMotion + "').");
                            _measT = 0f; _measMin = float.MaxValue; _measMax = 0f;
                        }
                        _measT += Time.deltaTime;
                        if (stable)
                        {
                            // RESEARCH: at the exact frame a NEW deepest point is hit, capture the
                            // base->canal vector in HIS base-local frame. Its magnitude is MIN, but the
                            // DIRECTION encodes the pose STAGING (the "she's held higher in lifting so the tip
                            // reaches less far" effect) - information a scalar distance throws away, and the
                            // most likely place a "something more complex" mechanic would show up.
                            if (d2 < _measMin && _danBase != null && _entrance != null)
                                _deepVec = _danBase.InverseTransformPoint(_entrance.position);
                            _measMin = Mathf.Min(_measMin, d2); _measMax = Mathf.Max(_measMax, d2);
                        }
                        bool strokeSeen = _measMax - _measMin > 0.004f && _measMin != float.MaxValue;
                        // Validity: a loop stroke is never >150mm - a bigger range = polluted window.
                        if (strokeSeen && _measMax - _measMin > 0.15f)
                        {
                            _measT = 0f; _measMin = float.MaxValue; _measMax = 0f;
                            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: fit measurement rejected (range >150mm = transition caught) — restarting the window.");
                        }
                        // ABSOLUTE plausibility: a deep end several canals long means the reference
                        // frame was mid-rebuild (pose change; BP m_referenceTarget was null moments
                        // before) even though the window looked 'stable' - MEASURED: stroke [317..336]mm
                        // locked on an 85mm canal, penis capped at 125%, aim visibly missed ("penis lost
                        // target"). Reject and re-measure; never lock garbage.
                        else if ((strokeSeen ? _measMin : d2) > canal * 2f && _measT >= 2.2f)
                        {
                            _measT = 0f; _measMin = float.MaxValue; _measMax = 0f;
                            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: fit measurement rejected (deep end "
                                + ((strokeSeen ? _measMin : d2) * 1000f).ToString("F0") + "mm >> canal " + (canal * 1000f).ToString("F0")
                                + "mm — reference frame mid-rebuild) — restarting the window.");
                        }
                        // settle once, do not re-fit. Lock on the FIRST real stroke. The
                        // no-motion fallback is pushed 6s->12s so a moving pose locks ONCE on its stroke
                        // instead of taking a premature no-motion lock (148) then re-fitting on motion
                        // (115) - an observed double-settle. Genuine poke poses still lock at 12s.
                        else if ((_measT >= 2.2f && strokeSeen) || _measT >= 12f)
                        {
                            // Deepest leg + desired tip depth, floored so the out-stroke keeps the tip
                            // solidly inside; capped 1.25×natural (no rubber penis, ever).
                            float deepL = (strokeSeen ? _measMin : d2) + wantTip;
                            // PARK CUT, RANGE-SCALED: SMALL strokes turn around just BELOW the
                            // cervix (overshoot parked the tip for most of the visible motion);
                            // BIG strokes keep the full In-stroke overshoot so the deep end PUSHES the
                            // womb dome each stroke - the womb is pushed along with penis motion where
                            // penis is deep and motion range is big" - the full cut produced
                            // stretchW>5 in only 2 of 187 engaged samples). range<=35% of canal: full
                            // cut; >=80%: no cut; linear between. No-motion poses keep the full press.
                            if (strokeSeen)
                            {
                                // ABSOLUTE stroke scale + ABSOLUTE poke: animations are
                                // authored in body-space mm, and so is the poke that should be VISIBLE.
                                // The old %-of-canal overshoot peaked at ~6-10mm, of which BP's squish
                                // shows only ~20% (=1-2mm: "penis enters the dome without deforming").
                                // Deep strokes (>=70mm) now target the tip 25mm PAST the cervix (the
                                // KK-approved dome-poke magnitude); small strokes (<=30mm) keep the
                                // just-below-cervix turnaround; linear between.
                                // too many poses poked, so reduce the NUMBER. The poke
                                // scales with stroke RANGE, and the onset was 30mm so most poses (40-48mm
                                // range on the tested char) poked. Raise onset 30->40mm: poke now starts at
                                // ~45mm range, so only the bigger-stroke (genuinely deep) poses push the dome
                                // and the mid-range ones land just below the cervix. Deep-poke magnitude kept.
                                deepL = FitFromStrokeRaw(_measMin, _measMax - _measMin, canal);   // shared with the predictor
                            }
                            // Keep-inside floor 0.15 -> 0.20 x canal: the straight base->entrance
                            // leg UNDERCOUNTS the true bent path at the SHALLOW end of the stroke, so a
                            // computed 15% read as "almost exited" on screen (reported). 25% was tried
                            // but keep-in only ever LENGTHENS - in big-range poses that parks more of the
                            // deep half past the cervix and flattens pose variety; 20% is the approved
                            // balance (visibly inside at the top, variety kept).
                            // for a MOTION lock FitFromStrokeRaw already folded keep-in into deepL,
                            // so only the no-motion (parked) branch still needs it applied here.
                            float keepIn = strokeSeen ? float.MinValue : (d2 + 0.20f * canal);
                            // Size floor 0.65x natural (hard rule): a reasonable minimum below which
                            // the fit never reduces the penis, whatever the animation would prefer.
                            _danLen = Mathf.Clamp(Mathf.Max(deepL, keepIn), _danLen0 * 0.65f, _danLen0 * LCap);
                            // the 0.65-floor's addition over the anatomical fit is VISUAL length
                            // only - record it so the compression feed can ignore it (see the feed).
                            _fitSurplus = Mathf.Max(0f, _danLen - Mathf.Min(Mathf.Max(deepL, keepIn), _danLen0 * LCap));
                            try { _fBaseLen.SetValue(_danAgent, _danLen); } catch { }
                            _fitState = 2;
                            _lockLoop = s_lastLoop;   // this lock is valid for THIS loop only
                            _lockedNoMotion = !strokeSeen;   // no-motion locks stay re-openable when a loop state starts
                            // remember this fit to PREDICT the next one (MOTION locks only - no-motion
                            // locks over-estimate). This is what turns the next pose's 178->140 settle jump
                            // into a small correction (or none, for a repeat pose).
                            if (strokeSeen) { s_lastFitLen = _danLen; s_poseFit[FitKey(canal)] = _danLen; }
                            // LEARN this animation's stroke as a (character-scale -> stroke) SAMPLE and
                            // persist it. Raw metres + her pelvis scale, NOT canal-normalized: canal is a
                            // config, her size is the character. Next session - or a different girl, or a
                            // different womb-scale setting - predicts this animation on FIRST encounter.
                            if (strokeSeen)
                            {
                                LoadAnimProf();
                                string apose = MainGameWomb.CurrentAnimKeyV + "|" + s_lastLoop;   // loop-keyed
                                float asc = HerScale();
                                System.Collections.Generic.List<AnimSample> alst;
                                if (!s_animProf.TryGetValue(apose, out alst))
                                { alst = new System.Collections.Generic.List<AnimSample>(); s_animProf[apose] = alst; }
                                var asmp = new AnimSample();
                                asmp.Min = _measMin; asmp.Range = _measMax - _measMin;
                                // record EVERY candidate predictor beside the outcome, so the best one
                                // can be picked from data later instead of assumed now.
                                asmp.FKok = asc;
                                asmp.FStat = HerStature();   // pose-independent (local bone offsets)
                                asmp.MScale = _danBase != null ? _danBase.lossyScale.y : 0f;
                                asmp.MDan = _danLen0;
                                // a sample is identified by (HER size, HIS size) - refresh only when BOTH
                                // match, otherwise a different-sized male would silently overwrite the other
                                // male's data at the same female size and wreck the fit.
                                int aat = -1;
                                for (int i = 0; i < alst.Count; i++)
                                    if (Mathf.Abs(alst[i].FKok - asc) < 0.02f
                                     && Mathf.Abs(alst[i].MScale - asmp.MScale) < 0.02f) { aat = i; break; }
                                // only touch the DISK when something was actually LEARNED - a new pose, a
                                // new character size, or a >0.5mm change on a known one. Once the table has
                                // converged the writes stop entirely (steady state = zero I/O), so leaving the
                                // learning enabled alongside a shipped pre-trained table costs nothing.
                                bool anew;
                                if (aat >= 0)
                                {
                                    anew = Mathf.Abs(alst[aat].Min - asmp.Min) > 0.0005f
                                        || Mathf.Abs(alst[aat].Range - asmp.Range) > 0.0005f;
                                    alst[aat] = asmp;                           // same size bucket -> refresh
                                }
                                else
                                {
                                    anew = true;
                                    if (alst.Count < 6) alst.Add(asmp);         // new size -> another point on the line
                                    else alst[alst.Count - 1] = asmp;
                                }
                                if (anew) SaveAnimProf();
                            }
                            float overDeep = strokeSeen ? Mathf.Max(0f, (_danLen - _measMin) - canal) * 1000f : Mathf.Max(0f, (_danLen - d2) - canal) * 1000f;
                            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: pose fit LOCKED [" + MainGameWomb.HAnimLabel + "] — stroke [" + (strokeSeen ? (_measMin * 1000f).ToString("F0") + ".." + (_measMax * 1000f).ToString("F0") : "no motion") + "]mm on canal " + (canal * 1000f).ToString("F0") + "mm, penis " + (_danLen * 1000f).ToString("F0") + "mm (" + (_danLen / _danLen0 * 100f).ToString("F0") + "% of natural), deep-end overshoot " + overDeep.ToString("F0") + "mm past the cervix (womb push). Frozen until the next pose change.");
                            // the research row is NO LONGER emitted here - the fit locks mid-settle and
                            // that is exactly what made identical conditions disagree 10x. The settled
                            // multi-cycle sampler above emits the research data instead.
                        }
                    }
                    // The physical model: the squish stop follows the DISPLACED cervix, not
                    // the resting one. Over-push splits: the dome takes CfgHWombPush (~0.65) of it and
                    // the rest becomes shaft squish ("slows a little due to resistance") - and because
                    // the stop point rises by the dome's current travel, the tip stays GLUED to the
                    // dome it is pushing. Before, the tip parked at the resting cervix (+15-20% BP
                    // leak) while the dome moved 1.0-1.5x the over-push: air gap on small chars, "tip
                    // stops but the dome keeps animating" on big ones.
                    if (_fSquishThr != null && _danLen > 1e-3f)
                        try
                        {
                            float domeD = _womb.CurrentDomeTravelMM * 0.001f;
                            // HOLD WHILE THE FIT IS UNSETTLED. _lastThr is a FRACTION of _danLen, so it
                            // is only meaningful when _danLen is the length BP is actually using. On a
                            // pose or variation change the fit re-measures for one stroke and the inputs
                            // degenerate - measured AUTO-LEN L=178(L0=0) with the tip reaching 176mm
                            // against a 104mm dome, i.e. full natural extent for exactly one stroke,
                            // which is the "pokes once then corrects" report. The PREVIOUS pose's stop
                            // point is a far better answer than one computed from a length BP is not
                            // using, and variations of one scene have similar canals - so keep it until
                            // the fit locks again. Still re-asserted every frame, just not recomputed.
                            bool fitUsable = _danLen0 > 1e-3f && _fitState == 2;
                            if (fitUsable || _lastThr <= 0f)
                                _lastThr = Mathf.Clamp((canal + domeD) / _danLen, 0.02f, 0.95f);
                            _fSquishThr.SetValue(_danOpts, _lastThr);
                        }
                        catch { }
                    // COMPRESSION feed: how much over-cervix excess BP's squish is absorbing
                    // right now (raw laid tip L−d2 minus the canal). This is the PRESS the womb feels;
                    // danEnd only leaks ~15-20% of it past the cervix. Depth/engagement stay danEnd-
                    // gated (unchanged); this only drives the displace WEIGHT while engaged.
                    // measure compression against the ANATOMICAL fit (_danLen minus the 0.65-floor
                    // surplus) - the floored extra length is squished by BP as pure visual, not a press
                    // the womb should feel. Small-char repro: L=115 floored (fit wanted 68) on a 59mm
                    // canal held compress 29-47mm -> stretchW pinned ~100 with the penis half-in.
                    _womb.ExternalCompressMM = Mathf.Max(0f, (_danLen - _fitSurplus - d2 - canal) * 1000f);
                    _womb.ExternalStrokeTrusted = _fitSurplus <= 1e-4f;   // floored penis -> danEnd leak is fake reach
                    // NO ESTIMATE FEED: the womb consumes ONLY BP's real danEnd (OnPreCullCanal
                    // override). The old L−d2 estimate silently took over whenever danEnd was
                    // unreadable - it ran a WHOLE session on dead agent handles (ref=- in every
                    // H-STATE) and kept the womb reacting while the penis was pulled out. Mechanism
                    // fails -> LogError + nothing moves (hard rule: no fallback paths, ever).
                }
                // STROKE (legacy path, auto-length OFF): sweep BP's squishThreshold with the smoothed
                // thrust so the tip's stop depth travels shallow..deep.
                // the penis doesn't reach the womb but is squished anyway - two holes:
                //  - A pose whose base->pin distance barely oscillates (range <= the same 6mm guard the
                //    push calc uses) forces push to 0, which PINNED the threshold at the 30mm shallow
                //    floor: constant heavy squish with the tip parked far short of the womb. A fake
                //    stroke must not squish a penis that is not stroking.
                //  - The deep end was a flat 72mm regardless of where THIS womb's cervix sits.
                // Mirror the fit path's stop: full push = canal + current dome travel, so the tip
                // reaches the displaced cervix exactly like auto-length mode; with no measurable stroke,
                // HOLD at that stop and let the animation's own depth show.
                else if (_danOpts != null && _fSquishThr != null && _danLen > 1e-3f)
                {
                    float capMM = (_womb.HasCanal && _womb.CanalLenWorld > 1e-4f)
                        ? (_womb.CanalLenWorld + _womb.CurrentDomeTravelMM * 0.001f) * 1000f
                        : LiquidWobbleMPBPlugin.CfgHStrokeDeep;
                    float mm = range > 0.006f
                        ? Mathf.Lerp(LiquidWobbleMPBPlugin.CfgHStrokeShallow,
                                     Mathf.Max(LiquidWobbleMPBPlugin.CfgHStrokeShallow + 5f, capMM), _pushSm)
                        : capMM;
                    try { _lastThr = Mathf.Clamp(mm * 0.001f / _danLen, 0.02f, 0.95f); _fSquishThr.SetValue(_danOpts, _lastThr); _womb.ExternalStrokeMM = mm; }
                    catch { }
                }
                // Intent tip kept as a log-only reference (hIntent in the WombExpand line).
                Vector3 dir = rawPin - _danBase.position;
                if (dir.sqrMagnitude > 1e-8f && _danLen > 1e-3f)
                {
                    _womb.ExternalIntentTip = _danBase.position + dir.normalized * _danLen;
                    _womb.HasIntentTip = true;
                }
                if (LiquidWobbleMPBPlugin.CfgDebugLog && Time.unscaledTime >= _dbgNext)
                {
                    _dbgNext = Time.unscaledTime + 2f;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("  PIN-THRUST d=" + (d2 * 1000f).ToString("F0") + "mm range=[" + (_minD * 1000f).ToString("F0") + ".." + (_maxD * 1000f).ToString("F0")
                        + "]mm push=" + push.ToString("F2") + "/sm=" + _pushSm.ToString("F2")
                        + (autoLen ? " AUTO-LEN L=" + (_danLen * 1000f).ToString("F0") + "(L0=" + (_danLen0 * 1000f).ToString("F0") + ")mm tipEst=" + _womb.ExternalStrokeMM.ToString("F0") + "/canal=" + (_womb.CanalLenWorld * 1000f).ToString("F0") + " strokeN=" + strokeN.ToString("F2") : " strokeMM=" + Mathf.Lerp(LiquidWobbleMPBPlugin.CfgHStrokeShallow, LiquidWobbleMPBPlugin.CfgHStrokeDeep, _pushSm).ToString("F0"))
                        + " stretchW=" + _womb.CurrentStretchWeight.ToString("F0")
                        + " follow=" + (_follow * 1000f).ToString("F1") + "mm");
                }
            }
            else if (_womb != null) { _womb.ExternalPress = 0f; _womb.HasIntentTip = false; }
        }

        private void Update()
        {
            UpdateProxy();
            // EVENT-DRIVEN re-bind check: a pose change is the moment BP re-inits things - run
            // the watchdog tick NOW instead of up to 2s later. The 2s poll stays as the TESTING
            // diagnostic (hard rule): if IT ever catches a dead agent that the pose event missed, the
            // log names the event that should really be hooked.
            if (_watchPose != MainGameWomb.PoseVersion)
            {
                _watchPose = MainGameWomb.PoseVersion;
                _next = 0f;
                // RETARGET on EVERY pose change, unconditionally - not only when the
                // watchdog finds dead agents. A pose change is exactly when BP rebuilds state; the
                // "sometimes lost target" cases were agents that stayed alive but went stale. A full
                // re-attach (pin + taps + options + re-primed fit) each change makes the aim
                // deterministic instead of depending on which parts BP happened to recreate.
                _fitState = 0;
                MainGameWomb.ReattachPenisAim();
                return;
            }
            if (_agent == null || _fInner == null || _proxy == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            try
            {
                // AGENT WATCHDOG: cached agents no longer in BP's live lists = BP re-created
                // them; everything below would write to dead objects. Re-run the full attach (rebinds
                // the pin, taps, options and length control on THIS component) and re-PRIME the fit.
                if (!MainGameWomb.BPAgentsAlive(_agent, _danAgent))
                {
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP re-created its agents — all cached handles are DEAD (pin/size/reads were no-ops). Re-attaching to the live agents.");
                    _fitState = 0;   // re-PRIME on the fresh agent (its natural length is re-read too)
                    MainGameWomb.ReattachPenisAim();
                    return;
                }
                var cur = _fInner.GetValue(_agent) as Transform;
                if (cur != _proxy)
                {
                    _fInner.SetValue(_agent, _proxy);
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP m_innerTarget re-pinned (BP re-initialized its agents).");
                }
                if (_fKokanPush != null && _colOpts != null && (bool)_fKokanPush.GetValue(_colOpts))
                {
                    _fKokanPush.SetValue(_colOpts, false);
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP kokan push/pull re-disabled (something re-enabled it).");
                }
                // LIVE BP-HEALTH (2s): watch m_bpDanPointsFound - if it flips FALSE mid-scene (new anim /
                // reload dropped the BP uncensor) the aim target is ignored and the endpoint goes random.
                const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                bool bp = false, dp = false;
                try { var f = _danAgent.GetType().GetField("m_bpDanPointsFound", BF); if (f != null) bp = (bool)f.GetValue(_danAgent); } catch { }
                try { var f = _danAgent.GetType().GetField("m_danPointsFound", BF); if (f != null) dp = (bool)f.GetValue(_danAgent); } catch { }
                if (bp != _lastBpHealth) { _lastBpHealth = bp; LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP-HEALTH bpDanPointsFound=" + bp + " danPointsFound=" + dp + " (our aim target is " + (bp ? "RESPECTED" : "IGNORED — endpoint body-driven/random") + ")."); }
                // PENETRATION-PATH watch (debug): BP's live m_danPenetration + which reference
                // target it resolved - the direct proof of whether the vaginal path (pin honored) is
                // active, esp. around the KKS idle case the motion-input prefix corrects.
                if (LiquidWobbleMPBPlugin.CfgDebugLog)
                {
                    bool pen = false; string rn = "-";
                    try { var f = _danAgent.GetType().GetField("m_danPenetration", BF); if (f != null) pen = (bool)f.GetValue(_danAgent); } catch { }
                    try { var f = _danAgent.GetType().GetField("m_referenceTarget", BF); var rt = f != null ? f.GetValue(_danAgent) as Transform : null; if (rt != null) rn = rt.name; } catch { }
                    // AIM GEOMETRY too: a persistent mistarget shows up as a large tip<->pin gap, or as
                    // a pin sitting far from the womb's own penis_target. Bucketed into the signature so
                    // it re-logs when the aim actually moves, not every frame.
                    string aim = "-";
                    try
                    {
                        // BP's own end marker is what it drives; compare it against the pin point.
                        Transform tip = null;
                        try { var ft = _danAgent.GetType().GetField("m_danEnd", BF); tip = ft != null ? ft.GetValue(_danAgent) as Transform : null; } catch { }
                        Vector3 pinW = _proxy != null ? _proxy.position : Vector3.zero;
                        Vector3 tgtW = _target != null ? _target.position : Vector3.zero;
                        float tipGap = (tip != null && _proxy != null) ? Vector3.Distance(tip.position, pinW) * 1000f : -1f;
                        float pinOff = (_proxy != null && _target != null) ? Vector3.Distance(pinW, tgtW) * 1000f : -1f;
                        aim = "tipToPin=" + tipGap.ToString("F0") + "mm pinToTarget=" + pinOff.ToString("F0")
                            + "mm target='" + (_target != null ? _target.name : "NULL") + "'";
                    }
                    catch { }
                    string sig = pen + "/" + rn + "/" + aim;
                    if (sig != _lastPenPath) { _lastPenPath = sig; LiquidWobbleMPBPlugin._logger?.LogInfo("  BP-PATH danPenetration=" + pen + " refTarget='" + rn + "' motion='" + MainGameWomb.HMotion + "' " + aim + " (pin " + (pen && rn != "-" ? "HONORED" : "DROPPED — dan reset path") + ")."); }
                }

                // DAN-OPTIONS GUARD (2s): BP rebuilds/rewrites m_danOptions from ITS config on setting
                // changes and agent re-inits - and a BP config with Simplify DISABLED
                // silently flips onto ConstrainDanToBody where the CLIPPING OFFSETS (front 0.08/0.06!)
                // shove the endpoint toward the FRONT WALL. Also a swapped object makes the stroke-drive
                // writes go to a DEAD options instance. Re-fetch the CURRENT object, re-apply the
                // overrides + simplifyVaginal=true every tick; log when a swap/flip was corrected.
                if (_fOptOnAgent != null && _danAgent != null)
                {
                    var curOpt = _fOptOnAgent.GetValue(_danAgent);
                    if (curOpt != null)
                    {
                        bool swapped = !ReferenceEquals(curOpt, _danOpts);
                        if (swapped) SetStrokeControl(curOpt);   // re-resolve squishThreshold/danLengthSquish fields on the NEW object
                        // ALWAYS re-apply the override string + simplify (idempotent, 2s): BP's config
                        // re-applies MUTATE THE SAME OBJECT (log: config Squish 0.6 silently replaced
                        // the 0.8 without an object swap -> less over-depth absorbed -> tip 33mm past the
                        // cervix -> stretch pegged). A cheap unconditional re-assert closes that for good.
                        string ovr = LiquidWobbleMPBPlugin.CfgHBPDanOptions;
                        if (!string.IsNullOrEmpty(ovr)) MainGameWomb.ApplyOptions(curOpt, ovr);
                        var fSimp = curOpt.GetType().GetField("simplifyVaginal", BF);
                        if (fSimp != null) fSimp.SetValue(curOpt, true);
                        // TWITCH FIX: ApplyOptions just rewrote squishThreshold from the config
                        // string (0.40), stomping the stroke-drive value UpdateProxy wrote EARLIER this
                        // same frame - BP's LateUpdate then laid the penis at a different squished length
                        // for one frame every 2s (the visible mid-animation twitch). Re-assert it now.
                        if (_fSquishThr != null && _lastThr > 0f)
                            try { _fSquishThr.SetValue(_danOpts, _lastThr); } catch { }
                        if (swapped) LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP DanOptions object SWAPPED (agent re-init) — stroke-drive re-bound + overrides re-applied.");
                    }
                }
            }
            catch { }
        }
        // BP's own reset entry points on DanAgent (same type as SetDanTarget). Fail-loud: if a name is
        // gone in a future BP the log says so rather than silently leaving the penis bent.
        private void HandDanBackToBP()
        {
            if (_danAgent == null) return;
            foreach (var name in new[] { "ResetDanAdjustment" })   // ClearDanTarget has no parameterless overload; this alone hands it back
            {
                var mi = _danAgent.GetType().GetMethod(name,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, System.Type.EmptyTypes, null);
                if (mi == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BetterPenetration has no parameterless DanAgent." + name
                        + " — cannot hand the penis back; it will keep its last bend until BP re-targets.");
                    continue;
                }
                try { mi.Invoke(_danAgent, null); LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP DanAgent." + name + "() — penis handed back to BetterPenetration."); }
                catch (System.Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BP DanAgent." + name + "() threw: " + e.Message); }
            }
        }

        private bool _lastBpHealth = true;
        private string _lastPenPath = "";

        private void OnDestroy()
        {
            try
            {
                if (_womb != null) { _womb.ExternalPress = 0f; _womb.HasIntentTip = false; _womb.ExternalStrokeMM = 0f; _womb.ExternalCompressMM = 0f; _womb.HasPenisBase = false; _womb.HasPenisRef = false; _womb.HasPenisEnd = false; }
                if (_fBaseLen != null && _danAgent != null && _danLen0 > 1e-3f)
                {
                    _fBaseLen.SetValue(_danAgent, _danLen0);
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP m_baseDanLength restored to " + (_danLen0 * 1000f).ToString("F0") + "mm.");
                }
                if (_fKokanPush != null && _colOpts != null)
                {
                    _fKokanPush.SetValue(_colOpts, _hadKokanPush);
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP kokan push/pull restored to " + _hadKokanPush + ".");
                }
                if (_agent != null && _fInner != null && ReferenceEquals(_fInner.GetValue(_agent), _proxy) && _original != null
                    && _original.name != ProxyName)
                {
                    _fInner.SetValue(_agent, _original);
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP m_innerTarget restored to '" + _original.name + "'.");
                }
                // HAND THE PENIS BACK TO BP. Restoring the fields above stops the drive here, but BP
                // only writes the dan bones while it is adjusting - so without this it simply stops and
                // the penis keeps whatever bend it was left in. BP re-targets every LateUpdate, so
                // clearing the target + adjustment is enough for it to take over cleanly next frame.
                HandDanBackToBP();
                MainGameWomb.RestoreBPSimplifyConfig();   // give the BP Simplify setting back
                MainGameWomb.RestoreBPUncensorDefaults(); // and their UncensorSelector defaults (reinstated, proven innocent)
            }
            catch { }
        }
    }

    // Mirrors every same-named cf_* bone of the womb item to the wearer's live skeleton.
    // Runs in LateUpdate (after the animator) AND Camera.onPreCull (after IK - the same
    // post-IK point the wobble feed samples, and this component registers first because it
    // is added before the effect components attach), so the mesh can never lag the body.
    internal class WombBoneMirror : MonoBehaviour
    {
        private Transform _wearer;
        private Transform[] _mine;
        private Transform[] _theirs;
        private Transform _pivot;          // wearer's cf_j_kokan - the scale anchor (entrance stays put)
        private Material _liquid;          // instanced CloXray/Liquid material (fill push)
        private float _lastFill = -1f;
        private WombExpandEffect _fx;      // engaged-state source (pull-out detection)
        // PULL-OUT cum flow: when the penis withdraws with cum in the womb, the cum flows womb->canal
        // then out the entrance. State machine over the two closed chambers (womb=_FillAmount,
        // canal=_FillAmount2). None -> WombToCanal (womb drains, canal fills) -> CanalOut (canal drains
        // out the bottom) -> None. Purely additive; canal returns to empty (its normal H state).
        private int _flowState;            // 0 none, 1 pre-fill canal, 2 wait for game drip, 3 drain womb, 4 drain canal
        private float _flowT, _flowStartFill, _flowDomeStart;
        private bool _prevPullOut;
        private float _lastFill2 = -1f, _lastBottom2 = -1f;
        private Renderer _liquidRend;      // renderer carrying the liquid material (extent MPB diag)
        private float _liqDbgNext;         // H-LIQUID diagnostic timer
        // Static 'mounddown' shaping (a Studio setup stretches the mound down to the female's
        // bottom - KKPE weight 50 there): applied as a LIVE F1 value in H. Not a reaction channel
        // (WombExpandEffect drives moundforward/back/left/right, never mounddown), so a static set is safe.
        private SkinnedMeshRenderer _moundSmr;
        private int _moundIdx = -2;        // -2 = not searched yet, -1 = mesh has no mounddown

        public void Bind(Transform wearer)
        {
            _wearer = wearer;

            var theirs = new Dictionary<string, Transform>();
            foreach (var t in wearer.GetComponentsInChildren<Transform>(true))
                // The guard survives the 7.4.0 rename ON PURPOSE: the womb's SKELETON is clo_-prefixed
                // and fails StartsWith("cf_") on its own, but the KEPT renderer path still starts with an
                // unrenamed cf_o_root - without this the walk could enter the womb's own cf_o_root into
                // `theirs` and self-pair the renderer root instead of mirroring it onto HERS.
                if (t.name.StartsWith("cf_") && !theirs.ContainsKey(t.name) && t.GetComponentInParent<WombBoneMirror>() == null)
                    theirs[t.name] = t;
            theirs.TryGetValue("cf_j_kokan", out _pivot);

            var mine = new List<Transform>();
            var match = new List<Transform>();
            var frozen = new List<Transform>();   // bones pinned rigid to the pelvis
            var frozenMatch = new List<Transform>();   // her matching bone (for the FOLLOW branch when the pin is toggled OFF)
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                Transform c;
                string role3 = MainGameWomb.HerNameFor(t.name);   // womb bone -> her equivalent
                if (role3.StartsWith("cf_") && theirs.TryGetValue(role3, out c))
                {
                    // the skin-helper bones that animate relative to the pelvis
                    // FOLLOW her matching bone (via frozenMatch) so the canal mesh - weighted ~98% to
                    // these four (cf_s_waist02 62%, cf_s_waist01 36%; legs bow the sides) - tracks her
                    // waist/leg articulation.
                    if (role3 == "cf_s_leg_L" || role3 == "cf_s_leg_R"
                        || role3 == "cf_s_waist01" || role3 == "cf_s_waist02")
                    {
                        frozen.Add(t); frozenMatch.Add(c);
                        continue;
                    }
                    mine.Add(t); match.Add(c);
                }
            }
            // parents before children so world writes compound correctly
            var order = new int[mine.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => Depth(mine[a]).CompareTo(Depth(mine[b])));
            _mine = new Transform[mine.Count];
            _theirs = new Transform[mine.Count];
            for (int i = 0; i < order.Length; i++) { _mine[i] = mine[order[i]]; _theirs[i] = match[order[i]]; }
            _kokanIdx = -1;
            for (int i = 0; i < _mine.Length; i++) if (ReferenceEquals(_theirs[i], _pivot)) { _kokanIdx = i; break; }   // lag meter
            _authScale = new Vector3[_mine.Length];   // authored rest local scales (bind-consistent skinning)
            for (int i = 0; i < _mine.Length; i++) if (_mine[i] != null) _authScale[i] = _mine[i].localScale;
            // the frozen skin-helpers FOLLOW her matching bone each frame (see SyncNow).
            _frozenT = frozen.ToArray();
            _frozenMatch = frozenMatch.ToArray();
        }

        private static int Depth(Transform t) { int d = 0; while (t.parent != null) { d++; t = t.parent; } return d; }

        private void OnEnable()  { Camera.onPreCull += OnPreCullSync; Camera.onPreRender += OnPreRenderSync; }
        private void OnDisable() { Camera.onPreCull -= OnPreCullSync; Camera.onPreRender -= OnPreRenderSync; }

        private void LateUpdate()
        {
            SyncNow();
            PushFill();
            EnsureMoundDown();
            EnsureDome();
        }

        // EPOCH COHERENCE - the womb syncs ONLY at LateUpdate now. The trace proved the bug:
        // in OLoop (only) the game moves her pelvis 9mm AFTER all LateUpdates; the per-camera re-syncs
        // chased that fresh pose, so the RENDERED womb sat 9mm forward of the PENIS (BP bends it
        // at the stale LateUpdate epoch - the penis following the teal marker perfectly proves penis and
        // line share that epoch) => penis+teal visually on the tube's back wall, fast anims only.
        // COHERENCE RULE: the womb must render at the SAME epoch as the penis and the aim - freshness
        // vs her skin is invisible (the womb sits inside flesh); penis-vs-womb offset is the whole bug.
        private void OnPreCullSync(Camera cam)
        {
            // when NOT penetrated, re-sync at the render epoch so the womb FOLLOWS HER even when
            // the game moves her pelvis fast AFTER LateUpdate (pull-out, pose change, idle repositioning).
            // Synced only at LateUpdate, the womb froze at its LU position and she moved out from
            // under it - left behind by her post-LU movement (measured 72mm on pull-out; reported as the womb
            // staying at the old place or flying out). While PENETRATED, keep LateUpdate-only sync so the womb
            // stays epoch-coherent with the penis + teal line - the womb-vs-her lag is invisible
            // then because the penis lags with it. Idempotent (positions the womb at her live kokan).
            if (!MainGameWomb.HPenetrated && Time.frameCount != _notPenSyncFrame)
            {
                _notPenSyncFrame = Time.frameCount;
                SyncNow();
            }
        }
        private int _notPenSyncFrame = -1;
        private int _notPenRenderFrame = -1;

        // FINAL re-sync at pre-render - the latest hook before skinning. During FAST loops the
        // girl's rig can receive its final pose after the LateUpdate/pre-cull syncs.
        private void OnPreRenderSync(Camera cam)
        {
            // when NOT penetrated, ALSO re-sync at pre-render - the LAST hook before the mesh is
            // skinned. The womb has no parent bone (it is a per-frame COPY of her poses, not an
            // attachment), so a hard pose snap after the copy leaves it behind (pull-out). Copying at the
            // latest possible moment always takes her FINAL position for the frame. While
            // penetrated, sync only at LateUpdate (epoch-coherent with the penis/line).
            if (!MainGameWomb.HPenetrated && Time.frameCount != _notPenRenderFrame)
            {
                _notPenRenderFrame = Time.frameCount;
                SyncNow();
            }
            // NO sync here while penetrated (see OnPreCullSync comment).
        }
        private int _kokanIdx = -1;
        private Transform[] _frozenT; private Transform[] _frozenMatch;   // skin-helpers that FOLLOW her bones
        private Vector3[] _authScale;   // authored rest local scales
        // ROUTE-B: set once the SMRs have been re-pointed at the wearer's bones. Unity then skins the
        // womb from her skeleton, so there is nothing for the mirror to copy - SyncNow becomes a no-op
        // (this also neutralises the pre-cull / pre-render re-syncs). ONE SHARED implementation -
        // the per-game #if split existed for the KKS seat servo, which is now removed (the body
        // was already unified); per-game seat constants stay in their own #if at the top of the file.
        public bool Rebound;

        public void SyncNow()
        {
            if (_mine == null || _wearer == null) return;
            if (Rebound) return;
            float s = MainGameWomb.HMirrorWombScaleFor(_pivot);   // KKS scales with her pelvis (female-tracking)
            bool scaled = _pivot != null && Mathf.Abs(s - 1f) > 0.001f;
            Vector3 pivot = scaled ? _pivot.position : Vector3.zero;
            // Whole-womb seat offset along the pelvis backward direction (F1 'Womb offset to back'):
            // the mesh's anatomical seat can sit too far toward her belly for some bodies/uncensors -
            // the penis then hugs the canal's BACK wall. Uniform on every bone = the womb translates
            // rigidly; the pelvis frame turns with the pose so it stays anatomical in any position.
            float seatBackMM = LiquidWobbleMPBPlugin.CfgHWombBack - MainGameWomb.SeatForwardMM;   //
            Vector3 seat = (_pivot != null && Mathf.Abs(seatBackMM) > 0.01f)
                ? -_pivot.forward * (seatBackMM * 0.001f) : Vector3.zero;
            // the KKS seat-correction servo was REMOVED - Route B (rebind) is the H default
            // and the mirror now exists only as the parity CALIBRATION INSTRUMENT (idle, unpenetrated)
            // and the research-build comparison lever, where the penetration-gated servo could never fire.
            for (int i = 0; i < _mine.Length; i++)
            {
                var m = _mine[i]; var c = _theirs[i];
                if (m == null || c == null) continue;
                // Scale is injected ONCE at the chain root (i==0, shallowest) so it propagates to every
                // descendant's lossy scale exactly once; positions are scaled about the entrance pivot so
                // the vaginal opening stays anchored while the womb grows upward/outward.
                // UNIFIED - both games use HER local scales (the KKS authored-scale special-
                // case reverted for the follow experiment; if the tube bows again that case is real, else
                // it was a casualty of the epoch bug fixed alongside it).
                m.localScale = (scaled && i == 0) ? c.localScale * s : c.localScale;
                Vector3 p = c.position;
                if (scaled) p = pivot + (p - pivot) * s;
                m.SetPositionAndRotation(p + seat, c.rotation);
            }

            // the anatomical-anchor search, the KKS servo (with its
            // jump guards) and the mirror-path probe were REMOVED. All of them lived below the
            // `if (Rebound) return;` gate, so in Route B - the H default since its introduction - none
            // ever executed (the probe in WombExpandEffect is the live replacement). The mirror
            // remains ONLY as the parity calibration instrument; its placement needs no servo (parity
            // runs idle and measures the raw mirror seat by design).

            // the frozen skin-helpers (cf_s_waist01/02, cf_s_leg_L/R) FOLLOW her matching bone each
            // frame, scaled about the pivot and seated like the main mirror, so the canal mesh tracks her
            // waist/leg articulation.
            if (_frozenT != null && _frozenT.Length > 0 && _kokanIdx >= 0 && _mine[_kokanIdx] != null)
            {
                for (int i = 0; i < _frozenT.Length; i++)
                {
                    var m = _frozenT[i]; var c = _frozenMatch != null && i < _frozenMatch.Length ? _frozenMatch[i] : null;
                    if (m == null || c == null) continue;
                    m.localScale = c.localScale;
                    Vector3 p = c.position;
                    if (scaled) p = pivot + (p - pivot) * s;
                    m.SetPositionAndRotation(p + seat, c.rotation);
                }
            }

            // NO WOMB ROTATION - hard rule, final: the womb stays in its anatomical, mirrored
            // orientation, period. Early aim experiments rotated it to "fix" alignment and made
            // everything worse (helicopter, frozen-wrong poses); the actual Studio-equivalent fix is
            // the END TARGET: BP's m_innerTarget = the womb's own penis_target bone (see UpdateProxy).
        }

        // ── Fill: F1 config base + finish bursts (ME has no H-scene UI) ─────────────────
        // Each inside-finish (the cum button) raises a burst accumulator by 'Fill on finish',
        // eased in over a few seconds on top of the base slider. The HFlag click watcher logs
        // every transition (and the full ClickKind name list once), so the trigger words can be
        // refined against real names if a game build differs.
        // CUM SURVIVES A RESPAWN. These were per-womb instance fields, so toggling the womb off (or ANY
        // internal respawn - the nudge bake, the placement-drift re-place) reset the accumulated cum,
        // the shot count and the belly dome to zero. The rule is that cum survives everything
        // except a real pull-out, which the state machine already handles. Session-scoped, keyed to the
        // girl so a different female starts clean.
        internal static Component s_cumOwner;
        private static float _burst;   // the finish-cum fill level (over the config base), driven by the state machine below
        // FINISH CUM state machine (by spec): each inside-finish RISES the womb fill to a PEAK over
        // the ejaculation animation (prolonged, spurty), then eases back to a lower SETTLE level as sex
        // continues. Levels per shot #: 1 -> 0.50 peak / 0.35 settle, 2 -> 0.80 / 0.65, 3+ -> 1.00 /
        // 1.00 (stays full). Shots BEYOND full drive the DOME EXPAND (wombbig + ovary_shrink). Reset on
        // pull-out / load.
        private static int _cumShot;
        private static float _cumFrom, _cumPeak, _cumSettle;   // fill level at shot start, this shot's peak, its settle
        private static float _cumPhaseT;                        // seconds since this shot fired (rise starts after CumDelay)
        private static int _cumPhase;                           // 0 hold-settle, 1 rising, 2 settling
        private static float _domeTarget;                       // wombbig/ovary_shrink weight (0..100), grows on shots past full
        private const float CumDelay = 0.6f, CumRiseDur = 4.0f, CumSettleDur = 8.0f;
        private SkinnedMeshRenderer _domeSmr; private int _wombBigIdx = -2, _ovaryShrinkIdx = -2;

        private void EnsureMoundDown()
        {
            if (_moundIdx == -2)
            {
                _moundIdx = -1;
                foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r == null || r.sharedMesh == null || r.sharedMesh.blendShapeCount == 0) continue;
                    for (int i = 0; i < r.sharedMesh.blendShapeCount; i++)
                        if (r.sharedMesh.GetBlendShapeName(i).ToLowerInvariant().Contains("mounddown")) { _moundSmr = r; _moundIdx = i; break; }
                    if (_moundIdx >= 0) break;
                }
                if (_moundIdx < 0)
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: 'mounddown' blendshape not found on the H womb — the mound-extend F1 slider has no effect on this mesh.");
            }
            if (_moundIdx >= 0 && _moundSmr != null)
            {
                float w = Mathf.Clamp(LiquidWobbleMPBPlugin.CfgHMoundDown, 0f, 100f);
                if (Mathf.Abs(_moundSmr.GetBlendShapeWeight(_moundIdx) - w) > 0.01f)
                    _moundSmr.SetBlendShapeWeight(_moundIdx, w);
            }
        }

        private static void CumLevels(int shot, out float peak, out float settle)
        {
            if (shot <= 1)      { peak = 0.50f; settle = 0.35f; }
            else if (shot == 2) { peak = 0.80f; settle = 0.65f; }
            else                { peak = 1.00f; settle = 1.00f; }   // 3+ = full & stays
        }

        private void QueueFinishBurst(string src)
        {
            _cumShot++;
            CumLevels(_cumShot, out _cumPeak, out _cumSettle);
            _cumFrom = _burst;      // rise from wherever the last shot settled
            _cumPhase = 1; _cumPhaseT = 0f;
            // From the 3rd creampie on (the womb is full), each extra load expands the dome (wombbig +
            // ovary_shrink) 40% -> full belly over ~3 overcum loads (25%/4 loads read too subtle).
            if (_cumShot >= 3) _domeTarget = Mathf.Clamp(_domeTarget + 40f, 0f, 100f);
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: inside finish #" + _cumShot + " (" + src + ") — fill " + _cumFrom.ToString("F2")
                + " -> peak " + _cumPeak.ToString("F2") + " -> settle " + _cumSettle.ToString("F2")
                + (_cumShot > 3 ? "; dome -> " + _domeTarget.ToString("F0") : "") + ".");
        }

        // Advance the finish-cum fill (called every frame from PushFill). Rise to peak over the
        // ejaculation (prolonged + slightly spurty), then ease to settle as sex continues.
        private void UpdateCumFill()
        {
            // A different girl starts clean; the same girl keeps what she has through any respawn.
            Component owner = MainGameWomb.FindTargetFemale();
            if (owner != null && !ReferenceEquals(owner, s_cumOwner))
            {
                if (s_cumOwner != null)
                {
                    _burst = 0f; _cumShot = 0; _cumPhase = 0; _cumPhaseT = 0f; _domeTarget = 0f;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: cum state reset — different H female.");
                }
                s_cumOwner = owner;
            }
            if (_cumShot <= 0) return;
            _cumPhaseT += Time.deltaTime;
            if (_cumPhase == 1)
            {
                float t = Mathf.Clamp01((_cumPhaseT - CumDelay) / CumRiseDur);   // 0.6s delay then rise over 4s
                if (t > 0f) _burst = Mathf.Lerp(_cumFrom, _cumPeak, SpurtEase(t));
                if (t >= 1f) { _cumPhase = 2; _cumPhaseT = 0f; }
            }
            else if (_cumPhase == 2)
            {
                float t = Mathf.Clamp01(_cumPhaseT / CumSettleDur);
                _burst = Mathf.Lerp(_cumPeak, _cumSettle, Mathf.SmoothStep(0f, 1f, t));
                if (t >= 1f) { _cumPhase = 0; _burst = _cumSettle; }
            }
        }

        // A rise that reads as SPURTS: 4 fast steps, each a quick climb then a brief hold.
        private static float SpurtEase(float t)
        {
            const float steps = 4f;
            float s = Mathf.Floor(t * steps);
            float f = Mathf.Clamp01((t * steps - s) * 1.7f);   // fast climb within the step, then hold
            return Mathf.Clamp01((s + Mathf.SmoothStep(0f, 1f, f)) / steps);
        }

        private void EnsureDome()
        {
            if (_wombBigIdx == -2)
            {
                _wombBigIdx = -1; _ovaryShrinkIdx = -1;
                foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r == null || r.sharedMesh == null || r.sharedMesh.blendShapeCount == 0) continue;
                    for (int i = 0; i < r.sharedMesh.blendShapeCount; i++)
                    {
                        string bn = r.sharedMesh.GetBlendShapeName(i).ToLowerInvariant();
                        if (_wombBigIdx < 0 && bn.Contains("wombbig")) { _domeSmr = r; _wombBigIdx = i; }
                        else if (_ovaryShrinkIdx < 0 && bn.Contains("ovary_shrink")) { _ovaryShrinkIdx = i; }
                    }
                    if (_wombBigIdx >= 0) break;
                }
                if (_wombBigIdx < 0)
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: 'wombbig' blendshape not found — cum-inflation (dome expand) has no effect on this mesh.");
            }
            if (_domeSmr != null && _wombBigIdx >= 0)
            {
                if (Mathf.Abs(_domeSmr.GetBlendShapeWeight(_wombBigIdx) - _domeTarget) > 0.01f)
                    _domeSmr.SetBlendShapeWeight(_wombBigIdx, _domeTarget);   // wombbig = cum-inflation ONLY
                // small females get a relatively OVERSIZED womb (sub-linear scaling), so the
                // ovaries can breach the small body. Pull them in with ovary_shrink proportional to the
                // oversize (effKok/kok - how much bigger than proportional the womb is). Combined with
                // the cum-dome shrink via Max, so it applies with or without cum. 0 at the reference
                // female or WombScaleBlend=1 (no oversize -> no shrink). wombbig is NOT touched.
                float ovaryTarget = _domeTarget;
                if (_pivot != null)
                {
                    float kok = _pivot.lossyScale.y;
                    float effKok = Mathf.Lerp(MainGameWomb.KokStd, kok, MainGameWomb.WombScaleBlend);
                    float oversize = kok > 1e-4f ? effKok / kok : 1f;
                    float smallShrink = Mathf.Clamp((oversize - 1f) * OvaryShrinkGain, 0f, OvaryShrinkMax);
                    ovaryTarget = Mathf.Max(_domeTarget, smallShrink);
                }
                if (_ovaryShrinkIdx >= 0 && Mathf.Abs(_domeSmr.GetBlendShapeWeight(_ovaryShrinkIdx) - ovaryTarget) > 0.01f)
                    _domeSmr.SetBlendShapeWeight(_ovaryShrinkIdx, ovaryTarget);
            }
        }
        // Ovary-shrink-for-small-female tuning (judged by eye): weight = (oversize-1)xGain, capped.
        public const float OvaryShrinkGain = 150f;
        public const float OvaryShrinkMax = 60f;

        private static Type _hflagType;
        private static System.Reflection.FieldInfo _fClick;
        private static UnityEngine.Object _hflag;
        private static object _lastClickBox;   // last boxed HFlag.click, so the per-frame path never stringifies
        private static float _hflagRetry;
        private static bool _namesLogged;
        // Counter-based detection (robust): HFlag keeps per-act finish COUNTERS that persist, unlike
        // the one-frame click pulse that script order can miss. Bound by name discovery + logged.
        private static object _countObj;
        private static System.Reflection.FieldInfo _fSonyuIn;
        private static int _lastSonyuIn = -1;
        // GAME MOTION STATE: read the KK pose info for penetrated or not. HFlag's current
        // anim state name ("Insert", "WLoop", "Idle", "Pull", "OUT"...). Penetrated per BP's OWN rule
        // (source line 2693): Contains("Insert") OR (not Idle AND not Pull AND not OUT). This is the
        // authoritative signal - the geometric danEnd gate alone let a near-idle penis (outside, but
        // pointing up within the lateral cone) keep the womb engaged.
        private static System.Reflection.FieldInfo _fNowAnim;
        private static bool _nowAnimMissing;
        private static string _lastMotion = "";
        private static string _unknownMotionLogged = "";
        // ALTERNATIVE/VARIANT detection: the H UI's "alternative" button swaps
        // HFlag.nowAnimationInfo (the AnimationListInfo the scene is playing) - sometimes without a
        // motion-state NAME change, so neither ChangeAnimator nor the state watch would re-fit.
        private static System.Reflection.FieldInfo _fAnimInfo;
        private static bool _animInfoMissing;
        private static object _lastAnimInfo;
        private static bool _animInfoSeen;
        private static System.Reflection.FieldInfo _fAiId, _fAiName;
        private static bool _aiFieldsTried;
        private static string _lastAnimDesc = "";
        // Shallow research dump: primitives/strings/enums + UnityEngine.Object names, one line. Used to
        // diff two variants of the same position and find the absolute main-vs-alt field.
        private static string DumpShallowFields(object o)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                foreach (var f in o.GetType().GetFields(BF))
                {
                    var ft = f.FieldType;
                    object v;
                    try { v = f.GetValue(o); } catch { continue; }
                    string s;
                    if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) s = v != null ? v.ToString() : "null";
                    else if (v is UnityEngine.Object uo) s = "obj:'" + (uo != null ? uo.name : "null") + "'";
                    else if (v is System.Collections.ICollection col) s = "n=" + col.Count;
                    else continue;
                    if (sb.Length > 0) sb.Append(" ");
                    sb.Append(f.Name).Append("=").Append(s);
                }
                return sb.ToString();
            }
            catch (Exception e) { return "dump failed: " + e.Message; }
        }
        // Diagnostic label for an AnimationListInfo: "id=.. name='..'". Best-effort field names (KK:
        // `id`, `nameAnimation`); a missing field just drops out of the label - this is a log tag,
        // not a behavior mechanism.
        private static string DescribeAnimInfo(object ai)
        {
            try
            {
                var t = ai.GetType();
                if (!_aiFieldsTried)
                {
                    _aiFieldsTried = true;
                    const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    _fAiId = t.GetField("id", BF);
                    _fAiName = t.GetField("nameAnimation", BF);
                    if (_fAiId == null && _fAiName == null)
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: AnimationListInfo has no id/nameAnimation — fields: " + string.Join(", ", Array.ConvertAll(t.GetFields(BF), f => f.Name)));
                }
                string s = "";
                if (_fAiId != null) s += "id=" + _fAiId.GetValue(ai);
                if (_fAiName != null) s += (s.Length > 0 ? " " : "") + "name='" + _fAiName.GetValue(ai) + "'";
                return s.Length > 0 ? s : "anim@" + ai.GetHashCode().ToString("X6");
            }
            catch { return "anim@?"; }
        }

        private void FeedPenetrationState()
        {
            if (_fx == null) _fx = GetComponentInChildren<WombExpandEffect>(true);
            if (_fx == null || _hflag == null || _hflagType == null || _nowAnimMissing) return;
            if (_fNowAnim == null)
            {
                _fNowAnim = _hflagType.GetField("nowAnimStateName");
                if (_fNowAnim == null)
                {
                    _nowAnimMissing = true;
                    _fx.HasPenetratedFlag = false;
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: HFlag.nowAnimStateName not found — the game-state penetration gate is unavailable (geometric gate only). HFlag fields: " + string.Join(", ", Array.ConvertAll(_hflagType.GetFields(), f => f.Name)));
                    return;
                }
            }
            try
            {
                // Variant watch: reference change on nowAnimationInfo = the game switched what it is
                // playing (position change OR the ALTERNATIVE button) -> re-fit. BumpPose debounces
                // the double-fire when ChangeAnimator also catches it.
                if (_fAnimInfo == null && !_animInfoMissing)
                {
                    _fAnimInfo = _hflagType.GetField("nowAnimationInfo");
                    if (_fAnimInfo == null)
                    {
                        _animInfoMissing = true;
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: HFlag.nowAnimationInfo not found — ALTERNATIVE-variant switches won't re-fit the penis (position changes still caught by ChangeAnimator).");
                    }
                }
                if (_fAnimInfo != null)
                {
                    object ai = _fAnimInfo.GetValue(_hflag);
                    if (!ReferenceEquals(ai, _lastAnimInfo))
                    {
                        bool first = !_animInfoSeen;
                        _lastAnimInfo = ai; _animInfoSeen = true;
                        if (ai != null)
                        {
                            // ALTERNATIVE detection (confirmed from the sweep run): the alt button
                            // swaps the AnimationListInfo but keeps the SAME id+name (Cowgirl id=4 locked
                            // twice). Same-description swap = the ALT toggle; it flips back on re-press;
                            // a different id/name = a new position (variant resets to main).
                            string desc = DescribeAnimInfo(ai);
                            bool sameAnim = !first && desc == _lastAnimDesc;
                            // variant identity now lives in MainGameWomb.OnPoseChanged (click-
                            // provenance gated). A registry HERE misidentified them - field-descriptions
                            // collide across different modded poses, so the base sweep minted fake
                            // ~v1..~v9. This watcher only mirrors the label + keeps the field dumps.
                            string vsuf = MainGameWomb.VariantSuffix;
                            _lastAnimDesc = desc;
                            MainGameWomb.HAnimLabel = desc + (vsuf.Length > 0 ? " ALT" + vsuf : "");
                            if (!first)
                            {
                                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: H animInfo swap -> " + MainGameWomb.HAnimLabel + (sameAnim ? " (same id = ALTERNATIVE toggle)" : " (new position)"));
                                // ABSOLUTE discriminator hunt (DebugLog): the two variants of one
                                // position are two AnimationListInfo objects with the same id+name - some
                                // field must differ (anim clip ref, mode, paramFemale...). Dump the shallow
                                // fields on every ALT toggle; diffing two consecutive dumps in the log
                                // reveals the field that says main-vs-alt ABSOLUTELY (parity is only a
                                // best-effort tag - it desyncs if the womb attaches mid-scene).
                                if (sameAnim && LiquidWobbleMPBPlugin.CfgDebugLog)
                                    LiquidWobbleMPBPlugin._logger?.LogInfo("  ANIMINFO-FIELDS: " + DumpShallowFields(ai));
                                MainGameWomb.BumpPose("nowAnimationInfo -> " + MainGameWomb.HAnimLabel);
                            }
                        }
                    }
                }
                string m = _fNowAnim.GetValue(_hflag) as string;
                if (m == null) { _fx.HasPenetratedFlag = false; return; }
                // POSITIVE-LIST: the old BP-derived rule defaulted UNKNOWN states to penetrated
                // - 'Drop' (the post-pull-out drip anim, penis clearly out) matched none of the
                // exclusions and kept the womb reacting after the pull-out button. Now: exclusions
                // first, then a state must POSITIVELY look inserted (Insert / *_IN_* / *Loop); anything
                // unrecognized = NOT penetrated (the womb must never react on a guess) + logged once.
                // 'Insert' wins BEFORE the exclusions (like BP's own rule): 'InsertIdle' =
                // inserted-and-resting - the earlier order let the Idle exclusion beat it and the womb
                // relaxed with the penis inside (log: 'InsertIdle' -> penetrated=False. Wrong.)
                bool exclude = m.Contains("Idle") || m.Contains("Pull") || m.Contains("OUT") || m.Contains("Touch") || m.Contains("Drop");
                bool include = m.Contains("IN") || m.Contains("Loop");
                bool pen = m.Contains("Insert") || (!exclude && include);
                if (!pen && !exclude && !include && m != _unknownMotionLogged)
                {
                    _unknownMotionLogged = m;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: unknown H motion state '" + m + "' — treated as NOT penetrated. Report if the womb should react during it.");
                }
                _fx.ExternalPenetrated = pen; _fx.HasPenetratedFlag = true;
                MainGameWomb.HPenetrated = pen; MainGameWomb.HMotionKnown = true;
                if (m != _lastMotion)
                {
                    _lastMotion = m;
                    MainGameWomb.HMotion = m; MainGameWomb.HMotionChangedAt = Time.unscaledTime;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: H motion '" + m + "' -> penetrated=" + pen + ".");
                }
            }
            catch { _fx.HasPenetratedFlag = false; }
        }

        private void PushFill()
        {
            WatchFinish();
            FeedPenetrationState();   // game's own penetrated/pulled-out flag -> womb gate
            UpdateCumFill();   // rise-to-peak / settle state machine drives _burst

            if (_liquid == null)
            {
                foreach (var r in GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.sharedMaterials == null) continue;
                    var mats = r.materials;   // instanced - never edit the shared bundle material
                    foreach (var m in mats)
                        if (m != null && m.shader != null && m.shader.name == "CloXray/Liquid") { _liquid = m; _liquidRend = r; break; }
                    if (_liquid != null) break;
                }
                if (_liquid == null) return;
                // Free-H cum belongs in the WOMB, not the canal: switch this instance to CLOSED mode
                // (independent chambers) with the canal chamber empty - the finish fill pools in the
                // bulb only. The shipped material stays connected for Studio.
                _liquid.SetFloat("_ChamberMode_0single_1connected_2closed", 2f);
                _liquid.SetFloat("_FillAmount2", 0f);
                _liquid.SetFloat("_FillBottom2", 0f);
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: H liquid switched to CLOSED chambers (womb-only fill).");
            }

            float baseFill = Mathf.Clamp01(LiquidWobbleMPBPlugin.CfgHFillAmount + _burst);

            // PULL-OUT detection: the GAME'S OWN pull-out motion states ("Pull" = the pull-out
            // button, "OUT" = finish-outside) - NOT the engagement edge. A pose change also drops
            // engagement for a moment and was wiping the accumulated cum (hard rule: cum survives pose
            // changes, disappears only on a real pull-out). Rising edge into a pull-out state fires it.
            // Flag unavailable => no reset at all (loud LogError already fired; no fallback paths).
            string hm = MainGameWomb.HMotion;
            bool pullOut = MainGameWomb.HMotionKnown && !string.IsNullOrEmpty(hm)
                && (hm.IndexOf("Pull", System.StringComparison.OrdinalIgnoreCase) >= 0 || hm.Contains("OUT"));
            if (pullOut && !_prevPullOut)
            {
                // Real pull-out (post-grace): the pooled finish-cum ALWAYS leaves the womb (hard rule:
                // +30% per shot, reset on load or pull-out); the flow animation runs when enabled.
                if (LiquidWobbleMPBPlugin.CfgHPullOutFlow && _flowState == 0 && baseFill > 0.05f)
                {
                    _flowState = 1; _flowT = 0f; _flowStartFill = baseFill; _flowDomeStart = _domeTarget;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: pull-out — leak choreography: pre-fill canal (top-down), WAIT for the game's drip ('Drop'), drain womb, drain canal (from fill " + baseFill.ToString("F2") + ").");
                }
                if (_cumShot > 0 || _burst > 0f)
                {
                    // Shots/burst reset immediately (the display is owned by the flow lerps below while
                    // it runs); the DOME deflates DURING the womb-drain phase instead of popping
                    // here - unless there is no flow to animate it.
                    _cumShot = 0; _burst = 0f; _cumPhase = 0;
                    if (_flowState == 0) _domeTarget = 0f;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: pull-out — cum reset (shots cleared; dome " + (_flowState != 0 ? "deflates with the womb drain" : "cleared") + ").");
                }
            }
            _prevPullOut = pullOut;

            // ── PULL-OUT LEAK CHOREOGRAPHY (by design), synced to the GAME's own drip anim ──
            // 1 PRE-FILL : the womb drains A LITTLE; the canal fills TOP-DOWN (the slab grows down from
            //              the cervix: _FillAmount2 pinned high, _FillBottom2 descends - the sliders
            //              built for exactly this).
            // 2 WAIT     : hold, until the game plays its own cum-drip animation (motion state 'Drop' -
            //              read from HFlag, the same variable family as everything else). Max 4s: the
            //              timeout log doubles as the survey of WHICH poses have no drip anim.
            // 3 WOMB     : game leak running -> drain the womb the rest of the way; the DOME deflates
            //              along with it; the canal stays full.
            // 4 CANAL    : womb empty -> the canal drains FROM THE TOP (level descends, bottom fixed).
            float fill = baseFill, fill2 = 0f, bottom2 = 0f;
            if (_flowState != 0)
            {
                _flowT += Time.deltaTime;
                const float D1 = 1.2f, WaitMax = 4f, D3 = 2.5f, D4 = 2.0f;
                const float CanalTop = 0.95f;      // canal slab's fixed top level while it holds
                const float PreDrain = 0.22f;      // fraction of the womb fill that pre-fills the canal
                if (_flowState == 1)
                {
                    float p = Mathf.Clamp01(_flowT / D1);
                    fill  = Mathf.Lerp(_flowStartFill, _flowStartFill * (1f - PreDrain), p);
                    fill2 = CanalTop;
                    bottom2 = Mathf.Lerp(CanalTop, 0f, p);           // slab grows DOWNWARD from the cervix
                    if (p >= 1f) { _flowState = 2; _flowT = 0f; }
                }
                else if (_flowState == 2)
                {
                    fill = _flowStartFill * (1f - PreDrain); fill2 = CanalTop; bottom2 = 0f;
                    bool gameLeak = MainGameWomb.HMotionKnown && MainGameWomb.HMotion.Contains("Drop");
                    if (gameLeak || _flowT >= WaitMax)
                    {
                        LiquidWobbleMPBPlugin._logger?.LogInfo(gameLeak
                            ? "CloXray: game drip ('Drop') detected " + _flowT.ToString("F1") + "s after pull-out — draining the womb in sync."
                            : "CloXray: NO game drip ('Drop') within " + WaitMax.ToString("F0") + "s after pull-out in [" + MainGameWomb.HAnimLabel + "] — draining anyway. (Pose without a drip anim? This log is the survey.)");
                        _flowState = 3; _flowT = 0f;
                    }
                }
                else if (_flowState == 3)
                {
                    float p = Mathf.Clamp01(_flowT / D3);
                    fill = Mathf.Lerp(_flowStartFill * (1f - PreDrain), 0f, p);   // womb empties into the canal/out
                    _domeTarget = _flowDomeStart * (1f - p);                       // creampie dome deflates with it
                    fill2 = CanalTop; bottom2 = 0f;
                    if (p >= 1f) { _flowState = 4; _flowT = 0f; _domeTarget = 0f; }
                }
                else // 4: canal drains FROM THE TOP (level descends; bottom stays at the opening)
                {
                    float p = Mathf.Clamp01(_flowT / D4);
                    fill = 0f;
                    fill2 = Mathf.Lerp(CanalTop, 0f, p);
                    bottom2 = 0f;
                    if (p >= 1f) { _flowState = 0; fill2 = 0f; bottom2 = 0f; }
                }
                if (Mathf.Abs(fill2 - _lastFill2) > 0.001f)   { _liquid.SetFloat("_FillAmount2", fill2); _lastFill2 = fill2; }
                if (Mathf.Abs(bottom2 - _lastBottom2) > 0.001f) { _liquid.SetFloat("_FillBottom2", bottom2); _lastBottom2 = bottom2; }
            }
            else if (_lastFill2 > 0f)   // flow just ended - make sure the canal is reset to empty
            {
                _liquid.SetFloat("_FillAmount2", 0f); _liquid.SetFloat("_FillBottom2", 0f);
                _lastFill2 = 0f; _lastBottom2 = 0f;
            }

            if (Mathf.Abs(fill - _lastFill) >= 0.001f) { _liquid.SetFloat("_FillAmount", fill); _lastFill = fill; }

            // H-LIQUID diagnostic (Debug Log, 2s): the fill-plane inputs vs the womb geometry, so a
            // "cum overflows to canal during stretch" report shows WHICH number breaks: the measured
            // chamber extents (MPB, live basis), the displace weight, or the fill value itself.
            if (LiquidWobbleMPBPlugin.CfgDebugLog && fill > 0.01f && Time.unscaledTime >= _liqDbgNext && _liquidRend != null && _fx != null)
            {
                _liqDbgNext = Time.unscaledTime + 2f;
                var mpb = new MaterialPropertyBlock();
                _liquidRend.GetPropertyBlock(mpb);
                Vector4 e1 = mpb.GetVector("_Chamber1ExtentY"), e2 = mpb.GetVector("_Chamber2ExtentY");
                float cervY = _fx.HasCanal ? (_fx.CanalEntranceWorld + _fx.CanalAxisWorld * _fx.CanalLenWorld).y : -999f;
                LiquidWobbleMPBPlugin._logger?.LogInfo("  H-LIQUID fill=" + fill.ToString("F2") + " fill2=" + _lastFill2.ToString("F2")
                    + " ext1=[" + e1.x.ToString("F3") + ".." + e1.y.ToString("F3") + " v=" + e1.w.ToString("F0") + "] ext2=[" + e2.x.ToString("F3") + ".." + e2.y.ToString("F3")
                    + "] displaceW=" + _fx.CurrentStretchWeight.ToString("F0") + " cervixY=" + cervY.ToString("F3")
                    + " entranceY=" + (_fx.HasCanal ? _fx.CanalEntranceWorld.y.ToString("F3") : "-"));
            }
        }

        private void WatchFinish()
        {
            try
            {
                if (_hflagType == null) _hflagType = Type.GetType("HFlag, Assembly-CSharp");
                if (_hflagType == null) return;
                if (_hflag == null)
                {
                    if (Time.unscaledTime < _hflagRetry) return;
                    _hflagRetry = Time.unscaledTime + 2f;
                    _hflag = UnityEngine.Object.FindObjectOfType(_hflagType);
                    if (_hflag == null) return;
                    _fClick = _hflagType.GetField("click");
                    // Bind the persistent inside-finish counter: HFlag.count.sonyuIn (name-discovered so a
                    // different game build logs what it actually has instead of failing silently).
                    _countObj = null; _fSonyuIn = null; _lastSonyuIn = -1;
                    var fCount = _hflagType.GetField("count");
                    if (fCount != null)
                    {
                        _countObj = fCount.GetValue(_hflag);
                        if (_countObj != null)
                        {
                            foreach (var fi in _countObj.GetType().GetFields())
                                if (fi.FieldType == typeof(int) && fi.Name.ToLowerInvariant().Contains("sonyuin")) { _fSonyuIn = fi; break; }
                            if (_fSonyuIn != null) _lastSonyuIn = (int)_fSonyuIn.GetValue(_countObj);
                        }
                    }
                    if (!_namesLogged)
                    {
                        _namesLogged = true;
                        if (_fClick != null) LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: HFlag.ClickKind values: " + string.Join(", ", Enum.GetNames(_fClick.FieldType)));
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: finish counter " + (_fSonyuIn != null ? "bound: count." + _fSonyuIn.Name + " (now " + _lastSonyuIn + ")" :
                            ("NOT bound — HFlag.count fields: " + (_countObj != null ? string.Join(", ", Array.ConvertAll(_countObj.GetType().GetFields(), f => f.Name)) : "no 'count' field"))));
                    }
                }

                // Counter watch (primary): fires reliably even if the click pulse is cleared mid-frame.
                if (_fSonyuIn != null && _countObj != null)
                {
                    int now = (int)_fSonyuIn.GetValue(_countObj);
                    if (now > _lastSonyuIn)
                    {
                        _lastSonyuIn = now;
                        QueueFinishBurst("counter #" + now);
                    }
                    else if (now < _lastSonyuIn) _lastSonyuIn = now;   // H restarted -> counter reset
                }

                // Click watch (secondary/diagnostic): logs transitions; also triggers if the counter isn't bound.
                if (_fClick == null) return;
                // PERF: compare the BOXED enum before stringifying. The old code ran .ToString() on the
                // enum EVERY frame (reflection name-lookup + a fresh string) just to discard it on the
                // no-change path - steady per-frame GC garbage for the whole H scene. Boxed Equals compares
                // value+type, so a change here is exactly a change in the click state; behaviour identical.
                object clickBox = _fClick.GetValue(_hflag);
                if (clickBox == null || clickBox.Equals(_lastClickBox)) return;
                _lastClickBox = clickBox;
                string click = clickBox.ToString();
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: HFlag.click -> " + click);
                if (_fSonyuIn == null)
                {
                    string lc = click.ToLowerInvariant();
                    if (lc == "inside" || (lc.Contains("inside") && !lc.Contains("anal")))
                        QueueFinishBurst("click");
                }
            }
            catch { }
        }
    }
}
