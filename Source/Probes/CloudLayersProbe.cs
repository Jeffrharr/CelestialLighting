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
public sealed class CloudLayersProbe : IProbe
{
    public enum Metric
    {
        Strength,
        Fraction,
        FieldPeak,

        // §23c and §25, reported off the same class because they read the same field through the same
        // adapter and a scenario almost always wants them next to each other — the whole point of the
        // three lanes is that they are one deck seen three ways, and three probe classes would make
        // that look like three subsystems that happen to run together.
        ShadowAlpha,
        SheetAlpha,

        // §25b: how the sky is layered right now, and where each layer is in its own sunset.
        //
        // THE LAST TWO ARE THE ONLY WAY THE HEADLINE CLAIM IS MEASURABLE. §25b's point is that the
        // decks lose the sun IN ORDER, so at the right elevation the low cloud is a grey mass while
        // the cirrus above it is still burning. That is a statement about two numbers at one instant
        // — no single probe, and no screenshot on its own, can distinguish it from "the sunset
        // happened" — so a scenario pins UnderlitLow and UnderlitHigh together, with sun_elevation
        // beside them so a later clock change fails loudly instead of quietly emptying the frames.
        DeckMeanAltitude,
        HighDeckShare,
        UnderlitLow,
        UnderlitHigh,
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

    public CloudLayersProbe(string name, Metric metric)
    {
        _name = name;
        _metric = metric;
    }

    public string Name => _name;

    public float Read(Map map)
    {
        // §23c and §25 gate themselves inside their own adapters, which check their own flags — so
        // unlike the three metrics below they need no flag test here, and must not get the
        // CloudUnderlightLayer one: the three lanes ship independently and a scenario has to be able
        // to turn on exactly one of them.
        if (_metric == Metric.ShadowAlpha)
            return CloudLayers.ShadowAlphaFor(map);

        if (_metric == Metric.SheetAlpha)
            return CloudLayers.SheetAlphaFor(map);

        if (IsDeckMetric(_metric))
            return ReadDeck(map);

        // The flag is checked directly rather than inferred from any pure-layer return, for the same
        // reason CloudUnderlightProbe does it: "off" must read as a clean zero on every metric so a
        // scenario can pin the true-no-op invariant, and one of the inputs (a cloud fraction of 0) is
        // a legitimate live value rather than a sentinel.
        //
        // Note the cloud FRACTION metric is gated on §23b's flag too, which is a wart: the fraction is
        // a property of the weather rather than of any lane. It stays that way because
        // cloud_underlight_layer.json's off/on pins were measured against it, and this repo does not
        // rewrite a measured pin to suit a later tidy-up.
        if (!CelestialLightingFeatures.CloudUnderlightLayer)
            return 0f;

        if (_metric == Metric.Strength)
            return CloudLayers.StrengthFor(map);

        float fraction = CloudLayers.CloudFractionFor(map);
        if (_metric == Metric.Fraction)
            return fraction;

        return PeakResidual(fraction, map.Tile.tileId);
    }

    private static bool IsDeckMetric(Metric metric) =>
        metric == Metric.DeckMeanAltitude
        || metric == Metric.HighDeckShare
        || metric == Metric.UnderlitLow
        || metric == Metric.UnderlitHigh;

    // §25b's four metrics, all read through the same pure core the overlay calls rather than
    // recomputed here — the discipline this whole file exists to keep.
    //
    // UNGATED ON §23b's FLAG, like ShadowAlpha and SheetAlpha above and unlike the three below it.
    // The deck mixture is a property of the WEATHER, not of any one lane, and a scenario running §25
    // alone has to be able to see it.
    private float ReadDeck(Map map)
    {
        if (_metric == Metric.UnderlitLow || _metric == Metric.UnderlitHigh)
        {
            int deck = _metric == Metric.UnderlitLow ? CloudDeckMath.LowDeck : CloudDeckMath.HighDeck;
            return CloudSheetMath.UnderlitFraction(
                SolarPosition.ElevationForMap(map), CloudDeckMath.ShadowEntryDegrees(deck));
        }

        float[] weights = new float[CloudDeckMath.DeckCount];
        CloudDeckMath.MixtureFor(weights, WeatherDimming.CloudAltitudeMetresFor(map));

        return _metric == Metric.HighDeckShare
            ? weights[CloudDeckMath.HighDeck]
            : CloudDeckMath.MeanAltitudeMetres(weights);
    }

    private static float PeakResidual(float fraction, int seed)
    {
        int step = (int)(fraction * 128f + 0.5f);
        if (step == _cachedFractionStep && seed == _cachedSeed)
            return _cachedPeak;

        int n = CloudField.Resolution;
        _intensity ??= new float[n * n];

        float mean = CloudField.FillIntensity(_intensity, n, n, step / 128f, seed);

        float peak = 0f;
        for (int i = 0; i < _intensity.Length; i++)
        {
            float residual = CloudField.Residual(_intensity[i], mean);
            if (residual > peak)
                peak = residual;
        }

        _cachedFractionStep = step;
        _cachedSeed = seed;
        _cachedPeak = peak;
        return peak;
    }
}
