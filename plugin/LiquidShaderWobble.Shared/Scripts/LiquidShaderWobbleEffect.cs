using UnityEngine;
using System.Collections.Generic;

namespace LiquidWobbleMPB
{
    /// Drives RSkoi/LiquidShader-style liquid materials (e.g. CloXray/Liquid) from the host object's own
    /// motion and world scale, all through a single renderer-wide MaterialPropertyBlock written once per <c>LateUpdate</c>.
    public class LiquidWobbleMPBEffect : MonoBehaviour
    {
        // Wobble tunables (surfaced and edited through ComponentUtil).
        public string ShaderRotXPropName { get; set; } = "_RotationX";
        public string ShaderRotZPropName { get; set; } = "_RotationZ";
        public float MaxWobble { get; set; } = 0.03f;
        public float WobbleSpeed { get; set; } = 1f;
        public float Recovery { get; set; } = 1f;

        // Thrust slosh: the BetterPenetration thrust speed (d depth/dt) jolts the wobble so the cum sloshes
        // with each thrust.
        public float ThrustSlosh { get; set; } = 0f;
        public float ThrustSloshGain { get; set; } = 2f;

        // The wobble SETUP tunables above (MaxWobble/WobbleSpeed/Recovery/ThrustSlosh/ThrustSloshGain) are
        // ALSO surfaced as material properties so they can be tuned in Material Editor.
        public float SetupReadPeriod { get; set; } = 0.5f;

        // Scale-feed tunables.
        public string ShaderScalePropName { get; set; } = "_ObjectScaleVec";
        // written = source.lossyScale * ScaleMultiplier. allows calibrate to rigs that bake in a non-1 base
        // scale without recompiling.
        public float ScaleMultiplier { get; set; } = 1f;

        // World-up expressed in the rest (mesh-local) frame, so the shader can keep the fill plane
        // horizontal to gravity while doing its fill math in rest space.
        public string ShaderRestWorldUpPropName { get; set; } = "_RestWorldUp";

        private const float CrossAxisFactor = 0.2f;
        // Below this the scale is treated as degenerate (object disabled, mid-load, re-parenting).
        private const float MinValidScale = 1e-4f;

        private Renderer _renderer;
        private SkinnedMeshRenderer _smr;
        private Transform[] _bonesCache;
        private Matrix4x4[] _bindposesCache;
        private MaterialPropertyBlock _block;
        private Material _liquidMat;   // the cum material (has the fill bounds), for chamber centres.
        private int _tubeBoneIdx = -1;   // index in _smr.bones of a canal-axis bone (tube centre anchor).
        // Bone the tube centre is anchored to for the skinning map (on the canal axis, tube base).
        public string TubeCenterBone { get; set; } = "cf_j_kokan";
        private int _wombBoneIdx = -1;   // index in _smr.bones of the womb (uterus) bone (womb centre anchor).
        // Bone the womb centre is anchored to. The womb bulb rides a DIFFERENT bone than the tube, so it
        // needs its own world anchor (extrapolating from the tube across the neck undershoots ~40%).
        public string WombCenterBone { get; set; } = "cf_s_waist02";

        // LIVE per-chamber world-Y extent, measured from the actual skinned+blendshaped cum verts.
        private int[] _cumVerts;   // vertex indices of submesh 0 (the cum), triangle list (6x duplicated).
        private int[] _cumVertsUnique;   // DISTINCT submesh-0 vertex indices - what the per-frame extent loop walks.
        private Vector3[] _restVerts;   // base (rest) vertex positions, object space (cached).
        private float _extentClock = 999f;   // high => measure on the first LateUpdate.
        private bool _wombBoneWarned;   // warn once if WombCenterBone isn't found (silent ~40% undershoot otherwise).
        private bool        _basisCaptured;
        private Vector3[]   _basisRest;   // [k over _cumVertsUnique] baked-frame vert, all weights 0, ÷ _basisScale0.
        private Vector3[][] _basisDelta;   // [shape][k] baked delta at weight 100, ÷ _basisScale0; null = no cum effect.
        private Vector3     _basisScale0;   // renderer lossyScale at capture (basis stored normalized by it).
        private Vector3[]   _lpCache;   // [k] last evaluated NORMALIZED baked-frame verts (current weights).
        private Vector3[]   _rigLocPos;   // per-bone LOCAL pos/rot/scale at capture - the rigidity snapshot.
        private Quaternion[] _rigLocRot;
        private Vector3[]   _rigLocScl;
        private float       _rigWarnClock;   // throttle: rigidity warning at most every 5s.
        private bool        _scaleWarned;   // one-time: non-uniform RELATIVE rescale (component model approximate).
        // Half the 3mm CUM_CHAMBER_GAP (build CUM_CHAMBER_GAP): the rest-Y midpoint of the gap between.
        private const float ChamberSplitBias = 0.0015f;
        // DIAGNOSTIC build stamp - bump every plugin build so the log proves which DLL is live.
        public const int PluginBuild = 860;   // build stamp - bump every build.
        // 0 = measure the chamber extents every frame (dirty-gated).
        public float ExtentPeriod { get; set; } = 0f;

        // ── Cap contact profile (chamber 1/womb) - shader rim-clamp feed ── The cum-wall cross-section,
        // measured in the same walk as the extents.
        private const int ProfH = 16, ProfA = 16;
        private Vector3[] _profWp;   // scratch: this measurement's chamber-1 world verts.
        private int       _profCount;
        private bool      _profValid;   // true once a measurement has filled the buffers.
        private readonly float[]   _profSumX = new float[ProfH];
        private readonly float[]   _profSumZ = new float[ProfH];
        private readonly int[]     _profCnt  = new int[ProfH];
        private readonly bool[]    _profRowAny = new bool[ProfH];
        private readonly Vector4[] _profC    = new Vector4[ProfH];
        private readonly float[]   _profR    = new float[ProfH * ProfA];
        private readonly float[]   _profRScratch = new float[ProfH * ProfA];

        private Vector3 _prevPosition;
        private Vector3 _prevEuler;
        private float _prevDepth;   // last BP penetration depth - thrust-slosh velocity source.
        private bool  _hasPrevDepth;   // false until the first valid BP read (no spurious first-frame jolt).
        private float _setupClock = 999f;   // high => read the Material-Editor setup floats on the first frame.
        private bool  _setupMatWarned;   // warn once if no live material exposes the setup props.

        // Accumulated sway amplitude per axis; bleeds back to zero over time.
        private float _amplitudeX;
        private float _amplitudeZ;
        // Chamber-2 (tube) sway amplitude - used only in per-chamber slosh mode (ThrustSlosh=2).
        private float _amplitudeX2;
        private float _amplitudeZ2;
        // Last world chamber centres (captured when _Box1/2CenterWorld are written) for the thrust proximity
        // gate; the flags say whether this frame produced a valid centre.
        private Vector3 _wombCenterW, _tubeCenterW;
        private float _fillLogClock;   // debug: throttle for the cum-fill input log.
        private Vector4 _dbgExt1, _dbgExt2;   // debug: captured chamber world-Y extents.
        private bool _extAnchorCaptured;
        private Transform _extAnchor;
        private Matrix4x4 _extAnchor0Inv, _extBakeM0;
        private Vector3 _extScale0 = Vector3.one;
        private Vector3 _dbgSkinScale;   // scale from the exact skin matrix (parented-womb-safe source).
        private bool _haveWombC, _haveTubeC;
        private Mesh _bakedMesh;   // BakeMesh target - SPAWN-only since build 333 (basis capture + one-time assertion).
        private System.Collections.Generic.List<Vector3> _bakedVList;   // reused GetVertices buffer (spawn-only).
        private Matrix4x4[] _sentinelBones;   // bone l2w at last measurement (dirty-check).
        private float[] _sentinelShapes;   // blendshape weights at last measurement (dirty-check).
        private Vector3 _dbgBakeC1;   // debug: measured chamber-1 center (world).
        private Vector3 _dbgV0W;   // plugin world pos of cum vertex tri0[0] (GPU-vs-plugin crosses).

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
            _bonesCache = null; _bindposesCache = null;
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

