using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types, linked into the offline tests so the shipped code is
// the tested code.
//
// Where §25's cloud sheets are, how big, and where they have drifted to. The shape INSIDE a sheet is
// CloudField's business; this file only places them.
//
// WHY THIS REPLACED A TILED FIELD, which is worth recording because the tiled version shipped first
// and was measured. §25's first cut stretched one tiling noise texture over the whole map. It read as
// two things, neither of them cloud: mottled haze at partial cover, and — because a tiling field at
// full coverage is uniform — a flat grey veil at full cover, ΔE 13.99 with every pixel changed. The
// tiling was doing real damage on its own: a repeat is a rhythm, and a rhythm is the one thing a sky
// does not have.
//
// So: DISCRETE SHEETS. One bounded cloud, drawn several times at different places, sizes and speeds.
// The differences that buys are not cosmetic —
//
//   * There is no repeat to find, because nothing tiles. A sheet's edges fade to zero alpha inside its
//     own quad, so it has a boundary rather than a seam.
//   * Coverage becomes a COUNT rather than a threshold. More cloud is more clouds, which is what "more
//     cloud" looks like out of a window, and full cover is many overlapping sheets rather than a
//     uniform slab.
//   * Motion stops being rigid. One panning plane translates as a whole and reads as the camera
//     moving — the observation AuroraFieldRegistry.Contour already records. Sheets at different
//     speeds cannot translate rigidly, so the sky evolves without any of it changing shape.
//
// This is §11a's own arrangement (AuroraSheetLayout / AuroraCurtainOverlay's slot materials), reached
// independently for §25 and then deliberately built to look like it, so the two are one pattern in the
// codebase rather than two.
public static class CloudSheetLayout
{
    // How many sheets a fully covered sky gets. The ceiling exists because every sheet is a draw call
    // and a material — the same reason AuroraSheetLayout.MaxSheets does — and because past a certain
    // count they stop being distinguishable and the sky is just covered.
    public const int MaxSheets = 12;

    // A sheet's size as a fraction of the map's smaller axis, before per-sheet variation. Deliberately
    // large: a cloud that fits comfortably on screen reads as a smudge on the ground, while one whose
    // edges run off the view reads as something overhead. Two-thirds of the map means a colony
    // typically sees part of two or three sheets rather than all of any.
    public const float BaseSizeFraction = 0.66f;

    // How much a sheet's size may vary from the base, either way. Enough that no two are obviously the
    // same object; not so much that the small ones read as a different phenomenon.
    public const float SizeVariation = 0.35f;

    // Ticks for the slowest sheet to cross the map once.
    //
    // RAISED FROM 9,000 AFTER WATCHING IT MOVE, which is the only way this number could have been set.
    // At 9,000 (about three and a half in-game hours a crossing) the sheets were unmistakably drifting
    // and unmistakably *stepping* — each tick moved them far enough that the motion read as a sequence
    // of positions rather than as travel. 20,000 is roughly eight in-game hours a crossing: a cloud
    // takes most of a day to cross a colony, which is both what weather does and what makes the motion
    // read as gradual rather than as animation.
    public const int BaseCrossingTicks = 20000;

    // The spread of speeds across sheets, as a fraction of the base. This is the whole reason the sky
    // stops translating rigidly, so it is not small: the fastest sheet crosses in about half the time
    // the slowest does.
    public const float SpeedVariation = 0.5f;

    // How far a sheet may wander across its travel axis over one full crossing, as a fraction of the
    // map. A cloud field is ONE AIR MASS, so the sheets still have to read as going the same way —
    // this is a spread of HEADINGS, not of directions.
    //
    // Raised from 0.25 after watching: at a quarter of the map's depth the tracks were varied enough
    // to measure and not enough to see, so the sky read as sheets on rails. 0.55 is roughly ±15
    // degrees off the shared heading at the extremes, which is visible as clouds fanning slightly
    // apart over a crossing while still plainly travelling together.
    public const float CrossDriftFraction = 0.55f;

