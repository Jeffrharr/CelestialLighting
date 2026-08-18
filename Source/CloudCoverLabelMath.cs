using System;

namespace CelestialLighting;

// Pure core for §22's weather-panel suffix: the percentage arithmetic, and the one-line fit rule that
// decides whether the suffix is shown at all. No UnityEngine/Verse usings — Patch_CloudCoverLabel does
// the measuring (Text.CalcSize) and the drawing, and hands the numbers here.
//
// WHY A FIT RULE EXISTS AT ALL. GlobalControls gives the weather label a fixed 230px rect and
// Widgets.Label word-wraps, so a label long enough to overflow does not shrink or clip — it wraps to a
// second line inside a 26px-tall rect and collides with the temperature row above it. Our suffix is not
// the only thing appended to that label: Uncompromising Fires appends a map-dryness readout to the same
// string, and any future mod may append more. When the total no longer fits, ours is the part that
// yields, because it is the one we own and the cheapest of the three to lose — the same number is still
// on the sky itself, whereas the dryness readout has no other presentation and the weather name is the
// label's actual subject.
public static class CloudCoverLabelMath
{
    // Vanilla's own inset: DoWeatherGUI draws into `new Rect(rect) { width = rect.width - 15f }`, so the
    // text the player sees has 15px less room than the rect the method is handed. Mirrored here rather
    // than measured off the real rect2 because rect2 is a local the transpiler has no clean handle on;
    // if Ludeon ever changes the inset, the consequence is a fit threshold wrong by a few pixels, not a
    // broken label — which is why this is a constant and not a pinned IL read.
    public const float VanillaLabelInset = 15f;

    // Non-finite widths (a font that failed to load, a zero-size screen during a resolution change)
    // must not be read as "does not fit" — that would drop the suffix for the whole session on a
    // transient. Anything we cannot measure is treated as fitting, so the failure mode is the old
    // behaviour (possible wrap) rather than a silently absent feature.
    public static bool FitsOneLine(float measuredWidth, float rectWidth)
    {
        bool measurable = !float.IsNaN(measuredWidth) && !float.IsInfinity(measuredWidth)
            && !float.IsNaN(rectWidth) && !float.IsInfinity(rectWidth);
        if (!measurable)
            return true;

        return measuredWidth <= rectWidth - VanillaLabelInset;
    }

    // Clamped because a cloud fraction is a fraction by contract and a readout of "-3% cloudy" would be
    // a worse bug report than a wrong-looking 0%. Math.Round rather than Mathf.RoundToInt only because
    // this file is Unity-free; both round half to even, so the two agree on every input.
    public static int Percent(float cloudFraction)
    {
        if (float.IsNaN(cloudFraction))
            return 0;

        float clamped = Math.Max(0f, Math.Min(1f, cloudFraction));
        return (int)Math.Round(clamped * 100f, MidpointRounding.ToEven);
    }

    // The one authored player-facing string in the mod — see Patch_CloudCoverLabel's header for why it
    // is not localized. Leading separator included so callers concatenate rather than format.
    public static string Suffix(int percent) => $" - {percent}% cloudy";
}
