using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// How BRIGHT the composed sky overlay is, as the mean of MatBases.LightOverlay.color's r/g/b — the
// same material SkyOverlayWarmthProbe reads, but the magnitude axis rather than the hue axis. §7a's
// Patch_PitchBlackOverlay Color.Lerp()s this material toward opaque black, so this probe is the one
// downstream of BOTH the §17 blackout gate and the §7a brightness floor: it is what actually answers
// "how dark does UnnaturalDarkness render right now, under this MinNightBrightness preset" rather
// than re-deriving either input from the live clock/settings.
//
// Mean of the three channels rather than a single one: unlike warmth, there is no reason to isolate
// one axis here, and averaging cancels out per-channel tint noise from §9's desaturation / §2's
// twilight warmth so the reading tracks overall darkness rather than colour.
public sealed class SkyOverlayLuminanceProbe : IProbe
{
    public string Name => "sky_overlay_luminance";

    public float Read(Map map)
    {
        var color = MatBases.LightOverlay.color;
        return (color.r + color.g + color.b) / 3f;
    }
}
