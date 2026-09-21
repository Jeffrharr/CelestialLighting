namespace CelestialLighting;

// The pure core for "an As above, So below II band below the surface should light like an
// underground map, while the surface band above it keeps the ordinary sky" (§17c).
//
// WHY THIS COULD NOT REUSE §17b's ENCLOSED AMBIENT. On a real cavern map, Patch_EnclosedAmbient
// forces SkyTarget.glow to 1.0 and everything downstream then works unchanged: §7b's cover caps at
// the minimum-indoor-brightness floor, and the cave renders at a constant `1.0 x floor` at every
// hour. That mechanism is MAP-WIDE, and an AASB2 band is not a map — it is a stripe of the same one,
// sharing a single SkyTarget with the open surface overhead. Forcing glow there would light the
// surface band at midnight, and SkyTarget.glow is gameplay light (it is what Dub's Skylights reads),
// so it would grow crops at night as well as look wrong.
//
// WHY NOT DO IT WITH COVER ALONE, which is per-cell and therefore the obvious lever. Every lighting
// overlay vertex renders as `skyColour x (1 - cover)`. Measured on the banded fixture, the map-wide
// sky luminance is 1.000 at noon and 0.309 at midnight, so holding a cave at its noon level of
// 0.498 would need a midnight floor of 0.498 / 0.309 = 1.61. Cover saturates at letting ALL of the
// sky through, i.e. a floor of 1.0, so the night side is unreachable by construction: there is not
// enough sky left at midnight to hold the cave up, whatever we do to the alpha channel.
//
// SO THE LIGHT HAS TO COME FROM THE OTHER CHANNEL. The overlay's RGB carries artificial light and
// its ALPHA carries sky cover (see RenderedLightCellProbe). RGB is not gameplay light — vanilla's
// GlowGrid is untouched by anything written here, which is §27's standing promise and is what makes
// this safe to do per cell. So an underground band gets:
//
//   - FULL sky cover, because an underground band genuinely has no sky, and
//   - a constant ambient added into RGB, which is the cave light a cavern map gets from its forced
//     glow, arriving through the one channel that can still deliver it after dark. It is added
//     beneath whatever §27 has already drawn rather than replacing it — see AddAmbient for the
//     measurement that settled that.
//
// The level is the player's minimum-indoor-brightness setting, deliberately and not a new constant:
// that slider is already the single thing deciding how bright a cavern is (§17b's header makes the
// same promise), so Cinematic's 0.50 gives a legible cave underground and Realistic's 0.0 keeps it
// black, exactly as they do on a Biomes! Caverns map. A second knob here would let a player
// re-create the broken shape by accident.
public static class UndergroundBandMath
{
    // No sky reaches an underground band, so its cover is total. This is the one place in the mod
    // that writes a hard 255 rather than a computed occlusion: on a surface map "fully covered" is a
    // claim about a roof, which a thin roof or a doorway can falsify, whereas here it is a claim
    // about two hundred cells of rock and a different band of the map entirely.
    public const byte FullCoverAlpha = 255;

    // The ambient RGB level for a cell on an underground band, from the minimum-indoor-brightness
    // setting. Rounded rather than truncated so 0.5 lands on 128 and not 127; a half-step is
    // invisible on its own but the value is compared against in tests and quoted in scenario pins.
    public static byte AmbientLevel(float minIndoorBrightness)
    {
        // Written as "not greater than zero" rather than "less than zero" so NaN lands here too.
        // Casting NaN to int is implementation-defined in C#, so letting it reach the cast below
        // would make a dark cave depend on the runtime rather than on the setting — and a test that
        // pinned whatever this machine happens to do would be pinning undefined behaviour.
        if (!(minIndoorBrightness > 0f))
            return 0;

        if (minIndoorBrightness >= 1f)
            return 255;

        return (byte)(int)(minIndoorBrightness * 255f + 0.5f);
    }

    // Add the cave's ambient underneath whatever light is already on this vertex, saturating at 255.
    //
    // A SUM, NOT A MAX, and the first cut of this got it backwards. The argument for a max was that it
    // would leave lit cells alone and so could not wash out §27's falloff. The live run said otherwise:
    // with the Cinematic slider the ambient is 128, the torch's own cell measured 87 (0.342), and three
    // cells out measured 51 (0.200) — every one of them BELOW the ambient. A max therefore flattened the
    // entire lamp to one value and the measured falloff 0.342 -> 0.200 -> 0.088 -> 0.000 became
    // 0.502 -> 0.502 -> 0.490 -> 0.502. The lamp was still there and completely invisible.
    //
    // A sum is a constant offset, so every difference between neighbouring cells survives it exactly —
    // which is what a falloff is. It is also the closer analogue of what a cavern map does: there the
    // ambient arrives through the sky channel and the lamp through RGB, and the shader composes the two
    // rather than picking one, so a torch reads as brighter than the cave around it. Adding once per
    // vertex per bake cannot compound across overlapping lamps, because the pass visits each vertex
    // once and knows nothing about emitters.
    public static byte AddAmbient(byte existing, byte ambient)
    {
        int sum = existing + ambient;
        return (byte)(sum > 255 ? 255 : sum);
    }
}
