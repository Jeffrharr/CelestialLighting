using System;
using System.Collections.Generic;

namespace CelestialLighting;

// The visibility polygon built the slow, obvious way: every ray against every segment, no index.
//
// WHY A SECOND IMPLEMENTATION EXISTS AT ALL. VectorLightMath.Build culls each ray to the segments
// whose arc could contain it, and the claim behind that cull is not "close enough" but "identical" —
// a culled segment is one the solver would have rejected anyway. A claim of exact equality can only
// be tested against something that does not share the code making the claim; computing both sides
// with the shipped Build and asserting they match would assert x - x == 0.
//
// IT IS A VERBATIM TRANSCRIPTION OF THE PRE-CULL Build, and that is deliberate on both counts.
// Independent of the code under test, because the index does not exist here at all — but not
// independently *written*, because this doubles as the baseline arm of Tools/VectorLightBench and a
// baseline written fresh is a strawman. The first cut of this file used foreach where the shipped
// loop used an index, and measured 1.67x slower than the code it was standing in for, which would
// have inflated every speedup quoted against it. So: same loop forms, same helper split, same
// un-presized list. The only thing missing is the cull.
//
// It lives in the test project and is LINKED into Tools/VectorLightBench rather than copied, so the
// arm the benchmark calls slow is the identical one the equivalence test calls correct.
public static class VectorLightBuildOracle
{
    public static VectorLightMath.LightPolygon Build(
        float lightX, float lightZ, float radius, VectorLightMath.Segment[] segments, int baseRayCount)
    {
        List<float> angles = new List<float>();

        AddBaseRays(angles, baseRayCount);
        AddCornerRays(angles, lightX, lightZ, segments);

        angles.Sort();

        float[] outAngles = new float[angles.Count];
        float[] outDistances = new float[angles.Count];
        int count = 0;

        for (int i = 0; i < angles.Count; i++)
        {
            float angle = angles[i];
            float distance = VectorLightMath.CastRay(lightX, lightZ, angle, radius, segments);

            if (!IsRedundant(outAngles, outDistances, count, angle, distance))
            {
                outAngles[count] = angle;
                outDistances[count] = distance;
                count++;
            }
        }

        return new VectorLightMath.LightPolygon(outAngles, outDistances, count);
    }

    private static bool IsRedundant(float[] angles, float[] distances, int count, float angle, float distance)
    {
        if (count == 0)
            return false;

        bool sameAngle = Math.Abs(angle - angles[count - 1]) < 1e-7f;
        bool sameDistance = Math.Abs(distance - distances[count - 1]) < 1e-6f;
        return sameAngle && sameDistance;
    }

    private static void AddBaseRays(List<float> angles, int baseRayCount)
    {
        for (int i = 0; i < baseRayCount; i++)
            angles.Add((float)(-Math.PI + 2.0 * Math.PI * i / baseRayCount));
    }

    private static void AddCornerRays(
        List<float> angles, float lightX, float lightZ, VectorLightMath.Segment[] segments)
    {
        if (segments == null)
            return;

        for (int i = 0; i < segments.Length; i++)
        {
            AddCornerRay(angles, lightX, lightZ, segments[i].X1, segments[i].Z1);
            AddCornerRay(angles, lightX, lightZ, segments[i].X2, segments[i].Z2);
        }
    }

    private static void AddCornerRay(
        List<float> angles, float lightX, float lightZ, float cornerX, float cornerZ)
    {
        float angle = (float)Math.Atan2(cornerZ - lightZ, cornerX - lightX);
        angles.Add(angle - VectorLightMath.CornerRayEpsilon);
        angles.Add(angle);
        angles.Add(angle + VectorLightMath.CornerRayEpsilon);
    }
}
