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
        // Stamp ZWrite. 1 = write depth, correct on the BODY: the skin already wrote that same depth at
        // 2350, so re-writing it changes nothing. On CLOTHES it is actively wrong. A garment may be
        // TRANSPARENT (xukmi's Studio-alpha variants sit at queue ~4907 and write no depth of their own),
        // and our copy stamps at 2500 - so we would lay an OPAQUE depth footprint for a see-through
        // garment, 2400 queue steps early, and everything behind it is depth-rejected. That is how a
        // clothes stamp can erase a character standing behind her. Clothes copies set this to 0: the
        // stencil is still stamped, so the x-ray window works, but no depth is written.
        [Enum(Off, 0, On, 1)] _StampZWrite ("Stamp ZWrite (0 for clothes)", Float) = 1
        // REGION MASK (body UV space). White = stamp here, black = do not.
        // ZTest LEqual means only the FRONTMOST body surface stamps, so when a hand crosses the belly the
        // fragment that stamps IS the hand - her whole body is one mesh and one material, and the stencil
        // only says "body", so the womb draws through the hand exactly as it draws through the belly.
        // Masking the limbs out means those fragments never stamp, that pixel is never marked as body, and
        // the limb reads solid. Default white so anything without a mask - every clothes copy, and any
        // existing scene - behaves exactly as before.
        _RegionMask ("Region Mask (white = reveal here)", 2D) = "white" {}
        // Below this the mask counts as black. 0 disables masking entirely.
        _RegionMaskCutoff ("Region Mask Cutoff", Range(0, 1)) = 0.5
        // CLOTHED-limb blocking master : 0 = OFF (default - a sleeve over
        // her hand x-rays as it always did), >0 = ON with this much outward inflation to cover the
        // fabric's overhang beyond the limb inside it. Off by default because the right width is
        // per-outfit; turning it on IS the tuning act. Bare hands block regardless - they rely on
        // the region mask + plain depth, never on this mark. ME row on the body copy.
        _LimbBlockInflate ("Clothed-Limb Block (m, 0=off)", Range(0, 0.06)) = 0
        // Which stencil bits the STAMP pass requires clear before stamping. The plugin sets 128 on
        // garment copies (respect the limb block) and 0 on the body copy (a bare limb already blocks
        // the body stamp by plain depth - gating it too only fattened the margins).
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
            ZWrite [_StampZWrite]
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

            sampler2D _RegionMask;
            float4 _RegionMask_ST;
            float _RegionMaskCutoff;

        // Ordered 4x4 Bayer in [0,1) from pixel coords - pure math, ps_3_0-safe (no arrays).
        float Bayer2(float2 a) { a = floor(a); return frac(a.x / 2.0 + a.y * a.y * 0.75); }
        float Bayer4(float2 a) { return Bayer2(0.5 * a) * 0.25 + Bayer2(a); }

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f    { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = TRANSFORM_TEX(v.uv, _RegionMask);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                // Cutoff 0 = masking off, so a default-white mask and an unset cutoff both mean "stamp".
                // The masks ship with a BLURRED boundary band, and the clip dithers against it
                // (threshold = cutoff +/- 0.5 across the Bayer pattern): the visible->invisible edge
                // becomes a gradual screen-space fade instead of a hard cut. Solid mask regions
                // (0 or 1) are unaffected.
#if defined(SHADER_API_D3D9)
                float _dth = 0.5;
#else
                float _dth = Bayer4(i.pos.xy);
#endif
                // clamp keeps the extremes absolute: Bayer reaches exactly 0, and clip(0) does NOT
                // discard - so an unclamped threshold let 1-in-16 pixels pass even on a pure-black
                // mask texel (the sack ghost through clothes, the faint limb bleed). Solid regions
                // are now decided absolutely; only the gradient band dithers.
                if (_RegionMaskCutoff > 0)
                    clip(tex2D(_RegionMask, i.uv).r - clamp(_RegionMaskCutoff + (_dth - 0.5), 0.001, 0.999));
                return 0;
            }
            ENDCG
        }

        // LIMB BLOCK (b969 - runs LAST, writes a REGION VALUE, not a bit).
        // Limb fragments (the region mask's BLACK area) overwrite the low-5 region with 31, the
        // RESERVED LIMB SENTINEL: "a limb of hers is in front here - never open the x-ray window on
        // it". WHY IT BLOCKS (exact semantics - Unity's stencil test is Ref <op> buffer): every pass
        // that CREATES punched depth (BottomWindow, DepthClear, the organ stencil/forward chain)
        // requires Comp Equal against a pair value or pair+1, and 31 is neither (pairs are 4k, +1 is
        // 4k+1; 31 = 4*7+3, and being the register maximum it can never be reached by future pairs
        // either) - so no window is ever punched on a limb pixel. The two Comp LEqual competition
        // passes DO pass their stencil test at 31 (5 <= 31), but they are ZTest Less against the
        // UNPUNCHED near limb depth, so they write nothing - depth blocks them, not stencil. Nothing
        // has to test for the sentinel explicitly, no gate property exists any more, and stencil
        // bit7 is free for USER x-ray-machine windows (see XRAY_RENDER_MODEL.md).
        // ORDER IS THE MECHANISM: this pass is declared AFTER the stamp so it overwrites the region
        // the stamp just wrote, and garment copies sit at queue 2499 (one BEFORE the body's 2500) so
        // a sleeve's stamp cannot re-open a limb the body then marks. The body's own queue is
        // untouched.
        // The vertex PULL makes a hand INSIDE a garment still win the ordinary z-test against the
        // fabric it wears (fabric is a few cm; the pull is 8cm), while an arm far behind the torso
        // still loses and cannot shadow the window. On garment copies the mask defaults to white, so
        // every fragment discards and this pass is a no-op - one shader serves both roles.
        Pass
        {
            Name "LimbBlock"
            ZTest LEqual
            ZWrite Off
            ColorMask 0

            Stencil
            {
                Ref 31
                WriteMask 31
                Comp Always
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _RegionMask;
            float4 _RegionMask_ST;
            float _RegionMaskCutoff;

            // Metres toward the camera for limb vertices: must exceed garment thickness (a few cm)
            // and stay far below behind-the-torso distances (tens of cm). Anything 0.05-0.15 works.
            #define LIMB_BLOCK_PULL 0.08
            float _LimbBlockInflate;   // outward inflation (m) - ME row on the body copy

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f    { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.uv = TRANSFORM_TEX(v.uv, _RegionMask);
                float limb = tex2Dlod(_RegionMask, float4(o.uv, 0, 0)).r;   // black = limb
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                if (_RegionMaskCutoff > 0 && limb < _RegionMaskCutoff)
                {
                    wp += UnityObjectToWorldNormal(v.normal) * _LimbBlockInflate;
                    wp += normalize(_WorldSpaceCameraPos - wp) * LIMB_BLOCK_PULL;
                }
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1));
                return o;
            }
            float Bayer2L(float2 a) { a = floor(a); return frac(a.x / 2.0 + a.y * a.y * 0.75); }
            float Bayer4L(float2 a) { return Bayer2L(0.5 * a) * 0.25 + Bayer2L(a); }
            fixed4 frag(v2f i) : SV_Target
            {
                if (_RegionMaskCutoff <= 0) discard;                     // masking off -> no limb block
                if (_LimbBlockInflate <= 0) discard;                     // clothed-limb blocking OFF (default)
                // dithered inverse of the stamp clip: the limb block fades over the same band
#if defined(SHADER_API_D3D9)
                float _dth = 0.5;
#else
                float _dth = Bayer4L(i.pos.xy);
#endif
                clip(clamp(_RegionMaskCutoff + (_dth - 0.5), 0.001, 0.999) - tex2D(_RegionMask, i.uv).r);   // clamped: solid regions absolute (see the stamp pass)
                return 0;
            }
            ENDCG
        }

        // Shadow caster — invisible, no shadow casting
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
