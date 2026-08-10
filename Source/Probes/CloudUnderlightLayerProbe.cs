using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads §23b (issue #88 option 2) through CloudUnderlightLayer, the SAME entry point
// CloudUnderlightOverlay.Draw calls, so a harness reading can never disagree with what was drawn. That
// discipline is why SnowGlare.AlphaFor exists in the shape it does, and it matters at least as much
// here: option 2's open question is whether the drawn structure reads as sky or as ground stains, and
// a probe computing its own answer would make the frames unfalsifiable.
//
// THREE METRICS OFF ONE CLASS, because the headline claim is a conjunction and no single number
// carries it. `Strength` is what reaches the material; `Fraction` is which of the two cloud sources
// was live (§13's deck opacity, or §22's Clear-weather fraction — see CloudFractionFor); and
// `FieldPeak` is how much structure the field actually has to draw at that fraction, which is the
// quantity that must go to ZERO at both a clear sky and a solid overcast. A scenario that pinned only
// the strength could not tell "the sun is in the window and the sky is uniform" from "the sun is in
// the window and the field is broken".
public sealed class CloudUnderlightLayerProbe : IProbe
{
    public enum Metric
    {
        Strength,
        Fraction,
        FieldPeak,
    }

    // The field's peak residual is a property of (fraction, tile seed) only, and re-walking 4,096
    // noise samples per probe read would make a survey scenario noticeably slower for no new
    // information. Cached on exactly the pair it depends on, the same shape CloudCoverClock caches its
    // own hourly sample.
    private static float[] _intensity;
    private static int _cachedFractionStep = int.MinValue;
    private static int _cachedSeed = int.MinValue;
    private static float _cachedPeak;

    private readonly string _name;
    private readonly Metric _metric;

    public CloudUnderlightLayerProbe(string name, Metric metric)
    {
        _name = name;
        _metric = metric;
    }

    public string Name => _name;

    public float Read(Map map)
    {
        // The flag is checked directly rather than inferred from any pure-layer return, for the same
        // reason CloudUnderlightProbe does it: "off" must read as a clean zero on every metric so a
        // scenario can pin the true-no-op invariant, and one of the inputs (a cloud fraction of 0) is
        // a legitimate live value rather than a sentinel.
        if (!CelestialLightingFeatures.CloudUnderlightLayer)
            return 0f;

        if (_metric == Metric.Strength)
            return CloudUnderlightLayer.StrengthFor(map);

        float fraction = CloudUnderlightLayer.CloudFractionFor(map);
        if (_metric == Metric.Fraction)
            return fraction;

        return PeakResidual(fraction, map.Tile.tileId);
    }

    private static float PeakResidual(float fraction, int seed)
    {
        int step = (int)(fraction * 128f + 0.5f);
        if (step == _cachedFractionStep && seed == _cachedSeed)
            return _cachedPeak;

        int n = CloudUnderlightField.Resolution;
        _intensity ??= new float[n * n];

        float mean = CloudUnderlightField.FillIntensity(_intensity, n, n, step / 128f, seed);

        float peak = 0f;
        for (int i = 0; i < _intensity.Length; i++)
        {
            float residual = CloudUnderlightField.Residual(_intensity[i], mean);
            if (residual > peak)
                peak = residual;
        }

        _cachedFractionStep = step;
        _cachedSeed = seed;
        _cachedPeak = peak;
        return peak;
    }
}
