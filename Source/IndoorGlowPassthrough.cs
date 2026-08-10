using UnityEngine;
using Verse;

namespace CelestialLighting;

// The general "another mod lit this interior, so let its light reach the screen" term for §7b.
// See DESIGN.md §7b. IndoorOcclusionMath.IndoorSkyGlowFraction carries the full derivation; this file
// is the thin live-state half that reads the glow grid.
//
// REPLACES AmbientLightCompat, and the reason is the point of the whole change. That class bound by
// reflection to one named mod (f1995.ambientlight) — its map component, its settings object, its
// GetDepth method — and re-derived its private falloff formula byte-for-byte so we could ask it how
// much sky it had put in a cell. It worked, and it was the wrong shape: it fixed exactly one mod, it
// broke whenever that mod refactored, and the next mod to brighten interiors would have needed its own
// copy of the same scaffolding. That per-mod pattern was the common root of this subsystem's
// compatibility bugs, not a one-off.
//
// This asks the question of the game instead of the mod. Vanilla's GroundGlowAt suppresses its sky
// term for a roofed cell, so on a roofed cell anything above the artificial (lamp) glow was put there
// by somebody else and is sky-sourced by elimination. No package id, no reflection, no formula to keep
// in sync — a mod that brightens interiors is picked up because it brightened interiors.
//
// TWO GLOW READS PER CELL PER SECTION REGENERATE, which is the cost of asking rather than reflecting.
// Accepted deliberately: this runs on a map-mesh dirty flag, never per frame (DESIGN.md §16 has the
// flag-to-layers table), so it lands on roof edits and lamp toggles rather than the render hot path.
public static class IndoorGlowPassthrough
{
    // The sky-sourced share of `cell`'s ground glow, in [0, 1]. 0 whenever the feature is off, on an
    // unmodded install, on an unroofed cell, or wherever no mod has added anything — and 0 is exactly
    // the value IndoorOcclusionMath.CapOcclusion treats as "no cap", so "absent" and "present but
    // contributing nothing" compose identically and no caller needs to branch on which it is.
    //
    // Assumes an in-bounds cell: it indexes the glow grid directly, and the corner pass already has to
    // do its own InBounds check for the off-map neighbours a map-edge lattice point reaches.
    public static float SkyFractionAt(Map map, IntVec3 cell)
    {
        if (!CelestialLightingFeatures.IndoorGlowPassthrough || map == null)
            return 0f;

        GlowGrid glowGrid = map.glowGrid;
        if (glowGrid == null)
            return 0f;

        // GroundGlowAt is the patched surface — whatever every mod in the load order has made of it.
        // VisualGlowAt is GetAccumulatedGlowAt, the raw artificial accumulation, which those mods do
        // not patch (checked against Ambient Light's assembly: it postfixes GroundGlowAt only). The
        // difference is therefore "sky that somebody added", and recomputing the artificial half
        // ourselves is what makes it work at all — see IndoorSkyGlowFraction's header for why the
        // obvious `ignoreSky: true` differencing does not.
        Color32 accumulated = glowGrid.VisualGlowAt(cell);
        float artificial = IndoorOcclusionMath.ArtificialGlow(
            accumulated.r, accumulated.g, accumulated.b, accumulated.a);

        return IndoorOcclusionMath.IndoorSkyGlowFraction(
            glowGrid.GroundGlowAt(cell), artificial, map.roofGrid.Roofed(cell));
    }
}
