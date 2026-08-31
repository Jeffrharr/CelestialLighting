using Verse;

namespace CelestialLighting;

// One question, asked in one place: does this map sit on a world tile at all?
//
// A POCKET MAP DOES NOT. Vanilla's PocketMapUtility.GeneratePocketMap builds a PocketMapParent and
// never assigns its tile, so it keeps WorldObject's initialiser — PlanetTile.Invalid, tileId -1 —
// for the whole life of the map. That is Anomaly's labyrinth and metal hell, its undercave, Odyssey's
// ancient stockpile / insect lair / space pocket, and any modded generator that calls the same
// helper. It is not a rare corner: an obelisk-generated labyrinth is ordinary Anomaly play.
//
// WHY THIS NEEDS A GATE AT ALL, given that we never index the world grid ourselves. Vanilla's own
// tile accessors disagree about invalid tiles, and the disagreement is silent in both directions:
//
//   Find.WorldGrid[tile]        subscripts the backing List<Tile> UNCHECKED -> ArgumentOutOfRangeException
//   PlanetLayer's own indexer   bounds-checks and returns null
//   WorldGrid.LongLatOf(tile)   substitutes the player's home map tile, or (0,0) if there is none
//   Map.TileInfo                returns the map's pocketTileInfo, so biome reads are always fine
//
// So passing an invalid tile to a vanilla helper is a coin toss between "throws every frame", "hands
// back null" and "quietly answers about somewhere else entirely". Cloud cover drew the first of the
// three: GenTemperature.GetTemperatureFromSeasonAtTile goes through Find.WorldGrid[tile], and it even
// null-checks the result it can never reach because the indexer throws first. A labyrinth therefore
// threw out of SkyManagerUpdate on every frame it was rendered, which takes the mod's whole sky
// composite down with it (see PatchAll-throw notes in Patch_SkyTargetComposite).
//
// WHY THIS IS NOT A CLAUSE ON MapSky. "No world tile" and "no sky" are different questions with
// different answers, and MapSky's header already argues at length against collapsing questions that
// happen to agree on the maps you tested. They agree on today's vanilla pocket maps only by
// coincidence of def data — every one of those biomes lists exactly one weather, so
// MapSkyMath.HasSky returns false for reasons that have nothing to do with the planet grid. A modded
// pocket map with two weathers would be skyful and still tileless, and a surface cavern map is
// skyless with a perfectly good tile. Asking the structural question structurally is what makes this
// hold for content we have not seen.
//
// THE CONVENTION, mirroring Vacuum.cs's: the adapter asks once and either early-returns or falls back
// to a tile-free answer. Do NOT push the check down into a pure function as a bool parameter the way
// §18's `inVacuum` is pushed down — there is no tile-free ARITHMETIC to select here, only a live read
// to decline to make, so the decision belongs at the boundary where the read happens.
public static class MapWorldTile
{
    // True when this map's world tile can safely be handed to a vanilla tile accessor. False on every
    // pocket map.
    //
    // PlanetTile.Valid is vanilla's own predicate (tileId >= 0) rather than one we invented, which is
    // what keeps this agreeing with the bounds check inside PlanetLayer's indexer. Deliberately NOT
    // map.IsPocketMap: that answers "how was this map made", and what the accessors actually branch
    // on is the tile id. The two coincide today; only one of them is the thing that throws.
    public static bool HasWorldTile(Map map) => map != null && map.Tile.Valid;
}
