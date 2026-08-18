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
// contribution to the same method — including transpilers, whose whole point is to compose.
// Uncompromising Fires (Fuu.UncompromisingFires, Workshop 2623963630) transpiles exactly this method to
// append its map dryness readout to the same label and its dryness detail to the same tooltip; our
// Prefix erased both outright, in all weathers, and even with CloudCoverLabel switched off, because the
// replacement body ran unconditionally. Nothing warns about this — the other mod's patch applies
// cleanly and simply never executes.
//
// So the trade is: accept the IL-shape fragility (guarded by the Cecil pin in ApiCompatibilityTests,
// which fails loudly if the seam moves) in exchange for being a well-behaved co-patcher.
//
// THE SEAM IS THE Widgets.Label CALL, NOT Def.LabelCap — AND THAT CHOICE IS LOAD-BEARING. The obvious
// anchor is the `callvirt Def::get_LabelCap` the label is built from, and in vanilla IL the two sites
// are adjacent, so either anchor produces the same code. They stop being equivalent the moment another
// mod inserts between them, which is exactly what Uncompromising Fires does. Anchoring on LabelCap puts
// our call BEFORE their concatenation when they patch second and AFTER it when they patch first, so
// what we see depends on load order. Anchoring on the Widgets.Label call that consumes the string puts
// us after everything anyone else appended, in either order: we are always the last hand on the label
// before it is drawn. That is what makes the fit check below able to measure the real, final string
// rather than our own half of it, and it is why About.xml's loadAfter entry is now only about
// determinism rather than about outcome.
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
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo drawLabel = AccessTools.Method(typeof(Widgets), nameof(Widgets.Label),
            new[] { typeof(Rect), typeof(TaggedString) });
        MethodInfo appender = AccessTools.Method(typeof(Patch_CloudCoverLabel), nameof(WithCloudCover));

        bool inserted = false;
        foreach (CodeInstruction instruction in instructions)
        {
            // Only the first match is ours. Vanilla draws the label once; another mod's transpiler
            // could have added a second Widgets.Label, and appending our percentage twice would be
            // worse than appending it to the wrong one of two.
            bool isSeam = !inserted && instruction.Calls(drawLabel);
            if (isSeam)
            {
                inserted = true;

                // Ldarg_1 is DoWeatherGUI's own `rect` parameter — the width the finished string has
                // to fit into. Ldarg_0 is `this`, the WeatherManager whose map we read. The stack at
                // this point is [rect2][label]; we push two more, our call pops three and pushes one,
                // leaving [rect2][label] for the Widgets.Label call itself. Stack-neutral by
                // construction, which is what lets another mod's inserts sit next to ours.
                CodeInstruction loadRect = new CodeInstruction(OpCodes.Ldarg_1);

                // Any branch that targeted the draw call must now target the first instruction we
                // inserted in front of it, or it would jump over our call and past a half-built stack.
                // Vanilla has no such branch here; this costs two lines and survives one appearing.
                loadRect.labels.AddRange(instruction.labels);
                instruction.labels.Clear();

                yield return loadRect;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, appender);
            }

            yield return instruction;
        }

        // A silent no-op would ship a dead feature that looks alive in the settings panel, so say so.
        // Not thrown: the rest of the mod is unaffected and a missing suffix is not worth a hard
        // failure at patch time. ApiCompatibilityTests is the check meant to catch this before a
        // player ever sees it.
        if (!inserted)
            Log.Warning("[CelestialLighting] Could not find the Widgets.Label call in "
                + "WeatherManager.DoWeatherGUI; the cloud-cover weather label (§22) is disabled this session.");
    }

    // Stack-neutral, type-preserving: TaggedString in, TaggedString out. Anything else here would
    // break whichever other mod's inserts happen to sit next to ours on the same seam.
    public static TaggedString WithCloudCover(TaggedString label, Rect rect, WeatherManager manager)
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
        int percent = CloudCoverLabelMath.Percent(CloudCoverClock.FractionForMap(manager.map));
        TaggedString withSuffix = label + CloudCoverLabelMath.Suffix(percent);

        // The one case where the suffix IS dropped: it no longer fits on one line. See
        // CloudCoverLabelMath's header for why ours is the part that yields rather than another mod's.
        // Measured through Text.CalcSize on the resolved string because that is exactly what
        // Widgets.Label(Rect, TaggedString) is about to draw — it calls Resolve() itself, and CalcSize
        // strips the same tags — and measured HERE rather than off a cached width because the string
        // includes whatever other mods appended, which we do not own and cannot predict. Text.Font is
        // already GameFont.Small at this point: DoWeatherGUI sets it before the draw call we sit in
        // front of, so this measures in the font the label is actually rendered in.
        //
        // Per-frame CalcSize is deliberate and in-budget: vanilla's own GlobalControls.DoCountdownTimer
        // measures a string the same way in the same panel every frame, and Text.CalcSize reuses a
        // shared GUIContent rather than allocating one.
        string resolved = withSuffix.Resolve();
        if (!CloudCoverLabelMath.FitsOneLine(Text.CalcSize(resolved).x, rect.width))
            return label;

        return withSuffix;
    }
}
