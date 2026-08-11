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
        Assert.That(CloudSheetMath.SheetAlpha(1f, 1f, inVacuum: false),
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
        float thin = CloudSheetMath.SheetAlpha(0.15f, 1f, inVacuum: false);
        float half = CloudSheetMath.SheetAlpha(0.5f, 1f, inVacuum: false);
        float full = CloudSheetMath.SheetAlpha(1f, 1f, inVacuum: false);

        Assert.That(thin, Is.EqualTo(half).Within(Tolerance));
        Assert.That(half, Is.EqualTo(full).Within(Tolerance));

        // The fraction survives as a gate and nowhere else.
        Assert.That(CloudSheetMath.SheetAlpha(0f, 1f, inVacuum: false), Is.EqualTo(0f));
    }

    [Test]
    public void NoSheetWithoutCloud()
    {
        Assert.That(CloudSheetMath.SheetAlpha(0f, 1f, inVacuum: false), Is.EqualTo(0f));
    }

    [Test]
    public void SheetIsZeroInVacuum()
    {
        Assert.That(CloudSheetMath.SheetAlpha(0.5f, 1f, inVacuum: true), Is.EqualTo(0f));
    }

    // Night cloud stays visible but goes both darker AND sheerer. A dark sheet drawn at full alpha
    // over a night map would black the colony out wholesale, which is a lighting change rather than a
    // cloud — see SheetAlpha's own note.
    [Test]
    public void NightCloudIsDarkerAndSheererButNotAbsent()
    {
        float day = CloudSheetMath.SheetAlpha(0.5f, 1f, inVacuum: false);
        float night = CloudSheetMath.SheetAlpha(0.5f, 0f, inVacuum: false);

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

    // The handover from "lit from above" to "lit from beneath" has to be gradual, or the deck
    // recolours in one step at the moment of sunset — the most visible instant it could pick.
    [Test]
    public void TheUnderlitHandoverIsSmoothAndCoversSunset()
    {
        Assert.That(CloudSheetMath.UnderlitFraction(30f), Is.EqualTo(0f));
        Assert.That(CloudSheetMath.UnderlitFraction(CloudSheetMath.UnderlitNoneDegrees), Is.EqualTo(0f));
        Assert.That(CloudSheetMath.UnderlitFraction(CloudSheetMath.UnderlitFullDegrees), Is.EqualTo(1f));
        Assert.That(CloudSheetMath.UnderlitFraction(-10f), Is.EqualTo(1f));

        // At the horizon itself the deck is already mostly underlit — the sun is grazing it from
        // beneath well before it is geometrically below the observer.
        float atHorizon = CloudSheetMath.UnderlitFraction(0f);
        Assert.That(atHorizon, Is.GreaterThan(0.5f).And.LessThan(1f));

        float previous = 1.1f;
        for (float elevation = CloudSheetMath.UnderlitFullDegrees;
             elevation <= CloudSheetMath.UnderlitNoneDegrees;
             elevation += 0.25f)
        {
            float underlit = CloudSheetMath.UnderlitFraction(elevation);
            Assert.That(underlit, Is.LessThanOrEqualTo(previous + Tolerance));
            previous = underlit;
        }
    }

    [Test]
    public void SheetScalesLinearlyWithAmplitudeAndIgnoresNonsense()
    {
        float half = CloudSheetMath.SheetAlphaWithAmplitude(0.5f, 1f, 0.1f, false);
        float whole = CloudSheetMath.SheetAlphaWithAmplitude(0.5f, 1f, 0.2f, false);

        Assert.That(whole, Is.EqualTo(half * 2f).Within(Tolerance));
        Assert.That(CloudSheetMath.SheetAlphaWithAmplitude(0.5f, 1f, 0f, false), Is.EqualTo(0f));
        Assert.That(CloudSheetMath.SheetAlphaWithAmplitude(float.NaN, 1f, 0.2f, false), Is.EqualTo(0f));
        // A NaN sky glow falls back to the night floor rather than propagating — the cloud is still
        // there, it is simply as dark as this model ever draws one.
        Assert.That(CloudSheetMath.SheetAlphaWithAmplitude(0.5f, float.NaN, 0.2f, false),
            Is.EqualTo(0.2f * CloudSheetMath.NightBrightness).Within(Tolerance));
    }
}
