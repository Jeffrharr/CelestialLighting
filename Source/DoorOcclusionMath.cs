namespace CelestialLighting;

// Pure core, DESIGN.md §27e: does the edifice standing on a cell stop §27's rays?
//
// Verse-free on purpose, same discipline as EavesMath.cs and DoorLeakMath.cs — the adapter
// (VectorLightBlockers) reads ThingDef.blockLight and Building_Door.Open off live things and hands
// the two booleans down here. Compiled into both Source (net481) and Tests (net8.0) through a linked
// <Compile Include>, so the tests exercise the exact shipped file.
//
// WHY THIS EXISTS AT ALL, GIVEN IT IS FOUR LINES. §27's occlusion rule was one expression inline in
// VectorLightBlockers — `edifice.def.blockLight` — and it was correct precisely because it was
// vanilla's own expression. Adding an open-state term to it is the first time that rule stops being
// a restatement of vanilla's, so it stops being self-evidently right and becomes something that has
// to be pinned by tests. Pulling it out is what makes the four-way table below testable offline.
//
// THE DIVERGENCE THIS ENCODES, STATED PLAINLY. RimWorld's glow grid never learns that a door opened.
// Verse.Building.SpawnSetup writes def.blockLight into GlowGrid's lightBlockers bit array once, at
// spawn, and Building_Door.DoorOpen sets openInt, clears the reachability cache, fires
// Map.events.Notify_DoorOpened — and touches the glow grid not at all. So in vanilla a door blocks
// light open or shut, and there is no vanilla behaviour here to mirror. With OpenDoorsPassLight on,
// §27 knowingly draws light through an open door that vanilla's gameplay light does not deliver.
//
// That is a divergence we are allowed to make and vanilla is not, because §27's contract is that it
// changes only what is RENDERED: map.glowGrid, GroundGlowAt, plant growth, work speed and pawn
// vision are identical with §27 on or off (see CelestialLightingFeatures.VectorLights). A drawn beam
// through an open door costs no gameplay light and takes none away. It is the visual half of a rule
// vanilla only ever implemented in the gameplay half.
//
// It is still a disagreement, and unlike §27's other one — VectorLightBlockers treating the light's
// own cell as open, which is static, one cell, and always in the direction that keeps a lit thing
// lit — this one is beam-sized and blinks on and off as pawns walk through. Issue #48 is the record
// of the opposite sign of the same mistake (a drawn shadow across a wall the glow grid passed light
// through). Whether that is worth it is a taste call, which is why it is a flag and why
// Patch_DoorGlowBlocker exists to measure the coherent alternative against it.
public static class DoorOcclusionMath
{
    // The whole rule. Five inputs, and the order of the tests is the interesting part:
    //
    // blocksLight FIRST, so a transparent edifice is transparent no matter what else is true. This is
    // the branch that makes glass doors and modded windows work without any code that knows what
    // glass is, and vector_light_glass_door pins it: a blockLight=false door reproduces a bare
    // doorway to the last decimal. An open-state term layered on top must not disturb it, and a
    // see-through door that also happens to be open must not somehow become MORE transparent than
    // open — there is no state past "does not occlude".
    //
    // openDoorsPassLight SECOND, so the flag off returns exactly `blocksLight` — the pre-feature
    // expression, character for character. CelestialLightingFeatures' rule is that a flag turned off
    // reproduces the previous behaviour rather than no behaviour, which is what lets the harness A/B
    // against a real baseline instead of a picture of the feature being absent.
    //
    // isDoor THIRD, because only a door has an open state to read. A wall is never open, and passing
    // doorOpen=true for a non-door is a caller bug rather than a request to delete a wall, so the
    // door test gates the open test rather than the two being combined.
    //
    // doorAperture LAST, and once it is being tracked it is the ONLY term that speaks for a door.
    // `doorOpen` is a state; the thing the player is looking at is an animation, and the two disagree
    // at BOTH ends of a slide:
    //
    //   - A door told to SHUT has `Open` false from the first tick of a slide that lasts tens of
    //     ticks (Building_Door.DoorTryClose sets openInt immediately; DrawMovers keeps sliding the
    //     leaves from OpenPct), so a rule reading only `doorOpen` puts a whole-cell occluder back
    //     under a door the game is still drawing half-open. That was issue #174 phase 1.
    //   - A door told to OPEN has `Open` TRUE from the first tick, while OpenPct is still 0 and
    //     vanilla is drawing the leaves shut -- DrawMovers offsets each leaf by 0.45 * OpenPct, so at
    //     OpenPct 0 they have not moved at all. A rule that OR-ed the two therefore turned the cell
    //     into a bare, FULL-WIDTH doorway for the tick or two before the leaves began to move, and
    //     the beam then collapsed to one-eighth width on the next quantisation step. That was issue
    //     #174 phase 2 -- the "room flickers as a door opens" report -- and it is why the two terms
    //     are no longer OR-ed.
    //
    // Both ends get the same answer once the question is asked about the DRAWN gap rather than about
    // the door's state: a cell with a gap in it is not a whole-cell occluder, and a cell with no gap
    // in it is one, whatever `Open` says. The aperture measures the drawn gap. So when it is being
    // tracked it decides alone, and `doorOpen` is not consulted at all.
    //
    // Note that quantisation is NOT what caused the phase 2 defect, though it is what made it
    // conspicuous. Measured offline: with quantisation off the spike still occurs, for one tick
    // instead of three, and collapses further (a full cell to 0.022). The cause was reading `Open`;
    // the step count only sets how long the wrong frame is held.
    //
    // WHY apertureTracked IS A SEPARATE PARAMETER AND NOT JUST `doorAperture > 0`. VectorLightBlockers
    // passes 0 whenever the aperture flag is off, and 0 is also what a genuinely shut door reads. The
    // two must not be conflated: with the flag off an OPEN door still has to pass light, because the
    // flag-off arm has to reproduce the pre-feature frame character for character, and an aperture-
    // only rule would silently wall up every open door in it. This flag says which of the two rules is
    // in force; it never says how wide the gap is.
    public static bool Occludes(
        bool blocksLight, bool isDoor, bool doorOpen, bool openDoorsPassLight, float doorAperture,
        bool apertureTracked)
    {
        if (!blocksLight)
        {
            return false;
        }

        if (!openDoorsPassLight)
        {
            return true;
        }

        if (!isDoor)
        {
            return true;
        }

        // `> 0f` rather than `>= MinimumLeafLength`: this asks whether there is a gap at all, which is
        // a different question from whether a LEAF is long enough to be worth a ray. An aperture below
        // the leaf threshold is a nearly-open door, and its cell is emphatically not a wall.
        //
        // Negated rather than written `doorAperture <= 0f` so a NaN from a modded OpenPct fails SHUT:
        // NaN compares false against every threshold, so `!(NaN > 0f)` is true.
        if (apertureTracked)
        {
            return !(doorAperture > 0f);
        }

        // Untracked: the pre-aperture rule, character for character.
        return !doorOpen;
    }
}