            if (_smr != null) LiquidWobbleMPBPlugin.EnsureWombExpand(transform);   // skinned womb only - a bottle (plain MeshRenderer) that got this via the hotkey is not a womb.

            // Strip leftover DynamicBone COLLIDERS from the womb skeleton.
            int strippedColliders = 0;
            if (_smr != null)   // womb only: never strip a bottle's DynamicBoneCollider (it may be the womb's reaction collider).
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

            // NEW-SPAWN DEFAULT: a womb without BodyReveal shows only the shell.
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

            // Build stamp on load - proves which DLL is live the instant a womb spawns.
            float sv = (_liquidMat != null && _liquidMat.HasProperty("_ShaderVersion"))
                       ? _liquidMat.GetFloat("_ShaderVersion") : -1f;
            LiquidWobbleMPBPlugin._logger?.LogInfo(
                $"[BUILD] LiquidWobbleMPB pluginBuild={PluginBuild}  shaderVersion={sv:F0}  on '{name}'");
        }

        // ── Frame-feed timing ── The pose-dependent feed must sample the bones after IK has written the
        // final pose.
        private int _lastFeedFrame = -1;
        private Camera.CameraCallback _preCull;

        private void OnEnable()
        {
            _preCull = OnPreCullCam;
            Camera.onPreCull += _preCull;
        }

