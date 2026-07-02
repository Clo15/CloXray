using UnityEngine;
using System.Collections.Generic;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// Drives RSkoi/LiquidShader-style liquid materials (e.g. CloXray/Liquid) from
    /// the host object's own motion and world scale, all through a single
    /// renderer-wide <see cref="MaterialPropertyBlock"/> written once per
    /// <c>LateUpdate</c>.
    ///
    /// Per frame it writes, on the same block:
    ///   * <c>_RotationX</c> / <c>_RotationZ</c> — a damped, oscillating sway derived
    ///     from how the transform moved this frame (the "wobble").
    ///   * <c>_ObjectScaleVec</c> (float4) — the live rest->world scale, so the
    ///     liquid's vertical cap snap tracks Studio scaling and stays crack-free
    ///     under blendshapes (a single uniform value, not per-triangle).
    ///
    /// The block override applies to every material slot of the renderer; only the
    /// liquid material declares these properties, so the organ/interior materials
    /// ignore them.
    ///
    /// Inspired by RSkoi's work:
    ///   https://github.com/RSkoi/LiquidShaderWobble
    ///   https://github.com/RSkoi/LiquidShader
    ///   https://github.com/RSkoi/ComponentUtil
    /// </summary>
    public class LiquidWobbleMPBEffect : MonoBehaviour
    {
        // Wobble tunables (surfaced and edited through ComponentUtil).
        public string ShaderRotXPropName { get; set; } = "_RotationX";
        public string ShaderRotZPropName { get; set; } = "_RotationZ";
        public float MaxWobble { get; set; } = 0.03f;
        public float WobbleSpeed { get; set; } = 1f;
        public float Recovery { get; set; } = 1f;

        // Thrust slosh: the BetterPenetration thrust speed (d depth/dt) jolts the wobble so the
        // cum sloshes with each thrust. Modes: 0 = off (BP never read), 1 = global (one slosh on
        // both chambers), 2 = per-chamber (womb/tube slosh independently, proximity-gated to each
        // chamber centre; drives _PerChamberWobble + _Rotation2X/Z; needs the BP tip pose).
        public float ThrustSlosh { get; set; } = 0f;
        public float ThrustSloshGain { get; set; } = 2f;

        // The wobble SETUP tunables above (MaxWobble/WobbleSpeed/Recovery/ThrustSlosh/ThrustSloshGain) are
        // ALSO surfaced as material properties so they can be tuned in Material Editor. When a live material
        // declares them, the plugin reads them from it on this throttle (setup data, not per-frame) and they
        // become the source of truth -> ComponentUtil edits to these get overwritten; tune them in Material
        // Editor. SetupReadPeriod = 0 disables the material read (ComponentUtil-only, old behaviour).
        public float SetupReadPeriod { get; set; } = 0.5f;

        // Scale-feed tunables.
        public string ShaderScalePropName { get; set; } = "_ObjectScaleVec";
        // written = source.lossyScale * ScaleMultiplier. Lets us calibrate to
        // rigs that bake in a non-1 base scale without recompiling.
        public float ScaleMultiplier { get; set; } = 1f;

        // World-up expressed in the rest (mesh-local) frame, so the shader can keep the
        // fill plane horizontal to gravity while doing its fill math in rest space.
        // Uses the same source transform as the scale feed for consistency.
        public string ShaderRestWorldUpPropName { get; set; } = "_RestWorldUp";

        private const float CrossAxisFactor = 0.2f;
        // Below this the scale is treated as degenerate (object disabled, mid-load,
        // re-parenting). We skip the write so we never feed the 0 = auto sentinel.
        private const float MinValidScale = 1e-4f;

        private Renderer _renderer;
        private SkinnedMeshRenderer _smr;
        private MaterialPropertyBlock _block;
        private Material _liquidMat;   // the cum material (has the fill bounds), for chamber centres
        private int _tubeBoneIdx = -1; // index in _smr.bones of a canal-axis bone (tube centre anchor)
        // Bone the tube centre is anchored to for the skinning map (on the canal axis, tube base).
        public string TubeCenterBone { get; set; } = "cf_j_kokan";
        private int _wombBoneIdx = -1; // index in _smr.bones of the womb (uterus) bone (womb centre anchor)
        // Bone the womb centre is anchored to. The womb bulb rides a DIFFERENT bone than the tube,
        // so it needs its own world anchor (extrapolating from the tube across the neck undershoots
        // ~40%). Default cf_s_waist02 (pelvis bone nearest the bulb); swap to cf_s_waist01 via
        // ComponentUtil if the fill level reads wrong — resolved live, no restart needed.
        public string WombCenterBone { get; set; } = "cf_s_waist02";

        // LIVE per-chamber world-Y extent, measured from the actual skinned+blendshaped cum verts.
        // The rest box can't see blendshape inflation or per-chamber scale, so fill=1 undershot the
        // real bulb; the shader places the fill surface by lerping this measured extent
        // (_Chamber1ExtentY / _Chamber2ExtentY).
        private int[] _cumVerts;            // vertex indices of submesh 0 (the cum), triangle list (6x duplicated)
        private int[] _cumVertsUnique;      // DISTINCT submesh-0 vertex indices — what the per-frame extent loop walks
        private Vector3[] _restVerts;       // base (rest) vertex positions, object space (cached)
        private float _extentClock = 999f;  // high => measure on the first LateUpdate
        private bool _wombBoneWarned;       // warn once if WombCenterBone isn't found (silent ~40% undershoot otherwise)
        // ── STAGE-2 PRECOMPUTED SKINNED BASIS — the ONLY measurement source (see DESIGN_NOTES.md). ──
        // Skinning is linear and the womb rig is internally rigid, so a cum vert's baked-frame
        // position is rest + Σ wₛ·deltaₛ with rest/deltaₛ constant, captured ONCE at spawn by
        // decomposing BakeMesh. A dirty frame is then a ~1.1k-vert weighted sum, not a whole-mesh
        // CPU skin. The per-frame BakeMesh and the manual-skin fallback are gone; assumption
        // violations warn loudly (spawn + per-frame rigidity assertions) and keep running.
        private bool        _basisCaptured;
        private Vector3[]   _basisRest;     // [k over _cumVertsUnique] baked-frame vert, ALL weights 0, ÷ _basisScale0
        private Vector3[][] _basisDelta;    // [shape][k] baked delta at weight 100, ÷ _basisScale0; null = no cum effect
        private Vector3     _basisScale0;   // renderer lossyScale at capture (basis stored normalized by it)
        private Vector3[]   _lpCache;       // [k] last evaluated NORMALIZED baked-frame verts (current weights)
        private Vector3[]   _rigLocPos;     // per-bone LOCAL pos/rot/scale at capture — the rigidity snapshot
        private Quaternion[] _rigLocRot;
        private Vector3[]   _rigLocScl;
        private float       _rigWarnClock;  // throttle: rigidity warning at most every 5s
        private bool        _scaleWarned;   // one-time: non-uniform RELATIVE rescale (component model approximate)
        // Half the 3mm CUM_CHAMBER_GAP (build CUM_CHAMBER_GAP): the rest-Y midpoint of the gap between the
        // womb floor and tube ceiling, used to classify a vert into its chamber. Keep in sync with the
        // shader's CUM_SPLIT_BIAS and the build's CUM_CHAMBER_GAP/2.
        private const float ChamberSplitBias = 0.0015f;
        // DIAGNOSTIC build stamp — bump every plugin build so the log proves which DLL is live.
        public const int PluginBuild = 438;   // build stamp — bump every build. Full history: LiquidShaderWobble\CHANGELOG.md
        // 0 = measure the chamber extents every frame (dirty-gated). Throttling made the fill
        // plane lag the geometry during rotation; a dirty frame is cheap (basis weighted sum + walk).
        public float ExtentPeriod { get; set; } = 0f;

        // ── Cap contact profile (chamber 1/womb) — shader rim-clamp feed ──
        // The cum-wall cross-section, measured in the same walk as the extents: ProfH world-Y
        // rows, each with an XZ centroid (_CapProfC) and ProfA angular max radii (_CapProfR).
        // The shader clamps cap verts onto this so the low-fill rim is the true contact line.
        // Sizes MUST match the shader's CAPPROF_H / CAPPROF_A. Persistent buffers (no per-
        // measurement GC); re-pushed every FeedFrame (MPB array persistence is unverified in
        // Unity 5.6, so don't rely on it). See DESIGN_NOTES.md.
        private const int ProfH = 16, ProfA = 16;
        private Vector3[] _profWp;          // scratch: this measurement's chamber-1 world verts
        private int       _profCount;
        private bool      _profValid;       // true once a measurement has filled the buffers
        private readonly float[]   _profSumX = new float[ProfH];
        private readonly float[]   _profSumZ = new float[ProfH];
        private readonly int[]     _profCnt  = new int[ProfH];
        private readonly bool[]    _profRowAny = new bool[ProfH];
        private readonly Vector4[] _profC    = new Vector4[ProfH];
        private readonly float[]   _profR    = new float[ProfH * ProfA];
        private readonly float[]   _profRScratch = new float[ProfH * ProfA];

        private Vector3 _prevPosition;
        private Vector3 _prevEuler;
        private float _prevDepth;       // last BP penetration depth — thrust-slosh velocity source
        private bool  _hasPrevDepth;    // false until the first valid BP read (no spurious first-frame jolt)
        private float _setupClock = 999f;  // high => read the Material-Editor setup floats on the first frame
        private bool  _setupMatWarned;     // warn once if no live material exposes the setup props

        // Accumulated sway amplitude per axis; bleeds back to zero over time.
        private float _amplitudeX;
        private float _amplitudeZ;
        // Chamber-2 (tube) sway amplitude — used only in per-chamber slosh mode (ThrustSlosh=2). The
        // womb is chamber 1 = _amplitudeX/Z above; the tube is chamber 2 = these.
        private float _amplitudeX2;
        private float _amplitudeZ2;
        // Last world chamber centres (captured when _Box1/2CenterWorld are written) for the thrust
        // proximity gate; the flags say whether this frame produced a valid centre.
        private Vector3 _wombCenterW, _tubeCenterW;
        private float _fillLogClock;             // debug: throttle for the cum-fill input log
        private Vector4 _dbgExt1, _dbgExt2;       // debug: captured chamber world-Y extents
        private Vector3 _dbgSkinScale;            // scale from the exact skin matrix (parented-womb-safe source)
        private bool _haveWombC, _haveTubeC;
        private Mesh _bakedMesh;                  // BakeMesh target — SPAWN-ONLY since build 333 (basis capture + one-time assertion)
        private System.Collections.Generic.List<Vector3> _bakedVList;   // reused GetVertices buffer (spawn-only)
        private Matrix4x4[] _sentinelBones;       // bone l2w at last measurement (dirty-check)
        private float[] _sentinelShapes;          // blendshape weights at last measurement (dirty-check)
        private Vector3 _dbgBakeC1;               // debug: measured chamber-1 center (world)
        private Vector3 _dbgV0W;                  // plugin world pos of cum vertex tri0[0] (GPU-vs-plugin crosses)

        // Free-running clock that drives the oscillation phase.
        private float _clock = 0.5f;

        private void Start()
        {
            _renderer = ResolveRenderer();
            if (_renderer == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError(
                    $"{nameof(LiquidWobbleMPBEffect)} on '{name}' found no Renderer (self or children); disabling.");
                enabled = false;
                return;
            }

            _smr = _renderer as SkinnedMeshRenderer;
            _block = new MaterialPropertyBlock();
            // The cum/liquid material carries the chamber bounds; cache it for chamber-centre feed.
            foreach (var m in _renderer.sharedMaterials)
                if (m != null && m.HasProperty("_Bound1MinY_bottom")) { _liquidMat = m; break; }
            // Index of the canal-axis bone in the SMR's bone list (for the tube-centre skinning map).
            if (_smr != null && _smr.bones != null)
                for (int i = 0; i < _smr.bones.Length; i++)
                    if (_smr.bones[i] != null && _smr.bones[i].name == TubeCenterBone) { _tubeBoneIdx = i; break; }
            if (_smr != null && _smr.bones != null && _tubeBoneIdx < 0)
                LiquidWobbleMPBPlugin._logger?.LogWarning(
                    $"[bone] TubeCenterBone '{TubeCenterBone}' not found on '{name}' — tube centre anchor disabled (fill may fall back to the box-centre estimate).");
            // Seed history with the current pose so the first frame produces no spike.
            _prevPosition = transform.position;
            _prevEuler = transform.rotation.eulerAngles;

            // Bootstrap WombExpandEffect onto this womb item's root (no 2s scan, no baked
            // MonoScript). Runtime AddComponent is safe; a freshly-baked MonoScript crashed
            // Unity's native loader. Scoped to the item via the plugin's FindItemRoot.
            if (_smr != null) LiquidWobbleMPBPlugin.EnsureWombExpand(transform);   // skinned womb ONLY — a bottle (plain MeshRenderer) that got this via the hotkey is not a womb

            // Strip leftover DynamicBone COLLIDERS from the womb skeleton: the kept body-accessory
            // bones carry the body's colliders, so skirt physics collide with the womb and get
            // pushed around. Destroy (not disable) — old DynamicBone versions don't all check
            // .enabled but treat a destroyed entry as null. Scoped to this item's subtree.
            int strippedColliders = 0;
            if (_smr != null)   // womb ONLY: never strip a bottle's DynamicBoneCollider (it may be the womb's reaction collider)
            foreach (var comp in GetComponentsInChildren<Component>(true))
            {
                if (comp == null || comp == this) continue;
                string tn = comp.GetType().Name;
                if (tn.StartsWith("DynamicBoneCollider") || tn == "DynamicBonePlaneCollider")
                {
                    Destroy(comp);
                    strippedColliders++;
                }
            }
            if (strippedColliders > 0)
                LiquidWobbleMPBPlugin._logger?.LogInfo(
                    $"[collider] stripped {strippedColliders} DynamicBone collider(s) from '{name}' — the womb no longer pushes clothes/skirt physics.");

            // NEW-SPAWN DEFAULT: a womb without BodyReveal shows only the shell — interior+cum are
            // hidden out-of-body via _OutBodyBackOcclude=0 (see XRAY_RENDER_MODEL.md). AutoBodyReveal
            // raises it back to 1 when the stamp is applied for this womb's character. Set on the
            // material INSTANCE so multiple wombs stay independent and the ME slider keeps working;
            // skipped when the bake already says 0.
            foreach (var sm in _renderer.sharedMaterials)
            {
                if (sm != null && sm.shader != null && sm.shader.name == "CloXray/Organ" &&
                    sm.HasProperty("_OutBodyBackOcclude") && sm.GetFloat("_OutBodyBackOcclude") > 0.5f)
                {
                    foreach (var im in _renderer.materials)
                        if (im != null && im.shader != null && im.shader.name == "CloXray/Organ" &&
                            im.HasProperty("_OutBodyBackOcclude"))
                            im.SetFloat("_OutBodyBackOcclude", 0f);
                    LiquidWobbleMPBPlugin._logger?.LogInfo(
                        $"[spawn-default] '{name}': interior+cum hidden out-of-body until BodyReveal is applied (hotkey / auto-apply).");
                    break;
                }
            }

            // Build stamp on load — proves which DLL is live the instant a womb spawns.
            float sv = (_liquidMat != null && _liquidMat.HasProperty("_ShaderVersion"))
                       ? _liquidMat.GetFloat("_ShaderVersion") : -1f;
            LiquidWobbleMPBPlugin._logger?.LogInfo(
                $"[BUILD] LiquidWobbleMPB pluginBuild={PluginBuild}  shaderVersion={sv:F0}  on '{name}'");
        }

        // ── Frame-feed timing ──
        // The pose-dependent feed must sample the bones AFTER IK has written the final pose.
        // IK runs in LateUpdate and LateUpdate-vs-LateUpdate order is script-execution roulette,
        // so we feed from Camera.onPreCull — strictly after all LateUpdates, just before culling:
        // the exact pose the GPU is about to draw. See DESIGN_NOTES.md.
        private int _lastFeedFrame = -1;
        private Camera.CameraCallback _preCull;

        private void OnEnable()
        {
            _preCull = OnPreCullCam;
            Camera.onPreCull += _preCull;
        }

        private void OnDisable()
        {
            if (_preCull != null) Camera.onPreCull -= _preCull;
        }

        private void OnPreCullCam(Camera cam)
        {
            if (Time.frameCount == _lastFeedFrame) return;        // first camera of the frame only
            _lastFeedFrame = Time.frameCount;
            FeedFrame();
        }

        private void FeedFrame()
        {
            if (_renderer == null || _block == null)
                return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled)
                return;   // master toggle OFF -> freeze the liquid (the MPB keeps its last-written fill/wobble values)
            if (string.IsNullOrEmpty(ShaderRotXPropName) || string.IsNullOrEmpty(ShaderRotZPropName))
                return;

            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            _clock += dt;

            // Clear the chamber-centre freshness flags EACH frame; the centre block below re-sets them only
            // when it actually re-writes a centre this frame. So a frame that skips the centre block (e.g.
            // ShaderScale/RestWorldUp prop names blanked, or the womb bone swapped to a missing one) makes the
            // per-chamber thrust gate fall back to the GLOBAL jolt rather than silently reuse a stale centre.
            _haveWombC = false;
            _haveTubeC = false;

            // Pull the wobble setup floats from the material (Material-Editor control surface), throttled.
            if (SetupReadPeriod > 0f)
            {
                _setupClock += dt;
                if (_setupClock >= SetupReadPeriod) { _setupClock = 0f; RefreshSetupFromMaterial(); }
            }

            // 1) Amplitude relaxes toward rest. Lerp(a, 0, dt*Recovery) == a*(1 - dt*Recovery).
            float decay = 1f - Mathf.Clamp01(dt * Recovery);
            _amplitudeX *= decay;
            _amplitudeZ *= decay;
            _amplitudeX2 *= decay;   // chamber-2 (tube) amplitude decays the same way
            _amplitudeZ2 *= decay;

            // 2) Oscillate the current amplitude.
            float omega = 2f * Mathf.PI * WobbleSpeed;
            float wave = Mathf.Sin(omega * _clock);

            // 3) One block, one write: rotations always, scale when valid.
            //    GetPropertyBlock first preserves any existing override (and keeps the
            //    last good scale on frames we skip it).
            _renderer.GetPropertyBlock(_block);
            _block.SetFloat(ShaderRotXPropName, _amplitudeX * wave);
            _block.SetFloat(ShaderRotZPropName, _amplitudeZ * wave);
            // Per-chamber slosh (ThrustSlosh=2): tell the shader to use _Rotation2X/Z for the tube and
            // push the tube's own sway. Off otherwise -> _PerChamberWobble=0 = the shader's old global
            // wobble (regression-safe), and we don't touch _Rotation2*.
            bool perChamber = ThrustSlosh > 1.5f;
            _block.SetFloat("_PerChamberWobble", perChamber ? 1f : 0f);
            if (perChamber)
            {
                _block.SetFloat("_Rotation2X", _amplitudeX2 * wave);
                _block.SetFloat("_Rotation2Z", _amplitudeZ2 * wave);
            }

            // Scale and world-up share the same rest-source transform so they stay
            // consistent (both bone-based, or both transform-based).
            if (!string.IsNullOrEmpty(ShaderScalePropName) || !string.IsNullOrEmpty(ShaderRestWorldUpPropName))
            {
                Transform restSource = ResolveRestSource();

                if (!string.IsNullOrEmpty(ShaderScalePropName))
                {
                    Vector3 scale = restSource.lossyScale * ScaleMultiplier;
                    // .y is what the shader's cap snap consumes; gate on it but send xyz.
                    if (scale.y > MinValidScale)
                        _block.SetVector(ShaderScalePropName, new Vector4(scale.x, scale.y, scale.z, 1f));
                }

                if (!string.IsNullOrEmpty(ShaderRestWorldUpPropName))
                {
                    // World-up in the rest frame. Direction-only (ignores scale/position),
                    // so no normalization needed; the shader normalizes.
                    Vector3 up = restSource.InverseTransformDirection(Vector3.up);
                    _block.SetVector(ShaderRestWorldUpPropName, new Vector4(up.x, up.y, up.z, 0f));
                }

                // Tube centre in WORLD via EXACT skinning: map the rest box centre through the bind
                // pose × bone world matrix of a bone on the canal axis (cf_j_kokan). (restSource.
                // TransformPoint was wrong — mesh-local space != bone-local space.) The shader fans
                // the tube cap to this centre to fill the open-cylinder annulus hole.
                if (_liquidMat != null && _smr != null && _tubeBoneIdx >= 0)
                {
                    var mesh = _smr.sharedMesh;
                    var bones = _smr.bones;
                    if (mesh != null && _tubeBoneIdx < bones.Length && bones[_tubeBoneIdx] != null)
                    {
                        Matrix4x4 skin = bones[_tubeBoneIdx].localToWorldMatrix * mesh.bindposes[_tubeBoneIdx];
                        Vector3 w2 = skin.MultiplyPoint3x4(ChamberCenterRest(true));
                        _block.SetVector("_Box2CenterWorld", new Vector4(w2.x, w2.y, w2.z, 1f));
                        _tubeCenterW = w2; _haveTubeC = true;   // capture for the thrust proximity gate
                    }
                }

                // Womb (chamber 1) centre in WORLD, anchored to its OWN bone. The womb bulb and
                // the tube ride different bones, so the shader can't extrapolate the womb plane
                // from the tube anchor across the neck (it undershot ~40% at fill=1). Resolve the
                // bone index live so WombCenterBone can be swapped via ComponentUtil with no restart.
                if (_liquidMat != null && _smr != null && _smr.bones != null && _smr.sharedMesh != null)
                {
                    var bones = _smr.bones;
                    var mesh  = _smr.sharedMesh;
                    if (_wombBoneIdx < 0 || _wombBoneIdx >= bones.Length ||
                        bones[_wombBoneIdx] == null || bones[_wombBoneIdx].name != WombCenterBone)
                    {
                        _wombBoneIdx = -1;
                        for (int i = 0; i < bones.Length; i++)
                            if (bones[i] != null && bones[i].name == WombCenterBone) { _wombBoneIdx = i; break; }
                    }
                    if (_wombBoneIdx < 0)
                    {
                        if (!_wombBoneWarned)   // warn ONCE, not every frame
                        {
                            LiquidWobbleMPBPlugin._logger?.LogWarning(
                                $"[bone] WombCenterBone '{WombCenterBone}' not found on '{name}' — womb anchor falls back to the box-centre estimate (~40% undershoot at fill=1). Set WombCenterBone via ComponentUtil.");
                            _wombBoneWarned = true;
                        }
                    }
                    else { _wombBoneWarned = false; }
                    if (_wombBoneIdx >= 0 && _wombBoneIdx < mesh.bindposes.Length && bones[_wombBoneIdx] != null)
                    {
                        Matrix4x4 skinW = bones[_wombBoneIdx].localToWorldMatrix * mesh.bindposes[_wombBoneIdx];
                        Vector3 w1 = skinW.MultiplyPoint3x4(ChamberCenterRest(false));
                        _block.SetVector("_Box1CenterWorld", new Vector4(w1.x, w1.y, w1.z, 1f));
                        _wombCenterW = w1; _haveWombC = true;   // capture for the thrust proximity gate

                        // Cum SCALE from the SAME exact skin matrix (column magnitudes = rest->world scale),
                        // not restSource.lossyScale: a womb parented under a rescaled character keeps its
                        // ACTUAL rendered size here, so the cap-snap stays consistent with the centers/extents.
                        // Overrides the lossyScale write above (falls back to it when the skin scale is invalid).
                        _dbgSkinScale = new Vector3(
                            ((Vector3)skinW.GetColumn(0)).magnitude,
                            ((Vector3)skinW.GetColumn(1)).magnitude,
                            ((Vector3)skinW.GetColumn(2)).magnitude) * ScaleMultiplier;
                        if (!string.IsNullOrEmpty(ShaderScalePropName) && _dbgSkinScale.y > MinValidScale)
                            _block.SetVector(ShaderScalePropName, new Vector4(_dbgSkinScale.x, _dbgSkinScale.y, _dbgSkinScale.z, 1f));
                    }
                }

                // Live per-chamber world-Y extents (dirty-gated precomputed basis) so the fill plane reaches
                // the REAL bulb top, immune to blendshape inflation / per-chamber scale / pose.
                _extentClock += dt;
                if (_extentClock >= ExtentPeriod) { _extentClock = 0f; UpdateChamberExtents(); }

                // DEBUG (gated by WombExpand 'Debug Log'): print the cum-fill WORLD inputs so a fill
                // desync can be traced. v0W pairs with the bounds-overlay crosses (plugin's value for
                // the GS's probe vertex); bakeC1 = measured chamber-1 center; ext = live Y extents.
                if (LiquidWobbleMPBPlugin.Configured && LiquidWobbleMPBPlugin.CfgDebugLog)
                {
                    _fillLogClock += dt;
                    if (_fillLogClock >= 2f)
                    {
                        _fillLogClock = 0f;
                        Vector3 ls = restSource.lossyScale;
                        Vector3 up = restSource.InverseTransformDirection(Vector3.up);
                        Vector3 rls = _renderer != null ? _renderer.transform.lossyScale : Vector3.one;
                        LiquidWobbleMPBPlugin._logger?.LogInfo(
                            $"[fill] '{name}' v0W=({_dbgV0W.x:F3},{_dbgV0W.y:F3},{_dbgV0W.z:F3}) bakeC1=({_dbgBakeC1.x:F3},{_dbgBakeC1.y:F3},{_dbgBakeC1.z:F3}) rendLossy=({rls.x:F4},{rls.y:F4},{rls.z:F4}) " +
                            $"skin=({_dbgSkinScale.x:F3},{_dbgSkinScale.y:F3},{_dbgSkinScale.z:F3}) lossy=({ls.x:F3},{ls.y:F3},{ls.z:F3}) up=({up.x:F2},{up.y:F2},{up.z:F2}) " +
                            $"wombC.y={_wombCenterW.y:F3} tubeC.y={_tubeCenterW.y:F3} ext1=[{_dbgExt1.x:F3}..{_dbgExt1.y:F3}] ext2=[{_dbgExt2.x:F3}..{_dbgExt2.y:F3}]");
                    }
                }
            }

            // ── Tip-detection DEBUG (gated by the F1 "Debug Log" switch — one place for all debug) ──
            // When Debug Log is on, enable the shader's TipDebug overlay (driven here, not a material
            // slider) and push the REAL BP tip pose so the marker can be checked against the penis tip.
            // Off -> overlay off, BP not read (no cost). The plugin owns _ShowTipDebug via the block, so
            // a stale material value can't show it.
            if (_renderer != null)
            {
                bool dbgTip = LiquidWobbleMPBPlugin.CfgDebugLog;
                _block.SetFloat("_ShowTipDebug", dbgTip ? 1f : 0f);
                if (dbgTip)
                {
                    BPBridge.Reading tip;
                    Vector3 dbgAnchor = _haveWombC ? _wombCenterW : transform.position;
                    if (BPBridge.TryReadNear(dbgAnchor, LiquidWobbleMPBPlugin.CfgPairRange, out tip) && tip.found && tip.hasPose)
                    {
                        _block.SetVector("_DebugTipPos",     new Vector4(tip.tipPos.x, tip.tipPos.y, tip.tipPos.z, 1f));
                        _block.SetVector("_DebugTipDir",     new Vector4(tip.tipDir.x, tip.tipDir.y, tip.tipDir.z, 0f));
                        _block.SetFloat ("_DebugTipDepth01", Mathf.Clamp01(tip.visualDepth));
                        _block.SetFloat ("_DebugTipGirth",   tip.girthBase * tip.girthFactor);
                    }
                    else
                    {
                        _block.SetVector("_DebugTipPos", new Vector4(0f, 0f, 0f, 0f));  // w=0 -> overlay skips
                    }
                }
            }

            // Contact profile: re-push the arrays every frame (~1.3 KB, negligible) rather than
            // trust MPB array persistence across the GetPropertyBlock round-trip (unverified in
            // Unity 5.6; its failure mode would be silent). See DESIGN_NOTES.md.
            _block.SetVectorArray("_CapProfC", _profC);
            _block.SetFloatArray("_CapProfR", _profR);
            _block.SetVector("_CapProfInfo", new Vector4(ProfH, ProfA, 0f, _profValid ? 1f : 0f));

            _renderer.SetPropertyBlock(_block);

            // 4) Convert this frame's movement into a fresh impulse.
            Vector3 position = transform.position;
            Vector3 euler = transform.rotation.eulerAngles;

            // Express world velocity in the object's OWN frame — the frame the shader's _RotationX/_RotationZ
            // tilt in (ResolveRestSource = the womb's root bone, or a bottle's own transform; same frame as the
            // _RestWorldUp feed). Without this the slosh axis is world-locked: after you rotate the object,
            // move-in-X still sloshes along the PRE-rotation X. Fixes bottles AND the womb when either rotates.
            Transform wobFrame = ResolveRestSource();
            Vector3 linearW = (_prevPosition - position) / dt;
            Vector3 linear = (wobFrame != null) ? wobFrame.InverseTransformDirection(linearW) : linearW;
            Vector3 angular = euler - _prevEuler;

            // Body-motion impulse (the whole organ moving). Chamber 1 always; in per-chamber mode the tube
            // (chamber 2) gets the SAME body jolt — the whole womb moves together, only the THRUST is gated.
            float bodyImpX = ClampImpulse(linear.x + angular.z * CrossAxisFactor);
            float bodyImpZ = ClampImpulse(linear.z + angular.x * CrossAxisFactor);
            _amplitudeX += bodyImpX;
            _amplitudeZ += bodyImpZ;
            if (ThrustSlosh > 1.5f) { _amplitudeX2 += bodyImpX; _amplitudeZ2 += bodyImpZ; }

            // Thrust slosh: BP penetration SPEED jolts the wobble. depth is relative (penis-vs-vagina) so
            // it's independent of the body-motion impulse above -> they compose, no double count. OFF => BP
            // is never read (no cost). The oscillate+decay above turns each jolt into a damped sway.
            if (ThrustSlosh > 0.5f)
            {
                BPBridge.Reading tip;
                // Pair with the penis nearest THIS womb (multi-womb scenes: each womb sloshes to
                // its own penetration, not the globally deepest one).
                Vector3 sloshAnchor = _haveWombC ? _wombCenterW : transform.position;
                if (BPBridge.TryReadNear(sloshAnchor, LiquidWobbleMPBPlugin.CfgPairRange, out tip) && tip.found)
                {
                    if (_hasPrevDepth)
                    {
                        float depthVel = (tip.depth - _prevDepth) / dt;   // >0 thrust IN, <0 pull OUT
                        // Lean the jolt along the tip's horizontal axis (an angled thrust sloshes sideways;
                        // a purely vertical pump barely tilts). No pose -> a fixed diagonal so it still reads.
                        // Tip direction expressed in the wobble frame too, so the thrust slosh axis tracks the
                        // womb's orientation (same world-lock fix as the body motion above).
                        Vector3 tipDirL = (wobFrame != null && tip.hasPose) ? wobFrame.InverseTransformDirection(tip.tipDir) : tip.tipDir;
                        float dirX = tip.hasPose ? tipDirL.x : 0.7071f;
                        float dirZ = tip.hasPose ? tipDirL.z : 0.7071f;
                        float impX = depthVel * dirX * ThrustSloshGain;
                        float impZ = depthVel * dirZ * ThrustSloshGain;
                        if (ThrustSlosh > 1.5f && tip.hasPose && _haveWombC && _haveTubeC)
                        {
                            // PER-CHAMBER: weight the jolt by closeness to each chamber centre. Squared-
                            // distance ratio -> tip near the womb sends ~all of it to the womb, near the tube
                            // to the tube, with a smooth blend through the neck. Self-scaling (no radius knob).
                            float d2w = (tip.tipPos - _wombCenterW).sqrMagnitude;
                            float d2t = (tip.tipPos - _tubeCenterW).sqrMagnitude;
                            float denom = d2w + d2t + 1e-8f;
                            float wombW = d2t / denom;   // near womb (d2w->0) => wombW->1
                            float tubeW = d2w / denom;   // near tube (d2t->0) => tubeW->1
                            _amplitudeX  += ClampImpulse(impX * wombW);
                            _amplitudeZ  += ClampImpulse(impZ * wombW);
                            _amplitudeX2 += ClampImpulse(impX * tubeW);
                            _amplitudeZ2 += ClampImpulse(impZ * tubeW);
                        }
                        else
                        {
                            // GLOBAL (or per-chamber lacking pose/centres): one shared jolt on chamber 1,
                            // which the shader applies to both chambers when _PerChamberWobble is off.
                            _amplitudeX += ClampImpulse(impX);
                            _amplitudeZ += ClampImpulse(impZ);
                        }
                    }
                    _prevDepth = tip.depth;
                    _hasPrevDepth = true;
                }
                else
                {
                    _hasPrevDepth = false;   // disengaged / BP absent -> re-engage won't fire a huge first jolt
                }
            }

            _prevPosition = position;
            _prevEuler = euler;
        }

        // Pull the wobble/slosh SETUP floats from the LIVE material so they can be tuned in Material Editor.
        // Material Editor edits the renderer's live material instance (not our cached _liquidMat), so scan
        // sharedMaterials for the one that declares the setup props and read them. Throttled by the caller.
        // GetFloat returns the shader-declared default for a property the material hasn't explicitly overridden,
        // so a fresh womb reads the defaults (== the old code defaults) and nothing changes until the user edits.
        private void RefreshSetupFromMaterial()
        {
            if (_renderer == null) return;
            var mats = _renderer.sharedMaterials;
            if (mats != null)
            {
                foreach (var m in mats)
                {
                    // Mode-suffixed name (shader 363+): the ME row label IS the property name, so the
                    // modes are spelled out in it (same convention as _ChamberMode_0single_1connected_2closed).
                    if (m == null || !m.HasProperty("_ThrustSlosh_0off_1global_2perChamber")) continue;
                    float maxW = m.GetFloat("_MaxWobble");
                    float spd  = m.GetFloat("_WobbleSpeed");
                    float rec  = m.GetFloat("_Recovery");
                    float ts   = m.GetFloat("_ThrustSlosh_0off_1global_2perChamber");
                    float tsg  = m.GetFloat("_ThrustSloshGain");
                    // Recovery is the decay rate; its valid range is [0.05,10]. If it reads ~0 the material is
                    // NOT supplying the declared defaults (GetFloat fell through to 0) — applying that would
                    // zero the decay and the amplitude would grow unbounded. Fail loud, keep current values.
                    if (rec <= 0.001f)
                    {
                        if (!_setupMatWarned)
                        {
                            LiquidWobbleMPBPlugin._logger?.LogWarning(
                                $"[setup] material '{m.name}' on '{name}' returned Recovery~0 for the wobble setup props — " +
                                $"NOT applying (would break the wobble); keeping the component's values.");
                            _setupMatWarned = true;
                        }
                        return;
                    }
                    MaxWobble = maxW; WobbleSpeed = spd; Recovery = rec; ThrustSlosh = ts; ThrustSloshGain = tsg;
                    _setupMatWarned = false;
                    return;
                }
            }
            if (!_setupMatWarned)   // fail-loud: setup sliders inactive until a material exposes these props
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning(
                    $"[setup] no live material on '{name}' declares the wobble setup props (_ThrustSlosh_0off_1global_2perChamber/_MaxWobble/...); " +
                    $"Material-Editor setup sliders are inactive — using the component's current values. Redeploy the shader if these are missing.");
                _setupMatWarned = true;
            }
        }

        // The component is usually attached straight to the liquid renderer, but the
        // host bundle may bake it onto a parent (e.g. the asset root) with the renderer
        // a child. Prefer a renderer on this object; otherwise search children and pick
        // the one whose material actually declares the wobble property, so we target the
        // liquid mesh and not a sibling organ/interior mesh.
        private Renderer ResolveRenderer()
        {
            var self = GetComponent<Renderer>();
            if (self != null)
                return self;

            Renderer firstChild = null;
            foreach (var candidate in GetComponentsInChildren<Renderer>(true))
            {
                if (candidate == null)
                    continue;
                if (firstChild == null)
                    firstChild = candidate;
                if (DeclaresWobbleProperty(candidate))
                    return candidate;
            }
            // No child advertises the property; fall back to the first child renderer.
            return firstChild;
        }

        private bool DeclaresWobbleProperty(Renderer candidate)
        {
            var materials = candidate.sharedMaterials;
            if (materials == null)
                return false;
            foreach (var material in materials)
            {
                if (material != null && material.HasProperty(ShaderRotXPropName))
                    return true;
            }
            return false;
        }

        // A CPU-skinned mesh is sized by its bones, so the rootBone defines the
        // authoritative rest->world map (scale and orientation); fall back to our own
        // transform. Both the scale feed and the world-up feed read from this.
        private Transform ResolveRestSource()
        {
            if (_smr != null && _smr.rootBone != null)
                return _smr.rootBone;
            return transform;
        }

        // Shader property IDs for the chamber-bounds floats -- resolved ONCE (Shader.PropertyToID) instead of
        // hashing six strings on every ChamberCenterRest call (called twice per FeedFrame, every frame).
        // GetFloat(int) returns the identical value GetFloat(string) does; only the per-call name hash goes away.
        static readonly int[] _boundIds = {
            Shader.PropertyToID("_Bound3MinX_left"),   Shader.PropertyToID("_Bound4MaxX_right"),
            Shader.PropertyToID("_Bound1MinY_bottom"), Shader.PropertyToID("_Bound2MaxY_top"),
            Shader.PropertyToID("_Bound5MinZ_back"),   Shader.PropertyToID("_Bound6MaxZ_front") };
        static readonly int[] _c2BoundIds = {
            Shader.PropertyToID("_C2Bound3MinX_left"),   Shader.PropertyToID("_C2Bound4MaxX_right"),
            Shader.PropertyToID("_C2Bound1MinY_bottom"), Shader.PropertyToID("_C2Bound2MaxY_top"),
            Shader.PropertyToID("_C2Bound5MinZ_back"),   Shader.PropertyToID("_C2Bound6MaxZ_front") };

        // Centre of a chamber's bounds box in REST (mesh-local) units, read from the liquid material.
        private Vector3 ChamberCenterRest(bool chamber2)
        {
            if (_liquidMat == null) return Vector3.zero;   // defensive: callers guard, but never NRE here
            int[] id = chamber2 ? _c2BoundIds : _boundIds;
            float minX = _liquidMat.GetFloat(id[0]);
            float maxX = _liquidMat.GetFloat(id[1]);
            float minY = _liquidMat.GetFloat(id[2]);
            float maxY = _liquidMat.GetFloat(id[3]);
            float minZ = _liquidMat.GetFloat(id[4]);
            float maxZ = _liquidMat.GetFloat(id[5]);
            return new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
        }

        // ── STAGE-2 basis capture (ONE-TIME at spawn). ──
        // Decompose BakeMesh by linearity: bake once at all-zero weights (= skinned rest), then
        // once per blendshape at weight 100 (delta = bake − rest). Stored scale-normalized so a
        // later uniform rescale just re-multiplies (non-uniform rescale is approximate; warned in
        // the walk). Weights are saved/restored synchronously so no rendered frame sees the probe.
        // Ends with the one-time spawn assertion (basis vs a direct BakeMesh; loud on mismatch,
        // continue). Returns false on a degenerate transient (logged, retried). See DESIGN_NOTES.md.
        private bool CaptureSkinnedBasis(Mesh shared, Transform rt)
        {
            int n = _cumVertsUnique.Length;
            if (rt == null || n == 0) return false;
            Vector3 s0 = rt.lossyScale;
            if (Mathf.Abs(s0.x) < MinValidScale || Mathf.Abs(s0.y) < MinValidScale || Mathf.Abs(s0.z) < MinValidScale)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning(
                    $"[fill] basis capture deferred on '{name}': degenerate lossyScale {s0} (mid-load?) — retrying on the next change.");
                return false;
            }
            Vector3 inv = new Vector3(1f / s0.x, 1f / s0.y, 1f / s0.z);
            int shapeCount = shared.blendShapeCount;
            if (_bakedMesh == null) _bakedMesh = new Mesh();
            if (_bakedVList == null) _bakedVList = new System.Collections.Generic.List<Vector3>(_restVerts.Length);

            float[] savedW = new float[shapeCount];
            for (int s = 0; s < shapeCount; s++)
            {
                savedW[s] = _smr.GetBlendShapeWeight(s);
                _smr.SetBlendShapeWeight(s, 0f);
            }
            string why = null, multiFrame = null;
            var rest = new Vector3[n];
            bool ok = BakeUniqueVerts(rest, inv, ref why);
            Vector3[][] delta = null;
            int kept = 0;
            if (ok)
            {
                delta = new Vector3[shapeCount][];
                var tmp = new Vector3[n];
                for (int s = 0; s < shapeCount; s++)
                {
                    // Multi-frame shapes are PIECEWISE linear — a linear basis can't represent them.
                    if (shared.GetBlendShapeFrameCount(s) != 1)
                        multiFrame = (multiFrame == null ? "" : multiFrame + ", ") + shared.GetBlendShapeName(s);
                    _smr.SetBlendShapeWeight(s, 100f);
                    ok = BakeUniqueVerts(tmp, inv, ref why);
                    _smr.SetBlendShapeWeight(s, 0f);
                    if (!ok) break;
                    float mx = 0f;
                    for (int k = 0; k < n; k++)
                    {
                        tmp[k] -= rest[k];
                        float m = Mathf.Abs(tmp[k].x) + Mathf.Abs(tmp[k].y) + Mathf.Abs(tmp[k].z);
                        if (m > mx) mx = m;
                    }
                    if (mx > 1e-6f) { delta[s] = (Vector3[])tmp.Clone(); kept++; }   // shapes not touching the cum stay null (skipped in eval)
                }
            }
            for (int s = 0; s < shapeCount; s++) _smr.SetBlendShapeWeight(s, savedW[s]);
            if (!ok)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning(
                    $"[fill] basis capture FAILED on '{name}' ({why}) — no measurement this frame; retrying on the next change.");
                return false;
            }
            _basisRest = rest; _basisDelta = delta; _basisScale0 = s0;
            // Rigidity snapshot: every skin bone's LOCAL transform. The per-frame assertion
            // compares against this — bone locals are bit-static on a healthy womb rig.
            var bones = _smr.bones;
            _rigLocPos = new Vector3[bones.Length];
            _rigLocRot = new Quaternion[bones.Length];
            _rigLocScl = new Vector3[bones.Length];
            for (int b = 0; b < bones.Length; b++)
            {
                if (bones[b] == null) continue;
                _rigLocPos[b] = bones[b].localPosition;
                _rigLocRot[b] = bones[b].localRotation;
                _rigLocScl[b] = bones[b].localScale;
            }
            _basisCaptured = true;
            // ── ONE-TIME SPAWN ASSERTION: basis vs a direct BakeMesh at the restored weights. ──
            EvaluateBasis();
            var probe = new Vector3[n];
            if (BakeUniqueVerts(probe, inv, ref why))
            {
                float maxErr2 = 0f;
                for (int k = 0; k < n; k++)
                {
                    float e2 = (probe[k] - _lpCache[k]).sqrMagnitude;
                    if (e2 > maxErr2) maxErr2 = e2;
                }
                float worldErr = Mathf.Sqrt(maxErr2) * Mathf.Max(Mathf.Abs(s0.x), Mathf.Max(Mathf.Abs(s0.y), Mathf.Abs(s0.z)));
                if (worldErr > 2e-4f)
                    LiquidWobbleMPBPlugin._logger?.LogWarning(
                        $"[fill] SPAWN ASSERTION FAILED on '{name}': precomputed basis disagrees with BakeMesh by {worldErr:E3} m " +
                        $"(rig/blendshapes violate the linear-rig assumption). CONTINUING on the basis path — extents/profile WILL drift visibly.");
                else
                    LiquidWobbleMPBPlugin._logger?.LogInfo(
                        $"[fill] precomputed skinned basis VERIFIED on '{name}': {n} cum verts, {kept}/{shapeCount} shapes affect them, " +
                        $"max |basis − BakeMesh| = {worldErr:E3} m. BakeMesh is now SPAWN-ONLY (build {PluginBuild}).");
            }
            if (multiFrame != null)
                LiquidWobbleMPBPlugin._logger?.LogWarning(
                    $"[fill] MULTI-FRAME blendshape(s) on '{name}': {multiFrame} — the linear basis is exact only at weights 0/100 " +
                    $"for these; intermediate weights will drift. (KK womb shapes are single-frame; a multi-frame one is a build change.)");
            return true;
        }

        // Bake the CURRENT SMR state and extract the unique cum verts, scale-normalized.
        // why is assigned (not just out) so a caller can accumulate across calls.
        private bool BakeUniqueVerts(Vector3[] dst, Vector3 invScale, ref string why)
        {
            _smr.BakeMesh(_bakedMesh);
            _bakedMesh.GetVertices(_bakedVList);
            if (_bakedVList.Count < _restVerts.Length)
            {
                why = $"BakeMesh returned {_bakedVList.Count}/{_restVerts.Length} verts";
                return false;
            }
            for (int k = 0; k < _cumVertsUnique.Length; k++)
            {
                Vector3 v = _bakedVList[_cumVertsUnique[k]];
                dst[k] = new Vector3(v.x * invScale.x, v.y * invScale.y, v.z * invScale.z);
            }
            return true;
        }

        // Per-WEIGHTS-CHANGE evaluation: _lpCache[k] = rest + Σ wₛ·deltaₛ (normalized units).
        // Cost: one array copy + one fused multiply-add pass per ACTIVE shape over ~1.1k verts.
        // Scale is applied later in the walk (so a pure rescale never reads a stale cache).
        private void EvaluateBasis()
        {
            int n = _cumVertsUnique.Length;
            if (_lpCache == null || _lpCache.Length != n) _lpCache = new Vector3[n];
            System.Array.Copy(_basisRest, _lpCache, n);
            // Read weights from the dirty-check sentinel captured moments earlier this same call in
            // UpdateChamberExtents (line ~913), and equal to the restored weights at the spawn-assertion
            // call site -- identical values to GetBlendShapeWeight(s), but skips the per-shape native
            // interop every weights-change frame. Fall back to the live getter if the sentinel isn't ready.
            bool useSentinel = _sentinelShapes != null && _sentinelShapes.Length == _basisDelta.Length;
            for (int s = 0; s < _basisDelta.Length; s++)
            {
                var d = _basisDelta[s];
                if (d == null) continue;
                float w = (useSentinel ? _sentinelShapes[s] : _smr.GetBlendShapeWeight(s)) * 0.01f;   // KK weight is 0..100
                if (w == 0f) continue;
                for (int k = 0; k < n; k++)
                {
                    _lpCache[k].x += d[k].x * w;
                    _lpCache[k].y += d[k].y * w;
                    _lpCache[k].z += d[k].z * w;
                }
            }
        }

        // PER-FRAME RIGIDITY ASSERTION — the precompute's load-bearing assumption, checked on
        // every dirty frame: every skin bone's LOCAL transform must still match the spawn
        // snapshot (whole-item motion lives in the ANCESTORS and never shows here). NO fallback
        // on violation: warn loudly (throttled to 1/5s) and keep measuring on the single path —
        // the visible drift is the diagnostic.
        private void AssertRigRigid(Transform[] bones)
        {
            if (_rigLocPos == null || bones.Length != _rigLocPos.Length) return;
            for (int b = 0; b < bones.Length; b++)
            {
                var t = bones[b];
                if (t == null) continue;
                Vector3 dp = t.localPosition - _rigLocPos[b];
                Vector3 ds = t.localScale - _rigLocScl[b];
                Quaternion q = t.localRotation; Quaternion q0 = _rigLocRot[b];
                float qd = Mathf.Abs(q.x - q0.x) + Mathf.Abs(q.y - q0.y) + Mathf.Abs(q.z - q0.z) + Mathf.Abs(q.w - q0.w);
                if (dp.sqrMagnitude > 1e-10f || ds.sqrMagnitude > 1e-10f || qd > 1e-5f)
                {
                    if (Time.unscaledTime - _rigWarnClock > 5f)
                    {
                        _rigWarnClock = Time.unscaledTime;
                        LiquidWobbleMPBPlugin._logger?.LogWarning(
                            $"[fill] RIGIDITY VIOLATED on '{name}': bone '{t.name}' moved LOCALLY (Δpos {dp.magnitude:E2}, Δrot {qd:E2}). " +
                            $"The precomputed basis (build {PluginBuild}) assumes a rigid womb rig — extents/box/profile will drift visibly. Respawn the womb after fixing whatever animates its internal bones.");
                    }
                    return;
                }
            }
        }

        // Measure each chamber's world-space vertical extent from the LIVE skinned+blendshaped cum
        // (submesh 0). Feeds _Chamber1ExtentY / _Chamber2ExtentY = (minWorldY, maxWorldY, 0, 1).
        // This is what lets the shader's fill plane reach the REAL bulb top: the rest box misses
        // blendshape inflation, per-chamber scale and pose, but measuring the geometry doesn't.
        // Called every frame (ExtentPeriod=0) but DIRTY-GATED: work runs only when a bone matrix
        // or blendshape weight actually changed since the last measurement — a static scene costs
        // only the cheap compares; animation tracks live. Build 333: the dirty work is the
        // precomputed-basis evaluation (weights changed) + the ~1.1k-vert walk — per-frame
        // BakeMesh and the manual-skinning fallback are GONE (single path, loud assertions).
        private void UpdateChamberExtents()
        {
            if (_smr == null || _smr.sharedMesh == null || _liquidMat == null || _block == null)
                return;
            var shared = _smr.sharedMesh;
            if (shared.subMeshCount < 1)
                return;
            var bones = _smr.bones;
            if (bones == null || bones.Length == 0)
                return;
            if (_cumVerts == null)
            {
                _cumVerts    = shared.GetTriangles(0);   // submesh 0 = cum (duplicate indices fine for min/max)
                _restVerts   = shared.vertices;          // base (rest) positions, object space
            }
            if (_restVerts == null)
                return;
            // Deduplicate the submesh-0 triangle index list to DISTINCT vertices. GetTriangles(0)
            // lists each vertex once PER triangle (~6x), and this loop runs every frame, so walking
            // the ~1.1k unique verts instead of ~6.5k indices is ~6x cheaper for the same min/max.
            // Built once (a bool marker avoids a HashSet dependency); _restVerts is valid past the guard.
            if (_cumVertsUnique == null)
            {
                bool[] seen = new bool[_restVerts.Length];
                int uniqueCount = 0;
                for (int t = 0; t < _cumVerts.Length; t++)
                {
                    int vi = _cumVerts[t];
                    if (vi >= 0 && vi < seen.Length && !seen[vi]) { seen[vi] = true; uniqueCount++; }
                }
                _cumVertsUnique = new int[uniqueCount];
                int u = 0;
                for (int vi = 0; vi < seen.Length; vi++)
                    if (seen[vi]) _cumVertsUnique[u++] = vi;
            }

            // DIRTY CHECK (event-style gating), split: a blendshape-weight change needs a basis
            // re-evaluation; a bone-matrix change (whole-item motion/rescale) only needs the walk
            // below to re-transform the cached verts. Exact compares (untouched transforms are
            // bit-identical). A static scene costs only the compares; on skip the last pushed
            // values persist in the MPB.
            int shapeCount = shared.blendShapeCount;
            bool bonesDirty   = _sentinelBones == null || _sentinelBones.Length != bones.Length;
            bool weightsDirty = _sentinelShapes == null || _sentinelShapes.Length != shapeCount;
            if (!bonesDirty)
                for (int b = 0; b < bones.Length; b++)
                {
                    Matrix4x4 cur = bones[b] != null ? bones[b].localToWorldMatrix : Matrix4x4.identity;
                    if (!MatEq(ref cur, ref _sentinelBones[b])) { bonesDirty = true; break; }
                }
            if (!weightsDirty)
                for (int s = 0; s < shapeCount; s++)
                    if (_smr.GetBlendShapeWeight(s) != _sentinelShapes[s]) { weightsDirty = true; break; }
            if (!bonesDirty && !weightsDirty)
                return;
            if (_sentinelBones == null || _sentinelBones.Length != bones.Length) _sentinelBones = new Matrix4x4[bones.Length];
            if (_sentinelShapes == null || _sentinelShapes.Length != shapeCount) _sentinelShapes = new float[shapeCount];
            for (int b = 0; b < bones.Length; b++)
                _sentinelBones[b] = bones[b] != null ? bones[b].localToWorldMatrix : Matrix4x4.identity;
            for (int s = 0; s < shapeCount; s++)
                _sentinelShapes[s] = _smr.GetBlendShapeWeight(s);

            // Unity (<2020) BakeMesh baked the renderer's lossyScale INTO the verts; the basis
            // keeps that convention (capture normalized ÷ scale, walk re-multiplies), so world
            // recovery stays the UNIT-scale frame (position+rotation only) — localToWorldMatrix
            // would apply the scale twice.
            Transform rt = _renderer != null ? _renderer.transform : null;
            Matrix4x4 bakeM = rt != null ? Matrix4x4.TRS(rt.position, rt.rotation, Vector3.one) : Matrix4x4.identity;
            // ── STAGE-2: capture once at spawn, assert rigidity, evaluate on weight changes. ──
            if (!_basisCaptured && !CaptureSkinnedBasis(shared, rt))
                return;                                   // degenerate transient (logged) — retried next dirty frame
            if (LiquidWobbleMPBPlugin.CfgDebugLog) AssertRigRigid(bones);  // dev sanity check (warn-only) -> Debug Log gated, so release does no per-frame bone scan and never spams the warning
            if (weightsDirty || _lpCache == null)
                EvaluateBasis();
            Vector3 sNow = rt != null ? rt.lossyScale : Vector3.one;
            if (!_scaleWarned)
            {
                // Non-uniform RELATIVE rescale of a rotated rig is not component-wise — the model
                // is then approximate (uniform rescale, the normal case, is exact). Warn once.
                float rx = sNow.x / _basisScale0.x, ry = sNow.y / _basisScale0.y, rz = sNow.z / _basisScale0.z;
                if (Mathf.Abs(rx - ry) + Mathf.Abs(rx - rz) > 1e-3f * Mathf.Abs(rx))
                {
                    _scaleWarned = true;
                    LiquidWobbleMPBPlugin._logger?.LogWarning(
                        $"[fill] NON-UNIFORM rescale on '{name}' since basis capture ({_basisScale0} -> {sNow}): " +
                        $"the precomputed basis scales component-wise, so the measurement is approximate until respawn. Rescale uniformly or respawn the womb.");
                }
            }
            float divider = _liquidMat.GetFloat("_Bound1MinY_bottom");
            // Per chamber: WORLD-Y min/max (fill plane) + RENDERER-LOCAL AABB (oriented bounds box —
            // drawn through the unit-scale frame so it hugs the mesh AND rotates with the item).
            float wy1mn = float.MaxValue, wy1mx = float.MinValue, wy2mn = float.MaxValue, wy2mx = float.MinValue;
            Vector3 mn1 = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 mx1 = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Vector3 mn2 = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 mx2 = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            // GPU-vs-plugin probe vertex: tri0[0] = the GS's input[0] of primID 0 (first index of
            // submesh 0). Captured below and pushed as _DbgVert0World for the shader's marker pair.
            int v0 = (_cumVerts != null && _cumVerts.Length > 0) ? _cumVerts[0] : -1;
            // Chamber-1 verts are captured for the contact profile (built after the
            // loop, once the extents are known — the profile rows are extent-relative).
            if (_profWp == null || _profWp.Length < _cumVertsUnique.Length)
                _profWp = new Vector3[_cumVertsUnique.Length];
            _profCount = 0;
            for (int k = 0; k < _cumVertsUnique.Length; k++)
            {
                int i = _cumVertsUnique[k];
                if (i < 0 || i >= _restVerts.Length)
                    continue;
                // Basis-evaluated vert (normalized) × current scale = the BakeMesh-convention
                // renderer-local vert; unit-scale frame -> world == the GPU render.
                Vector3 lp = _lpCache[k];
                lp.x *= sNow.x; lp.y *= sNow.y; lp.z *= sNow.z;
                Vector3 wp = bakeM.MultiplyPoint3x4(lp);
                if (i == v0) _dbgV0W = wp;   // probe vertex world pos (same source as the box)
                if (_restVerts[i].y < divider - ChamberSplitBias)
                {
                    if (wp.y < wy2mn) wy2mn = wp.y; if (wp.y > wy2mx) wy2mx = wp.y;
                    if (lp.x < mn2.x) mn2.x = lp.x; if (lp.y < mn2.y) mn2.y = lp.y; if (lp.z < mn2.z) mn2.z = lp.z;
                    if (lp.x > mx2.x) mx2.x = lp.x; if (lp.y > mx2.y) mx2.y = lp.y; if (lp.z > mx2.z) mx2.z = lp.z;
                }
                else
                {
                    _profWp[_profCount++] = wp;   // chamber-1 sample for the contact profile
                    if (wp.y < wy1mn) wy1mn = wp.y; if (wp.y > wy1mx) wy1mx = wp.y;
                    if (lp.x < mn1.x) mn1.x = lp.x; if (lp.y < mn1.y) mn1.y = lp.y; if (lp.z < mn1.z) mn1.z = lp.z;
                    if (lp.x > mx1.x) mx1.x = lp.x; if (lp.y > mx1.y) mx1.y = lp.y; if (lp.z > mx1.z) mx1.z = lp.z;
                }
            }
            if (v0 >= 0)
                _block.SetVector("_DbgVert0World", new Vector4(_dbgV0W.x, _dbgV0W.y, _dbgV0W.z, 1f));
            // Oriented-box frame: renderer position + rotation columns (unit scale — the local AABB
            // already carries the baked scale). The box rides item rotation again, like the old overlay.
            Vector3 fX = bakeM.GetColumn(0), fY = bakeM.GetColumn(1), fZ = bakeM.GetColumn(2), fP = bakeM.GetColumn(3);
            _block.SetVector("_BoxFrameX",   new Vector4(fX.x, fX.y, fX.z, 0f));
            _block.SetVector("_BoxFrameY",   new Vector4(fY.x, fY.y, fY.z, 0f));
            _block.SetVector("_BoxFrameZ",   new Vector4(fZ.x, fZ.y, fZ.z, 0f));
            _block.SetVector("_BoxFramePos", new Vector4(fP.x, fP.y, fP.z, 1f));
            if (wy1mx > wy1mn)
            {
                _dbgExt1 = new Vector4(wy1mn, wy1mx, 0f, 1f); _block.SetVector("_Chamber1ExtentY", _dbgExt1);
                _block.SetVector("_Box1LocalMin", new Vector4(mn1.x, mn1.y, mn1.z, 1f));
                _block.SetVector("_Box1LocalMax", new Vector4(mx1.x, mx1.y, mx1.z, 1f));
                _dbgBakeC1 = bakeM.MultiplyPoint3x4((mn1 + mx1) * 0.5f);   // measured chamber-1 center (world) for the [fill] log
                BuildCapProfile(wy1mn, wy1mx);   // v361 rim clamp (validity coupled to the extents; pushed every frame)
            }
            if (wy2mx > wy2mn)
            {
                _dbgExt2 = new Vector4(wy2mn, wy2mx, 0f, 1f); _block.SetVector("_Chamber2ExtentY", _dbgExt2);
                _block.SetVector("_Box2LocalMin", new Vector4(mn2.x, mn2.y, mn2.z, 1f));
                _block.SetVector("_Box2LocalMax", new Vector4(mx2.x, mx2.y, mx2.z, 1f));
            }
        }

        // Build the chamber-1 wall cross-section from this measurement's world verts: ProfH world-Y
        // rows over [yMin..yMax], each an XZ centroid (_CapProfC) + ProfA angular max radii
        // (_CapProfR). Deliberately OUTWARD-biased (per-cell max, 4-cell max-splat, hole fill) so
        // the clamp never pulls the cap inside the wall and is a no-op on raw wall verts (sampled
        // R >= each vert's radius -> no rim seam at the cut). Fills the buffers only; the MPB push
        // is unconditional in FeedFrame. See DESIGN_NOTES.md.
        private void BuildCapProfile(float yMin, float yMax)
        {
            float range = Mathf.Max(yMax - yMin, 1e-6f);
            for (int h = 0; h < ProfH; h++) { _profSumX[h] = 0f; _profSumZ[h] = 0f; _profCnt[h] = 0; }
            // 1) world-Y row binning + per-row XZ centroid accumulation
            for (int i = 0; i < _profCount; i++)
            {
                Vector3 wp = _profWp[i];
                int h = (int)((wp.y - yMin) / range * ProfH);
                if (h >= ProfH) h = ProfH - 1; else if (h < 0) h = 0;
                _profSumX[h] += wp.x; _profSumZ[h] += wp.z; _profCnt[h]++;
            }
            for (int h = 0; h < ProfH; h++)
            {
                _profRowAny[h] = _profCnt[h] > 0;
                if (_profRowAny[h])
                    _profC[h] = new Vector4(_profSumX[h] / _profCnt[h], _profSumZ[h] / _profCnt[h], 0f, 1f);
            }
            // 2) rows with no verts (sparse slab) borrow the nearest filled row's centroid
            for (int h = 0; h < ProfH; h++)
            {
                if (_profRowAny[h]) continue;
                int src = -1;
                for (int d = 1; d < ProfH && src < 0; d++)
                {
                    if (h - d >= 0 && _profRowAny[h - d]) src = h - d;
                    else if (h + d < ProfH && _profRowAny[h + d]) src = h + d;
                }
                _profC[h] = src >= 0 ? _profC[src] : Vector4.zero;   // src==-1 impossible (caller guards >=1 ch-1 vert)
            }
            // 3) per-cell MAX radii, max-SPLAT into the cells the shader's bilinear
            //    sample reads at this vert's own (angle, height)
            for (int k = 0; k < ProfH * ProfA; k++) _profR[k] = 0f;
            for (int i = 0; i < _profCount; i++)
            {
                Vector3 wp = _profWp[i];
                float fh = (wp.y - yMin) / range * ProfH - 0.5f;
                if (fh < 0f) fh = 0f; else if (fh > ProfH - 1f) fh = ProfH - 1f;
                int h0 = (int)fh;
                int h1 = h0 + 1 < ProfH ? h0 + 1 : ProfH - 1;
                SplatProfile(h0, wp);
                if (h1 != h0) SplatProfile(h1, wp);
            }
            // 4) rows still all-zero after the splat copy the nearest non-empty row's cells
            for (int h = 0; h < ProfH; h++)
            {
                int b = h * ProfA; bool any = false;
                for (int a = 0; a < ProfA; a++) if (_profR[b + a] > 0f) { any = true; break; }
                _profRowAny[h] = any;   // reused: now = post-splat radius-row occupancy
            }
            for (int h = 0; h < ProfH; h++)
            {
                if (_profRowAny[h]) continue;
                int src = -1;
                for (int d = 1; d < ProfH && src < 0; d++)
                {
                    if (h - d >= 0 && _profRowAny[h - d]) src = h - d;
                    else if (h + d < ProfH && _profRowAny[h + d]) src = h + d;
                }
                if (src >= 0) System.Array.Copy(_profR, src * ProfA, _profR, h * ProfA, ProfA);
            }
            // 5) angular hole fill per row: empty cells take the MAX of their wrap
            //    neighbours, iterated until closed (<= ProfA passes). Scratch copy
            //    keeps each pass order-free.
            for (int h = 0; h < ProfH; h++)
            {
                int b = h * ProfA;
                for (int pass = 0; pass < ProfA; pass++)
                {
                    bool holes = false, changed = false;
                    System.Array.Copy(_profR, b, _profRScratch, b, ProfA);
                    for (int a = 0; a < ProfA; a++)
                    {
                        if (_profRScratch[b + a] > 0f) continue;
                        holes = true;
                        float nb = Mathf.Max(_profRScratch[b + ((a + ProfA - 1) % ProfA)],
                                             _profRScratch[b + ((a + 1) % ProfA)]);
                        if (nb > 0f) { _profR[b + a] = nb; changed = true; }
                    }
                    if (!holes || !changed) break;
                }
            }
            _profValid = true;
        }

        // Max-splat one vert's radius (about the row's centroid) into the 2 angle
        // cells around its bearing in that row — angle convention matches the shader
        // EXACTLY: a01 = atan2(z,x)/2pi + 0.5, texel-centre, wrap.
        private void SplatProfile(int row, Vector3 wp)
        {
            float dx = wp.x - _profC[row].x, dz = wp.z - _profC[row].y;
            float r  = Mathf.Sqrt(dx * dx + dz * dz);
            float fa = (Mathf.Atan2(dz, dx) * 0.15915494f + 0.5f) * ProfA - 0.5f;
            int a0 = Mathf.FloorToInt(fa);
            int aA = ((a0 % ProfA) + ProfA) % ProfA;
            int aB = (aA + 1) % ProfA;
            int iA = row * ProfA + aA, iB = row * ProfA + aB;
            if (r > _profR[iA]) _profR[iA] = r;
            if (r > _profR[iB]) _profR[iB] = r;
        }

        // Exact element-wise matrix compare for the dirty-check (Vector4/Matrix operators are
        // approximate or version-dependent; untouched transforms are bit-identical, so exact is right).
        private static bool MatEq(ref Matrix4x4 a, ref Matrix4x4 b)
        {
            return a.m00 == b.m00 && a.m01 == b.m01 && a.m02 == b.m02 && a.m03 == b.m03 &&
                   a.m10 == b.m10 && a.m11 == b.m11 && a.m12 == b.m12 && a.m13 == b.m13 &&
                   a.m20 == b.m20 && a.m21 == b.m21 && a.m22 == b.m22 && a.m23 == b.m23 &&
                   a.m30 == b.m30 && a.m31 == b.m31 && a.m32 == b.m32 && a.m33 == b.m33;
        }

        private float ClampImpulse(float raw)
        {
            return Mathf.Clamp(raw * MaxWobble, -MaxWobble, MaxWobble);
        }
    }
}
