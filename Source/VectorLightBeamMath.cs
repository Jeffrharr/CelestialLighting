namespace CelestialLighting;

// §27 phase 3d: the owed-light BEAM as its own geometry, instead of as numbers written into vanilla's
// per-cell vertex colours.
//
// WHY A SEPARATE DRAW AFTER PHASE 3C ALREADY WORKED. Phase 3c's arithmetic is right and stays: the
// term is provably zero wherever vanilla already delivered, which is what finally stopped the lit room
// moving. What it could not fix is RESOLUTION. The lighting overlay has one vertex per cell corner
// plus one per centre, so every value written is averaged over the up-to-four cells meeting a corner.
// For vanilla's smooth flood that is nearly lossless. A doorway beam is not smooth: through a one-cell
// aperture it is one cell wide — measured, coverage 255 on the axis, 64 one cell off, 0 two cells off
// — so each corner averages a lit cell against neighbours at almost nothing.
//
// The loss was measured rather than assumed. The term delivers 58 units of glow at the first cell
// outside the door and the frame lifted +8 red, while the mirror cell inside the room — same distance
// from the lamp, vanilla delivering the same 58 — rendered about +22. About a third survived, which is
// why phase 3c needed a gain of 3 to look right, and why even then it reads as a rounded pool rather
// than a shaft: a 2-cell-wide, 6-cell-long feature smoothed over 4-cell corners is an ellipse.
//
// WHAT THIS DOES INSTEAD. The visibility polygon is already sub-cell — an inscribed n-gon built from
// real occluder segments, not a cell grid — and VectorLightOverlay already draws it as a triangle fan
// whose U coordinate is distance/radius and whose gradient texture bakes vanilla's own falloff into
// alpha. So the beam does not need a new lighting model at all. It needs the fan CLIPPED to the region
// where light is owed, which is what this builds.
//
// THE PROPERTY THAT MAKES IT SAFE, AND THAT PHASES 1 AND 2 DID NOT HAVE. Phase 1 drew our whole model
// over vanilla's and landed 6 L* bright; phase 2b tried to compose them as a max and measured a no-op,
// because our falloff IS vanilla's falloff so the max returned vanilla everywhere the polygon could
// see. Both failed on the same question: what happens where the two models overlap. The differential
// answers it once and for all — owed is zero there — so a mesh clipped to the owed region CANNOT paint
// the lit room, at any resolution, by construction rather than by calibration. Phase 3c turns out to be
// the enabler for the drawn approach rather than a replacement for it.
//
// WHAT IT DELIBERATELY DOES NOT COVER. Only the "vanilla delivered NOTHING here" case, which is the
// beam and the whole visible headline. The partial case — vanilla bent around a corner and arrived
// dimmer than a straight line would have — stays on phase 3c's per-cell path, because a partial
// shortfall is a magnitude and this is a geometric mask. The two are disjoint by construction
// (delivered == 0 versus delivered > 0) so they cannot double-count, and VectorLightMask enforces the
// split rather than trusting it.
public static class VectorLightBeamMath
{
    // How finely the owed region is traced along each sector, in cells.
    //
    // A QUARTER CELL, because the near end of the beam is the edge that matters most and it lands
    // wherever the wall is rather than on a cell boundary. Marching in whole cells would put the
    // beam's mouth back on the grid this whole file exists to escape — the angular edges would be
    // sharp and the mouth would still be a staircase, which reads as a bug rather than as a beam.
    public const float MarchStep = 0.25f;

    // The mask is sampled per step per sector, so the arrays are sectors x steps. Named rather than
    // inlined because the adapter has to allocate to exactly this shape and a disagreement between
    // the two would silently read a neighbouring sector's flags.
    public static int StepsFor(float radius)
    {
        if (radius <= 0f)
            return 0;

        return (int)(radius / MarchStep) + 1;
    }

    // The far radius of step i. Steps are measured at their OUTER edge so that a run ending at step i
    // covers ground the sample at i actually vouched for.
    public static float RadiusAtStep(int step) => (step + 1) * MarchStep;

    // The mid-angle of sector s, which is where the adapter samples. Sampling at a ray instead would
    // straddle the boundary the ray was built to sit on — half the samples of a doorway's edge sector
    // would land in the wall.
    public static float SectorMidAngle(VectorLightMath.LightPolygon polygon, int sector)
    {
        int next = (sector + 1) % polygon.Count;
        float a0 = polygon.Angles[sector];
        float a1 = polygon.Angles[next];
        float delta = a1 - a0;

        // The wrap sector spans the seam at 2pi, where a naive midpoint lands exactly opposite the
        // sector it belongs to. Half a turn is the only case, so testing for it is exact.
        if (delta < 0f)
            delta += TwoPi;

        return a0 + delta * 0.5f;
    }

