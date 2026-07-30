using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types, same rule and reason as AuroraMath.cs /
// AuroraNoise.cs / AuroraCurtain.cs: compiled into Source (net481), Tests (net8.0) and
// Tools/AuroraPreview (net8.0) through linked <Compile Include>, so the shipped generator is the
// tested and previewed generator.
//
// ================================================================================================
// WHAT THIS IS, AND WHY THERE ARE TWO OF THEM
//
// This is a SECOND, ALTERNATIVE shape function for §11a's aurora curtain (DESIGN.md §11a, issue
// #42). AuroraCurtain.cs is untouched and still the shipping one; this file exists to be compared
// against it side by side, not to replace it unreviewed. The output contract is deliberately
// identical (FillRows into an RGBA32 buffer, same argument list) so the adapter could adopt either
// with a one-line change.
//
// AuroraCurtain draws the CONTOUR where two counter-drifting fBm fields are equal. That is the
// physically honest answer for RimWorld's fixed top-down orthographic camera: seen from directly
// overhead, an auroral arc really is a flat wandering squiggle, and the vertical curtain structure
// everyone pictures is a side-on phenomenon we have no viewing angle for. The Godot shader the
// technique came from got the iconic look back by raymarching, which buys nothing here — there is
// no view ray to smear along and no parallax to sell.
//
// The complaint that produced this file is that the honest answer does not look like an aurora.
// So this one gives up on deriving the silhouette from a projection and AUTHORS IT DIRECTLY IN 2D:
// a bright wandering hem line near the bottom of each band, brightness falling off as you rise
// above it, and vertical striations cut into that falloff. It is a poster of an aurora rather than
// a simulation of one, and that trade is the whole thing being evaluated.
//
// ================================================================================================
// THE CONSTRUCTION, IN ONE PARAGRAPH
//
// Everything is built from fields of u ALONE — one dimension. A hem curve B(u) gives the v position
// of the band's bright lower edge. A ray field R(u) at much higher frequency modulates brightness.
// Because R varies in u and not in v, it streaks vertically BY CONSTRUCTION: no anisotropic noise
// tuning, no smearing, no luck. Vertical position enters only through s = v - B(u), the signed
// height above the hem, which drives an analytic falloff and the palette. Three such curtains at
// different hem heights, wander periods, ray densities and drift rates are summed additively, so
// where they overlap they brighten — the "overlap in terms of volume" the brief asked for.
//
// ================================================================================================
// WHY THIS IS DRAMATICALLY CHEAPER, WHICH IS THE OTHER HALF OF THE PITCH
//
// AuroraCurtain costs ~9 two-dimensional noise samples for EVERY PIXEL (warp 1 + two 2-octave
// ribbon fBms 4 + envelope 2 + hue 2), and that measured 1423 microseconds per frame on Mono at
// 128², which is what forced the drop to 96².
//
// Here every noise sample is a function of u only, so an entire texture COLUMN shares them. FillRows
// evaluates them once per column into a scratch table and the per-pixel inner loop then does
// arithmetic only — zero noise samples, zero hashing. A whole 192-wide tile costs 192 x 19 = 3648
// samples however tall it is, against 192 x 192 x 9 = 331,776 for the contour field at the same size.
// Per pixel that is 0.10 samples versus 9, and the offline previewer measures the whole-tile bake at
// roughly a quarter of the contour field's despite covering four times the area.
//
// Two honest caveats, because they are what decide whether the saving is real:
//
//   * The adapter fills SIX ROWS AT A TIME (AuroraCurtainOverlay.RowsPerUpdate), and the column table
//     must be rebuilt each call because `time` has advanced. So in the shipped call pattern the
//     amortisation is over 6 rows, not 192 — ~3.2 samples per pixel rather than 0.10. Getting the
//     rest back means letting the adapter bake the whole tile every 32 frames instead of a slice
//     every frame (identical average cost; the field is smooth enough in time that nobody could see
//     the difference), which is a change to AuroraCurtainOverlay this prototype deliberately does not
//     make.
//
//   * With the noise hoisted, per-pixel ARITHMETIC becomes the dominant cost, so resolution is now
//     the expensive knob — the exact opposite of the contour field. That is why the inner loop has no
//     divisions and no floating-point modulo left in it, and why every constant that could be a
//     reciprocal is one.
//
// ================================================================================================
// TILEABILITY, WHICH IS NON-NEGOTIABLE
//
// The field is baked small and UV-panned forever, so u and u+1 must be bit-identical or a hard seam
// sweeps the map once per pan cycle. Two mechanisms carry that here:
//
//   * HORIZONTALLY: every field is an AuroraNoise sample at an integer lattice period, which wraps
//     by construction. Nothing else varies with u.
//
//   * VERTICALLY: the height above the hem is taken as a WRAPPED difference (see
//     SignedHeightAboveHem). That makes v-periodicity automatic rather than something the hem
//     positions have to be tuned to respect — a curtain whose rays run off the top of the tile
//     simply reappears at the bottom, which is a real second arc rather than an artifact. The only
//     thing the constants must respect is that the falloff reaches exactly zero before it wraps
//     round into its own hem, which RayHeightMax + HemUnderhang < 1 guarantees.
public static class AuroraCurtainHemRays
{
    // ---- Time and drift --------------------------------------------------------------------------

