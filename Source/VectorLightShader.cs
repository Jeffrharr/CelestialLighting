using UnityEngine;
using UnityEngine.Rendering;
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
// NO [StaticConstructorOnStartup], AND THAT IS A CHANGE. It used to be here to guarantee that the
// eager `Loaded` field's Shader lookup ran on Unity's main thread after LoadedModManager had the
// bundles open. There is no eager field any more: the shader resolves on first use, and every use is
// a Material construction on the render path, which is already the main thread. An attribute that
// forces a type to initialise at load when nothing in it needs to is just a slower boot.
public static class VectorLightShader
{
    // The shader's path INSIDE the bundle, minus the extension: RimWorld builds the full asset path
    // as Assets/Data/<packageId>/Materials/<this>.shader.
    //
    // THIS IS NOW THE FALLBACK, NOT THE ROUTE. CL_VectorLightMax in
    // 1.6/Defs/ShaderTypeDefs/ShaderTypes.xml carries the same string and is what we normally load
    // through; this const is what ShaderLoader uses when that def is not in the database, and it is
    // still what the log messages below quote because the path is what a player can check against
    // the bundle. The duplication is deliberate and it is TESTED — ShaderTypeDefTests asserts the
    // const, the def's shaderPath and the asset name in BuildShaderBundles.cs are the same string,
    // because nothing at runtime notices if they drift, they just silently fall back to the crossfade.
    public const string ShaderPath = "VectorLightMax";

    // How much of vanilla's sampled glow the fragment program subtracts. One is the feature; zero
    // makes the shader reproduce MoteGlow exactly, which is what the live A/B's control arm needs to
    // separate "the composition changed the frame" from "the replacement shader changed the frame".
    private static readonly int VanillaWeightId = Shader.PropertyToID("_VanillaWeight");

    private static readonly int VanillaTexId = Shader.PropertyToID("_VanillaTex");

    // The blend factors, driven from the material because render state cannot come from a
    // MaterialPropertyBlock. The surface lift is exactly this pair and nothing else.
    private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");

    private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");

    private static readonly int SkyAmbientId = Shader.PropertyToID("_SkyAmbient");

    // The batched variant's three inputs: the slice stack of every emitter's square, and the two
    // uniform arrays the vertex program indexes with the slot carried in UV1.z. See
    // VectorLightDrawBatch for what fills them and the shader's own header for why they are a
    // keyword variant rather than a second shader.
    private static readonly int VanillaArrayId = Shader.PropertyToID("_VanillaArray");

    private static readonly int BatchColorId = Shader.PropertyToID("_BatchColor");

    private static readonly int BatchParamsId = Shader.PropertyToID("_BatchParams");

    // MUST MATCH THE SHADER'S OWN VECTORLIGHT_BATCH_MAX. A uniform array is a fixed allocation, so
    // writing more elements than the program declares is not a bigger batch — it is a silently
    // truncated one, and the emitters past the end draw with whatever the array happened to hold.
    // Named here because the C# side is what has to obey it; the shader's define is the authority.
    public const int BatchCapacity = 64;

    // The keyword that selects the batched variant. Spelled once, because EnableKeyword takes a
    // string and a typo in it is not an error — it enables a keyword no variant declares, which
    // leaves the per-emitter program running against nothing bound and draws black fans.
    public const string BatchKeyword = "VECTORLIGHT_BATCHED";

    // The shader itself, resolved through the def on every read. ShaderTypeDef memoises into its own
    // shaderInt, so after the first call this is a field read behind a property — cheap enough that a
    // second cache here would buy nothing except a second way to reach the same object.
    private static Shader Loaded => ShaderLoader.Resolve(
        CelestialShaderDefOf.CL_VectorLightMax, ShaderPath, "vector lighting's max composition");

    // The VERDICT is cached even though the shader is not, and the asymmetry is deliberate:
    // UnityEngine.Object.name is a native call that allocates a fresh string, Validate compares it,
    // and MaxActive reads this every frame. Caching a bool costs nothing and caching a Shader costs
    // the clarity above.
    private static bool? available;

    // Whether the max composition can actually be drawn. Read this rather than the feature flag
    // wherever the answer has to be true for the frame to be correct.
    public static bool Available => available ??= Validate();

    // Whether §27 should compose as a max this frame: asked for, and possible.
    public static bool MaxActive =>
        CelestialLightingFeatures.VectorLightShaderMax && Available;

    // Whether §27 should compose as a surface lift this frame: asked for, and drawable. The blend
    // state lives on the material built from our shader, so a machine that fell back to MoteGlow
    // gets the additive pass and this answers false however the flag is set.
    public static bool SurfaceLiftActive =>
        CelestialLightingFeatures.VectorLightSurfaceLift && MaxActive;

