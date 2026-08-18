using System.Collections.Generic;
using UnityEngine;
using Verse;

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

        CollectReaching(map, rect);

        if (Reaching.Count == 0)
            return true;

        BuildCellShadow(map, reader, rect);
        ApplyToCorners(map, colors, rect, corners);
        ApplyToCentres(colors, rect, corners);

        mesh.colors32 = colors;
        Reaching.Clear();
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

            if (overlaps)
            {
                VectorLightField.EnsurePolygon(map, entry);

                if (entry.Polygon.Count > 0)
                    Reaching.Add(entry);
            }
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

    private static void BuildCellShadow(Map map, GlowGridPerLight.Reader reader, CellRect rect)
    {
        int cells = CellsWide(rect) * CellsHigh(rect);
        Grow(ref cellShadow, cells);

        for (int i = 0; i < cells; i++)
            cellShadow[i] = default;

        for (int z = rect.minZ - 1; z <= rect.maxZ + 1; z++)
        {
            for (int x = rect.minX - 1; x <= rect.maxX + 1; x++)
            {
                IntVec3 cell = new IntVec3(x, 0, z);

                if (cell.InBounds(map))
                    cellShadow[CellIndex(rect, x, z)] = ShadowAt(reader, cell);
            }
        }
    }

    private static ColorInt ShadowAt(GlowGridPerLight.Reader reader, IntVec3 cell)
    {
        ColorInt total = default;

        for (int i = 0; i < Reaching.Count; i++)
        {
            VectorLightField.LightEntry entry = Reaching[i];

            if (!reader.TryGlowAt(entry.VanillaKey, cell, out Color32 own))
                continue;

            if (own.r == 0 && own.g == 0 && own.b == 0)
                continue;

            float lit = VectorLightMath.LitFraction(
                entry.Polygon, entry.Cell.x + 0.5f, entry.Cell.z + 0.5f, cell.x, cell.z,
                VectorLightMath.DefaultCoverageSamples);

            float shadowed = 1f - lit;

            if (shadowed <= 0f)
                continue;

            total.r += Mathf.RoundToInt(own.r * shadowed);
            total.g += Mathf.RoundToInt(own.g * shadowed);
            total.b += Mathf.RoundToInt(own.b * shadowed);
        }

        return total;
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

                int index = (z - rect.minZ) * (rect.Width + 1) + (x - rect.minX);
                ColorInt shadow = counted > 0 ? sum / counted : default;

                cornerShadow[index] = shadow;
                colors[index] = Subtract(colors[index], shadow);
            }
        }
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
