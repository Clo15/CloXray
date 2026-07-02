# Shader includes — KKShadersPlus (not bundled)

The CloXray organ shaders (CloXray_Organ, CloXray_OrgInside, CloXray_BodyRevealExtra) `#include`
xukmi's KKShadersPlus base. Those files are not redistributed here — supply them yourself:

> Source: https://github.com/xukmi/KKShadersPlus  (MIT · © 2021 xukmi)

Drop these 12 files flat into this folder and the shaders compile:

| From the KKShadersPlus repo | Files |
|---|---|
| `Shaders/Item/` | `KKPItemInput.cginc` · `KKPItemDiffuse.cginc` · `KKPItemNormals.cginc` · `KKPItemCoom.cginc` |
| `Shaders/Hair/` | `KKPHairInput.cginc` · `KKPHairDiffuse.cginc` · `KKPHairVertFrag.cginc` |
| `Shaders/` (top level) | `KKPVertexLights.cginc` · `KKPVertexLightsSpecular.cginc` · `KKPEmission.cginc` · `KKPReflect.cginc` · `KKPDisplace.cginc` |

The other five CloXray shaders (Liquid, BodyReveal, XrayMachine, GradientTest, AddXrayToMaterialCopy)
use only Unity's own `UnityCG.cginc` and need nothing extra.
