using System;
using UnityEngine;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// Drives the womb canal blendshapes from BetterPenetration, mirroring BP's own squish math.
    ///
    /// - Depth = BP's length-squished (rendered) tip position, normalized by FullDepthIn so the
    ///   deepest ring actually reaches open. Rings open progressively up to that depth.
    /// - Ring width scales with BP penis girth (maker + studio width) and BP's girth-squish, so a
    ///   bigger / deeper-pressed penis opens wider - matching BP's growth speed and thickness.
    /// - Vagina_stretch lengthens the canal as the tip nears/over-shoots the womb mouth, so a penis
    ///   aimed past the womb (deep IK target + BP Length Squish) stays visually inside the tube.
    /// - ADDITIVE over your manual pose: final = manual + reaction*strength, clamped 0..100. The
    ///   plugin remembers its own contribution, so editing a shape re-bases it.
    ///
    /// Control inputs are read (not driven) from inert blendshapes on the same mesh, so they're
    /// per-object and save with the scene (KKPE sliders, 0..100):
    ///   BP_Strength    weight/50  -> 0 = off, 50 = 1.0 (baked default), 100 = 2x  (opening AMOUNT)
    ///   BP_Dampening   weight/100 -> ring close time in seconds (baked default 15 = 0.15s)
    ///   BP_Sensitivity weight/50  -> penetration TRIGGER: 0 = off (manual), 50 = 1.0 (baked default), 100 = 2x.
    ///                  Scales the canal-containment lateral gate + the depth needed to engage. < 1 = the penis
    ///                  must be more centred / deeper to react (display wombs); > 1 = reacts further off-axis.
    /// </summary>
    public class WombExpandEffect : MonoBehaviour
    {
        // Rings entrance->deep, AUTO-DRIVEN by the plugin. Index 0 is the entrance (Vagina_1_open); the
        // LAST listed ring is the deepest, driven by CervixWeight (opens last, at deepest insertion).
        // M's 2026-06-22/23 mesh rework RENAMED Vagina_5_entrance_open -> Vagina_5_top (now the oval-topped
        // cervix = the deepest AUTO ring) and ADDED Vagina_6_entrance_open (the DOME above V5). Per the
        // human, **V6 is MANUAL-ONLY** (KKPE slider), NOT part of the auto animation - so it is deliberately
        // LEFT OUT of this list. The plugin only overrides shapes it lists, so V6 stays at whatever weight
        // set by hand. Channel names are a CONTRACT.
        public string RingBlendShapes { get; set; } =
            "Vagina_1_open,Vagina_2_low,Vagina_3_mid,Vagina_4_upper,Vagina_5_top";
        // Deep-insertion displacement shape. History: Vagina_stretch -> womb_displace (whole-womb
        // up-shove) -> back to `womb_displace`, and the ROD-PUSH reauthor was approved in-game
        // and ordered it to REPLACE the old channel IN-PLACE (one channel, v1.0 index layout restored;
        // `womb_displace2` existed only during the 2026-07-22 A/B window). v2 model: mouth pinned
        // EXACTLY 0, canal stretches along the penis_target2 line to the rod tip, dome dented from
        // below (top+bottom arcs), neck centered on the rod. Reads stronger at the same weight than
        // the pre-v2 shape - retune Stretch* amplitude here on request, not in the mesh.
        public string StretchBlendShape { get; set; } = "womb_displace";
        public string StrengthBlendShape { get; set; } = "BP_Strength";
        public string DampeningBlendShape { get; set; } = "BP_Dampening";
        // Inert per-object toggle: weight > 50 => this womb IGNORES DynamicBoneColliders (it will NOT
        // react to a collider/toy pushed in) while STILL reacting to the BP penis. Absent or weight 0
        // => react to colliders as before (default behavior unchanged).
        public string IgnoreCollidersShape { get; set; } = "BP_IgnoreColliders";
        // Inert per-object INDEPENDENT RINGS control for the collider/toy path. 0 (or absent) = OFF: the normal rule,
        // where a ring opens when the object reaches it and stays open while the object is deeper - what a SHAFT does
        // to a canal. ANY value above 0 turns it on, and the value is then the BAND WIDTH: each ring follows only what
        // is level with it, so a small ball opens the ring it is in and the rings behind it close again, and the
        // weight says how far either side of the object that band reaches - 1 is the tightest grip, 100 about a ring
        // and a half. Unlike BP_IgnoreColliders (a plain switch at 50) this one is gradual, which is why OFF is 0 and
        // the whole travel means something.
        // The BP penis is unaffected either way: a shaft spans every ring behind its tip, so both rules agree.
        public string IndependentRingsShape { get; set; } = "BP_IndependentRings";
        // Inert per-object penetration SENSITIVITY (the engagement TRIGGER, NOT the opening amount, which is
        // BP_Strength): weight/50 -> 0 = off (manual), 50 (baked default) = 1.0 = current behavior, 100 = 2x.
        // Scales the canal-containment lateral gate AND the depth-to-engage; absent shape => 1.0 (unchanged).
        public string SensitivityBlendShape { get; set; } = "BP_Sensitivity";
        // Entrance direction-reaction: lean the mouth toward the incoming penis (read from BP tip dir).
        public string MoundForwardShape { get; set; } = "moundforward";
        public string MoundBackShape { get; set; } = "moundback";
        // Left/right lean (local X) - the lateral pair that lets the mouth follow a penis offset SIDEWAYS, matching
        // BP's own kokan-pull (BP translates the vagina along the full 3D penis direction). KK has no left/right
        // mound shape, so the mesh side authors this pair; no-op until they exist.
        public string MoundLeftShape { get; set; } = "moundleft";
        public string MoundRightShape { get; set; } = "moundright";

        // Normalized depths: the entrance ring (V1, very bottom) opens EARLY (with BP entry) -> low
        // Depth Start; the cervix (V5) opens deep -> Depth End. Rings open in order entrance->cervix.
        public float DepthStart { get; set; } = 0.10f;
        public float DepthEnd { get; set; } = 0.97f;

        // Full-open weights (0..100). All rings radial-target a uniform cylinder (the canal opens to a
        // smooth round TUBE):
        //  - V1 (EntranceWeight): the ENTRANCE ring at the very bottom - opens the entrance ROUND (X=Z)
        //    and early (low Depth Start). Lower it for a narrower introitus.
        //  - V2-V4 (RingWeight): the tube BODY (~45mm at ~90).
        //  - V5 (CervixWeight): the TOP/cervix - opens the top at deep insertion (lower = keep a neck).
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

        // womb_displace driver: ramps from StretchStart (normalized depth) to 1.0 reaching
        // StretchMax; past 1.0 (overshoot) keeps growing by StretchOvershoot per unit, capped at
        // StretchCap. (StretchMax/Start/Overshoot are cfg; StretchCap is FIXED at 100.)
        public float StretchMax { get; set; } = 12f;
        public float StretchStart { get; set; } = 0.5f;     // start displacing earlier (was 0.8)
        public float StretchOvershoot { get; set; } = 25f;
        public float StretchCap { get; set; } = 100f;   // FIXED at 100 - a >100 blendshape write latches/sticks in KKPE, so this was always 100; no longer a cfg (to displace more, strengthen the womb_displace SHAPE in the mesh)
        // Penis LENGTH now scales the depth INPUT (see lenRatio / RefDanLength), not the output: applied
        // to the output it sat behind a 2.0 clamp and then a 100 clamp, and contributed nothing in every
        // measured case. DirReactWeight = how far the mouth leans toward the incoming penis direction
        // (moundforward/back); 0 = off, "a little" ~25.
        // The BP base length that reads exactly as it did before this correction. Longer penises now
        // read proportionally deeper, shorter ones shallower. Not a tuning knob - it only chooses
        // which length is the no-change point.
        public float RefDanLength { get; set; } = 0.178f;
        public float DirReactWeight { get; set; } = 25f;

        // Animation timing (2026-06-17): rings OPEN slightly ahead of the penis (anticipation) and CLOSE
        // behind it (follow-through). OpenLead = how far ahead, in normalized depth, a ring reaches full.
        // CloseSmoothing = how fast the measured depth FALLS on withdrawal (lower than DepthSmoothing -> the
        // depth lingers, so ALL rings close LATER). The ENTRANCE ring (V1) eases open over a wider window
        // (EntranceOpenWidth, so it isn't abrupt - it opens in step with BP's vagina) and closes
        // EntranceCloseScale x slower than the others (its geometric move per weight unit is smaller).
        public float OpenLead { get; set; } = 0.06f;
        public float CloseSmoothing { get; set; } = 4f;
        public float EntranceOpenWidth { get; set; } = 0.30f;
        public float RingRampWidth { get; set; } = 0.24f;
        public float EntranceCloseScale { get; set; } = 2f;
        // Entry-feel tuning (2026-06-17): OpenTime rate-limits opening so rings EASE IN (not snap);
        // EntranceOpenScale makes V1 open that-many-times slower; MaxGirthScale caps girth->width scaling.
        public float OpenTime { get; set; } = 0.2f;
        public float EntranceOpenScale { get; set; } = 2f;
        public float MaxRingWeight { get; set; } = 100f;   // FIXED at 100 - a >100 blendshape write latches/sticks in KKPE; no longer a cfg (widen the mesh tube to open beyond this)
        public float MaxGirthScale { get; set; } = 2.5f;

        // Below this raw depth the penis is treated as not penetrating (BP parks lastDanDistance at
        // baseLen -> depth 0 when out), so everything rests closed.
        private const float EngageEps = 0.01f;
        // Lateral floor for "the penis tip threads the canal" (metres). Was the global config CfgPenisInCanal;
        // removed from F1 now that per-womb BP_Sensitivity scales the gate. Hard-set to the old default.
        private const float PenisInCanalWidth = 0.06f;

        private SkinnedMeshRenderer _smr;
        private int[] _ringIdx;
        private int _stretchIdx = -1;

        // MAIN-GAME press feed (0..1): how hard the pinned penis is being squished against the pin -
        // fed per-frame by BPInnerTargetPin, converted into womb displace so a deep thrust reads as
        // the womb yielding instead of the penis shrinking. Always 0 in Studio.
        public float ExternalPress;

        // MAIN-GAME intent tip (Studio-copy stretch): world position where the penis tip WOULD be if
        // nothing yielded (base + natural length along the aim direction) - BP's drive intent, immune
        // to the pin gluing the real tip. Fed per-frame by BPInnerTargetPin; projected on the canal
        // and run through the classic StretchTargetAt curve.
        public Vector3 ExternalIntentTip;
        public bool HasIntentTip;
        private float _hIntentDepth;
        public float HIntentDepth { get { return _hIntentDepth; } }
        // MAIN-GAME engagement grace: hold-open window + the drive values frozen through a dropout.
        private float _hEngHold, _hEngDepth, _hEngGirth = 1f;
        private float _hStrokeMax, _hOnset;   // MAIN GAME: auto-calibrated deepest stroke reach + the contact-point onset (mm)
        private float _girthLogNext;          // throttle for the GIRTH-DRIVE diagnostic
        private bool _bpHadGirthThisFrame;    // BP reported a real girthTip this frame -> collider latch stands down
        private float _tipMinMM;              // shallow-end tip depth (parked-overshoot baseline for the displace drive)
        private float _girthRiseMM;           // rise-only live girth (canal follows BP inflation UP at the limit, never down)
        // MAIN-GAME commanded stroke depth (mm past the entrance), fed by BPInnerTargetPin's sweep.
        // >0 replaces the collider-detected depth: BP's collider positions are pose-fragile (reverse
        // cowgirl slides them sideways with the hips), while this is the COMMANDED tip depth.
        public float ExternalStrokeMM;
        // compression BP's squish absorbed (raw L−d2−canal, mm; fed by the pin per frame) - the
        // press the womb actually feels; danEnd only shows the ~15-20% squish leak of it.
        public float ExternalCompressMM;
        // false while the auto-length FLOOR is holding the penis longer than the anatomical
        // fit - BP then parks the squished danEnd past the resting cervix PERMANENTLY (~15-20% leak
        // of the floor excess; small-char log: tipEst 65-68mm on a 59mm canal, every frame of the
        // loop), so the danEnd-based contact term reads fake reach. The compression feed already
        // measures the SAME quantity against the anatomical length - contact trusts only it then.
        public bool ExternalStrokeTrusted = true;

        // the pin's penis-fit state (false = PRIME/measuring, true = LOCKED). Default true so
        // Studio and the collider path (no pin) are unaffected; the H stretch reaction gates on it.
        public bool ExternalFitLocked = true;

        // Current womb_displace reaction weight (0..100) - the pin reads it to RIDE the displace:
        // when the womb yields upward, the pin point moves with it so the pinned penis follows the
        // yielding womb (otherwise the womb escapes a static pin and the penis reads as too short).
        public float CurrentStretchWeight { get { return _stretchReaction; } }
        // the dome's CURRENT displacement in world mm (the displace blendshape's travel at the
        // live weight). The H pin raises BP's squish stop by exactly this each frame, so the visual
        // tip advances WITH the yielding dome (glued contact) instead of parking at the resting
        // cervix while the dome ran ahead, which reads as the penis pushing air and the dome bouncing alone.
        public float CurrentDomeTravelMM { get { return Mathf.Clamp01(_stretchReaction * 0.01f) * 0.71f * _canalLen * 1000f; } }
        // Final (post-grace) engaged state - the pin/fill logic reads this to detect a real pull-out.
        public bool IsEngaged { get { return _dbgEngaged; } }
        // Auto-parity accessors: the calibrated canal entrance is the placement ground truth.
        public bool CanalReady { get { return _canalLen > 1e-4f; } }
        public bool CanalCalibrated { get { return _canalCalibrated; } }   // loop must wait for the averaged calibration
        public Vector3 CanalEntranceW { get { return _canalEntrance; } }
        public void RecalibrateCanal()
        {   // re-run the averaged calibration from scratch (the mesh was re-baked)
            _canalCalibrated = false;
            _calN = 0; _calSumEnt = Vector3.zero; _calSumTop = Vector3.zero; _calSumDia = 0f; _calDiaN = 0; _calPrevFrac = -1f;
        }
        // Canal geometry in WORLD space (re-projected each frame) - the pin reads it to place the tip
        // in CANAL space (full-range stroke), instead of following the animation's penis_target.
        public bool HasCanal { get { return _canalLen > 1e-4f; } }
        public Vector3 CanalEntranceWorld { get { return _canalEntrance; } }
        public Vector3 CanalAxisWorld { get { return _canalAxis; } }
        public float CanalLenWorld { get { return _canalLen; } }
        private int _strengthIdx = -1;
        private int _dampeningIdx = -1;
        private int _ignoreCollidersIdx = -1;
        private int _indepRingsIdx = -1;
        private int _sensIdx = -1;
        private float _sens = 1f;          // penetration sensitivity factor this frame (1.0 = default); scales the containment lateral gate
        private Transform _entranceBone;     // item's clo_cf_j_kokan = canal-entrance proxy (auto-BodyReveal proximity)

        private float[] _ringReaction;   // plugin contribution (pre-strength) per ring, for decay
        private float _stretchReaction;
        private int _moundFwdIdx = -1, _moundBackIdx = -1, _moundLeftIdx = -1, _moundRightIdx = -1;
        private float _moundFwdReact, _moundBackReact, _moundLeftReact, _moundRightReact;   // direction-reaction contribution, for decay

        private float _depth;            // smoothed normalized depth
        private bool _ready;
        // Start() has resolved the mesh. Until then _smr is null, so every blendshape and marker lookup (the hand
        // shapes, clo_canal_entry) answers "not on this womb" - anything that sets a womb up must wait for this.
        internal bool Ready { get { return _ready; } }
        private BPBridge.Reading _bp;
        // The same penis, sampled in onPreCull - after constraints, in the epoch the canal lives in.
        // Positions only: the depth fields of this copy are not used.
        private BPBridge.Reading _bpPost;
        private bool _bpPostValid;
        // Canal axis (entrance->top, world) for collider-depth, baked + cached on a 2s timer (EnsureCanal).
        private Vector3 _canalEntrance, _canalAxis = Vector3.up;
        private float _anchorBpLogNext;   // ANCHOR-VS-BP(rebind) throttle
        private float _engMissT, _engMissLogNext;   // ENGAGE-TRACE state
        private float _canalLen, _canalTimer;
        private float _vetoLogNext;        // throttle for the canal-veto notice
        private float _geomLogNext;        // throttle for the lone-womb notice
        private string _traceSrc = "none";  // which source set targetNorm this frame, for the trace
        private float _colSkipLogNext;      // throttle for the out-ranked-toy notice
        private float _colGeomLogNext;      // throttle for the toy-geometry notice
        private float _latLogNext;          // throttle for the lateral-gate comparison
        private float _epochLogNext;        // throttle for the onPreCull-vs-LateUpdate epoch check
        private float _tightLogNext;        // throttle for the tightened-containment notice
        private float _postMissLogNext;     // throttle for the missing post-constraint read error
        // How far BP's penis BASE sits PAST the canal entrance, mm; negative (or unmeasured) means
        // outside her, which is the normal case. Read by the squish pin, which needs it to keep its
        // stop point in the same origin as the canal length.
        private float _girthErrNext;        // throttle for the missing-girth error
        private float _travelErrNext;       // throttle for the unmeasured displace-travel error
        private float _girthTraceNext;      // throttle for the per-ring girth trace
        private bool  _bpLineRejectedOnce; // say once when BP's marker line is not the canal line
        private float _outsideLogNext;     // throttle for the outside-the-womb notice

        /// Ring weight for something of `radius` (world metres) to be gripped by THIS womb.
        /// RefGirth is an absolute reference, so it has to be taken in the womb's own scale before it
        /// can be compared with a world radius - see the call site for why. RADIAL scale (x/z), since
        /// this is a width; the canal's LENGTH already scales through _canalLen.
        /// The womb's own axial world scale. 1.0 for a normally attached womb on a character of any
        /// size - Studio compensates an item's local scale on attach - so it only moves when the item
        /// itself is scaled.
        private float CanalScaleY()
        {
            float s = (_canalBone != null) ? Mathf.Abs(_canalBone.lossyScale.y) : 1f;
            return (s < 1e-3f) ? 1f : s;
        }

        // ── REST WIDTH vs BLENDSHAPE TRAVEL ───────────────────────────────────────────────
        // Measured ONCE per womb, then remembered. Reference numbers: at 0.7 the rings ran 61/63/62/61/59
        // against 43/44/44/43/41 at scale 1 - a ratio of 1.42 against the 1/0.7 = 1.43 the compensation
        // aims for, so GirthForOpening is landing exactly where it was designed to.
        //
        // It is still not enough, and the arithmetic says why. A blendshape deforms in LOCAL space and
        // is rendered at world scale, so the ADDED opening is (w/100)*D*s: at 43 and scale 1 that is
        // 0.43*D, at 61 and 0.7 it is 0.61*D*0.7 = 0.427*D. Identical - the opening is already absolute.
        // But the total width is rest*s + opening, and the REST TUBE shrank too. At 0.7 the canal is
        // short by 0.3*R that nothing gives back, which is exactly the narrowness he sees.
        //
        // Holding the total width constant needs
        //      w(s) = w(1)/s + (100*R/D)*(1/s - 1)
        // and only the first term exists today. R/D is dimensionless - both scale with the womb - so it
        // is a mesh constant worth measuring once: bake the rings CLOSED for R, bake them at 100 for
        // R+D, keep the ratio. Zero per-frame cost afterwards.
        private float _restOverTravel = -1f;      // R/D, -1 = not measured yet
        private float _displaceTravelFrac = -1f;  // measured cervix travel per 100 stretch weight, as a fraction of the canal
        private bool  _rtTried;

        /// Canal diameter across the mid band of the CURRENT bake, in world metres.
        private float BandDiameter(Vector3[] bv, Vector3 axisL)
        {
            if (bv == null || _bandMid == null || _bandMid.Length < 5) return -1f;
            Vector3 u = Vector3.Cross(axisL, Mathf.Abs(axisL.z) < 0.9f ? Vector3.forward : Vector3.right).normalized;
            Vector3 w = Vector3.Cross(axisL, u);
            float uMin = 1e9f, uMax = -1e9f, wMin = 1e9f, wMax = -1e9f;
            for (int k = 0; k < _bandMid.Length; k++)
            {
                if (_bandMid[k] >= bv.Length) continue;
                Vector3 p = bv[_bandMid[k]];
                float pu = Vector3.Dot(p, u), pw = Vector3.Dot(p, w);
                if (pu < uMin) uMin = pu; if (pu > uMax) uMax = pu;
                if (pw < wMin) wMin = pw; if (pw > wMax) wMax = pw;
            }
            if (uMax < uMin) return -1f;
            return Mathf.Max(uMax - uMin, wMax - wMin);
        }

        private Vector3 BandCentroid(Vector3[] bv, int[] band)
        {
            if (bv == null || band == null || band.Length == 0) return Vector3.zero;
            Vector3 s = Vector3.zero; int n = 0;
            for (int k = 0; k < band.Length; k++) { if (band[k] < bv.Length) { s += bv[band[k]]; n++; } }
            return n > 0 ? s / n : Vector3.zero;
        }

        /// Two bakes, once: rings closed -> R, rings at 100 -> R+D. Restores every weight it touched.
        /// FAIL LOUD and stay disabled if either bake is unusable - a wrong ratio would mis-open the
        /// canal at every scale, which is worse than not compensating the rest width at all.
        private void MeasureRestAndTravel()
        {
            if (_rtTried || _smr == null || _ringIdx == null || _ringIdx.Length < 2) return;
            if (_canalLocal == null || _canalLocal.Length < 2 || _canalLen <= 1e-4f) return;
            _rtTried = true;
            float[] saved = null; float savedStretch = 0f;   // declared out here so the finally can put them back
            try
            {
                EnsureCanalBands();
                if (_probeMesh == null) _probeMesh = new Mesh();

                savedStretch = (_stretchIdx >= 0) ? _smr.GetBlendShapeWeight(_stretchIdx) : 0f;
                saved = new float[_ringIdx.Length];
                for (int i = 0; i < _ringIdx.Length; i++)
                    if (_ringIdx[i] >= 0) saved[i] = _smr.GetBlendShapeWeight(_ringIdx[i]);
                if (_stretchIdx >= 0) _smr.SetBlendShapeWeight(_stretchIdx, 0f);

                // while the probe is open, measure the DISPLACE travel too - how far the cervix
                // band actually moves at full stretch weight, as a fraction of the canal. The drive has
                // been using a hand-derived 0.5 for this, and its own comment asks for exactly this
                // measurement ("replace it with a MEASURED travel (baked cervix-band displacement per
                // applied weight) - the self-calibrating version"). Dimensionless, so once is enough.
                if (_stretchIdx >= 0 && _canalLen > 1e-4f)
                {
                    for (int i = 0; i < _ringIdx.Length; i++) if (_ringIdx[i] >= 0) _smr.SetBlendShapeWeight(_ringIdx[i], 0f);
                    _smr.SetBlendShapeWeight(_stretchIdx, 0f);
                    _smr.BakeMesh(_probeMesh);
                    Vector3 cerv0 = BandCentroid(_probeMesh.vertices, _bandTop);
                    _smr.SetBlendShapeWeight(_stretchIdx, 100f);
                    _smr.BakeMesh(_probeMesh);
                    Vector3 cerv1 = BandCentroid(_probeMesh.vertices, _bandTop);
                    _smr.SetBlendShapeWeight(_stretchIdx, 0f);
                    float moved = (cerv1 - cerv0).magnitude;
                    if (moved > 1e-5f)
                    {
                        // MEASURED at two scales, both games. The bake's `moved` carries the item
                        // scale (55.6mm at scale 1.0, 43.6mm at 0.8 = 0.784x) but _canalLocalLen is the
                        // UNSCALED rest length (77.3mm at either), so dividing by it reported 0.564 on a
                        // 0.8 womb where the true fraction is 43.6/61.8 = 0.706 - the same 0.71 the
                        // scale-1.0 womb measures. A fraction must have both terms in one space, so
                        // divide by the WORLD canal. Identical at scale 1.0, where everything was tuned.
                        _displaceTravelFrac = moved / (_canalLen > 1e-5f ? _canalLen : _canalLocalLen);
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: '" + name + "' displace travel measured: cervix moves "
                            + (moved * 1000f).ToString("F1") + "mm at weight 100 = " + _displaceTravelFrac.ToString("F3")
                            + " of the canal (the drive has been assuming 0.500).");
                    }
                    else LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: '" + name + "' displace travel probe read 0 - keeping the assumed 0.500.");
                }

                // The MID band belongs to the width measurement below and to nothing else. It used to sit
                // at the top of this method and RETURN, which took the displace-travel measurement down
                // with it: a womb with a thin mid band lost _displaceTravelFrac permanently (_rtTried
                // latches before either bake), even though travel reads the TOP band and never touches
                // _bandMid at all. The two measurements are independent and now fail independently -
                // travel is taken FIRST, above, so nothing here can suppress it.
                if (_bandMid == null || _bandMid.Length < 5)
                { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: '" + name + "' canal rest/open width skipped - no mid band."); return; }

                Vector3 axisL = (_canalLocal[_canalLocal.Length - 1] - _canalLocal[0]).normalized;

                for (int i = 0; i < _ringIdx.Length; i++) if (_ringIdx[i] >= 0) _smr.SetBlendShapeWeight(_ringIdx[i], 0f);
                _smr.BakeMesh(_probeMesh);
                float rest = BandDiameter(_probeMesh.vertices, axisL);

                for (int i = 0; i < _ringIdx.Length; i++) if (_ringIdx[i] >= 0) _smr.SetBlendShapeWeight(_ringIdx[i], 100f);
                _smr.BakeMesh(_probeMesh);
                float open = BandDiameter(_probeMesh.vertices, axisL);

                float travel = open - rest;
                if (rest <= 0f || travel <= 1e-5f)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + name + "' rest/travel probe FAILED (rest="
                        + (rest * 1000f).ToString("F1") + "mm open=" + (open * 1000f).ToString("F1")
                        + "mm) - the scaled-womb rest-width make-up stays OFF rather than guess a ratio.");
                    return;
                }
                _restOverTravel = rest / travel;
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: '" + name + "' canal rest=" + (rest * 1000f).ToString("F1")
                    + "mm  full-open travel=" + (travel * 1000f).ToString("F1") + "mm  R/D=" + _restOverTravel.ToString("F3")
                    + "  -> make-up at 0.7 = +" + (100f * _restOverTravel * (1f / 0.7f - 1f)).ToString("F0") + " weight");
            }
            catch (Exception e)
            { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: rest/travel probe threw: " + e.Message); }
            finally
            {
                // THE restore, and the only one. The probe drives every ring and womb_displace through
                // rest, full-open and full-stretch; the scene has to come back exactly as it was however
                // this method leaves - the FAILED branch returns from the middle of it, and an exception
                // used to leave a hand-posed canal zeroed with nothing left to put it back. Two restores
                // inside the try did this on the two paths that happened to be thought of; this does it
                // on all of them.
                if (saved != null && _smr != null && _ringIdx != null)
                {
                    for (int i = 0; i < _ringIdx.Length && i < saved.Length; i++)
                        if (_ringIdx[i] >= 0) _smr.SetBlendShapeWeight(_ringIdx[i], saved[i]);
                    if (_stretchIdx >= 0) _smr.SetBlendShapeWeight(_stretchIdx, savedStretch);
                }
            }
        }

        /// Extra ring weight that gives back the rest width a scaled-DOWN womb loses. 0 at scale 1.
        private float RestWidthMakeup()
        {
            if (_restOverTravel <= 0f) return 0f;
            float s = CanalScaleY();
            return 100f * _restOverTravel * (1f / s - 1f);
        }

        // ── penis_target HOLDS ITS PLACE WHEN THE WOMB IS SCALED ──────────────────────────
        // Measured, settled, same penis, only the item scale changed:
        //     0.7   mouth (-1.007, 0.705, 2.098)   tip (-1.009, 0.720, 2.065)   tipAboveMouth 34.1mm
        //     1.0   mouth (-1.007, 0.702, 2.091)   tip (-1.008, 0.734, 2.044)   tipAboveMouth 48.3mm
        // The MOUTH held to 3mm - the item is pinned to her a_n_kokan, so scaling does not walk it off
        // the orifice. The TIP moved 14mm, because penis_target is a bone INSIDE the womb: its world
        // offset from the item origin is localOffset * s, so shrinking the womb pulls the aim - and the
        // penis with it - back out. Depth then rose only 1.067x instead of the 1/0.7 = 1.43x that a
        // shorter tube over an unmoved penis should give, and every downstream term was working
        // correctly on a depth that had already been robbed.
        //
        // The penis endpoint stays relative to the character and ignores the womb being smaller, so it
        // reaches the same distance and the womb must stretch more. The target holds its WORLD
        // offset: local = authored * scaleWhenAuthored / scaleNow.
        //
        // The reference is whatever the target's local position was when it last CHANGED, not a
        // value baked at spawn - penis_target is re-authored by hand, and a re-author must win. Any
        // local move not written from here is taken as the new authored value, together with the scale it was
        // made at. Only a write from here is corrected.
        private Transform _penisTarget;
        private Vector3 _ptAuthored, _ptLastWritten;
        private float _ptAuthoredScale = 1f;
        private bool _ptHave;

        private void HoldPenisTargetAgainstScale()
        {
            if (!MainGameWomb.IsStudio) return;   // Free-H rebinds the womb onto her bones; no item scale to fight
            if (_penisTarget == null)
            {
                foreach (var tr in GetComponentsInChildren<Transform>(true))
                    if (tr != null && tr.name == "penis_target") { _penisTarget = tr; break; }
                if (_penisTarget == null) return;
                _ptHave = false;
            }
            // CanalScaleY() answers 1f when the canal bone is not resolved, and latching that guess as
            // the authored scale moves this bone by the ratio once the real scale appears - 43% on a 0.7
            // womb. penis_target is authored by hand and is what the scene's NodesConstraint aims
            // k_f_dan_end at, so a guess here mutates the scene on a plain load. Wait for it.
            if (_canalBone == null) return;
            float s = CanalScaleY();
            Vector3 cur = _penisTarget.localPosition;
            if (!_ptHave || (cur - _ptLastWritten).sqrMagnitude > 1e-10f)
            {
                // first sight, or it was moved by hand - adopt this as the authored placement AT THIS SCALE
                _ptAuthored = cur; _ptAuthoredScale = s; _ptHave = true;
                _ptLastWritten = cur;
                return;
            }
            if (s < 1e-3f) return;
            Vector3 want = _ptAuthored * (_ptAuthoredScale / s);
            if ((want - cur).sqrMagnitude < 1e-10f) return;   // already there - never touch the transform for nothing
            _penisTarget.localPosition = want;
            _ptLastWritten = want;
        }

        private float GirthForOpening(float radius)
        {
            if (RefGirth <= 1e-5f) return 1f;
            float s = 1f;
            if (_canalBone != null)
            {
                Vector3 ls = _canalBone.lossyScale;
                s = 0.5f * (Mathf.Abs(ls.x) + Mathf.Abs(ls.z));
            }
            if (s < 1e-3f) s = 1f;   // a degenerate scale would divide the opening to infinity
            return radius / (RefGirth * s);
        }

        private float _scaleTraceNext;   // 1/s throttle for the scale trace
        private float _colLateral = -1f;   // when a collider is in the canal, its centre's lateral offset from the axis (m); -1 = none (debug)
        private float _bpLateral = -1f;    // BP penis tip's lateral offset from the canal axis (m); -1 = no pose/no bake (debug)
        private float _bpClosest = -1f;    // closest any shaft bone came to the canal axis when NOT contained (debug)
        private float[] _ringDepths;       // each ring's normalized canal depth (DepthStart..DepthEnd), for the collider per-ring scan
        private float[] _colRingRadius;    // per-ring MAX collider world radius (thickest collider reaching that ring) - multi-collider taper
        private bool _colPerRing;          // true when the collider path drove with per-ring radii available
        private bool _toyDrives, _engToy;  // a Studio toy drives the rings this frame (carried through the engage grace)
        private bool _engPerRing;          // _colPerRing as of the last engaged frame - carried through the grace with it
        // TOY RING TIMING (Studio). Two corrections for a toy, whose depth is a plain canal fraction. A penis (BP depth)
        // and Free-H keep their timing.
        // - ToyRingDelay: womb 7.5.3 re-cut the rings so each one fades into its neighbours, which moved the opening of
        // rings 1-3 a little up the canal. Each ring reaches full 2/3/2/1/0mm later, stored as fractions of the
        // 77.3mm canal so it scales with the womb.
        // - ToyStretch: past ToyStretchKnee (25mm) the rings follow the toy 1.3x slower. On the even ring timing the
        // canal opened further ahead the deeper the toy went: the 2-10mm ahead of it was ~40% open near the
        // entrance but 66/98/97% at 60/75/80mm. Stretched, it is 42/34/59% there and the top ring is about half open
        // when the toy reaches the cervix. Measured on the mesh; the round-tip clip stays at most 7% (6% before).
        private static readonly float[] ToyRingDelay = { 0.0259f, 0.0388f, 0.0259f, 0.0129f, 0f };
        private const float ToyStretchKnee = 0.3234f;   // 25mm of the 77.3mm canal
        private const float ToyStretch = 1.3f;
        private bool  _bpVaginaMain;   // engaged via BP-depth on the paired vagina without bone-containment (the bones sit below the cervix).
        private float _depthFast;          // the displace's own depth - same rise, faster fall
        // How much faster the DISPLACE lets go than the rings do. 3 puts its fall time constant at ~0.08s
        // against the rings' 0.25s: the womb springs back as the penis withdraws while the canal still
        // closes behind it. Hard-wired rather than another config knob.
        private const float StretchCloseBoost = 10f;   // TEST VALUE - tuning the fast-shrink option
        // How much faster the DISPLACE takes the depth UP than the rings do. Deliberately not
        // instant: BP's depth carries single-frame jumps that an unfiltered rise would snap on.
        private const float StretchRiseBoost = 6f;   // TEST VALUE - chasing the stroke peak
        // How much faster the RINGS take depth UP than the shipped DepthSmoothing. Same value as the
        // displace copy, and for the same reason: at 12/s they trailed a slow stroke by 11mm and a fast
        // one by 27mm, and passed only 39% of a 4.5Hz stroke.
        private const float RingRiseBoost = 6f;
        // Lead time for the displace depth: aim where the tip will be one time constant from now.
        private const float StretchLeadTime = 0.014f;   // ~1/(DepthSmoothing*StretchRiseBoost)
        private const float StretchLeadMax  = 0.17f;    // never predict further ahead than this
        // Studio engage grace: how long the last engaged depth is held through a gate flicker.
        private const float EngageGrace = 0.20f;
        // The lone-womb geometry path may not touch a womb BP has driven recently. A stroke trough or a
        // gate flicker must never hand the womb over mid-animation, so BP has to have been quiet for
        // this long first - far longer than the grace above, which only bridges a flicker.
        private const float GeomQuiet = 2.0f;
        private float _bpDroveUntil = float.NegativeInfinity;
        // Depth the BP path reports when the tip is level with the canal mouth. The whole curve is
        // calibrated in BP units, so the geometry path converts into them rather than using canal
        // fractions of its own - otherwise one StretchStart would mean two different places.
        private const float GeomMouthOffset = 0.01046f;   // metres, BP zero -> canal mouth
        private float _engHold, _engDepth, _engGirth = 1f;
        private bool  _traceGrace;
        private float _cleanPrev = float.NaN;                          // for the velocity estimate
        private float _traceClean, _traceLead, _traceRingLead;         // trace only
        private bool  _wasEngaged;         // engage-edge detector for the gate log

        // STRETCH TRACE state (diagnostic only). The measured depth before filtering, kept so the
        // trace can show input and output of every stage on one line.
        private float _traceTgtDepth;
        private float _traceSmFast;        // the rate _depthFast actually used this frame
        private float _traceLastTgt = float.NaN, _traceLastSt = float.NaN;
        private int _stretchTraceLeft = 3000;  // budget is spent on DEEP frames only (see below), so a
                                               // shallow warm-up cannot exhaust it before the event
        private float _traceLenRatio = 1f; // baseLen / RefDanLength actually applied to the depth
        private bool  _bpGeomDepth;        // depth came from the tip's position in the canal (BP reported none)
        private bool  _tipDetached;        // tip marker k_f_dan_end pulled beyond the detach distance -> treated as withdrawn (debug)
        private float _tipDist = -1f;      // distance from the womb entrance to k_f_dan_end (m); -1 = no tip (debug)
        private bool  _entryDetached;      // entry marker k_f_dan_entry swung off the canal axis (penis base withdrawn) -> withdrawn (debug)
        private float _entryLat = -1f;     // lateral distance from the canal axis to k_f_dan_entry (m); -1 = no entry (debug)
        // Vagina pairing: is THIS womb seated on a character's cf_J_Vagina_root? Cached on a 2s timer (EnsureVaginaPairing).
        // The MAIN penis signal: when BP penetrates the vagina this overlay sits on, react - bone geometry can't be trusted
        // (length-squish + overlay seating bunch the dan bones far below the cervix while the mesh/BP sit deep).
        private bool  _vaginaPaired;
        private bool  _vaginaFarLat;       // diag: vagina-paired but the penis is laterally beyond THIS womb's gate (it's in another/adjacent womb)
        private float _vaginaDist = -1f;   // nearest cf_J_Vagina_root distance to the entrance (m); -1 = none (debug)
        private float _vaginaTimer;
        // VAGINA SEAT (Studio): the paired penis enters through HER cf_J_Vagina_root, so BetterPenetration's
        // depth zero is her vagina and not this womb. See ResolveVaginaSeat.
        private Transform _seatVagina;     // her cf_J_Vagina_root driving the paired k_f_dan_entry; null = not vagina-seated
        private Transform _seatItem;       // this womb's Studio object; its placement counts while it sits directly in her a_n_kokan slot
        private float _seatLogNext;        // SEAT trace throttle (Diagnostic log)
        // Hand-set blendshapes against the shipped shape (MeasureCanalShape).
        private float _shapeLenRatio = 1f; // canal length now / canal length as shipped
        private float _shapeMouthFrac;     // mouth movement along the canal, in canal lengths (+ = deeper)
        private float[] _shapeSeen;        // weights at the last measurement
        private float[] _shapeRef;         // shipped weight per channel
        private float _shapeNext;          // re-measure throttle while a slider is dragged
        private bool  _shapeFailLogged;
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

        // Median XZ center of the tube cross-section at local height yb, restricted to a column of radius `rad`
        // around (cx,cz) so ovaries/womb body don't skew it. `spread` = median radial distance of the band points
        // from the found centre (~the tube radius at this height); it jumps when the canal opens into the uterus,
        // which the canal walk uses to detect the cervix. Returns false if too few points.
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

        private Vector3[] _canalBandW;     // canal endpoints (entrance, cervix) in WORLD - re-projected each frame
        private Vector3[] _canalLocal;     // canal endpoints (entrance, cervix) in LOCAL/rest space (constant; baked once)
        private float _canalLocalLen;      // pure REST canal length (sharedMesh space) - live scale re-applied per frame
        private float _canalRestLen;       // world canal length BEFORE the H base-stretch elongation (for the base displace weight)
        private float _baseStretchPctEff;  // config base-stretch % + the hard-wired 6mm down-extension %
        private Transform _ptBone;         // the womb's aim bone - penis_target2 (canal-frame) or the old penis_target
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
            // Search THIS WOMB's own hierarchy only: searching transform.root grabbed the FIRST
            // penis_target in the whole scene - with several womb items in Studio, every womb's ORANGE
            // marker converged on the same girl's bone. Diagnostic-only bug; the real
            // aiming (Studio NC pairs / the H pin) was always per-womb.
            // penis_target2 ONLY (zipmod+DLL ship together - no legacy old-bone path). Null lets
            // the caller fail LOUDLY instead of aiming at a stale bone.
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == "penis_target2") return t;   // the bone the pin actually targets
            // under REBIND the canal anchor (+PT2 child) is reparented under HER cf_j_kokan -
            // outside this womb's subtree - so search the wearer too. PT2's name is unique on her.
            if (transform.parent != null)
                foreach (var t in transform.parent.GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == "penis_target2") return t;
            return null;
        }
        // Penis BASE world position, fed by BPInnerTargetPin - the STABLE end of BP's straight penis line
        // (base->entrance = the canal direction; the deepest collider is stroke-jittery and unusable).
        public Vector3 ExternalPenisBase;
        public bool HasPenisBase;
        // BP's reference-target (the VAGINA bone BP aims the penis at). base→ref = BP's EXACT penis line.
        // Set once this womb's wearer is seen carrying cf_J_Vagina bones. Survives a Studio character
        // replacement (the womb item is not touched by it), so the replacement handler knows whether the
        // scene was built on a BP body and may restore one on a card that lacks it.
        internal bool WearerHadBPVagina;
        // The wearer's body-uncensor GUID, captured while she still had her vagina bones. A Studio
        // character replacement can drop the incoming card back to a body without them; this is what
        // gets re-applied so the scene keeps the body it was built on.
        internal string WearerBodyGuid;
        // Same idea for the MALE in this womb: his penis/balls uncensor, captured while he still has the
        // BP dan bones. It is what a replacement card needs to be penetrable at all.
        internal string PenetratorPenisGuid;
        internal string PenetratorBallsGuid;
        public Vector3 ExternalPenisRef;
        public bool HasPenisRef;
        // BP's REAL penis endpoint (m_danPoints.danEnd) - where the tip actually is after ConstrainDan.
        // base→end = the true penis vector; end projected on the canal = the true reaction depth.
        public Vector3 ExternalPenisEnd;
        public bool HasPenisEnd;
        // The GAME's own penetration state (HFlag motion name, BP's rule) - authoritative gate: a
        // pulled-out/idle penis can never engage the womb regardless of where its bones idle.
        public bool ExternalPenetrated;
        public bool HasPenetratedFlag;
        private Transform _canalBone;      // M's clo_canal_entry marker bone (if the mesh has it) -> exact, scale/rotation-native
        private bool _canalCalWarned;   // one-shot: CANAL-CAL saw an impossible error and refused to act
        private float _bakedCanalLen;   // true world canal length measured from the bake at calibration
        private const float RefCanalLen = 0.085f;   // reference womb canal (default female) for width scale-compensation
        private float _lastGirthScale = 1f;   // girthScale used last frame (for the opening-efficiency measure)
        private float _openEfficiency;        // measured mm canal-diameter per girthScale unit (the mesh's true opening rate)
        // expansion is precalculated so the womb spawns at the correct stretch. The efficiency is a
        // mesh+scale property, stable across respawns of the same womb - carry the last measured value
        // so a respawn (auto-parity iteration, mode toggle, pose change) opens at the CORRECT width
        // immediately instead of running the over-predicting RefGirth model until the phase-locked
        // calibration (up to 2 idle loops) re-seeds it ("spawns at max expansion, then slowly reduces").
        // The calibration still overwrites it with the fresh averaged measure when it completes.
        // Carried across spawns SCALE-FREE: mm of canal diameter per girthScale unit at UNIT world
        // scale. _openEfficiency itself is per-CHARACTER (it multiplies by RebindS and her kokan lossy
        // scale), so carrying the absolute value handed the next womb a number from the last one's body.
        // MeasureOpenCanal only accepts a fresh sample within 0.6x..1.7x of the current value, so a carry
        // from a character more than ~1.7x different in scale sat permanently outside the band: nothing
        // was ever accepted and the value never converged, for the rest of the session.
        private static float s_carryResp;
        // _effLive/_wasPenOpen removed - openEff is scale-law SEEDED (see the drive) + band-rejected,
        // so there is no first-measure "snap" state to track.
        // Per-male girth latch + stroke-velocity tracker (dome release adaptation)
        private static string s_diaMale; private static float s_diaMM;
        private static string s_diaLoggedMale; private static float s_diaLoggedMM;   // last value the LATCHED line reported
        private float[] _diaBuf; private int _diaN;
        // SEPARATE BP-owned girth latch. The shared latch above got polluted by the thin dan
        // collider (the FREE-H collider path latched 22.6mm before penetration, and BP's re-latch lost
        // the thrash) - so canal opened for 22.6mm while BP's real girth was ~38mm and the shaft poked
        // through. This latch is fed ONLY by BP's girthTip; the drive prefers it, and colliders can
        // never touch it. Median-15, keyed per male, resets when another male appears.
        private static string s_bpMale; private static float s_bpDiaMM;
        private float[] _bpDiaBuf; private int _bpDiaN;
        private bool _bpRelatch;   // a pose change asked for a fresh median; the old width stays live meanwhile
        // name-keyed resets never fire on a CARD SWAP (dan names collide across male cards; the
        // collider-latch key is a constant string by design), so a swapped-in male inherited the
        // previous male's latch (measured: bpLatch 44.2mm vs live tip 24.8mm on the small male = canal
        // 2x too wide). Natural dan length is the stable per-male identity - reset EVERYTHING girth
        // (both latches, both median buffers, the rise) when it changes.
        private static float s_latchNatural;
        // A pose change re-poses the penis and BP re-squishes it, so a girth latched under the old pose
        // sizes the canal wrong for the new one - latched 49.3mm while BP's live tip read 42.1mm. Both
        // penis" - latched 49.3mm while BP's live tip read 42.1mm). Both latches are one-shot for the
        // session, so nothing re-measured. Re-measure on a pose change, exactly as the LENGTH fit
        // already does ("penis fit will re-measure one stroke, then lock").
        private int _girthPoseVer = -1;
        private void ResetGirthOnPoseChange()
        {
            if (MainGameWomb.IsStudio) return;
            int pv = MainGameWomb.PoseVersion;
            if (_girthPoseVer == pv) return;
            // Wait for the LENGTH fit to settle before discarding the old girth: right after a pose
            // change BP is still re-squishing, and re-measuring there latched the SQUISHED girth
            // (37.5mm against a settled 42.1mm tip - the canal then reads too tight, the mirror of
            // the too-wide case). ExternalFitLocked is the same "settled on this pose" signal the
            // length fit uses, so the girth re-measures on a stable penis.
            if (!ExternalFitLocked) return;
            bool had = _girthPoseVer >= 0 && (s_bpDiaMM > 0f || s_diaMM > 0f);
            _girthPoseVer = pv;
            if (!had) return;
            // Restart the medians but KEEP the current width live: blanking it drove the canal from
            // 0mm for the ~15 frames the new median takes ("bpLatch=0.0mm" in the log), so the canal
            // collapsed and re-opened on every pose change. The old width is the best estimate until
            // the new one is ready.
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
            ResetGirthOnNewMale();   //
            ResetGirthOnPoseChange();
            if (diaMM <= 1e-3f) return;
            if (key == null) key = "BP";
            if (key != s_bpMale) { s_bpMale = key; s_bpDiaMM = 0f; _bpDiaN = 0; _girthRiseMM = 0f; }   // new male -> reset the rise too
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

        // shared girth latch - BOTH read paths feed it. Studio latches pre-penetration from the
        // BP marker read; FREE-H has no marker pairing (its _bp is synthesized from the cm_J_dan
        // collider scan), so that latch NEVER RAN there and the canal rode the LIVE per-collider
        // radius - tip collider first, shaft later - visibly "settling" over the first strokes
        // as reported. Now H latches from the first 15 collider reads (~0.25s, once per male).
        private void LatchGirth(float diaMM, string key)
        {
            ResetGirthOnNewMale();   //
            ResetGirthOnPoseChange();
            if (diaMM <= 1e-3f) return;
            if (key == null) key = "H-collider";
            // what the LATCHED line last reported, so a re-latch to the same number stays quiet
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
        private const float WidthMargin = 1.15f;   // 15% room (was 10%; grounding keeps it poke-free)

        private bool TryCanal(out Vector3 entranceW, out Vector3 topW)
        {
            entranceW = topW = Vector3.zero;
            try
            {
                // TRUE REST structure: the SHARED MESH vertices - no BakeMesh. BakeMesh captures the
                // LIVE bone pose (only blendshapes can be zeroed), and in the MAIN GAME the bone mirror
                // poses the womb's internal bones to the girl's animation - a toggle mid-pose baked a
                // SQUEEZED canal (logs: canalLen 71/70/55/33mm across sessions = whatever pose she
                // held at toggle time) and EnsureCanal never re-bakes -> shallow strokes + every ring
                // open + stretch pegged. sharedMesh is pose-independent and deterministic; on a rigid
                // Studio rig it equals the old bake (scale is re-applied via the canal bone / renderer
                // transform in RefreshCanalWorld). Canal runs along local +Y (mesh construction).
                Vector3[] v = _smr.sharedMesh != null ? _smr.sharedMesh.vertices : null;
                if (v == null) return false;
                float minY = float.MaxValue;                                // (orientation-robust: works for a rotated / anus-oriented womb)
                for (int i = 0; i < v.Length; i++) if (v[i].y < minY) minY = v[i].y;
                float seedx, seedz, seedSpread; int sc;
                RingCenter(v, minY + 0.012f, 0.014f, 0f, 0f, 1f, out seedx, out seedz, out sc, out seedSpread);
                var centers = new System.Collections.Generic.List<Vector3>();
                float cx = seedx, cz = seedz, entranceSpread = -1f, topSpread = 0f;
                // ADAPTIVE climb from the opening up the tube (was a fixed 8.5cm centred sub-segment). Below the first
                // hit, SKIP empty bands (rest-pose slit) to find the mouth; once on the canal STOP at the CERVIX -
                // where the section opens into the uterus the ring `spread` grows well past the canal radius.
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
                // The canal is a straight axis: cache ONLY the two endpoints (entrance + cervix) in LOCAL space. The
                // walk's intermediate centres were only used to FIND the cervix (the spread-stop) + the old debug trail;
                // entrance/axis/length need just these two. RefreshCanalWorld re-projects them through the live transform.
                _canalLocal = new Vector3[] { centers[0], centers[centers.Count - 1] };
                _canalLocalLen = Vector3.Distance(centers[0], centers[centers.Count - 1]);   // pure REST length; live scale re-applied per frame
                _canalBandW = new Vector3[] { _smr.transform.TransformPoint(_canalLocal[0]), _smr.transform.TransformPoint(_canalLocal[1]) };
                return true;
            }
            catch { return false; }
        }

        // Bake the canal SHAPE ONCE. The local rest-mesh structure is CONSTANT - the womb's reactions are blendshapes
        // (zeroed in the bake), and moving/rotating/scaling the womb is a TRANSFORM change, re-projected live by
        // RefreshCanalWorld. So there is nothing to re-detect: just retry (slowly) until the FIRST valid bake lands
        // (the womb may not be fully initialised on frame 1), then `_canalLocal` is set and this short-circuits forever.
        // Pose at bake time no longer matters (RefreshCanalWorld places the cached LOCAL shape through the live, post-IK
        // transform every frame). Re-bakes only if the womb item is reloaded -> the component re-inits, _canalLocal null.
        private void EnsureCanal(bool drive = false)
        {
            // Gated on the bake's OWN product, not on _canalLen - the world length belongs to the bone
            // (RefreshCanalWorld) and is rewritten every frame, so it cannot say whether a bake has happened.
            if (_canalLocalLen > 1e-4f && _canalLocal != null && _canalLocal.Length >= 2) return;   // baked once -> never again
            _canalTimer -= Time.deltaTime;
            if (_canalTimer > 0f) return;
            // FAST acquisition - place it in one try rather than waiting. This cadence only runs
            // BEFORE the first valid bake lands (line above short-circuits forever after). At 1s a frame-1
            // bake miss cost a full second before the canal - and therefore the analytic placement nudge -
            // could be computed, which showed as the womb jumping ~1s after spawn. 0.05s retries
            // the bake every few frames instead, so the canal (and the one-shot placement) lands within
            // ~0.1s = imperceptible. The plausible-length gate below still rejects any premature bake.
            _canalTimer = 0.05f;
            Vector3 entW, topW;
            if (TryCanal(out entW, out topW))
            {
                Vector3 ax = topW - entW; float l = ax.magnitude;
                // Only trust a plausible canal length (~77mm at unit scale; allow scaling). A degenerate/premature
                // bake (tiny len) would make cdepth = along/len blow up and read every collider as fully inserted.
                if (l >= 0.02f && l <= 0.5f)
                {
                    // The BONE owns entrance/axis/length - see RefreshCanalWorld, which sets all three
                    // every frame from the authored clo_canal_entry marker. The bake does NOT write them:
                    // its product is the cached LOCAL shape stored above. Writing them here made a second
                    // source that normally gets overwritten but survives whenever RefreshCanalWorld bails
                    // out, leaving stale geometry behind with nothing logged. One source, no override.
                    if (_canalBone == null) _canalBone = FindCanalBone();   // search once
                }
                else { _canalLocal = null; _canalLocalLen = 0f; }   // implausible bake -> discard it and retry
            }
        }

        // POST-IK feed: like LiquidShaderWobbleEffect, sample from Camera.onPreCull (strictly after all LateUpdates /
        // IK / NodesConstraints, the exact pose the GPU is about to draw) so the canal bake matches the rendered womb.
        private int _lastCanalFrame = -1;
        private Camera.CameraCallback _canalPreCull;
        private void OnEnable()
        {
            _canalPreCull = OnPreCullCanal;
            Camera.onPreCull += _canalPreCull;
            // A CloXray womb is now live. Bump the gate so the BP-interop hooks are allowed to act, install them
            // (idempotent; no-op until BP's assembly loads - also retried from the scene-load coroutine), and refresh
            // the shared vagina-root list (this womb's character may have just spawned).
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
            long t0 = Perf.Start();
            try { OnPreCullCanalBody(cam); }
            finally { Perf.Stop(Perf.WombPreCull, t0); }
        }

        private void OnPreCullCanalBody(Camera cam)
        {
            if (!_ready) return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;     // master toggle OFF -> freeze (no canal re-bake / pairing / markers)
            if (Time.frameCount == _lastCanalFrame) return;   // first camera of the frame only
            _lastCanalFrame = Time.frameCount;
            // WHICH camera won the first-of-frame latch, and was the womb visible to it. The canal
            // calibration (which MOVES and ROTATES the canal bone) runs below, so if this varies with the
            // view, placement becomes camera-dependent - the standing epoch-saga warning about latching
            // "first camera of the frame". Captured for the spawn report.
            _lastCanalCam = cam != null ? cam.name : "-";
            _lastCanalCamVisible = _smr != null && _smr.isVisible;
            EnsureCanal(true);          // authoritative post-IK re-bake (timed) -> refreshes the LOCAL/rest canal
            // the womb jumped ~1s after spawn, so run the analytic placement predictor the INSTANT
            // the canal is acquired - it needs only the rest-mesh entrance band (built here) + her live
            // bones, NOT the phase-locked calibration below (which waits for her idle loop to cross a fixed
            // phase x2 = the ~1-3s the placement was blocked on). The nudge is stance-stable (it's the
            // structural mirror-vs-rebind DIFFERENCE, not an absolute sample - logs show an identical
            // (0,13.2,1.7) whenever it runs), so predicting early loses nothing. PredictParity self-guards
            // (_parityPredicted); the old call inside the calibration block becomes a no-op backstop.
            if (!_parityPredicted && MainGameWomb.CurrentlyRebound
                && _canalLen > 1e-4f && _canalLocal != null && _canalLocal.Length >= 2)
            {
                EnsureCanalBands();     // build _bandEnt from the rest mesh now (idempotent; no-op later)
                PredictParity();
            }
            CalibrateCanalToSkinnedMesh();   // once, at rest - put the canal bone ON the true skinned canal
            RefreshCanalWorld();        // EVERY frame: re-project the cached LOCAL canal through the live transform
            // EPOCH CHECK. _canalEntrance is captured HERE, in onPreCull, which runs AFTER LateUpdate and
            // after NodesConstraints have pulled a pinned womb. The depth code in LateUpdate reads the bone
            // live, one epoch earlier. If this prints ~0 while the LateUpdate reading prints tens of mm,
            // the two are simply different instants - and the same split applies to the penis tip the depth
            // code compares against, which would be a real coherence fault, not a diagnostic one.
            if (LiquidWobbleMPBPlugin.CfgDebugLog && Time.unscaledTime >= _epochLogNext && _canalBone != null)
            {
                _epochLogNext = Time.unscaledTime + 1f;
                // The PENIS in this epoch, beside the canal. LateUpdate reads the nominal, pre-constraint
                // bones - an origin edit on an NC-pinned character walked basePos 7 metres while nothing
                // moved on screen. If this reads stable across the same edit, the penis IS correct here and
                // moving the geometry to onPreCull is sufficient; if it walks too, it is not.
                Vector3 danNow = BPBridge.GetDanTipWorld(_bp.srcName);
                LiquidWobbleMPBPlugin._logger?.LogInfo("EPOCH '" + WombTag() + "' onPreCull: canalBone="
                    + _canalBone.position.ToString("F3") + " entrance=" + _canalEntrance.ToString("F3")
                    + " diff=" + ((_canalBone.position - _canalEntrance).magnitude * 1000f).ToString("F1") + "mm"
                    + " || male='" + (_bp.srcName ?? "?") + "' danTipNOW=" + danNow.ToString("F3")
                    + " danTipLateUpdate=" + _bp.tipPos.ToString("F3")
                    + " epochGap=" + ((danNow - _bp.tipPos).magnitude * 1000f).ToString("F1") + "mm");
            }
            if (!_rtTried) MeasureRestAndTravel();   // once, as soon as the canal is usable
            HoldPenisTargetAgainstScale();           // the aim keeps its world place when the item is scaled
            MeasureOpenCanal();         // measures the open-canal efficiency that feeds the ring drive

            // PAIRING - POST-NodesConstraints here, so k_f_dan_entry sits at its DRIVEN position (the vagina or anus it
            // aimed it at). Match by lateral offset from this womb's channel axis (relative-sticky). FULLY EVENT-DRIVEN,
            // NO POLL: an NC dan_entry add/enable/disable/delete, a character (re)load, the hotkey, or this womb enabling
            // bumps _repairVersion; the womb re-runs FindByEntry on the first post-IK frame its canal is baked, then not
            // again until the next event. (A seated womb's entry is NC-pinned, so a Studio drag can't change this pairing
            // -> no fallback poll is needed.) Then grab that penis's entry (post-NC) for the debug marker.
            if (_seenRepairVersion != _repairVersion && _canalLen > 1e-4f)
            {
                _seenRepairVersion = _repairVersion;
                _pairedName = BPBridge.FindByEntry(_canalEntrance, _canalAxis, LiquidWobbleMPBPlugin.CfgPairRange, _pairedName, 0.02f, Wearer());
                ResolveVaginaSeat();
            }
            // BOTH depth paths read the canal shape now, and the geometry one drives any womb, so this is measured
            // for every Studio womb. Per frame it is a read of each weight; it bakes only when one changed.
            if (MainGameWomb.IsStudio) MeasureCanalShape();
            // NO canal-axis manipulation (hard rule): the canal is the womb mesh's own anatomical
            // axis, period - no tilting, no following, no rotation. Getting the penis INTO the canal is
            // BP's job via the END TARGET (m_innerTarget = the womb's penis_target bone - the literal
            // equivalent of a Studio NodesConstraints pair penis_target -> k_f_dan_end).

            // H REACTION DEPTH from the REAL penis endpoint: override the pin's commanded
            // ExternalStrokeMM with danEnd projected on the canal. GATED - if the end is behind the
            // entrance (along<0) or far off-axis, the penis is OUT -> 0 -> NO reaction. This fixes
            // "penis outside but womb reacts" (the auto-length depth false-fired when the base was near
            // the entrance while the penis pointed away). Only overrides when the real end is known.
            if (!MainGameWomb.IsStudio && _canalLen > 1e-4f)
            {
                // this override is now the ONLY ExternalStrokeMM source in H (the pin's L−d2
                // estimate is deleted). No readable danEnd => 0, never a stale last value.
                // A pose change re-seats the womb, and for a few frames afterwards the canal frame is the
                // OLD pose's. Projecting BP's endpoint onto it gives nonsense - measured lat=358mm and
                // along=535mm on an 84mm canal - and because the gate then fails, the womb was fed a
                // FABRICATED ZERO, so it collapsed and re-inflated on every single pose change. That is
                // the "womb didn't react on the first pose" report. Verified over 30 pose changes: every
                // bad reading landed 3-5 log lines after a bump, with no visual fault on screen at all.
                //
                // So: while the canal has not been re-calibrated for the CURRENT pose, drive nothing and
                // hold what was last measured. Holding existing state is not a fallback path - it is
                // declining to act on input known to be invalid, which is what the rule asks for.
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

            // Same epoch as _grabEntryW below and as the canal above: after constraints have run, so
            // these are the positions that are actually on screen.
            _bpPostValid = BPBridge.TryReadLocked(_pairedName, out _bpPost) && _bpPost.found;
            _grabEntryW = BPBridge.GetEntryWorld(_pairedName);   // post-NC entry of the paired penis (feeds the entry-detach gate)
        }

        // AIM diagnostic. The whole penis-mistarget class shows up right here: the tip is projected
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

        // CANAL CALIBRATION: the POSED-CANAL probe revealed a CONSTANT ~15mm/5° offset between
        // the TRUE skinned canal and the canal-line bone in KKS - every pose, every loop. Cause: the
        // bone was placed on the mesh canal at BUILD time against the KK donor rig's bone arrangement;
        // the KKS rig arranges the same-named bones slightly differently, so the skinned mesh comes out
        // a systematically SHIFTED shape relative to its bones (KK's wearer rig IS the donor rig ⇒ ~0
        // there). Fix at the single source: once per spawn, with the womb AT REST (not engaged, no
        // stretch), bake the skinned mesh, measure the real canal entrance/axis, and MOVE the canal
        // bone onto it. Everything downstream - teal line, seat anchor, depth math, penis_target2 (a
        // child of this bone; the PT2-is-the-target rule stays intact) - corrects together.
        private bool _canalCalibrated;

        private void CalibrateCanalToSkinnedMesh()
        {
            if (_canalCalibrated || MainGameWomb.IsStudio) return;
            if (_smr == null || _canalBone == null || _canalLocal == null || _canalLocal.Length < 2 || _canalLen <= 1e-4f) return;
            // gate on ENGAGEMENT only. The old `_stretchReaction > 1f` guard deadlocked the
            // averaged window: Free-H drives a PERMANENT resting base-stretch, so after the first frame
            // no frame ever passed and _calN froze (CANAL-CAL never printed; the single-frame era only
            // worked by sampling the one spawn frame before the stretch drive first applied). The stretch
            // channel is now ZEROED during the bake itself (below) - same rest shape, no gate needed.
            // the ENGAGED gate is GONE - it blocked calibration for the whole session on a
            // MID-PENETRATION womb spawn (hard rule: the womb may be spawned at any time, and a mid-
            // penetration spawn must not bug out), leaving the canal on the older fallback formula,
            // which drifts with character scale (measured 2026-07-26: formula rest 74.7mm vs baked
            // 81.6mm on the kokan-0.840 girl -> every fit ~7.5mm short -> "dick jumped out on many
            // normal animations"). The bake below is penetration-independent BY CONSTRUCTION:
            // womb_displace (the penis dome-push/stretch channel) is zeroed and the rings are forced
            // to design weights during the bake itself, so an engaged penis cannot poison
            // the sampled rest shape; her mid-stroke stance is averaged out by the same phase-locked
            // multi-loop window as idle wobble.
            // NOT latched here any more - the calibration is IDLE-AVERAGED. A single-frame bake
            // froze the canal bone at a random idle phase (mouth x/z center wobbles ±2-3mm with the pose
            // on wiggly characters), which capped the auto-parity loop at ~3mm: downstream averaging just
            // averaged a constant-but-wrong bone.
            // only bake on the frame where her LOOPED animation crosses CalPhase (once per loop,
            // CalLoops samples) - every calibration everywhere then sees the SAME stance (see consts).
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
                // calibrate against the OPENED tube - the rings open to a common DESIGN
                // centerline that sits 5.2mm (full-open, measured offline) from the rest-slit centroid
                // at the MOUTH (0.2mm at the cervix - the design axis is slightly tilted vs the rest
                // line). Play happens with rings open, so the line/aim must match the OPENED centers:
                // bake with the rings at their configured full-open weights, then restore.
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
                // bake with the stretch/displace channel at ZERO so every sample is the true rest
                // length regardless of the live resting base-stretch (restored right after - one frame,
                // invisible; RefreshCanalWorld applies the (1+f) elongation analytically on top, so a
                // stretched bake here would DOUBLE-COUNT it).
                float savedStretch = 0f;
                if (_stretchIdx >= 0) { savedStretch = _smr.GetBlendShapeWeight(_stretchIdx); _smr.SetBlendShapeWeight(_stretchIdx, 0f); }
                _smr.BakeMesh(_probeMesh);
                if (_stretchIdx >= 0) _smr.SetBlendShapeWeight(_stretchIdx, savedStretch);
                if (savedW != null)
                    for (int i = 0; i < _ringIdx.Length; i++)
                        if (_ringIdx[i] >= 0) _smr.SetBlendShapeWeight(_ringIdx[i], savedW[i]);
                var bv = _probeMesh.vertices;
                Matrix4x4 m = Matrix4x4.TRS(_smr.transform.position, _smr.transform.rotation, Vector3.one);
                // the 10-nearest-vert MEAN is only valid on the CLOSED slit (verts cluster at the
                // center). With the rings OPEN those verts sit on one side of the rim - measured offline:
                // mean reads +17mm off the true ring center (which put the penis in front in every
                // animation). Honest estimator: full ring band, EXTENT-mid in the plane ⊥ axis
                // (density-independent; matches the part-filtered truth to 0.6mm).
                Vector3 meL, mtL;
                BandCenters(bv, out meL, out mtL);
                // accumulate this frame's LOCAL band centers + mid-ring diameter; apply nothing
                // until the window is full. (The mid diameter is the efficiency seed, now averaged.)
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

                // window complete: apply the idle-phase-free MEANS, exactly once
                meL = _calSumEnt / _calN; mtL = _calSumTop / _calN;
                Vector3 me = m.MultiplyPoint3x4(meL), mt = m.MultiplyPoint3x4(mtL);
                Vector3 meshAxis = (mt - me).normalized;
                _bakedCanalLen = (mt - me).magnitude;   // the TRUE world canal length, idle-averaged
                // do NOT seed openEff from the calibration's REST diameter - it's the narrow rest
                // width, not the DRIVEN mm-per-girthScale response (~2x larger), so it made girthScale spike
                // ~2x at first contact (canal ballooned) and the driven measure then LERPED it back over
                // ~4-5s, which showed as the canal going extra big then reducing. openEff now comes ONLY from
                // driven measures (carried across spawns via s_carryResp); RefGirth covers the first contact.
                _canalCalibrated = true;
                // Mid-pen spawn fix, part 2: the baked truth replaces the drifting formula rest.
                // If they disagree by >2mm, every fit so far ran on a wrong canal - bump the pose version
                // so the CURRENT pose re-primes and re-measures on the corrected canal now, instead of
                // staying ~7.5mm short until the next pose hop.
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
                // shift put it off (KKS); a donor-rig wearer (KK) reads ~0 and is left untouched. The teal
                // line, seat anchor, depth math and penis_target2 all follow the bone.
                // An error several times the canal's own length is not a skinning shift - it is the bone
                // and the bake being read in different spaces. Acting on it moved the bone hundreds of mm
                // and drove openEff to 0, so the canal stopped reacting on that pose (measured: err=301.6mm
                // on an 80mm canal). Refuse to act on an impossible measurement and say so.
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
                // ROUTE-B DIAGNOSTIC (temporary): one-shot, ungated. Did the calibration actually see the
                // truly-skinned canal in REBIND mode? bakedCanal is what grounds _canalLen, so if this is
                // wrong the ring reaction desyncs from real penetration travel.
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: CANAL-CAL mode=" + (MainGameWomb.CurrentlyRebound ? "REBIND" : "MIRROR")
                    + " err=" + posErr.ToString("F1") + "mm/" + angErr.ToString("F1") + "deg -> "
                    + (moved ? "BONE MOVED onto the skinned canal" : "bone already matched")
                    + " | bakedCanal=" + (_bakedCanalLen * 1000f).ToString("F1") + "mm openEff=" + _openEfficiency.ToString("F1") + "mm/unit"
                    + " (phase-locked @" + CalPhase.ToString("F2") + " x" + CalLoops + " loops)");
                PredictParity();   // one-shot analytic prediction, logged beside the loop's result
                // PT2 onto the calibrated axis. penis_target2 was authored at the old penis_target's
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
                    // loud, not silent - an unsnapped aim bone means the penis can sit off the canal.
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no canal-frame aim bone found at calibration — PT2 axis-snap SKIPPED; the aim may sit off the canal. Fix the cause (no fallback).");
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: canal calibration failed: " + e.Message);
                _canalCalibrated = false;
                _calN = 0; _calSumEnt = Vector3.zero; _calSumTop = Vector3.zero; _calSumDia = 0f; _calDiaN = 0; _calPrevFrac = -1f;   // restart the window
            }
        }
        // PHASE-LOCKED calibration: catch the loop and measure at the same frame each
        // time"). The idle animation is LOOPED, so sampling at one fixed normalized phase sees the SAME
        // stance every time - stance shifts stop being noise by construction (a time-window average
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

        // DUAL-CHAIN PREDICTOR - a formula for any character, no cache. Both placement
        // paths are deterministic transforms of HER live bones, so the parity nudge can be COMPUTED
        // instead of learned. For each entrance-band vertex, evaluate on the SAME frame:
        //   MIRROR chain:  Σw · TRS(pivot + s·(p_j−pivot) + seat, R_j, s·lossy_j) · origBindpose_j · v
        //                  (SyncNow's bone placement: positions scaled about her kokan, seat added,
        //                   scale injected once at the root ⇒ world scale s × hers)
        //   REBIND chain:  Σw · herBone_j.localToWorld · origBindpose_j · M0 · v   (nudge-free bake)
        // Same-frame ⇒ stance/sway cancel EXACTLY. predictedNudge = mean(mir) − mean(reb).
        // This is the ONLY placement path: it agreed with the measured feedback loop it replaced
        // (2.1mm y, 1.7mm z) and works first-time on any character, so the loop and its learned cache
        // were retired. MainGameWomb.AutoPlacePredicted consumes the result.
        // MODE DOT, Debug-Log gated: a CYAN dot rides the live canal entrance in MIRROR mode, so
        // the entrance the whole drive keys off is visible in-world. X-ray material (ZTest Always) so
        // it shows through the body. It had an ORANGE twin under REBIND, drawn at the mirror target the
        // old measure-loop remembered; that loop was deleted, and the target with it. REBIND is the
        // default, so nothing draws unless Debug Log is on AND the mode is flipped to MIRROR (research builds).
        private GameObject _modeDot;
        private void UpdateModeDot()
        {
            if (MainGameWomb.IsStudio) return;
            if (MainGameWomb.CurrentlyRebound || _canalLen <= 1e-4f)
            { if (_modeDot != null) _modeDot.SetActive(false); return; }
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
                // Internal-Colored multiplies VERTEX colour; a primitive has none - stamp it into the mesh
                var mf = _modeDot.GetComponent<MeshFilter>();
                var mesh2 = mf.mesh; mesh2.hideFlags = HideFlags.HideAndDontSave;
                var cols = new Color[mesh2.vertexCount];
                for (int i = 0; i < cols.Length; i++) cols[i] = Color.cyan;
                mesh2.colors = cols;
            }
            if (!_modeDot.activeSelf) _modeDot.SetActive(true);
            _modeDot.transform.position = _canalEntrance;
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
                    // FROZEN-HELPER EXCEPTION: SyncNow's follow loop gives cf_s_waist01/02 and
                    // cf_s_leg_L/R HER natural scale (localScale = c.localScale - never ×s), only their
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
                // publish for AutoPlacePredicted (one-shot placement, no loop). x never commanded.
                MainGameWomb.PredictedNudgeMM = new Vector3(0f, dHer.y, dHer.z);
                MainGameWomb.PredictedValid = true;
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: PREDICT parity nudge=(" + dHer.x.ToString("F1") + ", " + dHer.y.ToString("F1") + ", " + dHer.z.ToString("F1")
                    + ")mm (analytic dual-chain, same-frame) — publishing for one-shot placement | bandVerts=" + cnt);
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: parity prediction failed: " + e.Message); }
        }

        // measure the OPEN-CANAL efficiency (mm of canal diameter per girthScale unit) from the
        // baked mid-ring, 1s cadence. FEEDS the ring drive so the canal opens to the real penis at any
        // character scale (the RefGirth model mis-predicts on scaled chars).
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
                // SHAPE probe: the VISIBLE mesh's ent/mid/top band centers in HER kokan frame,
                // averaged over 5 samples (=5s). Separates a bottom that sits to the right into
                // TRANSLATION (all three bands share the x offset) vs SKEW/rotation (x drifts with
                // height). Uses the live bake (resting stretch included) = what the eye actually sees.
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
                    // SNAP to the first live measurement of this spawn - the carried/seeded value
                    // can be stale (other mode, other bake, other girl) and the 0.5 lerp took seconds to
                    // converge ("channel still took time to adjust to size"). Later samples smooth noise.
                    // openEff is fine-tuned here but SEEDED from the scale law in the drive (it's a
                    // mesh constant × the womb's world-scale), so girthScale is right from frame 1. This
                    // measure must REJECT the ring-ramp TRANSIENT: right after contact the smoothed ring is
                    // still opening, so canalDiaMM (hence eff) reads far too low (~16 vs the settled ~43) -
                    // an earlier snap/lerp landed on exactly that garbage and the canal ballooned crawling
                    // back up. Accept eff only within a plausible band of the current (seeded) openEff: the
                    // settled value sits in-band, the transient lows fall out. Penetrated-only; slow lerp.
                    if (MainGameWomb.HPenetrated && _openEfficiency > 1e-3f
                        && eff > _openEfficiency * 0.6f && eff < _openEfficiency * 1.7f)
                    {
                        _openEfficiency = Mathf.Lerp(_openEfficiency, eff, 0.35f);
                        // Normalise out the character before carrying, so the next womb scales it by its OWN body.
                        float carryHl = (_canalBone != null && _canalBone.parent != null) ? _canalBone.parent.lossyScale.y : 1f;
                        float carryWs = MainGameWomb.RebindS * carryHl;
                        if (carryWs > 1e-3f) s_carryResp = _openEfficiency / carryWs;   // carried across spawns, scale-free
                    }
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: open-canal measure failed: " + e.Message); }
        }

        private int[] _bandEnt, _bandTop, _bandMid;
        private Mesh _probeMesh;
        private Vector3 _shSumE, _shSumM, _shSumT; private int _shN;   // SHAPE probe accumulators

        // Canal-ring sampling. Membership picked ONCE from the REST mesh: all verts within a thin
        // band (±4mm along the rest canal axis) and <12mm lateral of the endpoint center - the full ring,
        // not a cluster. Foreign verts that leak in (interior/cum layer) sit INSIDE the ring so they can't
        // stretch the extents (validated offline: ≤1.1mm vs the part-filtered truth).
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
            // MID-tube band - the ends both verifiably sit on the line while the visible mesh
            // shows the MIDDLE bowed; the mid was the one place never measured.
            _bandMid = BandVerts(v, (_canalLocal[0] + _canalLocal[_canalLocal.Length - 1]) * 0.5f, axis);
        }

        // Ring centers from a BAKED (posed) mesh, in baked/renderer-local space. EXTENT-mid in the plane
        // ⊥ the posed axis - density-independent (a vert-MEAN is dragged toward whichever rim the verts
        // cluster on: +17mm at the mouth with rings open). Pass 1: band means give
        // the posed axis; pass 2: extent-mids.
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
        // A rejected canal measurement is never silent: everything downstream (pairing, depth,
        // the collider path) is gated on a valid canal - so the womb simply stopped reacting with no
        // explanation. Reported once per womb.
        private bool _offCanalWarned;
        private bool _entryOffWarned;   // said once per womb      // the paired penis runs beside the canal, not through it
        private bool _canalMarkerWarned;   // the canal marker disagreed with the mesh and was dropped
        private const float CanalMeasureGrace = 2f;  // seconds a fresh womb gets to produce its first canal bake
        private float _canalWaitSince = -1f;         // when the canal was first seen unmeasured; -1 while measured
        private bool _canalUnmeasuredWarned;         // the never-measured error has been logged for this womb
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
            // ONE source of truth: the mesh's own `clo_canal_entry` marker bone. Entrance, axis AND
            // length all come from it - exact, and scale/rotation/position-native (a bake re-projection
            // double-applies lossyScale on a scaled womb). NO bake fallback: the shipped womb has the
            // marker, so a womb without one is not a womb this drives - say so and drive nothing.
            // (The old bake path also OWNED _canalLen, and the bone override was gated on it: a bake the
            // plausibility band rejected silently disabled the exact path and left stale geometry behind,
            // which is what reported tens of mm of "drift" the bone-attached womb was not doing.)
            // The bake band is still PROJECTED - the calibration and the diagnostics read _canalBandW -
            // but it is a MEASUREMENT, never a source: it does not set entrance/axis/length. (An earlier
            // the projection along with the fallback and left bakedCanal at 0, so the calibration saw a
            // 301mm error, "moved the bone onto the skinned canal" and drove openEff to 0 - the canal
            // stopped reacting. Removing the fallback must not remove the measurement.)
            if (_canalLocal != null && _canalLocal.Length >= 2 && _smr != null)
            {
                if (_canalBandW == null || _canalBandW.Length != _canalLocal.Length) _canalBandW = new Vector3[_canalLocal.Length];
                for (int i = 0; i < _canalLocal.Length; i++) _canalBandW[i] = _smr.transform.TransformPoint(_canalLocal[i]);
            }
            if (_canalLocal == null)
            {
                // A fresh womb needs a few frames for its first canal bake, and the marker bone is looked up by
                // that bake - so neither is missing yet, only unmeasured. Report it only if it stays that way.
                if (_canalWaitSince < 0f) _canalWaitSince = Time.unscaledTime;
                if (Time.unscaledTime - _canalWaitSince < CanalMeasureGrace) return;
                if (!_canalUnmeasuredWarned)
                {
                    _canalUnmeasuredWarned = true;
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb '" + name + "' still has no canal measurement "
                        + CanalMeasureGrace.ToString("F0") + "s after it appeared - the bake keeps failing or reads an implausible "
                        + "length, so nothing is driven. Respawn the womb, or check that its mesh is intact.");
                }
                return;
            }
            _canalWaitSince = -1f;
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

            // H BASE CANAL STRETCH: a resting womb_displace elongates the whole womb (a few percent of
            // canal length so the penis is shortened LESS to reach). The rest-mesh walk can't see the
            // (blendshape) elongation, so add it analytically here; the MATCHING displace weight is set on
            // the blendshape in the stretch block (both derive from the same DisplaceTravelMM, so the
            // depth math and the visible mesh agree). Everything downstream (rings/stretch/pin) then uses
            // the elongated _canalLen consistently. Studio unchanged (_canalRestLen == _canalLen).
            _canalRestLen = _canalLen;
            if (!MainGameWomb.IsStudio && _canalLen > 1e-4f)
            {
                // The womb SCALE (CfgHWombScale) is applied as a POSITION scale-about-the-entrance in the
                // bone mirror - it grows the mesh but does NOT appear in the canal bone's lossyScale, so
                // the lossyScale-based length under-reports the scaled mesh by that factor (measured:
                // canalLen stuck at 72mm at scale 1.35). Apply it here so the fit/displace math tracks
                // the VISIBLE size; otherwise the displace (calibrated for 72mm) over-elongates the
                // bigger mesh. Base-stretch (blendshape) elongation stacks on top.
                // + the hard-wired 6mm downward extension expressed as an elongation percent (the
                // matching seat down-shift lives in the servo target). ONE source - the displace base
                // weight below uses the same effective percent so mesh and math agree.
                // GROUND the rest canal to the BAKED MESH. The old formula (canalLocalLen ×
                // lossyScale × CfgHWombScale) tracks the real skinned canal only near the reference
                // female - as she scales it DRIFTS (small char kokan 0.598: formula gave 59mm vs the
                // TRUE baked 69mm, so the penis-fit mis-shortened and the tip stopped short). The
                // BakeMesh calibration measures the real canal (_bakedCanalLen) at ANY size, both games;
                // use it as the rest length. Fall back to the formula only until the first bake exists.
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
        // Find M's `clo_canal_entry` marker bone in THIS womb's armature (a leaf under cf_j_kokan, which is one of the
        // SMR's bones). Null on the old mesh -> RefreshCanalWorld falls back to the BakeMesh-walk canal. Searched once.
        // The womb's own canal mouth, for anchoring a penis entry at the womb rather than at one of the
        // character's orifices. Null on an old mesh that ships no marker bone.
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

        // Is this womb seated ON a character's vagina? Caches the nearest cf_J_Vagina_root (outside this subtree) to
        // the canal entrance, on a 2s timer, and pairs when it's within the "Womb-in-vagina range". This is the anchor
        // for the MAIN penis signal: BP-on-this-vagina drives the womb regardless of where the dan bones bunch up.
        // ── ZERO-EFFECT-WHEN-UNUSED GATE ──────────────────────────────────────────────────────────────────────
        // Count of live CloXray womb effects in the scene. The BP-interop hooks (dan-dup guard, penis-FK enforcer,
        // the on-load penis-bend fix) check AnyActive and become complete no-ops when it is 0, so a scene with no
        // CloXray womb behaves exactly as if the DLL were not installed. Bumped in OnEnable / dropped in OnDisable.
        private static int _activeCount;
        public static bool AnyActive { get { return _activeCount > 0; } }
        // EffectiveActive = the master F1 "Enabled" toggle AND a womb present. The BP-interop reach-ins (BPDanBones,
        // the on-load penis-FK release coroutine, the NC-enable pairing hook) gate on THIS, so flipping
        // Enabled OFF leaves BetterPenetration + the penis FK exactly as posed, instantly and live.
        public static bool EffectiveActive { get { return LiquidWobbleMPBPlugin.CfgEnabled && _activeCount > 0; } }

        // Pairing-recheck signal. Every discrete event that can change womb<->penis pairing bumps this version: an NC
        // dan_entry add / enable / disable / delete (NodeConstraintBridge hooks), a character (re)load, the hotkey
        // adding the NC link, or a womb enabling. Each womb re-runs FindByEntry once when its seen-version lags. This
        // REPLACES the old 1s poll outright - no fallback: a seated womb's NC-pinned entry only moves via these events.
        private static int _repairVersion;
        public static void RequestRepair() { _repairVersion++; }

        // SHARED cf_J_Vagina_root cache. The full-scene FindObjectsOfType<Transform> scan is heavy, and the root LIST
        // only changes when a CHARACTER spawns/despawns - an event, not a per-frame thing. So it is refreshed
        // EVENT-DRIVEN: InvalidateVaginaRoots() is called on every CharacterReloaded and whenever a womb is enabled,
        // forcing the next access to rescan. The long fallback below is pure insurance for a spawn path that somehow
        // doesn't raise the KKAPI event - NOT the primary path (was a 2s poll, whose periodic full-scene scan was the
        // breast-shake frame hitch with several wombs). Each womb then just measures distance to the cached few.
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

        // ── VAGINA SEAT: placement and hand-set canal shape (Studio, BetterPenetration) ─────────────────
        // BetterPenetration measures depth from HER vagina. GeomMouthOffset is where this womb's canal mouth
        // sits past that point with the womb at the zero placement in her a_n_kokan slot and every blendshape
        // on its shipped weight, and the whole depth curve is tuned there. A womb moved deeper along the canal,
        // or a blendshape that moves the mouth or lengthens the canal, is reached later by the same stroke, and
        // nothing in the scene can make up for it: the entry has to stay on her vagina for BP to treat the
        // penetration as vaginal. Toys and any other entry are not affected - their depth is not BP's.
        private const string SeatSlotName = "a_n_kokan";
        // Hand-set channels the womb mesh ships on a non-zero weight. The canal is measured against the shape as
        // shipped, so a womb left on its defaults measures exactly as before.
        private static readonly string[] ShippedShapeNames   = { "mounddown", "ovary_shrink" };
        private static readonly float[]  ShippedShapeWeights = { 50f, 10f };

        /// Re-run on every pairing event. Vagina-seated = the paired penis's k_f_dan_entry is driven by the
        /// wearer's own cf_J_Vagina_root. The womb's Studio object is looked up here too, since that walk is
        /// too heavy for every frame; whether it sits in her slot is checked live (SeatOffsetAlongCanal), so
        /// moving it into or out of the slot needs no pairing event.
        private void ResolveVaginaSeat()
        {
            Transform vagina = null, item = null;
            if (MainGameWomb.IsStudio && !string.IsNullOrEmpty(_pairedName))
            {
                Transform entry = BPBridge.GetEntryBone(_pairedName);
                Component her = Wearer();
                vagina = entry != null ? NodeConstraintBridge.DriverNamed(entry, "cf_J_Vagina_root") : null;
                if (vagina != null && (her == null || !vagina.IsChildOf(her.transform))) vagina = null;
                if (vagina != null) item = AutoBodyReveal.StudioObjectRoot(transform);
            }
            if (vagina != _seatVagina || item != _seatItem)
            {
                bool inSlot = item != null && item.parent != null && item.parent.name == SeatSlotName;
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: womb '" + WombTag() + "' - "
                    + (vagina == null
                       ? "not vagina-seated (penis '" + (_pairedName ?? "none") + "'), so its placement and blendshapes do not change the depth."
                       : "'" + _pairedName + "' enters through her vagina; "
                         + (inSlot
                            ? "the womb sits in her " + SeatSlotName + " slot, so its placement there and its hand-set blendshapes both move where he meets it."
                            : "the womb is not directly in her " + SeatSlotName + " slot, so only its hand-set blendshapes are accounted for.")));
            }
            _seatVagina = vagina; _seatItem = item;
        }

        /// An advance past the womb's mouth, in world metres, read back onto the canal the depth curve was tuned on.
        /// The hand-set blendshapes move the mesh mouth while the marker bone every measurement starts from stays
        /// put, so their shift comes off first; inside the canal the rest scales by the length ratio, and past the
        /// cervix it stays 1:1, since the displace works in millimetres there. Both depth paths go through this, so
        /// a stretched canal reaches its rings at the same place whichever one drives. Identity on the shipped shape.
        private float ShapedAdvance(float past)
        {
            past -= _shapeMouthFrac * _canalLen;
            float k = _shapeLenRatio;
            if (past <= 0f || k <= 0f) return past;
            float lenNow = _canalLen * k;
            return past <= lenNow ? past / k : _canalLen + (past - lenNow);
        }

        /// How far the womb has been moved along its canal from the zero placement in her slot, in world metres
        /// (+ = deeper). Read live: the womb can be dragged at any time.
        private float SeatOffsetAlongCanal()
        {
            if (_seatItem == null || _canalBone == null) return 0f;
            Transform slot = _seatItem.parent;
            if (slot == null || slot.name != SeatSlotName) return 0f;   // moved out of the slot since the pairing event
            Vector3 mouthInItem = _seatItem.InverseTransformPoint(_canalBone.position);
            Vector3 mouthAtZero = slot.TransformPoint(Vector3.Scale(_seatItem.localScale, mouthInItem));
            Vector3 axisAtZero = slot.TransformDirection(_seatItem.InverseTransformDirection(_canalBone.up));
            return Vector3.Dot(_canalBone.position - mouthAtZero, axisAtZero.normalized);
        }

        private bool IsDrivenShape(int i)
        {
            if (i < 0) return false;
            if (i == _stretchIdx || i == _moundFwdIdx || i == _moundBackIdx || i == _moundLeftIdx || i == _moundRightIdx) return true;
            if (_ringIdx != null) for (int r = 0; r < _ringIdx.Length; r++) if (_ringIdx[r] == i) return true;
            return false;
        }

        // Distance between two neighbouring rings, in metres - the natural unit for "how wide a band around the
        // object opens" when the rings react independently.
        private float RingGapWorld()
        {
            int n = _ringIdx != null ? _ringIdx.Length : 0;
            if (n < 2 || _canalLen <= 1e-4f) return 0.01f;
            return Mathf.Abs(DepthEnd - DepthStart) / (n - 1) * _canalLen;
        }

        // The control sliders move no vertex, so they are neither baked nor watched.
        private bool IsControlShape(int i)
        {
            return i >= 0 && (i == _strengthIdx || i == _dampeningIdx || i == _ignoreCollidersIdx
                          || i == _sensIdx || i == _indepRingsIdx);
        }

        /// Canal length and mouth position under the hand-set blendshapes, against the shipped shape. The weights
        /// are read every frame and the canal is re-measured only when one changed, at most four times a second
        /// while a slider is dragged. On the shipped weights nothing is baked.
        private void MeasureCanalShape()
        {
            if (_smr == null || _smr.sharedMesh == null || _canalLocal == null || _canalLocal.Length < 2) return;
            var mesh = _smr.sharedMesh;
            int n = mesh.blendShapeCount;
            if (_shapeRef == null || _shapeRef.Length != n)
            {
                _shapeRef = new float[n];
                for (int i = 0; i < n; i++)
                {
                    string nm = mesh.GetBlendShapeName(i) ?? "";
                    int k = Array.IndexOf(ShippedShapeNames, nm.Substring(nm.LastIndexOf('.') + 1));
                    _shapeRef[i] = k >= 0 ? ShippedShapeWeights[k] : 0f;
                }
                _shapeSeen = null;
            }
            bool changed = _shapeSeen == null || _shapeSeen.Length != n;
            for (int i = 0; i < n && !changed; i++)
                if (!IsDrivenShape(i) && !IsControlShape(i) && Mathf.Abs(_smr.GetBlendShapeWeight(i) - _shapeSeen[i]) > 0.01f) changed = true;
            if (!changed || Time.unscaledTime < _shapeNext) return;
            _shapeNext = Time.unscaledTime + 0.25f;
            if (_shapeSeen == null || _shapeSeen.Length != n) _shapeSeen = new float[n];
            bool shipped = true;
            var set = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                _shapeSeen[i] = _smr.GetBlendShapeWeight(i);
                if (IsDrivenShape(i) || IsControlShape(i) || Mathf.Abs(_shapeSeen[i] - _shapeRef[i]) <= 0.01f) continue;
                shipped = false;
                set.Append(' ').Append(mesh.GetBlendShapeName(i)).Append('=').Append(_shapeSeen[i].ToString("F0"));
            }
            float ratio = 1f, mouthFrac = 0f;
            if (!shipped && !BakeCanalShape(n, out ratio, out mouthFrac)) { ratio = 1f; mouthFrac = 0f; }
            if (Mathf.Abs(ratio - _shapeLenRatio) > 1e-4f || Mathf.Abs(mouthFrac - _shapeMouthFrac) > 1e-4f)
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: womb '" + WombTag() + "' canal shape "
                    + (shipped ? "back on the shipped weights" : "from" + set)
                    + ": length x" + ratio.ToString("F3") + ", mouth " + (mouthFrac * _canalLen * 1000f).ToString("+0.0;-0.0")
                    + "mm along the canal.");
            _shapeLenRatio = ratio; _shapeMouthFrac = mouthFrac;
        }

        /// Two bakes with every plugin-driven channel at 0: the hand-set weights as they are, then the shipped
        /// weights. The mouth and cervix bands of each give the length ratio and the mouth shift, both as
        /// fractions of the shipped canal so the bake's own units cancel. Every weight touched is put back
        /// before the frame renders, however this returns.
        private bool BakeCanalShape(int n, out float ratio, out float mouthFrac)
        {
            ratio = 1f; mouthFrac = 0f;
            var saved = new float[n];
            for (int i = 0; i < n; i++) saved[i] = _smr.GetBlendShapeWeight(i);
            try
            {
                EnsureCanalBands();
                if (_bandEnt == null || _bandTop == null || _bandEnt.Length == 0 || _bandTop.Length == 0)
                    return ShapeFail("the canal mouth or cervix band is empty");
                if (_probeMesh == null) _probeMesh = new Mesh();
                for (int i = 0; i < n; i++) if (IsDrivenShape(i)) _smr.SetBlendShapeWeight(i, 0f);
                _smr.BakeMesh(_probeMesh);
                Vector3[] v = _probeMesh.vertices;
                Vector3 mouthNow = BandCentroid(v, _bandEnt), topNow = BandCentroid(v, _bandTop);
                for (int i = 0; i < n; i++) if (!IsDrivenShape(i) && !IsControlShape(i)) _smr.SetBlendShapeWeight(i, _shapeRef[i]);
                _smr.BakeMesh(_probeMesh);
                v = _probeMesh.vertices;
                Vector3 mouthRef = BandCentroid(v, _bandEnt), topRef = BandCentroid(v, _bandTop);
                Vector3 axis = topRef - mouthRef;
                float lenRef = axis.magnitude;
                if (lenRef < 1e-4f) return ShapeFail("the shipped canal measured " + (lenRef * 1000f).ToString("F1") + "mm");
                axis /= lenRef;
                float r = Vector3.Dot(topNow - mouthNow, axis) / lenRef;
                if (r < 0.5f || r > 3f) return ShapeFail("the canal length ratio measured " + r.ToString("F3"));
                ratio = r;
                mouthFrac = Vector3.Dot(mouthNow - mouthRef, axis) / lenRef;
                return true;
            }
            catch (Exception e) { return ShapeFail("the bake threw: " + e.Message); }
            finally
            {
                for (int i = 0; i < n; i++)
                    if (!IsControlShape(i)) _smr.SetBlendShapeWeight(i, saved[i]);
            }
        }

        private bool ShapeFail(string why)
        {
            if (!_shapeFailLogged)
            {
                _shapeFailLogged = true;
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb '" + WombTag() + "' - its hand-set blendshapes could not be measured ("
                    + why + "), so they are NOT accounted for in when it reacts.");
            }
            return false;
        }

        /// BP depth as the shipped womb at the zero placement would read it. The mouth moves by the placement and
        /// the hand-set shape, and the canal up to the cervix is scaled by the length ratio; past the cervix the
        /// push stays 1:1, since the displace works in millimetres there. At the zero placement on the shipped
        /// shape the depth comes back unchanged. Not clamped at 0: the entrance ring starts opening below depth 0,
        /// so a womb seated deeper has to read negative until the penis nears its mouth, or its entrance would
        /// open while he is still short of it by the whole offset.
        private float SeatedDepth(float depth)
        {
            float unit = RefDanLength * FullDepthIn * CanalScaleY();
            if (unit <= 1e-6f || _canalLen <= 1e-4f) return depth;
            float seat = SeatOffsetAlongCanal();
            float mouth = seat + _shapeMouthFrac * _canalLen;
            float k = _shapeLenRatio;
            float pastRef = ShapedAdvance(depth * unit - GeomMouthOffset - seat);   // travelled past THIS womb's mouth
            float result = (GeomMouthOffset + pastRef) / unit;
            if (LiquidWobbleMPBPlugin.CfgDebugLog && Time.unscaledTime >= _seatLogNext
                && (Mathf.Abs(mouth) > 5e-4f || Mathf.Abs(k - 1f) > 1e-3f))
            {
                _seatLogNext = Time.unscaledTime + 1f;
                LiquidWobbleMPBPlugin._logger?.LogInfo("SEAT '" + WombTag() + "' depth " + depth.ToString("F3") + " -> " + result.ToString("F3")
                    + " (placement " + (seat * 1000f).ToString("+0.0;-0.0") + "mm, blendshape mouth "
                    + (_shapeMouthFrac * _canalLen * 1000f).ToString("+0.0;-0.0") + "mm, canal x" + k.ToString("F3") + ")");
            }
            return result;
        }

        // Project a world point onto this womb's TUBE CENTERLINE (the ring-center canal axis = the tube CENTER,
        // where BP naturally centers the penis) and return the foot at the same depth. Lets the plugin snap the
        // penis_target aim bone onto the real tube center. False if the canal hasn't baked yet.
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

        // Deepest point of the penis SHAFT (the whole cm_j_dan bone chain) that lies inside this womb's canal/bulb.
        // Replaces the single-tip test, which broke once BP bends the penis fully: the geometric TIP can curl back
        // OUT (below/beside the womb) while the shaft still threads it. Scan every shaft bone and keep the one
        // with the greatest along-canal depth whose lateral offset is within a DEPTH-SCALED womb radius - narrow
        // (~girth) at the entrance, widening to the bulb half-width deep in. The bulb radius comes from the REST
        // mesh extents mapped to world (oriented with the womb, so a tilted womb doesn't inflate it the way the
        // world AABB does). Rejects a penis lying BESIDE the womb (every bone beyond the radius) and a withdrawn
        // one (deepest contained bone shallow). Sets _bpLateral for the debug line (closest approach when missed).
        private readonly Vector3[] _aimBuf = new Vector3[1];   // reusable 1-point buffer for the aimed-tip containment test

        // Penis WORLD girth radius at a given distance BACK FROM THE TIP (world metres), from BP's per-collider
        // girth profile. Each collider's distance from the tip = |pos - tipPos|; interpolate the radius at
        // `distFromTip` (clamped to the tip/base ends). Returns -1 with no profile -> caller uses the uniform tip
        // girth. This is the per-ring taper: each ring maps to the penis part lined up with it, growing with depth,
        // WITHOUT the womb and penis needing a shared axis (it uses the penis's intrinsic curve + insertion depth).
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

        // The womb's half-width (bulb radius) in world units, from the REST mesh extents (oriented with the womb, so
        // it's tilt/flip-independent). The canal containment tolerance grows toward this with depth.
        // PERF: memoised per frame. lossyScale walks the whole ancestor chain, and this was called 2-4x
        // in a single LateUpdate (twice via DeepestShaftInCanal for the real + aimed shaft, plus the lateral
        // gate and the collider read) all returning the identical value. Every caller lives in that one
        // LateUpdate pass, so a frame-keyed cache is exact.
        private float _bulbRCache; private int _bulbRFrame = -1;
        // Several wombs in a scene carry the SAME object name - a crowded scene had four called
        // clo_xraywomb and five called clo_xraywomb2 - so a per-name reading pools wombs that have
        // nothing to do with each other. Every diagnostic that gets aggregated needs the instance.
        private string WombTag() { return name + "#" + GetInstanceID(); }

        // ONE definition of "inside the canal tube", used by every containment test. The rings open the
        // canal to fit THIS penis, so his own tip radius plus wall slack is its half-width - the womb's
        // MESH bounds are not (they include the tubes and ovaries, over 117mm, and reject nothing).
        // 25mm of slack: measured, a bent shaft's mid bones sit 21.7-29.2mm off the axis while the tip is
        // centred, and 10mm dropped engagement on five frames of one stroke. Kept in one place because
        // raising it in two of the three sites and not the third is exactly what went wrong before.
        private float CanalHalfWidth() { return (Mathf.Max(_bp.girthTip, 0.005f) + 0.025f) * _sens; }
        // TIP vetoes use a DIFFERENT width. The 25mm above is a bend allowance for the SHAFT scan, where
        // a bent penis's mid bones sit 21.7-29.2mm off the axis; the tip is centred and needs only wall
        // slack. Nor may sensitivity widen a REJECTION: at sens=2 the shaft width reaches 90mm, which
        // stops rejecting both the measured 50mm-off tip and the 80mm-away neighbouring womb these
        // vetoes exist for. sens may only tighten, which is the designed display-womb behaviour.
        // Never narrower than 30mm: tip thickness plus 10mm alone gives a thin tip only about 22mm, which leaves
        // little room for a tip slightly off the centre line. 30mm still rejects a tip 50mm off the axis and a
        // neighbouring womb 80mm away.
        private float CanalTipHalfWidth() { return Mathf.Max(Mathf.Max(_bp.girthTip, 0.005f) + 0.010f, CanalTipMinHalfWidth) * Mathf.Min(_sens, 1f); }
        private const float CanalTipMinHalfWidth = 0.030f;

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
            // THE CANAL TUBE, not the womb's bounds. WombBulbRadius covers the tubes and the ovaries
            // too - over 117mm - so as a containment width it accepted a shaft running beside the canal
            // as readily as one inside it. The rings open the canal to fit THIS penis, so his own tip
            // radius plus wall slack is its half-width. All the containment tests call CanalHalfWidth()
            // so they cannot disagree; the toy path has its own, based on the TOY's radius.
            float oldBulbR = Mathf.Max(WombBulbRadius(), w) * _sens;   // for the change report below
            float bulbR = CanalHalfWidth();
            bool any = false; float bestAlong = float.NegativeInfinity, bestLat = -1f, minLat = float.PositiveInfinity;
            bool wouldHavePassed = false;   // the old mesh-bounds gate would have accepted this frame
            float rejectedLat = float.PositiveInfinity;   // lateral of the bone the tightening actually cost
            for (int k = 0; k < count; k++)
            {
                Vector3 rel = pts[k] - _canalEntrance;
                float a = Vector3.Dot(rel, _canalAxis);
                float l = (rel - a * _canalAxis).magnitude;
                if (l < minLat) minLat = l;
                if (a < -w) continue;                                                  // behind the entrance
                // FLAT womb half-width (same as the collider path, build 367) - a depth-scaled gate is too strict at
                // shallow depth for a womb placed OFF the penis path (the shaft then reads off-axis at every
                // depth). "Inside the womb's half-width" is the real test; bulbR rotates with the womb (orientation-OK).
                if (l > bulbR)
                {
                    // Did the OLD mesh-bounds gate accept this point? Only then does the tightening
                    // change anything, and that is the only case worth reporting.
                    if (l <= oldBulbR && l < rejectedLat) { wouldHavePassed = true; rejectedLat = l; }
                    continue;                                                          // beside the womb, not threading it
                }
                if (a > bestAlong) { bestAlong = a; bestLat = l; any = true; }
            }
            if (!any)
            {
                // The womb went quiet AND the old width would have kept it alive - so this rejection is
                // the tightening, not the scene. Says so outright rather than leaving a silent womb.
                if (wouldHavePassed && LiquidWobbleMPBPlugin.CfgDebugLog && Time.unscaledTime >= _tightLogNext)
                {
                    _tightLogNext = Time.unscaledTime + 2f;
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb '" + WombTag()
                        + "' - no shaft bone is inside the canal tube (half-width "
                        + (bulbR * 1000f).ToString("F1") + "mm). The nearest bone AHEAD of the entrance sits "
                        + (rejectedLat * 1000f).ToString("F1") + "mm off the axis"
                        + " (the global closest, " + (minLat * 1000f).ToString("F1")
                        + "mm, is behind it and never a candidate). The old mesh-bounds width ("
                        + (oldBulbR * 1000f).ToString("F1")
                        + "mm) WOULD have accepted it, so the tightened gate is why this womb is not reacting.");
                }
                _bpClosest = (minLat < float.PositiveInfinity) ? minLat : -1f; return false;
            }
            along = bestAlong; lat = bestLat;
            return true;
        }

        // ROUTE-B DIAGNOSTIC (temporary - strip with the prototype). One line per spawn, ~2s in so the
        // canal bake/calibration has settled, ungated so no F1 change is needed. Toggling MIRROR<->REBIND
        // respawns the womb, so each press yields one directly comparable line.
        private float _routeBReportAt = -1f; private bool _routeBReported;
        private string _lastCanalCam = "-"; private bool _lastCanalCamVisible;   // camera-dependence probe
        private float _orbitNext;   // camera-orbit probe throttle
        // IDLE-AVERAGED placement: her idle loop flexes the waist/leg bones, deforming the skinned
        // tube ±0.2-0.5mm relative to her pelvis, so any single-frame sample carries idle-phase noise.
        // Accumulate entInHer EVERY frame and emit mean ± spread per 5s window (several idle cycles) -
        // the mean is the phase-free placement, the spread quantifies the idle wobble itself.
        private Vector3 _avgSum, _avgMin, _avgMax, _avgAxisSum; private int _avgN; private float _avgEnd = -1f;
        // alt-tab PAUSES Unity - coroutines/measurement windows freeze and resume with a
        // time jump, which may have contaminated earlier readings. Track focus transitions and STAMP
        // any measurement window they touch so it is discarded, never misread.
        private static int s_focusEvents;
        // exposed so the research sampler can discard any observation window that spans a focus
        // change. Alt-tab PAUSES Unity, so a window straddling it mixes real seconds with frozen ones.
        internal static int FocusEventCount { get { return s_focusEvents; } }
        private int _winFocusEv = -1;
        private void OnApplicationFocus(bool focused)
        {
            s_focusEvents++;
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: game " + (focused ? "FOCUSED" : "UNFOCUSED — Unity pauses; any measurement window spanning this is suspect") + ".");
        }

        private void LateUpdate()
        {
            Perf.FrameMark();
            long t0 = Perf.Start();
            try { LateUpdateBody(); }
            finally { Perf.Stop(Perf.WombLate, t0); }
        }

        private void LateUpdateBody()
        {
            if (!_ready) return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;     // master toggle OFF -> freeze: stop driving ring/stretch/mound weights (they stay latched)

            // Dev marker, Debug-Log gated so release does no per-frame work and never shows a
            // stray sphere; hide it explicitly if the flag is turned off while the dot is up.
            if (LiquidWobbleMPBPlugin.CfgDebugLog) UpdateModeDot();
            else if (_modeDot != null && _modeDot.activeSelf) _modeDot.SetActive(false);

            // ANCHOR-VS-BP, REBIND-PATH. Front/back inconsistency against the BP penis entrance is NOT
            // followed here: SyncNow early-returns when Rebound,
            // so the BP-anchor servo NEVER RUNS in Route B, the H default; placement there is
            // a pelvis-frame constant from the rebind bake, blind to each card's BP node. The probe
            // sat in that same dead path - moved HERE, which runs in every mode). Logs the live entrance
            // vs BP's aim point, decomposed along the canal axis / her forward / lateral. CONSTANT per
            // pair = the offsets; DIFFERENT across pairs = the card variance the rebind seat can't see.
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

            // ── CAMERA-ORBIT PROBE ──────────────────────────────────────────────────────────────────
            // Decides "sheared object LOOKS like it leans" vs "the womb genuinely moves with the camera".
            // The womb's placement is expressed in HER PELVIS frame, which cancels her idle animation, so
            // these values MUST stay constant while only the camera moves. If they track camPos/camFwd,
            // something really couples the view to the womb; if they are flat while the camera swings, the
            // lean was projection of the shear (or parallax) and not a placement bug.
            // Run with NO respawning - a re-toggle re-bakes and would confound the reading.
            // STANDARD MEASUREMENT PROTOCOL - no apples to oranges between runs.
            // Every absolute placement number is emitted through ONE normalized channel:
            //   frame     - her pelvis local (InverseTransformPoint) => her world pos/rot cancelled
            //   units     - BOTH her-local mm and world mm (x her lossyScale) => cross-character
            //               comparisons are explicit about basis, never accidental
            //   time      - every-frame accumulation over 5s windows => idle-phase averaged out
            //   validity  - spread > 2mm on any axis => UNSTABLE (she moved: transition/action) -
            //               such windows must be DISCARDED, not compared
            //   identity  - character root, her kok scale, animation name, penetration flag => two
            //               windows are comparable ONLY if these all match
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

            // DIAGNOSTIC ONLY, so it must cost nothing when the log is off. Everything below builds
            // report strings and one block walks the WHOLE character's transform hierarchy looking for
            // three bones - twice a second, in Free-H. Without this guard all of that ran in a shipped
            // build too and was thrown away at the `?.` on the logger.
            if (LiquidWobbleMPBPlugin._logger != null
                && !MainGameWomb.IsStudio && _canalLen > 1e-4f && Time.unscaledTime >= _orbitNext
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
                    // report the sizes at womb spawn - girl-picking needed a
                    // 15s stroke sample only because nothing LOGGED the sizes; the predictor itself always
                    // read the bones live). Her side is exact at spawn; the male values are the pin's
                    // last-known (pairing is async and a fresh pair may not have latched yet).
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
                        // GROUND TRUTH physical size: the rendered world AABB in metres. Independent of
                        // which placement path drew it, so MIRROR vs REBIND is a direct numeric compare -
                        // this is what actually answers "is the womb the same size".
                        + " || WORLDSIZE=" + (_smr != null ? _smr.bounds.size.ToString("F3") : "-")
                        + " kokanLossyY=" + (_canalBone != null && _canalBone.parent != null ? _canalBone.parent.lossyScale.y.ToString("F3") : "-")
                        // the entrance-to-kokan vector r, split along/perpendicular to the canal axis.
                        // The measured mode offset is (s-1) x ~48mm; if |r| lands near 48mm with r almost
                        // entirely ALONG the axis, the difference is a pure along-axis scaling of r and the
                        // correction is exact rather than derived from an assumption.
                        + (_canalBone != null && _canalBone.parent != null
                            ? " || kokanPos=" + _canalBone.parent.position.ToString("F3")
                              + " rLen=" + ((_canalEntrance - _canalBone.parent.position).magnitude * 1000f).ToString("F1")
                              + "mm rAlong=" + (Vector3.Dot(_canalEntrance - _canalBone.parent.position, _canalAxis) * 1000f).ToString("F1")
                              + "mm rPerp=" + (((_canalEntrance - _canalBone.parent.position) - _canalAxis * Vector3.Dot(_canalEntrance - _canalBone.parent.position, _canalAxis)).magnitude * 1000f).ToString("F1") + "mm"
                            : ""));

                    // ── VULVA-VS-ENTRANCE ── where the BP penis penetrates against where the
                    // womb entrance is: the game's own vulva anchors vs the canal mouth, in HER kokan
                    // frame. BP targets `cf_J_Vagina_root` (opening) / `cf_J_Vagina_Inner` (deep) /
                    // `k_f_kokan_00` (the game's vagina target node) - identical names in KK and KKS
                    // (verified from both BP assemblies), so delta(ent−root) is the direct, comparable
                    // placement-offset number for the KKS calibration.
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

                    // ── XFORM PROBE ─────────────────────────────────────────────────────────────────
                    // Stop INFERRING the transform chain from downstream symptoms and measure it.
                    // For the kokan bone, (bone.localToWorld * bindpose) IS the mesh->world matrix Unity
                    // skins through. Decomposing it exposes exactly which term differs between MIRROR and
                    // REBIND - translation, per-axis scale, or basis rotation - instead of guessing.
                    // predEnt maps the AUTHORED rest canal endpoint through that chain: comparing it with
                    // the ACTUAL measured entrance separates "chain math wrong" from "skinning differs".
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
                            // camera-dependence probe. If cam/visible differ between toggles and the
                            // placement moves with them, the calibration is reading a view-dependent bake.
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
                // The stretch CURVE is Studio-only - StretchTargetAt has no other call site - so the
                // main-game boost scaled two values Free-H never reads. Free-H builds its displace from
                // base + contact and is tuned with CfgHWombPush instead.
                StretchMax       = LiquidWobbleMPBPlugin.CfgStretchMax;
                StretchStart     = LiquidWobbleMPBPlugin.CfgStretchStart;
                StretchOvershoot = LiquidWobbleMPBPlugin.CfgStretchOvershoot;
                DirReactWeight   = LiquidWobbleMPBPlugin.CfgDirReact;
                OpenLead           = LiquidWobbleMPBPlugin.CfgOpenLead;
                CloseSmoothing     = LiquidWobbleMPBPlugin.CfgCloseSmoothing;
                EntranceOpenWidth  = LiquidWobbleMPBPlugin.CfgEntranceOpenWidth;
                RingRampWidth      = LiquidWobbleMPBPlugin.CfgRingRampWidth;
                EntranceCloseScale = LiquidWobbleMPBPlugin.CfgEntranceCloseScale;
                OpenTime           = LiquidWobbleMPBPlugin.CfgOpenTime;
                EntranceOpenScale  = LiquidWobbleMPBPlugin.CfgEntranceOpenScale;
                MaxGirthScale      = LiquidWobbleMPBPlugin.CfgMaxGirthScale;
            }

            // Per-object controls (inert blendshapes). Defaults if the control shape is absent.
            float strength = _strengthIdx >= 0 ? _smr.GetBlendShapeWeight(_strengthIdx) / 50f : 1f;
            float dampTime = _dampeningIdx >= 0 ? Mathf.Max(0.01f, _smr.GetBlendShapeWeight(_dampeningIdx) / 100f) : 0.15f;
            // Penetration TRIGGER sensitivity (per-womb): 0 = off, 1.0 = default (weight 50), up to 2x. Scales the
            // containment lateral gate (via _sens, used in DeepestShaftInCanal) and the depth-to-engage (below).
            float sens = _sensIdx >= 0 ? _smr.GetBlendShapeWeight(_sensIdx) / 50f : 1f;
            _sens = sens;
            // Per-object collider opt-out: weight > 50 => skip the DynamicBoneCollider fallback entirely
            // (this womb won't react to a collider/toy pushed in) while still reacting to the BP penis.
            // Absent shape (idx < 0) or weight 0 => react to colliders as before.
            bool ignoreColliders = _ignoreCollidersIdx >= 0 && _smr.GetBlendShapeWeight(_ignoreCollidersIdx) > 50f;
            // INDEPENDENT RINGS (collider path only) - see IndependentRingsShape. 0 = off; above that the weight is
            // the band width in ring gaps, so the author goes from a tight grip on a ball to a couple of rings
            // moving with it. Gradual, so unlike the switch above it uses the whole slider.
            float indepRingsW = _indepRingsIdx >= 0 ? _smr.GetBlendShapeWeight(_indepRingsIdx) : 0f;
            bool indepRings = indepRingsW > 0.01f;

            // BP_Strength = 0 -> hand off completely: don't touch the driven shapes, so your manual
            // / KKPE pose controls them. (Driven shapes are OVERRIDDEN when strength > 0; additive does not survive
            // KKPE re-applying manual weights each frame.)
            if (strength <= 0.001f || sens <= 0.001f)
            {
                if (!_handoffLogged) { _handoffLogged = true; LiquidWobbleMPBPlugin._logger?.LogInfo($"WombExpand '{name}': {(strength <= 0.001f ? "BP_Strength" : "BP_Sensitivity")} is 0 -> auto-reaction handed off to manual/KKPE (the womb will NOT auto-expand). Raise it in Material Editor to re-enable."); }
                for (int i = 0; i < _ringReaction.Length; i++) _ringReaction[i] = 0f;
                _stretchReaction = 0f;
                _moundFwdReact = 0f; _moundBackReact = 0f; _moundLeftReact = 0f; _moundRightReact = 0f;
                _depth = 0f; _depthFast = 0f;
                return;
            }
            _handoffLogged = false;

            // Depth + girth from BP - from the penis nearest THIS womb's entrance (TryReadNear),
            // not the globally deepest one: several wombs in a scene each pair with their own
            // penis instead of all reacting to one.
            _bpGeomDepth = false;   // cleared before this frame's decision so it reflects this frame's decision
            _traceSrc = "none";
            bool engaged = false;
            float girthScale = 1f;
            float targetNorm = 0f;
            _bpLateral = -1f; _bpClosest = -1f; _bpVaginaMain = false; _tipDetached = false; _tipDist = -1f; _vaginaFarLat = false;
            _entryDetached = false; _entryLat = -1f;
            EnsureVaginaPairing();   // cached 2s: is this womb seated on a cf_J_Vagina_root?
            // The womb<->penis pairing is decided in OnPreCullCanal - POST-NodesConstraints - so k_f_dan_entry is read at
            // its DRIVEN position (at the vagina/anus), not the penis-rig default. Here it is just the chosen penis.
            _bpHadGirthThisFrame = false;   // reset each frame; set below if BP reports a real girth
            // read the penis girth at SPAWN, before penetration. BP's girth is the male's
            // STATIC penis-collider radii, present from character load - so once the canal is known,
            // find the positioned penetrator (nearest male, generous range) and latch its girth NOW,
            // instead of waiting for the entry marker to reach the canal (penetration). Runs only until
            // latched (s_bpDiaMM>0) and only while not yet paired - then the paired-read below owns it,
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
                // the penis girth comes from BP's collider radii, which are STATIC - read
                // and latch the diameter the moment the male is paired, no penetration needed.
                if (_bp.girthTip > 1e-4f) { LatchBPGirth(_bp.girthTip * 2000f, _bp.srcName); _bpHadGirthThisFrame = true; }
                float w = PenisInCanalWidth;
                // `contained` = a dan bone (or the aimed tip) physically threads the canal. Kept ONLY to (a) report the
                // off-axis distance in the diagnostic and (b) act as an extra OR below - it is NOT REQUIRED
                // for the womb to react (it can never gate OUT now).
                // POST-CONSTRAINT positions. These are compared against the canal, which is sampled in
                // onPreCull, so they must come from that epoch too - the LateUpdate copy still holds the
                // nominal bones and walks with an origin edit that moves nothing on screen.
                // NO FALLBACK. This is compared against a canal captured in onPreCull, so it MUST be the
                // penis from that same epoch. Substituting the LateUpdate copy when the read failed is
                // precisely the fault this guard exists to prevent - measured at 4.4 METRES on an NC-pinned
                // character. If the read failed, geo is default and nothing can thread the canal; the
                // engage gate below refuses outright and says why.
                BPBridge.Reading geo = _bpPost;
                float realAlong = 0f, realLat = -1f;
                bool realIn = (geo.shaftPos != null) && DeepestShaftInCanal(geo.shaftPos, geo.shaftPos.Length, w, out realAlong, out realLat);
                float aimAlong = 0f, aimLat = -1f; bool aimedIn = false;
                if (geo.aimedValid) { _aimBuf[0] = geo.aimedTipPos; aimedIn = DeepestShaftInCanal(_aimBuf, 1, w, out aimAlong, out aimLat); }
                bool contained = realIn || aimedIn;
                _bpLateral = realIn ? realLat : (aimedIn ? aimLat : _bpClosest);
                // MAIN penis detection (build 376): the womb is an x-ray overlay ON a vagina, so react when BP says THIS
                // womb's vagina is being penetrated. BP's depth is POSITION-based (it holds on a static pose - a half-in
                // posed penis reads raw>0) and is robust to the length-squish + overlay seating that bunch the dan BONES
                // far below the cervix while the mesh + BP sit deep. So bone-containment is no longer required - it stays
                // only as an OR so anything that engaged in 370 still engages. raw>eps gates the close (falls to ~0 on
                // withdrawal) and rejects a glued k_f_dan_end on a withdrawn penis.
                // TIP-DETACH (build 377): ALSO treat the penis as withdrawn when its tip marker k_f_dan_end has been pulled
                // far from the womb. BP's `raw` does NOT fall when you move only the tip (a constraint/sphere on k_f_dan_end
                // moves the tip but not the male's base), so without this the womb stays open after you pull the tip out.
                // Inserted, the tip sits ~0.08-0.13m from the entrance; pulled out it's 0.3m+. Only gates when a tip exists.
                float tipDetach = LiquidWobbleMPBPlugin.CfgTipDetach;
                // DIRECTIONAL tip-detach: the aimed tip (k_f_dan_end) counts as "pulled out" (withdrawn) only when it's
                // BELOW the mouth (along < -detach) or far off the canal SIDEWAYS (lateral > detach). A tip aimed DEEP up
                // the canal (along large +, on-axis) is INSERTED, not withdrawn - so a long anal/vaginal canal aimed deep
                // no longer false-closes (the old straight-distance check read womb2's 204mm-deep anal tip as "pulled out").
                bool tipAttached;
                if (geo.aimedValid && _canalLen > 1e-4f)
                {
                    Vector3 trel = geo.aimedTipPos - _canalEntrance;
                    float tAlong = Vector3.Dot(trel, _canalAxis);
                    float tLat = (trel - _canalAxis * tAlong).magnitude;
                    _tipDist = trel.magnitude;
                    tipAttached = tAlong > -tipDetach && tLat <= tipDetach;
                }
                else { _tipDist = -1f; tipAttached = true; }
                _tipDetached = !tipAttached;
                // ENTRY-DETACH (build 429): treat the penis as WITHDRAWN when its entry/base marker k_f_dan_entry swings
                // OFF the canal axis. BP keeps the AIMED tip up in the womb even after you pull out (so tip-detach above
                // misses it), but for a penis whose entry is NOT NC-pinned to this womb's vagina the entry tracks the
                // penis BASE -> on withdrawal the base swings off-axis (this scene: ~5mm on-axis inserted -> ~65mm pulled
                // out). DIRECTIONAL like tip-detach but only the SIDEWAYS (lateral) component: being BELOW the mouth ALONG
                // the canal is normal insertion (hard rule), so along is NOT gated. An NC-pinned entry stays at the
                // vagina (lateral ~0) -> never false-closes. Only gates when a paired entry exists.
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
                // Sensitivity scales the depth needed to engage: sens>=1 -> EngageEps (unchanged); sens<1 -> needs
                // deeper insertion (a display womb won't react to a shallow/near penis). sens=0 already handed off above.
                float depthGate = EngageEps + Mathf.Max(0f, 1f - sens) * 0.30f;
                // LATERAL gate on the VAGINA-PAIRED path: BP depth in a paired vagina is NOT enough - the penis must
                // ALSO be near THIS womb's canal. Without it, a 2nd/display womb seated near ANOTHER womb's active penis
                // engaged off BP depth alone (penisTipLat 80mm+, the penis is in the other womb). latGate = the womb's
                // oriented half-width (same basis as containment) scaled by sensitivity; _bpLateral<0 (no shaft pose)
                // keeps the old pairing fallback. 'contained' already implies lateral<=gate, so this only newly gates
                // the vagina-paired-ALONE case -> low BP_Sensitivity (or even the default) now shuts a display womb up.
                // Same correction as the containment width above: the canal tube, not the mesh bounds.
                // Its own comment says it exists to reject a penis 80mm+ away that is in ANOTHER womb -
                // at 117mm it could not do that job at all.
                float latGate = CanalTipHalfWidth();
                bool vaginaNear = _vaginaPaired && (_bpLateral < 0f || _bpLateral <= latGate);
                _vaginaFarLat = _vaginaPaired && !contained && _bpLateral >= 0f && _bpLateral > latGate;
                // ENGAGE-GATE trace. The pause between BP visibly bending the shaft and the rings
                // starting is this gate: depthGate is compared against BP's RAW depth, which is a
                // fraction of the penis's own length, so a lowered BP_Sensitivity delays engagement by
                // a distance that grows with the penis. Printed on the engage edge so the actual
                // sensitivity in a scene can be read instead of inferred.
                // The post-constraint read is a REQUIREMENT, not a preference: without it the penis and
                // the canal are from different instants and there is no comparison to make. Apply nothing.
                //
                // The LEVEL is not the same question as the behaviour. While a scene loads or a body
                // reload settles, the read is legitimately not there yet and the right answer is to wait
                // a frame, so an error there is noise in the one file people attach to a bug report. It
                // stays an error when it persists, which is the case worth acting on.
                if (!_bpPostValid && LiquidWobbleMPBPlugin._logger != null && Time.unscaledTime >= _postMissLogNext)
                {
                    _postMissLogNext = Time.unscaledTime + 2f;
                    string why = "CloXray: womb '" + WombTag() + "' - the post-constraint read of '"
                        + (_pairedName ?? "?") + "' failed, so the penis and the canal are from different"
                        + " epochs and cannot be compared. Driving NOTHING this frame.";
                    if (AutoBodyReveal.SceneLoading || AutoBodyReveal.BodyReloadSettling)
                        LiquidWobbleMPBPlugin._logger.LogInfo(why + " A load or body reload is still settling,"
                            + " so this is expected and clears itself.");
                    else LiquidWobbleMPBPlugin._logger.LogError(why);
                }
                // Without a canal there is no frame to compare anything against, and the containment
                // tests below silently pass when _canalLen is 0 - so the womb would engage on BP depth
                // alone. Free-H already refuses this a few lines down; Studio now does too. The missing
                // clo_canal_entry marker already LogErrors once per womb, so the reason is on record.
                bool gateOpen = _bpPostValid && _canalLen > 1e-4f && _bp.depth > depthGate
                              && (contained || vaginaNear) && tipAttached && entryAttached;
                // Both lateral gates, side by side. latGate is built from WombBulbRadius - the womb MESH's
                // extent, which includes the tubes and the ovaries spreading sideways - so it is far wider
                // than the canal it is meant to describe. canalGate is the tube half-width the same way the
                // toy path and the outsideWomb veto measure it. Printing both says whether the loose one is
                // actually carrying any decision, instead of tightening it on the strength of the argument.
                float canalGate = CanalTipHalfWidth();
                if (gateOpen && !_wasEngaged && LiquidWobbleMPBPlugin.CfgDebugLog)
                    LiquidWobbleMPBPlugin._logger?.LogInfo(
                        "ENGAGE '" + WombTag() + "' sens=" + sens.ToString("F2")
                        + " depthGate=" + depthGate.ToString("F3")
                        + " bpDepth=" + _bp.depth.ToString("F3")
                        + " tipLat=" + (_bpLateral >= 0f ? (_bpLateral * 1000f).ToString("F1") + "mm" : "?")
                        + " latGate=" + (latGate * 1000f).ToString("F1")
                        + "mm canalGate=" + (canalGate * 1000f).ToString("F1") + "mm"
                        + " realIn=" + realIn + " contained=" + contained + " vagNear=" + vaginaNear
                        // Identity, so which physical womb this is can be CONFIRMED rather than inferred
                        // from a name. Names are not unique in a Studio scene and the numbering
                        // speaks ("womb2") need not match the object called clo_xraywomb2.
                        + " entrance=" + _canalEntrance.ToString("F3")
                        + " seat=" + (_vaginaPaired ? "vaginaPaired" : "unseated")
                        + " -> engaged after " + (depthGate * _bp.baseLen * 1000f).ToString("F1")
                        + "mm of BP compression");
                // THE case that decides whether the loose gate matters: engaged, but the tip is outside the
                // CANAL tube. If this never prints on a scene that works, tightening changes nothing there.
                if (gateOpen && _bpLateral > canalGate && LiquidWobbleMPBPlugin.CfgDebugLog
                    && Time.unscaledTime >= _latLogNext)
                {
                    _latLogNext = Time.unscaledTime + 1f;
                    // This penis can be dead-centre in this canal and reacting correctly while the reading says
                    // 49mm is a bad READING, not a bad scene - and the reaction is BP compression, which is
                    // independent of this geometry, so both can be true at once. Everything the number is
                    // built from is printed so the fault can be located rather than guessed: which male the
                    // womb is paired to, the canal frame it measures against, and the raw tip.
                    Vector3 relDbg = _bp.tipPos - _canalEntrance;
                    float alongDbg = Vector3.Dot(relDbg, _canalAxis);
                    // SAME FRAME. Comparing the canal frame against a penis position taken from BP-RAW
                    // meant comparing two different instants - BP-RAW is throttled to 2s, this to 1s, and
                    // the scene is animated - which made one womb read 4.5 degrees in one run and 28 in the
                    // next. The shaft is right here, so the comparison is done now, on this frame's numbers.
                    // The SHAFT POLYLINE, not the base->tip chord. BP BENDS the penis, so the chord is not
                    // the path it takes: on a bent shaft the chord passes tens of mm from a canal the tip is
                    // properly inside, and any "entrance is 48-121mm off the penis line" reading taken
                    // from the chord is suspect for exactly that reason. A womb used as an ANAL orifice is
                    // entered at a different approach angle, so it bends most and mis-reads worst.
                    // The angle likewise uses the LAST segment - the shaft's direction AT the tip - rather
                    // than the whole-shaft chord, since that is what enters the canal.
                    // FROM THE PENETRATION POINT. BP's shaft runs STRAIGHT from k_f_dan_entry to the tip;
                    // everything before that point is BP bending him into position and says nothing about
                    // where he is inside the canal. So the shaft's base-to-tip direction, and the distance
                    // to the nearest shaft bone, are both meaningless here - most of those bones are on the
                    // approach. entryPos is the entry, and entry->tip is the only direction that describes
                    // the penetration.
                    //
                    // Three facts, each independently checkable against the scene:
                    //   mouthToEntry  the womb's canal mouth against the point he actually enters at. If
                    //                 the womb is placed on that orifice these coincide; if it reads tens
                    //                 of mm, the womb's mouth is not where the penetration happens.
                    //   dirVsAxis     the penetration direction against the canal's own axis.
                    //   insertion     how far along that direction the tip has travelled from the entry.
                    Vector3 entryP = _bp.entryPos;
                    bool haveEntry = entryP != Vector3.zero;
                    float mouthToEntry = -1f, dirVsAxis = -1f, insertion = 0f;
                    if (haveEntry)
                    {
                        mouthToEntry = (entryP - _canalEntrance).magnitude;
                        Vector3 pen = _bp.tipPos - entryP;
                        if (pen.sqrMagnitude > 1e-8f)
                        {
                            Vector3 pdir = pen.normalized;
                            dirVsAxis = Vector3.Angle(pdir, _canalAxis);
                            insertion = pen.magnitude;
                        }
                    }
                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb '" + WombTag() + "' is engaged with its "
                        + (realIn ? "deepest shaft point " : aimedIn ? "aimed tip " : "closest shaft bone ")
                        + (_bpLateral * 1000f).ToString("F1") + "mm off the canal centreline, outside the canal tube ("
                        + (canalGate * 1000f).ToString("F1") + "mm) - engaged through "
                        + (contained ? "containment (shaft allowance " + (CanalHalfWidth() * 1000f).ToString("F1") + "mm)"
                                     : "the vagina pairing (gate " + (latGate * 1000f).ToString("F1") + "mm)") + "."
                        + " male='" + (_bp.srcName ?? "?") + "'"
                        + " tipPos=" + _bp.tipPos.ToString("F3")
                        + " entrance=" + _canalEntrance.ToString("F3")
                        + " axis=" + _canalAxis.ToString("F3")
                        + " canalLen=" + (_canalLen * 1000f).ToString("F1") + "mm"
                        + " tipAlong=" + (alongDbg * 1000f).ToString("F1") + "mm"
                        + " tipLatDirect=" + ((relDbg - _canalAxis * alongDbg).magnitude * 1000f).ToString("F1") + "mm"
                        + " || FROM-ENTRY: entryPos=" + (haveEntry ? entryP.ToString("F3") : "MISSING")
                        + " mouthToEntry=" + (mouthToEntry >= 0f ? (mouthToEntry * 1000f).ToString("F1") + "mm" : "?")
                        + " dirVsCanalAxis=" + (dirVsAxis >= 0f ? dirVsAxis.ToString("F1") + "deg" : "?")
                        + " insertion=" + (insertion * 1000f).ToString("F1") + "mm"
                        + " || wombItem=" + transform.position.ToString("F3")
                        // The AUTHORED canal-entry bone, against the entrance the plugin derived. These are
                        // two independent things: clo_canal_entry is baked into the mesh and cannot be
                        // re-aimed, while _canalEntrance comes from the plugin's own canal detection. If
                        // they disagree, the detection is wrong and every canal-frame number in this log is
                        // built on it. This is the one input that has had nothing to check it against.
                        + " canalBone=" + (CanalEntryBone != null ? CanalEntryBone.position.ToString("F3") : "MISSING")
                        + " boneVsDerived=" + (CanalEntryBone != null
                            ? ((CanalEntryBone.position - _canalEntrance).magnitude * 1000f).ToString("F1") + "mm" : "?")
                        + " (tipLatDirect is the TIP itself)");
                }
                _wasEngaged = gateOpen;
                if (gateOpen)
                {
                    engaged = true;
                    _traceSrc = "BP";
                    _bpDroveUntil = Time.unscaledTime;   // keeps the geometry path off a BP-driven womb
                    // THE branch that actually runs, and the one place womb scale never reached.
                    // _bp.visualDepth is BetterPenetration's own stroke fraction and FullDepthIn is a
                    // constant, so this ratio is blind to the womb - measured, it read 0.686 at scale 0.7
                    // and 0.686 at 1.0 from an insertion of 73.3mm and 72.6mm into canals of 54.1mm and
                    // 77.3mm. Identical depth from the same penis in tubes of two different lengths.
                    // Everything scaled by the length reference - the cap, tNorm - lives on the
                    // CANAL branch below, which is the fallback and was not running.
                    //
                    // FullDepthIn is a fraction of the womb's depth, authored against a womb at scale 1, so
                    // it has to be read in this womb's scale: a 0.7 womb is 30% shorter and the same stroke
                    // is proportionally deeper into it. Exactly 1.0 at scale 1, so nothing tuned there moves.
                    // ABSOLUTE, not per-length. BP's depth divides out his own size, so the same number
                    // means a different physical distance for every penis; scaling by his real base
                    // length restores it. Stale baseLen after a character rescale is safe: BP RENDERS
                    // with the same cached length it reports, so this tracks what is on screen either way.
                    float lenRatio = (RefDanLength > 1e-4f && _bp.baseLen > 1e-4f)
                                   ? _bp.baseLen / RefDanLength : 1f;
                    float bpNorm = (FullDepthIn > 1e-4f) ? _bp.visualDepth / FullDepthIn : _bp.visualDepth;
                    // TIP FRONT, not the bone. The depth is taken from the deepest cm_j_dan* shaft bone,
                    // which sits inside the glans - measured at 17mm behind the mesh front - so every
                    // reading was short by that and rings and dome both reacted late by it. Measured per
                    // male so it follows his scale and mesh. The dome curve needs no retune: with
                    // BONE = 110.4*d - 10.46, adding ahead/110.4 to d makes FRONT = 110.4*d - 10.46 in the
                    // new d, the same coefficients, so the geometry derivation repeats unchanged.
                    targetNorm = bpNorm * lenRatio / CanalScaleY();
                    // Through HER vagina the depth zero is hers, so where this womb sits and how long its canal
                    // is decide when the penis reaches the mouth, each ring and the cervix.
                    if (_seatVagina != null && _vaginaPaired) targetNorm = SeatedDepth(targetNorm);
                    _traceLenRatio = lenRatio;
                    _bpVaginaMain = !contained;   // diag: reacting from BP/vagina alone - the bones never threaded the canal
                }
                // BetterPenetration only reports depth for an orifice it recognises: anywhere else it parks
                // lastDanDistance at the base length, i.e. compression zero, so a womb held somewhere other
                // than the vagina or anus never engages off BP. When the shaft physically threads THIS canal,
                // take the depth from where the tip actually sits along the canal instead. Girth still comes
                // from BP (collider radii, which it reports regardless), and the tip pose is already cached,
                // so this is a projection and nothing more.
                // STUDIO ONLY. In the main game the womb sits in her vagina, so BetterPenetration classifies the
                // penetration and its compression drives the rings exactly as before - this path would never be
                // reached there, and leaving it live only risks an untested route into the H reaction.
                else if (MainGameWomb.IsStudio && !engaged && contained && _bp.found && _bp.hasPose && _canalLen > 1e-4f && tipAttached && entryAttached)
                {
                    // MEASURE ALONG THE LINE THE PENIS ACTUALLY TRAVELS. penis_target is authored far down the
                    // canal line as an aim marker - BP runs the shaft from the entry marker toward it and stops
                    // where the character's own BP settings put the tip. So the direction is entrance ->
                    // penis_target, while the usable RANGE stays the canal's own length: a tip at the cervix is
                    // depth 1, and the far marker never inflates it.
                    // MEASURE ALONG BP'S OWN LINE. BP holds the shaft perfectly straight between its entry and
                    // end markers, and both of those are constrained to this womb (mouth and penis_target), so
                    // that line IS the canal line - whatever the marker's own axis reads. Measuring along it
                    // makes the depth immune to any lateral disagreement between the marker and the penis.
                    Vector3 bpEntry = _grabEntryW;
                    Vector3 bpEnd = BPBridge.GetEndWorld(_pairedName);
                    Vector3 canalDir = _canalAxis;
                    Vector3 measureFrom = _canalEntrance;
                    if (bpEntry != Vector3.zero && bpEnd != Vector3.zero)
                    {
                        Vector3 ln = bpEnd - bpEntry;
                        if (ln.sqrMagnitude > 1e-6f)
                        {
                            // ...but ONLY while it still points along the canal. The line is the canal line
                            // because both markers are held at this womb; a hand-made NodesConstraint
                            // can pin k_f_dan_end anywhere, and `cf_j_spine02 -> k_f_dan_end` measured 50
                            // degrees off (dot 0.62). Projecting onto that tilt reports ~40mm of depth for a
                            // tip sitting AT the mouth, so the canal jumped half-open the moment he entered
                            // and again as he left. Below the threshold the womb's own entrance and axis are
                            // used instead - they cannot be re-aimed by anyone, and depth through the mouth
                            // is then continuous from zero. A properly aimed pair reads ~1.0 and is unaffected.
                            const float BpLineMinDot = 0.906f;   // 25 degrees
                            Vector3 dir = ln.normalized;
                            if (Vector3.Dot(dir, _canalAxis) >= BpLineMinDot)
                            {
                                canalDir = dir;
                                // adopt BP's DIRECTION, but only its ORIGIN when the entry marker
                                // really is on the mouth. The old comment here asserted it "sits on the
                                // mouth by constraint" - true when the plugin aims it, false the moment the
                                // scene aims k_f_dan_entry at an object of its own, which the marker guard exists to
                                // protect. Measured in a real scene: entry constrained to
                                // p_koi_stu_kihon32c_00, and depth read 43mm deeper than the tip actually
                                // was, so the womb opened its whole length while the penis was half in.
                                // The womb's own mouth cannot be re-aimed by anyone, so it stays the ruler.
                                float entryOff = (bpEntry - _canalEntrance).magnitude;
                                if (entryOff <= 0.020f) measureFrom = bpEntry;
                                else if (!_entryOffWarned)
                                {
                                    _entryOffWarned = true;
                                    LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb '" + name + "' - k_f_dan_entry sits "
                                        + (entryOff * 1000f).ToString("F0") + "mm from this womb's canal mouth (you have aimed it at "
                                        + "something of your own). Measuring depth from the MOUTH instead, so the rings follow the "
                                        + "penis rather than that offset. Nothing to fix in the scene.");
                                }
                            }
                            else if (!_bpLineRejectedOnce)
                            {
                                _bpLineRejectedOnce = true;
                                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb '" + name
                                    + "' - BetterPenetration's entry->end line runs "
                                    + (Mathf.Acos(Mathf.Clamp(Vector3.Dot(dir, _canalAxis), -1f, 1f)) * Mathf.Rad2Deg).ToString("F0")
                                    + " degrees off this canal, so it is NOT the canal line and depth is measured against the"
                                    + " womb's own entrance instead. A NodesConstraint pinning k_f_dan_end away from the womb"
                                    + " (or a womb rotated away from him) does this.");
                            }
                        }
                    }
                    Vector3 trel = _bp.tipPos - measureFrom;
                    float tAlong = Vector3.Dot(trel, canalDir);
                    float tLat = (trel - canalDir * tAlong).magnitude;
                    // The tip measured against the CANAL's own entrance and axis. This is the authority on
                    // whether he is inside at all: the depth above is a projection onto a marker line, and a
                    // projection says how far ALONG, never whether it is IN. Both vetoes below read these.
                    Vector3 crel = _bpPost.tipPos - _canalEntrance;   // same epoch as the canal - no fallback
                    float cAlong = Vector3.Dot(crel, _canalAxis);
                    float cLat = (crel - _canalAxis * cAlong).magnitude;
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
                    // Straight canal fraction: the tip is now measured along BP's own line from its entry marker,
                    // so the depth is the honest one and needs no scaling. (It was divided by FullDepthIn while
                    // the measurement under-read; with that fixed the same division put the rings ahead of the
                    // tip.) The ring formula applies its own OpenLead anticipation on top. CAPPED because
                    // overshoot past 1.0 drives the womb displacement.
                    float tNorm = tAlong / _canalLen;
                    // the cap is a limit on how far PAST the cervix tracking continues, and it is
                    // expressed in womb-lengths - so it has to be read in THIS womb's length. The same
                    // penis in a 0.6 womb genuinely travels 1/0.6 as many womb-lengths, and clamping that
                    // to the scale-1 number threw away exactly the overshoot that drives the displace:
                    // measured, it pinned StretchTargetAt to 20 + 0.13*110 = 34 of a possible 100 at ANY
                    // scale, which is why a penis visibly through a 0.6 womb still read as barely
                    // displaced. Dividing by the scale leaves scale 1 bit-identical (s = 1.0).
                    // FLOOR. Scaling the whole cap by 1/s sends it BELOW DepthEnd on a womb scaled
                    // UP (1.2 -> 0.94 < 0.97), and then the clamp is no longer limiting overshoot at all,
                    // it is cutting into the ring range and the cervix ring can never reach full. The
                    // reach part (DepthEnd + OpenLead) is what the rings need and must never be clipped;
                    // only the 0.10 of overshoot past it is scale business.
                    float reach = LiquidWobbleMPBPlugin.CfgDepthEnd + LiquidWobbleMPBPlugin.CfgOpenLead;
                    tNorm = Mathf.Min(tNorm, Mathf.Max(reach, (reach + 0.10f) / CanalScaleY()));
                    // No lateral re-test here: `contained` above is already the authoritative "the shaft
                    // threads THIS canal" check, and re-testing against an axis the tip may not follow only
                    // shuts the womb down.
                    // The canal is a ~6mm-radius tube. A penis threading the womb's outer volume but running
                    // well beside that tube still looks "inside the womb" through the x-ray, yet only its
                    // travel ALONG the canal can drive the rings - so the reaction reads late. Say so once.
                    if (!_offCanalWarned && tLat > 0.020f && tNorm > depthGate)
                    {
                        _offCanalWarned = true;
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb '" + name + "' - the penis passes "
                            + (tLat * 1000f).ToString("F0") + "mm BESIDE this womb's canal centreline (the canal is a narrow tube). "
                            + "It may look inside the womb, but it is not in the canal, so the rings only follow how far it advances "
                            + "along the canal. Move/rotate the womb so its canal mouth sits where he enters and the canal points along him.");
                    }
                    // CANAL VETO. Depth is measured along BP's entry->end line, which is only the canal line
                    // while BOTH markers are held at this womb. A hand-made NodesConstraint can pin
                    // k_f_dan_end anywhere - measured in this scene, `cf_j_spine02 -> k_f_dan_end` put the
                    // line 52 degrees off the canal (axisDot 0.62), and projecting a WITHDRAWN tip onto that
                    // tilt read +15mm of depth while the tip was 57mm BELOW the mouth. The canal stayed open
                    // for the whole pullout.
                    // The womb's own entrance and axis cannot be re-aimed by anyone, so they get the last
                    // word on the one question that matters: is the tip inside at all.
                    // The slack is a FIXED 5mm, not the tip's radius. Measured: girthTip is ~17mm, so a
                    // radius of slack left the whole band from 0 to 17mm below the mouth reading as
                    // engaged - and in exactly that band the tilted marker line swings `along` back up to
                    // ~40mm, which is the canal re-opening a moment after it finished closing. 5mm is
                    // enough to stop a tip resting IN the opening from flickering shut, and nothing like
                    // enough to call a withdrawn one inserted.
                    bool belowMouth = cAlong < -0.005f;
                    // OUTSIDE THE WOMB ALTOGETHER. Depth is a projection ALONG the canal, so a penis
                    // running PARALLEL to it but off to one side still projects as deep - measured in this
                    // scene, a tip 50mm off the centreline read 85mm up an 77mm canal and pinned the drive
                    // at the cap: max ring opening AND max stretch, on a penis that is not in the womb at
                    // all. BP was stretching him toward `cf_j_spine02`, 273mm from the vagina, so his shaft
                    // ran up beside the womb rather than through it.
                    // A tip further from the centreline than the womb is WIDE cannot be inside it, whatever
                    // the projection says. Measured lateral extent, not a constant, so it follows the mesh,
                    // the scale and the stretch. Logged whenever it fires, with the numbers, because this is
                    // the one veto that could silence a womb that ought to be reacting.
                    // THE CANAL TUBE, not the whole womb. WombBulbRadius is the mesh's own bounds, which
                    // include the tubes and the ovaries spreading sideways - measured, it is wider than
                    // 62mm, so it can never reject anything. The CANAL is a narrow tube, and the rings open
                    // it to fit THIS penis: so its half-width is his own tip radius, plus a little slack for
                    // the wall. A tip further off the axis than that is not in the tube it is supposed to be
                    // filling - it is beside it. Derived from a measured girth, not a chosen number, so it
                    // follows the penis, the womb scale and the stretch.
                    float canalR = CanalTipHalfWidth();
                    bool outsideWomb = cLat > canalR;
                    if (outsideWomb && LiquidWobbleMPBPlugin._logger != null && Time.unscaledTime >= _outsideLogNext)
                    {
                        _outsideLogNext = Time.unscaledTime + 2f;
                        LiquidWobbleMPBPlugin._logger.LogInfo("CloXray: womb '" + name + "' - the tip is "
                            + (cLat * 1000f).ToString("F0") + "mm off the canal centreline; the canal tube is only "
                            + (canalR * 1000f).ToString("F0") + "mm wide (his tip radius " + (_bp.girthTip * 1000f).ToString("F0")
                            + "mm + 10mm). He is BESIDE the canal, not in it, so the rings and the stretch stay put."
                            + " Depth along the canal reads " + (cAlong * 1000f).ToString("F0") + "mm.");
                    }
                    if (belowMouth && LiquidWobbleMPBPlugin._logger != null && Time.unscaledTime >= _vetoLogNext)
                    {
                        _vetoLogNext = Time.unscaledTime + 2f;
                        LiquidWobbleMPBPlugin._logger.LogInfo("CloXray: womb '" + name + "' - the marker line reads "
                            + (tAlong * 1000f).ToString("F0") + "mm of depth, but the tip is " + (-cAlong * 1000f).ToString("F0")
                            + "mm BELOW the canal mouth. Treating it as withdrawn.");
                    }
                    // NO GEOMETRY FALLBACK. This branch used to engage off the tip's projection along
                    // the canal whenever BP reported no compression depth. Measured: on a FULLY WITHDRAWN
                    // penis it reported a constant 1.130 for 77 consecutive frames and drove the dome back
                    // to 60+ and climbing, and a single-frame appearance mid-stroke snapped it 20 -> 94.
                    // The projection reads deep for a shaft running BESIDE the canal, so it cannot answer
                    // "is he in it". BP's depth is the only source; if BP has none, nothing is driven.
                    // LONE-WOMB PATH. A womb BP does not recognise as an orifice gets no compression
                    // depth from it, so without this it cannot react at all. Every clause is a veto:
                    //   realIn        the REAL shaft bones thread THIS canal - not the aimed tip, which
                    //                 stays in the canal while the penis is withdrawn
                    //   depth<=Eps    BP reports no compression ANYWHERE, not merely below this womb
                    //                 sensitivity gate, so it cannot undercut a BP_Sensitivity setting
                    //   quiet         BP has not driven THIS womb for GeomQuiet seconds
                    //   !belowMouth   the tip is not below the entrance
                    //   !outsideWomb  the tip is inside the canal tube, not running beside it
                    // The depth is the tip against the womb own entrance and axis, converted into BP
                    // units so the same curve applies. Marker lines are not used: they are what this
                    // plugin own constraints pin to the womb.
                    // The branch above already carries 1.1.3's gate verbatim - Studio, not engaged, contained,
                    // BP found with a pose, canal known, tip and entry attached - so only what 1.1.3 lacked
                    // is tested here. Nothing widens it.
                    bool bpQuiet = _bp.depth <= EngageEps && Time.unscaledTime >= _bpDroveUntil + GeomQuiet;
                    if (realIn && bpQuiet && !belowMouth && !outsideWomb)
                    {
                        float unit = Mathf.Max(RefDanLength * FullDepthIn * CanalScaleY(), 1e-6f);
                        // Through the same canal-shape correction the BP path uses, so a canal a hand-set
                        // blendshape stretched opens at the same place whichever path is driving.
                        float geomNorm = (ShapedAdvance(cAlong) + GeomMouthOffset) / unit;
                        // depthGate is a fraction of the PENIS's own length (BP raw depth), so compare
                        // it against a length, not against a womb-unit depth: geomNorm already carries the
                        // +0.0948 mouth offset, which alone cleared the gate at any sens >= ~0.72 and made
                        // it inert. cAlong is the advance PAST the mouth, in metres, which is what the gate
                        // was always meant to test. targetNorm still comes from geomNorm - the curve is
                        // unchanged, only the trigger.
                        if (_bp.baseLen > 1e-4f ? cAlong > depthGate * _bp.baseLen : geomNorm > depthGate)
                        {
                            // A real containment cannot read this deep. 1.1.3 clamped silently at 1.13 and
                            // that clamp is exactly what a withdrawn penis pegged, so this one reports.
                            if (geomNorm > 2f)
                            {
                                geomNorm = 2f;
                                if (LiquidWobbleMPBPlugin._logger != null && Time.unscaledTime >= _vetoLogNext)
                                {
                                    _vetoLogNext = Time.unscaledTime + 2f;
                                    LiquidWobbleMPBPlugin._logger.LogWarning("CloXray: womb '" + name
                                        + "' - the lone-womb depth read past the end of the canal ("
                                        + (cAlong * 1000f).ToString("F0") + "mm along a "
                                        + (_canalLen * 1000f).ToString("F0") + "mm canal) and was capped."
                                        + " The measurement is wrong, not the womb.");
                                }
                            }
                            engaged = true;
                            targetNorm = geomNorm;
                            _bpGeomDepth = true;
                            _traceSrc = "GEOM";
                            if (LiquidWobbleMPBPlugin.CfgDebugLog && Time.unscaledTime >= _geomLogNext)
                            {
                                _geomLogNext = Time.unscaledTime + 2f;
                                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: womb '" + name
                                    + "' - BP reports no compression for it, but the shaft threads its canal ("
                                    + (cAlong * 1000f).ToString("F0") + "mm in, " + (cLat * 1000f).ToString("F0")
                                    + "mm off centre). Driving it from the shaft position: depth "
                                    + geomNorm.ToString("F3") + ".");
                            }
                        }
                    }
                    // SCALE TRACE (temporary, diagnostic log only). Everything the depth and width decision
                    // is made from, in one throttled line, so a run at scale 1.0 and the same run at 0.9 can
                    // be diffed. What matters: does canalLen actually follow the item scale (it should -
                    // it is restLocalLen x boneY), and does tNorm jump rather than fall as the penis leaves?
                    // The rings saturate at the tNorm cap, so a jump reads on screen as "suddenly all open".
                    // girth->width has NO scale term at all (RefGirth is a constant), so watch gScale too.
                }
                if (engaged)
                {
                    // Ring WIDTH tracks the penis's STATIC TIP girth (girthTip / RefGirth) - the glans is what reaches
                    // and dilates the uterus; the whole-shaft average over-counted the fat base the uterus never
                    // touches (build 361 GIRTH-DIAG proved per-ring profiling can't differentiate - the colliders all
                    // sit below the entrance, so every ring sees the tip anyway). Deliberately does NOT include BP's
                    // deep-press fattening (girthFactor, which climbs to ~1.2 on cervix contact): that press DISPLACES/
                    // stretches the womb (via depth -> StretchTarget), it must not widen the canal further (folding it
                    // in ballooned the rings to ~max on a hard push). gFactor stays in the reading for the gFac= debug.
                    // WOMB SCALE. girthScale drives BLENDSHAPES, which deform in the womb's own local
                    // space and are then rendered at its world scale - so the same weight on a womb scaled
                    // to 0.8 opens the canal 20% less in world terms, while the penis has not shrunk at
                    // all. Divide the reference by that scale and the weight rises to keep the ABSOLUTE
                    // opening matched to the penis: a smaller womb reacts proportionally more, which is
                    // what it has to do to fit the same thing.
                    // The main game solves this a better way - it measures how many mm of canal a unit of
                    // girthScale actually opens (_openEfficiency, a world measure) and inverts it - but
                    // that whole path is behind `if (!MainGameWomb.IsStudio)` and never runs here.
                    // World scale, not local: Studio compensates an item's local scale on attach (measured:
                    // local 1.871 against her cf_n_height 0.535 -> world 1.001), so this reads 1.0 for a
                    // normal attach on a character of ANY size and only moves when the item is scaled.
                    // NO FABRICATED WIDTH. girthTip 0 used to fall through the 0.4 clamp and open the
                    // canal to a made-up 40%, hiding a broken girth read behind a plausible-looking womb.
                    // The other three girth sites already guard on > 1e-4f; this one now matches them.
                    if (_bp.girthTip > 1e-4f)
                        girthScale = Mathf.Clamp(GirthForOpening(_bp.girthTip), 0.4f, MaxGirthScale);
                    else
                    {
                        engaged = false;   // no girth = no width to open to; apply NOTHING
                        if (Time.unscaledTime >= _girthErrNext)
                        {
                            _girthErrNext = Time.unscaledTime + 2f;
                            LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb '" + WombTag()
                                + "' - BetterPenetration reported no tip girth for '" + (_bp.srcName ?? "?")
                                + "', so the canal opening cannot be sized. Driving NOTHING this frame.");
                        }
                    }
                    // NO womb-scale factor here. lengthScale answers one question - how long is
                    // this penis against the reference - and womb scale is not part of it. Folding
                    // scale in here (either direction) is wrong, because the real
                    // scale problem is elsewhere: the tNorm cap at line ~2000 and the uncompensated
                    // per-ring girth. Back to the one job.
                }
            }

            // ALSO react to a DynamicBoneCollider on a bottle/toy pushed in (a KKPE BP collider). READ-ONLY; the
            // scan + canal bake are 2s-cached. Depth tracks the collider's deeper SURFACE point, so a collider
            // sitting at/below the entrance reads as OUTSIDE -> closed, same as a withdrawn penis.
            // This runs REGARDLESS of the penis path, and the womb reacts to the
            // DEEPER of penis vs toy - so a toy inserted deeper than (or instead of) a barely-engaged penis drives
            // the womb instead of being ignored. Wrapped so a bad bake / reflection hiccup can NEVER abort LateUpdate.
            _colLateral = -1f; _colName = null; _colPerRing = false; _toyDrives = false;
            // Off-screen skip (perf, scales with womb count): a NON-engaged womb that is NOT visible has no reaction
            // a viewer could see, so skip the per-frame collider scan for it. `engaged` here is the penis-path result,
            // so a penis-engaged (mid-thrust) womb always runs even if its bounds get culled - no freeze. The ring loop
            // below keeps smoothing/decaying, so a womb returning on-screen re-opens smoothly rather than snapping.
            if (LiquidWobbleMPBPlugin.CfgReactColliders && !ignoreColliders && (engaged || _smr == null || _smr.isVisible))
            {
                try
                {
                    EnsureCanal();
                    // Precompute each ring's normalized depth so the collider scan can report the THICKEST collider
                    // reaching each ring (two toys -> each ring follows the biggest thing at its level).
                    int rn = _ringIdx.Length;
                    if (_ringDepths == null || _ringDepths.Length != rn) { _ringDepths = new float[rn]; _colRingRadius = new float[rn]; }
                    for (int ri = 0; ri < rn; ri++) _ringDepths[ri] = rn == 1 ? DepthStart : Mathf.Lerp(DepthStart, DepthEnd, (float)ri / (rn - 1));
                    Vector3 ctip, cbase; float crad, cdrive, clat; string cname;
                    if (_canalLen > 1e-4f &&
                        ColliderBridge.TryReadNearCanal(_canalEntrance, _canalAxis, LiquidWobbleMPBPlugin.CfgColliderRange,
                                                        LiquidWobbleMPBPlugin.CfgColliderInCanal, _canalLen, LiquidWobbleMPBPlugin.CfgColliderMaxRadius,
                                                        WombBulbRadius(), LiquidWobbleMPBPlugin.CfgColliderNameForGame,
                                                        _ringDepths, _colRingRadius,
                                                        indepRings ? RingGapWorld() * 1.5f * Mathf.Clamp01(indepRingsW / 100f) : 0f,
                                                        out ctip, out cbase, out crad, out cdrive, out clat, out cname))
                    {
                        // A canal FRACTION: 0 at the mouth, 1 at the top. Studio's targetNorm is in BP
                        // depth units (mouth 0.0948, cervix 0.795), so the two are not comparable until
                        // this is converted - the same expression the lone-womb path uses. Free-H needs no
                        // conversion: its targetNorm IS a canal fraction (see the ExternalStrokeMM line).
                        // TWO quantities, deliberately. cfrac is the canal FRACTION - 0 at the mouth - and
                        // answers "is it inserted at all", the same in both games. cdepth is whatever is
                        // comparable with targetNorm: the fraction in Free-H, BP depth units in Studio.
                        // They must not be conflated: on the BP scale the MOUTH already reads 0.0948, ten
                        // times EngageEps, so gating insertion on cdepth would pass a toy merely resting
                        // against the orifice.
                        float cfrac  = cdrive / _canalLen;
                        float cUnit  = Mathf.Max(RefDanLength * FullDepthIn * CanalScaleY(), 1e-6f);
                        float penisFrac = (targetNorm > 0f && MainGameWomb.IsStudio && _canalLen > 1e-6f)
                                        ? (targetNorm * cUnit - GeomMouthOffset) / _canalLen
                                        : targetNorm;
                        _colLateral = clat; _colName = cname;
                        // TOY-GEOM. Depth here is a projection along the canal, and a projection says how
                        // far ALONG, never whether it is IN - the same thing that made the old penis
                        // geometry path read deep for a shaft running beside the canal. The lateral
                        // distance is what separates "in the tube" from "swept past it", so it is printed
                        // beside the depth rather than inferred from the shape of the curve.
                        if (LiquidWobbleMPBPlugin.CfgDebugLog && cfrac > EngageEps
                            && Time.unscaledTime >= _colGeomLogNext)
                        {
                            _colGeomLogNext = Time.unscaledTime + 0.25f;
                            LiquidWobbleMPBPlugin._logger?.LogInfo("TOY-GEOM '" + WombTag() + "' toy='" + (cname ?? "?")
                                + "' along=" + (cdrive * 1000f).ToString("F1") + "mm lateral="
                                + (clat * 1000f).ToString("F1") + "mm radius=" + (crad * 1000f).ToString("F1")
                                + "mm canal=" + (_canalLen * 1000f).ToString("F1") + "mm -> canalFrac "
                                + cfrac.ToString("F3") + " penisClaim "
                                + penisFrac.ToString("F3") + " (canal fractions) (lateral gate is "
                                + (LiquidWobbleMPBPlugin.CfgColliderInCanal * 1000f).ToString("F0") + "mm)");
                        }
                        // A toy that IS in the canal but reads shallower than the penis path loses, and the
                        // penis path then keeps the womb open on a depth that may have nothing to do with
                        // this womb. That is the one case worth naming, so it is reported.
                        if (cfrac <= penisFrac && cfrac > EngageEps
                            && LiquidWobbleMPBPlugin.CfgDebugLog && Time.unscaledTime >= _colSkipLogNext)
                        {
                            _colSkipLogNext = Time.unscaledTime + 2f;
                            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: womb '" + name + "' - toy '"
                                + (cname ?? "?") + "' reads depth " + cfrac.ToString("F3")
                                + " but the penis path already claims " + penisFrac.ToString("F3")
                                + ", so the toy does NOT drive this womb.");
                        }
                        if (cfrac > EngageEps && cfrac > penisFrac)   // only take over when the toy reaches DEEPER than the penis
                        {
                            engaged    = true;
                            _traceSrc  = "TOY:" + (cname ?? "?");
                            targetNorm = cfrac;
                            girthScale = Mathf.Clamp(GirthForOpening(crad), 0.4f, MaxGirthScale);   // same womb-scale term as the penis path
                            // Per-ring radii are a STUDIO feature: in H the colliders slide with the
                            // hips in bent poses (reverse cowgirl), and the "thickest collider at this
                            // ring's depth" picks up random passers-by - the top ring visibly popped
                            // open at random. H uses the stable uniform tip girth instead.
                            _colPerRing = MainGameWomb.IsStudio;   // _colRingRadius[] holds the per-ring thickest collider -> ring loop uses it
                            _toyDrives  = MainGameWomb.IsStudio;   // ToyRingDelay applies
                            _bp = new BPBridge.Reading {
                                found = true, depth = cfrac, visualDepth = cfrac,
                                girthBase = crad, girthTip = crad, girthFactor = 1f, baseLen = 0f,
                                tipPos = ctip,
                                tipDir = (ctip - cbase).sqrMagnitude > 1e-8f ? (ctip - cbase).normalized : Vector3.up,
                                hasPose = true };
                            // STABLE key - cname is the reaching COLLIDER's name and rotates with
                            // the stroke (dan104/106/108...), which reset the shared latch every few frames
                            // ("LATCHED" spam, width oscillating 37.5-40.6mm). One key per H scene; the
                            // 15-read median mixes the dan colliders into a stable representative width.
                            // ONLY when BP is NOT giving a girth. When BP drives, its girthTip is
                            // the real VISUAL penis (measured 42.5mm), but the reaching collider here is
                            // often the penis's OWN THIN dan collider (22.6mm) - latching that clamps the
                            // canal to ~25mm and the 42mm shaft pokes through, since the penis is bigger than
                            // channel"). BP's latch (above) owns the girth whenever BP has one; the
                            // collider latch is only for the genuine no-BP toy path.
                            if (!_bpHadGirthThisFrame) LatchGirth(crad * 2000f, "H-colliders");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    if (!_colWarned) { LiquidWobbleMPBPlugin._logger?.LogWarning("WombExpand collider-react failed (disabled for this womb): " + ex.Message); _colWarned = true; }
                }
            }

            // ENGAGE-TRACE, for the mid-size-girl case where the womb is not reacting with the penis in the
            // canal - reacts sometimes randomly"): whenever the GAME says penetrated but the womb's
            // geometric gate disagrees for >1.5s, print every gate component so the flickering input
            // is readable from the log instead of guessed.
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

            // MAIN GAME: engagement GRACE + canal boost. H detection (the collider lateral gate, bent
            // reverse-cowgirl poses) flickers frame-to-frame; a dropout slammed the rings/stretch shut
            // and the re-detect snapped them open - the "womb suddenly jumps" look. Once engaged, hold
            // the last drive values through a short grace window so only a sustained withdrawal closes.
            if (!MainGameWomb.IsStudio)
            {
                // GAME-STATE GATE: the H motion state must positively say
                // PENETRATED. Missing flag used to SKIP this gate (a silent fallback) - now no flag =
                // no engagement (the missing-flag LogError already fired; the womb never reacts on a
                // guess).
                if (!HasPenetratedFlag || !ExternalPenetrated) { engaged = false; _hEngHold = 0f; }
                // HARD GATE on the REAL penis endpoint (danEnd): no readable
                // danEnd = no engagement. Skipping it when danEnd was unreadable let a whole session
                // run on dead BP agent handles with the womb engaging off the legacy path while the
                // penis was visibly out.
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
                    _hEngDepth = targetNorm; _hEngGirth = girthScale;
                }
                else if (Time.unscaledTime < _hEngHold)
                {
                    engaged = true;
                    targetNorm = _hEngDepth; girthScale = _hEngGirth;
                }
                // Depth = the REAL penis end along the canal (ExternalStrokeMM, overridden in OnPreCullCanal
                // from danEnd; 0 when the tip is out). Always use it in H (no >0 gate - 0 must close the rings).
                if (engaged && _canalLen > 1e-4f)
                    targetNorm = Mathf.Clamp(ExternalStrokeMM * 0.001f / _canalLen, 0f, 1.5f);
                // H-only canal opening boost, stretch-coupled: baseline wider for the penis girth, and
                // as the womb stretches the canal narrows back toward the stretched value - elongating
                // tissue pulls taut. (Tuned: boost 1.5 relaxed -> 1.25 fully stretched.)
                if (engaged)
                {
                    // the CanalBoost multipliers are GONE (config + code). They only ever acted in the
                    // brief pre-latch window - the grounded solver below overwrites girthScale outright
                    // - so they were inert knobs pretending to be tuning.
                    // GROUND the opening to the MEASURED mesh, superseding the RefGirth model +
                    // womb-scale compensation, which mis-predicted the real opening on scaled characters
                    // (CANAL-FIT: canal opened 33.9mm vs the model's ~43mm -> penis poked through 5mm).
                    // _openEfficiency (measured mm-diameter per girthScale unit, from the baked mesh)
                    // allows solving directly for the girthScale that ACTUALLY opens the canal to the
                    // penis + margin, at ANY character scale AND at the current stretch (the bake sees
                    // the stretch-thinned tube), so this subsumes both the scale-compensation and the
                    // stretch boost. Model path is the fallback until the first efficiency measure.
                    // opening target = LATCHED per-male diameter (constant; latched at pair
                    // time, pre-penetration - see the read site) x margin / measured mesh efficiency.
                    // The live per-frame girth NEVER drives the rings after latch (it spikes on BP
                    // rebuilds, first contact and fast strokes and was slamming the opening to max).
                    // SEED openEff from the scale law before it is ever used, so the very first
                    // girthScale is correct and the canal opens STRAIGHT to size - no balloon while a
                    // feedback measure crawls up from the ring-ramp transient, which showed as the canal going extra big
                    // then reduced"). openEff = ringResponse × the womb's WORLD scale (bake s × her kokan
                    // lossy, since BakeMesh bakes lossyScale): ringResponse≈53.6 mm-dia/unit at unit world
                    // scale (measured: openEff settles ~44 at s=1.374 × herKokanLossy=0.598). Carry wins if
                    // this session already measured a real value; the measure above keeps fine-tuning.
                    if (_openEfficiency <= 1e-3f && MainGameWomb.CurrentlyRebound && MainGameWomb.RebindS > 1e-3f)
                    {
                        float herLossy = (_canalBone != null && _canalBone.parent != null) ? _canalBone.parent.lossyScale.y : 1f;
                        _openEfficiency = (s_carryResp > 1e-3f ? s_carryResp : 53.6f) * MainGameWomb.RebindS * herLossy;
                    }
                    if (_openEfficiency > 1e-3f)
                    {
                        // latch BP's girth HERE, from the live _bp - the paired-read latch at the
                        // top of the frame never fired (bpLatch=0 while liveBPgirthTip=38-42: the womb's
                        // _pairedName girth-read is flaky, but _bp.girthTip is reliably the real penis at
                        // THIS point, every frame). This is inside `engaged`, so it only accumulates while
                        // the penis is actually in - exactly when the girth matters.
                        if (_bp.girthTip > 1e-4f) LatchBPGirth(_bp.girthTip * 2000f, _bp.srcName);
                        // BP-owned latch WINS (the shared s_diaMM may be a thin collider); fall to
                        // the collider latch only when there's no BP girth, then the live read.
                        float diaMM = s_bpDiaMM > 0f ? s_bpDiaMM
                                    : (s_diaMM > 0f ? s_diaMM
                                    : (_bp.girthTip > 1e-4f ? _bp.girthTip * 2000f : 0f));
                        // account for girth INCREASE at the limit. The base is the latched
                        // median (settle-once, frozen); the ONLY live term is a RISE - when BP inflates
                        // the girth at deep penetration, the canal follows UP, but it never falls (no
                        // shrink flicker). Rise smoothed (60mm/s) so a BP-rebuild spike can't slam it.
                        // on a big male the canal opened too wide for that penis size - GIRTH-DRIVE
                        // showed canalOpening 65.2mm for a 44mm latch: the rise was a RATCHET - it kept
                        // the all-time maximum live girth. The interplay that blew it up: the fit LENGTH-
                        // squishes a big penis, BP's danGirthSquish fattens it at the deepest press, and that
                        // one-frame peak (~57mm) held the canal at 65mm forever while the visual shaft
                        // relaxed back to ~44mm. Keep the "account for girth INCREASE at the
                        // limit") but let it RELAX: rise 60mm/s toward the live girth, fall 12mm/s when the
                        // press is over, floored at the latched median, capped at 125% of it - a deep press
                        // still widens the canal promptly, and it breathes back down with the stroke.
                        float liveNow = _bp.girthTip > 1e-4f ? _bp.girthTip * 2000f : 0f;
                        float riseCap = diaMM * 1.25f;
                        if (_girthRiseMM <= 0f) _girthRiseMM = diaMM;
                        float riseTgt = Mathf.Clamp(Mathf.Max(liveNow, diaMM), diaMM, riseCap);
                        _girthRiseMM = _girthRiseMM < riseTgt
                            ? Mathf.Min(riseTgt, _girthRiseMM + 60f * Time.deltaTime)
                            : Mathf.Max(riseTgt, _girthRiseMM - 12f * Time.deltaTime);
                        diaMM = Mathf.Max(diaMM, _girthRiseMM);
                        // the canal can end up just a little too tight for a big squished
                        // male): the latch is the TIP collider median, but a length-squished penis runs a
                        // FATTER visual shaft (BP danGirthSquish=0.8 volume conservation). Widen by the
                        // physically-derived fatten - ≈ half the squish × BP's 0.8 girth-squish ≈ 0.4 -
                        // capped +15%. Reference male (ratio≈1) keeps the approved 1.15-margin look.
                        float fitR = BPInnerTargetPin.FitSquishRatio;
                        // SYMMETRIC - a stretched penis (fitR>1, e.g. the small male stretched to
                        // the 1.25 cap) runs a THINNER shaft, so the same term narrows the opening.
                        diaMM *= Mathf.Clamp(1f + 0.4f * (1f - fitR), 0.85f, 1.15f);
                        if (diaMM > 0f)
                        {
                            float needed = (diaMM * WidthMargin) / _openEfficiency;
                            girthScale = Mathf.Clamp(needed, 0.4f, MaxGirthScale);
                        }
                    }
                    _lastGirthScale = girthScale;
                    // GIRTH DIAGNOSTIC, for the penis-thicker-than-womb case on a small char: show every girth
                    // input + the resulting opening, so it is visible whether the LATCH is too thin (thin
                    // collider) or the openEff is mis-measured. Throttled, Debug-gated.
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
            // the rings close LATER (follow-through), while opening still tracks the penis going in.
            // ENGAGE GRACE (Studio). At an animation's loop point the penis can leave the gate for a
            // frame or two. engaged=false forces tgtDepth to 0, the depth filters race down and the
            // dome collapses then climbs back - the jump at the end of a looping animation. Holding
            // the last engaged depth briefly lets a flicker ride through; a real pull-out outlasts the
            // grace and still collapses normally. The Free-H path has its own, longer hold.
            _traceGrace = false;
            if (MainGameWomb.IsStudio)
            {
                // Girth rides with the depth, the same pair the Free-H hold above carries. Holding the
                // depth alone re-engaged the womb on a girthScale that had already reverted to its
                // neutral 1.0, so a toy leaving the canal WIDENED the rings (52 of 100 against the 21 its
                // girth asks for) instead of letting them close.
                // The per-ring flag rides along too. Without it the grace re-engaged the womb at the held depth
                // while _colPerRing had already gone false with the failed scan - so for the length of the grace
                // the ring loop silently fell back to the CUMULATIVE rule and opened every ring up to that held
                // depth. Pulling a toy out flashed the rings open just as it left: reported, and exactly this.
                // Carried, independent rings keep their own rule through the grace, reading the zeroed per-ring
                // radii, so the rings close on the damping instead of popping.
                if (engaged) { _engHold = Time.unscaledTime + EngageGrace; _engDepth = targetNorm; _engGirth = girthScale; _engToy = _toyDrives; _engPerRing = _colPerRing; }
                else if (Time.unscaledTime < _engHold)
                {
                    engaged = true; targetNorm = _engDepth; girthScale = _engGirth; _toyDrives = _engToy; _colPerRing = _engPerRing; _traceGrace = true;
                }
            }
            float tgtDepth = engaged ? targetNorm : 0f;

            // ONE velocity estimate, shared by both depth copies. A first-order filter settles a fixed
            // distance behind a ramp - v divided by its rate - so aiming at where the tip WILL be cancels
            // that lag without opening the cutoff and letting BP's noise back in. Rise only: predicting a
            // withdrawal would close the canal while the shaft is still inside it.
            float depthVel = 0f;
            if (!float.IsNaN(_cleanPrev) && Time.deltaTime > 1e-5f)
                depthVel = Mathf.Max(0f, (tgtDepth - _cleanPrev) / Time.deltaTime);
            _cleanPrev = tgtDepth;

            // RINGS lead by 1/DepthSmoothing, the exact lag of their own filter. They had no lead at all,
            // so they settled v/DepthSmoothing behind the tip - 5mm at a 60mm/s entry, enough that the
            // tip was already inside before the entrance ring moved. Capped at half of OpenWidth so a
            // ring can never lead by a whole band.
            float ringRise = DepthSmoothing * RingRiseBoost;
            float ringLead = (ringRise > 1e-4f)
                           ? Mathf.Min(depthVel / ringRise, OpenWidth * 0.5f) : 0f;
            float ringIn = tgtDepth + ringLead;
            _traceRingLead = ringLead;

            float sm = (ringIn >= _depth) ? ringRise : CloseSmoothing;
            float k = sm > 0f ? 1f - Mathf.Exp(-sm * Time.deltaTime) : 1f;
            // The rise filter is there to tame BP's compression signal, which is noisy. A depth measured
            // straight off the shaft colliders is a position read and needs no filtering going IN - with it,
            // the rings trailed the tip by a ring or two early in a stroke and only caught up once the depth
            // saturated. Withdrawal keeps its follow-through either way.
            // NO unfiltered bypass for the geometry depth. It was justified as "a position read needs
            // no filtering going IN"; the lead above is what actually cancels the rise lag, and it does so
            // at the same cutoff. The bypass turned a one-frame source disagreement into a 74-weight snap
            // (BP branch reading 0.41 while the geometry branch read 1.13 for the same instant). A source
            // change is now smoothed like any other input.
            _depth = Mathf.Lerp(_depth, ringIn, k);

            // the displace's own depth. Identical on the way IN (shares k, and the same
            // geometry-depth bypass), StretchCloseBoost times faster on the way OUT. Without this block
            // this block missing - the field was read but never written, so the displace saw depth 0 and
            // sat at zero. Regression, not a tuning problem.
            // DESPIKE. Measured, BP's depth is clean apart from ~0.3% single-frame excursions, so a
            // 3-tap median is the right shape: it rejects those and passes a stroke ramp unchanged,
            // where an N-frame average would cost (N-1)/2 frames of lag to remove noise that is not
            // there. The rings keep the raw signal; only the displace copy is cleaned.
            // NO despike. A causal median cannot tell a genuine fast move from a spike: after a dropped
            // frame the real motion IS large, and the median froze the depth at the old value while the
            // tip was 0.33 deeper - which is what let the tip reach the dome. Measured noise is 3
            // spike-and-return events in 924 steady-state frames, and the lead clamp already bounds what
            // one bad frame can do, so the rejection cost far more than it saved.
            // Same depth as the rings. Referencing the penis FRONT here pushed the dome about 10mm
            // ahead of the penis: StretchStart/Max/Overshoot were derived from measurements taken
            // against the tip BONE, so they already carry that distance - adding it again counted it
            // twice. The mesh-reach measurement stays, as a diagnostic.
            float cleanDepth = tgtDepth;

            // LEAD. A first-order filter always trails its input, which is what let the tip reach the
            // dome mid-stroke. Aim at where the tip WILL be instead of where it is. Rise only, so a
            // withdrawal still lingers, and clamped so one bad frame cannot throw the dome forward.
            // This copy leads by its own, shorter time constant: it runs StretchRiseBoost times faster
            // than the rings, so it trails by that much less and needs that much less prediction.
            float leadDepth = cleanDepth + Mathf.Min(depthVel * StretchLeadTime, StretchLeadMax);
            _traceClean = cleanDepth; _traceLead = leadDepth;

            // Its OWN selector: borrowing the rings' k meant the rate was chosen by comparing against
            // _depth, then applied to _depthFast. The two disagree after a withdrawal (this copy falls
            // faster, so it sits lower), and in that window a thrust rose at the CLOSE rate.
            float smFast = (leadDepth >= _depthFast) ? DepthSmoothing * StretchRiseBoost
                                                     : CloseSmoothing * StretchCloseBoost;
            float kFast = smFast > 0f ? 1f - Mathf.Exp(-smFast * Time.deltaTime) : 1f;

            _depthFast = Mathf.Lerp(_depthFast, leadDepth, kFast);
            _traceTgtDepth = tgtDepth; _traceSmFast = smFast;   // STRETCH TRACE input

            // MAIN GAME intent depth: project the fed intent tip on the canal, smoothed with the same
            // rise/fall constants as the real depth. Drives the H stretch through the Studio curve.
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
            float ringD = _depth;   // the depth the ring timing reads
            if (_toyDrives && ringD > ToyStretchKnee) ringD = ToyStretchKnee + (ringD - ToyStretchKnee) / ToyStretch;
            for (int i = 0; i < n; i++)
            {
                if (_ringIdx[i] < 0) continue;

                float ringDepth = n == 1 ? DepthStart : Mathf.Lerp(DepthStart, DepthEnd, (float)i / (n - 1));
                // PER-RING girth. Two sources:
                //  • PENIS: this ring lines up with the penis part (_depth - ringDepth) back from the tip (cervix=thin
                //    tip, entrance=thicker shaft), growing with insertion.
                //  • COLLIDERS: the THICKEST collider reaching this ring's depth (two toys -> each ring follows the
                //    biggest one at its level).
                // Falls back to the uniform girthScale when neither per-ring source is available.
                float ringGScale = girthScale;
                if (engaged && _bp.girthPos != null)
                {
                    float lr = GirthAtDistFromTip(Mathf.Max(0f, _depth - ringDepth) * _canalLen);
                    // through GirthForOpening, like the uniform girthScale - this per-ring value
                    // OVERRIDES it whenever BP supplies a girth profile, so leaving it on the raw RefGirth
                    // meant the womb-scale compensation was bypassed in the normal engaged case and the
                    // rings under-opened on a small womb (54 * 0.80 = 43 of 100 at 0.6; 72 with this).
                    if (lr > 0f && RefGirth > 1e-5f) ringGScale = Mathf.Clamp(GirthForOpening(lr), 0.4f, MaxGirthScale);
                }
                else if (engaged && _colPerRing && _colRingRadius != null && i < _colRingRadius.Length && _colRingRadius[i] > 0f && RefGirth > 1e-5f)
                {
                    ringGScale = Mathf.Clamp(GirthForOpening(_colRingRadius[i]), 0.4f, MaxGirthScale);   // same compensation for the collider path
                }
                // + the rest-width make-up, so the ABSOLUTE canal width matches the penis at any
                // womb scale instead of only the added opening matching. Exactly 0 at scale 1, negative on
                // a womb scaled UP (its rest tube is already wider), and only while engaged - an idle womb
                // must not sit open just because it is small.
                float baseW = (i == 0 ? EntranceWeight : (i == n - 1 ? CervixWeight : RingWeight)) * ringGScale;
                if (engaged) baseW += RestWidthMakeup();
                baseW = Mathf.Clamp(baseW, 0f, Mathf.Min(MaxRingWeight, 100f));   // never exceed 100 (KKPE latches a >100 blendshape write)
                // Body rings (V2..V5) reach full AHEAD by OpenLead as the tip passes them. The ENTRANCE (V1)
                // opens GRADUALLY over EntranceOpenWidth so it shows intermediate states during shallow
                // insertion rather than snapping open on contact. It now carries the same OpenLead as the
                // others - without it the entrance was 24% open when the tip reached the mouth, the one ring
                // with no anticipation at all. Leading the ramp is not the same as the old window that sat
                // below depth 0 and hit full by 0.04: full still arrives 11.6mm in, so the ramp survives.
                // EVERY ring, entrance included, opens on the same rule: a window of OpenWidth ending
                // OpenLead before the ring's own position. The entrance used to ramp over
                // EntranceOpenWidth from depth 0 instead, which put it full at almost exactly V2's
                // position - the two finished 1.9mm apart and read as one movement. On the shared rule
                // they are a ring-spacing apart (24mm) and the rings sequence as the tip passes them.
                // The entrance keeps its own RATE handling (EntranceOpenScale) - only the timing is shared.
                // The ENTRANCE opens over a longer stroke than the body rings - the shape of that ring
                // reads better ramping in than snapping. Same rule, wider window: EntranceOpenWidth
                // instead of OpenWidth, with the ring's position shifted by the difference so the
                // window START stays put and only its END moves later.
                float ringW = (i == 0) ? EntranceOpenWidth : RingRampWidth;
                float ringAt = ringDepth + (ringW - OpenWidth);
                if (_toyDrives && i < ToyRingDelay.Length) ringAt += ToyRingDelay[i];
                float target = engaged ? OpenFraction(ringD, ringAt - OpenLead, ringW) * baseW : 0f;
                // INDEPENDENT RINGS: the ring follows what actually SPANS it, not how deep the deepest thing has
                // reached, so a ball opens the ring it is passing through and the rings behind it close again.
                // Only the collider path can answer that (_colRingRadius is filled per ring by the scan above);
                // a BP penis keeps the normal rule, which for a shaft is the same answer anyway - it spans every
                // ring behind its tip. The ring's WIDTH already comes from that same per-ring radius.
                if (indepRings && _colPerRing && _colRingRadius != null && i < _colRingRadius.Length)
                    target = (engaged && _colRingRadius[i] > 0f) ? baseW : 0f;

                // Rate-limited OPEN (eases in instead of snapping) + close at the dampening rate. The
                // ENTRANCE opens AND closes its scale-x slower (smaller geometric move per weight unit).
                float ringOpenStep = (i == 0) ? openStep / Mathf.Max(EntranceOpenScale, 0.01f) : openStep;
                // OPEN: rate-limited ease-in. CLOSE: exponential ease-OUT (no snap). The ENTRANCE closes with a
                // longer time-constant (EntranceCloseScale) so its smaller per-weight move still looks the same speed.
                float ringCloseK = (i == 0) ? 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(dampTime * EntranceCloseScale, 0.02f)) : closeK;
                if (target >= _ringReaction[i]) _ringReaction[i] = Mathf.MoveTowards(_ringReaction[i], target, ringOpenStep);
                else                            _ringReaction[i] = Mathf.Lerp(_ringReaction[i], target, ringCloseK);

                // Override (strength scales the reaction; manual is only honoured at strength 0 above).
                _smr.SetBlendShapeWeight(_ringIdx[i], Mathf.Clamp(_ringReaction[i] * strength, 0f, 100f));   // never write >100 (it latches/sticks in KKPE)
            }
            // RING-GIRTH TRACE (Diagnostic log, Studio, engaged, 1/s). The sampled distance below is
            // (_depth - ringDepth) * _canalLen, and those two terms are NOT in the same unit in Studio:
            // _depth is BP depth (1.0 = RefDanLength*FullDepthIn = 110.4mm x womb scale) while ringDepth
            // is used elsewhere as a canal FRACTION (BPBridge multiplies it by canalLen). Printing the
            // distance beside the collider profile it indexes answers the only question that matters
            // before anything is changed: whether the wrong distance and the right one land on DIFFERENT
            // colliders. If both fall outside the profile's ends, GirthAtDistFromTip clamps to the same
            // radius either way and the whole discrepancy is invisible on this model - no fix needed.
            if (engaged && MainGameWomb.IsStudio && _bp.girthPos != null && LiquidWobbleMPBPlugin.CfgDebugLog
                && Time.unscaledTime >= _girthTraceNext)
            {
                _girthTraceNext = Time.unscaledTime + 1f;
                var sb = new System.Text.StringBuilder();
                sb.Append("CloXray RING-GIRTH '").Append(name).Append("'  depth=").Append(_depth.ToString("F3"))
                  .Append("  canal=").Append((_canalLen * 1000f).ToString("F1")).Append("mm  unit=")
                  .Append((RefDanLength * FullDepthIn * CanalScaleY() * 1000f).ToString("F1")).Append("mm");
                for (int gi = 0; gi < _ringIdx.Length; gi++)
                {
                    float rd = _ringIdx.Length == 1 ? DepthStart
                             : Mathf.Lerp(DepthStart, DepthEnd, (float)gi / (_ringIdx.Length - 1));
                    float back = Mathf.Max(0f, _depth - rd);
                    float asIs = back * _canalLen;                                        // what the code samples
                    float asBP = back * RefDanLength * FullDepthIn * CanalScaleY();        // the BP-unit reading
                    sb.Append("\n  ring").Append(gi).Append(" at ").Append(rd.ToString("F2"))
                      .Append(": samples ").Append((asIs * 1000f).ToString("F1")).Append("mm -> r=")
                      .Append((GirthAtDistFromTip(asIs) * 1000f).ToString("F2"))
                      .Append("mm   | BP-unit ").Append((asBP * 1000f).ToString("F1")).Append("mm -> r=")
                      .Append((GirthAtDistFromTip(asBP) * 1000f).ToString("F2")).Append("mm");
                }
                sb.Append("\n  profile (distance from tip -> radius):");
                for (int gk = 0; gk < _bp.girthPos.Length && gk < 24; gk++)
                    sb.Append(' ').Append(((_bp.girthPos[gk] - _bp.tipPos).magnitude * 1000f).ToString("F0")).Append("->")
                      .Append((_bp.girthRad != null && gk < _bp.girthRad.Length ? _bp.girthRad[gk] * 1000f : -1f).ToString("F1"));
                LiquidWobbleMPBPlugin._logger?.LogWarning(sb.ToString());
            }

            if (_stretchIdx >= 0)
            {
                // Displace travel scales WITH the womb: womb_displace moves the cervix a fixed FRACTION of
                // the canal at full weight, and both scale together - so express the travel as a fraction
                // of the (scaled) canal length. This keeps "dome tracks tip" correct at any womb scale (a
                // const mm over-displaced a scaled-up womb -> visible elongation).
                // the tip POKES INTO/THROUGH the dome when this is wrong. The ratio is the estimate
                // of how far the blendshape ACTUALLY moves the cervix per unit weight. The old 0.71 was
                // measured on the ORIGINAL womb_displace; the v2 REPLACE travels LESS per weight, so 0.71
                // over-estimated the dome's travel and the drive under-displaced (dome fell short of the tip
                // = poke). Recalibrated to 0.5 - derived from the poke at push=1.15: the tip landing IN the
                // dome there means the real travel ≈ overshoot/(stretchW/100)/canal ≈ 0.5. NOTE: this is an
                // open-loop constant; if it pokes on some poses and over-encloses on others (real travel
                // varying with depth), replace it with a MEASURED travel (baked cervix-band displacement per
                // applied weight) - the self-calibrating version. Ratio 0.71 assumed v1 displace magnitude.
                // the measured fraction when the probe got one, the historical 0.5 otherwise.
                // MEASURED, 2026-09-04, this mesh, six spawns: 55.6mm at weight 100 = 0.719 +/- 0.001 of
                // real travel varies with the mesh and its scale.
                // weight, it travels 0.719 where v1 measured 0.71. The 0.5 was reverse-derived from a poke
                // at push=1.15 - a symptom, not a measurement - and was 30% low, so the contact term
                // over-drove by 0.719/0.5 = 1.44x whenever it was in force. NO FALLBACK now: without the probe's
                // number the weight-to-travel mapping is unknown and the contact term is not computed.
                float travelFrac = _displaceTravelFrac;
                float DisplaceTravelMM = travelFrac * _canalLen * 1000f;
                float st;
                if (MainGameWomb.IsStudio)
                {
                    // STUDIO: displace from depth overshoot, scaled by BP penis length (the classic path).
                    // No lengthScale here any more: the length correction moved to the DEPTH above,
                    // ahead of the 2.0 clamp on lengthScale and the 100 clamp on the written weight.
                    // Applied here it was erased by both in every measured case.
                    // A TOY reads a plain canal fraction while this curve is calibrated in BP depth units, so the
                    // same stroke stretched the womb ahead of a toy: at the cervix a toy asked for the full
                    // StretchMax where a penis asks for about two thirds, and the canal was pushed away before the
                    // toy got there. Convert the toy's depth into BP units (the mouth at GeomMouthOffset, the canal
                    // in RefDanLength*FullDepthIn) so the dome moves for a toy exactly when it moves for a penis.
                    float stretchDepth = _depthFast;
                    if (_toyDrives && _canalLen > 1e-6f)
                        stretchDepth = (_depthFast * _canalLen + GeomMouthOffset)
                                     / Mathf.Max(RefDanLength * FullDepthIn * CanalScaleY(), 1e-6f);
                    st = engaged ? StretchTargetAt(stretchDepth) : 0f;
                    // SCALE TRACE (Diagnostic log, 1/s, engaged only). Three builds of scale fixes
                    // did not move what is on screen, so stop reasoning about the chain and print it. If
                    // wombScale reads 1.00 on a womb he scaled to 0.6, every one of those fixes was a
                    // no-op and the scale never reaches this code at all - which is the first thing to
                    // rule out, not the last.
                    if (engaged && LiquidWobbleMPBPlugin.CfgDebugLog && Time.unscaledTime >= _scaleTraceNext)
                    {
                        _scaleTraceNext = Time.unscaledTime + 1f;
                        float sTr = CanalScaleY();
                        float reachTr = LiquidWobbleMPBPlugin.CfgDepthEnd + LiquidWobbleMPBPlugin.CfgOpenLead;
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray SCALE-TRACE '" + name + "'"
                            + "  wombScale=" + sTr.ToString("F3")
                            + "  itemLocalY=" + (transform != null ? transform.lossyScale.y.ToString("F3") : "?")
                            + "  canalLen=" + (_canalLen * 1000f).ToString("F1") + "mm"
                            // WHICH END MOVES when the womb is scaled. The intuitive model is that the
                            // penis tip holds its place against the CHARACTER while the tube gets shorter,
                            // which would raise depth by 1/s. The trace showed depth FALLING at 0.8, so one
                            // of these two moved and cancelled it - print both and stop inferring.
                            + "  mouth=" + _canalEntrance.ToString("F3")
                            + "  tip=" + _bp.tipPos.ToString("F3")
                            + "  tipAboveMouth=" + (Vector3.Dot(_bp.tipPos - _canalEntrance, _canalAxis) * 1000f).ToString("F1") + "mm"
                            + "  depth=" + _depth.ToString("F3")
                            // BP's own numbers, upstream of everything done to them here. depthUsed sat
                            // at a hard 1.130 while the tip travelled 42.8mm to 83.9mm, and 1.130 is
                            // 0.70/FullDepthIn - so the SIGNAL is flat, not the arithmetic here. BP squishes:
                            // visualDepth = T*S + (1-S)*depth, and with this character's DanOptions
                            // (T=0.2, S=0.6) that is 0.12 + 0.4*depth, compressing the range to 40%.
                            // Printing raw depth beside it separates the two possible causes:
                            //   raw VARIES, visual FLAT -> the squish mapping is eating the stroke (code)
                            //   raw FLAT               -> BP reports max compression the whole way, which
                            //                             is the aim driving the penis past the orifice
                            //                             and is a scene fix (sphere placement)
                            + "  bpRaw=" + _bp.depth.ToString("F3")
                            + "  bpVisual=" + _bp.visualDepth.ToString("F3")
                            + "  bpGirthFac=" + _bp.girthFactor.ToString("F3")
                            + "  cap=" + Mathf.Max(reachTr, (reachTr + 0.10f) / sTr).ToString("F3")
                            + "  girthScale=" + girthScale.ToString("F2")
                            + "  travelFrac=" + (_displaceTravelFrac > 0f ? _displaceTravelFrac.ToString("F3") : "UNMEASURED")
                            + "  restOverTravel=" + (_restOverTravel > 0f ? _restOverTravel.ToString("F3") : "-")
                            + "  stretchTarget=" + StretchTarget().ToString("F1")
                            + "  st=" + st.ToString("F1")
                            + "  ringReaction[0]=" + (_ringReaction != null && _ringReaction.Length > 0 ? _ringReaction[0].ToString("F0") : "?")
                            + "  ringReaction[last]=" + (_ringReaction != null && _ringReaction.Length > 0 ? _ringReaction[_ringReaction.Length - 1].ToString("F0") : "?")
                            + "  (these are the weights actually written to the blendshapes)");
                    }
                }
                else
                {
                    // MAIN GAME. BASE = the resting canal elongation (always on): the displace weight that
                    // realizes the +CfgHBaseStretchPct the depth math already baked into _canalLen, so the
                    // visible mesh matches the longer canal even with no penis. CONTACT adds on top when
                    // engaged, tracking the tip PAST the (elongated) cervix - 'Womb push response'
                    // multiplies it so deep thrusts visibly shove the womb (requested).
                    // Base-stretch displace weight = the % elongation as a fraction of the travel ratio
                    // (0.71) - scale-INDEPENDENT (both the elongation and the travel scale together).
                    // effective percent (config + the hard-wired 6mm down-extension) - same single
                    // source the canal-length formula uses, so the visible mesh matches the math.
                    // the RESTING womb read as too stretched - show only 70% of the base
                    // elongation while idle, full weight the moment the penis engages (existing smoothing
                    // eases the ramp). DEPTH MATH IS UNTOUCHED: _canalLen, the seat and the aim keep the
                    // full +6mm - this is a rest-pose VISUAL trim only, hard-wired (no new config).
                    const float RestStretchVisual = 0.7f;
                    float baseW = _baseStretchPctEff / 0.71f * (engaged ? 1f : RestStretchVisual);
                    float contact = 0f;
                    if (engaged)
                    {
                        if (LiquidWobbleMPBPlugin.CfgHAutoLength && _canalLen > 1e-4f)
                        {
                            // the press = the LARGER of the visible tip-past-cervix (danEnd leak)
                            // and the COMPRESSION BP's squish absorbed (raw L−d2−canal, fed by the pin).
                            // BP hides ~80-85% of the excess from danEnd - the womb was reacting only to
                            // the 2-3mm leak (stretchW 7-8, invisible) while the penis pressed 18mm+.
                            // EARLY ONSET - the dome starts yielding when the tip is within 7mm of
                            // the cervix instead of exactly at it, which triggers just slightly earlier;
                            // the compression path carries the same margin so the two stay continuous.
                            const float EarlyMM = 7f;
                            // push gradually, do not kick. The +EarlyMM bonus used to
                            // arrive as a STEP the frame compression first went positive - an instant
                            // ~15-weight jump the 26/s tracker turned into a visible kick. Ramp it in
                            // over the first EarlyMM of compression instead (slope 2 near onset, the
                            // same +EarlyMM beyond) - continuous at zero, the early feel kept.
                            float cmp = ExternalCompressMM;
                            // BASELINE-SUBTRACTED stroke term. A blunt suppression here instead
                            // which killed the LEGITIMATE deep-stroke dome push on a floored penis whose
                            // tip strokes from BELOW to ABOVE the cervix). Track the shallow-end tip depth
                            // (min, slow-decay) as the parked baseline: a floored penis PARKED past the
                            // cervix has a high baseline => ~0 net (no false constant displace);
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
                            if (travelFrac > 1e-4f)
                                contact = Mathf.Clamp(overMM / DisplaceTravelMM * 100f * LiquidWobbleMPBPlugin.CfgHWombPush, 0f, 100f);
                            else if (_rtTried && Time.unscaledTime >= _travelErrNext)
                            {
                                // _rtTried false = the probe simply has not run yet: it runs in onPreCull,
                                // one epoch after this LateUpdate, so the first frames of a spawn are
                                // expected and silent. Latched-and-still-unmeasured is a real failure.
                                _travelErrNext = Time.unscaledTime + 5f;
                                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb '" + WombTag()
                                    + "' - the displace-travel probe produced no measurement, so weight cannot be"
                                    + " converted into cervix travel. The contact term is NOT driven and no"
                                    + " assumed ratio is substituted.");
                            }
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
                    // no CONTACT stretch until the penis FIT is locked (the spawn-time uncut
                    // penis read stroke 112mm vs a 102mm canal -> stretch snapped to 100 then visibly
                    // decayed as the fit cut in - a fitting artifact, not an anatomical event). The
                    // resting BASE elongation stays on (it now carries the 6mm down-extension).
                    if (!ExternalFitLocked) st = Mathf.Min(st, baseW);
                    // DEBUG (Free-H bring-up): F1 slider forces the displace weight directly. Strip at release.
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
                    // H: FAST RISE / SLOWER FALL. Rise 26/s: the dome moves out ahead of an
                    // arriving tip (12/s lagged ~250ms and the penis tunneled in). Fall 7/s, so it is
                    // "less bouncy") - the dome keeps some displacement between strokes instead of
                    // snapping fully back each cycle; still relaxes in ~0.5s on a real withdrawal, far
                    // from the multi-second holds that made the womb expand at penis OUT.
                    // The PHYSICAL rule: the TIP holds the dome. While the tip is inside
                    // the dome (contact term active, st>0) the reaction FOLLOWS it tightly both ways
                    // (26/s) - release exactly tracks the withdrawal, never faster and never lagging.
                    // Only once the tip has LEFT the dome (st==0) does the residual relax at the calm,
                    // approved 7/s. (Replaces a stroke-velocity heuristic - physics, not tuning.)
                    // with auto-length OFF the womb is pushed too fast. 26/s was tuned
                    // for the fit path's precise tip signal (the tip holds the dome). The legacy
                    // signals (commanded stop depth / thrust envelope) are coarse, so the same speed
                    // reads as a shove - track gently there; the released-relax stays the approved 7/s.
                    float riseK = LiquidWobbleMPBPlugin.CfgHAutoLength ? 26f : 9f;
                    float trackK = (st > _stretchReaction) ? riseK : (st > 0.01f ? riseK : 7f);
                    _stretchReaction = Mathf.Lerp(_stretchReaction, st, 1f - Mathf.Exp(-trackK * Time.deltaTime));
                }

                // STRETCH TRACE. tgtDepth -> depthFast -> st -> reaction -> written, one line per frame
                // while engaged. knee = the depth at which StretchTargetAt stops clamping to the cap:
                // above it the curve is FLAT, so the weight cannot fall however fast the filters run.
                bool traceMoved = !(Mathf.Abs(_traceTgtDepth - _traceLastTgt) <= 0.003f
                                 && Mathf.Abs(st - _traceLastSt) <= 0.5f);   // NaN-safe: first frame always logs
                // Log through DISENGAGEMENT too (1s past the last engagement), so the pull-out contraction
                // can be compared against a shallow-but-engaged one. Gated on engaged alone, the
                // frames that answer that question were the ones never recorded.
                bool traceWindow = engaged || Time.unscaledTime < _engHold + 1.0f;
                if (traceWindow && LiquidWobbleMPBPlugin.CfgDebugLog && _stretchTraceLeft > 0 && traceMoved)
                {
                    // Only DEEP frames cost budget. Twice now a shallow warm-up burned the whole
                    // window before the deep stroke that was being investigated ever happened.
                    if (_traceTgtDepth > 0.8f) _stretchTraceLeft--;
                    _traceLastTgt = _traceTgtDepth; _traceLastSt = st;
                    float knee = (StretchOvershoot > 1e-4f)
                               ? 1f + (StretchCap - StretchMax) / StretchOvershoot : float.NaN;
                    LiquidWobbleMPBPlugin._logger?.LogInfo(
                        "STRETCH '" + WombTag() + "' tgtDepth=" + _traceTgtDepth.ToString("F3")
                        + " depthFast=" + _depthFast.ToString("F3")
                        + " knee=" + knee.ToString("F3")
                        + " st=" + st.ToString("F1")
                        + " reaction=" + _stretchReaction.ToString("F1")
                        + " written=" + Mathf.Clamp(_stretchReaction * strength, 0f, 100f).ToString("F1")
                        + " damp=" + dampTime.ToString("F3")
                        + " dt=" + Time.deltaTime.ToString("F4")
                        + " smFast=" + _traceSmFast.ToString("F1")
                        + " clean=" + _traceClean.ToString("F3")
                        + " lead=" + _traceLead.ToString("F3")
                        + " ringLead=" + _traceRingLead.ToString("F3")
                        + " lenR=" + _traceLenRatio.ToString("F2")
                        + " src=" + _traceSrc
                        + (_traceGrace ? " GRACE" : "")
                        + (engaged ? "" : " OUT")
                        + (_bpGeomDepth ? " geomDepth" : ""));
                }
                _smr.SetBlendShapeWeight(_stretchIdx, Mathf.Clamp(_stretchReaction * strength, 0f, 100f));
            }

            // Mouth leans toward the INCOMING PENIS, matching BP's own kokan-pull: BP translates the vagina along
            // (danEndTarget - entry) - the penis tip's offset from the canal. The SAME direction is used: the penis
            // tip's LATERAL offset from the canal axis (the along-canal/insertion part carries no lean info, so it's
            // stripped), projected into the womb-local frame. local +Z/-Z drive moundforward/moundback (fwd/back),
            // local +X/-X drive moundright/moundleft (the lateral pair M is authoring to match BP's sideways pull).
            // Symmetric DirReactWeight per direction; all reactions decay to 0 on withdrawal. (Left/right shapes are
            // live as of M's 2026-06-23 womb build; the lean is driven from the penis APPROACH ANGLE below.)
            if (_moundFwdIdx >= 0 || _moundBackIdx >= 0 || _moundLeftIdx >= 0 || _moundRightIdx >= 0)
            {
                float fwd = 0f, back = 0f, left = 0f, right = 0f;
                if (engaged && _bp.hasPose && DirReactWeight > 0.01f)
                {
                    // Lean toward the penis's APPROACH ANGLE, not its POSITION. (tipPos − entrance) made the mouth chase
                    // the tip's offset from the canal axis - on an off-seated overlay that's a big CONSTANT sideways offset
                    // (the womb sits off the penis path), and it was NORMALIZED, so ANY offset gave a full-strength lean →
                    // a mound stuck on permanently. The penis DIRECTION (base→tip = BP's kokan-pull vector) is offset-free:
                    // its component perpendicular to the canal axis is just how far the penis is ANGLED sideways, ~0 when it
                    // runs straight up the canal. NOT normalized → the lean is PROPORTIONAL to that angle (|lat| = sin θ).
                    Vector3 lat = _bp.tipDir - Vector3.Dot(_bp.tipDir, _canalAxis) * _canalAxis;
                    if (lat.sqrMagnitude > 1e-8f)
                    {
                        // NEGATED vs build 380: in-game BOTH axes (L/R and F/B) leaned the WRONG way, so flip the local
                        // lateral to lean TOWARD the penis. Then sqrt() shapes the response so even a SMALL off-axis angle
                        // already gives a clear lean (reacts FAST), easing toward the DirReactWeight ceiling so it "grows
                        // lower" overall - lower the 'Direction reaction' cfg for a smaller max.
                        Vector3 ld = -_smr.transform.InverseTransformDirection(lat);   // womb-local lean dir, sign-corrected
                        float lz = Mathf.Clamp(ld.z * 3f, -1f, 1f);
                        float lx = Mathf.Clamp(ld.x * 3f, -1f, 1f);
                        const float sideScale = 0.5f;   // L/R lean = HALF of fwd/back (per the human - sideways lean was too strong)
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

        // Ring opens as depth reaches it: closed until (ringDepth - width), full at/after ringDepth.
        // `width` is the per-ring opening softness (the entrance uses a wider window). The caller passes
        // (ringDepth - OpenLead) so the ring reaches full slightly AHEAD of the penis (anticipation).
        private float OpenFraction(float depth, float ringDepth, float width)
        {
            width = Mathf.Max(width, 1e-4f);
            float t = Mathf.Clamp01((depth - (ringDepth - width)) / width);
            return Mathf.SmoothStep(0f, 1f, t);
        }

        // Stretch ramps StretchStart..1 to StretchMax, then overshoot beyond 1, capped.
        private float StretchTarget() { return StretchTargetAt(_depth); }

        // The classic Studio displace curve, parametrized so the MAIN GAME can run the SAME curve on
        // its intent depth (where the tip would be if nothing yielded) instead of the pinned tip.
        private float StretchTargetAt(float depth)
        {
            float sMax = StretchMax, sStart = StretchStart, sOver = StretchOvershoot;
            if (depth <= sStart) return 0f;
            if (depth <= 1f)
            {
                float span = Mathf.Max(1f - sStart, 1e-4f);
                return Mathf.SmoothStep(0f, 1f, (depth - sStart) / span) * sMax;
            }
            return Mathf.Min(sMax + (depth - 1f) * sOver, StretchCap);
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
                // NO SILENT NO-OP: every displace write is gated `if (_stretchIdx >= 0)`, so a
                // missing channel would just stop the womb reacting with zero explanation. Say it loudly
                // (zipmod/DLL mismatch - they ship together).
                LiquidWobbleMPBPlugin._logger?.LogError($"CloXray: displace channel '{StretchBlendShape}' NOT FOUND on '{name}' — the womb will NOT react to deep strokes. The zipmod does not match this DLL; update [Clo]XrayWomb1.zipmod.");
            _strengthIdx  = FindBlendShapeIndex(mesh, StrengthBlendShape);
            _dampeningIdx = FindBlendShapeIndex(mesh, DampeningBlendShape);
            _ignoreCollidersIdx = FindBlendShapeIndex(mesh, IgnoreCollidersShape);
            _indepRingsIdx      = FindBlendShapeIndex(mesh, IndependentRingsShape);
            _sensIdx      = FindBlendShapeIndex(mesh, SensitivityBlendShape);
            _moundFwdIdx  = FindBlendShapeIndex(mesh, MoundForwardShape);
            _moundBackIdx = FindBlendShapeIndex(mesh, MoundBackShape);
            _moundLeftIdx  = string.IsNullOrEmpty(MoundLeftShape)  ? -1 : FindBlendShapeIndex(mesh, MoundLeftShape);
            _moundRightIdx = string.IsNullOrEmpty(MoundRightShape) ? -1 : FindBlendShapeIndex(mesh, MoundRightShape);
            return true;
        }

        /// The channel that collapses the uterus, leaving the plain canal an anal womb is. Hand-set, so Studio
        /// saves nothing about it by itself - the scene controller records and restores it.
        internal const string CollapseShape = "remove_womb";

        /// Read a hand-set blendshape by name (nothing in the plugin drives these). -1 when the mesh has no such
        /// channel, which is how the caller tells "off" from "not there".
        internal float HandShape(string shapeName)
        {
            if (_smr == null || _smr.sharedMesh == null) return -1f;
            int idx = FindBlendShapeIndex(_smr.sharedMesh, shapeName);
            return idx >= 0 ? _smr.GetBlendShapeWeight(idx) : -1f;
        }

        /// Write one. The anal setup key uses it to collapse the uterus, leaving the plain canal an anus needs.
        /// False when the channel is not on this mesh - the caller reports that rather than working around it.
        internal bool SetHandShape(string shapeName, float weight)
        {
            if (_smr == null || _smr.sharedMesh == null) return false;
            int idx = FindBlendShapeIndex(_smr.sharedMesh, shapeName);
            if (idx < 0) return false;
            _smr.SetBlendShapeWeight(idx, Mathf.Clamp(weight, 0f, 100f));
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

        /// <summary>World position of the womb canal entrance (the item's own clo_cf_j_kokan bone).
        /// Used by AutoBodyReveal to match a womb to the nearest character's vagina.</summary>
        public Vector3 EntranceWorld()
        {
            // Exact prefixed name: the ship-together zipmod always carries it, and an exact match can
            // never pick up a character's plain cf_j_kokan if one is ever parented UNDER the womb item.
            if (_entranceBone == null)
                foreach (var t in GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == MainGameWomb.WombBone("cf_j_kokan")) { _entranceBone = t; break; }
            return _entranceBone != null ? _entranceBone.position : transform.position;
        }

        /// <summary>The womb's organ-shell body stencil (_StencilBody on its CloXray/Organ material); the
        /// BodyReveal copy must match it. Read live (not cached) so a manual change is picked up. Default 4.</summary>
        public int OrganStencil()
        {
            if (_smr != null)
                foreach (var m in _smr.sharedMaterials)
                    if (m != null && m.HasProperty("_StencilBody"))
                        return Mathf.RoundToInt(m.GetFloat("_StencilBody"));
            return 4;
        }

        /// <summary>Called by AutoBodyReveal when the BodyReveal stamp is applied/adopted for this
        /// womb's character: restore the full out-of-body visibility (interior+cum, the half-out
        /// feature) that the new-spawn default hides, and turn the scene-depth confine on so opaque
        /// clothes/props/walls still occlude correctly. Material INSTANCES (per-womb; the ME sliders
        /// edit the same instances, so manual tweaks keep working).</summary>
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
                            // FREE-H (both games): hide ALL THREE womb layers out-of-body so a
                            // deep/edge pose can never flash the organ past the body silhouette. occlude=0
                            // hides interior+cum (only front-wall depth remains -> they fail LEqual, per
                            // the out-of-body routing rules); _OutsideOfBodyAlpha=0 hides the shell colour. The cum
                            // needs no separate touch - its out-of-body visibility rides entirely on the
                            // uterus back-wall depth this occlude switch controls.
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
