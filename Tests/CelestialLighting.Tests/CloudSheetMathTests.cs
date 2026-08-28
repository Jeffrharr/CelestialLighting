namespace CelestialLighting.Tests;

// Offline coverage for §25's drawn cloud sheet (Source/CloudSheetMath.cs, issue #138), linked into
// this project via <Compile Include> so these exercise the exact code that ships.
[TestFixture]
public class CloudSheetMathTests
{
    private const float Tolerance = 1e-4f;

    // THE DIFFERENCE FROM THE OTHER TWO LANES, pinned rather than only commented. §23b and §23c draw
    // residuals and go silent at full cover because a uniform sky is what a flat colour already
    // describes. §25 draws the cloud ITSELF, so full cover is its strongest case — an overcast sky
    // should look covered. A later "tidy-up" that made all three consistent would silently delete the
    // whole subsystem's reason for existing.
    [Test]
    public void TheSheetKeepsDrawingAtFullCoverUnlikeTheIlluminationLanes()
    {
        Assert.That(CloudSheetMath.SheetAlpha(1f, inVacuum: false),
            Is.EqualTo(CloudSheetMath.SheetAmplitude).Within(Tolerance));
    }

    // COVERAGE IS A COUNT, NOT AN OPACITY — CloudSheetLayout.SheetCount owns it, so a sheet's own
    // alpha must not scale with it as well. It did in the tiled version, where one stretched field had
    // to express "how cloudy" as opacity because it covered the map either way; carrying that over to
    // bounded sheets counted coverage twice and rendered a 0.35-covered noon sky at median ΔE 0.00.
    // This is the regression pin for that fix.
    [Test]
    public void ASheetsOwnOpacityDoesNotScaleWithHowCloudyItIs()
    {
        float thin = CloudSheetMath.SheetAlpha(0.15f, inVacuum: false);
        float half = CloudSheetMath.SheetAlpha(0.5f, inVacuum: false);
        float full = CloudSheetMath.SheetAlpha(1f, inVacuum: false);

        Assert.That(thin, Is.EqualTo(half).Within(Tolerance));
        Assert.That(half, Is.EqualTo(full).Within(Tolerance));

        // The fraction survives as a gate and nowhere else.
        Assert.That(CloudSheetMath.SheetAlpha(0f, inVacuum: false), Is.EqualTo(0f));
    }

    [Test]
    public void NoSheetWithoutCloud()
    {
        Assert.That(CloudSheetMath.SheetAlpha(0f, inVacuum: false), Is.EqualTo(0f));
    }

    [Test]
    public void SheetIsZeroInVacuum()
    {
        Assert.That(CloudSheetMath.SheetAlpha(0.5f, inVacuum: true), Is.EqualTo(0f));
    }

    // Night cloud stays visible but goes both darker AND sheerer. A dark sheet drawn at full alpha
    // over a night map would black the colony out wholesale, which is a lighting change rather than a
    // cloud — see DeckIllumination's own note. Stated on the illumination term rather than on the
    // alpha, because §25b moved it there: it is a per-SHEET quantity now, since two decks over one
    // map are not equally lit.
    [Test]
    public void NightCloudIsDarkerAndSheererButNotAbsent()
    {
        float day = CloudSheetMath.DeckIllumination(1f, 0f);
        float night = CloudSheetMath.DeckIllumination(0f, 0f);

        Assert.That(night, Is.GreaterThan(0f), "cloud does not stop existing at night");
        Assert.That(night, Is.LessThan(day * 0.2f));
        Assert.That(CloudSheetMath.SheetBrightness(0f),
            Is.EqualTo(CloudSheetMath.NightBrightness).Within(Tolerance));
        Assert.That(CloudSheetMath.SheetBrightness(1f), Is.EqualTo(1f).Within(Tolerance));
    }

    // Keyed on sky glow rather than on solar elevation, so anything else that darkens the world —
    // an eclipse above all — darkens the clouds with it. A geometric key would leave the deck brightly
    // lit through a total eclipse.
    [Test]
    public void BrightnessTracksSkyGlowMonotonically()
    {
        float previous = -1f;
        for (float glow = 0f; glow <= 1f; glow += 0.1f)
        {
            float brightness = CloudSheetMath.SheetBrightness(glow);
            Assert.That(brightness, Is.GreaterThan(previous));
            previous = brightness;
        }
    }

