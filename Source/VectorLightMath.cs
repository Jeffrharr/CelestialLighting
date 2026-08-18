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
        public readonly int[] Triangles;
        public readonly int VertexCount;

        public LightMesh(float[] x, float[] z, float[] u, int[] triangles, int vertexCount)
        {
            X = x;
            Z = z;
            U = u;
            Triangles = triangles;
            VertexCount = vertexCount;
        }
    }

    public static LightMesh BuildMesh(float lightX, float lightZ, float radius, LightPolygon polygon)
    {
        int rays = polygon.Count;

        if (rays < 3 || radius <= 0f)
            return new LightMesh(new float[0], new float[0], new float[0], new int[0], 0);

        int vertexCount = rays + 1;
        float[] x = new float[vertexCount];
        float[] z = new float[vertexCount];
        float[] u = new float[vertexCount];

        x[0] = lightX;
        z[0] = lightZ;
        u[0] = 0f;

        for (int i = 0; i < rays; i++)
        {
            float reach = Math.Min(polygon.Distances[i], radius);
            float angle = polygon.Angles[i];

            x[i + 1] = lightX + (float)Math.Cos(angle) * reach;
            z[i + 1] = lightZ + (float)Math.Sin(angle) * reach;
            u[i + 1] = reach / radius;
        }

        return new LightMesh(x, z, u, BuildTriangles(rays), vertexCount);
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
    // dim to uniformly washed out. Anchoring on vanilla's own 0.5 keeps §27 a change of SHAPE rather
    // than a change of how bright lamps are, which is what makes the A/B legible: if brightness moved
    // too, there would be no way to tell which of the two produced the difference on screen.
    public const float DefaultStrength = 0.5f;

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

    private static float Clamp01(float value)
    {
        if (value < 0f)
            return 0f;

        return value > 1f ? 1f : value;
    }
}
