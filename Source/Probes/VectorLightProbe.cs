using System.Collections.Generic;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// §27's live measurements, for the harness.
//
// NUMERIC, NOT PIXELS — and issue #3 is the reason that is stated rather than assumed. Measuring an
// effect like this from the frame has already produced two confident wrong answers in this repo: a
// brightness centroid dragged sideways by a colonist standing near the opening "proved" an inverted
// sign, and a profile across a beam "showed" three brightness bands that were really two beams
// projected onto one axis. A shadow's area is not something a centroid can be talked out of.
//
// RECOMPUTED FROM THE PURE CORE, NOT READ OFF THE RENDER. The probe runs the same
// VectorLightBlockers -> VectorLightMath path the draw runs, rather than reporting the cached mesh.
// Two reasons: a light outside the camera is never baked, so reading the render would report zero for
// half the colony depending on where it was looking; and asking the same functions the renderer asks
// is the repo's convention for probes (see EaveCellProbe), so a probe can never agree with a formula
// the screen is not using.
//
// DELIBERATELY NOT GATED ON THE FEATURE FLAG. It reports what the map contains, not what is switched
// on — which is what lets an A/B distinguish "the two frames match because the toggle did nothing"
// from "because this scene has no wall to cast anything".
public sealed class VectorLightProbe : IProbe
{
    public enum Metric
    {
        // How many emitters the map has. A zero here explains every other number below and is the
        // first thing to check when a scenario measures nothing.
        Count,

        // Total lit area, in square cells, summed over every emitter.
        LitArea,

        // The share of what each light COULD reach that is in shadow, weighted by area. This is the
        // number that says the walls are doing something: it is 0 on open ground with no obstruction
        // whatever the lights are doing, and rises as geometry blocks them.
        ShadowFraction,

        // Total fan vertices across every emitter — the mesh cost, and the thing that moves if the
        // ray budget or the corner-ray rule ever changes.
        //
        // FAN vertices, and only those: this is recomputed from the polygon and never builds a mesh,
        // so §27's penumbra wedges are invisible to it. That is not an oversight, but it did read as
        // one once — the first soft-edge A/B reported this metric identical in both arms and looked
        // like a flag that was not reaching the renderer. PenumbraArea is the metric for that.
        Vertices,

        // Total area of the penumbra wedges, in square cells, summed over every emitter — §27 phase
        // 2's soft shadow edges.
        //
        // THE ONE METRIC HERE THAT DOES READ THE FEATURE FLAG, against the file's convention above,
        // and deliberately. The others answer "what does this map contain", where gating would
        // destroy an A/B's ability to tell a dead toggle from an empty scene. This one answers a
        // different question — "how much soft edge is being DRAWN" — for which the flag is not
        // context but the subject. It reads 0 with vector_light_penumbra off and the measured area
        // with it on, so a flag that stopped reaching the mesh builder fails a scenario rather than
        // quietly producing two identical frames.
        PenumbraArea,

        // Whether §27 phase 3's subtractive mask can run: 1 if vanilla's per-emitter glow arrays are
        // readable, 0 if the mask has stood down to the crossfade.
        //
        // THIS IS THE ONE THAT CATCHES THE SILENT FAILURE, and it is not hypothetical: the arrays are
        // private fields on a Burst-adjacent type, exactly the sort of thing a RimWorld update
        // renames. Standing down is by design — a mod that cannot read them must still have light —
        // so without a pin here every other number in a mask scenario stays healthy while the frames
        // quietly show the crossfade instead.
        MaskAvailable,

