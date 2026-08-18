using System;
using System.Collections.Generic;

namespace CelestialLighting;

// §27 vector light sources, pure core: turns a light position plus a grid of light-blocking cells
// into the VISIBILITY POLYGON the light actually reaches, by casting rays at the corners of the
// obstructions — rather than flooding outward cell by cell the way vanilla does.
//
// WHY NOT VANILLA'S MODEL. Verse.Glow.ComputeGlowGridsJob runs a Dijkstra flood per glower over an
// 8-neighbour lattice, and the distance it accumulates is GEODESIC: the length of the shortest path
// AROUND the walls, not the straight line. Three consequences, all visible on screen. Light bends
// around corners, so a lamp behind a wall smears onto the far side. A diagonal step is refused only
// when BOTH flanking cardinals are blockers, so a single diagonal gap leaks. And — the one that
// matters most — the grid records how FAR light travelled and never which DIRECTION it came from,
// so nothing downstream can draw the dark side of anything. That last is why issue #48 has sat open
// since the mod started: a pawn beside a brazier at midnight is lit on all sides and throws nothing.
//
// This file answers a different question, the one a photon would ask: from where the light is, what
// can it SEE? Everything the light can see is lit, everything it cannot is dark, and the boundary
// between them is a straight line through a corner rather than a stairstep along a lattice. That is
// what produces the wedge through a doorway and the shadow behind a rock in one mechanism.
//
// WHAT THIS DELIBERATELY DOES NOT DO. It does not touch, read, or reproduce map.glowGrid. Vanilla's
// flood is still the GAMEPLAY light — plant growth, work speed, mood, StatPart_Glow, unnatural
// darkness and every mod reading GroundGlowAt all keep the numbers they have always had. §27 changes
// only what is drawn. That split is the whole reason this subsystem is allowed to be as opinionated
// as it is: being wrong here costs a look, not a save.
//
// Pure by the repo's convention: no UnityEngine, no Verse, primitives in and primitives out, so
// Tools/VectorLightPreview renders the exact code that ships and the NUnit suite drives it offline.
public static class VectorLightMath
{
    // Rays cast evenly around the full circle regardless of geometry. Without these an unobstructed
    // light would have no vertices at all (there are no corners to aim at) and a lightly obstructed
    // one would render as a coarse hull between whatever corners happened to exist. 48 is 7.5° per
    // step, which at radius 14 puts the chord no more than 0.03 cells inside the true circle —
    // comfortably below the point where a player could see the rim is a polygon.
    public const int DefaultBaseRayCount = 48;

    // Samples in the baked falloff gradient. 256 is one entry per output byte, so the gradient can
    // never be the thing quantising the light.
    public const int GradientSize = 256;

    // The angular nudge either side of a corner ray. The point of the three-ray trick is that the
    // middle ray stops exactly ON the corner while its two neighbours slip past it — one landing on
    // whatever is behind, one on the near face — and those two together ARE the shadow edge. Too
    // small and floating-point puts all three on the corner (no shadow); too large and the wedge is
    // visibly clipped. At 1e-4 rad the far ray lands 0.0014 cells to the side at radius 14.
    public const float CornerRayEpsilon = 1e-4f;

    // Vanilla seeds the flood's centre cell at intDist = 100, i.e. one cell, so its own falloff curve
    // is never evaluated closer than 1. Matching that matters for more than tidiness: the inverse
    // square term diverges at zero, and a light whose first ring is brighter than white would clip
    // to a flat disc exactly where the eye is most likely to be looking.
    public const float MinFalloffDistance = 1f;

    // How much of vanilla's falloff is inverse-square versus linear. Lifted from
    // ComputeGlowGridsJob.SetGlowFromDist — Mathf.Lerp(1f + num * num2, b, 0.4f) — because a lamp
    // with a given glowRadius should read at about the brightness players already expect from it.
    // We change WHERE the light reaches, not how bright a lamp is.
    public const float InverseSquareWeight = 0.4f;

    // The emitter's own half-width in cells, which is the entire reason a penumbra exists. A point
    // source casts a perfectly hard shadow at every distance; a source of finite size casts a soft
    // one, because near the edge of a shadow a receiver can see PART of the source. A standing lamp,
    // a torch and a campfire all occupy about one cell, so half a cell is the half-width of all of
    // them, and the difference between them is not worth a per-def lookup.
    public const float DefaultSourceRadius = 0.5f;

    // Radial subdivisions across one penumbra wedge. The wedge's angular half-width is NOT linear in
    // distance from the light — it is s*(d - d0)/(d0*d), which is zero at the occluding corner and
    // asymptotes to s/d0 — so a single quad from corner to rim would draw a wedge that is much too
    // wide close to the wall, softening the one place a shadow is genuinely sharp. Four bands puts a
    // piecewise-linear approximation through that curve. Eight was tried in the preview and is not
    // distinguishable; four is where it stopped being.
    public const int PenumbraBands = 4;

    // Rows in the baked 2-D gradient, i.e. samples across the penumbra ramp. Far coarser than the 256
    // along the falloff axis, and it can afford to be: the ramp is sampled bilinearly across a band
    // that is at least a cell wide on screen, where the falloff axis is compressing a curve that
    // rises steeply near the light.
    public const int PenumbraGradientSize = 32;

    // How far apart two consecutive polygon distances must be before the boundary between them counts
    // as a shadow EDGE rather than the polygon merely curving. Every corner produces a pair of rays
    // an epsilon apart in angle whose distances differ by the whole depth of the shadow, so the real
    // signal is enormous and this only has to clear numerical noise and grazing hits.
    public const float ShadowEdgeMinDepth = 0.25f;