    // THE REGRESSION PIN FOR A MEASURED FAILURE. §25 scaled the sheet by how bright the GROUND is, so
    // the moment the sun crossed the horizon the deck went dark and sheer — at exactly the elevations
    // §25b's sunset windows live in. The four-frame sunset A/B came out at median ΔE 0.00 with under
    // 1% of pixels changed, while the probes proved the colour arithmetic was running correctly: the
    // right answer computed onto an invisible surface.
    //
    // A deck in direct sun is the BRIGHTEST thing in a sunset sky, which is the entire reason anybody
    // looks at one. If this ever goes back to tracking sky glow alone, the subsystem silently stops
    // being visible rather than stops being correct, which is the harder failure to notice.
    [Test]
    public void AnUnderlitDeckIsBrighterThanTheGroundBeneathIt()
    {
        // Sun below the horizon: glow has collapsed, but this deck is still catching it.
        const float TwilightGlow = 0.05f;

        float shadowed = CloudSheetMath.DeckIllumination(TwilightGlow, 0f);
        float lit = CloudSheetMath.DeckIllumination(TwilightGlow, 1f);

        Assert.That(lit, Is.GreaterThan(shadowed * 3f),
            "a deck in direct sun must not fade with the ground it is lighting");
        Assert.That(lit, Is.EqualTo(CloudSheetMath.UnderlitDeckFloor).Within(Tolerance));
    }

    // And the property that concession was bought with: through a DAYLIGHT eclipse — the case that
    // matters — the underlit term is zero, so an eclipse still darkens the clouds exactly as it did
    // before. The floor cannot reach a high sun because UnderlitFraction cannot.
    [Test]
    public void ADaylightEclipseStillDarkensTheClouds()
    {
        float highSunUnderlit = CloudSheetMath.UnderlitFraction(
            30f, CloudDeckMath.ShadowEntryDegrees(CloudDeckMath.HighDeck));
        Assert.That(highSunUnderlit, Is.EqualTo(0f));

        float clear = CloudSheetMath.DeckIllumination(1f, highSunUnderlit);
        float eclipsed = CloudSheetMath.DeckIllumination(0.05f, highSunUnderlit);

        Assert.That(eclipsed, Is.LessThan(clear * 0.25f));
        Assert.That(eclipsed, Is.EqualTo(CloudSheetMath.SheetBrightness(0.05f)).Within(Tolerance));
    }

    // Never dimmer than the world around it either: the floor is a MAX, so full daylight still wins
    // over a deck that happens to be underlit at a low sun.
    [Test]
    public void IlluminationNeverFallsBelowTheAmbient()
    {
        for (float glow = 0f; glow <= 1f; glow += 0.1f)
        {
            for (float underlit = 0f; underlit <= 1f; underlit += 0.25f)
            {
                Assert.That(
                    CloudSheetMath.DeckIllumination(glow, underlit),
                    Is.GreaterThanOrEqualTo(CloudSheetMath.SheetBrightness(glow) - Tolerance),
                    $"glow {glow}, underlit {underlit}");
            }
        }
    }

