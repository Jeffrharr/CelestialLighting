using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// Which renderer is actually drawing the aurora curtain: 1 for the fragment shader, 0 for the CPU
// bake. Read from the same two conditions the overlay itself reads, in the same order.
//
// WHY A SCENARIO NEEDS THIS AND CANNOT INFER IT. The shader path degrades silently and on purpose —
// a missing AssetBundle, a bundle built for another OS, a card that will not compile the pass — and
// all three land on the bake with nothing but a log line. That is right for a player and wrong for an
// A/B: the arm that believes it is measuring the shader would measure the bake, the two frames would
// be identical, and the scenario would report a confident "no visible difference" that is a statement
// about the test rig rather than about the feature.
//
// The overlay-only trap this is aimed at is narrower still. Tests/Scenarios overlay a branch build's
// ASSEMBLIES onto the main checkout, and AssetBundles come from the stale main checkout — so a
// worktree that has just added a shader and not rebuilt the bundles gets a mod that knows about the
// shader, asks for it, does not find it, and quietly bakes. Pin this at 1 in any arm that means to
// exercise the shader.
public sealed class AuroraShaderActiveProbe : IProbe
{
    public string Name => "aurora_shader_active";

    public float Read(Map map) => AuroraShader.Active ? 1f : 0f;
}
