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
        // §9's per-cell half, which the probe above cannot see: the baked wash alphas on one cell's
        // nine mesh vertices, for wall_wash_diamond.json. Offsets address that scenario's two wall
        // runs and must move with it.
        //
        // Three metrics per subject and not one, because the claim is a SHAPE. A wall reading 255 at
        // its centre is not by itself wrong — a wall in an unlit field should read 255 — so the centre
        // alone cannot tell the defect from a dark night. What is wrong is a centre out of step with
        // the corners around it, because four triangles fan out of that vertex and the disagreement
        // renders as a diamond. wash_*_diamond is that difference; the other two say which way it went.
        //
        // wallA is lit from BOTH sides, which is the isolated-spike case: with the wall judged by its
        // neighbours every one of its nine vertices is equal and the diamond is exactly 0. wallB is lit
        // from one side only, the case in the report — there the diamond is legitimately non-zero
        // either way, because a wall with a lit face and a dark face IS a gradient, and what changes is
        // that the tile stops being a dark hub in a lit field and becomes the ramp it should have been.
        //
        // The two ground probes are the controls that separate "the wall rule fired" from "the whole
        // wash moved": neither cell holds an edifice, so neither may move at all.
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_walla_centre", new IntVec3(0, 0, 55), NightWashVertexProbe.Metric.CentreAlpha));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_walla_corners", new IntVec3(0, 0, 55), NightWashVertexProbe.Metric.CornerMeanAlpha));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_walla_diamond", new IntVec3(0, 0, 55), NightWashVertexProbe.Metric.CentreExcess));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_wallb_centre", new IntVec3(0, 0, 35), NightWashVertexProbe.Metric.CentreAlpha));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_wallb_corners", new IntVec3(0, 0, 35), NightWashVertexProbe.Metric.CornerMeanAlpha));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_wallb_diamond", new IntVec3(0, 0, 35), NightWashVertexProbe.Metric.CentreExcess));
        // The roofed room, at map centre + (-40, 45): its north wall's middle cell and the interior
        // floor cell immediately inside it. The floor cell is the subject — an outdoor wall's own
        // pixels are never reached by this layer at all (see the scenario description), so what a
        // player sees is the half-cell of lit floor beside the wall, whose shared corner and edge
        // vertices average the wall's reading in. The wall's own vertices are recorded beside it to
        // say whether a ROOFED wall renders where an outdoor one does not.
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_room_wall_centre", new IntVec3(-40, 0, 52), NightWashVertexProbe.Metric.CentreAlpha));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_room_wall_corners", new IntVec3(-40, 0, 52), NightWashVertexProbe.Metric.CornerMeanAlpha));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_room_wall_diamond", new IntVec3(-40, 0, 52), NightWashVertexProbe.Metric.CentreExcess));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_room_floor_centre", new IntVec3(-40, 0, 51), NightWashVertexProbe.Metric.CentreAlpha));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_room_floor_corners", new IntVec3(-40, 0, 51), NightWashVertexProbe.Metric.CornerMeanAlpha));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_room_floor_diamond", new IntVec3(-40, 0, 51), NightWashVertexProbe.Metric.CentreExcess));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_lit_ground_diamond", new IntVec3(3, 0, 52), NightWashVertexProbe.Metric.CentreExcess));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_dark_ground_centre", new IntVec3(17, 0, 45), NightWashVertexProbe.Metric.CentreAlpha));
        ProbeRegistry.Register(new NightWashVertexProbe(
            "wash_dark_ground_diamond", new IntVec3(17, 0, 45), NightWashVertexProbe.Metric.CentreExcess));
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
        // Issue #196. The port's guard rail: aurora_shader_agreement renders CelestialAurora.shader
        // and compares it against AuroraCurtainHemRays, because moving the field into HLSL is the one
        // change in this repo that can be wrong while every offline test stays green — the tests
        // would be pinning a C# twin that no longer draws. aurora_shader_active says which renderer
        // is live, which any arm claiming to exercise the shader has to pin: the shader path degrades
        // to the bake silently by design, and an overlay run takes AssetBundles from the STALE main
        // checkout, so "I added a shader" and "the shader is running" are different facts.
        ProbeRegistry.Register(new AuroraShaderAgreementProbe());
        ProbeRegistry.Register(new AuroraShaderActiveProbe());
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
        // The other half of §15's seam fix, and the only numeric answer this repo has to "rain falls
        // through the ceiling". Patch_IndoorMaskOverage edits the geometry of vanilla's weather clip,
        // so the coverage that clip provides is measured rather than argued: indoor_mask_uncovered is
        // the defect count and must read 0 on every map with a roof on it.
        //
        // The other four exist because a lone zero is ambiguous. indoor_mask_overage says the clamp
        // actually ran (0 with eave_shadows on, vanilla's 0.16 off), so a scenario cannot pass by
        // measuring a patch that never applied. The gravship pair reads the same two numbers through
        // BakeGravshipIndoorMesh with the takeoff cutscene's own material, which is the one mask path
        // no harness frame can photograph — gravship_mask_overage holding at 0.16 while
        // indoor_mask_overage sits at 0 is what proves the clamp stops at the section mask.
        // indoor_mask_visible reports DebugViewSettings.drawShadows, the switch that deletes the mask
        // wholesale: with it off every other number here is measuring a layer that is not drawn.
        ProbeRegistry.Register(
            new IndoorMaskProbe("indoor_mask_uncovered", IndoorMaskProbe.Metric.UncoveredCells));
        ProbeRegistry.Register(
            new IndoorMaskProbe("indoor_mask_overage", IndoorMaskProbe.Metric.Overage));
        ProbeRegistry.Register(new IndoorMaskProbe(
            "gravship_mask_uncovered", IndoorMaskProbe.Metric.GravshipUncoveredCells));
        ProbeRegistry.Register(
            new IndoorMaskProbe("gravship_mask_overage", IndoorMaskProbe.Metric.GravshipOverage));
        ProbeRegistry.Register(
            new IndoorMaskProbe("indoor_mask_visible", IndoorMaskProbe.Metric.LayerVisible));
        // §27 vector lights. Four metrics off one class, and they are read together on purpose:
        // vector_light_shadow_fraction is the claim ("walls are blocking light"), and the other three
        // are what stop a zero in it being mistaken for a disproof — no emitters, no reach, or no mesh
        // each produce the same 0 for entirely different reasons.
        // The stress suite's palette check, and the reason it is three metrics rather than one.
        // glow_colour_overrides counts the lamps SetGlowColors touched; the two distinct-* metrics
        // count what §27's roster is holding. Only the second pair proves the recolour travelled —
        // a repaint that never reached the roster leaves the overrides high and the distinct count
        // at 1, which is a state no frame and no other probe here can tell from success.
        //
        // glow_emitter_radii is the companion warning: this repo has already shipped a per-emitter
        // texture overflow that a single-radius fixture could not have caught, because the gradient
        // and material caches are keyed per distinct integer radius. A stress scenario reading 1
        // here has five hundred lamps and one cache entry.
        ProbeRegistry.Register(new GlowPaletteProbe("glow_colour_overrides", GlowPaletteProbe.Metric.Overrides));
        ProbeRegistry.Register(new GlowPaletteProbe(
            "glow_emitter_colours", GlowPaletteProbe.Metric.DistinctEmitterColors));
        ProbeRegistry.Register(new GlowPaletteProbe(
            "glow_emitter_radii", GlowPaletteProbe.Metric.DistinctEmitterRadii));
        ProbeRegistry.Register(new VectorLightProbe("vector_light_count", VectorLightProbe.Metric.Count));
        ProbeRegistry.Register(new VectorLightProbe("vector_light_lit_area", VectorLightProbe.Metric.LitArea));
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_shadow_fraction", VectorLightProbe.Metric.ShadowFraction));
        ProbeRegistry.Register(new VectorLightProbe("vector_light_verts", VectorLightProbe.Metric.Vertices));
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_penumbra_area", VectorLightProbe.Metric.PenumbraArea));
        // The draw takes its polygon from the field instead of building a second copy of it, and
        // this is what says the two are the same polygon. Pinned at 0 with tolerance 0 — the claim
        // is bit-identity, not closeness — and it reads -1 rather than 0 when there was nothing
        // cached to compare, so a scenario that stopped baking fails here instead of passing.
        ProbeRegistry.Register(new VectorLightProbe(
            "vector_light_polygon_reuse_error", VectorLightProbe.Metric.PolygonReuseError));
        // Bake accounting for the vector lights (§27 P2 phase 6). Counters the bake itself wrote,
        // NOT a recomputation — see VectorLightBakeProbe's header. The pair to read together is
        // vector_light_bakes and vector_light_bake_segments: a bake count is only interpretable
        // against the wall population it was measured over, because the ray cull's whole gain scales
        // with clutter and a healthy-looking count over an empty window has verified nothing.
        //
        // vector_light_marks_per_call is the invalidation radius, measured. The epic names
        // MarkGeometryDirtyAround as the suspect that turns one lamp toggle into a map-wide rebake;
        // read against vector_light_emitters, this says whether "only the lights that can see the
        // cell" holds in a real scene or only in the comment.
        ProbeRegistry.Register(
            new VectorLightBakeProbe("vector_light_bakes", VectorLightBakeProbe.Metric.Bakes));
        ProbeRegistry.Register(
            new VectorLightBakeProbe("vector_light_bake_hits", VectorLightBakeProbe.Metric.Hits));
        ProbeRegistry.Register(
            new VectorLightBakeProbe("vector_light_bake_segments", VectorLightBakeProbe.Metric.Segments));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_segments_per_bake", VectorLightBakeProbe.Metric.SegmentsPerBake));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_invalidations", VectorLightBakeProbe.Metric.InvalidationCalls));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_invalidation_marks", VectorLightBakeProbe.Metric.InvalidationMarks));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_marks_per_call", VectorLightBakeProbe.Metric.MarksPerCall));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_roster_resyncs", VectorLightBakeProbe.Metric.RosterResyncs));
        // Issue #188 item B. Pin it WITH vector_light_bakes or not at all: on its own a deferral
        // count cannot tell a working view cull from a scene whose lamps all happen to be off
        // screen. It counts ATTEMPTS rather than emitters -- the cull is re-evaluated every frame,
        // so one deferred emitter charges one per frame -- which is why the arms assert that the
        // emitter baked in one and not the other, rather than that the two numbers sum to a
        // constant.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_bake_deferrals", VectorLightBakeProbe.Metric.Deferrals));
        // The threaded bake. PIN ALL THREE OR NONE. A fan-out count on its own cannot separate "the
        // flag is off" from "no frame in this scenario ever had four dirty emitters at once", which
        // is the normal state of a colony and therefore the state most arms are in; the serial count
        // says the batching ran at all, and the batch maximum says whether the threaded path was
        // handed enough work for its cost to be a measurement rather than a branch taken.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_parallel_bakes", VectorLightBakeProbe.Metric.ParallelBakePasses));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_serial_bakes", VectorLightBakeProbe.Metric.SerialBakePasses));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_bake_batch_max", VectorLightBakeProbe.Metric.LargestBakeBatch));
        // RECORDED, NEVER PINNED TIGHTLY. It is a duration on a contended box, which this repo has
        // measured moving by a factor of two across one unchanged binary; it earns its place because
        // it is the one number in the bank that a threaded bake can move at all.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_bake_wall_ms", VectorLightBakeProbe.Metric.BakeWallMs));
        // Issue #188 item C, and the same rule as the three above: PIN BOTH COUNTS OR NEITHER. A hit
        // count on its own reads as a working memo in a scene where nothing ever asks twice, and a
        // rebuild count on its own reads as a broken one in a scene that is genuinely building
        // walls. Their sum is how many occluder sets were assembled, which is what turns either of
        // them into a share.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_silhouette_hits", VectorLightBakeProbe.Metric.SilhouetteHits));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_silhouette_rebuilds", VectorLightBakeProbe.Metric.SilhouetteRebuilds));
        // RECORDED, NEVER PINNED TIGHTLY, for the same reason as vector_light_bake_wall_ms: a
        // duration on a contended box. It is the OTHER half of the same window -- the gather, where
        // the memo works -- and the two are worth reading together, because a change that touches
        // one leaves the other standing as a control on how loaded the machine was that run.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_gather_wall_ms", VectorLightBakeProbe.Metric.GatherWallMs));
        // The third clock. Read all three together or none: they partition one frame's vector-light
        // work into the half that reads the map, the half that does arithmetic on it, and the half
        // that hands the result to Unity, and a change to any one of them is only interpretable
        // against what the other two did on the same run.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_upload_wall_ms", VectorLightBakeProbe.Metric.UploadWallMs));
        // The same total split by API, because they are not the same cost: a mesh channel write
        // copies a managed list into native memory, and Texture2D.Apply is a GPU transfer. Optimising
        // the smaller of the two is the documented way to spend a day here for nothing.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_upload_mesh_ms", VectorLightBakeProbe.Metric.UploadMeshWallMs));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_upload_field_ms", VectorLightBakeProbe.Metric.UploadFieldWallMs));
        // How the field refreshes split. PIN BOTH OR NEITHER: a UV-only count on its own reads as a
        // working hold in a scene where nothing ever refreshes twice, and a texture-upload count on
        // its own reads as a broken one in a scene where vanilla's glow really is moving.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_field_texture_uploads", VectorLightBakeProbe.Metric.FieldTextureUploads));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_field_uv_only_uploads", VectorLightBakeProbe.Metric.FieldUvOnlyUploads));
        // Issue #188 item 0. vector_light_sections_per_pass is the headline -- the map's whole
        // section count before item A, a handful after -- but pin vector_light_mask_applies beside
        // it or the reduction is unfalsifiable. Dirty flags are work REQUESTED and vanilla
        // regenerates only what is in view, so flags can fall fifty-fold while the work does not
        // move at all, and that outcome would mean the saving was on sections nobody was looking at.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_section_dirties", VectorLightBakeProbe.Metric.SectionDirties));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_section_dirty_passes", VectorLightBakeProbe.Metric.SectionDirtyPasses));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_sections_per_pass", VectorLightBakeProbe.Metric.SectionsPerPass));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_mask_applies", VectorLightBakeProbe.Metric.MaskApplies));
        // The one metric here that is a DEFECT COUNT rather than a workload figure: sections that
        // baked with an emitter reaching them dropped for want of a polygon, i.e. frames that
        // rendered with a shadow missing. Its partner says the fallback was exercised at all, which
        // is what stops "skips are zero" from passing against a scene where nothing ever moved.
        // Read both AFTER a bake_reset -- nothing is baked at map load and the counts start high.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_mask_skips_dirty", VectorLightBakeProbe.Metric.MaskSkipsNoPolygon));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_mask_stale_polys", VectorLightBakeProbe.Metric.MaskStalePolygonUses));
        // Bakes that changed nothing anybody can see. PIN IT BESIDE vector_light_bakes: the whole
        // claim of the changed-dirty feature is that the bake count stands still while the section
        // count falls, i.e. that the saving came from dirtying less rather than from baking less,
        // and neither number can say that on its own.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_unchanged_bakes", VectorLightBakeProbe.Metric.UnchangedBakes));
        // What a door swing's blocker write would have cost, counted rather than inferred. Read as a
        // RATIO of each other -- calls is how often we provoke vanilla, sections is what it costs --
        // and beside vector_light_section_dirties, which is what we flag on purpose. Both read 0 with
        // the suppression off, which is also what a scenario that never moved a door reports, so
        // neither means anything alone.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_suppressed_dirty_calls",
            VectorLightBakeProbe.Metric.SuppressedDirtyCalls));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_suppressed_dirty_sections",
            VectorLightBakeProbe.Metric.SuppressedDirtySections));
        ProbeRegistry.Register(
            new VectorLightBakeProbe("vector_light_emitters", VectorLightBakeProbe.Metric.Emitters));
        // The coverage grid, which until now nothing live could see at all -- every other shape
        // probe recomputes from the visibility polygon, so a change that rewrote every grid byte
        // moved nothing in any scenario. Pin both together: lit cells is what the nearest-ray bound
        // writes directly, and the mean is what the farthest-ray bound and the shadow edges move.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_coverage_mean", VectorLightBakeProbe.Metric.CoverageMean));
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_coverage_lit_cells", VectorLightBakeProbe.Metric.LitCells));
        // The grid's SIZE, which the two above cannot report between them: both are predicates over
        // the bytes, so both move when a grid merely saturates. This is what lamp glow reach is
        // measured against — the claim that stretching a lamp's radius does not enlarge its coverage
        // bake (VectorLightReachMath.CoverageRadius) is about allocation, not about content.
        ProbeRegistry.Register(new VectorLightBakeProbe(
            "vector_light_coverage_cells", VectorLightBakeProbe.Metric.CoverageCells));
        // Reads 0 and zeroes the counters, so the counting window can be opened at the same step as
        // the profiling window. See the metric's comment for the mismatch that provoked it.
        ProbeRegistry.Register(
            new VectorLightBakeProbe("vector_light_bake_reset", VectorLightBakeProbe.Metric.Reset));
        // §27 phase 3. Pin this at 1 in any arm claiming to measure the mask: the per-emitter glow
        // arrays are private fields read by reflection, and failing to read them is a defined
        // stand-down rather than an error, so an unpinned arm photographs the crossfade instead.
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_mask_available", VectorLightProbe.Metric.MaskAvailable));
        // §27 phase 6. Registered next to the phase 5 pair because a scenario shooting the
        // per-fragment max needs all three: available says the shader loaded, and lift_samples says
        // whether the per-cell path stood down for it as it is supposed to.
        ProbeRegistry.Register(new VectorLightProbe(
            "vector_light_shader_max_available", VectorLightProbe.Metric.ShaderMaxAvailable));
        // §27 phase 5's max. Registered as a pair and pinned as a pair — see the Metric comments:
        // samples at 0 means the composition never ran, peak at 0 with samples healthy means it ran
        // and correctly found nothing, and those are the two results this arm is trying to tell
        // apart.
        ProbeRegistry.Register(new VectorLightProbe(
            "vector_light_mask_lift_samples", VectorLightProbe.Metric.MaskLiftSamples));
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_mask_lift_peak", VectorLightProbe.Metric.MaskLiftPeak));
        // §27 phase 5b's three. Pinned together for the reason the Metric comments give: saturated
        // samples at 0 is the correct reading for a one-torch scene and a dead bake for a six-torch
        // ring, and only building the ring on purpose tells them apart. Skipped is the fallback
        // counter — a run where it dominates is measuring mixed-hue emitters rather than the fix.
        ProbeRegistry.Register(new VectorLightProbe(
            "vector_light_mask_saturated_samples", VectorLightProbe.Metric.MaskSaturatedSamples));
        ProbeRegistry.Register(new VectorLightProbe(
            "vector_light_mask_saturation_skipped", VectorLightProbe.Metric.MaskSaturationSkipped));
        ProbeRegistry.Register(new VectorLightProbe(
            "vector_light_mask_saturation_relief", VectorLightProbe.Metric.MaskSaturationRelief));
        // §27 phase 5b, vector_light_column.json. The rendered level at four cells around the
        // one-cell wall column, local to that scenario's anchor at (0, 45). `column_behind` is the
        // cell the whole phase is about: one step west of the column, permanently invisible to the
        // torch four cells east of it, and therefore the cell whose level must not fall when a torch
        // is added somewhere else entirely.
        //
        // LEVEL RATHER THAN LUMINANCE, and the Metric comment carries why: the monotonicity property
        // is false per channel for VANILLA, so a weighted mix of the three would fail the oracle
        // before it reached our arithmetic. The max channel is the only monotone summary, and it is
        // also the one GroundGlowAt itself reads.
        ProbeRegistry.Register(new RenderedLightCellProbe(
            "column_behind", new IntVec3(-1, 0, 45), RenderedLightCellProbe.Metric.Level));
        ProbeRegistry.Register(new RenderedLightCellProbe(
            "column_behind_far", new IntVec3(-3, 0, 45), RenderedLightCellProbe.Metric.Level));
        ProbeRegistry.Register(new RenderedLightCellProbe(
            "column_north", new IntVec3(0, 0, 46), RenderedLightCellProbe.Metric.Level));
        ProbeRegistry.Register(new RenderedLightCellProbe(
            "column_northwest", new IntVec3(-1, 0, 46), RenderedLightCellProbe.Metric.Level));
        // vector_light_sun_lamp.json, the mixed-hue sweep, local to the same anchor at (0, 45).
        // `sunlamp_lit` is the cell the bug report is about: one step EAST of the column, hidden
        // from the sun lamp four cells west of it and lit by the torch two cells further east — a
        // cell where a sun lamp's shadow lands on top of another lamp's light. `sunlamp_lit_far`
        // is past that torch and still in the same shadow. `sunlamp_open` is the control, four
        // cells north of the column with nothing between it and the sun lamp, so no arm may move
        // it: an arm that darkens the control is darkening the room rather than fixing a shadow.
        ProbeRegistry.Register(new RenderedLightCellProbe(
            "sunlamp_lit", new IntVec3(1, 0, 45), RenderedLightCellProbe.Metric.Level));
        ProbeRegistry.Register(new RenderedLightCellProbe(
            "sunlamp_lit_far", new IntVec3(3, 0, 45), RenderedLightCellProbe.Metric.Level));
        ProbeRegistry.Register(new RenderedLightCellProbe(
            "sunlamp_open", new IntVec3(0, 0, 49), RenderedLightCellProbe.Metric.Level));
        // vector_light_bake_flicker.json, anchored at (0, 45) like its neighbours. The torch stands
        // at local (-6, 0) with a single wall cell at (-4, 0); `flicker_shadow` is two steps behind
        // that wall, so it is inside the torch's radius and inside the shadow the wall throws --
        // the cell whose whole value comes from §27 having masked vanilla's flood off it.
        // `flicker_lit` is the control on the torch's other side, with nothing between: no arm may
        // move it, and an arm that does is dimming the room rather than restoring a shadow.
        ProbeRegistry.Register(new RenderedLightCellProbe(
            "flicker_shadow", new IntVec3(-2, 0, 45), RenderedLightCellProbe.Metric.Level));
        ProbeRegistry.Register(new RenderedLightCellProbe(
            "flicker_lit", new IntVec3(-8, 0, 45), RenderedLightCellProbe.Metric.Level));
        // Issue #218's instrument. Everything above reads ONE cell at the moment a Probe step runs,
        // which is what let a one-frame defect survive every scenario in this repo: they all capture
        // after a settle and all of them therefore read the correct final value. This one samples
        // from inside the render loop, so it sees every frame of a transition rather than the frames
        // steps happened to land on, and it folds a whole box so a scenario does not have to guess
        // which cell overshoots.
        //
        // ARMED BY THE ArmSwingSample STEP, not here: the box is a scene coordinate and belongs next
        // to the room it describes.
        //
        // THE FOUR SUPPORTING METRICS ARE NOT OPTIONAL FURNITURE. `excursion` reads zero for a fixed
        // build AND for a run where nothing happened, an instrument that never installed, or a box
        // over dead ground. `span` pinned above a floor says the swing moved something, `frames`
        // above a floor says the sampler ran, `rejected` at zero says it could read its subject, and
        // the two axis probes say WHERE the worst cell was so a non-zero reading names a place
        // instead of starting an argument.
        VectorLightSwingSampler.Install();
        ProbeRegistry.Register(new VectorLightSwingProbe(
            "vector_light_swing_excursion", VectorLightSwingProbe.Metric.Excursion));
        ProbeRegistry.Register(new VectorLightSwingProbe(
            "vector_light_swing_excursion_x", VectorLightSwingProbe.Metric.ExcursionX));
        ProbeRegistry.Register(new VectorLightSwingProbe(
            "vector_light_swing_excursion_z", VectorLightSwingProbe.Metric.ExcursionZ));
        ProbeRegistry.Register(new VectorLightSwingProbe(
            "vector_light_swing_span", VectorLightSwingProbe.Metric.Span));
        ProbeRegistry.Register(new VectorLightSwingProbe(
            "vector_light_swing_frames", VectorLightSwingProbe.Metric.Frames));
        ProbeRegistry.Register(new VectorLightSwingProbe(
            "vector_light_swing_rejected", VectorLightSwingProbe.Metric.Rejected));
        // §27e, vector_light_open_door.json. Cells are local to that scenario's room at offset
        // (0, 45): the doorway is local (0, 0), so (1, 0) is the first cell OUTSIDE it and (-1, 0)
        // the first cell inside. Both read vanilla's gameplay light, not ours -- they are what
        // separates "we drew a beam" from "we moved the glow grid", which no vector_light_* probe
        // can do because the drawn polygon is identical in both arms.
        ProbeRegistry.Register(
            new GlowGridCellProbe("door_outside_ground_glow", new IntVec3(1, 0, 45)));
        ProbeRegistry.Register(
            new GlowGridCellProbe("door_inside_ground_glow", new IntVec3(-1, 0, 45)));
        // Issue #174 phase 3, vector_light_wall_lamp.json. The wall lamp's own cell, local (-2, 0)
        // of the same room, i.e. two cells inside the doorway on the wall opposite it.
        //
        // A POWER CHECK WEARING A LIGHTING PROBE'S CLOTHES, and it is registered because the failure
        // it guards against is indistinguishable from the bug being investigated. The scenario runs
        // a real WallLamp off a real generator, so "the lamp casts no rays" and "the conduit never
        // energised, or the clock never ticked far enough for PowerNet to notice" produce the same
        // black frame and the same zero from every vector_light_* probe. Vanilla's own glow at the
        // emitter is the one number that is high when the lamp is lit whatever our polygon does.
        ProbeRegistry.Register(
            new GlowGridCellProbe("wall_lamp_cell_glow", new IntVec3(-2, 0, 45)));
        // The same three readings over that scenario's SECOND room, ten cells south, whose lamp
        // hangs on the door's own wall instead of the one opposite it. Its doorway is local (0, -10),
        // i.e. offset (0, 35) from centre, and its lamp is one cell north of that at (-1, 36).
        ProbeRegistry.Register(
            new GlowGridCellProbe("wall_lamp2_cell_glow", new IntVec3(-1, 0, 36)));
        ProbeRegistry.Register(
            new GlowGridCellProbe("door2_outside_ground_glow", new IntVec3(1, 0, 35)));
        ProbeRegistry.Register(
            new GlowGridCellProbe("door2_inside_ground_glow", new IntVec3(-1, 0, 35)));
        // §27e phase 2, vector_light_door_aperture.json. The door sits at local (0, 0) of that
        // scenario's room, i.e. offset (0, 45) from centre.
        ProbeRegistry.Register(new DoorApertureProbe(
            "door_aperture", DoorApertureProbe.Metric.Aperture, new IntVec3(0, 0, 45)));
        ProbeRegistry.Register(new DoorApertureProbe(
            "door_aperture_watched", DoorApertureProbe.Metric.Watched, IntVec3.Zero));
        ProbeRegistry.Register(new DoorApertureProbe(
            "door_aperture_bakes", DoorApertureProbe.Metric.DirtyRequests, IntVec3.Zero));
        // §27 phase 4 / issue #159. The rectangle a colonist's lamp shadow is actually built from,
        // which has to be the one vanilla's sun shadow is built from or the two leave the pawn at
        // different points. Pin BOTH in any arm that photographs a pawn shadow: they failed
        // independently, and each is invisible in the other's number.
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_pawn_anchor_z", VectorLightProbe.Metric.PawnShadowAnchorZ));
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_pawn_width", VectorLightProbe.Metric.PawnShadowWidth));
        // The other half of #159: not what shape the shadow is, but whether it is drawn at all.
        // Vanilla refuses one for four non-sun reasons and §27 asked none of them.
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_pawn_casters", VectorLightProbe.Metric.PawnShadowCasters));
        // §27 phase 4b. Registered as a PAIR, and scenarios should pin both: the fix drives the peak
        // arm down while leaving the rosette almost where it was, so either number on its own is
        // consistent with the feature being broken.
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_pawn_shadow_peak", VectorLightProbe.Metric.PawnShadowPeak));
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_pawn_shadow_rosette", VectorLightProbe.Metric.PawnShadowRosette));
        // How many arms that rosette is made of. Pin it in any arm whose point is that lamps SHARE a
        // pawn: with one caster the ground-share denominator is the blocked beam by identity, so a
        // scene that ends up single-lamp measures nothing while staying green in every other number.
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_pawn_shadow_arms", VectorLightProbe.Metric.PawnShadowArms));
        // Issue #166's metric: how far the longest arm reaches, in cells beyond the caster. Pinned
        // in a scenario that puts a wall inside that reach, where an unclipped shadow reads the full
        // geometric length and a clipped one reads the distance to the wall.
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_pawn_shadow_reach", VectorLightProbe.Metric.PawnShadowReach));
        // The along-length fade's own number, because peak and rosette are both defined at the
        // caster and so cannot see it -- see the metric's comment. Pin it beside them: together they
        // say "the shadow starts exactly as dark as it did and ends fainter", which is the whole
        // claim, and either number alone is consistent with the feature being broken.
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_pawn_shadow_tip_fade",
                VectorLightProbe.Metric.PawnShadowTipFade));
        // Registered as a PAIR with the tip, and scenarios should pin both: the curve ends at zero
        // by construction, so the tip says only "something fades" while the midpoint says which
        // curve reached the GPU.
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_pawn_shadow_mid_fade",
                VectorLightProbe.Metric.PawnShadowMidFade));
        // What the draw resolves for a NAMED animal kind, which reaches its shadow data by a
        // different route than a colonist and was therefore getting the human-shaped fallback: a cat
        // drawn 0.8 tall and 0.3 half-wide against a real 0.3 and 0.125. Pin both, in a view holding
        // the animal.
        //
        // THE KIND IS IN THE PROBE NAME, deliberately. These first selected the lowest-thing-ID
        // animal in view, which made the answer depend on the order a scenario spawned its animals
        // in -- a file with a cat and a squirrel read the cat only because the cat was written
        // first. A thing ID cannot be written down (they differ every run), so the kind is the
        // direct way to say which caster is meant. Registering one probe per kind rather than
        // parameterising at the scenario keeps the pin self-describing: a reader of the JSON can see
        // it is about a cat.
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_cat_caster_height",
                VectorLightProbe.Metric.AnimalCasterHeight, "Cat"));
        ProbeRegistry.Register(
            new VectorLightProbe("vector_light_cat_caster_half_width",
                VectorLightProbe.Metric.AnimalCasterHalfWidth, "Cat"));
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

        // OUR OWN POSTFIX, armed directly. Arming vanilla's Regenerate measures the whole bake, of
        // which §27 is a small share of a large number — 795 ms of vanilla for 112 sections. Harmony
        // emits a call to the postfix rather than inlining it, so instrumenting the postfix itself
        // isolates our cost from vanilla's and from every other mod patching the same method.
        const string suppress = "CelestialLighting.Patch_VectorLightSuppress";
        ProbeRegistry.Register(new CircinusProbe("circinus_ours_patched", CircinusProbe.Metric.Patched, suppress, "Postfix"));
        ProbeRegistry.Register(new CircinusProbe("circinus_ours_calls", CircinusProbe.Metric.Calls, suppress, "Postfix"));
        ProbeRegistry.Register(new CircinusProbe("circinus_ours_total_ms", CircinusProbe.Metric.TotalMs, suppress, "Postfix"));
        ProbeRegistry.Register(new CircinusProbe("circinus_ours_max_ms", CircinusProbe.Metric.MaxMs, suppress, "Postfix"));

        // §7b's postfix, armed directly, for the same reason the §27 block above arms its own: the
        // whole-bake row cannot separate our cost from the 795 ms vanilla spends baking 112 sections.
        //
        // WHY A SECOND ARM ON THE SAME VANILLA METHOD. Both this and circinus_ours_* postfix
        // SectionLayer_LightingOverlay.Regenerate, so a scenario reading both gets two of the three
        // terms in circinus_regen_* separately — which is what the §16 fan-out table could never do.
        //
        // The A/B this exists for compares the SAME arm across two BUILDS, not two arms in one run,
        // and that is the only comparison the numbers support: Circinus's instrumentation overhead is
        // roughly fixed per call, so it cancels between builds that make the same number of calls (a
        // whole-map rebake bakes the same sections either way) and does not cancel between one method
        // and another. Read total_ms/calls, not avg_ms: a rebake is not guaranteed to bake the same
        // number of sections twice, and a per-cycle mean silently mixes "cheaper per call" with
        // "called fewer times". circinus_occl_calls is pinned in the scenario for exactly that.
        const string occlusion = "CelestialLighting.Patch_IndoorSkyOcclusion";
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_patched", CircinusProbe.Metric.Patched, occlusion, "Postfix"));
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_calls", CircinusProbe.Metric.Calls, occlusion, "Postfix"));
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_total_ms", CircinusProbe.Metric.TotalMs, occlusion, "Postfix"));
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_max_ms", CircinusProbe.Metric.MaxMs, occlusion, "Postfix"));
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_max_calls", CircinusProbe.Metric.MaxCallsPerCycle, occlusion, "Postfix"));

        // A CHILD ARM, AND CHILD ARMS ARE FOR FINDING RATHER THAN JUDGING. Instrumenting a method
        // called from inside an already-armed one charges the child's own instrumentation to the
        // parent, so the parent's total inflates and the two rows must never be read as a clean
        // split. What this arm is for is the one question the postfix's own row cannot answer:
        // whether the window FILL — the part that could move to another thread — is most of the
        // postfix or a corner of it. The two mesh passes and the mesh.colors32 round trip cannot move
        // anywhere, so a gather phase is only worth building if this row is large.
        //
        // Read it in a run of its own and compare against an unarmed run's circinus_occl_total_ms to
        // see the inflation; do not compare arm to arm inside one run, and do not leave it armed for
        // the A/B that judges the change.
        //
        // DO NOT ARM THIS WITH THE GATHER PHASE ON. Once SkyOcclusionGather is running, BuildWindow is
        // called from worker threads, and arming it means Circinus's own instrumentation runs off the
        // main thread — which is neither something Circinus promises nor something this repo has any
        // reason to find out about the hard way. The number this arm exists for was taken on the
        // serial build, before the phase existed, and that is the only build it is safe on. The A/B
        // that judges the phase arms circinus_mesh_* and circinus_occl_*, both main-thread only.
        //
        // BuildWindow is private and Circinus arms it by name through AccessTools, which finds
        // non-public members — but a method small enough for the JIT to have inlined it before
        // Circinus arms reads zero calls and is indistinguishable from one that never ran. Pin
        // circinus_occl_window_calls against circinus_occl_calls: BuildWindow is called exactly once
        // per postfix, so anything other than equality means the arm did not take.
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_window_patched", CircinusProbe.Metric.Patched, occlusion, "BuildWindow"));
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_window_calls", CircinusProbe.Metric.Calls, occlusion, "BuildWindow"));
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_window_total_ms", CircinusProbe.Metric.TotalMs, occlusion, "BuildWindow"));
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_window_max_ms", CircinusProbe.Metric.MaxMs, occlusion, "BuildWindow"));
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_window_reset", CircinusProbe.Metric.Reset, occlusion, "BuildWindow"));

        // THE WHOLE FRAME'S MAP UPDATE — the scope at which a frame actually drops.
        //
        // Map.MapUpdate is everything this map does in one Update: the sky manager, the glow grid, the
        // region rebuild, mapDrawer.MapMeshDrawerUpdate_First, DrawMapMesh, dynamic things, the
        // condition draws. A Circinus cycle IS a rendered frame, so MaxMs here is the worst single
        // frame's map update, and 16.67 ms is the 60 fps budget it has to fit inside.
        //
        // WHY THIS EXISTS ALONGSIDE circinus_mesh_*. The narrower arm can only see what happens inside
        // the mesh update, so it cannot answer "would this trigger have had more time to bake" — the
        // question that decides between the two gather triggers. This one can, because anything a
        // trigger gained by starting earlier would have to show up as time recovered somewhere else in
        // the same frame. (It cannot, in fact: CloudBake.Rows ends in Parallel.For, which BLOCKS the
        // calling thread until every worker is done, so both triggers put the same synchronous wait
        // inside the same method and neither runs ahead in the background. The arm is here to test
        // that argument rather than to assert it.)
        const string mapType = "Verse.Map";
        ProbeRegistry.Register(new CircinusProbe("circinus_frame_patched", CircinusProbe.Metric.Patched, mapType, "MapUpdate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_frame_calls", CircinusProbe.Metric.Calls, mapType, "MapUpdate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_frame_total_ms", CircinusProbe.Metric.TotalMs, mapType, "MapUpdate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_frame_max_ms", CircinusProbe.Metric.MaxMs, mapType, "MapUpdate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_frame_avg_ms", CircinusProbe.Metric.AvgMs, mapType, "MapUpdate"));
        ProbeRegistry.Register(new CircinusProbe("circinus_frame_reset", CircinusProbe.Metric.Reset, mapType, "MapUpdate"));

        // THE FRAME, not the call — the one arm that can judge the gather phase.
        //
        // MapDrawer.MapMeshDrawerUpdate_First is the call that runs vanilla's whole regenerate loop:
        // it walks every section, and Section.TryUpdate regenerates every visible dirty layer of every
        // visible dirty section before it returns. The gather phase runs as a Prefix on this same
        // method, so BOTH halves of the change are inside this arm — the batch build we added and the
        // per-section work we took out of the postfixes underneath it. The main thread blocks on the
        // parallel fill, so its wall time is counted here honestly rather than disappearing onto the
        // workers.
        //
        // WHY circinus_occl_* CANNOT JUDGE THIS ON ITS OWN, and would flatter it badly. With the phase
        // on, the postfix no longer contains the window fill, so its row falls by construction whether
        // or not the frame got any shorter — the work moved, and an arm on the place it moved out of
        // can only ever report a win. MaxMs here is the worst frame of the thing that actually
        // stutters; read that first and the postfix row second.
        //
        // Registered on the gather branch and cherry-picked back onto the baseline's probe bridge, so
        // the same instrument reads both builds. An arm that exists in only one of two builds is not a
        // comparison.
        const string meshDrawer = "Verse.MapDrawer";
        ProbeRegistry.Register(new CircinusProbe("circinus_mesh_patched", CircinusProbe.Metric.Patched, meshDrawer, "MapMeshDrawerUpdate_First"));
        ProbeRegistry.Register(new CircinusProbe("circinus_mesh_calls", CircinusProbe.Metric.Calls, meshDrawer, "MapMeshDrawerUpdate_First"));
        ProbeRegistry.Register(new CircinusProbe("circinus_mesh_total_ms", CircinusProbe.Metric.TotalMs, meshDrawer, "MapMeshDrawerUpdate_First"));
        ProbeRegistry.Register(new CircinusProbe("circinus_mesh_max_ms", CircinusProbe.Metric.MaxMs, meshDrawer, "MapMeshDrawerUpdate_First"));
        ProbeRegistry.Register(new CircinusProbe("circinus_mesh_reset", CircinusProbe.Metric.Reset, meshDrawer, "MapMeshDrawerUpdate_First"));

        // Indoor sky occlusion's gather phase, by counter rather than by clock. See
        // SkyOcclusionGatherProbe for why a timing probe cannot see this feature fail: a phase that
        // stops matching produces the right pixels at the old cost and reads as "no regression".
        ProbeRegistry.Register(new SkyOcclusionGatherProbe("occl_gather_passes", SkyOcclusionGatherProbe.Metric.Passes));
        ProbeRegistry.Register(new SkyOcclusionGatherProbe("occl_gather_sections", SkyOcclusionGatherProbe.Metric.Sections));
        ProbeRegistry.Register(new SkyOcclusionGatherProbe("occl_gather_hits", SkyOcclusionGatherProbe.Metric.Hits));
        ProbeRegistry.Register(new SkyOcclusionGatherProbe("occl_gather_misses", SkyOcclusionGatherProbe.Metric.Misses));
        ProbeRegistry.Register(new SkyOcclusionGatherProbe("occl_gather_hit_fraction", SkyOcclusionGatherProbe.Metric.HitFraction));
        ProbeRegistry.Register(new SkyOcclusionGatherProbe("occl_gather_sections_per_pass", SkyOcclusionGatherProbe.Metric.SectionsPerPass));
        ProbeRegistry.Register(new SkyOcclusionGatherProbe("occl_gather_reset", SkyOcclusionGatherProbe.Metric.Reset));

        // The barrier between the discarded warm-up rebake and the measured one. CollectStatistics
        // accumulates across Circinus's whole 2000-cycle ring, so without a reset the measured rebake
        // would be reported with the feature-off rebake that preceded it still inside it — which
        // flatters whichever build ran the cheap early-returning pass more recently.
        ProbeRegistry.Register(new CircinusProbe("circinus_occl_reset", CircinusProbe.Metric.Reset, occlusion, "Postfix"));

        // One recorded run per arm. The label is the only thing separating one run document from
        // another, so there is a start probe per arm rather than one taking an argument — the
        // scenario language has no way to pass a string, and an arm whose document is mislabelled is
        // worse than one that was never recorded.
        foreach (string armName in new[] { "gated", "crossfade", "mask", "combo", "cull", "brute", "bounds" })
        {
            ProbeRegistry.Register(new CircinusProbe(
                "circinus_run_start_" + armName, CircinusProbe.Metric.RunStart,
                label: "celestiallighting-" + armName));
        }

        ProbeRegistry.Register(new CircinusProbe("circinus_run_stop", CircinusProbe.Metric.RunStop));

        // The POLYGON bake path, for the ray-cull work (§27 P2 phase 6). Offline the visibility
        // polygon is 83-94% of a bake and quadratic in the wall count; these arms are what say
        // whether that holds in Mono and whether the cull's 5-8x transfers.
        //
        // TWO LEVELS, AND ONLY ONE OF THEM DECIDES ANYTHING. EnsurePolygon is the PARENT — it
        // contains the silhouette extraction, the polygon and the coverage grid — and it is the arm
        // to A/B between builds, because it is where a saving has to show up to be real. Build,
        // SegmentsAround and BuildCoverage are child arms and exist to apportion the parent, not to
        // be compared across builds: Circinus instruments by transpiling, so its per-call overhead
        // inflates small methods, and the change under test REMOVES ray-segment solves rather than
        // calls, which is the case where a child arm flatters itself. Check the children sum to the
        // parent before believing either.
        const string field = "CelestialLighting.VectorLightField";
        ProbeRegistry.Register(new CircinusProbe("circinus_bake_patched", CircinusProbe.Metric.Patched, field, "EnsurePolygon"));
        ProbeRegistry.Register(new CircinusProbe("circinus_bake_calls", CircinusProbe.Metric.Calls, field, "EnsurePolygon"));
        ProbeRegistry.Register(new CircinusProbe("circinus_bake_total_ms", CircinusProbe.Metric.TotalMs, field, "EnsurePolygon"));
        ProbeRegistry.Register(new CircinusProbe("circinus_bake_max_ms", CircinusProbe.Metric.MaxMs, field, "EnsurePolygon"));
        ProbeRegistry.Register(new CircinusProbe("circinus_bake_reset", CircinusProbe.Metric.Reset, field, "EnsurePolygon"));

        const string polygon = "CelestialLighting.VectorLightMath";
        ProbeRegistry.Register(new CircinusProbe("circinus_poly_patched", CircinusProbe.Metric.Patched, polygon, "Build"));
        ProbeRegistry.Register(new CircinusProbe("circinus_poly_calls", CircinusProbe.Metric.Calls, polygon, "Build"));
        ProbeRegistry.Register(new CircinusProbe("circinus_poly_total_ms", CircinusProbe.Metric.TotalMs, polygon, "Build"));
        ProbeRegistry.Register(new CircinusProbe("circinus_silh_patched", CircinusProbe.Metric.Patched, "CelestialLighting.VectorLightBlockers", "SegmentsAround"));
        ProbeRegistry.Register(new CircinusProbe("circinus_cover_patched", CircinusProbe.Metric.Patched, polygon, "BuildCoverage"));
        ProbeRegistry.Register(new CircinusProbe("circinus_silh_total_ms", CircinusProbe.Metric.TotalMs, "CelestialLighting.VectorLightBlockers", "SegmentsAround"));
        ProbeRegistry.Register(new CircinusProbe("circinus_silh_calls", CircinusProbe.Metric.Calls, "CelestialLighting.VectorLightBlockers", "SegmentsAround"));
        ProbeRegistry.Register(new CircinusProbe("circinus_cover_total_ms", CircinusProbe.Metric.TotalMs, polygon, "BuildCoverage"));
        ProbeRegistry.Register(new CircinusProbe("circinus_cover_calls", CircinusProbe.Metric.Calls, polygon, "BuildCoverage"));

        // Sub-method breakdown of the bake. Registered after three speculative optimisations to the
        // mask moved its cost by nothing at all — a coverage cache, a per-frame reader cache and a
        // loop inversion, none of which touched the number. Guessing where the time goes has now
        // cost more than measuring it would have, so this measures it.
        const string mask = "CelestialLighting.VectorLightMask";
        ProbeRegistry.Register(new CircinusProbe("circinus_apply_total_ms", CircinusProbe.Metric.TotalMs, mask, "Apply"));
        ProbeRegistry.Register(new CircinusProbe("circinus_apply_calls", CircinusProbe.Metric.Calls, mask, "Apply"));
        ProbeRegistry.Register(new CircinusProbe("circinus_shadow_total_ms", CircinusProbe.Metric.TotalMs, mask, "BuildCellShadow"));
        ProbeRegistry.Register(new CircinusProbe("circinus_corners_total_ms", CircinusProbe.Metric.TotalMs, mask, "ApplyToCorners"));
        ProbeRegistry.Register(new CircinusProbe("circinus_centres_total_ms", CircinusProbe.Metric.TotalMs, mask, "ApplyToCentres"));
        ProbeRegistry.Register(new CircinusProbe("circinus_reader_total_ms", CircinusProbe.Metric.TotalMs, "CelestialLighting.GlowGridPerLight", "For"));
        ProbeRegistry.Register(new CircinusProbe("circinus_reader_calls", CircinusProbe.Metric.Calls, "CelestialLighting.GlowGridPerLight", "For"));
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
        // indoor_floor_decoupling.json's SEALED room (no door, so §7c's BFS never reaches it and the
        // indoor floor is the only cap in play). Its own middle cell, addressed as the room is built at
        // map-centre + (0, 45). Centre AND corner because an interior should read flat: both are capped by
        // the same floor, so an arm where only one moved would mean the cap is being applied in one pass
        // and not the other. Deliberately not reusing glass_centre/deep_corner from the room above — the
        // offsets happen to suit, but a probe named for a glass wall pinned in a scenario with no glass in
        // it is how a later reader ends up believing the wrong thing about what was measured.
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "sealed_centre_alpha", new IntVec3(0, 0, 45), SkyCoverVertexProbe.Metric.CentreAlpha));
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "sealed_corner_alpha", new IntVec3(0, 0, 45), SkyCoverVertexProbe.Metric.CornerAlpha));
        // door_strength_leak.json's wood-door room, at the SAME cell wood_door_fraction reads, so the
        // two answer about one another: that probe reports the sky fraction SkyFalloffSource resolved
        // (0.2625 there, pinned to 1e-4), this one reports the alpha §7b actually baked from it.
        //
        // Registered to give the sky-falloff reader a deterministic oracle. The existing pins go
        // through SkyFalloffSource.FractionAt -- the per-cell path -- while the mesh goes through the
        // per-section reader, so a change that broke only the reader would leave every one of those
        // pins bit-identical and show up nowhere except in pixels, where the harness's own run-to-run
        // drift is worth 10-18% of the frame. An integer read off the baked mesh has no such floor.
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "wood_door_corner_alpha", new IntVec3(-13, 0, 41), SkyCoverVertexProbe.Metric.CornerAlpha));
        ProbeRegistry.Register(new SkyCoverVertexProbe(
            "wood_door_centre_alpha", new IntVec3(-13, 0, 41), SkyCoverVertexProbe.Metric.CentreAlpha));
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
        //
        // NAMED `Apply`, NOT `Postfix`, for the fourteen stages the sky-target composite owns. When
        // those subsystems each held their own [HarmonyPatch] the entry point really was called
        // Postfix; the composite (§29) renamed every one of them to `Apply(Map, ref SkyTarget)` and
        // left `Postfix` on the composite alone. AccessTools.Method then resolved nothing for
        // thirteen of these arms, and a probe that cannot resolve its target reads zero calls -- the
        // same reading as a stage that never ran, so the whole breakdown reported the sky chain as
        // costing almost nothing and passed. The *_patched pins are what eventually said so, which is
        // exactly the job they were added for; keep them pinned on any arm added here.
        ArmBank("circ_pf_auroracurtaindraw", "CelestialLighting.Patch_AuroraCurtainDraw", "Postfix");
        // The composite's own Postfix: every stage below sums into this one, so it is the honest
        // denominator for our share of CurSkyTarget. The parent arm on CurSkyTarget also contains
        // vanilla's own work, which this excludes.
        ArmBank("circ_pf_composite", "CelestialLighting.Patch_SkyTargetComposite", "Postfix");
        ArmBank("circ_pf_cloudcoversky", "CelestialLighting.Patch_CloudCoverSky", "Apply");
        ArmBank("circ_pf_lowlightdesaturation", "CelestialLighting.Patch_LowLightDesaturation", "Apply");
        ArmBank("circ_pf_auroratint", "CelestialLighting.Patch_AuroraTint", "Apply");
        ArmBank("circ_pf_purplelight", "CelestialLighting.Patch_PurpleLight", "Apply");
        ArmBank("circ_pf_moonshadowcolor", "CelestialLighting.Patch_MoonShadowColor", "Apply");
        ArmBank("circ_pf_limbrefraction", "CelestialLighting.Patch_LimbRefraction", "Apply");
        ArmBank("circ_pf_bloodmoon", "CelestialLighting.Patch_BloodMoon", "Apply");
        ArmBank("circ_pf_nightdesaturationstrength", "CelestialLighting.Patch_NightDesaturationStrength", "Postfix");
        ArmBank("circ_pf_enclosedambient", "CelestialLighting.Patch_EnclosedAmbient", "Apply");
        ArmBank("circ_pf_polarnightblue", "CelestialLighting.Patch_PolarNightBlue", "Apply");
        ArmBank("circ_pf_weatherdimming", "CelestialLighting.Patch_WeatherDimming", "Apply");
        ArmBank("circ_pf_nightradiance", "CelestialLighting.Patch_NightRadiance", "Apply");
        ArmBank("circ_pf_twilightcolor", "CelestialLighting.Patch_TwilightColor", "Apply");
        ArmBank("circ_pf_skycolortemperature", "CelestialLighting.Patch_SkyColorTemperature", "Apply");
        ArmBank("circ_pf_weathershadowcolor", "CelestialLighting.Patch_WeatherShadowColor", "Apply");

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

        // Vector lighting's own frame, armed the same way and for the same reason the sky chain was.
        // Every performance number this subsystem carries so far came from Dubs windows on
        // `Patch_VectorLightDraw:Postfix` — one duration for the whole of it — and a duration cannot
        // answer the question that turns out to matter here, which is HOW MANY TIMES a polygon gets
        // built in a frame.
        //
        // THE PARENT. Everything below hangs off this postfix, so it is the denominator and the only
        // arm an A/B between two builds should be judged on (see the shed/instrumentation note above,
        // and Circinus's own advice: children find, parents judge).
        ArmBank("circ_vldraw", "CelestialLighting.Patch_VectorLightDraw", "Postfix");

        // Its three children, which between them are the whole of it: build this frame's dirty
        // polygons and dirty the sections they touched, draw the emitters in view, draw the pawn
        // shadows those emitters throw. A gap between the parent and the sum of these three is the
        // §27 lesson repeating — that is exactly how a polygon set being built lazily inside a timed
        // scope hid 43 ms.
        ArmBank("circ_vlbuilddirty", "CelestialLighting.Patch_VectorLightDraw", "BuildAndDirty");
        ArmBank("circ_vloverlay", "CelestialLighting.VectorLightOverlay", "Draw");
        ArmBank("circ_vlpawnshadows", "CelestialLighting.VectorLightPawnShadows", "Draw");

        // THE VISIBILITY POLYGON, AND THIS IS THE ARM THE WHOLE BANK EXISTS FOR. Tools/VectorLightBench
        // puts `Build` at 83-94% of a bake in any scene resembling a built colony, and it is reached
        // by two independent paths in the same frame: VectorLightField.EnsurePolygon bakes it for the
        // mask, and VectorLightOverlay.Rebuild bakes it again for the mesh. Nothing in the repo could
        // see that, because `vector_light_bakes` counts only the first of the two. Read this against
        // `vector_light_bakes` in the same window: the ratio between them is the number of times one
        // polygon was built, and it is supposed to be 1.
        ArmBank("circ_vlpolygon", "CelestialLighting.VectorLightMath", "Build");

        // The two stages either side of it, so the polygon's share is a share of something measured
        // rather than of the parent. SegmentsAround is the window scan — a bool[] the size of the
        // emitter's square plus an edifice-grid read per cell — and is reached by the same two paths,
        // so its count should track the polygon's exactly.
        ArmBank("circ_vlsegments", "CelestialLighting.VectorLightBlockers", "SegmentsAround");
        ArmBank("circ_vlcoverage", "CelestialLighting.VectorLightMath", "BuildCoverage");

        // The mask, which runs inside a section regenerate rather than in the draw and so appears in
        // neither the parent above nor any frame-cost table. `circ_lightoverlay` already measures the
        // vanilla bake it rides on; this separates our postfix from it.
        ArmBank("circ_vlmask", "CelestialLighting.VectorLightMask", "Apply");

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
        // §27. Registered with the THREE-arg overload and defaultEnabled: false. THAT IS NO LONGER
        // THE SHIPPED DEFAULT — a fresh install of the mod now starts with vector lighting on — and
        // it stays false here anyway, which is load-bearing rather than tidiness: the two-arg
        // overload assumes true, and
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
                // Zeroes the bake counters, the way the door-aperture flag zeroes its own, so each
                // arm counts from zero instead of inheriting the previous arm's total. ForceRebuild
                // below then provokes a fresh bake per emitter, which is what makes the first
                // reading of vector_light_bakes an emitter count rather than an accident of
                // whatever the previous arm left dirty.
                VectorLightField.ResetCounters();
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
        // TWO-arg overload now, i.e. defaultEnabled true, because true IS the shipped default since
        // the mask became what §27 composes with. Safe for the reason the penumbra flag is safe and
        // vector_lights itself is not: it is inert while vector_lights is off, which is what
        // FeatureRegistry.ResetAll leaves that one at, so it cannot contaminate a later scenario.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightMaskKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightMask = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        // §27 phase 3's beam half. THREE-arg overload with defaultEnabled: false to match the
        // shipped default; inert while vector_light_mask is off, but registered with the explicit
        // default anyway so ResetAll cannot switch it on for a later scenario in a suite.
        // §27 phase 4. Registered off, like every other §27 flag, and inert unless the mask is
        // composing — it asks the mask's coverage grid whether a lamp can see the pawn.
        // Two-arg overload, matching its shipped default of true. Inert while vector_lights is off,
        // and it draws nothing at all unless the mask is composing.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightPawnShadowsKey,
            enabled => CelestialLightingFeatures.VectorLightPawnShadows = enabled);
        // §27 phase 4b, the share denominator. Two-arg overload, matching its shipped default of
        // true. No ForceRebuild: this changes only the alpha the shadows are drawn at, and they are
        // drawn immediate-mode every frame rather than baked into a section.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightShadowSharesKey,
            enabled => CelestialLightingFeatures.VectorLightShadowShares = enabled);
        // Where that denominator is sampled -- the pawn's cell, or the ground the shadow falls on.
        // Two-arg overload, matching its shipped default of true. No ForceRebuild for the same
        // reason as the flag above: it moves an alpha, and nothing about it is baked.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightShadowGroundSharesKey,
            enabled => CelestialLightingFeatures.VectorLightShadowGroundShares = enabled);
        // §27's geometry and #166's clip. Two-arg overloads, matching their shipped default of true.
        // No ForceRebuild on either: both change only what the immediate-mode draw emits per frame,
        // and neither touches a baked section or the visibility polygons they read.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightShadowShapeKey,
            enabled => CelestialLightingFeatures.VectorLightShadowShape = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightShadowClipKey,
            enabled => CelestialLightingFeatures.VectorLightShadowClip = enabled);
        // The along-length fade. Two-arg overload, matching its shipped default of true. No
        // ForceRebuild for the same reason as its siblings above -- it selects which material the
        // per-frame immediate-mode draw picks and touches no baked section.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightShadowFeatherKey,
            enabled => CelestialLightingFeatures.VectorLightShadowFeather = enabled);
        // §27e, and all three now ship TRUE together -- an open door is a hole in the wall to our
        // polygon, to vanilla's glow grid and to the leaves' own slide. The registry default is what
        // a suite's ResetAll restores between scenarios, so it has to BE the shipped value: left at
        // false, every §27 scenario after one that reset would quietly measure doorways behaving as
        // walls while its description claimed otherwise. That exact mistake is on record for
        // realistic_day_length, and the glow-blocker one would carry gameplay light with it.
        //
        // Kept on the three-arg overload rather than moved to the two-arg one, because these are the
        // flags a scenario is most likely to want explicitly OFF, and reading `defaultEnabled: true`
        // at the registration site is what makes an arm's omission obvious in review.
        //
        // ForceRebuild on all three: the occlusion answer changes for every light near a door, and
        // unlike a door actually opening there is no MapEvents notification to provoke the rebake.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightOpenDoorsKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightOpenDoors = enabled;
                // Vanilla's blocker bits are moved by door EVENTS, and a SetFeature is not one.
                // Without this a scenario that flips the flag with a door already standing open
                // measures the previous answer -- which it did, bit-identically, on the first run.
                VectorLightDoorEvents.ReconcileAllDoors();
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: true);
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightDoorGlowBlockerKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightDoorGlowBlocker = enabled;
                // Vanilla's blocker bits are moved by door EVENTS, and a SetFeature is not one.
                // Without this a scenario that flips the flag with a door already standing open
                // measures the previous answer -- which it did, bit-identically, on the first run.
                VectorLightDoorEvents.ReconcileAllDoors();
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: true);
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightDoorApertureKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightDoorAperture = enabled;
                // Vanilla's blocker bits are moved by door EVENTS, and a SetFeature is not one.
                // Without this a scenario that flips the flag with a door already standing open
                // measures the previous answer -- which it did, bit-identically, on the first run.
                VectorLightDoorEvents.ReconcileAllDoors();
                // Clears the watched-door set AND the rebake counter, so each arm of a scenario
                // counts its own bakes from zero rather than inheriting the previous arm's total.
                GameComponent_DoorAperture.Reset();
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: true);
        // Issue #188's two invalidation flags, both shipping TRUE, so both take the two-arg overload.
        // The three-arg one with defaultEnabled false would make a ResetAll leave every later arm on
        // the pre-change path while its description claims to measure the new one — that exact
        // mistake is on record for realistic_day_length.
        //
        // ForceRebuild on both, because they change WHEN sections rebake: a flip with no rebuild
        // leaves the map showing whatever the previous arm baked, and the A/B measures nothing.
        // ForceRebuild here too, and for a sharper reason than its neighbours: this flag decides what
        // a section bakes when a polygon is dirty, so an arm that flipped it without provoking a
        // rebake would read whatever the previous arm baked and report the two arms as identical.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightStalePolygonKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightStalePolygon = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightSectionDirtyKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightSectionDirty = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        // ForceRebuild for the reason its two neighbours need it, sharpened: this flag decides what a
        // BAKE dirties, so an arm that flipped it without provoking one would find every polygon
        // already clean, dirty nothing at all, and report both arms at zero — a perfect match
        // between a feature that works and a feature that never ran.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightChangedDirtyKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightChangedDirty = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightViewCullKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightViewCull = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        // NO ForceRebuild HERE, for the same reason vector_light_door_dirty_suppress goes without
        // one and a sharper version of it. This flag changes nothing about what any section should
        // CONTAIN — only the moment in the frame at which the polygons a section reads are rebuilt —
        // and the defect it fixes is a transient that a whole-map rebake heals instantly. An arm that
        // rebuilt on entry would hand itself a fresh, correct map and could not measure its own
        // defect. Issue #218.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightBuildFirstKey,
            enabled => CelestialLightingFeatures.VectorLightBuildFirst = enabled);
        // NO ForceRebuild HERE, and the omission is deliberate rather than an oversight. This flag
        // changes nothing about what any section should CONTAIN — it only decides whether a door's
        // blocker write flags sections for redraw — so rebuilding the whole map on the flip would
        // hand the arm a fresh, correct frame and hide precisely the staleness the flag risks. An arm
        // that rebuilt on entry could not measure its own defect.
        //
        // The two-argument overload, so a ResetAll restores it to off. It ships off.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightDoorDirtySuppressKey,
            enabled => CelestialLightingFeatures.VectorLightDoorDirtySuppress = enabled);
        // ForceRebuild for the same reason as the two above, and one that is specific to this flag:
        // it decides HOW a batch is baked, not whether, so without a rebuild the arm inherits
        // whatever the previous arm already baked and every emitter is clean. The fan-out count then
        // reads zero and the arm looks like a feature that does not work rather than one that was
        // never given anything to do.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightParallelBakeKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightParallelBake = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        // ForceRebuild for the same reason again, and the rebuild is doing something slightly
        // different here: ClearAll drops the entries, and every memo goes with them. That is what an
        // arm switching this flag NEEDS — a memo carried over from the previous arm would be reused
        // by an arm that is supposed to be the baseline, or refused by one that is not, and either
        // way the first few gathers of the arm would belong to its neighbour.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightSilhouetteCacheKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightSilhouetteCache = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        // ForceRebuild again, and here it is doing the most work of any of them: ClearAll drops every
        // entry, so both dirty flags start true and the arm's first refresh is a full one whichever
        // way the flag is set. Without it an arm switching this on would inherit textures the
        // previous arm had already uploaded and report a hold rate belonging to its neighbour.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightGlowTextureHoldKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightGlowTextureHold = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        // Two-arg overload, matching its shipped default of true, and inert while vector_lights is
        // off for the same reason as the mask above.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightMaskBeamKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightMaskBeam = enabled;
                VectorLightRedraw.ForceRebuild();
            });
        // THREE-ARG, because phase 5's max ships OFF. The two-arg overload registers a default of
        // true, so a suite's ResetAll between scenarios would turn the max ON for every later
        // scenario in the batch — every §27 arm after this one would then be measuring a
        // composition its own JSON never asked for.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightMaskMaxKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightMaskMax = enabled;
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: false);

        // Two-arg, matching phase 6's shipped default of true. The registry default is what a suite
        // reset restores between scenarios, so it has to be the SHIPPED value or every scenario
        // after the first would silently measure a composition the mod does not ship with.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightShaderMaxKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightShaderMax = enabled;
                VectorLightRedraw.ForceRebuild();
            });

        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightShaderMaxSubtractKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightShaderMaxSubtract = enabled;
                VectorLightRedraw.ForceRebuild();
            });

        // THREE-ARG, because the surface lift ships OFF while its level is calibrated. The
        // two-arg overload registers a default of true, so a suite's ResetAll would turn the lift on
        // for every later §27 scenario in the batch and each of them would measure a compositing its
        // own JSON never asked for.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightSurfaceLiftKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightSurfaceLift = enabled;
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: false);

        // THREE-ARG, same reason as the lift it layers over: it ships OFF, and the two-arg overload
        // registers a default of TRUE, so a suite's ResetAll would leave it on for every later §27
        // scenario in the batch and each of them would measure a composition its own JSON never
        // asked for.
        //
        // NO ForceRebuild, and it is the only §27 flag in this file without one. Every neighbour has
        // one because it decides what gets BAKED — into a polygon, a section mesh or a memo — so an
        // arm that flipped it without provoking a rebake would inherit its neighbour's bake. This
        // flag decides whether a second Graphics.DrawMesh is issued, and that decision is taken
        // fresh every frame from the flag itself. Adding a rebuild here would cost the arm a
        // whole-map rebake and could not change what it measures.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightIndoorMultiplyKey,
            enabled => CelestialLightingFeatures.VectorLightIndoorMultiply = enabled,
            defaultEnabled: false);

        // THREE-ARG, and for the usual reason: this ships OFF while it is measured, and the two-arg
        // overload would turn it on for every later §27 scenario a suite reset touched.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightGapParityKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightGapParity = enabled;
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: false);

        // The four halves of one question, registered over one shared walk. Pinned together or not
        // at all: a peak of zero means something entirely different when the cell count is zero, and
        // reading one without the other is how a probe reports a healthy number about nothing.
        ProbeRegistry.Register(new VectorLightBeyondProbe(
            "vector_light_beyond_ours", VectorLightBeyondProbe.Metric.Ours));
        ProbeRegistry.Register(new VectorLightBeyondProbe(
            "vector_light_beyond_vanilla", VectorLightBeyondProbe.Metric.Vanilla));
        ProbeRegistry.Register(new VectorLightBeyondProbe(
            "vector_light_beyond_excess", VectorLightBeyondProbe.Metric.Excess));
        ProbeRegistry.Register(new VectorLightBeyondProbe(
            "vector_light_beyond_cells", VectorLightBeyondProbe.Metric.Cells));

        // THREE-ARG, same reason as its neighbours: ships OFF while it is measured.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightApertureBeamKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightApertureBeam = enabled;
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: false);

        // THREE-ARG, same reason again: ships OFF while it is measured, and the two-arg overload
        // would leave it on for every later §27 scenario a suite reset touched.
        //
        // ForceRebuild because both halves of this rule are latched. The mask's half is baked into
        // the lighting overlay during a section regenerate, and the fragment program's half rides in
        // a per-emitter texture that is only re-uploaded when the entry is marked dirty — so
        // flipping the flag changes nothing on screen until something else provokes both. Clearing
        // the field is what provokes both at once.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightBentPathKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightBentPath = enabled;
                VectorLightRedraw.ForceRebuild();
            },
            defaultEnabled: false);

        // The two halves of one question, and pinned together for the same reason the four above
        // are: "how many cells changed hands" is only a finding next to "and none of them were the
        // lamp's own near field". Either one alone reads as healthy while the rule is broken in the
        // direction the other one watches.
        ProbeRegistry.Register(new VectorLightBentProbe(
            "vector_light_bent_home", VectorLightBentProbe.Metric.Home));
        ProbeRegistry.Register(new VectorLightBentProbe(
            "vector_light_bent_beyond", VectorLightBentProbe.Metric.Beyond));
        ProbeRegistry.Register(new VectorLightBentProbe(
            "vector_light_bent_applied", VectorLightBentProbe.Metric.Applied));

        // Two-arg, matching phase 5b's shipped default of true. The registry default is what a suite
        // reset restores between scenarios, so it has to be the SHIPPED value — registered false,
        // every §27 scenario after this one would silently measure the pre-fix composition.
        // ForceRebuild because the correction is baked into the lighting overlay's vertex colours
        // during a section regenerate, so flipping it changes nothing on screen until something else
        // dirties a section.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightMaskSaturationKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightMaskSaturation = enabled;
                VectorLightRedraw.ForceRebuild();
            });

        // Two-arg, matching its shipped default of true: the control arm is only reachable by a
        // scenario asking for it, and a suite reset correctly puts the lift back on.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightMaskMaxLiftKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightMaskMaxLift = enabled;
                VectorLightRedraw.ForceRebuild();
            });

        // Two-arg, matching its shipped default of true: the seed IS matched unless a scenario
        // deliberately drops it to shoot the brightness-rescale arm.
        FeatureRegistry.Register(
            CelestialLightingFeatures.VectorLightMaskMaxSeedKey,
            enabled =>
            {
                CelestialLightingFeatures.VectorLightMaskMaxSeed = enabled;
                VectorLightRedraw.ForceRebuild();
            });
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
        // Not CelestialLightingFeatures flags either: the two positions of the player's cloud opacity
        // slider that are worth pinning. A float knob has no SetFeature of its own, and comparing one
        // across two boots compares two different cloud layouts — see CloudOpacityOverride's header.
        // defaultEnabled FALSE on both, like every other override here: the resting state is the
        // shipped slider position, so a ResetAll between scenarios in a suite puts the opacity back
        // to 1 rather than leaving every later scenario measuring a thinned sky.
        FeatureRegistry.Register(
            CloudOpacityOverride.ReducedFeatureKey,
            enabled => CloudOpacityOverride.SetReduced(enabled),
            defaultEnabled: false);
        FeatureRegistry.Register(
            CloudOpacityOverride.ZeroFeatureKey,
            enabled => CloudOpacityOverride.SetZero(enabled),
            defaultEnabled: false);
        // Same shape and the same reasoning as the two above — the positions of a FLOAT slider that
        // SetFeature cannot otherwise reach — with one extra obligation that is easy to miss:
        // VectorLightReachOverride rebuilds every polygon on the map when it fires, because reach is
        // baked rather than read per frame. defaultEnabled FALSE on both, so a ResetAll between
        // scenarios in a suite puts lamps back to their vanilla reach rather than leaving every
        // later scenario silently measuring stretched ones.
        FeatureRegistry.Register(
            VectorLightReachOverride.VibrantFeatureKey,
            enabled => VectorLightReachOverride.SetVibrant(enabled),
            defaultEnabled: false);
        FeatureRegistry.Register(
            VectorLightReachOverride.MaxFeatureKey,
            enabled => VectorLightReachOverride.SetMax(enabled),
            defaultEnabled: false);
        FeatureRegistry.Register(
            VectorLightReachOverride.BrightFeatureKey,
            enabled => VectorLightReachOverride.SetBright(enabled),
            defaultEnabled: false);
        FeatureRegistry.Register(
            VectorLightReachOverride.DefaultBrightFeatureKey,
            enabled => VectorLightReachOverride.SetDefaultBright(enabled),
            defaultEnabled: false);
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
        // Not a CelestialLightingFeatures flag either: flattens the pawn-shadow fade ramp WITHOUT
        // leaving the feathered material, so an arm can separate the shader swap from the curve. See
        // PawnShadowFlatRampOverride's header for why a change that does both at once needs it.
        // defaultEnabled FALSE, like its siblings — the resting state is the shipped curve.
        PawnShadowFlatRampOverride.Install();
        FeatureRegistry.Register(
            PawnShadowFlatRampOverride.FeatureKey,
            enabled => PawnShadowFlatRampOverride.Set(enabled),
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
        // Which RENDERER draws the curtain, not whether one is drawn. Off bakes the field on the CPU
        // exactly as it shipped before the shader existed; on evaluates it per fragment. Registered
        // with the default-true overload because true is the shipped default, so a ResetAll between
        // scenarios leaves later ones on the renderer subscribers actually get.
        //
        // Turning it ON does not guarantee the shader path: AuroraShader.Available is checked first,
        // so on a machine with no bundle this arm quietly measures the bake twice. A scenario that
        // cares must pin aurora_shader_active alongside whatever else it measures.
        FeatureRegistry.Register(
            CelestialLightingFeatures.AuroraShaderFieldKey,
            enabled => CelestialLightingFeatures.AuroraShaderField = enabled);
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
        // The decoupled indoor floor: same baked-mesh situation as §7b's own flag immediately above (it
        // resolves the floor inside that same Regenerate postfix), so toggling it needs the identical
        // rebuild or the A/B would compare a stale bake against itself. Note this flag is only visible at
        // an hour where §7a is actually darkening — at noon both arms bake the identical cover by
        // construction, so a scenario that toggles it in daylight measures a guaranteed zero.
        FeatureRegistry.Register(
            CelestialLightingFeatures.DecoupledIndoorFloorKey,
            enabled =>
            {
                CelestialLightingFeatures.DecoupledIndoorFloor = enabled;
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
        // The gather phase. Same baked-mesh situation as the §7b-family flags around it, so the toggle
        // needs the same forced rebuild — but for a different reason than they do: those flags change
        // what gets baked, and this one changes only WHERE the bake happens. The rebuild is what makes
        // the arm measure anything at all, since without it the meshes baked before the flip stay on
        // screen and both arms photograph the same pass.
        //
        // Registered with the default-TRUE overload to match the shipped default. A ResetAll that put
        // it back to false would leave every later scenario in a suite measuring the serial path while
        // its comments claimed the shipped one.
        FeatureRegistry.Register(
            CelestialLightingFeatures.IndoorOcclusionGatherKey,
            enabled =>
            {
                CelestialLightingFeatures.IndoorOcclusionGather = enabled;
                IndoorOcclusionRedraw.ForceRebuild();
            },
            defaultEnabled: true);

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
