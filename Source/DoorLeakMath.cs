using System;

namespace CelestialLighting;

// Pure core, DESIGN.md §7d: how much a door DIMS §7c's native sky falloff (NativeSkyFalloffGrid) as
// the flood crosses it, keyed off the door's own strength. Verse-free — the adapter
// (NativeSkyFalloffGrid) supplies both a door's live ThingDef.BaseMaxHitPoints and the wood-door
// reference this compares it against; this file only turns "how much stronger than a wood door" into
// a multiplicative attenuation on the flood's strength.
//
// DELIBERATELY NOT EXTRA BFS DISTANCE. An earlier version of this file had a stronger door cost extra
// BFS steps, which reads as pushing the room farther away — a security door made the room BEHIND it
// look deeper than it is, not just dimmer. What a stronger door should change is how much light
// SURVIVES the crossing, not how far that light has to travel afterward: the room is exactly as deep
// as its geometry says, same as a plain wood door, just darker. So NativeSkyFalloffGrid keeps its
// original, door-oblivious BFS depth entirely (unweighted, one step per cell, same as pre-§7d) and
// separately propagates a per-cell strength multiplier alongside it — this file computes what a single
// door crossing multiplies that running strength by; NativeSkyFalloffGrid.FractionAt applies the
// result as a final multiply on top of NativeSkyFalloffMath's ordinary depth/maxDepth falloff.
//
// WHY BaseMaxHitPoints AND NOT A BESPOKE "STRENGTH" STAT. RimWorld has no stat that means "how much
// light a door should block" — MaxHitPoints is the closest existing proxy for "how substantial is
// this door TYPE", and ThingDef.BaseMaxHitPoints reads it with no stuff applied (Verse.StatWorker
// gates its entire stuff-factor/offset block behind `if (req.StuffDef != null)`, confirmed by
// decompiling this session), so a plasteel Door and a wood Door — same ThingDef, different stuff —
// read identically. That is deliberate, not a limitation: a security door should leak less than a
// wood one because it IS a sturdier door, not because a player happened to build it from a pricier
// material, and pulling in stuff would also require a matching StatDef check per stuff category we
// have no way to keep exhaustive against modded stuff. A def-level number needs no per-material or
// per-def table to maintain here.
//
// WHY THE REFERENCE IS A WOOD DOOR SPECIFICALLY. "Our default should be wooden doors" (the feature
// request this exists for): the vanilla Door def must multiply the running strength by exactly 1 (no
// change at all), the same as stepping across open ground, so an existing playthrough built entirely
// from ordinary doors sees no change regardless of what material they were built from. Ratio 1 there
// is therefore load-bearing, not incidental — see CrossingMultiplier's own doc comment.
public static class DoorLeakMath
{
    // Exponential decay rate per whole ratio point of doorMaxHitPoints above referenceMaxHitPoints.
    // Tuned against decompiled ThingDef.BaseMaxHitPoints values (DoorBase sets 160, inherited by both
    // Door and Autodoor unless a subclass overrides it): OrnateDoor's 250 (ratio 1.5625) keeps about
    // three-quarters of its strength — a mild effect for a merely fancier door — Anomaly's
    // SecurityDoor's 800 (ratio 5.0) keeps about an eighth, matching "very strong doors should leak
    // almost nothing" without going fully dark, and Odyssey's AncientBlastDoor's 6000 (ratio 37.5)
    // decays to effectively zero — i.e. reads as opaque. A door with a LOWER base than the reference
    // (Odyssey's VacBarrier: 100) simply keeps the flat 1.0 multiplier, the same as the reference
    // itself — this knob only ever dims the flood, never brightens it. See DoorLeakMathTests for these
    // worked examples.
    public const float DefaultSensitivity = 0.5f;

    // doorMaxHitPoints: the door's ThingDef.BaseMaxHitPoints (DoorStrengthReference reads this, not
    // the live Thing's stuffed Thing.MaxHitPoints) — deliberately ignores both stuff material and
    // quality, so two doors built from the same def dim the flood by the same amount regardless of
    // what they're made of.
    // referenceMaxHitPoints: what the vanilla Door def reads for the same stat (DoorStrengthReference).
    // Non-positive is treated as "no reference available" and reproduces the flat pre-feature
    // multiplier of 1 (no dimming at all), rather than risking a divide-by-zero blow-up into an
    // enormous ratio.
    // sensitivity: NativeSkyFalloffSettings.DoorStrengthSensitivity — 0 (or below) reproduces the
    // pre-feature flat multiplier of 1 exactly, the same "slider at its floor is a true no-op" contract
    // every other §7c knob honours.
    public static float CrossingMultiplier(float doorMaxHitPoints, float referenceMaxHitPoints, float sensitivity)
    {
        if (referenceMaxHitPoints <= 0f || sensitivity <= 0f)
            return 1f;

        float ratio = doorMaxHitPoints / referenceMaxHitPoints;
        float extraRatio = ratio - 1f;
        if (extraRatio <= 0f)
            return 1f;

        return Clamp01(MathF.Exp(-sensitivity * extraRatio));
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
