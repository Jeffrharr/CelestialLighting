namespace CelestialLighting.Tests;

// Offline unit tests for §30's pure core (Source/ShadowRootShadeMath.cs, linked into this project so
// they run against the exact shipped file).
//
// The predicate is one line, so the boolean algebra is not what is worth pinning. What is worth
// pinning is the three decisions encoded in it, each of which a later reader could "simplify" away
// without any frame they happened to be looking at changing: that height 0 is excluded rather than
// included, that a door is excluded at every height, and that height otherwise does not scale the
// answer at all. Each test below names the case in the game that decision is about.
[TestFixture]
public class ShadowRootShadeMathTests
{
    // Vanilla's own staticSunShadowHeight values, so these are the numbers the renderer really sees:
    // 1.0 walls and natural rock, 0.5 shelves and the vanometric cell, 0.20 benches and the billiards
    // table, 0.17 beds and dressers.
    //
    // All of them shade, and that is the point rather than an accident of the expression: the ground
    // at the foot of an opaque object is out of the sun whether the object is a wall or a dresser.
    // Height decides how FAR the shadow is thrown, which is the skirt's job and already correct.
    [TestCase(1.0f)]
    [TestCase(0.5f)]
    [TestCase(0.2f)]
    [TestCase(0.17f)]
    public void AnyCasterAboveZeroTakesTheShade(float casterHeight)
    {
        Assert.That(ShadowRootShadeMath.TakesRootShade(casterHeight, isDoor: false), Is.True);
    }

    // An empty cell. Vanilla encodes "casts nothing" as height 0 and EaveShadowGrid carries that
    // convention forward, so this is the overwhelmingly common case on any map: shade nothing.
    [Test]
    public void EmptyCellTakesNoShade()
    {
        Assert.That(ShadowRootShadeMath.TakesRootShade(0f, isDoor: false), Is.False);
    }

    // A def that explicitly switches its static shadow off. Vanilla's FenceGate does exactly this
    // (`<staticSunShadowHeight>0</staticSunShadowHeight>`, commented "disable static shadow"), as do
    // the watermill generator and one ancient outdoor def. They throw no skirt, so there is no root
    // for a root shade to join up with, and shading them would invent a dark cell vanilla has never
    // drawn. This is why the predicate tests `> 0f` and not `>= 0f`.
    [Test]
    public void DefWithStaticShadowDisabledTakesNoShade()
    {
        Assert.That(ShadowRootShadeMath.TakesRootShade(0f, isDoor: false), Is.False);
    }

    // Doors, at both a wall's height and a low one. DoorBase declares staticSunShadowHeight 1.0, so
    // without this a door would qualify like any wall — harmless while it is shut, because the sprite
    // covers the cell, but an OPEN door is drawn as two halves slid aside and the middle of the cell
    // shows floor. Shading it would paint a dark square across an open doorway: an artifact vanilla
    // does not have, and the exact opposite of what this subsystem exists to do.
    [TestCase(1.0f)]
    [TestCase(0.17f)]
    public void DoorsAreExcludedAtEveryHeight(float casterHeight)
    {
        Assert.That(ShadowRootShadeMath.TakesRootShade(casterHeight, isDoor: true), Is.False);
    }

    // FenceGate is both a door and a zero-height caster, so it must stay excluded when the two
    // reasons coincide rather than cancelling.
    [Test]
    public void DoorThatAlsoDisablesItsShadowStaysExcluded()
    {
        Assert.That(ShadowRootShadeMath.TakesRootShade(0f, isDoor: true), Is.False);
    }

    // staticSunShadowHeight is an unvalidated ThingDef float, so a modded def can put anything in it.
    // Formulas.ShadowCasterAlphaByte already owns the clamp for the MESH side; this side only has to
    // not admit nonsense. A negative height is excluded because it casts nothing, and NaN is excluded
    // by construction (every comparison against NaN is false), which is the safe direction: an
    // unknown caster adds no shade rather than shading a cell nothing stands on.
    [TestCase(-0.5f)]
    [TestCase(float.NaN)]
    public void NonsenseHeightsAreExcluded(float casterHeight)
    {
        Assert.That(ShadowRootShadeMath.TakesRootShade(casterHeight, isDoor: false), Is.False);
    }

    // A modded def declaring more than a full wall still shades its own cell exactly once — there is
    // no scaling here to overflow, which is the whole reason this half is a bool and not a float.
    [Test]
    public void OversizedModdedHeightStillJustShades()
    {
        Assert.That(ShadowRootShadeMath.TakesRootShade(1.2f, isDoor: false), Is.True);
    }
}
