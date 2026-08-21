using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// Thin adapter for §13: pulls the primitives WeatherDimmingMath needs off live Map/WeatherManager
// state. Shared by Patch_WeatherDimming (sky tint), Patch_ShadowStrength (shadow softening),
// Patch_LowLightDesaturation (§9's apparent brightness) and WeatherDimmingProbe, so all four always
// agree about how heavy the weather is instead of risking four independently-derived values
// disagreeing — the same discipline SolarPosition enforces for sun elevation.
//
// This is also where the two live-state questions the pure classifier cannot answer are asked: does
// this map have a sky at all (HasSky), and has the def declared its own answer (WeatherCloudDeck).
// Both exist because §13's original palette-only classifier was tuned against a vanilla-only census
// and misread modded cave environments as overcast — see DESIGN.md §13.
//
// WHY map.weatherManager AND NOT WeatherWorker's own `def`. Patch_WeatherDimming postfixes
// WeatherWorker.CurSkyTarget, so the "obvious" read is the def belonging to the worker being
// patched. That field is private (pinned by ApiCompatibilityTests.WeatherWorker_DefFieldIsNotPublic),
// so it would need FieldRefAccess — and it would buy nothing, because reading the manager is exactly
// equivalent, not merely close. SkyManager.CurrentSkyTarget calls CurSkyTarget on BOTH the current
// and the last weather's worker and lerps the two results by TransitionLerpFactor. A uniform
// map-level multiply therefore factors straight back out of that lerp:
//
//     Lerp(a*k, b*k, t) == k * Lerp(a, b, t)
//
// so blending the two defs' opacities here and applying one scalar gives bit-identical output to
// applying each def's own scalar inside its own worker call — with no reflection and no fragile
// private-field binding.
public static class WeatherDimming
{
    // §28. Two memos and a def cache, all three added because a Circinus sweep counted the calls
    // rather than because the code looked slow. Over a 540-frame window: CloudOpacityFor 12.7 times a
    // frame, DimmingFor 4.9. Both are per-map answers that cannot change inside one frame, and
    // DimmingFor sits on top of CloudOpacityFor, so the counts compound.
    //
    // Keyed on GeometryStamp, which carries the TICK as well as the frame. That is what makes this
    // exact rather than approximately right: every live input here — TransitionLerpFactor, RainRate,
    // SnowRate, SandRate, snow depth under CavityGainFor — moves on the tick, so a stamp that is
    // still valid is a stamp across which none of them can have moved. The same argument MapSky's
    // header sets out at length; see it for why this needs none of the invalidation hooks a
    // subject-keyed cache would.
    private static readonly GeometryMemo<float> CloudOpacityMemo = new GeometryMemo<float>();
    private static readonly GeometryMemo<float> DimmingMemo = new GeometryMemo<float>();

    private static readonly Func<Map, float> ComputeCloudOpacity = ComputeCloudOpacityFor;
    private static readonly Func<Map, float> ComputeDimming = ComputeDimmingFor;

    // OpacityOf is cached PERMANENTLY rather than per frame, and it is the one cache here that needs
    // no stamp at all: it is a pure function of a WeatherDef's own immutable fields — its mod
    // extension, its day sky colours and its three precipitation rates. None of those change after
    // defs finish loading, so there is nothing for a frame key to protect against.
    //
    // Worth caching despite being small because of what it does: def.GetModExtension<T> walks the
    // def's modExtensions list doing a type test per entry, and CloudOpacityFor asks for TWO defs
    // (last and current weather) on every call, which is ~25 walks a frame before any of the memos
    // above deduplicate them.
    //
    // Dictionary rather than a field on the def because the def is Ludeon's type; growth is bounded
    // by the number of WeatherDefs the load carries (tens), so there is no eviction policy to tune.
    private static readonly Dictionary<WeatherDef, float> OpacityByDef = new Dictionary<WeatherDef, float>();

