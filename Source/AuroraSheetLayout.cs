using System;

namespace CelestialLighting;

// Where the aurora's sheets stand over a map, in map cells. Pure — no UnityEngine, no Verse — so the
// placement rules are covered by offline tests rather than only ever being seen in a screenshot.
//
// ================================================================================================
// THE PROBLEM THIS SOLVES
//
// §11a used to stretch one tiling texture over MeshPool.wholeMapPlane, which is 2000 world units
// across with its UVs multiplied by 200 — one texture repeat per ten cells. Scale that so a repeat
// spans N cells and the field tiles across the map every N cells in BOTH axes.
//
// For the contour field that is harmless: it is an overhead view of a wandering ribbon, and one patch
// of wandering ribbon looks much like another. For the hem-rays field it is fatal, because its v axis
// is not map-north — IT IS ALTITUDE UP THE CURTAIN. Repeating v does not tile a texture, it stamps the
// same three arcs, hem and all, over and over as you pan north. At 76 cells per repeat against a
// camera showing ~120 cells that is ~1.6 copies of a distinctive stack on one screen, and 3.3 of them
// up a 250-cell map. It reads as wallpaper.
//
// ================================================================================================
// THE FIX: A SHEET IS A BOUNDED QUAD WITH ITS V SCALE PINNED TO EXACTLY 1
//
// Rather than one plane covering the world, draw a small number of quads, each showing exactly ONE
// vertical repeat of the field. Vertical tiling stops being something the constants have to be tuned
// to avoid and becomes arithmetically impossible, at any map size, at any zoom, forever.
//
// Horizontal repeats stay, and are the acceptable kind: a wandering hem line repeating every 150 cells
// is well beyond one screen, and each sheet gets its own phase and may be mirrored in u, so no two
// read as copies of each other.
//
// The sky between and beyond the sheets is genuinely empty, which is intended rather than tolerated. A
// real auroral display occupies a band of sky, not all of it, and the effect should not obscure the
// game underneath it. §11's flat map-wide tint still colours the whole sky faintly, so the gaps are
// not black.
//
// This also subsumes the "one giant aurora" idea at zero extra cost: a giant is simply one sheet whose
// CellsPerRepeatY is large. Nothing here changes to express it.
public readonly struct AuroraSheetPlacement
{
    // Centre of the quad, in map cells from the south-west corner.
    public readonly float CenterX;
    public readonly float CenterZ;

    // Size of the quad, in map cells.
    public readonly float Width;
    public readonly float Height;

    // Texture scale. UScale is SIGNED — negative mirrors the sheet in u, which costs nothing and makes
    // a hem curve very hard to recognise as one you have already seen further up the sky.
    public readonly float UScale;

    // Always exactly 1 for a bounded sheet. That single fact is what this whole file is for.
    public readonly float VScale;

    // Constant added to the panning u offset, so two sheets sharing a texture are never showing the
    // same stretch of it at the same moment.
    public readonly float UPhase;

    public readonly float Alpha;

    public AuroraSheetPlacement(
        float centerX, float centerZ, float width, float height,
        float uScale, float vScale, float uPhase, float alpha)
    {
        CenterX = centerX;
        CenterZ = centerZ;
        Width = width;
        Height = height;
        UScale = uScale;
        VScale = vScale;
        UPhase = uPhase;
        Alpha = alpha;
    }
}

public static class AuroraSheetLayout
{
    // Ceiling on sheets drawn at once. Four is already more sky than RimWorld's camera can show; the
    // cap exists so the material array can be allocated once at startup, since `new Material` must
    // happen on the main thread.
    public const int MaxSheets = 4;

    // How much clear sky to leave per sheet, as a multiple of the sheet's own height. Above 1 means the
    // gaps are wider than the sheets, which is what keeps the aurora a feature of the sky rather than a
    // covering over it — and is what the "do not obscure the game" requirement amounts to numerically.
    public const float SheetSpacing = 4.6f;

