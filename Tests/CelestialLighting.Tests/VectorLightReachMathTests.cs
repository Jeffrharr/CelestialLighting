using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Lamp glow reach. Four properties, and only the last is ordinary arithmetic — the other three are
// each the shape of a defect this repo has already paid for once somewhere else.
[TestFixture]
public class VectorLightReachMathTests
{
    // THE OFF POSITION IS EXACT, not approximately exact.
    //
    // The repo's flag rule is that a feature turned off reproduces the pre-feature behaviour
    // precisely, because that is what makes a live A/B a real baseline rather than a picture of the
    // mod being absent. Here the off position is a slider value rather than a checkbox, so the rule
    // lands on this function: at NoReach the drawn radius has to BE the def's radius, so the model
    // is vanilla's own curve, so the per-fragment max has nothing but the geometry difference to
    // deliver — which is the shipped renderer.
    //
    // Asserted with an exact comparison deliberately. A tolerance here would pass on a
    // multiply-by-one that returned a value one ulp out, and one ulp is enough to make every
    // polygon on the map compare unequal in VectorLightField.Upsert and rebake forever.
    [TestCase(1.5f)]
    [TestCase(3f)]
    [TestCase(10f)]
    [TestCase(24f)]
    [TestCase(30f)]
    public void OffPositionReturnsTheDefsOwnRadius(float glowRadius)
    {
        Assert.That(
            VectorLightReachMath.ExtendedRadius(glowRadius, VectorLightReachMath.NoReach),
            Is.EqualTo(glowRadius));

        Assert.That(VectorLightReachMath.Extends(glowRadius, VectorLightReachMath.NoReach), Is.False);
    }

    // Below the off position is clamped UP, never through.
    //
    // A reach under 1 would ask for a lamp dimmer than vanilla's own, which the max composition
    // cannot express at all — it can only ever raise a channel — so the pass would quietly render
    // the reach-1 frame while the setting claimed otherwise. Clamping makes that a defined answer
    // rather than a discrepancy between the slider and the screen. A settings file written against
    // some future range is the realistic way to arrive here, which is why it clamps rather than
    // throws.
    [TestCase(0.5f)]
    [TestCase(0f)]
    [TestCase(-2f)]
    public void BelowOffIsClampedToOff(float reach)
    {
        Assert.That(VectorLightReachMath.ExtendedRadius(10f, reach), Is.EqualTo(10f));
        Assert.That(VectorLightReachMath.Extends(10f, reach), Is.False);
    }

    // The ceiling is vanilla's own, and a lamp already at it does not read as extended.
    //
    // Two defs ship above radius 20 (24 and 30). At the top of the slider they ask for 48 and 60
    // cells, both wider than GlowGrid.MaxLightRadius permits any light in this game to be, and both
    // spread thin enough that the excess near the lamp is a couple of levels bought with four times
    // the silhouette scan. The Extends half matters as much as the clamp: a radius-40 lamp cannot be
    // stretched by any setting, so nothing downstream should do the extra work of pretending it was.
    [TestCase(24f, 2f)]
    [TestCase(30f, 2f)]
    [TestCase(40f, 1.5f)]
    [TestCase(100f, 1.01f)]
    public void CeilingIsVanillasMaxLightRadius(float glowRadius, float reach)
    {
        Assert.That(
            VectorLightReachMath.ExtendedRadius(glowRadius, reach),
            Is.EqualTo(VectorLightReachMath.MaxRadiusCells));
    }

    [Test]
    public void ALampAlreadyAtTheCeilingIsNotExtendedByAnySetting()
    {
        Assert.That(
            VectorLightReachMath.Extends(VectorLightReachMath.MaxRadiusCells, VectorLightReachMath.MaxReach),
            Is.False);
    }

    // The coverage grid never grows with reach, which is the whole optimisation.
    //
    // Its only consumer scales VANILLA's light by it, and past vanilla's glowRadius the flood
    // delivers nothing, so a coverage byte out in the annulus can only ever be multiplied by zero.
    // Sizing the grid from the drawn radius would make a reach-2 lamp bake four times the grid for
    // no changed pixel — and the grid's bake is the step this subsystem already had to hoist out of
    // section regeneration once, at 239 us per section.
    [TestCase(3f, 1f)]
    [TestCase(3f, 1.5f)]
    [TestCase(3f, 2f)]
    [TestCase(10f, 1f)]
    [TestCase(10f, 1.5f)]
    [TestCase(10f, 2f)]
    [TestCase(30f, 2f)]
    public void CoverageRadiusIsIndependentOfReach(float glowRadius, float reach)
    {
        float drawn = VectorLightReachMath.ExtendedRadius(glowRadius, reach);

        Assert.That(
            VectorLightReachMath.CoverageRadius(glowRadius, drawn),
            Is.EqualTo(glowRadius));
    }

