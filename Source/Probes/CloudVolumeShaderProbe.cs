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

        // What FillBlobVolume cost, in milliseconds, on the main thread during load. Reported so the
        // "should this move to a background Task" question has a measured answer on real hardware
        // rather than an offline one from a different machine.
        BakeMilliseconds,

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

        return (float)CloudVolumeShader.BakeMilliseconds;
    }
}
