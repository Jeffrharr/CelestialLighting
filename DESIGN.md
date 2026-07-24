# CelestialLighting — Design

## Problem

"Tilt Planet! – Realism Overhaul" (Workshop 3520836521, delisted) had lighting the user liked —
axial-tilt-driven shadow direction, dramatic seasonal twilight — bundled with unrelated
economy/material changes. No code from it exists anywhere accessible; this mod is built purely
from public Workshop screenshots/description text plus decompiling *vanilla* `Assembly-CSharp.dll`
to understand RimWorld's existing celestial/sky systems. Scope is visual/atmospheric only — no
pawn work-speed or move-speed penalties.

Phase 1 covers exactly three effects: shadow direction, twilight color, and (experimental) a
subtle per-position shadow-length tilt across a single map. Two follow-up fixes were folded in
after in-game observation: `Patch_ShadowStrength` (vanilla's actual shadow-opacity source was
never patched, so moon shadows kept rendering) and `Patch_ShadowMeshPerimeter` (vanilla's own
shadow-mesh builder is missing a wall face on one of the four cardinal directions).

## 1. Shadow direction and length (`Patch_ShadowDirection`)

Vanilla's `GenCelestial.GetLightSourceInfo(Map, LightType.Shadow)` isn't derived from real sun
position at all: shadow length is a plain linear lerp across the day fraction, and the only public
signal for "how far below the horizon is the sun" — `CurCelestialSunGlow` — clamps to exactly `0`
the instant the sun dips under, discarding how far below it actually is. That makes vanilla unable
to distinguish "just set" from "polar midnight", and its near-pole handling
(`SunOffsetFractionFromLatitudeCurve`) is a two-point curve (`70°→0.2`, `75°→1.5`, evaluated on
*signed* latitude, flat-extrapolated everywhere else) tuned only to look plausible right at the
poles, not a real declination model. Concretely, this produced two visible bugs: shadows shrank to
nothing exactly at the day/night threshold instead of growing dramatically long right before
sunset (vanilla's own `CurShadowStrength` dips to `0` exactly at `glow == 0.6`), and there was no
way to get correct continuous midnight-sun/polar-night behavior at high latitude.

A Harmony Postfix on `GetLightSourceInfo`, active only for `LightType.Shadow`, replaces the result
outright with a small solar-position simulator (`Source/Formulas.cs`, "Solar-position shadow
simulator" section) — standard latitude/declination/hour-angle trigonometry, simplified (no
equation of time, and only one refraction constant rather than a full altitude-dependent refraction
curve — see the `AtmosphericRefractionDegrees` bullet below), using Earth's real axial tilt (23.44°)
rather than vanilla's fudge. `Source/SolarPosition.cs` is the thin `Map`/`Find`-touching adapter
that resolves latitude/declination/day-percent once per call and is shared by both this patch and
`Patch_ShadowStrength` below, so the two can never derive a different sun position from each other:

- `declination = 23.44° * declinationSign(dayOfYear)`, reusing the same one-line sinusoidal
  day-of-year term vanilla's own `GenCelestial.SunPositionUnmodified` already uses
  (`-cos(dayOfYear/60 * 2π)`, via `GenDate.DaysPerYear`).
- `elevation`: the standard formula
  `sin(elevation) = sin(lat)·sin(decl) + cos(lat)·cos(decl)·cos(hourAngle)`. At the poles,
  `cos(latitude) == 0` makes the hour-angle term vanish entirely, so elevation holds constant at
  `== declination` across the whole day — continuous midnight sun in local summer, total polar
  night in local winter, with no special-casing needed.
- If `elevation <= AtmosphericRefractionDegrees` (`-0.83°`, standard atmospheric refraction at the
  horizon — real sunrise/sunset happens here, not at geometric `0°`, because the atmosphere bends
  light near the horizon), `__result.intensity = 0f` and the vector is left alone. Vanilla's own
  night branch here (`num2 == -0.9f`, a folded mirror of the day curve) is meant to represent
  moonlight, but isn't tied to any real moon position — until `GameComponent_MoonPhase` exists to
  drive an actual moon-cast shadow (deferred subsystem, see below), this suppresses it rather than
  show a fake one.
- Otherwise: `azimuth` from the standard formula (falls back to tracking the hour angle directly
  when `cos(latitude)` or `cos(elevation)` is near zero — the pole, or sun at zenith/nadir, where
  azimuth is undefined but still needs to sweep smoothly through the day rather than divide by
  zero or hold still). Shadow length is `cot(elevation)`, clamped against both the near-zero
  blow-up and vanilla's own `ShadowMaxLengthDay`/`ShadowMaxLengthNight` (15) so the shader/mesh
  scale downstream doesn't need retuning. Intensity ramps from 0 to 1 over the 3° above
  `AtmosphericRefractionDegrees` (avoids a single-frame pop, and lets the last sliver of
  refraction-lingering light still cast a faint shadow instead of popping straight to zero) and
  otherwise stays at full strength — the dramatic-at-sunset look now comes entirely from length
  growing via `cot`, not from intensity fading in and out the way vanilla's `CurShadowStrength`
  did.

**Not yet verified against RimWorld's actual world-space convention**: which literal axis (`+X`
vs `-X`) corresponds to compass east isn't confirmed from decompiled code alone — verify in-game
that shadows sweep the expected direction (e.g. morning shadows pointing toward the map's west
edge) and flip the sign in `Formulas.ShadowVectorFromSunPosition` if it's mirrored.

