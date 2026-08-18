using System;
using System.IO;
using UnityEditor;

public static class BuildShaderBundles
{
    // RimWorld's ModAssetBundlesHandler loads only the bundle whose name ends in the suffix for the
    // running OS, and compiled shader variants are per graphics API — so all three have to be built
    // and shipped, or the shader silently fails to load for everyone not on the build machine's OS.
    private static readonly (BuildTarget Target, string Suffix)[] Targets =
    {
        (BuildTarget.StandaloneLinux64, "_linux"),
        (BuildTarget.StandaloneWindows64, "_win"),
        (BuildTarget.StandaloneOSX, "_mac"),
    };

    public static void Build()
    {
        string outDir = Environment.GetEnvironmentVariable("BUNDLE_OUT");
        string asset = Environment.GetEnvironmentVariable("BUNDLE_ASSET");
        string name = Environment.GetEnvironmentVariable("BUNDLE_NAME");
        Directory.CreateDirectory(outDir);

        foreach ((BuildTarget target, string suffix) in Targets)
        {
            var build = new AssetBundleBuild
            {
                assetBundleName = name + suffix,
                assetNames = new[] { asset },
            };

            var manifest = BuildPipeline.BuildAssetBundles(
                outDir, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, target);

            if (manifest == null)
                throw new Exception("BuildAssetBundles returned null for " + target);

            Console.WriteLine("BUILT " + name + suffix);
        }

        Console.WriteLine("ALL_BUNDLES_OK");
    }
}
