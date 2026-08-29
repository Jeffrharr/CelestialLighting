using System.IO;
using UnityEditor;
using UnityEngine;

// Builds the mod's shader AssetBundle, once per platform. Run headless by Tools/ShaderBundle/build.sh;
// there is no reason to open this project in the editor.
//
// THREE BUNDLES, NOT ONE, AND THAT IS NOT OPTIONAL FOR A SHADER. ModAssetBundlesHandler loads only
// the bundle whose name ends in the suffix for the running OS (BundleSuffixForCurrentOs, which throws
// on anything else). A bundle with NO suffix loads everywhere and is fine for textures — but a
// shader's compiled variants are per graphics API, so a Linux-built bundle carries glcore and vulkan
// programs and has nothing a Direct3D or Metal machine can run. Ship one platform's bundle only and
// the shader silently fails to load for everybody else, who then get the CPU fallback and no error.
//
// THE OTHER THREE THINGS THAT FAIL SILENTLY, all of them path conventions RimWorld enforces without
// ever logging that it did:
//
//   1. The bundle file must have NO EXTENSION. ModAssetBundlesHandler.IsAcceptableExtension accepts
//      only extensionless files, and skips anything else without a word.
//   2. Its name must end _linux / _mac / _win — the suffixes below, spelled RimWorld's way rather
//      than Unity's (BuildTarget is "StandaloneWindows64", the suffix is "_win").
//   3. The asset path INSIDE the bundle must be Assets/Data/<PackageIdPlayerFacing>/Materials/<name>.shader.
//      PackageIdPlayerFacing, not FolderName: FolderName is the mod's directory, which is
//      "CelestialLighting" for a dev symlink and the numeric Workshop id for everybody else — so a
//      FolderName-keyed bundle works on this machine and on no subscriber's.
public static class BuildShaderBundles
{
    private const string BundleName = "celestiallighting_shaders";

    // ONE BUNDLE FOR EVERY SHADER THE MOD SHIPS, not one per feature. ModAssetBundlesHandler opens
    // each bundle in the folder and ShaderDatabase.LoadShader searches all of them, so two bundles
    // would work — but they would also be two things to build, two to stage in publish.sh and two
    // to forget. Adding a shader here is adding a line to this array and rerunning build.sh.
    private static readonly string[] ShaderPaths =
    {
        "Assets/Data/joof.celestiallighting/Materials/CelestialCloudVolume.shader",
        "Assets/Data/joof.celestiallighting/Materials/VectorLightMax.shader",
        "Assets/Data/joof.celestiallighting/Materials/CelestialAurora.shader",
    };

    public static void Build()
    {
        // Each target gets its OWN output directory. BuildPipeline writes a manifest named after the
        // folder it builds into, so three targets sharing one folder would each overwrite the last
        // one's manifest — the bundles themselves would survive, but the build would report success
        // while leaving a directory whose contents nothing can describe.
        BuildOne(BuildTarget.StandaloneLinux64, "linux");
        BuildOne(BuildTarget.StandaloneWindows64, "win");
        BuildOne(BuildTarget.StandaloneOSX, "mac");
    }

    private static void BuildOne(BuildTarget target, string suffix)
    {
        string output = Path.GetFullPath(
            Path.Combine(Application.dataPath, "../../Build/" + suffix));
        Directory.CreateDirectory(output);

        AssetBundleBuild build = new AssetBundleBuild
        {
            assetBundleName = BundleName + "_" + suffix,
            assetNames = ShaderPaths,
        };

        // CHUNK-BASED (LZ4) RATHER THAN THE DEFAULT LZMA, which is what the 1.6 modder primer asks
        // for. The default is marginally smaller on disk and the wrong trade: LZMA is a whole-stream
        // codec, so Unity cannot read one asset out of the bundle without decompressing all of it
        // into memory, and it does that before the main menu appears. LZ4 is block-addressable, so
        // loading a shader touches only the blocks that shader lives in. It costs a little size and
        // buys load time and peak memory back.
        //
        // Worth stating that our three bundles are ~68 KB TOTAL, so nothing here is measurable today.
        // It is set correctly because the cost of being wrong scales with the bundle and the cost of
        // being right does not, and because a shipped default nobody revisits is how the small
        // version of this becomes the large one.
        BuildPipeline.BuildAssetBundles(
            output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, target);

        Debug.Log("Built " + build.assetBundleName + " into " + output);
    }
}
