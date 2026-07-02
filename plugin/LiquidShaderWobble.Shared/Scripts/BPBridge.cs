using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// Reads BetterPenetration (Animal42069) state by reflection — no hard dependency, no-ops if BP
    /// isn't installed.
    ///
    /// PENETRATION (from Core_BetterPenetration source):
    ///   DanAgent.m_baseDanLength = penis natural length (per character, from maker). Constant.
    ///   DanAgent.lastDanDistance = base->IK-target distance; shrinks as the male thrusts in, and is
    ///                              set back to m_baseDanLength when not penetrating.
    ///   depth = (baseLen - dist) / baseLen   -> 0 when out, up to ~1 deep. Normalizes per length, so
    ///   a long or short penis maps the same (length auto-adjust, no slider).
    ///
    /// SQUISH (reconstructed from DanAgent.GetSquishedDanLength / GetSquishedDanGirth so we track what
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
        private static Type _ctrlType;
        private static FieldInfo _fDan;
        private static FieldInfo _danDist, _danBaseLen, _danRad, _danColliders, _danOptions;
        private static FieldInfo _optRadiusScale, _optLengthSquish, _optGirthSquish, _optSquishThreshold;
        private static FieldInfo _colRadius; // DynamicBoneCollider.m_Radius (resolved lazily)
        private static FieldInfo _danTargetsValid;   // public bool on BetterPenetrationController (BP re-init finished)
        private static PropertyInfo _chaControlProp;  // CharaCustomFunctionController.ChaControl

        // Cached controller instances — refreshed on a slow timer, NOT every frame.
        private static UnityEngine.Object[] _cache = new UnityEngine.Object[0];
        private static Transform[] _tipBones = new Transform[0];      // each controller's k_f_dan_end (BP TARGET marker — only at the tip when constraint-driven)
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
        // (danTargetsValid == BP's load re-init finished). Used to gate the FK enforcer: we hand the dan chain to
        // BP ONLY when BP is actually enabled on this male — a male with FK-posed (non-BP) penis is left untouched.
        public static bool HasActiveBp(Component chaControl)
        {
            if (chaControl == null) return false;
            foreach (var m in EnumerateMales())
                if ((UnityEngine.Object)m.chaControl == (UnityEngine.Object)chaControl && m.danTargetsValid) return true;
            return false;
        }

        // The penis shaft bone NAMES (cm_j_dan*, excluding balls) on a male — used to scope the FK disable to ONLY
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
            // BP's Core_BetterPenetration assembly loads LAZILY — only when the first BP character spawns. Callers
            // (the scene-load penis-bend coroutine, the per-frame read) can hit this BEFORE that happens, so we must
            // NOT latch failure: re-scan (throttled ~1/s) until BP appears, then bind once. A single latched early
            // failure used to poison BPBridge for the whole session — womb expansion AND penis-bend both went dead.
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
            _tried = true;   // bound successfully — stop scanning

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
            _danTargetsValid    = _ctrlType.GetField("danTargetsValid", BF);                 // public bool — BP load re-init done
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
            public Vector3 tipPos;     // DEBUG: world pos of the tip (last) collider
            public Vector3 tipDir;     // DEBUG: base->tip unit direction
            public bool    hasPose;    // DEBUG: true when >=2 colliders gave a world pose
            public Vector3[] shaftPos; // WORLD positions of every REAL cm_j_dan* shaft bone (base..tip) for the matched
                                       // male — lets the womb test the whole bent shaft, not just the curl-prone tip.
            public Vector3 aimedTipPos;// k_f_dan_end (the aimed/constraint-driven tip). CONTAINMENT signal only — it's
            public bool aimedValid;    // glued to penis_target when the user aims the penis, so it is NOT a depth signal.
            public float girthTip;     // WORLD radius of the TIP collider = the girth the womb actually contacts (the
                                       // glans pushing the cervix). Fallback when the full profile isn't available.
            public Vector3[] girthPos; // per-collider WORLD position (base..tip) and paired WORLD radius = the penis
            public float[]   girthRad; // GIRTH PROFILE (m_danColliderRadius*danRadiusScale*lossyScale). The womb maps
                                       // each ring to the profile by DISTANCE-FROM-TIP (the penis part at that ring),
                                       // so the canal tapers with the penis. Mapping-by-POSITION was the dead end (the
                                       // off-centre womb put every collider below its entrance) — distance-from-tip
                                       // uses the penis's INTRINSIC curve + insertion depth, no shared-axis needed.
            public string srcName;     // DEBUG: the paired BP controller's character name (which penis won the pairing)
            public Vector3 entryPos;   // DEBUG: the paired penis's k_f_dan_entry world position (the node the pairing matched)
        }

        private static float ReadF(FieldInfo fi, object o, float def)
        {
            if (fi == null || o == null) return def;
            try { return Convert.ToSingle(fi.GetValue(o)); } catch { return def; }
        }

        // Picks the most-penetrated controller GLOBALLY (legacy — correct only with a single
        // penis/womb pair in the scene). Prefer TryReadLocked for anything per-womb.
        public static bool TryRead(out Reading r)
        {
            return TryReadCore(out r, false, Vector3.zero, 0f, null);
        }

        // Refresh the BP controller cache + per-controller bone handles (k_f_dan_entry/end, deepest dan, shaft chain)
        // on a timer / when an entry went null. Shared by the readers + the entry matcher.
        private static int _cacheTickFrame = -1;
        private static void EnsureCache()
        {
            if (Time.frameCount != _cacheTickFrame) { _refreshTimer -= Time.deltaTime; _cacheTickFrame = Time.frameCount; }   // tick the timer ONCE per frame however many readers call us
            bool stale = _refreshTimer <= 0f || _cache.Length == 0;
            if (!stale)
                for (int i = 0; i < _cache.Length; i++)
                    if (_cache[i] == null) { stale = true; break; }
            if (!stale) return;
            _cache = UnityEngine.Object.FindObjectsOfType(_ctrlType);
            _tipBones = new Transform[_cache.Length];        // k_f_dan_end (BP target marker)
            _entryBones = new Transform[_cache.Length];      // k_f_dan_entry (ENTRY marker — the pairing key)
            _danTipBones = new Transform[_cache.Length];     // DEEPEST cm_j_dan* shaft bone = visual mesh tip
            _danShaftBones = new Transform[_cache.Length][]; // full shaft chain (base..tip)
            for (int ci = 0; ci < _cache.Length; ci++)
            {
                var cc = _cache[ci] as Component;
                if (cc == null) continue;
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
        // marker is nearest the womb's entrance — the NodesConstraints `cf_J_Vagina_root -> k_f_dan_entry` pins that
        // marker at the womb mouth, so its position alone decides which penis belongs to which womb (no shaft scan).
        // The womb caches the returned name and reads ONLY that penis per-frame via TryReadLocked.
        // STICKY: hold the current pairing (`currentName`) while ITS entry is still attached — within `stickyHold` of the
        // womb mouth. BP drags the vagina (and the pinned k_f_dan_entry) toward the penis on a thrust, and the entry
        // shifts on withdrawal; staying attached means a thrust/withdraw can't flip the womb to a neighbour. We only
        // re-pair to the nearest male when the current entry has moved AWAY (NC re-aimed to another womb, or toggled off
        // -> the marker jumps back to the penis base). Logs every candidate's entry distance for alignment debugging.
        public static string FindByEntry(Vector3 wombEntrance, Vector3 wombAxis, float maxRange, string currentName, float stickyMargin)
        {
            Init();
            if (_ctrlType == null) return currentName;
            EnsureCache();
            // Match by LATERAL offset from the womb's CHANNEL AXIS, not raw 3D distance to the mouth. The entry node always
            // sits BELOW the mouth, down the channel (NC shift + how the womb seats in the body) — and "below" rotates with
            // the womb. So the entry that lies ON this womb's axis line (smallest perpendicular distance), at or below the
            // mouth, is this womb's penis — even if a neighbour's entry is a closer raw point.
            string best = null; float bestLat = maxRange; float curLat = float.PositiveInfinity;
            System.Text.StringBuilder dbg = LiquidWobbleMPBPlugin.CfgDebugLog ? new System.Text.StringBuilder() : null;
            for (int i = 0; i < _cache.Length; i++)
            {
                var oc = _cache[i] as Component;
                if (oc == null) continue;
                string nm = oc.gameObject.name;
                if (nm.StartsWith("chaF", System.StringComparison.OrdinalIgnoreCase)) continue;   // males only
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
            // a 57mm off-axis penis over the 26mm on-axis one — that was the "grabbed the wrong penis" bug.)
            bool holdCur = currentName != null && curLat < bestLat + stickyMargin;
            string chosen = holdCur ? currentName : best;
            if (dbg != null)
                LiquidWobbleMPBPlugin._logger?.LogInfo($"  BP-PAIR mouth={wombEntrance.ToString("F3")} axis={wombAxis.ToString("F2")} entries=[{dbg}] cur={(currentName ?? "-")}(lat {(curLat < 1e8f ? Mathf.RoundToInt(curLat * 1000f) + "mm" : "gone")}) hold={holdCur} -> {(chosen ?? "NONE")}");
            return chosen;
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

        // POST-NC read of a named male's k_f_dan_end (the AIMED tip — driven deep by a penis_target NodesConstraint).
        // Call from onPreCull. Vector3.zero if gone. (The cm_j_dan deepest bone gets squished shallow by BP; this is
        // the user's actual deep visual tip when they aim it via NC.)
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

        // PER-FRAME read of the womb's already-paired penis (by character name). No matching, no scan — read that one
        // controller's live depth/girth/tip. found=false if charName is null/empty or that controller is gone.
        public static bool TryReadLocked(string charName, out Reading r)
        {
            r = new Reading();
            if (string.IsNullOrEmpty(charName)) return false;
            return TryReadCore(out r, true, Vector3.zero, 0f, charName);
        }

        // NEAREST-tip reader (cum slosh / debug overlay only): the controller whose tip is closest to `anchor`.
        public static bool TryReadNear(Vector3 anchor, float maxRange, out Reading r)
        {
            return TryReadCore(out r, true, anchor, maxRange, null);
        }

        private static bool TryReadCore(out Reading r, bool byNearest, Vector3 anchor, float maxRange, string lockName)
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
                // Per-womb pairing: only MALE penises are candidates. Females carry vestigial cm_j_dan* bones (a phantom
                // penis tip at the crotch where the womb sits). In LOCK mode read ONLY the womb's already-paired penis.
                if (byNearest)
                {
                    var fc = o as Component;
                    if (fc == null) continue;
                    string cnm = fc.gameObject.name;
                    if (cnm.StartsWith("chaF", System.StringComparison.OrdinalIgnoreCase)) continue;   // males only
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
                        float rr = _colRadius != null ? Convert.ToSingle(_colRadius.GetValue(c)) : 0f;
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
                    // Tip = the REAL penis-tip bone (k_f_dan_end — what the mesh tips to / BP aims), NOT the last
                    // collision capsule centre (that sits well down the shaft -> tip read ~13cm low). Falls back to
                    // the last collider when the bone isn't resolvable. base = first collider; dir = base->tip.
                    // Tip = the DEEPEST cm_j_dan* shaft bone (the visual mesh tip — a real penis bone, so it tracks BP
                    // bend AND squish). k_f_dan_end was wrong: it's a BP TARGET marker that sits at the base/vagina
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
                    // to penis_target when the user aims the penis, so it tells us the penis is AIMED at this womb but
                    // NOT how deep it physically is — the womb uses it for containment only, never as a depth source.
                    var sb2 = (i < _danShaftBones.Length) ? _danShaftBones[i] : null;
                    int sn = (sb2 != null) ? sb2.Length : 0;
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
                    if (_gPosBuf.Count > 0) { r.girthPos = _gPosBuf.ToArray(); r.girthRad = _gRadBuf.ToArray(); }   // girth PROFILE for per-ring taper
                    else { r.girthPos = null; r.girthRad = null; }
                }
            }
            return r.found;
        }
    }

    // ── Generic non-BP penetrator: the nearest DynamicBoneCollider to a womb entrance (e.g. a KKPE-placed
    // BP collider on a bottle/toy). READ-ONLY (we never touch BP or the collider), reflection so there's no
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
        // the MAX world radius among ALL qualifying colliders whose body spans that depth — so with two toys (a thin
        // and a thick one) each ring reacts to the thickest thing reaching it. The single deepest collider still
        // drives the engagement DEPTH (tip/base/driveAlong) below.
        public static bool TryReadNearCanal(Vector3 entrance, Vector3 axisDir, float maxRange, float lateralMax, float canalLen, float maxRadius, float bulbRadius, string nameFilter,
            float[] ringDepths, float[] outRingRadius,
            out Vector3 tip, out Vector3 baseEnd, out float radiusWorld, out float driveAlong, out float lateralOut, out string chosenName)
        {
            tip = baseEnd = Vector3.zero; radiusWorld = 0f; driveAlong = 0f; lateralOut = 0f; chosenName = null;
            if (outRingRadius != null) for (int z = 0; z < outRingRadius.Length; z++) outRingRadius[z] = 0f;
            Init();
            if (_colType == null) return false;

            _refreshTimer -= Time.deltaTime;
            bool stale = _refreshTimer <= 0f || _cache.Length == 0;
            if (!stale) for (int i = 0; i < _cache.Length; i++) if (_cache[i] == null) { stale = true; break; }
            if (stale) { _cache = UnityEngine.Object.FindObjectsOfType(_colType); _refreshTimer = RefreshInterval; }
            if (_cache.Length == 0) return false;

            bool named = !string.IsNullOrEmpty(nameFilter);
            float rangeSq = maxRange * maxRange;
            float maxAlong = 1.5f * canalLen;        // auto-mode centre upper bound (1.5x canal = generous overshoot room)
            float best = 0f; bool found = false;
            for (int i = 0; i < _cache.Length; i++)
            {
                var comp = _cache[i] as Component; if (comp == null) continue;
                if (named && !NameMatch(comp, nameFilter)) continue;     // name mode: ONLY the user's collider
                Transform t = comp.transform;
                Vector3 ctr = _fCenter != null ? t.TransformPoint((Vector3)_fCenter.GetValue(comp)) : t.position;
                Vector3 d = ctr - entrance;
                if (d.sqrMagnitude > rangeSq) continue;
                float cAlong = Vector3.Dot(d, axisDir);
                float cLat = (d - cAlong * axisDir).magnitude;           // centre's perpendicular offset from the axis
                float h   = _fHeight != null ? Convert.ToSingle(_fHeight.GetValue(comp)) : 0f;
                float r   = _fRadius != null ? Convert.ToSingle(_fRadius.GetValue(comp)) : 0f;
                int   dir = _fDirection != null ? Convert.ToInt32(_fDirection.GetValue(comp)) : 1;
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
                // This was previously enforced ONLY in auto mode (the !named block below), so a NAME-targeted
                // collider (the DEFAULT mode, filter "Collider") drove the womb from anywhere its tip crossed the
                // entrance plane along the axis, INCLUDING off to the side -> the "collider outside the womb still
                // reacts" bug. Using the TIP's lateral offset matches the config doc ("its tip is within this
                // lateral distance of the canal axis") and handles angled insertion (centre off-axis, tip in).
                // lateralMax = CfgColliderInCanal ("Collider in-canal width"); lower it for stricter containment.
                Vector3 tipRel   = dEnd - entrance;
                float   tipAlong = Vector3.Dot(tipRel, axisDir);
                float   tipLat   = (tipRel - tipAlong * axisDir).magnitude;
                // Lateral tolerance = the womb's BULB radius (its own half-width), FLAT at all depths. A fixed mm
                // gate (or a depth-ramped one) can't fit a womb the user placed off the insertion path: the collider
                // then reads laterally offset at EVERY depth, so a depth-ramp only caught it deep (sudden pop-in).
                // "Inside the womb's half-width" is the real 'in the womb' test and is orientation/placement-agnostic
                // (bulbRadius comes from the REST mesh, rotates with the womb). lateralMax stays a floor (tiny wombs).
                float maxLatTip = Mathf.Max(lateralMax, bulbRadius);
                if (tipLat > maxLatTip) continue;                        // tip beyond the womb's width -> outside, don't react

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
                        if (ra >= nearAlong && ra <= reach && radW > outRingRadius[rk]) outRingRadius[rk] = radW;
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
        private static bool NameMatch(Component comp, string filter)
        {
            Transform t = comp.transform;
            for (int k = 0; k < 6 && t != null; k++, t = t.parent)
            {
                if (string.IsNullOrEmpty(t.name)) continue;
                if (t.name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) return true;
                if (t.name.IndexOf("[" + filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
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
            float rangeSq = maxRange * maxRange, maxAlong = 1.5f * canalLen;
            int nameMatched = 0, inRange = 0; var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _cache.Length; i++)
            {
                var comp = _cache[i] as Component; if (comp == null) continue;
                if (named && !NameMatch(comp, nameFilter)) continue;
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
                float maxLatTip = Mathf.Max(lateralMax, bulbRadius);
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

    // ── On-load BP penis-bend re-assert (reflection; soft-dep — full no-op without BP or KK_AdditionalFKNodes).
    //
    // PROBLEM: after a scene load the restored FK pose on the penis dan bones (cm_J_dan101_00..109_00 +
    // cm_J_dan_f_top) overrides BP's smooth per-frame bend, because the FK GuideObject/FKCtrl LateUpdate is the
    // last writer each frame -> FK wins, the penis stands straight instead of following BP's target.
    //
    // FIX (replicates the user's manual toggle): for the dan FK BoneGroup, call OCIChar.ActiveFK(danGroup,false)
    // then ActiveFK(danGroup,true). "false" runs the game's ResetFKNodes (dan bones -> identity) so BP's bend shows;
    // "true" rebuilds the FK targets from the now-BP-bent pose so FK no longer fights BP. KK_AdditionalFKNodes puts
    // the penis dan bones in their OWN FK group (separate from every body group), so this touches ONLY the penis.
    //
    // Reflection signatures confirmed against the decompiles:
    //  * Studio OCIChar.ActiveFK(BoneGroup,bool,bool) — KK_AdditionalFKNodes.decompiled.cs:264
    //    (value.ociChar.ActiveFK(FKCtrl.parts[j], activeFK[j], false)) and KKPE.decompiled.cs:9954.
    //  * OCIChar.charInfo (ChaControl), OCIChar.oiCharInfo, OCIChar.fkCtrl, OCIChar.oiCharInfo.activeFK[] (parallel
    //    to FKCtrl.parts) — KK_AdditionalFKNodes.decompiled.cs:74,195,264; Timeline.decompiled.cs:8651.
    //  * KK_AdditionalFKNodes.additionalBoneInfoDictionary (Dictionary<int,BoneInfo>) + BoneInfo.bone(string)/
    //    .group(int); ToolBox.Tools.GetBoneGroup(int)->BoneGroup — KK_AdditionalFKNodes.decompiled.cs:42,164-176,194,308.
    //  * FK-active read mirrors KKPE.decompiled.cs:9938-9950: iterate OCIChar.fkCtrl.listBones; a TargetInfo's
    //    .group(BoneGroup) == danGroup with .enable==true means the dan FK group is currently active.
    //  * BP gate: Core_BetterPenetration.BetterPenetrationController (BPBridge), public ChaControl + public
    //    danTargetsValid (studio/KK_Studio_BetterPenetration.decompiled.cs:74) -> BP re-init finished.
    internal static class PenisFKBridge
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags SBF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string DanGroupProbeBone = "cm_J_dan101_00";   // first penis dan bone — its FK group IS the penis group

        private static bool _tried;
        private static bool _ok;
        // OCIChar
        private static Type _ociCharType;
        private static MethodInfo _activeFK;          // ActiveFK(BoneGroup,bool,bool)
        private static FieldInfo _charInfoF;          // OCIChar.charInfo (ChaControl)
        private static FieldInfo _fkCtrlF;            // OCIChar.fkCtrl
        // FKCtrl.listBones -> TargetInfo.group / .enable (per-group active read, KKPE-style)
        private static FieldInfo _listBonesF;
        private static FieldInfo _tiGroupF, _tiEnableF;
        // KK_AdditionalFKNodes static dicts + BoneInfo shape + Tools.GetBoneGroup
        private static FieldInfo _addBoneDictF;       // additionalBoneInfoDictionary
        private static FieldInfo _biBoneF, _biGroupF; // BoneInfo.bone / .group
        private static MethodInfo _getBoneGroupM;     // Tools.GetBoneGroup(int)->BoneGroup
        // Studio object dict to resolve OCIChar from a ChaControl
        private static Type _studioType;
        private static PropertyInfo _studioInstProp;
        private static FieldInfo _studioInstFieldF;
        private static FieldInfo _dicObjectCtrlF;
        // Per-bone FK disable (build 346): OCIChar.listBones (List<BoneInfo>) -> BoneInfo.guideObject -> GuideObject.transformTarget.
        // Nulling a dan bone's transformTarget makes KK_AdditionalFKNodes' own GuideObject.LateUpdate prefix skip it
        // (returns false when transformTarget==null) -> FK writes nothing -> stateless BP owns the bend live.
        private static FieldInfo _ociListBonesF;   // OCIChar.listBones
        // BoneInfo.guideObject + GuideObject.transformTarget are PROPERTIES in Studio (auto-properties) — GetField
        // returned null (the build-346 bug); bind via GetProperty, with a <name>k__BackingField field fallback.
        private static PropertyInfo _obiGuidePropP; private static FieldInfo _obiGuideF; private static bool _guideBound;
        private static PropertyInfo _goTargetPropP; private static FieldInfo _goTargetF; private static bool _tgtBound;
        private static MethodInfo _goSetActiveM;   // GuideObject.SetActive(bool,bool) — deactivate a dan guide (build 353)
        private static FieldInfo _tiTransformF;   // TargetInfo.m_Transform (diag: match fkCtrl bones by name)
        // TargetInfo.enable/.group are PROPERTIES in this Studio build (GetField returned null -> fkEn=? in build 351).
        // Bind field-OR-property lazily off the live TargetInfo type and read/write through whichever exists.
        private static PropertyInfo _tiEnableP, _tiGroupP; private static bool _tiMembersBound;
        private static void BindTiMembers(object ti)
        {
            if (_tiMembersBound || ti == null) return;
            _tiMembersBound = true;
            var t = ti.GetType();
            _tiEnableF = t.GetField("enable", BF); if (_tiEnableF == null) _tiEnableP = t.GetProperty("enable", BF);
            _tiGroupF  = t.GetField("group",  BF); if (_tiGroupF  == null) _tiGroupP  = t.GetProperty("group",  BF);
            _tiTransformF = _tiTransformF ?? t.GetField("m_Transform", BF);
        }
        private static bool TiEnable(object ti) { BindTiMembers(ti); var v = _tiEnableF != null ? _tiEnableF.GetValue(ti) : (_tiEnableP != null ? _tiEnableP.GetValue(ti, null) : null); return v != null && Convert.ToBoolean(v); }
        private static int  TiGroup(object ti)  { BindTiMembers(ti); var v = _tiGroupF  != null ? _tiGroupF.GetValue(ti)  : (_tiGroupP  != null ? _tiGroupP.GetValue(ti, null)  : null); return v != null ? Convert.ToInt32(v) : int.MinValue; }
        private static Transform TiBone(object ti) { BindTiMembers(ti); return _tiTransformF != null ? _tiTransformF.GetValue(ti) as Transform : null; }
        private static void SetTiEnable(object ti, bool on) { BindTiMembers(ti); if (_tiEnableF != null) _tiEnableF.SetValue(ti, on); else if (_tiEnableP != null && _tiEnableP.CanWrite) _tiEnableP.SetValue(ti, on, null); }

        private static void Init()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                _ociCharType = FindType("Studio.OCIChar") ?? FindType("OCIChar");
                if (_ociCharType == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogWarning("PenisFKBridge: Studio.OCIChar type not found — penis-bend re-assert disabled.");
                    return;
                }
                foreach (var m in _ociCharType.GetMethods(BF))
                    if (m.Name == "ActiveFK" && m.GetParameters().Length == 3) { _activeFK = m; break; }
                _charInfoF = _ociCharType.GetField("charInfo", BF);
                _fkCtrlF   = _ociCharType.GetField("fkCtrl", BF);
                _ociListBonesF = _ociCharType.GetField("listBones", BF);   // OCIChar.listBones (BoneInfo carries the per-bone GuideObject)

                var fkCtrlType = FindType("Studio.FKCtrl") ?? FindType("FKCtrl") ?? (_fkCtrlF != null ? _fkCtrlF.FieldType : null);
                _listBonesF = fkCtrlType != null ? fkCtrlType.GetField("listBones", BF) : null;
                var tiType = FindType("Studio.TargetInfo") ?? FindType("TargetInfo");
                if (tiType != null) { _tiGroupF = tiType.GetField("group", BF); _tiEnableF = tiType.GetField("enable", BF); }

                var afkType = FindType("AdditionalFKNodes.AdditionalFKNodes");
                _addBoneDictF = afkType != null ? afkType.GetField("additionalBoneInfoDictionary", SBF) : null;
                // BoneInfo here is the GAME's Info.BoneInfo (the dict values come from Singleton<Info>.dicBoneInfo,
                // KK_AdditionalFKNodes.decompiled.cs:144). Its full name varies by build; resolving .bone/.group off
                // the live value's runtime type (in ResolveDanGroup) is more robust than guessing the type name, so
                // here we only require the type to exist for the readiness flag — fields are bound lazily/per-value.
                var boneInfoType = FindType("Info+BoneInfo") ?? FindType("Studio.Info+BoneInfo") ?? FindType("BoneInfo");
                if (boneInfoType != null) { _biBoneF = boneInfoType.GetField("bone", BF); _biGroupF = boneInfoType.GetField("group", BF); }
                var toolsType = FindType("ToolBox.Tools");
                _getBoneGroupM = toolsType != null ? toolsType.GetMethod("GetBoneGroup", SBF, null, new[] { typeof(int) }, null) : null;

                _studioType = FindType("Studio.Studio");
                if (_studioType != null)
                {
                    for (var t = _studioType; t != null && _studioInstProp == null && _studioInstFieldF == null; t = t.BaseType)
                    {
                        _studioInstProp   = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        _studioInstFieldF = t.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    }
                    _dicObjectCtrlF = _studioType.GetField("dicObjectCtrl", BF);
                }

                // _biBoneF/_biGroupF (BoneInfo) and _tiGroupF/_tiEnableF (TargetInfo) are bound per-value off the
                // live runtime type at use, so they are NOT part of the readiness gate (their type names vary by build).
                _ok = _charInfoF != null && _dicObjectCtrlF != null && _ociListBonesF != null;   // DisableDanFK needs only ResolveOciChar + OCIChar.listBones
                if (!_ok)
                    LiquidWobbleMPBPlugin._logger?.LogWarning(
                        "PenisFKBridge: a reflection lookup failed — penis-bend disable unavailable. " +
                        $"charInfo={_charInfoF != null} dicObjectCtrl={_dicObjectCtrlF != null} ociListBones={_ociListBonesF != null}");
                else
                    LiquidWobbleMPBPlugin._logger?.LogInfo("PenisFKBridge: hooked OCIChar.ActiveFK + KK_AdditionalFKNodes dan group (penis-bend re-assert ready).");
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("PenisFKBridge: init failed (" + e.Message + ") — penis-bend re-assert disabled.");
            }
        }

        public static bool Available { get { Init(); return _ok; } }

        private static Type FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { var t = a.GetType(fullName, false); if (t != null) return t; } catch { } }
            return null;
        }

        // The penis dan FK BoneGroup, derived from KK_AdditionalFKNodes' additionalBoneInfoDictionary:
        // find the BoneInfo whose .bone == "cm_J_dan101_00", convert its .group (int) via Tools.GetBoneGroup.
        // Returns the boxed BoneGroup enum value (object) or null if the dan nodes aren't registered (no FK penis).
        private static object ResolveDanGroup()
        {
            var dict = _addBoneDictF.GetValue(null) as IDictionary;
            if (dict == null) return null;
            int probeGrp = int.MinValue, firstDanGrp = int.MinValue;
            string probeLower = DanGroupProbeBone.ToLowerInvariant();
            var danNames = new System.Collections.Generic.List<string>();
            foreach (DictionaryEntry de in dict)
            {
                object bi = de.Value;
                if (bi == null) continue;
                // Bind .bone/.group off the live value's runtime type the first time (robust to the BoneInfo
                // type-name varying by build); fall back to the name-resolved fields if present.
                var boneF  = _biBoneF  ?? (_biBoneF  = bi.GetType().GetField("bone",  BF));
                var groupF = _biGroupF ?? (_biGroupF = bi.GetType().GetField("group", BF));
                if (boneF == null || groupF == null) return null;
                var bone = boneF.GetValue(bi) as string;
                if (string.IsNullOrEmpty(bone)) continue;
                string bl = bone.ToLowerInvariant();
                if (bl.Contains("dan")) { danNames.Add(bone); if (firstDanGrp == int.MinValue) firstDanGrp = Convert.ToInt32(groupF.GetValue(bi)); }
                if (bl == probeLower) probeGrp = Convert.ToInt32(groupF.GetValue(bi));   // case-insensitive probe
            }
            int grp = probeGrp != int.MinValue ? probeGrp : firstDanGrp;   // prefer the exact probe, else ANY dan bone's group
            if (grp == int.MinValue) return null;
            return _getBoneGroupM.Invoke(null, new object[] { grp });   // BoneGroup
        }

        // OCIChar whose charInfo == this male's ChaControl, via Studio.Studio.Instance.dicObjectCtrl.
        private static object ResolveOciChar(Component chaControl)
        {
            object studio = _studioInstProp != null ? _studioInstProp.GetValue(null, null)
                          : (_studioInstFieldF != null ? _studioInstFieldF.GetValue(null) : null);
            if (studio == null) return null;
            var dic = _dicObjectCtrlF.GetValue(studio) as IDictionary;
            if (dic == null) return null;
            foreach (DictionaryEntry de in dic)
            {
                object oci = de.Value;
                if (oci == null || !_ociCharType.IsInstanceOfType(oci)) continue;
                var ci = _charInfoF.GetValue(oci) as Component;
                if (ci != null && (UnityEngine.Object)ci == (UnityEngine.Object)chaControl) return oci;
            }
            return null;
        }

        // Is the dan FK group currently ACTIVE on this OCIChar? Mirrors KKPE.decompiled.cs:9938-9950 —
        // any TargetInfo in fkCtrl.listBones whose .group == danGroup with .enable==true means FK is on.
        private static bool IsDanFKActive(object ociChar, object danGroup)
        {
            object fkCtrl = _fkCtrlF.GetValue(ociChar);
            if (fkCtrl == null) return false;
            var list = _listBonesF.GetValue(fkCtrl) as IEnumerable;
            if (list == null) return false;
            int dg = Convert.ToInt32(danGroup);
            foreach (var ti in list)
            {
                if (ti == null) continue;
                if (TiGroup(ti) != dg) continue;
                if (TiEnable(ti)) return true;
            }
            return false;
        }

        // Re-assert BP's bend for ONE male's ChaControl: OCIChar.ActiveFK(danGroup,false) then (danGroup,true).
        // Returns true only if the toggle was actually performed (penis FK group existed AND was active).
        // No-op + false if there's no FK penis or its FK is already off (nothing fighting BP).
        public static bool ReassertBend(Component chaControl, out string reason)
        {
            reason = null;
            Init();
            if (!_ok) { reason = "bridge unavailable"; return false; }
            if (chaControl == null) { reason = "null ChaControl"; return false; }
            try
            {
                object danGroup = ResolveDanGroup();
                if (danGroup == null) { reason = "no penis dan FK group registered (no FK penis)"; return false; }
                object oci = ResolveOciChar(chaControl);
                if (oci == null) { reason = "OCIChar not found for this male"; return false; }
                bool fkActive = IsDanFKActive(oci, danGroup);
                if (!fkActive) { reason = "penis FK group not active (nothing to fix)"; return false; }
                _activeFK.Invoke(oci, new object[] { danGroup, false, false });   // off -> ResetFKNodes -> BP bend shows
                _activeFK.Invoke(oci, new object[] { danGroup, true,  false });   // on  -> rebuild FK from BP-bent pose
                return true;
            }
            catch (Exception e)
            {
                reason = "exception: " + e.Message;
                LiquidWobbleMPBPlugin._logger?.LogWarning("PenisFKBridge.ReassertBend failed: " + e.Message);
                return false;
            }
        }

        // Disable FK apply for ONLY this male's dan bones by nulling each dan GuideObject.transformTarget.
        // KK_AdditionalFKNodes' own GuideObject.LateUpdate prefix then skips those bones (returns false when
        // transformTarget==null), so stateless BP (AimDanPoints, absolute world write each LateUpdate) owns the
        // bend LIVE — no frozen snapshot, follows the target as it moves. Per-bone scoped: body GuideObjects are
        // untouched (matched by transform name against the cm_j_dan* scan). Idempotent (already-null target -> skip).
        // The FK plugin re-binds transformTarget on reload, so we re-run this every scene load (after danTargetsValid).
        public static bool DisableDanFK(Component chaControl, out int disabledCount, out string reason)
        {
            disabledCount = 0; reason = null;
            Init();
            if (!_ok) { reason = "bridge unavailable"; return false; }
            if (chaControl == null) { reason = "null ChaControl"; return false; }
            try
            {
                object oci = ResolveOciChar(chaControl);
                if (oci == null) { reason = "OCIChar not found"; return false; }
                var danNames = BPBridge.DanBoneNames(chaControl);
                if (danNames.Count == 0) { reason = "no dan bones"; return false; }
                var list = _ociListBonesF.GetValue(oci) as IEnumerable;
                if (list == null) { reason = "OCIChar.listBones null"; return false; }
                foreach (var bi in list)
                {
                    if (bi == null) continue;
                    // guideObject is a PROPERTY (auto-prop) — GetProperty, with backing-field fallback. Bind once.
                    if (!_guideBound)
                    {
                        _guideBound = true; var bt = bi.GetType();
                        _obiGuidePropP = bt.GetProperty("guideObject", BF);
                        if (_obiGuidePropP == null) _obiGuideF = bt.GetField("guideObject", BF) ?? bt.GetField("<guideObject>k__BackingField", BF);
                    }
                    if (_obiGuidePropP == null && _obiGuideF == null) { reason = "no guideObject member"; return false; }
                    var go = _obiGuidePropP != null ? _obiGuidePropP.GetValue(bi, null) : _obiGuideF.GetValue(bi);
                    if (go == null) continue;
                    // transformTarget is a PROPERTY too — bind once, get + null via property (backing-field fallback).
                    if (!_tgtBound)
                    {
                        _tgtBound = true; var gt = go.GetType();
                        _goTargetPropP = gt.GetProperty("transformTarget", BF);
                        if (_goTargetPropP == null) _goTargetF = gt.GetField("transformTarget", BF) ?? gt.GetField("<transformTarget>k__BackingField", BF);
                    }
                    if (_goTargetPropP == null && _goTargetF == null) { reason = "no transformTarget member"; return false; }
                    var t = (_goTargetPropP != null ? _goTargetPropP.GetValue(go, null) : _goTargetF.GetValue(go)) as Transform;
                    if (t == null) continue;                          // already disabled / no target -> idempotent
                    if (!danNames.Contains(t.name)) continue;          // not a dan bone -> leave the body's FK intact
                    if (_goTargetPropP != null && _goTargetPropP.CanWrite) _goTargetPropP.SetValue(go, null, null);  // DISABLE FK for this bone -> BP drives it
                    else if (_goTargetF != null) _goTargetF.SetValue(go, null);
                    else { reason = "transformTarget not settable"; return false; }
                    disabledCount++;
                }
                return disabledCount > 0;
            }
            catch (Exception e)
            {
                reason = "exception: " + e.Message;
                LiquidWobbleMPBPlugin._logger?.LogWarning("PenisFKBridge.DisableDanFK failed: " + e.Message);
                return false;
            }
        }

        // CURE (build 353). The penis dan FK nodes (cm_J_dan103/105/107_00 + foreskin 119 — the ONLY shaft bones that
        // KK_AdditionalFKNodes registers as FK nodes; every other shaft bone has no FK node and is always BP-driven,
        // which is exactly why ONLY the odd ones ever stick) live in BoneGroup BODY (Tools.GetBoneGroup(0)=Body=1),
        // SHARED with the whole skeleton. So ActiveFK(group,false) would kill whole-body FK — UNUSABLE. The lever is
        // per-bone: TargetInfo.enable (proven by the log — en=1 -> stuck, en=0 -> BP-driven). We set .enable=false on
        // JUST the dan TargetInfos and deactivate their GuideObjects (mirrors what ActiveFK(false) does per bone, the
        // manual "FK off" the user confirmed) so stateless BP owns the chain. Body FK is untouched. Idempotent: skips
        // bones already off, so it's safe to call every frame / from the ActiveFK postfix that re-asserts after the
        // load re-enable. NEVER calls ActiveFK (would re-pose the body AND recurse through the enforcer postfix).
        public static bool DisablePenisFK(Component chaControl, out string info)
        {
            info = null;
            Init();
            if (!_ok) { info = "bridge unavailable"; return false; }
            if (chaControl == null) { info = "null ChaControl"; return false; }
            try
            {
                object oci = ResolveOciChar(chaControl);
                if (oci == null) { info = "OCIChar not found"; return false; }
                var danNames = BPBridge.DanBoneNames(chaControl);
                if (danNames.Count == 0) { info = "no dan bones"; return false; }

                // 1) Disable each enabled dan TargetInfo (the FK-apply gate).
                object fk = _fkCtrlF != null ? _fkCtrlF.GetValue(oci) : null;
                var fkList = (fk != null && _listBonesF != null) ? _listBonesF.GetValue(fk) as IEnumerable : null;
                int disabled = 0; var names = new System.Collections.Generic.List<string>();
                if (fkList != null)
                    foreach (var ti in fkList)
                    {
                        if (ti == null) continue;
                        var bone = TiBone(ti); if (bone == null || !danNames.Contains(bone.name)) continue;
                        if (!TiEnable(ti)) continue;                 // already off -> idempotent
                        SetTiEnable(ti, false);
                        disabled++; names.Add(bone.name.Replace("cm_J_dan", "").Replace("_00", ""));
                    }

                // 2) Deactivate the matching GuideObjects (mirror ActiveFK(false) per bone — belt & suspenders so the
                //    studio guide's own LateUpdate can't re-write the bone even if it ignores TargetInfo.enable).
                if (disabled > 0)
                {
                    var bl = _ociListBonesF != null ? _ociListBonesF.GetValue(oci) as IEnumerable : null;
                    if (bl != null)
                        foreach (var bi in bl)
                        {
                            if (bi == null) continue;
                            if (!_guideBound) { _guideBound = true; var bt = bi.GetType(); _obiGuidePropP = bt.GetProperty("guideObject", BF); if (_obiGuidePropP == null) _obiGuideF = bt.GetField("guideObject", BF) ?? bt.GetField("<guideObject>k__BackingField", BF); }
                            object go = _obiGuidePropP != null ? _obiGuidePropP.GetValue(bi, null) : (_obiGuideF != null ? _obiGuideF.GetValue(bi) : null);
                            if (go == null) continue;
                            if (!_tgtBound) { _tgtBound = true; var gt = go.GetType(); _goTargetPropP = gt.GetProperty("transformTarget", BF); if (_goTargetPropP == null) _goTargetF = gt.GetField("transformTarget", BF) ?? gt.GetField("<transformTarget>k__BackingField", BF); }
                            var tt = _goTargetPropP != null ? _goTargetPropP.GetValue(go, null) as Transform : (_goTargetF != null ? _goTargetF.GetValue(go) as Transform : null);
                            if (tt == null || !danNames.Contains(tt.name)) continue;
                            var sa = _goSetActiveM ?? (_goSetActiveM = go.GetType().GetMethod("SetActive", new[] { typeof(bool), typeof(bool) }));
                            if (sa != null) { try { sa.Invoke(go, new object[] { false, true }); } catch { } }
                        }
                }

                if (disabled == 0) { info = "penis FK already off (BP owns the chain)"; return false; }
                info = "per-bone enable=false on dan node(s) [" + string.Join(",", names.ToArray()) + "] -> BP drives";
                return true;
            }
            catch (Exception e)
            {
                info = "exception: " + e.Message;
                LiquidWobbleMPBPlugin._logger?.LogWarning("PenisFKBridge.DisablePenisFK failed: " + e.Message);
                return false;
            }
        }
    }

    // ENFORCER (build 353). On scene load KK_AdditionalFKNodes re-applies the SAVED activeFK[] per group
    // (AddAdditionalNodes -> ActiveFK(Body,true)), which re-enables the penis dan FK a moment AFTER our on-load
    // disable ran — re-pinning the bends BP wants to drive. A one-shot disable therefore loses the race. This
    // Harmony postfix on OCIChar.ActiveFK re-disables the dan FK nodes every time Body FK gets enabled (load
    // re-apply, manual panel toggle, KKPE IK post-update — all of them), so the penis stays BP-driven. Per-bone
    // and body-safe; idempotent; DisablePenisFK never calls ActiveFK so there's no recursion through this postfix.
    internal static class PenisFKEnforcer
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool _applied;
        private static HarmonyLib.Harmony _harmony;

        public static void TryApply()
        {
            if (_applied) return;
            try
            {
                var ociType = HarmonyLib.AccessTools.TypeByName("Studio.OCIChar") ?? HarmonyLib.AccessTools.TypeByName("OCIChar");
                if (ociType == null) return;   // Studio not ready yet — retry on next CharacterReloaded
                System.Reflection.MethodInfo m = null;
                foreach (var mi in ociType.GetMethods(BF)) if (mi.Name == "ActiveFK" && mi.GetParameters().Length == 3) { m = mi; break; }
                if (m == null)
                {
                    _applied = true;
                    LiquidWobbleMPBPlugin._logger?.LogWarning("PenisFKEnforcer: OCIChar.ActiveFK(3 args) not found — penis-FK enforcer NOT installed.");
                    return;
                }
                if (_harmony == null) _harmony = new HarmonyLib.Harmony("Clo.LiquidWobbleMPB.penisfk");
                _harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(PenisFKEnforcer).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
                _applied = true;
                LiquidWobbleMPBPlugin._logger?.LogInfo("PenisFKEnforcer: patched OCIChar.ActiveFK (keeps penis dan FK off so BP drives the bend).");
            }
            catch (Exception e)
            {
                _applied = true;
                LiquidWobbleMPBPlugin._logger?.LogWarning("PenisFKEnforcer: install failed (" + e.Message + ") — penis FK may re-enable on load.");
            }
        }

        // Re-entrancy guard: SetActive(false) on a guide can't call ActiveFK, but be defensive.
        private static bool _inside;
        private static void Postfix(object __instance, bool _active)
        {
            if (!_active || _inside || __instance == null) return;   // only when FK is being turned ON
            if (!WombExpandEffect.EffectiveActive) return;            // mod off or no CloXray womb -> leave the user's penis FK alone
            try
            {
                _inside = true;
                var ccF = __instance.GetType().GetField("charInfo", BF);
                var cc = ccF != null ? ccF.GetValue(__instance) as Component : null;
                if (cc == null) return;
                // Only hand the dan chain to BP when BP is ACTUALLY enabled+driving this male (danTargetsValid). A male
                // with a hand-FK-posed, non-BP penis is left completely alone — we never clear FK out from under the user.
                if (!BPBridge.HasActiveBp(cc)) return;
                string info;
                if (PenisFKBridge.DisablePenisFK(cc, out info))
                    LiquidWobbleMPBPlugin._logger?.LogInfo("PenisFKEnforcer: re-disabled penis FK after ActiveFK on '" + cc.name + "' (" + info + ").");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("PenisFKEnforcer.Postfix: " + e.Message); }
            finally { _inside = false; }
        }
    }
}