    // How much of a cloud deck is overhead right now, in [0,1], blended across any in-flight weather
    // transition. 0 when the feature is off, when there is no weather manager (pocket maps during
    // generation), when the map has no sky over it, or under any clear / non-weather weather.
    public static float CloudOpacityFor(Map map)
    {
        // Null map and no-TickManager bypass the memo rather than caching under a made-up key: the
        // first has no uniqueID, the second is every context outside a running game, where
        // FrameStamp.Current would dereference a null Find.TickManager. Both fall through to exactly
        // the pre-memo path — pocket maps during generation still read 0 the way they always did.
        if (map == null || Find.TickManager == null)
            return ComputeCloudOpacityFor(map);

        return CloudOpacityMemo.Get(map.uniqueID, FrameStamp.Current(), map, ComputeCloudOpacity);
    }

    private static float ComputeCloudOpacityFor(Map map)
    {
        if (!CelestialLightingFeatures.WeatherDimming)
            return 0f;

        WeatherManager weather = map?.weatherManager;
        if (weather == null)
            return 0f;

        if (!MapSky.HasSky(map))
            return 0f;

        return WeatherDimmingMath.BlendOpacity(
            OpacityOf(weather.lastWeather),
            OpacityOf(weather.curWeather),
            weather.TransitionLerpFactor);
    }

    // The 0..1 fraction by which the rendered sky is currently darkened. 0 whenever CloudOpacityFor
    // is 0 — the feature gate and the skyless fast path are inherited from it, and Clear weather
    // stays at exactly 0 too even though ReadDimmingAndGain now looks past it for §24's sake (see
    // that function's header for why the clamp, not a gate, is what holds that).
    public static float DimmingFor(Map map)
    {
        if (map == null || Find.TickManager == null)
            return ComputeDimmingFor(map);

        return DimmingMemo.Get(map.uniqueID, FrameStamp.Current(), map, ComputeDimming);
    }

    private static float ComputeDimmingFor(Map map)
    {
        if (!ReadDimmingAndGain(map, out float dimming, out float cavityGain))
            return 0f;

        // §21: the DAYTIME half of the surface-cloud cavity. The same deck this function is dimming
        // for also bounces the ground's light back down, and over snow it hands most of the dimming
        // back. Not a contradiction and not a sign error — a cloud blocks the sun AND reflects from
        // its base, for the same reason it is a cloud. Over bare ground the gain is exactly 1 and
        // this line returns `dimming` unchanged, so every non-snowy map is bit-identical to pre-§21.
        //
        // WHY HERE RATHER THAN IN Patch_WeatherDimming. DimmingFor is the shared read, and all three
        // of its consumers want the recovered value: the sky tint (§13), §9's ApparentGlow and §9's
        // per-cell night-wash strength. A snowy overcast that renders brighter must also desaturate
        // less, and it does so here for free — which is exactly why §21 writes no saturation term of
        // its own (DESIGN.md §21, §9).
        //
        // WHAT IS DELIBERATELY LEFT ALONE: Patch_ShadowStrength, which reads CloudOpacityFor rather
        // than this, so the deck still softens shadows by the full amount. Brightness comes back,
        // contrast does not. That asymmetry is the whiteout.
        //
        // The opacity is passed rather than re-read: CavityGainFor would otherwise walk MapSky's
        // uncached biome/condition gates a second time on a path SkyManager runs twice per map per
        // frame — see ReadDimmingAndGain below, which owns that single read for both consumers.
        return AlbedoCavityMath.RecoveredDimming(dimming, cavityGain);
    }

    // §24 (issue #90): the amplification RecoveredDimming's clamp threw away, for the additive glare
    // overlay to draw instead. 0 whenever DimmingFor returns 0, and 0 whenever the multiply lane had
    // headroom for the whole cavity — see SnowGlareMath.UndrawableExcess for why the residual rather
    // than the gain is the right quantity to hand a second renderer.
    //
    // Shares DimmingFor's reads through ReadDimmingAndGain rather than repeating them, for §13's own
    // stated reason: two independently-derived answers to "how heavy is the weather" are two answers
    // that can disagree, and here they would disagree ON SCREEN — the multiply lane and the additive
    // lane are rendering two halves of one product, so they must be halves of the SAME product.
    public static float UndrawableExcessFor(Map map)
    {
        if (!ReadDimmingAndGain(map, out float dimming, out float cavityGain))
            return 0f;

        return SnowGlareMath.UndrawableExcess(dimming, cavityGain, Vacuum.InVacuumForMap(map));
    }

