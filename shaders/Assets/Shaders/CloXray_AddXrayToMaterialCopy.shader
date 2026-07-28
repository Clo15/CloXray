// CloXray/AddXrayToMaterialCopy
// This shader renders INVISIBLY (ColorMask 0). Its only output is depth.
// With Offset -1, -1 it writes depth slightly closer to the camera than
// A Bayer 4x4 dither pattern in the fragment shader controls which screen
// 1. Duplicate the target mesh (so original + copy render at same world position)
// 2. On the copy, apply CloXray/AddXrayToMaterialCopy
Shader "CloXray/AddXrayToMaterialCopy"
{
    Properties
    {
        _Alpha ("X-ray Strength", Range(0, 1)) = 0.5
    }

    SubShader
    {
        LOD 100
        // (Geometry-1 by default; adjust for transparent targets.)
        Tags { "Queue" = "Geometry-1" "RenderType" = "Opaque" }

        Pass
        {
            Name "DepthBlock"
            ZTest LEqual
            ZWrite On
            Offset -1, -1
            ColorMask 0
            Cull Back

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _Alpha;

            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            // Bayer 4x4 dither matrix, threshold values in (0, 1).
            // Offsetting by 0.5 ensures (Alpha=0 -> all discard) and
            // (Alpha=1 -> none discard) work cleanly.
            float bayer4x4(int2 p)
            {
                int x = p.x & 3;
                int y = p.y & 3;
                // Hardcoded 4x4 Bayer matrix.
                int idx = y * 4 + x;
                float vals[16] = {
                     0,  8,  2, 10,
                    12,  4, 14,  6,
                     3, 11,  1,  9,
                    15,  7, 13,  5
                };
                return (vals[idx] + 0.5) / 16.0;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                int2 screenPx = int2(i.pos.xy);
                float threshold = bayer4x4(screenPx);

                // _Alpha < threshold -> this pixel does NOT block original.
                // _Alpha >= threshold -> this pixel blocks original (depth written).
                if (_Alpha < threshold) discard;

                return 0; // ColorMask 0 makes this irrelevant.
            }
            ENDCG
        }

        // Invisible shadow caster.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZTest Never
            ZWrite Off
            ColorMask 0

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };
            v2f vert(appdata v) { v2f o; o.pos = float4(2, 2, 2, 1); return o; }
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }
    }
    Fallback Off
}