    // Where sheets sit up the map, as fractions of its height, in the order they are added.
    //
    // SLOT 0 IS PLACED WHERE A PLAYER IS ACTUALLY LOOKING, THE REST FOR THE MAP. Placement stays
    // map-relative — a display hangs over a fixed piece of sky and the player can pan away from it,
    // which is the honest behaviour and the reason the aurora is scenery rather than a HUD element.
    //
    // But "map-relative" is not the same as "anywhere", and getting this wrong is what made the patch
    // look clipped through several iterations. A RimWorld camera sits over the COLONY, not the map
    // centre: the harness fixture's own camera rests at (110, 134) on a 250-cell map — 0.44 and 0.54
    // as fractions — with an orthographic size of ~37, i.e. a view about 131 x 74 cells. A patch put
    // at the map centre lands 15 cells off that view's centre and only ~2 cells inside its right edge,
    // which reads exactly like being cut off, and no amount of shrinking the patch fixes it because
    // the error is in the position rather than the size.
    //
    // So slot 0 sits at the fractions a colony camera actually looks at. The others are scattered for
    // someone who pans, and are expected to be out of frame.
    //
    // Deliberately IRREGULAR. Evenly spaced sheets are periodicity wearing a different hat: three
    // curtains at 0.25/0.50/0.75 would read as one pattern repeating, which is the exact defect this
    // file exists to remove. These gaps are 0.36, 0.20 and 0.44 of map height — no two alike, and none
    // a simple multiple of another.
    private static readonly float[] CentreFractions = { 0.54f, 0.26f, 0.80f, 0.40f };

    // And where they sit ACROSS the map. Sheets are bounded in u as well as v — one horizontal repeat,
    // feathered at both ends — so a display is a patch of sky with all four edges visible rather than a
    // band running off both sides of the screen. Paired with the z fractions above these put the
    // patches on a diagonal, which is why neither list is sorted the same way.
    private static readonly float[] CentreFractionsX = { 0.44f, 0.26f, 0.72f, 0.60f };

    // Descending, so the sky has one dominant display with fainter ones behind it rather than several
    // arguing about which is the subject.
    private static readonly float[] Alphas = { 1f, 0.72f, 0.50f, 0.35f };

    // Irrational-ish spacing rather than 0, 0.25, 0.5, 0.75, for the same reason as CentreFractions.
    private static readonly float[] UPhases = { 0f, 0.37f, 0.71f, 0.13f };

    // Alternating, so adjacent sheets are mirror images and the eye cannot match their hems.
    private static readonly bool[] Mirrored = { false, true, false, true };

    // ---- Per-display size and position ---------------------------------------------------------
    //
    // A display is now placed ONCE PER AURORA, at a random spot, at a random size. Fixed placement made
    // every aurora identical, which is the one thing a rare event cannot afford: the second one a
    // player sees should not be a copy of the first.
    //
    // The randomness is seeded from the tick the aurora began, so it is stable for the whole event —
    // the patch does not jitter frame to frame — and reproducible, which is what lets a harness
    // scenario screenshot it at all.

    // Largest a display may be, in map cells. Deliberately smaller than the view: at 70 x 37 against a
    // ~131 x 74 view the patch occupies rather over half of each axis, leaving room for it to sit
    // somewhere other than dead centre and still show all four of its tapered edges.
    public const float PatchMaxWidth = 70f;
    public const float PatchMaxHeight = 37f;

    // Smallest, as a fraction of the maximum. Half means the largest display has four times the area of
    // the smallest, which is a wide enough spread to be obvious without the small ones reading as a
    // different effect.
    public const float PatchMinScale = 0.5f;