    // The handover from "lit from above" to "lit from beneath" has to be gradual, or the deck
    // recolours in one step at the moment of sunset — the most visible instant it could pick.
    [Test]
    public void TheUnderlitHandoverIsSmoothAndCoversSunset()
    {
        float entry = CloudDeckMath.ShadowEntryDegrees(CloudDeckMath.MidDeck);

        Assert.That(CloudSheetMath.UnderlitFraction(30f, entry), Is.EqualTo(0f));
        Assert.That(CloudSheetMath.UnderlitFraction(CloudSheetMath.UnderlitNoneDegrees, entry),
            Is.EqualTo(0f));

        // Fully underlit at the horizon and stays so until the deck's own shadow entry — the geometry
        // is not subtle, the sun is either above the plane the deck sits in or below it.
        Assert.That(CloudSheetMath.UnderlitFraction(0f, entry), Is.EqualTo(1f).Within(Tolerance));
        Assert.That(CloudSheetMath.UnderlitFraction(-entry, entry), Is.EqualTo(1f).Within(Tolerance));

        // And out again once the deck is in Earth's shadow: past its entry plus the fade, nothing is
        // lighting it from below any more.
        Assert.That(
            CloudSheetMath.UnderlitFraction(-entry - CloudSheetMath.ShadowFadeDegrees, entry),
            Is.EqualTo(0f));
        Assert.That(CloudSheetMath.UnderlitFraction(-10f, entry), Is.EqualTo(0f));

        // Monotone up to the peak: the sky must not brighten again on the way down.
        float previous = 1.1f;
        for (float elevation = 0f; elevation <= CloudSheetMath.UnderlitNoneDegrees; elevation += 0.25f)
        {
            float underlit = CloudSheetMath.UnderlitFraction(elevation, entry);
            Assert.That(underlit, Is.LessThanOrEqualTo(previous + Tolerance));
            previous = underlit;
        }
    }

    // THE HEADLINE CLAIM OF §25b, pinned rather than only described. A sunset is not "the clouds turn
    // orange", it is "the clouds turn orange and then go out from the bottom up". At a depression
    // between the low deck's shadow entry and the high deck's, the low cloud must be finished while
    // the cirrus above it is still fully lit — and if that ordering ever inverts or collapses, the
    // subsystem has stopped doing the one thing it was built for.
    [Test]
    public void TheDecksGoOutFromTheBottomUp()
    {
        float low = CloudDeckMath.ShadowEntryDegrees(CloudDeckMath.LowDeck);
        float mid = CloudDeckMath.ShadowEntryDegrees(CloudDeckMath.MidDeck);
        float high = CloudDeckMath.ShadowEntryDegrees(CloudDeckMath.HighDeck);

        Assert.That(low, Is.LessThan(mid), "a lower deck must lose the sun first");
        Assert.That(mid, Is.LessThan(high));

        // 2.5 degrees down: past the low deck's entry and its fade, inside the high deck's window.
        const float Elevation = -2.5f;
        Assert.That(CloudSheetMath.UnderlitFraction(Elevation, low), Is.EqualTo(0f),
            "the low deck should be a grey mass by now");
        Assert.That(CloudSheetMath.UnderlitFraction(Elevation, high), Is.EqualTo(1f).Within(Tolerance),
            "cirrus should still be burning");
        Assert.That(CloudSheetMath.UnderlitFraction(Elevation, mid),
            Is.GreaterThan(0f).And.LessThan(1f), "the mid deck should be part-way out");
    }

    // A deck sitting on the ground loses the sun exactly when the ground does. It must still fade
    // rather than snap: a zero-width window fed to an inverse lerp is where the NaNs live, and a hard
    // switch at the horizon is the most visible instant available.
    [Test]
    public void AGroundHuggingDeckFadesRatherThanSnapping()
    {
        Assert.That(CloudSheetMath.UnderlitFraction(0f, 0f), Is.EqualTo(1f).Within(Tolerance));
        Assert.That(CloudSheetMath.UnderlitFraction(-0.5f, 0f),
            Is.GreaterThan(0f).And.LessThan(1f));
        Assert.That(CloudSheetMath.UnderlitFraction(-CloudSheetMath.ShadowFadeDegrees, 0f),
            Is.EqualTo(0f));

        // A negative entry is not reachable from the deck table, but it rides in from a caller and a
        // render path is the wrong place to produce a NaN.
        Assert.That(CloudSheetMath.UnderlitFraction(-0.5f, -3f), Is.GreaterThanOrEqualTo(0f));
        Assert.That(CloudSheetMath.UnderlitFraction(float.NaN, 2f), Is.EqualTo(0f));
    }