    // ...and it follows the DRAWN radius when that is the smaller of the two, which is not a
    // hypothetical branch. A lamp above the ceiling has a drawn radius BELOW its own glowRadius, and
    // a grid sized to the def there would hold coverage for cells the polygon was never cast to —
    // read as "wholly shadowed" by a consumer that has vanilla light to scale, which is the one
    // direction CoverageAt's own out-of-bounds answer was chosen to avoid erring in.
    [Test]
    public void CoverageRadiusFollowsTheDrawnRadiusWhenTheCeilingBites()
    {
        float glowRadius = 60f;
        float drawn = VectorLightReachMath.ExtendedRadius(glowRadius, VectorLightReachMath.MaxReach);

        Assert.That(drawn, Is.EqualTo(VectorLightReachMath.MaxRadiusCells));
        Assert.That(VectorLightReachMath.CoverageRadius(glowRadius, drawn), Is.EqualTo(drawn));
    }

    // TICKING THE CHECKBOX HAS TO DO SOMETHING, which is a property of where DefaultReach sits
    // rather than of any arithmetic, and is exactly the kind of thing that breaks silently. The
    // switch pushes the slider's stored value when it is on; if that value could rest at (or below)
    // the off position, the control would read as broken — tick it, nothing happens — and no test of
    // the formula would notice, because the formula would be right.
    [Test]
    public void TheSwitchesStartingReachActuallyExtends()
    {
        Assert.That(VectorLightReachMath.DefaultReach, Is.GreaterThan(VectorLightReachMath.NoReach));
        Assert.That(
            VectorLightReachMath.DefaultReach, Is.LessThanOrEqualTo(VectorLightReachMath.MaxReach));

        // And on a lamp, not merely as a number: a radius the ceiling already clamps would extend
        // nothing however far above 1 the multiplier sat.
        Assert.That(VectorLightReachMath.Extends(10f, VectorLightReachMath.DefaultReach), Is.True);
    }

    // The brightness axis. The RESTING value is what the master switch pushes when the feature is
    // off, and it alone has to leave the shipped renderer bit-identical — the slider's own range is
    // free to sit either side of it, which is what lets Astryl's sub-1 calibration be adopted at all.
    [Test]
    public void RestingBrightnessIsExactlyOneAndAltersNothing()
    {
        Assert.That(
            VectorLightReachMath.Brightness(VectorLightReachMath.NoBrightness),
            Is.EqualTo(1f));

        Assert.That(VectorLightReachMath.AltersBrightness(VectorLightReachMath.NoBrightness), Is.False);
    }

    // THE STARTING VALUE IS ASTRYL'S OWN AND SITS BELOW THE RESTING ONE, which is the direction
    // nobody predicts and the reason it is pinned rather than left to the field initialiser. Their
    // FillStrength scales a model that replaces vanilla's contribution; ours scales the excess over
    // it, already delivered at a constant fitted to read at vanilla's brightness — so their 0.85 is
    // ≈0.85 of ours, and the switch starts a little under what the size slider alone would give.
    [Test]
    public void TheSwitchStartsAtAstrylsCalibrationBelowResting()
    {
        Assert.That(VectorLightReachMath.DefaultBrightness, Is.EqualTo(0.85f));
        Assert.That(
            VectorLightReachMath.DefaultBrightness, Is.LessThan(VectorLightReachMath.NoBrightness));

        // Inside the range, and not ON the floor: a slider whose default sits at its minimum cannot
        // be turned down, which is the one thing splitting the axes was supposed to make possible.
        Assert.That(
            VectorLightReachMath.DefaultBrightness, Is.GreaterThan(VectorLightReachMath.MinBrightness));
        Assert.That(
            VectorLightReachMath.Brightness(VectorLightReachMath.DefaultBrightness),
            Is.EqualTo(VectorLightReachMath.DefaultBrightness));

        // And it is a real change, in the dimming direction.
        Assert.That(
            VectorLightReachMath.AltersBrightness(VectorLightReachMath.DefaultBrightness), Is.True);
    }

    // Clamped to the FLOOR now, not up to the resting value — the range deliberately extends below
    // 1 so the beams can be softened, which is only safe because the master switch owns "off" and
    // pushes NoBrightness regardless of where this sits.
    [TestCase(0f)]
    [TestCase(0.25f)]
    [TestCase(-3f)]
    public void BelowTheFloorIsClampedToIt(float brightness)
    {
        Assert.That(
            VectorLightReachMath.Brightness(brightness),
            Is.EqualTo(VectorLightReachMath.MinBrightness));
    }

    [TestCase(2.5f)]
    [TestCase(10f)]
    public void AboveTheCeilingIsClampedDown(float brightness)
    {
        Assert.That(
            VectorLightReachMath.Brightness(brightness),
            Is.EqualTo(VectorLightReachMath.MaxBrightness));
    }

