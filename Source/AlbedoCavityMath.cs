namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, the same discipline
// Formulas.cs / IlluminanceMath.cs / ShadowFillMath.cs follow. Linked into both the shipped mod
// (net481) and the test project (net8.0) by a single <Compile Include>, so the model that runs in
// RimWorld is the model under test.
//
// Subsystem 21 (DESIGN.md §21): THE SURFACE-CLOUD LIGHT CAVITY.
//
// Fresh snow under a thick overcast is far brighter than the same overcast over bare ground, and —
// counterintuitively — brighter in diffuse light than a CLEAR sky over that same snow. The
// mechanism is not "snow is white so the map is brighter". It is a light-trapping cavity: the
// ground throws most of the light back up, the cloud base throws most of that back down, and the
// bounce repeats. Summing the geometric series gives
//
//     A = 1 / (1 - a_surface * a_cloud)
//
// and that single expression is the whole physical content of this file. Everything else here is
// either an albedo constant or the plumbing that turns RimWorld's grids into one.
//
// WHY THE CORE IS KEYED ON ALBEDO AND NOT ON SNOW DEPTH. RimWorld 1.6 generalized snow into
// "weather buildup": SnowGrid.GetCategory returns a WeatherBuildupCategory via
// WeatherBuildupUtility, and Odyssey ships Map.sandGrid — a byte-for-byte sibling of SnowGrid
// (same NativeArray<float> depth grid, same maintained `totalDepth` running total, same
// MaxDepth = 1f) for sand on desert maps. Sand is not white but it is not bare ground either
// (~0.35 against soil's ~0.20), so it falls out of the SAME formula with a different constant.
// Keying the core on an albedo rather than on a snow depth means that generalization costs one
// call site and no new maths. See SandAlbedo below for the constant that is already sitting here
// waiting for it.
//
// WHY THE SHIPPED QUANTITY IS A *RATIO* AND NOT `A` ITSELF. A over bare ground is already 1.18
// under an overcast — the cavity does not switch on when it snows, it merely gets much stronger.
// Every brightness anchor this mod ships (IlluminanceMath's lux table, §7's starlight/airglow
// floors) was read off published measurements taken over ORDINARY GROUND, so the bare-ground
// cavity is already baked into them. Applying raw `A` would therefore double-count it and brighten
// a mud-and-grass map that nothing had changed. CavityGain divides by the bare-ground baseline
// instead, which makes "no buildup" exactly 1.0 — bit-identical to what the mod did before this
// subsystem existed, with no epsilon and no tuning. That is the regression pin
// AlbedoCavityMathTests leads with, and it is a structural property of the ratio rather than a
// number anyone chose.
//
// WHAT THIS FILE DELIBERATELY DOES NOT MODEL. Cloud EXTINCTION of the night sources. A thick deck
// both closes the cavity (modelled here) and attenuates the starlight and moonlight arriving
// through it (not modelled anywhere in the mod — §13 owns weather attenuation and owns it on the
// COLOUR channel, never on .glow). So the amplified night floor this feeds is an upper bound on a
// snowy overcast night. That is a deliberate split, not an oversight: the two effects live on
// different channels by §13's design, and what the player actually sees is their composition —
// glow up by CavityGain here, rendered colour down by WeatherDimmingMath.SkyTintFactor there.
// AlbedoCavityMathTests states the headline inversion in exactly those composed terms rather than
// in raw gain, so the claim under test is the one the screen makes. DESIGN.md §21 records this as
// the first thing the live A/B should look at.
public static class AlbedoCavityMath
{
    // --- Surface albedos ---
    //
    // Published shortwave broadband albedos, mid-range of the usual quoted spans. These are the
    // numbers that make the effect large: snow swings the surface reflectance by roughly a factor
    // of four, which is why the cavity goes from barely-there to dominant.

    // Soil, rock and vegetation: 0.10-0.25. This is also the BASELINE the gain is measured against
    // (see the header), so it is the one constant here that is load-bearing twice — change it and
    // every gain moves, including the no-buildup case, which is why the tests pin the no-buildup
    // gain as an exact 1.0 rather than against a computed expectation.
    public const float BareGroundAlbedo = 0.20f;

