using System;

namespace CelestialLighting;

// Pure math/data only — no UnityEngine or Verse types anywhere in this file, exactly like
// Formulas.cs. It is compiled into both the shipped mod (net481, runs inside RimWorld) and the
// test project (net8.0, runs standalone under `dotnet test`) via a linked <Compile Include>, so the
// exact code that ships is the exact code under test. The Verse-facing pieces — the ModSettings
// class, the settings window, and the SkyManager patch — are thin adapters that read primitives off
// this file and write primitives back.
//
// What lives here: the opinionated presets — named bundles of the correlated aesthetic knobs (§1
// shadows, §7/§7b brightness floors, §9 desaturation, §13 weather dimming) so a player can pick one
// and never open a slider.

// The three settings states. Custom is not an "opinionated preset" — it is simply the state the
// settings fall into the moment a player nudges any individual slider away from a named bundle, so
// ResolvePreset never needs to answer for it (the UI stops applying a bundle once it is Custom).
// This lives in the pure file (not the Verse ModSettings class) so the preset *values* and the
// enum that selects them are unit-testable together with no RimWorld assembly present.
public enum CelestialPreset
{
    // Order matters only for ExposeData's integer round-trip; keep Custom first (== 0) so a
    // freshly-defaulted/never-saved settings blob reads as Custom rather than silently claiming to
    // be an opinionated preset the user never chose.
    Custom = 0,
    Realistic = 1,
    Cinematic = 2,
}

// The correlated aesthetic knobs a preset sets in one go. Deliberately a plain readonly struct of
// primitives: the ModSettings class copies these fields into its own persisted fields when a preset
// is applied, so a preset is "a bundle of the same values", never a separate runtime code path
// (DESIGN.md). None of these are consumed by live patches yet — §1/§7/§9 are being built in
// parallel — so today they are persisted placeholders; see PresetKnobs' field comments for which
// future subsystem each one feeds.
public readonly struct PresetKnobs
{
    // §1: multiplier on the solar-position shadow simulator's max shadow length. 1.0 keeps the
    // physically-derived length; >1 exaggerates the dramatic near-horizon stretch for a cinematic
    // look. TODO(integration): consumed once Patch_ShadowDirection reads settings.
    public readonly float ShadowLengthScale;

    // §1: overall multiplier on shadow opacity/strength, [0,1]. TODO(integration): consumed once
    // Patch_ShadowStrength reads settings.
    public readonly float ShadowStrength;

    // §9: night desaturation strength, [0,1]. Grounded in the Purkinje shift — human scotopic
    // (rod-driven) night vision is nearly colourless — so "Realistic" desaturates hard and
    // "Cinematic" keeps more colour for looks. TODO(integration): consumed once §9's desaturation
    // patch reads settings.
    public readonly float Desaturation;

    // §13: peak weather dimming, [0, 0.5]. How far a full cloud deck at maximum precipitation
    // darkens the rendered sky, and (scaled against DefaultMaxDimming) how far it softens cast
    // shadows. Belongs in the preset bundle because it is strongly correlated with the other
    // aesthetic knobs: a "Realistic" sky that goes genuinely dark under a blizzard wants the same
    // taste that picks black nights and hard desaturation. Colour-channel only — see
    // WeatherDimmingMath — so no preset can affect plant growth or solar output.
    public readonly float WeatherDimming;

    // §7: the pitch-black overlay's hard lower bound on outdoor night brightness, [0,1] — the floor
    // the overlay refuses to go below no matter what the radiance model produces.
    // In the bundle because "how black is an unlit night" is the single most visible difference
    // between the two looks — Realistic keeps it at 0 (genuinely black), Cinematic lifts it so a
    // moonless night is still readable without the player hunting for a slider.
    public readonly float MinNightBrightness;

    // §7b: the matching floor for roofed cells, [0,1]. Paired with MinNightBrightness because indoor
    // occlusion and night darkness compound — a sealed room under a black night is the darkest thing
    // the mod can produce, so a preset that lifts one and not the other would still leave interiors
    // unreadable. Same taste axis, so same bundle.
    public readonly float MinIndoorBrightness;

    // §7c: brightness at an opening and how many cells the gradient reaches, [0,100] / cells. In the
    // bundle (not a standalone slider like doorSkyLeak) because how strong this should read depends on
    // MinIndoorBrightness above it: Cinematic's 0.50 floor already lifts every roofed cell most of the
    // way up, so a Realistic-tuned PassThroughPercent would barely register against that floor and the
    // opening would stop reading as brighter than the rest of the room. Live-tuned per preset for
    // exactly that reason — see DESIGN.md §7c for the measured numbers.
    public readonly float NativeSkyFalloffPassThroughPercent;
    public readonly int NativeSkyFalloffMaxDepth;

    public PresetKnobs(
        float shadowLengthScale,
        float shadowStrength,
        float desaturation,
        float weatherDimming,
        float minNightBrightness,
        float minIndoorBrightness,
        float nativeSkyFalloffPassThroughPercent,
        int nativeSkyFalloffMaxDepth)
    {
        ShadowLengthScale = shadowLengthScale;
        ShadowStrength = shadowStrength;
        Desaturation = desaturation;
        WeatherDimming = weatherDimming;
        MinNightBrightness = minNightBrightness;
        MinIndoorBrightness = minIndoorBrightness;
        NativeSkyFalloffPassThroughPercent = nativeSkyFalloffPassThroughPercent;
        NativeSkyFalloffMaxDepth = nativeSkyFalloffMaxDepth;
    }
}

