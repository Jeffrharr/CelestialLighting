using Verse;

namespace CelestialLighting;

// One shared, render-safe way to ask "what room is this cell in?", used by every §15 consumer.
//
// Why not `cell.GetRoom(map)`. That is the obvious call, and it is what Perspective: Eaves uses, but
// it routes through `RegionGrid.GetValidRegionAt`, which does two things that are wrong to do from
// inside a section regenerate:
//
//   1. It calls `regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms()` — a full region/room rebuild
//      kicked off from inside the render path. Rebuilding rooms can itself dirty map-mesh sections,
//      which is precisely what we are in the middle of regenerating.
//   2. When the updater is disabled with work outstanding it emits `Log.Warning` per call. We would
//      make up to 361 of these calls per section, so a single disabled window turns into log spam
//      measured in thousands of lines.
//
// `GetValidRegionAt_NoRebuild` has neither behaviour: it reads the region grid as it stands and
// returns null if the answer is not ready yet. Null is safe for both callers by construction —
// EavesMath's `hasRoom: false` means "no eave" for the shadow mesh and "still counts as covered" for
// §7b occlusion, i.e. each falls back to exactly the pre-§15 behaviour rather than to something new.
//
// And in practice the answer is always ready. `Map.FinalizeInit` sets `regionAndRoomUpdater.Enabled`
// and calls `RebuildAllRegionsAndRooms()` BEFORE it queues `mapDrawer.RegenerateEverythingNow()`
// (decompiled to confirm the order), so by the time any section mesh is built the rooms exist. The
// null path is a guard against future reorderings, not a case we expect to hit.
public static class RoomLookup
{
    // Mirrors RegionAndRoomQuery.RoomAt's own Region -> District -> Room walk. RoomAt filters on
    // RegionType.Set_All, which admits every region type there is, so there is nothing to filter.
    public static Room RoomAtNoRebuild(Map map, IntVec3 cell)
    {
        Region region = map.regionGrid.GetValidRegionAt_NoRebuild(cell);
        return region?.District?.Room;
    }
}
