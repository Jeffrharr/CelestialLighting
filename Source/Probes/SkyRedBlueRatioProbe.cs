using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and instead compiled into TestMod/CelestialLighting.Probes.csproj —
// the shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// The OUTPUT half of §20d: the red/blue ratio of the colour Patch_SkyColorTemperature is actually
// tinting toward, from the same SkyColorForElevation composition the patch calls with the same four
// inputs. One number, and it is the right one — R/B is what "how far along the warm axis is this
// colour" means, and it is the quantity every §20d invariant is stated in offline.
//
// WHY A RATIO AND NOT A KELVIN. Because the whole point of §20d is that the composed colour is NOT a
// blackbody at any temperature once the aerosol's spectral shape is applied, so projecting it back
// onto the Planckian locus to report a Kelvin would throw away exactly the thing the scenario is
// there to observe. sky_color_temperature still reports the clean-air Kelvin, honestly labelled as
// such; this reports where the sky actually ended up.
//
// Reading it: ~18 on a clean sea-level horizon (§8's 2000 K anchor, 1.0 / 0.055), rising as aerosol
// with a high Angstrom exponent strips blue, and holding flat at the clean value however much aerosol
// arrives when the exponent is near 0 — that last case being the one the pre-§20d model could not
// produce at all, and therefore the one most worth watching in a live A/B.
public sealed class SkyRedBlueRatioProbe : IProbe
{
    public string Name => "sky_red_blue_ratio";

    public float Read(Map map)
    {
        SkyColorTemperature.Rgb rgb = SkyColorTemperature.SkyColorForElevation(
            SolarPosition.ElevationForMap(map),
            SiteAltitude.PressureFractionForMap(map),
            SiteAltitude.AerosolFractionForMap(map),
            SiteAltitude.AngstromExponentForMap(map),
            Vacuum.InVacuumForMap(map));

        // Blue is genuinely 0 at the bottom of the Helland fit (it pins there below 1900 K), and a
        // scenario reading "infinity" is less useful than one reading a large finite number it can
        // write a threshold against. -1 would be worse: it sorts below every real value and would
        // silently satisfy a "less than" assertion that was meant to catch a WEAKER tint.
        return rgb.B <= 0f ? float.MaxValue : rgb.R / rgb.B;
    }
}