    // Places one display somewhere on the map, in map cells.
    //
    // THE MAP, NOT THE CAMERA. An earlier version picked the spot inside the camera's view rectangle so
    // the display would always be framed. That was solving the wrong problem — it was compensating for a
    // separate bug where the placement was recomputed every frame, which made the patch slide with the
    // camera — and it does not make sense on its own terms: where an aurora is has nothing to do with
    // where the player happens to be looking. A display now happens over a piece of ground, and you see
    // it if you are looking that way, which also means the overlay no longer touches CameraDriver at
    // all.
    public static AuroraSheetPlacement RandomPlacement(int seed, int mapX, int mapZ)
    {
        // Clamped to what the map can actually hold, wander included. A pocket or quest map can be 75
        // cells against a display up to 70 wide, and an oversized patch would hang off the edge where
        // its taper is clipped by geometry rather than fading — the exact hard edge the taper exists to
        // remove. Shrinking it simply draws a smaller display, which is the right answer on a small map.
        float width = Math.Min(PatchMaxWidth * ScaleFrom(seed, 1), mapX - 2f * PatchDriftCells);
        float height = Math.Min(PatchMaxHeight * ScaleFrom(seed, 2), mapZ);

        width = Math.Max(width, 1f);
        height = Math.Max(height, 1f);

        // Inset by the drift as well as the half-extent, so a patch that has wandered to the end of its
        // travel is still wholly framed rather than half off the edge at the extremes of its motion.
        // Inset so the whole display — including the far end of its wander — is on the map, because a
        // patch overhanging the edge would have its taper clipped by geometry rather than fading out.
        float centreX = PlaceWithin(seed, 3, 0f, mapX, width * 0.5f + PatchDriftCells);
        float centreZ = PlaceWithin(seed, 4, 0f, mapZ, height * 0.5f);

        return new AuroraSheetPlacement(
            centreX,
            centreZ,
            width,
            height,
            Hash01(seed, 5) < 0.5f ? -1f : 1f,
            vScale: 1f,
            uPhase: 0f,
            alpha: 1f);
    }

    // Applies this frame's east-west wander to a placement already fixed in MAP coordinates.
    //
    // Split from RandomPlacement because the two happen at completely different rates and confusing
    // them is how the patch ended up glued to the camera: the spot is chosen ONCE, when the aurora
    // begins, from the view rectangle at that moment — after which it is a piece of sky at fixed map
    // coordinates and the camera must never be consulted again. Only the wander changes per frame.
    public static AuroraSheetPlacement WithDrift(in AuroraSheetPlacement placed, float driftPhase)
    {
        return new AuroraSheetPlacement(
            placed.CenterX + PatchDriftCells * driftPhase,
            placed.CenterZ,
            placed.Width,
            placed.Height,
            placed.UScale,
            placed.VScale,
            placed.UPhase,
            placed.Alpha);
    }

    private static float ScaleFrom(int seed, int salt) =>
        PatchMinScale + (1f - PatchMinScale) * Hash01(seed, salt);

    private static float PlaceWithin(int seed, int salt, float low, float high, float margin)
    {
        float lo = low + margin;
        float hi = high - margin;

        // A view too small to hold the patch: centre it and accept the overhang rather than placing it
        // at a nonsense coordinate. Only reachable on a heavily zoomed-in camera.
        if (hi <= lo)
            return (low + high) * 0.5f;

        return lo + (hi - lo) * Hash01(seed, salt);
    }

    // Small deterministic hash, so a given aurora always lands in the same place. Same shape as
    // AuroraNoise's, masked to 24 bits so the result is exactly representable in a float.
    private static float Hash01(int seed, int salt)
    {
        unchecked
        {
            uint h = (uint)(seed * 374761393 + salt * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) * (1f / 16777216f);
        }
    }

    // How many quads this field wants over a map of this height.
    //
    // A field whose sheets span the map (the contour field) gets exactly one quad per declared sheet —
    // it is an overhead view and covering the ground is the point. A bounded field gets as many as fit
    // with SheetSpacing's worth of clear sky each, clamped to at least one so a pocket map still shows
    // an aurora.
    public static int PlacementCount(AuroraFieldSpec spec, int mapZ)
    {
        if (SpansMap(spec))
            return spec.Sheets.Length;

        float sheetHeight = spec.Sheets[0].CellsPerRepeatY;
        if (sheetHeight <= 0f)
            return 1;

        int fits = (int)Math.Round(mapZ / (sheetHeight * SheetSpacing));
        return Clamp(fits, 1, MaxSheets);
    }

    // How far a bounded patch wanders east-west from its home position, in map cells.
    //
    // A bounded patch CANNOT be moved by panning its UVs, and that is worth stating plainly because it
    // is the obvious thing to try and it fails in a way that looks like a different bug. The taper is
    // baked into the texture at u = 0 and u = 1; with a non-zero UV offset and Repeat wrapping, those
    // feathered edges slide into the MIDDLE of the quad while the quad's own edges land on mid-field
    // content and are cut off square. The live run showed exactly that — a hard vertical line down one
    // side of an otherwise soft patch.
    //
    // So the patch keeps a fixed UV window and MOVES INSTEAD, which is what a real display does anyway.
    // Trimmed from 26: the drift has to be small enough that a patch framed in view cannot wander out
    // of it. At 80 cells wide inside a ~120-cell view there are only ~20 cells of slack either side.
    public const float PatchDriftCells = 5f;