    // The shared read behind DimmingFor and UndrawableExcessFor: §13's dimming fraction and §21's
    // cavity gain. Returns false for the no-deck / feature-off / skyless fast path, where both
    // consumers answer 0 — extracted rather than duplicated so the extraction is provably
    // behaviour-preserving for DimmingFor (same reads, same order, same early return), which matters
    // because DimmingFor reaches every map on every save.
    //
    // THE TWO OPACITIES ARE DELIBERATELY NOT THE SAME NUMBER (issue #134), and the split is the whole
    // content of this function:
    //
    //   * DIMMING reads §13's classifier ALONE, which scores Clear as exactly 0. A clear sky does not
    //     darken the ground, whatever fraction of it §22 has drifted cloud across — and §22 already
    //     renders its own sky tint in Patch_CloudCoverSky, so deriving a second dimming from the same
    //     fraction here would darken a partly-cloudy Clear day twice.
    //   * The CAVITY reads §13 off Clear and §22's continuous fraction on it, because "is there a
    //     cloud base overhead for the snow to bounce light off" is a question §22 answers better than
    //     §13's abstention does. That is the same substitution SurfaceBuildup.CloudOpacityOrClear
    //     already makes for §7's night floor (issue #100), through the same pure function, so the two
    //     arms of §21 now give ONE answer to "is there a deck" instead of disagreeing across noon.
    //
    // WHAT THAT MEANS ON SCREEN, and why it does not move §21's daytime arm at all: with dimming 0 and
    // a gain above 1, AlbedoCavityMath.RecoveredDimming's `1 - (1 - 0) * gain` is negative and clamps
    // to 0, so DimmingFor still returns exactly 0 on every Clear sky exactly as before. The whole
    // cavity therefore overflows the multiply lane, and every bit of it lands in §24's additive one as
    // `gain - 1` — which is precisely the partition SnowGlareMath.UndrawableExcess exists to perform.
    // §21's day arm keeps rendering nothing on Clear not because it is gated off but because it has no
    // headroom to render anything into.
    private static bool ReadDimmingAndGain(Map map, out float dimming, out float cavityGain)
    {
        dimming = 0f;
        cavityGain = 1f;

        float opacity = CloudOpacityFor(map);
        float deckOpacity = DeckOpacityFor(map, opacity);
        if (deckOpacity <= 0f)
            return false;

        // The cavity always reads the DECK opacity. Off Clear the two are the same float by
        // EffectiveCloudOpacity's definition, so this is not a behaviour change for any weather §13
        // classifies — it just stops the Clear case needing a second call site.
        cavityGain = SurfaceBuildup.CavityGainFor(map, deckOpacity);

        // Clear weather: §22 found a deck for the cavity, but §13 still has no dimming to offer, so
        // skip the rate read entirely rather than feeding it an opacity it never classified. Leaving
        // `dimming` at 0 is what keeps DimmingFor bit-identical on Clear (see the header).
        if (opacity <= 0f)
            return true;

        // Vanilla already lerps all three rates across the weather transition (WeatherManager.RainRate
        // and friends are Mathf.Lerp(last, cur, TransitionLerpFactor)), so we deliberately do not lerp
        // them again here — only the palette-derived opacity needs our own blend. SandRate returns 0
        // without Odyssey, so reading it unconditionally is safe.
        WeatherManager weather = map.weatherManager;
        dimming = WeatherDimmingMath.DimmingFraction(
            opacity,
            weather.RainRate,
            weather.SnowRate,
            weather.SandRate,
            WeatherDimmingSettings.MaxDimming);

        return true;
    }