    // Settled or melting snow: 0.40-0.60. Reached at ShallowBuildupDepth, where RimWorld's own
    // WeatherBuildupUtility stops calling the cover a "dusting" — a thin, patchy, part-melted layer.
    public const float SettledSnowAlbedo = 0.50f;

    // Fresh snow: 0.80-0.90. Reached only at full depth, i.e. a map that has just been buried.
    public const float FreshSnowAlbedo = 0.85f;

    // Dry sand: ~0.35. NOT consumed by any shipped call site yet — Map.sandGrid is the Odyssey
    // desert half of the 1.6 weather-buildup generalization and §21 deliberately shipped the snow
    // half first (DESIGN.md §21). It lives here rather than in a future commit because its presence
    // is the argument for keying the core on albedo: wiring sand up is one adapter read and this
    // constant, with nothing in this file changing.
    public const float SandAlbedo = 0.35f;

    // The buildup depth at which the ground underneath has stopped showing through. RimWorld's own
    // WeatherBuildupUtility.GetBuildupCategory puts the Dusting/Thin boundary here (0.25), which is
    // exactly the "you can no longer see the dirt" threshold this needs — so the two agree by
    // construction instead of by coincidence, and a Ludeon retune of that boundary is a visible
    // disagreement rather than a silent drift.
    public const float ShallowBuildupDepth = 0.25f;

    // --- Overhead albedos: the other wall of the cavity ---

    // A clear sky's Rayleigh backscatter, ~0.10. This is why the effect is NOT symmetric and why
    // "snowy maps are brighter" is the wrong model: with nothing overhead to bounce light back, the
    // snow's 0.85 buys almost nothing. The cavity needs both walls.
    public const float ClearSkyAlbedo = 0.10f;

    // A thick overcast cloud base, ~0.75. Stratus reflects most of what hits it from below as well
    // as from above; that is the same optical property that makes the TOP of the deck bright from
    // an aircraft.
    public const float OvercastCloudAlbedo = 0.75f;

    // --- The bound on the series ---

    // The largest surface*cloud product the series is evaluated at. At a product of 1 the geometric
    // series diverges (a perfect mirror facing a perfect mirror never loses a photon), and no real
    // pair of surfaces reaches it — our own worst case is 0.85 * 0.75 = 0.6375, well clear.
    //
    // This is therefore a BOUND rather than a tuning: it exists so that a modded def declaring
    // WeatherCloudDeck.opacity = 1 alongside some future albedo of 1 produces a large number
    // instead of an infinity that propagates into SkyTarget.glow as a NaN. Nothing in the shipped
    // configuration can reach it, which is what the tests assert rather than the cap's value.
    public const float MaxCavityProduct = 0.9f;

    // --- Inputs ---

    // The albedo of whatever is overhead, from §13's cloud-deck opacity. Interpolating between the
    // two anchors above rather than switching on a threshold is what makes the effect ramp with a
    // weather transition instead of popping: WeatherDimming.CloudOpacityFor already blends across
    // WeatherManager.TransitionLerpFactor, so a front rolling in closes the cavity smoothly.
    //
    // Note which end 0 is. An opacity of 0 is a CLEAR sky, not "no sky" — a clear sky still
    // backscatters, so the floor here is ClearSkyAlbedo and not zero. The genuinely skyless cases
    // (caves, pocket maps) are handled upstream: WeatherDimming.CloudOpacityFor returns 0 for them,
    // and their buildup depth is 0 anyway, so the gain is 1 by the surface term regardless.
    public static float CloudBaseAlbedo(float cloudOpacity) =>
        Lerp(ClearSkyAlbedo, OvercastCloudAlbedo, Clamp01(cloudOpacity));

