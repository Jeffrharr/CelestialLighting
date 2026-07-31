namespace CelestialLighting;

// Runtime on/off switches for CelestialLighting's individual visual effects.
//
// In the shipped mod every switch is simply "on" and nothing flips it at runtime yet — the default
// value IS the shipped behaviour, so the effect works with neither of the two intended consumers
// present:
//   1. The settings screen drives these from user-facing toggles.
//   2. The dev-only test harness flips them via RimWorldTestHarness's SetFeature step — bridged in
//      CelestialLighting.Probes' ProbeRegistration, so the shipped assembly still never references
//      the harness — letting a single scenario screenshot a colony with an effect off and then on
//      for an A/B visual diff, instead of eyeballing one frame and guessing what "before" looked
//      like.
//
// Kept as plain static bools rather than routed through ModSettings so the darkening/tint patches
// stay pure and offline-testable and take no dependency on a settings instance existing. Each flag
// gates exactly one effect; a flag turned off must fall back to the pre-feature behaviour (not to
// "no sky effect at all"), so that "off" is a faithful baseline of what the game looked like before
// that feature, which is what makes the harness A/B meaningful.
public static class CelestialLightingFeatures
{
    // Feature key the harness/settings use to address CivilTwilightPersistence. Kept next to the
    // flag so the bridge registration and any future settings binding share one spelling; scenario
    // JSON must use this same string in its SetFeature steps.
    public const string CivilTwilightPersistenceKey = "civil_twilight";

    // Civil-twilight persistence (linger warm tint through civil twilight after geometric sunset).
    // When off, Patch_TwilightColor falls back to the pre-feature glow-keyed-only warm band, so the
    // warm tint snaps off at geometric sunset exactly as it did before this feature existed — that
    // "off" state is the A/B baseline the harness screenshots against before flipping it on.
    public static bool CivilTwilightPersistence = true;

    // Feature key for PenumbraContrast (see CivilTwilightPersistenceKey for why the const lives here).
    public const string PenumbraContrastKey = "penumbra";

    // Angular-size penumbra contrast attenuation (Patch_ShadowStrength scales the global shadow
    // strength — GenCelestial.CurShadowStrength, which drives MatBases.SunShadow.color — down toward
    // the horizon as the solar-disk penumbra widens). When off, shadow strength keeps the raw
    // elevation-based intensity with no contrast falloff — crisp full-opacity shadows at every
    // elevation, exactly as before this feature — so "off" is a clean pre-feature baseline for the
    // harness A/B.
    public static bool PenumbraContrast = true;

    // Feature key for MoonShadows (see CivilTwilightPersistenceKey for why the const lives here).
    public const string MoonShadowsKey = "moon_shadows";

    // Real moon-cast night shadows (the moon subsystem's only visible effect so far — MoonPosition
    // drives a faint, phase-and-altitude-scaled shadow through Patch_ShadowDirection/Strength once
    // the sun is down). When off, MoonPosition.ShadowForMap returns null, so both shadow patches fall
    // back to a shadowless night — exactly what the game showed before the moon existed (vanilla
    // renders no real moon shadow; its fake "moonlight" shadow is already suppressed). That makes
    // "off" the faithful pre-feature baseline for the harness A/B. Gating at ShadowForMap — the
    // single point both patches consume — keeps the two from ever disagreeing about whether a moon
    // shadow exists. The moon *phase/illumination* model itself is deliberately not gated: it is
    // foundational state (§7 night-radiance will read it) with no independent visible effect to
    // toggle yet.
    public static bool MoonShadows = true;

    // Feature key for SkyColorTemperature (see CivilTwilightPersistenceKey for why it lives here).
    public const string SkyColorTemperatureKey = "sky_color_temperature";

    // §8 sky colour-temperature curve: Patch_SkyColorTemperature nudges colors.sky/overlay toward a
    // blackbody colour keyed on sun altitude (warm near the horizon, neutral overhead) — colour-only,
    // never glow. When off, the patch early-returns and leaves each WeatherDef's palette exactly as
    // vanilla/§2 renders it — the faithful pre-feature baseline for the harness A/B.
    public static bool SkyColorTemperature = true;

    // Feature key for Aurora (see CivilTwilightPersistenceKey for why it lives here).
    public const string AuroraKey = "aurora";

    // §11 auroral night-sky tint: Patch_AuroraTint shifts the night sky toward auroral colours while
    // a SolarFlare or vanilla Aurora condition is active, and at no other time (colour-only, never
    // glow). When off, the patch early-returns and leaves the sky exactly as vanilla/§2/§8 rendered
    // it — the faithful pre-feature baseline for the harness A/B.
    public static bool Aurora = true;

    // Feature key for AuroraCurtain (see CivilTwilightPersistenceKey for why it lives here).
    public const string AuroraCurtainKey = "aurora_curtain";

