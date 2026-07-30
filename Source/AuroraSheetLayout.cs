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
    public const float SheetSpacing = 1.6f;

    // Where sheets sit up the map, as fractions of its height, in the order they are added.
    //
    // Deliberately IRREGULAR. Evenly spaced sheets are periodicity wearing a different hat: three
    // curtains at 0.25/0.50/0.75 would read as one pattern repeating, which is the exact defect this
    // file exists to remove. These gaps are 0.36, 0.20 and 0.44 of map height — no two alike, and none
    // a simple multiple of another.
    private static readonly float[] CentreFractions = { 0.30f, 0.66f, 0.86f, 0.42f };

    // Descending, so the sky has one dominant display with fainter ones behind it rather than several
    // arguing about which is the subject.
    private static readonly float[] Alphas = { 1f, 0.72f, 0.50f, 0.35f };

    // Irrational-ish spacing rather than 0, 0.25, 0.5, 0.75, for the same reason as CentreFractions.
    private static readonly float[] UPhases = { 0f, 0.37f, 0.71f, 0.13f };

    // Alternating, so adjacent sheets are mirror images and the eye cannot match their hems.
    private static readonly bool[] Mirrored = { false, true, false, true };

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

    public static AuroraSheetPlacement Placement(AuroraFieldSpec spec, int index, int mapX, int mapZ)
    {
        return SpansMap(spec)
            ? SpanningPlacement(spec.Sheets[index], mapX, mapZ)
            : BoundedPlacement(spec.Sheets[0], index, mapX, mapZ);
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
        AuroraSheetSpec sheet, int index, int mapX, int mapZ)
    {
        int slot = index % MaxSheets;

        float height = sheet.CellsPerRepeatY;
        float uScale = mapX / sheet.CellsPerRepeatX;

        return new AuroraSheetPlacement(
            mapX * 0.5f,
            CentreZ(CentreFractions[slot], height, mapZ),
            mapX,
            height,
            Mirrored[slot] ? -uScale : uScale,
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
    private static float CentreZ(float fraction, float height, int mapZ)
    {
        if (height >= mapZ)
            return mapZ * 0.5f;

        float half = height * 0.5f;
        return Clamp(fraction * mapZ, half, mapZ - half);
    }

    private static bool SpansMap(AuroraFieldSpec spec) => spec.Sheets[0].SpansMapVertically;

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}
