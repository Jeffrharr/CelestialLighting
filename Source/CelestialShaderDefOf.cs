using RimWorld;
using Verse;

namespace CelestialLighting;

// The mod's three ShaderTypeDefs, bound by name so the shader classes never carry a literal path.
// 1.6/Defs/ShaderTypeDefs/ShaderTypes.xml is the other half and records why the defs exist at all.
//
// THE STATIC CONSTRUCTOR IS NOT BOILERPLATE. DefOfHelper.EnsureInitializedInCtor logs a warning if
// anything reads these fields before DefOfHelper.RebindAllDefOfs has run, which is the only way to
// tell "the def is missing" apart from "you asked too early" — both present as a null field.
//
// We are safe on ordering as it happens: PlayDataLoader.DoPlayLoad calls RebindAllDefOfs well before
// StaticConstructorOnStartupUtility.CallAll, so every [StaticConstructorOnStartup] class in this mod
// sees bound defs. That is a vanilla ordering guarantee we are relying on rather than one we control,
// so the warning stays as the tripwire if Ludeon ever reorders it.
//
// CL_ PREFIX because defNames share one flat namespace across every loaded mod. Vanilla's own shader
// defs are unprefixed (Cutout, MoteGlow, Transparent) and we must not collide with a name a future
// version might claim.
[DefOf]
public static class CelestialShaderDefOf
{
    public static ShaderTypeDef CL_CloudVolume;

    public static ShaderTypeDef CL_VectorLightMax;

    public static ShaderTypeDef CL_Aurora;

    static CelestialShaderDefOf()
    {
        DefOfHelper.EnsureInitializedInCtor(typeof(CelestialShaderDefOf));
    }
}
