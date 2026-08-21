using System;
using System.Reflection;
using RimWorld;
using Verse;

namespace CelestialLighting;

// Building_Door.OpenPct is `protected virtual`, and §27e phase 2 needs it every bake to know how far
// a door has slid. Same shape and same reasoning as GlowGridAccess: the reflection lives in one named
// file so the next thing that needs it reuses this rather than reinventing it.
//
// WHY A DELEGATE TO THE PROPERTY AND NOT THE BACKING FIELD. `ticksSinceOpen` and `TicksToOpenNow` are
// both reachable and would reproduce vanilla's own expression, but OpenPct is VIRTUAL — a modded door
// is free to override it, and several do, precisely because their leaves animate differently. An open
// -instance delegate built from the base MethodInfo still dispatches virtually, so reading through it
// honours the override while reading the fields would silently ignore it and desynchronise our beam
// from the door the player is actually watching.
//
// WHY A CACHED DELEGATE AND NOT MethodInfo.Invoke. This is called once per open door per bake, inside
// the geometry path §27 already treats as hot. Invoke boxes its arguments and its return value on
// every call; a delegate created once is an ordinary virtual call.
public static class DoorAccess
{
    private static readonly Func<Building_Door, float> OpenPctGetter = BuildOpenPctGetter();

    // Null when a RimWorld update renames or reshapes OpenPct. Callers treat that as "no aperture
    // detail available" and fall back to the boolean rule, so a rename costs the animation tracking
    // and not the feature — see VectorLightBlockers.OpenFractionOf.
    public static bool Available => OpenPctGetter != null;

    private static Func<Building_Door, float> BuildOpenPctGetter()
    {
        PropertyInfo property = typeof(Building_Door).GetProperty(
            "OpenPct", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        MethodInfo getter = property?.GetGetMethod(nonPublic: true);
        if (getter == null)
        {
            Log.Warning(
                "[CelestialLighting] Building_Door.OpenPct not found — §27e will open doors in one "
                + "step instead of tracking their slide.");
            return null;
        }

        return (Func<Building_Door, float>)Delegate.CreateDelegate(
            typeof(Func<Building_Door, float>), getter);
    }

    // How far this door has slid, 0 shut and 1 fully open. Read through the CLOSE as well as the open:
    // this is the drawn aperture, not the door's open STATE, and the two disagree for the whole length
    // of a close.
    //
    // WHY NOT GATE ON `Open`, WHICH IS WHAT THIS DID. `Building_Door.DoorTryClose` sets `openInt =
    // false` on the tick the door is told to shut (Building_Door.cs:502), and `Tick` then decrements
    // `ticksSinceOpen` one per tick down to zero (:327-331). So `Open` goes false at the START of the
    // slide while `OpenPct`, a ratio of that counter, ramps correctly down through it. Gating here on
    // `Open` therefore returned 0 for the whole close and the beam snapped shut in one frame while the
    // player watched the leaves take another forty ticks to arrive — §27e phase 2's own bug, in the
    // one direction phase 2 never filmed. DESIGN.md calls a beam disagreeing with the animation "the
    // most conspicuous moment there is to disagree", which is as true shutting as opening.
    //
    // WHY READING IT UNGATED IS SAFE, WHICH IS THE PART THAT WAS ACTUALLY BEING WORRIED ABOUT. The old
    // comment said the raw property is only meaningful while Open is true. It is meaningful whenever
    // the door is drawn, because it is what the door is drawn FROM: `Building_Door.DrawMovers` slides
    // each leaf by +/-0.45 * OpenPct, so a door whose OpenPct did not fall back to zero when shut
    // would render its own leaves standing open. Vanilla's shut door reads `ticksSinceOpen == 0` and a
    // door saved open comes back with `ticksSinceOpen = TicksToOpenNow` (:305-307), so both ends are
    // already right; and a modded override cannot lie to us here without lying to vanilla's own
    // renderer first. That is a stronger guarantee than the gate it replaces.
    //
    // The clamp is not a formality. `OpenPct` is `protected virtual`, TicksToOpenNow can in principle
    // be zero, and `!(value > 0f)` catches NaN as well as negatives -- a NaN reaching the occlusion
    // rule would compare false against every threshold and quietly unblock a shut door.
    public static float OpenFraction(Building_Door door)
    {
        if (door == null)
        {
            return 0f;
        }

        // No delegate means no animation detail at all, so fall back to the boolean the feature had
        // before phase 2: fully open while Open, fully shut the instant it is not.
        if (OpenPctGetter == null)
        {
            return door.Open ? 1f : 0f;
        }

        float slid = OpenPctGetter(door);
        if (!(slid > 0f))
        {
            return 0f;
        }

        return slid > 1f ? 1f : slid;
    }

    // Which world axis the leaves slide along, matching Building_Door.DrawMovers exactly: it rotates
    // the door's own Rotation clockwise and pushes the movers along (0, 0, +/-size.x), so a door
    // facing north or south slides along X and one facing east or west slides along Z. Derived from
    // Rotation rather than from which neighbours are walls, because the point is to track the DRAWN
    // leaves, and Rotation is what vanilla draws them from.
    public static bool LeavesSlideAlongX(Building_Door door)
    {
        Rot4 rotation = door.Rotation;
        return rotation == Rot4.North || rotation == Rot4.South;
    }
}
