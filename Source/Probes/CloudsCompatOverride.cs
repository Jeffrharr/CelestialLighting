namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see <Compile Remove> in CelestialLighting.csproj)
// and compiled into TestMod/CelestialLighting.Probes.csproj instead, the same boundary
// CloudCoverFractionOverride and PlanetsmithTiltOverride draw and for the same reason: this must
// never reach a player's game.
//
// WHY THIS EXISTS. The Clouds interop (Source/CloudsCompat.cs) is the one thing in this mod whose
// A/B cannot be arranged by a feature flag, because what it switches is not an effect of ours — it
// is our reading of the load order. The comparison it has to justify itself with is "Pardeike's
// clouds alone" against "Pardeike's clouds and ours at once", and both of those frames need Clouds
// LOADED, so they have to come out of one boot with the interop the only thing moving between them.
// Turning our own cloud flags off would measure a different question entirely (their clouds against
// a clear sky), and running the two halves as two processes would compare frames from two different
// cloud fields — theirs is a live ParticleSystem, not a pure function of the tick.
//
// So the flag suppresses the interop rather than the feature: OFF here means "pretend Clouds is not
// installed", which is precisely the pre-interop build, and is what makes the harness A/B a real
// baseline rather than a picture of one mod being absent. Same contract as every feature flag in
// CelestialLightingFeatures, reached from the other side.
//
// NO HARMONY PATCH, unlike its two siblings in this folder: CloudsCompat already carries the seam
// (OverrideInstalled) for exactly this caller, so there is nothing to intercept.
public static class CloudsCompatOverride
{
    public const string FeatureKey = "clouds_interop";

    // Registered with defaultEnabled TRUE, unlike CloudCoverFractionOverride: the resting state here
    // is the shipped behaviour, and a scenario that never mentions this key must measure what a
    // player with Clouds installed actually gets.
    //
    // Enabled restores the real load-order read (null, not true) rather than forcing "installed" —
    // on a run WITHOUT Clouds this key must be inert in both positions, and pinning it to true would
    // instead make every such run stand our clouds down for a mod that is not there.
    public static void Set(bool enabled) => CloudsCompat.OverrideInstalled = enabled ? (bool?)null : false;
}