    // §11a auroral curtain: the structured, drifting ribbon overlay drawn over the map during the same
    // two events §11 tints for. §11 alone can only ever put ONE colour on the whole map at once, which
    // is why it had to stay subtle — turned up, a uniform hue reads as a colour grade rather than as an
    // aurora. This draws actual bands instead (AuroraCurtain generates the field, AuroraCurtainOverlay
    // renders it), so the effect gets its vividness from structure and movement rather than saturation.
    //
    // When off, the aurora falls back to §11's flat tint AT ITS FULL 0.18/0.08 — not to a weaker sky.
    // That is what makes "off" the faithful pre-curtain baseline the harness A/B screenshots against,
    // and it is why AuroraMath carries two pairs of tint peaks instead of one: turning this off has to
    // restore exactly what the mod rendered before §11a existed, rather than leaving the sky dimmer
    // than either version ever shipped.
    //
    // Gated under Aurora as well as on its own: Aurora is the master for "does this mod do auroras at
    // all", and a curtain drawing with that master off would be an aurora the player switched off.
    public static bool AuroraCurtain = true;

    // Feature key for NightRadiance (see CivilTwilightPersistenceKey for why the const lives here).
    public const string NightRadianceKey = "night_radiance";

    // §7 night-sky radiance: below the horizon, Patch_NightRadiance replaces vanilla's flat night
    // glow floor with the sum of starlight + airglow + (real, phase-and-altitude-scaled) moonlight.
    // When off, the patch early-returns and leaves vanilla's glow untouched — the faithful
    // pre-feature baseline the harness A/B screenshots against. The individual source strengths and
    // the "true pitch-black" atmospheric-floor switch are separate runtime knobs on
    // NightRadianceSettings (bridged for the harness under NightAtmosphericGlowKey); this master flag
    // gates the whole effect on/off the way every other effect flag does.
    public static bool NightRadiance = true;

    // Feature key bridging NightRadianceSettings.AtmosphericGlowEnabled — the "true pitch-black"
    // switch. Not a pre-feature baseline toggle (off gives a moonlight-only night, darker than
    // vanilla, not equal to it); it is a user setting (see NightRadianceSettings) exposed to the
    // harness so a probe scenario can flip it and watch the constant starlight+airglow floor drop
    // out of the night-radiance sum, leaving only moonlight.
    public const string NightAtmosphericGlowKey = "night_atmospheric_glow";

    // Feature key for LowLightDesaturation (see CivilTwilightPersistenceKey for why it lives here).
    public const string LowLightDesaturationKey = "low_light_desaturation";

    // §9 low-light desaturation / Purkinje shift: as the displayed glow falls toward night,
    // Patch_LowLightDesaturation drains colour saturation and drifts the sky/overlay tint toward a
    // cool blue-grey (colour-only — it never touches glow). When off, the patch early-returns and
    // leaves each WeatherDef's palette exactly as vanilla renders it — the faithful pre-feature
    // baseline for the harness A/B.
    public static bool LowLightDesaturation = true;

    // Feature key for PitchBlackNights (see CivilTwilightPersistenceKey for why it lives here).
    public const string PitchBlackNightsKey = "pitch_black_nights";

    // §7a "pitch-black nights": the VISUAL arm of night radiance. §7 only writes SkyTarget.glow, which
    // drives gameplay light but not on-screen darkness (RimWorld always draws the terrain and dims it
    // via MatBases.LightOverlay), so a low glow floor still renders dim-grey, not black.
    // Patch_PitchBlackOverlay darkens that overlay toward black in step with the night floor, so a
    // moonless / floors-off night can actually look black. Gated separately from NightRadiance because
    // it is a strong, taste-dependent visual change some players will want off even while keeping the
    // (invisible) glow floor; when off, the overlay is left exactly as vanilla/§9 composed it. How dark
    // it is allowed to get is the separate NightRadianceSettings.MinNightBrightness clamp.
    public static bool PitchBlackNights = true;

    // Feature key for EclipseDarkening (see CivilTwilightPersistenceKey for why it lives here).
    public const string EclipseDarkeningKey = "eclipse_darkening";

    // §10 eclipse MASTER (surfaced as the "Eclipse effects" checkbox). When off, everything eclipse
    // stands down — Patch_EclipseDarkening early-returns so vanilla's own flat SkyTargetLerpFactor
    // stands (the faithful pre-feature baseline for the harness A/B), the geometric trigger doesn't
    // fire, and the random eclipse isn't suppressed. When on, Patch_EclipseDarkening reshapes the dim
    // into the coverage ramp, and which flavour(s) of eclipse are live is chosen by EclipseSettings
    // .Mode (the natural/unnatural/both radio).
    public static bool EclipseDarkening = true;

