using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// §25c: whether the raymarch shader actually loaded, and what its volume bake cost (issue #144).
//
// THIS PROBE EXISTS TO STOP A PROFILING RUN LYING. Every failure in the §25c load path degrades to
// §25b's baked atlas ON PURPOSE — a missing AssetBundle, a shader the card will not compile, a
// graphics API without 3-D textures. That is the right behaviour for a player and the worst possible
// behaviour for a measurement, because a run that quietly fell back produces a complete, healthy,
// entirely plausible profiler table for a feature that was never switched on. "Zeros are not a
// measurement" applies to small numbers just as much as to literal zeros.
//
// So a scenario measuring the volumetric path must PIN `cloud_volume_shader` at 1 next to whatever
// it is really asking about, and a run where the bundle failed to load then fails loudly instead of
// reporting the shader as free.
public sealed class CloudVolumeShaderProbe : IProbe
{
    public enum Metric
    {
        // 1 when the shader loaded, compiled and has its volume; 0 when this run is silently drawing
        // §25b instead.
        Available,

        // What FillBlobVolume cost, in milliseconds. Since §25e that is WALL-CLOCK ON A BACKGROUND
        // THREAD, spread across cores, not main-thread time — the question it used to answer
        // ("should this move to a background Task") has been answered, and it now answers two
        // others: whether the parallel split is working on this machine (a value near the old serial
        // number means it is not), and how long after load the volumetric path stays unavailable.
        BakeMilliseconds,

        // What the main thread spent handing the finished bake to Unity, in milliseconds. §25e's
        // whole claim is that this is all that is left of BakeMilliseconds on the critical path, so
        // it is measured separately rather than folded in.
        UploadMilliseconds,

        // What §25's two 2-D atlas bakes cost the MAIN thread at load, summed. The one part of the
        // cloud load path §25e made faster in place rather than moving, so this is the number that
        // says whether leaving it there is still defensible.
        AtlasBakeMilliseconds,

        // 1 once the background bake has finished, whether or not it has been uploaded yet.
        //
        // Pin this NEXT TO Available, not instead of it. `ready 1, available 0` is a texture Unity
        // refused; `0, 0` at the same instant is simply early and wants a wait, not a bug report.
        // Without the pair those read identically.
        BakeFinished,

        // Whether the driver takes the one-byte volume format. An unsupported format is the OTHER
        // way this path draws solid rectangles — the march reads a density of 1 everywhere — and it
        // is indistinguishable on screen from an unbound sampler, so it is worth a number.
        FormatSupported,
    }

    private readonly Metric metric;

    public CloudVolumeShaderProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public string Name { get; }

    // Map-independent: the shader and its volume are static, built once at load, and shared by every
    // map. Taking the parameter and ignoring it is the IProbe contract, not an oversight.
    public float Read(Map map)
    {
        if (metric == Metric.Available)
            return CloudVolumeShader.Available ? 1f : 0f;

        if (metric == Metric.FormatSupported)
            return CloudVolumeShader.VolumeFormatSupported ? 1f : 0f;

        if (metric == Metric.BakeFinished)
            return CloudVolumeShader.BakeFinished ? 1f : 0f;

        if (metric == Metric.UploadMilliseconds)
            return (float)CloudVolumeShader.UploadMilliseconds;

        if (metric == Metric.AtlasBakeMilliseconds)
            return (float)CloudSheetOverlay.AtlasBakeMilliseconds;

        return (float)CloudVolumeShader.BakeMilliseconds;
    }
}