    // Lattice units per GAME TICK, matching AuroraCurtain.DriftRate exactly. Not a coincidence and not
    // laziness: these two fields are going to be A/B'd against each other in the live harness, and a
    // different base rate would make "which one moves better" unanswerable because they would not be
    // moving at comparable speeds. Per tick rather than per second for the same reasons AuroraCurtain
    // documents at length — game time, pause-aware, and reproducible under the harness's TicksGame
    // jumps, which an accumulated delta-time would sit frozen through.
    public const float DriftRate = 0.0006f;

    // Drift is wrapped here before it reaches the noise, solving the same float-precision problem
    // AuroraCurtain.DriftWrapCycle solves: drift grows without bound with TicksGame and a float's
    // resolution degrades as it does, so a century-old colony would see the field quantize into visible
    // steps. Wrapping keeps the value small and the increments smooth forever.
    //
    // WHY 2400, AND WHY THE WRAP IS BIT-EXACT RATHER THAN MERELY UNNOTICEABLE. This is NOT
    // AuroraCurtain's 840, because the coefficients and periods here are different and reusing its
    // number would silently put a discontinuity in the field. The arithmetic redone from scratch:
    //
    //   1. Every lattice period in this file divides 120 (drawn from 2, 3, 4, 5, 6, 8, 12, 30, 40, 60).
    //   2. Every HORIZONTAL drift coefficient is a multiple of 0.25, so the shift a wrap applies is
    //      2400 * 0.25n = 600n, and 600 = 5 x 120 is a multiple of every period. Octave doubling
    //      preserves that: octave 2 shifts by twice as much against twice the period.
    //   3. Every VERTICAL (hem-rise) coefficient is a multiple of 1/32, so the wrap shifts the hem by
    //      2400 / 32 = 75 whole tiles — and the hem only ever matters modulo 1, so that is a no-op.
    //   4. 0.25 and 1/32 are both exactly representable in binary floating point, and so is 2400. So
    //      the products above are exact, not approximately exact, and the wrap is bit-identical.
    //
    // Choosing coefficients from a power-of-two grid is the improvement over AuroraCurtain's 1/20 grid
    // (0.35, 0.6, ...), where the coefficients themselves are inexact floats and the wrap is only
    // invisible rather than exact. If any period or coefficient in this file changes, redo this.
    public const float DriftWrapCycle = 2400f;

    // DriftWrapCycle expressed back in ticks: 2400 / 0.0006. The ADAPTER must wrap the tick count with
    // this, in INTEGER arithmetic, before it ever converts to float. That is a second and completely
    // separate precision problem from the one above: a float carries a 24-bit mantissa and so stops
    // representing every integer past 16,777,216 (~278 in-game days), after which consecutive ticks
    // collapse onto the same float and the aurora advances in jerks and then stops. 4,000,000 sits
    // comfortably inside that range forever.
    public const int DriftWrapTicks = 4000000;

    // ---- Curtain silhouette ----------------------------------------------------------------------

    // How far BELOW its hem a curtain still emits, as a fraction of the tile height. An aurora's lower
    // border is its sharpest feature — that is the altitude where the incoming electrons stop, so the
    // emission simply ends — and this is the width of that edge. Small, but not zero: at exactly zero
    // the edge is a one-texel step, which bilinear filtering turns into a stair-stepped diagonal as the
    // hem wanders. Two or three texels of ramp reads as "sharp" and antialiases itself.
    public const float HemUnderhang = 0.022f;

    // Height of the extra always-on brightness ridge sitting on the hem, and how much it adds. This is
    // the "base line at the bottom" of the brief, and it is deliberately NOT ray-modulated: the rays
    // are gaps as much as they are stripes, and without a continuous ridge underneath them the hem
    // dissolves into a row of disconnected blobs instead of reading as one curtain seen edge-on.
    //
    // The height was 0.038 and the gain 0.55, which previewed as a hard bright wire — a horizon line
    // rather than the bottom of something luminous. Trading a little gain for more height turns it into
    // a glow that the rays grow out of, which is the join the whole silhouette depends on.
    public const float HemCoreHeight = 0.055f;
    public const float HemCoreGain = 0.46f;

    // The tallest any curtain's rays reach, as a fraction of tile height. Only a bound: each curtain
    // has its own RayHeight, and each individual RAY tops out somewhere below its curtain's (see
    // RayTopFloor). Exists so the tileability constraint can be stated as one comparison —
    // RayHeightMax + HemUnderhang < 1 is what guarantees a curtain's tail dies out before it wraps
    // round into its own hem and the field stops being periodic in v for the wrong reason.
    public const float RayHeightMax = 0.97f;

    // Brightness a column keeps where the ray field says "gap", in [0, 1]. At 0 the gaps are black and
    // the curtain is a picket fence with nothing behind it; at 1 there are no rays at all. 0.46 leaves
    // a coherent sheet with rays picked out on top of it, which is what an active curtain looks like.
    // Raised from 0.32 after the first preview, where the sheet vanished and the curtain read as a
    // bright line with a few whiskers attached.
    public const float RayFloor = 0.46f;

    // How far the ray field is pushed toward AuroraCurtain's CC0 power chain, in [0, 1]. 0 is raw value
    // noise, which is a gentle sinusoidal brightness wobble rather than distinct lines; 1 is the full
    // chain, which is right for a contour and wrong here — it kept only the field's top few percent, so
    // a window that should show ~24 rays showed six isolated spikes. 0.62 keeps a defined core on the
    // bright rays while letting the many mid-strength ones stay visible, which is what makes a curtain
    // look striated rather than spiked.
    public const float RaySharpen = 0.62f;

