// CloXray/Liquid - x-ray womb cum/liquid shader (Koikatsu CharaStudio).
Shader "CloXray/Liquid"
{
    Properties
    {
        // ── Color ─────────────────────────────────────────────────────────
        _Color   ("Color (alpha = transparency)", Color) = (1, 1, 0.96, 0.614)
        _MainTex ("Texture", 2D) = "white" {}
        // ── Goo: matcap + sheen ───────────────────────────────────────────
        _Matcap      ("Matcap (sphere capture)", 2D) = "white" {}
        _MatcapAlpha ("Matcap Strength (0=off)", Range(0,1)) = 0.05
        _Sh          ("Sheen Darkness (depth feel)", Range(0,1)) = 0.27
        // ── Goo: fresnel ──────────────────────────────────────────────────
        _FresnelPower ("Fresnel Power",  Range(0,5))   = 2.2
        _FresnelScale ("Fresnel Scale (0=off)", Range(0,255)) = 0.5
        _FresnelBias  ("Fresnel Bias",   Range(0,5))   = 0.01
        _FresnelAlpha ("Fresnel Cap (0=off)", Range(0,1)) = 0.385
        // Cum EMISSION glow - added ON TOP of the goo colour (HDR, so it can bloom). Default Strength 0 = no glow
        // (unchanged). Keyframe _EmissionStrength for a creampie / impregnation bloom; tint via _EmissionColor.
        [HDR] _EmissionColor ("Cum Emission Color", Color) = (1, 1, 1, 1)
        _EmissionStrength ("Cum Emission Strength (0=off)", Range(0, 5)) = 0
        // ── Cap shape ─────────────────────────────────────────────────────
        [Toggle] _CapSmooth ("Cap Smooth Normals", Float) = 1
        // Adaptive tessellation of the cap/rim region (1 = off). Subdivides only the
        _CapTess ("Cap Tessellation (1=off)", Range(1, 6)) = 4
        // CLOSED-MODE only: flatten the chamber-1 cervix funnel (verts within this rest-
        _NeckFloorBand ("Neck Floor Flatten (closed mode, 0=off)", Range(0, 0.1)) = 0.02
        [Enum(Off,0,Filled,1,Raw,2,Normals,3,Facets,4,NormalVsFacet,5)] _DebugLiquidCap ("DEBUG (0=off 1=filled 2=raw 3=normals 4=facets 5=mismatch)", Float) = 0
        [Toggle] _PerTriSnap ("Per-triangle snap scale (fix teeth)", Float) = 0
        _ShaderVersion ("Shader build id (diagnostic)", Float) = 390
        // Edge rounding (closed mode): fillet radius where the surface meets the wall,
        _EdgeRadius ("Edge Rounding Radius (x half-height, 0=flat)", Range(0, 1)) = 0.5
        // Cap Dome (SHADING only): tilt the flat cap's normal outward by radius so under the matcap it
        // shades like a rounded dome that meets the wall smoothly, instead of a flat blown-out disc. 0=flat.
        _CapDome ("Cap Dome (shading round, 0=flat)", Range(0, 2)) = 0.7
        // ── Fill ──────────────────────────────────────────────────────────
        _FillAmount  ("Fill Amount (0=empty, 1=full)", Range(0,1)) = 0.2
        // ── Object physics boundary box ───────────────────────────────────
        // The 6 planes of the liquid's bounding box, in the OBJECT's local space.
        _Bound1MinY_bottom ("Bound Min Y (bottom)", Range(-1.5,1.5)) = -0.5
        _Bound2MaxY_top    ("Bound Max Y (top)",    Range(-1.5,1.5)) =  0.5
        _Bound3MinX_left   ("Bound Min X (left)",   Range(-1.5,1.5)) = -0.5
        _Bound4MaxX_right  ("Bound Max X (right)",  Range(-1.5,1.5)) =  0.5
        _Bound5MinZ_back   ("Bound Min Z (back)",   Range(-1.5,1.5)) = -0.5
        _Bound6MaxZ_front  ("Bound Max Z (front)",  Range(-1.5,1.5)) =  0.5
        // Setup aid: instead of clipping, tint the parts outside the box red so
        // you can see where each plane lands on the mesh. Turn off when done.
        [Toggle] _ShowSetupPhysicsBounds ("Show Setup Physics Bounds", Float) = 0
        [Toggle] _ShowTipDebug ("Show Tip Debug (BP read)", Float) = 0
        // Rest-pos mode (hidden, not in the Material Editor). 1 = the mesh bakes each
        // vertex's REST position into UV2 and all fill math runs there - required for a
        [Toggle] _UseRestPosTangent ("Rest-Pos Mode (skinned)", Float) = 0
        // Open-mesh cap (bottles): 1 = also build the liquid surface as a fan from the chamber centre
        // to the fill rim, so a mesh OPEN at the top (a bottle mouth) still caps closed. 0 = womb (closed mesh).
        [Toggle] _CapForOpenMesh ("Cap Open-Mesh Holes (bottle)", Float) = 0
        // Volume mode: 0 = off (linear height), 1 = cube (box -
        // linear volume, tilt-compensated), 2 = ellipsoid (cubic cap volume).
        [Enum(Off,0,Cube,1,Ellipsoid,2)] _VolumeConserve_0off_1cube_2ellipsoid ("Volume Mode (0=off,1=cube,2=ellipsoid)", Float) = 1
        // ── Two chambers (e.g. womb + tube, or bottle + throat) ───────────
        // 0 = Single - one box (_Bound*), one fill (_FillAmount). [current]
        // 1 = Connected- two boxes share ONE world-horizontal level; _FillAmount
        // 2 = Closed - two independent levels: chamber 1 uses _FillAmount,
        [Enum(Single,0,Connected,1,Closed,2)] _ChamberMode_0single_1connected_2closed ("Chambers (0=single 1=connected 2=closed)", Float) = 1
        _FillAmount2 ("Fill Amount Chamber 2 (closed mode)", Range(0,1)) = 0.2
        // Chamber-2 (canal/tube) BOTTOM-CLEAR: with the top level (_FillAmount2) fixed, this empties the canal
        // from the BOTTOM up, so the cum becomes a BAND [bottom-clear .. top]. 0 = cum reaches the floor (no
        // change); 1 = cleared all the way up to the top level. Lets you drain the canal from the bottom
        _FillBottom2 ("Fill Bottom Clear C2 (0=full, 1=cleared up)", Range(0,1)) = 0
        // Chamber-2 bounds box (same convention as _Bound*). Chamber 1 = _Bound*.
        _C2Bound1MinY_bottom ("C2 Bound Min Y (bottom)", Range(-1.5,1.5)) = -0.5
        _C2Bound2MaxY_top    ("C2 Bound Max Y (top)",    Range(-1.5,1.5)) =  0.5
        _C2Bound3MinX_left   ("C2 Bound Min X (left)",   Range(-1.5,1.5)) = -0.5
        _C2Bound4MaxX_right  ("C2 Bound Max X (right)",  Range(-1.5,1.5)) =  0.5
        _C2Bound5MinZ_back   ("C2 Bound Min Z (back)",   Range(-1.5,1.5)) = -0.5
        _C2Bound6MaxZ_front  ("C2 Bound Max Z (front)",  Range(-1.5,1.5)) =  0.5
        // ── Wobble (fill plane tilt) ──────────────────────────────────────
        _RotationX        ("Rotation X (plugin/manual)", Range(-1,1)) = 0.0
        _RotationZ        ("Rotation Z (plugin/manual)", Range(-1,1)) = 0.0
        // Second wobble pair for CHAMBER 2 (tube), used only when _PerChamberWobble is on. The plugin
        _Rotation2X       ("Rotation2 X (chamber-2 tube)", Range(-1,1)) = 0.0
        _Rotation2Z       ("Rotation2 Z (chamber-2 tube)", Range(-1,1)) = 0.0
        [Toggle] _PerChamberWobble ("Per-chamber wobble (1=tube uses Rotation2)", Float) = 0
        _IdleWobbleSpeed    ("Idle Wobble Speed (auto-sine)",    Range(0,10))  = 0.0
        _IdleWobbleStrength ("Idle Wobble Strength (auto-sine)", Range(0,0.2)) = 0.0
        // Plugin SETUP sliders surfaced here so they can be tuned in Material Editor instead of ComponentUtil.
        // The SHADER does not use these - the LiquidWobbleMPB plugin READS them from the material (throttled)
        _MaxWobble       ("Setup: Max Wobble",                 Range(0, 0.2)) = 0.03
        _WobbleSpeed     ("Setup: Wobble Speed",               Range(0, 10))  = 1.0
        _Recovery        ("Setup: Wobble Recovery (>0)",       Range(0.05, 10)) = 1.0
        // ME shows the PROPERTY NAME as the row label, so the modes live in the name itself
        // (same convention as _ChamberMode_0single_1connected_2closed). Plugin 332+ reads this name.
        _ThrustSlosh_0off_1global_2perChamber ("Setup: Thrust Slosh (0off/1global/2perChamber)", Range(0, 2)) = 0
        _ThrustSloshGain ("Setup: Thrust Slosh Gain",          Range(0, 5))   = 2.0
        // ── Reveal gate (stencil) ─────────────────────────────────────────
        // then draw the liquid only there. Default Ref=64 / ReadMask=0 = UNGATED (Comp passes everywhere - the
        // Plane-gated cum (show only through the x-ray plane): Ref=192 (=64|128), ReadMask=128 -> needs bit7.
        // Tie to a specific revealed organ region R: Ref = R|64, ReadMask = that organ's StencilReadMask
        [IntRange] _CumStencilRef      ("Reveal gate Ref (64=ungated; 192=plane-gated)",   Range(0,255)) = 64
        [IntRange] _CumStencilReadMask ("Reveal gate ReadMask (0=ungated; 128=plane)",     Range(0,255)) = 0
        // Polygon depth offset for the cum surface (DepthPrime + Combined). Use this when the cum sits inside
        // ANOTHER coincident surface - another Liquid item, or an Organ the cum was added to as a 2nd material -
        // 0 = no offset (default, unchanged). Try -2: that pulls the cum toward the camera so it wins the shared
        // surface. If the wrong surface wins (cum hides), flip the sign (+2). Drives both Offset factor and units.
        _DepthOffset ("Cum depth offset (0=off; -2 pulls cum to front to break z-fight)", Range(-10, 10)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent+502"
            "IgnoreProjector" = "True"
            "RenderType"      = "Transparent"
            "DisableBatching" = "True"
        }
        LOD 100

        // ── Shared CG code for both passes ────────────────────────────────
        CGINCLUDE
        #pragma target 3.0
        #include "UnityCG.cginc"

        float _FillAmount, _RotationX, _RotationZ, _IdleWobbleSpeed, _IdleWobbleStrength;
        float _Rotation2X, _Rotation2Z, _PerChamberWobble;
        float _VolumeConserve_0off_1cube_2ellipsoid;
        float _Bound1MinY_bottom, _Bound2MaxY_top, _Bound3MinX_left, _Bound4MaxX_right, _Bound5MinZ_back, _Bound6MaxZ_front;
        float _ShowSetupPhysicsBounds, _UseRestPosTangent, _CapForOpenMesh;
        // Tip-detection DEBUG (plugin-pushed; gated by _ShowTipDebug). _DebugTipPos.w=1 when valid.
        float _ShowTipDebug, _DebugTipDepth01, _DebugTipGirth;
        float4 _DebugTipPos, _DebugTipDir;
        float _ChamberMode_0single_1connected_2closed, _FillAmount2, _FillBottom2;
        // -- v390 "SHAPE" volume (plugin-driven, MPB-only -- no Properties entries on purpose) --
        // (neck-narrow, upper-fat), under tilt, and under any blendshape (wombbig/pregnant/
        // displace2...), which the static box+smoothstep models cannot be. Absent plugin => 0 =>
        float  _VolumeShape;
        float  _ShapeYfill1;   // chamber 1 (womb) fill plane, world Y (closed mode)
        float  _ShapeYfill2;   // chamber 2 (tube) fill plane, world Y (closed mode)
        float  _ShapeYconn;    // connected shared level, world Y
        float _NeckFloorBand, _EdgeRadius, _CapDome;
        float _DebugLiquidCap;   // DEBUG: tint by chamber (red=womb, green=tube) + bright where fillSign>0
        float _ShaderVersion;    // DIAGNOSTIC: build id, read+logged by the plugin to prove the live shader
        float _PerTriSnap;       // 1 = per-triangle local rest->world scale for the cap snap (fixes teeth+shimmer)
        // ── Plugin-driven uniforms (LiquidWobbleMPB) - REST mode only ─────
        float4 _ObjectScaleVec;
        // World-up expressed in the mesh's REST frame (rootBone.InverseTransformDirection
        // (Vector3.up)). Lets the fill classify along true world-vertical while staying
        float4 _RestWorldUp;
        // WORLD-space centre of each chamber's box (plugin writes these in rest mode via the
        float4 _Box1CenterWorld, _Box2CenterWorld;
        // Per-chamber RENDERER-LOCAL AABB of the actual skinned cum verts (min/max .xyz; .w=1 valid)
        float4 _Box1LocalMin, _Box1LocalMax, _Box2LocalMin, _Box2LocalMax;
        float4 _BoxFrameX, _BoxFrameY, _BoxFrameZ, _BoxFramePos;
        // GPU-vs-plugin differential probe: the plugin's bone-world position of cum vertex
        // tri0[0] (.w=1 valid). The BoundsOverlay draws a small cross at the GPU's own wpos of
        float4 _DbgVert0World;
        // LIVE per-chamber world-Y extent (minY, maxY, 0, w=1 when valid), measured by the plugin
        // from the actual skinned+blendshaped cum (BakeMesh). The rest box can't see blendshape
        // inflation or per-chamber scale, so fill=1 undershot the real bulb; the shader places the
        float4 _Chamber1ExtentY, _Chamber2ExtentY;
        // ── Cap contact profile (closed mode, chamber 1/womb) - plugin-measured ──
        // The cum-wall cross-section: CAPPROF_H world-Y rows over _Chamber1ExtentY, each
        // _CapProfInfo.w is set whenever the extents are - the GS gate fail-hards on it.
        // Uniform cbuffer arrays with computed indices (the X4580 ban is on LOCAL arrays).
        #define CAPPROF_H 16
        #define CAPPROF_A 16
        float4 _CapProfC[CAPPROF_H];              // per row: (cx, cz, 0, 1)
        float  _CapProfR[CAPPROF_H * CAPPROF_A];  // [h*CAPPROF_A + a] = max wall radius (world)
        float4 _CapProfInfo;                      // .x=H .y=A (diagnostic), .w=1 when fed

        // Angularly-wrapped radius lookup in row h, smoothstep-blended between bins (C1;
        // linear blending kinked the rim into a visible 16-gon at low fill). A convex
        float CapProfRadiusRow(float a01, int h)
        {
            float fa  = a01 * CAPPROF_A - 0.5;         // texel-centre convention
            float a0f = floor(fa);
            int   a0  = (int)a0f & (CAPPROF_A - 1);    // wrap (power of two)
            int   a1  = (a0 + 1) & (CAPPROF_A - 1);
            float wa  = smoothstep(0.0, 1.0, fa - a0f);
            return lerp(_CapProfR[h * CAPPROF_A + a0],
                        _CapProfR[h * CAPPROF_A + a1], wa);
        }
        // Profile ring (centre + radius) at world height y for a point at world xz.
        void CapProfSample(float2 xz, float y, out float2 c, out float R)
        {
            float u   = saturate((y - _Chamber1ExtentY.x) /
                                 max(_Chamber1ExtentY.y - _Chamber1ExtentY.x, 1e-5));
            float fh  = clamp(u * CAPPROF_H - 0.5, 0.0, CAPPROF_H - 1.0);
            int   h0  = (int)floor(fh);
            int   h1  = min(h0 + 1, CAPPROF_H - 1);
            // Same smoothstep across rows, and the same weight for centroid and radius -
            // the ring (c, R) must move as one object or the clamp would self-disagree.
            float wy  = smoothstep(0.0, 1.0, fh - (float)h0);
            c = lerp(_CapProfC[h0].xy, _CapProfC[h1].xy, wy);
            float2 d   = xz - c;
            float  a01 = atan2(d.y, d.x) * 0.15915494 + 0.5;   // 1/(2*pi)
            R = lerp(CapProfRadiusRow(a01, h0), CapProfRadiusRow(a01, h1), wy);
        }
        float _C2Bound1MinY_bottom, _C2Bound2MaxY_top, _C2Bound3MinX_left, _C2Bound4MaxX_right, _C2Bound5MinZ_back, _C2Bound6MaxZ_front;

        // Bounds box helpers, in REST units (match the baked rest-pos UV2). World scale
        float3 BoxMin()    { return float3(_Bound3MinX_left, _Bound1MinY_bottom, _Bound5MinZ_back); }
        float3 BoxMax()    { return float3(_Bound4MaxX_right, _Bound2MaxY_top, _Bound6MaxZ_front); }
        float3 BoxCenter() { return (BoxMin() + BoxMax()) * 0.5; }
        float3 BoxHalf()   { return max((BoxMax() - BoxMin()) * 0.5, 1e-4); }
        // Chamber 2 box (rest space).
        float3 Box2Min()    { return float3(_C2Bound3MinX_left, _C2Bound1MinY_bottom, _C2Bound5MinZ_back); }
        float3 Box2Max()    { return float3(_C2Bound4MaxX_right, _C2Bound2MaxY_top, _C2Bound6MaxZ_front); }
        float3 Box2Center() { return (Box2Min() + Box2Max()) * 0.5; }
        float3 Box2Half()   { return max((Box2Max() - Box2Min()) * 0.5, 1e-4); }
        // Chamber split bias: half the 3mm CUM_CHAMBER_GAP (build CUM_CHAMBER_GAP). A vert belongs to
        // the tube (chamber 2) when its REST-Y < _Bound1MinY_bottom - CUM_SPLIT_BIAS (the gap midpoint).
        // Keep in sync with the plugin's ChamberSplitBias and the build's CUM_CHAMBER_GAP/2.
        static const float CUM_SPLIT_BIAS = 0.0015;
        // Relative chamber volumes (rest-space box products) - weighting for the
        // connected-vessel solve (big womb gets more volume per level rise).
        float Box1Volume() { float3 d = BoxMax()  - BoxMin();  return max(d.x*d.y*d.z, 1e-9); }
        float Box2Volume() { float3 d = Box2Max() - Box2Min(); return max(d.x*d.y*d.z, 1e-9); }
        // Submerged fraction of a chamber at world-up height h within [ylo,yhi],
        // remapped by the volume model (cube = linear, ellipsoid = cubic cap).
        float SubmergedFrac(float ylo, float yhi, float Y)
        {
            float t = saturate((Y - ylo) / max(yhi - ylo, 1e-6));
            if (_VolumeConserve_0off_1cube_2ellipsoid < 1.5) return t;                       // off/cube: linear
            return t * t * (3.0 - 2.0 * t);                            // smoothstep ≈ cap volume curve
        }
        // Invert SubmergedFrac: find the surface offset in [lo,hi] whose submerged
        float SolveSurfaceOffset(float lo, float hi, float fillFrac)
        {
            // Exact endpoints: bisection only converges to ~lo/~hi; pin them so fill=0 reads truly
            // EMPTY and fill=1 truly FULL (no sub-pixel drift at the divider / bulb top).
            if (fillFrac <= 0.0) return lo;
            if (fillFrac >= 1.0) return hi;
            float a = lo, b = hi;
            [unroll] for (int it = 0; it < 18; it++)
            {
                float m = (a + b) * 0.5;
                if (SubmergedFrac(lo, hi, m) < fillFrac) a = m; else b = m;
            }
            return (a + b) * 0.5;
        }
        // Connected vessels: one shared surface across two boxes. Find the offset
        // where V1+V2 == fillFrac*(V1max+V2max) (each Vi weighted by its rest volume).
        float SolveConnected(float lo1, float hi1, float Vmax1,
                             float lo2, float hi2, float Vmax2, float fillFrac)
        {
            float target = fillFrac * (Vmax1 + Vmax2);
            float a = min(lo1, lo2), b = max(hi1, hi2);
            [unroll] for (int it = 0; it < 20; it++)
            {
                float m = (a + b) * 0.5;
                float v = Vmax1 * SubmergedFrac(lo1, hi1, m) + Vmax2 * SubmergedFrac(lo2, hi2, m);
                if (v < target) a = m; else b = m;
            }
            return (a + b) * 0.5;
        }
        // Wobble factors for a vertex/fragment in chamber `inCh2`. With _PerChamberWobble
        // on, chamber 2 uses the plugin's second rotation pair (_Rotation2X/Z) so the tube
        void ComputeWobble(out float wX, out float wZ, bool inCh2)
        {
            float t = _Time.y * _IdleWobbleSpeed;
            bool  ch2  = (_PerChamberWobble > 0.5) && inCh2;
            float rotX = ch2 ? _Rotation2X : _RotationX;
            float rotZ = ch2 ? _Rotation2Z : _RotationZ;
            wX = rotX + sin(t)               * _IdleWobbleStrength;
            wZ = rotZ + sin(t * 0.83 + 1.57) * _IdleWobbleStrength;
        }

        // ── Cap shape uniforms (shared by DepthPrime + Combined vert) ──
        float _CapSmooth, _CapTess;

        struct appdata_liq
        {
            float4 vertex  : POSITION;
            float2 uv      : TEXCOORD0;
            float3 normal  : NORMAL;
            float3 restPos : TEXCOORD2;   // rest-pos mode: baked rest mesh-local position (UV2 - NOT skinned)
        };

        struct v2f_liq
        {
            float4 pos         : SV_POSITION;
            float2 uv          : TEXCOORD0;
            float3 worldPos    : TEXCOORD1;
            float3 worldNormal : TEXCOORD2;
            float  fillSign    : TEXCOORD3;
            float3 localPos    : TEXCOORD4;
        };

        // Vertex -> Geometry. Carries everything v2f needs PLUS the raw (unsnapped)
        struct v2g_liq
        {
            float4 pos         : SV_POSITION;
            float2 uv          : TEXCOORD0;
            float3 worldPos    : TEXCOORD1;
            float3 worldNormal : TEXCOORD2;
            float  fillSign    : TEXCOORD3;
            float3 localPos    : TEXCOORD4;
            float3 restPos     : TEXCOORD5;   // rest mesh-local (uv2)
            float3 worldPosRaw : TEXCOORD6;   // UNSNAPPED skinned world (for GS reconstruction)
        };

        // ── GEOMETRY EDGE ROUNDING (closed mode) ───────────────────────────
        // Reff = arc band half-depth (world units; caller floors >= 1e-5).
        // Angle parameterization (theta=(1-t)*phi, bevel=R(1-cos theta)) keeps
        // fs = fs0 + bevel strictly increasing -> a unique transversal cut. The chord
        static const float EDGE_SWEEP   = 1.5707963; // pi/2 - constant quarter-round (cap-edge bevel sweep)
        // Chamber fill height in WORLD Y - ONE definition (closed-mode snap);
        // never re-derive this lerp elsewhere.
        float ChamberYfill(bool inCh2)
        {
            float2 e  = inCh2 ? _Chamber2ExtentY.xy : _Chamber1ExtentY.xy;
            float  hf = SolveSurfaceOffset(0.0, 1.0, inCh2 ? _FillAmount2 : _FillAmount);
            return lerp(e.x, e.y, saturate(hf));
        }
        void EdgeArc(float fs0, float Reff, float phi, out float bevel, out float theta)
        {
            float t = saturate((fs0 + Reff) / (2.0 * Reff));   // 0 at band bottom .. 1 interior
            theta   = (1.0 - t) * phi;                         // arc angle: phi at band bottom .. 0 interior
            bevel   = Reff * (1.0 - cos(theta));               // C1 into the flat interior
        }

        // Per-vertex cap snap (scalar - keeps the geometry shader free of loop-indexed
        // arrays, which the d3d11 compiler flags as 'potentially uninitialized'/X4580).
        void SnapVertGS(float3 rp, float3 wpIn, float3 nIn,
                        float3 up, float3 upRest, float snapScale, bool two,
                        float surf1, float surf2, float surfConn, float yFill1, float yFill2,
                        out float3 outP, out float3 outN, out float outFS, out bool outEmpty, out bool outBot)
        {
            bool tookBot = false;   // true when this vert becomes a chamber-2 BOTTOM cap (band feature; geom uses it)
            // Chamber membership by REST-Y vs the divider - POSE-INVARIANT (a world-up
            bool inCh2 = two && (rp.y < _Bound1MinY_bottom - CUM_SPLIT_BIAS);
            // Per-chamber wobble from this vertex's own chamber. _PerChamberWobble off
            // -> both chambers use the same (global) wobble.
            float wX, wZ; ComputeWobble(wX, wZ, inCh2);
            // ONE level referenced to chamber-1 centre; closed uses each chamber's own.
            float surfOff; float chamberFill; float3 boxC;
            if (_ChamberMode_0single_1connected_2closed < 0.5)      { surfOff = surf1;    chamberFill = _FillAmount;  boxC = BoxCenter(); }
            else if (_ChamberMode_0single_1connected_2closed < 1.5) { surfOff = surfConn; chamberFill = _FillAmount;  boxC = BoxCenter(); }
            else { if (inCh2) { surfOff = surf2; chamberFill = _FillAmount2; boxC = Box2Center(); }
                   else       { surfOff = surf1; chamberFill = _FillAmount;  boxC = BoxCenter(); } }
            float3 rel = rp - boxC;
            float relX = rel.x, relZ = rel.z;        // horizontal-ish (wobble footprint)
            float fillCoord = dot(rel, upRest);
            float fs = fillCoord - (surfOff + relX * wX + relZ * wZ);  // >0 above (cap)
            float3 wp = wpIn; float3 wn = nIn;
            bool empty = (chamberFill < 0.004);
            // ── Surface placement ──────────────────────────────────────────
            // WORLD-SPACE rounded cap. CLOSED (2, both chambers) AND CONNECTED (1, BULB only) place the fill
            // (inCh2) deliberately stays FLAT: it falls to the else, because the contact profile (_profWp)
            // only covers chamber 1 so the tube has no overhang guard, and a thin tube reads clean flat.
            if ((_ChamberMode_0single_1connected_2closed > 1.5                                                                        // CLOSED: any chamber
                 || (_ChamberMode_0single_1connected_2closed > 0.5 && _ChamberMode_0single_1connected_2closed < 1.5 && !inCh2))        // CONNECTED: bulb only (tube stays flat)
                && _Box2CenterWorld.w > 0.5 && _Box1CenterWorld.w > 0.5)
            {
                float2 _ext  = inCh2 ? _Chamber2ExtentY.xy : _Chamber1ExtentY.xy;
                // Closed: each chamber's own world-Y fill (ChamberYfill). Connected bulb: lift the SHARED level
                // surfConn (rest offset from the box centre along upRest) into world-Y at chamber-1's world centre.
                float  Yfill = (_ChamberMode_0single_1connected_2closed > 1.5)
                                 ? (inCh2 ? yFill2 : yFill1)          // hoisted ChamberYfill (solved once per patch)
                                 : (_Box1CenterWorld.y + surfConn * snapScale);
                float  Ydiv  = (_Chamber1ExtentY.x + _Chamber2ExtentY.y) * 0.5;
                // Edge rounding: constant quarter-round (EDGE_SWEEP) folded into the
                // classification (fs = fs0 + bevel) so the cut follows the rounded edge.
                float3 _bh    = inCh2 ? Box2Half() : BoxHalf();
                float  _wob   = (relX * wX + relZ * wZ) * snapScale;          // wobble tilt (world); no radial term
                float  fs0    = wpIn.y - (Yfill + _wob);                      // original height above the flat plane
                // Arc band half-depth (world): slider x chamber rest half-height x snapScale,
                float4 _abMin = inCh2 ? _Box2LocalMin : _Box1LocalMin;
                float4 _abMax = inCh2 ? _Box2LocalMax : _Box1LocalMax;
                float  _rXZ   = 0.5 * min(_abMax.x - _abMin.x, _abMax.z - _abMin.z);
                float  Reff0  = _EdgeRadius * _bh.y * snapScale;
                float  Reff   = (_abMax.w > 0.5) ? min(Reff0, _rXZ) : Reff0;
                // ROOM CAPS (uniforms only -> Reff stays per-chamber uniform): cap by half the
                // BOTTOM-CLEAR BAND (chamber 2 / canal only): empty the canal from the floor UP to Ybot, so the cum
                float Ybot = (inCh2) ? lerp(_ext.x, Yfill, saturate(_FillBottom2)) : _ext.x;        // floor .. top plane
                Reff = max(min(min(Reff, 0.5 * max(_ext.y - Yfill, 0.0)),
                                          0.5 * max(Yfill - Ybot, 0.0)), 1e-5);    // lower term = BAND depth (was full chamber)
                float  bevel; float thArc;
                EdgeArc(fs0, Reff, EDGE_SWEEP, bevel, thArc);
                if (_EdgeRadius <= 1e-5) { bevel = 0.0; thArc = 0.0; }       // slider 0 -> exactly flat (uniform branch)
                fs = fs0 + bevel;                 // monotonic (see EdgeArc) -> unique cut
                float fsBot = (inCh2 && _FillBottom2 > 1e-4) ? ((Ybot + _wob) - wpIn.y) : -1e30;     // >0 below the clear plane
                fs = max(fs, fsBot);              // outside the band: above the top OR below the bottom-clear plane
                if (!empty && fsBot > 0.0 && fsBot >= fs0 + bevel)
                {
                    // BOTTOM CAP: flat plane at Ybot, down-facing (wobble-tilted). Simple (no EdgeArc/profile) - the
                    // canal is a thin tube, so a flat closing disc reads clean. Geometry only; shading via wn.
                    wp = wpIn; wp.y = Ybot + _wob;
                    wn = normalize(float3(-wX, -1.0, -wZ));
                    tookBot = true;               // tag: this is a BOTTOM-cap vert (geom collapses band-spanning tris)
                }
                else if (!empty && fs > 0.0)
                {
                    wp = wpIn;
                    // Flat interior + edge roll-off; down-only placement, wall junction at fs=0.
                    wp.y = Yfill + _wob - bevel;
                    // CONTACT-PROFILE CLAMP (chamber 1/womb only): pull this cap vertex's world
                    if (!inCh2 && _CapProfInfo.w > 0.5)
                    {
                        float2 _pc; float _pR;
                        CapProfSample(wp.xz, wp.y, _pc, _pR);
                        float2 _pd = wp.xz - _pc;
                        float  _pl = length(_pd);
                        if (_pl > _pR) wp.xz = _pc + _pd * (_pR / _pl);   // _pl > _pR >= 0 -> no /0
                    }
                    // Cap SHADING normal - domed top that ROLLS to the bulb wall at the cut. DEBUG (v377)
                    float2 _rd     = rp.xz - boxC.xz;                        // horizontal radial (rest)
                    float  _R      = max(max(_bh.x, _bh.z), 1e-4);
                    float3 _domeUp = normalize(float3(-wX, 1.0, -wZ) + float3(_rd.x, 0.0, _rd.y) * (_CapDome / _R));
                    // Roll the rim from the domed top to the cap vertex's OWN input mesh normal (nIn). The rim
                    // verts are NEAR-CUT (small fs0 -> they were WALL verts just above the fill), so their nIn
                    wn = normalize(lerp(_domeUp, normalize(nIn + float3(0,1e-6,0)), saturate(thArc / EDGE_SWEEP)));
                }
                else if (_ChamberMode_0single_1connected_2closed > 1.5 && !empty && _NeckFloorBand > 1e-5)
                {
                    // CLOSED-only: clamp neck-band verts onto the chamber DIVIDER world plane so the cut cap
                    // discs can't hump through it. CONNECTED has ONE continuous level with no divider, so it
                    float3 b2     = Box2Center();
                    float  restAboveDiv = dot(rp - b2, upRest)
                                        - dot(float3(b2.x, _Bound1MinY_bottom, b2.z) - b2, upRest);
                    if (abs(restAboveDiv) < _NeckFloorBand)
                        wp.y = inCh2 ? min(wpIn.y, Ydiv)   // tube ceiling
                                     : max(wpIn.y, Ydiv);  // womb floor
                }
            }
            else
            {
                // REST-SPACE snap (single / connected / non-rest bottle): move each vertex
                if (!empty && fs > 0.0)
                {
                    wp -= up * (fs * snapScale);
                    if (_CapSmooth > 0.5)
                        wn = normalize(float3(-wX, 1.0, -wZ));   // flat up + wobble
                }
                // CLOSED-mode cervix flatten: the funnel band at the divider is raw mesh at any fill
                else if (_ChamberMode_0single_1connected_2closed > 1.5 && _NeckFloorBand > 1e-5 && !empty && !inCh2)
                {
                    float dOff = dot(float3(boxC.x, _Bound1MinY_bottom, boxC.z) - boxC, upRest);
                    float aboveDiv = fillCoord - dOff;            // >0 above divider (rest)
                    if (aboveDiv < _NeckFloorBand)
                        wp = wpIn - up * (aboveDiv * snapScale);  // womb floor onto the divider plane
                }
                else if (_ChamberMode_0single_1connected_2closed > 1.5 && _NeckFloorBand > 1e-5 && !empty && inCh2)
                {
                    float dOff2 = dot(float3(boxC.x, _C2Bound2MaxY_top, boxC.z) - boxC, upRest);
                    float belowDiv = dOff2 - fillCoord;           // >0 below divider (tube interior)
                    if (belowDiv < _NeckFloorBand)
                        wp = wpIn - up * ((fillCoord - dOff2) * snapScale); // tube ceiling onto divider plane
                }
            }
            outP = wp; outN = wn; outFS = fs; outEmpty = empty; outBot = tookBot;
        }

        // Cap-side cut-vertex normal - both triangles sharing a cut point lerp the same
        // per-vertex data to the same crossing, so they get identical normals (no rim facets).
        float3 CapCutNormal(float3 restPos, float3 nmIn)
        {
            // Return the edge-lerped INPUT mesh normal (nmIn) ~ the ACTUAL wall normal at the cut. For a
            return normalize(nmIn + float3(0.0, 1e-6, 0.0));
        }

        // VERTEX stage. No snapping here - the geometry shader does all of it (it
        // SMR). Just forward raw data: restPos (uv2 in rest mode, else object-local
        v2g_liq vert_liq(appdata_liq v)
        {
            v2g_liq o;
            o.uv          = v.uv;
            // Rest mode (skinned womb): baked rest pos from UV2. Non-rest (ordinary
            // MeshRenderer, e.g. bottle): the object-local vertex itself.
            o.restPos     = (_UseRestPosTangent > 0.5) ? v.restPos : v.vertex.xyz;
            o.worldPosRaw = mul(unity_ObjectToWorld, v.vertex).xyz;
            o.worldPos    = o.worldPosRaw;
            o.worldNormal = UnityObjectToWorldNormal(v.normal);
            o.pos         = UnityObjectToClipPos(v.vertex);
            o.localPos    = o.restPos;
            o.fillSign    = 0.0;
            return o;
        }

        // Build an output vertex from a world position + attributes. `nrm` rides the
        // worldNormal varying; the fragment shades directly from it (the mesh normal).
        v2f_liq BuildV(float3 wp, float3 rest, float2 uv, float3 nrm, float fs)
        {
            v2f_liq o;
            o.pos         = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
            o.uv          = uv;
            o.worldPos    = wp;
            o.worldNormal = nrm;
            o.fillSign    = fs;
            o.localPos    = rest;
            return o;
        }

        // ── Adaptive cap tessellation (approach D) ─────────────────────────
        // Optional SM5 tessellation inserted before the geometry snap. It subdivides
#if SHADER_TARGET >= 50
        // Signed distance above (>0) / below (<0) the fill surface in rest space -
        // (a small error just shifts the density band; it never affects the snap itself).
        // The THREE fill-surface bisection solves (surf1 = chamber-1 offset, surf2 = chamber-2,
        void SolveFillOffsets(out float surf1, out float surf2, out float surfConn)
        {
            bool rest = _UseRestPosTangent > 0.5;
            float3 upRest = rest
                ? ((dot(_RestWorldUp.xyz, _RestWorldUp.xyz) > 1e-6) ? normalize(_RestWorldUp.xyz) : float3(0,1,0))
                : normalize(mul((float3x3)unity_WorldToObject, float3(0,1,0)));
            bool two = _ChamberMode_0single_1connected_2closed > 0.5;
            float halfUp1 = dot(BoxHalf(),  abs(upRest));
            float halfUp2 = two ? dot(Box2Half(), abs(upRest)) : 0.0;
            surf1 = SolveSurfaceOffset(-halfUp1, halfUp1, _FillAmount);
            surf2 = two ? SolveSurfaceOffset(-halfUp2, halfUp2, _FillAmount2) : 0.0;
            float o2 = two ? dot(Box2Center() - BoxCenter(), upRest) : 0.0;
            surfConn = (_ChamberMode_0single_1connected_2closed > 0.5 &&
                        _ChamberMode_0single_1connected_2closed < 1.5)
                ? SolveConnected(-halfUp1, halfUp1, Box1Volume(), o2 - halfUp2, o2 + halfUp2, Box2Volume(), _FillAmount)
                : 0.0;
        }
        // Per-vertex classify against the already-solved fill surface - cheap arithmetic, no solve.
        // Signed distance above (>0) / below (<0) the fill surface in rest space; used only to
        // decide WHERE to tessellate (a small error just shifts the density band, never the snap).
        float ClassifyFillSign(float3 rp, float surf1, float surf2, float surfConn)
        {
            bool rest = _UseRestPosTangent > 0.5;
            float3 upRest = rest
                ? ((dot(_RestWorldUp.xyz, _RestWorldUp.xyz) > 1e-6) ? normalize(_RestWorldUp.xyz) : float3(0,1,0))
                : normalize(mul((float3x3)unity_WorldToObject, float3(0,1,0)));
            bool two = _ChamberMode_0single_1connected_2closed > 0.5;
            bool inCh2 = two && (rp.y < _Bound1MinY_bottom - CUM_SPLIT_BIAS);   // pose-invariant rest-Y (matches SnapVertGS)
            float wX, wZ; ComputeWobble(wX, wZ, inCh2);   // per-chamber wobble (this vert's chamber)
            float surfOff; float3 boxC;
            if (_ChamberMode_0single_1connected_2closed < 0.5)      { surfOff = surf1;    boxC = BoxCenter(); }
            else if (_ChamberMode_0single_1connected_2closed < 1.5) { surfOff = surfConn; boxC = BoxCenter(); }
            else { if (inCh2) { surfOff = surf2; boxC = Box2Center(); } else { surfOff = surf1; boxC = BoxCenter(); } }
            float3 rel = rp - boxC;
            return dot(rel, upRest) - (surfOff + rel.x * wX + rel.z * wZ);
        }
        // Thin wrapper (solve + classify) - the single-arg form the Combined-frag debug marker
        // still calls; bit-identical to the pre-split EvalFillSign.
        float EvalFillSign(float3 rp)
        {
            float s1, s2, sc; SolveFillOffsets(s1, s2, sc);
            return ClassifyFillSign(rp, s1, s2, sc);
        }

        struct LiqTessF { float edge[3] : SV_TessFactor; float inside : SV_InsideTessFactor; };

        LiqTessF hsconst_liq(InputPatch<v2g_liq,3> ip)
        {
            LiqTessF f;
            float t = max(_CapTess, 1.0);
            // Surface band: tessellate cap + rim. In closed mode the band is widened by the
            float s1, s2, sc; SolveFillOffsets(s1, s2, sc);
            float fmax = max(ClassifyFillSign(ip[0].restPos, s1, s2, sc),
                         max(ClassifyFillSign(ip[1].restPos, s1, s2, sc), ClassifyFillSign(ip[2].restPos, s1, s2, sc)));
            float band = 0.02 + ((_ChamberMode_0single_1connected_2closed > 1.5)
                                 ? _EdgeRadius * max(BoxHalf().y, Box2Half().y) : 0.0);
            float fac = (fmax > -band) ? t : 1.0;
            f.edge[0] = fac; f.edge[1] = fac; f.edge[2] = fac; f.inside = fac;
            return f;
        }

        [domain("tri")]
        [partitioning("integer")]
        [outputtopology("triangle_cw")]
        [outputcontrolpoints(3)]
        [patchconstantfunc("hsconst_liq")]
        v2g_liq hull_liq(InputPatch<v2g_liq,3> ip, uint id : SV_OutputControlPointID)
        {
            return ip[id];
        }

        [domain("tri")]
        v2g_liq domain_liq(LiqTessF f, OutputPatch<v2g_liq,3> op, float3 bary : SV_DomainLocation)
        {
            v2g_liq o;
            o.pos         = float4(0,0,0,1);   // GS recomputes clip pos; this is unused
            o.uv          = op[0].uv*bary.x          + op[1].uv*bary.y          + op[2].uv*bary.z;
            o.worldPos    = op[0].worldPos*bary.x    + op[1].worldPos*bary.y    + op[2].worldPos*bary.z;
            o.worldNormal = normalize(op[0].worldNormal*bary.x + op[1].worldNormal*bary.y + op[2].worldNormal*bary.z);
            o.fillSign    = 0.0;
            o.localPos    = op[0].localPos*bary.x    + op[1].localPos*bary.y    + op[2].localPos*bary.z;
            o.restPos     = op[0].restPos*bary.x     + op[1].restPos*bary.y     + op[2].restPos*bary.z;
            o.worldPosRaw = op[0].worldPosRaw*bary.x + op[1].worldPosRaw*bary.y + op[2].worldPosRaw*bary.z;
            return o;
        }
#endif

        // GEOMETRY stage - does the surface snap. Works entirely in the mesh's REST
        // _ChamberMode_0single_1connected_2closed: 0 single, 1 connected (shared level,
        // volume flows), 2 closed (two independent levels).
        [maxvertexcount(9)]
        void geom_liq(triangle v2g_liq input[3], inout TriangleStream<v2f_liq> stream)
        {
            bool rest = _UseRestPosTangent > 0.5;

            // PLUGIN GATE (rest mode): the CPU-skinned womb REQUIRES LiquidWobbleMPB, which
            // writes _ObjectScaleVec (live lossyScale) every frame. No plugin -> it stays 0
            if (rest && _ObjectScaleVec.y <= 0.001) return;

            // RAW DEBUG (mode 2 only): emit the cum mesh UNTOUCHED (no fill snap / cut / collapse) so the
            // (Modes 3-5 = normal/facet viz run on the FINAL snapped+cut geometry, so they fall through.)
            if (_DebugLiquidCap > 1.5 && _DebugLiquidCap < 2.5)
            {
                stream.Append(BuildV(input[0].worldPosRaw, input[0].restPos, input[0].uv, input[0].worldNormal, -1.0));
                stream.Append(BuildV(input[1].worldPosRaw, input[1].restPos, input[1].uv, input[1].worldNormal, -1.0));
                stream.Append(BuildV(input[2].worldPosRaw, input[2].restPos, input[2].uv, input[2].worldNormal, -1.0));
                stream.RestartStrip();
                return;
            }

            // FAIL-HARD (rest + closed mode): the womb fill REQUIRES the plugin's per-chamber
            if (rest && _ChamberMode_0single_1connected_2closed > 1.5 &&
                (_Box1CenterWorld.w <= 0.5 || _Box2CenterWorld.w <= 0.5 ||
                 _Chamber1ExtentY.w <= 0.5 || _Chamber2ExtentY.w <= 0.5 ||
                 _CapProfInfo.w <= 0.5))
                return;

            // (Wobble is now computed PER-CHAMBER inside SnapVertGS from each vertex's own chamber, so

            // Fill math runs in the box's local space (uniforms only ⇒ identical for
            float3 up = float3(0,1,0);
            float3 upRest; float snapScale;
            if (rest)
            {
                upRest = (dot(_RestWorldUp.xyz, _RestWorldUp.xyz) > 1e-6)
                         ? normalize(_RestWorldUp.xyz) : float3(0,1,0);
                snapScale = length(_ObjectScaleVec.xyz * upRest);
            }
            else
            {
                upRest = normalize(mul((float3x3)unity_WorldToObject, float3(0,1,0)));
                snapScale = length(mul((float3x3)unity_ObjectToWorld, upRest));
            }

            // ── Per-triangle local rest->world scale (fixes the collapse teeth + world-shimmer) ──
            float fillScale = snapScale;
            if (_PerTriSnap > 0.5)
            {
                float d01 = dot(input[0].restPos - input[1].restPos, upRest);
                float d12 = dot(input[1].restPos - input[2].restPos, upRest);
                float d20 = dot(input[2].restPos - input[0].restPos, upRest);
                float ad01 = abs(d01), ad12 = abs(d12), ad20 = abs(d20);
                if (max(ad01, max(ad12, ad20)) > 1e-5)
                {
                    if (ad01 >= ad12 && ad01 >= ad20) fillScale = (input[0].worldPosRaw.y - input[1].worldPosRaw.y) / d01;
                    else if (ad12 >= ad20)            fillScale = (input[1].worldPosRaw.y - input[2].worldPosRaw.y) / d12;
                    else                              fillScale = (input[2].worldPosRaw.y - input[0].worldPosRaw.y) / d20;
                }
                // Guard degenerate/inverted/NaN ratios (horizontal cut discs, flipped poses):
                fillScale = (fillScale > 1e-4) ? fillScale : snapScale;
            }

            bool two = _ChamberMode_0single_1connected_2closed > 0.5;
            float Vmax1 = Box1Volume();
            float Vmax2 = two ? Box2Volume() : 0.0;
            // Fill surface OFFSETS measured from each box centre ALONG upRest (the box's
            float halfUp1 = dot(BoxHalf(),  abs(upRest));
            float halfUp2 = two ? dot(Box2Half(), abs(upRest)) : 0.0;
            float surf1 = SolveSurfaceOffset(-halfUp1, halfUp1, _FillAmount);
            float surf2 = two ? SolveSurfaceOffset(-halfUp2, halfUp2, _FillAmount2) : 0.0;
            float o2 = two ? dot(Box2Center() - BoxCenter(), upRest) : 0.0;
            float surfConn = (_ChamberMode_0single_1connected_2closed > 0.5 &&
                              _ChamberMode_0single_1connected_2closed < 1.5)
                ? SolveConnected(-halfUp1, halfUp1, Vmax1, o2 - halfUp2, o2 + halfUp2, Vmax2, _FillAmount)
                : 0.0;
            if (_VolumeShape > 0.5 &&
                _ChamberMode_0single_1connected_2closed > 0.5 && _ChamberMode_0single_1connected_2closed < 1.5 &&
                _Box1CenterWorld.w > 0.5)
            {
                surfConn = (_ShapeYconn - _Box1CenterWorld.y) / max(fillScale, 1e-5);
            }
            // Closed-mode world-Y fill per chamber - patch-uniform given the chamber, so solve the
            // ChamberYfill [0,1] bisections once here (was recomputed per vertex inside SnapVertGS).
            float yFill1 = ChamberYfill(false);
            float yFill2 = two ? ChamberYfill(true) : yFill1;
            if (_VolumeShape > 0.5)
            {
                yFill1 = _ShapeYfill1;
                yFill2 = two ? _ShapeYfill2 : yFill1;
            }
            // (Closed mode is always world-clip now - gated in SnapVertGS on ChamberMode>1.5 + the

            // Phase 1: per-vertex snap (scalar - no loop-indexed arrays -> avoids the
            // d3d11 X4580 'potentially uninitialized' false-positive).
            float3 P0,P1,P2,N0,N1,N2; float F0,F1,F2; bool E0,E1,E2; bool B0,B1,B2;
            SnapVertGS(input[0].restPos, input[0].worldPosRaw, input[0].worldNormal,
                       up, upRest, fillScale, two, surf1, surf2, surfConn, yFill1, yFill2, P0,N0,F0,E0,B0);
            SnapVertGS(input[1].restPos, input[1].worldPosRaw, input[1].worldNormal,
                       up, upRest, fillScale, two, surf1, surf2, surfConn, yFill1, yFill2, P1,N1,F1,E1,B1);
            SnapVertGS(input[2].restPos, input[2].worldPosRaw, input[2].worldNormal,
                       up, upRest, fillScale, two, surf1, surf2, surfConn, yFill1, yFill2, P2,N2,F2,E2,B2);
            int nEmpty = (E0 ? 1 : 0) + (E1 ? 1 : 0) + (E2 ? 1 : 0);

            // Empty-chamber handling (no discard, rounding-safe):
            // - all 3 empty -> collapse to vertex 0 -> zero area -> invisible. SCALE- and
            if (nEmpty > 0)
            {
                if (nEmpty == 3) { P0 = input[0].worldPosRaw; P1 = P0; P2 = P0; }
                else
                {
                    float3 fc = float3(0,0,0); float nf = 0.0;
                    if (!E0) { fc += P0; nf += 1.0; }
                    if (!E1) { fc += P1; nf += 1.0; }
                    if (!E2) { fc += P2; nf += 1.0; }
                    fc /= max(nf, 1.0);
                    if (E0) { P0 = fc; F0 = 0.0; }
                    if (E1) { P1 = fc; F1 = 0.0; }
                    if (E2) { P2 = fc; F2 = 0.0; }
                }
            }

            // CLOSED mode (mode 2): the cum shell is CONTINUOUS across the neck, but the
            bool collapsed = (nEmpty > 0);
            if (_ChamberMode_0single_1connected_2closed > 1.5)
            {
                // Classify each vertex by its REST-Y vs the divider - POSE-INVARIANT. Projecting
                float  _splitY = _Bound1MinY_bottom - CUM_SPLIT_BIAS;
                float  d0 = input[0].restPos.y - _splitY;
                float  d1 = input[1].restPos.y - _splitY;
                float  d2 = input[2].restPos.y - _splitY;
                bool span = (min(d0, min(d1, d2)) < 0.0) && (max(d0, max(d1, d2)) > 0.0);
                // Collapse the divider-crossing band to a point (invisible).
                if (span) { P1 = P0; P2 = P0; collapsed = true; }
            }

            // BAND-SPAN collapse (chamber-2 bottom-clear band): a triangle that spans the whole band - one vertex a
            // TOP cap (F>0, !B) and another a BOTTOM cap (F>0, B) - would bridge the top surface to the bottom cap with
            // a solid sheet through the hollow band (the k=sum(F>0) cut can't tell the two planes apart, and the
            {
                bool hasTop = (F0 > 0.0 && !B0) || (F1 > 0.0 && !B1) || (F2 > 0.0 && !B2);
                bool hasBot = (F0 > 0.0 &&  B0) || (F1 > 0.0 &&  B1) || (F2 > 0.0 &&  B2);
                if (hasTop && hasBot) { P1 = P0; P2 = P0; collapsed = true; }
            }

            // ── Emit (with fill-plane cut) ─────────────────────────────────
            // Classify the triangle against the fill line. Fully-wall (k==0) and
            // fully-cap (k==3) emit as-is. A STRADDLING triangle is CUT exactly on the
            int k = (F0 > 0.0 ? 1 : 0) + (F1 > 0.0 ? 1 : 0) + (F2 > 0.0 ? 1 : 0);

            if (collapsed || k == 0 || k == 3)
            {
                if (_CapForOpenMesh > 0.5 && k == 3 && !collapsed) return;   // open-mesh: drop the flat upper cap (bottle mouth); the rim fan rebuilds a closed surface
                stream.Append(BuildV(P0, input[0].restPos, input[0].uv, N0, F0));
                stream.Append(BuildV(P1, input[1].restPos, input[1].uv, N1, F1));
                stream.Append(BuildV(P2, input[2].restPos, input[2].uv, N2, F2));
                stream.RestartStrip();
                return;
            }

            // Straddling: the odd-signed vertex is the apex; (a,b) are the other two in
            // winding order. Cyclic rotation (0,1,2)->(1,2,0)->(2,0,1) preserves winding.
            bool s0 = F0 > 0.0, s1 = F1 > 0.0, s2 = F2 > 0.0;
            int odd = (s1 == s2 && s0 != s1) ? 0 : ((s0 == s2 && s1 != s0) ? 1 : 2);

            float3 apexP, apexRaw, apexRest, apexNm, apexNmI; float2 apexUV; float apexF;
            float3 aP, aRaw, aRest, aNm, aNmI; float2 aUV; float aF;
            float3 bP, bRaw, bRest, bNm, bNmI; float2 bUV; float bF;
            if (odd == 0)
            {
                apexP=P0; apexRaw=input[0].worldPosRaw; apexRest=input[0].restPos; apexNm=N0; apexNmI=input[0].worldNormal; apexUV=input[0].uv; apexF=F0;
                aP=P1; aRaw=input[1].worldPosRaw; aRest=input[1].restPos; aNm=N1; aNmI=input[1].worldNormal; aUV=input[1].uv; aF=F1;
                bP=P2; bRaw=input[2].worldPosRaw; bRest=input[2].restPos; bNm=N2; bNmI=input[2].worldNormal; bUV=input[2].uv; bF=F2;
            }
            else if (odd == 1)
            {
                apexP=P1; apexRaw=input[1].worldPosRaw; apexRest=input[1].restPos; apexNm=N1; apexNmI=input[1].worldNormal; apexUV=input[1].uv; apexF=F1;
                aP=P2; aRaw=input[2].worldPosRaw; aRest=input[2].restPos; aNm=N2; aNmI=input[2].worldNormal; aUV=input[2].uv; aF=F2;
                bP=P0; bRaw=input[0].worldPosRaw; bRest=input[0].restPos; bNm=N0; bNmI=input[0].worldNormal; bUV=input[0].uv; bF=F0;
            }
            else
            {
                apexP=P2; apexRaw=input[2].worldPosRaw; apexRest=input[2].restPos; apexNm=N2; apexNmI=input[2].worldNormal; apexUV=input[2].uv; apexF=F2;
                aP=P0; aRaw=input[0].worldPosRaw; aRest=input[0].restPos; aNm=N0; aNmI=input[0].worldNormal; aUV=input[0].uv; aF=F0;
                bP=P1; bRaw=input[1].worldPosRaw; bRest=input[1].restPos; bNm=N1; bNmI=input[1].worldNormal; bUV=input[1].uv; bF=F1;
            }

            // Fill-line crossings on the two apex edges (opposite signs ⇒ nonzero
            // denominators). The cut point sits on the UNSNAPPED surface (fs≈0 ⇒ no
            float ta = saturate(apexF / (apexF - aF));
            float tb = saturate(apexF / (apexF - bF));
            float3 XaPos = lerp(apexRaw, aRaw, ta);  float3 XaRest = lerp(apexRest, aRest, ta);
            float2 XaUV  = lerp(apexUV, aUV, ta);
            float3 XbPos = lerp(apexRaw, bRaw, tb);  float3 XbRest = lerp(apexRest, bRest, tb);
            float2 XbUV  = lerp(apexUV, bUV, tb);
            // Cut-point normals. Under MESH-NORMAL (goo2) shading the cut must be SMOOTH, so both the cap-side
            float3 XaNmI     = lerp(apexNmI, aNmI, ta);   // edge-lerped INPUT mesh normal (kept for the signature)
            float3 XbNmI     = lerp(apexNmI, bNmI, tb);
            float3 XaNm_cap  = CapCutNormal(XaRest, XaNmI);
            float3 XbNm_cap  = CapCutNormal(XbRest, XbNmI);
            float3 XaNm_wall = XaNm_cap;   // wall side = cap side (same ellipsoid) -> no down/outward red lip
            float3 XbNm_wall = XbNm_cap;

            const float CAP_FS = 1e-3;               // cut-vertex fillSign on the cap side
            const float WALL_FS = -1e-3;             // ... and the wall side

            // Both modes use the same smooth COLLAPSE cap below (the mesh's own cap triangulation
            bool openMesh = _CapForOpenMesh > 0.5;   // bottle mode: replace per-tri cap with a centre fan (below)
            if (apexF > 0.0)
            {
                // Cap triangle (apex + the two cut points) - cap-side normals (fragment overrides anyway).
                if (!openMesh)
                {
                    stream.Append(BuildV(apexP, apexRest, apexUV, apexNm,   apexF));
                    stream.Append(BuildV(XaPos, XaRest, XaUV, XaNm_cap,     CAP_FS));
                    stream.Append(BuildV(XbPos, XbRest, XbUV, XbNm_cap,     CAP_FS));
                    stream.RestartStrip();
                }
                // Wall quad (a, b, Xb, Xa) as two triangles - WALL-side cut normals so it shades as wall.
                stream.Append(BuildV(aP, aRest, aUV, aNm, aF));
                stream.Append(BuildV(bP, bRest, bUV, bNm, bF));
                stream.Append(BuildV(XbPos, XbRest, XbUV, XbNm_wall,    WALL_FS));
                stream.RestartStrip();
                stream.Append(BuildV(aP, aRest, aUV, aNm, aF));
                stream.Append(BuildV(XbPos, XbRest, XbUV, XbNm_wall,    WALL_FS));
                stream.Append(BuildV(XaPos, XaRest, XaUV, XaNm_wall,    WALL_FS));
                stream.RestartStrip();
            }
            else
            {
                // Wall triangle (apex + the two cut points) - WALL-side cut normals (= apexNm) so it shades as wall.
                stream.Append(BuildV(apexP, apexRest, apexUV, apexNm,   apexF));
                stream.Append(BuildV(XaPos, XaRest, XaUV, XaNm_wall,    WALL_FS));
                stream.Append(BuildV(XbPos, XbRest, XbUV, XbNm_wall,    WALL_FS));
                stream.RestartStrip();
                // Cap quad (a, b, Xb, Xa) as two triangles - cap-side normals (fragment overrides anyway).
                if (!openMesh)
                {
                    stream.Append(BuildV(aP, aRest, aUV, aNm, aF));
                    stream.Append(BuildV(bP, bRest, bUV, bNm, bF));
                    stream.Append(BuildV(XbPos, XbRest, XbUV, XbNm_cap,     CAP_FS));
                    stream.RestartStrip();
                    stream.Append(BuildV(aP, aRest, aUV, aNm, aF));
                    stream.Append(BuildV(XbPos, XbRest, XbUV, XbNm_cap,     CAP_FS));
                    stream.Append(BuildV(XaPos, XaRest, XaUV, XaNm_cap,     CAP_FS));
                    stream.RestartStrip();
                }
            }
            // OPEN-MESH surface: replace the per-triangle cap with a FAN from the chamber centre to this
            if (openMesh)
            {
                // Fan apex = the chamber CENTRE on the fill surface. It must be ONE shared point for every
                float3 boxCW  = mul(unity_ObjectToWorld, float4(BoxCenter(), 1.0)).xyz;
                float  capOff = (_ChamberMode_0single_1connected_2closed < 0.5) ? surf1
                              : (_ChamberMode_0single_1connected_2closed < 1.5) ? surfConn : surf1;
                float3 capCtr = float3(boxCW.x, boxCW.y + capOff * snapScale, boxCW.z);
                float3 cRest  = BoxCenter() + capOff * upRest;            // rest-space match of capCtr (shared, not per-tri)
                float2 cUV    = 0.5 * (XaUV + XbUV);
                stream.Append(BuildV(capCtr, cRest,  cUV,  float3(0,1,0), CAP_FS));
                stream.Append(BuildV(XaPos,  XaRest, XaUV, float3(0,1,0), CAP_FS));
                stream.Append(BuildV(XbPos,  XbRest, XbUV, float3(0,1,0), CAP_FS));
                stream.RestartStrip();
                stream.Append(BuildV(capCtr, cRest,  cUV,  float3(0,1,0), CAP_FS));
                stream.Append(BuildV(XbPos,  XbRest, XbUV, float3(0,1,0), CAP_FS));
                stream.Append(BuildV(XaPos,  XaRest, XaUV, float3(0,1,0), CAP_FS));
                stream.RestartStrip();
            }
        }
        ENDCG

        // ════════════════════════════════════════════════════════════════
        // Pass 1a: Mark - set liquid bit 6 over the whole cum footprint. DepthPrime
        // + Combined then draw only where bit 6 is set and CLEAR it after the first
        // _StencilInner_BodyPlus66) were removed - they existed only to hide a cap
        Pass
        {
            Name "Mark"
            ColorMask 0
            ZWrite Off
            Cull   Off

            // Mark bit 6 (Ref & WriteMask 64 = 64) over the cum footprint, gated by [_CumStencilRef]/[_CumStencilReadMask].
            // Default Ref=64/ReadMask=0 => Comp Equal (64&0)==(buf&0) => 0==0 => passes everywhere (= old Comp Always).
            // Plane-gated: Ref=192/ReadMask=128 => marks bit6 only where the x-ray plane set bit7.
            Stencil
            {
                Ref      [_CumStencilRef]
                ReadMask [_CumStencilReadMask]
                WriteMask 64
                Comp Equal
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex   vertPrep
            #pragma fragment fragPrep
            struct appdata  { float4 vertex : POSITION; };
            struct v2fPrep  { float4 pos : SV_POSITION; };
            v2fPrep vertPrep(appdata v)
            {
                v2fPrep o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }
            fixed4 fragPrep(v2fPrep i) : SV_Target { return 0; }
            ENDCG
        }

        // ════════════════════════════════════════════════════════════════
        // Pass 1c: DepthPrime - pre-write the CLOSEST cum-mesh depth (no color)
        // for every pixel the liquid will draw on (stencil bit 6 set). The
        Pass
        {
            Name "DepthPrime"
            Cull   Back
            ZWrite On
            ZTest  LEqual
            Offset [_DepthOffset], [_DepthOffset]
            ColorMask 0

            Stencil
            {
                Ref  64
                ReadMask 64
                Comp Equal
                Pass Keep
            }

            CGPROGRAM
            #pragma target   5.0
            #pragma vertex   vert_liq
            #pragma hull     hull_liq
            #pragma domain   domain_liq
            #pragma geometry geom_liq
            #pragma fragment frag
            fixed4 frag(v2f_liq i) : SV_Target { return 0; }
            ENDCG
        }

        // ════════════════════════════════════════════════════════════════
        // Pass 2: Combined cap + wall
        Pass
        {
            Name "Combined"
            Cull  Back
            ZWrite Off
            Offset [_DepthOffset], [_DepthOffset]
            Blend SrcAlpha OneMinusSrcAlpha

            // Paint-once: render the first fragment at each pixel with bit 6 set, then CLEAR
            // bit 6 (Pass Zero). Further fragments there fail -> no stacked transparent overdraw
            // (no doubled alpha/fresnel when _Color.a < 1). The Mark pass re-sets bit 6 each
            // frame. Trade-off: overlapping front layers don't show through each other.
            Stencil
            {
                Ref  64
                ReadMask  64
                WriteMask 64
                Comp Equal
                Pass Zero
            }

            CGPROGRAM
            // Uses the shared vert_liq / geom_liq / v2f_liq / EdgeArc / CapNormalAt
            // declared in the SubShader's CGINCLUDE (also used by DepthPrime).
            #pragma target   5.0
            #pragma vertex   vert_liq
            #pragma hull     hull_liq
            #pragma domain   domain_liq
            #pragma geometry geom_liq
            #pragma fragment frag

            sampler2D _MainTex, _Matcap;
            float4    _MainTex_ST;
            float4    _Color;
            float     _MatcapAlpha, _Sh;
            float     _FresnelPower, _FresnelScale, _FresnelBias, _FresnelAlpha;
            float4    _EmissionColor;
            float     _EmissionStrength;

            // (appdata_liq / v2f_liq / EdgeArc / CapNormalAt / vert_liq

            // Goo color blend.
            float3 GooColor(float3 base, float Fresnel, float3 matcap)
            {
                float3 src  = (base + Fresnel.xxx) - (1.0 - Fresnel) * _Sh;
                float3 dest = saturate(matcap * _MatcapAlpha);
                return saturate(src + dest) + _EmissionColor.rgb * _EmissionStrength;
            }

            fixed4 frag(v2f_liq i) : SV_Target
            {
                // ── DEBUG modes 3-5 (normal viz, on the FINAL snapped+cut geometry) ─────────
                // 3 = SHADING normal RGB (i.worldNormal - what fresnel/matcap light with).
                // 4 = FACET normal RGB (screen-space derivative of worldPos): every planar facet
                // 5 = MISMATCH heat: blue = shading normal agrees with the facet, red = deviates
                if (_DebugLiquidCap > 4.5)
                {
                    float3 nS = normalize(i.worldNormal);
                    float3 nF = normalize(cross(ddy(i.worldPos), ddx(i.worldPos)));
                    nF *= sign(dot(nF, nS) + 1e-6);            // same hemisphere as shading normal
                    float mis = saturate(length(nF - nS));     // 0 agree .. ~2 opposite
                    return fixed4(mis, mis * 0.25, 1.0 - mis, 1.0);
                }
                if (_DebugLiquidCap > 3.5)
                {
                    float3 nF = normalize(cross(ddy(i.worldPos), ddx(i.worldPos)));
                    float3 Vd = normalize(_WorldSpaceCameraPos.xyz - i.worldPos);
                    nF *= sign(dot(nF, Vd) + 1e-6);            // camera-facing so colours are stable
                    return fixed4(nF * 0.5 + 0.5, 1.0);
                }
                if (_DebugLiquidCap > 2.5)
                    return fixed4(normalize(i.worldNormal) * 0.5 + 0.5, 1.0);
                // ── DEBUG (DebugLiquidCap=1): rest-space ground-truth rings, best read in RAW
                // mode (=2, mesh shown un-snapped so the lines sit on true geometry). dim RED =
                // womb, dim GREEN = canal; the filled cap (fillSign>0) brightens. MAGENTA = neck/
                // divider, CYAN = box top (fill=1), YELLOW = box bottom (fill=0), WHITE = the
                if (_DebugLiquidCap > 0.5)
                {
                    bool dbgTube = i.localPos.y < _Bound1MinY_bottom - CUM_SPLIT_BIAS; // gap-midpoint split (matches render)
                    float restY  = i.localPos.y;
                    float div    = _Bound1MinY_bottom;
                    float topY   = dbgTube ? _C2Bound2MaxY_top   : _Bound2MaxY_top;     // box ceiling (fill=1)
                    float botY   = dbgTube ? _C2Bound1MinY_bottom : _Bound1MinY_bottom; // box floor (fill=0)
                    float3 dc = dbgTube ? float3(0.0, 0.42, 0.0) : float3(0.42, 0.0, 0.0);
                    if (i.fillSign > 0.0) dc = lerp(dc, float3(0.85, 0.85, 0.85), 0.5); // actual filled cap (grey)
                    if (abs(restY - div)  < 0.0013)                 return fixed4(1.0, 0.0, 1.0, 1.0); // NECK
                    if (abs(restY - topY) < 0.0013)                 return fixed4(0.0, 1.0, 1.0, 1.0); // BOX TOP
                    if (abs(restY - botY) < 0.0013)                 return fixed4(1.0, 1.0, 0.0, 1.0); // BOX BOTTOM
                    if (abs(EvalFillSign(i.localPos)) < 0.0018)     return fixed4(1.0, 1.0, 1.0, 1.0); // FILL SURFACE
                    return fixed4(dc, 1.0);
                }
                // ── Goo fresnel ───────────────────────────────────────────
                float3 V = normalize(_WorldSpaceCameraPos.xyz - i.worldPos);

                // SHADING NORMAL = the MESH normal (i.worldNormal): the GS fills it with the wall surface
                float3 N        = normalize(i.worldNormal);

                // Fresnel drives the colour and a uniform smooth alpha rim (driven by the smooth N,
                // so no seam teeth; small weight so an up-facing cap can't grazing-spike opaque).
                float  ndotv = dot(N, V);
                float  fresnelNode = _FresnelBias
                                   + _FresnelScale * pow(1.0 - ndotv, max(_FresnelPower, 0.001));
                float  Fresnel      = saturate(_FresnelAlpha * fresnelNode);
                float  alphaFresnel = Fresnel * 0.10;

                // ── Matcap: NORMAL-based reflect (stable under ZOOM). Was position-based, which slid
                float3 viewNormal = normalize(mul((float3x3)UNITY_MATRIX_V, N));
                float3 r          = reflect(float3(0.0, 0.0, -1.0), viewNormal);
                float  m          = sqrt(r.x*r.x + r.y*r.y + (r.z + 1.0)*(r.z + 1.0)) * 2.0;
                float2 mcUV       = saturate(r.xy / max(m, 1e-4) + 0.5);   // guard /0 at grazing angles
                float3 matcap     = tex2D(_Matcap, mcUV).rgb;

                // ── Bounds box (physics only - no clipping) ───────────────
                bool onPlane = false;
                if (_ShowSetupPhysicsBounds > 0.5)
                {
                    float3 bmn = BoxMin(); float3 bmx = BoxMax();
                    float3 h = BoxHalf();
                    float lineW = max(min(min(h.x, h.y), h.z) * 0.02, 1e-4);
                    onPlane = abs(i.localPos.x - bmn.x) < lineW
                           || abs(i.localPos.x - bmx.x) < lineW
                           || abs(i.localPos.y - bmn.y) < lineW
                           || abs(i.localPos.y - bmx.y) < lineW
                           || abs(i.localPos.z - bmn.z) < lineW
                           || abs(i.localPos.z - bmx.z) < lineW;
                }

                // ── CAP + WALL share the same colour & alpha ──────────────
                fixed4 baseCol = tex2D(_MainTex, i.uv * _MainTex_ST.xy + _MainTex_ST.zw) * _Color;
                float3 rgb     = GooColor(baseCol.rgb, Fresnel, matcap);   // identical law to the wall (full Fresnel+matcap)
                if (onPlane) rgb = float3(1, 1, 0);       // bright yellow plane line
                return float4(rgb, onPlane ? 1.0 : saturate(_Color.a + alphaFresnel));
            }
            ENDCG
        }

        // ════════════════════════════════════════════════════════════════
        // BoundsOverlay - when _ShowSetupPhysicsBounds is on, draw the 12 wireframe edges
        Pass
        {
            Name "BoundsOverlay"
            Cull   Off
            ZTest  Always
            ZWrite Off

            CGPROGRAM
            #pragma target 4.0
            #pragma vertex   vert_ov
            #pragma geometry geom_ov
            #pragma fragment frag_ov

            struct app_ov { float4 vertex : POSITION; float3 restPos : TEXCOORD2; };
            struct v2g_ov { float4 pos : SV_POSITION; float3 rest : TEXCOORD0; float3 wpos : TEXCOORD1; };
            struct g2f_ov { float4 pos : SV_POSITION; };

            v2g_ov vert_ov(app_ov v)
            {
                v2g_ov o;
                o.pos  = UnityObjectToClipPos(v.vertex);   // GS uses primID, this keeps the input valid
                o.rest = v.restPos;                    // rest mesh-local (rest-pos mode)
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;  // skinned world (identity O2W on SMR)
                return o;
            }

            // Box-corner clip helpers. REST (womb): the corner is already a WORLD point (the plugin's
            float4 ClipWorld(float3 w) { return mul(UNITY_MATRIX_VP, float4(w, 1.0)); }
            float4 ClipObj  (float3 c) { return UnityObjectToClipPos(float4(c, 1.0)); }

            // Emit a thin screen-space quad between two CLIP-space points.
            void EmitEdgeClip(float4 p0, float4 p1, inout TriangleStream<g2f_ov> stream)
            {
                float2 ndc0 = p0.xy / max(abs(p0.w), 1e-5) * sign(p0.w);
                float2 ndc1 = p1.xy / max(abs(p1.w), 1e-5) * sign(p1.w);
                float2 d    = ndc1 - ndc0;
                float  lenD = max(length(d), 1e-5);
                float2 dirN = d / lenD;
                float2 perp = float2(-dirN.y, dirN.x);
                float  halfW = 0.0025;                     // ~2-3 px wide on a 1080p screen
                float  aspect = _ScreenParams.y / max(_ScreenParams.x, 1.0);
                perp.x *= aspect;                          // even thickness across axes
                float2 ofs0 = perp * halfW * p0.w;
                float2 ofs1 = perp * halfW * p1.w;
                g2f_ov va, vb, vc, vd;
                va.pos = float4(p0.xy + ofs0, p0.z, p0.w);
                vb.pos = float4(p0.xy - ofs0, p0.z, p0.w);
                vc.pos = float4(p1.xy + ofs1, p1.z, p1.w);
                vd.pos = float4(p1.xy - ofs1, p1.z, p1.w);
                stream.Append(va);
                stream.Append(vb);
                stream.Append(vc);
                stream.Append(vd);
                stream.RestartStrip();
            }

            // Emit the 12 wireframe edges of a box [bmn,bmx]. isFrame=true: bmn/bmx are RENDERER-LOCAL
            float3 FrameCorner(float3 c)
            {
                return _BoxFramePos.xyz + _BoxFrameX.xyz * c.x + _BoxFrameY.xyz * c.y + _BoxFrameZ.xyz * c.z;
            }
            void EmitBox(float3 bmn, float3 bmx, bool isFrame, inout TriangleStream<g2f_ov> stream)
            {
                float3 c0 = float3(bmn.x, bmn.y, bmn.z), c1 = float3(bmx.x, bmn.y, bmn.z);
                float3 c2 = float3(bmn.x, bmn.y, bmx.z), c3 = float3(bmx.x, bmn.y, bmx.z);
                float3 c4 = float3(bmn.x, bmx.y, bmn.z), c5 = float3(bmx.x, bmx.y, bmn.z);
                float3 c6 = float3(bmn.x, bmx.y, bmx.z), c7 = float3(bmx.x, bmx.y, bmx.z);
                float4 q0 = isFrame?ClipWorld(FrameCorner(c0)):ClipObj(c0), q1 = isFrame?ClipWorld(FrameCorner(c1)):ClipObj(c1);
                float4 q2 = isFrame?ClipWorld(FrameCorner(c2)):ClipObj(c2), q3 = isFrame?ClipWorld(FrameCorner(c3)):ClipObj(c3);
                float4 q4 = isFrame?ClipWorld(FrameCorner(c4)):ClipObj(c4), q5 = isFrame?ClipWorld(FrameCorner(c5)):ClipObj(c5);
                float4 q6 = isFrame?ClipWorld(FrameCorner(c6)):ClipObj(c6), q7 = isFrame?ClipWorld(FrameCorner(c7)):ClipObj(c7);
                EmitEdgeClip(q0, q1, stream); EmitEdgeClip(q1, q3, stream);
                EmitEdgeClip(q3, q2, stream); EmitEdgeClip(q2, q0, stream);
                EmitEdgeClip(q4, q5, stream); EmitEdgeClip(q5, q7, stream);
                EmitEdgeClip(q7, q6, stream); EmitEdgeClip(q6, q4, stream);
                EmitEdgeClip(q0, q4, stream); EmitEdgeClip(q1, q5, stream);
                EmitEdgeClip(q3, q7, stream); EmitEdgeClip(q2, q6, stream);
            }

            // World-axis cross (3 short edges through a world point) - marker for the probe.
            void EmitCross(float3 w, float r, inout TriangleStream<g2f_ov> stream)
            {
                EmitEdgeClip(ClipWorld(w - float3(r,0,0)), ClipWorld(w + float3(r,0,0)), stream);
                EmitEdgeClip(ClipWorld(w - float3(0,r,0)), ClipWorld(w + float3(0,r,0)), stream);
                EmitEdgeClip(ClipWorld(w - float3(0,0,r)), ClipWorld(w + float3(0,0,r)), stream);
            }

            [maxvertexcount(128)]
            void geom_ov(triangle v2g_ov input[3], uint primID : SV_PrimitiveID,
                         inout TriangleStream<g2f_ov> stream)
            {
                if (_ShowSetupPhysicsBounds < 0.5) return;  // overlay off
                if (primID != 0)      return;               // emit once per frame

                // GPU-vs-plugin differential marker (rest mode, plugin feeding): SMALL cross at the
                // GPU's wpos of cum vertex tri0[0] (sits ON the rendered cum by construction), BIG
                if (_UseRestPosTangent > 0.5 && _DbgVert0World.w > 0.5)
                {
                    EmitCross(input[0].wpos,       0.006, stream);
                    EmitCross(_DbgVert0World.xyz,  0.012, stream);
                    EmitEdgeClip(ClipWorld(input[0].wpos), ClipWorld(_DbgVert0World.xyz), stream);
                }

                if (_UseRestPosTangent > 0.5)
                {
                    // REST (womb): draw the plugin's MEASURED renderer-local AABB per chamber through
                    if (_BoxFramePos.w > 0.5)
                    {
                        if (_Box1LocalMax.w > 0.5)
                            EmitBox(_Box1LocalMin.xyz, _Box1LocalMax.xyz, true, stream);   // chamber 1 (womb)
                        if (_ChamberMode_0single_1connected_2closed > 0.5 && _Box2LocalMax.w > 0.5)
                            EmitBox(_Box2LocalMin.xyz, _Box2LocalMax.xyz, true, stream);   // chamber 2 (tube)
                    }
                }
                else
                {
                    // NON-REST generic object: object-space rest bounds via the real object matrix.
                    EmitBox(BoxMin(), BoxMax(), false, stream);
                    if (_ChamberMode_0single_1connected_2closed > 0.5)
                        EmitBox(Box2Min(), Box2Max(), false, stream);
                }
            }

            fixed4 frag_ov(g2f_ov i) : SV_Target
            {
                return fixed4(1, 1, 0, 1);                 // bright yellow
            }
            ENDCG
        }

        // ════════════════════════════════════════════════════════════════
        // TipDebug - when _ShowTipDebug is on AND the plugin pushed a valid tip (_DebugTipPos.w>0.5),
        // draw a WORLD-space marker at the BP tip to verify the plugin's tip read: a 3-axis crosshair,
        Pass
        {
            Name "TipDebug"
            Cull   Off
            ZTest  Always
            ZWrite Off

            CGPROGRAM
            #pragma target 4.0
            #pragma vertex   vert_tip
            #pragma geometry geom_tip
            #pragma fragment frag_tip

            struct app_tip { float4 vertex : POSITION; };
            struct v2g_tip { float4 pos : SV_POSITION; };
            struct g2f_tip { float4 pos : SV_POSITION; float4 col : TEXCOORD0; };

            v2g_tip vert_tip(app_tip v) { v2g_tip o; o.pos = UnityObjectToClipPos(v.vertex); return o; }

            float4 WClip(float3 w) { return mul(UNITY_MATRIX_VP, float4(w, 1.0)); }

            // Thin screen-space quad between two CLIP-space points, carrying a colour.
            void EmitTipEdge(float4 p0, float4 p1, float4 col, inout TriangleStream<g2f_tip> stream)
            {
                float2 ndc0 = p0.xy / max(abs(p0.w), 1e-5) * sign(p0.w);
                float2 ndc1 = p1.xy / max(abs(p1.w), 1e-5) * sign(p1.w);
                float2 d    = ndc1 - ndc0;
                float2 dirN = d / max(length(d), 1e-5);
                float2 perp = float2(-dirN.y, dirN.x);
                perp.x *= _ScreenParams.y / max(_ScreenParams.x, 1.0);
                float  hw = 0.0022;
                float2 o0 = perp * hw * p0.w, o1 = perp * hw * p1.w;
                g2f_tip va, vb, vc, vd;
                va.pos = float4(p0.xy + o0, p0.z, p0.w); va.col = col;
                vb.pos = float4(p0.xy - o0, p0.z, p0.w); vb.col = col;
                vc.pos = float4(p1.xy + o1, p1.z, p1.w); vc.col = col;
                vd.pos = float4(p1.xy - o1, p1.z, p1.w); vd.col = col;
                stream.Append(va); stream.Append(vb); stream.Append(vc); stream.Append(vd);
                stream.RestartStrip();
            }

            [maxvertexcount(128)]
            void geom_tip(triangle v2g_tip input[3], uint primID : SV_PrimitiveID,
                          inout TriangleStream<g2f_tip> stream)
            {
                if (_ShowTipDebug < 0.5)  return;          // overlay off
                if (_DebugTipPos.w < 0.5) return;          // no valid tip pushed
                if (primID != 0)          return;          // once per frame

                float3 P     = _DebugTipPos.xyz;
                float3 dir   = (dot(_DebugTipDir.xyz, _DebugTipDir.xyz) > 1e-6) ? normalize(_DebugTipDir.xyz) : float3(0,1,0);
                float  g     = max(_DebugTipGirth, 1e-4);
                float  depth = saturate(_DebugTipDepth01);
                float4 col   = float4(lerp(float3(0.1,0.4,1.0), float3(1.0,0.2,0.1), depth), 1.0); // blue(out)->red(in)

                EmitTipEdge(WClip(P - float3(g,0,0)), WClip(P + float3(g,0,0)), col, stream);
                EmitTipEdge(WClip(P - float3(0,g,0)), WClip(P + float3(0,g,0)), col, stream);
                EmitTipEdge(WClip(P - float3(0,0,g)), WClip(P + float3(0,0,g)), col, stream);

                // Girth ring perpendicular to dir.
                float3 up0 = abs(dir.y) < 0.9 ? float3(0,1,0) : float3(1,0,0);
                float3 u   = normalize(cross(dir, up0));
                float3 w   = cross(dir, u);
                float4 prev = WClip(P + g * u);
                [unroll] for (int k = 1; k <= 16; k++)
                {
                    float  a   = 6.2831853 * (float)k / 16.0;
                    float4 cur = WClip(P + g * (cos(a) * u + sin(a) * w));
                    EmitTipEdge(prev, cur, col, stream);
                    prev = cur;
                }

                // Axis line along -dir (entrance side -> tip); length grows with depth.
                float axisLen = g * 4.0 * (0.25 + 0.75 * depth);
                EmitTipEdge(WClip(P - dir * axisLen), WClip(P), col, stream);
            }

            fixed4 frag_tip(g2f_tip i) : SV_Target { return i.col; }
            ENDCG
        }
    }
    Fallback Off
}
