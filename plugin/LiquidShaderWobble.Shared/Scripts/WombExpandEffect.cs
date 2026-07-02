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
    ///   bigger / deeper-pressed penis opens wider — matching BP's growth speed and thickness.
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
        // human, **V6 is MANUAL-ONLY** (KKPE slider), NOT part of the auto animation — so it is deliberately
        // LEFT OUT of this list. The plugin only overrides shapes it lists, so V6 stays at whatever weight
        // you set by hand. Channel names are a CONTRACT (COORD.md pt4).
        public string RingBlendShapes { get; set; } =
            "Vagina_1_open,Vagina_2_low,Vagina_3_mid,Vagina_4_upper,Vagina_5_top";
        // Deep-insertion displacement shape. Switched from Vagina_stretch to the whole-womb
        // womb_displace (M's shape); the Stretch* params below drive it.
        public string StretchBlendShape { get; set; } = "womb_displace";
        public string StrengthBlendShape { get; set; } = "BP_Strength";
        public string DampeningBlendShape { get; set; } = "BP_Dampening";
        // Inert per-object toggle: weight > 50 => this womb IGNORES DynamicBoneColliders (it will NOT
        // react to a collider/toy pushed in) while STILL reacting to the BP penis. Absent or weight 0
        // => react to colliders as before (default behavior unchanged).
        public string IgnoreCollidersShape { get; set; } = "BP_IgnoreColliders";
        // Inert per-object penetration SENSITIVITY (the engagement TRIGGER, NOT the opening amount, which is
        // BP_Strength): weight/50 -> 0 = off (manual), 50 (baked default) = 1.0 = current behavior, 100 = 2x.
        // Scales the canal-containment lateral gate AND the depth-to-engage; absent shape => 1.0 (unchanged).
        public string SensitivityBlendShape { get; set; } = "BP_Sensitivity";
        // Entrance direction-reaction: lean the mouth toward the incoming penis (read from BP tip dir).
        public string MoundForwardShape { get; set; } = "moundforward";
        public string MoundBackShape { get; set; } = "moundback";
        // Left/right lean (local X) — the lateral pair that lets the mouth follow a penis offset SIDEWAYS, matching
        // BP's own kokan-pull (BP translates the vagina along the full 3D penis direction). KK has no left/right
        // mound shape, so M authors this pair (see the [S→M] mound request in COORD.md); no-op until they exist.
        public string MoundLeftShape { get; set; } = "moundleft";
        public string MoundRightShape { get; set; } = "moundright";

        // Normalized depths: the entrance ring (V1, very bottom) opens EARLY (with BP entry) -> low
        // Depth Start; the cervix (V5) opens deep -> Depth End. Rings open in order entrance->cervix.
        public float DepthStart { get; set; } = 0.10f;
        public float DepthEnd { get; set; } = 0.97f;

        // Full-open weights (0..100). All rings radial-target a uniform cylinder (the canal opens to a
        // smooth round TUBE):
        //  - V1 (EntranceWeight): the ENTRANCE ring at the very bottom — opens the entrance ROUND (X=Z)
        //    and early (low Depth Start). Lower it for a narrower introitus.
        //  - V2-V4 (RingWeight): the tube BODY (~45mm at ~90).
        //  - V5 (CervixWeight): the TOP/cervix — opens the top at deep insertion (lower = keep a neck).
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
        public float StretchCap { get; set; } = 100f;   // FIXED at 100 — a >100 blendshape write latches/sticks in KKPE, so this was always 100; no longer a cfg (to displace more, strengthen the womb_displace SHAPE in the mesh)
        // Displace scales with BP penis LENGTH (m_baseDanLength): a longer / deeper-set penis displaces
        // the womb MORE. lengthScale = clamp(baseLen / RefLength, 0.6, 2). Set RefLength to your default
        // penis's logged baseLen (see the WombExpand debug line). DirReactWeight = how far the mouth
        // leans toward the incoming penis direction (moundforward/back); 0 = off, "a little" ~25.
        public float RefLength { get; set; } = 0.10f;
        public float DirReactWeight { get; set; } = 25f;

        // Animation timing (2026-06-17): rings OPEN slightly ahead of the penis (anticipation) and CLOSE
        // behind it (follow-through). OpenLead = how far ahead, in normalized depth, a ring reaches full.
        // CloseSmoothing = how fast the measured depth FALLS on withdrawal (lower than DepthSmoothing -> the
        // depth lingers, so ALL rings close LATER). The ENTRANCE ring (V1) eases open over a wider window
        // (EntranceOpenWidth, so it isn't abrupt — it opens in step with BP's vagina) and closes
        // EntranceCloseScale x slower than the others (its geometric move per weight unit is smaller).
        public float OpenLead { get; set; } = 0.06f;
        public float CloseSmoothing { get; set; } = 4f;
        public float EntranceOpenWidth { get; set; } = 0.30f;
        public float EntranceCloseScale { get; set; } = 2f;
        // Entry-feel tuning (2026-06-17): OpenTime rate-limits opening so rings EASE IN (not snap);
        // EntranceOpenScale makes V1 open that-many-times slower; MaxGirthScale caps girth->width scaling.
        public float OpenTime { get; set; } = 0.2f;
        public float EntranceOpenScale { get; set; } = 2f;
        public float MaxRingWeight { get; set; } = 100f;   // FIXED at 100 — a >100 blendshape write latches/sticks in KKPE; no longer a cfg (widen the mesh tube to open beyond this)
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
        private int _strengthIdx = -1;
        private int _dampeningIdx = -1;
        private int _ignoreCollidersIdx = -1;
        private int _sensIdx = -1;
        private float _sens = 1f;          // penetration sensitivity factor this frame (1.0 = default); scales the containment lateral gate
        private Transform _entranceBone;     // item's cf_j_kokan = canal-entrance proxy (auto-BodyReveal proximity)

        private float[] _ringReaction;   // plugin contribution (pre-strength) per ring, for decay
        private float _stretchReaction;
        private int _moundFwdIdx = -1, _moundBackIdx = -1, _moundLeftIdx = -1, _moundRightIdx = -1;
        private float _moundFwdReact, _moundBackReact, _moundLeftReact, _moundRightReact;   // direction-reaction contribution, for decay

        private float _depth;            // smoothed normalized depth
        private bool _ready;
        private BPBridge.Reading _bp;
        // Canal axis (entrance->top, world) for collider-depth, baked + cached on a 2s timer (EnsureCanal).
        private Vector3 _canalEntrance, _canalAxis = Vector3.up;
        private float _canalLen, _canalTimer;
        private float _colLateral = -1f;   // when a collider is in the canal, its centre's lateral offset from the axis (m); -1 = none (debug)
        private float _bpLateral = -1f;    // BP penis tip's lateral offset from the canal axis (m); -1 = no pose/no bake (debug)
        private float _bpClosest = -1f;    // closest any shaft bone came to the canal axis when NOT contained (debug)
        private float[] _ringDepths;       // each ring's normalized canal depth (DepthStart..DepthEnd), for the collider per-ring scan
        private float[] _colRingRadius;    // per-ring MAX collider world radius (thickest collider reaching that ring) — multi-collider taper
        private bool _colPerRing;          // true when the collider path drove with per-ring radii available
        private bool  _bpVaginaMain;       // engaged via BP-depth on the paired vagina WITHOUT bone-containment (the bones sit below the cervix)
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
        // Engagement diagnostics — why the womb did / didn't react this frame.
        private bool  _dbgFound, _dbgTipKnown, _dbgContained, _dbgInWomb, _dbgEngaged, _dbgTipShow;
        private int    _seenRepairVersion;           // last _repairVersion this womb consumed -> a bump re-pairs it on the next post-IK frame (FULLY event-driven, no poll/fallback)
        private string _pairedName;    // the character whose penis is paired to THIS womb (by entry node); read locked each frame
        private Vector3 _grabEntryW;   // paired penis's k_f_dan_entry world pos, read POST-NC in onPreCull (debug marker/line)
        private Vector3 _showTipW;     // effective penis tip for the yellow marker: DEEPER of the cm_j_dan bone vs the NC-aimed k_f_dan_end
        private float _dbgTipDepth = -1f;
        private bool  _notEngagedWarned;   // one-shot: BP penetrating but womb won't engage (tip off-axis)
        private bool  _handoffLogged;      // one-shot: BP_Strength=0 hand-off logged

        public bool DebugLog { get; set; } = false;
        private const float LogInterval = 2f;
        private float _logTimer;

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

        private bool _placeLogged;

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

        // Canal entrance + top (world), from a bake + median ring-centre walk (same as LogPlacement).
        // ── DEBUG read-markers (gated by Debug Log): GameObject spheres, rendered by Unity's own pipeline so the
        // projection is correct. (The old GL line/cross overlay was removed — it skewed on DirectX and swam with the
        // camera.) CYAN = canal mouth (_canalEntrance), MAGENTA = cervix (entrance + axis*len), YELLOW = paired
        // penis tip, GREEN = grabbed penis entry node.
        private GameObject _mkEntrance, _mkAxisEnd, _mkTip, _mkGrabEntry;
        private Vector3[] _canalBandW;     // canal endpoints (entrance, cervix) in WORLD — re-projected each frame
        private Vector3[] _canalLocal;     // canal endpoints (entrance, cervix) in LOCAL/rest space (constant; baked once)
        private Transform _canalBone;      // M's clo_canal_entry marker bone (if the mesh has it) -> exact, scale/rotation-native
        private bool _mkDiagLogged;
        private static GameObject MakeDbgSphere(Color col, float diam)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "CloXrayDbgMarker";
            var c = go.GetComponent<Collider>(); if (c != null) Destroy(c);
            go.transform.localScale = Vector3.one * diam;

            // Bake the colour into the MESH vertex colours. Hidden/Internal-Colored outputs (vertexColor * _Color)
            // with an alpha blend by default, and a primitive sphere has NO colour channel -> on D3D11 that reads as
            // (0,0,0,0) -> the sphere blends to fully transparent (invisible). Accessing .mesh clones the shared mesh.
            var mf = go.GetComponent<MeshFilter>();
            var mesh = mf.mesh;                                  // instance copy — does NOT touch Unity's shared sphere mesh
            mesh.hideFlags = HideFlags.HideAndDontSave;
            var cols = new Color[mesh.vertexCount];
            for (int i = 0; i < cols.Length; i++) cols[i] = col;
            mesh.colors = cols;

            var mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var sh = Shader.Find("Hidden/Internal-Colored");
            var m = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            m.SetColor("_Color", Color.white);                                       // colour comes from the mesh verts
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);         // OPAQUE — never alpha-blend to invisible
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            m.SetInt("_Cull", 0);
            m.SetInt("_ZWrite", 1);
            m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);   // x-ray: visible through the body
            m.renderQueue = 5000;
            mr.sharedMaterial = m;
            go.hideFlags = HideFlags.HideAndDontSave;
            return go;
        }
        private static void DestroyMarker(ref GameObject go) { if (go != null) { Destroy(go); go = null; } }
        private static void EnsureSphere(ref GameObject go, Color col, float diam, int layer) { if (go == null) go = MakeDbgSphere(col, diam); go.layer = layer; }
        private void UpdateDebugMarkers()
        {
            if (!DebugLog)
            {
                DestroyMarker(ref _mkEntrance); DestroyMarker(ref _mkAxisEnd); DestroyMarker(ref _mkTip); DestroyMarker(ref _mkGrabEntry);
                _mkDiagLogged = false;
                return;
            }
            int wl = (_smr != null) ? _smr.gameObject.layer : gameObject.layer;   // CreatePrimitive lands on layer 0 (Default), which the Studio camera may not render
            EnsureSphere(ref _mkEntrance,  Color.cyan,             0.016f, wl);   // canal mouth / opening
            EnsureSphere(ref _mkAxisEnd,   new Color(1f, 0f, 1f),  0.011f, wl);   // canal top / cervix
            EnsureSphere(ref _mkTip,       Color.yellow,           0.013f, wl);   // penis tip the plugin paired with
            EnsureSphere(ref _mkGrabEntry, Color.green,            0.014f, wl);   // GRABBED penis's k_f_dan_entry (what the pairing matched)

            if (!_mkDiagLogged)
            {
                _mkDiagLogged = true;
                var emr = _mkEntrance.GetComponent<MeshRenderer>();
                LiquidWobbleMPBPlugin._logger?.LogInfo(
                    $"  DBG-MARKERS: shaderFound={(Shader.Find("Hidden/Internal-Colored") != null)} wombLayer={wl}('{LayerMask.LayerToName(wl)}') " +
                    $"rendEnabled={emr.enabled} mat='{(emr.sharedMaterial != null && emr.sharedMaterial.shader != null ? emr.sharedMaterial.shader.name : "NULL")}'");
            }

            bool haveCanal = _canalLen > 1e-4f;
            _mkEntrance.SetActive(haveCanal);
            _mkAxisEnd.SetActive(haveCanal);
            if (haveCanal)
            {
                _mkEntrance.transform.position = _canalEntrance;
                _mkAxisEnd.transform.position  = _canalEntrance + _canalAxis * _canalLen;
            }
            // Show the paired penis's tip whenever this womb is ENGAGED. The pairing is now locked to ONE penis (by entry
            // node), so this is always THIS womb's penis — no cross-pairing to reject. Loose bulb-radius bound so an
            // off-axis-but-inside tip (e.g. an angled anal insertion ~58mm off) still shows; only a wildly-off tip hides.
            _dbgTipShow = false;
            if (_dbgEngaged && _bp.found && _canalLen > 1e-4f)
            {
                Vector3 rel = _showTipW - _canalEntrance;
                float along = Vector3.Dot(rel, _canalAxis);
                float lat = (rel - _canalAxis * along).magnitude;
                _dbgTipShow = lat < Mathf.Max(0.09f, WombBulbRadius()) && along > -0.10f && along < _canalLen + 0.30f;
            }
            _mkTip.SetActive(_dbgTipShow);
            if (_dbgTipShow) _mkTip.transform.position = _showTipW;
            // GREEN = the POST-NC k_f_dan_entry node of the penis this womb GRABBED (so you can see WHICH entry the
            // pairing chose and whether it sits on the cyan->magenta channel line). Read in onPreCull (driven position).
            bool haveEntry = _grabEntryW != Vector3.zero;
            _mkGrabEntry.SetActive(haveEntry);
            if (haveEntry) _mkGrabEntry.transform.position = _grabEntryW;
            // (canal is a 2-point line now: CYAN marks the entrance, MAGENTA the cervix; the cyan GL line extends BELOW
            //  the mouth to show the channel line the pairing tests entries against.)
        }
        private void OnDestroy()
        {
            DestroyMarker(ref _mkEntrance); DestroyMarker(ref _mkAxisEnd); DestroyMarker(ref _mkTip); DestroyMarker(ref _mkGrabEntry);
        }

        private bool TryCanal(out Vector3 entranceW, out Vector3 topW)
        {
            entranceW = topW = Vector3.zero;
            try
            {
                Mesh baked = new Mesh();
                // Bake the REST shape (blendshape weights zeroed) so the canal STRUCTURE is independent of the
                // penetration animation. Otherwise the WombExpand blendshapes deform the canal every frame -> the
                // detected entrance/length flicker -> that flicker feeds back into depth/reaction and the womb
                // ANIMATES while the penis is stationary (the "jumps up/down on pause" bug), and the entrance jumps
                // on penetration. Bones (post-IK position/orientation) are unaffected. Weights are restored before
                // this returns (we're in onPreCull, before render -> no visible flicker).
                int bsc = (_smr.sharedMesh != null) ? _smr.sharedMesh.blendShapeCount : 0;
                float[] bsSaved = null;
                if (bsc > 0)
                {
                    bsSaved = new float[bsc];
                    for (int i = 0; i < bsc; i++) { bsSaved[i] = _smr.GetBlendShapeWeight(i); _smr.SetBlendShapeWeight(i, 0f); }
                }
                _smr.BakeMesh(baked);
                if (bsSaved != null) for (int i = 0; i < bsc; i++) _smr.SetBlendShapeWeight(i, bsSaved[i]);
                Vector3[] v = baked.vertices;                               // LOCAL space, REST shape — canal runs along local +Y
                float minY = float.MaxValue;                                // (orientation-robust: works for a rotated / anus-oriented womb)
                for (int i = 0; i < v.Length; i++) if (v[i].y < minY) minY = v[i].y;
                float seedx, seedz, seedSpread; int sc;
                RingCenter(v, minY + 0.012f, 0.014f, 0f, 0f, 1f, out seedx, out seedz, out sc, out seedSpread);
                var centers = new System.Collections.Generic.List<Vector3>();
                float cx = seedx, cz = seedz, entranceSpread = -1f, topSpread = 0f;
                // ADAPTIVE climb from the opening up the tube (was a fixed 8.5cm centred sub-segment). Below the first
                // hit we SKIP empty bands (rest-pose slit) to find the mouth; once on the canal we STOP at the CERVIX —
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
                if (centers.Count < 2) { Destroy(baked); return false; }
                entranceW = _smr.transform.TransformPoint(centers[0]);
                topW = _smr.transform.TransformPoint(centers[centers.Count - 1]);
                // The canal is a straight axis: cache ONLY the two endpoints (entrance + cervix) in LOCAL space. The
                // walk's intermediate centres were only used to FIND the cervix (the spread-stop) + the old debug trail;
                // entrance/axis/length need just these two. RefreshCanalWorld re-projects them through the live transform.
                _canalLocal = new Vector3[] { centers[0], centers[centers.Count - 1] };
                _canalBandW = new Vector3[] { _smr.transform.TransformPoint(_canalLocal[0]), _smr.transform.TransformPoint(_canalLocal[1]) };
                if (DebugLog)
                {
                    float mny=1e9f,mxy=-1e9f;
                    for (int i = 0; i < v.Length; i++){ float py=v[i].y; if(py<mny)mny=py; if(py>mxy)mxy=py; }
                    float canalLen = Vector3.Distance(entranceW, topW);
                    LiquidWobbleMPBPlugin._logger?.LogInfo(
                        $"  CANAL-DIAG '{name}' postIK={_bakingPostIK}: bands={centers.Count} canalLen={canalLen:F3} entSpread={entranceSpread:F3} topSpread={topSpread:F3} | " +
                        $"entW={entranceW.ToString("F3")} (opening) topW={topW.ToString("F3")} (cervix) bakedLocalY[{mny:F3}..{mxy:F3}] | " +
                        $"smrPos={_smr.transform.position.ToString("F3")} euler={_smr.transform.rotation.eulerAngles.ToString("F0")} " +
                        $"rendBoundsCtrW={_smr.bounds.center.ToString("F3")} rendBoundsBottomY={(_smr.bounds.center.y - _smr.bounds.extents.y):F3} bpTip={(_bp.found && _bp.hasPose ? _bp.tipPos.ToString("F3") : "-")}");
                }
                Destroy(baked);
                return true;
            }
            catch { return false; }
        }

        // Bake the canal SHAPE ONCE. The local rest-mesh structure is CONSTANT — the womb's reactions are blendshapes
        // (zeroed in the bake), and moving/rotating/scaling the womb is a TRANSFORM change, re-projected live by
        // RefreshCanalWorld. So there is nothing to re-detect: we just retry (slowly) until the FIRST valid bake lands
        // (the womb may not be fully initialised on frame 1), then `_canalLocal` is set and this short-circuits forever.
        // Pose at bake time no longer matters (RefreshCanalWorld places the cached LOCAL shape through the live, post-IK
        // transform every frame). Re-bakes only if the womb item is reloaded -> the component re-inits, _canalLocal null.
        private void EnsureCanal(bool drive = false)
        {
            if (_canalLen > 1e-4f && _canalLocal != null && _canalLocal.Length >= 2) return;   // have the constant shape -> never re-bake
            _canalTimer -= Time.deltaTime;
            if (_canalTimer > 0f) return;
            _canalTimer = 1f;                                                                   // retry cadence ONLY until the womb is ready (then the line above short-circuits forever)
            Vector3 entW, topW;
            if (TryCanal(out entW, out topW))
            {
                Vector3 ax = topW - entW; float l = ax.magnitude;
                // Only trust a plausible canal length (~77mm at unit scale; allow scaling). A degenerate/premature
                // bake (tiny len) would make cdepth = along/len blow up and read every collider as fully inserted.
                if (l >= 0.02f && l <= 0.5f)
                {
                    _canalEntrance = entW; _canalAxis = ax / l; _canalLen = l;
                    if (_canalBone == null)   // mesh bone supersedes the bake for entrance+axis (RefreshCanalWorld); search once
                    {
                        _canalBone = FindCanalBone();
                        if (DebugLog) LiquidWobbleMPBPlugin._logger?.LogInfo($"  CANAL-BONE '{name}': {(_canalBone != null ? "FOUND clo_canal_entry -> using the mesh bone (exact, scale/rotation-native)" : "no clo_canal_entry bone -> using the BakeMesh walk (old mesh)")}");
                    }
                }
                else _canalLen = 0f;
            }
        }

        // POST-IK feed: like LiquidShaderWobbleEffect, sample from Camera.onPreCull (strictly after all LateUpdates /
        // IK / NodesConstraints, the exact pose the GPU is about to draw) so the canal bake matches the rendered womb.
        private int _lastCanalFrame = -1;
        private bool _bakingPostIK;
        private Camera.CameraCallback _canalPreCull;
        private void OnEnable()
        {
            _canalPreCull = OnPreCullCanal;
            Camera.onPreCull += _canalPreCull;
            // A CloXray womb is now live. Bump the gate so the BP-interop hooks are allowed to act, install them
            // (idempotent; no-op until BP's assembly loads — also retried from the scene-load coroutine), and refresh
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
            if (!_ready) return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;     // master toggle OFF -> freeze (no canal re-bake / pairing / markers)
            if (Time.frameCount == _lastCanalFrame) return;   // first camera of the frame only
            _lastCanalFrame = Time.frameCount;
            _bakingPostIK = true;
            EnsureCanal(true);          // authoritative post-IK re-bake (timed) -> refreshes the LOCAL/rest canal
            _bakingPostIK = false;
            RefreshCanalWorld();        // EVERY frame: re-project the cached LOCAL canal through the live transform
            // PAIRING — POST-NodesConstraints here, so k_f_dan_entry sits at its DRIVEN position (the vagina/anus the user
            // aimed it at). Match by lateral offset from this womb's channel axis (relative-sticky). FULLY EVENT-DRIVEN,
            // NO POLL: an NC dan_entry add/enable/disable/delete, a character (re)load, the hotkey, or this womb enabling
            // bumps _repairVersion; the womb re-runs FindByEntry on the first post-IK frame its canal is baked, then not
            // again until the next event. (A seated womb's entry is NC-pinned, so a Studio drag can't change this pairing
            // -> no fallback poll is needed.) Then grab that penis's entry (post-NC) for the debug marker.
            if (_seenRepairVersion != _repairVersion && _canalLen > 1e-4f)
            {
                _seenRepairVersion = _repairVersion;
                _pairedName = BPBridge.FindByEntry(_canalEntrance, _canalAxis, LiquidWobbleMPBPlugin.CfgPairRange, _pairedName, 0.02f);
            }
            _grabEntryW = BPBridge.GetEntryWorld(_pairedName);   // post-NC entry of the paired penis (for the green marker / line)
            // Effective yellow tip = the DEEPER (along the canal axis) of the cm_j_dan deepest bone (_bp.tipPos, BP can
            // squish it shallow) and the NC-aimed k_f_dan_end (read post-NC). k_f_dan_end is the aimed ENDPOINT, usually
            // placed BEYOND the real tip — so CLAMP the depth to the canal (entrance..cervix): the real penis tip can't
            // go past the cervix, so an over-aimed endpoint snaps to the cervix and a partial insertion shows its depth.
            _showTipW = _bp.tipPos;
            Vector3 endW = BPBridge.GetEndWorld(_pairedName);
            if (endW != Vector3.zero && _canalLen > 1e-4f &&
                Vector3.Dot(endW - _canalEntrance, _canalAxis) > Vector3.Dot(_bp.tipPos - _canalEntrance, _canalAxis))
                _showTipW = endW;
            if (_canalLen > 1e-4f)
            {
                Vector3 trel = _showTipW - _canalEntrance;
                float rawAlong = Vector3.Dot(trel, _canalAxis);
                _showTipW = _canalEntrance + _canalAxis * Mathf.Clamp(rawAlong, 0f, _canalLen) + (trel - _canalAxis * rawAlong);
            }
            UpdateDebugMarkers();       // place the debug markers with the post-IK canal
        }

        // Re-project the cached LOCAL (rest) canal centres through the womb's CURRENT (post-IK) transform. Cheap, runs
        // every frame so the canal tracks the womb without a per-frame bake. SHAPE is from the rest bake (stable, no
        // penetration-deformation feedback); POSITION is live. Keeps _canalEntrance/_canalAxis/_canalLen + the markers
        // in sync with where the womb actually renders.
        private void RefreshCanalWorld()
        {
            // Bake-based re-projection: gives _canalLen + the fallback when the mesh has no canal bone.
            if (_canalLocal != null && _canalLocal.Length >= 2)
            {
                if (_canalBandW == null || _canalBandW.Length != _canalLocal.Length) _canalBandW = new Vector3[_canalLocal.Length];
                for (int i = 0; i < _canalLocal.Length; i++) _canalBandW[i] = _smr.transform.TransformPoint(_canalLocal[i]);
                Vector3 e = _canalBandW[0], t = _canalBandW[_canalBandW.Length - 1];
                Vector3 ax = t - e; float l = ax.magnitude;
                if (l >= 0.02f && l <= 0.5f) { _canalEntrance = e; _canalAxis = ax / l; _canalLen = l; }
            }
            // If the mesh ships M's `clo_canal_entry` marker bone, OVERRIDE entrance + axis with it: exact, and
            // scale/rotation/position-NATIVE (the bake re-project double-applies lossyScale on a scaled womb). The bone's
            // local +Y is the canal axis (M's spec). Length stays the baked scalar; the cervix marker follows it.
            if (_canalBone != null && _canalLen > 1e-4f)
            {
                _canalEntrance = _canalBone.position;
                _canalAxis = _canalBone.up;
                if (_canalBandW == null || _canalBandW.Length < 2) _canalBandW = new Vector3[2];
                _canalBandW[0] = _canalEntrance;
                _canalBandW[_canalBandW.Length - 1] = _canalEntrance + _canalAxis * _canalLen;
            }
        }
        // Find M's `clo_canal_entry` marker bone in THIS womb's armature (a leaf under cf_j_kokan, which is one of the
        // SMR's bones). Null on the old mesh -> RefreshCanalWorld falls back to the BakeMesh-walk canal. Searched once.
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

        // Is this womb seated ON a character's vagina? Caches the nearest cf_J_Vagina_root (outside our own subtree) to
        // the canal entrance, on a 2s timer, and pairs when it's within the "Womb-in-vagina range". This is the anchor
        // for the MAIN penis signal: BP-on-this-vagina drives the womb regardless of where the dan bones bunch up.
        // ── ZERO-EFFECT-WHEN-UNUSED GATE ──────────────────────────────────────────────────────────────────────
        // Count of live CloXray womb effects in the scene. Our BP-interop hooks (dan-dup guard, penis-FK enforcer,
        // the on-load penis-bend fix) check AnyActive and become complete no-ops when it is 0, so a scene with no
        // CloXray womb behaves exactly as if our DLL weren't installed. Bumped in OnEnable / dropped in OnDisable.
        private static int _activeCount;
        public static bool AnyActive { get { return _activeCount > 0; } }
        // EffectiveActive = the master F1 "Enabled" toggle AND a womb present. The BP-interop reach-ins (BPDanReaddGuard,
        // PenisFKEnforcer, the on-load penis-FK-off coroutine, the NC-enable pairing hook) gate on THIS, so flipping
        // Enabled OFF leaves BetterPenetration + the penis FK exactly as posed, instantly and live.
        public static bool EffectiveActive { get { return LiquidWobbleMPBPlugin.CfgEnabled && _activeCount > 0; } }

        // Pairing-recheck signal. Every discrete event that can change womb<->penis pairing bumps this version: an NC
        // dan_entry add / enable / disable / delete (NodeConstraintBridge hooks), a character (re)load, the hotkey
        // adding the NC link, or a womb enabling. Each womb re-runs FindByEntry once when its seen-version lags. This
        // REPLACES the old 1s poll outright — no fallback: a seated womb's NC-pinned entry only moves via these events.
        private static int _repairVersion;
        public static void RequestRepair() { _repairVersion++; }

        // SHARED cf_J_Vagina_root cache. The full-scene FindObjectsOfType<Transform> scan is heavy, and the root LIST
        // only changes when a CHARACTER spawns/despawns — an event, not a per-frame thing. So it is refreshed
        // EVENT-DRIVEN: InvalidateVaginaRoots() is called on every CharacterReloaded and whenever a womb is enabled,
        // forcing the next access to rescan. The long fallback below is pure insurance for a spawn path that somehow
        // doesn't raise the KKAPI event — NOT the primary path (was a 2s poll, whose periodic full-scene scan was the
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

        // The BP penis TIP's position in THIS canal: lateral offset from the axis (m) and along-axis distance (m,
        // 0=entrance). From the k_f_dan_end collider. Used BOTH for the containment gate (reject a tip parked
        // beside/under the vagina) AND for the tip-depth fallback (drive the womb from where the tip physically is,
        // since BP's own depth = base->target shrink reads ~0 when the male is posed rather than BP-thrusting).
        // Returns false when there's no collider pose / no canal bake. Sets _bpLateral for the debug line.
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

        // Penis WORLD girth radius at a given distance BACK FROM THE TIP (world metres), from BP's per-collider
        // girth profile. Each collider's distance from the tip = |pos - tipPos|; we interpolate the radius at
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
        private float WombBulbRadius()
        {
            Vector3 le = _smr.sharedMesh != null ? _smr.sharedMesh.bounds.extents : _smr.bounds.extents;
            Vector3 ls = _smr.transform.lossyScale;
            return Mathf.Max(Mathf.Abs(le.x * ls.x), Mathf.Abs(le.z * ls.z));
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

        // One-shot: tube entrance center + tube centerline (median) vs BP entry/channel.
        private void LogPlacement()
        {
            try
            {
                Mesh baked = new Mesh();
                _smr.BakeMesh(baked);
                Vector3[] v = baked.vertices;
                float minY = float.MaxValue, maxY = float.MinValue;
                for (int i = 0; i < v.Length; i++) { if (v[i].y < minY) minY = v[i].y; if (v[i].y > maxY) maxY = v[i].y; }

                // Seed column center from a wide median of the bottom slice.
                float seedx, seedz; int sc;
                RingCenter(v, minY + 0.010f, 0.012f, 0f, 0f, 1f, out seedx, out seedz, out sc, out _);

                // Walk up the tube taking median ring centers (column radius 0.035 excludes ovaries).
                var centers = new System.Collections.Generic.List<Vector3>();
                float cx = seedx, cz = seedz;
                float[] bands = { 0.008f, 0.025f, 0.045f, 0.065f, 0.085f };
                foreach (float off in bands)
                {
                    float yb = minY + off;
                    float ox, oz; int cnt;
                    if (RingCenter(v, yb, 0.008f, cx, cz, 0.035f, out ox, out oz, out cnt, out _))
                    { centers.Add(new Vector3(ox, yb, oz)); cx = ox; cz = oz; }
                }
                Destroy(baked);
                if (centers.Count < 2) { LiquidWobbleMPBPlugin._logger?.LogWarning("PLACEMENT: too few ring centers"); return; }

                Vector3 entranceL = centers[0];
                Vector3 topL = centers[centers.Count - 1];
                Vector3 entrance = _smr.transform.TransformPoint(entranceL);
                Vector3 top = _smr.transform.TransformPoint(topL);
                Vector3 tubeAxis = (top - entrance).normalized;

                // Character bones only — exclude our own item's leftover skeleton.
                Transform itemRoot = transform;
                System.Func<Transform, bool> ours = (t) => { for (var c = t; c != null; c = c.parent) if (c == itemRoot) return true; return false; };
                var all = UnityEngine.Object.FindObjectsOfType<Transform>();
                Transform vr = null, w1 = null; float best = float.MaxValue;
                foreach (var t in all)
                    if (t != null && t.name == "cf_J_Vagina_root" && !ours(t))
                    { float d = (t.position - entrance).sqrMagnitude; if (d < best) { best = d; vr = t; } }
                if (vr != null)
                {
                    best = float.MaxValue;
                    foreach (var t in all)
                        if (t != null && t.name == "cf_j_waist01" && !ours(t))
                        { float d = (t.position - vr.position).sqrMagnitude; if (d < best) { best = d; w1 = t; } }
                }

                string vrs = vr != null ? $"({vr.position.x:F4},{vr.position.y:F4},{vr.position.z:F4})" : "<not found>";
                Vector3 delta = vr != null ? entrance - vr.position : Vector3.zero;
                LiquidWobbleMPBPlugin._logger?.LogInfo(
                    $"{nameof(WombExpandEffect)} PLACEMENT: entranceCenter=({entrance.x:F4},{entrance.y:F4},{entrance.z:F4}) " +
                    $"tubeTop=({top.x:F4},{top.y:F4},{top.z:F4}) rings={centers.Count} " +
                    $"cf_J_Vagina_root={vrs} delta=({delta.x:F4},{delta.y:F4},{delta.z:F4}) |d|={delta.magnitude * 100f:F2}cm");
                if (vr != null && w1 != null)
                {
                    Vector3 bpAxis = (w1.position - vr.position).normalized;
                    float ang = Vector3.Angle(tubeAxis, bpAxis);
                    if (ang > 90f) ang = 180f - ang; // undirected
                    LiquidWobbleMPBPlugin._logger?.LogInfo(
                        $"{nameof(WombExpandEffect)} AXIS: tubeAxis=({tubeAxis.x:F3},{tubeAxis.y:F3},{tubeAxis.z:F3}) " +
                        $"bpAxis(vroot->waist01)=({bpAxis.x:F3},{bpAxis.y:F3},{bpAxis.z:F3}) " +
                        $"waist01=({w1.position.x:F4},{w1.position.y:F4},{w1.position.z:F4}) ANGLE(undirected)={ang:F1}deg");
                }
            }
            catch (System.Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning($"PLACEMENT log failed: {e.Message}"); }
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;     // master toggle OFF -> freeze: stop driving ring/stretch/mound weights (they stay latched)

            if (DebugLog && !_placeLogged) { _placeLogged = true; LogPlacement(); }

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
                StretchMax       = LiquidWobbleMPBPlugin.CfgStretchMax;
                StretchStart     = LiquidWobbleMPBPlugin.CfgStretchStart;
                StretchOvershoot = LiquidWobbleMPBPlugin.CfgStretchOvershoot;
                RefLength        = LiquidWobbleMPBPlugin.CfgRefLength;
                DirReactWeight   = LiquidWobbleMPBPlugin.CfgDirReact;
                OpenLead           = LiquidWobbleMPBPlugin.CfgOpenLead;
                CloseSmoothing     = LiquidWobbleMPBPlugin.CfgCloseSmoothing;
                EntranceOpenWidth  = LiquidWobbleMPBPlugin.CfgEntranceOpenWidth;
                EntranceCloseScale = LiquidWobbleMPBPlugin.CfgEntranceCloseScale;
                OpenTime           = LiquidWobbleMPBPlugin.CfgOpenTime;
                EntranceOpenScale  = LiquidWobbleMPBPlugin.CfgEntranceOpenScale;
                MaxGirthScale      = LiquidWobbleMPBPlugin.CfgMaxGirthScale;
                DebugLog         = LiquidWobbleMPBPlugin.CfgDebugLog;
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

            // BP_Strength = 0 -> hand off completely: don't touch the driven shapes, so your manual
            // / KKPE pose controls them. (We OVERRIDE when strength > 0; additive doesn't survive
            // KKPE re-applying manual weights each frame.)
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
            bool engaged = false;
            float girthScale = 1f;
            float lengthScale = 1f;
            float targetNorm = 0f;
            _bpLateral = -1f; _bpVaginaMain = false; _tipDetached = false; _tipDist = -1f; _vaginaFarLat = false;
            _entryDetached = false; _entryLat = -1f;
            _dbgTipKnown = false; _dbgTipDepth = -1f; _dbgContained = false;
            EnsureVaginaPairing();   // cached 2s: is this womb seated on a cf_J_Vagina_root?
            // The womb<->penis pairing is decided in OnPreCullCanal — POST-NodesConstraints — so k_f_dan_entry is read at
            // its DRIVEN position (at the vagina/anus), not the penis-rig default. Here we just read the chosen penis.
            if (BPBridge.TryReadLocked(_pairedName, out _bp) && _bp.found)
            {
                float w = PenisInCanalWidth;
                // `contained` = a dan bone (or the aimed tip) physically threads the canal. Kept ONLY to (a) report the
                // off-axis distance in the diagnostic and (b) act as a no-regression OR below — it is NO LONGER REQUIRED
                // for the womb to react (it can never gate OUT now).
                float realAlong = 0f, realLat = -1f;
                bool realIn = (_bp.shaftPos != null) && DeepestShaftInCanal(_bp.shaftPos, _bp.shaftPos.Length, w, out realAlong, out realLat);
                float aimAlong = 0f, aimLat = -1f; bool aimedIn = false;
                if (_bp.aimedValid) { _aimBuf[0] = _bp.aimedTipPos; aimedIn = DeepestShaftInCanal(_aimBuf, 1, w, out aimAlong, out aimLat); }
                bool contained = realIn || aimedIn;
                _bpLateral = realIn ? realLat : (aimedIn ? aimLat : _bpClosest);
                _dbgTipKnown = contained; _dbgTipDepth = (_bp.visualDepth > 0f ? _bp.visualDepth : -1f); _dbgContained = contained; _dbgInWomb = contained;
                // MAIN penis detection (build 376): the womb is an x-ray overlay ON a vagina, so react when BP says THIS
                // womb's vagina is being penetrated. BP's depth is POSITION-based (it holds on a static pose — a half-in
                // posed penis reads raw>0) and is robust to the length-squish + overlay seating that bunch the dan BONES
                // far below the cervix while the mesh + BP sit deep. So bone-containment is no longer required — it stays
                // only as an OR so anything that engaged in 370 still engages. raw>eps gates the close (falls to ~0 on
                // withdrawal) and rejects a glued k_f_dan_end on a withdrawn penis.
                // TIP-DETACH (build 377): ALSO treat the penis as withdrawn when its tip marker k_f_dan_end has been pulled
                // far from the womb. BP's `raw` does NOT fall when you move only the tip (a constraint/sphere on k_f_dan_end
                // moves the tip but not the male's base), so without this the womb stays open after you pull the tip out.
                // Inserted, the tip sits ~0.08-0.13m from the entrance; pulled out it's 0.3m+. Only gates when a tip exists.
                float tipDetach = LiquidWobbleMPBPlugin.CfgTipDetach;
                // DIRECTIONAL tip-detach: the aimed tip (k_f_dan_end) counts as "pulled out" (withdrawn) only when it's
                // BELOW the mouth (along < -detach) or far off the canal SIDEWAYS (lateral > detach). A tip aimed DEEP up
                // the canal (along large +, on-axis) is INSERTED, not withdrawn — so a long anal/vaginal canal aimed deep
                // no longer false-closes (the old straight-distance check read womb2's 204mm-deep anal tip as "pulled out").
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
                // ENTRY-DETACH (build 429): treat the penis as WITHDRAWN when its entry/base marker k_f_dan_entry swings
                // OFF the canal axis. BP keeps the AIMED tip up in the womb even after you pull out (so tip-detach above
                // misses it), but for a penis whose entry is NOT NC-pinned to this womb's vagina the entry tracks the
                // penis BASE -> on withdrawal the base swings off-axis (this scene: ~5mm on-axis inserted -> ~65mm pulled
                // out). DIRECTIONAL like tip-detach but only the SIDEWAYS (lateral) component: being BELOW the mouth ALONG
                // the canal is normal insertion (the user's rule), so along is NOT gated. An NC-pinned entry stays at the
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
                // LATERAL gate on the VAGINA-PAIRED path: BP depth in a paired vagina is NOT enough — the penis must
                // ALSO be near THIS womb's canal. Without it, a 2nd/display womb seated near ANOTHER womb's active penis
                // engaged off BP depth alone (penisTipLat 80mm+, the penis is in the other womb). latGate = the womb's
                // oriented half-width (same basis as containment) scaled by sensitivity; _bpLateral<0 (no shaft pose)
                // keeps the old pairing fallback. 'contained' already implies lateral<=gate, so this only newly gates
                // the vagina-paired-ALONE case -> low BP_Sensitivity (or even the default) now shuts a display womb up.
                float latGate = Mathf.Max(WombBulbRadius(), PenisInCanalWidth) * sens;
                bool vaginaNear = _vaginaPaired && (_bpLateral < 0f || _bpLateral <= latGate);
                _vaginaFarLat = _vaginaPaired && !contained && _bpLateral >= 0f && _bpLateral > latGate;
                if (_bp.depth > depthGate && (contained || vaginaNear) && tipAttached && entryAttached)
                {
                    engaged = true;
                    targetNorm = (FullDepthIn > 1e-4f) ? _bp.visualDepth / FullDepthIn : _bp.visualDepth;
                    _bpVaginaMain = !contained;   // diag: reacting from BP/vagina alone — the bones never threaded the canal
                }
                if (engaged)
                {
                    // Ring WIDTH tracks the penis's STATIC TIP girth (girthTip / RefGirth) — the glans is what reaches
                    // and dilates the uterus; the whole-shaft average over-counted the fat base the uterus never
                    // touches (build 361 GIRTH-DIAG proved per-ring profiling can't differentiate — the colliders all
                    // sit below the entrance, so every ring sees the tip anyway). Deliberately does NOT include BP's
                    // deep-press fattening (girthFactor, which climbs to ~1.2 on cervix contact): that press DISPLACES/
                    // stretches the womb (via depth -> StretchTarget), it must not widen the canal further (folding it
                    // in ballooned the rings to ~max on a hard push). gFactor stays in the reading for the gFac= debug.
                    float g = (RefGirth > 1e-5f) ? _bp.girthTip / RefGirth : 1f;
                    girthScale = Mathf.Clamp(g, 0.4f, MaxGirthScale);
                    lengthScale = (RefLength > 1e-4f) ? Mathf.Clamp(_bp.baseLen / RefLength, 0.6f, 2.0f) : 1f;
                }
            }

            // ALSO react to a DynamicBoneCollider on a bottle/toy pushed in (a KKPE BP collider). READ-ONLY; the
            // scan + canal bake are 2s-cached. Depth tracks the collider's deeper SURFACE point, so a collider
            // sitting at/below the entrance reads as OUTSIDE -> closed, same as a withdrawn penis.
            // This runs REGARDLESS of the penis path (previously gated behind !engaged) and the womb reacts to the
            // DEEPER of penis vs toy — so a toy inserted deeper than (or instead of) a barely-engaged penis drives
            // the womb instead of being ignored. Wrapped so a bad bake / reflection hiccup can NEVER abort LateUpdate.
            _colLateral = -1f; _colName = null; _colPerRing = false;
            // Off-screen skip (perf, scales with womb count): a NON-engaged womb that is NOT visible has no reaction
            // a viewer could see, so skip the per-frame collider scan for it. `engaged` here is the penis-path result,
            // so a penis-engaged (mid-thrust) womb always runs even if its bounds get culled — no freeze. The ring loop
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
                                                        WombBulbRadius(), LiquidWobbleMPBPlugin.CfgColliderName,
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
                            _colPerRing = true;   // _colRingRadius[] holds the per-ring thickest collider -> ring loop uses it
                            _bp = new BPBridge.Reading {
                                found = true, depth = cdepth, visualDepth = cdepth,
                                girthBase = crad, girthTip = crad, girthFactor = 1f, baseLen = 0f,
                                tipPos = ctip,
                                tipDir = (ctip - cbase).sqrMagnitude > 1e-8f ? (ctip - cbase).normalized : Vector3.up,
                                hasPose = true };
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    if (!_colWarned) { LiquidWobbleMPBPlugin._logger?.LogWarning("WombExpand collider-react failed (disabled for this womb): " + ex.Message); _colWarned = true; }
                }
            }

            // Engagement diagnostic: surface ONCE when BP clearly penetrates but the womb won't react because the
            // tip reads off this womb's canal axis — the classic "BP works, expansion doesn't" case. Ungated so it
            // shows without Debug Log; re-arms when the womb next engages.
            _dbgFound = _bp.found;
            _dbgEngaged = engaged;     // debug markers: only show the penis-tip marker for a womb that's actually engaged
            if (engaged) _notEngagedWarned = false;
            else if (_bp.found && _bp.depth > EngageEps && _dbgTipKnown && !_dbgContained && !_notEngagedWarned)
            {
                _notEngagedWarned = true;
                LiquidWobbleMPBPlugin._logger?.LogWarning($"WombExpand '{name}': BP reports penetration (depth={_bp.depth:F2}) but the womb is NOT reacting — the penis tip is OUTSIDE this womb's volume (off-axis {Mathf.RoundToInt(_bpLateral * 1000f)}mm). Likely the womb is mis-placed/rotated vs the penis. Fix: re-seat/re-orient the womb on the penis path. Turn on 'Debug Log' for the full readout.");
            }

            // Rise fast (DepthSmoothing), FALL slow (CloseSmoothing) so the depth lingers on withdrawal ->
            // the rings close LATER (follow-through), while opening still tracks the penis going in.
            float tgtDepth = engaged ? targetNorm : 0f;
            float sm = (tgtDepth >= _depth) ? DepthSmoothing : CloseSmoothing;
            float k = sm > 0f ? 1f - Mathf.Exp(-sm * Time.deltaTime) : 1f;
            _depth = Mathf.Lerp(_depth, tgtDepth, k);

            float closeK    = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(dampTime, 0.02f));  // close = exponential ease-OUT (decelerates smoothly to target, never an instant snap)
            float openStep  = (100f / Mathf.Max(OpenTime, 0.01f)) * Time.deltaTime; // open rate (eases in, not instant)

            int n = _ringIdx.Length;
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

            if (_stretchIdx >= 0)
            {
                // Displace scales with BP penis LENGTH (longer/deeper-set penis -> more displace).
                float st = engaged ? StretchTarget() * lengthScale : 0f;
                if (st >= _stretchReaction) _stretchReaction = st;
                else _stretchReaction = Mathf.Lerp(_stretchReaction, st, closeK);

                _smr.SetBlendShapeWeight(_stretchIdx, Mathf.Clamp(_stretchReaction * strength, 0f, 100f));
            }

            // Mouth leans toward the INCOMING PENIS, matching BP's own kokan-pull: BP translates the vagina along
            // (danEndTarget - entry) — the penis tip's offset from the canal. We use the SAME direction: the penis
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
                    // the tip's offset from the canal axis — on an off-seated overlay that's a big CONSTANT sideways offset
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

            if (DebugLog)
            {
                _logTimer -= Time.deltaTime;
                if (_logTimer <= 0f)
                {
                    _logTimer = LogInterval;
                    var w = new int[n];
                    for (int i = 0; i < n; i++) w[i] = _ringIdx[i] >= 0 ? Mathf.RoundToInt(_smr.GetBlendShapeWeight(_ringIdx[i])) : 0;
                    LiquidWobbleMPBPlugin._logger?.LogInfo(
                        $"{nameof(WombExpandEffect)} '{name}': eng={engaged} depth={_depth:F3} vis={_bp.visualDepth:F3} " +
                        $"raw={_bp.depth:F3} baseLen={_bp.baseLen:F3} gBase={_bp.girthBase:F4} gTip={_bp.girthTip:F4} gFac={_bp.girthFactor:F2} girthScale={girthScale:F2} lenScale={lengthScale:F2} str={strength:F2} sens={sens:F2} damp={dampTime:F2} " +
                        $"src={(_colLateral >= 0f ? $"COLLIDER('{_colName}' depth={_bp.visualDepth:F2} lat={Mathf.RoundToInt(_colLateral * 1000f)}mm)" : (_bpVaginaMain ? "penis-BP/vagina(bones below)" : "penis/canal"))} penisTipLat={(_bpLateral >= 0f ? Mathf.RoundToInt(_bpLateral * 1000f) + "mm/" + Mathf.RoundToInt(PenisInCanalWidth * 1000f) + "mm" : "-")} canalLen={_canalLen:F3} " +
                        $"rings=[{string.Join(",", Array.ConvertAll(w, x => x.ToString()))}] " +
                        $"stretch={(_stretchIdx >= 0 ? Mathf.RoundToInt(_smr.GetBlendShapeWeight(_stretchIdx)) : 0)} " +
                        $"mound(f/b/l/r)={Mathf.RoundToInt(_moundFwdReact)}/{Mathf.RoundToInt(_moundBackReact)}/{Mathf.RoundToInt(_moundLeftReact)}/{Mathf.RoundToInt(_moundRightReact)}");
                    string vagStr = _vaginaDist >= 0f ? Mathf.RoundToInt(_vaginaDist * 1000f) + "mm" : "none";
                    string why = engaged ? ("engaged" + (_bpVaginaMain ? " (BP/vagina; bones below cervix)" : " (canal-contained)"))
                        : !_dbgFound ? $"NO penis/collider within {LiquidWobbleMPBPlugin.CfgPairRange:F2}m of entrance"
                        : (_bp.depth <= EngageEps) ? $"BP not penetrating (raw={_bp.depth:F2}) — penis withdrawn / not inserted (correctly closed)"
                        : _tipDetached ? $"tip marker k_f_dan_end pulled {Mathf.RoundToInt(_tipDist * 1000f)}mm from entrance (> detach {Mathf.RoundToInt(LiquidWobbleMPBPlugin.CfgTipDetach * 1000f)}mm) -> treated as withdrawn (closed). Raise 'Penis tip detach distance' if it closes while still inserted."
                        : _entryDetached ? $"entry marker k_f_dan_entry swung {Mathf.RoundToInt(_entryLat * 1000f)}mm OFF this womb's canal axis (> off-axis limit {Mathf.RoundToInt(LiquidWobbleMPBPlugin.CfgEntryDetach * 1000f)}mm) -> penis base withdrawn (closed). Raise 'Penis entry off-axis limit' if it closes while still inserted at an angle."
                        : (!_vaginaPaired && !_dbgInWomb) ? $"BP penetrating (raw={_bp.depth:F2}) but this womb is NOT on a vagina (nearest cf_J_Vagina_root {vagStr}, range {Mathf.RoundToInt(LiquidWobbleMPBPlugin.CfgAutoBodyRevealRange * 1000f)}mm) AND no bone threads the canal ({Mathf.RoundToInt(_bpLateral * 1000f)}mm off-axis) — re-seat the womb on the woman, or raise 'Womb-in-vagina range'"
                        : _vaginaFarLat ? $"BP penetrating (raw={_bp.depth:F2}) and this womb IS on a vagina, but the penis is {Mathf.RoundToInt(_bpLateral * 1000f)}mm off THIS womb's axis (> {Mathf.RoundToInt(Mathf.Max(WombBulbRadius(), PenisInCanalWidth) * sens * 1000f)}mm gate @ BP_Sensitivity {sens:F2}) — it's in a DIFFERENT/adjacent womb, not this one. Raise BP_Sensitivity if it really is this womb."
                        : "not engaged (other)";
                    LiquidWobbleMPBPlugin._logger?.LogInfo($"  WombExpand '{name}' why={{{why}}} found={_dbgFound} pairedWith={_bp.srcName} hasPose={_bp.hasPose} canalEntrance={_canalEntrance.ToString("F3")} axis={_canalAxis.ToString("F2")} bpTip={_bp.tipPos.ToString("F3")} entranceW={EntranceWorld().ToString("F3")}");
                    // POSITION VERIFY (one scene pass): compare BP's vaginal target (cf_J_Vagina_root) AND the REAL
                    // penis-tip bone (k_f_dan_end — what BP aims / the mesh tips to) against THIS womb's canal. This
                    // distinguishes a wrong TIP SOURCE (our bpTip = BP's last collider centre, may be mid-shaft/base)
                    // from a wrong CANAL BAKE (entrance/axis don't match the visual womb). Excludes the womb's own subtree.
                    {
                        Transform vr = null, de = null; float vb = float.MaxValue, db = float.MaxValue;
                        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                        {
                            if (t == null) continue;
                            if (t.name == "cf_J_Vagina_root" && !t.IsChildOf(transform)) { float d = (t.position - _canalEntrance).sqrMagnitude; if (d < vb) { vb = d; vr = t; } }
                            else if (t.name == "k_f_dan_end")                            { float d = (t.position - _canalEntrance).sqrMagnitude; if (d < db) { db = d; de = t; } }
                        }
                        Vector3 wE = EntranceWorld();
                        if (vr != null)
                            LiquidWobbleMPBPlugin._logger?.LogInfo($"  VAGINA-CHECK cf_J_Vagina_root={vr.position.ToString("F3")}  wombEntrance<->vagina={Mathf.RoundToInt((wE - vr.position).magnitude * 1000f)}mm  bpTip<->vagina={Mathf.RoundToInt((_bp.tipPos - vr.position).magnitude * 1000f)}mm");
                        else LiquidWobbleMPBPlugin._logger?.LogInfo("  VAGINA-CHECK no cf_J_Vagina_root found in scene");
                        if (de != null)
                        {
                            Vector3 rel = de.position - _canalEntrance;
                            float dAlong = Vector3.Dot(rel, _canalAxis);
                            float dLat = (rel - dAlong * _canalAxis).magnitude;
                            LiquidWobbleMPBPlugin._logger?.LogInfo($"  TIP-CHECK k_f_dan_end(REAL tip)={de.position.ToString("F3")} -> canal along={Mathf.RoundToInt(dAlong * 1000f)}mm lat={Mathf.RoundToInt(dLat * 1000f)}mm  ||  bpTip(now used)={_bp.tipPos.ToString("F3")} lat={Mathf.RoundToInt(_bpLateral * 1000f)}mm");
                            Bounds wb = _smr.bounds;   // world-space AABB of the womb mesh — is the REAL tip inside the womb VOLUME?
                            LiquidWobbleMPBPlugin._logger?.LogInfo($"  AABB-CHECK womb bounds center={wb.center.ToString("F3")} extents={wb.extents.ToString("F3")}  tipInBounds={wb.Contains(de.position)}  tip<->center={Mathf.RoundToInt((de.position - wb.center).magnitude * 1000f)}mm");
                        }
                        else LiquidWobbleMPBPlugin._logger?.LogInfo("  TIP-CHECK no k_f_dan_end found in scene");
                    }
                    // SHAFT-DIAG (build 356): every cm_j_dan bone's canal projection (along/lat mm vs entrance), so we
                    // can SEE where the shaft actually is relative to the uterus canal vs where k_f_dan_end is aimed.
                    if (_bp.shaftPos != null && _canalLen > 1e-4f)
                    {
                        var sd = new System.Text.StringBuilder("  SHAFT-DIAG (along/lat mm, " + _bp.shaftPos.Length + " bones): ");
                        for (int ks = 0; ks < _bp.shaftPos.Length; ks++)
                        {
                            Vector3 rel = _bp.shaftPos[ks] - _canalEntrance;
                            float a = Vector3.Dot(rel, _canalAxis);
                            float l = (rel - a * _canalAxis).magnitude;
                            sd.Append(Mathf.RoundToInt(a * 1000f)).Append('/').Append(Mathf.RoundToInt(l * 1000f)).Append(' ');
                        }
                        LiquidWobbleMPBPlugin._logger?.LogInfo(sd.ToString());
                    }
                    // GIRTH-DIAG (build 368): the penis girth PROFILE (distFromTip mm : radius mm) + the resulting
                    // per-ring girth (V1..Vn = radius mm / scale) at the current depth, to verify the taper.
                    if (_bp.girthPos != null && _bp.girthRad != null && _canalLen > 1e-4f)
                    {
                        var gd = new System.Text.StringBuilder("  GIRTH-DIAG depth=" + _depth.ToString("F2") + " refG=" + Mathf.RoundToInt(RefGirth * 1000f) + "mm profile[distTip:rad]= ");
                        for (int ks = 0; ks < _bp.girthPos.Length; ks++)
                            gd.Append(Mathf.RoundToInt((_bp.girthPos[ks] - _bp.tipPos).magnitude * 1000f)).Append(':').Append(Mathf.RoundToInt(_bp.girthRad[ks] * 1000f)).Append(' ');
                        gd.Append("| per-ring= ");
                        int nn = _ringIdx.Length;
                        for (int ri = 0; ri < nn; ri++)
                        {
                            float rd = nn == 1 ? DepthStart : Mathf.Lerp(DepthStart, DepthEnd, (float)ri / (nn - 1));
                            float lr = GirthAtDistFromTip(Mathf.Max(0f, _depth - rd) * _canalLen);
                            float sc = (lr > 0f && RefGirth > 1e-5f) ? Mathf.Clamp(lr / RefGirth, 0.4f, MaxGirthScale) : girthScale;
                            gd.Append('V').Append(ri + 1).Append('=').Append(Mathf.RoundToInt(lr * 1000f)).Append("mm/").Append(sc.ToString("F2")).Append(' ');
                        }
                        LiquidWobbleMPBPlugin._logger?.LogInfo(gd.ToString());
                    }
                    if (LiquidWobbleMPBPlugin.CfgReactColliders && _canalLen > 1e-4f)
                    {
                        LiquidWobbleMPBPlugin._logger?.LogInfo($"  DynamicBoneColliders (<=2m | d=centre-depth, r=world radius): {ColliderBridge.DebugListNear(_canalEntrance, _canalAxis, 2.0f, _canalLen)}");
                        // COLLIDER-DIAG (build 363): replay the actual engage gates so we SEE why a collider 'inside'
                        // doesn't drive the womb (wrong name filter / off-axis / below entrance / too fat). Runs even
                        // when the penis path engaged (the collider path is gated behind !engaged), so it's visible.
                        LiquidWobbleMPBPlugin._logger?.LogInfo($"  COLLIDER-DIAG penisEngaged={engaged}  " + ColliderBridge.DebugDecision(_canalEntrance, _canalAxis, LiquidWobbleMPBPlugin.CfgColliderRange, LiquidWobbleMPBPlugin.CfgColliderInCanal, _canalLen, LiquidWobbleMPBPlugin.CfgColliderMaxRadius, WombBulbRadius(), LiquidWobbleMPBPlugin.CfgColliderName));
                        if (_colPerRing && _colRingRadius != null)
                        {
                            var cr = new System.Text.StringBuilder("  COLLIDER-PERRING (thickest collider radius mm at each ring): ");
                            for (int ri = 0; ri < _colRingRadius.Length; ri++) cr.Append('V').Append(ri + 1).Append('=').Append(Mathf.RoundToInt(_colRingRadius[ri] * 1000f)).Append("mm ");
                            LiquidWobbleMPBPlugin._logger?.LogInfo(cr.ToString());
                        }
                    }
                }
            }

            // Debug markers are placed from OnPreCullCanal (post-IK) so they sit on the rendered womb, not the pre-IK pose.
            if (!DebugLog && _mkEntrance != null) UpdateDebugMarkers();   // still allow teardown the frame Debug Log goes off
        }

        // Ring opens as depth reaches it: closed until (ringDepth - width), full at/after ringDepth.
        // `width` is the per-ring opening softness (the entrance uses a wider window). The caller passes
        // (ringDepth - OpenLead) so the ring reaches full slightly AHEAD of the penis (anticipation).
        private float OpenFraction(float ringDepth, float width)
        {
            width = Mathf.Max(width, 1e-4f);
            float t = Mathf.Clamp01((_depth - (ringDepth - width)) / width);
            return Mathf.SmoothStep(0f, 1f, t);
        }

        // Stretch ramps StretchStart..1 to StretchMax, then overshoot beyond 1, capped.
        private float StretchTarget()
        {
            if (_depth <= StretchStart) return 0f;
            if (_depth <= 1f)
            {
                float span = Mathf.Max(1f - StretchStart, 1e-4f);
                return Mathf.SmoothStep(0f, 1f, (_depth - StretchStart) / span) * StretchMax;
            }
            return Mathf.Min(StretchMax + (_depth - 1f) * StretchOvershoot, StretchCap);
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

        /// <summary>World position of the womb canal entrance (the item's own cf_j_kokan bone). Used by
        /// AutoBodyReveal to match a womb to the nearest character's vagina.</summary>
        public Vector3 EntranceWorld()
        {
            if (_entranceBone == null)
                foreach (var t in GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == "cf_j_kokan") { _entranceBone = t; break; }
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
                        if (im.HasProperty("_OutBodyBackOcclude")) im.SetFloat("_OutBodyBackOcclude", 1f);
                        if (im.HasProperty("_OutBodySceneConfine")) im.SetFloat("_OutBodySceneConfine", 1f);
                    }
                LiquidWobbleMPBPlugin._logger?.LogInfo(
                    $"[spawn-default] '{name}': BodyReveal applied -> out-of-body interior+cum restored (occlude=1, sceneConfine=1).");
                return;
            }
        }
    }
}
