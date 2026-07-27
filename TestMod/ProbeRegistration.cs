using RimWorld;
using RimWorldTestHarness.Mod.Features;
using RimWorldTestHarness.Mod.Probes;
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
        ProbeRegistry.Register(new ShadowVectorXProbe());
        ProbeRegistry.Register(new CivilTwilightProbe());
        ProbeRegistry.Register(new PenumbraProbe());
        ProbeRegistry.Register(new MoonIlluminationProbe());
        ProbeRegistry.Register(new NightRadianceProbe());
        ProbeRegistry.Register(new PurkinjeProbe());
        // §9's applied strength, added after two versions shipped whose rod-vision factor was right
        // and whose effect on screen was nil. PurkinjeProbe says how dark the sky is; this says how
        // much desaturation that actually turned into.
        ProbeRegistry.Register(new NightDesaturationProbe());
        ProbeRegistry.Register(new SkyColorTemperatureProbe());
        ProbeRegistry.Register(new AuroraTintProbe());
        ProbeRegistry.Register(new EclipseCoverageProbe());
        ProbeRegistry.Register(new BloodMoonProbe());
        // §6a's two instruments. moon_shadow_render measures the composed shadow colour the shader
        // actually uses, which is the only reliable way to test a moon shadow — a screenshot A/B of a
        // night scene moves pixels by 1-3/255 and cannot be told apart from weather and pawn motion.
        // moon_elevation exists so a scenario can prove the moon was actually in the sky, since
        // moon_illumination reports phase alone and a full moon can be below the horizon.
        ProbeRegistry.Register(new MoonShadowRenderProbe());
        ProbeRegistry.Register(new MoonElevationProbe());
        ProbeRegistry.Register(new WeatherDimmingProbe());
        // Raw gameplay glow, so the weather_dimming scenario can assert §13's central negative: the
        // sky visibly darkens under a storm while this value does not move at all.
        ProbeRegistry.Register(new SkyGlowProbe());
        // §14: one number that says whether vanilla's sky and our sun agree about day/night.
        ProbeRegistry.Register(new SunClockDisagreementProbe());
        ProbeRegistry.Register(new SunElevationProbe());
        // §15: how many cells on this map are eaves at all. Separates "the A/B images match because
        // the toggle did nothing" from "they match because this colony has no porch to shade".
        ProbeRegistry.Register(new EaveCellProbe());

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

        // #12/PR #18: how many times per frame solar and lunar geometry is asked for, and how many
        // times it is actually derived. Eleven probes rather than one because the whole claim is the
        // gap between those two series — a single "evaluations" number could not distinguish a memo
        // that works from a caller that stopped asking. Mean and max are both reported for each: the
        // mean is the steady frame, the max is the frame that also regenerated visible sections
        // (Patch_ShadowTilt is per visible section) and is where any tick-boundary double-evaluation
        // would surface. See GeometryEvalCountProbe's header for why this counts instead of timing.
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

        // Expose CelestialLighting's runtime feature flags to the harness's SetFeature step so a
        // scenario can screenshot an effect off then on. The setter just writes the shipped mod's
        // static flag; in production nothing calls it and the flag stays at its default (on).
        FeatureRegistry.Register(
            CelestialLightingFeatures.CivilTwilightPersistenceKey,
            enabled => CelestialLightingFeatures.CivilTwilightPersistence = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.PenumbraContrastKey,
            enabled => CelestialLightingFeatures.PenumbraContrast = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.MoonShadowsKey,
            enabled => CelestialLightingFeatures.MoonShadows = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.NightRadianceKey,
            enabled => CelestialLightingFeatures.NightRadiance = enabled);
        FeatureRegistry.Register(
            CelestialLightingFeatures.WeatherDimmingKey,
            enabled => CelestialLightingFeatures.WeatherDimming = enabled);
        // Not a CelestialLightingFeatures flag: this bridges the "true pitch-black" atmospheric-floor
        // switch that lives on NightRadianceSettings, so a probe scenario can drop the constant
        // starlight+airglow floor out of the night_radiance sum and watch only moonlight remain.
        FeatureRegistry.Register(
            CelestialLightingFeatures.NightAtmosphericGlowKey,
            enabled => NightRadianceSettings.Current.AtmosphericGlowEnabled = enabled);
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
        FeatureRegistry.Register(
            CelestialLightingFeatures.AuroraKey,
            enabled => CelestialLightingFeatures.Aurora = enabled);
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
        // feature must see exactly what a player sees on a fresh install.
        FeatureRegistry.Register(
            "natural_eclipse",
            enabled => EclipseSettings.Mode = enabled ? EclipseMode.NaturalOnly : EclipseMode.UnnaturalOnly);
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
            });
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
        FeatureRegistry.Register(
            "pitch_black_true",
            enabled => NightRadianceSettings.Current.MinNightBrightness =
                enabled ? 0f : NightRadianceMath.DefaultMinNightBrightness);
    }
}
