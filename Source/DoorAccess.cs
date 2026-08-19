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

    // How far this door has slid, 0 shut and 1 fully open. A door that is not Open reads 0 regardless
    // of what OpenPct says: vanilla keeps counting ticksSinceOpen after the door has shut, so the raw
    // property is only meaningful while Open is true.
    public static float OpenFraction(Building_Door door)
    {
        if (door == null || !door.Open)
        {
            return 0f;
        }

        if (OpenPctGetter == null)
        {
            return 1f;
        }

        return OpenPctGetter(door);
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