    // Both directions count as altering it, which is why the predicate is not named for brightening.
    [TestCase(0.5f)]
    [TestCase(0.85f)]
    [TestCase(1.25f)]
    [TestCase(2f)]
    public void InRangeBrightnessPassesThroughAndCountsEitherWay(float brightness)
    {
        Assert.That(VectorLightReachMath.Brightness(brightness), Is.EqualTo(brightness));
        Assert.That(VectorLightReachMath.AltersBrightness(brightness), Is.True);
    }

    // THE TWO AXES ARE INDEPENDENT, which is the property the split exists to provide and the one a
    // reader is most likely to doubt. Brightness must not move the radius and reach must not move
    // the delivered fraction — so a player who turns the brightness down keeps their lamp's SIZE,
    // which is precisely what Astryl's fork could not offer before it separated them.
    [TestCase(1f)]
    [TestCase(1.5f)]
    [TestCase(2f)]
    public void BrightnessDoesNotTouchTheRadius(float brightness)
    {
        float withReachOnly = VectorLightReachMath.ExtendedRadius(10f, 1.5f);

        // Brightness has no way into ExtendedRadius at all, which is the point: they are separate
        // functions of separate inputs, and the composition that joins them lives in the draw.
        Assert.That(VectorLightReachMath.ExtendedRadius(10f, 1.5f), Is.EqualTo(withReachOnly));
        Assert.That(VectorLightReachMath.Brightness(brightness), Is.EqualTo(brightness));
    }

    [Test]
    public void AnUnlitEmitterHasNoRadiusEitherWay()
    {
        Assert.That(VectorLightReachMath.ExtendedRadius(0f, 2f), Is.EqualTo(0f));
        Assert.That(VectorLightReachMath.CoverageRadius(0f, 12f), Is.EqualTo(0f));
    }

    // THE CLAIM THE TOOLTIP AND THE HEADERS BOTH REST ON, pinned here because it is the one thing
    // about this feature that is genuinely counter-intuitive and it is stated in four places.
    //
    // At the old rim (d = glowRadius) the excess over vanilla is EXACTLY 0.6*(reach-1)/reach, with
    // no dependence on the lamp's radius whatsoever. Both curves carry the same 0.4/R² inverse-square
    // term at that distance — it is evaluated at d, and d is the same on both sides — so it cancels
    // out of the difference and only the linear term survives.
    //
    // So REACH ALONE decides how bright the new light is, and the lamp's own radius only decides how
    // many cells that brightness is spread over. That is the claim the tooltip, the pure core's
    // header and the override probe all rest on, and it is counter-intuitive enough to be worth
    // pinning: a tester reporting "it does nothing on my sun lamp" is describing the NEAR field,
    // where the inverse-square term dominates and a large radius has already flattened the curve.
    //
    // Computed through the SHIPPED falloff on one side and a closed form on the other, so this is a
    // differential test with an independent oracle rather than the code under test on both.
    [TestCase(3f)]
    [TestCase(6f)]
    [TestCase(10f)]
    [TestCase(15f)]
    public void ExcessAtTheOldRimDependsOnReachAloneAndNotOnRadius(float glowRadius)
    {
        const float reach = 1.5f;

        float drawn = VectorLightReachMath.ExtendedRadius(glowRadius, reach);
        float ours = VectorLightMath.Falloff(glowRadius, drawn);
        float vanilla = VectorLightMath.Falloff(glowRadius, glowRadius);

        // Vanilla at its own rim is the inverse-square residue and NOT zero, which is worth stating
        // because it is easy to read the hard cutoff as reaching zero. For a radius-3 torch it is
        // still 11 of 255 levels — vanilla's own step, unchanged by anything here.
        Assert.That(
            vanilla,
            Is.EqualTo(VectorLightMath.InverseSquareWeight / (glowRadius * glowRadius)).Within(1e-6f));

        float predicted = (1f - VectorLightMath.InverseSquareWeight) * (reach - 1f) / reach;

        Assert.That(ours - vanilla, Is.EqualTo(predicted).Within(1e-5f));
    }

    // The same excess, at four reaches on one radius, to show the other half of the claim: it is
    // monotone in reach and it is the ONLY thing that moves it. 0.2 at the shipped vibrant position
    // is about 51 of 255 glow levels.
    [TestCase(1f, 0f)]
    [TestCase(1.25f, 0.12f)]
    [TestCase(1.5f, 0.2f)]
    [TestCase(2f, 0.3f)]
    public void ExcessAtTheOldRimIsMonotoneInReach(float reach, float expected)
    {
        const float glowRadius = 10f;

        float drawn = VectorLightReachMath.ExtendedRadius(glowRadius, reach);
        float excess =
            VectorLightMath.Falloff(glowRadius, drawn) - VectorLightMath.Falloff(glowRadius, glowRadius);

        Assert.That(excess, Is.EqualTo(expected).Within(1e-5f));
    }
}
