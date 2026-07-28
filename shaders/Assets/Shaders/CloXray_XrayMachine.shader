// CloXray/XrayMachine - "X-ray viewing screen" plane shader.
// - the outside-stencil organ rendered through in its actual color
// (this works because the plane is STENCIL-GATED to render only at
// 1. Add a Plane in Studio, position it in front of the character body
// 2. Apply CloXray/XrayMachine to it
// 3. Make sure the organ is using CloXray/Organ (or OrganDepthEdge) with
Shader "CloXray/XrayMachine"
{
    Properties
    {
        [Gamma] _BackgroundColor ("Background Color", Color) = (0, 0, 0, 1)
        [Gamma] _OutlineColor ("Outline Color", Color) = (1, 1, 1, 1)
        // Outline width in cm of world space at _RefDepth. Kept adjacent to
        _OutlineWidth ("Outline Width (cm, 0=off)", Range(0, 20)) = 0.15
        _RefDepth ("Reference Depth (meters)", Range(0.1, 20)) = 2.0
        _Threshold ("Depth Threshold (meters)", Range(0.001, 5)) = 0.3
        // Pair A: body=40, organ=41, orginside=42
        // Pair B: body=44, organ=45, orginside=46
        // Pair C: body=48, organ=49, orginside=50
        [IntRange] _StencilOrgan ("Stencil: Backdrop Ref (0 = bodies pass)", Range(0, 255)) = 0
        [IntRange] _StencilOrganMask ("Stencil Mask (3 = low 2 bits)", Range(0, 255)) = 3
    }

    SubShader
    {
        // Queue Transparent+550 - after Organ (3500), OrgInside (3501),
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
            // Caveat: any other game shader that writes a stencil value with
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