    [Test]
    public void SheetScalesLinearlyWithAmplitudeAndIgnoresNonsense()
    {
        float half = CloudSheetMath.SheetAlphaWithAmplitude(0.5f, 0.1f, false);
        float whole = CloudSheetMath.SheetAlphaWithAmplitude(0.5f, 0.2f, false);

        Assert.That(whole, Is.EqualTo(half * 2f).Within(Tolerance));
        Assert.That(CloudSheetMath.SheetAlphaWithAmplitude(0.5f, 0f, false), Is.EqualTo(0f));
        Assert.That(CloudSheetMath.SheetAlphaWithAmplitude(float.NaN, 0.2f, false), Is.EqualTo(0f));
    }

    // The player's cloud-opacity slider. The properties worth holding are the ones the settings
    // screen's tooltip promises: the top of the range is the shipped look exactly, the bottom is a
    // true no-op, and everything between is proportional rather than stepped.
    [Test]
    public void OpacityScalesAmplitudeProportionally()
    {
        float shipped = CloudSheetMath.AmplitudeAtOpacity(
            CloudSheetMath.PresentSheetAmplitude, CloudSheetMath.DefaultOpacityScale);

        // EQUAL, not approximately equal, and that is the point of the assertion rather than
        // precision fussiness: an untouched slider has to be bit-identical to the build before the
        // slider existed, or every measured pin in every cloud scenario silently moves.
        Assert.That(shipped, Is.EqualTo(CloudSheetMath.PresentSheetAmplitude));

        float half = CloudSheetMath.AmplitudeAtOpacity(CloudSheetMath.PresentSheetAmplitude, 0.5f);
        Assert.That(half, Is.EqualTo(CloudSheetMath.PresentSheetAmplitude * 0.5f).Within(Tolerance));

        // Zero has to reach the overlay as a zero ALPHA, not merely as a small one: the draw call is
        // skipped on `alpha <= 0`, which is what makes the bottom of the slider cost nothing.
        float alpha = CloudSheetMath.SheetAlphaWithAmplitude(
            0.5f,
            CloudSheetMath.AmplitudeAtOpacity(CloudSheetMath.PresentSheetAmplitude, 0f),
            inVacuum: false);
        Assert.That(alpha, Is.EqualTo(0f));
    }

