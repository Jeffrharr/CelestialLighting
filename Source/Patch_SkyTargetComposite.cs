using HarmonyLib;
using RimWorld;
using Verse;

namespace CelestialLighting;

// THE mod's single Postfix on WeatherWorker.CurSkyTarget.
//
// Fourteen subsystems contribute to the SkyTarget this method returns. Until now each one registered
// its own [HarmonyPatch] on this same method, which meant the order they composed in was decided by
// Harmony rather than by us — and, as the note below records, that order was not the one two of those
// subsystems documented for themselves. This class replaces the fourteen registrations with one, and
// spells the sequence out as fourteen ordinary static calls.
//
// WHAT THIS IS AND IS NOT WORTH. It is NOT a throughput optimisation, and the header should say so
// plainly so nobody re-derives the wrong reason for it later. Harmony does not dispatch per patch:
// MethodCreator.AddPostfixes emits a direct `call` to each postfix into ONE generated wrapper method,
// so fourteen postfixes were already fourteen direct static calls, and routing them through this
// class adds one more rather than removing thirteen. Measured cost of the change is nil either way.
//
// What it buys is legibility, in two specific places that had both gone wrong:
//
//   1. PROFILERS REPORT PER PATCHED METHOD. Dub's Performance Analyzer listed this mod as fourteen
//      separate rows against one vanilla method, none of which was the mod's cost and all of which
//      had to be added up by hand before the number meant anything. Anyone trying to answer "what
//      does CelestialLighting cost me" — the question this repo gets asked most — started by
//      reconstructing a total the tooling had taken apart. Now there is one row, and Circinus can
//      still arm the individual stages when the breakdown is what you actually want (which is the
//      right way round: parents to judge, children to find).
//
//   2. ORDER IS NOW WRITTEN DOWN. Fourteen same-priority postfixes tie-break on Harmony's
//      `Patch.index` — registration order, which under PatchAll is assembly metadata type order,
//      which Roslyn emits in file order, which is alphabetical. So the composition order of this
//      mod's entire sky pipeline was a consequence of what the files happened to be CALLED. Renaming
//      a file would have silently recomposed the sky. Several of the headers below reason carefully
//      about ordering ("by the time this runs, §7 has already..."); none of them could enforce it,
//      because [HarmonyAfter] takes owner IDs and every patch here shares the one "celestiallighting"
//      owner — an intra-assembly order was not expressible at all.
//
// EACH STAGE KEEPS ITS OWN GATE, UNCHANGED. Every subsystem still owns its feature flag and its own
// early-outs, in its own file, exactly as before — some check CelestialLightingFeatures directly
// (§11 Aurora, §12 BloodMoon, §9 LowLightDesaturation, §6a MoonShadows, §7 NightRadiance, §8
// SkyColorTemperature, §17b IndoorSkyOcclusion), others gate inside their shared adapter so the live
// probe and the patch can never disagree (§2 TwilightWarmth.ForMap, §13 WeatherDimming.DimmingFor,
// §19 OzoneTwilight.BandStrengthFor, §19c PurpleLight.WindowStrengthFor, §22
// CloudCoverClock.FractionForMap). Nothing about "a flag off must reproduce the pre-feature
// behaviour exactly" changes here; this class only decides who is asked, and in what order.
//
// The stage methods are `internal static Apply(Map, ref SkyTarget target)` rather than `Postfix`,
// because they are now plain functions that nothing reflects over. The one-file-per-subsystem layout
// is deliberately untouched: the reasoning for each effect stays next to that effect, and this file
// holds only the sequence.
//
// THE POSTFIX PARAMETER BELOW MUST STAY NAMED `__result`. It is not a style choice and it is not
// interchangeable with the `target` the stages take: `__result` is Harmony's magic name for the
// patched method's return value, matched by STRING at patch time. Rename it and Harmony looks for a
// real parameter of that name on `CurSkyTarget(Map map)`, does not find one, and throws
// `Parameter "..." not found` out of PatchAll — which runs inside CelestialLightingMod's static
// constructor, so the failure takes down EVERY patch in the mod plus the AxialTiltCompat and
// MoonSeam wiring after it, not just this one.
//
// This was not hypothetical: it happened while writing this class, and the way it presented is the
// reason it is documented here rather than left to be rediscovered. RimWorld swallows the static
// constructor exception into the log and carries on, so the game ran, the harness scenario reported
// pass=True, and three screenshots came out looking like a plausible sky — vanilla's sky. The only
// signal was the frames measuring a median CIELAB deltaE of 4.23/11.74/3.45 against the baseline
// when a pure refactor owed 0.00. A green scenario does not prove the mod loaded; grep Player.log
// for "Error in static constructor" when a whole-mod A/B comes back surprising.
[HarmonyPatch(typeof(WeatherWorker), nameof(WeatherWorker.CurSkyTarget))]
public static class Patch_SkyTargetComposite
{
    // THE ORDER BELOW IS ALPHABETICAL BY CLASS NAME, WHICH IS WHAT HARMONY WAS DOING — with the
    // documented exceptions called out inline. A straight-line sequence rather than a list of
    // delegates: no allocation, and the order is readable as code.
    //
    // The composite landed first reproducing the alphabetical order EXACTLY, so that merging fourteen
    // patches into one was a provably inert change with nothing riding along (§28's discipline), and
    // the order was then corrected in its own commit with its own measurement. That sequencing is why
    // §29 in DESIGN.md can quote a deltaE for the merge and a separate one for each move.
    //
    //   §9 LowLightDesaturation IS DELIBERATELY OUT OF ALPHABETICAL POSITION, moved to sit directly
    //   after §7 NightRadiance. It reads `__result.glow` to key the rod-vision ramp, and §7 is what
    //   puts the starlight + airglow + MOONLIGHT floor into that field. Alphabetically §9 sorts before
    //   §7, so for the whole life of the mod it read vanilla's raw below-horizon glow — near zero
    //   under every moon phase alike — and the moon-phase dependence its own header describes did not
    //   exist. Moving it also makes the patch agree with PurkinjeProbe and with §9's own wash
    //   (Patch_NightDesaturationStrength), both of which read the FINAL composed glow off SkyManager
    //   and so had always been reporting the post-§7 value the patch was not using.
    //
    //   §19c PurpleLight IS ALSO DELIBERATELY OUT OF ALPHABETICAL POSITION, moved to sit after §8
    //   SkyColorTemperature. It is a CORRECTION APPLIED TO WHAT §8 AND §19 LEFT BEHIND rather than an
    //   independent tint — the whole subsystem exists because those two model the -6..-4 handoff as
    //   substituting for one another when both sources are physically present at once — so it has to
    //   see their output. Alphabetically it got §19 (which sorts earlier) but not §8, so §8 ran
    //   afterwards and blended part of the correction back down. That was a bounded degradation rather
    //   than a defect, exactly as this file's own header argued: the window envelope is zero at both
    //   boundaries and the nudge is a lerp toward a rescaled hue, so running early made the effect
    //   WEAKER, never wrong or discontinuous. Moved anyway, because "weaker than designed everywhere
    //   it applies" is not a property worth keeping once the order is expressible.
    static void Postfix(Map map, ref SkyTarget __result)
    {
        Patch_AuroraTint.Apply(map, ref __result);              // §11  night sky tint under an auroral event
        Patch_BloodMoon.Apply(map, ref __result);               // §12  crimson night under a blood-moon condition
        Patch_CloudCoverSky.Apply(map, ref __result);           // §22  partial cloud cover, Clear weather only
        Patch_EnclosedAmbient.Apply(map, ref __result);         // §17b constant ambient glow in a cavern
        Patch_LimbRefraction.Apply(map, ref __result);          // §18d orbital sunset; owns .glow in vacuum
        Patch_MoonShadowColor.Apply(map, ref __result);         // §6a  colors.shadow below the horizon
        Patch_NightRadiance.Apply(map, ref __result);           // §7   starlight/airglow/moonlight night floor on .glow
        Patch_LowLightDesaturation.Apply(map, ref __result);    // §9   Purkinje cool-grey drift — MUST follow §7, see note
        Patch_PolarNightBlue.Apply(map, ref __result);          // §19  ozone Chappuis band
        Patch_SkyColorTemperature.Apply(map, ref __result);     // §8   blackbody curve, site altitude, aerosol
        Patch_PurpleLight.Apply(map, ref __result);             // §19c -6..-4 window correction — MUST follow §8 and §19, see note
        Patch_TwilightColor.Apply(map, ref __result);           // §2   warm nudge through civil twilight
        Patch_WeatherDimming.Apply(map, ref __result);          // §13  storm darkening, colour-only
        Patch_WeatherShadowColor.Apply(map, ref __result);      // §13a/§18c colors.shadow above the horizon
    }
}
