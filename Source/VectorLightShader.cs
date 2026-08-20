using UnityEngine;
using Verse;

namespace CelestialLighting;

// §27 phase 6's one dependency on a compiled asset, lifted from #151 unchanged apart from
// which flag it reads: the fragment program that composes
// max(vanilla, ours), loaded out of the AssetBundles this mod now ships.
//
// THE FIRST BINARY ASSET IN THE REPO, and a decision rather than an implementation detail. §11a
// rejected asset bundles outright on the grounds that this mod ships no binaries; that was revisited
// for this feature and reversed, because the composition needs a per-vertex channel MoteGlow does not
// read and there is no way to reach one without a shader. DESIGN.md §27 records the reversal and what
// it costs. Everything else in the mod still runs on stock shaders.
//
// WHY LOADING IS ALLOWED TO FAIL, AND WHAT HAPPENS WHEN IT DOES. Three things can go wrong and none
// of them are hypothetical: the bundle can be absent (a source checkout that never ran
// Tools/ShaderBundle/build.sh, or a publish.sh that forgot to stage it), it can be present but built
// for another OS, and it can load fine and not compile on the player's hardware. All three land here
// as Available == false, and the whole subsystem falls back to the crossfade — which is a shipped,
// measured arm rather than an unknown one. A missing shader must never mean missing light.
//
// [StaticConstructorOnStartup] is load-bearing, not tidiness, for the same reason it is on
// VectorLightOverlay: Shader lookups and Material construction have to happen on Unity's main thread
// after LoadedModManager has the bundles open, and the attribute is what guarantees the static
// initialiser runs there rather than on whichever thread first touches the type.
[StaticConstructorOnStartup]
public static class VectorLightShader
{
    // The shader's path INSIDE the bundle, minus the extension. ContentFinder builds the full path as
    // Assets/Data/<id>/Materials/<this>.shader, and Tools/ShaderBundle/build.sh is what puts it
    // there — the two have to be changed together, and there is no error if they disagree, only a
    // silent fallback to the crossfade.
    public const string ShaderPath = "VectorLightMax";

    // How much of vanilla's sampled glow the fragment program subtracts. One is the feature; zero
    // makes the shader reproduce MoteGlow exactly, which is what the live A/B's control arm needs to
    // separate "the composition changed the frame" from "the replacement shader changed the frame".
    private static readonly int VanillaWeightId = Shader.PropertyToID("_VanillaWeight");

    private static readonly int VanillaTexId = Shader.PropertyToID("_VanillaTex");

    private static readonly Shader Loaded = Load();

    // Whether the max composition can actually be drawn. Read this rather than the feature flag
    // wherever the answer has to be true for the frame to be correct.
    public static bool Available => Loaded != null;

    // Whether §27 should compose as a max this frame: asked for, and possible.
    public static bool MaxActive =>
        CelestialLightingFeatures.VectorLightShaderMax && Available;

    public static Material NewMaterial(Texture2D gradient)
    {
        Material material = new Material(Loaded) { mainTexture = gradient };

        // COPY MoteGlow'S RENDER QUEUE RATHER THAN DECLARING ONE, and this is the single most
        // expensive thing learned building this feature. An additive pass is order-independent only
        // against other additive passes: the lighting overlay is a MULTIPLY, so a light drawn before
        // it gets attenuated by it and a light drawn after it does not. Our first bundle declared
        // "Queue"="Transparent" and landed on the wrong side of that multiply, which made the whole
        // pass render at a fraction of MoteGlow's brightness — and in the arm where vanilla's own
        // light was suppressed, the multiply was nearly black and our light all but vanished.
        //
        // It did not look like an ordering bug. It looked like the composition being wrong, because
        // the frame was dimmer than vanilla in exactly the place the composition was supposed to be
        // adding light. The control arm — our shader with its subtraction switched off, which must
        // reproduce MoteGlow exactly — is what separated the two, and it is why that arm exists.
        //
        // Reading the queue off MoteGlow rather than hardcoding a number means we cannot drift from
        // it if Ludeon moves the motes, and it needs no rebuild of the three bundles to change.
        // Measured on RimWorld 1.6: our declared queue was 3000, MoteGlow's is 3151.
        material.renderQueue = ShaderDatabase.MoteGlow.renderQueue;

        return material;
    }

    public static void SetVanillaWeight(MaterialPropertyBlock props, float weight)
    {
        props.SetFloat(VanillaWeightId, weight);
    }

    // Vanilla's delivered glow over this emitter's own square, as a texture the fragment program
    // looks up per fragment.
    //
    // ON THE PROPERTY BLOCK RATHER THAN THE MATERIAL, and for the same deferred-draw reason the
    // colour is: Graphics.DrawMesh queues the draw and resolves it later, so a texture written to
    // the shared material between calls would give every light in the frame whichever one was
    // written last. The material is shared per RADIUS — the falloff gradient is the only thing that
    // depends on radius — while this is per emitter, so it could not live there in any case.
    public static void SetVanillaTexture(MaterialPropertyBlock props, Texture texture)
    {
        props.SetTexture(VanillaTexId, texture);
    }

    private static Shader Load()
    {
        Shader shader = ShaderDatabase.LoadShader(ShaderPath);

        // LoadShader does not report failure to its caller — it logs a warning and hands back
        // ShaderDatabase.DefaultShader, which is Map/Cutout. Rendering our additive pass through a
        // cutout shader would not fail, it would draw opaque black quads over the map, so identity
        // against the default is the check that matters and not a null test.
        if (shader == null || shader == ShaderDatabase.DefaultShader)
        {
            Log.Warning(
                "[CelestialLighting] Could not load shader '" + ShaderPath + "' from the mod's asset "
                + "bundles. §27's max composition is unavailable; falling back to the crossfade.");
            return null;
        }

        // Supported is a per-machine answer, not a per-build one: the bundle can be perfectly valid
        // and still fail to compile on hardware or a graphics API that cannot run the pass. Vanilla
        // asks the same question of its own shaders — SectionLayer_SunShadows is skipped entirely
        // when MatBases.SunShadow.shader.isSupported is false — so this is the established shape of
        // the check rather than defensiveness.
        if (!shader.isSupported)
        {
            Log.Warning(
                "[CelestialLighting] Shader '" + ShaderPath + "' loaded but is not supported on this "
                + "system. §27's max composition is unavailable; falling back to the crossfade.");
            return null;
        }

        return shader;
    }
}
