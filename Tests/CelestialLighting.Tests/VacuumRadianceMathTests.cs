namespace CelestialLighting.Tests;

/// <summary>
/// Offline coverage for VacuumRadianceMath.cs (§18b, the night light budget on an Odyssey vacuum
/// map) and for NightRadianceMath.NightFloorGlow, the shared floor #31 and #33 also consume.
///
/// The test that matters most is <see cref="VacuumNightFloor_IsDarkerThanEverySurfaceNight"/>, and it
/// was written before any of the physics below it. It encodes the design claim the whole subsystem
/// exists to make — that orbital night is the darkest state this mod can produce — so if the three
/// terms are ever retuned, that is the assertion that must survive, not the individual anchors.
/// </summary>
[TestFixture]
public class VacuumRadianceMathTests
{
    private const float Tolerance = 0.0001f;

    private const float SeaLevelStarlight = NightRadianceMath.DefaultStarlightGlow;   // 0.02
    private const float SeaLevelAirglow = NightRadianceMath.DefaultAirglowGlow;       // 0.02
    private const float MaxMoonlight = NightRadianceMath.DefaultMaxMoonlightGlow;     // 0.15

    private static float SurfaceFloor(float moonlightGlow) =>
        NightRadianceMath.NightFloorGlow(
            SeaLevelStarlight, SeaLevelAirglow, moonlightGlow, MaxMoonlight, inVacuum: false);

    private static float VacuumFloor(float moonlightGlow) =>
        NightRadianceMath.NightFloorGlow(
            SeaLevelStarlight, SeaLevelAirglow, moonlightGlow, MaxMoonlight, inVacuum: true);

    // --- THE DESIGN CLAIM ---

    [Test]
    public void VacuumNightFloor_IsDarkerThanEverySurfaceNight()
    {
        // The claim, in the epic's words: "orbital night should be the darkest state the mod can
        // produce — darker than any surface night, including a new moon."
        //
        // It is a claim about FLOORS — the minimum each environment can reach — which is why both
        // sides are evaluated with the moon contributing nothing. A vacuum night under a full moon is
        // legitimately brighter than a surface new-moon night; the moon is unaffected by any of this
        // and is still up there. What §18b asserts is that when the fill light is gone, orbit is the
        // darkest place the model has.
        //
        // The reason it holds is geometric rather than tuned. Airglow is a 90 km emission layer the
        // platform is above, so it leaves entirely; starlight rises by removing extinction but only
        // by ~1.56x, which does not make up the loss; and planetshine — the thing that replaced
        // moonlight as the dominant reflector — is at its minimum exactly here, because RimWorld's
        // orbits are stationary and a platform in shadow hangs over the planet's own night side.
        float surfaceNewMoon = SurfaceFloor(moonlightGlow: 0f);
        float orbitalNight = VacuumFloor(moonlightGlow: 0f);

        Assert.That(orbitalNight, Is.LessThan(surfaceNewMoon),
            "orbital night must be darker than a surface new-moon night");

        // "Darker than ANY surface night" — every surface night is at least the new-moon floor,
        // because §7's sources add, so beating the new moon beats all of them. Spelled out over a
        // sweep of moon states so the claim is not resting on that one inference.
        foreach (float moonlight in new[] { 0f, 0.01f, 0.05f, MaxMoonlight })
        {
            Assert.That(orbitalNight, Is.LessThan(SurfaceFloor(moonlight)),
                $"orbital night must be darker than a surface night with moonlight {moonlight}");
        }
    }

    [Test]
    public void VacuumNightFloor_IsDarkerThanSurface_AcrossTheWholeExtinctionRange()
    {
        // The claim must not depend on the exact extinction coefficient we picked. Starlight is the
        // one term pushing the vacuum floor UP, and it is bounded by 1/T; even at the most
        // transparent atmosphere anyone quotes (k = 0.20, gain 1.38) it cannot recover the airglow
        // half. Restated as an inequality on the constants so it stays true if the anchor moves.
        float vacuumStarlight = VacuumRadianceMath.StarlightGlow(SeaLevelStarlight);
        Assert.That(vacuumStarlight + VacuumRadianceMath.PlanetshineFloorGlow(MaxMoonlight),
            Is.LessThan(SeaLevelStarlight + SeaLevelAirglow),
            "the starlight gain must not exceed what losing airglow buys back");
    }

