using HarmonyLib;
using Verse;

namespace CelestialLighting;

[StaticConstructorOnStartup]
public static class CelestialLightingMod
{
    // StaticConstructorOnStartup ensures this runs after all mods are loaded and RimWorld's own
    // static initialization is complete. PatchAll() scans this assembly for [HarmonyPatch]
    // classes and applies them.
    static CelestialLightingMod()
    {
        new Harmony("celestiallighting").PatchAll();

        // Tell Realistic Axial Tilt, if present, to stand its own lighting patches down — we render
        // the sky, it owns the planet's solar geometry. Done here rather than lazily because it must
        // happen before the first frame draws, and StaticConstructorOnStartup is the one point where
        // every mod's patches are registered but nothing has rendered yet. No-op without RAT.
        AxialTiltCompat.ClaimLighting();

        // Wire the night-radiance moon seam (§7) to the real moon subsystem (§6). MoonSeam defaults
        // to "no moon" so §7 builds and unit-tests standalone with no dependency on §6; here — the
        // one startup point where both subsystems are present in the shipped assembly — we point it
        // at the live MoonPosition adapter so the night floor actually responds to moon phase and
        // altitude (a full moon reads brighter than a new moon; a set moon adds nothing). The
        // reassignment is deliberately kept out of MoonSeam.cs itself so that file stays
        // Verse-free and offline-testable. MoonPosition.SkyForMap already degrades to a
        // below-horizon, unilluminated moon when no game/component is loaded, so this is safe to call
        // on any tick, including during load.
        MoonSeam.Provider = map =>
        {
            MoonPosition.Sky sky = MoonPosition.SkyForMap(map);
            return new MoonState(sky.IlluminatedFraction, sky.ElevationDegrees);
        };
    }
}
