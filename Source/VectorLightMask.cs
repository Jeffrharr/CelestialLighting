using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Verse;
using Verse.Glow;

namespace CelestialLighting;

// §27 phase 3: the subtractive mask — vanilla's own lighting, with the light that bent around a
// corner taken back out.
//
// WHY THIS EXISTS AT ALL, given phases 1–2b. Phase 1 replaced vanilla's render with an additive pass
// of our own and phase 2b tried to compose the two as a max. The max measured as a no-op, and the
// reason generalises: our falloff IS vanilla's falloff, so wherever our polygon can see a cell the
// two models agree, and any composition that never darkens vanilla cannot express a shadow — which
// is §27's entire visible contribution. See DESIGN.md §27 phase 2b for the measurement.
//
// So this inverts the operator. Instead of adding our model on top, it SUBTRACTS, per emitter, the
// share of that emitter's own light that reached a cell only by bending:
//
//     newGlow(c) = totalGlow(c) - SUM over our emitters of  own(e, c) * (1 - lit(e, c))
//
// THREE THINGS FALL OUT OF THAT SHAPE, and they are the point of it:
//
//  1. THE LEVEL DOES NOT MOVE. In a cell our polygon can see, lit is 1 and nothing is subtracted, so
//     a lit room reads at exactly the brightness vanilla always gave it. Phases 1–2 needed
//     DefaultStrength to calibrate an additive pass against vanilla's multiply and never quite
//     landed; there is nothing to calibrate here, because we are editing vanilla's own light rather
//     than competing with it.
//  2. DAYLIGHT IS FREE. The additive pass sat ABOVE the sky's multiply, which is why DaylightScale
//     had to exist — without it a torch outglowed noon. This edits the value the multiply consumes,
//     so the sky handles time of day exactly as it does for vanilla.
//  3. NOTHING WE DID NOT MODEL IS EVER TOUCHED. We subtract a named emitter's own contribution and
//     nothing else, so a mod's light arriving by any route survives untouched — by construction,
//     not by a floor. That is the compatibility problem the crossfade exists to manage, gone.
//
// THE COST IS RESOLUTION. The lighting overlay's mesh has one vertex per cell corner plus one per
// cell centre, so a shadow boundary can only be expressed to within a cell, bilinearly interpolated.
// That is why lit is a FRACTION rather than a yes/no — VectorLightMath.LitFraction samples the cell
// and reports what share of it the polygon covers, which turns what would be a staircase into a ramp
// across the boundary cell. DESIGN.md rejected cell resolution for §27 once, on the grounds that it
// is "the resolution §27 exists to escape"; that was written before phase 2 chose to soften every
// edge to half a cell on purpose, so the two are now the same order of blur and the question is
// worth a measurement rather than an inheritance.
public static class VectorLightMask
{
    // Reused across regenerates, which happen on every glow change. Allocating these per call would
    // put a few hundred kilobytes a second through the collector on a flickering torch.
    private static readonly List<VectorLightField.LightEntry> Reaching =
        new List<VectorLightField.LightEntry>();

    private static ColorInt[] cellShadow = new ColorInt[0];
    private static ColorInt[] cornerShadow = new ColorInt[0];

    // Whether phase 3 can run: the per-light arrays have to be readable, or there is nothing to
    // subtract and the subsystem stands down to the crossfade.
    public static bool Available => GlowGridPerLight.Available;

    public static bool Active =>
        CelestialLightingFeatures.VectorLightMask && Available;

