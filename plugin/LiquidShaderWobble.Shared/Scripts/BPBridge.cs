using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// Reads BetterPenetration (Animal42069) state by reflection - no hard dependency, no-ops if BP
    /// isn't installed.
    ///
    /// PENETRATION (from Core_BetterPenetration source):
    ///   DanAgent.m_baseDanLength = penis natural length (per character, from maker). Constant.
    ///   DanAgent.lastDanDistance = base->IK-target distance; shrinks as the male thrusts in, and is
    ///                              set back to m_baseDanLength when not penetrating.
    ///   depth = (baseLen - dist) / baseLen   -> 0 when out, up to ~1 deep. Normalizes per length, so
    ///   a long or short penis maps the same (length auto-adjust, no slider).
    ///
    /// SQUISH (reconstructed from DanAgent.GetSquishedDanLength / GetSquishedDanGirth so this tracks what
    /// is actually RENDERED, not a theoretical tip):
    ///   T = squishThreshold, S = danLengthSquish, GS = danGirthSquish (all in m_danOptions).
    ///   visualDepth = depth                       when depth <= T   (no squish yet)
    ///               = T*S + (1-S)*depth           when depth >  T   (length-squished tip)
    ///       S=0 -> visualDepth=depth (tip can poke deep); S=1 -> capped at T (tip held at threshold).
    ///   girthFactor = 1 + max(0, depth - T) * GS  (penis fattens when pressed deep).
    ///
    /// GIRTH (width): avg(m_danColliderRadius) * danRadiusScale captures BOTH maker width and the
    /// studio "Collider Radius Scale" slider; * girthFactor adds the deep-press fattening.
    /// </summary>
    internal static class BPBridge
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool _tried;          // true once BP is bound (then never re-scan)
        private static float _nextScan;      // throttle the assembly re-scan while BP's lazy assembly hasn't loaded
        private static bool _bpMissingLogged;
        // PER MALE. This was a single static, so in a scene with more than one male only the first
        // one reached ever logged - a three-male scene printed chaM_001 twenty times and neither of the
        // two actually being investigated. A throttle that silently drops most of its subjects is worse
        // than no throttle.
        private static readonly System.Collections.Generic.Dictionary<int, float> _rawLogNextBy = new System.Collections.Generic.Dictionary<int, float>();
        private static Type _ctrlType;
        private static readonly System.Collections.Generic.Dictionary<int, float> _tipAheadCache = new System.Collections.Generic.Dictionary<int, float>();
        private static float[] _tipAhead = new float[0];   // per controller: metres the penis mesh reaches past the tip bone
        private static FieldInfo _fDan;
        private static FieldInfo _danDist, _danBaseLen, _danRad, _danColliders, _danOptions;
        private static FieldInfo _optRadiusScale, _optLengthSquish, _optGirthSquish, _optSquishThreshold;
        private static FieldInfo _colRadius; // DynamicBoneCollider.m_Radius (resolved lazily)
        private static FieldInfo _danTargetsValid;   // public bool on BetterPenetrationController (BP re-init finished)
        private static PropertyInfo _chaControlProp;  // CharaCustomFunctionController.ChaControl

        // Cached controller instances - refreshed on a slow timer, NOT every frame.
        private static UnityEngine.Object[] _cache = new UnityEngine.Object[0];
        private static bool[] _hasPenis = new bool[0];                // owns a real penis mesh (a female-bodied character with one included)
        private static Transform[] _tipBones = new Transform[0];      // each controller's k_f_dan_end (BP TARGET marker - only at the tip when constraint-driven)
        private static Transform[] _entryBones = new Transform[0];    // each controller's k_f_dan_entry (the ENTRY marker; cf_J_Vagina_root constraint pins it at the womb mouth -> the womb<->penis pairing key)
        private static Transform[] _danTipBones = new Transform[0];   // each controller's DEEPEST cm_j_dan* shaft bone = the visual mesh tip (tracks bend+squish)
        private static Transform[][] _danShaftBones = new Transform[0][]; // each controller's full cm_j_dan* shaft chain (base..tip), for the womb's whole-shaft containment test
        private static readonly System.Collections.Generic.List<Vector3> _gPosBuf = new System.Collections.Generic.List<Vector3>(16);  // reusable: per-collider girth-profile positions
        private static readonly System.Collections.Generic.List<float>   _gRadBuf = new System.Collections.Generic.List<float>(16);    // reusable: per-collider girth-profile world radii
        private static float _refreshTimer;
        private const float RefreshInterval = 2f;

        public static bool Available => _ctrlType != null;

        // Snapshot of one live BP controller for the on-load penis-bend re-assert.
        public struct BpMale
        {
            public Component chaControl;    // CharaCustomFunctionController.ChaControl (the male body)
            public bool danTargetsValid;    // BP finished its load re-init (danTargets re-resolved)
        }

        // The character BetterPenetration currently has this penis bound to (its collision agent), or null.
        // This is the pairing BP itself holds, so it stays right even when the aim constraint is missing -
        // which is exactly the case when someone deletes the links to re-make them.
        private static FieldInfo _fCollisionAgent, _fCollisionChar;
        public static Component CollisionCharacterOf(Component maleCc)
        {
            Init();
            if (_ctrlType == null || maleCc == null) return null;
            try
            {
                var ctl = maleCc.GetComponent(_ctrlType);
                if (ctl == null) return null;
                if (_fCollisionAgent == null) _fCollisionAgent = _ctrlType.GetField("collisionAgent", BF);
                object agent = _fCollisionAgent != null ? _fCollisionAgent.GetValue(ctl) : null;
                if (agent == null) return null;
                if (_fCollisionChar == null) _fCollisionChar = agent.GetType().GetField("m_collisionCharacter", BF);
                return _fCollisionChar != null ? _fCollisionChar.GetValue(agent) as Component : null;
            }
            catch { return null; }
        }

        // Does this cached controller belong to a character that actually has a penis mesh?
        private static bool OwnsPenis(int i)
        {
            return i >= 0 && i < _hasPenis.Length && _hasPenis[i];
        }

        // ── RE-ARM BP's ORIFICE AFTER A SCENE LOAD ────────────────────────────────────────
        // Studio BP decides "this is vaginal" in a Harmony patch on NodesConstraints.AddConstraint: when
        // a constraint whose CHILD is k_f_dan_entry is ADDED, it calls SetCollisionAgent with
        // isKokan = (parent.name == "cf_J_Vagina_root"). Adding that constraint is what wakes BP up -
        // which is exactly what was found by hand in-game.
        //
        // On LOAD it does not survive. SetCollisionAgent opens with
        //     if (danAgent != null && controllerOptions != null && danTargetsValid)
        // and NodesConstraints restores the scene's constraints BEFORE BP's dan agent exists, so every
        // one of those calls silently does nothing. BP then re-initialises its agent (resetDelay ->
        // ReinitializeControllers) but nothing re-fires the constraint-added path for constraints that
        // are already there, so isKokan stays false and BP reports no depth at all - measured as
        // bpRaw = 0.000 for a whole run.
        //
        // So it is re-fired once BP is ready. GATED ON A CLOXRAY WOMB: only when the female has one,
        // womb"): the female named by the constraint must be wearing a CloXray womb, so a scene that has
        // nothing to do with this mod is never touched, per the zero-effect rule.
        private static MethodInfo _mSetCollisionAgent;

        /// Re-assert "penetrating THIS female vaginally" on every BP male whose k_f_dan_entry is
        /// constrained to a cf_J_Vagina_root belonging to a womb-wearing female. Returns how many it
        /// re-armed. Safe to call repeatedly: BP rebuilds its collision agent each time, and only
        /// call it for pairs that are already wired that way in the scene.
        public static int ReArmVaginalTargets(Func<Component, bool> femaleWearsOurWomb, bool report = true)
        {
            Init();
            if (_ctrlType == null || femaleWearsOurWomb == null) return 0;
            if (_mSetCollisionAgent == null)
            {
                _mSetCollisionAgent = _ctrlType.GetMethod("SetCollisionAgent", BF, null,
                    new[] { typeof(ChaControl), typeof(bool), typeof(bool), typeof(bool) }, null);
                if (_mSetCollisionAgent == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("BPBridge: BetterPenetrationController.SetCollisionAgent"
                        + "(ChaControl,bool,bool,bool) not found - cannot re-arm BP's orifice. "
                        + "Delete and re-add the cf_J_Vagina_root -> k_f_dan_entry constraint by hand instead.");
                    return 0;
                }
            }

            int armed = 0;
            UnityEngine.Object[] ctrls;
            try { ctrls = UnityEngine.Object.FindObjectsOfType(_ctrlType); } catch { return 0; }
            // say what happened to EVERY controller. Logging only successes made "0 armed"
            // and "never ran" look identical in the log, which is the hole this closes.
            // again. Diagnostic-log gated; one block, not per-frame.
            var rep = (report && LiquidWobbleMPBPlugin.CfgDebugLog) ? new System.Text.StringBuilder("CloXray: BP re-arm scan:") : null;
            foreach (var o in ctrls)
            {
                if (o == null) continue;
                var ctlComp = o as Component;
                if (ctlComp == null) continue;
                string who = ctlComp.name;
                bool valid = false;
                try { if (_danTargetsValid != null) valid = Convert.ToBoolean(_danTargetsValid.GetValue(o)); }
                catch { }
                if (!valid) { rep?.AppendLine().Append("   ").Append(who).Append(": SKIP - danTargetsValid false (BP not ready)"); continue; }

                Transform entry = FindDanEntry(ctlComp.transform);
                if (entry == null) { rep?.AppendLine().Append("   ").Append(who).Append(": SKIP - no k_f_dan_entry under it (not a penis-bearing controller)"); continue; }

                Transform vagina = NodeConstraintBridge.DriverNamed(entry, "cf_J_Vagina_root");
                if (vagina == null)
                {
                    Transform any = NodeConstraintBridge.ParentOfChild(entry);
                    rep?.AppendLine().Append("   ").Append(who).Append(": SKIP - k_f_dan_entry has no cf_J_Vagina_root constraint")
                        .Append(any != null ? " (its driver is '" + any.name + "')" : " (it has no constraint at all)");
                    continue;
                }

                var her = vagina.GetComponentInParent<ChaControl>();
                if (her == null) { rep?.AppendLine().Append("   ").Append(who).Append(": SKIP - that cf_J_Vagina_root has no ChaControl above it"); continue; }
                if (!femaleWearsOurWomb(her)) { rep?.AppendLine().Append("   ").Append(who).Append(": SKIP - '").Append(her.name).Append("' wears no CloXray womb (gate)"); continue; }

                try
                {
                    _mSetCollisionAgent.Invoke(o, new object[] { her, true, false, false });
                    armed++;
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: re-armed BetterPenetration on '"
                        + ctlComp.name + "' -> '" + her.name + "' (vaginal).");
                }
                catch (Exception e)
                { LiquidWobbleMPBPlugin._logger?.LogWarning("BPBridge: SetCollisionAgent failed on '" + ctlComp.name + "': " + e.Message); }
            }
            if (rep != null)
            {
                rep.AppendLine().Append("   => re-armed ").Append(armed).Append(" of ").Append(ctrls.Length).Append(" controller(s)");
                LiquidWobbleMPBPlugin._logger?.LogWarning(rep.ToString());
            }
            return armed;
        }

        private static Transform FindDanEntry(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == "k_f_dan_entry") return t;
            return null;
        }

        // Enumerate every live BetterPenetrationController as (ChaControl, danTargetsValid). Used by the scene-load
        // coroutine to (a) wait until BP re-init completes (all danTargetsValid) and (b) drive each male's penis FK.
        // Returns an empty list (never null) when BP isn't installed -> caller no-ops. Does its own FindObjectsOfType
        // (NOT the 2s _cache, which is keyed to the per-frame read path) so the load coroutine sees fresh state.
        public static System.Collections.Generic.List<BpMale> EnumerateMales()
        {
            var outList = new System.Collections.Generic.List<BpMale>();
            Init();
            if (_ctrlType == null) return outList;
            UnityEngine.Object[] ctrls;
            try { ctrls = UnityEngine.Object.FindObjectsOfType(_ctrlType); } catch { return outList; }
            foreach (var o in ctrls)
            {
                if (o == null) continue;
                Component cc = null;
                bool valid = false;
                try
                {
                    if (_chaControlProp != null) cc = _chaControlProp.GetValue(o, null) as Component;
                    if (_danTargetsValid != null) valid = Convert.ToBoolean(_danTargetsValid.GetValue(o));
                }
                catch { }
                outList.Add(new BpMale { chaControl = cc, danTargetsValid = valid });
            }
            return outList;
        }

        // True only if this ChaControl has a BetterPenetrationController that is currently DRIVING the penis
        // (danTargetsValid == BP's load re-init finished). Used to gate the FK enforcer: the dan chain is handed to
        // BP ONLY when BP is actually enabled on this male - a male with FK-posed (non-BP) penis is left untouched.
        public static bool HasActiveBp(Component chaControl)
        {
            if (chaControl == null) return false;
            foreach (var m in EnumerateMales())
                if ((UnityEngine.Object)m.chaControl == (UnityEngine.Object)chaControl && m.danTargetsValid) return true;
            return false;
        }

        private static MethodInfo _miInitDan, _miSetCollision, _miReleaseFK;

        // Which BetterPenetration is installed. Two methods mark a build that handles these itself:
        //   DanBoneAlreadyConstrained -> BP refuses to re-add a dan constraint that already exists
        //   ReleaseDanBonesFromFK     -> BP patches OCIChar.ActiveFK itself and frees the dan bones
        // Each CloXray equivalent installs only where BP lacks its counterpart: BP's duplicate guard
        // is per bone, and two postfixes on OCIChar.ActiveFK would do the same work twice. Re-checked
        // until BP's lazily-loaded assembly appears; the result is logged once.
        private static int _bpCaps = -1;   // bit0 = own dup guard, bit1 = own FK release
        private static int BpCaps
        {
            get
            {
                if (_bpCaps >= 0) return _bpCaps;
                Init();
                if (_ctrlType == null) return 0;   // BP not loaded yet - ask again next call
                int caps = 0;
                if (_ctrlType.GetMethod("DanBoneAlreadyConstrained", BF) != null) caps |= 1;
                if (_ctrlType.GetMethod("ReleaseDanBonesFromFK", BF) != null) caps |= 2;
                _bpCaps = caps;
                LiquidWobbleMPBPlugin._logger?.LogInfo("BPBridge: BetterPenetration handles the dan-duplicate guard itself: "
                    + ((caps & 1) != 0 ? "YES" : "no") + "; releases the dan bones from FK itself: " + ((caps & 2) != 0 ? "YES" : "no")
                    + " (CloXray's own copy of each is installed only where BP does not).");
                return _bpCaps;
            }
        }
        internal static bool BpHasOwnDanDupGuard { get { return (BpCaps & 1) != 0; } }
        internal static bool BpHasOwnDanFK      { get { return (BpCaps & 2) != 0; } }
        private static bool _oldBpLogged;

        // Frees the penis dan bones from Studio FK so BP can bend the chain: BP's own public
        // ReleaseDanBonesFromFK, maintained upstream, idempotent, gated on its dan agent being live.
        // Returns false when nothing was done.
        // CloXray carried its own equivalent until BP 5.1 merged the fix; 5.1 is now the minimum, so
        // the work-around is gone and an older BP is reported rather than silently limped along.
        public static bool ReleaseDanFK(Component maleCc, out string info)
        {
            info = null;
            if (maleCc == null) return false;
            if (!BpHasOwnDanFK)
            {
                if (!_oldBpLogged)
                {
                    _oldBpLogged = true;
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: this BetterPenetration has no ReleaseDanBonesFromFK - it is older than 5.1, which CloXray now requires. The penis bend will stay kinked after a scene load until BP is updated.");
                }
                info = "BetterPenetration older than 5.1";
                return false;
            }
            Init();
            try
            {
                var ctl = maleCc.GetComponent(_ctrlType);
                if (ctl == null) return false;
                if (_miReleaseFK == null) _miReleaseFK = _ctrlType.GetMethod("ReleaseDanBonesFromFK", BF);
                if (_miReleaseFK == null) { LiquidWobbleMPBPlugin._logger?.LogError("BPBridge: ReleaseDanBonesFromFK vanished from BetterPenetration - the penis may stay pinned by Studio FK."); return false; }
                _miReleaseFK.Invoke(ctl, new object[] { null });
                info = "BP released the dan bones from FK";
                return true;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("BPBridge: BP's ReleaseDanBonesFromFK threw on '" + maleCc.name + "': " + e.Message); return false; }
        }

        // danTargetsValid for ONE male's controller (false when BP has cleared/not yet re-initialized).
        public static bool DanTargetsValidFor(Component maleCc)
        {
            Init();
            if (_ctrlType == null || maleCc == null || _danTargetsValid == null) return false;
            try
            {
                var ctl = maleCc.GetComponent(_ctrlType);
                return ctl != null && Convert.ToBoolean(_danTargetsValid.GetValue(ctl));
            }
            catch { return false; }
        }

        // Re-binds the female collision agent on a male's BP controller. An uncensor body reload runs
        // BP's ClearDanAgent (its prefix on UncensorSelector.ReloadCharacterBody) and BP's own rebuild
        // re-binds the female ONLY inside AddDanConstraints - which BPDanBones blocks whenever the
        // dan links already exist (re-made here). This is the SetCollisionAgent call that blocked
        // rebuild would have made: without it the penis no longer deforms/opens her vagina until the
        // uncensor is flicked by hand. Idempotent - BP removes old colliders before re-adding.
        public static bool RebindCollisionAgent(Component maleCc, Component femaleCc, bool kokan, bool ana)
        {
            Init();
            if (_ctrlType == null || maleCc == null || femaleCc == null) return false;
            try
            {
                var ctl = maleCc.GetComponent(_ctrlType);
                if (ctl == null) return false;
                if (_miInitDan == null) _miInitDan = _ctrlType.GetMethod("InitializeDanAgent", BF);
                if (_miSetCollision == null) _miSetCollision = _ctrlType.GetMethod("SetCollisionAgent", BF);
                if (_miInitDan == null || _miSetCollision == null)
                { LiquidWobbleMPBPlugin._logger?.LogError("BPBridge: InitializeDanAgent/SetCollisionAgent not found - cannot re-bind the vagina collision (BP version mismatch?)."); return false; }
                bool valid = _danTargetsValid != null && Convert.ToBoolean(_danTargetsValid.GetValue(ctl));
                if (!valid)
                {
                    _inRebind = true;   // this init must not re-enter via the DanInitPostfix
                    try { _miInitDan.Invoke(ctl, null); } finally { _inRebind = false; }
                }
                _miSetCollision.Invoke(ctl, new object[] { femaleCc, kokan, ana, false });
                LiquidWobbleMPBPlugin._logger?.LogInfo("BPBridge: collision agent re-bound - '" + maleCc.name + "' deforms '" + femaleCc.name + "' again (" + (kokan ? "kokan" : ana ? "ana" : "none") + "; the body reload had cleared it).");
                return true;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("BPBridge: collision re-bind failed on '" + maleCc.name + "': " + e.Message); return false; }
        }

        internal static bool _inRebind;
        private static HarmonyLib.Harmony _danInitHarmony;
        private static bool _danInitHooked;

        // Postfix on BetterPenetrationController.InitializeDanAgent - the one call BOTH of BP's rebuild
        // paths go through (cardReloaded in Update, and ReinitializeControllers after an uncensor
        // reload). Every rebuild starts with ClearDanAgent, wiping the female collision binding, and
        // BP's own re-bind lives in the AddDanConstraints call BPDanBones blocks - so ours is
        // re-asserted right after each rebuild instead of waiting out BP's delays on a timer.
        public static void InstallDanInitWatch()
        {
            if (_danInitHooked) return;
            Init();
            if (_ctrlType == null) return;
            _danInitHooked = true;
            try
            {
                var m = _ctrlType.GetMethod("InitializeDanAgent", BF);
                if (m == null) { LiquidWobbleMPBPlugin._logger?.LogError("BPBridge: InitializeDanAgent not found - collision re-binds cannot follow BP rebuilds."); return; }
                if (_danInitHarmony == null) _danInitHarmony = new HarmonyLib.Harmony("Clo.LiquidWobbleMPB.bpdaninit");
                _danInitHarmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(BPBridge).GetMethod(nameof(DanInitPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                LiquidWobbleMPBPlugin._logger?.LogInfo("BPBridge: hooked InitializeDanAgent (collision re-bind follows every BP rebuild).");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("BPBridge: InitializeDanAgent hook failed: " + e.Message); }
        }

        private static void DanInitPostfix(object __instance)
        {
            if (_inRebind) return;
            try
            {
                Component cc = _chaControlProp != null ? _chaControlProp.GetValue(__instance, null) as Component : null;
                WobbleSceneController.ReassertBpBindings(cc);
            }
            catch { }
        }

        // The penis shaft bone NAMES (cm_j_dan*, excluding balls) on a male - used to scope the FK disable to ONLY
        // the penis bones (same filter as the tip scan in TryReadCore's cache refresh).
        public static System.Collections.Generic.HashSet<string> DanBoneNames(Component chaControl)
        {
            var set = new System.Collections.Generic.HashSet<string>();
            if (chaControl == null) return set;
            foreach (var t in chaControl.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                string nl = t.name.ToLowerInvariant();
                if (nl.StartsWith("cm_j_dan") && !nl.Contains("tama")) set.Add(t.name);
            }
            return set;
        }

        private static void Init()
        {
            if (_tried) return;
            // BP's Core_BetterPenetration assembly loads LAZILY - only when the first BP character spawns. Callers
            // (the scene-load penis-bend coroutine, the per-frame read) can hit this BEFORE that happens, so it must
            // NOT latch failure: re-scan (throttled ~1/s) until BP appears, then bind once. A single latched early
            // failure used to poison BPBridge for the whole session - womb expansion AND penis-bend both went dead.
            if (UnityEngine.Time.realtimeSinceStartup < _nextScan) return;
            _nextScan = UnityEngine.Time.realtimeSinceStartup + 1f;

            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = a.GetType("Core_BetterPenetration.BetterPenetrationController", false);
                    if (t != null) { _ctrlType = t; break; }
                }
                catch { }
            }
            if (_ctrlType == null)
            {
                if (!_bpMissingLogged) { _bpMissingLogged = true; LiquidWobbleMPBPlugin._logger?.LogInfo("BPBridge: BetterPenetration not present yet — watching for a BP character (no-op until then)."); }
                return;   // do NOT set _tried: retry on the next call (throttled) so a lazy BP load is picked up
            }
            _tried = true;   // bound successfully - stop scanning

            _fDan = _ctrlType.GetField("danAgent", BF);
            var danT = _fDan != null ? _fDan.FieldType : null;
            _danDist      = danT != null ? danT.GetField("lastDanDistance", BF) : null;
            _danBaseLen   = danT != null ? danT.GetField("m_baseDanLength", BF) : null;
            _danRad       = danT != null ? danT.GetField("m_danColliderRadius", BF) : null;
            _danColliders = danT != null ? danT.GetField("m_danColliders", BF) : null;
            _danOptions   = danT != null ? danT.GetField("m_danOptions", BF) : null;
            var optT = _danOptions != null ? _danOptions.FieldType : null;
            _optRadiusScale     = optT != null ? optT.GetField("danRadiusScale", BF) : null;
            _optLengthSquish    = optT != null ? optT.GetField("danLengthSquish", BF) : null;
            _optGirthSquish     = optT != null ? optT.GetField("danGirthSquish", BF) : null;
            _optSquishThreshold = optT != null ? optT.GetField("squishThreshold", BF) : null;
            _danTargetsValid    = _ctrlType.GetField("danTargetsValid", BF);                 // public bool - BP load re-init done
            _chaControlProp     = _ctrlType.GetProperty("ChaControl", BF);                   // CharaCustomFunctionController.ChaControl

            LiquidWobbleMPBPlugin._logger?.LogInfo(
                $"BPBridge: hooked BP. dist={_danDist != null} baseLen={_danBaseLen != null} " +
                $"radius={_danRad != null} options={_danOptions != null} " +
                $"lenSquish={_optLengthSquish != null} girthSquish={_optGirthSquish != null} thresh={_optSquishThreshold != null}");
        }

        public struct Reading
        {
            public bool found;
            public float depth;        // raw penetration 0..1 (engage/squish math)
            public float visualDepth;  // length-squished (rendered) tip depth -> drives rings/stretch
            public float girthBase;    // avg collider radius * danRadiusScale (maker+studio width)
            public float girthFactor;  // deep-press fattening multiplier (>=1)
            public float dist;         // raw lastDanDistance (diagnostic)
            public float baseLen;      // m_baseDanLength (diagnostic)
            public float tipAhead;     // metres the penis MESH reaches past the tip bone the depth is measured from
            public Vector3 tipPos;     // DEBUG: world pos of the tip (last) collider
            public Vector3 tipDir;     // DEBUG: base->tip unit direction
            public bool    hasPose;    // DEBUG: true when >=2 colliders gave a world pose
            public Vector3[] shaftPos; // WORLD positions of every REAL cm_j_dan* shaft bone (base..tip) for the matched
                                       // male - lets the womb test the whole bent shaft, not just the curl-prone tip.
            public Vector3 aimedTipPos;// k_f_dan_end (the aimed/constraint-driven tip). CONTAINMENT signal only - it's
            public bool aimedValid;    // glued to penis_target when the penis is hand-aimed, so it is NOT a depth signal.
            public float girthTip;     // WORLD radius of the TIP collider = the girth the womb actually contacts (the
                                       // glans pushing the cervix). Fallback when the full profile isn't available.
            public Vector3[] girthPos; // per-collider WORLD position (base..tip) and paired WORLD radius = the penis
            public float[]   girthRad; // GIRTH PROFILE (m_danColliderRadius*danRadiusScale*lossyScale). The womb maps
                                       // each ring to the profile by DISTANCE-FROM-TIP (the penis part at that ring),
                                       // so the canal tapers with the penis. Mapping-by-POSITION was the dead end (the
                                       // off-centre womb put every collider below its entrance) - distance-from-tip
                                       // uses the penis's INTRINSIC curve + insertion depth, no shared-axis needed.
            public string srcName;     // DEBUG: the paired BP controller's character name (which penis won the pairing)
            public Vector3 entryPos;   // DEBUG: the paired penis's k_f_dan_entry world position (the node the pairing matched)
        }

        // Perf, following the profiling findings: FieldInfo.GetValue boxes every numeric read - these
        // helpers run per frame per womb, so compile a typed accessor ONCE per field (net35 has
        // System.Linq.Expressions; KK/KKS are desktop Mono JIT). A field whose type can't compile to a
        // float conversion keeps the reflective path from bind time on - a capability decision made
        // once, not a runtime fallback.
        private static readonly System.Collections.Generic.Dictionary<FieldInfo, Func<object, float>> _fGetters
            = new System.Collections.Generic.Dictionary<FieldInfo, Func<object, float>>();
        private static float ReadF(FieldInfo fi, object o, float def)
        {
            if (fi == null || o == null) return def;
            Func<object, float> g;
            if (!_fGetters.TryGetValue(fi, out g))
            {
                try
                {
                    var pp = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
                    g = System.Linq.Expressions.Expression.Lambda<Func<object, float>>(
                            System.Linq.Expressions.Expression.Convert(
                                System.Linq.Expressions.Expression.Field(
                                    System.Linq.Expressions.Expression.Convert(pp, fi.DeclaringType), fi),
                                typeof(float)),
                            pp).Compile();
                }
                catch { g = null; }
                _fGetters[fi] = g;
            }
            if (g != null) { try { return g(o); } catch { return def; } }
            try { return Convert.ToSingle(fi.GetValue(o)); } catch { return def; }
        }

        // Picks the most-penetrated controller GLOBALLY (legacy - correct only with a single
        // penis/womb pair in the scene). Prefer TryReadLocked for anything per-womb.
        public static bool TryRead(out Reading r)
        {
            return TryReadCore(out r, false, Vector3.zero, 0f, null);
        }

        // Refresh the BP controller cache + per-controller bone handles (k_f_dan_entry/end, deepest dan, shaft chain)
        // on a timer / when an entry went null. Shared by the readers + the entry matcher.
        private static int _cacheTickFrame = -1;
        // TIP-GEOM. How far the penis MESH reaches beyond the bone the depth is measured from. The
        // depth chain uses the deepest cm_j_dan* shaft bone as the tip, and that bone sits inside the
        // glans, not at its front - so the reading is short by this distance and the rings open late by
        // it. It is a rig/mesh property, not a feel value, so it is measured once per male here.
        //
        // Only vertices actually weighted to the tip bone are considered: the penis can share a
        // renderer with the body, and the body would otherwise supply the furthest vertex.
        private static float MeasureTipGeometry(Component cc, Transform danTip)
        {
            if (cc == null || danTip == null) return 0f;
            int key = cc.GetInstanceID();
            float cached;
            if (_tipAheadCache.TryGetValue(key, out cached)) return cached;
            var parent = danTip.parent;
            if (parent == null) return 0f;
            Vector3 axis = danTip.position - parent.position;
            if (axis.sqrMagnitude < 1e-10f) return 0f;
            axis.Normalize();
            float best = 0f; bool measured = false;

            var smrs = cc.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int s = 0; s < smrs.Length; s++)
            {
                var smr = smrs[s];
                if (smr == null || smr.sharedMesh == null || smr.bones == null) continue;
                int bi = -1;
                for (int b = 0; b < smr.bones.Length; b++)
                    if (smr.bones[b] == danTip) { bi = b; break; }
                if (bi < 0) continue;

                var bw = smr.sharedMesh.boneWeights;
                if (bw == null || bw.Length == 0) continue;
                var baked = new Mesh();
                try
                {
                    smr.BakeMesh(baked);
                    var verts = baked.vertices;
                    if (verts.Length != bw.Length) { UnityEngine.Object.Destroy(baked); continue; }
                    int used = 0; float ahead = float.NegativeInfinity, lateral = 0f;
                    for (int v = 0; v < verts.Length; v++)
                    {
                        var w = bw[v];
                        float wt = (w.boneIndex0 == bi ? w.weight0 : 0f) + (w.boneIndex1 == bi ? w.weight1 : 0f)
                                 + (w.boneIndex2 == bi ? w.weight2 : 0f) + (w.boneIndex3 == bi ? w.weight3 : 0f);
                        if (wt < 0.5f) continue;
                        used++;
                        Vector3 d = smr.transform.TransformPoint(verts[v]) - danTip.position;
                        float along = Vector3.Dot(d, axis);
                        if (along > ahead) ahead = along;
                        float lat = (d - axis * along).magnitude;
                        if (lat > lateral) lateral = lat;
                    }
                    if (used > 0)
                    {
                        // Only a renderer that is actually being drawn defines where the front is. This is
                        // what makes a worn condom count and a hidden one not.
                        bool live = smr.enabled && smr.gameObject.activeInHierarchy;
                        if (live && ahead > best) { best = ahead; measured = true; }
                        if (LiquidWobbleMPBPlugin.CfgDebugLog)
                            LiquidWobbleMPBPlugin._logger?.LogInfo(
                                "TIP-GEOM '" + cc.gameObject.name + "' mesh='" + smr.name
                                + "' tipBone='" + danTip.name + "' verts=" + used
                                + " meshReachesPastBone=" + (ahead * 1000f).ToString("F1") + "mm"
                                + " maxLateralRadius=" + (lateral * 1000f).ToString("F1") + "mm"
                                + (live ? " ACTIVE" : " hidden"));
                    }
                }
                finally { UnityEngine.Object.Destroy(baked); }
            }
            if (!measured)
            {
                // Diagnostic only. This was a LogError back when the measurement DROVE the depth; nothing
                // reads it now, so a missing mesh changes no behaviour and must not be reported
                // as a fault. Kept as a note because it still says the probe found no drawn penis mesh.
                if (LiquidWobbleMPBPlugin.CfgDebugLog)
                    LiquidWobbleMPBPlugin._logger?.LogInfo(
                        "CloXray TIP-GEOM: no active penis mesh weighted to '" + danTip.name + "' on '"
                        + cc.gameObject.name + "' - nothing measured. Diagnostic only; no behaviour depends on it.");
            }
            _tipAheadCache[key] = best;
            return best;
        }

        private static void EnsureCache()
        {
            if (Time.frameCount != _cacheTickFrame) { _refreshTimer -= Time.deltaTime; _cacheTickFrame = Time.frameCount; }   // tick the timer ONCE per frame however many readers call in
            bool stale = _refreshTimer <= 0f || _cache.Length == 0;
            if (!stale)
                for (int i = 0; i < _cache.Length; i++)
                    if (_cache[i] == null) { stale = true; break; }
            if (!stale) return;
            _cache = UnityEngine.Object.FindObjectsOfType(_ctrlType);
            _hasPenis = new bool[_cache.Length];             // real penis mesh present (the pairing candidate test)
            _tipBones = new Transform[_cache.Length];        // k_f_dan_end (BP target marker)
            _entryBones = new Transform[_cache.Length];      // k_f_dan_entry (ENTRY marker - the pairing key)
            _danTipBones = new Transform[_cache.Length];     // DEEPEST cm_j_dan* shaft bone = visual mesh tip
            _tipAhead = new float[_cache.Length];
            _danShaftBones = new Transform[_cache.Length][]; // each controller // full shaft chain (base..tip)
            for (int ci = 0; ci < _cache.Length; ci++)
            {
                var cc = _cache[ci] as Component;
                if (cc == null) continue;
                _hasPenis[ci] = MEBridge.HasVisiblePenisMesh(cc);
                Transform deepest = null; int bestDanDepth = -1;
                var shaft = new System.Collections.Generic.List<Transform>();
                foreach (var t in cc.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null) continue;
                    if (t.name == "k_f_dan_end") _tipBones[ci] = t;
                    if (t.name == "k_f_dan_entry") _entryBones[ci] = t;
                    string nl = t.name.ToLowerInvariant();
                    if (!nl.StartsWith("cm_j_dan") || nl.Contains("tama")) continue;   // penis shaft bones only (exclude balls)
                    int depth = 0; var p = t.parent;
                    while (p != null) { var pl = p.name.ToLowerInvariant(); if (pl.StartsWith("cm_j_dan") && !pl.Contains("tama")) depth++; p = p.parent; }
                    if (depth > bestDanDepth) { bestDanDepth = depth; deepest = t; }
                    shaft.Add(t);
                }
                _danTipBones[ci] = deepest;
                _danShaftBones[ci] = shaft.ToArray();
                // Diagnostic only - nothing reads tipAhead any more, so it does not bake in a normal run.
                if (LiquidWobbleMPBPlugin.CfgDebugLog) _tipAhead[ci] = MeasureTipGeometry(cc, deepest);
            }
            _refreshTimer = RefreshInterval;
            if (LiquidWobbleMPBPlugin.CfgDebugLog)
            {
                var sb = new System.Text.StringBuilder("BP-CACHE: " + _cache.Length + " BP controller(s); entry/danTip=");
                for (int ci = 0; ci < _cache.Length; ci++)
                {
                    var oc = _cache[ci] as Component;
                    sb.Append('[').Append(oc != null ? oc.gameObject.name : "?").Append(" entry=")
                      .Append(_entryBones[ci] != null ? _entryBones[ci].position.ToString("F3") : "none").Append(']');
                }
                LiquidWobbleMPBPlugin._logger?.LogInfo(sb.ToString());
            }
        }

        // PAIRING (call on scene-load / a slow timer, NOT per-frame): returns the MALE controller whose k_f_dan_entry
        // marker is nearest the womb's entrance - the NodesConstraints `cf_J_Vagina_root -> k_f_dan_entry` pins that
        // marker at the womb mouth, so its position alone decides which penis belongs to which womb (no shaft scan).
        // The womb caches the returned name and reads ONLY that penis per-frame via TryReadLocked.
        // STICKY: hold the current pairing (`currentName`) while ITS entry is still attached - within `stickyHold` of the
        // womb mouth. BP drags the vagina (and the pinned k_f_dan_entry) toward the penis on a thrust, and the entry
        // shifts on withdrawal; staying attached means a thrust/withdraw can't flip the womb to a neighbour. Only
        // re-pair to the nearest male when the current entry has moved AWAY (NC re-aimed to another womb, or toggled off
        // -> the marker jumps back to the penis base). Logs every candidate's entry distance for alignment debugging.
        // `wearer` is the character this womb sits in - she can never be the one penetrating it, and her own
        // vestigial entry marker sits at her crotch, i.e. exactly where the womb mouth is, so without this she
        // would win the lateral test outright.
        public static string FindByEntry(Vector3 wombEntrance, Vector3 wombAxis, float maxRange, string currentName, float stickyMargin, Component wearer)
        {
            Init();
            if (_ctrlType == null) return currentName;
            EnsureCache();
            // Match by LATERAL offset from the womb's CHANNEL AXIS, not raw 3D distance to the mouth. The entry node always
            // sits BELOW the mouth, down the channel (NC shift + how the womb seats in the body) - and "below" rotates with
            // the womb. So the entry that lies ON this womb's axis line (smallest perpendicular distance), at or below the
            // mouth, is this womb's penis - even if a neighbour's entry is a closer raw point.
            string best = null; float bestLat = maxRange; float curLat = float.PositiveInfinity;
            System.Text.StringBuilder dbg = LiquidWobbleMPBPlugin.CfgDebugLog ? new System.Text.StringBuilder() : null;
            for (int i = 0; i < _cache.Length; i++)
            {
                var oc = _cache[i] as Component;
                if (oc == null) continue;
                string nm = oc.gameObject.name;
                if (wearer != null && oc.gameObject == wearer.gameObject) continue;   // her own womb
                // Pairing runs only on events, and while no womb is paired nothing reads the cache per frame, so its
                // refresh timer stands still and _hasPenis can be minutes old - a penis shown since then (the hotkey
                // unhides the one it claims) would never be seen. Ask the live question here, and store the answer
                // so the per-frame read of the paired male agrees with it.
                bool visible = MEBridge.HasVisiblePenisMesh(oc);
                if (i < _hasPenis.Length) _hasPenis[i] = visible;
                if (!visible) continue;
                Transform entry = (i < _entryBones.Length) ? _entryBones[i] : null;
                if (entry == null) continue;
                Vector3 rel = entry.position - wombEntrance;
                float along = Vector3.Dot(rel, wombAxis);                 // + = up the channel (into the uterus), - = below the mouth (expected)
                float lateral = (rel - wombAxis * along).magnitude;       // perpendicular distance from the channel axis line
                bool valid = along <= 0.05f && along >= -maxRange && lateral <= maxRange;   // at/below the mouth, on-axis, in range
                if (dbg != null) dbg.Append(nm).Append("(lat=").Append(Mathf.RoundToInt(lateral * 1000f)).Append(",alng=").Append(Mathf.RoundToInt(along * 1000f)).Append("mm) ");
                if (!valid) continue;
                if (nm == currentName) curLat = lateral;
                if (lateral < bestLat) { bestLat = lateral; best = nm; }
            }
            // STICKY but RELATIVE: hold the current pairing unless a challenger's entry is CLEARLY more on-axis (its
            // lateral is smaller by MORE than stickyMargin). A thrust nudges laterals by only a few mm -> no flip; a
            // wrong initial pick or a re-aim makes the right penis clearly better -> switch. (An ABSOLUTE threshold held
            // a 57mm off-axis penis over the 26mm on-axis one - that was the "grabbed the wrong penis" bug.)
            bool holdCur = currentName != null && curLat < bestLat + stickyMargin;
            string chosen = holdCur ? currentName : best;
            if (dbg != null)
                LiquidWobbleMPBPlugin._logger?.LogInfo($"  BP-PAIR mouth={wombEntrance.ToString("F3")} axis={wombAxis.ToString("F2")} entries=[{dbg}] cur={(currentName ?? "-")}(lat {(curLat < 1e8f ? Mathf.RoundToInt(curLat * 1000f) + "mm" : "gone")}) hold={holdCur} -> {(chosen ?? "NONE")}");
            return chosen;
        }

        // nearest MALE BP controller to a point, by k_f_dan_entry (or root) proximity, within
        // maxRange. Unlike FindByEntry this does NOT require the penis to be ON the canal axis / at the mouth
        // - it just finds the penetrator that is POSITIONED near the womb, so the womb can read that male's
        // girth (static collider radii) at SPAWN, before penetration. Returns null if none in range.
        public static string FindNearestMaleName(Vector3 point, float maxRange, Component wearer = null)
        {
            Init();
            if (_ctrlType == null) return null;
            EnsureCache();
            string best = null; float bestD = maxRange * maxRange;
            for (int i = 0; i < _cache.Length; i++)
            {
                var oc = _cache[i] as Component;
                if (oc == null) continue;
                if (wearer != null && oc.gameObject == wearer.gameObject) continue;   // her own womb
                string nm = oc.gameObject.name;
                if (!OwnsPenis(i)) continue;   // a real penis mesh, not a name: female-bodied penetrators count
                Transform entry = (i < _entryBones.Length) ? _entryBones[i] : null;
                Vector3 p = entry != null ? entry.position : oc.transform.position;
                float d = (p - point).sqrMagnitude;
                if (d < bestD) { bestD = d; best = nm; }
            }
            return best;
        }

        // POST-NC read of a named male's k_f_dan_entry world position. NodesConstraints drives that bone in LateUpdate,
        // so call this from Camera.onPreCull (after all LateUpdates) to get the DRIVEN position (at the vagina/anus),
        // not the penis-rig default. Vector3.zero if the controller / bone is gone.
        public static Vector3 GetEntryWorld(string charName)
        {
            if (string.IsNullOrEmpty(charName)) return Vector3.zero;
            Init();
            if (_ctrlType == null) return Vector3.zero;
            EnsureCache();
            for (int i = 0; i < _cache.Length; i++)
            {
                var oc = _cache[i] as Component;
                if (oc == null || oc.gameObject.name != charName) continue;
                Transform e = (i < _entryBones.Length) ? _entryBones[i] : null;
                return e != null ? e.position : Vector3.zero;
            }
            return Vector3.zero;
        }

        // The named character's k_f_dan_entry node itself, so NodesConstraints can be asked what drives it.
        // Null if the controller or the bone is gone.
        public static Transform GetEntryBone(string charName)
        {
            if (string.IsNullOrEmpty(charName)) return null;
            Init();
            if (_ctrlType == null) return null;
            EnsureCache();
            for (int i = 0; i < _cache.Length; i++)
            {
                var oc = _cache[i] as Component;
                if (oc == null || oc.gameObject.name != charName) continue;
                return (i < _entryBones.Length) ? _entryBones[i] : null;
            }
            return null;
        }

        // POST-NC read of a named male's k_f_dan_end (the AIMED tip - driven deep by a penis_target NodesConstraint).
        // Call from onPreCull. Vector3.zero if gone. (The cm_j_dan deepest bone gets squished shallow by BP; this is
        // the actual deep visual tip when the penis is aimed via NC.)
        // The DEEPEST cm_j_dan shaft bone, read LIVE. GetEndWorld reads k_f_dan_end, which a scene
        // typically pins to penis_target with a constraint - so it looks stable whatever the character
        // does and cannot answer which epoch the penis is in. This one follows the character.
        public static Vector3 GetDanTipWorld(string charName)
        {
            if (string.IsNullOrEmpty(charName)) return Vector3.zero;
            Init();
            if (_ctrlType == null) return Vector3.zero;
            EnsureCache();
            for (int i = 0; i < _cache.Length; i++)
            {
                var oc = _cache[i] as Component;
                if (oc == null || oc.gameObject.name != charName) continue;
                Transform d = (i < _danTipBones.Length) ? _danTipBones[i] : null;
                return d != null ? d.position : Vector3.zero;
            }
            return Vector3.zero;
        }

        public static Vector3 GetEndWorld(string charName)
        {
            if (string.IsNullOrEmpty(charName)) return Vector3.zero;
            Init();
            if (_ctrlType == null) return Vector3.zero;
            EnsureCache();
            for (int i = 0; i < _cache.Length; i++)
            {
                var oc = _cache[i] as Component;
                if (oc == null || oc.gameObject.name != charName) continue;
                Transform e = (i < _tipBones.Length) ? _tipBones[i] : null;
                return e != null ? e.position : Vector3.zero;
            }
            return Vector3.zero;
        }

        // PER-FRAME read of the womb's already-paired penis (by character name). No matching, no scan - read that one
        // controller's live depth/girth/tip. found=false if charName is null/empty or that controller is gone.
        public static bool TryReadLocked(string charName, out Reading r)
        {
            r = new Reading();
            if (string.IsNullOrEmpty(charName)) return false;
            return TryReadCore(out r, true, Vector3.zero, 0f, charName);
        }

        // NEAREST-tip reader (cum slosh / debug overlay only): the controller whose tip is closest to `anchor`.
        // PERF: wantProfile:false - this reader's only callers (the per-frame slosh feed and the debug tip
        // overlay) use depth/tip/girthBase/girthFactor/visualDepth and NEVER touch shaftPos/girthPos/girthRad,
        // so skip those three per-read array allocations. The womb's own read (TryReadLocked) still gets them.
        public static bool TryReadNear(Vector3 anchor, float maxRange, out Reading r)
        {
            return TryReadCore(out r, true, anchor, maxRange, null, false);
        }

        private static bool TryReadCore(out Reading r, bool byNearest, Vector3 anchor, float maxRange, string lockName, bool wantProfile = true)
        {
            r = new Reading();
            Init();
            if (_ctrlType == null) return false;

            EnsureCache();
            if (_cache.Length == 0) return false;
            if (!byNearest) r.found = true;   // legacy semantics: found once any controller exists

            float bestDepth = float.NegativeInfinity;
            float bestDistSq = float.PositiveInfinity;
            for (int i = 0; i < _cache.Length; i++)
            {
                object o = _cache[i];
                // Per-womb pairing: only characters with a REAL penis mesh are candidates. Every female carries
                // vestigial cm_j_dan* bones (a phantom tip at the crotch where the womb sits), so the mesh is the
                // test rather than the character's sex. In LOCK mode read ONLY the womb's already-paired penis.
                if (byNearest)
                {
                    var fc = o as Component;
                    if (fc == null) continue;
                    string cnm = fc.gameObject.name;
                    if (!OwnsPenis(i)) continue;   // a real penis mesh, not a name: female-bodied penetrators count
                    if (lockName != null && cnm != lockName) continue;                                  // LOCK: read only the paired penis
                }
                object dan = (o != null && _fDan != null) ? _fDan.GetValue(o) : null;
                if (dan == null) continue;

                float D = ReadF(_danDist, dan, 0f);
                float L = ReadF(_danBaseLen, dan, 0f);
                if (L <= 1e-5f) continue;
                float depth = Mathf.Clamp01((L - D) / L);

                object opt = _danOptions != null ? _danOptions.GetValue(dan) : null;
                float T  = ReadF(_optSquishThreshold, opt, 0f);
                float S  = ReadF(_optLengthSquish, opt, 0f);
                float GS = ReadF(_optGirthSquish, opt, 0f);
                float RS = _optRadiusScale != null ? ReadF(_optRadiusScale, opt, 1f) : 1f;

                float visual = (depth <= T) ? depth : (T * S + (1f - S) * depth);

                // BP-RAW. The four numbers the whole depth chain is built from, plus the male's WORLD
                // scale: m_baseDanLength is documented as the maker's natural length, so if a Studio
                // scale does not multiply it, a scaled male reports a length he no longer has and any
                // baseLen-based correction is wrong for him.
                var rawC = o as Component;
                int rawKey = (rawC != null) ? rawC.GetInstanceID() : 0;
                float rawNext;
                if (LiquidWobbleMPBPlugin.CfgDebugLog
                    && (!_rawLogNextBy.TryGetValue(rawKey, out rawNext) || Time.unscaledTime >= rawNext))
                {
                    _rawLogNextBy[rawKey] = Time.unscaledTime + 2f;
                    Vector3 ls = (rawC != null) ? rawC.transform.lossyScale : Vector3.one;
                    LiquidWobbleMPBPlugin._logger?.LogInfo(
                        "BP-RAW '" + (rawC != null ? rawC.gameObject.name : "?")
                        + "' L=" + L.ToString("F4") + "m D=" + D.ToString("F4")
                        + "m depth=" + depth.ToString("F4")
                        + " T=" + T.ToString("F3") + " S=" + S.ToString("F2")
                        + " visual=" + visual.ToString("F4")
                        + " advance=" + (visual * L * 1000f).ToString("F1") + "mm"
                        + " charScale=" + ls.y.ToString("F3")
                        // World positions of the bones BP's distance is supposed to describe. If the
                        // character is pinned by a NodesConstraint, editing his coordinates moves nothing
                        // on screen - so these must not change. If D moves while they do not, BP computed
                        // it from the nominal transform, before the constraint pulled him back.
                        + " basePos=" + ((i < _danShaftBones.Length && _danShaftBones[i] != null
                                          && _danShaftBones[i].Length > 0 && _danShaftBones[i][0] != null)
                                         ? _danShaftBones[i][0].position.ToString("F4") : "none")
                        + " tipPos=" + ((i < _danTipBones.Length && _danTipBones[i] != null)
                                        ? _danTipBones[i].position.ToString("F4") : "none")
                        + " endPos=" + ((i < _tipBones.Length && _tipBones[i] != null)
                                        ? _tipBones[i].position.ToString("F4") : "none"));
                }
                float gFactor = 1f + Mathf.Max(0f, depth - T) * GS;

                // Girth = the colliders' WORLD radius (m_Radius * transform.lossyScale). This is
                // what BP's collision actually pushes the vagina with, so it captures studio thickness
                // done via bone scale (which m_danColliderRadius does NOT). Falls back to the stored
                // radius * danRadiusScale if the collider list isn't available.
                float gBase = 0f, gTip = 0f;
                Vector3 firstP = Vector3.zero, lastP = Vector3.zero; int pcnt = 0;   // DEBUG tip pose
                _gPosBuf.Clear(); _gRadBuf.Clear();                                  // per-collider girth PROFILE (pos + world radius)
                var colList = _danColliders != null ? _danColliders.GetValue(dan) as IList : null;
                if (colList != null && colList.Count > 0)
                {
                    float s = 0f; int cnt = 0;
                    for (int ci = 0; ci < colList.Count; ci++)   // indexed (not foreach): IList.GetEnumerator boxes the struct enumerator every frame
                    {
                        var c = colList[ci];
                        if (c == null) continue;
                        if (_colRadius == null) _colRadius = c.GetType().GetField("m_Radius", BF);
                        float rr = ReadF(_colRadius, c, 0f);   // compiled accessor, no boxing
                        float sc = 1f;
                        var comp = c as Component;
                        if (comp != null)
                        {
                            Vector3 ls = comp.transform.lossyScale; sc = Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z));
                            if (pcnt == 0) firstP = comp.transform.position;   // base end (collider 0)
                            lastP = comp.transform.position;                   // tip end (last collider)
                            gTip = rr * sc;                                    // WORLD radius at the tip = the girth the womb actually contacts
                            pcnt++;
                            _gPosBuf.Add(comp.transform.position); _gRadBuf.Add(rr * sc);   // world pos + WORLD radius at this point on the shaft
                        }
                        s += rr * sc; cnt++;
                    }
                    if (cnt > 0) gBase = s / cnt;
                }
                if (gBase <= 1e-6f && _danRad != null) // fallback
                {
                    var list = _danRad.GetValue(dan) as IList;
                    if (list != null && list.Count > 0)
                    {
                        float s = 0f; int n = 0;
                        foreach (var v in list) { s += Convert.ToSingle(v); n++; }
                        if (n > 0) gBase = (s / n) * RS;
                    }
                }

                bool take;
                if (byNearest)
                {
                    if (lockName != null)
                    {
                        take = true;   // LOCK mode: this IS the womb's already-paired penis (the only one reaching here)
                    }
                    else
                    {
                        // NEAREST mode (cum slosh / debug overlay): pick the controller whose tip (or root) is closest
                        // to the anchor. Tip when a collider pose exists, else the character root with a 3x looser range.
                        Vector3 candPos; float allow;
                        if (pcnt >= 1) { candPos = lastP; allow = maxRange; }
                        else
                        {
                            var oc = o as Component;
                            if (oc == null) continue;
                            candPos = oc.transform.position; allow = maxRange * 3f;
                        }
                        float dSq = (candPos - anchor).sqrMagnitude;
                        if (dSq > allow * allow) continue;     // out of range
                        take = dSq < bestDistSq;
                        if (take) bestDistSq = dSq;
                    }
                }
                else
                {
                    take = depth > bestDepth;
                    if (take) bestDepth = depth;
                }

                if (take)
                {
                    r.found          = true;
                    r.srcName        = (o as Component) != null ? ((Component)o).gameObject.name : "?";
                    r.entryPos       = (i < _entryBones.Length && _entryBones[i] != null) ? _entryBones[i].position : Vector3.zero;
                    r.depth          = depth;
                    r.visualDepth    = visual;
                    r.girthBase      = gBase;
                    r.girthFactor    = gFactor;
                    r.dist           = D;
                    r.baseLen        = L;
                    r.tipAhead       = (i < _tipAhead.Length) ? _tipAhead[i] : 0f;
                    // Tip = the REAL penis-tip bone (k_f_dan_end - what the mesh tips to / BP aims), NOT the last
                    // collision capsule centre (that sits well down the shaft -> tip read ~13cm low). Falls back to
                    // the last collider when the bone isn't resolvable. base = first collider; dir = base->tip.
                    // Tip = the DEEPEST cm_j_dan* shaft bone (the visual mesh tip - a real penis bone, so it tracks BP
                    // bend AND squish). k_f_dan_end is a BP TARGET marker that sits at the base/vagina
                    // entrance unless a NodesConstraint drives it to the tip (this scene has none). Fallback: deepest
                    // dan bone -> k_f_dan_end -> last collider.
                    Transform danTip = (i < _danTipBones.Length) ? _danTipBones[i] : null;
                    Transform tb     = (i < _tipBones.Length) ? _tipBones[i] : null;
                    r.tipPos         = danTip != null ? danTip.position : (tb != null ? tb.position : lastP);
                    Vector3 _td      = r.tipPos - firstP;
                    r.tipDir         = (_td.sqrMagnitude > 1e-8f) ? _td.normalized : Vector3.up;
                    r.hasPose        = (danTip != null) || (tb != null) || (pcnt >= 2);
                    // Whole-shaft world positions (REAL cm_j_dan bones only) for the womb's polyline containment +
                    // physical-depth test (handles a bent penis whose geometric tip curls OUT while the shaft is still
                    // inside). k_f_dan_end is kept SEPARATE (aimedTipPos): it's the constraint-driven aim point, glued
                    // to penis_target when the penis is hand-aimed, so it says the penis is AIMED at this womb but
                    // NOT how deep it physically is - the womb uses it for containment only, never as a depth source.
                    var sb2 = (i < _danShaftBones.Length) ? _danShaftBones[i] : null;
                    int sn = (wantProfile && sb2 != null) ? sb2.Length : 0;
                    if (sn > 0)
                    {
                        var sp = new Vector3[sn];
                        for (int s = 0; s < sn; s++) sp[s] = sb2[s] != null ? sb2[s].position : r.tipPos;
                        r.shaftPos = sp;
                    }
                    else r.shaftPos = null;
                    r.aimedValid  = tb != null;
                    r.aimedTipPos = tb != null ? tb.position : r.tipPos;
                    r.girthTip    = gTip > 1e-6f ? gTip : gBase;   // tip girth (fall back to the average if no per-collider read)
                    // girth PROFILE for per-ring taper (the girth SCAN above still runs - girthTip needs it)
                    if (wantProfile && _gPosBuf.Count > 0) { r.girthPos = _gPosBuf.ToArray(); r.girthRad = _gRadBuf.ToArray(); }
                    else { r.girthPos = null; r.girthRad = null; }
                }
            }
            return r.found;
        }
    }

    // ── Generic non-BP penetrator: the nearest DynamicBoneCollider to a womb entrance (e.g. a KKPE-placed
    // BP collider on a bottle/toy). READ-ONLY (BP and the collider are never touched), reflection so there's no
    // hard DynamicBone dependency, and the scan is 2s-cached like BPBridge (no per-frame FindObjectsOfType).
    internal static class ColliderBridge
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool _tried;
        private static Type _colType;
        private static FieldInfo _fCenter, _fRadius, _fHeight, _fDirection;
        private static UnityEngine.Object[] _cache = new UnityEngine.Object[0];
        private static float _refreshTimer;
        private const float RefreshInterval = 2f;

        private static void Init()
        {
            if (_tried) return;
            _tried = true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = a.GetType("DynamicBoneCollider", false); if (t != null) { _colType = t; break; } } catch { }
            }
            if (_colType == null)
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { foreach (var t in a.GetTypes()) if (t != null && t.Name == "DynamicBoneCollider") { _colType = t; break; } } catch { }
                    if (_colType != null) break;
                }
            if (_colType == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("ColliderBridge: DynamicBoneCollider type not found.");
                return;
            }
            _fCenter    = _colType.GetField("m_Center", BF);
            _fRadius    = _colType.GetField("m_Radius", BF);
            _fHeight    = _colType.GetField("m_Height", BF);
            _fDirection = _colType.GetField("m_Direction", BF);
        }

        // Pick the collider that drives the womb. Two modes:
        //  * NAME-TARGETED (nameFilter set): consider ONLY colliders whose name (or a parent's) contains the
        //    filter -- e.g. "J694" for a KKPE "[J694] Dynamic Bone Collider". Robust: the character's body
        //    colliders are ignored outright regardless of size/position. Depth = the collider's TIP (deeper
        //    end) projection, so the womb tracks how DEEP it reaches -- a long collider whose tip is in the
        //    bulb reads as fully inserted (no lag).
        //  * AUTO (nameFilter empty): best-effort heuristic -- a candidate qualifies if its CENTRE is inside
        //    the canal (0..maxAlong), laterally near the axis, and not too fat (maxRadius). This rejects body
        //    colliders (fat / centre far above) and the womb's own collider; the penis's own dan-colliders or
        //    a small inserted object qualify.
        // In BOTH modes depth = the collider's TIP (deeper-end) projection, so it tracks how deep it reaches
        // (responsive -- a long collider whose tip is in the bulb reads as fully inserted, no lag).
        // Returns the chosen collider's tip + base ends (world, for lean dir), world radius, the DRIVING
        // along-axis distance (m), the centre's lateral offset (m), and the chosen collider's name.
        // ringDepths/outRingRadius (optional, may be null): for EACH ring depth (0..1), outRingRadius is filled with
        // the MAX world radius among ALL qualifying colliders whose body spans that depth - so with two toys (a thin
        // and a thick one) each ring reacts to the thickest thing reaching it. The single deepest collider still
        // drives the engagement DEPTH (tip/base/driveAlong) below.
        public static bool TryReadNearCanal(Vector3 entrance, Vector3 axisDir, float maxRange, float lateralMax, float canalLen, float maxRadius, float bulbRadius, string nameFilter,
            float[] ringDepths, float[] outRingRadius, float ringPad,
            out Vector3 tip, out Vector3 baseEnd, out float radiusWorld, out float driveAlong, out float lateralOut, out string chosenName)
        {
            tip = baseEnd = Vector3.zero; radiusWorld = 0f; driveAlong = 0f; lateralOut = 0f; chosenName = null;
            if (outRingRadius != null) for (int z = 0; z < outRingRadius.Length; z++) outRingRadius[z] = 0f;
            Init();
            if (_colType == null) return false;

            _refreshTimer -= Time.deltaTime;
            bool stale = _refreshTimer <= 0f || _cache.Length == 0;
            if (!stale) for (int i = 0; i < _cache.Length; i++) if (_cache[i] == null) { stale = true; break; }
            if (stale) { _cache = UnityEngine.Object.FindObjectsOfType(_colType); _refreshTimer = RefreshInterval; _metaFilter = null; }
            if (_cache.Length == 0) return false;

            bool named = !string.IsNullOrEmpty(nameFilter);
            // Profiling findings, 7-womb scene: TryReadNearCanal 4.6ms/frame, 96% of
            // this LateUpdate, ~10^6 allocations/s - NameMatch string work ran on EVERY collider BEFORE the
            // cheap distance reject, plus boxed reflection reads per candidate): ALL per-collider facts
            // (name-match verdict, centre offset, height/radius/direction) are now precomputed ONCE at the
            // existing 2s cache refresh, and the filter is parsed once per distinct string. The per-frame
            // loop below is array lookups + float math; the distance reject runs FIRST. KKPE live edits to
            // a collider's radius appear within the same 2s the cache itself already tolerated.
            if (_metaFilter == null || _metaFilter != nameFilter) RebuildColliderMeta(nameFilter);
            float rangeSq = maxRange * maxRange;
            float maxAlong = 1.5f * canalLen;        // auto-mode centre upper bound (1.5x canal = generous overshoot room)
            float best = 0f; bool found = false;
            for (int i = 0; i < _cache.Length; i++)
            {
                var comp = _cache[i] as Component; if (comp == null) continue;
                Transform t = comp.transform;
                Vector3 ctr = t.TransformPoint(_cCenter[i]);
                Vector3 d = ctr - entrance;
                if (d.sqrMagnitude > rangeSq) continue;                  // cheap float reject FIRST
                if (named && !_nameOk[i]) continue;                      // precomputed at refresh
                float cAlong = Vector3.Dot(d, axisDir);
                float cLat = (d - cAlong * axisDir).magnitude;           // centre's perpendicular offset from the axis
                float h = _cHeight[i];
                float r = _cRadius[i];
                int dir = _cDir[i];
                Vector3 axis  = dir == 0 ? Vector3.right : (dir == 2 ? Vector3.forward : Vector3.up);
                Vector3 ls = t.lossyScale; float sc = Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z));
                float radW = r * sc;
                // A capsule = the segment between its two SPHERE centres (at +/- (height/2 - radius) along the
                // axis) inflated by `radius`. Find the deeper sphere centre, then the deepest SURFACE point is
                // that + radius. Counting the radius is what lets a FAT collider sitting AT the entrance (its
                // body overlapping the canal) read as inserted even when its axis doesn't point up the canal.
                Vector3 halfV = t.TransformVector(axis * Mathf.Max(h * 0.5f - r, 0f));   // to the sphere centres
                Vector3 e1 = ctr + halfV, e2 = ctr - halfV;
                float a1 = Vector3.Dot(e1 - entrance, axisDir), a2 = Vector3.Dot(e2 - entrance, axisDir);
                Vector3 dEnd, sEnd; float dA;
                if (a1 >= a2) { dEnd = e1; sEnd = e2; dA = a1; } else { dEnd = e2; sEnd = e1; dA = a2; }
                float reach = dA + radW;                                 // deepest surface point along the canal

                // CONTAINMENT (BOTH modes): the collider's deeper end (its "tip") must be laterally INSIDE the
                // canal -- within lateralMax of the axis -- i.e. actually inserted, not resting beside the womb.
                // Enforced ONLY in auto mode (the !named block below) until now, so a NAME-targeted
                // collider (the DEFAULT mode, filter "Collider") drove the womb from anywhere its tip crossed the
                // entrance plane along the axis, INCLUDING off to the side -> the "collider outside the womb still
                // reacts" bug. Using the TIP's lateral offset matches the config doc ("its tip is within this
                // lateral distance of the canal axis") and handles angled insertion (centre off-axis, tip in).
                // lateralMax = CfgColliderInCanal ("Collider in-canal width"); lower it for stricter containment.
                Vector3 tipRel   = dEnd - entrance;
                float   tipAlong = Vector3.Dot(tipRel, axisDir);
                float   tipLat   = (tipRel - tipAlong * axisDir).magnitude;
                // Lateral tolerance = the womb's BULB radius (its own half-width), FLAT at all depths. A fixed mm
                // gate (or a depth-ramped one) can't fit a womb placed off the insertion path: the collider
                // then reads laterally offset at EVERY depth, so a depth-ramp only caught it deep (sudden pop-in).
                // "Inside the womb's half-width" is the real 'in the womb' test and is orientation/placement-agnostic
                // (bulbRadius comes from the REST mesh, rotates with the womb). lateralMax stays a floor (tiny wombs).
                // NOT the womb's BOUNDS. bulbRadius is the mesh's own extent, and that mesh includes the
                // tubes and the ovaries spreading sideways - measured wider than 62mm - so as a lateral
                // gate it rejected nothing: a toy pulled out and swung 81mm, then 117mm, off the
                // centreline still reported 0.85 depth and flashed the womb to full stretch. The penis
                // path reached this same conclusion and answers it with the CANAL TUBE half-width; the
                // collider path never got that fix. A toy's own radius plus wall slack is its equivalent,
                // and lateralMax stays as the floor it always was.
                float maxLatTip = Mathf.Max(lateralMax, radW + 0.010f);
                if (tipLat > maxLatTip) continue;                        // tip beyond the canal tube -> outside, don't react

                // ALONG-AXIS bound (BOTH modes): reject a collider whose WHOLE body sits ABOVE the canal top --
                // its SHALLOW (near) end is already past the top by more than the in-canal slop. That's a collider
                // resting on/above the womb, not inserted THROUGH it (real insertion enters from BELOW, so the near
                // end stays in the canal even when the tip overshoots the top). Together with reach>0 below (which
                // rejects a collider entirely BELOW the entrance) the collider body must OVERLAP the canal range
                // [0, canalLen] -> a "clearly out of range" collider (above OR below) can't drive the womb.
                float nearAlong = Mathf.Min(a1, a2) - radW;             // shallowest surface point along the axis
                if (nearAlong > canalLen + lateralMax) continue;        // whole body above the womb top -> out of range

                if (!named)
                {
                    if (cAlong <= 0f || cAlong > maxAlong) continue;     // body CENTRE must be inside the canal range
                    if (cLat > lateralMax) continue;                     // off to the side
                    if (radW > maxRadius) continue;                      // too fat -> a character body collider
                }                                                        // (named mode: name match at loop top is enough)
                if (reach <= 0f) continue;                               // not even the collider SURFACE reaches above the entrance -> outside
                // PER-RING: this collider's body spans canal along [nearAlong, reach]; contribute its radius to every
                // ring it covers, keeping the MAX (thickest collider wins per ring). Runs for ALL qualifying colliders.
                if (outRingRadius != null && ringDepths != null)
                    for (int rk = 0; rk < ringDepths.Length && rk < outRingRadius.Length; rk++)
                    {
                        float ra = ringDepths[rk] * canalLen;
                        // ringPad widens the span so a SHORT object (a ball) still counts as covering the ring
                        // nearest it instead of falling between two rings and opening neither. 0 = exact span.
                        if (ra >= nearAlong - ringPad && ra <= reach + ringPad && radW > outRingRadius[rk]) outRingRadius[rk] = radW;
                    }
                float drive = reach;                                     // depth tracks the deepest surface (responsive; fat/long colliders read deep)
                if (drive <= best) continue;                             // keep the deepest
                best = drive; tip = dEnd; baseEnd = sEnd; radiusWorld = radW; driveAlong = drive; lateralOut = cLat;
                chosenName = comp.name; found = true;
            }
            return found;
        }

        // Match the filter against the collider's GameObject name OR any of its parents (KKPE may put a unique
        // id on a guide parent). Two rules, both case-insensitive:
        //   * PREFIX  -- name starts with the filter. So "Collider" matches KKPE's "Collider" object but NOT
        //     the game's body colliders "KK_Colliders_..." (a plain substring test WOULD wrongly hit those).
        //   * BRACKET -- name contains "[<filter>" . So a filter of "J694" matches a KKPE guide "[J694] ..." ,
        //     letting you target the exact collider by its KKPE index when that index is a real object.
        // per-collider metadata, rebuilt at cache refresh / filter change - see TryReadNearCanal.
        private static string _metaFilter;
        private static string[] _filterParts = new string[0];     // pre-trimmed entries
        private static string[] _filterBracket = new string[0];   // pre-built "[" + entry
        private static bool[] _nameOk = new bool[0];
        private static Vector3[] _cCenter = new Vector3[0];
        private static float[] _cHeight = new float[0], _cRadius = new float[0];
        private static int[] _cDir = new int[0];
        private static void RebuildColliderMeta(string filter)
        {
            _metaFilter = filter ?? "";
            var raw = _metaFilter.Split(',');
            var parts = new System.Collections.Generic.List<string>(raw.Length);
            var brack = new System.Collections.Generic.List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                string f = raw[i].Trim();
                if (f.Length == 0) continue;
                parts.Add(f); brack.Add("[" + f);
            }
            _filterParts = parts.ToArray(); _filterBracket = brack.ToArray();
            int n = _cache.Length;
            if (_nameOk.Length != n)
            { _nameOk = new bool[n]; _cCenter = new Vector3[n]; _cHeight = new float[n]; _cRadius = new float[n]; _cDir = new int[n]; }
            for (int i = 0; i < n; i++)
            {
                var comp = _cache[i] as Component;
                if (comp == null) { _nameOk[i] = false; continue; }
                _cCenter[i] = _fCenter != null ? (Vector3)_fCenter.GetValue(comp) : Vector3.zero;
                _cHeight[i] = _fHeight != null ? Convert.ToSingle(_fHeight.GetValue(comp)) : 0f;
                _cRadius[i] = _fRadius != null ? Convert.ToSingle(_fRadius.GetValue(comp)) : 0f;
                _cDir[i]    = _fDirection != null ? Convert.ToInt32(_fDirection.GetValue(comp)) : 1;
                _nameOk[i]  = NameMatchOnce(comp);
            }
        }
        // Match the filter against the collider's GameObject name OR any of its parents (KKPE may put a
        // unique id on a guide parent). PREFIX or BRACKET ("[<filter>"), case-insensitive - semantics
        // unchanged from the older NameMatch; runs only at refresh now. Each Transform.name is read
        // ONCE per level (Object.name marshals native->managed and allocates on every access).
        private static bool NameMatchOnce(Component comp)
        {
            Transform t = comp.transform;
            for (int k = 0; k < 6 && t != null; k++, t = t.parent)
            {
                string nm = t.name;
                if (string.IsNullOrEmpty(nm)) continue;
                for (int i = 0; i < _filterParts.Length; i++)
                {
                    if (nm.StartsWith(_filterParts[i], StringComparison.OrdinalIgnoreCase)) return true;
                    if (nm.IndexOf(_filterBracket[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        // DEBUG: compact list of DynamicBoneColliders within range of the entrance, with name, centre depth
        // (normalized) and world radius -- so you can read off the exact name to put in the filter.
        public static string DebugListNear(Vector3 entrance, Vector3 axisDir, float maxRange, float canalLen)
        {
            Init();
            if (_colType == null || _cache == null || _cache.Length == 0) return "(none)";
            var sb = new System.Text.StringBuilder();
            float rangeSq = maxRange * maxRange; int shown = 0;
            for (int i = 0; i < _cache.Length && shown < 20; i++)
            {
                var comp = _cache[i] as Component; if (comp == null) continue;
                Transform t = comp.transform;
                Vector3 ctr = _fCenter != null ? t.TransformPoint((Vector3)_fCenter.GetValue(comp)) : t.position;
                Vector3 d = ctr - entrance;
                if (d.sqrMagnitude > rangeSq) continue;
                float cAlong = Vector3.Dot(d, axisDir);
                float r = _fRadius != null ? Convert.ToSingle(_fRadius.GetValue(comp)) : 0f;
                Vector3 ls = t.lossyScale; float sc = Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z));
                string chain = comp.name; Transform pt = t.parent; int up = 0;
                while (pt != null && up < 6) { chain += "<" + pt.name; pt = pt.parent; up++; }   // full hierarchy -> find a unique handle
                sb.Append(string.Format("[{0} | d={1:F2} r={2:0}mm id={3}]  ", chain, canalLen > 1e-4f ? cAlong / canalLen : 0f, r * sc * 1000f, comp.GetInstanceID()));
                shown++;
            }
            return sb.Length == 0 ? "(none in range)" : sb.ToString();
        }

        // DIAG: replay the TryReadNearCanal gates and report, for every candidate that passes the name filter and is
        // in range, WHICH gate rejects it (or that it would drive). Pinpoints why "a collider inside" doesn't react:
        // wrong name filter (0 name-matched), off-axis (tipLat>inCanalW), below the entrance (reach<=0), or too fat.
        public static string DebugDecision(Vector3 entrance, Vector3 axisDir, float maxRange, float lateralMax, float canalLen, float maxRadius, float bulbRadius, string nameFilter)
        {
            Init();
            if (_colType == null) return "no DynamicBoneCollider type";
            if (_cache == null || _cache.Length == 0) return "no colliders cached";
            bool named = !string.IsNullOrEmpty(nameFilter);
            if (_metaFilter == null || _metaFilter != nameFilter) RebuildColliderMeta(nameFilter);   // rebuild when the filter changes
            float rangeSq = maxRange * maxRange, maxAlong = 1.5f * canalLen;
            int nameMatched = 0, inRange = 0; var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _cache.Length; i++)
            {
                var comp = _cache[i] as Component; if (comp == null) continue;
                if (named && !_nameOk[i]) continue;
                nameMatched++;
                Transform t = comp.transform;
                Vector3 ctr = _fCenter != null ? t.TransformPoint((Vector3)_fCenter.GetValue(comp)) : t.position;
                Vector3 d = ctr - entrance;
                if (d.sqrMagnitude > rangeSq) continue;
                inRange++;
                float cAlong = Vector3.Dot(d, axisDir), cLat = (d - cAlong * axisDir).magnitude;
                float h = _fHeight != null ? Convert.ToSingle(_fHeight.GetValue(comp)) : 0f;
                float r = _fRadius != null ? Convert.ToSingle(_fRadius.GetValue(comp)) : 0f;
                int dir = _fDirection != null ? Convert.ToInt32(_fDirection.GetValue(comp)) : 1;
                Vector3 ax = dir == 0 ? Vector3.right : (dir == 2 ? Vector3.forward : Vector3.up);
                Vector3 ls = t.lossyScale; float sc = Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z)); float radW = r * sc;
                Vector3 halfV = t.TransformVector(ax * Mathf.Max(h * 0.5f - r, 0f));
                Vector3 e1 = ctr + halfV, e2 = ctr - halfV;
                float a1 = Vector3.Dot(e1 - entrance, axisDir), a2 = Vector3.Dot(e2 - entrance, axisDir);
                float dA = Mathf.Max(a1, a2); float reach = dA + radW;
                Vector3 dEnd = a1 >= a2 ? e1 : e2; Vector3 tipRel = dEnd - entrance;
                float tipLat = (tipRel - Vector3.Dot(tipRel, axisDir) * axisDir).magnitude;
                float nearAlong = Mathf.Min(a1, a2) - radW;
                float maxLatTip = Mathf.Max(lateralMax, radW + 0.010f);   // canal tube, not mesh bounds
                string verdict;
                if (tipLat > maxLatTip) verdict = "REJECT tipLat=" + Mathf.RoundToInt(tipLat * 1000) + ">" + Mathf.RoundToInt(maxLatTip * 1000);
                else if (nearAlong > canalLen + lateralMax) verdict = "REJECT wholeBodyAboveTop";
                else if (!named && (cAlong <= 0f || cAlong > maxAlong)) verdict = "REJECT(auto) centreOutOfCanal";
                else if (!named && cLat > lateralMax) verdict = "REJECT(auto) cLat>w";
                else if (!named && radW > maxRadius) verdict = "REJECT(auto) tooFat=" + Mathf.RoundToInt(radW * 1000);
                else if (reach <= 0f) verdict = "REJECT reach<=0 (below entrance, reach=" + Mathf.RoundToInt(reach * 1000) + "mm)";
                else verdict = "DRIVE reach=" + Mathf.RoundToInt(reach * 1000) + "mm depth=" + (reach / Mathf.Max(canalLen, 1e-4f)).ToString("F2");
                if (sb.Length < 700) sb.Append('[').Append(comp.name).Append(" cA=").Append(Mathf.RoundToInt(cAlong * 1000)).Append(" a1=").Append(Mathf.RoundToInt(a1 * 1000)).Append(" a2=").Append(Mathf.RoundToInt(a2 * 1000)).Append(" tipLat=").Append(Mathf.RoundToInt(tipLat * 1000)).Append(" h=").Append(Mathf.RoundToInt(h * 1000 * Mathf.Max(t.lossyScale.x, Mathf.Max(t.lossyScale.y, t.lossyScale.z)))).Append(" reach=").Append(Mathf.RoundToInt(reach * 1000)).Append(" r=").Append(Mathf.RoundToInt(radW * 1000)).Append(" -> ").Append(verdict).Append("] ");
            }
            return "nameMatched=" + nameMatched + " inRange=" + inRange + " canalLen=" + Mathf.RoundToInt(canalLen * 1000) + "mm :: " + (sb.Length == 0 ? "(no name-matched-in-range candidate)" : sb.ToString());
        }
    }

}