    // How close in angle two consecutive rays must be to be the epsilon-pair AddCornerRay emitted
    // rather than two unrelated rays. The pair is exactly CornerRayEpsilon apart by construction and
    // the nearest thing that could be confused with it is a base ray 7.5 degrees away, so there are
    // three orders of magnitude of headroom and the exact multiplier does not matter.
    public const float ShadowEdgeMaxAngle = CornerRayEpsilon * 2.5f;

    // One straight occluding edge, in float cell coordinates. Segments have no sidedness: they block
    // light from either direction, which is what lets SilhouetteSegments merge spans contributed by
    // different cells without caring which cell they came from.
    //
    // A struct rather than a 4-tuple so the field names survive into the ray caster. OpenSkyMaskMath
    // makes the same choice for the same reason: an argument-order slip in geometry code produces a
    // mesh that renders as nothing at all, with no error and no clue.
    public readonly struct Segment
    {
        public readonly float X1;
        public readonly float Z1;
        public readonly float X2;
        public readonly float Z2;

        public Segment(float x1, float z1, float x2, float z2)
        {
            X1 = x1;
            Z1 = z1;
            X2 = x2;
            Z2 = z2;
        }
    }

    // The visibility polygon as a fan around the light: for each ray, the angle it was cast at and
    // how far it got before something stopped it (or the radius did). Angles are sorted ascending in
    // (-pi, pi], so consecutive entries — and the wrap from the last back to the first — are the
    // triangles.
    //
    // Kept as parallel arrays rather than a point list because the mesh builder wants the angle
    // again to place the ring vertices, and recovering it from a point with atan2 would reintroduce
    // exactly the rounding the sort was done to avoid.
    public readonly struct LightPolygon
    {
        public readonly float[] Angles;
        public readonly float[] Distances;
        public readonly int Count;

        public LightPolygon(float[] angles, float[] distances, int count)
        {
            Angles = angles;
            Distances = distances;
            Count = count;
        }
    }

    // Every occluding edge implied by a grid of light-blocking cells, as the OUTLINE of the blocked
    // regions with all interior edges removed and all collinear spans merged.
    //
    // `blocked` is indexed z * width + x, the way RimWorld indexes its own cell grids, so the adapter
    // hands over what it read off map.edificeGrid without transposing anything. Cell (x, z) occupies
    // the unit square [x, x+1] x [z, z+1].
    //
    // WHY THE OUTLINE RATHER THAN ONE RECTANGLE PER CELL. Both describe the same obstruction, but a
    // per-cell rectangle set has interior edges where two wall cells abut, and a ray aimed at the
    // corner where four such rectangles meet can slip BETWEEN them on a rounding error — a one-pixel
    // spike of light straight through a solid wall, appearing and disappearing as the camera moves.
    // Deleting shared edges removes that failure mode by construction rather than by epsilon.
    //
    // WHY MERGING IS SAFE EVEN ACROSS CELLS THAT FACE OPPOSITE WAYS. The merge is a point-set union
    // along one grid line. A north-facing edge of one cell and a south-facing edge of another can
    // land on the same line and be contiguous; unioning them is still exactly the same set of points,
    // and since a Segment occludes from both sides there is nothing else about them to preserve.
    // (This is why Segment has no normal — giving it one would make this merge wrong.)
    //
    // WHY A WINDOW, AND WHY IT CARRIES AN ORIGIN. Every ray is tested against every segment, so the
    // cost of a light is proportional to how much wall it is given. Handed a whole 250x250 colony,
    // one torch would be tested against every wall on the map — which is why the adapter extracts a
    // window around each light instead, and why `originX`/`originZ` exist: the segments come back in
    // WORLD cell coordinates, so the light position, the segments and the finished mesh are all in
    // one coordinate system and there is no offset left to get backwards later.
    //
    // CALLER CONTRACT: the window must be padded to at least the light's radius plus one cell.
    // Cells outside it count as unblocked, so a wall running out of the window gains a spurious end
    // cap on the boundary — harmless only because every ray has already been clamped to the radius
    // before it could reach one.
    public static Segment[] SilhouetteSegments(bool[] blocked, int width, int height, int originX, int originZ)
    {
        List<Segment> segments = new List<Segment>();

        if (blocked == null || width <= 0 || height <= 0)
            return segments.ToArray();

        // Horizontal edges live on lines z = 0..height and span [x, x+1]; vertical edges live on
        // lines x = 0..width and span [z, z+1]. Marking them into flat grids first is what makes the
        // run merge below a simple scan instead of a sort.
        bool[] horizontal = new bool[(height + 1) * width];
        bool[] vertical = new bool[height * (width + 1)];

        MarkExposedEdges(blocked, width, height, horizontal, vertical);
        MergeHorizontalRuns(horizontal, width, height, originX, originZ, segments);
        MergeVerticalRuns(vertical, width, height, originX, originZ, segments);

        return segments.ToArray();
    }