    // --- Airglow: pinned at zero ---

    [Test]
    public void Airglow_IsZeroInVacuum()
    {
        Assert.That(VacuumRadianceMath.AirglowGlow, Is.EqualTo(0f));
    }

    [TestCase(0f)]
    [TestCase(0.02f)]
    [TestCase(0.5f)]
    public void VacuumNightFloor_IgnoresTheAirglowArgumentEntirely(float airglowGlow)
    {
        // The airglow parameter is still accepted by the shared signature (so a call site never has
        // to know which atmosphere it is in), and it must have no effect whatsoever in vacuum. A
        // sweep rather than a single case because "ignored" is exactly the property a future refactor
        // could break silently by folding the argument back in.
        float floor = NightRadianceMath.NightFloorGlow(
            SeaLevelStarlight, airglowGlow, 0f, MaxMoonlight, inVacuum: true);
        Assert.That(floor, Is.EqualTo(VacuumFloor(0f)).Within(Tolerance));
    }

    // --- Starlight: the term that goes UP ---

    [Test]
    public void VacuumStarlight_IsBrighterThanSeaLevel()
    {
        // The sign the issue warns about. No atmosphere means no extinction, so the vacuum starlight
        // term sits ABOVE the sea-level one — this is a division, and getting it backwards would
        // still produce a plausible-looking darker orbit for the wrong reason.
        Assert.That(VacuumRadianceMath.StarlightGlow(SeaLevelStarlight),
            Is.GreaterThan(SeaLevelStarlight));
    }

    [Test]
    public void VacuumStarlight_IsTheSeaLevelFloorDividedByTheHemisphericTransmittance()
    {
        // ~0.641 at k = 0.28: a projection-weighted mean over the whole hemisphere, not the
        // zenith-only 0.773, because the heavily-extinguished low sky is a large share of the sky.
        Assert.That(VacuumRadianceMath.StarlightTransmittance, Is.EqualTo(0.6413f).Within(0.001f));
        Assert.That(VacuumRadianceMath.StarlightGlow(SeaLevelStarlight),
            Is.EqualTo(SeaLevelStarlight / VacuumRadianceMath.StarlightTransmittance).Within(Tolerance));
        Assert.That(VacuumRadianceMath.StarlightGlow(SeaLevelStarlight),
            Is.EqualTo(0.0312f).Within(0.0005f));
    }

    [Test]
    public void HemisphericTransmittance_IsBelowTheZenithTransmittance()
    {
        // A guard against the commonest way to get this integral wrong: using sec(z) at the zenith
        // only, which is 10^(-0.4 * 0.28) == 0.773 and would understate the vacuum gain.
        double zenith = System.Math.Pow(10, -0.4 * VacuumRadianceMath.SeaLevelExtinctionMagPerAirmass);
        Assert.That(VacuumRadianceMath.StarlightTransmittance, Is.LessThan((float)zenith));
        Assert.That(VacuumRadianceMath.StarlightTransmittance, Is.GreaterThan(0.5f),
            "but not so far below it that the model has run away");
    }

    // --- Planetshine ---

    [Test]
    public void HorizonCap_MatchesTheOrbitGeometry()
    {
        // acos(6371 / 6571) == 14.17 degrees of surface arc. This is the one number that makes orbit
        // different from the ground: the platform can see terrain up to 14.17 degrees of solar
        // depression ahead of its own, so it is still lit by a sun the platform has lost.
        Assert.That(VacuumRadianceMath.HorizonCapHalfAngleDegrees, Is.EqualTo(14.172f).Within(0.01f));
        // (6371 / 6571)^2 — the planet fills 94% of the sky below, which is why it outranks the moon.
        Assert.That(VacuumRadianceMath.PlanetDiscFillFactor, Is.EqualTo(0.9401f).Within(0.0005f));
    }