    // Mix between a linear and a squared falloff above the hem (0 = linear, 1 = squared). Pure squared
    // was the first attempt and it put almost nothing above the bottom fifth of the curtain, so the
    // rays had no sheet to rise out of. The blend keeps the hem clearly brightest without emptying the
    // body — see VerticalFalloff.
    public const float FalloffCurvature = 0.62f;

    // Shortest a ray can be as a fraction of its curtain's RayHeight. This is what stops the rays
    // running the full height in unison and gives the curtain a ragged top edge.
    //
    // Ray LENGTH is driven by its own noise field rather than reusing the brightness field, which was
    // the first attempt. Brighter-means-taller is defensible physics (both track the same electron
    // flux) but it renders badly: with the two perfectly correlated, only the bright rays are tall, so
    // they stand out of the sheet as isolated spikes and the silhouette reads as an audio waveform.
    // Decorrelating them costs one more per-column sample and gives a genuinely ragged top.
    public const float RayTopFloor = 0.34f;

    // How deeply the coarse "bundle" field can dim a stretch of rays, in [0, 1]. Rays on a real curtain
    // are not evenly spaced pickets — they come in bundles with quieter sheet between, and without this
    // the striations sit at one frequency across the whole sky and read as corduroy. The bundle field
    // runs at a fifth of the ray frequency (RayClumpPeriod below), which is enough to group them
    // without becoming a second, competing set of rays.
    // Trimmed from 0.55: any field that varies only in u modulates a whole column at once, so at
    // bundle scale a deep one draws rectangular blocks of differing brightness with hard vertical
    // seams — the same mechanism that makes the rays work, one octave too coarse. 0.38 groups the rays
    // without the blocks becoming legible as blocks.
    public const float RayClumpDepth = 0.38f;

    // ---- Palette ---------------------------------------------------------------------------------

    // Hue selector at the hem, and how far it travels per unit of RayHeight climbed. Fed to
    // AuroraCurtain.PaletteColor, whose scale is violet below 0.30, pure oxygen green from 0.30 to
    // 0.66, and oxygen red above.
    //
    // THIS IS THE PART THAT IS MORE PHYSICAL THAN THE CONTOUR FIELD, not less. AuroraCurtain picks hue
    // from a 2D fBm, so its colours vary across the sky for no reason beyond "noise". Real auroral
    // colour is stratified BY ALTITUDE: the 630 nm oxygen red line only radiates high up where
    // collisions are rare enough for its 110-second metastable state to survive, 557.7 nm green
    // dominates the body, and the N2+ violet fringe hugs the sharp lower border. Height above the hem
    // is exactly the coordinate that ought to drive hue, and here we have it for free.
    // The rise was 0.46, i.e. exactly enough to reach full red at the very top of a curtain, which is
    // the physically tidy answer and renders as nothing at all: the top of a curtain is also where the
    // falloff has taken the alpha to zero, so the red was always painted where nobody could see it.
    // 0.78 puts the transition around half height, where there is still light to colour.
    public const float HueAtHem = 0.46f;
    public const float HueRisePerHeight = 0.78f;

    // How much faster hue falls below the hem than it rises above it. The violet fringe is a thin
    // band right at the border, not a gradient, so the underhang's ~0.02 of tile has to cover most of
    // the distance from green (0.46) to violet (<0.30) on its own.
    public const float HueFallBelowHem = 3.2f;

    // A slow along-the-curtain hue wobble on top of the altitude stratification, so one curtain is not
    // a single vertical gradient repeated identically across the whole sky. Small — the point of the
    // stratification above is that hue should mostly mean height.
    public const int HueWobblePeriod = 4;
    public const float HueWobbleAmplitude = 0.09f;
    public const float HueWobbleDrift = 0.25f;

    // Unchanged from AuroraCurtain so the two fields answer a driver tint identically and an A/B is
    // comparing shapes rather than colour-blend strengths.
    public const float DriverTintWeight = 0.3f;

    // ---- Layer geometry --------------------------------------------------------------------------
    //
    // Lives in the pure core rather than in the adapter for the reason AuroraCurtain states:
    // Tools/AuroraPreview can only be trusted to show what the game shows if it reads the same numbers.

    // wholeMapPlane's UVs are pre-multiplied by 200 over 2000 world units. A mesh fact, restated rather
    // than referenced so this file has no dependency on the alternative it is competing with.
    public const float PlaneRepeatsPerCell = 0.1f;

    // Map cells spanned by one tile repeat. ANISOTROPIC, which AuroraCurtain deliberately is not, and
    // the reason is the whole difference between the two approaches. The contour field is isotropic
    // noise whose anisotropy comes from its lattice periods, so stretching its UVs as well would
    // double up and smear it. This field is authored with an explicit up direction, and its two axes
    // want opposite things: WIDE horizontally, so the tile's horizontal repeat falls outside the
    // ~120-cell visible window and the player never sees the same hem twice on one screen; SHORT
    // vertically, so more than one curtain fits on screen at once — which is the entire point of
    // having three.
    // 200 x 76 gives rays 5 cells wide and curtains ~26 cells tall (a 1:5 silhouette, which is what a
    // ray looks like), fits all three curtains inside a ~68-cell screen, and puts the horizontal repeat
    // outside that screen. The vertical repeat lands just inside it, which is fine and arguably right:
    // vertically the tile is three DIFFERENT curtains, so its repeat reads as a fourth arc further
    // north rather than as a repeat.
    public const float CellsPerRepeatX = 150f;
    public const float CellsPerRepeatY = 76f;

