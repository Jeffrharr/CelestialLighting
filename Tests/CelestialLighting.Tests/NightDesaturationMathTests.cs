namespace CelestialLighting.Tests;

// Offline coverage for §9's per-cell wash (Source/NightDesaturationMath.cs), linked into this project
// via <Compile Include> so these exercise the exact code that ships.
//
// The contract these pin is the one the two previous attempts broke in opposite directions: the
// campfire (and every lamp-lit cell) must come out at its standard colour, and the unlit ground must
// not. Both halves are asserted, because each earlier version satisfied exactly one of them.
[TestFixture]
public class NightDesaturationMathTests
{
    private const float Tolerance = 1e-5f;

    // --- CellWash: the "campfire keeps its colour" half ---

    // A campfire's own cell, and any lamp-lit cell, reads at or above GlowGrid's 0.5 artificial-light
    // cap. Those must take NO wash at all — not "a little", which is what the global camera
    // saturation did and what the whole rewrite is for.
    [TestCase(NightDesaturationMath.LitExemptGlow)]
    [TestCase(0.6f)]
    [TestCase(1.0f)]   // a sun lamp, which GroundGlowAt short-circuits to exactly 1
    public void CellWash_IsZeroAtOrAboveTheLitExemption(float localGlow)
    {
        Assert.That(NightDesaturationMath.CellWash(localGlow), Is.EqualTo(0f).Within(Tolerance));
    }

    // The exemption is anchored on RimWorld's own "fully lit" value, not on taste. If PurkinjeMath's
    // onset ever moves, this must move with it rather than silently drifting apart — the sky ramp and
    // the cell exemption are the same threshold seen from two sides.
    [Test]
    public void CellWash_ExemptionMatchesPurkinjeOnset()
    {
        Assert.That(NightDesaturationMath.LitExemptGlow, Is.EqualTo(PurkinjeMath.OnsetGlow).Within(Tolerance));
    }

    // --- CellWash: the "unlit ground desaturates" half ---

    [TestCase(0f)]
    [TestCase(-0.1f)]  // nothing produces a negative glow, but the pure core takes what it is given
    public void CellWash_IsFullOnAnUnlitCell(float localGlow)
    {
        Assert.That(NightDesaturationMath.CellWash(localGlow), Is.EqualTo(1f).Within(Tolerance));
    }

    // Linear between the two, deliberately: the eye reads this as the gradient around a light source,
    // and an eased curve would draw a visible ring where its knee falls.
    [TestCase(0.125f, ExpectedResult = 0.75f)]
    [TestCase(0.25f, ExpectedResult = 0.5f)]
    [TestCase(0.375f, ExpectedResult = 0.25f)]
    public float CellWash_RampsLinearlyBetween(float localGlow) =>
        NightDesaturationMath.CellWash(localGlow);

    [Test]
    public void CellWash_NeverLeavesUnitRangeAndOnlyFalls()
    {
        float previous = 1f + Tolerance;
        for (int i = 0; i <= 20; i++)
        {
            float glow = i * 0.05f;
            float wash = NightDesaturationMath.CellWash(glow);

            Assert.That(wash, Is.InRange(0f, 1f));
            Assert.That(wash, Is.LessThanOrEqualTo(previous + Tolerance),
                $"wash rose while the cell got brighter at glow={glow}");
            previous = wash;
        }
    }

    // --- MapWash: the map-wide strength written into the material's alpha ---

    // Daylight is the case where §9 must be invisible: the factor is 0 above PurkinjeMath.OnsetGlow,
    // so the wash has to vanish entirely rather than leave a faint sheet over a lit map.
    [Test]
    public void MapWash_IsZeroWhenTheSkyIsNotInRodVision()
    {
        Assert.That(NightDesaturationMath.MapWash(0f, 1f), Is.EqualTo(0f).Within(Tolerance));
    }

    // The slider at zero must be a complete no-op, indistinguishable from the feature being off —
    // the same contract every other knob in this mod holds.
    [Test]
    public void MapWash_IsZeroWithTheSliderDown()
    {
        Assert.That(NightDesaturationMath.MapWash(1f, 0f), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void MapWash_PeaksAtMaxWash()
    {
        Assert.That(NightDesaturationMath.MapWash(1f, 1f),
            Is.EqualTo(NightDesaturationMath.MaxWash).Within(Tolerance));
    }

    // Alpha compositing lifts whatever it blends toward, so a bright wash grey would raise black
    // night ground into a flat haze and undo §7a's pitch-black nights. Pin it dark.
    [Test]
    public void WashGrey_StaysDarkEnoughNotToLiftBlackNights()
    {
        Assert.That(NightDesaturationMath.WashGrey, Is.InRange(0f, 0.2f));
    }

    // Out-of-range inputs clamp rather than extrapolate: a hand-edited settings file must not be able
    // to drive the material alpha past 1 (which Unity would clamp anyway, silently) or negative.
    [TestCase(2f, 2f)]
    [TestCase(-1f, 1f)]
    [TestCase(1f, -1f)]
    public void MapWash_ClampsBothInputs(float factor, float strength)
    {
        Assert.That(NightDesaturationMath.MapWash(factor, strength),
            Is.InRange(0f, NightDesaturationMath.MaxWash));
    }

    // --- WashAlpha: the byte a mesh vertex actually carries ---

    // Moved here out of the layer so NightWashWindowTests can compare the memoised vertex loop against
    // the pre-memoisation one on the shipped conversion. It replaced UnityEngine.Mathf.RoundToInt, so
    // what these pin is that it still rounds the way Mathf did — banker's rounding, ties to even —
    // rather than the away-from-zero form used elsewhere in the mod. A silent switch would move one bit
    // per vertex on every exact midpoint, which no visual test would ever catch.
    [TestCase(0f, ExpectedResult = (byte)0)]
    [TestCase(1f, ExpectedResult = (byte)255)]
    [TestCase(0.5f, ExpectedResult = (byte)128)]        // 127.5 -> ties to even -> 128
    [TestCase(0.25f, ExpectedResult = (byte)64)]        // 63.75 -> 64
    [TestCase(0.1f, ExpectedResult = (byte)26)]         // 25.5 -> 26 either way
    // The discriminating case: 0.3f * 255f is exactly 76.5, and ties-to-even gives 76 where the
    // away-from-zero rounding CoverAlpha uses would give 77. This is the one that would move if
    // someone "tidied" the two conversions into one shape.
    [TestCase(0.3f, ExpectedResult = (byte)76)]
    public byte WashAlpha_RoundsTheWayMathfDid(float wash) => NightDesaturationMath.WashAlpha(wash);

    // Defensive only — CellWash returns [0, 1] and averaging clamped values cannot leave it — but a
    // byte that wrapped instead of clamping would turn a fully-washed cell transparent.
    [TestCase(1.5f, ExpectedResult = (byte)255)]
    [TestCase(-0.5f, ExpectedResult = (byte)0)]
    public byte WashAlpha_ClampsRatherThanWrapping(float wash) => NightDesaturationMath.WashAlpha(wash);
}
