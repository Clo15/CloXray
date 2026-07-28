// CloXray/BodyReveal
// Invisible (ColorMask 0) - only stamps stencil=[_StencilRef] at body surface pixels.
// 4-slot pair scheme - body stencils are multiples of 4:
// Pair A: body=40, organ=41, orginside=42 (slot 43 unused)
// Pair B: body=44, organ=45, orginside=46
// Pair C: body=48, organ=49, orginside=50
Shader "CloXray/BodyReveal"
{
    Properties
    {
        // Match this to _StencilBody on the Organ material applied to the same character.
        // Pairs use EVEN bases 4/8/12/16 (organ = base+1). Match _StencilBody on the Organ material.
        [IntRange] _StencilRef ("Stencil Reference (pairs: 4/8/12/16; bit-stamp: 128)", Range(0, 255)) = 4
        // Default 255 = write the whole byte (the body's normal stamp). A BIT-STAMP plane (windowed reveal) sets
        // this to a single bit (128 = bit7) so Pass Replace writes only that bit, leaving the low-5 region intact.
        [IntRange] _StencilWriteMask ("Stencil Write Mask (255 = whole byte)", Range(0, 255)) = 255
        // Bit-stamp ZTest. Default LEqual = the body's normal on-mesh stamp: it sits AT body depth so only the
        [Enum(UnityEngine.Rendering.CompareFunction)] _StampZTest  ("Stamp ZTest (8=Always for a bit-stamp plane)", Float) = 4
    }
    SubShader
    {
        LOD 600
        // Queue 2500: renders with skin so ZTest LEqual correctly finds body depth.
        Tags { "Queue" = "AlphaTest+50" "RenderType" = "Transparent" }

        // Main pass: writes stencil=[_StencilRef] at body surface depth
        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }
            ZTest [_StampZTest]
            ZWrite On
            ColorMask 0

            Stencil
            {
                Ref [_StencilRef]
                WriteMask [_StencilWriteMask]
                Comp Always
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }

        // Shadow caster - invisible, no shadow casting
        Pass
        {
            Name "SHADOWCASTER"
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

            // Send all verts off-screen
            v2f vert(appdata v) { v2f o; o.pos = float4(2, 2, 2, 1); return o; }
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }
    }
    Fallback Off
}
