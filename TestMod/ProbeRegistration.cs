using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

[StaticConstructorOnStartup]
public static class ProbeRegistration
{
    static ProbeRegistration()
    {
        ProbeRegistry.Register(new ShadowLeanProbe());
    }
}