        // §27 phase 5: how many cells the last whole-map rebake put a lift on, and the largest single
        // channel of lift it wrote.
        //
        // READ AS A PAIR OR NOT AT ALL, because a zero in either has a different cause and only the
        // pair can separate them. MaskLiftSamples at zero means the max never ran — a stale bake, a
        // flag that did not reach the mesh builder, or the relaxed emitter skip having quietly
        // un-relaxed. MaskLiftPeak at zero with a healthy sample count means it ran and found
        // nothing to do, which in a scene where both lighting models see the same geometry is the
        // CORRECT answer and is #151's entire finding. A scenario pinning only the peak cannot tell
        // "the composition is degenerate here" from "the composition is not running", which are the
        // two outcomes this arm exists to distinguish.
        // §27 phase 6: whether the custom shader loaded and is supported, so the per-fragment max
        // can actually be drawn.
        //
        // PIN THIS IN ANY ARM CLAIMING TO MEASURE IT. The shader failing to load is BY DESIGN not an
        // error — a missing bundle, a bundle built for another OS, or hardware that cannot compile
        // the pass all stand the feature down silently so that a player never loses their light. So
        // without a pin here, an arm whose bundle never reached the run leaves every other number
        // healthy while the frames quietly show the previous composition. #151 registered the same
        // metric for the same reason.
        ShaderMaxAvailable,

        MaskLiftSamples,

        MaskLiftPeak,

        // §27 phase 5b: how many cells the last rebake found over vanilla's 255 ceiling and rewrote,
        // how many it declined to rewrite, and the largest number of levels it took OFF a shadow.
        //
        // THE SAME PAIRING RULE AS THE LIFT ABOVE, and a third number because this correction has two
        // ways of doing nothing rather than one. Samples at zero means no cell in the scene saturated
        // — correct for a one-lamp room and a bake that never ran for a six-lamp ring, and the way to
        // tell those apart is to build the ring on purpose. Skipped counts cells where our
        // reconstruction of vanilla's sum did not reproduce vanilla's own displayed value, which
        // happens for mixed-hue emitters and nothing else; a run where Skipped is large and Samples
        // is small is measuring the fallback rather than the fix. Relief is the size of the bug being
        // corrected, in levels of glow, so a scenario can say how much of a shadow was spurious
        // rather than only that some of it was.
        MaskSaturatedSamples,

        MaskSaturationSkipped,

        MaskSaturationRelief,

        // §27 phase 4, issue #159: where on a colonist the lamp shadow is anchored, as the z offset
        // from DrawPos, and how wide that footprint is.
        //
        // TWO METRICS FOR ONE BUG BECAUSE IT HAD TWO HALVES, and each is invisible to the other. The
        // shadow left the pawn's torso instead of their feet (this read 0 where vanilla's sun shadow
        // uses -0.3) AND it was twice as wide (0.6 against a real 0.3), because the data was being
        // read from `graphicData.shadowData`, which humanlikes do not have. Fixing only the offset
        // would leave the width pinned wrong and vice versa, and a single "is it right" metric would
        // pass on half a fix.
        //
        // These read VectorLightPawnShadows.ShadowDataOf — the same accessor the draw calls — rather
        // than reaching into the def here. A probe with its own copy of the lookup would have agreed
        // with the intended rectangle while the renderer used another one, which is the exact shape
        // of the bug being pinned.
        PawnShadowAnchorZ,
        PawnShadowWidth,

        // How many spawned pawns on the map §27 would actually draw a lamp shadow for.
        //
        // The counterpart to the two metrics above: they pin the SHAPE of a shadow that is drawn,
        // this one pins WHETHER it is drawn at all. Vanilla suppresses a pawn shadow for four
        // reasons that have nothing to do with sunlight — not standing, psychically invisible,
        // swimming, flying — and §27 asked none of them, so a downed colonist threw a
        // standing-height shadow from a torch.
        //
        // A COUNT RATHER THAN A PER-PAWN FLAG because that is what a scenario can pin without
        // depending on spawn order, and because the failure it guards against is a clause being
        // dropped rather than a pawn being misjudged: three colonists, one of them upright, has
        // exactly one answer.
        //
        // SCOPED TO THE CAMERA VIEW RECT, which is the renderer's own cull and not a convenience.
        // Counted map-wide it read 11 on minimal_colony.rws — the fixture's own colonists and
        // animals swamped the three the scenario spawned, and 11 is consistent with the suppression
        // working AND with it not working, which is the one thing a pin must never be. Sharing the
        // renderer's cull also means the number answers the question a screenshot asks: of the pawns
        // in this frame, how many are casting.
        PawnShadowCasters,

