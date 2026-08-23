using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead.
//
// ================================================================================================
// WHY THIS PROBE IS PART OF THE FEATURE AND NOT AN EXTRA
//
// Moving the curtain's field into CelestialAurora.shader is the first place in this repo where an
// offline-tested pure core stops governing what is drawn. The cloud volume does not have this
// problem — CloudRaymarchMath bakes a density field on the CPU and the shader only marches it, so
// the pure core still decides the pixels. Here the whole field is HLSL.
//
// That matters because of how such a port fails. AuroraCurtainTests pins tileability at both seams,
// slice identity, palette-band coverage and drift-wrap bit-identity, and after the port every one of
// those still passes — against a C# twin that no longer draws anything. A transposed seed offset, a
// dropped `- 0.5`, a `<` where the core has `<=`: all of them produce a perfectly plausible aurora,
// on a subsystem whose entire output is "plausible drifting light". There is no screenshot anyone can
// look at and no offline test that can fail.
//
// So the core keeps a second job. This probe renders the shader, reads it back, and compares it to
// AuroraCurtainHemRays evaluated at the same coordinates. It is the only thing standing between a
// typo in the port and an aurora that is simply a *different* aurora.
//
// ================================================================================================
// WHAT IT REPORTS, AND WHY IT IS A MEAN AND NOT A MAX
//
// Mean absolute per-channel difference over SampleSize² fragments, in 0-1 units, against a CPU
// reference computed at the same (u, v). Near zero is agreement.
//
// A max would be the obvious statistic and it would be the wrong one, for a reason specific to
// lattice noise. The field's coordinate is pushed through floor() to pick a lattice cell, and the
// hash of a neighbouring cell is not a nearby value — it is an unrelated one. Any float difference
// at all between GPU and CPU evaluation, down to the last bit, flips the cell for fragments sitting
// exactly on a lattice boundary, and those fragments then differ by an arbitrary amount. That is not
// a port error, it is what value noise does at a boundary, and a max would report it as total
// disagreement forever. A handful of flipped fragments out of SampleSize² move a mean by ~1/N.
//
// The readback is 8-bit, so quantisation alone puts a floor of about 1/255 ≈ 0.004 on this number
// before anything else is wrong. Anything under ~0.01 is the port agreeing; the scenario pin says so.
public sealed class AuroraShaderAgreementProbe : IProbe
{
    // Big enough that a localised mistake — one curtain of three, one band of hue — cannot hide
    // between samples, small enough that a synchronous GPU readback stays cheap. The field's finest
    // feature is a ray, and at 60 ray periods across the tile 256 samples give ~4 per ray.
    private const int SampleSize = 256;

    // An arbitrary but fixed instant, and it must be fixed: this probe compares two renderers, so
    // letting it drift with the game clock would mean a failure could be either a port error or the
    // two sides having been asked about different moments. Chosen off a lattice multiple so the
    // sampled columns are not all sitting on integer noise coordinates, which is the one place the
    // boundary-flip caveat above would be over-represented.
    private const float FieldTime = 123457f;

    // Pinned rather than read from the live driver, for the same reason FieldTime is. A green tint at
    // the field's own weight exercises the lerp toward the driver colour without making the answer
    // depend on which condition happens to be running when the probe is read.
    private static readonly Color DriverTint = new Color(0.2f, 0.9f, 0.3f);

    public string Name => "aurora_shader_agreement";

    public float Read(Map map)
    {
        // A machine with no usable bundle has nothing to compare. Reporting a perfect zero would be a
        // lie of exactly the shape the harness warns about elsewhere — a table of zeros reading as
        // "this agrees" rather than "nothing was measured" — so this reports -1, which no real
        // agreement reading can produce and which a scenario pin will reject on sight.
        if (!AuroraShader.Available)
            return -1f;

        Texture2D rendered = RenderField();

        if (rendered == null)
            return -1f;

        float difference = CompareToCore(rendered);
        Object.Destroy(rendered);
        return difference;
    }

