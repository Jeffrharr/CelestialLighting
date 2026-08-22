using System;

namespace CelestialLighting;

// The coverage grid built the slow, obvious way: every cell sampled, no bounds.
//
// WHY A SECOND IMPLEMENTATION EXISTS AT ALL. VectorLightMath.BuildCoverage answers a cell from the
// polygon's nearest and farthest ray wherever those two settle the question, and the claim behind
// that is not "close enough" but "identical" — a cell the bounds answer is one the sampler would
// have answered the same way. A claim of exact equality can only be tested against something that
// does not share the code making the claim; computing both sides with the shipped BuildCoverage
// would assert x - x == 0.
//
// IT IS A VERBATIM TRANSCRIPTION OF THE PRE-BOUNDS BuildCoverage, for the same two reasons the
// visibility polygon's oracle is one. Independent of the code under test, because the bounds do not
// exist here at all — but not independently *written*, because it doubles as the baseline arm of
// Tools/VectorLightBench, and a baseline written fresh is a strawman that inflates every ratio
// quoted against it. Same loop nesting, same LitFraction call, same round-and-cast.
//
// It calls VectorLightMath.LitFraction rather than transcribing that too, which is the same line
// VectorLightBuildOracle draws at CastRay: the sampler is the shared arithmetic both arms are
// entitled to agree on, and it is not what either fixture is testing.
public static class VectorLightCoverageOracle
{
    public static byte[] BuildCoverage(
        VectorLightMath.LightPolygon polygon,
        int lightCellX, int lightCellZ, int radiusCells, int samplesPerAxis)
    {
        int span = radiusCells * 2 + 1;
        byte[] grid = new byte[span * span];

        if (polygon.Count == 0 || radiusCells < 0)
            return grid;

        float lightX = lightCellX + 0.5f;
        float lightZ = lightCellZ + 0.5f;

        for (int zi = 0; zi < span; zi++)
        {
            for (int xi = 0; xi < span; xi++)
            {
                float lit = VectorLightMath.LitFraction(
                    polygon, lightX, lightZ,
                    lightCellX - radiusCells + xi, lightCellZ - radiusCells + zi, samplesPerAxis);

                grid[zi * span + xi] = (byte)Math.Round(Clamp01(lit) * 255f);
            }
        }

        return grid;
    }

    // Transcribed rather than reached for, because VectorLightMath's own is private. Identical.
    private static float Clamp01(float value)
    {
        if (value < 0f)
            return 0f;

        return value > 1f ? 1f : value;
    }
}
