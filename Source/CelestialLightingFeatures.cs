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

    // Feature key for AuroraShaderField (see CivilTwilightPersistenceKey for why it lives here).
    public const string AuroraShaderFieldKey = "aurora_shader";

    // Whether the aurora curtain's field is evaluated PER FRAGMENT by CelestialAurora.shader, or
    // baked into a 192-square texture and stretched over the sheets (issue #196).
    //
    // WHAT IT CHANGES IS WHAT THE CURTAIN LOOKS LIKE. The bake gives a sheet 88 cells wide 2.2 texels
    // per cell, so everything the field draws arrives through a bilinear magnification: the rays come
    // out as soft vertical smears rather than as rays, the hem is a broad band rather than a line, and
    // the violet fringe under it blends into the green above it. Evaluated per fragment, all three are
    // drawn at the resolution of the screen. The gap widens the closer the camera gets, because the
    // magnification factor is what the camera controls.
    //
    // It also lets the field advance CONTINUOUSLY. The bake reaches the screen one completed sweep at
    // a time and is cross-faded between the last two, so its motion is quantised and it lags; the
    // shader is evaluated at the current tick, so the curtain simply moves.
    //
    // THIS IS NOT A PERFORMANCE SWITCH AND SHOULD NOT BE DESCRIBED AS ONE. There is no "turn it off if
    // you are short of frames" here — unlike the volumetric cloud beside it, which is genuinely a
    // GPU-cost trade a player might want to make. The field is three curtains of one-dimensional value
    // noise over bounded patches during a rare night-only event, and the path it replaces was doing
    // the same arithmetic on the CPU. Whichever way this flag sits, nobody is buying frames with it,
    // which is why the mod's settings screen does not offer it.
    //
    // IT DEGRADES ON ITS OWN, without this flag, wherever it cannot run: a missing AssetBundle, a
    // bundle built for another OS, a shader the card will not compile. AuroraCurtainOverlay checks
    // AuroraShader.Available AHEAD of this flag, so "on" never means an empty sky.
    //
    // OFF REPRODUCES THE BAKE EXACTLY, and that is the point of the flag rather than a courtesy. Both
    // paths take the same field, palette, driver tint, sheet layout and per-display alpha; only the
    // renderer changes. So the live A/B measures resolution, which is the claim — not "the aurora
    // looks different now", which would also be true if the port had a typo in it.
    // AuroraShaderAgreementProbe is the other half of that guarantee: it renders the shader and
    // compares it against AuroraCurtainHemRays, so a divergence fails loudly instead of quietly
    // shipping a different aurora.
    public static bool AuroraShaderField = true;

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

    // Feature key for IndoorOcclusionGather (see CivilTwilightPersistenceKey for why it lives here).
    public const string IndoorOcclusionGatherKey = "indoor_occlusion_gather";

    // Indoor sky occlusion's gather phase: build every dirty on-screen section's window across cores
    // on MapMeshDrawerUpdate_First, instead of one at a time inside each section's regenerate. Purely
    // a change of WHEN and WHERE the work happens — the same windows, the same values, the same
    // frame — so the only acceptable A/B result is a median CIELAB dE of 0.00, and any visible
    // difference is a bug rather than a trade-off.
    //
    // OFF REPRODUCES THE PREVIOUS BEHAVIOUR EXACTLY rather than skipping the work: with the flag
    // down, Gather returns before collecting anything and every section builds its own window inline
    // through TakeOrBuild's miss path, which is the code that ran before this existed. That is what
    // makes the off arm a baseline instead of a picture of indoor occlusion being absent.
    //
    // Defaults ON. It is not a taste call and there is nothing for a player to judge — the shipped
    // expensive-feature-is-opt-in rule is about cost the player chooses to pay, and this only ever
    // subtracts cost. SkyOcclusionGather.ParallelSafe is the safety valve that decides per install,
    // and it stands the phase down on its own where another mod owns the glow accessors.
    public static bool IndoorOcclusionGather = true;

    // Feature key for DecoupledIndoorFloor (see CivilTwilightPersistenceKey for why it lives here).
    public const string DecoupledIndoorFloorKey = "decoupled_indoor_floor";

    // Stops the two "never fully black" floors from compounding. MinNightBrightness is a fraction of the
    // undarkened sky; MinIndoorBrightness was a fraction of whatever §7a had already left of it, so the
    // shipped Cinematic pair (0.50 / 0.50) rendered 0.50 outdoors against 0.25 indoors at the night floor
    // — two sliders showing the same number and meaning different things. On, §7b divides its floor by
    // §7a's keep factor (IndoorOcclusionMath.EffectiveIndoorFloor) so both are fractions of the same sky.
    //
    // Daylight is untouched either way: keep is 1 whenever §7a is not darkening, and the division is then
    // the identity. When off, CapOcclusion receives the raw setting exactly as before — the faithful
    // pre-feature baseline for the harness A/B, and a one-flag revert if the parity reads worse than the
    // compounding did.
    public static bool DecoupledIndoorFloor = true;

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

    // Feature key for CloudDeckVarieties (see CivilTwilightPersistenceKey for why it lives here).
    public const string CloudDeckVarietiesKey = "cloud_deck_varieties";

    // §25b: whether the sky is LAYERED — cumulus low, altocumulus in the middle, cirrus high, each
    // at its own altitude, its own opacity, its own speed and its own sunset window — or all one
    // deck, which is what §25 drew before this existed.
    //
    // SHIPS ON, AND UNLIKE CloudSheet ABOVE IT GETS NO SETTINGS CHECKBOX. That is the distinction
    // rather than the default: §25 ships on behind "Visible clouds" because whether the mod draws
    // clouds at all is a real opinion a player might hold. This is not a lane and not an opinion — it
    // is a property of the cloud that lane already draws, costing one extra atlas row and nothing per
    // frame, and "would you like your clouds to all be at the same altitude" is not a question worth
    // a checkbox. With CloudSheet off it is unreachable either way.
    //
    // WHAT IT EXISTS FOR IS THE A/B, and the shape of "off" is what makes that A/B honest. Off does
    // NOT mean "no varieties feature" in the sense of skipping the code — it means the deck mixture
    // collapses to all-low (CloudSheetDraw.PlaceSheets), so both sides draw from the same atlas with
    // the same shapes, the same placements, the same sizes and the same speeds, and the ONLY
    // difference between the two frames is which sheets were promoted off the low deck. Without that,
    // an A/B could only compare §25b-on against §25-absent and would measure both at once.
    public static bool CloudDeckVarieties = true;

    // Feature key for CloudPresence (see CivilTwilightPersistenceKey for why it lives here).
    public const string CloudPresenceKey = "cloud_presence";

    // §25d (issue #144): the recalibration that makes §25's drawn cloud actually visible.
    //
    // FOUR TERMS, ONE FLAG, because they are one decision and splitting them would let a build exist
    // that nobody has looked at:
    //   * the lane's amplitude, 0.35 -> 0.55 (CloudSheetMath.PresentSheetAmplitude)
    //   * the daylight cloud colour, a 0.86 grey -> near-white (CloudSheetOverlay.PresentDayColour)
    //   * opacity decoupled from how lit the GROUND is (CloudSheetMath.DeckOpacity)
    //   * the direct-lit ceiling, 0.55 -> 1.0 (CloudSheetMath.SunlitDeckCeiling)
    //
    // WHY IT IS A RECALIBRATION AND NOT A LANE. Nothing here computes anything new. §25 and §25b
    // already draw the cloud, place it, deck it and colour it correctly — the measured failure was
    // that every one of those answers was multiplied down to somewhere between two and seven parts in
    // 255 before it reached the screen. At -2.44 degrees §25c's raymarch and §25b's bake measured a
    // mean 0.19/255 apart with not one pixel differing by more than 2: two renderers agreeing because
    // neither had anything to draw. This is the term that gives them something.
    //
    // SHIPS ON, unlike §25c beside it. §25c adds a cost and a binary asset and needs a GPU budget
    // before it can be a default; this costs nothing, needs nothing, and the alternative is
    // continuing to ship a cloud subsystem whose whole output is invisible.
    //
    // OFF REPRODUCES §25b EXACTLY, all four terms, which is what keeps every §25/§25b scenario pin in
    // the repo meaningful and gives the A/B a real baseline rather than a picture of no clouds.
    public static bool CloudPresence = true;

    // Feature key for CloudVolume (see CivilTwilightPersistenceKey for why it lives here).
    public const string CloudVolumeKey = "cloud_volume";

    // §25c (issue #144): whether the drawn cloud sheet is RAYMARCHED per pixel through a baked 3-D
    // density volume, or drawn as §25b's flat quad wearing a baked 2-D atlas.
    //
    // SHIPS ON, with the baked atlas as the PERFORMANCE OPTION behind it rather than as the default.
    // The march is 192 volume fetches per fragment on the GPU, and the CPU it adds is eight uniform
    // writes per sheet per frame, which the analyzer reports as approximately free. Frame time at
    // 1080p measured inside the noise band across three alternating repeats at two zooms — though
    // the frame is CPU-bound near 5 ms there, so that is a statement about this machine and not a
    // promise about every card. A player who is GPU-bound turns it off and gets §25b.
    //
    // IT DEGRADES TO §25b ON ITS OWN, without this flag, wherever it cannot run: a missing
    // AssetBundle, a shader the card will not compile, a graphics API without 3-D textures.
    // CloudSheetOverlay checks CloudVolumeShader.Available AHEAD of this flag, so "on" never means
    // an empty sky.
    //
    // OFF REPRODUCES §25b EXACTLY, and not merely "no clouds". Both paths take the same placements,
    // deck, overlap boost, illumination and alpha — all of it computed before either renderer is
    // chosen — and §25c's extinction is calibrated so its column alpha matches the atlas's deck for
    // deck (1.03 / 0.95 / 0.98). So the flag switches the RENDERER and nothing else, which is what
    // makes it a real A/B rather than a change of how much sky got covered.
    public static bool CloudVolume = true;

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
    // THIS FLAG'S DEFAULT IS NOT THE SHIPPED DEFAULT, and the gap is deliberate. A new install gets
    // vector lighting ON (UpdateNotice.SeedFirstRun seeds the setting, ApplyToRuntime pushes it in
    // here), while an existing install keeps it off until the one-time notice is answered. The static
    // initialiser stays FALSE because it is also the value FeatureRegistry.ResetAll restores between
    // scenarios in a suite: registered as true, §27 would switch itself on for every later scenario
    // in the file and rewrite their lighting. See TestMod/ProbeRegistration.cs, which says the same
    // thing from the other end.
    //
    // It is the most opinionated thing in the mod — turning it on makes indirectly-lit rooms
    // genuinely darker, because light that vanilla delivered along a path bending around a corner no
    // longer arrives at all. That is the feature working, and it is a large enough taste call that
    // an upgrading player is asked rather than switched.
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

    // Feature key for VectorLightSuppress.
    public const string VectorLightSuppressKey = "vector_light_suppress";

    // §27's suppressing half: whether Patch_VectorLightSuppress zeroes the artificial-light RGB in
    // SectionLayer_LightingOverlay before our polygons are drawn over it.
    //
    // ON is the real subsystem. Vanilla's flood has already lit every cell §27 wants to carve a
    // shadow into, and an additive pass cannot remove light, so without this every shadow simply
    // fills back in from underneath and the whole mechanism reduces to a brightness increase.
    //
    // OFF IS THE MIXED CASE, and it is worth being able to look at rather than only to reason about.
    // Epic #145 rejected "additive polygons on top of vanilla's render" on exactly the argument
    // above; this flag is what lets that rejection be a photograph instead of a claim. It is also
    // the escape hatch the epic asks for in as many words — the suppressing half is the risky one,
    // because with it on anything §27 does not know about goes BLACK rather than merely unimproved,
    // and it was always meant to be droppable independently of the polygons if that went wrong.
    public static bool VectorLightSuppress = true;

    // Feature key for VectorLightBlend.
    public const string VectorLightBlendKey = "vector_light_blend";

    // §27 crossfaded with vanilla's flood rather than replacing it: a fraction of vanilla survives
    // underneath as a floor, and our own contribution drops by the same fraction so the overall level
    // does not move. See VectorLightMath.DefaultVanillaFloor for why it compensates rather than adds.
    //
    // ON BY DEFAULT whenever §27 itself is on, and the deciding argument is compatibility rather
    // than taste. §27 knows about exactly what vanilla's GlowGrid tells it: registered glowers and
    // glowing terrain. That covers any mod adding an ordinary CompGlower, and it does NOT cover light
    // that arrives by some other route — a mod passing sunlight through a window, anything writing
    // its own section layer, anything lighting cells without registering a glower. With the
    // suppression total, every one of those goes BLACK rather than merely unimproved, and each would
    // need finding and special-casing one at a time. With a floor under it, they are all simply dim,
    // and the list of things §27 has to know about stops being load-bearing.
    //
    // The look is the same bargain seen from the other side: shadows are dim rather than dark, and
    // nothing is ever black. Off is §27 as originally designed — shadows reach full dark, at the
    // price of a room lit only by light bending around a corner losing all of it.
    public static bool VectorLightBlend = true;

    // Feature key for VectorLightMask.
    public const string VectorLightMaskKey = "vector_light_mask";

    // §27 phase 3: stop drawing a second lighting model and start EDITING vanilla's, by subtracting
    // each emitter's own light back out of the cells our polygons say it cannot reach.
    //
    // WHY THE OPERATOR HAD TO INVERT. Phase 1 replaced vanilla's render with an additive pass; phase
    // 2b tried to compose the two as a max and measured a no-op. The reason generalises: our falloff
    // IS vanilla's falloff, so the two models agree wherever both can see, and nothing that only ever
    // ADDS can express a shadow — which is the whole of what §27 has to say. Subtracting is the only
    // operator that can.
    //
    // WHAT IT BUYS BEYOND THE SHADOW. The level stops needing calibration, because a lit cell is left
    // at exactly vanilla's own value rather than at an additive approximation of it; DaylightScale
    // stops being needed, because we edit the value the sky's multiply consumes instead of drawing
    // above it; and — the one that matters most — nothing we did not model is ever touched, because
    // we subtract a NAMED emitter's own contribution and nothing else. That last property is the
    // compatibility problem VectorLightBlend exists to manage, solved rather than tuned.
    //
    // WHAT IT COSTS. Resolution. The lighting overlay's mesh carries one vertex per cell corner and
    // one per cell centre, so a boundary can only be placed to within a cell and is interpolated
    // bilinearly between. VectorLightMath.LitFraction samples each cell and reports the share the
    // polygon covers rather than a yes or no, which turns a staircase into a ramp — and phase 2
    // already softens every edge to half a cell deliberately, so the two blurs are the same order.
    //
    // REQUIRES GlowGridPerLight, and stands down without it. Reading vanilla's per-emitter arrays is
    // what makes the subtraction targeted; with them unreadable there is nothing to subtract, and
    // VectorLightMask.Active goes false rather than the mask guessing.
    //
    // ON, AND IT IS WHAT §27 SHIPS AS. The crossfade stays reachable by turning this off, but
    // the mask is what the subsystem is designed around now: it is the only composition that
    // carves a real shadow without suppressing anybody else's light, and two later features are
    // built on it — the beam below, and phase 4's pawn shadows, which ask its coverage grid
    // whether a lamp can see a pawn at all. Inert unless VectorLights is on.
    public static bool VectorLightMask = true;

    // Feature key for VectorLightMaskBeam.
    public const string VectorLightMaskBeamKey = "vector_light_mask_beam";

    // §27 phase 3's other half: keep the additive pass running ON TOP of the mask, at a reduced
    // strength, so the lit region gains the beam the mask alone cannot produce.
    //
    // THE MASK AND THE BEAM FAIL IN OPPOSITE DIRECTIONS, which is why both exist. The mask only
    // subtracts, so it delivers §27's shadows and a beam DIMMER than vanilla's. Phase 2b's max only
    // added, so it delivered vanilla's brightness and no shadow at all. Running them together is the
    // first arrangement in §27 that can have both: vanilla with the bent light taken out, plus a
    // fraction of our own model put back over what remains.
    //
    // IT IS NOT THE MIXED CASE. Epic #145 rejected drawing our full model over vanilla's full model,
    // measured at 6 L* bright. Here what is underneath has already had the shadowed light removed,
    // so the sum is (V + k*O) * lit rather than V + k*O — vanilla scaled inside the lit region and
    // zero outside it, instead of two complete lighting models added together.
    //
    // Inert unless VectorLightMask is on, so it cannot contaminate any other arm.
    //
    // ON, alongside the mask. The two fail in opposite directions and only together have both
    // halves: measured, the mask alone reaches shadow 9.07 with the doorway beam DIMMER than
    // vanilla's at 13.34, while the pair keeps that shadow and takes the beam to 16.38 — the
    // best contrast of any arm at 1.68, against full §27's 1.66 and the crossfade's 1.38.
    public static bool VectorLightMaskBeam = true;

    // Feature key for VectorLightPawnShadows.
    // Feature key for VectorLightShaderMax.
    public const string VectorLightShaderMaxKey = "vector_light_shader_max";

    // §27 phase 6: the max evaluated PER FRAGMENT on the polygon's own fan, instead of per cell on
    // the lighting overlay's mesh.
    //
    // WHY THIS EXISTS AFTER PHASE 5. Phase 5 computes exactly the right level and delivers it at
    // cell resolution, because `coverage` is one byte per cell and the lighting overlay carries one
    // vertex per cell corner plus one per centre. A shadow BOUNDARY survives that — a long straight
    // edge blurred by half a cell still reads straight. A one-cell APERTURE does not: a doorway beam
    // comes out a soft ellipse where it should be a wedge, measured and captured in
    // Tests/Screenshots/maskmax_door_beam_shape.png. Coverage is the polygon's area integral over a
    // cell; the fan IS the polygon. One is a summary, the other is the shape.
    //
    // SO THE SHADER IS NEEDED AFTER ALL, for a reason #151 never gave. #151 justified it as the only
    // way to get vanilla's glow into a fragment program; that is not true — the mask holds it in C#,
    // which is what phase 5 proves. It is needed because the fan is the only surface in §27 finer
    // than a cell, and modulating the fan per cell needs vanilla's value at its vertices. The fan's
    // vertices sit at arbitrary sub-cell positions on the visibility polygon, so a max evaluated
    // there keeps the wedge.
    //
    // THE TWO HALVES WANT DIFFERENT RESOLUTIONS, and that is the whole design. The shadow is a long
    // boundary and is carved by phase 3's mask at cell resolution, where cell resolution is fine.
    // The level is a one-cell aperture and is set by this pass at polygon resolution, where nothing
    // coarser will do. Phase 5's per-cell lift stands down when this is on — they are two deliveries
    // of the same quantity and running both would light the region twice.
    //
    // Requires the shader to have loaded. A bundle that is absent, built for another OS, or
    // unsupported on the player's hardware all land as VectorLightShader.Available == false and the
    // subsystem falls back, because a missing shader must never mean missing light.
    //
    // ON, AND IT IS THE COMPOSITION §27 SHIPS AS. Measured against the flat beam it replaces, on a
    // sealed roofed room lit by one torch at midnight — the scene that answers the question every
    // §27 composition has to answer, since epic #145 rejected drawing our model over vanilla's at
    // 6 L* over:
    //
    //     lit room   vanilla 12.05    flat beam 14.83 (+2.78)    phase 6 12.44 (+0.39)
    //     near lamp  vanilla 17.89    flat beam 22.55 (+4.66)    phase 6 18.04 (+0.15)
    //     whole frame, masked ΔE against vanilla:  flat beam 2.52,  phase 6 0.84
    //
    // and on an open door, where the flat beam lifts the doorway +0.51 L* at masked ΔE 1.25 while
    // this delivers +1.36 at masked ΔE 2.99 — 2.7x the beam for a fiftieth of the indoor spill,
    // with no strength to choose. VectorLightSettings.BeamStrength now governs only the fallback
    // path below.
    //
    // Inert unless VectorLights is on.
    public static bool VectorLightShaderMax = true;

    // Feature key for VectorLightShaderMaxSubtract.
    public const string VectorLightShaderMaxSubtractKey = "vector_light_shader_max_subtract";

    // THE CONTROL ARM for the pass above, lifted from #151 along with the shader. With this off the
    // fragment program computes max(0, ours - 0), which is MoteGlow's output exactly, so the arm
    // renders whatever the stock additive pass renders and any difference is the SHADER rather than
    // the composition.
    //
    // #151 records what that arm is worth: its first run measured a masked ΔE of 5.58 that had
    // nothing to do with the arithmetic — the bundle declared the default "Queue"="Transparent"
    // (3000) against MoteGlow's 3151, so the additive pass drew under the lighting overlay's
    // multiply and came out dimmer than vanilla exactly where it was supposed to be adding light.
    // Without this arm that reads as the composition being wrong.
    //
    // ON.
    public static bool VectorLightShaderMaxSubtract = true;

    // Feature key for VectorLightSurfaceLift.
    public const string VectorLightSurfaceLiftKey = "vector_light_surface_lift";

    // The surface lift: the beam brightens the surface it lands on instead of adding light beside it.
    //
    // THE REPORT, WORD FOR WORD. "The vector lighting through a door is a lot brighter than outdoors
    // and notably, doesn't actually light the other room up — no features are lit, just the
    // additional glow." Both halves are one property of the compositing. Phase 6's pass is ADDITIVE
    // and sits above the lighting overlay's multiply, so it adds a fixed amount of light to a pixel
    // regardless of what that pixel is: the ground beyond a doorway has been multiplied near-black,
    // and a smooth wedge added on top of it is a smooth wedge. The floor's own texture is still
    // there underneath, at unchanged absolute contrast against a mean the beam has raised — which is
    // exactly what "no features are lit, just the additional glow" describes.
    //
    // WHAT CHANGES. The blend, and the divisor that gives the same output its new meaning. At
    // Blend One One the frame gains the fragment program's number; at Blend DstColor One the frame is
    // multiplied by one plus it. Light on a surface is albedo * illuminance and the frame already
    // holds albedo * ambient, so multiplying is what carries the ground's texture into the lit
    // region. The program divides its excess by (sky ambient + vanilla's own glow here), which is
    // the factor that takes a cell from what it renders at to what our model says it should render
    // at — so the level becomes a RATIO against its surroundings rather than an absolute amount of
    // light, and that is the half that answers "a lot brighter than outdoors".
    //
    // MEASURED, on one torch four cells from each of two open doors at midnight. Under the beam, one
    // cell past the door, the readable contrast of the gravel goes 0.0691 unlit -> 0.0393 additive
    // -> 0.0702 lifted: the additive pass destroys 43% of the ground's texture while brightening it,
    // and the lift restores it. The room the beam is NOT for moves +1.03 -> +1.15 L*, i.e. the
    // self-limiting property survives. See DESIGN.md for the two mistakes made reaching that.
    //
    // WHY §11a AND §23b STAY ADDITIVE, since this contradicts the idiom they established. An aurora
    // and an underlit cloud base are emitting MEDIA seen in the sky; they are not supposed to reveal
    // the terrain under them, and adding is right for both. A lamp beam falls on a floor. §27 took
    // its compositing from the wrong neighbour, and issue #103's additive-overlay epic is about sky
    // headroom rather than about surfaces, so nothing there is being reversed.
    //
    // THE CEILING IS THE BLEND'S. A UNORM target clamps the fragment output to [0, 1] before
    // blending, so the frame can never be more than doubled — one stop over the ambient, whatever
    // that ambient is. That is a bound rather than a knob, and it is why this needs no separate
    // guard against washing a room out the way the flat beam did.
    //
    // Requires the shader, like phase 6 itself: the blend state lives on the material built from it,
    // so a machine that fell back to MoteGlow gets the additive pass and this flag does nothing.
    //
    // OFF, pending a look at it in a real colony rather than in a fixture. Off reproduces the shader
    // max exactly — same program, same gradient, one blend factor and one property apart — so the
    // A/B measures the compositing and nothing else.
    public static bool VectorLightSurfaceLift = false;

    // Feature key for VectorLightGapParity.
    public const string VectorLightGapParityKey = "vector_light_gap_parity";

    // Subtract what vanilla still contributes ON SCREEN, rather than what its glow grid holds.
    //
    // THE DEFECT, MEASURED. Two identical roofed rooms, one torch each, each torch four cells from
    // its own opening onto the same open ground; the only difference is one wall cell, a door in one
    // and a bare gap in the other. Past the door the composition adds 5.5 L*. Past the gap it
    // SUBTRACTS 1.4 — the ground outside comes out darker than vanilla and no beam is drawn at all.
    // One wall cell, and the two openings look nothing like each other.
    //
    // WHY, FOR THE HALF THAT IS UNDERSTOOD. Phase 3's mask has already scaled each cell's vanilla
    // light by this emitter's coverage,
    // which is how much of the cell our polygon can actually see. The fragment program then subtracts
    // the RAW glow-grid value on top of that, so the part the mask already removed is removed twice.
    // Past a door that is invisible: RimWorld's glow grid never learns a door opened, the raw value
    // beyond one is exactly zero, and zero subtracted twice is still zero — which is why every
    // doorway scenario in this repo measured the composition working perfectly. Past a one-cell gap
    // the grid floods straight through, coverage is well under 1, and the double subtraction is the
    // entire result.
    //
    // NOT A GAP-SPECIFIC RULE, which is the thing worth being clear about. Nothing here asks how the
    // light got out. The composition was always meant to subtract what vanilla delivers where we are
    // drawing, and this makes it do that; a door is simply the case where the two answers coincide.
    //
    // WHAT THIS DOES NOT EXPLAIN, stated here because the flag's name suggests more than it delivers.
    // It recovers roughly half the gap's deficit and no more. The obvious account of the rest — the
    // mask trimming vanilla to our coverage — is contradicted by the geometry: an offline probe of the
    // shipped core puts the polygon at the full radius through a one-cell gap with coverage 255 on the
    // axis for seven cells beyond it, and CoverageAt answers 255 outside its grid rather than 0. See
    // DESIGN.md for what has been ruled out, including the fact that the scenario's vanilla baseline
    // is itself confounded and has to be fixed before the residual is worth chasing.
    //
    // OFF while it is measured. Off uploads the raw field exactly as before.
    public static bool VectorLightGapParity = false;

    // Feature key for VectorLightApertureBeam.
    public const string VectorLightApertureBeamKey = "vector_light_aperture_beam";

    // Our model REPLACES this emitter's vanilla light inside the polygon, instead of the fan
    // composing a max against it.
    //
    // WHAT IT IS FOR. Light through a bare gap does not read as a beam the way light through a door
    // does, and the reason is not geometry: an offline dump of the shipped core puts the polygon at
    // the full radius through a one-cell gap with coverage 255 along its axis and a penumbra that
    // widens with distance, exactly as an aperture should. The beam is composed away rather than
    // culled away. Vanilla's flood takes a SHORT path through an open hole and arrives at close to
    // our own straight-line value, so max(0, ours - vanilla) is degenerate there by construction and
    // the fan draws nothing. Beyond a DOOR the same arithmetic gives our whole model, because
    // RimWorld's glow grid never learns a door opened and vanilla delivers exactly zero — which is
    // why a doorway has always looked right and an aperture never has.
    //
    // NOT AN APERTURE-SPECIFIC RULE. Nothing in it asks how the light left the room; it removes the
    // emitter's vanilla contribution wherever the mask runs and lets the fan deliver the model. A
    // doorway already arrives at that state on its own, so this makes the two cases the same
    // arithmetic rather than adding a second path that has to agree with the first.
    //
    // THE RISK IT HAS TO CLEAR is epic #145's rejected option, where drawing our model over an
    // UNsuppressed flood summed two lighting models and landed a room 6 L* bright. This is a
    // replacement and not a sum — the mask takes the emitter's light off before the fan puts ours
    // back — and under the surface lift the result is bounded by the blend's own 2x ceiling as well.
    //
    // AND IT DOES NOT CLEAR THE OPPOSITE RISK, WHICH IS THE FINDING. It buys the aperture beam by
    // spending exactly what the max exists to protect: the torch's own RADIANCE. Judged on the
    // frames rather than the table — the beam reaches parity with a doorway to within 0.2 L* and the
    // lamp still looks wrong, because the warm near-field falloff around it is gone. Measured, the
    // lamp cell goes 19.89 -> 17.87 and two cells out 9.68 -> 7.16.
    //
    // The cause is not the composition and cannot be tuned out of it. Once vanilla's contribution is
    // removed, the fan has to deliver a lamp's near field on its own, and that is where the model
    // asks for a multiplier far above Blend DstColor One's ceiling of 2x — the saturation
    // JustPastAnOpenDoorTheModelAsksForMoreThanTheBlendCanGive pins offline. The max was never a
    // compromise: leaving vanilla holding the cells it already gets right is what keeps a lamp
    // looking like a lamp, and this trades that away wholesale to fix one region.
    //
    // SO A GLOBAL REPLACEMENT IS THE WRONG SHAPE, and the next attempt should not be a gentler
    // version of it. What the arms actually establish is a contradiction worth resolving first: the
    // aperture arm proves our model has +2.57 L* to give at those cells, the max arm proves the
    // fragment program computes ours - vanilla ~ 0 there, and the frame shows vanilla contributing
    // only +0.5 over the local background. Two of those three cannot all hold. The next step is a
    // probe reporting `ours` and the sampled `vanilla` side by side for ONE named cell beyond the
    // gap — not another composition. This repo has three times now theorised its way to a plausible
    // wrong answer about that pair.
    //
    // OFF, and kept only as the arm that produced the finding above.
    public static bool VectorLightApertureBeam = false;

    // Feature key for VectorLightBentPath.
    public const string VectorLightBentPathKey = "vector_light_bent_path";

    // The aperture beam's replacement, decided PER CELL instead of per emitter.
    //
    // WHAT THE APERTURE BEAM GOT RIGHT AND WRONG. Handing our model the whole of an emitter's field
    // reached doorway parity and cost the torch its near-field radiance, because once vanilla's
    // contribution is gone the fan has to deliver a lamp's own falloff on its own and the model
    // there asks for more than Blend DstColor One's 2x ceiling can give. The level was never the
    // problem: the cells around a lamp are cells vanilla already lights correctly, and taking them
    // was pure loss.
    //
    // So the replacement is made cell by cell. Vanilla keeps every cell it reached by the same route
    // our polygon sees along, and our model takes only the cells vanilla had to DETOUR to — which
    // is the aperture fringe and the far side of an open door, i.e. exactly the region the whole
    // subsystem exists for. See VectorLightLiftMath's ownership header for why the test is on
    // vanilla's own accumulated distance rather than on the two brightnesses; a brightness
    // comparison claims most of an open-ground lamp and rebuilds the aperture beam by accident.
    //
    // TWO HALVES THAT HAVE TO AGREE, which is the thing to be careful with when editing either. The
    // mask decides how much of an emitter's vanilla light to take OFF a cell, and the per-emitter
    // texture the fragment program subtracts has to describe what the mask LEFT. They call the same
    // pure predicate for that reason; a disagreement is either light removed and never redrawn or
    // light subtracted twice, and neither announces itself.
    //
    // HOW MUCH IT CLAIMS, MODELLED OFFLINE FIRST: two cells of the gate scene, holding 7 levels of
    // glow each. This is a small effect by construction rather than by tuning — our polygon and
    // vanilla's flood share a blocker set, so the only geometry they genuinely disagree about is an
    // open door, and there vanilla delivers nothing and the composition already degenerates to our
    // whole model. It states one rule where a doorway and a gap previously arrived at two different
    // places by accident, and that is its whole claim.
    //
    // Inert unless VectorLightMask is on, and subsumed by VectorLightApertureBeam when that is on —
    // a global replacement has already taken every cell this could.
    //
    // OFF while it is measured.
    public static bool VectorLightBentPath = false;

    // Feature key for VectorLightMaskMax.
    public const string VectorLightMaskMaxKey = "vector_light_mask_max";

    // §27 phase 5: the mask's lift is decided by max(vanilla, ours) instead of by a strength knob.
    //
    // WHAT IT REPLACES. VectorLightMaskBeam above keeps the additive MoteGlow pass running over the
    // mask at a reduced strength, which lifts EVERY cell of the lit region by the same fraction of
    // our model — including the cells vanilla already lit correctly. That is why
    // VectorLightSettings.BeamStrength exists: the flat lift had to be cut back until the room
    // stopped reading bright, and the cut applies equally to the cells that needed it. With this on,
    // the mask computes the lift itself, per emitter and per cell, as
    //
    //     max(0, ours(e, c) - vanilla(e, c)) * lit(e, c)
    //
    // so a cell vanilla already lit to our model's own value gets nothing and a cell vanilla left
    // dark gets all of it. Self-limiting rather than tuned; there is no strength to pick.
    //
    // ISSUE #151, WITH THE OPERATOR IT WAS MISSING. #151 built max(vanilla, ours) as a whole-frame
    // composition, measured it as near-degenerate and closed on the grounds that a max can never
    // carve a shadow. Both halves of that are right and neither is a reason not to do this: the max
    // sets the LEVEL and the mask's own subtraction carves the darkness, so the pair can do what
    // neither can. Where the max is degenerate — everywhere the two models see the same geometry —
    // it correctly contributes nothing, which is the property that makes it need no calibration.
    // See VectorLightLiftMath for the three places they do not see the same geometry, of which the
    // open door is the one worth having.
    //
    // NO SHADER, WHICH IS THE OTHER DIFFERENCE FROM #151. #151 composed in a fragment program and
    // so had to smuggle vanilla's glow in through a spare UV channel, which is what the custom
    // shader and the three binary bundles were for. The mask runs per cell in C# and is ALREADY
    // holding vanilla's per-emitter glow — GlowGridPerLight is what phase 3 is built on — so the
    // max is four lines of integer arithmetic next to the subtraction it composes with. It also
    // lands the lift BELOW the sky's multiply rather than above it, so DaylightScale is not needed
    // and a torch cannot outglow noon.
    //
    // TAKES PRECEDENCE OVER THE BEAM. Both are lifts on the same lit region and running them
    // together would light it twice, so VectorLightOverlay stands down when this is on whatever
    // VectorLightMaskBeam says. That makes the two directly comparable in one boot rather than
    // additive.
    //
    // Inert unless VectorLightMask is on. OFF, pending the measurement — this is a bake-off arm
    // against the shipped flat beam and the honest outcome is whichever frame reads better.
    public static bool VectorLightMaskMax = false;

    // Feature key for VectorLightMaskMaxLift.
    public const string VectorLightMaskMaxLiftKey = "vector_light_mask_max_lift";

    // THE CONTROL ARM, and it is an instrument rather than a taste knob. With this off the mask
    // still walks every cell of every emitter's disc under the max's relaxed skips, still resolves
    // the emitter, still evaluates our falloff, still projects it and still goes through
    // VectorLightMask.Compose — and then delivers a lift of zero. So the frame it renders must be
    // the mask-alone frame, to the byte.
    //
    // WHAT IT SEPARATES. Phase 5 changed three things at once: it added the max, it relaxed two
    // early-outs that used to skip fully lit cells and unshadowed emitters, and it replaced the
    // mask's Subtract with a Compose that clamps once at the end instead of once per term. Only the
    // first is the feature. Without this arm a difference in the frame is consistent with any of
    // the three, and #151 records what that costs: its own first control run measured a masked ΔE
    // of 5.58 that had nothing to do with the composition and would have been "fixed" by retuning
    // arithmetic that was already correct.
    //
    // ON. Inert unless VectorLightMaskMax is on, so it cannot contaminate any other arm.
    public static bool VectorLightMaskMaxLift = true;

    // Feature key for VectorLightMaskMaxSeed.
    public const string VectorLightMaskMaxSeedKey = "vector_light_mask_max_seed";

    // Whether the max compares like with like: our straight line evaluated at Euclidean distance
    // PLUS ONE, matching the intDist = 100 that ComputeGlowGridsJob.PrepareFill seeds the light's
    // own cell at and therefore carries into every cell of the flood.
    //
    // THIS IS THE DIFFERENCE BETWEEN A GEOMETRY CORRECTION AND A BRIGHTNESS RESCALE, which is why
    // it is a flag and not a constant. With the seed matched, the max wins only where the flood's
    // path was genuinely longer than the line — the open door, the octile residue, the last cell of
    // the rim — and correctly finds nothing on a clear cardinal run. Drop it and our curve at d is
    // compared against vanilla's at d + 1, so the max wins in EVERY cell of EVERY lamp: measured
    // offline at 76 levels of glow one cell out from a radius-12 lamp, 23 at two cells, 13 at four.
    // That is a halo around every light on the map, and §27's standing rule is that it changes where
    // light reaches and not how bright a lamp is.
    //
    // ON. The unmatched arm exists to be shot beside this one so the choice is evidence rather than
    // an assertion in a comment; it is not a taste knob and should not become one.
    public static bool VectorLightMaskMaxSeed = true;

    // Feature key for VectorLightMaskSaturation.
    public const string VectorLightMaskSaturationKey = "vector_light_mask_saturation";

    // §27 phase 5b (epic #174 phase 5): the mask's edit is applied to vanilla's raw SUM and projected
    // once, instead of being subtracted from the projected byte.
    //
    // WHAT IT FIXES, and it is a direction rather than a level. Ring lamps around a free-standing
    // wall column and the shadows behind the column get DEEPER as lamps are added. Every lamp you
    // add fills in part of the region the others cannot see, so the physical answer runs the other
    // way: more lamps, shallower shadows, always.
    //
    // WHY IT HAPPENS. Vanilla sums its emitters into a ColorInt and then calls
    // ColorInt.ProjectToColor32Fast, which over 255 rescales all three channels by 255/max — the
    // glow grid SATURATES. The mask subtracts each blocked emitter's raw `own` out of that saturated
    // byte, so once a cell is over the ceiling the subtraction keeps its full strength while the
    // thing it is taken from has stopped growing. Six lamps at 150 leaves the cell displaying 255 and
    // the mask taking 300 off it; add a seventh the column also blocks and the same cell darkens
    // again. Below the ceiling the two spaces are identical and none of this is reachable, which is
    // why §27 shipped for this long without it showing up outside a heavily lit room.
    //
    // WHAT IT DOES INSTEAD. VectorLightSaturationMath re-applies the accumulated shadow and lift to
    // the raw sum, projects that, and hands the mask the difference against what vanilla displayed.
    // The composition becomes proj(R - shadow + lift) rather than proj(R) - shadow + lift, which is
    // monotone in emitter count by construction: adding an emitter adds a non-negative amount to R'
    // whatever our geometry says about it, and vanilla — the oracle here, since its flood is
    // unambiguously monotone — is matched rather than approximated.
    //
    // CONFINED TO SATURATED CELLS ON PURPOSE. Where the raw sum is under 255 the correction is
    // provably the identity, so VectorLightMask tests the sum and leaves the accumulators untouched
    // rather than running arithmetic that cannot change anything. The shipped shadow in an ordinary
    // one-lamp room is therefore byte-identical with this on, and the ONLY frames that move are the
    // ones that were wrong.
    //
    // ON, and it is a correctness fix rather than a bake-off arm — but it keeps a flag because the
    // off arm is what makes the monotonicity sweep an A/B rather than an assertion, and because it
    // costs a second walk over the section's emitters to learn the raw sum vanilla projected.
    // Inert unless VectorLightMask is on.
    public static bool VectorLightMaskSaturation = true;

    public const string VectorLightPawnShadowsKey = "vector_light_pawn_shadows";

    // §27 phase 4: a pawn throws a shadow away from each lamp that lights it.
    //
    // VANILLA CANNOT DO THIS AND IT IS NOT A GAP IN VANILLA. Its pawn shadow leans on `_CastVect`, a
    // shader global the sky manager sets once a frame, so every shadow on the map points the same
    // way. That is exactly right for a sun and meaningless for a torch, and per-lamp direction is
    // unreachable through that material — see VectorLightPawnShadows for how the extrusion is baked
    // into the mesh instead.
    //
    // REQUIRES THE MASK, and not merely for tidiness. A pawn behind a wall must not throw a shadow
    // from a lamp that cannot see it, and phase 3's coverage grid is the only thing in the mod that
    // can say so — the crossfade knows one global constant, and vanilla's own glow grid would answer
    // yes, because its light bends around corners.
    //
    // ROOFS AND EAVES ARE NOT SKIPPED, deliberately and against vanilla's own rule: Graphic_Shadow
    // bails on any roofed cell because sunlight does not get in, and a lamp indoors is the entire
    // point of this. §15's eaves are a sun concept too and have no bearing on a torch.
    //
    // On by default and separately switchable, because it is the one part of §27 that draws a
    // new OBJECT rather than changing the colour of an existing one — the same reasoning that
    // gives §25's visible clouds their own switch rather than riding the master.
    public static bool VectorLightPawnShadows = true;

    // Feature key for VectorLightShadowShares.
    public const string VectorLightShadowSharesKey = "vector_light_shadow_shares";

    // §27 phase 4b: each lamp's shadow is that lamp's SHARE of the light on the pawn, not the whole
    // of it.
    //
    // WHAT IT FIXES. Phase 4 charged every lamp the full darkening, as though each were the only
    // light in the room. One lamp is the case that is true for, and it is the case it was calibrated
    // and captured against — but a colony lights a room with four or six, and the shadows then
    // stacked instead of sharing: eight lamps put eight full-strength arms through one pawn and left
    // the ground at their feet 94% black. The complaint is that a well-lit pawn looks WORSE than a
    // dimly lit one, which is precisely backwards.
    //
    // WHY IT IS PHYSICS RATHER THAN A FUDGE FACTOR. Illuminance adds, so blocking one of N lights
    // removes that light's share of the total and no more — see VectorLightMath.PawnShadowShare.
    // Dividing by the total is not a strength knob turned down; it is the denominator that was
    // missing, and it is why the answer gets it right at both ends without a second constant: one
    // lamp still draws at full strength, and adding lamps makes each shadow fainter while the light
    // in the room goes up.
    //
    // ON BY DEFAULT, and separately switchable so the live A/B has a control arm in one boot. The
    // off arm is a genuine pre-feature baseline rather than an absence: with the flag down the
    // denominator is pinned to FullIlluminance and the arithmetic is bit-for-bit phase 4's.
    public static bool VectorLightShadowShares = true;

    // Feature key for VectorLightShadowGroundShares.
    public const string VectorLightShadowGroundSharesKey = "vector_light_shadow_ground_shares";

    // §27: a lamp's share is taken against the light on the ground the shadow falls on, not against
    // the light on the pawn.
    //
    // WHAT IT FIXES, which is the half of the share model above that stayed wrong for three
    // releases. The denominator was sampled at the caster's own cell, so it counted lamps that light
    // the PAWN rather than lamps that light the cells the shadow covers — and those are different
    // cells, up to a full shadow length away. A colonist beside a wall corner had their shadows
    // thinned by a lamp in the next room that reached them but not their shadow, and a shadow thrown
    // across a bright aisle stayed as dark as one thrown into a cupboard. See
    // VectorLightMath.ShadowGroundTotal for why the blocked lamp's own term stays measured at the
    // pawn while every other lamp's moves to the ground.
    //
    // SEPARATE FROM VectorLightShadowShares RATHER THAN FOLDED INTO IT, because the two answer
    // different questions and an A/B has to be able to tell them apart. With shares off there is no
    // denominator to place, so this flag does nothing; with shares on and this off, the denominator
    // is the phase-4b one and the arm reproduces the shipped frame exactly. That gives three arms in
    // one boot — no shares, shares at the pawn, shares on the ground — which is what makes it
    // possible to attribute a pixel to this change rather than to the model it refines.
    public static bool VectorLightShadowGroundShares = true;

    // Feature key for VectorLightShadowShape.
    public const string VectorLightShadowShapeKey = "vector_light_shadow_shape";

    // §27: lamp shadows the length and shape of the ones the game already draws.
    //
    // TWO CHANGES THAT ARE ONE DECISION, which is why they share a flag. Phase 4's shadow ran the
    // full distance to the lamp — a colonist four cells from a torch threw four cells of shadow,
    // longer than anything vanilla draws — and it tapered to 32% at the tip. Both came from the same
    // place: nothing had compared the result against a sun shadow standing beside it.
    //
    // The length is now a third of that, from vanilla's own numbers where possible. `ShadowData
    // .BaseY` is the tallness the game's own shadow shader uses, 0.8 for a human, and §27 had
    // invented 1.2 while reading BaseX and BaseZ out of the same struct. The lamp moved from 2.4 to
    // 3.2, which is the taste half and is stated as such.
    //
    // The shape is now a constant-width rectangle, because that is what a sun shadow IS —
    // MeshMakerShadows extrudes each footprint edge at full width and tapers nothing. The taper was
    // added when these were six cells long and a full-width quad read as a plank; shortening them
    // removed the premise, and matching vanilla's blocky silhouette is what makes a lamp shadow and
    // a sun shadow on one pawn read as two shadows rather than two effects.
    //
    // Off restores phase 4b's geometry exactly — the old heights, the old cap and the taper — so the
    // A/B's control arm is the previous look rather than no shadows at all.
    public static bool VectorLightShadowShape = true;

    // Feature key for VectorLightShadowClip.
    public const string VectorLightShadowClipKey = "vector_light_shadow_clip";

    // §27, issue #166: a lamp shadow stops at the wall instead of crossing it.
    //
    // Phase 4 asked whether the lamp could see the PAWN and never what the shadow crossed, so a pawn
    // beside a wall threw its shadow over the wall into the next room — onto ground that lamp never
    // reached, and which the room's own lamp is lighting, so it lands somewhere a player is looking.
    //
    // The shadow runs directly away from the lamp, so it lies along a radial of that lamp's
    // visibility polygon, and phase 3 already bakes that polygon: one boundary query per shadow
    // clamps the tip. It also clips at the lamp's own rim, which is the same statement rather than
    // an extra one — a shadow exists only inside the region the lamp lights.
    public static bool VectorLightShadowClip = true;

    // Feature key for VectorLightShadowFeather.
    public const string VectorLightShadowFeatherKey = "vector_light_shadow_feather";

    // Vector lighting, pawn shadows: a lamp shadow dissolves toward its tip the way a sun shadow does.
    //
    // WHAT WAS LEFT AFTER THE SHAPE MATCHED. The blocky-silhouette work above made a lamp shadow the
    // same *outline* as a sun shadow and it still read as a different kind of object. The reason is
    // opacity, and it is measurable: binned along its own length and normalised to its value at the
    // caster, vanilla's sun shadow runs 1.000 → 0.709 → 0.568 → 0.471 → 0.396 while ours was flat to
    // within ±4% end to end. A shadow that stops on a hard line does not look like a shadow.
    //
    // HOW, given the obvious route is closed. Vanilla gets its fade from the vertex-colour channel
    // `Custom/Sun shadow fade` already spends on extrusion, and that material is unusable here for
    // the two reasons VectorLightPawnShadows' header records. It is also NOT, as was assumed for a
    // while, a texture feathering the shadow: that shader declares one property, `_Color`, samples no
    // texture, and `MeshMakerShadows` gives the mesh no UVs to sample one with.
    //
    // So the fade rides a ramp texture of our own instead — one texel row, alpha falling along the
    // extrusion, on UVs we bake ourselves, through `Map/Transparent`. Off keeps the flat solid-colour
    // material exactly as it shipped, so the control arm is the previous look rather than an absence.
    //
    // ON BY DEFAULT. This makes a shadow fainter over most of its length and never darker, so the
    // conservative direction and the correct one agree for once; and pawn shadows only render at all
    // once vector lighting itself is on.
    public static bool VectorLightShadowFeather = true;

    // Feature key for VectorLightOpenDoors.
    public const string VectorLightOpenDoorsKey = "vector_light_open_doors";

    // §27e: an OPEN door stops occluding §27's rays, so light spills through a doorway a pawn is
    // standing in. Shut doors are untouched, and so is every wall.
    //
    // IT USED TO BE A DELIBERATE DISAGREEMENT WITH GAMEPLAY LIGHT, and it is no longer one.
    // RimWorld's glow grid never learns a door opened: Building.SpawnSetup writes def.blockLight into
    // lightBlockers once at spawn, and Building_Door.DoorOpen touches the grid not at all. So on its
    // own this flag drew a beam through an open door that vanilla did not deliver — GroundGlowAt read
    // dark there, plants did not grow, pawns could not see — and it shipped off for exactly that
    // reason: a beam-sized disagreement that blinks as pawns walk through is the most visible kind
    // there is, and issue #48 records the opposite sign of the same mistake.
    //
    // VectorLightDoorGlowBlocker below now closes that gap by moving vanilla's own bit once the door
    // is fully open, so the two halves agree and this is the RENDERING half of one coherent rule
    // rather than half a rule. The pair ships on together; turning this one off alone leaves vanilla
    // flooding through a doorway our polygon still treats as a wall, which is a third behaviour again
    // and is only useful as a measurement arm.
    public static bool VectorLightOpenDoors = true;

    // Feature key for VectorLightDoorGlowBlocker.
    public const string VectorLightDoorGlowBlockerKey = "vector_light_door_glow_blocker";

    // The COMPARISON ARM for the flag above, and the line the rest of the mod does not cross: instead
    // of drawing light vanilla does not deliver, make vanilla deliver it — call
    // GlowGrid.LightBlockerRemoved when a door opens and LightBlockerAdded when it shuts, so the glow
    // grid itself learns about open doors and gameplay light changes to match.
    //
    // WHAT IT IS FOR, AND IT IS NOT BRIGHTNESS. A bare one-cell GAP in a wall and an open DOOR one
    // cell away are the same aperture, and vanilla renders them from completely different inputs: it
    // floods straight through the gap and delivers nothing at all through the door. Every attempt to
    // reconcile the two by changing what WE draw has failed — most recently a visibility floor that
    // reached parity at the gap and drew a ring around every lamp on the map. The disagreement is not
    // in our model; it is that vanilla is lighting one of the two and not the other. Fixing it at the
    // source lets one composition serve both openings and needs no special case anywhere downstream.
    //
    // FULLY OPEN, NOT OPENING: see DoorApertureMath.GlowGridHoleWanted for why the bit moves at the
    // END of the swing and comes back on the FIRST tick of a close, and Map.FinalizeInit's patch for
    // the door that was left open across a save.
    //
    // THIS IS GAMEPLAY LIGHT, and it is the only term in §27 that is. Plant growth, work speed, pawn
    // vision and every mod reading GroundGlowAt move with it. It keeps its own flag for that reason
    // rather than riding on VectorLights: somebody who wants the rendering without the rule change
    // has to be able to say so. What it does is make RimWorld treat a hole as a hole — the answer it
    // already gives for a gap — rather than inventing a rule of our own.
    //
    // Wants vector_light_open_doors on beside it. On its own, vanilla floods through a doorway our
    // polygon still treats as a wall: a coherent thing to measure, not a coherent thing to ship.
    public static bool VectorLightDoorGlowBlocker = true;

    // Feature key for VectorLightDoorDirtySuppress.
    public const string VectorLightDoorDirtySuppressKey = "vector_light_door_dirty_suppress";

    // Stop the glow-blocker write above flagging sections for redraw, while leaving everything it
    // does to gameplay light exactly as it is.
    //
    // WHAT IT IS FOR. The write is what makes vanilla's flood arrive through an open door, and it is
    // also the most expensive thing this mod does: measured on the door storm, it is the difference
    // between 7.4 and 1.9 lighting-overlay regenerates a frame and between 15.8 and 7.6 ms of mod per
    // frame, at roughly 1.2 ms of mask per regenerate. But the regenerates are a side effect of
    // GlowGrid.DirtyCell being a blunt instrument — it names one cell and lets MapMeshDirty fan it out
    // — and the sections that genuinely need to look different are already flagged, precisely, from
    // the coverage delta.
    //
    // WHY IT IS NOT SIMPLY THE GLOW BLOCKER TURNED OFF. That switch gives up vanilla's wash and
    // reverts a gameplay-light rule; this keeps both and declines only the redraw. The two are
    // genuinely different offers and both are worth being able to make.
    //
    // OFF BY DEFAULT, because it can be wrong in a way nothing else here notices. Vanilla's flood is
    // geodesic, so a cell lit only by a path bending around a corner beyond the door has its glow
    // changed while our straight-line coverage never moved — and that section then holds a stale
    // overlay with no exception, no log line and no other probe moving. GlowDirtyScope.SuppressedSections
    // is the witness for what was declined; the residue is dim, and dim is not the same as absent.
    //
    // OFF REPRODUCES TODAY EXACTLY: the scope still opens and closes around the write, the prefix
    // reads the flag first and returns true, and vanilla's MapMeshDirty runs unchanged.
    public static bool VectorLightDoorDirtySuppress = false;

    // Feature key for VectorLightDoorAperture.
    public const string VectorLightDoorApertureKey = "vector_light_door_aperture";

    // §27e phase 2: the beam TRACKS the door's slide instead of appearing at full width the instant
    // the door is declared open.
    //
    // WHY THE BOOLEAN WAS NOT ENOUGH. Building_Door.Open flips to true on the first tick of the
    // swing, while the leaves take tens of ticks to finish sliding. So phase 1 put a full-width beam
    // on screen over a door the player can still see closing — the aperture and the artwork
    // disagreeing for the whole animation, which is the most conspicuous moment there is to disagree.
    //
    // HOW. DoorApertureMath places the two leaves along the wall axis from OpenPct, and
    // VectorLightBlockers hands them to Build as ordinary segments beside the silhouette. Build
    // already fires a corner ray at every segment endpoint, so the beam narrows to exactly the gap
    // and the penumbra follows the leaf edges without any new concept.
    //
    // THE COST, AND THE KNOB THAT BOUNDS IT. OpenPct changes every tick, and every distinct value is
    // a fresh bake for the lights near that door — tens per swing where §27's cost model assumed
    // geometry changes when a player builds something. DoorApertureMath.Quantise snaps it to eight
    // steps, which caps the bakes per swing regardless of door speed or game speed and is finer than
    // a sub-second animation reads. That knob is the whole reason this is affordable; see DESIGN.md
    // §27e phase 2 for the filmed comparison and the measured bake counts.
    //
    // Requires vector_light_open_doors. Off with that flag on reproduces phase 1 exactly — a beam
    // that pops — which is what makes the two filmable against each other.
    //
    // ON BY DEFAULT, so that the three code defaults agree with what the one player-facing switch
    // produces. That matters beyond tidiness: a fresh install has no settings file, so ApplyTo never
    // runs and these initialisers ARE the shipped configuration. Left false here, a new install would
    // get the beam popping to full width at the first tick of a swing while vanilla's own bit does
    // not move until the last — the two halves disagreeing for the whole animation, which is the one
    // arrangement this pair exists to avoid.
    public static bool VectorLightDoorAperture = true;

    // Feature key for VectorLightStalePolygon.
    public const string VectorLightStalePolygonKey = "vector_light_stale_polygon";

    // An emitter whose polygon is dirty is baked from its PREVIOUS polygon instead of being dropped
    // from the section.
    //
    // THE BUG THIS CLOSES. Vanilla's Map.MapUpdate regenerates dirty sections at line 1173 and does
    // not reach GameConditionManagerDraw -- where EnsurePolygons rebuilds polygons -- until 1178. So
    // anything that both marks a polygon dirty and dirties a section inside one tick is baked before
    // the rebuild, every time, by construction. Door swings do exactly that, so it fired constantly
    // in ordinary play. Dropping the emitter there does not just lose its shadow: VectorLightMask.
    // Apply returns true having collected nothing, so the section also keeps vanilla's flood with no
    // suppression on it, and the room reads a frame BRIGHTER than its settled state before snapping
    // back -- the flicker the report was about.
    //
    // OFF REPRODUCES THE DEFECT EXACTLY rather than approximately, which is the point of it being a
    // flag at all: the transient is one frame wide and cannot be photographed reliably, so the only
    // way to show it is a probe reading the same cell in both arms of one boot.
    public static bool VectorLightStalePolygon = true;

    // Feature key for VectorLightSectionDirty.
    public const string VectorLightSectionDirtyKey = "vector_light_section_dirty";

    // Issue #188 item A: after rebuilding polygons the draw dirties only the sections those emitters
    // can change, instead of the whole map.
    //
    // WHAT WAS WRONG. Patch_VectorLightDraw must re-dirty after a build, because a section that baked
    // while a polygon was still dirty skipped that emitter and nothing would ever ask it again. It
    // did so with WholeMapChanged — a call that says "something, somewhere" and costs every section
    // under the camera. Fine for a player building a wall; not fine for what actually provokes it,
    // which is door aperture tracking invalidating nine times per swing while a pawn walks through a
    // door on the other side of the colony.
    //
    // WHY IT IS A FLAG AT ALL when it changes no pixel. Because it changes no pixel: with the old
    // behaviour unreachable there is nothing to measure the new one against, and the counters this
    // ships with would have no baseline in the same boot. Off passes SectionDirtyMath.WholeMap and
    // calls WholeMapChanged, reproducing the previous behaviour exactly rather than approximately —
    // which is what makes the A/B a baseline instead of a picture of the feature being absent.
    //
    // Inert unless vector_lights and the mask are on, which still ship OFF.
    public static bool VectorLightSectionDirty = true;

    // Feature key for VectorLightChangedDirty.
    public const string VectorLightChangedDirtyKey = "vector_light_changed_dirty";

    // Dirty the sections a rebuilt emitter actually CHANGED, rather than every section it reaches.
    //
    // WHAT IS LEFT OVER FROM THE PER-SECTION DIRTY ABOVE. That one stopped a door swing regenerating
    // the whole viewport, and replaced it with the union of the rebuilt emitters' reaches — which is
    // still every section within a lamp's radius of the door, for every lamp within a radius of it.
    // Two thirds of those lamps are sealed away from the door by a wall and rebake to a coverage grid
    // that is byte-identical to the one they had; the third that do change change a wedge, not a
    // disc. Measured on the stress colony's own geometry, one swing dirties 146 sections that way and
    // 18 this way, and the door storm charges roughly a millisecond of mask to each of them.
    //
    // THE COMPARISON IS THE COVERAGE GRID, NOT THE POLYGON. See VectorLightMath.CoverageDelta: the
    // polygon moves for reasons no pixel can see, and the grid is the only thing the mask reads.
    //
    // WHY IT IS SOUND TO DIRTY LESS, which is the direction that goes quietly wrong in this
    // subsystem. A section that baked against the emitter's PREVIOUS shape is already showing the
    // right answer when the new shape is identical to it — that is exactly what the comparison
    // establishes. It rests on vector_light_stale_polygon, which is what makes a section bake against
    // the previous shape rather than skip the emitter; with that off a section really did render
    // without the emitter in between, so this stands down and dirties the whole reach. That pair is a
    // combination to keep in step rather than an orthogonal one, the same way the view cull is.
    //
    // Measured by vector_light_section_dirties beside vector_light_bakes: the ratio is the number
    // this moves, and the bake count standing still is what says the saving came from dirtying less
    // rather than from baking less. Off unions the whole reach, which is what the previous shape did,
    // so the arm is a baseline rather than a picture of the feature missing.
    public static bool VectorLightChangedDirty = true;

    // Feature key for VectorLightViewCull.
    public const string VectorLightViewCullKey = "vector_light_view_cull";

    // Issue #188 item B: a dirty polygon out of camera range is left dirty until it comes back.
    //
    // The draw has culled its own emitters against the view since phase 1 — VectorLightOverlay.
    // DrawLight bails on anything off screen — but EnsurePolygons never did, so scrolling away from
    // a lamp stopped it being DRAWN and not being BUILT. A door in an unwatched corridor therefore
    // paid full price nine times a swing to produce a polygon nothing would read.
    //
    // WHY IT IS SAFE, AND ONLY BECAUSE OF vector_light_section_dirty. Deferring a build leaves a
    // section that would have baked against a polygon baking without it, which is the documented
    // failure this subsystem has hit before. It is safe here because whoever eventually builds the
    // polygon also dirties the sections it reaches, so the emitter's sections rebake on the frame it
    // scrolls into range. Turning the section-dirty flag off while this one is on is therefore a
    // combination to avoid rather than an orthogonal pair; the scenario sweeps them in the order
    // that keeps each arm meaningful.
    //
    // Measured by vector_light_bake_deferrals beside vector_light_bakes. Off builds every dirty
    // polygon on the map exactly as before.
    public static bool VectorLightViewCull = true;

    // Feature key for VectorLightParallelBake.
    public const string VectorLightParallelBakeKey = "vector_light_parallel_bake";

    // Hand a frame's visibility polygons out across threads instead of baking them one after another.
    //
    // WHAT IS SAFE TO THREAD AND WHY. VectorLightField.BakeSelected splits the bake in two: reading
    // the map for each emitter's silhouette stays on the calling thread, and everything after it is
    // arithmetic over a Segment[] that writes only to the entry it was handed. The frames this
    // rescues are the ones that bake a whole room's worth of lamps at once — a map load, a wall going
    // up in a lit building, a whole-map rebake — which is where the dropped frame is. The steady
    // state bakes nothing at all, and a batch below the threshold does not fan out.
    //
    // WHY IT IS SOUND TO DO THIS AT ALL, which is a property of the CALLER rather than of the code.
    // RimWorld ticks and draws on one thread, and this runs inside the draw. While the join is
    // outstanding the main thread is blocked inside it and therefore not ticking, so no door opens
    // and no wall moves under a worker. Anything that later calls EnsurePolygons from somewhere else
    // has to re-establish that, and it is the reason this is a flag rather than an unconditional
    // change.
    //
    // Measured by vector_light_parallel_bakes and vector_light_bake_batch_max, which have to be read
    // together: a fan-out count says the path was taken and the batch size says whether it was ever
    // handed enough work to be worth taking. Off bakes the same batch in the same order on the
    // calling thread, which is what the previous shape did, so the arm is a baseline rather than a
    // picture of the feature missing.
    public static bool VectorLightParallelBake = true;

    // Feature key for VectorLightSilhouetteCache.
    public const string VectorLightSilhouetteCacheKey = "vector_light_silhouette_cache";

    // Issue #188 item C: hold a light's whole-cell occluder silhouette across a door swing instead of
    // rescanning its window nine times to rebuild the same wall.
    //
    // WHAT IS STATIC AND WHAT MOVES. The aperture is quantised into eight steps, so one swing dirties
    // every light that can see the door nine times. Eight of those nine differ only in where the two
    // door LEAVES are — sub-cell segments that ride alongside the silhouette rather than through it —
    // while the whole-cell grid moves exactly once, between the shut step and the first open one.
    // VectorLightBlockers records the silhouette and the doors inside it; a later bake re-reads only
    // those doors, and reuses the silhouette when none of them has changed which side of the
    // whole-cell question it is on.
    //
    // WHY IT IS SAFE TO HOLD, and it is the same argument the invalidation is built on: the static
    // half moves only when a light blocker is built or removed, and vanilla says so at the moment it
    // happens through GlowGrid.LightBlockerAdded/Removed. Those are already patched, so the memo is
    // invalidated by the same write that dirties the polygon. A door SLIDING fires neither, which is
    // both why the swing is cheap here and why MarkGeometryDirtyAround had to grow a parameter
    // saying which of the two kinds of change it is carrying.
    //
    // Measured by vector_light_silhouette_hits beside vector_light_silhouette_rebuilds — a hit count
    // alone cannot tell a working memo from a scene where nothing ever asks twice. Off rescans the
    // window on every bake, which is what the previous shape did, so the arm is a baseline rather
    // than a picture of the feature missing.
    public static bool VectorLightSilhouetteCache = true;

    // Feature key for VectorLightGlowTextureHold.
    public const string VectorLightGlowTextureHoldKey = "vector_light_glow_texture_hold";

    // Keep an emitter's copy of vanilla's glow when only OUR geometry moved, instead of refilling
    // and re-uploading a texture that is byte-for-byte what it already was.
    //
    // WHY THE TWO COME APART. The per-emitter field is vanilla's own delivered glow over that
    // emitter's square, and the fragment program indexes it through UV1 — where each vertex sits in
    // the square. A rebuild clears UV1, because Mesh.Clear wipes every channel, so the coordinates
    // always have to be written again. The TEXTURE only goes stale when vanilla's glow moves, and
    // the commonest thing that provokes a rebuild does not move it: a door slides through eight
    // quantisation steps and RimWorld's glow grid is never told a door opened.
    //
    // AND WE DO NOT TELL IT, which is the contract this rests on rather than an accident.
    // CelestialLighting writes the glow grid in exactly two places, both behind
    // vector_light_door_glow_blocker, which ships off — so with the shipped flags a door swing leaves
    // gameplay light untouched, and the texture with it. Under that flag the write goes through
    // vanilla's LightBlockerAdded/Removed, which we postfix, so it arrives back as a real
    // invalidation and the texture is correctly refilled. Nothing special-cases it.
    //
    // Measured by vector_light_field_texture_uploads beside vector_light_field_uv_only_uploads, read
    // as a pair for the usual reason, and by vector_light_upload_field_ms which is the half of the
    // upload clock this moves. Off refills on every refresh, which is what the previous shape did.
    public static bool VectorLightGlowTextureHold = true;
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
