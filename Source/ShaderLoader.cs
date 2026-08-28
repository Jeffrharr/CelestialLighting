using UnityEngine;
using Verse;

namespace CelestialLighting;

// One place where a ShaderTypeDef becomes a Shader, shared by the three shader classes so the
// def-missing branch is written and reasoned about once rather than three times.
//
// WHAT THIS DELIBERATELY DOES NOT DO IS VALIDATE. Each caller keeps its own check on the returned
// shader — AuroraShader and CloudVolumeShader compare the DECLARED name, VectorLightShader tests
// identity against ShaderDatabase.DefaultShader — because those checks differ, they are the thing
// that catches a bad bundle, and centralising them here would have meant weakening one to fit the
// others. All this class decides is WHERE to look.
internal static class ShaderLoader
{
    // Resolve a shader through its def, falling back to the literal bundle path when the def is not
    // in the database.
    //
    // WHY THERE IS A FALLBACK AT ALL, given that the def existing is the whole point. The def can be
    // absent for two reasons and they want opposite treatment:
    //
    //   - The mod shipped without its Defs folder. That is a packaging bug, it is exactly how v1.0.0
    //     shipped (see publish.sh's CONTENT_DIRS comment), and it must be LOUD.
    //   - The assemblies and the content tree disagree, which is the normal state of a live harness
    //     run: --mod-overlay swaps the DLL and leaves Defs/, Textures/ and AssetBundles/ coming from
    //     the main checkout. A branch that adds a def therefore runs its new code against a content
    //     tree that has never heard of it, and without this fallback every shader in the mod would go
    //     dark in exactly the runs meant to verify them.
    //
    // So: complain in a way nobody can miss, then keep the lights on. The house rule from
    // VectorLightShader applies unchanged — a missing shader must never mean missing light — and it
    // applies just as much when what went missing is the def that names the shader.
    public static Shader Load(ShaderTypeDef def, string fallbackPath, string subsystem)
    {
        if (def != null)
            return def.Shader;

        // Error rather than Warning, and it names the defName rather than the path: the reader needs
        // to know that a FILE is missing from the install, not that a shader failed to compile.
        Log.Error(
            "[CelestialLighting] ShaderTypeDef for '" + fallbackPath + "' is not in the def database, "
            + "so 1.6/Defs/ShaderTypeDefs/ShaderTypes.xml is missing from this install (or the "
            + "assemblies are newer than the content tree, which is normal under --mod-overlay). "
            + "Falling back to the literal bundle path for " + subsystem + ".");

        return ShaderDatabase.LoadShader(fallbackPath);
    }
}
