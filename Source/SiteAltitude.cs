using RimWorld.Planet;
using Verse;

namespace CelestialLighting;

// The impure boundary for DESIGN.md §20, shaped exactly like LatitudeEffect.cs: it pulls one live
// value off Find.WorldGrid and hands a primitive to the pure model (AtmosphericColumn), which does
// the actual math and is fully covered by offline unit tests. Keep it that way — if you are tempted
// to add a formula here, it belongs in AtmosphericColumn.cs instead.
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

    // Metres above sea level for the map's world tile. RimWorld.Planet.Tile.elevation is a plain
    // public float on the BASE Tile type (not a SurfaceTile/DLC addition — verified by decompiling
    // 1.6 Assembly-CSharp and pinned by ApiCompatibilityTests.Tile_HasElevation), and it is already
    // in metres: vanilla worldgen defaults it to 100 f and pushes mountainous tiles into the
    // thousands.
    //
    // Both guards below return 0 m — sea level, i.e. pressureFraction 1, i.e. bit-identical to the
    // pre-§20 curve. That is the right default for "we could not honestly answer this", because it
    // is the behaviour the mod shipped with rather than a new invented one.
    private static float SiteAltitudeMetresForMap(Map map)
    {
        PlanetLayer layer = map.Tile.Layer;

        // Non-surface PlanetLayer (an Odyssey orbital ring, and whatever later layers land on the
        // same mechanism). Those tiles still carry an `elevation` field because it is on base Tile,
        // but it is not a terrain height up there and reading it would silently make orbital sky
        // colour a function of a meaningless number. In practice §18's vacuum gate short-circuits
        // space maps before the value is ever used; this guard is what makes that a belt-and-braces
        // arrangement rather than an unstated dependency on the two gates always agreeing.
        if (!layer.IsRootSurface)
            return 0f;

        // Deliberately the PlanetLayer indexer rather than Find.WorldGrid[map.Tile]: the two reach
        // the same Tile, but WorldGrid's indexer subscripts the backing List<Tile> unchecked while
        // the layer's bounds-checks and returns null. A pocket map (vanilla's undercave, a Biomes!
        // Caverns cavern) has no world tile — PlanetTile.Invalid, tileId -1 — so the WorldGrid form
        // would throw there. §8 itself never gets this far on those maps (MapSky.IsEnclosed returns
        // first), but SiteAltitude is a shared helper and must not throw for a caller that has not
        // made that check.
        Tile tile = layer[map.Tile];
        if (tile == null)
            return 0f;

        return tile.elevation;
    }
}