        // §27 phase 4b: how dark this pawn's lamp shadows actually are — the darkest single arm, and
        // what all of them composite to where they overlap at the caster's feet.
        //
        // TWO METRICS BECAUSE THEY FALL BY DIFFERENT FACTORS, and the RATIO between them is the
        // thing under test. Measured over six lamps, the peak arm goes 0.1875 -> 0.0772 (x0.41)
        // while the rosette goes 0.7028 -> 0.3753 (x0.53): individual arms get much fainter than
        // the total darkening does, because the arms overlap at the caster's feet and the shares
        // they are now drawn at sum rather than compound.
        //
        // An earlier draft of this comment predicted the rosette would barely move at all, and the
        // live run said otherwise — it halves, because phase 4's rosette was over-dark rather than
        // correct. Recorded rather than quietly corrected: the prediction was the reasoning for
        // pinning both, and it was wrong in a way only the measurement caught.
        //
        // AND NEITHER IS VISIBLE TO A SCREENSHOT AS A NUMBER. These are alphas — the frame shows
        // their consequence composited against whatever ground, lighting overlay and pawn sprite
        // happen to be underneath, which is exactly the situation where a pixel measurement reports
        // the sprite rather than the effect.
        PawnShadowPeak,
        PawnShadowRosette,

        // §27 / issue #166: how far the longest of this pawn's lamp shadows actually reaches, in
        // cells beyond the caster.
        //
        // THE METRIC FOR A CLIP, and it needs one for the same reason the alphas did. A shadow that
        // stops at a wall and one that runs through it differ only in pixels on the FAR side of the
        // wall — a region a masked frame measurement has to be told to look at, and which a median
        // over the whole frame cannot see at all. The reach says it in one number.
        //
        // The maximum rather than the sum, because the failure is one shadow escaping: six arms
        // averaging correctly while one crosses a wall is exactly what a sum would hide.
        PawnShadowReach,

        // The alpha the along-length fade ramp ends on, read off the texture the draw samples.
        //
        // A SEPARATE METRIC BECAUSE peak AND rosette CANNOT SEE THIS. Both are defined at the
        // caster, where the ramp is 1 by construction, so a correctly feathered shadow leaves them
        // exactly where a flat one left them. That is not the pair failing to notice a bug — it is
        // what makes them the right control for a change that must not move the shadow's darkness
        // where it starts — but it does mean the fade needs its own number or it is unpinnable, and
        // an unpinned render path is one that can silently fall back to flat.
        //
        // Map-free: the ramp is one row shared by every shadow on every map.
        PawnShadowTipFade,

        // The same ramp halfway along, which is what actually identifies the curve now that both of
        // its ends are structural rather than calibrated.
        PawnShadowMidFade,

        // The caster dimensions the draw resolves for the ANIMAL in view, as opposed to the colonist
        // every other probe here picks. Their own metrics because animals reach their shadow data by
        // a different route entirely -- a life stage's bodyGraphicData rather than the ThingDef --
        // and a probe that only ever looked at colonists reported a healthy 0.3/0.8 while every cat
        // on the map was drawn as a human. Pin BOTH: the wrong source got the height and the width
        // wrong independently, and each is invisible in the other's number.
        AnimalCasterHeight,
        AnimalCasterHalfWidth,
    }

    private readonly Metric metric;

    public string Name { get; }

