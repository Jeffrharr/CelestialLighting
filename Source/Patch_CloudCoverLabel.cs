using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
// A TRANSPILER, NOT A PREFIX REPLACEMENT — AND THE REASON IS INTEROP, NOT TASTE. This patch used to
// be a Prefix that ran a copy of the whole (12-line, stable) vanilla body and returned false, matching
// Patch_SuppressRandomEclipse's technique, on the argument that IL-shape patches are the more fragile
// option across a RimWorld update. That argument was sound in isolation and wrong in a mod ecosystem:
// a Prefix returning false skips the *patched* original, so it silently deletes every other mod's
// contribution to the same method — including transpilers, whose whole point is to compose. Uncompromising
// Fires (Fuu.UncompromisingFires, Workshop 2623963630) transpiles exactly this method to append its map
// dryness readout to the same label and its dryness detail to the same tooltip; our Prefix erased both
// outright, in all weathers, and even with CloudCoverLabel switched off, because the replacement body
// ran unconditionally. Nothing warns about this — the other mod's patch applies cleanly and simply
// never executes.
//
// So the trade is: accept the IL-shape fragility (guarded by the Cecil pin in ApiCompatibilityTests,
// which fails loudly if the seam moves) in exchange for being a well-behaved co-patcher. The seam is
// the single `callvirt Def::get_LabelCap` that DoWeatherGUI's Widgets.Label call is built from; we
// insert a call that takes the TaggedString the property just produced and returns a TaggedString, so
// the insertion is stack-neutral and type-preserving. That composes with Uncompromising Fires in either
// application order — its inserts sit on the same seam and are also TaggedString-in/TaggedString-out,
// so whichever mod's transpiler runs second simply wraps the other's result. About.xml's loadAfter
// entry pins the nicer of the two readings ("Clear - 50% cloudy, Moderate dryness").
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
    // The seam: DoWeatherGUI's only `curWeatherPerceived.LabelCap`, the value it hands to
    // Widgets.Label(Rect, TaggedString). We append our call immediately after it so the suffix is
    // applied to the label before anything else consumes it.
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo labelCap = AccessTools.PropertyGetter(typeof(Def), nameof(Def.LabelCap));
        MethodInfo appender = AccessTools.Method(typeof(Patch_CloudCoverLabel), nameof(WithCloudCover));

        bool inserted = false;
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction;

            // Only the first match is ours; vanilla has exactly one, but another mod's transpiler may
            // have already added more, and appending our percentage twice would be worse than missing
            // it once. Ldarg_0 is DoWeatherGUI's `this` — the WeatherManager whose map we read.
            bool isSeam = !inserted && instruction.Calls(labelCap);
            if (isSeam)
            {
                inserted = true;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, appender);
            }
        }

        // A silent no-op would ship a dead feature that looks alive in the settings panel, so say so.
        // Not thrown: the rest of the mod is unaffected and a missing suffix is not worth a hard
        // failure at patch time. ApiCompatibilityTests is the check meant to catch this before a
        // player ever sees it.
        if (!inserted)
            Log.Warning("[CelestialLighting] Could not find Def.LabelCap in WeatherManager.DoWeatherGUI; "
                + "the cloud-cover weather label (§22) is disabled this session.");
    }

    // Stack-neutral, type-preserving: TaggedString in, TaggedString out. Anything else here would
    // break whichever other mod's inserts happen to sit next to ours on the same seam.
    public static TaggedString WithCloudCover(TaggedString label, WeatherManager manager)
    {
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
        bool suffixApplies = manager?.map != null && manager.CurWeatherPerceived == WeatherDefOf.Clear
            && CelestialLightingFeatures.CloudCover && CelestialLightingFeatures.CloudCoverLabel;
        if (!suffixApplies)
            return label;

        // Always appended, including 0% — a player watching this label to confirm the feature is
        // alive should see a stable readout every time it's Clear, not have it silently vanish at
        // exactly the moments (a calm hour) that are most likely to prompt the question.
        int percent = Mathf.RoundToInt(Mathf.Clamp01(CloudCoverClock.FractionForMap(manager.map)) * 100f);
        return label + $" - {percent}% cloudy";
    }
}
