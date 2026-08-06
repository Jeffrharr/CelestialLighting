using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads §19c back for the live tile/tick. Five metrics off one class, the LimbRefractionProbe
// pattern, because IProbe.Read returns a single float and a hue needs more than one number:
//
//   purple_light       the window envelope in [0, 1] — 0 outside -6..-4, 1 at -5.
//   purple_hue_green   the composed hue's GREEN channel. Red and blue are both pinned at 1 by
//                      BalancedBlueFraction (that is what "balanced" means) and so carry no signal,
//                      exactly as limb_tint_green's header says of its own normalisation. Green
//                      below 1 IS the green deficit, and the deficit IS the purple.
//   purple_sky_red     the live colors.sky channels AFTER all four sky patches have run. Pinned as
//   purple_sky_green   a TRIPLE so a scenario can assert the whole B > R > G ordering — that green
//   purple_sky_blue    is the minimum is what says the sky is purple rather than merely differently
//                      blue, and no single channel carries that claim on its own.
//
// The first two read the SHARED adapter rather than recomputing, so the probe cannot disagree with
// the patch about whether a gate fired.
//
// DELIBERATELY DOES NOT CALL WeatherWorker.CurSkyTarget, for the reason PolarNightBlueProbe spells
// out: that method slew-rate-limits at |latitude| >= 75 and MUTATES
// WeatherManager.prevSkyTargetLerp/currSkyTargetLerp doing so, so a probe calling it would advance
// vanilla's slew state as a side effect of being measured. The two sky-channel metrics instead read
// the finished material the way OverlayBrightnessProbe does — MatBases.LightOverlay.color, which
// SkyManager.SkyManagerUpdate assigns straight from curSky.colors.sky.
//
// That last point carries OverlayBrightnessProbe's caveat with it: the material is only assigned
// when map == Find.CurrentMap, so on a non-current map the two sky metrics report the last current
// map's colour. Scenarios run one map, so this has never bitten, but it is why the envelope and hue
// metrics exist as the primary assertions and the material reads are the corroboration.
public sealed class PurpleLightProbe : IProbe
{
    public enum Metric
    {
        Window,
        HueGreen,
        SkyRed,
        SkyGreen,
        SkyBlue,
    }

    private readonly Metric _metric;

    public PurpleLightProbe(string name, Metric metric)
    {
        Name = name;
        _metric = metric;
    }

    public string Name { get; }

    public float Read(Map map) => _metric switch
    {
        Metric.Window => PurpleLight.WindowStrengthFor(map),
        Metric.HueGreen => HueGreenFor(map),
        Metric.SkyRed => UnityEngine.Mathf.Max(0f, MatBases.LightOverlay.color.r),
        Metric.SkyGreen => UnityEngine.Mathf.Max(0f, MatBases.LightOverlay.color.g),
        Metric.SkyBlue => UnityEngine.Mathf.Max(0f, MatBases.LightOverlay.color.b),
        _ => 0f,
    };

    // Outside the window the composed hue is meaningless rather than zero (PurpleLight.ComposedHueFor
    // deliberately does not repeat the gates), so report a flat 1 there — "no deficit" — rather than
    // a number the caller would have to know to ignore. A scenario sampling outside the window then
    // reads 1.0 and fails loudly against a deficit expectation instead of passing vacuously.
    private static float HueGreenFor(Map map)
    {
        if (PurpleLight.WindowStrengthFor(map) <= 0f)
            return 1f;

        return PurpleLight.ComposedHueFor(map).G;
    }
}
