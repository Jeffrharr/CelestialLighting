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
substantial or copyrightable expression) for `DeclinationSign`. No code, assets, or shaders from
Sjaandi's mod were ever available to reference. The shadow-tilt subsystem deliberately avoids
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
