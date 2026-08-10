using System;
using Verse;

namespace CelestialLighting;

// Is another mod in this load the OWNER of under-roof sky falloff — i.e. does it already provide a
// whole-map, distance-from-an-opening gradient of its own, making §7c's native BFS a duplicate?
//
// Distinct from "is another mod putting some light in some interior cells", which is
// IndoorGlowPassthrough's per-cell question. This one is a whole-map, whole-session fact, and it has
// to be, because the failure it prevents is a SEAM: two gradients with independently tuned maxDepths
// meeting inside one room. A per-cell fallthrough looks reasonable and produces exactly that — a cell
// just past Ambient Light's reach returns 0 from the passthrough and is then answered by our BFS, so
// the room carries a visible discontinuity neither gradient has on its own. §7c's own header made this
// argument and it stands; only the mechanism for detecting the other mod has changed.
//
// WHY THIS NAMES MODS WHEN THE PASSTHROUGH DELIBERATELY DOES NOT. The obvious general test — "has
// anyone other than us patched GlowGrid.GroundGlowAt?" — is wrong, and concretely so. ReBuild: Doors
// and Corners patches exactly that method, but only to let light past its GLASS WALLS; it supplies no
// door gradient at all. Standing §7c down for it would silently delete under-roof falloff for every
// player who has ReBuild, which is a regression rather than a compat fix. The distinguishing property
// is "does this mod own the whole gradient", and nothing observable at runtime answers that — so it is
// a short, explicit list, with the reason recorded next to each entry.
//
// The VALUE still comes from the general passthrough either way. This only decides whether our own BFS
// is allowed to fill in the cells that mod left dark.
public static class UnderRoofFalloffOwner
{
    // Ambient Light (issue #80). Its whole purpose is a BFS-graded fraction of CurSkyGlow pushed into
    // roofed cells by distance from the nearest opening — the same job §7c does, with its own
    // player-facing maxDepth and passThroughPercent sliders. Running both means two gradients, so ours
    // stands down entirely and IndoorGlowPassthrough carries theirs.
    private const string AmbientLightPackageId = "f1995.ambientlight";

    private static bool resolved;
    private static bool present;

    // Cached for the session, including the negative case: a player without any such mod pays one pass
    // over the running mod list at first read and nothing thereafter. This is read once per cell per
    // section regenerate, so it cannot afford to walk the mod list each time.
    public static bool Present
    {
        get
        {
            if (!resolved)
            {
                resolved = true;
                present = IsLoaded(AmbientLightPackageId);
                if (present)
                {
                    Log.Message(
                        "[CelestialLighting] Ambient Light detected; standing our own under-roof sky "
                        + "falloff down and letting its gradient through instead.");
                }
            }

            return present;
        }
    }

    private static bool IsLoaded(string packageId)
    {
        foreach (ModContentPack pack in LoadedModManager.RunningMods)
        {
            if (pack.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