    // Tile repeats per game tick. Horizontal only: the field already moves itself vertically, per
    // curtain and at different rates (HemRise below), and that is non-rigid, whereas a vertical pan
    // would slide all three curtains north in lockstep and read as the camera moving. 5e-5/tick
    // advances one repeat (200 cells) every 20,000 ticks — about 20 cells an hour, weather rather than
    // scrolling background.
    public const float PanU = 5e-5f;
    public const float PanV = 0f;

    // Edge length of the baked texture. 192 rather than AuroraCurtain's 96 — and the extra resolution
    // is BOUGHT WITH the sampling saving rather than spent on top of it, which is the trade this whole
    // approach is offering.
    //
    // The contour field can afford 96 because it is deliberately soft and large; this one cannot,
    // because a RAY is a high-frequency feature in u by definition. At 96 texels the widest ray period
    // here (40) puts a ray core at 2.4 texels, which bilinear filtering rounds into a blob and the
    // striations stop reading as striations. 192 gives 4.8, and against a 200-cell tile that is roughly
    // one texel per map cell, which is as sharp as this effect can usefully be.
    //
    // Not a power of two, which is fine: no mip chain is generated and Repeat wrap has no NPOT
    // restriction on any platform RimWorld ships to. Memory is 192^2 x 4 = 147 KB.
    //
    // Note where the cost now lives. With the noise hoisted per column, the field's price is almost
    // entirely per-pixel arithmetic, so resolution is the ONLY expensive knob left — the opposite of
    // the contour field, where samples-per-pixel dominates and resolution is comparatively cheap.
    public const int Resolution = 192;

    // ---- Seeds ------------------------------------------------------------------------------------
    // Fixed rather than per-game, for the reason AuroraCurtain gives: a fixed lattice is the only thing
    // that lets a harness scenario screenshot pin this effect at all.
    private const int HueWobbleSeed = 61001;

    // Peak the ray sharpener can return, used to renormalise it back to [0, 1]. Precomputed because
    // Amplify is a chain of multiplies and this is a loop-invariant.
    private static readonly float PeakAmplified = AuroraCurtain.Amplify(1f);

    // Reciprocals of the two fixed vertical scales, so the pixel loop multiplies instead of dividing.
    private const float InvHemUnderhang = 1f / HemUnderhang;
    private const float InvHemCoreHeight = 1f / HemCoreHeight;

    // Everything that distinguishes one curtain from another. A struct in an array rather than three
    // copies of the same code with different constants, because the interesting knob is the
    // RELATIONSHIP between the three (do their hems collide? do they drift apart?) and that is only
    // legible if they are written as a table.
    public readonly struct CurtainSpec
    {
        // Base v position of the hem, before wander and before vertical drift.
        public readonly float HemCenter;

        // Lattice period and octave count of the hem wander, and its amplitude in tile heights. Low
        // period means few, broad folds — an auroral arc bends over hundreds of kilometres, it does
        // not zigzag. Two octaves adds a smaller kink inside each fold without making it scribble.
        public readonly int HemPeriod;
        public readonly int HemOctaves;
        public readonly float HemAmplitude;

        // Horizontal drift of the hem wander, in multiples of 0.25 (see DriftWrapCycle). This is what
        // makes the folds TRAVEL along the curtain rather than sitting still while the rays move.
        public readonly float HemDrift;

        // Vertical drift of the whole curtain, in multiples of 1/32 (see DriftWrapCycle). Different
        // per curtain and signed, so the three slowly cross each other and the overlaps between them
        // are never the same twice — that is where the sum-of-curtains construction earns its keep.
        public readonly float HemRise;

        // Lattice period of the ray striations and their horizontal drift. High period, because these
        // are the fine vertical lines. Drifting them faster than the hem is the shimmer: on a real
        // curtain the rays visibly race along the arc while the arc itself barely moves.
        public readonly int RayPeriod;
        public readonly float RayDrift;

        // Period of the coarse field that groups the rays into bundles — see RayClumpDepth. A fifth of
        // RayPeriod, and like every period here it must divide 120 for the drift wrap to stay exact.
        public readonly int RayClumpPeriod;

        // Very-low-period gate deciding which stretches of this curtain are lit at all, so a curtain
        // fades out along its length instead of running edge to edge at constant brightness.
        public readonly int EnvelopePeriod;
        public readonly float EnvelopeDrift;

        // How far this curtain's rays reach above its hem, in tile heights. Must not exceed
        // RayHeightMax.
        public readonly float RayHeight;

        // This curtain's share of the summed alpha. Descending across the three so one curtain reads
        // as near and dominant and the others as depth behind it, rather than three equal curtains
        // arguing about which is the subject.
        public readonly float Weight;

        // This curtain's colour, on AuroraCurtain.PaletteColor's scale, applied uniformly over the
        // whole sheet: below 0.30 slides violet, 0.30-0.66 is green, above 0.66 slides red.
        //
        // Flat per curtain, replacing the height-driven ramp. That ramp modelled the real
        // stratification — violet fringe, green body, red top, because the 630 nm line is emitted
        // higher than the 557.7 nm one — but it gave every curtain the same gradient, so three arcs
        // could not read as three different colours. This is an art-directed palette, chosen so the
        // curtains separate visually.
        public readonly float CurtainHue;

        public readonly int Seed;

        public CurtainSpec(
            float hemCenter, int hemPeriod, int hemOctaves, float hemAmplitude, float hemDrift,
            float hemRise, int rayPeriod, float rayDrift, int rayClumpPeriod, int envelopePeriod,
            float envelopeDrift, float rayHeight, float weight, float curtainHue, int seed)
        {
            CurtainHue = curtainHue;
            HemCenter = hemCenter;
            HemPeriod = hemPeriod;
            HemOctaves = hemOctaves;
            HemAmplitude = hemAmplitude;
            HemDrift = hemDrift;
            HemRise = hemRise;
            RayPeriod = rayPeriod;
            RayDrift = rayDrift;
            RayClumpPeriod = rayClumpPeriod;
            EnvelopePeriod = envelopePeriod;
            EnvelopeDrift = envelopeDrift;
            RayHeight = rayHeight;
            Weight = weight;
            Seed = seed;
        }
    }