    public VectorLightProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    public float Read(Map map)
    {
        if (metric == Metric.MaskAvailable)
            return VectorLightMask.Available ? 1f : 0f;

        // Map-free like MaskAvailable above: these count what the bake did, and the bake has already
        // happened by the time a probe reads.
        if (metric == Metric.ShaderMaxAvailable)
            return VectorLightShader.Available ? 1f : 0f;

        if (metric == Metric.MaskLiftSamples)
            return VectorLightMask.LiftSamples;

        if (metric == Metric.MaskLiftPeak)
            return VectorLightMask.LiftPeak;

        if (metric == Metric.MaskSaturatedSamples)
            return VectorLightMask.SaturatedSamples;

        if (metric == Metric.MaskSaturationSkipped)
            return VectorLightMask.SaturationSkipped;

        if (metric == Metric.MaskSaturationRelief)
            return VectorLightMask.SaturationRelief;

        // Reports 1 when the feature is off, which is the true statement about the frame rather than
        // a sentinel: the flat path really does draw a shadow that keeps full opacity to its tip.
        //
        // ASKS THE TEXTURE THE DRAW ACTUALLY BOUND, not the one a fresh build would produce. Reading
        // the live material's own texture is the difference between "the formula says flat" and "the
        // frame is flat" — and those came apart once already here, when a cached material went on
        // sampling the previous arm's ramp while every recomputed number agreed the arm had changed.
        if (metric == Metric.PawnShadowTipFade)
        {
            return CelestialLightingFeatures.VectorLightShadowFeather
                ? VectorLightPawnShadows.BoundRampAlphaAt(1f)
                : 1f;
        }

        // Pinned BESIDE the tip, not instead of it. The curve now ends at exactly zero by
        // construction, so the tip alone no longer identifies it — any broken shape that happens to
        // vanish reads the same there. Halfway along is where the shape lives.
        if (metric == Metric.PawnShadowMidFade)
        {
            return CelestialLightingFeatures.VectorLightShadowFeather
                ? VectorLightPawnShadows.BoundRampAlphaAt(0.5f)
                : 1f;
        }

        if (map == null)
            return 0f;

        if (metric == Metric.PawnShadowAnchorZ || metric == Metric.PawnShadowWidth)
            return ReadFootprint(map);

        if (metric == Metric.AnimalCasterHeight || metric == Metric.AnimalCasterHalfWidth)
            return ReadAnimalCaster(map);

        if (metric == Metric.PawnShadowCasters)
            return CountCasters(map);

        if (metric == Metric.PawnShadowPeak || metric == Metric.PawnShadowRosette
            || metric == Metric.PawnShadowReach)
            return ReadShadow(map);

        float litArea = 0f;
        float openArea = 0f;
        float penumbraArea = 0f;
        int vertices = 0;
        int count = 0;

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
        {
            count++;
            Accumulate(map, entry, ref litArea, ref openArea, ref vertices, ref penumbraArea);
        }

        return Report(count, litArea, openArea, vertices, penumbraArea);
    }

    // Asks VectorLightPawnShadows.CastsShadow, never its own copy of the four tests: a probe with a
    // private reimplementation would agree with the intended policy while the renderer used another,
    // which is the shape of the bug this whole pair of commits is about.
    private static float CountCasters(Map map)
    {
        CellRect view = Find.CameraDriver.CurrentViewRect;
        int casters = 0;

        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (view.Contains(pawn.Position) && VectorLightPawnShadows.CastsShadow(pawn))
                casters++;
        }

