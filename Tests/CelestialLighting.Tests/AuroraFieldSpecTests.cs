using CelestialLighting;
using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline coverage for the descriptions the aurora adapter drives itself from.
//
// These are cheap assertions guarding expensive mistakes. A field spec is the one place where a wrong
// number does not fail loudly: the texture still bakes, the quads still draw, and the aurora simply
// looks worse than it did — which is exactly the class of regression that survives a code review and
// is only caught weeks later by someone squinting at a screenshot.
[TestFixture]
public class AuroraFieldSpecTests
{
    private static readonly AuroraFieldSpec[] All =
    {
        AuroraFieldRegistry.HemRays,
        AuroraFieldRegistry.Contour,
    };

    [Test]
    public void EverySpec_IsInternallyCoherent()
    {
        foreach (AuroraFieldSpec spec in All)
        {
            Assert.That(spec.Fill, Is.Not.Null, $"{spec.Name} has no fill delegate");
            Assert.That(spec.Sheets, Is.Not.Empty, $"{spec.Name} declares no sheets");
            Assert.That(spec.ResolutionX, Is.GreaterThanOrEqualTo(32), $"{spec.Name} width");
            Assert.That(spec.ResolutionY, Is.GreaterThanOrEqualTo(32), $"{spec.Name} height");
            Assert.That(spec.RefreshRows, Is.GreaterThan(0), $"{spec.Name} refresh rows");
            Assert.That(spec.DriftWrapTicks, Is.GreaterThan(0), $"{spec.Name} drift wrap");
            Assert.That(spec.TintWeight, Is.InRange(0f, 1f), $"{spec.Name} tint weight");

            foreach (AuroraSheetSpec sheet in spec.Sheets)
            {
                Assert.That(sheet.CellsPerRepeatX, Is.GreaterThan(0f), $"{spec.Name} sheet width");
                Assert.That(sheet.CellsPerRepeatY, Is.GreaterThan(0f), $"{spec.Name} sheet height");
                Assert.That(sheet.Alpha, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f), $"{spec.Name} alpha");
            }
        }
    }

    [Test]
    public void RefreshRows_DivideTheTextureWithoutStranding_ARow()
    {
        // The refresh cursor wraps when it passes ResolutionY, so a slice size that does not divide the
        // height leaves the final partial slice re-baking rows already done and skipping the last few
        // entirely. Those rows would then hold whatever the previous aurora left there — invisible while
        // the field is similar, and a stale band across the sky after a retune.
        foreach (AuroraFieldSpec spec in All)
            Assert.That(spec.ResolutionY % spec.RefreshRows, Is.Zero,
                $"{spec.Name}: {spec.RefreshRows} rows do not divide {spec.ResolutionY}");
    }

    [Test]
    public void HemRays_HasEnoughTexelsToResolveItsFinestRays()
    {
        // The constant that silently destroys this field's look, with no other guard anywhere.
        //
        // The rays are the finest structure in the field, and there are RayPeriod lobes of them across
        // the tile, so what matters is texels PER LOBE, not texels per cell. At 96 texels that was 1.6
        // per lobe and bilinear filtering ate them entirely — the rays came out as a soft wash and the
        // curtain read as smoke. 192 gives 3.2, which works. Below about 3 it degrades fast.
        int finest = 0;

        for (int i = 0; i < AuroraCurtainHemRays.CurtainCount; i++)
        {
            int period = AuroraCurtainHemRays.Curtain(i).RayPeriod;
            if (period > finest)
                finest = period;
        }

        float texelsPerLobe = AuroraFieldRegistry.HemRays.ResolutionX / (float)finest;

        Assert.That(texelsPerLobe, Is.GreaterThanOrEqualTo(3f),
            $"{texelsPerLobe:F1} texels per ray lobe — the rays will blur into a wash");
    }

    [Test]
    public void EverySpec_WrapsItsDriftOnItsOwnFieldsCycle()
    {
        // DriftWrapTicks is consumed by the adapter for BOTH the field clock and the pan wrap, so it has
        // to travel with the field rather than being a global. The two fields genuinely differ, and
        // using one field's number with the other's maths puts a visible discontinuity in the sky some
        // hours into a colony — the kind of bug that cannot be found by looking.
        Assert.That(AuroraFieldRegistry.HemRays.DriftWrapTicks,
            Is.EqualTo(AuroraCurtainHemRays.DriftWrapTicks));
        Assert.That(AuroraFieldRegistry.Contour.DriftWrapTicks,
            Is.EqualTo(AuroraCurtain.DriftWrapTicks));
        Assert.That(AuroraFieldRegistry.HemRays.DriftWrapTicks,
            Is.Not.EqualTo(AuroraFieldRegistry.Contour.DriftWrapTicks),
            "if these ever coincide, this test stops proving anything — pick new evidence");
    }

    [Test]
    public void HemRays_IsBounded_AndContourSpansTheMap()
    {
        // The distinction the whole sheet layout turns on: hem-rays' v axis is altitude and must not
        // repeat; the contour field's v axis is map-north and may.
        Assert.That(AuroraFieldRegistry.HemRays.Sheets[0].SpansMapVertically, Is.False);

        foreach (AuroraSheetSpec sheet in AuroraFieldRegistry.Contour.Sheets)
            Assert.That(sheet.SpansMapVertically, Is.True);
    }

    [Test]
    public void ActiveField_IsTheOneChosenForShipping()
    {
        // Not ceremony. The contour field is deliberately kept compiled and tested for the planned §11b
        // world-map auroral oval, so it stays one line away from being drawn by accident.
        Assert.That(AuroraFieldRegistry.Active, Is.SameAs(AuroraFieldRegistry.HemRays));
    }
}
