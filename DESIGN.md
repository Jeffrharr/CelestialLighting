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
`__result.glow`, so twilight *timing* is anchored to true sun position rather than to displayed
brightness. Note the original rationale for this — that `__result.glow` "may already be clamped by
the active `WeatherDef.maxGlow`" — was overstated: `maxGlow` defaults to 1.0 and is set exactly once
in all of vanilla (see §13), so it almost never clamps anything. The decision is still right, and
since §13 landed it is *more* important, not less: §7 now rewrites `.glow` below the horizon, so
reading it here would make golden hour track the night floor. The extra call is trig-only, no
allocation.

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
previously suppressed vanilla's fake one. **Moonlight is now consumed by §7**: at startup
`CelestialLightingMod` reassigns §7's `MoonSeam.Provider` to read `MoonPosition`, handing §7 the
moon's live illuminated fraction and per-tile altitude so the night floor brightens under a full
moon and stays dark on a new one. (`MoonPosition.MoonlightBrightnessForMap` remains as a bare 0–1
convenience accessor; §7 takes the raw fraction+altitude instead and applies its own
`MaxMoonlightGlow` weighting via `NightRadianceMath.MoonlightGlow`.)

## 7. Night-sky radiance: stars, airglow, moonlight (`Patch_NightRadiance`)

Vanilla night is a flat glow floor. We want night brightness to instead be the *sum of a few
physically-motivated dim light sources*, so that darkness is emergent — legible under a full moon,
much darker on a new moon — rather than a hard on/off toggle:

- **Starlight** — a near-constant faint floor (the background sky is never truly zero under an open
  sky).
- **Airglow** — faint atmospheric self-emission, a second small constant floor.
- **Moonlight** — the phase-and-altitude-scaled contribution from subsystem 6.

Summing these (rather than picking a max) means a clear full-moon night reads distinctly brighter
than a new-moon night; §13's weather dimming then darkens how all of them *appear* under a cloud
deck, without altering the glow value itself. Each source is **independently tunable in settings**, which is also how we deliver the user's
original ask for *true pitch-black unlit nights*: pitch-black is simply the starlight and airglow
floors set to zero, not a separate special-case hack. A "background stars / atmospheric night glow"
toggle (default on) gives the atmospheric look; turning it off, or sliding the floors to zero,
yields genuinely black unlit nights.

Where it writes: a Postfix on `WeatherWorker.CurSkyTarget` sets `__result.glow` **only** (never
`.colors`) and **only below the horizon**, so it composes cleanly under subsystem 2's twilight blend
— §2 warms `.colors` during the dusk/dawn band *above* the horizon, §7 owns the glow floor *below*
it, and the two never touch the same field in the same regime. The night radiance sets the floor the
twilight warm-tint then rides on top of at dusk/dawn. As with subsystem 2, we recompute the sun's
true elevation from the shared `SolarPosition`/`Formulas` simulator rather than reading
`__result.glow`, so night brightness tracks true celestial geometry rather than whatever earlier
patches left in the field. The original wording here — that the incoming glow was
"already-weather-clamped" — was wrong (see §13: `maxGlow` almost never clamps), but the sentence it
ended on has since come true in a different way: weather dimming *is* a separate multiply, applied by
§13 on the colour channel rather than folded into this floor.

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

**Moon seam (wired to §6).** Moonlight needs the moon's illuminated fraction and altitude. `MoonSeam.cs`
is a minimal self-contained hook (`Func<Map, MoonState>`) whose default reports "no moon" so §7 builds
and unit-tests standalone; now that §6 is merged, `CelestialLightingMod` startup reassigns
`MoonSeam.Provider` to read the live `MoonPosition`, so the floor brightens under a full moon and stays
dark on a new one. The per-source tunables, the atmospheric-glow master toggle, and the §7a
minimum-brightness clamp live in `NightRadianceSettings.cs`, holding the DESIGN defaults until the
settings/presets screen (below) is built to write them (`// TODO(integration:`).

### 6a. Making moon shadows visible (`Patch_MoonShadowColor`)

**Problem.** §6 computed moon shadows correctly and they still could not be seen. `Patch_ShadowDirection`
sets a real, moon-position-derived shadow vector at night and `Patch_ShadowStrength` sets a matching
alpha, both from the one `MoonPosition.ShadowForMap` adapter. But that alpha is only a lerp *factor*:

```csharp
color = Color.Lerp(Color.white, curSky.colors.shadow, GenCelestial.CurShadowStrength(map));
MatBases.SunShadow.color = color;   // and SunShadowFade
```

and vanilla's night `colors.shadow` is nearly white — `(0.85,0.85,0.85)` on Clear, `(0.92,…)` on every
other weather — because vanilla never intended to draw a real night shadow. That caps a night shadow at
a 15% darkening even at alpha 1.0; at `MoonShadowMaxStrength` (0.28) it worked out to **4.2%** on a clear
night and **2.2%** otherwise, and less for anything short of a full moon. Below the perceptual floor,
and doubly so on ground §7a has already pulled toward black. Structurally the same trap as §7a: we
owned a factor while vanilla owned the colour that bounded it.

**Approach.** A Postfix on `WeatherWorker.CurSkyTarget` replaces `colors.shadow` at night with a value
derived by *inverting vanilla's own lerp* — `MoonMath.MoonShadowColorValue` solves
`1 - strength·(1 - value) = 1 - peakDarkening` at `strength == MoonShadowMaxStrength`, so a full moon at
the zenith renders exactly `MoonShadowPeakDarkening` (0.25) darker than the lit ground.

