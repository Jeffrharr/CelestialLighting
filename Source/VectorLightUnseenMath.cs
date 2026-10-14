using System;

namespace CelestialLighting;

// Whether a change to the occluders inside a rectangle can move an emitter's visibility polygon at
// all. The question a door swing asks of every lamp in reach, nine times an opening.
//
// WHY THIS EXISTS. MarkGeometryDirtyAround admits every emitter whose radius reaches the changed
// cell, and on a built colony most of them are sealed away from it by a wall: they rebake, rebuild
// their mesh and re-upload its UVs to arrive at a coverage grid byte-identical to the one they had.
// The changed-dirty feature already measured that as roughly three bakes in four on the stress
// colony's door storm, and stopped those bakes dirtying sections. It could not stop the bakes
// themselves, because it only learns the answer by baking. This answers the same question first,
// from the polygon the emitter already holds.
//
// THE ARGUMENT, and why it is about the polygon on hand rather than the true visible region. The
// polygon is a fan of rays cast at fixed base angles and at every segment endpoint (± a corner
// epsilon), each stopped at its first hit. If the rectangle does not touch that fan anywhere:
//
//   - no ray reaches the rectangle before its hit, so no hit moves when the occluders inside it
//     change — the old rays give the same answer against the new geometry;
//   - every endpoint of the old geometry outside the rectangle is still an endpoint, so between two
//     consecutive old rays the segment each one hits is still the one segment there, and a new ray
//     the change adds (aimed at an endpoint inside the rectangle) lands on the chord already drawn.
//
// So the polygon a rebake would produce differs from the one on hand only by collinear vertices.
// Coverage is sampled off that same boundary, and VectorLightUnseenMathTests holds the rebaked grid
// to the kept one byte for byte across randomised layouts rather than trusting the argument.
//
// THE MARGIN COVERS THE ONE PLACE A CHORD IS NOT A WALL. Where no ray hits anything the fan ends at
// the radius, and the chord between two base rays cuts inside the circle by a sagitta. A rectangle
// lying in that sliver is outside the fan and still inside reach, so a new ray aimed at it would
// find it. Growing the rectangle by the sagitta closes that, and the same few hundredths of a cell
// also absorb the corner epsilon and float rounding at a shadow edge. Growing it errs towards
// rebaking, which costs only the time this exists to save.
//
// Pure: no Verse, no Unity. Primitives in, bool out.
public static class VectorLightUnseenMath
{
    // Added to the sagitta: the corner epsilon is 1e-4 rad (two thousandths of a cell at radius
    // twenty), and anything under a tenth of a cell is still far inside the cell the change occupies.
    public const float Slack = 0.05f;

    // How far to grow a changed rectangle before asking whether a fan of `baseRayCount` rays at
    // `radius` can see it. The sagitta of one base-ray step, plus Slack.
    public static float Margin(float radius, int baseRayCount)
    {
        float halfStep = (float)(Math.PI / Math.Max(baseRayCount, 1));
        float sagitta = radius * (1f - (float)Math.Cos(halfStep));

        return sagitta + Slack;
    }

    // True when the fan (angles, distances) around (lightX, lightZ) touches the rectangle
    // [minX, maxX] × [minZ, maxZ] grown by `margin` — i.e. when a change inside it CAN move the
    // polygon and the emitter has to rebake.
    //
    // ANY DOUBT ANSWERS TRUE. A fan too small to be a polygon is one Build never produces, and it is
    // answered as visible rather than reasoned about.
    public static bool TouchesRect(
        float[] angles, float[] distances, int count, float lightX, float lightZ,
        float minX, float minZ, float maxX, float maxZ, float margin)
    {
        if (angles == null || distances == null || count < 3)
            return true;

        float x0 = minX - margin;
        float z0 = minZ - margin;
        float x1 = maxX + margin;
        float z1 = maxZ + margin;

        // The light itself is inside the fan, so a rectangle around it is touched outright. Tested
        // first because the boundary test below cannot see it: the fan would sit wholly inside.
        if (PointInRect(lightX, lightZ, x0, z0, x1, z1))
            return true;

        float firstX = lightX + distances[0] * (float)Math.Cos(angles[0]);
        float firstZ = lightZ + distances[0] * (float)Math.Sin(angles[0]);

        float prevX = firstX;
        float prevZ = firstZ;

        bool crosses = false;

        // The boundary is the closed chain of chords, the last one wrapping back to the first ray.
        // A rectangle that touches the fan without containing the light either crosses that chain
        // or lies wholly inside it, and the containment test after the loop answers the second.
        for (int i = 1; i <= count && !crosses; i++)
        {
            int k = i == count ? 0 : i;

            float x = k == 0 ? firstX : lightX + distances[k] * (float)Math.Cos(angles[k]);
            float z = k == 0 ? firstZ : lightZ + distances[k] * (float)Math.Sin(angles[k]);

            crosses = SegmentTouchesRect(prevX, prevZ, x, z, x0, z0, x1, z1);

            prevX = x;
            prevZ = z;
        }

        if (crosses)
            return true;

        return PointInFan(
            angles, distances, count, lightX, lightZ, (x0 + x1) * 0.5f, (z0 + z1) * 0.5f);
    }

    private static bool PointInRect(float x, float z, float x0, float z0, float x1, float z1) =>
        x >= x0 && x <= x1 && z >= z0 && z <= z1;

    // Liang–Barsky: clip the segment's parameter range against each slab and see whether anything
    // is left. Inclusive at the boundary, because a chord lying exactly along the rectangle's edge
    // is a wall face the light reaches, which is the case that must answer true.
    internal static bool SegmentTouchesRect(
        float ax, float az, float bx, float bz, float x0, float z0, float x1, float z1)
    {
        float dx = bx - ax;
        float dz = bz - az;

        float t0 = 0f;
        float t1 = 1f;

        return Clip(-dx, ax - x0, ref t0, ref t1)
            && Clip(dx, x1 - ax, ref t0, ref t1)
            && Clip(-dz, az - z0, ref t0, ref t1)
            && Clip(dz, z1 - az, ref t0, ref t1);
    }

    private static bool Clip(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0f)
            return q >= 0f;

        float r = q / p;

        if (p < 0f)
        {
            if (r > t1)
                return false;

            t0 = Math.Max(t0, r);
            return true;
        }

        if (r < t0)
            return false;

        t1 = Math.Min(t1, r);
        return true;
    }

    // Even–odd crossing test over the same chord chain. Only reached when no chord touches the
    // rectangle, so the point is never on the boundary and the parity is unambiguous.
    private static bool PointInFan(
        float[] angles, float[] distances, int count, float lightX, float lightZ, float px, float pz)
    {
        bool inside = false;

        int j = count - 1;
        float jx = lightX + distances[j] * (float)Math.Cos(angles[j]);
        float jz = lightZ + distances[j] * (float)Math.Sin(angles[j]);

        for (int i = 0; i < count; i++)
        {
            float ix = lightX + distances[i] * (float)Math.Cos(angles[i]);
            float iz = lightZ + distances[i] * (float)Math.Sin(angles[i]);

            bool straddles = (iz > pz) != (jz > pz);

            if (straddles && px < (jx - ix) * (pz - iz) / (jz - iz) + ix)
                inside = !inside;

            jx = ix;
            jz = iz;
        }

        return inside;
    }
}
