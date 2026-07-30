namespace CelestialLighting;

// Pure description of an aurora field: everything AuroraCurtainOverlay needs in order to bake and
// draw one, and nothing about Unity. Same rule and reason as AuroraMath.cs / AuroraNoise.cs — compiled
// into Source (net481), Tests (net8.0) and Tools/AuroraPreview (net8.0) through a linked
// <Compile Include>, so the shipped description is the tested and previewed one.
//
// WHY THIS EXISTS. There are two fields — AuroraCurtain (the physically honest overhead contour) and
// AuroraCurtainHemRays (the art-directed side-on curtain that §11a ships). The adapter used to read
// eleven separate members straight off AuroraCurtain, and only four of them had hem-rays equivalents.
// The mismatches were not cosmetic: the contour field wants two counter-panning layers at different
// scales, hem-rays wants exactly one because three curtains already live inside its tile and a second
// plane would put contradictory hems on screen. The contour field wants an isotropic UV scale, because
// its anisotropy comes from its lattice periods and stretching the UVs too would smear it; hem-rays
// wants an explicitly anisotropic one. They even wrap their drift clocks on different periods.
//
// Encoding that as a data description rather than as branches means adding a third field is a table
// entry, and — more useful day to day — the previewer, the cost probe and the game all read the same
// numbers instead of three drifting copies of them.
public delegate void AuroraFillRows(
    byte[] rgba, int width, int height, int firstRow, int rowCount, float time,
    float tintR, float tintG, float tintB, float tintWeight);

// One drawn plane of a field.
//
// Called a SHEET rather than a layer because that is what it is now: a bounded quad standing in the
// sky over part of the map, not a texture stretched across the whole world. See AuroraSheetLayout.
public sealed class AuroraSheetSpec
{
    // Map cells spanned by one repeat of the texture, per axis. Separate because the two fields
    // genuinely disagree about whether that should be square — see the class comment.
    public readonly float CellsPerRepeatX;
    public readonly float CellsPerRepeatY;

    // UV offset per GAME TICK. Per tick rather than per second so the drift obeys pause and game speed,
    // and computed from the tick count rather than accumulated, so it is reproducible: the same tick
    // always yields the same pan, which is the only reason a harness scenario can screenshot this.
    public readonly float PanU;
    public readonly float PanV;

    // This sheet's share of the alpha. Below 1 for anything meant to read as depth behind the dominant
    // sheet rather than as a competing aurora.
    public readonly float Alpha;

    // Whether this sheet is stretched over the whole map (contour: true — it is an overhead view, so
    // covering the ground is the point and vertical repeats are unobjectionable) or drawn at exactly
    // one vertical repeat (hem-rays: false — v is ALTITUDE up the curtain, so repeating it would stamp
    // the same three arcs up the map).
    public readonly bool SpansMapVertically;

    public AuroraSheetSpec(
        float cellsPerRepeatX, float cellsPerRepeatY, float panU, float panV, float alpha,
        bool spansMapVertically)
    {
        CellsPerRepeatX = cellsPerRepeatX;
        CellsPerRepeatY = cellsPerRepeatY;
        PanU = panU;
        PanV = panV;
        Alpha = alpha;
        SpansMapVertically = spansMapVertically;
    }
}

public sealed class AuroraFieldSpec
{
    public readonly string Name;

    // Baked texture size. Non-square is allowed and deliberate: FillRows has always taken width and
    // height separately and both fields normalise u = x/width, v = y/height, so the pure core has
    // supported it all along — only the adapter ever assumed square.
    public readonly int ResolutionX;
    public readonly int ResolutionY;

    // How far §11's driver colour is allowed to pull this field's palette. Lives here rather than on
    // AuroraCurtain because the two fields want different amounts of it.
    public readonly float TintWeight;

    // Tick count at which the field's drift wraps bit-exactly. Consumed for BOTH the field clock and
    // the pan wrap, so it must travel with the field: the two fields wrap on different periods
    // (1,400,000 and 4,000,000) and using one field's number with the other's maths would put a visible
    // discontinuity in the sky some hours into a colony.
    public readonly int DriftWrapTicks;