    [Test]
    public void Planetshine_IsNegligibleAtOrbitalNight()
    {
        // THE ANSWER TO THE OPEN DESIGN QUESTION, computed rather than asserted. Evaluated at the top
        // of the full-night band (-18 degrees), where planetshine is at its largest over that band,
        // it comes out at ~0.00085 lux — about 1/300th of a full moon.
        Assert.That(VacuumRadianceMath.PlanetshineFloorLux, Is.EqualTo(0.00085f).Within(0.00005f));

        // In glow units that is ~0.0005, which is 40x under §13a's perceptibility threshold — a fact
        // about human vision, reused here rather than restated. So a constant floor is not merely
        // "honest enough": planetshine is below the threshold at which a phase model could show a
        // difference at all.
        Assert.That(VacuumRadianceMath.PlanetshineFloorGlow(MaxMoonlight),
            Is.LessThan(WeatherDimmingMath.PerceptibleDarkening / 10f));
    }

    [TestCase(-18f)]
    [TestCase(-20f)]
    [TestCase(-25f)]
    [TestCase(-30f)]
    public void Planetshine_FallsAsTheSunSinks(float sunElevationDegrees)
    {
        // Monotone across the full-night band, which is what makes the value at -18 the supremum and
        // therefore a safe constant to use as the floor's planetshine term.
        Assert.That(VacuumRadianceMath.PlanetshineLux(sunElevationDegrees),
            Is.LessThanOrEqualTo(VacuumRadianceMath.PlanetshineFloorLux));
    }

    [Test]
    public void Planetshine_IsExactlyZero_OnceTheWholeVisibleCapIsPastAstronomicalTwilight()
    {
        // -18 - 14.17 == -32.2 degrees: below this even the far limb of the visible cap is past the
        // end of astronomical twilight, so there is no sunlit ground anywhere the platform can see
        // and planetshine is not merely small but identically zero. This is the geometry producing
        // the design claim, and it is the reason the night floor's planetshine term does not need a
        // phase model: the term dies of its own accord.
        float belowCap = -(18f + VacuumRadianceMath.HorizonCapHalfAngleDegrees) - 1f;
        Assert.That(VacuumRadianceMath.PlanetshineLux(belowCap), Is.EqualTo(0f).Within(1e-9f));
    }

    [Test]
    public void Planetshine_IsOverwhelmingOverTheDaySide()
    {
        // The other end, and the reason the term is NOT evaluated dynamically inside the night floor.
        // Over lit ground the planet below is a daylight-bright reflector — thousands of lux, four
        // orders of magnitude past the moon. Folding that into a *night* floor would brighten dusk
        // enormously, and it would be double-counting: at a fixed lat/long the sun-platform-planet
        // angle is just the platform's own solar elevation, which §7's night-floor ramp already
        // encodes. The model can state the day-side value; the night floor deliberately does not use it.
        Assert.That(VacuumRadianceMath.PlanetshineLux(0f), Is.GreaterThan(100f));
        Assert.That(VacuumRadianceMath.PlanetshineLux(90f),
            Is.GreaterThan(1000f * VacuumRadianceMath.PlanetshineLux(-6f)));
    }

    [Test]
    public void ReflectedGlow_IsCalibratedOnTheMoon()
    {
        // The lux -> glow conversion is anchored on the moon and nothing else: feeding it a full
        // moon's illuminance must return exactly MaxMoonlightGlow. That identity is what lets
        // planetshine borrow §7's look-calibration by a true photometric ratio instead of inventing
        // a second brightness scale.
        Assert.That(VacuumRadianceMath.ReflectedGlow(IlluminanceMath.FullMoonZenithLux, MaxMoonlight),
            Is.EqualTo(MaxMoonlight).Within(Tolerance));
        Assert.That(VacuumRadianceMath.ReflectedGlow(0f, MaxMoonlight), Is.EqualTo(0f).Within(Tolerance));
    }

    // --- The moon survives alongside planetshine ---