        return casters;
    }

    // The darkest arm, or the composite of every arm, for the same one colonist ReadFootprint picks
    // — and picked by the same rule for the same reason: a scenario with two of them must pin a
    // stable pawn across runs rather than whichever the spawn order happened to yield.
    //
    // The composite is 1 - prod(1 - a), which is what alpha blending does when the arms overlap, and
    // they all overlap at the caster's own feet: every shadow starts at the silhouette's trailing
    // edge and radiates outward, so the cells immediately around the pawn are under all of them.
    // That is the spot the old model turned black, so it is the spot worth reporting.
    private float ReadShadow(Map map)
    {
        // SCOPED TO THE CAMERA VIEW RECT, exactly as CountCasters is, and for a reason that cost a
        // whole live run to find. ReadFootprint picks its colonist map-wide, which is harmless there
        // because every human shares one ShadowData — but this metric depends on WHERE the pawn is
        // standing, and minimal_colony.rws ships its own colonists whose thing IDs are lower than
        // anything a scenario spawns. Map-wide, this read a fixture colonist asleep on the far side
        // of the map with no lamp near them and reported 0.00 in both arms, which is indistinguishable
        // from the feature being dead. Sharing the renderer's own cull makes the number a fact about
        // the frame the screenshot shows.
        CellRect view = Find.CameraDriver.CurrentViewRect;
        Pawn subject = null;

        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
        {
            if (!view.Contains(pawn.Position))
                continue;

            if (subject == null || pawn.thingIDNumber < subject.thingIDNumber)
                subject = pawn;
        }

        VectorLightPawnShadows.ShadowsFor(map, subject, DrawnShadows);

        float peak = 0f;
        float reach = 0f;
        float clear = 1f;

        for (int i = 0; i < DrawnShadows.Count; i++)
        {
            VectorLightPawnShadows.DrawnShadow drawn = DrawnShadows[i];

            if (drawn.Opacity > peak)
                peak = drawn.Opacity;

            if (drawn.Length > reach)
                reach = drawn.Length;

            clear *= 1f - drawn.Opacity;
        }

        if (metric == Metric.PawnShadowReach)
            return reach;

        return metric == Metric.PawnShadowPeak ? peak : 1f - clear;
    }

    // Reused rather than allocated per read, matching the draw path's own list: a probe runs on the
    // main thread beside the renderer and there is no reason for it to be the one making garbage.
    private static readonly List<VectorLightPawnShadows.DrawnShadow> DrawnShadows =
        new List<VectorLightPawnShadows.DrawnShadow>();

    // The footprint of ONE colonist, chosen by lowest thing ID so a scenario with two of them pins a
    // stable one across runs rather than whichever the spawn order happened to yield.
    private float ReadFootprint(Map map)
    {
        Pawn subject = null;

        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
        {
            if (subject == null || pawn.thingIDNumber < subject.thingIDNumber)
                subject = pawn;
        }

        ShadowData shadow = subject == null ? null : VectorLightPawnShadows.ShadowDataOf(subject);

        if (shadow == null)
            return 0f;

        return metric == Metric.PawnShadowAnchorZ ? shadow.offset.z : shadow.BaseX;
    }

    // The animal the camera is looking at, and what the renderer resolves for it.
    //
    // VIEW-SCOPED like the shadow metrics and for the same reason: minimal_colony.rws ships its own
    // animals, and a map-wide lowest-thing-ID pick would read some muffalo asleep across the map
    // instead of the cat the scenario placed under the lamp -- a confident wrong number in every arm.
    //
    // Non-humanlike rather than "not a colonist", because the distinction that matters here is which
    // ROUTE the shadow data arrives by: humanlikes declare race.specialShadowData, everything else
    // declares it on a life stage's body graphic.
    private float ReadAnimalCaster(Map map)
    {
        CellRect view = Find.CameraDriver.CurrentViewRect;
        Pawn subject = null;

        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (pawn.RaceProps == null || pawn.RaceProps.Humanlike)
                continue;

            if (!view.Contains(pawn.Position))
                continue;

            if (subject == null || pawn.thingIDNumber < subject.thingIDNumber)
                subject = pawn;
        }

        if (subject == null)
            return 0f;

        return metric == Metric.AnimalCasterHeight
            ? VectorLightPawnShadows.CasterHeightOf(subject)
            : VectorLightPawnShadows.CasterHalfWidthOf(subject);
    }

    private static void Accumulate(
        Map map, VectorLightField.LightEntry entry, ref float litArea, ref float openArea,
        ref int vertices, ref float penumbraArea)
    {
        VectorLightMath.Segment[] segments =
            VectorLightBlockers.SegmentsAround(map, entry.Cell, entry.Radius);

        VectorLightMath.LightPolygon polygon = VectorLightMath.Build(
            entry.Cell.x + 0.5f, entry.Cell.z + 0.5f, entry.Radius, segments,
            VectorLightMath.DefaultBaseRayCount);

        litArea += Area(polygon);
        vertices += polygon.Count + 1;
        penumbraArea += WedgeArea(map, entry, polygon);

        // The unobstructed comparison is the polygon the same ray budget would produce with nothing in
        // the way — the inscribed 48-gon, not the true circle. Comparing against pi*r^2 instead would
        // report a standing 0.1% shadow on completely open ground, which is a floor that would have to
        // be remembered and subtracted every time anyone read this number.
        openArea += 0.5f * VectorLightMath.DefaultBaseRayCount * entry.Radius * entry.Radius
            * (float)System.Math.Sin(2.0 * System.Math.PI / VectorLightMath.DefaultBaseRayCount);
    }

    private float Report(int count, float litArea, float openArea, int vertices, float penumbraArea)
    {
        switch (metric)
        {
            case Metric.Count:
                return count;
            case Metric.LitArea:
                return litArea;
            case Metric.Vertices:
                return vertices;
            case Metric.PenumbraArea:
                return penumbraArea;
            default:
                return openArea <= 0f ? 0f : 1f - litArea / openArea;
        }
    }

    // The area the soft edges cover, taken from the mesh the renderer would build for this light with
    // the flag in the state it is actually in — so the source radius comes from the same expression
    // VectorLightOverlay.Rebuild uses, not from a constant this file picked.
    //
    // Summed over the triangles PAST FanTriangleCount, which is the only place the wedges are: the fan
    // tiles the visibility polygon exactly once and the wedges lie outside it, in the shadow. Summing
    // the whole index buffer would report the lit area plus the soft edges and would be dominated by
    // the former, which is the number LitArea already gives.
    private static float WedgeArea(
        Map map, VectorLightField.LightEntry entry, VectorLightMath.LightPolygon polygon)
    {
        float sourceRadius = CelestialLightingFeatures.VectorLightPenumbra
            ? VectorLightMath.DefaultSourceRadius
            : 0f;

        VectorLightMath.LightMesh mesh = VectorLightMath.BuildMesh(
            entry.Cell.x + 0.5f, entry.Cell.z + 0.5f, entry.Radius, polygon, sourceRadius);

        float total = 0f;

        for (int t = mesh.FanTriangleCount; t < mesh.Triangles.Length; t += 3)
        {
            int a = mesh.Triangles[t];
            int b = mesh.Triangles[t + 1];
            int c = mesh.Triangles[t + 2];

            total += 0.5f * System.Math.Abs(
                (mesh.X[b] - mesh.X[a]) * (mesh.Z[c] - mesh.Z[a])
                - (mesh.X[c] - mesh.X[a]) * (mesh.Z[b] - mesh.Z[a]));
        }

        return total;
    }

    private static float Area(VectorLightMath.LightPolygon polygon)
    {
        float total = 0f;

        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;

            double ax = System.Math.Cos(polygon.Angles[i]) * polygon.Distances[i];
            double az = System.Math.Sin(polygon.Angles[i]) * polygon.Distances[i];
            double bx = System.Math.Cos(polygon.Angles[next]) * polygon.Distances[next];
            double bz = System.Math.Sin(polygon.Angles[next]) * polygon.Distances[next];

            total += (float)System.Math.Abs(0.5 * (ax * bz - bx * az));
        }

        return total;
    }
}