    // Three, not two and not five. Two never overlap enough to show the additive brightening the brief
    // asked for; past three the tile fills up, the dark sky between arcs disappears and the whole thing
    // slides back toward the flat wash §11a exists to replace.
    //
    // The hem heights are spaced so that each curtain's tail lands near the next one's hem — arcs
    // stacked with a little overlap, which is both what a real multi-arc display looks like and where
    // the additive sum produces its brightest, most interesting pixels. The third is short, faint and
    // near the top of the tile precisely so its tail wraps round and grazes the first curtain's hem:
    // that wrap is invisible (see the class comment) and it means there is no "top of the sky".
    // Hem amplitudes are much larger than the first pass's 0.05: at 76 map cells to the tile that was
    // four cells of wander across the whole sky, which previewed as a spirit level rather than an arc.
    // 0.11 is ~8 cells, enough that a fold is visible inside one screen.
    // The bottom of this field's colour ramp.
    //
    // AuroraCurtain.NitrogenViolet is (0.35, 0.30, 1.00) — the honest N2+ 427.8 nm colour, and it
    // renders as blue rather than violet because that emission line genuinely IS blue-violet: its green
    // component almost equals its red, so nothing pulls it toward purple. That is right for the contour
    // field, which is the physically-honest path, and it is left untouched there.
    //
    // This field's palette is art-directed anyway (see CurtainSpec.CurtainHue), and a deep purple
    // separates the top curtain from the green one far better than a blue does. The trick is entirely
    // in the green channel: red well above green reads as purple, red near green as blue.
    public static readonly AuroraMath.Rgb CurtainPurple = new AuroraMath.Rgb(0.62f, 0.10f, 0.92f);

    // As AuroraCurtain.PaletteColor — same scale, same green plateau, same red ramp — but anchored on
    // CurtainPurple. Duplicated rather than parameterised because the contour field's version is one of
    // the things this file is being compared against, and a shared function taking a colour argument
    // would let a retune of one silently move the other.
    public static AuroraMath.Rgb PaletteColor(float hue01)
    {
        float h = Clamp01(hue01);

        if (h < AuroraCurtain.HueGreenLow)
            return LerpRgb(CurtainPurple, AuroraMath.OxygenGreen, h / AuroraCurtain.HueGreenLow);

        if (h > AuroraCurtain.HueGreenHigh)
            return LerpRgb(
                AuroraMath.OxygenGreen, AuroraMath.OxygenRed,
                (h - AuroraCurtain.HueGreenHigh) / (1f - AuroraCurtain.HueGreenHigh));

        return AuroraMath.OxygenGreen;
    }

    private static AuroraMath.Rgb LerpRgb(AuroraMath.Rgb a, AuroraMath.Rgb b, float t) =>
        new AuroraMath.Rgb(
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t);

    private static readonly CurtainSpec[] Curtains =
    {
        //             hemC   hemP oct hemAmp hemDrift hemRise   rayP rayDrift clump envP envDrift rayH  weight hue   seed
        new CurtainSpec(0.19f, 3, 2, 0.110f, 0.25f,  1f / 32f,   60, 1.00f,  12,  2, 0.25f,  0.97f, 1.00f, 0.46f, 10009),
        new CurtainSpec(0.57f, 5, 2, 0.085f, -0.50f, -2f / 32f,  40, -0.75f,  8,  3, -0.25f, 0.74f, 0.76f, 1.00f, 20011),
        new CurtainSpec(0.84f, 4, 2, 0.070f, 0.75f,  3f / 32f,   30, 0.50f,   6,  2, 0.50f,  0.49f, 0.52f, 0.06f, 30011),
    };

    // Read-only access to the table above, for the offline tests that check the invariants this file's
    // comments claim (every period divides 120, every drift coefficient sits on the right grid, no
    // curtain exceeds RayHeightMax). An indexer rather than the array itself, because a public array
    // field is writable by anyone who can see it and a mutated spec would break the drift-wrap proof
    // silently, hours into a run.
    public static int CurtainCount => Curtains.Length;

    public static CurtainSpec Curtain(int index) => Curtains[index];