    // Rows rebaked per frame. See AuroraCurtainOverlay for why this is a rolling slice rather than a
    // whole-tile bake.
    public readonly int RefreshRows;

    public readonly AuroraFillRows Fill;

    // Whether this field's bake can hoist per-column work across a whole refresh sweep. True only for
    // hem-rays, whose noise is a function of u alone; the contour field samples 2D noise per pixel and
    // has nothing to hoist. The adapter reads this to decide whether to hold a column table; the two
    // paths produce identical pixels, which AuroraCurtainHemRaysTests asserts byte for byte.
    public readonly bool CachesColumnTable;

    public readonly AuroraSheetSpec[] Sheets;

    public AuroraFieldSpec(
        string name, int resolutionX, int resolutionY, float tintWeight, int driftWrapTicks,
        int refreshRows, AuroraFillRows fill, AuroraSheetSpec[] sheets, bool cachesColumnTable = false)
    {
        CachesColumnTable = cachesColumnTable;
        Name = name;
        ResolutionX = resolutionX;
        ResolutionY = resolutionY;
        TintWeight = tintWeight;
        DriftWrapTicks = driftWrapTicks;
        RefreshRows = refreshRows;
        Fill = fill;
        Sheets = sheets;
    }

    public int PixelCount => ResolutionX * ResolutionY * 4;
}

// Which field is which, and which one ships.
//
// There is deliberately no user-facing setting yet. The contour field stays compiled, tested and
// rendered by the previewer, but is not offered in the settings screen until §11b (the world-map
// auroral oval) exists — because from a top-down camera it is physically correct and visually
// unrecognisable, so a player choosing it from a radio button would reasonably conclude the aurora is
// broken. On the world map, viewed from orbit, an overhead contour is simply what an aurora looks
// like, and at that point the option means something.
public static class AuroraFieldRegistry
{
    // Hem-rays: one sheet. Three curtains already live inside its tile, so a second plane at another
    // scale would put a second set of hems on screen disagreeing with the first about where the sky is.
    public static readonly AuroraFieldSpec HemRays = new AuroraFieldSpec(
        "hemrays",
        AuroraCurtainHemRays.Resolution,
        AuroraCurtainHemRays.Resolution,
        AuroraCurtainHemRays.DriverTintWeight,
        AuroraCurtainHemRays.DriftWrapTicks,
        refreshRows: 6,
        AuroraCurtainHemRays.FillRows,
        new[]
        {
            new AuroraSheetSpec(
                AuroraCurtainHemRays.CellsPerRepeatX,
                AuroraCurtainHemRays.CellsPerRepeatY,
                AuroraCurtainHemRays.PanU,
                AuroraCurtainHemRays.PanV,
                alpha: 1f,
                spansMapVertically: false),
        },
        cachesColumnTable: true);

    // Contour: two counter-panning sheets over the whole map. A single panning plane translates
    // rigidly, and rigid translation of the whole sky reads as the camera moving rather than the aurora
    // moving; two at different scales and rates make the sum move non-uniformly across the frame.
    public static readonly AuroraFieldSpec Contour = new AuroraFieldSpec(
        "contour",
        AuroraCurtain.Resolution,
        AuroraCurtain.Resolution,
        AuroraCurtain.DriverTintWeight,
        AuroraCurtain.DriftWrapTicks,
        refreshRows: 6,
        AuroraCurtain.FillRows,
        new[]
        {
            new AuroraSheetSpec(
                AuroraCurtain.Layer1CellsPerRepeat, AuroraCurtain.Layer1CellsPerRepeat,
                AuroraCurtain.Layer1PanU, AuroraCurtain.Layer1PanV,
                alpha: 1f, spansMapVertically: true),
            new AuroraSheetSpec(
                AuroraCurtain.Layer2CellsPerRepeat, AuroraCurtain.Layer2CellsPerRepeat,
                AuroraCurtain.Layer2PanU, AuroraCurtain.Layer2PanV,
                alpha: AuroraCurtain.Layer2Alpha, spansMapVertically: true),
        });

    // The shipping field. A property rather than a const so the eventual settings enum has one place to
    // change, and so tests can state which field they are exercising.
    public static AuroraFieldSpec Active => HemRays;
}
