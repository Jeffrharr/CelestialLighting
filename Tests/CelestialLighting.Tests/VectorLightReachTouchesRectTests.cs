using NUnit.Framework;

namespace CelestialLighting.Tests;

// Offline tests for VectorLightMath.ReachTouchesRect, the camera-cull predicate the overlay pass
// asks per lamp and the pawn-shadow pass asks once per frame to shrink the roster every visible
// pawn walks (CelestialLightingFeatures.VectorLightShadowLampCull).
//
// The property that matters is not "it culls" but "it never culls a lamp the shadow pass would
// have accepted": Gather admits a lamp when the float distance from the lamp's cell centre to a
// pawn's DrawPos is within the radius, and the pawn's DrawPos can sit anywhere inside a cell that
// is itself inside the view rect. So the sweep below places a pawn at every extreme of a one-cell
// rect and a lamp at the farthest cell Gather could still accept, and checks the predicate keeps
// it. The reject cases pin that the reach is not merely "always true".
[TestFixture]
public class VectorLightReachTouchesRectTests
{
    // A lamp whose disc touches the rect only at a corner, from each of the four diagonals.
    [TestCase(10, 10, 5f, 15, 15, 15, 15, true)]
    [TestCase(10, 10, 5f, 5, 5, 5, 5, true)]
    [TestCase(10, 10, 5f, 15, 5, 15, 5, true)]
    [TestCase(10, 10, 5f, 5, 15, 5, 15, true)]
    // The lamp's own cell.
    [TestCase(10, 10, 5f, 10, 10, 10, 10, true)]
    // One cell past the integer reach on each axis is out.
    [TestCase(10, 10, 5f, 17, 10, 17, 10, false)]
    [TestCase(10, 10, 5f, 3, 10, 3, 10, false)]
    [TestCase(10, 10, 5f, 10, 17, 10, 17, false)]
    [TestCase(10, 10, 5f, 10, 3, 10, 3, false)]
    // The integer reach itself is in: (int)5 + 1 = 6.
    [TestCase(10, 10, 5f, 16, 10, 16, 10, true)]
    [TestCase(10, 10, 5f, 4, 10, 4, 10, true)]
    // A fractional radius rounds DOWN before the +1, so 5.9 reaches 6 cells, not 7.
    [TestCase(10, 10, 5.9f, 16, 10, 16, 10, true)]
    [TestCase(10, 10, 5.9f, 17, 10, 17, 10, false)]
    // A rect that contains the lamp outright.
    [TestCase(10, 10, 5f, 0, 0, 20, 20, true)]
    // A wide rect the lamp lies just beyond on one axis only.
    [TestCase(10, 10, 5f, 17, 0, 40, 40, false)]
    [TestCase(10, 10, 5f, 0, 0, 40, 3, false)]
    public void TouchesExactlyWhenTheIntegerReachMeetsTheRect(
        int cellX, int cellZ, float radius, int minX, int minZ, int maxX, int maxZ, bool expected)
    {
        Assert.That(
            VectorLightMath.ReachTouchesRect(cellX, cellZ, radius, minX, minZ, maxX, maxZ),
            Is.EqualTo(expected));
    }

    // The soundness sweep for the shadow pass: for every lamp cell whose centre is within `radius`
    // of ANY point inside a pawn cell that lies inside the rect, the predicate must keep the lamp.
    // Sampled at the pawn cell's four corners and centre, which are the extreme DrawPos positions
    // a pawn standing in that cell can take, over a ring of radii including fractional ones.
    [TestCase(3f)]
    [TestCase(5f)]
    [TestCase(5.5f)]
    [TestCase(7.9f)]
    [TestCase(11.99f)]
    public void NeverRejectsALampGatherWouldAccept(float radius)
    {
        const int pawnX = 50, pawnZ = 50;
        int span = (int)radius + 3;

        for (int lampX = pawnX - span; lampX <= pawnX + span; lampX++)
        for (int lampZ = pawnZ - span; lampZ <= pawnZ + span; lampZ++)
        {
            float lightX = lampX + 0.5f;
            float lightZ = lampZ + 0.5f;

            bool gatherAccepts = false;

            foreach ((float ox, float oz) in new[] {
                (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f), (0.5f, 0.5f) })
            {
                float dx = pawnX + ox - lightX;
                float dz = pawnZ + oz - lightZ;
                gatherAccepts |= dx * dx + dz * dz <= radius * radius;
            }

            if (gatherAccepts)
            {
                Assert.That(
                    VectorLightMath.ReachTouchesRect(
                        lampX, lampZ, radius, pawnX, pawnZ, pawnX, pawnZ),
                    Is.True,
                    $"lamp ({lampX},{lampZ}) r={radius} lights pawn cell but was culled");
            }
        }
    }
}