    // Feature key for BloodMoon (see CivilTwilightPersistenceKey for why it lives here).
    public const string BloodMoonKey = "blood_moon";

    // §12 blood-moon crimson night: Patch_BloodMoon recolours the moonlit night deep crimson while
    // the (soft-dependency) VRE – Sanguophage blood-moon condition is active — colour-only, never
    // glow. When off, the patch early-returns and leaves the sky as vanilla/§2 rendered it — the
    // faithful pre-feature baseline. (The flag `CelestialLightingFeatures.BloodMoon` and the pure-core
    // type `BloodMoon` never collide — one is always qualified, the other referenced bare.)
    public static bool BloodMoon = true;

    // Feature key for WeatherDimming (see CivilTwilightPersistenceKey for why it lives here).
    public const string WeatherDimmingKey = "weather_dimming";

    // §13 weather dimming: clouds, rain and storms darken the rendered sky and soften cast shadows,
    // scaled by precipitation intensity. Vanilla does neither — WeatherDef.maxGlow defaults to 1.0
    // and is set exactly once across all vanilla XML, so as far as light is concerned a blizzard is
    // a clear noon. Colour-channel only (colors.sky/.overlay plus the shadow-alpha multiply); it
    // never writes SkyTarget.glow, so plant growth, solar output and pawn vision stay bit-for-bit
    // vanilla under every weather. When off, all three consumers early-return and the sky, shadows
    // and §9's desaturation read exactly as they did before this feature — the faithful pre-feature
    // baseline for the harness A/B. How strong the effect is at its peak is the separate
    // WeatherDimmingSettings.MaxDimming slider, which at 0 is independently a full no-op.
    public static bool WeatherDimming = true;

    // Feature key for IndoorSkyOcclusion (see CivilTwilightPersistenceKey for why it lives here).
    public const string IndoorSkyOcclusionKey = "indoor_sky_occlusion";

    // §7b indoor sky occlusion: vanilla clamps a roofed cell's baked sky cover to a constant 100/255,
    // so a sealed, unlit cave renders at ~61% of the sky colour day and night — no amount of §7a
    // overlay darkening can reach black indoors, because the interior is a fixed fraction *of the sky*.
    // Patch_IndoorSkyOcclusion raises that cover to full for roofed cells, so an unlit interior is lit
    // by its lamps or not at all. Gated separately from PitchBlackNights because it changes daytime
    // interiors too (an unlit shed at noon goes black), which is a much larger taste call than night
    // darkness; when off, the baked alphas are left exactly as vanilla wrote them — the faithful
    // pre-feature baseline for the harness A/B. Door leak and the accessibility cap are the separate
    // IndoorOcclusionSettings knobs.
    public static bool IndoorSkyOcclusion = true;

    // Feature key for EaveShadows (see CivilTwilightPersistenceKey for why the const lives here).
    public const string EaveShadowsKey = "eave_shadows";

    // §15 eave shadows: a roofed cell that is not enclosed — a porch, an overhang, the eave that
    // oversails a wall — casts a wall-height roofline shadow, which vanilla never does because its
    // only shadow casters are edifices. When off, EaveShadowGrid resolves caster heights straight off
    // the edifice grid and nothing else, so the mesh is bit-for-bit what §4 built before this feature
    // — the faithful pre-feature baseline for the harness A/B.
    //
    // Note the asymmetry with §7b: this flag gates only the shadow-caster injection. §7b's matching
    // "a porch is not indoors" fix is unconditional, because there it is not a new effect
    // but a correction to a question §7b was already asking wrongly.
    public static bool EaveShadows = true;

    // Feature key for EaveShade (see CivilTwilightPersistenceKey for why the const lives here).
    public const string EaveShadeKey = "eave_shade";

    // §15b eave shade: the roofed cell ITSELF is darkened, which the cast-shadow mesh structurally
    // cannot do — the skirt that would sweep back across a caster's own footprint is the
    // backface-culled one, so a caster never shades the cell it stands on. Without this the roof
    // reads as a bright lip against the shadow it is throwing, which is the artifact §15b exists to
    // remove.
    //
    // Split from EaveShadows purely as a DIAGNOSTIC axis, and the user-facing behaviour is
    // unchanged: CelestialLightingSettings drives both from the one `eaveShadows` setting, so a
    // player still has a single switch and the two halves can never diverge in a shipped game. What
    // the split buys is that the harness can now turn the caster and the shade off independently and
    // attribute a boundary artifact to one of them — with a single flag both halves vanish together
    // and every A/B frame is silent about which layer owned the effect.
    //
    // The two are separable at all only because they reach the screen by different routes: the
    // caster is baked into the sun-shadow mesh (EaveShadowGrid), the shade is its own SectionLayer.
    // They must agree about WHICH cells are involved — see EavesMath — but nothing forces them to be
    // switched on together.
    public static bool EaveShade = true;

