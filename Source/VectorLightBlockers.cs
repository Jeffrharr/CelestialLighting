using RimWorld;
using Verse;

namespace CelestialLighting;

// §27's live half of the occlusion question: which cells around a light stop it, expressed as the
// occluding segments VectorLightMath casts rays at.
//
// THE BLOCKER SET IS VANILLA'S, EXACTLY. `ThingDef.blockLight` on the edifice is the same test
// Verse.Building writes into GlowGrid's own lightBlockers bit array on spawn and despawn
// (Building.cs SpawnSetup/DeSpawn), which is the set vanilla's flood refuses to pass. Issue #48
// names the consequence of getting this wrong: a drawn shadow appearing across a wall that the glow
// grid itself passed straight through, disagreeing with the gameplay light in exactly the places a
// player is looking. Asking the same question of the same grid makes that disagreement impossible
// rather than unlikely.
//
// WHY A WINDOW PER LIGHT. Every ray is tested against every segment handed over, so cost scales with
// how much wall a light is given rather than with how much it can see. On a 250x250 colony a single
// torch handed the whole map would be tested against every wall in the base. The window is the
// light's own reach plus a cell, which for a radius-14 lamp is 31x31 — three hundredths of a percent
// of that map.
public static class VectorLightBlockers
{
    // Occluding segments within one light's reach, in world cell coordinates.
    //
    // The light's OWN cell is deliberately treated as open even when something on it blocks light. A
    // wall-mounted lamp, or a mod's glowing wall, would otherwise be sealed inside its own occluder
    // and light nothing at all — where vanilla's flood simply starts on that cell and spreads out.
    // This is the one place §27 knowingly disagrees with the blocker grid, and it disagrees in the
    // direction that keeps a lit thing lit.
    public static VectorLightMath.Segment[] SegmentsAround(Map map, IntVec3 centre, float radius)
    {
        if (map == null)
            return new VectorLightMath.Segment[0];

        int pad = (int)System.Math.Ceiling(radius) + 1;
        int minX = System.Math.Max(centre.x - pad, 0);
        int minZ = System.Math.Max(centre.z - pad, 0);
        int maxX = System.Math.Min(centre.x + pad, map.Size.x - 1);
        int maxZ = System.Math.Min(centre.z + pad, map.Size.z - 1);

        int width = maxX - minX + 1;
        int height = maxZ - minZ + 1;

        if (width <= 0 || height <= 0)
            return new VectorLightMath.Segment[0];

        bool[] blocked = new bool[width * height];
        FillWindow(map, centre, minX, minZ, width, height, blocked);

        return VectorLightMath.SilhouetteSegments(blocked, width, height, minX, minZ);
    }

    private static void FillWindow(
        Map map, IntVec3 centre, int minX, int minZ, int width, int height, bool[] blocked)
    {
        EdificeGrid edifices = map.edificeGrid;

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                IntVec3 cell = new IntVec3(minX + x, 0, minZ + z);
                blocked[z * width + x] = cell != centre && BlocksLight(edifices, cell);
            }
        }
    }

    // The per-cell occlusion question. The rule itself lives in DoorOcclusionMath so it can be
    // exhausted offline; everything here is the reading of live state that a pure function cannot do.
    //
    // The Building_Door cast is the only new work per cell, and it is a type check on a reference we
    // had already fetched — no grid lookup, no allocation. Building_Door rather than an interface or
    // a def flag because `Open` is where vanilla itself keeps the answer, and every modded door worth
    // supporting derives from it (Steve's Doors' Building_UnmirroredDoor does, so its glass doors and
    // its opaque ones both answer correctly without a compat entry).
    private static bool BlocksLight(EdificeGrid edifices, IntVec3 cell)
    {
        Building edifice = edifices[cell];
        if (edifice == null || edifice.def == null)
        {
            return false;
        }

        Building_Door door = edifice as Building_Door;
        return DoorOcclusionMath.Occludes(
            edifice.def.blockLight,
            door != null,
            door != null && door.Open,
            CelestialLightingFeatures.VectorLightOpenDoors);
    }
}
