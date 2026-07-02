// CloXray/AddXrayToMaterialCopy
//
// Generic "make any object see-through" shader. Apply to a DUPLICATE of any
// mesh whose original shader you don't want to modify (e.g. a glass object
// with complex lighting you don't want to recreate).
//
// How it works:
//   This shader renders INVISIBLY (ColorMask 0). Its only output is depth.
//   With Offset -1, -1 it writes depth slightly closer to the camera than
//   the duplicate's natural position, so any later-rendering object at the
//   same screen pixel fails its ZTest and is skipped.
//
//   A Bayer 4x4 dither pattern in the fragment shader controls which screen
//   pixels participate. At low _Alpha most pixels discard (no blocking →
//   original fully visible). At high _Alpha few pixels discard (most block
//   → original mostly invisible → see what's behind).
//
//   The original mesh's full shader/lighting is preserved on the pixels that
//   are NOT blocked — no need to recreate its visuals.
//
// User workflow:
//   1. Duplicate the target mesh (so original + copy render at same world position)
//   2. On the copy, apply CloXray/AddXrayToMaterialCopy
//   3. Set the copy's "Render Queue" to be slightly LOWER than the original's
//      (e.g. original at 2000 → copy at 1999; original at 3000 → copy at 2999)
//   4. Adjust _Alpha:
//        0 = no x-ray (original fully visible)
//        1 = full x-ray (original invisible, what's behind shows through)
//        in between = dither-stippled partial transparency
//
// Notes:
//   - Stipple pattern visible at close camera distance (4-pixel grid).
//     Acceptable for general use; Bayer 4x4 minimizes visible artifacts.
//   - "What's behind" depends on what rendered before the original at those
//     pixels. The shader doesn't fabricate anything — if nothing rendered
//     behind, you'll see the scene clear color.
//   - No stencil interaction. Works independently of CloXray's body/organ
//     stencil chain. Compose with anything.
Shader "CloXray/AddXrayToMaterialCopy"
{
    Properties
    {
        // 0 = no see-through (original fully visible)
        // 1 = full see-through (original blocked, what's behind shows)
        _Alpha ("X-ray Strength", Range(0, 1)) = 0.5
    }

    SubShader
    {
        LOD 100
        // Default queue. User MUST set this to be lower than the target
        // original's queue in MEs for the shader to do anything useful.
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
            // Offsetting by 0.5 ensures (Alpha=0 → all discard) and
            // (Alpha=1 → none discard) work cleanly.
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
                // i.pos.xy gives screen-space pixel coordinates in DX11.
                int2 screenPx = int2(i.pos.xy);
                float threshold = bayer4x4(screenPx);

                // _Alpha < threshold → this pixel does NOT block original.
                // _Alpha >= threshold → this pixel blocks original (depth written).
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