    // One placed sheet: where its centre is in map cells, how big it is, and how far round its own
    // rotation it sits. Plain floats rather than a Unity type, so this file stays pure.
    public readonly struct Placement
    {
        public readonly float CenterX;
        public readonly float CenterZ;
        public readonly float Size;

        // Mirroring, per axis — and it is here INSTEAD OF A ROTATION, which is a rendering constraint
        // showing through and worth naming as one.
        //
        // All three cloud lanes draw the same sheet: §25 as cloud above the map, §23c as its shadow on
        // the ground, §23b as the light it bounces down. The two illumination lanes have to be masked
        // to unroofed cells (a roof stops the light), and the only way to mask a bounded shape by an
        // arbitrary cell set in one draw call is to draw the MASK's geometry and position the shape
        // through its texture coordinates — see UvTransform. A UV transform can translate, scale and
        // mirror. It cannot rotate.
        //
        // So rotation was dropped rather than kept for §25 alone: a cloud whose shadow sat at a
        // different angle to itself would be worse than one that never rotates. Mirroring recovers
        // most of the variety it cost (four atlas shapes x four flip combinations = sixteen
        // silhouettes), and unlike rotation it costs nothing on either path.
        public readonly bool FlipU;
        public readonly bool FlipV;

        // Per-sheet alpha, in [0, 1]. Not every sheet is the same thickness — a sky of identically
        // opaque clouds reads as a stencil.
        public readonly float Alpha;

        // Which shape this sheet is wearing, as an opaque non-negative number the caller reduces
        // modulo however many shapes it actually has. The layout deliberately does not know the size
        // of anybody's atlas: that is a rendering fact, and baking it in here would mean a change to
        // the atlas silently changed which shapes appear.
        //
        // It changes ONCE PER CROSSING — see PlacementFor. A cloud keeps its silhouette for the whole
        // time it is on screen (a shape that morphed mid-flight would read as a glitch, not as
        // weather) and wears a different one next time round, which is what stops a dozen sheets on a
        // long-running colony from being four shapes on repeat.
        public readonly int ShapeSeed;

        public Placement(
            float centerX, float centerZ, float size, bool flipU, bool flipV, float alpha, int shapeSeed)
        {
            CenterX = centerX;
            CenterZ = centerZ;
            Size = size;
            FlipU = flipU;
            FlipV = flipV;
            Alpha = alpha;
            ShapeSeed = shapeSeed;
        }
    }

    // The texture transform that puts this sheet's blob exactly where it belongs on the map, for a
    // mesh whose UVs chart 0..1 across the whole map — which is what OpenSkyMask's does (and what
    // MeshPool.wholeMapPlane's does, so the two agree).
    //
    // THIS IS WHAT LETS A BOUNDED CLOUD BE MASKED BY ROOFS. Drawing the sheet as its own quad cannot
    // be clipped to an arbitrary set of cells; drawing the MASK's geometry can, and then the only
    // question is where in that geometry the cloud lands. Scaling the UVs by map-over-sheet and
    // offsetting by the sheet's corner answers it: every cell of the mask samples the blob at the
    // right place, and cells the mask does not contain are simply not drawn.
    //
    // `atlasCells` is how many blobs the atlas holds per axis and `blob` which one this sheet wears —
    // folded in here rather than composed by the caller, because getting the order of two chained UV
    // transforms wrong produces a plausible-looking cloud in the wrong place.
    //
    // Outside the sheet's own rectangle the sampler runs off the blob's cell; that is harmless
    // provided the atlas is Clamp-wrapped and every blob's alpha reaches zero at its own edge, which
    // CloudField.FillBlobAtlas guarantees with its radial falloff.
    public static void UvTransform(
        in Placement placement, int mapX, int mapZ, int atlasCells, int blob,
        out float scaleU, out float scaleV, out float offsetU, out float offsetV)
    {
        float cell = 1f / atlasCells;

        // Map-space UV of the sheet's lower-left corner and its extent, in [0, 1] of the map.
        float sheetU = (placement.CenterX - placement.Size * 0.5f) / mapX;
        float sheetV = (placement.CenterZ - placement.Size * 0.5f) / mapZ;
        float extentU = placement.Size / mapX;
        float extentV = placement.Size / mapZ;

        // Guard against a degenerate map or sheet rather than dividing by zero: a zero-size sheet is
        // invisible anyway, so any finite transform will do.
        if (extentU <= 0f || extentV <= 0f)
        {
            scaleU = scaleV = 1f;
            offsetU = offsetV = 0f;
            return;
        }

        scaleU = cell / extentU;
        scaleV = cell / extentV;
        offsetU = (blob % atlasCells) * cell - sheetU * scaleU;
        offsetV = (blob / atlasCells) * cell - sheetV * scaleV;

        // Mirroring is a negative scale about the blob's own cell, so the flip happens inside the cell
        // rather than sliding the sample into its neighbour.
        if (placement.FlipU)
        {
            scaleU = -scaleU;
            offsetU = ((blob % atlasCells) + 1) * cell - sheetU * scaleU;
        }

        if (placement.FlipV)
        {
            scaleV = -scaleV;
            offsetV = ((blob / atlasCells) + 1) * cell - sheetV * scaleV;
        }
    }

