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

    // Feature key for PolarNightBlue (see CivilTwilightPersistenceKey for why it lives here).
    public const string PolarNightBlueKey = "polar_night_blue";

    // §19 polar night blue: Patch_PolarNightBlue nudges colors.sky/overlay toward the transmitted
    // spectrum of sunlight that has crossed a long slant path through the ozone layer (Chappuis
    // band), keyed on sun elevation alone — colour-only, never glow. Its second arm raises the
    // minimum overlay brightness §7a honours, which is visual-only and touches no gameplay value.
    // When off, the patch early-returns AND OzoneTwilight.OverlayFloorFor collapses to the caller's
    // own minBrightness, so both arms vanish together and the sky is exactly what vanilla/§2/§8/§9
    // renders — the faithful pre-feature baseline for the harness A/B.
    public static bool PolarNightBlue = true;

    // Feature key for PurpleLight (see CivilTwilightPersistenceKey for why it lives here).
    public const string PurpleLightKey = "purple_light";

    // §19c twilight purple light: Patch_PurpleLight nudges colors.sky/overlay toward the
    // superposition of §8's reddened horizon band with §19's ozone-crossed vault, across the
    // two-degree window (-6..-4) where both sources are live — colour-only, never glow, and with no
    // brightness arm at all. When off, the patch early-returns via PurpleLight.WindowStrengthFor and
    // the sky is exactly what vanilla/§2/§8/§9/§19 renders — the faithful pre-feature baseline for
    // the harness A/B. Note that "on" is ALSO a no-op outside that window by construction, so this
    // toggle only ever changes two degrees of every dusk and dawn.
    public static bool PurpleLight = true;

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

    // Feature key for CloudCover (see CivilTwilightPersistenceKey for why it lives here).
    public const string CloudCoverKey = "cloud_cover";

    // §22 partial cloud cover: a Clear-weather sky darkens and desaturates toward vanilla's own
    // Overcast/Rain/Fog palette in proportion to a slowly-drifting, biome/season-derived cloud
    // fraction (CloudCoverClock), and the weather label gains a "- N% cloudy" suffix to match.
    // Distinct from WeatherDimming above: that subsystem reshapes an ALREADY-cloudy weather's own
    // dimming curve and reads exactly 0 throughout Clear by construction, where this is the axis that
    // exists only during Clear — the two never move together and are gated separately. When off,
    // Patch_CloudCoverSky and Patch_CloudCoverLabel both early-return and Clear renders and reads
    // exactly as vanilla/§2/§8/§9/§13 already compose it — the faithful pre-feature baseline for the
    // harness A/B.
    public static bool CloudCover = true;

    // Feature key for CloudCoverLabel (see CivilTwilightPersistenceKey for why it lives here).
    public const string CloudCoverLabelKey = "cloud_cover_label";

    // A sub-toggle of CloudCover above, same relationship as AuroraCurtain to Aurora: this covers only
    // Patch_CloudCoverLabel's "- N% cloudy" weather-panel suffix, not the sky tint itself, so a player
    // who wants the visual effect but not the UI readout (or who finds a stable "- 0% cloudy" during a
    // calm hour more noise than signal — see that patch's header for why it is shown at every reading
    // now, including 0%) can drop the text without losing the render. Gated under CloudCover as well as
    // on its own, same reasoning as AuroraCurtain under Aurora: CloudCover is the master for "does this
    // mod do partial cloud at all", and a label reporting cloudiness with that master off would be
    // describing an effect the player switched off.
    public static bool CloudCoverLabel = true;

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

    // Feature key for IndoorGlowPassthrough (see CivilTwilightPersistenceKey for why it lives here).
    public const string IndoorGlowPassthroughKey = "indoor_glow_passthrough";

    // Lets ANOTHER MOD's indoor brightening reach the screen (issue #80). §7b decides occlusion from
    // geometry alone, so a mod that redistributes sky glow into roofed cells produced a real,
    // gameplay-visible, mouseover-reportable number that rendered as flat black. With this on,
    // SkyFalloffSource honours whatever sky-sourced glow actually reached the cell, ahead of §7c's own
    // native BFS.
    //
    // This replaced a per-mod interop (AmbientLightCompat, which reflected into f1995.ambientlight's
    // map component and re-derived its private falloff formula). The general version asks the glow grid
    // instead of the mod, so any mod that brightens interiors is honoured without being named — and it
    // reaches cases the old one structurally could not, notably ReBuild: Doors and Corners' glass walls
    // (§7c's BFS treats a glass wall as solid, because it holds roof and is not a door).
    //
    // When off, or on an unmodded install, IndoorGlowPassthrough.SkyFractionAt returns 0 for every
    // cell, so SkyFalloffSource falls straight through to §7c — the faithful pre-feature baseline for
    // the harness A/B.
    public static bool IndoorGlowPassthrough = true;

    // Feature key for NativeSkyFalloff (see CivilTwilightPersistenceKey for why it lives here).
    public const string NativeSkyFalloffKey = "native_sky_falloff";

    // §7c (issue #124): the same distance-from-opening sky gradient IndoorGlowPassthrough above gives
    // players who have Ambient Light installed, built natively for players who don't. A whole-map BFS
    // (NativeSkyFalloffGrid) grades §7b's blanket "every interior cell goes fully dark" back down near
    // an opening. Deferred to IndoorGlowPassthrough when another mod is answering rather than composed
    // — see SkyFalloffSource for why merging two independently-tuned gradients would put a visible
    // seam wherever the smaller maxDepth runs out. Default on: the whole point is to close the gap for
    // players without Ambient Light, so shipping it off would leave that gap unfixed by default. When
    // off, SkyFalloffSource.FractionAt returns 0 for every cell exactly like the passthrough's own
    // off-state, which is the pre-feature CapOcclusion identity — the faithful baseline for the harness
    // A/B.
    public static bool NativeSkyFalloff = true;

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

    // Feature key for SnowAlbedo (see CivilTwilightPersistenceKey for why the const lives here).
    public const string SnowAlbedoKey = "snow_albedo";

    // §21 the surface-cloud light cavity: weather buildup on the ground (snow today, Odyssey's sand
    // later) bounces light back up, the cloud base bounces it back down, and the map's diffuse light
    // is amplified by the resulting geometric series. SurfaceBuildup.CavityGainFor multiplies §7's
    // night floor by it, so a snowed-in map under an overcast no longer goes as black as a bare one
    // — which is exactly the tension with the mod's pitch-black-nights premise that §21 exists to
    // resolve in favour of the physics. When off, CavityGainFor returns exactly 1 and every consumer
    // sees bit-identical pre-§21 behaviour — the faithful baseline for the harness A/B, and the same
    // value a bare map produces anyway.
    //
    // NOT surfaced in the settings screen, for §18c's reason: the gain is DERIVED from published
    // albedos and §13's own cloud opacity rather than tuned, so there is no number here a player
    // would sensibly dial. It is a harness A/B axis and an escape hatch. The one thing that genuinely
    // wants a human look before this is called settled is the permanently-white biomes — ice sheet
    // and sea ice sit at full buildup year-round and become permanently brighter, which is physically
    // right but is a persistent change to the game's darkest maps (DESIGN.md §21).
    public static bool SnowAlbedo = true;

    // Feature key for SnowGlare (see CivilTwilightPersistenceKey for why the const lives here).
    public const string SnowGlareKey = "snow_glare";

    // §24 snow-glare bloom (issue #90). Default ON, after the live A/B answered #90's actual open
    // question — whether a static additive wash reads as BRIGHTNESS in a fixed-exposure top-down
    // game or merely as a washed-out screen. It reads as brightness at the calibrated strength: a
    // snowed-in overcast noon measures median CIELAB ΔE 5.13, and a polar snowfield 3.67 (low winter
    // sun) to 5.08, which sits alongside §21's own 6.06 rather than above the mod's whole measured
    // set. It shipped off through the prototype precisely so that question could be answered from
    // frames rather than from argument; DESIGN.md §24 keeps the numbers.
    //
    // WHAT DEFAULT-ON DOES NOT CLAIM: the physically larger inversion (a snowy overcast brighter
    // than a snowy CLEAR sky) is still NOT rendered at this strength — that needs roughly ΔE 15 and
    // reads as a milky haze. The shipped default is the visible-but-restrained half of #90, not its
    // headline. Anyone re-opening that trade should read §24's measured table first.
    //
    // What it does: §21's daytime cavity can amplify diffuse light past what SkyColorSet.sky can
    // express (a multiply whose brightest palette is already (1,1,1)), so a snowy overcast currently
    // clamps to clear-day parity and the inversion §21 exists to demonstrate — snowy overcast
    // BRIGHTER than snowy clear sky — flattens to a tie. SnowGlareOverlay draws the clamped-away
    // remainder as one additive full-map quad. When off, SnowGlare.AlphaFor returns 0, the overlay
    // returns before its draw call, and rendering is bit-identical to pre-§24 — the faithful baseline
    // for the A/B, and a genuinely zero standing cost on every map that never turns it on.
    public static bool SnowGlare = true;

    // Feature key for CloudUnderlight (see CivilTwilightPersistenceKey for why it lives here).
    public const string CloudUnderlightKey = "cloud_underlight";

    // §23 cloud-base underlighting (issue #88, option 1): a cloud deck keeps catching direct sunlight
    // from below for a while after the ground itself falls into Earth's own shadow, so
    // Patch_SkyColorTemperature scales §8's tint strength by CloudUnderlightMath.WarmthMultiplier —
    // above 1 while the deck is still lit (a high deck lingers longest), below 1 once the deck has
    // gone dark too (a low deck kills the tail early). Colour-only, and strictly a MODULATION of §8's
    // existing single sky colour, never a second colour target — see DESIGN.md §23 for why the actual
    // spatial "warm cloud against cool vault" contrast real skies show is out of scope for THIS flag.
    // That contrast is issue #88's option 2 and now exists as §23b (CloudUnderlightLayer below), which
    // is a complement rather than a replacement: this lane owns the sky's mean colour, that one owns
    // the structure above it.
    //
    // Zero altitude is a legitimate value (a ground-hugging deck), not a sentinel for "off" — see
    // WeatherDimming.CloudAltitudeMetresFor's header — so this flag is checked directly in
    // Patch_SkyColorTemperature rather than inferred from any pure-layer return value. When off, the
    // multiplier is never applied and §8 renders exactly as it did before this feature — the faithful
    // pre-feature baseline for the harness A/B. Coupled to WeatherDimming the same way §21's SnowAlbedo
    // is coupled to it: with WeatherDimming off, CloudOpacityFor already reads 0 everywhere, so this
    // silently has nothing to modulate regardless of its own flag — an honest consequence, not a bug.
    public static bool CloudUnderlight = true;

    // Feature key for CloudUnderlightLayer (see CivilTwilightPersistenceKey for why it lives here).
    public const string CloudUnderlightLayerKey = "cloud_underlight_layer";

    // §23b, issue #88's OPTION 2: the spatial half of cloud-base underlighting, drawn additively
    // through the pass §24 built rather than modulated into the sky palette's multiply. Warm underlit
    // cloud against a cool vault is a difference between two PLACES, which one flat sky colour cannot
    // express at all — §23 above is explicit about only getting the timing and intensity right.
    //
    // SHIPS OFF, and deliberately, exactly as §24 did through its own prototype phase. Issue #88 and
    // epic #103 both record the same open question, and neither can be settled by argument: RimWorld
    // is top-down with fixed exposure, so warm patches drifting over the ground may read as sky drama
    // or may read as stains on the terrain. Off is a true no-op — CloudUnderlightLayer.StrengthFor
    // returns 0, the overlay returns before its draw call, and rendering is bit-identical to pre-§23b
    // — which is what makes the harness A/B a real baseline rather than a picture of the mod being
    // absent, and what makes the standing cost on a player's save genuinely zero until this flips.
    //
    // INDEPENDENT OF CloudUnderlight above, in both directions. §23 modulates §8's flat tint and §23b
    // adds structure on top; they partition one quantity (the flat lane renders the field's mean, this
    // lane renders what is above it) but neither needs the other to be on. Turning both off reproduces
    // pre-§23 rendering exactly.
    public static bool CloudUnderlightLayer = false;

    // Feature key for CloudShadow (see CivilTwilightPersistenceKey for why it lives here).
    public const string CloudShadowKey = "cloud_shadow";

    // §23c: DAYLIGHT CLOUD SHADOWS — the same field §23b adds light through, subtracted instead, while
    // the sun is up. The two are one phenomenon at opposite ends of the day: below the horizon a deck
    // is the only lit thing in the sky and is a SOURCE; above it, the deck is an OCCLUDER and the
    // patches on the ground are shade.
    //
    // It exists because of what watching §23b showed. Warm patches with no cloud drawn above them read
    // as "the sun is being shaded by clouds" — which is the wrong reading of §23b (inside its window
    // there is no direct sun left to shade) and a completely right description of the other twelve
    // hours of the day, which the mod did not have. Rather than fight the eye's reading, this is the
    // effect the eye was reaching for.
    //
    // Ships OFF, same prototype posture as §23b and §25 alongside it.
    public static bool CloudShadow = false;

    // Feature key for CloudSheet (see CivilTwilightPersistenceKey for why it lives here).
    public const string CloudSheetKey = "cloud_sheet";

    // §25 (issue #138): the drawn cloud sheet — actual cloud between the camera and the map, above
    // FogOfWar, rather than only the light a deck adds or blocks. The other two lanes are
    // ILLUMINATION and stop at the ground; this is SKY.
    //
    // SHIPS ON, and it is the only one of the three prototype lanes that does — it is the one a
    // player would describe as "the mod draws clouds", where §23b and §23c are adjustments to light
    // that a player would attribute to the weather. Issue #138's open question (does drawn cloud read
    // as weather, or as something in the way of the base?) was asked of frames, and the answer was
    // that it reads as sky; what stays true is that it is a taste call, which is what the settings
    // checkbox is for. Off is a genuine no-op — CloudLayers.SheetAlphaFor returns 0 and the overlay
    // makes no draw call at all — so the pre-feature baseline is still exactly reachable for a
    // harness A/B, and that is also what the player's "off" buys them.
    //
    // A SUB-TOGGLE OF CloudCover, same relationship as CloudCoverLabel above and gated on both flags
    // in SheetAlphaFor. §22 is the master for "does this mod have an opinion about cloud at all", and
    // drawing a cloud deck over a player who switched that off would be the mod arguing with them.
    // The gate is a real one rather than bookkeeping: the sheet takes its coverage from §13's weather
    // deck as well as §22's Clear-day fraction, so without it a rainy day would still grow clouds
    // with partial cover switched off.
    //
    // It also knowingly double-counts §13/§22 over a solid overcast — the sheet and the flat dimming
    // are both rendering the same deck — which is recorded in CloudSheetMath.SheetAlpha and is the
    // first thing to fix now that this is on by default rather than opt-in.
    public static bool CloudSheet = true;

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
    // HARNESS-ONLY, and the only flag on this class with no settings-screen counterpart. Nothing in a
    // shipped game ever writes it. When a world-geometry mod is installed, that planet's obliquity is
    // ITS setting, and a switch of ours beside it would be a second source of truth for one number --
    // which is exactly the biome/sky disagreement this interop exists to remove. Realistic Axial Tilt
    // has never had an opt-out for the same reason; this is what makes Planetsmith match it rather
    // than being the one geometry source we let the player second-guess. The settings screen reports
    // the tilt and who set it instead of offering a choice.
    //
    // The flag survives because a scenario still needs both arms reachable inside a single run, and
    // "off" is a faithful pre-feature baseline -- our own tilt, Planetsmith installed or not.
    public static bool PlanetsmithGeometry = true;

    // Feature key for RealisticPlanetsGeometry (see CivilTwilightPersistenceKey for why the const
    // lives here).
    public const string RealisticPlanetsGeometryKey = "realistic_planets_geometry";

    // Realistic Planets 2's per-world axial tilt AND seasonal phase: with RP2 installed, run our sun
    // on the tilt the CURRENT world was generated with and on RP2's own reckoning of the year, so the
    // sky matches the planet whose biomes and weather are on the map. See RealisticPlanetsCompat.
    //
    // HARNESS-ONLY for the same reason PlanetsmithGeometry above is, and the reasoning there applies
    // here word for word: the tilt is RP2's setting, and a switch of ours beside it would be a second
    // source of truth for one number.
    //
    // It carries more than Planetsmith's flag does, though, and a scenario flipping it is measuring
    // more: "off" is our tilt on our phase, "on" is their tilt on their phase, so the two arms differ
    // by a fortnight of season as well as by a swing. That is also why "off" is still an honest
    // pre-feature baseline -- it is exactly the sky a build without this file renders.
    public static bool RealisticPlanetsGeometry = true;

    // §14 sun-clock reconciliation. An enum rather than two bools because the modes are mutually
    // exclusive by construction: locked warps OUR sun onto vanilla's clock, realistic makes VANILLA's
    // glow follow ours. Running both would mean each defining itself in terms of the other.
    //
    // Defaults to LockedToVanilla: it is the only mode with zero gameplay impact, and it fixes a real
    // artifact the mod shipped with — 3 to 6 hours a day at ordinary latitudes where vanilla's sky was
    // lit while our sun sat below the horizon, i.e. bright ground casting no shadows.
    public static SunClockMode SunClock = SunClockMode.LockedToVanilla;

    // Feature key for SkyFalloffRedraw (see CivilTwilightPersistenceKey for why the const lives here).
    public const string SkyFalloffRedrawKey = "sky_falloff_redraw";

    // §7b/§7c staleness fix: GameComponent_SkyFalloffRedraw rebuilds a map's lighting meshes when its
    // CurSkyGlow has drifted since they were last baked, since neither a roof edit nor a glow change —
    // the only two things that dirty a section otherwise — happens just because the clock advanced. A
    // room's baked brightness would otherwise stay pinned at whatever time it was last touched by
    // something else, straight through to the opposite end of the day. HARNESS-ONLY: there is no
    // equivalent settings-screen toggle, since "the mesh matches the current sky" is a correctness
    // property, not a taste knob a player would want turned off. Exists purely so a scenario can hold
    // the fix off, jump the clock across a civil-twilight ramp with no lamp or roof touched, and show
    // the stale mesh the bug used to leave behind — with it on, the same jump lands on a mesh that
    // matches the new CurSkyGlow. When off, GameComponentTick returns immediately and no map is ever
    // read, reproducing the pre-fix behaviour exactly (the mesh only updates from whatever else
    // dirties a section, same as before this file existed).
    public static bool SkyFalloffRedraw = true;

    // Feature key for VectorLights.
    public const string VectorLightsKey = "vector_lights";

    // §27 vector light sources: artificial light rendered as a visibility polygon cast from each
    // emitter — rays fired at the corners of the walls around it — instead of vanilla's flood-fill.
    // This is what produces a beam through a doorway, a hard shadow behind a rock, and firelight
    // spilling out of a window, none of which vanilla's grid can express: it records how far light
    // travelled and never which direction it came from.
    //
    // SHIPS OFF. It is a prototype, and it is the most opinionated thing in the mod — turning it on
    // makes indirectly-lit rooms genuinely darker, because light that vanilla delivered along a path
    // bending around a corner no longer arrives at all. That is the feature working, and it is still
    // a large enough taste call to be opt-in until it has been lived with.
    //
    // OFF REPRODUCES VANILLA EXACTLY, which matters more here than for most flags because the feature
    // has a suppressing half: with this false, Patch_VectorLightSuppress returns before touching the
    // lighting overlay and nothing is drawn, so the baseline frame is the real pre-feature render and
    // not a picture of the lights being missing.
    //
    // Gameplay light is untouched either way. map.glowGrid, GroundGlowAt, plant growth, work speed and
    // pawn vision are identical with this on or off — §27 changes only what is rendered.
    public static bool VectorLights = false;

    // Feature key for VectorLightPenumbra.
    public const string VectorLightPenumbraKey = "vector_light_penumbra";

    // §27 phase 2, soft shadow edges. Every emitter is treated as a disc half a cell across rather
    // than a point, so each shadow boundary gains a penumbra wedge that widens with distance from the
    // corner casting it — the transition band a real light of finite size produces.
    //
    // SEPARATE FROM VectorLights ON PURPOSE, even though it is meaningless without it. The two are
    // the only A/B that isolates the soft edge from the mechanism: with vector_lights on and this
    // off, the frame is phase 1's hard-edged render, which is the baseline a softness measurement
    // has to be taken against. Measuring against vanilla instead would measure the whole subsystem.
    //
    // OFF REPRODUCES PHASE 1 EXACTLY rather than approximately, and does it without a second draw
    // path: off passes a source radius of zero, no wedge geometry is emitted at all, and every fan
    // vertex already carries V = 0, which samples the row of the baked gradient that is the plain
    // falloff curve. The hard-edged mesh and the hard-edged texture lookup are the same objects the
    // soft version uses, not a preserved copy of them.
    public static bool VectorLightPenumbra = true;
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
