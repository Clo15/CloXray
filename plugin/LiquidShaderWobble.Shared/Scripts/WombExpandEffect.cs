using System;
using UnityEngine;

namespace LiquidWobbleMPB
{
    /// Drives the womb canal blendshapes from BetterPenetration, mirroring BP's own squish math.
    public class WombExpandEffect : MonoBehaviour
    {
        // Rings entrance->deep, AUTO-DRIVEN by the plugin. Index 0 is the entrance (Vagina_1_open); the LAST
        // listed ring is the deepest, driven by CervixWeight (opens last, at deepest insertion).
        public string RingBlendShapes { get; set; } =
            "Vagina_1_open,Vagina_2_low,Vagina_3_mid,Vagina_4_upper,Vagina_5_top";
        // Deep-insertion displacement shape.
        public string StretchBlendShape { get; set; } = "womb_displace";
        public string StrengthBlendShape { get; set; } = "BP_Strength";
        public string DampeningBlendShape { get; set; } = "BP_Dampening";
        // Inert per-object toggle: weight > 50 => this womb IGNORES DynamicBoneColliders (it will NOT react
        // to a collider/toy pushed in) while STILL reacting to the BP penis.
        public string IgnoreCollidersShape { get; set; } = "BP_IgnoreColliders";
        // Inert per-object penetration SENSITIVITY (the engagement TRIGGER, NOT the opening amount, which is
        // BP_Strength).
        public string SensitivityBlendShape { get; set; } = "BP_Sensitivity";
        // Entrance direction-reaction: lean the mouth toward the incoming penis (read from BP tip dir).
        public string MoundForwardShape { get; set; } = "moundforward";
        public string MoundBackShape { get; set; } = "moundback";
        // Left/right lean (local X) - the lateral pair that lets the mouth follow a penis offset SIDEWAYS,
        // matching BP's own kokan-pull (BP translates the vagina along the full 3D penis direction).
        public string MoundLeftShape { get; set; } = "moundleft";
        public string MoundRightShape { get; set; } = "moundright";

        // Normalized depths: the entrance ring (V1, very bottom) opens EARLY (with BP entry) -> low Depth
        // Start; the cervix (V5) opens deep -> Depth End.
        public float DepthStart { get; set; } = 0.10f;
        public float DepthEnd { get; set; } = 0.97f;

        // Full-open weights (0..100).
        public float RingWeight { get; set; } = 90f;
        public float EntranceWeight { get; set; } = 90f;
        public float CervixWeight { get; set; } = 85f;
        // Leading-edge softness in normalized depth. Wider = more gradual opening (like BP).
        public float OpenWidth { get; set; } = 0.18f;
        public float DepthSmoothing { get; set; } = 12f;

        // Girth -> width. RefGirth (default penis base radius) maps to the full weights above.
        public float RefGirth { get; set; } = 0.0213f;

        // Depth (BP visualDepth) at which the tip sits at the womb mouth = normalized 1.0.
        public float FullDepthIn { get; set; } = 0.83f;

        // womb_displace driver: ramps from StretchStart (normalized depth) to 1.0 reaching StretchMax; past
        // 1.0 (overshoot) keeps growing by StretchOvershoot per unit, capped at StretchCap.
        public float StretchMax { get; set; } = 12f;
        public float StretchStart { get; set; } = 0.5f;     // start displacing earlier (was 0.8)
        public float StretchOvershoot { get; set; } = 25f;
        public float StretchCap { get; set; } = 100f;   // FIXED at 100 — a >100 blendshape write latches/sticks in KKPE, so this was always 100; no longer a cfg (to displace more, strengthen the womb_displace SHAPE in the mesh)
        // Displace scales with BP penis LENGTH (m_baseDanLength): a longer / deeper-set penis displaces
        // the womb MORE. lengthScale = clamp(baseLen / RefLength, 0.6, 2). Set RefLength to your default
        // penis's logged baseLen (see the WombExpand debug line). DirReactWeight = how far the mouth
        // leans toward the incoming penis direction (moundforward/back); 0 = off, "a little" ~25.
        public float RefLength { get; set; } = 0.10f;
        public float DirReactWeight { get; set; } = 25f;

        // behind it (follow-through).
        public float OpenLead { get; set; } = 0.06f;
        public float CloseSmoothing { get; set; } = 4f;
        public float EntranceOpenWidth { get; set; } = 0.30f;
        public float EntranceCloseScale { get; set; } = 2f;
        // EntranceOpenScale makes V1 open that-many-times slower; MaxGirthScale caps girth->width scaling.
        public float OpenTime { get; set; } = 0.2f;
        public float EntranceOpenScale { get; set; } = 2f;
        public float MaxRingWeight { get; set; } = 100f;   // FIXED at 100 — a >100 blendshape write latches/sticks in KKPE; no longer a cfg (widen the mesh tube to open beyond this)
        public float MaxGirthScale { get; set; } = 2.5f;

        // Below this raw depth the penis is treated as not penetrating (BP parks lastDanDistance at baseLen
        // -> depth 0 when out), so everything rests closed.
        private const float EngageEps = 0.01f;
        // Lateral floor for "the penis tip threads the canal" (metres).
        private const float PenisInCanalWidth = 0.06f;

        private SkinnedMeshRenderer _smr;
        private int[] _ringIdx;
        private int _stretchIdx = -1;

        // MAIN-GAME press feed (0..1): how hard the pinned penis is being squished against the pin.
        public float ExternalPress;

        // MAIN-GAME intent tip (Studio-copy stretch): world position where the penis tip WOULD be if nothing
        // yielded (base + natural length along the aim direction).
        public Vector3 ExternalIntentTip;
        public bool HasIntentTip;
        private float _hIntentDepth;
        public float HIntentDepth { get { return _hIntentDepth; } }
        // MAIN-GAME engagement grace: hold-open window + the drive values frozen through a dropout.
        private float _hEngHold, _hEngDepth, _hEngGirth = 1f, _hEngLen = 1f;
        private float _hStrokeMax, _hOnset;   // MAIN GAME: auto-calibrated deepest stroke reach + the contact-point onset (mm)
        private float _girthLogNext;          // b664: throttle for the GIRTH-DRIVE diagnostic
        private bool _bpHadGirthThisFrame;    // b666: BP reported a real girthTip this frame -> collider latch stands down
        private float _tipMinMM;              // b669: shallow-end tip depth (parked-overshoot baseline for the displace drive)
        private float _girthRiseMM;           // b670: rise-only live girth (canal follows BP inflation UP at the limit, never down)
        // MAIN-GAME commanded stroke depth (mm past the entrance), fed by BPInnerTargetPin's sweep.
        public float ExternalStrokeMM;
        public float ExternalCompressMM;
        public bool ExternalStrokeTrusted = true;

        public bool ExternalFitLocked = true;

        // Current womb_displace reaction weight (0..100).
        public float CurrentStretchWeight { get { return _stretchReaction; } }
        // weight).
        public float CurrentDomeTravelMM { get { return Mathf.Clamp01(_stretchReaction * 0.01f) * 0.71f * _canalLen * 1000f; } }
        // Final (post-grace) engaged state - the pin/fill logic reads this to detect a real pull-out.
        public bool IsEngaged { get { return _dbgEngaged; } }
        public bool CanalReady { get { return _canalLen > 1e-4f; } }
        public bool CanalCalibrated { get { return _canalCalibrated; } }   // b618: loop must wait for the averaged calibration
        public Vector3 CanalEntranceW { get { return _canalEntrance; } }
        public void RecalibrateCanal()
        {   // re-run the averaged calibration from scratch (the mesh was re-baked)
            _canalCalibrated = false;
            _calN = 0; _calSumEnt = Vector3.zero; _calSumTop = Vector3.zero; _calSumDia = 0f; _calDiaN = 0; _calPrevFrac = -1f;
        }
        // Canal geometry in WORLD space (re-projected each frame).
        public bool HasCanal { get { return _canalLen > 1e-4f; } }
        public Vector3 CanalEntranceWorld { get { return _canalEntrance; } }
        public Vector3 CanalAxisWorld { get { return _canalAxis; } }
        public float CanalLenWorld { get { return _canalLen; } }
        private int _strengthIdx = -1;
        private int _dampeningIdx = -1;
        private int _ignoreCollidersIdx = -1;
        private int _sensIdx = -1;
        private float _sens = 1f;          // penetration sensitivity factor this frame (1.0 = default); scales the containment lateral gate
        private Transform _entranceBone;     // item's clo_cf_j_kokan = canal-entrance proxy (auto-BodyReveal proximity)

        private float[] _ringReaction;   // plugin contribution (pre-strength) per ring, for decay
        private float _stretchReaction;
        private int _moundFwdIdx = -1, _moundBackIdx = -1, _moundLeftIdx = -1, _moundRightIdx = -1;
        private float _moundFwdReact, _moundBackReact, _moundLeftReact, _moundRightReact;   // direction-reaction contribution, for decay

        private float _depth;            // smoothed normalized depth
        private bool _ready;
        private BPBridge.Reading _bp;
        // Canal axis (entrance->top, world) for collider-depth, baked + cached on a 2s timer (EnsureCanal).
        private Vector3 _canalEntrance, _canalAxis = Vector3.up;
        private float _anchorBpLogNext;   // b740 ANCHOR-VS-BP(rebind) throttle
        private float _engMissT, _engMissLogNext;   // b746 ENGAGE-TRACE state
        private float _canalLen, _canalTimer;
        private float _colLateral = -1f;   // when a collider is in the canal, its centre's lateral offset from the axis (m); -1 = none (debug)
        private float _bpLateral = -1f;    // BP penis tip's lateral offset from the canal axis (m); -1 = no pose/no bake (debug)
        private float _bpClosest = -1f;    // closest any shaft bone came to the canal axis when NOT contained (debug)
        private float[] _ringDepths;       // each ring's normalized canal depth (DepthStart..DepthEnd), for the collider per-ring scan
        private float[] _colRingRadius;    // per-ring MAX collider world radius (thickest collider reaching that ring) — multi-collider taper
        private bool _colPerRing;          // true when the collider path drove with per-ring radii available
        private bool  _bpVaginaMain;   // engaged via BP-depth on the paired vagina without bone-containment (the bones sit below the cervix).
        private bool  _bpGeomDepth;        // depth came from the tip's position in the canal (BP reported none)
        private bool  _tipDetached;        // tip marker k_f_dan_end pulled beyond the detach distance -> treated as withdrawn (debug)
        private float _tipDist = -1f;      // distance from the womb entrance to k_f_dan_end (m); -1 = no tip (debug)
        private bool  _entryDetached;      // entry marker k_f_dan_entry swung off the canal axis (penis base withdrawn) -> withdrawn (debug)
        private float _entryLat = -1f;     // lateral distance from the canal axis to k_f_dan_entry (m); -1 = no entry (debug)
        // Vagina pairing: is THIS womb seated on a character's cf_J_Vagina_root? Cached on a 2s timer (EnsureVaginaPairing).
        // The MAIN penis signal: when BP penetrates the vagina this overlay sits on, react — bone geometry can't be trusted
        // (length-squish + overlay seating bunch the dan bones far below the cervix while the mesh/BP sit deep).
        private bool  _vaginaPaired;
        private bool  _vaginaFarLat;       // diag: vagina-paired but the penis is laterally beyond THIS womb's gate (it's in another/adjacent womb)
        private float _vaginaDist = -1f;   // nearest cf_J_Vagina_root distance to the entrance (m); -1 = none (debug)
        private float _vaginaTimer;
        private string _colName;           // name of the collider currently driving this womb (debug)
        private bool  _colWarned;          // collider-react exception logged once
        private bool  _dbgEngaged;   // engagement state, exposed via IsEngaged; gates calibration + the mesh reaction
        private int    _seenRepairVersion;           // last _repairVersion this womb consumed -> a bump re-pairs it on the next post-IK frame (FULLY event-driven, no poll/fallback)
        private string _pairedName;    // the character whose penis is paired to THIS womb (by entry node); read locked each frame
        private Vector3 _grabEntryW;   // paired penis's k_f_dan_entry world pos, read POST-NC in onPreCull (feeds the entry-detach gate)
        private bool  _handoffLogged;      // one-shot: BP_Strength=0 hand-off logged

        private void Start()
        {
            if (!Resolve())
            {
                LiquidWobbleMPBPlugin._logger?.LogError(
                    $"{nameof(WombExpandEffect)} on '{name}': ring blendshapes not found; disabling.");
                enabled = false;
                return;
            }
            _ready = true;
            if (s_carryEff > 1e-3f) _openEfficiency = s_carryEff;   // b632: spawn at the last measured opening (see field)
            LiquidWobbleMPBPlugin._logger?.LogInfo(
                $"{nameof(WombExpandEffect)} on '{name}': READY. mesh='{_smr.name}', rings={_ringIdx.Length}, " +
                $"stretch={_stretchIdx >= 0}, strengthCtl={_strengthIdx >= 0}, dampCtl={_dampeningIdx >= 0}, ignoreColCtl={_ignoreCollidersIdx >= 0}, sensCtl={_sensIdx >= 0}.");
        }

        private static float Median(System.Collections.Generic.List<float> xs)
        {
            if (xs.Count == 0) return 0f;
            xs.Sort();
            return xs[xs.Count / 2];
        }

        // Median XZ center of the tube cross-section at local height yb, restricted to a column of radius
        // `rad` around (cx,cz) so ovaries/womb body don't skew.
        private static bool RingCenter(Vector3[] v, float yb, float halfBand, float cx, float cz, float rad, out float ox, out float oz, out int cnt, out float spread)
        {
            var xs = new System.Collections.Generic.List<float>();
            var zs = new System.Collections.Generic.List<float>();
            float r2 = rad * rad;
            for (int i = 0; i < v.Length; i++)
            {
                if (Mathf.Abs(v[i].y - yb) > halfBand) continue;
                float dx = v[i].x - cx, dz = v[i].z - cz;
                if (dx * dx + dz * dz > r2) continue;
                xs.Add(v[i].x); zs.Add(v[i].z);
            }
            cnt = xs.Count;
            ox = Median(xs); oz = Median(zs);
            spread = 0f;
            if (cnt > 0)
            {
                var ds = new System.Collections.Generic.List<float>(cnt);
                for (int i = 0; i < xs.Count; i++) { float ddx = xs[i] - ox, ddz = zs[i] - oz; ds.Add(Mathf.Sqrt(ddx * ddx + ddz * ddz)); }
                spread = Median(ds);
            }
            return cnt >= 4;
        }

        private Vector3[] _canalBandW;     // canal endpoints (entrance, cervix) in WORLD — re-projected each frame
        private Vector3[] _canalLocal;     // canal endpoints (entrance, cervix) in LOCAL/rest space (constant; baked once)
        private float _canalLocalLen;      // pure REST canal length (sharedMesh space) — live scale re-applied per frame
        private float _canalRestLen;       // world canal length BEFORE the H base-stretch elongation (for the base displace weight)
        private float _baseStretchPctEff;  // b553: config base-stretch % + the hard-wired 6mm down-extension %
        private Transform _ptBone;         // the womb's aim bone — penis_target2 (canal-frame, b499) or the old penis_target
        // The character this womb sits in. Studio answers exactly (the item is parented to her, or her node
        // is above it in the workspace tree); elsewhere it falls back to the nearest vagina/crotch bone.
        // Resolved on the pairing event, not per frame, and re-resolved whenever the womb re-pairs.
        private Component _wearer;
        private int _wearerSeenVersion = -1;
        private Component Wearer()
        {
            if (_wearerSeenVersion != _repairVersion)
            {
                _wearerSeenVersion = _repairVersion;
                string how;
                _wearer = AutoBodyReveal.ResolveWearer(this, out how);
            }
            return _wearer;
        }

