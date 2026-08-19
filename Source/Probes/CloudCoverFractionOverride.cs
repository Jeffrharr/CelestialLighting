using HarmonyLib;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead, same
// boundary PlanetsmithTiltOverride draws and for the same reason: this must never reach a player's
// game.
//
// WHY THIS EXISTS. CloudCoverClock.FractionForMap's noise is a pure function of (tileId, absTick,
// wetFraction) -- CloudCoverDrift.cs itself is already fully deterministic given those inputs, no
// change needed there. The instability is entirely upstream, in what LIVE input the impure half
// feeds it: absTick comes from Find.TickManager.TicksAbs, and SetSeason/SetTime jump to a
// day-of-year *within whatever absolute year the clock is already in at boot* (see
// RimWorldTestHarness's ClockProbes.cs header). RimWorld's year is 60 days; CloudCoverDrift's noise
// field repeats only every ~205 years -- so "day 5, hour 1" samples a genuinely different point in
// the field depending on which year the boot happens to land in. Three consecutive fresh runs of
// the same scenario measured ticks_abs_day 65, 65, then 5 with no scenario change, and
// cloud_cover_fraction swung from ~0.14 to ~0.22 as a direct result. That is a real, reproducible
// harness characteristic (day-of-year is not the same as absolute tick), not a CelestialLighting
// bug -- but it makes the live fraction unusable as an exact scenario pin.
//
// So: sidestep it. A Harmony postfix on CloudCoverClock.FractionForMap overwrites its result with a
// fixed constant whenever the harness flag below is on, so a scenario gets a specific, reproducible
// cloud fraction on demand instead of tolerating a value that depends on which year the fixture
// happened to boot into. This is strictly a test seam over our OWN method, so a Harmony patch
// (rather than PlanetsmithTiltOverride's reflection-into-another-mod's-settings approach) is the
// natural shape -- there is no external object to reach into.
//
// PATCHED MANUALLY, NOT VIA [HarmonyPatch]/PatchAll(). The only PatchAll() call in this codebase is
// CelestialLightingMod's, and it scans the SHIPPED assembly -- this file is compiled into the
// separate TestMod/CelestialLighting.Probes.csproj instead, which that scan never sees. Every other
// probe that patches something (GeometryEvalCountProbe, SectionLayerDrawCountProbe,
// AuroraPathTimingProbe) does it the same way this does: a static Install() that builds its own
// Harmony instance and patches explicitly, called once from ProbeRegistration's static constructor.
public static class CloudCoverFractionOverride
{
    public const string FeatureKey = "cloud_cover_forced_fraction";

    // 0.35, not 0 and not 1: high enough that CavityGain's math has real headroom to move (a value
    // near either clamp boundary would mask a clamping bug the same way PlanetsmithTiltOverride's
    // own header warns 90 degrees would for tilt), and it matches CloudCoverDrift.WobbleAmplitude in
    // magnitude only coincidentally -- picked as "a plainly mid-range fraction", not derived from it.
    public const float ForcedFraction = 0.35f;

    // A second key that forces a near-full sky instead of a mid-range one.
    //
    // SEPARATE KEY RATHER THAN A DIFFERENT VALUE ON THE FIRST, because 0.35 is pinned into other
    // scenarios' expected values and moving it would silently rewrite what they measure. This one
    // exists for the case the mid-range fraction cannot serve: §25's sheets are large and few, so at
    // 0.35 a 250-cell map gets two of them and the camera is quite likely to be looking at neither —
    // which produces a healthy-looking run, a full report, and frames with no cloud in them. When
    // the question is "what does the cloud look like", the sky has to have some.
    public const string OvercastFeatureKey = "cloud_cover_forced_overcast";

    // Not 1.0: a value at the clamp boundary would mask a clamping bug, the same reason ForcedFraction
    // is not 0 or 1.
    public const float ForcedOvercastFraction = 0.92f;

    private static bool active;
    private static bool overcast;
    private static bool installed;
    private static bool logged;

    // Called once from ProbeRegistration's static constructor, mirroring GeometryEvalCounters'
    // Install(). Idempotent so a second call (there should never be one, but nothing enforces that)
    // does not double-patch.
    public static void Install()
    {
        if (installed)
            return;

        installed = true;
        Harmony harmony = new Harmony("celestiallighting.probes.cloudcoverfractionoverride");
        // PATCHED ON FractionForTick, NOT FractionForMap, and the difference is load-bearing since §25
        // started latching each sheet's cover at the tick it entered the map. FractionForMap now
        // delegates to FractionForTick, so patching the primitive covers both callers; patching the
        // wrapper instead would leave every sheet placing itself from the REAL drift while every probe
        // reported the forced value — a scenario that looked pinned and photographed something else.
        harmony.Patch(
            AccessTools.Method(typeof(CloudCoverClock), nameof(CloudCoverClock.FractionForTick)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(CloudCoverFractionOverride), nameof(Postfix))));
    }

    public static void Set(bool enabled)
    {
        active = enabled;
        logged = false;
    }

    public static void SetOvercast(bool enabled)
    {
        overcast = enabled;
        logged = false;
    }

    // Logged on every actual override, not only on failure -- see PlanetsmithTiltOverride's own
    // comment on why a silent test hook is worse than a noisy one: it is indistinguishable from the
    // feature under test simply not working.
    private static void Postfix(ref float __result)
    {
        if (!active && !overcast)
            return;

        // Overcast wins when both are on. Stated rather than left to argument order: a scenario that
        // set both would otherwise get whichever this method happened to check first, and the two
        // keys exist precisely because the difference between them is the thing being controlled.
        float forced = overcast ? ForcedOvercastFraction : ForcedFraction;

        // LOGGED ONCE PER ACTIVATION rather than once per call, which §25's per-sheet latch forced:
        // this now runs a dozen times a tick rather than a handful of times a frame. The reason for
        // logging at all is PlanetsmithTiltOverride's — a silent test hook is indistinguishable from
        // the feature under test not working — and one line per flip still says that without burying
        // the rest of the run's log.
        if (!logged)
        {
            logged = true;
            Log.Message(
                $"[CelestialLighting.Probes] Cloud cover fraction override active: {__result} -> {forced}.");
        }

        __result = forced;
    }
}