    // Feature key for VacuumShadowContrast (see CivilTwilightPersistenceKey for why it lives here).
    public const string VacuumShadowContrastKey = "vacuum_shadow_contrast";

    // §18c vacuum shadow contrast: on an airless map (Odyssey orbital platforms), a daytime shadow
    // bottoms out at §18b's night light budget instead of at a skylight fill there is no air to
    // produce. Patch_WeatherShadowColor writes colors.shadow from NightRadiance.FloorGlowFor rather
    // than from a sky palette; the geometric penumbra is untouched, because the sun subtends the same
    // half-degree with or without air in the way. When off, that same patch keeps writing §13a's
    // atmospheric fill on vacuum maps and they render exactly as they did before this feature — the
    // faithful pre-feature baseline for the harness A/B.
    //
    // NOT surfaced in the settings screen, deliberately, and the §18 epic is expected to follow the
    // same rule: a vacuum branch is DERIVED (there is no dome, so there is no fill) rather than tuned,
    // so there is nothing here a player would sensibly want to dial. The flag exists for the harness
    // A/B and as an escape hatch if some other mod's vacuum biome turns out to want the atmospheric
    // look. Whether §18 as a whole eventually gets one user-facing "vacuum realism" switch is an open
    // question on the epic.
    public static bool VacuumShadowContrast = true;

    // Feature key for AxialTiltLunarGeometry (see CivilTwilightPersistenceKey for why it lives here).
    public const string AxialTiltLunarGeometryKey = "axial_tilt_lunar_geometry";

    // Realistic Axial Tilt's lunar geometry: with RAT installed, take the moon's declination from
    // their inclined-orbit model (inclination + node regression, both player-tunable on their
    // settings screen) instead of placing the moon on the ecliptic ourselves.
    //
    // Gated for a reason the other flags here don't have: this one depends on code that is not ours
    // and not required to exist. RAT's lunar block arrived additively, without an ApiVersion bump, so
    // the only runtime test for it is "did the method resolve" — and the answer differs between two
    // RAT builds a player might have. AxialTiltCompat.LunarGeometryActive is therefore this flag AND
    // the binding, and neither alone. Turning it off does not disable the interop: the moon falls
    // back to the sun's declination at MoonMath.MoonEquivalentSunDayOfYear, which under RAT is still
    // THEIR seasonal model, just their moon without its orbital inclination — the pre-feature
    // baseline, and exactly what a RAT too old to have a moon gives. That makes "off" both the
    // harness A/B baseline and a real escape hatch if an upstream lunar change ever misbehaves.
    public static bool AxialTiltLunarGeometry = true;

    // Feature key for PlanetsmithGeometry (see CivilTwilightPersistenceKey for why the const lives
    // here).
    public const string PlanetsmithGeometryKey = "planetsmith_geometry";

    // Planetsmith's per-world axial tilt: with Planetsmith installed, scale our seasonal swing by the
    // obliquity the CURRENT world was generated with instead of Earth's 23.44, so the sky matches the
    // planet whose biomes are on the map. See PlanetsmithCompat.
    //
    // Gated for the same reason AxialTiltLunarGeometry is — it depends on a third mod's code, so a
    // scenario cannot reach both arms without a switch — plus one that flag doesn't have: "off" here
    // is not only a baseline, it is a setting someone might actually want. A player who generated a
    // 70-degree world for its biomes has not necessarily signed up for the four-month polar night
    // that honestly lighting it implies. Off is exactly the pre-feature behaviour — our own tilt,
    // Planetsmith or not — so it is a faithful A/B baseline either way.
    public static bool PlanetsmithGeometry = true;

    // §14 sun-clock reconciliation. An enum rather than two bools because the modes are mutually
    // exclusive by construction: locked warps OUR sun onto vanilla's clock, realistic makes VANILLA's
    // glow follow ours. Running both would mean each defining itself in terms of the other.
    //
    // Defaults to LockedToVanilla: it is the only mode with zero gameplay impact, and it fixes a real
    // artifact the mod shipped with — 3 to 6 hours a day at ordinary latitudes where vanilla's sky was
    // lit while our sun sat below the horizon, i.e. bright ground casting no shadows.
    public static SunClockMode SunClock = SunClockMode.LockedToVanilla;
}

public enum SunClockMode
{
    // Warp our physical sun so it rises and sets exactly when vanilla's sky does. Day-length error is
    // zero by construction at every latitude and season. Inherits vanilla's quirks: a 5-degree polar
    // cliff between latitude 70 and 75, and a southern hemisphere whose polar day never arrives.
    LockedToVanilla,

    // Drive vanilla's glow from our sun. Physically correct everywhere, including the poles at the
    // equinoxes, at the cost of ~1.5 h average day-length change (and the growing hours that follow).
    Realistic,
}