    // What one texel column contributes, evaluated once per column and reused down the whole column.
    // Public because the previewer and the tests both want to inspect it directly — the per-column
    // split is the performance claim, so it has to be observable rather than an implementation detail.
    public readonly struct ColumnState
    {
        // Hem position in v, already wandered, already vertically drifted, and already wrapped into
        // [0, 1). Wrapping HERE rather than in the pixel loop is not tidiness — the vertical drift can
        // reach 225 tiles before DriftWrapCycle catches it, so an unwrapped hem would force a general
        // floating-point modulo per pixel, and a float `%` is a function call to fmod on both runtimes.
        // Pre-wrapped, the pixel loop's wrap is a compare and an add.
        public readonly float Hem;

        // Reciprocal of the height this column's ray reaches, and of its curtain's nominal RayHeight
        // (the hue coordinate). Reciprocals rather than the values, because the pixel loop divides by
        // both and there are three curtains — six divides per pixel was measurably the single largest
        // line item in the inner loop.
        public readonly float InvRayTop;
        public readonly float InvRayHeight;

        // Brightness multiplier from the ray field, in [RayFloor, 1], already folded together with the
        // along-the-curtain envelope gate and the curtain's weight. Three multiplies hoisted out of the
        // pixel loop for free, since none of the three vary with v.
        public readonly float RayWeight;

        // The same product without the ray modulation, for the continuous hem ridge.
        public readonly float CoreWeight;

        // This curtain's flat hue, carried through from its CurtainSpec. Constant in u, so it has no
        // business being per-column — but CurtainAt is handed a ColumnState and nothing else, and
        // copying one float costs nothing next to threading the spec into the pixel loop as well.
        public readonly float CurtainHue;

        public ColumnState(
            float hem, float invRayTop, float invRayHeight, float rayWeight, float coreWeight,
            float curtainHue)
        {
            Hem = hem;
            InvRayTop = invRayTop;
            InvRayHeight = invRayHeight;
            RayWeight = rayWeight;
            CoreWeight = coreWeight;
            CurtainHue = curtainHue;
        }
    }

    // Alpha and hue at a point. A plain struct out, per the pure-core rule — the caller turns hue into
    // a colour with AuroraCurtain.PaletteColor.
    public readonly struct Sample
    {
        public readonly float Alpha;
        public readonly float Hue;

        public Sample(float alpha, float hue)
        {
            Alpha = alpha;
            Hue = hue;
        }
    }

    // Base drift distance for a tick count, wrapped into [0, DriftWrapCycle). Every field scales this
    // by its own coefficient, which is what lets one wrap here keep all of them landing on exact
    // period multiples.
    //
    // Negative times cannot come from TicksGame, but the modulo is made sign-safe anyway: a negative
    // drift mirrors a field rather than merely offsetting it, and a test that feeds -1 should get a
    // sane field rather than a subtly reflected one.
    public static float Drift(float time)
    {
        float drift = time * DriftRate % DriftWrapCycle;
        return drift < 0f ? drift + DriftWrapCycle : drift;
    }

    // Field value at texture coordinate (u, v) at a given time. This is the reference definition; the
    // fast path in FillRows produces bit-identical results because it calls exactly these two helpers,
    // just at different rates.
    public static Sample At(float u, float v, float time)
    {
        float drift = Drift(time);
        float wobble = HueWobble(u, drift);

        float alpha = 0f;
        float hueWeighted = 0f;

        for (int i = 0; i < Curtains.Length; i++)
        {
            Sample s = CurtainAt(EvaluateColumn(Curtains[i], u, drift), v, wobble);
            alpha += s.Alpha;
            hueWeighted += s.Alpha * s.Hue;
        }

        return Resolve(alpha, hueWeighted, wobble);
    }

    // The per-column half of the field: everything that depends on u and not on v. This is the only
    // part that touches noise at all, and hoisting it is the whole performance story.
    public static ColumnState EvaluateColumn(in CurtainSpec c, float u, float drift)
    {
        // 1D value noise, obtained by pinning AuroraNoise's second axis to a period of 1 — with
        // yPeriod 1 both lattice rows are row 0, so the y interpolation is between identical values
        // and the result is exactly a wrapping 1D field. Reusing the shipped primitive rather than
        // writing a 1D twin of it is deliberate: the twin would be a second thing to keep tileable.
        // (It would also halve the hash count, since two of the four hashes here are recomputed
        // duplicates. Worth doing if this approach ships; not worth forking the primitive to prototype
        // it, and the samples are per-column anyway so the waste is already divided by the tile height.)
        float wander = Fbm1(u * c.HemPeriod + drift * c.HemDrift, c.HemPeriod, c.Seed, c.HemOctaves);

        // Vertical drift is applied here rather than to v, so it costs nothing and so the wrap
        // argument in DriftWrapCycle only has to hold modulo 1.
        float hem = c.HemCenter + (wander - 0.5f) * 2f * c.HemAmplitude + drift * c.HemRise;

        // One octave for the rays, not two. Two was the first guess and it is wrong for the same
        // reason AuroraCurtain dropped its third: an extra octave here halves the feature size, and at
        // this texture resolution the half-size features fall below what bilinear filtering can hold,
        // so all they contribute is mush on top of the rays that do survive.
        float rayRaw = Value1(u * c.RayPeriod + drift * c.RayDrift, c.RayPeriod, c.Seed + 701);

        // The CC0 power chain from AuroraCurtain, reused rather than reinvented, but only partway — see
        // RaySharpen. Its job there is to crush a soft contour gradient into a defined edge; its job
        // here is the same one dimension down, because value noise is smooth and rounded and raw it
        // gives a brightness wobble rather than lines.
        float raySharp = Lerp(rayRaw, AuroraCurtain.Amplify(rayRaw) / PeakAmplified, RaySharpen);

        // The bundle field multiplies the sharpened rays rather than being summed with them, so a
        // quiet stretch of curtain has faint rays rather than a different set of rays.
        float clump = Value1(u * c.RayClumpPeriod + drift * c.RayDrift, c.RayClumpPeriod, c.Seed + 907);
        float bundled = raySharp * (1f - RayClumpDepth + RayClumpDepth * clump);

        float rayMod = RayFloor + (1f - RayFloor) * bundled;

        float lengthRaw = Value1(u * c.RayPeriod + drift * c.RayDrift, c.RayPeriod, c.Seed + 1109);
        float rayTop = c.RayHeight * (RayTopFloor + (1f - RayTopFloor) * lengthRaw);

        float envelope = Value1(u * c.EnvelopePeriod + drift * c.EnvelopeDrift, c.EnvelopePeriod, c.Seed + 1301);
        float gate = EnvelopeGate(envelope) * c.Weight;

        // The Max guards are not defensive noise: 0*Infinity is NaN, and a NaN alpha propagates into
        // every overlapping curtain through the sum. A zero RayHeight is nonsense but it should produce
        // an invisible curtain, not a hole in the texture.
        return new ColumnState(
            Wrap01(hem),
            1f / Math.Max(rayTop, 1e-4f),
            1f / Math.Max(c.RayHeight, 1e-4f),
            rayMod * gate,
            HemCoreGain * gate,
            c.CurtainHue);
    }

