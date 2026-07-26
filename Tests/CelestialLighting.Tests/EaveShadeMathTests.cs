namespace CelestialLighting.Tests;

// Offline unit tests for §15b's pure core (Source/EaveShadeMath.cs, linked into this project so these
// run against the exact shipped file).
//
// Most of these assert the COMPOSITE an eave cell ends up rendering at rather than the alpha in
// isolation: an alpha can look plausible and still compose wrongly, and every claim this subsystem
// makes — "as dark as the shadow beside it", "never anywhere near black" — is a claim about the
// product, not the factor.
//
// The reference numbers come from a live A/B (15:00, latitude 45, clear sky, 9x9 roof slab on
// concrete), quoted against open sunlit ground at 1.000. See EaveShadeMath's header.
[TestFixture]
public class EaveShadeMathTests
{
    // Vanilla's Clear-day shadow tint (0.718, 0.745, 0.757), the darkest any vanilla weather declares.
    private const float ClearDayShadowKeep = 0.7402f;

    private const float Cover = EaveShadeMath.VanillaRoofedSkyKeep;

    // Pins the cover model against the renderer: vanilla clamps a roofed cell's sky cover to 100/255,
    // and the live A/B measured the ground under a roof slab at 0.605 of open sunlit ground. If
    // Ludeon ever moves RoofedAreaMinSkyCover this constant follows it (ApiCompatibilityTests pins
    // the vanilla field itself) and this test says what the number is supposed to mean.
    [Test]
    public void RoofedSkyKeepMatchesTheMeasuredRoofCover()
    {
        Assert.That(Cover, Is.EqualTo(0.6078f).Within(0.001f));
    }

    // The rule, stated as the thing it is: an eave takes the same multiply as the ground beside it.
    [TestCase(0.742f)]
    [TestCase(0.92f)]
    [TestCase(1f)]
    public void ShadeAppliesExactlyTheCastShadowsMultiply(float shadowKeep)
    {
        Assert.That(EaveShadeMath.EaveBrightness(1f, shadowKeep), Is.EqualTo(shadowKeep).Within(0.0005f));
    }

    // The reported artifact, as a test. Before: 0.605 under the roof against a 0.581 rim right at the
    // roofline — lighter than its own shadow exactly where the two touch. After: darker than both,
    // so brightness rises monotonically outward from the porch instead of dipping at its edge.
    [Test]
    public void MeasuredArtifactIsInverted()
    {
        const float rim = 0.581f;
        float before = Cover;
        float after = EaveShadeMath.EaveBrightness(Cover, ClearDayShadowKeep);

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.GreaterThan(rim), "the artifact being fixed: porch lighter than its rim");
            Assert.That(after, Is.LessThan(rim), "after: the porch is the darkest region, not the rim");
            Assert.That(after, Is.LessThan(ClearDayShadowKeep), "and darker than the shadow on open ground");
        });
    }

    // The guard that matters most, because "porch went pitch black at noon" is the failure this
    // subsystem's sibling (issue #33) already caused once. The multiply is bounded below by vanilla's
    // own shadow palette — the darkest tint any vanilla weather declares is Clear's 0.740 — so the
    // deepest an eave can ever go is ~0.45 of open sunlit ground, in full midday sun.
    [Test]
    public void DeepestPossibleEaveIsNowhereNearBlack()
    {
        float deepest = EaveShadeMath.EaveBrightness(Cover, ClearDayShadowKeep);

        Assert.Multiple(() =>
        {
            Assert.That(deepest, Is.EqualTo(0.45f).Within(0.01f));
            Assert.That(deepest, Is.GreaterThan(0.4f), "an eave must stay plainly readable in full sun");
        });
    }

    // The self-limiting half: as the sun drops the shadow tint lerps back toward white, so this
    // contributes nothing at dusk, under heavy overcast (vanilla's other tints are 0.92 and lighter),
    // in a shadowless biome, or at night — leaving exactly the roof cover players already see.
    [TestCase(1f)]
    public void NoShadowMeansNoChangeAtAll(float shadowKeep)
    {
        Assert.That(EaveShadeMath.ShadeAlpha(shadowKeep), Is.EqualTo(0f));
        Assert.That(EaveShadeMath.EaveBrightness(Cover, shadowKeep), Is.EqualTo(Cover).Within(0.0005f));
    }

    // Vanilla's non-Clear weather tints are all 0.92 or lighter, so their eaves barely move — the
    // effect is a fair-weather one, exactly as a hard-edged shadow should be.
    [Test]
    public void OvercastWeatherTintsBarelyDarkenAnEave()
    {
        float overcast = EaveShadeMath.EaveBrightness(Cover, 0.92f);
        Assert.That(Cover - overcast, Is.LessThan(0.06f));
    }

    // Never lighter than the shadow on open ground, at any depth — the inequality the whole
    // subsystem exists to establish, swept rather than spot-checked.
    [TestCase(0f)]
    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(0.742f)]
    [TestCase(0.9f)]
    [TestCase(1f)]
    public void EaveIsNeverLighterThanTheShadowItCasts(float shadowKeep)
    {
        Assert.That(EaveShadeMath.EaveBrightness(Cover, shadowKeep),
            Is.LessThanOrEqualTo(shadowKeep + 0.0005f));
    }

    // Deeper shadow, more shade — monotonic, so a sunrise sweep cannot wobble.
    [Test]
    public void ShadeDeepensMonotonicallyAsTheShadowDoes()
    {
        float previous = -1f;
        for (int i = 0; i <= 20; i++)
        {
            float alpha = EaveShadeMath.ShadeAlpha(1f - i / 20f);
            Assert.That(alpha, Is.GreaterThanOrEqualTo(previous), $"non-monotonic at step {i}");
            previous = alpha;
        }
    }

    // Out-of-range input is clamped rather than trusted: the tint arrives from a live Material that
    // any other mod may have written to, and an alpha outside [0,1] would be a visibly broken frame
    // rather than a slightly wrong one.
    [TestCase(-1f, 1f)]
    [TestCase(2f, 0f)]
    public void OutOfRangeShadowKeepIsClamped(float shadowKeep, float expectedAlpha)
    {
        Assert.That(EaveShadeMath.ShadeAlpha(shadowKeep), Is.EqualTo(expectedAlpha).Within(0.0005f));
    }

    // Luminance: Rec. 709 weights, and a neutral grey must come back as itself so an achromatic tint
    // passes through undistorted.
    [TestCase(0f)]
    [TestCase(0.5f)]
    [TestCase(1f)]
    public void NeutralGreyShadowTintReadsAsItsOwnValue(float grey)
    {
        Assert.That(EaveShadeMath.ShadowKeep(grey, grey, grey), Is.EqualTo(grey).Within(0.0005f));
    }

    // The end-to-end pin, and the one that would catch a wrong weighting: fed vanilla's own Clear-day
    // shadow tint, this must return the depth the live A/B actually measured the cast band at. The
    // channels are near-neutral, so a mis-ordered weight vector would still land close — hence the
    // tight tolerance rather than a loose sanity check.
    [Test]
    public void VanillaClearDayTintReadsAsTheMeasuredShadowDepth()
    {
        Assert.That(EaveShadeMath.ShadowKeep(0.718f, 0.745f, 0.757f), Is.EqualTo(0.742f).Within(0.003f),
            "the live A/B measured the cast band at 0.742 of open sunlit ground");
    }
}