    // Rewrites one section's lighting overlay in place. Returns false when it declined to, so the
    // caller can fall through to the crossfade rather than leaving the section unlit or unmasked.
    public static bool Apply(Map map, Mesh mesh, List<Vector3> verts, CellRect rect)
    {
        GlowGridPerLight.Reader reader = GlowGridPerLight.For(map);

        if (reader == null || mesh == null)
            return false;

        CollectReaching(map, rect);

        if (Reaching.Count == 0)
            return true;

        // DECIDE BEFORE TOUCHING THE MESH. `mesh.colors32` copies 613 Color32 out of native memory
        // and the write-back copies them in again, and a section with no shadow anywhere in it
        // changes not one of them. Doing the shadow accumulation first — which needs no mesh at all
        // — means an unshadowed section pays the emitter scan and nothing else, where the crossfade
        // pays the round trip plus a write to every vertex unconditionally.
        bool anyShadow = BuildCellShadow(map, reader, rect);

        Reaching.Clear();

        if (!anyShadow)
            return true;

        int width = rect.Width;
        int height = rect.Height;
        int corners = (width + 1) * (height + 1);
        int expected = corners + width * height;

        Color32[] colors = mesh.colors32;

        // A mesh that is not the shape vanilla builds is somebody else's mesh — another mod
        // transpiling the overlay, or a version change. Bailing keeps us from writing colours into
        // vertices whose meaning we are guessing at.
        if (colors == null || colors.Length != expected || verts == null || verts.Count != expected)
            return false;

        ApplyToCorners(map, colors, rect, corners);
        ApplyToCentres(colors, rect, corners);

        mesh.colors32 = colors;
        return true;
    }

    // Which emitters can reach any cell this section's vertices average over. The vertex loop reads
    // one cell further out on the min side than the section itself, because a corner vertex at the
    // section's edge averages the cells on both sides of it.
    private static void CollectReaching(Map map, CellRect rect)
    {
        Reaching.Clear();

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
        {
            int reach = Mathf.CeilToInt(entry.Radius) + 1;

            bool overlaps = entry.Cell.x + reach >= rect.minX - 1
                && entry.Cell.x - reach <= rect.maxX
                && entry.Cell.z + reach >= rect.minZ - 1
                && entry.Cell.z - reach <= rect.maxZ;

            // CONSUMED, NOT BUILT. Building a polygon here is what put 43 ms of geometry
            // construction inside a whole-map rebake; VectorLightField.EnsurePolygons does it once
            // per frame from the draw instead. An entry whose polygon is not ready yet is skipped
            // for this frame and picked up on the next one, which costs one frame of a shadow that
            // has only just come into existence.
            //
            // An emitter that shadows nothing is skipped outright rather than looked up cell by cell
            // and found to subtract zero every time. In open ground that is most of them.
            if (overlaps && !entry.PolygonDirty && entry.Polygon.Count > 0 && !entry.Unobstructed)
                Reaching.Add(entry);
        }
    }

    // How much light to take back out of each cell, summed over every emitter that reaches it.
    //
    // Per CELL rather than per vertex on purpose: a vertex averages up to four cells and a cell is
    // touched by up to four vertices, so doing it the other way round would run the polygon test
    // four times for the same answer. On a 17x17 section that is the difference between about 1,300
    // coverage tests and about 5,200.
    // ONE CELL OF MARGIN ON EVERY SIDE, not just on the min side, and the asymmetry is a trap worth
    // naming. A section's corner vertices run from minX to maxX + 1 INCLUSIVE, and each one averages
    // the cells on both sides of it, so the outermost vertex reads a cell at maxX + 1 — outside the
    // section entirely. Sizing this grid to the section plus a margin on the min side alone leaves
    // the last row and column unbacked.
    //
    // It fails as an IndexOutOfRangeException inside a section regenerate, which RimWorld CATCHES:
    // it logs "Could not regenerate layer" and leaves the mesh holding the colours vanilla wrote. So
    // the whole feature renders as pixel-identical to vanilla while every probe reads healthy — the
    // exact signature of a feature that never activated. Cost an hour; hence CellIndex below, which
    // both loops now go through so they cannot disagree about the size again.
    private static int CellsWide(CellRect rect) => rect.Width + 2;

    private static int CellsHigh(CellRect rect) => rect.Height + 2;