    // The per-pixel half: pure arithmetic on a ColumnState, no noise, no hashing, no division.
    public static Sample CurtainAt(in ColumnState col, float v, float hueWobble)
    {
        float s = SignedHeightAboveHem(v, col.Hem);

        float alpha = s < 0f
            ? BelowHem(s, col)
            : AboveHem(s, col);

        // Flat hue: the sheet is one colour top to bottom. The wobble still rides on it so the colour
        // varies slightly ALONG the curtain, which keeps it from looking like a printed band.
        return new Sample(alpha, Clamp01(col.CurtainHue + hueWobble));
    }

    // Signed height of v above a hem, in tile heights, in [-HemUnderhang, 1 - HemUnderhang). Both
    // arguments must already be in [0, 1), which is why ColumnState pre-wraps the hem.
    //
    // Taking the difference MODULO THE TILE and then folding the top sliver down to negative is what
    // makes the whole field seamless in v for free: v and v+1 land on the same s by construction, so
    // no combination of hem positions and ray heights can produce a seam. The fold is also what gives
    // the curtain a below-the-hem side at all — without it every curtain would have a hard step from
    // black to full brightness at its hem, one texel wide.
    public static float SignedHeightAboveHem(float v, float hem)
    {
        float s = v - hem;

        if (s < 0f)
            s += 1f;

        return s > 1f - HemUnderhang ? s - 1f : s;
    }

    // Below the hem: one shared ramp, because the sheet and the hem ridge both simply stop at the
    // curtain's lower border. Real auroras cut off there hard — it is the altitude where the incoming
    // electrons run out — so this is a short smoothstep, not a falloff.
    private static float BelowHem(float s, in ColumnState col)
    {
        float ramp = Smoothstep01(1f + s * InvHemUnderhang);
        return Clamp01(ramp * (col.RayWeight + col.CoreWeight));
    }

    // Above the hem: the ray-modulated sheet decaying to exactly zero at this column's ray top, plus
    // the continuous hem ridge, which is deliberately OUTSIDE the ray modulation so the hem stays an
    // unbroken line where the rays leave gaps.
    private static float AboveHem(float s, in ColumnState col)
    {
        float body = Falloff(s * col.InvRayTop) * col.RayWeight;
        float core = Falloff(s * InvHemCoreHeight) * col.CoreWeight;
        return Clamp01(body + core);
    }

    // Decay profile as a function of fractional height, reaching exactly zero at 1.
    //
    // A blend of linear and squared rather than either alone. Pure squared makes the hem clearly the
    // brightest part (which is the single most recognisable thing about an aurora) but empties the body
    // so the rays have no sheet to rise out of; pure linear puts as much light halfway up as at the
    // bottom and the hem stops reading as an edge. Reaching exactly zero rather than trailing off
    // exponentially is not cosmetic — it is what makes RayHeightMax an enforceable bound, and therefore
    // what makes the vertical wrap provable rather than merely likely.
    public static float Falloff(float fraction)
    {
        float f = 1f - fraction;

        if (f <= 0f)
            return 0f;

        // The smoothstep is what FEATHERS THE TOP. Every column has its own ray top, so any profile
        // with a non-zero slope where it reaches zero draws a visible step between neighbouring
        // columns, and the sheet's upper edge previewed as a bar chart. Smoothstep's derivative is zero
        // at both ends, so adjacent ray tops blend into each other instead of stepping.
        //
        // The linear-to-squared blend on top of it is what keeps the HEM brightest. Smoothstep alone is
        // symmetric and puts half the brightness at half height, which flattens the curtain into a
        // slab; the extra factor tilts the weight back down toward the hem without restoring the hard
        // top edge.
        return Smoothstep01(f) * (1f - FalloffCurvature + FalloffCurvature * f);
    }

    // Hue as a function of fractional height above the hem: violet fringe below, green at and just
    // above it, red toward the top. See HueAtHem for why altitude is the right driver.
    public static float HueAtHeight(float heightFraction, float wobble)
    {
        float rate = heightFraction < 0f ? HueFallBelowHem : 1f;
        return Clamp01(HueAtHem + heightFraction * rate * HueRisePerHeight + wobble);
    }

