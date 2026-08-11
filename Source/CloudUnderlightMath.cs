using System;

namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// Formulas.cs / WeatherDimmingMath.cs. Compiled into both Source (net481, inside RimWorld) and
// Tests (net8.0, standalone) via a linked <Compile Include>, so the exact code that ships is the
// exact code under test.
//
// Subsystem 23 (DESIGN.md §23, issue #88 option 1): cloud-base underlighting.
//
// THE MECHANISM. Once the sun sits at or below the horizon, direct light no longer reaches the
// ground, but it still reaches a cloud deck's UNDERSIDE from below for a while longer — the deck
// sticks up above the horizon dip that has already swallowed the ground. That light has crossed an
// extremely long low-elevation path and arrives heavily reddened, which is the real-world source of
// a sunset's drama (pinks/magentas from high cirrus, deep orange from mid altocumulus) and also of
// its opposite (a low deck goes dark almost immediately, "killing" the sunset). Full derivation and
// the option-1/option-2 scoping decision are in DESIGN.md §23; this file is only the arithmetic.
//
// SCOPE: NOT a spatial effect. RimWorld's sky is one flat colour, so this cannot paint a warm cloud
// against a cool vault the way the real sky does (that is option 2, deliberately not attempted
// here — see DESIGN.md §23's "what was rejected"). What it CAN do, honestly, is get the TIMING and
// INTENSITY of §8's existing tint right: a high deck should let the warm tint linger well past
// geometric sunset, and a low deck should kill it early. This file produces exactly one number — a
// multiplier on §8's tint strength — and Patch_SkyColorTemperature applies it to the same single
// colour §8 already blends toward. No new colour target, no new render path.
//
// THE GEOMETRY: reusing PurpleLightMath.ShadowHeightKm's own model, inverted. That function answers
// "how high has Earth's own shadow risen, given the sun is this far below the horizon" —
// h(theta) = R * (sec(theta) - 1). This file asks the inverse question: "given a cloud base at this
// height, at what depression angle does the rising shadow finally reach it" —
// theta(h) = arccos(R / (R + h)). Deliberately the same secant-shadow model, not the coarser
// sqrt(2h/R) small-angle horizon-dip approximation aviation/marine navigation normally uses:
// this codebase already has one canonical answer for "how high is Earth's shadow", and a second,
// slightly different approximation of the same physical quantity is exactly the kind of drift
// DESIGN.md's mired-space and aerosol notes (§20, §20d) warn against.
public static class CloudUnderlightMath
{
    // Earth's mean radius in km. Duplicated here rather than shared from PurpleLightMath (whose own
    // copy is private) — same choice LimbRefractionMath.PlanetRadiusKm and
    // VacuumRadianceMath.PlanetRadiusKm already made independently of each other. See
    // PurpleLightMath.ShadowHeightKm for the sibling formula this file inverts.
    private const float EarthRadiusKm = 6371f;

    private const float RadToDeg = 180f / MathF.PI;

    // How much a fully-lit cloud base can boost §8's tint at the peak of its glow window. Kept
    // modest relative to the suppression side's full [0,1] range (see WarmthMultiplier): a warm
    // cloud reads as MORE saturated colour at the same brightness, not as an unboundedly stronger
    // blend, and Color.Lerp's own clamp caps how much overshoot is ever visible anyway.
    private const float GlowAmplitude = 0.6f;

    // §23b's peak additive alpha, at the top of the glow window over a half-covered sky.
    //
    // CALIBRATED BY WATCHING, WHICH IS WHAT THE PROTOTYPE PHASE WAS FOR. The first guess was 0.20,
    // which measured median CIELAB deltaE 9.12 — larger than anything the mod ships (§20b pollution
    // at 6.79) — and read as distracting over a sunset rather than as part of one. Halved to 0.10 on
    // that call. 0.20 is still reachable in one boot through the harness's cloud_underlight_strong
    // sweep, so the frames that motivated the change stay reproducible rather than becoming a claim
    // about a build nobody can rebuild.
    //
    // Note the alpha actually reaching any one pixel is well under this: the field's residual peaks
    // at 1 - fraction and only over the cloudy part of the map, so the budget is spent on a fraction
    // of the frame rather than across it. The sweep seam is CloudLayers.AmplitudeScale, the
    // same dev seam SnowGlare.IntensityScale opened for the same kind of question.
    public const float LayerAmplitude = 0.10f;