    // How much deck the CAVITY sees: §13's blended opacity under every weather except Clear, §22's
    // continuous cloud-cover fraction under Clear (issue #134). The daytime mirror of
    // SurfaceBuildup.CloudOpacityOrClear, sharing AlbedoCavityMath.EffectiveCloudOpacity with it so
    // the rule itself is written down once and the night and day arms cannot drift apart.
    //
    // TAKES THE ALREADY-READ §13 OPACITY rather than re-reading it, for the reason
    // SurfaceBuildup.CavityGainFor's two-arg overload exists: CloudOpacityFor walks MapSky's uncached
    // biome gates, and SkyManager calls CurSkyTarget twice per map per frame.
    //
    // GATED ON curWeather BEING Clear, not on `weatherOpacity == 0`, exactly as CloudOpacityOrClear is
    // and for the reason EffectiveCloudOpacity's header gives: §13 also reads 0 on a skyless map and
    // with its own feature off, and neither of those means "this map is in Clear weather right now".
    //
    // MapSky.HasSky IS ASKED AGAIN HERE, and it is not redundant with CloudOpacityFor's copy of it —
    // that one has already collapsed to the same 0 a Clear sky produces, so by this point a 0 cannot
    // tell us whether there is a sky at all. Asking costs one biome-list walk on Clear frames only
    // (the `weatherIsClear` test short-circuits it away on every other weather), and it is what keeps
    // DimmingFor's and §24's long-standing contract that a cave gets no weather lighting. It is
    // deliberately NOT pushed down into CloudOpacityOrClear: that would change §7's shipped night
    // floor on skyless maps, which is a separate question with its own measurements behind it.
    //
    // §13'S FEATURE FLAG IS NOT CONSULTED ON THE CLEAR PATH, which is a real (if small) coupling worth
    // stating: turning weather dimming off leaves §24 firing on a partly-cloudy Clear day while
    // silencing it under an actual overcast. That is the same asymmetry §7's night floor has shipped
    // with since #100 — §22 has its own gate (CelestialLightingFeatures.CloudCover), and §13 has no
    // opinion about Clear to switch off in the first place, so matching the night arm keeps one story
    // rather than inventing a second.
    private static float DeckOpacityFor(Map map, float weatherOpacity)
    {
        bool weatherIsClear = map?.weatherManager?.curWeather == WeatherDefOf.Clear;

        // Read §22 only when it can matter — a cheap per-tile dictionary lookup, but there is no
        // reason to touch it (or seed its cache) on a map that is not in Clear weather right now, and
        // no reason to walk MapSky a second time either.
        float cloudCoverFraction = weatherIsClear && MapSky.HasSky(map)
            ? CloudCoverClock.FractionForMap(map)
            : 0f;

        return AlbedoCavityMath.EffectiveCloudOpacity(weatherOpacity, weatherIsClear, cloudCoverFraction);
    }

    // §13's STRUCTURAL GUARD, and the half of the problem the pure classifier cannot reach. "Is this
    // palette a cloud deck?" is a question about a WeatherDef; "is there any sky here?" is a question
    // about the map, and asking it rather than trying to infer it from a palette is what closes the
    // entire cave / pocket-map / orbit class in one cheap check. §13 shipped without it on the
    // strength of a vanilla-only census; Biomes! Caverns and MultiFloors both ship cave environments
    // with overcast-shaped palettes, which the palette rule alone rates 1.00 and 0.71.
    //
    // The rule itself now lives in MapSky, because Biomes! Caverns showed the question was never
    // specific to weather: sunset warmth, colour temperature, aurora, blood moon, eclipses and a
    // night floor lifted by MOONLIGHT are all equally meaningless under a rock ceiling, and all of
    // them were applying themselves to sealed caves. This delegation is a pure move — the rule and
    // the counting are unchanged, which is what keeps weather_dimming_skyless.json passing untouched
    // and makes that scenario the regression proving the move was behaviour-preserving.

    // Classifies a single WeatherDef from data it already ships — its day palette and its
    // precipitation rates. No def-name list and no registration step; see
    // WeatherDimmingMath.CloudOpacity for what each line of evidence buys and why neither alone is
    // enough.
    private static float OpacityOf(WeatherDef def)
    {
        if (def == null)
            return 0f;

        if (OpacityByDef.TryGetValue(def, out float cached))
            return cached;

        float opacity = ComputeOpacityOf(def);
        OpacityByDef[def] = opacity;
        return opacity;
    }

    private static float ComputeOpacityOf(WeatherDef def)
    {
        // An explicit statement by the def beats anything we can infer from it. Checked first so the
        // escape hatch is unconditional: a mod author (or an XML patch) who says "this is not a cloud
        // deck" is not overruled by a palette that happens to look like one.
        WeatherCloudDeck declared = def.GetModExtension<WeatherCloudDeck>();
        if (declared != null && declared.OverridesOpacity)
            return Mathf.Clamp01(declared.opacity);

        SkyColorSet colors = def.skyColorsDay;
        return WeatherDimmingMath.CloudOpacity(
            colors.sky.r, colors.sky.g, colors.sky.b, colors.saturation,
            def.rainRate, def.snowRate, def.sandRate);
    }

