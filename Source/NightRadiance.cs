using Verse;

namespace CelestialLighting;

// Thin adapter for §7 / §18b: pulls the primitives NightRadianceMath needs off live Map state and
// answers one question — how dark can the sky over this map get right now.
//
// SHARED ON PURPOSE, and this is the point of the file existing at all. Three subsystems need the
// same number and would otherwise each derive their own:
//
//   §7   Patch_NightRadiance blends the night sky toward it (this is the value the floor IS)
//   #31  vacuum shadow contrast — with no skylight to fill the umbra, a shadow bottoms out here
//   #33  vacuum eclipse response — totality falls to here rather than to a tuned fraction
//
// Same value reached from three directions. That is the same discipline SolarPosition enforces for
// sun elevation and WeatherDimming for cloud cover, and it is deliberately a SHARED READ rather than
// one patch stashing a value for another to pick up later: a read has no ordering to get wrong, no
// staleness across frames, and no silent dependency on which Harmony patch ran first.
//
// WHAT IT DOES NOT DO. It reports the floor, not the current sky — no sun-elevation ramp is applied
// (NightRadianceMath.ApplyNightFloor owns that) and no weather multiply (§13 owns that, on the colour
// channel). Callers that want "the darkest this can get" want exactly this; callers that want "how
// bright is it now" want SkyManager.CurSkyGlow.
//
// It is also deliberately NOT gated on CelestialLightingFeatures.NightRadiance. That flag turns §7's
// own effect off, restoring vanilla's flat night glow — a value this mod does not own and cannot
// report. Making the shared floor jump whenever an unrelated toggle flipped would be worse than
// useless to #31 and #33, so each consumer gates its own effect and this reports the model.
public static class NightRadiance
{
    // The night floor for this map, in RimWorld's 0..1 glow units: the sum of every dim night-side
    // source the model knows about, with the §18b vacuum substitutions applied when the map has no
    // atmosphere. Includes moonlight, so it tracks the lunar cycle — a full-moon night genuinely has
    // a higher floor than a new-moon one, which is what makes an eclipse or an umbra under a full
    // moon bottom out higher than the same event on a new moon.
    public static float FloorGlowFor(Map map)
    {
        NightRadianceSettings settings = NightRadianceSettings.Current;

        // The atmospheric floors are the "true pitch-black" switch: with them off, starlight and
        // airglow drop to zero and only the reflected sources remain. Read here rather than in the
        // patch so every consumer of the floor sees the same answer to that toggle.
        float starlight = settings.AtmosphericGlowEnabled ? settings.StarlightGlow : 0f;
        float airglow = settings.AtmosphericGlowEnabled ? settings.AirglowGlow : 0f;

        MoonState moon = MoonSeam.Provider(map);
        float moonlight = NightRadianceMath.MoonlightGlow(
            moon.IlluminatedFraction, moon.ElevationDegrees, settings.MaxMoonlightGlow);

        float floor = NightRadianceMath.NightFloorGlow(
            starlight, airglow, moonlight, settings.MaxMoonlightGlow, Vacuum.InVacuumForMap(map));

        // §21: the surface-cloud light cavity. Snow on the ground and a cloud base overhead trap
        // light between them, and the same geometric series that makes a snowy overcast DAY dazzling
        // amplifies starlight and moonlight at night — a full moon on fresh snow under cloud is
        // famously bright enough to read by. Applied here rather than in Patch_NightRadiance for the
        // reason this whole file exists: it belongs to "how dark can the sky over this map get", and
        // all three consumers of that answer should see the same one.
        //
        // Costs the two vacuum consumers nothing to have it here. SurfaceBuildup.CavityGainFor
        // returns exactly 1 on a vacuum map (no atmosphere, no cloud base, no cavity), so #31's umbra
        // floor and #33's eclipse minimum read the same value they always did — and they read it
        // through one function rather than through a branch each.
        return AlbedoCavityMath.AmplifiedGlow(floor, SurfaceBuildup.CavityGainFor(map));
    }

    // The glow the mod's VISUAL-ONLY effects should read this frame — CurSkyGlow, except that an active
    // eclipse may not drive it below the night floor above. See
    // NightRadianceMath.EclipseFlooredGlow for the physics and the measured before/after; this is the
    // Verse adapter that answers the two live questions the pure rule takes as bools.
    //
    // A SHARED READ for the same reason FloorGlowFor is one. Two visual subsystems need this — §7a's
    // overlay darkening and §9's Purkinje wash — and they run as two separate postfixes on
    // SkyManagerUpdate with no ordering between them. Each reading the same function cannot disagree;
    // each deriving its own answer would drift the moment either was retuned, and would do it
    // invisibly, since the symptom is a hue rather than a number anybody prints.
    //
    // NEVER WRITTEN BACK TO SkyTarget.glow. That field is gameplay light and stays exactly vanilla
    // during an eclipse — solar panels, plant growth, GlowGrid and Dub's Skylights all go on seeing the
    // blackout the event is supposed to be. This value only ever decides how the frame is drawn.
    //
    // ANOMALY'S UnnaturalDarkness WINS OUTRIGHT over this floor, which is why the gate is composed here
    // rather than inside the pure rule. That event is a gameplay-critical horror set-piece whose whole
    // point is that you cannot see ("stay in the light"; GameCondition_UnnaturalDarkness spawns
    // DarknessExposure hediffs off the same darkness), and lifting it back to "as bright as an ordinary
    // night" is not a call this mod should make — the same reasoning, and the same direction, as
    // NightRadianceMath.EffectiveMinNightBrightness's existing carve-out. When it is live the floor
    // stands down completely and this returns currentGlow untouched, so that path is bit-identical to
    // what it was before this rule existed.
    public static float VisualGlowFor(Map map, float currentGlow)
    {
        bool eclipseFloorApplies = MapSky.EclipseActive(map) && !MapSky.UnnaturalDarknessActive(map);

        return NightRadianceMath.EclipseFlooredGlow(eclipseFloorApplies, currentGlow, FloorGlowFor(map));
    }
}
