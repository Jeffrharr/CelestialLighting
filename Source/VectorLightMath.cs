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

    // Stands in for a null segment array so Build has exactly one null check. Shared and immutable:
    // a zero-length array has nothing to mutate.
    private static readonly Segment[] NoSegments = new Segment[0];

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
        // Normalised to a non-null array once, here, so nothing downstream needs its own null check
        // and no call site can pass one on. The public CastRay keeps its own guard because it is
        // called directly by the tests and by nothing else in this file.
        Segment[] walls = segments ?? NoSegments;
        int segmentCount = walls.Length;

        // One atan2 per endpoint, computed ONCE and shared by the corner rays and the angular index.
        // The index is built out of exactly the angles the corner rays are already made of, so
        // computing them twice would add a transcendental per endpoint to the pass being made cheaper.
        float[] endpointAngles = EndpointAngles(lightX, lightZ, walls, segmentCount);

        // Sized up front: the ring plus three rays per endpoint is the exact final count, and the
        // cluttered case grows this list past 1,300 entries — eleven doublings and eleven copies.
        List<float> angles = new List<float>(baseRayCount + segmentCount * 6);

        AddBaseRays(angles, baseRayCount);
        AddCornerRays(angles, endpointAngles, segmentCount);

        angles.Sort();

        AngularIndex index = AngularIndex.For(endpointAngles, segmentCount);

        float[] outAngles = new float[angles.Count];
        float[] outDistances = new float[angles.Count];
        int count = 0;

        for (int i = 0; i < angles.Count; i++)
        {
            float angle = angles[i];
            float distance = index.CastRay(lightX, lightZ, angle, radius, walls);

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

    // The direction of every segment endpoint as seen from the light, endpoint 1 of segment i at
    // 2i and endpoint 2 at 2i+1. Kept as a flat array in that order because the index below walks it
    // in pairs and the corner rays walk it in sequence.
    private static float[] EndpointAngles(float lightX, float lightZ, Segment[] segments, int segmentCount)
    {
        float[] endpointAngles = new float[segmentCount * 2];

        for (int i = 0; i < segmentCount; i++)
        {
            endpointAngles[i * 2] =
                (float)Math.Atan2(segments[i].Z1 - lightZ, segments[i].X1 - lightX);
            endpointAngles[i * 2 + 1] =
                (float)Math.Atan2(segments[i].Z2 - lightZ, segments[i].X2 - lightX);
        }

        return endpointAngles;
    }

    private static void AddCornerRays(List<float> angles, float[] endpointAngles, int segmentCount)
    {
        for (int i = 0; i < segmentCount * 2; i++)
            AddCornerRay(angles, endpointAngles[i]);
    }

    private static void AddCornerRay(List<float> angles, float angle)
    {
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

    // Which segments a ray in a given direction could possibly hit, so Build stops testing every ray
    // against every wall.
    //
    // WHY THIS EXISTS. Build is QUADRATIC in the segment count and it is the whole cost of a bake.
    // Each segment contributes six rays and each ray was tested against every segment, so the work is
    // 6S^2 + 48S ray-segment solves. Measured offline across a clutter sweep (Tools/VectorLightBench),
    // the visibility polygon was 83-94% of a bake in any scene resembling a built colony, and it grew
    // as the square: 224 segments cost 1.39 ms, 480 cost 5.79 — 2.14x the walls for 4.17x the time.
    // The silhouette, coverage and mesh passes together never exceeded 0.4 ms in any scene.
    //
    // WHAT MAKES THE CULL EXACT RATHER THAN APPROXIMATE. A segment and a light not standing on it
    // subtend a single angular interval of less than pi. A ray can hit the segment only if its
    // direction lies inside that interval, because `u` in RaySegmentDistance is outside [0, 1] for
    // every direction outside it — so a segment culled here would have returned "miss" anyway. This
    // does not approximate the polygon, it declines to ask questions whose answer is already known.
    // Nothing about the OUTPUT changes, which is why the offline test asserts bit-for-bit equality
    // against a brute-force oracle rather than equality within a tolerance.
    //
    // The light can never stand on a segment in practice — emitters sit at cell centres (x + 0.5) and
    // every segment lies on an integer grid line, including door leaves — but a span of pi or more is
    // handled by putting the segment in every bucket rather than by trusting that.
    private readonly struct AngularIndex
    {
        // A power of two so the wrap is a mask rather than a modulo. 64 divides the circle into
        // 5.6-degree slices, which is finer than the base ring's 7.5 and about the angle a one-cell
        // wall subtends from across a radius-14 room — so a typical segment lands in one to three
        // buckets and a bucket holds a handful of segments rather than all of them.
        private const int Buckets = 64;
        private const int BucketMask = Buckets - 1;

        // Below this the index costs more than it saves: two array allocations and a counting sort
        // to avoid a loop that is already only a few hundred solves. Measured rather than guessed —
        // an eight-segment room bakes in 0.008 ms, which is three orders of magnitude under the case
        // this exists for, so the threshold's only job is to not make the cheap case worse.
        private const int MinSegments = 24;

        // CSR layout: bucket b owns items[starts[b] .. starts[b + 1]). Null when the segment count is
        // under MinSegments, which CastRay reads as "test everything".
        private readonly int[] starts;
        private readonly int[] items;

        private AngularIndex(int[] starts, int[] items)
        {
            this.starts = starts;
            this.items = items;
        }

        public static AngularIndex For(float[] endpointAngles, int segmentCount)
        {
            if (segmentCount < MinSegments)
                return new AngularIndex(null, null);

            // TWO ALLOCATIONS, and the count is deliberate rather than incidental. The obvious
            // counting sort wants four more — per-segment arc bounds, and a per-bucket write cursor —
            // and the first cut here had them. On this call rate that garbage is not free: the bench
            // showed a SECOND sweep of untouched stages (coverage, mesh) slowing by 60%, which is
            // this method's litter being collected during somebody else's measurement. In Mono, with
            // a bake per lamp per door step, it would be collected during a frame.
            //
            // So the arc bounds are recomputed in the fill pass instead of stored — BucketRange is
            // comparisons and one divide, no transcendentals, and it was already paid for by the
            // atan2 the caller shares — and the cursor is `starts` itself, walked forward and then
            // shifted back into place.
            int[] starts = new int[Buckets + 1];

            for (int i = 0; i < segmentCount; i++)
            {
                BucketRange(endpointAngles[i * 2], endpointAngles[i * 2 + 1], out int first, out int span);

                for (int k = 0; k < span; k++)
                    starts[((first + k) & BucketMask) + 1]++;
            }

            for (int b = 0; b < Buckets; b++)
                starts[b + 1] += starts[b];

            int[] items = new int[starts[Buckets]];

            for (int i = 0; i < segmentCount; i++)
            {
                BucketRange(endpointAngles[i * 2], endpointAngles[i * 2 + 1], out int first, out int span);

                for (int k = 0; k < span; k++)
                {
                    int bucket = (first + k) & BucketMask;
                    items[starts[bucket]] = i;
                    starts[bucket]++;
                }
            }

            // The fill left every start one bucket advanced — starts[b] now holds where bucket b
            // ENDS, which is where bucket b+1 begins. Shifting right restores the CSR invariant
            // without a second counting pass.
            for (int b = Buckets; b > 0; b--)
                starts[b] = starts[b - 1];

            starts[0] = 0;

            return new AngularIndex(starts, items);
        }

        // The bucket range one segment's arc covers, PADDED BY A BUCKET AT EACH END.
        //
        // The padding guards the one place exact arithmetic runs out. A ray is cast from cos/sin of
        // an angle, while the arc is derived from atan2 of the endpoint offsets; the two round
        // differently, so a ray aimed exactly along a corner — precisely what AddCornerRay emits,
        // three times per endpoint — can land a few ulps outside the arc it was computed from.
        //
        // IT IS KEPT DESPITE BEING UNNECESSARY HERE, which is worth writing down because the obvious
        // review note is to delete it. Removing it was tried: VectorLightBuildCullTests stays green,
        // so on this runtime no ray ever does fall outside. But that fixture runs on CoreCLR and the
        // game runs Mono, whose transcendentals are a different implementation and need not round
        // the same way — and the offline suite is structurally incapable of noticing the difference.
        // A bucket is 5.6 degrees of slack against a discrepancy measured in millionths, bought for
        // one extra bucket of solves per ray. The same fixture, made to UNDER-cover the arc, fails 11
        // of its 13 cases — so the margin is slack, not the thing holding the cull up.
        private static void BucketRange(float angleA, float angleB, out int first, out int span)
        {
            float difference = angleB - angleA;

            // Normalise into (-pi, pi] so the arc is the SHORT way round, which is the one the
            // segment actually subtends. Taking the long way would put a wall in three quarters of
            // the buckets and cull nothing.
            while (difference > (float)Math.PI)
                difference -= (float)(2.0 * Math.PI);

            while (difference <= -(float)Math.PI)
                difference += (float)(2.0 * Math.PI);

            // A span at or beyond a half turn means the light is on the segment's line, where the
            // interval stops being a single arc. Give up and test it against every ray.
            if (Math.Abs(difference) >= (float)Math.PI - 1e-6f)
            {
                first = 0;
                span = Buckets;
                return;
            }

            float start = difference >= 0f ? angleA : angleB;
            float width = Math.Abs(difference);

            int startBucket = BucketOf(start);
            int endBucket = startBucket + (int)(width / (float)(2.0 * Math.PI) * Buckets) + 1;

            first = startBucket - 1;
            span = Math.Min(endBucket - startBucket + 3, Buckets);
        }

        private static int BucketOf(float angle)
        {
            // atan2 returns [-pi, pi], so the +pi shift lands in [0, 2pi] and only the closed upper
            // end can reach Buckets. Masking rather than clamping wraps it to 0, which is the same
            // slice: +pi and -pi are one direction.
            int bucket = (int)((angle + (float)Math.PI) / (float)(2.0 * Math.PI) * Buckets);
            return bucket & BucketMask;
        }

        // Distance to the nearest segment along one ray, testing only the bucket the ray points into.
        // Identical in result to the public CastRay, which stays the brute-force version: it is the
        // oracle the offline test compares this against, and it is the one the unit tests call.
        public float CastRay(float lightX, float lightZ, float angle, float radius, Segment[] segments)
        {
            if (starts == null)
                return VectorLightMath.CastRay(lightX, lightZ, angle, radius, segments);

            float dirX = (float)Math.Cos(angle);
            float dirZ = (float)Math.Sin(angle);
            float nearest = radius;

            int bucket = BucketOf(angle);
            int to = starts[bucket + 1];

            for (int k = starts[bucket]; k < to; k++)
            {
                float hit = RaySegmentDistance(lightX, lightZ, dirX, dirZ, segments[items[k]]);
                bool closer = hit >= 0f && hit < nearest;

                if (closer)
                    nearest = hit;
            }

            return nearest;
        }
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
    public static byte[] FalloffGradient(float radius, int size) =>
        FalloffGradient(radius, size, matchSeed: false);

    // `matchSeed` bakes vanilla's own intDist = 100 seed into the curve, so the gradient answers
    // "what would vanilla's flood have delivered here along a clear straight line" rather than "what
    // does our curve give at this distance".
    //
    // WHY THAT IS A DIFFERENT CURVE AND NOT A SCALE. ComputeGlowGridsJob.PrepareFill seeds the
    // light's own cell at one cell rather than zero, so vanilla's falloff is evaluated at octile + 1
    // EVERYWHERE. Comparing our curve at d against vanilla's at d + 1 is not comparing like with
    // like: ours is brighter in every cell of every lamp, worst near the middle where the inverse
    // square term dominates. Pinned offline at 76 levels of glow one cell out from a radius-12 lamp,
    // 23 at two cells and 13 at four — a halo on every light, which §27 exists to avoid, since its
    // whole claim is that it changes WHERE light reaches and not how bright a lamp is.
    //
    // Only the max composition wants this. The stock additive pass is not subtracting vanilla from
    // anything, so for it the seed would just be a dimmer lamp for no reason.
    public static byte[] FalloffGradient(float radius, int size, bool matchSeed)
    {
        int count = Math.Max(size, 2);
        byte[] gradient = new byte[count];
        float seed = matchSeed ? VectorLightLiftMath.VanillaSeedDistance : 0f;

        for (int i = 0; i < count; i++)
        {
            float distance = radius * i / (count - 1);
            gradient[i] = (byte)Math.Round(Falloff(distance + seed, radius) * 255f);
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
    public static byte[] PenumbraGradient(float radius, int width, int height) =>
        PenumbraGradient(radius, width, height, matchSeed: false);

    public static byte[] PenumbraGradient(float radius, int width, int height, bool matchSeed)
    {
        int columns = Math.Max(width, 2);
        int rows = Math.Max(height, 2);
        byte[] gradient = new byte[columns * rows];

        byte[] falloff = FalloffGradient(radius, columns, matchSeed);

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

    // How long a pawn's shadow is, in cells, when a lamp `distance` away lights it.
    //
    // GEOMETRY, NOT TASTE. A light of height h above the floor, a caster of height t, and a
    // horizontal separation d put the shadow's tip at d * t / (h - t) — similar triangles, the same
    // relation §27's penumbra wedges already use for source size. The lamp is treated as being
    // LampHeight above the floor; closer than that and the shadow would invert through infinity,
    // which is why the denominator is clamped rather than allowed to reach zero.
    //
    // The consequence is the thing that makes it read as light rather than as decoration: length
    // grows with d, so a pawn standing under a lamp has almost no shadow and one at the edge of its
    // reach throws a long one — the same relation that makes a low sun throw long shadows, which is
    // why it looks right without anyone being told what it is doing. Vanilla's own pawn shadow
    // cannot do this at all: its direction and length come from one global _CastVect that every
    // shadow on the map shares.
    public static float PawnShadowLength(float distance, float casterHeight, float lampHeight)
    {
        return PawnShadowLength(distance, casterHeight, lampHeight, MaxPawnShadowLength);
    }

    // The same, with the cap passed in, so the shape flag's off arm can reproduce phase 4b's cap as
    // well as its heights. Overloaded rather than defaulted because a default would let a caller
    // forget the cap exists at exactly the site that has to choose one.
    public static float PawnShadowLength(
        float distance, float casterHeight, float lampHeight, float maxLength)
    {
        float headroom = lampHeight - casterHeight;

        if (headroom < MinLampHeadroom)
            headroom = MinLampHeadroom;

        float length = distance * casterHeight / headroom;

        return length > maxLength ? maxLength : length;
    }

    // How much light this lamp delivers to the pawn's cell, in the same 0-1 glow space the rest of
    // §27 works in: the lamp's own falloff, scaled by the share of the cell it can actually SEE.
    //
    // Split out of PawnShadowOpacity because it is now needed TWICE per pawn — once to accumulate
    // what every lamp together puts on that cell, and once per lamp to take that lamp's share of the
    // total. Naming it is what makes the two passes provably ask the same question rather than two
    // subtly different ones.
    public static float PawnIlluminance(float distance, float radius, float coverage)
    {
        return Falloff(distance, radius) * Clamp01(coverage);
    }

    // What fraction of the light standing on the pawn's cell this ONE lamp is responsible for.
    //
    // THE PHYSICS. Illuminance adds. A cell receives E_total from every source that reaches it, and
    // interposing a caster between it and lamp L removes E_L and nothing else — so the darkening a
    // player sees is E_L / E_total. It is a SHARE, and the shares over all the lamps lighting a cell
    // sum to at most one, which is the property the old model lacked.
    //
    // THE BUG THIS FIXES. The previous formula was this same expression with the denominator pinned
    // to 1, i.e. every lamp charged the full darkening as though it were the only light in the room.
    // That is correct when it is, and compounds absurdly when it is not: eight lamps around a pawn
    // each drew at 0.30 and left the ground at their feet 94% black — one opaque asterisk of arms —
    // because nothing in the arithmetic knew that the seven lamps NOT being blocked were still
    // lighting the ground being darkened. Under the share model those same eight draw at 0.058 each
    // and the rosette settles at 38%, which is what a room with eight lamps in it looks like.
    //
    // WHY THE DENOMINATOR IS FLOORED AT FULL ILLUMINATION rather than being the bare sum. A lone
    // lamp at half falloff delivers 0.5, and dividing by 0.5 would hand it a share of 1 — a pawn at
    // the dim rim of a single lamp's reach throwing a BLACKER shadow than one standing under it.
    // That is defensible only in a sealed darkroom, and a RimWorld map is never one: there is always
    // sky, glowing terrain, a fire, another mod's light. Flooring at one states "you cannot remove
    // more light than there is", and it buys the property that made this change safe to make at all
    // — with one lamp the result is ALGEBRAICALLY IDENTICAL to what shipped, so every existing pin
    // and every committed single-lamp capture stays valid, and dilution begins at exactly the point
    // a second lamp makes the pawn brighter than fully lit.
    public static float PawnShadowShare(float lampIlluminance, float totalIlluminance)
    {
        if (lampIlluminance <= 0f)
            return 0f;

        float total = totalIlluminance < FullIlluminance ? FullIlluminance : totalIlluminance;

        return Clamp01(lampIlluminance / total);
    }

    // How much of a shadow survives the first thing that stops the light (issue #166).
    //
    // THE BUG. Phase 4 asked the occlusion question exactly once, at the caster's own cell — "can
    // this lamp see the pawn" — and then drew the full quad with nothing checking what it CROSSED.
    // A pawn standing beside a wall threw its shadow over the wall and out the other side, into the
    // next room or across a corridor, onto ground that lamp never reached. It was more visible here
    // than in vanilla only because these shadows are long.
    //
    // THE FIX IS ONE NUMBER, because phase 3 already knows the answer. The shadow runs directly
    // away from the lamp, so it lies along a RADIAL of that lamp's visibility polygon — and
    // `BoundaryDistanceAt` gives how far that polygon reaches at a bearing in a binary search plus a
    // lerp. Everything past the boundary is a cell the lamp cannot see, which is precisely a cell
    // with no light to remove. So the tip is clamped to the boundary and the shadow stops at the
    // wall, without a raycast and without clipping the mesh.
    //
    // ALL THREE DISTANCES ARE MEASURED FROM THE LAMP, which is the only part worth being careful
    // about: `boundary` and `distance` already are, and the shadow does not start at the pawn's
    // centre but at the silhouette's trailing edge, so that has to be paid too or a shadow beside a
    // wall keeps a sliver of overhang.
    //
    // IT ALSO CLIPS AT THE LAMP'S OWN RIM, not only at walls, and that is intended rather than a
    // side effect: an unobstructed polygon's boundary IS the light's radius, so a pawn near the
    // edge of a lamp's reach no longer throws a shadow out into ground that lamp does not light.
    // The same statement covers both — a shadow exists only inside the region the lamp lights.
    public static float ClipShadowLength(
        float length, float boundaryDistance, float distance, float trailingEdge)
    {
        float room = boundaryDistance - distance - trailingEdge;

        if (room <= 0f)
            return 0f;

        return length > room ? room : length;
    }

    // How dark a pawn's shadow from this lamp is, in [0, 1].
    //
    // Two things multiply, and each is there for a reason a screenshot would otherwise ask about:
    //
    //  - This lamp's SHARE of the light on the pawn's cell, which carries the lamp's own falloff and
    //    the share of the cell it can see inside it — so a shadow fades out exactly where the light
    //    that casts it does, a pawn behind a wall casts nothing from a lamp that cannot reach it,
    //    and a pawn under six lamps gets six faint shadows rather than six full ones.
    //  - Daylight, on the same reasoning as DaylightScale: a torch at noon casts nothing anyone can
    //    see, and drawing it anyway is how an effect starts looking like a bug. Kept as a separate
    //    multiply rather than folded into the denominator as an ambient term, because glow units are
    //    perceptual rather than photometric — the sky reads 1.0 against a lamp's 0.5 where the real
    //    ratio is four orders of magnitude, so daylight has to be applied as the calibrated curve it
    //    already is instead of being allowed to compete on those numbers.
    public static float PawnShadowOpacity(
        float lampIlluminance, float totalIlluminance, float curSkyGlow)
    {
        float lit = PawnShadowShare(lampIlluminance, totalIlluminance) * DaylightScale(curSkyGlow);

        return Clamp01(lit * PawnShadowStrength);
    }

    // Which way the shadow points: directly away from the lamp, in radians, ready for a rotation
    // about Y. The mesh is baked extruded along +X, so this IS the transform — there is no
    // per-frame mesh rebuild and no per-draw shader global, which there could not be anyway:
    // Graphics.DrawMesh is deferred, so a global set between calls applies to whichever call
    // resolves last. VectorLightOverlay's header records the same trap costing §17 a branch.
    public static float PawnShadowAngleDegrees(float lightX, float lightZ, float pawnX, float pawnZ)
    {
        float dx = pawnX - lightX;
        float dz = pawnZ - lightZ;

        if (dx == 0f && dz == 0f)
            return 0f;

        // Negated because Unity's Y rotation runs clockwise from +Z while atan2 runs anticlockwise
        // from +X. Getting this wrong points every shadow at its lamp instead of away from it, which
        // looks deliberate enough to survive a glance.
        return -(float)(Math.Atan2(dz, dx) * 180.0 / Math.PI);
    }

    // Whether a pawn in this state casts a shadow at all — the POLICY half of issue #159's second
    // question, kept separate from the four live reads that feed it so the disjunction can be tested
    // without a Map.
    //
    // EVERY CLAUSE IS VANILLA'S, NOT OURS, and that is the entire argument for each. §27 renders a
    // shadow vanilla does not in exactly one place — under a roof, because Graphic_Shadow bails on
    // roofed cells and "sunlight does not get in" says nothing about a torch. These four are the
    // suppressions that have nothing to do with sunlight, so declining to honour them was not a
    // deliberate divergence the way the roof is; it was simply not asking.
    //
    //  - STANDING. PawnRenderer.RenderPawnAt only calls DrawShadowInternal when
    //    `results.posture == PawnPosture.Standing`, and RenderShadowOnlyAt repeats the test. A pawn
    //    lying in a bed or bleeding out on the floor is not a 1.2-cell-tall caster, and drawing them
    //    as one puts a full-height shadow beside a body that is visibly flat.
    //  - VISIBLE. `pawn.IsPsychologicallyInvisible()` is what sets PawnRenderFlags.Invisible, which
    //    is the other half of that same test. A shadow is the one thing that gives an invisible pawn
    //    away, so this clause is the one with actual GAMEPLAY consequence — §27 is a render-only
    //    subsystem and handing the player a tell vanilla does not is exactly the sort of thing that
    //    scope boundary exists to forbid.
    //  - NOT SWIMMING. DrawShadowInternal returns before any shadow for `Swimming ||
    //    DrawNonHumanlikeSwimmingGraphic`: the pawn is drawn part-submerged, and a full blob beside
    //    them reads as floating.
    //  - NOT FLYING. Vanilla does not suppress this one, it SUBSTITUTES — a soft circle at
    //    AltitudeLayer.Filth, offset by the flight arc, rather than the extruded footprint. §27 has
    //    no equivalent and inventing one is a different feature, so the honest answer is to draw
    //    nothing rather than to stamp a ground-caster's shadow under a pawn who is in the air.
    //
    // Written as "all four must hold" rather than four early returns in the adapter so the policy is
    // one expression that can be read at a glance and inverted in one place if it is ever wrong.
    public static bool PawnCastsShadow(bool standing, bool invisible, bool swimming, bool flying)
    {
        return standing && !invisible && !swimming && !flying;
    }

    // How far a caster's own footprint reaches along a direction, from the point the footprint is
    // centred on.
    //
    // WHY THIS IS THE WHOLE OF ISSUE #159. Vanilla describes a caster's shadow footprint as an
    // AXIS-ALIGNED RECTANGLE — `ShadowData.volume`'s x and z, centred on `DrawPos + ShadowData
    // .offset` — and then draws the shadow as that rectangle PLUS a skirt extruded from whichever
    // edge faces away from the light. So vanilla's shadow leaves the caster's silhouette, and the
    // silhouette is direction-dependent: a human's 0.3 x 0.4 blob presents 0.15 half-cells to a lamp
    // due east and 0.20 to one due south. §27's own quad ignored all of this and started at the
    // pawn's DrawPos, which is the pawn's TORSO — 0.3 cells north of where its sun shadow starts,
    // and a rectangle's worth short of the edge. Two shadows on the same pawn visibly disagreeing
    // about which part of them cast it is what the issue reported.
    //
    // The support function of a rectangle answers both halves of the fix with one call. Passed the
    // shadow direction it gives the distance from the centre to the silhouette edge — how far to
    // push the quad out so it leaves the caster rather than crossing it. Passed the PERPENDICULAR it
    // gives the silhouette's half-width — how wide the quad's base should be. That is why this takes
    // a direction rather than an angle: the caller already holds (pawn - lamp) in components, and an
    // angle convention is one more thing to get backwards between here and the transform.
    //
    // A zero-length direction means the pawn is standing on the lamp's own cell, where there is no
    // direction to be shadowed in. PawnShadowAngleDegrees resolves that same degeneracy to +X, so
    // this does too — agreeing with it matters more than the arbitrary choice does.
    public static float FootprintExtent(float halfX, float halfZ, float dirX, float dirZ)
    {
        float magnitude = (float)Math.Sqrt(dirX * dirX + dirZ * dirZ);

        if (magnitude <= 0f)
            return halfX;

        float x = dirX / magnitude;
        float z = dirZ / magnitude;

        return halfX * Math.Abs(x) + halfZ * Math.Abs(z);
    }

    // One emitter's cell coverage, baked once per polygon instead of per section.
    //
    // WHY THIS EXISTS: 239 MICROSECONDS PER SECTION. Phase 3 first asked LitFraction per cell per
    // emitter during every section regenerate, which measured 239.3 us per section against the
    // crossfade's 20.2 — 11.8x, and a 29.5 ms worst frame on a whole-map rebake. The waste was
    // structural rather than a constant factor: coverage depends only on the polygon and the cell,
    // and neither of those knows which section is being baked, so the same answer was recomputed
    // once per section that happened to overlap the emitter, and four sample points and a binary
    // search over a hundred rays were paid for each time.
    //
    // Baked here, the bake becomes an array index. The grid is rebuilt only when the polygon is —
    // i.e. when somebody builds or removes a wall in range — which is the same cadence the polygon
    // itself already had, and it costs one byte per cell of the emitter's square: 441 bytes for a
    // radius-10 lamp.
    //
    // A BYTE RATHER THAN A FLOAT, deliberately. The consumer multiplies vanilla's glow bytes by
    // (1 - coverage) and rounds, so a 1/255 quantisation of the coverage is finer than the thing it
    // is modulating and cannot be seen. It also keeps a radius-14 emitter's grid under a kilobyte.
    public static byte[] BuildCoverage(
        LightPolygon polygon, int lightCellX, int lightCellZ, int radiusCells, int samplesPerAxis)
    {
        int span = radiusCells * 2 + 1;
        byte[] grid = new byte[span * span];

        if (polygon.Count == 0 || radiusCells < 0)
            return grid;

        // A zero grid is what the loop below produces when there is nothing to sample with, because
        // LitFraction answers 0 for every cell. Returned here instead so the bounds work — which
        // divides by this — never sees it.
        if (samplesPerAxis < 1)
            return grid;

        float lightX = lightCellX + 0.5f;
        float lightZ = lightCellZ + 0.5f;

        RayExtremes(polygon, out float nearestRay, out float farthestRay);

        // The two extreme sample offsets inside a cell, placed by the SAME expression LitFraction
        // places them with. See AxisSpan for why the expression has to match rather than merely
        // agree.
        float step = 1f / samplesPerAxis;
        float firstSample = (0 + 0.5f) * step;
        float lastSample = (samplesPerAxis - 1 + 0.5f) * step;

        for (int zi = 0; zi < span; zi++)
        {
            int cellZ = lightCellZ - radiusCells + zi;

            // Hoisted per ROW: the z half of every cell in this row is the same, and the inner loop
            // runs `span` times for it.
            AxisSpan(cellZ + firstSample, cellZ + lastSample, lightZ, out float nearZ, out float farZ);

            for (int xi = 0; xi < span; xi++)
            {
                int cellX = lightCellX - radiusCells + xi;

                AxisSpan(cellX + firstSample, cellX + lastSample, lightX, out float nearX, out float farX);

                float nearest = (float)Math.Sqrt(nearX * nearX + nearZ * nearZ);
                float farthest = (float)Math.Sqrt(farX * farX + farZ * farZ);

                grid[zi * span + xi] = CoverageForCell(
                    polygon, lightX, lightZ, cellX, cellZ, samplesPerAxis,
                    nearest, farthest, nearestRay, farthestRay);
            }
        }

        return grid;
    }

    // One cell of the coverage grid, answered from the bounds where the bounds can answer it.
    //
    // WHY THIS IS NOT AN APPROXIMATION. Every value BoundaryDistanceAt can return is a convex
    // combination of two neighbouring polygon distances — it and WrapBoundary both Clamp01 their
    // interpolant, so neither can overshoot the pair it is interpolating — and therefore the polygon
    // boundary in EVERY direction lies between the nearest and farthest ray. Which makes two whole
    // classes of cell answerable by a compare:
    //
    //   - a cell whose farthest sample is nearer than the NEAREST ray is inside the polygon whatever
    //     bearing its samples turn out to be at, so it is fully lit;
    //   - a cell whose nearest sample is farther than the FARTHEST ray is outside it on the same
    //     reasoning, so it is fully unlit.
    //
    // Both are the answer LitFraction would compute, reached without the four atan2s, four sqrts and
    // four binary searches computing it costs. VectorLightCoverageBoundsTests asserts that bit for
    // bit against a transcription of the unbounded loop.
    //
    // WHAT EACH BOUND IS FOR, because they pay for two different shapes of waste the phase 6
    // decomposition found. The FARTHEST bound rejects the corners of the square the grid is stored
    // in: a radius-14 emitter spends 841 cells describing a 615-cell circle, so 27% of the grid was
    // working its way to a zero that was never in doubt. The NEAREST bound is the one that makes an
    // UNOBSTRUCTED emitter nearly free — with no ray stopped short, the nearest ray is the radius
    // itself and every cell inside the circle takes the lit path. In open ground that is most
    // emitters on the map, and it is why this subsumes the cheaper idea of skipping the grid
    // outright for such an emitter: that would have changed what CoverageAt answers at the rim,
    // where an inscribed 48-gon reads as partly shadowed, and this changes nothing anywhere.
    private static byte CoverageForCell(
        LightPolygon polygon, float lightX, float lightZ, int cellX, int cellZ, int samplesPerAxis,
        float nearestSample, float farthestSample, float nearestRay, float farthestRay)
    {
        if (farthestSample <= nearestRay)
            return 255;

        if (nearestSample > farthestRay)
            return 0;

        float lit = LitFraction(polygon, lightX, lightZ, cellX, cellZ, samplesPerAxis);

        return (byte)Math.Round(Clamp01(lit) * 255f);
    }

    // How near and how far this polygon's boundary ever gets to the light.
    //
    // The same O(count) pass IsUnobstructed already makes, and deliberately not folded into it: that
    // one asks a question about shadow and this one is a numeric range, and a caller wanting the
    // range on an obstructed emitter — which is every caller here — would have to ignore its answer.
    private static void RayExtremes(LightPolygon polygon, out float nearest, out float farthest)
    {
        nearest = polygon.Distances[0];
        farthest = polygon.Distances[0];

        for (int i = 1; i < polygon.Count; i++)
        {
            float distance = polygon.Distances[i];

            if (distance < nearest)
                nearest = distance;

            if (distance > farthest)
                farthest = distance;
        }
    }

    // The nearest and farthest one axis of a cell's sample points gets from the light.
    //
    // TAKES THE EXTREME SAMPLE COORDINATES, NOT THE CELL'S EDGES, and the caller computes them with
    // the identical expression LitFraction uses to place a sample. The bounds have to hold in FLOAT,
    // not merely in arithmetic: a bound derived from the cell edge is a different expression that
    // can round the other way by an ulp, and an upper bound that is an ulp low is a cell reported
    // fully lit that the sampler would have found partly shadowed. Sharing the expression makes the
    // endpoints bit-identical to two of the samples being bounded, and the samples between them are
    // monotonic in the loop index, so the interval provably contains all of them.
    //
    // Squaring and summing two such bounds preserves the ordering — IEEE multiply, add and sqrt are
    // all monotonic — so the distance bounds this feeds are exact in the same sense.
    private static void AxisSpan(float low, float high, float light, out float near, out float far)
    {
        float fromLow = low - light;
        float fromHigh = high - light;
        float absLow = Math.Abs(fromLow);
        float absHigh = Math.Abs(fromHigh);

        far = absLow > absHigh ? absLow : absHigh;

        // An interval straddling the light has its closest approach in the middle rather than at
        // either end. Zero is a lower bound rather than an attained sample, which is all this is
        // used for: too small a lower bound costs a cell the fast path, never a wrong answer.
        if (fromLow <= 0f && fromHigh >= 0f)
        {
            near = 0f;
            return;
        }

        near = absLow < absHigh ? absLow : absHigh;
    }

    // Coverage for one cell, or FULLY LIT for a cell outside the emitter's square.
    //
    // Outside the square is the right place to answer 255 rather than 0: the emitter delivers no
    // light there at all, so the caller has nothing to subtract either way — but 0 would mean
    // "wholly shadowed", and a caller that reached here with a non-zero glow would darken a cell the
    // emitter never lit. Erring towards subtracting nothing keeps a bug in this lookup from removing
    // somebody else's light.
    public static byte CoverageAt(
        byte[] grid, int lightCellX, int lightCellZ, int radiusCells, int cellX, int cellZ)
    {
        if (grid == null || grid.Length == 0)
            return 255;

        int span = radiusCells * 2 + 1;
        int xi = cellX - lightCellX + radiusCells;
        int zi = cellZ - lightCellZ + radiusCells;

        if (xi < 0 || zi < 0 || xi >= span || zi >= span)
            return 255;

        return grid[zi * span + xi];
    }

    // Whether nothing blocks this emitter — every ray reached the full radius.
    //
    // Worth its own pass because the answer is usually YES in open ground and it makes the whole
    // emitter free: an unobstructed lamp shadows nothing anywhere, so the bake can skip it outright
    // rather than looking a grid up cell by cell to be told 255 each time.
    //
    // ASKED OF THE POLYGON, NOT OF THE GRID, and the difference matters. A grid covers the emitter's
    // SQUARE while the polygon covers its circle, so the square's corners are outside the light
    // whatever the geometry does and an all-255 grid is a state that essentially never occurs. The
    // rim is the same story one cell in: the polygon is an inscribed 48-gon, so cells straddling the
    // radius are partly outside it and read as partly shadowed even with nothing in the way. Both
    // are discretisation, not shadow. A ray stopping short of the radius is shadow, and it is the
    // only thing here that is.
    public static bool IsUnobstructed(LightPolygon polygon, float radius)
    {
        if (polygon.Count == 0)
            return false;

        for (int i = 0; i < polygon.Count; i++)
        {
            if (polygon.Distances[i] < radius - CornerRayEpsilon)
                return false;
        }

        return true;
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
    // How far, in cells, a mesh vertex is pulled back towards the light before vanilla's glow is
    // sampled for it. See SampleTowardLight for why this is not zero. Lifted unchanged from #151.
    public const float VanillaSamplePull = 0.5f;

    // Vanilla's glow byte in the units our own falloff curve produces. Both are the same physical
    // quantity — that same Lerp(1 - d/R, 1/d^2, 0.4), evaluated on different distances — so the only
    // conversion needed is the byte scale, and the comparison is meaningful rather than a units
    // accident.
    public static float GlowUnit(byte channel)
    {
        return channel / 255f;
    }

    // Where to sample vanilla's glow for a mesh vertex: the vertex itself, pulled `pull` cells back
    // along the line to the light.
    //
    // WHY NOT AT THE VERTEX. Every boundary vertex of the visibility polygon sits ON the thing that
    // stopped the ray, which is a wall — and a wall is a light blocker, so ComputeGlowGridsJob never
    // floods it and its glow is zero. Sampling there would report that vanilla delivers NOTHING at
    // the rim of every light, so we would subtract nothing and hand back the full hard-edged render
    // in exactly the ring where the two models agree most closely. Half a cell is enough to land in
    // the last open cell instead, and is small enough not to matter anywhere else.
    //
    // The apex is unaffected — it is already at the light, and pulling it towards itself is a no-op
    // once the distance is below the pull, which the clamp below is there to make true rather than
    // to guard a division.
    public static void SampleTowardLight(
        float x, float z, float lightX, float lightZ, float pull, out float sampleX, out float sampleZ)
    {
        float dx = lightX - x;
        float dz = lightZ - z;
        float distance = (float)Math.Sqrt(dx * dx + dz * dz);

        if (distance <= pull || distance <= 0f)
        {
            sampleX = lightX;
            sampleZ = lightZ;
            return;
        }

        float step = pull / distance;
        sampleX = x + dx * step;
        sampleZ = z + dz * step;
    }

    public const float MaskBeamStrength = DefaultStrength * (1f - DefaultVanillaFloor);

    // WHY THAT NUMBER WAS TOO BRIGHT, and what this scale exists to correct. The comment above
    // justifies 0.175 as "what the crossfade already delivers", but the crossfade delivers it while
    // ALSO cutting vanilla to DefaultVanillaFloor underneath — Patch_VectorLightSuppress scales the
    // artificial RGB before we add anything. In mask mode that suppression does not run at all: the
    // mask returns early, so the lit region keeps the FULL vanilla flood and 0.175 lands on top of
    // it. The same constant is a swap in one mode and a straight lift in the other, which is exactly
    // what the comment above assumed it was not.
    //
    // The measurement was in the same PR that shipped it (#154): the lit room goes 9.61 -> 10.56 L*
    // against the crossfade's 9.72, i.e. nine times the lift, and the sum above says why — (1 + k)
    // inside the lit region, k = 0.175, so the room renders at 1.175x vanilla by construction.
    //
    // WHY A SCALE RATHER THAN A SMALLER CONSTANT. The beam and the room brightness are the SAME
    // quantity here — the polygon covers the lit region, so nothing can raise the doorway without
    // raising the room with it. Where that lands is a taste call rather than a derivation, so it is a
    // slider with the old value still reachable at 1.0, not a new number picked and frozen.
    public const float DefaultBeamStrengthScale = 0.5f;

    // The beam's additive level once the player's scale is applied. Pure so the endpoints can be
    // pinned offline: 0 must be exactly no beam (mask alone) and 1 must be exactly the pre-slider
    // MaskBeamStrength, with no arithmetic drift at either end.
    //
    // THE CROSSFADE BRANCH IS DELIBERATELY NOT SCALED BY THIS. Its level is calibrated to conserve —
    // it adds back the share of vanilla that Patch_VectorLightSuppress just took away — so scaling it
    // down would land the fallback path DIMMER than vanilla rather than merely less lifted, which is
    // a different bug rather than a milder version of this one.
    public static float MaskBeamStrengthFor(float scale)
    {
        if (scale <= 0f)
            return 0f;

        return MaskBeamStrength * (scale >= 1f ? 1f : scale);
    }

    // How high a lamp sits above the floor, in cells, for shadow-length purposes.
    //
    // Raised from 2.4 to 3.2 when the geometry was first shortened, and now LOWERED TO 2.4 again.
    // Length is `d * t / (h - t)`, so this and the caster's height are the whole of how dramatic
    // these get.
    //
    // The history matters because the number has been here before and does not mean what it meant
    // then. At 2.4 against the INVENTED 1.2-cell pawn the ratio was exactly 1, and a lamp four cells
    // away threw four cells of shadow — longer than anything vanilla draws, and reading as a
    // floodlight at ankle height. That pawn height was wrong (vanilla says 0.8), and fixing it is
    // most of what shortened these. Against the correct 0.8 the same 2.4 gives a ratio of **0.5**,
    // half of what looked wrong and a full third longer than the 3.2 that replaced it.
    //
    // NUMERICALLY EQUAL TO LegacyLampHeight AND NOT THE SAME STATEMENT — the legacy constant is
    // paired with LegacyPawnHeight 1.2 for a ratio of 1.0, and exists only so the shape flag's off
    // arm reproduces the previous look. They are two different models that happen to share a lamp
    // height.
    //
    // Lowered because the shadow now fades to nothing at its tip rather than stopping at a visible
    // edge, which costs about a tenth of its apparent length, and because it was asked for: a
    // shadow running half the distance to the lamp reads better than a third. The number is a look
    // choice inside a physical relation rather than a measurement, and it is stated as one.
    public const float DefaultLampHeight = 2.4f;

    // A pawn's height as a caster, in cells, for defs that declare no shadow of their own.
    //
    // 0.8 rather than 1.2 because that is what VANILLA says a human casts: Races_Humanlike.xml
    // declares `specialShadowData` volume (0.3, 0.8, 0.4), and `ShadowData.BaseY` is the tallness
    // its own shader multiplies the extrusion by. §27 invented 1.2 while the answer was sitting in
    // the same struct it was already reading BaseX and BaseZ out of — the same shape of miss as
    // issue #159, where the width came from the wrong place too.
    //
    // The draw prefers the def's own BaseY and only falls back to this, so an animal that declares
    // a squatter shadow now gets a squatter one rather than a human's.
    public const float DefaultPawnHeight = 0.8f;

    // What phase 4b shipped, kept only so the shape flag's off arm is the previous LOOK rather than
    // an absence. Not reachable from settings and not a fallback — if the flag is ever retired,
    // these go with it.
    public const float LegacyPawnHeight = 1.2f;
    public const float LegacyLampHeight = 2.4f;
    public const float LegacyMaxShadowLength = 6f;
    public const float LegacyTipTaper = 0.32f;

    // Never divide by less than this. A pawn taller than the lamp would otherwise throw a shadow
    // through infinity and back, which renders as a bar across the whole map for one frame.
    public const float MinLampHeadroom = 0.35f;

    // Shadows stop growing here however close the lamp gets. Without a cap, a pawn standing ON a
    // lamp's cell casts an arbitrarily long shadow, and the cap is cheaper than special-casing it.
    //
    // Brought down from 6 with the rest of the geometry, then RAISED FROM 2 TO 3 WITH IT: it is a
    // backstop on the degenerate case, so it wants to stay a small multiple of what a normal shadow
    // runs to rather than a number ordinary shadows can reach.
    //
    // Scaled by exactly the 1.5 the lamp height moved the ratio by, and that is the point rather
    // than tidiness. Left at 2 while the ratio went to 0.5, the cap would start binding at four
    // cells from the lamp — an ordinary distance, well inside a torch's reach — and every pawn
    // beyond it would throw the same length. That would quietly delete the property the whole
    // relation exists for, that a shadow grows as its caster moves away from the light, and it
    // would do it in the half of the room furthest from the lamp where it is most visible.
    public const float MaxPawnShadowLength = 3f;

    // The narrowest a pawn shadow's base is allowed to get, in cells from the centre line.
    //
    // A floor rather than a taste value: a def declaring a hairline `ShadowData.volume.x` would
    // otherwise produce a quad thinner than the pixel that has to draw it, which shimmers rather
    // than shades. It sits well BELOW every vanilla pawn (a human's silhouette runs 0.15 to 0.20
    // half-cells as the lamp goes round it) on purpose — the floor's predecessor was 0.175 and
    // clipped most of that range flat, which is the direction-dependence issue #159 is about.
    public const float MinPawnShadowHalfWidth = 0.125f;

    // The footprint assumed for a caster with no ShadowData of its own, as half-extents in cells.
    //
    // Vanilla draws no sun shadow at all in that case, and this deliberately still draws one: an
    // absent blob is a decision about SUNlight — most things that lack one are small, flat or
    // indoors — and a torch a cell away should still throw something. Square, because with no data
    // there is no reason to prefer an axis.
    public const float DefaultPawnShadowHalfExtent = 0.3f;

    // The illuminance a cell counts as fully lit at, and the floor on PawnShadowShare's denominator.
    //
    // One rather than a tuned value because it is not a taste knob: it is the point at which the
    // old model's implicit assumption — that the pawn's cell receives exactly one unit of light —
    // stops being an approximation and starts being an underestimate. Below it the shares would
    // exceed the darkening actually available; at and above it they are the real thing.
    public const float FullIlluminance = 1f;

    // How dark a fully lit, fully seen pawn shadow gets at most.
    //
    // CALIBRATED TWICE, IN OPPOSITE DIRECTIONS, which is worth recording because the first move was
    // a reaction to the wrong cause. At 0.55 through MatBases.SunShadowFade this rendered as an
    // opaque black box, so it was cut to 0.26 — but the box was the MATERIAL ignoring alpha, not the
    // constant being too high. Once the draw moved to a solid-colour material that honours alpha,
    // 0.26 read as barely there and the constant had to come most of the way back.
    //
    // The edge is still hard: a flat polygon has no gradient in it, and vertex colour cannot supply
    // one here either — SolidColor ignores it, and the shadow material that reads it spends the
    // alpha channel on extrusion. A genuinely soft edge needs a shader of our own, which is #151's
    // bundle. Recorded rather than attempted.
    //
    // Still well under 1, because these stack one per lamp: two torches either side of a pawn should
    // read as two soft shadows rather than as a black cross.
    public const float PawnShadowStrength = 0.5f;

    // What vanilla's own sun shadow keeps at its tip, recorded because it is the measurement this
    // whole curve was built against and because we deliberately no longer match it there.
    //
    // Tests/Scenarios/vector_light_shadow_reference.json puts a lamp-lit colonist and a sun-lit
    // colonist in one frame, each carrying exactly one kind of shadow, and the sun shadow's opacity
    // binned along its own length and normalised to its first bin runs
    // 1.000 → 0.709 → 0.568 → 0.471 → 0.396. Ours was flat end to end, which is what made a lamp
    // shadow beside a sun shadow read as a different kind of object rather than a different light.
    public const float VanillaSunShadowTipOpacity = 0.396f;

    // How front-loaded the fade is: 0 is a straight line to nothing, larger values lose opacity
    // faster near the caster and trail off more gently.
    //
    // FITTED TO VANILLA OVER THE NEAR HALF, which is as far as the fit can honestly go — see
    // PawnShadowFade for why the far half deliberately departs. Sweeping k against the measured
    // bins at t ≤ 0.45 puts the best agreement at 0.27, within 0.045 everywhere across that range.
    // A straight line (k = 0) is not much worse there and was rejected on the interior rather than
    // the ends: it reads 0.789 a quarter of the way out where vanilla reads 0.709.
    public const float PawnShadowFadeFrontLoad = 0.27f;

    // The opacity multiplier at `alongFraction` of the way from the caster to the shadow's tip.
    //
    // REACHES EXACTLY ZERO AT THE TIP, WHICH IS A DELIBERATE DEPARTURE FROM VANILLA. Vanilla stops
    // at 0.396 and therefore still ends on an edge — a faint one, but a visible line where the mesh
    // stops. Ending at zero is a look decision, taken deliberately after seeing the 0.396 version in
    // motion, and it is the one place this subsystem knowingly disagrees with the game it is
    // otherwise matching. The consequence is that the curve tracks vanilla over the near half and
    // then falls below it: 0.523 against 0.568 at t = 0.45, and 0.042 against 0.396 at the tip.
    //
    // `(1 - t) / (1 + k·t)` rather than the hyperbola that came before it, because the family that
    // fitted vanilla's endpoint — 1/(1 + k·t) — cannot reach zero at any finite k. This one hits 1
    // at t = 0 and 0 at t = 1 by construction, with k left to shape the interior, so the two
    // endpoints are structural rather than calibrated and only the shape needs a number.
    //
    // IT COSTS ALMOST NO APPARENT LENGTH, which is the objection to check rather than assume. At
    // k = 0.27 the shadow is still above the ~0.08 relative alpha that reads as visible on a lit
    // floor until t = 0.91, so a shadow that fades to nothing looks about a tenth shorter than its
    // geometry, not half. The lamp height was lowered alongside this with that already accounted
    // for.
    //
    // Vanilla reaches its own fade through the vertex-colour channel its shadow shader already
    // spends on extrusion (`MeshMakerShadows` writes alpha 0 at the footprint and `tallness` at the
    // tip, and `Custom/Sun shadow fade` samples NO texture — it has no UVs to sample one with). We
    // cannot borrow that material, so we reach ours through a ramp texture instead; see
    // VectorLightPawnShadows.RampTexture.
    public static float PawnShadowFade(float alongFraction, float frontLoad)
    {
        // A negative front-load would put a pole inside the shadow and flip its sign partway along.
        // Clamped rather than trusted because the only caller that can supply one is a settings path
        // that does not exist yet, and this is cheaper than discovering it later.
        if (frontLoad < 0f)
            frontLoad = 0f;

        float t = alongFraction < 0f ? 0f : (alongFraction > 1f ? 1f : alongFraction);

        return (1f - t) / (1f + frontLoad * t);
    }

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