    // Fills `rowCount` rows of an RGBA32 buffer (R,G,B,A per pixel, row-major) starting at `firstRow`.
    // Identical contract to AuroraCurtain.FillRows, on purpose: the adapter should be able to swap one
    // for the other without knowing which it is holding.
    //
    // The structure is the performance claim made concrete. Column state is evaluated once per column
    // into a scratch table and the pixel loop reads it — so the noise cost scales with the tile WIDTH
    // and the arithmetic cost with its AREA, where the contour field pays noise cost on area.
    public static void FillRows(
        byte[] rgba, int width, int height, int firstRow, int rowCount, float time,
        float tintR, float tintG, float tintB, float tintWeight)
    {
        if (rgba == null || width < 1 || height < 1)
            return;

        int lastRow = Math.Min(firstRow + rowCount, height);
        int startRow = Math.Max(firstRow, 0);

        if (startRow >= lastRow)
            return;

        float drift = Drift(time);
        float invWidth = 1f / width;
        float invHeight = 1f / height;
        float tint = Clamp01(tintWeight);

        ColumnState[] columns = RentColumns(width);
        float[] wobbles = RentWobbles(width);
        BuildColumns(columns, wobbles, width, invWidth, drift);

        for (int y = startRow; y < lastRow; y++)
        {
            float v = y * invHeight;
            int rowBase = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                Sample sample = SampleColumn(columns, wobbles, x, v);
                AuroraMath.Rgb colour = PaletteColor(sample.Hue);

                int i = rowBase + x * 4;
                rgba[i] = ToByte(Lerp(colour.R, tintR, tint));
                rgba[i + 1] = ToByte(Lerp(colour.G, tintG, tint));
                rgba[i + 2] = ToByte(Lerp(colour.B, tintB, tint));
                rgba[i + 3] = ToByte(sample.Alpha);
            }
        }
    }

    // ---- Internals ---------------------------------------------------------------------------------

    private static void BuildColumns(
        ColumnState[] columns, float[] wobbles, int width, float invWidth, float drift)
    {
        for (int x = 0; x < width; x++)
        {
            float u = x * invWidth;
            wobbles[x] = HueWobble(u, drift);

            for (int c = 0; c < Curtains.Length; c++)
                columns[x * Curtains.Length + c] = EvaluateColumn(Curtains[c], u, drift);
        }
    }

    private static Sample SampleColumn(ColumnState[] columns, float[] wobbles, int x, float v)
    {
        float wobble = wobbles[x];
        int baseIndex = x * Curtains.Length;

        float alpha = 0f;
        float hueWeighted = 0f;

        for (int c = 0; c < Curtains.Length; c++)
        {
            Sample s = CurtainAt(columns[baseIndex + c], v, wobble);
            alpha += s.Alpha;
            hueWeighted += s.Alpha * s.Hue;
        }

        return Resolve(alpha, hueWeighted, wobble);
    }

    // Collapses the per-curtain contributions. Alpha SUMS — that is the "curtains overlap in terms of
    // volume" requirement, and the clamp is only there because a byte cannot hold more than 1. Hue is
    // the alpha-weighted mean, because where two curtains overlap the colour a viewer sees is the sum
    // of two emissions, not the nearer one's; where nothing is lit the weighting is undefined and the
    // hue is irrelevant, so it falls back to the wobbled green.
    private static Sample Resolve(float alpha, float hueWeighted, float wobble)
    {
        if (alpha <= 0f)
            return new Sample(0f, Clamp01(HueAtHem + wobble));

        return new Sample(Clamp01(alpha), Clamp01(hueWeighted / alpha));
    }

    // Remaps the raw envelope noise so that a useful fraction of each curtain is genuinely dark rather
    // than merely dimmer. fBm and value noise both cluster near 0.5, so a raw field used as a gate
    // multiplies almost everything by roughly a half and gates nothing.
    private static float EnvelopeGate(float raw) => Clamp01((raw - 0.20f) / 0.42f);

    private static float HueWobble(float u, float drift) =>
        (Value1(u * HueWobblePeriod + drift * HueWobbleDrift, HueWobblePeriod, HueWobbleSeed) - 0.5f)
        * 2f * HueWobbleAmplitude;

    // 1D wrapping value noise and its fractal sum, both built on AuroraNoise by pinning the second
    // axis — see EvaluateColumn for why the primitive is reused rather than forked.
    private static float Value1(float x, int period, int seed) =>
        AuroraNoise.Value(x, 0f, period, 1, seed);

    private static float Fbm1(float x, int period, int seed, int octaves) =>
        AuroraNoise.Fbm(x, 0f, period, 1, seed, octaves);

    // Scratch buffers for the column table, reused across calls rather than allocated per call.
    //
    // This is the one piece of mutable state in the file and it is worth being explicit about why it
    // is not a purity violation: nothing is ever READ out of these that was not written by the same
    // FillRows call, so the output is a function of the arguments alone and the offline tests can pin
    // it. What reuse buys is the absence of ~7 KB of garbage per frame, which on Mono's collector is a
    // periodic frame spike for an atmospheric background that should never cause one. FillRows is only
    // ever called from the SkyOverlay draw on Unity's main thread, so there is no sharing to guard.
    //
    // Empty rather than null to start, so the grow check below is a length comparison in every case —
    // the tests compile this file under an enabled nullable context and a null-initialised static
    // array is a warning there and a second branch here for no benefit.
    private static ColumnState[] _columnScratch = new ColumnState[0];
    private static float[] _wobbleScratch = new float[0];

    private static ColumnState[] RentColumns(int width)
    {
        int needed = width * Curtains.Length;

        if (_columnScratch.Length < needed)
            _columnScratch = new ColumnState[needed];

        return _columnScratch;
    }

    private static float[] RentWobbles(int width)
    {
        if (_wobbleScratch.Length < width)
            _wobbleScratch = new float[width];

        return _wobbleScratch;
    }

    private static float Wrap01(float v)
    {
        float f = v % 1f;
        return f < 0f ? f + 1f : f;
    }

    private static float Smoothstep01(float t)
    {
        float c = Clamp01(t);
        return c * c * (3f - 2f * c);
    }

    private static byte ToByte(float v) => (byte)(Clamp01(v) * 255f + 0.5f);

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