    private static int CellIndex(CellRect rect, int cellX, int cellZ) =>
        (cellZ - rect.minZ + 1) * CellsWide(rect) + (cellX - rect.minX + 1);

    // Returns whether anything is shadowed at all, so Apply can leave the mesh untouched when
    // nothing is.
    private static bool BuildCellShadow(Map map, GlowGridPerLight.Reader reader, CellRect rect)
    {
        int cells = CellsWide(rect) * CellsHigh(rect);
        Grow(ref cellShadow, cells);

        for (int i = 0; i < cells; i++)
            cellShadow[i] = default;

        bool any = false;

        // EMITTERS OUTER, CELLS INNER, and that ordering is the whole performance story. The first
        // version walked every cell of the section and asked every reaching emitter about it: a
        // dictionary lookup on a long key, a CellRect.Contains and two native-container indexers,
        // about eighteen hundred times per section, for 239 us against the crossfade's 20.
        //
        // None of that per-cell work depended on the cell except the final array read. Resolving the
        // emitter once and then walking only the cells inside its own square — intersected with the
        // section — makes the inner loop an index, a compare and three multiplies, and visits each
        // (emitter, cell) pair once instead of visiting every pair whether or not it overlaps.
        for (int i = 0; i < Reaching.Count; i++)
        {
            VectorLightField.LightEntry entry = Reaching[i];

            if (!reader.TryResolveEmitter(entry.VanillaKey, out GlowLight light, out UnsafeList<Color32> colors))
                continue;

            any |= AccumulateEmitter(map, rect, entry, light, colors);
        }

        return any;
    }