    // Draws one tile of the field into an offscreen target and reads it back.
    //
    // Graphics.Blit rather than a hand-rolled GL quad: it is the standard path, it draws a full-target
    // quad with UV running 0-1, and the orientation question it raises is handled in CompareToCore
    // rather than guessed at here.
    private static Texture2D RenderField()
    {
        Material material = AuroraShader.NewFieldMaterial();

        // One repeat, no offset. This is what the sheet materials would carry for an unmirrored
        // display, and it makes the rendered tile directly comparable to a CPU bake of the same tile.
        material.mainTextureScale = Vector2.one;
        material.mainTextureOffset = Vector2.zero;

        // White at full alpha, so the readback carries the field's own premultiplied colour rather
        // than a display's brightness on top of it.
        material.color = Color.white;

        AuroraShader.SetFieldTime(material, FieldTime);
        AuroraShader.SetDriverTint(material, DriverTint, AuroraFieldRegistry.Active.TintWeight);

        RenderTexture target = RenderTexture.GetTemporary(
            SampleSize, SampleSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

        // The shader is additive (Blend One One), so the target has to start black or every fragment
        // reads whatever the pooled RenderTexture happened to contain.
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;
        GL.Clear(false, true, Color.black);
        RenderTexture.active = previous;

        Graphics.Blit(Texture2D.blackTexture, target, material);

        Texture2D readback = new Texture2D(SampleSize, SampleSize, TextureFormat.RGBA32, false);
        RenderTexture.active = target;
        readback.ReadPixels(new Rect(0f, 0f, SampleSize, SampleSize), 0, 0);
        readback.Apply();
        RenderTexture.active = previous;

        RenderTexture.ReleaseTemporary(target);
        Object.Destroy(material);
        return readback;
    }

    // Mean absolute per-channel difference against the pure core, taking the better of the two
    // vertical orientations.
    //
    // WHY BOTH ORIENTATIONS. Whether a RenderTexture readback comes back the same way up as it went
    // in depends on the graphics API, not on the field: D3D and OpenGL disagree about where a render
    // target's origin is, and Unity papers over it in some paths and not others. A flip is therefore a
    // render-target convention and not a statement about the aurora, while this probe exists to
    // answer a question about the aurora. Getting it wrong the other way would be worse: a probe that
    // reported total disagreement on half the machines it ran on would be switched off, and a
    // switched-off guard guards nothing.
    //
    // Orientation on the DRAW path is not left to this. That is a question about pixels on screen,
    // and the live A/B captures answer it — an upside-down curtain is the single most obvious thing
    // in a screenshot.
    private static float CompareToCore(Texture2D rendered)
    {
        Color32[] pixels = rendered.GetPixels32();

        float direct = 0f;
        float flipped = 0f;

        for (int y = 0; y < SampleSize; y++)
        {
            for (int x = 0; x < SampleSize; x++)
            {
                // Fragment centres, which is where the rasteriser samples. Comparing at texel corners
                // instead would put every reading half a texel away from the field the GPU drew and
                // report a real disagreement that is entirely an indexing convention.
                float u = (x + 0.5f) / SampleSize;
                float v = (y + 0.5f) / SampleSize;

                Color32 got = pixels[y * SampleSize + x];
                direct += ChannelError(got, u, v);
                flipped += ChannelError(got, u, 1f - v);
            }
        }

        float samples = SampleSize * SampleSize * 3f;
        return Mathf.Min(direct / samples, flipped / samples);
    }

    // The core's answer at one point, composed exactly as the shader composes it: palette colour,
    // lerped toward the driver tint, premultiplied by the field's alpha.
    //
    // AuroraCurtainHemRays.At rather than FillRows, deliberately. FillRows is the bake's path and
    // quantises to bytes at texel corners; At is the reference definition the bake is itself tested
    // against, so comparing against it checks the shader against the FIELD rather than against the
    // other renderer's rounding.
    private static float ChannelError(Color32 got, float u, float v)
    {
        AuroraCurtainHemRays.Sample sample = AuroraCurtainHemRays.At(u, v, FieldTime);
        AuroraMath.Rgb palette = AuroraCurtainHemRays.PaletteColor(sample.Hue);

        float weight = AuroraFieldRegistry.Active.TintWeight;
        float alpha = sample.Alpha;

        float r = Mathf.Lerp(palette.R, DriverTint.r, weight) * alpha;
        float g = Mathf.Lerp(palette.G, DriverTint.g, weight) * alpha;
        float b = Mathf.Lerp(palette.B, DriverTint.b, weight) * alpha;

        return Mathf.Abs(got.r / 255f - r)
             + Mathf.Abs(got.g / 255f - g)
             + Mathf.Abs(got.b / 255f - b);
    }
}