    // The depression angle (degrees below the horizon, positive) at which a cloud base of the given
    // altitude enters Earth's own shadow and stops catching direct sunlight from underneath.
    //
    // theta = arccos(R / (R + h)) — see this file's header for the derivation and why it mirrors
    // PurpleLightMath.ShadowHeightKm rather than a second approximation of the same geometry.
    //
    // altitudeMetres <= 0 returns 0: a deck sitting at the ground has no window at all — it enters
    // shadow the instant the sun crosses the geometric horizon, which is the low-stratus "kills the
    // sunset early" case in its most extreme form (DESIGN.md §23's table).
    //
    // No domain clamp needed on the arccos argument: for any altitudeKm >= 0, R / (R + altitudeKm)
    // is provably in (0, 1], so it is always a valid cosine.
    public static float ShadowEntryDepressionDegrees(float altitudeMetres)
    {
        if (altitudeMetres <= 0f)
            return 0f;

        float altitudeKm = altitudeMetres / 1000f;
        float cosTheta = EarthRadiusKm / (EarthRadiusKm + altitudeKm);
        return RadToDeg * MathF.Acos(cosTheta);
    }

    // Fraction of the below-horizon window during which the cloud deck ITSELF is still directly
    // lit — the phase this subsystem adds warmth for. Zero at and above the horizon (§8 already owns
    // that regime) and zero again once the sun's depression reaches shadowEntryDegrees, the point the
    // deck goes dark too.
    //
    // 4t(1-t): zero at both t=0 (sun right at the horizon) and t=1 (deck enters shadow), peaking at
    // the window's midpoint. The zero-at-both-ends shape removes any seam at either boundary — there
    // is nothing left to hand-tune once the window's width is fixed by the geometry above.
    //
    // shadowEntryDegrees <= 0 returns 0 outright rather than feeding a degenerate zero-width window
    // into InverseLerpClamped: a ground-hugging deck has no glow phase at all (see
    // ShadowEntryDepressionDegrees).
    public static float GlowPhase(float elevationDegrees, float shadowEntryDegrees)
    {
        if (shadowEntryDegrees <= 0f)
            return 0f;

        float belowHorizon = -elevationDegrees;
        float t = InverseLerpClamped(0f, shadowEntryDegrees, belowHorizon);
        return 4f * t * (1f - t);
    }

    // Fraction of the way this deck has suppressed the REMAINDER of §8's warm tail, once the deck
    // itself has gone dark. Ramps from 0 right where GlowPhase's window ends up to 1 at §8's own
    // NightFadeFloorDegrees — the point §8's tint is already zero, so full suppression there
    // multiplies an already-zero contribution rather than doing anything of its own.
    //
    // Reads SkyColorTemperature.NightFadeFloorDegrees directly rather than taking it as a parameter —
    // same choice PurpleLightMath makes reading the same constant, so there is exactly one place that
    // says where civil twilight ends rather than a second copy threaded in by every caller.
    //
    // suppressionStart is shadowEntryDegrees clamped up to 0: a ground-hugging deck (shadowEntryDegrees
    // 0, or WarmthMultiplier's cap pulling a very high override down to the fade floor) still gets a
    // smooth ramp from the horizon itself rather than a discontinuous jump to full suppression.
    public static float ShadowSuppressionPhase(float elevationDegrees, float shadowEntryDegrees)
    {
        float suppressionStart = shadowEntryDegrees < 0f ? 0f : shadowEntryDegrees;
        float belowHorizon = -elevationDegrees;
        return InverseLerpClamped(
            suppressionStart, -SkyColorTemperature.NightFadeFloorDegrees, belowHorizon);
    }

    // The multiplier Patch_SkyColorTemperature applies to §8's tint strength before it blends colour.
    // 1 is a pure no-op (no deck, in vacuum, or sun still up); above 1 is the glow phase's boost;
    // below 1 (down to a floor of exactly 0 at full opacity and full suppression) is the low-deck
    // "kills the sunset" case.
    //
    // inVacuum takes the Vacuum.cs convention: last parameter, required, early-returns the no-op
    // value before any of the geometry above runs — there is no cloud deck to underlight without air.
    public static float WarmthMultiplier(
        float elevationDegrees, float altitudeMetres, float cloudOpacity, bool inVacuum)
    {
        if (inVacuum)
            return 1f;

        float opacity = Clamp01(cloudOpacity);
        if (opacity <= 0f)
            return 1f;

        // Capped at §8's own fade floor: an override altitude far higher than any real troposphere
        // cloud would otherwise push the window past the point §8 has any tint left to modulate, and
        // the suppression phase (see its own header) already treats that as "no tail remains" rather
        // than needing a separate guard here.
        float shadowEntry = MathF.Min(
            ShadowEntryDepressionDegrees(altitudeMetres), -SkyColorTemperature.NightFadeFloorDegrees);

        float glow = GlowPhase(elevationDegrees, shadowEntry);
        float suppression = ShadowSuppressionPhase(elevationDegrees, shadowEntry);

        float multiplier = 1f + opacity * (GlowAmplitude * glow - suppression);
        // Defensive floor only: opacity and suppression are both already clamped to [0,1], so the
        // algebra never actually goes negative, but a consumer multiplying this straight into a blend
        // strength should never be handed a sign flip from float error.
        return multiplier < 0f ? 0f : multiplier;
    }