    [Test]
    public void VacuumNightFloor_StillRespondsToTheMoon()
    {
        // Design question: does the moon term survive, or is it replaced by planetshine? It survives.
        // Planetshine outranks it over the day side, but at orbital night — the only regime the floor
        // is consulted in — planetshine is ~zero and the moon is the ONLY reflected source left.
        // Deleting it would flatten every orbital night to the same value and throw away information
        // the model already has.
        float newMoon = VacuumFloor(moonlightGlow: 0f);
        float fullMoon = VacuumFloor(
            NightRadianceMath.MoonlightGlow(1f, 90f, MaxMoonlight));

        Assert.That(fullMoon, Is.GreaterThan(newMoon));

        // The moon survives as a SOURCE and is corrected as a MEASUREMENT (§18c, the photometric half
        // this issue left open). §7's glow scale is anchored on IlluminanceMath.FullMoonZenithLux,
        // which is a sea-level figure with the atmosphere's extinction already in it, so above the
        // atmosphere the same moon is worth 1/ZenithTransmittance == 1.294x the glow. Pinned as a
        // pair with the sea-level value so the correction cannot silently drift into the surface
        // model, and expressed as the ratio rather than as 0.194 so it stays readable as "one
        // atmosphere's worth of extinction removed".
        Assert.That(fullMoon - newMoon,
            Is.EqualTo(MaxMoonlight / VacuumRadianceMath.ZenithTransmittance).Within(Tolerance),
            "the vacuum moon is the sea-level moon with its extinction divided back out");
        Assert.That((fullMoon - newMoon) / MaxMoonlight, Is.EqualTo(1.294f).Within(0.001f));
    }

    [Test]
    public void VacuumNightFloor_KeepsPlanetshineWhenTheAtmosphericFloorsAreOff()
    {
        // The "true pitch-black" toggle zeroes starlight and airglow. It must not zero planetshine,
        // which is reflected sunlight and no more atmospheric than moonlight is — and moonlight
        // already survives that toggle. What is left is ~0.0005 glow, which is far under the
        // perceptibility threshold, so the pitch-black contract survives in substance: an orbital
        // night with the floors off and no moon renders black.
        float floorsOff = NightRadianceMath.NightFloorGlow(
            starlightGlow: 0f, airglowGlow: 0f, moonlightGlow: 0f,
            maxMoonlightGlow: MaxMoonlight, inVacuum: true);

        Assert.That(floorsOff, Is.EqualTo(VacuumRadianceMath.PlanetshineFloorGlow(MaxMoonlight))
            .Within(Tolerance));
        Assert.That(NightRadianceMath.OverlayBrightnessFactor(floorsOff, 0f),
            Is.LessThan(0.01f), "still renders as black");
    }

    // --- The shared floor, both atmospheres, one sweep ---

    [Test]
    public void NightFloorGlow_PairsSeaLevelAndVacuum()
    {
        // Vacuum.cs's convention #3: pin the vacuum value AND its sea-level counterpart together, so
        // a regression in either shows up as a diverging pair rather than one number quietly matching
        // a stale expectation.
        Assert.That(SurfaceFloor(0f), Is.EqualTo(0.04f).Within(Tolerance));
        Assert.That(VacuumFloor(0f), Is.EqualTo(0.0317f).Within(0.0005f));

        // Full moon: the pair INVERTS, and that inversion is the headline of the vacuum night model.
        // A new-moon orbital night is DARKER than its sea-level counterpart (airglow is gone), while a
        // full-moon orbital night is BRIGHTER (nothing is dimming the moon any more, §18c). Orbit
        // therefore has strictly more dynamic range between its darkest and brightest nights than the
        // ground does — 7.1x against 4.75x — which §18c's umbra inherits directly.
        Assert.That(SurfaceFloor(MaxMoonlight), Is.EqualTo(0.19f).Within(Tolerance));
        Assert.That(VacuumFloor(MaxMoonlight), Is.EqualTo(0.2258f).Within(0.0005f));

        Assert.That(VacuumFloor(0f), Is.LessThan(SurfaceFloor(0f)), "darkest orbital night is darkest");
        Assert.That(VacuumFloor(MaxMoonlight), Is.GreaterThan(SurfaceFloor(MaxMoonlight)),
            "brightest orbital night is brightest");
    }

    [Test]
    public void NightFloorGlow_ClampsToOne()
    {
        Assert.That(NightRadianceMath.NightFloorGlow(0.9f, 0f, 0.9f, MaxMoonlight, inVacuum: true),
            Is.EqualTo(1f).Within(Tolerance));
    }
}