`Patch_ShadowTilt` (below) reads `lightInfo.intensity` from the same patched call rather than a
second, independent `GenCelestial.CurShadowStrength(map)` call, so its per-section length scaling
always agrees with this existence/intensity decision instead of silently falling back to vanilla's
curve.

### `Patch_ShadowStrength` — the patch that actually suppresses moon shadows

Decompiling `SkyManager.SkyManagerUpdate` revealed that `Patch_ShadowDirection` alone was not
enough to suppress vanilla's fake night/moon shadow, despite zeroing `LightInfo.intensity`:
`SkyManager.SetSunShadowVector` sets the actual shader global (`MapSunLightDirection`, read by the
sun-shadow shader every frame) using `Vector4(vector.x, 0, vector.y, GenCelestial.CurShadowStrength(map))`
— the opacity/alpha component comes from a *second, entirely independent* call to
`GenCelestial.CurShadowStrength(Map)`, not from `LightInfo.intensity` at all. `MatBases.SunShadow`/
`SunShadowFade`'s colors are tinted the same way. Vanilla's `CurShadowStrength`
(`Clamp01(Abs(CurCelestialSunGlow(map) - 0.6f) / 0.15f)`) stays near full strength through the
night (that's what renders its moonlight look), so it kept driving a fully visible shadow
regardless of what `Patch_ShadowDirection` computed.

`Patch_ShadowStrength` is a second, independent Postfix directly on `CurShadowStrength(Map)` that
overrides its result with `Formulas.ShadowIntensityFromElevation(elevation)` — the exact same
function `Patch_ShadowDirection` uses, fed by the same `SolarPosition.ElevationForMap(map)` — so
the two patches share one source of truth for "is the sun up" and can't silently disagree the way
this bug let them.

## 2. Twilight color (`Patch_TwilightColor`)

Vanilla's `WeatherWorker.CurSkyTarget(Map)` blends between four fixed `SkyColorSet` thresholds
purely by `GenCelestial.CurCelestialSunGlow` — no latitude dependence. A Harmony Postfix nudges
(never replaces) the returned `SkyTarget.colors` toward a warm color
(`RGB(1, 0.45, 0.15)`) during a latitude-scaled twilight band centered on `sunGlow ≈ 0.35` (between
vanilla's `nightEdge` at 0.1 and `dusk` at 0.6), with both the band width and the maximum nudge
strength scaling with latitude via the same `LatitudeEffect.Context.Strength`. Blending (not
overwriting) preserves each `WeatherDef`'s own palette — rain/fog still read as distinct, just
warmed during dusk/dawn.

Deliberately recomputes `GenCelestial.CurCelestialSunGlow(map)` rather than reading
`__result.glow`: the latter may already be clamped by the active `WeatherDef.maxGlow`, which would
make twilight timing track weather-dimmed brightness instead of true sun position. The extra call
is trig-only, no allocation.

**Civil-twilight persistence (linger after geometric sunset).** Vanilla's `CurCelestialSunGlow` is
`Clamp01(InverseLerp(0, 0.7, sin(elevation)))`, so it pins to exactly `0` the instant the sun's
geometric elevation crosses the horizon and stays `0` all night — it carries no "how far below the
horizon" signal. Keyed purely on that value, the warm dusk tint necessarily collapsed to nothing at
the sunset instant and snapped the sky to full-night colour. Real dusk instead stays lit and warm
through *civil twilight* (sun `0°` down to `-6°`, ~20-30 min past sunset at mid-latitudes). The
factor is therefore now `Formulas.TwilightWarmthFactor`, the `max` of two colour-only pieces on the
same latitude-scaled peak height (`Formulas.TwilightPeakHeight`): the original above-horizon
glow-keyed band, and a below-horizon `CivilTwilightPersistence` pulse — a triangular ramp keyed on
true solar elevation (from the shared `SolarPosition.ElevationForMap`, the same sun position the
shadow patches use), `0` at the horizon, peaking a couple degrees under it, fading to `0` by `-6°`.
Both pieces are `~0` at the horizon so they meet without a jump. **Still colour-only — never writes
`.glow`**, so night stays exactly as dark as vanilla makes it and glow-reading mods (Dub's
Skylights) see an unmodified brightness; we only warm the *hue* of the darkening sky. This composes
with the planned §7 night-radiance floor (which sets night brightness) rather than fighting it — §2
tints, §7 will light.

## 3. Shadow tilt across the map (`Patch_ShadowTilt`) — experimental

The user asked whether shadows could be made subtly longer on one side of the map than the other
(a per-position variant of effect 1, at colony-map scale rather than map-tile-of-the-world scale).

Vanilla has no mechanism for this: `SkyManager.SetSunShadowVector` sets the shadow vector via
`Shader.SetGlobalVector(ShaderPropertyIDs.MapSunLightDirection, ...)` — one value for the entire
map, applied uniformly by every section's `SunShadow`-shader draw call
(`MeshMakerShadows`/`SectionLayer_SunShadows` build only the shadow mesh's *footprint*; the actual
push/extrusion along the shadow vector happens in the shader itself, reading that one global).

To vary shadow length by position without writing a custom shader or shipping an AssetBundle
(both ruled out — see "Clean-room provenance" below), `Patch_ShadowTilt` replaces
`SectionLayer_SunShadows.DrawLayer()` and draws each section's shadow submesh with its own
`MaterialPropertyBlock`, setting a per-section-rescaled copy of the same `_CastVect` vector (same
direction, magnitude scaled ±15% based on how far the section sits from the map center along the
shadow axis, using the map's actual runtime size — no hardcoded map dimensions).

This depends on `_CastVect` being a real per-material shader property (not only a value set via
`Shader.SetGlobalVector` with no corresponding `Properties {}` entry) so a
`MaterialPropertyBlock` override can win over the global for one draw call. The compiled shader
asset isn't inspectable from decompiled C#, so this is unverified until tested in-game. The
failure mode is safe: `MaterialPropertyBlock.SetVector` on an undeclared property name is a silent
Unity no-op — every section falls back to rendering with the global vector, i.e. exactly vanilla's
uniform look, no exception, no visual glitch. **Verify by loading a large map and comparing shadow
length near opposite map edges at a low sun angle; if there's no visible difference, this
subsystem is inert and should be considered deferred pending an actual shader edit, not treated as
broken.**

### Angular-size penumbra — softening shadow edges near the horizon (`PenumbraMath`)

Sections 1/3 treat the Sun as a point source, so shadows keep a perfectly hard edge and only their
overall opacity fades (via `Formulas.ShadowIntensityFromElevation`). The real Sun is a disk ~0.53°
across, so every shadow has a *penumbra* — a soft transition band at its edge where the disk is only
partially occluded. That band is narrow at high Sun but widens sharply toward the horizon, because
the same angular spread of the solar disk projects across a rapidly-lengthening shadow; real
sunrise/sunset shadows visibly blur and lose contrast, which a point-source model never reproduces.

The physics lives in `Source/PenumbraMath.cs` (System-only pure core, linked into the test project
like `Formulas.cs`). From the two solar-limb elevations (`elevation ± 0.2665°`, the Sun's mean
angular radius) it computes penumbra width per unit caster height,
`cot(elevation − α) − cot(elevation + α)` — monotonically increasing toward the horizon, and (unlike
width-relative-to-shadow-length) well-behaved at the zenith where the shadow length itself goes to
zero. A saturating map `w/(w+k)` turns that into a bounded softness in [0, 1].

Two consumers, one physical model:

- **Shipped, guaranteed-visible:** `PenumbraContrastFactor` (1 at high Sun, floored at
  `1 − MaxContrastLoss` = 0.4 near the horizon) multiplies the per-section shadow opacity in
  `Patch_ShadowTilt`'s draw path. A wider penumbra means a larger partially-shaded fraction of the
  footprint, i.e. a lower-contrast, washed-out shadow — approximated in the opacity channel the
  sun-shadow shader already reads, needing no shader property. Outright disappearance at the horizon
  stays `ShadowIntensityFromElevation`'s job; the two compose by multiplication. Elevation comes from
  the shared `SolarPosition.ElevationForMap`, so this reads the exact same Sun position as
  `Patch_ShadowDirection`/`Patch_ShadowStrength`.
- **Forward hook, no-op-safe:** `PenumbraSoftness` is also pushed into a `_PenumbraSoftness`
  `MaterialPropertyBlock` float. **Blocker (same as the `Patch_ShadowTilt` `_CastVect` caveat):** we
  can't confirm from decompiled C# whether `MatBases.SunShadow`'s compiled shader exposes an
  edge-softness uniform. `MaterialPropertyBlock.SetFloat` on an undeclared name is a silent Unity
  no-op, so this drives a true geometric edge blur *if and only if* such a uniform is ever confirmed,
  and otherwise does nothing — the contrast attenuation above is what actually ships. Verify in-game
  (or by inspecting the shader asset) before assuming the mesh edge itself blurs.

Conflict risk: none beyond `Patch_ShadowTilt`'s existing profile — this only scales the opacity
component of the same per-section draw and adds one no-op-safe float; it touches no new vanilla
member (so no `ApiCompatibilityTests` addition is needed). Clean-room: solar angular diameter and
umbra/penumbra limb geometry are standard textbook astronomy/optics, not derived from any external
mod. Visual only — no gameplay effect.

## 4. Missing north-facing shadow wall (`Patch_ShadowMeshPerimeter`)

In-game testing showed shadows consistently missing their "top" edge — a wall whose north side
should have been throwing a shadow rendered with a gap instead of a quad. Decompiling
`SectionLayer_SunShadows.Regenerate()` in full confirmed this is a genuine vanilla bug, not
something our patches introduced: the method builds an exposed-edge shadow "wall" quad for a
building's west (`i - 1`), east (`i + 1`), and south (`j - 1`) neighbors, but there is no fourth
`if` block for the north (`j + 1`) neighbor at all. Under vanilla's own narrow day/night
shadow-angle range this gap was apparently never noticed; `Patch_ShadowDirection`'s full
elevation/azimuth simulator sweeps shadows through every compass direction across the day and
season, which makes the missing face visible far more often.

Harmony can't Postfix-append to an already-`FinalizeMesh`'d submesh, so `Patch_ShadowMeshPerimeter`
is a full Prefix replacement (`__runOriginal = false`) that reimplements `Regenerate()` verbatim,
with the missing `j + 1` block added. That block mirrors the existing south-facing block's
triangle-winding pattern but traverses the shared edge in the opposite direction — the same way
the existing west/east blocks mirror each other for their opposite sides — so the added quad's
outward normal points north instead of south. Everything else in the method is copied unchanged
from vanilla; this is a gap fill, not a redesign. `SectionLayerAccess.cs` holds the one reflection
lookup for `SectionLayer.section` (a protected field with no public accessor), shared with
`Patch_ShadowTilt` rather than duplicated.

## 5. Shadow opacity not actually reading our intensity (`Patch_ShadowStrength`)

Also caught during in-game testing: even after `Patch_ShadowDirection` zeroed
`LightInfo.intensity` for a below-horizon sun (to suppress vanilla's fake moon shadow — see
section 1), moon shadows still rendered. Decompiling `SkyManager.SkyManagerUpdate` explains why:
the shader global that actually controls shadow opacity (`MapSunLightDirection`, read by the
sun-shadow shader every frame) is set via
`Vector4(vector.x, 0, vector.y, GenCelestial.CurShadowStrength(map))` — a *second, independent*
call to `GenCelestial.CurShadowStrength(Map)`, not `LightInfo.intensity`. `MatBases.SunShadow`/
`SunShadowFade`'s colors are tinted the same way. Vanilla's `CurShadowStrength`
(`Clamp01(Abs(CurCelestialSunGlow(map) - 0.6f) / 0.15f)`) stays near full strength through the
night — that's what was still rendering a fully visible "moon" shadow regardless of what
`Patch_ShadowDirection` computed.

`Patch_ShadowStrength` is a Postfix directly on `CurShadowStrength(Map)` that overrides its result
with `Formulas.ShadowIntensityFromElevation(elevation)` — the same function
`Patch_ShadowDirection` uses, fed by the same `SolarPosition.ElevationForMap(map)` (see
`Source/SolarPosition.cs`, a thin adapter shared by both patches) — so the two can never disagree
about whether the sun is up the way this bug let them.

## 6. Moon position (`GameComponent_MoonPhase`) — implemented (moonlight consumer deferred to §7)

Subsystems 1 and 5 already suppress vanilla's fake, position-less night/moon shadow (see section 1),
leaving a gap this subsystem fills: a *real* moon with a position in the sky, so night can be lit
(and shadowed) by something that actually rises, sets, and waxes/wanes.

The moon reuses the exact machinery the sun already uses. `Source/Formulas.cs` already turns
`(latitude, declination, hourAngle)` into elevation/azimuth; a moon is just a second body fed
through the same functions, differing only in how we derive its declination and hour angle:

- **Phase** comes from the sun–moon elongation. Track the moon's ecliptic longitude as the sun's
  plus an offset that advances once per configurable **synodic period** (a game-wide value — one
  moon shared across all maps/tiles, so `GameComponent`, not `MapComponent`). Illuminated fraction
  (0 = new, 1 = full) is `(1 − cos(elongation)) / 2`, and the eight-way labelled enum (New, Waxing
  Crescent, First Quarter, …) falls out of the elongation angle plus its sign (waxing vs waning).
- **Position** comes from that same ecliptic longitude fed back through the solar-position formulas
  with the moon's own hour angle, giving a moon altitude/azimuth for the current tile and tick. A
  first cut can ignore the ~5° lunar orbital inclination and lunar parallax — a Moon-on-the-ecliptic
  approximation is more than accurate enough for a shadow direction and a brightness scalar.

**Scope: one moon to start.** Canon says the planet has several ("*one of* the moons of this
planet", per vanilla's eclipse letter), but we model a single representative moon — everything below
needs only one, and nothing in Odyssey requires more (it ships no moon by default). The *only* thing
that would need more than the Moon-on-the-ecliptic approximation is astronomical eclipse-triggering
(§10): geometric eclipses need the orbital inclination and nodes, because without them the moon
would transit the sun every single new moon. Shadows and moonlight never need that, so it stays out
of the first cut and lives with the opt-in eclipse feature.

Two consumers, both reusing existing adapters so they can never derive a different moon than each
other (the same discipline `SolarPosition.cs` already enforces for the sun across
`Patch_ShadowDirection`/`Patch_ShadowStrength`):

1. **Moon-cast shadows** — when the sun is below the horizon but the moon is up, feed the moon's
   elevation/azimuth into the same shadow vector/strength path, with strength additionally scaled by
   illuminated fraction (a new-moon night casts no shadow; a full moon casts a soft one).
2. **Moonlight** — a night-brightness contribution for subsystem 7 below, scaled by phase and moon
   altitude.

Clean-room note: elongation→phase and synodic-period→ecliptic-longitude are standard textbook lunar
approximations, the lunar counterpart of the sun math already justified under "Clean-room
provenance" — no external mod referenced.

**Implementation.** The pure core lives in `Source/MoonMath.cs` (System-only, linked into the test
project the same way `Formulas.cs` is): synodic cycle position from the absolute tick count,
elongation, illuminated fraction `(1 - cos elongation) / 2`, the eight-way `MoonPhase` enum, and the
moon's on-the-ecliptic declination/hour-angle. Position deliberately reuses `Formulas`' own solar
equations — the moon's declination mirrors `DeclinationSign` evaluated at (sun angle + elongation),
and its "day percent" is the sun's lagged by the elongation, so `SolarElevationDegrees`/
`SolarAzimuthDegrees` compute the moon exactly as they do the sun (at new moon the two collapse to
the same values, verified in `MoonMathTests`). `GameComponent_MoonPhase` is the game-wide single
moon (one shared cycle, no per-tick state — it is derived from `Find.TickManager.TicksAbs`, with only
the configurable cycle length persisted); `Source/MoonPosition.cs` is the per-map adapter that
combines that cycle with the map tile's latitude. Of the two consumers, **moon-cast shadows are
wired in**: `Patch_ShadowDirection`/`Patch_ShadowStrength`'s below-horizon night branches now defer
to `MoonPosition.ShadowForMap`, so a real moon casts a faint, phase-scaled shadow where they
previously suppressed vanilla's fake one. **Moonlight is exposed but not yet consumed** —
`MoonPosition.MoonlightBrightnessForMap` returns a normalized 0–1 scalar marked with a
`TODO(integration)` for §7 to sum with its starlight/airglow floors.

## 7. Night-sky radiance: stars, airglow, moonlight (`Patch_NightRadiance`)

Vanilla night is a flat glow floor. We want night brightness to instead be the *sum of a few
physically-motivated dim light sources*, so that darkness is emergent — legible under a full moon,
much darker on a new moon — rather than a hard on/off toggle:

- **Starlight** — a near-constant faint floor (the background sky is never truly zero under an open
  sky).
- **Airglow** — faint atmospheric self-emission, a second small constant floor.
- **Moonlight** — the phase-and-altitude-scaled contribution from subsystem 6.

Summing these (rather than picking a max) means a clear full-moon night reads distinctly brighter
than a new-moon night, and both read brighter than an overcast night once weather dimming is folded
in. Each source is **independently tunable in settings**, which is also how we deliver the user's
original ask for *true pitch-black unlit nights*: pitch-black is simply the starlight and airglow
floors set to zero, not a separate special-case hack. A "background stars / atmospheric night glow"
toggle (default on) gives the atmospheric look; turning it off, or sliding the floors to zero,
yields genuinely black unlit nights.

Where it writes: a Postfix on `WeatherWorker.CurSkyTarget` sets `__result.glow` **only** (never
`.colors`) and **only below the horizon**, so it composes cleanly under subsystem 2's twilight blend
— §2 warms `.colors` during the dusk/dawn band *above* the horizon, §7 owns the glow floor *below*
it, and the two never touch the same field in the same regime. The night radiance sets the floor the
twilight warm-tint then rides on top of at dusk/dawn. As with subsystem 2, we recompute the sun's
true elevation from the shared `SolarPosition`/`Formulas` simulator rather than reading an
already-weather-clamped `__result.glow`, so night brightness tracks true celestial geometry and
weather dimming stays a separate, later multiply.

The floor fades in across a **textbook twilight band**: 0 above the atmospheric-refraction horizon
(`-0.83°`, the same constant the shadow simulator uses), ramping to full ownership of the glow at the
end of astronomical twilight (`-18°`, past which scattered sunlight no longer lightens the sky). The
blend is a `Lerp` (not a `Max`) precisely so the floor can cross *below* vanilla's night glow for
true pitch-black, not only above it for a full moon.

Pure-function boundary holds: the star/airglow floor constants and the phase/altitude→glow and
band-blend curves live in `Source/NightRadianceMath.cs` (a dependency-free `System`-only static
class, linked into the test project) with offline `[TestCase]` coverage in
`NightRadianceMathTests.cs`; `Patch_NightRadiance.cs` is the thin adapter that reads sun/moon
elevation off live state and blends the resulting glow into the sky target.

**Moon seam (deferred dependency on §6/#3).** Moonlight needs the moon's illuminated fraction and
altitude, which the moon-position subsystem (§6) will provide — it is not yet merged. `MoonSeam.cs`
is a minimal self-contained hook (`Func<Map, MoonState>`) whose default reports "no moon" (new moon
below the horizon), so moonlight contributes exactly 0 and the shipped floor is starlight + airglow
only — a correct, standalone behavior. Wiring `GameComponent_MoonPhase` in later is a one-line
reassignment of `MoonSeam.Provider` (marked `// TODO(integration:`). The per-source tunables and the
atmospheric-glow master toggle live in `NightRadianceSettings.cs`, holding the DESIGN defaults until
the settings/presets screen (below) is built to write them (also `// TODO(integration:`).

## 8. Sky colour-temperature curve (planned)

Subsystem 2 warms the sky toward a single fixed hue inside one twilight band. This generalizes that
into a continuous **colour-temperature curve keyed on sun altitude**: the sky and direct sunlight
shift from a warm low-colour-temperature glow near the horizon (~2000 K) up to a neutral daylight
white near the zenith (~5772 K, the Sun's actual effective temperature), passing through the
familiar golden-hour warmth on the way. This is the physically-grounded version of "dramatic
seasonal twilight" — because day length and peak sun altitude already vary with latitude and season
(vanilla `GenCelestial` + our own simulator), a high-latitude winter day that never lifts the sun
far above the horizon *stays* warm all day, for free.

Blackbody colour temperature → RGB is a standard tabulated conversion (textbook, not
mod-specific). It composes with subsystem 2 rather than replacing it: §2's dusk/dawn warm nudge can
become one anchor point on this curve. Critically it stays in the same low-risk lane as §2 — it
blends `WeatherWorker.CurSkyTarget`'s **colour only, never `.glow`** — so it does not disturb the
brightness other mods read (see "Conflict risk").

## 9. Low-light desaturation / Purkinje shift (planned)

As scene brightness falls, human vision loses colour discrimination and everything drifts toward a
dim blue-grey (the Purkinje shift — rod vision taking over from cones). This subsystem reproduces
that: as the sky glow drops toward night, blend the sky colour toward a desaturated cool grey, most
strongly on the darkest (new-moon, overcast) nights. It's cheap, distinctly atmospheric, and makes
our darkness read as *night* rather than as a uniformly dimmed day.

It composes directly with subsystem 7 — §7 sets *how much* light the night sky provides, §9 sets
*how that light reads* as colour drains out of it — and, like §8, it is a colour-only blend on
`CurSkyTarget`, so it stacks cleanly with §2/§8 and stays clear of the glow value. The
brightness→saturation falloff curve is a plain function in `Source/Formulas.cs` with offline
coverage.

## 10. Eclipse: natural and unnatural (planned)

RimWorld's `Eclipse` `GameCondition` fires on a random timer, lasts far longer than a real solar
eclipse (roughly a game-day), and darkens the map with a flat on/off dim. That length is physically
impossible — a real total eclipse's totality is minutes and the whole partial-to-partial span is a
couple of hours. Rather than paper over that, we split eclipses into two deliberately distinct
concepts, keyed on whether the eclipse's *timing and duration are astronomically real*:

Both share one piece of math: a **coverage ramp** from disk-overlap (standard circle-intersection)
geometry that drives a gradual partial → near-total darkening and the characteristic wan eclipse
colour, replacing the vanilla flat dim. Only *what moves the discs together* and *how long they stay*
differ.

### 10a. Natural eclipse (opt-in, off by default)

Driven by the modeled moon's real position (§6): when the moon geometrically transits the sun, fire
an eclipse that lasts the **correct, short real-eclipse duration** — the event triggers *during an
actual eclipse* and ends when the discs part. Astronomically accurate in both when it happens and
how long it lasts.

This steps **one notch outside the visual-only remit** — it changes *when* (and, by shortening the
duration, *how long*) a gameplay event occurs (solar-power loss, mood) — so it is opt-in and **off by
default**. Design consequences:

- It requires the moon's **orbital inclination and nodes** (see §6 scope note). With the flat
  Moon-on-the-ecliptic approximation the moon would transit every new moon and eclipses would fire
  ~monthly; the tilt + nodal geometry is what makes them appropriately rare and correctly timed. This
  feature owns that extra modeling — shadows/moonlight don't pay for it.
- It drives vanilla's *existing* `Eclipse` condition (with a corrected short duration) rather than a
  new one, so all downstream mods that react to eclipses keep working. While this mode is on, the
  random vanilla eclipse incident is suppressed (otherwise they double-fire) — the random ones can
  instead be surfaced as *unnatural* eclipses (10b), a setting.

### 10b. Unnatural eclipse (default cosmetic replacement of the vanilla event)

The vanilla eclipse is unrealistically long, so we lean into that instead of hiding it: replace the
vanilla event's flat darkening with a **visible moon disc that quickly slides in front of the sun,
parks over it for the (vanilla) duration, then slides away and disappears**. The scripted
fly-in / hold / fly-out is *deliberately* unnatural motion — an in-fiction wink that a day-long
stationary transit is not real orbital mechanics — which is exactly why it's the honest way to render
an event whose duration was never physical.

This keeps the vanilla event's random timing, duration, and gameplay untouched — it is a **visual-only
replacement** and is the on-by-default cosmetic behaviour. It does **not** need the real moon model:
the disc is a scripted visual body, not the orbital §6 moon, and its motion feeds the shared coverage
ramp as it covers and uncovers the sun.

(The related lunar-eclipse "blood moon" is a third-party event — see §12 — and we only *render* it,
never trigger it.)

## 11. Aurora and solar-flare sky tinting (planned, cosmetic overlay on vanilla events)

Same principle as §10, for the `SolarFlare` and (Biotech) aurora-style conditions: shift the night
sky toward auroral greens/reds while the condition is active. **Visual only — the flare's electronics
disruption and every other gameplay effect are left entirely untouched.** Lowest priority of the
planned set; listed so the design is complete. Auroral emission colours (oxygen green ~557 nm, red
~630 nm) are physical constants, not mod-specific.

## 12. Blood moon rendering (planned, soft-compat with a third-party event)

A "blood moon" is a *lunar* eclipse — the moon passing into the planet's shadow (umbra) and turning
crimson — as opposed to the *solar* eclipse of §10. There is no vanilla blood moon; the well-known
one is **Vanilla Races Expanded – Sanguophage**'s `VRE_BloodMoonCondition` (packageId
`vanillaracesexpanded.sanguophage`), a night-time `GameCondition` whose in-game text is lore-
consistent with ours ("*one of the moons of this planet has orbited into the rimworld's umbra…*").

Since we're the mod that actually models moonlight colour, we should make sure a blood moon *looks*
right under our lighting instead of rendering as an ordinary silver-blue moonlit night. When that
condition is active, tint our moonlight and moonlit-sky (§6/§7) deep crimson so the whole night
reads red — bright enough to still be a *moonlit* night (a blood moon is a full moon), not darkness.

Boundaries:

- **Soft dependency, not a requirement.** Detect the condition by def lookup / reflection guarded on
  the mod being present; never a hard assembly reference. Add `vracesexpanded.sanguophage` to
  `About.xml`'s `loadAfter` when this ships so our render reads its state after it starts.
- **Visual only.** We recolour the night; we touch none of VRE's sanguophage/hemogen mechanics.
- We *react to* the third-party condition; we never trigger it (contrast §10's opt-in solar
  trigger). If both this and §10's astronomical mode ever coexist, a blood moon should line up with
  a full moon — but that coupling is out of scope for a first pass; reacting to the live condition is
  enough to "look how we'd expect."

## Settings, presets, and the brightness floor (planned)

Two cross-cutting settings ideas that span the subsystems above:

- **Opinionated presets.** Ship a small number of named presets (e.g. "Realistic" vs
  "Cinematic/Pretty") that set the correlated knobs together — shadow length/strength (§1/§3), night
  radiance floors (§7), desaturation strength (§9) — so most players pick one preset and never open
  a slider. Individual sliders remain for anyone who wants them.
- **Minimum-brightness accessibility floor.** A user-set (and hotkey-toggleable) floor on displayed
  night brightness. This is the deliberate complement to true pitch-black nights (§7): pitch-black
  for atmosphere by default, one keypress to a legible floor when a player actually needs to see to
  play. Because it clamps the *displayed* glow upward, it must be applied as the last step, after
  §7's floors and any weather dimming.

All tunables persist via the mod's `ModSettings`; the preset buttons just write bundles of those
same values, so a preset is never a separate code path.

## Conflict risk

Decompiled the user's local Dub's Skylights 1.6 copy (`Dubwise.DubsSkylights`) — its patches
(`Patch_GameGlowAt`, `Patch_NeedInterval`, `Patch_SectionLayer_LightingOverlay_Regenerate`,
`Patch_SetRoof`, `Patch_SpawningWipes`, `GardenPatches`) touch none of `GetLightSourceInfo`,
`CurSkyTarget`, `CurShadowStrength`, `SectionLayer_SunShadows.DrawLayer`, or
`SectionLayer_SunShadows.Regenerate`. Dub's Skylights reads `SkyManager.CurSkyGlow` (the map's
overall glow value), which none of these five patches modify — we only touch shadow-vector
direction/length/strength and `CurSkyTarget`'s *colors*, never `.glow` itself. So existing light
sources (`CompGlower`, `GlowGrid.GroundGlowAt`) are entirely unaffected by this mod: there's no
shared computation to skip or interact with in the first place.

`SectionLayer_SunShadows` is a leaf, internal, non-subclassed vanilla type with `DrawLayer()` and
`Regenerate()` overrides, both now Prefixed here (`Patch_ShadowTilt`, `Patch_ShadowMeshPerimeter`)
— low risk of another mod patching either exact method, but if one does, Harmony will only run
whichever prefix returns `true` from `__runOriginal` handling last (both returning `false` means
only one prefix's replacement actually runs); no known mod in this setup currently does.
`GenCelestial.CurShadowStrength(Map)` is a small public static leaf method with a single call site
inside `SkyManager.SkyManagerUpdate` — same low-risk profile as `GetLightSourceInfo`.

## Clean-room provenance

The shadow simulator's elevation/azimuth math is standard textbook solar-position trigonometry
(the same equations used by any planetarium/sundial calculation), not derived from vanilla or from
Sjaandi's mod; it reuses only one public-domain trig line already present in vanilla's
`GenCelestial.SunPositionUnmodified` (a standard sinusoidal day-of-year declination term, not a
substantial or copyrightable expression) for `DeclinationSign`. This mod copies no code, assets, or
shaders from Sjaandi's mod; its feature set derives from the public Workshop description plus
standard astronomy, and any behavioral resemblance is convergence on the same real-world physics.
The shadow-tilt subsystem deliberately avoids
writing or shipping a custom shader — it only calls Unity's existing `MaterialPropertyBlock` API
against RimWorld's own, already-compiled `MatBases.SunShadow` material.

## Pure-function core (`Source/Formulas.cs`)

Every formula above — latitude strength, the solar-position simulator (declination/elevation/
azimuth/shadow-length/intensity), the twilight band/factor curve, and the shadow-length position/
scale math — lives in `Source/Formulas.cs`, a static class with no `UnityEngine`/`Verse` dependency
at all (only `System`). `LatitudeEffect.cs`, `SolarPosition.cs`, and the four patch files are thin
adapters: they pull primitives off live `Map`/`Section`/`Find` state and hand them to `Formulas`,
which does the actual math and returns primitives/plain structs back.

`Tests/CelestialLighting.Tests/CelestialLighting.Tests.csproj` links `Source/Formulas.cs` directly
into the test project via `<Compile Include>` (not a copy), so `FormulasTests.cs` exercises the
exact code that ships, running standalone under `dotnet test` with no RimWorld/Unity assembly
present. This pattern exists because a real formula bug (an earlier version of the shadow-direction
math flattened shadows at every equinox) was caught by a one-off manual review, not by any
automated test — the API compatibility tests below only check that vanilla members still exist,
not that our own math is correct. `FormulasTests.cs` covers each function's edge cases directly,
including the solar-position simulator's boundary conditions: equatorial sunrise/noon/midnight,
constant elevation at the poles (the midnight-sun/polar-night case), and the shadow-length clamps
near the horizon and at zenith.

## API compatibility tests

`Tests/CelestialLighting.Tests/ApiCompatibilityTests.cs` uses Mono.Cecil to verify every vanilla
type/method/field these patches depend on still exists, including asserting
`GenDate.DaysPerYear == 60` by value (not just existence) since `Formulas.DeclinationSign`'s `/60f`
divisor would silently desync seasons if that constant's value ever changed. Run `./test.sh` before
loading the game after any RimWorld update — it runs both `ApiCompatibilityTests` and
`FormulasTests` together.
