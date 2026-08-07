using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §22's UI contribution: appends "- N% cloudy" to the weather label whenever the label itself reads
// Clear, e.g. "Clear - 50% cloudy". Confirmed safe to touch: WeatherManager.DoWeatherGUI is a single
// cosmetic Widgets.Label call inside a fixed-width Rect (GlobalControls is its only caller, passing a
// 230px-wide rect) with no other reader anywhere in vanilla — nothing parses this string back out, so
// appending to it cannot move a game-mechanical value. See parent CLAUDE.md's "careful not to affect
// actual game mechanics" scope and DESIGN.md §22 for the decision to append to the label directly
// rather than push the number into the tooltip only.
//
// A FULL PREFIX REPLACEMENT, NOT A TRANSPILER. DoWeatherGUI builds its string and draws it in one
// method with no seam to hook a suffix onto — the label text is a local expression
// (`curWeatherPerceived.LabelCap`) consumed immediately by Widgets.Label, not a property or field this
// mod could intercept on its own. A transpiler could splice the suffix in without duplicating the
// method, but IL-shape patches are the more fragile of the two options across a RimWorld update (see
// parent CLAUDE.md's silent-override pitfall on why signature/shape drift is the recurring failure
// mode here); Patch_SuppressRandomEclipse already establishes the alternative — a Prefix that runs the
// whole (12-line, stable) body itself and returns false — as an accepted pattern in this codebase, so
// this follows it rather than introducing a second technique for one small method.
//
// WHY THE SUFFIX IS NOT LOCALIZED. This mod ships with no Languages/ folder and no other
// mod-authored string anywhere in Source — every existing `[MustTranslate]` reference here is the mod
// READING a vanilla field (see LimbRefractionMath.cs), never writing one of its own. This is the first
// player-facing string the mod itself authors, and hardcoding it in English matches the codebase's
// current (zero) localization convention rather than introducing a translation system for one string.
// Revisit if a second authored string ever shows up and the two together justify the scaffolding.
//
// GATED ON CurWeatherPerceived, NOT curWeather. DoWeatherGUI's own label is driven by
// CurWeatherPerceived — the weather WeatherManager judges to be visually dominant right now, which can
// still read as the outgoing or incoming weather partway through a transition (see that property's own
// perceivePriority-threshold logic) — so the suffix is gated on the same value the label text itself
// is built from, not on the underlying state-machine field Patch_CloudCoverSky gates on. The two
// patches answering slightly different questions ("what does the label say" vs "what does the sky
// look like") is a deliberate consequence of matching each one to what it renders, not an
// inconsistency between them.
[HarmonyPatch(typeof(WeatherManager), nameof(WeatherManager.DoWeatherGUI))]
public static class Patch_CloudCoverLabel
{
    static bool Prefix(WeatherManager __instance, Rect rect)
    {
        WeatherDef curWeatherPerceived = __instance.CurWeatherPerceived;
        string label = curWeatherPerceived.LabelCap;

        // Pocket maps and pre-game previews can carry a WeatherManager with no map yet — the same
        // null a live map never has, guarded the same way CloudCoverClock.FractionForMap itself
        // assumes it will not see.
        //
        // GATED ON THE FLAGS HERE TOO, NOT JUST INSIDE FractionForMap. The suffix is now shown at
        // every reading including 0%, so the fraction being exactly 0 no longer implies "feature is
        // off" the way it used to — a calm hour with the feature on reads identically to the feature
        // being off unless this patch checks the flag itself. Patch_CloudCoverSky can still lean on
        // FractionForMap's own zero-return (its "no tint" and "feature off" cases render pixel-
        // identical either way), but this patch cannot: "Clear" and "Clear - 0% cloudy" are visibly
        // different strings, so only an explicit check keeps "off" reproducing the pre-feature label.
        // CloudCoverLabel is the separate UI-only sub-toggle (a player can want the sky tint without
        // the text); CloudCover is still checked too, matching AuroraCurtain's own relationship to
        // Aurora — the sub-toggle never draws with its master off.
        if (__instance.map != null && curWeatherPerceived == WeatherDefOf.Clear
            && CelestialLightingFeatures.CloudCover && CelestialLightingFeatures.CloudCoverLabel)
        {
            float cloudCover = CloudCoverClock.FractionForMap(__instance.map);

            // Always appended, including 0% — a player watching this label to confirm the feature is
            // alive should see a stable readout every time it's Clear, not have it silently vanish at
            // exactly the moments (a calm hour) that are most likely to prompt the question.
            int percent = Mathf.RoundToInt(Mathf.Clamp01(cloudCover) * 100f);
            label = $"{label} - {percent}% cloudy";
        }

        // Everything below is DoWeatherGUI's own body, unchanged, operating on `label` instead of
        // `curWeatherPerceived.LabelCap` directly.
        Text.Anchor = TextAnchor.MiddleRight;
        Rect rect2 = new Rect(rect);
        rect2.width -= 15f;
        Text.Font = GameFont.Small;
        Widgets.Label(rect2, label);
        if (!curWeatherPerceived.description.NullOrEmpty())
            TooltipHandler.TipRegion(rect, curWeatherPerceived.description);
        Text.Anchor = TextAnchor.UpperLeft;

        return false;
    }
}