        // material's 6 box planes).
        private float _bndLogNext; private string _bndLast;
        private void LogAuthoredBounds()
        {
            if (!LiquidWobbleMPBPlugin.Configured || !LiquidWobbleMPBPlugin.CfgDebugLog || _liquidMat == null) return;
            if (Time.unscaledTime < _bndLogNext) return;
            _bndLogNext = Time.unscaledTime + 1f;
            try
            {
                string s = "CUM-BOUNDS '" + name + "': L=" + _liquidMat.GetFloat("_Bound3MinX_left").ToString("F4")
                    + " R=" + _liquidMat.GetFloat("_Bound4MaxX_right").ToString("F4")
                    + " Bottom=" + _liquidMat.GetFloat("_Bound1MinY_bottom").ToString("F4")
                    + " Top=" + _liquidMat.GetFloat("_Bound2MaxY_top").ToString("F4")
                    + " Back=" + _liquidMat.GetFloat("_Bound5MinZ_back").ToString("F4")
                    + " Front=" + _liquidMat.GetFloat("_Bound6MaxZ_front").ToString("F4");
                if (s == _bndLast) return;
                _bndLast = s;
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: " + s + "   <- authored bounds; the LAST such line is the current tuning");
            }
            catch (System.Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: bounds logging failed: " + e.Message); }
        }

        private void RelinkLiquidMat()
        {
            if (_renderer == null) return;
            var mats = _renderer.sharedMaterials;   // allocates - called on the setup throttle only.
            if (mats == null) return;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m != null && m.HasProperty("_Bound1MinY_bottom")) { _liquidMat = m; return; }
            }
            // renderer currently offers no cum material (mid-teardown/reload).
        }

        private void OnDisable()
        {
            if (_preCull != null) Camera.onPreCull -= _preCull;
        }

        private void OnPreCullCam(Camera cam)
        {
            if (Time.frameCount == _lastFeedFrame) return;   // first camera of the frame only.
            _lastFeedFrame = Time.frameCount;
            FeedFrame();
        }

        private void FeedFrame()
        {
            if (_renderer == null || _block == null)
                return;
            if (!_renderer.isVisible)
                return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled)
                return;   // master toggle OFF -> freeze the liquid (the MPB keeps its last-written fill/wobble values).
            if (string.IsNullOrEmpty(ShaderRotXPropName) || string.IsNullOrEmpty(ShaderRotZPropName))
                return;

            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            _clock += dt;

            // Clear the chamber-centre freshness flags EACH frame; the centre block below re-sets them only
            // when it actually re-writes a centre this frame.
            _haveWombC = false;
            _haveTubeC = false;

            // Pull the wobble setup floats from the material (Material-Editor control surface), throttled.
            if (SetupReadPeriod > 0f)
            {
                _setupClock += dt;
                if (_setupClock >= SetupReadPeriod) { _setupClock = 0f; RelinkLiquidMat(); RefreshSetupFromMaterial(); }
            }

            // 1) Amplitude relaxes toward rest. Lerp(a, 0, dt*Recovery) == a*(1.
            float decay = 1f - Mathf.Clamp01(dt * Recovery);
            _amplitudeX *= decay;
            _amplitudeZ *= decay;
            _amplitudeX2 *= decay;   // chamber-2 (tube) amplitude decays the same way.
            _amplitudeZ2 *= decay;

            // 2) Oscillate the current amplitude.
            float omega = 2f * Mathf.PI * WobbleSpeed;
            float wave = Mathf.Sin(omega * _clock);

            // 3) One block, one write: rotations always, scale when valid.
            _renderer.GetPropertyBlock(_block);
            _block.SetFloat(ShaderRotXPropName, _amplitudeX * wave);
            _block.SetFloat(ShaderRotZPropName, _amplitudeZ * wave);
            // Per-chamber slosh (ThrustSlosh=2): tell the shader to use _Rotation2X/Z for the tube and push
            // the tube's own sway.
            bool perChamber = ThrustSlosh > 1.5f;
            _block.SetFloat("_PerChamberWobble", perChamber ? 1f : 0f);
            if (perChamber)
            {
                _block.SetFloat("_Rotation2X", _amplitudeX2 * wave);
                _block.SetFloat("_Rotation2Z", _amplitudeZ2 * wave);
            }

            // Scale and world-up share the same rest-source transform so they stay consistent (both
            // bone-based, or both transform-based).
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
                    // World-up in the rest frame. Direction-only (ignores scale/position), so no
                    // normalization needed; the shader normalizes.
                    Vector3 up = restSource.InverseTransformDirection(Vector3.up);
                    _block.SetVector(ShaderRestWorldUpPropName, new Vector4(up.x, up.y, up.z, 0f));
                }

                // Tube centre in WORLD via exact skinning: map the rest box centre through the bind pose ×
                // bone world matrix of a bone on the canal axis (cf_j_kokan).
                if (_liquidMat != null && _smr != null && _tubeBoneIdx >= 0)
                {
                    var bones = SkinBones();
                    var binds = SkinBindposes();
                    if (binds != null && bones != null && _tubeBoneIdx < bones.Length && _tubeBoneIdx < binds.Length && bones[_tubeBoneIdx] != null)
                    {
                        Matrix4x4 skin = bones[_tubeBoneIdx].localToWorldMatrix * binds[_tubeBoneIdx];
                        Vector3 w2 = skin.MultiplyPoint3x4(ChamberCenterRest(true));
                        _block.SetVector("_Box2CenterWorld", new Vector4(w2.x, w2.y, w2.z, 1f));
                        _tubeCenterW = w2; _haveTubeC = true;   // capture for the thrust proximity gate.
                    }
                }

                // Womb (chamber 1) centre in WORLD, anchored to its OWN bone.
                if (_liquidMat != null && _smr != null && SkinBones() != null && _smr.sharedMesh != null)
                {
                    var bones = SkinBones();
                    var binds = SkinBindposes();
                    if (_wombBoneIdx < 0 || _wombBoneIdx >= bones.Length ||
                        bones[_wombBoneIdx] == null || bones[_wombBoneIdx].name != WombCenterBone)
                    {
                        _wombBoneIdx = -1;
                        for (int i = 0; i < bones.Length; i++)
                            if (bones[i] != null && bones[i].name == WombCenterBone) { _wombBoneIdx = i; break; }
                    }
                    if (_wombBoneIdx < 0)
                    {
                        if (!_wombBoneWarned)   // warn once, not every frame.
                        {
                            LiquidWobbleMPBPlugin._logger?.LogWarning(
                                $"[bone] WombCenterBone '{WombCenterBone}' not found on '{name}' — womb anchor falls back to the box-centre estimate (~40% undershoot at fill=1). Set WombCenterBone via ComponentUtil.");
                            _wombBoneWarned = true;
                        }
                    }
                    else { _wombBoneWarned = false; }
                    if (_wombBoneIdx >= 0 && binds != null && _wombBoneIdx < binds.Length && bones[_wombBoneIdx] != null)
                    {
                        Matrix4x4 skinW = bones[_wombBoneIdx].localToWorldMatrix * binds[_wombBoneIdx];
                        Vector3 w1 = skinW.MultiplyPoint3x4(ChamberCenterRest(false));
                        _block.SetVector("_Box1CenterWorld", new Vector4(w1.x, w1.y, w1.z, 1f));
                        _wombCenterW = w1; _haveWombC = true;   // capture for the thrust proximity gate.

                        // Cum SCALE from the same exact skin matrix (column magnitudes = rest->world scale),
                        // not restSource.lossyScale.
                        _dbgSkinScale = new Vector3(
                            ((Vector3)skinW.GetColumn(0)).magnitude,
                            ((Vector3)skinW.GetColumn(1)).magnitude,
                            ((Vector3)skinW.GetColumn(2)).magnitude) * ScaleMultiplier;
                        if (!string.IsNullOrEmpty(ShaderScalePropName) && _dbgSkinScale.y > MinValidScale)
                            _block.SetVector(ShaderScalePropName, new Vector4(_dbgSkinScale.x, _dbgSkinScale.y, _dbgSkinScale.z, 1f));
                    }
                }

                // Live per-chamber world-Y extents (dirty-gated precomputed basis) so the fill plane reaches
                // the real bulb top, immune to blendshape inflation / per-chamber scale / pose.
                _extentClock += dt;
                if (_extentClock >= ExtentPeriod) { _extentClock = 0f; UpdateChamberExtents(); }
                LogAuthoredBounds();
                bool shapeOn = _shapeFed && _liquidMat != null
                    && _liquidMat.GetFloat("_VolumeConserve_0off_1cube_2ellipsoid") > 1.5f;
                _block.SetFloat("_VolumeShape", shapeOn ? 1f : 0f);
                if (shapeOn) SolveShapeFillLevels();

                // DEBUG (gated by WombExpand 'Debug Log'): print the cum-fill WORLD inputs so a fill desync
                // can be traced.
                if (LiquidWobbleMPBPlugin.Configured && LiquidWobbleMPBPlugin.CfgDebugLog)
                {
                    _fillLogClock += dt;
                    if (_fillLogClock >= 2f)
                    {
                        _fillLogClock = 0f;
                        Vector3 ls = restSource.lossyScale;
                        Vector3 up = restSource.InverseTransformDirection(Vector3.up);
                        Vector3 rls = _renderer != null ? _renderer.transform.lossyScale : Vector3.one;
                        string shapeDbg = _shapeFed
                            ? $" shape(f1={_liquidMat.GetFloat("_FillAmount"):F3} y1=[{_shY1mn:F3}..{_shY1mx:F3}] yF1={_block.GetFloat("_ShapeYfill1"):F3} yConn={_block.GetFloat("_ShapeYconn"):F3} on={_block.GetFloat("_VolumeShape"):F0})"
                            : " shape(unfed)";
                        LiquidWobbleMPBPlugin._logger?.LogInfo(
                            $"[fill] '{name}' v0W=({_dbgV0W.x:F3},{_dbgV0W.y:F3},{_dbgV0W.z:F3}) bakeC1=({_dbgBakeC1.x:F3},{_dbgBakeC1.y:F3},{_dbgBakeC1.z:F3}) rendLossy=({rls.x:F4},{rls.y:F4},{rls.z:F4}) " + shapeDbg +
                            $"skin=({_dbgSkinScale.x:F3},{_dbgSkinScale.y:F3},{_dbgSkinScale.z:F3}) lossy=({ls.x:F3},{ls.y:F3},{ls.z:F3}) up=({up.x:F2},{up.y:F2},{up.z:F2}) " +
                            $"wombC.y={_wombCenterW.y:F3} tubeC.y={_tubeCenterW.y:F3} ext1=[{_dbgExt1.x:F3}..{_dbgExt1.y:F3}] ext2=[{_dbgExt2.x:F3}..{_dbgExt2.y:F3}]");
                    }
                }
            }

            // ── Tip-detection DEBUG (gated by the F1 "Debug Log" switch.
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
                        _block.SetVector("_DebugTipPos", new Vector4(0f, 0f, 0f, 0f));   // w=0 -> overlay skips.
                    }
                }
            }

            // Contact profile: re-push the arrays every frame (~1.3 KB, negligible) rather than trust MPB
            // array persistence across the GetPropertyBlock round-trip (unverified in Unity 5.6; its failure mode would be silent).
            _block.SetVectorArray("_CapProfC", _profC);
            _block.SetFloatArray("_CapProfR", _profR);
            _block.SetVector("_CapProfInfo", new Vector4(ProfH, ProfA, 0f, _profValid ? 1f : 0f));

            _renderer.SetPropertyBlock(_block);

            // 4) Convert this frame's movement into a fresh impulse.
            Vector3 position = transform.position;
            Vector3 euler = transform.rotation.eulerAngles;

            // Express world velocity in the object's OWN frame.
            Transform wobFrame = ResolveRestSource();
            Vector3 linearW = (_prevPosition - position) / dt;
            Vector3 linear = (wobFrame != null) ? wobFrame.InverseTransformDirection(linearW) : linearW;
            Vector3 angular = euler - _prevEuler;

            // Body-motion impulse (the whole organ moving). Chamber 1 always; in per-chamber mode the tube
            // (chamber 2) gets the same body jolt.
            float bodyImpX = ClampImpulse(linear.x + angular.z * CrossAxisFactor);
            float bodyImpZ = ClampImpulse(linear.z + angular.x * CrossAxisFactor);
            _amplitudeX += bodyImpX;
            _amplitudeZ += bodyImpZ;
            if (ThrustSlosh > 1.5f) { _amplitudeX2 += bodyImpX; _amplitudeZ2 += bodyImpZ; }

            // Thrust slosh: BP penetration SPEED jolts the wobble.
            if (ThrustSlosh > 0.5f)
            {
                BPBridge.Reading tip;
                // Pair with the penis nearest THIS womb.
                Vector3 sloshAnchor = _haveWombC ? _wombCenterW : transform.position;
                if (BPBridge.TryReadNear(sloshAnchor, LiquidWobbleMPBPlugin.CfgPairRange, out tip) && tip.found)
                {
                    if (_hasPrevDepth)
                    {
                        float depthVel = (tip.depth - _prevDepth) / dt;   // >0 thrust IN, <0 pull OUT.
                        // Lean the jolt along the tip's horizontal axis. No pose -> a fixed diagonal so it
                        // still reads.
                        Vector3 tipDirL = (wobFrame != null && tip.hasPose) ? wobFrame.InverseTransformDirection(tip.tipDir) : tip.tipDir;
                        float dirX = tip.hasPose ? tipDirL.x : 0.7071f;
                        float dirZ = tip.hasPose ? tipDirL.z : 0.7071f;
                        float impX = depthVel * dirX * ThrustSloshGain;
                        float impZ = depthVel * dirZ * ThrustSloshGain;
                        if (ThrustSlosh > 1.5f && tip.hasPose && _haveWombC && _haveTubeC)
                        {
                            // PER-CHAMBER: weight the jolt by closeness to each chamber centre.
                            float d2w = (tip.tipPos - _wombCenterW).sqrMagnitude;
                            float d2t = (tip.tipPos - _tubeCenterW).sqrMagnitude;
                            float denom = d2w + d2t + 1e-8f;
                            float wombW = d2t / denom;   // near womb (d2w->0) => wombW->1.
                            float tubeW = d2w / denom;   // near tube (d2t->0) => tubeW->1.
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
                    _hasPrevDepth = false;   // disengaged / BP absent -> re-engage won't fire a huge first jolt.
                }
            }

            _prevPosition = position;
            _prevEuler = euler;
        }

        // Pull the wobble/slosh SETUP floats from the LIVE material so they can be tuned in Material Editor.
        private void RefreshSetupFromMaterial()
        {
            if (_renderer == null) return;
            var mats = _renderer.sharedMaterials;
            if (mats != null)
            {
                foreach (var m in mats)
                {
                    // Mode-suffixed name (shader 363+): the ME row label IS the property name, so the modes
                    // are spelled out in it (same convention as _ChamberMode_0single_1connected_2closed).
                    if (m == null || !m.HasProperty("_ThrustSlosh_0off_1global_2perChamber")) continue;
                    float maxW = m.GetFloat("_MaxWobble");
                    float spd  = m.GetFloat("_WobbleSpeed");
                    float rec  = m.GetFloat("_Recovery");
                    float ts   = m.GetFloat("_ThrustSlosh_0off_1global_2perChamber");
                    float tsg  = m.GetFloat("_ThrustSloshGain");
                    // Recovery is the decay rate; its valid range is [0.05,10].
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
            if (!_setupMatWarned)   // fail-loud: setup sliders inactive until a material exposes these props.
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning(
                    $"[setup] no live material on '{name}' declares the wobble setup props (_ThrustSlosh_0off_1global_2perChamber/_MaxWobble/...); " +
                    $"Material-Editor setup sliders are inactive — using the component's current values. Redeploy the shader if these are missing.");
                _setupMatWarned = true;
            }
        }

        // The component is usually attached straight to the liquid renderer, but the host bundle may bake it
        // onto a parent (e.g.
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

        // A CPU-skinned mesh is sized by its bones, so the rootBone defines the authoritative rest->world
        // map (scale and orientation); fall back to its own transform.
        private Transform ResolveRestSource()
        {
            if (_smr != null && _smr.rootBone != null)
                return _smr.rootBone;
            return transform;
        }

        // Shader property IDs for the chamber-bounds floats -- resolved once (Shader.PropertyToID) instead
        // of hashing six strings on every ChamberCenterRest call (called twice per FeedFrame, every frame).
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
            if (_liquidMat == null) return Vector3.zero;   // defensive: callers guard, but never NRE here.
            int[] id = chamber2 ? _c2BoundIds : _boundIds;
            float minX = _liquidMat.GetFloat(id[0]);
            float maxX = _liquidMat.GetFloat(id[1]);
            float minY = _liquidMat.GetFloat(id[2]);
            float maxY = _liquidMat.GetFloat(id[3]);
            float minZ = _liquidMat.GetFloat(id[4]);
            float maxZ = _liquidMat.GetFloat(id[5]);
            return new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
        }

        // ── STAGE-2 basis capture (ONE-TIME at spawn).
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
                    // Multi-frame shapes are PIECEWISE linear - a linear basis can't represent them.
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
                    if (mx > 1e-6f) { delta[s] = (Vector3[])tmp.Clone(); kept++; }   // shapes not touching the cum stay null (skipped in eval).
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
            // Rigidity snapshot: every skin bone's LOCAL transform.
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
            // ── ONE-TIME SPAWN ASSERTION: basis vs a direct BakeMesh at the restored weights.
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
        private void EvaluateBasis()
        {
            int n = _cumVertsUnique.Length;
            if (_lpCache == null || _lpCache.Length != n) _lpCache = new Vector3[n];
            System.Array.Copy(_basisRest, _lpCache, n);
            // Read weights from the dirty-check sentinel captured moments earlier this same call in
            // UpdateChamberExtents (line ~913), and equal to the restored weights at the spawn-assertion call site -- identical values to GetBlendShapeWeight(s), but skips the per-shape native interop every weights-change frame.
            bool useSentinel = _sentinelShapes != null && _sentinelShapes.Length == _basisDelta.Length;
            for (int s = 0; s < _basisDelta.Length; s++)
            {
                var d = _basisDelta[s];
                if (d == null) continue;
                float w = (useSentinel ? _sentinelShapes[s] : _smr.GetBlendShapeWeight(s)) * 0.01f;   // KK weight is 0..100.
                if (w == 0f) continue;
                for (int k = 0; k < n; k++)
                {
                    _lpCache[k].x += d[k].x * w;
                    _lpCache[k].y += d[k].y * w;
                    _lpCache[k].z += d[k].z * w;
                }
            }
        }

        // PER-FRAME RIGIDITY ASSERTION - the precompute's load-bearing assumption, checked on every dirty
        // frame.
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

        // Measure each chamber's world-space vertical extent from the LIVE skinned+blendshaped cum (submesh
        // 0).
        private Transform[] SkinBones()
        {
            if (_bonesCache == null && _smr != null) _bonesCache = _smr.bones;
            return _bonesCache;
        }
        private Matrix4x4[] SkinBindposes()
        {
            if (_bindposesCache == null && _smr != null && _smr.sharedMesh != null) _bindposesCache = _smr.sharedMesh.bindposes;
            return _bindposesCache;
        }

        // precomputed-basis evaluation (weights changed) + the ~1.1k-vert walk.
        private void UpdateChamberExtents()
        {
            if (_smr == null || _smr.sharedMesh == null || _liquidMat == null || _block == null)
                return;
            var shared = _smr.sharedMesh;
            if (shared.subMeshCount < 1)
                return;
            var bones = SkinBones();
            if (bones == null || bones.Length == 0)
                return;
            if (_cumVerts == null)
            {
                _cumVerts    = shared.GetTriangles(0);   // submesh 0 = cum (duplicate indices fine for min/max).
                _restVerts   = shared.vertices;   // base (rest) positions, object space.
            }
            if (_restVerts == null)
                return;
            // Deduplicate the submesh-0 triangle index list to DISTINCT vertices.
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
            // re-evaluation; a bone-matrix change (whole-item motion/rescale) only needs the walk below to re-transform the cached verts.
            int shapeCount = shared.blendShapeCount;
            bool bonesDirty   = _sentinelBones == null || _sentinelBones.Length != bones.Length;
            bool weightsDirty = _sentinelShapes == null || _sentinelShapes.Length != shapeCount;
            if (!bonesDirty)
                for (int b = 0; b < bones.Length; b++)
                {
                    Matrix4x4 cur = bones[b] != null ? bones[b].localToWorldMatrix : Matrix4x4.identity;
                    if (!MatNear(ref cur, ref _sentinelBones[b])) { bonesDirty = true; break; }
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

            // Unity (<2020) BakeMesh baked the renderer's lossyScale INTO the verts; the basis keeps that
            // convention (capture normalized ÷ scale, walk re-multiplies), so world recovery stays the UNIT-scale frame (position+rotation only).
            Transform rt = _renderer != null ? _renderer.transform : null;
            Matrix4x4 bakeM = rt != null ? Matrix4x4.TRS(rt.position, rt.rotation, Vector3.one) : Matrix4x4.identity;
            // ── STAGE-2: capture once at spawn, assert rigidity, evaluate on weight changes.
            if (!_basisCaptured && !CaptureSkinnedBasis(shared, rt))
                return;   // degenerate transient (logged) - retried next dirty frame.
            // via its TRANSFORM (Studio items).
            if (!_extAnchorCaptured && rt != null)
            {
                for (int b = 0; b < bones.Length; b++)
                    if (bones[b] != null && bones[b].name == WombCenterBone) { _extAnchor = bones[b]; break; }
                if (_extAnchor == null)
                    for (int b = 0; b < bones.Length; b++)
                        if (bones[b] != null) { _extAnchor = bones[b]; break; }
                if (_extAnchor != null)
                {
                    _extAnchor0Inv = _extAnchor.localToWorldMatrix.inverse;
                    _extBakeM0 = bakeM;
                    _extScale0 = rt.lossyScale;
                    _extAnchorCaptured = true;
                }
            }
            if (LiquidWobbleMPBPlugin.CfgDebugLog) AssertRigRigid(bones);   // dev sanity check (warn-only) -> Debug Log gated, so release does no per-frame bone scan and never spams the warning.
            if (weightsDirty || _lpCache == null)
                EvaluateBasis();
            Vector3 sNow;
            if (_extAnchorCaptured && _extAnchor != null)
            {
                Matrix4x4 Dw = _extAnchor.localToWorldMatrix * _extAnchor0Inv;   // anchor's world delta since capture.
                Matrix4x4 M = Dw * _extBakeM0;
                Vector3 my = ((Vector3)M.GetColumn(1)).normalized, mz = ((Vector3)M.GetColumn(2)).normalized;
                bakeM = Matrix4x4.TRS((Vector3)M.GetColumn(3), Quaternion.LookRotation(mz, my), Vector3.one);
                Vector3 sRel = new Vector3(((Vector3)Dw.GetColumn(0)).magnitude, ((Vector3)Dw.GetColumn(1)).magnitude, ((Vector3)Dw.GetColumn(2)).magnitude);
                sNow = Vector3.Scale(_extScale0, sRel);
            }
            else sNow = rt != null ? rt.lossyScale : Vector3.one;
            if (!_scaleWarned)
            {
                // Non-uniform RELATIVE rescale of a rotated rig is not component-wise.
                float rx = sNow.x / _basisScale0.x, ry = sNow.y / _basisScale0.y, rz = sNow.z / _basisScale0.z;
                if (Mathf.Abs(rx - ry) + Mathf.Abs(rx - rz) > 1e-2f * Mathf.Abs(rx))
                {
                    _scaleWarned = true;
                    LiquidWobbleMPBPlugin._logger?.LogWarning(
                        $"[fill] NON-UNIFORM rescale on '{name}' since basis capture ({_basisScale0.ToString("F4")} -> {sNow.ToString("F4")}, " +
                        $"per-axis ratio {rx:F4}/{ry:F4}/{rz:F4}): " +
                        $"the precomputed basis scales component-wise, so the measurement is approximate until respawn. Rescale uniformly or respawn the womb.");
                }
            }
            float divider = _liquidMat.GetFloat("_Bound1MinY_bottom");
            // Per chamber: WORLD-Y min/max (fill plane) + RENDERER-LOCAL AABB.
            float wy1mn = float.MaxValue, wy1mx = float.MinValue, wy2mn = float.MaxValue, wy2mx = float.MinValue;
            EnsureCoreBand(divider);
            float cy1mn = float.MaxValue, cy1mx = float.MinValue;
            float cl1mn = float.MaxValue, cl1mx = float.MinValue;
            Vector3 mn1 = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 mx1 = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Vector3 mn2 = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 mx2 = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            // GPU-vs-plugin probe vertex: tri0[0] = the GS's input[0] of primID 0 (first index of submesh 0).
            int v0 = (_cumVerts != null && _cumVerts.Length > 0) ? _cumVerts[0] : -1;
            // Chamber-1 verts are captured for the contact profile.
            if (_profWp == null || _profWp.Length < _cumVertsUnique.Length)
                _profWp = new Vector3[_cumVertsUnique.Length];
            _profCount = 0;
            for (int k = 0; k < _cumVertsUnique.Length; k++)
            {
                int i = _cumVertsUnique[k];
                if (i < 0 || i >= _restVerts.Length)
                    continue;
                // Basis-evaluated vert (normalized) × current scale = the BakeMesh-convention renderer-local
                // vert; unit-scale frame -> world == the GPU render.
                Vector3 lp = _lpCache[k];
                lp.x *= sNow.x; lp.y *= sNow.y; lp.z *= sNow.z;
                Vector3 wp = bakeM.MultiplyPoint3x4(lp);
                if (_wpCache == null || _wpCache.Length < _cumVertsUnique.Length) _wpCache = new Vector3[_cumVertsUnique.Length];
                _wpCache[k] = wp;
                if (i == v0) _dbgV0W = wp;   // probe vertex world pos (same source as the box).
                if (_restVerts[i].y < divider - ChamberSplitBias)
                {
                    if (wp.y < wy2mn) wy2mn = wp.y; if (wp.y > wy2mx) wy2mx = wp.y;
                    if (lp.x < mn2.x) mn2.x = lp.x; if (lp.y < mn2.y) mn2.y = lp.y; if (lp.z < mn2.z) mn2.z = lp.z;
                    if (lp.x > mx2.x) mx2.x = lp.x; if (lp.y > mx2.y) mx2.y = lp.y; if (lp.z > mx2.z) mx2.z = lp.z;
                }
                else
                {
                    _profWp[_profCount++] = wp;   // chamber-1 sample for the contact profile.
                    if (wp.y < wy1mn) wy1mn = wp.y; if (wp.y > wy1mx) wy1mx = wp.y;
                    float ry = _restVerts[i].y;
                    if (ry >= _coreLo && ry <= _coreHi)
                    {
                        if (wp.y < cy1mn) cy1mn = wp.y; if (wp.y > cy1mx) cy1mx = wp.y;
                        if (lp.y < cl1mn) cl1mn = lp.y; if (lp.y > cl1mx) cl1mx = lp.y;
                    }
                    if (lp.x < mn1.x) mn1.x = lp.x; if (lp.y < mn1.y) mn1.y = lp.y; if (lp.z < mn1.z) mn1.z = lp.z;
                    if (lp.x > mx1.x) mx1.x = lp.x; if (lp.y > mx1.y) mx1.y = lp.y; if (lp.z > mx1.z) mx1.z = lp.z;
                }
            }
            if (v0 >= 0)
                _block.SetVector("_DbgVert0World", new Vector4(_dbgV0W.x, _dbgV0W.y, _dbgV0W.z, 1f));
            // Oriented-box frame: renderer position + rotation columns (unit scale.
            Vector3 fX = bakeM.GetColumn(0), fY = bakeM.GetColumn(1), fZ = bakeM.GetColumn(2), fP = bakeM.GetColumn(3);
            _block.SetVector("_BoxFrameX",   new Vector4(fX.x, fX.y, fX.z, 0f));
            _block.SetVector("_BoxFrameY",   new Vector4(fY.x, fY.y, fY.z, 0f));
            _block.SetVector("_BoxFrameZ",   new Vector4(fZ.x, fZ.y, fZ.z, 0f));
            _block.SetVector("_BoxFramePos", new Vector4(fP.x, fP.y, fP.z, 1f));
            if (wy1mx > wy1mn)
            {
                // RANGE, not the volume.
                _dbgExt1 = new Vector4(wy1mn, wy1mx, 0f, 1f); _block.SetVector("_Chamber1ExtentY", _dbgExt1);
                _block.SetVector("_Box1LocalMin", new Vector4(mn1.x, mn1.y, mn1.z, 1f));
                _block.SetVector("_Box1LocalMax", new Vector4(mx1.x, mx1.y, mx1.z, 1f));
                _dbgBakeC1 = bakeM.MultiplyPoint3x4((mn1 + mx1) * 0.5f);   // measured chamber-1 center (world) for the [fill] log.
                BuildCapProfile(wy1mn, wy1mx);   // v361 rim clamp (validity coupled to the extents; pushed every frame).
            }
            if (wy2mx > wy2mn)
            {
                _dbgExt2 = new Vector4(wy2mn, wy2mx, 0f, 1f); _block.SetVector("_Chamber2ExtentY", _dbgExt2);
                _block.SetVector("_Box2LocalMin", new Vector4(mn2.x, mn2.y, mn2.z, 1f));
                _block.SetVector("_Box2LocalMax", new Vector4(mx2.x, mx2.y, mx2.z, 1f));
            }
            BuildVolumeProfiles(divider, wy1mn, wy1mx, wy2mn, wy2mx);
            LogDomeProfile(divider);
        }

        // chamber.
        private Vector3[] _wpCache;
        private readonly float[] _pbMnX = new float[32], _pbMxX = new float[32], _pbMnZ = new float[32], _pbMxZ = new float[32];
        private readonly int[] _pbCnt = new int[32];
        private readonly float[] _volCum1 = new float[16], _volCum2 = new float[16];
        private float _volTot1, _volTot2;   // absolute chamber volumes (world m^3-ish).
        private float _shY1mn, _shY1mx, _shY2mn, _shY2mx;   // extents the curves were measured over.
        private bool _shCh2ok, _shapeFed;

        private void BuildVolumeProfiles(float divider, float y1mn, float y1mx, float y2mn, float y2mx)
        {
            if (_wpCache == null || _restVerts == null) return;
            bool ch1ok = y1mx > y1mn + 1e-6f;
            bool ch2ok = y2mx > y2mn + 1e-6f;
            if (!ch1ok) { _shapeFed = false; return; }   // nothing measurable - shader flag stays off below.
            const int PB = 16; const float QPI = 0.785398163f;
            for (int b = 0; b < 32; b++) { _pbMnX[b] = float.MaxValue; _pbMxX[b] = float.MinValue; _pbMnZ[b] = float.MaxValue; _pbMxZ[b] = float.MinValue; _pbCnt[b] = 0; }
            float r1 = PB / Mathf.Max(y1mx - y1mn, 1e-6f);
            float r2 = PB / Mathf.Max(y2mx - y2mn, 1e-6f);
            for (int k = 0; k < _cumVertsUnique.Length; k++)
            {
                int i = _cumVertsUnique[k]; if (i < 0 || i >= _restVerts.Length) continue;
                bool ch2 = _restVerts[i].y < divider - ChamberSplitBias;
                if (ch2 && !ch2ok) continue;
                Vector3 wp = _wpCache[k];
                int b = ch2 ? 16 + Mathf.Clamp((int)((wp.y - y2mn) * r2), 0, PB - 1)
                            : Mathf.Clamp((int)((wp.y - y1mn) * r1), 0, PB - 1);
                if (wp.x < _pbMnX[b]) _pbMnX[b] = wp.x; if (wp.x > _pbMxX[b]) _pbMxX[b] = wp.x;
                if (wp.z < _pbMnZ[b]) _pbMnZ[b] = wp.z; if (wp.z > _pbMxZ[b]) _pbMxZ[b] = wp.z;
                _pbCnt[b]++;
            }
            _volTot1 = PackProfile(0, (y1mx - y1mn) / PB, QPI, _volCum1);
            _volTot2 = ch2ok ? PackProfile(16, (y2mx - y2mn) / PB, QPI, _volCum2) : 0f;
            _shY1mn = y1mn; _shY1mx = y1mx; _shY2mn = y2mn; _shY2mx = y2mx;
            _shCh2ok = ch2ok && _volTot2 > 1e-12f;
            _shapeFed = _volTot1 > 1e-12f;
        }

        private float PackProfile(int b0, float binH, float qpi, float[] dst)
        {
            float total = 0f;
            for (int s = 0; s < 16; s++)
            {
                int b = b0 + s;
                float area = 0f;
                if (_pbCnt[b] > 0)
                {
                    float dx = Mathf.Max(_pbMxX[b] - _pbMnX[b], 1e-4f);
                    float dz = Mathf.Max(_pbMxZ[b] - _pbMnZ[b], 1e-4f);
                    area = dx * dz * qpi;
                }
                total += area * binH;
                dst[s] = total;   // cumulative (normalized below).
            }
            float inv = total > 1e-12f ? 1f / total : 0f;
            for (int s = 0; s < 16; s++) dst[s] *= inv;
            dst[15] = 1f;   // pin F(top) = exactly 1 (fill=1 truly full).
            return total;
        }

        private void SolveShapeFillLevels()
        {
            float f1 = Mathf.Clamp01(_liquidMat.GetFloat("_FillAmount"));
            float f2 = Mathf.Clamp01(_liquidMat.GetFloat("_FillAmount2"));
            // closed mode: each chamber fills on its own curve, plane spans its own extents.
            _block.SetFloat("_ShapeYfill1", Mathf.Lerp(_shY1mn, _shY1mx, InvertCum(_volCum1, f1)));
            _block.SetFloat("_ShapeYfill2", _shCh2ok
                ? Mathf.Lerp(_shY2mn, _shY2mx, InvertCum(_volCum2, f2))
                : Mathf.Lerp(_shY1mn, _shY1mx, InvertCum(_volCum1, f2)));
            // connected mode: one shared level over both chambers, weighted by absolute volumes.
            float lo = _shCh2ok ? Mathf.Min(_shY1mn, _shY2mn) : _shY1mn;
            float hi = _shCh2ok ? Mathf.Max(_shY1mx, _shY2mx) : _shY1mx;
            float ym;
            if (f1 <= 0f) ym = lo;
            else if (f1 >= 1f) ym = hi;
            else
            {
                float target = f1 * (_volTot1 + (_shCh2ok ? _volTot2 : 0f));
                float a = lo, b = hi;
                for (int it = 0; it < 24; it++)
                {
                    float m = 0.5f * (a + b);
                    float v = _volTot1 * SampleCum(_volCum1, (m - _shY1mn) / Mathf.Max(_shY1mx - _shY1mn, 1e-6f));
                    if (_shCh2ok) v += _volTot2 * SampleCum(_volCum2, (m - _shY2mn) / Mathf.Max(_shY2mx - _shY2mn, 1e-6f));
                    if (v < target) a = m; else b = m;
                }
                ym = 0.5f * (a + b);
            }
            _block.SetFloat("_ShapeYconn", ym);
        }

        private static float InvertCum(float[] F, float fill)
        {
            if (fill <= 0f) return 0f;
            if (fill >= 1f) return 1f;
            float prev = 0f;
            for (int s = 0; s < 16; s++)
            {
                float cur = F[s];
                if (fill <= cur) return (s + (fill - prev) / Mathf.Max(cur - prev, 1e-9f)) / 16f;
                prev = cur;
            }
            return 1f;
        }

        private static float SampleCum(float[] F, float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float x = t * 16f;
            int s = (int)x; if (s > 15) s = 15;
            float prev = s > 0 ? F[s - 1] : 0f;
            return prev + (F[s] - prev) * (x - s);
        }

        public static float CoreFrac = 0.80f;
        private float _coreLo, _coreHi; private bool _coreDone; private float _coreFracUsed = -1f;
        public static void InvalidateCoreBand() { s_coreEpoch++; }
        private static int s_coreEpoch; private int _coreEpochSeen = -1;

        private void EnsureCoreBand(float divider)
        {
            if (_coreDone && _coreFracUsed == CoreFrac && _coreEpochSeen == s_coreEpoch) return;
            _coreFracUsed = CoreFrac; _coreEpochSeen = s_coreEpoch;
            if (_restVerts == null || _cumVertsUnique == null) return;
            const int NB = 12;
            float yMin = float.MaxValue, yMax = float.MinValue;
            for (int k = 0; k < _cumVertsUnique.Length; k++)
            {
                int i = _cumVertsUnique[k]; if (i < 0 || i >= _restVerts.Length) continue;
                if (_restVerts[i].y < divider) continue;
                float y = _restVerts[i].y; if (y < yMin) yMin = y; if (y > yMax) yMax = y;
            }
            if (yMax <= yMin) { _coreLo = float.MinValue; _coreHi = float.MaxValue; return; }
            var xN = new float[NB]; var xP = new float[NB]; var zN = new float[NB]; var zP = new float[NB];
            float range = yMax - yMin;
            for (int k = 0; k < _cumVertsUnique.Length; k++)
            {
                int i = _cumVertsUnique[k]; if (i < 0 || i >= _restVerts.Length) continue;
                Vector3 v = _restVerts[i]; if (v.y < divider) continue;
                int b = (int)((v.y - yMin) / range * NB); if (b >= NB) b = NB - 1; if (b < 0) b = 0;
                if (v.x < xN[b]) xN[b] = v.x; if (v.x > xP[b]) xP[b] = v.x;
                if (v.z < zN[b]) zN[b] = v.z; if (v.z > zP[b]) zP[b] = v.z;
            }
            float pkX = 0f, pkZ = 0f;
            for (int b = 0; b < NB; b++)
            {
                float hx = (xP[b] - xN[b]) * 0.5f, hz = (zP[b] - zN[b]) * 0.5f;
                if (hx > pkX) pkX = hx; if (hz > pkZ) pkZ = hz;
            }
            int lo = -1, hi = -1;
            for (int b = 0; b < NB; b++)
            {
                float hx = (xP[b] - xN[b]) * 0.5f, hz = (zP[b] - zN[b]) * 0.5f;
                if (hx >= pkX * CoreFrac && hz >= pkZ * CoreFrac) { if (lo < 0) lo = b; hi = b; }
            }
            float bandH = range / NB;
            if (lo < 0) { _coreLo = yMin; _coreHi = yMax; }   // nothing qualified -> full span.
            else { _coreLo = yMin + lo * bandH; _coreHi = yMin + (hi + 1) * bandH; }
            _coreDone = true;
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: CORE-BAND frac=" + CoreFrac.ToString("F2")
                + " -> restY[" + _coreLo.ToString("F4") + ".." + _coreHi.ToString("F4") + "] of dome["
                + yMin.ToString("F4") + ".." + yMax.ToString("F4") + "]  (clipped bottom "
                + ((_coreLo - yMin) * 1000f).ToString("F1") + "mm, top " + ((yMax - _coreHi) * 1000f).ToString("F1")
                + "mm) — the fill volume now ignores those.");
        }

        private float _domeLogNext;
        private void LogDomeProfile(float divider)
        {
            if (!LiquidWobbleMPBPlugin.Configured || !LiquidWobbleMPBPlugin.CfgDebugLog) return;
            if (Time.unscaledTime < _domeLogNext || _restVerts == null || _cumVertsUnique == null) return;
            _domeLogNext = Time.unscaledTime + 3f;
            try
            {
                const int NB = 12;
                float yMin = float.MaxValue, yMax = float.MinValue;
                for (int k = 0; k < _cumVertsUnique.Length; k++)
                {
                    int i = _cumVertsUnique[k]; if (i < 0 || i >= _restVerts.Length) continue;
                    if (_restVerts[i].y < divider) continue;   // chamber 1 (womb) only.
                    float y = _restVerts[i].y; if (y < yMin) yMin = y; if (y > yMax) yMax = y;
                }
                if (yMax <= yMin) return;
                float L = _liquidMat.GetFloat("_Bound3MinX_left"), R = _liquidMat.GetFloat("_Bound4MaxX_right");
                float Bk = _liquidMat.GetFloat("_Bound5MinZ_back"), Fr = _liquidMat.GetFloat("_Bound6MaxZ_front");
                float cx = (L + R) * 0.5f, cz = (Bk + Fr) * 0.5f;
                // per band, SIGNED extents from the box centre in X and Z separately (asymmetry matters).
                var xNeg = new float[NB]; var xPos = new float[NB]; var zNeg = new float[NB]; var zPos = new float[NB];
                float range = yMax - yMin;
                for (int k = 0; k < _cumVertsUnique.Length; k++)
                {
                    int i = _cumVertsUnique[k]; if (i < 0 || i >= _restVerts.Length) continue;
                    Vector3 v = _restVerts[i]; if (v.y < divider) continue;
                    int b = (int)((v.y - yMin) / range * NB); if (b >= NB) b = NB - 1; if (b < 0) b = 0;
                    float dx = v.x - cx, dz = v.z - cz;
                    if (dx < xNeg[b]) xNeg[b] = dx; if (dx > xPos[b]) xPos[b] = dx;
                    if (dz < zNeg[b]) zNeg[b] = dz; if (dz > zPos[b]) zPos[b] = dz;
                }
                var wX = new System.Text.StringBuilder(); var wZ = new System.Text.StringBuilder();
                float pkX = 0f, pkZ = 0f;
                for (int b = 0; b < NB; b++)
                {
                    float hx = (xPos[b] - xNeg[b]) * 0.5f, hz = (zPos[b] - zNeg[b]) * 0.5f;
                    if (hx > pkX) pkX = hx; if (hz > pkZ) pkZ = hz;
                    wX.Append((hx * 1000f).ToString("F0")); wZ.Append((hz * 1000f).ToString("F0"));
                    if (b < NB - 1) { wX.Append(","); wZ.Append(","); }
                }
                // Recommend: X/Z sized to the dome's own extent (fills the box in the widest region), and
                // TOP clipped to the last band where both X and Z are still >= FitFrac of their peak.
                const float FitFrac = 0.80f; float bandH = range / NB; int hiB = 0;
                for (int b = 0; b < NB; b++)
                {
                    float hx = (xPos[b] - xNeg[b]) * 0.5f, hz = (zPos[b] - zNeg[b]) * 0.5f;
                    if (hx >= pkX * FitFrac && hz >= pkZ * FitFrac) hiB = b;
                }
                float recTop = yMin + (hiB + 1) * bandH;
                float recL = cx - pkX, recR = cx + pkX, recBk = cz - pkZ, recFr = cz + pkZ;
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: DOME-PROFILE '" + name + "' restY[" + yMin.ToString("F3") + ".." + yMax.ToString("F3")
                    + "] bottom->top  Xhalf=[" + wX + "]mm(pk" + (pkX * 1000f).ToString("F0") + ")  Zhalf=[" + wZ + "]mm(pk" + (pkZ * 1000f).ToString("F0") + ")");
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: DOME-FIT recommend (X/Z=dome extent, top clipped where <" + (FitFrac * 100f).ToString("F0")
                    + "% peak): L=" + recL.ToString("F4") + " R=" + recR.ToString("F4") + " Bottom=" + yMin.ToString("F4") + " Top=" + recTop.ToString("F4")
                    + " Back=" + recBk.ToString("F4") + " Front=" + recFr.ToString("F4")
                    + "  ||  CURRENT: L=" + L.ToString("F4") + " R=" + R.ToString("F4") + " Bottom=" + _liquidMat.GetFloat("_Bound1MinY_bottom").ToString("F4")
                    + " Top=" + _liquidMat.GetFloat("_Bound2MaxY_top").ToString("F4") + " Back=" + Bk.ToString("F4") + " Front=" + Fr.ToString("F4"));
            }
            catch (System.Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: dome profile failed: " + e.Message); }
        }

        // Build the chamber-1 wall cross-section from this measurement's world verts.
        private void BuildCapProfile(float yMin, float yMax)
        {
            float range = Mathf.Max(yMax - yMin, 1e-6f);
            for (int h = 0; h < ProfH; h++) { _profSumX[h] = 0f; _profSumZ[h] = 0f; _profCnt[h] = 0; }
            // 1) world-Y row binning + per-row XZ centroid accumulation.
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
            // 2) rows with no verts (sparse slab) borrow the nearest filled row's centroid.
            for (int h = 0; h < ProfH; h++)
            {
                if (_profRowAny[h]) continue;
                int src = -1;
                for (int d = 1; d < ProfH && src < 0; d++)
                {
                    if (h - d >= 0 && _profRowAny[h - d]) src = h - d;
                    else if (h + d < ProfH && _profRowAny[h + d]) src = h + d;
                }
                _profC[h] = src >= 0 ? _profC[src] : Vector4.zero;   // src==-1 impossible (caller guards >=1 ch-1 vert).
            }
            // 3) per-cell MAX radii, max-SPLAT into the cells the shader's bilinear sample reads at this
            // vert's own (angle, height).
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
            // 4) rows still all-zero after the splat copy the nearest non-empty row's cells.
            for (int h = 0; h < ProfH; h++)
            {
                int b = h * ProfA; bool any = false;
                for (int a = 0; a < ProfA; a++) if (_profR[b + a] > 0f) { any = true; break; }
                _profRowAny[h] = any;   // reused: now = post-splat radius-row occupancy.
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
            // 5) angular hole fill per row: empty cells take the MAX of their wrap neighbours, iterated
            // until closed (<= ProfA passes).
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

        // Max-splat one vert's radius (about the row's centroid) into the 2 angle cells around its bearing
        // in that row.
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

        // Exact element-wise matrix compare for the dirty-check.
        private static bool MatEq(ref Matrix4x4 a, ref Matrix4x4 b)
        {
            return a.m00 == b.m00 && a.m01 == b.m01 && a.m02 == b.m02 && a.m03 == b.m03 &&
                   a.m10 == b.m10 && a.m11 == b.m11 && a.m12 == b.m12 && a.m13 == b.m13 &&
                   a.m20 == b.m20 && a.m21 == b.m21 && a.m22 == b.m22 && a.m23 == b.m23 &&
                   a.m30 == b.m30 && a.m31 == b.m31 && a.m32 == b.m32 && a.m33 == b.m33;
        }
        private const float PosEps = 0.002f, AxisEps = 0.002f;
        private static bool MatNear(ref Matrix4x4 a, ref Matrix4x4 b)
        {
            float d;
            d = a.m03 - b.m03; if (d > PosEps || d < -PosEps) return false;
            d = a.m13 - b.m13; if (d > PosEps || d < -PosEps) return false;
            d = a.m23 - b.m23; if (d > PosEps || d < -PosEps) return false;
            d = a.m00 - b.m00; if (d > AxisEps || d < -AxisEps) return false;
            d = a.m01 - b.m01; if (d > AxisEps || d < -AxisEps) return false;
            d = a.m02 - b.m02; if (d > AxisEps || d < -AxisEps) return false;
            d = a.m10 - b.m10; if (d > AxisEps || d < -AxisEps) return false;
            d = a.m11 - b.m11; if (d > AxisEps || d < -AxisEps) return false;
            d = a.m12 - b.m12; if (d > AxisEps || d < -AxisEps) return false;
            d = a.m20 - b.m20; if (d > AxisEps || d < -AxisEps) return false;
            d = a.m21 - b.m21; if (d > AxisEps || d < -AxisEps) return false;
            d = a.m22 - b.m22; if (d > AxisEps || d < -AxisEps) return false;
            return true;
        }

        private float ClampImpulse(float raw)
        {
            return Mathf.Clamp(raw * MaxWobble, -MaxWobble, MaxWobble);
        }
    }
}
