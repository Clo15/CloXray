using UnityEngine;
using UnityEditor;
using System.IO;

public class BuildCloXray
{
    private const string OutputDir = "Output";
    private const string BundleName = "cloxray";

    [MenuItem("Build/Build CloXray Bundle")]
    public static void Build()
    {
        // Create output directory next to Assets/
        // Use nested Combine (2-arg) for Mono 2.0 compatibility with Unity 5.6
        string outputPath = Path.GetFullPath(Path.Combine(Path.Combine(Application.dataPath, ".."), OutputDir));
        if (!Directory.Exists(outputPath))
            Directory.CreateDirectory(outputPath);

        // Define which shaders go into the bundle
        var builds = new AssetBundleBuild[]
        {
            new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = new string[]
                {
                    "Assets/Shaders/CloXray_BodyReveal.shader",
                    "Assets/Shaders/CloXray_DepthOnly.shader",
                    "Assets/Shaders/CloXray_Organ.shader",
                    "Assets/Shaders/CloXray_OrgInside.shader",
                    "Assets/Shaders/CloXray_Liquid.shader",
                    "Assets/Shaders/CloXray_BodyRevealExtra.shader",
                    "Assets/Shaders/CloXray_AddXrayToMaterialCopy.shader",
                    "Assets/Shaders/CloXray_XrayMachine.shader",
                    "Assets/Shaders/CloXray_GradientTest.shader",
                    "Assets/Textures/matcap.png",
                }
            }
        };

        BuildPipeline.BuildAssetBundles(
            outputPath,
            builds,
            BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64
        );

        Debug.Log("CloXray bundle built to: " + outputPath);
        EditorUtility.RevealInFinder(outputPath);
    }
}