    // Out-of-range values arrive from a settings file a player (or another mod) can edit by hand, and
    // the two ends are deliberately NOT symmetrical. Above 1 clamps, because the amplitude above it
    // clamps the alpha to an opaque lid over the colony. NaN returns the shipped amplitude rather
    // than zero, so a corrupt config gives back the normal sky instead of an invisible one.
    [TestCase(-1f, 0f)]
    [TestCase(0f, 0f)]
    [TestCase(1f, CloudSheetMath.PresentSheetAmplitude)]
    [TestCase(4f, CloudSheetMath.PresentSheetAmplitude)]
    public void OpacityClampsToTheShippedRange(float scale, float expected)
    {
        Assert.That(
            CloudSheetMath.AmplitudeAtOpacity(CloudSheetMath.PresentSheetAmplitude, scale),
            Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void NonsenseOpacityKeepsTheShippedLook()
    {
        Assert.That(
            CloudSheetMath.AmplitudeAtOpacity(CloudSheetMath.PresentSheetAmplitude, float.NaN),
            Is.EqualTo(CloudSheetMath.PresentSheetAmplitude));
    }

    // A NaN sky glow falls back to the night floor rather than propagating — the cloud is still
    // there, it is simply as dark as this model ever draws one. Moved here with the illumination
    // term when §25b made it per-sheet; the property is the same one.
    [Test]
    public void NonsenseLightingFallsBackToTheNightFloor()
    {
        Assert.That(CloudSheetMath.DeckIllumination(float.NaN, 0f),
            Is.EqualTo(CloudSheetMath.NightBrightness).Within(Tolerance));
        Assert.That(CloudSheetMath.DeckIllumination(0.5f, float.NaN),
            Is.EqualTo(CloudSheetMath.SheetBrightness(0.5f)).Within(Tolerance));
    }

    // ---------------------------------------------------------------------------------------------
    // §25d: the sunlit deck keeps its own opacity and its full brightness (issue #144)
    // ---------------------------------------------------------------------------------------------

    // The two-argument form must keep returning exactly what it returned before the ceiling was
    // exposed, or "§25d off" stops being §25b and every A/B built on that flag is comparing two
    // changed things.
    [TestCase(0f, 0f)]
    [TestCase(0.3f, 0.5f)]
    [TestCase(1f, 1f)]
    [TestCase(0.05f, 0.9f)]
    public void DeckIllumination_two_arg_form_is_the_shipped_ceiling(float glow, float underlit)
    {
        Assert.That(
            CloudSheetMath.DeckIllumination(glow, underlit),
            Is.EqualTo(CloudSheetMath.DeckIllumination(glow, underlit, CloudSheetMath.UnderlitDeckFloor))
                .Within(1e-6f));
    }

    // A deck in full sun is brighter under §25d than under §25b, and by exactly the ratio of the two
    // ceilings. Stated as a ratio rather than an absolute so it survives a recalibration of either.
    [Test]
    public void A_sunlit_deck_is_brighter_under_the_raised_ceiling()
    {
        const float DarkGround = 0f;

        float shipped = CloudSheetMath.DeckIllumination(
            DarkGround, 1f, CloudSheetMath.UnderlitDeckFloor);
        float raised = CloudSheetMath.DeckIllumination(
            DarkGround, 1f, CloudSheetMath.SunlitDeckCeiling);

        Assert.Multiple(() =>
        {
            Assert.That(raised, Is.GreaterThan(shipped));
            Assert.That(raised / shipped,
                Is.EqualTo(CloudSheetMath.SunlitDeckCeiling / CloudSheetMath.UnderlitDeckFloor)
                    .Within(1e-5f));
        });
    }

    // The ceiling must not reach a deck that has no direct light: raising how bright a SUNLIT cloud
    // is has no business changing an overcast noon, which is almost every frame of almost every save.
    [TestCase(0.2f)]
    [TestCase(0.6f)]
    [TestCase(1f)]
    public void The_raised_ceiling_does_nothing_to_a_deck_with_no_direct_light(float glow)
    {
        Assert.That(
            CloudSheetMath.DeckIllumination(glow, 0f, CloudSheetMath.SunlitDeckCeiling),
            Is.EqualTo(CloudSheetMath.DeckIllumination(glow, 0f)).Within(1e-6f));
    }

    // Opacity takes the underlit fraction RAW. The 0.55 floor is a brightness calibration and has no
    // business deciding how much water is in the way, so a fully sunlit deck is fully opaque whatever
    // that constant is set to.
    [Test]
    public void A_sunlit_deck_keeps_its_own_opacity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CloudSheetMath.DeckOpacity(0f, 1f), Is.EqualTo(1f).Within(1e-6f));
            Assert.That(CloudSheetMath.DeckOpacity(0f, 1f),
                Is.GreaterThan(CloudSheetMath.DeckIllumination(0f, 1f)));
        });
    }

    // ...and an UNLIT one does not. A cloud nobody can see should not sit on the map at full
    // strength, which is the half of the shipped coupling that was right and is kept.
    [TestCase(0f)]
    [TestCase(0.25f)]
    [TestCase(1f)]
    public void An_unlit_deck_still_fades_with_the_sky(float glow)
    {
        Assert.That(
            CloudSheetMath.DeckOpacity(glow, 0f),
            Is.EqualTo(CloudSheetMath.SheetBrightness(glow)).Within(1e-6f));
    }

    // Opacity is never below illumination, at any combination of the two inputs — so §25d can only
    // ever make cloud more present, never less. Pinned because "off must reproduce the old
    // behaviour" is checked above, and this is the matching guarantee for "on".
    [TestCase(0f, 0f)]
    [TestCase(0f, 0.5f)]
    [TestCase(0.4f, 0.2f)]
    [TestCase(0.9f, 1f)]
    [TestCase(0.1f, 0.75f)]
    public void Decoupled_opacity_never_makes_a_deck_sheerer(float glow, float underlit)
    {
        Assert.That(
            CloudSheetMath.DeckOpacity(glow, underlit),
            Is.GreaterThanOrEqualTo(CloudSheetMath.DeckIllumination(glow, underlit) - 1e-6f));
    }
}