    private static bool AccumulateEmitter(
        Map map, CellRect rect, VectorLightField.LightEntry entry, GlowLight light,
        UnsafeList<Color32> colors)
    {
        bool any = false;

        CellRect reach = light.AffectedRect;

        // The cell grid spans the section plus one cell of margin on every side; the emitter spans
        // its own square. Only their intersection can contribute, and clamping here is what stops
        // the inner loop needing a bounds test per cell.
        int minX = Math.Max(reach.minX, rect.minX - 1);
        int maxX = Math.Min(reach.maxX, rect.maxX + 1);
        int minZ = Math.Max(reach.minZ, rect.minZ - 1);
        int maxZ = Math.Min(reach.maxZ, rect.maxZ + 1);

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int coverage = VectorLightMath.CoverageAt(
                    entry.Coverage, entry.Cell.x, entry.Cell.z, entry.CoverageRadius, x, z);

                // Fully lit is the common case and costs one compare. Checked before the glow read
                // because the array index is the dearer of the two.
                //
                // §27 phase 3c WANTS the fully lit cells, which the shadow pass has no use for: the
                // light vanilla owes a cell is only claimable where the polygon says the cell can be
                // seen, so the deficit lives precisely in the cells this early-out exists to skip.
                // The shadow-only path keeps the early-out and therefore keeps costing what it did.
                if (coverage >= 255 && !CelestialLightingFeatures.VectorLightBeamDifferential)
                    continue;

                IntVec3 cell = new IntVec3(x, 0, z);

                if (!cell.InBounds(map))
                    continue;

                int local = light.WorldToLocalIndex(cell);

                if (local < 0 || local >= colors.Length)
                    continue;

                Color32 own = colors[local];

                // An unlit cell has nothing to subtract, so the shadow pass has always skipped it —
                // but it is precisely where phase 3c has the most to ADD. Vanilla delivering zero
                // while our polygon can see the cell is the open-door case, and it is the only case
                // that produces a beam at all. Skipping it here is what made the previous version
                // photograph an empty doorway.
                bool unlit = own.r == 0 && own.g == 0 && own.b == 0;

                if (unlit && !CelestialLightingFeatures.VectorLightBeamDifferential)
                    continue;

                int shadowed = unlit ? 0 : 255 - coverage;
                int index = CellIndex(rect, x, z);

                // Integer throughout: these are bytes scaled by a byte, so the float round-trip the
                // first version did per channel bought nothing but conversions.
                cellShadow[index].r += own.r * shadowed / 255;
                cellShadow[index].g += own.g * shadowed / 255;
                cellShadow[index].b += own.b * shadowed / 255;

                // §27 phase 3c, accumulated as a NEGATIVE shadow on purpose. ColorInt is signed and
                // Subtract already clamps at both ends, so the corner averaging, the centre averaging
                // and the write-back each carry an addition without knowing they are doing it: no
                // second buffer, no second pass over the mesh, and the two terms net against each
                // other per cell rather than fighting as separate lanes.
                if (CelestialLightingFeatures.VectorLightBeamDifferential)
                {
                    // Vanilla's own origin, so the two distances cannot disagree about where the
                    // lamp is; near the lamp its 1/(d*d) term makes a one-cell error enormous.
                    //
                    // AND VANILLA'S OWN ORIGIN FOR THE DISTANCE ITSELF, which is the fix that made
                    // this term finally net to zero. ComputeGlowGridsJob seeds the emitter's own cell
                    // at intDist 100 rather than 0, so the curve is evaluated at octile + 1 and the
                    // raw octile distance samples it a cell too close — inventing a debt at every
                    // unobstructed cell and lifting the whole room. See VanillaGlowDistance.
                    float straight = VectorLightMath.VanillaGlowDistance(
                        x - light.position.x, z - light.position.z);
                    float falloff = VectorLightMath.VanillaFalloff(straight, light.glowRadius);

                    // Projected as a triple before differencing, because that is what vanilla did to
                    // the value being differenced against. See ProjectLikeVanilla, and OurLightAt for
                    // why the falloff is capped before it is ever multiplied out.
                    VectorLightMath.OurLightAt(
                        light.glowColor.r, light.glowColor.g, light.glowColor.b, falloff,
                        out int ourR, out int ourG, out int ourB);

                    cellShadow[index].r -= VectorLightMath.OwedLightChannel(ourR, own.r, coverage);
                    cellShadow[index].g -= VectorLightMath.OwedLightChannel(ourG, own.g, coverage);
                    cellShadow[index].b -= VectorLightMath.OwedLightChannel(ourB, own.b, coverage);
                }

                any = true;
            }
        }

        return any;
    }

    // Corner vertices, mirroring GenerateLightingOverlay's own averaging exactly: the up-to-four
    // cells meeting at this lattice point, skipping any whose edifice blocks light and any off the
    // map, divided by however many that left. Averaging over a different set than vanilla did would
    // subtract a different quantity than it added, which shows up as a faint grid of light and dark
    // corners rather than as an obvious error.
    private static void ApplyToCorners(Map map, Color32[] colors, CellRect rect, int corners)
    {
        Grow(ref cornerShadow, corners);

        for (int z = rect.minZ; z <= rect.maxZ + 1; z++)
        {
            for (int x = rect.minX; x <= rect.maxX + 1; x++)
            {
                int index = (z - rect.minZ) * (rect.Width + 1) + (x - rect.minX);

                // FOUR INTEGER COMPARES BEFORE ANYTHING ELSE. Most vertices of most sections have
                // no shadow on any of their four cells — a lamp shadows a wedge, not a section —
                // and the full path costs four IntVec3 constructions, four InBounds tests and four
                // edificeGrid lookups to arrive at zero. Reading the accumulator first turns the
                // common case into a handful of compares.
                if (NoShadowAround(rect, x, z))
                {
                    cornerShadow[index] = default;
                    continue;
                }

                ColorInt sum = default;
                int counted = 0;

                for (int corner = 0; corner < 4; corner++)
                {
                    int cx = x - (corner % 2 == 0 ? 1 : 0);
                    int cz = z - (corner < 2 ? 1 : 0);
                    IntVec3 cell = new IntVec3(cx, 0, cz);

                    if (!cell.InBounds(map) || BlocksLight(map, cell))
                        continue;

                    sum += cellShadow[CellIndex(rect, cx, cz)];
                    counted++;
                }

                ColorInt shadow = counted > 0 ? sum / counted : default;

                cornerShadow[index] = shadow;
                colors[index] = Subtract(colors[index], shadow);
            }
        }
    }

    // Whether all four cells meeting at this lattice point are unshadowed. Reads the accumulator
    // rather than the map, so it costs four array reads and no game state at all — the point is to
    // decide NOT to ask the map.
    private static bool NoShadowAround(CellRect rect, int x, int z)
    {
        for (int corner = 0; corner < 4; corner++)
        {
            int cx = x - (corner % 2 == 0 ? 1 : 0);
            int cz = z - (corner < 2 ? 1 : 0);
            ColorInt shadow = cellShadow[CellIndex(rect, cx, cz)];

            if (shadow.r != 0 || shadow.g != 0 || shadow.b != 0)
                return false;
        }

        return true;
    }

    // Centre vertices are vanilla's own average of the four corners around them, so ours is the
    // average of the four corner SUBTRACTIONS. Recomputing the centre from the cell instead would
    // disagree with the corners by a fraction of a level and put a faint diamond in every cell.
    private static void ApplyToCentres(Color32[] colors, CellRect rect, int corners)
    {
        int stride = rect.Width + 1;

        for (int z = rect.minZ; z <= rect.maxZ; z++)
        {
            for (int x = rect.minX; x <= rect.maxX; x++)
            {
                int botLeft = (z - rect.minZ) * stride + (x - rect.minX);

                ColorInt sum = cornerShadow[botLeft];
                sum += cornerShadow[botLeft + 1];
                sum += cornerShadow[botLeft + stride];
                sum += cornerShadow[botLeft + stride + 1];

                // Four unshadowed corners average to nothing, and subtracting nothing is a write
                // that changes no pixel. Skipping it is the same saving as the corner pass's, on the
                // same reasoning.
                if (sum.r == 0 && sum.g == 0 && sum.b == 0)
                    continue;

                int index = corners + (z - rect.minZ) * rect.Width + (x - rect.minX);
                colors[index] = Subtract(colors[index], sum / 4);
            }
        }
    }

    // Alpha is the sky-cover term, not light, and is left exactly alone — §7b's occlusion, §7c/§7d's
    // falloff and the roofed-area floor all ride on it.
    private static Color32 Subtract(Color32 colour, ColorInt shadow)
    {
        return new Color32(
            ClampByte(colour.r - shadow.r),
            ClampByte(colour.g - shadow.g),
            ClampByte(colour.b - shadow.b),
            colour.a);
    }

    // The UNOBSTRUCTED distance from the emitter to a cell, in vanilla's own octile metric rather
    // than in Euclidean. Same metric, straight path: vanilla's glow grid measured the same thing the
    // long way round the walls, so subtracting one from the other leaves exactly the detour and
    // nothing else. Measuring it in Euclidean instead is what made the first phase 3c lift the whole
    // room — see VectorLightMath.OctileDistance.
    private static float DistanceTo(IntVec3 emitter, int x, int z)
    {
        return VectorLightMath.OctileDistance(x - emitter.x, z - emitter.z);
    }

    private static byte ClampByte(int value)
    {
        if (value <= 0)
            return 0;

        return value >= 255 ? (byte)255 : (byte)value;
    }

    private static bool BlocksLight(Map map, IntVec3 cell)
    {
        Thing edifice = map.edificeGrid[cell];
        return edifice != null && edifice.def.blockLight;
    }

    private static void Grow(ref ColorInt[] buffer, int needed)
    {
        if (buffer.Length < needed)
            buffer = new ColorInt[needed];
    }
}
