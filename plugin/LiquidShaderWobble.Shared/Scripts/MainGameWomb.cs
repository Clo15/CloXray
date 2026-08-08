using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LiquidWobbleMPB
{
    // ── Free-H / main-game womb (build 439) ───────────────────────────────────────────── In the MAIN GAME
    // (not Studio) the hotkey TOGGLES a womb on the female instead of the Studio apply.
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
                    // codebase), so type-existence is not a discriminator.
                    string pn = null;
                    try { pn = BepInEx.Paths.ProcessName; } catch { }
                    _isStudio = pn != null && pn.IndexOf("CharaStudio", StringComparison.OrdinalIgnoreCase) >= 0;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: process '" + pn + "' -> " + (_isStudio.Value ? "STUDIO" : "MAIN GAME") + " mode.");
                }
                return _isStudio.Value;
            }
        }

        // one womb per female (keyed by her ChaControl component).
        private static readonly Dictionary<Component, GameObject> _spawned = new Dictionary<Component, GameObject>();

        // POSE-CHANGE hook: bumped by a Harmony postfix on HSceneProc.ChangeAnimator.
        internal static string RespawnWhy = "forced uncensor body reload";
        internal static bool BpRefTargetMissing;   // BP DanAgent.m_referenceTarget is null — published where it is read
        internal static float RespawnAt;   // >0: respawn the H womb at this time (forced-uncensor body reload)
        internal static float ReloadPendingUntil;   // a forced uncensor reload is settling until this time
        internal static float DeferredSpawnAt;      // >0: spawn the H womb at this time (waiting on that reload)
        internal static float DeferredSpawnDeadline; // loud giving-up point if the reload never reports back

        // KKAPI's CharacterReloaded does NOT fire for UncensorSelector's ReloadCharacterBody.
        internal static Component DeferredSpawnFemale;
        internal static void NotifyReloadComplete() { }

        // A forced body uncensor drops MaterialEditor's edits on the old body mesh (its re-apply hooks are
        // KKAPI-level and none of them fire for UncensorSelector's partial reload). ME exposes
        // RefreshBodyEdits() - body/face only - but nothing calls it on KK/KKS. Run it once the reload has
        // settled so the character's own skin shader comes back.
        // EVENT-DRIVEN, not a timer: UncBodyReloadWatch is a Harmony postfix on the very method that
        // rebuilds the body, so Done() marks the exact frame the swap finished. A guessed delay would be
        // wrong on a slow load and wasteful on a fast one. The deadline is a LOUD giving-up point, not a
        // second path: if the reload never reports, we say so and repair nothing.
        internal static Component MeRefreshChara;
        internal static float MeRefreshDeadline;

        // The pose version our BP pin was last established for. BP resets m_innerTarget to its own
        // default on every pose change, so danEnd is not ours until we re-pin - see PinAgent.
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

        public static object CurrentAnimInfo;
        public static string CurrentAnimKey = "?";
        public static string VariantSuffix = "";
        private static readonly System.Collections.Generic.List<object> _varReg = new System.Collections.Generic.List<object>();
        private static string _varRegKey;   // field-identity of the bank _varReg belongs to
        public static string CurrentAnimKeyV { get { return (CurrentAnimKey ?? "?") + VariantSuffix; } }
        // AnimationListInfo separates them.
        public static string PendingBtnTag;
        public static string BtnTag(GameObject go)
        {
            if (go == null) return null;
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
            // Button has no visible text (blank label): fall back to the positional identity.
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
                string tag = PendingBtnTag; PendingBtnTag = null;   // b714: one-shot, consumed here
                string fkey = AnimUniqueKey(__0);
                if (!string.IsNullOrEmpty(tag))
                {
                    // A real menu-button click = a fresh pose selection, always.
                    CurrentAnimKey = fkey + " #" + tag;
                    _varReg.Clear(); _varReg.Add(__0); _varRegKey = fkey; VariantSuffix = "";
                }
                else if (_varReg.Count > 0 && fkey == _varRegKey)
                {
                    // CLICKLESS swap inside the same field-identity bank = the ALTERNATE control.
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
                    // transition).
                    string mtag = TagFromMenu(__0);
                    CurrentAnimKey = mtag != null ? (fkey + " #" + mtag) : fkey;
                    _varReg.Clear(); _varReg.Add(__0); _varRegKey = fkey; VariantSuffix = "";
                    if (LiquidWobbleMPBPlugin.CfgDebugLog)
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: CLICKLESS anim change -> game-internal, key='" + CurrentAnimKey + "'" + (mtag == null ? " (no menu button matched — field key only)" : " (name recovered from the menu)"));
                }
            }
            BumpPose("HSceneProc.ChangeAnimator");
        }
        // reference) and return its visible label.
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
        // clicked button's visible label.
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