    // §23 (DESIGN.md, issue #88): cloud base height overhead right now, in metres, blended across a
    // weather transition the same way CloudOpacityFor blends opacity. Deliberately a SEPARATE public
    // entry point rather than folded into CloudOpacityFor's own return: the two questions ("how much
    // deck" and "how high is it") have independent callers today (Patch_WeatherDimming never needs
    // altitude at all) and combining them into one struct would force every existing call site to
    // start naming a field it does not use.
    //
    // Mirrors CloudOpacityFor's gates exactly (feature flag, null weatherManager, MapSky.HasSky) rather
    // than sharing its early-return path, which does cost one extra MapSky.HasSky check per frame
    // where a caller wants both numbers. Accepted rather than threaded through as an overload: §8
    // already reads three separate SiteAltitude values per frame in Patch_SkyColorTemperature
    // (pressure fraction, aerosol fraction, Angstrom exponent), so one more whole-map, no-cell-scan
    // read is consistent with what that call site already pays, not a new category of cost.
    //
    // THE FEATURE-FLAG CHECK HERE IS NOT, BY ITSELF, WHAT MAKES "OFF" A NO-OP. Unlike opacity, 0 is a
    // legitimate, physically meaningful altitude (a ground-hugging deck — see
    // CloudUnderlightMath.ShadowEntryDepressionDegrees), so a caller cannot treat this method reading
    // 0 as "the feature is off" the way CloudOpacityFor's callers safely treat ITS 0 as "no cloud, do
    // nothing". Patch_SkyColorTemperature therefore checks CelestialLightingFeatures.CloudUnderlight
    // itself before calling CloudUnderlightMath.WarmthMultiplier at all; the check here is only
    // defense in depth (mirroring CloudOpacityFor's own shape) for the unlikely case something else
    // ever calls this directly.
    //
    // THREE FLAGS, ANY OF WHICH KEEPS THIS ALIVE. §23b's additive layer (CloudUnderlightLayer) reads
    // the same altitude for the same geometry, and §25b's deck mixture (CloudSheet) decomposes it
    // into the layered sky a single number cannot describe. All three lanes are independently
    // switchable — so gating on §23's flag alone would make "flat lane off, spatial lane on" silently
    // return a ground-hugging 0 and kill the layer for a reason no setting names. The guard means "no
    // consumer of cloud altitude is on", which is the thing it was always standing for.
    //
    // §25b is the one that makes the list load-bearing rather than tidy: a 0 here collapses the deck
    // mixture onto the low deck, so leaving CloudSheet off this list would mean switching off §23
    // silently deleted every cirrus in the sky.
    public static float CloudAltitudeMetresFor(Map map)
    {
        if (!CelestialLightingFeatures.CloudUnderlight
            && !CelestialLightingFeatures.CloudUnderlightLayer
            && !CelestialLightingFeatures.CloudSheet)
        {
            return 0f;
        }

        WeatherManager weather = map?.weatherManager;
        if (weather == null)
            return 0f;

        if (!MapSky.HasSky(map))
            return 0f;

        return WeatherDimmingMath.BlendOpacity(
            AltitudeOf(weather.lastWeather),
            AltitudeOf(weather.curWeather),
            weather.TransitionLerpFactor);
    }

    // Classifies a single WeatherDef's cloud base height from data it already ships — the escape
    // hatch first, the rain/snow/sand-rate classifier otherwise. Mirrors OpacityOf's own shape
    // exactly, one field over.
    private static float AltitudeOf(WeatherDef def)
    {
        if (def == null)
            return 0f;

        WeatherCloudDeck declared = def.GetModExtension<WeatherCloudDeck>();
        if (declared != null && declared.OverridesAltitude)
            return Mathf.Max(0f, declared.altitudeMetres);

        return WeatherDimmingMath.DefaultAltitudeMetres(def.rainRate, def.snowRate, def.sandRate);
    }
}
