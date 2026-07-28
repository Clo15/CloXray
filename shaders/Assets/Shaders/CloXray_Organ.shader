// CloXray/Organ - v3 (with Outline pass)
// v3: removed outline pulse animation + view-facing gradient fade (and their
// Pass 0 StencilWrite : stamps stencil=[_StencilBody_Plus_1] only where body stencil
// [_StencilBody] is present. Cull Back, ZTest Always, ZWrite Off.
// Pass 1 Forward : full KK lighting. ZTest Always renders the organ through
// the body regardless of depth - correct for x-ray. Stencil Equal gating
Shader "CloXray/Organ"
{
    Properties
    {
        _AnotherRamp ("Another Ramp(LineR)", 2D) = "white" {}
        _MainTex ("MainTex", 2D) = "white" {}
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalMapDetail ("Normal Map Detail", 2D) = "bump" {}
        _DetailMask ("Detail Mask", 2D) = "black" {}
        _LineMask ("Line Mask", 2D) = "black" {}
        _EmissionMask ("Emission Mask", 2D) = "black" {}
        [Gamma]_EmissionColor("Emission Color", Color) = (1, 1, 1, 1)
        _EmissionIntensity("Emission Intensity", Float) = 1
        [Gamma]_ShadowColor ("Shadow Color", Vector) = (0.628,0.628,0.628,1)
        [Gamma]_SpecularColor ("Specular Color", Color) = (1,1,1,1)
        _SpecularPower ("Specular Power", Range(0, 1)) = 0
        _SpeclarHeight ("Speclar Height", Range(0, 1)) = 0.98
        _rimpower ("Rim Width", Range(0, 1)) = 0.5
        _rimV ("Rim Strength", Range(0, 1)) = 0.5
        _ShadowExtend ("Shadow Extend", Range(0, 1)) = 1
        _ShadowExtendAnother ("Shadow Extend Another", Range(0, 1)) = 1
        [MaterialToggle] _AnotherRampFull ("Another Ramp Full", Float) = 0
        [MaterialToggle] _DetailBLineG ("DetailB LineG", Float) = 0
        [MaterialToggle] _DetailRLineR ("DetailR LineR", Float) = 0
        [MaterialToggle] _notusetexspecular ("not use tex specular", Float) = 0
        _LineWidthS ("LineWidthS", Float) = 1
        _Clock ("Clock(xy/piv)(z/ang)(w/spd)", Vector) = (0,0,0,0)
        _ColorMask ("Color Mask", 2D) = "black" {}
        [Gamma]_Color ("Color", Color) = (1,0,0,1)
        [Gamma]_Color2 ("Color2", Color) = (0.1172419,0,1,1)
        [Gamma]_Color3 ("Color3", Color) = (0.5,0.5,0.5,1)
        [Gamma]_CustomAmbient("Custom Ambient", Color) = (0.666666666, 0.666666666, 0.666666666, 1)
        _NormalMapScale ("NormalMapScale", Float) = 1
        _DetailNormalMapScale ("Detail Normal Scale", Float) = 1
        [MaterialToggle] _UseRampForLights ("Use Ramp For Light", Float) = 1
        [MaterialToggle] _UseRampForSpecular ("Use Ramp For Specular", Float) = 0
        [MaterialToggle] _UseLightColorSpecular ("Use Light Color Specular", Float) = 1
        [HideInInspector] _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        [Enum(Off,0,On,1)]_AlphaOptionZWrite ("ZWrite", Float) = 0.0
        // Out-of-body stencil control for the split-out SOLID ovary slot: the ovary writes a
        // "protect" bit (Ref=32, WriteMask=32) so the interior skips its pixels; the uterus
        // leaves both 0 (no write, stays see-through). Comp uses ReadMask 31 so Ref's bit-5
        _OutStencilRef ("OutBody Stencil Ref", Float) = 0
        _OutStencilWriteMask ("OutBody Stencil WriteMask", Float) = 0
        // In-body Forward ZTest (uterus=8 Always for x-ray-through-organ; the split ovary
        // sets 3 Equal so only its nearest surface draws -> no self-clip). And the MarkProtect
        // pass writes bit5 on the ovary footprint (ovary: Ref=37, WriteMask=32; uterus: 0).
        _OrganZTest ("Organ Forward ZTest", Float) = 8
        _ProtectRef ("Protect Stencil Ref", Float) = 5
        _ProtectWriteMask ("Protect Stencil WriteMask", Float) = 0
        [Enum(Off,0,On,1)]_AlphaOptionCutoff ("Cutoff On", Float) = 0.0
        [Enum(Off,0,Front,1,Back,2)] _CullOption ("Cull Option", Range(0, 2)) = 2
        _Alpha ("AlphaValue", Float) = 1
        // Where the output alpha comes from. 1 (default) = mainTex.a * _Alpha (the
        // original x-ray look; the uterus keeps this). 0 = _Alpha only, IGNORING the
        // texture alpha - used by the split-out SOLID ovary slot so that at _Alpha=1
        // (protect bit5); if the fill were semi-transparent the womb shell behind it
        _AlphaFromTex ("Alpha From Texture (1=tex*Alpha, 0=Alpha only)", Range(0, 1)) = 1.0
        // Ovary interior-visibility mode (the split-out ovary/tube slot uses this).
        // 0 (default, uterus) = LEGACY CARVE: MarkProtect leaves the ovary FRONT-wall
        // depth over its footprint (bit5), so the interior is hidden everywhere
        // 1 = FARTHEST back wall: gate on the outermost back face. See into hollow
        [Enum(Carve,0,Farthest,1,Nearest,2)] _OvaryGateMode ("Ovary Interior Gate (0=carve,1=far,2=near)", Float) = 0
        [Enum(Off,0,On,1)] _OutBodyBackOcclude ("Outside-Body Back-Wall Occlude (uterus)", Float) = 0
        // Scene-depth confinement of the out-of-body depth wipe: where an OPAQUE scene surface
        [Enum(Off,0,On,1)] _OutBodySceneConfine ("Outside-Body Scene Confine (uterus)", Float) = 0
        _OutBodySceneBias ("Outside-Body Scene Confine bias (m)", Float) = 0.015
        [MaterialToggle] _UseDetailRAsSpecularMap ("Use DetailR as Specular Map", Float) = 0
        _Reflective("Reflective", Range(0, 1)) = 0.75
        _ReflectiveBlend("Reflective Blend", Range(0, 1)) = 0.05
        _ReflectiveMulOrAdd("Mul Or Add", Range(0, 1)) = 1
        _UseKKMetal("Use KK Metal", Range(0, 1)) = 1
        _UseMatCapReflection("Use Mat Cap", Range(0, 1)) = 1
        _ReflectionMapCap("Mat Cap", 2D) = "black" {}
        _UseKKPRim ("Use KKP Rim", Range(0 ,1)) = 0
        [Gamma]_KKPRimColor ("Body Rim Color", Color) = (1.0, 1.0, 1.0, 1.0)
        _KKPRimSoft ("Body Rim Softness", Float) = 1.5
        _KKPRimIntensity ("Body Rim Intensity", Float) = 0.75
        _KKPRimAsDiffuse ("Body Rim As Diffuse", Range(0, 1)) = 0.0
        _KKPRimRotateX("Body Rim Rotate X", Float) = 0.0
        _KKPRimRotateY("Body Rim Rotate Y", Float) = 0.0
        // Stencil pairing - must match the BodyReveal material on the same body mesh.
        // _StencilBody_Plus_1 must equal _StencilBody + 1.
        // 4-slot pair scheme - body stencils are multiples of 4 (40, 44, 48, ...).
        // Body = base, Organ = base+1, OrgInside = base+2. Slot base+3 is wasted
        [IntRange] _StencilBody  ("Stencil: Body Ref (pairs: 4/8/12/16; +bit for windowed)",   Range(0, 255)) = 4
        [IntRange] _StencilBody_Plus_1 ("Stencil: Organ Ref (= Body + 1)",       Range(0, 255)) = 5
        // Default 31 = gate on the low-5 region only (unchanged). A WINDOWED organ (e.g. a stomach revealed only
        // under an x-ray plane) sets this to 31|bit (159 = 31|128) and _StencilBody/_Plus_1 to region|bit
        // (132/133 = 4|128, 5|128), so the in-body chain fires only where the region AND the plane-stamped bit coincide.
        [IntRange] _StencilReadMask ("Stencil Read Mask (31 = region only)", Range(1, 255)) = 31
        // Optional flat-colored outline drawn around the organ silhouette.
        // _XrayOutlineWidth = 0 -> no outline. Width is in NDC units, ~0.005 ≈ 5px
        // on a 1080p screen. Outline color includes alpha for transparency.
        _XrayOutlineWidth ("X-ray Outline Width (NDC, 0=off)", Range(0, 0.05)) = 0.0
        _XrayOutlineColor ("X-ray Outline Color (alpha = transparency)", Color) = (1, 1, 1, 1)
        // Outline color blend with organ's _Color. 0 = pure XrayOutlineColor,
        // 1 = XrayOutlineColor multiplied by organ Color (tinted outline).
        _XrayOutlineColorBlend ("Outline: blend w/ organ Color", Range(0, 1)) = 0.0
        // Outline cull mode. Stencil-dedup (bit 7) is always on in the Outline
        [Enum(Off,0,Front,1,Back,2)] _XrayOutlineCull ("Outline Cull (Off=solid, Front=ring)", Range(0, 2)) = 0
        // Extrusion space. World (default) = vertex pushed along world normal
        [Enum(World,0,NDC,1)] _XrayOutlineExtrusionMode ("Outline Extrusion (World=zoom, NDC=screen)", Range(0, 1)) = 0
        // When the organ mesh extends beyond the body silhouette, this slider
        // controls how visible the "poking out" parts are. 0 = invisible
        // (default, organ stays hidden outside body), 1 = fully visible.
        _OutsideOfBodyAlpha ("Outside-Body Visibility", Range(0, 1)) = 0.0
        // X-ray ALPHA-TO-BLACK. In the plane / x-ray-screen view the framebuffer behind the organ is the BODY SKIN
        [Enum(Off,0,On,1)] _XrayAlphaToBlack ("X-ray: Alpha fades to black (plane view)", Float) = 0.0
        // Brightness multiplier on the final lit color. 1 = normal, < 1 darker,
        // > 1 brighter. Lets you tune organ visibility independently of Alpha.
        _Brightness ("Brightness", Range(0, 3)) = 1.0
    }
    SubShader
    {
        LOD 600
        Tags { "Queue" = "Transparent+500" "RenderType" = "Transparent" }

        // Shared lighting code - vertOrgan/fragOrgan used by the Forward pass.
        CGINCLUDE
        #include "UnityCG.cginc"
        #include "AutoLight.cginc"
        #include "Lighting.cginc"
        #define KKP_EXPENSIVE_RAMP
        #include "Includes/KKPItemInput.cginc"
        #include "Includes/KKPItemDiffuse.cginc"
        #include "Includes/KKPItemNormals.cginc"
        #include "Includes/KKPItemCoom.cginc"
        #include "Includes/KKPVertexLights.cginc"
        #include "Includes/KKPVertexLightsSpecular.cginc"
        #include "Includes/KKPEmission.cginc"
        #include "Includes/KKPReflect.cginc"

        float3 AmbientShadowAdjust(){
            float4 u_xlat5; float4 u_xlat6; float u_xlat30; bool u_xlatb30; float u_xlat31;
            u_xlatb30 = _ambientshadowG.y>=_ambientshadowG.z;
            u_xlat30 = u_xlatb30 ? 1.0 : float(0.0);
            u_xlat5.xy = _ambientshadowG.yz; u_xlat5.z = float(0.0); u_xlat5.w = float(-0.333333343);
            u_xlat6.xy = _ambientshadowG.zy; u_xlat6.z = float(-1.0); u_xlat6.w = float(0.666666687);
            u_xlat5 = u_xlat5 + (-u_xlat6);
            u_xlat5 = (u_xlat30) * u_xlat5.xywz + u_xlat6.xywz;
            u_xlatb30 = _ambientshadowG.x>=u_xlat5.x;
            u_xlat30 = u_xlatb30 ? 1.0 : float(0.0);
            u_xlat6.z = u_xlat5.w; u_xlat5.w = _ambientshadowG.x; u_xlat6.xyw = u_xlat5.wyx;
            u_xlat6 = (-u_xlat5) + u_xlat6; u_xlat5 = (u_xlat30) * u_xlat6 + u_xlat5;
            u_xlat30 = min(u_xlat5.y, u_xlat5.w); u_xlat30 = (-u_xlat30) + u_xlat5.x;
            u_xlat30 = u_xlat30 * 6.0 + 1.00000001e-10;
            u_xlat31 = (-u_xlat5.y) + u_xlat5.w; u_xlat30 = u_xlat31 / u_xlat30;
            u_xlat30 = u_xlat30 + u_xlat5.z;
            u_xlat5.xyz = abs((u_xlat30)) + float3(0.0, -0.333333343, 0.333333343);
            u_xlat5.xyz = frac(u_xlat5.xyz);
            u_xlat5.xyz = (-u_xlat5.xyz) * float3(2.0, 2.0, 2.0) + float3(1.0, 1.0, 1.0);
            u_xlat5.xyz = abs(u_xlat5.xyz) * float3(3.0, 3.0, 3.0) + float3(-1.0, -1.0, -1.0);
            u_xlat5.xyz = clamp(u_xlat5.xyz, 0.0, 1.0);
            u_xlat5.xyz = u_xlat5.xyz * float3(0.400000006, 0.400000006, 0.400000006) + float3(0.300000012, 0.300000012, 0.300000012);
            return u_xlat5.xyz;
        }

        // CloXray-specific uniforms (declared up front so all functions below
        // can reference them).
        float _Brightness;
        float _OutsideOfBodyAlpha;
        float _XrayAlphaToBlack;
        float _AlphaFromTex;
        float _OvaryGateMode;
        float _OutBodyBackOcclude;
        float _OutBodySceneConfine;
        float _OutBodySceneBias;

        Varyings vertOrgan(VertexData v)
        {
            Varyings o;
            o.posWS = mul(unity_ObjectToWorld, v.vertex);
            o.posCS = mul(UNITY_MATRIX_VP, o.posWS);
            o.normalWS = UnityObjectToWorldNormal(v.normal);
            o.tanWS = float4(UnityObjectToWorldDir(v.tangent.xyz), v.tangent.w);
            float3 biTan = cross(o.tanWS, o.normalWS);
            o.bitanWS = normalize(biTan);
            o.uv0 = v.uv0;
        #ifdef SHADOWS_SCREEN
            float4 projPos = o.posCS;
            projPos.y *= _ProjectionParams.x;
            float4 projbiTan;
            projbiTan.xyz = biTan;
            projbiTan.xzw = projPos.xwy * 0.5;
            o.shadowCoordinate.zw = projPos.zw;
            o.shadowCoordinate.xy = projbiTan.zz + projbiTan.xw;
        #endif
            return o;
        }


        float3x3 AngleAxis3x3(float angle, float3 axis)
        {
            float c, s; sincos(angle, s, c); float t = 1 - c;
            float x = axis.x, y = axis.y, z = axis.z;
            return float3x3(t*x*x+c, t*x*y-s*z, t*x*z+s*y, t*x*y+s*z, t*y*y+c, t*y*z-s*x, t*x*z-s*y, t*y*z+s*x, t*z*z+c);
        }

        fixed4 fragOrgan(Varyings i, int faceDir : VFACE) : SV_Target
        {
            float4 mainTex = tex2D(_MainTex, i.uv0 * _MainTex_ST.xy + _MainTex_ST.zw);
            AlphaClip(i.uv0, mainTex.a);

            float3 worldLightPos = normalize(_WorldSpaceLightPos0.xyz);
            float3 viewDir = normalize(_WorldSpaceCameraPos.xyz - i.posWS);
            float3 halfDir = normalize(viewDir + worldLightPos);

            float4 colorMask = tex2D(_ColorMask, i.uv0 * _ColorMask_ST.xy + _ColorMask_ST.zw);
            float3 color;
            color = colorMask.r * (_Color.rgb - 1) + 1;
            color = colorMask.g * (_Color2.rgb - color) + color;
            color = colorMask.b * (_Color3.rgb - color) + color;
            float3 diffuse = mainTex * color;

            float3 normal = NormalAdjust(i, GetNormal(i), 1);

            float3x3 rotX = AngleAxis3x3(_KKPRimRotateX, float3(0, 1, 0));
            float3x3 rotY = AngleAxis3x3(_KKPRimRotateY, float3(1, 0, 0));
            float3 adjustedViewDir = faceDir == 1 ? viewDir : -viewDir;
            float3 rotView = mul(adjustedViewDir, mul(rotX, rotY));
            float kkpFres = dot(normal, rotView);
            kkpFres = saturate(pow(1-kkpFres, _KKPRimSoft) * _KKPRimIntensity);
            _KKPRimColor.a *= (_UseKKPRim);
            float3 kkpFresCol = kkpFres * _KKPRimColor;
            diffuse = lerp(diffuse, kkpFresCol, _KKPRimColor.a * kkpFres * _KKPRimAsDiffuse);

            float time = _TimeEditor.y + _Time.y;
            time *= _Clock.z * _Clock.w;
            float sinTime = sin(time); float cosTime = cos(time);
            float3 rotVal = float3(-sinTime, cosTime, sinTime);
            float2 detailUVAdjust = i.uv0 - _Clock.xy;
            float2 rotatedDetailUV;
            rotatedDetailUV.x = dot(detailUVAdjust, rotVal.yz);
            rotatedDetailUV.y = dot(detailUVAdjust, rotVal.xy);
            rotatedDetailUV += _Clock.xy;
            rotatedDetailUV = rotatedDetailUV * _LineMask_ST.xy + _LineMask_ST.zw;
            float4 lineMaskRot = tex2D(_LineMask, rotatedDetailUV);
            diffuse = lineMaskRot.b * -diffuse + diffuse;
            float3 shadingAdjustment = ShadeAdjustItem(diffuse);

            float2 detailUV = i.uv0 * _DetailMask_ST.xy + _DetailMask_ST.zw;
            float4 detailMask = tex2D(_DetailMask, detailUV);
            float2 lineMaskUV = i.uv0 * _LineMask_ST.xy + _LineMask_ST.zw;
            float4 lineMask = tex2D(_LineMask, lineMaskUV);
            lineMask.r = _DetailRLineR * (detailMask.r - lineMask.r) + lineMask.r;

            float3 diffuseShaded = shadingAdjustment * 0.899999976 - 0.5;
            diffuseShaded = -diffuseShaded * 2 + 1;
            float4 ambientShadow = 1 - _ambientshadowG.wxyz;
            float3 ambientShadowIntensity = -ambientShadow.x * ambientShadow.yzw + 1;
            float ambientShadowAdjust = _ambientshadowG.w * 0.5 + 0.5;
            float ambientShadowAdjustDoubled = ambientShadowAdjust + ambientShadowAdjust;
            bool ambientShadowAdjustShow = 0.5 < ambientShadowAdjust;
            ambientShadow.rgb = ambientShadowAdjustDoubled * _ambientshadowG.rgb;
            float3 finalAmbientShadow = ambientShadowAdjustShow ? ambientShadowIntensity : ambientShadow.rgb;
            finalAmbientShadow = saturate(finalAmbientShadow);
            float3 invertFinalAmbientShadow = 1 - finalAmbientShadow;
            bool3 compTest = 0.555555582 < shadingAdjustment;
            shadingAdjustment *= finalAmbientShadow;
            shadingAdjustment *= 1.79999995;
            diffuseShaded = -diffuseShaded * invertFinalAmbientShadow + 1;
            {
                float3 hlslcc_movcTemp = shadingAdjustment;
                hlslcc_movcTemp.x = (compTest.x) ? diffuseShaded.x : shadingAdjustment.x;
                hlslcc_movcTemp.y = (compTest.y) ? diffuseShaded.y : shadingAdjustment.y;
                hlslcc_movcTemp.z = (compTest.z) ? diffuseShaded.z : shadingAdjustment.z;
                shadingAdjustment = saturate(hlslcc_movcTemp);
            }
            float shadowExtendAnother = 1 - _ShadowExtendAnother;
            float kkMetal = _AnotherRampFull * (1 - lineMask.r) + lineMask.r;
            float kkMetalMap = kkMetal;
            kkMetal *= _UseKKMetal;
            shadowExtendAnother -= kkMetal;
            shadowExtendAnother += 1;
            shadowExtendAnother = saturate(shadowExtendAnother) * 0.670000017 + 0.330000013;
            float3 shadowExtendShaded = shadowExtendAnother * shadingAdjustment;
            shadingAdjustment = -shadingAdjustment * shadowExtendAnother + 1;
            float3 diffuseShadow = diffuse * shadowExtendShaded;
            float3 diffuseShadowBlended = -shadowExtendShaded * diffuse + diffuse;

            KKVertexLight vertexLights[4];
        #ifdef VERTEXLIGHT_ON
            GetVertexLights(vertexLights, i.posWS);
        #endif
            float4 vertexLighting = 0.0;
            float vertexLightRamp = 1.0;
        #ifdef VERTEXLIGHT_ON
            vertexLighting = GetVertexLighting(vertexLights, normal);
            float2 vertexLightRampUV = vertexLighting.a * _RampG_ST.xy + _RampG_ST.zw;
            vertexLightRamp = tex2D(_RampG, vertexLightRampUV).x;
            float3 rampLighting = GetRampLighting(vertexLights, normal, vertexLightRamp);
            vertexLighting.rgb = _UseRampForLights ? rampLighting : vertexLighting.rgb;
        #endif
            float lambert = dot(_WorldSpaceLightPos0.xyz, normal);
            lambert = max(lambert, vertexLighting.a);
            float2 rampUV = lambert * _RampG_ST.xy + _RampG_ST.zw;
            float ramp = tex2D(_RampG, rampUV);
            float fresnel = max(dot(normal, viewDir), 0.0);
            fresnel = log2(1 - fresnel);
            float specular = dot(normal, halfDir);
            specular = max(specular, 0.0);
            float anotherRampSpecularVertex = 0.0;
        #ifdef VERTEXLIGHT_ON
            [unroll]
            for(int j = 0; j < 4; j++){
                KKVertexLight light = vertexLights[j];
                float3 halfVector = normalize(viewDir + light.dir) * saturate(MaxGrayscale(light.col));
                anotherRampSpecularVertex = max(anotherRampSpecularVertex, dot(halfVector, normal));
            }
        #endif
            float2 anotherRampUV = max(specular, anotherRampSpecularVertex) * _AnotherRamp_ST.xy + _AnotherRamp_ST.zw;
            float anotherRamp = tex2D(_AnotherRamp, anotherRampUV);
            specular = log2(specular);
            anotherRamp -= ramp;
            float finalRamp = kkMetal * anotherRamp + ramp;
        #ifdef SHADOWS_SCREEN
            float2 shadowMapUV = i.shadowCoordinate.xy / i.shadowCoordinate.ww;
            float4 shadowMap = tex2D(_ShadowMapTexture, shadowMapUV);
            float shadowAttenuation = saturate(shadowMap.x * 2.0 - 1.0);
            finalRamp *= shadowAttenuation;
        #endif
            diffuseShadow = finalRamp * diffuseShadowBlended + diffuseShadow;
            float specularHeight = _SpeclarHeight - 1.0;
            specularHeight *= 0.800000012;
            float2 detailSpecularOffset;
            detailSpecularOffset.x = dot(i.tanWS, viewDir);
            detailSpecularOffset.y = dot(i.bitanWS, viewDir);
            float2 detailMaskUV2 = specularHeight * detailSpecularOffset + i.uv0;
            detailMaskUV2 = detailMaskUV2 * _DetailMask_ST.xy + _DetailMask_ST.zw;
            float drawnSpecular = tex2D(_DetailMask, detailMaskUV2).x;
            float drawnSpecularSquared = min(drawnSpecular * drawnSpecular, 1.0);
            _SpecularPower *= _UseDetailRAsSpecularMap ? detailMask.x : 1;
            float specularPower = _SpecularPower * 256.0;
            specular *= specularPower;
            specular = exp2(specular) * 5.0 - 4.0;
            drawnSpecular = saturate(specular * _SpecularPower + drawnSpecularSquared);
        #ifdef KKP_EXPENSIVE_RAMP
            float2 lightRampUV = specular * _RampG_ST.xy + _RampG_ST.zw;
            specular = tex2D(_RampG, lightRampUV) * _UseRampForSpecular + specular * (1 - _UseRampForSpecular);
        #endif
            specular = saturate(specular * _SpecularPower);
            specular = specular - drawnSpecular;
            specular = _notusetexspecular * specular + drawnSpecular;
            float specularVertex = 0.0;
            float3 specularVertexCol = 0.0;
        #ifdef VERTEXLIGHT_ON
            specularVertex = GetVertexSpecularDiffuse(vertexLights, normal, viewDir, _SpecularPower, specularVertexCol);
        #endif
            float3 specularCol = saturate(specular) * _SpecularColor.rgb + saturate(specularVertex) * specularVertexCol * _notusetexspecular;
            specularCol *= _SpecularColor.a;
            float3 ambientShadowAdjust2 = AmbientShadowAdjust();
            detailMask.rg = 1 - detailMask.bg;
            float rimPow = _rimpower * 9.0 + 1.0;
            rimPow = rimPow * fresnel;
            float rim = saturate(exp2(rimPow) * 2.5 - 0.5) * _rimV;
            float rimMask = detailMask.x * 9.99999809 + -8.99999809;
            rim *= rimMask;
            ambientShadowAdjust2 *= rim;
            ambientShadowAdjust2 *= detailMask.g;
            ambientShadowAdjust2 = min(max(ambientShadowAdjust2, 0.0), 0.5);
            diffuseShadow += ambientShadowAdjust2;
            float3 lightCol = (_LightColor0.xyz + vertexLighting.rgb * vertexLightRamp) * float3(0.600000024, 0.600000024, 0.600000024) + _CustomAmbient.rgb;
            float3 ambientCol = max(lightCol, _ambientshadowG.xyz);
            diffuseShadow = diffuseShadow * ambientCol;
            float shadowExtend = _ShadowExtend * -1.20000005 + 1.0;
            float drawnShadow = detailMask.y * (1 - shadowExtend) + shadowExtend;
            float detailLineShadow = 1 - detailMask.x;
            detailLineShadow -= lineMask.y;
            detailLineShadow = _DetailBLineG * detailLineShadow + lineMask.y;
            shadingAdjustment = drawnShadow * shadingAdjustment + shadowExtendShaded;
            shadingAdjustment *= diffuseShadow;
            diffuse = diffuse * _LineColorG;
            float3 lineCol = -diffuse * shadowExtendShaded + 1;
            diffuse *= shadowExtendShaded;
            float lineAlpha = _LineColorG.w - 0.5;
            lineAlpha = -lineAlpha * 2.0 + 1.0;
            lineCol = -lineAlpha * lineCol + 1;
            lineAlpha = _LineColorG.w * 2;
            diffuse *= lineAlpha;
            diffuse = 0.5 < _LineColorG.w ? lineCol : diffuse;
            diffuse = saturate(diffuse);
            diffuse = -shadingAdjustment + diffuse;
            float3 finalDiffuse = detailLineShadow * diffuse + shadingAdjustment;
            finalDiffuse += specularCol;
            finalDiffuse = GetBlendReflections(finalDiffuse, normal, viewDir, kkMetalMap);
            finalDiffuse = lerp(finalDiffuse, kkpFresCol, _KKPRimColor.a * kkpFres * (1 - _KKPRimAsDiffuse));
            float4 emission = GetEmission(i.uv0);
            finalDiffuse = finalDiffuse * (1 - emission.a) + (emission.a * emission.rgb);
            // _AlphaFromTex: 1 = tex*Alpha (uterus x-ray); 0 = Alpha only (solid ovary,
            // opaque at _Alpha=1 so the carved interior hole shows no womb show-through).
            return float4(finalDiffuse * _Brightness, lerp(_Alpha, mainTex.a * _Alpha, _AlphaFromTex));
        }

        // Outside-body variant: same full KK lighting, alpha additionally
        // the organ that poke out beyond the body silhouette.
        fixed4 fragOrganOutside(Varyings i, int faceDir : VFACE) : SV_Target
        {
            fixed4 c = fragOrgan(i, faceDir);
            c.a *= _OutsideOfBodyAlpha;
            return c;
        }

        // Forward-pass wrapper. Default: returns fragOrgan and the pass's Blend SrcAlpha OneMinusSrcAlpha does
        // normal alpha blending with the framebuffer. With _XrayAlphaToBlack=1 it instead premultiplies rgb*alpha
        fixed4 fragOrganXray(Varyings i, int faceDir : VFACE) : SV_Target
        {
            fixed4 c = fragOrgan(i, faceDir);
            // X-ray Alpha-to-black: fade organ toward black by _Alpha (premultiplied, opaque) so it reads as
            // semi-transparent over the black x-ray backdrop instead of washing out against the body skin.
            if (_XrayAlphaToBlack > 0.5) return fixed4(c.rgb * saturate(c.a), 1.0);
            return c;  // traditional alpha blend (the Forward pass does Blend SrcAlpha OneMinusSrcAlpha)
        }

        ENDCG

        // ----------------------------------------------------------------
        // Pass 0: DepthClear
        // At body-stencil pixels (43), push depth to far plane (1.0).
        // "behind" the body). Only fires at stencil==43, so once an organ
        Pass
        {
            Name "DepthClear"
            ZTest Always
            ZWrite On
            ColorMask 0
            Cull [_CullOption]

            Stencil
            {
                Ref [_StencilBody]
                ReadMask [_StencilReadMask]
                Comp Equal
                Pass Keep
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };
            v2f vert(appdata v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // Push to FAR plane. Unity 5.6 on DX11+ uses reversed-Z
                // (near=1.0, far=0.0), so far plane needs z=0 in NDC.
                // For non-reversed-Z platforms, far plane needs z=w (NDC=1).
                #if UNITY_REVERSED_Z
                    o.pos.z = 0;
                #else
                    o.pos.z = o.pos.w;
                #endif
                return o;
            }
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 1: DepthWrite
        Pass
        {
            Name "DepthWrite"
            ZTest Less
            ZWrite On
            ColorMask 0
            Cull [_CullOption]

            Stencil
            {
                Ref [_StencilBody]
                ReadMask [_StencilReadMask]
                Comp LEqual
                Pass Keep
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 2: StencilWrite
        // Stamp stencil 43 -> 44 at body pixels covered by this organ.
        Pass
        {
            Name "StencilWrite"
            ZTest Always
            ZWrite Off
            ColorMask 0
            Cull [_CullOption]

            Stencil
            {
                Ref [_StencilBody]
                ReadMask [_StencilReadMask]
                WriteMask 31
                Comp Equal
                Pass IncrSat
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass: MarkProtect - runs after StencilWrite (stencil == organ here). The split-out
        // SOLID ovary slot sets the protect bit (bit5) over its footprint (Ref=37, WriteMask=32:
        // Comp tests low5==organ via ReadMask 31, Replace writes only bit5). The interior's
        Pass
        {
            Name "MarkProtect"
            // ZTest = _OrganZTest (ovary=Equal): mark bit5 only where the ovary is the nearest
            ZTest [_OrganZTest]
            ZWrite Off
            ColorMask 0
            Cull [_CullOption]

            Stencil
            {
                Ref [_ProtectRef]
                ReadMask 31
                WriteMask [_ProtectWriteMask]
                Comp Equal
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            // _Alpha / _AlphaFromTex come from the shared CGINCLUDE (auto-included in
            // every pass), so they're available here without re-declaration.
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                // Gate the protect bit by visibility: when the ovary is effectively
                // invisible (slider _Alpha ~ 0) DISCARD so the stencil Replace never
                // runs -> bit5 not written -> the interior is NOT carved -> no silhouette
                // (For the ovary _AlphaFromTex=0 -> its visible alpha is exactly _Alpha.)
                if (_Alpha < 0.004) discard;
                return 0;
            }
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 3: Forward - full KK lighting, alpha-blended on body
        // ZWrite Off: don't disturb the primed organ depth (the liquid sorts against it).
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "ForwardBase" }
            ZTest [_OrganZTest]
            ZWrite Off
            // Blend SrcAlpha OneMinusSrcAlpha works for both fragOrganXray outputs:
            // • _XrayAlphaToBlack=1: (rgb*alpha, 1) -> 1*src + 0*dst = src, opaque fade-to-black.
            Blend SrcAlpha OneMinusSrcAlpha
            Cull [_CullOption]

            Stencil
            {
                Ref [_StencilBody_Plus_1]
                ReadMask [_StencilReadMask]
                Comp Equal
                Pass Keep
            }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vertOrgan
            #pragma fragment fragOrganXray
            #pragma multi_compile _ VERTEXLIGHT_ON
            #pragma multi_compile _ SHADOWS_SCREEN
            ENDCG
        }

        // ================================================================
        // BACK-WALL DEPTH GATE (3 passes). The interior (OrgInside, queue 3502)
        // (bit5): interior shows where it's IN FRONT of that depth, hidden where behind.
        // Gated by stencil bit5 (set by MarkProtect only where the ovary is the visible

        // Pass: BackDepthClear (mode 2 only) - push the bit5 footprint depth to FAR so
        // the following ZTest Less can select the nearest back face.
        Pass
        {
            Name "BackDepthClear"
            ZTest Always
            ZWrite On
            ColorMask 0
            Cull Back   // front faces cover the footprint

            Stencil { Ref 32 ReadMask 32 Comp Equal Pass Keep }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            // _OvaryGateMode / _Alpha come from the shared CGINCLUDE (auto-included).
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };
            v2f vert(appdata v)
            {
                v2f o; o.pos = UnityObjectToClipPos(v.vertex);
                #if UNITY_REVERSED_Z
                    o.pos.z = 0;
                #else
                    o.pos.z = o.pos.w;
                #endif
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                if (_OvaryGateMode < 1.5) discard;   // only mode 2
                if (_Alpha < 0.004) discard;
                return 0;
            }
            ENDCG
        }

        // Pass: BackDepthFar (mode 1 only) - ZTest Greater keeps the FARTHEST back face
        // (order-independent). Buffer still holds the front-wall depth here (mode 1 does
        Pass
        {
            Name "BackDepthFar"
            ZTest Greater
            ZWrite On
            ColorMask 0
            Cull Front

            Stencil { Ref 32 ReadMask 32 Comp Equal Pass Keep }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            // _OvaryGateMode / _Alpha come from the shared CGINCLUDE (auto-included).
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                if (abs(_OvaryGateMode - 1.0) > 0.5) discard;   // only mode 1
                if (_Alpha < 0.004) discard;
                return 0;
            }
            ENDCG
        }

        // Pass: BackDepthNear (mode 2 only) - after BackDepthClear pushed the footprint
        Pass
        {
            Name "BackDepthNear"
            ZTest Less
            ZWrite On
            ColorMask 0
            Cull Front

            Stencil { Ref 32 ReadMask 32 Comp Equal Pass Keep }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            // _OvaryGateMode / _Alpha come from the shared CGINCLUDE (auto-included).
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                if (_OvaryGateMode < 1.5) discard;   // only mode 2
                if (_Alpha < 0.004) discard;
                return 0;
            }
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 4: OutsideBody - organ poking outside the body silhouette (stencil 0).
        Pass
        {
            Name "OutsideBody"
            Tags { "LightMode" = "ForwardBase" }
            ZTest LEqual
            ZWrite [_AlphaOptionZWrite]
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Back

            Stencil { Ref [_OutStencilRef] ReadMask 31 WriteMask [_OutStencilWriteMask] Comp Equal Pass Replace }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vertOrgan
            #pragma fragment fragOrganOutside
            #pragma multi_compile _ VERTEXLIGHT_ON
            #pragma multi_compile _ SHADOWS_SCREEN
            ENDCG
        }

        // ================================================================
        // outside-BODY far-ovary occluder - only when _OutBodyBackOcclude=1 (set on the
        // UTERUS) and only outside body (stencil low5 == 0). The womb shell's OutsideBody

        // Pass: OutBodyBackClear - push the womb footprint depth to the NEAR plane.
        Pass
        {
            Name "OutBodyBackClear"
            ZTest Always
            ZWrite On
            ColorMask 0
            Cull Back   // front faces cover the footprint

            Stencil { Ref 0 ReadMask 31 Comp Equal Pass Keep }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            // _OutBodyBackOcclude / _OutBodySceneConfine / _Alpha come from the shared CGINCLUDE.
            sampler2D _CameraDepthTexture;
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; float4 spos : TEXCOORD0; float eyeZ : TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o; o.pos = UnityObjectToClipPos(v.vertex);
                // Scene-confine probe data from the UNPUSHED position (the NEAR push below
                // destroys the fragment's own depth, so capture screen uv + eye depth first).
                o.spos = ComputeScreenPos(o.pos);
                o.eyeZ = -UnityObjectToViewPos(v.vertex).z;   // womb FRONT-wall linear eye depth (Cull Back)
                // NEAR plane (opposite of the FAR push DepthClear uses).
                #if UNITY_REVERSED_Z
                    o.pos.z = o.pos.w;   // reversed-Z near = 1.0
                #else
                    o.pos.z = 0;
                #endif
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                if (_OutBodyBackOcclude < 0.5) discard;
                if (_Alpha < 0.004) discard;
                // Scene-depth confinement: an opaque scene surface in FRONT of the womb's front
                if (_OutBodySceneConfine >= 0.5)
                {
                    float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.spos.xy / i.spos.w));
                    if (sceneEye < i.eyeZ - _OutBodySceneBias) discard;
                }
                return 0;
            }
            ENDCG
        }

        // Pass: OutBodyBackDepth - ZTest Greater (vs the just-cleared NEAR plane) writes
        // the FARTHEST womb back wall (true outer rear), the gate the far ovary fails.
        Pass
        {
            Name "OutBodyBackDepth"
            ZTest Greater
            ZWrite On
            ColorMask 0
            Cull Front

            Stencil { Ref 0 ReadMask 31 Comp Equal Pass Keep }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            // _OutBodyBackOcclude / _OutBodySceneConfine / _Alpha come from the shared CGINCLUDE.
            sampler2D _CameraDepthTexture;
            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; float4 spos : TEXCOORD0; float eyeZ : TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o; o.pos = UnityObjectToClipPos(v.vertex);
                o.spos = ComputeScreenPos(o.pos);
                o.eyeZ = -UnityObjectToViewPos(v.vertex).z;   // this BACK-wall fragment's linear eye depth (Cull Front)
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                if (_OutBodyBackOcclude < 0.5) discard;
                if (_Alpha < 0.004) discard;
                // Scene-depth confinement: don't write the back wall over an opaque scene surface
                // that sits in front of it - the foreign depth must keep occluding (see Clear pass).
                if (_OutBodySceneConfine >= 0.5)
                {
                    float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.spos.xy / i.spos.w));
                    if (sceneEye < i.eyeZ - _OutBodySceneBias) discard;
                }
                return 0;
            }
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 5: Outline (optional flat-color rim around the organ)
        // Renders an extruded silhouette in _XrayOutlineColor, gated by stencil
        // so it only paints body pixels (Ref 43) - never on top of the organ
        Pass
        {
            Name "Outline"
            ZTest Always
            ZWrite Off
            Cull [_XrayOutlineCull]
            Blend SrcAlpha OneMinusSrcAlpha

            // First-fragment-wins dedup using stencil bit 7. Without this, every
            // extruded shell fragment at a pixel alpha-blends (so 3 overlapping
            // layers + alpha 0.5 -> looks like alpha 0.875, color drift, washout).
            // ReadMask 159 (=128+31) tests bits 0-4 AND bit 7.
            Stencil
            {
                Ref [_StencilBody]
                ReadMask 159
                WriteMask 128
                Comp Equal
                Pass Invert
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float  _XrayOutlineWidth;
            float4 _XrayOutlineColor;
            float  _XrayOutlineColorBlend;
            float  _XrayOutlineExtrusionMode;
            // _Color, _Color2, _Color3, _MainTex, _ColorMask, etc. are declared
            // by KKPItemInput.cginc (via CGINCLUDE) - available without redecl.

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;
            };
            struct v2f
            {
                float4 pos       : SV_POSITION;
                float2 uv        : TEXCOORD0;
                float3 normalWS  : TEXCOORD1;
                float3 worldPos  : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 worldNormal = UnityObjectToWorldNormal(v.normal);

                if (_XrayOutlineExtrusionMode < 0.5)
                {
                    // World-space extrusion. Width in meters, zoom-aware (outline
                    // shrinks as camera pulls back).
                    float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz
                                    + worldNormal * _XrayOutlineWidth;
                    o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1));
                    o.worldPos = worldPos;
                }
                else
                {
                    // NDC-space extrusion. Project the world normal into clip
                    float4 clipPos = UnityObjectToClipPos(v.vertex);
                    float3 clipNormal = mul((float3x3)UNITY_MATRIX_VP, worldNormal);
                    float2 ndcOffset = normalize(clipNormal.xy + 1e-6) * _XrayOutlineWidth;
                    // Multiply by w so width is in NDC (post-divide) units.
                    clipPos.xy += ndcOffset * clipPos.w;
                    o.pos = clipPos;
                    o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                }

                o.uv = v.uv;
                o.normalWS = worldNormal;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                // No-outline early-out. At width ≈ 0 the shell is not extruded
                // but its triangles still rasterize onto body-stencil pixels at
                if (_XrayOutlineWidth < 0.0001) discard;

                // Sample organ's actual color so the outline can tint with it.
                fixed4 mainTex = tex2D(_MainTex, i.uv * _MainTex_ST.xy + _MainTex_ST.zw);
                float4 colorMask = tex2D(_ColorMask, i.uv * _ColorMask_ST.xy + _ColorMask_ST.zw);
                float3 organColor = colorMask.r * (_Color.rgb - 1) + 1;
                organColor = colorMask.g * (_Color2.rgb - organColor) + organColor;
                organColor = colorMask.b * (_Color3.rgb - organColor) + organColor;
                fixed3 organDiffuse = mainTex.rgb * organColor;
                // Blend pure outline color toward (outline * organ-diffuse).
                fixed3 baseRGB = lerp(_XrayOutlineColor.rgb,
                                      _XrayOutlineColor.rgb * organDiffuse,
                                      _XrayOutlineColorBlend);

                // Solid outline - uniform alpha (dedup stencil keeps it single-layer).
                return fixed4(baseRGB, _XrayOutlineColor.a);
            }
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 5: ShadowCaster - invisible, no shadow casting
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
