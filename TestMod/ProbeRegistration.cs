using RimWorld;
using RimWorldTestHarness.Mod.Features;
using RimWorldTestHarness.Mod.Probes;
using RimWorldTestHarness.Shared;
using Verse;

namespace CelestialLighting.Probes;

// The one place the harness and the shipped mod are bridged. This dev-only assembly is the only one
// that references BOTH RimWorldTestHarness and CelestialLighting, so it is where probes get exposed
// to the harness's Probe step and feature flags get exposed to its SetFeature step. Neither the
// shipped CelestialLighting.dll nor RimWorldTestHarness.dll references the other — see
// RimWorldTestHarness/DESIGN.md's "Where probe tests live".
[StaticConstructorOnStartup]
public static class ProbeRegistration
{
    static ProbeRegistration()
    {
        ProbeRegistry.Register(new ShadowLeanProbe());
        ProbeRegistry.Register(new AxialTiltDeclinationProbe());
        ProbeRegistry.Register(new AxialTiltActiveProbe());
        ProbeRegistry.Register(new AxialTiltLunarProbe());
        ProbeRegistry.Register(new PlanetsmithActiveProbe());
        // Reports the obliquity actually in force rather than Planetsmith's stored field, so it
        // follows the whole precedence chain: with RAT installed too this reads RAT's tilt while
        // planetsmith_active still reads 1, which is the intended outcome and the only way a scenario
        // can catch that precedence silently inverting.
        ProbeRegistry.Register(new PlanetsmithTiltProbe());
        // No realistic_planets_tilt to go with this: planetsmith_tilt already reports
        // AxialTiltCompat.ObliquityDegrees, which is the whole chain's answer rather than
        // Planetsmith's field, so on an RP2 world it reads RP2's tilt. A second probe reading the
        // same property under a different name would be one more thing to keep in step for no
        // additional coverage.
        ProbeRegistry.Register(new RealisticPlanetsActiveProbe());
        ProbeRegistry.Register(new MoonDeclinationProbe());
        ProbeRegistry.Register(new ShadowVectorXProbe());
        ProbeRegistry.Register(new CivilTwilightProbe());
        // The composed, vacuum-gated factor Patch_TwilightColor actually blends with. CivilTwilight
        // above reads only one (deliberately ungated) component of it, so it cannot see §18a's
        // suppression — on an orbital map it reports a pulse the renderer is not applying.
        ProbeRegistry.Register(new TwilightWarmthProbe());
        ProbeRegistry.Register(new PenumbraProbe());
        ProbeRegistry.Register(new MoonIlluminationProbe());
        ProbeRegistry.Register(new NightRadianceProbe());
        ProbeRegistry.Register(new PurkinjeProbe());
        // §9's applied strength, added after two versions shipped whose rod-vision factor was right
        // and whose effect on screen was nil. PurkinjeProbe says how dark the sky is; this says how
        // much desaturation that actually turned into.
        ProbeRegistry.Register(new NightDesaturationProbe());
        ProbeRegistry.Register(new SkyColorTemperatureProbe());
        // §20d. sky_color_temperature above reports the CLEAN-AIR half of the curve and, since the
        // aerosol's colour left the Planckian locus, no longer moves with pollution at all. These two
        // are the aerosol half stated honestly: the exponent the tile's rainfall resolves to, and the
        // red/blue ratio of the colour actually being blended in. The pairing is the point — the
        // headline claim of the subsystem is that a low-exponent tile can carry a full aerosol load
        // and still show an unshifted sky, and only reading both numbers together can show that.
        ProbeRegistry.Register(new AerosolAngstromProbe());
        ProbeRegistry.Register(new SkyRedBlueRatioProbe());
        // §20e (issue #92). The raw column both probes above are downstream of — pollution and the
        // rainfall-keyed background, already summed and already drifted. Before §20e this read exactly
        // 0 on any pollution-0 tile; pairing it with sky_red_blue_ratio on a clean tile is what proves
        // the headline fix landed rather than only the colour it happens to produce this frame.
        ProbeRegistry.Register(new AerosolLoadProbe());

        // §19. The band strength is the subsystem's thesis in one number: hold latitude 78 in
        // midwinter and it reads ~1.0 at every hour of the day, while latitude 0 gets a ~25-minute
        // window at dusk out of the same latitude-free curve. overlay_brightness is what proves the
        // floor arm does anything — it is the only probe that measures what the screen renders, as
        // opposed to sky_glow, which is the gameplay brightness the floor deliberately never touches.
        ProbeRegistry.Register(new PolarNightBlueProbe());
        // §19c. Four metrics off one class (the LimbRefractionProbe pattern), because IProbe.Read
        // returns a single float and "is the sky purple" is an ordering between channels, not a
        // scalar. purple_hue_green is the primary signal: red and blue are pinned at 1 by the
        // balanced mix, so green below 1 is the entire green deficit.
        ProbeRegistry.Register(new PurpleLightProbe("purple_light", PurpleLightProbe.Metric.Window));
        ProbeRegistry.Register(new PurpleLightProbe("purple_hue_green", PurpleLightProbe.Metric.HueGreen));
        ProbeRegistry.Register(new PurpleLightProbe("purple_sky_red", PurpleLightProbe.Metric.SkyRed));
        ProbeRegistry.Register(new PurpleLightProbe("purple_sky_green", PurpleLightProbe.Metric.SkyGreen));
        ProbeRegistry.Register(new PurpleLightProbe("purple_sky_blue", PurpleLightProbe.Metric.SkyBlue));
        ProbeRegistry.Register(new OverlayBrightnessProbe());

        // §18d's limb-refraction ramp. Four series rather than one because this is a temporal
        // effect: the claim is that the platform holds full sun ~14 degrees past the ground's
        // sunset and then loses it over ~2.4 degrees, and no single scalar can show a band's width
        // or its position. limb_sun_elevation is the x-axis; the other three are what happens on it.
        // limb_tint_green rather than red because normalisation pins red at 1, so green falling from
        // ~1 to ~0.02 IS the reddening.
        ProbeRegistry.Register(new LimbRefractionProbe(
            "limb_sun_elevation", LimbRefractionProbe.Metric.SunElevation));
        ProbeRegistry.Register(new LimbRefractionProbe(
            "limb_sunlight_fraction", LimbRefractionProbe.Metric.SunlightFraction));
        ProbeRegistry.Register(new LimbRefractionProbe(
            "limb_tint_strength", LimbRefractionProbe.Metric.TintStrength));
        ProbeRegistry.Register(new LimbRefractionProbe(
            "limb_tint_green", LimbRefractionProbe.Metric.TintGreen));
        ProbeRegistry.Register(new AuroraTintProbe());
        // §11a's pair: aurora_curtain pins the ribbon overlay's alpha the same way aurora_tint pins the
        // flat tint's, and aurora_curtain_cost carries the one performance number the offline benchmarks
        // cannot supply — what a frame of field regeneration costs under RimWorld's own Mono runtime.
        ProbeRegistry.Register(new AuroraCurtainProbe());
        ProbeRegistry.Register(new AuroraCurtainCostProbe());
        // Issue #60. aurora_curtain_cost above answers "what does one bake slice cost"; this family
        // answers the question that one structurally cannot — how often each stage of the draw path
        // is entered per FRAME, and what the postfix costs on a frame that bakes nothing at all.
        // Registered as a family off one class because the Probe step reads a single float, and the
        // whole point here is to see the stages side by side.
        RegisterAuroraPathTiming();
        ProbeRegistry.Register(new EclipseCoverageProbe());
        // §18e: what the coverage ramp is aimed AT, as opposed to how far along it is.
        // Paired with night_radiance it is the whole vacuum-eclipse claim in two numbers —
        // equal in orbit (totality is night), different at sea level (vanilla's umbra is a flat 0).
        ProbeRegistry.Register(new EclipseUmbraProbe());
        ProbeRegistry.Register(new BloodMoonProbe());
        // §6a's two instruments. moon_shadow_render measures the composed shadow colour the shader
        // actually uses, which is the only reliable way to test a moon shadow — a screenshot A/B of a
        // night scene moves pixels by 1-3/255 and cannot be told apart from weather and pawn motion.
        // moon_elevation exists so a scenario can prove the moon was actually in the sky, since
        // moon_illumination reports phase alone and a full moon can be below the horizon.
        ProbeRegistry.Register(new MoonShadowRenderProbe());
        ProbeRegistry.Register(new MoonElevationProbe());
        ProbeRegistry.Register(new WeatherDimmingProbe());
        // §22. Independent of weather_dimming above: that fraction is 0 throughout Clear by
        // construction (WeatherDimmingMath classifies Clear at opacity 0 on both its axes — see
        // Patch_CloudCoverSky's header), and this is the axis that instead moves during Clear. Pair
        // with SetWeather("Clear") to read what Patch_CloudCoverSky and Patch_CloudCoverLabel are
        // actually acting on; read under a different weather to confirm the drift keeps moving
        // underneath even while nothing currently consumes it.
        ProbeRegistry.Register(new CloudCoverProbe());
        // Issue #100: §21's cavity gain, the same value NightRadiance.FloorGlowFor multiplies its
        // floor by. Pair with cloud_cover_fraction above under Clear weather to show the two moving
        // together — before the fix, this stayed flat at the ClearSkyAlbedo backscatter (~1.07 at
        // full snow) no matter what cloud_cover_fraction read.
        ProbeRegistry.Register(new SurfaceBuildupCavityGainProbe());
        // §23 (issue #88 option 1). Two metrics off one class, same reasoning as EaveCellProbe: the
        // headline claim is a comparison between a high deck and a low deck at the same elevation, and
        // a scenario needs cloud_underlight_altitude alongside the multiplier to confirm which deck it
        // actually measured rather than trusting the weather roll silently.
        ProbeRegistry.Register(
            new CloudUnderlightProbe("cloud_underlight", CloudUnderlightProbe.Metric.Multiplier));
        ProbeRegistry.Register(new CloudUnderlightProbe(
            "cloud_underlight_altitude", CloudUnderlightProbe.Metric.AltitudeMetres));
        // §23b (issue #88 option 2), the spatial lane. cloud_underlight_layer is what reaches the
        // material; cloud_underlight_cover says which of the two cloud sources was live (§13's deck
        // opacity or §22's Clear-weather fraction, which §23's own probes above cannot see at all);
        // and cloud_underlight_structure is the field's peak residual, the number that must go to zero
        // at BOTH a clear sky and a solid overcast.
        ProbeRegistry.Register(new CloudLayersProbe(
            "cloud_underlight_layer", CloudLayersProbe.Metric.Strength));
        ProbeRegistry.Register(new CloudLayersProbe(
            "cloud_underlight_cover", CloudLayersProbe.Metric.Fraction));
        ProbeRegistry.Register(new CloudLayersProbe(
            "cloud_underlight_structure", CloudLayersProbe.Metric.FieldPeak));
        // §23c and §25, the other two consumers of the same field. Registered next to §23b's because a
        // scenario reading one almost always wants the others in the same breath: the claim the three
        // make together is that they are one cloud deck seen three ways, and the cheapest way to show
        // a lane standing down is the other two carrying on.
        ProbeRegistry.Register(new CloudLayersProbe(
            "cloud_shadow_alpha", CloudLayersProbe.Metric.ShadowAlpha));
        ProbeRegistry.Register(new CloudLayersProbe(
            "cloud_sheet_alpha", CloudLayersProbe.Metric.SheetAlpha));
        // How much sheet is actually PLACED, as opposed to how opaque a placed one is. The pair is
        // what makes a cloud pop measurable: cloud_sheet_alpha is flat across a threshold crossing
        // because the lane's opacity never depended on the count, so only this one moves when a sheet
        // appears or vanishes — see CloudSheetMassProbe for why it sums alphas rather than counting.
        ProbeRegistry.Register(new CloudSheetMassProbe());
        // Whether the Clouds interop sees Clouds in the load order. Paired with the two alphas above
        // rather than left implicit: a zero alpha alone cannot tell "we stood down for Clouds" from
        // "Clouds never loaded" (CloudsActiveProbe's header).
        ProbeRegistry.Register(new CloudsActiveProbe());
        // §25b, the cloud varieties. The first two say how the sky is layered — the mixture's mean
        // altitude, which must reproduce §13's classifier exactly on a raining sky, and the cirrus
        // share, which is the thing a single classified altitude could never report.
        //
        // THE LAST TWO ARE THE SUBSYSTEM'S WHOLE CLAIM AND ONLY WORK AS A PAIR. §25b says the decks
        // go out from the bottom up, so at the right depression the low cloud reads 0 while the
        // cirrus above it still reads 1. Pin them together, and pin sun_elevation beside them: the
        // whole sequence is under four degrees of elevation wide, so a clock change that moved the
        // sample out of it would otherwise show as "the effect stopped working".
        ProbeRegistry.Register(new CloudLayersProbe(
            "cloud_deck_mean_altitude", CloudLayersProbe.Metric.DeckMeanAltitude));
        ProbeRegistry.Register(new CloudLayersProbe(
            "cloud_deck_high_share", CloudLayersProbe.Metric.HighDeckShare));
        ProbeRegistry.Register(new CloudLayersProbe(
            "cloud_deck_underlit_low", CloudLayersProbe.Metric.UnderlitLow));
        ProbeRegistry.Register(new CloudLayersProbe(
            "cloud_deck_underlit_high", CloudLayersProbe.Metric.UnderlitHigh));
        // §25c. Pin `cloud_volume_shader` at 1 in any scenario that claims to measure the raymarch:
        // every failure in its load path degrades to §25b's baked atlas on purpose, so without this
        // a run that never loaded the shader still produces a full, healthy-looking profiler table.
        ProbeRegistry.Register(new CloudVolumeShaderProbe(
            "cloud_volume_shader", CloudVolumeShaderProbe.Metric.Available));
        ProbeRegistry.Register(new CloudVolumeShaderProbe(
            "cloud_volume_bake_ms", CloudVolumeShaderProbe.Metric.BakeMilliseconds));
        ProbeRegistry.Register(new CloudVolumeShaderProbe(
            "cloud_volume_format", CloudVolumeShaderProbe.Metric.FormatSupported));
        ProbeRegistry.Register(new CloudVolumeShaderProbe(
            "cloud_volume_shader_loaded", CloudVolumeShaderProbe.Metric.ShaderLoaded));
        ProbeRegistry.Register(new CloudVolumeShaderProbe(
            "cloud_volume_upload_ms", CloudVolumeShaderProbe.Metric.UploadMilliseconds));
        ProbeRegistry.Register(new CloudVolumeShaderProbe(
            "cloud_volume_baked", CloudVolumeShaderProbe.Metric.BakeFinished));
        ProbeRegistry.Register(new CloudVolumeShaderProbe(
            "cloud_atlas_bake_ms", CloudVolumeShaderProbe.Metric.AtlasBakeMilliseconds));
        // How much cloud is inside the CAMERA, not merely on the map. Pin cloud_sheets_in_view beside
        // any capture or fill-rate window of the cloud lane: three §25c runs produced healthy tables
        // and a full set of A/B frames for a patch of sky that had no cloud over it, and nothing in
        // the report said so.
        ProbeRegistry.Register(new CloudSheetViewProbe(
            "cloud_sheets_placed", CloudSheetViewProbe.Metric.Placed));
        ProbeRegistry.Register(new CloudSheetViewProbe(
            "cloud_sheets_in_view", CloudSheetViewProbe.Metric.InView));
        ProbeRegistry.Register(new CloudSheetViewProbe(
            "cloud_view_coverage", CloudSheetViewProbe.Metric.ViewCoverage));
        // The three map-kind gates themselves, so a cavern scenario pins the DECISION and not just its
        // consequences — every gated effect also reads zero for unrelated reasons (wrong time of day,
        // no active condition), so the effect probes alone cannot say whether a gate actually fired.
        ProbeRegistry.Register(new MapEnclosedProbe());
        ProbeRegistry.Register(new MapDrawsShadowsProbe());
        // map_sky_blacked_out is the dynamic one (issue #35): unlike the other two it is not a function
        // of map.Biome, so a scenario has to start a real GameCondition to move it, and it is the only
        // place the eclipse carve-out is observable at all.
        ProbeRegistry.Register(new MapSkyBlackedOutProbe());
        // Raw gameplay glow, so the weather_dimming scenario can assert §13's central negative: the
        // sky visibly darkens under a storm while this value does not move at all.
        ProbeRegistry.Register(new SkyGlowProbe());
        // The composed sky's warm/cool axis, read off the material SkyManager actually tints the map
        // through. The only sky probe downstream of the map-kind gates — the other two recompute their
        // patch's input and so cannot see a gate fire at all (issue #35).
        ProbeRegistry.Register(new SkyOverlayWarmthProbe());
        // The same material's magnitude axis — how bright the composed overlay is, downstream of
        // BOTH the §17 blackout gate and §7a's MinNightBrightness floor. Anomaly's UnnaturalDarkness
        // scenario reads this under different presets to prove the floor no longer washes the event out.
        ProbeRegistry.Register(new SkyOverlayLuminanceProbe());
        // §14: one number that says whether vanilla's sky and our sun agree about day/night.
        ProbeRegistry.Register(new SunClockDisagreementProbe());
        ProbeRegistry.Register(new SunElevationProbe());
        // §15: how many cells on this map are eaves at all. Separates "the A/B images match because
        // the toggle did nothing" from "they match because this colony has no porch to shade".
        // Two counts, not one: the eave predicate and the shadow-caster predicate differ on a
        // mountain roof, and a scenario reading only the first cannot see a thick/constructed
        // roofline seam at all — the eave count is correct there and the caster count is the one
        // that was wrong. See EaveCellProbe and EavesMath.
        ProbeRegistry.Register(new EaveCellProbe("eave_cells", EaveCellProbe.Metric.Eaves));
        ProbeRegistry.Register(
            new EaveCellProbe("roof_shadow_cells", EaveCellProbe.Metric.ShadowCasters));
        // §27 vector lights. Four metrics off one class, and they are read together on purpose:
        // vector_light_shadow_fraction is the claim ("walls are blocking light"), and the other three
        // are what stop a zero in it being mistaken for a disproof — no emitters, no reach, or no mesh
        // each produce the same 0 for entirely different reasons.
        ProbeRegistry.Register(new VectorLightProbe("vector_light_count", VectorLightProbe.Metric.Count));
        ProbeRegistry.Register(new VectorLightProbe("vector_light_lit_area", VectorLightProbe.Metric.LitArea));
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_shadow_fraction", VectorLightProbe.Metric.ShadowFraction));
        ProbeRegistry.Register(new VectorLightProbe("vector_light_verts", VectorLightProbe.Metric.Vertices));
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_penumbra_area", VectorLightProbe.Metric.PenumbraArea));
        // §27 phase 3. Pin this at 1 in any arm claiming to measure the mask: the per-emitter glow
        // arrays are private fields read by reflection, and failing to read them is a defined
        // stand-down rather than an error, so an unpinned arm photographs the crossfade instead.
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_mask_available", VectorLightProbe.Metric.MaskAvailable));
        // Performance, measured through Circinus rather than Dubs, because Circinus reports CALL
        // COUNTS. §27 phase 3 does all its work inside a section regenerate, and the Dubs window that
        // appeared to show it running three times cheaper than the feature-off baseline had simply
        // not provoked a single regenerate — the patch was absent from the table rather than cheap.
        // circinus_regen_calls is the guard against measuring an idle window again: pin it above zero
        // in any arm that quotes a timing, or the timing is of nothing happening.
        //
        // The armed method is vanilla's Regenerate rather than our own postfix, so the row covers the
        // whole bake and the cost of the mask is the DIFFERENCE between the feature-on and
        // feature-off arms — the ratio-between-builds comparison Dubs' own report notes recommend.
        //
        // circinus_available and circinus_cycles are NOT registered here even though §27's own
        // scenarios were written against them. §28's sweep below registers both under the same two
        // names, and ProbeRegistry.Register is a dictionary write -- last registration wins, silently.
        // Registering them twice would mean §27's bake scenarios read §28's arm (SkyManagerUpdate)
        // while their comments claimed the overlay's, which is the kind of wrong number nothing
        // catches. §28's circinus_available is identical (the metric takes no target), and no §27
        // scenario pins circinus_cycles, so dropping both here costs nothing and removes the shadow.
        const string overlay = "Verse.SectionLayer_LightingOverlay";
        ProbeRegistry.Register(new CircinusProbe("circinus_regen_calls", CircinusProbe.Metric.Calls, overlay, "Regenerate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_regen_avg_ms", CircinusProbe.Metric.AvgMs, overlay, "Regenerate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_regen_max_ms", CircinusProbe.Metric.MaxMs, overlay, "Regenerate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_regen_total_ms", CircinusProbe.Metric.TotalMs, overlay, "Regenerate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_regen_max_calls", CircinusProbe.Metric.MaxCallsPerCycle, overlay, "Regenerate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_regen_patched", CircinusProbe.Metric.Patched, overlay, "Regenerate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_reset", CircinusProbe.Metric.Reset, overlay, "Regenerate"));
        // Issue #80: the fixed near-door cell in ambient_light_compat.json.
        // ambient_ground_glow is the GAMEPLAY value (what Ambient Light's own readout reports);
        // ambient_sky_fraction is what SkyFalloffSource resolves for it, for §7b to cap occlusion with.
        // Pairing them tells "their boost isn't real here" apart from "our passthrough mis-read it".
        ProbeRegistry.Register(
            new AmbientLightDoorCellProbe("ambient_ground_glow", AmbientLightDoorCellProbe.Metric.GroundGlow));
        ProbeRegistry.Register(
            new AmbientLightDoorCellProbe("ambient_sky_fraction", AmbientLightDoorCellProbe.Metric.SkyFraction));
        // §7b's baked lighting-overlay vertex alphas, for glass_wall_leak.json. Six probes because the
        // claim is a SHAPE, not a number: glass_corner moves; glass_centre and deep_corner say how far
        // in it reaches; the granite pair is the control separating "glass leaks" from "the whole room
        // got brighter". Offsets address that scenario's room and must move with it.
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "glass_corner", new IntVec3(0, 0, 41), SkyCoverVertexProbe.Metric.CornerAlpha));
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "glass_centre", new IntVec3(0, 0, 41), SkyCoverVertexProbe.Metric.CentreAlpha));
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "deep_corner", new IntVec3(0, 0, 43), SkyCoverVertexProbe.Metric.CornerAlpha));
        // z=50 rather than 49: a lattice point is the SOUTH-west corner of the cell it is named for, so
        // the corner touching the NORTH wall is indexed by the wall's own row. 49 would have addressed
        // a corner between two interior cells and passed for the wrong reason.
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "granite_corner", new IntVec3(0, 0, 50), SkyCoverVertexProbe.Metric.CornerAlpha));
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "granite_centre", new IntVec3(0, 0, 49), SkyCoverVertexProbe.Metric.CentreAlpha));
        // sky_falloff_redraw.json: the near-door corner of native_sky_falloff.json's own room --
        // same map address glass_corner reads (that room and this one are both built at offset
        // (0, 45) with the south gap at local (0, -5)), registered under its own name because this
        // scenario's room has no glass and reusing "glass_corner" here would misname what it reads.
        // This is the vertex GameComponent_SkyFalloffRedraw exists to keep in step with CurSkyGlow: a
        // corner probe rather than the fraction/depth probes above because SkyFalloffSource.FractionAt
        // is a pure function of live CurSkyGlow and so can never observe a stale BAKE -- only the mesh
        // byte Patch_IndoorSkyOcclusion actually wrote can.
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "redraw_corner", new IntVec3(0, 0, 41), SkyCoverVertexProbe.Metric.CornerAlpha));
        // thick_roof_wall.json (#129): whether a wall ring under a mountain roof is a boundary or is
        // swallowed into the room's blackout. Four centres and no corner, because the claim here is
        // about the cells the wall RING renders as, and a wall's centre is the mean of its own corners
        // — the one vertex that moves when the classification changes. thick_wall against
        // constructed_wall is the whole comparison: the constructed room is built identically and its
        // wall was never affected, so a run where BOTH moved means something other than this rule did
        // it. thick_outside is the second symptom, one cell of open ground north of the wall that
        // inherited darkness through the wall's outer lattice points; thick_floor is the control that
        // must NOT move, since the interior floor being black is working as designed.
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "thick_wall_centre", new IntVec3(-10, 0, 33), SkyCoverVertexProbe.Metric.CentreAlpha));
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "thick_outside_centre", new IntVec3(-10, 0, 34), SkyCoverVertexProbe.Metric.CentreAlpha));
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "thick_floor_centre", new IntVec3(-10, 0, 31), SkyCoverVertexProbe.Metric.CentreAlpha));
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "constructed_wall_centre", new IntVec3(10, 0, 33), SkyCoverVertexProbe.Metric.CentreAlpha));
        // indoor_glow_lamp.json: the lamp regression for the passthrough's subtraction. Two cells in a
        // sealed, roofed, lamp-lit room — beside the lamp and in the far corner it cannot reach (the
        // room is 25x25 precisely because glowRadius is 10) — each reporting all three terms of
        // `sky = max(0, ground - artificial)`. Beside the lamp, ground is HIGH and sky must still be 0;
        // a build that capped on total glow would show lamp_near_sky tracking lamp_near_ground, which
        // no single-cell probe could tell from working.
        ProbeRegistry.Register(new IndoorGlowCellProbe(
            "lamp_near_ground", new IntVec3(0, 0, 46), IndoorGlowCellProbe.Metric.GroundGlow));
        ProbeRegistry.Register(new IndoorGlowCellProbe(
            "lamp_near_artificial", new IntVec3(0, 0, 46), IndoorGlowCellProbe.Metric.ArtificialGlow));
        ProbeRegistry.Register(new IndoorGlowCellProbe(
            "lamp_near_sky", new IntVec3(0, 0, 46), IndoorGlowCellProbe.Metric.SkyFraction));
        ProbeRegistry.Register(new IndoorGlowCellProbe(
            "lamp_far_ground", new IntVec3(-11, 0, 34), IndoorGlowCellProbe.Metric.GroundGlow));
        ProbeRegistry.Register(new IndoorGlowCellProbe(
            "lamp_far_artificial", new IntVec3(-11, 0, 34), IndoorGlowCellProbe.Metric.ArtificialGlow));
        ProbeRegistry.Register(new IndoorGlowCellProbe(
            "lamp_far_sky", new IntVec3(-11, 0, 34), IndoorGlowCellProbe.Metric.SkyFraction));
        // Issue #124 / §7c: same near-door cell, native_sky_falloff.json's own copy of the same room
        // layout. native_falloff_depth is the raw BFS layer NativeSkyFalloffGrid computes;
        // native_falloff_fraction is what SkyFalloffSource actually dispatches to §7b's CapOcclusion.
        ProbeRegistry.Register(
            new NativeSkyFalloffProbe("native_falloff_depth", NativeSkyFalloffProbe.Metric.Depth));
        ProbeRegistry.Register(
            new NativeSkyFalloffProbe("native_falloff_fraction", NativeSkyFalloffProbe.Metric.Fraction));
        // §7d door_strength_leak.json: two rooms side by side, at (-13, 45) and (13, 45) rather than
        // native_sky_falloff.json's single room at (0, 45) -- neither reuses native_falloff_depth /
        // native_falloff_fraction above, since those are hardcoded to (0, 45), which this scenario
        // leaves as open exterior ground between the two rooms and would read as "no occlusion" for
        // the wrong reason. wood_door_* is room A (a plain wood Door, must match native_falloff_*'s
        // OWN pinned values exactly since DoorLeakMath's reference ratio is 1 for the reference door);
        // door_strength_* is room B (Odyssey's AncientBlastDoor, ratio 37.5). Same near-door local
        // offset for both, so the pair is directly comparable at identical depth/geometry.
        ProbeRegistry.Register(new NativeSkyFalloffProbe(
            "wood_door_depth", NativeSkyFalloffProbe.Metric.Depth, new IntVec3(-13, 0, 45)));
        ProbeRegistry.Register(new NativeSkyFalloffProbe(
            "wood_door_fraction", NativeSkyFalloffProbe.Metric.Fraction, new IntVec3(-13, 0, 45)));
        ProbeRegistry.Register(new NativeSkyFalloffProbe(
            "door_strength_depth", NativeSkyFalloffProbe.Metric.Depth, new IntVec3(13, 0, 45)));
        ProbeRegistry.Register(new NativeSkyFalloffProbe(
            "door_strength_fraction", NativeSkyFalloffProbe.Metric.Fraction, new IntVec3(13, 0, 45)));
        // glass_wall_leak2.json (§7c's IsWall blockLight gate, distinct from IndoorGlowPassthrough's own
        // glass_wall_leak.json): same two-room-side-by-side shape as door_strength_leak.json above, at
        // (-13, 65) and (13, 65) so it cannot collide with either scenario's rooms. Room A's south wall
        // is unbroken granite (wall_control_*) -- the BFS must never reach the near-wall interior cell
        // at all, since a solid wall is never a seed and is never crossed. Room B swaps the single wall
        // cell a door would otherwise occupy for VFEArch_CellWall itself (holdsRoof true, blockLight
        // false) instead (glass_wall_*) -- IsWall's own blockLight check is what lets the flood cross it
        // exactly like an open threshold.
        ProbeRegistry.Register(new NativeSkyFalloffProbe(
            "wall_control_depth", NativeSkyFalloffProbe.Metric.Depth, new IntVec3(-13, 0, 65)));
        ProbeRegistry.Register(new NativeSkyFalloffProbe(
            "wall_control_fraction", NativeSkyFalloffProbe.Metric.Fraction, new IntVec3(-13, 0, 65)));
        ProbeRegistry.Register(new NativeSkyFalloffProbe(
            "glass_wall_depth", NativeSkyFalloffProbe.Metric.Depth, new IntVec3(13, 0, 65)));
        ProbeRegistry.Register(new NativeSkyFalloffProbe(
            "glass_wall_fraction", NativeSkyFalloffProbe.Metric.Fraction, new IntVec3(13, 0, 65)));
        // §16: what one map-mesh dirty flag costs, per layer, in microseconds. Seven probes rather
        // than one because the question is a comparison — our three added regenerates against the
        // vanilla ones already on the same flag — and a single total would hide exactly that. The
        // layer names are strings because SectionLayer_SunShadows and SectionLayer_Darkness are
        // internal to Assembly-CSharp; see SectionRegenerateTimingProbe's header.
        ProbeRegistry.Register(new SectionRegenerateTimingProbe(
            "regen_us_lighting_overlay", "Verse.SectionLayer_LightingOverlay"));
        ProbeRegistry.Register(new SectionRegenerateTimingProbe(
            "regen_us_indoor_mask", "Verse.SectionLayer_IndoorMask"));
        ProbeRegistry.Register(new SectionRegenerateTimingProbe(
            "regen_us_gravship_hull", "RimWorld.SectionLayer_GravshipHull"));
        ProbeRegistry.Register(new SectionRegenerateTimingProbe(
            "regen_us_darkness", "Verse.SectionLayer_Darkness"));
        ProbeRegistry.Register(new SectionRegenerateTimingProbe(
            "regen_us_sun_shadows", "Verse.SectionLayer_SunShadows"));
        ProbeRegistry.Register(new SectionRegenerateTimingProbe(
            "regen_us_night_desaturation", "CelestialLighting.SectionLayer_NightDesaturation"));
        ProbeRegistry.Register(new SectionRegenerateTimingProbe(
            "regen_us_eave_shade", "CelestialLighting.SectionLayer_EaveShade"));

        // §28: whole-mod performance sweep, measured through Circinus rather than Dubs because the
        // question driving it is "which of our per-frame entry points actually costs anything", and
        // Dubs omits a row entirely when a method was never called -- an absent row and a cheap row
        // read identically. Circinus reports Calls directly, so an arm that measured nothing says so.
        //
        // ARMED AS A BANK, NOT ONE PER RUN. §27's one-arm-per-run rule exists because flag flips stop
        // dirtying the map after the first whole-map rebake, so a second arm in the same process
        // records nothing. That failure is specific to comparing ONE method across two feature
        // states. These are DISTINCT methods, each with its own DevProfiler, read at the same
        // instant -- a breakdown of one frame budget, not an A/B -- so simultaneous arming is sound
        // and is the only way to get shares that sum against a parent.
        //
        // Each target gets Calls + TotalMs + MaxMs + Patched. Calls and Patched are not optional
        // detail: TotalMs is meaningless while Calls is zero, and Circinus sheds instrumentation on
        // its own schedule, so an arm that shed mid-run reads zero and looks exactly like an entry
        // point that never ran. Per-call cost is TotalMs/Calls, never AvgMs -- AvgMs is per CYCLE,
        // and a frame that entered the method twice divides by the wrong thing.
        void ArmBank(string prefix, string type, string method)
        {
            ProbeRegistry.Register(new CircinusProbe(
                prefix + "_calls", CircinusProbe.Metric.Calls, type, method));
            ProbeRegistry.Register(new CircinusProbe(
                prefix + "_total_ms", CircinusProbe.Metric.TotalMs, type, method));
            ProbeRegistry.Register(new CircinusProbe(
                prefix + "_max_ms", CircinusProbe.Metric.MaxMs, type, method));
            ProbeRegistry.Register(new CircinusProbe(
                prefix + "_patched", CircinusProbe.Metric.Patched, type, method));
        }

        ProbeRegistry.Register(new CircinusProbe("circinus_available", CircinusProbe.Metric.Available));
        ProbeRegistry.Register(new CircinusProbe("circinus_cycles", CircinusProbe.Metric.Cycles,
            "Verse.SkyManager", "SkyManagerUpdate"));

        // The two vanilla parents. Nearly every patch we own hangs off one of these, so they are the
        // denominators: an arm on one of our own methods is only interesting as a share of these.
        ArmBank("circ_skyupdate", "Verse.SkyManager", "SkyManagerUpdate");
        ArmBank("circ_skytarget", "Verse.WeatherWorker", "CurSkyTarget");

        // Our per-frame draw entry points, each the postfix that vanilla actually calls. Harmony
        // emits a call rather than inlining, so these isolate our cost from vanilla's.
        ArmBank("circ_clouddraw", "CelestialLighting.CloudSheetOverlay", "Draw");
        // NOT CloudSheetDraw.PlaceSheets: it has two overloads, so AccessTools.Method by name alone
        // throws Ambiguous match. No loss -- it is cached per TICK, so its per-frame cost is a
        // dictionary-free field compare, and circ_clouddraw already contains whatever it does spend.
        ArmBank("circ_snowglare", "CelestialLighting.SnowGlareOverlay", "Draw");
        ArmBank("circ_aurora", "CelestialLighting.AuroraCurtainOverlay", "Advance");

        // Section bakes. Ours and the two vanilla layers we patch, so a regression in our postfix is
        // separable from the bake it rides on.
        ArmBank("circ_lightoverlay", "Verse.SectionLayer_LightingOverlay", "Regenerate");
        ArmBank("circ_sunshadows", "Verse.SectionLayer_SunShadows", "Regenerate");
        ArmBank("circ_desat", "CelestialLighting.SectionLayer_NightDesaturation", "Regenerate");
        ArmBank("circ_eaveshade", "CelestialLighting.SectionLayer_EaveShade", "Regenerate");

        // Named suspects, armed for their CALL COUNT rather than their duration. Each is individually
        // trivial and none would ever show up as a Dubs row; the question is how many times a frame
        // they run, which is the number Dubs cannot give and Circinus can.
        //
        // MapSky's two condition gates walk the GameConditionManager chain behind a lambda that
        // captures `map`, so each call allocates a closure and a delegate. That is invisible in a
        // duration column and expensive in aggregate if the count is per-frame-per-call-site: there
        // are 15 SkyBlackedOut call sites and 14 HasSky ones, and HasSky additionally walks the
        // biome's weather commonality list every time. MapSky's own header records a decision NOT to
        // cache these on the grounds that it "would buy nothing measurable" -- this arm is what
        // checks that decision against a measurement rather than assuming it still holds.
        ArmBank("circ_blackedout", "CelestialLighting.MapSky", "SkyBlackedOut");
        ArmBank("circ_hassky", "CelestialLighting.MapSky", "HasSky");

        // The shared GameConditionManager chain walk itself, underneath all four gates. Armed because
        // it is the only thing that can measure a memo ON a gate: memoising SkyBlackedOut or
        // EclipseActive does not change how often the GATE is asked -- every caller still calls it --
        // it changes how often the ask reaches the walk. An arm on the public gate would therefore
        // report an unchanged call count across the exact change that removed the work, which reads
        // as "the memo did nothing" and is the opposite of what happened.
        //
        // Private, and AccessTools.Method resolves it anyway. That is the reason to name the walk
        // rather than the compute helpers above it: one arm covers all four gates, so the count is
        // directly "chain walks this frame" and needs no summing across arms.
        ArmBank("circ_anycondition", "CelestialLighting.MapSky", "AnyCondition");
        ArmBank("circ_eclipseactive", "CelestialLighting.MapSky", "EclipseActive");
        ArmBank("circ_unnaturaldark", "CelestialLighting.MapSky", "UnnaturalDarknessActive");

        // O(n^2) in sheet count, recomputed every frame from placements that only change once a TICK.
        // Cheap per call; the arm is here to say how many calls there are.
        ArmBank("circ_overlap", "CelestialLighting.CloudSheetLayout", "OverlapDepth");

        // §28 step 2: the sixteen postfixes hanging off WeatherWorker.CurSkyTarget, armed
        // individually. CurSkyTarget is the largest child of the frame budget and is entirely made of
        // these, so the breakdown is the only way to know which one to open -- the parent row says
        // how much, never where.
        //
        // Read as SHARES, not as absolute milliseconds. Sixteen simultaneous arms put Circinus's
        // per-call transpile cost into all sixteen rows, so each is inflated by roughly the same
        // fixed amount and the sum overshoots the parent measured alone. That is fine for the
        // question being asked (which of these is the big one) and wrong for the question it must not
        // be used for (what does this one cost), which is what perf_parents.json exists for.
        // Checking the sum against an unarmed parent is also the guard from §27's lesson: a large gap
        // between parent and the sum of its children means the time is somewhere nobody armed.
        ArmBank("circ_pf_auroracurtaindraw", "CelestialLighting.Patch_AuroraCurtainDraw", "Postfix");
        ArmBank("circ_pf_cloudcoversky", "CelestialLighting.Patch_CloudCoverSky", "Postfix");
        ArmBank("circ_pf_lowlightdesaturation", "CelestialLighting.Patch_LowLightDesaturation", "Postfix");
        ArmBank("circ_pf_auroratint", "CelestialLighting.Patch_AuroraTint", "Postfix");
        ArmBank("circ_pf_purplelight", "CelestialLighting.Patch_PurpleLight", "Postfix");
        ArmBank("circ_pf_moonshadowcolor", "CelestialLighting.Patch_MoonShadowColor", "Postfix");
        ArmBank("circ_pf_limbrefraction", "CelestialLighting.Patch_LimbRefraction", "Postfix");
        ArmBank("circ_pf_bloodmoon", "CelestialLighting.Patch_BloodMoon", "Postfix");
        ArmBank("circ_pf_nightdesaturationstrength", "CelestialLighting.Patch_NightDesaturationStrength", "Postfix");
        ArmBank("circ_pf_enclosedambient", "CelestialLighting.Patch_EnclosedAmbient", "Postfix");
        ArmBank("circ_pf_polarnightblue", "CelestialLighting.Patch_PolarNightBlue", "Postfix");
        ArmBank("circ_pf_weatherdimming", "CelestialLighting.Patch_WeatherDimming", "Postfix");
        ArmBank("circ_pf_nightradiance", "CelestialLighting.Patch_NightRadiance", "Postfix");
        ArmBank("circ_pf_twilightcolor", "CelestialLighting.Patch_TwilightColor", "Postfix");
        ArmBank("circ_pf_skycolortemperature", "CelestialLighting.Patch_SkyColorTemperature", "Postfix");
        ArmBank("circ_pf_weathershadowcolor", "CelestialLighting.Patch_WeatherShadowColor", "Postfix");

        // §28 step 3: the shared weather/glow reads underneath the sky postfixes. Same technique that
        // found the MapSky gates -- arm for CALL COUNT and see whether one answer is being rebuilt
        // many times a frame. WeatherDimming.DimmingFor has eight call sites and CloudSheetDraw's own
        // header already notes that the read behind it "walks the weather pair, resolves a mod
        // extension on each and lerps them, which is small but not free", so the count is the
        // question.
        ArmBank("circ_dimming", "CelestialLighting.WeatherDimming", "DimmingFor");
        ArmBank("circ_cloudopacity", "CelestialLighting.WeatherDimming", "CloudOpacityFor");
        ArmBank("circ_visualglow", "CelestialLighting.NightRadiance", "VisualGlowFor");

        // §28 step 4: inside CloudSheetOverlay.Draw, the largest single per-frame method we own at
        // ~80 us/call. Same shares-not-absolutes caveat as the postfix bank above. The sum is checked
        // against the parent deliberately: §27's lesson was that a large gap between a parent and its
        // armed children is itself the finding -- there the missing 43 ms was a polygon set being
        // built lazily inside the timed scope, and no amount of optimising the children would have
        // touched it.
        ArmBank("circ_sheetcolour", "CelestialLighting.CloudSheetOverlay", "SheetColour");
        ArmBank("circ_bakedsheet", "CelestialLighting.CloudSheetOverlay", "BakedSheet");
        ArmBank("circ_volumesheet", "CelestialLighting.CloudSheetOverlay", "VolumeSheet");
        ArmBank("circ_underlit", "CelestialLighting.CloudSheetMath", "UnderlitFraction");
        ArmBank("circ_deckillum", "CelestialLighting.CloudSheetMath", "DeckIllumination");
        ArmBank("circ_sheetalpha", "CelestialLighting.CloudLayers", "SheetAlphaFor");
        ArmBank("circ_hottint", "CelestialLighting.CloudLayers", "HotTintFor");

        // §28 step 5: the shader-uniform writer under VolumeSheet, and the one arm in this bank that
        // is honest as an A/B rather than only as a share.
        //
        // The general objection to arming a child and then optimising it is that the optimisation
        // removes CALLS, so the arm's own per-call transpile overhead falls with them and the fix is
        // credited with deleting its own instrumentation. That does not apply here: the §28 memo
        // inside Configure removes native `Material.Set*` calls from WITHIN the method and leaves the
        // number of times Configure itself is entered exactly where it was, at one per drawn sheet
        // per frame. Instrumentation cost per call is therefore identical between the two builds, and
        // circ_configure_calls is the pin that says so -- an A/B where that number moved would be
        // comparing two different amounts of instrumentation and its TotalMs delta would mean
        // nothing.
        ArmBank("circ_configure", "CelestialLighting.CloudVolumeShader", "Configure");

        // Inertness guard for the removed across-map shadow tilt (issues #11, #26). These three
        // originally asked "does §3's gradient actually render?"; now they assert it does NOT, at
        // both ends of the shadow axis. Still three probes because a ratio alone cannot say whether
        // both ends were measured at all — the two lengths in cells are what make a 1.0 ratio
        // readable as "genuinely equal" rather than "both sentinels". See ShadowExtrusionProbe's
        // header for why this reads baked vertex alpha instead of screenshot pixels.
        ProbeRegistry.Register(new ShadowExtrusionProbe(
            "shadow_extrude_far_cells", ShadowExtrusionProbe.Metric.FarEdgeCells));
        ProbeRegistry.Register(new ShadowExtrusionProbe(
            "shadow_extrude_near_cells", ShadowExtrusionProbe.Metric.NearEdgeCells));
        ProbeRegistry.Register(new ShadowExtrusionProbe(
            "shadow_extrude_ratio", ShadowExtrusionProbe.Metric.FarOverNear));

        // #12/PR #18: how many times per frame solar and lunar geometry is asked for, and how many
        // times it is actually derived. Eleven probes rather than one because the whole claim is the
        // gap between those two series — a single "evaluations" number could not distinguish a memo
        // that works from a caller that stopped asking. Mean and max are both reported for each: the
        // mean is the steady frame, the max is the frame that also regenerated visible sections and
        // is where any tick-boundary double-evaluation would surface. See GeometryEvalCountProbe's header for why this counts instead of timing.
        GeometryEvalCounters.Install();
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_solar_calls_mean", GeometryEvalCountProbe.Metric.SolarCallsMean));
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_solar_calls_max", GeometryEvalCountProbe.Metric.SolarCallsMax));
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_solar_evals_mean", GeometryEvalCountProbe.Metric.SolarEvalsMean));
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_solar_evals_max", GeometryEvalCountProbe.Metric.SolarEvalsMax));
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_moon_calls_mean", GeometryEvalCountProbe.Metric.MoonCallsMean));
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_moon_calls_max", GeometryEvalCountProbe.Metric.MoonCallsMax));
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_moon_evals_mean", GeometryEvalCountProbe.Metric.MoonEvalsMean));
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_moon_evals_max", GeometryEvalCountProbe.Metric.MoonEvalsMax));
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_frames_counted", GeometryEvalCountProbe.Metric.FramesCounted));
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_memo_present", GeometryEvalCountProbe.Metric.MemoPresent));
        ProbeRegistry.Register(new GeometryEvalCountProbe(
            "geom_maps_loaded", GeometryEvalCountProbe.Metric.MapsLoaded));

        // How many section-layer meshes §9's night wash and §15b's eave shade actually submit per
        // frame. Both carry their map-wide strength in a material alpha rather than in the mesh, so
        // each is fully transparent for half of every day — the wash through daylight, the shade
        // through night — and vanilla's MapDrawLayer.DrawLayer cannot tell. These count the
        // submissions that survive the layers' own DrawLayer overrides. sections_drawn_mean is the
        // guard reading: a zero wash count only means anything while the map is demonstrably still
        // drawing something.
        SectionLayerDrawCounters.Install();
        ProbeRegistry.Register(new SectionLayerDrawCountProbe(
            "wash_draws_mean", SectionLayerDrawCountProbe.Metric.WashDrawsMean));
        ProbeRegistry.Register(new SectionLayerDrawCountProbe(
            "wash_draws_max", SectionLayerDrawCountProbe.Metric.WashDrawsMax));
        ProbeRegistry.Register(new SectionLayerDrawCountProbe(
            "shade_draws_mean", SectionLayerDrawCountProbe.Metric.ShadeDrawsMean));
        ProbeRegistry.Register(new SectionLayerDrawCountProbe(
            "shade_draws_max", SectionLayerDrawCountProbe.Metric.ShadeDrawsMax));
        ProbeRegistry.Register(new SectionLayerDrawCountProbe(
            "sections_drawn_mean", SectionLayerDrawCountProbe.Metric.SectionsDrawnMean));
        ProbeRegistry.Register(new SectionLayerDrawCountProbe(
            "draw_frames_counted", SectionLayerDrawCountProbe.Metric.FramesCounted));

        // Expose CelestialLighting's runtime feature flags to the harness's SetFeature step so a
        // scenario can screenshot an effect off then on. The setter just writes the shipped mod's
        // static flag; in production nothing calls it and the flag stays at its default (on).
        // §27. Registered with the THREE-arg overload and defaultEnabled: false, which is
        // load-bearing rather than tidiness: the two-arg overload assumes true, and
        // FeatureRegistry.ResetAll() — which WorldStateReset runs between every pair of scenarios in a
        // suite — calls every setter with its registered default. Registered as true, §27 would switch
        // itself on for every later scenario in the file and rewrite their lighting, which is how
        // weather_dimming_census came to fail in-suite while passing standalone.
        //
        // ForceRebuild is not optional here either: half of §27 is baked into the lighting overlay's
        // vertex colours during a section regenerate, so flipping the flag alone changes nothing on
        // screen until something else happens to dirty a section.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightsKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLights = enabled;
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: false);
        // §27 phase 2. Registered with the two-arg overload, i.e. defaultEnabled true, because true
        // IS its shipped default — it is a sub-flag of vector_lights and does nothing at all while
        // that one is off, so it cannot contaminate a later scenario the way §27 itself could.
        // ForceRebuild for the same reason as above: the wedge geometry is baked into each light's
        // mesh, so flipping this changes nothing until something rebuilds it.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightPenumbraKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightPenumbra = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        // Two-arg overload, i.e. defaultEnabled true, because true is the shipped default and the
        // one that makes §27 mean anything. Like the penumbra flag it is inert while vector_lights
        // is off, so it cannot contaminate a later scenario. ForceRebuild because the suppression is
        // baked into the lighting overlay's vertex colours during a section regenerate.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightSuppressKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightSuppress = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        // Two-arg overload, i.e. defaultEnabled true, matching the shipped default. Safe for the same
        // reason as the penumbra flag: it is inert while vector_lights is off, which is what
        // FeatureRegistry.ResetAll leaves that one at, so it cannot contaminate a later scenario.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightBlendKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightBlend = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        // §27 phase 3. THREE-arg overload with defaultEnabled: false, matching the shipped default
        // and load-bearing for the same reason vector_lights uses it — registered as true,
        // FeatureRegistry.ResetAll() would switch the mask on for every later scenario in a suite.
        // ForceRebuild because the mask is baked into the lighting overlay's vertex colours during a
        // section regenerate, so flipping it changes nothing on screen until something else dirties a
        // section.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightMaskKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightMask = enabled;
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: false);
        // §27 phase 3's beam half. THREE-arg overload with defaultEnabled: false to match the
        // shipped default; inert while vector_light_mask is off, but registered with the explicit
        // default anyway so ResetAll cannot switch it on for a later scenario in a suite.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightMaskBeamKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightMaskBeam = enabled;
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: false);
        FeatureRegistry.Register(
            CelestialLightingFeatures.CivilTwilightPersistenceKey,
            enabled => CelestialLightingFeatures.CivilTwilightPersistence = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.PenumbraContrastKey,
            enabled => CelestialLightingFeatures.PenumbraContrast = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.MoonShadowsKey,
            enabled => CelestialLightingFeatures.MoonShadows = enabled);
        // The only A/B on this list whose two arms both require a THIRD mod to be installed: off is
        // the moon we place on the ecliptic ourselves, on is Realistic Axial Tilt's inclined one, and
        // flipping it inside one run is the only way to compare them against the same world, tick and
        // moon phase. Comparing two RAT builds instead would vary the tilt world along with the moon.
        FeatureRegistry.Register(
            CelestialLightingFeatures.AxialTiltLunarGeometryKey,
            enabled => CelestialLightingFeatures.AxialTiltLunarGeometry = enabled);
        // Like the RAT flag above, both arms need a third mod present — off is our own tilt, on is
        // the tilt the loaded world was generated with. Flipping it inside one run is the only way to
        // compare them against the same world and day-of-year; generating two worlds instead would
        // vary the biomes and the latitude along with the tilt.
        FeatureRegistry.Register(
            CelestialLightingFeatures.PlanetsmithGeometryKey,
            enabled => CelestialLightingFeatures.PlanetsmithGeometry = enabled);
        // Not a feature flag either, and it WRITES to Planetsmith's world: it puts a steep tilt on
        // the loaded save so the interop has something visible to carry. Registered with
        // defaultEnabled FALSE for the same reason realistic_preset is — the resting state is "leave
        // the world alone", so ResetAll() between scenarios must restore, not apply.
        FeatureRegistry.Register(
            PlanetsmithTiltOverride.FeatureKey,
            enabled => PlanetsmithTiltOverride.Set(enabled),
            defaultEnabled: false);
        // The RP2 pair, same shape as the Planetsmith pair above, measuring one more thing: off is
        // our tilt on OUR phase, on is their tilt on THEIR phase, and the phase half of that is
        // visible at a day-of-year where ours is flat and theirs is at full swing.
        FeatureRegistry.Register(
            CelestialLightingFeatures.RealisticPlanetsGeometryKey,
            enabled => CelestialLightingFeatures.RealisticPlanetsGeometry = enabled);
        // WRITES to Realistic Planets 2's state, defaultEnabled FALSE, for the reasons given on the
        // Planetsmith override above. One difference matters here: their tilt is a STATIC field, so
        // an un-restored override survives the world being unloaded and would follow the harness into
        // the next scenario's save-load rather than dying with the map.
        FeatureRegistry.Register(
            RealisticPlanetsTiltOverride.FeatureKey,
            enabled => RealisticPlanetsTiltOverride.Set(enabled),
            defaultEnabled: false);
        FeatureRegistry.Register(
            CelestialLightingFeatures.NightRadianceKey,
            enabled => CelestialLightingFeatures.NightRadiance = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.WeatherDimmingKey,
            enabled => CelestialLightingFeatures.WeatherDimming = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.CloudCoverKey,
            enabled => CelestialLightingFeatures.CloudCover = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.CloudCoverLabelKey,
            enabled => CelestialLightingFeatures.CloudCoverLabel = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.CloudUnderlightKey,
            enabled => CelestialLightingFeatures.CloudUnderlight = enabled);
        // §23b ships OFF, so this is registered with defaultEnabled false — same as the two dev
        // overrides below, and for a related reason: the resting state has to be the pre-feature one
        // until the live A/B settles issue #88's option-2 question.
        FeatureRegistry.Register(
            CelestialLightingFeatures.CloudUnderlightLayerKey,
            enabled => CelestialLightingFeatures.CloudUnderlightLayer = enabled,
            defaultEnabled: false);
        // §23c and §25, registered separately. Independently switchable on purpose: they share a field
        // but they are three different claims, and a scenario has to be able to show one of them
        // without the other two confounding the frame.
        FeatureRegistry.Register(
            CelestialLightingFeatures.CloudShadowKey,
            enabled => CelestialLightingFeatures.CloudShadow = enabled,
            defaultEnabled: false);
        // §25 ships ON now, so its resting state is true and this takes the two-arg overload like every
        // other shipped feature. ResetAll between scenarios in a suite restores THIS value, so getting
        // it wrong would leave a later scenario measuring an unshipped baseline without saying so.
        FeatureRegistry.Register(
            CelestialLightingFeatures.CloudSheetKey,
            enabled => CelestialLightingFeatures.CloudSheet = enabled);
        // Not a CelestialLightingFeatures flag either, and the one whose OFF position is the interesting
        // one: it suppresses the Clouds interop rather than one of our effects, so a run with Clouds
        // loaded can measure "their clouds alone" against "their clouds and ours at once" out of one
        // boot. Registered defaultEnabled TRUE because the resting state is the shipped behaviour —
        // see CloudsCompatOverride's header for why that must be the real load-order read rather than
        // a forced "installed".
        FeatureRegistry.Register(
            CloudsCompatOverride.FeatureKey,
            enabled => CloudsCompatOverride.Set(enabled));
        // §25b, which also ships on and so also takes the two-arg overload. It is not a lane — it is a
        // property of the cloud the lane above draws — and turning it off collapses the deck mixture
        // to all-low, i.e. the single-deck sky §25 drew before, which is what makes an A/B of it
        // measure the varieties rather than the whole subsystem.
        FeatureRegistry.Register(
            CelestialLightingFeatures.CloudDeckVarietiesKey,
            enabled => CelestialLightingFeatures.CloudDeckVarieties = enabled);
        // §25c's raymarched cloud volume. Registered with the THREE-arg overload and defaultEnabled
        // FALSE, because it ships off — the two-arg overload would leave every later scenario in a
        // suite running the shader after a ResetAll and quietly change what their frames cost.
        // §25c now ships ON, so it takes the two-arg overload like every other shipped feature. A
        // ResetAll between scenarios in a suite restores TRUE; registering it false would leave every
        // later scenario measuring the flat renderer without saying so.
        FeatureRegistry.Register(
            CelestialLightingFeatures.CloudVolumeKey,
            enabled => CelestialLightingFeatures.CloudVolume = enabled);
        // §25d, which unlike §25c beside it ships ON — so it takes the two-arg overload, and a
        // ResetAll between scenarios in a suite restores TRUE. Getting that wrong would leave every
        // later scenario measuring the invisible pre-#144 cloud without saying so.
        FeatureRegistry.Register(
            CelestialLightingFeatures.CloudPresenceKey,
            enabled => CelestialLightingFeatures.CloudPresence = enabled);
        // Not a CelestialLightingFeatures flag: forces CloudCoverClock.FractionForMap's result to a
        // fixed constant so a scenario gets a specific, reproducible cloud fraction on demand instead
        // of depending on which absolute year the harness's clock jump happened to land in (see
        // CloudCoverFractionOverride's header). Registered with defaultEnabled FALSE for the same
        // reason PlanetsmithTiltOverride is — the resting state is "leave the real drift alone".
        CloudCoverFractionOverride.Install();
        // The near-full sky, for the case the mid-range fraction cannot answer: §25's sheets are
        // large and few, so at 0.35 the camera is quite likely to be looking at no cloud at all.
        FeatureRegistry.Register(
            CloudCoverFractionOverride.OvercastFeatureKey,
            enabled => CloudCoverFractionOverride.SetOvercast(enabled),
            defaultEnabled: false);
        FeatureRegistry.Register(
            CloudCoverFractionOverride.FeatureKey,
            enabled => CloudCoverFractionOverride.Set(enabled),
            defaultEnabled: false);
        // Not a CelestialLightingFeatures flag: this bridges the "true pitch-black" atmospheric-floor
        // switch that lives on NightRadianceSettings, so a probe scenario can drop the constant
        // starlight+airglow floor out of the night_radiance sum and watch only moonlight remain.
        FeatureRegistry.Register(
            CelestialLightingFeatures.NightAtmosphericGlowKey,
            enabled => NightRadianceSettings.Current.AtmosphericGlowEnabled = enabled);
        // Not a feature flag either: this switches the whole aesthetic preset, and it exists because
        // §7/§18b's night floors are INVISIBLE on the shipped default. Cinematic sets
        // minNightBrightness to 0.50 — an accessibility clamp more than ten times every night floor
        // the mod computes (surface 0.040, vacuum 0.0317, floors-off 0.0005) — so the overlay clamps
        // to 0.50 in all three cases and the rendered frames come out pixel-identical. A scenario
        // that screenshots a night-floor change on Cinematic is measuring the clamp, not the floor.
        // Realistic puts both brightness floors at 0, which is the preset whose stated purpose is
        // that an unlit night is actually dark, so it is the only one where the floor reaches pixels.
        // ForceRebuild because Realistic also raises §9's desaturation (0.4 -> 0.85) and that half of
        // the wash lives in baked section meshes, exactly as the LowLightDesaturation bridge below.
        // Apply this BEFORE any night_atmospheric_glow toggle: ApplyToRuntime rewrites
        // AtmosphericGlowEnabled from the persisted field and would silently undo it.
        //
        // Registered with defaultEnabled: FALSE, which is load-bearing rather than tidiness. This is
        // not a feature flag whose resting state is "the shipped behaviour is on" — the two-argument
        // Register overload's assumption, and correct for every genuine effect toggle around it. Here
        // "on" means a preset the mod does NOT ship on, so the resting state is off.
        //
        // Registered as true, FeatureRegistry.ResetAll() — which WorldStateReset runs between every
        // pair of scenarios in a suite — called this setter with true and applied Realistic to a run
        // that had never asked for it. Everything downstream of a preset knob then silently measured
        // the wrong preset from the second scenario onward: weather_dimming_census and
        // weather_dimming read every non-zero dimming fraction 1.5x high (Realistic's 0.30 against
        // Cinematic's 0.20) and had been failing in-suite while passing standalone.
        FeatureRegistry.Register(
            "realistic_preset",
            enabled =>
            {
                CelestialLightingSettings settings = CelestialLightingSettingsMod.Settings;
                if (settings == null)
                    return;

                settings.ApplyPreset(enabled ? CelestialPreset.Realistic : CelestialPreset.Cinematic);
                settings.ApplyToRuntime();
                NightDesaturationRedraw.ForceRebuild();
            },
            defaultEnabled: false);
        // §9. Like §7b and §15 below, the flag write alone is no longer enough: the per-cell wash lives
        // in baked section meshes and SectionLayer_NightDesaturation now skips the bake entirely while
        // the feature is off, so a scenario flipping this back on must force the rebuild or its "after"
        // screenshot shows no wash at all. (The material half — Patch_NightDesaturationStrength — still
        // reacts per frame; only the mesh half needs this.)
        FeatureRegistry.Register(
            CelestialLightingFeatures.LowLightDesaturationKey,
            enabled =>
            {
                CelestialLightingFeatures.LowLightDesaturation = enabled;
                NightDesaturationRedraw.ForceRebuild();
            });
        FeatureRegistry.Register(
            CelestialLightingFeatures.SkyColorTemperatureKey,
            enabled => CelestialLightingFeatures.SkyColorTemperature = enabled);
        // §19. Flipping this off kills BOTH arms together — the sky tint and §7a's raised brightness
        // floor — because OzoneTwilight.OverlayFloorFor collapses to the caller's own minBrightness
        // when the feature is off. That is what makes "off" a faithful pre-feature baseline rather
        // than "no blue but the nights are still lifted". Left at the default enabled state, which
        // matches the shipped default: a registered arm disagreeing with what the mod ships is what
        // silently corrupted later scenarios in the realistic_preset and pitch_black_true
        // post-mortems, via ResetAll().
        FeatureRegistry.Register(
            CelestialLightingFeatures.PolarNightBlueKey,
            enabled => CelestialLightingFeatures.PolarNightBlue = enabled);
        // §19c. Left at the default enabled state, which matches the shipped default. "Off" is a
        // faithful pre-feature baseline everywhere, and outside the -6..-4 window "on" is too — the
        // patch early-returns on the envelope — so this flag can only ever change two degrees of
        // dusk, which is exactly the A/B the purple_light scenario films.
        FeatureRegistry.Register(
            CelestialLightingFeatures.PurpleLightKey,
            enabled => CelestialLightingFeatures.PurpleLight = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.AuroraKey,
            enabled => CelestialLightingFeatures.Aurora = enabled);
        // §11a. Flipping this off restores §11's flat tint at its full solo strength rather than leaving a
        // weaker sky, which is what makes it a usable A/B baseline: "off" is exactly what the mod
        // rendered before the curtain existed, so the two screenshots differ only by this feature.
        FeatureRegistry.Register(
            CelestialLightingFeatures.AuroraCurtainKey,
            enabled => CelestialLightingFeatures.AuroraCurtain = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.EclipseDarkeningKey,
            enabled => CelestialLightingFeatures.EclipseDarkening = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.BloodMoonKey,
            enabled => CelestialLightingFeatures.BloodMoon = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.PitchBlackNightsKey,
            enabled => CelestialLightingFeatures.PitchBlackNights = enabled);
        // Not a CelestialLightingFeatures flag: the eclipse mode lives on EclipseSettings (mirrors the
        // mod's own eclipse-mode radio). Bridged as a bool for the harness's SetFeature step: enabled
        // => NaturalOnly (pure geometric eclipses, so a scenario films/validates the §10a trigger in
        // isolation), disabled => UnnaturalOnly (the shipped default, which fires no events at all).
        // The disabled arm tracks the shipped default deliberately: a scenario that never sets this
        // feature must see exactly what a player sees on a fresh install. defaultEnabled: false is
        // what actually delivers that — without it FeatureRegistry.ResetAll(), which runs between
        // every pair of scenarios in a suite, selected the ENABLED arm and left every later scenario
        // on NaturalOnly against the shipped UnnaturalOnly.
        FeatureRegistry.Register(
            "natural_eclipse",
            enabled => EclipseSettings.Mode = enabled ? EclipseMode.NaturalOnly : EclipseMode.UnnaturalOnly,
            defaultEnabled: false);
        // Dev-only staging for the natural-eclipse trigger validation: a real eclipse only happens
        // once every few game years, so this phase-slides the modeled moon (via the pure EclipseStaging
        // math) onto a genuine new-moon-at-node alignment one pre-roll ahead of "now", after which the
        // real trigger detects the transit from real geometry and fires a real Eclipse. Disabling it
        // clears the shifts so the moon returns to its true phase. Never touched by the shipped mod.
        FeatureRegistry.Register(
            "eclipse_stage_alignment",
            enabled =>
            {
                GameComponent_MoonPhase moon = GameComponent_MoonPhase.Current;
                if (moon == null)
                    return;

                if (!enabled)
                {
                    moon.debugSynodicShiftTicks = 0L;
                    moon.debugNodalShiftTicks = 0L;
                    return;
                }

                EclipseStaging.AlignmentShifts shifts = EclipseStaging.ComputeAlignmentShifts(
                    Find.TickManager.TicksAbs,
                    (long)(moon.synodicPeriodDays * GenDate.TicksPerDay),
                    (long)(moon.nodalPeriodDays * GenDate.TicksPerDay),
                    GenDate.TicksPerDay,
                    Formulas.DaysPerYear,
                    EclipseStaging.DefaultPreRollTicks);
                moon.debugSynodicShiftTicks = shifts.SynodicShiftTicks;
                moon.debugNodalShiftTicks = shifts.NodalShiftTicks;
            },
            // defaultEnabled: false, and this one is the most damaging of the family to get wrong.
            // Registered as true, FeatureRegistry.ResetAll() phase-slid the moon onto an eclipse
            // alignment between every pair of scenarios in a suite, so from the second scenario onward
            // the moon was not where the calendar said — silently, because the shift is applied inside
            // GameComponent_MoonPhase.CyclePosition and every clock reading stays correct.
            //
            // Measured 2026-07-30: a scenario reading the moon at day-of-year 40 reported
            // moon_illumination 0.7347 standalone and 0.4988 after a suite prefix, with the harness's
            // own ticks_abs_day reading exactly 40 in BOTH. The clock was never wrong; only the moon
            // was. That is what makes this worth a comment rather than a one-word diff — the obvious
            // diagnosis for a drifting moon is a drifting clock, and here the clock is innocent.
            defaultEnabled: false);
        // Not a CelestialLightingFeatures flag: bridges the minimum-brightness clamp so a visual
        // scenario can force a genuinely pitch-black night (MinNightBrightness -> 0) instead of the
        // shipped playable floor. "enabled" == true means clamp to 0 (true black); false restores the
        // default playable floor.
        // §7b indoor sky occlusion. The flag write alone is not enough: unlike every other effect here,
        // §7b's output lives in baked section meshes rather than in a per-frame material, so a scenario
        // toggling it must also force those meshes to regenerate or both A/B screenshots show the same
        // pre-toggle bake.
        FeatureRegistry.Register(
            CelestialLightingFeatures.IndoorSkyOcclusionKey,
            enabled =>
            {
                CelestialLightingFeatures.IndoorSkyOcclusion = enabled;
                IndoorOcclusionRedraw.ForceRebuild();
            });
        // Issue #80 / IndoorGlowPassthrough: same baked-mesh situation as §7b's own flag immediately
        // above (this cap term is applied inside the same SectionLayer_LightingOverlay.Regenerate
        // postfix), so toggling it needs the identical rebuild or the A/B would compare a stale bake
        // against itself.
        FeatureRegistry.Register(
            CelestialLightingFeatures.IndoorGlowPassthroughKey,
            enabled =>
            {
                CelestialLightingFeatures.IndoorGlowPassthrough = enabled;
                IndoorOcclusionRedraw.ForceRebuild();
            });
        // §7c / NativeSkyFalloff: same baked-mesh situation as the two §7b-family flags immediately
        // above (SkyFalloffSource feeds the identical CapOcclusion call inside that same postfix), so
        // the toggle needs the same forced rebuild.
        FeatureRegistry.Register(
            CelestialLightingFeatures.NativeSkyFalloffKey,
            enabled =>
            {
                CelestialLightingFeatures.NativeSkyFalloff = enabled;
                IndoorOcclusionRedraw.ForceRebuild();
            });
        // §7b mesh-staleness fix (GameComponent_SkyFalloffRedraw). Unlike the three flags above, no
        // ForceRebuild here: this only changes whether FUTURE ticks keep the mesh in step with
        // CurSkyGlow, and toggling it must not itself repaint anything, or a scenario could never
        // capture the "already stale, fix now switched off" frame the bug depends on.
        FeatureRegistry.Register(
            CelestialLightingFeatures.SkyFalloffRedrawKey,
            enabled => CelestialLightingFeatures.SkyFalloffRedraw = enabled);
        // §15's caster heights are baked into the sun-shadow meshes, so like §7b the toggle is
        // invisible until they are regenerated — without the rebuild both A/B screenshots would show
        // whatever was baked before the flip.
        FeatureRegistry.Register(
            CelestialLightingFeatures.EaveShadowsKey,
            enabled =>
            {
                CelestialLightingFeatures.EaveShadows = enabled;
                EaveShadowRedraw.ForceRebuild();
            });
        // §15b's shade, as a SEPARATE axis from the caster above. In a shipped game one setting drives
        // both; here they are independent so a scenario can hold one fixed and move the other, which
        // is the only way a frame can say WHICH layer owns a boundary artifact. The caster-on /
        // shade-off cell is the diagnostic case: it is precisely the bright lip §15b exists to remove,
        // so a scenario can now produce that artifact deliberately and measure it.
        //
        // EaveShadowRedraw is the right rebuild despite the name: it dirties every section with
        // MapMeshFlagDefOf.Buildings, and SectionLayer_EaveShade subscribes to Roofs | Buildings, so
        // the flag reaches this layer as well as the sun-shadow meshes. Both halves of §15 therefore
        // re-bake off one call, which is also what keeps an A/B honest — a toggle that rebuilt only
        // one layer would leave the other showing what was baked before the flip.
        FeatureRegistry.Register(
            CelestialLightingFeatures.EaveShadeKey,
            enabled =>
            {
                CelestialLightingFeatures.EaveShade = enabled;
                EaveShadowRedraw.ForceRebuild();
            });
        // §18c vacuum shadow contrast. A plain per-frame material effect (colors.shadow feeds
        // MatBases.SunShadow.color through SkyManager's own lerp), so unlike §7b/§15 above there is
        // nothing baked to rebuild — the next frame shows the flip. The A/B this exists for is an
        // orbital-map daytime shadow off vs on, read with the moon_shadow_render probe, which reads
        // the composed MatBases.SunShadow.color and so measures the umbra directly rather than
        // through pixels.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VacuumShadowContrastKey,
            enabled => CelestialLightingFeatures.VacuumShadowContrast = enabled);
        // §21 the surface-cloud light cavity. Nothing baked to rebuild — SurfaceBuildup.CavityGainFor
        // is read fresh inside NightRadiance.FloorGlowFor on every sky update, so the next frame shows
        // the flip. The A/B this exists for is a snowed-in map at night under an overcast, off vs on;
        // the note on the flag itself (CelestialLightingFeatures.SnowAlbedo) records that the ice
        // sheet and sea ice biomes are the case that specifically wants a human look, since they sit
        // at full buildup year-round and so become permanently brighter.
        FeatureRegistry.Register(
            CelestialLightingFeatures.SnowAlbedoKey,
            enabled => CelestialLightingFeatures.SnowAlbedo = enabled);
        // The raw gain SurfaceBuildup.CavityGainFor hands to both consumers (§7 night floor, §13
        // daytime recovery) — see SurfaceCavityGainProbe's header for why a screenshot A/B alone
        // cannot tell a genuinely-small lift from no lift at all.
        ProbeRegistry.Register(new SurfaceCavityGainProbe());
        // §24 snow-glare bloom (issue #90), the prototype's A/B axis. Nothing baked to rebuild —
        // SnowGlare.AlphaFor is read fresh inside SnowGlareOverlay.Draw every frame, so the next
        // frame shows the flip. Unlike every other feature here the shipped default is OFF, so this
        // registration is what turns it on for a capture at all; a scenario that forgets the
        // SetFeature step measures the baseline twice and reports a confident ΔE 0.00.
        FeatureRegistry.Register(
            CelestialLightingFeatures.SnowGlareKey,
            enabled => CelestialLightingFeatures.SnowGlare = enabled);
        // The drawn alpha, and the residual behind it. Two probes rather than one because they
        // distinguish "no overflow to draw" from "overflow drawn too faintly" — see the probes' own
        // headers for why that distinction decides whether #90 gets built or closed.
        ProbeRegistry.Register(new SnowGlareProbe());
        ProbeRegistry.Register(new SnowGlareExcessProbe());
        // Not a CelestialLightingFeatures flag: sweeps §24's strength knob within one boot, so the
        // calibrated look and the strength needed to actually DRAW #90's inversion can be captured as
        // frames from the same process rather than from two builds. "enabled" true is the strong end;
        // false restores the shipped calibration. Ceiling moves with the scale — see SnowGlare
        // .MaxIntensity for why sweeping one without the other measures the clamp instead of the knob.
        // Not a CelestialLightingFeatures flag: sweeps §23b's amplitude within one boot, the same seam
        // and the same reason as snow_glare_strong below. "enabled" true is 0.20 — the amplitude §23b
        // was first calibrated at, before watching a sunset at it showed it read as distracting and
        // the shipped value was halved to 0.10. Keeping the old value reachable is what stops that
        // decision from becoming a claim about a build nobody can rebuild: the frames either side of
        // it come from one process, at one instant, differing in exactly one constant.
        FeatureRegistry.Register(
            "cloud_underlight_strong",
            enabled => CloudLayers.AmplitudeScale =
                enabled ? 0.20f : CloudUnderlightMath.LayerAmplitude,
            defaultEnabled: false);
        FeatureRegistry.Register(
            "snow_glare_strong",
            enabled =>
            {
                SnowGlare.IntensityScale = enabled ? 0.18f : SnowGlareMath.DefaultIntensityScale;
                SnowGlare.MaxIntensity = enabled ? 1f : SnowGlareMath.MaxIntensity;
            },
            defaultEnabled: false);
        // Not a CelestialLightingFeatures flag: bridges §7b's minimum-indoor-brightness slider so a
        // visual scenario can A/B a sealed room at full black against one held above it. "enabled" ==
        // true means raise the floor to a clearly-visible 0.25; false restores the shipped 0 (black).
        // Rebuilds the baked meshes for the same reason the occlusion toggle does.
        FeatureRegistry.Register(
            "indoor_min_brightness",
            enabled =>
            {
                IndoorOcclusionSettings.Current.MinIndoorBrightness =
                    enabled ? 0.25f : IndoorOcclusionMath.DefaultMinIndoorBrightness;
                IndoorOcclusionRedraw.ForceRebuild();
            });
        // Not a CelestialLightingFeatures flag: flips §14's warp so a scenario can capture the
        // pre-§14 behaviour as the BEFORE half of an A/B. "enabled" false == no warp == the artifact.
        FeatureRegistry.Register(
            "sun_clock_warp",
            enabled => SunClockAdapter.WarpEnabled = enabled);
        // Not a CelestialLightingFeatures flag either: leaves the MOON on the raw day percent while
        // the sun stays warped, which is precisely the artifact §14 shipped with. sun_clock_warp
        // above cannot capture it — that one reverts both bodies to the pre-§14 single clock, where
        // the moon was already correct. This is the toggle that A/Bs the moon-clock fix itself.
        FeatureRegistry.Register(
            "moon_clock_warp",
            enabled => MoonPosition.WarpMoonClock = enabled);
        // The §14 SETTING itself, not a dev-only escape hatch like the two above — this is the real
        // "Vanilla day length / Realistic day length" radio from the mod's settings screen, driven
        // through the same static the settings screen writes. "enabled" true == Realistic.
        //
        // Exposed because the two toggles above can only A/B artifacts WITHIN locked mode, so until
        // now nothing could exercise realistic mode at all — Patch_SunGlow, and the moon riding the
        // identity clock underneath it, were live-untested. A scenario that flips this drives the real
        // SunClockAdapter and MoonPosition, which is the one thing SunClockModeMoonTests structurally
        // cannot do (both take a live Map, so the unit tests mirror the composition instead).
        //
        // Scenarios MUST set this back to false when done: it is a plain static with no per-scenario
        // reset, so leaving it on silently re-times every subsequent scenario's sun.
        FeatureRegistry.Register(
            "realistic_day_length",
            enabled => CelestialLightingFeatures.SunClock =
                enabled ? SunClockMode.Realistic : SunClockMode.LockedToVanilla);
        // Not a CelestialLightingFeatures flag: opens a fresh counting window for the geometry
        // evaluation probes above. A scenario resets, Waits out the segment it wants to characterize,
        // then probes — otherwise every mean is diluted by whatever the scenario did before it, and
        // the steady-state and section-regenerate segments cannot be told apart. Both arms reset;
        // there is no "counting off" state, because a probe read with counting off would report a
        // stale window rather than obviously nothing.
        FeatureRegistry.Register(
            "geometry_count_reset",
            _ => GeometryEvalCounters.Reset());
        // Same contract, for the section-layer draw counters: a scenario resets the window right after
        // a SetTime and before the Wait, so the reading characterizes the new time of day rather than
        // averaging across the clock change that produced it. Both arms reset, for the same reason.
        FeatureRegistry.Register(
            "draw_count_reset",
            _ => SectionLayerDrawCounters.Reset());
        // The off arm restores the floor the SETTINGS carry, not NightRadianceMath's compile-time
        // default. Those are not the same number and the difference is the whole bug: the math default
        // is 0f, so both arms used to write 0f and turning this feature "off" restored nothing. That
        // made ResetAll — which runs between every pair of scenarios in a suite — pin the night floor
        // at 0 for the rest of the run, against Cinematic's shipped 0.50, and every later night
        // reading was measured under a preset nobody selected: weather_dimming's sky_glow read 0.0817
        // against a pinned 0.1419 and its purkinje correspondingly high, because §9 keys rod vision on
        // a darker apparent sky.
        //
        // defaultEnabled: false for the same reason as realistic_preset above — "on" here means true
        // black, which is not what the mod ships, so the resting state is off.
        FeatureRegistry.Register(
            "pitch_black_true",
            enabled =>
            {
                CelestialLightingSettings settings = CelestialLightingSettingsMod.Settings;
                NightRadianceSettings.Current.MinNightBrightness = enabled
                    ? 0f
                    : settings?.minNightBrightness ?? NightRadianceMath.DefaultMinNightBrightness;
            },
            defaultEnabled: false);

        // SunClock caches its measured half-day per TILE, re-measuring only when the absolute day
        // rolls over. The harness's SetTile does not move the colony — it overrides what
        // WorldGrid.LongLatOf reports — so the tile key never changes and the cache cannot tell the
        // latitude under it did. Left unhooked, a scenario at latitude 45 can read a half-day measured
        // for its predecessor at latitude 20 and report a confidently wrong sun_elevation; the
        // scenario's own SetSeason sometimes hides it by rolling the day index, which makes the bug
        // depend on step ordering rather than on anything a reader would think to check.
        //
        // Registered here rather than inside SunClock because the shipped assembly must not reference
        // the harness — this bridge is the only place allowed to know both types exist. In production
        // nothing fires the hook and the cache behaves exactly as it does today.
        WorldOverrideHookRegistry.Register(SunClock.Clear);
    }

    // Installed here rather than at the first probe read for the same reason
    // GeometryEvalCounters.Install is: the patches must be in place before the aurora path is first
    // walked, or the first frames of a scenario would be timed by a different code path than the
    // rest.
    private static void RegisterAuroraPathTiming()
    {
        AuroraPathTimers.Install();

        Register("aurora_path_reset", AuroraPathTimingProbe.Metric.Reset);
        Register("aurora_path_frames", AuroraPathTimingProbe.Metric.Frames);
        Register("aurora_path_overhead_us", AuroraPathTimingProbe.Metric.OverheadUs);
        Register("aurora_path_missing_stages", AuroraPathTimingProbe.Metric.MissingStages);

        Register("aurora_path_total_us", AuroraPathTimingProbe.Metric.TotalUsPerFrame);
        Register("aurora_path_strength_us", AuroraPathTimingProbe.Metric.StrengthUsPerFrame);
        Register("aurora_path_driver_us", AuroraPathTimingProbe.Metric.DriverUsPerFrame);
        Register("aurora_path_advance_us", AuroraPathTimingProbe.Metric.AdvanceUsPerFrame);
        Register("aurora_path_bake_us", AuroraPathTimingProbe.Metric.BakeUsPerFrame);
        Register("aurora_path_upload_us", AuroraPathTimingProbe.Metric.UploadUsPerFrame);
        Register("aurora_path_place_us", AuroraPathTimingProbe.Metric.PlaceUsPerFrame);
        Register("aurora_path_draw_us", AuroraPathTimingProbe.Metric.DrawUsPerFrame);
        Register("aurora_path_setsheet_us", AuroraPathTimingProbe.Metric.SetSheetUsPerFrame);
        Register("aurora_path_drawsheet_us", AuroraPathTimingProbe.Metric.DrawSheetUsPerFrame);

        Register("aurora_path_driver_per_frame", AuroraPathTimingProbe.Metric.DriverCallsPerFrame);
        Register("aurora_path_bake_per_frame", AuroraPathTimingProbe.Metric.BakeCallsPerFrame);
        Register("aurora_path_upload_per_frame", AuroraPathTimingProbe.Metric.UploadCallsPerFrame);
        Register("aurora_path_setsheet_per_frame", AuroraPathTimingProbe.Metric.SetSheetCallsPerFrame);
        Register("aurora_path_drawsheet_per_frame", AuroraPathTimingProbe.Metric.DrawSheetCallsPerFrame);
        Register("aurora_path_bake_us_per_call", AuroraPathTimingProbe.Metric.BakeUsPerCall);
        Register("aurora_path_table_us_per_call", AuroraPathTimingProbe.Metric.TableUsPerCall);
        Register("aurora_path_table_per_frame", AuroraPathTimingProbe.Metric.TableCallsPerFrame);
        Register("aurora_path_fillrows_us_per_call", AuroraPathTimingProbe.Metric.FillRowsUsPerCall);
        Register("aurora_path_upload_us_per_call", AuroraPathTimingProbe.Metric.UploadUsPerCall);

        Register("aurora_path_total_us_max", AuroraPathTimingProbe.Metric.TotalUsMax);
        Register("aurora_path_bake_us_max", AuroraPathTimingProbe.Metric.BakeUsMax);
        Register("aurora_path_table_us_max", AuroraPathTimingProbe.Metric.TableUsMax);
        Register("aurora_path_upload_us_max", AuroraPathTimingProbe.Metric.UploadUsMax);
    }

    private static void Register(string name, AuroraPathTimingProbe.Metric metric) =>
        ProbeRegistry.Register(new AuroraPathTimingProbe(name, metric));
}
