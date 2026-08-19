using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CelestialLighting;

// §27e phase 2's clock: while a door is mid-slide, something has to tell §27's field that the
// geometry moved, once per quantisation step.
//
// WHY A COMPONENT AND NOT A PATCH ON Building_Door.Tick. That method runs for every door on the map
// every tick, and a base with two hundred doors would pay a Harmony prologue two hundred times a tick
// to discover that none of them are moving. This watches only the doors that are actually animating —
// a set that is empty almost always, and holds one or two entries when it does not — so the standing
// cost when nothing is opening is one null check on an empty collection.
//
// WHY NOT A MapComponent, which is the more obvious home for per-map state: Map.ExposeComponents
// scribes a permanent node per component, so deleting the type later logs two red errors per map on
// every load forever. MapComponent_SunShadowAxis.cs is this repo's tombstone for exactly that
// mistake. A GameComponent holding a map-keyed dictionary of live things carries no save data at all,
// which is right for state that is meaningless across a load anyway — a door mid-swing at save time
// is shut or open by the time the game comes back.
public class GameComponent_DoorAperture : GameComponent
{
    // The doors currently mid-slide, with the quantised aperture each was last baked at. Comparing
    // against that is what turns "OpenPct changed" (every tick) into "the geometry we actually drew
    // changed" (eight times a swing).
    private static readonly Dictionary<Building_Door, float> Animating =
        new Dictionary<Building_Door, float>();

    // Two scratch lists, reused rather than rebuilt because this runs every tick a door is moving.
    //
    // Sweeping stays OFF the dictionary's own enumerator on purpose: Advance writes the new aperture
    // back, and on Mono/net481 writing an existing key during a foreach invalidates the enumerator
    // and throws. Snapshotting the keys first is a couple of pointer copies for a set that holds one
    // or two entries, and it is the difference between this working and throwing every time a door
    // crosses a quantisation step.
    private static readonly List<Building_Door> Sweeping = new List<Building_Door>();
    private static readonly List<Building_Door> Finished = new List<Building_Door>();

    public GameComponent_DoorAperture(Game game)
    {
    }

    // Called from VectorLightDoorEvents when a door starts opening or closing. Registering on BOTH
    // transitions is deliberate: a door that is interrupted mid-open and shuts again animates back
    // down through the same steps, and a beam that tracked the way open but snapped shut would look
    // worse than one that never tracked at all.
    public static void Watch(Building_Door door)
    {
        if (door == null || !CelestialLightingFeatures.VectorLightDoorAperture)
        {
            return;
        }

        // -1 rather than the current aperture, so the first sweep always counts as a change and bakes
        // the opening frame. Seeding with the real value would skip the first step, which is the one
        // that matters most: it is the frame the beam first appears in.
        Animating[door] = -1f;
    }

    public override void GameComponentTick()
    {
        if (Animating.Count == 0)
        {
            return;
        }

        Sweeping.AddRange(Animating.Keys);

        foreach (Building_Door door in Sweeping)
        {
            Advance(door, Animating[door]);
        }

        Sweeping.Clear();
        DropFinished();
    }

    // One door, one tick. Dirties §27's geometry only when the QUANTISED aperture has moved, which is
    // the entire performance argument for phase 2 — an unquantised comparison here would bake on
    // every tick of every swing and there would be nothing left to measure.
    private static void Advance(Building_Door door, float lastBakedAperture)
    {
        if (!door.Spawned || door.Map == null)
        {
            Finished.Add(door);
            return;
        }

        float aperture = DoorApertureMath.Quantise(
            DoorAccess.OpenFraction(door), DoorApertureMath.DefaultQuantisationSteps);

        if (aperture != lastBakedAperture)
        {
            VectorLightField.MarkGeometryDirtyAround(door.Map, door.Position);
            Animating[door] = aperture;
            DirtyRequests++;
        }

        // Done once the slide has run out at either end. A door sitting fully open is an ordinary
        // hole and a shut one an ordinary blocker; neither needs watching, and leaving them in the
        // set would turn this into a per-door-per-tick sweep of the whole base.
        if (aperture >= 1f || (aperture <= 0f && !door.Open))
        {
            Finished.Add(door);
        }
    }

    private static void DropFinished()
    {
        foreach (Building_Door door in Finished)
        {
            Animating.Remove(door);
        }

        Finished.Clear();
    }

    // How many times this component has asked §27 to rebake since the last Reset. THE performance
    // number for phase 2: it is bakes-per-swing, which is what quantisation exists to bound, and it
    // is the one figure that separates "the beam tracks the door" from "the beam tracks the door and
    // costs a rebake every tick to do it". Counted here rather than timed, because a per-call timer
    // cannot see a call COUNT going up — the exact blind spot recorded for Dubs in the harness notes.
    public static int DirtyRequests;

    // The harness flips flags mid-scenario, and a door left in the set under a flag that is now off
    // would keep dirtying geometry for a feature nobody asked for. Also gives the arms a clean start.
    public static void Reset()
    {
        Animating.Clear();
        DirtyRequests = 0;
    }

    // For the probe: how many doors are currently being tracked. This is the number that says whether
    // the sweep is bounded — if it ever reads more than the doors actually moving, the drop logic has
    // stopped working and the cost grows with the size of the base.
    public static int WatchedCount => Animating.Count;
}