        private Transform FindPenisTargetBone()
        {
            // penis_target in the whole scene.
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == "penis_target2") return t;   // the bone the pin actually targets (b499)
            // b629: under REBIND the canal anchor (+PT2 child) is reparented under HER cf_j_kokan —
            // outside this womb's subtree — so search the wearer too. PT2's name is unique on her.
            if (transform.parent != null)
                foreach (var t in transform.parent.GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == "penis_target2") return t;
            return null;
        }
        // Penis BASE world position, fed by BPInnerTargetPin.
        public Vector3 ExternalPenisBase;
        public bool HasPenisBase;
        // BP's reference-target (the VAGINA bone BP aims the penis at).
        internal bool WearerHadBPVagina;
        // The wearer's body-uncensor GUID, captured while she still had her vagina bones.
        internal string WearerBodyGuid;
        // dan bones.
        internal string PenetratorPenisGuid;
        internal string PenetratorBallsGuid;
        public Vector3 ExternalPenisRef;
        public bool HasPenisRef;
        // BP's real penis endpoint (m_danPoints.danEnd).
        public Vector3 ExternalPenisEnd;
        public bool HasPenisEnd;
        // The GAME's own penetration state (HFlag motion name, BP's rule).
        public bool ExternalPenetrated;
        public bool HasPenetratedFlag;
        private Transform _canalBone;      // the mesh build clo_canal_entry marker bone (if the mesh has it) -> exact, scale/rotation-native
        private bool _canalCalWarned;   // one-shot: CANAL-CAL saw an impossible error and refused to act
        private float _bakedCanalLen;   // b561: true world canal length measured from the bake at calibration
        private const float RefCanalLen = 0.085f;   // b566: reference womb canal (default female) for width scale-compensation
        private float _lastGirthScale = 1f;   // b570: girthScale used last frame (for the opening-efficiency measure)
        private float _openEfficiency;        // b570: measured mm canal-diameter per girthScale unit (the mesh's true opening rate)
        // b632 — "precalculate expansion and spawn it at correct stretch": the efficiency is a
        // mesh+scale property, stable across respawns of the same womb — carry the last measured value
        // so a respawn (auto-parity iteration, mode toggle, pose change) opens at the CORRECT width
        // immediately instead of running the over-predicting RefGirth model until the phase-locked
        // calibration (up to 2 idle loops) re-seeds it ("spawns at max expansion, then slowly reduces").
        // The calibration still overwrites it with the fresh averaged measure when it completes.
        private static float s_carryEff;
        // so there is no first-measure "snap" state to track.
        private static string s_diaMale; private static float s_diaMM;
        private static string s_diaLoggedMale; private static float s_diaLoggedMM;   // last value the LATCHED line reported
        private float[] _diaBuf; private int _diaN;
        private static string s_bpMale; private static float s_bpDiaMM;
        private float[] _bpDiaBuf; private int _bpDiaN;
        private bool _bpRelatch;   // a pose change asked for a fresh median; the old width stays live meanwhile
        // b738: name-keyed resets never fire on a CARD SWAP (dan names collide across male cards; the
        // collider-latch key is a constant string by b651 design), so a swapped-in male inherited the
        // previous male's latch (measured: bpLatch 44.2mm vs live tip 24.8mm on the small male = canal
        // 2x too wide). Natural dan length is the stable per-male identity — reset EVERYTHING girth
        // (both latches, both median buffers, the b736 rise) when it changes.
        private static float s_latchNatural;
        // sizes the canal wrong for the new one.
        private int _girthPoseVer = -1;
        private void ResetGirthOnPoseChange()
        {
            if (MainGameWomb.IsStudio) return;
            int pv = MainGameWomb.PoseVersion;
            if (_girthPoseVer == pv) return;
            if (!ExternalFitLocked) return;
            bool had = _girthPoseVer >= 0 && (s_bpDiaMM > 0f || s_diaMM > 0f);
            _girthPoseVer = pv;
            if (!had) return;
            // Restart the medians but keep the current width live: blanking it drove the canal from 0mm for
            // the ~15 frames the new median takes ("bpLatch=0.0mm" in the log), so the canal collapsed and re-opened on every pose change.
            _bpRelatch = true; _bpDiaN = 0; _diaN = 0; _girthRiseMM = 0f;
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: girth re-measure armed (pose v" + pv
                + ") — re-measuring his girth for the new pose.");
        }

