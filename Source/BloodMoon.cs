using RimWorld;
using Verse;

namespace CelestialLighting;

// Thin adapter: pulls the primitives BloodMoonMath needs off live Map/Find state and detects the
// third-party blood-moon condition. Shared by Patch_BloodMoon (which applies the crimson tint) and
// BloodMoonProbe (which reads the resulting strength back for a live end-to-end assertion), so the
// two can never derive a different value — the same discipline SolarPosition.cs enforces for the
// sun across its two patches.
//
// Soft dependency only (DESIGN.md §12): the blood moon is Vanilla Races Expanded – Sanguophage's
// VRE_BloodMoonCondition GameCondition. We never take a hard assembly reference to that mod — we
// look its condition def up by name and treat "not present" as "never a blood moon". We *react to*
// the condition; we never trigger it or touch any of its sanguophage/hemogen mechanics.
public static class BloodMoon
{
    // VRE – Sanguophage's condition defName. A def lookup (not a typed reference) is what keeps
    // this a soft dependency: when that mod isn't installed the def simply doesn't exist and the
    // effect is inert.
    public const string ConditionDefName = "VRE_BloodMoonCondition";

    // Maximum crimson blend at full night. Below 1 so the base sky's own hue still shows through a
    // little rather than the night flattening to a single flat red.
    public const float MaxTint = 0.85f;

    // The condition def is resolved once and cached. Def lookups only ever run while a game is
    // loaded (CurSkyTarget/the probe only fire in-game), by which point the def database is fully
    // populated, so a one-shot lookup can't cache a spurious null for an installed mod. RimWorld
    // requires a restart to change the active mod list, so the resolved def is stable for the
    // process lifetime. `_lookupDone` distinguishes "looked up, genuinely absent" from "not yet
    // looked up".
    private static GameConditionDef _cachedDef;
    private static bool _lookupDone;

    private static GameConditionDef ConditionDef()
    {
        if (!_lookupDone)
        {
            _cachedDef = DefDatabase<GameConditionDef>.GetNamedSilentFail(ConditionDefName);
            _lookupDone = true;
        }
        return _cachedDef;
    }

    public static bool IsActive(Map map)
    {
        GameConditionDef def = ConditionDef();
        return def != null && map.GameConditionManager.ConditionIsActive(def);
    }

    // Strength of the crimson recolour to apply on this map right now: 0 when no blood moon is
    // active (and during daylight, via the NightFactor ramp), rising toward MaxTint as the sky
    // darkens. Re-derives sun glow from GenCelestial.CurCelestialSunGlow — the true celestial
    // value — rather than the composed sky glow, matching how Patch_TwilightColor anchors its own
    // timing to real sun position. ("Weather-clamped" was the original wording; weather does not in
    // fact clamp glow — see DESIGN.md §13 — but §7's night floor does rewrite it, which is the real
    // reason to prefer the celestial value here.)
    //
    // TODO(integration): once #6 (moon position) and #7 (night radiance) land, the authoritative
    // moonlit-sky colour will come from the night-radiance subsystem. At that point Patch_BloodMoon
    // should recolour *that* moonlight colour (so a blood moon is a genuinely bright red night)
    // instead of tinting the vanilla night sky in place, and this ramp can additionally gate on the
    // modeled moon being above the horizon. The detection + pure recolour below are the
    // self-contained seam that plugs into it.
    public static float TintStrengthForMap(Map map)
    {
        if (!IsActive(map))
            return 0f;

        float sunGlow = GenCelestial.CurCelestialSunGlow(map);
        return BloodMoonMath.TintStrength(sunGlow, MaxTint);
    }
}