    // An edge is exposed when the cell across it is not itself a blocker. Off-window neighbours count
    // as open, per the caller contract above.
    private static void MarkExposedEdges(bool[] blocked, int width, int height, bool[] horizontal, bool[] vertical)
    {
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                if (blocked[z * width + x])
                    MarkCellEdges(blocked, width, height, x, z, horizontal, vertical);
            }
        }
    }

    private static void MarkCellEdges(
        bool[] blocked, int width, int height, int x, int z, bool[] horizontal, bool[] vertical)
    {
        if (!IsBlocked(blocked, width, height, x, z - 1))
            horizontal[z * width + x] = true;

        if (!IsBlocked(blocked, width, height, x, z + 1))
            horizontal[(z + 1) * width + x] = true;

        if (!IsBlocked(blocked, width, height, x - 1, z))
            vertical[z * (width + 1) + x] = true;

        if (!IsBlocked(blocked, width, height, x + 1, z))
            vertical[z * (width + 1) + x + 1] = true;
    }

    private static bool IsBlocked(bool[] blocked, int width, int height, int x, int z)
    {
        bool inside = x >= 0 && x < width && z >= 0 && z < height;
        return inside && blocked[z * width + x];
    }

    private static void MergeHorizontalRuns(
        bool[] horizontal, int width, int height, int originX, int originZ, List<Segment> segments)
    {
        for (int z = 0; z <= height; z++)
        {
            int runStart = -1;

            for (int x = 0; x <= width; x++)
            {
                bool set = x < width && horizontal[z * width + x];
                bool opening = set && runStart < 0;
                bool closing = !set && runStart >= 0;

                if (opening)
                    runStart = x;

                if (closing)
                {
                    segments.Add(new Segment(originX + runStart, originZ + z, originX + x, originZ + z));
                    runStart = -1;
                }
            }
        }
    }

    private static void MergeVerticalRuns(
        bool[] vertical, int width, int height, int originX, int originZ, List<Segment> segments)
    {
        for (int x = 0; x <= width; x++)
        {
            int runStart = -1;

            for (int z = 0; z <= height; z++)
            {
                bool set = z < height && vertical[z * (width + 1) + x];
                bool opening = set && runStart < 0;
                bool closing = !set && runStart >= 0;

                if (opening)
                    runStart = z;

                if (closing)
                {
                    segments.Add(new Segment(originX + x, originZ + runStart, originX + x, originZ + z));
                    runStart = -1;
                }
            }
        }
    }

    // The visibility polygon for one light. `lightX`/`lightZ` are in the same float cell coordinates
    // the segments use — for a glower on cell (x, z) that is (x + 0.5, z + 0.5), the cell centre.
    //
    // Three rays per corner plus a base ring, all clamped to `radius`, sorted by angle. See
    // CornerRayEpsilon for why three and DefaultBaseRayCount for why the ring exists at all.
    public static LightPolygon Build(
        float lightX, float lightZ, float radius, Segment[] segments, int baseRayCount)
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
            float distance = CastRay(lightX, lightZ, angle, radius, segments);

            if (!IsRedundant(outAngles, outDistances, count, angle, distance))
            {
                outAngles[count] = angle;
                outDistances[count] = distance;
                count++;
            }
        }

        return new LightPolygon(outAngles, outDistances, count);
    }

    // A ray is redundant only when BOTH its angle and its distance repeat the previous one. Dropping
    // on angle alone would delete the shadow: the two rays either side of a corner sit at almost
    // exactly the same angle and differ enormously in distance, and that difference IS the edge.
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

    private static void AddCornerRays(List<float> angles, float lightX, float lightZ, Segment[] segments)
    {
        if (segments == null)
            return;

        for (int i = 0; i < segments.Length; i++)
        {
            AddCornerRay(angles, lightX, lightZ, segments[i].X1, segments[i].Z1);
            AddCornerRay(angles, lightX, lightZ, segments[i].X2, segments[i].Z2);
        }
    }

    private static void AddCornerRay(List<float> angles, float lightX, float lightZ, float cornerX, float cornerZ)
    {
        float angle = (float)Math.Atan2(cornerZ - lightZ, cornerX - lightX);
        angles.Add(angle - CornerRayEpsilon);
        angles.Add(angle);
        angles.Add(angle + CornerRayEpsilon);
    }

    // Distance to the nearest segment along one ray, or `radius` if it reaches that far unobstructed.
    public static float CastRay(float lightX, float lightZ, float angle, float radius, Segment[] segments)
    {
        float dirX = (float)Math.Cos(angle);
        float dirZ = (float)Math.Sin(angle);
        float nearest = radius;

        if (segments == null)
            return nearest;

        for (int i = 0; i < segments.Length; i++)
        {
            float hit = RaySegmentDistance(lightX, lightZ, dirX, dirZ, segments[i]);
            bool closer = hit >= 0f && hit < nearest;

            if (closer)
                nearest = hit;
        }

        return nearest;
    }

    // Solves L + t*D = A + u*S for t >= 0 and u in [0, 1]. Returns -1 when the ray misses, which
    // includes the parallel case (a ray running exactly along a wall face is not stopped by it).
    private static float RaySegmentDistance(float lightX, float lightZ, float dirX, float dirZ, Segment segment)
    {
        float sx = segment.X2 - segment.X1;
        float sz = segment.Z2 - segment.Z1;
        float det = sx * dirZ - dirX * sz;

        if (Math.Abs(det) < 1e-12f)
            return -1f;

        float wx = segment.X1 - lightX;
        float wz = segment.Z1 - lightZ;

        float t = (sx * wz - wx * sz) / det;
        float u = (dirX * wz - wx * dirZ) / det;

        bool hits = t >= 0f && u >= 0f && u <= 1f;
        return hits ? t : -1f;
    }

    // Vanilla's own falloff curve, evaluated at a EUCLIDEAN distance rather than the geodesic one the
    // flood accumulates. Keeping the curve is deliberate: a lamp should read at about the brightness
    // its glowRadius has always given it, so that what changed on screen is unmistakably the SHAPE of
    // the lit region and not somebody quietly turning the lights up.
    //
    // The consequence, which belongs in the release notes rather than being discovered: cells vanilla
    // lit by a path bending around a corner now get nothing at all, so indirectly-lit rooms are
    // genuinely darker than they were. That is the feature, and it is also the thing most likely to
    // need a compensation knob before this is comfortable to play with.
    public static float Falloff(float distance, float radius)
    {
        if (radius <= 0f || distance > radius)
            return 0f;

        float clamped = Math.Max(distance, MinFalloffDistance);
        float linear = 1f - clamped / radius;
        float inverseSquare = 1f / (clamped * clamped);
        float mixed = linear + InverseSquareWeight * (inverseSquare - linear);

        return Clamp01(mixed);
    }

    // The drawable mesh for one light: a triangle fan from the light out to the visibility polygon's
    // boundary, with a per-vertex radial coordinate that the draw turns into brightness.
    //
    // WHY A RADIAL UV RATHER THAN A PER-VERTEX COLOUR. The pass has to be additive, and
    // CloudUnderlightOverlay's header records the finding that settles this: nothing in this codebase
    // has ever asked ShaderDatabase.MoteGlow to honour a vertex colour, while §11a's aurora and §23b
    // both put real structure through it as a TEXTURE. So brightness travels as a texture coordinate
    // and the falloff curve is baked into a 1-D gradient — the route already known to work here,
    // rather than the one that might.
    //
    // It is also the better geometry, which is a happy accident rather than the reason. U is
    // distance/radius, and distance along a ray is linear in position, so the GPU's own interpolation
    // reproduces the falloff curve EXACTLY between the light and the boundary — where subdividing the
    // fan into concentric rings and interpolating brightness between them only ever approximated it,
    // at six times the vertices. The residual error is across a wedge rather than along one: a point
    // on the chord between two rays 7.5 degrees apart is 0.2% nearer the light than its interpolated
    // U claims, which is a fifth of one step of a 256-entry gradient.
    //
    // Layout: vertex 0 is the apex at the light (U = 0), then one boundary vertex per ray in polygon
    // order, so ray i owns vertex i + 1.
    public readonly struct LightMesh
    {
        public readonly float[] X;
        public readonly float[] Z;

        // Distance from the light as a fraction of the radius, in [0, 1]. The draw looks this up in
        // the baked falloff gradient — see FalloffGradient.
        public readonly float[] U;

        // How far across a soft shadow edge this vertex sits: 0 fully lit, 1 fully occluded. Zero on
        // every vertex of the fan itself, so a mesh built with no source radius carries an all-zero V
        // and samples the gradient's first row — which is the falloff curve unmodified, i.e. exactly
        // the 1-D texture the hard-edged version sampled. That is what lets the soft edge be switched
        // off without a second code path.
        public readonly float[] V;

        public readonly int[] Triangles;
        public readonly int VertexCount;

        // How many of Triangles' entries belong to the visibility fan, before the penumbra wedges
        // that follow it. The fan alone tiles the polygon exactly once, and the offline geometry
        // tests assert precisely that — an overlap doubles the light on an additive pass — so they
        // need to be able to stop where the fan stops. The wedges deliberately lie OUTSIDE the
        // polygon, in the shadow, and would read as overlap to a test that could not tell them apart.
        public readonly int FanTriangleCount;

        public LightMesh(
            float[] x, float[] z, float[] u, float[] v, int[] triangles, int vertexCount,
            int fanTriangleCount)
        {
            X = x;
            Z = z;
            U = u;
            V = v;
            Triangles = triangles;
            VertexCount = vertexCount;
            FanTriangleCount = fanTriangleCount;
        }
    }

    public static LightMesh BuildMesh(
        float lightX, float lightZ, float radius, LightPolygon polygon, float sourceRadius)
    {
        int rays = polygon.Count;

        if (rays < 3 || radius <= 0f)
        {
            return new LightMesh(
                new float[0], new float[0], new float[0], new float[0], new int[0], 0, 0);
        }

        List<float> x = new List<float>(rays + 1);
        List<float> z = new List<float>(rays + 1);
        List<float> u = new List<float>(rays + 1);
        List<float> v = new List<float>(rays + 1);

        x.Add(lightX);
        z.Add(lightZ);
        u.Add(0f);
        v.Add(0f);

        for (int i = 0; i < rays; i++)
        {
            float reach = Math.Min(polygon.Distances[i], radius);
            float angle = polygon.Angles[i];

            x.Add(lightX + (float)Math.Cos(angle) * reach);
            z.Add(lightZ + (float)Math.Sin(angle) * reach);
            u.Add(reach / radius);
            v.Add(0f);
        }

        List<int> triangles = new List<int>(BuildTriangles(rays));
        int fanTriangleCount = triangles.Count;

        AddPenumbraWedges(lightX, lightZ, radius, polygon, sourceRadius, x, z, u, v, triangles);

        return new LightMesh(
            x.ToArray(), z.ToArray(), u.ToArray(), v.ToArray(), triangles.ToArray(), x.Count,
            fanTriangleCount);
    }

    // The soft half of the shadow edge: for every corner the polygon turned into a hard boundary, a
    // wedge extending from that boundary INTO the shadow, ramping from fully lit to fully dark.
    //
    // WHY IT ONLY EVER EXTENDS INTO THE SHADOW, never into the lit side. A real penumbra straddles
    // the geometric boundary — the lit side should dim as much as the dark side brightens. This pass
    // is additive, so it can put light into the shadow but has no way to take light back out of the
    // lit region, and the alternative (rebuilding the fan so its boundary sits at the umbra instead)
    // would make every shadow WIDER. §27's standing risk is that indirectly-lit rooms come out
    // uncomfortably dark, so of the two available errors — a soft edge that reaches half a band too
    // far into the shadow, or one that eats half a band out of the light — the first is the one that
    // moves in the safe direction. The visible result is the same softening either way.
    //
    // THE SHAPE. With the source a disc of radius s and the occluding corner at distance d0, similar
    // triangles put the penumbra's width at distance d past the light at s*(d - d0)/d0, so its
    // ANGULAR half-width is s*(d - d0)/(d0*d): zero at the corner, asymptotic to s/d0 far away. A
    // wedge of constant angular width — which is what a single triangle fanned from the light would
    // give — is wrong by a full source width at every distance, and wrong in the worst place, since
    // it softens the shadow right where it touches the wall and is genuinely sharp. Hence bands.
    private static void AddPenumbraWedges(
        float lightX, float lightZ, float radius, LightPolygon polygon, float sourceRadius,
        List<float> x, List<float> z, List<float> u, List<float> v, List<int> triangles)
    {
        if (sourceRadius <= 0f)
            return;

        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;

            if (IsShadowEdge(polygon, i, next))
                AddPenumbraWedge(lightX, lightZ, radius, polygon, i, next, sourceRadius, x, z, u, v, triangles);
        }
    }

    // A boundary is a shadow edge when consecutive rays sit at essentially the same angle and reach
    // wildly different distances — which is exactly the signature AddCornerRay leaves behind, one ray
    // stopping on the corner and its neighbour an epsilon away slipping past it. Both halves of the
    // test are load-bearing: the angle alone also matches the pair on the SAME side of a corner
    // (whose distances agree, so there is no edge to soften), and the distance alone also matches two
    // base rays a full 7.5 degrees apart straddling a wall, where the polygon is genuinely that shape
    // and softening it would blur a wall face rather than a shadow.
    private static bool IsShadowEdge(LightPolygon polygon, int i, int next)
    {
        float angleGap = Math.Abs(NormaliseAngle(polygon.Angles[next] - polygon.Angles[i]));
        float depth = Math.Abs(polygon.Distances[next] - polygon.Distances[i]);

        return angleGap <= ShadowEdgeMaxAngle && depth >= ShadowEdgeMinDepth;
    }

    private static void AddPenumbraWedge(
        float lightX, float lightZ, float radius, LightPolygon polygon, int i, int next,
        float sourceRadius, List<float> x, List<float> z, List<float> u, List<float> v,
        List<int> triangles)
    {
        bool farIsNext = polygon.Distances[next] > polygon.Distances[i];
        int far = farIsNext ? next : i;
        int near = farIsNext ? i : next;

        float cornerDistance = Math.Min(polygon.Distances[near], radius);
        float reach = Math.Min(polygon.Distances[far], radius);

        // Guard the corner distance the same way Falloff guards its own: the angular width divides by
        // it, and a light sitting inside the cell it is casting from would otherwise open a wedge
        // wider than the whole shadow. Vanilla never evaluates its curve closer than one cell either.
        float d0 = Math.Max(cornerDistance, MinFalloffDistance);

        if (reach <= d0)
            return;

        // The wedge grows away from the lit side, so its direction is whichever way the near (blocked)
        // ray lies from the far (unblocked) one. Taken from the ray angles rather than assumed from
        // index order, because the wrap from the polygon's last entry to its first reverses that.
        float baseAngle = polygon.Angles[far];
        float sign = Math.Sign(NormaliseAngle(polygon.Angles[near] - baseAngle));

        if (sign == 0f)
            return;

        int firstVertex = x.Count;

        for (int band = 0; band <= PenumbraBands; band++)
        {
            float distance = d0 + (reach - d0) * band / PenumbraBands;
            float spread = sign * PenumbraHalfWidth(distance, d0, sourceRadius);

            AddWedgeVertex(lightX, lightZ, radius, baseAngle, distance, 0f, x, z, u, v);
            AddWedgeVertex(lightX, lightZ, radius, baseAngle + spread, distance, 1f, x, z, u, v);
        }

        AddWedgeTriangles(firstVertex, sign > 0f, triangles);
    }

    // The angular half-width of the penumbra at `distance`, for a source of radius `sourceRadius`
    // whose light is clipped by a corner at `cornerDistance`. Zero at the corner by construction, so
    // the innermost band is degenerate and the wedge comes to a point exactly where the shadow does.
    public static float PenumbraHalfWidth(float distance, float cornerDistance, float sourceRadius)
    {
        if (distance <= cornerDistance || cornerDistance <= 0f)
            return 0f;

        return sourceRadius * (distance - cornerDistance) / (cornerDistance * distance);
    }

    private static void AddWedgeVertex(
        float lightX, float lightZ, float radius, float angle, float distance, float across,
        List<float> x, List<float> z, List<float> u, List<float> v)
    {
        x.Add(lightX + (float)Math.Cos(angle) * distance);
        z.Add(lightZ + (float)Math.Sin(angle) * distance);
        u.Add(Clamp01(distance / radius));
        v.Add(across);
    }

    // Two triangles per band, wound clockwise in world XZ to match the fan — see BuildTriangles for
    // why that matters more than it looks. Which of the two orderings is clockwise depends on which
    // way the wedge opened, hence the flag rather than a fixed order.
    private static void AddWedgeTriangles(int firstVertex, bool positiveSpread, List<int> triangles)
    {
        for (int band = 0; band < PenumbraBands; band++)
        {
            int innerNear = firstVertex + band * 2;
            int outerNear = innerNear + 1;
            int innerFar = innerNear + 2;
            int outerFar = innerNear + 3;

            if (positiveSpread)
            {
                triangles.Add(innerNear);
                triangles.Add(outerNear);
                triangles.Add(innerFar);

                triangles.Add(outerNear);
                triangles.Add(outerFar);
                triangles.Add(innerFar);
            }
            else
            {
                triangles.Add(innerNear);
                triangles.Add(innerFar);
                triangles.Add(outerNear);

                triangles.Add(outerNear);
                triangles.Add(innerFar);
                triangles.Add(outerFar);
            }
        }
    }

    // Wrap a raw angle difference into (-pi, pi]. Only the polygon's last-to-first boundary needs it,
    // where the difference comes out as nearly -2pi, but the shadow-edge test is a magnitude
    // comparison and would read that wrap as the largest angular gap on the polygon rather than the
    // smallest.
    private static float NormaliseAngle(float angle)
    {
        while (angle > Math.PI)
            angle -= (float)(2.0 * Math.PI);

        while (angle <= -Math.PI)
            angle += (float)(2.0 * Math.PI);

        return angle;
    }

    // WINDING. Every face is emitted so its vertices run CLOCKWISE in world XZ, which is what faces
    // RimWorld's top-down camera. A counter-clockwise face is back-facing and is culled, and the
    // failure mode is that absolutely nothing draws while every numeric probe still reports healthy
    // geometry — §17's sun-shaft branch and OpenSkyMask both record paying for this once. The
    // polygon's angles ascend counter-clockwise, so the fan is emitted in reverse to come out
    // clockwise: that is why `next` is the middle index rather than the last.
    private static int[] BuildTriangles(int rays)
    {
        int[] triangles = new int[rays * 3];

        for (int i = 0; i < rays; i++)
        {
            int next = (i + 1) % rays;

            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = next + 1;
            triangles[i * 3 + 2] = i + 1;
        }

        return triangles;
    }

    // The falloff curve baked into a 1-D gradient, sampled from the light out to the radius, as bytes
    // ready to become a texture.
    //
    // WHY IT IS PER RADIUS. Falloff is not a function of normalised distance alone. Its linear term
    // is, but its inverse-square term is 1/(u*radius)^2, so a radius-6 campfire and a radius-24 sun
    // lamp have genuinely different curve shapes and cannot share a gradient. There are only a handful
    // of distinct radii on any map, so the caller caches these rather than rebaking them per light.
    public static byte[] FalloffGradient(float radius, int size)
    {
        int count = Math.Max(size, 2);
        byte[] gradient = new byte[count];

        for (int i = 0; i < count; i++)
        {
            float distance = radius * i / (count - 1);
            gradient[i] = (byte)Math.Round(Falloff(distance, radius) * 255f);
        }

        return gradient;
    }

    // How much of the source a receiver can still see, `across` of the way through the penumbra:
    // 1 at the lit edge, 0 at the dark one. This is the ramp's SHAPE, and it is not a straight line.
    //
    // Sliding a straight occluding edge across a disc does not uncover it evenly — it uncovers a
    // circular segment, whose area is arccos(p) - p*sqrt(1 - p^2) over pi for an edge p of the way
    // out from the centre of a unit disc. That is an S-curve: slow at both limbs, steepest across the
    // middle. A linear ramp instead leaves a visible crease at each end of the band, where the
    // gradient meets flat light or flat shadow at an angle rather than tangentially, and reads as two
    // faint extra edges in place of the one hard edge it was supposed to remove.
    public static float PenumbraVisibleFraction(float across)
    {
        float p = 2f * Clamp01(across) - 1f;
        float area = (float)(Math.Acos(p) - p * Math.Sqrt(Math.Max(1.0 - p * p, 0.0)));

        return Clamp01(area / (float)Math.PI);
    }

    // The falloff curve and the penumbra ramp baked together into one 2-D gradient, row-major with U
    // (distance) along the row and V (across the soft edge) down the columns, as bytes ready to
    // become a texture.
    //
    // WHY ONE TEXTURE RATHER THAN TWO PASSES OR A SHADER. The product falloff(u) * ramp(v) is
    // separable, so a single bilinear sample reproduces it exactly — there is nothing a second pass
    // or a custom fragment program could compute here that the texture does not already carry. That
    // matters beyond tidiness: a shader would mean shipping a compiled AssetBundle per platform, and
    // this repo ships no binary assets (see §11a). The soft edge does not need one.
    //
    // Row 0 is the falloff curve alone, which is what every vertex of the fan samples. So a mesh
    // built with no source radius draws exactly what the hard-edged version drew, through the same
    // texture, with no branch anywhere — the invariant PenumbraGradientFirstRowIsTheFalloffCurve
    // pins offline.
    public static byte[] PenumbraGradient(float radius, int width, int height)
    {
        int columns = Math.Max(width, 2);
        int rows = Math.Max(height, 2);
        byte[] gradient = new byte[columns * rows];

        byte[] falloff = FalloffGradient(radius, columns);

        for (int row = 0; row < rows; row++)
        {
            float visible = PenumbraVisibleFraction((float)row / (rows - 1));

            for (int column = 0; column < columns; column++)
                gradient[row * columns + column] = (byte)Math.Round(falloff[column] * visible);
        }

        return gradient;
    }

    // How far a light's colour has to be scaled down so its brightest channel lands at 1.
    //
    // Vanilla's CompProperties_Glower.glowColor is a ColorInt scaled by 1.45, so the default white
    // lamp arrives here as (1.45, 1.45, 1.45). Vanilla gets away with that because its flood ends in
    // ProjectToColor32, which clamps per channel — the overbright simply saturates. An additive draw
    // has no such clamp and would blow the core out to a flat disc, and clamping per channel here
    // would do worse: it would clamp a warm torch's red before its blue and quietly desaturate the
    // light toward white exactly where it is brightest. Scaling by the peak preserves the hue and
    // moves only the level.
    public static float PeakScale(float r, float g, float b)
    {
        float peak = Math.Max(r, Math.Max(g, b));
        return peak > 1f ? 1f / peak : 1f;
    }

    // Overall level of the additive pass, before distance and daylight are applied.
    //
    // WHY IT IS NOT 1. Vanilla's artificial light is composited UNDER the sky's multiply and is
    // additionally capped: GlowGrid.GroundGlowAt clamps ordinary artificial light to 0.5 and only
    // lets a light past that when it is inside its own overlightRadius. An additive pass has neither
    // the compositing nor the cap, so at full strength a torch delivers visibly more light than the
    // same torch does in vanilla — measured on the first live A/B, where a 14-cell room went from
    // dim to uniformly washed out.
    //
    // WHY IT IS NOT 0.5 EITHER, WHICH IS WHAT IT WAS. Anchoring on vanilla's own GroundGlowAt cap
    // was an argument, not a measurement, and it came out about 3 L* too bright: a lit room read
    // mean L* 17.09 against vanilla's 14.02 on the same scene, roughly a fifth more light. Nothing
    // could see that until the vanilla arm was captured alongside, because every A/B until then had
    // §27 in BOTH of its frames. Solved for directly instead — the additive term is linear in this
    // constant, so with the room's ambient floor measured from the darkest fifth of the shadowed
    // frame, 0.5 * (vanilla's contribution / ours) lands on 0.3534, and 0.35 predicts L* 13.94
    // against vanilla's 14.02. §27 is a change of SHAPE, not a change of how bright lamps are, and
    // this is the constant that has to hold that line.
    //
    // Re-measure rather than re-derive if the falloff curve, PeakScale or the suppression half ever
    // move: this is a fitted value and its inputs are all upstream of it.
    public const float DefaultStrength = 0.35f;

    // How much of vanilla's own flood survives underneath §27, as a fraction — the CROSSFADE between
    // the two lighting models rather than a choice of one.
    //
    // WHY THIS EXISTS. Suppressing vanilla outright is what lets a shadow actually reach dark, and it
    // is also §27's most dangerous property: anything the polygons do not know about goes BLACK
    // rather than merely unimproved, and a room lit only by light that bent around a corner loses all
    // of it. Keeping vanilla's flood at a fraction turns both of those from cliffs into slopes. The
    // shadow is no longer black, it is dimmer; the room §27 cannot see is no longer unlit, it is
    // dimmer; and neither depends on §27 having a complete picture of what emits light.
    //
    // WHY IT COMPENSATES RATHER THAN ADDS. The naive version of this — leave vanilla alone and draw
    // our polygons over it — is epic #145's rejected option 1, and the measured reason it fails is
    // that it SUMS two complete lighting models and lands 6 L* above vanilla. Scaling our own
    // contribution by (1 - floor) makes the floor a redistribution instead: at 0 this is §27 exactly,
    // at 1 it is vanilla exactly, and in between the overall level barely moves while the SHAPE
    // crossfades from one model to the other. That is the property that makes it a usable knob rather
    // than a brightness control with a shape side effect.
    //
    // NOT A MAX, WHICH IS WHAT IT WANTS TO BE. The right composition is max(vanilla, ours) per cell —
    // vanilla's falloff runs on GEODESIC distance, so in a beam through a doorway its light has
    // travelled further and arrived dimmer than our straight-line value, and a max would take our
    // beam exactly where we have something to say and vanilla's floor everywhere we do not. It needs
    // a per-vertex "how much did vanilla deliver here" channel that MoteGlow has no way to carry, so
    // it is a shader away rather than an edit away. See DESIGN.md §27.
    public const float DefaultVanillaFloor = 0.5f;

    // Our additive contribution once the crossfade has taken its share. Kept here rather than inline
    // in the draw so the offline tests can pin that floor 0 and floor 1 are exactly §27 and exactly
    // nothing, with no arithmetic drift at the endpoints.
    public static float BlendedStrength(float strength, float vanillaFloor)
    {
        return strength * (1f - Clamp01(vanillaFloor));
    }

    // Vanilla's own light channel, scaled by the crossfade. Rounds rather than truncates: these are
    // bytes, and truncation biases every channel down by half a level, which across a whole lighting
    // overlay reads as the floor being dimmer than it was asked to be.
    public static byte FlooredChannel(byte channel, float vanillaFloor)
    {
        return (byte)Math.Round(channel * Clamp01(vanillaFloor));
    }

    // How much of a light's contribution survives the daylight around it.
    //
    // This exists because of what §27 changed about the draw. Vanilla paints artificial light into
    // the lighting overlay's vertex colours, where the sky's own multiply swamps it — at noon a lamp
    // contributes nothing a player can see, for free, as a side effect of the compositing. Ours is an
    // ADDITIVE pass sitting above that multiply, so with no attenuation a torch at midday would glow
    // brighter than it does at midnight. Fading on the ratio of the light to the sky it competes with
    // is the same "brightness ratio, not a switch" conclusion issues #4 and #48 both reached.
    public static float DaylightScale(float curSkyGlow)
    {
        return Clamp01(1f - Clamp01(curSkyGlow));
    }

    // What the additive pass delivers when it is riding ON TOP of §27 phase 3's mask rather than
    // over a suppressed vanilla — the "combination" arm.
    //
    // WHY A BEAM IS NEEDED AT ALL ON TOP OF THE MASK. The mask can only subtract, so the light
    // through a doorway can never exceed what vanilla put there, and the cells just past a one-cell
    // gap are only PARTLY visible and lose their unseen share — so the beam comes out dimmer than
    // vanilla's. Measured: doorway beam 13.34 L* against vanilla's 15.54. The mask has §27's shadows
    // and none of its beam; this is the half that puts the beam back.
    //
    // WHY IT DOES NOT SUM THE WAY THE MIXED CASE DID. Epic #145's rejected option 1 drew our full
    // model over vanilla's full model and landed 6 L* bright. This draws over a vanilla that has
    // already had the bent light REMOVED, so the two are not two complete models: what is underneath
    // is vanilla restricted to the cells we can see, and what goes on top is a fraction of the same
    // shape. Their sum is (V + k*O) * lit, which with O ~ V is just vanilla scaled by (1 + k) inside
    // the lit region and zero outside it — the shape §27 wanted, expressed on vanilla's own levels.
    //
    // 0.175 is DefaultStrength * (1 - DefaultVanillaFloor), i.e. exactly what the crossfade already
    // delivers on top of the half of vanilla it keeps. Reusing that number rather than picking a new
    // one means the beam's lift is a quantity already lived with rather than a fresh guess, and it
    // makes the combination directly comparable to the crossfade it is trying to beat.
    public const float MaskBeamStrength = DefaultStrength * (1f - DefaultVanillaFloor);

    // How many samples per axis the cell-coverage test takes. Four samples over a cell is enough to
    // resolve the quarter-cell steps the lighting overlay's own bilinear interpolation can express,
    // and a finer grid would be measuring a boundary the mesh cannot represent.
    public const int DefaultCoverageSamples = 2;

    // How far the visibility polygon reaches at a given angle.
    //
    // The polygon is stored as parallel sorted arrays rather than as points, which makes this a
    // binary search plus one lerp instead of a walk. That matters because §27 phase 3 asks this
    // question a few thousand times per section regenerate rather than once per ray.
    //
    // LERPING ACROSS A SHADOW EDGE IS CORRECT, not an approximation. AddCornerRay leaves a pair of
    // rays a CornerRayEpsilon apart with wildly different distances — one stopping on the corner and
    // one slipping past it — so interpolating between them reproduces a step, which is what a hard
    // shadow boundary is. The same lerp across two ordinary base rays 7.5 degrees apart reproduces
    // the chord the mesh actually draws, so this answers "what does the polygon look like here"
    // rather than "what would a fresh raycast say", and those differ by up to 0.2% near a wall.
    public static float BoundaryDistanceAt(LightPolygon polygon, float angle)
    {
        if (polygon.Count == 0)
            return 0f;

        if (polygon.Count == 1)
            return polygon.Distances[0];

        int last = polygon.Count - 1;

        // Outside the stored range the two neighbours are the last ray and the first one, with the
        // seam at +-pi between them. Handling it as its own case rather than by normalising every
        // angle keeps the common path a plain search.
        if (angle <= polygon.Angles[0] || angle >= polygon.Angles[last])
            return WrapBoundary(polygon, angle, last);

        int lo = 0;
        int hi = last;

        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;

            if (polygon.Angles[mid] <= angle)
                lo = mid;
            else
                hi = mid;
        }

        return Lerp(
            polygon.Angles[lo], polygon.Distances[lo],
            polygon.Angles[hi], polygon.Distances[hi], angle);
    }

    private static float WrapBoundary(LightPolygon polygon, float angle, int last)
    {
        float span = (float)(2.0 * Math.PI) - (polygon.Angles[last] - polygon.Angles[0]);

        if (span <= 0f)
            return polygon.Distances[0];

        // Measure the angle from the last ray, going forward through the seam. An angle below the
        // first ray has come the whole way round, which is what adding a turn expresses.
        float from = angle - polygon.Angles[last];

        if (from < 0f)
            from += (float)(2.0 * Math.PI);

        float t = from / span;
        return polygon.Distances[last] + (polygon.Distances[0] - polygon.Distances[last]) * Clamp01(t);
    }

    private static float Lerp(float x0, float y0, float x1, float y1, float x)
    {
        float dx = x1 - x0;

        if (dx <= 0f)
            return y0;

        float t = Clamp01((x - x0) / dx);
        return y0 + (y1 - y0) * t;
    }

    // What share of one map cell this light can actually see, in [0, 1].
    //
    // WHY A SHARE AND NOT A YES/NO. §27 phase 3 subtracts vanilla's own light back out of the cells
    // its polygon says are shadowed, and it does that on the lighting overlay's mesh, whose finest
    // unit is the cell. A binary test would quantise every shadow boundary to whole cells and make
    // the edge a staircase; sampling the cell and reporting the fraction lit turns the same mesh
    // into a bilinear ramp across the boundary cell instead, which is the difference between a
    // visible stair and a soft edge roughly the width of the penumbra phase 2 already draws.
    //
    // The samples are cell-CENTRED on a sub-grid rather than placed on the cell's corners: a corner
    // sample sits exactly on the boundary between two cells and on the wall faces the polygon is
    // built from, which is the one place a point-in-polygon answer is least reliable.
    public static float LitFraction(
        LightPolygon polygon, float lightX, float lightZ, int cellX, int cellZ, int samplesPerAxis)
    {
        if (polygon.Count == 0 || samplesPerAxis < 1)
            return 0f;

        int lit = 0;
        float step = 1f / samplesPerAxis;

        for (int iz = 0; iz < samplesPerAxis; iz++)
        {
            for (int ix = 0; ix < samplesPerAxis; ix++)
            {
                float x = cellX + (ix + 0.5f) * step;
                float z = cellZ + (iz + 0.5f) * step;

                if (IsLit(polygon, lightX, lightZ, x, z))
                    lit++;
            }
        }

        return (float)lit / (samplesPerAxis * samplesPerAxis);
    }

    // Whether one point is inside the visibility polygon: nearer to the light than the polygon's
    // boundary in that direction. The light's own position counts as lit, which is what the zero
    // check is for rather than a guard against atan2.
    public static bool IsLit(
        LightPolygon polygon, float lightX, float lightZ, float x, float z)
    {
        float dx = x - lightX;
        float dz = z - lightZ;
        float distance = (float)Math.Sqrt(dx * dx + dz * dz);

        if (distance <= 0f)
            return true;

        float angle = (float)Math.Atan2(dz, dx);
        return distance <= BoundaryDistanceAt(polygon, angle);
    }

    private static float Clamp01(float value)
    {
        if (value < 0f)
            return 0f;

        return value > 1f ? 1f : value;
    }
}