    // driftPhase is a triangle wave in [-1, 1] rather than an unbounded slide, for the same reason the
    // hems oscillate: a patch that drifts forever eventually reaches the map edge, where it would be
    // clipped by geometry rather than fading by its own taper.
    public static AuroraSheetPlacement Placement(
        AuroraFieldSpec spec, int index, int mapX, int mapZ, float driftPhase = 0f)
    {
        return SpansMap(spec)
            ? SpanningPlacement(spec.Sheets[index], mapX, mapZ)
            : BoundedPlacement(spec.Sheets[0], index, mapX, mapZ, driftPhase);
    }

    // The whole-map case, which is the old behaviour expressed as a placement: one quad covering the
    // map, tiling in both axes. Kept because the contour field genuinely wants it.
    private static AuroraSheetPlacement SpanningPlacement(AuroraSheetSpec sheet, int mapX, int mapZ)
    {
        return new AuroraSheetPlacement(
            mapX * 0.5f, mapZ * 0.5f,
            mapX, mapZ,
            mapX / sheet.CellsPerRepeatX,
            mapZ / sheet.CellsPerRepeatY,
            uPhase: 0f,
            alpha: sheet.Alpha);
    }

    private static AuroraSheetPlacement BoundedPlacement(
        AuroraSheetSpec sheet, int index, int mapX, int mapZ, float driftPhase)
    {
        int slot = index % MaxSheets;

        // Clamped to the map, because a patch wider than the map it sits on cannot show its own taper —
        // the fade would happen off the edge and the player would see a hard cut, which is the whole
        // defect the feather exists to remove. Pocket and quest maps get down to 75 cells, well under a
        // patch's natural size. Shrinking the quad while the UV scale stays at one repeat simply draws
        // the same display smaller, which is the right answer for a small map anyway.
        float height = Math.Min(sheet.CellsPerRepeatY, mapZ);
        float width = Math.Min(sheet.CellsPerRepeatX, mapX);

        // Alternate sheets drift in opposite directions, so two patches on screen never move as one
        // rigid picture — the same argument as the mirrored u scale, applied to motion.
        float wander = PatchDriftCells * driftPhase * (Mirrored[slot] ? -1f : 1f);

        return new AuroraSheetPlacement(
            CentreOnAxis(CentreFractionsX[slot], width, mapX) + wander,
            CentreOnAxis(CentreFractions[slot], height, mapZ),
            width,
            height,
            // Exactly one HORIZONTAL repeat too, mirrored for alternate sheets. One repeat is what lets
            // the field's own u feather reach both edges of the quad, which is what makes the patch
            // taper away instead of being sliced off.
            Mirrored[slot] ? -1f : 1f,
            // Exactly one vertical repeat. Not "about one" — this is the invariant.
            vScale: 1f,
            UPhases[slot],
            Alphas[slot] * sheet.Alpha);
    }

    // Keeps a sheet inside the map where it can, and centres it when it cannot.
    //
    // Computed in FLOATS from Size, never from Map.Center, which is `Size.x / 2` in integer arithmetic
    // and therefore half a cell off true centre on every even-sized map — and every stock RimWorld map
    // size is even.
    private static float CentreOnAxis(float fraction, float extent, int mapExtent)
    {
        if (extent >= mapExtent)
            return mapExtent * 0.5f;

        float half = extent * 0.5f;

        // Inset by the drift amplitude as well as the half-extent, so a patch that has wandered to the
        // end of its travel is still wholly on the map and still shows its own taper rather than being
        // clipped by the map edge.
        float margin = half + PatchDriftCells;
        if (margin * 2f >= mapExtent)
            return mapExtent * 0.5f;

        return Clamp(fraction * mapExtent, margin, mapExtent - margin);
    }

    private static bool SpansMap(AuroraFieldSpec spec) => spec.Sheets[0].SpansMapVertically;

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}