#if CLOXRAY_RESEARCH   // 1.1 release strip: research scaffolding compiles only in research builds (add CLOXRAY_RESEARCH to DefineConstants)
        // ===== b692 RESEARCH AUTO-COLLECTOR (temporary; strip with the rest of the b688 scaffolding) =====
        // User: "switching poses and then 3 variations for each takes a lot of time — can you automate it?"
        // Walks HSceneProc.lstAnimInfo and drives ChangeAnimator (the SAME entry point the game's own pose
        // buttons use, and the one we already Harmony-hook), waiting for the settled sampler to emit a row
        // for each pose before advancing. NO FALLBACK: if the reflection surface isn't exactly what we
        // expect we LogError and stop rather than half-driving the H state machine.
        public static int ResearchRows;              // bumped by ResearchLog; the collector waits on it
        // b699: bumped when an observation COMPLETES, logged or not — the collector waits on THIS so a
        // static pose advances the sweep immediately instead of burning the 30s sample timeout.
        public static int ResearchEpisodes;
        private static bool _autoRun;
        // b715 — "I have no indicator when I'm ready to switch character — draw me a dot". Per-character
        // (identified by CANAL — the pose-stable key, NOT fKok) count of samples per animation key, so an
        // on-screen dot can show when the CURRENT character has enough repeats of every reachable pose.
        public static bool AutoCollectActive { get { return _autoRun; } }
        internal static readonly System.Collections.Generic.Dictionary<string, int> ResearchCounts
            = new System.Collections.Generic.Dictionary<string, int>();
        internal static int ResearchCanalMM;    // canal of the most recently sampled row = "current character"
        public const int ResearchTargetPerPose = 2;   // b716: 2 agreeing samples already confirm a (button-tagged) animation; 3 was too slow
        // Summary for the current character: distinct animations collected, how many hit the target, the
        // least-covered count (min), and the running pass number.
        public static void ResearchDot(out int canalMM, out int poses, out int ready, out int minCount, out int passNo)
        {
            canalMM = ResearchCanalMM; poses = 0; ready = 0; minCount = int.MaxValue; passNo = _autoPass;
            string pref = canalMM.ToString() + "|";
            foreach (var kv in ResearchCounts)
            {
                if (!kv.Key.StartsWith(pref, StringComparison.Ordinal)) continue;
                // b747 — "dot = TOTAL progress, green = finish": count ONLY loop-state keys — the
                // baseline's verification rule ignores transition motions (Insert/InsertIdle/...), so a
                // dot that counts them reports green while the real dataset is half-thin.
                int li = kv.Key.LastIndexOf('|');
                if (li < 0 || kv.Key.IndexOf("Loop", li, StringComparison.Ordinal) < 0) continue;
                poses++;
                if (kv.Value >= ResearchTargetPerPose) ready++;
                if (kv.Value < minCount) minCount = kv.Value;
            }
            if (poses == 0) minCount = 0;
        }
        private static int _autoPass;   // current sweep pass (for the dot)
        public static void ToggleAutoCollect(MonoBehaviour host)
        {
            if (IsStudio) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: auto-collect is MAIN GAME only."); return; }
            // b703: RE-ENABLED with the state I was missing. Decoding HSprite.OnChangePlaySelect (the real
            // menu button handler, found via an IL scan for writers of HFlag.set_selectAnimationListInfo)
            // shows a menu click sets TWO things:
            // flags.selectAnimationListInfo = info;
            // flags.voiceWait = true; // ldc.i4.1 + stfld at IL +196/+197
            // and HSceneProc.Update's guard branches on voiceWait. Setting only the selection sent Update
            // down the other path — which is why every attempt replayed the insert/idle sequence.
            if (_autoRun) { _autoRun = false; LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: auto-collect STOPPED by hotkey."); return; }
            if (host == null) return;
            _autoRun = true; host.StartCoroutine(AutoCollect());
        }
        // b746: rebuild the sweep's button lists from the LIVE menu. AnimationLoader DESTROYS and
        // re-instantiates every button on LoadMotionList, and clicking a destroyed button makes
        // OnChangePlaySelect a SILENT no-op (Unity fake-null swallows the GameObject inside the
        // handler and in our tag prefix) — the b745 mid-girl run "advanced" 15 poses while actually
        // re-sampling ONE pose 17 times. Same filter and the menu is name-sorted, so indices align;
        // a changed entry count means the menu genuinely changed shape and the caller must stop.
        private static bool ReEnumerateMenu(object sprite, Type tAIC, System.Reflection.FieldInfo fAicInfo,
            System.Reflection.FieldInfo fMode, object curMode,
            System.Collections.Generic.List<object> sweep, System.Collections.Generic.List<GameObject> sweepBtn)
        {
            var spriteComp = sprite as Component;
            if (spriteComp == null) return false;
            var s2 = new System.Collections.Generic.List<object>();
            var b2 = new System.Collections.Generic.List<GameObject>();
            foreach (var c in spriteComp.GetComponentsInChildren(tAIC, true))
            {
                if (c == null) continue;
                object info = null; try { info = fAicInfo.GetValue(c); } catch { }
                if (info == null) continue;
                object m = null; try { m = fMode.GetValue(info); } catch { }
                if (m == null || !m.Equals(curMode)) continue;
                s2.Add(info); b2.Add(((Component)c).gameObject);
            }
            if (s2.Count != sweep.Count) return false;
            sweep.Clear(); sweep.AddRange(s2);
            sweepBtn.Clear(); sweepBtn.AddRange(b2);
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-collect — menu buttons re-enumerated (" + s2.Count + " live entries).");
            return true;
        }
        private static IEnumerator AutoCollect()
        {
            var tHS = Type.GetType("HSceneProc, Assembly-CSharp");
            UnityEngine.Object hs = tHS != null ? UnityEngine.Object.FindObjectOfType(tHS) : null;
            if (hs == null)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — no live HSceneProc (start an H scene first). Doing nothing."); _autoRun = false; yield break; }
            // b696: prefer lstUseAnimInfo — the metadata shows BOTH are List<AnimationListInfo>[], but "Use"
            // is the subset actually available in the current H situation, whereas lstAnimInfo is the whole
            // 249-entry catalogue (mostly non-penetrating). Fall through to lstAnimInfo only if it's empty.
            var fList = tHS.GetField("lstUseAnimInfo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                     ?? tHS.GetField("lstAnimInfo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            // b700: the MENU never calls ChangeAnimator. IL analysis of HSceneProc.Update shows it reads
            // `HSceneProc.flags` -> `HFlag.get_selectAnimationListInfo` immediately before calling
            // ChangeAnimator itself — i.e. the UI merely SETS the selection and Update performs the change
            // WITH its own transition handling. Invoking ChangeAnimator directly bypassed that, which is why
            // every hop replayed the insert/dialogue sequence. So we set the same property and let the game
            // do the work. (selectAnimationListInfo is on HFlag, not HSceneProc — hence the earlier misses.)
            var fFlags = tHS.GetField("flags", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            object flags = fFlags != null ? fFlags.GetValue(hs) : null;
            var pSel = flags != null ? flags.GetType().GetProperty("selectAnimationListInfo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
            // b703: the menu also sets flags.voiceWait = true; Update's guard branches on it.
            var fVoiceWait = flags != null ? flags.GetType().GetField("voiceWait", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
            // b733: HFlag.click is the game's H command word — writing ClickKind.motionchange (=6 in both
            // KK and KKS metadata) cycles the motion pattern exactly like the user's right-click box
            // (b732 instrumentation: main->alternate->fast = WLoop->SLoop->OLoop, zero animInfo changes;
            // the game's own UI and SensibleH command the state machine through this same field).
            var fClick = flags != null ? flags.GetType().GetField("click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
            if (fClick == null) LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — HFlag.click not found; motion-pattern cycling disabled for this run (base collection unaffected).");
            if (fList == null || flags == null || pSel == null || fVoiceWait == null || !pSel.CanWrite || !pSel.CanRead)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — HSceneProc.flags selection state not resolvable (flags=" + (flags != null) + ", selectProp=" + (pSel != null) + ", voiceWait=" + (fVoiceWait != null) + "). Doing nothing (no fallback)."); _autoRun = false; yield break; }
            // b697 — "it switched to a non-penetrated dialogue state; through the menu I just go to the
            // next pose in the list"). lstUseAnimInfo is List<AnimationListInfo>[] — an array indexed by
            // CATEGORY. The game's menu cycles WITHIN the category you're in; jumping ACROSS categories is
            // what forced the re-entry (log: our cross-category change produced WLoop -> Insert -> InsertIdle
            // with d2 blowing out to 1052mm = the characters separating and replaying the insert sequence).
            // So keep the per-category structure and sweep ONLY the category the user is currently in.
            var cats = new System.Collections.Generic.List<System.Collections.Generic.List<object>>();
            var outer = fList.GetValue(hs) as System.Collections.IEnumerable;
            if (outer != null)
                foreach (var lvl1 in outer)
                {
                    var one = new System.Collections.Generic.List<object>();
                    var inner = lvl1 as System.Collections.IEnumerable;
                    if (inner != null && !(lvl1 is string)) { foreach (var it in inner) if (it != null) one.Add(it); }
                    else if (lvl1 != null) one.Add(lvl1);
                    cats.Add(one);
                }
            var flat = new System.Collections.Generic.List<object>();
            for (int c = 0; c < cats.Count; c++) flat.AddRange(cats[c]);
            // b693: the real signature is ChangeAnimator(AnimationListInfo _nextAinmInfo, bool _isForceCameraReset)
            // (read from Assembly-CSharp metadata). Bind ADAPTIVELY — locate the pose parameter by type and
            // default the rest — so a differing KKS overload still works instead of hard-failing on arity.
            if (flat.Count == 0)
            {   // lstUseAnimInfo was present but empty -> take the full catalogue instead
                var fAll = tHS.GetField("lstAnimInfo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var outer2 = fAll != null ? fAll.GetValue(hs) as System.Collections.IEnumerable : null;
                if (outer2 != null)
                    foreach (var lvl1 in outer2)
                    {
                        var inner = lvl1 as System.Collections.IEnumerable;
                        if (inner != null && !(lvl1 is string)) { foreach (var it in inner) if (it != null) flat.Add(it); }
                        else if (lvl1 != null) flat.Add(lvl1);
                    }
            }
            // b700: no ChangeAnimator arity/parameter binding needed any more — we assign the selection
            // property and HSceneProc.Update drives the actual change.
            if (flat.Count == 0)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — pose list is empty. Doing nothing."); _autoRun = false; yield break; }
            // b695 begin at the pose AFTER the one they already selected instead of restarting at
            // index 0 — they pick a PENETRATING starting point and the sweep continues from there, wrapping
            // once to cover the rest. Sweeping from 0 just walked standing/sitting/etc, which never penetrate.
            // b701: KK_HSceneOptions reads `HFlag.nowAnimationInfo` for the CURRENT pose (a field on HFlag —
            // which is why every lookup on HSceneProc came back empty) and `HFlag.selectAnimationListInfo`
            // for the pending selection. Use nowAnimationInfo as the authoritative "where am I", falling back
            // to the selection property, then to our own hook capture.
            object cur = null;
            var fNow = flags.GetType().GetField("nowAnimationInfo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (fNow != null) { try { cur = fNow.GetValue(flags); } catch { } }
            if (cur == null) { try { cur = pSel.GetValue(flags, null); } catch { } }
            if (cur == null) cur = CurrentAnimInfo;   // b696 hook capture as last resort
            if (cur == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — current pose unknown (hook has not seen a ChangeAnimator yet); switch pose once manually, then retry. Doing nothing.");
                _autoRun = false; yield break;
            }
            // b698: `AnimationListInfo.mode` (EMode) IS the H mode — caress / service / INSERTION / etc.
            // b697's category scoping was a GUESS and still crossed modes; switching to a pose of a DIFFERENT
            // mode is precisely what drops out of penetration into the insert/dialogue sequence the user keeps
            // landing in. Filter the sweep to poses sharing the CURRENT pose's mode so every hop stays the
            // same kind of act — which is also what the game's own menu is showing you.
            var fMode = cur.GetType().GetField("mode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            object curMode = fMode != null ? fMode.GetValue(cur) : null;
            if (curMode == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — AnimationListInfo.mode unreadable; refusing to sweep across H modes (that is what caused the dialogue drop). Doing nothing.");
                _autoRun = false; yield break;
            }
            // b699 — "we only need insertion poses": HFlag.EMode — aibu=0, houshi=1, SONYU=2,
            // masturbation=3, peeping=4, lesbian=5, houshi3P=6, sonyu3P=7, houshi3PMMF=8, sonyu3PMMF=9.
            // Only the sonyu* modes are penetrative. Refuse to start from anything else rather than sweep
            // poses that can't produce a womb sample at all.
            int modeVal; try { modeVal = Convert.ToInt32(curMode); } catch { modeVal = -1; }
            if (modeVal != 2 && modeVal != 7 && modeVal != 9)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — current mode is " + curMode
                    + " which is NOT an insertion mode (need sonyu/sonyu3P/sonyu3PMMF). Switch to an INSERTION pose first. Doing nothing.");
                _autoRun = false; yield break;
            }
            // b706 click plumbing: HSceneProc.sprite -> HSprite.OnChangePlaySelect(GameObject), plus the
            // AnimationInfoComponent that tags each menu button with its AnimationListInfo.
            var fSprite = tHS.GetField("sprite", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            object sprite = fSprite != null ? fSprite.GetValue(hs) : null;
            var mClick = sprite != null ? sprite.GetType().GetMethod("OnChangePlaySelect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
            // b708: it is a NESTED type — metadata says `HSprite+AnimationInfoComponent` (no namespace),
            // so the plain name could never resolve and the sweep refused before doing anything.
            var tAIC = Type.GetType("HSprite+AnimationInfoComponent, Assembly-CSharp")
                    ?? Type.GetType("AnimationInfoComponent, Assembly-CSharp");
            var fAicInfo = tAIC != null ? tAIC.GetField("info", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
            if (sprite == null || mClick == null || tAIC == null || fAicInfo == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — cannot reach the menu click path (sprite=" + (sprite != null)
                    + ", OnChangePlaySelect=" + (mClick != null) + ", AnimationInfoComponent=" + (tAIC != null) + "). Doing nothing (no fallback).");
                _autoRun = false; yield break;
            }
            // b707 — "Standing is not part of the insertion menu — why did you switch to it?".
            // Correct, and that was the real defect: the sweep was built from `lstUseAnimInfo`, an INTERNAL
            // catalogue, not from the menu. So it visited poses the UI never offers — exactly the ones a
            // human could never pick. Build the sweep from the MENU'S OWN BUTTONS instead: every entry the
            // player can click carries an AnimationInfoComponent, so the button set IS the menu. Anything not
            // in it is unreachable by definition and must not be visited.
            var sweep = new System.Collections.Generic.List<object>();
            var sweepBtn = new System.Collections.Generic.List<GameObject>();
            {
                var spriteComp = sprite as Component;
                var uiComps = spriteComp != null ? spriteComp.GetComponentsInChildren(tAIC, true) : null;
                if (uiComps != null)
                    foreach (var c in uiComps)
                    {
                        if (c == null) continue;
                        object info = null; try { info = fAicInfo.GetValue(c); } catch { }
                        if (info == null) continue;
                        object m = null; try { m = fMode.GetValue(info); } catch { }
                        if (m == null || !m.Equals(curMode)) continue;              // insertion-mode only
                        // No Selectable check here (that would need the UI assembly reference):
                        // OnChangePlaySelect already tests Selectable.interactable and no-ops on a
                        // greyed-out entry, so an unavailable pose simply won't switch.
                        sweep.Add(info); sweepBtn.Add(c.gameObject);
                    }
            }
            // b708 PROOF: print the menu exactly as the sweep sees it, so it can be compared against the
            // on-screen list. If these are the poses the player can click, the candidate set is correct.
            {
                var fIdDbg = cur.GetType().GetField("id", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var fNameA = cur.GetType().GetField("nameAnimation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < sweep.Count; i++)
                {
                    int sid = -1; string snm = "?";
                    try { if (fIdDbg != null) sid = Convert.ToInt32(fIdDbg.GetValue(sweep[i])); } catch { }
                    try { if (fNameA != null) snm = (fNameA.GetValue(sweep[i]) ?? "?").ToString(); } catch { }
                    // b719: show the detected VISIBLE menu name beside the internal id/name — instant
                    // verification that name detection works and every entry is unique.
                    sb.Append('\n').Append("    [").Append(i).Append("] id=").Append(sid).Append(" '").Append(snm)
                      .Append("'  ").Append(BtnTag(sweepBtn[i]) ?? "?");
                }
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: auto-collect — MENU as seen by the sweep (mode=" + curMode + ", " + sweep.Count + " clickable entries):" + sb);
            }
            if (sweep.Count == 0)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — the menu exposes no clickable poses of mode " + curMode
                    + " (found 0 AnimationInfoComponent buttons). Doing nothing.");
                _autoRun = false; yield break;
            }
            var fId = cur.GetType().GetField("id", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            int curId = -1;
            if (fId != null) { try { curId = Convert.ToInt32(fId.GetValue(cur)); } catch { } }
            int startIdx = -1;
            for (int i = 0; i < sweep.Count; i++) if (ReferenceEquals(sweep[i], cur)) { startIdx = i; break; }
            if (startIdx < 0 && curId >= 0)   // b705: fall back to identity by id (instance may differ/lag)
                for (int i = 0; i < sweep.Count; i++)
                { int sid = -2; try { sid = Convert.ToInt32(fId.GetValue(sweep[i])); } catch { } if (sid == curId) { startIdx = i; break; } }
            if (startIdx < 0)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — current pose not found among the " + sweep.Count + " poses of its own mode (" + curMode + "). Doing nothing.");
                _autoRun = false; yield break;
            }
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: AUTO-COLLECT started at the pose AFTER your selection — mode=" + curMode
                + ", pose " + (startIdx + 1) + " of " + sweep.Count + " sharing that mode (staying in-mode; changing MODE is what drops to the insert/dialogue sequence). Hotkey again to stop.");
            // b710: KEEP CYCLING until stopped. Analysis of the first full pass: a single observation is
            // NOT reproducible because the staging drifts within a pose (same pose+char+motion, full 11s
            // settled windows, yet deepY swung -37.9..+48.3mm and MIN 23..110mm). But the MEDIAN converges —
            // over 12 Doggy samples the half-split medians differed by 4.7mm vs an 86.7mm individual spread,
            // while 4 samples still differed by 19mm. So MIN/RANGE are DISTRIBUTIONS needing ~10 repeats per
            // pose, not one. A single pass left most poses at n=1, which is unusable.
            int done = 0, skipped = 0, pass = 0;
            GameObject lastClickedBtn = null;   // b728: button-identity skip guard
            // b746 — "detect after what scene the penetration breaks and skip that scene": pose
            // indices that SWAPPED IN fine but never reached penetration (even after the b735 INSERT
            // command) are remembered and skipped on every later pass of this run.
            var deadPoses = new System.Collections.Generic.HashSet<int>();
            // b730 POST-MORTEM: the same-button re-click does NOT toggle variants — AnimationLoader's
            // OnChangePlaySelect transpiler NOPs the same-id early-return, so the click re-runs the SAME
            // AnimationListInfo (visible pose restart, no key change; the user watched every pose play
            // twice). The sub-loop is removed; auto-variants return once the b731 click instrumentation
            // names the real ALTERNATE control (one manual right-click shows it in the log).
            while (_autoRun)
            {
            pass++; _autoPass = pass;
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: auto-collect — PASS " + pass + " over " + sweep.Count
                + " poses (each pass adds ONE sample per pose; ~10 passes gives a stable median). Hotkey to stop.");
            for (int n = 1; n <= sweep.Count && _autoRun; n++)
            {
                int i = (startIdx + n) % sweep.Count;   // continue from your pose, wrap within the category
                if (sweep[i] == null) { skipped++; continue; }
                if (deadPoses.Contains(i)) { skipped++; continue; }   // b746: known non-penetrating pose
                // b705 (THE bug was caught late): never select the pose that is ALREADY PLAYING. The game
                // re-ENTERS it from the top and drops to Idle/non-penetrated — exactly the "dialogue state".
                // Log proof: "H animInfo swap -> id=7 name='Standing'" while already on Standing, followed by
                // "H motion 'Idle' -> penetrated=False". The user's manual switches always went to a DIFFERENT
                // pose, which is why theirs never re-entered. nowAnimationInfo can also lag by one, so compare
                // by AnimationListInfo.id against the LIVE current pose each iteration, not by reference.
                // b728: the b712 key-compare guard ALIASED — ~20 same-key menu entries all matched the
                // live pose's key, so whole passes span in "already playing" skips (the stall the user hit).
                // The sweep itself knows exactly which BUTTON it clicked last — the only identity that never
                // aliases. Key-compare survives only pre-first-click, and only when the key maps to a single
                // candidate (else it would alias again; one re-entry hop is the lesser evil).
                if (lastClickedBtn != null)
                {
                    if (ReferenceEquals(sweepBtn[i], lastClickedBtn)) { skipped++; continue; }
                }
                else
                {
                    object liveNow = null;
                    try { if (fNow != null) liveNow = fNow.GetValue(flags); } catch { }
                    if (liveNow == null) { try { liveNow = pSel.GetValue(flags, null); } catch { } }
                    if (liveNow != null)
                    {
                        string liveKey = AnimUniqueKey(liveNow);
                        string candKey = AnimUniqueKey(sweep[i]);
                        int aliases = 0;
                        for (int q = 0; q < sweep.Count; q++) if (AnimUniqueKey(sweep[q]) == liveKey) aliases++;
                        if (liveKey != "?" && candKey == liveKey && aliases <= 1) { skipped++; continue; }
                    }
                }
                int before = ResearchEpisodes;   // b699: completes on static poses too
                bool ok = true;
                // b700: set the selection exactly as the menu does; HSceneProc.Update picks it up and runs
                // the transition itself (no insert/dialogue re-entry).
                // b707: we already hold THIS entry's real menu button — click it directly.
                GameObject btn = sweepBtn[i];
                // b746: destroyed button (menu rebuilt since enumeration) -> refresh the lists first.
                if (btn == null)
                {
                    if (!ReEnumerateMenu(sprite, tAIC, fAicInfo, fMode, curMode, sweep, sweepBtn))
                    { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-collect — menu changed shape mid-run; stopping this pass (restart the sweep)."); break; }
                    btn = sweepBtn[i];
                }
                PendingBtnTag = BtnTag(btn);   // b714: identify THIS entry, so variants stop merging
                lastClickedBtn = btn;
                int pvClick = PoseVersion;
                try { mClick.Invoke(sprite, new object[] { btn }); }
                catch (Exception e) { ok = false; LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: auto-collect — OnChangePlaySelect failed on pose " + i + ": " + e.Message); }
                if (!ok) { skipped++; continue; }
                // b746: VERIFY the click actually reached the game — every real selection fires
                // ChangeAnimator (PoseVersion bumps, even on a same-pose re-entry). No bump in 2s =
                // the silent dead-button no-op -> re-enumerate the live menu and retry this index
                // once; still silent -> loud skip (no fallback).
                float swapBy = Time.unscaledTime + 2f;
                while (_autoRun && PoseVersion == pvClick && Time.unscaledTime < swapBy) yield return null;
                if (PoseVersion == pvClick && _autoRun)
                {
                    if (ReEnumerateMenu(sprite, tAIC, fAicInfo, fMode, curMode, sweep, sweepBtn))
                    {
                        btn = sweepBtn[i];
                        PendingBtnTag = BtnTag(btn); lastClickedBtn = btn;
                        try { mClick.Invoke(sprite, new object[] { btn }); } catch { }
                        swapBy = Time.unscaledTime + 2f;
                        while (_autoRun && PoseVersion == pvClick && Time.unscaledTime < swapBy) yield return null;
                    }
                    if (PoseVersion == pvClick)
                    { skipped++; LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: auto-collect — pose " + i + " button is DEAD even after re-enumeration; skipping."); continue; }
                }
                // b693: 249 poses include many NON-PENETRATING ones (oral/touch/etc). Waiting the full
                // sample timeout on each would make a sweep take hours, so first give it a short window to
                // reach penetration and skip immediately if it never does.
                float penBy = Time.unscaledTime + 8f;
                while (_autoRun && !HPenetrated && Time.unscaledTime < penBy) yield return null;
                // b735 SELF-RE-INSERT: some menu entries drop the game to Idle/unpenetrated on selection
                // (b705 re-entry behavior — live proof 2026-07-26: a same-key 'Doggy' entry landed in
                // "H motion 'Idle' -> penetrated=False" and the next 31 poses all skipped over ~4.5min,
                // because pose clicks alone never re-insert; the game waits for its INSERT command).
                // Recover exactly like the user does: command the game's own insert click
                // (HFlag.click = ClickKind.insert = 1 — same command-word interface as b733's
                // motionchange) and give it one more penetration window. One attempt per pose, verdict
                // logged; still unpenetrated -> skip as before (no fallback chain).
                if (!HPenetrated && fClick != null && _autoRun)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-collect — pose " + i + " sits unpenetrated; commanding the game's INSERT click.");
                    bool insOk = true;
                    try { fClick.SetValue(flags, Enum.ToObject(fClick.FieldType, 1)); }   // ClickKind.insert
                    catch (Exception ie) { insOk = false; LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: auto-collect — insert command failed: " + ie.Message); }
                    if (insOk)
                    {
                        float insBy = Time.unscaledTime + 8f;
                        while (_autoRun && !HPenetrated && Time.unscaledTime < insBy) yield return null;
                        if (HPenetrated) LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-collect — INSERT recovered penetration; resuming collection.");
                        else LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-collect — INSERT did not take on pose " + i + " (non-penetrating pose or wrong state); skipping.");
                    }
                }
                if (!HPenetrated)
                {
                    skipped++;
                    deadPoses.Add(i);   // b746: swapped in but never penetrates — skip it on later passes
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-collect — pose " + i + " never penetrates (INSERT included); added to this run's skip list.");
                    continue;
                }
                // penetrating -> wait for the settled sampler (3s settle + 8s observation) to emit its row
                float until = Time.unscaledTime + 30f;
                while (_autoRun && ResearchEpisodes == before && Time.unscaledTime < until) yield return null;
                if (ResearchEpisodes > before) done++;
                else { skipped++; LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-collect — pose " + i + " penetrated but produced no settled sample in 30s; moving on."); }
                // ── b733 AUTO-LOOP PATTERNS — the SOLVED "alternates". The user's right-click box cycles
                // the MOTION LOOP of the same animation; the sweep now does it itself: after the base
                // sample, command a pattern change and let the settled sampler (rkey already carries
                // |motion) record each loop; the b732 loop-keyed fit re-measures automatically.
                // Deterministic verdicts, no fallback: no loop change in 4s = single-pattern pose;
                // return to the first loop = cycle closed.
                if (fClick != null && ResearchEpisodes > before && HPenetrated)
                {
                    string firstLoop = null, curLoop = null;
                    { var m0 = HMotion; if (m0 != null && m0.IndexOf("Loop", StringComparison.Ordinal) >= 0) { firstLoop = m0; curLoop = m0; } }
                    for (int lc = 0; lc < 3 && _autoRun && firstLoop != null; lc++)
                    {
                        string loopBefore = curLoop;
                        int epBefore = ResearchEpisodes;
                        try { fClick.SetValue(flags, Enum.ToObject(fClick.FieldType, 6)); }   // ClickKind.motionchange
                        catch (Exception ce)
                        {
                            LiquidWobbleMPBPlugin._logger?.LogError("CloXray: auto-loop — cannot write HFlag.click (" + ce.Message + "); pattern cycling OFF.");
                            fClick = null; break;
                        }
                        float lEnd = Time.unscaledTime + 4f;
                        while (_autoRun && Time.unscaledTime < lEnd)
                        {
                            var mL = HMotion;
                            if (mL != null && mL.IndexOf("Loop", StringComparison.Ordinal) >= 0) curLoop = mL;
                            if (curLoop != loopBefore) break;
                            yield return null;
                        }
                        if (curLoop == loopBefore)
                        { LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-loop — no further motion patterns on this pose (motionchange had no effect)."); break; }
                        if (curLoop == firstLoop)
                        { LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-loop — pattern cycle closed on pose " + i + " (" + lc + " extra pattern(s) sampled)."); break; }
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-loop — pattern '" + curLoop + "' engaged on pose " + i + "; sampling.");
                        float lUntil = Time.unscaledTime + 30f;
                        while (_autoRun && ResearchEpisodes == epBefore && Time.unscaledTime < lUntil) yield return null;
                        if (ResearchEpisodes > epBefore) done++;
                        else { LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-loop — pattern '" + curLoop + "' gave no settled sample in 30s; moving on."); break; }
                    }
                }
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: auto-collect progress " + n + "/" + sweep.Count + " (pose #" + (i + 1) + " in mode " + curMode + ", sampled " + done + ", skipped " + skipped + ").");
            }
            }   // b710: end of one pass — loop again until the hotkey stops us
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: AUTO-COLLECT stopped after " + pass + " pass(es) — sampled " + done
                + ", skipped " + skipped + ". Each pose now has roughly " + pass + " sample(s); the median needs ~10. "
                + "Then switch to your OTHER character (no restart needed) and run the SAME poses again.");
            _autoRun = false;
        }
#endif   // CLOXRAY_RESEARCH

        // KKS RIG MATCH (b523, hard-wired — user: "copy the scaling from KK so the visual appearance
        // is the same", no sliders): the KK female pelvis chain carries ~0.828 bone scale (measured
        // kokanScale=0.828 on the reference body) which the KKS rig does NOT (1.0) — the same womb
        // rendered ~21% bigger vs the girl in KKS (canal 94mm vs KK ~74-78: weaker dome push, wider
        // look, larger canal-vs-shaft divergence arm). ONE source of truth: every H womb-scale
        // consumer (the bone mirror AND the canal-length math) reads this — never CfgHWombScale
        // directly — so the mesh and the math can never disagree.
        // b524: rig-match corrected by MEASUREMENT — the KKS rig is NOT scale-1.0: her pelvis reads
        // 0.917 (log) vs KK's 0.828, so the conversion is 0.828/0.917 = 0.903 (b523's 0.828 over-
        // shrank ~9%). MIRROR-ONLY: the canal-length formula keeps multiplying the plain config value
        // (its 1.15 double-count exists identically in KK and every KK-approved calibration absorbed
        // it — the rig-match reaches the canal number through the mirrored bones' lossyScale, giving
        // numeric parity with KK: same visual womb -> same canal number -> same normalized math).
        // b563: the KKS womb now SCALES WITH THE FEMALE'S BODY, matching KK's behaviour. KK mirrors
        // HER bone scales, so its womb inherits her pelvis size (canal-bone lossy = her kokan 0.888 in
        // the log) — a bigger female automatically gets a bigger womb. b538 replaced her scales with
        // fixed AUTHORED scales for bind-consistent skinning, which broke that female-tracking (KKS
        // canal-bone lossy was a fixed 1.150). This restores it WITHOUT losing the skinning fix: the
        // root scale injection is now CfgHWombScale × (her kokan lossy / KokRef), so any female sizes
        // her womb from her own pelvis. KokRef = 1.019 makes this female (kokan 0.917) reproduce the
        // approved ~86mm (= the old ×0.90). KK keeps its implicit her-scale path.
        // b564 sub-linear womb scaling — small females get a proportionately BIGGER womb —
        // organs don't scale 1:1 with body size, and a fixed-size penis fits a too-tiny womb badly).
        // effKok pulls the female's pelvis scale toward the reference: Blend 1 = womb scales 1:1 with
        // her (pure b563), 0 = fixed size, 0.6 = womb tracks 60% of her size change (petite → womb
        // shrinks only 60% as much). Applied in BOTH games so they behave identically.
        public const float KokStd = 0.888f;       // reference female pelvis (default KK) where the womb size is calibrated
        public const float KokRefKKS = 1.006f;    // KKS authored-scale baseline (gives the approved ~86mm at the KKS ref female)
        // b678 — "womb on small char looked too big, reduce a little": 0.6 gave a small female
        // (kokan 0.598) a +19% womb boost (effKok/kok=1.19). 0.75 trims that to +12% — the womb is ~6%
        // smaller on petite chars while the reference (0.888) is unchanged by construction and larger
        // females barely move. Ovaries ride the same bake scale so they shrink with it (plus the extra
        // ovary_shrink baked below for their disproportionate size).
        public static float WombScaleBlend = 0.75f;

        public static float HMirrorWombScaleFor(Transform pivot)
        {
            float kok = pivot != null ? pivot.lossyScale.y : KokStd;
            float effKok = Mathf.Lerp(KokStd, kok, WombScaleBlend);   // sub-linear pull toward the reference size
            // b577: UNIFIED both games — the mirror uses HER local scales (KKS no longer uses authored
            // scales, b538 reverted for the KKS-follow experiment), so the womb scales ∝ kok and we
            // counter with effKok/kok for the sub-linear target. At kok == KokStd this is exactly 1.0.
            return LiquidWobbleMPBPlugin.CfgHWombScale * (effKok / kok);
        }

        // (`m_danPenetration = (topStick && ref) || motion.Contains("IN")`; false -> AdjustDanToTargetNull =
        // the dan RESETS and the pin is ignored).
        private static bool _danMotionHookTried;
        private static string _motionPatchLogged;

        public static void InstallDanMotionHook()
        {
            if (_danMotionHookTried || IsStudio) return;
            _danMotionHookTried = true;
            try
            {
                // loaded BP assembly (main + studio variants).
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
                // dan look-at while still penetrated -> BP's reference target stays null ->
                // AdjustDanToTargetNull = dan reset, pin dropped ("after cumming the penis stopped following the canal").
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

        // (the ALTERNATIVE-variant button).
        private static float _lastPoseBump = -10f;
        public static void BumpPose(string why)
        {
            if (Time.unscaledTime - _lastPoseBump < 0.5f) return;
            _lastPoseBump = Time.unscaledTime;
            _poseVersion++;
            // b884: a reporter has the penis rendering correctly on apply and going wrong on a pose
            // change, and nothing re-checks the x-ray here. Dump the material chain once the pose has
            // settled so the log holds the last good chain and the first bad one.
            ChainDumpAt = Time.unscaledTime + 2f;
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: H animation changed via " + why + " (pose v" + _poseVersion + ") — penis fit will re-measure one stroke, then lock.");
        }

        public static string HAnimLabel = "?";

        // Live H motion state (fed by WombBoneMirror.FeedPenetrationState from HFlag).
        public static string HMotion = "";

        public const float CanalExtendDownMM = 6f;
#if KKS
        // b726 KKS SEAT RECALIBRATION (tuned by eye + the b720 vulva probe, on the FORCED BP body — the old
        // value 4 was tuned on the pre-BP body mesh). Probe: KKS delta(ent−vulvaRoot)=(−0.2,+4.8,+3.8)mm vs
        // KK's approved (0,+6.1,+2.2). Adjusted "slightly down and 2mm to back": z lands at +1.8
        // ≈ KK's +2.2; expected post-fix probe ≈ (0, ~3.8, +1.8).
        // Absorbs the KK<->KKS difference so the shared 'Womb offset to back' can read as KK's value.
        // KKS = setting(2) - (-2) = 4mm; KK = the setting itself. Each game seated by eye on its
        // own meshes. Change the setting by N and this constant by N together, or KKS moves too.
        public const float SeatForwardMM = -2f;
        public const float SeatAlongCanalMM = -1f;      // NEW: 1mm DOWN along the canal (KKS body sits the vulva differently)
#else
        public const float SeatForwardMM = 0f;
        public const float SeatAlongCanalMM = 0f;       // KK = the reference; no along-canal seat correction
#endif
        public static bool HPenetrated;
        public static bool HMotionKnown;
        public static float HMotionChangedAt;

        public static bool UseRebind = true;

        // trusting that the womb can be placed/sized deliberately, prove.
        public static Vector3 RebindNudgeMM = Vector3.zero;
        public static float RebindScaleMul = 1f;
        public static bool CurrentlyRebound;          // ACTUAL live state (UseRebind is the user's intent;
                                                      // during auto-parity phase A the womb runs MIRROR)
        private static Vector3 _autoTargetLocal;      // MIRROR entrance, her-kokan local (idle-averaged)
        private static bool _autoHaveTarget;
        private static int _autoIter;                 // verify/top-up passes used (cap 2)
        private static bool _autoImmediate;           // respawn issued by the loop: rebind at spawn, skip phase A
        private static Component _nudgeFemale;        // nudge/target belong to THIS female; reset on change
        // b621 MANUAL mode: a hotkey nudge/scale press disarms auto-parity (they were fighting — the
        // user's +30mm jump was instantly "re-pinned" by the loop re-running on the respawn). Manual
        // spawns rebind immediately with the commanded values and run NO loop. Cleared by Shift+Alt+G
        // and by toggling the placement mode, which re-arms the auto loop.
        // b626 SNAP: once the loop VERIFIES a girl's correction, later REBIND toggles for the SAME girl
        // bake instantly with the learned value — no loop, no blinks (proven cacheable: a repeat run
        // verified with 0 top-ups). Invalidated by: switching girls, any manual nudge/scale command, or
        // Shift+Alt+G (= explicit re-learn). Session-only; card-persistent caching is later engineering.
        public static bool NudgeVerified;
        public static Vector3 PredictedNudgeMM; public static bool PredictedValid;
        public static bool AutoTargetKnown { get { return _autoHaveTarget; } }
        public static Vector3 AutoTargetLocal { get { return _autoTargetLocal; } }
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

            // PHASE 2 — reproduce the mirror's SCALE and SEAT OFFSET. Native skinning gives no per-bone
            // hook, so bake them into the mesh instead. Multiplying the transform into the BINDPOSES
            // applies it to the rest vertices AND to every blendshape delta consistently (deltas are
            // added to rest BEFORE skinning), so the rings still open by the correct absolute amount at
            // any womb size. Bake-once at spawn — zero per-frame cost.
            // Mirror parity: it scales bone positions about HER KOKAN by s and translates every bone by
            // -herKokan.forward * back, so we use exactly the same pivot, scalar and offset.
            // b601, MEASURED: the un-baked rebind womb is NOT uniformly mis-sized. Skinning from her bones
            // compresses it ALONG THE CANAL (~0.75x on a small female, kokanLossy 0.598) while leaving the
            // GIRTH already correct (~1.00x) — verified identical at standing and cowgirl, so it is a fixed
            // anisotropy, not a pose-dependent bindpose mismatch. A uniform s therefore fixed the length
            // (0.75 x 1.374 = 1.03) and over-inflated the girth (1.00 x 1.374 = 1.37), which also skewed
            // openEff and hence the ring drive. So scale by s ONLY along the canal axis, girth untouched.
            float s = HMirrorWombScaleFor(herKokan) * RebindScaleMul;   // b609 commanded scale multiplier
            float backMM = LiquidWobbleMPBPlugin.CfgHWombBack - SeatForwardMM;
            Vector3 pivotW = herKokan.position;
            Vector3 offsetW = Mathf.Abs(backMM) > 0.01f ? -herKokan.forward * (backMM * 0.001f) : Vector3.zero;
            var canalBone = FindDeep(womb.transform, "clo_canal_entry");
            if (canalBone == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: REBIND FAILED — no clo_canal_entry on the womb. Applying NOTHING (no fallback).");
                return false;
            }
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
                // The ONE lookup b901 missed: the rootBone is a WOMB bone (clo_cf_j_root since 7.4.0)
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
                    // NO SILENT SKIP: if the bake can't be applied the womb renders at the wrong size.
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
                Matrix4x4 boneToMesh = bp[kIdx].inverse;
                // pivot = her kokan origin, which is the bone-local origin.
                Vector3 pivotM = boneToMesh.MultiplyPoint3x4(Vector3.zero);
                // seat offset: -forward in bone-local (Transform.forward is local +Z), converted from a
                // world millimetre distance into bone-local units via her (pose-invariant) lossy scale.
                Vector3 nudgeLocal = RebindNudgeMM * (0.001f / herLossy);
                Vector3 offsetM = boneToMesh.MultiplyVector(Vector3.back * (backMM * 0.001f / herLossy)
                                                          + Vector3.up * (SeatAlongCanalMM * 0.001f / herLossy)
                                                          + nudgeLocal);
                // canal axis = the bone's authored local +Y, expressed in the parent (kokan) frame
                Vector3 axisM = boneToMesh.MultiplyVector(canalLocalRot * Vector3.up).normalized;   // kept for the parity term
                // b633 (user, ARCHITECTURAL): NO matrix-space STRETCH — ever. Deformation belongs to the
                // AUTHORED blendshapes only (womb_displace = elongation, rings = opening); an
                // anisotropic matrix stretch distorts everything indiscriminately (the "stretched
                // ovaries"). What the bake DOES carry, besides seat+nudge placement, is the UNIFORM SIZE
                // scale s (womb-scale config × sub-linear female term): proportional, distortion-free —
                // the rebind equivalent of base-bone scaling (her bones are shared with her body and
                // cannot be scaled). Canal opening self-adapts regardless of size (girthScale =
                // penisDia × margin / measured openEff), and the depth math self-measures the real mesh
                // (bakedCanal) — so uniform size is consistent by construction. Mirror-length-parity as
                // a goal is retired with the anisotropy.
                Matrix4x4 S = Matrix4x4.identity;
                S.m00 = s; S.m11 = s; S.m22 = s;
                Vector3 tM = pivotM - S.MultiplyPoint3x4(pivotM) + offsetM;
                Matrix4x4 M = S;
                M.m03 = tM.x; M.m13 = tM.y; M.m23 = tM.z;

                // b627 PREDICTION SUPPORT: stash everything the analytic dual-chain predictor needs —
                // the ORIGINAL bindposes (pre-M) and the nudge-FREE bake matrix M0. The predictor
                // (WombExpandEffect.PredictParity) then computes mirror-chain vs rebind-chain entrance
                // positions from the same live bones and derives the nudge as their difference — the
                // "formula for any character" (mechanical simulation, nothing fitted).
                RebindOrigBindposes = bp;   // pre-instance copy (mesh.bindposes returned a fresh array)
                {
                    Vector3 tM0 = pivotM - S.MultiplyPoint3x4(pivotM) + boneToMesh.MultiplyVector(Vector3.back * (backMM * 0.001f / herLossy));
                    Matrix4x4 M0 = S;
                    M0.m03 = tM0.x; M0.m13 = tM0.y; M0.m23 = tM0.z;
                    RebindM0 = M0;
                }
                RebindPivotBone = herKokan; RebindS = s; RebindSeatBackMM = backMM;
                // INSTANCE the mesh first - bindposes live on the asset, and the shared one is used by every
                // other womb and by Studio.
                var inst = UnityEngine.Object.Instantiate(mesh);
                inst.name = mesh.name + "_CloXrayRebind";
                var nbp = inst.bindposes;
                for (int ri = 0; ri < redir.Count; ri++) nbp[redir[ri]] = bp[kIdx];
                for (int i = 0; i < nbp.Length; i++) nbp[i] = nbp[i] * M;
                inst.bindposes = nbp;
                smr.sharedMesh = inst;
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

            // The measurement leaves ride the CANAL frame: clo_canal_entry is the anchor and penis_target2.
            canalBone.SetParent(herKokan, true);

            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: REBIND (route B) OK — " + meshes + " mesh(es), " + remapped
                + " bones re-pointed at the wearer (non-core -> kokan-rigid), " + kept + " item-only bones kept, " + baked + " mesh(es) baked. "
                + "clo_canal_entry (+penis_target2) reparented under her cf_j_kokan. Per-frame mirror is OFF for this womb. "
                + "PHASE 2 bake: scale s=" + s.ToString("F3") + " about her kokan, seat back=" + backMM.ToString("F1") + "mm"
                + " | COMMANDED nudge=" + RebindNudgeMM.ToString("F1") + "mm (her frame: x=right y=alongCanal z=forward) scaleMul="
                + RebindScaleMul.ToString("F3") + ".");
            return true;
        }

        private static Transform FindHerKokan(Transform wearer)
        {
            foreach (var t in wearer.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == "cf_j_kokan") return t;   // first match — same rule as Bind/rebind
            return null;
        }

        // Idle-averaged entrance sample in HER kokan-local frame.
        private static IEnumerator SampleEntrance(WombExpandEffect fx, Transform herKok, float seconds, Vector3[] buf)
        {
            Vector3 sum = Vector3.zero; int n = 0;
            float until = Time.unscaledTime + seconds;
            while (Time.unscaledTime < until)
            {
                if (fx == null || herKok == null) yield break;
                sum += herKok.InverseTransformPoint(fx.CanalEntranceW);
                n++;
                yield return null;
            }
            if (n > 0) { buf[0] = sum / n; buf[1] = Vector3.one; }
        }

        private static void AutoAbort(MonoBehaviour host, string why, Vector3 nudge0)
        {
            LiquidWobbleMPBPlugin._logger?.LogError("CloXray: AUTO-PARITY ABORTED — " + why
                + " Nudge restored to pre-loop " + nudge0.ToString("F1") + "mm. Toggle Shift+Alt+B while she is IDLE (no pose change, no penetration) to re-run.");
            RebindNudgeMM = nudge0;
            _autoHaveTarget = false;   // also makes the respawn's AutoVerify exit immediately (bounded)
            if (!HPenetrated && AnySpawned()) { _autoImmediate = true; ToggleWhy = "auto-parity"; Toggle(host); ToggleWhy = "auto-parity"; Toggle(host); }
        }

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
                ShowWomb(womb);   // b672: never leave the hidden first womb invisible
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
            RebindNudgeMM = nudge; NudgeVerified = true; _nudgeFemale = female;   // cache -> repeat spawns snap
            ToggleWhy = "nudge-bake"; Toggle(host); ToggleWhy = "nudge-bake"; Toggle(host);   // one respawn -> NudgeVerified snap path bakes the predicted nudge (no loop)
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: PLACED via analytic prediction (one-shot, no loop) — nudge " + RebindNudgeMM.ToString("F1") + "mm, cached for this girl.");
        }

        private static IEnumerator AutoParity(MonoBehaviour host, Component female, GameObject womb, WombBoneMirror mirror)
        {
            RebindNudgeMM = new Vector3(0f, RebindNudgeMM.y, RebindNudgeMM.z);
            WombExpandEffect fx = null;
            float deadline = Time.unscaledTime + 75f;   // b624: phase-locked calibration = up to 2 idle loops (~20-30s)
            while (Time.unscaledTime < deadline)
            {
                if (womb == null || female == null) yield break;
                fx = womb.GetComponentInChildren<WombExpandEffect>(true);
                if (fx != null && fx.CanalReady && fx.CanalCalibrated) break;   // b618: wait for the AVERAGED calibration
                yield return null;
            }
            if (fx == null || !fx.CanalReady || !fx.CanalCalibrated)
            {
                // FELL THROUGH silently and the loop measured an uncalibrated bone.
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: AUTO-PARITY: canal never became ready+calibrated within the deadline (ready="
                    + (fx != null && fx.CanalReady) + " calibrated=" + (fx != null && fx.CanalCalibrated)
                    + ") — womb STAYS IN MIRROR (no rebind, no fallback).");
                yield break;
            }
            Transform herKok = FindHerKokan(female.transform);
            if (herKok == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: AUTO-PARITY: wearer has no cf_j_kokan — womb STAYS IN MIRROR.");
                yield break;
            }
            int pv0 = PoseVersion; Vector3 nudge0 = RebindNudgeMM;   // b634: POSE snapshot (b625 watched motion STRINGS and aborted on benign idle micro-states like M_Touch — the loop could never finish)
            yield return new WaitForSecondsRealtime(1.0f);   // let the calibration settle
            var buf = new Vector3[2];
            yield return host.StartCoroutine(SampleEntrance(fx, herKok, 1.5f, buf));
            if (buf[1] == Vector3.zero || womb == null) yield break;
            if (HPenetrated || PoseVersion != pv0)
            {
                AutoAbort(host, "the POSE changed (or penetration started) while measuring the MIRROR target (poseVer " + pv0 + " -> " + PoseVersion + ", pen=" + HPenetrated + ").", nudge0);
                yield break;
            }
            float targetDistMM = Vector3.Scale(buf[0], herKok.lossyScale).magnitude * 1000f;
            if (targetDistMM > 120f)
            {
                AutoAbort(host, "MIRROR target implausible (" + targetDistMM.ToString("F0") + "mm from her pelvis — measured mid-transition).", nudge0);
                yield break;
            }
            _autoTargetLocal = buf[0]; _autoHaveTarget = true;

            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: AUTO-PARITY: MIRROR target locked (her-kokan local "
                + (_autoTargetLocal * 1000f).ToString("F1") + "mm) — switching to a fresh REBIND spawn.");
            _autoIter = 0;
            _autoImmediate = true;
            ToggleWhy = "rebind-respawn"; Toggle(host);   // remove the mirror womb
            ToggleWhy = "rebind-respawn"; Toggle(host);   // fresh spawn -> immediate rebind (current nudge) -> AutoVerify measures + corrects
        }

        private static IEnumerator AutoVerify(MonoBehaviour host, Component female, GameObject womb)
        {
            if (!_autoHaveTarget) yield break;
            WombExpandEffect fx = null;
            float deadline = Time.unscaledTime + 75f;   // b624: phase-locked calibration = up to 2 idle loops (~20-30s)
            while (Time.unscaledTime < deadline)
            {
                if (womb == null || female == null) yield break;
                fx = womb.GetComponentInChildren<WombExpandEffect>(true);
                if (fx != null && fx.CanalReady && fx.CanalCalibrated) break;   // b618: wait for the AVERAGED calibration
                yield return null;
            }
            if (fx == null || !fx.CanalReady || !fx.CanalCalibrated)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: AUTO-PARITY VERIFY: canal never became ready+calibrated (ready="
                    + (fx != null && fx.CanalReady) + " calibrated=" + (fx != null && fx.CanalCalibrated)
                    + ") — cannot verify; keeping the current bake UNVERIFIED.");
                yield break;
            }
            Transform herKok = FindHerKokan(female.transform);
            if (herKok == null) yield break;
            int pv0 = PoseVersion; Vector3 nudge0 = RebindNudgeMM;   // b634: POSE snapshot (see AutoParity)
            yield return new WaitForSecondsRealtime(1.0f);
            var buf = new Vector3[2];
            yield return host.StartCoroutine(SampleEntrance(fx, herKok, 1.5f, buf));
            if (buf[1] == Vector3.zero || womb == null) yield break;
            if (HPenetrated || PoseVersion != pv0)
            {
                AutoAbort(host, "the POSE changed (or penetration started) while verifying (poseVer " + pv0 + " -> " + PoseVersion + ", pen=" + HPenetrated + ").", nudge0);
                yield break;
            }

            Vector3 residMM = Vector3.Scale(_autoTargetLocal - buf[0], herKok.lossyScale) * 1000f;
            residMM.x = 0f;
            bool parityOk = Mathf.Abs(residMM.y) <= 2.5f && Mathf.Abs(residMM.z) <= 7f;
            if (parityOk)
            {
                NudgeVerified = true;   // b626: later toggles for this girl SNAP with this correction
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: AUTO-PARITY VERIFIED — REBIND on the MIRROR target: y="
                    + residMM.y.ToString("F2") + "mm z=" + residMM.z.ToString("F2")
                    + "mm (x not judged: lateral sway; parity naturally ~0.1mm). Total nudge " + RebindNudgeMM.ToString("F1")
                    + "mm world. Cached — this girl's REBIND now snaps instantly.");
                yield break;
            }
            if (_autoIter >= 3)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: AUTO-PARITY did NOT converge after 3 top-ups — residual "
                    + residMM.ToString("F1") + "mm. Keeping the last bake; send this log.");
                yield break;
            }
            if (residMM.magnitude > 50f)
            {
                AutoAbort(host, "residual " + residMM.ToString("F0") + "mm is not a parity delta (state changed mid-loop).", nudge0);
                yield break;
            }
            _autoIter++;
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: AUTO-PARITY top-up #" + _autoIter + " — residual " + residMM.ToString("F1") + "mm, re-baking.");
            RebindNudgeMM += residMM;
            _autoImmediate = true;
            Toggle(host);
            Toggle(host);
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
            // prune stale entries (H scene ended, character destroyed).
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
                // after the map entry is gone: RemoveForWomb asks AnySpawned whether any womb is still set
                // up before it touches the male, and this one must not count itself.
                try { AutoBodyReveal.RemoveForWomb(female); }
                catch (Exception rex) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: x-ray cleanup threw on womb removal — continuing: " + rex.Message); }
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: womb removed from '" + female.name + "'.");
                return;
            }

            // BP UNCENSOR first.
            if (!IsStudio)
            {
                ReloadPendingUntil = 0f;
                // HER BODY first, ALONE.
                TryForceBPUncensor(female, 2);
                if (ReloadPendingUntil <= Time.unscaledTime)
                {
                    Component male0 = FindNearestMaleWithPenis(female.transform.position, 2f, female);
                    if (male0 != null) { TryForceBPUncensor(male0, 0); TryForceBPUncensor(male0, 1); }
                }
                if (ReloadPendingUntil > Time.unscaledTime)
                {
                    // Far out on purpose: NotifyReloadComplete pulls it in the moment KKAPI reports the
                    // reload finished.
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
            if (FindDeep(go.transform, "penis_target2") == null)
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb has NO penis_target2 — [Clo]XrayWomb1.zipmod does not match this DLL. Update the zipmod; the penis aim will NOT be steered.");
            var mirror = go.AddComponent<WombBoneMirror>();
            mirror.Bind(female.transform);
            mirror.SyncNow();          // position the skeleton BEFORE anything measures it
            // b605 CONTROL: force updateWhenOffscreen on for BOTH placement paths. With Unity's default
            // (false) a renderer that is off-screen skips its skinning/bounds update, so BakeMesh-derived
            // measurements — and the canal calibration that MOVES/ROTATES the canal bone from them — can
            // read stale data depending on the camera view. b599 set this only in the rebind path, which
            // made the two modes measure under different conditions; that asymmetry is now removed.
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r != null) r.updateWhenOffscreen = true;
            // ROUTE-B: hand placement over to Unity's skinning.
            CurrentlyRebound = false;
            if (UseRebind)
            {
                if (!ReferenceEquals(female, _nudgeFemale))
                {   // different girl: her correction is not this girl's — restart the universal loop
                    RebindNudgeMM = Vector3.zero; _autoHaveTarget = false; _autoIter = 0; _autoImmediate = false;
                    NudgeVerified = false;
                    _nudgeFemale = female;
                }
                if (_autoImmediate)
                {
                    _autoImmediate = false;
                    if (RebindWombToWearer(go, female.transform)) { mirror.Rebound = true; CurrentlyRebound = true; host.StartCoroutine(AutoVerify(host, female, go)); }
                    else LiquidWobbleMPBPlugin._logger?.LogError("CloXray: AUTO-PARITY respawn rebind FAILED — womb is NOT driven. Toggle back to MIRROR (Shift+Alt+B).");
                }
                else if (NudgeVerified)
                {   // b626 SNAP: this girl's correction is already VERIFIED — bake instantly, no loop
                    if (RebindWombToWearer(go, female.transform))
                    {
                        mirror.Rebound = true; CurrentlyRebound = true;
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: REBIND snap — using this character's VERIFIED correction ("
                            + RebindNudgeMM.ToString("F1") + "mm world). Shift+Alt+G to re-learn.");
                    }
                    else LiquidWobbleMPBPlugin._logger?.LogError("CloXray: REBIND snap rebind FAILED — womb is NOT driven.");
                }
                else
                {   // b665: fresh girl — rebind IMMEDIATELY (nudge 0), then let the analytic predictor
                    // place it in ONE respawn (no mirror-first, no measure loop, no 4× respawn churn).
                    if (RebindWombToWearer(go, female.transform))
                    {
                        mirror.Rebound = true; CurrentlyRebound = true;
                        host.StartCoroutine(AutoPlacePredicted(host, female, go));
                    }
                    else LiquidWobbleMPBPlugin._logger?.LogError("CloXray: fresh rebind FAILED — womb is NOT driven. Toggle to MIRROR (Shift+Alt+B).");
                }
            }
            _spawned[female] = go;
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: womb spawned on '" + female.name + "'.");

            // The baked bootstrap component attaches the wobble + womb effects in its Start; give it a
            // couple of frames, then apply the x-ray stack DIRECTLY to the known wearer.
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
                    // character the same log shows the womb placing on, so the naive test was wrong about
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
                        // The ownership column stays as EVIDENCE - it is what caught the b913
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
                // sitting on the character (the b913 incident) made it exclude HER whole skeleton.
                Component cc = null;
                for (var c = t; c != null; c = c.parent) { cc = c.GetComponent("ChaControl"); if (cc != null) break; }
                if (cc == null || !IsFemale(cc)) continue;
                float d = (t.position - eye).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = cc; }
            }
            return best;
        }

        // Nearest MALE character carrying a penis material, within maxRange of a world point.
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

        // MAIN GAME aiming. The k_f_dan markers are declared but never READ by main-game BP (verified in its
        // source).
        private static object[] _bpSimpEntries;
        private static bool[] _bpSimpSaved;
        private static bool _bpSimpForced;

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
                // Empty lists = H tearing down / not yet initialized: report alive so the watchdog doesn't
                // thrash re-attaches against a scene that has no agents to bind.
                if (!anyC || !anyD) return true;
                return hasC && hasD;
            }
            catch { return true; }
        }

        public static void ReattachPenisAim()
        {
            if (_sWomb != null && _sFemale != null) AttachPenisAim(_sWomb, _sMale, _sFemale);
            if (!IsStudio && HPenetrated && BpRefTargetMissing && _sMale != null && _sFemale != null)
            {
                bool ok = BPBridge.RebindCollisionAgent(_sMale, _sFemale, true, false);
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP had NO collision target while penetrated — re-bound '"
                    + _sMale.name + "' to '" + _sFemale.name + "': " + (ok ? "OK" : "FAILED"));
            }
            if (_sWomb != null && _sMale != null) AutoBodyReveal.ReapplyMainGamePenisXray(_sWomb, _sMale);
            // HER body reload wipes the body/veil/clothes copies the same way.
            if (_sWomb != null && _sFemale != null) AutoBodyReveal.ReapplyMainGameBodyXray(_sWomb, _sFemale);
        }

        private static readonly HashSet<string> _uncForced = new HashSet<string>();
        private static readonly HashSet<int> _vagBonesOk = new HashSet<int>();
        private static readonly HashSet<int> _vagWarned = new HashSet<int>();
        private static readonly string[] _uncDictName  = { "PenisDictionary", "BallsDictionary", "BodyDictionary" };
        private static readonly string[] _uncGuidProp  = { "PenisGUID", "BallsGUID", "BodyGUID" };
        // which uncensor is ACTUALLY in use (UncensorSelector's own Studio menu reads it the same way).
        private static readonly string[] _uncDataProp  = { "PenisData", "BallsData", "BodyData" };
        private static readonly string[] _uncCfgName   = { "DefaultMalePenis", "DefaultMaleBalls", "DefaultFemaleBody" };
        private static readonly string[] _uncPartLabel = { "penis", "balls", "body" };
        // selection).
        private static readonly object[] _uncEntry = new object[3];
        private static readonly string[] _uncSaved = new string[3];
        private static readonly bool[] _uncCfgForced = new bool[3];

        private static object FindUncConfigEntry(System.Reflection.Assembly asm, string name)
        {
            const System.Reflection.BindingFlags BS = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (var t in asm.GetTypes())
            {
                var p = t.GetProperty(name, BS); if (p != null) { var v = p.GetValue(null, null); if (v != null) return v; }
                var f = t.GetField(name, BS); if (f != null) { var v = f.GetValue(null); if (v != null) return v; }
            }
            return null;
        }

        // Studio character replacement: the swapped-in card may not carry a BP-compatible body, and without
        // cf_J_Vagina* bones there is nothing for the penis-entry constraint to attach.
        internal static void ForceBPBodyAgain(Component cha)
        {
            if (cha == null) return;
            _uncForced.Remove(cha.GetInstanceID() + ":2");
            TryForceBPUncensor(cha, 2);
        }
        // A female body uncensor on a male reloads his body as a female one and his penis comes back as
        // the vanilla mosaic - and if the scene is saved in that state, it stays that way. The womb-
        // proximity search can reach a male standing against the wearer, so this guard is what keeps a
        // body-slot force strictly on the character it was meant for.
        // ── WOMB BONE NAMES ────────────────────────────────────────────────────────────────────────
        // The womb item's own bones carry a `clo_` prefix (womb build 7.4.0+). They used to be exact
        // copies of HER bone names, and ABMX indexes bones by BARE NAME with first-match-wins while
        // excluding only accessories and other characters - so a womb PARENTED under a character in
        // Studio could win her names, and her ABMX modifiers were then silently never applied. Her
        // bones just sat at default.
        //
        // Two rules for every bone lookup in this plugin, because the same literal used to mean both:
        // - a bone of HERS -> plain name, no prefix (wearer/hers/theirs lookups)
        // - a bone of the WOMB -> WombBone(...) or one of the constants below
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

                // installed uncensors: the plugin's static dictionary (key = GUID, value has DisplayName).
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

                // Candidates = display names containing "BP" (BP's uncensors.
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
                // Log every candidate: the zipmod FILE names ("[KKS][Balls][BP] Color Match.zipmod") are not
                // what UncensorSelector reports as the display name, so picking by guessed substrings silently lands on the wrong variant (the white-balls report).
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: " + part + " uncensor candidates: "
                    + string.Join(" | ", all.ToArray()) + "  -> picked '" + (bestName ?? "none") + "'.");
                if (bestName == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no installed " + part + " uncensor with 'BP' in its name — install BetterPenetration's uncensor zipmods. Installed: " + string.Join(" | ", all.ToArray()));
                    return;
                }

                // The GLOBAL UncensorSelector defaults are NOT touched.
                var pGuid = tc.GetProperty(_uncGuidProp[kind], BI);
                string cur = pGuid != null ? pGuid.GetValue(ctl, null) as string : null;

                // ALREADY BP -> DO NOTHING (design rule 2026-08-01: "if F already has BP = no need").
                // The reload this used to run is never free: UncensorSelector's body/penis/balls reload
                // is a PARTIAL reload, and MaterialEditor has no handler for it (its re-apply hooks are
                // KKAPI-level - ClothesStateChange / CoordinateChanged / ChangeCustomClothes / ChangeHair /
                // AccessoryTransferred - and the patches calling RefreshBodyEdits are #if PH). So every
                // swap silently drops the character's own material edits: a KKUTS skin comes back as
                // vanilla Main_Skin. Doing that to a character who ALREADY has a BP uncensor buys nothing,
                // and picking "our" BP variant over the one they chose is worse than leaving it alone.
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
                // Belt and braces for her body: the BP vagina bones ARE the thing we need, so if they are
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
                // ReloadCharacterBody(); ReloadCharacterPenis(); ReloadCharacterBalls(); UpdateSkin();
                // The ReloadCharacter* methods are PRIVATE and only swap meshes - calling them directly
                // (as this did) skipped UpdateSkin(), so a rebuilt mesh came back with no texture: the
                // balls rendered WHITE (o_dan_f / cm_m_dan_f, tex=NULL). It is also asynchronous, which is
                // why an immediate check after it looked like "the body did not reload" and led to the
                // private calls in the first place. Let the coroutine do the whole job; the spawn already
                // waits on BpBodyReady() for it to finish.
                // RELOAD ONLY THE SLOT WE CHANGED.
                // UncensorSelector source: UpdateUncensor() => StartCoroutine(ReloadCharacterUncensor()),
                // and that coroutine runs
                // if (ExType == 0) ReloadCharacterBody(); ReloadCharacterPenis(); ReloadCharacterBalls(); UpdateSkin();
                // back-to-back with NO yields between them - all four are plain private void methods. So
                // the male path below is exactly what the coroutine does minus the body reload, which he
                // never needed: we only ever set his penis/balls GUID (a body uncensor is refused above),
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
                        // returns — and Arm() clears the done flag, which would swallow the very event we
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

        // Gives a swapped-in card the scene's body uncensor the way UncensorSelector itself expects.
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
        // UncensorSelector's own Studio dropdown does.
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

        // Applies a specific body uncensor to ONE character and reloads that mesh.
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
            try
            {
                int fid = female.GetInstanceID();
                if (!_vagBonesOk.Contains(fid))
                {
                    bool hasVag = false;
                    foreach (var t721 in female.GetComponentsInChildren<Transform>(true))
                        if (t721 != null && t721.name.StartsWith("cf_J_Vagina", StringComparison.OrdinalIgnoreCase)) { hasVag = true; break; }
                    if (hasVag) _vagBonesOk.Add(fid);   // satisfied — never scan this character again
                    else if (_vagWarned.Add(fid))
                    {
                        LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + female.name + "' has NO cf_J_Vagina* bones — her body uncensor is not BP-compatible (BP can only aim at the game's kokan; no BP vagina colliders, and the womb entrance cannot be verified against the vulva). Forcing a BP body uncensor now (womb spawn = consent).");
                        TryForceBPUncensor(female, 2);
                    }
                }
            }
            catch (Exception e721) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP-uncensor bone check failed: " + e721.Message); }
            if (!LiquidWobbleMPBPlugin.CfgHPinEnable)
            {
                var old = w.gameObject.GetComponent<BPInnerTargetPin>();
                if (old != null) UnityEngine.Object.Destroy(old);   // OnDestroy restores BP's original target
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: penis pin disabled by config — BP keeps its own depth (womb chases with displace).");
                return;
            }
            // The CANAL-FRAME aim bone (penis_target2).
            Transform target = FindDeep(w.transform, "penis_target2");
            if (target == null && w.transform.parent != null) target = FindDeep(w.transform.parent, "penis_target2");
            if (target == null)
            {
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
                if (bpAsm != null) ForceBPSimplifyConfig(bpAsm);   // fix Simplify at BP's SOURCE (user's cfg has it Disabled)
                if (fAgents == null) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: BetterPenetration not found (no 'collisionAgents') — the Free-H womb HARD-DEPENDS on BP. Install + enable BetterPenetration."); return; }
                _sfAgents = fAgents; _sfDans = fDans; _sWomb = w; _sMale = male; _sFemale = female;
                // lookup).

                // Female agent -> pin m_innerTarget to the penis_target (component keeps it pinned +
                // restores).
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
                    // Back axis: the WOMB's mirrored kokan (always the animated pelvis) — position-
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

                // Male dan options -> simplifyVaginal, so the constraint PINS at the inner limit instead of
                // the body-collider walk (which ignores the inner target entirely).
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
                            // NATURAL penis length from BP.
                            var fLen = td.GetField("m_baseDanLength", BF);
                            if (fLen != null && pin != null)
                            {
                                float bl = 0f;
                                try { bl = (float)fLen.GetValue(d); } catch { }
                                if (bl > 0.02f) { pin.SetNaturalLength(bl); pin.SetLengthControl(d, fLen); }
                            }
                            else LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP danOptions/simplifyVaginal not found (danOptions=" + (opt != null) + ") — pin may be ignored on the body-collider path.");
                            // Stroke drive: the pin modulates this DanOptions' squishThreshold per frame so
                            // the visual tip actually travels with the thrust (see BPInnerTargetPin).
                            if (opt != null && pin != null) pin.SetStrokeControl(opt);
                            // BP HEALTH: BP.SetDanTarget only DRIVES the penis if m_danPointsFound (the male
                            // has a BP-compatible uncensor) AND the female's m_collisionPointsFound.
                            bool danFound = false, colFound = false, bpFound = false;
                            try { var f = td.GetField("m_danPointsFound", BF); if (f != null) danFound = (bool)f.GetValue(d); } catch { }
                            try { var f = td.GetField("m_bpDanPointsFound", BF); if (f != null) bpFound = (bool)f.GetValue(d); } catch { }
                            try { var f = agent.GetType().GetField("m_collisionPointsFound", BF); if (f != null) colFound = (bool)f.GetValue(agent); } catch { }
                            // m_bpDanPointsFound (>2 dan points = a FULL BP uncensor) is the real gate.
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
                            // FEMALE side: no m_bpKokanTarget = her body uncensor lacks BP's kokan bones.
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

        // ── BP DanOptions harvest/apply ────────────────────────────────────────────── Studio and main-game
        // BP share an identical DanOptions layout (12 primitive fields), so a name=value string round-trips them.
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
                // ChaControl.sex is a field in some game builds and a property in others.
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

    // Keeps the female BP agent's m_innerTarget pinned to the womb's penis_target.
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
        // One-time pose fit state (b492): 0 = prime pending, 1 = measuring one stroke, 2 = locked.
        private int _fitState;
        private float _measT, _measMin = float.MaxValue, _measMax;
        private Vector3 _deepVec;   // b688 research: base->canal vector in HIS base frame at the deepest sample
#if CLOXRAY_RESEARCH   // 1.1 release strip: research scaffolding compiles only in research builds (add CLOXRAY_RESEARCH to DefineConstants)
        // b690 RESEARCH SAMPLER state. The gameplay fit locks FAST (~2.2s after a pose change) which is right
        // for gameplay but caught the pose MID-SETTLE: identical conditions (same pose+motion+scales) produced
        // 10x different MIN, and deepVec showed the staging genuinely differed -> the old research rows were
        // recording WHEN we measured, not WHO the character is. So research now observes on a SETTLED window
        // spanning several cycles and emits ONE clean row per (pose,motion) episode.
        private float _rsT, _rsSettle, _rsMin = float.MaxValue, _rsMax;
        // b729 — "faster collection — few seconds per pose, I'll raise the stroke speed": the window
        // ends EARLY once it has what it exists to capture — >=3 full stroke cycles (6 direction reversals
        // of d2, 1.5mm hysteresis so jitter can't fake one) AND >=1s since the last min/max improvement
        // (extremes converged). Fast game speed => ~2.5-4s windows; slow strokes still get the full 8s cap.
        private float _rsPrevD2 = -1f, _rsImpT; private int _rsDir, _rsRevs;
        private int _rsFrame0; private float _rsReal0;   // b727: frame-progress stamps at window open
#endif   // CLOXRAY_RESEARCH
        // (b709's focus-event discard RETIRED: measured 2026-07-26 — this setup runs in background, the
        // game does NOT pause on alt-tab, so focus changes were discarding VALID windows. Frame progress
        // is the assumption-free test: a genuinely paused/stalled window shows ~0 fps and is discarded;
        // an alt-tabbed-but-running one passes.)
        // b649: how much of _danLen is the 0.65x-natural FLOOR's addition over the anatomical fit.
        // On a SMALL character the floor forces a penis longer than pose+canal can absorb (log: fit
        // wanted ~68mm on a 59mm canal, floor held 115mm) — that surplus is NOT compression the womb
        // should feel; it pinned stretchW at ~100 while the penis was visibly only half-in.
        private float _fitSurplus;
        private int _seenPose = -1;
        private string _measMotion = "";   // the motion state the current measure window belongs to
        private float _dbgNext;  // diagnostics timer
        // b680 — "penis reduces its size after a stroke — predict it BEFORE, small correction": the pin
        // is recreated per pairing, so _fitState resets and every pose PRIMEs at natural 178 -> the ~38mm
        // settle jump. But the locked size clusters tightly PER CHARACTER (~140mm here across most poses), so
        // the last fit predicts the next. STATIC caches survive pin recreation: an exact per-pose+canal answer
        // (repeat pose = zero jump), else the last MOTION-locked length (same-char cluster = small correction).
        private static float s_lastFitLen;
        private static readonly System.Collections.Generic.Dictionary<string, float> s_poseFit
            = new System.Collections.Generic.Dictionary<string, float>();
        private static string s_lastLoop = "WLoop";
        private static string FitKey(float canalM)
            => MainGameWomb.CurrentAnimKeyV + "|" + s_lastLoop + "@" + Mathf.RoundToInt(canalM * 1000f).ToString()
             + "m" + Mathf.RoundToInt(NaturalDanLen * 200f).ToString();  // b744: + male identity (5mm natural-length buckets) — a male swap must never reuse the previous male's locked fit

        private struct AnimSample
        {
            public float FKok;    // HER pelvis (cf_j_kokan) lossyScale.y <- current predictor
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
        private static string AnimBaselineFile()
        {
            try { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(MainGameWomb).Assembly.Location), "Clo.CloXray.animfit.baseline.txt"); }
            catch { return null; }
        }
        // OVERWRITES a baseline sample at the same size bucket.
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
                {   // b684 full candidate row
                    if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out smp.FKok)) continue;
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out smp.FStat);
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out smp.MScale);
                    float.TryParse(parts[3], System.Globalization.NumberStyles.Float, inv, out smp.MDan);
                    if (!float.TryParse(parts[4], System.Globalization.NumberStyles.Float, inv, out smp.Min)) continue;
                    if (!float.TryParse(parts[5], System.Globalization.NumberStyles.Float, inv, out smp.Range)) continue;
                }
                else if (parts.Length == 3)
                {   // b682 legacy row (scale,min,range)
                    if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out smp.FKok)) continue;
                    if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out smp.Min)) continue;
                    if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out smp.Range)) continue;
                }
                else continue;
                string pose = line.Substring(0, i);
                // stroke ~30mm apart.
                int lp = pose.LastIndexOf('|');
                if (lp < 0 || pose.IndexOf("Loop", lp, StringComparison.Ordinal) < 0) continue;
                System.Collections.Generic.List<AnimSample> lst;
                if (!s_animProf.TryGetValue(pose, out lst)) { lst = new System.Collections.Generic.List<AnimSample>(); s_animProf[pose] = lst; }
                int at = -1;
                for (int k = 0; k < lst.Count; k++)
                    if (Mathf.Abs(lst[k].FKok - smp.FKok) < 0.02f && Mathf.Abs(lst[k].MScale - smp.MScale) < 0.05f) { at = k; break; }
                if (at >= 0) { if (replaceExisting) lst[at] = smp; }   // same size bucket: user wins, baseline defers
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
                int b = MergeProfFile(AnimBaselineFile(), false);   // b718: shipped seed first (never overrides the user)
                int u = MergeProfFile(AnimProfFile(), true);        // user's live-learned samples win at their own sizes
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
#if CLOXRAY_RESEARCH   // 1.1 release strip: research scaffolding compiles only in research builds (add CLOXRAY_RESEARCH to DefineConstants)
        // b688 (user data-collection campaign: "collect extra data for this testing period; we keep only what's
        // useful once we understand and have proof of the full mechanic"). Rich APPEND-ONLY research log, kept
        // SEPARATE from the compact prediction table so the prediction path is untouched (zero risk) and the
        // surplus columns can simply be deleted later. One row per MOTION lock; append = no rewrite cost.
        // MOTION STATE is included deliberately: KK runs speed variants (S/M/W-Loop) under the SAME pose id,
        // and if those have different stroke lengths they would be silently averaged together in the table —
        // a prime suspect for "something more complex going on" than pure character size.
        private const string ResearchHeader = "pose,motion,fKok,fStat,mScale,mDan,canal,MIN,RANGE,deepX,deepY,deepZ,natural,fitted,settleS,bRoot,bHeight,bHips,bWaist,bThigh,bHead";
        private static void ResearchLog(string pose, string motion, float fKok, float fStat, float mScale,
                                        float mDan, float canal, float mn, float rg, Vector3 deepVec,
                                        float natural, float fitted, float settleS)
        {
            try
            {
                string p = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "Clo.CloXray.animresearch.csv");
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder();
                // b694: ONE canonical header + SCHEMA ROTATION. The old code wrote a header only when the file
                // was ABSENT, so when the columns changed (b690 settleS, b691 bone probe) an existing file kept
                // a STALE header while the rows changed underneath it — silently mislabelled data. Now, if the
                // existing header differs from the current one, that file is rotated aside and we start clean:
                // two schemas can never be mixed into one CSV mid-campaign.
                bool needHeader = !System.IO.File.Exists(p);
                if (!needHeader)
                {
                    string first = null;
                    try { using (var sr = new System.IO.StreamReader(p)) first = sr.ReadLine(); } catch { }
                    if (first != null && first.Trim() != ResearchHeader)
                    {
                        string bak = p + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".old";
                        try
                        {
                            System.IO.File.Move(p, bak); needHeader = true;
                            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: research schema changed — rotated the old CSV to " + System.IO.Path.GetFileName(bak) + " so layouts never mix.");
                        }
                        catch (Exception mv)
                        { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: research schema changed but rotation FAILED (" + mv.Message + ") — writing NOTHING rather than mixed-schema rows."); return; }
                    }
                }
                if (needHeader) sb.Append(ResearchHeader).Append('\n');
                sb.Append('"').Append(pose == null ? "?" : pose.Replace('"', '\'')).Append("\",")
                  .Append('"').Append(motion == null ? "?" : motion).Append("\",")
                  .Append(fKok.ToString("F4", inv)).Append(',').Append(fStat.ToString("F4", inv)).Append(',')
                  .Append(mScale.ToString("F4", inv)).Append(',').Append(mDan.ToString("F4", inv)).Append(',')
                  .Append(canal.ToString("F5", inv)).Append(',')
                  .Append(mn.ToString("F5", inv)).Append(',').Append(rg.ToString("F5", inv)).Append(',')
                  .Append(deepVec.x.ToString("F5", inv)).Append(',').Append(deepVec.y.ToString("F5", inv)).Append(',')
                  .Append(deepVec.z.ToString("F5", inv)).Append(',')
                  .Append(natural.ToString("F4", inv)).Append(',').Append(fitted.ToString("F4", inv)).Append(',')
                  .Append(settleS.ToString("F1", inv)).Append(',')
                  // b691 bone-scale probe — b694 is the wiring that silently never applied before. Which
                  // level does the game actually touch per pose? lossyScale is ACCUMULATED, so a scaled
                  // parent moves everything below it; sampling several levels localises where it happens.
                  .Append((MainGameWomb.RebindPivotBone != null && MainGameWomb.RebindPivotBone.root != null
                           ? MainGameWomb.RebindPivotBone.root.lossyScale.y : 0f).ToString("F4", inv)).Append(',')
                  .Append(BoneScaleY("cf_n_height").ToString("F4", inv)).Append(',')
                  .Append(BoneScaleY("cf_j_hips").ToString("F4", inv)).Append(',')
                  .Append(BoneScaleY("cf_j_waist01").ToString("F4", inv)).Append(',')
                  .Append(BoneScaleY("cf_j_thigh00_L").ToString("F4", inv)).Append(',')
                  .Append(BoneScaleY("cf_j_head").ToString("F4", inv)).Append('\n');
                System.IO.File.AppendAllText(p, sb.ToString());
                MainGameWomb.ResearchRows++;   // b692: signals the auto-collector that this pose yielded a sample
                // b715: tally per current character (canal) x animation key, for the readiness dot.
                MainGameWomb.ResearchCanalMM = Mathf.RoundToInt(canal * 1000f);
                string dk = MainGameWomb.ResearchCanalMM + "|" + (pose ?? "?");
                int dc; MainGameWomb.ResearchCounts.TryGetValue(dk, out dc); MainGameWomb.ResearchCounts[dk] = dc + 1;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: research-log write failed: " + e.Message); }
        }
#endif   // CLOXRAY_RESEARCH

        // pose + her scale -> predicted (deepest base->canal, stroke range).
        private static bool PredictInBucket(System.Collections.Generic.List<AnimSample> lst, float scale, float hisScale, out float minOut, out float rangeOut)
        {
            minOut = 0f; rangeOut = 0f;
            if (lst.Count == 0) return false;
            if (lst.Count == 1)
            {
                AnimSample s0 = lst[0];
                float fHer = s0.FKok > 1e-4f ? scale / s0.FKok : 1f;
                float fHim = (s0.MScale > 1e-4f && hisScale > 1e-4f) ? hisScale / s0.MScale : 1f;
                minOut = s0.Min * fHer;
                rangeOut = s0.Range * fHim;
                return true;
            }
            // endpoints.
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
            // group into male buckets keyed by round(mScale/0.05); track each bucket's mean mScale.
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
            // nearest bucket below and above HIS size (by bucket mean mScale).
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

        // STROKE CONTROL: BP lays the dan chain out at FIXED length, so the tip lands at (squished length −
        // base->kokan).
        private object _danOpts;
        private System.Reflection.FieldInfo _fSquishThr, _fLenSquish;
        private float _lastThr;   // last stroke-drive squishThreshold written this frame (guard re-asserts it)
        private bool _lineMissingLogged;   // one-shot: penis line unreadable while the game says penetrated
        // b869 — the unreadable-line error used to fire the instant HFlag said PENETRATED, but BP sets
        // m_referenceTarget on its OWN first vaginal SetDanTarget, which lands a frame or two later. So the
        // error was announcing a race we always win: measured twice in one session with no visible fault,
        // and the 2s watchdog never fired because the flag had already cleared. That is worse than noise —
        // it trains us to ignore the one message that would announce the real fault, where the target never
        // arrives at all (BP agents recreated, or a male with no BP uncensor) and the womb genuinely gets
        // no depth feed. So: latch when it goes missing, and only escalate if it is STILL missing after a
        // grace period. Transient stays silent; persistent reports how long, and what changed.
        private float _refMissingSince = -1f;      // unscaled time the line first went unreadable, -1 = readable
        private object _refMissingAgent;           // the DanAgent instance at that moment (identity change = BP rebuilt it)
        // b883: 0.5 -> 1.2s on evidence. A KK Free-H run produced one dropout that recovered on its own
        // after 0.6s, so 0.5s still let a benign transient print an error asserting "this is the real
        // fault ... suspect the male's BP uncensor" for a male whose penis was BP SoS. Still well inside
        // the 2s watchdog, so a genuinely dead line is announced before anything else reacts.
        private const float RefMissingGraceSec = 1.2f;
        private string _mdState; private float _mdMin = float.MaxValue, _mdMax, _mdT0;   // MOTION-DEPTH research window (debug)
        private bool _lockedNoMotion; private int _noMotionReopens; private string _loopCheckedState;   // b529 no-motion re-open
        private string _lockLoop;   // b732: loop the current lock was measured in (null = no lock yet)
        private float _pushSm;   // smoothed thrust (the raw envelope is near-binary on short strokes)
        // AUTO-LENGTH (b457): fit m_baseDanLength ONCE per animation (slow drift, constant during
        // play — per-stroke size change was rejected as unnatural). The DanAgent + field to write:
        private object _danAgent;
        private System.Reflection.FieldInfo _fBaseLen;
        private float _danLen0;        // BP's ORIGINAL natural length — restored on remove
        // b737: fitted/natural, <1 = the fit length-squished the penis, whose visual shaft then runs
        // FATTER than the tip-collider latch (BP danGirthSquish volume conservation); >1 = stretched,
        // shaft runs THINNER (proven both ways: big male 209 squished to ~180 read too tight, small
        // male 117 stretched to the 146 cap read too wide). Read by the canal-width solver.
        public static float FitSquishRatio = 1f;
        public static float NaturalDanLen;
        private float _lenSquishVal = 0.8f;   // the user-string danLengthSquish (for the tip estimate)

        // BP's own penis-line inputs (read from the DanAgent each frame).
        private System.Reflection.FieldInfo _fRefTarget, _fDanPoints, _fDanEnd;
        private System.Reflection.MethodInfo _miGetDanStart;
        private object _danPointsObj;

        private System.Reflection.FieldInfo _fOptOnAgent;   // DanAgent.m_danOptions — re-read each tick (BP can swap/rewrite it)

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

        // BP's live penis line: base (danStart), reference-target (vagina), and the real end (danEnd, where
        // BP actually puts the tip after ConstrainDan/bend).
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
                // live config offset.
                var go = new GameObject(ProxyName);
                _proxy = go.transform;
                _proxy.SetParent(transform, false);
            }
            UpdateProxy();
            // never capture its own proxy as "the original": a re-attach (agent watchdog, respawn) finds.
            var cur = _fInner.GetValue(_agent) as Transform;
            if (cur == null || cur.name != ProxyName) _original = cur;
            _fInner.SetValue(_agent, _proxy);
            // BP's endpoint is only OURS from here. Before this, on every pose change, BP has rebuilt its
            // agent and reset m_innerTarget to its own default (cf_j_waist01) - so danEnd is aimed at her
            // WAIST, and projecting it onto the canal produced the nonsense readings that sent us chasing
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
            // Back = the PELVIS frame's backward (the womb's mirrored kokan).
            Vector3 back = _backRef != null ? -_backRef.forward : Vector3.zero;
            // Deeper along the canal axis (entrance -> aim bone): a positive depth offset makes the pinned
            // penis press INTO the dome, feeding the stretch/displace reaction.
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
            // rawPin = the pin point WITHOUT the follow.
            bool autoLen = LiquidWobbleMPBPlugin.CfgHAutoLength && _womb != null && _womb.HasCanal && _womb.CanalLenWorld > 1e-4f;
            // END TARGET = the womb's authored `penis_target` BONE, pure.
            Vector3 rawPin = autoLen
                ? _target.position
                : _target.position
                  + back * (LiquidWobbleMPBPlugin.CfgHPinOffset * 0.001f)
                  + axis * (LiquidWobbleMPBPlugin.CfgHPinDepth * 0.001f);
            _proxy.position = autoLen ? rawPin : rawPin + axis * _follow;
            // THRUST ENVELOPE (the stretch driver).
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
                    // the game flips to PENETRATED — BP sets m_referenceTarget on its own first vaginal
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
                // AUTO-LENGTH measures the first LEG of BP's layout path directly.
                float d2 = autoLen ? Vector3.Distance(_danBase.position, _womb.CanalEntranceWorld)
                                   : Vector3.Distance(_danBase.position, rawPin);
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
                // b649: the envelope samples ONLY while the game says PENETRATED. It used to eat every
                // d2 — including the male standing across the room at scene start (log: range [9..276]mm
                // on a 59mm canal) — and at 10mm/s the spike took ~25s to forget, holding push≈0.93 the
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
                float push = range > 0.006f ? Mathf.Clamp01((_maxD - d2) / range) : 0f;   // guard: idle/no strokes yet (6mm — close-body anims stroke only ~10-14mm)
                // SMOOTH the push: on short strokes (12-19mm ranges) the raw envelope is near-binary
                // (d touches the extremes) and slammed the stretch 0<->max — the "sudden stretch that
                // reads as a jump". Rise faster than fall so the push still leads the thrust.
                float kp = push > _pushSm ? 6f : 3f;
                _pushSm = Mathf.Lerp(_pushSm, push, 1f - Mathf.Exp(-kp * Time.deltaTime));
                _womb.ExternalPress = _pushSm;
                // the commanded tip only used the middle of the canal.
                if (_smMin > _smMax) { _smMin = _smMax = _pushSm; }
                _smMax = Mathf.Max(_pushSm, _smMax - 0.12f * Time.deltaTime);
                _smMin = Mathf.Min(_pushSm, _smMin + 0.12f * Time.deltaTime);
                float strokeN = (_smMax - _smMin > 0.05f) ? Mathf.Clamp01((_pushSm - _smMin) / (_smMax - _smMin)) : _pushSm;

                // ===== AUTO PENIS LENGTH (b457) =====
                // Per-stroke size modulation was rejected (rubber penis). Instead FIT the penis's
                // natural length ONCE per animation (slow 15mm/s drift, imperceptible; constant during
                // play): the deepest point of the animation should put the tip just at/past the cervix
                // (In-stroke %). Too long (tip parked at the womb, BP squish eating the whole stroke)
                // -> shortens until the animation's own motion shows; too short (never reaches) ->
                // lengthens. The STROKE itself is 100% the animation's own base motion.
                // Colinear estimate: path base->entrance ≈ d2 − Dpin, so tip depth = L − (d2 − Dpin).
#if CLOXRAY_RESEARCH   // 1.1 release strip: research scaffolding compiles only in research builds (add CLOXRAY_RESEARCH to DefineConstants)
                // ===== b690 RESEARCH SAMPLER (temporary; strip with the rest of the b688 scaffolding) =====
                // Independent of the gameplay fit: wait for the pose+motion to be SETTLED, then observe for a
                // multi-cycle window so MIN/MAX are the animation's true extremes rather than whatever slice
                // the fast fit happened to catch.
                {
                    // b711: unique per ANIMATION ASSET, not id+name (which ~20 Doggy variants share).
                    string rkey = MainGameWomb.CurrentAnimKeyV + "|" + (MainGameWomb.HMotion ?? "?");
                    if (rkey != _rsKey)
                    { _rsKey = rkey; _rsT = 0f; _rsSettle = 0f; _rsMin = float.MaxValue; _rsMax = 0f; _rsDone = false; _rsPrevD2 = -1f; _rsDir = 0; _rsRevs = 0; _rsImpT = 0f; }
                    if (!MainGameWomb.HPenetrated) _rsSettle = 0f;
                    else if (!_rsDone)
                    {
                        _rsSettle += Time.deltaTime;
                        if (_rsSettle >= 3f)          // let the characters finish staging first
                        {
                            if (d2 < _rsMin && _danBase != null && _entrance != null)
                                _rsDeep = _danBase.InverseTransformPoint(_entrance.position);
                            if (d2 < _rsMin - 0.0005f || d2 > _rsMax + 0.0005f) _rsImpT = _rsT;   // b729: extremes still improving
                            _rsMin = Mathf.Min(_rsMin, d2); _rsMax = Mathf.Max(_rsMax, d2);
                            // b729: stroke-cycle counter — direction reversals of d2 with 1.5mm hysteresis.
                            if (_rsPrevD2 < 0f) _rsPrevD2 = d2;
                            else if (Mathf.Abs(d2 - _rsPrevD2) > 0.0015f)
                            {
                                int rsDirNow = d2 > _rsPrevD2 ? 1 : -1;
                                if (_rsDir != 0 && rsDirNow != _rsDir) _rsRevs++;
                                _rsDir = rsDirNow; _rsPrevD2 = d2;
                            }
                            if (_rsT <= 0f) { _rsFrame0 = Time.frameCount; _rsReal0 = Time.unscaledTime; }   // b727: window opens
                            _rsT += Time.deltaTime;
                            bool rsEnough = _rsRevs >= 6 && _rsT >= 2.5f && (_rsT - _rsImpT) >= 1.0f;   // b729 early-exit
                            if ((_rsT >= 8f || rsEnough) && _rsMin < float.MaxValue && _womb != null)   // several cycles
                            {
                                _rsDone = true;
                                // b699 — "only moving ones": a pose whose thrust barely moves over a
                                // full multi-cycle window is a STATIC/idle one — it characterises nothing and
                                // would just add a RANGE~0 row that drags any fit toward zero. Skip logging,
                                // but still count the episode so the sweep advances immediately.
                                float rsRange = _rsMax - _rsMin;
                                // b709: ALT-TAB SAFETY. Unity pauses when the window loses focus, so an
                                // observation spanning that is part real seconds and part frozen ones —
                                // its min/max are not this animation's true extremes. Discard rather than
                                // record a plausible-looking bad row; the pose can simply be re-collected.
                                // b727: discard only if frames actually STALLED across the window (paused
                                // engine or catastrophic hitch) — <5 fps average. Alt-tab with run-in-background
                                // keeps animating and passes; a paused window cannot fake progress.
                                float rsElapsed = Mathf.Max(Time.unscaledTime - _rsReal0, 1e-3f);
                                bool rsFocusOk = (Time.frameCount - _rsFrame0) / rsElapsed >= 5f;
                                if (!rsFocusOk)
                                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: research — DISCARDED ["
                                        + MainGameWomb.CurrentAnimKeyV + " / " + MainGameWomb.HMotion
                                        + "]: frames stalled during the observation window ("
                                        + ((Time.frameCount - _rsFrame0) / rsElapsed).ToString("F1") + " fps avg — engine paused or hitching).");
                                else if (rsRange >= 0.010f)
                                    ResearchLog(MainGameWomb.CurrentAnimKeyV, MainGameWomb.HMotion, HerScale(), HerStature(),
                                                _danBase != null ? _danBase.lossyScale.y : 0f, _danLen0,
                                                _womb.CanalLenWorld, _rsMin, rsRange, _rsDeep,
                                                _danLen0, _danLen, _rsSettle);
                                else
                                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: research — skipped STATIC pose ["
                                        + MainGameWomb.CurrentAnimKeyV + " / " + MainGameWomb.HMotion + "]: stroke only "
                                        + (rsRange * 1000f).ToString("F1") + "mm over 8s.");
                                MainGameWomb.ResearchEpisodes++;
                            }
                        }
                    }
                }
#endif   // CLOXRAY_RESEARCH
                if (autoLen)
                {
                    float canal = _womb.CanalLenWorld;
                    float wantTip = Mathf.Clamp(LiquidWobbleMPBPlugin.CfgHInStrokePct * 0.01f, 0.5f, 1.4f) * canal;
                    bool contactStable = MainGameWomb.HMotionKnown
                        ? (MainGameWomb.HPenetrated && Time.unscaledTime - MainGameWomb.HMotionChangedAt > 0.4f)
                        : _measT >= 1.2f;
                    _womb.ExternalFitLocked = (_fitState == 2 && !_lockedNoMotion) || (_fitState == 1 && contactStable);
                    const float LCap = 1.25f;
                    { var hmLoop = MainGameWomb.HMotion; if (hmLoop != null && hmLoop.IndexOf("Loop", StringComparison.Ordinal) >= 0) s_lastLoop = hmLoop; }
                    FitSquishRatio = (_danLen0 > 1e-3f && _danLen > 1e-3f) ? Mathf.Clamp(_danLen / _danLen0, 0.5f, 1.25f) : 1f;   // b737: live squish for the canal-width solver
                    NaturalDanLen = _danLen0;   // b738: male identity for the girth latches
                    if (_seenPose != MainGameWomb.PoseVersion && _fitState == 2)
                    {
                        _seenPose = MainGameWomb.PoseVersion;
                        _fitState = 1; _measT = 0f; _measMin = float.MaxValue; _measMax = 0f;
                        _lockedNoMotion = false; _noMotionReopens = 0; _lockLoop = null;
                        _minD = float.MaxValue; _maxD = 0f; _smMin = float.MaxValue; _smMax = 0f;
                        float reEst;
                        if (s_poseFit.TryGetValue(FitKey(canal), out reEst))
                        {
                            _danLen = Mathf.Clamp(reEst, _danLen0 * 0.65f, _danLen0 * LCap);
                            try { _fBaseLen.SetValue(_danAgent, _danLen); } catch { }
                        }
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: fit RE-MEASURING (pose change) — one stroke (~1.5s); size stays locked meanwhile.");
                    }
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
                    // (WLoop<->SLoop via the gauge), re-prime from the NEW loop's cached/learned answer and
                    // re-measure one stroke.
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
                        // PRIME instantly from the CURRENT frame so the penis never STARTS at max stretch.
                        _seenPose = MainGameWomb.PoseVersion;
                        float primeEst, pMin, pRange;
                        if (s_poseFit.TryGetValue(FitKey(canal), out primeEst)) { }              // this exact pose+char before -> its answer
                        // b682: LEARNED profile for this ANIMATION at HER size (pose + character scale),
                        // config-independent and persisted -> predicts a character/session we've never fit.
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
                            // base->canal vector in HIS base-local frame.
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
                        // frame was mid-rebuild (pose change; BP m_referenceTarget was null moments before)
                        // even though the window looked 'stable'.
                        else if ((strokeSeen ? _measMin : d2) > canal * 2f && _measT >= 2.2f)
                        {
                            _measT = 0f; _measMin = float.MaxValue; _measMax = 0f;
                            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: fit measurement rejected (deep end "
                                + ((strokeSeen ? _measMin : d2) * 1000f).ToString("F0") + "mm >> canal " + (canal * 1000f).ToString("F0")
                                + "mm — reference frame mid-rebuild) — restarting the window.");
                        }
                        else if ((_measT >= 2.2f && strokeSeen) || _measT >= 12f)
                        {
                            // Deepest leg + desired tip depth, floored so the out-stroke keeps the tip
                            // solidly inside; capped 1.25×natural (no rubber penis, ever).
                            float deepL = (strokeSeen ? _measMin : d2) + wantTip;
                            if (strokeSeen)
                            {
                                // ABSOLUTE stroke scale (b522) + ABSOLUTE poke (b524): animations are
                                // authored in body-space mm, and so is the poke the user wants to SEE.
                                // The old %-of-canal overshoot peaked at ~6-10mm, of which BP's squish
                                // shows only ~20% (=1-2mm: "penis enters the dome without deforming").
                                // Deep strokes (>=70mm) now target the tip 25mm PAST the cervix (the
                                // KK-approved dome-poke magnitude); small strokes (<=30mm) keep the
                                // just-below-cervix turnaround; linear between.
                                // b680 — "still too many poses poke — reduce the NUMBER": the poke
                                // scales with stroke RANGE, and the onset was 30mm so most poses (40-48mm
                                // range on the tested char) poked. Raise onset 30->40mm: poke now starts at
                                // ~45mm range, so only the bigger-stroke (genuinely deep) poses push the dome
                                // and the mid-range ones land just below the cervix. Deep-poke magnitude kept.
                                deepL = FitFromStrokeRaw(_measMin, _measMax - _measMin, canal);   // b689: shared with the predictor
                            }
                            float keepIn = strokeSeen ? float.MinValue : (d2 + 0.20f * canal);
                            // fit never reduces the penis, whatever the animation would prefer.
                            _danLen = Mathf.Clamp(Mathf.Max(deepL, keepIn), _danLen0 * 0.65f, _danLen0 * LCap);
                            _fitSurplus = Mathf.Max(0f, _danLen - Mathf.Min(Mathf.Max(deepL, keepIn), _danLen0 * LCap));
                            try { _fBaseLen.SetValue(_danAgent, _danLen); } catch { }
                            _fitState = 2;
                            _lockLoop = s_lastLoop;   // b732: this lock is valid for THIS loop only
                            _lockedNoMotion = !strokeSeen;   // b529: no-motion locks stay re-openable when a loop state starts
                            // b680: remember this fit to PREDICT the next one (MOTION locks only — no-motion
                            // locks over-estimate). This is what turns the next pose's 178->140 settle jump
                            // into a small correction (or none, for a repeat pose).
                            if (strokeSeen) { s_lastFitLen = _danLen; s_poseFit[FitKey(canal)] = _danLen; }
                            // and persist.
                            if (strokeSeen)
                            {
                                LoadAnimProf();
                                string apose = MainGameWomb.CurrentAnimKeyV + "|" + s_lastLoop;   // b732: loop-keyed
                                float asc = HerScale();
                                System.Collections.Generic.List<AnimSample> alst;
                                if (!s_animProf.TryGetValue(apose, out alst))
                                { alst = new System.Collections.Generic.List<AnimSample>(); s_animProf[apose] = alst; }
                                var asmp = new AnimSample();
                                asmp.Min = _measMin; asmp.Range = _measMax - _measMin;
                                // can be picked from data later instead of assumed now.
                                asmp.FKok = asc;
                                asmp.FStat = HerStature();   // b686: pose-independent (local bone offsets)
                                asmp.MScale = _danBase != null ? _danBase.lossyScale.y : 0f;
                                asmp.MDan = _danLen0;
                                int aat = -1;
                                for (int i = 0; i < alst.Count; i++)
                                    if (Mathf.Abs(alst[i].FKok - asc) < 0.02f
                                     && Mathf.Abs(alst[i].MScale - asmp.MScale) < 0.02f) { aat = i; break; }
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
                        }
                    }
                    // resting one.
                    if (_fSquishThr != null && _danLen > 1e-3f)
                        try
                        {
                            float domeD = _womb.CurrentDomeTravelMM * 0.001f;
                            _lastThr = Mathf.Clamp((canal + domeD) / _danLen, 0.02f, 0.95f);
                            _fSquishThr.SetValue(_danOpts, _lastThr);
                        }
                        catch { }
                    // now (raw laid tip L−d2 minus the canal).
                    _womb.ExternalCompressMM = Mathf.Max(0f, (_danLen - _fitSurplus - d2 - canal) * 1000f);
                    _womb.ExternalStrokeTrusted = _fitSurplus <= 1e-4f;   // b650: floored penis -> danEnd leak is fake reach
                    // NO ESTIMATE FEED (b496): the womb consumes ONLY BP's real danEnd (OnPreCullCanal
                    // override). The old L−d2 estimate silently took over whenever danEnd was
                    // unreadable — it ran a WHOLE session on dead agent handles (ref=- in every
                    // H-STATE) and kept the womb reacting while the penis was pulled out. Mechanism
                    // fails -> LogError + nothing moves (design rule: no fallback paths, ever).
                }
                // STROKE (legacy path, auto-length OFF): sweep BP's squishThreshold with the smoothed thrust
                // so the tip's stop depth travels shallow..deep.
                else if (_danOpts != null && _fSquishThr != null && _danLen > 1e-3f)
                {
                    float mm = Mathf.Lerp(LiquidWobbleMPBPlugin.CfgHStrokeShallow, LiquidWobbleMPBPlugin.CfgHStrokeDeep, _pushSm);
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
            if (_watchPose != MainGameWomb.PoseVersion)
            {
                _watchPose = MainGameWomb.PoseVersion;
                _next = 0f;
                _fitState = 0;
                MainGameWomb.ReattachPenisAim();
                return;
            }
            if (_agent == null || _fInner == null || _proxy == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            try
            {
                // everything below would write to dead objects.
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
                // LIVE BP-HEALTH (2s): watch m_bpDanPointsFound.
                const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                bool bp = false, dp = false;
                try { var f = _danAgent.GetType().GetField("m_bpDanPointsFound", BF); if (f != null) bp = (bool)f.GetValue(_danAgent); } catch { }
                try { var f = _danAgent.GetType().GetField("m_danPointsFound", BF); if (f != null) dp = (bool)f.GetValue(_danAgent); } catch { }
                if (bp != _lastBpHealth) { _lastBpHealth = bp; LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP-HEALTH bpDanPointsFound=" + bp + " danPointsFound=" + dp + " (our aim target is " + (bp ? "RESPECTED" : "IGNORED — endpoint body-driven/random") + ")."); }
                // it resolved.
                if (LiquidWobbleMPBPlugin.CfgDebugLog)
                {
                    bool pen = false; string rn = "-";
                    try { var f = _danAgent.GetType().GetField("m_danPenetration", BF); if (f != null) pen = (bool)f.GetValue(_danAgent); } catch { }
                    try { var f = _danAgent.GetType().GetField("m_referenceTarget", BF); var rt = f != null ? f.GetValue(_danAgent) as Transform : null; if (rt != null) rn = rt.name; } catch { }
                    // AIM GEOMETRY too: a persistent mistarget shows up as a large tip<->pin gap, or as a
                    // pin sitting far from the womb's own penis_target.
                    string aim = "-";
                    try
                    {
                        // BP's own end marker is what it drives; compare it against where it pin.
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
                // changes and agent re-inits.
                if (_fOptOnAgent != null && _danAgent != null)
                {
                    var curOpt = _fOptOnAgent.GetValue(_danAgent);
                    if (curOpt != null)
                    {
                        bool swapped = !ReferenceEquals(curOpt, _danOpts);
                        if (swapped) SetStrokeControl(curOpt);   // re-resolve squishThreshold/danLengthSquish fields on the NEW object
                        // ALWAYS re-apply the override string + simplify (idempotent, 2s): BP's config
                        // re-applies MUTATE THE SAME OBJECT (b490 log: config Squish 0.6 silently replaced
                        // our 0.8 without an object swap -> less over-depth absorbed -> tip 33mm past the
                        // cervix -> stretch pegged). A cheap unconditional re-assert closes that for good.
                        string ovr = LiquidWobbleMPBPlugin.CfgHBPDanOptions;
                        if (!string.IsNullOrEmpty(ovr)) MainGameWomb.ApplyOptions(curOpt, ovr);
                        var fSimp = curOpt.GetType().GetField("simplifyVaginal", BF);
                        if (fSimp != null) fSimp.SetValue(curOpt, true);
                        // string (0.40), stomping the stroke-drive value UpdateProxy wrote EARLIER this same
                        // frame.
                        if (_fSquishThr != null && _lastThr > 0f)
                            try { _fSquishThr.SetValue(_danOpts, _lastThr); } catch { }
                        if (swapped) LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP DanOptions object SWAPPED (agent re-init) — stroke-drive re-bound + overrides re-applied.");
                    }
                }
            }
            catch { }
        }
        // BP's own reset entry points on DanAgent (same type as SetDanTarget).
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
                // HAND THE PENIS BACK TO BP. Restoring the fields above stops it driving it, but BP only
                // writes the dan bones while it is adjusting.
                HandDanBackToBP();
                MainGameWomb.RestoreBPSimplifyConfig();   // give the user their BP Simplify setting back
                MainGameWomb.RestoreBPUncensorDefaults(); // and their UncensorSelector defaults (b515: reinstated, proven innocent)
            }
            catch { }
        }
    }

    // Mirrors every same-named cf_* bone of the womb item to the wearer's live skeleton.
    internal class WombBoneMirror : MonoBehaviour
    {
        private Transform _wearer;
        private Transform[] _mine;
        private Transform[] _theirs;
        private Transform _pivot;          // wearer's cf_j_kokan — the scale anchor (entrance stays put)
        private Material _liquid;          // instanced CloXray/Liquid material (fill push)
        private float _lastFill = -1f;
        private WombExpandEffect _fx;      // engaged-state source (pull-out detection)
        // PULL-OUT cum flow: when the penis withdraws with cum in the womb, the cum flows womb->canal
        // then out the entrance. State machine over the two closed chambers (womb=_FillAmount,
        // canal=_FillAmount2). None -> WombToCanal (womb drains, canal fills) -> CanalOut (canal drains
        // out the bottom) -> None. Purely additive; canal returns to empty (its normal H state).
        private int _flowState;            // 0 none, 1 pre-fill canal, 2 wait for game drip, 3 drain womb, 4 drain canal (b504)
        private float _flowT, _flowStartFill, _flowDomeStart;
        private bool _prevPullOut;
        private float _lastFill2 = -1f, _lastBottom2 = -1f;
        private Renderer _liquidRend;      // renderer carrying the liquid material (extent MPB diag)
        private float _liqDbgNext;         // H-LIQUID diagnostic timer
        // Static 'mounddown' shaping (the user's Studio setup stretches the mound down to the female's
        // bottom — KKPE weight 50 there): applied as a LIVE F1 value in H. Not a reaction channel
        // (WombExpandEffect drives moundforward/back/left/right, never mounddown), so a static set is safe.
        private SkinnedMeshRenderer _moundSmr;
        private int _moundIdx = -2;        // -2 = not searched yet, -1 = mesh has no mounddown

        public void Bind(Transform wearer)
        {
            _wearer = wearer;

            var theirs = new Dictionary<string, Transform>();
            foreach (var t in wearer.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("cf_") && !theirs.ContainsKey(t.name) && t.GetComponentInParent<WombBoneMirror>() == null)
                    theirs[t.name] = t;
            theirs.TryGetValue("cf_j_kokan", out _pivot);

            var mine = new List<Transform>();
            var match = new List<Transform>();
            var frozen = new List<Transform>();   // b537: bones pinned rigid to the pelvis
            var frozenMatch = new List<Transform>();   // b575: her matching bone (for the FOLLOW branch when the pin is toggled OFF)
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                Transform c;
                string role3 = MainGameWomb.HerNameFor(t.name);   // womb bone -> her equivalent
                if (role3.StartsWith("cf_") && theirs.TryGetValue(role3, out c))
                {
                    // b530/b536/b537/b577: the skin-helper bones that animate relative to the pelvis
                    // FOLLOW her matching bone (via frozenMatch) so the canal mesh — weighted ~98% to
                    // these four (cf_s_waist02 62%, cf_s_waist01 36%; legs bow the sides) — tracks her
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
            // parents before children so world writes compound correctly.
            var order = new int[mine.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => Depth(mine[a]).CompareTo(Depth(mine[b])));
            _mine = new Transform[mine.Count];
            _theirs = new Transform[mine.Count];
            for (int i = 0; i < order.Length; i++) { _mine[i] = mine[order[i]]; _theirs[i] = match[order[i]]; }
            _kokanIdx = -1;
            for (int i = 0; i < _mine.Length; i++) if (ReferenceEquals(_theirs[i], _pivot)) { _kokanIdx = i; break; }   // b532 lag meter
            _authScale = new Vector3[_mine.Length];   // b538: authored rest local scales (bind-consistent skinning)
            for (int i = 0; i < _mine.Length; i++) if (_mine[i] != null) _authScale[i] = _mine[i].localScale;
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

        private void OnPreCullSync(Camera cam)
        {
            // game moves her pelvis fast after LateUpdate (pull-out, pose change, idle repositioning).
            if (!MainGameWomb.HPenetrated && Time.frameCount != _notPenSyncFrame)
            {
                _notPenSyncFrame = Time.frameCount;
                SyncNow();
            }
        }
        private int _notPenSyncFrame = -1;
        private int _notPenRenderFrame = -1;

        private void OnPreRenderSync(Camera cam)
        {
            if (!MainGameWomb.HPenetrated && Time.frameCount != _notPenRenderFrame)
            {
                _notPenRenderFrame = Time.frameCount;
                SyncNow();
            }
        }
        private int _kokanIdx = -1;
        private Transform[] _frozenT; private Transform[] _frozenMatch;   // b577 skin-helpers that FOLLOW her bones
        private Vector3[] _authScale;   // b538 authored rest local scales
        // ROUTE-B: set once the SMRs have been re-pointed at the wearer's bones. Unity then skins the
        // womb from her skeleton, so there is nothing for the mirror to copy — SyncNow becomes a no-op
        // (this also neutralises the pre-cull / pre-render re-syncs). b743: ONE SHARED implementation —
        // the per-game #if split existed for the KKS seat servo, which is now removed (the b577 body
        // was already unified); per-game seat constants stay in their own #if at the top of the file.
        public bool Rebound;

        public void SyncNow()
        {
            if (_mine == null || _wearer == null) return;
            if (Rebound) return;
            float s = MainGameWomb.HMirrorWombScaleFor(_pivot);   // b563: KKS scales with her pelvis (female-tracking)
            bool scaled = _pivot != null && Mathf.Abs(s - 1f) > 0.001f;
            Vector3 pivot = scaled ? _pivot.position : Vector3.zero;
            // Whole-womb seat offset along the pelvis backward direction (F1 'Womb offset to back'):
            // the mesh's anatomical seat can sit too far toward her belly for some bodies/uncensors —
            // the penis then hugs the canal's BACK wall. Uniform on every bone = the womb translates
            // rigidly; the pelvis frame turns with the pose so it stays anatomical in any position.
            float seatBackMM = LiquidWobbleMPBPlugin.CfgHWombBack - MainGameWomb.SeatForwardMM;   // b554
            Vector3 seat = (_pivot != null && Mathf.Abs(seatBackMM) > 0.01f)
                ? -_pivot.forward * (seatBackMM * 0.001f) : Vector3.zero;
            for (int i = 0; i < _mine.Length; i++)
            {
                var m = _mine[i]; var c = _theirs[i];
                if (m == null || c == null) continue;
                // Scale is injected once at the chain root (i==0, shallowest) so it propagates to every
                // descendant's lossy scale exactly once; positions are scaled about the entrance pivot so the vaginal opening stays anchored while the womb grows upward/outward.
                m.localScale = (scaled && i == 0) ? c.localScale * s : c.localScale;
                Vector3 p = c.position;
                if (scaled) p = pivot + (p - pivot) * s;
                m.SetPositionAndRotation(p + seat, c.rotation);
            }


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

            // orientation, period.
        }

        // ── Fill: F1 config base + finish bursts (ME has no H-scene UI) ───────────────── Each
        // inside-finish (the cum button) raises a burst accumulator by 'Fill on finish', eased in over a few seconds on top of the base slider.
        internal static Component s_cumOwner;
        private static float _burst;   // the finish-cum fill level (over the config base), driven by the state machine below
        // FINISH CUM state machine (user spec): each inside-finish RISES the womb fill to a PEAK over
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
            // ovary_shrink) 40% -> full belly over ~3 overcum loads (25%/4 loads read too subtle.
            if (_cumShot >= 3) _domeTarget = Mathf.Clamp(_domeTarget + 40f, 0f, 100f);
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: inside finish #" + _cumShot + " (" + src + ") — fill " + _cumFrom.ToString("F2")
                + " -> peak " + _cumPeak.ToString("F2") + " -> settle " + _cumSettle.ToString("F2")
                + (_cumShot > 3 ? "; dome -> " + _domeTarget.ToString("F0") : "") + ".");
        }

        // Advance the finish-cum fill (called every frame from PushFill).
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
                // b567: small females get a relatively OVERSIZED womb (sub-linear scaling), so the
                // ovaries can breach the small body. Pull them in with ovary_shrink proportional to the
                // oversize (effKok/kok — how much bigger than proportional the womb is). Combined with
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
        public const float OvaryShrinkGain = 150f;
        public const float OvaryShrinkMax = 60f;

        private static Type _hflagType;
        private static System.Reflection.FieldInfo _fClick;
        private static UnityEngine.Object _hflag;
        private static string _lastClick;
        private static object _lastClickBox;   // b596: last boxed HFlag.click, so the per-frame path never stringifies
        private static float _hflagRetry;
        private static bool _namesLogged;
        // Counter-based detection (robust): HFlag keeps per-act finish COUNTERS that persist, unlike the
        // one-frame click pulse that script order can miss.
        private static object _countObj;
        private static System.Reflection.FieldInfo _fSonyuIn;
        private static int _lastSonyuIn = -1;
        private static System.Reflection.FieldInfo _fNowAnim;
        private static bool _nowAnimMissing;
        private static string _lastMotion = "";
        private static string _unknownMotionLogged = "";
        // (the AnimationListInfo the scene is playing).
        private static System.Reflection.FieldInfo _fAnimInfo;
        private static bool _animInfoMissing;
        private static object _lastAnimInfo;
        private static bool _animInfoSeen;
        private static System.Reflection.FieldInfo _fAiId, _fAiName;
        private static bool _aiFieldsTried;
        private static string _lastAnimDesc = "";
        private static bool _varAlt;   // toggles on same-id animInfo swaps (the ALTERNATIVE button)
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
        // Diagnostic label for an AnimationListInfo: "id=..
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
                // Variant watch: reference change on nowAnimationInfo = the game switched what it is playing
                // (position change OR the ALTERNATIVE button) -> re-fit.
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
                            string desc = DescribeAnimInfo(ai);
                            bool sameAnim = !first && desc == _lastAnimDesc;
                            // provenance gated).
                            string vsuf = MainGameWomb.VariantSuffix;
                            _varAlt = vsuf.Length > 0;
                            _lastAnimDesc = desc;
                            MainGameWomb.HAnimLabel = desc + (vsuf.Length > 0 ? " ALT" + vsuf : "");
                            if (!first)
                            {
                                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: H animInfo swap -> " + MainGameWomb.HAnimLabel + (sameAnim ? " (same id = ALTERNATIVE toggle)" : " (new position)"));
                                // position are two AnimationListInfo objects with the same id+name.
                                if (sameAnim && LiquidWobbleMPBPlugin.CfgDebugLog)
                                    LiquidWobbleMPBPlugin._logger?.LogInfo("  ANIMINFO-FIELDS: " + DumpShallowFields(ai));
                                MainGameWomb.BumpPose("nowAnimationInfo -> " + MainGameWomb.HAnimLabel);
                            }
                        }
                    }
                }
                string m = _fNowAnim.GetValue(_hflag) as string;
                if (m == null) { _fx.HasPenetratedFlag = false; return; }
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
                    var mats = r.materials;   // instanced — never edit the shared bundle material
                    foreach (var m in mats)
                        if (m != null && m.shader != null && m.shader.name == "CloXray/Liquid") { _liquid = m; _liquidRend = r; break; }
                    if (_liquid != null) break;
                }
                if (_liquid == null) return;
                // Free-H cum belongs in the WOMB, not the canal: switch this instance to CLOSED mode
                // (independent chambers) with the canal chamber empty.
                _liquid.SetFloat("_ChamberMode_0single_1connected_2closed", 2f);
                _liquid.SetFloat("_FillAmount2", 0f);
                _liquid.SetFloat("_FillBottom2", 0f);
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: H liquid switched to CLOSED chambers (womb-only fill).");
            }

            float baseFill = Mathf.Clamp01(LiquidWobbleMPBPlugin.CfgHFillAmount + _burst);

            // button, "OUT" = finish-outside).
            string hm = MainGameWomb.HMotion;
            bool pullOut = MainGameWomb.HMotionKnown && !string.IsNullOrEmpty(hm)
                && (hm.IndexOf("Pull", System.StringComparison.OrdinalIgnoreCase) >= 0 || hm.Contains("OUT"));
            if (pullOut && !_prevPullOut)
            {
                // Real pull-out (post-grace).
                if (LiquidWobbleMPBPlugin.CfgHPullOutFlow && _flowState == 0 && baseFill > 0.05f)
                {
                    _flowState = 1; _flowT = 0f; _flowStartFill = baseFill; _flowDomeStart = _domeTarget;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: pull-out — leak choreography: pre-fill canal (top-down), WAIT for the game's drip ('Drop'), drain womb, drain canal (from fill " + baseFill.ToString("F2") + ").");
                }
                if (_cumShot > 0 || _burst > 0f)
                {
                    // Shots/burst reset immediately (the display is owned by the flow lerps below.
                    _cumShot = 0; _burst = 0f; _cumPhase = 0;
                    if (_flowState == 0) _domeTarget = 0f;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: pull-out — cum reset (shots cleared; dome " + (_flowState != 0 ? "deflates with the womb drain" : "cleared") + ").");
                }
            }
            _prevPullOut = pullOut;

            // PRE-FILL.
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
            else if (_lastFill2 > 0f)   // flow just ended — make sure the canal is reset to empty
            {
                _liquid.SetFloat("_FillAmount2", 0f); _liquid.SetFloat("_FillBottom2", 0f);
                _lastFill2 = 0f; _lastBottom2 = 0f;
            }

            if (Mathf.Abs(fill - _lastFill) >= 0.001f) { _liquid.SetFloat("_FillAmount", fill); _lastFill = fill; }

            // H-LIQUID diagnostic (Debug Log, 2s).
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
                    // Bind the persistent inside-finish counter: HFlag.count.sonyuIn.
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

                // Click watch (secondary/diagnostic): logs transitions; also triggers if the counter isn't
                // bound.
                if (_fClick == null) return;
                object clickBox = _fClick.GetValue(_hflag);
                if (clickBox == null || clickBox.Equals(_lastClickBox)) return;
                _lastClickBox = clickBox;
                string click = clickBox.ToString();
                _lastClick = click;
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