    // How many sheets this much cloud gets. Rounds UP off zero, so any cloud at all puts at least one
    // sheet in the sky rather than rounding a thin morning away to nothing.
    public static int SheetCount(float cloudFraction)
    {
        float fraction = Clamp01(cloudFraction);
        if (fraction <= 0f)
            return 0;

        int count = (int)MathF.Ceiling(fraction * MaxSheets);
        return count > MaxSheets ? MaxSheets : count;
    }

    // Where sheet `index` is at `ticks`, on a map of this size.
    //
    // A PURE FUNCTION OF THE TICK, not an accumulated position, for the reason AuroraSheetSpec.PanU
    // records: an accumulated drift depends on how many frames have been drawn, which makes it
    // unreproducible between two harness runs of the same scenario and therefore unscreenshotable. It
    // also means sheets need no state at all — no spawn, no despawn, no list to maintain.
    //
    // ENTERS AND LEAVES OFF-SCREEN, which is what the span below is for. A sheet's travel runs from a
    // full sheet-width off one edge of the map to a full sheet-width off the other, so it is entirely
    // outside the map at both ends of its journey and nothing ever appears or vanishes in view.
    //
    // The wrap at the end of that span IS the "remove it and start another": because the sheet is
    // completely off-map at that instant, a wrap and a despawn-plus-respawn are indistinguishable on
    // screen — and the wrap costs no state at all. There is no list of live clouds to maintain, no
    // spawn schedule, and nothing to persist across a save; the sky at tick N is a pure function of N.
    //
    // Travel is along +x for every sheet, with a small per-sheet cross drift (see
    // CrossDriftFraction). Sheets going visibly different ways would read as several unrelated
    // weathers at once, because a cloud field is one air mass — so the variation is in SPEED and in a
    // few degrees of heading, not in direction.
    public static Placement PlacementFor(int index, int seed, int ticks, int mapX, int mapZ)
    {
        float sizeNoise = Hash01(index, seed, 1);
        float speedNoise = Hash01(index, seed, 2);
        float crossNoise = Hash01(index, seed, 3);
        float phaseNoise = Hash01(index, seed, 4);
        float rotationNoise = Hash01(index, seed, 5);
        float alphaNoise = Hash01(index, seed, 6);
        float driftNoise = Hash01(index, seed, 7);

        int shorterAxis = mapX < mapZ ? mapX : mapZ;
        float size = shorterAxis * BaseSizeFraction * (1f + SizeVariation * (2f * sizeNoise - 1f));

        // Speed varies per sheet; the crossing distance is the map plus a whole sheet at each end.
        float span = mapX + size * 2f;
        float crossingTicks = BaseCrossingTicks * (1f - SpeedVariation * speedNoise);
        float travelled = phaseNoise + ticks / crossingTicks;

        // How many complete crossings this sheet has made. Everything that should differ between one
        // pass and the next — its shape, its heading — is keyed on this rather than on the tick, so it
        // changes exactly once, at the moment the sheet is off-map and nobody can see it change.
        int crossing = (int)MathF.Floor(travelled);
        float phase = travelled - crossing;

        float centerX = -size + phase * span;

        // The cross drift is measured from the crossing's midpoint, so a sheet's average track is its
        // own crossNoise line rather than being biased to one side of the map. Its sign and magnitude
        // are re-rolled per crossing, so a sheet does not fan the same way every time round.
        float headingNoise = Hash01(index, seed + crossing * 104729, 8);
        float centerZ = crossNoise * mapZ
            + (phase - 0.5f) * mapZ * CrossDriftFraction * (2f * headingNoise - 1f);

        return new Placement(
            centerX,
            centerZ,
            size,
            // Mirroring is re-rolled per crossing, like the shape below: a sheet returning for its
            // fifth pass is a different-looking cloud rather than the same one coming back round.
            Hash01(index, seed + crossing * 104729, 5) > 0.5f,
            Hash01(index, seed + crossing * 104729, 6) > 0.5f,
            // 0.55..1.0, so no sheet is invisible and none is a solid stencil.
            0.55f + 0.45f * alphaNoise,
            (int)(Hash01(index, seed + crossing * 104729, 9) * 1024f));
    }