public static class Presets
{
    // The brightness floor both "never fully black" knobs take under Cinematic. Well above the ~0.15
    // the accessibility floor calls "legible but still clearly night" — Cinematic is a look, not an
    // accessibility aid, and it is picking legibility on purpose.
    //
    // 0.50 rather than the 0.30 this shipped with first: the two floors compound (a sealed room under
    // a moonless night multiplies both), and at 0.30 the compounded result still read as too dark to
    // play by on the preset whose entire job is to be readable out of the box. Realistic keeps 0 and
    // is one click away, so raising the *default* costs nothing for players who want true black.
    private const float CinematicMinBrightness = 0.50f;

    // "Realistic": physically-faithful shadows at full length/strength, heavy desaturation to match
    // colourless scotopic night vision, and full weather dimming so a blizzard reads as one. Both
    // brightness floors sit at 0 for one reason: this preset's whole point is that an unlit night,
    // indoors or out, is actually dark — atmosphere over legibility, which is what the opt-in
    // accessibility floor is for.
    public static readonly PresetKnobs Realistic =
        new PresetKnobs(shadowLengthScale: 1.0f, shadowStrength: 1.0f, desaturation: 0.85f,
            weatherDimming: WeatherDimmingMath.DefaultMaxDimming,
            minNightBrightness: 0.0f, minIndoorBrightness: 0.0f,
            nativeSkyFalloffPassThroughPercent: 35f, nativeSkyFalloffMaxDepth: 8);

    // "Cinematic/Pretty": longer, softer shadows, only mild desaturation so night keeps some colour,
    // and gentler weather dimming so storms stay photogenic rather than murky. The shipped default
    // (see CelestialLightingSettings) — hence the brightness floors: a new player's first night
    // should look good and stay readable, and anyone who wants true black can pick Realistic in one
    // click.
    public static readonly PresetKnobs Cinematic =
        new PresetKnobs(shadowLengthScale: 1.4f, shadowStrength: 0.8f, desaturation: 0.4f,
            weatherDimming: 0.20f,
            minNightBrightness: CinematicMinBrightness, minIndoorBrightness: CinematicMinBrightness,
            nativeSkyFalloffPassThroughPercent: 80f, nativeSkyFalloffMaxDepth: 10);

    // Returns the bundle for a named preset. Custom is intentionally rejected: there is no "custom
    // bundle" to hand back — Custom means the individual fields are whatever the player last set, so
    // callers must not overwrite them. Guarding here (rather than silently returning Realistic)
    // turns a caller bug — applying a preset for the Custom state — into an obvious exception in
    // tests instead of a silent settings stomp in-game.
    public static PresetKnobs Resolve(CelestialPreset preset)
    {
        if (preset == CelestialPreset.Realistic)
            return Realistic;

        if (preset == CelestialPreset.Cinematic)
            return Cinematic;

        throw new ArgumentOutOfRangeException(
            nameof(preset), preset, "Custom has no preset bundle; only named presets resolve to knob values.");
    }

    // Named predicate so the settings UI can ask "is this a bundle I can apply?" without duplicating
    // the enum check — keeps the Custom special-case in exactly one place.
    public static bool IsOpinionated(CelestialPreset preset) =>
        preset == CelestialPreset.Realistic || preset == CelestialPreset.Cinematic;
}