    // Whether §27 should draw the surface lift as a SECOND pass on top of the additive beam this
    // frame: asked for, drawable, and not already what the primary pass is doing.
    //
    // The third clause is what makes the two flags exclusive rather than additive. With the surface
    // lift on, the primary pass already carries the multiply blend, so layering another one is a
    // duplicate rather than a layer and an arm setting both would measure the surface lift under the
    // wrong name. Stated here, next to the flag it excludes, rather than in the draw where the two
    // reads would sit thirty lines apart.
    //
    // The ROOF test is deliberately NOT here. It is per emitter, and this property is asked once per
    // frame; keeping "can this be drawn at all" apart from "does this emitter qualify" is what lets
    // the draw cull on the cheap answer first. See VectorLightOverlay.DrawIndoorMultiply.
    public static bool IndoorMultiplyActive =>
        CelestialLightingFeatures.VectorLightIndoorMultiply && MaxActive && !SurfaceLiftActive;

    // The verdict on the batched variant, cached beside Available and for the same reason.
    private static bool? batchAvailable;

    // Whether one draw call can carry several emitters on this machine. THREE SEPARATE THINGS CAN
    // SAY NO and each of them is a real configuration rather than a defensive nicety:
    //
    //   - the loaded shader may not declare the keyword at all, which is what a STALE ASSET BUNDLE
    //     looks like. The harness's --mod-overlay swaps assemblies and leaves AssetBundles/ coming
    //     from the main checkout, so a branch that changed the shader and was live-tested through an
    //     overlay is running new C# against the old compiled program. Without this check that
    //     arrangement does not fail — EnableKeyword on a keyword no variant declares is a no-op, the
    //     per-emitter program runs with no _Color bound, and the frame is black fans with green
    //     probes. See the overlay-swaps-assemblies-only trap;
    //   - the machine may not support texture arrays, which is where every emitter's square lives;
    //   - it may not support copying a Texture2D into a slice of one, which is how they get there.
    //     Graphics.CopyTexture between different texture types needs DifferentTypes specifically,
    //     and Basic alone is not enough.
    //
    // All three land as false and the pass draws per emitter, which is the shipped path — a batching
    // optimisation that cannot run must cost a frame nothing but the question.
    public static bool BatchAvailable => batchAvailable ??= ValidateBatch();

    // Whether this frame should batch: asked for, possible, and drawing through our own program at
    // all. The fallback additive path goes through MoteGlow, which has no array to read per-emitter
    // colour out of, so there is nothing to batch there however the flag is set.
    public static bool BatchActive =>
        CelestialLightingFeatures.VectorLightDrawBatch && MaxActive && BatchAvailable;

    // `queueOffset` is added to MoteGlow's own queue, and is 0 for everything except the indoor
    // multiply layer. Passed rather than defaulted so that every caller has to have an opinion about
    // where in the frame its pass lands — the one thing this file's own header calls the single most
    // expensive lesson of building the feature.
    public static Material NewMaterial(Texture2D gradient, bool surfaceLift, int queueOffset)
    {
        Material material = new Material(Loaded) { mainTexture = gradient };

        // ADDING LIGHT VERSUS BRIGHTENING WHAT IS THERE, and the fragment program does not know
        // which it is doing. One/One makes the pass additive — the frame gains the program's output.
        // DstColor/One makes it dst * (1 + output), so the beam scales the surface it lands on and
        // carries that surface's own texture into the lit region. See the surface lift in DESIGN.md for
        // why a lamp beam wants the second and §11a's aurora wants the first.
        material.SetFloat(SrcBlendId, (float)(surfaceLift ? BlendMode.DstColor : BlendMode.One));
        material.SetFloat(DstBlendId, (float)BlendMode.One);

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
        //
        // THE OFFSET IS HOW THE INDOOR MULTIPLY LAYER GETS TO BE "ON TOP", and it has to be a queue
        // rather than a submission order. Two Graphics.DrawMesh calls of one fan at one altitude in
        // one queue are tied on every key Unity sorts transparent geometry by, and a tie is resolved
        // by nothing this repo can rely on. Add-then-multiply and multiply-then-add differ by a real
        // amount — see VectorLightMath.IndoorMultiplyFrame — and both look like the feature working,
        // so a coin flip here would be a silently wrong composition rather than a visible bug.
        // MoteGlow + 1 keeps the layer above the additive pass and still well below the indoor mask's
        // own quads at 3160, so nothing else in the frame moves.
        material.renderQueue = ShaderDatabase.MoteGlow.renderQueue + queueOffset;

        return material;
    }

