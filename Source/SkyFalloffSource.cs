using Verse;

namespace CelestialLighting;

// Single dispatcher for §7b's third CapOcclusion argument -- picks between the two mutually exclusive
// sources for "how much sky is reaching this interior cell": IndoorGlowPassthrough (another MOD's
// under-roof light, gameplay-authoritative wherever it answers) and NativeSkyFalloffGrid (§7c, this
// mod's own whole-map BFS). Patch_IndoorSkyOcclusion's two call sites go through this rather than
// either source directly, so there is exactly one place that decides which (if either) answers a
// given cell.
//
// Deferral, not composition, and deferral at WHOLE-MAP scope rather than per cell. The two sources
// use different maxDepth values and formulas by construction (players tune each independently), so
// blending them (e.g. Max()) would put a visible seam wherever the smaller maxDepth runs out -- a
// discontinuity neither gradient has on its own. Falling through PER CELL has the same fault and is
// easier to write by accident: a cell just past Ambient Light's reach returns 0 from the passthrough
// and would then be answered by our BFS, so the seam reappears inside a single room. So when another
// mod OWNS under-roof falloff (UnderRoofFalloffOwner), our BFS is not consulted anywhere on the map --
// not merely skipped for the cells that mod happened to light.
//
// Their value is also gameplay-authoritative: Ambient Light's own mouseover readout already reports
// exactly it, so it wins outright rather than being treated as one input among several.
//
// WHY THE FIRST ARM IS NO LONGER AMBIENT-LIGHT-SPECIFIC. It used to be AmbientLightCompat, which bound
// by reflection to one named mod -- its map component, its settings object, its GetDepth -- and
// re-derived its private falloff formula byte-for-byte. IndoorGlowPassthrough asks the glow grid
// instead of the mod, so it covers Ambient Light (measured: it recovers the same 0.4583 that
// reflection did, with no reflection) AND every other mod that lights interiors. That matters for a
// case the old arm structurally could not reach: ReBuild: Doors and Corners transpiles GroundGlowAt so
// cells near its GLASS WALLS receive sky glow, and ReBuild's own patch makes it an
// UnderRoofFalloffOwner, standing §7c's BFS down entirely wherever it is installed. A glass wall from a
// mod that does NOT own the gradient is now covered by §7c's own BFS instead (NativeSkyFalloffGrid's
// IsWall reads blockLight directly), so between the two arms every glass-wall mod is covered one way or
// the other -- passthrough wherever another mod owns the gradient, native BFS passthrough everywhere
// else.
public static class SkyFalloffSource
{
    public static float FractionAt(Map map, IntVec3 cell)
    {
        // Whatever another mod put in this cell, whoever it is. Read first because it is
        // gameplay-authoritative wherever it answers.
        float fromOtherMod = IndoorGlowPassthrough.SkyFractionAt(map, cell);

        // Short-circuited rather than passed to the arbiter as a fourth argument, because computing it
        // means BUILDING the whole-map BFS: with an owner present that grid is never wanted, and
        // running it for nothing on every map with Ambient Light installed is exactly the waste §7c's
        // own header set out to avoid.
        if (fromOtherMod > 0f || UnderRoofFalloffOwner.Present
            || !CelestialLightingFeatures.NativeSkyFalloff)
        {
            return SkyFalloffArbitration.Resolve(
                fromOtherMod, UnderRoofFalloffOwner.Present,
                CelestialLightingFeatures.NativeSkyFalloff, nativeFraction: 0f);
        }

        NativeSkyFalloffSettings settings = NativeSkyFalloffSettings.Current;
        float native = NativeSkyFalloffGrid.FractionAt(
            map, cell, map.skyManager.CurSkyGlow,
            settings.MaxDepth, settings.PassThroughPercent, settings.DoorStrengthSensitivity);

        return SkyFalloffArbitration.Resolve(
            fromOtherMod, ownerPresent: false, nativeEnabled: true, native);
    }

    // The same dispatch, bound once for a caller about to ask about a whole section's worth of cells.
    // See NativeSkyFalloffGrid.Reader for the measurement that motivated it; this half hoists what
    // FractionAt above re-reads per cell -- the two feature flags, UnderRoofFalloffOwner.Present, the
    // settings object and CurSkyGlow.
    //
    // The ARBITRATION stays per cell, and must: which source answers depends on whether the other mod
    // contributed anything *at this cell*, which is the one input here that genuinely varies within a
    // section. Only what cannot vary is hoisted.
    public readonly struct SectionReader
    {
        private readonly Map map;
        private readonly bool ownerPresent;
        private readonly bool nativeEnabled;
        private readonly NativeSkyFalloffGrid.Reader native;

        internal SectionReader(
            Map map, bool ownerPresent, bool nativeEnabled, NativeSkyFalloffGrid.Reader native)
        {
            this.map = map;
            this.ownerPresent = ownerPresent;
            this.nativeEnabled = nativeEnabled;
            this.native = native;
        }

        public float FractionAt(IntVec3 cell)
        {
            float fromOtherMod = IndoorGlowPassthrough.SkyFractionAt(map, cell);

            // Same short-circuit as the per-cell path, for the same reason. The difference is only that
            // deciding it costs nothing here: the answer was resolved when the reader was built.
            if (fromOtherMod > 0f || ownerPresent || !nativeEnabled)
            {
                return SkyFalloffArbitration.Resolve(
                    fromOtherMod, ownerPresent, nativeEnabled, nativeFraction: 0f);
            }

            return SkyFalloffArbitration.Resolve(
                fromOtherMod, ownerPresent: false, nativeEnabled: true,
                native.FractionAt(cell.x, cell.z));
        }
    }

    // Resolving the native reader up front is a deliberate difference from the per-cell path, which
    // short-circuits before ever touching NativeSkyFalloffGrid. Building it can mean running the BFS,
    // so the one case this pays for and the per-cell path does not -- an owner present with the
    // feature on -- is bought off by skipping the build in exactly that case, which is what the guard
    // below is for. Without it, §7c's "never consulted when another mod owns the gradient" promise
    // would be broken by a reader that built the grid to hold something it would never read.
    public static SectionReader ForSection(Map map)
    {
        bool ownerPresent = UnderRoofFalloffOwner.Present;
        bool nativeEnabled = CelestialLightingFeatures.NativeSkyFalloff;

        if (map == null || ownerPresent || !nativeEnabled)
            return new SectionReader(map, ownerPresent, nativeEnabled, default);

        NativeSkyFalloffSettings settings = NativeSkyFalloffSettings.Current;
        NativeSkyFalloffGrid.Reader native = NativeSkyFalloffGrid.ReaderFor(
            map, map.skyManager.CurSkyGlow,
            settings.MaxDepth, settings.PassThroughPercent, settings.DoorStrengthSensitivity);

        return new SectionReader(map, ownerPresent, nativeEnabled, native);
    }
}
