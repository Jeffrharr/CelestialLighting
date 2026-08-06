using RimWorld.Planet;
using Verse;

namespace CelestialLighting;

// The impure boundary for DESIGN.md §20, §20b and §20c, shaped exactly like LatitudeEffect.cs: it
// pulls live values off the world grid and hands primitives to the pure models (AtmosphericColumn
// for the columns themselves, AerosolDrift for their day-to-day variation), which do the actual math
// and are fully covered by offline unit tests. Keep it that way — if you are tempted to add a
// formula here, it belongs in one of those two files instead.
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
    // (DESIGN.md §20b), driven by the slow day-to-day air-mass drift of §20c. Zero on any unpolluted
    // tile, which is every tile in a game without Biotech — the drift is multiplicative, so it
    // cannot manufacture haze over a tile that has none.
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

        float baseline = AtmosphericColumn.AerosolLoadFraction(tile.elevation, tile.pollution);

        // §20c. The tile's baseline is what the map's own sources put into the air; the multiplier is
        // which air mass is currently sitting over it. Applied HERE, at the boundary, rather than
        // inside AtmosphericColumn: the column model answers "how much of species X is overhead given
        // an altitude and a loading", which is a timeless question, and threading a clock into it
        // would make every one of its callers pass a tick they do not have. This file already reads
        // live state, so one more live read belongs here — and applying it here means the live probe
        // (Source/Probes/SkyColorTemperatureProbe.cs) reports the driven value the sky is actually
        // being tinted toward, which is §18's rule about probes reading the same value as their patch.
        return AerosolDrift.ApplyMultiplier(baseline, AerosolDriftClock.MultiplierForMap(map));
    }

    // The Angstrom exponent for this map's tile — how wavelength-selective its aerosol is, i.e. what
    // SIZE the particles are (DESIGN.md §20c). The two accessors above answer "how much aerosol";
    // this one answers "what kind", which is the input that takes §8 off the Planckian locus.
    //
    // Keyed on Tile.rainfall for the reason AerosolSpectrum.AngstromExponentForRainfall spells out:
    // vanilla's own BiomeWorkers score biomes from rainfall and temperature, so this keys on the same
    // axis the biome label is derived from, but continuously and without a defName table that modded
    // biomes fall off the end of. Tile.rainfall is a plain public float on BASE RimWorld.Planet.Tile
    // in millimetres per year, exactly like elevation and pollution, so it needs no DLC gate and is
    // pinned by ApiCompatibilityTests.Tile_HasRainfall.
    //
    // The guard falls back to the reference exponent rather than to 0: unlike the two fractions
    // above, there is no "identity" value here. An exponent of 0 is not "no effect", it is a specific
    // physical claim (grey, large-particle extinction), so the honest default for "we could not
    // answer this" is the urban-haze middle of the range — which is also the value §20b's single
    // shipped colour was implicitly calibrated at, so an unanswerable tile keeps the shipped look.
    public static float AngstromExponentForMap(Map map)
    {
        Tile tile = TileForMap(map);
        if (tile == null)
            return AerosolSpectrum.ReferenceAngstromExponent;

        return AerosolSpectrum.AngstromExponentForRainfall(tile.rainfall);
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
