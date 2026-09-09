using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace CelestialLighting.Probes;

// Settles a new player colony on a fresh tile and switches to its map.
//
// WHY THIS EXISTS. Scenarios load a fixture save, so every map they see was GENERATED BEFORE the
// mods under test were ever loaded. That is fine for anything reading a map, and useless for
// anything that hooks map GENERATION — which is where As above, So below II builds its bands. Their
// Patch_MapGenerator_GenerateMap only fires for a player Settlement's own map (ShouldBand: not a
// pocket map, parent is a Settlement, faction is the player's), so no amount of poking at the
// fixture produces one. This runs the real generation path, with their patches installed, and gets
// the map a player would actually get.
//
// AsAboveSoBelowBand exists alongside it and is NOT redundant. That step reconfigures an existing
// map's band component, which is cheap and deterministic and tests the interop wiring; this one pays
// a full map generation — their own comment puts the carve at roughly 3x normal generation time —
// and tests it against a map with real gutters, real carved bands and real see-through cells. Use
// the cheap one unless the expensive one is the point.
//
// NOT AASB2-SPECIFIC. Nothing here names their mod: it is "generate a colony map with whatever mods
// are loaded", which is the thing the harness could not previously express.
public sealed class SettleColonyStep : IStepSpec
{
    public const string TypeName = "SettleColony";

    public string Type => TypeName;

    // It adds a world object and a whole map to the game. Nothing here takes either away.
    public ScenarioResidue Residue => ScenarioResidue.Map;

    // Generating a map into somebody's real colony is not a thing to do down the live channel.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string error)
    {
        error = null;
        return true;
    }
}

public sealed class SettleColonyAction : IStepAction
{
    public string Type => SettleColonyStep.TypeName;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (Find.World == null || Current.Game == null)
            return StepOutcome.Fail("no world — SettleColony needs a game in progress");

        if (!TryPickTile(out PlanetTile tile, out string tileError))
            return StepOutcome.Fail(tileError);

        // The two calls SettleInEmptyTileUtility itself makes, in its order. Going through
        // SettleUtility.AddNewHome rather than assembling a Settlement by hand matters: it is what
        // puts a player-factioned Settlement at the tile, and their ShouldBand reads exactly that.
        Settlement settlement = SettleUtility.AddNewHome(tile, Faction.OfPlayer);

        if (settlement == null)
            return StepOutcome.Fail($"SettleUtility.AddNewHome returned no settlement for tile {tile}");

        // World.info.initialMapSize, not a size of our own: any mod hooking generation sizes itself
        // from the world's own number, and handing it a different one would measure a map shape no
        // player has. AASB2's prefix inflates this z itself, by design.
        Map map = GetOrGenerateMapUtility.GetOrGenerateMap(tile, Find.World.info.initialMapSize, null);

        if (map == null)
            return StepOutcome.Fail($"GetOrGenerateMapUtility returned no map for tile {tile}");

        Current.Game.CurrentMap = map;

        return new StepOutcome();
    }

    // The first tile the game itself considers settleable, rather than a random one: a scenario that
    // generates a different map every run cannot be A/B'd against itself, and RandomStartingTile
    // would do exactly that. Deterministic for a given world seed, which the fixture fixes.
    private static bool TryPickTile(out PlanetTile tile, out string error)
    {
        tile = PlanetTile.Invalid;
        error = null;

        PlanetLayer surface = Find.WorldGrid?.Surface;

        if (surface == null)
        {
            error = "no surface planet layer";
            return false;
        }

        StringBuilder reason = new StringBuilder();

        for (int i = 0; i < surface.TilesCount; i++)
        {
            PlanetTile candidate = new PlanetTile(i, surface);

            reason.Length = 0;

            if (TileFinder.IsValidTileForNewSettlement(candidate, reason))
            {
                tile = candidate;
                return true;
            }
        }

        error = $"no settleable tile on the surface layer ({surface.TilesCount} scanned); "
              + $"last reason: {reason}";
        return false;
    }
}
