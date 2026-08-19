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
    // The whole rule. Four inputs, and the order of the tests is the interesting part:
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
    public static bool Occludes(bool blocksLight, bool isDoor, bool doorOpen, bool openDoorsPassLight)
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

        return !doorOpen;
    }
}
