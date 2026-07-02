// CloXray/XrayMachine — "X-ray viewing screen" plane shader.
//
// Apply to a plane in scene positioned in front of a character. The plane
// acts like an x-ray viewing screen: behind the plane area you see
//   - solid black background
//   - silhouette outlines of characters and objects (where depth jumps)
//   - the OUTSIDE-stencil organ rendered through in its actual color
//     (this works because the plane is STENCIL-GATED to render only at
//     non-organ pixels, so organ's earlier alpha-blended render survives)
//
// Setup:
//   1. Add a Plane in Studio, position it in front of the character body
//   2. Apply CloXray/XrayMachine to it
//   3. Make sure the organ is using CloXray/Organ (or OrganDepthEdge) with
//      Alpha = 1 (fully opaque organ render)
//   4. Leave _StencilOrgan=0 and _StencilOrganMask=3 to catch every organ +
//      orginside in the scene (4-slot pair scheme: bodies on multiples of 4).
//
// What you'll see THROUGH the plane:
//   - Body pixels: solid black with white silhouette outlines at depth edges
//   - Organ pixels: full-color organ (rendered earlier by CloXray/Organ, not
//     covered by the plane thanks to stencil gating)
//   - Background pixels: solid black with silhouette outlines
//
// Outside the plane's footprint, the scene renders normally.
//
// Properties:
//   _BackgroundColor - "black" fill color (default pure black)
//   _OutlineColor    - silhouette outline color
//   _OutlineWidth    - outline width in centimeters (world space, at _RefDepth)
//   _RefDepth        - reference depth for sample offset calc
//   _Threshold       - depth jump in METERS for silhouette detection
//   _StencilOrgan    - stencil value that marks organ pixels (gated to NOT
//                      render over those, so organ shows through plane)
//   _StencilOrganMask- AND-mask for organ comparison
Shader "CloXray/XrayMachine"
{
    Properties
    {
        [Gamma] _BackgroundColor ("Background Color", Color) = (0, 0, 0, 1)
        [Gamma] _OutlineColor ("Outline Color", Color) = (1, 1, 1, 1)
        // Outline width in cm of world space at _RefDepth. Kept adjacent to
        // _RefDepth — they're tuned as a pair (RefDepth normalizes how much
        // the outline scales with zoom).
        _OutlineWidth ("Outline Width (cm, 0=off)", Range(0, 20)) = 0.15
        _RefDepth ("Reference Depth (meters)", Range(0.1, 20)) = 2.0
        _Threshold ("Depth Threshold (meters)", Range(0.001, 5)) = 0.3
        // 4-slot pair scheme — body stencils are multiples of 4:
        //   Pair A: body=40, organ=41, orginside=42
        //   Pair B: body=44, organ=45, orginside=46
        //   Pair C: body=48, organ=49, orginside=50
        // Backdrop renders where (stencil & mask) == (ref & mask) [Comp Equal].
        // With default Ref=0, Mask=3, Comp Equal:
        //   backdrop on stencils where low 2 bits = 0 → bodies + background.
        //   skipped on stencils where low 2 bits != 0 → all pair organs +
        //   orginsides simultaneously, regardless of which pair (A/B/C).
        // To restrict to a single pair, raise the mask to include high bits, e.g.
        //   Mask=255, Ref=41 → only pair A organ (only stencil exactly 41).
        [IntRange] _StencilOrgan ("Stencil: Backdrop Ref (0 = bodies pass)", Range(0, 255)) = 0
        [IntRange] _StencilOrganMask ("Stencil Mask (3 = low 2 bits)", Range(0, 255)) = 3
    }

    SubShader
    {
        // Queue Transparent+550 — after Organ (3500), OrgInside (3501),
        // BodyRevealCutout (3502), OutlineDepthEdge (3503), SilhouetteOutline
        // (3504). Renders late so organ alpha-blends have already happened.
        Tags { "Queue" = "Transparent+550" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "XrayMachine"
            Tags { "LightMode" = "ForwardBase" }
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            // Render backdrop where (stencil & mask) == (ref & mask) [Comp Equal].
            // With Ref=0, Mask=3: backdrop on stencils with low 2 bits = 0
            // (bodies and background). Organs (low2=1) and orginsides (low2=2)
            // fail the test → backdrop not drawn → the previously rendered
            // organ pixel shows through unchanged.
            //
            // Caveat: any other game shader that writes a stencil value with
            // low 2 bits != 0 (e.g. some hair, eye shaders depending on KK's
            // stencil usage) will also show through. If you see unrelated
            // geometry leaking through the plane, raise the mask to include
            // high bits — e.g. Mask=63, Ref=40 to restrict to the 40-47 range.
            Stencil
            {
                Ref [_StencilOrgan]
                ReadMask [_StencilOrganMask]
                Comp Equal
                Pass Keep
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _CameraDepthTexture;
            float4 _CameraDepthTexture_TexelSize;
            fixed4 _BackgroundColor;
            fixed4 _OutlineColor;
            float _Threshold;
            float _OutlineWidth;
            float _RefDepth;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos       : SV_POSITION;
                float4 screenPos : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            float SampleEyeDepth(float2 uv)
            {
                float d = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                return LinearEyeDepth(d);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.screenPos.xy / i.screenPos.w;
                // Outline off -> plain black backdrop; skip the 9 depth fetches over this near-full-screen plane.
                if (_OutlineWidth < 1e-4) return _BackgroundColor;
                float c = SampleEyeDepth(uv);

                // World-cm to UV offset (zoom-aware via camera projection).
                float widthMeters = _OutlineWidth * 0.01;
                float refDepth = max(c, _RefDepth);
                float2 sampleOffset = widthMeters /
                                      (2.0 * refDepth) *
                                      float2(unity_CameraProjection._m00,
                                             unity_CameraProjection._m11);

                // 8-direction sampling for continuous outline ring.
                float l = SampleEyeDepth(uv + float2(-sampleOffset.x, 0));
                float r = SampleEyeDepth(uv + float2(+sampleOffset.x, 0));
                float u = SampleEyeDepth(uv + float2(0, +sampleOffset.y));
                float d = SampleEyeDepth(uv + float2(0, -sampleOffset.y));
                float2 diag = sampleOffset * 0.70710678;
                float ul = SampleEyeDepth(uv + float2(-diag.x, +diag.y));
                float ur = SampleEyeDepth(uv + float2(+diag.x, +diag.y));
                float dl = SampleEyeDepth(uv + float2(-diag.x, -diag.y));
                float dr = SampleEyeDepth(uv + float2(+diag.x, -diag.y));

                float maxDiff = max(
                    max(max(abs(c - l), abs(c - r)),
                        max(abs(c - u), abs(c - d))),
                    max(max(abs(c - ul), abs(c - ur)),
                        max(abs(c - dl), abs(c - dr))));

                if (maxDiff >= _Threshold) return _OutlineColor;
                return _BackgroundColor;
            }
            ENDCG
        }
    }
    Fallback Off
}
