// CloXray/GradientTest
// Solid-color shell shader with three independent alpha modulators that all
// multiply together:
//   1. _Tint.a              — base transparency
//   2. View-angle gradient  — pow(|dot(N, V)|, _Power), optionally inverted
//                             (off when _Power = 0)
//   3. Vertex color alpha   — multiplies by vertex.color.a, optionally
//                             inverted (1 - vColor.a) for meshes whose
//                             paint convention is opposite of what you need
//
// Recipes:
//   A. Bigger-mesh silhouette fresnel:
//      _Power = 2.5, _Invert = 1, _Cull = 1 (Front), _UseVertexAlpha = 0
//      → bright at mesh silhouette, fades to transparent at the middle.
//
//   B. Solidify+vertex-paint, organ-visible-at-center:
//      _Power = 0, _UseVertexAlpha = 1, _InvertVertexAlpha = 0, _Cull = 0
//      → with a mesh painted (inner=alpha 1, outer=alpha 0): inner = opaque,
//        outer = transparent. (Original WombShell convention.)
//
//   C. Body-fade — organ visible near surface, fades to BODY COLOR outward:
//      _Tint = body skin color (alpha 1), _Power = 0,
//      _UseVertexAlpha = 1, _InvertVertexAlpha = 1, _Cull = 2 (Back)
//      → with the same WombShell mesh (inner=alpha 1, outer=alpha 0): inner
//        becomes transparent (organ shows through), outer becomes opaque
//        body color so the shell blends smoothly into the surrounding body.
//
// Always-on-top (ZTest Always) so it's visible regardless of body depth.
// No stencil interaction.
Shader "CloXray/GradientTest"
{
    Properties
    {
        _Tint ("Tint (alpha = base transparency)", Color) = (1, 0.5, 0.5, 1)
        [Enum(Off,0,Front,1,Back,2)] _Cull ("Cull", Range(0, 2)) = 1
        _Power ("Gradient Power (0=off, 2-3=soft)", Range(0, 10)) = 2.5
        [Toggle] _Invert ("Gradient Invert (1=bright at silhouette)", Float) = 1
        [Toggle] _UseVertexAlpha ("Use Vertex Color Alpha", Float) = 0
        // Flip the vertex-color alpha: if your shell has inner=alpha 1 and
        // outer=alpha 0, turn this ON to swap the gradient direction (use
        // when you want OUTER opaque + INNER transparent, e.g. body-fade).
        [Toggle] _InvertVertexAlpha ("Invert Vertex Alpha (1 - vColor.a)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+520" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Name "GradientTest"
            Tags { "LightMode" = "ForwardBase" }
            ZTest Always
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            Cull [_Cull]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Tint;
            float _Power;
            float _Invert;
            float _UseVertexAlpha;
            float _InvertVertexAlpha;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float4 vColor   : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normalWS = UnityObjectToWorldNormal(v.normal);
                o.vColor = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float a = _Tint.a;

                // View-angle gradient (cheap when _Power == 0).
                if (_Power > 0.0001)
                {
                    float3 V = normalize(_WorldSpaceCameraPos.xyz - i.worldPos);
                    float facing = saturate(abs(dot(normalize(i.normalWS), V)));
                    float g = pow(facing, _Power);
                    if (_Invert > 0.5) g = 1.0 - g;
                    a *= g;
                }

                // Vertex-color alpha multiplier (off by default).
                if (_UseVertexAlpha > 0.5)
                {
                    float vca = i.vColor.a;
                    if (_InvertVertexAlpha > 0.5) vca = 1.0 - vca;
                    a *= vca;
                }

                return fixed4(_Tint.rgb, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
