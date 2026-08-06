using RimWorld.Planet;
using Verse;

namespace CelestialLighting;

// The impure boundary for DESIGN.md §20 and §20b, shaped exactly like LatitudeEffect.cs: it pulls
// live values off the world grid and hands primitives to the pure model (AtmosphericColumn), which
// does the actual math and is fully covered by offline unit tests. Keep it that way — if you are
// tempted to add a formula here, it belongs in AtmosphericColumn.cs instead.
//
// Two fields off one tile, two fractions out: `elevation` -> the Rayleigh air column §20 scales the
// sunset by, and `pollution` -> the boundary-layer aerosol column §20b loads on top of it. They stay
// separate accessors because they are separate questions with separate scale heights, and the only
// place they are ever combined is the colour curve that knows what it wants them for.
//
// It is its own small file rather than a private method on Patch_SkyColorTemperature because the
// live probe (Source/Probes/SkyColorTemperatureProbe.cs) has to read the *same* value the patch
// does. §18's rule that a probe reads the same gate as its patch applies here for the same reason:
// a probe reporting a colour temperature the sky is not actually being tinted toward is worse than
// no probe at all.
//
// NAMING (this is load-bearing, see §20). "elevation" already means SUN elevation everywhere in this
// subsystem — Patch_SkyColorTemperature reads `float elevation = SolarPosition.ElevationForMap(map)`
// and SkyColorTemperature's whole API is keyed on `elevationDegrees`. RimWorld's own field for
// terrain height is unfortunately also called `elevation`. So the moment it crosses into our code it
// is renamed to siteAltitudeMetres, and no value derived from it is ever called "elevation" again.
public static class SiteAltitude
{
    // Fraction of the sea-level air column still overhead at this map's tile, in (0, 1]. 1 on a
    // sea-level tile, ~0.62 on a 4000 m mountain.
    public static float PressureFractionForMap(Map map) =>
        AtmosphericColumn.RayleighPressureFraction(SiteAltitudeMetresForMap(map));

    // Fraction of a full sea-level aerosol load still overhead at this map's tile, in [0, 1]
    // (DESIGN.md §20b). Zero on any unpolluted tile, which is every tile in a game without Biotech.
    //
    // Tile.pollution is read here rather than anywhere else for the same reason elevation is: it is
    // a live-game read, and everything downstream of it is pure. It is a plain public float on BASE
    // RimWorld.Planet.Tile despite being Biotech's mechanic — all DLC code ships in the base
    // assembly — so this needs no ModsConfig.BiotechActive gate and simply reads 0 everywhere when
    // Biotech is absent. That is the identical situation to §18's BiomeDef.inVacuum read, and §18's
    // rule applies for the same reason: a DLC branch could only ever agree with the field, and would
    // be a second thing to keep in sync. ApiCompatibilityTests.Tile_HasPollution pins it.
    //
    // Note both public accessors here read the tile independently rather than sharing one lookup.
    // The read is a bounds-checked list index, so the duplication costs nothing measurable, and the
    // alternative — a struct carrying both fractions — would change the shape §20 deliberately gave
    // this file (one primitive out per question) for no gain.
    public static float AerosolFractionForMap(Map map)
    {
        Tile tile = TileForMap(map);
        if (tile == null)
            return 0f;

        return AtmosphericColumn.AerosolLoadFraction(tile.elevation, tile.pollution);
    }

    // Metres above sea level for the map's world tile. RimWorld.Planet.Tile.elevation is a plain
    // public float on the BASE Tile type (not a SurfaceTile/DLC addition — verified by decompiling
    // 1.6 Assembly-CSharp and pinned by ApiCompatibilityTests.Tile_HasElevation), and it is already
    // in metres: vanilla worldgen defaults it to 100 f and pushes mountainous tiles into the
    // thousands.
    private static float SiteAltitudeMetresForMap(Map map)
    {
        Tile tile = TileForMap(map);
        if (tile == null)
            return 0f;

        return tile.elevation;
    }

    // The map's world tile, or null when the question cannot be honestly answered.
    //
    // Both guards below make their caller fall back to the sea-level, unpolluted answer —
    // pressureFraction 1 and aerosolFraction 0, i.e. bit-identical to the pre-§20 curve. That is the
    // right default for "we could not honestly answer this", because it is the behaviour the mod
    // shipped with rather than a new invented one.
    private static Tile TileForMap(Map map)
    {
        PlanetLayer layer = map.Tile.Layer;

        // Non-surface PlanetLayer (an Odyssey orbital ring, and whatever later layers land on the
        // same mechanism). Those tiles still carry an `elevation` field because it is on base Tile,
        // but it is not a terrain height up there and reading it would silently make orbital sky
        // colour a function of a meaningless number. In practice §18's vacuum gate short-circuits
        // space maps before the value is ever used; this guard is what makes that a belt-and-braces
        // arrangement rather than an unstated dependency on the two gates always agreeing.
        if (!layer.IsRootSurface)
            return null;

        // Deliberately the PlanetLayer indexer rather than Find.WorldGrid[map.Tile]: the two reach
        // the same Tile, but WorldGrid's indexer subscripts the backing List<Tile> unchecked while
        // the layer's bounds-checks and returns null. A pocket map (vanilla's undercave, a Biomes!
        // Caverns cavern) has no world tile — PlanetTile.Invalid, tileId -1 — so the WorldGrid form
        // would throw there. §8 itself never gets this far on those maps (MapSky.IsEnclosed returns
        // first), but SiteAltitude is a shared helper and must not throw for a caller that has not
        // made that check.
        return layer[map.Tile];
    }
}
