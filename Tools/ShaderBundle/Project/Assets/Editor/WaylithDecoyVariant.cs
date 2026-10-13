using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

// THE RIMWORLD 1.7 DECOY VARIANT, and why a shader carries a variant nothing ever draws.
//
// When every variant of a shader program uses the same parameter, Unity stores that parameter once,
// in the program's m_CommonParameters block, instead of in each variant's own parameter blob. A shader
// with no keywords has exactly one variant, so it shares EVERYTHING: its textures, its constant
// buffers, the lot. Unity's own player reads both places. RimWorld 1.7's Waylith engine does not —
// UnityStubs' UnityShaderAsset.ReadParameters reads only the per-variant blob — so it sees a program
// that binds a constant buffer it was never told about, and refuses the shader with "buffer at set 1
// binding 1 is not a known constant buffer". The mod then drops to its CPU fallback with nothing but a
// warning in Player.log. That is what happened to the cloud volume and aurora shaders on 1.7, while
// VectorLightMax, which already had two variants with different parameters, loaded fine.
//
// The fix is to give Unity nothing to hoist. A shader opts in with
//     #pragma multi_compile_local _ CL_WAYLITH_DECOY
// and an early `return 0;` under that keyword, so the decoy variant uses NO parameter at all and no
// parameter is common to every variant any more. Two properties make it safe:
//   - Waylith draws the variant with the FEWEST keywords and never switches (TryPickProgram), so the
//     keywordless real program is the one it uses. The decoy is never selected there.
//   - Nothing ever enables CL_WAYLITH_DECOY, so Unity never selects it either.
// It depends on Unity's hoisting heuristic, which is not documented. That dependency is closed by
// WaylithCheck (Tools/ShaderBundle/WaylithCheck), which build.sh runs on every Vulkan bundle: it calls
// the installed engine's own loader and fails the build if any shader still has m_CommonParameters or
// would be refused. A Unity upgrade that changes the heuristic fails loudly at build time, not in a
// player's log.
//
// STRIPPED FROM EVERY 1.6 BUILD. 1.6 runs real Unity, which reads the shared block correctly and has
// no use for the decoy. More to the point, 1.6 can no longer be tested on the machine that builds
// these bundles (Steam has moved it to 1.7), so the 1.6 bundles must stay what they were. Stripping
// here is what keeps their compiled programs identical; BuildVulkan flips KeepForThisBuild for the one
// build that wants the decoy.
//
// Built for the decoy keyword only. This does not strip any other variant, so VectorLightMax's
// VECTORLIGHT_BATCHED survives in both versions exactly as before.
class WaylithDecoyVariant : IPreprocessShaders
{
    public const string Keyword = "CL_WAYLITH_DECOY";

    // Set by BuildShaderBundles.BuildVulkan around the 1.7 build, and only there. Static because
    // IPreprocessShaders gets no say in which build it is serving.
    public static bool KeepForThisBuild;

    public int callbackOrder => 0;

    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (KeepForThisBuild)
            return;

        // Walk backwards so RemoveAt does not shift the entries still to be visited.
        for (int i = data.Count - 1; i >= 0; i--)
        {
            if (HasDecoy(shader, data[i]))
                data.RemoveAt(i);
        }
    }

    private static bool HasDecoy(Shader shader, ShaderCompilerData variant)
    {
        var keyword = new LocalKeyword(shader, Keyword);
        return keyword.isValid && variant.shaderKeywordSet.IsEnabled(keyword);
    }
}