- **Fixing the input, not adding a second writer of `MatBases.SunShadow`.** Vanilla keeps the lerp, so
  the moon's own strength keeps scaling the result — a half-lit moon reads at half the contrast with no
  second curve to keep in sync — and the weather-event branch that skips the lerp and uses
  `colors.shadow` directly stays correct too.
- **One owner per field.** Nothing else in the mod writes `colors.shadow`: §2 twilight, §8
  colour-temperature, §9 desaturation and §11/§12 all work on `colors.sky` / `overlay` / `saturation`.
  So there is no composition order to argue about, unlike §7a which had to inject after everything.
- **Night only**, above-horizon returns early: vanilla's daytime shadow colours are already correct and
  well-tuned, and this must not touch every daylight shadow in the game.
- **Neutral grey, not blue-tinted moonlight.** §9 owns the night's colour cast; a tint here would fight
  it for the same look.
- Gated on `MoonShadows` — with §6 off there is no moon shadow to make visible, and darkening
  `colors.shadow` would only deepen vanilla's own fake night shadow, the opposite of that flag's
  faithful-baseline promise.

**Conflict risk.** Shares `WeatherWorker.CurSkyTarget` with five of our own postfixes and with any mod
that recolours the sky; we touch only `.shadow`, which none of them do. Rendering-only.

### 7a. Pitch-black nights — visual overlay darkening (`Patch_PitchBlackOverlay`)