    // §23b (issue #88 OPTION 2): how strong the additive underlit-cloud LAYER is right now, in
    // [0, LayerAmplitude]. Zero is a true no-op — the overlay returns before its draw call rather than
    // drawing a transparent quad.
    //
    // DELIBERATELY THE SAME GlowPhase WarmthMultiplier USES, not a second window of its own. The two
    // lanes are drawing one physical event — a deck still catching direct sunlight from beneath after
    // the ground has fallen into Earth's shadow — and the whole reason §23's geometry was derived from
    // §19's Earth-shadow model rather than from a fresh horizon-dip approximation was to keep one
    // canonical answer to "how high is Earth's shadow" in the codebase (see this file's header, and
    // DESIGN.md §20/§20d on mired-space drift). A separate window here would reintroduce exactly that
    // drift one subsystem later, and the two lanes would disagree about when a sunset ends.
    //
    // NO OPACITY TERM, and its absence is load-bearing rather than an omission. How much cloud there
    // is enters this lane through the FIELD (CloudField.PatchIntensity thresholds on it, and
    // the mean subtraction is what makes a solid overcast contribute no structure at all), so
    // multiplying by it again here would count coverage twice and, worse, would make a full overcast
    // the strongest case when it is precisely the case with nothing spatial to say.
    //
    // The suppression phase is likewise absent, and correctly so: a low deck's shadow-entry angle is
    // near zero, so GlowPhase is near zero across the whole below-horizon range and this lane simply
    // never fires for one. Issue #88's "a low overcast kills the sunset" case stays entirely §23's,
    // where it belongs — killing a sunset is something done to the flat colour, not something an
    // additive pass can draw.
    public static float LayerStrength(
        float elevationDegrees, float altitudeMetres, float cloudFraction, bool inVacuum) =>
        LayerStrengthWithAmplitude(
            elevationDegrees, altitudeMetres, cloudFraction, LayerAmplitude, inVacuum);

    // The amplitude spelled out, so the invariants pinned against this in CloudUnderlightMathTests do
    // not silently stop meaning anything when the live A/B retunes LayerAmplitude — the same reason
    // CloudCoverDrift.FractionWithAmplitude exists alongside CloudCoverDrift.Fraction, and the seam
    // the harness sweep drives.
    public static float LayerStrengthWithAmplitude(
        float elevationDegrees, float altitudeMetres, float cloudFraction, float amplitude,
        bool inVacuum)
    {
        if (inVacuum)
            return 0f;

        if (Clamp01(cloudFraction) <= 0f || !(amplitude > 0f))
            return 0f;

        float shadowEntry = MathF.Min(
            ShadowEntryDepressionDegrees(altitudeMetres), -SkyColorTemperature.NightFadeFloorDegrees);

        return amplitude * GlowPhase(elevationDegrees, shadowEntry);
    }

    // Guards the a==b case InverseLerpClamped's callers above can genuinely reach (WarmthMultiplier's
    // cap can make suppressionStart equal the fade-floor bound exactly), where the plain
    // (v - a) / (b - a) division would be a NaN-producing 0/0 rather than a domain error. Treated as
    // "the window has zero width, so nothing has happened yet" — 0 — which is the right answer for
    // both call sites: a GlowPhase window can only be degenerate at the ground, before its 4t(1-t)
    // hump has anything to peak in, and a ShadowSuppressionPhase window can only be degenerate when
    // the glow phase has already consumed the entire tail, leaving no suppression tail behind it.
    private static float InverseLerpClamped(float a, float b, float v) =>
        MathF.Abs(b - a) < 1e-6f ? 0f : Clamp01((v - a) / (b - a));

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
