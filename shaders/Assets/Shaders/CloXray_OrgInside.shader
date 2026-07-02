// CloXray/OrgInside
// For objects that cross the organ/body boundary (e.g. a tube entering the body, or the penis).
// Behaviour per pixel:
//   Outside body entirely  → renders normally (ZTest LEqual passes freely)
//   Inside body, outside organ → hidden (body depth in buffer; ZTest LEqual fails)
//   Inside organ (low-5 stencil region = 5, "body+1") → visible through body, multiple
//     OrgInside objects depth-sort correctly against each other.
//
// How it works (inside-organ case) — see XRAY_RENDER_MODEL.md §2 (stencil byte) and §3:
//   Pass 0 (DepthClear): where the organ region == 5, push depth to the FAR
//     plane and IncrSat the region 5 → 6. IncrSat fires only ONCE per pixel,
//     so subsequent OrgInside objects don't wipe each other's depth.
//   Pass 1 (DepthWrite): where the region >= 5 (5 or 6), ZTest Less + ZWrite On. Multiple
//     OrgInside objects compete here — only the closest one writes its depth.
//   Pass 2 (FORWARD): ZTest LEqual + ZWrite On, NO stencil pair check (Comp NotEqual
//     Ref 0, ReadMask 31). The depth test alone gates visibility:
//     – Outside body: buffer holds sky depth → LEqual passes → visible.
//     – Inside body, outside organ: buffer holds body depth (closer than this
//       object) → LEqual FAILS → hidden.
//     – Inside organ: depth was set to closest OrgInside in Pass 1 → only the
//       closest OrgInside passes LEqual at each pixel.
//
// Stencil = the low-5 region (see the [IntRange] pair sliders below): Pair A is
// body=4 / organ=5 / orginside-cavity=6. Multi-pair OrgInside (Pair B = 8/9/10, …)
// is the known unfinished frontier — not yet validated.
Shader "CloXray/OrgInside"
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
        [Enum(Off,0,On,1)]_AlphaOptionZWrite ("ZWrite", Float) = 1.0
        [Enum(Off,0,On,1)]_AlphaOptionCutoff ("Cutoff On", Float) = 0.0
        [Enum(Off,0,Front,1,Back,2)] _CullOption ("Cull Option", Range(0, 2)) = 2
        _Alpha ("AlphaValue", Float) = 1
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
        // 4-slot pair scheme. OrgInside writes Body+2 (orginside slot).
        //   Pair A: body=4, organ=5, orginside(cavity)=6
        //   Pair B: body=8, organ=9, orginside(cavity)=10
        //   Pair C: body=12, organ=13, orginside(cavity)=14
        //   Pair D: body=16, organ=17, orginside(cavity)=18
        [IntRange] _StencilBody_Plus_1 ("Stencil: Organ Ref (match Organ)", Range(2, 18)) = 5
        [IntRange] _StencilBody  ("Stencil: Body Ref (match BodyReveal)",  Range(1, 17)) = 4
        // When the OrgInside object is inside the body but OUTSIDE any organ,
        // it normally is hidden by body skin. This slider lets it show as a
        // semi-transparent ghost through the body. 0 = invisible (default,
        // matches old behavior), 1 = fully opaque. Try 0.2-0.4 for a faint ghost.
        _BehindBodyAlpha ("Behind-Body Visibility", Range(0, 1)) = 0.0
        // Optional flat-colored outline drawn around the OrgInside silhouette
        // wherever it sits inside the body region.
        _XrayOutlineWidth ("X-ray Outline Width (world units)", Range(0, 0.05)) = 0.0
        _XrayOutlineColor ("X-ray Outline Color (alpha=transparency)", Color) = (1, 1, 1, 1)
        // Visibility when the object is OUTSIDE any character body (stencil=0).
        // 1 = fully visible (default, matches old behavior). 0 = invisible.
        _OutsideOfBodyAlpha ("Outside-Body Visibility", Range(0, 1)) = 1.0
        // Open the x-ray window on up-the-open-canal rays from below (pixels the
        // uterus EXTERIOR never covered). 0 = off (DEFAULT; canal hidden by skin),
        // 1 = on (see up the open canal). The womb's own interior material bakes this
        // ON explicitly; plugin-applied OrgInside (e.g. the penis) inherits this 0 default.
        [Enum(Off,0,On,1)] _BottomWindow ("Bottom Window (see up the open canal)", Float) = 0
    }
    SubShader
    {
        LOD 600
        // Queue 3501: just after Organ (3500) which must write stencil=44 before OrgInside reads it
        Tags { "Queue" = "Transparent+501" "RenderType" = "Transparent" }

        // Shared full KK lighting code for Forward, BehindBody, OutsideBody.
        // fragOrgInside  = full lighting (used by Forward)
        // fragOrgInsideBehind  = full lighting * _BehindBodyAlpha (BehindBody)
        // fragOrgInsideOutside = full lighting * _OutsideOfBodyAlpha (OutsideBody)
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

        float _BehindBodyAlpha;
        float _OutsideOfBodyAlpha;

        float3 AmbientShadowAdjust(){
            float4 u_xlat5; float4 u_xlat6; float u_xlat30; bool u_xlatb30; float u_xlat31;
            u_xlatb30 = _ambientshadowG.y>=_ambientshadowG.z; u_xlat30 = u_xlatb30 ? 1.0 : float(0.0);
            u_xlat5.xy = _ambientshadowG.yz; u_xlat5.z = float(0.0); u_xlat5.w = float(-0.333333343);
            u_xlat6.xy = _ambientshadowG.zy; u_xlat6.z = float(-1.0); u_xlat6.w = float(0.666666687);
            u_xlat5 = u_xlat5 + (-u_xlat6); u_xlat5 = (u_xlat30) * u_xlat5.xywz + u_xlat6.xywz;
            u_xlatb30 = _ambientshadowG.x>=u_xlat5.x; u_xlat30 = u_xlatb30 ? 1.0 : float(0.0);
            u_xlat6.z = u_xlat5.w; u_xlat5.w = _ambientshadowG.x; u_xlat6.xyw = u_xlat5.wyx;
            u_xlat6 = (-u_xlat5) + u_xlat6; u_xlat5 = (u_xlat30) * u_xlat6 + u_xlat5;
            u_xlat30 = min(u_xlat5.y, u_xlat5.w); u_xlat30 = (-u_xlat30) + u_xlat5.x;
            u_xlat30 = u_xlat30 * 6.0 + 1.00000001e-10; u_xlat31 = (-u_xlat5.y) + u_xlat5.w;
            u_xlat30 = u_xlat31 / u_xlat30; u_xlat30 = u_xlat30 + u_xlat5.z;
            u_xlat5.xyz = abs((u_xlat30)) + float3(0.0, -0.333333343, 0.333333343);
            u_xlat5.xyz = frac(u_xlat5.xyz);
            u_xlat5.xyz = (-u_xlat5.xyz) * float3(2.0, 2.0, 2.0) + float3(1.0, 1.0, 1.0);
            u_xlat5.xyz = abs(u_xlat5.xyz) * float3(3.0, 3.0, 3.0) + float3(-1.0, -1.0, -1.0);
            u_xlat5.xyz = clamp(u_xlat5.xyz, 0.0, 1.0);
            u_xlat5.xyz = u_xlat5.xyz * float3(0.4, 0.4, 0.4) + float3(0.3, 0.3, 0.3);
            return u_xlat5.xyz;
        }

        Varyings vertOrgInside(VertexData v)
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
            float4 projPos = o.posCS; projPos.y *= _ProjectionParams.x;
            float4 projbiTan; projbiTan.xyz = biTan; projbiTan.xzw = projPos.xwy * 0.5;
            o.shadowCoordinate.zw = projPos.zw; o.shadowCoordinate.xy = projbiTan.zz + projbiTan.xw;
        #endif
            return o;
        }

        float3x3 AngleAxis3x3(float angle, float3 axis){
            float c, s; sincos(angle, s, c); float t = 1 - c;
            float x = axis.x, y = axis.y, z = axis.z;
            return float3x3(t*x*x+c,t*x*y-s*z,t*x*z+s*y,t*x*y+s*z,t*y*y+c,t*y*z-s*x,t*x*z-s*y,t*y*z+s*x,t*z*z+c);
        }

        fixed4 fragOrgInside(Varyings i, int faceDir : VFACE) : SV_Target
        {
            float4 mainTex = tex2D(_MainTex, i.uv0 * _MainTex_ST.xy + _MainTex_ST.zw);
            AlphaClip(i.uv0, mainTex.a);
            float3 worldLightPos = normalize(_WorldSpaceLightPos0.xyz);
            float3 viewDir = normalize(_WorldSpaceCameraPos.xyz - i.posWS);
            float3 halfDir = normalize(viewDir + worldLightPos);
            float4 colorMask = tex2D(_ColorMask, i.uv0 * _ColorMask_ST.xy + _ColorMask_ST.zw);
            float3 color; color = colorMask.r * (_Color.rgb - 1) + 1; color = colorMask.g * (_Color2.rgb - color) + color; color = colorMask.b * (_Color3.rgb - color) + color;
            float3 diffuse = mainTex * color;
            float3 normal = NormalAdjust(i, GetNormal(i), 1);
            float3x3 rotX = AngleAxis3x3(_KKPRimRotateX, float3(0, 1, 0)); float3x3 rotY = AngleAxis3x3(_KKPRimRotateY, float3(1, 0, 0));
            float3 adjustedViewDir = faceDir == 1 ? viewDir : -viewDir; float3 rotView = mul(adjustedViewDir, mul(rotX, rotY));
            float kkpFres = dot(normal, rotView); kkpFres = saturate(pow(1-kkpFres, _KKPRimSoft) * _KKPRimIntensity);
            _KKPRimColor.a *= (_UseKKPRim); float3 kkpFresCol = kkpFres * _KKPRimColor;
            diffuse = lerp(diffuse, kkpFresCol, _KKPRimColor.a * kkpFres * _KKPRimAsDiffuse);
            float time = _TimeEditor.y + _Time.y; time *= _Clock.z * _Clock.w;
            float sinTime = sin(time); float cosTime = cos(time);
            float3 rotVal = float3(-sinTime, cosTime, sinTime);
            float2 detailUVAdjust = i.uv0 - _Clock.xy; float2 rotatedDetailUV;
            rotatedDetailUV.x = dot(detailUVAdjust, rotVal.yz); rotatedDetailUV.y = dot(detailUVAdjust, rotVal.xy);
            rotatedDetailUV += _Clock.xy; rotatedDetailUV = rotatedDetailUV * _LineMask_ST.xy + _LineMask_ST.zw;
            float4 lineMaskRot = tex2D(_LineMask, rotatedDetailUV); diffuse = lineMaskRot.b * -diffuse + diffuse;
            float3 shadingAdjustment = ShadeAdjustItem(diffuse);
            float2 detailUV = i.uv0 * _DetailMask_ST.xy + _DetailMask_ST.zw; float4 detailMask = tex2D(_DetailMask, detailUV);
            float2 lineMaskUV = i.uv0 * _LineMask_ST.xy + _LineMask_ST.zw; float4 lineMask = tex2D(_LineMask, lineMaskUV);
            lineMask.r = _DetailRLineR * (detailMask.r - lineMask.r) + lineMask.r;
            float3 diffuseShaded = shadingAdjustment * 0.899999976 - 0.5; diffuseShaded = -diffuseShaded * 2 + 1;
            float4 ambientShadow = 1 - _ambientshadowG.wxyz;
            float3 ambientShadowIntensity = -ambientShadow.x * ambientShadow.yzw + 1;
            float ambientShadowAdjust = _ambientshadowG.w * 0.5 + 0.5;
            ambientShadow.rgb = (ambientShadowAdjust + ambientShadowAdjust) * _ambientshadowG.rgb;
            float3 finalAmbientShadow = 0.5 < ambientShadowAdjust ? ambientShadowIntensity : ambientShadow.rgb;
            finalAmbientShadow = saturate(finalAmbientShadow); float3 invFAS = 1 - finalAmbientShadow;
            bool3 compTest = 0.555555582 < shadingAdjustment;
            shadingAdjustment *= finalAmbientShadow; shadingAdjustment *= 1.79999995;
            diffuseShaded = -diffuseShaded * invFAS + 1;
            { float3 t = shadingAdjustment; t.x=(compTest.x)?diffuseShaded.x:t.x; t.y=(compTest.y)?diffuseShaded.y:t.y; t.z=(compTest.z)?diffuseShaded.z:t.z; shadingAdjustment=saturate(t); }
            float shadowExtendAnother = 1 - _ShadowExtendAnother;
            float kkMetal = _AnotherRampFull * (1 - lineMask.r) + lineMask.r; float kkMetalMap = kkMetal; kkMetal *= _UseKKMetal;
            shadowExtendAnother -= kkMetal; shadowExtendAnother += 1;
            shadowExtendAnother = saturate(shadowExtendAnother) * 0.670000017 + 0.330000013;
            float3 shadowExtendShaded = shadowExtendAnother * shadingAdjustment;
            shadingAdjustment = -shadingAdjustment * shadowExtendAnother + 1;
            float3 diffuseShadow = diffuse * shadowExtendShaded; float3 diffuseShadowBlended = -shadowExtendShaded * diffuse + diffuse;
            KKVertexLight vertexLights[4];
        #ifdef VERTEXLIGHT_ON
            GetVertexLights(vertexLights, i.posWS);
        #endif
            float4 vertexLighting = 0.0; float vertexLightRamp = 1.0;
        #ifdef VERTEXLIGHT_ON
            vertexLighting = GetVertexLighting(vertexLights, normal);
            float2 vlrUV = vertexLighting.a * _RampG_ST.xy + _RampG_ST.zw; vertexLightRamp = tex2D(_RampG, vlrUV).x;
            float3 rampLighting = GetRampLighting(vertexLights, normal, vertexLightRamp);
            vertexLighting.rgb = _UseRampForLights ? rampLighting : vertexLighting.rgb;
        #endif
            float lambert = dot(_WorldSpaceLightPos0.xyz, normal); lambert = max(lambert, vertexLighting.a);
            float2 rampUV = lambert * _RampG_ST.xy + _RampG_ST.zw; float ramp = tex2D(_RampG, rampUV);
            float fresnel = max(dot(normal, viewDir), 0.0); fresnel = log2(1 - fresnel);
            float specular = max(dot(normal, halfDir), 0.0);
            float anotherRampSpecularVertex = 0.0;
        #ifdef VERTEXLIGHT_ON
            [unroll] for(int j=0;j<4;j++){ KKVertexLight lv=vertexLights[j]; float3 hv=normalize(viewDir+lv.dir)*saturate(MaxGrayscale(lv.col)); anotherRampSpecularVertex=max(anotherRampSpecularVertex,dot(hv,normal)); }
        #endif
            float anotherRamp = tex2D(_AnotherRamp, max(specular, anotherRampSpecularVertex) * _AnotherRamp_ST.xy + _AnotherRamp_ST.zw);
            specular = log2(specular); anotherRamp -= ramp; float finalRamp = kkMetal * anotherRamp + ramp;
        #ifdef SHADOWS_SCREEN
            float2 shadowMapUV = i.shadowCoordinate.xy / i.shadowCoordinate.ww;
            float shadowAttenuation = saturate(tex2D(_ShadowMapTexture, shadowMapUV).x * 2.0 - 1.0);
            finalRamp *= shadowAttenuation;
        #endif
            diffuseShadow = finalRamp * diffuseShadowBlended + diffuseShadow;
            float specularHeight = _SpeclarHeight - 1.0; specularHeight *= 0.8;
            float2 dso; dso.x = dot(i.tanWS, viewDir); dso.y = dot(i.bitanWS, viewDir);
            float2 dmUV2 = specularHeight * dso + i.uv0; dmUV2 = dmUV2 * _DetailMask_ST.xy + _DetailMask_ST.zw;
            float drawnSpecular = tex2D(_DetailMask, dmUV2).x; float drawnSpecularSquared = min(drawnSpecular * drawnSpecular, 1.0);
            _SpecularPower *= _UseDetailRAsSpecularMap ? detailMask.x : 1;
            specular *= _SpecularPower * 256.0; specular = exp2(specular) * 5.0 - 4.0;
            drawnSpecular = saturate(specular * _SpecularPower + drawnSpecularSquared);
        #ifdef KKP_EXPENSIVE_RAMP
            float2 lrUV = specular * _RampG_ST.xy + _RampG_ST.zw;
            specular = tex2D(_RampG, lrUV) * _UseRampForSpecular + specular * (1 - _UseRampForSpecular);
        #endif
            specular = saturate(specular * _SpecularPower); specular = _notusetexspecular * (specular - drawnSpecular) + drawnSpecular;
            float specularVertex = 0.0; float3 specularVertexCol = 0.0;
        #ifdef VERTEXLIGHT_ON
            specularVertex = GetVertexSpecularDiffuse(vertexLights, normal, viewDir, _SpecularPower, specularVertexCol);
        #endif
            float3 specularCol = (saturate(specular) * _SpecularColor.rgb + saturate(specularVertex) * specularVertexCol * _notusetexspecular) * _SpecularColor.a;
            float3 asShadow2 = AmbientShadowAdjust(); detailMask.rg = 1 - detailMask.bg;
            float rimPow = _rimpower * 9.0 + 1.0; rimPow = rimPow * fresnel;
            float rim = saturate(exp2(rimPow) * 2.5 - 0.5) * _rimV * (detailMask.x * 9.99999809 - 8.99999809);
            asShadow2 = min(max(asShadow2 * rim * detailMask.g, 0.0), 0.5);
            diffuseShadow += asShadow2;
            float3 lightCol = (_LightColor0.xyz + vertexLighting.rgb * vertexLightRamp) * 0.6 + _CustomAmbient.rgb;
            diffuseShadow = diffuseShadow * max(lightCol, _ambientshadowG.xyz);
            float shadowExtend = _ShadowExtend * -1.2 + 1.0; float drawnShadow = detailMask.y * (1 - shadowExtend) + shadowExtend;
            float detailLineShadow = _DetailBLineG * (1 - detailMask.x - lineMask.y) + lineMask.y;
            shadingAdjustment = drawnShadow * shadingAdjustment + shadowExtendShaded; shadingAdjustment *= diffuseShadow;
            diffuse = diffuse * _LineColorG; float3 lineCol = -diffuse * shadowExtendShaded + 1; diffuse *= shadowExtendShaded;
            float lineAlpha = _LineColorG.w - 0.5; lineAlpha = -lineAlpha * 2.0 + 1.0; lineCol = -lineAlpha * lineCol + 1;
            diffuse = 0.5 < _LineColorG.w ? lineCol : diffuse * (_LineColorG.w * 2);
            diffuse = saturate(diffuse) - shadingAdjustment;
            float3 finalDiffuse = detailLineShadow * diffuse + shadingAdjustment + specularCol;
            finalDiffuse = GetBlendReflections(finalDiffuse, normal, viewDir, kkMetalMap);
            finalDiffuse = lerp(finalDiffuse, kkpFresCol, _KKPRimColor.a * kkpFres * (1 - _KKPRimAsDiffuse));
            float4 emission = GetEmission(i.uv0);
            finalDiffuse = finalDiffuse * (1 - emission.a) + (emission.a * emission.rgb);
            return float4(finalDiffuse, mainTex.a * _Alpha);
        }

        // BehindBody variant: full lighting * _BehindBodyAlpha for ghost-through-skin.
        fixed4 fragOrgInsideBehind(Varyings i, int faceDir : VFACE) : SV_Target
        {
            fixed4 c = fragOrgInside(i, faceDir);
            c.a *= _BehindBodyAlpha;
            return c;
        }

        // OutsideBody variant: full lighting * _OutsideOfBodyAlpha for outside-body fade.
        fixed4 fragOrgInsideOutside(Varyings i, int faceDir : VFACE) : SV_Target
        {
            fixed4 c = fragOrgInside(i, faceDir);
            c.a *= _OutsideOfBodyAlpha;
            return c;
        }
        ENDCG

        // ----------------------------------------------------------------
        // Pass: BottomWindow (must run FIRST, before DepthClear)
        // Up-the-open-vagina rays have no uterus-EXTERIOR coverage, so the
        // exterior's DepthClear/StencilWrite never ran there: stencil stays
        // pure body (low5 == _StencilBody, bit5 clear) and the body depth
        // blocks the canal. At exactly those pixels — pure body AND covered
        // by this interior mesh — wipe the body depth to FAR and promote
        // 4->5, exactly what the exterior would have done. The existing
        // chain (DepthClear 5->6, DepthWrite, Forward, Liquid, veil) then
        // runs unchanged and the pixel ends bit-identical to a normal
        // x-ray-window pixel. Everywhere the exterior did cover, the pixel
        // is already 5 -> Comp Equal vs 4 skips -> in-body look unchanged.
        // ReadMask 63: also requires bit5 clear (never fire on the ovary's
        // protected pixels; reachable states make this belt-and-braces).
        // WriteMask 31: IncrSat touches low5 only — bit6 (cum paint-once)
        // and bit7 (outline dedup) are preserved and masked out of the test.
        // Cull [_CullOption] (= Back on the interior bake, inward normals):
        // MUST match DepthWrite/Forward so window coverage stays a subset of
        // what Forward can paint — do not change to Off.
        // ----------------------------------------------------------------
        Pass
        {
            Name "BottomWindow"
            ZTest Always
            ZWrite On
            ColorMask 0
            Cull [_CullOption]

            Stencil
            {
                Ref [_StencilBody]
                ReadMask 63
                WriteMask 31
                Comp Equal
                Pass IncrSat
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _BottomWindow;

            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // Push to FAR plane, accounting for reversed-Z on DX11+
                // (identical idiom to the DepthClear pass below).
                #if UNITY_REVERSED_Z
                    o.pos.z = 0;
                #else
                    o.pos.z = o.pos.w;
                #endif
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                // Toggle OFF = discard = no stencil write, no depth write:
                // byte-exact old behavior.
                if (_BottomWindow < 0.5) discard;
                return 0;
            }
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 0: DepthClear (fires once per organ pixel, IncrSat to 45)
        // At stencil=44 pixels, push depth to FAR. Stamp stencil to 45 so
        // subsequent OrgInside objects skip this pass and don't wipe the
        // depth that the first one (and Pass 1) wrote.
        // ----------------------------------------------------------------
        Pass
        {
            Name "DepthClear"
            ZTest Always
            ZWrite On
            ColorMask 0
            // Honour _CullOption (was hardcoded Cull Back). For an inner-cavity
            // mesh, set _CullOption = Front so the near wall doesn't write depth
            // — otherwise it blocks anything (e.g. cum) behind it.
            Cull [_CullOption]

            Stencil
            {
                Ref [_StencilBody_Plus_1]
                ReadMask 63
                WriteMask 31
                Comp Equal
                Pass IncrSat
            }
            // ReadMask 63 (was 31): the split ovary marks bit5, so this DepthClear skips the
            // ovary's pixels — its depth survives and the interior never repaints over it.

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
                // Push to FAR plane, accounting for reversed-Z on DX11+.
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
        // Pass 1: DepthWrite (inter-OrgInside depth competition)
        // At stencil >= 44 (organ region, possibly already incremented to 45),
        // write THIS object's depth with ZTest Less. Among multiple OrgInside
        // objects covering the same pixel, only the closest's depth survives.
        // After this pass, depth at each organ pixel = closest OrgInside depth.
        // ----------------------------------------------------------------
        Pass
        {
            Name "DepthWrite"
            ZTest Less
            ZWrite On
            ColorMask 0
            // Same as DepthClear — honour _CullOption so the near wall depth
            // doesn't get written for inner-cavity meshes.
            Cull [_CullOption]

            Stencil
            {
                Ref [_StencilBody_Plus_1]
                ReadMask 31
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
        // Pass 2: FORWARD — full KK item lighting, normal ZTest LEqual
        // No stencil check: depth alone gates visibility. Inside organ, the
        // depth was set in Pass 1 to the closest OrgInside's depth, so only
        // that one passes. Outside body, depth = scene depth so we render
        // normally. Inside body but outside organ, body depth blocks us.
        // ----------------------------------------------------------------
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "ForwardBase" }
            ZTest LEqual
            ZWrite On
            Blend SrcAlpha OneMinusSrcAlpha
            Cull [_CullOption]

            // Stencil NotEqual 0: only fire INSIDE a character body (any
            // stenciled region). Outside body (stencil = 0) is handled by
            // the separate OutsideBody pass so its alpha can be controlled.
            Stencil
            {
                Ref 0
                ReadMask 31
                Comp NotEqual
                Pass Keep
            }

            CGPROGRAM
            // Uses shared vertOrgInside / fragOrgInside from CGINCLUDE above.
            #pragma target 3.0
            #pragma vertex vertOrgInside
            #pragma fragment fragOrgInside
            #pragma multi_compile _ VERTEXLIGHT_ON
            #pragma multi_compile _ SHADOWS_SCREEN
            ENDCG
        }


        // ----------------------------------------------------------------
        // Pass 2b: BehindBody (semi-transparent visibility through body skin)
        // ----------------------------------------------------------------
        // Pass 2a: OutsideBody (object outside any character body)
        // Forward pass is gated to stencil != 0 now, so this handles
        // stencil == 0 (no body at this pixel). Default _OutsideOfBodyAlpha = 1
        // matches the old behavior (full visibility outside body); lower it
        // to fade out the object when not contained.
        // Uses simplified texture+tint shading.
        // ----------------------------------------------------------------
        Pass
        {
            Name "OutsideBody"
            Tags { "LightMode" = "ForwardBase" }
            ZTest LEqual
            ZWrite On
            Blend SrcAlpha OneMinusSrcAlpha
            Cull [_CullOption]

            Stencil
            {
                Ref 0
                ReadMask 63
                Comp Equal
                Pass Keep
            }

            CGPROGRAM
            // ReadMask 63 (was 31): also tests bit5 — the solid ovary slot sets bit5 over its
            // footprint, so this pass fails there and the interior does NOT paint over the ovary.
            // Uses shared vertOrgInside / fragOrgInsideOutside (full KK
            // lighting with alpha multiplied by _OutsideOfBodyAlpha).
            #pragma target 3.0
            #pragma vertex vertOrgInside
            #pragma fragment fragOrgInsideOutside
            #pragma multi_compile _ VERTEXLIGHT_ON
            #pragma multi_compile _ SHADOWS_SCREEN
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 2b: BehindBody (semi-transparent visibility through body skin)
        // Renders this object inside the body silhouette but OUTSIDE any organ
        // — exactly the case the regular Forward pass hides via depth test.
        // ZTest Always so the body skin's depth doesn't cull us. Stencil
        // gates rendering to body-only pixels (stencil = _StencilBody, NOT
        // organ stencil). Output alpha is multiplied by _BehindBodyAlpha to
        // create a "ghost through skin" effect. _BehindBodyAlpha = 0 → no
        // visible ghost (preserves the old behavior).
        // Uses simplified texture+tint shading (not full KK lighting) since
        // this is a low-fidelity ghost visualization.
        // ----------------------------------------------------------------
        Pass
        {
            Name "BehindBody"
            Tags { "LightMode" = "ForwardBase" }
            ZTest Always
            ZWrite Off
            Cull [_CullOption]
            Blend SrcAlpha OneMinusSrcAlpha

            // ReadMask 31: ignore bits 5 (free), 6 (OrganMark) and 7 (set by the
            // OutlineMark pass below) so this still fires
            // at body-region pixels regardless of whether the OrgInside
            // silhouette is marked there or whether the suit's organ-mark is set.
            Stencil
            {
                Ref [_StencilBody]
                ReadMask 31
                Comp Equal
                Pass Keep
            }

            CGPROGRAM
            // Uses shared vertOrgInside / fragOrgInsideBehind (full KK
            // lighting with alpha multiplied by _BehindBodyAlpha).
            #pragma target 3.0
            #pragma vertex vertOrgInside
            #pragma fragment fragOrgInsideBehind
            #pragma multi_compile _ VERTEXLIGHT_ON
            #pragma multi_compile _ SHADOWS_SCREEN
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 2c: OutlineMark (sets stencil bit 7 at OrgInside silhouette in body region)
        // Marks pixels covered by the OrgInside mesh AND inside any body
        // (stencil bit 0 = 1 means body, bit 0 = 0 means organ — exploits
        // that StencilRef is always odd in our scheme). Bit 7 is unused
        // elsewhere; we set it WITHOUT touching the lower bits so other
        // passes that read the body/organ stencil still work.
        // The Outline pass below then checks stencil exactly equals
        // _StencilBody (no ReadMask), so it WON'T fire at marked pixels,
        // making the outline appear only OUTSIDE the OrgInside silhouette
        // (the extruded ring region).
        // ----------------------------------------------------------------
        Pass
        {
            Name "OutlineMark"
            ZTest Always
            ZWrite Off
            ColorMask 0
            Cull Back

            // Comp Equal Ref 129 ReadMask 1: compares (stencil & 1) with
            // (129 & 1) = 1 — i.e. passes when stencil bit 0 = 1 (body region).
            // Pass Replace WriteMask 128: writes Ref's bit 7 (=1) to stencil's
            // bit 7, leaving other bits unchanged.
            Stencil
            {
                Ref 129
                ReadMask 1
                Comp Equal
                Pass Replace
                WriteMask 128
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
        // Pass 2d: Outline (optional flat-color rim around the OrgInside silhouette)
        // Fires only at unmarked body pixels (stencil exactly = _StencilBody,
        // i.e. body region NOT marked by Pass 2c). With extruded vertices,
        // produces a clean ring just OUTSIDE the OrgInside silhouette.
        // _XrayOutlineWidth = 0 → no extrusion → no visible outline.
        // ----------------------------------------------------------------
        Pass
        {
            Name "Outline"
            ZTest Always
            ZWrite Off
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            Stencil
            {
                Ref [_StencilBody]
                ReadMask 159
                Comp Equal
                Pass Keep
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float  _XrayOutlineWidth;
            float4 _XrayOutlineColor;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f    { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 worldNormal = UnityObjectToWorldNormal(v.normal);
                worldPos += worldNormal * _XrayOutlineWidth;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1));
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                if (_XrayOutlineWidth < 0.0001) discard;
                return _XrayOutlineColor;
            }
            ENDCG
        }

        // ----------------------------------------------------------------
        // Pass 3: ShadowCaster — invisible
        // ----------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZTest Never  ZWrite Off  ColorMask 0
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