§7 writes only `SkyTarget.glow`, which drives *gameplay* light and everything that reads
`SkyManager.CurSkyGlow` (GlowGrid, Dub's Skylights) — but not the on-screen darkness. RimWorld always
draws the terrain sprites and dims them with the `MatBases.LightOverlay` material (coloured from
`SkyTarget.colors.sky` inside `SkyManagerUpdate`), so a glow floor of `0.04` and `0` render nearly
identically: a "pitch-black" moonless night still looks dim-grey, never black. That gap is why the
original "true pitch-black unlit nights" ask wasn't actually visible from the glow floor alone.

`Patch_PitchBlackOverlay` is a Postfix on `SkyManager.SkyManagerUpdate` that runs *after* vanilla (and
§9's desaturation) have composed `MatBases.LightOverlay` / `MatBases.FogOfWar`, and lerps their colours
toward opaque black by how far below full brightness the night floor sits
(`NightRadianceMath.OverlayBrightnessFactor(CurSkyGlow, minBrightness)` — linear between two anchors
derived from §7's own source constants: fully black at/below `OverlayDarkGlow` (= starlight + airglow,
the baseline of darkness) and untouched at/above `OverlayFullBrightGlow` (= that floor plus a full moon
at zenith), so only *moonlight* buys screen brightness back). Injecting at the composed overlay, not in a `SkyTarget` postfix, is deliberate: it darkens
*last* and never fights §2/§9 for ownership of `colors.sky`. A bright moonlit night keeps vanilla
brightness; a moonless / floors-off night blacks out — down to the `MinNightBrightness` clamp, the
playability floor (ships at 0 = truly pitch black; raise it via settings if that is hard to navigate,
a call to be revisited once every light source is in). Note this darkens one global material, so it is
the *outdoor* arm only, and specifically it does **not** make interiors black — see §7b, which exists
because the assumption originally recorded here ("roofed cells receive no skyglow, so unlit interiors
are already dark") is true of `GlowGrid.GroundGlowAt` (gameplay light) but false of the renderer.
Gated by `CelestialLightingFeatures.PitchBlackNights` (separate
from the §7 glow floor, since this is a strong taste-dependent visual some players will want off) and
only active while `NightRadiance` is (the darkening is defined relative to §7's floor). Conflict risk:
it shares the `MatBases.LightOverlay`/`FogOfWar` globals RimWorld itself rewrites every frame, and only
reads them after vanilla sets them, so it composes rather than races; visual-only, no gameplay math.

### 7b. Indoor sky occlusion — black unlit interiors (`Patch_IndoorSkyOcclusion`)

**Problem.** §7a can darken the sky arbitrarily and a sealed cave still renders visibly lit. On-screen
brightness is not one global value: `Verse.SectionLayer_LightingOverlay` bakes a per-vertex *sky cover*
into the lighting mesh's vertex alpha, and the shader mixes the sky colour in by how uncovered each
vertex is. Vanilla clamps that cover for roofed cells to a constant and never raises it:

```csharp
private const byte RoofedAreaMinSkyCover = 100;                   // 100/255 == 0.392
if (flag /* a neighbouring cell is roofed */ && a < 100) a = 100;
```

So every roofed tile renders at ~61% of the current sky colour, day and night. No amount of §7a
darkening reaches black indoors, because an interior is a fixed fraction *of the sky* — it only goes
black if the sky does. This is also why sky/atmospheric glow visibly "applies indoors" even though
`GlowGrid.GroundGlowAt` correctly returns early for roofed cells: the *gameplay* light is right and the
*render* is not. Vanilla's 0.392 is a legibility compromise (it is what lets you build an unlit room and
still see inside it), not a physical model.

**Approach.** A Postfix on `SectionLayer_LightingOverlay.Regenerate` re-walks the section vanilla just
baked and raises that alpha: full cover (255) for roofed cells, so an unlit interior is lit by its lamps
or not at all. Per-cell logic is the pure `IndoorOcclusionMath`; the patch is a thin adapter that reads
`map.roofGrid` / `map.edificeGrid` and rewrites `mesh.colors32`.

- **Corner vertices are averaged, centres are not.** A lattice corner is shared by up to four cells, so
  its occlusion is their mean — 1.0 deep inside a building, 0.5 on an exterior wall line. The shader
  interpolates across the quad, so interior blackness *fades out* over the wall instead of printing a
  black halo on the ground outside. The denominator is the count of cells actually inside the map, which
  also makes the two sections that each bake a shared boundary vertex agree (no 17-cell seams).
- **Leaky doors.** Vanilla lumps doors in with roof for cover (`altitudeLayer == AltitudeLayer.DoorMoveable`
  is one of the disjuncts that sets its roofed flag) and a closed door's `blockLight` suppresses glow too,
  so at full occlusion a doorway would go dead black. `DoorSkyLeak` (default 0.15) keeps a sliver of sky
  at the threshold. The door test mirrors vanilla's own so the two can never disagree about which cell is
  a doorway.
- **Only ever raises the baked alpha.** Other mods legitimately write it: Dub's Skylights nulls
  `map.roofGrid` across `Regenerate` so skylit cells never take vanilla's roofed branch, and Biomes!
  Caverns transpiles the roofed test so cavern roofs read as open. Taking `max` means we can add occlusion
  without undoing anyone's decision to let light *in* — worst case we leave their value alone. The patch
  also takes `Priority.First` so it runs before Dub's Skylights' Postfix restores the roofs it removed,
  and therefore sees skylit cells as unroofed. (Biomes! Caverns' intent is the opposite of ours by design;
  with both installed, our toggle is the one to turn off.)
- **Two floors reach interiors through here, and only through here.** `Patch_BrightnessFloor` lifts
  `CurSkyGlow`, which cannot brighten a sealed cave by one shade — roofed cells take no sky glow at all.
  So a floor is applied as a *cap* on occlusion (`1 - floor`), leaving exactly that fraction of sky
  bleeding in. Two knobs feed it and the higher wins (`IndoorOcclusionMath.EffectiveIndoorFloor`):
  the map-wide accessibility floor, which is what makes its hotkey work indoors as well as out, and
  **minimum indoor brightness**, a dedicated slider for players who want readable interiors *without*
  also brightening the outdoors. The dedicated one ships at 0 (interiors may go fully black — the point
  of the feature); at 1 it cancels occlusion outright, which is exactly equivalent to switching the
  feature off, a property of the formula rather than a special case.
- **Baked, not per-frame.** Unlike §7a's material colour, these alphas only change when a section is
  dirtied, so `IndoorOcclusionRedraw` forces a `WholeMapChanged(GroundGlow)` when the toggle or either
  slider changes (it compares the *resolved* floor, so either knob moving is caught without duplicating
  the max() rule) — otherwise the setting appears to do nothing until something else dirties the map.
- Skipped entirely on `disableSkyLighting` biomes (the Odyssey undercave), where vanilla already zeroes
  the sky contribution wholesale; there is nothing to occlude and overriding it would fight that contract.

**Why the alpha polarity is safe to rely on** (higher == more occluded == less sky), from decompiled
vanilla plus one third-party mod: an unroofed cell keeps the glow grid's own alpha (0 in the common case)
while a roofed one is forced *up* to 100; `disableSkyLighting` biomes kill the sky wholesale with
`MatBases.LightOverlay.color = (1,1,1,0)`, which is exactly why those maps are black away from lamps; and
Dub's Skylights makes a roofed cell sky-lit by removing its roof, never by touching this alpha directly.
`ApiCompatibilityTests` pins `RoofedAreaMinSkyCover == 100` and `Section.Size == 17`, so a Ludeon retune
of either surfaces as a test failure rather than a silent look regression.

**Conflict risk.** Shares `SectionLayer_LightingOverlay.Regenerate` with Dub's Skylights (bracketing
Prefix/Postfix) and Biomes! Caverns (Transpiler) — both listed in `About.xml`'s `loadAfter`. We are a
Postfix that only raises alphas, so we compose with a transpiled body rather than replacing it.
Rendering-only; no gameplay math, and gameplay light (`GroundGlowAt`) is untouched.

Gated by `CelestialLightingFeatures.IndoorSkyOcclusion`, default on, separate from `PitchBlackNights`
because it changes daytime interiors too (an unlit shed at noon goes black), which is a much larger taste
call than night darkness.

## 8. Sky colour-temperature curve (`Patch_SkyColorTemperature`)

Subsystem 2 warms the sky toward a single fixed hue inside one twilight band. This generalizes that
into a continuous **colour-temperature curve keyed on sun altitude**: the sky shifts from a warm
low-colour-temperature glow near the horizon (~2000 K) up to a neutral daylight white near the
zenith (~5772 K, the Sun's actual effective temperature), passing through the familiar golden-hour
warmth on the way. This is the physically-grounded version of "dramatic seasonal twilight" — because
day length and peak sun altitude already vary with latitude and season (vanilla `GenCelestial` + our
own simulator), a high-latitude winter day that never lifts the sun far above the horizon *stays*
warm all day, for free — the tint is a function of altitude alone.

A Harmony Postfix on `WeatherWorker.CurSkyTarget` nudges (never replaces) `__result.colors.sky` and
`.overlay` toward a blackbody colour derived from the current sun elevation. All the math is pure and
offline-tested in `Source/SkyColorTemperature.cs` (no `UnityEngine`/`Verse` deps, linked into the
test project like `Formulas.cs`):

- `ColorTemperatureKelvin(elevation)` — a monotonic linear ramp from `HorizonKelvin` (2000 K, at/below
  the horizon) to `ZenithKelvin` (5772 K, at/above `DaylightAltitudeDegrees` = 60°).
- `BlackbodyToRgb(kelvin)` — the widely published, public-domain Tanner Helland approximation of the
  Planckian locus (a standard tabulated/curve-fit conversion, textbook not mod-specific — see
  "Clean-room provenance"). Split into three small per-channel functions so the piecewise structure
  reads top-to-bottom.
- `TintStrength(elevation)` — the geometric blend factor in `[0, 1]`: the product of a low-sun ramp
  (1 at the horizon → 0 by 60°, so high sun gets no tint) and a civil-twilight gate (fading out
  between the refraction-adjusted horizon and −6°, so night — subsystem 7's domain — isn't tinted
  warm). The adapter multiplies this by per-channel blend strengths (sky 0.35 / overlay 0.25),
  mirroring `Patch_TwilightColor`.

The adapter re-derives sun elevation from `SolarPosition.ElevationForMap(map)` (our own simulator),
not from `__result.glow` — for the same reason as §2: the tint should track true sun position rather
than displayed brightness, which §7 rewrites below the horizon. (The original reason given here,
that glow "may already be weather-clamped", was overstated — see §13.) It touches neither
`.saturation` (that's §2's job) nor `.glow`.

Composition with §2: both Postfixes run on the same call and both warm the sky at low sun. That's
intentional — §2's dusk/dawn nudge is one concentrated anchor point (a narrow band around
`sunGlow ≈ 0.35`) and this adds the broader altitude-driven tint around it. Critically it stays in
the same low-risk lane as §2 — **colour only, never `.glow`** — so it does not disturb the brightness
other mods read (see "Conflict risk"). Every vanilla member it depends on
(`WeatherWorker.CurSkyTarget`, `SkyColorSet.sky`/`.overlay`, and the `SolarPosition` inputs) is
already covered by `ApiCompatibilityTests` for §2 and §1, so no new API assertions were needed.

## 9. Low-light desaturation / Purkinje shift (`Patch_LowLightDesaturation`)

As scene brightness falls, human vision loses colour discrimination and everything drifts toward a
dim blue-grey (the Purkinje shift — rod vision taking over from cones). This subsystem reproduces
that: as the sky glow drops toward night, blend the sky colour toward a desaturated cool grey, most
strongly on the darkest (new-moon, overcast) nights. It's cheap, distinctly atmospheric, and makes
our darkness read as *night* rather than as a uniformly dimmed day.

It composes directly with subsystem 7 — §7 sets *how much* light the night sky provides, §9 sets
*how that light reads* as colour drains out of it — and, like §8, it is a colour-only blend on
`CurSkyTarget`, so it stacks cleanly with §2/§8 and stays clear of the glow value.

A second Harmony Postfix on `WeatherWorker.CurSkyTarget` (alongside §2's) that:

- Reads `__result.glow` — the sky target's *own* brightness — rather than recomputing
  `GenCelestial.CurCelestialSunGlow` the way §2 does. This is the deliberate opposite choice: §2
  wants twilight *timing* anchored to true sun position, but §9 wants actual *displayed* brightness.
  It then attenuates that by §13's dimming (`WeatherDimmingMath.ApparentGlow`) to get the brightness
  the eye actually receives. **That second step is a correction, not a refinement:** this bullet used
  to claim an overcast night desaturated more than a clear one "for free" because glow arrived
  weather-clamped. It did not — `maxGlow` is set exactly once in all of vanilla and is inert at
  night (§13) — so until §13 landed, a blizzard and a clear sky desaturated identically. Note §13
  supplies the weather term through a shared adapter read rather than by writing `.glow`, so the
  gameplay brightness driving plant growth and solar output stays vanilla. This is
  also the seam to subsystem 7: since §7 raises/lowers `__result.glow` by moon phase and the
  star/airglow floors, a full-moon night lands lower on the desaturation ramp than a new-moon one
  automatically.
- Feeds that glow to `PurkinjeMath.PurkinjeFactor` — the pure "how far into rod vision are we" ramp:
  `0` at/above `OnsetGlow` (0.30, below vanilla's 0.6 dusk and §2's 0.35 peak, so golden hour stays
  warm), `1` at/below `FullGlow` (0.05, a small nonzero floor so the shift completes while it's
  still bright enough to see). The falloff and the resulting `SaturationMultiplier` live in
  `Source/PurkinjeMath.cs` (its own System-only pure file, not `Formulas.cs`, to avoid colliding
  with the other in-flight subsystems editing `Formulas.cs`) with offline `[TestCase]` coverage of
  both plateaus, monotonicity, the midpoint, and the multiplier endpoints.
- Applies the factor as a colour-only nudge: multiplies `SkyColorSet.saturation` down toward a
  rod-vision floor (60% of colour removed at full shift) and `Color.Lerp`s the `sky`/`overlay` tints
  toward a desaturated cool blue-grey (the perceptual Purkinje blue). Lerping (never overwriting)
  preserves each `WeatherDef`'s palette. `__result.glow` is never touched.

## 10. Eclipse: natural and unnatural

RimWorld's `Eclipse` `GameCondition` fires on a random timer, lasts far longer than a real solar
eclipse (roughly a game-day), and darkens the map with a flat on/off dim. That length is physically
impossible — a real total eclipse's totality is minutes and the whole partial-to-partial span is a
couple of hours. Rather than paper over that, we split eclipses into two deliberately distinct
concepts, keyed on whether the eclipse's *timing and duration are astronomically real*:

Both share one piece of math: a **coverage ramp** from disk-overlap (standard circle-intersection)
geometry that drives a gradual partial → near-total darkening and the characteristic wan eclipse
colour, replacing the vanilla flat dim. Only *what moves the discs together* and *how long they stay*
differ.

### Eclipse mode (natural / unnatural / both)

Natural (§10a) and unnatural (§10b) are independent — natural fires real geometric *events*,
unnatural only reshapes the darkening of the storyteller's own eclipse — so all three combinations
are exposed as a radio (`EclipseMode`, pure rules in `EclipseModeRules`, tested):

- **Natural + unnatural — the shipped default.** Geometric eclipses fire at real astronomical times
  (natural ramp) *and* the storyteller's random eclipses still occur (unnatural ramp); each active
  eclipse is darkened by whichever kind it is (`EclipseIntegration.RendersNatural` tags the ones we
  fired). This does mean natural eclipses — which touch gameplay — are on by default, a deliberate
  choice; the two other modes let a player opt out either direction.
- **Natural only.** Only geometric eclipses; the random storyteller eclipse is suppressed
  (`Patch_SuppressRandomEclipse`) so they don't double-fire.
- **Unnatural eclipse only.** The original visual-only behaviour: no extra events, just the §10b
  reshape of the storyteller's eclipse.

The "Eclipse effects" checkbox is the master above the radio — off means the mod leaves eclipses
entirely alone (vanilla flat dim, vanilla timing, no trigger, no suppression).

### 10a. Natural eclipse

Driven by the modeled moon's real position (§6): when the moon geometrically transits the sun, fire
an eclipse that lasts the **correct, short real-eclipse duration** — the event triggers *during an
actual eclipse* and ends when the discs part. Astronomically accurate in both when it happens and
how long it lasts.

Because it changes *when* (and, by shortening the duration, *how long*) a gameplay event occurs
(solar-power loss, mood), it is the flavour gated by the eclipse-mode radio above (active in
Natural-only and the default Both). Design consequences:

- It requires the moon's **orbital inclination and nodes** (see §6 scope note). With the flat
  Moon-on-the-ecliptic approximation the moon would transit every new moon and eclipses would fire
  ~monthly; the tilt + nodal geometry is what makes them appropriately rare and correctly timed. This
  feature owns that extra modeling — shadows/moonlight don't pay for it.
- It drives vanilla's *existing* `Eclipse` condition (with a corrected short duration) rather than a
  new one, so all downstream mods that react to eclipses keep working. In **Natural-only** the random
  vanilla eclipse incident is suppressed so the two don't double-fire; in the default **Both** the
  random ones are deliberately kept and rendered as *unnatural* (§10b) alongside the geometric ones.

**Implementation (now wired, against the merged §6 moon).** The inclination/node geometry lives in the
pure `MoonMath` core (`LunarInclinationDegrees`, a slowly-regressing nodal cycle, `MoonEclipticLatitude`
and `SunMoonSeparationDegrees`): the moon only crosses the sun when a new moon coincides with a node,
where its ecliptic latitude passes through zero. The nodal period is tuned (not astronomically scaled)
so eclipses land **rare but recurring — one every few game years** (rather than reality's per-location
rarity, which a colony might never witness); an offline simulation test (`MoonMathTests`) pins that
cadence so a formula change can't silently make eclipses monthly or never. `EclipseIntegration` is the
thin Verse adapter turning the live moon into a `MoonSunGeometry` (separation, disc radii from
`EclipseMath`, and the impact parameter); `GameComponent_NaturalEclipse` is the orchestrator that, when
the transit becomes active, fires a real short `Eclipse` (duration from the moon's relative angular
speed via `EclipseMath.NaturalEclipseDurationTicks`) on each map and publishes the transit **magnitude**
so a grazing pass reads as a *partial* (the darkening peaks below full night) and a bullseye as a
*total*. `Patch_SuppressRandomEclipse` vetoes the random `Eclipse` incident while the mode is on. A
richer *graphical* partial (a visible moon disc occulting part of the sun) is a tracked follow-up;
today "partial" is expressed as reduced darkening depth. Because a real eclipse is years away, the
dev-only `EclipseStaging` (pure, tested) phase-slides the modeled moon onto a genuine new-moon-at-node
alignment on demand so the live trigger can be filmed/validated — the shipped mod never shifts the
moon.

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

## 11. Aurora and solar-flare sky tinting (`Patch_AuroraTint`)

Same principle as §10: shift the night sky toward auroral greens/reds while a solar flare is active.
**Visual only — the flare's electronics disruption and every other gameplay effect are left entirely
untouched, and this blends only `SkyTarget.colors`, never `SkyTarget.glow`, so it stays in the same
low-risk colour-only lane as §2/§8 (see "Conflict risk"): the brightness value other mods read is
undisturbed.** Auroral emission colours (atomic-oxygen green ~557.7 nm, red ~630 nm) are physical
constants, not mod-specific.

**Why only the solar flare, not the vanilla `Aurora` condition.** Decompiling vanilla 1.6 showed
`GameCondition_Aurora` *already* renders its own shifting auroral sky colours via a
`GameCondition.SkyTarget(Map)` override that `SkyManager` applies on top of
`WeatherWorker.CurSkyTarget`. Tinting it again here would double up and fight vanilla's own render.
The `SolarFlare` condition (`GameCondition_DisableElectricity`) has *no* sky visual at all — yet a
real solar flare is exactly what drives auroras — so tinting the night sky during a flare adds the
missing visual without conflicting with anything vanilla does. `AuroraConditions` is the thin adapter
that resolves the active driver; any future aurora-style condition lacking its own sky render can be
added to its driver set. (`SolarFlare` is a core `GameConditionDef` but, unlike `Eclipse`/`Aurora`,
is not exposed on `GameConditionDefOf`, so it's resolved by defName via `DefDatabase.GetNamedSilentFail`.)

**Approach.** A Harmony Postfix on `WeatherWorker.CurSkyTarget` — the same injection point as
`Patch_TwilightColor` (§2). The two blend different, non-overlapping things (twilight warms the sky
at dusk-glow ~0.35; this tints it green only at deep night and only during a flare), so they stack
cleanly regardless of postfix order. The pure core (`Source/AuroraMath.cs`, offline-tested) supplies:

- a **night-visibility ramp** (`NightVisibility`) that fades the tint to zero as the sky brightens,
  reusing vanilla's own `GameCondition_Aurora.MaxSunGlow` (0.5) as the upper cutoff so our
  flare-driven tint disappears at the same brightness vanilla's aurora does — auroras are invisible
  in daylight, so a daytime flare produces no sky colour;
- a **condition fade** (`ConditionRampFactor`) easing the tint in over the flare's first ~hour and
  out over its last ~hour (combined with `Min`, so a very short flare simply peaks lower than full
  rather than ever snapping in);
- a slow **green↔red shimmer** (`ShimmerRedMix` / `AuroralColorAtPhase`) advanced by game ticks,
  capped at `MaxRedMix` so the aurora stays green-dominant (as real ones mostly are), warming only
  partway toward the high-altitude red line at each cycle's peak.

The blend strengths (`MaxSkyTintStrength`, `MaxOverlayTintStrength`) are deliberately moderate: we
ship no shimmering overlay *texture* the way vanilla's aurora does, only a colour tint, so we need a
touch more colour than vanilla's ~0.075 to read as an aurora without turning the sky flat neon.
`AuroraConditions.CurrentSkyTintStrength` is shared by the patch and the `aurora_tint` live probe so
they can never derive a different value from each other — the same discipline `SolarPosition.cs`
enforces between the shadow patches.

Deferred: a matching moonlight/HUD hook and per-condition settings sliders (the tint constants are
already isolated in `AuroraMath` for that). Lowest priority of the planned set.

## 12. Blood moon rendering (`Patch_BloodMoon`) — soft-compat with a third-party event

A "blood moon" is a *lunar* eclipse — the moon passing into the planet's shadow (umbra) and turning
crimson — as opposed to the *solar* eclipse of §10. There is no vanilla blood moon; the well-known
one is **Vanilla Races Expanded – Sanguophage**'s `VRE_BloodMoonCondition` (packageId
`vanillaracesexpanded.sanguophage`), a night-time `GameCondition` whose in-game text is lore-
consistent with ours ("*one of the moons of this planet has orbited into the rimworld's umbra…*").

Since we're the mod that actually models moonlight colour, we make sure a blood moon *looks* right
under our lighting instead of rendering as an ordinary silver-blue moonlit night. When that condition
is active, `Patch_BloodMoon` (a Postfix on `WeatherWorker.CurSkyTarget`, the same low-risk seam §2's
`Patch_TwilightColor` uses) shifts the sky's `SkyColorSet` colours toward deep coppery crimson so the
whole night reads red — bright enough to still be a *moonlit* night (a blood moon is a full moon),
not darkness.

The recolour math lives in the pure `Source/BloodMoonMath.cs` (offline `[TestCase]` coverage, no
`Verse`/`UnityEngine`): `NightFactor(sunGlow)` ramps the effect out through dusk so it never paints
the daytime sky, and `CrimsonTint(r,g,b,strength)` blends each colour toward a crimson hue that is
first rescaled to the *input's own* luma — so the shift is "this exact colour, but red" and preserves
brightness (a black input stays black; a dim night colour keeps its luma exactly). `Source/BloodMoon.cs`
is the thin adapter: it resolves `VRE_BloodMoonCondition` by def lookup once (cached) and reads
`GameConditionManager.ConditionIsActive` + `GenCelestial.CurCelestialSunGlow` off the live map, so
`Patch_BloodMoon` and the `blood_moon` live probe re-derive the identical tint strength.

Boundaries:

- **Soft dependency, not a requirement.** The condition is detected by def lookup
  (`DefDatabase<GameConditionDef>.GetNamedSilentFail`), never a hard assembly reference — the effect
  is simply inert when VRE – Sanguophage isn't installed. `vanillaracesexpanded.sanguophage` is in
  `About.xml`'s `loadAfter` so our render reads its state after it starts.
- **Visual only.** We recolour the night — colours only, never `SkyColorSet.glow`, so the brightness
  value other mods read (Dub's Skylights' `CurSkyGlow`) is untouched; we touch none of VRE's
  sanguophage/hemogen mechanics.
- We *react to* the third-party condition; we never trigger it (contrast §10's opt-in solar
  trigger). If both this and §10's astronomical mode ever coexist, a blood moon should line up with
  a full moon — but that coupling is out of scope for a first pass; reacting to the live condition is
  enough to "look how we'd expect."

**Integration seam (pending #6/#7).** Until the moon-position (§6) and night-radiance (§7)
subsystems merge, there is no authoritative "moonlight colour" to recolour, so `Patch_BloodMoon`
tints the vanilla night sky in place. The self-contained detection + pure crimson recolour are the
seam that plugs into them: once §7 owns the moonlit-sky colour, the tint should apply to *that* (so a
blood-moon night is a genuinely bright red night) and additionally gate on the modeled moon being
above the horizon. Marked with a `TODO(integration)` in `BloodMoon.TintStrengthForMap`.

## 13. Weather dimming (`Patch_WeatherDimming`)

**Problem.** Vanilla weather does not darken the sky. This is easy to disbelieve, so it is worth
stating precisely: `WeatherWorker.CurSkyTarget` computes

```csharp
result.glow = Math.Min(GenCelestial.CurCelestialSunGlow(map), def.maxGlow);
```

and `WeatherDef.maxGlow` defaults to `1.0` and is set **exactly once** across all vanilla XML —
Odyssey's `Overcast`, at `0.95`. Rain, Fog, FoggyRain, SnowHard, SnowGentle, both thunderstorms,
Sandstorm, BlindFog, Blizzard and TorrentialRain all leave it at the default. Weather changes the
sky's *colour* (clear ships `skyColorsDay.sky = (1,1,1)`, the overcast/wet family ships
`(0.8,0.8,0.8)`) but never its *brightness*. Shadows are worse off still: `GenCelestial.CurShadowStrength`
is `Clamp01(Abs(CurCelestialSunGlow - 0.6) / 0.15)`, and `CurCelestialSunGlow` is a pure function of
latitude, longitude and tick with no weather term at all — so vanilla renders a blizzard's shadows
exactly as crisp as a clear noon's.

This also means §9's original design promise was never met. It was written believing `__result.glow`
arrived "already clamped by the active `WeatherDef`'s `maxGlow`", so an overcast night would
desaturate more than a clear one for free. It never did: at night celestial glow is ~0 under every
weather alike, and even Overcast's `0.95` can only bite in full daylight, which is above §9's onset
threshold anyway. A blizzard and a clear sky desaturated identically.

**Approach.** A colour-only Postfix on `WeatherWorker.CurSkyTarget` that scales `colors.sky` and
`colors.overlay`, a multiply on `GenCelestial.CurShadowStrength`, and an apparent-brightness term
feeding §9 — all driven by one shared classifier.

**Which channel, and why it is the whole design.** `SkyTarget` carries two independent outputs and
`SkyManagerUpdate` consumes them separately:

| channel | consumer | nature |
|---|---|---|
| `.glow` | `curSkyGlowInt` → `GlowGrid.GroundGlowAt` | **gameplay** — `PlantProperties.growMinGlow` (0.51), `CompPowerPlantSolar`, pawn psych-glow |
| `.colors.sky` / `.overlay` / `.saturation` | `MatBases.LightOverlay.color`, `MatBases.FogOfWar`, `Find.CameraColor.saturation` | **pure render** |

We write the colour channel and never touch `.glow`. That is not a workaround — it is the channel
vanilla already uses for weather, and we are deepening an existing 20% step into a continuous,
intensity-scaled ramp. It keeps CLAUDE.md's *"scope is visual/atmospheric only"* intact with no
asterisk: under every weather at every strength, plant growth, solar output and pawn vision are
bit-for-bit vanilla. Had we dimmed `.glow` instead, a 25% dim would have cost ~25% of solar output
and measurably shortened the outdoor growing window — a gameplay change smuggled in under a
lighting mod.

It also buys ordering freedom. Because we only touch `.colors`, this patch is order-independent
against every other postfix on `CurSkyTarget` and needs no `HarmonyPriority`. §7 writes `.glow` and
never reads `.colors`; §2/§8/§11/§12 blend `.colors` but recompute their own brightness from the
solar simulator. §9 is the one consumer, and it reads `WeatherDimming.DimmingFor(map)` itself rather
than observing a value we left behind — a shared adapter read instead of an ordering dependency.
That distinction matters, because `[HarmonyAfter]` takes *owner IDs* and every patch here shares the
single `"celestiallighting"` owner, so it could never have expressed an intra-assembly order anyway.

**The classifier.** Cloud opacity is the **product** of a luminance deficit and a saturation
deficit, each measured against the clear-family palette:

```
lumDeficit   = InverseLerpClamped(1.00, 0.80, Rec709Luminance(skyColorsDay.sky))
satDeficit   = InverseLerpClamped(1.25, 0.90, skyColorsDay.saturation)
cloudOpacity = lumDeficit * satDeficit
```

The product is simultaneously the classifier and the guard, which is what lets §13 ship with no roof
check, no biome check and no defName list:

| family | `skyColorsDay.sky` | `saturation` | lumDef | satDef | opacity |
|---|---|---|---|---|---|
| Clear, Windy, Orbit | (1,1,1) | 1.25 | 0 | 0 | **0** |
| Underground, Undercave | (0.3,0.4,0.4) | 1.25 | 1 | **0** | **0** |
| MetalHell | (0.4,0.5,0.5) | 1.25 | 1 | **0** | **0** |
| UnnaturalDarkness | (0.482,0.603,0.682) | 1.25 | 1 | **0** | **0** |
| Fog, Rain, Blizzard, Sandstorm, … | (0.8,0.8,0.8) | 0.9 | 1 | 1 | **1** |
| GrayPall / UnnaturalFog | (0.482,0.603,0.682) | 0.75 / 0.5 | 1 | 1 | **1** |

Every dark-palette *non-weather* keeps the clear family's saturation of 1.25, so its saturation
deficit is exactly 0 and the product zeroes it structurally. A luminance-only rule would have dimmed
caves and the metal hell into blackness and needed an explicit guard bolted on; Orbit would have been
spared only by the luck of shipping Clear's palette. A modded weather is classified by the same data
it already declares for rendering, with no registration step.

Precipitation then scales the result across a band — `Lerp(0.6 * maxDimming, maxDimming, …)` keyed on
`max(rainRate, snowRate, sandRate) / 1.6` (Sandstorm being vanilla's heaviest) — so a dry deck dims
18%, rain 25.5%, hard snow 27%, a blizzard 29.3% and a sandstorm the full 30%. Transitions blend the
two defs' opacities by `WeatherManager.TransitionLerpFactor`, mirroring how vanilla lerps `RainRate`.

**Shadows get their own seam** (`Patch_ShadowStrength`), because shadow alpha derives from
`CurCelestialSunGlow` and never from `SkyManager.CurSkyGlow` or `SkyTarget.colors` — nothing we do to
the sky would ever have reached it. Under a full deck only 15% of the contrast survives. Unlike
§5's penumbra factor, which models the sun's angular disk and so applies only while the sun is up,
this scales the moon's shadow too: clouds hide the moon exactly as they hide the sun.

**Reading the weather off `map.weatherManager`, not `WeatherWorker.def`.** The latter is private (a
fact pinned by `ApiCompatibilityTests.WeatherWorker_DefFieldIsNotPublic`), so it would need
`FieldRefAccess` — and it would buy nothing, because reading the manager is *exactly* equivalent
rather than merely close. `SkyManager.CurrentSkyTarget` calls `CurSkyTarget` on both the current and
last weather's worker and lerps the two by the same factor, so a uniform map-level multiply factors
straight back out: `Lerp(a·k, b·k, t) == k · Lerp(a, b, t)`.

**Decisions worth flagging as decisions, not oversights:**

- `TorrentialRain` is data-identical to `Rain` (palette B, `rainRate` 1) — their difference lives in
  `WeatherWorker_TorrentialRain`, not in data — so both dim 25.5%. A defName override would defeat
  the data-driven premise.
- Anomaly's `UnnaturalDarkness` is deliberately **not** dimmed. Its darkness is owned by
  `GameCondition_UnnaturalDarkness`'s `LerpDarken`, and stacking a second multiply on a
  gameplay-critical horror event would be wrong.
- A modded weather that copies Clear but sets `saturation: 1.0` picks up a partial deficit (~0.71)
  and so a mild unrequested dim. Bounded, and the alternative (a hard threshold) is more brittle.

**Conflict risk.** Low, and lower than every other glow-touching subsystem here: we write only
`SkyColorSet` fields plus a shadow-alpha multiply, so a mod that reads `SkyManager.CurSkyGlow`
(Dub's Skylights, solar output, plant growth) sees nothing different under any weather. The one
sharp edge is internal: `colors.sky` is assigned straight to `MatBases.LightOverlay.color`, whose
**alpha** is how much of the lighting overlay is drawn at all — vanilla writes `(1,1,1,0)` to switch
it off for `disableSkyLighting` biomes. So the scale is RGB-only; a naive `color * factor` (Unity
scales all four channels) would fade the darkening overlay *out* and make heavy weather render
brighter, the exact opposite of the intent.

## Settings, presets, and the brightness floor (planned)

Two cross-cutting settings ideas that span the subsystems above:

- **Opinionated presets.** Ship a small number of named presets (e.g. "Realistic" vs
  "Cinematic/Pretty") that set the correlated knobs together — shadow length/strength (§1/§3), night
  radiance floors (§7), desaturation strength (§9) — so most players pick one preset and never open
  a slider. Individual sliders remain for anyone who wants them.
- **Minimum-brightness accessibility floor.** A user-set floor on displayed night brightness,
  toggleable in Mod Settings and — if the player binds it — by an optional hotkey. This is the
  deliberate complement to true pitch-black nights (§7): pitch-black for atmosphere by default, a
  legible floor when a player actually needs to see to play. Because it clamps the *displayed* glow
  upward, it must be applied as the last step, after §7's floors and any weather dimming.

  The keybinding ships with **no default key**. It was `Semicolon` on the assumption that vanilla left
  it free; vanilla binds it to `Dev_ToggleGodMode`, so every install logged a startup "Key binding
  conflict" and one of the two lost. Since almost every free key risks colliding with some other mod
  instead, the def ships unbound and is assignable in Options -> Keyboard Configuration. The checkbox
  and slider in Mod Settings mean this costs discoverability, not reachability.

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
