using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads back §19's band strength for the live tile/tick, in [0, 1]: 0 outside the ozone twilight
// band, 1 while the sun sits between -7.2 and -12 degrees. Reads the SHARED adapter rather than
// recomputing, so the probe cannot disagree with the patch about whether a gate fired — this is the
// same discipline TwilightWarmth exists to enforce for §2, and §19 needs it more than most because
// two different consumers (the sky tint and §7a's brightness floor) read this one number.
//
// A scenario can therefore prove the subsystem's central claim numerically: hold latitude 78 in
// midwinter and this reads ~1.0 at every hour of the day (the sun never leaves the band — that IS
// polar night), while at latitude 0 the same curve gives a nonzero window only ~25 minutes wide
// around dusk. Same function, different dwell, and no latitude term anywhere in it.
//
// DELIBERATELY DOES NOT CALL WeatherWorker.CurSkyTarget. That method slew-rate-limits its threshold
// lerp at |latitude| >= 75 and MUTATES WeatherManager.prevSkyTargetLerp/currSkyTargetLerp doing so —
// at exactly the latitudes this subsystem targets. A probe that called it would advance vanilla's
// slew state as a side effect of being measured. Recomputing from the adapter avoids that entirely.
public sealed class PolarNightBlueProbe : IProbe
{
    public string Name => "polar_night_blue";

    public float Read(Map map) => OzoneTwilight.BandStrengthFor(map);
}