    // Whether this sheet is currently within drawing distance of the map at all.
    //
    // Sheets spend a real share of their crossing off-map — the span is the map plus a sheet at each
    // end — and drawing one that is entirely outside the view is a wasted draw call and a wasted
    // material write. Cheap to ask (one rectangle overlap) and it is what makes the sheet COUNT a
    // budget rather than a promise: at any instant several of the twelve are off-map, so the number
    // actually drawn is lower than the number that exist.
    public static bool OnScreen(in Placement placement, int mapX, int mapZ)
    {
        float half = placement.Size * 0.5f;
        return placement.CenterX + half > 0f
            && placement.CenterX - half < mapX
            && placement.CenterZ + half > 0f
            && placement.CenterZ - half < mapZ;
    }

    // How much other cloud sits on top of sheet `index` — 0 when it is alone in its patch of sky, and
    // rising with each neighbour it overlaps.
    //
    // WHY THIS IS COMPUTED RATHER THAN COMPOSITED. Where two sheets overlap there is physically more
    // cloud in the column: thicker, denser, brighter from above. Ordinary alpha blending does not say
    // that — it converges on the sheet's own colour, so two stacked sheets look exactly like one
    // slightly more opaque one. Getting the real answer from the GPU would need the pass to read back
    // what it had already drawn, which is a framebuffer round trip per frame. Getting it from the
    // LAYOUT is free: there are at most MaxSheets of them, their positions are already known here, and
    // the result is a pure function that can be tested offline.
    //
    // The measure is the standard circle-overlap approximation — how far the two centres are apart
    // against the sum of their radii — which is crude for a ragged cloud and exactly right for the
    // question being asked, which is "roughly how much is stacked here". Sheets whose centres coincide
    // contribute 1 each; sheets that barely touch contribute nearly nothing.
    public static float OverlapDepth(Placement[] placements, int count, int index)
    {
        if (placements == null || index < 0 || index >= count)
            return 0f;

        Placement self = placements[index];
        float depth = 0f;

        for (int i = 0; i < count; i++)
        {
            if (i == index)
                continue;

            Placement other = placements[i];
            float dx = other.CenterX - self.CenterX;
            float dz = other.CenterZ - self.CenterZ;
            float distance = MathF.Sqrt(dx * dx + dz * dz);

            // Both sizes are full widths, so the radii sum to half of their sum.
            float reach = (self.Size + other.Size) * 0.5f;
            if (reach <= 0f)
                continue;

            float overlap = 1f - distance / reach;
            if (overlap > 0f)
            {
                // Weighted by the other sheet's own alpha: a thin sheet lying across a thick one adds
                // less depth than a thick one does, which is the difference between haze and a second
                // storey of cloud.
                depth += overlap * other.Alpha;
            }
        }

        return depth;
    }

    // Integer hash → [0, 1). The same avalanche AuroraNoise.Hash01 uses, and for the same reason: the
    // per-sheet values must be stable across save and load and decorrelated between neighbouring
    // indices, or sheet 3 and sheet 4 end up the same size at the same speed.
    private static float Hash01(int index, int seed, int channel)
    {
        unchecked
        {
            uint h = (uint)(index * 374761393 + seed * 668265263 + channel * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) * (1f / 16777216f);
        }
    }

    private static float Frac(float v)
    {
        float f = v - (int)v;
        return f < 0f ? f + 1f : f;
    }

    private static float Clamp01(float v)
    {
        if (float.IsNaN(v))
            return 0f;

        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