    // The area-averaged albedo of the ground, given the map's mean weather-buildup depth in [0,1]
    // (RimWorld's SnowGrid.MaxDepth is 1f, so the depth is already normalized).
    //
    // TWO SEGMENTS, because two different physical things are happening and a single lerp would
    // conflate them:
    //
    //   0 -> ShallowBuildupDepth        OPTICAL COVER. A dusting does not have snow's albedo, it has
    //                                   a mixture of snow's and the dirt showing through it. This
    //                                   segment is that mixture, and it is also what makes a
    //                                   partially thawed map read as partially bright.
    //
    //   ShallowBuildupDepth -> 1        FRESH VERSUS SETTLED. Once the ground is hidden, the
    //                                   remaining variation is the snowpack's own age. Depth is a
    //                                   fair proxy for it in RimWorld specifically: SnowGrid melts
    //                                   depth down over time, so a deep grid IS a recent fall and a
    //                                   shallow one IS a settling pack. That is a happy accident of
    //                                   the vanilla simulation rather than a general truth, and it
    //                                   is the reason this ramp is honest with one input instead of
    //                                   needing an age we would have to track ourselves.
    //
    // Fully parameterised on its three albedos rather than reading the constants above directly, so
    // the sand arm needs no second copy of this function — pass SandAlbedo where the snow arm
    // passes FreshSnowAlbedo and the shape is unchanged.
    public static float BuildupSurfaceAlbedo(
        float meanBuildupDepth, float bareAlbedo, float shallowAlbedo, float deepAlbedo)
    {
        float depth = Clamp01(meanBuildupDepth);

        if (depth <= ShallowBuildupDepth)
            return Lerp(bareAlbedo, shallowAlbedo, depth / ShallowBuildupDepth);

        float settled = (depth - ShallowBuildupDepth) / (1f - ShallowBuildupDepth);
        return Lerp(shallowAlbedo, deepAlbedo, settled);
    }

    // --- The cavity ---

    // The multi-bounce amplification A = 1 / (1 - a_surface * a_cloud), in [1, 1/(1-MaxCavityProduct)].
    //
    // Shaped to §18's convention (Source/Vacuum.cs): `inVacuum` is the LAST parameter, it is
    // required rather than defaulted, and the vacuum value returns before any atmospheric term is
    // read. The vacuum answer is exactly 1 and the reason is structural rather than a limit being
    // taken: there is no atmosphere, so there is no cloud base, so there is no second wall and no
    // cavity. Note what the shape buys beyond consistency — the two albedo arguments are simply NOT
    // CONSULTED in the vacuum arm, so "an orbital platform's regolith bounces light off nothing" is
    // expressed by the code rather than by a comment promising it.
    public static float Amplification(float surfaceAlbedo, float cloudAlbedo, bool inVacuum)
    {
        if (inVacuum)
            return 1f;

        float product = Clamp01(surfaceAlbedo) * Clamp01(cloudAlbedo);
        if (product > MaxCavityProduct)
            product = MaxCavityProduct;

        return 1f / (1f - product);
    }

    // THE SHIPPED QUANTITY: how much brighter the cavity makes this map than the bare-ground map
    // the mod's brightness anchors were calibrated on. 1.0 means "exactly what shipped before §21".
    //
    // See the file header for why this is a ratio. The two properties worth stating next to the
    // code, because both are what the tests pin:
    //
    //   * Exactly 1 when surfaceAlbedo == bareAlbedo, for EVERY cloud albedo, with no epsilon. The
    //     numerator and denominator are literally the same expression, so a bare map is untouched
    //     by construction rather than by a value happening to land near 1.
    //
    //   * Monotonically non-decreasing in both albedos. Raising the surface albedo raises only the
    //     numerator; raising the cloud albedo raises both, but raises the (larger) numerator faster,
    //     which is the formal statement of "the cavity needs both walls".
    public static float CavityGain(
        float surfaceAlbedo, float bareAlbedo, float cloudAlbedo, bool inVacuum)
    {
        if (inVacuum)
            return 1f;

        float lit = Amplification(surfaceAlbedo, cloudAlbedo, inVacuum);
        float baseline = Amplification(bareAlbedo, cloudAlbedo, inVacuum);
        return lit / baseline;
    }

    // --- Consumer ---

    // Applies the gain to a RimWorld 0..1 glow value, clamped back into range.
    //
    // The clamp is why this exists as a named function instead of a `*` at the call site: §7's night
    // floor is a sum of sources that NightRadianceMath already clamped to [0,1], and multiplying
    // after that clamp can leave the range. Doing the multiply and the re-clamp in one pure,
    // testable place means every consumer of the amplified floor gets the same bounded answer, and
    // that a gain of exactly 1 returns the input bit-identically (Clamp01 of an already-clamped
    // value is the identity), which is what makes the no-buildup regression pin exact.
    public static float AmplifiedGlow(float glow, float cavityGain) =>
        Clamp01(Clamp01(glow) * cavityGain);

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
