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
// PHASE 5 ADDS A SECOND TERM, and it is the reason the header above no longer tells the whole story.
// The shape below can only take light away, so a cell our polygon CAN see lands at exactly vanilla's
// own value and never above it — which leaves §27 unable to light a cell vanilla left dark, however
// clearly our geometry says the lamp can see it. The open door is the case that makes that matter:
// the glow grid never learns a door opened, so beyond one there is no vanilla light for a mask to
// keep. VectorLightMask then adds back, per emitter, the excess of our own model over what vanilla
// delivered, gated by the same coverage the subtraction uses:
//
//     newGlow(c) = totalGlow(c) - SUM over our emitters of  own(e, c) * (1 - lit(e, c))
//                              + SUM over our emitters of  max(0, ours(e, c) - own(e, c)) * lit(e, c)
//
// i.e. each emitter we modelled contributes lit * max(vanilla, ours) in place of its own light. The
// max sets the level and the coverage carves the darkness. See VectorLightLiftMath for why that is
// not phase 2b's no-op, and CelestialLightingFeatures.VectorLightMaskMax for what it replaces.
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

    // Phase 5's lift, kept in its own arrays rather than netted against the shadow into one signed
    // accumulator. Two reasons, and the first is the load-bearing one: with the max off the shadow
    // path has to produce the SAME BYTES it produced before phase 5 existed, and `own * (255 - c) /
    // 255` subtracted is not the same integer as `own * c / 255 - own` added — they differ by one
    // level wherever the division does not divide evenly. A control arm that is off by a level
    // everywhere is not a control arm. The second is that ColorInt's operators are vanilla's, and
    // asking them to carry negative channels through a divide is a guess about a struct we do not
    // own.
    private static ColorInt[] cellLift = new ColorInt[0];
    private static ColorInt[] cornerLift = new ColorInt[0];

    // Phase 5b's reconstruction of the sum vanilla PROJECTED, per cell — every emitter on the map
    // that reaches the cell, ours and everybody else's, added up unprojected. See CorrectSaturation.
    //
    // KEPT FOR THE GATE ALONE, not for the arithmetic. Whether a cell saturates is a question about
    // the true sum, and answering it from the fold below would be circular: the fold has already
    // stopped at the ceiling, so it cannot say whether it got there.
    private static ColorInt[] cellRaw = new ColorInt[0];

    // Vanilla's own accumulation replayed, per cell, and the same accumulation with our polygons
    // deciding how much of each emitter we modelled actually arrived.
    //
    // TWO FOLDS RATHER THAN A SUM AND A DELTA, because vanilla's accumulation is a fold and not a
    // sum: CombineColorsJob.AddColors projects after every addition, so a saturated cell's displayed
    // value depends on the ORDER its emitters were added in and cannot be recovered from their
    // total. The first of these must reproduce what the player is looking at, byte for byte — that
    // is the self-check — and the difference between them is the edit. See VectorLightSaturationMath
    // for what the single-projection version of this got wrong, and which room it got it wrong in.
    private static ColorInt[] cellFold = new ColorInt[0];

    private static ColorInt[] cellFoldOurs = new ColorInt[0];

    // What the last whole-map rebake's lift actually came to, for the probes.
    //
    // TWO NUMBERS, READ TOGETHER, because a zero in either has an entirely different cause. Samples
    // counts the cells the max was EVALUATED at, so a zero there means it never ran — a stale bake,
    // a flag that did not reach the mesh builder, the Unobstructed skip still dropping every
    // emitter. Peak is the largest lift it actually wrote, so a zero there over healthy samples
    // means it ran and correctly found nothing, which in a scene where both lighting models see the
    // same geometry is #151's whole finding rather than a failure. Pinning only one of them cannot
    // tell those apart, and they are the two outcomes this arm exists to distinguish.
    //
    // Reset by VectorLightRedraw.ForceRebuild, which is what every SetFeature step goes through, so
    // a probe read after a toggle reports that toggle's rebake rather than everything since load.
    public static long LiftSamples;

    public static int LiftPeak;

    // Phase 5b's own two numbers, read together for the same reason the lift's pair are.
    //
    // SaturatedSamples counts the cells the correction actually rewrote — cells that both carry an
    // edit and sit over vanilla's 255 ceiling. A zero means the scene never saturated, which for an
    // unlit corridor is the correct answer and for a six-lamp ring is a bake that did not run.
    // SaturationSkipped counts the cells it declined to rewrite because our reconstruction of
    // vanilla's sum did not reproduce vanilla's own displayed value — see CorrectSaturation for the
    // one case that is known to do that. A scenario that pins the first without the second cannot
    // tell "the correction ran" from "the correction bailed on every cell".
    public static long SaturatedSamples;

    public static long SaturationSkipped;

    // The largest number of levels the correction took OFF a shadow, on any channel of any cell. It
    // is the size of the bug, in the units the bug is measured in: the old composition's shadow
    // minus the corrected one.
    public static int SaturationRelief;

    // How many cell-emitter pairs the per-cell replacement actually took light off, since the last
    // reset. The OUTCOME measure for that rule rather than a description of the scene: a probe that
    // counts which cells the rule WOULD claim reads the same number whether the flag is on or off,
    // and cannot tell a working feature from one whose patch failed to apply. This only moves when
    // the mask really removed a cell's vanilla contribution, so a zero under the flag is a finding.
    public static long BentSamples;

    // What Apply cost, split by stage, on the thread that paid for it.
    //
    // WHY A STOPWATCH RATHER THAN A PROFILER ARM, and this is the second time in this repo the answer
    // has been the same. Both instruments already here are blind to this method. Dubs reports one
    // duration for the whole of Patch_VectorLightSuppress, which cannot say which stage inside it
    // grew. Circinus reports ZERO CALLS on a build that just ran it a thousand times -- the same
    // misreport NativeSkyFalloffGrid's rebuild hit, and for the same underlying reason: a section
    // regenerate is not a rendered frame, and both analyzers are built around frames. An arm that
    // reads zero is indistinguishable from a stage that never ran, so a scenario built on one
    // reports a mask that costs nothing while photographing a perfectly lit colony.
    //
    // THE STAGES ARE THE ONES WHOSE COST IS A FUNCTION OF THE EMITTER POPULATION rather than of the
    // section, because that is the axis a colony moves along and the axis nothing else here measures.
    // CollectReaching walks the field's whole roster. BuildCellShadow walks the emitters that
    // survived it, times the cells each one covers. CorrectSaturation walks every light the GLOW GRID
    // holds, ours and every other mod's, and it is the only one of the three that does not shrink
    // when our own roster does.
    //
    // ACCUMULATED, NOT AVERAGED, and read against MaskApplies for a per-call figure. A mean computed
    // in here would divide by a count the caller cannot see, and the count is the more interesting
    // half whenever a change makes a stage cheaper and provokes it more often.
    //
    // WALL TIME ON THE CALLING THREAD, deliberately, and unlike VectorLightField.BakeWallMs there is
    // nothing here that could move to another one -- a section regenerate is main-thread work through
    // and through. If that ever stops being true this measures a wait rather than a cost, and the
    // comment is the warning.
    public static double ApplyWallMs;

    public static double CollectWallMs;

    public static double ShadowWallMs;

    public static double SaturationWallMs;

    public static void ResetTelemetry()
    {
        LiftSamples = 0;
        LiftPeak = 0;
        SaturatedSamples = 0;
        SaturationSkipped = 0;
        SaturationRelief = 0;
        BentSamples = 0;
        ApplyWallMs = 0.0;
        CollectWallMs = 0.0;
        ShadowWallMs = 0.0;
        SaturationWallMs = 0.0;
    }

    // Whether phase 3 can run: the per-light arrays have to be readable, or there is nothing to
    // subtract and the subsystem stands down to the crossfade.
    public static bool Available => GlowGridPerLight.Available;

    public static bool Active =>
        CelestialLightingFeatures.VectorLightMask && Available;

    // Whether phase 5's lift runs this frame. Read ONCE per Apply and threaded down rather than
    // tested in the inner loops, so with the max off every added branch below is one bool compare
    // against a local and the shipped path keeps the shape it was profiled in.
    public static bool Lifting =>
        Active && CelestialLightingFeatures.VectorLightMaskMax;

    // Whether phase 5b's saturation correction runs this frame. Threaded down as a local for the same
    // reason Lifting is: with it off the inner loops must keep the shape they were profiled in.
    public static bool Correcting =>
        Active && CelestialLightingFeatures.VectorLightMaskSaturation;

    // Whether the aperture beam runs this frame: our model replaces this emitter's vanilla light
    // inside the polygon rather than the fan composing a max against it. Requires the shader, since
    // the fan is what delivers the replacement — without it the mask would take vanilla's light away
    // and nothing would put it back.
    public static bool Replacing =>
        Active && CelestialLightingFeatures.VectorLightApertureBeam && VectorLightShader.MaxActive;

    // Whether the PER-CELL replacement runs this frame — the aperture beam's rule applied only to the
    // cells vanilla detoured to, instead of to an emitter's whole field.
    //
    // Requires the shader for the same reason Replacing does: the mask takes vanilla's light off and
    // only the fan can put ours back, so without a fragment program this would carve darkness and
    // deliver nothing. Stood down while the aperture beam is on, which has already taken every cell
    // this could and more — running both would be one rule shadowing the other rather than two
    // effects, and an arm that set both would report the aperture beam's numbers under this flag's
    // name.
    //
    // READ BY VectorLightOverlay TOO. The field upload has to describe exactly the light this mask
    // left standing, so both sides ask this one property rather than each reading the feature flag
    // and the shader's availability for themselves.
    public static bool BentPath =>
        Active
        && CelestialLightingFeatures.VectorLightBentPath
        && !CelestialLightingFeatures.VectorLightApertureBeam
        && VectorLightShader.MaxActive;

    // Rewrites one section's lighting overlay in place. Returns false when it declined to, so the
    // caller can fall through to the crossfade rather than leaving the section unlit or unmasked.
    public static bool Apply(Map map, Mesh mesh, List<Vector3> verts, CellRect rect)
    {
        GlowGridPerLight.Reader reader = GlowGridPerLight.For(map);

        if (reader == null || mesh == null)
            return false;

        // Issue #188 item 0's outcome measure: a lighting-overlay section regenerate that actually
        // reached us. Counted AFTER the stand-down check above and before any work, so it means
        // "this section really did rebake through the mask" rather than "something called Apply".
        //
        // It lives on VectorLightField rather than beside the telemetry below because that class has
        // the reset the bake_reset probe calls; a counter split across this file's ResetTelemetry and
        // that one's ResetCounters would drain at different moments in an arm.
        //
        // THIS IS THE NUMBER TO BELIEVE over the section-dirty count. Dirty flags are work REQUESTED
        // and vanilla only regenerates the sections in view, so a change can cut flags by fifty times
        // and leave this untouched — which would mean the saving was on sections nobody was looking
        // at. Comparable between arms and between builds with no per-arm adjustment, because it
        // counts what happened rather than what was asked for.
        VectorLightField.MaskApplies++;

        bool lifting = Lifting;
        bool correcting = Correcting;

        // STARTED AFTER THE STAND-DOWN CHECKS AND THE APPLY COUNTER, so the clock measures sections
        // that really did rebake through the mask rather than calls that turned round at the door.
        // Same boundary MaskApplies is counted at, which is what makes the two divide.
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        CollectReaching(map, rect, lifting);

        CollectWallMs += clock.Elapsed.TotalMilliseconds;

        if (Reaching.Count == 0)
        {
            ApplyWallMs += clock.Elapsed.TotalMilliseconds;
            return true;
        }

        // DECIDE BEFORE TOUCHING THE MESH. `mesh.colors32` copies 613 Color32 out of native memory
        // and the write-back copies them in again, and a section with no shadow anywhere in it
        // changes not one of them. Doing the shadow accumulation first — which needs no mesh at all
        // — means an unshadowed section pays the emitter scan and nothing else, where the crossfade
        // pays the round trip plus a write to every vertex unconditionally.
        double beforeShadow = clock.Elapsed.TotalMilliseconds;

        bool anyEdit = BuildCellShadow(map, reader, rect, lifting, correcting);

        // BuildCellShadow's own time NET OF the saturation pass it calls, so the three stage clocks
        // partition Apply instead of double-counting the largest one inside the second largest.
        // CorrectSaturation adds to SaturationWallMs from inside that call.
        ShadowWallMs += clock.Elapsed.TotalMilliseconds - beforeShadow - lastSaturationMs;
        lastSaturationMs = 0.0;

        Reaching.Clear();

        if (!anyEdit)
        {
            ApplyWallMs += clock.Elapsed.TotalMilliseconds;
            return true;
        }

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

        // THE LIFT ARRAYS ARE LIVE UNDER THE CORRECTION TOO, even with the max off, and it is not an
        // over-allocation. Vanilla's projection normalises the three channels against their shared
        // peak, so taking a blocked emitter out of a saturated cell RAISES the channels that were
        // being scaled down by the one we removed — a red lamp's shadow makes the surviving green
        // brighter, in vanilla's own arithmetic. That is a genuine lift with no max involved, and
        // dropping it would leave the corrected colour hue-shifted against vanilla's.
        bool composing = lifting || correcting;

        ApplyToCorners(map, colors, rect, corners, composing);
        ApplyToCentres(colors, rect, corners, composing);

        mesh.colors32 = colors;

        ApplyWallMs += clock.Elapsed.TotalMilliseconds;
        return true;
    }

    // The saturation pass's contribution to the CURRENT Apply, so the shadow stage can subtract it
    // and the three clocks partition rather than nest. Reset by its reader on the same call, and
    // never read from anywhere else: it is a return value that could not be one, because
    // BuildCellShadow's result already means something else.
    private static double lastSaturationMs;

    // How far past its own radius an emitter still has a say, in cells. Named and public because
    // SectionDirtyMath.Reach has to agree with it exactly: this constant decides which emitters a
    // section admits, and that one decides which sections an emitter dirties. They are the same
    // question asked from the two ends, and if they disagree the losing case is a section that is
    // never told to rebake — no exception, no probe moves, just one square of map holding a shadow
    // that has already moved. Issue #188 item A.
    public const int ReachMargin = 1;

    // How far outside a section the accumulation reads, in cells. Named and public for the same
    // reason ReachMargin is: SectionDirtyMath.Changed has to expand a changed-cell box by exactly
    // this before turning it into section flags, and the two are the same question asked from the
    // two ends. If this one grew and that one did not, a cell whose coverage moved would stop
    // dirtying the section that reads it — which is not an exception or a wrong colour, but one
    // square of map holding a shadow that has already moved.
    //
    // It is the margin CellsWide/CellsHigh below build the grid with; see their header for why the
    // margin is on every side rather than on the min side alone.
    public const int CellMargin = 1;

    // Which emitters can reach any cell this section's vertices average over. The vertex loop reads
    // one cell further out on the min side than the section itself, because a corner vertex at the
    // section's edge averages the cells on both sides of it.
    private static void CollectReaching(Map map, CellRect rect, bool lifting)
    {
        Reaching.Clear();

        foreach (VectorLightField.LightEntry entry in VectorLightField.LightsFor(map))
        {
            int reach = Mathf.CeilToInt(entry.Radius) + ReachMargin;

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
            // UNOBSTRUCTED IS ONLY A REASON TO SKIP WHEN THE MASK CAN ONLY SUBTRACT. Under the max
            // an emitter that shadows nothing still has a lift to deliver — the octile residue at
            // the very least, and the whole beam if what made it "unobstructed" was §27e deciding
            // an open door is a hole. Keeping the skip under the max would drop exactly the
            // emitters phase 5 exists for, and would do it silently: the frame would come back
            // looking like the mask alone.
            bool worthVisiting = lifting || !entry.Unobstructed;

            // A DIRTY POLYGON IS A SHAPE THAT MAY HAVE MOVED, NOT ONE THAT CANNOT BE DRAWN, and
            // the difference between those two readings is a visible flicker. Vanilla regenerates
            // dirty sections at Map.MapUpdate line 1173 and does not reach
            // GameConditionManagerDraw -- where EnsurePolygons runs -- until 1178, so anything that
            // both marks a polygon dirty and dirties a section inside one tick is baked here BEFORE
            // the rebuild. Dropping the emitter at that point does not merely lose its shadow: Apply
            // returns true with nothing collected, so the section keeps vanilla's flood with neither
            // our mask nor the crossfade's suppression on it, and the room reads a frame BRIGHTER
            // than its settled state before snapping back. A door swing provokes it, so it fires
            // over and over in ordinary play.
            //
            // So the last shape is used instead. It is one frame stale by exactly the edit that
            // provoked the rebuild -- a wall cell, a door leaf -- and the corrected shape lands on
            // the next frame regardless, because EnsurePolygons re-dirties whatever it rebuilds.
            // Carrying yesterday's shadow for one frame is invisible; dropping it is not.
            //
            // AN EMPTY POLYGON IS STILL SKIPPED, and that is a different case rather than the same
            // one with a smaller count: an emitter that has never been baked has no prior state to
            // propagate. Vanilla's own flood is the right thing to show for a lamp that appeared
            // this frame, and suppressing it with nothing to put back would light nothing at all.
            bool hasPrior = entry.Polygon.Count > 0
                && (CelestialLightingFeatures.VectorLightStalePolygon || !entry.PolygonDirty);

            if (overlaps && worthVisiting && hasPrior)
            {
                Reaching.Add(entry);

                // Counted where it happens rather than inferred from the dirty flag afterwards: by
                // the time the frame ends EnsurePolygons has cleared the flag, so nothing outside
                // this loop can still tell that the shape used here was the previous one.
                if (entry.PolygonDirty)
                    VectorLightField.MaskStalePolygonUses++;
            }

            // The emitter really did drop out of a section that renders it. Zero after a scene has
            // settled; see VectorLightField.MaskSkipsNoPolygon.
            if (overlaps && worthVisiting && !hasPrior)
                VectorLightField.MaskSkipsNoPolygon++;
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
    private static int CellsWide(CellRect rect) => rect.Width + CellMargin * 2;

    private static int CellsHigh(CellRect rect) => rect.Height + CellMargin * 2;

    private static int CellIndex(CellRect rect, int cellX, int cellZ) =>
        (cellZ - rect.minZ + 1) * CellsWide(rect) + (cellX - rect.minX + 1);

    // Returns whether anything is shadowed at all, so Apply can leave the mesh untouched when
    // nothing is.
    private static bool BuildCellShadow(
        Map map, GlowGridPerLight.Reader reader, CellRect rect, bool lifting, bool correcting)
    {
        int cells = CellsWide(rect) * CellsHigh(rect);
        Grow(ref cellShadow, cells);

        for (int i = 0; i < cells; i++)
            cellShadow[i] = default;

        if (lifting || correcting)
        {
            Grow(ref cellLift, cells);

            for (int i = 0; i < cells; i++)
                cellLift[i] = default;
        }

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

            any |= AccumulateEmitter(map, rect, entry, light, colors, lifting);
        }

        // AFTER THE ACCUMULATION AND ONLY IF THERE IS SOMETHING TO CORRECT. The correction is a
        // rewrite of what the loop above just wrote, so it needs the totals; and a section with no
        // edit anywhere in it has nothing to rewrite, which is most sections of most maps. Paying
        // the raw-sum walk unconditionally would put a second pass over every emitter on the map
        // into every regenerate, for cells whose shadow is already zero.
        if (any && correcting)
            CorrectSaturation(map, reader, rect, cells, lifting);

        return any;
    }

    // §27 phase 5b: redo the edit in the space vanilla ADDED its light in, wherever that space and
    // the one the mask writes into have come apart.
    //
    // THE PROBLEM, IN ONE LINE. Vanilla sums its emitters into a ColorInt and projects that sum into
    // a byte, and over 255 the projection SCALES rather than clips. The mask subtracts each blocked
    // emitter's raw contribution out of the projected byte, so in a saturated cell it removes light
    // at full strength from a value that has stopped growing. The visible consequence is a direction:
    // ring lamps around a wall column and the shadow behind it gets DEEPER with every lamp added,
    // when adding a lamp can only ever fill shadow in. See VectorLightSaturationMath for the algebra
    // and for the monotonicity property the offline tests hold this to.
    //
    // WHAT IT DOES. Reconstructs the raw sum, applies the accumulated shadow and lift to THAT, and
    // projects once — then hands back the difference against what vanilla actually displayed, split
    // into the same non-negative shadow and lift halves the rest of the file already averages. The
    // accumulation above is untouched; only the space its result is measured in changes.
    //
    // THE SELF-CHECK IS LOAD-BEARING, and it is why this compares against VisualGlowAt rather than
    // trusting its own reconstruction. CombineColorsJob.AddColors projects after EVERY addition, not
    // once at the end, and that fold is lossy: two saturating red lamps followed by a green one land
    // somewhere a single projection of the true sum does not, by about 128 levels. Where the two
    // disagree that far our reconstruction is simply wrong about what vanilla did, so the cell is
    // left alone with today's arithmetic rather than "corrected" against a value vanilla never
    // displayed. SaturationSkipped counts those, so the residue is a number a scenario can read
    // rather than a paragraph.
    //
    // WITH A SLACK, AND THE SLACK IS THE LESSON. Same-hue emitters — the ring of identical lamps this
    // exists for, and every ordinary colony room — land on the same capped ray whichever way they are
    // added, but the fold's per-step integer divide still leaves one or two levels between the two
    // answers. The first cut of this tested exact equality, and on the six-torch fixture it rejected
    // 50 of 85 candidate cells over that rounding: the corrected arm fell back to the broken
    // composition on most of the cells the scenario was built to measure, and came back non-monotone.
    // See VectorLightSaturationMath.ReconstructionSlack for where the number comes from.
    private static void CorrectSaturation(
        Map map, GlowGridPerLight.Reader reader, CellRect rect, int cells, bool lifting)
    {
        // ONE EMITTER CANNOT SATURATE ANYTHING. Its own light is a Color32 and tops out at 255, so a
        // section only one light reaches has no cell over the ceiling and nothing here can change a
        // byte. Counting first costs a rect test per emitter and skips both the cell walk and the
        // 361-cell fix-up pass below — which is the common case in a colony, where most sections that
        // carry a shadow at all carry it from a single lamp.
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        if (OverlappingLights(rect, reader) < 2)
        {
            // THE EARLY RETURN IS TIMED TOO, and on a colony it is the interesting half. Proving
            // there is no second overlapping light means walking the whole glow grid, so the cheap
            // path is not free and its cost grows with the emitter population exactly as the
            // expensive one does.
            SaturationWallMs += clock.Elapsed.TotalMilliseconds;
            lastSaturationMs += clock.Elapsed.TotalMilliseconds;
            return;
        }

        Grow(ref cellRaw, cells);
        Grow(ref cellFold, cells);
        Grow(ref cellFoldOurs, cells);

        for (int i = 0; i < cells; i++)
        {
            cellRaw[i] = default;
            cellFold[i] = default;
            cellFoldOurs[i] = default;
        }

        // EVERY light on the map, not only the ones §27 modelled, and IN VANILLA'S OWN ORDER.
        //
        // Every light, because the value being reconstructed is what vanilla displayed and vanilla
        // folded in everything reaching the cell — a mod's lamp, a glowing plant, a fire. Replaying
        // our own emitters alone would read a saturated cell as unsaturated and leave the
        // over-subtraction exactly where it was in the case most likely to saturate: a room several
        // mods are all lighting.
        //
        // In vanilla's order, because the fold is lossy and therefore not commutative. This walks
        // `lights` front to back, which is the order CombineColorsJob.Execute adds them in, and each
        // cell accumulates independently — so folding emitter-outer over cells gives every cell its
        // own emitters in vanilla's order, which is the only order that reproduces vanilla's answer.
        // THE REJECT COMES BEFORE THE RESOLVE, and at a colony's worth of lamps that reordering is
        // most of what this loop costs. A light whose square misses this section writes nothing --
        // AccumulateFold intersects its reach with the section and the resulting loop is empty -- so
        // the two native-container indexers TryLightAt pays to hand back its colour array, and the
        // linear scan ReachingEntryFor pays to look for it, were both being spent to arrive at "no
        // change" for the great majority of the map's emitters. Reader.OverlapsAt answers the same
        // question from `lights[i].AffectedRect` alone.
        //
        // IDENTICAL BY CONSTRUCTION RATHER THAN BY TOLERANCE, which is the property that lets this
        // sit under a lossy fold at all. The skipped lights are exactly the ones whose contribution
        // to both accumulators is empty, so removing them removes no STEP of vanilla's accumulation
        // -- and the surviving lights are still visited in ascending index order, which is the order
        // CombineColorsJob.Execute added them in and the only order that reproduces vanilla's answer.
        // See FoldInto for why that order is load-bearing rather than tidy.
        int foldMinX = rect.minX - 1;
        int foldMaxX = rect.maxX + 1;
        int foldMinZ = rect.minZ - 1;
        int foldMaxZ = rect.maxZ + 1;

        for (int i = 0; i < reader.LightCount; i++)
        {
            if (!reader.OverlapsAt(i, foldMinX, foldMaxX, foldMinZ, foldMaxZ))
                continue;

            if (reader.TryLightAt(i, out GlowLight light, out UnsafeList<Color32> colors))
                AccumulateFold(map, rect, light, colors, ReachingEntryFor(light), lifting);
        }

        for (int z = rect.minZ - 1; z <= rect.maxZ + 1; z++)
        {
            for (int x = rect.minX - 1; x <= rect.maxX + 1; x++)
                CorrectCell(map, rect, x, z);
        }

        SaturationWallMs += clock.Elapsed.TotalMilliseconds;
        lastSaturationMs += clock.Elapsed.TotalMilliseconds;
    }

    // How many emitters reach any cell this section's vertices average over — the same one-cell
    // margin the accumulators use, and stopping at two because that is all the caller asks.
    private static int OverlappingLights(CellRect rect, GlowGridPerLight.Reader reader)
    {
        int found = 0;

        // THE CHEAP TEST FIRST, and the two conditions have simply swapped places: a light counts
        // here only if it both resolves AND overlaps, so which one is asked first cannot change the
        // count. It changes who pays. This loop runs on the path the caller's header calls the
        // common case -- a section carrying one lamp's shadow -- and on that path it does not stop
        // early, because proving there is no SECOND overlapping light means walking the whole map's
        // emitter list. Paying TryLightAt's pool indexer for every one of them was the cost of
        // establishing that nothing needed correcting.
        for (int i = 0; i < reader.LightCount && found < 2; i++)
        {
            if (!reader.OverlapsAt(i, rect.minX - 1, rect.maxX + 1, rect.minZ - 1, rect.maxZ + 1))
                continue;

            if (reader.TryLightAt(i, out GlowLight _, out UnsafeList<Color32> _))
                found++;
        }

        return found;
    }

    // Which of the emitters this section is masking, if any, is the light vanilla is about to fold
    // in. Null means "not one of ours", which is the answer for most lights on most maps and means
    // the fold below takes the emitter exactly as vanilla delivered it.
    //
    // A LINEAR SCAN RATHER THAN A LOOKUP. Reaching holds the handful of emitters whose square
    // overlaps ONE section — a lamp shadows a wedge, not a colony — so building a dictionary per
    // section would cost more than walking the list once per light.
    private static VectorLightField.LightEntry ReachingEntryFor(GlowLight light)
    {
        long key = GlowGridPerLight.Reader.KeyFor(light.id, light.isTerrain);

        for (int i = 0; i < Reaching.Count; i++)
        {
            if (Reaching[i].VanillaKey == key)
                return Reaching[i];
        }

        return null;
    }

    // One emitter folded into both reconstructions: vanilla's own accumulation of this cell, and the
    // same accumulation with our polygons deciding how much of this emitter arrived.
    //
    // DELIBERATELY NOT AccumulateEmitter WITH THE COVERAGE TORN OUT, and no longer a plain sum
    // either. It visits every cell of every emitter — ours and everybody else's, lit or shadowed,
    // skipping nothing — because each of those is a STEP of the fold vanilla performed, and a step
    // skipped is a reconstruction that no longer reproduces the value on screen.
    //
    // THE TWO FOLDS DIFFER IN ONE PLACE ONLY: for an emitter we modelled, the second one folds in
    // `own * coverage` plus whatever lift the max has to add, in place of `own`. Everything else —
    // the order, the projection after each step, the zero-addend guard — is identical, so
    // subtracting one from the other leaves precisely the light our geometry says never arrived.
    private static void AccumulateFold(
        Map map, CellRect rect, GlowLight light, UnsafeList<Color32> colors,
        VectorLightField.LightEntry entry, bool lifting)
    {
        CellRect reach = light.AffectedRect;

        int minX = Math.Max(reach.minX, rect.minX - 1);
        int maxX = Math.Min(reach.maxX, rect.maxX + 1);
        int minZ = Math.Max(reach.minZ, rect.minZ - 1);
        int maxZ = Math.Min(reach.maxZ, rect.maxZ + 1);

        // Hoisted out of the inner loop for the same reason AccumulateEmitter hoists them: nothing
        // here depends on the cell, and the loop runs a few hundred times per emitter per section.
        bool ours = entry != null;
        bool liftHere = ours && lifting;
        bool matchSeed = CelestialLightingFeatures.VectorLightMaskMaxSeed;
        float radius = light.glowRadius;
        float radiusSquared = radius * radius;
        ColorInt colour = light.glowColor;
        int lightX = light.position.x;
        int lightZ = light.position.z;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                IntVec3 cell = new IntVec3(x, 0, z);

                if (!cell.InBounds(map))
                    continue;

                int local = light.WorldToLocalIndex(cell);

                if (local < 0 || local >= colors.Length)
                    continue;

                Color32 own = colors[local];

                // Vanilla's own guard, and it has to be here rather than inside the fold step: a
                // light that delivers nothing to a cell is not a step of the accumulation at all,
                // so counting it would put a projection where vanilla performed none.
                if (own.r == 0 && own.g == 0 && own.b == 0)
                    continue;

                int index = CellIndex(rect, x, z);

                cellRaw[index].r += own.r;
                cellRaw[index].g += own.g;
                cellRaw[index].b += own.b;

                FoldInto(ref cellFold[index], own.r, own.g, own.b);

                int keptR = own.r;
                int keptG = own.g;
                int keptB = own.b;

                if (ours)
                {
                    int coverage = VectorLightMath.CoverageAt(
                        entry.Coverage, entry.Cell.x, entry.Cell.z, entry.CoverageRadius, x, z);

                    // TAKEN AWAY RATHER THAN SCALED UP, which is not the same integer: this is the
                    // exact quantity AccumulateEmitter adds to the shadow, so the two descriptions
                    // of one emitter's blocked light agree to the byte instead of sitting a rounding
                    // step apart. A disagreement there would draw a faint seam wherever a cell
                    // crosses the saturation ceiling, which is a moving boundary in a lit room.
                    if (coverage < 255)
                    {
                        int shadowed = 255 - coverage;

                        keptR -= own.r * shadowed / 255;
                        keptG -= own.g * shadowed / 255;
                        keptB -= own.b * shadowed / 255;
                    }

                    if (liftHere && coverage > 0
                        && ComputeLift(
                            coverage, own, colour, x - lightX, z - lightZ, radius, radiusSquared,
                            matchSeed, out int liftR, out int liftG, out int liftB))
                    {
                        keptR += liftR;
                        keptG += liftG;
                        keptB += liftB;
                    }
                }

                FoldInto(ref cellFoldOurs[index], keptR, keptG, keptB);
            }
        }
    }

    // One step of vanilla's accumulation, in the ColorInt the accumulators are held in.
    private static void FoldInto(ref ColorInt fold, int addR, int addG, int addB)
    {
        int r = fold.r;
        int g = fold.g;
        int b = fold.b;

        VectorLightSaturationMath.Accumulate(ref r, ref g, ref b, addR, addG, addB);

        fold.r = r;
        fold.g = g;
        fold.b = b;
    }

    private static void CorrectCell(Map map, CellRect rect, int x, int z)
    {
        int index = CellIndex(rect, x, z);
        ColorInt shadow = cellShadow[index];
        ColorInt lift = cellLift[index];

        // A cell with no edit is left exactly as vanilla wrote it, whatever the raw sum says. The
        // correction is a restatement of an edit, not an edit of its own.
        bool edited = shadow.r != 0 || shadow.g != 0 || shadow.b != 0
            || lift.r != 0 || lift.g != 0 || lift.b != 0;

        if (!edited)
            return;

        ColorInt raw = cellRaw[index];

        // THE ONE GATE THAT MAKES THIS A CONFINED FIX. Under the ceiling no step of either fold
        // projects anything, so both folds are plain sums, `delivered - ours` is the accumulated
        // shadow back again and the whole pass is provably a no-op. Testing for it rather than
        // computing it keeps every unsaturated shadow in the mod byte-identical to what it was
        // before this existed, which is what makes the flag's off arm and its on arm comparable
        // everywhere except where the bug lives.
        if (!VectorLightSaturationMath.Saturates(raw.r, raw.g, raw.b))
            return;

        IntVec3 cell = new IntVec3(x, 0, z);

        if (!cell.InBounds(map))
            return;

        Color32 delivered = map.glowGrid.VisualGlowAt(cell);
        ColorInt fold = cellFold[index];

        // THE SELF-CHECK, AND IT IS EXACT. cellFold is a transcription of the arithmetic that
        // produced `delivered`, run over the same emitters in the same order, so the two agreeing is
        // the expected outcome rather than a lucky one. Where they do not, this cell holds light
        // that did not reach it the way we think it did — something writing the accumulated grid
        // directly, or a version change under the reflection GlowGridPerLight leans on — and the
        // honest answer is to leave it with today's arithmetic and count it, rather than to rewrite
        // it against a value the game never showed.
        bool reconstructed = VectorLightSaturationMath.Reconstructs(
            fold.r, fold.g, fold.b, delivered.r, delivered.g, delivered.b);

        if (!reconstructed)
        {
            SaturationSkipped++;
            return;
        }

        ColorInt ours = cellFoldOurs[index];

        ColorInt corrected = new ColorInt(
            VectorLightSaturationMath.ShadowFrom(delivered.r, ours.r),
            VectorLightSaturationMath.ShadowFrom(delivered.g, ours.g),
            VectorLightSaturationMath.ShadowFrom(delivered.b, ours.b),
            0);

        cellShadow[index] = corrected;
        cellLift[index] = new ColorInt(
            VectorLightSaturationMath.LiftFrom(delivered.r, ours.r),
            VectorLightSaturationMath.LiftFrom(delivered.g, ours.g),
            VectorLightSaturationMath.LiftFrom(delivered.b, ours.b),
            0);

        SaturatedSamples++;
        SaturationRelief = Math.Max(
            SaturationRelief,
            Math.Max(
                shadow.r - corrected.r,
                Math.Max(shadow.g - corrected.g, shadow.b - corrected.b)));
    }

    private static bool AccumulateEmitter(
        Map map, CellRect rect, VectorLightField.LightEntry entry, GlowLight light,
        UnsafeList<Color32> colors, bool lifting)
    {
        bool any = false;

        // Hoisted out of the inner loop rather than read per cell. glowColor and glowRadius are
        // fields on a struct we hold by value, so this is not about the cost of reading them — it
        // is that the loop below runs a few hundred times per emitter per section and every line
        // inside it that does not depend on the cell is a line that should not be there.
        bool matchSeed = CelestialLightingFeatures.VectorLightMaskMaxSeed;

        // Read ONCE per emitter and threaded down as a local, for the reason Lifting's header gives:
        // the loop below runs a few hundred times per emitter per section, and with the flag off
        // every added branch has to be one bool compare against a local or the shipped path stops
        // having the shape it was profiled in.
        bool replacing = Replacing;
        bool bentPath = BentPath;
        float radius = light.glowRadius;
        float radiusSquared = radius * radius;
        ColorInt colour = light.glowColor;
        int lightX = light.position.x;
        int lightZ = light.position.z;

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

                // Fully lit is the common case for the SUBTRACTION and costs one compare. Checked
                // before the glow read because the array index is the dearer of the two. Under the
                // max a fully lit cell is exactly where the lift lands, so the skip is conditional
                // on there being no lift to compute rather than unconditional.
                //
                // AND UNDER THE PER-CELL REPLACEMENT THE SKIP CANNOT FIRE AT ALL, because a fully lit cell
                // is exactly where a replacement has the most to take away. Removing this emitter's
                // whole contribution is the point there, not an edge case. The per-cell rule has to
                // read vanilla's own delivered distance before it can decide, and that read is what
                // this skip exists to avoid — so with it on the skip goes, and the shipped path
                // keeps its shape only because the flag is off there.
                if (coverage >= 255 && !lifting && !replacing && !bentPath)
                    continue;

                IntVec3 cell = new IntVec3(x, 0, z);

                if (!cell.InBounds(map))
                    continue;

                int local = light.WorldToLocalIndex(cell);

                if (local < 0 || local >= colors.Length)
                    continue;

                Color32 own = colors[local];
                int index = CellIndex(rect, x, z);

                // THE BLACK TEST GUARDS THE SUBTRACTION ONLY, and moving it was the whole point.
                // A cell vanilla never lit has nothing to take away — and under the max it is the
                // single most interesting cell on the map, because "vanilla delivered none of it"
                // is precisely what the far side of an open door looks like from in here. Leaving
                // the old skip in place would have dropped every cell phase 5 exists to light,
                // and would have done it silently: the frame comes back looking like the mask
                // alone, which is a passing arm rather than an obvious failure.
                bool anyOwn = own.r != 0 || own.g != 0 || own.b != 0;

                // THE APERTURE BEAM REPLACES RATHER THAN TRIMS. Normally the mask removes only the
                // SHADOWED share of this emitter's light and leaves the rest of vanilla's flood
                // standing, and the fan then adds the excess of our model over it. That composition
                // is degenerate wherever vanilla already delivers what our model claims — which is
                // precisely an open aperture, where vanilla's flood takes a short path through the
                // hole and arrives at close to our own straight-line value. The beam is composed
                // away rather than culled away: the polygon is built, the coverage is 255 along it,
                // and there is simply no excess left to draw.
                //
                // With this on, every cell in the emitter's reach gives up ALL of that emitter's
                // vanilla light, and VectorLightOverlay drops _VanillaWeight to zero so the fan
                // delivers the whole model instead of the difference. Inside the polygon that is a
                // replacement rather than a sum — which is what keeps it clear of epic #145's
                // rejected option, where drawing over an UNsuppressed flood landed a room 6 L* high.
                //
                // NOT AN APERTURE-SPECIFIC RULE, and that is deliberate. Nothing here asks how the
                // light left the room. A doorway already looks like this because vanilla delivers
                // nothing beyond a door and the max degenerates to our whole model on its own; this
                // makes an aperture reach the same place by the same arithmetic rather than by a
                // second code path that has to agree with the first.
                //
                // THE PER-CELL RULE IS THE SAME REPLACEMENT WITH A PREDICATE IN FRONT OF IT. Where
                // vanilla reached the cell by the route our polygon sees along, it delivered our
                // model's own value and there is nothing to gain by taking the cell off it — that is
                // the near field, and it is what the global version spent to buy the aperture. Where
                // vanilla DETOURED, our model is the better description and takes the cell whole.
                // VectorLightOverlay.SurvivingShare asks the identical question of the identical
                // pure predicate, so the field the fragment program subtracts describes what this
                // loop left behind.
                bool claimed = bentPath
                    && VectorLightLiftMath.VanillaBentToArrive(
                        x - lightX, z - lightZ, own.a, anyOwn);

                int shadowed = replacing || claimed ? 255 : 255 - coverage;

                if (shadowed > 0 && anyOwn)
                {

                    // Integer throughout: these are bytes scaled by a byte, so the float round-trip
                    // the first version did per channel bought nothing but conversions.
                    cellShadow[index].r += own.r * shadowed / 255;
                    cellShadow[index].g += own.g * shadowed / 255;
                    cellShadow[index].b += own.b * shadowed / 255;
                    any = true;

                    // Counted HERE and not beside the predicate, so it means "the rule took light
                    // off this cell" rather than "the rule matched". Those differ at every cell
                    // vanilla never lit — the far side of an open door — which the rule claims and
                    // where claiming it changes nothing.
                    if (claimed)
                        BentSamples++;
                }

                if (lifting && coverage > 0)
                {
                    any |= AccumulateLift(
                        index, coverage, own, colour, x - lightX, z - lightZ, radius, radiusSquared,
                        matchSeed);
                }
            }
        }

        return any;
    }

    // Phase 5's half of one cell: how much of this emitter's light to put BACK, being the excess of
    // our straight-line model over what vanilla's flood actually delivered, gated by coverage.
    //
    // Returns whether it wrote anything, so BuildCellShadow's "did anything change at all" answer
    // covers the lift as well as the shadow. Getting that wrong would mean a section whose ONLY edit
    // is a lift — a doorway beam into a room with no shadow in it — deciding it had nothing to do
    // and handing back vanilla's mesh untouched.
    private static bool AccumulateLift(
        int index, int coverage, Color32 own, ColorInt colour, int dx, int dz, float radius,
        float radiusSquared, bool matchSeed)
    {
        if (!ComputeLift(
                coverage, own, colour, dx, dz, radius, radiusSquared, matchSeed,
                out int liftR, out int liftG, out int liftB))
            return false;

        // COUNTED BEFORE THE ZERO TEST, not after, and that is the whole value of the metric. A max
        // that ran everywhere and found nothing to add is the correct outcome in a scene where both
        // models see the same geometry; a max that never ran is a bug. Counting only the cells it
        // brightened would give the two the same number.
        //
        // COUNTED HERE AND NOT IN ComputeLift, because the saturation correction evaluates the same
        // lift a second time to fold it, and a sample count that doubled whenever a section happened
        // to saturate would be measuring the scene rather than the max.
        LiftSamples++;
        LiftPeak = Math.Max(LiftPeak, Math.Max(liftR, Math.Max(liftG, liftB)));

        // The common case under a matched seed is that vanilla already delivered everything our
        // model would have, on all three channels — that is #151's finding, and it is still true
        // everywhere the two models see the same geometry. Checking before the write keeps those
        // cells out of the corner pass's work as well as out of this one.
        if ((liftR | liftG | liftB) == 0)
            return false;

        cellLift[index].r += liftR;
        cellLift[index].g += liftG;
        cellLift[index].b += liftB;
        return true;
    }

    // What the max says this emitter adds at this cell, with no telemetry and no accumulation — the
    // arithmetic on its own, so that both the accumulation above and the saturation correction's
    // fold can ask for it and get the same answer.
    //
    // Returns false when there is nothing to add, which is the common case: outside the disc, past
    // the falloff, or — the interesting one — a cell where vanilla's flood already delivered
    // everything our straight line would have.
    private static bool ComputeLift(
        int coverage, Color32 own, ColorInt colour, int dx, int dz, float radius,
        float radiusSquared, bool matchSeed, out int liftR, out int liftG, out int liftB)
    {
        liftR = 0;
        liftG = 0;
        liftB = 0;

        // THE INTEGER RADIUS TEST BEFORE THE SQUARE ROOT. AffectedRect is the light's bounding
        // SQUARE, so more than a fifth of the cells this loop visits are outside the disc entirely
        // and would come back with a falloff of zero having paid for a square root, a divide and a
        // projection first. This is the one line that keeps the max's cost proportional to the lit
        // disc rather than to the square around it.
        int distanceSquared = dx * dx + dz * dz;

        if (distanceSquared > radiusSquared)
            return false;

        float distance = VectorLightLiftMath.SightlineDistance(dx, dz, matchSeed);
        float falloff = VectorLightMath.Falloff(distance, radius);

        if (falloff <= 0f)
            return false;

        VectorLightLiftMath.Project(
            colour.r, colour.g, colour.b, falloff, out int r, out int g, out int b);

        liftR = VectorLightLiftMath.LiftChannel(r, own.r, coverage);
        liftG = VectorLightLiftMath.LiftChannel(g, own.g, coverage);
        liftB = VectorLightLiftMath.LiftChannel(b, own.b, coverage);

        // The control arm zeroes the lift HERE rather than earlier, so that everything above — the
        // relaxed skips that got us to this cell, the falloff, the projection — has run exactly as
        // it does under the feature. An arm that short-circuits at the top of the method would be
        // testing the flag, not the composition. See CelestialLightingFeatures.VectorLightMaskMaxLift.
        if (!CelestialLightingFeatures.VectorLightMaskMaxLift)
        {
            liftR = 0;
            liftG = 0;
            liftB = 0;
        }

        return true;
    }

    // Corner vertices, mirroring GenerateLightingOverlay's own averaging exactly: the up-to-four
    // cells meeting at this lattice point, skipping any whose edifice blocks light and any off the
    // map, divided by however many that left. Averaging over a different set than vanilla did would
    // subtract a different quantity than it added, which shows up as a faint grid of light and dark
    // corners rather than as an obvious error.
    private static void ApplyToCorners(
        Map map, Color32[] colors, CellRect rect, int corners, bool lifting)
    {
        Grow(ref cornerShadow, corners);

        if (lifting)
            Grow(ref cornerLift, corners);

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
                if (NoEditAround(rect, x, z, lifting))
                {
                    cornerShadow[index] = default;

                    if (lifting)
                        cornerLift[index] = default;

                    continue;
                }

                ColorInt sum = default;
                ColorInt lift = default;
                int counted = 0;

                // ONE WALK OVER THE FOUR CELLS, NOT TWO. The averaging RULE is what has to be
                // shared, not just the loop: vanilla averaged the cells that are in bounds and do
                // not block light, and both terms have to average over exactly that set or the
                // subtraction and the lift would each draw their own edge a fraction of a level
                // apart — which reads as a faint bright fringe on the shadow side of every
                // boundary rather than as an obvious error.
                for (int corner = 0; corner < 4; corner++)
                {
                    int cx = x - (corner % 2 == 0 ? 1 : 0);
                    int cz = z - (corner < 2 ? 1 : 0);
                    IntVec3 cell = new IntVec3(cx, 0, cz);

                    if (!cell.InBounds(map) || BlocksLight(map, cell))
                        continue;

                    int at = CellIndex(rect, cx, cz);
                    sum += cellShadow[at];

                    if (lifting)
                        lift += cellLift[at];

                    counted++;
                }

                ColorInt shadow = counted > 0 ? sum / counted : default;
                ColorInt lifted = counted > 0 && lifting ? lift / counted : default;

                cornerShadow[index] = shadow;

                if (lifting)
                    cornerLift[index] = lifted;

                colors[index] = Compose(colors[index], shadow, lifted);
            }
        }
    }

    // Whether all four cells meeting at this lattice point are both unshadowed and unlifted. Reads
    // the accumulators rather than the map, so it costs a handful of array reads and no game state
    // at all — the point is to decide NOT to ask the map.
    private static bool NoEditAround(CellRect rect, int x, int z, bool lifting)
    {
        for (int corner = 0; corner < 4; corner++)
        {
            int cx = x - (corner % 2 == 0 ? 1 : 0);
            int cz = z - (corner < 2 ? 1 : 0);
            int at = CellIndex(rect, cx, cz);
            ColorInt shadow = cellShadow[at];

            if (shadow.r != 0 || shadow.g != 0 || shadow.b != 0)
                return false;

            if (!lifting)
                continue;

            ColorInt lift = cellLift[at];

            if (lift.r != 0 || lift.g != 0 || lift.b != 0)
                return false;
        }

        return true;
    }

    // Centre vertices are vanilla's own average of the four corners around them, so ours is the
    // average of the four corner SUBTRACTIONS. Recomputing the centre from the cell instead would
    // disagree with the corners by a fraction of a level and put a faint diamond in every cell.
    private static void ApplyToCentres(Color32[] colors, CellRect rect, int corners, bool lifting)
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

                ColorInt lift = default;

                if (lifting)
                {
                    lift = cornerLift[botLeft];
                    lift += cornerLift[botLeft + 1];
                    lift += cornerLift[botLeft + stride];
                    lift += cornerLift[botLeft + stride + 1];
                }

                // Four unedited corners average to nothing, and adding nothing to nothing is a write
                // that changes no pixel. Skipping it is the same saving as the corner pass's, on the
                // same reasoning.
                if (sum.r == 0 && sum.g == 0 && sum.b == 0
                    && lift.r == 0 && lift.g == 0 && lift.b == 0)
                    continue;

                int index = corners + (z - rect.minZ) * rect.Width + (x - rect.minX);
                colors[index] = Compose(colors[index], sum / 4, lift / 4);
            }
        }
    }

    // Vanilla's own value with the bent light taken out and the max's excess put back.
    //
    // ONE CLAMP, AT THE END, not one per term. Clamping the subtraction on its own first and then
    // adding would let a cell that momentarily went below zero come back UP from zero rather than
    // from where the arithmetic actually put it, which turns a deep shadow with a little lift on it
    // into a visibly grey one. The two terms are parts of a single expression — lit * max(vanilla,
    // ours) in place of each emitter's own contribution — and the byte is where that expression is
    // finally allowed to hit its floor.
    //
    // Alpha is the sky-cover term, not light, and is left exactly alone — §7b's occlusion, §7c/§7d's
    // falloff and the roofed-area floor all ride on it.
    private static Color32 Compose(Color32 colour, ColorInt shadow, ColorInt lift)
    {
        return new Color32(
            ClampByte(colour.r - shadow.r + lift.r),
            ClampByte(colour.g - shadow.g + lift.g),
            ClampByte(colour.b - shadow.b + lift.b),
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
