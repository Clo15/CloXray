// CloXray/BodyRevealExtra - overlay-style x-ray cutout
// Apply to a duplicate body material on body slot 2 (alongside CloXray/BodyReveal
// on body slot 1). Renders body texture × _Color with KK hair-style lighting ON
// Architecture (uses existing CloXray/Organ's stencil chain - no OrganMark needed):
// Q2500 - CloXray/BodyReveal stamps stencil 43 at body pixels
// Q3500 - CloXray/Organ alpha-blends organ over body, increments stencil to 44
Shader "CloXray/BodyRevealExtra"
{
    Properties
    {
        // Must equal the paired CloXray/Organ material's _StencilBody_Plus_1.
        // 4-slot scheme: pair A=41, pair B=45, pair C=49.
        [IntRange] _StencilBody_Plus_1 ("Stencil: Organ Ref (= Body + 1)", Range(2, 18)) = 5

        // Whether to include OrgInside pixels in the cutout effect.
        // 4 (LEqual): cutout covers Organ + OrgInside pixels (default)
        // 3 (Equal): cutout covers Organ pixels only
        [Enum(OrganOnly, 3, OrganAndOrgInside, 4)] _StencilComp ("Cutout: include OrgInside?", Float) = 4

        _AnotherRamp ("Another Ramp(ViewDir)", 2D) = "white" {}
        _MainTex ("Body Texture", 2D) = "white" {}
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _AlphaMask ("Alpha Mask", 2D) = "white" {}
        _DetailMask ("Detail Mask", 2D) = "black" {}
        _HairGloss ("Gloss Mask", 2D) = "black" {}
        _ColorMask ("Color Mask", 2D) = "black" {}
        [Gamma]_Color ("Color", Color) = (1,1,1,1)
        [Gamma]_Color2 ("Color2", Color) = (0.7843137,0.7843137,0.7843137,1)
        [Gamma]_Color3 ("Color3", Color) = (0.5,0.5,0.5,1)
        [Gamma]_GlossColor ("GlossColor", Color) = (1,1,1,1)
        [Gamma]_SpecularColor ("SpecularColor", Color) = (1,1,1,1)
        [Gamma]_LineColor ("LineColor", Color) = (0.5,0.5,0.5,1)
        [Gamma]_ShadowColor ("Shadow Color", Color) = (0.628,0.628,0.628,1)
        [Gamma]_CustomAmbient("Custom Ambient", Color) = (0.666666666, 0.666666666, 0.666666666, 1)
        _SpeclarHeight ("Speclar Height", Range(0, 1)) = 0.85
        _SpecularHairPower ("Specular Power", Range(0, 1)) = 1
        _rimpower ("Rim Width", Range(0, 1)) = 0.5
        _rimV ("Rim Strength", Range(0, 1)) = 0.75
        _ShadowExtend ("Shadow Extend", Range(0, 1)) = 0.5
        _NormalMapScale ("NormalMapScale", Float) = 1
        [HideInInspector] _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        [MaterialToggle] _UseRampForLights ("Use Ramp For Light", Float) = 1
        [MaterialToggle] _UseRampForSpecular ("Use Ramp For Specular", Float) = 0
        [MaterialToggle] _SpecularIsHighlights ("Specular is highlight", Float) = 0
        _SpecularIsHighLightsPow ("Specular is highlight Pow", Range(0,128)) = 64
        _SpecularIsHighlightsRange ("Specular is highlight Range", Range(0, 20)) = 5
        [MaterialToggle] _UseMeshSpecular ("Use Mesh Specular", Float) = 0
        [MaterialToggle] _UseLightColorSpecular ("Use Light Color Specular", Float) = 1
        _EmissionMask ("Emission Mask", 2D) = "black" {}
        [Gamma]_EmissionColor("Emission Color", Color) = (1, 1, 1, 1)
        _EmissionIntensity("Emission Intensity", Float) = 1
        [Enum(Off,0,On,1)]_SpecularHeightInvert ("Specular Height Invert", Float) = 0
        [MaterialToggle] _UseDetailRAsSpecularMap ("Use DetailR as Specular Map", Float) = 0
        _UseKKPRim ("Use KKP Rim", Range(0 ,1)) = 0
        [Gamma]_KKPRimColor ("Body Rim Color", Color) = (1.0, 1.0, 1.0, 1.0)
        _KKPRimSoft ("Body Rim Softness", Float) = 1.5
        _KKPRimIntensity ("Body Rim Intensity", Float) = 0.75
        _KKPRimAsDiffuse ("Body Rim As Diffuse", Range(0, 1)) = 0.0
        _KKPRimRotateX("Body Rim Rotate X", Float) = 0.0
        _KKPRimRotateY("Body Rim Rotate Y", Float) = 0.0

        // X-ray strength. 0 = no x-ray (body suit opaque). 1 = full x-ray
        // (body suit invisible, organ revealed).
        _XrayAlpha ("X-ray Strength", Range(0, 1)) = 0.5
    }

    SubShader
    {
        LOD 600
        // Queue 3504 = after the whole womb stack - Organ (3500), OrgInside (3502),
        // Liquid/cum (3503) - so _XrayAlpha is a true master fade of the in-body
        Tags { "Queue" = "Transparent+504" "RenderType" = "Transparent" }

        // ----------------------------------------------------------------
        // Pass 0: Forward - KK hair-style lighting at organ silhouette
        // pixels (stencil = _StencilBody_Plus_1, set by CloXray/Organ).
        // Output alpha = textureAlpha × (1 - _XrayAlpha).
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha, SrcAlpha OneMinusSrcAlpha
            Cull Back
            ZTest LEqual
            ZWrite Off

            Stencil
            {
                Ref [_StencilBody_Plus_1]
                ReadMask 31
                Comp [_StencilComp]
                Pass Keep
            }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vertCutout
            #pragma fragment fragCutout
            #pragma multi_compile _ VERTEXLIGHT_ON
            #pragma multi_compile _ SHADOWS_SCREEN

            #define KKP_EXPENSIVE_RAMP
            #define HAIR_FRONT

            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            #include "Lighting.cginc"

            #include "Includes/KKPHairInput.cginc"
            #include "Includes/KKPHairDiffuse.cginc"
            #include "Includes/KKPVertexLights.cginc"
            #include "Includes/KKPVertexLightsSpecular.cginc"
            #include "Includes/KKPEmission.cginc"

            float _XrayAlpha;

            float3 CreateBinormal_C(float3 normal, float3 tangent, float binormalSign) {
                return cross(normal, tangent.xyz) * (binormalSign * unity_WorldTransformParams.w);
            }
            float3x3 AngleAxis3x3_C(float angle, float3 axis) {
                float c, s; sincos(angle, s, c); float t = 1 - c;
                float x = axis.x, y = axis.y, z = axis.z;
                return float3x3(
                    t*x*x+c,   t*x*y-s*z, t*x*z+s*y,
                    t*x*y+s*z, t*y*y+c,   t*y*z-s*x,
                    t*x*z-s*y, t*y*z+s*x, t*z*z+c);
            }

            Varyings vertCutout (VertexData v)
            {
                Varyings o;
                o.posWS = mul(unity_ObjectToWorld, v.vertex);
                o.posCS = mul(UNITY_MATRIX_VP, o.posWS);
                o.normalWS = UnityObjectToWorldNormal(v.normal);
                o.tanWS = float4(UnityObjectToWorldDir(v.tangent.xyz), v.tangent.w);
                float3 biTan = cross(o.tanWS, o.normalWS);
                o.bitanWS = normalize(biTan);
                o.uv0 = v.uv0;
                o.uv1 = v.uv1;
            #ifdef SHADOWS_SCREEN
                float4 projPos = o.posCS;
                projPos.y *= _ProjectionParams.x;
                float4 projbiTan;
                projbiTan.xyz = o.bitanWS;
                projbiTan.xzw = projPos.xwy * 0.5;
                o.shadowCoordinate.zw = projPos.zw;
                o.shadowCoordinate.xy = projbiTan.zz + projbiTan.xw;
            #endif
                return o;
            }

            fixed4 fragCutout (Varyings i, int frontFace : VFACE) : SV_Target
            {
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.posWS);
                float3 worldLight = normalize(_WorldSpaceLightPos0.xyz);

                float4 mainTex = tex2D(_MainTex, i.uv0 * _MainTex_ST.xy + _MainTex_ST.zw);
                float alpha = AlphaClip(i.uv0, mainTex.a);
                float3 diffuse = GetDiffuse(i.uv0) * mainTex.rgb;

                float3 ambientShadowExtendAdjust;
                AmbientShadowAdjust(ambientShadowExtendAdjust);

                float2 normalUV = i.uv0 * _NormalMap_ST.xy + _NormalMap_ST.zw;
                float3 normal = UnpackScaleNormal(tex2D(_NormalMap, normalUV), _NormalMapScale);
                float3 binormal = CreateBinormal_C(i.normalWS, i.tanWS.xyz, i.tanWS.w);
                normal = normalize(normal.x * i.tanWS + normal.y * binormal + normal.z * i.normalWS);
                float3 adjustedNormal = normalize(normal);

                float3x3 rotX = AngleAxis3x3_C(_KKPRimRotateX, float3(0, 1, 0));
                float3x3 rotY = AngleAxis3x3_C(_KKPRimRotateY, float3(1, 0, 0));
                float3 adjustedViewDir = frontFace == 1 ? viewDir : -viewDir;
                float3 rotView = mul(adjustedViewDir, mul(rotX, rotY));
                float kkpFres = max(0.1, dot(normal, rotView));
                kkpFres = saturate(pow(1-kkpFres, _KKPRimSoft) * _KKPRimIntensity);
                _KKPRimColor.a *= (_UseKKPRim);
                float3 kkpFresCol = kkpFres * _KKPRimColor;
                diffuse = lerp(diffuse, kkpFresCol, _KKPRimColor.a * kkpFres * _KKPRimAsDiffuse);

                float fresnel = max(0.0, dot(viewDir, adjustedNormal));
                float anotherRamp = tex2D(_AnotherRamp, fresnel * _AnotherRamp_ST.xy + _AnotherRamp_ST.zw).x;
                fresnel = 1 - fresnel;
                fresnel = log2(fresnel);
                float rimPow = _rimpower * 9.0 + 1.0;
                fresnel *= rimPow;
                fresnel = exp2(fresnel);
                fresnel = saturate(fresnel * 5.0 - 1.5) * (1-_UseKKPRim);

                ambientShadowExtendAdjust = min(ambientShadowExtendAdjust * fresnel, 0.5);

                KKVertexLight vertexLights[4];
            #ifdef VERTEXLIGHT_ON
                GetVertexLights(vertexLights, i.posWS);
            #endif
                float4 vertexLighting = 0.0;
                float vertexLightRamp = 1.0;
            #ifdef VERTEXLIGHT_ON
                vertexLighting = GetVertexLighting(vertexLights, adjustedNormal);
                float2 vertexLightRampUV = vertexLighting.a * _RampG_ST.xy + _RampG_ST.zw;
                vertexLightRamp = tex2D(_RampG, vertexLightRampUV).x;
                float3 rampLighting = GetRampLighting(vertexLights, adjustedNormal, vertexLightRamp);
                vertexLighting.rgb = _UseRampForLights ? rampLighting : vertexLighting.rgb;
            #endif

                float3 halfVector = normalize(viewDir + worldLight);
                float specularMesh = max(dot(halfVector, adjustedNormal), 0.0);
                specularMesh = log2(specularMesh);
                float specularPowerMesh = _SpecularHairPower * 256;
                specularPowerMesh = specularPowerMesh * specularMesh;
                specularPowerMesh = saturate(exp2(specularPowerMesh) * _SpecularHairPower * _SpecularColor.a);
                float specularMask = _SpecularIsHighLightsPow;
                specularMask = specularMask * specularMesh;
                specularMask = saturate(exp2(specularMask) * _SpecularColor.a);

            #ifdef KKP_EXPENSIVE_RAMP
                float2 lightRampUV = specularPowerMesh * _RampG_ST.xy + _RampG_ST.zw;
                specularPowerMesh = tex2D(_RampG, lightRampUV) * _UseRampForSpecular + specularPowerMesh * (1 - _UseRampForSpecular);
            #endif

                float3 specularLightColor = _UseLightColorSpecular ? _LightColor0.rgb * _SpecularColor.a : _SpecularColor.rgb * _SpecularColor.a;
                float4 specularColorMesh;
                specularColorMesh.rgb = specularPowerMesh * specularLightColor;
                specularColorMesh.a = specularMask;
            #ifdef VERTEXLIGHT_ON
                specularColorMesh += GetVertexSpecularHair(vertexLights, adjustedNormal, viewDir, _SpecularIsHighLightsPow, _SpecularHairPower);
            #endif
                float specular = specularColorMesh.a;
                float3 specularColor = specularColorMesh.rgb;

                float lambert = saturate(dot(worldLight, adjustedNormal)) + vertexLighting.a;
                float ramp = tex2D(_RampG, lambert * _RampG_ST.xy + _RampG_ST.zw).x;
                float bitanFres = dot(viewDir, i.bitanWS);
                float specularHeight = _SpeclarHeight - 1.0;
                float3 hairGlossVal;
            #ifdef HAIR_FRONT
                hairGlossVal.x = lambert * 0.0199999809 + i.uv1.x;
                hairGlossVal.x += 0.99000001;
            #else
                hairGlossVal.x = lambert * 0.00499999989 + i.uv1.x;
            #endif
                float invertSpecularHeight = _SpecularHeightInvert ? -1 : 1;
                hairGlossVal.z = invertSpecularHeight * specularHeight * bitanFres + i.uv1.y;
                hairGlossVal.y = hairGlossVal.z + 0.00800000038;

                float4 hairGlossUV = hairGlossVal.xyxz * _HairGloss_ST.xyxy + _HairGloss_ST.zwzw;
                float4 hairGloss1 = tex2D(_HairGloss, hairGlossUV.xy);
                float4 hairGloss2 = tex2D(_HairGloss, hairGlossUV.zw);
                float hairGloss = (hairGloss1 - hairGloss2) * 0.5f;

                float4 ambientShadow = 1 - _ambientshadowG.wxyz;
                float3 ambientShadowIntensity = -ambientShadow.x * ambientShadow.yzw + 1;
                float ambientShadowAdjust = _ambientshadowG.w * 0.5 + 0.5;
                float ambientShadowAdjustDoubled = ambientShadowAdjust + ambientShadowAdjust;
                bool ambientShadowAdjustShow = 0.5 < ambientShadowAdjust;
                ambientShadow.rgb = ambientShadowAdjustDoubled * _ambientshadowG.rgb;
                float3 finalAmbientShadow = ambientShadowAdjustShow ? ambientShadowIntensity : ambientShadow.rgb;
                finalAmbientShadow = saturate(finalAmbientShadow);
                float3 invertFinalAmbientShadow = 1 - finalAmbientShadow;

                finalAmbientShadow = finalAmbientShadow * _ShadowColor.xyz;
                finalAmbientShadow += finalAmbientShadow;
                float3 shadowCol = _ShadowColor - 0.5;
                shadowCol = -shadowCol * 2 + 1;
                invertFinalAmbientShadow = -shadowCol * invertFinalAmbientShadow + 1;
                bool3 shadeCheck = 0.5 < _ShadowColor.xyz;
                {
                    float3 hlslcc_movcTemp = finalAmbientShadow;
                    hlslcc_movcTemp.x = (shadeCheck.x) ? invertFinalAmbientShadow.x : finalAmbientShadow.x;
                    hlslcc_movcTemp.y = (shadeCheck.y) ? invertFinalAmbientShadow.y : finalAmbientShadow.y;
                    hlslcc_movcTemp.z = (shadeCheck.z) ? invertFinalAmbientShadow.z : finalAmbientShadow.z;
                    finalAmbientShadow = hlslcc_movcTemp;
                }
                finalAmbientShadow = saturate(finalAmbientShadow);
                float minusAmbientShadow = finalAmbientShadow - 1;
                minusAmbientShadow = hairGloss * minusAmbientShadow + 1;
                shadowCol = diffuse * minusAmbientShadow;
                shadowCol *= finalAmbientShadow;
                diffuse = diffuse * minusAmbientShadow - shadowCol;

                float shadowAttenuation = saturate(min(ramp, anotherRamp));
                float rampAdjust = ramp * 0.5 + 0.5;
            #ifdef SHADOWS_SCREEN
                float2 shadowMapUV = i.shadowCoordinate.xy / i.shadowCoordinate.ww;
                float4 shadowMap = tex2D(_ShadowMapTexture, shadowMapUV);
                shadowAttenuation *= shadowMap;
            #endif

                float4 detailMask = tex2D(_DetailMask, i.uv0 * _DetailMask_ST.xy + _DetailMask_ST.zw);
                float specularMap = _UseDetailRAsSpecularMap ? detailMask.r : 1;
                _SpecularHairPower *= specularMap;
                float2 invertDetailGB = 1 - detailMask.gb;
                float shadowMasked = shadowAttenuation * invertDetailGB.x;
                shadowAttenuation = max(shadowAttenuation, invertDetailGB.x);
                diffuse = shadowMasked * diffuse + shadowCol;

                hairGloss2.x = _SpecularIsHighlights ? min(hairGloss2.x, specular * _SpecularIsHighlightsRange) : hairGloss2.x;
                hairGloss2.x *= specularMap;
                float hairGlossMask = hairGloss2.x * rampAdjust * _GlossColor.a;
                float3 hairGlossColor = hairGlossMask * _GlossColor.rgb * _GlossColor.a;
                diffuse = hairGlossColor + saturate(1 - hairGlossMask) * diffuse;
                float rimVal = invertDetailGB.x * _rimV;
                rimVal *= invertDetailGB.y;

                float3 finalDiffuse = saturate(rimVal * ambientShadowExtendAdjust + diffuse) + _UseMeshSpecular * specularColor;

                float shadowExtend = 1 - _ShadowExtend;
                shadowAttenuation = max(shadowAttenuation, shadowExtend);
                float3 shading = 1 - finalAmbientShadow;
                shading = shadowAttenuation * shading + finalAmbientShadow;
                finalDiffuse *= shading;
                shading = (_LightColor0.xyz + vertexLighting.rgb * vertexLightRamp) * float3(0.6, 0.6, 0.6) + _CustomAmbient.rgb;
                shading = max(shading, _ambientshadowG.rgb);
                finalDiffuse *= shading;
                finalDiffuse = lerp(finalDiffuse, kkpFresCol, _KKPRimColor.a * kkpFres * (1 - _KKPRimAsDiffuse));

                float4 emission = GetEmission(i.uv0);
                finalDiffuse = finalDiffuse * (1 - emission.a) + (emission.a * emission.rgb);

                // _XrayAlpha = 0 -> suit fully opaque (organ hidden behind body)
                // _XrayAlpha = 1 -> suit invisible (organ from Q3500 revealed)
                return float4(finalDiffuse, alpha * (1 - _XrayAlpha));
            }
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 1: ShadowCaster - invisible.
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