    // The same material as NewMaterial, with the batched variant selected.
    //
    // A SEPARATE MATERIAL AND NOT A KEYWORD FLIPPED PER DRAW. Keywords are material state, and
    // Graphics.DrawMesh is deferred — the draws are queued and resolved later — so toggling the
    // keyword between two calls on one material would give every draw in the frame whichever setting
    // was written last. It is the same trap that puts the colour on a property block, one level up:
    // anything that varies per draw has to be per draw, and a keyword cannot be.
    public static Material NewBatchMaterial(Texture2D gradient, bool surfaceLift, int queueOffset)
    {
        Material material = NewMaterial(gradient, surfaceLift, queueOffset);

        material.EnableKeyword(BatchKeyword);

        return material;
    }

    // Everything one batched draw carries, on its own block. The arrays are passed whole rather than
    // by count because SetVectorArray fixes the uniform array's length on first use — Unity's own
    // documented trap — so the caller keeps one array at BatchCapacity and writes a prefix of it.
    // Unused tails are never indexed: the mesh only carries slots the caller filled.
    public static void SetBatch(
        MaterialPropertyBlock props, Texture array, Vector4[] colors, Vector4[] parameters)
    {
        props.SetTexture(VanillaArrayId, array);
        props.SetVectorArray(BatchColorId, colors);
        props.SetVectorArray(BatchParamsId, parameters);
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

    // The sky half of the surface lift's divisor, in vanilla's glow units. ZERO IS THE ADDITIVE PASS,
    // not a disabled lift: the fragment program divides only when this is positive, so one property
    // selects the composition and there is no second flag inside the shader to disagree with the
    // blend state on the material.
    public static void SetSkyAmbient(MaterialPropertyBlock props, float ambient)
    {
        props.SetFloat(SkyAmbientId, ambient);
    }

    // Runs once, behind BatchAvailable's cache. Each refusal is logged as a MESSAGE and not a
    // warning: unlike a missing shader, none of these is a broken installation. They are machines
    // and bundles on which a performance path does not apply, and the frame is correct without it.
    private static bool ValidateBatch()
    {
        if (!Available)
            return false;

        // The stale-bundle check. LocalKeyword answers what the LOADED program declares rather than
        // what this assembly believes it declares, which is exactly the gap an assembly-only overlay
        // opens. Asked through the keyword space rather than by enabling it and reading it back,
        // because EnableKeyword succeeds for a keyword that does not exist.
        if (!new LocalKeyword(Loaded, BatchKeyword).isValid)
        {
            Log.Message(
                "[CelestialLighting] Shader '" + ShaderPath + "' does not declare the '" + BatchKeyword
                + "' variant, so the asset bundle predates vector lighting's batched draw. Drawing "
                + "one emitter at a time. Rebuild the bundle with Tools/ShaderBundle/build.sh.");
            return false;
        }

        if (!SystemInfo.supports2DArrayTextures)
        {
            Log.Message(
                "[CelestialLighting] This system does not support 2D texture arrays, so vector "
                + "lighting draws one emitter at a time.");
            return false;
        }

        // DifferentTypes specifically, not Basic. Basic covers a Texture2D to a Texture2D; putting
        // one into a SLICE of an array is a copy between texture types, and a machine with only
        // Basic silently copies nothing — every batched emitter would then sample an empty slice and
        // subtract no vanilla, which is a doubled lighting model rather than a missing one.
        if ((SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) == 0)
        {
            Log.Message(
                "[CelestialLighting] This system cannot copy a texture into a texture-array slice, "
                + "so vector lighting draws one emitter at a time.");
            return false;
        }

        return true;
    }

    // Runs once, behind Available's cache. Returns a verdict rather than a shader because the shader
    // is not ours to hold — see Loaded.
    private static bool Validate()
    {
        Shader shader = Loaded;

        // LoadShader does not report failure to its caller — it logs a warning and hands back
        // ShaderDatabase.DefaultShader, which is Map/Cutout. Rendering our additive pass through a
        // cutout shader would not fail, it would draw opaque black quads over the map, so identity
        // against the default is the check that matters and not a null test.
        if (shader == null || shader == ShaderDatabase.DefaultShader)
        {
            Log.Warning(
                "[CelestialLighting] Could not load shader '" + ShaderPath + "' from the mod's asset "
                + "bundles. §27's max composition is unavailable; falling back to the crossfade.");
            return false;
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
            return false;
        }

        return true;
    }
}