        private void ResetGirthOnNewMale()
        {
            float natNow = BPInnerTargetPin.NaturalDanLen;
            if (natNow <= 1e-3f || Mathf.Abs(natNow - s_latchNatural) <= 0.002f) return;
            s_latchNatural = natNow;
            s_bpDiaMM = 0f; _bpDiaN = 0; s_diaMM = 0f; _diaN = 0; _girthRiseMM = 0f;
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: girth latches RESET — new male (natural "
                + (natNow * 1000f).ToString("F0") + "mm); re-measuring his girth.");
        }
        private void LatchBPGirth(float diaMM, string key)
        {
            ResetGirthOnNewMale();   // b738
            ResetGirthOnPoseChange();
            if (diaMM <= 1e-3f) return;
            if (key == null) key = "BP";
            if (key != s_bpMale) { s_bpMale = key; s_bpDiaMM = 0f; _bpDiaN = 0; _girthRiseMM = 0f; }   // b670: new male -> reset the rise too
            if (s_bpDiaMM > 0f && !_bpRelatch) return;
            if (_bpDiaBuf == null) _bpDiaBuf = new float[15];
            if (_bpDiaN < _bpDiaBuf.Length) _bpDiaBuf[_bpDiaN++] = diaMM;
            if (_bpDiaN >= _bpDiaBuf.Length)
            {
                var srt = (float[])_bpDiaBuf.Clone(); System.Array.Sort(srt); s_bpDiaMM = srt[srt.Length / 2]; _bpRelatch = false;
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: BP penis girth LATCHED at " + s_bpDiaMM.ToString("F1")
                    + "mm for '" + s_bpMale + "' (median of 15 BP reads) — OWNS the canal opening over the collider path.");
            }
        }

        private void LatchGirth(float diaMM, string key)
        {
            ResetGirthOnNewMale();   // b738
            ResetGirthOnPoseChange();
            if (diaMM <= 1e-3f) return;
            if (key == null) key = "H-collider";
            if (key != s_diaMale) { s_diaMale = key; s_diaMM = 0f; _diaN = 0; }
            if (s_diaMM > 0f && !_bpRelatch) return;
            if (_diaBuf == null) _diaBuf = new float[15];
            if (_diaN < _diaBuf.Length) _diaBuf[_diaN++] = diaMM;
            if (_diaN >= _diaBuf.Length)
            {
                var sortedDia = (float[])_diaBuf.Clone();
                System.Array.Sort(sortedDia);
                s_diaMM = sortedDia[sortedDia.Length / 2];
                // Log the LATCH, not every frame it stays latched. _diaN is never reset, so once the
                // buffer is full this block runs every frame a re-latch is armed - 49 identical lines in
                // one session, all claiming to be a one-off. Only a value that actually moved is news.
                if (s_diaMale != s_diaLoggedMale || Mathf.Abs(s_diaMM - s_diaLoggedMM) > 0.05f)
                {
                    s_diaLoggedMale = s_diaMale; s_diaLoggedMM = s_diaMM;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: penis diameter LATCHED at "
                        + s_diaMM.ToString("F1") + "mm for '" + s_diaMale + "' (median of "
                        + _diaBuf.Length + " reads; constant until another male appears).");
                }
            }
        }
        private const float WidthMargin = 1.15f;   // b669 — "slightly thicker": 15% room (was 10%; b583 grounding keeps it poke-free)

        private bool TryCanal(out Vector3 entranceW, out Vector3 topW)
        {
            entranceW = topW = Vector3.zero;
            try
            {
                // TRUE REST structure: the SHARED MESH vertices.
                Vector3[] v = _smr.sharedMesh != null ? _smr.sharedMesh.vertices : null;
                if (v == null) return false;
                float minY = float.MaxValue;                                // (orientation-robust: works for a rotated / anus-oriented womb)
                for (int i = 0; i < v.Length; i++) if (v[i].y < minY) minY = v[i].y;
                float seedx, seedz, seedSpread; int sc;
                RingCenter(v, minY + 0.012f, 0.014f, 0f, 0f, 1f, out seedx, out seedz, out sc, out seedSpread);
                var centers = new System.Collections.Generic.List<Vector3>();
                float cx = seedx, cz = seedz, entranceSpread = -1f, topSpread = 0f;
                for (float off = 0.004f; off <= 0.22f; off += 0.011f)
                {
                    float yb = minY + off, ox, oz, spread; int cnt;
                    bool ok = RingCenter(v, yb, 0.009f, cx, cz, 0.040f, out ox, out oz, out cnt, out spread);
                    if (!ok) { if (centers.Count == 0) continue; else break; }
                    if (centers.Count > 0 && (spread > 0.030f || spread > entranceSpread * 2.4f)) break;   // entered the uterus -> just past the cervix
                    if (centers.Count == 0) entranceSpread = Mathf.Max(spread, 0.004f);
                    centers.Add(new Vector3(ox, yb, oz)); cx = ox; cz = oz; topSpread = spread;
                }
                if (centers.Count < 2) { return false; }
                entranceW = _smr.transform.TransformPoint(centers[0]);
                topW = _smr.transform.TransformPoint(centers[centers.Count - 1]);
                // The canal is a straight axis: cache only the two endpoints (entrance + cervix) in LOCAL
                // space.
                _canalLocal = new Vector3[] { centers[0], centers[centers.Count - 1] };
                _canalLocalLen = Vector3.Distance(centers[0], centers[centers.Count - 1]);   // pure REST length; live scale re-applied per frame
                _canalBandW = new Vector3[] { _smr.transform.TransformPoint(_canalLocal[0]), _smr.transform.TransformPoint(_canalLocal[1]) };
                return true;
            }
            catch { return false; }
        }

        // Bake the canal SHAPE once. The local rest-mesh structure is CONSTANT.
        private void EnsureCanal(bool drive = false)
        {
            if (_canalLen > 1e-4f && _canalLocal != null && _canalLocal.Length >= 2) return;   // have the constant shape -> never re-bake
            _canalTimer -= Time.deltaTime;
            if (_canalTimer > 0f) return;
            _canalTimer = 0.05f;
            Vector3 entW, topW;
            if (TryCanal(out entW, out topW))
            {
                Vector3 ax = topW - entW; float l = ax.magnitude;
                // Only trust a plausible canal length (~77mm at unit scale; allow scaling).
                if (l >= 0.02f && l <= 0.5f)
                {
                    _canalEntrance = entW; _canalAxis = ax / l; _canalLen = l;
                    if (_canalBone == null)   // mesh bone supersedes the bake for entrance+axis (RefreshCanalWorld); search once
                    {
                        _canalBone = FindCanalBone();
                    }
                }
                else _canalLen = 0f;
            }
        }

        // POST-IK feed: like LiquidShaderWobbleEffect, sample from Camera.onPreCull so the canal bake
        // matches the rendered womb.
        private int _lastCanalFrame = -1;
        private Camera.CameraCallback _canalPreCull;
        private void OnEnable()
        {
            _canalPreCull = OnPreCullCanal;
            Camera.onPreCull += _canalPreCull;
            // A CloXray womb is now live. Bump the gate so the BP-interop hooks are allowed to act, install
            // them, and refresh the shared vagina-root list (this womb's character may have just spawned).
            _activeCount++;
            AutoBodyReveal.InstallWombHooks();
            InvalidateVaginaRoots();
            RequestRepair();   // pair THIS new womb on the first frame its canal bakes (also covers the very first womb, when _repairVersion is still 0)
        }
        private void OnDisable()
        {
            if (_canalPreCull != null) Camera.onPreCull -= _canalPreCull;
            if (_activeCount > 0) _activeCount--;   // last womb gone -> the BP-interop hooks go inert (AnyActive=false)
        }
        private void OnPreCullCanal(Camera cam)
        {
            if (!_ready) return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;     // master toggle OFF -> freeze (no canal re-bake / pairing / markers)
            if (Time.frameCount == _lastCanalFrame) return;   // first camera of the frame only
            _lastCanalFrame = Time.frameCount;
            _lastCanalCam = cam != null ? cam.name : "-";
            _lastCanalCamVisible = _smr != null && _smr.isVisible;
            EnsureCanal(true);          // authoritative post-IK re-bake (timed) -> refreshes the LOCAL/rest canal
            // b674 — "womb jumps ~1s after spawn": run the analytic placement predictor the INSTANT
            // the canal is acquired — it needs only the rest-mesh entrance band (built here) + her live
            // bones, NOT the phase-locked calibration below (which waits for her idle loop to cross a fixed
            // phase x2 = the ~1-3s the placement was blocked on). The nudge is stance-stable (it's the
            // structural mirror-vs-rebind DIFFERENCE, not an absolute sample — logs show an identical
            // (0,13.2,1.7) whenever it runs), so predicting early loses nothing. PredictParity self-guards
            // (_parityPredicted); the old call inside the calibration block becomes a no-op backstop.
            if (!_parityPredicted && MainGameWomb.CurrentlyRebound
                && _canalLen > 1e-4f && _canalLocal != null && _canalLocal.Length >= 2)
            {
                EnsureCanalBands();     // build _bandEnt from the rest mesh now (idempotent; no-op later)
                PredictParity();
            }
            CalibrateCanalToSkinnedMesh();   // b534: once, at rest — put the canal bone ON the true skinned canal
            RefreshCanalWorld();        // EVERY frame: re-project the cached LOCAL canal through the live transform
            MeasureOpenCanal();         // b570: measures the open-canal efficiency that feeds the ring drive

            // PAIRING - POST-NodesConstraints here, so k_f_dan_entry sits at its DRIVEN position.
            if (_seenRepairVersion != _repairVersion && _canalLen > 1e-4f)
            {
                _seenRepairVersion = _repairVersion;
                _pairedName = BPBridge.FindByEntry(_canalEntrance, _canalAxis, LiquidWobbleMPBPlugin.CfgPairRange, _pairedName, 0.02f, Wearer());
            }
            // axis, period.

            // ExternalStrokeMM with danEnd projected on the canal.
            if (!MainGameWomb.IsStudio && _canalLen > 1e-4f)
            {
                // b496: this override is now the ONLY ExternalStrokeMM source in H (the pin's L−d2
                // estimate is deleted). No readable danEnd => 0, never a stale last value.
                // A pose change re-seats the womb, and for a few frames afterwards the canal frame is the
                // OLD pose's. Projecting BP's endpoint onto it gives nonsense - measured lat=358mm and
                // along=535mm on an 84mm canal - and because the gate then fails we fed the womb a
                // FABRICATED ZERO, so it collapsed and re-inflated on every single pose change. That is
                // the "womb didn't react on the first pose" report. Verified over 30 pose changes: every
                // bad reading landed 3-5 log lines after a bump, and the user saw no visual fault at all.
                //
                // So: while the canal has not been re-calibrated for the CURRENT pose, drive nothing and
                // hold what we last measured. Holding existing state is not a fallback path - it is
                // declining to act on input we know is invalid, which is what the rule asks for.
                if (MainGameWomb.AimedForPose != MainGameWomb.PoseVersion)
                {
                    LogAim(0f, 0f, true, true);   // reported as re-seating, not as an aim fault
                }
                else if (HasPenisEnd)
                {
                    Vector3 er = ExternalPenisEnd - _canalEntrance;
                    float ea = Vector3.Dot(er, _canalAxis);
                    float el = (er - _canalAxis * ea).magnitude;
                    ExternalStrokeMM = (ea > 0f && el < _canalLen * 0.6f) ? ea * 1000f : 0f;
                    LogAim(ea, el, true);
                }
                else { ExternalStrokeMM = 0f; LogAim(0f, 0f, false); }
            }

            _grabEntryW = BPBridge.GetEntryWorld(_pairedName);   // post-NC entry of the paired penis (feeds the entry-detach gate)
        }

        // AIM diagnostic (b883). The whole penis-mistarget class shows up right here: the tip is projected
        // on the canal as ALONG (depth) and LAT (how far off the canal line it is), and the gate that
        // decides whether the womb reacts at all is `along > 0 && lat < 60% of canal`. A mistarget is
        // exactly LAT going large - the womb then reads zero depth and stops reacting while the penis is
        // visibly inside her. Nothing in a 1.1 build reports this: H-STATE, CANAL-VERIFY and ENGAGE-ON
        // were all stripped for release, so a mistarget report currently arrives with no evidence at all.
        //
        // Throttled to 1s, but a GATE FLIP is logged immediately - a brief mistarget between two ticks
        // would otherwise be invisible, and "it happened once on that pose" is the usual report.
        private float _aimLogAt;
        private int _aimLastState = -1;   // -1 unknown, 0 no end, 1 out of canal, 2 in canal, 3 re-seating
        private void LogAim(float alongM, float latM, bool hasEnd, bool reseating = false)
        {
            if (!AutoBodyReveal.Debug) return;
            if (reseating)
            {
                if (_aimLastState == 3 && Time.unscaledTime < _aimLogAt) return;
                bool firstReseat = _aimLastState != 3;
                _aimLastState = 3; _aimLogAt = Time.unscaledTime + 1f;
                if (firstReseat)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: AIM [CHANGED] CANAL RE-SEATING after pose change"
                        + " — holding the last depth (" + ExternalStrokeMM.ToString("F0") + "mm); the canal frame is still the old pose's,"
                        + " so any projection onto it would be meaningless.");
                return;
            }
            int state = !hasEnd ? 0 : ((alongM > 0f && latM < _canalLen * 0.6f) ? 2 : 1);
            bool flip = state != _aimLastState;
            if (!flip && Time.unscaledTime < _aimLogAt) return;
            _aimLastState = state;
            _aimLogAt = Time.unscaledTime + 1f;
            string verdict = state == 0 ? "NO PENIS END (no depth feed)"
                           : state == 2 ? "IN CANAL"
                           : (alongM <= 0f ? "OUT: behind the entrance" : "OUT: OFF-AXIS -> womb gets NO depth feed");
            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: AIM " + (flip ? "[CHANGED] " : "")
                + verdict + " | along=" + (alongM * 1000f).ToString("F1") + "mm lat=" + (latM * 1000f).ToString("F1")
                + "mm (limit " + (_canalLen * 600f).ToString("F0") + "mm) canal=" + (_canalLen * 1000f).ToString("F0")
                + "mm stroke=" + ExternalStrokeMM.ToString("F0") + "mm | motion='" + MainGameWomb.HMotion
                + "' penetrated=" + MainGameWomb.HPenetrated);
        }

        // CANAL CALIBRATION (b534): the POSED-CANAL probe revealed a CONSTANT ~15mm/5° offset between
        // the TRUE skinned canal and the canal-line bone in KKS — every pose, every loop. Cause: the
        // bone was placed on the mesh canal at BUILD time against the KK donor rig's bone arrangement;
        // the KKS rig arranges the same-named bones slightly differently, so the skinned mesh comes out
        // a systematically SHIFTED shape relative to its bones (KK's wearer rig IS the donor rig ⇒ ~0
        // there). Fix at the single source: once per spawn, with the womb AT REST (not engaged, no
        // stretch), bake the skinned mesh, measure the real canal entrance/axis, and MOVE the canal
        // bone onto it. Everything downstream — teal line, seat anchor, depth math, penis_target2 (a
        // child of this bone; the user's PT2-is-the-target rule stays intact) — corrects together.
        private bool _canalCalibrated;

        private void CalibrateCanalToSkinnedMesh()
        {
            if (_canalCalibrated || MainGameWomb.IsStudio) return;
            if (_smr == null || _canalBone == null || _canalLocal == null || _canalLocal.Length < 2 || _canalLen <= 1e-4f) return;
            if (!_wearerAnimSearched)
            {
                _wearerAnimSearched = true;
                var pr = transform.parent;   // the womb item is instantiated as a CHILD of the wearer
                if (pr != null) _wearerAnim = pr.GetComponentInChildren<Animator>();
                if (_wearerAnim == null)
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: phase-locked calibration needs the wearer's Animator and none was found — NOT calibrating (no fallback).");
            }
            if (_wearerAnim == null) return;
            var animSt = _wearerAnim.GetCurrentAnimatorStateInfo(0);
            float frac = animSt.normalizedTime - Mathf.Floor(animSt.normalizedTime);
            bool crossed = false;
            if (_calPrevFrac >= 0f)
            {
                if (frac >= _calPrevFrac) crossed = (_calPrevFrac < CalPhase && frac >= CalPhase);
                else crossed = (CalPhase >= _calPrevFrac || CalPhase < frac);   // wrapped past 1.0
            }
            _calPrevFrac = frac;
            if (!crossed) return;
            try
            {
                EnsureCanalBands();
                if (_probeMesh == null) _probeMesh = new Mesh();
                // that sits 5.2mm (full-open, measured offline) from the rest-slit centroid at the MOUTH.
                float[] savedW = _ringIdx != null ? new float[_ringIdx.Length] : null;
                if (savedW != null)
                {
                    for (int i = 0; i < _ringIdx.Length; i++)
                    {
                        if (_ringIdx[i] < 0) continue;
                        savedW[i] = _smr.GetBlendShapeWeight(_ringIdx[i]);
                        float w = i == 0 ? EntranceWeight : (i == _ringIdx.Length - 1 ? CervixWeight : RingWeight);
                        _smr.SetBlendShapeWeight(_ringIdx[i], w);
                    }
                }
                // length regardless of the live resting base-stretch (restored right after.
                float savedStretch = 0f;
                if (_stretchIdx >= 0) { savedStretch = _smr.GetBlendShapeWeight(_stretchIdx); _smr.SetBlendShapeWeight(_stretchIdx, 0f); }
                _smr.BakeMesh(_probeMesh);
                if (_stretchIdx >= 0) _smr.SetBlendShapeWeight(_stretchIdx, savedStretch);
                if (savedW != null)
                    for (int i = 0; i < _ringIdx.Length; i++)
                        if (_ringIdx[i] >= 0) _smr.SetBlendShapeWeight(_ringIdx[i], savedW[i]);
                var bv = _probeMesh.vertices;
                Matrix4x4 m = Matrix4x4.TRS(_smr.transform.position, _smr.transform.rotation, Vector3.one);
                // center).
                Vector3 meL, mtL;
                BandCenters(bv, out meL, out mtL);
                // the window is full.
                Vector3 axisL0 = (mtL - meL).normalized;
                if (_bandMid != null && _bandMid.Length > 4)
                {
                    Vector3 uu = Vector3.Cross(axisL0, Mathf.Abs(axisL0.z) < 0.9f ? Vector3.forward : Vector3.right).normalized;
                    Vector3 ww = Vector3.Cross(axisL0, uu);
                    float uMin = 1e9f, uMax = -1e9f, wMin = 1e9f, wMax = -1e9f;
                    for (int k = 0; k < _bandMid.Length; k++)
                    {
                        Vector3 p = bv[_bandMid[k]];
                        float pu = Vector3.Dot(p, uu), pw = Vector3.Dot(p, ww);
                        if (pu < uMin) uMin = pu; if (pu > uMax) uMax = pu;
                        if (pw < wMin) wMin = pw; if (pw > wMax) wMax = pw;
                    }
                    _calSumDia += Mathf.Max(uMax - uMin, wMax - wMin) * 1000f; _calDiaN++;
                }
                _calSumEnt += meL; _calSumTop += mtL; _calN++;
                if (_calN < CalLoops) return;   // need one stance-matched bake per animation loop

                // window complete: apply the idle-phase-free MEANS, exactly once.
                meL = _calSumEnt / _calN; mtL = _calSumTop / _calN;
                Vector3 me = m.MultiplyPoint3x4(meL), mt = m.MultiplyPoint3x4(mtL);
                Vector3 meshAxis = (mt - me).normalized;
                _bakedCanalLen = (mt - me).magnitude;   // b561: the TRUE world canal length, idle-averaged
                // b674: do NOT seed openEff from the calibration's REST diameter — it's the narrow rest
                // width, not the DRIVEN mm-per-girthScale response (~2x larger), so it made girthScale spike
                // ~2x at first contact (canal ballooned) and the driven measure then LERPED it back over
                // ~4-5s = the user's "canal extra big then reduced its size". openEff now comes ONLY from
                // driven measures (carried across spawns via s_carryEff); RefGirth covers the first contact.
                _canalCalibrated = true;
                float formulaRest =
#if KKS
                    _canalRestLen;
#else
                    _canalRestLen * LiquidWobbleMPBPlugin.CfgHWombScale;
#endif
                if (Mathf.Abs(_bakedCanalLen - formulaRest) > 0.002f)
                {
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: canal calibrated to "
                        + (_bakedCanalLen * 1000f).ToString("F1") + "mm rest (formula said "
                        + (formulaRest * 1000f).ToString("F1") + "mm) — re-fitting the current pose on the corrected canal.");
                    MainGameWomb.BumpPose("canal calibrated");
                }
                float posErr = (me - _canalBone.position).magnitude * 1000f;
                float angErr = Vector3.Angle(_canalBone.up, meshAxis);
                // Move the canal-line bone onto the TRUE skinned canal when the rig-arrangement skinning
                // shift put it off (KKS); a donor-rig wearer (KK) reads ~0 and is left untouched.
                float sane = Mathf.Max(0.02f, _canalLen) * 1000f * 2f;
                if (posErr > sane)
                {
                    if (!_canalCalWarned)
                    {
                        _canalCalWarned = true;
                        LiquidWobbleMPBPlugin._logger?.LogError("CloXray: CANAL-CAL error " + posErr.ToString("F0")
                            + "mm exceeds twice the canal length (" + (_canalLen * 1000f).ToString("F0")
                            + "mm) — bone and bake disagree by more than a skinning shift can explain. NOT moving the bone.");
                    }
                    return;
                }
                bool moved = posErr >= 3f || angErr >= 2f;
                if (moved)
                {
                    _canalBone.position = me;
                    _canalBone.rotation = Quaternion.FromToRotation(_canalBone.up, meshAxis) * _canalBone.rotation;
                }
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: CANAL-CAL mode=" + (MainGameWomb.CurrentlyRebound ? "REBIND" : "MIRROR")
                    + " err=" + posErr.ToString("F1") + "mm/" + angErr.ToString("F1") + "deg -> "
                    + (moved ? "BONE MOVED onto the skinned canal" : "bone already matched")
                    + " | bakedCanal=" + (_bakedCanalLen * 1000f).ToString("F1") + "mm openEff=" + _openEfficiency.ToString("F1") + "mm/unit"
                    + " (phase-locked @" + CalPhase.ToString("F2") + " x" + CalLoops + " loops)");
                PredictParity();   // b627: one-shot analytic prediction, logged beside the loop's result
                // b541: PT2 onto the calibrated axis. penis_target2 was authored at the old penis_target's
                // rest position (a hair FRONT of the canal top); PT2 is the aim, so a lateral authorship
                // offset drags the penis toward that wall at depth. Runtime BONE calibration (allowed):
                // keep its depth along the axis, zero its lateral offset.
                if (_ptBone == null) _ptBone = FindPenisTargetBone();
                if (_ptBone != null)
                {
                    Vector3 axisW = _canalBone.up;
                    Vector3 rel = _ptBone.position - _canalBone.position;
                    _ptBone.position = _canalBone.position + axisW * Vector3.Dot(rel, axisW);
                }
                else
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no canal-frame aim bone found at calibration — PT2 axis-snap SKIPPED; the aim may sit off the canal. Fix the cause (no fallback).");
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: canal calibration failed: " + e.Message);
                _canalCalibrated = false;
                _calN = 0; _calSumEnt = Vector3.zero; _calSumTop = Vector3.zero; _calSumDia = 0f; _calDiaN = 0; _calPrevFrac = -1f;   // restart the window
            }
        }
        // b624 PHASE-LOCKED calibration (user's idea: "catch the loop, measure at the same frame each
        // time"). The idle animation is LOOPED, so sampling at one fixed normalized phase sees the SAME
        // stance every time — stance shifts stop being noise by construction (b618's time-window average
        // could not fix this: stance switching is state noise, not zero-mean sway; each spawn's window
        // caught a different stance mix and the bone landed ±5mm differently). CalPhase is one global
        // constant so every spawn, BOTH modes, target and verify are all stance-matched to each other.
        private const float CalPhase = 0.25f;   // fixed normalized phase, identical everywhere
        private const int CalLoops = 2;         // one bake per animation loop, averaged
        private Animator _wearerAnim; private bool _wearerAnimSearched;
        private float _calPrevFrac = -1f;
        private int _calN, _calDiaN;
        private Vector3 _calSumEnt, _calSumTop;
        private float _calSumDia;

        private GameObject _modeDot; private Color _modeDotCol;
        private void UpdateModeDot()
        {
            if (MainGameWomb.IsStudio) return;
            bool show = false; Vector3 pos = Vector3.zero; Color col = Color.cyan;
            if (!MainGameWomb.CurrentlyRebound && _canalLen > 1e-4f)
            { show = true; pos = _canalEntrance; col = Color.cyan; }
            else if (MainGameWomb.CurrentlyRebound && MainGameWomb.AutoTargetKnown && _canalBone != null && _canalBone.parent != null)
            { show = true; pos = _canalBone.parent.TransformPoint(MainGameWomb.AutoTargetLocal); col = new Color(1f, 0.55f, 0f); }
            if (!show) { if (_modeDot != null) _modeDot.SetActive(false); return; }
            if (_modeDot == null)
            {
                _modeDot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _modeDot.name = "CloXrayModeDot";
                var c = _modeDot.GetComponent<Collider>(); if (c != null) Destroy(c);
                _modeDot.transform.localScale = Vector3.one * 0.012f;
                var mr = _modeDot.GetComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                var m = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                m.SetColor("_Color", Color.white);
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                m.SetInt("_ZWrite", 1);
                m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                m.renderQueue = 5000;
                mr.sharedMaterial = m;
                _modeDot.hideFlags = HideFlags.HideAndDontSave;
                if (_smr != null) _modeDot.layer = _smr.gameObject.layer;
                _modeDotCol = new Color(0, 0, 0, 0);   // force the first colour stamp
            }
            if (col != _modeDotCol)
            {   // Internal-Colored multiplies VERTEX colour; a primitive has none — stamp it into the mesh
                _modeDotCol = col;
                var mf = _modeDot.GetComponent<MeshFilter>();
                var mesh2 = mf.mesh; mesh2.hideFlags = HideFlags.HideAndDontSave;
                var cols = new Color[mesh2.vertexCount];
                for (int i = 0; i < cols.Length; i++) cols[i] = col;
                mesh2.colors = cols;
            }
            if (!_modeDot.activeSelf) _modeDot.SetActive(true);
            _modeDot.transform.position = pos;
        }
        private void OnDestroy() { if (_modeDot != null) Destroy(_modeDot); }

        private bool _parityPredicted;
        private void PredictParity()
        {
            if (_parityPredicted || !MainGameWomb.CurrentlyRebound) return;
            var bpO = MainGameWomb.RebindOrigBindposes;
            Transform pivot = MainGameWomb.RebindPivotBone;
            if (bpO == null || pivot == null || _smr == null || _bandEnt == null || _bandEnt.Length < 4) return;
            _parityPredicted = true;
            try
            {
                var mesh = _smr.sharedMesh;
                var verts = mesh.vertices;
                var bw = mesh.boneWeights;
                var bones = _smr.bones;   // in REBIND these ARE her transforms
                if (bw == null || bw.Length != verts.Length || bones == null) return;
                float s = MainGameWomb.RebindS;
                Vector3 pivotW = pivot.position;
                Vector3 seatW = Mathf.Abs(MainGameWomb.RebindSeatBackMM) > 0.01f ? -pivot.forward * (MainGameWomb.RebindSeatBackMM * 0.001f) : Vector3.zero;
                int n = Mathf.Min(bones.Length, bpO.Length);
                var mir = new Matrix4x4[n];
                var reb = new Matrix4x4[n];
                for (int j = 0; j < n; j++)
                {
                    var b = bones[j];
                    if (b == null) { mir[j] = Matrix4x4.identity; reb[j] = Matrix4x4.identity; continue; }
                    Vector3 ls = b.lossyScale;
                    // b632 FROZEN-HELPER EXCEPTION: SyncNow's follow loop gives cf_s_waist01/02 and
                    // cf_s_leg_L/R HER natural scale (localScale = c.localScale — never ×s), only their
                    // POSITIONS are pivot-scaled. This is the measured mirror law's mechanism (girth ×1)
                    // and the first predictor run missed exactly its share: y off by ~7mm with z correct.
                    string hb = MainGameWomb.HerNameFor(b.name);   // womb bone -> her equivalent role
                    bool frozenHelper = hb == "cf_s_waist01" || hb == "cf_s_waist02"
                                     || hb == "cf_s_leg_L" || hb == "cf_s_leg_R";
                    Vector3 mls = frozenHelper ? ls : ls * s;
                    mir[j] = Matrix4x4.TRS(pivotW + s * (b.position - pivotW) + seatW, b.rotation, mls) * bpO[j];
                    reb[j] = b.localToWorldMatrix * bpO[j] * MainGameWomb.RebindM0;
                }
                Vector3 sumM = Vector3.zero, sumR = Vector3.zero; int cnt = 0;
                for (int k = 0; k < _bandEnt.Length; k++)
                {
                    int vi = _bandEnt[k];
                    if (vi >= verts.Length) continue;
                    var w = bw[vi]; Vector3 v = verts[vi];
                    Vector3 pm = w.weight0 * mir[w.boneIndex0].MultiplyPoint3x4(v);
                    Vector3 pr = w.weight0 * reb[w.boneIndex0].MultiplyPoint3x4(v);
                    if (w.weight1 > 0f) { pm += w.weight1 * mir[w.boneIndex1].MultiplyPoint3x4(v); pr += w.weight1 * reb[w.boneIndex1].MultiplyPoint3x4(v); }
                    if (w.weight2 > 0f) { pm += w.weight2 * mir[w.boneIndex2].MultiplyPoint3x4(v); pr += w.weight2 * reb[w.boneIndex2].MultiplyPoint3x4(v); }
                    if (w.weight3 > 0f) { pm += w.weight3 * mir[w.boneIndex3].MultiplyPoint3x4(v); pr += w.weight3 * reb[w.boneIndex3].MultiplyPoint3x4(v); }
                    sumM += pm; sumR += pr; cnt++;
                }
                if (cnt == 0) return;
                Vector3 dW = (sumM - sumR) / cnt;                    // world: mirror − rebind(nudge-free)
                Vector3 dHer = Quaternion.Inverse(pivot.rotation) * dW * 1000f;   // her-frame world mm (nudge convention)
                // b665: publish for AutoPlacePredicted (one-shot placement, no loop). x never commanded.
                MainGameWomb.PredictedNudgeMM = new Vector3(0f, dHer.y, dHer.z);
                MainGameWomb.PredictedValid = true;
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: PREDICT parity nudge=(" + dHer.x.ToString("F1") + ", " + dHer.y.ToString("F1") + ", " + dHer.z.ToString("F1")
                    + ")mm (analytic dual-chain, same-frame) — publishing for one-shot placement | bandVerts=" + cnt);
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: parity prediction failed: " + e.Message); }
        }

        // mid-ring, 1s cadence.
        private float _effNext;
        private void MeasureOpenCanal()
        {
            if (_canalBone == null || MainGameWomb.IsStudio) return;
            if (Time.unscaledTime < _effNext || _smr == null || _canalLocal == null || _canalLocal.Length < 2 || _canalLen <= 1e-4f) return;
            _effNext = Time.unscaledTime + 1f;
            try
            {
                EnsureCanalBands();
                if (_probeMesh == null) _probeMesh = new Mesh();
                _smr.BakeMesh(_probeMesh);
                var bv = _probeMesh.vertices;
                Vector3 meL, mtL, miL;
                BandCenters(bv, out meL, out mtL, out miL);
                // over 5 samples (=5s).
                if (_canalBone != null && _canalBone.parent != null)
                {
                    Matrix4x4 m2 = Matrix4x4.TRS(_smr.transform.position, _smr.transform.rotation, Vector3.one);
                    Transform herK2 = _canalBone.parent;
                    _shSumE += herK2.InverseTransformPoint(m2.MultiplyPoint3x4(meL));
                    _shSumM += herK2.InverseTransformPoint(m2.MultiplyPoint3x4(miL));
                    _shSumT += herK2.InverseTransformPoint(m2.MultiplyPoint3x4(mtL));
                    _shN++;
                    if (_shN >= 5)
                    {
                        Vector3 e5 = _shSumE / _shN * 1000f, m5 = _shSumM / _shN * 1000f, t5 = _shSumT / _shN * 1000f;
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: SHAPE mode=" + (MainGameWomb.CurrentlyRebound ? "REBIND" : "MIRROR")
                            + " xEnt=" + e5.x.ToString("F1") + " xMid=" + m5.x.ToString("F1") + " xTop=" + t5.x.ToString("F1")
                            + " (her mm; equal = pure translation, drifting = skew)"
                            + " | ent=(" + e5.x.ToString("F1") + ", " + e5.y.ToString("F1") + ", " + e5.z.ToString("F1") + ")"
                            + " mid=(" + m5.x.ToString("F1") + ", " + m5.y.ToString("F1") + ", " + m5.z.ToString("F1") + ")"
                            + " top=(" + t5.x.ToString("F1") + ", " + t5.y.ToString("F1") + ", " + t5.z.ToString("F1") + ")"
                            + " | nudge=" + MainGameWomb.RebindNudgeMM.ToString("F1") + "mm");
                        _shSumE = Vector3.zero; _shSumM = Vector3.zero; _shSumT = Vector3.zero; _shN = 0;
                    }
                }
                if (_bandMid != null && _bandMid.Length > 4 && _lastGirthScale > 0.1f)
                {
                    Vector3 axisL = (mtL - meL).normalized;
                    Vector3 u = Vector3.Cross(axisL, Mathf.Abs(axisL.z) < 0.9f ? Vector3.forward : Vector3.right).normalized;
                    Vector3 w = Vector3.Cross(axisL, u);
                    float uMin = 1e9f, uMax = -1e9f, wMin = 1e9f, wMax = -1e9f;
                    for (int k = 0; k < _bandMid.Length; k++)
                    {
                        Vector3 p = bv[_bandMid[k]];
                        float pu = Vector3.Dot(p, u), pw = Vector3.Dot(p, w);
                        if (pu < uMin) uMin = pu; if (pu > uMax) uMax = pu;
                        if (pw < wMin) wMin = pw; if (pw > wMax) wMax = pw;
                    }
                    float canalDiaMM = Mathf.Max(uMax - uMin, wMax - wMin) * 1000f;
                    float eff = canalDiaMM / _lastGirthScale;   // mm diameter per girthScale unit (mesh property)
                    // b634: SNAP to the first live measurement of this spawn — the carried/seeded value
                    // can be stale (other mode, other bake, other girl) and the 0.5 lerp took seconds to
                    // converge ("channel still took time to adjust to size"). Later samples smooth noise.
                    // b676: openEff is fine-tuned here but SEEDED from the scale law in the drive (it's a
                    // mesh constant × the womb's world-scale), so girthScale is right from frame 1. This
                    // measure must REJECT the ring-ramp TRANSIENT: right after contact the smoothed ring is
                    // still opening, so canalDiaMM (hence eff) reads far too low (~16 vs the settled ~43) —
                    // b674/b675 snapped/lerped onto exactly that garbage and the canal ballooned crawling
                    // back up. Accept eff only within a plausible band of the current (seeded) openEff: the
                    // settled value sits in-band, the transient lows fall out. Penetrated-only; slow lerp.
                    if (MainGameWomb.HPenetrated && _openEfficiency > 1e-3f
                        && eff > _openEfficiency * 0.6f && eff < _openEfficiency * 1.7f)
                    {
                        _openEfficiency = Mathf.Lerp(_openEfficiency, eff, 0.35f);
                        s_carryEff = _openEfficiency;   // b632 carry (in-band, driven values only)
                    }
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: open-canal measure failed: " + e.Message); }
        }

        private int[] _bandEnt, _bandTop, _bandMid;
        private Mesh _probeMesh;
        private Vector3 _shSumE, _shSumM, _shSumT; private int _shN;   // b620 SHAPE probe accumulators

        private static int[] BandVerts(Vector3[] v, Vector3 c, Vector3 axis)
        {
            var sel = new System.Collections.Generic.List<int>();
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 o = v[i] - c;
                float a = Vector3.Dot(o, axis);
                if (a < -0.004f || a > 0.004f) continue;
                if ((o - a * axis).sqrMagnitude < 0.012f * 0.012f) sel.Add(i);
            }
            return sel.ToArray();
        }

        private void EnsureCanalBands()
        {
            if (_bandEnt != null) return;
            var v = _smr.sharedMesh.vertices;
            Vector3 axis = (_canalLocal[_canalLocal.Length - 1] - _canalLocal[0]).normalized;
            _bandEnt = BandVerts(v, _canalLocal[0], axis);
            _bandTop = BandVerts(v, _canalLocal[_canalLocal.Length - 1], axis);
            _bandMid = BandVerts(v, (_canalLocal[0] + _canalLocal[_canalLocal.Length - 1]) * 0.5f, axis);
        }

        // Ring centers from a BAKED (posed) mesh, in baked/renderer-local space.
        private void BandCenters(Vector3[] baked, out Vector3 ent, out Vector3 top)
        {
            Vector3 mid;
            BandCenters(baked, out ent, out top, out mid);
        }

        private void BandCenters(Vector3[] baked, out Vector3 ent, out Vector3 top, out Vector3 mid)
        {
            Vector3 me0 = Vector3.zero, mt0 = Vector3.zero;
            for (int k = 0; k < _bandEnt.Length; k++) me0 += baked[_bandEnt[k]];
            for (int k = 0; k < _bandTop.Length; k++) mt0 += baked[_bandTop[k]];
            me0 /= _bandEnt.Length; mt0 /= _bandTop.Length;
            Vector3 axis = (mt0 - me0).normalized;
            ent = ExtentMid(baked, _bandEnt, axis);
            top = ExtentMid(baked, _bandTop, axis);
            mid = _bandMid != null && _bandMid.Length > 0 ? ExtentMid(baked, _bandMid, axis) : (ent + top) * 0.5f;
        }

        private static Vector3 ExtentMid(Vector3[] baked, int[] band, Vector3 axis)
        {
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.z) < 0.9f ? Vector3.forward : Vector3.right).normalized;
            Vector3 w = Vector3.Cross(axis, u);
            float uMin = float.MaxValue, uMax = float.MinValue, wMin = float.MaxValue, wMax = float.MinValue, aSum = 0f;
            for (int k = 0; k < band.Length; k++)
            {
                Vector3 p = baked[band[k]];
                float pu = Vector3.Dot(p, u), pw = Vector3.Dot(p, w);
                if (pu < uMin) uMin = pu; if (pu > uMax) uMax = pu;
                if (pw < wMin) wMin = pw; if (pw > wMax) wMax = pw;
                aSum += Vector3.Dot(p, axis);
            }
            return u * ((uMin + uMax) * 0.5f) + w * ((wMin + wMax) * 0.5f) + axis * (aSum / band.Length);
        }

        // Re-project the cached LOCAL (rest) canal centres through the womb's CURRENT (post-IK) transform. Cheap, runs
        // every frame so the canal tracks the womb without a per-frame bake. SHAPE is from the rest bake (stable, no
        // penetration-deformation feedback); POSITION is live. Keeps _canalEntrance/_canalAxis/_canalLen + the markers
        // in sync with where the womb actually renders.
        // A rejected canal measurement used to be silent, and everything downstream (pairing, depth,
        // the collider path) is gated on a valid canal - so the womb simply stopped reacting with no
        // explanation. Reported once per womb.
        private bool _offCanalWarned;      // the paired penis runs beside the canal, not through it
        private bool _canalMarkerWarned;   // the canal marker disagreed with the mesh and was dropped
        private bool _canalRejectWarned;
        private void ReportCanalRejected(float measured, float scale)
        {
            if (_canalRejectWarned) return;
            _canalRejectWarned = true;
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb '" + name + "' canal length " + (measured * 1000f).ToString("F1")
                + "mm is outside the plausible band at scale " + scale.ToString("F2") + "x (expect roughly "
                + (77f * scale).ToString("F0") + "mm) - the womb cannot pair with a penis or react until this measures correctly. "
                + "Respawn the womb after scaling, or check that its mesh/canal bone is intact.");
        }

        private void RefreshCanalWorld()
        {
            // ONE source of truth: the mesh's own `clo_canal_entry` marker bone.
            if (_canalLocal != null && _canalLocal.Length >= 2 && _smr != null)
            {
                if (_canalBandW == null || _canalBandW.Length != _canalLocal.Length) _canalBandW = new Vector3[_canalLocal.Length];
                for (int i = 0; i < _canalLocal.Length; i++) _canalBandW[i] = _smr.transform.TransformPoint(_canalLocal[i]);
            }
            if (_canalBone == null)
            {
                if (!_canalMarkerWarned)
                {
                    _canalMarkerWarned = true;
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb '" + name + "' has no clo_canal_entry marker bone — "
                        + "canal entrance/axis/length are UNKNOWN and nothing is driven. Re-add the womb from the current mod.");
                }
                return;
            }
            if (_canalLocalLen < 0.02f || _canalLocalLen > 0.5f)
            {
                ReportCanalRejected(_canalLocalLen, 1f);
                return;
            }
            float syB = Mathf.Abs(_canalBone.lossyScale.y);
            float lenB = _canalLocalLen * syB;
            if (lenB <= 1e-4f) { ReportCanalRejected(lenB, syB); return; }
            _canalEntrance = _canalBone.position;
            _canalAxis = _canalBone.up;
            _canalLen = lenB;
            if (_canalBandW == null || _canalBandW.Length < 2) _canalBandW = new Vector3[2];
            _canalBandW[0] = _canalEntrance;                                        // endpoints from the BONE
            _canalBandW[_canalBandW.Length - 1] = _canalEntrance + _canalAxis * _canalLen;

            // H BASE CANAL STRETCH: a resting womb_displace elongates the whole womb.
            _canalRestLen = _canalLen;
            if (!MainGameWomb.IsStudio && _canalLen > 1e-4f)
            {
                // The womb SCALE (CfgHWombScale) is applied as a POSITION scale-about-the-entrance in the
                // bone mirror.
                float restBase;
                if (_bakedCanalLen > 1e-4f) restBase = _bakedCanalLen;
                else
#if KKS
                    restBase = _canalRestLen;
#else
                    restBase = _canalRestLen * LiquidWobbleMPBPlugin.CfgHWombScale;
#endif
                _baseStretchPctEff = LiquidWobbleMPBPlugin.CfgHBaseStretchPct
                    + MainGameWomb.CanalExtendDownMM * 0.1f / Mathf.Max(restBase, 1e-4f);
                float f = _baseStretchPctEff * 0.01f;
                _canalLen = restBase * (1f + f);
            }
        }
        // one of the SMR's bones).
        internal Transform CanalEntryBone { get { return _canalBone != null ? _canalBone : FindCanalBone(); } }

        private Transform FindCanalBone()
        {
            if (_smr == null || _smr.bones == null) return null;
            foreach (var b in _smr.bones)
            {
                if (b == null) continue;
                foreach (var t in b.GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == "clo_canal_entry") return t;
            }
            return null;
        }

        // Is this womb seated ON a character's vagina? Caches the nearest cf_J_Vagina_root (outside its own
        // subtree) to the canal entrance, on a 2s timer, and pairs when it's within the "Womb-in-vagina range".
        private static int _activeCount;
        public static bool AnyActive { get { return _activeCount > 0; } }
        // EffectiveActive = the master F1 "Enabled" toggle AND a womb present.
        public static bool EffectiveActive { get { return LiquidWobbleMPBPlugin.CfgEnabled && _activeCount > 0; } }

        // Pairing-recheck signal.
        private static int _repairVersion;
        public static void RequestRepair() { _repairVersion++; }

        // SHARED cf_J_Vagina_root cache. The full-scene FindObjectsOfType<Transform> scan is heavy, and the
        // root LIST only changes when a CHARACTER spawns/despawns.
        private const float VaginaRescanFallback = 30f;
        private static Transform[] _vaginaRootCache;
        private static float _vaginaRootStamp = -100f;
        public static void InvalidateVaginaRoots() { _vaginaRootStamp = -1000f; }   // force a rescan next access (a character spawned/despawned)
        private static Transform[] VaginaRoots()
        {
            if (_vaginaRootCache != null && Time.unscaledTime - _vaginaRootStamp < VaginaRescanFallback) return _vaginaRootCache;
            _vaginaRootStamp = Time.unscaledTime;
            var all = UnityEngine.Object.FindObjectsOfType<Transform>();
            int n = 0;
            for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].name == "cf_J_Vagina_root") n++;
            var roots = new Transform[n];
            for (int i = 0, k = 0; i < all.Length; i++) if (all[i] != null && all[i].name == "cf_J_Vagina_root") roots[k++] = all[i];
            _vaginaRootCache = roots;
            return roots;
        }
        private void EnsureVaginaPairing()
        {
            _vaginaTimer -= Time.deltaTime;
            if (_vaginaTimer > 0f) return;
            _vaginaTimer = 2f;
            Vector3 wE = EntranceWorld();
            float best = float.MaxValue;
            var roots = VaginaRoots();
            for (int i = 0; i < roots.Length; i++)
            {
                var t = roots[i];
                if (t == null || t.IsChildOf(transform)) continue;
                float d = (t.position - wE).sqrMagnitude;
                if (d < best) best = d;
            }
            _vaginaDist = (best < float.MaxValue) ? Mathf.Sqrt(best) : -1f;
            _vaginaPaired = _vaginaDist >= 0f && _vaginaDist <= LiquidWobbleMPBPlugin.CfgAutoBodyRevealRange;
        }

        // Project a world point onto this womb's TUBE CENTERLINE and return the foot at the same depth.
        public bool SnapToTubeCenter(Vector3 worldPoint, out Vector3 foot)
        {
            foot = worldPoint;
            EnsureCanal();
            if (_canalLen <= 1e-4f) return false;
            Vector3 rel = worldPoint - _canalEntrance;
            float along = Vector3.Dot(rel, _canalAxis);
            foot = _canalEntrance + _canalAxis * along;   // on the centerline, same depth
            return true;
        }

        // The BP penis TIP's position in THIS canal: lateral offset from the axis (m) and along-axis
        // distance (m, 0=entrance).
        private bool PenisTipCanal(BPBridge.Reading bp, out float lat, out float along)
        {
            lat = -1f; along = 0f; _bpLateral = -1f;
            if (!bp.hasPose) return false;
            EnsureCanal();
            if (_canalLen <= 1e-4f) return false;
            Vector3 rel = bp.tipPos - _canalEntrance;
            along = Vector3.Dot(rel, _canalAxis);
            lat = (rel - along * _canalAxis).magnitude;
            _bpLateral = lat;
            return true;
        }

        // Deepest point of the penis SHAFT (the whole cm_j_dan bone chain) that lies inside this womb's canal/bulb.
        // Replaces the single-tip test, which broke once BP bends the penis fully: the geometric TIP can curl back
        // OUT (below/beside the womb) while the shaft still threads it. We scan every shaft bone and keep the one
        // with the greatest along-canal depth whose lateral offset is within a DEPTH-SCALED womb radius — narrow
        // (~girth) at the entrance, widening to the bulb half-width deep in. The bulb radius comes from the REST
        // mesh extents mapped to world (oriented with the womb, so a tilted womb doesn't inflate it the way the
        // world AABB does). Rejects a penis lying BESIDE the womb (every bone beyond the radius) and a withdrawn
        // one (deepest contained bone shallow). Sets _bpLateral for the debug line (closest approach when missed).
        private readonly Vector3[] _aimBuf = new Vector3[1];   // reusable 1-point buffer for the aimed-tip containment test

        // Penis WORLD girth radius at a given distance BACK FROM THE TIP (world metres), from BP's
        // per-collider girth profile.
        private float GirthAtDistFromTip(float distFromTip)
        {
            var gp = _bp.girthPos; var gr = _bp.girthRad;
            if (gp == null || gr == null || gp.Length == 0) return -1f;
            float belowD = float.NegativeInfinity, belowR = -1f, aboveD = float.PositiveInfinity, aboveR = -1f;
            float nearR = -1f, nearD = float.PositiveInfinity;
            for (int k = 0; k < gp.Length; k++)
            {
                float d = (gp[k] - _bp.tipPos).magnitude;   // this collider's distance from the penis tip (along the shaft)
                float r = gr[k];
                float diff = Mathf.Abs(d - distFromTip);
                if (diff < nearD) { nearD = diff; nearR = r; }
                if (d <= distFromTip && d > belowD) { belowD = d; belowR = r; }
                if (d >= distFromTip && d < aboveD) { aboveD = d; aboveR = r; }
            }
            if (belowR >= 0f && aboveR >= 0f && aboveD > belowD + 1e-6f)
                return Mathf.Lerp(belowR, aboveR, (distFromTip - belowD) / (aboveD - belowD));
            return nearR;   // outside the profile span -> clamp to the nearest end
        }

        // The womb's half-width (bulb radius) in world units, from the REST mesh extents (oriented with the
        // womb, so it's tilt/flip-independent).
        private float _bulbRCache; private int _bulbRFrame = -1;
        private float WombBulbRadius()
        {
            if (_bulbRFrame == Time.frameCount) return _bulbRCache;
            Vector3 le = _smr.sharedMesh != null ? _smr.sharedMesh.bounds.extents : _smr.bounds.extents;
            Vector3 ls = _smr.transform.lossyScale;
            _bulbRCache = Mathf.Max(Mathf.Abs(le.x * ls.x), Mathf.Abs(le.z * ls.z));
            _bulbRFrame = Time.frameCount;
            return _bulbRCache;
        }

        private bool DeepestShaftInCanal(Vector3[] pts, int count, float w, out float along, out float lat)
        {
            along = 0f; lat = -1f;
            if (pts == null || count <= 0) return false;
            EnsureCanal();
            if (_canalLen <= 1e-4f) return false;
            float bulbR = WombBulbRadius();   // womb half-width in world units
            if (bulbR < w) bulbR = w;
            bulbR *= _sens;                   // per-womb penetration sensitivity (1.0 = default; <1 tighter / >1 looser)
            bool any = false; float bestAlong = float.NegativeInfinity, bestLat = -1f, minLat = float.PositiveInfinity;
            for (int k = 0; k < count; k++)
            {
                Vector3 rel = pts[k] - _canalEntrance;
                float a = Vector3.Dot(rel, _canalAxis);
                float l = (rel - a * _canalAxis).magnitude;
                if (l < minLat) minLat = l;
                if (a < -w) continue;                                                  // behind the entrance
                // FLAT womb half-width (same as the collider path, build 367) — a depth-scaled gate is too strict at
                // shallow depth for a womb the user placed OFF the penis path (the shaft then reads off-axis at every
                // depth). "Inside the womb's half-width" is the real test; bulbR rotates with the womb (orientation-OK).
                if (l > bulbR) continue;                                               // beside the womb, not threading it
                if (a > bestAlong) { bestAlong = a; bestLat = l; any = true; }
            }
            if (!any) { _bpClosest = (minLat < float.PositiveInfinity) ? minLat : -1f; return false; }
            along = bestAlong; lat = bestLat;
            return true;
        }

        private float _routeBReportAt = -1f; private bool _routeBReported;
        private string _lastCanalCam = "-"; private bool _lastCanalCamVisible;   // b605 camera-dependence probe
        private float _orbitNext;   // b608 camera-orbit probe throttle
        // b612 IDLE-AVERAGED placement: her idle loop flexes the waist/leg bones, deforming the skinned
        // tube ±0.2-0.5mm relative to her pelvis, so any single-frame sample carries idle-phase noise.
        // Accumulate entInHer EVERY frame and emit mean ± spread per 5s window (several idle cycles) —
        // the mean is the phase-free placement, the spread quantifies the idle wobble itself.
        private Vector3 _avgSum, _avgMin, _avgMax, _avgAxisSum; private int _avgN; private float _avgEnd = -1f;
        // jump, which may have contaminated earlier readings.
        private static bool s_gameFocused = true; private static int s_focusEvents;
        internal static int FocusEventCount { get { return s_focusEvents; } }
        private int _winFocusEv = -1;
        private void OnApplicationFocus(bool focused)
        {
            s_gameFocused = focused; s_focusEvents++;
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: game " + (focused ? "FOCUSED" : "UNFOCUSED — Unity pauses; any measurement window spanning this is suspect") + ".");
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;     // master toggle OFF -> freeze: stop driving ring/stretch/mound weights (they stay latched)

            UpdateModeDot();   // b628: cyan = MIRROR entrance, orange = mirror target under REBIND

            // canal axis / her forward / lateral.
            if (LiquidWobbleMPBPlugin.CfgDebugLog && HasPenisRef && MainGameWomb.HPenetrated
                && _canalLen > 1e-4f && Time.unscaledTime >= _anchorBpLogNext)
            {
                _anchorBpLogNext = Time.unscaledTime + 3f;
                Vector3 dW = _canalEntrance - ExternalPenisRef;
                float along = Vector3.Dot(dW, _canalAxis) * 1000f;
                Transform kok = _canalBone != null ? _canalBone.parent : null;
                Vector3 dL = kok != null ? (Quaternion.Inverse(kok.rotation) * dW) * 1000f : dW * 1000f;
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: ANCHOR-VS-BP(rebind) entrance-BPref mm: kokan-frame=("
                    + dL.x.ToString("F1") + ", " + dL.y.ToString("F1") + ", " + dL.z.ToString("F1")
                    + ") along-canal=" + along.ToString("F1") + " |d|=" + (dW.magnitude * 1000f).ToString("F1"));
            }

            // Decides "sheared object LOOKS like it leans" vs "the womb genuinely moves with the camera".
            if (!MainGameWomb.IsStudio && _canalLen > 1e-4f && _canalBone != null && _canalBone.parent != null)
            {
                Transform herK = _canalBone.parent;
                Vector3 eh = herK.InverseTransformPoint(_canalEntrance) * 1000f;
                if (_avgN == 0) { _avgMin = eh; _avgMax = eh; _avgSum = eh; _avgAxisSum = Vector3.zero; _winFocusEv = s_focusEvents; }
                _avgSum += (_avgN == 0) ? Vector3.zero : eh;
                _avgMin = Vector3.Min(_avgMin, eh);
                _avgMax = Vector3.Max(_avgMax, eh);
                _avgAxisSum += herK.InverseTransformDirection(_canalAxis);
                _avgN++;
                if (_avgEnd < 0f) _avgEnd = Time.unscaledTime + 5f;
                else if (Time.unscaledTime >= _avgEnd)
                {
                    Vector3 mean = _avgSum / _avgN;
                    Vector3 ls = herK.lossyScale;
                    Vector3 meanW = Vector3.Scale(mean, ls);
                    Vector3 spread = _avgMax - _avgMin;
                    Vector3 axisMean = (_avgAxisSum / _avgN).normalized;
                    bool focusLost = _winFocusEv != s_focusEvents;
                    bool stable = !focusLost && spread.x <= 2f && spread.y <= 2f && spread.z <= 2f;
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: MEASURE mode=" + (MainGameWomb.CurrentlyRebound ? "REBIND" : "MIRROR")
                        + " char='" + herK.root.name + "' kok=" + ls.y.ToString("F3")
                        + " motion='" + MainGameWomb.HMotion + "' pen=" + MainGameWomb.HPenetrated
                        + " n=" + _avgN
                        + " | meanLocal=(" + mean.x.ToString("F2") + ", " + mean.y.ToString("F2") + ", " + mean.z.ToString("F2") + ")mm"
                        + " meanWorld=(" + meanW.x.ToString("F2") + ", " + meanW.y.ToString("F2") + ", " + meanW.z.ToString("F2") + ")mm"
                        + " axis=(" + axisMean.x.ToString("F3") + ", " + axisMean.y.ToString("F3") + ", " + axisMean.z.ToString("F3") + ")"
                        + " | spread=(" + spread.x.ToString("F2") + ", " + spread.y.ToString("F2") + ", " + spread.z.ToString("F2") + ")mm "
                        + (stable ? "STABLE" : (focusLost ? "FOCUS-LOST-DISCARD" : "UNSTABLE-DISCARD")));
                    _avgN = 0; _avgEnd = Time.unscaledTime + 5f;
                }
            }

            if (!MainGameWomb.IsStudio && _canalLen > 1e-4f && Time.unscaledTime >= _orbitNext
                && _canalBone != null && _canalBone.parent != null)
            {
                _orbitNext = Time.unscaledTime + 0.5f;
                Transform par = _canalBone.parent;
                Camera mc = Camera.main;
                Vector3 entInHer = par.InverseTransformPoint(_canalEntrance) * 1000f;      // mm, pelvis frame
                Vector3 axisInHer = par.InverseTransformDirection(_canalAxis);             // unit, pelvis frame
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: ORBIT mode=" + (MainGameWomb.CurrentlyRebound ? "REBIND" : "MIRROR")
                    + " camPos=" + (mc != null ? mc.transform.position.ToString("F2") : "-")
                    + " camFwd=" + (mc != null ? mc.transform.forward.ToString("F2") : "-")
                    + " | entInHer=" + entInHer.ToString("F1") + "mm axisInHer=" + axisInHer.ToString("F3")
                    + " | latchCam='" + _lastCanalCam + "'");
            }

            if (!_routeBReported && !MainGameWomb.IsStudio && _canalLen > 1e-4f)
            {
                if (_routeBReportAt < 0f) _routeBReportAt = Time.unscaledTime + 2f;
                else if (Time.unscaledTime >= _routeBReportAt)
                {
                    _routeBReported = true;
                    // async and a fresh pair may not have latched yet).
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: SIZE-REPORT her fKok=" + BPInnerTargetPin.HerScale().ToString("F4")
                        + " fStat=" + BPInnerTargetPin.HerStature().ToString("F4")
                        + " | male natural=" + (BPInnerTargetPin.NaturalDanLen * 1000f).ToString("F0") + "mm (last-known)");
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: CANAL-REPORT mode=" + (MainGameWomb.CurrentlyRebound ? "REBIND" : "MIRROR")
                        + " canalLen=" + (_canalLen * 1000f).ToString("F1")
                        + "mm bakedCanal=" + (_bakedCanalLen * 1000f).ToString("F1")
                        + "mm restLocalLen=" + (_canalLocalLen * 1000f).ToString("F1")
                        + "mm boneLossyY=" + (_canalBone != null ? _canalBone.lossyScale.y.ToString("F3") : "-")
                        + " cfgWombScale=" + LiquidWobbleMPBPlugin.CfgHWombScale.ToString("F3")
                        + " (1+f)=" + (1f + _baseStretchPctEff * 0.01f).ToString("F3")
                        + " | entrance=" + _canalEntrance.ToString("F3") + " axis=" + _canalAxis.ToString("F2")
                        + " smrPos=" + (_smr != null ? _smr.transform.position.ToString("F3") : "-")
                        + " smrLossy=" + (_smr != null ? _smr.transform.lossyScale.ToString("F2") : "-")
                        // GROUND TRUTH physical size: the rendered world AABB in metres.
                        + " || WORLDSIZE=" + (_smr != null ? _smr.bounds.size.ToString("F3") : "-")
                        + " kokanLossyY=" + (_canalBone != null && _canalBone.parent != null ? _canalBone.parent.lossyScale.y.ToString("F3") : "-")
                        + (_canalBone != null && _canalBone.parent != null
                            ? " || kokanPos=" + _canalBone.parent.position.ToString("F3")
                              + " rLen=" + ((_canalEntrance - _canalBone.parent.position).magnitude * 1000f).ToString("F1")
                              + "mm rAlong=" + (Vector3.Dot(_canalEntrance - _canalBone.parent.position, _canalAxis) * 1000f).ToString("F1")
                              + "mm rPerp=" + (((_canalEntrance - _canalBone.parent.position) - _canalAxis * Vector3.Dot(_canalEntrance - _canalBone.parent.position, _canalAxis)).magnitude * 1000f).ToString("F1") + "mm"
                            : ""));

                    // kokan frame.
                    try
                    {
                        Transform herK720 = _canalBone.parent;
                        Transform vRoot = null, vInner = null, kok00 = null;
                        foreach (var t720 in herK720.root.GetComponentsInChildren<Transform>(true))
                        {
                            if (t720 == null) continue;
                            if (t720.name == "cf_J_Vagina_root") vRoot = t720;
                            else if (t720.name == "cf_J_Vagina_Inner") vInner = t720;
                            else if (t720.name == "k_f_kokan_00") kok00 = t720;
                        }
                        Vector3 entL720 = herK720.InverseTransformPoint(_canalEntrance) * 1000f;
                        Vector3 axL720 = herK720.InverseTransformDirection(_canalAxis);
                        string s720 = "CloXray: VULVA-VS-ENTRANCE (her kokan frame, mm) — entrance=" + entL720.ToString("F1");
                        if (vRoot != null)
                        {
                            Vector3 vr = herK720.InverseTransformPoint(vRoot.position) * 1000f;
                            Vector3 d = entL720 - vr;
                            s720 += " | cf_J_Vagina_root=" + vr.ToString("F1")
                                 + " delta(ent-root)=" + d.ToString("F1")
                                 + " [along-canal=" + Vector3.Dot(d, axL720).ToString("F1")
                                 + "mm, perp=" + (d - axL720 * Vector3.Dot(d, axL720)).magnitude.ToString("F1") + "mm]";
                        }
                        else s720 += " | cf_J_Vagina_root=NOT FOUND";
                        if (vInner != null) s720 += " | Vagina_Inner=" + (herK720.InverseTransformPoint(vInner.position) * 1000f).ToString("F1");
                        if (kok00 != null) s720 += " | k_f_kokan_00=" + (herK720.InverseTransformPoint(kok00.position) * 1000f).ToString("F1");
                        LiquidWobbleMPBPlugin._logger?.LogWarning(s720);
                    }
                    catch (Exception e720) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: vulva-vs-entrance probe failed: " + e720.Message); }

                    // INFERRING the transform chain from downstream symptoms and measure.
                    var bonesX = _smr != null ? _smr.bones : null;
                    var bindX = (_smr != null && _smr.sharedMesh != null) ? _smr.sharedMesh.bindposes : null;
                    int kx = -1;
                    if (bonesX != null)
                        for (int i = 0; i < bonesX.Length; i++)
                            if (bonesX[i] != null && MainGameWomb.HerNameFor(bonesX[i].name) == "cf_j_kokan") { kx = i; break; }
                    if (kx >= 0 && bindX != null && kx < bindX.Length && _canalLocal != null && _canalLocal.Length >= 2)
                    {
                        Matrix4x4 m2w = bonesX[kx].localToWorldMatrix * bindX[kx];
                        Vector3 cX = new Vector3(m2w.m00, m2w.m10, m2w.m20);
                        Vector3 cY = new Vector3(m2w.m01, m2w.m11, m2w.m21);
                        Vector3 cZ = new Vector3(m2w.m02, m2w.m12, m2w.m22);
                        Vector3 tT = new Vector3(m2w.m03, m2w.m13, m2w.m23);
                        Vector3 predEnt = m2w.MultiplyPoint3x4(_canalLocal[0]);
                        Vector3 predTop = m2w.MultiplyPoint3x4(_canalLocal[_canalLocal.Length - 1]);
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: XFORM mode=" + (MainGameWomb.CurrentlyRebound ? "REBIND" : "MIRROR")
                            + " kok=" + (_canalBone != null && _canalBone.parent != null ? _canalBone.parent.lossyScale.y.ToString("F3") : "-")
                            + " | m2wScale=(" + cX.magnitude.ToString("F3") + "," + cY.magnitude.ToString("F3") + "," + cZ.magnitude.ToString("F3") + ")"
                            + " m2wT=" + tT.ToString("F3")
                            + " | boneName='" + bonesX[kx].name + "' bonePos=" + bonesX[kx].position.ToString("F3")
                            + " boneLossy=" + bonesX[kx].lossyScale.ToString("F3")
                            + " | predEnt=" + predEnt.ToString("F4") + " actualEnt=" + _canalEntrance.ToString("F4")
                            + " predVsActual=" + ((predEnt - _canalEntrance).magnitude * 1000f).ToString("F1") + "mm"
                            + " | predLen=" + ((predTop - predEnt).magnitude * 1000f).ToString("F1") + "mm"
                            + " restLocalEnt=" + _canalLocal[0].ToString("F4")
                            + " || cam='" + _lastCanalCam + "' visibleAtLatch=" + _lastCanalCamVisible
                            + " isVisibleNow=" + (_smr != null && _smr.isVisible)
                            + " updateOffscreen=" + (_smr != null && _smr.updateWhenOffscreen));
                    }
                    else
                        LiquidWobbleMPBPlugin._logger?.LogError("CloXray: XFORM probe unavailable — kokanIdx=" + kx
                            + " bindposes=" + (bindX == null ? "null" : bindX.Length.ToString())
                            + " canalLocal=" + (_canalLocal == null ? "null" : _canalLocal.Length.ToString()));
                }
            }

            if (LiquidWobbleMPBPlugin.Configured)
            {
                RingWeight       = LiquidWobbleMPBPlugin.CfgRingWeight;
                EntranceWeight   = LiquidWobbleMPBPlugin.CfgEntranceWeight;
                CervixWeight     = LiquidWobbleMPBPlugin.CfgCervixWeight;
                DepthStart       = LiquidWobbleMPBPlugin.CfgDepthStart;
                DepthEnd         = LiquidWobbleMPBPlugin.CfgDepthEnd;
                OpenWidth        = LiquidWobbleMPBPlugin.CfgOpenWidth;
                DepthSmoothing   = LiquidWobbleMPBPlugin.CfgDepthSmoothing;
                RefGirth         = LiquidWobbleMPBPlugin.CfgRefGirth;
                FullDepthIn      = LiquidWobbleMPBPlugin.CfgFullDepthIn;
                // MAIN GAME: the penis is length-limited by the animation (no Studio-style overshoot), so
                // the same depth needs a boosted displace to read as the womb stretching.
                float hBoost     = MainGameWomb.IsStudio ? 1f : LiquidWobbleMPBPlugin.CfgHStretchBoost;
                StretchMax       = LiquidWobbleMPBPlugin.CfgStretchMax * hBoost;
                StretchStart     = LiquidWobbleMPBPlugin.CfgStretchStart;
                StretchOvershoot = LiquidWobbleMPBPlugin.CfgStretchOvershoot * hBoost;
                RefLength        = LiquidWobbleMPBPlugin.CfgRefLength;
                DirReactWeight   = LiquidWobbleMPBPlugin.CfgDirReact;
                OpenLead           = LiquidWobbleMPBPlugin.CfgOpenLead;
                CloseSmoothing     = LiquidWobbleMPBPlugin.CfgCloseSmoothing;
                EntranceOpenWidth  = LiquidWobbleMPBPlugin.CfgEntranceOpenWidth;
                EntranceCloseScale = LiquidWobbleMPBPlugin.CfgEntranceCloseScale;
                OpenTime           = LiquidWobbleMPBPlugin.CfgOpenTime;
                EntranceOpenScale  = LiquidWobbleMPBPlugin.CfgEntranceOpenScale;
                MaxGirthScale      = LiquidWobbleMPBPlugin.CfgMaxGirthScale;
            }

            // Per-object controls (inert blendshapes). Defaults if the control shape is absent.
            float strength = _strengthIdx >= 0 ? _smr.GetBlendShapeWeight(_strengthIdx) / 50f : 1f;
            float dampTime = _dampeningIdx >= 0 ? Mathf.Max(0.01f, _smr.GetBlendShapeWeight(_dampeningIdx) / 100f) : 0.15f;
            // Penetration TRIGGER sensitivity (per-womb): 0 = off, 1.0 = default (weight 50), up to 2x.
            float sens = _sensIdx >= 0 ? _smr.GetBlendShapeWeight(_sensIdx) / 50f : 1f;
            _sens = sens;
            // Per-object collider opt-out: weight > 50 => skip the DynamicBoneCollider fallback entirely
            // (this womb won't react to a collider/toy pushed in) while still reacting to the BP penis.
            bool ignoreColliders = _ignoreCollidersIdx >= 0 && _smr.GetBlendShapeWeight(_ignoreCollidersIdx) > 50f;

            // BP_Strength = 0 -> hand off completely: don't touch the driven shapes, so your manual / KKPE
            // pose controls them.
            if (strength <= 0.001f || sens <= 0.001f)
            {
                if (!_handoffLogged) { _handoffLogged = true; LiquidWobbleMPBPlugin._logger?.LogInfo($"WombExpand '{name}': {(strength <= 0.001f ? "BP_Strength" : "BP_Sensitivity")} is 0 -> auto-reaction handed off to manual/KKPE (the womb will NOT auto-expand). Raise it in Material Editor to re-enable."); }
                for (int i = 0; i < _ringReaction.Length; i++) _ringReaction[i] = 0f;
                _stretchReaction = 0f;
                _moundFwdReact = 0f; _moundBackReact = 0f; _moundLeftReact = 0f; _moundRightReact = 0f;
                _depth = 0f;
                return;
            }
            _handoffLogged = false;

            // Depth + girth from BP — from the penis nearest THIS womb's entrance (TryReadNear),
            // not the globally deepest one: several wombs in a scene each pair with their own
            // penis instead of all reacting to one.
            _bpGeomDepth = false;   // cleared before this frame's decision so it reflects this frame's decision
            bool engaged = false;
            float girthScale = 1f;
            float lengthScale = 1f;
            float targetNorm = 0f;
            _bpLateral = -1f; _bpVaginaMain = false; _tipDetached = false; _tipDist = -1f; _vaginaFarLat = false;
            _entryDetached = false; _entryLat = -1f;
            EnsureVaginaPairing();   // cached 2s: is this womb seated on a cf_J_Vagina_root?
            // The womb<->penis pairing is decided in OnPreCullCanal — POST-NodesConstraints — so k_f_dan_entry is read at
            // its DRIVEN position (at the vagina/anus), not the penis-rig default. Here we just read the chosen penis.
            _bpHadGirthThisFrame = false;   // b666: reset each frame; set below if BP reports a real girth
            // b671 read the penis girth at SPAWN, before penetration. BP's girth is the male's
            // STATIC penis-collider radii, present from character load — so once the canal is known we
            // find the positioned penetrator (nearest male, generous range) and latch its girth NOW,
            // instead of waiting for the entry marker to reach the canal (penetration). Runs only until
            // latched (s_bpDiaMM>0) and only while not yet paired — then the paired-read below owns it,
            // and the rise-at-limit corrects upward if it differs under compression.
            if (s_bpDiaMM <= 0f && _canalLen > 1e-4f && !MainGameWomb.IsStudio)
            {
                string preMale = BPBridge.FindNearestMaleName(_canalEntrance, 0.50f, Wearer());
                BPBridge.Reading preBp = default(BPBridge.Reading);
                bool got = preMale != null && BPBridge.TryReadLocked(preMale, out preBp) && preBp.found;
                float preGirth = got ? preBp.girthTip * 2000f : 0f;
                if (got && preGirth > 1e-4f) LatchBPGirth(preGirth, preMale);
                else if (LiquidWobbleMPBPlugin.CfgDebugLog && Time.unscaledTime >= _girthLogNext)
                { _girthLogNext = Time.unscaledTime + 2f; LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: SPAWN girth pre-read — male=" + (preMale ?? "NONE-in-0.5m") + " found=" + got + " girthTip=" + preGirth.ToString("F1") + "mm (0=BP colliders not populated yet)."); }
            }
            if (BPBridge.TryReadLocked(_pairedName, out _bp) && _bp.found)
            {
                if (_bp.girthTip > 1e-4f) { LatchBPGirth(_bp.girthTip * 2000f, _bp.srcName); _bpHadGirthThisFrame = true; }
                float w = PenisInCanalWidth;
                // `contained` = a dan bone (or the aimed tip) physically threads the canal.
                float realAlong = 0f, realLat = -1f;
                bool realIn = (_bp.shaftPos != null) && DeepestShaftInCanal(_bp.shaftPos, _bp.shaftPos.Length, w, out realAlong, out realLat);
                float aimAlong = 0f, aimLat = -1f; bool aimedIn = false;
                if (_bp.aimedValid) { _aimBuf[0] = _bp.aimedTipPos; aimedIn = DeepestShaftInCanal(_aimBuf, 1, w, out aimAlong, out aimLat); }
                bool contained = realIn || aimedIn;
                _bpLateral = realIn ? realLat : (aimedIn ? aimLat : _bpClosest);
                // MAIN penis detection (build 376): the womb is an x-ray overlay ON a vagina, so react when
                // BP says THIS womb's vagina is being penetrated.
                float tipDetach = LiquidWobbleMPBPlugin.CfgTipDetach;
                // DIRECTIONAL tip-detach: the aimed tip (k_f_dan_end) counts as "pulled out" (withdrawn)
                // only when it's BELOW the mouth (along < -detach) or far off the canal SIDEWAYS (lateral > detach).
                bool tipAttached;
                if (_bp.aimedValid && _canalLen > 1e-4f)
                {
                    Vector3 trel = _bp.aimedTipPos - _canalEntrance;
                    float tAlong = Vector3.Dot(trel, _canalAxis);
                    float tLat = (trel - _canalAxis * tAlong).magnitude;
                    _tipDist = trel.magnitude;
                    tipAttached = tAlong > -tipDetach && tLat <= tipDetach;
                }
                else { _tipDist = -1f; tipAttached = true; }
                _tipDetached = !tipAttached;
                // ENTRY-DETACH (build 429): treat the penis as WITHDRAWN when its entry/base marker
                // k_f_dan_entry swings OFF the canal axis.
                bool entryAttached = true;
                if (_grabEntryW != Vector3.zero && _canalLen > 1e-4f)
                {
                    Vector3 erel = _grabEntryW - _canalEntrance;
                    float eAlong = Vector3.Dot(erel, _canalAxis);
                    _entryLat = (erel - _canalAxis * eAlong).magnitude;
                    entryAttached = _entryLat <= LiquidWobbleMPBPlugin.CfgEntryDetach;
                }
                else { _entryLat = -1f; }
                _entryDetached = !entryAttached;
                // Sensitivity scales the depth needed to engage: sens>=1 -> EngageEps (unchanged); sens<1 ->
                // needs deeper insertion (a display womb won't react to a shallow/near penis).
                float depthGate = EngageEps + Mathf.Max(0f, 1f - sens) * 0.30f;
                // LATERAL gate on the VAGINA-PAIRED path: BP depth in a paired vagina is NOT enough.
                float latGate = Mathf.Max(WombBulbRadius(), PenisInCanalWidth) * sens;
                bool vaginaNear = _vaginaPaired && (_bpLateral < 0f || _bpLateral <= latGate);
                _vaginaFarLat = _vaginaPaired && !contained && _bpLateral >= 0f && _bpLateral > latGate;
                if (_bp.depth > depthGate && (contained || vaginaNear) && tipAttached && entryAttached)
                {
                    engaged = true;
                    targetNorm = (FullDepthIn > 1e-4f) ? _bp.visualDepth / FullDepthIn : _bp.visualDepth;
                    _bpVaginaMain = !contained;   // diag: reacting from BP/vagina alone — the bones never threaded the canal
                }
                // BetterPenetration only reports depth for an orifice it recognises.
                else if (MainGameWomb.IsStudio && !engaged && contained && _bp.found && _bp.hasPose && _canalLen > 1e-4f && tipAttached && entryAttached)
                {
                    // MEASURE ALONG THE LINE THE PENIS ACTUALLY TRAVELS. penis_target is authored far down
                    // the canal line as an aim marker.
                    Vector3 bpEntry = _grabEntryW;
                    Vector3 bpEnd = BPBridge.GetEndWorld(_pairedName);
                    Vector3 canalDir = _canalAxis;
                    Vector3 measureFrom = _canalEntrance;
                    bool onBpLine = false;
                    if (bpEntry != Vector3.zero && bpEnd != Vector3.zero)
                    {
                        Vector3 ln = bpEnd - bpEntry;
                        if (ln.sqrMagnitude > 1e-6f)
                        {
                            canalDir = ln.normalized;
                            measureFrom = bpEntry;   // the entry marker sits on the canal mouth by constraint
                            onBpLine = true;
                        }
                    }
                    Vector3 trel = _bp.tipPos - measureFrom;
                    float tAlong = Vector3.Dot(trel, canalDir);
                    float tLat = (trel - canalDir * tAlong).magnitude;
                    // tipPos is the deepest shaft BONE, and the rendered glans reaches well past it. Walk the
                    // shaft colliders instead and take the deepest SURFACE point along the canal (centre plus
                    // its own radius) - the same measure the toy/collider path uses, so a ring opens as the
                    // penis actually arrives at it rather than once it has already gone through.
                    float surf = tAlong + _bp.girthTip;   // bone + tip radius, until the colliders say better
                    if (_bp.girthPos != null && _bp.girthRad != null)
                    {
                        int gn = Mathf.Min(_bp.girthPos.Length, _bp.girthRad.Length);
                        for (int gi = 0; gi < gn; gi++)
                        {
                            float ga = Vector3.Dot(_bp.girthPos[gi] - measureFrom, canalDir) + _bp.girthRad[gi];
                            if (ga > surf) surf = ga;
                        }
                    }
                    tAlong = surf;
                    // Straight canal fraction: the tip is now measured along BP's own line from its entry
                    // marker, so the depth is the honest one and needs no scaling.
                    float tNorm = tAlong / _canalLen;
                    tNorm = Mathf.Min(tNorm, LiquidWobbleMPBPlugin.CfgDepthEnd + LiquidWobbleMPBPlugin.CfgOpenLead + 0.10f);
                    // No lateral re-test here: `contained` above is already the authoritative "the shaft
                    // threads THIS canal" check, and re-testing against an axis the tip may not follow only shuts the womb down.
                    if (!_offCanalWarned && tLat > 0.020f && tNorm > depthGate)
                    {
                        _offCanalWarned = true;
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb '" + name + "' - the penis passes "
                            + (tLat * 1000f).ToString("F0") + "mm BESIDE this womb's canal centreline (the canal is a narrow tube). "
                            + "It may look inside the womb, but it is not in the canal, so the rings only follow how far it advances "
                            + "along the canal. Move/rotate the womb so its canal mouth sits where he enters and the canal points along him.");
                    }
                    if (tNorm > depthGate)
                    {
                        engaged = true;
                        targetNorm = tNorm;
                        _bpGeomDepth = true;   // diag: depth measured from the tip, not from BP compression
                    }
                }
                if (engaged)
                {
                    // Ring WIDTH tracks the penis's STATIC TIP girth (girthTip / RefGirth).
                    float g = (RefGirth > 1e-5f) ? _bp.girthTip / RefGirth : 1f;
                    girthScale = Mathf.Clamp(g, 0.4f, MaxGirthScale);
                    lengthScale = (RefLength > 1e-4f) ? Mathf.Clamp(_bp.baseLen / RefLength, 0.6f, 2.0f) : 1f;
                }
            }

            // ALSO react to a DynamicBoneCollider on a bottle/toy pushed in (a KKPE BP collider).
            _colLateral = -1f; _colName = null; _colPerRing = false;
            // Off-screen skip (perf, scales with womb count): a NON-engaged womb that is NOT visible has no
            // reaction a viewer could see, so skip the per-frame collider scan.
            if (LiquidWobbleMPBPlugin.CfgReactColliders && !ignoreColliders && (engaged || _smr == null || _smr.isVisible))
            {
                try
                {
                    EnsureCanal();
                    // Precompute each ring's normalized depth so the collider scan can report the THICKEST
                    // collider reaching each ring (two toys -> each ring follows the biggest thing at its level).
                    int rn = _ringIdx.Length;
                    if (_ringDepths == null || _ringDepths.Length != rn) { _ringDepths = new float[rn]; _colRingRadius = new float[rn]; }
                    for (int ri = 0; ri < rn; ri++) _ringDepths[ri] = rn == 1 ? DepthStart : Mathf.Lerp(DepthStart, DepthEnd, (float)ri / (rn - 1));
                    Vector3 ctip, cbase; float crad, cdrive, clat; string cname;
                    if (_canalLen > 1e-4f &&
                        ColliderBridge.TryReadNearCanal(_canalEntrance, _canalAxis, LiquidWobbleMPBPlugin.CfgColliderRange,
                                                        LiquidWobbleMPBPlugin.CfgColliderInCanal, _canalLen, LiquidWobbleMPBPlugin.CfgColliderMaxRadius,
                                                        WombBulbRadius(), LiquidWobbleMPBPlugin.CfgColliderNameForGame,
                                                        _ringDepths, _colRingRadius,
                                                        out ctip, out cbase, out crad, out cdrive, out clat, out cname))
                    {
                        float cdepth = cdrive / _canalLen;   // 0 entrance .. 1 top (tip-based when name-targeted; can overshoot)
                        _colLateral = clat; _colName = cname;
                        if (cdepth > EngageEps && cdepth > targetNorm)   // only take over when the toy reaches DEEPER than the penis
                        {
                            engaged    = true;
                            targetNorm = cdepth;
                            girthScale = (RefGirth > 1e-5f) ? Mathf.Clamp(crad / RefGirth, 0.4f, MaxGirthScale) : 1f;
                            lengthScale = 1f;
                            // Per-ring radii are a STUDIO feature: in H the colliders slide with the
                            // hips in bent poses (reverse cowgirl), and the "thickest collider at this
                            // ring's depth" picks up random passers-by — the top ring visibly popped
                            // open at random. H uses the stable uniform tip girth instead.
                            _colPerRing = MainGameWomb.IsStudio;   // _colRingRadius[] holds the per-ring thickest collider -> ring loop uses it
                            _bp = new BPBridge.Reading {
                                found = true, depth = cdepth, visualDepth = cdepth,
                                girthBase = crad, girthTip = crad, girthFactor = 1f, baseLen = 0f,
                                tipPos = ctip,
                                tipDir = (ctip - cbase).sqrMagnitude > 1e-8f ? (ctip - cbase).normalized : Vector3.up,
                                hasPose = true };
                            if (!_bpHadGirthThisFrame) LatchGirth(crad * 2000f, "H-colliders");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    if (!_colWarned) { LiquidWobbleMPBPlugin._logger?.LogWarning("WombExpand collider-react failed (disabled for this womb): " + ex.Message); _colWarned = true; }
                }
            }

            // for >1.5s, print every gate component so the flickering input is readable from the log instead
            // of guessed.
            if (LiquidWobbleMPBPlugin.CfgDebugLog && MainGameWomb.HPenetrated && !engaged)
            {
                if (_engMissT <= 0f) _engMissT = Time.unscaledTime;
                else if (Time.unscaledTime - _engMissT > 1.5f && Time.unscaledTime >= _engMissLogNext)
                {
                    _engMissLogNext = Time.unscaledTime + 3f;
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: ENGAGE-MISS " + (Time.unscaledTime - _engMissT).ToString("F0")
                        + "s penetrated-but-unengaged: bpFound=" + _bp.found + " depth=" + _bp.depth.ToString("F3")
                        + " visDepth=" + _bp.visualDepth.ToString("F3") + " girthTip=" + (_bp.girthTip * 2000f).ToString("F1")
                        + "mm lateral=" + (_bpLateral >= 0f ? (_bpLateral * 1000f).ToString("F0") + "mm" : "?")
                        + " vagPaired=" + _vaginaPaired + " farLat=" + _vaginaFarLat);
                }
            }
            else _engMissT = 0f;

            // MAIN GAME: engagement GRACE + canal boost.
            if (!MainGameWomb.IsStudio)
            {
                // PENETRATED.
                if (!HasPenetratedFlag || !ExternalPenetrated) { engaged = false; _hEngHold = 0f; }
                if (!HasPenisEnd || _canalLen <= 1e-4f) { engaged = false; _hEngHold = 0f; }
                else
                {
                    Vector3 er = ExternalPenisEnd - _canalEntrance;
                    float ea = Vector3.Dot(er, _canalAxis);
                    float el = (er - _canalAxis * ea).magnitude;
                    if (!(ea > 0f && el < _canalLen * 0.6f)) { engaged = false; _hEngHold = 0f; }
                }
                if (engaged)
                {
                    _hEngHold = Time.unscaledTime + 1.2f;
                    _hEngDepth = targetNorm; _hEngGirth = girthScale; _hEngLen = lengthScale;
                }
                else if (Time.unscaledTime < _hEngHold)
                {
                    engaged = true;
                    targetNorm = _hEngDepth; girthScale = _hEngGirth; lengthScale = _hEngLen;
                }
                // Depth = the real penis end along the canal. Always use it in H (no >0 gate.
                if (engaged && _canalLen > 1e-4f)
                    targetNorm = Mathf.Clamp(ExternalStrokeMM * 0.001f / _canalLen, 0f, 1.5f);
                // H-only canal opening boost, stretch-coupled: baseline wider for the penis girth, and as
                // the womb stretches the canal narrows back toward the stretched value.
                if (engaged)
                {
                    if (_openEfficiency <= 1e-3f && MainGameWomb.CurrentlyRebound && MainGameWomb.RebindS > 1e-3f)
                    {
                        float herLossy = (_canalBone != null && _canalBone.parent != null) ? _canalBone.parent.lossyScale.y : 1f;
                        _openEfficiency = s_carryEff > 1e-3f ? s_carryEff : 53.6f * MainGameWomb.RebindS * herLossy;
                    }
                    if (_openEfficiency > 1e-3f)
                    {
                        if (_bp.girthTip > 1e-4f) LatchBPGirth(_bp.girthTip * 2000f, _bp.srcName);
                        // collider latch only when there's no BP girth, then the live read.
                        float diaMM = s_bpDiaMM > 0f ? s_bpDiaMM
                                    : (s_diaMM > 0f ? s_diaMM
                                    : (_bp.girthTip > 1e-4f ? _bp.girthTip * 2000f : 0f));
                        float liveNow = _bp.girthTip > 1e-4f ? _bp.girthTip * 2000f : 0f;
                        float riseCap = diaMM * 1.25f;
                        if (_girthRiseMM <= 0f) _girthRiseMM = diaMM;
                        float riseTgt = Mathf.Clamp(Mathf.Max(liveNow, diaMM), diaMM, riseCap);
                        _girthRiseMM = _girthRiseMM < riseTgt
                            ? Mathf.Min(riseTgt, _girthRiseMM + 60f * Time.deltaTime)
                            : Mathf.Max(riseTgt, _girthRiseMM - 12f * Time.deltaTime);
                        diaMM = Mathf.Max(diaMM, _girthRiseMM);
                        // FATTER visual shaft (BP danGirthSquish=0.8 volume conservation).
                        float fitR = BPInnerTargetPin.FitSquishRatio;
                        diaMM *= Mathf.Clamp(1f + 0.4f * (1f - fitR), 0.85f, 1.15f);
                        if (diaMM > 0f)
                        {
                            float needed = (diaMM * WidthMargin) / _openEfficiency;
                            girthScale = Mathf.Clamp(needed, 0.4f, MaxGirthScale);
                        }
                    }
                    _lastGirthScale = girthScale;
                    if (LiquidWobbleMPBPlugin.CfgDebugLog && Time.unscaledTime >= _girthLogNext)
                    {
                        _girthLogNext = Time.unscaledTime + 2f;
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: GIRTH-DRIVE bpLatch=" + s_bpDiaMM.ToString("F1")
                            + "mm colliderLatch=" + s_diaMM.ToString("F1")
                            + "mm (male='" + (s_diaMale ?? "?") + "') liveBPgirthTip=" + (_bp.girthTip * 2000f).ToString("F1")
                            + "mm openEff=" + _openEfficiency.ToString("F1") + "mm/unit margin=" + WidthMargin.ToString("F2")
                            + " -> girthScale=" + girthScale.ToString("F2") + " canalOpening~" + (girthScale * _openEfficiency).ToString("F1")
                            + "mm (penis pokes through if this < the VISUAL penis diameter).");
                    }
                }
            }

            _dbgEngaged = engaged;   // engagement state (IsEngaged); gates calibration + the mesh reaction

            // Rise fast (DepthSmoothing), FALL slow (CloseSmoothing) so the depth lingers on withdrawal ->
            // the rings close LATER (follow-through), while opening still tracks the penis going.
            float tgtDepth = engaged ? targetNorm : 0f;
            float sm = (tgtDepth >= _depth) ? DepthSmoothing : CloseSmoothing;
            float k = sm > 0f ? 1f - Mathf.Exp(-sm * Time.deltaTime) : 1f;
            // The rise filter is there to tame BP's compression signal, which is noisy.
            if (_bpGeomDepth && tgtDepth >= _depth) k = 1f;
            _depth = Mathf.Lerp(_depth, tgtDepth, k);

            // MAIN GAME intent depth: project the fed intent tip on the canal, smoothed with the same
            // rise/fall constants as the real depth.
            if (HasIntentTip && _canalLen > 1e-4f)
            {
                float ir = Mathf.Max(Vector3.Dot(ExternalIntentTip - _canalEntrance, _canalAxis) / _canalLen, 0f);
                float tgtI = engaged ? ir : 0f;
                float smI = (tgtI >= _hIntentDepth) ? DepthSmoothing : CloseSmoothing;
                _hIntentDepth = Mathf.Lerp(_hIntentDepth, tgtI, smI > 0f ? 1f - Mathf.Exp(-smI * Time.deltaTime) : 1f);
            }
            else _hIntentDepth = 0f;

            float closeK    = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(dampTime, 0.02f));  // close = exponential ease-OUT (decelerates smoothly to target, never an instant snap)
            float openStep  = (100f / Mathf.Max(OpenTime, 0.01f)) * Time.deltaTime; // open rate (eases in, not instant)

            int n = _ringIdx.Length;
            for (int i = 0; i < n; i++)
            {
                if (_ringIdx[i] < 0) continue;

                float ringDepth = n == 1 ? DepthStart : Mathf.Lerp(DepthStart, DepthEnd, (float)i / (n - 1));
                // PER-RING girth.
                float ringGScale = girthScale;
                if (engaged && _bp.girthPos != null)
                {
                    float lr = GirthAtDistFromTip(Mathf.Max(0f, _depth - ringDepth) * _canalLen);
                    if (lr > 0f && RefGirth > 1e-5f) ringGScale = Mathf.Clamp(lr / RefGirth, 0.4f, MaxGirthScale);
                }
                else if (engaged && _colPerRing && _colRingRadius != null && i < _colRingRadius.Length && _colRingRadius[i] > 0f && RefGirth > 1e-5f)
                {
                    ringGScale = Mathf.Clamp(_colRingRadius[i] / RefGirth, 0.4f, MaxGirthScale);
                }
                float baseW = Mathf.Min((i == 0 ? EntranceWeight : (i == n - 1 ? CervixWeight : RingWeight)) * ringGScale, Mathf.Min(MaxRingWeight, 100f));   // never exceed 100 (KKPE latches a >100 blendshape write)
                // Body rings (V2..V5) reach full AHEAD by OpenLead as the tip passes them. The ENTRANCE (V1)
                // is special: it opens GRADUALLY from depth 0 (the moment the tip enters) over EntranceOpenWidth,
                // so it shows intermediate states during shallow insertion instead of snapping fully open the
                // instant the tip touches it (its old anticipation window sat below depth 0 -> ~full by 0.04).
                float target;
                if (!engaged) target = 0f;
                else if (i == 0) target = Mathf.SmoothStep(0f, 1f, _depth / Mathf.Max(EntranceOpenWidth, 1e-4f)) * baseW;
                else target = OpenFraction(ringDepth - OpenLead, OpenWidth) * baseW;

                // Rate-limited OPEN (eases in instead of snapping) + close at the dampening rate.
                float ringOpenStep = (i == 0) ? openStep / Mathf.Max(EntranceOpenScale, 0.01f) : openStep;
                float ringCloseK = (i == 0) ? 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(dampTime * EntranceCloseScale, 0.02f)) : closeK;
                if (target >= _ringReaction[i]) _ringReaction[i] = Mathf.MoveTowards(_ringReaction[i], target, ringOpenStep);
                else                            _ringReaction[i] = Mathf.Lerp(_ringReaction[i], target, ringCloseK);

                // Override (strength scales the reaction; manual is only honoured at strength 0 above).
                _smr.SetBlendShapeWeight(_ringIdx[i], Mathf.Clamp(_ringReaction[i] * strength, 0f, 100f));   // never write >100 (it latches/sticks in KKPE)
            }

            if (_stretchIdx >= 0)
            {
                // Displace travel scales WITH the womb: womb_displace moves the cervix a fixed FRACTION of
                // the canal at full weight, and both scale together.
                float DisplaceTravelMM = 0.5f * _canalLen * 1000f;
                float st;
                if (MainGameWomb.IsStudio)
                {
                    // STUDIO: displace from depth overshoot, scaled by BP penis length (the classic path).
                    st = engaged ? StretchTarget() * lengthScale : 0f;
                }
                else
                {
                    // MAIN GAME.
                    const float RestStretchVisual = 0.7f;
                    float baseW = _baseStretchPctEff / 0.71f * (engaged ? 1f : RestStretchVisual);
                    float contact = 0f;
                    if (engaged)
                    {
                        if (LiquidWobbleMPBPlugin.CfgHAutoLength && _canalLen > 1e-4f)
                        {
                            // the COMPRESSION BP's squish absorbed (raw L−d2−canal, fed by the pin).
                            const float EarlyMM = 7f;
                            float cmp = ExternalCompressMM;
                            // b669: BASELINE-SUBTRACTED stroke term (replaces b650's blunt suppression,
                            // which killed the LEGITIMATE deep-stroke dome push on a floored penis whose
                            // tip strokes from BELOW to ABOVE the cervix). Track the shallow-end tip depth
                            // (min, slow-decay) as the parked baseline: a floored penis PARKED past the
                            // cervix has a high baseline => ~0 net (no false constant displace, the b650
                            // problem); a real stroke that dips below the cervix has ~0 baseline => the
                            // full overshoot drives the dome (this char: tip 63→93 on an 89mm canal).
                            float onsetMM = _canalLen * 1000f - EarlyMM;   // cervix minus early-onset
                            float tipMM = ExternalStrokeMM;
                            _tipMinMM = (_tipMinMM <= 0f || tipMM < _tipMinMM) ? tipMM : Mathf.Min(tipMM, _tipMinMM + 25f * Time.deltaTime);
                            float baselineMM = Mathf.Max(_tipMinMM - onsetMM, 0f);
                            float strokeOver = Mathf.Max(0f, tipMM - onsetMM - baselineMM);
                            float overMM = Mathf.Max(strokeOver,
                                                     cmp > 0f ? cmp + EarlyMM * Mathf.Clamp01(cmp / EarlyMM) : 0f);
                            _hOnset = _canalLen * 1000f; _hStrokeMax = ExternalStrokeMM;   // (diag)
                            contact = Mathf.Clamp(overMM / DisplaceTravelMM * 100f * LiquidWobbleMPBPlugin.CfgHWombPush, 0f, 100f);
                        }
                        else if (ExternalStrokeMM > 0f)
                        {
                            // Legacy contact-point (auto-length off): onset at ContactPct% of the reach.
                            _hStrokeMax = Mathf.Max(ExternalStrokeMM, _hStrokeMax - 25f * Time.deltaTime);
                            _hOnset = Mathf.Clamp01(LiquidWobbleMPBPlugin.CfgHContactPct * 0.01f) * _hStrokeMax;
                            float span = Mathf.Max(_hStrokeMax - _hOnset, 3f);
                            contact = Mathf.Clamp01((ExternalStrokeMM - _hOnset) / span) * LiquidWobbleMPBPlugin.CfgHPressGain;
                        }
                        else contact = Mathf.Clamp01(ExternalPress) * LiquidWobbleMPBPlugin.CfgHPressGain;   // no-BP-agent fallback
                    }
                    st = Mathf.Clamp(baseW + contact, 0f, 100f);
                    if (!ExternalFitLocked) st = Mathf.Min(st, baseW);
                    // DEBUG (Free-H bring-up): F1 slider forces the displace weight directly.
                    float force = LiquidWobbleMPBPlugin.CfgHForceStretch;
                    if (force >= 0f) st = force;
                }
                if (MainGameWomb.IsStudio)
                {
                    if (st >= _stretchReaction) _stretchReaction = st;                     // Studio: rise-instant, fall-slow (follow-through)
                    else _stretchReaction = Mathf.Lerp(_stretchReaction, st, closeK);
                }
                else
                {
                    // Rise speed: the fit path's tip signal is precise, so the dome tracks it fast
                    // (26/s - slower lagged the arriving tip and the penis tunneled in). With
                    // auto-length OFF the drive signals are coarser (commanded stop depth / thrust
                    // envelope) and the same speed reads as a shove - track gently there (9/s).
                    // Fall stays 7/s once the tip has left: while inside, the tip holds the dome
                    // and the reaction follows it tightly both ways; only the residual relaxes calm.
                    float riseK = LiquidWobbleMPBPlugin.CfgHAutoLength ? 26f : 9f;
                    float trackK = (st > _stretchReaction) ? riseK : (st > 0.01f ? riseK : 7f);
                    _stretchReaction = Mathf.Lerp(_stretchReaction, st, 1f - Mathf.Exp(-trackK * Time.deltaTime));
                }

                _smr.SetBlendShapeWeight(_stretchIdx, Mathf.Clamp(_stretchReaction * strength, 0f, 100f));
            }

            // Mouth leans toward the INCOMING PENIS, matching BP's own kokan-pull.
            if (_moundFwdIdx >= 0 || _moundBackIdx >= 0 || _moundLeftIdx >= 0 || _moundRightIdx >= 0)
            {
                float fwd = 0f, back = 0f, left = 0f, right = 0f;
                if (engaged && _bp.hasPose && DirReactWeight > 0.01f)
                {
                    // Lean toward the penis's APPROACH ANGLE, not its POSITION.
                    Vector3 lat = _bp.tipDir - Vector3.Dot(_bp.tipDir, _canalAxis) * _canalAxis;
                    if (lat.sqrMagnitude > 1e-8f)
                    {
                        // NEGATED vs build 380: in-game BOTH axes (L/R and F/B) leaned the WRONG way, so flip the local
                        // lateral to lean TOWARD the penis. Then sqrt() shapes the response so even a SMALL off-axis angle
                        // already gives a clear lean (reacts FAST), easing toward the DirReactWeight ceiling so it "grows
                        // lower" overall — lower the 'Direction reaction' cfg for a smaller max.
                        Vector3 ld = -_smr.transform.InverseTransformDirection(lat);   // womb-local lean dir, sign-corrected
                        float lz = Mathf.Clamp(ld.z * 3f, -1f, 1f);
                        float lx = Mathf.Clamp(ld.x * 3f, -1f, 1f);
                        const float sideScale = 0.5f;   // L/R lean = HALF of fwd/back (per the human — sideways lean was too strong)
                        fwd   = Mathf.Sqrt(Mathf.Max(0f,  lz)) * DirReactWeight;              back  = Mathf.Sqrt(Mathf.Max(0f, -lz)) * DirReactWeight;
                        right = Mathf.Sqrt(Mathf.Max(0f,  lx)) * DirReactWeight * sideScale;  left  = Mathf.Sqrt(Mathf.Max(0f, -lx)) * DirReactWeight * sideScale;
                    }
                }
                _moundFwdReact   = (fwd   >= _moundFwdReact)   ? fwd   : Mathf.Lerp(_moundFwdReact,   fwd,   closeK);
                _moundBackReact  = (back  >= _moundBackReact)  ? back  : Mathf.Lerp(_moundBackReact,  back,  closeK);
                _moundLeftReact  = (left  >= _moundLeftReact)  ? left  : Mathf.Lerp(_moundLeftReact,  left,  closeK);
                _moundRightReact = (right >= _moundRightReact) ? right : Mathf.Lerp(_moundRightReact, right, closeK);
                if (_moundFwdIdx   >= 0) _smr.SetBlendShapeWeight(_moundFwdIdx,   Mathf.Clamp(_moundFwdReact   * strength, 0f, 100f));
                if (_moundBackIdx  >= 0) _smr.SetBlendShapeWeight(_moundBackIdx,  Mathf.Clamp(_moundBackReact  * strength, 0f, 100f));
                if (_moundLeftIdx  >= 0) _smr.SetBlendShapeWeight(_moundLeftIdx,  Mathf.Clamp(_moundLeftReact  * strength, 0f, 100f));
                if (_moundRightIdx >= 0) _smr.SetBlendShapeWeight(_moundRightIdx, Mathf.Clamp(_moundRightReact * strength, 0f, 100f));
            }

        }

        // Ring opens as depth reaches it: closed until (ringDepth.
        private float OpenFraction(float ringDepth, float width)
        {
            width = Mathf.Max(width, 1e-4f);
            float t = Mathf.Clamp01((_depth - (ringDepth - width)) / width);
            return Mathf.SmoothStep(0f, 1f, t);
        }

        // Stretch ramps StretchStart..1 to StretchMax, then overshoot beyond 1, capped.
        private float StretchTarget() { return StretchTargetAt(_depth); }

        // The classic Studio displace curve, parametrized so the MAIN GAME can run the same curve on its
        // intent depth (where the tip would be if nothing yielded) instead of the pinned tip.
        private float StretchTargetAt(float depth)
        {
            if (depth <= StretchStart) return 0f;
            if (depth <= 1f)
            {
                float span = Mathf.Max(1f - StretchStart, 1e-4f);
                return Mathf.SmoothStep(0f, 1f, (depth - StretchStart) / span) * StretchMax;
            }
            return Mathf.Min(StretchMax + (depth - 1f) * StretchOvershoot, StretchCap);
        }

        private bool Resolve()
        {
            string[] names = SplitCsv(RingBlendShapes);
            if (names.Length == 0) return false;

            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                if (FindBlendShapeIndex(smr.sharedMesh, names[0]) < 0) continue;
                _smr = smr;
                break;
            }
            if (_smr == null) return false;

            var mesh = _smr.sharedMesh;
            _ringIdx = new int[names.Length];
            _ringReaction = new float[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                _ringIdx[i] = FindBlendShapeIndex(mesh, names[i]);
                if (_ringIdx[i] < 0)
                    LiquidWobbleMPBPlugin._logger?.LogWarning(
                        $"{nameof(WombExpandEffect)} on '{name}': blendshape '{names[i]}' not found.");
            }
            _stretchIdx   = FindBlendShapeIndex(mesh, StretchBlendShape);
            if (_stretchIdx < 0)
                // channel would just stop the womb reacting with zero explanation.
                LiquidWobbleMPBPlugin._logger?.LogError($"CloXray: displace channel '{StretchBlendShape}' NOT FOUND on '{name}' — the womb will NOT react to deep strokes. The zipmod does not match this DLL; update [Clo]XrayWomb1.zipmod.");
            _strengthIdx  = FindBlendShapeIndex(mesh, StrengthBlendShape);
            _dampeningIdx = FindBlendShapeIndex(mesh, DampeningBlendShape);
            _ignoreCollidersIdx = FindBlendShapeIndex(mesh, IgnoreCollidersShape);
            _sensIdx      = FindBlendShapeIndex(mesh, SensitivityBlendShape);
            _moundFwdIdx  = FindBlendShapeIndex(mesh, MoundForwardShape);
            _moundBackIdx = FindBlendShapeIndex(mesh, MoundBackShape);
            _moundLeftIdx  = string.IsNullOrEmpty(MoundLeftShape)  ? -1 : FindBlendShapeIndex(mesh, MoundLeftShape);
            _moundRightIdx = string.IsNullOrEmpty(MoundRightShape) ? -1 : FindBlendShapeIndex(mesh, MoundRightShape);
            return true;
        }

        // KK names blendshapes "o_uterus.Name"; try exact, then suffix.
        public static int FindBlendShapeIndex(Mesh mesh, string nameOrSuffix)
        {
            if (mesh == null || string.IsNullOrEmpty(nameOrSuffix)) return -1;
            int exact = mesh.GetBlendShapeIndex(nameOrSuffix);
            if (exact >= 0) return exact;
            int n = mesh.blendShapeCount;
            for (int i = 0; i < n; i++)
            {
                string bn = mesh.GetBlendShapeName(i);
                if (bn == null) continue;
                if (bn.EndsWith("." + nameOrSuffix, StringComparison.Ordinal) ||
                    bn.EndsWith(nameOrSuffix, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private static string[] SplitCsv(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return new string[0];
            var raw = csv.Split(',');
            int n = 0;
            for (int i = 0; i < raw.Length; i++) { raw[i] = raw[i].Trim(); if (raw[i].Length > 0) n++; }
            var res = new string[n];
            int w = 0;
            for (int i = 0; i < raw.Length; i++) if (raw[i].Length > 0) res[w++] = raw[i];
            return res;
        }

        public Vector3 EntranceWorld()
        {
            if (_entranceBone == null)
                foreach (var t in GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == MainGameWomb.WombBone("cf_j_kokan")) { _entranceBone = t; break; }
            return _entranceBone != null ? _entranceBone.position : transform.position;
        }

        /// The womb's organ-shell body stencil (_StencilBody on its CloXray/Organ material); the BodyReveal
        /// copy must match.
        public int OrganStencil()
        {
            if (_smr != null)
                foreach (var m in _smr.sharedMaterials)
                    if (m != null && m.HasProperty("_StencilBody"))
                        return Mathf.RoundToInt(m.GetFloat("_StencilBody"));
            return 4;
        }

        /// Called by AutoBodyReveal when the BodyReveal stamp is applied/adopted for this womb's character.
        public void OnBodyRevealApplied()
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                bool hasOrgan = false;
                foreach (var sm in r.sharedMaterials)
                    if (sm != null && sm.shader != null && sm.shader.name == "CloXray/Organ") { hasOrgan = true; break; }
                if (!hasOrgan) continue;
                foreach (var im in r.materials)
                    if (im != null && im.shader != null && im.shader.name == "CloXray/Organ")
                    {
                        if (MainGameWomb.IsStudio)
                        {
                            // Studio: restore the half-in/half-out feature (interior+cum show outside body).
                            if (im.HasProperty("_OutBodyBackOcclude")) im.SetFloat("_OutBodyBackOcclude", 1f);
                            if (im.HasProperty("_OutBodySceneConfine")) im.SetFloat("_OutBodySceneConfine", 1f);
                        }
                        else
                        {
                            // deep/edge pose can never flash the organ past the body silhouette.
                            if (im.HasProperty("_OutBodyBackOcclude")) im.SetFloat("_OutBodyBackOcclude", 0f);
                            if (im.HasProperty("_OutsideOfBodyAlpha")) im.SetFloat("_OutsideOfBodyAlpha", 0f);
                        }
                    }
                LiquidWobbleMPBPlugin._logger?.LogInfo(MainGameWomb.IsStudio
                    ? $"[spawn-default] '{name}': BodyReveal applied -> Studio out-of-body interior+cum restored (occlude=1, sceneConfine=1)."
                    : $"[spawn-default] '{name}': BodyReveal applied -> FREE-H: shell+interior+cum HIDDEN out-of-body (alpha=0, occlude=0).");
                return;
            }
        }
    }
}