    public const float TwoPi = 6.28318548f;

    // Quads for every run of owed light, in the same LightMesh shape VectorLightOverlay already
    // uploads — so the existing material, gradient texture and property block are reused unchanged and
    // the beam gets vanilla's falloff for free through U.
    //
    // ONE QUAD PER RUN, NOT PER STEP. A radius-10 lamp at a quarter-cell step is 41 steps across 48
    // sectors, so per-step quads would be about two thousand per emitter every rebuild. Merging
    // consecutive owed steps makes a doorway beam one or two quads per sector, and the merge is exact
    // rather than approximate: the run's endpoints are step boundaries either way.
    // `rayNear` is the first radius at which light is owed along each POLYGON RAY, or a value past
    // the radius where none ever is.
    //
    // WHY THE QUAD CANNOT USE ONE NEAR EDGE FOR BOTH CORNERS. A run's near radius is measured along
    // the sector's mid-angle, so using it for both corners makes the beam's mouth an ARC centred on
    // the lamp. An aperture is a straight wall. Off the axis the arc bulges back through it: at a
    // run starting 2.5 cells out, a ray at angle t reaches only 2.5*cos(t) along the wall's normal,
    // so the quad's corner lands INSIDE the room and paints the floor there.
    //
    // Measured on the first build with the door cell included: the room read a 40-level lift in a
    // band a third of a cell deep just inside the doorway, having read exactly 0 the build before.
    // Clamping each corner to its own ray's first owed radius makes the mouth a chord across the
    // aperture instead of an arc through it.
    // `rayEdge` is how OCCLUDED each ray's own edge is, 0 for the middle of the beam and 1 for a ray
    // that bounds it. It becomes the vertex's V, which is the gradient texture's second axis — the
    // penumbra ramp the fan already uses for its soft shadow edges.
    //
    // WHY THE BEAM NEEDS IT AND THE FAN'S WEDGES DO NOT COVER IT. Every quad here used to carry V = 0,
    // the gradient's fully-lit row, so the beam had knife edges down both sides: a hard-edged wedge on
    // near-black ground, which reads as a rendering artefact rather than as light. It is also wrong on
    // its own terms. A lamp is not a point source — §27 already models a source radius for exactly
    // this reason — so light through an aperture has a real penumbra along both flanks, and a doorway
    // beam is nearly all flank.
    public static VectorLightMath.LightMesh BuildOwedMesh(
        float lightX, float lightZ, float radius,
        VectorLightMath.LightPolygon polygon, bool[] owed, int stepsPerSector, float[] rayNear,
        float[] rayEdge)
    {
        if (polygon.Count == 0 || radius <= 0f || owed == null || stepsPerSector <= 0)
            return Empty();

        int maxRuns = polygon.Count * (stepsPerSector / 2 + 1);
        float[] xs = new float[maxRuns * 4];
        float[] zs = new float[maxRuns * 4];
        float[] us = new float[maxRuns * 4];
        float[] vs = new float[maxRuns * 4];
        int[] tris = new int[maxRuns * 6];

        int verts = 0;
        int triCount = 0;

        for (int sector = 0; sector < polygon.Count; sector++)
        {
            int next = (sector + 1) % polygon.Count;
            float a0 = polygon.Angles[sector];
            float a1 = polygon.Angles[next];
            float d0 = polygon.Distances[sector];
            float d1 = polygon.Distances[next];
            int step = 0;

            while (step < stepsPerSector)
            {
                // ONE predicate for both the run's start and its continuation. They were separate
                // once and disagreed by a step, which started every run a quarter-cell before it was
                // allowed to and grew a lip through the wall the beam was meant to begin at.
                if (IsOwedAt(owed, sector, step, stepsPerSector, d0, d1))
                {
                    int runStart = step;

                    while (IsOwedAt(owed, sector, step, stepsPerSector, d0, d1))
                        step++;

                    float near = runStart * MarchStep;
                    float near0 = rayNear == null ? near : Max(near, rayNear[sector]);
                    float near1 = rayNear == null ? near : Max(near, rayNear[next]);

                    float v0 = rayEdge == null ? 0f : rayEdge[sector];
                    float v1 = rayEdge == null ? 0f : rayEdge[next];

                    Emit(lightX, lightZ, radius, a0, a1, d0, d1,
                        near0, near1, RadiusAtStep(step - 1), v0, v1,
                        xs, zs, us, vs, tris, ref verts, ref triCount);
                }
                else
                {
                    step++;
                }
            }
        }

        // TRIMMED TO THE USED LENGTH, which is not tidiness. VectorLightOverlay.UploadMesh does
        // Tris.AddRange(built.Triangles) over the WHOLE array, so handing it the oversized buffer
        // would upload thousands of zero-indexed degenerate triangles — every one of them a
        // zero-area sliver at the first vertex, which draws nothing but costs a full mesh upload
        // and makes the vertex count meaningless to anything reading it.
        return new VectorLightMath.LightMesh(
            Trim(xs, verts), Trim(zs, verts), Trim(us, verts), Trim(vs, verts),
            TrimInts(tris, triCount), verts, triCount);
    }

    private static float[] Trim(float[] source, int count)
    {
        float[] trimmed = new float[count];
        System.Array.Copy(source, trimmed, count);
        return trimmed;
    }

    private static int[] TrimInts(int[] source, int count)
    {
        int[] trimmed = new int[count];
        System.Array.Copy(source, trimmed, count);
        return trimmed;
    }

    // Whether this sector owes light at this step AND the step is still inside the polygon on both
    // of the sector's rays. Named because the run loop and the run-start test have to agree exactly;
    // when they disagreed, runs started one step before they were allowed to and every beam grew a
    // quarter-cell lip through the wall it was supposed to start at.
    private static bool IsOwedAt(
        bool[] owed, int sector, int step, int stepsPerSector, float d0, float d1)
    {
        if (step >= stepsPerSector)
            return false;

        int flat = sector * stepsPerSector + step;

        if (flat >= owed.Length || !owed[flat])
            return false;

        float near = step * MarchStep;

        return near < d0 && near < d1;
    }

    // One quad spanning [near, far] across the sector, with each outer corner clamped to its OWN ray's
    // polygon distance. Clamping both to min(d0, d1) instead would square off the beam's far end and
    // lose the polygon boundary this file exists to follow.
    private static void Emit(
        float lightX, float lightZ, float radius, float a0, float a1, float d0, float d1,
        float nearA, float nearB, float far, float v0, float v1,
        float[] xs, float[] zs, float[] us, float[] vs, int[] tris, ref int verts, ref int triCount)
    {
        float near0 = nearA < d0 ? nearA : d0;
        float near1 = nearB < d1 ? nearB : d1;
        float far0 = far < d0 ? far : d0;
        float far1 = far < d1 ? far : d1;

        int baseIndex = verts;

        Put(lightX, lightZ, radius, a0, near0, v0, xs, zs, us, vs, ref verts);
        Put(lightX, lightZ, radius, a1, near1, v1, xs, zs, us, vs, ref verts);
        Put(lightX, lightZ, radius, a1, far1, v1, xs, zs, us, vs, ref verts);
        Put(lightX, lightZ, radius, a0, far0, v0, xs, zs, us, vs, ref verts);

        tris[triCount++] = baseIndex;
        tris[triCount++] = baseIndex + 1;
        tris[triCount++] = baseIndex + 2;
        tris[triCount++] = baseIndex;
        tris[triCount++] = baseIndex + 2;
        tris[triCount++] = baseIndex + 3;
    }

    private static void Put(
        float lightX, float lightZ, float radius, float angle, float distance, float edge,
        float[] xs, float[] zs, float[] us, float[] vs, ref int verts)
    {
        xs[verts] = lightX + Cos(angle) * distance;
        zs[verts] = lightZ + Sin(angle) * distance;

        // U is distance as a fraction of the radius, exactly as the fan's is, so the beam samples the
        // SAME baked falloff curve the lit region does. That is what makes the light coming out of the
        // door read as the room's own light continuing rather than as a second effect with its own
        // profile — which is the thing that was actually being asked for.
        us[verts] = radius <= 0f ? 0f : distance / radius;

        // V walks the gradient's penumbra ramp: 0 in the body of the beam, 1 on a ray that bounds it,
        // so a quad spanning the boundary fades across its own width instead of ending at a knife
        // edge. Same axis, same texture and same meaning as the fan's soft shadow edges.
        vs[verts] = edge;

        verts++;
    }

    private static float Max(float a, float b) => a > b ? a : b;

    private static VectorLightMath.LightMesh Empty() =>
        new VectorLightMath.LightMesh(
            new float[0], new float[0], new float[0], new float[0], new int[0], 0, 0);

    // Local trig so this file stays free of UnityEngine, per the repo's pure-core rule.
    private static float Cos(float radians) => (float)System.Math.Cos(radians);

    private static float Sin(float radians) => (float)System.Math.Sin(radians);
}
